using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.TradeCli;

// trade — the stable command surface every supported agent uses.
//
// --json is the canonical interface: it always prints one object with ok/data/error, so an agent can
// branch on structure rather than parse prose. Human output is a convenience for the person watching.

var argv = args.ToList();
var wantJson = argv.Remove("--json");
var wantAll = argv.Remove("--all");

if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

var command = argv[0].ToLowerInvariant();
var positional = Positional(argv);
var flags = ParseFlags(argv);

if (command is "schema" && flags.ContainsKey("offline"))
{
    Console.WriteLine(Json.Write(new { note = "offline schema; start TradeAgent for live status" }, true));
    return 0;
}

var (op, args2) = Map(command, positional, flags, wantAll);
if (op is null)
{
    Console.Error.WriteLine($"trade: unknown command '{command}'. Try: trade --help");
    return 2;
}

// The replay contract lives in CliReplayContract so it can be tested; see that file for why.
// Minted and announced BEFORE the frame goes out, because the case that needs the id is the case
// where no reply arrives.
var requestId = CliReplayContract.MintRequestId(op, flags.GetValueOrDefault("request-id"));
CliReplayContract.AnnounceRequestId(Console.Error, requestId);

// Whether the frame was handed to the pipe at all. False means nothing was sent and there is
// nothing to reconcile; true means the outcome is UNKNOWN, which is a different sentence.
var sent = false;

await using var client = new PipeClient();
try
{
    await client.ConnectAsync();
    var request = new IpcRequest
    {
        Op = op,
        Session = Environment.GetEnvironmentVariable("TRADEAGENT_SESSION") ?? "agent",
        RequestId = requestId,
        Args = args2.ToDictionary(k => k.Key, v => JsonSerializer.SerializeToElement(v.Value))
    };

    sent = true;
    var reply = await client.SendAsync(request);

    if (wantJson)
    {
        Console.WriteLine(Json.Write(CliReplayContract.AnsweredJson(requestId, reply), true));
        return reply.Ok ? 0 : 1;
    }

    if (!reply.Ok)
    {
        Console.Error.WriteLine($"{reply.Error?.Code}: {reply.Error?.UserMessage}");
        if (!string.IsNullOrWhiteSpace(reply.Error?.Repair)) Console.Error.WriteLine($"  what to do: {reply.Error!.Repair}");
        if (!string.IsNullOrWhiteSpace(reply.Error?.Message)) Console.Error.WriteLine($"  detail: {reply.Error!.Message}");
        return 1;
    }

    Console.WriteLine(reply.Data is null ? "(nothing)" : Json.Write(reply.Data, true));
    if (Ops.IsMutating(op))
        Console.WriteLine("\nnote: retrying with the same --request-id is safe; it will not place a second order.");
    return 0;
}
catch (TradeAgentException ex)
{
    // A transport failure after the frame went out is not a failed order; see CliReplayContract.
    var recovery = CliReplayContract.RecoveryLine(sent, requestId);

    if (wantJson)
    {
        Console.WriteLine(Json.Write(CliReplayContract.UnansweredJson(requestId, sent, IpcError.From(ex.Info)), true));
        return 1;
    }
    Console.Error.WriteLine($"{ex.Code}: {ex.Info.UserMessage}");
    Console.Error.WriteLine($"  what to do: {ex.Info.Repair}");
    if (recovery is not null) Console.Error.WriteLine($"  {recovery}");
    return 1;
}

// Everything that is neither a flag nor a flag's value. The earlier version filtered on "does not
// start with --", which also kept the VALUE of every flag — harmless while no command read past its
// second positional, and wrong the moment one takes a flag between positionals.
static List<string> Positional(List<string> argv)
{
    var pos = new List<string>();
    for (var i = 1; i < argv.Count; i++)
    {
        if (!argv[i].StartsWith("--")) { pos.Add(argv[i]); continue; }
        if (i + 1 < argv.Count && !argv[i + 1].StartsWith("--")) i++;   // skip the flag's value
    }
    return pos;
}

static Dictionary<string, string> ParseFlags(List<string> argv)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Count; i++)
    {
        if (!argv[i].StartsWith("--")) continue;
        var key = argv[i][2..];
        var value = i + 1 < argv.Count && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
        d[key] = value;
    }
    return d;
}

