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
    /// <c>context</c> says so, as the session decided it on the argv it started. No model.
    ///
    /// <para>RED at base e697686d: no attempt's context carried a session.</para>
    /// </summary>
    [Fact]
    public async Task Every_attempt_records_fresh_or_resumed_and_why()
    {
        const string head = "pos-5e55aa000001", member = "pos-5e55aa000002", team = "team-5e55aa00";
        await using var life = await PuppetLife.Begin("sessions", settings: s => s.MissionTurnsPerSession = 2, seed: db => db.Write(_ =>
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
        await life.RunUntil(() => life.AttemptsOf(member).Count(a => a.State == AiAttemptState.ENDED) == 2, "the member answered both mandates");

        static (string? Session, string? Why) Of(AiAttempt a)
        {
            using var context = JsonDocument.Parse(a.Context!);
            var root = context.RootElement;
            return (root.TryGetProperty("session", out var s) ? s.GetString() : null,
                root.TryGetProperty("session_why", out var w) ? w.GetString() : null);
        }

        var members = life.AttemptsOf(member).OrderBy(a => a.StartedAt).ToList();
        Assert.Equal([m1.Id, m2.Id], members.Select(a => a.MandateId));
        Assert.Equal([("fresh", SessionWhy.First), ("fresh", SessionWhy.Mandate)], members.Select(Of));
        var chairs = life.AttemptsOf(Chair).OrderBy(a => a.StartedAt).ToList();
        Assert.Equal([("fresh", SessionWhy.First), ("resumed", null), ("fresh", SessionWhy.Turns)], chairs.Select(Of));

        foreach (var a in members) output.WriteLine($"life (sessions) {a.Role} {a.Id} mandate={a.MandateId}: {a.Context}");
        foreach (var a in chairs) output.WriteLine($"life (sessions) {a.Role} {a.Id}: {a.Context}");
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

    static int Occurrences(string text, string of)
    {
        var n = 0;
        for (var at = text.IndexOf(of, StringComparison.Ordinal); at >= 0; at = text.IndexOf(of, at + of.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
