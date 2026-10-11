using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// E3 AND THE POWER OF THE REFEREE'S OWN GATES (<c>U-referee-v2a</c> item 1): <see cref="Deflation"/> and
/// <see cref="GatePower"/> against the two sources this unit is built from — R04's worked example
/// (<c>docs/research/2026-10-02/R04-evolution-statistics.md</c>:72) and the E2/E3 rows of
/// <c>docs/research/2026-10-02/R10-calc/cascade_power.py</c>, run and quoted below.
/// </summary>
public class DeflationTests
{
    static readonly DateOnly Day0 = new(2026, 1, 1);

    /// <summary>A stream of known days from <paramref name="from"/>, one per date.</summary>
    internal static List<DailyReturn> Series(DateOnly from, IEnumerable<double> returns) =>
        [.. returns.Select((r, i) => new DailyReturn(from.AddDays(i), 1, 1m, (decimal)r, i == 0 ? null : from.AddDays(i - 1)))];

    /// <summary>Deterministic normal draws (Box–Muller over a seeded <see cref="Random"/>).</summary>
    internal static IEnumerable<double> Normal(int seed, int n, double mean, double sd)
    {
        var random = new Random(seed);
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            yield return mean + sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    /// <summary>
    /// (a) THE EXPECTED MAXIMUM AND THE DSR MATCH R04, AND THE POWER MODEL MATCHES R10-calc.
    ///
    /// <para>R04 :72 (Bailey &amp; López de Prado's worked example): N = 100, V = ½, T = 1250, γ3 = −3, γ4 = 10, SR̂ = 2.5
    /// gives DSR 0.90 — annual figures over 250 periods a year, converted here to per-period. The power rows are
    /// <c>python3 cascade_power.py</c>'s output, run 2026-10-11:</para>
    /// <code>
    ///   D=  139  SR0.0:0.14  SR0.5:0.21  SR1.0:0.29  SR1.5:0.38  SR2.0:0.47
    ///   D= 1825  SR0.0:0.14  SR0.5:0.44  SR1.0:0.76  SR1.5:0.94  SR2.0:0.99
    ///   D=  139 N= 20 ceiling=3.08 need SR_obs>=5.76  P(pass|SR1,1.5,2)=0.00,0.00,0.01
    ///   D=  730 N= 20 ceiling=1.34 need SR_obs>=2.51  P(pass|SR1,1.5,2)=0.02,0.08,0.24
    ///   D=  730 N=200 ceiling=1.96 need SR_obs>=3.12  P(pass|SR1,1.5,2)=0.00,0.01,0.06
    ///   D= 1825 N= 20 ceiling=0.85 need SR_obs>=1.59  P(pass|SR1,1.5,2)=0.10,0.42,0.82
    ///   D= 1825 N= 50 ceiling=1.02 need SR_obs>=1.75  P(pass|SR1,1.5,2)=0.05,0.29,0.71
    ///   D= 1825 N=200 ceiling=1.24 need SR_obs>=1.97  P(pass|SR1,1.5,2)=0.01,0.15,0.52
    /// </code>
    /// <para>And the depths at which each gate starts to discriminate (P(pass | SR 2) − P(pass | SR 0) ≥ 0.50), the same
    /// model searched day by day: blocks 293; DSR 444 at N_eff 2 (the brief's ≈ 429 is P(pass | SR 2) ≥ 0.50 alone, which
    /// at N_eff 2 is not the ruling's difference: P(pass | SR 0) is 0.015 there), 1,148 at 20, 1,776 at 200.</para>
    /// </summary>
    [Fact]
    public void Expected_max_and_dsr_match_r04_and_the_r10_calc_power_table()
    {
        // R04's worked example.
        var perYear = Math.Sqrt(250);
        var sr0 = Math.Sqrt(0.5) * Deflation.ExpectedMax(100) / perYear;
        var dsr = Deflation.Psr(2.5 / perYear, sr0, 1250, -3, 10);
        Assert.NotNull(dsr);
        Assert.Equal(0.90, Math.Round(dsr!.Value, 2));
        Assert.Equal(2.53, Math.Round(Deflation.ExpectedMax(100), 2));
        Assert.Equal(0, Deflation.ExpectedMax(1));

        // E2's rows.
        Near(0.14, GatePower.BlocksPass(139, 0));
        Near(0.29, GatePower.BlocksPass(139, 1));
        Near(0.47, GatePower.BlocksPass(139, 2));
        Near(0.44, GatePower.BlocksPass(1825, 0.5));
        Near(0.76, GatePower.BlocksPass(1825, 1));
        Near(0.99, GatePower.BlocksPass(1825, 2));

        // E3's rows: ceiling, the SR needed, and P(pass | SR 1, 1.5, 2).
        foreach (var (d, n, ceiling, need, p1, p15, p2) in new[]
                 {
                     (139, 20, 3.08, 5.76, 0.00, 0.00, 0.01),
                     (730, 20, 1.34, 2.51, 0.02, 0.08, 0.24),
                     (730, 200, 1.96, 3.12, 0.00, 0.01, 0.06),
                     (1825, 20, 0.85, 1.59, 0.10, 0.42, 0.82),
                     (1825, 50, 1.02, 1.75, 0.05, 0.29, 0.71),
                     (1825, 200, 1.24, 1.97, 0.01, 0.15, 0.52)
                 })
        {
            Near(ceiling, GatePower.NullCeiling(d, n));
            Near(need, GatePower.DsrNeeded(d, n));
            Near(p1, GatePower.DsrPass(d, n, 1.0));
            Near(p15, GatePower.DsrPass(d, n, 1.5));
            Near(p2, GatePower.DsrPass(d, n, 2.0));
        }

        Assert.Equal(293, GatePower.DaysToDiscriminate(GatePower.Blocks));
        Assert.Equal(444, GatePower.DaysToDiscriminate(d => GatePower.Dsr(d, 2)));
        Assert.Equal(1148, GatePower.DaysToDiscriminate(d => GatePower.Dsr(d, 20)));
        Assert.Equal(1776, GatePower.DaysToDiscriminate(d => GatePower.Dsr(d, 200)));
    }

    /// <summary>A figure the script printed to two decimals: within its rounding, and the z quantile's last digits.</summary>
    static void Near(double quoted, double computed) =>
        Assert.True(Math.Abs(quoted - computed) <= 0.006, $"the script printed {quoted:0.00} and this build computes {computed:0.0000}");

    /// <summary>
    /// (b) CORRELATED STREAMS CLUSTER, AND A TRIAL WITHOUT A STREAM CLUSTERS ALONE.
    ///
    /// <para>A and B (B = 2A plus a little noise) are one bet; C is independent; D has no stream; E is A's own returns
    /// shifted so the two share only 20 known days — under the 30 a correlation is read over, so it is not joined; F
    /// has no stream either and is not joined by G, which is A again and joins A's cluster. N_eff 5 of M 7, two unstreamed.
    /// A trial with no stream counted as no trial, or as a member of anyone's cluster, would lower N_eff and the hurdle.</para>
    /// </summary>
    [Fact]
    public void Correlated_streams_cluster_and_a_trial_without_a_stream_clusters_alone()
    {
        var a = Normal(1, 60, 0, 0.02).ToList();
        var noise = Normal(2, 60, 0, 0.002).ToList();
        var trials = new List<TrialStream>
        {
            new("A", Series(Day0, a)),
            new("B", Series(Day0, a.Select((r, i) => 2 * r + noise[i]))),
            new("C", Series(Day0, Normal(3, 60, 0, 0.02))),
            new("D", null),
            new("E", Series(Day0.AddDays(40), a)),
            new("F", null),
            new("G", Series(Day0, a))
        };

        var clusters = Deflation.Cluster(trials);

        Assert.Equal([0, 0, 1, 2, 3, 4, 0], clusters.ClusterOf);
        Assert.Equal(5, clusters.NEff);
        Assert.Null(clusters.RepresentativeSr[2]);
        Assert.Null(clusters.RepresentativeSr[4]);
        Assert.NotNull(clusters.RepresentativeSr[0]);

        var measured = Deflation.Of(trials[0].Days!, trials);
        Assert.Equal(7, measured.M);
        Assert.Equal(2, measured.Unstreamed);
        Assert.Equal(5, measured.NEff);
        Assert.Equal(60, measured.Days);
        Assert.True(measured.Sr0 > 0, "five clusters set no hurdle");
        Assert.NotNull(measured.Dsr);
        Assert.Equal(measured.Sr0!.Value * Math.Sqrt(365), measured.Ceiling!.Value, 12);

        // One cluster sets no hurdle; and with under two representatives that have a ratio, V is the null's 1/(T−1).
        Assert.Equal(0, Deflation.Sr0([0.1], 1, 60));
        Assert.Equal(Math.Sqrt(1.0 / 59) * Deflation.ExpectedMax(3), Deflation.Sr0([0.1, null, null], 3, 60)!.Value, 12);
    }

    /// <summary>
    /// (c) A GATE BELOW ITS POWER ANSWERS INCONCLUSIVE, EVEN WHEN ITS STATISTIC PASSES.
    ///
    /// <para>A 139-day stream earning a per-day Sharpe of about 1 passes both statistics by a mile — every block positive,
    /// DSR ≈ 1 — and neither gate can tell SR 2 from SR 0 at 139 days, so both read <c>inconclusive</c>. The mutant is the
    /// power check removed from <see cref="GatePower.Status"/>: the passing DSR at 139 days then reads as enforceable.
    /// Deep enough, the same statistic reads enforceable — and an unknown statistic never does.</para>
    /// </summary>
    [Fact]
    public void A_gate_below_its_power_answers_inconclusive_even_when_its_statistic_passes()
    {
        var stream = Series(Day0, Normal(4, 139, 0.01, 0.01));
        var measured = Deflation.Of(stream, [new TrialStream("only", stream)]);
        var blocks = BlockTest.Of(stream);

        Assert.True(measured.Dsr >= GatePower.DsrPassAt, $"the statistic does not pass: DSR {measured.Dsr}");
        Assert.True(BlockTest.Passes(blocks.Positive), $"the blocks do not pass: {blocks.Positive}/8");

        Assert.Equal(GateStatus.Inconclusive, GatePower.Status(GatePower.Dsr(139, measured.NEff), measured.Dsr is not null));
        Assert.Equal(GateStatus.Inconclusive, GatePower.Status(GatePower.Blocks(139), true));

        Assert.Equal(GateStatus.Enforceable, GatePower.Status(GatePower.Dsr(1825, 1), true));
        Assert.Equal(GateStatus.Enforceable, GatePower.Status(GatePower.Blocks(1825), true));
        Assert.Equal(GateStatus.Inconclusive, GatePower.Status(GatePower.Dsr(1825, 1), false));
    }
}

/// <summary>E2's sub-period blocks (<c>U-referee-v2a</c> item 1).</summary>
public class BlockTests
{
    static readonly DateOnly Day0 = new(2026, 1, 1);

