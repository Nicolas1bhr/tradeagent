using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>Where one launch of the agent CLI has got to. See <see cref="AiAttemptStore"/>.</summary>
public enum AiAttemptState
{
    /// <summary>The process was started and nothing has reported how it ended. Its reservation stands.</summary>
    LAUNCHED,

    /// <summary>It ended and the app saw it end. <c>Cost</c> is what the usage came to, or null.</summary>
    ENDED,

    /// <summary>
    /// A meter opened this database while the row was still LAUNCHED, so the process that owned it
    /// is gone and nobody will ever report its usage. The reservation becomes the cost.
    /// </summary>
    LOST
}

/// <summary>
/// ONE LAUNCH OF THE AGENT CLI, as the app recorded it before and after.
///
/// <see cref="ReservedCost"/> is committed BEFORE the process starts and <see cref="Cost"/> is what
/// it turned out to be. They are two different numbers on purpose and neither replaces the other: a
/// row whose cost is still null is an allowance that has been spent as far as the ceiling is
/// concerned, because the vendor has already been asked to do the work.
/// </summary>
public sealed record AiAttempt
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The runtime id the turn ran on, e.g. <c>codex</c>. Null when nothing named one.</summary>
    public string? Runtime { get; init; }

    /// <summary>The model TradeAgent PUT ON THE COMMAND LINE, or null where it named none.</summary>
    public string? RequestedModel { get; init; }

    /// <summary>
    /// Where the rate came from: <c>owner</c> for the two numbers on the Safety page, or the day a
    /// list price was read and the page it was read from. A figure whose basis is not recorded is a
    /// figure nobody can check later, and these go stale on the vendor's schedule.
    /// </summary>
    public string? PricingBasis { get; init; }

    public decimal ReservedCost { get; init; }
    public AiAttemptState State { get; init; } = AiAttemptState.LAUNCHED;

    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }

    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }

    /// <summary>The model the RUNTIME'S OWN STREAM named, or null when it named none.</summary>
    public string? EffectiveModel { get; init; }

    public decimal? Cost { get; init; }

    /// <summary>Why <see cref="Cost"/> is absent. Present exactly when it is.</summary>
    public string? UnpricedReason { get; init; }

    /// <summary>What the app could observe about the turn's context, as JSON. See <c>TurnContext</c>.</summary>
    public string? Context { get; init; }

    /// <summary>The grant-policy revision in force at launch. See <see cref="Versions.GrantPolicyVersion"/>.</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>SHA-256, lower-case hex, of the prompt the app wrote. Never the prompt itself.</summary>
    public string? InputHash { get; init; }

    /// <summary>
    /// WHICH COUNCIL ROLE WAS CHARGED FOR THIS LAUNCH. Null on every row written before the council
    /// existed, and read as the chair's — the single agent it replaces was Operations, and a launch
    /// attributed to nobody is a bill nobody can allocate.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// THE RUNTIME PROFILE THIS LAUNCH RAN UNDER (<c>U-seat-home</c>, rung 37): one SHA-256 over the pinned program, the seat's deny
    /// set, its config file and the CLI's own feature and MCP report, verified before the launch was admitted. Written once, in
    /// this row's insert, and never updated. Null on a row from before the rung and on a launch with no seat profile.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// THE UNITS THAT FUND THIS LAUNCH (<c>U-org-envelopes</c>, rung 38): the position's own unit first, then every unit above
    /// it to the root, as the chart stood when the launch was admitted. Written by <see cref="AiAttemptStore.Begin"/> as
    /// <c>ai_attempt_funding</c> rows in the attempt's own transaction and never moved afterwards — funding lineage is immutable
    /// (<c>docs/ORCHESTRATION.md</c> § 2.4). Null or empty for a launch no unit funds (an app principal). Not read back onto
    /// this record: <see cref="AiAttemptStore.FundingOf"/> reads the rows.
    /// </summary>
    public IReadOnlyList<string>? Funding { get; init; }

    /// <summary>
    /// THE SEAT THIS LAUNCH WAS VERIFIED ON (<c>U-thinking-seats-a</c>, rung 39): its class (<see cref="SeatClasses"/>) and its
    /// assurance (<see cref="SeatClasses.Name"/>), as the admission established them before this row existed. Written once, in this
    /// row's insert, and never updated. Null on a row from before the rung and on a launch whose conversation declared no seat.
    /// </summary>
    public string? SeatClass { get; init; }

    /// <inheritdoc cref="SeatClass"/>
    public string? Assurance { get; init; }

    /// <summary>
    /// THE MANDATE THIS LAUNCH WAS ADMITTED UNDER (<c>U-org-assignments-a</c>, rung 40), or null: the one its consumed
    /// <c>mandate</c> wake names, after the mandate's own allocation gate in <see cref="AiAttemptStore.Begin"/> — written in the
    /// row's own insert and never updated. Its spend is the mandate's (<see cref="OrgStore.SpendOf"/>).
    /// </summary>
    public string? MandateId { get; init; }

    /// <summary>
    /// THE ALLOWANCE THIS LAUNCH WAS RESERVED AT, AND WHETHER ITS TURN IS HELD TO IT (<c>U-org-seats</c>, rung 44): input and output tokens
    /// as the reservation priced them, and <c>enforced</c> (the app's harness withholds any request that could pass them) or
    /// <c>reservation</c> (a vendor's program: the reservation is the only bound, and an overrun is recorded and charged). Composed by the
    /// app from the seat it resolved before this row existed, written in the row's own insert, and never updated — refused by trigger.
    /// Null on a row from before the rung and on a row no admission wrote (the owner's unmetered turn's bill).
    /// </summary>
    public long? AllowIn { get; init; }

    /// <inheritdoc cref="AllowIn"/>
    public long? AllowOut { get; init; }

    /// <inheritdoc cref="AllowIn"/>
    public string? TurnBound { get; init; }

    /// <summary>
    /// WHO PAYS FOR THIS LAUNCH (<c>U-billing-classes</c>, the rung): <see cref="BillingClasses.Plan"/>, <see cref="BillingClasses.Key"/> or
    /// <see cref="BillingClasses.Unknown"/>, composed by the app from the seat verdict its admission read before this row existed — never
    /// an agent's word — written in the row's own insert and never updated (refused by trigger). It decides which ceiling the row counts
    /// against; an unknown row counts against both. A row from before the rung reads <c>unknown</c>: nobody recorded it.
    /// </summary>
    public string BillingClass { get; init; } = BillingClasses.Unknown;

    /// <summary>
    /// WHAT THIS ROW COSTS THE DAY IT STARTED ON. A row still LAUNCHED costs its reservation,
    /// because the work has been asked for; a LOST one costs it for good.
    /// </summary>
    public decimal? Charge => State == AiAttemptState.LAUNCHED ? ReservedCost : Cost;
}

/// <summary>
/// Today's totals for the AI's spending, and the daily ceiling's ruler.
///
/// <paramref name="Reserved"/> is separate from <paramref name="Spent"/> so the loop can add the
/// turn it is about to take: the admission rule is <c>spent + reserved + this turn ≤ cap</c>, which
/// is a cap, where <c>spent ≥ cap</c> alone is only a check on money already gone.
/// </summary>
/// <param name="Unreported">
/// Resolved turns charged their RESERVATION because nothing ever said what they used — a turn the
/// app saw end with no usage event, and a turn a restart declared LOST. Their money is in
/// <paramref name="Spent"/>, and this is what stops that total reading as a bill.
/// </param>
/// <param name="UnreportedCharge">
/// THE MONEY OF THOSE TURNS (<c>U-verbs-a-followups</c>): the reservations charged as their cost because nothing measured them — the
/// part of <paramref name="Spent"/> that is no bill. Summed over the same rows as <paramref name="Spent"/>, so
/// <c>Spent − UnreportedCharge</c> is what was priced from a measurement, and nothing else is.
/// </param>
public sealed record AiAttemptTotals(
    decimal Spent, decimal Reserved, int Turns, int Unpriced, int Estimated, int Open,
    int Unreported = 0, decimal UnreportedCharge = 0m);

