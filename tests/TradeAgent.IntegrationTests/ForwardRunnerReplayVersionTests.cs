using System.Globalization;
using System.Text.RegularExpressions;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A RUN IS REPLAYED ONLY UNDER THE REPLAY SEMANTICS IT WAS RECORDED UNDER (<c>U-runner-maxhold-replay</c>, the Codex
/// review's finding): every pass rebuilds a run from its first minute, and a run an earlier build decided replays under
/// this build's reading into a state it never had. The run carries the semantics it was started under
/// (<c>strategy_deployment.replay_version</c>, rung 34), and a run whose stamp is not this build's — or that has none —
/// is ended in words naming both, before anything is settled, replayed or sent. Nothing it recorded is rewritten.
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>A run's stamp as an earlier build would have left it: none (before rung 34), or another number.</summary>
    static void Stamped(Rig rig, int? replayVersion) =>
        rig.Db.Write(_ =>
        {
            using var c = rig.Db.Cmd("UPDATE strategy_deployment SET replay_version=$v WHERE id=$id",
                ("$v", replayVersion), ("$id", rig.Deployment.Id));
            return c.ExecuteNonQuery();
        });

    /// <summary>
    /// (r1) A RUN RECORDED UNDER OTHER REPLAY SEMANTICS IS ENDED IN WORDS NAMING BOTH VERSIONS, AND NEVER REPLAYED: the
    /// bar that would enter a run of this build's is stepped for nobody, no entry, exit, stop or target is written and
    /// nothing reaches the wire; the run's line says which semantics it was recorded under and which this build replays
    /// under.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Versions.ForwardReplay + 1)]
    public async Task A_run_recorded_under_other_replay_semantics_is_ended_in_words_and_never_replayed(int? stamp)
    {
        await using var rig = await ReadyAsync(ProgramText());
        Assert.Equal(Versions.ForwardReplay, rig.Deployment.ReplayVersion);

        Stamped(rig, stamp);
        rig.Bar(1, 99m, 101m, 98m, 101m);      // entry when close > 100 — a run of this build's enters here
        var state = Assert.Single(await rig.Runner.AdvanceAsync());

        Show(log, rig);
        log.WriteLine($"ended: {state.Ended}");
        // NO OPERATION OF THE RUN'S OWN: the one row is the END's close of a book that is flat, which every end writes.
        var op = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        Assert.Equal(DeploymentOpKind.Flatten, op.Kind);
        Assert.Equal("there was nothing to close: the run's own book is flat", op.Answer);
        Assert.Empty(await Wire(rig));
        Assert.Equal(0, state.BarsReplayed);

        var ended = rig.Deployment;
        Assert.True(ended.IsEnded);
        Assert.Equal(state.Ended, ended.EndReason);
        Assert.Contains(stamp is { } other
            ? $"replay semantics {other.ToString(CultureInfo.InvariantCulture)}"
            : "no replay semantics", ended.EndReason);
        Assert.Contains($"this build replays under {Versions.ForwardReplay.ToString(CultureInfo.InvariantCulture)}",
            ended.EndReason);

        // AND WHAT IT RECORDED STAYS AS IT WAS WRITTEN: the stamp is not restated by the end.
        Assert.Equal(stamp, ended.ReplayVersion);
    }

    /// <summary>
    /// (r2) THE REVIEW'S CASE: AN HOURLY RUN AN EARLIER BUILD ENTERED AFTER ITS MAXIMUM HOLD IS NEVER PROTECTED FROM A
    /// SIGNAL IT NEVER SENT.
    ///
    /// <para>The rig of (d2) with <c>stop percent 5</c>. Under the earlier build the 15:00 hour's flatten (minute 239)
    /// left a phantom <c>max_hold_bars</c> exit pending on every later pass, so the 16:00 hour's close (299, at 100) wrote
    /// nothing and the entry went out at the 17:00 hour's close (359, at 110). That ledger is written here as the
    /// earlier build left it: this build's passes through minute 298, where both builds write the same operations; the
    /// cursor moved over 299–358 as that build's passes moved it, writing nothing; the 359 entry through the runner's
    /// own one way onto the money path; and the run's stamp as that build left it — none. The home is then opened by
    /// this build and the entry fills at 110.</para>
    ///
    /// <para><b>Replayed under this build's reading</b>, minute 299 reads flat and emits an entry nobody sent, the 359
    /// order in flight suppresses its own signal, and the fill's stop is sized from the phantom: 110 − (100 − 95) = 105,
    /// not 110 × 0.95 = 104.5. <b>Refused instead</b>: the run is ended in words at its first pass under this build, and
    /// no stop is ever written for the fill.</para>
    /// </summary>
    [Fact]
    public async Task A_run_an_earlier_build_entered_after_its_maximum_hold_is_never_protected_from_a_signal_it_never_sent()
    {
        await using var rig = await ReadyAsync(HourlyText("max_hold_bars 2\nstop percent 5\n"));
        var id = rig.Deployment.Id;

        await EnteredAsync(rig);
        rig.Minutes(62, HourClose(4) - 1, _ => 100m);
        Assert.Single(await rig.Runner.AdvanceAsync());
        for (var minute = HourClose(4); minute <= HourClose(5) - 1; minute++)
            await FlatMinuteAsync(rig, minute, 100m);
        Assert.Contains(rig.Gw.Deployments.OpsOf(id), o => o.Kind == DeploymentOpKind.Flatten && MinuteOf(rig, o) == HourClose(4));

        // THE EARLIER BUILD'S 16:00 AND 17:00 HOURS: minute 299 at 100 and the rest at 100, read and nothing written —
        // its phantom exit held the 299 entry back — then 359 at 110 and the entry it sent there.
        rig.Minutes(HourClose(5), HourClose(6) - 1, _ => 100m);
        await rig.Gw.RefreshHealthAsync();
        Assert.True(rig.Gw.Deployments.AdvanceCursor(id, rig.Origin.AddMinutes(HourClose(6) - 1)));
        rig.Bar(HourClose(6), 110m, 110m, 110m, 110m);
        await rig.Gw.RefreshHealthAsync();

        var first = rig.Gw.Deployments.OpsOf(id).First(o => o.Kind == DeploymentOpKind.Entry);
        var then = Json.Read<PlaceIntent>(first.IntentJson)!;
        var decision = then.Decision!;
        var hour = rig.Origin.AddHours(5);
        var sent = then with
        {
            Decision = decision with { BarOpen = hour, BarClose = hour.AddHours(1) }
        };
        Assert.True(await rig.Gw.RunDeploymentIntentAsync(rig.Deployment,
            Deployments.RequestIdFor(id, rig.Origin.AddMinutes(HourClose(6)), 0), DeploymentOpKind.Entry,
            rig.Origin.AddMinutes(HourClose(6)), sent));
        Assert.Equal([HourClose(1), HourClose(6)], rig.Gw.Deployments.OpsOf(id)
            .Where(o => o.Kind == DeploymentOpKind.Entry).Select(o => MinuteOf(rig, o)).ToList());

        // THE HOME, OPENED BY THIS BUILD: the run as the earlier build started it, with no stamp.
        Stamped(rig, null);

        var ends = new List<string?>();
        for (var minute = HourClose(6) + 1; minute <= HourClose(6) + 3; minute++)
        {
            rig.Bar(minute, 110m, 110m, 110m, 110m);
            await rig.Gw.RefreshHealthAsync();
            ends.AddRange((await rig.Runner.AdvanceAsync()).Select(s => s.Ended));
        }

        Show(log, rig);

        // NEVER A STOP SIZED FROM THE PHANTOM — and none at all: the run is not protected by a replay on a guess.
        var stops = rig.Gw.Deployments.OpsOf(id)
            .Where(o => o.Kind == DeploymentOpKind.Stop && MinuteOf(rig, o) > HourClose(6))
            .Select(o => Json.Read<PlaceIntent>(o.IntentJson)!.StopPrice)
            .ToList();
        Assert.Empty(stops);

        // ENDED IN WORDS, AT ITS FIRST PASS UNDER THIS BUILD, NAMING BOTH.
        var ended = Assert.Single(ends);
        Assert.Contains("no replay semantics", ended);
        Assert.Contains($"this build replays under {Versions.ForwardReplay.ToString(CultureInfo.InvariantCulture)}", ended);
        Assert.True(rig.Gw.Deployments.ById(id)!.IsEnded);
    }

    /// <summary>
    /// (r3) THE STAMP ARRIVES AT RUNG 34, A CRASH BEFORE ITS STAMP RUNS IT AGAIN OVER WHAT IS THERE, AND ONLY
    /// <c>Deployments.Start</c> WRITES IT — this build's number, whatever the caller handed it.
    /// </summary>
    [Fact]
    public void The_replay_stamp_arrives_at_its_rung_and_has_one_writer()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 34,
            "the replay stamp arrives at rung 34; this build says " + Versions.DatabaseSchemaVersion);

        var db = TestEnv.NewDb();
        var started = new Deployments(db).Start(new StrategyDeploymentRow(
            "", "version", "allocation", "envelope", "paper", "account", "BTCUSDT", "PAPER",
            DeploymentState.Active, null, new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), null, null, null)
        {
            ReplayVersion = Versions.ForwardReplay + 7
        });
        Assert.True(started.Ok, started.Why);
        Assert.Equal(Versions.ForwardReplay, started.Deployment!.ReplayVersion);

        var file = db.Read(c => c.DataSource);
        db.Dispose();

        // A CRASH BETWEEN THE RUNG'S STATEMENT AND ITS STAMP: the column in place, the stamp one lower.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = "UPDATE meta SET value='33' WHERE key='schema_version';";
            c.ExecuteNonQuery();
        }

        using (var rerun = new Database(file))
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return (string)c.ExecuteScalar()!;
            }));
            Assert.Equal(Versions.ForwardReplay, new Deployments(rerun).ById(started.Deployment.Id)!.ReplayVersion);
        }

        // NO STATEMENT IN THE PRODUCT RESTATES A RUN'S STAMP: the one writer is the run's own insert.
        var restates = new Regex(@"UPDATE\s+strategy_deployment\b[^;""]*\breplay_version\b", RegexOptions.IgnoreCase);
        var writers = Directory.GetFiles(Path.Combine(Build.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select(line => (f, line)))
            .Where(l => restates.IsMatch(l.line) || l.line.Contains("\"$replay\"", StringComparison.Ordinal))
            .Select(l => $"{Path.GetFileName(l.f)}: {l.line.Trim()}")
            .ToList();
        Assert.Equal(["DeploymentStore.cs: (\"$replay\", Versions.ForwardReplay));"], writers);
    }
}
