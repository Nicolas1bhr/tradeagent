using System.Text;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE STUB BRIDGE SENDS ONE FRAME AT A TIME, AS THE REAL ONE DOES.
///
/// <para><see cref="StubBridge"/> answers the connector's RPCs from its own read loop, on the same
/// writer a test sends its pulses, hellos and events through. With no gate between the two, a test's
/// next frame could meet the loop's answer still inside its write: on windows-latest the connector
/// read the answer to <c>accounts</c>, the test moved on to its next pulse, and the stub's write of
/// that answer had not yet completed — so <c>StreamWriter</c> refused the pulse with
/// <c>InvalidOperationException: The stream is currently in use by a previous operation on the
/// stream</c>, out of <c>BridgeLivenessClockTests.A_bridge_pulsing_while_the_wall_clock_steps_forward
/// _an_hour_stays_ready</c> at its third pulse, on no assertion (run 37612881764). The real bridge
/// sends every frame through one <c>SemaphoreSlim</c> (<c>BridgeServer._send</c>); the stub now does
/// too.</para>
///
/// <para>The window is held open, never hoped for: a stream under the stub's writer holds the frame
/// carrying the loop's answer inside its write until the test lets it go, and the pulse is sent inside
/// that window. Without the gate the test is red in one of two shapes, both measured on this Mac: the
/// writer has registered the answer's write and refuses the pulse with the exception above, or it has
/// not yet, and the pulse is written over the buffer the held answer is still being written from
/// ("the pulse was written while the loop's answer was still being written"). The connector's liveness
/// clock stands still, so no drop of a quiet peer can decide anything here however slow the runner is.
/// Nothing in this class places an order.</para>
/// </summary>
public class StubBridgeTests
{
    static string NewPipe() => "ta-sbt-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// A PULSE SENT WHILE THE LOOP'S ANSWER IS STILL BEING WRITTEN WAITS FOR IT, and then both reach
    /// the connector: the answer, and the pulse behind it.
    /// </summary>
    [Fact]
    public async Task A_send_waits_for_an_answer_in_flight()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(30)) { LivenessClock = new Still() };
        await connector.ConnectAsync();

        LatchedStream? latch = null;
        await using var stub = new StubBridge(pipe, Speaking("0.3.0"))
        {
            // The frame the loop writes in answer to `accounts` names the stub's simulation account.
            WriterStream = wire => latch = new LatchedStream(wire, holding: "ATAS simulation")
        };
        await stub.ConnectAsync();
        Assert.True(await Within(() => connector.Bridge?.BridgeVersion == "0.3.0"), "the stub's hello never reached the connector");

        var accounts = connector.GetAccountsAsync();
        Task pulse;
        try
        {
            Assert.True(await latch!.HasEntered(TestTime.MarginSeconds(10)), "the loop's answer to accounts never reached its write");

            // THE ANSWER IS INSIDE ITS WRITE, and the test's pulse goes out now. It waits behind the
            // answer. A writer with no gate may refuse it on the spot instead, and that refusal is
            // awaited here so that it fails the test in its own words.
            pulse = stub.Heartbeat(Speaking("0.3.1"));
            if (pulse.IsFaulted) await pulse;
            Assert.False(pulse.IsCompleted, "the pulse was written while the loop's answer was still being written");
        }
        finally { latch?.Release(); }

        await pulse.WaitAsync(TestTime.MarginSeconds(10));
        Assert.Equal("ATAS-SIM", Assert.Single(await accounts).Id);
        Assert.True(await Within(() => connector.Bridge?.BridgeVersion == "0.3.1"), "the pulse sent behind the answer never reached the connector");
    }

    static BridgeHello Speaking(string bridgeVersion) => new()
    {
        BridgeProtocolVersion = Versions.BridgeProtocolVersion,
        BridgeVersion = bridgeVersion, AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM",
        SupportsClientOrderId = true, SupportsOrderHistory = true,
        SupportsModify = true, SupportsClosePosition = true
    };

    /// <summary>
    /// Whether <paramref name="condition"/> holds within the fixture's patience: polled for an outcome
    /// the connector decides, never a sleep that hopes it got there. Timed on <c>TickCount64</c>.
    /// </summary>
    static async Task<bool> Within(Func<bool> condition, int ms = 10_000)
    {
        var deadline = Environment.TickCount64 + TestTime.MarginMillis(ms);
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>A liveness clock that never moves, so the connector never finds this peer quiet.</summary>
    sealed class Still : TimeProvider
    {
        static readonly DateTimeOffset At = new(2026, 10, 7, 21, 0, 0, TimeSpan.Zero);
        public override long GetTimestamp() => 5_000_000;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() => At;
    }

    /// <summary>
    /// A STREAM THAT HOLDS ONE FRAME INSIDE ITS WRITE until the test lets it go: the first write whose
    /// bytes contain <c>holding</c> waits for <see cref="Release"/> before it reaches the pipe, and
    /// every other write goes straight through. The hold is bounded, so a test that fails before its
    /// release cannot keep the stub's writer for ever.
    /// </summary>
    sealed class LatchedStream(Stream inner, string holding) : Stream
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _held;

        public async Task<bool> HasEntered(TimeSpan within) =>
            await Task.WhenAny(_entered.Task, Task.Delay(within)) == _entered.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _held) == 0
                && Encoding.UTF8.GetString(buffer.Span).Contains(holding, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                _entered.TrySetResult();
                await Task.WhenAny(_released.Task, Task.Delay(TestTime.MarginSeconds(30), ct));
            }
            await inner.WriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