static (string? Op, Dictionary<string, object> Args) Map(string cmd, List<string> pos, Dictionary<string, string> flags, bool all)
{
    var a = new Dictionary<string, object>();
    void Opt(string key, string? argKey = null)
    {
        if (flags.TryGetValue(key, out var v)) a[argKey ?? key] = v;
    }

    switch (cmd)
    {
        case "status": return (Ops.Status, a);
        case "connectors": return (Ops.Connectors, a);
        case "accounts": return (Ops.Accounts, a);
        case "account": return (Ops.Account, a);
        case "instruments": return (Ops.Instruments, a);
        case "executions": return (Ops.Executions, a);
        case "schema": return (Ops.Schema, a);
        case "positions": return (Ops.Positions, a);
        case "orders":
            if (all) a["all"] = "true";
            return (Ops.Orders, a);

        case "quote":
            a["symbol"] = pos.ElementAtOrDefault(0) ?? flags.GetValueOrDefault("symbol") ?? "";
            return (Ops.Quote, a);
        case "position":
            a["symbol"] = pos.ElementAtOrDefault(0) ?? "";
            return (Ops.Position, a);
        case "order":
            a["id"] = pos.ElementAtOrDefault(0) ?? "";
            return (Ops.Order, a);

        case "buy":
        case "sell":
            a["symbol"] = pos.ElementAtOrDefault(0) ?? flags.GetValueOrDefault("symbol") ?? "";
            a["quantity"] = pos.ElementAtOrDefault(1) ?? flags.GetValueOrDefault("quantity") ?? "";
            Opt("limit"); Opt("stop"); Opt("tif"); Opt("comment");
            return (cmd == "buy" ? Ops.Buy : Ops.Sell, a);

        case "modify":
            a["id"] = pos.ElementAtOrDefault(0) ?? "";
            Opt("quantity"); Opt("limit"); Opt("stop");
            return (Ops.Modify, a);

        case "cancel":
            a["id"] = pos.ElementAtOrDefault(0) ?? "";
            return (Ops.Cancel, a);
        case "material":
        {
            var sub = (pos.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
            if (sub is "list" or "ls")
            {
                Opt("origin");
                return (Ops.MaterialList, a);
            }

            // trade material ran <sha> some words about it
            // trade material derived <sha> --from <sha> some words
            // trade material note "some words"            (a bare note needs no subject)
            a["kind"] = sub;
            var rest = pos.Skip(1).ToList();
            var bare = sub == "note" && rest.Count == 1 && !flags.ContainsKey("sha");
            if (!bare && rest.Count > 0) a["sha"] = flags.GetValueOrDefault("sha") ?? rest[0];
            else Opt("sha");
            a["text"] = flags.GetValueOrDefault("text")
                        ?? string.Join(' ', bare ? rest : rest.Skip(1));
            Opt("from");
            return (Ops.MaterialNote, a);
        }

        case "cancel-all": return (Ops.CancelAll, a);
        case "close":
            a["symbol"] = pos.ElementAtOrDefault(0) ?? "";
            return (Ops.Close, a);
        case "close-all": return (Ops.CloseAll, a);

        default: return (null, a);
    }
}

static void Usage()
{
    Console.WriteLine("""
    trade — TradeAgent's trading interface.

      trade status                     everything at a glance
      trade schema --json              machine-readable description of every command
      trade accounts | account
      trade instruments
      trade quote <symbol>
      trade positions | position <symbol>
      trade orders [--all] | order <id>
      trade executions

      trade buy  <symbol> <qty> [--limit P] [--stop P] [--tif Day] [--request-id ID]
      trade sell <symbol> <qty> [--limit P] [--stop P] [--tif Day] [--request-id ID]
      trade modify <id> [--quantity Q] [--limit P] [--stop P]
      trade cancel <id> | trade cancel-all
      trade close <symbol> | trade close-all

      trade material list [--origin inbox|agent]     what the owner gave you, and what you made
      trade material ran <sha> <what it did>         you executed it
      trade material used <sha> <how you used it>    you read or worked from it
      trade material derived <sha> --from <sha> <how>  this file came from that one
      trade material note [<sha>] <anything>         anything else worth recording

    Add --json to any command for machine-readable output. That is the canonical interface.

    Retries: every order command carries a request id, printed on stderr BEFORE the order is sent
    and included in --json output. Reusing the SAME --request-id is always safe — it returns the
    original outcome and never places a second order. Use a NEW id for a new order.

    If a command dies without a reply, the order may still have reached the broker. Re-run it with
    the SAME --request-id, or read `trade orders` first. Never retry with a new id.
    """);
}
