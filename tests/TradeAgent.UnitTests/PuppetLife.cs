using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUPPET PROGRAM THIS BUILD MADE, and the manifest that starts it (<c>U-puppet-runtime</c>; <c>tests/TradeAgent.Puppet</c>).
/// The manifest is handed to the host in-process: its id is reserved, so no <c>runtimes.json</c> can carry it, and its
/// <see cref="RuntimeManifest.Puppet"/> flag is internal, so only this assembly can set it.
/// </summary>
static class Puppet
{
    static string Built(string name) =>
        typeof(Puppet).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == name).Value
        ?? throw new InvalidOperationException($"this assembly was built without its {name}");

    /// <summary>The puppet's assembly, as this configuration built it.</summary>
    public static string Dll
    {
        get
        {
            var dll = Path.GetFullPath(Built("PuppetDll"));
            Assert.True(File.Exists(dll), $"the puppet seat was not built: no {dll}");
            return dll;
        }
    }

    /// <summary>The folder this configuration built the trade command into, which the app deploys from.</summary>
    public static string TradeCliDir => Path.GetFullPath(Built("TradeCliDir"));

    /// <summary>Where this test host's .NET is: the folder above <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c>.</summary>
    public static string DotnetRoot =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    /// <summary>The <c>dotnet</c> program that runs the puppet's assembly.</summary>
    public static string DotnetHost
    {
        get
        {
            var host = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(host)) return host;
            var fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            Assert.True(fromSdk is { Length: > 0 } && File.Exists(fromSdk), $"no dotnet program beside this test host's runtime ({host})");
            return fromSdk!;
        }
    }

    /// <summary>
    /// A puppet manifest: the real codex event stream (<c>--json</c>), the prompt on stdin, the scripts in
    /// <paramref name="scripts"/>, and the seat class its test declares — <see cref="SeatPlan.PuppetClass"/> unless named.
    /// </summary>
    public static RuntimeManifest Manifest(string scripts, string? seatClass = null)
    {
        string[] turn = [Dll, "--scripts", scripts, "{prompt}"];
        return new RuntimeManifest
        {
            Id = RuntimeManifest.PuppetId,
            DisplayName = "Puppet seat",
            Description = "A scripted seat for TradeAgent's own tests. Runs no model.",
            Executable = DotnetHost,
            Install = new InstallPlan { Kind = InstallKind.None },
            VersionArgs = [Dll, "--version"],
            HealthArgs = [Dll, "--version"],
            ExecArgs = turn,
            ResumeArgs = turn,
            JsonFlag = "--json",
            PromptOnStdin = true,
            StdinMarker = "",
            Puppet = true,
            PuppetSeatClass = seatClass ?? SeatPlan.PuppetClass
        };
    }

    /// <summary>A fresh folder for one test's scripts, under this process's test home.</summary>
    public static string NewScripts(string name)
    {
        var dir = Path.Combine(TestEnv.Home, $"puppet-{name}-{Guid.NewGuid():n}"[..Math.Min(60, name.Length + 40)]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// ONE ORGANISATIONAL LIFE, PLAYED BY PUPPETS THROUGH THE REAL KERNEL (<c>U-puppet-runtime</c>; <c>docs/ORCHESTRATION.md</c> § 7).
///
/// <para><b>What is real.</b> The host's own composition (<c>AppHost.Composed</c>) over a database of its own: the meter, the wake
/// queue, the relay, the loop. TradeAgent's own <c>trade</c> command, deployed by <see cref="ToolDeployer"/> as the app deploys
/// it — which also makes it the launcher that holds every turn on macOS and Linux — and a <see cref="GatewayPipeServer"/> over the
/// host's gateway with the peer rule the app uses. A turn is <see cref="MissionLoop"/> → admission (<see cref="TurnMeter.Begin"/>)
/// → the activation seam → <see cref="AgentSession"/> (the prompt on stdin) → <see cref="ProcessContainment"/> → the puppet
/// process → <c>trade</c> with the launch's own grant → the pipe → the stores. The puppet manifest is handed to the supervisor
/// in-process: its id is reserved and its flag is not data, so no file can carry it.</para>
///
/// <para><b>What is a stand-in.</b> The model (a script per position, <see cref="Script"/>), the broker (<c>FakeConnector</c>),
/// and a crash (<see cref="RestartMidTurn"/>: the database copied whole while a turn runs is what the next start opens).</para>
///
/// <para><b>Adding a life</b> (later units): write each position's turns with <see cref="Script"/>, wake it with
/// <see cref="Wake"/> or the owner's words, <see cref="RunUntil"/> the row or file that ends the act, and read the stores.
/// O3 settles: script the refusal a position meets and read the wake's disposition. O4 envelopes: <c>usage</c> steps until
/// the envelope runs out, then read the refusal. O5 assignments and the verbs' conformance suite: a <c>trade &lt;verb&gt;</c> step
/// per cell of the rights matrix, with <see cref="Begin"/>'s seat class naming the class under test (the class is declared by
/// the manifest, never by a file). Part c's broker class: a puppet manifest declaring it. Seed positions with
/// <see cref="Begin"/>'s <c>seed</c>; positions the activation gate refuses are stepped over, so a life asserts it.</para>
///
/// <para>Every life runs in <see cref="VendorOverrideFiles"/>: the deployed <c>trade</c> is the shared bin folder's, the pipe is
/// the assembly's, and the host reads <c>runtimes.json</c>.</para>
/// </summary>
sealed class PuppetLife : IAsyncDisposable
{
    /// <summary>The longest any act of a life is waited for. Generous: a loaded runner starts two .NET programs per turn.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    public AppHost Host { get; private set; } = null!;
    public AgentPresence Presence { get; private set; } = null!;
    public string Scripts { get; }
    public string DatabasePath { get; private set; } = "";
    public RuntimeManifest Manifest { get; }

    GatewayPipeServer? _server;
    int _wakes;
    ProcessWide _found = null!;

    PuppetLife(string scripts, string? seatClass)
    {
        Scripts = scripts;
        Manifest = Puppet.Manifest(scripts, seatClass);
    }

    /// <summary>
    /// A LIFE'S FIRST START: a fresh database, finished setup, the owner's own rate set (so every reservation is a real sum and
    /// every turn a priced one), <paramref name="seed"/> applied to the database before anything reads it, and the puppet started
    /// through the supervisor's own prepare and start.
    /// </summary>
    public static async Task<PuppetLife> Begin(string name, Action<Database>? seed = null, string? seatClass = null)
    {
        // WHAT THE LIFE IS ABOUT TO TAKE from the assembly's other classes, noted before it takes anything and put back at its end.
        var found = ProcessWide.Note();

        // The deployed trade command is framework-dependent on a developer machine; the app hands its children the .NET it found.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Puppet.DotnetRoot);

        var life = new PuppetLife(Puppet.NewScripts(name), seatClass) { _found = found };
        DeployTrade();

        life.DatabasePath = Path.Combine(TestEnv.Home, $"puppet-life-{Guid.NewGuid():n}.db");
        var db = new Database(life.DatabasePath);
        seed?.Invoke(db);
        var connector = new FakeConnector(new FakeBroker());
        await using (var last = new TradingGateway(db, connector, new HealthRegistry()))
            last.Update(s =>
            {
                s.SelectedRuntimeId = RuntimeManifest.PuppetId;
                s.AiPriceInputPerMillion = 2m;
                s.AiPriceOutputPerMillion = 8m;
                s.AiDailyCostCap = 1000m;
            });
        new OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);
        await life.Compose(db, connector);
        return life;
    }

    async Task Compose(Database db, FakeConnector connector)
    {
        await connector.ConnectAsync();
        Presence = new AgentPresence();
        Host = AppHost.Composed(db, connector, Presence);
        Host.Agent.VersionDeadline = TimeSpan.FromSeconds(30);
        _server = new GatewayPipeServer(Host.Gateway, IpcToken.Ensure()) { Peer = Containment.PeerRuleNow() };
        _server.Start();
        await Host.Agent.PrepareAsync(Manifest, Host.WorkspaceContext());
        await Host.Agent.StartAsync();
        Assert.True(Host.Agent.Running, "the puppet runtime did not start");
        _ = Host.Conversation;
    }

    /// <summary>
    /// TradeAgent's own trade command, deployed the way the app deploys it at start, from this configuration's build. Whatever
    /// another test left at that path goes first: the deployer copies only over an OLDER file.
    /// </summary>
    static void DeployTrade()
    {
        var exe = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
        if (File.Exists(exe)) File.Delete(exe);
        Assert.Equal(exe, ToolDeployer.EnsureTradeCli(Puppet.TradeCliDir));
    }

    // ---- the script ------------------------------------------------------------------------------------------------------

    /// <summary>The key a position's script and records are filed under: its home's own folder name.</summary>
    public string KeyOf(string role) => Path.GetFileName(Host.HomeFor(role).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Writes <paramref name="role"/>'s turns, one array of steps per turn (see <c>tests/TradeAgent.Puppet/Program.cs</c>).</summary>
    public void Script(string role, params string[][] turns)
    {
        var json = new JsonObject { ["turns"] = new JsonArray([.. turns.Select(t => (JsonNode?)new JsonArray([.. t.Select(s => (JsonNode?)s)]))]) };
        File.WriteAllText(Path.Combine(Scripts, KeyOf(role) + ".json"), json.ToJsonString());
    }

    /// <summary>Lets a puppet waiting on <c>await <paramref name="name"/></c> go on.</summary>
    public void Release(string name) => File.WriteAllText(Path.Combine(Scripts, name), "");

    /// <summary>Whether a puppet has reached <c>signal <paramref name="name"/></c>.</summary>
    public bool Reached(string name) => File.Exists(Path.Combine(Scripts, name));

    /// <summary>The prompt <paramref name="role"/>'s <paramref name="turn"/>-th turn received, or null before it started.</summary>
    public string? Prompt(string role, int turn)
    {
        var file = Path.Combine(Scripts, $"{KeyOf(role)}.{turn.ToString(CultureInfo.InvariantCulture)}.prompt");
        return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
    }

    /// <summary>How many turns <paramref name="role"/>'s puppet has started.</summary>
    public int Turns(string role)
    {
        var counter = Path.Combine(Scripts, KeyOf(role) + ".turns");
        return File.Exists(counter) ? int.Parse(File.ReadAllText(counter).Trim(), CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>What <paramref name="role"/>'s <paramref name="turn"/>-th turn's steps reported, one JSON object per step.</summary>
    public List<JsonObject> Log(string role, int turn)
    {
        var file = Path.Combine(Scripts, $"{KeyOf(role)}.{turn.ToString(CultureInfo.InvariantCulture)}.log");
        return File.Exists(file)
            ? [.. File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())]
            : [];
    }

    // ---- driving it ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A WAKE DUE NOW for <paramref name="role"/> — the kind a seat's own request raises (<c>self</c>), so the loop's clock never
    /// withdraws it as a look nobody needs (a role that owns nothing open has its scheduled looks closed) — and the loop nudged.
    /// </summary>
    public string Wake(string role)
    {
        var due = DateTimeOffset.UtcNow.AddSeconds(-++_wakes);
        var id = MissionEventIds.Self($"puppet-life-{Guid.NewGuid():n}");
        Assert.True(Host.Wakes!.RaiseDue(id, MissionEventKind.Self, due.AddMinutes(-1), due, role: role), $"the wake {id} was not raised");
        Host.Mission.Wake();
        return id;
    }

    /// <summary>
    /// THE HOST'S OWN LOOP RUNS until <paramref name="condition"/> holds, for <see cref="Patience"/> at most; a wait that runs out
    /// says what it waited for and what the ledgers read at that moment.
    /// </summary>
    public async Task RunUntil(Func<bool> condition, string waitingFor)
    {
        Host.Mission.Start();
        Host.Mission.Wake();
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"{waitingFor}: not within {Patience.TotalMinutes} minutes. {State()}");
    }

    /// <summary>What the ledgers read now, for a failure's message.</summary>
    public string State()
    {
        try
        {
            var attempts = string.Join("; ", Attempts().Select(a =>
                $"{a.Id} {a.Role} {a.State} exit={a.ExitCode} in={a.InputTokens} out={a.OutputTokens}"));
            var wakes = string.Join("; ", Events().Select(e => $"{e.Id} {e.Role} consumed_by={e.ConsumedBy} {e.Disposition}"));
            var launches = Path.Combine(Scripts, "launches.log");
            var history = string.Join(" | ", (Host.Conversation?.History ?? []).TakeLast(6).Select(t => $"{t.Role}: {t.Text}"));
            return $"Attempts: [{attempts}]. Wakes: [{wakes}]. Launches: [{(File.Exists(launches) ? File.ReadAllText(launches).Trim() : "")}]. " +
                   $"Mission: {Host.Mission.Status.State}. Chat: {history}";
        }
        catch (Exception ex) { return $"(the ledgers could not be read: {ex.Message})"; }
    }

    /// <summary>
    /// A CRASH AND A RESTART, MID-TURN. The database is copied whole as it stands — <c>VACUUM INTO</c>, one consistent image, with
    /// the turn's row LAUNCHED and its wakes consumed — and that image is what the next start opens, exactly what a process that
    /// died at this instant would leave. The old host is then closed, its puppet with it; nothing it does afterwards reaches the
    /// image. The new host is composed over the image and starts the puppet again, as the app's start does.
    /// </summary>
    public async Task RestartMidTurn()
    {
        var image = Path.Combine(TestEnv.Home, $"puppet-life-{Guid.NewGuid():n}.db");
        Host.Db.Read(_ => { Host.Db.Exec($"VACUUM INTO '{image.Replace("'", "''", StringComparison.Ordinal)}'"); return 0; });
        await Close();
        DatabasePath = image;
        await Compose(new Database(image), new FakeConnector(new FakeBroker()));
    }

    // ---- the stores ------------------------------------------------------------------------------------------------------

    public List<AiAttempt> Attempts() =>
        new AiAttemptStore(Host.Db).Between(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2))
            .OrderBy(a => a.StartedAt).ToList();

    public List<AiAttempt> AttemptsOf(string role) => [.. Attempts().Where(a => a.Role == role)];

    public List<MissionEvent> Events() =>
        Host.Db.Read(_ =>
        {
            using var c = Host.Db.Cmd("SELECT id FROM mission_event ORDER BY created_at, rowid");
            using var r = c.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }).Select(id => Host.Wakes!.Get(id)!).ToList();

    /// <summary>The row counts a life leaves, for its evidence line.</summary>
    public string Counts()
    {
        long Count(string table) => Host.Db.Read(_ =>
        {
            using var c = Host.Db.Cmd($"SELECT COUNT(*) FROM {table}");
            return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
        return $"ai_attempt={Count("ai_attempt")} mission_event={Count("mission_event")} " +
               $"ledger_entry={Count("ledger_entry")} on {RuntimeInformation.OSDescription}";
    }

    // ---- the end ---------------------------------------------------------------------------------------------------------

    async Task Close()
    {
        try { await Host.Mission.PauseAsync(); } catch (Exception) { /* closing regardless */ }
        try { await Host.Agent.StopAsync(); } catch (Exception) { }
        try { if (_server is not null) await _server.DisposeAsync(); } catch (Exception) { }
        try { await Host.Gateway.DisposeAsync(); } catch (Exception) { }
        try { Host.Db.Dispose(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await Close();
        _found.PutBack();
    }
}

/// <summary>
/// WHAT A PUPPET LIFE TAKES FROM THE WHOLE TEST PROCESS AND MUST GIVE BACK (U-puppet-isolation): the launcher at
/// <c>Paths.Bin</c> every held launch in this assembly is started by (the life deploys the real <c>trade</c> over it and used
/// to delete it at its end, so the next class to launch a held process was refused before it started), the deployer's hash
/// record, the side-car files the deployment copies beside it, and <c>DOTNET_ROOT</c>. Noted by <see cref="Note"/> before
/// the life touches any of them; <see cref="PutBack"/> restores the launcher byte for byte and mode for mode, the record,
/// the variable, and removes the files the life added.
/// </summary>
sealed class ProcessWide
{
    readonly string _launcher = Path.Combine(Paths.Bin, ToolDeployer.TradeCliName);
    byte[]? _launcherBytes;
    UnixFileMode _mode;
    string? _hash;
    string? _dotnetRoot;
    HashSet<string> _files = [];

    public static ProcessWide Note()
    {
        var w = new ProcessWide { _dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") };
        if (File.Exists(w._launcher))
        {
            w._launcherBytes = File.ReadAllBytes(w._launcher);
            if (!OperatingSystem.IsWindows()) w._mode = File.GetUnixFileMode(w._launcher);
        }
        w._hash = File.Exists(ToolDeployer.DeployedHashFile) ? File.ReadAllText(ToolDeployer.DeployedHashFile) : null;
        if (Directory.Exists(Paths.Bin)) w._files = [.. Directory.GetFiles(Paths.Bin)];
        return w;
    }

    public void PutBack()
    {
        Environment.SetEnvironmentVariable("DOTNET_ROOT", _dotnetRoot);
        try
        {
            if (Directory.Exists(Paths.Bin))
                foreach (var f in Directory.GetFiles(Paths.Bin).Where(f => !_files.Contains(f)))
                    File.Delete(f);
            if (_launcherBytes is null) File.Delete(_launcher);
            else
            {
                File.WriteAllBytes(_launcher, _launcherBytes);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_launcher, _mode);
            }
            if (_hash is null) File.Delete(ToolDeployer.DeployedHashFile);
            else File.WriteAllText(ToolDeployer.DeployedHashFile, _hash);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
