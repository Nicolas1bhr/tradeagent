using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Proving;

/// <summary>What a configuration is: the deterministic floor, or a chart of seats.</summary>
public enum ProvingConfigurationKind
{
    /// <summary>A closed grammar of programs searched in a fixed order, with no model (<see cref="FloorGrammar"/>).</summary>
    Floor,

    /// <summary>Positions on a chart, each played by its seat — a puppet script per position in the test host.</summary>
    Seats
}

/// <summary>
/// One position of a configuration's chart template, and the turns its puppet plays, one array of steps per turn. A founding position
/// is named by its own id; a position in a chartered unit by a template id the app replaces with the id it mints at the hire, and
/// <paramref name="Head"/> appoints it its unit's head.
/// </summary>
public sealed record ProvingPosition(string Id, string Unit, IReadOnlyList<IReadOnlyList<string>> Script, bool Head = false);

/// <summary>
/// A UNIT THE TEMPLATE CHARTERS (<c>U-proving-ground-d</c> item 1): a template id the app replaces with the unit id it mints, its kind, the
/// founding unit it is chartered under, and its AI share of that unit's envelope — chartered through <see cref="OrgStore.Charter"/> by the
/// parent's head as a funded, bounded experiment, never seeded.
/// </summary>
/// <para><paramref name="VerdictShare"/>: the team's fraction of each campaign's own verdict budget — what lets a position below the division's head
/// spend a verdict at all (the referee's rule) — or null for none.</para>
public sealed record ProvingCharter(string Id, string Kind, string Parent, decimal AiShare, decimal? VerdictShare = null);

