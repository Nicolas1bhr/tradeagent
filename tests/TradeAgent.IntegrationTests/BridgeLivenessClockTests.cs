using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE BRIDGE'S LIVENESS IS MEASURED ON THE MONOTONIC CLOCK, SO A WALL CLOCK STEPPED AN HOUR EITHER
/// WAY DECIDES NOTHING (<c>U-bridge-liveness-clock</c>).
///
/// The heartbeat stamp, the arrival stamp, the drop of a quiet peer, the health verdict and the auth
/// grace all used to read <c>DateTimeOffset.UtcNow</c>, while the connector's own write and answer
/// deadlines read <c>Environment.TickCount64</c>. A wall clock is a reading somebody can set — an NTP
/// correction, a clock put back by hand — and a step backwards made <c>UtcNow − lastHeard</c> small or
/// negative: a bridge that had gone silent stayed READY and was never dropped. That is the DANGEROUS
/// direction, because the gateway goes on treating a peer that answers nothing as healthy. A step
/// forward dropped a live one: the safe direction, and still a defect. Each test below was red on that
/// code, read through this same seam's wall half.
///
/// EVERYTHING HERE GOES THROUGH <see cref="AtasConnector.LivenessClock"/>, and no verdict waits on the
/// runner's speed. <see cref="SteppedClock"/> moves only when a test moves it, so a timeout passes at
/// the millisecond the test chooses; the real pipe, the real handshake and the real read loop are the
/// only things left running on their own, and where one of them could race a reading the test holds it.
/// </summary>
public class BridgeLivenessClockTests
{
    static string NewPipe() => "ta-blc-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The bridge's own heartbeat interval (<c>BridgeServer.HeartbeatInterval</c>), a third of the timeout.</summary>
    static readonly TimeSpan Pulse = TimeSpan.FromSeconds(5);

    const string Connecting = "connecting — waiting for the add-on to authenticate";

    /// <summary>
    /// A MACHINE'S TWO CLOCKS, AND THE ONE DIFFERENCE BETWEEN THEM THIS CLASS IS ABOUT.
    /// <see cref="Advance"/> is time passing: both halves move together, as on a machine nobody has
    /// touched. <see cref="StepWall"/> is the wall half moving on its own — what an NTP correction or a
    /// person setting the clock does, and what a monotonic counter never does. Neither half moves by
    /// itself, so a deadline here passes when the test says so and not when the runner gets round to it.
    /// Milliseconds, like the <c>TickCount64</c> the connector reads in production.
    /// </summary>
    sealed class SteppedClock : TimeProvider
    {
        static readonly DateTimeOffset Origin = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);
        const long Boot = 5_000_000;
        long _monotonic = Boot;
        long _wallStep;

        public override long GetTimestamp() => Interlocked.Read(ref _monotonic);

        public override long TimestampFrequency => 1000;

