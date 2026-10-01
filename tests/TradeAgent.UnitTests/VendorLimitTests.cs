using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHEN THE AI'S OWN PLAN RUNS OUT: the vendor's words on the card, no launch before the time the
/// vendor named, and a refused turn charged nothing.
///
/// <para>Every stream here starts from ONE recording, and nothing in it was written to make a parser
/// pass: codex-cli 0.153.4 on this Mac, 2026-10-01T18:20:07Z, <c>codex exec --json
/// --skip-git-repo-check "reply ok"</c> in a scratch folder with stdin closed, run once while the
/// owner's plan was limited. It exited 1 after four seconds, having printed exactly what
/// <see cref="Stdout"/> and <see cref="Stderr"/> hold. The thread id is the one thing replaced.</para>
///
/// <para>The owner's local clock read 20:20 CEST when it said "10:30 PM": the vendor prints its
/// retry time in the machine's own zone, so that is 20:30Z, which is when the brief says the plan
/// came back.</para>
///
/// <para>These tests reach the product only through members the base already had — the session's
/// history, the loop's launches and card, the launch ledger's rows — so they ran, and failed, on the
/// base before any of this unit existed. The typed reason is pressed in
/// <c>VendorLimitReadTests</c>.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class VendorLimitTests : IDisposable
{
    /// <summary>The recorded standard output, verbatim but for the thread id.</summary>
    public const string Stdout =
        """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
        {"type":"turn.started"}
        {"type":"error","message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 10:30 PM."}
        {"type":"turn.failed","error":{"message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 10:30 PM."}}
        """;

    /// <summary>The recorded standard error, verbatim. The line the card used to show as the reason.</summary>
    public const string Stderr = "Reading additional input from stdin...";

    /// <summary>What the vendor said, as the message of its own <c>error</c> event.</summary>
    public const string VendorSaid =
        "You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit "
        + "https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 10:30 PM.";

    /// <summary>
    /// THE SAME REFUSAL, CUT INTO A TURN THAT HAD ALREADY WORKED — the shape of the observed attempt
    /// <c>…1802091</c>, which ran twenty commands and was then stopped by the same limit. Derived,
    /// not recorded: the recording above with one command item in front of the refusal, in the
    /// item shape codex 0.153.4 was measured emitting on 2026-09-07 (<see cref="TurnContext"/>).
    /// </summary>
    public const string CutAfterWork =
        """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
        {"type":"turn.started"}
        {"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"ls","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"ls","aggregated_output":"PLAN.md\n","exit_code":0,"status":"completed"}}
        {"type":"error","message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 10:30 PM."}
        {"type":"turn.failed","error":{"message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 10:30 PM."}}
        """;

    /// <summary>
    /// The vendor's other ending, "or try again later.", which states no time. Derived: the text is
    /// the recording's with the suffix codex 0.153.4's own binary carries for a limit with no reset
    /// time (<c>" or try again later."</c>, read out of the executable with <c>strings</c>).
    /// </summary>
    public const string NoTimeNamed =
        """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000000"}
        {"type":"turn.started"}
        {"type":"error","message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again later."}
        {"type":"turn.failed","error":{"message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again later."}}
        """;

    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"vendor-limit-{Guid.NewGuid():n}.jsonl");

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// CODEX'S OWN SHIPPED MANIFEST, pointed at the recording. Only the program and its arguments
    /// are replaced; the display name, the stream flag and whatever the manifest says about this
    /// vendor's limit are the build's own data.
    /// </summary>
    internal static RuntimeManifest Codex(string script)
    {
        var m = RuntimeCatalog.BuiltIn().Single(x => x.Id == "codex");
        m.Executable = script;
        m.ExecArgs = ["{prompt}"];
        m.ResumeArgs = ["{prompt}"];
        m.UnattendedArgs = [];
        m.ModelArgs = [];
        return m;
    }

    /// <summary>The recording, replayed by a real child process through a real session.</summary>
    internal static AgentSession Refusing(string name, string stream = Stdout) =>
        AgentRuntimeProbe.SessionOverStream(stream, stderr: Stderr, exitCode: 1, manifest: Codex, name: name);

    /// <summary>A wall-clock minute TODAY, on the offset this machine's zone has at that minute.</summary>
    internal static DateTimeOffset Today(int hour, int minute)
    {
        var local = DateTime.Today.AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>
    /// WHEN THE LIMIT IS CERTAINLY OVER: the END of the minute the vendor named. It prints minutes and
    /// drops the seconds, so at 22:30:00 the limit may still have up to 59 seconds to run.
    /// </summary>
    internal static DateTimeOffset Retry => Today(22, 30).AddMinutes(1);

    // ---- (a) the vendor's words -------------------------------------------------------------------

    /// <summary>
    /// THE FINDING, AS THE OBSERVED RUN SHOWED IT: the card said "OpenAI Codex CLI did not finish:
    /// Reading additional input from stdin..." — stderr's first line, which codex prints on every run,
    /// successful or not — while the vendor's own sentence, the one saying what happened and when it
    /// ends, was a line further up. The last thing the conversation says is now the vendor's, and the
    /// raw stream is not shown as words the AI said.
    /// </summary>
    [Fact]
    public async Task A_turn_the_vendor_refused_ends_on_its_own_words_and_not_on_stderrs_first_line()
    {
        var session = Refusing(nameof(A_turn_the_vendor_refused_ends_on_its_own_words_and_not_on_stderrs_first_line));

        await session.SendAsync("reply ok");

        var said = session.History[^1];
        Assert.Equal(ChatRole.System, said.Role);
        Assert.Equal(VendorSaid, said.Text);
        Assert.DoesNotContain(session.History, t => t.Text.Contains(Stderr, StringComparison.Ordinal));
        Assert.DoesNotContain(session.History, t => t.Role == ChatRole.Ai);
    }

    // ---- (b) the wait -----------------------------------------------------------------------------

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

    static string Home(string name)
    {
        var home = Path.Combine(TestEnv.Home, "vendor-limit", name);
        Directory.CreateDirectory(Path.Combine(home, ".tradeagent"));
        return home;
    }

    static int OwnerWakesWaiting(MissionEventStore events, DateTimeOffset now) =>
        events.Due(now).Count(e => e.Kind == MissionEventKind.Owner);

    /// <summary>
    /// ITEM (b), RED FIRST: NO TURN IS LAUNCHED BEFORE THE TIME THE VENDOR NAMED, and the wakes that
    /// arrive meanwhile are kept rather than spent on turns the vendor would refuse.
    ///
    /// <para>The observed run scheduled its next look for 18:38Z, inside a limit that ran to 20:30Z,
    /// and every look launched a turn — each one billed its whole reservation and each one consumed
    /// the wakes it was answering. Here the owner asks at noon, the vendor refuses, the owner asks
    /// again an hour later and the loop is woken for it: nothing is launched until the minute the
    /// vendor named is over, and then the launch answers both messages. The owner's own chat is not
    /// held — a typed message still runs a turn of its own — and Pause still pauses.</para>
    /// </summary>
    [Fact]
    public async Task No_turn_is_launched_before_the_time_the_vendor_named()
    {
        var name = nameof(No_turn_is_launched_before_the_time_the_vendor_named);
        var events = new MissionEventStore(_db);
        var session = Refusing(name);
        var host = new Host(_db, events, session, Home(name));
        var now = Today(12, 0);
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        await loop.TurnAsync();
        Assert.Single(host.Opened);                         // the turn the vendor refused

        // INSIDE THE LIMIT the owner asks again, and the loop is woken to look.
        now = Today(13, 0);
        Assert.True(events.RecordOwnerMessage("anything new?", now));
        var waited = await loop.TurnAsync();
        Assert.Single(host.Opened);
        Assert.Equal(2, OwnerWakesWaiting(events, now));    // the refused one re-raised, and the new one

        // And it looks again within the loop's own ceiling rather than sleeping out nine hours: the
        // app's sweeps at the top of every look must not wait for a vendor.
        Assert.Equal(options.MaxDelay, waited);

        // THE CARD SAYS WHY, in words the owner can act on, and until when.
        Assert.Equal($"Operations Director: OpenAI Codex CLI's usage limit is reached — the AI waits until {Retry:HH:mm}",
            DashboardPage.MissionSentence(loop.Status with { State = MissionState.Waiting }));

        // THE OWNER'S OWN CHAT IS NOT HELD: what they type still runs a turn of its own.
        var before = session.History.Count;
        await session.SendAsync("are you there?");
        Assert.Contains(session.History.Skip(before), t => t is { Role: ChatRole.System, Text: VendorSaid });
        Assert.Single(host.Opened);                         // and it was not one of the loop's

        // During the minute the vendor named, still nothing.
        now = Retry.AddMinutes(-1);
        await loop.TurnAsync();
        Assert.Single(host.Opened);
        Assert.Equal(2, OwnerWakesWaiting(events, now));

        // Once it is over, one launch, answering both.
        now = Retry;
        await loop.TurnAsync();
        Assert.Equal(2, host.Opened.Count);
        Assert.Contains("how is the book?", host.Opened[1], StringComparison.Ordinal);
        Assert.Contains("anything new?", host.Opened[1], StringComparison.Ordinal);

        await loop.PauseAsync();
        Assert.Equal(MissionState.Paused, loop.Status.State);
    }

    /// <summary>
    /// A LIMIT THAT NAMES NO TIME WAITS THE MISSION'S OWN BACKOFF, exactly as any failed turn does —
    /// the first step of it, thirty seconds. A guard: it was the base's answer for every turn, and it
    /// stays the answer for the one turn whose vendor gave nothing better.
    /// </summary>
    [Fact]
    public async Task A_limit_that_names_no_time_waits_the_missions_own_backoff()
    {
        var name = nameof(A_limit_that_names_no_time_waits_the_missions_own_backoff);
        var events = new MissionEventStore(_db);
        var host = new Host(_db, events, Refusing(name, NoTimeNamed), Home(name));
        var now = Today(12, 0);
        var options = new MissionOptions { ReviewEvery = TimeSpan.Zero };
        var loop = new MissionLoop(host, options, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        var wait = await loop.TurnAsync();

        Assert.Single(host.Opened);
        Assert.Equal(options.FirstBackoff, wait);
    }

    // ---- (c) the charge ---------------------------------------------------------------------------

    /// <summary>The owner's own rate, so a reservation has one right answer: 1.20 M in at 1.00 and 20 k out at 4.00.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    const decimal Reservation = (1_200_000m * 1m + 20_000m * 4m) / 1_000_000m;   // 1.28

    TurnMeter Meter() =>
        new(_db, () => 50m, runtimeId: () => "codex", now: () => DateTimeOffset.Now, recordPath: _records,
            owner: () => Rate, model: () => "gpt-5.6-sol", share: _ => 1m);

    /// <summary>The one launch a test made, read back from the ledger.</summary>
    AiAttempt OnlyRow() =>
        Assert.Single(new AiAttemptStore(_db).Between(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));

    /// <summary>What the row's context says the vendor said, or null — read as JSON, as the report reads it.</summary>
    static string? RefusedOn(AiAttempt row)
    {
        using var doc = JsonDocument.Parse(row.Context ?? "{}");
        return doc.RootElement.TryGetProperty("refused", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
    }

    /// <summary>
    /// ITEM (c), RED FIRST: A TURN THE VENDOR REFUSED BEFORE ANY WORK IS CHARGED NOTHING, with the
    /// vendor's reason on its row.
    ///
    /// <para>The observed attempt <c>…1808061</c> ran 2.9 s, showed no item at all and reported no
    /// usage; the app charged it the full 0.324 reservation, "the turn ended without reporting what it
    /// used". That rule is right for a turn whose work nobody measured and wrong for this one: the
    /// stream itself says the vendor refused the first request, and a turn the vendor did not run is
    /// not a turn it billed. It was still RESERVED before it ran, which is what the reservation is
    /// for — the commitment is made before anyone can know.</para>
    /// </summary>
    [Fact]
    public async Task A_turn_the_vendor_refused_before_any_work_is_charged_nothing()
    {
        var meter = Meter();
        var chat = Refusing(nameof(A_turn_the_vendor_refused_before_any_work_is_charged_nothing));
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

    /// <summary>
    /// AND A TURN CUT BY THE LIMIT AFTER IT HAD WORKED KEEPS ITS RESERVATION AS ITS COST, as it did.
    /// It ran a command, so the vendor ran model requests for it, and nothing reported what they
    /// used: unknown is never zero. The reason is on the row all the same.
    /// </summary>
    [Fact]
    public async Task A_turn_the_limit_cut_after_it_had_worked_keeps_its_reservation_as_its_cost()
    {
        var meter = Meter();
        var chat = Refusing(nameof(A_turn_the_limit_cut_after_it_had_worked_keeps_its_reservation_as_its_cost),
            CutAfterWork);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);

        await chat.SendAsync("reply ok");

        var row = OnlyRow();
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(Reservation, row.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, row.UnpricedReason);
        Assert.Equal(VendorSaid, RefusedOn(row));
        Assert.Equal(1, meter.Today.UnreportedTurns);
    }

    /// <summary>
    /// A TURN THAT REPORTED WHAT IT USED IS CHARGED THAT, limit or no limit. A guard: nothing about
    /// recognising the vendor's refusal may change what a turn with measured usage costs.
    /// </summary>
    [Fact]
    public async Task A_refused_turn_that_reported_usage_is_charged_its_usage()
    {
        var stream = Stdout.Replace("{\"type\":\"turn.started\"}",
            "{\"type\":\"turn.started\"}\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1000,\"cached_input_tokens\":0,\"output_tokens\":10}}",
            StringComparison.Ordinal);
        var meter = Meter();
        var chat = Refusing(nameof(A_refused_turn_that_reported_usage_is_charged_its_usage), stream);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);

        await chat.SendAsync("reply ok");

        var row = OnlyRow();
        Assert.Equal((1_000m * 1m + 10m * 4m) / 1_000_000m, row.Cost);
        Assert.Null(row.UnpricedReason);
    }
}
