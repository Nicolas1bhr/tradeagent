using System.Reflection;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- items 6 and 7: the board's caller, and its verbs on the pipe --------------------------------------------------------------

    /// <summary>
    /// A LAUNCH THE TEST RECORDED, as the ledger would: <paramref name="role"/>'s row on a seat of <paramref name="seatClass"/> with its
    /// assurance — the <c>OrgAssignmentTests</c> pattern; b2 builds the board seat's real turn. Never a real model.
    /// </summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Harness)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "board",
        RequestId = "board-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(TradeAgent.Core.Json.Write(r.Data)).RootElement;

    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    static IpcRequest NarrowFrame(decimal from = 40m, decimal to = 30m) =>
        Frame(Ops.BoardNarrow, ("intent", Change("plan_day_total", from, to)), ("rationale", "a smaller day while nothing is proven"));

    /// <summary>
    /// ONLY AN ACTIVE BOARD SEAT EXERCISES A BOARD VERB: a dormant seat — even with a launch the ledger recorded — is refused every
    /// board verb, its reads included; a position, a caller with no grant, a launch recorded for another seat and an active seat on an
    /// unconfined launch are refused too, each with nothing written; the active seat on a brokered or confined launch is served. An
    /// organisation verb still refuses a board id. RED before the board's verbs: unknown operation. Watched red with a dormant seat
    /// accepted at the gate.
    /// </summary>
    [Fact]
    public async Task Only_an_active_board_seat_exercises_a_board_verb()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var before = Counts(db);

        var dormant = Launch(db, "board-1");
        foreach (var frame in new[] { NarrowFrame(), Frame(Ops.BoardList), Frame(Ops.BoardShow, ("act", "bact-000000000000")) })
        {
            var refused = await server.CallAsync(frame, "board-1", dormant);
            Assert.False(refused.Ok, $"{frame.Op} was served to a dormant seat");
            Assert.Contains("dormant, not active", refused.Error!.Message, StringComparison.Ordinal);
        }

        Activate(db, "board-1");
        foreach (var (role, attempt, says) in new[]
                 {
                     (CouncilRoles.Research, Launch(db, CouncilRoles.Research), "No position holds the board's verbs"),
                     ((string?)null, (string?)null, "No position holds the board's verbs"),
                     ("board-1", Launch(db, "board-2"), "is no launch the ledger recorded for board-1"),
                     ("board-1", Launch(db, "board-1", SeatClasses.Cli), "assurance")
                 })
        {
            var refused = await server.CallAsync(NarrowFrame(), role, attempt);
            log.WriteLine($"{role ?? "(no grant)"}: {refused.Error?.Message}");
            Assert.False(refused.Ok);
            Assert.Contains(says, refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(before, Counts(db));

        var served = await server.CallAsync(NarrowFrame(), "board-1", Launch(db, "board-1"));
        Assert.True(served.Ok, TradeAgent.Core.Json.Write(served.Error));
        Assert.Equal((BoardActEventKind.Applied, 2), (Body(served).GetProperty("status").GetString(), Body(served).GetProperty("version").GetInt32()));
        Assert.Equal(30m, gw.Policy.PlanDayTotal);
        var confined = await server.CallAsync(NarrowFrame(30m, 25m), "board-1", Launch(db, "board-1", SeatClasses.Cell));
        Assert.True(confined.Ok, TradeAgent.Core.Json.Write(confined.Error));
        var listed = await server.CallAsync(Frame(Ops.BoardList), "board-1", Launch(db, "board-1"));
        Assert.Equal(2, Body(listed).GetProperty("acts").GetArrayLength());

        // AN ORGANISATION VERB STILL REFUSES A BOARD ID.
        var org = await server.CallAsync(Frame(Ops.MandateList), "board-1", Launch(db, "board-1"));
        Assert.False(org.Ok);
    }

    /// <summary>Every op name the pipe knows, read off <see cref="Ops"/>'s own fields.</summary>
    static IReadOnlyList<string> AllOps() =>
        [.. typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Distinct()];

    /// <summary>
    /// A BOARD SEAT WRITES NOTHING BUT BOARD ACTS: an active seat on a brokered launch is refused EVERY op that is not one of the board's
    /// verbs or one of the reads named for it — the ledger, a lens, a material note, every organisation verb, every order — before
    /// anything is read, with nothing written; the reads it may call are each a read, never an order, a write or an organisation verb.
    /// No side channel around the blind read, and no text the organisation reads. RED before the gate: the ledger served board-1.
    /// </summary>
    [Fact]
    public async Task A_board_seat_writes_nothing_but_board_acts()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Activate(db, "board-1");
        string Written() => Scalar(db, "SELECT (SELECT COUNT(*) FROM ledger_entry) || '/' || (SELECT COUNT(*) FROM org_decision) || '/' "
                                       + "|| (SELECT COUNT(*) FROM org_lateral) || '/' || (SELECT COUNT(*) FROM org_mandate) || '/' "
                                       + "|| (SELECT COUNT(*) FROM execution_request) || '/' || (SELECT COUNT(*) FROM material_note)")!;
        var before = Written();

        string[] writes = [Ops.LedgerAdd, Ops.LedgerRevise, Ops.LensPropose, Ops.MaterialNote, Ops.Backtest, Ops.Verdict, Ops.RunTrades, Ops.DeploymentStop];
        Assert.Empty(GatewayPipeServer.BoardSeatReads.Intersect(Ops.Mutating.Concat(Ops.Organisation).Concat(Ops.Board).Concat(writes)));
        Assert.All(GatewayPipeServer.BoardSeatReads, op => Assert.Contains(op, AllOps()));

        var refusedOps = AllOps().Where(op => !Ops.IsBoard(op) && Array.IndexOf(GatewayPipeServer.BoardSeatReads, op) < 0).ToList();
        Assert.Superset(writes.Concat(Ops.Mutating).Concat(Ops.Organisation).ToHashSet(), refusedOps.ToHashSet());
        foreach (var op in refusedOps)
        {
            var refused = await server.CallAsync(Frame(op, ("text", "the proposer's reasons, smuggled")), "board-1", Launch(db, "board-1"));
            Assert.False(refused.Ok, $"{op} was served to a board seat");
            Assert.Contains("writes nothing but its own acts", refused.Error!.Message, StringComparison.Ordinal);
        }
        log.WriteLine($"refused to board-1: {string.Join(", ", refusedOps)}");
        Assert.Equal(before, Written());

        var status = await server.CallAsync(Frame(Ops.Status), "board-1", Launch(db, "board-1"));
        Assert.True(status.Ok, TradeAgent.Core.Json.Write(status.Error));
    }
}

