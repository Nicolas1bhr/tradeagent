using System.Diagnostics;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>FaultProfile.Hold</c> (U-closeall-latch): an uninterruptible call whose end the TEST decides.
/// The seam is the simulator's own, null in the product, and a test that parks a wave on it is only
/// worth reading if the seam is pinned: null leaves the old delay path exactly as it was, and set
/// parks the call until the test releases it — whatever the cancellation token says, at the read
/// path (<c>Wire</c>) and at the placement path alike, the two places the delay is paid.
/// </summary>
public class FakeConnectorHoldTests
{
    static PlaceOrderCommand Place(FakeConnector conn) =>
        new("hold-1", conn.Broker.AccountId, "ES", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null);

    [Fact]
    public async Task With_no_hold_the_uncancellable_delay_is_paid_as_before()
    {
        var conn = new FakeConnector(new FakeBroker());
        conn.Faults.UncancellableLatencyMs = 300;

        var read = Stopwatch.StartNew();
        await conn.GetPositionsAsync(conn.Broker.AccountId).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(read.ElapsedMilliseconds >= 250, $"the read took {read.ElapsedMilliseconds} ms; the delay is 300");

        var place = Stopwatch.StartNew();
        await conn.PlaceOrderAsync(Place(conn)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(place.ElapsedMilliseconds >= 250, $"the placement took {place.ElapsedMilliseconds} ms; the delay is 300");

        // And with neither a delay nor a hold, nothing waits at all.
        conn.Faults.UncancellableLatencyMs = 0;
        await conn.GetPositionsAsync(conn.Broker.AccountId).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_held_read_parks_until_released_and_a_cancelled_token_does_not_release_it()
    {
        var conn = new FakeConnector(new FakeBroker());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Faults.Hold = () => { entered.TrySetResult(); return release.Task; };
        // The delay is NOT summed with the hold: a minute of it would outlast the test.
        conn.Faults.UncancellableLatencyMs = 60_000;

        using var cts = new CancellationTokenSource();
        var call = conn.GetPositionsAsync(conn.Broker.AccountId, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await cts.CancelAsync();
        await Task.Delay(300);
        Assert.False(call.IsCompleted, "a cancelled token released a call that was meant to ignore it");

        release.SetResult();
        await call.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_held_placement_parks_until_released_and_a_cancelled_token_does_not_release_it()
    {
        var conn = new FakeConnector(new FakeBroker());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Faults.Hold = () => { entered.TrySetResult(); return release.Task; };

        using var cts = new CancellationTokenSource();
        var call = conn.PlaceOrderAsync(Place(conn), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var before = conn.Broker.Orders.Count;

        await cts.CancelAsync();
        await Task.Delay(300);
        Assert.False(call.IsCompleted, "a cancelled token released a placement that was meant to ignore it");
        Assert.Equal(before, conn.Broker.Orders.Count);

        release.SetResult();
        var order = await call.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(order);
        Assert.True(conn.Broker.Orders.Count > before, "the released placement never reached the broker");
    }
}
