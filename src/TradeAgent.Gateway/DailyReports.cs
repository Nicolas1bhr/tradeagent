using System.Globalization;
using TradeAgent.Core;
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
            OtherCosts = ComposeOtherCosts(),
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

        return new ReportReadiness
        {
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

        var budget = gateway.Settings.Risk.MaxDailyLoss;
        return new ReportPerformance
        {
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
            FillsToday = SafeCount(() => gateway.Fills.Since(from).Count(f => f.At < to)),
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
            Currency = whole?.Currency ?? "",
            Runtime = i.Runtime,
            // RULE 4, SAID EVERY DAY. Three figures, never one — and this build has only the third.
            Basis = "every AI figure here is a LIST-PRICE EQUIVALENT TradeAgent calculated from token "
                    + "counts. It is not an invoice, it is not a subscription charge, and it is not an "
                    + "API charge your provider has billed.",
            Missing = gaps
        };
    }

    static ReportOtherCosts ComposeOtherCosts() => new()
    {
        Missing =
        [
            new ReportGap("data, market data subscriptions, platform fees, machine",
                "TradeAgent does not measure any operating cost other than the AI, so no figure here "
                + "is a complete cost of running this")
        ]
    };

    ReportResearch ComposeResearch(DateTimeOffset from, DateTimeOffset to)
    {
        var metrics = new List<string>();
        var claims = new List<string>();
        var gaps = new List<ReportGap>();

        try
        {
            foreach (var set in gateway.Datasets.All())
                metrics.Add($"{set.Source} {set.Pair} {set.Interval} v{set.Version}: {set.Bars} bars, "
                            + $"{set.MonthsPresent} of {set.MonthsAttempted} months, {set.Gaps} gaps, "
                            + $"{set.State}");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("datasets", $"the dataset ledger could not be read ({ex.Message})")); }

        try
        {
            foreach (var role in CouncilRoles.All)
                foreach (var p in _publications.By(role).Where(p => p.CreatedAt >= from && p.CreatedAt < to))
                    claims.Add($"{CouncilRoles.Title(p.Role)} published {p.Kind} #{p.Revision} ({p.Id[..12]})");
        }
        catch (Exception ex) { gaps.Add(new ReportGap("publications", $"the relay's tables could not be read ({ex.Message})")); }

        if (metrics.Count == 0)
            gaps.Add(new ReportGap("evaluation evidence",
                "nothing has been backtested or promoted by this build, so there is no measured "
                + "evidence for or against any strategy"));

        return new ReportResearch
        {
            AppMetrics = Cap(metrics, ListShown, "dataset"),
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
            foreach (var a in _attempts.Between(from, to)
                         .Where(a => a.State is AiAttemptState.LAUNCHED or AiAttemptState.LOST))
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
    static List<string> Cap(IReadOnlyList<string> items, int take, string what)
    {
        if (items.Count <= take) return [.. items];
        return [.. items.Take(take), $"… and {items.Count - take} more {what}(s) not listed here"];
    }
}
