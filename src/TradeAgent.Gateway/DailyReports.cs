using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// THE FACTS ONLY THE APP'S OWN LAYERS HOLD, handed to the composer instead of reached for.
///
/// <para>The mission loop and the turn meter live above the gateway on purpose — the gateway is the
/// execution authority and must not learn what a loop is — so what they know arrives here as values.
/// Everything else in a report is read by <see cref="DailyReports"/> itself, out of tables this app
/// wrote. Nothing on this record is optional-with-a-default: a caller that knows nothing leaves it
/// null and the report says the figure was not measured.</para>
/// </summary>
public sealed record DailyReportInputs
{
    /// <summary>What the loop says it is doing: <c>working</c>, <c>waiting</c>, <c>paused</c>, <c>stopped</c>.</summary>
    public string? MissionState { get; init; }

    /// <summary>The loop's own coded reason for that state, as it words it. Never composed here.</summary>
    public string? MissionReason { get; init; }

    public DateTimeOffset? NextEligibleWake { get; init; }

    /// <summary>
    /// The day's spending: the whole-installation reading first, then one per role. An empty list is
    /// "nothing metered this", which the report prints as unmeasured rather than as zero.
    /// </summary>
    public IReadOnlyList<AiSpendToday> Spending { get; init; } = [];

    /// <summary>The runtime the turns ran on, where anything named one.</summary>
    public string? Runtime { get; init; }

    /// <summary>
    /// WHETHER A KEY IS HELD FOR THE APP-OWNED HARNESS, or null in a host that has no harness at all.
    ///
    /// Handed in rather than read here for the reason every other input on this record is: the key lives
    /// in memory in the composition root, the report is composed in the gateway, and the gateway must
    /// never be able to reach a credential. Two words reach it; the key never does.
    /// </summary>
    public bool? HarnessKeyHeld { get; init; }

    /// <summary>When the next scheduled look is due, and why it may not happen.</summary>
    public DateTimeOffset? NextReviewAt { get; init; }

    public string? ReviewNotFunded { get; init; }
}

/// <summary>
/// THE OWNER'S DAILY REPORT: COMPOSED FROM ONE SNAPSHOT, WRITTEN TO A FILE THE APP OWNS.
///
/// <para><b>No inference, and no paid turn.</b> Every figure is read out of a table this app wrote —
/// the fill ledger, the request records, the launch ledger, the wake queue, the relay's publications,
/// the dataset ledger — or off settings the owner set. There is no model call on this path and no
/// wake is raised by it: <c>docs/COUNCIL.md</c> rule 10 says the app generates the daily factual
/// report itself, and the Operations Director's note is a separate, paid publication that appears
/// BESIDE it. A report that woke the chair would cost a turn every day for a document the app had
/// already finished writing.</para>
///
/// <para><b>No connector call either</b>, which is why <c>trade report</c> can sit in the handler
/// table at <see cref="TimeSpan.Zero"/>. The price of that is real and is stated rather than hidden:
/// what is still OPEN is not valued here, and the report names that gap in the section where the
/// figure would have stood. <c>trade pnl</c> and the Dashboard do ask the platform.</para>
///
/// <para><b>The day is the owner's local day</b>, cut at the offsets in force at each boundary, so a
/// daylight-saving day is twenty-three or twenty-five hours long rather than silently wrong. The
/// same choice <c>TurnMeter</c> makes about the spending day, for the same reason: the owner's
/// midnight is the one that means anything to them.</para>
/// </summary>
public sealed class DailyReports(TradingGateway gateway, Database db, Func<DateTimeOffset>? now = null)
{
    readonly MissionEventStore _events = new(db);
    readonly PublicationStore _publications = new(db);
    readonly Promotions _promotions = new(db);
    readonly CouncilBoundaries _boundaries = new(db);
    readonly AiAttemptStore _attempts = new(db);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>The most owner messages one report prints in full before it counts the rest.</summary>
    public const int MessagesShown = 12;

    /// <summary>The most entries any other list in the report prints before it counts the rest.</summary>
    public const int ListShown = 6;

    /// <summary>Where a day's report is kept. App-owned: no verb and no pipe op writes in here.</summary>
    public static string FileFor(string day) => Path.Combine(Paths.Reports, $"{day}.md");

