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
    /// The distinction the whole contract turns on: nothing sent is a failed call, sent-and-unanswered
    /// is an UNKNOWN order. Only the second may tell the agent to re-run with the same id.
    /// </summary>
    [Fact]
    public void Nothing_sent_and_reply_lost_are_different_sentences()
    {
        Assert.Null(CliReplayContract.RecoveryLine(sent: false, "cli-abc"));
        Assert.Null(CliReplayContract.RecoveryLine(sent: true, null));   // a read has nothing to replay

        var lost = CliReplayContract.RecoveryLine(sent: true, "cli-abc");
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
        var unsent = JsonDocument.Parse(Json.Write(CliReplayContract.UnansweredJson("cli-abc", sent: false, err))).RootElement;
        Assert.Equal("cli-abc", unsent.GetProperty("request_id").GetString());
        Assert.False(unsent.GetProperty("reply_lost").GetBoolean());

        var lost = JsonDocument.Parse(Json.Write(CliReplayContract.UnansweredJson("cli-abc", sent: true, err))).RootElement;
        Assert.True(lost.GetProperty("reply_lost").GetBoolean());
        Assert.Contains("--request-id cli-abc", lost.GetProperty("recovery").GetString());
    }

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

    // ---------------------------------------------------------------- helpers

    /// <summary>Answers the handshake, takes one order frame, then closes without replying.</summary>
    static async Task HangUpAfterTheOrder(string pipe, string token) => await Task.Run(async () =>
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var r = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        var w = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        var hello = Json.Read<IpcRequest>((await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!)!;
        await w.WriteLineAsync(Json.Write(IpcResponse.Success(hello.Id, new
        {
            protocol_version = Versions.ProtocolVersion,
            app_version = Versions.App,
            compatible = true
        })));

        await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));   // the order, taken and never answered
        server.Dispose();
    });

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