        public override DateTimeOffset GetUtcNow() =>
            Origin.AddMilliseconds(Interlocked.Read(ref _monotonic) - Boot + Interlocked.Read(ref _wallStep));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _monotonic, (long)by.TotalMilliseconds);

        public void StepWall(TimeSpan by) => Interlocked.Add(ref _wallStep, (long)by.TotalMilliseconds);
    }

    /// <summary>
    /// (a) A BRIDGE THAT GOES SILENT WHILE THE WALL CLOCK IS PUT BACK AN HOUR IS DEGRADED AND DROPPED
    /// ONCE THE MONOTONIC TIMEOUT PASSES — and not a millisecond before it.
    ///
    /// The bridge says hello and then nothing at all. The read loop is HELD inside the READY that hello
    /// raises: by then its heartbeat stamp is taken, and that turn's question — has this peer gone
    /// quiet? — has not been asked yet. So nothing can drop the peer while the clocks are moved and the
    /// health is read, and the reading cannot depend on whether an idle poll happened to fire in
    /// between. Released, the loop asks the question on that same turn, which is where the drop is
    /// decided. With the wall clock deciding, an hour behind, "fifteen seconds since the last beat" read
    /// as minus fifty-nine minutes and forty-five seconds: READY, and the loop never let go.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_goes_silent_while_the_wall_clock_steps_back_an_hour_is_degraded_and_dropped_on_the_monotonic_timeout()
    {
        var clock = new SteppedClock();
        using var held = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { LivenessClock = clock };
        connector.ConnectionChanged += state =>
        {
            if (state != HealthState.READY) return;
            held.Release();
            release.Wait(TestTime.MarginSeconds(30));   // bounded: a failed test must not keep the loop for ever
        };
        await connector.ConnectAsync();

        await using var silent = new StubBridge(pipe, Speaking("0.1.1"));
        HealthState atTheTimeout, pastIt;
        try
        {
            await silent.ConnectAsync();
            Assert.True(await held.WaitAsync(TestTime.MarginSeconds(10)), "the bridge's hello never reached the connector");

            clock.StepWall(TimeSpan.FromHours(-1));
            clock.Advance(connector.HeartbeatTimeout);
            atTheTimeout = await connector.GetHealthAsync();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            pastIt = await connector.GetHealthAsync();
        }
        finally { release.Set(); }

        var dropped = await Within(async () => !await connector.IsConnectedAsync());

        Assert.Equal((HealthState.READY, HealthState.DEGRADED, true), (atTheTimeout, pastIt, dropped));
        Assert.Null(connector.Bridge);
        Assert.Equal(HealthState.FAILED, await connector.GetHealthAsync());
    }

    /// <summary>
    /// (b) A BRIDGE THAT KEEPS PULSING STAYS READY WHILE THE WALL CLOCK JUMPS AN HOUR AHEAD, AND IS NOT
    /// DROPPED.
    ///
    /// It pulses every five seconds on a clock only this test moves, and the wall clock jumps an hour
    /// ahead between two pulses. Right after the jump the health is read; then the next frame is an
    /// ANSWER, not a pulse. The read loop asks whether the peer has gone quiet on every turn, and on
    /// that turn nothing refreshed the stamp in front of the question, so it is answered with the jump
    /// in it. Each pulse is waited for until the connector has recorded it — its version is what the
    /// bridge then reports — so a connection dropped on that turn shows as the next pulse never heard.
    /// With the wall clock deciding, it read an hour and five seconds since the last beat: DEGRADED, and
    /// a bridge that never stopped was dropped.
    /// </summary>
    [Fact]
    public async Task A_bridge_pulsing_while_the_wall_clock_steps_forward_an_hour_stays_ready()
    {
        var clock = new SteppedClock();
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { LivenessClock = clock };
        await connector.ConnectAsync();

        await using var alive = new StubBridge(pipe, Speaking("0.2.0"));
        await alive.ConnectAsync();
        Assert.True(await Within(() => connector.Bridge?.BridgeVersion == "0.2.0"), "the bridge's hello never reached the connector");

        async Task<bool> Heard(int pulse)
        {
            try { await alive.Heartbeat(Speaking($"0.2.{pulse}")); }
            catch (IOException) { return false; }   // the connector already let go of the pipe
            return await Within(() => connector.Bridge?.BridgeVersion == $"0.2.{pulse}");
        }

        for (var pulse = 1; pulse <= 4; pulse++)
        {
            clock.Advance(Pulse);
            if (pulse == 3)
            {
                clock.StepWall(TimeSpan.FromHours(1));
                Assert.Equal(HealthState.READY, await connector.GetHealthAsync());
                Assert.NotEmpty(await connector.GetAccountsAsync());
            }
            Assert.True(await Heard(pulse), $"pulse {pulse} was never heard: the connection did not outlive the wall clock's jump");
        }

        Assert.Equal(HealthState.READY, await connector.GetHealthAsync());
        Assert.True(await connector.IsConnectedAsync());
    }

    /// <summary>
    /// (c) THE AUTH GRACE RUNS OUT ON THE MONOTONIC CLOCK WHILE THE WALL CLOCK IS PUT BACK AN HOUR.
    ///
    /// A program takes the pipe and says nothing at all. For the length of the grace the row says it is
    /// connecting; once the grace is spent the row says what is true — something holds the pipe and has
    /// neither proved itself nor said hello — and sends the owner to the repair. The grace governs that
    /// sentence and nothing else, so this is the whole of what a clock step can do to it: with the wall
    /// clock deciding, an hour behind, the row went on saying "connecting" for the hour about a program
    /// that was never going to authenticate.
    /// </summary>
    [Fact]
    public async Task The_auth_grace_runs_out_on_the_monotonic_clock_while_the_wall_clock_steps_back_an_hour()
    {
        var clock = new SteppedClock();
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5)) { LivenessClock = clock };
        await connector.ConnectAsync();

        using var quiet = await HandOver.ToASilentPeer(connector, pipe, () => connector.StatusDetail == Connecting);

        clock.StepWall(TimeSpan.FromHours(-1));
        clock.Advance(connector.AuthGrace);
        var atTheGrace = Reading(connector.StatusDetail);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var pastIt = Reading(connector.StatusDetail);

        Assert.Equal(("connecting", "silent"), (atTheGrace, pastIt));
        Assert.Same(UnauthenticatedBridge.Silent, connector.Unauthenticated);
    }

    /// <summary>The row, named: the grace's line, the silence reading, or whatever else it says.</summary>
    static string Reading(string? row) =>
        row == Connecting ? "connecting"
        : row == UnauthenticatedBridge.Silent.ToString() ? "silent"
        : row ?? "(nothing)";

    static BridgeHello Speaking(string bridgeVersion) => new()
    {
        BridgeProtocolVersion = Versions.BridgeProtocolVersion,
        BridgeVersion = bridgeVersion, AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
        SupportsClientOrderId = true, SupportsOrderHistory = true,
        SupportsModify = true, SupportsClosePosition = true
    };

    /// <summary>
    /// Whether <paramref name="condition"/> holds within the fixture's patience: polled for an outcome
    /// the product decides, never a sleep that hopes it got there. Timed on <c>TickCount64</c>.
    /// </summary>
    static async Task<bool> Within(Func<Task<bool>> condition, int ms = 10_000)
    {
        var deadline = Environment.TickCount64 + TestTime.MarginMillis(ms);
        while (Environment.TickCount64 < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(20);
        }
        return await condition();
    }

    static Task<bool> Within(Func<bool> condition, int ms = 10_000) =>
        Within(() => Task.FromResult(condition()), ms);
}
