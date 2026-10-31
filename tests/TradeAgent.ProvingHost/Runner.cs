using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Gateway;
using TradeAgent.Security;

namespace TradeAgent.ProvingHost;

/// <summary>
/// ONE RUN OF A LADDER (<c>U-proving-ground-c</c>): seed once, copy once per configuration, run each in a process of its own, score.
/// </summary>
static class Runner
{
    /// <summary>How long past its deadline a configuration's process may take to stop and write its report before it is killed.</summary>
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public static async Task<int> Run(string runFile)
    {
        var run = RunFile.Read(runFile);
        var configurations = new List<(ProvingConfiguration Config, string File)>();
        foreach (var file in run.Configurations)
        {
            var (c, why) = ProvingConfiguration.Parse(File.ReadAllText(file), file);
            if (c is null) throw new Refused(why + " Nothing was run.");
            configurations.Add((c, Path.GetFullPath(file)));
        }
        // EQUAL BUDGETS, OR NO RUN (BUDGET-001): refused before anything is seeded.
        if (ProvingConfiguration.UnequalBudgets([.. configurations.Select(c => c.Config)]) is { } unequal) throw new Refused(unequal);
        var budgets = configurations[0].Config;

        var started = DateTimeOffset.UtcNow;
        var home = ProvingHome.Create(Paths.Home);
        var truths = await Seed(home, run, budgets);
        Console.WriteLine($"{ProvingScore.Label} proving run in {home.Dir}: {truths.Count} market(s) seeded, {configurations.Count} configuration(s) at {ProvingConfiguration.Budgets(budgets)}");

        using var store = new ProvingStore(Path.Combine(home.Dir, ProvingStore.FileName));
        var runId = store.BeginRun(started, home.Dir, JsonSerializer.Serialize(run.Markets));
        for (var i = 0; i < configurations.Count; i++)
        {
            var (config, file) = configurations[i];
            var dir = Path.Combine(home.Dir, "configurations", $"{i + 1}-{config.Name}");
            var sha = CopySeed(home, dir);
            var report = await ApplyIn(dir, file, $"{Paths.PipeName}-c{i + 1}", config.DeadlineSeconds);
            using var db = new Database(Path.Combine(dir, "state", "tradeagent.db"), HomeKind.Proving);
            var score = ProvingScorer.Score(db, config, sha, truths, report.StartedAt, report.Refusals);
            store.Record(runId, i + 1, score);
            Console.WriteLine(score.Sentence);
        }
        Console.WriteLine($"run {runId} scored into {store.File}");
        return 0;
    }

    /// <summary>
    /// THE SEED: every market written through the real collection path, the venue declared, each market's campaign opened at the
    /// configurations' equal budgets, setup finished and the owner's rate set — once, so every configuration starts from it.
    /// </summary>
    static async Task<List<MarketTruth>> Seed(ProvingHome home, RunFile run, ProvingConfiguration budgets)
    {
        var truths = new List<MarketTruth>();
        using var db = new Database(home.DatabaseFile, HomeKind.Proving);

        // THE SETTINGS FIRST: the gateway that writes them syncs the built-in venue catalogue as it is made, and the synthetic
        // instruments' rows are declared after it.
        var connector = new FakeConnector(new FakeBroker());
        await using (var gateway = new TradingGateway(db, connector, new HealthRegistry()))
            gateway.Update(s =>
            {
                s.SelectedRuntimeId = RuntimeManifest.PuppetId;
                s.AiPriceInputPerMillion = 2m;
                s.AiPriceOutputPerMillion = 8m;
                s.AiDailyCostCap = budgets.AiEnvelope;
            });
        using (var tape = new TapeStore(home.TapeFile, kind: HomeKind.Proving))
        {
            var written = run.Markets.Select(m => (Spec: run.Spec(m, budgets), Market: (SyntheticMarketWritten?)null)).ToList();
            for (var i = 0; i < written.Count; i++)
                written[i] = (written[i].Spec, SyntheticMarket.Write(home, db, tape, written[i].Spec));
            new VenueStore(db).Sync(SyntheticMarket.Venues(Core.Data.VenueCatalog.BinanceSpot, run.Markets.Select(m => m.Instrument)));
            foreach (var (spec, market) in written)
            {
                var opened = ProvingCampaign.Open(home, db, market!, spec, spec.Start.AddDays(spec.Holdout!.FromDay));
                if (!opened.Ok) throw new InvalidOperationException($"the campaign over {spec.Instrument} was not opened: {opened.Why}");
                truths.Add(MarketTruth.Read(market!.TruthFile));
            }
        }

        new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);
        return truths;
    }

    /// <summary>
    /// ONE CONFIGURATION'S HOME: the seeded database and tape copied whole (<c>VACUUM INTO</c>, one consistent image each) into
    /// <paramref name="dir"/>'s state folder, and nothing else — the markets' truth stays in the seeded home, where no seat looks.
    /// Returns the SHA-256 over the two copies, which is equal for every configuration of the run.
    /// </summary>
    static string CopySeed(ProvingHome seed, string dir)
    {
        var state = Path.Combine(dir, "state");
        if (Directory.Exists(dir)) throw new InvalidOperationException($"{dir} already exists; a configuration's home is made fresh.");
        Directory.CreateDirectory(state);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (from, name) in new[] { (seed.DatabaseFile, "tradeagent.db"), (seed.TapeFile, "tape.db") })
        {
            var to = Path.Combine(state, name);
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "VACUUM INTO $to";
                cmd.Parameters.AddWithValue("$to", to);
                cmd.ExecuteNonQuery();
            }
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(to)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// THIS PROGRAM AGAIN, IN <paramref name="dir"/> AS ITS HOME AND ON <paramref name="pipe"/>: the configuration applied and run
    /// there, its report read back. A process that does not stop by its deadline and the grace is killed and the run fails.
    /// </summary>
    static async Task<ApplyReport> ApplyIn(string dir, string configuration, string pipe, int deadlineSeconds)
    {
        var report = Path.Combine(dir, "apply-report.json");
        var host = typeof(Runner).Assembly.Location;
        var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("this process cannot name its own program");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { host, "apply", "--configuration", configuration, "--scripts", Path.Combine(dir, "puppet"),
                     "--puppet", Built("puppet"), "--trade", Built("trade"), "--report", report })
            start.ArgumentList.Add(a);
        start.Environment["TRADEAGENT_HOME"] = dir;
        start.Environment["TRADEAGENT_PIPE"] = pipe;
        start.Environment.Remove(AgentGrants.Variable);   // a configuration's launches carry the grants ITS app mints, never one inherited

        using var p = Process.Start(start) ?? throw new InvalidOperationException("the configuration's process did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(deadlineSeconds) + Grace);
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"the configuration in {dir} did not stop within its deadline and {Grace.TotalMinutes} minutes; it was killed.");
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "apply.out.txt"), await stdout + "\n--- stderr ---\n" + await stderr);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"the configuration in {dir} exited {p.ExitCode}: {(await stderr).Trim()}");
        return JsonSerializer.Deserialize<ApplyReport>(await File.ReadAllTextAsync(report))
               ?? throw new InvalidOperationException($"the configuration in {dir} wrote no report");
    }

    /// <summary>Where this host's build put the puppet and the trade command: beside it, by the host's own project.</summary>
    static string Built(string what)
    {
        var name = what == "puppet" ? "PuppetDll" : "TradeCliDir";
        var value = typeof(Runner).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == name).Value!;
        return Path.GetFullPath(value);
    }
}