/// <summary>
/// ONE GROUP OF RESOLVED ATTEMPTS IN A WINDOW (<see cref="AiAttemptStore.SessionProfile"/>, <c>U-org-packets-a</c>): a position, the
/// kind of launch — <c>mandate</c> where it served one, <c>own</c> where it served none — and the session it ran in, <c>fresh</c>,
/// <c>resumed</c> or <c>unknown</c> where its record says neither. <paramref name="Unreported"/> attempts reported no usage and are
/// counted apart: they are in no median, and are never read as a zero. Each median is the lower middle of the figures the runtime
/// reported, so every figure here is one a turn actually reported — never an estimate, and never bytes turned into tokens.
/// </summary>
public sealed record SessionProfileRow(
    string Role, string Kind, string Session, int Attempts, int Unreported,
    long? MedianInput, long? MedianCached, long? MedianUncached, long? MedianOutput);

/// <summary>
/// THE CEILING A LAUNCH IS ADMITTED AGAINST, handed to <see cref="AiAttemptStore.Begin"/> so that
/// the comparison is made INSIDE the transaction that writes the reservation.
///
/// It carries everything the arithmetic needs EXCEPT the day's totals, and that omission is the
/// whole design: the totals are the one part that another launch can change between a caller's look
/// and this launch's commit, so they are read where nothing can move them. A rule that carried a
/// reading would be the defect this unit closes, wearing a different name.
///
/// <see cref="Metered"/> false is a host with no price list at all: every launch is admitted, which
/// is what <see cref="AiSpendToday.AdmitsGlobally"/> has always answered for an unmetered build.
/// </summary>
public sealed record AiAdmissionRule
{
    /// <summary>The owner's local day, as the half-open window a row's <c>started_at</c> is in.</summary>
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    /// <summary>The role being charged, or null for a launch attributed to nobody.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// WHO PAYS FOR THIS LAUNCH (<c>U-billing-classes</c>), the class its row records — and so which ceilings it is admitted against
    /// (<see cref="BillingClasses.CeilingsOf"/>): a <c>key</c> launch the money cap, <see cref="Cap"/>, over the day's <c>key</c> and
    /// <c>unknown</c> rows; a <c>plan</c> launch the plan's capacity, <see cref="PlanCap"/>, over the day's <c>plan</c> and <c>unknown</c>
    /// rows, and never the money cap; an <c>unknown</c> one both. Required: a rule that named no class would be a guess at one.
    /// </summary>
    public required string BillingClass { get; init; }

    /// <summary>
    /// THE OWNER'S REAL-MONEY CEILING FOR THE WHOLE DAY (<see cref="TradeAgentSettings.AiDailyCostCap"/>), across every role — what a
    /// <c>key</c> or <c>unknown</c> launch must fit, over the day's <c>key</c> and <c>unknown</c> rows.
    /// </summary>
    public decimal Cap { get; init; }

    /// <summary>
    /// THE OWNER'S PLAN-CAPACITY CEILING FOR THE WHOLE DAY (<c>U-billing-classes</c>): a list-price equivalent of what his AI plan's turns
    /// would cost, never money — what a <c>plan</c> or <c>unknown</c> launch must fit, over the day's <c>plan</c> and <c>unknown</c> rows.
    /// Read in the same admission as <see cref="Cap"/>, once. Zero admits no plan turn.
    /// </summary>
    public decimal PlanCap { get; init; }

    /// <summary>
    /// That role's slice of it. Meaningless, and unread, when <see cref="Role"/> is null — and unread when <see cref="Funding"/>
    /// is set, whose envelopes replace it.
    /// </summary>
    public decimal RoleCap { get; init; }

    /// <summary>
    /// THE UNITS THAT FUND THIS LAUNCH, nearest first and ending at the root, each with the envelope it admits against
    /// (<c>U-org-envelopes</c>, <c>docs/ORCHESTRATION.md</c> § 2.4): resolved from the chart before the transaction, and the
    /// launch is admitted only if every unit's spent + unresolved reservations + this reservation fit its envelope — the
    /// totals read INSIDE the transaction, through the chains recorded with each row, so two siblings cannot both take a
    /// parent's last room. Ids and envelopes only, never a total: a rule that carried a reading is the defect above.
    ///
    /// <para>Null for a launch no unit funds — an app principal such as perception, which keeps <see cref="RoleCap"/> and counts
    /// in the day's total and in no subtree. When set, it replaces <see cref="RoleCap"/>, and it must name the same units as
    /// the attempt's own <see cref="AiAttempt.Funding"/>, which is what is recorded.</para>
    /// </summary>
    public IReadOnlyList<FundedUnit>? Funding { get; init; }

    /// <summary>
    /// THE SAME CHAIN, EACH ENVELOPE SIZED FROM <see cref="PlanCap"/> (<c>U-billing-classes</c>): the same units in the same order as
    /// <see cref="Funding"/>, read off the same chart in the same admission, each unit's share of the plan's capacity — the envelopes
    /// nest in each class. Read for a <c>plan</c> or <c>unknown</c> launch; null exactly where <see cref="Funding"/> is.
    /// </summary>
    public IReadOnlyList<FundedUnit>? PlanFunding { get; init; }

    /// <summary>What THIS launch commits — the number about to be written as its reservation.</summary>
    public decimal Reservation { get; init; }

    /// <summary>
    /// THE SECURITY BOARD'S SHARE OF THE DAY (<c>U-board-b2</c> item 2; R24 § 4.1 "Its cost", BUDGET-001), set exactly for a launch of a
    /// board seat: the launch fits the day's ceiling FIRST, then the board's own — this share of the plan's day-total in force — over every
    /// board seat's rows of the day (<see cref="AiAttemptStore.BoardTotalsBetween"/>), never an org unit's envelope; and it is funded by
    /// no unit, so no chain is recorded. Null for every other launch.
    /// </summary>
    public decimal? BoardShare { get; init; }

    /// <summary>The local midnight the day's totals expire at, for the refusal's own sentence.</summary>
    public DateTimeOffset ResumesAt { get; init; }

    public bool Metered { get; init; } = true;

    /// <summary>
    /// The reading the admission is decided on: the day's own rows, the role's own rows, and this
    /// launch's reservation. Built here rather than in the store so that ONE record answers the
    /// question — <see cref="AiSpendToday.AdmitsAnotherTurn"/> — for the loop's cheap first look and
    /// for the transaction alike. Two comparisons that could drift apart would be two caps.
    /// </summary>
    internal AiSpendToday Reading(AiAttemptTotals day, AiAttemptTotals mine, string ceiling) => new()
    {
        Metered = Metered,
        Role = Role,
        Class = ceiling,
        Spent = day.Spent,
        Reserved = day.Reserved,
        RoleSpent = mine.Spent,
        RoleReserved = mine.Reserved,
        NextTurnReservation = Reservation,
        Cap = CapOf(ceiling),
        // A ROLE'S OWN CAP IS A SLICE OF THE MONEY CAP (perception's budget): under the plan's ceiling only the day's limit binds.
        RoleCap = ceiling == BillingClasses.Plan ? CapOf(ceiling) : RoleCap,
        ResumesAt = ResumesAt
    };

    /// <summary>The ceiling named by <paramref name="ceiling"/>: the plan's capacity for <c>plan</c>, otherwise the money cap.</summary>
    internal decimal CapOf(string ceiling) => ceiling == BillingClasses.Plan ? PlanCap : Cap;

    /// <summary>The funding chain sized from the ceiling named by <paramref name="ceiling"/>.</summary>
    internal IReadOnlyList<FundedUnit>? FundingOf(string ceiling) => ceiling == BillingClasses.Plan ? PlanFunding : Funding;
}

/// <summary>
/// WHETHER A LAUNCH MAY HAPPEN, AND WHAT IT COMMITTED — decided and written in one transaction.
///
/// <see cref="Admitted"/> is keyed on the REFUSAL and not on the id, because the two absences mean
/// opposite things. A refusal is "do not start the process": some ceiling has no room, or the launch
/// ledger could not take the row that reserves the turn (<see cref="Ledger"/>). A null id with no
/// refusal is <see cref="NoLedger"/> and nothing else: a host BUILT without a ledger, by construction.
/// A database that would not take the row is never that — it is a refusal, because a launch nobody
/// could reserve is spending no ceiling sees (<c>U-admission-fail-closed</c>).
/// </summary>
public sealed record AiAdmission
{
    /// <summary>
    /// A HOST BUILT WITHOUT A LAUNCH LEDGER, AND ONLY THAT: nothing was written because there is nothing to write to by
    /// construction, and nothing refused it. The unit-test hosts' explicit path. Never the answer to a write that failed
    /// (that is <see cref="LedgerRefused"/>), and never what the shipped app answers when its meter or a conversation's
    /// admission is missing (that is <see cref="NoLedgerOpen"/>).
    /// </summary>
    public static readonly AiAdmission NoLedger = new();

