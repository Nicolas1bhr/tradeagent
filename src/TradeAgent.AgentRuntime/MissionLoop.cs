using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>What the AI card says the loop is doing. Four words, and the card shows exactly one.</summary>
public enum MissionState
{
    /// <summary>There is no agent to run a turn through: none has been started.</summary>
    Stopped,

    /// <summary>A turn is in flight right now.</summary>
    Working,

    /// <summary>Between turns, until <see cref="MissionStatus.NextTurnAt"/>.</summary>
    Waiting,

    /// <summary>The owner pressed Pause. It survives a restart.</summary>
    Paused
}

/// <summary>Everything the AI card draws, taken in one read so the card cannot show two moments at once.</summary>
public sealed record MissionStatus(
    MissionState State,
    DateTimeOffset? NextTurnAt,
    int Turns,
    int ConsecutiveErrors,
    string? LastTurnFirstLine)
{
    /// <summary>
    /// WHAT THE LOOP IS WAITING FOR, in the owner's words, or null where there is nothing to say.
    ///
    /// The card used to read "waiting until 14:32" and nothing else, which was the whole truth while
    /// the loop's only reason to wait was a delay the AI had asked for. Now that a turn happens
    /// because something happened, an owner looking at a quiet card needs to know whether it is
    /// quiet because nothing has happened or because something is broken — and "waiting for the
    /// next scheduled look" is the difference between those two readings.
    ///
    /// Init-only rather than a sixth positional member so that every existing site that builds one
    /// of these keeps compiling and keeps meaning what it meant.
    /// </summary>
    public string? WaitingFor { get; init; }

    /// <summary>
    /// WHICH COUNCIL ROLE THE CARD IS DESCRIBING — the one whose turn is in flight, or the one whose
    /// wake is next. Null where no wake queue is behind the loop, and then the card reads exactly as
    /// it did before the council existed.
    ///
    /// It is on the card because "working" with two roles running serially is an ambiguous word: an
    /// owner watching a quiet Research Director while Operations takes every turn cannot tell that
    /// from a council that is working, and the fix for the two is different.
    /// </summary>
    public string? Role { get; init; }
}

/// <summary>
/// The numbers the loop runs on. Defaults are the brief's: a turn at once unless the AI asked for a
/// delay, that delay capped at half an hour, an error backoff doubling from thirty seconds to the
/// same half hour, and twenty turns per CLI session before a fresh one.
/// </summary>
public sealed record MissionOptions
{
    /// <summary>Turns resumed into one CLI session before a fresh one is started. The files are the memory.</summary>
    public int TurnsPerSession { get; init; } = 20;

