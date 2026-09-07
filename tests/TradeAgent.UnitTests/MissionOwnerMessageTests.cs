using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER TYPED SOMETHING AND THE SOFTWARE SAID IT WOULD DEAL WITH IT.
///
/// A message typed while the AI is working is queued and acknowledged — "The AI is working. It will
/// see this at the start of its next turn." That promise was kept in a list in memory
/// (<c>AgentSession._typedMeanwhile</c>), which was defensible while a queued message was only ever
/// seconds old. It stopped being defensible the moment the loop could be asleep for half an hour, or
/// the app could be closed and reopened, with a question sitting in it: the restart lost the words
/// AND the receipt, which is worse than losing them, because the software had already promised.
///
/// So the queue is the table. Exactly one of the two holds any message — a copy in both would have
/// the next turn answering one question twice.
/// </summary>
public class MissionOwnerMessageTests
{
    /// <summary>
    /// A conversation with no process behind it. Nothing here starts a child, and nothing needs to:
    /// <see cref="AgentSession.Queue"/> is the whole of what happens when a message is typed at a
    /// busy AI, and it touches no executable.
    /// </summary>
    static AgentSession Session(Func<string, bool>? record = null)
    {
        var dir = Path.Combine(TestEnv.Home, $"owner-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        var manifest = new RuntimeManifest
        {
            Id = "probe",
            DisplayName = "Probe runtime",
            Executable = "never-run",
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"]
        };
        // ITS OWN PRESENCE REGISTER. AgentPresence.Shared is sticky — once an agent has been alive
        // in a process, no scan can attest a window that began before it — and a session built under
        // the shared one would downgrade every inbox sighting in this assembly. Measured, and
        // written down in TurnMeterTests for the same reason.
        return new AgentSession(manifest, () => null, () => dir,
            () => new Dictionary<string, string>(), presence: new AgentPresence())
        {
            RecordTyped = record
        };
    }

    /// <summary>A loop's world with nothing in it but the wake queue and a conversation.</summary>
    sealed class Host(MissionEventStore events, IAgentConversation conversation) : IMissionHost
    {
        public List<string> Prompts { get; } = [];

        public IAgentConversation? Conversation => conversation;
        public MissionEventStore? Events => events;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"owner-home-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        public string? BeginTurn(string prompt, IReadOnlyList<string> wakes)
        {
            Prompts.Add(prompt);
            return null;
        }
    }

    /// <summary>A conversation that records what it was sent and can be made to fail its turns.</summary>
    sealed class Recording : IAgentConversation
    {
        public List<string> Sent { get; } = [];
        public int Exit { get; set; }
        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string m, CancellationToken ct = default) => SendMissionAsync(m, ct);

        public Task SendMissionAsync(string message, CancellationToken ct = default)
        {
            Sent.Add(message);
            TurnAdded?.Invoke(new ChatTurn(ChatRole.Ai, "done", DateTimeOffset.UtcNow));
            Delta?.Invoke("");
            StateChanged?.Invoke();
            TurnEnded?.Invoke(new AgentTurnEnded(Exit, TimeSpan.Zero,
                Exit == 0 ? "done" : "the runtime went away mid-turn", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    static readonly MissionOptions NoHeartbeat = new() { ReviewEvery = TimeSpan.Zero };

    /// <summary>
    /// MIDDAY, for the one test here that counts turns. The loop schedules the day's renewal at
    /// local midnight, so a second turn taken on the real clock is a turn the renewal caused
    /// whenever midnight falls between the two — see MissionLoopTests.Midday, which says the same
    /// thing where the rule itself is pinned.
    /// </summary>
    static readonly DateTimeOffset Midday =
        new(DateTime.Today.AddHours(12), DateTimeOffset.Now.Offset);

    /// <summary>
    /// RED FIRST, AND THE RED IS THE PRODUCT DEFECT: a question typed at a working AI, then a new
    /// host over the same database, and the words are gone. Today they live in a list on the session
    /// object that the restart destroyed — along with the receipt the owner was shown.
    ///
    /// The words come back FIRST in the next turn's block, above the time, the mode and the account,
    /// because a person waiting for an answer outranks everything else in it.
    /// </summary>
    [Fact]
    public async Task A_message_typed_while_it_worked_survives_a_restart_and_is_read_first()
    {
        using var db = TestEnv.NewDb();
        var typed = new MissionEventStore(db);
        // The product's own writer, so that what is asserted below is what the app does — see
        // AppHost.RecordOwnerMessage, which is this call and a nudge to the loop.
        var session = Session(text => typed.RecordOwnerMessage(text, Midday));

        session.Queue("stop buying NQ");

        // The row is the only copy: a message held in both places would be answered twice.
        Assert.Empty(session.TakeTyped());

        // A new host over the same database. Nothing of the session above survives into it.
        var conversation = new Recording();
        var loop = new MissionLoop(new Host(new MissionEventStore(db), conversation), NoHeartbeat,
            now: () => Midday);
        await loop.TurnAsync();

        var sent = conversation.Sent.Single();
        Assert.Contains("> stop buying NQ", sent);
        Assert.True(sent.IndexOf("stop buying NQ", StringComparison.Ordinal)
                    < sent.IndexOf("Trading mode", StringComparison.Ordinal),
            $"the owner's words came after the state:\n{sent}");

        // Taken once. The next turn does not re-ask a question that has been answered.
        await loop.TurnAsync();
        Assert.Single(conversation.Sent);
    }

    /// <summary>
    /// The receipt is still shown, in the same words, because the point of writing the row down is to
    /// make that promise true rather than to replace it.
    /// </summary>
    [Fact]
    public void The_owner_still_sees_their_message_and_where_it_went()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var session = Session(text => events.RecordOwnerMessage(text, DateTimeOffset.UtcNow));

        session.Queue("stop buying NQ");

        Assert.Equal("stop buying NQ", session.History[0].Text);
        Assert.Equal(ChatRole.You, session.History[0].Role);
        Assert.Equal("The AI is working. It will see this at the start of its next turn.",
            session.History[1].Text);
    }

    /// <summary>
    /// A build with no queue behind it keeps the list, and keeps working. The durable record is the
    /// better answer, not the only one — and a session that refused to take a message because nobody
    /// was writing it down would be a worse failure than the one being fixed.
    /// </summary>
    [Fact]
    public void With_no_queue_behind_it_the_message_is_still_carried_in_memory()
    {
        var session = Session();

        session.Queue("stop buying NQ");

        Assert.Equal(["stop buying NQ"], session.TakeTyped());
    }

    /// <summary>
    /// A message the AI ANSWERED is disposed of as answered, and one whose turn FELL OVER is not
    /// silently swallowed: it is marked failed, re-raised once with the failure in its payload, and
    /// the next turn is handed the same words again.
    /// </summary>
    [Fact]
    public async Task A_turn_that_failed_re_raises_the_owners_message_once_with_the_failure()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var conversation = new Recording { Exit = 2 };
        var loop = new MissionLoop(new Host(events, conversation), NoHeartbeat);

        events.Raise(MissionEventIds.Owner(1), MissionEventKind.Owner, DateTimeOffset.UtcNow,
            Json.Write(new MissionOwnerMessage("close everything", DateTimeOffset.UtcNow)));

        await loop.TurnAsync();

        var first = events.Get(MissionEventIds.Owner(1))!;
        Assert.Equal(MissionEventDisposition.Failed, first.Disposition);

        var retry = events.OfKind(MissionEventKind.Owner).Single(e => e.Id != first.Id);
        var said = Json.Read<MissionOwnerMessage>(retry.Payload!)!;
        Assert.Equal("close everything", said.Text);
        Assert.Equal(first.Id, said.RetryOf);
        Assert.Contains("the runtime went away mid-turn", said.Failure);

        // ONCE. A message that makes the runtime fall over must not be retried for ever at the price
        // of a turn each time; the second failure is recorded and left, and the owner can ask again
        // in their own words.
        await loop.TurnAsync();
        Assert.Contains("close everything", conversation.Sent[1]);
        Assert.Equal(2, events.OfKind(MissionEventKind.Owner).Count);
        Assert.Equal(MissionEventDisposition.Failed, events.Get(retry.Id)!.Disposition);
    }

    /// <summary>
    /// The turn that answered it says so, and the record names the launch that took it. A
    /// disposition is a verdict on a turn that happened.
    /// </summary>
    [Fact]
    public async Task A_message_the_ai_answered_is_recorded_as_answered()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var loop = new MissionLoop(new Host(events, new Recording()), NoHeartbeat);
        events.Raise(MissionEventIds.Owner(1), MissionEventKind.Owner, DateTimeOffset.UtcNow,
            Json.Write(new MissionOwnerMessage("how did today go?", DateTimeOffset.UtcNow)));

        await loop.TurnAsync();

        var row = events.Get(MissionEventIds.Owner(1))!;
        Assert.Equal(MissionEventDisposition.Answered, row.Disposition);
        Assert.True(row.Consumed);
        Assert.Single(events.OfKind(MissionEventKind.Owner));
    }
}