/// <summary>
/// A CONFIGURATION, AS DATA (<c>U-proving-ground-c</c> item 2; <c>docs/ORCHESTRATION.md</c> § 2.14: "a configuration — chart
/// template, seats and policies — works a brief under a budget and is scored in hours"). Read from one JSON file by
/// <see cref="Parse"/>, refused — never guessed — when it is not exactly this build's version and shape.
///
/// <para><b>The budgets are envelopes the kernel enforces, not settings read back</b> (BUDGET-001): the trial and verdict
/// budgets become each market's campaign, which the referee and the backtest runner refuse past; the AI envelope becomes the
/// root unit's envelope, which admission refuses past. A run of several configurations is refused unless every budget and
/// the deadline are equal (<see cref="UnequalBudgets"/>).</para>
/// </summary>
/// <param name="Name">A short name, which names its home's folder: letters, digits and '-' only.</param>
/// <param name="Kind">The floor or a chart of seats.</param>
/// <param name="Units">The founding units the chart template names, by id.</param>
/// <param name="Charters">The units the template charters, in order, each under a founding unit (<c>U-proving-ground-d</c>).</param>
/// <param name="Positions">The chart template's positions, each with its puppet script. The brief — a wake per scripted turn — goes to
/// the first; every other plays its script when the kernel's own records wake it.</param>
/// <param name="AiEnvelope">The root unit's AI envelope, in the price list's currency.</param>
/// <param name="TrialBudget">Trials per market's campaign.</param>
/// <param name="VerdictBudget">Verdicts per market's campaign.</param>
/// <param name="DeadlineSeconds">How long the configuration works, from its start, before it is stopped.</param>
public sealed record ProvingConfiguration(
    string Name,
    ProvingConfigurationKind Kind,
    IReadOnlyList<string> Units,
    IReadOnlyList<ProvingCharter> Charters,
    IReadOnlyList<ProvingPosition> Positions,
    decimal AiEnvelope,
    int TrialBudget,
    int VerdictBudget,
    int DeadlineSeconds)
{
    /// <summary>The one format version this build reads and writes.</summary>
    public const int Version = 2;

    /// <summary>The longest a configuration may work: a proving run is scored in hours, never days.</summary>
    public const int MaxDeadlineSeconds = 6 * 3600;

    /// <summary>
    /// THE FOUNDING CHART'S UNITS AND POSITIONS, which every home holds. Anything more — a critic, a headed team — the template charters
    /// and hires through the kernel's own writers (<see cref="Charters"/>), under the founding unit the research position heads.
    /// </summary>
    public static readonly IReadOnlyList<string> FoundingUnits = [OrgIds.Root, OrgIds.Operations, OrgIds.Research];

    /// <summary>The founding chart's positions and the unit each sits in.</summary>
    public static readonly IReadOnlyDictionary<string, string> FoundingPositions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CouncilRoles.Operations] = OrgIds.Operations,
        [CouncilRoles.Research] = OrgIds.Research
    };

    /// <summary>
    /// THE CONFIGURATION IN <paramref name="json"/>, or the refusal naming what is wrong — the version first, so a file of
    /// another version is refused for that and nothing else.
    /// </summary>
    public static (ProvingConfiguration? Configuration, string Why) Parse(string json, string from = "the configuration")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (null, $"{from} is not JSON: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, $"{from} is not a JSON object.");
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                return (null, $"{from} names no format version; this build reads version {Version} only, and guesses at none.");
            if (version != Version)
                return (null, $"{from} is format version {version}, and this build reads version {Version} only. Write it again for this build; nothing was read from it.");

            string[] known = ["version", "name", "kind", "chart", "ai_envelope", "trial_budget", "verdict_budget", "deadline_seconds"];
            foreach (var p in root.EnumerateObject())
                if (!known.Contains(p.Name, StringComparer.Ordinal))
                    return (null, $"{from} holds '{p.Name}', which is not a field of a version-{Version} configuration ({string.Join(", ", known)}).");

            if (Text(root, "name") is not { Length: > 0 and <= 40 } name || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return (null, $"{from}'s name is missing or not 1 to 40 letters, digits and '-'.");
            var kind = Text(root, "kind") switch
            {
                "floor" => ProvingConfigurationKind.Floor,
                "seats" => ProvingConfigurationKind.Seats,
                var other => (ProvingConfigurationKind?)null
            };
            if (kind is null) return (null, $"{from}'s kind is '{Text(root, "kind")}', and a configuration is 'floor' or 'seats'.");

            if (!root.TryGetProperty("ai_envelope", out var env) || env.ValueKind != JsonValueKind.Number || !env.TryGetDecimal(out var envelope) || envelope <= 0m)
                return (null, $"{from}'s ai_envelope is missing or not a number above 0.");
            if (Whole(root, "trial_budget") is not { } trials || trials < 1 || trials > 1000)
                return (null, $"{from}'s trial_budget is missing or not a whole number from 1 to 1000.");
            if (Whole(root, "verdict_budget") is not { } verdicts || verdicts < 1 || verdicts > 100)
                return (null, $"{from}'s verdict_budget is missing or not a whole number from 1 to 100.");
            if (Whole(root, "deadline_seconds") is not { } deadline || deadline < 1 || deadline > MaxDeadlineSeconds)
                return (null, $"{from}'s deadline_seconds is missing or not a whole number from 1 to {MaxDeadlineSeconds}.");

            if (!root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object)
                return (null, $"{from} has no chart template (an object with 'units' and 'positions').");
            foreach (var p in chart.EnumerateObject())
                if (p.Name is not ("units" or "positions" or "charters"))
                    return (null, $"{from}'s chart holds '{p.Name}', and a chart template holds 'units', 'charters' and 'positions' only.");
            if (!chart.TryGetProperty("units", out var unitsJson) || unitsJson.ValueKind != JsonValueKind.Array)
                return (null, $"{from}'s chart names no units.");
            var units = new List<string>();
            foreach (var u in unitsJson.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.String || u.GetString() is not { Length: > 0 } unit) return (null, $"{from}'s chart names a unit that is not text.");
                if (units.Contains(unit, StringComparer.Ordinal)) return (null, $"{from}'s chart names unit '{unit}' twice.");
                units.Add(unit);
            }
            var charters = new List<ProvingCharter>();
            if (chart.TryGetProperty("charters", out var chartersJson))
            {
                if (chartersJson.ValueKind != JsonValueKind.Array) return (null, $"{from}'s charters are not a list.");
                foreach (var c in chartersJson.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) return (null, $"{from}'s chart holds a charter that is not an object.");
                    foreach (var f in c.EnumerateObject())
                        if (f.Name is not ("id" or "kind" or "parent" or "ai_share" or "verdict_share"))
                            return (null, $"{from}'s charter holds '{f.Name}', and a charter holds 'id', 'kind', 'parent', 'ai_share' and 'verdict_share' only.");
                    if (Text(c, "id") is not { Length: > 0 } cid || Text(c, "kind") is not { Length: > 0 } ckind || Text(c, "parent") is not { Length: > 0 } cparent)
                        return (null, $"{from}'s chart holds a charter without an id, a kind or a parent.");
                    if (!c.TryGetProperty("ai_share", out var share) || share.ValueKind != JsonValueKind.Number || !share.TryGetDecimal(out var aiShare))
                        return (null, $"{from}'s charter '{cid}' names no ai_share: the fraction of its parent's envelope it is funded with.");
                    decimal? verdictShare = null;
                    if (c.TryGetProperty("verdict_share", out var vs))
                    {
                        if (vs.ValueKind != JsonValueKind.Number || !vs.TryGetDecimal(out var vShare) || vShare <= 0m || vShare > 1m)
                            return (null, $"{from}'s charter '{cid}' names a verdict_share that is not a fraction above 0 and at most 1.");
                        verdictShare = vShare;
                    }
                    charters.Add(new ProvingCharter(cid, ckind, cparent, aiShare, verdictShare));
                }
            }
            if (!chart.TryGetProperty("positions", out var positionsJson) || positionsJson.ValueKind != JsonValueKind.Array)
                return (null, $"{from}'s chart names no positions (an empty list for none).");
            var positions = new List<ProvingPosition>();
            foreach (var p in positionsJson.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) return (null, $"{from}'s chart holds a position that is not an object.");
                foreach (var f in p.EnumerateObject())
                    if (f.Name is not ("id" or "unit" or "script" or "head"))
                        return (null, $"{from}'s position holds '{f.Name}', and a position holds 'id', 'unit', 'script' and 'head' only.");
                var head = false;
                if (p.TryGetProperty("head", out var headJson))
                {
                    if (headJson.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (null, $"{from}'s position has a 'head' that is not true or false.");
                    head = headJson.GetBoolean();
                }
                if (Text(p, "id") is not { Length: > 0 } id || Text(p, "unit") is not { Length: > 0 } unit)
                    return (null, $"{from}'s chart holds a position without an id or a unit.");
                if (positions.Any(x => x.Id == id)) return (null, $"{from}'s chart names position '{id}' twice.");
                if (!p.TryGetProperty("script", out var script) || script.ValueKind != JsonValueKind.Array || script.GetArrayLength() == 0)
                    return (null, $"{from}'s position '{id}' has no script: a list of turns, each a list of steps.");
                var turns = new List<IReadOnlyList<string>>();
                foreach (var turn in script.EnumerateArray())
                {
                    if (turn.ValueKind != JsonValueKind.Array || turn.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String))
                        return (null, $"{from}'s position '{id}' has a turn that is not a list of steps in text.");
                    turns.Add([.. turn.EnumerateArray().Select(s => s.GetString()!)]);
                }
                positions.Add(new ProvingPosition(id, unit, turns, head));
            }

            var configuration = new ProvingConfiguration(name, kind.Value, units, charters, positions, envelope, trials, verdicts, deadline);
            return configuration.ChartRefusal(from) is { } chartWhy ? (null, chartWhy) : (configuration, "");
        }
    }

    /// <summary>A template id: a lower-case letter, then up to 19 lower-case letters, digits and '-' — never an id the app mints.</summary>
    static readonly Regex TemplateId = new("^[a-z][a-z0-9-]{0,19}$", RegexOptions.CultureInvariant);

    /// <summary>A token in a script the writer replaces with the id the app minted for a template id: <c>{position:x}</c> or <c>{unit:x}</c>.</summary>
    internal static readonly Regex Token = new(@"\{(position|unit):([^}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// WHY THIS CHART TEMPLATE IS NOT ONE THIS BUILD APPLIES, or null: the floor seats nobody and charters nothing; a chart of seats seats
    /// every founding position in its own unit, and anything more lives in units it charters under the research division — teams, each
    /// with a template id, a share no larger than a bounded experiment takes (<see cref="OrgBounds.ExperimentMaxShare"/>), at least one
    /// position, at most one head and <see cref="OrgBounds.MaxMembers"/> members — and every token in a script names a template id it declares.
    /// </summary>
    string? ChartRefusal(string from)
    {
        if (Kind == ProvingConfigurationKind.Floor)
            return Positions.Count > 0 || Charters.Count > 0
                ? $"{from} is the floor, which seats no position, charters no unit and plays no script — it is a search with no model; it names "
                  + $"{Positions.Count} position(s) and {Charters.Count} charter(s)."
                : null;
        if (Positions.Count == 0) return $"{from} is a chart of seats with no position on it.";
        foreach (var unit in Units)
            if (!FoundingUnits.Contains(unit, StringComparer.Ordinal))
                return $"{from}'s chart names unit '{unit}' among its founding units, and the founding units are {string.Join(", ", FoundingUnits)}: "
                       + "a unit beyond them is chartered (a 'charters' entry), never named into being. Nothing was applied.";

        var chartered = new Dictionary<string, ProvingCharter>(StringComparer.Ordinal);
        foreach (var c in Charters)
        {
            if (!TemplateId.IsMatch(c.Id) || FoundingUnits.Contains(c.Id, StringComparer.Ordinal))
                return $"{from}'s charter '{c.Id}' is no template id: a lower-case letter then up to 19 lower-case letters, digits and '-', and no founding unit's.";
            if (!chartered.TryAdd(c.Id, c)) return $"{from} charters '{c.Id}' twice.";
            if (c.Kind != OrgUnitKind.Team)
                return $"{from}'s charter '{c.Id}' is a '{c.Kind}', and a proving template charters teams ('{OrgUnitKind.Team}') only.";
            if (c.Parent != OrgIds.Research)
                return $"{from}'s charter '{c.Id}' is chartered under '{c.Parent}', and a proving template charters under {OrgIds.Research}, which the "
                       + "research position heads — never under operations, whose structure is a board act. Nothing was applied.";
            if (c.AiShare <= 0m || c.AiShare > OrgBounds.ExperimentMaxShare)
                return string.Create(CultureInfo.InvariantCulture,
                    $"{from}'s charter '{c.Id}' asks an AI share of {c.AiShare}, and a chartered team is a bounded experiment funded with above 0 and at most {OrgBounds.ExperimentMaxShare} of its parent's envelope.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var position in Positions)
        {
            ids.Add(position.Id);
            if (FoundingPositions.TryGetValue(position.Id, out var sits))
            {
                if (sits != position.Unit)
                    return $"{from}'s position '{position.Id}' is placed in unit '{position.Unit}', and it sits in '{sits}'.";
                if (!Units.Contains(position.Unit, StringComparer.Ordinal))
                    return $"{from}'s position '{position.Id}' sits in unit '{position.Unit}', which the chart's units do not name.";
                if (position.Head)
                    return $"{from}'s position '{position.Id}' is a founding position, which heads what it already heads: 'head' is for a chartered unit's position.";
                continue;
            }
            if (!chartered.ContainsKey(position.Unit))
                return $"{from}'s chart names position '{position.Id}' in unit '{position.Unit}', and a position beyond the founding chart's "
                       + $"({string.Join(", ", FoundingPositions.Keys)}) sits in a unit the template charters. Nothing was applied.";
            if (!TemplateId.IsMatch(position.Id) || position.Id.StartsWith("pos-", StringComparison.Ordinal))
                return $"{from}'s position '{position.Id}' is no template id: a lower-case letter then up to 19 lower-case letters, digits and '-', never 'pos-…', which the app mints.";
        }
        foreach (var c in Charters)
        {
            var seated = Positions.Where(p => p.Unit == c.Id).ToList();
            if (seated.Count == 0) return $"{from} charters '{c.Id}' and seats nobody in it: a unit with no position does no work and is not chartered.";
            if (seated.Count(p => p.Head) > 1) return $"{from} appoints {seated.Count(p => p.Head)} heads of '{c.Id}', and a unit has one.";
            if (seated.Count(p => !p.Head) > OrgBounds.MaxMembers)
                return $"{from} seats {seated.Count(p => !p.Head)} members in '{c.Id}', and one head leads at most {OrgBounds.MaxMembers}.";
        }
        if (Positions.Count > OrgBounds.MaxPositions)
            return $"{from} seats {Positions.Count} positions, and the chart carries at most {OrgBounds.MaxPositions}.";

        // EVERY FOUNDING POSITION IS SEATED: one left off the template would still be on the chart and woken by the kernel's own records
        // (a promotion's boundary wakes the chair), adding a seat and spend the configuration never named.
        foreach (var founding in FoundingPositions.Keys)
            if (!Positions.Any(p => p.Id == founding))
                return $"{from} leaves position '{founding}' off its chart, and every founding position is on every home's chart, woken by the "
                       + "kernel's own records whatever a template says: a chart of seats seats each of them, with its script. Nothing was applied.";

        // EVERY TOKEN NAMES WHAT THE TEMPLATE DECLARES: the writer replaces it with the id the app minted, and a token it could not
        // replace would reach a seat as words that name nothing.
        foreach (var position in Positions)
            foreach (var step in position.Script.SelectMany(t => t))
                foreach (Match m in Token.Matches(step))
                {
                    var known = m.Groups[1].Value == "position" ? ids.Contains(m.Groups[2].Value)
                        : chartered.ContainsKey(m.Groups[2].Value) || FoundingUnits.Contains(m.Groups[2].Value, StringComparer.Ordinal);
                    if (!known)
                        return $"{from}'s position '{position.Id}' has a step naming '{m.Value}', and the template declares no such {m.Groups[1].Value}.";
                }
        return null;
    }

    /// <summary>
    /// WHY THESE CONFIGURATIONS DO NOT RUN SIDE BY SIDE, or null: a ladder compares configurations at EQUAL budgets on the same
    /// markets, so every trial budget, verdict budget, AI envelope and deadline must be the same, and every name different.
    /// </summary>
    public static string? UnequalBudgets(IReadOnlyList<ProvingConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if (configurations.Count == 0) return "a proving run names no configuration.";
        var first = configurations[0];
        foreach (var c in configurations.Skip(1))
        {
            if (c.TrialBudget != first.TrialBudget || c.VerdictBudget != first.VerdictBudget || c.AiEnvelope != first.AiEnvelope
                || c.DeadlineSeconds != first.DeadlineSeconds)
                return $"configurations '{first.Name}' and '{c.Name}' do not work at equal budgets: {Budgets(first)} against {Budgets(c)}. "
                       + "A ladder compares configurations at equal budgets on the same markets (ORCHESTRATION § 2.14), so the run did not start.";
        }
        var twice = configurations.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return twice is null ? null : $"two configurations are named '{twice.Key}', and each names its own home.";
    }

    /// <summary>The four budgets in words.</summary>
    public static string Budgets(ProvingConfiguration c) => string.Create(CultureInfo.InvariantCulture,
        $"{c.TrialBudget} trial(s), {c.VerdictBudget} verdict(s), an AI envelope of {c.AiEnvelope} and {c.DeadlineSeconds} s");

    static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    static int? Whole(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;
}

/// <summary>What applying a configuration wrote, for its report.</summary>
/// <param name="Scripts">Each scripted position, by its template id, and the file its script was written to.</param>
/// <param name="Positions">Each position of the template, by its template id, and the position id it holds on this home's chart — a founding
/// position its own, a hired one the id the app minted.</param>
/// <param name="Units">Each unit the template chartered, by its template id, and the unit id the app minted.</param>
public sealed record ProvingApplied(IReadOnlyDictionary<string, string> Scripts, IReadOnlyDictionary<string, string> Positions,
    IReadOnlyDictionary<string, string> Units);

/// <summary>
/// WHAT THE HOST THAT METERS LAUNCHES ANSWERS FOR A TEMPLATE'S CHARTERS (<c>U-proving-ground-d</c> item 1): a charter's funding as
/// admission sizes it for the chartering head (<see cref="CharterFunding"/>; null refuses), and the owner's in-process activation of a
/// hired position in this proving home — the press the app's "For you" page makes, which returns its words or throws refused.
/// </summary>
public sealed record ProvingChartHost(Func<string, CharterFunding?> Funding, Func<string, string> Activate);

/// <summary>
/// APPLIES A CONFIGURATION TO A PROVING HOME (<c>U-proving-ground-c</c> item 2; <c>U-proving-ground-d</c> item 1): the root unit's AI
/// envelope through the settings the meter sizes the root from; each unit the template charters through <see cref="OrgStore.Charter"/>
/// by the research position, which heads its parent — a funded, bounded experiment with its collapse date and a <c>unit-admissible</c>
/// forecast, inside the rights matrix, the chart's bounds, the weekly throttle and the earning test; each of its positions through
/// <see cref="OrgStore.Hire"/> by the same head, then the owner's activation (<see cref="ProvingChartHost.Activate"/>); and a puppet
/// script per position under the key its home's folder gives it, every <c>{position:x}</c> and <c>{unit:x}</c> replaced by the id the
/// app minted. NEVER SEEDED SQL: what a richer chart holds, the kernel wrote, and refused what it refuses.
///
/// <para><b>Only in a home marked proving</b> (SIM-001): the database must be opened as a proving home's and carry the
/// proving mark on disk. Anything else — the owner's home, a test assembly's home, an unmarked file — is refused before
/// anything is written: a puppet configuration applied there would put scripted seats — and chartered teams — behind a real home's chart.</para>
/// </summary>
public static class ProvingConfigurationWriter
{
    /// <summary>How long a chartered team's bounded experiment runs before it collapses unless its forecast holds.</summary>
    public static readonly TimeSpan CollapseAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Applies <paramref name="configuration"/> to <paramref name="db"/>: its envelope through <paramref name="settings"/> (the
    /// gateway's own update), its charters and hires through the kernel's writers with <paramref name="chart"/>'s answers, and each
    /// position's script into <paramref name="scripts"/> as <c>&lt;key&gt;.json</c>, the key being <paramref name="keyOf"/> of its
    /// position id — the folder name of its home, which is how the puppet finds it.
    /// </summary>
    public static ProvingApplied Apply(Database db, ProvingConfiguration configuration, string scripts,
        Func<string, string> keyOf, Action<Action<TradeAgentSettings>> settings, ProvingChartHost? chart = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scripts);

        if (NotProving(db) is { } refused) throw new InvalidOperationException(refused);
        if (configuration.Charters.Count > 0 && chart is null)
            throw new InvalidOperationException($"configuration '{configuration.Name}' charters {configuration.Charters.Count} unit(s), and this host "
                                                + "answers neither a charter's funding nor an activation. Nothing was applied.");

        // THE FOUNDING POSITIONS ARE ON THE CHART AS THE FOUNDING CHART PUT THEM, ACTIVE — read, never written.
        var org = new OrgStore(db);
        var founding = configuration.Positions.Where(p => ProvingConfiguration.FoundingPositions.ContainsKey(p.Id)).ToList();
        foreach (var position in founding)
        {
            if (org.Position(position.Id) is not { } row || row.Status != OrgPositionStatus.Active || row.UnitId != position.Unit)
                throw new InvalidOperationException(
                    $"configuration '{configuration.Name}' seats position '{position.Id}' in unit '{position.Unit}', and this proving home's chart "
                    + "does not hold it there as an active position. Nothing was applied.");
        }

        settings(s => s.AiDailyCostCap = configuration.AiEnvelope);

        var positions = founding.ToDictionary(p => p.Id, p => p.Id, StringComparer.Ordinal);
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var charter in configuration.Charters)
        {
            var caller = org.Units().FirstOrDefault(u => u.Id == charter.Parent)?.HeadPositionId
                         ?? throw new InvalidOperationException($"unit '{charter.Parent}' has no head in this proving home to charter '{charter.Id}'.");
            var now = DateTimeOffset.UtcNow;
            var collapse = now + CollapseAfter;
            var why = $"proving configuration '{configuration.Name}' charters its '{charter.Id}' team as a bounded experiment (SIMULATED)";
            var made = org.Charter(new CharterRequest(caller, null, charter.Kind, charter.AiShare, why, [], [],
                    [new ForecastClaim(ForecastVocabulary.UnitAdmissible, null, ForecastVocabulary.Admitted, collapse, 0.5m)], collapse,
                    VerdictShare: charter.VerdictShare),
                chart!.Funding(caller), now);
            units[charter.Id] = made.Unit.Id;

            // ITS HEAD FIRST, THEN ITS MEMBERS — each hired by the head of the unit's parent, then activated by the owner's press.
            foreach (var position in configuration.Positions.Where(p => p.Unit == charter.Id).OrderBy(p => p.Head ? 0 : 1))
            {
                var hired = org.Hire(new HireRequest(caller, null, made.Unit.Id, position.Head,
                    $"proving configuration '{configuration.Name}' seats its '{position.Id}' position (SIMULATED)", [], [],
                    [new ForecastClaim(ForecastVocabulary.UnitAdmissible, null, ForecastVocabulary.Admitted, collapse, 0.5m)]), DateTimeOffset.UtcNow);
                if (hired.Position.Status != OrgPositionStatus.Active) chart.Activate(hired.Position.Id);
                if (org.Position(hired.Position.Id) is not { Status: OrgPositionStatus.Active })
                    throw new InvalidOperationException($"position '{position.Id}' ({hired.Position.Id}) was hired and is not active. The run stops here.");
                positions[position.Id] = hired.Position.Id;
            }
        }

        Directory.CreateDirectory(scripts);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var position in configuration.Positions)
        {
            var turns = position.Script.Select(t => t.Select(step => ProvingConfiguration.Token.Replace(step,
                m => m.Groups[1].Value == "position" ? positions[m.Groups[2].Value]
                    : units.TryGetValue(m.Groups[2].Value, out var u) ? u : m.Groups[2].Value)).ToList()).ToList();
            var file = Path.Combine(scripts, keyOf(positions[position.Id]) + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { turns }));
            written[position.Id] = file;
        }
        return new ProvingApplied(written, positions, units);
    }

    /// <summary>Why <paramref name="db"/> is not a proving home's, in words, or null.</summary>
    static string? NotProving(Database db)
    {
        var word = HomeMark.Read(db.FilePath, "meta");
        if (db.Kind == HomeKind.Proving && HomeMark.Parse(word) == HomeKind.Proving) return null;
        return $"TradeAgent did not apply a proving configuration to the database at {db.FilePath}: "
               + (word is null ? "it carries no home mark, so it is a real home's" : $"it is marked '{word}'")
               + $", opened as a {HomeMark.Word(db.Kind)} home's, and a configuration — puppet seats included — is applied only to a home marked "
               + $"'{HomeMark.Word(HomeKind.Proving)}' (SIM-001). Nothing was written.";
    }
}
