using System.Globalization;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// ONE FIGURE THE REPORT COULD NOT MEASURE, NAMED WHERE IT WOULD HAVE STOOD.
///
/// <para>The report never omits a number it could not work out and never prints a zero in its place:
/// it prints a dash and this sentence. <c>docs/COUNCIL.md</c> rule 10 — "every missing trading or
/// operating cost named, so an incomplete net figure can never read as a complete profitability
/// claim" — is the whole reason this type exists rather than a nullable field on its own.</para>
/// </summary>
public sealed record ReportGap(string Field, string Why);

/// <summary>Who this report is about, and exactly when it was taken. Nothing here is a range.</summary>
public sealed record ReportIdentity
{
    /// <summary>The owner's local calendar day, <c>yyyy-MM-dd</c>. What the file is named after.</summary>
    public required string Day { get; init; }

    /// <summary>What the day is bounded by — the two instants, on the offsets in force at each.</summary>
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>The zone the day was cut on, by name, so a reader in another one can place it.</summary>
    public required string Timezone { get; init; }

    /// <summary>
    /// THE ONE INSTANT EVERY FIGURE BELOW WAS READ AT. A report assembled from three reads a minute
    /// apart is a report whose sections can contradict each other; this says which moment it is.
    /// </summary>
    public required DateTimeOffset SnapshotAt { get; init; }

    public string? AppVersion { get; init; }
    public int? SchemaVersion { get; init; }
}

/// <summary>One role's state on the day. Absent figures are absent, never zero.</summary>
public sealed record ReportRole(string Role, string? Model, int? Turns, decimal? Spent,
    decimal? Reserved, decimal? Cap, string? Estimated);

/// <summary>Working, healthy idle, blocked or paused — with the coded reason and the next wake.</summary>
public sealed record ReportMission
{
    /// <summary>The loop's own word for what it is doing, or null where nothing reported one.</summary>
    public string? State { get; init; }

    /// <summary>The CODED reason, as the app names it — never a sentence composed here.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset? NextEligibleWake { get; init; }
    public IReadOnlyList<ReportRole> Roles { get; init; } = [];
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>Whether an order could be placed at all, and what stands in the way if not.</summary>
public sealed record ReportReadiness
{
    public string? Connector { get; init; }
    public bool? ConnectorIsPaper { get; init; }
    public string? Account { get; init; }
    public string? Mode { get; init; }
    public bool? ExecutionAvailable { get; init; }
    public string? ExecutionBlockedReason { get; init; }

    /// <summary>How old the newest price this installation has seen is, at the snapshot.</summary>
    public TimeSpan? DataAge { get; init; }

    /// <summary>
    /// HOW OLD THE FRESHEST BAR IN EACH DATASET IS, one line each, with fixture marked as fixture.
    ///
    /// <para>Beside <see cref="DataAge"/> and not instead of it, because they are two different
    /// facts: that one is the newest PRICE the platform has quoted, this is the newest BAR this
    /// installation holds, and a promoted strategy's <c>data_freshness</c> is about the second.
    /// Measured from the dataset's last bar and never from when it was collected — see
    /// <c>BarAge</c>, which is the one definition this and the Situation's data line share.</para>
    /// </summary>
    public IReadOnlyList<string> DataAges { get; init; } = [];

    /// <summary>
    /// WHETHER THE PROMOTED STRATEGY'S OWN FRESHNESS BOUND CAN BE MET BY THE BARS THIS INSTALLATION
    /// HOLDS, in words — or null because nothing is promoted.
    ///
    /// <para>The gate that refuses a stale decision is in the gateway and needs no report. This line
    /// is the OWNER's half of it: a strategy that is promoted, healthy and silent because every bar
    /// here is older than it will act on is otherwise indistinguishable from one that simply has not
    /// signalled, and nothing else on this page would say which.</para>
    /// </summary>
    public string? FreshnessBound { get; init; }

    public IReadOnlyList<string> MissingCapabilities { get; init; } = [];
    public IReadOnlyList<string> ActivationBlockers { get; init; } = [];
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// WHAT THE TRADING MADE OR LOST, AND WHAT THE FIGURE DOES NOT KNOW.
///
/// <para><see cref="Net"/> is null exactly when <see cref="PnlReport.Net"/> is — a fill whose fee the
/// platform never reported is not a fill that cost nothing — and <see cref="Missing"/> then names the
/// fee. The two travel together on purpose: a dash with no reason beside it is read as a rounding
/// artefact, and a net printed as though an unreported cost were zero is wrong in the owner's favour
/// every single time.</para>
/// </summary>
public sealed record ReportPerformance
{
    /// <summary>
    /// WHOSE MONEY THIS SECTION IS ABOUT — the platform and the account every figure below was
    /// computed over (<c>U-scope-identity</c>). Until this, the figures were a sum over every fill in
    /// the database whatever account or platform put it there, so a document that named no pair was
    /// naming no one's money (REVIEW 2026-09-16, finding 2).
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Fills of this account in the window that name no platform — written before schema 22. Printed
    /// BESIDE the figures and never in them.
    /// </summary>
    public int UnattributedFills { get; init; }

