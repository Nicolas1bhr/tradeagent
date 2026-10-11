using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A PROVIDER THAT HONOURS THE OFFERED SCHEMA CAN SEND EVERY ARGUMENT AN OP TAKES (<c>U-harness-trade-args</c>, item 2).
///
/// <para><b>What this holds.</b> For every harness tool that carries ops and every op it carries, a call naming EVERY
/// argument <see cref="GatewaySchema"/> declares for that op, each a value of the type the tool's own <c>Parameters</c>
/// offers it as, (1) validates against those <c>Parameters</c> exactly as the provider is handed them — the bytes
/// <see cref="Json.Write"/> puts on the wire, read by a small validator here (<c>type</c>, <c>properties</c>,
/// <c>enum</c>, <c>additionalProperties</c>) — and (2) is refused by the gateway for no argument's name or type, both
/// through the harness's own route (<see cref="GrantedWorkerTools"/> over <see cref="IGatewayCalls"/>) and as the same
/// frame over the real named pipe. A refusal for the role, the mode or a value (an id nobody holds) is fine and logged.
/// Before this unit <c>trade</c> offered only <c>op</c> and <c>request_id</c> with <c>additionalProperties</c> false, so a
/// schema-honouring provider could place, cancel, backtest or write the ledger with none of the arguments they need.</para>
///
/// <para><b>And what it does not widen.</b> No offered property is one the gateway does not declare for some op of that
/// tool, and the gateway's own refusal of an argument an op does not take still answers on both routes.</para>
/// </summary>
public class HarnessTradeArgsTests(ITestOutputHelper log)
{
    /// <summary>The wire by hand: the machine's key on every frame, the launch grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> DialAsync(string pipeName, string? grant)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            var hello = await c.SendAsync(new IpcRequest { Op = Ops.Hello, Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
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

    /// <summary>
    /// The words of a refusal FOR AN ARGUMENT'S NAME OR TYPE, on either route: the gateway's undeclared-argument refusal,
    /// its strict number and flag readers, its refusal of a field the protocol derives, and the harness's own refusal of
    /// arguments it could not read or an op its tool does not carry.
    /// </summary>
    static readonly string[] NameOrTypeRefusals =
    [
        "is not an argument", "are not arguments", "is not a number this build can read", "must be true or false",
        "is not a field of this protocol", "the arguments were not", "is not an operation"
    ];

    /// <summary>
    /// THE ARGUMENTS AN OP TAKES, as the gateway declares them. <c>sell</c> declares none and says "Same arguments as buy":
    /// one parser reads both (<c>ParsePlace</c>), so its call carries buy's.
    /// </summary>
    static GatewaySchema.ArgSpec[] ArgsOf(string op) =>
        GatewaySchema.Ops().Single(o => o.Op == (op == Ops.Sell ? Ops.Buy : op)).Args;

    /// <summary>A value of the offered TYPE for one argument, plausible where the name says what it holds.</summary>
    static object Value(string op, string name, string offeredType) => offeredType switch
    {
        "number" => name switch { "confidence" => 0.5m, "fees" or "slippage" => 0.001m, "after" => 0, _ => 1 },
        "boolean" => false,
        "string" => (op, name) switch
        {
            (_, "from" or "to" or "since" or "day") => "2026-01-01",
            (_, "as_of") => "2026-01-01T00:00:00Z",
            (_, "symbol" or "pair" or "subject") => "BTCUSDT",
            (_, "tif") => "Day",
            (_, "mark") => "hypothesis",
            (_, "author") => "research",
            (_, "part") => "revisions",
            (_, "origin") => "all",
            (Ops.MaterialNote, "kind") => "note",
            (_, "kind") => "lesson",
            (Ops.LedgerRevise, "status") => "held",
            (_, "status") => "open",
            (Ops.DataBars, "source") => "archive",
            (Ops.DataTape, "source") => "binance-um-oi",
            (_, "strategy") => "strategies/none.strategy",
            (_, "request_id") => "args-" + Guid.NewGuid().ToString("n")[..12],
            _ => "x"
        },
        _ => throw new InvalidOperationException($"'{name}' of '{op}' is offered as '{offeredType}', a type this test has no value for")
    };

