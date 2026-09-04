using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The CLI half of the replay contract, which had no test at all.
///
/// `trade` mints the request id, and the id is the only thing that makes a retry safe. Round 2 fixed
/// the behaviour and verified it by running the binary once, by hand — so two mutants, "stop printing
/// the id" and "never say reply lost", both left the whole suite green. Top-level statements in an
/// exe are not reachable from a test, which is why the contract now lives in
/// <see cref="CliReplayContract"/>.
///
/// The last two tests run the REAL binary, because a tested function that Program.cs has stopped
/// calling is worth nothing.
/// </summary>
public class CliReplayContractTests
{
    // ------------------------------------------------------------ the contract itself

    [Fact]
    public void An_order_gets_an_id_and_a_read_does_not()
    {
        Assert.NotNull(CliReplayContract.MintRequestId(Ops.Buy, null));
        Assert.StartsWith("cli-", CliReplayContract.MintRequestId(Ops.Sell, null));
        Assert.Null(CliReplayContract.MintRequestId(Ops.Status, null));

        // What the caller asked for always wins; that is what makes a re-run a replay.
        Assert.Equal("mine-1", CliReplayContract.MintRequestId(Ops.Buy, "mine-1"));
        Assert.Equal("mine-1", CliReplayContract.MintRequestId(Ops.Status, "mine-1"));

        // Two calls, two orders: an id is only reused when the caller says so.
        Assert.NotEqual(CliReplayContract.MintRequestId(Ops.Buy, null), CliReplayContract.MintRequestId(Ops.Buy, null));
    }

    [Fact]
    public void The_id_is_announced_on_stderr_and_nowhere_else()
    {
        var err = new StringWriter();
        CliReplayContract.AnnounceRequestId(err, "cli-abc123");
        Assert.Equal($"request-id: cli-abc123{Environment.NewLine}", err.ToString());

        // Nothing to announce for a read.
        var quiet = new StringWriter();
        CliReplayContract.AnnounceRequestId(quiet, null);
        Assert.Equal("", quiet.ToString());
    }

    /// <summary>
    /// The distinction the whole contract turns on: provably nothing written is a failed call,
    /// possibly written is an UNKNOWN order. Only the second may tell the agent to re-run with the
    /// same id — and a reply that came back needs no recovery line at all.
    /// </summary>
    [Fact]
    public void Nothing_sent_and_reply_lost_are_different_sentences()
    {
        Assert.Null(CliReplayContract.RecoveryLine(TransportOutcome.NothingWritten, "cli-abc"));
        Assert.Null(CliReplayContract.RecoveryLine(TransportOutcome.ReplyReceived, "cli-abc"));
        Assert.Null(CliReplayContract.RecoveryLine(TransportOutcome.PossiblyWritten, null));   // a read has nothing to replay

        var lost = CliReplayContract.RecoveryLine(TransportOutcome.PossiblyWritten, "cli-abc");
        Assert.NotNull(lost);
        Assert.Contains("reply lost", lost);
        Assert.Contains("--request-id cli-abc", lost);
        Assert.Contains("trade orders", lost);
    }

    [Fact]
    public void The_json_object_carries_the_id_on_every_path()
    {
        var answered = JsonDocument.Parse(Json.Write(CliReplayContract.AnsweredJson("cli-abc", IpcResponse.Success("1", new { x = 1 })))).RootElement;
        Assert.Equal("cli-abc", answered.GetProperty("request_id").GetString());
        Assert.True(answered.GetProperty("ok").GetBoolean());

        var err = IpcError.From(new ErrorInfo(ErrorCode.IPC_UNAVAILABLE, "gone", "gone", "restart", true));
        var unsent = JsonDocument.Parse(Json.Write(CliReplayContract.UnansweredJson("cli-abc", TransportOutcome.NothingWritten, err))).RootElement;
        Assert.Equal("cli-abc", unsent.GetProperty("request_id").GetString());
        Assert.False(unsent.GetProperty("reply_lost").GetBoolean());
        Assert.Equal("NothingWritten", unsent.GetProperty("transport").GetString());

        var lost = JsonDocument.Parse(Json.Write(CliReplayContract.UnansweredJson("cli-abc", TransportOutcome.PossiblyWritten, err))).RootElement;
        Assert.True(lost.GetProperty("reply_lost").GetBoolean());
        Assert.Equal("PossiblyWritten", lost.GetProperty("transport").GetString());
        Assert.Contains("--request-id cli-abc", lost.GetProperty("recovery").GetString());
    }

