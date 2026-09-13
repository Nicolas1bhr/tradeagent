using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ITEM 5 — `trade backtest` AS AN AGENT ACTUALLY REACHES IT: over the pipe, through the gateway, on a
/// program in a role's own folder and a dataset the ledger really recorded.
///
/// <para>The unit tests hold the arithmetic, the containment rule and the report's wording. This holds
/// the things only the wire settles: that the op is dispatched at all, that an UNKNOWN crosses as a
/// null rather than as a missing key, that a refusal reaches the agent with a code it can act on, and
/// that the daily report served over the same pipe stops saying nothing has been backtested.</para>
/// </summary>
public class BacktestOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    static string NewPipe() => "ta-bt-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// A GATEWAY, AND A CONNECTION THAT HAS PROVED WHICH LAUNCH IT IS.
    ///
    /// <para>The hello carries the launch grant the app minted for that role's process, which is what
    /// `U-containment` made the pipe's idea of who is calling. A backtest is recorded under a role and
    /// reads that role's own folder, so a connection that proved nothing is refused the op — these
    /// tests therefore speak the wire by hand, as <c>LaunchGrantTests</c> does, rather than through
    /// <c>PipeClient</c>, whose hello reads the grant out of the process environment.</para>
    /// </summary>
    static async Task<(TradingGateway Gw, Database Db, Raw Client, IAsyncDisposable Server, AgentGrants Grants)>
        Connected(string? role = CouncilRoles.Operations, string attempt = "attempt-1")
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Raw.ConnectAsync(pipe);
        var grant = role is null ? null : grants.Issue(role, attempt).Token;
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
        Assert.True(hello.Ok, Json.Write(hello.Error));

        return (gw, db, client, server, grants);
    }

    /// <summary>The wire by hand: the machine token on every frame, and the grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
            args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    const string Text = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A program in Operations' own folder, written with CRLF ON PURPOSE.
    ///
    /// <para>The agent writes this file on whatever platform the installation runs on, and the parser is
    /// line-based. A program that ran on one machine and was refused on another would be the
    /// `U-crlf-strategy-win` defect with a strategy in it, so the app normalises as it reads and this is
    /// the fixture that holds it to that.</para>
    /// </summary>
    static string GivenProgram(
        string text = Text, string name = "over-the-pipe.strategy", string role = CouncilRoles.Operations)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text.ReplaceLineEndings("\r\n"));
        return "strategies/" + name;
    }

    static DatasetRecord GivenData(Database db, int bars = 120, string pair = "BTCUSDT")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{At.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, At, At.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, At, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, At, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// A RUN OVER THE WIRE: THE RUN ID, THE METRICS THE APP COMPUTED, AND WHAT THEY ARE NOT.
    ///
    /// <para>The whole point of the op is that the agent gets back a figure it did not compute. The reply
    /// carries the run's id, the version's id, the declared execution model it ran under, the metrics,
    /// and a note saying what a run over bars cannot establish.</para>
    /// </summary>
    [Fact]
    public async Task A_backtest_over_the_pipe_answers_the_run_id_and_the_metrics_the_app_computed()
    {
        var (gw, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(("strategy", GivenProgram()), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                ("fees", "0.001"), ("slippage", "0.0005"))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = Data(reply);
        log.WriteLine(Json.Write(reply.Data));

        var runId = data.GetProperty("run_id").GetString()!;
        Assert.Equal(64, runId.Length);                                  // a sha256, not a guid
        Assert.Equal(64, data.GetProperty("version_id").GetString()!.Length);
        Assert.Equal(CouncilRoles.Operations, data.GetProperty("role").GetString());
        Assert.Equal("BTCUSDT", data.GetProperty("pair").GetString());
        Assert.Equal("fees=0.001;slippage=0.0005;increment=1;capital=10000",
            data.GetProperty("execution_model").GetString());
        Assert.Equal("COMPLETED", data.GetProperty("outcome").GetString());
        Assert.Equal(set.NormalisedSha256, data.GetProperty("dataset_sha256").GetString());

        var metrics = data.GetProperty("metrics");
        Assert.Equal(120, metrics.GetProperty("bars").GetInt64());
        Assert.True(metrics.GetProperty("trades").GetInt32() > 0);
        Assert.True(metrics.GetProperty("fills").GetInt32() > 0);

        // What it is NOT, in the answer itself rather than only in the schema.
        var note = data.GetProperty("note").GetString()!;
        Assert.Contains("no queue position", note);
        Assert.Contains("never a record of a trade", note);
        Assert.Contains("CLOSED trades only", note);

        // And the app recorded it: the row is there with the trace's hash on it.
        var row = gw.Strategies.RunById(runId);
        Assert.NotNull(row);
        Assert.Equal(data.GetProperty("trace_sha256").GetString(), row.TraceSha256);
        Assert.Equal(row.Trades, gw.Strategies.TradesOf(runId).Count);
    }

    /// <summary>
    /// AN UNKNOWN CROSSES THE WIRE AS A NULL AND NOT AS A MISSING KEY.
    ///
    /// <para>A window one bar long closes nothing, so there is no win rate — and a reader that received
    /// no <c>win_rate</c> key at all would read it as a field this build does not have rather than as a
    /// figure no trade was closed to compute. <c>missing</c> says which and why.</para>
    /// </summary>
    [Fact]
    public async Task A_figure_the_run_could_not_compute_crosses_as_a_null_with_a_reason_beside_it()
    {
        var (_, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(("strategy", GivenProgram()), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                ("from", "2026-08-01T00:00:00Z"), ("to", "2026-08-01T00:02:00Z"))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = Data(reply);
        log.WriteLine(Json.Write(reply.Data));

        var metrics = data.GetProperty("metrics");
        Assert.Equal(0, metrics.GetProperty("trades").GetInt32());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("win_rate").ValueKind);

        var missing = data.GetProperty("missing").EnumerateArray().ToList();
        Assert.Contains(missing, g => g.GetProperty("field").GetString() == "win rate");
    }

    /// <summary>
    /// A PATH OUTSIDE THE ROLE FOLDER IS REFUSED OVER THE WIRE, AND THE FILE IS NEVER READ.
    ///
    /// <para>The database is a real file this process can open, and a parse refusal echoes the line it
    /// failed on. So the refusal has to happen before the read, and the agent has to be told what the
    /// rule is rather than that something went wrong.</para>
    /// </summary>
    [Fact]
    public async Task A_program_path_outside_the_role_folder_is_refused_over_the_wire()
    {
        var (_, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        foreach (var path in new[] { "../../state/tradeagent.db", Paths.DatabaseFile, "../inbox/x.pdf" })
        {
            var reply = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Backtest, Session = "agent-1",
                Args = Args(("strategy", path), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
            });

            log.WriteLine(Json.Write(reply.Error));
            Assert.False(reply.Ok);
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
            Assert.Contains("outside the Operations Director's own folder", reply.Error.Message);
        }
    }

    /// <summary>A text that is not a program is refused with the LINE, which is what the writer needs.</summary>
    [Fact]
    public async Task A_program_that_does_not_parse_is_refused_over_the_wire_naming_the_line()
    {
        var (gw, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(
                ("strategy", GivenProgram("instrument BTCUSDT\nsize fixed 1\nentry when now() > 3\n", "bad.strategy")),
                ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Contains("line 3", reply.Error!.Message);
        Assert.Equal(0, gw.Strategies.RunCount);
    }

    /// <summary>A dataset whose bytes changed under it serves no run, with the code `data-bars` uses.</summary>
    [Fact]
    public async Task A_rejected_dataset_serves_no_run_over_the_wire()
    {
        var (_, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);
        File.WriteAllText(set.Files[0].Path, "somebody replaced the vendor's archive");

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(("strategy", GivenProgram()), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.MARKET_DATA_UNAVAILABLE), reply.Error!.Code);
        Assert.Contains("REJECTED", reply.Error.Message);
    }

    /// <summary>
    /// THE OWNER'S REPORT, OVER THE SAME PIPE, STOPS SAYING NOTHING HAS BEEN BACKTESTED.
    ///
    /// <para>Section 8's gap line is the one thing in the daily report that said this build measures
    /// nothing. It goes when a run exists — and the run is listed under "measured by TradeAgent",
    /// because the app computed every figure in it from its own trace.</para>
    /// </summary>
    [Fact]
    public async Task The_reports_nothing_has_been_backtested_line_goes_once_a_run_exists()
    {
        var (_, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        var before = await client.SendAsync(new IpcRequest { Op = Ops.Report, Session = "agent-1" });
        Assert.True(before.Ok, Json.Write(before.Error));
        Assert.Contains("nothing has been backtested", Data(before).GetProperty("text").GetString());

        var run = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(("strategy", GivenProgram()), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });
        Assert.True(run.Ok, Json.Write(run.Error));

        var after = await client.SendAsync(new IpcRequest { Op = Ops.Report, Session = "agent-1" });
        Assert.True(after.Ok, Json.Write(after.Error));
        var text = Data(after).GetProperty("text").GetString()!;
        log.WriteLine(text[text.IndexOf("8. Research evidence", StringComparison.Ordinal)..]);

        Assert.DoesNotContain("nothing has been backtested", text);
        Assert.Contains("measured by TradeAgent", text);
        Assert.Contains("backtest ", text);
    }

    /// <summary>
    /// THE OP IS DISPATCHED, IT IS IN THE DRAIN TABLE AT ZERO, AND IT IS NOT MUTATING.
    ///
    /// <para>Zero because it makes no connector call at all — a row is the connector chain and not the
    /// handler — and being IN the table is what makes it covered by the shutdown derivation rather than
    /// invisible to it.</para>
    /// </summary>
    [Fact]
    public async Task The_backtest_op_is_dispatched_and_is_in_the_deadline_table_at_zero()
    {
        var (_, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        // Dispatched: a request with no arguments is REFUSED, not unknown.
        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Backtest, Session = "agent-1" });
        Assert.False(reply.Ok);
        Assert.DoesNotContain("unknown operation", reply.Error!.Message);
        Assert.Contains("'strategy' is required", reply.Error.Message);

        var row = Assert.Single(((GatewayPipeServer)server).HandlerPaths, p => p.Handler == Ops.Backtest);
        Assert.Equal(TimeSpan.Zero, row.Path);
        Assert.False(Ops.IsMutating(Ops.Backtest));
    }

    /// <summary>
    /// A LAUNCH READS ITS OWN ROLE'S FOLDER, AND ITS RUN IS RECORDED AS ITS OWN.
    ///
    /// <para>The same relative name — <c>strategies/shared-name.strategy</c> — exists in BOTH homes and
    /// holds a DIFFERENT program in each. A Research launch asking for it must get the Research copy and
    /// a run attributed to Research. Before the caller's identity reached the runner, the resolver tried
    /// the chair's home first and won there: the chair's program was run and the run was recorded as the
    /// chair's, which is a result about a program Research never wrote appearing in its lineage.</para>
    ///
    /// <para>The attempt comes from the grant too, so a row can be traced to the launch that asked for
    /// it rather than only to a role.</para>
    /// </summary>
    [Fact]
    public async Task A_research_launch_reads_its_own_copy_of_a_name_both_roles_have_and_the_run_is_its_own()
    {
        var (gw, db, client, server, _) = await Connected(CouncilRoles.Research, "attempt-r1");
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        // The chair's copy enters above 103; Research's enters above 104. Two programs, two ids.
        GivenProgram(Text, "shared-name.strategy", CouncilRoles.Operations);
        var researchText = Text.Replace("close > 103", "close > 104");
        GivenProgram(researchText, "shared-name.strategy", CouncilRoles.Research);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "research",
            Args = Args(("strategy", "strategies/shared-name.strategy"),
                ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = Data(reply);
        log.WriteLine(Json.Write(reply.Data));

        Assert.Equal(CouncilRoles.Research, data.GetProperty("role").GetString());

        // THE PROGRAM IT RAN IS RESEARCH'S OWN, by its id: the chair's copy is a different version.
        var chairs = StrategyParser.Parse(Text).Program!.StrategyId;
        var researchs = StrategyParser.Parse(researchText).Program!.StrategyId;
        Assert.NotEqual(chairs, researchs);
        Assert.Equal(researchs, data.GetProperty("version_id").GetString());

        var version = gw.Strategies.VersionById(researchs);
        Assert.NotNull(version);
        Assert.Equal(CouncilRoles.Research, version.Role);
        Assert.Equal("attempt-r1", version.Attempt);

        var run = gw.Strategies.RunById(data.GetProperty("run_id").GetString()!);
        Assert.NotNull(run);
        Assert.Equal(CouncilRoles.Research, run.Role);
        Assert.Equal("attempt-r1", run.Attempt);

        // And the chair's version was never even parsed: nothing of the other role is in the ledger.
        Assert.Null(gw.Strategies.VersionById(chairs));
    }

    /// <summary>
    /// THE OTHER ROLE'S FOLDER IS OUTSIDE YOURS, and is refused exactly as the database is.
    ///
    /// <para>A role home is not a shared library. Research reaching into the chair's folder would be
    /// reading work it was not given, and the refusal says which folder the program has to be in.</para>
    /// </summary>
    [Fact]
    public async Task A_launch_cannot_read_a_program_out_of_the_other_roles_folder()
    {
        var (gw, db, client, server, _) = await Connected(CouncilRoles.Research, "attempt-r2");
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);

        GivenProgram(Text, "chairs-own.strategy", CouncilRoles.Operations);
        var chairsPath = Path.Combine(Paths.RoleHome(CouncilRoles.Operations), "strategies", "chairs-own.strategy");

        foreach (var path in new[] { chairsPath, "../agent/strategies/chairs-own.strategy" })
        {
            var reply = await client.SendAsync(new IpcRequest
            {
                Op = Ops.Backtest, Session = "research",
                Args = Args(("strategy", path), ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
            });

            log.WriteLine(Json.Write(reply.Error));
            Assert.False(reply.Ok);
            Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
            Assert.Contains("outside the Research Director's own folder", reply.Error.Message);
        }

        Assert.Equal(0, gw.Strategies.RunCount);
    }

    /// <summary>
    /// A CALLER THAT PROVED NO LAUNCH IS REFUSED THE OP, and told how a launch proves itself.
    ///
    /// <para>Every process on the agent's side of the fence can read the machine token, so a connection
    /// with no grant is authenticated and is nobody. A run is recorded under a role and reads a role's
    /// folder; there is no honest role to give this caller, and the reading every other table uses — a
    /// row with no role is the chair's — is exactly the one `U-containment` established must not be
    /// applied to a live caller.</para>
    /// </summary>
    [Fact]
    public async Task A_connection_that_proved_no_launch_is_refused_the_backtest()
    {
        var (gw, db, client, server, _) = await Connected(role: null);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenData(db);
        GivenProgram();

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "nobody",
            Args = Args(("strategy", "strategies/over-the-pipe.strategy"),
                ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Contains("launch grant", reply.Error!.Message);
        Assert.Equal(0, gw.Strategies.RunCount);

        // A read it may still do: the refusal is about what this caller can be recorded AS, not about
        // whether it may speak.
        Assert.True((await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "nobody" })).Ok);
    }
}
