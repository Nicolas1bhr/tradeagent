using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TYPED REASON: the vendor's refusal read out of the recorded stream as data, the time it named
/// resolved to an instant, and a hold that holds the runtime that refused and no other.
///
/// <para>The recording and its provenance are <see cref="VendorLimitTests"/>'s. The time-zone cases use
/// zones built here rather than the machine's, so a verdict about 20:30Z is the same verdict on a runner
/// in UTC as on the Mac it was recorded on — and the build runs with invariant globalization, under which
/// a Windows runner cannot look an IANA zone up by name at all.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class VendorLimitReadTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();

    public void Dispose() => _db.Dispose();

    static UsageLimitPlan Plan => RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex").UsageLimit!;

    /// <summary>Where the recording was made: CEST, two hours ahead of UTC, on 2026-10-01.</summary>
    static readonly TimeZoneInfo Cest =
        TimeZoneInfo.CreateCustomTimeZone("Test/UTC+2", TimeSpan.FromHours(2), "Test/UTC+2", "Test/UTC+2");

    /// <summary>
    /// The European rule, built by hand: CET, an hour ahead from 02:00 on the last Sunday of March until
    /// 03:00 summer time on the last Sunday of October.
    /// </summary>
    static readonly TimeZoneInfo Europe = TimeZoneInfo.CreateCustomTimeZone("Test/Europe", TimeSpan.FromHours(1),
        "Test/Europe", "CET", "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))
        ]);

    static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    static string Saying(string when) => VendorLimitTests.VendorSaid.Replace("10:30 PM", when, StringComparison.Ordinal);

    // ---- (a) the reason and the instant -----------------------------------------------------------

    /// <summary>
    /// ITEM (a), RED FIRST: THE RECORDING IS READ AS CODEX'S USAGE LIMIT, and "10:30 PM", said at
    /// 18:20Z on a machine two hours ahead of UTC, is 20:30Z — the time the brief says the plan came
    /// back. The instant is the END of that minute, because the vendor drops the seconds.
    /// </summary>
    [Fact]
    public void The_recorded_refusal_is_codexs_usage_limit_and_the_minute_it_named_ends_at_2031Z()
    {
        var limit = Plan.Read(VendorLimitTests.VendorSaid, Utc(2026, 10, 1, 18, 20), Cest);

        Assert.NotNull(limit);
        Assert.Equal(VendorLimitTests.VendorSaid, limit!.Message);
        Assert.Equal(Utc(2026, 10, 1, 20, 31), limit.RetryAt!.Value);
    }

    /// <summary>
    /// NOTHING ELSE IS READ AS ONE: not the stderr line every run prints, not the CLI's own retry chatter,
    /// not an empty message — and OpenCode, whose refusal nobody has recorded, recognises nothing.
    /// </summary>
    [Fact]
    public void What_is_not_the_vendors_limit_is_not_read_as_one()
    {
        var now = Utc(2026, 10, 1, 18, 20);

        Assert.Null(Plan.Read(VendorLimitTests.Stderr, now, Cest));
        Assert.Null(Plan.Read("Reconnecting... 1/5 (stream disconnected before completion)", now, Cest));
        Assert.Null(Plan.Read("", now, Cest));
        Assert.Null(RuntimeCatalog.BuiltIn().Single(m => m.Id == "opencode").UsageLimit);
    }

    /// <summary>A limit that names no time is still the limit, with no instant — and the loop waits its backoff.</summary>
    [Fact]
    public void A_limit_that_names_no_time_is_read_with_no_instant()
    {
        var limit = Plan.Read(VendorLimitTests.VendorSaid.Replace(" at 10:30 PM.", " later.", StringComparison.Ordinal),
            Utc(2026, 10, 1, 18, 20), Cest);

        Assert.NotNull(limit);
        Assert.Null(limit!.RetryAt);
    }

    /// <summary>
    /// A TIME ON ANOTHER DAY carries its date, in the binary's own second format. Read from the
    /// executable with <c>strings</c> and never seen in a stream, which is why it is pressed here.
    /// </summary>
    [Fact]
    public void A_time_on_another_day_is_read_on_that_day()
    {
        var now = Utc(2026, 10, 1, 18, 20);

        Assert.Equal(Utc(2026, 10, 3, 20, 31), Plan.Read(Saying("Oct 3rd, 2026 10:30 PM"), now, Cest)!.RetryAt!.Value);
        Assert.Equal(Utc(2026, 11, 1, 7, 6), Plan.Read(Saying("Nov 1st, 2026 9:05 AM"), now, Cest)!.RetryAt!.Value);
    }

    /// <summary>
    /// THE HOUR THE CLOCK PASSES TWICE is the LATER of its two instants: on 2026-10-25 "2:30 AM" is both
    /// 00:30Z and 01:30Z, and only the second is certainly after the limit. The hour it skips cannot be
    /// printed by a vendor converting a real instant; it is moved forward rather than refused.
    /// </summary>
    [Fact]
    public void An_hour_the_clock_passes_twice_is_read_as_the_later_of_its_two_instants()
    {
        Assert.Equal(Utc(2026, 10, 25, 1, 31),
            Plan.Read(Saying("2:30 AM"), Utc(2026, 10, 25, 0, 10), Europe)!.RetryAt!.Value);

        Assert.Equal(Utc(2027, 3, 28, 1, 31),
            Plan.Read(Saying("2:30 AM"), Utc(2027, 3, 28, 0, 10), Europe)!.RetryAt!.Value);
    }

    /// <summary>
    /// THE TURN ENDS CARRYING IT: the recording, replayed through a real session over the shipped codex
    /// manifest, ends with the vendor's name, its sentence, the end of the minute it named on this
    /// machine's clock, and the fact that nothing came before it. The same refusal after one command is
    /// the same reason with work before it.
    /// </summary>
    [Fact]
    public async Task The_turn_ends_carrying_the_vendors_reason_its_instant_and_whether_work_came_first()
    {
        AgentTurnEnded? refused = null, cut = null;

        var first = VendorLimitTests.Refusing(nameof(The_turn_ends_carrying_the_vendors_reason_its_instant_and_whether_work_came_first));
        first.TurnEnded += e => refused = e;
        await first.SendAsync("reply ok");

        var second = VendorLimitTests.Refusing(nameof(The_turn_ends_carrying_the_vendors_reason_its_instant_and_whether_work_came_first) + "-cut",
            VendorLimitTests.CutAfterWork);
        second.TurnEnded += e => cut = e;
        await second.SendAsync("reply ok");

        var limit = refused!.Limit!;
        Assert.Equal(1, refused.ExitCode);
        Assert.Null(refused.Usage);
        Assert.Equal("OpenAI Codex CLI", limit.Vendor);
        Assert.Equal(VendorLimitTests.VendorSaid, limit.Message);
        Assert.Equal(VendorLimitTests.Retry, limit.RetryAt!.Value);
        Assert.True(limit.BeforeAnyWork);

        Assert.Equal(VendorLimitTests.VendorSaid, cut!.Limit!.Message);
        Assert.False(cut.Limit.BeforeAnyWork);
    }

    // ---- (b) the hold, per runtime --------------------------------------------------------------

    /// <summary>Two roles on two runtimes: the chair on the vendor CLI, Research on something else.</summary>
    sealed class CouncilHost(Database db, MissionEventStore events, string home) : IMissionHost
    {
        readonly string _tag = Guid.NewGuid().ToString("n")[..8];
        int _n;

        public Dictionary<string, IAgentConversation> Conversations { get; } = [];
        public Dictionary<string, string> Runtimes { get; } = [];
        public List<(string Role, VendorHold Hold)> Told { get; } = [];

        /// <summary>The role of every launch, in order.</summary>
        public List<string> Opened { get; } = [];

        public IAgentConversation? Conversation => Conversations[CouncilRoles.Operations];
        public IAgentConversation? ConversationFor(string role) => Conversations[role];
        public string? RuntimeFor(string role) => Runtimes[role];
        public void VendorLimitReached(string role, VendorHold hold) => Told.Add((role, hold));

        public MissionEventStore? Events => events;
        public string AgentHome => home;
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) => Task.FromResult(new MissionSituation());

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes, string role)
        {
            Opened.Add(role);
            return new AiAttemptStore(db).Begin(
                new AiAttempt { Id = $"turn-{_tag}-{++_n}", StartedAt = DateTimeOffset.UtcNow, Role = role }, wakes);
        }
    }

    /// <summary>
    /// THE OWNER IS TOLD ONCE, AND ONLY THE RUNTIME THAT REFUSED IS HELD. The chair's codex turn is
    /// refused; an hour later both roles have a reason to work, and Research — on another runtime, billed
    /// to another account — takes its turn while the chair's wakes wait. The next look launches nothing,
    /// and says nothing to the log a second time.
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_once_and_only_the_runtime_that_refused_is_held()
    {
        var name = nameof(The_owner_is_told_once_and_only_the_runtime_that_refused_is_held);
        var events = new MissionEventStore(_db);
        var home = Path.Combine(TestEnv.Home, "vendor-limit", name);
        Directory.CreateDirectory(Path.Combine(home, ".tradeagent"));

        var host = new CouncilHost(_db, events, home);
        host.Conversations[CouncilRoles.Operations] = VendorLimitTests.Refusing(name);
        host.Conversations[CouncilRoles.Research] =
            AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, name: name + "-research");
        host.Runtimes[CouncilRoles.Operations] = "codex";
        host.Runtimes[CouncilRoles.Research] = ApiAgentRuntime.RuntimeId;

        var now = VendorLimitTests.Today(12, 0);
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        await loop.TurnAsync();
        Assert.Equal([CouncilRoles.Operations], host.Opened);

        var (role, hold) = Assert.Single(host.Told);
        Assert.Equal(CouncilRoles.Operations, role);
        Assert.Equal("OpenAI Codex CLI", hold.Vendor);
        Assert.Equal(VendorLimitTests.VendorSaid, hold.Message);
        Assert.Equal(VendorLimitTests.Retry, hold.Until);

        now = VendorLimitTests.Today(13, 0);
        Assert.True(events.RecordOwnerMessage("anything new?", now));
        events.Raise(MissionEventIds.ForRole(MissionEventIds.Review(now), CouncilRoles.Research),
            MissionEventKind.Review, now, role: CouncilRoles.Research);

        await loop.TurnAsync();
        Assert.Equal([CouncilRoles.Operations, CouncilRoles.Research], host.Opened);

        await loop.TurnAsync();
        Assert.Equal([CouncilRoles.Operations, CouncilRoles.Research], host.Opened);
        Assert.Single(host.Told);
        Assert.Equal(hold, loop.Status.Held);
    }

    /// <summary>
    /// THE CARD AND THE ACTIVITY LOG SAY WHOSE LIMIT AND UNTIL WHEN, one sentence for both, and then the
    /// log gives the vendor's own words — the part that says what the owner can do about it. A hold that
    /// ends on another day says which day, or "until 22:31" would be read as tonight.
    /// </summary>
    [Fact]
    public void The_card_and_the_activity_log_say_whose_limit_and_until_when()
    {
        var noon = VendorLimitTests.Today(12, 0);
        var hold = new VendorHold("OpenAI Codex CLI", VendorLimitTests.VendorSaid, VendorLimitTests.Retry);

        Assert.Equal("OpenAI Codex CLI's usage limit is reached — the AI waits until 22:31", hold.Sentence(noon));
        Assert.Equal("OpenAI Codex CLI's usage limit is reached — the AI waits until 22:31. OpenAI Codex CLI said: "
                     + VendorLimitTests.VendorSaid,
            AppHost.VendorLimitLine(hold, noon));

        // 22:31 on the owner's wall clock two days from now, whatever the zone does in between.
        var day = DateTime.Today.AddDays(2).AddHours(22).AddMinutes(31);
        var later = hold with { Until = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)) };
        Assert.Equal($"OpenAI Codex CLI's usage limit is reached — the AI waits until {day:yyyy-MM-dd} 22:31",
            later.Sentence(noon));
    }
}
