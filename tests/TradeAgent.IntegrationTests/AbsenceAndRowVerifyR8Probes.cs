using System.IO.Pipes;
using System.Text;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// VERIFIER ROUND 8 — targets 1 (the absence predicate), 2 (the lease on every terminal path with the
/// race), 5 (the other F23 peers) and 6 (V4's precedence in both orders).
/// </summary>
public class AbsenceAndRowVerifyR8Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "abs8-" + Guid.NewGuid().ToString("n")[..8]);
    public AbsenceAndRowVerifyR8Probes() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception) { }
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }
    string File_ => Path.Combine(_dir, "coid-witness.json");

    // ---------------------------------------------------------------- target 1

    /// <summary>
    /// The builder's PRIOR 17 test RENAMES the directory (`Directory.Move`). This DELETES it, which is
    /// the case the ratified rule is written about — a cleanup, an unmounted volume, a wiped profile.
    /// </summary>
    [Fact]
    public void A_deleted_bridge_directory_is_unreadable_and_nothing_is_written()
    {
        var w = new CoidWitness(File_);
        Assert.True(w.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        w.Identified("TA-A", "BRK-A");

        Directory.Delete(_dir, recursive: true);      // under a LIVE witness that holds its lease

        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("could not be read", w.Trouble);
        Assert.DoesNotContain("changed underneath", w.Trouble);
        Assert.False(Directory.Exists(_dir), "nothing was recreated under the witness path");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// A MACHINE WITH NO BRIDGE DIRECTORY AT ALL — the ratified fail-closed case, from a cold start
    /// rather than by removing one out from under a live writer.
    /// </summary>
    [Fact]
    public void A_machine_with_no_bridge_directory_refuses_every_order()
    {
        var nowhere = Path.Combine(_dir, "never-existed", "coid-witness.json");
        var w = new CoidWitness(nowhere);

        Assert.False(w.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.False(Directory.Exists(Path.GetDirectoryName(nowhere)!),
                     "the witness created its own directory, which would let it write over a history it could not see");
    }

    /// <summary>
    /// THE FIFTH F17 VARIANT — the DIRECTORY, not the file, is unreadable. `chmod 000` on the folder
    /// makes every stat and open inside it fail, which is a different syscall path from a chmod on the
    /// file. It must land in the same predicate: refused, and diagnosed as unreadable.
    /// </summary>
    [Fact]
    public void An_unreadable_bridge_directory_is_the_same_predicate_as_an_unreadable_file()
    {
        var w = new CoidWitness(File_);
        Assert.True(w.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        w.Dispose();

        File.SetUnixFileMode(_dir, UnixFileMode.None);
        try
        {
            var reader = new CoidWitness(File_);
            Assert.False(reader.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
            Assert.NotNull(reader.Trouble);
            Assert.Contains("could not be read", reader.Trouble);
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---------------------------------------------------------------- target 2

    /// <summary>
    /// THE INTERLEAVING ITSELF, driven with two real threads rather than with an injected callback:
    /// `Record` is entered while the strategy is running and the stop lands mid-write. Repeated, so a
    /// window that opens only sometimes shows up.
    /// </summary>
    [Fact]
    public void A_stop_that_lands_mid_write_never_leaves_the_lease_held()
    {
        for (var round = 0; round < 40; round++)
        {
            var dir = Path.Combine(_dir, "r" + round);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "coid-witness.json");
            // ROUND 9: the teardown OWNS the witness now, so the harness constructs it that way.
            var witness = new CoidWitness(path);
            var teardown = new AdapterTeardown(witness);
            teardown.Started();

            using var go = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                go.Wait();
                teardown.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null);
            });
            var stopper = Task.Run(() =>
            {
                go.Wait();
                teardown.Stop(steps: () => throw new InvalidOperationException("UntrackSecurities blew up"));
            });
            go.Set();
            try { Task.WaitAll(writer, stopper); } catch (AggregateException) { /* the steps throw by design */ }

            var replacement = new CoidWitness(path);
            Assert.True(replacement.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null),
                        $"round {round}: the lease survived a terminal path: {replacement.Trouble}");
            replacement.Dispose();
            witness.Dispose();
        }
    }

    // ---------------------------------------------------------------- targets 5 and 6

    static string NewPipe() => "ta-abs8-" + Guid.NewGuid().ToString("n")[..12];

    static async Task Wait(Func<bool> c, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline) { if (c()) return; await Task.Delay(50); }
        throw new TimeoutException("condition was not met in time");
    }

    static async Task<(NamedPipeClientStream Client, StreamWriter W, StreamReader R)> AuthAsync(string pipe)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        var w = new StreamWriter(client, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var r = new StreamReader(client, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var cred = BridgePipeAuth.ReadForClient()!;
        var nonce = BridgePipeAuth.NewNonce();
        await w.WriteLineAsync(Json.Write(new
        {
            v = Versions.BridgeProtocolVersion,
            op = BridgePipeAuth.Challenge,
            data = new { nonce, proof = BridgePipeAuth.Proof(cred.Secret, BridgePipeAuth.BridgeRole, nonce) }
        }));
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            if (Json.Read<BridgeFrame>(line)?.Op == BridgePipeAuth.Response) break;
        return (client, w, r);
    }

    static Task SayHello(StreamWriter w, int protocolVersion) =>
        w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(
                new BridgeHello { BridgeProtocolVersion = protocolVersion, BridgeVersion = "0.1.1", AtasVersion = "6.1.2.3" },
                Json.Options)
        }));

    /// <summary>TARGET 5 — a peer that writes bytes and never a newline holds no instance for ever.</summary>
    [Fact]
    public async Task A_partial_frame_peer_is_dropped_so_the_pipe_can_be_taken_again()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10)) { HeartbeatTimeout = TimeSpan.FromSeconds(1) };
        await connector.ConnectAsync();
        await using var _1 = connector;

        var (partial, w, _) = await AuthAsync(pipe);
        await w.WriteAsync("{\"v\":3,\"op\":\"hel");        // no newline, ever
        await w.FlushAsync();
        await Task.Delay(3_000);

        await using var live = new StubBridge(pipe, new BridgeHello
        {
            BridgeProtocolVersion = Versions.BridgeProtocolVersion,
            BridgeVersion = "0.1.2", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
        });
        await live.ConnectAsync();
        await Wait(() => connector.Bridge?.BridgeVersion == "0.1.2", 10_000);
        partial.Dispose();
    }

    /// <summary>
    /// TARGET 6 — V4's precedence in the REVERSE order the builder's test drives: an authentication
    /// refusal FIRST, then a protocol-2 peer. The row must say the protocol sentence.
    /// </summary>
    [Fact]
    public async Task The_reverse_order_also_puts_the_newer_refusal_on_the_row()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        // A peer that authenticates and then says hello without ever presenting the secret is refused
        // in Dispatch; here the simplest form of the same news — say hello with no challenge at all.
        var unproved = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await unproved.ConnectAsync(10_000);
        var uw = new StreamWriter(unproved, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        await SayHello(uw, Versions.BridgeProtocolVersion);
        await Wait(() => connector.Unauthenticated is not null, 10_000);
        unproved.Dispose();

        // Then the operator's real add-on arrives — an OLD one, speaking protocol 2.
        var (old, ow, _) = await AuthAsync(pipe);
        await SayHello(ow, 2);
        await Wait(() => connector.Incompatible is not null, 10_000);

        Assert.Contains("speaks protocol 2", connector.StatusDetail);
        // And the older AUTH marker is gone, correctly: this peer DID prove it holds the secret, so
        // NoteUnauthenticated(null) at :641 repaired it. The precedence rule is not what cleared it.
        Assert.Null(connector.Unauthenticated);
        Assert.Equal(2, connector.Incompatible!.ReportedProtocolVersion);
        old.Dispose();
    }

    /// <summary>TARGET 6 — and a live good bridge clears both readings.</summary>
    [Fact]
    public async Task A_live_good_bridge_clears_both_refusals()
    {
        var pipe = NewPipe();
        var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10));
        await connector.ConnectAsync();
        await using var _1 = connector;

        var unproved = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await unproved.ConnectAsync(10_000);
        var uw = new StreamWriter(unproved, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        await SayHello(uw, Versions.BridgeProtocolVersion);
        await Wait(() => connector.Unauthenticated is not null, 10_000);
        unproved.Dispose();

        var (old, ow, _) = await AuthAsync(pipe);
        await SayHello(ow, 2);
        await Wait(() => connector.Incompatible is not null, 10_000);
        old.Dispose();

        // The instance recycles asynchronously after the refused peer goes; redial until it is offered,
        // exactly as the real BridgeServer does at ReconnectDelay.
        StubBridge? good = null;
        for (var attempt = 0; attempt < 20 && good is null; attempt++)
        {
            var candidate = new StubBridge(pipe, new BridgeHello
            {
                BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                BridgeVersion = "0.1.2", AtasVersion = "6.1.2.3", AccountId = "ATAS-SIM"
            });
            try { await candidate.ConnectAsync(); good = candidate; }
            catch (Exception) { try { await candidate.DisposeAsync(); } catch (Exception) { } await Task.Delay(150); }
        }
        Assert.NotNull(good);
        await using var _2 = good;
        await Wait(() => connector.Bridge?.BridgeVersion == "0.1.2", 10_000);

        Assert.Null(connector.Incompatible);
        Assert.Null(connector.Unauthenticated);
        Assert.Null(connector.StatusDetail);
    }
}
