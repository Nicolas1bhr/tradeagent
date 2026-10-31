using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.ProvingHost;

// THE PROVING HOST (U-proving-ground-c item 1; docs/ORCHESTRATION.md § 2.14: "a separate app process with its own home, pipe and
// registers, never inside the real one"; § 7: "plumbing proved with puppet seats at no cost"). A program a test starts, never
// shipped: it lives under tests/, no src project references it, and no packaging script publishes it.
//
//   TradeAgent.ProvingHost run --run <run.json>
//       In the proving home TRADEAGENT_HOME names (marked proving, never the default) on the pipe TRADEAGENT_PIPE names (never the
//       real gateway's): seeds the run's synthetic markets once, their campaigns at the configurations' equal budgets; copies the
//       seeded stores once per configuration (VACUUM INTO, SHA-256 recorded); starts this program again per configuration in
//       `apply` mode, in that copy as ITS home and on a pipe of its own; scores each configuration from its home's own rows
//       against the markets' truths into <home>/proving.db; prints each score's sentence; exits.
//   TradeAgent.ProvingHost apply --configuration <file> --scripts <dir> --puppet <dll> --trade <dir> --report <file>
//       In its own proving home: composes the app as a puppet life does (no collector, no network, the fake broker), applies the
//       configuration, runs it until it is done or its deadline, writes what it saw to the report, exits.
//
// Exit codes: 0 done; 2 refused, the sentence on stderr, nothing run; 3 failed, the reason on stderr.

// FIRST, BEFORE ANYTHING READS A PATH: this process is a proving ground and a test host for puppet seats and the activation gate.
// Each flag is internal to the product's assemblies and visible to this program alone among programs (InternalsVisibleTo).
ProvingGround.ThisProcess = true;
PuppetSeats.TestHost = true;
PositionActivation.TestHost = true;

try { _ = Paths.Home; }
catch (TypeInitializationException ex) when (ex.InnerException is TradeAgentException refused)
{
    Console.Error.WriteLine(refused.Message);
    return 2;
}

var mode = args.FirstOrDefault();
var flags = Flags(args.Skip(1).ToArray());
try
{
    return mode switch
    {
        "run" => await Runner.Run(Need(flags, "run")),
        "apply" => await Applier.Apply(Need(flags, "configuration"), Need(flags, "scripts"), Need(flags, "puppet"), Need(flags, "trade"),
            Need(flags, "report")),
        _ => Refuse("the proving host runs 'run --run <file>' or 'apply --configuration <file> ...'.")
    };
}
catch (Refused r)
{
    Console.Error.WriteLine(r.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine("the proving host failed: " + ex);
    return 3;
}

static int Refuse(string why)
{
    Console.Error.WriteLine(why);
    return 2;
}

static Dictionary<string, string> Flags(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < a.Length; i += 2)
        if (a[i].StartsWith("--", StringComparison.Ordinal)) d[a[i][2..]] = a[i + 1];
    return d;
}

static string Need(Dictionary<string, string> f, string name) =>
    f.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new Refused($"the proving host needs --{name}.");

namespace TradeAgent.ProvingHost
{
    /// <summary>A refusal in words: exit 2, nothing run.</summary>
    sealed class Refused(string message) : Exception(message);

    /// <summary>One market of a run, as its file states it.</summary>
    /// <remarks>A market starts <c>StartDay</c> days after the run's start: markets over disjoint windows have campaigns that charge
    /// each other nothing (a run's bars are charged to every open campaign whose months they overlap).</remarks>
    sealed record RunMarket(ulong Seed, string Instrument, int StartDay, int Days, int HoldoutFromDay, long? BetaTicks, string? Trap);

    /// <summary>
    /// A RUN, AS DATA: the markets every configuration works on, the configurations, and the programs the seats run. Refused, never
    /// guessed, when it is not exactly this build's version.
    /// </summary>
    sealed record RunFile(DateTimeOffset Start, IReadOnlyList<RunMarket> Markets, IReadOnlyList<string> Configurations)
    {
        public const int Version = 1;

        public static RunFile Read(string file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.GetInt32() != Version)
                throw new Refused($"the run file {file} is not format version {Version}, the only one this build reads. Nothing was run.");
            var markets = root.GetProperty("markets").EnumerateArray().Select(m => new RunMarket(
                ulong.Parse(m.GetProperty("seed").GetString()!, CultureInfo.InvariantCulture),
                m.GetProperty("instrument").GetString()!,
                m.TryGetProperty("start_day", out var d) ? d.GetInt32() : 0,
                m.GetProperty("days").GetInt32(),
                m.GetProperty("holdout_from_day").GetInt32(),
                m.TryGetProperty("beta_ticks", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null,
                m.TryGetProperty("trap", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)).ToList();
            if (markets.Count == 0) throw new Refused($"the run file {file} names no market. Nothing was run.");
            return new RunFile(
                DateTimeOffset.Parse(root.GetProperty("start").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                markets,
                [.. root.GetProperty("configurations").EnumerateArray().Select(c => c.GetString()!)]);
        }

        public SyntheticMarketSpec Spec(RunMarket m, ProvingConfiguration budgets)
        {
            SyntheticEdge? edge = m.BetaTicks is not { } beta ? null : m.Trap switch
            {
                null => new SyntheticEdge(beta),
                "pre-fee-only" => SyntheticEdge.PreFeeOnly(beta),
                "in-sample-only" => SyntheticEdge.InSampleOnly(beta),
                var other => throw new Refused($"market {m.Instrument} names trap '{other}', and a run plants 'pre-fee-only' or 'in-sample-only'. Nothing was run.")
            };
            return new SyntheticMarketSpec(m.Seed, m.Instrument, Core.Data.VenueCatalog.BinanceSpot, Start.AddDays(m.StartDay), m.Days, edge)
            {
                Holdout = new SyntheticHoldout(m.HoldoutFromDay, budgets.TrialBudget, budgets.VerdictBudget)
            };
        }
    }

    /// <summary>What a configuration's process saw, written for the run's scorer.</summary>
    sealed record ApplyReport(DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool DeadlineReached, List<EnvelopeRefusal> Refusals, List<string> Log);
}
