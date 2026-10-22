using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FIRST LIVES (U-puppet-runtime; <c>docs/ORCHESTRATION.md</c> § 7): scripted seats run through the real kernel — the loop,
/// admission, the activation seam, the session, containment, a real process, the real <c>trade</c> with the launch's real grant,
/// the pipe, the stores — on every platform the suite runs on, at no AI cost. <see cref="PuppetLife"/> is the fixture; its
/// summary says how later units add theirs.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PuppetLivesTests(ITestOutputHelper output) : IDisposable
{
    const string Chair = CouncilRoles.Operations;
    const string Research = CouncilRoles.Research;

    public void Dispose()
    {
        // The puppet is handed in-process; a runtimes.json another class left could only be obeyed or refused by the host.
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    /// <summary>
    /// (c) THE LEGACY PAIR, EACH A PUPPET, EACH TURN REACHING THE PIPE WITH ITS OWN GRANT AND METERED FROM ITS OWN STREAM. Each
    /// writes a research-ledger entry through <c>trade</c>: the gateway files it under the role and the attempt its launch grant
    /// proved, never a name the frame carried. Each attempt is ENDED, carries the tokens its puppet reported, is priced at the
    /// owner's rate, says its prompt travelled on stdin and names the seat class its manifest declared; each wake is consumed
    /// by its own attempt. RED before this unit: there was no puppet to start.
    /// </summary>
    [Fact]
    public async Task A_legacy_turn_by_a_puppet_reaches_the_pipe_with_its_own_grant_and_its_usage_is_metered()
    {
        await using var life = await PuppetLife.Begin("legacy");
        life.Script(Chair, ["trade ledger add finding \"The chair's book is flat.\" --mark claim", "usage 1200 200 300", "final I wrote down what I saw."]);
        life.Script(Research, ["trade ledger add hypothesis \"A research hunch.\" --mark hypothesis", "usage 900 0 100", "final Noted."]);

        var chairWake = life.Wake(Chair);
        var researchWake = life.Wake(Research);
        await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED)
                                  && life.AttemptsOf(Research).Any(a => a.State == AiAttemptState.ENDED),
            "both legacy puppets' turns ended");

        foreach (var (role, wake, input, cached, outputTokens, author) in new[]
                 {
                     (Chair, chairWake, 1200L, 200L, 300L, Chair),
                     (Research, researchWake, 900L, 0L, 100L, Research)
                 })
        {
            var attempt = Assert.Single(life.AttemptsOf(role));
            Assert.Equal(AiAttemptState.ENDED, attempt.State);
            Assert.Equal(0, attempt.ExitCode);

            // THE PIPE, WITH ITS OWN GRANT: the trade command was served, and the entry names this attempt and this role.
            var trade = Assert.Single(life.Log(role, 1), s => (string?)s["step"] == "trade");
            Assert.True((int?)trade["exit"] == 0, $"{role}'s trade call failed: {trade.ToJsonString()}");
            var entries = life.Host.Gateway.Ledger.List(author, null, null, null, 10);
            var entry = Assert.Single(entries).Entry;
            Assert.Equal(attempt.Id, entry.Attempt);
            Assert.Equal(role, entry.Author);

            // METERED FROM ITS OWN STREAM, by AgentSession's own parser, priced at the owner's rate.
            Assert.Equal(input, attempt.InputTokens);
            Assert.Equal(cached, attempt.CachedInputTokens);
            Assert.Equal(outputTokens, attempt.OutputTokens);
            Assert.NotNull(attempt.Cost);
            Assert.True(attempt.Cost > 0m, $"{role}'s turn was not priced: {attempt.UnpricedReason}");
            Assert.True(attempt.ReservedCost > 0m, $"{role}'s turn reserved nothing");
            using var context = JsonDocument.Parse(attempt.Context!);
            Assert.Equal(AgentArgs.OnStdin, context.RootElement.GetProperty("prompt_in").GetString());
            Assert.Equal(SeatPlan.PuppetClass, attempt.SeatClass);   // the row's own column, written at admission
            Assert.Equal(1, context.RootElement.GetProperty("command_items").GetInt32());

            // ITS WAKE, CONSUMED BY IT, and the prompt it read is the app's packet.
            Assert.Equal(attempt.Id, life.Host.Wakes!.Get(wake)!.ConsumedBy);
            Assert.False(string.IsNullOrWhiteSpace(life.Prompt(role, 1)));
        }

        Assert.Contains(life.Host.Conversation!.History, t => t.Role == ChatRole.Ai && t.Text == "I wrote down what I saw.");
        output.WriteLine($"life (c): {life.Counts()}");
    }

    /// <summary>
    /// A PUPPET MANIFEST DECLARING ANOTHER CLASS RECORDS THAT CLASS (seat B's O5 survey): what a later unit's conformance suite
    /// keys the verbs it may reach on. Declared by the test's manifest, in code — never by a file.
    /// </summary>
    [Fact]
    public async Task A_puppet_records_the_seat_class_its_test_manifest_declares()
    {
        await using var life = await PuppetLife.Begin("declared", seatClass: SeatPlan.CliClass);
        life.Script(Chair, ["usage 10 0 5", "final Done."]);
        life.Wake(Chair);
        await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED), "the chair's turn ended");

        var row = Assert.Single(life.AttemptsOf(Chair));
        Assert.Equal((SeatPlan.CliClass, "unconfined"), (row.SeatClass, row.Assurance));
        using var context = JsonDocument.Parse(row.Context!);
        Assert.False(context.RootElement.TryGetProperty("seat_class", out _), "the interim context.seat_class is still written");
    }

    /// <summary>
    /// (d) A RESTART MID-TURN: the chair takes the owner's words and hangs; the process dies (the database as it stood is what the
    /// next start opens). The next start declares the attempt LOST and keeps its reservation as its cost; the loop settles the
    /// words the lost turn took as failed and raises them again ONCE; the next turn receives them, once, and answers.
    /// </summary>
    [Fact]
    public async Task A_restart_mid_turn_loses_the_attempt_keeps_its_reservation_and_re_raises_the_owners_words_once()
    {
        const string words = "Close the book before the open.";
        await using var life = await PuppetLife.Begin("restart");
        life.Script(Chair, ["signal hung", "hang"], ["usage 40 0 20", "final Understood: I will close the book before the open."]);

        Assert.True(life.Host.RecordOwnerMessage(words));
        await life.RunUntil(() => life.Reached("hung"), "the chair's puppet took the owner's words and hung");

        var hung = Assert.Single(life.AttemptsOf(Chair));
        Assert.Equal(AiAttemptState.LAUNCHED, hung.State);
        Assert.True(hung.ReservedCost > 0m, "the hung turn reserved nothing");
        var original = Assert.Single(life.Host.Wakes!.OfKind(MissionEventKind.Owner));
        Assert.Equal(hung.Id, original.ConsumedBy);
        Assert.Contains(words, life.Prompt(Chair, 1));

        await life.RestartMidTurn();

        // LOST AT THE START, BEFORE ANY TURN: the meter's own reconciliation, its reservation standing as its cost.
        var lost = new AiAttemptStore(life.Host.Db).Get(hung.Id)!;
        Assert.Equal(AiAttemptState.LOST, lost.State);
        Assert.Equal(hung.ReservedCost, lost.Cost);
        // ITS DECLARED SEAT CLASS, WRITTEN AT ADMISSION, so a turn that never ended still says what it ran on (U-thinking-seats-a,
        // fix pass 2, item 7: the seat class is the row's column from Begin, never the close's context).
        Assert.True(lost.SeatClass == SeatPlan.PuppetClass, $"the LOST puppet attempt kept seat class '{lost.SeatClass}'");

        await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED), "the chair's next turn ended");

        var owner = life.Host.Wakes!.OfKind(MissionEventKind.Owner);
        Assert.True(owner.Count == 2, $"the owner's words were raised {owner.Count - 1} more time(s), not once: {life.State()}");
        var settled = owner.Single(e => e.Id == original.Id);
        Assert.Equal(MissionEventDisposition.Failed, settled.Disposition);
        var retry = owner.Single(e => e.Id != original.Id);
        Assert.Contains(words, retry.Payload);
        var answered = Assert.Single(life.AttemptsOf(Chair), a => a.State == AiAttemptState.ENDED);
        Assert.Equal(answered.Id, retry.ConsumedBy);

        var prompt = life.Prompt(Chair, 2)!;
        Assert.Equal(1, Occurrences(prompt, words));
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(life.Host.Db).Get(hung.Id)!.State);
        output.WriteLine($"life (d): {life.Counts()}");
    }

    /// <summary>
    /// (e) A POSITION BEYOND THE LEGACY PAIR, ACTIVE ON THE CHART AND WOKEN, IS STEPPED OVER BY THE ACTIVATION SEAM — no attempt,
    /// no process, its wake left due and unconsumed — while the legacy puppets keep turning around it. The puppets PLAY the harness
    /// class (<c>U-thinking-seats-a</c> fix pass 2, item 7): a real-model class, so with no proven executor cell recorded on this
    /// host the third is refused about the host, and a brokered seat is no unconfined neighbour, so the pair turn. On the puppet's
    /// own class the third would run — a puppet runs no model and is asked for no cell — which is not this life's act.
    /// </summary>
    [Fact]
    public async Task A_position_beyond_the_legacy_pair_is_stepped_over_and_the_legacy_puppets_keep_turning()
    {
        const string third = "pos-0b5e7c21";
        await using var life = await PuppetLife.Begin("third", seatClass: SeatClasses.Harness, seed: db => db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $unit, $home, $status, '2026-10-16T12:00:00.0000000Z')
                """, ("$id", third), ("$unit", OrgIds.Research), ("$home", $"org/{third}"), ("$status", OrgPositionStatus.Active));
            return c.ExecuteNonQuery();
        }));
        life.Script(third, ["trade ledger add finding \"Nobody activated me.\" --mark claim", "final I should not be here."]);
        life.Script(Chair, ["usage 10 0 5", "final The chair turned."]);
        life.Script(Research, ["usage 10 0 5", "final Research turned."]);

        var thirds = life.Wake(third);
        life.Wake(Chair);
        life.Wake(Research);
        await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED)
                                  && life.AttemptsOf(Research).Any(a => a.State == AiAttemptState.ENDED),
            "the legacy puppets turned beside a refused position");

        var refused = life.Host.ActivationRefusal(third);
        Assert.Equal(RefusalKind.HostOrLaunch, refused?.Kind);
        Assert.Contains("no proven executor cell recorded", refused!.Words, StringComparison.Ordinal);
        Assert.Empty(life.AttemptsOf(third));
        Assert.Equal(0, life.Turns(third));
        var wake = life.Host.Wakes!.Get(thirds)!;
        Assert.False(wake.Consumed, $"the refused position's wake was consumed: {wake.Disposition} {wake.DispositionDetail}");
        Assert.Empty(life.Host.Gateway.Ledger.List(third, null, null, null, 10));

        // AND THE PAIR GO ON: a second look each, the third still stepped over.
        life.Wake(Chair);
        await life.RunUntil(() => life.AttemptsOf(Chair).Count(a => a.State == AiAttemptState.ENDED) == 2, "the chair turned again");
        Assert.Empty(life.AttemptsOf(third));
        Assert.False(life.Host.Wakes!.Get(thirds)!.Consumed);
        output.WriteLine($"life (e): {life.Counts()}");
    }

    /// <summary>
    /// (d) EVERY ATTEMPT RECORDS FRESH OR RESUMED, AND WHY (<c>U-org-packets-a</c>), through the real kernel: a team member holding
    /// two mandates and the chair, each a puppet, under a bound of two turns a session. The member's first mandate opens its first
    /// session; its second is FRESH, for <c>mandate</c> — its conversation was stopped before the send. The chair, of the legacy pair,
    /// is fresh on its first turn, resumed on its second and fresh again on its third, for <c>turns</c>. Each attempt's
    /// <c>context</c> says so, as the session decided it on the argv it started, and carries its packet's bytes per block — the
    /// member's with its mandate block, summing to the bytes its puppet received. No model.
    ///
    /// <para>RED at base e697686d: no attempt's context carried a session.</para>
    /// </summary>
    [Fact]
    public async Task Every_attempt_records_fresh_or_resumed_and_why()
    {
        const string head = "pos-5e55aa000001", member = "pos-5e55aa000002", team = "team-5e55aa00";
        // EVERY RE-WAKE FLOOR ZERO (U-rewake-floor): this life reads the sessions of a member the sweep wakes again after each turn
        // that delivered nothing, on the wall clock, so it runs the sweep as it was before the floor — under the shipped floors its
        // third turn would wait a quarter of an hour. The floor itself is CouncilLoopTests' (h), on the loop's own clock.
        await using var life = await PuppetLife.Begin("sessions", settings: s =>
        {
            s.MissionTurnsPerSession = 2;
            s.RewakeFloorMinutes = OrgLevel.All.ToDictionary(l => l, _ => 0);
        }, seed: db => db.Write(_ =>
        {
            foreach (var sql in new[]
                     {
                         $"INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('{team}', '{OrgIds.Research}', 'team', NULL, 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{head}', '{team}', 'org/{head}', 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{member}', '{team}', 'org/{member}', 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"UPDATE org_unit SET head_position_id='{head}' WHERE id='{team}'"
                     })
            {
                using var c = db.Cmd(sql);
                c.ExecuteNonQuery();
            }
            return 0;
        }));
        life.Script(member, ["usage 100 0 10", "final First mandate."], ["usage 120 0 10", "final Second mandate."]);
        life.Script(Chair, ["usage 10 0 5", "final One."], ["usage 10 0 5", "final Two."], ["usage 10 0 5", "final Three."]);

        var org = new OrgStore(life.Host.Db);
        MandateRow Give(string objective) => org.Assign(new MandateIssue(head, null, member, MandateKind.Task, objective, null,
            MandateShape.Report, false, DateTimeOffset.UtcNow.AddDays(7), 5m, 2), DateTimeOffset.UtcNow);
        var m1 = Give("find out whether the funding skew predicts the next hour");
        var m2 = Give("count the gaps in last week's tape");

        for (var n = 1; n <= 3; n++)
        {
            life.Wake(Chair);
            var turned = n;
            await life.RunUntil(() => life.AttemptsOf(Chair).Count(a => a.State == AiAttemptState.ENDED) == turned, $"the chair's turn {n} ended");
        }
        // MORE THAN TWO, AND WHY: neither mandate is delivered, so the organisation's sweep owes each one ONE wake again once a launch
        // of it has ended (OrgStore.Sweep; ORCHESTRATION § 2.5, "an executor owns its open mandate") and the member goes on turning.
        // The first two of its turns are the ones read for sessions; every turn after them is asserted below to be that re-raise.
        await life.RunUntil(() => life.AttemptsOf(member).Count(a => a.State == AiAttemptState.ENDED) >= 3,
            "the member answered both mandates and the sweep woke it again");

        static (string? Session, string? Why) Of(AiAttempt a)
        {
            using var context = JsonDocument.Parse(a.Context!);
            var root = context.RootElement;
            return (root.TryGetProperty("session", out var s) ? s.GetString() : null,
                root.TryGetProperty("session_why", out var w) ? w.GetString() : null);
        }

        var all = life.AttemptsOf(member).OrderBy(a => a.StartedAt).ToList();
        var members = all.Where(a => a.State == AiAttemptState.ENDED).Take(2).ToList();
        Assert.Equal(members, all.Take(2));

        // EVERY MEMBER TURN AFTER THE TWO WAS WOKEN BY THE SWEEP'S RE-RAISE AND NOTHING ELSE: one `mandate` wake, keyed by its
        // mandate, that mandate's latest event and the launch of it before — never a timer, a ceiling floor or a self-wake.
        var wakes = life.Events();
        foreach (var later in all.Skip(2).Where(a => a.State == AiAttemptState.ENDED))
        {
            var took = Assert.Single(wakes, e => e.ConsumedBy == later.Id);
            var before = all.Last(a => a.MandateId == later.MandateId && a.StartedAt < later.StartedAt);
            Assert.Equal(MissionEventKind.Mandate, took.Kind);
            Assert.Equal(later.MandateId, took.Payload);
            Assert.Equal(MissionEventIds.Mandate(later.MandateId!, org.MandateEvents(later.MandateId!)[^1].Id, before.Id), took.Id);
            output.WriteLine($"life (sessions) member turn after the two: {later.Id} took {took.Kind} wake {took.Id}");
        }
        Assert.Equal([m1.Id, m2.Id], members.Select(a => a.MandateId));
        Assert.Equal([("fresh", SessionWhy.First), ("fresh", SessionWhy.Mandate)], members.Select(Of));
        var chairs = life.AttemptsOf(Chair).OrderBy(a => a.StartedAt).ToList();
        Assert.Equal([("fresh", SessionWhy.First), ("resumed", null), ("fresh", SessionWhy.Turns)], chairs.Select(Of));

        // AND EACH PACKET'S BYTES PER BLOCK: the member's carries its mandate block, and the blocks sum to the bytes it received.
        for (var turn = 1; turn <= 2; turn++)
        {
            using var context = JsonDocument.Parse(members[turn - 1].Context!);
            var packet = context.RootElement.GetProperty("packet").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
            Assert.True(packet.ContainsKey("mandate"), $"the member's packet has no mandate block: {context.RootElement}");
            Assert.Equal(Encoding.UTF8.GetByteCount(life.Prompt(member, turn)!), packet.Values.Sum());
            output.WriteLine($"life (sessions) packet bytes, member turn {turn}: {string.Join(", ", packet.Select(p => $"{p.Key}={p.Value}"))}");
        }

        foreach (var a in members) output.WriteLine($"life (sessions) {a.Role} {a.Id} mandate={a.MandateId}: {a.Context}");
        foreach (var a in chairs) output.WriteLine($"life (sessions) {a.Role} {a.Id}: {a.Context}");
    }

    /// <summary>
    /// A NOTE BETWEEN TWO EXECUTORS RIDES THE RECIPIENT'S NEXT PACKET ONCE (<c>U-org-packets-b</c>; ORCHESTRATION §§ 2.6, 2.10), on the
    /// real kernel with puppet seats: two members of a team and their head; member 2 turns once, member 1 sends it a note through the
    /// pipe in its turn, and member 2's second packet carries the note under its sender's stamp while its third does not. The two
    /// member-2 attempts' input rows and their packets' bytes per block are printed. No model.
    /// </summary>
    [Fact]
    public async Task A_note_between_two_executors_rides_the_recipients_next_packet_once()
    {
        const string head = "pos-5e55bb000001", one = "pos-5e55bb000002", two = "pos-5e55bb000003", team = "team-5e55bb00";
        await using var life = await PuppetLife.Begin("notes", seed: db => db.Write(_ =>
        {
            foreach (var sql in new[]
                     {
                         $"INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('{team}', '{OrgIds.Research}', 'team', NULL, 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{head}', '{team}', 'org/{head}', 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{one}', '{team}', 'org/{one}', 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{two}', '{team}', 'org/{two}', 'active', '2026-10-16T12:00:00.0000000Z')",
                         $"UPDATE org_unit SET head_position_id='{head}' WHERE id='{team}'"
                     })
            {
                using var c = db.Cmd(sql);
                c.ExecuteNonQuery();
            }
            return 0;
        }));
        life.Script(two, ["usage 100 0 10", "final One."], ["usage 100 0 10", "final Two."], ["usage 100 0 10", "final Three."]);
        life.Script(one, [$"trade lateral send {two} \"the funding skew looks venue-specific; check the second venue\"", "usage 100 0 10", "final Sent."]);

        async Task Turn(string role, int n)
        {
            life.Wake(role);
            await life.RunUntil(() => life.AttemptsOf(role).Count(a => a.State == AiAttemptState.ENDED) >= n, $"{role}'s turn {n} ended");
        }

        await Turn(two, 1);
        await Turn(one, 1);
        var send = Assert.Single(life.Log(one, 1), s => (string?)s["step"] == "trade");
        Assert.True((int?)send["exit"] == 0, $"member 1's note was refused: {send.ToJsonString()}");
        var note = Assert.Single(new OrgStore(life.Host.Db).LateralsOf(two));
        Assert.Equal(one, note.Sender);
        Assert.Equal(life.AttemptsOf(one).Single().Id, note.Attempt);

        await Turn(two, 2);
        await Turn(two, 3);

        Assert.DoesNotContain(note.Id, life.Prompt(two, 1)!, StringComparison.Ordinal);
        Assert.Contains($"Note `{note.Id}` from `{one}`", life.Prompt(two, 2)!, StringComparison.Ordinal);
        Assert.Contains("    > the funding skew looks venue-specific; check the second venue", life.Prompt(two, 2)!, StringComparison.Ordinal);
        Assert.DoesNotContain(note.Id, life.Prompt(two, 3)!, StringComparison.Ordinal);

        foreach (var line in life.Prompt(two, 2)!.Split('\n').SkipWhile(l => !l.StartsWith("- Notes sent to you", StringComparison.Ordinal)).Take(3))
            output.WriteLine($"life (notes) member 2 turn 2 org block: {line.TrimEnd()}");

        var attempts = life.AttemptsOf(two).Where(a => a.State == AiAttemptState.ENDED).OrderBy(a => a.StartedAt).ToList();
        var store = new AiAttemptStore(life.Host.Db);
        Assert.Equal([new PacketInput(PacketInputKind.Lateral, note.Id)], store.InputsOf(attempts[1].Id));
        Assert.Empty(store.InputsOf(attempts[2].Id));
        foreach (var (a, turn) in attempts.Skip(1).Take(2).Select((a, i) => (a, i + 2)))
        {
            using var context = JsonDocument.Parse(a.Context!);
            var packet = context.RootElement.GetProperty("packet").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetInt32()}");
            output.WriteLine($"life (notes) member 2 turn {turn} {a.Id}: inputs [{string.Join(", ", store.InputsOf(a.Id).Select(i => $"{i.Kind}:{i.RefId}"))}]; "
                             + $"packet bytes {string.Join(", ", packet)}");
        }
    }

    /// <summary>A team under Research's division: its head first, then its members, each active — as only a test may write the chart.</summary>
    static Action<Database> SeedTeam(string team, params string[] positions) => db => db.Write(_ =>
    {
        const string at = "2026-10-22T12:00:00.0000000Z";
        var sql = new List<string> { $"INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('{team}', '{OrgIds.Research}', 'team', NULL, 'active', '{at}')" };
        sql.AddRange(positions.Select(p => $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{p}', '{team}', 'org/{p}', 'active', '{at}')"));
        sql.Add($"UPDATE org_unit SET head_position_id='{positions[0]}' WHERE id='{team}'");
        foreach (var line in sql)
        {
            using var c = db.Cmd(line);
            c.ExecuteNonQuery();
        }
        return 0;
    });

    /// <summary>The <c>trade</c> steps of one turn's log, in order.</summary>
    static List<System.Text.Json.Nodes.JsonObject> TradeSteps(PuppetLife life, string role, int turn) =>
        [.. life.Log(role, turn).Where(s => (string?)s["step"] == "trade")];

    static string Said(System.Text.Json.Nodes.JsonObject step) => $"{(string?)step["stdout"]}{(string?)step["stderr"]}";

    /// <summary>
    /// (f) O6 PART A ON THE REAL KERNEL, WITH PUPPET SEATS (<c>U-org-verbs-a</c>): a mandate whose holder failed twice — code retried
    /// once and escalated — wakes its head, whose puppet RE-SCOPES it through <c>trade mandate rescope</c>: the old reads
    /// <c>rescoped</c> naming the new, the new is issued naming the old, and the member is woken ONCE for the new. The member's
    /// puppet, in the turn that wake buys, PROPOSES a finding citing two app records through <c>trade mandate propose</c>, and the
    /// head is woken ONCE for it; each reads its own place with <c>trade org show</c> — the head's showing the successor it decides,
    /// the member's the one it holds, each chain to the owner with the meter's envelopes. The seats are the puppet's own class,
    /// MEDIATED (<c>SeatAssurance.cs</c>), the manifest <see cref="Puppet.Manifest"/> builds. No model.
    /// </summary>
    [Fact]
    public async Task A_puppet_head_rescopes_a_failed_mandate_and_its_executor_proposes_a_finding()
    {
        const string head = "pos-5e55cc000001", member = "pos-5e55cc000002", team = "team-5e55cc00";
        await using var life = await PuppetLife.Begin("org-verbs", seed: SeedTeam(team, head, member));
        var org = new OrgStore(life.Host.Db);
        var now = DateTimeOffset.UtcNow;
        var old = org.Assign(new MandateIssue(head, null, member, MandateKind.Task, "find out whether the funding skew predicts the next hour",
            null, MandateShape.Report, false, now.AddDays(7), 5m, 2), now);
        var tooLong = string.Join("\n", Enumerable.Range(1, MandateLimits.ReportLines + 1).Select(i => $"line {i}"));
        for (var i = 0; i < 2; i++) org.Deliver(old.Id, member, null, tooLong, [], [], DateTimeOffset.UtcNow);
        Assert.Equal(MandateEventKind.Escalated, org.MandateStatus(old.Id));
        var failed = org.Deliveries(old.Id)[^1].Id;

        life.Script(head,
            [$"trade mandate rescope {old.Id} \"two clean failures: the question was too wide\" \"summarise in ten lines whether the skew leads the hour\" "
             + $"--kind task --shape report --due {now.AddDays(5):O} --allocation 5",
             "signal rescoped", "await member-scripted", "trade org show", "usage 100 0 10", "final Re-scoped."],
            ["trade org show", "usage 50 0 5", "final Read the proposal."]);
        await life.RunUntil(() => life.Reached("rescoped"), "the head's puppet re-scoped the mandate");

        // ONE LANE: the member cannot turn while the head's turn waits, so its script is written before its wake is taken.
        var successor = org.SuccessorOf(old.Id);
        Assert.True(successor is not null, $"the re-scope wrote no successor: {Said(TradeSteps(life, head, 1)[0])}");
        var refs = JsonSerializer.Serialize(new[] { new { kind = "mandate", id = old.Id }, new { kind = "delivery", id = failed } })
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        life.Script(member, [$"trade mandate propose {successor} \"venue B leads venue A by a minute on the skew; it deserves its own mandate\" --refs {refs}",
            "trade org show", "usage 100 0 10", "final Proposed."]);
        life.Release("member-scripted");
        await life.RunUntil(() => life.AttemptsOf(head).Count(a => a.State == AiAttemptState.ENDED) >= 2
                                  && life.AttemptsOf(member).Any(a => a.State == AiAttemptState.ENDED),
            "the member proposed and the head was woken for it");

        // THE RE-SCOPE: the old closed naming the new, the new issued naming the old, the member woken once for it.
        var rescope = TradeSteps(life, head, 1);
        Assert.True((int?)rescope[0]["exit"] == 0, $"the re-scope was refused: {Said(rescope[0])}");
        Assert.Equal(MandateEventKind.Rescoped, org.MandateStatus(old.Id));
        Assert.Equal([successor!], org.MandateEvents(old.Id)[^1].Evidence);
        var issued = org.MandateEvents(successor!)[0];
        Assert.Equal((MandateEventKind.Issued, head), (issued.Kind, issued.Actor));
        Assert.Equal([old.Id], issued.Evidence);
        var events = life.Events();
        var took = Assert.Single(events, e => e.Kind == MissionEventKind.Mandate && e.Payload == successor && e.ConsumedBy is not null);
        Assert.Equal(MissionEventIds.Mandate(successor!, issued.Id), took.Id);
        var memberTurn = life.AttemptsOf(member).First(a => a.State == AiAttemptState.ENDED);
        Assert.Equal((memberTurn.Id, successor), (took.ConsumedBy, memberTurn.MandateId));

        // THE PROPOSAL: the member's claim, its two refs, ONE wake its head's second turn took — and no authority.
        var propose = TradeSteps(life, member, 1);
        Assert.True((int?)propose[0]["exit"] == 0, $"the proposal was refused: {Said(propose[0])}");
        var proposed = Assert.Single(org.MandateEvents(successor!), e => e.Kind == MandateEventKind.Proposed);
        Assert.Equal(member, proposed.Actor);
        Assert.Equal([$"mandate:{old.Id}", $"delivery:{failed}"], proposed.Evidence);
        var proposal = Assert.Single(events, e => e.Kind == MissionEventKind.Proposal);
        Assert.Equal((head, successor), (proposal.Role, proposal.Payload));
        Assert.Equal(life.AttemptsOf(head).Where(a => a.State == AiAttemptState.ENDED).OrderBy(a => a.StartedAt).ElementAt(1).Id, proposal.ConsumedBy);
        Assert.Equal(MandateEventKind.Issued, org.MandateStatus(successor!));
        Assert.Equal(5m, org.Mandate(successor!)!.Allocation);

        // EACH READS ITS OWN PLACE: the head decides the successor, the member holds it, each chain ends at the owner.
        foreach (var (role, turn, step, mine) in new[] { (head, 1, 1, "decided"), (member, 1, 1, "held"), (head, 2, 0, "decided") })
        {
            var show = TradeSteps(life, role, turn)[step];
            Assert.True((int?)show["exit"] == 0, $"{role}'s org show was refused: {Said(show)}");
            using var view = JsonDocument.Parse((string)show["stdout"]!);
            var data = view.RootElement.TryGetProperty("data", out var d) ? d : view.RootElement;
            Assert.Equal(role, data.GetProperty("position").GetString());
            Assert.Equal([team, OrgIds.Research, OrgIds.Root], data.GetProperty("chain").EnumerateArray().Select(u => u.GetProperty("id").GetString()));
            Assert.Equal("the owner", data.GetProperty("chain")[2].GetProperty("head").GetString());
            Assert.Contains(successor, data.GetProperty(mine).EnumerateArray().Select(m => m.GetProperty("mandate").GetString()));
            Assert.DoesNotContain(old.Id, view.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.True(data.GetProperty("chain")[2].TryGetProperty("ai_envelopes", out var rootDays) && rootDays.ValueKind == JsonValueKind.Array
                        && rootDays.EnumerateArray().Any() && rootDays.EnumerateArray().All(d => d.TryGetProperty("ceiling", out _)),
                $"the root's envelope was not shown, each reading naming its ceiling: {data.GetRawText()}");
            output.WriteLine($"life (org-verbs) {role} turn {turn} org show: {data.GetProperty("chain").GetRawText()}");
        }
        output.WriteLine($"life (org-verbs): {life.Counts()}");
    }

    /// <summary>
    /// (f2) THE LEGACY PAIR ON UNCONFINED SEATS ARE REFUSED ALL THREE, ON THE REAL KERNEL (<c>U-org-verbs-a</c>): their puppets play the
    /// CLI class — unconfined, the class the legacy pair's real seats record — and each asks <c>trade mandate rescope</c>,
    /// <c>trade mandate propose</c> and <c>trade org show</c>; every one is refused in the gate's words (research heads the division
    /// above every team, and headship grants no verb), and nothing is written. No model.
    /// </summary>
    [Fact]
    public async Task The_legacy_pair_on_unconfined_puppet_seats_are_refused_rescope_propose_and_org_show()
    {
        await using var life = await PuppetLife.Begin("org-verbs-legacy", seatClass: SeatPlan.CliClass);
        string[] steps =
        [
            "trade mandate rescope man-000000000000 \"w\" \"g\" --kind exploration --due 2099-01-01T00:00:00Z --allocation 1",
            "trade mandate propose man-000000000000 \"c\" --refs []",
            "trade org show",
            "usage 10 0 5",
            "final Refused."
        ];
        life.Script(Research, steps);
        life.Script(Chair, steps);
        life.Wake(Research);
        life.Wake(Chair);
        await life.RunUntil(() => life.AttemptsOf(Research).Any(a => a.State == AiAttemptState.ENDED)
                                  && life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED), "the legacy pair turned");

        foreach (var role in new[] { Research, Chair })
        {
            var asked = TradeSteps(life, role, 1);
            Assert.Equal(3, asked.Count);
            foreach (var step in asked)
            {
                Assert.True((int?)step["exit"] != 0, $"{role}'s {step["args"]?.ToJsonString()} was served on an unconfined seat");
                Assert.Contains("Heading a unit grants no verb by itself", Said(step), StringComparison.Ordinal);
            }
            output.WriteLine($"life (org-verbs-legacy) {role}: {Said(asked[2]).Trim()}");
        }
        Assert.DoesNotContain(life.Events(), e => e.Kind is MissionEventKind.Proposal or MissionEventKind.Mandate);
        Assert.Empty(new OrgStore(life.Host.Db).MandatesOf(Research));
    }

    /// <summary>
    /// THE MATERIAL PASS WAITS FOR A PUPPET TURN'S QUIESCENCE (COUNCIL rule 7): while the chair's puppet is mid-turn its process is
    /// in the host's presence register and the app's own pass refuses to walk the inbox — nothing walked, the window unmoved; once
    /// the turn is over the same call walks it.
    /// </summary>
    [Fact]
    public async Task The_material_pass_waits_for_a_puppet_turns_quiescence()
    {
        await using var life = await PuppetLife.Begin("quiescence");
        life.Script(Chair, ["signal turning", "await go", "usage 10 0 5", "final Done."]);
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning"), "the chair's puppet is mid-turn");

        Assert.True(life.Presence.Live > 0, "a running puppet turn is not in the host's presence register");
        var windowBefore = life.Host.Db.GetKv("material_scan_at");
        Assert.Null(life.Host.ScanMaterials());
        Assert.Equal(windowBefore, life.Host.Db.GetKv("material_scan_at"));

        life.Release("go");
        await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED) && life.Presence.Live == 0,
            "the chair's turn ended and its process left the register");
        await life.Host.Mission.PauseAsync();
        Assert.NotNull(life.Host.ScanMaterials());
    }

    /// <summary>
    /// THE OWNER'S WORDS REACH A BUSY CHAIR'S NEXT TURN: typed on the Chat page while the chair's puppet is mid-turn, they are
    /// held as a wake — not handed to the turn in flight — and the next turn's prompt carries them, once; that turn consumes them.
    /// </summary>
    [Fact]
    public async Task The_owners_words_reach_a_busy_chairs_next_turn()
    {
        const string words = "Sell nothing today.";
        await using var life = await PuppetLife.Begin("busy");
        life.Script(Chair, ["signal turning", "await go", "usage 10 0 5", "final First."], ["usage 10 0 5", "final Nothing sold today."]);
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning"), "the chair's puppet is mid-turn");

        var chat = life.Host.Conversation!;
        Assert.True(chat.Busy);
        await chat.SendAsync(words);
        var held = Assert.Single(life.Host.Wakes!.OfKind(MissionEventKind.Owner));
        Assert.False(held.Consumed);
        Assert.DoesNotContain(words, life.Prompt(Chair, 1));

        life.Release("go");
        await life.RunUntil(() => life.AttemptsOf(Chair).Count(a => a.State == AiAttemptState.ENDED) == 2, "the chair's next turn ended");

        Assert.Equal(1, Occurrences(life.Prompt(Chair, 2)!, words));
        var second = life.AttemptsOf(Chair)[1];
        Assert.Equal(second.Id, life.Host.Wakes!.Get(held.Id)!.ConsumedBy);
        Assert.Contains(chat.History, t => t.Role == ChatRole.You && t.Text == words);
        Assert.Contains(chat.History, t => t.Role == ChatRole.Ai && t.Text == "Nothing sold today.");
    }

    /// <summary>
    /// A PUPPET LIFE LEAVES NOTHING PROCESS-WIDE BEHIND (U-puppet-isolation). A life deploys TradeAgent's own <c>trade</c> over the
    /// test home's launcher, records its hash, copies the command's side-car files beside it and, where the process had none,
    /// sets <c>DOTNET_ROOT</c>; the assembly's other classes share all four. It used to delete the launcher at its end, so
    /// the next class to launch a held process — <c>AiAttemptLedgerTests</c>' restart test — was refused before it started
    /// ("the trade command is not at ...") and read as not busy. Disposed, the life puts back what it found: the launcher byte
    /// for byte and mode for mode, the hash record, the environment variable and the folder's file list.
    /// </summary>
    [Fact]
    public async Task A_puppet_life_puts_back_every_process_wide_thing_it_took()
    {
        TestEnv.EnsureLauncher();
        var launcher = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        // Windows holds a launch in a job and has no launcher: there the life must leave none, as it found none.
        var bytes = File.Exists(launcher) ? await File.ReadAllBytesAsync(launcher) : null;
        var mode = bytes is null || OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(launcher);
        var hashed = File.Exists(ToolDeployer.DeployedHashFile) ? await File.ReadAllTextAsync(ToolDeployer.DeployedHashFile) : null;
        var files = Directory.GetFiles(Paths.Bin).Select(Path.GetFileName).Order().ToArray();
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", null);   // the case that makes the life set it
            await using (var life = await PuppetLife.Begin("isolation"))
            {
                life.Script(Chair, ["usage 10 0 5", "final Done."]);
                life.Wake(Chair);
                await life.RunUntil(() => life.AttemptsOf(Chair).Any(a => a.State == AiAttemptState.ENDED), "the chair's turn ended");
                Assert.NotNull(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            }

            Assert.Null(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            Assert.Equal(bytes is not null, File.Exists(launcher));
            if (bytes is not null)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(launcher));
                if (!OperatingSystem.IsWindows()) Assert.Equal(mode, File.GetUnixFileMode(launcher));
            }
            Assert.Equal(hashed, File.Exists(ToolDeployer.DeployedHashFile) ? await File.ReadAllTextAsync(ToolDeployer.DeployedHashFile) : null);
            Assert.Equal(files, Directory.GetFiles(Paths.Bin).Select(Path.GetFileName).Order().ToArray());
            Assert.Null(ProcessContainment.CannotHold());
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_ROOT", root); }
    }

    /// <summary>
    /// A LAUNCH BESIDE A DEPLOYMENT NEVER FINDS NO LAUNCHER (U-launcher-race, review R1). <c>DeployTrade</c> used to delete the
    /// launcher and then copy the real command over the gap; a turn of another class that began in that interval was refused
    /// ("the trade command is not at ...", CONTAINMENT_UNAVAILABLE). Deployed over and over while another thread looks for the
    /// file as a launch does, the launcher is there every time and is always whole: the real command, with its mode.
    /// </summary>
    [Fact]
    public async Task A_deployment_never_leaves_the_launcher_absent_or_half_written()
    {
        if (OperatingSystem.IsWindows()) return;   // there is no launcher: a launch is held in a job
        TestEnv.EnsureLauncher();
        var launcher = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        var found = ProcessWide.Note();
        var real = await File.ReadAllBytesAsync(Path.Combine(Puppet.TradeCliDir, ToolDeployer.TradeCliName));
        var stop = false;
        var gaps = 0;
        var wrong = 0;
        var looker = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    var now = File.ReadAllBytes(launcher);
                    if (!now.AsSpan().StartsWith("#!"u8) && now.Length != real.Length) Interlocked.Increment(ref wrong);
                }
                catch (FileNotFoundException) { Interlocked.Increment(ref gaps); }
                catch (DirectoryNotFoundException) { Interlocked.Increment(ref gaps); }
            }
        });
        try
        {
            for (var i = 0; i < 200; i++) PuppetLife.DeployTrade();
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await looker;
            found.PutBack();
        }

        Assert.Equal(0, gaps);
        Assert.Equal(0, wrong);
    }

    static int Occurrences(string text, string of)
    {
        var n = 0;
        for (var at = text.IndexOf(of, StringComparison.Ordinal); at >= 0; at = text.IndexOf(of, at + of.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
