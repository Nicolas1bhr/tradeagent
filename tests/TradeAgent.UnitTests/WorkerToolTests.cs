using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — EVERY TOOL IS A GRANT, DEFAULT DENY, AND EVERY CALL IS RECORDED BY THE APP.
///
/// <para><b>The two guards that decide money and privacy.</b> A worker's <c>trade</c> goes through the
/// gateway's OWN handler under the identity TradeAgent assigned, so the Research Director's
/// <c>trade buy</c> is refused by the same line that refuses a Research launch on the pipe — never by a
/// second mechanism written beside it, which is the arrangement that drifts. And a file tool is
/// confined to the role's own folder, so <c>read_file("../operations/PLAN.md")</c> is refused for what
/// it says rather than for where it happens to land.</para>
///
/// <para><b>And the record.</b> One <c>tool_call</c> row per call, served or refused, written by the
/// app at schema 13. It is round 4's "observed deliveries", and the refusals are the half that says
/// whether the grants are doing anything at all.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class WorkerToolTests : IAsyncLifetime
{
    readonly string _root = Path.Combine(TestEnv.Home, $"tools-{Guid.NewGuid():n}");
    Database _db = null!;
    TradingGateway _gw = null!;
    GatewayPipeServer _server = null!;

    string Home(string role) => Path.Combine(_root, CouncilRoles.HomeDir(role));

    public async Task InitializeAsync()
    {
        foreach (var role in CouncilRoles.All)
        {
            Directory.CreateDirectory(Path.Combine(Home(role), CouncilRoles.InDir));
            Directory.CreateDirectory(Path.Combine(Home(role), CouncilRoles.OutDir));
            Directory.CreateDirectory(Path.Combine(Home(role), "trading"));
        }

        var ready = await TestEnv.Ready();
        (_gw, _, _db) = ready;
        // NOT STARTED. The pipe is one transport into the handler and an in-process worker is another;
        // nothing here listens on a pipe, which is the point — the handler is what is shared.
        _server = new GatewayPipeServer(_gw, "not-a-real-token");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _gw.DisposeAsync();
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    const string Attempt = "turn-tools-1";

    GrantedWorkerTools Tools(string role, ToolCallStore? ledger = null) =>
        new(role, () => Home(role), () => Attempt, () => _server, ledger);

    static ToolRequest Call(string tool, object args, string id = "c1") =>
        new(id, tool, Json.Write(args));

    // ---- what a backtest needs, as `BacktestRequestTests` builds it -------------------------------
    //
    // A PROGRAM IN THE ROLE'S OWN MANAGED HOME, not in this class's `_root`: the file tools are
    // confined to the home this surface was handed, and the `backtest` op resolves the program against
    // `Paths.RoleHome(ctx.Role)` — the app's own answer for that role, which no argument can move. In
    // the product the two are the same directory; here they differ, which is exactly why the path is
    // passed THROUGH and the confinement is the gateway's.

    static readonly DateTimeOffset BarsFrom = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Program = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    static string GivenProgram(string role, string name = "worker-asked.strategy")
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), Program);
        return $"strategies/{name}";
    }

    /// <summary>A dataset the ledger really recorded, with a sawtooth that crosses 103 every ten minutes.</summary>
    DatasetRecord GivenData(int bars = 120, string pair = "BTCUSDT")
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
                $"{BarsFrom.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, BarsFrom, BarsFrom.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, BarsFrom, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, BarsFrom, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(_db).Record(record) };
    }

    // ---- the trade tool ---------------------------------------------------------------------------

    /// <summary>
    /// THE ROLE DECIDES WHETHER MONEY MOVES, and the harness inherits that decision rather than
    /// repeating it. Research reads and is refused an order; the chair's own order is placed.
    ///
    /// Watch <c>GatewayPipeServer.Handle</c>'s <c>IsMutating(req.Op) &amp;&amp; !ctx.MayPlaceOrders</c>
    /// check go away and the first half of this goes red: the Research worker's buy is served.
    /// </summary>
    [Fact]
    public async Task Research_may_read_the_trading_surface_and_may_not_place_an_order()
    {
        var research = Tools(CouncilRoles.Research);

        var read = await research.InvokeAsync(Call(Trade, new { op = Ops.Status }));
        Assert.True(read.Served, read.Content);
        Assert.Contains("\"mode\"", read.Content);

        var buy = await research.InvokeAsync(
            Call(Trade, new { op = Ops.Buy, symbol = "ES", quantity = 1 }));

        Assert.False(buy.Served);
        Assert.Contains(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), buy.Content);
        Assert.Contains("may not", buy.Content);

        // AND THE SAME CALL FROM THE CHAIR IS SERVED, which is what makes the refusal above a role
        // check rather than a harness that cannot trade at all.
        var chair = Tools(CouncilRoles.Operations);
        var placed = await chair.InvokeAsync(
            Call(Trade, new { op = Ops.Buy, symbol = "ES", quantity = 1, request_id = "w-chair-1" }));

        Assert.True(placed.Served, placed.Content);
        Assert.Contains("w-chair-1", placed.Content);
    }

    /// <summary>
    /// A MUTATING OP WITH NO REQUEST ID IS STILL IDEMPOTENT, because the app mints one. The gateway
    /// refuses a frame that names none, and a worker that forgot would otherwise lose the turn's order.
    /// </summary>
    [Fact]
    public async Task An_order_with_no_request_id_of_its_own_is_sent_under_one_the_app_minted()
    {
        var chair = Tools(CouncilRoles.Operations);
        var placed = await chair.InvokeAsync(Call(Trade, new { op = Ops.Buy, symbol = "ES", quantity = 1 }));

        Assert.True(placed.Served, placed.Content);
        using var body = JsonDocument.Parse(placed.Content);
        var id = body.RootElement.GetProperty("request_id").GetString();
        Assert.StartsWith($"w-{CouncilRoles.Operations}-", id);
        // Never the prefix the gateway mints for its own sweep legs, which it refuses outright.
        Assert.DoesNotContain("op-", id);
    }

    /// <summary>
    /// A RESEARCH WORKER MAY ASK FOR A BACKTEST OF A PROGRAM IN ITS OWN HOME — <c>U-harness-loop</c>
    /// item 3, and the one measurement the runner exists to give it.
    ///
    /// <para><b>Why it belongs on the closed list.</b> That list says which operations <c>trade</c>
    /// CARRIES; the gateway's own role check says who may use them. <c>backtest</c> is READ-ONLY for the
    /// gateway — it touches no connector, reads no mode and could change none — and it runs under the
    /// caller's own launch identity, so the run is recorded under the role that asked and reads only
    /// that role's folder. Leaving it off meant a Research worker on the harness could not ask for the
    /// figure the whole research loop is about, and its alternative was to describe an unmeasured result
    /// in a report.</para>
    ///
    /// <para><b>The mutant is the other way to "add" it:</b> put <c>backtest</c> into
    /// <see cref="Ops.Mutating"/> and the pipe's own role check refuses Research with
    /// <c>ROLE_MAY_NOT_TRADE</c> — the op would exist and the one role that needs it could never call
    /// it. That word on this channel means "sends something to a broker", and nothing here does.</para>
    /// </summary>
    [Fact]
    public async Task Research_may_ask_for_a_backtest_of_a_program_in_its_own_home()
    {
        var set = GivenData();
        var ledger = new ToolCallStore(_db);
        var research = Tools(CouncilRoles.Research, ledger);

        var ran = await research.InvokeAsync(Call(Trade, new
        {
            op = Ops.Backtest,
            strategy = GivenProgram(CouncilRoles.Research),
            dataset = set.Id,
            fees = 0.001m,
            // Declared, because this fixture's dataset records no venue and TradeAgent will not invent
            // an increment (`VenueIncrementTests`). 1 is what this call used before the catalogue.
            increment = 1m
        }));

        Assert.True(ran.Served, ran.Content);
        using var body = JsonDocument.Parse(ran.Content);

        // UNDER THE WORKER'S OWN LAUNCH, from the identity the app assigned this surface — never from
        // the folder the program happened to be in.
        Assert.Equal(CouncilRoles.Research, body.RootElement.GetProperty("role").GetString());
        var runId = body.RootElement.GetProperty("run_id").GetString()!;
        Assert.Equal(64, runId.Length);                                   // a sha256, not a guid

        var run = _gw.Strategies.RunById(runId);
        Assert.NotNull(run);
        Assert.Equal(Attempt, run!.Attempt);
        Assert.Equal(CouncilRoles.Research, run.Role);
        Assert.Equal(set.NormalisedSha256, run.DatasetSha256);
        Assert.True(run.Trades > 0, "the fixture's sawtooth closed no trade, so this measured nothing");
        Assert.Equal(CouncilRoles.Research,
            _gw.Strategies.VersionById(run.VersionId)!.Role);

        // AND IT IS NOT A MUTATING OP, which is what lets Research reach it at all.
        Assert.DoesNotContain(Ops.Backtest, Ops.Mutating);
        Assert.False(Ops.IsMutating(Ops.Backtest));

        // The call is recorded like every other, by the summary and not by the payload.
        Assert.Contains(ledger.ForAttempt(Attempt),
            r => r.Tool == Trade && r.Argument == $"{Trade} {Ops.Backtest}" && r.Served);

        // ONE RUN AT A TIME PER ROLE IS A GUARD THAT RELEASES: the same ask again is served, which is
        // the half of `Backtests`' own rule this route has to keep working.
        var again = await research.InvokeAsync(Call(Trade, new
        {
            op = Ops.Backtest,
            strategy = GivenProgram(CouncilRoles.Research),
            dataset = set.Id,
            fees = 0.001m,
            increment = 1m
        }, "c2"));
        Assert.True(again.Served, again.Content);
    }

    /// <summary>
    /// OPERATOR AUTHORITY IS NOT ON THIS SURFACE. There is no tool and no op that changes the mode,
    /// lifts the kill switch, activates real money, approves an order or updates the app — and a worker
    /// naming one is refused by the tool's own closed list, before the gateway is even asked.
    /// </summary>
    [Theory]
    [InlineData("set-mode")]
    [InlineData("approve")]
    [InlineData("activate-live")]
    [InlineData("resume-ai")]
    [InlineData("update")]
    [InlineData("hello")]
    public async Task No_operation_on_this_surface_reaches_operator_authority(string op)
    {
        var chair = Tools(CouncilRoles.Operations);
        var answer = await chair.InvokeAsync(Call(Trade, new { op }));

        Assert.False(answer.Served);
        Assert.Contains($"'{op}' is not an operation 'trade' carries", answer.Content);
    }

    /// <summary>Every other tool name is denied by the switch's own default, naming what was asked for.</summary>
    [Theory]
    [InlineData("run_shell")]
    [InlineData("http_get")]
    [InlineData("npm_install")]
    [InlineData("read_File")]
    public async Task A_tool_this_launch_was_not_granted_is_denied_by_name(string tool)
    {
        var research = Tools(CouncilRoles.Research);
        var answer = await research.InvokeAsync(Call(tool, new { }));

        Assert.False(answer.Served);
        Assert.Contains($"'{tool}' is not a tool this launch was granted", answer.Content);
    }

    // ---- the file tools --------------------------------------------------------------------------

    /// <summary>
    /// THE PATH RED. A climb out of the role's own folder is refused for what it SAYS, and the sentence
    /// says which rule refused it rather than "not found" — because a worker told "not found" will try
    /// another spelling all turn.
    ///
    /// Watch the <c>..</c> segment check go out of <c>ToolPaths.Resolve</c> and this goes red with the
    /// other role's plan in the answer.
    /// </summary>
    [Fact]
    public async Task A_path_that_climbs_out_of_the_roles_own_folder_is_refused()
    {
        var chairsPlan = Path.Combine(Home(CouncilRoles.Operations), "trading", "PLAN.md");
        Directory.CreateDirectory(Path.GetDirectoryName(chairsPlan)!);
        File.WriteAllText(chairsPlan, "the chair's own plan");

        var research = Tools(CouncilRoles.Research);
        var answer = await research.InvokeAsync(Call(ReadFile, new { path = "../agent/trading/PLAN.md" }));

        Assert.False(answer.Served);
        Assert.Contains("climbs out of your own folder with '..'", answer.Content);
        Assert.DoesNotContain("the chair's own plan", answer.Content);
    }

    [Theory]
    [InlineData("/etc/hosts", "is an absolute path")]
    [InlineData("~/.ssh/id_rsa", "is an absolute path")]
    [InlineData("a/../../agent/trading/PLAN.md", "climbs out of your own folder")]
    [InlineData("in/../../agent/trading/PLAN.md", "climbs out of your own folder")]
    public void Every_way_out_of_the_folder_is_refused_with_the_rule_that_refused_it(string path, string says)
    {
        var (full, refusal) = ToolPaths.Resolve(Home(CouncilRoles.Research), path);

        Assert.Null(full);
        Assert.Contains(says, refusal);
    }

    /// <summary>
    /// A LINK IS A PATH THAT OBEYS EVERY OTHER RULE AND STILL READS SOMEWHERE ELSE, so it is refused.
    /// Only components BELOW the root are checked — the root's own ancestry is the app's choice, and on
    /// macOS it sits under one.
    /// </summary>
    [Fact]
    public async Task A_link_inside_the_folder_is_refused_rather_than_followed()
    {
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "not yours");
        var link = Path.Combine(Home(CouncilRoles.Research), "shortcut.txt");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A machine that will not create a link cannot exercise the rule. Skipping is honest; the
            // alternative is a test that passes because nothing happened.
            return;
        }

        var research = Tools(CouncilRoles.Research);
        var answer = await research.InvokeAsync(Call(ReadFile, new { path = "shortcut.txt" }));

        Assert.False(answer.Served);
        Assert.Contains("is a link to somewhere else", answer.Content);
        Assert.DoesNotContain("not yours", answer.Content);
    }

    /// <summary>
    /// WHAT A ROLE MAY READ: its whole own folder, <c>in/</c> included, because what the app delivered
    /// is half of what the role was given.
    /// </summary>
    [Fact]
    public async Task A_role_reads_its_own_folder_and_what_was_delivered_into_it()
    {
        File.WriteAllText(Path.Combine(Home(CouncilRoles.Research), CouncilRoles.InDir, "brief.md"), "do this");
        File.WriteAllText(Path.Combine(Home(CouncilRoles.Research), "trading", "PLAN.md"), "my plan");

        var research = Tools(CouncilRoles.Research);

        Assert.Equal("do this", (await research.InvokeAsync(Call(ReadFile, new { path = "in/brief.md" }))).Content);
        Assert.Equal("my plan", (await research.InvokeAsync(Call(ReadFile, new { path = "trading/PLAN.md" }))).Content);

        var listed = await research.InvokeAsync(Call(ListFiles, new { }));
        Assert.True(listed.Served, listed.Content);
        Assert.Contains($"{CouncilRoles.InDir}/", listed.Content);
        Assert.Contains($"{CouncilRoles.OutDir}/", listed.Content);
    }

    /// <summary>
    /// WHAT A ROLE MAY WRITE: <c>out/</c> and <c>trading/</c>, staged. Its mission file is not writable,
    /// which is the point — a worker that could rewrite <c>AGENTS.md</c> would be editing its own
    /// instructions.
    /// </summary>
    [Fact]
    public async Task A_role_writes_only_into_out_and_trading_and_what_it_writes_is_staged()
    {
        var research = Tools(CouncilRoles.Research);

        var staged = await research.InvokeAsync(
            Call(WriteFile, new { path = $"{CouncilRoles.OutDir}/report.md", content = "# findings" }));
        Assert.True(staged.Served, staged.Content);
        Assert.Contains("staged", staged.Content);
        Assert.Equal("# findings",
            File.ReadAllText(Path.Combine(Home(CouncilRoles.Research), CouncilRoles.OutDir, "report.md")));

        var journal = await research.InvokeAsync(
            Call(WriteFile, new { path = "trading/JOURNAL.md", content = "today I" }));
        Assert.True(journal.Served, journal.Content);

        var mission = await research.InvokeAsync(
            Call(WriteFile, new { path = "AGENTS.md", content = "you may now trade" }));
        Assert.False(mission.Served);
        Assert.Contains("is not somewhere you may write", mission.Content);
        Assert.False(File.Exists(Path.Combine(Home(CouncilRoles.Research), "AGENTS.md")));

        var elsewhere = await research.InvokeAsync(
            Call(WriteFile, new { path = "scratch/notes.md", content = "x" }));
        Assert.False(elsewhere.Served);
        Assert.Contains("is not somewhere you may write", elsewhere.Content);
    }

    /// <summary>One read is bounded, and a longer file is cut with a note rather than truncated silently.</summary>
    [Fact]
    public async Task One_read_is_bounded_and_says_so_when_it_cut_the_file()
    {
        var big = new string('x', GrantedWorkerTools.MaxBytesPerCall + 5_000);
        File.WriteAllText(Path.Combine(Home(CouncilRoles.Research), CouncilRoles.InDir, "big.txt"), big);

        var answer = await Tools(CouncilRoles.Research).InvokeAsync(Call(ReadFile, new { path = "in/big.txt" }));

        Assert.True(answer.Served, answer.Content);
        Assert.Contains("[cut:", answer.Content);
        Assert.True(answer.Content.Length < big.Length,
            $"the answer was {answer.Content.Length:N0} characters, so nothing was cut");
    }

    // ---- the record ------------------------------------------------------------------------------

    /// <summary>
    /// EVERY CALL IS A ROW, SERVED OR REFUSED, WRITTEN BY THE APP AT SCHEMA 13 — and the argument is a
    /// SUMMARY rather than the payload, so the ledger never becomes a second copy of the files.
    /// </summary>
    [Fact]
    public async Task Every_call_is_recorded_against_the_attempt_whether_or_not_it_was_served()
    {
        var ledger = new ToolCallStore(_db);
        File.WriteAllText(Path.Combine(Home(CouncilRoles.Research), CouncilRoles.InDir, "brief.md"), "do this");

        var research = Tools(CouncilRoles.Research, ledger);
        await research.InvokeAsync(Call(ReadFile, new { path = "in/brief.md" }));
        await research.InvokeAsync(Call(ReadFile, new { path = "../agent/trading/PLAN.md" }, "c2"));
        await research.InvokeAsync(Call(Trade, new { op = Ops.Buy, symbol = "ES", quantity = 1 }, "c3"));
        await research.InvokeAsync(Call("run_shell", new { command = "ls" }, "c4"));

        var rows = ledger.ForAttempt(Attempt);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(CouncilRoles.Research, r.Role));

        Assert.True(rows[0].Served);
        Assert.Equal($"{ReadFile} in/brief.md", rows[0].Argument);
        Assert.Equal(7, rows[0].Bytes);
        Assert.Null(rows[0].Refusal);

        Assert.False(rows[1].Served);
        Assert.Contains("climbs out", rows[1].Refusal);
        // A SUMMARY, NEVER THE PAYLOAD: the path is here and the file's content is not.
        Assert.Equal($"{ReadFile} ../agent/trading/PLAN.md", rows[1].Argument);

        Assert.False(rows[2].Served);
        Assert.Contains(nameof(ErrorCode.ROLE_MAY_NOT_TRADE), rows[2].Refusal);
        Assert.Equal($"{Trade} {Ops.Buy}", rows[2].Argument);

        Assert.False(rows[3].Served);
        Assert.Equal("run_shell", rows[3].Tool);
    }

    /// <summary>
    /// THE RED FOR THIS ITEM, written against nothing new: the schema this build supports and the table
    /// a fresh database gains. Before the migration there was no <c>tool_call</c> at all, so there was
    /// nowhere for an observed delivery to be recorded and round 4's "unrestricted CLI reads remain
    /// unobserved" was still the whole story.
    ///
    /// <para><b>A FLOOR AND NOT A PIN</b>, which is the shape this unit's own four inherited expectations
    /// were changed to and which its report named as the trap. The table arrives at 13; what a LATER
    /// migration takes the ladder to is that migration's business, and an equality here makes every
    /// additive rung above this one edit a test about the rung below it. `U-referee-1`'s schema 14 was
    /// the next one, and it went red here for exactly that reason. What is asserted instead is what this
    /// test is about: 13 is reached, the table is there, and the number a fresh database is stamped with
    /// is the number this build says it supports.
    /// </summary>
    [Fact]
    public void The_schema_this_build_carries_the_tool_call_ledger_at_thirteen()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 13,
            $"the tool_call ledger needs schema 13 or later; this build says {Versions.DatabaseSchemaVersion}");

        using var fresh = TestEnv.NewDb();
        using var table = fresh.Cmd(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tool_call'");
        Assert.Equal(1L, Convert.ToInt64(table.ExecuteScalar()));

        using var version = fresh.Cmd("SELECT value FROM meta WHERE key='schema_version'");
        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            version.ExecuteScalar() as string);
    }

    /// <summary>
    /// THE LEDGER IS THE APP'S: no pipe op and no verb names it, which is the rule <c>material</c>,
    /// <c>fill</c>, <c>ai_attempt</c>, <c>mission_event</c> and <c>publication</c> already keep.
    /// </summary>
    [Fact]
    public void Nothing_on_the_agent_facing_surface_writes_the_tool_call_ledger()
    {
        using var fresh = TestEnv.NewDb();
        var store = new ToolCallStore(fresh);
        Assert.Empty(store.ForAttempt("nothing"));

        var at = DateTimeOffset.Parse("2026-09-13T10:00:00Z");
        store.Record(new ToolCallRow
        {
            At = at, Attempt = "a1", Role = CouncilRoles.Research, Tool = ReadFile,
            Argument = "read_file in/brief.md", Bytes = 7, Served = true
        });
        var row = Assert.Single(store.ForAttempt("a1"));
        Assert.Equal(at, row.At);
        Assert.True(row.Served);

        // Nothing on the agent-facing surface reaches it: not an op the pipe serves, and not a tool.
        Assert.DoesNotContain("tool_call", string.Join(" ", GatewaySchema.Ops().Select(o => o.Op + " " + o.Cli)));
        Assert.DoesNotContain("tool_call", string.Join(" ", GrantedWorkerTools.TradeOps));
    }

    // ---- the data and report tools ---------------------------------------------------------------

    /// <summary>
    /// <c>data</c> and <c>report</c> carry their own two and one ops and nothing else, so neither can be
    /// talked into a trading operation by an argument.
    /// </summary>
    [Fact]
    public async Task The_data_and_report_tools_carry_only_their_own_reads()
    {
        var research = Tools(CouncilRoles.Research);

        var list = await research.InvokeAsync(Call(Data, new { op = Ops.DataList }));
        Assert.True(list.Served, list.Content);

        var today = await research.InvokeAsync(Call(Report, new { }));
        Assert.True(today.Served, today.Content);

        foreach (var (tool, op) in new[] { (Data, Ops.Buy), (Report, Ops.DataBars), (Data, Ops.Report) })
        {
            var answer = await research.InvokeAsync(Call(tool, new { op }));
            Assert.False(answer.Served);
            Assert.Contains($"is not an operation '{tool}' carries", answer.Content);
        }
    }

    /// <summary>Arguments that are not readable JSON run nothing, and say so rather than guessing.</summary>
    [Fact]
    public async Task Arguments_that_are_not_readable_json_run_nothing()
    {
        var research = Tools(CouncilRoles.Research);

        var broken = await research.InvokeAsync(new ToolRequest("c1", ReadFile, "{ \"path\": "));
        Assert.False(broken.Served);
        Assert.Contains("not JSON this build could read", broken.Content);

        var notAnObject = await research.InvokeAsync(new ToolRequest("c2", ReadFile, "\"in/brief.md\""));
        Assert.False(notAnObject.Served);
        Assert.Contains("not a JSON object", notAnObject.Content);
    }

    const string Trade = GrantedWorkerTools.Trade;
    const string Data = GrantedWorkerTools.Data;
    const string Report = GrantedWorkerTools.Report;
    const string ReadFile = GrantedWorkerTools.ReadFile;
    const string ListFiles = GrantedWorkerTools.ListFiles;
    const string WriteFile = GrantedWorkerTools.WriteFile;
}
