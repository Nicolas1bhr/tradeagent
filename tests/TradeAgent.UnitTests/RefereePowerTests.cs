using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The tests that make this process a proving process (<see cref="ProvingGround.ThisProcess"/>), as a proving host's entry
/// does: run alone, after every parallel class, so no other test opens a store or parses a program as a proving home's.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProvingProcess
{
    public const string Name = "a proving process";
}

/// <summary>
/// THIS PROCESS AS A PROVING HOST'S (the <c>PuppetSeats.TestHost</c> pattern): the flag set for the class's life and cleared
/// after it, so the UNCHANGED referee — which parses a version in its process's kind — judges a program over the proving
/// ground's series. <c>Paths</c> is resolved first, as a real process, so its first use is never a proving one's.
/// </summary>
public sealed class AsProvingProcess : IDisposable
{
    public AsProvingProcess()
    {
        _ = Paths.Home;
        ProvingGround.ThisProcess = true;
    }

    public void Dispose() => ProvingGround.ThisProcess = false;
}

/// <summary>
/// THE REFEREE OVER MARKETS WHOSE TRUTH IS KNOWN (<c>U-proving-ground-b</c> items 3 to 5; <c>docs/EDGE-FACTORY.md</c> § 4.5:
/// "the referee computes the power of its own gates"; <c>docs/VISION.md</c> § 6.4: "an empirical check of the referee's own
/// power"). Each market is written into a proving home of its own, its holdout and campaign set by
/// <see cref="ProvingCampaign.Open"/> as the owner would, one program frozen before the cutoff, and the UNCHANGED
/// <see cref="Referee.Verdict"/> asked once. A PASS is a promotion or a paper eligibility; anything else is not.
///
/// <para><b>Honest scope:</b> process, not market truth. The power measured here is the referee's on these synthetic
/// markets and this program; that it equals the referee's power on real markets is NOT claimed.</para>
/// </summary>
[Collection(ProvingProcess.Name)]
public class RefereePowerTests(ITestOutputHelper log) : IClassFixture<AsProvingProcess>
{
    static readonly DateTimeOffset Start = PlantedEdgeTests.Start;

    /// <summary>The one program every market is judged on: long while the instrument's signal is above 0.0030, flat otherwise.</summary>
    static string Program(string symbol) =>
        PlantedEdgeTests.Program(symbol, ProvingSources.Signal, ProvingSources.ValueField, "x > 0.0030", "x <= 0.0030");

    /// <summary>What one verdict said.</summary>
    internal sealed record Judged(string Verdict, string Reason)
    {
        public bool Pass => Verdict is PromotionVerdict.Promoted or PromotionVerdict.PaperEligible;
    }

