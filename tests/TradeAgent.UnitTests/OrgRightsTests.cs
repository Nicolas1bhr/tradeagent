using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT AN ACTIVE POSITION MAY DO WHEN IT RUNS (<c>U-org-rights</c>, O2b): backtest from its own home, ask a verdict and
/// propose a lens — through <see cref="TradingGateway.ActivePositionOf"/>, the gateway's one answer — and never place,
/// change or cancel an order unless it is <c>operations</c>, headship included (ORDER-001); its output is HELD, published
/// to nobody, never routed to Research; it holds no objective. Rights are not runnability: whether it runs is the
/// activation gate's (<see cref="PositionActivation"/>). Every third position here is seeded by the TEST with raw SQL,
/// as no writer of the chart exists yet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgRightsTests
{
    /// <summary>An id shaped as the app mints one.</summary>
    const string Third = "pos-5be1c07a9d24";

    /// <summary>The team the third position HEADS, under Research's division.</summary>
    const string Team = "team-5be1c07a";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    /// <summary>A team under <paramref name="parent"/>, headed by a new active position — as only a test may write it.</summary>
    static void HeadATeam(Database db, string position, string team, string parent = OrgIds.Research) =>
        db.Write(_ =>
        {
            using (var u = db.Cmd("""
                INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at)
                VALUES($team, $parent, 'team', NULL, 'active', $at)
                """, ("$team", team), ("$parent", parent), ("$at", Stamp)))
                u.ExecuteNonQuery();
            using (var p = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $team, $home, 'active', $at)
                """, ("$id", position), ("$team", team), ("$home", $"org/{position}"), ("$at", Stamp)))
                p.ExecuteNonQuery();
            using var h = db.Cmd("UPDATE org_unit SET head_position_id=$id WHERE id=$team", ("$id", position), ("$team", team));
            return h.ExecuteNonQuery();
        });

    /// <summary>A member position under <paramref name="unit"/>, in the status given.</summary>
    static void Seat(Database db, string id, string unit = OrgIds.Research, string status = OrgPositionStatus.Active) =>
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at)
                VALUES($id, $unit, $home, $status, $at)
                """, ("$id", id), ("$unit", unit), ("$home", $"org/{id}"), ("$status", status), ("$at", Stamp));
            return c.ExecuteNonQuery();
        });

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "rights",
        RequestId = "rights-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    /// <summary>The reply's data as the wire writes it.</summary>
    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    // ---- what a backtest needs -------------------------------------------------------------------------------------

    static readonly DateTimeOffset BarsFrom = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Program = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>A program in <paramref name="role"/>'s own managed home, where the gateway resolves it.</summary>
    static string GivenProgram(string role, string name)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), Program);
        return $"strategies/{name}";
    }

    /// <summary>A dataset the ledger really recorded, with a sawtooth that crosses 103 every ten minutes.</summary>
    static DatasetRecord GivenData(Database db, int bars = 120)
    {
        var pair = TestEnv.NewPair();
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{BarsFrom.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, BarsFrom, BarsFrom.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, BarsFrom, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, BarsFrom, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    // ---- (c) rights, and no order permission ------------------------------------------------------------------------

    /// <summary>
    /// (c) A THIRD POSITION MAY BACKTEST — its program read from its own home, <c>org/&lt;id&gt;</c> — AND ASK A VERDICT,
    /// AND IS REFUSED ORDERS, though it HEADS a team; <c>research</c>, head of <c>div-research</c>, is refused too. RED at
    /// base: <c>Backtests.RoleOf</c> was <c>CouncilRoles.IsKnown</c>, so the third position's backtest and verdict were
    /// refused "presented no launch grant". The mutant — <c>MayPlaceOrders</c> by headship — serves both heads' buy.
    /// </summary>
    [Fact]
    public async Task A_third_position_may_backtest_and_is_refused_orders()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        HeadATeam(db, Third, Team);

        // THE CHART SAYS BOTH ARE HEADS: the third position of its team, Research of its division.
        Assert.Equal(OrgPositionKind.Head, new OrgStore(db).Standing(Third)!.Kind);
        Assert.Equal(OrgPositionKind.Head, new OrgStore(db).Standing(CouncilRoles.Research)!.Kind);

        // A BACKTEST, under its own id, of a program in its own home.
        var set = GivenData(db);
        var ran = await server.CallAsync(Frame(Ops.Backtest, ("strategy", GivenProgram(Third, "third.strategy")),
            ("dataset", set.Id), ("fees", 0.001m), ("increment", 1m)), Third, "turn-third-1");
        Assert.True(ran.Ok, Json.Write(ran.Error));
        var runId = Body(ran).GetProperty("run_id").GetString()!;
        var run = gw.Strategies.RunById(runId)!;
        Assert.Equal(Third, run.Role);
        Assert.Equal("turn-third-1", run.Attempt);

        // ITS HOME IS ITS OWN: a program only in Research's home is not one it can name.
        GivenProgram(CouncilRoles.Research, "research-only.strategy");
        var elsewhere = await server.CallAsync(Frame(Ops.Backtest, ("strategy", "strategies/research-only.strategy"),
            ("dataset", set.Id), ("fees", 0.001m), ("increment", 1m)), Third, "turn-third-2");
        Assert.False(elsewhere.Ok);

        // A VERDICT IS ASKED: whatever is refused, it is not the caller — the role clause passes for an active position.
        var verdict = await server.CallAsync(Frame(Ops.Verdict, ("version", "no-such-version")), Third, "turn-third-3");
        Assert.False(verdict.Ok);
        Assert.DoesNotContain("presented no launch grant", verdict.Error!.Message, StringComparison.Ordinal);

        // THE HOLDOUT'S REFUSAL NAMES THE POSITION, never "a caller that proved no role".
        Assert.Contains(Third, BarAudience.Pipe(Third).Who, StringComparison.Ordinal);

        // AN APP PRINCIPAL, A DORMANT POSITION AND NOBODY are refused the backtest, as before.
        Seat(db, "pos-dormant", status: OrgPositionStatus.Dormant);
        foreach (var who in AppPrincipals.All.Append("pos-dormant").Append("nobody-here").Append(null))
        {
            var refused = await server.CallAsync(Frame(Ops.Backtest, ("strategy", "strategies/third.strategy"),
                ("dataset", set.Id), ("fees", 0.001m), ("increment", 1m)), who, "turn-x");
            Assert.False(refused.Ok);
            Assert.Contains("presented no launch grant", refused.Error!.Message, StringComparison.Ordinal);
        }

        // NO ORDER PERMISSION, HEADSHIP INCLUDED: the third position and Research are both refused a buy.
        foreach (var head in new[] { Third, CouncilRoles.Research })
        {
            Assert.False(CouncilRoles.MayPlaceOrders(head));
            Assert.False(AgentContext.ForAgent("s", head).MayPlaceOrders);
            var buy = await server.CallAsync(Frame(Ops.Buy, ("symbol", "ES"), ("quantity", 1)), head, "turn-buy");
            Assert.False(buy.Ok, $"{head}'s buy was served");
            Assert.Equal(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Error!.Code);
        }
        Assert.True(CouncilRoles.MayPlaceOrders(CouncilRoles.Operations));
    }

    // ---- (d) the relay holds a third position's output ---------------------------------------------------------------

    /// <summary>
    /// (d) A THIRD POSITION'S OUTPUT IS NEVER ROUTED TO RESEARCH. RED at base: <c>RecipientsOf</c> sent every role but
    /// Research's to Research, so <c>Run(Third)</c> landed its file as Research's agenda, with a paid wake. The mutant —
    /// <c>RecipientsOf</c>'s else-branch serving a non-legacy position — does the same. Legacy routing is unchanged.
    /// </summary>
    [Fact]
    public void A_third_positions_output_is_never_routed_to_research()
    {
        var root = Path.Combine(TestEnv.Home, $"rights-relay-{Guid.NewGuid():n}");
        string Home(string role) => WorkspaceBuilder.HomeOf(root, role);
        foreach (var role in CouncilRoles.All.Append(Third))
        {
            Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.InDir));
            Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.OutDir));
        }

        using var db = TestEnv.NewDb();
        Seat(db, Third);
        var attempts = new AiAttemptStore(db);
        attempts.Begin(new AiAttempt { Id = "turn-third-out", StartedAt = DateTimeOffset.UtcNow, Role = Third });
        File.WriteAllText(Path.Combine(Home(Third), WorkspaceBuilder.OutDir, MissionSituation.OutputName(Third, "turn-third-out")),
            "what the third position found");

        var relay = new CouncilRelay(db, Home);
        relay.Run(Third, "turn-third-out");

        var store = new PublicationStore(db);
        Assert.Empty(store.To(CouncilRoles.Research));
        Assert.Empty(store.To(CouncilRoles.Operations));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Home(CouncilRoles.Research), WorkspaceBuilder.InDir)));
        var events = new MissionEventStore(db);
        Assert.Empty(events.DueFor(CouncilRoles.Research, DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Empty(events.DueFor(CouncilRoles.Operations, DateTimeOffset.UtcNow.AddMinutes(1)));

        // PUBLISHED TO NOBODY, and the file the third position wrote is the app's record, attributed to its launch.
        var held = Assert.Single(store.By(Third));
        Assert.Empty(held.RecipientList);
        Assert.Equal("turn-third-out", held.Attempt);
        Assert.Equal(PublicationKind.Report, held.Kind);

        // THE HOLD IS A DISPOSITION, never a wake: born consumed by no launch, and named in the writer's next Situation.
        var hold = Assert.Single(events.OfKind(MissionEventKind.Output));
        Assert.Equal(MissionEventIds.Held(held.Id), hold.Id);
        Assert.Equal(Third, hold.Role);
        Assert.Equal(MissionEventDisposition.Held, hold.Disposition);
        Assert.Equal(CouncilRelay.HeldWhy, hold.DispositionDetail);
        Assert.NotNull(hold.ConsumedAt);
        Assert.Null(hold.ConsumedBy);
        Assert.Empty(events.DueFor(Third, DateTimeOffset.UtcNow.AddMinutes(1)));
        var notice = Assert.Single(CouncilRelay.HeldNotices(db, Third));
        Assert.Contains(held.Source!, notice, StringComparison.Ordinal);
        Assert.Contains($"{MissionEventDisposition.Held} — {CouncilRelay.HeldWhy}", notice, StringComparison.Ordinal);
        var text = new MissionSituation { Role = Third, HeldOutputs = CouncilRelay.HeldNotices(db, Third) }.Text();
        Assert.Contains(notice, text, StringComparison.Ordinal);
        Assert.Empty(CouncilRelay.HeldNotices(db, CouncilRoles.Research));

        // A RE-RUN HOLDS NOTHING TWICE, and an assessment it stages is not its to publish (a boundary is the legacy pair's).
        File.WriteAllText(Path.Combine(Home(Third), WorkspaceBuilder.OutDir, "assessment-turn-third-out.md"), "my view");
        relay.Run(Third, "turn-third-out");
        Assert.Single(store.By(Third));
        Assert.Single(events.OfKind(MissionEventKind.Output));

        // RECONCILE WALKS THE ACTIVE POSITIONS: a lost turn's file is held on the start-up pass too, never routed.
        attempts.Begin(new AiAttempt { Id = "turn-third-lost", StartedAt = DateTimeOffset.UtcNow, Role = Third });
        attempts.LoseOpen(DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(Home(Third), WorkspaceBuilder.OutDir, MissionSituation.OutputName(Third, "turn-third-lost")),
            "what the lost turn found");
        relay.Reconcile();
        Assert.Equal(2, store.By(Third).Count);
        Assert.All(store.By(Third), p => Assert.Empty(p.RecipientList));
        Assert.Equal(2, events.OfKind(MissionEventKind.Output).Count);
        Assert.Empty(store.To(CouncilRoles.Research));

        // LEGACY ROUTING IS UNCHANGED: Research's report still reaches the chair.
        attempts.Begin(new AiAttempt { Id = "turn-research-out", StartedAt = DateTimeOffset.UtcNow, Role = CouncilRoles.Research });
        File.WriteAllText(Path.Combine(Home(CouncilRoles.Research), WorkspaceBuilder.OutDir,
            MissionSituation.OutputName(CouncilRoles.Research, "turn-research-out")), "what Research found");
        relay.Run(CouncilRoles.Research, "turn-research-out");
        Assert.Single(store.To(CouncilRoles.Operations));
        Assert.Empty(store.To(CouncilRoles.Research));
    }

    // ---- U-org-wakes (f): a held output and its disposition commit together --------------------------------------------

    /// <summary>
    /// (f) A HELD OUTPUT AND ITS HOLD COMMIT TOGETHER (<c>U-org-wakes</c> item 6; the O2b review's P2, its settling check). The
    /// start-up pass (<see cref="CouncilRelay.Reconcile"/>) records a third position's lost turn's file and holds it; the hold's
    /// insert is made to fail. Then the file is deleted and the pass retried: no output may be left recorded with no
    /// disposition — the writer would never be told of it. And where the file is still there, the retry holds it once, and the
    /// writer's next Situation names it.
    ///
    /// <para>RED at base: the record committed on its own, so after the failed hold and the deleted file one publication of the
    /// position stood with no held row and no notice, for good.</para>
    /// </summary>
    [Fact]
    public void A_held_output_and_its_hold_commit_together()
    {
        foreach (var keepFile in new[] { false, true })
        {
            var root = Path.Combine(TestEnv.Home, $"rights-hold-{Guid.NewGuid():n}");
            string Home(string role) => WorkspaceBuilder.HomeOf(root, role);
            foreach (var role in CouncilRoles.All.Append(Third))
            {
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.InDir));
                Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.OutDir));
            }

            using var db = TestEnv.NewDb();
            Seat(db, Third);
            var attempts = new AiAttemptStore(db);
            attempts.Begin(new AiAttempt { Id = "turn-third-lost", StartedAt = DateTimeOffset.UtcNow, Role = Third });
            attempts.LoseOpen(DateTimeOffset.UtcNow);
            var file = Path.Combine(Home(Third), WorkspaceBuilder.OutDir, MissionSituation.OutputName(Third, "turn-third-lost"));
            File.WriteAllText(file, "what the lost turn found");

            void Exec(string sql) => db.Write(_ =>
            {
                using var c = db.Cmd(sql);
                return c.ExecuteNonQuery();
            });
            Exec("""
                CREATE TRIGGER abort_hold BEFORE INSERT ON mission_event WHEN NEW.kind = 'output'
                BEGIN SELECT RAISE(ABORT, 'the hold could not be written'); END
                """);
            var relay = new CouncilRelay(db, Home);
            Assert.NotNull(Record.Exception(relay.Reconcile));

            Exec("DROP TRIGGER abort_hold");
            if (!keepFile) File.Delete(file);
            relay.Reconcile();

            var store = new PublicationStore(db);
            var held = new MissionEventStore(db).OfKind(MissionEventKind.Output);
            Assert.Equal(store.By(Third).Select(p => MissionEventIds.Held(p.Id)).Order(StringComparer.Ordinal),
                held.Select(e => e.Id).Order(StringComparer.Ordinal));
            if (keepFile)
            {
                var row = Assert.Single(held);
                Assert.Equal(MissionEventDisposition.Held, row.Disposition);
                Assert.Single(CouncilRelay.HeldNotices(db, Third));
            }
            else
            {
                Assert.Empty(store.By(Third));
                Assert.Empty(held);
            }
        }
    }

    // ---- (f) minting --------------------------------------------------------------------------------------------------

    /// <summary>The names Windows reserves for devices, spelled here independently of the build's own list.</summary>
    static readonly string[] DeviceNames =
    [
        "con", "prn", "aux", "nul",
        .. Enumerable.Range(0, 10).Select(i => $"com{i}"), .. Enumerable.Range(0, 10).Select(i => $"lpt{i}")
    ];

    /// <summary>
    /// (f) A MINTED POSITION ID NEVER EQUALS A LEGACY ID, AN APP PRINCIPAL OR A DEVICE NAME, and the one validator
    /// <c>U-org-verbs</c>' writers call refuses each of those by name. RED at base: no minting rule and no validator
    /// existed (<c>OrgStore</c> has no writer; ids are minted by <c>U-org-verbs</c>, so the RULE lands here as code).
    /// </summary>
    [Fact]
    public void A_minted_position_id_never_equals_a_legacy_id_an_app_principal_or_a_device_name()
    {
        var reserved = CouncilRoles.All.Concat(AppPrincipals.All).Concat(DeviceNames).ToArray();

        var minted = Enumerable.Range(0, 2000).Select(_ => OrgIds.MintPosition()).ToList();
        Assert.Equal(minted.Count, minted.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in minted)
        {
            Assert.StartsWith(OrgIds.PositionPrefix, id, StringComparison.Ordinal);
            Assert.True(CouncilRoles.IsPositionId(id), id);
            Assert.Null(OrgIds.PositionIdRefusal(id));
            Assert.DoesNotContain(id, reserved);
        }

        // THE PREFIX ITSELF IS ONE NONE OF THEM CAN TAKE.
        foreach (var name in reserved)
            Assert.False(name.StartsWith(OrgIds.PositionPrefix, StringComparison.Ordinal), name);

        // THE VALIDATOR REFUSES EACH, IN A SENTENCE NAMING WHAT IT IS.
        foreach (var legacy in CouncilRoles.All)
            Assert.Contains("legacy", OrgIds.PositionIdRefusal(legacy)!, StringComparison.Ordinal);
        foreach (var principal in AppPrincipals.All)
            Assert.Contains("app's own principals", OrgIds.PositionIdRefusal(principal)!, StringComparison.Ordinal);
        foreach (var device in DeviceNames)
            Assert.Contains("Windows reserves", OrgIds.PositionIdRefusal(device)!, StringComparison.Ordinal);
        foreach (var malformed in new[] { null, "", "CON", "Pos-1", "pos 1", "pos/1", "pos.1", "../pos" })
            Assert.NotNull(OrgIds.PositionIdRefusal(malformed));
    }

    // ---- (g) lenses -------------------------------------------------------------------------------------------------

    /// <summary>A lens over OKX's announcements, this suite's own words.</summary>
    const string Lens = """
        {"id":"rights-headline","target":{"source":"okx-eea-announcements","series":"announcements"},
         "state":{"fields":[{"path":"title","max_chars":200}]},
         "questions":{"kind":{"type":"choice","instructions":"Which kind of notice is this?",
                              "criteria":{"listing":null,"maintenance":null,"none_of_these":null}}}}
        """;

    /// <summary>
    /// (g) A THIRD POSITION MAY PROPOSE A LENS AND AN APP PRINCIPAL MAY NOT. RED at base: <c>LensPropose</c> asked
    /// <c>CouncilRoles.IsKnown</c>, so the third position was refused "presented no launch grant" (<c>:4026</c>).
    /// </summary>
    [Fact]
    public async Task A_third_position_may_propose_a_lens_and_an_app_principal_may_not()
    {
        var (gw, _, db) = await TestEnv.Ready();
        await using var _gw = gw;
        using var _db = db;
        using var tape = new TapeStore(Path.Combine(TestEnv.Home, $"rights-lens-{Guid.NewGuid():n}.db"));
        var registered = new List<string>();
        gw.LensRegistrar = (definition, proposer, attempt) =>
        {
            lock (registered) registered.Add($"{proposer}/{attempt}");
            return tape.RegisterLens(definition, proposer, attempt);
        };
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        Seat(db, Third);
        Seat(db, "pos-dormant", status: OrgPositionStatus.Dormant);

        var proposed = await server.CallAsync(Frame(Ops.LensPropose, ("lens", Lens)), Third, "turn-lens-1");
        Assert.True(proposed.Ok, Json.Write(proposed.Error));
        Assert.Equal(Third, Body(proposed).GetProperty("proposer").GetString());
        Assert.Equal([$"{Third}/turn-lens-1"], registered);

        foreach (var who in AppPrincipals.All.Append("pos-dormant").Append("nobody-here").Append(null))
        {
            var refused = await server.CallAsync(
                Frame(Ops.LensPropose, ("lens", Lens.Replace("rights-headline", $"rights-{who ?? "none"}"))), who, "turn-lens-x");
            Assert.False(refused.Ok, $"{who ?? "nobody"} proposed a lens");
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), refused.Error!.Code);
        }
        Assert.Single(registered);
    }

    // ---- (i) no objective -------------------------------------------------------------------------------------------

    /// <summary>
    /// (i) GUARD, FLIPPED FOR HEADS BY <c>U-org-wakes</c>: A MEMBER HOLDS NO OBJECTIVE, AND A HEAD ONLY THE OWNER'S WORDS TO IT.
    /// A member's objectives are mandates (<c>U-org-assignments</c>), so nothing open — a verdict wake raised for it, an open
    /// boundary, the owner's words — opens one for it. A head after the legacy pair owns the owner's words addressed to it
    /// (ORCHESTRATION § 2.5) and nothing else yet; the legacy pair's kinds are theirs alone.
    /// </summary>
    [Fact]
    public void A_member_holds_no_objective_and_a_head_only_the_owners_words_to_it()
    {
        using var db = TestEnv.NewDb();
        HeadATeam(db, Third, Team);
        const string member = "pos-0c0ffee0c0de";
        Seat(db, member, unit: Team);
        var events = new MissionEventStore(db);
        foreach (var who in new[] { Third, member })
            events.Raise(MissionEventIds.ForRole(MissionEventIds.Verdict("promotion-x"), who),
                MissionEventKind.Verdict, DateTimeOffset.UtcNow, role: who);

        Assert.Empty(RoleObjectives.KindsOf(Third));
        Assert.Equal([RoleObjectives.OwnerAnswered], RoleObjectives.KindsOf(Third, OrgPositionKind.Head));
        Assert.Empty(RoleObjectives.KindsOf(member, OrgPositionKind.Member));
        Assert.Empty(RoleObjectives.Open(db, Third, book: null));
        Assert.Empty(RoleObjectives.Open(db, Third, book: ["ES 1 long"]));

        // THE OWNER'S WORDS TO EACH: the head owns them, the member does not.
        foreach (var who in new[] { Third, member })
            events.Raise(MissionEventIds.ForRole(MissionEventIds.Owner(800), who), MissionEventKind.Owner, DateTimeOffset.UtcNow,
                Json.Write(new MissionOwnerMessage("status?", DateTimeOffset.UtcNow)), who);
        var owned = Assert.Single(RoleObjectives.Open(db, Third, book: null));
        Assert.Equal(RoleObjectives.OwnerAnswered, owned.Kind);
        Assert.Empty(RoleObjectives.Open(db, member, book: null));

        // The legacy pair keep theirs.
        Assert.NotEmpty(RoleObjectives.KindsOf(CouncilRoles.Operations));
        Assert.NotEmpty(RoleObjectives.KindsOf(CouncilRoles.Research));
        Assert.Contains(RoleObjectives.Open(db, CouncilRoles.Research, null), o => o.Kind == RoleObjectives.ResearchMandate);
    }
}