public partial class BoardActTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    /// <summary>
    /// BOARD VERB NAMES ARE DISJOINT FROM PIPE OPS AND ORGANISATION VERBS (GUARD; ORCHESTRATION § 2.9): three families, read off
    /// <see cref="Ops"/>'s own fields — every <c>Board*</c> constant is a <c>board-*</c> name, <see cref="Ops.Board"/> is exactly them, and
    /// none is an organisation verb or any other op of the pipe; every one is in the op table's schema and is `trade board …` in the
    /// CLI. RED before item 7's CLI: no <c>board</c> command.
    /// </summary>
    [Fact]
    public void Board_verb_names_are_disjoint_from_pipe_ops_and_org_verbs()
    {
        var fields = typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).ToList();
        var board = fields.Where(f => f.Name.StartsWith("Board", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var rest = fields.Where(f => !f.Name.StartsWith("Board", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(board.Order(StringComparer.Ordinal), Ops.Board.Order(StringComparer.Ordinal));
        Assert.All(board, op => Assert.StartsWith("board-", op, StringComparison.Ordinal));
        Assert.Empty(board.Intersect(rest));
        Assert.Empty(board.Intersect(Ops.Organisation));
        Assert.DoesNotContain(rest, op => op.StartsWith("board-", StringComparison.Ordinal));

        var cli = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradeAgent.TradeCli", "Program.cs"));
        Assert.Contains("case \"board\":", cli, StringComparison.Ordinal);
        foreach (var f in fields.Where(f => f.Name.StartsWith("Board", StringComparison.Ordinal)))
        {
            Assert.Single(GatewaySchema.Ops(), o => o.Op == (string)f.GetRawConstantValue()!);
            Assert.StartsWith("trade board ", GatewaySchema.Ops().Single(o => o.Op == (string)f.GetRawConstantValue()!).Cli, StringComparison.Ordinal);
            Assert.Contains($"return (Ops.{f.Name}, a);", cli, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// NO BOARD VERB IS MUTATING AND NONE NAMES AN ORDER FIELD (ORDER-001, SIGNAL-001): none is in <see cref="Ops.Mutating"/> or described
    /// mutating, no board verb's name or argument names a side, a price, a stop, a fill, a time in force or a client order id, and no
    /// field an intent may change names one of those — the quantity and notional fields are CEILINGS (<c>max_*</c>), never the size of
    /// an order. Watched red with a <c>price</c> argument given to <c>board-narrow</c>.
    /// </summary>
    [Fact]
    public void No_board_verb_is_mutating_and_none_names_an_order_field()
    {
        string[] order = ["buy", "sell", "side", "price", "limit_price", "stop", "fill", "tif", "time_in_force", "client_order", "order_id", "symbol",
            "schedule", "deadline", "when", "place", "modify", "cancel", "close"];
        Assert.NotEmpty(Ops.Board);
        foreach (var op in Ops.Board)
        {
            Assert.False(Ops.IsMutating(op), $"{op} is mutating");
            var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == op);
            Assert.False(spec.Mutating, $"{op} is described mutating");
            var words = op.Split('-').Concat(spec.Args.SelectMany(a => a.Name.Split('_'))).ToList();
            foreach (var word in order)
                Assert.DoesNotContain(words, w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var field in BoardDirection.Fields.Select(f => f.Path))
        {
            var leaf = field.Split('.')[^1];
            foreach (var word in order)
                Assert.DoesNotContain(word, leaf.Split('_'), StringComparer.OrdinalIgnoreCase);
            if (leaf.Contains("quantity", StringComparison.Ordinal) || leaf.Contains("notional", StringComparison.Ordinal))
                Assert.StartsWith("max_", leaf, StringComparison.Ordinal);
        }
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// THE UNIT'S OBSERVABLE RESULT, END TO END: a launch the ledger recorded for an active board seat sends <c>board-narrow</c> over the
    /// pipe, and the gateway refuses the next order above the new limit — one seat, a new version decided by the board, in force at once;
    /// an order that passed the moment before is refused the moment after.
    /// </summary>
    [Fact]
    public async Task A_narrowing_sent_over_the_pipe_refuses_the_next_order_above_it()
    {
        using var db = TestEnv.NewDb();
        SeedVersion(db, Tradable);
        Activate(db, "board-1");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure());
        await gw.PlaceAsync(new AgentContext("a"), "pipe-before", TestEnv.Buy("ES", 3m));

        var reply = await server.CallAsync(Frame(Ops.BoardNarrow, ("intent", Change("risk.max_order_quantity", 10, 2)),
            ("rationale", "three at once is more than this book should carry")), "board-1", Launch(db, "board-1"));
        Assert.True(reply.Ok, TradeAgent.Core.Json.Write(reply.Error));
        Assert.Equal(BoardActEventKind.Applied, Body(reply).GetProperty("status").GetString());

        var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.PlaceAsync(new AgentContext("a"), "pipe-after", TestEnv.Buy("ES", 3m)));
        Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, refused.Code);
        var inForce = new BoardReader(db).InForce();
        Assert.Equal((BoardDecider.Board, BoardBasis.Act, 2m), (inForce.Decider, inForce.Basis, inForce.Policy.Risk.MaxOrderQuantity));
    }
}
