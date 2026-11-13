using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE MATERIAL PASS RUNS BESIDE THE APP'S BACKGROUND LOOP, NOT IN IT (<c>U-perf-scan</c>, R28 G2). The loop that runs the trading pass —
/// the reconciler, the paper deployments, the forward runs — also walked the workspace inline every thirty seconds, so a disk that was slow
/// or stopped answering held protection behind a record-keeping pass. Now the pass runs on a task of its own, one at a time.
/// </summary>
public class MaterialPassOffLoopTests
{
    [Fact]
    public async Task A_walk_the_disk_holds_does_not_hold_the_trading_pass_and_no_second_walk_starts_beside_it()
    {
        var inbox = Path.GetFullPath(Path.Combine(Paths.Workspace, MaterialScanner.InboxDir));
        using var disk = new ManualResetEventSlim();
        var listings = 0;
        // THE DISK HOLDS THE INBOX'S LISTING: the shape of a share that stopped answering.
        var reads = new WorkspaceReads(
            path => Path.GetFullPath(path) == inbox,
            path =>
            {
                if (Path.GetFullPath(path) == inbox) { Interlocked.Increment(ref listings); disk.Wait(); }
                return ([], []);
            },
            _ => null);

        var connector = new FakeConnector(new FakeBroker());
        await connector.ConnectAsync();
        var host = AppHost.Composed(TestEnv.NewDb(), connector, new AgentPresence(), reads);
        var passes = 0;
        host.BeforeBackgroundPassUnderTest = () => Interlocked.Increment(ref passes);
        using var stop = new CancellationTokenSource();
        Task? loop = null;
        try
        {
            // ON A WORKER: a loop that walks inline blocks whoever called it, and this test must be the one left to say so.
            loop = Task.Run(() => host.BackgroundAsync(stop.Token));
            await Until(() => Volatile.Read(ref listings) >= 1, "the first tick's material pass reached the disk");
            await Until(() => Volatile.Read(ref passes) >= 2, "the loop made its next pass while the walk was held");
            Assert.Equal(1, Volatile.Read(ref listings));
            // A DUE TICK WHILE THE WALK IS HELD STARTS NOTHING BESIDE IT: it is handed the pass in flight.
            var inFlight = host.MaterialPass(1, stop.Token);
            Assert.False(inFlight.IsCompleted);
            Assert.Same(inFlight, host.MaterialPass(6, stop.Token));
        }
        finally
        {
            disk.Set();
            await stop.CancelAsync();
            if (loop is not null) await loop;
            await host.MaterialPass(0, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            await host.Mission.PauseAsync();
            await host.Agent.StopAsync();
            await host.Gateway.DisposeAsync();
            host.Db.Dispose();
        }
    }

    static async Task Until(Func<bool> condition, string waitingFor)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, waitingFor);
            await Task.Delay(20);
        }
    }
}
