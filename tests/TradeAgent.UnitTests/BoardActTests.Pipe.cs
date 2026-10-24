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
