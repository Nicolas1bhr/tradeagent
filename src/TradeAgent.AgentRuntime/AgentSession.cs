using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

public enum ChatRole { You, Ai, Tool, System }

public sealed record ChatTurn(ChatRole Role, string Text, DateTimeOffset At);

/// <summary>
/// The conversation with the AI, hosted by TradeAgent itself.
///
/// This is what replaces the console window. Nothing here shows a terminal, and there is no
/// interactive child process the user could be looking at: one message is one non-interactive run of
/// the CLI, and the run's machine-readable event stream is turned into turns the window draws.
///
/// Events can be raised from a worker thread. A UI consumer must marshal them onto its own thread.
/// </summary>
public interface IAgentConversation
{
    bool Busy { get; }
    IReadOnlyList<ChatTurn> History { get; }

    /// <summary>A complete turn was appended to <see cref="History"/>.</summary>
    event Action<ChatTurn>? TurnAdded;

    /// <summary>Streaming text for the AI turn currently in flight.</summary>
    event Action<string>? Delta;

    /// <summary><see cref="Busy"/> flipped.</summary>
    event Action? StateChanged;

    /// <summary>
    /// One run of the CLI finished, however it finished. Raised for every turn — the owner's and the
    /// mission's alike — and carrying only what was measured. `U-meter` prices turns from these.
    /// </summary>
    event Action<AgentTurnEnded>? TurnEnded;

    Task StartAsync(CancellationToken ct = default);
    Task SendAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Runs one turn on the mission's behalf. Identical to <see cref="SendAsync"/> except that the
    /// message is not shown as something the owner said, because nobody said it: the Situation block
    /// is written by the app. The reply and the tool activity appear exactly as they do for a typed
    /// message, so the Chat page still shows everything the AI does while it works on its own.
    /// </summary>
    Task SendMissionAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Carries one message to the top of the AI's NEXT turn instead of running a turn for it now,
    /// and shows it in the conversation as the owner's. This is what <see cref="SendAsync"/> does
    /// with anything typed while a turn is already in flight.
    /// </summary>
    void Queue(string message);

    /// <summary>
    /// Takes, and clears, what the owner typed while a turn was already running. The mission loop
    /// puts these at the top of the next turn's Situation; nothing else reads them, and a message
    /// taken here has already been shown in the conversation as theirs.
    /// </summary>
    IReadOnlyList<string> TakeTyped();

    Task CancelAsync();
    Task StopAsync();
}

/// <summary>
/// What the user has to do to finish signing in, produced by running the runtime's own login command
/// headless and reading the URL out of its output. TradeAgent opens the URL; the user never sees a
/// console and is never asked for a key.
/// </summary>
public sealed record AuthChallenge(string? Url, string? Code, string Message);

/// <summary>
/// Strips terminal colour codes.
///
/// These CLIs are written for a terminal and dress their output up even when nobody is watching:
/// sign-in URLs arrive wrapped in blue, credential lists in grey. A regex looking for a URL or a
/// number finds the escape bytes instead, so they come off before anything is matched.
/// </summary>
internal static partial class Ansi
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Sequence();

    public static string Strip(string text) =>
        text.Contains('\x1B') ? Sequence().Replace(text, "") : text;
}

/// <summary>
/// Turns an argument template into a real argument list.
///
/// The unattended flags, the stream flag and the model flag have to land before the prompt, because
/// every CLI here treats the first non-flag word after the subcommand as the prompt.
///
/// <b>Public, and <see cref="For"/> with it.</b> The argv is a rule rather than a detail — the model
/// TradeAgent chose has to reach the RESUMED turn as well as the first one, and nineteen turns in
/// twenty are resumes — and a rule that can only be checked by starting a vendor's CLI is a rule
/// nobody is checking.
/// </summary>
public static class AgentArgs
{
    /// <summary>
    /// THE ARGV FOR ONE TURN, first message or resumed, from one method so the two cannot drift.
    ///
    /// <paramref name="resuming"/> picks the template and NOTHING ELSE: every flag below is chosen
    /// the same way for both, which is the whole point. A resumed session does not remember which
    /// model it was run with — it is a fresh process reading a transcript — so a build that put the
    /// flag on the first turn only would run one turn on the model TradeAgent chose and every turn
    /// after it on whatever the CLI's own config file says.
    /// </summary>
    public static List<string> For(RuntimeManifest manifest, string prompt, bool resuming, string? model)
    {
        var template =
            resuming && manifest.ResumeArgs.Length > 0 ? manifest.ResumeArgs :
            manifest.ExecArgs.Length > 0 ? manifest.ExecArgs :
            manifest.TaskArgs;

        var streaming = !string.IsNullOrWhiteSpace(manifest.JsonFlag);
        return Build(template, prompt, streaming ? manifest.JsonFlag : null, manifest.UnattendedArgs,
            ModelArgs(manifest, model));
    }

