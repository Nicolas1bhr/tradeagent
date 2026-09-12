using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE THREE APP-OWNED TABLES AT SCHEMA 12.
///
/// <para><b>What was wrong before this.</b> A program was a file in <c>strategies/</c>. It had an
/// identity in memory — <see cref="StrategyProgram.StrategyId"/>, computed by `U-runner-1` — and
/// nothing anywhere wrote it down, so two sessions that offered the same rule set had no way to be
/// talking about the same strategy, and a run had nowhere to be recorded at all. Accumulated evidence
/// about a strategy is the referee's whole subject (`docs/COUNCIL.md`, "The runner and the referee":
/// "lineage by hash; metrics computed by the app from its own trace"), and evidence attached to a
/// file name is evidence about nothing.</para>
///
/// <para><b>The mutant this class exists to catch</b> is an id minted from the ATTEMPT rather than
/// from the program: every restart a new version, every run the first run of a brand-new strategy,
/// and a trial budget that can never be spent because nothing is ever the same submission twice.</para>
/// </summary>
public class BacktestLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    const string Text = """
        instrument BTCUSDT
        const fast = 5
        indicator fastma = sma(close, fast)
        size fixed 1
        exit when close < fastma
        entry when close > fastma
        """;

    static StrategyProgram Parsed(string text = Text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>A dataset row a run can point at. No bars are read here; the FK is what needs one.</summary>
    static long Dataset(Database db, string sha = "aa11")
    {
        var record = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(Paths.Data, "not-read-here.csv"), sha, 100, At, At.AddMinutes(99),
            0, [], false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []);

        return new DatasetStore(db).Record(record);
    }

    static StrategyVersionRow Version(StrategyProgram program, string? role = null, string? attempt = null) =>
        new(program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, At, role, attempt);

    static StrategyRunRow Run(string id, string versionId, long datasetId, string sha = "aa11") =>
        new(id, versionId, datasetId, sha, At, At.AddHours(1), "fees=0.001;slippage=0;increment=1;capital=10000",
            "COMPLETED", null, 60, 2, 1, 3, 4, 21, 0, 0, 12.5m, 0.5m, 12m, 3.25m, "beef", At, null, null);

    static StrategyTradeRow Trade(string runId, int ordinal) =>
        new(runId, ordinal, At.AddMinutes(ordinal), 100m, At.AddMinutes(ordinal + 5), 101m, 1m,
            "Rule", 0.2m, 0.8m);

    /// <summary>
    /// THE SAME PROGRAM, OFFERED TWICE, IS ONE VERSION — and its id is the program's own hash, not a
    /// number this table handed out.
    ///
    /// <para>The two <see cref="StrategyProgram"/> objects here are separately parsed from separately
    /// spelled text: different spacing, a comment, the constant declared after the indicator that uses
    /// it. `U-runner-1` made all of that invisible to the identity, and this is the row that has to
    /// agree — offered on Monday and again on Friday, one lineage.</para>
    /// </summary>
    [Fact]
    public void A_program_offered_twice_is_one_version_and_the_row_is_keyed_by_the_programs_own_id()
    {
        using var db = TestEnv.NewDb();
        var store = new StrategyStore(db);

        var first = Parsed();
        var again = Parsed("""
            # the same program, spelled differently
            instrument   BTCUSDT
            indicator fastma = sma(close, fast)
            const fast = 5
            size fixed 1
            exit  when close < fastma
            entry when close > fastma
            """);
        Assert.Equal(first.StrategyId, again.StrategyId);        // `U-runner-1`'s promise, restated

        Assert.Equal(first.StrategyId, store.RecordVersion(Version(first, role: CouncilRoles.Research)));
        Assert.Equal(again.StrategyId, store.RecordVersion(Version(again, role: CouncilRoles.Operations)));

        var row = Assert.Single(store.AllVersions());
        Assert.Equal(first.StrategyId, row.Id);
        Assert.Equal(first.Canonical, row.Canonical);
        Assert.Equal(first.Manifest, row.Manifest);
        Assert.Equal(first.WarmUpBars, row.WarmUpBars);
        Assert.Equal(ParseVerdict.Accepted, row.ParseVerdict);

        // THE FIRST WRITER WINS, which is what makes a re-offer a no-op rather than a rewrite: the
        // role that got there first is still the role on the row.
        Assert.Equal(CouncilRoles.Research, row.Role);

        // The interpreter build is recorded and names both halves the brief asks for.
        Assert.Contains(Versions.App, row.InterpreterBuild);
        Assert.Contains($"language={StrategyVersions.LanguageVersion}", row.InterpreterBuild);

        // The source is kept verbatim beside the canonical form, comments and spacing and all.
        Assert.Equal(first.Source, store.VersionById(first.StrategyId)!.Source);
    }

    /// <summary>
    /// A RUN IS IDENTIFIED BY ITS INPUTS, AND ITS TRADES COME WITH IT.
    ///
    /// <para>Recording the same run twice is recording the same row twice, which the primary key
    /// refuses — and the trades are inserted only with the run, so a second request cannot double the
    /// trade list of a run that already exists.</para>
    /// </summary>
    [Fact]
    public void Recording_one_run_twice_leaves_one_run_one_set_of_trades_and_the_first_account_of_it()
    {
        using var db = TestEnv.NewDb();
        var store = new StrategyStore(db);
        var program = Parsed();
        store.RecordVersion(Version(program));
        var dataset = Dataset(db);

        var run = Run("run-1", program.StrategyId, dataset);
        store.RecordRun(run, [Trade("run-1", 0), Trade("run-1", 1)]);
        store.RecordRun(run with { Trades = 99, NetPnl = -5m }, [Trade("run-1", 0)]);

        var only = Assert.Single(store.Runs());
        Assert.Equal("run-1", only.Id);
        Assert.Equal(program.StrategyId, only.VersionId);
        Assert.Equal(dataset, only.DatasetId);
        Assert.Equal("aa11", only.DatasetSha256);
        Assert.Equal(2, only.Trades);                       // the first account of it, not the second
        Assert.Equal(12m, only.NetPnl);
        Assert.Equal(1, store.RunCount);

        var trades = store.TradesOf("run-1");
        Assert.Equal(2, trades.Count);
        Assert.Equal([0, 1], trades.Select(t => t.Ordinal));
        Assert.Equal(100m, trades[0].EntryPrice);
        Assert.Equal(0.8m, trades[0].Pnl);
    }

    /// <summary>
    /// A DECIMAL COMES BACK OUT AS IT WENT IN, and an UNKNOWN comes back as null rather than as zero.
    ///
    /// <para>The columns are TEXT for the reason <c>fill</c>'s are: a price that made a round trip
    /// through a double is a different price. And a run that faulted before it measured anything has
    /// no net result — null, which a reader prints as a dash, never as a flat outcome.</para>
    /// </summary>
    [Fact]
    public void Money_survives_the_round_trip_and_an_unmeasured_figure_comes_back_null()
    {
        using var db = TestEnv.NewDb();
        var store = new StrategyStore(db);
        var program = Parsed();
        store.RecordVersion(Version(program));
        var dataset = Dataset(db);

        store.RecordRun(Run("exact", program.StrategyId, dataset) with
        {
            GrossPnl = 1234.56789012345m, Fees = 0.00000001m, NetPnl = 1234.56789011345m,
            MaxDrawdown = 0.12345m
        }, []);
        store.RecordRun(Run("faulted", program.StrategyId, dataset) with
        {
            Outcome = "FAULTED", FaultReason = "a bar arrived out of order",
            Bars = 3, Trades = 0, Wins = 0, Intents = 0, Fills = 0, ExposureBars = 0,
            GrossPnl = null, Fees = null, NetPnl = null, MaxDrawdown = null
        }, []);

        var exact = store.RunById("exact");
        Assert.NotNull(exact);
        Assert.Equal(1234.56789012345m, exact.GrossPnl);
        Assert.Equal(0.00000001m, exact.Fees);
        Assert.Equal(0.12345m, exact.MaxDrawdown);

        var faulted = store.RunById("faulted");
        Assert.NotNull(faulted);
        Assert.Equal("FAULTED", faulted.Outcome);
        Assert.Equal("a bar arrived out of order", faulted.FaultReason);
        Assert.Null(faulted.NetPnl);
        Assert.Null(faulted.MaxDrawdown);
        Assert.Null(faulted.Fees);
    }

    /// <summary>
    /// THE THREE TABLES ARRIVE AT SCHEMA 12, AND AN OLDER DATABASE GAINS THEM EMPTY.
    ///
    /// <para>12 is this class's FLOOR and not the build's number, for the reason
    /// <c>OwnerDispositionTests</c> and <c>CouncilRoleTests</c> already give: pinning the exact figure
    /// makes every later additive migration fail here for no reason of its own. The row on disk is
    /// still required to equal what this build writes, so an upgrade that did not run is still
    /// caught.</para>
    /// </summary>
    [Fact]
    public void The_strategy_ledger_arrives_at_schema_twelve_and_starts_empty()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= 12,
            $"the strategy ledger needs schema 12 or later; this build says {Versions.DatabaseSchemaVersion}");
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return c.ExecuteScalar() as string;
        }));

        foreach (var table in new[] { "strategy_version", "strategy_run", "strategy_trade" })
            Assert.Equal(1L, db.Read(_ =>
            {
                using var c = db.Cmd(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", table));
                return (long)c.ExecuteScalar()!;
            }));

        // Empty, which reads correctly as "this installation has measured no strategy yet" — and is
        // what section 8 of the owner's report then says.
        var store = new StrategyStore(db);
        Assert.Empty(store.AllVersions());
        Assert.Empty(store.Runs());
        Assert.Equal(0, store.RunCount);
    }
}
