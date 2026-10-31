using System.Text.Json;
using System.Text.Json.Serialization;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Board;

/// <summary>
/// ONE PAIR'S STANDING PAPER ENVELOPE TERMS AS A POLICY STATES THEM (R24 § 4.5; per pair since <c>U-evidence-breadth-paper</c>): on
/// the app's own paper venue only, the ONE instrument they are for, a quantity and a value ceiling — BOTH bind each deployment, so
/// the value ceiling is the one that binds whenever the instrument's price makes the quantity worth more — how many deployments it
/// carries, and for how many days, or null for no expiry. Terms, not an envelope: the paper allocator grants one from them through
/// the gateway's own grant path (<c>U-board-a2</c>), and a version is admitted only into the envelope of the instrument it trades.
/// </summary>
public sealed record PaperEnvelopeTerms(string Connector, string Account, string Instrument, decimal MaxQuantity, decimal MaxNotional,
    int Deployments, int? Days);

/// <summary>
/// THE BOARD'S POLICY, TYPED (<c>U-board-a1</c>; R24 §§ 4.1, 4.5): every value the gateway, the meter and the paper allocator read
/// where they read an owner-set box before the board — mode and activation, the gateway's limits, the plan's AI day-total and its
/// division, the turn allowance, the lanes, the wake policy and the standing paper envelope's terms.
///
/// <para><b>What it never holds</b> (R24's hard lines; a guard reflects over it): nothing that names, times or sizes an order
/// (ORDER-001, SIGNAL-001), nothing of the evidence — paper friction, the holdout, the AI's price, a campaign's budgets, the judge's
/// capital (EVID-001) — and nothing of the board's own power: its composition, its budget, the audit line's or the watcher's
/// (CHANGE-001). The owner's real-money cap and perception's budget inside it stay his settings (AUTH-001).</para>
///
/// <para><b>An instance is only a value.</b> Anyone can build one; what GOVERNS is the version in force that <see cref="BoardStore"/>
/// reads from <c>board_policy</c>, whose CHECKs refuse a live mode and an activation, so no row of the store can hold either. The
/// gateway takes it from there and from nowhere else (a test host's seam aside, which nothing in the product calls).</para>
/// </summary>
public sealed record BoardPolicy
{
    public required TradingMode Mode { get; init; }
    public required bool LiveActivated { get; init; }

    readonly RiskPolicy _risk = new();

    /// <summary>The gateway's limits. A COPY on every read: a reader that changed what it was handed changes nothing in force.</summary>
    public required RiskPolicy Risk { get => Copy(_risk); init => _risk = Copy(value); }

    /// <summary>The plan's AI day-total, a list-price equivalent within the owner's plan — never money (ORGANISATION § 10).</summary>
    public required decimal PlanDayTotal { get; init; }

    /// <summary>Each role's fraction of the day-total, by role id; absent is an equal share.</summary>
    public required IReadOnlyDictionary<string, decimal> RoleShares { get; init; }

    /// <summary>
    /// THIS ROLE'S FRACTION OF THE DAY'S CEILING (<c>U-board-a2</c> item 9; the reader the settings row held until the shares moved here):
    /// its share when it is one, and an equal share when none is set or the one set is not a fraction — a zero or a negative would be a
    /// role that can never work, which is a stop nobody pressed. A slice of the ceiling, never an addition to it: shares are applied on
    /// top of the global gate, so shares summing past one still cannot spend past the ceiling.
    /// </summary>
    public decimal ShareForRole(string role) =>
        RoleShares.TryGetValue(role, out var s) && s > 0m && s <= 1m
            ? s
            : 1m / CouncilRoles.All.Length;

    public required long TurnAllowanceInput { get; init; }
    public required long TurnAllowanceOutput { get; init; }
    public required int Lanes { get; init; }
    public required int LanesPerPool { get; init; }
    public required int LanesPerCellClass { get; init; }

    /// <summary>How often the loop reviews its missions, in minutes (the wake policy).</summary>
    public required int ReviewMinutes { get; init; }

    /// <summary>The most a head's wake interval grows to, in hours (the wake policy).</summary>
    public required int HeadCeilingHours { get; init; }

    /// <summary>The re-wake floors by level, in minutes (the wake policy).</summary>
    public required IReadOnlyDictionary<string, int> RewakeFloorMinutes { get; init; }