    /// <summary>
    /// THE ONE TRANSPORT TRANSITION THAT WOULD PLACE A SECOND REAL ORDER, AND IT HAD NO TEST.
    ///
    /// Verifier finding F-C. Mutant W3 turns the read-failure path from <c>PossiblyWritten</c> into
    /// <c>NothingWritten</c> and all 238 integration tests stayed green — its two siblings, the
    /// clean-EOF path and the truncated-reply path, both had biting tests, so this was a hole rather
    /// than a pattern. The consequence is exact and it is the worst one this unit has:
    /// <c>RecoveryLine</c> returns null, <c>reply_lost</c> is false, and the agent is never told to
    /// re-run with the SAME id — so a frame that provably left this process becomes a fresh proposal
    /// with a new id, which is a second real order. That is 7c93181's original defect, reachable by
    /// a one-word edit nothing caught.
    ///
    /// The read has to FAIL rather than end, which is what makes this path distinct from its two
    /// siblings: a clean close gives end-of-stream and a truncated object gives a parse failure.
    /// Cancelling the caller's token while the reply is outstanding is the reachable form of it — a
    /// timeout or a Ctrl-C on a call whose order is already at the service — and it is deterministic,
    /// which an abortive socket close on a local pipe is not.
    /// </summary>
    [Fact]
    public async Task A_reply_whose_read_fails_leaves_the_order_possibly_written()
    {
        var pipe = "ta-w3-" + Guid.NewGuid().ToString("n")[..12];
        IpcToken.Ensure();
        using var stop = new CancellationTokenSource();
        var serving = TakeTheOrderAndSayNothing(pipe, stop.Token);

        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        using var caller = new CancellationTokenSource();
        var call = client.TrySendAsync(new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = "cli-w3-1",
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement("ES"),
                ["quantity"] = JsonSerializer.SerializeToElement("1")
            }
        }, caller.Token);

        // The frame is out and the service is holding it; we are inside the read.
        await Task.Delay(400);
        await caller.CancelAsync();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));

        // The frame left this process, so nothing below may call it unsent.
        Assert.Equal(TransportOutcome.PossiblyWritten, result.Outcome);
        Assert.Null(result.Reply);

        // And the whole consequence chain, because the outcome only matters through what it makes
        // the CLI say: an id to re-run with, and reply_lost telling the agent this is a retry.
        var recovery = CliReplayContract.RecoveryLine(result.Outcome, "cli-w3-1");
        Assert.NotNull(recovery);
        Assert.Contains("--request-id cli-w3-1", recovery);

        var json = JsonDocument.Parse(Json.Write(CliReplayContract.UnansweredJson(
            "cli-w3-1", result.Outcome, IpcError.From(result.Failure!.Info)))).RootElement;
        Assert.True(json.GetProperty("reply_lost").GetBoolean(),
            "a frame that reached the service was reported as never sent, so the agent would propose again with a NEW id");
        Assert.Equal("PossiblyWritten", json.GetProperty("transport").GetString());

        await stop.CancelAsync();
        try { await serving; } catch (Exception) { /* torn down with the test */ }
    }

    /// <summary>
    /// THE CLI MUST NOT PROMISE A CONTRACT THE GATEWAY HAS NOT IMPLEMENTED.
    ///
    /// Codex PRIOR 8, CLI half. Every mutating command printed "retrying with the same --request-id
    /// is safe; it will not place a second order." That is true of `buy` and `sell` and of nothing
    /// else today: `TradingGateway` consults the idempotency store before dispatch only on the place
    /// path, `CancelAsync` and `ModifyAsync` authorize and resolve their target before looking, and
    /// `CloseAsync` re-reads positions and places an offsetting order. An agent that believed the
    /// old sentence would re-run a close and flatten a position twice.
    ///
    /// The blanket contract is U2c-1's to implement. Until it does, this is what the CLI says.
    /// </summary>
    [Theory]
    [InlineData(Ops.Buy, true)]
    [InlineData(Ops.Sell, true)]
    [InlineData(Ops.Cancel, false)]
    [InlineData(Ops.CancelAll, false)]
    [InlineData(Ops.Modify, false)]
    [InlineData(Ops.Close, false)]
    [InlineData(Ops.CloseAll, false)]
    public void The_success_note_promises_a_replay_only_where_the_gateway_performs_one(string op, bool replayable)
    {
        var note = CliReplayContract.SuccessNote(op);
        Assert.NotNull(note);
        Assert.Contains("--request-id", note);

        if (replayable)
        {
            Assert.Contains("will not place a second order", note);
            Assert.DoesNotContain("NOT a replay", note);
        }
        else
        {
            Assert.Contains("NOT a replay", note);
            Assert.DoesNotContain("will not place a second order", note);
            Assert.Contains("check", note);
        }
    }

    /// <summary>A read has nothing to replay and is told nothing.</summary>
    [Theory]
    [InlineData(Ops.Status)]
    [InlineData(Ops.Orders)]
    [InlineData(Ops.Positions)]
    public void A_read_gets_no_note(string op) => Assert.Null(CliReplayContract.SuccessNote(op));

    // ------------------------------------------------------------ the real binary

    /// <summary>
    /// Nothing listening: the id is still announced before the attempt, and the tool must NOT claim a
    /// lost reply, because nothing was sent and there is nothing to reconcile.
    /// </summary>
    [Fact]
    public async Task The_real_cli_announces_the_id_before_it_fails_to_connect()
    {
        var (exit, stdout, stderr) = await RunTrade("buy", "ES", "1", "--json");

        Assert.Equal(1, exit);
        Assert.Contains("request-id: cli-", stderr);

        var json = JsonDocument.Parse(stdout).RootElement;
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.StartsWith("cli-", json.GetProperty("request_id").GetString());
        Assert.False(json.GetProperty("reply_lost").GetBoolean());

        // And the id it printed is the id it reported.
        Assert.Contains($"request-id: {json.GetProperty("request_id").GetString()}", stderr);
    }

    /// <summary>
    /// THE BRANCH ROUND 2 COULD NOT TEST. A peer that takes the order and hangs up without answering
    /// is exactly the dangerous case: the order may be at the broker and only the reply is gone.
    /// </summary>
    [Fact]
    public async Task The_real_cli_says_the_reply_is_lost_when_the_service_hangs_up_on_the_order()
    {
        var token = IpcToken.Ensure();
        var pipe = Paths.PipeName;
        var hungUp = HangUpAfterTheOrder(pipe, token);

        var (exit, stdout, stderr) = await RunTrade("buy", "ES", "1", "--json");
        await hungUp;

        Assert.Equal(1, exit);
        var json = JsonDocument.Parse(stdout).RootElement;
        var id = json.GetProperty("request_id").GetString()!;

        Assert.True(json.GetProperty("reply_lost").GetBoolean(),
            $"the service took the order and hung up, and the CLI did not say the reply was lost: {stdout}");
        Assert.Contains($"--request-id {id}", json.GetProperty("recovery").GetString());
        Assert.Contains($"request-id: {id}", stderr);
    }

    /// <summary>
    /// A TRUNCATED REPLY IS A REPLY WE DID NOT GET, and it must not kill the process.
    ///
    /// Codex F7: <c>PipeClient</c> let write/read <c>IOException</c>, <c>ObjectDisposedException</c>
    /// and a <c>JsonException</c> from a partial response escape unwrapped, while <c>Program.cs</c>
    /// caught only <c>TradeAgentException</c> — so the most common lost-reply shapes terminated the
    /// CLI with a stack trace and no structured output at all. The one wrapped path was the only one
    /// the suite exercised, because the old fake server always closed cleanly on a frame boundary.
    ///
    /// Half a JSON object and then a close. The order went out, so this is UNKNOWN and must say so.
    /// </summary>
    [Fact]
    public async Task The_real_cli_reports_a_half_written_reply_as_an_unknown_order()
    {
        var token = IpcToken.Ensure();
        var serving = HalfAReplyThenClose(Paths.PipeName, token);

        var (exit, stdout, stderr) = await RunTrade("buy", "ES", "1", "--json");
        await serving;

        Assert.Equal(1, exit);
        Assert.DoesNotContain("Unhandled exception", stderr);

        var json = JsonDocument.Parse(stdout).RootElement;
        var id = json.GetProperty("request_id").GetString()!;
        Assert.Equal("PossiblyWritten", json.GetProperty("transport").GetString());
        Assert.True(json.GetProperty("reply_lost").GetBoolean(),
            $"a half-written reply was not reported as a lost one: {stdout}");
        Assert.Contains($"--request-id {id}", json.GetProperty("recovery").GetString());
    }

    /// <summary>
    /// The other half of F7: the service goes away while the REQUEST is being written. Whichever
    /// classification the transport can prove, it must be one of them, reported in the structured
    /// output, and never an unhandled exception.
    /// </summary>
    [Fact]
    public async Task The_real_cli_reports_a_service_that_vanishes_during_the_request()
    {
        var token = IpcToken.Ensure();
        var serving = CloseRightAfterTheHandshake(Paths.PipeName, token);

        var (exit, stdout, stderr) = await RunTrade("buy", "ES", "1", "--json");
        await serving;

        Assert.Equal(1, exit);
        Assert.DoesNotContain("Unhandled exception", stderr);

        var json = JsonDocument.Parse(stdout).RootElement;
        Assert.StartsWith("cli-", json.GetProperty("request_id").GetString());
        var transport = json.GetProperty("transport").GetString();
        Assert.True(transport is "NothingWritten" or "PossiblyWritten",
            $"the transport state was '{transport}', which is neither of the two things that can be true here");

        // Whatever it decided, the recovery advice has to agree with it — the two used to be able to
        // disagree, because one came from the transport and the other from a boolean set in advance.
        Assert.Equal(transport == "PossiblyWritten", json.GetProperty("reply_lost").GetBoolean());
    }

    /// <summary>
    /// THE ID IS ANNOUNCED BEFORE THE FRAME GOES OUT, AND THIS TEST WATCHES IT HAPPEN.
    ///
    /// Codex F13: the previous version waited for the process to exit and then checked that stderr
    /// contained the id, which moving the announcement into the connection-failure catch — AFTER the
    /// attempted send — would also have satisfied. Here the service completes the handshake and then
    /// never answers, so the CLI is still blocked when the assertion is made: an announcement made
    /// on any failure path could not have been printed yet, because there has been no failure.
    /// </summary>
    [Fact]
    public async Task The_real_cli_announces_the_id_while_the_call_is_still_in_flight()
    {
        var token = IpcToken.Ensure();
        var handshaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var serving = HandshakeThenSilence(Paths.PipeName, handshaken, stop.Token);

        var (exe, prefix) = TradeBinary();
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "buy", "ES", "1" }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        try
        {
            // Wait until the service has answered the handshake and gone quiet. From here the CLI is
            // inside the call: its order is written or being written, and no reply will ever come.
            await handshaken.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var line = await p.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(line);
            Assert.StartsWith("request-id: cli-", line);

            // The ordering claim: the id is on stderr while the call is STILL OUTSTANDING. An
            // announcement moved into any failure path could not have run — nothing has failed yet,
            // and nothing will, because this service simply never answers.
            Assert.False(p.HasExited, "the CLI had already finished, so this proves nothing about ordering");
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            await stop.CancelAsync();
            try { await serving; } catch (Exception) { /* the service is torn down with the client */ }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Answers the handshake, takes one order frame, then closes without replying.</summary>
    static async Task HangUpAfterTheOrder(string pipe, string token) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        // The client may be killed by the test before it says hello; that is an ending, not a fault.
        if (await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is not { } helloLine) return;
        var hello = Json.Read<IpcRequest>(helloLine)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));

        await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));   // the order, taken and never answered
        server.Dispose();
    });

    /// <summary>Answers the handshake, takes the order, writes HALF a reply, then closes.</summary>
    static async Task HalfAReplyThenClose(string pipe, string token) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        // The client may be killed by the test before it says hello; that is an ending, not a fault.
        if (await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is not { } helloLine) return;
        var hello = Json.Read<IpcRequest>(helloLine)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));

        await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));   // the order
        // A line terminator with a truncated object in front of it: the client reads a whole line
        // and cannot parse it, which is a different failure from never receiving one.
        await w.WriteLineAsync("{\"ok\":true,\"data\":{\"state\":\"FIL");
        server.Dispose();
    });

    /// <summary>Answers the handshake and closes at once, before the order can be written.</summary>
    static async Task CloseRightAfterTheHandshake(string pipe, string token) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        // The client may be killed by the test before it says hello; that is an ending, not a fault.
        if (await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is not { } helloLine) return;
        var hello = Json.Read<IpcRequest>(helloLine)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));
        server.Dispose();
    });

    /// <summary>
    /// Answers the handshake, signals that it has, and then says nothing at all — holding the
    /// connection open so the client stays inside its call until the test tears it down.
    /// </summary>
    static async Task HandshakeThenSilence(string pipe, TaskCompletionSource handshaken, CancellationToken stop) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(30), stop);
        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        // The client may be killed by the test before it says hello; that is an ending, not a fault.
        if (await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is not { } helloLine) return;
        var hello = Json.Read<IpcRequest>(helloLine)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));

        handshaken.TrySetResult();

        // Held open, unread and unanswered, until the test tears the client down.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (Exception) { /* expected */ }
        server.Dispose();
    }, stop);

    /// <summary>Answers the handshake, takes the order, and then simply holds it — no reply, no close.</summary>
    static async Task TakeTheOrderAndSayNothing(string pipe, CancellationToken stop) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(stop).WaitAsync(TimeSpan.FromSeconds(30), stop);
        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        if (await r.ReadLineAsync(stop) is not { } helloLine) return;
        var hello = Json.Read<IpcRequest>(helloLine)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));

        await r.ReadLineAsync(stop);          // the order, taken and held
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (Exception) { /* expected */ }
        server.Dispose();
    }, stop);

    static async Task<(int Exit, string Stdout, string Stderr)> RunTrade(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var (exe, prefix) = TradeBinary();
        psi.FileName = exe;
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// The built `trade`, preferring the native apphost and falling back to `dotnet trade.dll`.
    /// Located from the test assembly by walking up to the solution, so it does not depend on the
    /// working directory a runner happens to choose.
    /// </summary>
    static (string Exe, string[] Prefix) TradeBinary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.True(dir is not null, "could not find TradeAgent.sln above " + AppContext.BaseDirectory);

        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        var bin = Path.Combine(dir!.FullName, "src", "TradeAgent.TradeCli", "bin", config, "net10.0");

        var apphost = Path.Combine(bin, OperatingSystem.IsWindows() ? "trade.exe" : "trade");
        if (File.Exists(apphost)) return (apphost, []);

        var dll = Path.Combine(bin, "trade.dll");
        Assert.True(File.Exists(dll), $"the trade binary was not found at {bin}");
        return ("dotnet", [dll]);
    }
}