    /// <summary>The most the AI can ask to be left alone for, and the ceiling of the error backoff.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The first wait after a turn that ended badly. It doubles up to <see cref="MaxDelay"/>.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to stand off when the owner is mid-conversation and the session is busy.</summary>
    public TimeSpan BusyRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// HOW OFTEN THE AI IS WOKEN WHEN NOTHING HAS HAPPENED. <see cref="TimeSpan.Zero"/> is off, and
    /// then only a real event — the owner, material, a fill, an order, the day's renewal — starts a
    /// turn.
    ///
    /// It exists because "nothing happened" is not the same as "there is nothing worth doing": the
    /// research, the backtests and the journal are the job, and a market that has been quiet for an
    /// hour is not a reason to stop working on them. It is a setting rather than a constant because
    /// every tick costs a turn, and what a turn costs is the owner's business — see
    /// <c>TradeAgentSettings.MissionReviewMinutes</c>, where lowering it asks twice.
    /// </summary>
    public TimeSpan ReviewEvery { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// One completed run of the agent CLI, as the loop saw it end.
///
/// It carries the exit code, the wall clock, the raw stream and what the runtime SAID the turn used,
/// and nothing else on purpose: a number invented here — a token count, a cost, a "success" verdict
/// — would be a claim rather than a measurement.
///
/// <see cref="ExitCode"/> is -1 when no child produced one at all: the runtime was missing, the
/// process would not start, or the turn was cancelled. That is a failed turn for the loop's backoff,
/// and <see cref="Raw"/> then holds the reason rather than the program's output.
/// </summary>
public sealed record AgentTurnEnded(int ExitCode, TimeSpan Duration, string Raw, DateTimeOffset At)
{
    public bool Failed => ExitCode != 0;

    /// <summary>
    /// The tokens the runtime reported for this turn, or NULL when it reported none — which is a
    /// different fact from a turn that used nothing, and is why this is nullable rather than a zeroed
    /// <see cref="TurnUsage"/>. An init-only property with no default so that every existing site
    /// that builds one of these keeps compiling and keeps meaning what it meant: silence.
    /// </summary>
    public TurnUsage? Usage { get; init; }
}

/// <summary>
/// Everything outside the loop that one turn needs. An interface rather than a bag of delegates so
/// the loop can be driven a turn at a time in a test with no process, no database and no window.
/// </summary>
public interface IMissionHost
{
    /// <summary>The conversation turns run through, or null when no agent has been started.</summary>
    IAgentConversation? Conversation { get; }

    /// <summary>
    /// ONE COUNCIL ROLE'S CONVERSATION — its own CLI session, its own working directory, its own
    /// model. The loop runs exactly one of these at a time, which is what makes the council serial:
    /// two processes against one workspace and one account is a race with real money in it.
    ///
    /// The default is the single conversation above, so a host that knows nothing about roles — a
    /// test, a build with one agent prepared — keeps behaving as it did.
    /// </summary>
    IAgentConversation? ConversationFor(string role) => Conversation;

    /// <summary>
    /// Everything the app knows this turn, gathered at the moment the turn is composed. Asynchronous
    /// because positions and open orders come from the broker, and a turn boundary is exactly the
    /// right place to pay for a fresh reading rather than hand the AI a cached one.
    /// </summary>
    Task<MissionSituation> SituationAsync(CancellationToken ct);

    /// <summary>
    /// The same, for one role: its deliveries, its share of the day, its name. The default ignores
    /// the role, which is the honest answer for a host that has none.
    /// </summary>
    Task<MissionSituation> SituationAsync(string role, CancellationToken ct) => SituationAsync(ct);

    /// <summary>The agent's own directory — where <c>.tradeagent/next.json</c> is read from.</summary>
    string AgentHome { get; }

    /// <summary>
    /// One role's own directory. Each role asks to be woken in its OWN <c>next.json</c>: one file
    /// shared between them would let whichever ran last decide when the other works.
    /// </summary>
    string HomeFor(string role) => AgentHome;

    /// <summary>
    /// Something is in the owner's drop folder that no complete scan pass has recorded yet. Answered
    /// conservatively: an unreadable folder, or a pass that has never run, both read as changed,
    /// because the cost of a spurious yield is one scan and the cost of a missed one is a row that
    /// says the owner handed something over when nobody can show that.
    /// </summary>
    bool InboxChangedSinceLastPass { get; }

    /// <summary>Runs one material scan pass to completion. Called only when no turn is in flight.</summary>
    Task ScanAsync(CancellationToken ct);

    /// <summary>
    /// THE PERSISTED REASONS THE AI MAY TAKE A TURN. With one wired the loop turns only when an
    /// unconsumed event is due; with none it turns whenever it is asked to, which is what it did
    /// before this existed.
    ///
    /// Null is the honest default for a host with no database behind it — a test, a build with no AI
    /// prepared — and follows the same rule as <see cref="Spend"/> and <see cref="BeginTurn"/>: the
    /// restrictive reading belongs where the facts are recorded, and a loop that refused to work
    /// because nobody was writing events down would refuse for ever.
    /// </summary>
    MissionEventStore? Events => null;

    /// <summary>
    /// WHAT THE AI HAS COST TODAY AND WHAT IT IS ALLOWED TO COST. The loop reads this before every
    /// turn and takes none unless <see cref="AiSpendToday.AdmitsAnotherTurn"/> — which asks whether
    /// the ceiling has room for the turn about to run, rather than whether the money already gone
    /// has passed it.
    ///
    /// A default of <see cref="AiSpendToday.NotMetered"/> so that a host with no meter behind it —
    /// a test, a build with no AI prepared — keeps working and keeps turning. That default is
    /// deliberately the permissive one: the restrictive reading belongs where the number is
    /// measured, and a loop that stopped because nobody was counting would stop for ever.
    /// </summary>
    AiSpendToday Spend => AiSpendToday.NotMetered;

    /// <summary>
    /// THE SAME READING, NARROWED TO ONE ROLE: what that role has spent and committed today, and the
    /// slice of the owner's ceiling it may spend. The loop admits a turn only when BOTH gates say so
    /// — the day's ceiling and the role's share — which is what stops whichever role woke first
    /// taking every turn until midnight.
    ///
    /// The default is the whole-day reading, so a host with no roles behind it is bounded exactly as
    /// it was: <see cref="AiSpendToday.RoleAdmitsAnotherTurn"/> is true whenever no role is named.
    /// </summary>
    AiSpendToday SpendFor(string role) => Spend;

    /// <summary>
    /// Told ONCE, on the turn the cap is first reached, so the activity log gets one line rather
    /// than one every five seconds for the rest of the day. The loop knows the transition; the host
    /// owns the words and the log.
    /// </summary>
    void SpendCapReached(AiSpendToday spend) { }

    /// <summary>
    /// OPENS THE DURABLE RECORD OF THE TURN ABOUT TO RUN AND COMMITS ITS COST — called by the loop
    /// immediately before the CLI is started, and for no other reason.
    ///
    /// The order is the property. A record written after the turn prices a killed turn at nothing:
    /// the vendor did the work and billed for it, the app died before the usage event arrived, and
    /// the day's total never moved — so the allowance came back every time the AI was killed, and
    /// again at midnight. Written first, it cannot.
    ///
    /// A default of nothing, so a host with no meter behind it — a test, a build with no AI prepared
    /// — keeps turning, exactly as <see cref="Spend"/> defaults to unmetered.
    ///
    /// <paramref name="wakes"/> is the ids of the <c>mission_event</c> rows this turn is answering.
    /// They are marked consumed IN THE SAME TRANSACTION as the launch record, which is why they are
    /// handed to this call rather than written by the loop before or after it: two commits leave a
    /// window in which a kill hands the same wake to the next launch and the owner pays twice.
    ///
    /// Returns the attempt id, or null where nothing was recorded. The loop uses it to name the
    /// <c>self</c> event when the AI asks to be woken later, and to spend the wakes anyway when
    /// nothing could be written — a wake nobody consumed would buy an unbounded number of turns.
    /// </summary>
    string? BeginTurn(string prompt, IReadOnlyList<string> wakes) => null;

    /// <summary>
    /// The same, naming the role being charged. The role is on the launch record so the day's
    /// spending can be allocated at all; a bill nobody can allocate cannot be shared.
    /// </summary>
    string? BeginTurn(string prompt, IReadOnlyList<string> wakes, string role) => BeginTurn(prompt, wakes);

    /// <summary>
    /// EVERYTHING THE APP OWES THE FILESYSTEM AFTER A TURN, AND ON START: an unpublished report
    /// published, a committed delivery whose file is missing re-copied. Called by the loop at both
    /// moments, and by nothing else on this interface that can change what the AI is allowed to do.
    ///
    /// It is on the host rather than inside the loop because the relay needs the database and the
    /// role folders, and the loop is deliberately drivable with neither. A default of nothing keeps
    /// every existing host turning.
    ///
    /// <paramref name="role"/> is the role whose turn just ended and <paramref name="attempt"/> is
    /// its launch. The attempt no longer ATTRIBUTES anything — a file's provenance is the launch its
    /// NAME carries — but it still says which launch this pass belongs to, which is what lets the
    /// relay tell this turn's own output from a file dropped by a process the app is not accounting
    /// for. Null on start, when nobody's turn produced what is being reconciled.
    /// </summary>
    void Relay(string role, string? attempt) { }

    /// <summary>
    /// THE ID THE NEXT LAUNCH WILL CARRY, minted before the turn's message is composed so that the
    /// message can NAME it — <c>out/report-&lt;attempt&gt;.md</c> is how staged output is bound to
    /// the launch that produced it, and an agent cannot write that name without being told the id.
    ///
    /// It has to be minted here rather than returned by <see cref="BeginTurn"/>, because that call
    /// hashes the prompt: an id handed back afterwards could not be in the prompt it is hashed with.
    /// Null where nothing records launches, and then the Situation names no id and the relay
    /// quarantines whatever the turn writes — which is the honest outcome of a turn the app could
    /// not record at all.
    /// </summary>
    string? NextAttemptId() => null;

    /// <summary>
    /// THE ARTIFACT ONE DELIVERED TASK IS ABOUT, or null where it cannot be read. The loop asks for
    /// exactly the publications named by the wakes it is answering, so the turn is told about what
    /// it is being charged to act on and about nothing else.
    ///
    /// It is on the host because the publication table is the app's; the loop is deliberately
    /// drivable with no database at all, and a default of null keeps it so.
    /// </summary>
    MissionDelivery? Delivered(string publicationId) => null;
}

/// <summary>
/// The turn's message, written by the app and rendered here.
///
/// The order is the point. The owner's own words come FIRST — before the time, the mode, the account
/// or anything else — because a person who typed something while the AI was working is the one thing
/// in this block that is waiting for an answer. Everything below it is context the AI did not ask
/// for; a Situation that buries a question under nine lines of state is a Situation that gets it
/// answered nine turns late.
///
/// Nothing here grants anything. <see cref="Guidance"/> and <see cref="OwnerMessages"/> are the
/// owner's words and <see cref="NewMaterial"/> names files, and none of the three can change what the
/// AI is allowed to do: permission lives in the gateway, which the agent cannot reach.
/// </summary>
public sealed record MissionSituation
{
    /// <summary>
    /// WHICH ROLE THIS TURN IS, or null where no council is behind the loop. A role that is not told
    /// which one it is has to infer it from its own mission file, which is exactly the guess a
    /// serial council cannot afford: two roles, one account, and only one of them chairs.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// THE ID OF THIS TURN'S LAUNCH, and the name the turn must write its output under.
    ///
    /// The app binds staged output to the attempt that produced it by the FILE NAME, so a turn that
    /// is not told its own id cannot publish anything: the relay quarantines a file naming no launch
    /// it made. Null where nothing records launches — a test, a build with no meter — and then the
    /// line is absent rather than inventing an id nothing will recognise.
    /// </summary>
    public string? Attempt { get; init; }

    public DateTimeOffset LocalTime { get; init; }
    public string Mode { get; init; } = "";
    public bool ExecutionAvailable { get; init; }
    public string? ExecutionBlockedReason { get; init; }
    public string? Account { get; init; }
    public IReadOnlyList<string> Positions { get; init; } = [];
    public int OpenOrders { get; init; }
    public int UnconfirmedRequests { get; init; }

    /// <summary>What has appeared in <c>../inbox</c> since the last turn, in the owner's file names.</summary>
    public IReadOnlyList<string> NewMaterial { get; init; } = [];

    /// <summary>The Guidance box on the Dashboard. Free text, included in every turn, never permission.</summary>
    public string? Guidance { get; init; }

    /// <summary>What the owner typed in the chat while the AI was working. Rendered first.</summary>
    public IReadOnlyList<string> OwnerMessages { get; init; } = [];

    /// <summary>
    /// WHAT THE APP DELIVERED TO THIS ROLE, AND CHARGED IT A TURN TO READ. The chair's are the
    /// Research Director's reports; Research's are the chair's briefs.
    ///
    /// They are the wakes of THIS turn and nothing else — every one of them is an artifact this turn
    /// is being paid to act on, and a role handed everything it had ever been sent would re-read the
    /// whole correspondence every turn at the owner's expense. The text is here as well as in the
    /// file so the turn does not have to spend a tool call opening it, and the id is here so what it
    /// says about the report can be tied to the artifact afterwards.
    ///
    /// Rendered after the owner's words and before everything else. The owner keeps first place:
    /// a person waiting for an answer outranks another agent's report.
    /// </summary>
    public IReadOnlyList<MissionDelivery> Deliveries { get; init; } = [];

    /// <summary>
    /// WHY THIS TURN IS HAPPENING, in the words of the events that caused it.
    ///
    /// A turn used to have no cause at all: the previous one ended, so this one started. An AI told
    /// nothing about why it is awake has to work that out by looking at everything, every turn,
    /// which is both the expensive way and the way that misses the one thing that changed. Empty
    /// only where no wake queue is behind the loop.
    /// </summary>
    public IReadOnlyList<string> Wakes { get; init; } = [];

    /// <summary>
    /// WHAT THE AI HAS COST ITS OWNER TODAY. The other half of the sentence it was given as its
    /// mission — make at least enough to pay for yourself — and an AI told to cover its own costs
    /// without being told what they are is being asked to guess at half the arithmetic.
    /// </summary>
    public AiSpendToday Spend { get; init; } = AiSpendToday.NotMetered;

    /// <summary>
    /// WHAT THE TRADING HAS LOST TODAY, against what the owner allows it to lose.
    ///
    /// Beside <see cref="Spend"/> because they are the two halves of the same sentence the AI was
    /// given as its mission — make at least enough to pay for yourself — and because this one is the
    /// half that can STOP it: reaching the day's budget refuses every order that could increase
    /// exposure. An AI that is not told the figure will plan a day it is not going to be allowed to
    /// have, and will read the refusals as the software being broken.
    /// </summary>
    public LossToday Loss { get; init; } = LossToday.NotEnforced;

    /// <summary>
    /// WHAT HISTORY THE APP HOLDS, in one line, or the sentence that says there is none.
    ///
    /// An AI told to do its own research and given no idea whether there is any data to research
    /// spends its first turn finding out — measured on 2026-09-07, when the loop's first run on a
    /// screen spent a turn discovering what the simulator was. The line is short on purpose: it says
    /// what there is and where to get it, and `trade data list` says the rest.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// The sentence that ends every turn's message. It is the whole of what makes the loop a mission
    /// rather than a cron job: the AI is told where its memory is and that keeping it is part of
    /// finishing, because a fresh CLI session starts every <see cref="MissionOptions.TurnsPerSession"/>
    /// turns and the files are the only thing that crosses that boundary.
    /// </summary>
    public const string Continue =
        "Continue your mission. Your memory is your files: read `PLAN.md` and `JOURNAL.md` first and " +
        "update them before you finish.";

    public string Text()
    {
        var b = new StringBuilder();
        b.AppendLine("## Situation").AppendLine();

        if (OwnerMessages.Count > 0)
        {
            b.AppendLine("**The owner typed this while you were working. Deal with it first.**").AppendLine();
            foreach (var m in OwnerMessages)
                foreach (var line in m.Replace("\r\n", "\n").Split('\n'))
                    b.Append("> ").AppendLine(line);
            b.AppendLine();
        }

        // THE OTHER ROLE'S WORK, BELOW THE OWNER AND ABOVE EVERYTHING ELSE. The app put the file in
        // `in/` and is charging this turn for it; quoting it here is what makes the turn able to act
        // on it without spending a tool call, and naming the id is what lets what it decides be tied
        // back to the artifact it decided about.
        foreach (var d in Deliveries)
        {
            b.AppendLine($"**{d.Headline()}** It is in `{WorkspaceBuilder.InDir}/{d.Id}.md`.").AppendLine();
            foreach (var line in d.Text.Replace("\r\n", "\n").TrimEnd().Split('\n'))
                b.Append("> ").AppendLine(line);
            b.AppendLine();
        }

        // THE CAUSE, ABOVE THE STATE AND BELOW THE OWNER. The owner's words keep their first place —
        // a person waiting for an answer outranks everything — and then the AI is told what woke it,
        // because that is the one line that tells it where to look first.
        if (Wakes.Count > 0)
            b.AppendLine($"- Why you are awake: {string.Join("; ", Wakes)}");

        if (Role is { Length: > 0 } role)
            b.AppendLine($"- You are the {CouncilRoles.Title(role)}.");

        // THE ID THE TURN'S OUTPUT HAS TO CARRY. Above the state lines because it is an instruction
        // rather than a fact about the account, and spelled out with the file name because a bare id
        // is a fact the turn has to work out what to do with.
        if (Attempt is { Length: > 0 } attempt)
            b.AppendLine($"- This turn is attempt `{attempt}`. Anything you publish this turn must be "
                         + $"named after it — `{WorkspaceBuilder.OutDir}/{OutputName(Role, attempt)}` — "
                         + "or TradeAgent cannot tell which turn wrote it and will not publish it.");

        b.AppendLine($"- Local time: {LocalTime.LocalDateTime:yyyy-MM-dd HH:mm}");
        b.AppendLine($"- Trading mode: {Mode}");
        b.AppendLine(ExecutionAvailable
            ? "- execution_available: true"
            : $"- execution_available: false — {ExecutionBlockedReason ?? "no reason was given"}");
        b.AppendLine($"- Account: {Account ?? "not selected"}");
        b.AppendLine($"- Open orders: {OpenOrders}, unconfirmed: {UnconfirmedRequests}");
        b.AppendLine(Positions.Count == 0
            ? "- Positions: none"
            : $"- Positions: {string.Join("; ", Positions)}");
        if (SpendLine(Spend) is { } spend) b.AppendLine($"- {spend}");
        if (ShareLine(Spend) is { } share) b.AppendLine($"- {share}");
        if (Loss.Line() is { } loss) b.AppendLine($"- {loss}");
        if (!string.IsNullOrWhiteSpace(Data)) b.AppendLine($"- {Data}");
        if (NewMaterial.Count > 0)
            b.AppendLine($"- New in `../inbox` since your last turn: {string.Join(", ", NewMaterial)}");
        if (!string.IsNullOrWhiteSpace(Guidance))
            b.AppendLine($"- The owner's guidance: {Guidance.Trim()}");

        b.AppendLine().AppendLine(Continue);
        return b.ToString();
    }

    /// <summary>
    /// THE FILE NAME THIS TURN'S OUTPUT MUST CARRY. The app decides it, from the role and the
    /// launch: a role does not choose what its output IS (<see cref="CouncilRelay.KindFor"/>) and it
    /// does not choose which turn it is attributed to either.
    /// </summary>
    public static string OutputName(string? role, string attempt) =>
        CouncilRelay.Pattern(CouncilRelay.KindFor(role ?? CouncilRoles.Default)).Replace("*", attempt);

    /// <summary>
    /// THE DATA LINE. A function beside <see cref="SpendLine"/> so it can be read back without a
    /// running loop, and so the three cases are visibly three: history, no history, and history the
    /// ledger has stopped standing behind.
    /// </summary>
    public static string DataLine(DatasetRecord? set)
    {
        if (set is null)
            return "Data: no dataset yet. Your owner collects history in TradeAgent, on the Settings page "
                   + "under Market data; there is no command you can run that does it.";

        if (set.State == DatasetState.REJECTED)
            return $"Data: the {set.Pair} {set.Interval} dataset is REJECTED and serves no bars — "
                   + $"{set.RejectedReason}. Your owner has to collect it again.";

        var period = set.FirstBar is { } first && set.LastBar is { } last
            ? $"{first.UtcDateTime:yyyy-MM-dd} → {last.UtcDateTime:yyyy-MM-dd}"
            : "no bars";

        return $"Data: {set.Pair} {set.Interval}, {period}, {set.Bars:N0} bars, {set.Gaps:N0} gaps — "
               + "`trade data list` for its provenance, `trade data bars` for the bars. They are hypothesis "
               + "evidence and establish no fill.";
    }

    /// <summary>
    /// THE COST LINE, or null when there is nothing measured to say. A function so it can be read
    /// back without a running loop, and so the three cases are visibly three.
    ///
    /// The unpriced case is spelled out rather than shortened to a number, because a "0.00 today"
    /// beside a working AI is the one reading that is actively misleading: it says the turns were
    /// free when what happened is that nobody could price them. It also says the limit is not
    /// holding anything back, so the AI does not plan its day against a ceiling that is not there.
    /// </summary>
    public static string? SpendLine(AiSpendToday spend)
    {
        if (!spend.Metered) return null;

        var turns = spend.Turns == 1 ? "1 turn" : $"{spend.Turns} turns";

        if (!spend.CanPrice)
            return $"What you have cost today: {turns}, price unknown — {spend.WhyNoPrice}. "
                   + $"Your owner's daily limit of {Money(spend.Cap, spend.Currency)} cannot be applied to that, "
                   + "so nothing is holding your spending back but you.";

        if (spend.CapCannotFundATurn)
            return $"What you have cost today: {Money(spend.Spent, spend.Currency)} of a "
                   + $"{Money(spend.Cap, spend.Currency)} daily limit, over {turns}. One turn can cost up to "
                   + $"{Money(spend.NextTurnReservation, spend.Currency)}, which is more than that whole limit, so "
                   + "your owner has to raise it or choose a cheaper model before you can work again. Midnight "
                   + "will not change it.";

        var spent = $"What you have cost today: {Money(spend.Spent, spend.Currency)} of a "
                    + $"{Money(spend.Cap, spend.Currency)} daily limit, over {turns}";

        // The AI is told the figure is an upper bound in the same words the owner reads. An agent
        // asked to cover what it costs and given a number without that word would plan against a
        // bill it has not actually run up — and would be wrong in the expensive direction if the
        // label were ever dropped, since it is the ONLY thing separating a ceiling from a receipt.
        if (spend.Estimated is { } estimated) spent += $" — {estimated}";

        if (spend.UnpricedTurns > 0)
            spent += $" — and {spend.UnpricedTurns} of those could not be priced, so the real figure is higher";

        if (spend.Reserved > 0m)
            spent += $" — {Money(spend.Reserved, spend.Currency)} of that is committed to turns whose "
                     + "usage has not come back";

        // THE CEILING IS APPLIED BEFORE A TURN, so the AI is told that rather than being told it is
        // in the middle of its last one. The distinction is not decoration: an agent that believes
        // the current turn is its last finishes what it is doing, and an agent that knows the next
        // one will be refused writes down where it got to.
        return spent + (spend.AdmitsAnotherTurn
            ? "."
            : ". You are at the limit, and it is applied BEFORE a turn runs: the next one is refused "
              + "rather than cut short, and nothing starts again until midnight.");
    }

    /// <summary>
    /// THIS ROLE'S SLICE OF THE DAY, or null where the reading is about no role in particular or
    /// nothing can be priced.
    ///
    /// Its own line rather than a clause inside <see cref="SpendLine"/>, because the two are
    /// different stops with different repairs: the day's ceiling is the owner's and only they can
    /// raise it, while a share that has run out is the chair reallocating what is left. A role told
    /// only the day's figure would plan against money that belongs to the other one.
    /// </summary>
    public static string? ShareLine(AiSpendToday spend)
    {
        if (spend is not { Metered: true, Role: { Length: > 0 } role } || !spend.CanPrice) return null;

        var line = $"Your share of that limit, as the {CouncilRoles.Title(role)}: "
                   + $"{Money(spend.RoleRemaining, spend.Currency)} left of "
                   + $"{Money(spend.RoleCap, spend.Currency)}";

        return line + (spend.RoleAdmitsAnotherTurn
            ? "."
            : ". Your share is spent, so your next turn is refused even though the day's limit has "
              + "room. Nothing starts again until the Operations Director reallocates, or midnight.");
    }

    /// <summary>
    /// An amount with its currency, or without one where <c>costs.json</c> named none. It moved to
    /// <see cref="Labels.Money"/> in Core when the gateway's loss budgets started printing money
    /// too: one formatter, because two of them is how one screen ends up showing "5 USD" and
    /// "5.0000" for the same kind of figure. Kept here because every call site already says it.
    /// </summary>
    public static string Money(decimal amount, string currency) => Labels.Money(amount, currency);
}

/// <summary>
/// ONE ARTIFACT THE APP DELIVERED TO THE ROLE TAKING THIS TURN — its id, what kind of thing it is,
/// which role produced it, and its text.
///
/// The id is the publication's, which is a hash of the content: it names the file in <c>in/</c> and
/// it is what a later decision can be tied back to. Nothing here is authority — a report is another
/// agent's account of itself, exactly as the inbox is the owner's material, and neither grants
/// anything.
/// </summary>
public sealed record MissionDelivery(string Id, string Kind, string From, string Text)
{
    /// <summary>The sentence that introduces it, naming the kind and who sent it.</summary>
    public string Headline() =>
        $"A {Kind} from the {CouncilRoles.Title(From)}, delivered to you by TradeAgent.";
}

/// <summary>
/// What the AI asks for in <c>.tradeagent/next.json</c>. One field, so that a runtime writing it by
/// hand cannot get it wrong in an interesting way.
/// </summary>
public sealed record MissionNext(double? AfterSeconds);

/// <summary>
/// Whether the owner's drop folder holds anything a complete scan pass has not seen yet.
///
/// Deliberately a directory read rather than a query against the ledger: the question is "is there
/// something the ledger does not know about", and asking the ledger cannot answer it. It errs
/// towards yes — see <see cref="IMissionHost.InboxChangedSinceLastPass"/> — because a wrong yes
/// costs one scan pass and a wrong no costs an attestation.
/// </summary>
public static class MissionInbox
{
    /// <summary>Entries walked before the answer is simply yes. A drop bigger than this needs a pass anyway.</summary>
    public const int Limit = 5_000;

    public static bool ChangedSince(string workspaceRoot, DateTimeOffset? lastPassAt)
    {
        if (lastPassAt is null) return true;
        var inbox = Path.Combine(workspaceRoot, MaterialScanner.InboxDir);

        try
        {
            if (!Directory.Exists(inbox)) return false;
            var seen = 0;
            foreach (var file in Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories))
            {
                if (++seen > Limit) return true;
                // Creation as well as last write: a file COPIED into the folder keeps the source's
                // write time, which is routinely older than the last pass, and that is the ordinary
                // way material arrives here.
                if (File.GetLastWriteTimeUtc(file) >= lastPassAt.Value.UtcDateTime) return true;
                if (File.GetCreationTimeUtc(file) >= lastPassAt.Value.UtcDateTime) return true;
            }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}

/// <summary>
/// THE AI DOES NOT STOP. Turns run back to back for as long as the owner has said it may work on its
/// own; each one is handed a <see cref="MissionSituation"/> and the sentence that points it at its
/// own files, and the next one starts at once, or after the delay the AI asked for, or after a
/// backoff while turns keep ending badly.
///
/// <b>What this loop is not.</b> It holds no authority of any kind. It does not consult the kill
/// switch and cannot lift it, it does not choose a mode, and it approves nothing: STOP AI TRADING
/// removes the AI's permission to trade and leaves the loop running, which is the correct pair —
/// there is plenty of work (research, backtesting, writing strategies, keeping the journal) that
/// wants doing precisely while execution is off. Nothing here is reachable from the agent-facing
/// pipe.
///
/// <b>Why it yields to the scanner.</b> An inbox sighting can be attested to the account owner only
/// across a window with no agent process alive (BUILD-STATUS, U-batch-2 finding 5). A loop running
/// turns back to back leaves no such window ever, so every file the owner drops would be recorded
/// <see cref="MaterialOrigin.InboxUnattested"/> — the software forgetting how to tell "the owner
/// gave me this" from "this was in their folder", as a side effect of working harder. So a pass runs
/// after every turn, which closes the scanner's window behind that turn, and the next turn waits for
/// a pass whenever something new is sitting in the drop folder. Both halves are needed: the pass
/// after the turn is what makes the pass before the next one able to attest.
/// </summary>
public sealed class MissionLoop
{
    readonly IMissionHost _host;
    readonly MissionOptions _options;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly Lock _gate = new();

    CancellationTokenSource? _cts;
    Task? _running;

    int _turns;
    int _sessionTurns;
    int _consecutiveErrors;
    string? _lastFirstLine;
    bool _working;
    DateTimeOffset? _nextTurnAt;
    string? _waitingFor;

    /// <summary>The role the card is describing: the turn in flight, or the next wake's owner.</summary>
    string? _role;

    /// <summary>The sleep in flight, so <see cref="Wake"/> can end it. Null while a turn is running.</summary>
    CancellationTokenSource? _waitCts;

    /// <summary>A wake that arrived with nothing asleep to interrupt. Spent by the next wait.</summary>
    bool _nudged;

    /// <summary>Whether the owner has already been told about THIS spell of being over the cap.</summary>
    bool _reportedCap;

    public MissionLoop(IMissionHost host, MissionOptions? options = null,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _host = host;
        _options = options ?? new MissionOptions();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Raised whenever <see cref="Status"/> would read differently. The card redraws on it.</summary>
    public event Action? Changed;

    /// <summary>True while the loop is taking turns of its own accord.</summary>
    public bool Running => _running is { IsCompleted: false };

    public MissionStatus Status
    {
        get
        {
            // Asked OUTSIDE the lock. The host's answer comes from the composition root, and holding
            // this loop's lock across a call into it is how two locks that never met become a
            // deadlock in the next unit that adds one.
            var hasAgent = _host.Conversation is not null;

            lock (_gate)
            {
                // STOPPED OUTRANKS EVERYTHING, because with no conversation there is nothing to take
                // a turn: a loop started before the AI was would otherwise sit reporting "waiting
                // until 14:32" over a turn that cannot happen, and every number beside it would be
                // describing that same turn.
                var state =
                    !hasAgent ? MissionState.Stopped :
                    _working ? MissionState.Working :
                    !Running ? MissionState.Paused :
                    MissionState.Waiting;
                return new MissionStatus(state, state == MissionState.Waiting ? _nextTurnAt : null,
                    _turns, _consecutiveErrors, _lastFirstLine)
                {
                    WaitingFor = state == MissionState.Waiting ? _waitingFor : null,
                    Role = _role
                };
            }
        }
    }

    /// <summary>
    /// Lets the AI work on its own. Idempotent: pressing it twice does not start two loops, which
    /// would be two agent processes against one workspace and one account.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            _consecutiveErrors = 0;
            _running = Task.Run(() => LoopAsync(_cts.Token));
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops taking new turns and cancels the one in flight. One press, always: this only ever takes
    /// work away, and a Pause that argued with the person pressing it would be the wrong shape.
    /// </summary>
    public async Task PauseAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_gate) { cts = _cts; run = _running; _cts = null; _running = null; }

        if (cts is not null) { await cts.CancelAsync(); cts.Dispose(); }
        if (_host.Conversation is { } c) await c.CancelAsync();
        if (run is not null) { try { await run; } catch (Exception) { /* it was cancelled */ } }

        lock (_gate) { _working = false; _nextTurnAt = null; _waitingFor = null; _role = null; }
        Changed?.Invoke();
    }

    /// <summary>
    /// ENDS THE SLEEP NOW, because something has just been written to the wake queue.
    ///
    /// Without it a message typed at 10:01 would sit unanswered until the next scheduled look, which
    /// with the shipped half-hour tick is a person watching a quiet window for twenty-nine minutes.
    /// It grants nothing and starts nothing: the loop wakes, reads the queue exactly as it would
    /// have, and finds whatever is actually due — a raise that turned out to be a duplicate leaves
    /// it with nothing to do and it goes back to sleep.
    ///
    /// A wake that arrives while a turn is running is remembered rather than lost, so the queue is
    /// re-read the moment that turn ends.
    /// </summary>
    public void Wake()
    {
        CancellationTokenSource? cts;
        lock (_gate) { _nudged = true; cts = _waitCts; }
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the sleep ended on its own between the read and here */ }
    }

    async Task LoopAsync(CancellationToken ct)
    {
        var wait = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            if (wait > TimeSpan.Zero)
            {
                lock (_gate) _nextTurnAt = _now() + wait;
                Changed?.Invoke();
                try { await WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
            }

            try { wait = await TurnAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // A LOOP WHOSE WHOLE PURPOSE IS NOT TO STOP DOES NOT STOP ON A SURPRISE. Anything
                // reaching here is a turn that failed before it produced an AgentTurnEnded — a race
                // with the owner's own message on the Chat page, a runtime that vanished mid-turn —
                // so it is counted as a failed turn and backed off from exactly like any other.
                //
                // Dying here would leave the card reading "paused" over a decision nobody made, and
                // an owner who had gone to bed with the AI working would find it stopped with no
                // reason given anywhere. The climbing error count on the card is the visible signal.
                int errors;
                lock (_gate) { _working = false; errors = ++_consecutiveErrors; }
                Changed?.Invoke();
                wait = Backoff(errors);
            }
        }
    }

    /// <summary>
    /// Sleeps, or stops sleeping because <see cref="Wake"/> was called. Cancellation of the loop's
    /// own token still propagates — only a nudge is swallowed — so Pause is unaffected.
    /// </summary>
    async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            // Something arrived while the last turn was in flight. Do not sleep on it.
            if (_nudged) { _nudged = false; return; }
            _waitCts = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try { await _delay(wait, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* woken */ }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_waitCts, cts)) _waitCts = null; _nudged = false; }
            cts.Dispose();
        }
    }

    /// <summary>
    /// ONE TURN, START TO FINISH, and the wait before the next one.
    ///
    /// Public because this — not the timer around it — is the unit worth driving in a test: the
    /// yield to the scanner, the fresh session, the message, the run, and the pass that closes the
    /// scanner's window behind the turn all happen here, in this order, and the order is what the
    /// attestation depends on.
    /// </summary>
    public async Task<TimeSpan> TurnAsync(CancellationToken ct = default)
    {
        // ---- the wake, and WHOSE it is -----------------------------------------------------------
        // A TURN HAPPENS BECAUSE SOMETHING HAPPENED, and with nothing due nothing is launched. This
        // is what replaces the immediate re-turn: `AskedForDelay` answered Zero whenever the AI had
        // written no `next.json`, so the loop started another turn the instant one ended and the
        // only thing that ever stopped it was the day's cost ceiling — which is a bill, not a
        // reason. docs/COUNCIL.md rule 7: justified idleness launches no inference.
        //
        // The scheduled kinds are written first, per role, so an idle installation always has a next
        // wake to sleep until, and every one of them is DUE IN THE FUTURE — the loop scheduling its
        // own next look must never be what makes this one eligible.
        //
        // ONE ROLE PER TURN, AND ONE TURN AT A TIME. The council is serial: the role that has waited
        // longest goes first, and a role that has spent its slice of the day is stepped over rather
        // than being allowed to stop the other from working. Everything below this block — the
        // ceiling, the conversation, the record, the process — is that role's.
        var events = _host.Events;
        var role = CouncilRoles.Default;
        List<MissionEvent> wake = [];

        if (events is not null)
        {
            Schedule(events);
            var due = RolesDue(events);
            if (due.Count == 0) return Idle(events);

            // The first role whose own share AND the owner's ceiling both have room for a turn.
            var affordable = due.FirstOrDefault(r => Spend(r).AdmitsAnotherTurn);
            if (affordable is null)
            {
                // WHAT THE OWNER IS OWED AND CANNOT BE GIVEN, written down where they will read it.
                // The message stays unconsumed and is still owed a turn; this is the standing reason
                // it has not had one, and recording it costs nothing — which is the point, because
                // the thing that stopped the turn is that there is no money left to spend on one.
                BlockOwnerMessages(events, "the day's AI spending ceiling is reached, so no turn can "
                                           + "be taken until it resets");
                return CappedUntilMidnight(Spend(due[0])) ?? _options.BusyRetry;
            }
            role = affordable;
        }

        // ---- the day's ceiling -------------------------------------------------------------------
        // The AI's mission is to make at least enough to pay for itself and half of that sentence is
        // its own bill; turns run back to back for as long as the machine is on, so without this the
        // account is being charged with nobody watching. It removes no permission and touches no
        // order — it is the loop declining to spend more of the owner's money today.
        //
        // Asked again here rather than only above, because a host with no wake queue never reached
        // the block above at all and this is the only gate it has.
        if (CappedUntilMidnight(Spend(role)) is { } untilMidnight) return untilMidnight;

        var conversation = _host.ConversationFor(role);
        if (conversation is null) return _options.BusyRetry;

        // The owner is mid-conversation on the Chat page. Their turn is the one in flight; the
        // mission's waits rather than racing it, and what they typed is carried into the next one.
        if (conversation.Busy) return _options.BusyRetry;

        // ---- yield to the scanner ----------------------------------------------------------------
        // Nothing is running at this line: the previous turn's process has exited and this one has
        // not started. So a pass taken here measures a window that contains no agent and can attest
        // what it finds to the owner. Removing this is what makes A_file_the_owner_drops_between_
        // turns_is_still_recorded_as_theirs go red.
        if (_host.InboxChangedSinceLastPass) await _host.ScanAsync(ct);

        // That pass may have recorded material, which raises a wake of its own. Taking it NOW rather
        // than leaving it for the next turn is what keeps "the owner dropped a file" one turn: the
        // turn about to run is the one that should be told about it.
        if (events is not null)
        {
            wake = events.DueFor(role, _now());
            if (wake.Count == 0) return Idle(events);
        }

        // ---- a fresh CLI session every N turns ---------------------------------------------------
        // Resuming forever grows one context until the runtime refuses it or prices it absurdly. The
        // files are the memory, so a fresh session loses nothing the AI wrote down — and StopAsync
        // forgets the session without touching the workspace.
        if (_sessionTurns >= _options.TurnsPerSession)
        {
            await conversation.StopAsync();
            _sessionTurns = 0;
        }

        // THE OWNER'S WORDS COME OFF THE TABLE FIRST AND THE IN-MEMORY QUEUE SECOND. With a wake
        // queue behind it `AgentSession.Queue` writes the row instead of the list, so exactly one of
        // the two holds any given message; without one the list is still where they are, and this
        // reads the same as it always did.
        // THE LAUNCH ID, MINTED BEFORE THE MESSAGE IT GOES INTO. The turn has to be told the id it
        // must name its output after, and BeginTurn hashes the prompt — so an id handed back by
        // that call could never be in the prompt it is hashed with. Minting is not recording:
        // nothing is committed until BeginTurn below, and an id nobody launched under names no row.
        var attemptId = Mint();

        var situation = (await _host.SituationAsync(role, ct)) with
        {
            Role = role,
            Attempt = attemptId,
            OwnerMessages = [.. OwnerWords(wake), .. conversation.TakeTyped()],
            Deliveries = Delivered(wake),
            Wakes = Reasons(wake)
        };
        var prompt = situation.Text();

        lock (_gate) { _working = true; _nextTurnAt = null; _waitingFor = null; _role = role; }
        Changed?.Invoke();

        // ---- the record and the commitment, BEFORE the process ------------------------------------
        // The one line in this method whose position is the whole of what it does. Everything above
        // is preparation and nothing has been spent yet; the next statement starts a program that
        // bills somebody. A record written on the far side of it is a record that a killed turn
        // never gets, and a turn nobody recorded is a turn the daily ceiling never sees.
        //
        // It is handed the SAME string that is sent, so the hash on the row is of the prompt that
        // actually entered the model request rather than of a second rendering of the same facts.
        // It also carries the wakes this turn is answering, which are marked consumed in the same
        // commit — see IMissionHost.BeginTurn.
        var wakeIds = wake.Select(e => e.Id).ToArray();
        var recorded = _host.BeginTurn(prompt, wakeIds, role);
        attemptId = recorded ?? attemptId;

        // A LAUNCH NOBODY COULD RECORD STILL SPENDS ITS WAKE. Leaving the events unconsumed because
        // the attempt row could not be written would hand the same wake to the next turn, and the
        // next, for as long as the database stayed unwritable — an unbounded number of paid turns
        // out of one reason to take one.
        if (recorded is null && events is not null && wakeIds.Length > 0)
            try { events.Consume(wakeIds, Unrecorded, _now()); }
            catch (Exception) { /* the same failure that lost the attempt row; the turn still runs */ }

        AgentTurnEnded? ended = null;
        void Watch(AgentTurnEnded e) => ended = e;
        conversation.TurnEnded += Watch;
        try { await conversation.SendMissionAsync(prompt, ct); }
        // A turn that threw its way out — cancelled, or a runtime that will not start — must not
        // leave the card reading "working" for ever over a turn that is not happening.
        finally { conversation.TurnEnded -= Watch; lock (_gate) _working = false; }

        // ---- close the scanner's window behind this turn -----------------------------------------
        // The pass above can only attest because a pass ran AFTER the previous turn: the window a
        // pass measures starts at the previous pass, so one taken while the loop is idle is what
        // moves that start past the agent's last breath. Without this line the pass before the next
        // turn still measures a window this turn is inside, and attests nothing.
        await _host.ScanAsync(ct);

        // ---- and what the turn left in `out/` ----------------------------------------------------
        // After the turn rather than before it, because the file this publishes is the one the turn
        // just wrote. It runs whether the turn succeeded or failed: a report written by a turn that
        // then fell over is still the role's work, and the publication is idempotent by content, so
        // running it after every turn costs a hash of a small file.
        Relay(role, attemptId);

        var failed = ended?.Failed ?? true;
        int errors;
        lock (_gate)
        {
            _turns++;
            _sessionTurns++;
            errors = _consecutiveErrors = failed ? _consecutiveErrors + 1 : 0;
            _lastFirstLine = FirstLineOfLastReply(conversation) ?? _lastFirstLine;
        }
        Changed?.Invoke();

        // WHAT BECAME OF EACH WAKE, written after the turn rather than assumed by it. An owner's
        // message whose turn failed is re-raised once, so a runtime that fell over does not swallow
        // the one thing in the block that was waiting for an answer.
        if (events is not null) Settle(events, wake, failed, ended);

        return failed ? Backoff(errors) : NextWait(events, attemptId, role);
    }

    /// <summary>
    /// What <c>consumed_by</c> reads when the launch record could not be written. It is deliberately
    /// not an attempt id: nothing in <c>ai_attempt</c> will ever carry it, and a row pointing at an
    /// attempt that does not exist is the honest shape of "this turn ran and was not recorded".
    /// </summary>
    public const string Unrecorded = "unrecorded";

    /// <summary>
    /// THE TWO WAKES THE LOOP SCHEDULES FOR ITSELF, and both are always due in the FUTURE — the loop
    /// arranging its next look must never be what makes this moment eligible, or "no eligible event"
    /// could never be reached and the queue would be a clock with extra steps.
    ///
    /// One pending row of each kind at a time. Without that check every early wake would leave
    /// another review behind it, and a burst of owner messages would buy a trickle of paid reviews
    /// over the following half hour.
    /// </summary>
    void Schedule(MissionEventStore events)
    {
        var now = _now();
        try
        {
            // PER ROLE, both of them. One shared review tick would be consumed by whichever role
            // reached it first, so the other would only ever run when a real event named it — a
            // Research Director that is never scheduled at all on a quiet day.
            foreach (var role in CouncilRoles.All)
            {
                if (_options.ReviewEvery > TimeSpan.Zero
                    && !events.HasUnconsumed(MissionEventKind.Review, role))
                {
                    var due = now + _options.ReviewEvery;
                    events.RaiseDue(MissionEventIds.ForRole(MissionEventIds.Review(due), role),
                        MissionEventKind.Review, now, due, role: role);
                }

                // NOT A SETTING. The allowance the AI works under is the owner's day, and the moment
                // it becomes a new one is a fact about the world rather than a preference — a role
                // that stopped at its share has to be told when it may work again, and nothing else
                // in the queue is going to say so on a quiet night.
                if (!events.HasUnconsumed(MissionEventKind.Renewal, role))
                {
                    var midnight = LocalMidnightAfter(now);
                    events.RaiseDue(MissionEventIds.ForRole(MissionEventIds.Renewal(midnight), role),
                        MissionEventKind.Renewal, now, midnight, role: role);
                }
            }
        }
        catch (Exception)
        {
            // A queue that cannot be written must not stop the loop. The turn that follows reads
            // whatever is there, and a missing scheduled wake costs a look, not the mission.
        }
    }

    /// <summary>
    /// Records, on every DUE owner message nothing has settled, the reason no turn can take it.
    ///
    /// It writes a disposition and leaves the row unconsumed, so the message is still owed a turn and
    /// gets one the moment the day turns over. Never throws: a disposition is a record, and a loop
    /// that stopped over one would turn bookkeeping into an AI that stopped working.
    /// </summary>
    void BlockOwnerMessages(MissionEventStore events, string why)
    {
        try
        {
            foreach (var e in events.Due(_now()))
                if (e.Kind == MissionEventKind.Owner)
                    events.SettleWithoutTurn(e.Id, MissionEventDisposition.Blocked, why);
        }
        catch (Exception) { /* the same queue the turn could not be afforded out of */ }
    }

    /// <summary>
    /// The roles with a due wake, longest-waiting first, or nothing at all when the queue cannot be
    /// read. A queue that throws is not a reason to launch a process nobody asked for.
    /// </summary>
    List<string> RolesDue(MissionEventStore events)
    {
        try { return events.RolesDue(_now()); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// THE ID THE NEXT LAUNCH WILL CARRY, or null where nothing records launches. Never throws: a
    /// host whose ledger cannot be reached gives the turn no id, the turn's output is quarantined
    /// rather than published under a launch nobody recorded, and the loop keeps working.
    /// </summary>
    string? Mint()
    {
        try { return _host.NextAttemptId(); }
        catch (Exception) { return null; }
    }

    /// <summary>This role's reading of today's spending, or the whole day's where the host has none.</summary>
    AiSpendToday Spend(string role)
    {
        try { return _host.SpendFor(role); }
        catch (Exception) { return _host.Spend; }
    }

    /// <summary>
    /// Publishes what the turn left behind and re-copies anything a crash lost. Never throws: the
    /// relay is bookkeeping, and a loop that stopped over it would turn a lost copy into an AI that
    /// stopped working.
    /// </summary>
    void Relay(string role, string? attempt)
    {
        try { _host.Relay(role, attempt); }
        catch (Exception) { /* the next turn reconciles; the host has already logged it */ }
    }

    /// <summary>The next LOCAL midnight, on the offset in force at that boundary.</summary>
    static DateTimeOffset LocalMidnightAfter(DateTimeOffset now)
    {
        var start = now.ToLocalTime().Date.AddDays(1);
        return new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
    }

    /// <summary>
    /// NOTHING IS DUE, so nothing is launched and the loop sleeps until something is. The card is
    /// left reading "waiting" with the reason beside it: an owner looking at a quiet window needs
    /// the difference between "nothing has happened yet" and "this is broken".
    /// </summary>
    TimeSpan Idle(MissionEventStore events)
    {
        DateTimeOffset? next;
        string? why;
        string? whose;
        try
        {
            next = events.NextDueAt();
            why = Waiting(events.NextKind());
            whose = events.NextRole();
        }
        catch (Exception) { next = null; why = null; whose = null; }

        lock (_gate) { _working = false; _nextTurnAt = next; _waitingFor = why; _role = whose; }
        Changed?.Invoke();

        if (next is null) return _options.MaxDelay;
        var wait = next.Value - _now();
        return wait <= TimeSpan.Zero ? _options.BusyRetry
            : wait > _options.MaxDelay ? _options.MaxDelay
            : wait;
    }

    /// <summary>
    /// The wait after a turn that ended cleanly: the AI's own request becomes a <c>self</c> event,
    /// and then the queue decides, exactly as it does when the loop was idle. The file is read and
    /// deleted either way — a request left on disk would make one "leave me half an hour" into every
    /// turn being half an hour apart for ever.
    /// </summary>
    TimeSpan NextWait(MissionEventStore? events, string? attemptId, string role)
    {
        var asked = AskedForDelay(role);
        if (events is null) return asked;

        if (asked > TimeSpan.Zero)
        {
            var now = _now();
            // The role's OWN wake. An attempt id already names one role, but the fallback does not,
            // and a self-wake with no role on it would be answered by the chair — one role asking to
            // be woken and the other one waking.
            var id = MissionEventIds.Self(attemptId is { Length: > 0 } a
                ? a
                : now.UtcDateTime.ToString("yyyyMMddHHmmssfff"));
            try { events.RaiseDue(id, MissionEventKind.Self, now, now + asked, role: role); }
            catch (Exception) { /* the schedule below still gives the loop something to wake for */ }
        }

        Schedule(events);
        return Idle(events);
    }

    /// <summary>
    /// The owner's own words, off the durable queue, oldest first. The payload is the only copy
    /// once an event exists for a message — see <see cref="MissionOwnerMessage"/> — so a row whose
    /// payload cannot be read is skipped rather than rendered as an empty quotation.
    /// </summary>
    static IEnumerable<string> OwnerWords(IEnumerable<MissionEvent> wake)
    {
        foreach (var e in wake.Where(e => e.Kind == MissionEventKind.Owner))
        {
            string? text = null;
            try { text = Json.Read<MissionOwnerMessage>(e.Payload ?? "")?.Text; }
            catch (Exception) { /* an unreadable payload is not a message */ }
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    /// <summary>
    /// The artifacts THIS turn's wakes are about, in the order they arrived. A wake whose payload
    /// cannot be read, or whose publication the host cannot produce, is skipped rather than rendered
    /// as an empty quotation — the wake still says a report arrived, and the file is still in
    /// <c>in/</c>, so the turn is not left believing nothing happened.
    /// </summary>
    IReadOnlyList<MissionDelivery> Delivered(IEnumerable<MissionEvent> wake)
    {
        var list = new List<MissionDelivery>();
        foreach (var e in wake.Where(e => e.Kind is MissionEventKind.Report or MissionEventKind.Brief))
        {
            try
            {
                if (Json.Read<MissionTask>(e.Payload ?? "")?.Publication is not { Length: > 0 } id) continue;
                if (_host.Delivered(id) is { } d) list.Add(d);
            }
            catch (Exception) { /* an unreadable payload is not a delivery */ }
        }
        return list;
    }

    /// <summary>Why the turn is happening, one phrase per KIND — six fills are one reason, not six.</summary>
    static string[] Reasons(IEnumerable<MissionEvent> wake) =>
        [.. wake.Select(e => Reason(e.Kind)).Distinct()];

    static string Reason(string kind) => kind switch
    {
        MissionEventKind.Owner => "your owner typed something",
        MissionEventKind.Inbox => "new material arrived in `../inbox`",
        MissionEventKind.Fill => "an order filled",
        MissionEventKind.Order => "an order reached a final state",
        MissionEventKind.Renewal => "the day turned over, so your spending allowance is a new one",
        MissionEventKind.Self => "you asked to be woken now",
        MissionEventKind.Review => "a scheduled look; nothing else has happened",
        MissionEventKind.Report => "a report from the Research Director arrived in `in/`",
        MissionEventKind.Brief => "a brief from the Operations Director arrived in `in/`",
        _ => kind
    };

    /// <summary>The card's line for what is being waited for, from the kind of the earliest wake.</summary>
    static string? Waiting(string? kind) => kind switch
    {
        null => "nothing is scheduled",
        MissionEventKind.Review => "the next scheduled look",
        MissionEventKind.Renewal => "the day to turn over",
        MissionEventKind.Self => "the time the AI asked for",
        _ => Reason(kind)
    };

    /// <summary>
    /// Records what became of each wake this turn took, and re-raises an owner's message ONCE when
    /// the turn that was supposed to answer it failed.
    ///
    /// Once, and not more: a message that makes the runtime fall over would otherwise be retried for
    /// ever, at the price of a turn each time. The retry carries the failure in its payload, and a
    /// retry that fails again is left <c>failed</c> — the owner can see both rows and ask again in
    /// their own words, which is the outcome a loop cannot improve on.
    /// </summary>
    void Settle(MissionEventStore events, IReadOnlyList<MissionEvent> wake, bool failed, AgentTurnEnded? ended)
    {
        if (wake.Count == 0) return;
        var disposition = failed ? MissionEventDisposition.Failed : MissionEventDisposition.Answered;

        try
        {
            foreach (var e in wake)
            {
                events.Settle(e.Id, disposition);
                if (!failed || e.Kind != MissionEventKind.Owner) continue;

                var said = Json.Read<MissionOwnerMessage>(e.Payload ?? "");
                if (said is null || said.RetryOf is not null) continue;

                var seq = events.NextOwnerSequence();
                events.Raise(MissionEventIds.Owner(seq), MissionEventKind.Owner, _now(),
                    Json.Write(said with
                    {
                        RetryOf = e.Id,
                        Failure = ended?.Raw is { Length: > 0 } raw
                            ? raw[..Math.Min(raw.Length, 500)]
                            : "the turn ended without a reply"
                    }));
            }
        }
        catch (Exception)
        {
            // The disposition is a record, not a gate. A queue that cannot be written has already
            // cost this turn its consumption record, and stopping the loop over it would turn a
            // bookkeeping failure into the AI stopping work.
        }
    }

    /// <summary>
    /// The delay the AI asked for, capped, or none at all.
    ///
    /// An absent file means "at once", which is the shape the product wants by default: an AI that
    /// stops only when it decides there is nothing to do until later. A file that cannot be read as
    /// a number is also "at once" rather than the cap — the AI can write it again next turn, and a
    /// half-hour stall on a mistyped file is a worse failure than an eager turn.
    ///
    /// The file is consumed. Leaving it would make one request to be left alone for half an hour
    /// into every turn being half an hour apart, for ever.
    /// </summary>
    TimeSpan AskedForDelay(string role)
    {
        var path = Path.Combine(_host.HomeFor(role), ".tradeagent", "next.json");
        try
        {
            if (!File.Exists(path)) return TimeSpan.Zero;
            var text = File.ReadAllText(path);
            File.Delete(path);

            var seconds = Json.Read<MissionNext>(text)?.AfterSeconds;
            if (seconds is null or <= 0 || double.IsNaN(seconds.Value)) return TimeSpan.Zero;

            var asked = TimeSpan.FromSeconds(Math.Min(seconds.Value, _options.MaxDelay.TotalSeconds));
            return asked > _options.MaxDelay ? _options.MaxDelay : asked;
        }
        catch (Exception) { return TimeSpan.Zero; }
    }

    /// <summary>
    /// HOW LONG TO WAIT BECAUSE THE NEXT TURN WOULD PASS THE OWNER'S CAP, or null to carry on.
    ///
    /// <b>Before the turn, not after it.</b> This used to compare the day's COMPLETED spending with
    /// the ceiling, which is a report rather than a cap: the turn that carries the total past the
    /// number is always admitted, because when it is admitted the total does not include it yet.
    /// Measured on 2026-09-07 — 5.07 USD against a 5 USD ceiling. The comparison is now
    /// <see cref="AiSpendToday.AdmitsAnotherTurn"/>: what has been spent, plus what is committed and
    /// unresolved, plus what THIS turn would commit, against the ceiling.
    ///
    /// The wait is until local MIDNIGHT rather than the backoff's half hour, because that is when
    /// the number this refuses on stops being today's — waking every thirty minutes to re-read the
    /// same total would be the loop asking the same question all night and getting the same answer.
    ///
    /// The card is left reading "waiting until 00:00" rather than "paused": the owner has not paused
    /// anything and their permission is intact, so a card saying they had would be the software
    /// putting a decision in their mouth. The AI card's cost line is where the reason is said.
    ///
    /// <see cref="IMissionHost.SpendCapReached"/> is told on the TRANSITION only. It fires again
    /// after a cap is raised and reached a second time, because that is a second event.
    /// </summary>
    TimeSpan? CappedUntilMidnight(AiSpendToday spend)
    {
        // ASKED BEFORE THE TURN, ABOUT THE TURN. `CapReached` alone could only ever be answered
        // after the turn that passed the ceiling had already run and been billed.
        if (spend.AdmitsAnotherTurn) { _reportedCap = false; return null; }

        if (!_reportedCap)
        {
            _reportedCap = true;
            _host.SpendCapReached(spend);
        }

        lock (_gate)
        {
            _working = false;
            _nextTurnAt = spend.ResumesAt;
            _waitingFor = spend.Role is { } r && !spend.RoleAdmitsAnotherTurn && spend.AdmitsGlobally
                ? $"the {CouncilRoles.Title(r)}'s share of the day to reset"
                : "the daily spending limit to reset";
            _role = spend.Role;
        }
        Changed?.Invoke();

        var wait = spend.ResumesAt - _now();
        // A resume time already behind us would busy-spin the loop against a total that is about to
        // roll over anyway. One BusyRetry is the smallest honest wait.
        return wait > TimeSpan.Zero ? wait : _options.BusyRetry;
    }

    /// <summary>Doubling from <see cref="MissionOptions.FirstBackoff"/>, capped at the same ceiling.</summary>
    TimeSpan Backoff(int consecutiveErrors)
    {
        var n = Math.Max(1, consecutiveErrors);
        var ticks = _options.FirstBackoff.Ticks;
        for (var i = 1; i < n && ticks < _options.MaxDelay.Ticks; i++) ticks *= 2;
        return TimeSpan.FromTicks(Math.Min(ticks, _options.MaxDelay.Ticks));
    }

    /// <summary>
    /// The one line the card shows from the last turn. The AI's own words where it produced any,
    /// and the System turn otherwise — which is where a failure says what went wrong, and is exactly
    /// what the owner needs on the card when the error count is climbing.
    /// </summary>
    static string? FirstLineOfLastReply(IAgentConversation conversation)
    {
        var last = conversation.History.LastOrDefault(t => t.Role is ChatRole.Ai or ChatRole.System);
        return last?.Text.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
    }
}