    /// <summary>
    /// EACH PAIR'S STANDING PAPER ENVELOPE TERMS, by the instrument they are for (<c>U-evidence-breadth-paper</c>): every entry's key
    /// IS its terms' <see cref="PaperEnvelopeTerms.Instrument"/> (the strict read refuses a body where it is not), so one pair's terms
    /// can never stand under another's name. Empty for none. Ordinal keys.
    /// </summary>
    public required IReadOnlyDictionary<string, PaperEnvelopeTerms> PaperEnvelopes { get; init; }

    /// <summary>The terms the policy states for <paramref name="instrument"/>, or null for none.</summary>
    public PaperEnvelopeTerms? PaperEnvelopeFor(string instrument) =>
        PaperEnvelopes.TryGetValue(instrument, out var terms) ? terms : null;

    /// <summary>
    /// TRUE ONLY ON <see cref="Unreadable"/>: no value here came from a record that verified. Not stored, never in the body — a fact
    /// about this read, which the health row and the dispatch gate say in words.
    /// </summary>
    [JsonIgnore] public bool CouldNotBeRead { get; private init; }

    [JsonIgnore] public bool ModeAllowsExecution => Enum.IsDefined(Mode) && Mode != TradingMode.OBSERVE;
    [JsonIgnore] public bool ModeIsLive => Mode is TradingMode.LIVE_CONFIRM or TradingMode.LIVE_AUTONOMOUS;

    /// <summary>
    /// THE POLICY A BUILD USES WHEN THE ONE IN FORCE CANNOT BE READ (<c>U-board-a1</c> item 6) — <see cref="TradeAgentSettings.Unreadable"/>'s
    /// shape: OBSERVE, real money off, a quantity, position and rate limit of zero over an allowlist that allows nothing, a plan
    /// day-total of zero so no plan turn is admitted, and no envelope on any pair. Never the founding defaults re-derived: a record that failed to
    /// verify is not a fresh home, and treating it as one would be the failure handing out authority nobody decided.
    /// </summary>
    public static BoardPolicy Unreadable() => new()
    {
        CouldNotBeRead = true,
        Mode = TradingMode.OBSERVE,
        LiveActivated = false,
        Risk = new RiskPolicy { MaxOrderQuantity = 0m, MaxOpenPositions = 0, MaxOrdersPerMinute = 0, InstrumentAllowlist = [] },
        PlanDayTotal = 0m,
        RoleShares = new Dictionary<string, decimal>(StringComparer.Ordinal),
        TurnAllowanceInput = TurnAllowance.Default.InputTokens,
        TurnAllowanceOutput = TurnAllowance.Default.OutputTokens,
        Lanes = 1,
        LanesPerPool = 1,
        LanesPerCellClass = 1,
        ReviewMinutes = 30,
        HeadCeilingHours = 24,
        RewakeFloorMinutes = new Dictionary<string, int>(Db.RewakeFloors.DefaultMinutes, StringComparer.Ordinal),
        PaperEnvelopes = new Dictionary<string, PaperEnvelopeTerms>(StringComparer.Ordinal)
    };

    static RiskPolicy Copy(RiskPolicy r) => new()
    {
        MaxOrderQuantity = r.MaxOrderQuantity,
        MaxNotionalPerOrder = r.MaxNotionalPerOrder,
        MaxOpenPositions = r.MaxOpenPositions,
        MaxOrdersPerMinute = r.MaxOrdersPerMinute,
        MaxLossPerTrade = r.MaxLossPerTrade,
        MaxDailyLoss = r.MaxDailyLoss,
        LossMinClosureHours = r.LossMinClosureHours,
        LossStrikeWindowDays = r.LossStrikeWindowDays,
        ValuationLossExitMinutes = r.ValuationLossExitMinutes,
        InstrumentAllowlist = [.. r.InstrumentAllowlist]
    };

    /// <summary>Value equality over every field, the dictionaries and the allowlist by content.</summary>
    public bool Equals(BoardPolicy? other) =>
        other is not null && BoardPolicyBody.Canonical(this) == BoardPolicyBody.Canonical(other) && CouldNotBeRead == other.CouldNotBeRead;