    /// <summary>
    /// THE REFUSAL FOR A LAUNCH WITH NO LEDGER OPEN TO RESERVE IT — the shipped app's meter not built, or a conversation
    /// no meter is attached to. Refused for the same reason a failed write is: there is no row, so no ceiling sees it.
    /// </summary>
    public static AiAdmission NoLedgerOpen => LedgerRefused("no launch ledger is open for it");

    /// <summary>The attempt id to complete at the end of the turn, or null when none was written.</summary>
    public string? Id { get; init; }

    public bool Admitted => Refusal is null;

    /// <summary>Why the launch was refused, in the owner's words. Null when it was admitted.</summary>
    public string? Refusal { get; init; }

    /// <summary>
    /// WHAT THE REFUSAL IS ABOUT, where it is a refusal to activate (<c>U-org-wakes</c>, ORCHESTRATION § 2.5):
    /// <see cref="RefusalKind.Position"/> for a name the chart does not hold as an active position — the loop settles its due
    /// wakes — and <see cref="RefusalKind.HostOrLaunch"/> for a refusal about this host or this launch, every <see cref="LaunchRefused"/> among them. Null for every other
    /// refusal — a ceiling, the ledger, a process still running — and for an admission: those keep every wake, as a
    /// <see cref="RefusalKind.HostOrLaunch"/> does. Never a default of <see cref="RefusalKind.Position"/>: only a refusal that
    /// says so settles anything.
    /// </summary>
    public RefusalKind? Kind { get; init; }

    /// <summary>
    /// WHICH CEILING REFUSED IT: <see cref="Day"/> for the owner's whole-day limit, or the role id
    /// whose share ran out. The two have different repairs — the day's limit is raised on the Safety
    /// page and a role's share is a reallocation — so a caller that said one when it meant the other
    /// would send the owner to the wrong screen.
    /// </summary>
    public string? Ceiling { get; init; }

    /// <summary>What <see cref="Ceiling"/> says for the owner's whole-day limit of real money.</summary>
    public const string Day = "day";

    /// <summary>
    /// What <see cref="Ceiling"/> says when the SECURITY BOARD'S OWN BUDGET refused a board seat's launch (<c>U-board-b2</c> item 2): its
    /// share of the day has no room for it, or the turn could not be priced. Not the owner's day, and no unit's envelope.
    /// </summary>
    public const string Board = "board";

    /// <summary>
    /// What <see cref="Ceiling"/> says for the owner's whole-day PLAN-CAPACITY limit (<c>U-billing-classes</c>): a different figure with a
    /// different repair — the plan's ceiling, raised on the same Safety page — so a caller never reads it as the money cap.
    /// </summary>
    public const string PlanDay = "plan-day";

    /// <summary>
    /// What <see cref="Ceiling"/> says when the LAUNCH LEDGER refused: the row that reserves the turn could not be
    /// written, or there was no ledger to write it to. Neither the day's limit nor a role's share ran out, so a caller
    /// that reads this must not send the owner to the Safety page.
    /// </summary>
    public const string Ledger = "ledger";

    /// <summary>
    /// What <see cref="Ceiling"/> says when the position's FUNDING CHAIN could not be read (<c>U-org-envelopes</c>): no unit's
    /// envelope ran out and no row was written; the chart would not say which units fund the launch, so it was not admitted
    /// on the day's limit alone.
    /// </summary>
    public const string Chart = "chart";

    /// <summary>
    /// THE REFUSAL FOR A LAUNCH WHOSE ROW COULD NOT BE WRITTEN — never an admission. A turn the ledger did not take is a
    /// turn whose cost no ceiling sees, so it is not started, it consumes no wake, and the reason to take it stays owed.
    /// </summary>
    public static AiAdmission LedgerRefused(string cause) => new()
    {
        Refusal = Labels.LaunchLedgerUnwritable(cause),
        Ceiling = Ledger,
        Cause = cause
    };

    /// <summary>
    /// What <see cref="Ceiling"/> says when the LAUNCH ITSELF was refused (<c>U-mint-per-launch</c>): its role already has
    /// a launch open, it carried an id not minted for it, or another launch took some of its wakes. A refusal about the
    /// launch and not the position (<c>docs/ORCHESTRATION.md</c> § 2.5): the loop steps over it keeping every wake, and the
    /// owner's words are not settled on it. ONE KIND, ONE RECORD (the orchestrator's ruling, 2026-10-16): it is typed
    /// <see cref="RefusalKind.HostOrLaunch"/>, never <see cref="RefusalKind.Position"/>.
    /// </summary>
    public const string Launch = "launch";

    /// <summary>
    /// What <see cref="Ceiling"/> says when the MANDATE refused (<c>U-org-assignments-a</c>): the mandate the launch would serve is
    /// closed, not its own, of unknown spend, or its allocation has no room for this reservation. <see cref="MandateId"/> names it.
    /// Nothing was written and no wake consumed; the loop closes that mandate's wakes and tells its decider.
    /// </summary>
    public const string Mandate = "mandate";

    /// <summary>The mandate a <see cref="Mandate"/> refusal is about.</summary>
    public string? MandateId { get; init; }

    /// <summary>
    /// A refusal of the launch itself (<see cref="Launch"/>), in <paramref name="refusal"/>'s words, typed
    /// <see cref="RefusalKind.HostOrLaunch"/>: it is about this launch, so it keeps every wake and settles none.
    /// </summary>
    public static AiAdmission LaunchRefused(string refusal) =>
        new() { Refusal = refusal, Ceiling = Launch, Kind = RefusalKind.HostOrLaunch };

    /// <summary>What the launch ledger said when it refused (<see cref="Ledger"/>), on its own; null for any other answer.</summary>
    public string? Cause { get; init; }

    /// <summary>By how much this launch would have passed that ceiling. Never negative.</summary>
    public decimal Over { get; init; }

    /// <summary>The local midnight the refusing day's totals expire at.</summary>
    public DateTimeOffset ResumesAt { get; init; }
}

/// <summary>
/// THE LAUNCH LEDGER: one row per run of the agent CLI, written before the process starts.
///
/// <para><b>Why it exists.</b> The meter it replaces wrote its totals when a turn FINISHED, so a
/// turn killed mid-flight cost nothing at all — the vendor had done the work and billed for it, and
/// the app's day total never moved. An agent that is killed and restarted got its allowance back
/// every time, and across midnight it got a fresh one; the ceiling that is the only bound on an AI
/// working non-stop was therefore a report on completed spending. Measured on 2026-09-07 the loop
/// spent 5.07 USD against a 5 USD cap.</para>
///
/// <para><b>The mechanism is <see cref="AiAttemptState"/> and nothing else.</b> The row goes in as
/// LAUNCHED with a reservation; it becomes ENDED when the app sees the usage; and it becomes LOST —
/// keeping the reservation as its cost — the next time a meter opens this database with it still
/// open, because at that moment the process that owned it is provably gone and its usage will never
/// arrive. Nothing here releases a reservation.</para>
///
/// <para><b>Written by the app only.</b> There is no verb, no pipe op and no path from the agent to
/// this table, which is the same rule <c>material</c> keeps: a bill the billed party can edit is not
/// a bill. <c>state/agent-turns.jsonl</c> remains beside it as the per-turn diagnostic mirror.</para>
/// </summary>
public sealed class AiAttemptStore(Database db)
{
    const string Cols = """
        id, started_at, runtime, requested_model, pricing_basis, reserved_cost, state, ended_at,
        exit_code, input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens,
        reasoning_output_tokens, effective_model, cost, unpriced_reason, context, policy_version,
        input_hash, role, runtime_profile, seat_class, assurance, mandate_id, allow_in, allow_out, turn_bound, billing_class
        """;

