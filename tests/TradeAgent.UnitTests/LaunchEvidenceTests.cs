using Microsoft.Data.Sqlite;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT A LAUNCH WAS OFFERED AND WHAT ITS VENDOR SAID (<c>U-chief-wake-once</c>, rung 52): the two evidence gaps the M-org1 sitting needs
/// closed to diagnose a refused model — the vendor's own 4xx/refusal text, bounded and never a credential, and the offered tools' NAMES beside
/// the profile hash — both on the launch's own row, measurements only. No model: a scripted stream, a fake seat, the real ledger.
/// </summary>
public class LaunchEvidenceTests
{
    const string Refused =
        "unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-6-luna' model is not supported when using Codex with a ChatGPT account.\"}";

    /// <summary>A turn the vendor refused before any work: its stream's top-level <c>error</c> event and the turn's failure.</summary>
    const string RefusedTurn =
        """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000052"}
        {"type":"turn.started"}
        {"type":"error","message":"unexpected status 400 Bad Request: {\"detail\":\"The 'gpt-6-luna' model is not supported when using Codex with a ChatGPT account.\"}"}
        {"type":"turn.failed","error":{"message":"the turn failed"}}
        """;

    static TurnMeter Meter(Database db, DateTimeOffset at) =>
        new(db, () => 50m, runtimeId: () => "codex", now: () => at,
            recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"), model: () => "gpt-6-luna", share: _ => 1m);

    static List<AiAttempt> Rows(Database db) =>
        [.. new AiAttemptStore(db).Between(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1))];

    /// <summary>
    /// (e1) THE VENDOR'S ERROR EVENT TRAVELS OUT OF THE TURN AND ONTO ITS ROW: the session's stream reader keeps the top-level <c>error</c>
    /// event's message (<see cref="AgentTurnEnded.VendorErrors"/>), and the meter's close writes it as <c>ai_attempt.vendor_said</c>, once —
    /// an UPDATE that changes it is refused by trigger. RED at base: the text was shown in the conversation and stored nowhere under state/.
    /// </summary>
    [Fact]
    public async Task The_vendors_refusal_is_written_on_the_launchs_row_once()
    {
        var session = AgentRuntimeProbe.SessionOverStream(RefusedTurn, manifest: VendorLimitTests.Codex, exitCode: 1);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        await session.SendAsync("continue");
        Assert.NotNull(ended);
        Assert.Contains(Refused, ended!.VendorErrors!);

        using var db = TestEnv.NewDb();
        var at = TestEnv.LocalNoon();
        Meter(db, at).Record(ended, CouncilRoles.Research);
        var row = Assert.Single(Rows(db));
        Assert.Contains(Refused, row.VendorSaid, StringComparison.Ordinal);

        Assert.Throws<SqliteException>(() => db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE ai_attempt SET vendor_said='a kinder story' WHERE id=$id", ("$id", row.Id));
            return c.ExecuteNonQuery();
        }));
    }

    /// <summary>
    /// (e2) THE VENDOR'S WORDS ARE BOUNDED AND NEVER HOLD A CREDENTIAL: a bearer value, an <c>sk-</c> key, a JWT and any long key-shaped run
    /// are withheld; a text past the bound is cut and says how long it was; nothing to say is null.
    /// </summary>
    [Fact]
    public void The_vendors_words_are_bounded_and_hold_no_credential()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJvd25lciJ9.c2lnbmF0dXJlLXZhbHVl";
        var said = LaunchEvidence.VendorSaid(
        [
            "401 Unauthorized: Authorization: Bearer abc.def-hidden-value was refused",
            "key sk-proj-AbCdEf0123456789xyz is invalid",
            $"session {jwt} expired",
            "id 0123456789abcdef0123456789abcdef0123 unknown",
            "invalid API key: short.test.value",
            "invalid API key: 'quoted.test.value'",
            "Incorrect API key provided: given.test.value. You can find your key on the platform.",
            "API key provided: \"bearer.test.value\"",
            "password: abc`tick.test.value",
            "API key is invalid",
            "API key was not found",
            Refused
        ])!;
        foreach (var hidden in new[] { "abc.def-hidden-value", "sk-proj-AbCdEf0123456789xyz", jwt, "0123456789abcdef0123456789abcdef0123", "short.test.value", "quoted.test.value", "given.test.value", "bearer.test.value", "tick.test.value" })
            Assert.DoesNotContain(hidden, said, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer [withheld] was refused", said, StringComparison.Ordinal);
        Assert.Contains("invalid API key: '[withheld]'", said, StringComparison.Ordinal);
        // THE VENDOR'S DIAGNOSIS IS KEPT where no value follows a separator.
        Assert.Contains("API key is invalid\nAPI key was not found", said, StringComparison.Ordinal);
        Assert.Contains("gpt-6-luna' model is not supported", said, StringComparison.Ordinal);

        var long_ = LaunchEvidence.VendorSaid([string.Join(' ', Enumerable.Repeat("refused", 1000))])!;
        Assert.Equal(LaunchEvidence.VendorSaidChars, long_.Length);
        Assert.EndsWith("characters in all)", long_, StringComparison.Ordinal);
        Assert.Null(LaunchEvidence.VendorSaid(null));
        Assert.Null(LaunchEvidence.VendorSaid(["  "]));
    }

    /// <summary>A seat whose verified surface offered these tools, as the broker seat's dry run captures them.</summary>
    sealed class Offering(IReadOnlyList<string> offered) : IAgentConversation, ISeatLaunch
    {
        public SeatVerdict CheckSeat() => SeatVerdict.None with { SeatClass = SeatClasses.Cli, Profile = "profile-hash", Offered = offered };

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// (e3) THE OFFERED TOOLS' NAMES ARE WRITTEN BESIDE THE PROFILE HASH, from the verdict admitted: the names as the surface check reads them
    /// (<see cref="BrokerSeat.ToolName"/>), sorted, distinct, one per line, in the row's own insert — and an UPDATE that changes them is refused by
    /// trigger. RED at base: only the hash was kept.
    /// </summary>
    [Fact]
    public void The_offered_tools_names_are_written_beside_the_profile_hash()
    {
        using var db = TestEnv.NewDb();
        var meter = Meter(db, TestEnv.LocalNoon());
        using (meter.Attach(new Offering(["functions.trade_status", "(mcp) functions.trade_org[1]", "functions.trade_status"]), CouncilRoles.Research))
            Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
        var row = Assert.Single(Rows(db));
        Assert.Equal(("profile-hash", "trade_org\ntrade_status"), (row.RuntimeProfile, row.OfferedTools));

        Assert.Throws<SqliteException>(() => db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE ai_attempt SET offered_tools='trade_status' WHERE id=$id", ("$id", row.Id));
            return c.ExecuteNonQuery();
        }));
    }
}