    public override int GetHashCode() => BoardPolicyBody.Canonical(this).GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// THE POLICY'S BODY ON DISK — canonical JSON (<see cref="TapeJson.Canonical"/>: keys in ordinal order, numbers as written) and its
/// SHA-256, and the STRICT reader (<c>U-board-a1</c> item 2): a field missing, a field this build does not know, a value of the wrong
/// type, a number for the mode, a null where none may be, a body not in its canonical spelling, or a hash that differs — each is
/// refused, never defaulted. A body this build wrote round-trips byte for byte; anything else is not a body it wrote.
/// </summary>
public static class BoardPolicyBody
{
    sealed record Body(
        TradingMode Mode, bool LiveActivated, RiskBody Risk, decimal PlanDayTotal, Dictionary<string, decimal> RoleShares,
        long TurnAllowanceInput, long TurnAllowanceOutput, int Lanes, int LanesPerPool, int LanesPerCellClass, int ReviewMinutes,
        int HeadCeilingHours, Dictionary<string, int> RewakeFloorMinutes, Dictionary<string, EnvelopeBody> PaperEnvelopes);

    sealed record RiskBody(
        decimal MaxOrderQuantity, decimal MaxNotionalPerOrder, int MaxOpenPositions, int MaxOrdersPerMinute, decimal MaxLossPerTrade,
        decimal MaxDailyLoss, decimal LossMinClosureHours, int LossStrikeWindowDays, decimal ValuationLossExitMinutes,
        List<string> InstrumentAllowlist);

    sealed record EnvelopeBody(string Connector, string Account, string Instrument, decimal MaxQuantity, decimal MaxNotional,
        int Deployments, int? Days);

    static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    /// <summary>The canonical body of <paramref name="policy"/>: what <c>board_policy.body</c> holds.</summary>
    public static string Canonical(BoardPolicy policy)
    {
        var risk = policy.Risk;
        var body = new Body(policy.Mode, policy.LiveActivated,
            new RiskBody(risk.MaxOrderQuantity, risk.MaxNotionalPerOrder, risk.MaxOpenPositions, risk.MaxOrdersPerMinute,
                risk.MaxLossPerTrade, risk.MaxDailyLoss, risk.LossMinClosureHours, risk.LossStrikeWindowDays,
                risk.ValuationLossExitMinutes, [.. risk.InstrumentAllowlist]),
            policy.PlanDayTotal, new Dictionary<string, decimal>(policy.RoleShares, StringComparer.Ordinal),
            policy.TurnAllowanceInput, policy.TurnAllowanceOutput, policy.Lanes, policy.LanesPerPool, policy.LanesPerCellClass,
            policy.ReviewMinutes, policy.HeadCeilingHours, new Dictionary<string, int>(policy.RewakeFloorMinutes, StringComparer.Ordinal),
            policy.PaperEnvelopes.ToDictionary(p => p.Key,
                p => new EnvelopeBody(p.Value.Connector, p.Value.Account, p.Value.Instrument, p.Value.MaxQuantity, p.Value.MaxNotional,
                    p.Value.Deployments, p.Value.Days), StringComparer.Ordinal));
        return TapeJson.Canonical(JsonSerializer.Serialize(body, Strict));
    }

    /// <summary>The hash a row stores for <paramref name="canonicalBody"/>: lower-case hex SHA-256 of its UTF-8 bytes.</summary>
    public static string Sha256(string canonicalBody) => Sha256Hex.Of(canonicalBody);

    /// <summary>
    /// THE STRICT READ: the policy <paramref name="body"/> states, or the sentence refusing it. <paramref name="sha256"/> must be the
    /// body's own hash and the body its own canonical spelling; every field must be present, known and of its type.
    /// </summary>
    public static (BoardPolicy? Policy, string? Why) Read(string body, string sha256)
    {
        if (!string.Equals(Sha256(body), sha256, StringComparison.Ordinal))
            return (null, "its body does not match the hash stored with it");
        string canonical;
        try { canonical = TapeJson.Canonical(body); }
        catch (JsonException ex) { return (null, $"its body is not one JSON value ({ex.Message})"); }
        if (!string.Equals(canonical, body, StringComparison.Ordinal))
            return (null, "its body is not written the one way this build writes a policy");

        Body? read;
        try { read = JsonSerializer.Deserialize<Body>(body, Strict); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, $"its body is not a policy this build reads ({ex.Message})");
        }
        if (read is null) return (null, "its body is the JSON literal null");
        if (!Enum.IsDefined(read.Mode)) return (null, "its mode is not one this build knows");
        if (read.Risk.InstrumentAllowlist.Any(string.IsNullOrWhiteSpace))
            return (null, "its instrument allowlist names an empty instrument");
        // ONE PAIR'S TERMS UNDER ITS OWN NAME ONLY (U-evidence-breadth-paper): a key that is not a pair, or terms for another
        // instrument than the key that holds them, is a body this build never writes — refused, never read as either pair's.
        foreach (var (pair, terms) in read.PaperEnvelopes)
        {
            if (!BinanceArchive.IsPair(pair)) return (null, $"its paper envelopes name '{pair}', which is not a pair");
            if (!string.Equals(pair, terms.Instrument, StringComparison.Ordinal))
                return (null, $"its paper envelope under {pair} states terms for {terms.Instrument}");
        }

