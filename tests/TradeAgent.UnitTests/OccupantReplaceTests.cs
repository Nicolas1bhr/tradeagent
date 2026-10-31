using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;
using static TradeAgent.Tests.Unit.OrgReviewTests;
using static TradeAgent.Tests.Unit.OrgSurfaceSeed;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A FRESH OCCUPANT TAKES OVER A SEAT FROM THE POSITION'S MEMORY (<c>U-worker-replace</c>; VISION § 1 test 1; ORGANISATION §§ 6, 8): an app
/// rule (a stall, repeated failed checks, calibration below the base rate, an audit finding) or the owner's press retires the OCCUPANT; the
/// position stays; the replacement writes its event, a fence and one wake and nothing else — trials, spend, reservations, mandates and
/// unresolved launches carry over — and the new occupant's packet opens with the takeover block. The chart is <see cref="OrgReviewTests"/>'s:
/// <see cref="Head"/> heads the division, <see cref="TeamHead"/> the team, <see cref="Worker"/> works in it. No model.
/// </summary>
public class OccupantReplaceTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    static MandateRow Give(Database db, DateTimeOffset at) => new OrgStore(db).Assign(new MandateIssue(TeamHead, null, Worker, MandateKind.Task,
        "find out whether the funding skew predicts the next hour", null, MandateShape.Report, false, at.AddDays(7), 5m, 2), at);

    /// <summary>One launch of <see cref="Worker"/> under <paramref name="mandate"/>: ended at <paramref name="cost"/>, or still LAUNCHED holding its reservation.</summary>
    static void Launch(Database db, string id, string? mandate, DateTimeOffset at, decimal? cost, decimal reserved = 0.3m)
    {
        var store = new AiAttemptStore(db);
        Assert.True(store.Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = Worker, ReservedCost = reserved, SeatClass = SeatClasses.Harness, MandateId = mandate,
            BillingClass = BillingClasses.Key, Funding = [OrgReviewTests.Team, Div, OrgIds.Root]
        }).Admitted);
        if (cost is { } c) Assert.True(store.End(id, 0, at.AddMinutes(1), 1000, 0, null, 100, null, "model-x", c, null, null));
    }

    /// <summary>A delivery of <see cref="Worker"/>'s on <paramref name="mandate"/>, written by no launch, with one check of TradeAgent's that failed (or passed).</summary>
    static string Delivered(Database db, string mandate, int n, DateTimeOffset at, bool failed = true)
    {
        var id = $"dlv-occupant-{n:000}";
        Exec(db, "INSERT INTO org_delivery(id, mandate_id, attempt_id, report, deviations, refs, manifest, at) VALUES($id,$m,NULL,'the answer','[]','[]','{}',$at)",
            ("$id", id), ("$m", mandate), ("$at", Sql.T(at)));
        Exec(db, "INSERT INTO org_check(delivery_id, check_name, passed, reason) VALUES($id,'shape',$p,'TradeAgent''s reason')",
            ("$id", id), ("$p", failed ? 0 : 1));
        return id;
    }

    /// <summary>Every row of every table but <paramref name="except"/>, as text, table by table — what "nothing else was written" is checked against.</summary>
    static Dictionary<string, List<string>> Rows(Database db, params string[] except) => db.Read(_ =>
    {
        var tables = new List<string>();
        using (var c = db.Cmd("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
        using (var r = c.ExecuteReader())
            while (r.Read()) tables.Add(r.GetString(0));
        var all = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in tables.Where(t => !except.Contains(t)))
        {
            using var c = db.Cmd($"SELECT * FROM \"{t}\"");
            using var r = c.ExecuteReader();
            var rows = new List<string>();
            while (r.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
            rows.Sort(StringComparer.Ordinal);
            all[t] = rows;
        }
        return all;
    });

    static List<(long Id, string Kind, string Decider, string Detail)> Events(Database db, long after) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, kind, decider, detail FROM org_event WHERE id > $a ORDER BY id", ("$a", after));
        using var r = c.ExecuteReader();
        var rows = new List<(long, string, string, string)>();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return rows;
    });

    static long LastEvent(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COALESCE(MAX(id), 0) FROM org_event");
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// GUARD (BUDGET-001; ORGANISATION § 8 "trials, spend and open operations carry over"): THE OWNER'S PRESS REPLACES THE OCCUPANT AND RESETS
    /// NOTHING. The worker holds an open mandate, one launch of it ended at a cost and one is still LAUNCHED on its reservation; press one shows
    /// occupant 1 retiring, press two writes exactly an <c>occupant-replaced</c> event decided by the owner and a <c>fenced</c> event — and one
    /// <c>occupant</c> wake to the worker — while EVERY other row of EVERY other table is byte for byte what it was: the mandate still open and
    /// owed by the worker, the attempts, their funding and reservations, every trial and verdict count. The predecessor's live launch is fenced.
    /// </summary>
    [Fact]
    public void The_owners_press_replaces_the_occupant_and_resets_nothing()
    {
        using var db = Chart();
        var m = Give(db, Now.AddHours(-3));
        Launch(db, "turn-occ-0001", m.Id, Now.AddHours(-2), cost: 0.5m);
        Launch(db, "turn-occ-0002", m.Id, Now.AddHours(-1), cost: null);
        var org = new OrgStore(db);
        var before = Rows(db, "org_event", "mission_event");
        var lastEvent = LastEvent(db);
        var spend = new AiAttemptStore(db).TotalsBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Worker);

        var shown = org.PreviewReplaceOccupant(Worker);
        Assert.Null(shown.Refusal);
        Assert.Equal((Worker, (int?)1), (shown.Position, shown.Retires));
        var done = org.ReplaceOccupant(shown, Now);

        var after = Rows(db, "org_event", "mission_event");
        Assert.Equal(before.Keys, after.Keys);
        foreach (var t in before.Keys) Assert.True(before[t].SequenceEqual(after[t]), $"replacing an occupant changed the table {t}");
        var written = Events(db, lastEvent);
        Assert.Equal([OrgEventKind.OccupantReplaced, OrgEventKind.Fenced], written.Select(e => e.Kind));
        Assert.All(written, e => Assert.Equal(OrgDecider.Owner, e.Decider));
        Assert.Equal($"position={Worker} occupant=2 retired=1 rule=owner press=owner (launches recorded through #2)", written[0].Detail);
        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Occupant));
        Assert.Equal((MissionEventIds.Occupant(Worker, done.EventId), Worker, done.EventId.ToString(CultureInfo.InvariantCulture)),
            (wake.Id, wake.Role, wake.Payload));

        Assert.Equal([m.Id], org.MandatesOwedBy(Worker));
        Assert.Equal(spend, new AiAttemptStore(db).TotalsBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Worker));
        Assert.NotNull(org.FencedSince("turn-occ-0002"));
        var sits = org.OccupancyOf(Worker);
        Assert.Equal((2, 2L, OrgDecider.Owner, OccupantRule.Owner), (sits.Ordinal, sits.Through, sits.Decider, sits.Rule));
        Assert.Equal(1, org.UnresolvedBefore(sits));
        log.WriteLine($"replaced: {written[0].Detail}; wake {wake.Id}; {before.Count} tables unchanged");
    }

    /// <summary>
    /// THE OWNER'S PRESS IS REFUSED IN WORDS, NOTHING WRITTEN: a council seat (its conversation is "New conversation"'s), a position the chart
    /// does not hold, a dormant one; a second press while the new occupant's wake waits untaken; and press two handed a preview of an occupant
    /// that was replaced since.
    /// </summary>
    [Fact]
    public void The_owners_press_is_refused_in_words_and_writes_nothing()
    {
        using var db = Chart();
        Seat(db, "pos-4e71e0000009", OrgReviewTests.Team, OrgPositionStatus.Dormant);
        var org = new OrgStore(db);
        Assert.Contains("New conversation", org.PreviewReplaceOccupant(CouncilRoles.Research).Refusal, StringComparison.Ordinal);
        Assert.Contains("holds no position", org.PreviewReplaceOccupant("pos-000000000000").Refusal, StringComparison.Ordinal);
        Assert.Contains("is dormant", org.PreviewReplaceOccupant("pos-4e71e0000009").Refusal, StringComparison.Ordinal);
        var refused = Assert.Throws<TradeAgentException>(() => org.ReplaceOccupant(org.PreviewReplaceOccupant(CouncilRoles.Research), Now));
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);

        var first = org.PreviewReplaceOccupant(Worker);
        var stale = org.PreviewReplaceOccupant(Worker);
        org.ReplaceOccupant(first, Now);
        var events = LastEvent(db);
        Assert.Contains("has not taken its first turn yet", Assert.Throws<TradeAgentException>(() => org.ReplaceOccupant(stale, Now)).Message,
            StringComparison.Ordinal);
        Exec(db, "UPDATE mission_event SET consumed_at = $at WHERE kind = $k", ("$at", Sql.T(Now)), ("$k", MissionEventKind.Occupant));
        Assert.Contains("it was replaced since you looked", Assert.Throws<TradeAgentException>(() => org.ReplaceOccupant(stale, Now)).Message,
            StringComparison.Ordinal);
        Assert.Equal(events, LastEvent(db));
        Assert.Equal(2, org.OccupancyOf(Worker).Ordinal);
    }

    /// <summary>
    /// GUARD (ORG-001; "restructuring resets nothing", and nothing retires a successor for its predecessor's record): REPEATED FAILED CHECKS
    /// RETIRE THE OCCUPANT ONCE, AND THE SUCCESSOR ONLY ON ITS OWN RECORD. Three deliveries of the worker's each failed a check ⇒ ONE
    /// replacement decided by the app on <c>failed-checks</c>, its measurement the app's; a second sweep — the same three deliveries, now the
    /// predecessor's — replaces nothing; three more failed deliveries after the replacement retire occupant 2. Two failed and one passed: nothing.
    /// </summary>
    [Fact]
    public void Repeated_failed_checks_retire_the_occupant_once_and_the_successor_only_on_its_own_record()
    {
        using var db = Chart();
        var m = Give(db, Now.AddHours(-5));
        var org = new OrgStore(db);
        Delivered(db, m.Id, 1, Now.AddHours(-3));
        Delivered(db, m.Id, 2, Now.AddHours(-2), failed: false);
        Delivered(db, m.Id, 3, Now.AddHours(-1));
        Assert.Empty(org.OccupantSweep(Now, OccupantThresholds.Default, () => []).Replaced);
        var last = Delivered(db, m.Id, 4, Now.AddMinutes(-30));
        Delivered(db, m.Id, 5, Now.AddMinutes(-40));     // older than #4, so #4, #3... are the last three: #4, #5, #3

        var swept = org.OccupantSweep(Now, OccupantThresholds.Default, () => []);
        var r = Assert.Single(swept.Replaced);
        Assert.Equal((Worker, 1, 2, OrgDecider.App, OccupantRule.FailedChecks), (r.Position, r.Retired, r.Seated, r.Decider, r.Rule));
        Assert.Equal($"deliveries=3 each_failed_a_check last={last}", r.Measurement);

        // THE SAME RECORD, A SECOND PASS (a restart included): the predecessor's, so nothing.
        Assert.Empty(new OrgStore(db).OccupantSweep(Now.AddMinutes(5), OccupantThresholds.Default, () => []).Replaced);
        Assert.Equal(2, org.OccupancyOf(Worker).Ordinal);

        // THE SUCCESSOR'S OWN THREE: retired on them.
        for (var n = 6; n <= 8; n++) Delivered(db, m.Id, n, Now.AddMinutes(n));
        Exec(db, "UPDATE mission_event SET consumed_at = $at WHERE kind = $k", ("$at", Sql.T(Now)), ("$k", MissionEventKind.Occupant));
        Assert.Equal(3, Assert.Single(org.OccupantSweep(Now.AddMinutes(20), OccupantThresholds.Default, () => []).Replaced).Seated);
    }

    /// <summary>
    /// A STALL RETIRES THE OCCUPANT: six resolved launches on the mandate it holds that moved no mandate ⇒ <c>stall</c>; five ⇒ nothing; and a
    /// position that holds no open mandate is never stalled, however many launches.
    /// </summary>
    [Fact]
    public void Six_launches_that_moved_no_mandate_it_holds_are_a_stall()
    {
        using var db = Chart();
        var m = Give(db, Now.AddHours(-10));
        var org = new OrgStore(db);
        for (var n = 1; n <= 5; n++) Launch(db, $"turn-stall-{n:000}", m.Id, Now.AddHours(-9 + n), cost: 0.1m);
        Assert.Empty(org.OccupantSweep(Now, OccupantThresholds.Default, () => []).Replaced);
        Launch(db, "turn-stall-006", m.Id, Now.AddHours(-1), cost: 0.1m);
        var r = Assert.Single(org.OccupantSweep(Now, OccupantThresholds.Default, () => []).Replaced);
        Assert.Equal((OccupantRule.Stall, "launches=6 moved_no_mandate open_held=1 last=turn-stall-006"), (r.Rule, r.Measurement));
        Assert.Empty(org.OccupantSweep(Now.AddMinutes(1), OccupantThresholds.Default, () => []).Replaced);
    }

    /// <summary>
    /// CALIBRATION BELOW THE BASE RATE RETIRES THE OCCUPANT: ten of its forecasts resolved against it, skill below zero ⇒ <c>calibration</c>.
    /// </summary>
    [Fact]
    public void Calibration_below_the_base_rate_over_ten_scored_forecasts_retires_the_occupant()
    {
        using var db = Chart();
        var t0 = Now.AddDays(-3);
        var org = new OrgStore(db);
        for (var i = 0; i < 10; i++)
        {
            var version = $"ver-0cc{i:00}";
            OrgScorecardTests.Version(db, version);
            org.Decide(Worker, null, OrgDecisionKind.Routine, ["forecast it"], "forecast it", "my reasons", [],
                [new ForecastClaim(ForecastVocabulary.VerdictClass, version, PromotionVerdict.Promoted, t0.AddDays(2), 0.9m)], t0);
            OrgScorecardTests.Verdict(db, version, PromotionVerdict.Refused, t0.AddHours(1));
        }
        Assert.Empty(new ForecastResolver(db).Sweep(t0.AddDays(1)).Refused);
        var r = Assert.Single(org.OccupantSweep(Now, OccupantThresholds.Default, () => []).Replaced);
        Assert.Equal((OccupantRule.Calibration, "scored=10 skill=-0.56 below=0"), (r.Rule, r.Measurement));
        Assert.Empty(org.OccupantSweep(Now.AddDays(1), OccupantThresholds.Default, () => []).Replaced);
    }

    /// <summary>
    /// AN AUDIT FINDING RETIRES THE OCCUPANT ONLY WHEN SEVERE AND ON ITS OWN DELIVERY; and a host that relays no finding says so, never guesses.
    /// </summary>
    [Fact]
    public void A_severe_audit_finding_on_its_own_delivery_retires_the_occupant()
    {
        using var db = Chart();
        var m = Give(db, Now.AddHours(-5));
        var d = Delivered(db, m.Id, 1, Now.AddHours(-1), failed: false);
        var org = new OrgStore(db);
        var none = org.OccupantSweep(Now, OccupantThresholds.Default, null);
        Assert.Empty(none.Replaced);
        Assert.Contains("relays no audit finding", Assert.Single(none.Unjudged), StringComparison.Ordinal);
        Assert.Empty(org.OccupantSweep(Now, OccupantThresholds.Default, () => [new OccupantFinding("af-1", d, "info", Now)]).Replaced);
        Assert.Empty(org.OccupantSweep(Now, OccupantThresholds.Default, () => [new OccupantFinding("af-2", "dlv-elsewhere", "severe", Now)]).Replaced);
        var r = Assert.Single(org.OccupantSweep(Now, OccupantThresholds.Default, () => [new OccupantFinding("af-3", d, "severe", Now)]).Replaced);
        Assert.Equal((OccupantRule.Audit, $"finding=af-3 delivery={d} severity=severe"), (r.Rule, r.Measurement));
    }

    /// <summary>
    /// THE NEW OCCUPANT'S FIRST PACKET IS THE POSITION'S MEMORY (ORGANISATION § 6): before any replacement no takeover block; after it the
    /// block opens the packet — occupant 2, retired by the owner's press, no conversation — with the plan's latest revision quoted, the open
    /// mandate it holds, what carried over, measured; a note the predecessor was shown rides again (shown to an occupant that is gone); and
    /// once a launch of the new occupant has ENDED the block is gone.
    /// </summary>
    [Fact]
    public void The_new_occupants_first_packet_is_the_positions_memory_and_never_the_old_conversation()
    {
        using var db = Chart();
        var m = Give(db, Now.AddHours(-3));
        var org = new OrgStore(db);
        new PublicationStore(db).Record(new Publication
        {
            Id = Publication.IdOf(Worker, PublicationKind.Plan, "# Plan\nCheck the second venue's funding before the first.\n"),
            Role = Worker, Kind = PublicationKind.Plan, Recipients = Worker, Classification = PublicationClass.Private,
            Content = "# Plan\nCheck the second venue's funding before the first.\n", Source = "trading/PLAN.md"
        }, Now.AddHours(-2));
        var note = org.SendLateral(TeamHead, null, Worker, "the funding skew looks venue-specific", [], Now.AddHours(-2));
        Launch(db, "turn-mem-0001", m.Id, Now.AddHours(-1), cost: 0.2m);
        new AiAttemptStore(db).RecordInputs("turn-mem-0001", [new PacketInput(PacketInputKind.Lateral, note.Id)]);
        Assert.DoesNotContain(note.Id, PositionPacket.Compose(db, Worker, Now)!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("occupant", PositionPacket.Compose(db, Worker, Now)!.Text, StringComparison.Ordinal);

        org.ReplaceOccupant(org.PreviewReplaceOccupant(Worker), Now);
        var text = PositionPacket.Compose(db, Worker, Now.AddMinutes(1))!.Text;
        log.WriteLine(text);
        Assert.StartsWith("- You are this position's occupant 2, since 2026-11-04 12:00 UTC. Occupant 1 was retired by your owner's press. You do "
                          + "not have its conversation", text, StringComparison.Ordinal);
        Assert.Contains($"  - Mandate `{m.Id}` you hold, open, due ", text, StringComparison.Ordinal);
        Assert.Contains("  - Your plan, `trading/PLAN.md`, revision 1 of", text, StringComparison.Ordinal);
        Assert.Contains("    > Check the second venue's funding before the first.", text, StringComparison.Ordinal);
        Assert.Contains("  - Your journal, `trading/JOURNAL.md`: none written yet.", text, StringComparison.Ordinal);
        Assert.Contains("Carried over unchanged — a new occupant resets nothing: today this position has spent 0.2 over 1 launch(es)", text,
            StringComparison.Ordinal);
        Assert.Contains($"Note `{note.Id}` from `{TeamHead}`", text, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= PacketBounds.Default.Executor);

        Launch(db, "turn-mem-0002", m.Id, Now.AddMinutes(2), cost: 0.1m);
        Assert.DoesNotContain("You are this position's occupant", PositionPacket.Compose(db, Worker, Now.AddMinutes(5))!.Text, StringComparison.Ordinal);
    }

    static string Src() => Path.Combine(DayOnePrograms.RepoRoot(), "src");

    static bool Built(string f) => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                   || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    static string[] Naming(string text) =>
        [.. Directory.GetFiles(Src(), "*.cs", SearchOption.AllDirectories).Where(f => !Built(f))
            .Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Src(), f).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    /// <summary>
    /// GUARD (AUTH-001; ORG-001 — "never let an agent retire itself or another occupant outside the rule or the owner's press"): NO OP OR VERB
    /// REACHES AN OCCUPANT'S REPLACEMENT. No source of the pipe server, the broker door, the worker's tools, the schema, the CLI or the board
    /// names the owner's press, its preview, the app's sweep, the wake kind it raises or the event it writes, and no op of the pipe is named for
    /// an occupant; the press and the sweep are called by the host alone, and the wake kind is raised by the store file alone (the wake table
    /// and the loop's phrase name it).
    /// </summary>
    [Fact]
    public void No_op_or_verb_reaches_an_occupants_replacement()
    {
        string[] seams = ["ReplaceOccupant", "PreviewReplaceOccupant", "OccupantSweep", "MissionEventKind.Occupant", "occupant-replaced", "OccupantReplaced"];
        var src = Src();
        var agentFacing = Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Gateway"), "GatewayPipeServer*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Gateway"), "TradingGateway*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "BrokerDoor*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "WorkerTools*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.TradeCli"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Core", "Board"), "*.cs"))
            .Concat([Path.Combine(src, "TradeAgent.Gateway", "GatewaySchema.cs")])
            .Where(f => !Built(f)).ToList();
        Assert.Contains(agentFacing, f => f.EndsWith("BrokerDoor.cs", StringComparison.Ordinal));
        var hits = agentFacing.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => seams.Any(x => l.line.Contains(x, StringComparison.Ordinal)))
            .Select(l => $"{Path.GetFileName(l.f)}:{l.i + 1}: {l.line.Trim()}")
            .ToList();
        foreach (var h in hits) log.WriteLine(h);
        Assert.Empty(hits);
        Assert.DoesNotContain(GatewaySchema.Ops(), op => op.Op.Contains("occupant", StringComparison.OrdinalIgnoreCase)
                                                          || op.Op.Contains("replace", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["TradeAgent.App/AppHost.cs", "TradeAgent.Core/Db/OrgStore.Occupants.cs"],
            Naming(".ReplaceOccupant(").Concat(Naming(" ReplaceOccupant(")).Concat(Naming(".OccupantSweep(")).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["TradeAgent.AgentRuntime/MissionLoop.cs", "TradeAgent.Core/Db/MissionEventStore.cs", "TradeAgent.Core/Db/OrgStore.Occupants.cs"],
            Naming("MissionEventKind.Occupant"));
    }

    /// <summary>
    /// THE OWNER'S PRESS ON A POSITION'S ROW TAKES TWO PRESSES: the organisation page draws "Replace the occupant" on the worker's row; press
    /// one arms with the preview's words and writes nothing; press two replaces occupant 1 and says so on the row. A council seat's row and a
    /// dormant row carry none that shows.
    /// </summary>
    [Fact]
    public void Replacing_an_occupant_on_the_organisation_page_takes_two_presses()
    {
        using var db = Chart();
        var page = new OrganisationPage(new OrgSurfaceSources
        {
            Org = () => new OrgStore(db),
            Attempts = () => new AiAttemptStore(db),
            Currency = () => "USD",
            Occupant = new OccupantPresses(p => new OrgStore(db).PreviewReplaceOccupant(p), shown =>
            {
                try { return $"replaced: occupant {new OrgStore(db).ReplaceOccupant(shown, Now).Seated}"; }
                catch (TradeAgentException ex) { return ex.Message; }
            })
        });
        page.Update();
        var shownPresses = page.Root.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.IsVisible && (b.Content as string) == Labels.ReplaceOccupant).ToList();
        Assert.Equal(4, shownPresses.Count);                         // the four active positions of the chart — never research's or operations' row
        var row = page.Root.GetLogicalDescendants().OfType<StackPanel>()
            .First(p => p.Children.OfType<StackPanel>().Any(h => h.Children.OfType<TextBlock>().Any(t => t.Text == Worker)));
        var press = row.Children.OfType<Button>().Single(b => (b.Content as string) == Labels.ReplaceOccupant);
        var events = LastEvent(db);
        press.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Ui.IsArmed(press));
        Assert.Contains($"Replace {Worker}'s occupant 1 with occupant 2", press.Content as string, StringComparison.Ordinal);
        Assert.Equal(events, LastEvent(db));
        press.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(row.Children.OfType<TextBlock>(), t => t.Text == "replaced: occupant 2");
        Assert.Equal(2, new OrgStore(db).OccupancyOf(Worker).Ordinal);
    }
}
