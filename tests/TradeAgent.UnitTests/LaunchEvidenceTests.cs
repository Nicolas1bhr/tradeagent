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
            "API key value: value.test.value",
            "password was = was.test.value",
            "API key is \"is.test.value\"",
            "API key is invalid",
            "API key was not found",
            Refused
        ])!;
        foreach (var hidden in new[] { "abc.def-hidden-value", "sk-proj-AbCdEf0123456789xyz", jwt, "0123456789abcdef0123456789abcdef0123", "short.test.value", "quoted.test.value", "given.test.value", "bearer.test.value", "tick.test.value", "value.test.value", "was.test.value", "is.test.value" })
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

    /// <summary>
    /// F-C1 (R29): THE COMMON SHAPES A VENDOR ECHOES. A key name in JSON ends in a quote, a name carries a prefix, and a scheme other than
    /// Bearer stands in an Authorization header. Each value here is fake, and each input is the shape R29 ran; none of the value may survive.
    /// </summary>
    [Theory]
    [InlineData("""{"api_key":"Zq81mP4xLw0"}""", "Zq81mP4xLw0")]
    [InlineData("""{"password": "two words here"}""", "two words here")]
    [InlineData("""{\"api_key\":\"Zq81mP4xLw0\"}""", "Zq81mP4xLw0")]
    [InlineData("access_token=fakeAccess1", "fakeAccess1")]
    [InlineData("client_secret=fakeClient2", "fakeClient2")]
    [InlineData("sessionToken=fakeSession3", "fakeSession3")]
    [InlineData("pwd=fakePwd4", "fakePwd4")]
    [InlineData("the key sk_live_fake5678 was refused", "sk_live_fake5678")]
    [InlineData("the key rk_test_fake9012 was refused", "rk_test_fake9012")]
    [InlineData("Authorization: Basic QUJDREVGR0g", "QUJDREVGR0g")]
    [InlineData("Authorization: basic abcdefgh", "abcdefgh")]
    [InlineData("Authorization: Token fakeScheme6", "fakeScheme6")]
    [InlineData("sent Basic QUJDREVGR0g= and was refused", "QUJDREVGR0g=")]
    public void A_credential_in_a_common_shape_is_withheld(string line, string value)
    {
        var said = LaunchEvidence.VendorSaid([line])!;
        Assert.DoesNotContain(value, said, StringComparison.Ordinal);
        Assert.Contains(LaunchEvidence.Withheld, said, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND THE VENDOR'S DIAGNOSIS STAYS: a row that hid every word near "key" or "basic" would hide why a launch failed. These name a credential
    /// or a plan and hold no value of one.
    /// </summary>
    [Theory]
    [InlineData("""{"error":{"code":"invalid_api_key","type":"invalid_request_error"}}""")]
    [InlineData("""{"code":"invalid_api_key"}""")]
    [InlineData("basic authentication failed")]
    [InlineData("token expired")]
    [InlineData("input_tokens=1234 output_tokens=56")]
    [InlineData("key_id: 42")]
    [InlineData("API key is invalid")]
    public void The_vendors_diagnosis_is_kept(string line) =>
        Assert.Equal(line, LaunchEvidence.VendorSaid([line]));

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