    public decimal? Exposure { get; init; }
    public decimal? Realized { get; init; }
    public decimal? Unrealized { get; init; }

    /// <summary>When the open positions were valued. Null when nothing valued them.</summary>
    public DateTimeOffset? ValuedAt { get; init; }

    public decimal? Fees { get; init; }
    public int? FeesUnknownFills { get; init; }
    public decimal? Net { get; init; }
    public decimal? Drawdown { get; init; }
    public int? Fills { get; init; }
    public decimal? LossAllowanceRemaining { get; init; }
    public decimal? LossBudgetDay { get; init; }

    /// <summary>
    /// SINCE WHEN THE DAY WAS CLOSED TO NEW RISK, off the closure record — null because it was open.
    /// Not derived from anything in this section: the figure here is the LEDGER's, and a day closed
    /// on an unrealised loss that was then realised smaller reads as under its budget while still
    /// being closed.
    /// </summary>
    public DateTimeOffset? DayClosedAt { get; init; }

    /// <summary>The words the closure was recorded with, or the reason the record could not be read.</summary>
    public string? DayClosedWhy { get; init; }

    /// <summary>Instruments closed to opens and adds for the rest of that UTC day.</summary>
    public IReadOnlyList<string> SymbolsClosed { get; init; } = [];

    /// <summary>
    /// What TradeAgent DID about the closure: <c>flat</c>, <c>unresolved</c>, or null because nothing
    /// was flattened. See <see cref="LossToday.FlattenState"/>; this report is composed from the
    /// gateway's own record and asks the platform nothing, which is why the state has to be a record
    /// rather than a reading.
    /// </summary>
    public string? FlattenState { get; init; }

    /// <summary>The flatten's own sentence, written once with its record.</summary>
    public string? FlattenWhy { get; init; }

    /// <summary>
    /// THE EARLIEST INSTANT A STANDING CLOSURE MAY BE LIFTED, or null because nothing is closed or
    /// because <see cref="ReopenHeld"/> names what is standing in the way. The report is the
    /// document an owner reads the morning after, so "when does this end" is the question it is most
    /// often opened to answer.
    /// </summary>
    public DateTimeOffset? ReopensAt { get; init; }

    /// <summary>What is holding a closure that has already served its time. Null while nothing is.</summary>
    public string? ReopenHeld { get; init; }

    /// <summary>When the last closure was lifted, or null. Null whenever anything is still closed.</summary>
    public DateTimeOffset? ReopenedAt { get; init; }

    /// <summary>The receipt's own sentence, written once with it.</summary>
    public string? ReopenedWhy { get; init; }

    /// <summary>
    /// EVERY CLOSURE OF THIS ACCOUNT INSIDE THE STRIKE WINDOW, one line each, oldest first: the
    /// scope, the instant the budget was reached, the RULE that episode was judged by, and how it
    /// ended — reopened by code, released by the owner with their note quoted, held for review, or
    /// still running.
    ///
    /// <para>A list rather than a figure, because a run of losing days is the thing the strike rule
    /// exists to notice and a count would hide exactly what the owner has to look at. The rule is on
    /// every line because the owner may have changed both numbers since, and what governs an episode
    /// is what was in force when it was recorded.</para>
    /// </summary>
    public IReadOnlyList<string> Closures { get; init; } = [];

    /// <summary>The hold's own sentence while a standing closure is held for review. Null otherwise.</summary>
    public string? HeldForReview { get; init; }

    /// <summary>The rule the standing closure is judged under, in words. Null while nothing is closed.</summary>
    public string? ClosureRule { get; init; }

    /// <summary>The bounded extension in force, naming its END. Null while there is none.</summary>
    public string? ExtendedWhy { get; init; }

    /// <summary>
    /// OPEN POSITIONS NOBODY CAN VALUE, and since when — one line each, empty as the honest none.
    /// The clock the data-loss exit runs on, put in front of the owner before it fires rather than
    /// after (<c>U-flatten-3</c>).
    /// </summary>
    public IReadOnlyList<string> ValuationLost { get; init; } = [];

    /// <summary>
    /// POSITIONS CLOSED TODAY UNDER <c>VALUATION_LOST</c>, in the exit record's own words. Empty is
    /// the honest none, and every sentence in here says that no budget was reached.
    /// </summary>
    public IReadOnlyList<string> ValuationExits { get; init; } = [];

    /// <summary>
    /// WHAT THE THRESHOLDS IN THIS SECTION DO AND WHAT THEY CANNOT PROMISE — printed WHATEVER the
    /// budgets are set to, and that is the point of it.
    ///
    /// <para>"Most it may lose in one day" reads as a bound on the loss. It is not one: it is the
    /// figure at which TradeAgent intervenes, and what the intervention is left holding is whatever
    /// a market order fills at after a gap, across whatever spread, less fees the platform may never
    /// have reported. The one reader who must see this is the owner who HAS set a budget and is
    /// relying on it, so printing it only for an installation with no budget — the mutant this
    /// unit's third test watches go red — would put the disclosure exactly where there is nothing to
    /// disclose.</para>
    /// </summary>
    public string? ThresholdDisclosure { get; init; }

