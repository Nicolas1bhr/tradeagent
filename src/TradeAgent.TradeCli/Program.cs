using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.TradeCli;

// trade — the stable command surface every supported agent uses.
//
// --json is the canonical interface: it always prints one object with ok/data/error, so an agent can
// branch on structure rather than parse prose. Human output is a convenience for the person watching.

// THE macOS/LINUX LAUNCHER, AND IT IS THE FIRST THING THIS PROGRAM DOES. It opens no pipe, reads no
// token and parses no verb: it starts a session of its own and becomes the program it was handed, so
// that TradeAgent can kill a cancelled turn by its process group. See ContainedLaunch for why the
// launcher has to be a separate process at all.
if (ContainedLaunch.TryRun(args, Console.Error, out var launcherExit)) return launcherExit;

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

// What is known about where the frame got to. It is set by the TRANSPORT, from what it observed,
// and never by this file in advance — the previous version assigned it on the line before the write
// began, so it could not tell a completed frame from a zero-byte one (Codex F3).
var outcome = TransportOutcome.NothingWritten;

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

    var attempt = await client.TrySendAsync(request);
    outcome = attempt.Outcome;
    if (attempt.Reply is not { } reply) throw attempt.Failure!;

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
    if (CliReplayContract.SuccessNote(op) is { } note)
        Console.WriteLine($"\n{note}");
    // THE CREDIT A SOURCE'S TERMS ASK FOR, ON ITS OWN LINE BENEATH ITS ROWS (U-tape-read): GDELT's terms ask every use
    // of its data to cite the project and link to its site, so a person reading the answer sees it without looking.
    if (CliReplayContract.CreditLine(reply.Data) is { } credit)
        Console.WriteLine($"\n{credit}");
    return 0;
}
catch (TradeAgentException ex)
{
    // A transport failure after the frame went out is not a failed order; see CliReplayContract.
    var recovery = CliReplayContract.RecoveryLine(outcome, requestId);

    if (wantJson)
    {
        Console.WriteLine(Json.Write(CliReplayContract.UnansweredJson(requestId, outcome, IpcError.From(ex.Info)), true));
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

// THE POSITIONALS FILL, IN ORDER, THE NAMED ARGUMENTS NO FLAG GAVE, and the last of them takes every word left — so
// `trade ledger add lesson stop using minute bars --mark claim` is one text, and a word too many for an id is sent on
// as part of it and refused by the gateway rather than dropped here.
static void Fill(Dictionary<string, object> a, List<string> rest, params string[] names)
{
    var open = names.Where(n => !a.ContainsKey(n)).ToList();
    for (var i = 0; i < open.Count && i < rest.Count; i++)
        a[open[i]] = i == open.Count - 1 ? string.Join(' ', rest.Skip(i)) : rest[i];
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

        // `--all` is consumed before the argument split, like `orders`; `--since` is a flag with a
        // value and reaches here through `flags`. Neither is defaulted on this side: an unreadable
        // date is refused by the gateway rather than quietly becoming today.
        case "pnl":
            if (all) a["all"] = "true";
            Opt("since");
            return (Ops.Pnl, a);

        // `trade report` and `trade report --day 2026-09-08`. A READ, like `data`: there is no
        // `trade report write`, because the report is the record the agent's work is judged by and
        // the account owner presses Write it now in TradeAgent's own window.
        case "report":
            if (flags.ContainsKey("day")) Opt("day");
            else if (pos.ElementAtOrDefault(0) is { Length: > 0 } d) a["day"] = d;
            return (Ops.Report, a);

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
        // `trade data list` and `trade data bars --pair BTCUSDT --from 2026-08-01 --to 2026-08-02`,
        // with `--source forward` for the bars TradeAgent collected itself minute by minute.
        // Both are READS. There is deliberately no `trade data collect`: the account owner presses
        // that in TradeAgent, and this CLI is the agent's side of the fence.
        case "data":
        {
            var sub = (pos.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
            if (sub is "list" or "ls") return (Ops.DataList, a);

            // `trade data tape --source binance-um-oi --subject BTCUSDT --from D --as-of D`: the market's context as
            // it arrived, newest first, bounded (U-tape-read). A READ like the other two: there is no `trade data
            // record`, because the account owner's two switches are the only control of what the tape records.
            if (sub is "tape")
            {
                a["source"] = flags.GetValueOrDefault("source") ?? pos.ElementAtOrDefault(1) ?? "";
                Opt("series"); Opt("subject"); Opt("from"); Opt("to"); Opt("as-of", "as_of"); Opt("limit"); Opt("before");
                return (Ops.DataTape, a);
            }

            if (sub is not "bars") return (null, a);

            a["pair"] = flags.GetValueOrDefault("pair") ?? pos.ElementAtOrDefault(1) ?? "";
            // `--source forward` asks for the closed minutes TradeAgent collected itself rather than
            // the frozen archive. Left out means the archive, which is what every caller written
            // before forward bars existed meant and still means.
            Opt("from"); Opt("to"); Opt("source");
            return (Ops.DataBars, a);
        }

        // `trade venue list`. A READ, and the only subcommand there is: there is deliberately no
        // `trade venue add`, `set`, `verify` or `check`. An increment is what a size is rounded down to, so
        // an agent that could write one would be choosing how much it trades; the app itself checks an
        // instrument against the venue's own published definition, in-process (U-venue-verify).
        case "venue":
        case "venues":
        {
            var sub = (pos.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
            return sub is "list" or "ls" ? (Ops.VenueList, a) : (null, a);
        }

        // `trade deployment list` and `trade deployment stop --id <deployment>`. There is deliberately
        // no `trade deployment start`: a deployment is written by the app's own policy inside the
        // envelope the account owner pressed once, exactly as an allocation is, and an agent that
        // could start one would be choosing what runs on the owner's practice account. Stopping is
        // here because it only ever removes exposure.
        case "deployment":
        case "deployments":
        {
            var sub = (pos.ElementAtOrDefault(0) ?? "list").ToLowerInvariant();
            if (sub is "list" or "ls") return (Ops.DeploymentList, a);
            if (sub is "stop" or "end")
            {
                a["id"] = flags.GetValueOrDefault("id") ?? pos.ElementAtOrDefault(1) ?? "";
                return (Ops.DeploymentStop, a);
            }
            return (null, a);
        }

        // `trade backtest --strategy strategies/x.strategy --dataset 3`. A READ as far as trading is
        // concerned: it places no order and grants nothing. The path is resolved inside the caller's
        // own role folder by the gateway, which refuses anything outside it.
        case "backtest":
            a["strategy"] = flags.GetValueOrDefault("strategy") ?? pos.ElementAtOrDefault(0) ?? "";
            a["dataset"] = flags.GetValueOrDefault("dataset") ?? pos.ElementAtOrDefault(1) ?? "";
            Opt("from"); Opt("to"); Opt("fees"); Opt("slippage"); Opt("increment"); Opt("capital");
            // THE RESEARCH-LEDGER ENTRY IT IS ASKED UNDER (U-research-ledger): the gateway checks it is the caller's own
            // before anything runs, and links the run it answers with.
            Opt("entry");
            return (Ops.Backtest, a);

        // `trade run trades --run <id> [--after <ordinal>] [--limit <n>]`: every closed trade of a recorded run, in pages
        // (U-run-trace). A READ, and the only subcommand there is: there is deliberately no `trade run delete` or `edit`,
        // because a run's record is the evidence its author is judged on. The id may be the whole one or its first twelve
        // characters, and the gateway refuses the rest in words; nothing is defaulted here.
        case "run":
        case "runs":
        {
            var sub = (pos.ElementAtOrDefault(0) ?? "").ToLowerInvariant();
            if (sub is not "trades") return (null, a);

            a["run"] = flags.GetValueOrDefault("run") ?? pos.ElementAtOrDefault(1) ?? "";
            Opt("after"); Opt("limit");
            return (Ops.RunTrades, a);
        }

        // `trade verdict --version <hash> [--dataset 3]`. The agent ASKS; the app judges. It reads no
        // bar and hands none back: what comes back is the verdict and its reason class in words. There
        // is deliberately no `--fees`, no `--slippage` and no window — the execution model is the
        // JUDGE'S and the months are the campaign's own, because a submitter that could choose either
        // would be choosing the standard its own evidence was scored against.
        case "verdict":
            a["version"] = flags.GetValueOrDefault("version") ?? pos.ElementAtOrDefault(0) ?? "";
            Opt("dataset");
            // The entry it is asked under, which chooses nothing judged; the app links the promotion it answers with.
            Opt("entry");
            return (Ops.Verdict, a);

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

        // `trade ledger add <kind> <text> --mark M …`, `revise <entry> <text> --mark M --why W …`, `list …` and
        // `show <entry> …`: the research ledger (U-research-ledger), the role's own claims. EVERY FLAG IS SENT ON, under its
        // own name, and the gateway refuses one the verb does not take: a flag dropped here would be a claim written
        // without words its author added — a link, a run — and nobody told. The positionals fill, in the form's order,
        // what no flag named; the text takes every word left.
        case "ledger":
        {
            foreach (var (key, value) in flags)
                if (!key.Equals("request-id", StringComparison.OrdinalIgnoreCase))
                    a[key.Replace('-', '_')] = value;
            if (all) a["all"] = "true";

            var rest = pos.Skip(1).ToList();
            switch ((pos.ElementAtOrDefault(0) ?? "").ToLowerInvariant())
            {
                case "add":
                    Fill(a, rest, "kind", "text");
                    return (Ops.LedgerAdd, a);
                case "revise":
                    Fill(a, rest, "entry", "text");
                    return (Ops.LedgerRevise, a);
                case "list" or "ls":
                    return (Ops.LedgerList, a);
                case "show":
                    Fill(a, rest, "entry");
                    return (Ops.LedgerShow, a);
                default:
                    return (null, a);
            }
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
      trade pnl [--since 2026-09-06] [--all]     what it made or lost, and what that figure misses
      trade report [--day 2026-09-08]            the owner's daily report, exactly as they read it

      trade buy  <symbol> <qty> [--limit P] [--stop P] [--tif TIF] [--request-id ID]
      trade sell <symbol> <qty> [--limit P] [--stop P] [--tif TIF] [--request-id ID]
      trade modify <id> [--quantity Q] [--limit P] [--stop P]
      trade cancel <id> | trade cancel-all
      trade close <symbol> | trade close-all

      trade venue list                               what instruments there are, their increments,
                                                     and whether anybody has checked the numbers
      trade data list                                what history you have, and where it came from
      trade data bars --pair BTCUSDT [--from D] [--to D]   the bars themselves, at most 10000 a call
      trade data tape --source binance-um-oi [--series S] [--subject BTCUSDT] [--from D] [--to D]
                     [--as-of D] [--limit 1000] [--before ID]
                     the market's context as it ARRIVED, newest first: each row with the vendor's
                     time, the instant TradeAgent received it, its revision and its evidence class.
                     At most 5000 rows a call; an answer that stopped early says so and gives the
                     --before that continues it. --as-of reads only what had arrived by then
      trade backtest --strategy strategies/x.strategy --dataset 3 [--from D] [--to D]
                     [--fees 0.001] [--slippage 0.0005] [--increment 0.001] [--capital 10000] [--entry E]
                     run one of your own programs over that history and record it. The four
                     model numbers are yours to declare and are part of the run's identity;
                     omitted, the run declares no friction at all and says so
      trade run trades --run <id> [--after <ordinal>] [--limit 1000]
                     every closed trade of a recorded run — yours or the other director's —
                     beside its row, in ordinal order, at most 1000 a call; an answer that
                     stopped early says so and gives the --after that continues it. The
                     referee's holdout run, and a run a holdout window reaches, are refused
      trade verdict --version <hash> [--dataset 3]   ask TradeAgent to judge a version you have
                     already backtested, over the months it holds back from you. It COSTS one of
                     the campaign's final judgements — three by default, counted across renewals
                     and never reset — and what comes back is a verdict and a reason class in
                     words, never a figure. Asking twice about one version is one judgement.
                     --entry on either names your own research-ledger entry it is asked under, and
                     TradeAgent links the run or the verdict it answers with

      trade material list [--origin inbox|agent]     what the owner gave you, and what you made
      trade material ran <sha> <what it did>         you executed it
      trade material used <sha> <how you used it>    you read or worked from it
      trade material derived <sha> --from <sha> <how>  this file came from that one
      trade material note [<sha>] <anything>         anything else worth recording

      trade ledger add <kind> <text> --mark M [--confidence P] [--about E] [--source S]
                     write down what you believe — a hypothesis, experiment, finding, kill or lesson —
                     marked claim, assumption or hypothesis: your claim, never a measurement. Leave
                     --confidence out when you cannot say; it is recorded as unknown
      trade ledger revise <entry> <text> --mark M --why W [--confidence P] [--status S]
                     a new revision of one of your own entries; the earlier ones are kept
      trade ledger list [--author R] [--kind K] [--status S] [--limit N] [--before E]
      trade ledger show <entry> [--part P] [--before C]
                     its revisions, the entries about it, and the runs and verdicts TradeAgent
                     linked to it — ask for them with --entry on backtest or verdict; an answer
                     that stopped names the --part and --before that continue it

    --tif is one of Day, GoodTillCancel, ImmediateOrCancel, FillOrKill. Spell it exactly; case does
    not matter, and anything else is refused rather than treated as Day, because a misspelled
    ImmediateOrCancel that quietly became Day would leave a resting order you did not ask for. Leave
    it out for Day. The order type is not a flag: no price is a market order, --limit is a limit
    order, --stop is a stop, both is a stop-limit.

    Add --json to any command for machine-readable output. That is the canonical interface.

    Retries: every order command carries a request id, printed on stderr BEFORE the order is sent
    and included in --json output. Reusing the SAME --request-id is always safe — it returns the
    original outcome and never places a second order. Use a NEW id for a new order.

    If a command dies without a reply, the order may still have reached the broker. Re-run it with
    the SAME --request-id, or read `trade orders` first. Never retry with a new id.
    """);
}