    /// <summary>
    /// ADMITS THE LAUNCH AND WRITES ITS RESERVATION, OR WRITES NOTHING — one transaction, one answer.
    ///
    /// <para><b>Why the ceiling is compared here.</b> The caller's own look at the day is a cheap
    /// first filter and cannot be the gate: it reads the totals and the row goes in some lines
    /// later, so two launches can both read "there is room for one" and both then take it. That is
    /// not a hypothetical — it is what <c>docs/COUNCIL.md</c> rule 3 requires reserved rather than
    /// checked, and the ledger's own arithmetic cannot fix a comparison made against a number that
    /// was already stale when it was read. Inside <see cref="Database.Write"/> the totals below are
    /// read under the same lock and the same transaction as the INSERT, so the second of two racing
    /// launches sees the first one's reservation and is refused.</para>
    ///
    /// <paramref name="admit"/> null is an ungated write: the owner's own recorded turn before any
    /// ceiling existed, and a test that is asserting about rows rather than about admission.
    ///
    /// <paramref name="consuming"/> is the wake queue's part of the same commit: the
    /// <c>mission_event</c> rows this launch is answering are marked <c>consumed_by</c> this attempt
    /// IN THIS TRANSACTION. Two transactions would leave a window in which the launch is recorded
    /// and the events are not — a kill there hands the same wake to the next launch and the owner
    /// pays for both — and a window the other way round in which events are spent on a launch that
    /// never happened. One commit has neither. A REFUSED launch consumes nothing, because it never
    /// happened and the reason to take it is still owed a turn.
    /// </summary>
    public AiAdmission Begin(AiAttempt a, IReadOnlyList<string>? consuming = null,
        AiAdmissionRule? admit = null) => db.Write(_ =>
    {
        // READ HERE, NOT BY THE CALLER. This is the whole of the guard: same transaction, same lock,
        // same connection as the INSERT below.
        if (admit is { } rule)
        {
            // THE ROW'S CLASS IS THE RULE'S CLASS: a launch admitted against one ceiling and recorded against another would be spend
            // the ceiling it is counted in never admitted.
            if (BillingClasses.Of(a.BillingClass) != BillingClasses.Of(rule.BillingClass))
                throw new ArgumentException("the attempt's billing class is not the one it was admitted against", nameof(a));

            // EACH CEILING THE CLASS MUST FIT (U-billing-classes; BUDGET-001): a key launch the money cap, a plan launch the plan's
            // capacity, an unknown one both — each over its own rows (its class's and the unknown ones), every one read here.
            foreach (var ceiling in BillingClasses.CeilingsOf(rule.BillingClass))
            {
                var day = Totals(rule.From, rule.To, null, null, ceiling);
                if (rule.BoardShare is { } share)
                {
                    // A SEAT OF THE SECURITY BOARD (U-board-b2 item 2): funded by no unit of the chart, so it records none.
                    if (a.Funding is { Count: > 0 } || rule.FundingOf(ceiling) is { Count: > 0 })
                        throw new ArgumentException("a board seat's launch is funded by no unit of the chart", nameof(a));

                    // THE DAY FIRST: every row of that ceiling's money.
                    var reading = rule.Reading(day, day, ceiling);
                    if (!reading.AdmitsGlobally) return Refuse(rule, reading);

                    // THEN THE BOARD'S OWN SHARE OF IT, over every board seat's rows read here under the same lock as the INSERT — an
                    // unpriced turn never counted as nothing: a launch that cannot be priced is not started, and a row that ended
                    // unpriced counts its reservation.
                    if (rule.Metered)
                    {
                        if (rule.Reservation <= 0m) return new AiAdmission { Refusal = Labels.BoardTurnUnpriced, Ceiling = AiAdmission.Board };
                        // ITS SHARE OF THE PLAN'S DAY-TOTAL IN FORCE, whichever ceiling the launch is billed to (R24 § 4.1: "its share of
                        // the plan's day-total"): a key launch fits the money cap above and this as well, never a larger share of money.
                        // ONE BUDGET ACROSS EVERY BILLING CLASS (BUDGET-001; U-board-b2 fix pass, the review's P1-1): every board seat's rows,
                        // plan, key and unknown alike — read through a class's filter, plan launches and key launches could each spend it whole.
                        var ceilingOf = share * rule.PlanCap;
                        var board = BoardTotals(rule.From, rule.To);
                        if (board.Spent + board.Reserved + rule.Reservation > ceilingOf)
                        {
                            var room = ceilingOf - board.Spent - board.Reserved;
                            return new AiAdmission
                            {
                                Refusal = Labels.BoardBudgetReached(share, ceilingOf, room > 0m ? room : 0m, rule.Reservation),
                                Ceiling = AiAdmission.Board,
                                Over = rule.Reservation - room,
                                ResumesAt = rule.ResumesAt
                            };
                        }
                    }
                }
                else if (rule.FundingOf(ceiling) is { } chain)
                {
                    // THE CHAIN RECORDED IS THE CHAIN ADMITTED: one resolution, or the launch is not written.
                    if (!chain.Select(u => u.UnitId).SequenceEqual(a.Funding ?? [], StringComparer.Ordinal))
                        throw new ArgumentException("the attempt's funding chain is not the one it was admitted against", nameof(a));

                    // THE CLASS'S DAY FIRST: every row of that ceiling's money, an app principal's included.
                    var reading = rule.Reading(day, day, ceiling);
                    if (!reading.AdmitsGlobally) return Refuse(rule, reading);

                    // THEN EVERY UNIT OF THE CHAIN, its envelope sized from this ceiling, over the attempts it funded in this
                    // ceiling's rows, read here under the same lock as the INSERT.
                    if (rule.Metered)
                        foreach (var unit in chain)
                        {
                            if (unit.Envelope is not { } envelope) continue;
                            var funded = Totals(rule.From, rule.To, null, unit.UnitId, ceiling);
                            if (OrgStore.Unfundable(rule.Role ?? CouncilRoles.Default, unit.UnitId, envelope, rule.Reservation) is { } never)
                                return RefuseUnit(rule, unit.UnitId, envelope, funded, never, ceiling);
                            if (funded.Spent + funded.Reserved + rule.Reservation > envelope)
                                return RefuseUnit(rule, unit.UnitId, envelope, funded, null, ceiling);
                        }
                }
                else
                {
                    var mine = rule.Role is null ? day : Totals(rule.From, rule.To, rule.Role, null, ceiling);
                    var reading = rule.Reading(day, mine, ceiling);
                    if (!reading.AdmitsAnotherTurn) return Refuse(rule, reading);
                }
            }
        }

        // THE MANDATE'S OWN ALLOCATION — THE THIRD GATE (U-org-assignments-a; ORCHESTRATION § 2.4, BUDGET-001), after the day
        // and the funding chain, in this same transaction: the mandate this launch serves is the one its consumed `mandate` wake
        // names (or the one the caller named, which must agree), and it is admitted only while that mandate is open, held by this
        // role, its spend is known, and its measured spend + its unresolved reservations + this reservation fit its lifetime
        // allocation. A LOST launch keeps its reservation as its cost, so it stays counted. Refused with no row, no reservation
        // and no wake consumed — nothing below has run.
        string? mandateId;
        if (OrgStore.MandateServed(db, a, consuming, out mandateId) is { } served) return served;
        if (mandateId is not null && OrgStore.AllocationRefusal(db, mandateId, a) is { } unaffordable) return unaffordable;

        // A SAVEPOINT, so a launch refused on its wakes below rolls back its own row and nothing else — the same whether
        // this is the outermost write or joins one already open (Database.Write).
        Exec("SAVEPOINT ai_attempt_begin");

        using var c = db.Cmd($"""
            INSERT INTO ai_attempt({Cols})
            VALUES($id,$started,$rt,$req,$basis,$res,$state,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                   NULL,NULL,NULL,$ctx,$policy,$hash,$role,$profile,$class,$assurance,$mandate,$in,$out,$bound,$billing)
            """,
            ("$id", a.Id), ("$started", Sql.T(a.StartedAt)), ("$rt", a.Runtime),
            ("$req", a.RequestedModel), ("$basis", a.PricingBasis), ("$res", Sql.D(a.ReservedCost)),
            ("$state", nameof(AiAttemptState.LAUNCHED)), ("$ctx", a.Context),
            ("$policy", a.PolicyVersion), ("$hash", a.InputHash), ("$role", a.Role), ("$profile", a.RuntimeProfile),
            ("$class", a.SeatClass), ("$assurance", a.Assurance), ("$mandate", mandateId),
            ("$in", a.AllowIn), ("$out", a.AllowOut), ("$bound", a.TurnBound), ("$billing", BillingClasses.Of(a.BillingClass)));
        c.ExecuteNonQuery();
        // THE CHAIN, IN THE SAME COMMIT AS THE ROW IT FUNDS: never a row without its chain, and never a chain re-read later.
        if (a.Funding is { Count: > 0 } funding)
            for (var depth = 0; depth < funding.Count; depth++)
            {
                using var f = db.Cmd("INSERT INTO ai_attempt_funding(attempt_id, unit_id, depth) VALUES($id,$unit,$depth)",
                    ("$id", a.Id), ("$unit", funding[depth]), ("$depth", depth));
                f.ExecuteNonQuery();
            }

        // THE WAKES ARE THE TRANSACTION'S ANSWER (U-mint-per-launch). A launch is admitted and charged only for wakes it
        // actually took: `MarkConsumed` writes only a wake nobody consumed, and one another launch already took is a
        // reason this launch no longer has. Refused in words naming the launch and the count — its row rolled back,
        // nothing reserved, and its own untaken wakes still due.
        if (consuming is { Count: > 0 })
        {
            var asked = consuming.Distinct(StringComparer.Ordinal).Count();
            var taken = MissionEventStore.MarkConsumed(db, consuming, a.Id, a.StartedAt);
            if (taken < asked)
            {
                Exec("ROLLBACK TO ai_attempt_begin");
                Exec("RELEASE ai_attempt_begin");
                return AiAdmission.LaunchRefused(Labels.LaunchWakesTaken(a.Id, taken, asked));
            }
        }

        Exec("RELEASE ai_attempt_begin");
        return new AiAdmission { Id = a.Id, ResumesAt = admit?.ResumesAt ?? default };
    });