    /// <summary>The owner's local day <paramref name="at"/> falls in, as a half-open instant window.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) LocalDay(DateTimeOffset at)
    {
        var start = at.ToLocalTime().Date;
        var next = start.AddDays(1);
        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next)));
    }

    public static string DayName(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// THE WHOLE REPORT FOR ONE DAY, AT ONE INSTANT. <paramref name="at"/> is both the snapshot time
    /// and the day it selects; a caller writing yesterday's report at 00:01 passes an instant inside
    /// yesterday, and every window below moves with it.
    /// </summary>
    public DailyReport Compose(DateTimeOffset at, DailyReportInputs? inputs = null)
    {
        var i = inputs ?? new DailyReportInputs();
        var (from, to) = LocalDay(at);

        return new DailyReport
        {
            Identity = new ReportIdentity
            {
                Day = DayName(at),
                From = from,
                To = to,
                Timezone = TimeZoneInfo.Local.Id,
                SnapshotAt = at,
                AppVersion = Versions.App,
                SchemaVersion = Versions.DatabaseSchemaVersion
            },
            Mission = ComposeMission(i),
            Readiness = ComposeReadiness(at),
            Performance = ComposePerformance(from, at),
            Execution = ComposeExecution(from, to, at),
            Spending = ComposeSpending(i),
            OtherCosts = ComposeOtherCosts(from, to, at),
            Research = ComposeResearch(from, to),
            Decisions = ComposeDecisions(from, to, at, gateway.Settings.OwnerReplyDeadlineHours),
            Recovery = ComposeRecovery(from, to, i)
        };
    }

    /// <summary>
    /// Writes the day's report and answers what was written. An over-long draft is REFUSED by
    /// <see cref="DailyReportText.Draft"/> and the refusal is what lands on disk, so a day never has
    /// a file that quietly leaves things out — see <see cref="DailyReportDraft.Rejected"/>.
    ///
    /// It records one activity line and RAISES NOTHING. There is deliberately no mission event here:
    /// every row in that queue is a reason to spend the owner's money on a turn, and a report the app
    /// wrote by itself is not one.
    /// </summary>
    public DailyReportDraft Write(DateTimeOffset at, DailyReportInputs? inputs = null)
    {
        var report = Compose(at, inputs);
        var draft = DailyReportText.Draft(report);
        var path = FileFor(report.Identity.Day);

        Directory.CreateDirectory(Paths.Reports);
        File.WriteAllText(path, draft.Text);

        gateway.Log.Activity(draft.Rejected is null
            ? $"Daily report for {report.Identity.Day} written"
            : $"Daily report for {report.Identity.Day}: {draft.Rejected}",
            draft.Rejected is null ? "info" : "warn");

        return draft;
    }

    /// <summary>Today's report, written now, whoever asked. The "Write it now" press and the op.</summary>
    public DailyReportDraft WriteNow(DailyReportInputs? inputs = null) => Write(_now(), inputs);

    /// <summary>
    /// THE DAYS THAT HAVE PASSED WITH NO REPORT WRITTEN FOR THEM, oldest first — what the background
    /// loop asks on every tick so that "written at local midnight" survives a machine that was off at
    /// midnight, which is the ordinary case for a laptop.
    ///
    /// Today is never in it: the day is not over, and a file written now would be overwritten at the
    /// next midnight pass anyway. Bounded by <paramref name="lookBackDays"/> so a machine that was off
    /// for a year does not write a year of reports about nothing.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Owed(DateTimeOffset at, int lookBackDays = 7)
    {
        var owed = new List<DateTimeOffset>();
        for (var back = lookBackDays; back >= 1; back--)
        {
            var day = at.AddDays(-back);
            if (!File.Exists(FileFor(DayName(day)))) owed.Add(day);
        }
        return owed;
    }

    /// <summary>Every day a report exists for, newest first. What the page's picker lists.</summary>
    public IReadOnlyList<string> Days()
    {
        try
        {
            if (!Directory.Exists(Paths.Reports)) return [];
            return [.. Directory.EnumerateFiles(Paths.Reports, "*.md")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(d => d is { Length: 10 })
                .Select(d => d!)
                .OrderByDescending(d => d, StringComparer.Ordinal)];
        }
        catch (Exception) { return []; }
    }

    /// <summary>One day's report as it was written, or null when that day has none.</summary>
    public string? Read(string day)
    {
        try
        {
            var path = FileFor(day);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// THE OPERATIONS DIRECTOR'S NOTE FOR ONE DAY, or null — which is the ordinary answer, because a
    /// note is paid for and is written only when an app predicate changed.
    ///
    /// <para>It is looked up, never composed, and it is looked up BY DAY AND BY KIND. Both filters
    /// are the guard: a page that showed the newest note whatever day it belonged to would present
    /// last week's commentary as today's, and one that did not check the kind would show the chair's
    /// agenda to Research as its note on the owner's report. Absent is shown as absent.</para>
    /// </summary>
    public Publication? NoteFor(string day)
    {
        if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return null;

        var (from, to) = LocalDay(new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed)));
        try
        {
            return _publications.By(CouncilRoles.Operations)
                .LastOrDefault(p => p.Kind == PublicationKind.Note
                                    && p.CreatedAt >= from && p.CreatedAt < to);
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- the sections

    ReportMission ComposeMission(DailyReportInputs i)
    {
        var gaps = new List<ReportGap>();
        if (i.MissionState is null)
            gaps.Add(new ReportGap("state", "no mission loop reported to this report, so what the AI "
                                            + "was doing was not measured"));

        return new ReportMission
        {
            State = i.MissionState,
            Reason = i.MissionReason,
            NextEligibleWake = i.NextEligibleWake ?? SafeNextDue(),
            Roles = [.. i.Spending.Where(s => s.Role is not null).Select(RoleOf)],
            Missing = gaps
        };
    }

    ReportReadiness ComposeReadiness(DateTimeOffset at)
    {
        var settings = gateway.Settings;
        var available = gateway.TryAuthorizeExecution(AgentContext.Operator, out var blocked);
        var caps = gateway.Connector.Capabilities;

        var missingCaps = new List<string>();
        if (!caps.SupportsClientOrderId)
            missingCaps.Add("this platform cannot carry an order id of TradeAgent's own, so fully "
                            + "automatic live trading is refused");
        if (!caps.SupportsOrderHistory)
            missingCaps.Add("this platform cannot prove its order history back to a timestamp, so "
                            + "\"this order does not exist\" is never provable here");

        var blockers = new List<string>();
        if (settings.AiTradingStopped) blockers.Add("you have stopped AI trading");
        if (!settings.LiveActivated) blockers.Add("real-money trading has not been switched on");

        var newest = SafeNewestQuote();
        var gaps = new List<ReportGap>();
        if (newest is null)
            gaps.Add(new ReportGap("newest price", "TradeAgent has not seen a price on this run"));

        // HOW OLD THE BARS ARE, PER DATASET, AND WHETHER A PROMOTED STRATEGY COULD ACT ON THEM.
        //
        // `docs/COUNCIL.md`:14-15 puts freshness among the gates code enforces, and the gate itself
        // is in the gateway (`TradingGateway.RefuseAStaleDecisionOrThrow`). This is the owner's half:
        // a promoted strategy that is healthy and silent because every bar here is older than it will
        // act on looks exactly like one that has not signalled, and nothing else on this page would
        // tell the two apart.
        var ages = new List<string>();
        TimeSpan? freshest = null;
        try
        {
            foreach (var set in gateway.Datasets.All())
            {
                ages.Add(BarAge.Line(set, at));

                // The bound is judged against MARKET data only. A fixture establishes plumbing and is
                // never evidence (`EvaluationClass`), so a fixture written a minute ago must not be
                // able to make a stale installation read as one a strategy could trade on.
                if (set.State != DatasetState.ACCEPTED
                    || string.Equals(set.EvaluationClass, Core.Data.EvaluationClass.Fixture, StringComparison.Ordinal))
                    continue;

                if (BarAge.Of(set, at) is { } age && (freshest is null || age < freshest)) freshest = age;
            }
        }
        catch (Exception ex) { gaps.Add(new ReportGap("newest bars", $"the dataset ledger could not be read ({ex.Message})")); }

        return new ReportReadiness
        {
            DataAges = Cap(ages, ListShown, "dataset"),
            FreshnessBound = FreshnessBoundLine(freshest, gaps),
            Connector = gateway.Connector.DisplayName,
            ConnectorIsPaper = caps.IsPaper,
            Account = settings.SelectedAccountId,
            Mode = settings.Mode.ToString(),
            ExecutionAvailable = available,
            ExecutionBlockedReason = blocked,
            DataAge = newest is { } q ? at - q : null,
            MissingCapabilities = missingCaps,
            ActivationBlockers = blockers,
            Missing = gaps
        };
    }

    /// <summary>
    /// THE MONEY SECTION, AND THE ONE RULE IT IS BUILT AROUND.
    ///
    /// <see cref="ReportPerformance.Net"/> is <see cref="PnlReport.Net"/>, which is null whenever a
    /// fill in the window carries no fee — and every sentence <c>Pnl</c> put in <c>incomplete</c>
    /// becomes a named gap here. The pair is the guarantee: a withheld figure and the reason it was
    /// withheld are written together, so an incomplete net can never read as a complete one.
    /// </summary>
    ReportPerformance ComposePerformance(DateTimeOffset from, DateTimeOffset at)
    {
        PnlReport? pnl = null;
        var gaps = new List<ReportGap>();
        try { pnl = gateway.LedgerPnl(from, "today"); }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("everything in this section",
                $"the fill ledger could not be read ({ex.Message})"));
        }

        if (pnl is not null)
            foreach (var line in Cap(pnl.Incomplete, ListShown, "gap"))
                gaps.Add(new ReportGap("net after costs", line));

        // WHAT A REPORT THAT TOUCHES NO PLATFORM CANNOT KNOW, said where the figure would have been.
        gaps.Add(new ReportGap("exposure, unrealised, loss allowance left",
            "this report is written from TradeAgent's own ledger and asks your platform nothing, so "
            + "what is still OPEN is not valued in it; the Dashboard and 'trade pnl' do ask"));

        // WHAT CAPITAL STANDS BEHIND A PROMOTED VERSION. Read at the snapshot instant, like everything
        // else in this document, and a ledger that cannot be read becomes a named gap rather than an
        // empty list — "nothing is allocated" and "TradeAgent could not look" are different facts and
        // this section is the one place they must not collapse into each other.
        var allocations = new List<string>();
        var paper = new List<string>();
        try
        {
            foreach (var standing in gateway.Allocations.Standing(at))
                allocations.Add(AllocationLine(standing));

            // AND WHAT THE APP PUT ON PAPER ITSELF, read at the same instant and kept in its own list:
            // it is not capital, it authorises nothing in a real-money mode, and a reader skimming one
            // list would take it for an allocation however the line was worded.
            foreach (var standing in gateway.Allocations.PaperStanding(at))
                paper.Add(PaperAllocationLine(standing));
        }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("allocated",
                $"the allocation ledger could not be read ({ex.Message}), so what capital stands behind "
                + "a promoted version is not in this report"));
        }

        var budget = gateway.Settings.Risk.MaxDailyLoss;
        // OFF THE RECORD, LIKE EVERY OTHER SURFACE. It costs no platform call — the closure is a row
        // this app wrote — so it belongs in a report that asks the platform nothing.
        var closed = gateway.ClosureToday();
        var flattened = gateway.FlattenStateToday();
        var reopen = gateway.ReopenReading();
        var valuation = gateway.ValuationReading();
        return new ReportPerformance
        {
            Scope = pnl is { Account.Length: > 0 }
                ? $"{pnl.Account} at {pnl.Connector}"
                : null,
            UnattributedFills = pnl?.UnattributedFills ?? 0,
            ValuationLost = Cap(valuation.Lost, ListShown, "position"),
            ValuationExits = Cap(valuation.Exits, ListShown, "exit"),

            // PRINTED WHATEVER THE BUDGETS ARE. The owner relying on one is the reader who needs it;
            // an installation with none has nothing to be misled about. See the field's own summary.
            ThresholdDisclosure = gateway.ThresholdDisclosure(),
            FlattenState = flattened.State,
            FlattenWhy = flattened.Why,
            ReopensAt = reopen.At,
            ReopenHeld = reopen.Held,
            ReopenedAt = reopen.ReopenedAt,
            ReopenedWhy = reopen.ReopenedWhy,
            HeldForReview = reopen.HeldForReview,
            ClosureRule = reopen.Rule,
            ExtendedWhy = reopen.Extended,
            Closures = Cap(gateway.ClosureHistory(), ListShown, "closure"),
            Allocations = Cap(allocations, ListShown, "allocation"),
            PaperAllocations = Cap(paper, ListShown, "paper allocation"),
            DayClosedAt = closed.At,
            DayClosedWhy = closed.Why,
            SymbolsClosed = closed.Symbols,
            Exposure = null,
            Realized = pnl?.Realized,
            Unrealized = null,
            ValuedAt = null,
            Fees = pnl?.Fees,
            FeesUnknownFills = pnl?.FeesUnknownFills,
            Net = pnl?.Net,
            Drawdown = pnl?.MaxDrawdown,
            Fills = pnl?.Fills,
            LossAllowanceRemaining = null,
            LossBudgetDay = budget > 0m ? budget : null,
            Currency = gateway.AccountCurrency,
            Missing = gaps
        };
    }

    /// <summary>
    /// ONE STANDING ALLOCATION, in the owner's words: the version, what it may hold, in which currency,
    /// from when, and under which allocation policy.
    ///
    /// <para>The policy version is on the line rather than implied, for the reason a promotion's
    /// scoring-policy sha is on its row: what the app applied when it wrote the allocation is part of
    /// what the allocation MEANS, and a reader a release later has no other way to know which rules
    /// were in force.</para>
    ///
    /// <para>A version whose promotion no longer stands is marked HERE rather than filtered upstream.
    /// The mutant is printing the line without the mark: the row is still on the table and the version
    /// may trade nothing, so an unmarked line tells the owner their capital is working when it is not.</para>
    ///
    /// <para><b>And a promotion carrying none of the three execution bounds is marked too.</b>
    /// `U-promote-bounds` stopped the referee promoting such a version, and deliberately did not
    /// withdraw the ones recorded before it (see <c>docs/CONTRACTS.md</c>) — so what is left is capital
    /// standing behind a version whose decisions the gate at dispatch cannot judge the age of. That is
    /// the opposite of the WITHDRAWN case and has to read as its opposite: the allocation IS live, and
    /// the thing the owner cannot rely on is the staleness refusal rather than the permission.</para>
    /// </summary>
    internal static string AllocationLine(AllocationStanding standing)
    {
        var a = standing.Allocation;
        var line = $"{Short(a.VersionId)} up to {AllocationRow.Num(a.MaxQuantity)}"
                   + (a.MaxNotional is { } n and > 0m ? $" and {Labels.Money(n, a.Currency)}" : "")
                   + $" {a.Currency}, from {a.EffectiveFrom:yyyy-MM-dd HH:mm:ssK}, policy {a.PolicyVersion}";

        if (!standing.Authorises)
            return line + " — WITHDRAWN: its promotion no longer stands, so it may trade nothing. "
                        + standing.Promotion.Why;

        return standing.Promotion.Promotion is { Freshness: null }
            ? line + " — NO EXECUTION BOUNDS: its promotion declares no `timeframe`, no "
                   + "`data_freshness` and no `max_decision_age`, so the dispatch gate cannot judge how "
                   + "stale anything this version decides is and would send every order it decided. "
                   + "TradeAgent no longer promotes a version without the three; this promotion was "
                   + "recorded before that and still stands."
            : line;
    }

    /// <summary>
    /// ONE PAPER ALLOCATION, AND EVERY LINE SAYS WHAT IT IS NOT.
    ///
    /// <para>The version, the instrument, the account, the ceiling the OWNER declared on the envelope
    /// card, and then the words "PAPER — no live authority" on every line without exception. A figure
    /// from the held-back months never appears here, exactly as it never appears beside a promotion:
    /// the ceiling is the owner's own number from their own screen.</para>
    ///
    /// <para>A row whose GRANT no longer stands is listed and MARKED rather than filtered out — the
    /// <c>WITHDRAWN</c> reading <see cref="AllocationLine"/> takes of a promotion, applied to the
    /// envelope: the row is still on the table and the experiment is over.</para>
    /// </summary>
    internal static string PaperAllocationLine(AllocationStanding standing)
    {
        var a = standing.Allocation;
        var line = $"{Short(a.VersionId)} up to {AllocationRow.Num(a.MaxQuantity)}"
                   + (a.MaxNotional is { } n and > 0m ? $" and {Labels.Money(n, a.Currency)}" : "")
                   + $" on account {a.AccountId} at {a.ConnectorId}, from "
                   + $"{a.EffectiveFrom:yyyy-MM-dd HH:mm:ssK} — PAPER — no live authority";

        if (standing.Envelope is null)
            return line + ", and WITHDRAWN: the paper envelope it was written under no longer stands, "
                        + "so it may trade nothing at all.";

        return standing.Authorises
            ? line
            : line + ", and WITHDRAWN: the verdict under it no longer stands. " + standing.Promotion.Why;
    }

    ReportExecution ComposeExecution(DateTimeOffset from, DateTimeOffset to, DateTimeOffset at)
    {
        var gaps = new List<ReportGap>();
        List<ExecutionRequest> requests;
        try { requests = gateway.Requests.Query(); }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("orders", $"the request records could not be read ({ex.Message})"));
            requests = [];
        }

        var today = requests.Where(r => r.CreatedAt >= from && r.CreatedAt < to).ToList();
        var unknown = requests
            .Where(r => r.State == ExecutionState.UNKNOWN || r.NeedsReconciliation)
            .OrderBy(r => r.CreatedAt)
            .Take(ListShown)
            .Select(r => new ReportUnknown(r.RequestId, r.Instrument, r.CreatedAt, at - r.CreatedAt))
            .ToList();

        var pull = gateway.LastPull();
        return new ReportExecution
        {
            OpenRequests = SafeCount(() => gateway.Requests.Open().Count),
            Unreconciled = SafeCount(() => gateway.Unreconciled().Count),
            OrdersToday = today.Count,
            // THE OPERATING PAIR'S OWN, like every other figure in this document: a count taken over
            // every row in the table counts another account's day as this one's (`U-scope-identity`).
            FillsToday = SafeCount(() => gateway.ScopedFills(from).Fills.Count(f => f.At >= from && f.At < to)),
            RejectionsToday = today.Count(r => r.State == ExecutionState.REJECTED),
            Unknown = unknown,
            LastFillPullAt = pull?.At,
            LastFillPullError = pull is { Ok: false } ? pull.Error : null,
            Missing = gaps
        };
    }

    ReportSpending ComposeSpending(DailyReportInputs i)
    {
        var whole = i.Spending.FirstOrDefault(s => s.Role is null);
        var gaps = new List<ReportGap>();

        if (whole is null || !whole.Metered)
            gaps.Add(new ReportGap("spent, reserved, turns",
                "nothing is metering this installation's AI turns, so what they cost was not measured"));
        else if (!whole.CanPrice)
            gaps.Add(new ReportGap("spent",
                whole.WhyNoPrice ?? "this installation cannot price a turn, so the figure is a floor"));

        return new ReportSpending
        {
            Roles = [.. i.Spending.Where(s => s.Role is not null).Select(RoleOf)],
            Spent = whole is { Metered: true, CanPrice: true } ? whole.Spent : null,
            Reserved = whole is { Metered: true } ? whole.Reserved : null,
            Cap = whole is { Metered: true } ? whole.Cap : null,
            Turns = whole is { Metered: true } ? whole.Turns : null,
            UnpricedTurns = whole is { Metered: true } ? whole.UnpricedTurns : null,
            UnreportedTurns = whole is { Metered: true } ? whole.UnreportedTurns : null,
            Currency = whole?.Currency ?? "",
            Runtime = i.Runtime,
            HarnessKey = i.HarnessKeyHeld is { } held ? Labels.HarnessKeyLine(held) : null,
            // RULE 4, SAID EVERY DAY. Three figures, never one — and this build has only the third.
            Basis = "every AI figure here is a LIST-PRICE EQUIVALENT TradeAgent calculated from token "
                    + "counts. It is not an invoice, it is not a subscription charge, and it is not an "
                    + "API charge your provider has billed.",
            Missing = gaps
        };
    }

    /// <summary>A hash as it is shown in a report: the first twelve characters, or all of a short id.</summary>
    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>One research run, with the figures the app computed from its own trace.</summary>
    static string RunLine(StrategyRunRow run) =>
        $"backtest {Short(run.Id)} of version {Short(run.VersionId)} over dataset {run.DatasetId} "
        + $"({run.ExecutionModel}): {run.Bars} bars, {run.Trades} trades, net {Figure(run.NetPnl)}, "
        + $"worst drawdown {Figure(run.MaxDrawdown)}, {run.Outcome}";

    /// <summary>
    /// ONE HOLDOUT RUN, NAMED AND NOT VALUED — the one run in this ledger whose figures this document
    /// may not carry.
    ///
    /// <para>`trade report` serves this report to the AI over the agent pipe. A holdout run's net result
    /// and drawdown are computed over the months the research process is never shown, so printing them
    /// here would hand back through the report exactly what `data-bars` and `backtest` refuse
    /// (<c>docs/COUNCIL.md</c>:196-197, and :212 — a leaked holdout cannot become unseen). The owner's
    /// answer is the verdict beneath it, which is the thing that was worth knowing; the figures are in
    /// the app's own <c>strategy_run</c> table.</para>
    /// </summary>
    static string HoldoutRunLine(StrategyRunRow run) =>
        $"holdout run {Short(run.Id)} of version {Short(run.VersionId)} over dataset {run.DatasetId} "
        + $"({run.ExecutionModel}), {run.Outcome} — TradeAgent's referee ran it over the months held "
        + "back from research, and its figures are not printed in this report, which is served to the "
        + "AI as well as to you";

    /// <summary>A figure, or the report's own dash. A null is an UNKNOWN here as it is everywhere else.</summary>
    static string Figure(decimal? value) =>
        value is { } d ? d.ToString(CultureInfo.InvariantCulture) : DailyReportText.Unknown;

    /// <summary>
    /// THE ONE OPERATING ACTIVITY THIS APP CAN COUNT — the live market-data feed — and the gap that
    /// still says nothing here is priced.
    ///
    /// <para>Counted off the forward ledger's own rows: the attempts made in the day's window, how
    /// many of them recorded a failure, the bars the series holds and how stale its newest one is.
    /// Never off "the collector is running": a collector running happily against a vendor publishing
    /// nothing is exactly what this line must not report as healthy.</para>
    ///
    /// <para>Null — printed as "not collected" — when this installation has neither a bar nor an
    /// attempt, which is what an owner who switched the toggle off has. A read that fails leaves it
    /// null too and adds the reason as a gap, never a zero: a zero here would read as a day on which
    /// the feed worked and published nothing.</para>
    /// </summary>
    ReportOtherCosts ComposeOtherCosts(DateTimeOffset from, DateTimeOffset to, DateTimeOffset at)
    {
        string? forward = null;
        var gaps = new List<ReportGap>
        {
            new("data, market data subscriptions, platform fees, machine",
                "TradeAgent does not measure any operating cost other than the AI, so no figure here "
                + "is a complete cost of running this")
        };

        try
        {
            var symbol = gateway.Settings.MarketDataPair;
            var series = gateway.Forward.Series(symbol);
            var (attempts, failed) = gateway.Forward.Fetches(symbol, from, to);

            if (series.Bars > 0 || attempts > 0)
            {
                var age = gateway.Forward.Freshness(symbol, at);
                forward =
                    $"{series.Source} {symbol} {series.Interval}: {attempts:N0} requests today"
                    + (failed > 0 ? $", {failed:N0} of them recorded a failure" : "")
                    + $", {series.Bars:N0} bars held, newest "
                    + (age is { } old ? $"{old.TotalMinutes:N0} min old" : "none yet")
                    + $", {series.Gaps:N0} gap runs / {series.BarsMissing:N0} minutes missing and NOTHING "
                    + "filled in"
                    + (gateway.Settings.CollectLiveBars ? "" : " — collection is switched OFF")
                    + (series.LastError is { Length: > 0 } why ? $" — last look: {why.ReplaceLineEndings(" ")}" : "")
                    // THE SENTENCE TRAVELS WITH THE FIGURE. `trade report` serves this document to the
                    // AI as well as to the owner, and a bar count with no caveat beside it is a claim
                    // about evidence neither of them can check.
                    + $" ({ForwardBars.Evidence}). No price: TradeAgent does not know what this "
                    + "installation's bandwidth costs.";
            }
        }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("live market data",
                $"the forward ledger could not be read ({ex.Message})"));
        }

        return new ReportOtherCosts { ForwardData = forward, Missing = gaps };
    }

    ReportResearch ComposeResearch(DateTimeOffset from, DateTimeOffset to)
    {
        var metrics = new List<string>();
        var claims = new List<string>();
        var gaps = new List<ReportGap>();

        try
        {
            // THE VENUE AND THE INSTRUMENT ARE ON THE LINE, because a bar count without them says
            // what was measured and not what it was measured ON. A dataset that recorded neither says
            // so in words: the owner reading this is the person who would have to put it right, and a
            // line that silently omitted the fact would leave the runner's refusal unexplained.
            foreach (var set in gateway.Datasets.All())
                metrics.Add($"{set.Source} {set.Pair} {set.Interval} v{set.Version} on "
                            + $"{set.VenueId ?? "no venue recorded"}/"
                            + $"{set.InstrumentSymbol ?? "no instrument recorded"}: {set.Bars} bars, "
                            + $"{set.MonthsPresent} of {set.MonthsAttempted} periods, "
                            + $"{set.CoverageActualDays} of {set.CoverageTargetDays} days deep, "
                            + $"{set.Gaps} gaps, "
                            // MIDPOINT-DERIVED BARS ARE NAMED ON THE LINE, not left to the bar count.
                            // `trade report` serves this document to the AI as well as to the owner,
                            // and a bar count with no midpoint count beside it is a claim about depth
                            // neither of them can check (docs/COUNCIL.md:164-172).
                            + (set.MidpointBars > 0
                                ? $"{set.MidpointBars} bars with NO traded volume (midpoint-derived, "
                                  + "never trade evidence), "
                                : set.SourceCarriesVolume ? "" : "0 midpoint-derived bars, ")
                            + $"{set.State}");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("datasets", $"the dataset ledger could not be read ({ex.Message})")); }

        // WHAT THIS BUILD HAS MEASURED ABOUT A STRATEGY, and it goes in "measured by TradeAgent"
        // rather than in "claimed by an agent" because the app computed every figure in it from its
        // own trace. An agent's own account of a backtest is a publication and stays on the other
        // side of that line, where it can be compared with this rather than merged into it.
        var runs = 0;
        try
        {
            runs = gateway.Strategies.RunCount;
            foreach (var run in gateway.Strategies.Runs(ListShown))
                metrics.Add(run.Role == Core.Strategy.Referee.RunRole ? HoldoutRunLine(run) : RunLine(run));
        }
        catch (Exception ex) { gaps.Add(new ReportGap("backtests", $"the strategy ledger could not be read ({ex.Message})")); }

        // EVERY VERDICT, AND WHETHER IT STILL STANDS. It goes in "measured by TradeAgent" because the
        // app computed all of it — the run, the figures and the clauses applied to them — and no agent's
        // opinion is anywhere near it (docs/COUNCIL.md:55-57, the referee is code). The REASON is the
        // promotion row's closed vocabulary, so this line cannot print a figure from the held-back
        // months even by accident, which matters because `trade report` serves this document to the AI.
        var verdicts = 0;
        try
        {
            verdicts = _promotions.Count;
            foreach (var p in _promotions.All(ListShown))
            {
                var standing = _promotions.Standing(p.VersionId);
                var withdrawn = standing.Promotion?.Id == p.Id && standing.State == PromotionState.Invalidated;

                metrics.Add($"{p.Verdict.ToUpperInvariant()} version {Short(p.VersionId)} at "
                            + $"{p.At.UtcDateTime:yyyy-MM-dd HH:mm}Z under campaign {p.CampaignId}, on holdout run "
                            + $"{Short(p.HoldoutRunId)}: {PromotionReason.Words(p.Reason)}"
                            + (withdrawn ? $" — INVALIDATED since: {standing.Why}" : ""));
            }
        }
        catch (Exception ex) { gaps.Add(new ReportGap("verdicts", $"the promotion ledger could not be read ({ex.Message})")); }

        // AND THE DIRECTORS' OWN RECORD, which goes in "measured by TradeAgent" for the same reason the
        // verdicts do: every part of it was frozen by the app — the recommendation and the forecast when
        // the assessment was sealed, the disposition and the measurement when code settled the boundary
        // — and no director's account of its own performance is anywhere near it
        // (`docs/COUNCIL.md`:225, "the directors' own recommendations, forecasts and timeliness are
        // recorded against declared baselines"). A director that submitted nothing is listed too:
        // silence is part of a timeliness record, and a list of only the assessments that arrived would
        // be a record of the diligent.
        try
        {
            foreach (var record in _boundaries.Records(ListShown))
                metrics.Add(record.Line());
        }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("director records",
                $"the boundary ledger could not be read ({ex.Message})"));
        }

        try
        {
            foreach (var role in CouncilRoles.All)
                foreach (var p in _publications.By(role).Where(p => p.CreatedAt >= from && p.CreatedAt < to))
                    claims.Add($"{CouncilRoles.Title(p.Role)} published {p.Kind} #{p.Revision} ({p.Id[..12]})");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("publications", $"the relay's tables could not be read ({ex.Message})")); }

        // THE GAP LINE GOES WHEN A RUN EXISTS, AND NOT WHEN A DATASET DOES. It used to fire on
        // `metrics.Count == 0`, which the dataset lines above already satisfied: an installation that
        // had collected twelve months and backtested nothing read as though it had evidence. Having
        // the data is not having measured anything with it.
        if (runs == 0 && verdicts == 0)
            gaps.Add(new ReportGap("evaluation evidence",
                "nothing has been backtested or promoted by this build, so there is no measured "
                + "evidence for or against any strategy"));

        return new ReportResearch
        {
            // "measured line" rather than "dataset": this list has held datasets, backtests and now
            // verdicts since long before the noun was last read, and a report that says "3 more
            // dataset(s) not listed" while hiding a refusal is a report that miscounts what it withheld.
            AppMetrics = Cap(metrics, ListShown, "measured line"),
            AgentClaims = Cap(claims, ListShown, "publication"),
            Missing = gaps
        };
    }

    /// <summary>
    /// EVERY MESSAGE THE OWNER TYPED ON THE DAY, WITH ITS DISPOSITION AND ITS DEADLINE.
    ///
    /// <para>Round 4: "Owner text enters Operations' agenda first, with receipt, disposition and
    /// deadline." The deadline is <c>TradeAgentSettings.OwnerReplyDeadlineHours</c> from the moment
    /// the message was received, and OVERDUE is this method comparing that instant with the snapshot.
    /// It is a LINE, not a wake: an overdue message must never be what buys a turn.</para>
    ///
    /// <para>The rows are read, never composed — the wake queue is the only copy of what the owner
    /// typed.</para>
    /// </summary>
    ReportDecisions ComposeDecisions(DateTimeOffset from, DateTimeOffset to, DateTimeOffset at,
        int deadlineHours)
    {
        var gaps = new List<ReportGap>();
        var messages = new List<ReportOwnerMessage>();
        var deadline = TimeSpan.FromHours(deadlineHours > 0 ? deadlineHours : 24);

        try
        {
            foreach (var e in _events.OfKind(MissionEventKind.Owner))
            {
                var said = e.Payload is { Length: > 0 } p ? Json.Read<MissionOwnerMessage>(p) : null;
                var received = said?.ReceivedAt ?? e.CreatedAt;
                var due = received + deadline;

                // THE DEADLINE CHECK, AND ITS DIRECTION IS THE GUARD. A message nothing has settled,
                // at or past its due instant, is overdue; inverting this comparison hides exactly the
                // messages the owner is still waiting on and shows the ones nobody is waiting for.
                var overdue = e.Disposition is null && at >= due;

                // THE DAY'S MESSAGES, AND ANY OLDER ONE STILL OWED AN ANSWER. Without the second
                // clause a message sent yesterday can never be reported overdue at all: a deadline of
                // a day or more expires outside the window of the report that listed it, so the
                // backlog would be visible on exactly the day it was not yet late.
                if (!(e.CreatedAt >= from && e.CreatedAt < to) && !overdue) continue;

                messages.Add(new ReportOwnerMessage(
                    said?.Text ?? "(this message's text was not recorded)",
                    received, e.Disposition, e.DispositionDetail, due, overdue));
            }

            messages = [.. messages.OrderBy(m => m.ReceivedAt)];
        }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("your messages", $"the wake queue could not be read ({ex.Message})"));
        }

        // EVERY CONSEQUENTIAL BOUNDARY, AND WHAT BECAME OF IT. Read from the ledger and never composed:
        // the disposition, its author and the deadline are all on the row, written by code at the moment
        // each was decided, and a report that restated them from anything else would be a second account
        // of a decision the app already recorded.
        var boundaries = new List<ReportBoundary>();
        try
        {
            boundaries.AddRange(_boundaries.All(ListShown).Select(b => new ReportBoundary(
                b.Id, b.Kind, b.Entity, b.Evidence, b.DeadlineAt, b.Disposition, b.DisposedBy,
                b.DisposedAt, b.IsOpen && at >= b.DeadlineAt)));
        }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("decisions", $"the boundary ledger could not be read ({ex.Message})"));
        }

        var changes = new List<string>();
        try
        {
            changes.AddRange(gateway.Log.RecentActivity(400)
                .Where(a => a.At >= from && a.At < to && a.Level == "warn")
                .Select(a => a.Text));
        }
        catch (Exception ex) { gaps.Add(new ReportGap("changes", $"the activity log could not be read ({ex.Message})")); }

        return new ReportDecisions
        {
            OwnerMessages = messages.Count <= MessagesShown
                ? messages
                : [.. messages.Take(MessagesShown)],
            Boundaries = boundaries,
            Changes = Cap(changes, ListShown, "change"),
            Missing = messages.Count > MessagesShown
                ? [.. gaps, new ReportGap("your messages",
                    $"{messages.Count - MessagesShown} more message(s) were received on this day and "
                    + "are not listed above")]
                : gaps
        };
    }

    ReportRecovery ComposeRecovery(DateTimeOffset from, DateTimeOffset to, DailyReportInputs i)
    {
        var gaps = new List<ReportGap>();
        var interrupted = new List<string>();
        var pending = new List<string>();

        try
        {
            // ENDED-WITH-NO-USAGE IS ON THIS LIST TOO. It is the same unresolved commitment as a
            // LOST one — the app never heard what the turn used and charged the reservation — and
            // leaving it off would make "reserved and unresolved" above read as the whole of it.
            foreach (var a in _attempts.Between(from, to)
                         .Where(a => a.State is AiAttemptState.LAUNCHED or AiAttemptState.LOST
                                     || a.UnpricedReason == AiAttemptStore.UnreportedReason))
                interrupted.Add($"{CouncilRoles.Title(CouncilRoles.Or(a.Role))} attempt {a.Id[..Math.Min(a.Id.Length, 20)]} "
                                + $"is {a.State} — its reservation stands as its cost");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("interrupted attempts", $"the launch ledger could not be read ({ex.Message})")); }

        try
        {
            foreach (var d in _publications.Undelivered())
                pending.Add($"{d.PublicationId[..12]} to {CouncilRoles.Title(d.Recipient)}, committed {d.CreatedAt:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("handoffs", $"the relay's tables could not be read ({ex.Message})")); }

        var blockers = new List<string>();
        if (!gateway.TryAuthorizeExecution(AgentContext.Operator, out var why) && why is { Length: > 0 })
            blockers.Add(why);

        return new ReportRecovery
        {
            Interrupted = Cap(interrupted, ListShown, "attempt"),
            PendingHandoffs = Cap(pending, ListShown, "handoff"),
            Blockers = blockers,
            NextReviewAt = i.NextReviewAt,
            ReviewNotFunded = i.ReviewNotFunded,
            Missing = gaps
        };
    }

    // ---------------------------------------------------------------- small readers

    static ReportRole RoleOf(AiSpendToday s) => new(
        CouncilRoles.Title(s.Role!), s.Model, s.Turns,
        s.CanPrice ? s.RoleSpent : null, s.RoleReserved, s.RoleCap, s.Estimated);

    DateTimeOffset? SafeNextDue()
    {
        try { return _events.NextDueAt(); }
        catch (Exception) { return null; }
    }

    DateTimeOffset? SafeNewestQuote()
    {
        try { return gateway.Fills.Newest(); }
        catch (Exception) { return null; }
    }

    static int? SafeCount(Func<int> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The first <paramref name="take"/> entries, and a COUNT of what is left rather than silence.
    /// A list quietly cut short is the one thing a report about completeness may not do.
    /// </summary>
    /// <summary>
    /// WHETHER THE PROMOTED STRATEGY'S OWN <c>data_freshness</c> COULD BE MET RIGHT NOW, in words —
    /// or null because nothing stands promoted.
    ///
    /// <para>Four answers, and they are four because reading any of them as another is a wrong
    /// conclusion about whether the software is working: nothing promoted; promoted but declaring no
    /// bound at all (the shape of every version frozen before schema 19, and a real state rather than
    /// a generous default); promoted with a bound the freshest market bars meet; and promoted with a
    /// bound nothing here meets, which is a strategy that will refuse every order it decides and is
    /// the case this line exists for.</para>
    ///
    /// <para>The FRESHEST market-data bar in the installation is what the bound is measured against,
    /// and not the dataset the version was promoted over: the holdout is by construction history, and
    /// a bound measured against it would read as unsatisfiable on a machine that is collecting
    /// perfectly current bars. A dataset of the wrong instrument still counts here, because a
    /// promotion row names no instrument and re-parsing a version's source to learn one is work this
    /// page should not be doing (`Promotions.Standing` takes the same reading).</para>
    /// </summary>
    string? FreshnessBoundLine(TimeSpan? freshest, List<ReportGap> gaps)
    {
        PromotionStanding? standing;
        try { standing = _promotions.Current(); }
        catch (Exception ex)
        {
            gaps.Add(new ReportGap("promoted strategy's data bound",
                $"the promotion ledger could not be read ({ex.Message})"));
            return null;
        }

        if (standing is not { IsPromoted: true, Promotion: { } promotion }) return null;

        // SHORTENED THE WAY EVERY OTHER READER SHORTENS AN ID, and never by a slice that assumes a
        // length: this runs over a row read back from the database, and a page the owner reads must
        // not be able to throw over the spelling of an id.
        var version = promotion.VersionId.Length <= 12 ? promotion.VersionId : promotion.VersionId[..12];

        if (promotion.Freshness is not { } bounds)
            return $"version {version} is promoted and declares no data freshness at all, "
                   + "so TradeAgent has no bound to check its signals against";

        var declared = $"version {version} needs bars no older than "
                       + $"{BarAge.Words(bounds.DataFreshness)}";

        if (freshest is not { } age)
            return declared + ", and this installation holds no market bars at all — nothing it decides "
                            + "would be sent";

        return age <= bounds.DataFreshness
            ? $"{declared}, and the freshest here are {BarAge.Words(age)} old: satisfiable now"
            : $"{declared}, and the freshest here are {BarAge.Words(age)} old: NOT satisfiable — every "
              + "signal it decides would be refused as stale and nothing would be sent";
    }

    static List<string> Cap(IReadOnlyList<string> items, int take, string what)
    {
        if (items.Count <= take) return [.. items];
        return [.. items.Take(take), $"… and {items.Count - take} more {what}(s) not listed here"];
    }
}
