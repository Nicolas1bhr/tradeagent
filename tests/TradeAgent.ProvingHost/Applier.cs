using System.Globalization;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;

namespace TradeAgent.ProvingHost;

/// <summary>
/// ONE CONFIGURATION, IN A PROCESS AND HOME OF ITS OWN (<c>U-proving-ground-c</c> item 1): the app composed as a puppet life composes it
/// — <c>AppHost.Composed</c> over this home's database, the fake broker, TradeAgent's own <c>trade</c> deployed into this home's
/// <c>bin/</c>, a <see cref="GatewayPipeServer"/> on this process's own pipe — and nothing else: no collector, no network, no model.
/// </summary>
static class Applier
{
    public static async Task<int> Apply(string configurationFile, string scripts, string puppetDll, string tradeDir, string reportFile)
    {
        var (config, why) = ProvingConfiguration.Parse(await File.ReadAllTextAsync(configurationFile), configurationFile);
        if (config is null) throw new Refused(why + " Nothing was run.");

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) && Environment.ProcessPath is { } dotnet)
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(dotnet));

        var log = new List<string>();
        var refusals = new List<EnvelopeRefusal>();
        var db = new Database(Paths.DatabaseFile);   // this process's kind: proving, or the open refuses
        var connector = new FakeConnector(new FakeBroker());
        await connector.ConnectAsync();
        var host = AppHost.Composed(db, connector, new AgentPresence());
        host.Gateway.Tape = new TapeReader(Paths.TapeFile, kind: HomeKind.Proving);

        // THE SYNTHETIC INSTRUMENTS' VENUE ROWS, AGAIN: a gateway syncs the built-in catalogue as it is made, which names no synthetic
        // instrument, so this home's declares them once more — the proving ground's own steps, the venue's published fee unchanged.
        host.Gateway.Venues.Sync(SyntheticMarket.Venues(Core.Data.VenueCatalog.BinanceSpot,
            new DatasetStore(db).All().Where(d => d.Source == ProvingSources.Synthetic).Select(d => d.Pair).Distinct()));

        ProvingConfigurationWriter.Apply(db, config, scripts,
            role => Path.GetFileName(host.HomeFor(role).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            host.Gateway.Update);

        // TRADEAGENT'S OWN trade COMMAND, deployed into THIS home's bin/ as the app deploys it: the launcher every turn is held by.
        if (ToolDeployer.EnsureTradeCli(tradeDir) is null)
            throw new InvalidOperationException($"there is no trade command in {tradeDir} to deploy into {Paths.Bin}");

        var server = new GatewayPipeServer(host.Gateway, IpcToken.Ensure()) { Peer = Containment.PeerRuleNow() };
        server.Start();

        var started = DateTimeOffset.UtcNow;
        var deadline = started.AddSeconds(config.DeadlineSeconds);
        var reached = false;
        try
        {
            if (config.Kind == ProvingConfigurationKind.Floor)
                reached = await Floor.Run(server, db, deadline, log);
            else
                reached = await Seats(host, config, scripts, puppetDll, deadline, log, refusals);
        }
        finally
        {
            try { await host.Mission.PauseAsync(); } catch (Exception) { }
            try { await host.Agent.StopAsync(); } catch (Exception) { }
            try { await server.DisposeAsync(); } catch (Exception) { }
            try { await host.Gateway.DisposeAsync(); } catch (Exception) { }
            try { db.Dispose(); } catch (Exception) { }
        }

        var report = new ApplyReport(started, DateTimeOffset.UtcNow, reached, refusals, log);
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(report));
        foreach (var line in log) Console.WriteLine(line);
        return 0;
    }

    /// <summary>
    /// A CHART OF SEATS, PLAYED BY PUPPETS, run by the host's own loop: the brief to the first position, every later turn woken by the
    /// kernel, until the organisation is quiet. A position left with due wakes nothing consumed while the meter admits no further turn
    /// for it is the envelope's refusal, recorded with those wakes. True when the deadline stopped it.
    /// </summary>
    static async Task<bool> Seats(AppHost host, ProvingConfiguration config, string scripts, string puppetDll, DateTimeOffset deadline,
        List<string> log, List<EnvelopeRefusal> refusals)
    {
        string[] turn = [puppetDll, "--scripts", scripts, "{prompt}"];
        var manifest = new RuntimeManifest
        {
            Id = RuntimeManifest.PuppetId,
            DisplayName = "Puppet seat",
            Description = "A scripted seat for TradeAgent's proving host. Runs no model.",
            Executable = Environment.ProcessPath!,
            Install = new InstallPlan { Kind = InstallKind.None },
            VersionArgs = [puppetDll, "--version"],
            HealthArgs = [puppetDll, "--version"],
            ExecArgs = turn,
            ResumeArgs = turn,
            JsonFlag = "--json",
            PromptOnStdin = true,
            StdinMarker = "",
            Puppet = true,
            PuppetSeatClass = SeatPlan.PuppetClass
        };
        host.Agent.VersionDeadline = TimeSpan.FromSeconds(30);
        await host.Agent.PrepareAsync(manifest, host.WorkspaceContext());
        await host.Agent.StartAsync();
        if (!host.Agent.Running) throw new InvalidOperationException("the puppet runtime did not start");
        _ = host.Conversation;
        host.Mission.Start();

        // THE BRIEF GOES TO THE TEMPLATE'S FIRST POSITION — one wake; every later turn of every position is woken by the kernel's own
        // records (a verdict wakes its author, a promotion's boundary wakes the chair), never by this host, and each position plays its
        // script's turns in order, the last again once they run out.
        var first = config.Positions[0].Id;
        var brief = MissionEventIds.Self($"proving-{Guid.NewGuid():n}");
        var due = DateTimeOffset.UtcNow.AddSeconds(-1);
        if (!host.Wakes!.RaiseDue(brief, MissionEventKind.Self, due.AddMinutes(-1), due, role: first))
            throw new InvalidOperationException($"the brief's wake {brief} was not raised");
        host.Mission.Wake();
        log.Add($"brief: {brief} to {first}");

        // UNTIL THE ORGANISATION IS QUIET: no launch open and none started for three times the loop's rest between a position's turns.
        var quiet = TimeSpan.FromSeconds(15);
        var quietSince = DateTimeOffset.UtcNow;
        var seen = Attempts(host).Count;
        while (DateTimeOffset.UtcNow - quietSince < quiet)
        {
            if (DateTimeOffset.UtcNow >= deadline) { log.Add("deadline reached while the organisation was still working"); return true; }
            var now = Attempts(host);
            if (now.Count != seen || now.Any(a => a.State == AiAttemptState.LAUNCHED)) { seen = now.Count; quietSince = DateTimeOffset.UtcNow; }
            await Task.Delay(200);
        }
        await host.Mission.PauseAsync();

        // WHAT THE ENVELOPE REFUSED: each seated position the meter admits no further turn for, with wakes due that nothing consumed.
        foreach (var position in config.Positions)
        {
            var spend = host.Meter!.TodayFor(position.Id);
            if (spend.AdmitsAnotherTurn) continue;
            var owed = DueUnconsumed(host, position.Id);
            if (owed.Count == 0) continue;
            var b = spend.Binding;
            refusals.Add(new EnvelopeRefusal(position.Id, owed, b.Spent + b.Reserved, b.Cap));
            log.Add(string.Create(CultureInfo.InvariantCulture,
                $"{position.Id}: refused by the envelope — {owed.Count} wake(s) due and unconsumed ({string.Join(", ", owed)}); spent {b.Spent}, reserved {b.Reserved}, next turn {b.NextTurnReservation}, cap {b.Cap}"));
        }
        foreach (var a in Attempts(host)) log.Add($"attempt {a.Id} {a.Role} {a.State} in={a.InputTokens} out={a.OutputTokens} cost={a.Cost}");
        return false;
    }

    /// <summary>The ids of wakes due now for <paramref name="role"/> that nothing has consumed, as the home's own ledger holds them.</summary>
    static List<string> DueUnconsumed(AppHost host, string role) => host.Db.Read(_ =>
    {
        using var c = host.Db.Cmd("SELECT id FROM mission_event WHERE consumed_by IS NULL AND due_at <= $now ORDER BY due_at, id",
            ("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }).Where(id => CouncilRoles.Or(host.Wakes!.Get(id)?.Role) == role).ToList();

    static List<AiAttempt> Attempts(AppHost host) =>
        [.. new AiAttemptStore(host.Db).Between(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2))];


}