        var policy = new BoardPolicy
        {
            Mode = read.Mode,
            LiveActivated = read.LiveActivated,
            Risk = new RiskPolicy
            {
                MaxOrderQuantity = read.Risk.MaxOrderQuantity,
                MaxNotionalPerOrder = read.Risk.MaxNotionalPerOrder,
                MaxOpenPositions = read.Risk.MaxOpenPositions,
                MaxOrdersPerMinute = read.Risk.MaxOrdersPerMinute,
                MaxLossPerTrade = read.Risk.MaxLossPerTrade,
                MaxDailyLoss = read.Risk.MaxDailyLoss,
                LossMinClosureHours = read.Risk.LossMinClosureHours,
                LossStrikeWindowDays = read.Risk.LossStrikeWindowDays,
                ValuationLossExitMinutes = read.Risk.ValuationLossExitMinutes,
                InstrumentAllowlist = [.. read.Risk.InstrumentAllowlist]
            },
            PlanDayTotal = read.PlanDayTotal,
            RoleShares = new Dictionary<string, decimal>(read.RoleShares, StringComparer.Ordinal),
            TurnAllowanceInput = read.TurnAllowanceInput,
            TurnAllowanceOutput = read.TurnAllowanceOutput,
            Lanes = read.Lanes,
            LanesPerPool = read.LanesPerPool,
            LanesPerCellClass = read.LanesPerCellClass,
            ReviewMinutes = read.ReviewMinutes,
            HeadCeilingHours = read.HeadCeilingHours,
            RewakeFloorMinutes = new Dictionary<string, int>(read.RewakeFloorMinutes, StringComparer.Ordinal),
            PaperEnvelopes = read.PaperEnvelopes.ToDictionary(p => p.Key,
                p => new PaperEnvelopeTerms(p.Value.Connector, p.Value.Account, p.Value.Instrument, p.Value.MaxQuantity,
                    p.Value.MaxNotional, p.Value.Deployments, p.Value.Days), StringComparer.Ordinal)
        };

        // AND BACK: the policy read must write the very body it was read from — a number spelled another way, a key in another
        // case, anything this build would not have written, is a body it did not write.
        if (!string.Equals(Canonical(policy), body, StringComparison.Ordinal))
            return (null, "its body does not read back as the body this build would write for it");
        return (policy, null);
    }

    /// <summary>
    /// EVERY FIELD OF A BODY, FLATTENED, as the Board page shows one: its path (<c>risk.max_order_quantity</c>), and its value as text
    /// — a number as written, a word as itself, a list joined, null as <c>none</c>. In the body's own (ordinal) order.
    /// </summary>
    public static IReadOnlyList<(string Path, string Value)> Flatten(string canonicalBody)
    {
        using var doc = JsonDocument.Parse(canonicalBody);
        var fields = new List<(string, string)>();
        Walk(doc.RootElement, "", fields);
        return fields;

        static void Walk(JsonElement e, string path, List<(string, string)> into)
        {
            // AN EMPTY GROUP IS ONE FIELD, "none" (a policy with no paper envelope on any pair still shows that it holds none).
            if (e.ValueKind == JsonValueKind.Object && !IsMap(path) && (path.Length == 0 || e.EnumerateObject().Any()))
            {
                foreach (var p in e.EnumerateObject())
                    Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", into);
                return;
            }
            into.Add((path, Text(e)));
        }

        static bool IsMap(string path) => path is "role_shares" or "rewake_floor_minutes";

        static string Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Null => "none",
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => e.GetArrayLength() == 0 ? "none" : string.Join(", ", e.EnumerateArray().Select(Text)),
            JsonValueKind.Object => e.EnumerateObject().Any()
                ? string.Join(", ", e.EnumerateObject().Select(p => $"{p.Name} {Text(p.Value)}"))
                : "none",
            _ => e.GetRawText()
        };
    }
}