    /// <summary>
    /// The model flag as this runtime spells it, or nothing at all. Nothing when the runtime has no
    /// such flag, and nothing when no model was named: an empty <c>-m</c> would be a CLI refusing to
    /// start, which is a worse failure than a model TradeAgent did not choose.
    /// </summary>
    static string[] ModelArgs(RuntimeManifest manifest, string? model) =>
        manifest.ModelArgs.Length == 0 || model is not { Length: > 0 }
            ? []
            : [.. manifest.ModelArgs.Select(a => a.Replace("{model}", model, StringComparison.Ordinal))];

    public static List<string> Build(string[] template, string prompt, string? jsonFlag, string[] unattended,
        string[]? modelArgs = null)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(jsonFlag))
            flags.AddRange(jsonFlag.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        flags.AddRange(unattended.Where(a => !string.IsNullOrEmpty(a)));
        if (modelArgs is not null) flags.AddRange(modelArgs.Where(a => !string.IsNullOrEmpty(a)));

        var promptIndex = Array.FindIndex(template, a => a.Contains("{prompt}", StringComparison.Ordinal));
        var args = new List<string>();

        for (var i = 0; i < template.Length; i++)
        {
            if (i == promptIndex) args.AddRange(flags);
            args.Add(template[i].Replace("{prompt}", prompt));
        }

        // A template with no placeholder still has to carry the message somewhere.
        if (promptIndex < 0)
        {
            args.AddRange(flags);
            args.Add(prompt);
        }

        return args;
    }
}

