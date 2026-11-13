using System.Diagnostics;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE WATCH'S DIGEST SEES AN UNPROTECTED PAPER RUN (<c>U-digest-polish</c>; the 72-hour runbook's case J). The run's line says
/// "UNPROTECTED: it holds …" while a position stands without the stop or target it declared, and the app re-asks by itself; the
/// digest a watcher reads every three hours said nothing of it unless it happened to be the last day note. Here the real runner,
/// gateway and paper venue write the home, and the digest is run over that home as a watcher runs it.
/// </summary>
public partial class ForwardRunnerTests
{
    /// <summary>
    /// WHILE THE RUN STANDS UNPROTECTED THE DIGEST SAYS SO IN THE RUN'S OWN SENTENCE, word for word, on its run and in ATTENTION;
    /// the minute its protection rests the words are gone from both.
    ///
    /// <para>RED at base: the digest's deployment had no <c>unprotected</c> at all, and ATTENTION held nothing of it.</para>
    /// </summary>
    [OnlyOnFact("the digest runs on the Linux host; python3 is there and on macOS", "linux", "macos")]
    public async Task The_digest_says_a_run_stands_unprotected_in_the_runs_own_words()
    {
        var state = Path.Combine(TestEnv.Home, $"digest-state-{Guid.NewGuid():n}");
        Directory.CreateDirectory(state);
        await using var rig = await ReadyAsync(Protected, db: new Database(Path.Combine(state, "tradeagent.db")));
        var since = rig.Clock.At.AddHours(-1);

        await RefusedProtectionAsync(rig);
        var words = Assert.Single(rig.Gw.DeploymentReadings()).Unprotected;
        Assert.NotNull(words);
        Assert.Contains("no stop and no target", words, StringComparison.Ordinal);

        var digest = Digest(state, since, rig.Clock.At);
        var run = Assert.Single(digest.GetProperty("deployments").GetProperty("deployments").EnumerateArray());
        Assert.Equal(words, run.GetProperty("unprotected").GetString());
        var flags = digest.GetProperty("attention").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Contains(flags, a => a.StartsWith("paper run ", StringComparison.Ordinal) && a.EndsWith(": " + words, StringComparison.Ordinal));

        // THE SAME RULE WHERE A REFUSED STOP HAS AN ORDER ROW: none here (the rate refused it before the write-ahead row), so it is missing. A write-ahead row a
        // gate refused before the wire leaves it missing; one a person's press can still send may yet rest, so only the target is. The app's reading and the
        // digest's, over the same rows, are one.
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var stop = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop);
        var entry = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Entry);
        Assert.Equal(DeploymentOpState.Refused, stop.State);
        Assert.Null(rig.Gw.Requests.Get(stop.RequestId));
        foreach (var held in new[] { ExecutionState.CREATED, ExecutionState.AWAITING_APPROVAL })
        {
            rig.Db.Exec($"""
                DELETE FROM execution_request WHERE request_id = '{stop.RequestId}';
                INSERT INTO execution_request(request_id, connector_id, account_id, instrument, intent, parameters, client_order_id, created_at, dispatched_at,
                                              execution_state, filled_quantity, mode, updated_at)
                  SELECT '{stop.RequestId}', connector_id, account_id, instrument, intent, parameters, 'digest-test-{held}', created_at, NULL, '{held}', '0', mode, updated_at
                  FROM execution_request WHERE request_id = '{entry.RequestId}';
                """);
            var app = Assert.Single(rig.Gw.DeploymentReadings()).Unprotected;
            var read = Assert.Single(Digest(state, since, rig.Clock.At).GetProperty("deployments").GetProperty("deployments").EnumerateArray());
            Assert.Equal(app, read.GetProperty("unprotected").GetString());
            Assert.Contains(held == ExecutionState.CREATED ? "no stop and no target" : "with no target", app, StringComparison.Ordinal);
        }
        rig.Db.Exec($"DELETE FROM execution_request WHERE request_id = '{stop.RequestId}';");
        Assert.Equal(words, Assert.Single(rig.Gw.DeploymentReadings()).Unprotected);

        // ITS PROTECTION RESTS AGAIN: the run's line says nothing of it, and neither does the digest.
        await QuietMinuteAsync(rig, 4);
        Assert.Null(Assert.Single(rig.Gw.DeploymentReadings()).Unprotected);
        digest = Digest(state, since, rig.Clock.At);
        run = Assert.Single(digest.GetProperty("deployments").GetProperty("deployments").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("unprotected").ValueKind);
        Assert.DoesNotContain(digest.GetProperty("attention").EnumerateArray(), a => a.GetString()!.Contains("UNPROTECTED", StringComparison.Ordinal));
    }

    /// <summary>The digest over <paramref name="state"/>, as JSON, the way a watch runs it from this checkout.</summary>
    static JsonElement Digest(string state, DateTimeOffset since, DateTimeOffset now)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-I", Path.Combine(TradeAgent.Tests.DayOnePrograms.RepoRoot(), "tools", "org-digest.py"), "--state", state,
                     "--since", since.ToString("o"), "--now", now.ToString("o"), "--no-systemd", "--json" })
            psi.ArgumentList.Add(a);
        psi.Environment["TZ"] = "UTC";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "the digest did not finish in a minute");
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {err.Result}");
        return JsonDocument.Parse(output.Result).RootElement.Clone();
    }
}