    public string Currency { get; init; } = "";

    /// <summary>
    /// WHAT CAPITAL STANDS BEHIND EACH PROMOTED VERSION, one line each: the version, the ceiling, the
    /// currency, the instant it took effect, and the allocation policy that was applied.
    ///
    /// <para>Here, in the money section, because that is what an allocation is. Empty is "nothing is
    /// allocated", which is the truth on every installation that has not pressed the Capital card, and
    /// is printed as <c>none</c> rather than omitted — a silent section would read as "no allocations
    /// exist" and as "this build does not know" identically.</para>
    ///
    /// <para>An allocation whose promotion no longer stands is LISTED AND MARKED rather than filtered
    /// out. It is the line that most needs printing: the row is still on the table, the version can
    /// trade nothing, and a reader shown only the live ones would be told the owner's capital is
    /// somewhere it is not.</para>
    /// </summary>
    public IReadOnlyList<string> Allocations { get; init; } = [];

    /// <summary>
    /// WHAT TRADEAGENT PUT ON PAPER BY ITS OWN POLICY, LISTED APART FROM THE CAPITAL ABOVE.
    ///
    /// <para>A separate list rather than a flag on the same lines, because "this version has your
    /// money behind it" and "this version is being observed on a practice account TradeAgent asked you
    /// for once" are the two facts this product most needs never to blur — and a reader skimming one
    /// list would blur them however each line was worded. Every line says PAPER and says it carries no
    /// live authority.</para>
    ///
    /// <para>Empty is "TradeAgent has put nothing on paper", printed as <c>none</c> rather than
    /// omitted, for the reason <see cref="Allocations"/> is.</para>
    /// </summary>
    public IReadOnlyList<string> PaperAllocations { get; init; } = [];

    /// <summary>
    /// WHAT TRADEAGENT IS ACTUALLY RUNNING FORWARD, one line per deployment, beneath what it
    /// allocated.
    ///
    /// <para>An allocation is a ceiling and a deployment is a RUN, and the owner needs both: "this
    /// version may trade up to five" and "this version is trading right now, and one of its orders
    /// has no answer" are different facts about their account. Every line says PAPER and says it
    /// carries no live authority, for <see cref="PaperAllocations"/>'s reason.</para>
    ///
    /// <para>An ENDED run is listed rather than dropped whenever it still has an operation nobody can
    /// account for — that is the line that most needs printing, because it is an order that may be
    /// live at the platform. Empty is printed as <c>none</c> rather than omitted.</para>
    /// </summary>
    public IReadOnlyList<string> Deployments { get; init; } = [];

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>One operation the app cannot yet say the outcome of, and how long that has been true.</summary>
public sealed record ReportUnknown(string RequestId, string Instrument, DateTimeOffset Since, TimeSpan Age);

public sealed record ReportExecution
{
    public int? OpenRequests { get; init; }
    public int? Unreconciled { get; init; }
    public int? OrdersToday { get; init; }
    public int? FillsToday { get; init; }
    public int? RejectionsToday { get; init; }
    public IReadOnlyList<ReportUnknown> Unknown { get; init; } = [];
    public DateTimeOffset? LastFillPullAt { get; init; }
    public string? LastFillPullError { get; init; }
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// WHAT THE AI COST, WITH THE THREE FIGURES <c>docs/COUNCIL.md</c> RULE 4 KEEPS APART.
///
/// <para>"subscription charges, API charges and list-price equivalents are three figures, never one".
/// This build has only the third: every number here is a list-price equivalent computed by TradeAgent
/// from token counts, which is a calculation and not an invoice. <see cref="Basis"/> says so, in the
/// owner's words, on every report — so an advisory number can never be read as a cap that was
/// enforced by somebody's billing.</para>
/// </summary>
public sealed record ReportSpending
{
    public IReadOnlyList<ReportRole> Roles { get; init; } = [];
    public decimal? Spent { get; init; }
    public decimal? Reserved { get; init; }
    public decimal? Cap { get; init; }
    public int? Turns { get; init; }
    public int? UnpricedTurns { get; init; }

    /// <summary>
    /// Turns inside <see cref="Spent"/> charged their RESERVATION because their usage never arrived.
    /// On the report because rule 4 keeps enforcement apart from billing, and a total the owner
    /// cannot tell a worst case from is exactly the reading that rule forbids.
    /// </summary>
    public int? UnreportedTurns { get; init; }
    public string Currency { get; init; } = "";
    public string? Runtime { get; init; }

    /// <summary>
    /// WHETHER THE APP-OWNED HARNESS HAS A KEY — "held" or "not held", and never the key.
    ///
    /// On the report because it is the difference between a role that can work and a role that starts
    /// nothing, and it does not survive a restart: a day with no research turns and no reason beside
    /// them is a day the owner cannot account for. Null in a build with no harness.
    /// </summary>
    public string? HarnessKey { get; init; }