    /// <summary>
    /// ONE MARKET, JUDGED: written into a fresh proving home under <paramref name="dir"/>, the venue catalogue declared, its
    /// holdout and campaign set by the proving process at the cutoff, the program frozen at the market's start, and the
    /// referee asked for a verdict an hour after its end.
    /// </summary>
    internal static Judged Judge(string dir, SyntheticMarketSpec spec, ITestOutputHelper? log = null)
    {
        using var p = new PlantedEdgeTests.Proving(dir);
        var market = p.Write(spec);
        new VenueStore(p.Db).Sync(SyntheticMarket.Venues(spec.VenueId, [spec.Instrument]));
        var opened = ProvingCampaign.Open(p.Home, p.Db, market, spec, Start.AddDays(spec.Holdout!.FromDay));
        Assert.True(opened.Ok, opened.Why);

        var parsed = StrategyParser.Parse(Program(spec.Instrument));
        Assert.True(parsed.Ok, parsed.Why);
        var program = parsed.Program!;
        new StrategyStore(p.Db).RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, spec.Start, CouncilRoles.Research, "attempt-1")
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var reader = new TapeReader(p.Home.TapeFile, kind: HomeKind.Proving);
        var verdict = new Referee(p.Db, () => spec.Start.AddDays(spec.Days).AddHours(1), tape: () => reader)
            .Verdict(program.StrategyId, opened.Campaign!.Id);
        Assert.True(verdict.Ok, verdict.Why);
        log?.WriteLine($"{spec.Instrument} seed {spec.Seed}: {verdict.Promotion!.Verdict} / {verdict.Promotion.Reason}");
        return new Judged(verdict.Promotion!.Verdict, verdict.Promotion.Reason);
    }

    /// <summary>
    /// (d) THE IN-SAMPLE REGIME PASSES RESEARCH AND FAILS THE VERDICT. A market whose edge is on for its first three weeks and
    /// off from the holdout cutoff: the unchanged backtest over the months before the cutoff, as the Research Director's
    /// launch at the venue's cost, nets above zero — and the referee, over the held-back week, refuses it. A real edge of the
    /// same size beside it, judged the same way, passes: the refusal is the trap's, not the setup's.
    /// </summary>
    [Fact]
    public void The_in_sample_regime_passes_research_and_fails_the_verdict()
    {
        using var scratch = TestEnv.NewScratch("in-sample");
        var holdout = new SyntheticHoldout(21, TrialBudget: 10, VerdictBudget: 2);
        var trap = new SyntheticMarketSpec(71, "SYNA", VenueCatalog.BinanceSpot, Start, 28, SyntheticEdge.InSampleOnly(16)) { Holdout = holdout };
        var real = new SyntheticMarketSpec(72, "SYNB", VenueCatalog.BinanceSpot, Start, 28, new SyntheticEdge(16)) { Holdout = holdout };

        // RESEARCH, BEFORE THE CUTOFF: a proving home of its own, the bars and the tape up to the hour before it.
        using (var p = new PlantedEdgeTests.Proving(Path.Combine(scratch.Dir, "research")))
        {
            var market = p.Write(trap);
            new VenueStore(p.Db).Sync(SyntheticMarket.Venues(VenueCatalog.BinanceSpot, ["SYNA"]));
            var opened = ProvingCampaign.Open(p.Home, p.Db, market, trap, Start.AddDays(21));
            Assert.True(opened.Ok, opened.Why);
            var research = PlantedEdgeTests.Backtest(log, p.Home, p.Db, market.Dataset, Program("SYNA"),
                PlantedEdgeTests.VenueCost(p.Db, market.Dataset), Start, Start.AddDays(21).AddMinutes(-61));
            Assert.True(research.Metrics.Trades >= 20, $"research traded {research.Metrics.Trades} times");
            Assert.True(research.Metrics.NetPnl > 0m, $"research nets {research.Metrics.NetPnl} on the in-sample edge");

            var said = PlantedEdgeTests.Truth(market).GetProperty("traps")[0];
            Assert.Equal("in-sample-only", said.GetProperty("trap").GetString());
            Assert.StartsWith("no edge on the held-back months", said.GetProperty("honest_app_says").GetString(), StringComparison.Ordinal);
        }

        // THE VERDICT, OVER THE HELD-BACK WEEK: the trap refused, the real edge passed.
        var trapped = Judge(Path.Combine(scratch.Dir, "trap"), trap, log);
        var control = Judge(Path.Combine(scratch.Dir, "control"), real, log);
        Assert.False(trapped.Pass, $"the referee passed the in-sample trap: {trapped}");
        Assert.Equal(PromotionVerdict.Refused, trapped.Verdict);
        Assert.True(control.Pass, $"the referee refused a real edge of the same size: {control}");
    }

    /// <summary>The planted sizes, β in ticks per unit of the signal; 0 is noise.</summary>
    static readonly long[] Sizes = [0, 6, 8, 12];

    /// <summary>The holdout depths, in days judged.</summary>
    static readonly int[] Depths = [2, 6, 14];

    /// <summary>Seeds per cell: bounded so one CI shard carries the table.</summary>
    const int Seeds = 12;

    /// <summary>PASS counts by (size, depth), computed once per class.</summary>
    static readonly Lazy<(Dictionary<(long Size, int Depth), int> Passes, string Table)> Power = new(Measure);

    static (Dictionary<(long, int), int>, string) Measure()
    {
        using var scratch = TestEnv.NewScratch("referee-power");
        var passes = new Dictionary<(long, int), int>();
        foreach (var size in Sizes)
            foreach (var depth in Depths)
            {
                var n = 0;
                for (var i = 0; i < Seeds; i++)
                {
                    var spec = new SyntheticMarketSpec((ulong)(100_000 * (size + 1) + 1000 * depth + i), "SYNA", VenueCatalog.BinanceSpot,
                        Start, depth + 1, size == 0 ? null : new SyntheticEdge(size)) { Holdout = new SyntheticHoldout(1, 10, 2) };
                    if (Judge(Path.Combine(scratch.Dir, $"b{size}-d{depth}-s{i}"), spec).Pass) n++;
                }
                passes[(size, depth)] = n;
            }

        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"referee PASS rate over {Seeds} seeds; program: long while signal > 0.0030; venue {VenueCatalog.BinanceSpot}\n");
        table.Append("beta(ticks/unit) bps/signal | ").AppendJoin(" | ", Depths.Select(d => $"{d,2} d holdout")).Append('\n');
        foreach (var size in Sizes)
        {
            var bps = size * PlantedEdges.MeanAbsoluteValue * 10_000m / SyntheticMarket.StartTicks;
            table.Append(CultureInfo.InvariantCulture, $"{(size == 0 ? "noise" : size.ToString(CultureInfo.InvariantCulture)),16} {bps,10:0.00} | ")
                .AppendJoin(" | ", Depths.Select(d => $"{passes[(size, d)],2}/{Seeds} = {passes[(size, d)] * 100 / Seeds,3}%"))
                .Append('\n');
        }
        return (passes, table.ToString());
    }

    void Print()
    {
        var table = Power.Value.Table;
        log.WriteLine(table);
        log.WriteLine($"table sha256 {Sha256Hex.Of(table)}");
    }

    /// <summary>
    /// (e) THE REFEREE PASSES NOISE NO MORE OFTEN THAN ITS STATED α. Over the bounded seeds of the power table, at each holdout
    /// depth, the share of noise markets the unchanged referee passes is at or below the holdout's stated α
    /// (<see cref="AlphaWealth.Alpha"/>, R04's α_paper).
    /// </summary>
    [Fact]
    public void The_referee_passes_noise_no_more_often_than_its_stated_alpha()
    {
        Print();
        foreach (var depth in Depths)
        {
            var rate = (double)Power.Value.Passes[(0, depth)] / Seeds;
            Assert.True(rate <= AlphaWealth.Alpha, $"over a {depth}-day holdout the referee passed {rate:P0} of noise markets, above α {AlphaWealth.Alpha}");
        }
    }

    /// <summary>
    /// ITEM 5: THE REFEREE'S POWER RISES WITH THE PLANTED SIZE AND THE HOLDOUT DEPTH. The PASS rate over the bounded seeds is
    /// non-decreasing in β at every depth and in depth at every planted β, and the strongest, deepest cell passes more often
    /// than the weakest, shallowest — so the table discriminates. Printed, with its SHA-256, so the three CI platforms' tables
    /// can be compared.
    /// </summary>
    [Fact]
    public void The_referees_power_rises_with_the_planted_size_and_the_holdout_depth()
    {
        Print();
        var pass = Power.Value.Passes;
        foreach (var depth in Depths)
            for (var i = 1; i < Sizes.Length; i++)
                Assert.True(pass[(Sizes[i], depth)] >= pass[(Sizes[i - 1], depth)],
                    $"at {depth} days, β {Sizes[i]} passes {pass[(Sizes[i], depth)]} and β {Sizes[i - 1]} passes {pass[(Sizes[i - 1], depth)]}");
        foreach (var size in Sizes.Where(s => s > 0))
            for (var i = 1; i < Depths.Length; i++)
                Assert.True(pass[(size, Depths[i])] >= pass[(size, Depths[i - 1])],
                    $"β {size} passes {pass[(size, Depths[i])]} at {Depths[i]} days and {pass[(size, Depths[i - 1])]} at {Depths[i - 1]}");
        Assert.True(pass[(Sizes[^1], Depths[^1])] > pass[(Sizes[1], Depths[0])], "the table does not discriminate");
        Assert.True(pass[(Sizes[^1], Depths[^1])] > pass[(0, Depths[^1])], "the strongest edge passes no more often than noise");
    }
}
