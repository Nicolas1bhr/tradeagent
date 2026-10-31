using System.Globalization;
using System.Text.Json;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Proving;

/// <summary>What a configuration is: the deterministic floor, or a chart of seats.</summary>
public enum ProvingConfigurationKind
{
    /// <summary>A closed grammar of programs searched in a fixed order, with no model (<see cref="FloorGrammar"/>).</summary>
    Floor,

    /// <summary>Positions on a chart, each played by its seat — in part c a puppet script per position.</summary>
    Seats
}

/// <summary>One position of a configuration's chart template, and the turns its puppet plays, one array of steps per turn.</summary>
public sealed record ProvingPosition(string Id, string Unit, IReadOnlyList<IReadOnlyList<string>> Script);

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
/// <param name="Units">The chart template's units, by id.</param>
/// <param name="Positions">The chart template's positions, each with its puppet script.</param>
/// <param name="AiEnvelope">The root unit's AI envelope, in the price list's currency.</param>
/// <param name="TrialBudget">Trials per market's campaign.</param>
/// <param name="VerdictBudget">Verdicts per market's campaign.</param>
/// <param name="DeadlineSeconds">How long the configuration works, from its start, before it is stopped.</param>
public sealed record ProvingConfiguration(
    string Name,
    ProvingConfigurationKind Kind,
    IReadOnlyList<string> Units,
    IReadOnlyList<ProvingPosition> Positions,
    decimal AiEnvelope,
    int TrialBudget,
    int VerdictBudget,
    int DeadlineSeconds)
{
    /// <summary>The one format version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The longest a configuration may work: a proving run is scored in hours, never days.</summary>
    public const int MaxDeadlineSeconds = 6 * 3600;

    /// <summary>
    /// THE FOUNDING CHART'S UNITS AND POSITIONS — all part c applies. A template naming any other unit or position (a critic, a
    /// headed team) is part d's, and is refused in words rather than half-written.
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
                if (p.Name is not ("units" or "positions"))
                    return (null, $"{from}'s chart holds '{p.Name}', and a chart template holds 'units' and 'positions' only.");
            if (!chart.TryGetProperty("units", out var unitsJson) || unitsJson.ValueKind != JsonValueKind.Array)
                return (null, $"{from}'s chart names no units.");
            var units = new List<string>();
            foreach (var u in unitsJson.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.String || u.GetString() is not { Length: > 0 } unit) return (null, $"{from}'s chart names a unit that is not text.");
                if (units.Contains(unit, StringComparer.Ordinal)) return (null, $"{from}'s chart names unit '{unit}' twice.");
                units.Add(unit);
            }
            if (!chart.TryGetProperty("positions", out var positionsJson) || positionsJson.ValueKind != JsonValueKind.Array)
                return (null, $"{from}'s chart names no positions (an empty list for none).");
            var positions = new List<ProvingPosition>();
            foreach (var p in positionsJson.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) return (null, $"{from}'s chart holds a position that is not an object.");
                foreach (var f in p.EnumerateObject())
                    if (f.Name is not ("id" or "unit" or "script"))
                        return (null, $"{from}'s position holds '{f.Name}', and a position holds 'id', 'unit' and 'script' only.");
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
                positions.Add(new ProvingPosition(id, unit, turns));
            }

            var configuration = new ProvingConfiguration(name, kind.Value, units, positions, envelope, trials, verdicts, deadline);
            return configuration.ChartRefusal(from) is { } chartWhy ? (null, chartWhy) : (configuration, "");
        }
    }

    /// <summary>
    /// WHY THIS CHART TEMPLATE IS NOT ONE PART C APPLIES, or null: the floor seats nobody; a chart of seats names only the founding
    /// chart's units and positions (a critic or a headed team is part d's), each position in the unit it sits in, at least one.
    /// </summary>
    string? ChartRefusal(string from)
    {
        if (Kind == ProvingConfigurationKind.Floor)
            return Positions.Count > 0
                ? $"{from} is the floor, which seats no position and plays no script — it is a search with no model; it names {Positions.Count}."
                : null;
        if (Positions.Count == 0) return $"{from} is a chart of seats with no position on it.";
        foreach (var unit in Units)
            if (!FoundingUnits.Contains(unit, StringComparer.Ordinal))
                return $"{from}'s chart names unit '{unit}', and this build applies the founding chart's units only ({string.Join(", ", FoundingUnits)}): "
                       + "a template that charters new units — a critic, a headed team — is the next part of the proving ground's. Nothing was applied.";
        foreach (var position in Positions)
        {
            if (!FoundingPositions.TryGetValue(position.Id, out var sits))
                return $"{from}'s chart names position '{position.Id}', and this build seats the founding chart's positions only "
                       + $"({string.Join(", ", FoundingPositions.Keys)}). Nothing was applied.";
            if (sits != position.Unit)
                return $"{from}'s position '{position.Id}' is placed in unit '{position.Unit}', and it sits in '{sits}'.";
            if (!Units.Contains(position.Unit, StringComparer.Ordinal))
                return $"{from}'s position '{position.Id}' sits in unit '{position.Unit}', which the chart's units do not name.";
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
/// <param name="Scripts">Each scripted position and the file its script was written to.</param>
public sealed record ProvingApplied(IReadOnlyDictionary<string, string> Scripts);

/// <summary>
/// APPLIES A CONFIGURATION TO A PROVING HOME (<c>U-proving-ground-c</c> item 2): the root unit's AI envelope through the
/// settings the meter sizes the root from, and a puppet script per position under the key its home's folder gives it.
/// Positions are the founding chart's, which every home holds as O1/O2a wrote them, so nothing is added to the chart and no
/// column is new; a template naming any other is refused at parse.
///
/// <para><b>Only in a home marked proving</b> (SIM-001): the database must be opened as a proving home's and carry the
/// proving mark on disk. Anything else — the owner's home, a test assembly's home, an unmarked file — is refused before
/// anything is written: a puppet configuration applied there would put scripted seats behind a real home's chart.</para>
/// </summary>
public static class ProvingConfigurationWriter
{
    /// <summary>
    /// Applies <paramref name="configuration"/> to <paramref name="db"/>: its envelope through <paramref name="settings"/> (the
    /// gateway's own update), and each position's script into <paramref name="scripts"/> as <c>&lt;key&gt;.json</c>, the key
    /// being <paramref name="keyOf"/> of its id — the folder name of its home, which is how the puppet finds it.
    /// </summary>
    public static ProvingApplied Apply(Database db, ProvingConfiguration configuration, string scripts,
        Func<string, string> keyOf, Action<Action<TradeAgentSettings>> settings)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scripts);

        if (NotProving(db) is { } refused) throw new InvalidOperationException(refused);

        // THE POSITIONS ARE ON THE CHART AS THE FOUNDING CHART PUT THEM, ACTIVE — read, never written.
        var org = new OrgStore(db);
        foreach (var position in configuration.Positions)
        {
            if (org.Position(position.Id) is not { } row || row.Status != OrgPositionStatus.Active || row.UnitId != position.Unit)
                throw new InvalidOperationException(
                    $"configuration '{configuration.Name}' seats position '{position.Id}' in unit '{position.Unit}', and this proving home's chart "
                    + "does not hold it there as an active position. Nothing was applied.");
        }

        settings(s => s.AiDailyCostCap = configuration.AiEnvelope);

        Directory.CreateDirectory(scripts);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var position in configuration.Positions)
        {
            var file = Path.Combine(scripts, keyOf(position.Id) + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { turns = position.Script }));
            written[position.Id] = file;
        }
        return new ProvingApplied(written);
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