    void Exec(string sql)
    {
        using var c = db.Cmd(sql);
        c.ExecuteNonQuery();
    }

    /// <summary>
    /// The refusal, in the owner's words, naming the ceiling that had no room and by how much. It
    /// carries no money formatting: this layer does not know the currency, and a sentence that
    /// guessed one would put a wrong symbol on the owner's screen.
    /// </summary>
    static AiAdmission Refuse(AiAdmissionRule rule, AiSpendToday reading)
    {
        var global = !reading.AdmitsGlobally;
        var over = global
            ? reading.Spent + reading.Reserved + reading.NextTurnReservation - reading.Cap
            : reading.RoleSpent + reading.RoleReserved + reading.NextTurnReservation - reading.RoleCap;

        var plan = reading.Class == BillingClasses.Plan;
        return new AiAdmission
        {
            Refusal = global
                ? plan ? Labels.DailyPlanCapacityReached : Labels.DailySpendingLimitReached
                // PERCEPTION'S OWN BUDGET, IN ITS OWN WORDS (U-decision-port): it is no council role's share, and the
                // sentence that names one would send the owner to a split that does not hold it.
                : rule.Role == AppPrincipals.Perception
                    ? Labels.PerceptionBudgetReached
                    // NEVER FOLDED INTO THE CHAIR: a position is refused by its unit (RefuseUnit), and any other role
                    // named here is named as itself (U-org-envelopes).
                    : Labels.RoleShareReached(CouncilRoles.Title(rule.Role!)),
            Ceiling = global ? plan ? AiAdmission.PlanDay : AiAdmission.Day : rule.Role,
            Over = over > 0m ? over : 0m,
            ResumesAt = rule.ResumesAt
        };
    }

    /// <summary>
    /// THE REFUSAL BY A UNIT OF THE FUNDING CHAIN (<c>U-org-envelopes</c>): <see cref="AiAdmission.Ceiling"/> is the unit's id, and
    /// the sentence names the unit, its envelope and the room it has left — or, where one turn is larger than the unit's whole
    /// envelope, <paramref name="never"/>: the sentence of a split that cannot fund its own seat, which midnight does not repair.
    /// </summary>
    static AiAdmission RefuseUnit(AiAdmissionRule rule, string unitId, decimal envelope, AiAttemptTotals funded, string? never,
        string ceiling)
    {
        var room = envelope - funded.Spent - funded.Reserved;
        return new AiAdmission
        {
            Refusal = never ?? Labels.UnitEnvelopeReached(OrgEnvelopes.NameOf(unitId), envelope, room > 0m ? room : 0m, rule.Reservation,
                plan: ceiling == BillingClasses.Plan),
            Ceiling = unitId,
            Over = rule.Reservation - room,
            ResumesAt = rule.ResumesAt
        };
    }

