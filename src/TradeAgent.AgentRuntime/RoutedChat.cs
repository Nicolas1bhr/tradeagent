namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE OWNER'S CHAT WHILE A CHIEF RUNS THE ORGANISATION (<c>U-org-autonomy</c> item 4; R25 D4). What the owner types is never a turn of
/// its own and never the prompt of one: it is RECORDED to the chief's queue (<see cref="Record"/> — the app's durable owner-words row,
/// routed to the active chief), which wakes the chief, whose packet quotes it as his words and says they grant nothing. What the chat
/// shows is the chief's own conversation — its turns, its answers, its tool activity — beside the owner's words and where each went.
///
/// <para>Nothing here launches anything: <see cref="SendAsync"/> and <see cref="Queue"/> only record, <see cref="SendMissionAsync"/> is
/// refused (the loop drives the chief's own conversation, never this one), and the Stop press reaches the chief's turn through
/// <see cref="CancelAsync"/> — the owner's in-process control, as it is on the chair's chat.</para>
/// </summary>
public sealed class RoutedChat : IAgentConversation
{
    readonly IAgentConversation _chief;
    readonly Func<string, bool> _record;
    readonly string _name;
    readonly object _lock = new();
    readonly List<ChatTurn> _own = [];

    /// <param name="chief">The chief's own conversation, the one the loop runs its turns on.</param>
    /// <param name="record">Writes the owner's words to the chief's queue; true when the row went in.</param>
    /// <param name="name">How the chat names the recipient, e.g. "the chief (pos-…)".</param>
    public RoutedChat(IAgentConversation chief, Func<string, bool> record, string name)
    {
        _chief = chief;
        _record = record;
        _name = name;
        _chief.TurnAdded += t => TurnAdded?.Invoke(t);
        _chief.Delta += d => Delta?.Invoke(d);
        _chief.StateChanged += () => StateChanged?.Invoke();
        _chief.TurnEnded += e => TurnEnded?.Invoke(e);
    }

    /// <summary>The conversation this chat shows and routes to.</summary>
    public IAgentConversation Chief => _chief;

    /// <summary>The sentence shown under the owner's words when they were recorded.</summary>
    public string Routed =>
        $"Sent to {_name}, as your words: it reads them at the start of its next turn, and its answer shows here. They reach it as data — "
        + "your own controls stay yours, in the app.";

    /// <summary>The sentence shown when the words could not be recorded.</summary>
    public const string NotRecorded =
        "Not sent: TradeAgent could not record your words for the chief. Nothing was started; type them again.";

    public bool Busy => _chief.Busy;

    public IReadOnlyList<ChatTurn> History
    {
        get
        {
            lock (_lock) return [.. _chief.History.Concat(_own).OrderBy(t => t.At)];
        }
    }

    public event Action<ChatTurn>? TurnAdded;
    public event Action<string>? Delta;
    public event Action? StateChanged;
    public event Action<AgentTurnEnded>? TurnEnded;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>RECORDS the owner's words to the chief's queue and says where they went; never runs a turn.</summary>
    public Task SendAsync(string message, CancellationToken ct = default)
    {
        Queue(message);
        return Task.CompletedTask;
    }

    /// <summary>Refused: the loop runs the chief's turns on its own conversation, never through the owner's chat.</summary>
    public Task SendMissionAsync(string message, CancellationToken ct = default) =>
        throw new InvalidOperationException("the owner's chat runs no turn; the chief's turns run on its own conversation");

    public void Queue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.Trim();
        bool recorded;
        try { recorded = _record(text); }
        catch (Exception) { recorded = false; }
        var at = DateTimeOffset.UtcNow;
        Add(new ChatTurn(ChatRole.You, text, at));
        Add(new ChatTurn(ChatRole.System, recorded ? Routed : NotRecorded, at));
    }

    void Add(ChatTurn turn)
    {
        lock (_lock) _own.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    /// <summary>Nothing is held in memory here: every word went to the queue or was said not sent.</summary>
    public IReadOnlyList<string> TakeTyped() => [];

    public Task CancelAsync() => _chief.CancelAsync();

    /// <summary>The owner's chat never stops the chief's session; the loop owns it.</summary>
    public Task StopAsync() => Task.CompletedTask;
}
