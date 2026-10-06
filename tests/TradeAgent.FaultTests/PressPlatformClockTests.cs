using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE OWNER'S EMERGENCY PRESSES RUN ON THE PLATFORM'S CLOCK, NOT ON THEIR OWN DISK'S
/// (<c>U-fix-press-budget</c>).
///
/// <para><b>The sighting.</b> windows-latest, run 37391256380 (branch <c>r-containment</c>, no src or
/// tests change): <c>PressIdShapeTests.The_operator_cancel_all_names_its_legs_without_the_brokers_
/// order_id</c> failed "Expected: 2 / Actual: 0" after 46 s — two working orders, and the owner's
/// Cancel all working orders press wrote no cancel leg. The same test took 1.25 s on main's green
/// windows run of the same src (37388890179), and every test running beside it in that minute was
/// 3–37× slower than there too: the whole runner had stalled.</para>
///
/// <para><b>The cause, measured on windows-latest</b> (three branch-only diagnostic runs, 37413619642,
/// 37413622132 and 37413624368: the sighting's fixture and its close-all twin, 270 presses of each,
/// every platform call and every SQLite statement stamped with the budget left). The press opens its
/// two-second deadline and then, before its first platform call, writes its own write-ahead records —
/// the press row, the pause's health event, the DISPATCHING transition: three durable commits at
/// <c>synchronous=FULL</c> — and its budget moved at those commits and nowhere else (26 + 18 + 18 ms →
/// 1937 ms left at the read, for one). Over the 270, a press's wall time minus its commits' was −2 to
/// 26 ms. A stall puts all of it in the store: one close-all press there spent 2688 ms of its 2689 in
/// nine commits, and its next platform call was refused before the wire at −31 ms; one bare one-row
/// commit on the same image took 5207 ms. The control — the deadline gone at the first platform call —
/// reproduced the sighting's message byte for byte 78 times out of 78: the read is refused "the
/// operation deadline had already passed and nothing was sent", the press records that it could not
/// read the orders, and nothing is cancelled.</para>
///
/// <para><b>Why it is the product's to fix, not the fixture's.</b> Press fixtures whose verdict is the
/// book have been given a longer budget for this same reason since <c>U-press-settle-win</c>
/// (<c>Unresolved.PressBudget</c>, <c>PressBudgetFor</c>). This product runs on
/// the owner's Windows PC, and a slow disk there is not a test artefact: a press that refuses
/// itself because TradeAgent's own bookkeeping was slow leaves the orders working, or the book open,
/// that the owner pressed the button to remove — and it then refuses the next press until the owner
/// has resolved this one on the card. The orchestrator's ruling on the owner's behalf (2026-10-06):
/// the owner's presses are charged for the platform's time only, exactly as the loss budget's own
/// flatten has been since <c>U-fix-loss-reopen</c> (<c>RiskReducingScope.BeginExcludingTheStore</c>).
/// The two seconds are unchanged, and so is the bound on a stalled PLATFORM —
/// <c>OperatorPressIsAnEmergencyTests</c> guards it.</para>
///
/// <para><b>How the disk is made slow here</b> — the way <c>LossFlattenOwedTests</c> makes it slow,
/// with no hook in the product: a second connection holds the database's write lock for a second
/// longer than the whole budget, from just before the press, so the press's own first commit waits it
/// out. Nothing waits on the platform: every simulator call answers at once. The budget is the
/// simulator's shipped two seconds, asserted rather than assumed.</para>
/// </summary>
public class PressPlatformClockTests(ITestOutputHelper log)
{
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Hold = Budget + TimeSpan.FromSeconds(1);

