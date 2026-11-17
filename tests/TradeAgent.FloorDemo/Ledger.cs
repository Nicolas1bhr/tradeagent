using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.FloorDemo;

/// <summary>
/// THE DEMO'S PEN: every SYNTHETIC row the floor demo writes, written the way the app's ledgers hold it — through the stores where a store
/// writes it (launches, tool calls, decisions, the owner's words), as the ledger's own rows where only a verb would (the chart, mandates,
/// deliveries, escalations, laterals). Nothing here is a real model's work, and no text here reads like a trading edge.
/// </summary>
sealed class Ledger(Database db, Random rng)
{
    public Database Db => db;
    public OrgStore Org { get; } = new(db);

    public static string T(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public string Hex(int n) => string.Concat(Enumerable.Range(0, n).Select(_ => "0123456789abcdef"[rng.Next(16)]));
    public T1 Pick<T1>(IReadOnlyList<T1> from) => from[rng.Next(from.Count)];
    public double Next() => rng.NextDouble();
    public int Next(int lo, int hi) => rng.Next(lo, hi);

    public void Exec(string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    // ---- the chart -----------------------------------------------------------------------------------------------------------------

    public string Unit(string parent, string decider, DateTimeOffset at)
    {
        var id = OrgStore.UnitPrefix + Hex(12);
        Exec("INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($id,$p,$k,NULL,$s,$at)",
            ("$id", id), ("$p", parent), ("$k", OrgUnitKind.Team), ("$s", OrgUnitStatus.Active), ("$at", T(at)));
        Chart(at, id, null, OrgEventKind.Chartered, decider, $"unit={id} kind={OrgUnitKind.Team} parent={parent} share=none decision=none");
        return id;
    }

    /// <summary>A position hired into <paramref name="unit"/> by <paramref name="decider"/>, dormant; <paramref name="head"/> fills the unit's head.</summary>
    public string Hire(string unit, string decider, bool head, DateTimeOffset at)
    {
        var id = OrgIds.PositionPrefix + Hex(12);
        Exec("INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", CouncilRoles.HomeDir(id)), ("$s", OrgPositionStatus.Dormant), ("$at", T(at)));
        if (head) Exec("UPDATE org_unit SET head_position_id=$id WHERE id=$u AND head_position_id IS NULL", ("$id", id), ("$u", unit));
        Chart(at, unit, id, OrgEventKind.Hired, decider,
            $"position={id} unit={unit} as={(head ? "head" : "member")} status={OrgPositionStatus.Dormant} home={CouncilRoles.HomeDir(id)} decision=none");
        return id;
    }

    /// <summary>A dormant position made active by the head that hired it (the self-activation the app records with the hirer as decider).</summary>
    public void Activate(string position, string unit, string decider, DateTimeOffset at)
    {
        Exec("UPDATE org_position SET status=$a WHERE id=$p AND status=$d",
            ("$a", OrgPositionStatus.Active), ("$p", position), ("$d", OrgPositionStatus.Dormant));
        Chart(at, unit, position, OrgEventKind.Activated, decider, $"position={position} unit={unit} was={OrgPositionStatus.Dormant}");
    }

    public void Chart(DateTimeOffset at, string unit, string? position, string kind, string decider, string detail) =>
        Exec("INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at,$u,$p,$k,$d,$x)",
            ("$at", T(at)), ("$u", unit), ("$p", position), ("$k", kind), ("$d", decider), ("$x", detail));

    // ---- turns and tool calls ------------------------------------------------------------------------------------------------------

    public string Begin(string role, DateTimeOffset at, string? mandate = null)
    {
        var id = "turn-" + Hex(12);
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = role, ReservedCost = 0.40m, SeatClass = SeatClasses.Harness, BillingClass = BillingClasses.Key,
            MandateId = mandate, Runtime = "claude", RequestedModel = rng.Next(4) == 0 ? "claude-opus" : "claude-sonnet",
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null,
            Funding = [.. Org.FundingLine(role).Select(u => u.Id)]
        });
        if (!admission.Admitted) throw new InvalidOperationException($"the demo's launch for {role} was refused: {admission.Refusal}");
        return id;
    }

    /// <summary>Ends a turn; <paramref name="cost"/> null is a turn the runtime reported usage for but nothing priced — unknown, never zero.</summary>
    public bool End(string id, DateTimeOffset at, decimal? cost, string model) =>
        new AiAttemptStore(db).End(id, 0, at, rng.Next(8_000, 40_000), rng.Next(2_000, 20_000), 0, rng.Next(800, 4_000), 0, model, cost,
            cost is null ? "no price is known for this model on this runtime" : null, null);

    /// <summary>Loses every open launch at <paramref name="at"/>, as a restart would: the reservation stands as its cost.</summary>
    public int LoseOpen(DateTimeOffset at) => new AiAttemptStore(db).LoseOpen(at);

    public decimal Cost() => Math.Round(0.05m + (decimal)Next() * 0.55m, 2);

    static readonly (string Tool, string Argument)[] Reads =
    [
        ("data", "data bars BTCUSDT 1h"), ("data", "data bars ETHUSDT 15m"), ("data", "data bars SOLUSDT 4h"), ("data", "data datasets"),
        ("data", "data coverage BTCUSDT"), ("data", "data gaps ETHUSDT 1h"), ("trade", "trade status"), ("trade", "trade runs"),
        ("trade", "trade backtest v3 BTCUSDT 1h"), ("trade", "trade backtest v2 ETHUSDT 15m"), ("trade", "trade versions"),
        ("report", "report read"), ("report", "report write"), ("mandate_show", "mandate-show"), ("mandate_list", "mandate-list"),
    ];

    static readonly string[] Refusals =
    [
        "the dataset named is not one this home holds, so nothing was read.",
        "the backtest window is longer than this position's cap allows, so nothing was run.",
        "the arguments were not a JSON object, so nothing was run.",
        "that version does not parse, so no backtest was run.",
    ];

    /// <summary>One tool call answered at <paramref name="at"/>; one in twelve refused. <paramref name="verb"/> names a door verb instead of a read.</summary>
    public (string Tool, string Argument, bool Served) Tool(string attempt, string role, DateTimeOffset at, (string Tool, string Argument)? verb = null)
    {
        var (tool, argument) = verb ?? Pick(Reads);
        var served = verb is not null || rng.Next(12) != 0;
        new ToolCallStore(db).Record(new ToolCallRow
        {
            At = at, Attempt = attempt, Role = role, Tool = tool, Argument = argument, Bytes = served ? rng.Next(200, 60_000) : 0,
            Served = served, Refusal = served ? null : Pick(Refusals), Duration = TimeSpan.FromMilliseconds(rng.Next(40, tool == "trade" ? 9_000 : 1_500))
        });
        return (tool, argument, served);
    }

    // ---- work ----------------------------------------------------------------------------------------------------------------------

    public static readonly string[] Objectives =
    [
        "Backtest the opening-range breakout on the last six months and report the costs.",
        "Collect a second source for the hourly bars and compare the two.",
        "Check the hourly bars for gaps over the last quarter and list them.",
        "Re-run last week's backtest with the fees doubled and report the difference.",
        "Write up which datasets the team holds, with their coverage and their gaps.",
        "Compare the two fee models on one month of bars and say which the runs should use.",
        "Measure how late the minute bars arrive and how often, over two days.",
        "Repeat the moving-average baseline on a second pair and report the trade count.",
    ];

    public static readonly string[] Reports =
    [
        "The run covered six months of hourly bars; the result is negative after fees. The trade list and the costs are attached.",
        "Both sources agree within 2% on most hours; the hours where they do not are listed in the attached table.",
        "Eleven gaps found, the longest four hours; each is listed with its start and end.",
        "With the fees doubled the result falls further below zero; the two runs are cited side by side.",
        "The team holds four datasets; coverage and gaps for each are in the table.",
        "The second fee model is closer to the venue's published schedule; the comparison is attached.",
    ];

    public static readonly string[] Rejections =
    [
        "The costs are missing from the report: the mandate asks for them.",
        "The report cites no run: attach the run it describes.",
        "The window is three months, not the six the mandate asks for.",
    ];

    public string Mandate(string unit, string decider, string holder, string attempt, DateTimeOffset at)
    {
        var id = "man-" + Hex(12);
        Exec("""
            INSERT INTO org_mandate(id, unit_id, decider, holder, kind, objective, scope, checks, latitude, deadline, allocation_ai, retry_limit, attempt_id, created_at)
            VALUES($id,$u,$d,$h,'task',$o,NULL,'[]','exact',$by,$al,3,$x,$at)
            """, ("$id", id), ("$u", unit), ("$d", decider), ("$h", holder), ("$o", Pick(Objectives)), ("$by", T(at.AddDays(3))),
            ("$al", D(4.00m)), ("$x", attempt), ("$at", T(at)));
        MandateEvent(id, at, MandateEventKind.Issued, decider, attempt, null);
        return id;
    }

    public void MandateEvent(string mandate, DateTimeOffset at, string kind, string actor, string? attempt, string? reason) =>
        Exec("INSERT INTO org_mandate_event(mandate_id, at, kind, actor, attempt_id, reason) VALUES($m,$at,$k,$a,$x,$r)",
            ("$m", mandate), ("$at", T(at)), ("$k", kind), ("$a", actor), ("$x", attempt), ("$r", reason));

    /// <summary>A delivery and its <c>delivered</c> event; a failed check is followed, as the app does, by its <c>retry</c>.</summary>
    public void Deliver(string mandate, string holder, string attempt, DateTimeOffset at, bool passes = true)
    {
        Tool(attempt, holder, at.AddSeconds(-5), ("mandate_deliver", "mandate-deliver"));
        var id = Hex(64);
        Exec("INSERT INTO org_delivery(id, mandate_id, author, attempt_id, report, deviations, refs, manifest, at) VALUES($id,$m,$h,$x,$r,'[]','[]','{}',$at)",
            ("$id", id), ("$m", mandate), ("$h", holder), ("$x", attempt), ("$r", Pick(Reports)), ("$at", T(at)));
        Exec("INSERT INTO org_check(delivery_id, check_name, passed, reason) VALUES($d,'report',$p,$r)",
            ("$d", id), ("$p", passes ? 1 : 0), ("$r", passes ? "the report is present" : "the report is empty"));
        MandateEvent(mandate, at, MandateEventKind.Delivered, holder, attempt, null);
        if (!passes) MandateEvent(mandate, at.AddSeconds(1), MandateEventKind.Retry, OrgStore.AppActor, null, "the report check failed");
    }

    public void Decide(string position, string attempt, DateTimeOffset at) =>
        Org.Decide(position, attempt, OrgDecisionKind.Routine, ["keep the current dataset", "add the second source"],
            Next() < 0.5 ? "keep the current dataset" : "add the second source",
            "the second source costs a day to wire and the gaps it closes are short", [], [], at);

    /// <summary>An escalation; <paramref name="to"/> null goes to the owner.</summary>
    public void Escalate(string sender, string unit, string? to, string? attempt, string? mandate, string reason, DateTimeOffset at) =>
        Exec("""
            INSERT INTO org_escalation(id, sender, attempt_id, unit_id, mandate_id, to_position, reason, skipped, wake_id, day, at)
            VALUES($id,$s,$x,$u,$m,$to,$r,'[]',$w,$day,$at)
            """, ("$id", "esc-" + Hex(12)), ("$s", sender), ("$x", attempt), ("$u", unit), ("$m", mandate), ("$to", to), ("$r", reason),
            // A wake id with no queued wake behind it: the demo raises nothing the AI could be launched for.
            ("$w", to is null ? null : "demo-wake-" + Hex(8)), ("$day", at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$at", T(at)));

    public void Lateral(string sender, string to, string? attempt, string text, DateTimeOffset at) =>
        Exec("INSERT INTO org_lateral(id, sender, attempt_id, to_position, text, refs, at) VALUES($id,$s,$x,$to,$t,'[]',$at)",
            ("$id", "lat-" + Hex(12)), ("$s", sender), ("$x", attempt), ("$to", to), ("$t", text), ("$at", T(at)));
}