    /// <summary>The wire form of one tool's <c>Parameters</c>: the bytes the provider is handed.</summary>
    static JsonElement Wire(ToolSpec tool)
    {
        using var doc = JsonDocument.Parse(Json.Write(tool.Parameters));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// A SMALL JSON-SCHEMA VALIDATOR, over exactly the keywords the offered schema uses. Returns every reason the call
    /// does not validate; empty means a provider honouring the schema would send it. A schema that does not say
    /// <c>additionalProperties</c> under that name says nothing about extra arguments, and the tool contract promises
    /// that it does — so its absence is a failure too, not a pass.
    /// </summary>
    static List<string> Validate(JsonElement schema, IReadOnlyDictionary<string, object> call)
    {
        var why = new List<string>();
        if (!schema.TryGetProperty("type", out var t) || t.GetString() != "object") why.Add("the schema is not of type object");
        if (!schema.TryGetProperty("additionalProperties", out var extra) || extra.ValueKind != JsonValueKind.False)
            why.Add("the schema does not say additionalProperties: false under that name (its keys: "
                    + string.Join(", ", schema.EnumerateObject().Select(k => k.Name)) + ")");
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

        foreach (var (name, value) in call)
        {
            if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var prop))
            {
                why.Add($"'{name}' is not a property the schema offers");
                continue;
            }
            var element = JsonSerializer.SerializeToElement(value);
            var type = prop.GetProperty("type").GetString();
            var fits = type switch
            {
                "string" => element.ValueKind == JsonValueKind.String,
                "number" => element.ValueKind == JsonValueKind.Number,
                "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
                "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!fits) why.Add($"'{name}' is {element.ValueKind} and the schema offers {type}");
            if (prop.TryGetProperty("enum", out var choices) && choices.EnumerateArray().All(c => c.GetRawText() != element.GetRawText()))
                why.Add($"'{name}' = {element.GetRawText()} is not one of the schema's enum");
        }
        return why;
    }

    /// <summary>The offered type of one property, or null when the schema does not offer it.</summary>
    static string? OfferedType(JsonElement schema, string name) =>
        schema.TryGetProperty("properties", out var p) && p.TryGetProperty(name, out var prop) ? prop.GetProperty("type").GetString() : null;

    /// <summary>
    /// THE CALL A SCHEMA-HONOURING PROVIDER SENDS FOR ONE OP WITH EVERY ARGUMENT: <c>op</c> where the tool carries more
    /// than one, and each declared argument as a value of the type the schema offers it as — or, where the schema does not
    /// offer it at all, of the type the gateway declares, so the validator says so rather than the test skipping it.
    /// </summary>
    static Dictionary<string, object> FullCall(JsonElement schema, string tool, string op)
    {
        var call = new Dictionary<string, object>(StringComparer.Ordinal);
        if (GrantedWorkerTools.OpsOf(tool).Count > 1) call["op"] = op;
        foreach (var arg in ArgsOf(op))
            call[arg.Name] = Value(op, arg.Name, OfferedType(schema, arg.Name) ?? (arg.Type == "bool" ? "boolean" : arg.Type));
        return call;
    }

    static void NotForANameOrType(string route, string op, bool served, string answer)
    {
        foreach (var words in NameOrTypeRefusals)
            Assert.False(answer.Contains(words, StringComparison.Ordinal),
                $"{route}: '{op}' was refused for an argument's name or type ('{words}'): {answer}");
    }

    [Fact]
    public async Task Every_op_called_with_every_argument_it_takes_validates_against_the_offered_schema_and_is_refused_for_no_argument_name_or_type()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = "ta-args-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        using var _db = db;     // declared first, so it is disposed after the server that reads it
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        // THE OPERATIONS DIRECTOR, so a mutating op reaches as far as the gateway lets a role that may move money — on the
        // test's own simulated broker. Any refusal it then meets for its mode or its values is fine; one for a name or a
        // type is the defect.
        var role = CouncilRoles.Operations;
        var tools = new GrantedWorkerTools(role, () => Paths.RoleHome(role), () => "attempt-args", () => server);
        await using var wire = await Raw.DialAsync(pipe, grants.Issue(role, "attempt-args").Token);