    /// <summary>
    /// (d) A DAY WITH NO BAR IS NO DAY OF ANY BLOCK.
    ///
    /// <para>Eight losing days, twenty-four days with no bar, eight winning days: over the sixteen KNOWN days the blocks
    /// hold two each, four losing and four winning. Read as forty dates with the unknown ones as 0, the blocks would be
    /// five dates each — four of them all-unknown "flat" blocks and only two winning — which is unknown read as zero. And
    /// a stream of five known days fills five blocks, all positive, and does not pass: an unknown block is no positive.</para>
    /// </summary>
    [Fact]
    public void A_day_with_no_bar_is_no_day_of_any_block()
    {
        var days = new List<DailyReturn>();
        for (var i = 0; i < 40; i++)
        {
            var day = Day0.AddDays(i);
            days.Add(i switch
            {
                < 8 => new DailyReturn(day, 1, 1m, -0.01m, null),
                < 32 => new DailyReturn(day, 0, null, null, null),
                _ => new DailyReturn(day, 1, 1m, 0.01m, null)
            });
        }

        Assert.Equal((16, 8, 4), BlockTest.Of(days));

        var five = DeflationTests.Series(Day0, [0.01, 0.01, 0.01, 0.01, 0.01]);
        var (known, blocks, positive) = BlockTest.Of(five);
        Assert.Equal((5, 5, 5), (known, blocks, positive));
        Assert.False(BlockTest.Passes(positive));
    }
}
