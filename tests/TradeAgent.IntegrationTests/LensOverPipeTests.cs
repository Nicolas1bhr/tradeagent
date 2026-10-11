using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Perception;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U-lenses — THE AGENTS' SURFACE OVER THE LENSES: a proposal reaches the tape only through the app's registrar, under the
/// role the grant proved, and nothing on the pipe reaches a decision model. No request here leaves the machine.
/// </summary>
public class LensOverPipeTests(ITestOutputHelper log)
{
    /// <summary>A lens over OKX's announcements, this suite's own words.</summary>
    const string Valid = """
        {"id":"pipe-headline","target":{"source":"okx-eea-announcements","series":"announcements"},
         "state":{"fields":[{"path":"title","max_chars":200}]},
         "questions":{"kind":{"type":"choice","instructions":"Which kind of notice is this?",
                              "criteria":{"listing":null,"maintenance":null,"none_of_these":null}}}}
        """;

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

    sealed record Rig(TradingGateway Gw, Database Db, TapeStore Store, GatewayPipeServer Server, AgentGrants Grants, string Pipe,
        List<string> Registered) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string? role, string attempt)
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
            Store.Dispose();
            Db.Dispose();
        }
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var store = new TapeStore(Path.Combine(TestEnv.Home, $"lens-pipe-{Guid.NewGuid():n}.db"));
        gw.Tape = new TapeReader(store.File);
        var registered = new List<string>();
        gw.LensRegistrar = (definition, proposer, attempt) =>
        {
            lock (registered) registered.Add($"{proposer}/{attempt}");
            return store.RegisterLens(definition, proposer, attempt);
        };

        var pipe = "ta-lens-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();
        return new Rig(gw, db, store, server, grants, pipe, registered);
    }

    static IpcRequest Frame(string op, params (string Key, object? Value)[] args)
    {
        var a = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in args)
            if (value is not null) a[key] = JsonSerializer.SerializeToElement(value);
        return new IpcRequest { Op = op, Session = "lens", RequestId = "lens-" + Guid.NewGuid().ToString("n")[..12], Args = a };
    }

    static JsonElement Data(IpcResponse r)
    {
        Assert.True(r.Ok, Json.Write(r.Error));
        return JsonSerializer.SerializeToElement(r.Data, Json.Options);
    }

    /// <summary>Every table but the lenses', every row, as one hash: what "the rest of the tape is untouched" is measured as.</summary>
    static string RestOfTheTape(string file)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        var text = new StringBuilder();
        foreach (var table in new[] { "tape_meta", "tape_fetch", "tape_obs", "decision_call" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    text.Append(r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// (h) LENS-PROPOSE WRITES ONLY THROUGH THE REGISTRAR AND REACHES NO PORT. A Research launch's proposal is registered by
    /// the app under that launch's role and attempt, numbered and hashed by the store; a frame that names a proposer, a
    /// version or anything else beside the definition is refused, as is a caller with no launch grant and a host with no
    /// registrar, each with nothing written; the reads serve every role's, the definition only on show; no other table of
    /// the tape moves; and the gateway's assembly holds no store, while <c>No_pipe_op_reaches_the_decision_port</c> stays as
    /// it was.
    /// </summary>
    [Fact]
    public async Task Lens_propose_writes_only_through_the_registrar_and_reaches_no_port()
    {
        await using var rig = await Ready();
        var rest = RestOfTheTape(rig.Store.File);
        await using var research = await rig.Dial(CouncilRoles.Research, "attempt-lens-research");

        var proposed = await research.SendAsync(Frame(Ops.LensPropose, ("lens", Valid)));
        var data = Data(proposed);
        log.WriteLine(data.GetRawText());
        Assert.Equal(1, data.GetProperty("version").GetInt32());
        Assert.True(data.GetProperty("new").GetBoolean());
        var row = rig.Store.LensOf("pipe-headline", 1)!;
        Assert.Equal((CouncilRoles.Research, "attempt-lens-research"), (row.Proposer, row.Attempt));
        Assert.Equal(Lens.Parse(Valid).Lens!.LensSha, data.GetProperty("lens_sha").GetString());
        Assert.Equal([$"{CouncilRoles.Research}/attempt-lens-research"], rig.Registered);

        // A FRAME CANNOT NAME WHO PROPOSED IT, WHICH VERSION IT IS, OR ANYTHING ELSE.
        foreach (var (key, value) in new (string, object)[] { ("proposer", CouncilRoles.Operations), ("version", 7), ("attempt", "attempt-x") })
        {
            var refused = await research.SendAsync(Frame(Ops.LensPropose, ("lens", Valid.Replace("pipe-headline", "pipe-other")), (key, value)));
            log.WriteLine($"{key}: {refused.Error?.Message}");
            Assert.False(refused.Ok);
            Assert.Contains($"'{key}' is not an argument of '{Ops.LensPropose}'", refused.Error!.Message, StringComparison.Ordinal);
        }

        // A REFUSED DEFINITION IS REFUSED IN THE STORE'S WORDS.
        var bad = await research.SendAsync(Frame(Ops.LensPropose, ("lens", Valid.Replace("none_of_these", "neither"))));
        Assert.False(bad.Ok);
        Assert.Contains("offers no way out", bad.Error!.Message, StringComparison.Ordinal);

        // NOBODY PROPOSES NOTHING.
        await using (var nobody = await rig.Dial(null, ""))
        {
            var refused = await nobody.SendAsync(Frame(Ops.LensPropose, ("lens", Valid.Replace("pipe-headline", "pipe-nobody"))));
            Assert.False(refused.Ok);
            Assert.Contains("presented no launch grant", refused.Error!.Message, StringComparison.Ordinal);

            // AND EVERY ROLE READS EVERY ROLE'S — the definition on show only.
            var list = Data(await nobody.SendAsync(Frame(Ops.LensList)));
            var ids = list.GetProperty("lenses").EnumerateArray().Select(l => l.GetProperty("id").GetString()).ToList();
            Assert.Equal(["pipe-headline", BuiltInLenses.OkxAnnouncement], ids);
            Assert.False(list.GetProperty("lenses")[0].TryGetProperty("lens", out _));
            var shown = Data(await nobody.SendAsync(Frame(Ops.LensShow, ("id", "pipe-headline"))));
            Assert.Equal(row.Definition, shown.GetProperty("definition").GetRawText());
        }

        // THE REGISTRAR SAW THE TWO PROPOSALS THAT CARRIED ONLY A DEFINITION UNDER A ROLE — the one it registered and the one
        // the store refused — and none of the frames refused before it.
        Assert.Equal([$"{CouncilRoles.Research}/attempt-lens-research", $"{CouncilRoles.Research}/attempt-lens-research"], rig.Registered);
        Assert.Equal(2, rig.Store.Lenses().Count);

        // NO REGISTRAR, NO REGISTRATION.
        rig.Gw.LensRegistrar = null;
        var closed = await research.SendAsync(Frame(Ops.LensPropose, ("lens", Valid.Replace("pipe-headline", "pipe-closed"))));
        Assert.False(closed.Ok);
        Assert.Equal(nameof(ErrorCode.MARKET_DATA_UNAVAILABLE), closed.Error!.Code);
        Assert.Equal(2, rig.Store.Lenses().Count);

        Assert.Equal(rest, RestOfTheTape(rig.Store.File));

        // THE GATEWAY HOLDS NO STORE: the registrar is a delegate, and nothing in its assembly is typed as the tape's writer.
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(TradingGateway).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(all).Where(f => f.FieldType == typeof(TapeStore)).Select(f => $"{t.Name}.{f.Name}")
                .Concat(t.GetProperties(all).Where(p => p.PropertyType == typeof(TapeStore)).Select(p => $"{t.Name}.{p.Name}")))
            .ToList();
        Assert.True(holders.Count == 0, "the gateway holds the tape's store: " + string.Join(", ", holders));

        // AND THE OPS' NAMES PASS THE PORT GUARD'S WORDS, which that guard asserts for every op unedited.
        foreach (var op in GatewaySchema.Ops().Where(o => o.Op.StartsWith("lens-", StringComparison.Ordinal)))
            foreach (var word in new[] { "decision", "perception", "jev", "typesafe", "systemone", "openrouter", "annotat",
                         "budget", "pin", "instrument", "key", "model", "question", "test" })
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.All(op.Args, a => Assert.DoesNotContain(word, a.Name, StringComparison.OrdinalIgnoreCase));
            }
    }
}