    /// <summary>
    /// A second writer takes the database's write lock and keeps it for <see cref="Hold"/>; the task
    /// it returns completes once the lock is held. WAL, so the press can still READ while it waits;
    /// what waits is its first commit.
    /// </summary>
    static (Task Holder, Task Locked) HoldTheStore(string file, Stopwatch heldFor)
    {
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(async () =>
        {
            using var other = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file, Pooling = false
            }.ToString());
            other.Open();
            using (var begin = other.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE;"; begin.ExecuteNonQuery(); }
            heldFor.Restart();
            locked.SetResult();
            await Task.Delay(Hold);
            using (var commit = other.CreateCommand()) { commit.CommandText = "COMMIT;"; commit.ExecuteNonQuery(); }
            heldFor.Stop();
        });
        return (holder, locked.Task);
    }

    /// <summary>
    /// THE SIGHTING'S OWN PRESS ON A DISK SLOWER THAN ITS WHOLE BUDGET: BOTH ORDERS ARE STILL
    /// CANCELLED.
    ///
    /// <para>The fixture is the sighting's — <c>TestEnv.Ready</c>, two resting limit orders, the
    /// simulator's two seconds. Before: the press row's commit waited out the second writer, the
    /// deadline passed inside it, the orders read was refused before the wire, and the press wrote
    /// no leg — "Expected: 2 / Actual: 0", the CI red's own message. After: the time the press spent
    /// in its own store is not the platform's and is not charged to the platform's budget, so the
    /// read, both legs and both cancels go out.</para>
    /// </summary>
    [Fact]
    public async Task A_cancel_all_whose_own_records_are_slower_to_write_than_its_whole_budget_still_cancels_every_order()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        await using var _2 = gw;
        Assert.Equal(Budget, conn.EmergencyBudget);

        await gw.PlaceAsync(new AgentContext("a"), "slow-store-a",
            new PlaceIntent("ES", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        await gw.PlaceAsync(new AgentContext("a"), "slow-store-b",
            new PlaceIntent("NQ", OrderSide.Buy, OrderType.Limit, 1m, 1m, null, TimeInForce.Day, null));
        Assert.Equal(2, conn.Broker.Orders.Count(o => o.State == ExecutionState.WORKING));

        var heldFor = new Stopwatch();
        var (holder, locked) = HoldTheStore(db.Connection.DataSource, heldFor);
        await locked;

        var pressed = Stopwatch.StartNew();
        var outcome = await gw.OperatorCancelAllAsync();
        pressed.Stop();
        await holder;

        var row = Assert.Single(gw.Requests.Query("request_id LIKE 'op-cancel-%' AND intent='CANCEL_ALL'"));
        var legs = gw.Requests.Query("request_id LIKE 'op-cancel-%' AND intent='CANCEL'");
        log.WriteLine($"store held by another : {heldFor.ElapsedMilliseconds} ms against a {Budget.TotalMilliseconds:0} ms budget");
        log.WriteLine($"the press             : {pressed.ElapsedMilliseconds} ms");
        log.WriteLine($"press row             : {row.State} '{row.LastError}'");
        log.WriteLine($"legs                  : {legs.Count} [{string.Join("; ", legs.Select(l => $"{l.RequestId} {l.State}"))}]");
        log.WriteLine($"the book              : [{string.Join("; ", conn.Broker.Orders.Select(o => $"{o.ConnectorOrderId} {o.Symbol} {o.State}"))}]");
        log.WriteLine($"outcome               : {outcome.Summary}");

        // THE STALL WAS REAL AND THE PRESS WAS INSIDE IT: longer than the whole budget, both.
        Assert.True(heldFor.Elapsed >= Budget, $"the store was held {heldFor.ElapsedMilliseconds} ms");
        Assert.True(pressed.Elapsed >= Budget, $"the press took {pressed.ElapsedMilliseconds} ms");

        // AND THE PRESS STILL DID WHAT IT WAS PRESSED FOR: a leg per order, each cancelled, and
        // nothing left working at the platform.
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal(ExecutionState.CANCELLED, l.State));
        Assert.All(conn.Broker.Orders, o => Assert.Equal(ExecutionState.CANCELLED, o.State));
    }

    /// <summary>
    /// THE SAME DISK UNDER THE OTHER BUTTON: CLOSE ALL POSITIONS STILL LEAVES THE BOOK FLAT.
    ///
    /// <para>Close-all reads the positions first and writes afterwards, so the stall lands where its
    /// first commit is — the composite, between the capture and the close. Before: the composite's
    /// commit waited out the second writer, the re-read of the position immediately before the close
    /// was refused before the wire, the leg was recorded not confirmed and the position stayed open.
    /// After: the close goes out and the book is flat.</para>
    /// </summary>
    [Fact]
    public async Task A_close_all_whose_own_records_are_slower_to_write_than_its_whole_budget_still_leaves_the_book_flat()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var _2 = gw;
        Assert.Equal(Budget, conn.EmergencyBudget);

        await gw.PlaceAsync(new AgentContext("a"), "slow-store-open", TestEnv.Buy("ES", 2m));
        Assert.Equal(2m, Assert.Single(conn.Broker.Positions).Quantity);

        var heldFor = new Stopwatch();
        var (holder, locked) = HoldTheStore(db.Connection.DataSource, heldFor);
        await locked;

        var pressed = Stopwatch.StartNew();
        var outcome = await gw.OperatorCloseAllAsync();
        pressed.Stop();
        await holder;

        var rows = gw.Requests.Query("request_id LIKE 'op-close-%' AND intent='PLACE'");
        log.WriteLine($"store held by another : {heldFor.ElapsedMilliseconds} ms against a {Budget.TotalMilliseconds:0} ms budget");
        log.WriteLine($"the press             : {pressed.ElapsedMilliseconds} ms");
        log.WriteLine($"rows                  : [{string.Join("; ", rows.Select(r => $"{r.RequestId} {r.State} '{r.LastError}'"))}]");
        log.WriteLine($"positions             : [{string.Join("; ", conn.Broker.Positions.Select(p => $"{p.Symbol} {p.Quantity}"))}]");
        log.WriteLine($"outcome               : {outcome.Summary}");

        Assert.True(heldFor.Elapsed >= Budget, $"the store was held {heldFor.ElapsedMilliseconds} ms");
        Assert.True(pressed.Elapsed >= Budget, $"the press took {pressed.ElapsedMilliseconds} ms");

        // THE CLOSE WENT OUT, IT FILLED, AND NOTHING IS LEFT OPEN.
        var leg = Assert.Single(rows);
        Assert.Equal(ExecutionState.FILLED, leg.State);
        Assert.Empty(conn.Broker.Positions);
    }
}
