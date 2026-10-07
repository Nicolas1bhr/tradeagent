using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE VENDOR'S LIMIT SENTENCE WITH A TYPOGRAPHIC APOSTROPHE: codex now writes "You’ve" (U+2019) where
/// the 2026-10-01 recording has "You've" (U+0027), and at the owner's plan limit the app must still end
/// the turn on the vendor's words, charge a refused turn nothing and launch nothing before the minute named.
///
/// <para>OBSERVED, by seat M on 2026-10-07, in this Mac's codex session logs, read for
/// <c>usage_limit_exceeded</c> events only, with the sentence the only text taken: the three events of
/// 2026-10-01 carry U+0027, the eight of 2026-10-05/06 (Codex Desktop sessions, codex-cli 0.159.2 and
/// 0.160.1) carry U+2019. <see cref="Observed"/> and <see cref="ObservedDated"/> are two of those
/// sentences, verbatim; the apostrophe is written as the escape <c>\u2019</c> so the source shows which
/// one it is. NOT VERIFIED: that <c>codex exec --json</c>'s stdout carries this text under 0.160.1 — the
/// session log's error message does, and on 2026-10-01 the stream and the log both carried U+0027.</para>
///
/// <para>The recording, its provenance and the helpers that replay it are <see cref="VendorLimitTests"/>'s;
/// the time-zone reasoning is <see cref="VendorLimitReadTests"/>'s: a zone built here rather than the
/// machine's, so a verdict is the same on a runner in UTC as on the Mac the sentences were written on.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class VendorLimitQuoteTests : IDisposable
{
    /// <summary>OBSERVED, verbatim: codex-cli 0.159.2/0.160.1's sentence for a limit that ends later the same local day.</summary>
    public const string Observed =
        "You\u2019ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit "
        + "https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 8:49 PM.";

    /// <summary>
    /// OBSERVED, verbatim: the same sentence for a limit that ends on another local day — the dated form,
    /// which the 2026-10-01 capture knew only from the binary's strings and here seen in a vendor sentence.
    /// </summary>
    public const string ObservedDated =
        "You\u2019ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit "
        + "https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Oct 7th, 2026 1:10 AM.";

    /// <summary>
    /// DERIVED, NOT RECORDED: the 2026-10-01 recording (<see cref="VendorLimitTests.Stdout"/>) with ONLY its
    /// apostrophe changed, U+0027 to U+2019, in the two events that carry the sentence (the <c>error</c>
    /// and the <c>turn.failed</c>). Its retry time stays the recording's "10:30 PM", so the instant it
    /// names is <see cref="VendorLimitTests.Retry"/>. Written with a raw U+2019 in the UTF-8 stream, the
    /// way the JSON writer of a CLI emits a non-ASCII character unless told to escape it.
    /// </summary>
    public static readonly string Stdout =
        VendorLimitTests.Stdout.Replace("You've", "You\u2019ve", StringComparison.Ordinal);

    /// <summary>The derived stream's sentence: <see cref="VendorLimitTests.VendorSaid"/> with only its apostrophe changed.</summary>
    public static readonly string VendorSaid =
        VendorLimitTests.VendorSaid.Replace("You've", "You\u2019ve", StringComparison.Ordinal);

    /// <summary>
    /// NOT THE LIMIT: an error in the same voice and the same apostrophe that is not the usage-limit
    /// sentence. Written for this guard, not a vendor's sentence.
    /// </summary>
    const string RateLimit = "You\u2019ve hit your rate limit.";

    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"vendor-limit-quote-{Guid.NewGuid():n}.jsonl");
    readonly DateTimeOffset _at = TestEnv.LocalNoon();

    public void Dispose() => _db.Dispose();

    static UsageLimitPlan Plan => RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex").UsageLimit!;

    /// <summary>Where the sentences were written: this Mac on CEST, two hours ahead of UTC until 2026-10-25.</summary>
    static readonly TimeSpan CestOffset = TimeSpan.FromHours(2);

    static readonly TimeZoneInfo Cest =
        TimeZoneInfo.CreateCustomTimeZone("Test/UTC+2", CestOffset, "Test/UTC+2", "Test/UTC+2");

    static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    /// <summary>The END of a wall-clock minute in CEST: the vendor drops the seconds, so the limit can run to :59.</summary>
    static DateTimeOffset EndOfCestMinute(int y, int mo, int d, int h, int mi) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, CestOffset).AddMinutes(1);

    // ---- (i) the observed sentences, read by the shipped codex manifest --------------------------

    /// <summary>
    /// ITEM 2 (i), RED FIRST: THE U+2019 SENTENCE IS CODEX'S USAGE LIMIT, and "8:49 PM" — a time with no
    /// date — is the end of the minute 20:49 local on the event's own date. The instant the event is read
    /// at, 20:00 CEST on 2026-10-06, is this test's choice: the brief does not carry the event's own.
    /// </summary>
    [Fact]
    public void The_typographic_sentence_is_codexs_usage_limit_held_to_the_end_of_2049_local_on_its_own_date()
    {
        var limit = Plan.Read(Observed, Utc(2026, 10, 6, 18, 0), Cest);

        Assert.NotNull(limit);
        Assert.Equal(Observed, limit!.Message);
        Assert.Equal(EndOfCestMinute(2026, 10, 6, 20, 49), limit.RetryAt!.Value);
    }

    /// <summary>
    /// ITEM 2 (i), RED FIRST: THE DATED U+2019 SENTENCE IS THE LIMIT TOO, and "Oct 7th, 2026 1:10 AM" is the
    /// end of the minute 2026-10-07 01:10 local, read at 23:00 CEST the evening before (this test's choice).
    /// </summary>
    [Fact]
    public void The_dated_typographic_sentence_is_held_to_the_end_of_0110_local_on_2026_10_07()
    {
        var limit = Plan.Read(ObservedDated, Utc(2026, 10, 6, 21, 0), Cest);

        Assert.NotNull(limit);
        Assert.Equal(ObservedDated, limit!.Message);
        Assert.Equal(EndOfCestMinute(2026, 10, 7, 1, 10), limit.RetryAt!.Value);
    }

    // ---- (ii) the derived stream, replayed through a real session -----------------------------------

    /// <summary>The owner's own rate, so a reservation has one right answer: 1.20 M in at 1.00 and 20 k out at 4.00.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    const decimal Reservation = (1_200_000m * 1m + 20_000m * 4m) / 1_000_000m;   // 1.28

    TurnMeter Meter() =>
        new(_db, () => 50m, runtimeId: () => "codex", now: () => _at, recordPath: _records,
            owner: () => Rate, model: () => "gpt-5.6-sol", share: _ => 1m);

    /// <summary>The one launch a test made, read back from the ledger.</summary>
    AiAttempt OnlyRow() =>
        Assert.Single(new AiAttemptStore(_db).Between(_at.AddDays(-1), _at.AddDays(1)));

    /// <summary>What the row's context says the vendor said, or null — read as JSON, as the report reads it.</summary>
    static string? RefusedOn(AiAttempt row)
    {
        using var doc = JsonDocument.Parse(row.Context ?? "{}");
        return doc.RootElement.TryGetProperty("refused", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
    }

    /// <summary>
    /// ITEM 2 (ii), RED FIRST: THE DERIVED U+2019 STREAM ENDS THE TURN ON THE VENDOR'S WORDS. Replayed by a
    /// real child through a real session over the shipped codex manifest, as
    /// <see cref="VendorLimitTests.Refusing"/> replays the recording: the last line is the U+2019 sentence,
    /// not "did not finish: " and stderr's first line, and the raw stream is not shown as the AI's words.
    /// </summary>
    [Fact]
    public async Task The_typographic_refusal_ends_the_turn_on_the_vendors_own_words()
    {
        // Only the apostrophe changed: the same length, and U+2019 at each place the recording had U+0027.
        Assert.Equal(VendorLimitTests.Stdout.Length, Stdout.Length);
        var changed = Enumerable.Range(0, Stdout.Length).Where(i => Stdout[i] != VendorLimitTests.Stdout[i]).ToList();
        Assert.Equal(2, changed.Count);
        Assert.All(changed, i => Assert.Equal(('\'', '\u2019'), (VendorLimitTests.Stdout[i], Stdout[i])));

        var chat = VendorLimitTests.Refusing(nameof(The_typographic_refusal_ends_the_turn_on_the_vendors_own_words), Stdout);

        await chat.SendAsync("reply ok");

        var said = chat.History[^1];
        Assert.Equal(ChatRole.System, said.Role);
        Assert.Equal(VendorSaid, said.Text);
        Assert.DoesNotContain(chat.History, t => t.Text.Contains(VendorLimitTests.Stderr, StringComparison.Ordinal));
        Assert.DoesNotContain(chat.History, t => t.Role == ChatRole.Ai);
    }

    /// <summary>
    /// ITEM 2 (ii), RED FIRST: THE DERIVED U+2019 REFUSAL, BEFORE ANY WORK, IS CHARGED NOTHING: the launch row
    /// costs 0 with the sentence as its <c>context.refused</c>, and the day's spend is untouched. The base
    /// charged it the whole reservation, "the turn ended without reporting what it used".
    /// </summary>
    [Fact]
    public async Task The_typographic_refusal_before_any_work_is_charged_nothing()
    {
        var meter = Meter();
        var chat = VendorLimitTests.Refusing(nameof(The_typographic_refusal_before_any_work_is_charged_nothing), Stdout);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);

        await chat.SendAsync("reply ok");

        var row = OnlyRow();
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(Reservation, row.ReservedCost);
        Assert.Equal(0m, row.Cost);
        Assert.Null(row.UnpricedReason);
        Assert.Equal(VendorSaid, RefusedOn(row));

        var spend = meter.Today;
        Assert.Equal(0m, spend.Spent);
        Assert.Equal(0m, spend.Reserved);
        Assert.Equal(0, spend.UnreportedTurns);
    }

    /// <summary>The mission's world: the real wake queue, the real launch ledger, and a turn counter.</summary>
    sealed class Host(Database db, MissionEventStore events, IAgentConversation conversation, string home)
        : IMissionHost
    {
        readonly string _tag = Guid.NewGuid().ToString("n")[..8];
        int _n;

        /// <summary>Every launch the loop opened, in order. The count is the verdict.</summary>
        public List<string> Opened { get; } = [];

        public IAgentConversation? Conversation => conversation;
        public MissionEventStore? Events => events;
        public string AgentHome => home;
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MissionSituation> SituationAsync(CancellationToken ct) => Task.FromResult(new MissionSituation());

        /// <summary>The launch record through the REAL store, so a launch consumes its wakes as it does in the product.</summary>
        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes)
        {
            Opened.Add(prompt);
            return new AiAttemptStore(db).Begin(
                new AiAttempt { Id = $"turn-{_tag}-{++_n}", StartedAt = DateTimeOffset.UtcNow }, wakes);
        }
    }

    /// <summary>
    /// ITEM 2 (ii), RED FIRST: NO TURN IS LAUNCHED BEFORE THE MINUTE THE U+2019 SENTENCE NAMED. The owner asks
    /// at noon, the vendor refuses in the derived stream, the owner asks again an hour later: nothing is
    /// launched until the end of the minute named, and then one launch answers both. The base launched
    /// again at 13:00 — a refused turn charged its reservation on every look.
    /// </summary>
    [Fact]
    public async Task No_turn_is_launched_before_the_minute_the_typographic_sentence_named()
    {
        var name = nameof(No_turn_is_launched_before_the_minute_the_typographic_sentence_named);
        var home = Path.Combine(TestEnv.Home, "vendor-limit-quote", name);
        Directory.CreateDirectory(Path.Combine(home, ".tradeagent"));

        var events = new MissionEventStore(_db);
        var host = new Host(_db, events, VendorLimitTests.Refusing(name, Stdout), home);
        var now = VendorLimitTests.Today(12, 0);
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        await loop.TurnAsync();
        Assert.Single(host.Opened);                         // the turn the vendor refused

        now = VendorLimitTests.Today(13, 0);
        Assert.True(events.RecordOwnerMessage("anything new?", now));
        await loop.TurnAsync();
        Assert.Single(host.Opened);

        now = VendorLimitTests.Retry.AddMinutes(-1);        // during the minute the vendor named, still nothing
        await loop.TurnAsync();
        Assert.Single(host.Opened);

        now = VendorLimitTests.Retry;                       // once it is over, one launch, answering both
        await loop.TurnAsync();
        Assert.Equal(2, host.Opened.Count);
        Assert.Contains("how is the book?", host.Opened[1], StringComparison.Ordinal);
        Assert.Contains("anything new?", host.Opened[1], StringComparison.Ordinal);
    }

    // ---- (iii) guards: green before the change and after it ------------------------------------------

    /// <summary>
    /// GUARD: THE 2026-10-01 ASCII RECORDING IS STILL CODEX'S USAGE LIMIT, "10:30 PM" said at 18:20Z on a
    /// machine two hours ahead of UTC still ending at 20:31Z. Taking the new spelling must not drop the old.
    /// </summary>
    [Fact]
    public void The_ascii_recording_is_still_codexs_usage_limit()
    {
        var limit = Plan.Read(VendorLimitTests.VendorSaid, Utc(2026, 10, 1, 18, 20), Cest);

        Assert.NotNull(limit);
        Assert.Equal(VendorLimitTests.VendorSaid, limit!.Message);
        Assert.Equal(Utc(2026, 10, 1, 20, 31), limit.RetryAt!.Value);
    }

    /// <summary>
    /// GUARD: AN ERROR THAT IS NOT THE LIMIT SENTENCE IS NOT A LIMIT, in either apostrophe — and one that
    /// carries the words "usage limit" without being the sentence is not one either (written for this
    /// guard, not a vendor's), so a pattern widened past the apostrophe to a bare "usage limit" goes red
    /// here. Through a real session the U+2019 rate-limit error is the ordinary failed turn it always was:
    /// shown as a System line, no limit on the turn, and its reservation stands as its cost.
    /// </summary>
    [Fact]
    public async Task An_error_that_is_not_the_limit_sentence_is_not_a_limit()
    {
        var now = Utc(2026, 10, 6, 18, 0);
        Assert.Null(Plan.Read(RateLimit, now, Cest));
        Assert.Null(Plan.Read("You've hit your rate limit.", now, Cest));
        Assert.Null(Plan.Read("Could not read your usage limit: the request timed out.", now, Cest));

        var stream = Stdout.Replace(VendorSaid, RateLimit, StringComparison.Ordinal);
        Assert.Contains(RateLimit, stream, StringComparison.Ordinal);

        var meter = Meter();
        var chat = VendorLimitTests.Refusing(nameof(An_error_that_is_not_the_limit_sentence_is_not_a_limit), stream);
        AgentTurnEnded? ended = null;
        chat.TurnEnded += e => ended = e;
        using var metering = meter.Attach(chat, CouncilRoles.Operations);

        await chat.SendAsync("reply ok");

        Assert.NotNull(ended);
        Assert.Null(ended!.Limit);
        Assert.Contains(chat.History, t => t is { Role: ChatRole.System, Text: RateLimit });

        var row = OnlyRow();
        Assert.Equal(Reservation, row.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, row.UnpricedReason);
        Assert.Null(RefusedOn(row));
    }
}