    /// <summary>Where these figures come from, and what they are not. Never null in a real report.</summary>
    public string? Basis { get; init; }

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// Everything it costs to run that is NOT the AI. The app still measures no money here — see
/// <see cref="Missing"/> — and one operating activity is now at least COUNTED.
/// </summary>
public sealed record ReportOtherCosts
{
    /// <summary>
    /// WHAT THE LIVE MARKET-DATA FEED DID TODAY, in one line, or null because this installation
    /// collects none.
    ///
    /// <para>It belongs in this section and not in section 8 because it is the one operating
    /// activity this app runs continuously and pays for in requests rather than in evidence: the
    /// gap this section declares names "data, market data subscriptions" among the costs nothing
    /// measures, and this is the part of it TradeAgent can actually count. The BARS are research
    /// evidence and are reported as such where the evidence is.</para>
    ///
    /// <para>It is a COUNT and never a price. TradeAgent does not know what this installation's
    /// bandwidth costs, and the gap below still says so.</para>
    /// </summary>
    public string? ForwardData { get; init; }

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// The evidence, with the app's own measurements kept apart from what an agent said about them —
/// round 4's "app-owned measurements and separately attributed judgments".
/// </summary>
public sealed record ReportResearch
{
    /// <summary>Facts the app computed from its own tables. Nothing an agent wrote is in here.</summary>
    public IReadOnlyList<string> AppMetrics { get; init; } = [];

    /// <summary>Publications, by role and revision. What was claimed, attributed, never merged above.</summary>
    public IReadOnlyList<string> AgentClaims { get; init; } = [];

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// ONE MESSAGE THE OWNER TYPED, WITH WHAT BECAME OF IT AND WHEN AN ANSWER WAS DUE.
///
/// <para>Round 4: "Owner text enters Operations' agenda first, with receipt, disposition and
/// deadline." <see cref="Disposition"/> is null while nothing has become of it yet, which is a
/// different fact from any of the recorded outcomes and is printed as one.
/// <see cref="DispositionDetail"/> is what that outcome points at — the publication the message was
/// delegated into, the later message that superseded it, the reason nothing could take it.</para>
///
/// <para><see cref="Overdue"/> is the app comparing two instants it wrote down itself. It costs no
/// turn and wakes nobody: an overdue message that bought a turn would let a backlog spend tomorrow's
/// allowance the moment the day turned over.</para>
/// </summary>
public sealed record ReportOwnerMessage(string Text, DateTimeOffset ReceivedAt, string? Disposition,
    string? DispositionDetail, DateTimeOffset DueBy, bool Overdue);

/// <summary>
/// ONE CONSEQUENTIAL BOUNDARY, WITH ITS EVIDENCE, ITS OWNER AND ITS DEADLINE.
///
/// <para><c>docs/COUNCIL.md</c> rule 10: "every decision is recorded with its evidence, its owner and
/// its deadline". The three are separate fields rather than one sentence because they are three
/// different facts about the same row and a reader checking one must not have to parse prose for it.</para>
///
/// <para><see cref="Owner"/> is who APPLIED the disposition and is <c>policy</c> for every row this
/// build can write — there is no method by which a director disposes a boundary. It is printed anyway,
/// because a column that can only say one thing today is what makes it visible on the day it says
/// something else.</para>
/// </summary>
public sealed record ReportBoundary(
    string Id, string Kind, string Entity, string Evidence, DateTimeOffset DeadlineAt,
    string? Disposition, string? Owner, DateTimeOffset? DisposedAt, bool Overdue);

public sealed record ReportDecisions
{
    public IReadOnlyList<ReportOwnerMessage> OwnerMessages { get; init; } = [];

    /// <summary>
    /// Every consequential boundary this installation has opened, newest first — the ones code has
    /// already answered and the ones still open with their deadlines. The word the doctrine uses for
    /// these is "decision", and this is the section that lists them.
    /// </summary>
    public IReadOnlyList<ReportBoundary> Boundaries { get; init; } = [];

    /// <summary>Limit changes and cap events, as the activity log recorded them on the day.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

public sealed record ReportRecovery
{
    /// <summary>Launches that were never closed — a killed turn nobody will report usage for.</summary>
    public IReadOnlyList<string> Interrupted { get; init; } = [];

    /// <summary>Publications committed whose copy into the recipient's folder has not been made.</summary>
    public IReadOnlyList<string> PendingHandoffs { get; init; } = [];

    public IReadOnlyList<string> Blockers { get; init; } = [];
    public DateTimeOffset? NextReviewAt { get; init; }