/// <summary>
/// One conversation, backed by the CLI's non-interactive execution mode.
///
/// The first message starts a session; every later message resumes it with the manifest's
/// <see cref="RuntimeManifest.ResumeArgs"/>. When the manifest declares a stream flag, stdout is
/// parsed line by line so assistant text appears while it is being written and tool activity — the
/// AI checking a price, placing an order — is visible as it happens rather than after the fact.
/// </summary>
/// <param name="presence">
/// The register a running turn reports itself to. Null is the process-wide one and is what the
/// product always uses; only a test that starts a child in the agent's role passes its own.
/// </param>
/// <param name="model">
/// THE MODEL TRADEAGENT ASKS FOR, read at the start of every turn rather than captured once, so an
/// owner who changes it on the Safety page is obeyed by the next turn and not by the next restart.
/// Null, or a null answer, means no model flag goes on the command line.
/// </param>
public sealed class AgentSession(
    RuntimeManifest manifest,
    Func<string?> resolveExecutable,
    Func<string> workspace,
    Func<IReadOnlyDictionary<string, string>> environment,
    AgentPresence? presence = null,
    Func<string?>? model = null) : IAgentConversation
{
    readonly List<ChatTurn> _history = [];
    readonly Lock _historyLock = new();
    readonly List<string> _typedMeanwhile = [];

    ContainedProcess? _current;
    CancellationTokenSource? _cts;
    bool _busy;
    bool _sessionExists;
    string? _threadId;

    public bool Busy => _busy;

    /// <summary>
    /// THE REGISTER THIS SESSION'S CHILD PROCESSES REPORT THEMSELVES TO — the process-wide
    /// <see cref="AgentPresence.Shared"/> unless a test passed its own.
    ///
    /// Exposed for the same reason <see cref="MaterialScanner.Attests"/> is: the inbox attestation
    /// is worth nothing unless the scanner and every agent process are looking at ONE register, and
    /// a chat session on a register nobody reads is a running agent the ledger cannot see. Reading
    /// this does not enter the register, which matters — entering the shared one from a test would
    /// permanently downgrade every inbox sighting in that assembly.
    /// </summary>
    public AgentPresence Presence => presence ?? AgentPresence.Shared;

    public IReadOnlyList<ChatTurn> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    /// <summary>The runtime's own session identifier, once it has told us one. Diagnostics only.</summary>
    public string? ThreadId => _threadId;

    /// <summary>
    /// The model this session's next turn will ask for, or null for none. A settings read that threw
    /// answers null — the turn runs on the runtime's own default rather than not running at all,
    /// which is the same rule the meter keeps about a rate it could not read.
    /// </summary>
    public string? RequestedModel
    {
        get
        {
            try { return model?.Invoke(); }
            catch (Exception) { return null; }
        }
    }

    public event Action<ChatTurn>? TurnAdded;
    public event Action<string>? Delta;
    public event Action? StateChanged;
    public event Action<AgentTurnEnded>? TurnEnded;

    /// <summary>
    /// Checks the runtime is actually there and clears any previous session, so the next message
    /// starts a fresh one. No process is started: in exec mode there is nothing to keep running
    /// between messages.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        var exe = resolveExecutable();
        if (exe is null)
            throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{manifest.DisplayName} is not installed on this computer");

        _sessionExists = false;
        _threadId = null;
        Directory.CreateDirectory(workspace());
        Append(new ChatTurn(ChatRole.System, $"{manifest.DisplayName} is ready.", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends one message the OWNER typed, and returns when the AI's reply is complete.
    ///
    /// Failures are reported as a System turn rather than thrown: this drives a chat panel, and a
    /// conversation that throws its errors somewhere else is a conversation that loses them.
    ///
    /// <b>Typing while the AI is working no longer throws it away.</b> It used to raise
    /// INVALID_REQUEST, which was defensible when every turn was one the owner had asked for: they
    /// pressed send twice and the second press was a mistake. It is not defensible once the mission
    /// loop is taking turns on its own, because then the AI is busy almost all the time and the
    /// owner's question would have nowhere to go. The message is shown as theirs and carried to the
    /// top of the next turn's Situation instead — see <see cref="TakeTyped"/>.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_busy) { Queue(message); return; }

        // THE OWNER'S TURN IS ADMITTED AND RESERVED LIKE ANY OTHER, BEFORE ANYTHING IS STARTED.
        //
        // It used to be metered and never reserved: this line started the CLI, and the row was
        // written when the usage came back. So a typed question could run beside a mission turn,
        // spend the last of the day's allowance and be recorded only after the vendor had been paid
        // — and, holding the meter's single open slot, resolve the OTHER turn's reservation with its
        // own usage. Every launch has a commitment made before it (docs/COUNCIL.md rule 3), and the
        // person typing is not an exception to a bill they are the one paying.
        var admission = Admit?.Invoke(message) ?? AiAdmission.Unrecorded;
        if (!admission.Admitted)
        {
            // NOT LAUNCHED, NOT LOST. Their words are held exactly as a message typed while the AI
            // is working is held, and the System line says which ceiling stopped it rather than
            // leaving a chat that simply went quiet.
            Hold(message, admission.Refusal!);
            return;
        }

        Append(new ChatTurn(ChatRole.You, message, DateTimeOffset.UtcNow));
        await RunAsync(message, ct);
    }

    /// <summary>
    /// OPENS THE DURABLE RECORD AND COMMITS THE COST OF A TURN THE OWNER TYPED, before it is
    /// started, and answers whether it may be started at all.
    ///
    /// Set by <see cref="TurnMeter.Attach"/>, which is the one place a conversation is metered.
    /// Null in every host that has no meter — a test, a build with no AI prepared — and then this
    /// behaves exactly as it did: the turn runs and the row is written when it ends. That default is
    /// the permissive one for the same reason the loop's is, and for the same reason: a chat that
    /// refused to work because nobody was counting would refuse for ever.
    /// </summary>
    public Func<string, AiAdmission>? Admit { get; set; }

    /// <summary>
    /// WRITES THE OWNER'S WORDS SOMEWHERE THAT SURVIVES THIS PROCESS, and answers whether it did.
    ///
    /// The queue below it is a list in memory. That was defensible while a queued message was only
    /// ever a few seconds old — the AI was working and would take it on the next turn — and it stops
    /// being defensible the moment the loop can be waiting half an hour, or the app can be closed
    /// and reopened, with a question sitting in it. A restart lost the words AND the receipt the
    /// owner had already been shown, which is worse than losing them: the software had said it would
    /// deal with it.
    ///
    /// Set by the composition root to the wake queue. Null in every host that has none, and then
    /// this behaves exactly as it did.
    /// </summary>
    public Func<string, bool>? RecordTyped { get; set; }

    /// <inheritdoc />
    public void Queue(string message) => Hold(message, Labels.HeldWhileTheAiIsWorking);

    /// <summary>
    /// KEEPS THE OWNER'S WORDS AND SAYS WHERE THEY WENT. Two callers, one behaviour: the AI is busy,
    /// or the day's ceiling refused the turn. The words are recorded the same way in both — the
    /// difference is one sentence, and a message that disappeared into a queue with no
    /// acknowledgement reads exactly like a message that was dropped.
    /// </summary>
    void Hold(string message, string said)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.Trim();

        // EXACTLY ONE OF THE TWO HOLDS IT. Writing to both would hand the same words to the next
        // turn twice — once off the table and once off the list — and an owner who asked one
        // question would watch it answered twice.
        var recorded = false;
        try { recorded = RecordTyped?.Invoke(text) ?? false; }
        catch (Exception) { recorded = false; }
        if (!recorded) lock (_historyLock) _typedMeanwhile.Add(text);

        Append(new ChatTurn(ChatRole.You, message, DateTimeOffset.UtcNow));
        Append(new ChatTurn(ChatRole.System, said, DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    public Task SendMissionAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
        if (_busy)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                "The AI is still working on the previous message.");
        return RunAsync(message, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> TakeTyped()
    {
        lock (_historyLock)
        {
            if (_typedMeanwhile.Count == 0) return [];
            var taken = _typedMeanwhile.ToArray();
            _typedMeanwhile.Clear();
            return taken;
        }
    }

    /// <summary>
    /// The run itself, shared by the owner's messages and the mission's. Everything that differs
    /// between the two — whose words they are, and whether they are shown as such — has already
    /// happened by the time this is called.
    ///
    /// <see cref="TurnEnded"/> is raised on EVERY path out, including the ones where no child ever
    /// started, because the mission loop's backoff and `U-meter`'s prices are both counting turns and
    /// a turn that vanished silently is one neither of them can account for.
    /// </summary>
    async Task RunAsync(string message, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var exitCode = -1;
        var raw = "";
        TurnUsage? usage = null;

        var exe = resolveExecutable();
        if (exe is null)
        {
            raw = $"{manifest.DisplayName} is not installed, so the message was not sent.";
            Append(new ChatTurn(ChatRole.System, raw, DateTimeOffset.UtcNow));
            TurnEnded?.Invoke(new AgentTurnEnded(exitCode, DateTimeOffset.UtcNow - startedAt, raw, DateTimeOffset.UtcNow));
            return;
        }

        SetBusy(true);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            (exitCode, raw, usage) = await RunTurnAsync(exe, message, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            raw = "Stopped.";
            Append(new ChatTurn(ChatRole.System, raw, DateTimeOffset.UtcNow));
        }
        catch (TradeAgentException ex)
        {
            raw = ex.Message;
            Append(new ChatTurn(ChatRole.System, ex.Message, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            raw = ex.Message;
            Append(new ChatTurn(ChatRole.System, $"The AI could not be reached: {ex.Message}", DateTimeOffset.UtcNow));
        }
        finally
        {
            _current = null;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            TurnEnded?.Invoke(new AgentTurnEnded(exitCode, DateTimeOffset.UtcNow - startedAt, raw, DateTimeOffset.UtcNow)
            {
                // What the RUNTIME said this turn used, or null where it said nothing. Carried out
                // of the turn rather than re-derived from Raw by whoever prices it, because the
                // stream is parsed once, here, and a second parser somewhere else would be a second
                // thing to keep correct as these CLIs change their event shapes.
                Usage = usage
            });
        }
    }

    async Task<(int ExitCode, string Raw, TurnUsage? Usage)> RunTurnAsync(string exe, string message, CancellationToken ct)
    {
        var streaming = !string.IsNullOrWhiteSpace(manifest.JsonFlag);

        // The model is read here, at the turn, and handed to the SAME builder for a first message and
        // a resumed one. See AgentArgs.For: `resuming` picks the template and nothing else.
        var args = AgentArgs.For(manifest, message, resuming: _sessionExists, model: RequestedModel);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected so it can be CLOSED immediately, below. Not redirecting it is the bug this
            // line exists to prevent: Codex reads stdin IN ADDITION to the prompt argument — it
            // prints "Reading additional input from stdin..." and waits for end-of-file. An
            // unredirected child inherits our stdin, and TradeAgent is a window with no console, so
            // that handle never reaches end-of-file. Measured on Windows 11: the run hung forever
            // with the turn stuck at Busy, and the same command with stdin closed answered in
            // seconds. A conversation that never returns is the worst possible failure here,
            // because it looks exactly like the AI thinking.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        CliAgentRuntime.SetCommand(psi, exe, args);

        // This environment is the only thing that puts `trade` on the agent's PATH. Losing it is how
        // the agent ends up reading its own instructions about a command it cannot run.
        foreach (var (k, v) in environment()) psi.Environment[k] = v;

        // HELD, NOT MERELY STARTED. A Job Object on Windows, a session of its own on macOS and
        // Linux: Kill(entireProcessTree) walks parent links, and a grandchild whose parent has
        // exited has none left to walk — measured before this line existed, a detached grandchild
        // outlived CancelAsync on both platforms. See ProcessContainment.
        using var contained = ProcessContainment.Start(psi);
        var process = contained.Process;
        _current = contained;
        // The conversation turn: the one process that runs what the agent decided to do. Held open
        // for exactly as long as it runs, so the material scanner cannot attest an inbox sighting
        // to the account owner across a window this process was inside (REVIEW 2026-09-05b f5).
        // Every path out of here has the child already dead — the method awaits its exit, and
        // CancelAsync kills the tree before it cancels the token — so the window never closes on a
        // process that is still writing.
        using var alive = CliAgentRuntime.Presence(process, presence);

        // End-of-file on stdin, at once. See the comment on RedirectStandardInput above.
        try { process.StandardInput.Close(); } catch (Exception) { /* already gone */ }

        var stderr = process.StandardError.ReadToEndAsync(ct);
        var raw = new StringBuilder();
        var state = new TurnState();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            raw.AppendLine(line);
            if (streaming) HandleStreamLine(line, state);
        }

        await process.WaitForExitAsync(ct);
        var errorText = (await stderr).Trim();

        FinishTurn(state, raw.ToString(), streaming, process.ExitCode, errorText);
        return (process.ExitCode, raw.ToString(), state.Usage);
    }

    /// <summary>
    /// Everything that has to happen once the child has exited: flush partial assistant text, apply
    /// the non-streaming fallback, and say something useful if the run failed.
    /// </summary>
    void FinishTurn(TurnState state, string raw, bool streaming, int exitCode, string errorText)
    {
        // Assistant text that streamed but never got a "completed" event still belongs in history.
        foreach (var pending in state.TakePendingMessages())
            AppendAi(pending);

        if (!streaming)
        {
            // FALLBACK PATH — this runtime has no machine-readable stream, so nothing could be shown
            // while it was thinking and there is no way to see which tools it used. Everything the
            // program printed becomes one assistant turn at the end. Deliberately plain: a guess at
            // structure here would be a guess presented to the user as fact.
            var text = raw.Trim();
            if (text.Length > 0) AppendAi(text);
        }
        else if (!state.ProducedAnyMessage)
        {
            // Streaming was asked for but nothing recognisable arrived — a flag the runtime does not
            // have, or an event shape that changed. Showing the raw output is worse than useless
            // only if it is empty, so show it when it is not.
            var text = raw.Trim();
            if (text.Length > 0) AppendAi(text);
        }

        if (exitCode != 0)
        {
            var detail = errorText.Length > 0 ? Tail(errorText) : $"it stopped with code {exitCode}";
            Append(new ChatTurn(ChatRole.System, $"{manifest.DisplayName} did not finish: {detail}", DateTimeOffset.UtcNow));
        }
        else
        {
            _sessionExists = true;
        }

        if (_threadId is not null) _sessionExists = true;
    }

    // ---- stream parsing ------------------------------------------------------------------------

    sealed class TurnState
    {
        /// <summary>Assistant text seen so far per stream item, so only the new part is emitted.</summary>
        public Dictionary<string, string> Partial { get; } = [];

        /// <summary>Tool items already shown, so a start and its completion are one line, not two.</summary>
        public HashSet<string> AnnouncedTools { get; } = [];

        public bool ProducedAnyMessage { get; set; }

        /// <summary>
        /// What the runtime has reported this turn using so far, or null while it has reported
        /// nothing. Null and zero are different answers — "it did not say" against "it used none" —
        /// and only the first is honest about a CLI with no usage event.
        /// </summary>
        public TurnUsage? Usage { get; private set; }

        public void Add(TurnUsage usage) => Usage = Usage is null ? usage : Usage.Plus(usage);

        readonly List<string> _pending = [];

        public void Remember(string id, string text) => Partial[id] = text;

        public void Complete(string id) => Partial.Remove(id);

        public void Pend(string text) => _pending.Add(text);

        public IEnumerable<string> TakePendingMessages()
        {
            // Anything still half-written when the process exited.
            var leftovers = Partial.Values.Where(v => v.Trim().Length > 0).ToList();
            Partial.Clear();
            var all = _pending.Concat(leftovers).ToList();
            _pending.Clear();
            return all;
        }
    }

    void HandleStreamLine(string line, TurnState state)
    {
        var trimmed = Ansi.Strip(line).Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return;   // progress chatter, not an event

        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed); }
        catch (JsonException) { return; }

        using (doc)
        {
            try { HandleEvent(doc.RootElement, state); }
            catch (Exception) { /* one unreadable event must not end the conversation */ }
        }
    }

    void HandleEvent(JsonElement e, TurnState state)
    {
        if (e.ValueKind != JsonValueKind.Object) return;

        // WHAT THE TURN COST, READ BEFORE ANY OTHER BRANCH CAN RETURN.
        //
        // It has to be first because the event carrying it is not one this method otherwise cares
        // about: Codex 0.153.4 reports tokens on `turn.completed`, which names no message, no tool
        // and no error, and would fall out of the bottom of this method untouched. Every branch
        // below returns, so anywhere else is a place the counts are silently dropped for one runtime
        // and not another.
        if (TurnUsage.Read(e) is { } usage) state.Add(usage);

        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : "";

        // Codex announces its session once, as thread_id. OpenCode puts sessionID on every line.
        // Neither is needed to resume — both runtimes take "continue the last one" — but knowing
        // which session the window is looking at is worth having when something goes wrong.
        if (_threadId is null)
            _threadId = Text(e, "thread_id") ?? Text(e, "sessionID") ?? Text(e, "session_id");

        if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            var msg = Text(e, "message") ?? Text(e, "error") ?? FindText(e, 3) ?? "the AI reported an error";
            Append(new ChatTurn(ChatRole.System, msg, DateTimeOffset.UtcNow));
            return;
        }

        // Codex: every unit of work arrives as an "item" with its own type.
        if (e.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            HandleItem(type, item, state);
            return;
        }

        // OpenCode: the event type IS the kind, and the payload sits in "part".
        // "text" is the answer; "tool_use" is the AI doing something; "reasoning" is thinking and
        // is left out for the same reason it is left out for Codex.
        if (type.Equals("tool_use", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            var part = e.TryGetProperty("part", out var p) && p.ValueKind == JsonValueKind.Object ? p : e;
            var name = Text(part, "tool") ?? Text(part, "name") ?? "a tool";
            var described = $"Used {name}";
            // No stable per-call id is documented, so the description itself is the identity —
            // enough to stop a tool that reports twice from appearing twice.
            if (state.AnnouncedTools.Add(described))
                Append(new ChatTurn(ChatRole.Tool, described, DateTimeOffset.UtcNow));
            return;
        }

        if (type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return;

        // Anything else: only trust it when the event calls itself a message.
        if (type.Contains("message", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("assistant", StringComparison.OrdinalIgnoreCase))
        {
            var text = FindText(e, 3);
            if (string.IsNullOrWhiteSpace(text)) return;

            // OpenCode emits one complete text block per event rather than a growing string, so
            // each event is its own finished piece of the answer.
            var id = Text(e, "id") ?? $"text-{state.Partial.Count}-{text.Length}";
            Stream(id, text, state, complete: true);
        }
    }

    void HandleItem(string eventType, JsonElement item, TurnState state)
    {
        var itemType = Text(item, "type") ?? "";
        var id = Text(item, "id") ?? itemType;
        var completed = eventType.EndsWith(".completed", StringComparison.Ordinal);

        if (itemType.Contains("agent_message", StringComparison.OrdinalIgnoreCase) ||
            itemType.Equals("assistant_message", StringComparison.OrdinalIgnoreCase) ||
            itemType.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            var text = Text(item, "text") ?? FindText(item, 3);
            if (string.IsNullOrEmpty(text)) return;
            Stream(id, text, state, completed);
            return;
        }

        // Reasoning is deliberately not shown: it is long, it is not a decision, and putting it in
        // the same panel as trading actions makes the actions harder to see.
        if (itemType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return;

        // Tool activity. Announced once, when it starts — or on completion if that is all we saw.
        if (!eventType.EndsWith(".started", StringComparison.Ordinal) && !completed) return;
        if (!state.AnnouncedTools.Add(id)) return;

        var described = DescribeTool(itemType, item);
        if (described is not null) Append(new ChatTurn(ChatRole.Tool, described, DateTimeOffset.UtcNow));
    }

    static string? DescribeTool(string itemType, JsonElement item)
    {
        if (itemType.Contains("command", StringComparison.OrdinalIgnoreCase))
        {
            var command = Text(item, "command") ?? Text(item, "cmd");
            return command is null ? null : $"$ {Shorten(command, 300)}";
        }
        if (itemType.Contains("mcp", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            var name = Text(item, "tool") ?? Text(item, "name") ?? "a tool";
            var server = Text(item, "server");
            return server is null ? $"Used {name}" : $"Used {server}.{name}";
        }
        if (itemType.Contains("file", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("patch", StringComparison.OrdinalIgnoreCase))
        {
            var path = Text(item, "path") ?? Text(item, "file");
            return path is null ? "Edited files" : $"Edited {path}";
        }
        if (itemType.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            var query = Text(item, "query") ?? Text(item, "text");
            return query is null ? "Searched the web" : $"Searched: {Shorten(query, 200)}";
        }
        if (itemType.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("todo", StringComparison.OrdinalIgnoreCase))
            return "Updated its plan";

        return null;
    }

    /// <summary>Emits only the part of the assistant text that has not been shown yet.</summary>
    void Stream(string id, string text, TurnState state, bool complete)
    {
        var seen = state.Partial.TryGetValue(id, out var s) ? s : "";
        if (text.Length > seen.Length && text.StartsWith(seen, StringComparison.Ordinal))
        {
            var suffix = text[seen.Length..];
            if (suffix.Length > 0) Delta?.Invoke(suffix);
        }
        else if (!string.Equals(text, seen, StringComparison.Ordinal))
        {
            // The runtime rewrote the message rather than extending it: start the delta over.
            Delta?.Invoke(text);
        }

        if (complete)
        {
            state.Complete(id);
            AppendAi(text);
            state.ProducedAnyMessage = true;
        }
        else
        {
            state.Remember(id, text);
            state.ProducedAnyMessage = true;
        }
    }

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Finds the assistant text in an event whose exact shape is not known, by looking a few levels
    /// down for the property names every one of these tools uses. Used only for runtimes whose
    /// stream format has not been confirmed.
    /// </summary>
    static string? FindText(JsonElement e, int depth)
    {
        if (depth < 0 || e.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "text", "content", "delta", "message" })
        {
            if (!e.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var nested = v.ValueKind == JsonValueKind.Object
                    ? FindText(v, depth - 1)
                    : v.EnumerateArray().Select(x => FindText(x, depth - 1)).FirstOrDefault(x => x is not null);
                if (nested is not null) return nested;
            }
        }

        foreach (var name in new[] { "part", "item", "info", "data" })
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                var nested = FindText(v, depth - 1);
                if (nested is not null) return nested;
            }

        return null;
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    /// <summary>
    /// Kills the run in flight, and everything it started, without touching the session.
    ///
    /// "Everything it started" is what <see cref="ContainedProcess.Kill"/> buys and what the bare
    /// tree kill could not: a grandchild that detached is no longer anybody's child, so a walk down
    /// parent links stops one level above it. The job — or the process group — still names it.
    /// </summary>
    public Task CancelAsync()
    {
        var contained = _current;
        try { contained?.Kill(); }
        catch (Exception) { /* already gone */ }
        try { _cts?.Cancel(); }
        catch (Exception) { }
        return Task.CompletedTask;
    }

    /// <summary>Cancels anything running and forgets the session, so the next message starts fresh.</summary>
    public async Task StopAsync()
    {
        await CancelAsync();
        _sessionExists = false;
        _threadId = null;
        SetBusy(false);
    }

    void SetBusy(bool value)
    {
        if (_busy == value) return;
        _busy = value;
        StateChanged?.Invoke();
    }

    void AppendAi(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        Append(new ChatTurn(ChatRole.Ai, trimmed, DateTimeOffset.UtcNow));
    }

    void Append(ChatTurn turn)
    {
        lock (_historyLock) _history.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "…" + trimmed[^400..];
    }
}
