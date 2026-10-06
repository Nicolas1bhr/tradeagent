using System.Globalization;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A RUN IS ENDED ONCE (<c>U-close-once</c>): two END requests for one paper run at the same moment put one close on
/// the wire, and the second writes nothing.
///
/// <para>Same harness as the rest of this class — the owner's grant, the app's own allocation and deployment, a filled
/// long the owner placed by hand, every order through <see cref="TradingGateway.PlaceAsync"/> over
/// <see cref="RecordingConnector"/> and the built-in simulator. No venue is reached and no real money is involved.</para>
/// </summary>
public partial class PaperDeploymentTests
{
    /// <summary>
    /// (b) TWO ENDS AT ONCE SEND ONE CLOSE, AND THE SECOND WRITES NOTHING.
    ///
    /// <para>A is the owner's Stop, held INSIDE the connector's place call — its close on the wire, its row
    /// <c>DISPATCHING</c>. B is the agent's <c>deployment-stop</c>, let go at the same moment, and it gets as far as it
    /// can: on the base to the quote, its last call before the dispatch gate A is holding; with the in-flight rule
    /// alone, to its own refused flatten; ended once, to the run's gate, where it says so and waits. Then A is let go.
    /// Two venues: one where A's market close RESTS — paper until its next bar, ATAS until its fill report — and one
    /// where it FILLS at once.</para>
    ///
    /// <para><b>RED on the base</b> (seat A's probe at 2952c285, the same arms): rests — two flattens, two closes at the
    /// wire, the account -1 once they fill; fills — B's flatten refused <c>POSITION_MOVED</c> and the run read "NOT
    /// closed" over a flat book. <b>Mutant (ii)</b>, the gate removed with the in-flight rule kept: B's flatten is
    /// refused <c>CLOSE_IN_FLIGHT</c> and becomes the run's latest word — two flattens, "NOT closed", a close owed
    /// over A's.</para>
    /// </summary>
    [Theory]
    [InlineData("rests")]
    [InlineData("fills")]
    public async Task Two_ENDs_at_once_send_one_close_and_the_second_writes_nothing(string venue)
    {
        var (gw, conn, db, _) = await Ready();
        using var _1 = db;

        await Allocated(gw, db);
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var deployment = gw.Deployments.Open().Single();
        await SeedAPosition(gw, conn);
        var placesBefore = conn.Places;
        conn.Faults.Fill = venue == "fills" ? FillBehaviour.FillImmediately : FillBehaviour.LeaveWorking;

        // A — the owner's Stop, held inside the connector's place call.
        var releaseA = new TaskCompletionSource();
        conn.Holds = RecordingConnector.HeldCall.Place;
        conn.Hold = releaseA.Task;
        var a = gw.EndPaperDeploymentAsync(deployment.Id, "test A: the owner pressed Stop");
        await conn.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var aFlatten = Assert.Single(Flattens(gw, deployment.Id));
        var aRowHeld = gw.GetRequest(aFlatten.RequestId)?.State.ToString() ?? "no row";

        // B — the agent's deployment-stop, at the same moment, as far as it gets while A is held.
        var bAtQuote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Seam = kind =>
        {
            if (kind == RecordingConnector.HeldCall.Quote) bAtQuote.TrySetResult();
            return Task.CompletedTask;
        };
        using var looking = new CancellationTokenSource();
        var b = Task.Run(() => gw.EndPaperDeploymentAsync(deployment.Id, "test B: the agent's deployment-stop"));
        var bWaits = Logged(db, "deployment_end_waits", looking.Token);
        var arrived = await Task.WhenAny(bAtQuote.Task, b, bWaits).WaitAsync(TimeSpan.FromSeconds(30));
        await arrived;
        await looking.CancelAsync();
        var bGot = arrived == bAtQuote.Task ? "to the quote, behind A at the dispatch gate"
            : arrived == b ? "to its end" : "to the run's gate, waiting on A";
        var flattensWhileAHeld = Flattens(gw, deployment.Id).Count;

        // A let go: its close reaches the simulator, and B goes on from wherever it was.
        releaseA.SetResult();
        await a.WaitAsync(TimeSpan.FromSeconds(30));
        var bAnswer = await b.WaitAsync(TimeSpan.FromSeconds(30));
        conn.Seam = null;

        var flattens = Flattens(gw, deployment.Id);
        var atTheWire = conn.Places - placesBefore;
        var reading = gw.DeploymentReadings().Single(d => d.Id == deployment.Id);
        var heldBeforeFills = Held(conn);

        // Price arrives: whatever still rests at the simulator fills, as a real book's would.
        foreach (var o in conn.Broker.Orders.Where(o => o.State == ExecutionState.WORKING).ToList())
            conn.Broker.FillWorking(o.ConnectorOrderId);

        log.WriteLine($"venue                : {venue}");
        log.WriteLine($"A held at the wire   : its row {aRowHeld}");
        log.WriteLine($"B got                : {bGot}; flattens while A was held {flattensWhileAHeld}");
        foreach (var f in flattens)
            log.WriteLine($"flatten              : {f.RequestId} {f.State} — {f.Answer ?? "-"} "
                          + $"(row {gw.GetRequest(f.RequestId)?.State.ToString() ?? "none"})");
        log.WriteLine($"orders at the wire   : {atTheWire}");
        log.WriteLine($"B's answer           : {bAnswer?.State ?? "none"} — {bAnswer?.EndReason ?? "-"}");
        log.WriteLine($"line                 : {reading.Line}");
        log.WriteLine($"close owed           : {reading.CloseOwed ?? "not owed"}");
        log.WriteLine($"position             : {heldBeforeFills} before the resting orders fill, {Held(conn)} after");

        // ONE FLATTEN — A's — AND ONE ORDER AT THE WIRE; THE ACCOUNT FLAT ONCE IT FILLS.
        Assert.Equal(aFlatten.RequestId, Assert.Single(flattens).RequestId);
        Assert.Equal(1, atTheWire);
        Assert.Equal(0m, Held(conn));

        // NOTHING OWED AND NOTHING SAID TO BE: the run ended, by A, and B answered with it as A left it.
        Assert.Null(reading.CloseOwed);
        Assert.DoesNotContain("NOT closed", reading.Line, StringComparison.Ordinal);
        Assert.NotNull(bAnswer);
        Assert.True(bAnswer!.IsEnded);
        Assert.Equal("test A: the owner pressed Stop", bAnswer.EndReason);
        await gw.DisposeAsync();
    }

    /// <summary>Completes once the gateway has written <paramref name="event"/> to the engineering log.</summary>
    static async Task Logged(Database db, string @event, CancellationToken ct)
    {
        while (true)
        {
            var seen = db.Read(_ =>
            {
                using var c = db.Cmd("SELECT COUNT(*) FROM engineering_log WHERE event=$e", ("$e", @event));
                return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
            if (seen > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }
}
