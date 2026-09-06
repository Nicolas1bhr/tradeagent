using System.Text;
using TradeAgent.Core;

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
    string? LastTurnFirstLine);

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
    /// Everything the app knows this turn, gathered at the moment the turn is composed. Asynchronous
    /// because positions and open orders come from the broker, and a turn boundary is exactly the
    /// right place to pay for a fresh reading rather than hand the AI a cached one.
    /// </summary>
    Task<MissionSituation> SituationAsync(CancellationToken ct);

    /// <summary>The agent's own directory — where <c>.tradeagent/next.json</c> is read from.</summary>
    string AgentHome { get; }

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
    /// WHAT THE AI HAS COST TODAY AND WHAT IT IS ALLOWED TO COST. The loop reads this before every
    /// turn and takes none while <see cref="AiSpendToday.CapReached"/> is true.
    ///
    /// A default of <see cref="AiSpendToday.NotMetered"/> so that a host with no meter behind it —
    /// a test, a build with no AI prepared — keeps working and keeps turning. That default is
    /// deliberately the permissive one: the restrictive reading belongs where the number is
    /// measured, and a loop that stopped because nobody was counting would stop for ever.
    /// </summary>
    AiSpendToday Spend => AiSpendToday.NotMetered;

    /// <summary>
    /// Told ONCE, on the turn the cap is first reached, so the activity log gets one line rather
    /// than one every five seconds for the rest of the day. The loop knows the transition; the host
    /// owns the words and the log.
    /// </summary>
    void SpendCapReached(AiSpendToday spend) { }
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
    /// WHAT THE AI HAS COST ITS OWNER TODAY. The other half of the sentence it was given as its
    /// mission — make at least enough to pay for yourself — and an AI told to cover its own costs
    /// without being told what they are is being asked to guess at half the arithmetic.
    /// </summary>
    public AiSpendToday Spend { get; init; } = AiSpendToday.NotMetered;

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
        if (NewMaterial.Count > 0)
            b.AppendLine($"- New in `../inbox` since your last turn: {string.Join(", ", NewMaterial)}");
        if (!string.IsNullOrWhiteSpace(Guidance))
            b.AppendLine($"- The owner's guidance: {Guidance.Trim()}");

        b.AppendLine().AppendLine(Continue);
        return b.ToString();
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

        var spent = $"What you have cost today: {Money(spend.Spent, spend.Currency)} of a "
                    + $"{Money(spend.Cap, spend.Currency)} daily limit, over {turns}";

        // The AI is told the figure is an upper bound in the same words the owner reads. An agent
        // asked to cover what it costs and given a number without that word would plan against a
        // bill it has not actually run up — and would be wrong in the expensive direction if the
        // label were ever dropped, since it is the ONLY thing separating a ceiling from a receipt.
        if (spend.Estimated is { } estimated) spent += $" — {estimated}";

        if (spend.UnpricedTurns > 0)
            spent += $" — and {spend.UnpricedTurns} of those could not be priced, so the real figure is higher";

        return spent + (spend.CapReached
            ? ". You are at the limit: this is the last turn until midnight."
            : ".");
    }

    /// <summary>An amount with its currency, or without one where <c>costs.json</c> named none.</summary>
    public static string Money(decimal amount, string currency) =>
        currency.Length == 0 ? amount.ToString("0.####") : $"{amount:0.####} {currency}";
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
                    _turns, _consecutiveErrors, _lastFirstLine);
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

        lock (_gate) { _working = false; _nextTurnAt = null; }
        Changed?.Invoke();
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
                try { await _delay(wait, ct); }
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
    /// ONE TURN, START TO FINISH, and the wait before the next one.
    ///
    /// Public because this — not the timer around it — is the unit worth driving in a test: the
    /// yield to the scanner, the fresh session, the message, the run, and the pass that closes the
    /// scanner's window behind the turn all happen here, in this order, and the order is what the
    /// attestation depends on.
    /// </summary>
    public async Task<TimeSpan> TurnAsync(CancellationToken ct = default)
    {
        // ---- the day's ceiling -------------------------------------------------------------------
        // FIRST, because it is the one reason not to take a turn that depends on nothing else here.
        // The AI's mission is to make at least enough to pay for itself and half of that sentence is
        // its own bill; turns run back to back for as long as the machine is on, so without this the
        // account is being charged with nobody watching. It removes no permission and touches no
        // order — it is the loop declining to spend more of the owner's money today.
        if (CappedUntilMidnight() is { } untilMidnight) return untilMidnight;

        var conversation = _host.Conversation;
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

        // ---- a fresh CLI session every N turns ---------------------------------------------------
        // Resuming forever grows one context until the runtime refuses it or prices it absurdly. The
        // files are the memory, so a fresh session loses nothing the AI wrote down — and StopAsync
        // forgets the session without touching the workspace.
        if (_sessionTurns >= _options.TurnsPerSession)
        {
            await conversation.StopAsync();
            _sessionTurns = 0;
        }

        var situation = (await _host.SituationAsync(ct)) with { OwnerMessages = conversation.TakeTyped() };

        lock (_gate) { _working = true; _nextTurnAt = null; }
        Changed?.Invoke();

        AgentTurnEnded? ended = null;
        void Watch(AgentTurnEnded e) => ended = e;
        conversation.TurnEnded += Watch;
        try { await conversation.SendMissionAsync(situation.Text(), ct); }
        // A turn that threw its way out — cancelled, or a runtime that will not start — must not
        // leave the card reading "working" for ever over a turn that is not happening.
        finally { conversation.TurnEnded -= Watch; lock (_gate) _working = false; }

        // ---- close the scanner's window behind this turn -----------------------------------------
        // The pass above can only attest because a pass ran AFTER the previous turn: the window a
        // pass measures starts at the previous pass, so one taken while the loop is idle is what
        // moves that start past the agent's last breath. Without this line the pass before the next
        // turn still measures a window this turn is inside, and attests nothing.
        await _host.ScanAsync(ct);

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

        return failed ? Backoff(errors) : AskedForDelay();
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
    TimeSpan AskedForDelay()
    {
        var path = Path.Combine(_host.AgentHome, ".tradeagent", "next.json");
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
    /// HOW LONG TO WAIT BECAUSE THE DAY'S SPENDING HAS REACHED THE OWNER'S CAP, or null to carry on.
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
    TimeSpan? CappedUntilMidnight()
    {
        var spend = _host.Spend;
        if (!spend.CapReached) { _reportedCap = false; return null; }

        if (!_reportedCap)
        {
            _reportedCap = true;
            _host.SpendCapReached(spend);
        }

        lock (_gate) { _working = false; _nextTurnAt = spend.ResumesAt; }
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
