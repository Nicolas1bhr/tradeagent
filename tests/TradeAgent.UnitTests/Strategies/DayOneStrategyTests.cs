using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE THREE PROGRAMS `docs/COUNCIL.md` REQUIRES THE LANGUAGE TO EXPRESS ON DAY ONE, as files, each
/// pinned to an id.
///
/// <para>COUNCIL's round-3 specification ends with a test rather than a feature list: "Day one must
/// express: a moving-average crossover with fixed sizing and stop; an opening-range breakout with ATR
/// risk sizing and a time stop; an RSI mean reversion with a profit exit and a maximum holding time."
/// Those three are the fixtures beside this file, they are the same bytes that
/// `docs/STRATEGY-LANGUAGE.md` shows a model — asserted below, because a specification and an example
/// that drift apart teach the wrong language — and each is pinned to a `StrategyId`.</para>
///
/// <para><b>What a pinned id buys.</b> Results, a trial budget and a lineage attach to an id
/// (`docs/COUNCIL.md`, the referee), so the identity of a program has to be stable across builds, not
/// merely deterministic within one. These three figures are the tripwire: any change to the canonical
/// layout, to the manifest, to how a number is normalised or to how warm-up is computed changes them,
/// and that change then has to be made deliberately — with a version bumped in `StrategyVersions` —
/// rather than noticed later by an installation whose stored results no longer match anything.</para>
/// </summary>
public class DayOneStrategyTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tests", "TradeAgent.UnitTests", "Strategies", name));

    static StrategyProgram Parsed(string name)
    {
        var parse = StrategyParser.Parse(Fixture(name));
        Assert.True(parse.Ok, $"{name} did not parse: {parse.Why}");
        return parse.Program!;
    }

    /// <summary>
    /// THE GOLDEN IDS. Each one was also computed OUTSIDE this build — `sha256` over the canonical
    /// text, a newline, the parameter lines, a newline and the manifest, exactly as
    /// `docs/COUNCIL.md` states the rule — so what is pinned here is the documented formula's answer
    /// rather than whatever this build happened to produce. Every input to them is asserted below.
    /// </summary>
    const string CrossoverId = "8873b58693fc00af44a0a630ddcad9a7b6c3b61dcbbbb45f8ea6373e6b14edaf";
    const string BreakoutId = "88f6586a99138dde124993655df33f074ea5711fb1b712525f59e495247386a0";
    const string MeanReversionId = "eeb614304febec859879ee3f747d91a44dc899d525eb294a37454e803a806548";

    /// <summary>
    /// THE FIXTURES AND THE DOCUMENT ARE THE SAME BYTES. The document is what a model is shown; these
    /// files are what is proven to parse. If they can drift, the language that is specified and the
    /// language that works are two languages, and only one of them has tests.
    /// </summary>
    [Theory]
    [InlineData("ma-crossover.strategy")]
    [InlineData("opening-range-breakout.strategy")]
    [InlineData("rsi-mean-reversion.strategy")]
    public void Each_fixture_is_the_program_the_document_prints(string name)
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "STRATEGY-LANGUAGE.md"));

        Assert.Contains($"`{name}`\n```\n{Fixture(name).TrimEnd('\n')}\n```", doc.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// <summary>A moving-average crossover with fixed sizing and a stop (`docs/COUNCIL.md:164`).</summary>
    [Fact]
    public void The_moving_average_crossover_parses_canonicalises_and_hashes_to_its_id()
    {
        var p = Parsed("ma-crossover.strategy");

        Assert.Equal("BTCUSDT", p.Instrument);
        Assert.Equal(new Sizing(SizingKind.FixedQuantity, 1m), p.Sizing);
        Assert.Equal(new StopRule(StopKind.Percent, 1.5m, 0), p.Stop);
        Assert.Equal(51, p.WarmUpBars);
        Assert.Equal([RuleKind.Exit, RuleKind.Entry], p.Rules.Select(r => r.Kind));

        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            days all
            ind fastma=sma(close,20)
            ind slowma=sma(close,50)
            size fixed:1
            stop percent:1.5
            target none
            hold none
            warmup 51
            exit (crosses_below @fastma @slowma)
            entry (crosses_above @fastma @slowma)

            """.ReplaceLineEndings("\n"), p.Canonical);
        Assert.Equal("fast=number:20\nslow=number:50", p.Parameters);
        Assert.Equal(CrossoverId, p.StrategyId);
    }

    /// <summary>An opening-range breakout with ATR risk sizing and a time stop (`docs/COUNCIL.md:165`).</summary>
    [Fact]
    public void The_opening_range_breakout_parses_canonicalises_and_hashes_to_its_id()
    {
        var p = Parsed("opening-range-breakout.strategy");

        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m), p.Sizing);
        Assert.Equal(new StopRule(StopKind.EntryAtr, 2m, 14), p.Stop);       // risk is measured over an ATR distance
        Assert.Equal(120, p.MaxHoldBars);                                    // the time stop
        Assert.Equal(new TimeOfDay(15, 55), p.Time.SessionExit);
        Assert.Equal(new TimeWindow(new TimeOfDay(9, 30), new TimeOfDay(10, 0)), p.Time.OpeningRange);
        Assert.Equal("America/New_York", p.Time.TimeZone);
        Assert.Equal(15, p.WarmUpBars);                                      // atr(14) needs fifteen bars

        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone America/New_York
            days mon,tue,wed,thu,fri
            window 10:00-15:30
            openrange 09:30-10:00
            sessionexit 15:55
            ind rangehigh=opening_range_high()
            ind rangelow=opening_range_low()
            ind truerange=atr(14)
            size risk_fraction:0.01
            stop atr:2:14
            target none
            hold 120
            warmup 15
            exit (< low @rangelow)
            entry (> close @rangehigh)

            """.ReplaceLineEndings("\n"), p.Canonical);
        Assert.Equal("atrmultiple=number:2\natrperiod=number:14\nriskfraction=number:0.01", p.Parameters);
        Assert.Equal(BreakoutId, p.StrategyId);
    }

    /// <summary>An RSI mean reversion with a profit exit and a maximum holding time (`docs/COUNCIL.md:166`).</summary>
    [Fact]
    public void The_rsi_mean_reversion_parses_canonicalises_and_hashes_to_its_id()
    {
        var p = Parsed("rsi-mean-reversion.strategy");

        Assert.Equal(new Sizing(SizingKind.CapitalFraction, 0.25m), p.Sizing);
        Assert.Equal(new TargetRule(TargetKind.Percent, 1m), p.Target);       // the profit exit
        Assert.Equal(60, p.MaxHoldBars);                                      // the maximum holding time
        Assert.Equal(new StopRule(StopKind.Percent, 2m, 0), p.Stop);
        Assert.Equal(15, p.WarmUpBars);                                       // rsi(14) needs fifteen bars

        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            days all
            ind momentum=rsi(close,14)
            size capital_fraction:0.25
            stop percent:2
            target percent:1
            hold 60
            warmup 15
            exit (> @momentum 55)
            entry (< @momentum 30)

            """.ReplaceLineEndings("\n"), p.Canonical);
        Assert.Equal("oversold=number:30\nperiod=number:14\nrecovered=number:55", p.Parameters);
        Assert.Equal(MeanReversionId, p.StrategyId);
    }

    /// <summary>
    /// THE ROUND TRIP, ON ALL THREE: re-spelling a fixture — stripped of its comment, respaced, its
    /// declarations shuffled, its line endings turned into Windows ones — is the same program, with the
    /// same canonical form and the same id. This is the property the referee's lineage rests on, tested
    /// on the three programs the product actually ships with rather than on a contrived one.
    /// </summary>
    [Theory]
    [InlineData("ma-crossover.strategy")]
    [InlineData("opening-range-breakout.strategy")]
    [InlineData("rsi-mean-reversion.strategy")]
    public void Each_fixture_round_trips_through_the_canonical_form_whatever_its_spelling(string name)
    {
        var original = Parsed(name);
        var lines = Fixture(name).Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        // Rules keep their order; every other declaration is moved to the end, which must change nothing.
        var declarations = lines.Where(l => !l.StartsWith("exit ") && !l.StartsWith("entry ")).ToList();
        var rules = lines.Where(l => l.StartsWith("exit ") || l.StartsWith("entry ")).ToList();

        foreach (var spelling in new[]
        {
            string.Join("\n", rules.Concat(declarations)),                        // rules first, no comment
            string.Join("\r\n", declarations.Concat(rules)) + "\r\n",             // Windows line endings
            string.Join("\n\n", declarations.Select(l => "  " + l + "   ").Concat(rules)),   // respaced
            string.Join("\n", declarations.AsEnumerable().Reverse().Concat(rules)),          // declared backwards
        })
        {
            var parse = StrategyParser.Parse(spelling);
            Assert.True(parse.Ok, $"{name} re-spelled did not parse: {parse.Why}");
            Assert.Equal(original.Canonical, parse.Program!.Canonical);
            Assert.Equal(original.Parameters, parse.Program.Parameters);
            Assert.Equal(original.StrategyId, parse.Program.StrategyId);
        }
    }

    /// <summary>The three are three different programs, which a shared id would deny.</summary>
    [Fact]
    public void The_three_ids_are_three()
    {
        Assert.Equal(3, new HashSet<string> { CrossoverId, BreakoutId, MeanReversionId }.Count);
    }
}