        var invalid = new List<string>();
        var checkedOps = 0;
        foreach (var tool in GrantedWorkerTools.Granted.Where(t => GrantedWorkerTools.OpsOf(t.Name).Count > 0))
        {
            var schema = Wire(tool);
            foreach (var op in GrantedWorkerTools.OpsOf(tool.Name))
            {
                var call = FullCall(schema, tool.Name, op);
                var why = Validate(schema, call);
                if (why.Count > 0)
                {
                    invalid.Add($"{tool.Name}·{op}: {string.Join("; ", why)}");
                    continue;
                }

                // THE HARNESS'S ROUTE: the provider's arguments as it would send them.
                var answer = await tools.InvokeAsync(new ToolRequest("c-" + op, tool.Name, JsonSerializer.Serialize(call)));
                NotForANameOrType("harness", op, answer.Served, answer.Content);

                // THE REAL PIPE: the same arguments as one frame, under the same role's launch grant.
                var args = call.Where(kv => kv.Key is not "op" and not "request_id")
                    .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
                var reply = await wire.SendAsync(new IpcRequest
                {
                    Op = op, Session = "args", RequestId = "args-" + Guid.NewGuid().ToString("n")[..12], Args = args
                });
                NotForANameOrType("pipe", op, reply.Ok, reply.Ok ? "" : $"{reply.Error?.Code}: {reply.Error?.Message}");

                log.WriteLine($"{tool.Name}·{op} [{string.Join(",", call.Keys)}] harness={(answer.Served ? "served" : "refused: " + Cut(answer.Content))} "
                              + $"pipe={(reply.Ok ? "served" : $"refused: {reply.Error?.Code} {Cut(reply.Error?.Message)}")}");
                checkedOps++;
            }
        }

        Assert.True(invalid.Count == 0, "the offered schema refuses a complete call:\n" + string.Join("\n", invalid));
        Assert.Equal(GrantedWorkerTools.Granted.Sum(t => GrantedWorkerTools.OpsOf(t.Name).Count), checkedOps);

        // THE GUARD STILL STANDS, ON BOTH ROUTES: an argument an op does not take is refused by the gateway, in its words.
        var undeclared = await tools.InvokeAsync(new ToolRequest("u1", GrantedWorkerTools.Trade,
            """{"op":"ledger-add","kind":"lesson","text":"x","mark":"claim","run":"0123456789ab"}"""));
        Assert.False(undeclared.Served);
        Assert.Contains("'run' is not an argument of 'ledger-add'", undeclared.Content, StringComparison.Ordinal);
        var overPipe = await wire.SendAsync(new IpcRequest
        {
            Op = Ops.LedgerAdd, Session = "args", RequestId = "args-u2",
            Args = new() { ["kind"] = J("lesson"), ["text"] = J("x"), ["mark"] = J("claim"), ["run"] = J("0123456789ab") }
        });
        Assert.False(overPipe.Ok);
        Assert.Contains("'run' is not an argument of 'ledger-add'", overPipe.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_offered_property_is_one_the_gateway_does_not_declare_for_an_op_of_that_tool()
    {
        // WHAT RIDES EVERY API TURN: the tools as ApiConversation.RequestBody writes them.
        var wire = Json.Write(GrantedWorkerTools.Granted
            .Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.Parameters } })
            .ToList());
        log.WriteLine($"offered tools: {Encoding.UTF8.GetByteCount(wire):N0} bytes on the wire");

        foreach (var tool in GrantedWorkerTools.Granted.Where(t => GrantedWorkerTools.OpsOf(t.Name).Count > 0))
        {
            var schema = Wire(tool);
            var ops = GrantedWorkerTools.OpsOf(tool.Name);
            var declared = ops.SelectMany(ArgsOf).ToList();
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                if (property.Name == "op")
                {
                    Assert.True(property.Value.TryGetProperty("enum", out var choices), $"{tool.Name} offers 'op' with no enum of its ops");
                    Assert.Equal(ops, choices.EnumerateArray().Select(e => e.GetString()));
                    continue;
                }
                // `request_id` is the harness's own idempotency key, consumed before the op's arguments are handed on, and
                // offered only on a tool that carries an op that needs one.
                if (property.Name == "request_id")
                {
                    Assert.Contains(ops, Ops.IsMutating);
                    continue;
                }
                var specs = declared.Where(a => a.Name == property.Name).ToList();
                Assert.True(specs.Count > 0, $"{tool.Name} offers '{property.Name}', which no op it carries declares");
                var offered = property.Value.GetProperty("type").GetString();
                Assert.All(specs, a => Assert.Equal(a.Type == "bool" ? "boolean" : a.Type, offered));
            }
        }
    }

    static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    static string Cut(string? s) => s is null ? "" : s.Length <= 140 ? s : s[..140] + "…";
}
