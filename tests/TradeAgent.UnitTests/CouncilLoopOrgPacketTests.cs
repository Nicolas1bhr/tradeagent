using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE LOOP'S HALF OF A POSITION'S PACKET (<c>U-org-packets-b</c>; ORCHESTRATION § 2.6: "A position's cursor advances only with the
/// attempt's committed outcome"; § 2.10: "a lateral note is app-relayed into the recipient's next packet … it wakes nobody"). Every
/// position here runs on the SEAM HOST <see cref="CouncilHost"/>: conversations are test doubles, the ledgers are real. No real model.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>The lineage rows of <paramref name="attempt"/>, as kind:id.</summary>
    static List<string> InputsOf(Database db, string attempt) =>
        [.. new AiAttemptStore(db).InputsOf(attempt).Select(i => $"{i.Kind}:{i.RefId}")];

    /// <summary>Each launch of <paramref name="position"/>, in launch order.</summary>
    static List<string> LaunchesOf(Database db, string position)
    {
        using var c = db.Cmd("SELECT id FROM ai_attempt WHERE role=$r ORDER BY rowid", ("$r", position));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>
    /// (a) A NOTE RIDES ITS RECIPIENT'S NEXT PACKET ONCE — AND AGAIN AFTER A LOST OR GATE-REFUSED LAUNCH. The head sends the executor a
    /// note, which wakes nobody; the executor's own self-wakes then launch it five times: the gate refuses the first at its last check
    /// (its packet carried the note, nothing recorded), the second's transition never commits (a launch the next start loses; nothing
    /// recorded), the third launches no session (its process never started; nothing recorded), the fourth commits and records the note
    /// as its input, and the fifth's packet no longer carries it.
    ///
    /// <para>RED at base: no packet carried a note (the first send held no <c>lat-</c> id). MUTANT, the lineage written whatever the
    /// launch's ending (the gate-refusal condition removed): the second launch's packet no longer carried the note.</para>
    /// </summary>
    [Fact]
    public async Task A_note_rides_the_recipients_next_packet_once_and_again_after_a_lost_or_gate_refused_launch()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor,
            "The funding skew looks venue-specific.\nCheck the second venue before you trust it.", [], DateTimeOffset.UtcNow);
        Assert.DoesNotContain(host.Events!.Due(DateTimeOffset.UtcNow.AddMinutes(1)), e => e.Role == Executor);   // it woke nobody

        var executor = host.Conversations[Executor];
        executor.Session = TurnSession.Resumed;
        executor.GateRefused = new LateRefusal(new TurnRefusal(RefusalKind.HostOrLaunch, "the host's cell proof changed"), true);
        SelfWake(host, Executor, "packet-1");
        await TurnsUntil(loop, host, Executor, 1);

        executor.GateRefused = null;
        host.CommitThrows = true;
        SelfWake(host, Executor, "packet-2");
        await TurnsUntil(loop, host, Executor, 2);

        host.CommitThrows = false;
        executor.Session = null;
        SelfWake(host, Executor, "packet-3");
        await TurnsUntil(loop, host, Executor, 3);

        executor.Session = TurnSession.Resumed;
        SelfWake(host, Executor, "packet-4");
        await TurnsUntil(loop, host, Executor, 4);

        SelfWake(host, Executor, "packet-5");
        await TurnsUntil(loop, host, Executor, 5);

        var sent = executor.Sent;
        Assert.All(sent.Take(4), prompt => Assert.Contains($"Note `{note.Id}` from `{TeamHead}`", prompt, StringComparison.Ordinal));
        Assert.DoesNotContain(note.Id, sent[4], StringComparison.Ordinal);

        var launches = LaunchesOf(db, Executor);
        Assert.Equal(5, launches.Count);
        Assert.Empty(InputsOf(db, launches[0]));   // the gate refused it at its last check
        Assert.Empty(InputsOf(db, launches[1]));   // its transition never committed
        Assert.Empty(InputsOf(db, launches[2]));   // it launched no session
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, launches[3]));
        Assert.Empty(InputsOf(db, launches[4]));
        Assert.Empty(host.InputsOutsideCommit);
    }

    /// <summary>
    /// A HARNESS TURN THAT HANDED NO REQUEST OVER RECORDS NO INPUT, AND ITS NOTE RIDES THE NEXT PACKET (fix pass 1, the review's
    /// first point; ORCHESTRATION § 2.6: the cursor advances only with an outcome a model was shown). The executor runs on a REAL
    /// harness conversation over the loopback fake, twice: its seat's input allowance is smaller than its first request's body, so
    /// that request is withheld; then its worker tools throw before any request is built. Neither hands anything to the transport —
    /// the provider receives nothing — so neither records the note, and the executor's next launch is still shown it.
    ///
    /// <para>RED at base 4d38cada: "Assert.Empty() Failure: Collection was not empty Collection: ["lateral:lat-0752a1b6544b"]" — the
    /// withheld turn named a fresh session, so its packet was marked shown. MUTANT, the session keyed on the exit code again
    /// (<c>_sent || exitCode != NotStarted</c>): the same failure; with only the withheld turn excused, the thrown one's lineage
    /// held the note (line 143).</para>
    /// </summary>
    [Fact]
    public async Task A_harness_turn_that_handed_no_request_over_records_no_input_and_its_note_rides_the_next_packet()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor, "The second venue has gaps.", [], DateTimeOffset.UtcNow);

        using var provider = new FakeProvider();
        // A MANIFEST BUILT HERE, never the catalogue's: nothing here reads the override file another test may be writing.
        var manifest = new RuntimeManifest
        {
            Id = ApiAgentRuntime.RuntimeId, DisplayName = "Loopback harness", BaseUrl = provider.BaseUrl,
            CompletionsPath = "chat/completions", DefaultModel = "loopback-model"
        };
        using var withheld = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            allowance: _ => new TurnAllowance(100, 20_000), requestTimeout: TimeSpan.FromSeconds(10));
        using var threw = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            tools: _ => throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "the worker's tools could not be read"),
            requestTimeout: TimeSpan.FromSeconds(10));

        var endings = new List<AgentTurnEnded>();
        foreach (var (runtime, tag) in new[] { (withheld, "harness-withheld"), (threw, "harness-threw") })
        {
            var conversation = runtime.OpenConversation(Executor, () => host.HomeFor(Executor),
                () => new Dictionary<string, string>(), () => null);
            conversation.TurnEnded += endings.Add;
            host.Harnessed[Executor] = conversation;
            var before = endings.Count;
            SelfWake(host, Executor, tag);
            for (var i = 0; i < 12 && endings.Count == before; i++) await loop.TurnAsync();
            Assert.Equal(before + 1, endings.Count);
        }
        Assert.Empty(provider.Requests);
        Assert.NotNull(endings[0].FirstRequestWithheld);
        Assert.Equal(ApiConversation.EndedProviderFailed, endings[1].Outcome);

        host.Harnessed.Remove(Executor);
        host.Conversations[Executor].Session = TurnSession.Resumed;
        SelfWake(host, Executor, "double-after");
        await TurnsUntil(loop, host, Executor, 1);

        var launches = LaunchesOf(db, Executor);
        Assert.Equal(3, launches.Count);
        Assert.Empty(InputsOf(db, launches[0]));   // its first request was withheld
        Assert.Empty(InputsOf(db, launches[1]));   // it threw before any request was built
        Assert.Contains($"Note `{note.Id}` from `{TeamHead}`", host.Conversations[Executor].Sent[0], StringComparison.Ordinal);
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, launches[2]));
    }

    /// <summary>
    /// A HARNESS TURN WHOSE REQUEST NO PROVIDER ACCEPTED RECORDS NO INPUT, AND ITS NOTE RIDES THE NEXT PACKET (fix pass 2, the
    /// re-review's point; ORCHESTRATION § 2.6: input lineage is what the attempt was GIVEN, and a packet no model received was given
    /// to nobody). The executor runs on a REAL harness conversation twice, and each time its first request IS handed to the
    /// transport — so each names part a's session — but no model reads it: the transport cannot connect (an
    /// <see cref="HttpRequestException"/>), then the provider answers 401, as it does to a wrong pasted key. Neither records the note;
    /// the third turn, which the provider accepts, is still shown it and records it.
    ///
    /// <para>RED at base b82882fa (before <see cref="AgentTurnEnded.NoRequestAccepted"/> existed to assert): "Assert.Empty() Failure:
    /// Collection was not empty Collection: ["lateral:lat-cee8b9b615c1"]" — the unconnected turn's packet was marked shown; with the
    /// 401 turn first, the same failure on it. MUTANT, <c>NoRequestAccepted: false</c> taken out of the commit: the same failure.</para>
    /// </summary>
    [Fact]
    public async Task A_harness_turn_whose_request_no_provider_accepted_records_no_input_and_its_note_rides_the_next_packet()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor, "The second venue has gaps.", [], DateTimeOffset.UtcNow);

        using var provider = new FakeProvider { AlwaysAnswer = System.Net.HttpStatusCode.Unauthorized };
        // A MANIFEST BUILT HERE, never the catalogue's: nothing here reads the override file another test may be writing.
        var manifest = new RuntimeManifest
        {
            Id = ApiAgentRuntime.RuntimeId, DisplayName = "Loopback harness", BaseUrl = provider.BaseUrl,
            CompletionsPath = "chat/completions", DefaultModel = "loopback-model"
        };
        // THE GATE PASSES: the executor's seat is the harness on a host holding a proven cell, so each turn reaches its first request.
        ThinkingSeatsTests.ProveCell(db);
        var seats = ThinkingSeatsTests.Register(db, SeatClasses.Harness);
        using var unreachable = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            requestTimeout: TimeSpan.FromSeconds(10), transport: new Unconnectable(), seats: seats);
        using var refused = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            requestTimeout: TimeSpan.FromSeconds(10), seats: seats);
        using var accepted = new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"),
            requestTimeout: TimeSpan.FromSeconds(10), seats: seats);

        var endings = new List<AgentTurnEnded>();
        foreach (var (runtime, tag) in new[] { (unreachable, "harness-unreachable"), (refused, "harness-401"), (accepted, "harness-200") })
        {
            // THE THIRD TURN THE PROVIDER ACCEPTS: an answer in 2xx, so a model received its packet.
            if (tag == "harness-200")
            {
                provider.AlwaysAnswer = null;
                provider.Answer(FakeProvider.Message("Noted: the second venue has gaps."));
            }
            var conversation = runtime.OpenConversation(Executor, () => host.HomeFor(Executor),
                () => new Dictionary<string, string>(), () => null);
            conversation.TurnEnded += endings.Add;
            host.Harnessed[Executor] = conversation;
            var before = endings.Count;
            SelfWake(host, Executor, tag);
            for (var i = 0; i < 12 && endings.Count == before; i++) await loop.TurnAsync();
            Assert.Equal(before + 1, endings.Count);
        }
        // EACH HANDED A REQUEST OVER — part a's session, unchanged: the requests left this app — AND THE FIRST TWO FAILED: one never
        // connected, one the provider answered 401. Only the third was accepted.
        Assert.All(endings, e => Assert.NotNull(e.Session));
        Assert.Equal([true, true, false], endings.Select(e => e.NoRequestAccepted));
        Assert.All(endings.Take(2), e => Assert.Equal(ApiConversation.EndedProviderFailed, e.Outcome));
        Assert.Equal(2, provider.Requests.Count);

        var launches = LaunchesOf(db, Executor);
        Assert.Equal(3, launches.Count);
        Assert.Empty(InputsOf(db, launches[0]));   // its request never connected
        Assert.Empty(InputsOf(db, launches[1]));   // the provider answered 401
        Assert.Contains(note.Id, provider.Requests[1], StringComparison.Ordinal);
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, launches[2]));
    }

    /// <summary>A transport that takes the request and cannot connect, as a host that is down or a network that is off.</summary>
    sealed class Unconnectable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused (on purpose)");
    }

    /// <summary>
    /// (h) INPUT LINEAGE IS APPEND-ONLY AND WRITTEN ONLY WITH THE COMMITTED OUTCOME: the loop hands the host what a packet showed only
    /// from inside the turn's committed transition; the rows refuse UPDATE and DELETE and a kind outside the closed list; and a
    /// second commit of the same attempt writes nothing twice. RED at base: no <c>ai_attempt_input</c> table existed.
    /// </summary>
    [Fact]
    public async Task Input_lineage_is_append_only_and_written_only_with_the_committed_outcome()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor, "a note", [], DateTimeOffset.UtcNow);
        host.Conversations[Executor].Session = TurnSession.FreshBecause(SessionWhy.First);
        SelfWake(host, Executor, "lineage-1");
        await TurnsUntil(loop, host, Executor, 1);

        var attempt = Assert.Single(LaunchesOf(db, Executor));
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));
        Assert.Empty(host.InputsOutsideCommit);

        new AiAttemptStore(db).RecordInputs(attempt, [new PacketInput(PacketInputKind.Lateral, note.Id)]);
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));

        void Exec(string sql) => db.Write(_ => { using var c = db.Cmd(sql); return c.ExecuteNonQuery(); });
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec("UPDATE ai_attempt_input SET ref_id='lat-000000000000'"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec("DELETE FROM ai_attempt_input"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            new AiAttemptStore(db).RecordInputs(attempt, [new PacketInput("approval", "owner")]));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            new AiAttemptStore(db).RecordInputs("turn-nobody-launched", [new PacketInput(PacketInputKind.Lateral, note.Id)]));
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));
    }

    /// <summary>
    /// A TURN SHOWN ONE BELIEF RECORDS IT AS AN INPUT (<c>U-packets-followups</c> item 2; the Codex second pass of
    /// <c>U-org-packets-b</c>, its first P1; ORCHESTRATION § 2.6, EVID-001): the executor holds one open ledger entry and no
    /// organisation record is owed it. Its packet shows the entry under "What you believe", and the attempt its committed outcome
    /// records names that entry — no more: the empty org block adds nothing.
    ///
    /// <para>RED at base bb4f177e: "Expected: ["ledger:1"] Actual: []". MUTANT, the inputs built from <c>org.Shown</c> alone again
    /// (the beliefs' lines dropped from <c>Lineage</c>): the same failure.</para>
    /// </summary>
    [Fact]
    public async Task A_turn_shown_one_belief_records_it_as_an_input()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        host.Owns = true;
        var loop = new MissionLoop(host, NoHeartbeat);
        var entry = new ResearchLedger(db).Add(Executor, null, LedgerKind.Hypothesis, "Funding below -0.05% mean-reverts within 8h.",
            LedgerMark.Hypothesis, 0.6m, null, null);
        host.Conversations[Executor].Session = TurnSession.Resumed;
        SelfWake(host, Executor, "belief-1");
        await TurnsUntil(loop, host, Executor, 1);

        Assert.Contains($"entry {entry.Id} (hypothesis", host.Conversations[Executor].Sent[0], StringComparison.Ordinal);
        var attempt = Assert.Single(LaunchesOf(db, Executor));
        Assert.Equal([$"ledger:{entry.Id}"], InputsOf(db, attempt));
    }

    /// <summary>
    /// A FAILED DELIVERY SHOWN IN THE MANDATE BLOCK IS RECORDED (<c>U-packets-followups</c> item 3; the Codex second pass of
    /// <c>U-org-packets-b</c>, its first P1): the executor's delivery fails TradeAgent's checks and code retries the mandate; the
    /// executor's next packet names that delivery and quotes its failed check, and the attempt its committed outcome records names
    /// the delivery.
    ///
    /// <para>RED at base bb4f177e: "Expected: ["delivery:…"] Actual: []" — the mandate block quoted the failed check and the
    /// lineage held nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_failed_delivery_shown_in_the_mandate_block_is_recorded()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var m = Give(db, "find out whether the funding skew predicts the next hour");
        var failed = new OrgStore(db).Deliver(m.Id, Executor, null, OverLong, [], [], DateTimeOffset.UtcNow);
        Assert.Equal(MandateEventKind.Retry, failed.Status);
        host.Conversations[Executor].Session = TurnSession.Resumed;
        await TurnsUntil(loop, host, Executor, 1);

        Assert.Contains($"- Delivery `{failed.Delivery.Id}` failed TradeAgent's checks", host.Conversations[Executor].Sent[0],
            StringComparison.Ordinal);
        var attempt = Assert.Single(LaunchesOf(db, Executor));
        Assert.Equal([$"delivery:{failed.Delivery.Id}"], InputsOf(db, attempt));
    }
}