    /// <summary>
    /// Completes one attempt. ONLY a row still LAUNCHED is written: if a restart already declared it
    /// LOST, that verdict stands and the reservation stays charged, because a process that came back
    /// to claim a cheaper number for a turn nobody was watching is exactly what this table refuses.
    /// Returns false when nothing was updated.
    /// </summary>
    public bool End(string id, int exitCode, DateTimeOffset endedAt, long? input, long? cached,
        long? cacheWrite, long? output, long? reasoning, string? effectiveModel, decimal? cost,
        string? unpricedReason, string? context, string? pricingBasis = null) => db.Write(_ =>
    {
        // A TURN THAT ENDED WITHOUT SAYING WHAT IT USED KEEPS ITS RESERVATION AS ITS COST.
        //
        // This is the non-crash half of the same rule LoseOpen keeps. A turn whose process exited
        // with no usage event — it failed to start, it was cancelled, the runtime printed nothing
        // this parser recognised — became ENDED with a null cost, and `Reserved` sums only LAUNCHED
        // rows, so the whole commitment was released and NOTHING replaced it. The vendor had still
        // been asked to do the work. Kill a turn early enough and the allowance came back, which is
        // exactly what docs/COUNCIL.md rule 3 forbids on every path, not only on the crash path.
        //
        // A reservation of zero is not a charge, and such a row stays unpriced: an installation that
        // cannot price a turn at all must not have its ceiling filled with zeroes that look measured.
        using var c = db.Cmd("""
            UPDATE ai_attempt SET
              state='ENDED', ended_at=$end, exit_code=$exit,
              input_tokens=$in, cached_input_tokens=$cached, cache_write_input_tokens=$write,
              output_tokens=$out, reasoning_output_tokens=$reason,
              effective_model=$model,
              cost=COALESCE($cost, CASE WHEN $in IS NULL AND $out IS NULL
                                             AND CAST(reserved_cost AS REAL) > 0
                                        THEN reserved_cost END),
              unpriced_reason=CASE WHEN $cost IS NULL AND $in IS NULL AND $out IS NULL
                                        AND CAST(reserved_cost AS REAL) > 0
                                   THEN $unreported ELSE $why END,
              context=COALESCE($ctx, context),
              pricing_basis=COALESCE($basis, pricing_basis)
            WHERE id=$id AND state='LAUNCHED'
            """,
            ("$end", Sql.T(endedAt)), ("$exit", exitCode), ("$in", input), ("$cached", cached),
            ("$write", cacheWrite), ("$out", output), ("$reason", reasoning),
            ("$model", effectiveModel), ("$cost", cost is null ? null : Sql.D(cost.Value)),
            ("$why", unpricedReason), ("$unreported", UnreportedReason),
            ("$ctx", context), ("$basis", pricingBasis), ("$id", id));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// What <c>unpriced_reason</c> says on a turn charged its reservation because its usage never
    /// arrived. A constant because three surfaces read the row and one of them is the owner's daily
    /// report, where "its reservation stands as its cost" is the whole of what they need to know.
    /// </summary>
    public const string UnreportedReason =
        "the turn ended without reporting what it used; the reservation stands as its cost";

    /// <summary>
    /// What <c>pricing_basis</c> says when the owner's own two numbers priced the turn. A row with
    /// this basis is never counted as an estimate, however little the runtime said about itself:
    /// the rate is what the person paying the bill says they are charged, not a guess over a
    /// catalogue. It is a constant here because the query below is what reads it.
    /// </summary>
    public const string OwnerBasis = "owner";

    /// <summary>
    /// EVERY ATTEMPT STILL LAUNCHED BECOMES LOST, KEEPING ITS RESERVATION AS ITS COST.
    ///
    /// Called when a meter opens the database. At that moment any row still LAUNCHED belongs to a
    /// process that is gone — the app is starting, or a second meter is being built over the same
    /// file — so its usage will never be reported by anyone. Releasing the reservation there is the
    /// defect this whole table exists for: kill the turn before its usage arrives and the allowance
    /// comes back. It does not come back. Returns how many were lost.
    /// </summary>
    /// <param name="except">
    /// THE LAUNCHES THE CALLING PROCESS IS STILL FLYING, which are the one kind of open row that is
    /// NOT a turn nobody will hear from again. Two roles' turns may overlap, so a meter built while
    /// one of them is in flight would otherwise declare that live turn LOST and charge it its
    /// reservation — and the usage it then reported would be refused by the row, because
    /// <see cref="End"/> writes only a row still LAUNCHED. Skipping it releases nothing: a LAUNCHED
    /// row's reservation is counted against the day's ceiling exactly as a LOST row's cost is.
    ///
    /// Empty or null loses everything, which is what a RESTART is: a process that has just started
    /// holds no launches, so nothing is skipped and the whole table is reconciled.
    /// </param>
    public int LoseOpen(DateTimeOffset at, IReadOnlyCollection<string>? except = null) => db.Write(_ =>
    {
        var live = except is { Count: > 0 } ? except.ToArray() : [];
        var names = live.Select((_, i) => $"$live{i.ToString(CultureInfo.InvariantCulture)}").ToArray();
        var keep = live.Length == 0 ? "" : $" AND id NOT IN ({string.Join(",", names)})";

        using var c = db.Cmd($"""
            UPDATE ai_attempt
               SET state='LOST', ended_at=$at, cost=reserved_cost,
                   unpriced_reason=COALESCE(unpriced_reason, $why)
             WHERE state='LAUNCHED'{keep}
            """,
            [("$at", (object?)Sql.T(at)),
             ("$why", "the turn was launched and never reported its usage; the reservation stands"),
             .. names.Select((n, i) => (n, (object?)live[i]))]);
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// The day's totals, over attempts that STARTED inside the local day <paramref name="from"/> to
    /// <paramref name="to"/>. Local because the owner's midnight is the one on their wall.
    ///
    /// A turn is counted once it is resolved, so a launch in flight is in <c>Reserved</c> and not in
    /// <c>Turns</c>. "Estimated" is not a column: a row priced while the runtime named no model IS
    /// the estimate, and deriving it here keeps one fact in one place — except where the rate was
    /// the owner's own (<see cref="OwnerBasis"/>), which is a figure they are responsible for rather
    /// than one this build inferred.
    /// </summary>
    /// <param name="ceiling">
    /// The rows ONE CEILING counts (<c>U-billing-classes</c>): <c>plan</c> the day's <c>plan</c> and <c>unknown</c> rows, <c>key</c> its
    /// <c>key</c> and <c>unknown</c> rows — an unknown row is in both — or null for every row.
    /// </param>
    public AiAttemptTotals TotalsBetween(DateTimeOffset from, DateTimeOffset to, string? role = null, string? ceiling = null) =>
        db.Read(_ => Totals(from, to, role, null, ceiling));

    /// <summary>
    /// ONE UNIT'S DAY (<c>U-org-wakes</c>): every attempt in the window whose recorded funding chain holds <paramref name="unitId"/>
    /// — the same totals <see cref="Begin"/> compares that unit's envelope against inside its transaction, read here for the
    /// loop's first look and the card, so neither can admit what the gate refuses.
    /// </summary>
    public AiAttemptTotals UnitTotalsBetween(DateTimeOffset from, DateTimeOffset to, string unitId, string? ceiling = null) =>
        db.Read(_ => Totals(from, to, null, unitId, ceiling));

    /// <summary>
    /// EVERY POSITION'S LATEST LAUNCH, by position id, in ONE query: its seat class, assurance and billing class as the ledger recorded
    /// them. A position that never launched is not in it. The chart page reads this once and not once per position. The class column is named here, in the ledger's own store, and nowhere else (a guard says so).
    /// </summary>
    public IReadOnlyDictionary<string, RecordedSeat> RecordedSeats() => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT COALESCE(a.role, $chair), a.seat_class, a.assurance, a.billing_class, a.started_at
              FROM ai_attempt a
             WHERE a.rowid = (SELECT b.rowid FROM ai_attempt b WHERE COALESCE(b.role, $chair) = COALESCE(a.role, $chair)
                               ORDER BY b.started_at DESC, b.rowid DESC LIMIT 1)
            """, ("$chair", CouncilRoles.Default));
        using var r = c.ExecuteReader();
        var seats = new Dictionary<string, RecordedSeat>(StringComparer.Ordinal);
        while (r.Read())
        {
            var name = Sql.S(r.GetValue(2));
            SeatAssurance? assurance = null;
            foreach (var a in Enum.GetValues<SeatAssurance>())
                if (SeatClasses.Name(a) == name) assurance = a;
            seats[r.GetString(0)] = new RecordedSeat(r.GetString(0), Sql.S(r.GetValue(1)), assurance, BillingClasses.Of(Sql.S(r.GetValue(3))),
                Sql.Time(r.GetValue(4)));
        }
        return (IReadOnlyDictionary<string, RecordedSeat>)seats;
    });

    /// <summary>
    /// EVERY UNIT'S DAY SPLIT BY THE CLASS THAT PAID FOR IT (<c>U-org-surface-a</c>), in ONE query: for each unit that funded an attempt
    /// in the window and each billing class among those attempts, the same eight figures <see cref="UnitTotalsBetween"/> reads for
    /// that unit (the same aggregate list, over the rows each unit's recorded funding chain holds). <b>The class is exact:</b>
    /// <c>plan</c> is the plan's rows alone and <c>unknown</c> is the rows nobody established — NOT the "or unknown" of a ceiling
    /// (<c>ceiling</c> counts an unknown row in both), so the three classes of a unit add up to its whole day and none is counted
    /// twice. A turn with no cost is in <see cref="AiAttemptTotals.Unpriced"/> and contributes nothing to <c>Spent</c>; it is never added as
    /// a zero price. <b>A unit's day carries every unit merged into it</b> (<c>U-org-verbs-d</c> fix pass 2), exactly as
    /// <see cref="Totals"/> carries it for admission — through the merge's successor link (<see cref="OrgStore.CarriedPairs"/>), each row
    /// counted once per unit and <c>ai_attempt_funding</c> never rewritten — so the page and the envelope read the same day.
    /// </summary>
    public IReadOnlyList<UnitClassTotals> UnitTotalsByClass(DateTimeOffset from, DateTimeOffset to) => db.Read(_ =>
    {
        var principals = AppPrincipals.All.Select((p, i) => ($"$principal{i.ToString(CultureInfo.InvariantCulture)}", (object?)p)).ToArray();
        // EACH ROW UNDER EVERY UNIT ITS CHAIN HOLDS, AND UNDER EVERY SUCCESSOR OF ONE; UNION counts a row once per unit. The carried
        // pairs are ONE parameter read with json_each — two compound terms however many merges there were (U-merge-evidence-shares).
        using var c = db.Cmd($"""
            SELECT unit_id, billing_class,
            {TotalsColumns}
            FROM (SELECT a.*, k.unit_id AS unit_id,
                         CASE WHEN a.role IN ({string.Join(",", principals.Select(p => p.Item1))}) THEN 0 ELSE 1 END AS counted
                    FROM ai_attempt a
                    JOIN (SELECT f.attempt_id, f.unit_id FROM ai_attempt_funding f
                          UNION
                          SELECT f.attempt_id, json_extract(p.value, '$[0]') FROM json_each($carry) p
                            JOIN ai_attempt_funding f ON f.unit_id = json_extract(p.value, '$[1]')) k ON k.attempt_id = a.id
                   WHERE a.started_at >= $from AND a.started_at < $to)
            GROUP BY unit_id, billing_class
            ORDER BY unit_id, billing_class
            """, [("$from", Sql.T(from)), ("$to", Sql.T(to)), ("$carry", OrgStore.CarriedPairs(db)), .. principals]);
        using var r = c.ExecuteReader();
        var rows = new List<UnitClassTotals>();
        while (r.Read())
            rows.Add(new UnitClassTotals(r.GetString(0), BillingClasses.Of(r.GetString(1)),
                new AiAttemptTotals((decimal)r.GetDouble(2), (decimal)r.GetDouble(3), Int(r, 4), Int(r, 5), Int(r, 6), Int(r, 7), Int(r, 8),
                    (decimal)r.GetDouble(9))));
        return (IReadOnlyList<UnitClassTotals>)rows;
    });

    /// <summary>
    /// THE EIGHT AGGREGATES EVERY TOTAL IS (<see cref="Totals"/>, <see cref="UnitTotalsByClass"/>), over rows that carry a <c>counted</c>
    /// flag: one expression list, so a figure read by unit and by class can never drift from the figure read whole.
    /// </summary>
    const string TotalsColumns = """
              COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN 0 ELSE CAST(cost AS REAL) END), 0),
              COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN CAST(reserved_cost AS REAL) ELSE 0 END), 0),
              SUM(CASE WHEN counted=1 AND state<>'LAUNCHED' THEN 1 ELSE 0 END),
              SUM(CASE WHEN counted=1 AND state<>'LAUNCHED' AND cost IS NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN counted=1 AND state<>'LAUNCHED' AND cost IS NOT NULL AND effective_model IS NULL
                            AND COALESCE(pricing_basis,'') <> 'owner' THEN 1 ELSE 0 END),
              SUM(CASE WHEN counted=1 AND state='LAUNCHED' THEN 1 ELSE 0 END),
              -- Charged a RESERVATION rather than a bill: a cost with a reason it could not be
              -- priced is exactly that, and it is what keeps `Spent` from reading as an invoice.
              SUM(CASE WHEN counted=1 AND state<>'LAUNCHED' AND cost IS NOT NULL AND unpriced_reason IS NOT NULL
                       THEN 1 ELSE 0 END),
              -- THE MONEY OF THOSE CHARGES (U-verbs-a-followups), over every row `Spent` sums — money, like `Spent`, never a
              -- count — so the priced part of the day is `Spent` less this, and an unmeasured charge never reads as a bill.
              COALESCE(SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL AND unpriced_reason IS NOT NULL
                                THEN CAST(cost AS REAL) ELSE 0 END), 0)
        """;

    /// <summary>
    /// The same query with NO lock of its own, so <see cref="Begin"/> can run it inside the
    /// transaction it is about to insert into. Every caller is already holding the database's gate —
    /// either through <see cref="Database.Read"/> above or through <see cref="Database.Write"/> —
    /// and that is the point: an admission decided outside that gate is decided on a number another
    /// launch is free to change before the row lands.
    ///
    /// <para><b>The day's MONEY is every row's; the day's COUNTS are the AI's turns.</b> With no role named,
    /// spent and reserved sum every row — the app's own principals' too, so a decision model's calls
    /// (<see cref="AppPrincipals.Perception"/>, <c>U-decision-port</c>) are inside the owner's one cap — while
    /// the five counts (turns, unpriced, estimated, open, unreported) skip the app's own principals: a
    /// perception call is not a turn of the AI's, and the card, <c>ai_turns_today</c>, the Situation and the
    /// report that read these counts were each reading one as a turn. A role named is that role's rows,
    /// counts and all — perception's own reading is whole, because its budget is compared against it.</para>
    /// </summary>
    /// <para><b>A UNIT named</b> (<c>U-org-envelopes</c>) is every row whose recorded funding chain holds it — the subtree's spend as
    /// it was admitted, never as today's chart would re-file it; its counts follow the role-less rule above. A unit's day also carries
    /// every unit merged into it (<c>U-org-verbs-d</c>; <see cref="OrgStore.CarriedBy"/>), read through the same recorded chains.</para>
    /// <para><b>A CEILING named</b> (<c>U-billing-classes</c>) is the rows that ceiling counts: its own class's and every <c>unknown</c>
    /// row — an unknown row is counted in both, never in neither.</para>
    AiAttemptTotals Totals(DateTimeOffset from, DateTimeOffset to, string? role, string? unit = null, string? ceiling = null)
    {
        var principals = AppPrincipals.All.Select((p, i) => ($"$principal{i.ToString(CultureInfo.InvariantCulture)}", (object?)p)).ToArray();
        // A UNIT'S DAY CARRIES EVERY UNIT MERGED INTO IT (U-org-verbs-d; OrgStore.CarriedBy): each row stays on the chain it was
        // admitted under, and counts in its successor's day too.
        // One parameter, read with json_each, however many merges there were (U-merge-evidence-shares).
        var unitClause = unit is null ? "" : """
             AND EXISTS (SELECT 1 FROM ai_attempt_funding f
                          WHERE f.unit_id IN (SELECT value FROM json_each($carried)) AND f.attempt_id = ai_attempt.id)
            """;
        using var c = db.Cmd($"""
            SELECT
            {TotalsColumns}
            FROM (SELECT *,
                         CASE WHEN $role IS NULL AND role IN ({string.Join(",", principals.Select(p => p.Item1))})
                              THEN 0 ELSE 1 END AS counted
                    FROM ai_attempt WHERE started_at >= $from AND started_at < $to
                     AND ($role IS NULL OR COALESCE(role,$chair) = $role)
                     {unitClause}
                     AND ($ceiling IS NULL OR billing_class = $ceiling OR billing_class = 'unknown'))
            """, [("$from", Sql.T(from)), ("$to", Sql.T(to)), ("$role", role), ("$ceiling", ceiling),
            ("$chair", CouncilRoles.Default), ("$carried", unit is null ? null : OrgStore.CarriedSet(db, unit)), .. principals]);
        using var r = c.ExecuteReader();
        if (!r.Read()) return new AiAttemptTotals(0m, 0m, 0, 0, 0, 0);

        // Summed as REAL and returned as decimal. SQLite has no decimal, and these are text columns
        // precisely so a written value comes back as it went in; a total of a few dozen four-decimal
        // figures is exact to far more places than a currency needs, and the per-row numbers the
        // owner reads are never routed through this.
        return new AiAttemptTotals(
            (decimal)r.GetDouble(0), (decimal)r.GetDouble(1),
            Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5), Int(r, 6), (decimal)r.GetDouble(7));
    }

    static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));

    /// <summary>
    /// WHAT THE SECURITY BOARD SPENT IN A WINDOW (<c>U-board-b2</c> item 2; R24 § 4.1 "Its cost"): every row whose role is a board seat,
    /// of EVERY billing class — the board's share is one budget, whoever pays for a launch (BUDGET-001; the fix pass's P1-1) — summed: a
    /// closed row at its cost, or at its RESERVATION where it ended unpriced (an unknown is never counted as nothing), and an open row at
    /// its reservation. The board's spend is its launches' rows by role; no funding row is read, and no class filter is offered.
    /// </summary>
    public AiAttemptTotals BoardTotalsBetween(DateTimeOffset from, DateTimeOffset to) =>
        db.Read(_ => BoardTotals(from, to));

    AiAttemptTotals BoardTotals(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Cmd("""
            SELECT COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN 0 ELSE CAST(COALESCE(cost, reserved_cost) AS REAL) END), 0),
                   COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN CAST(reserved_cost AS REAL) ELSE 0 END), 0),
                   SUM(CASE WHEN state<>'LAUNCHED' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NULL THEN 1 ELSE 0 END),
                   0,
                   SUM(CASE WHEN state='LAUNCHED' THEN 1 ELSE 0 END)
              FROM ai_attempt
             WHERE started_at >= $from AND started_at < $to
               AND substr(role, 1, length($prefix)) = $prefix
            """, ("$from", Sql.T(from)), ("$to", Sql.T(to)), ("$prefix", Board.BoardSeats.Prefix));
        using var r = c.ExecuteReader();
        if (!r.Read()) return new AiAttemptTotals(0m, 0m, 0, 0, 0, 0);
        return new AiAttemptTotals((decimal)r.GetDouble(0), (decimal)r.GetDouble(1), Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5));
    }

    /// <summary>
    /// THE CHAIN AN ATTEMPT WAS ADMITTED UNDER, nearest unit first, as <see cref="Begin"/> recorded it; empty for an attempt no
    /// unit funds. Read from the rows and never from today's chart, which may have moved since.
    /// </summary>
    public IReadOnlyList<string> FundingOf(string attemptId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT unit_id FROM ai_attempt_funding WHERE attempt_id=$id ORDER BY depth", ("$id", attemptId));
        using var r = c.ExecuteReader();
        var units = new List<string>();
        while (r.Read()) units.Add(r.GetString(0));
        return (IReadOnlyList<string>)units;
    });

    /// <summary>
    /// WHAT AN ATTEMPT'S PACKET GAVE IT (<c>U-org-packets-b</c>, rung <see cref="Versions.InputLineageRung"/>; ORCHESTRATION § 2.6): one
    /// <c>ai_attempt_input</c> row per record shown, by kind and id. Called only inside the turn's committed transition — the
    /// caller's <see cref="Database.Write"/> — so the records a launch was shown are recorded with its outcome or not at all.
    /// INSERT … ON CONFLICT DO NOTHING: a transition committed twice writes nothing twice. A kind outside
    /// <see cref="PacketInputKind.All"/> is refused by the table's CHECK, and the transition with it.
    /// </summary>
    public void RecordInputs(string attemptId, IEnumerable<PacketInput> inputs) => db.Write(_ =>
    {
        foreach (var input in inputs)
        {
            using var c = db.Cmd("INSERT INTO ai_attempt_input(attempt_id, kind, ref_id) VALUES($a,$k,$r) ON CONFLICT DO NOTHING",
                ("$a", attemptId), ("$k", input.Kind), ("$r", input.RefId));
            c.ExecuteNonQuery();
        }
        return 0;
    });

    /// <summary>What <paramref name="attemptId"/>'s packet gave it, as recorded, in kind then id order.</summary>
    public IReadOnlyList<PacketInput> InputsOf(string attemptId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT kind, ref_id FROM ai_attempt_input WHERE attempt_id=$a ORDER BY kind, ref_id", ("$a", attemptId));
        using var r = c.ExecuteReader();
        var rows = new List<PacketInput>();
        while (r.Read()) rows.Add(new PacketInput(r.GetString(0), r.GetString(1)));
        return (IReadOnlyList<PacketInput>)rows;
    });

    /// <summary>One attempt by id, or null. For the screens and for a test that has to read a row back.</summary>
    public AiAttempt? Get(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM ai_attempt WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>
    /// THE LATEST LAUNCH OF <paramref name="role"/> THAT TOOK A WAKE — the newest row of that role that consumed a
    /// <c>mission_event</c> in its own commit (<see cref="Begin"/>), whatever became of it after: LAUNCHED, ENDED or
    /// LOST. That is a mission turn the loop's admission let through, the one kind of launch that is shown a
    /// Situation; the owner's typed turn takes no wake, and a launch the ledger refused wrote no row at all. Null
    /// where the role has none.
    /// </summary>
    public AiAttempt? LastWoken(string role) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM ai_attempt a
             WHERE COALESCE(a.role, $chair) = $role
               AND EXISTS (SELECT 1 FROM mission_event e WHERE e.consumed_by = a.id)
             ORDER BY a.started_at DESC, a.rowid DESC LIMIT 1
            """, ("$chair", CouncilRoles.Default), ("$role", role));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>
    /// WHO PAID FOR <paramref name="role"/>'S LAST ADMITTED LAUNCH, as its row recorded it (<c>U-billing-classes</c> fix pass 1): the
    /// billing class the meter wrote from the seat verdict it admitted, read from the ledger rather than from any process's memory, so an
    /// app start does not forget which ceiling a role is held to. Narrowed to rows on <paramref name="seatClass"/> where one is named — a
    /// role moved to another kind of seat is not read by a row of the seat it left. Null where the role has no such row.
    /// </summary>
    public string? LastBillingClass(string role, string? seatClass = null) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT billing_class FROM ai_attempt
             WHERE COALESCE(role, $chair) = $role
               AND ($seat IS NULL OR seat_class = $seat)
             ORDER BY started_at DESC, rowid DESC LIMIT 1
            """, ("$chair", CouncilRoles.Default), ("$role", role), ("$seat", seatClass));
        return c.ExecuteScalar() as string;
    });

    /// <summary>
    /// THE TOKEN PROFILE PER POSITION × KIND × SESSION OVER A WINDOW (<c>U-org-packets-a</c>; ORGANISATION § 17, R17 #20): what a fresh
    /// packet costs against a resumed session, from the runtime's own figures on every RESOLVED attempt that started in the window.
    /// One read. The kind is the row's own fact (<c>mandate_id</c>), never today's chart; the session is what the attempt's
    /// <c>context</c> recorded. An attempt with no input figure is UNREPORTED — counted, and in no median.
    /// </summary>
    public IReadOnlyList<SessionProfileRow> SessionProfile(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = db.Read(_ =>
        {
            using var c = db.Cmd("""
                SELECT COALESCE(role, $chair), mandate_id IS NOT NULL, input_tokens, cached_input_tokens, output_tokens, context
                  FROM ai_attempt
                 WHERE started_at >= $from AND started_at < $to AND state <> 'LAUNCHED'
                """, ("$chair", CouncilRoles.Default), ("$from", Sql.T(from)), ("$to", Sql.T(to)));
            using var r = c.ExecuteReader();
            var list = new List<(string Role, string Kind, string Session, long? Input, long? Cached, long? Output)>();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt64(1) != 0 ? "mandate" : "own", SessionOf(Sql.S(r.GetValue(5))),
                    Long(r, 2), Long(r, 3), Long(r, 4)));
            return list;
        });

        return [.. rows
            .GroupBy(a => (a.Role, a.Kind, a.Session))
            .OrderBy(g => g.Key.Role, StringComparer.Ordinal).ThenBy(g => g.Key.Kind, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Session, StringComparer.Ordinal)
            .Select(g =>
            {
                var reported = g.Where(a => a.Input is not null).ToList();
                return new SessionProfileRow(g.Key.Role, g.Key.Kind, g.Key.Session, g.Count(), g.Count() - reported.Count,
                    Median(reported.Select(a => a.Input)),
                    Median(reported.Select(a => a.Cached)),
                    Median(reported.Select(a => a.Cached is { } cached ? a.Input - cached : null)),
                    Median(reported.Select(a => a.Output)));
            })];
    }

    /// <summary>The session an attempt's context recorded, or <c>unknown</c> where it recorded none or cannot be read.</summary>
    static string SessionOf(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return "unknown";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(context);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("session", out var s) && s.GetString() is { Length: > 0 } session
                ? session
                : "unknown";
        }
        catch (System.Text.Json.JsonException) { return "unknown"; }
        catch (InvalidOperationException) { return "unknown"; }
    }

    /// <summary>The lower middle of the reported figures, or null where none was reported.</summary>
    static long? Median(IEnumerable<long?> figures)
    {
        var sorted = figures.Where(f => f is not null).Select(f => f!.Value).Order().ToList();
        return sorted.Count == 0 ? null : sorted[(sorted.Count - 1) / 2];
    }

    /// <summary>Attempts that started in the window, oldest first.</summary>
    public List<AiAttempt> Between(DateTimeOffset from, DateTimeOffset to) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM ai_attempt WHERE started_at >= $from AND started_at < $to ORDER BY started_at, rowid",
            ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        using var r = c.ExecuteReader();
        var list = new List<AiAttempt>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    static AiAttempt Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        StartedAt = Sql.Time(r.GetValue(1)),
        Runtime = Sql.S(r.GetValue(2)),
        RequestedModel = Sql.S(r.GetValue(3)),
        PricingBasis = Sql.S(r.GetValue(4)),
        ReservedCost = Sql.Dec(r.GetValue(5)),
        State = Enum.TryParse<AiAttemptState>(r.GetString(6), out var s) ? s : AiAttemptState.LOST,
        EndedAt = Sql.TimeN(r.GetValue(7)),
        ExitCode = r.IsDBNull(8) ? null : r.GetInt32(8),
        InputTokens = Long(r, 9),
        CachedInputTokens = Long(r, 10),
        CacheWriteInputTokens = Long(r, 11),
        OutputTokens = Long(r, 12),
        ReasoningOutputTokens = Long(r, 13),
        EffectiveModel = Sql.S(r.GetValue(14)),
        Cost = Sql.DecN(r.GetValue(15)),
        UnpricedReason = Sql.S(r.GetValue(16)),
        Context = Sql.S(r.GetValue(17)),
        PolicyVersion = Sql.S(r.GetValue(18)),
        InputHash = Sql.S(r.GetValue(19)),
        Role = Sql.S(r.GetValue(20)),
        RuntimeProfile = Sql.S(r.GetValue(21)),
        SeatClass = Sql.S(r.GetValue(22)),
        Assurance = Sql.S(r.GetValue(23)),
        MandateId = Sql.S(r.GetValue(24)),
        AllowIn = Long(r, 25),
        AllowOut = Long(r, 26),
        TurnBound = Sql.S(r.GetValue(27)),
        BillingClass = BillingClasses.Of(Sql.S(r.GetValue(28)))
    };

    static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