    /// <summary>Set when a review is pending because it was not funded, never because it was skipped.</summary>
    public string? ReviewNotFunded { get; init; }

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// THE OWNER'S DAILY REPORT, WRITTEN BY THE APP FROM WHAT IT MEASURED (<c>docs/COUNCIL.md</c> rule 10
/// and "The owner's report, every day, from the app, even when no agent ran").
///
/// <para><b>Nothing here is inferred and no paid turn produces it.</b> Every field is composed from a
/// table this app wrote or a read it took itself, at ONE instant
/// (<see cref="ReportIdentity.SnapshotAt"/>). There is no model call anywhere on this path: the
/// Operations Director's note is a separate, paid publication and appears beside the report rather
/// than inside it.</para>
///
/// <para><b>Every field is nullable and labelled, never defaulted.</b> A figure the app could not
/// work out is null and is named in that section's <c>Missing</c>. That is not a style: the report is
/// the document an owner decides on, and a zero standing in for an unknown is a claim this software
/// is not entitled to make — most sharply for <see cref="ReportPerformance.Net"/>, which is withheld
/// whenever a single fill's fee was never reported.</para>
/// </summary>
public sealed record DailyReport
{
    public required ReportIdentity Identity { get; init; }
    public ReportMission Mission { get; init; } = new();
    public ReportReadiness Readiness { get; init; } = new();
    public ReportPerformance Performance { get; init; } = new();
    public ReportExecution Execution { get; init; } = new();
    public ReportSpending Spending { get; init; } = new();
    public ReportOtherCosts OtherCosts { get; init; } = new();
    public ReportResearch Research { get; init; } = new();
    public ReportDecisions Decisions { get; init; } = new();
    public ReportRecovery Recovery { get; init; } = new();

    /// <summary>Every gap in the whole report, section by section, for a reader that wants the list.</summary>
    public IReadOnlyList<ReportGap> AllGaps =>
    [
        .. Mission.Missing, .. Readiness.Missing, .. Performance.Missing, .. Execution.Missing,
        .. Spending.Missing, .. OtherCosts.Missing, .. Research.Missing, .. Decisions.Missing,
        .. Recovery.Missing
    ];
}

/// <summary>
/// A RENDERED REPORT AND WHETHER THE APP ACCEPTED ITS OWN DRAFT.
///
/// <see cref="Rejected"/> is non-null when the draft ran past <see cref="DailyReportText.MaxLines"/>.
/// The text is then the refusal itself, so the day's file still exists and still says what happened —
/// <c>docs/COUNCIL.md</c>'s "an invalid publication is rejected and the last valid plan stands",
/// applied to the app's own output rather than only to an agent's.
/// </summary>
public sealed record DailyReportDraft(string Text, int Lines, string? Rejected);

/// <summary>
/// THE REPORT AS WORDS. Separated from everything that gathers it so the SENTENCES can be tested
/// without a gateway, a connector or a screen — the same reason <c>PerformanceCard.Words</c> is a
/// static function, and for the same kind of defect: "we do not know" is the part of this document
/// that matters and it is not something a screenshot proves.
/// </summary>
public static class DailyReportText
{
    /// <summary>
    /// The most lines the app will write for one day. A report nobody finishes reading is not a
    /// report, and the limit is enforced rather than requested — see <see cref="DailyReportDraft"/>.
    /// </summary>
    public const int MaxLines = 120;

    /// <summary>What stands where a figure could not be worked out. Never a zero, never blank.</summary>
    public const string Unknown = "—";

    /// <summary>The sentence at the top of every report, which is also the whole of its authority.</summary>
    public const string Preamble =
        "Written by TradeAgent from what it measured. Nothing in it is inferred, no figure is a "
        + "guess, and no AI turn was spent producing it. A dash is a figure TradeAgent could not "
        + "work out; the reason is named beside it.";

    public static DailyReportDraft Draft(DailyReport r)
    {
        var text = Render(r);
        var lines = Count(text);
        if (lines <= MaxLines) return new DailyReportDraft(text, lines, null);

        var why = $"TradeAgent's own draft of the report for {r.Identity.Day} came to {lines} lines, "
                  + $"which is past the {MaxLines} this report may be, so it was not kept. Nothing has "
                  + "been shortened or left out silently.";
        var refusal = string.Join('\n',
        [
            $"# TradeAgent — {r.Identity.Day}",
            "",
            why,
            ""
        ]);
        return new DailyReportDraft(refusal, Count(refusal), why);
    }

    static int Count(string text) => text.Replace("\r\n", "\n").Split('\n').Length;

