using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE RESEARCH LEDGER'S FOUR VERBS, OVER THE WIRE (<c>U-research-ledger</c>, item 2): <c>ledger-add</c>,
/// <c>ledger-revise</c>, <c>ledger-list</c> and <c>ledger-show</c> — a role's beliefs written as its own claims under the
/// role and attempt its launch grant proved, read by every role, and never a measurement.
///
/// <para><b>What these hold.</b> The agent channel cannot write a measurement or a link: a "measured" mark and every
/// argument a verb does not declare — <c>link</c>, <c>run</c>, <c>version</c>, <c>promotion</c> — are refused in words with
/// nothing written, and the one writer of <c>ledger_link</c> is the app's. A confidence the author did not state is
/// recorded and read back as unknown, never refused, never defaulted and never carried forward. Every verb reaches
/// every live pair — the command line, the harness's <c>trade</c> tool — and every read is bounded and says where it
/// stopped. These tests speak the wire by hand, as <c>HoldoutOverPipeTests</c> does.</para>
/// </summary>
public class ResearchLedgerOverPipeTests(ITestOutputHelper log)
{
    // ---- the rig -----------------------------------------------------------------------------------------------------

    /// <summary>The wire by hand: the machine's key on every frame, the grant on the hello.</summary>
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

        /// <summary>The bytes of the last answer's <c>data</c>, exactly as the wire carried them — escaping included.</summary>
        public int LastDataBytes { get; private set; }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            using (var doc = JsonDocument.Parse(line))
                LastDataBytes = doc.RootElement.TryGetProperty("data", out var data) ? Encoding.UTF8.GetByteCount(data.GetRawText()) : 0;
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, AgentGrants Grants, string Pipe) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string? role, string attempt = "attempt-ledger")
        {
            var client = await Raw.ConnectAsync(Pipe);
            var grant = role is null ? null : Grants.Issue(role, attempt).Token;
            var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = "ta-ledger-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        return new Rig(gw, db, server, grants, pipe);
    }

    static Dictionary<string, JsonElement> Args(params (string Key, object? Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
            if (value is not null) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static IpcRequest Frame(string op, params (string Key, object? Value)[] args) => new()
    {
        Op = op, Session = "ledger", RequestId = "ledger-" + Guid.NewGuid().ToString("n")[..12], Args = Args(args)
    };

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static long EntryOf(IpcResponse r)
    {
        Assert.True(r.Ok, Json.Write(r.Error));
        return Data(r).GetProperty("entry").GetInt64();
    }

    static void Refused(IpcResponse r, params string[] words)
    {
        Assert.False(r.Ok, "answered: " + Json.Write(r.Data));
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), r.Error!.Code);
        foreach (var w in words) Assert.Contains(w, r.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>How many rows each ledger table holds, in one line.</summary>
    static string Counts(Database db) => string.Join(" ", new[] { "ledger_entry", "ledger_revision", "ledger_link" }
        .Select(t => $"{t}={Scalar(db, $"SELECT COUNT(*) FROM {t}")}"));

    static string? Scalar(Database db, string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ps);
        var v = c.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    });

    static string RepoRoot() => Build.RepoRoot;

    // ---- (a) a measured mark or a link the agent wrote itself is refused ---------------------------------------------

    /// <summary>
    /// (a) A MEASURED MARK OR A LINK THE AGENT WROTE ITSELF IS REFUSED (R22 § 6). <c>--mark measured</c> is refused in
    /// words saying nothing written here is a measurement; <c>link</c>, <c>run</c>, <c>version</c>, <c>promotion</c> and the
    /// link's own column names as arguments of <c>ledger-add</c> or <c>ledger-revise</c> are refused naming what the verb
    /// takes and whose a link is — with nothing written, on the pipe and on the harness's door alike. No ledger verb takes
    /// an argument that names a record, no op is named for a link, and the writer scan finds every ledger table written by
    /// the store alone, <c>ledger_link</c> by <c>LedgerLinks</c> alone, and that type built by the gateway alone.
    /// </summary>
    [Fact]
    public async Task A_measured_mark_or_a_link_the_agent_wrote_itself_is_refused()
    {
        await using var rig = await Ready();
        await using var wire = await rig.Dial(CouncilRoles.Research);

        var entry = EntryOf(await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "hypothesis"),
            ("text", "breakouts above the 20-bar high pay after costs"), ("mark", "hypothesis"))));
        var before = Counts(rig.Db);
        Assert.Equal("ledger_entry=1 ledger_revision=1 ledger_link=0", before);

        // THE MARK THAT WOULD MAKE A CLAIM A MEASUREMENT, ON BOTH WRITES.
        foreach (var mark in new[] { "measured", "observed", "Measured" })
        {
            Refused(await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "finding"), ("text", "it paid"), ("mark", mark))),
                $"'{mark}' is not a mark", "nothing you write in the ledger is a measurement");
            Refused(await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "it paid"), ("mark", mark),
                ("why", "the run said so"))), $"'{mark}' is not a mark");
        }

        // AND A LINK, UNDER ANY NAME, ON BOTH WRITES AND BOTH DOORS.
        foreach (var name in new[] { "link", "run", "version", "promotion", "record_kind", "record_id", "links" })
        {
            Refused(await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "finding"), ("text", "it paid"), ("mark", "claim"),
                (name, "0123456789abcdef"))), $"'{name}' is not an argument of 'ledger-add'",
                "A link between an entry and a run or a verdict is TradeAgent's alone to write", "Nothing was written.");
            Refused(await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "it paid"), ("mark", "claim"),
                ("why", "the run said so"), (name, "0123456789abcdef"))), $"'{name}' is not an argument of 'ledger-revise'");
            Refused(await rig.Server.CallAsync(Frame(Ops.LedgerAdd, ("kind", "finding"), ("text", "it paid"),
                ("mark", "claim"), (name, "0123456789abcdef")), CouncilRoles.Research, "attempt-harness"),
                $"'{name}' is not an argument of 'ledger-add'");
        }
        Assert.Equal(before, Counts(rig.Db));

        // NO LEDGER VERB TAKES AN ARGUMENT THAT NAMES A RECORD, AND NO OP IS NAMED FOR A LINK.
        var ledgerOps = new[] { Ops.LedgerAdd, Ops.LedgerRevise, Ops.LedgerList, Ops.LedgerShow };
        foreach (var spec in GatewaySchema.Ops().Where(o => ledgerOps.Contains(o.Op)))
            Assert.DoesNotContain(spec.Args, a => Regex.IsMatch(a.Name, "link|run|version|promotion|record|deployment|measure"));
        Assert.DoesNotContain(GatewaySchema.Ops(), o => o.Op.Contains("link", StringComparison.OrdinalIgnoreCase));

        // THE WRITER SCAN: each ledger table is written in one file, and `ledger_link` by one type built in one place.
        var root = RepoRoot();
        var sources = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText);
        foreach (var table in new[] { "ledger_entry", "ledger_revision", "ledger_link" })
            Assert.Equal(["src/TradeAgent.Core/Db/ResearchLedger.cs"], sources
                .Where(s => Regex.IsMatch(s.Value, $@"\b(INSERT|REPLACE)\s+(OR\s+\w+\s+)?INTO\s+{table}\b", RegexOptions.IgnoreCase))
                .Select(s => s.Key).Order(StringComparer.Ordinal));
        var store = sources["src/TradeAgent.Core/Db/ResearchLedger.cs"];
        var links = store.IndexOf("public sealed class LedgerLinks", StringComparison.Ordinal);
        Assert.True(links > 0, "LedgerLinks is not in the store's file");
        var insert = Assert.Single(Regex.Matches(store, @"INSERT\s+INTO\s+ledger_link\b"));
        Assert.True(insert.Index > links, "ledger_link is inserted outside LedgerLinks");
        Assert.Equal(["src/TradeAgent.Gateway/TradingGateway.cs"], sources
            .Where(s => s.Value.Contains("new LedgerLinks(", StringComparison.Ordinal)).Select(s => s.Key));

        // AND `Link` IS CALLED FROM THE TWO RECORD PATHS ALONE (item 3), read off the compiled bodies — a lambda's
        // under the method that wrote it: the run's own write, and the verdict's — its promotion write's callback and the
        // answer of one already recorded.
        Assert.Equal(["Backtests.Record", "GatewayPipeServer.VerdictFor"], CallersOf(
            typeof(LedgerLinks).GetMethod(nameof(LedgerLinks.Link))!,
            typeof(ResearchLedger).Assembly, typeof(GatewayPipeServer).Assembly, typeof(GrantedWorkerTools).Assembly));
    }

    /// <summary>
    /// EVERY METHOD IN THESE ASSEMBLIES WHOSE COMPILED BODY CALLS <paramref name="target"/>, as <c>Type.Method</c> — a
    /// compiler-made type or lambda named by the type and the method it was written in.
    /// </summary>
    static List<string> CallersOf(MethodInfo target, params Assembly[] assemblies)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                                 | BindingFlags.DeclaredOnly;
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()))
            foreach (var m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (m.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
                for (var i = 0; i < il.Length - 4; i++)
                {
                    if (il[i] is not (0x28 or 0x6F)) continue;   // call, callvirt
                    MethodBase? called = null;
                    try { called = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); }
                    catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or TypeLoadException) { }
                    if (called is not null && called.MetadataToken == target.MetadataToken && called.Module == target.Module)
                        callers.Add($"{Outermost(type).Name}.{Written(m.Name, type)}");
                }
            }
        return [.. callers];

        static Type Outermost(Type t) => t.DeclaringType is { } outer ? Outermost(outer) : t;

        static string Written(string name, Type type) =>
            Regex.Match(name, "^<([^>]+)>") is { Success: true } lambda ? lambda.Groups[1].Value
            : Regex.Match(type.Name, "^<([^>]+)>") is { Success: true } state ? state.Groups[1].Value
            : name;
    }

    // ---- (b) a confidence the agent did not state is recorded as unknown ---------------------------------------------

    /// <summary>
    /// (b) A CONFIDENCE THE AGENT DID NOT STATE IS RECORDED AS UNKNOWN, NEVER REFUSED (R22 § 6). An add and a revise
    /// without one are NULL on disk and read back <c>unknown</c> — on the write's answer, on <c>ledger-show</c>'s
    /// revisions and on <c>ledger-list</c> — and a revise after a stated 0.3 does not carry it forward. 0.3 is kept as
    /// stated, the word <c>unknown</c> a read prints is taken as unstated, and 1.7 and -0.2 are refused with nothing
    /// written: a number outside 0 to 1 is not clamped into a certainty nobody stated.
    /// </summary>
    [Fact]
    public async Task A_confidence_the_agent_did_not_state_is_recorded_as_unknown_never_refused()
    {
        await using var rig = await Ready();
        await using var wire = await rig.Dial(CouncilRoles.Operations);

        var added = await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "lesson"), ("text", "fills are the app's record"),
            ("mark", "claim")));
        var entry = EntryOf(added);
        Assert.Equal("unknown", Data(added).GetProperty("confidence").GetString());

        var stated = await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "fills are the app's record"),
            ("mark", "claim"), ("why", "I can say now"), ("confidence", 0.3m)));
        Assert.True(stated.Ok, Json.Write(stated.Error));
        Assert.Equal("0.3", Data(stated).GetProperty("confidence").GetString());

        var unstated = await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "fills are the app's record"),
            ("mark", "assumption"), ("why", "I am less sure")));
        Assert.True(unstated.Ok, Json.Write(unstated.Error));
        Assert.Equal("unknown", Data(unstated).GetProperty("confidence").GetString());

        var word = await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "fills are the app's record"),
            ("mark", "assumption"), ("why", "as the read said"), ("confidence", "unknown")));
        Assert.True(word.Ok, Json.Write(word.Error));
        Assert.Equal("unknown", Data(word).GetProperty("confidence").GetString());

        Assert.Equal("NULL 0.3 NULL NULL", string.Join(" ", rig.Db.Read(_ =>
        {
            using var c = rig.Db.Cmd("SELECT confidence FROM ledger_revision WHERE entry_id=$e ORDER BY revision", ("$e", entry));
            using var rd = c.ExecuteReader();
            var all = new List<string>();
            while (rd.Read()) all.Add(rd.IsDBNull(0) ? "NULL" : rd.GetString(0));
            return all;
        })));

        var shown = await wire.SendAsync(Frame(Ops.LedgerShow, ("entry", entry)));
        Assert.True(shown.Ok, Json.Write(shown.Error));
        Assert.Equal(["unknown", "unknown", "0.3", "unknown"],
            Data(shown).GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("confidence").GetString()));

        var listed = await wire.SendAsync(Frame(Ops.LedgerList));
        Assert.True(listed.Ok, Json.Write(listed.Error));
        Assert.Equal("unknown", Data(listed).GetProperty("entries")[0].GetProperty("confidence").GetString());

        // STATED, AS STATED; OUT OF RANGE, REFUSED WITH NOTHING WRITTEN.
        var kept = await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "hypothesis"), ("text", "a second idea"),
            ("mark", "hypothesis"), ("confidence", "0.3")));
        Assert.Equal("0.3", Data(kept).GetProperty("confidence").GetString());
        var counts = Counts(rig.Db);
        foreach (var bad in new object[] { 1.7m, "1.7", -0.2m })
        {
            Refused(await wire.SendAsync(Frame(Ops.LedgerAdd, ("kind", "hypothesis"), ("text", "sure"), ("mark", "claim"),
                ("confidence", bad))), "is not a confidence: it is a number from 0 to 1", "recorded as unknown");
            Refused(await wire.SendAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "sure"), ("mark", "claim"),
                ("why", "certain now"), ("confidence", bad))), "is not a confidence");
        }
        Assert.Equal(counts, Counts(rig.Db));
    }

    // ---- (h) every ledger verb reaches every live pair ---------------------------------------------------------------

    /// <summary>The pairs that run: both directors on a command line, the Research Director on the harness.</summary>
    static readonly (string Role, RuntimeClass Runtime)[] LivePairs =
    [
        (CouncilRoles.Operations, RuntimeClass.Cli), (CouncilRoles.Research, RuntimeClass.Cli),
        (CouncilRoles.Research, RuntimeClass.Harness)
    ];

    /// <summary>
    /// (h) EVERY LEDGER VERB REACHES EVERY LIVE PAIR. The four ops are reads and writes of claims and none of them is in
    /// <see cref="Ops.Mutating"/>; each is in the schema under its own form — so <see cref="CanonRules.CommandWords"/>
    /// gives each its own words — with its positionals first in the form's order, in the drain table at zero, on the
    /// harness's <c>trade</c> tool, and in the reach of every pair that runs: both directors on a command line, the
    /// Research Director on the harness. The real CLI on the default pipe adds, revises, lists and shows, sends a flag the
    /// verb does not take on to be refused rather than dropping it, and the harness's <c>trade</c> tool does the same; a
    /// connection with no launch grant reads and is refused a write.
    /// </summary>
    [Fact]
    public async Task Every_ledger_verb_reaches_every_live_pair()
    {
        var forms = new Dictionary<string, (string Cli, string[] Args)>
        {
            [Ops.LedgerAdd] = ("trade ledger add <kind> <text> --mark M [--confidence P] [--about E] [--source S]",
                ["kind", "text", "mark", "confidence", "about", "source"]),
            [Ops.LedgerRevise] = ("trade ledger revise <entry> <text> --mark M --why W [--confidence P] [--status S]",
                ["entry", "text", "mark", "why", "confidence", "status"]),
            [Ops.LedgerList] = ("trade ledger list [--author R] [--kind K] [--status S] [--limit N] [--before E]",
                ["author", "kind", "status", "limit", "before"]),
            [Ops.LedgerShow] = ("trade ledger show <entry> [--part P] [--before C]", ["entry", "part", "before"])
        };
        Assert.Equal(["ledger-add", "ledger-revise", "ledger-list", "ledger-show"], forms.Keys);

        await using (var rig = await Ready())
        {
            foreach (var (op, (cli, args)) in forms)
            {
                Assert.False(Ops.IsMutating(op));
                var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == op);
                Assert.False(spec.Mutating);
                Assert.Equal(cli, spec.Cli);
                Assert.Equal(args, spec.Args.Select(a => a.Name));
                Assert.Equal(["trade", "ledger", op["ledger-".Length..]], CanonRules.CommandWords(spec.Cli));
                Assert.Contains(op, GrantedWorkerTools.TradeOps);
                Assert.Equal(TimeSpan.Zero, rig.Server.HandlerPaths.Single(h => h.Handler == op).Path);

                foreach (var (role, runtime) in LivePairs)
                    Assert.True(AgentReach.For(role, runtime).Verb(op) is not null, $"{role}·{runtime} cannot reach {op}");
            }

            // AND EVERY LIVE PAIR IS TOLD OF THEM (canon v2, item 4): its canon names the ledger's write, through a slot that
            // renders for its runtime and throws for a verb it cannot reach, and its guide names all four.
            Assert.Equal(2, Canon.Version);
            foreach (var (role, runtime) in LivePairs)
                foreach (var simulator in new[] { true, false })
                {
                    var ctx = new WorkspaceContext("Simulator (built in)", ConnectorIsPaper: true, null, TradingMode.PAPER, true,
                        null, new RiskPolicy(), ConnectorIsBuiltInSimulator: simulator, Role: role);
                    var reach = AgentReach.For(role, runtime);
                    var canon = Canon.Render(ctx, reach);
                    Assert.Contains(Ops.LedgerAdd, canon.Verbs);
                    Assert.Contains("none of it is a measurement", canon.Text, StringComparison.Ordinal);
                    Assert.Equal(forms.Keys.Order(StringComparer.Ordinal),
                        Canon.Guide(ctx, reach).Verbs.Where(forms.ContainsKey).Distinct().Order(StringComparer.Ordinal));
                }
            Assert.Contains("It is YOUR CLAIM and is stored as one", Spec(Ops.LedgerAdd).Description, StringComparison.Ordinal);
            Assert.Contains("THE LINK IS TRADEAGENT'S", Spec(Ops.LedgerAdd).Description, StringComparison.Ordinal);
            Assert.Contains("A LINK is TRADEAGENT'S", Spec(Ops.LedgerShow).Description, StringComparison.Ordinal);

            // A CONNECTION THAT PROVED NO ROLE READS, AND IS REFUSED A WRITE.
            await using var nobody = await rig.Dial(null);
            Assert.True((await nobody.SendAsync(Frame(Ops.LedgerList))).Ok);
            Refused(await nobody.SendAsync(Frame(Ops.LedgerAdd, ("kind", "lesson"), ("text", "x"), ("mark", "claim"))),
                "this connection presented no launch grant");

            // THE HARNESS'S `trade` TOOL, AS THE RESEARCH DIRECTOR.
            var tools = new GrantedWorkerTools(CouncilRoles.Research, () => Paths.RoleHome(CouncilRoles.Research),
                () => "attempt-harness", () => rig.Server);
            var wrote = await tools.InvokeAsync(new ToolRequest("t1", GrantedWorkerTools.Trade,
                """{"op":"ledger-add","kind":"experiment","text":"run the breakout over August","mark":"hypothesis","confidence":0.6}"""));
            Assert.True(wrote.Served, wrote.Content);
            var harnessEntry = JsonDocument.Parse(wrote.Content).RootElement.GetProperty("entry").GetInt64();
            Assert.Equal("research|attempt-harness", Scalar(rig.Db,
                "SELECT author || '|' || attempt FROM ledger_entry WHERE id=$e", ("$e", harnessEntry)));
            foreach (var call in new[]
                     {
                         $$"""{"op":"ledger-revise","entry":{{harnessEntry}},"text":"over July too","mark":"hypothesis","why":"one month is thin"}""",
                         """{"op":"ledger-list","author":"research"}""", $$"""{"op":"ledger-show","entry":{{harnessEntry}}}""",
                         $$"""{"op":"ledger-show","entry":{{harnessEntry}},"part":"about","before":{{harnessEntry}}}"""
                     })
            {
                var answer = await tools.InvokeAsync(new ToolRequest("t2", GrantedWorkerTools.Trade, call));
                Assert.True(answer.Served, answer.Content);
            }
            var linked = await tools.InvokeAsync(new ToolRequest("t3", GrantedWorkerTools.Trade,
                """{"op":"ledger-add","kind":"finding","text":"it paid","mark":"claim","run":"0123456789ab"}"""));
            Assert.False(linked.Served);
            Assert.Contains("'run' is not an argument of 'ledger-add'", linked.Content, StringComparison.Ordinal);
        }

        // THE REAL CLI, ON THE DEFAULT PIPE, AS THE ASSEMBLY'S OWN LAUNCH OF THE OPERATIONS DIRECTOR.
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var cliServer = new GatewayPipeServer(gw, IpcToken.Ensure());
        cliServer.Start();

        var add = await Build.RunTradeAsync("ledger", "add", "lesson", "a", "fill", "is", "the", "app's", "record",
            "--mark", "claim", "--source", "attempt 3", "--json");
        Assert.True(add.Code == 0, add.Err + add.Out);
        long cliEntry;
        using (var doc = JsonDocument.Parse(add.Out))
        {
            var data = doc.RootElement.GetProperty("data");
            cliEntry = data.GetProperty("entry").GetInt64();
            Assert.Equal("operations", data.GetProperty("author").GetString());
            Assert.Equal("unknown", data.GetProperty("confidence").GetString());
        }
        Assert.Equal("a fill is the app's record", Scalar(db, "SELECT text FROM ledger_revision WHERE entry_id=$e", ("$e", cliEntry)));

        var id = cliEntry.ToString(CultureInfo.InvariantCulture);
        foreach (var argv in new[]
                 {
                     new[] { "ledger", "revise", id, "a", "fill", "is", "only", "the", "app's", "--mark", "claim", "--why", "said better", "--confidence", "0.9", "--json" },
                     ["ledger", "list", "--author", "operations", "--json"],
                     ["ledger", "show", id, "--json"],
                     ["ledger", "show", id, "--part", "links", "--before", "1", "--json"]
                 })
        {
            var ran = await Build.RunTradeAsync(argv);
            Assert.True(ran.Code == 0, string.Join(' ', argv) + ": " + ran.Err + ran.Out);
        }
        Assert.Equal("2|a fill is only the app's|0.9", Scalar(db,
            "SELECT revision || '|' || text || '|' || confidence FROM ledger_revision WHERE entry_id=$e AND revision=2", ("$e", cliEntry)));

        var smuggled = await Build.RunTradeAsync("ledger", "add", "finding", "it", "paid", "--mark", "claim", "--run", "0123", "--json");
        Assert.Equal(1, smuggled.Code);
        using (var doc = JsonDocument.Parse(smuggled.Out))
            Assert.Contains("'run' is not an argument of 'ledger-add'",
                doc.RootElement.GetProperty("error").GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal("1", Scalar(db, "SELECT COUNT(*) FROM ledger_entry"));

        static GatewaySchema.OpSpec Spec(string op) => GatewaySchema.Ops().Single(o => o.Op == op);
    }

    // ---- (i) a ledger read is bounded and says where it stopped -----------------------------------------------------

    /// <summary>
    /// (i) A LEDGER READ IS BOUNDED AND SAYS WHERE IT STOPPED. A list serves at most 100 entries newest first and at most
    /// 64 KiB of them, never splitting one; an answer either bound stopped says which in <c>capped_by</c> and hands back
    /// the <c>next_before</c> that continues it exactly, so the pages put together are every entry once, in order. A show
    /// does the same for revisions, with <c>next</c>. A limit outside 1 to 100 is refused, never clamped; a filter outside
    /// its vocabulary is refused rather than answered with nothing. The bound is on the WHOLE answer's data as the wire
    /// carries it, escaping included — measured on the raw line, with no tolerance — and it holds for an entry at every
    /// maximum with 100 links and 100 entries about it, page after page.
    /// </summary>
    [Fact]
    public async Task A_ledger_read_is_bounded_and_says_where_it_stopped()
    {
        await using var rig = await Ready();
        await using var wire = await rig.Dial(CouncilRoles.Research);

        var ids = new List<long>();
        for (var i = 0; i < 105; i++)
            ids.Add(rig.Gw.Ledger.Add(CouncilRoles.Research, "attempt-i", LedgerKind.Lesson, $"lesson {i}", LedgerMark.Claim,
                null, null, null).Id);
        ids.Reverse();

        // THE ENTRY BOUND.
        var first = Data(await wire.SendAsync(Frame(Ops.LedgerList, ("kind", "lesson"))));
        Assert.Equal((100, true, "limit"), (first.GetProperty("count").GetInt32(), first.GetProperty("more").GetBoolean(),
            first.GetProperty("capped_by").GetString()));
        Assert.Equal(ids[99], first.GetProperty("next_before").GetInt64());
        Assert.Contains("'before' set to 'next_before' continues exactly", first.GetProperty("note").GetString()!, StringComparison.Ordinal);
        var rest = Data(await wire.SendAsync(Frame(Ops.LedgerList, ("kind", "lesson"), ("before", ids[99]))));
        Assert.Equal((5, false), (rest.GetProperty("count").GetInt32(), rest.GetProperty("more").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("capped_by").ValueKind);
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("next_before").ValueKind);
        Assert.Equal(ids, first.GetProperty("entries").EnumerateArray().Concat(rest.GetProperty("entries").EnumerateArray())
            .Select(e => e.GetProperty("entry").GetInt64()));

        var seven = Data(await wire.SendAsync(Frame(Ops.LedgerList, ("limit", 7))));
        Assert.Equal((7, "limit"), (seven.GetProperty("count").GetInt32(), seven.GetProperty("capped_by").GetString()));

        foreach (var limit in new object[] { 0, 101, 2.5m, 1000 })
            Refused(await wire.SendAsync(Frame(Ops.LedgerList, ("limit", limit))), "TradeAgent does not clamp it for you");
        foreach (var (name, value) in new[] { ("author", "referee"), ("kind", "decision"), ("status", "closed") })
            Refused(await wire.SendAsync(Frame(Ops.LedgerList, (name, value))), $"'{value}' is not");

        // THE BYTE BOUND: entries of 2,000 characters, which fit about thirty to 64 KiB.
        var big = new List<long>();
        for (var i = 0; i < 40; i++)
            big.Add(rig.Gw.Ledger.Add(CouncilRoles.Research, "attempt-i", LedgerKind.Experiment, new string((char)('a' + i % 26), 2_000),
                LedgerMark.Hypothesis, null, null, null).Id);
        big.Reverse();
        var served = new List<long>();
        long? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = await wire.SendAsync(Frame(Ops.LedgerList, ("kind", "experiment"), ("before", cursor)));
            var d = Data(page);
            var entries = d.GetProperty("entries").EnumerateArray().ToList();
            var bytes = wire.LastDataBytes;
            log.WriteLine($"page {++pages}: {entries.Count} entries, {bytes:N0} bytes sent, capped by {d.GetProperty("capped_by")}");
            Assert.True(bytes <= ResearchLedger.MaxReadBytes, $"{bytes:N0} bytes is over {ResearchLedger.MaxReadBytes:N0}");
            served.AddRange(entries.Select(e => e.GetProperty("entry").GetInt64()));
            if (!d.GetProperty("more").GetBoolean()) break;
            Assert.Equal("bytes", d.GetProperty("capped_by").GetString());
            cursor = d.GetProperty("next_before").GetInt64();
        }
        Assert.True(pages >= 2, "the byte bound never stopped a page");
        Assert.Equal(big, served);

        // A SHOW'S REVISIONS: the count bound, then the byte bound.
        var many = ids[0];
        for (var r = 2; r <= 105; r++)
            rig.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-i", many, $"revision {r}", LedgerMark.Claim, "again", null, null);
        var shown = Data(await wire.SendAsync(Frame(Ops.LedgerShow, ("entry", many))));
        Assert.Equal((105, 100, true, "limit"), (shown.GetProperty("revision_count").GetInt32(),
            shown.GetProperty("revisions").GetArrayLength(), shown.GetProperty("more").GetBoolean(),
            shown.GetProperty("capped_by").GetString()));
        Assert.Equal((many, "revisions", 6L), (shown.GetProperty("next").GetProperty("entry").GetInt64(),
            shown.GetProperty("next").GetProperty("part").GetString(), shown.GetProperty("next").GetProperty("before").GetInt64()));
        Assert.Contains("asking again with the arguments in 'next' continues exactly", shown.GetProperty("note").GetString()!,
            StringComparison.Ordinal);
        var older = Data(await wire.SendAsync(Frame(Ops.LedgerShow, ("entry", many), ("part", "revisions"), ("before", 6))));
        Assert.Equal([5, 4, 3, 2, 1], older.GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("revision").GetInt32()));
        Assert.False(older.GetProperty("more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, older.GetProperty("next").ValueKind);

        var heavy = big[0];
        for (var r = 2; r <= 40; r++)
            rig.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-i", heavy, new string('z', 2_000), LedgerMark.Claim, "again", null, null);
        var capped = Data(await wire.SendAsync(Frame(Ops.LedgerShow, ("entry", heavy))));
        Assert.True(capped.GetProperty("more").GetBoolean());
        Assert.Equal("bytes", capped.GetProperty("capped_by").GetString());
        Assert.True(wire.LastDataBytes <= ResearchLedger.MaxReadBytes, $"a show of {wire.LastDataBytes:N0} bytes");

        // THE WHOLE ANSWER IS BOUNDED, MEASURED AS SENT (the fix pass, finding 2): an entry at every maximum — its source,
        // each revision's text and reason in a character the wire writes as twelve bytes — with 100 links and 100 entries
        // about it, themselves at every maximum, is read whole by following 'next', and no answer's data passes 64 KiB.
        // The first answer is the entry's frame and one maximal revision, which is always served: its bytes are quoted.
        const string Wide = "\U0001F600";   // one character: four bytes of UTF-8, and \uD83D\uDE00 — twelve — on the wire
        static string Widest(int chars) => string.Concat(Enumerable.Repeat(Wide, chars));
        var top = rig.Gw.Ledger.Add(CouncilRoles.Research, "attempt-i", LedgerKind.Hypothesis, Widest(ResearchLedger.MaxTextChars),
            LedgerMark.Hypothesis, 0.5m, null, Widest(ResearchLedger.MaxNoteChars)).Id;
        for (var r = 2; r <= 4; r++)
            rig.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-i", top, Widest(ResearchLedger.MaxTextChars), LedgerMark.Claim,
                Widest(ResearchLedger.MaxNoteChars), 0.25m, LedgerStatus.Held);
        var app = new LedgerLinks(rig.Db);
        var asked = rig.Gw.Ledger.Ask(top, CouncilRoles.Research);
        for (var i = 0; i < 100; i++)
            Assert.True(app.Link(asked, LedgerRecord.Run, i.ToString("D2", CultureInfo.InvariantCulture) + new string('f', 62), "attempt-i"));
        var against = new List<long>();
        for (var i = 0; i < 100; i++)
            against.Add(rig.Gw.Ledger.Add(CouncilRoles.Operations, "attempt-i", LedgerKind.Kill, Widest(ResearchLedger.MaxTextChars),
                LedgerMark.Claim, null, top, Widest(ResearchLedger.MaxNoteChars)).Id);
        foreach (var revised in against.Take(3))
            rig.Gw.Ledger.Revise(CouncilRoles.Operations, "attempt-i", revised, Widest(ResearchLedger.MaxTextChars), LedgerMark.Claim,
                Widest(ResearchLedger.MaxNoteChars), null, null);

        var (revs, lnks, abts) = (0, 0, 0);
        var next = new List<(string, object?)> { ("entry", top) };
        for (var page = 1; ; page++)
        {
            Assert.True(page <= 20, "the maximal show never finished");
            var d = Data(await wire.SendAsync(Frame(Ops.LedgerShow, [.. next])));
            log.WriteLine($"maximal show, page {page}: {wire.LastDataBytes:N0} bytes sent — {d.GetProperty("revisions").GetArrayLength()} "
                + $"revisions, {d.GetProperty("links").GetArrayLength()} links, {d.GetProperty("about_it").GetArrayLength()} about it, "
                + $"capped by {d.GetProperty("capped_by")}");
            Assert.True(wire.LastDataBytes <= ResearchLedger.MaxReadBytes, $"page {page}: {wire.LastDataBytes:N0} bytes");
            if (page == 1) Assert.Equal(1, d.GetProperty("revisions").GetArrayLength());
            revs += d.GetProperty("revisions").GetArrayLength();
            lnks += d.GetProperty("links").GetArrayLength();
            abts += d.GetProperty("about_it").GetArrayLength();
            if (d.GetProperty("next") is not { ValueKind: JsonValueKind.Object } more) break;
            next = [("entry", top), ("part", more.GetProperty("part").GetString())];
            if (more.TryGetProperty("before", out var at)) next.Add(("before", at.GetInt64()));
        }
        Assert.Equal((4, 100, 100), (revs, lnks, abts));

        // AND A LIST: maximal entries, and a pair whose entries alone come within the frame's size of the bound.
        var (listed, widest) = (0, 0);
        long? from = null;
        for (var page = 1; ; page++)
        {
            Assert.True(page <= 200, "the maximal list never finished");
            var d = Data(await wire.SendAsync(Frame(Ops.LedgerList, ("kind", "kill"), ("before", from))));
            Assert.True(wire.LastDataBytes <= ResearchLedger.MaxReadBytes, $"list page {page}: {wire.LastDataBytes:N0} bytes");
            widest = Math.Max(widest, wire.LastDataBytes);
            listed += d.GetProperty("count").GetInt32();
            if (!d.GetProperty("more").GetBoolean()) break;
            from = d.GetProperty("next_before").GetInt64();
        }
        Assert.Equal(100, listed);
        log.WriteLine($"maximal list: its widest page {widest:N0} bytes sent, one entry");

        for (var i = 0; i < 2; i++)
            rig.Gw.Ledger.Add(CouncilRoles.Research, "attempt-i", LedgerKind.Finding, Widest(ResearchLedger.MaxTextChars),
                LedgerMark.Claim, null, null, Widest(680));
        var pair = Data(await wire.SendAsync(Frame(Ops.LedgerList, ("kind", "finding"))));
        log.WriteLine($"the pair: {wire.LastDataBytes:N0} bytes sent, {pair.GetProperty("count")} entries, "
            + $"{pair.GetProperty("entries").EnumerateArray().Sum(e => Encoding.UTF8.GetByteCount(e.GetRawText())):N0} of them entries");
        Assert.True(wire.LastDataBytes <= ResearchLedger.MaxReadBytes, $"the pair: {wire.LastDataBytes:N0} bytes");
    }

    // ---- every reference a show names is reachable ------------------------------------------------------------------

    /// <summary>
    /// EVERY REFERENCE A SHOW NAMES IS REACHABLE (the fix pass, finding 3): an entry with two revisions, 101 linked runs
    /// and 101 entries about it is read whole by following each answer's <c>next</c> — every revision, every link and every
    /// entry about it once, newest first, the oldest link and the oldest entry about it included — each list stopping at
    /// its count bound and saying so. A part outside the vocabulary is refused. RED before the fix: <c>part</c> was not an
    /// argument, and links and entries about stopped at the newest 100 with no way past them.
    /// </summary>
    [Fact]
    public async Task Every_reference_a_show_names_is_reachable_by_paging()
    {
        await using var rig = await Ready();
        await using var wire = await rig.Dial(CouncilRoles.Research);
        var entry = rig.Gw.Ledger.Add(CouncilRoles.Research, "attempt-p", LedgerKind.Hypothesis, "one belief, asked under often",
            LedgerMark.Hypothesis, null, null, null).Id;
        rig.Gw.Ledger.Revise(CouncilRoles.Research, "attempt-p", entry, "still held", LedgerMark.Claim, "asked again", null, null);
        var ask = rig.Gw.Ledger.Ask(entry, CouncilRoles.Research);
        var runs = Enumerable.Range(0, 101).Select(i => $"run-{i:D3}").ToList();
        var app = new LedgerLinks(rig.Db);   // the app's writer, built here as the gateway builds it: no run is needed for a link to exist
        foreach (var run in runs) Assert.True(app.Link(ask, LedgerRecord.Run, run, "attempt-p"));
        var about = new List<long>();
        for (var i = 0; i < 101; i++)
            about.Add(rig.Gw.Ledger.Add(i % 2 == 0 ? CouncilRoles.Operations : CouncilRoles.Research, "attempt-p",
                LedgerKind.Kill, $"against it, {i}", LedgerMark.Claim, null, entry, null).Id);

        var revisions = new List<int>();
        var links = new List<string>();
        var abouts = new List<long>();
        var args = new List<(string, object?)> { ("entry", entry) };
        for (var page = 1; ; page++)
        {
            Assert.True(page <= 10, "the show never finished");
            var answer = await wire.SendAsync(Frame(Ops.LedgerShow, [.. args]));
            Assert.True(answer.Ok, Json.Write(answer.Error));
            var d = Data(answer);
            Assert.Equal((2, 101, 101), (d.GetProperty("revision_count").GetInt32(), d.GetProperty("link_count").GetInt32(),
                d.GetProperty("about_count").GetInt32()));
            revisions.AddRange(d.GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("revision").GetInt32()));
            links.AddRange(d.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("id").GetString()!));
            abouts.AddRange(d.GetProperty("about_it").EnumerateArray().Select(a => a.GetProperty("entry").GetInt64()));
            log.WriteLine($"page {page} from {string.Join(", ", args.Skip(1))}: {d.GetProperty("revisions").GetArrayLength()} revisions, "
                + $"{d.GetProperty("links").GetArrayLength()} links, {d.GetProperty("about_it").GetArrayLength()} about it, "
                + $"capped by {d.GetProperty("capped_by")}, next {d.GetProperty("next")}");

            var next = d.GetProperty("next");
            if (next.ValueKind == JsonValueKind.Null)
            {
                Assert.False(d.GetProperty("more").GetBoolean());
                break;
            }
            Assert.True(d.GetProperty("more").GetBoolean());
            Assert.Equal("limit", d.GetProperty("capped_by").GetString());
            Assert.Equal(entry, next.GetProperty("entry").GetInt64());
            args = [("entry", entry), ("part", next.GetProperty("part").GetString())];
            if (next.TryGetProperty("before", out var cursor)) args.Add(("before", cursor.GetInt64()));
        }

        Assert.Equal([2, 1], revisions);
        Assert.Equal(Enumerable.Reverse(runs), links);
        Assert.Equal("run-000", links[^1]);
        about.Reverse();
        Assert.Equal(about, abouts);

        Refused(await wire.SendAsync(Frame(Ops.LedgerShow, ("entry", entry), ("part", "derived"))),
            "'derived' is not a part of a show");
    }
}
