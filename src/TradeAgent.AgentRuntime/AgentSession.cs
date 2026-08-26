using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;

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

    Task StartAsync(CancellationToken ct = default);
    Task SendAsync(string message, CancellationToken ct = default);
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
/// The unattended flags and the stream flag have to land before the prompt, because every CLI here
/// treats the first non-flag word after the subcommand as the prompt.
/// </summary>
internal static class AgentArgs
{
    public static List<string> Build(string[] template, string prompt, string? jsonFlag, string[] unattended)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(jsonFlag))
            flags.AddRange(jsonFlag.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        flags.AddRange(unattended.Where(a => !string.IsNullOrEmpty(a)));

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
public sealed class AgentSession(
    RuntimeManifest manifest,
    Func<string?> resolveExecutable,
    Func<string> workspace,
    Func<IReadOnlyDictionary<string, string>> environment) : IAgentConversation
{
    readonly List<ChatTurn> _history = [];
    readonly Lock _historyLock = new();

    Process? _current;
    CancellationTokenSource? _cts;
    bool _busy;
    bool _sessionExists;
    string? _threadId;

    public bool Busy => _busy;

    public IReadOnlyList<ChatTurn> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    /// <summary>The runtime's own session identifier, once it has told us one. Diagnostics only.</summary>
    public string? ThreadId => _threadId;

    public event Action<ChatTurn>? TurnAdded;
    public event Action<string>? Delta;
    public event Action? StateChanged;

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
    /// Sends one message and returns when the AI's reply is complete.
    ///
    /// Failures are reported as a System turn rather than thrown: this drives a chat panel, and a
    /// conversation that throws its errors somewhere else is a conversation that loses them. The one
    /// exception is being asked to send while already busy, which is a caller mistake.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_busy)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                "The AI is still working on the previous message.");

        var exe = resolveExecutable();
        if (exe is null)
        {
            Append(new ChatTurn(ChatRole.System,
                $"{manifest.DisplayName} is not installed, so the message was not sent.", DateTimeOffset.UtcNow));
            return;
        }

        Append(new ChatTurn(ChatRole.You, message, DateTimeOffset.UtcNow));
        SetBusy(true);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await RunTurnAsync(exe, message, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Append(new ChatTurn(ChatRole.System, "Stopped.", DateTimeOffset.UtcNow));
        }
        catch (TradeAgentException ex)
        {
            Append(new ChatTurn(ChatRole.System, ex.Message, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Append(new ChatTurn(ChatRole.System, $"The AI could not be reached: {ex.Message}", DateTimeOffset.UtcNow));
        }
        finally
        {
            _current = null;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    async Task RunTurnAsync(string exe, string message, CancellationToken ct)
    {
        var streaming = !string.IsNullOrWhiteSpace(manifest.JsonFlag);

        var template =
            _sessionExists && manifest.ResumeArgs.Length > 0 ? manifest.ResumeArgs :
            manifest.ExecArgs.Length > 0 ? manifest.ExecArgs :
            manifest.TaskArgs;

        var args = AgentArgs.Build(template, message, streaming ? manifest.JsonFlag : null, manifest.UnattendedArgs);

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

        using var process = Process.Start(psi)
            ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND, $"{manifest.DisplayName} would not start");
        _current = process;

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

    /// <summary>Kills the run in flight, and everything it started, without touching the session.</summary>
    public Task CancelAsync()
    {
        var process = _current;
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
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