    /// <summary>
    /// The ten sections, in the order <c>docs/COUNCIL.md</c> fixes them. LF endings on every
    /// platform: the file is read by a person, by a later build and by the AI, and a line ending that
    /// depends on which machine wrote it is a diff nobody asked for.
    /// </summary>
    public static string Render(DailyReport r)
    {
        var b = new StringBuilder();
        var id = r.Identity;

        Head(b, $"TradeAgent — {id.Day}");
        b.Append(Preamble).Append('\n').Append('\n');

        Section(b, "1. Identity");
        Kv(b, "day", $"{id.Day} ({id.Timezone})");
        Kv(b, "covers", $"{Instant(id.From)} to {Instant(id.To)}");
        Kv(b, "snapshot taken", Instant(id.SnapshotAt));
        Kv(b, "app version", id.AppVersion ?? Unknown);
        Kv(b, "database schema", id.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? Unknown);

        Section(b, "2. Mission state");
        Kv(b, "state", r.Mission.State ?? Unknown);
        Kv(b, "reason", r.Mission.Reason ?? Unknown);
        Kv(b, "next eligible wake", Instant(r.Mission.NextEligibleWake));
        foreach (var role in r.Mission.Roles)
            Kv(b, role.Role, $"model {role.Model ?? Unknown}, "
                             + $"{role.Turns?.ToString(CultureInfo.InvariantCulture) ?? Unknown} turns");
        Gaps(b, r.Mission.Missing);

        Section(b, "3. Trading readiness");
        Kv(b, "platform", r.Readiness.Connector is { } c
            ? c + (r.Readiness.ConnectorIsPaper is { } p ? p ? " (practice)" : " (real money)" : "")
            : Unknown);
        Kv(b, "account", r.Readiness.Account ?? Unknown);
        Kv(b, "mode", r.Readiness.Mode ?? Unknown);
        Kv(b, "orders allowed", r.Readiness.ExecutionAvailable switch
        {
            true => "yes",
            false => "no — " + (r.Readiness.ExecutionBlockedReason ?? Unknown),
            null => Unknown
        });
        Kv(b, "newest price", r.Readiness.DataAge is { } age ? Age(age) : Unknown);
        // THE BARS, BESIDE THE PRICE. Two facts and two lines: the price is what the platform last
        // quoted and these are the history this installation holds, and a promoted strategy's
        // declared freshness is about the second.
        List(b, "newest bars", r.Readiness.DataAges);
        Kv(b, "promoted strategy's data bound", r.Readiness.FreshnessBound ?? Unknown);
        List(b, "missing capabilities", r.Readiness.MissingCapabilities);
        List(b, "activation blockers", r.Readiness.ActivationBlockers);
        Gaps(b, r.Readiness.Missing);

        Section(b, "4. Capital and performance");
        var money = r.Performance.Currency;
        // WHOSE MONEY, FIRST. Every figure under this line is one account's on one platform, and a
        // figure that named no pair was the defect this line exists to close.
        Kv(b, "account", r.Performance.Scope ?? Unknown);
        if (r.Performance.UnattributedFills > 0)
            Kv(b, "unattributed fills", $"{r.Performance.UnattributedFills} — recorded before TradeAgent "
                                        + "stamped which platform a fill happened on, and not counted in "
                                        + "anything below");
        Kv(b, "exposure", Money(r.Performance.Exposure, money));
        Kv(b, "realised", Money(r.Performance.Realized, money));
        Kv(b, "unrealised", Money(r.Performance.Unrealized, money)
                            + (r.Performance.ValuedAt is { } v ? $" (valued {Instant(v)})" : ""));
        Kv(b, "fees reported", Money(r.Performance.Fees, money)
                               + (r.Performance.FeesUnknownFills is int n and > 0
                                   ? $" — {n} fill(s) carry no fee at all" : ""));
        // THE ONE FIGURE THIS DOCUMENT EXISTS TO GET RIGHT. Withheld whenever a cost is unknown, and
        // the reason for withholding it is printed by the Gaps line below, never omitted.
        Kv(b, "net after costs", Money(r.Performance.Net, money));
        Kv(b, "worst drop", Money(r.Performance.Drawdown, money));
        Kv(b, "fills", r.Performance.Fills?.ToString(CultureInfo.InvariantCulture) ?? Unknown);
        Kv(b, "loss allowance left", Money(r.Performance.LossAllowanceRemaining, money)
                                     + (r.Performance.LossBudgetDay is { } d
                                         ? $" of {Labels.Money(d, money)}" : " — no daily loss budget is set"));
        // WHETHER THE DAY WAS CLOSED, SINCE WHEN AND WHY. Printed even when the figure above it is
        // smaller than the budget, because that is exactly the case an owner would otherwise read as
        // "the software stopped trading for no reason".
        Kv(b, "closed to new risk", r.Performance.DayClosedAt is { } closed
            ? $"since {Instant(closed)} — {r.Performance.DayClosedWhy ?? Unknown}"
            : r.Performance.DayClosedWhy is { Length: > 0 } unreadable ? unreadable
            : "no — the day was open to new positions");
        if (r.Performance.SymbolsClosed.Count > 0)
            Kv(b, "positions closed to adds", string.Join(", ", r.Performance.SymbolsClosed));

        // WHEN IT LIFTS, OR WHAT IS STOPPING IT. Printed whenever anything is closed, because the
        // first thing an owner reading a closure wants is the end of it — and because since
        // U-reopen-1 the answer is a decision with conditions rather than the next midnight.
        if (r.Performance.DayClosedAt is not null || r.Performance.SymbolsClosed.Count > 0)
            Kv(b, "reopens", r.Performance.ReopenHeld is { Length: > 0 } stuck
                ? $"not yet — waiting on: {stuck}"
                : r.Performance.ReopensAt is { } when
                    ? $"by code, at the earliest {Instant(when)} — once TradeAgent can see your book is flat "
                      + "and everything it sent is accounted for. Nothing to press."
                    : Unknown);

        // HELD FOR REVIEW IS ITS OWN LINE, above the flatten and below the instant that is NOT being
        // shown: it is the one thing in the "waiting on" list that waiting does not fix, and an owner
        // who reads "not yet" without reading "this one is yours to release" waits for ever.
        if (r.Performance.HeldForReview is { Length: > 0 } review)
            Kv(b, "held for review", review);

        // AND THE RULE THE STANDING CLOSURE IS BEING JUDGED BY, because both numbers are the owner's
        // now and what governs an episode is what was in force when it was recorded.
        if (r.Performance.ClosureRule is { Length: > 0 } rule)
            Kv(b, "closure rule", rule);

        if (r.Performance.ExtendedWhy is { Length: > 0 } extended)
            Kv(b, "held open until", extended);

        // AND WHEN ONE WAS LIFTED, as its own line and in the receipt's own words. Only ever printed
        // while nothing is closed: see ReportPerformance.ReopenedAt.
        if (r.Performance.ReopenedAt is { } back)
            Kv(b, "reopened", $"{Instant(back)} — {r.Performance.ReopenedWhy ?? Unknown}");

        // EVERY CLOSURE IN THE WINDOW, WITH ITS RULE AND ITS OUTCOME. A run of losing days is what
        // the strike rule exists to notice, and it is what this document exists to put in front of a
        // person: a single "closed to new risk" line says nothing about the third one this week.
        List(b, "loss closures in the window", r.Performance.Closures);

        // AND WHAT WAS DONE ABOUT IT, AS ITS OWN LINE. A closed day and a flattened book are two
        // facts, and this is the one that says where the owner's money is. It is the flatten
        // record's own sentence, never a paraphrase and never derived from the figure above.
        if (r.Performance.FlattenState is { Length: > 0 } state)
            Kv(b, "positions closed for you", $"{state} — {r.Performance.FlattenWhy ?? Unknown}");

        // WHAT CANNOT BE VALUED, AND SINCE WHEN. It is beside the closure rather than in the health
        // section because it is about the owner's MONEY: a position nobody can value is a position
        // the budget above it is not bounding, and the figure in this section is silently missing it.
        List(b, "positions TradeAgent cannot value", r.Performance.ValuationLost);

        // AND WHAT WAS CLOSED FOR IT. Its own line, never folded into "positions closed for you":
        // that line is about a budget that was REACHED, and a reader who found a data-loss exit
        // under it would be told their money went when their prices did.
        List(b, "closed because nothing could value it", r.Performance.ValuationExits);

        // AND WHAT NONE OF THE NUMBERS ABOVE PROMISES. Last in the money section, after every figure
        // it qualifies, and printed whatever the budgets are — see ReportPerformance.ThresholdDisclosure.
        if (r.Performance.ThresholdDisclosure is { Length: > 0 } disclosure)
            Kv(b, "what these limits are and are not", disclosure);
        // WHAT IS ALLOCATED, AND TO WHAT. See ReportPerformance.Allocations: a withdrawn promotion is
        // listed and marked rather than dropped, because the row is still there and the version can
        // trade nothing.
        List(b, "allocated", r.Performance.Allocations);
        // AND WHAT THE APP PUT ON PAPER ITSELF, on its own line beneath. See
        // ReportPerformance.PaperAllocations: it is not capital and it must not be read as any.
        List(b, "on paper (no capital)", r.Performance.PaperAllocations);
        // AND WHAT IS ACTUALLY RUNNING, beneath what was allocated. See
        // ReportPerformance.Deployments: a ceiling and a run are different facts about the account.
        List(b, "running forward on paper", r.Performance.Deployments);
        Gaps(b, r.Performance.Missing);

        Section(b, "5. Execution health");
        Kv(b, "orders placed today", Whole(r.Execution.OrdersToday));
        Kv(b, "fills today", Whole(r.Execution.FillsToday));
        Kv(b, "rejections today", Whole(r.Execution.RejectionsToday));
        Kv(b, "open requests", Whole(r.Execution.OpenRequests));
        Kv(b, "awaiting reconciliation", Whole(r.Execution.Unreconciled));
        foreach (var u in r.Execution.Unknown)
            Kv(b, "UNKNOWN", $"{u.RequestId} on {u.Instrument}, {Age(u.Age)} since {Instant(u.Since)}");
        Kv(b, "last read of the platform's fills", r.Execution.LastFillPullAt is { } at
            ? Instant(at) + (r.Execution.LastFillPullError is { } e ? $" — FAILED: {e}" : "")
            : Unknown);
        Gaps(b, r.Execution.Missing);

        Section(b, "6. AI spending");
        Kv(b, "spent", Money(r.Spending.Spent, r.Spending.Currency));
        Kv(b, "reserved and unresolved", Money(r.Spending.Reserved, r.Spending.Currency));
        Kv(b, "daily ceiling", Money(r.Spending.Cap, r.Spending.Currency));
        Kv(b, "turns", Whole(r.Spending.Turns)
                       + (r.Spending.UnpricedTurns is int unpriced and > 0 ? $", {unpriced} of them unpriced" : "")
                       + (r.Spending.UnreportedTurns is int held and > 0
                           ? $", {held} charged at their reservation because no usage was ever reported" : ""));
        Kv(b, "runtime", r.Spending.Runtime ?? Unknown);
        if (r.Spending.HarnessKey is { Length: > 0 } key) Kv(b, "harness key", key);
        foreach (var role in r.Spending.Roles)
            Kv(b, role.Role, $"{Money(role.Spent, r.Spending.Currency)} spent, "
                             + $"{Money(role.Reserved, r.Spending.Currency)} committed, "
                             + $"of {Money(role.Cap, r.Spending.Currency)} on {role.Model ?? Unknown}"
                             + (role.Estimated is { } est ? $" — {est}" : ""));
        Kv(b, "basis", r.Spending.Basis ?? Unknown);
        Gaps(b, r.Spending.Missing);

        Section(b, "7. Other operating costs");
        Kv(b, "live market data", r.OtherCosts.ForwardData ?? "not collected");
        Gaps(b, r.OtherCosts.Missing);

        Section(b, "8. Research evidence");
        List(b, "measured by TradeAgent", r.Research.AppMetrics);
        List(b, "claimed by an agent", r.Research.AgentClaims);
        Gaps(b, r.Research.Missing);

        Section(b, "9. Decisions and changes");
        foreach (var m in r.Decisions.OwnerMessages)
            Kv(b, "you said", $"\"{OneLine(m.Text)}\" at {Instant(m.ReceivedAt)} — "
                              + $"{m.Disposition ?? "nothing yet"}"
                              + (m.DispositionDetail is { Length: > 0 } detail ? $" ({OneLine(detail)})" : "")
                              + $", due by {Instant(m.DueBy)}"
                              // OVERDUE IS SAID OUT LOUD. A message the owner is still waiting on,
                              // printed exactly like one that was answered, is a backlog nobody sees.
                              + (m.Overdue ? " — OVERDUE" : ""));
        // EVERY DISPOSITION WITH ITS EVIDENCE, ITS OWNER AND ITS DEADLINE (docs/COUNCIL.md rule 10).
        // A boundary still open is printed too, with the answer TradeAgent will write on its own: a
        // section that listed only what had been decided would hide exactly the ones a person can still
        // change their mind about.
        foreach (var boundary in r.Decisions.Boundaries)
            Kv(b, $"{boundary.Kind} of {boundary.Entity}",
                (boundary.Disposition is { Length: > 0 } settled
                    ? $"{settled.ToUpperInvariant()} by {boundary.Owner ?? "nobody recorded"} at "
                      + Instant(boundary.DisposedAt)
                    : "open")
                + $", due by {Instant(boundary.DeadlineAt)}"
                + (boundary.Overdue ? " — OVERDUE" : "")
                + $" — {OneLine(boundary.Evidence)}");

        List(b, "changes", r.Decisions.Changes);
        Gaps(b, r.Decisions.Missing);

        Section(b, "10. Recovery and next work");
        List(b, "interrupted", r.Recovery.Interrupted);
        List(b, "handoffs not yet delivered", r.Recovery.PendingHandoffs);
        List(b, "blockers", r.Recovery.Blockers);
        Kv(b, "next review", Instant(r.Recovery.NextReviewAt)
                             + (r.Recovery.ReviewNotFunded is { } why ? $" — {why}" : ""));
        Gaps(b, r.Recovery.Missing);

        return b.ToString();
    }

