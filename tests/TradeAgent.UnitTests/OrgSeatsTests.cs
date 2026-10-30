using System.Globalization;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>U-org-seats</c> — THE METER, THE CONVERSATION AND THE HARNESS BOUND READ ONE SEAT (ORCHESTRATION §§ 2.1, 2.4, 2.7, 5; ORGANISATION
/// § 10; R15 § 3c; R18 #1): a runtime, a model and an allowance resolved once per launch, a reservation below the shipped bound only where
/// the turn is held to it, and every attempt recording the allowance it was reserved at and whether that bound is enforced.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgSeatsTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"org-seats-{Guid.NewGuid():n}.jsonl");
    readonly string _home = Path.Combine(TestEnv.Home, $"org-seats-{Guid.NewGuid():n}");
    readonly DateTimeOffset _at = TestEnv.LocalNoon();

    /// <summary>The owner's own two numbers per million tokens, so a reservation has exactly one right answer.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    const string Harness = ApiAgentRuntime.RuntimeId;
    const string Pos = "pos-seat";

    static readonly TurnAllowance Small = new(300_000, 8_000);

    public OrgSeatsTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, CouncilRoles.OutDir));
        Directory.CreateDirectory(Path.Combine(_home, CouncilRoles.InDir));
    }

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        _db.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    TurnMeter Meter(Func<string?, SeatSpec> seat) =>
        new(_db, () => 50m, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m, seat: seat,
            live: new LiveAttempts());

    static decimal Priced(TurnAllowance a) => a.InputTokens * 1m / 1_000_000m + a.OutputTokens * 4m / 1_000_000m;

    ApiAgentRuntime HarnessOn(FakeProvider provider, TurnAllowance appWide)
    {
        var manifest = RuntimeCatalog.Require(Harness);
        manifest.BaseUrl = provider.BaseUrl;
        // THE APP-WIDE FIGURE AS THE RUNTIME'S OWN FALLBACK — what the harness read before this unit: only a turn that reads its
        // admitted seat is held to the seat's allowance.
        return new ApiAgentRuntime(manifest, provider.Holding("not-a-real-credential"), allowance: _ => appWide,
            requestTimeout: TimeSpan.FromSeconds(10));
    }

    static long[] MaxOutputs(FakeProvider provider) => [.. provider.Requests.Select(sent =>
    {
        using var body = JsonDocument.Parse(sent);
        return body.RootElement.GetProperty("max_completion_tokens").GetInt64();
    })];

    static string ModelOf(string sent)
    {
        using var body = JsonDocument.Parse(sent);
        return body.RootElement.GetProperty("model").GetString()!;
    }

    AiAttempt OnlyRow() => Assert.Single(Rows(_db));

    static List<AiAttempt> Rows(Database db) =>
        new AiAttemptStore(db).Between(DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(2));

    void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    void Position(Database db, string id = Pos)
    {
        Exec(db, """
            INSERT INTO org_unit(id, parent_id, kind, status, created_at) VALUES('team-seat', 'div-research', 'team', 'active', $at)
            ON CONFLICT DO NOTHING
            """, ("$at", Sql.T(_at)));
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id, 'team-seat', $home, 'active', $at)",
            ("$id", id), ("$home", $"org/{id}"), ("$at", Sql.T(_at)));
    }

    // ---- (a) the seat's allowance prices the reservation and bounds the harness turn -------------------------------

    /// <summary>
    /// (a) A POSITION SEATED ON THE HARNESS AT 300 k / 8 k RESERVES AT 300 k / 8 k, AND ITS TURN IS HELD THERE: each request asks for the
    /// output still unspent (8,000, then 5,000, then 2,000), the turn ends on its bound with its work kept, and its row reads
    /// <c>allow_in 300000, allow_out 8000, turn_bound enforced</c>. The runtime is handed the app-wide 1.2 M / 20 k as its own fallback,
    /// so only a turn that reads the seat it was admitted on is held to the seat. THE WATCHED MUTANT: the harness conversation reading
    /// that fallback instead of its admitted seat asks for 20,000 a request.
    /// </summary>
    [Fact]
    public async Task A_seat_allowance_prices_the_reservation_and_bounds_the_harness_turn()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 100_000, output: 3_000));
        var meter = Meter(role => SeatResolver.Of(role, Harness, "gpt-5.6-luna", Small));
        Assert.Equal(Priced(Small), meter.Reservation(CouncilRoles.Research).Cost);

        using var runtime = HarnessOn(provider, TurnAllowance.Default);
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("go on then");

        Assert.Equal([8_000L, 5_000L, 2_000L], MaxOutputs(provider));
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        var row = OnlyRow();
        Assert.Equal((300_000L, 8_000L, SeatSpec.EnforcedName), (row.AllowIn, row.AllowOut, row.TurnBound));
        Assert.Equal(Priced(Small), row.ReservedCost);
        Assert.Equal(AiAttemptState.ENDED, row.State);
    }

    // ---- (b) a CLI seat never reserves below the shipped bound -----------------------------------------------------

    /// <summary>
    /// (b) THE SAME SEAT ON THE CODEX CLI RESERVES AT max(seat, 1.2 M) / max(seat, 20 k): nothing stops a vendor's program midway, so a
    /// smaller allowance there would be a smaller reservation for an unbounded turn. Its row reads the shipped bound and
    /// <c>turn_bound reservation</c>; a larger seat raises the reservation, component by component.
    /// </summary>
    [Fact]
    public void A_cli_seat_never_reserves_below_the_shipped_bound()
    {
        var meter = Meter(role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", Small));
        var shipped = CostCatalog.Reserve(TurnAllowance.Default, "codex", owner: Rate, requestedModel: "gpt-5.6-sol").Cost;
        Assert.Equal(shipped, meter.Reservation(CouncilRoles.Research).Cost);
        Assert.Equal(Priced(TurnAllowance.Default), shipped);

        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
        var row = OnlyRow();
        Assert.Equal((1_200_000L, 20_000L, SeatSpec.ReservationName), (row.AllowIn, row.AllowOut, row.TurnBound));
        Assert.Equal(shipped, row.ReservedCost);

        var larger = Meter(role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", new TurnAllowance(2_000_000, 8_000)));
        Assert.Equal(Priced(new TurnAllowance(2_000_000, 20_000)), larger.Reservation(CouncilRoles.Research).Cost);
    }

    // ---- (c) the model launched is the model reserved for -----------------------------------------------------------

    /// <summary>
    /// (c) THE MODEL ON EVERY LAUNCH IS THE ROW'S <c>requested_model</c>. On the harness: the request body names the admitted seat's model
    /// even where the conversation was opened on another. On the CLI: the app's conversation model is the admitted seat's, so a choice
    /// changed between the admission and the launch does not reach the argv. A broker-only seat: the seat resolves to the pin, and its
    /// conversation is handed the choice it verifies against that pin (<c>U-thinking-seats-c</c>).
    /// </summary>
    [Fact]
    public async Task The_model_launched_is_the_model_reserved_for_on_every_seat_kind()
    {
        // THE HARNESS.
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("done", input: 10, output: 5));
        var meter = Meter(role => SeatResolver.Of(role, Harness, "gpt-5-nano", TurnAllowance.Default));
        using var runtime = HarnessOn(provider, TurnAllowance.Default);
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(),
            () => "gpt-5.6-luna");
        using (meter.Attach(conversation, CouncilRoles.Research)) await conversation.SendAsync("go on then");
        Assert.Equal("gpt-5-nano", OnlyRow().RequestedModel);
        Assert.Equal("gpt-5-nano", ModelOf(Assert.Single(provider.Requests)));

        // THE CLI, THROUGH THE APP: the conversation's model is the admitted seat's, not the choice as it reads at launch.
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        host.Gateway.Update(s =>
        {
            s.RoleRuntime[CouncilRoles.Research] = "codex";
            s.RoleModel[CouncilRoles.Research] = "gpt-5.6-sol";
            s.AiDailyCostCap = 1_000m;   // room for one shipped-bound reservation at list price, in Research's half of the day
        });
        var admitted = host.Meter!.Begin("## Situation", role: CouncilRoles.Research);
        Assert.True(admitted.Admitted, admitted.Refusal);
        var row = Assert.Single(Rows(db));
        Assert.Equal("gpt-5.6-sol", row.RequestedModel);
        host.Gateway.Update(s => s.RoleModel[CouncilRoles.Research] = "gpt-5.6-luna");
        Assert.Equal(row.RequestedModel, host.ConversationModelFor(CouncilRoles.Research));
        Assert.Equal("gpt-5.6-luna", host.SeatFor(CouncilRoles.Research).Model);   // the NEXT launch's seat moves; this one's does not

        // A BROKER-ONLY SEAT: its model is the pin whatever was chosen, and the pin is what its session launches.
        Assert.Equal(BrokerSeat.Model, AppHost.SeatModelOn(SeatPlan.BrokerClass, chosen: "gpt-5.6-luna", resolved: "gpt-5.6-luna"));
        Assert.Equal("gpt-5.6-luna", AppHost.SeatModelOn(SeatClasses.Cli, chosen: "gpt-5.6-luna", resolved: "gpt-5.6-luna"));
        // ...unless the choice is a model of the verified catalog (U-seat-models): the fallback is priced as the fallback, on both verified
        // classes; no choice is the default.
        foreach (var verified in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            Assert.Equal(BrokerSeat.Fallback, AppHost.SeatModelOn(verified, chosen: BrokerSeat.Fallback, resolved: "gpt-5.6-sol"));
            Assert.Equal(BrokerSeat.Model, AppHost.SeatModelOn(verified, chosen: null, resolved: "gpt-5.6-sol"));
        }
    }

    // ---- (c2) the owner switches a position's model among the verified catalog's (U-seat-models) -------------------

    /// <summary>
    /// (c2) THE OWNER SWITCHES A POSITION'S MODEL, ONLY AMONG THE VERIFIED CATALOG'S AND ONLY WHILE NO BOARD SEAT IS ACTIVE (<c>U-seat-models</c>;
    /// R25 D8): a position with no seat of its own is seated on the runtime its launch resolves to, at the policy's allowance, on the chosen
    /// model, with one <c>org_event seated</c> decided by the owner; a second switch keeps that runtime and allowance. Refused in words, nothing
    /// written: a model outside the catalog, the model it already runs on, the legacy pair, and anything once a board seat is active.
    /// </summary>
    [Fact]
    public void The_owner_switches_a_positions_model_among_the_catalogs_only_while_no_board_seat_is_active()
    {
        using var db = TestEnv.NewDb();
        Position(db);
        var store = new OrgStore(db);
        static bool Known(string id) => RuntimeCatalog.Find(id) is not null;
        var now = DateTimeOffset.UtcNow;
        long Events() => db.Read(_ => { using var c = db.Cmd("SELECT COUNT(*) FROM org_event WHERE kind='seated' AND decider='owner'"); return (long)c.ExecuteScalar()!; });

        Assert.Null(store.ChooseSeatModel(Pos, BrokerSeat.Fallback, BrokerSeat.IsCatalogModel, "codex", Known, now));
        var policy = new TradeAgent.Core.Board.BoardReader(db).InForce().Policy;
        var row = store.Position(Pos)!;
        Assert.Equal(("codex", BrokerSeat.Fallback, policy.TurnAllowanceInput.ToString(), policy.TurnAllowanceOutput.ToString()),
            (row.SeatRuntime, row.SeatModel, row.SeatAllowIn, row.SeatAllowOut));
        Assert.Equal(1, Events());

        foreach (var (model, said) in new[] { ("gpt-6-astra", "not a model of this build's verified catalog"), ("gpt-5.6-sol", "not a model"),
                                               (BrokerSeat.Fallback, "already set to run on") })
            Assert.Contains(said, Assert.Throws<TradeAgentException>(() => store.ChooseSeatModel(Pos, model, BrokerSeat.IsCatalogModel, "codex", Known, now)).Message);
        Assert.Contains("Owner page", Assert.Throws<TradeAgentException>(() =>
            store.ChooseSeatModel(CouncilRoles.Research, BrokerSeat.Model, BrokerSeat.IsCatalogModel, "codex", Known, now)).Message);
        Assert.Equal(BrokerSeat.Fallback, store.Position(Pos)!.SeatModel);
        Assert.Equal(1, Events());

        store.SetSeat(Pos, Harness, BrokerSeat.Fallback, 300_000, 8_000, Known, now);
        Assert.Equal(BrokerSeat.Fallback, store.ChooseSeatModel(Pos, BrokerSeat.Model, BrokerSeat.IsCatalogModel, "codex", Known, now));
        row = store.Position(Pos)!;
        Assert.Equal((Harness, BrokerSeat.Model, "300000", "8000"), (row.SeatRuntime, row.SeatModel, row.SeatAllowIn, row.SeatAllowOut));

        new TradeAgent.Core.Board.BoardStore(db).SetSeatStateUnderTest("board-2", TradeAgent.Core.Board.BoardSeatStatus.Active, now);
        var refused = Assert.Throws<TradeAgentException>(() => store.ChooseSeatModel(Pos, BrokerSeat.Fallback, BrokerSeat.IsCatalogModel, "codex", Known, now)).Message;
        Assert.Contains("the security board's now that a board seat is active", refused);
        Assert.Contains("Nothing was written", refused);
        Assert.Equal(BrokerSeat.Model, store.Position(Pos)!.SeatModel);
    }

    // ---- (d) the harness never sends a request that could pass the seat allowance --------------------------------

    /// <summary>
    /// (d) EVERY REQUEST ASKS FOR THE OUTPUT STILL UNSPENT, AND A BODY THAT COULD TAKE THE INPUT PAST THE ALLOWANCE IS WITHHELD — counted
    /// as its UTF-8 bytes (a byte-level BPE token covers at least one byte: INFERENCE, the vendor's tokenizer NOT VERIFIED). Here the
    /// provider reports little, so only the body count can stop the turn: the first request goes, the second — whose body with the
    /// first's reported input would pass the allowance — is never sent.
    /// </summary>
    [Fact]
    public async Task The_harness_never_sends_a_request_that_could_pass_the_seat_allowance()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 10, output: 5));
        using var probe = new FakeProvider();
        probe.Answer(FakeProvider.Message("done", input: 10, output: 5));
        using (var sizing = HarnessOn(probe, TurnAllowance.Default))
            await sizing.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null)
                .SendMissionAsync("## Situation");
        var first = System.Text.Encoding.UTF8.GetByteCount(Assert.Single(probe.Requests));

        // Room for the first body and its reported input, and not for a second body of at least the same size.
        using var runtime = HarnessOn(provider, new TurnAllowance(first + 10 + first / 2, 20_000));
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;
        await conversation.SendMissionAsync("## Situation");

        Assert.Single(provider.Requests);
        Assert.Equal([20_000L], MaxOutputs(provider));
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        Assert.Contains(conversation.History, t => t.Role == ChatRole.System
            && t.Text.Contains("could take it past that, so no further request was sent", StringComparison.Ordinal));
    }

    // ---- (e) a malformed seat refuses the launch in words and keeps its wakes -------------------------------------

    /// <summary>
    /// (e) A SEAT COLUMN THAT DOES NOT PARSE REFUSES THE LAUNCH IN WORDS NAMING THE POSITION AND THE COLUMN: the app's activation seam
    /// answers a refusal about the launch (its wakes kept, never settled), and the meter writes no row, reserves nothing and consumes no
    /// wake. Never half a seat: a runtime set with no model is refused, not filled from the Safety page.
    /// </summary>
    [Fact]
    public void A_malformed_seat_refuses_the_launch_in_words_and_keeps_its_wakes()
    {
        using var db = TestEnv.NewDb();
        Position(db);
        Exec(db, "UPDATE org_position SET seat_runtime='codex' WHERE id=$id", ("$id", Pos));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());

        var refusal = host.ActivationRefusal(Pos);
        Assert.Equal(RefusalKind.HostOrLaunch, refusal?.Kind);
        Assert.Contains($"“{Pos}”", refusal!.Words, StringComparison.Ordinal);
        Assert.Contains(SeatColumns.ModelColumn, refusal.Words, StringComparison.Ordinal);
        Assert.Throws<SeatRefusedException>(() => host.SeatFor(Pos));

        var wakes = new MissionEventStore(db);
        Assert.True(wakes.Raise("wake-seat-1", MissionEventKind.Data, DateTimeOffset.Now, role: Pos));
        var admission = host.Meter!.Begin("## Situation", ["wake-seat-1"], role: Pos);
        Assert.False(admission.Admitted);
        Assert.Equal(RefusalKind.HostOrLaunch, admission.Kind);
        Assert.Contains(SeatColumns.ModelColumn, admission.Refusal, StringComparison.Ordinal);
        Assert.Empty(Rows(db));
        Assert.False(wakes.Get("wake-seat-1")!.Consumed, "a launch refused on its seat consumed its wake");

        foreach (var (column, value) in new[] { ("seat_allow_in", "0"), ("seat_allow_out", "eight thousand"), ("seat_runtime", "nobody") })
        {
            Exec(db, "UPDATE org_position SET seat_runtime='codex', seat_model='gpt-5.6-sol', seat_allow_in='300000', seat_allow_out='8000' WHERE id=$id",
                ("$id", Pos));
            Exec(db, $"UPDATE org_position SET {column}=$v WHERE id=$id", ("$v", value), ("$id", Pos));
            Assert.Contains(column, Assert.Throws<SeatRefusedException>(() => host.SeatFor(Pos)).Message, StringComparison.Ordinal);
        }
    }

    // ---- (f) GUARD: only the owner, in-process, seats a position ---------------------------------------------------

    /// <summary>
    /// (f) GUARD: NO PIPE OP, DOOR VERB OR <c>trade</c> VERB WRITES A SEAT (AUTH-001). No op or CLI verb or argument names a seat; the
    /// only statement in the source that writes a seat column is <see cref="OrgStore.SetSeat"/>'s, and nothing in the gateway's pipe or the
    /// broker's door calls it. The writer itself: one <c>org_event seated</c> decided by the owner; the legacy pair, an unknown runtime and
    /// a non-positive allowance refused with nothing written.
    /// </summary>
    [Fact]
    public void No_pipe_op_door_verb_or_trade_verb_writes_a_seat()
    {
        foreach (var op in GatewaySchema.Ops())
        {
            Assert.DoesNotContain("seat", op.Op, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seat", op.Cli, StringComparison.OrdinalIgnoreCase);
            foreach (var arg in op.Args) Assert.DoesNotContain("seat", arg.Name, StringComparison.OrdinalIgnoreCase);
        }

        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        string[] Matching(string pattern) => [.. files
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), pattern))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["src/TradeAgent.Core/Db/OrgStore.Seats.cs"], Matching(@"\bseat_(runtime|model|allow_in|allow_out)\s*="));
        Assert.Equal(["src/TradeAgent.Core/Db/OrgStore.Seats.cs"], Matching(@"\bSetSeat\("));

        using var db = TestEnv.NewDb();
        Position(db);
        var store = new OrgStore(db);
        static bool Known(string id) => RuntimeCatalog.Find(id) is not null;
        Assert.Throws<TradeAgentException>(() => store.SetSeat(CouncilRoles.Research, "codex", "gpt-5.6-sol", 1, 1, Known, _now()));
        Assert.Throws<TradeAgentException>(() => store.SetSeat(Pos, "nobody", "gpt-5.6-sol", 1, 1, Known, _now()));
        Assert.Throws<TradeAgentException>(() => store.SetSeat(Pos, "codex", "gpt 5", 1, 1, Known, _now()));
        Assert.Throws<TradeAgentException>(() => store.SetSeat(Pos, "codex", "gpt-5.6-sol", 0, 1, Known, _now()));
        Assert.Null(store.Position(Pos)!.SeatRuntime);

        store.SetSeat(Pos, Harness, "gpt-5.6-luna", 300_000, 8_000, Known, _now());
        var row = store.Position(Pos)!;
        Assert.Equal((Harness, "gpt-5.6-luna", "300000", "8000"), (row.SeatRuntime, row.SeatModel, row.SeatAllowIn, row.SeatAllowOut));
        Assert.Equal(new SeatColumns(Harness, "gpt-5.6-luna", Small), SeatResolver.Seated(db, Pos));
        Assert.Equal((OrgEventKind.Seated, OrgDecider.Owner), db.Read(_ =>
        {
            using var c = db.Cmd("SELECT kind, decider FROM org_event WHERE position_id=$id", ("$id", Pos));
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            var found = (r.GetString(0), r.GetString(1));
            Assert.False(r.Read());
            return found;
        }));

        static DateTimeOffset _now() => DateTimeOffset.UtcNow;
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    // ---- (g) each attempt records its allowance and whether its bound is enforced -----------------------------------

    /// <summary>The rung, spelled once: renumbered with the rung in landing order.</summary>
    const int Rung = 44;

    /// <summary>
    /// (g) EACH ATTEMPT RECORDS ITS ALLOWANCE AND WHETHER ITS BOUND IS ENFORCED, AND NOTHING RE-DESCRIBES IT: written in the row's own insert;
    /// an UPDATE that changes any of the three is refused by the schema, while the row's close still writes its usage; the bound is one
    /// of its two words.
    /// </summary>
    [Fact]
    public void Each_attempt_records_its_allowance_and_whether_its_bound_is_enforced()
    {
        var meter = Meter(role => role == CouncilRoles.Research
            ? SeatResolver.Of(role, Harness, "gpt-5.6-luna", Small)
            : SeatResolver.Of(role, "codex", "gpt-5.6-sol", Small));
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
        var rows = Rows(_db).ToDictionary(a => a.Role!);
        Assert.Equal((300_000L, 8_000L, SeatSpec.EnforcedName),
            (rows[CouncilRoles.Research].AllowIn, rows[CouncilRoles.Research].AllowOut, rows[CouncilRoles.Research].TurnBound));
        Assert.Equal((1_200_000L, 20_000L, SeatSpec.ReservationName),
            (rows[CouncilRoles.Operations].AllowIn, rows[CouncilRoles.Operations].AllowOut, rows[CouncilRoles.Operations].TurnBound));

        var id = rows[CouncilRoles.Research].Id;
        foreach (var set in new[] { "allow_in=1", "allow_out=1", "turn_bound='reservation'" })
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db, $"UPDATE ai_attempt SET {set} WHERE id=$id", ("$id", id)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db,
            "INSERT INTO ai_attempt(id, started_at, reserved_cost, state, turn_bound) VALUES('a-word', $at, 0, 'LAUNCHED', 'sometimes')",
            ("$at", Sql.T(_at))));
        Exec(_db, "UPDATE ai_attempt SET exit_code=0 WHERE id=$id", ("$id", id));
        Assert.Equal(SeatSpec.EnforcedName, new AiAttemptStore(_db).Get(id)!.TurnBound);
    }

    /// <summary>
    /// THE RUNG REOPENS AFTER A CRASH BETWEEN ITS STATEMENTS AND ITS STAMP: with the trigger not yet made, and with everything made but
    /// the stamp, the next open runs the rung again to the same schema, the trigger present and a row's allowance as it was.
    /// </summary>
    [Fact]
    public void The_allowance_rung_arrives_at_its_number_and_a_crash_before_its_stamp_runs_it_again()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= Rung, $"the columns arrive at rung {Rung}; this build says {Versions.DatabaseSchemaVersion}");
        var file = Path.Combine(TestEnv.Home, $"seats-rung-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
            new AiAttemptStore(db).Begin(new AiAttempt
            {
                Id = "a-rung", StartedAt = _at, ReservedCost = 1m, AllowIn = 300_000, AllowOut = 8_000, TurnBound = SeatSpec.EnforcedName
            });

        foreach (var dropped in new[] { true, false })
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                raw.Open();
                using var c = raw.CreateCommand();
                c.CommandText = (dropped ? "DROP TRIGGER ai_attempt_bound_fixed; " : "")
                                + $"UPDATE meta SET value='{Rung - 1}' WHERE key='schema_version';";
                c.ExecuteNonQuery();
            }
            using var rerun = new Database(file);
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
                rerun.Read(_ => { using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
            Assert.Equal(1L, rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='ai_attempt_bound_fixed'");
                return (long)c.ExecuteScalar()!;
            }));
            var row = new AiAttemptStore(rerun).Get("a-rung")!;
            Assert.Equal((300_000L, 8_000L, SeatSpec.EnforcedName), (row.AllowIn, row.AllowOut, row.TurnBound));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(rerun, "UPDATE ai_attempt SET allow_in=1 WHERE id='a-rung'"));
        }
    }

    // ---- (h) lowering the allowance lowers no CLI reservation ------------------------------------------------------

    /// <summary>
    /// (h) THE SAFETY PAGE'S ONE PRESS LOWERS NO CLI RESERVATION: on the app's composition, a role and the chair on the vendor's program
    /// reserve the same before and after the allowance is lowered to 300 k / 8 k — the shipped bound is their floor — and a raise still
    /// raises it.
    /// </summary>
    [Fact]
    public void Lowering_the_allowance_lowers_no_cli_reservation()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        host.Gateway.Update(s => s.RoleRuntime[CouncilRoles.Research] = "codex");
        var meter = host.Meter!;
        var (role, chair) = (meter.Reservation(CouncilRoles.Research), meter.Reservation());
        Assert.NotNull(role.Cost);
        Assert.Equal(TurnBound.Reservation, host.SeatFor(CouncilRoles.Research).Bound);

        host.Gateway.SetPolicyUnderTest(p => p with { TurnAllowanceInput = 300_000, TurnAllowanceOutput = 8_000 });
        Assert.Equal(role.Cost, meter.Reservation(CouncilRoles.Research).Cost);
        Assert.Equal(chair.Cost, meter.Reservation().Cost);
        Assert.Equal(Small, host.SeatFor(CouncilRoles.Research).Allowance);

        host.Gateway.SetPolicyUnderTest(p => p with { TurnAllowanceInput = 2_400_000, TurnAllowanceOutput = 40_000 });
        Assert.True(meter.Reservation(CouncilRoles.Research).Cost > role.Cost, "a raised allowance did not raise a CLI reservation");
    }

    // ---- fix pass 1, review P1: usage nobody reported ends the bound's measurement, and the turn with it ----------------

    /// <summary>
    /// A RESPONSE WITHOUT USAGE IS NOT A ZERO (review P1): the harness can no longer hold the turn to its allowance, so no further
    /// request goes — not a second one asking for the whole output again — the turn ends in words naming the missing usage, its usage
    /// is unknown rather than what the other responses reported, and the row is charged its reservation with
    /// <c>usage_unreported</c> naming the request nothing measured.
    /// </summary>
    [Fact]
    public async Task A_response_without_usage_sends_no_further_request_and_the_row_says_it_was_not_measured()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCallWithoutUsage("read_file", new { path = "in/x.md" }))
            .Answer(FakeProvider.Message("done", input: 10, output: 5));
        var meter = Meter(role => SeatResolver.Of(role, Harness, "gpt-5.6-luna", Small));
        using var runtime = HarnessOn(provider, TurnAllowance.Default);
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("go on then");

        Assert.Equal([8_000L], MaxOutputs(provider));
        Assert.Equal("usage-unreported", ended!.Outcome);
        Assert.Null(ended.Usage);
        Assert.Contains(conversation.History, t => t.Role == ChatRole.System
            && t.Text.Contains("reported no usage", StringComparison.Ordinal));
        var row = OnlyRow();
        Assert.Equal((null, null), (row.InputTokens, row.OutputTokens));
        Assert.Equal(Priced(Small), row.Cost);
        using var context = JsonDocument.Parse(row.Context!);
        Assert.Contains("request 1", context.RootElement.GetProperty("usage_unreported").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LAST RESPONSE WITHOUT USAGE IS NOT A ZERO EITHER (review P1): a turn the model ended on a response that reported nothing
    /// completes, but its usage is unknown — never the earlier responses' sum, which would price the unmeasured request at nothing —
    /// so the row is charged its reservation and says which request was not measured.
    /// </summary>
    [Fact]
    public async Task A_last_response_without_usage_leaves_the_turn_unmeasured_never_the_sum_of_the_others()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.ToolCall("read_file", new { path = "in/x.md" }, input: 1_000, output: 100))
            .Answer(FakeProvider.WithoutUsage("done"));
        var meter = Meter(role => SeatResolver.Of(role, Harness, "gpt-5.6-luna", Small));
        using var runtime = HarnessOn(provider, TurnAllowance.Default);
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("go on then");

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(ApiConversation.EndedCompleted, ended!.Outcome);
        Assert.Null(ended.Usage);
        var row = OnlyRow();
        Assert.Equal(Priced(Small), row.Cost);
        using var context = JsonDocument.Parse(row.Context!);
        Assert.Contains("request 2", context.RootElement.GetProperty("usage_unreported").GetString(), StringComparison.Ordinal);
    }

    // ---- fix pass 1, review P2: a turn that sent nothing is charged nothing ------------------------------------------

    /// <summary>
    /// A TURN WHOSE FIRST REQUEST IS WITHHELD FOR ITS SIZE SENT NOTHING, AND IS CHARGED NOTHING (review P2; BUDGET-001): the app's own
    /// record of its own act, set where it happened, as a refusal before any work is — not a turn whose usage never arrived, which
    /// keeps its reservation. Its reservation still reads on the row; its cost is zero.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_first_request_is_withheld_for_size_sends_nothing_and_is_charged_nothing()
    {
        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("done", input: 10, output: 5));
        var tiny = new TurnAllowance(100, 8_000);
        var meter = Meter(role => SeatResolver.Of(role, Harness, "gpt-5.6-luna", tiny));
        using var runtime = HarnessOn(provider, TurnAllowance.Default);
        var conversation = runtime.OpenConversation(CouncilRoles.Research, () => AppCanon.Keep(_home, CouncilRoles.Research), () => new Dictionary<string, string>(), () => null);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;
        using var metering = meter.Attach(conversation, CouncilRoles.Research);

        await conversation.SendAsync("go on then");

        Assert.Empty(provider.Requests);
        Assert.Equal(ApiConversation.EndedOverBudget, ended!.Outcome);
        var row = OnlyRow();
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(Priced(tiny), row.ReservedCost);
        Assert.Equal(0m, row.Cost);
        using var context = JsonDocument.Parse(row.Context!);
        Assert.Contains("could take it past that", context.RootElement.GetProperty("refused").GetString(), StringComparison.Ordinal);
    }
}
