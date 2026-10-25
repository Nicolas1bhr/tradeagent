using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A BOARD SEAT'S TURN (<c>U-board-b2</c>; R24 § 4.1 "Its cost" and "Its seats are real-model seats under the activation gate"): its
/// standing and its recorded class at admission, the board's own budget, its home, runtime and grant, and the wake and blind packet a
/// proposal raises. NOTHING HERE REACHES A MODEL: a board seat is made active only by the test host's seam
/// (<c>BoardStore.SetSeatStateUnderTest</c>), and a launch's seat is a test's conversation or a puppet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class BoardTurnTests(ITestOutputHelper log)
{
    /// <summary>A meter over <paramref name="db"/> whose seats run <paramref name="runtime"/>, every seat declared as the test says.</summary>
    static TurnMeter Meter(Database db, Func<string, string?> declared, string runtime = RuntimeManifest.PuppetId,
        Func<decimal>? cap = null, Func<decimal>? planCap = null) =>
        new(db, cap ?? (() => 1000m), runtimeId: () => runtime, live: new LiveAttempts(), declaredSeat: declared, planCap: planCap,
            owner: () => new OwnerPrice(2m, 8m));

    static void Activate(Database db, params string[] seats)
    {
        var store = new BoardStore(db);
        foreach (var seat in seats) store.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
    }

    static void SetState(Database db, string seat, string state) =>
        new BoardStore(db).SetSeatStateUnderTest(seat, state, DateTimeOffset.UtcNow);

    static long Attempts(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM ai_attempt");
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    static string Wake(MissionEventStore wakes, string role, string kind = MissionEventKind.Self)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var id = $"{kind}:{role}:{Guid.NewGuid():n}";
        Assert.True(wakes.Raise(id, kind, at, role: role));
        return id;
    }

    // ---- item 1: a board seat's standing at admission ----------------------------------------------------------------------------

    /// <summary>
    /// A DORMANT BOARD SEAT IS NEVER LAUNCHED: board-1 on its own recorded class, on a puppet that needs no proven cell, is refused at
    /// admission while its state now is dormant (the founding state), retired, or names no seat of the board — a refusal about the
    /// seat (<see cref="RefusalKind.Position"/>), with no row written and its wake not consumed. Made active, the same launch is
    /// admitted and records its seat. RED before the unit: a board id was refused as no active position whatever its state. Watched
    /// red with the meter admitting a board id without reading its state.
    /// </summary>
    [Fact]
    public void A_dormant_board_seat_is_never_launched()
    {
        using var db = TestEnv.NewDb();
        var wakes = new MissionEventStore(db);
        var meter = Meter(db, _ => SeatClasses.Broker);
        using var seat = meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), "board-1");
        using var ghost = meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), "board-9");

        var wake = Wake(wakes, "board-1");
        var dormant = meter.Begin("## Board", consuming: [wake], role: "board-1");
        Assert.False(dormant.Admitted, "a dormant board seat was launched");
        Assert.Equal(RefusalKind.Position, dormant.Kind);
        Assert.Contains("board seat board-1 is dormant, not active", dormant.Refusal, StringComparison.Ordinal);

        var none = meter.Begin("## Board", role: "board-9");
        Assert.False(none.Admitted, "a board id with no seat was launched");
        Assert.Equal(RefusalKind.Position, none.Kind);
        Assert.Contains("'board-9' is no seat of the security board", none.Refusal, StringComparison.Ordinal);
        log.WriteLine(dormant.Refusal);
        Assert.Equal(0, Attempts(db));
        Assert.False(wakes.Get(wake)!.Consumed);

        Activate(db, "board-1");
        var admitted = meter.Begin("## Board", consuming: [wake], role: "board-1");
        Assert.True(admitted.Admitted, admitted.Refusal);
        var row = new AiAttemptStore(db).Get(admitted.Id!)!;
        Assert.Equal(("board-1", SeatClasses.Broker), (row.Role, row.SeatClass));
        Assert.Equal(admitted.Id, wakes.Get(wake)!.ConsumedBy);
        meter.Release("board-1", admitted.Id!);

        SetState(db, "board-1", BoardSeatStatus.Retired);
        var retired = meter.Begin("## Board", role: "board-1");
        Assert.False(retired.Admitted, "a retired board seat was launched");
        Assert.Equal(RefusalKind.Position, retired.Kind);
        Assert.Contains("is retired, not active", retired.Refusal, StringComparison.Ordinal);
        Assert.Equal(1, Attempts(db));
    }

    /// <summary>
    /// A BOARD SEAT LAUNCHES ONLY ON ITS RECORDED CLASS: board-1 is founded on a broker seat, board-2 on the harness. On a puppet, a
    /// seat about to start on another class than the recorded one is refused about the launch (its wakes kept), with no row; on its
    /// own class it is admitted and the row records that class. A runtime that is not a puppet runs a real model and is refused
    /// until this host records a proven cell — then admitted. RED before the unit: every board id was refused as no position.
    /// </summary>
    [Fact]
    public void A_board_seat_launches_only_on_its_recorded_class()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        Assert.Equal((SeatClasses.Broker, SeatClasses.Harness),
            (new BoardStore(db).Seat("board-1")!.SeatClass, new BoardStore(db).Seat("board-2")!.SeatClass));

        // ANOTHER CLASS THAN THE RECORDED ONE, declared and verified alike: refused about the launch, nothing written.
        var meter = Meter(db, _ => SeatClasses.Harness);
        using (meter.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Harness), "board-1"))
        {
            var wrong = meter.Begin("## Board", role: "board-1");
            Assert.False(wrong.Admitted, "board-1 launched on a harness seat it was not founded on");
            Assert.Equal(RefusalKind.HostOrLaunch, wrong.Kind);
            Assert.Contains("recorded on a broker seat and the seat about to start is a harness seat", wrong.Refusal, StringComparison.Ordinal);
        }
        // A CLI SEAT IS NEVER A BOARD SEAT'S, whatever the app declared.
        var cli = Meter(db, _ => SeatClasses.Broker);
        using (cli.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Cli), "board-1"))
        {
            var shell = cli.Begin("## Board", role: "board-1");
            Assert.False(shell.Admitted, "board-1 launched on a cli seat");
            Assert.Equal(RefusalKind.HostOrLaunch, shell.Kind);
        }
        Assert.Equal(0, Attempts(db));

        // ITS OWN CLASS, ON A PUPPET: admitted, each recording the class it was founded on.
        foreach (var (id, recorded) in new[] { ("board-1", SeatClasses.Broker), ("board-2", SeatClasses.Harness) })
        {
            var own = Meter(db, _ => recorded);
            using var attached = own.Attach(new ThinkingSeatsTests.Seated(recorded), id);
            var admitted = own.Begin("## Board", role: id);
            Assert.True(admitted.Admitted, admitted.Refusal);
            Assert.Equal(recorded, new AiAttemptStore(db).Get(admitted.Id!)!.SeatClass);
            own.Release(id, admitted.Id!);
        }

        // A REAL RUNTIME RUNS A MODEL: refused until a proven cell is recorded on this host, then admitted.
        var real = Meter(db, _ => SeatClasses.Broker, runtime: "codex");
        using (real.Attach(new ThinkingSeatsTests.Seated(SeatClasses.Broker), "board-1"))
        {
            var unproven = real.Begin("## Board", role: "board-1");
            Assert.False(unproven.Admitted, "board-1 ran a real model with no proven cell recorded");
            Assert.Equal(RefusalKind.HostOrLaunch, unproven.Kind);
            Assert.Contains("no proven executor cell recorded", unproven.Refusal, StringComparison.Ordinal);
            Assert.Equal(2, Attempts(db));

            ThinkingSeatsTests.ProveCell(db);
            var proven = real.Begin("## Board", role: "board-1");
            Assert.True(proven.Admitted, proven.Refusal);
        }
    }
}