    static void Head(StringBuilder b, string text) => b.Append("# ").Append(text).Append('\n').Append('\n');

    static void Section(StringBuilder b, string title) =>
        b.Append("## ").Append(title).Append('\n');

    static void Kv(StringBuilder b, string key, string value) =>
        b.Append("- ").Append(key).Append(": ").Append(value).Append('\n');

    static void List(StringBuilder b, string key, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { Kv(b, key, "none"); return; }
        foreach (var item in items) Kv(b, key, OneLine(item));
    }

    /// <summary>
    /// THE GAPS, AND THEY ARE NOT OPTIONAL. Removing this line is what turns a withheld net into a
    /// dash nobody can account for, which reads as a complete figure that happened to be empty.
    /// </summary>
    static void Gaps(StringBuilder b, IReadOnlyList<ReportGap> gaps)
    {
        foreach (var g in gaps)
            b.Append("- missing (").Append(g.Field).Append("): ").Append(OneLine(g.Why)).Append('\n');
        b.Append('\n');
    }

    static string Whole(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? Unknown;

    /// <summary>An amount with its currency, or the dash. Never a zero standing in for an unknown.</summary>
    static string Money(decimal? v, string currency) => v is { } m ? Labels.Money(m, currency) : Unknown;

    static string Instant(DateTimeOffset? at) =>
        at is { } t ? t.ToString("yyyy-MM-dd HH:mm:ssK", CultureInfo.InvariantCulture) : Unknown;

    static string Age(TimeSpan age) =>
        age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s old"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m old"
        : $"{(int)age.TotalHours}h old";

    /// <summary>
    /// One line, because a line count is the limit and a message with newlines in it would let one
    /// entry silently spend the whole budget. The owner's own words are never truncated away — they
    /// are folded, and the fold is visible.
    /// </summary>
    static string OneLine(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
