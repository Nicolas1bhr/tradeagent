using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BROKER-ONLY CODEX SEAT (<c>U-thinking-seats-c</c>; <c>docs/ORCHESTRATION.md</c> § 2.7, § 5; <c>R-exec-server</c> DECISION 2): a
/// position beyond the legacy pair on codex starts with no environment, the app's door as its only tool server and the launch's grant as
/// bearer, on its pinned model, and only after the tools that binary, template and model would be offered are verified as exactly the
/// class's expected set — refused before its admission writes anything otherwise. CRED-001 (the seat home's login offered to no model
/// tool), Law 2 (no model-directed code in the process that holds the login), ORG-001.
///
/// <para>CI has no codex: the CLI is <see cref="SeatHomeTests.EnvDumper"/>'s script, its reports <see cref="SeatHomeTests.FakeProbe"/>,
/// and its offered tools <see cref="FakeSurface"/> — the shapes the real dry run captured on the dev Mac (codex-cli 0.160.1).</para>
///
/// <para>Shares the vendor-override collection because (d) writes <c>runtimes.json</c> under the shared test home.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BrokerSeatTests : IDisposable
{
    const string Pos = "pos-5c1e2d9a";

    static readonly Uri DoorAt = new("http://127.0.0.1:1/mcp");

    /// <summary>What the real dry run captured for gpt-5.5 under the broker class (RUN 2026-10-17, broker.sh x-gpt-5.5).</summary>
    internal static readonly string[] Classic =
        ["list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource", "request_user_input", "tool_search[tool_search]"];

    /// <summary>What it captured for gpt-6-astra under the same argv (x-gpt-6-astra): code mode's exec, inside the codex process.</summary>
    internal static readonly string[] CodeMode =
    [
        "(additional_tools) functions.exec[custom]", "(additional_tools) functions.wait",
        "(additional_tools) functions.request_user_input", "(additional_tools) functions.request_user_input_async"
    ];

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    // ---- (a) no environment, the door as the only tool server ----------------------------------------------------------

    /// <summary>
    /// (a) A BROKER SEAT STARTS WITH NO ENVIRONMENT AND THE DOOR AS ITS ONLY TOOL SERVER. A position beyond the legacy pair on codex, on a
    /// host with a proven cell and the register declaring it broker: its verdict is class <c>broker</c>, MEDIATED; the attempt row records
    /// both; and the process that starts carries <c>CODEX_EXEC_SERVER_URL=none</c> and the grant in its environment, the door as
    /// <c>mcp_servers.tradeagent</c> with <c>required=true</c> and the grant's variable as bearer, the shell, exec, image and sleep tools
    /// and sub-agents off, no empty server table, and <c>-m gpt-5.5</c>.
    ///
    /// <para>RED at base: it did not compile — no <c>door</c> or <c>surfaceProbe</c> on <see cref="AgentSession"/>, no
    /// <c>SeatPlan.BrokerClass</c>; every position ran as class <c>cli</c> with <c>-c mcp_servers={}</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_broker_seat_starts_with_no_environment_and_the_door_as_its_only_tool_server()
    {
        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(A_broker_seat_starts_with_no_environment_and_the_door_as_its_only_tool_server));
        var surface = new FakeSurface(Classic);
        var session = Session(codex, exe, register, surface);

        var verdict = session.CheckSeat();
        Assert.False(verdict.Refused, verdict.Refusal);
        Assert.Equal((SeatClasses.Broker, SeatAssurance.Mediated), (verdict.SeatClass, verdict.Assurance));
        Assert.Equal(Classic, verdict.Offered);

        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
        using var metering = meter.Attach(session, Pos);
        await session.SendAsync("what are you doing?");
        Assert.DoesNotContain(session.History, t => t.Role == ChatRole.System);

        var row = Assert.Single(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), a => a.Role == Pos);
        Assert.Equal((SeatClasses.Broker, "mediated"), (row.SeatClass, row.Assurance));

        var exec = Unquoted(Assert.Single(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal)));
        foreach (var carried in new[]
                 {
                     $"mcp_servers.tradeagent.url=\"{DoorAt.AbsoluteUri}\"", "mcp_servers.tradeagent.required=true",
                     "mcp_servers.tradeagent.bearer_token_env_var=\"TRADEAGENT_GRANT\"",
                     "mcp_servers.tradeagent.default_tools_approval_mode=\"approve\"", "agents.enabled=false",
                     "--disable shell_tool", "--disable unified_exec", "--disable view_image", "--disable sleep_tool",
                     "--disable code_mode_host", "--ignore-user-config", "-m gpt-5.5"
                 })
            Assert.True(exec.Contains(carried, StringComparison.Ordinal), $"the broker turn's argv lacks '{carried}': {exec}");
        Assert.DoesNotContain("mcp_servers={}", exec, StringComparison.Ordinal);
        Assert.DoesNotContain(DoorSlotWord, exec, StringComparison.Ordinal);

        var env = SeatHomeTests.ReadEnv(dump);
        Assert.Equal("none", env.GetValueOrDefault("CODEX_EXEC_SERVER_URL"));
        Assert.True(env.GetValueOrDefault("TRADEAGENT_GRANT") is { Length: > 0 }, "the broker turn started without its grant");
        Assert.Equal(1, surface.Asks);
    }

    const string DoorSlotWord = SeatPlan.DoorSlot;

    /// <summary>
    /// The script's record of its command line as the arguments arrived. On Windows the <c>.cmd</c> echoes the raw line, each argument
    /// with a quote in it wrapped and its quotes escaped (<c>-c "web_search=\"disabled\""</c>, as the CLI class's always was); elsewhere
    /// <c>$*</c> is already the arguments.
    /// </summary>
    static string Unquoted(string line) =>
        OperatingSystem.IsWindows() ? line.Replace("\\\"", "\u0001").Replace("\"", "").Replace("\u0001", "\"") : line;

    // ---- (b) the offered surface ---------------------------------------------------------------------------------------

    /// <summary>
    /// (b) A BROKER SEAT WHOSE OFFERED TOOLS INCLUDE A HOST TOOL OR <c>exec</c> IS REFUSED BEFORE ADMISSION — and so is one offered any set
    /// but the expected one: the verdict refuses naming the tool, the meter writes no attempt row and reserves nothing, and no process
    /// starts. The captured surface is cached per (binary, template, model): the probe is asked once and the comparison runs every time.
    ///
    /// <para>RED at base: it did not compile (no <see cref="ISurfaceProbe"/>); the watched mutant — the comparison skipped, the probe
    /// still asked — is red here, quoted in the unit's report.</para>
    /// </summary>
    [Fact]
    public async Task A_broker_seat_whose_offered_tools_include_a_host_tool_or_exec_is_refused_before_admission()
    {
        foreach (var (what, offered, named) in new (string, string[], string)[]
                 {
                     ("code mode's exec (gpt-6-astra)", CodeMode, "'exec'"),
                     ("a shell", [.. Classic, "exec_command", "write_stdin"], "'exec_command'"),
                     ("a patch tool", [.. Classic, "apply_patch[custom]"], "'apply_patch'"),
                     ("an image read off the disk", [.. Classic, "view_image"], "'view_image'"),
                     ("one tool more than expected", [.. Classic, "update_plan"], "'update_plan'"),
                     ("one tool fewer", Classic[..^1], "'tool_search'")
                 })
        {
            using var db = TestEnv.NewDb();
            var register = Position(db);
            var (codex, exe, dump) = SeatHomeTests.Seat($"surface-{what.Length}-{named.Length}");
            var surface = new FakeSurface(offered);
            var session = Session(codex, exe, register, surface);

            var verdict = session.CheckSeat();
            Assert.True(verdict.Refused, $"a broker seat offered {what} was not refused");
            Assert.Contains(named, verdict.Refusal);

            var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
            using var metering = meter.Attach(session, Pos);
            Assert.False(meter.Begin("## Situation", role: Pos).Admitted, $"a broker seat offered {what} was admitted");
            Assert.Empty(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));

            await session.SendAsync("go");
            Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
            Assert.Equal(1, surface.Asks);
        }
    }

    // ---- (c) the pinned model ------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) A BROKER SEAT WITHOUT ITS PINNED MODEL IS REFUSED: an owner's choice of another model for the position, a command line that
    /// does not name the pin, and a runtime whose command line cannot name a model at all. The pin itself, chosen or not chosen, runs.
    ///
    /// <para>RED at base: it did not compile — no class carried a model, and a position ran on whatever was chosen or the runtime's
    /// default (gpt-5.6-sol), code mode included.</para>
    /// </summary>
    [Fact]
    public async Task A_broker_seat_without_its_pinned_model_is_refused()
    {
        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_broker_seat_without_its_pinned_model_is_refused));

        foreach (var chosen in new string?[] { null, "", BrokerSeat.Model })
            Assert.False(Session(codex, exe, register, new FakeSurface(Classic), model: chosen).CheckSeat().Refused, $"the pin refused, chosen '{chosen}'");

        var other = Session(codex, exe, register, new FakeSurface(Classic), model: "gpt-6-astra").CheckSeat();
        Assert.True(other.Refused, "a broker seat chosen onto another model was not refused");
        Assert.Contains("gpt-6-astra", other.Refusal);
        Assert.Contains(BrokerSeat.Model, other.Refusal);

        // THE COMMAND LINE THAT STARTS MUST NAME THE PIN: one built without a model, or for another, is refused as built.
        var launch = new SeatLaunch(SeatPlan.BrokerClass, DoorAt, null, new FakeSurface(Classic));
        var home = Path.GetDirectoryName(exe)!;
        foreach (var model in new string?[] { null, "gpt-6-astra" })
        {
            var argv = AgentArgs.For(codex, "", resuming: false, model, SeatPlan.BrokerClass, DoorAt, out _);
            var verdict = await SeatProfile.VerifyAsync(codex, exe, argv, home, BrokerProbe(), launch, default);
            Assert.True(verdict.Refused, $"a broker argv naming '{model}' was verified");
            Assert.Contains($"does not name {BrokerSeat.Model}", verdict.Refusal);
        }

        // NO CLASS PINS A MODEL BUT THE VERIFIED ONES (BROKER, CELL), AND A RUNTIME THAT CANNOT NAME ONE CANNOT RUN AS ONE.
        // The cell class pins gpt-5.5 since U-cell-linux-c (L2); the harness class still pins none.
        Assert.Contains("pins no model", BrokerSeat.ModelRefusal(SeatClasses.Harness, null, codex));
        var mute = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        mute.ModelArgs = [];
        Assert.Contains("cannot name the model", BrokerSeat.ModelRefusal(SeatPlan.BrokerClass, null, mute));
    }

    // ---- (d) runtimes.json cannot change the class ---------------------------------------------------------------------

    /// <summary>
    /// (d) A <c>runtimes.json</c> OVERRIDE OF THE BROKER CLASS IS REFUSED, NAMING BOTH: an entry whose broker deny set, class environment,
    /// feature or tool-server allowlist, model flag, workspace's forbidden names or credential files differs from this build's refuses the
    /// class with the field, the file's version and this build's; nothing is admitted (the pin and the home's forbidden paths: below). The file's other runtimes and the legacy pair's CLI class are untouched.
    ///
    /// <para>RED at base: it did not compile — no class had fields of its own and nothing compared them.</para>
    /// </summary>
    [Fact]
    public void A_runtimes_json_override_of_the_broker_class_is_refused_naming_both()
    {
        foreach (var (field, change) in new (string, Action<RuntimeManifest>)[]
                 {
                     ("deny set", m => m.Seat!.Args[SeatPlan.BrokerClass] = m.Seat.Args[SeatPlan.BrokerClass][..^2]),
                     ("class environment", m => m.Seat!.ClassEnvironment.Remove(SeatPlan.BrokerClass)),
                     ("feature allowlist", m => m.Seat!.FeatureAllowlist[SeatPlan.BrokerClass] = [.. m.Seat.FeaturesFor(SeatPlan.BrokerClass), "shell_tool"]),
                     ("tool-server allowlist", m => m.Seat!.McpAllowlist[SeatPlan.BrokerClass] = [BrokerSeat.DoorServer, "elsewhere"]),
                     ("model flag", m => m.ModelArgs = ["--model", "{model}"]),
                     ("workspace's forbidden names", m => m.Seat!.WorkspaceForbidden = [".agents"]),
                     ("credential files", m => m.Seat!.PrivateFiles = ["codex/auth.json"])
                 })
        {
            using var db = TestEnv.NewDb();
            var register = Position(db);
            var (codex, exe, _) = SeatHomeTests.Seat($"override-{field.Replace(' ', '-')}");
            change(codex);
            RuntimeCatalog.SaveOverrides([codex]);
            var loaded = RuntimeCatalog.Find("codex")!;

            var verdict = Session(loaded, exe, register, new FakeSurface(Classic)).CheckSeat();
            Assert.True(verdict.Refused, $"a runtimes.json that changed the broker class's {field} was not refused");
            Assert.Contains($"runtimes.json gives its {field} as SHA-256 ", verdict.Refusal);
            Assert.Contains($"this build ({Versions.App}) compiles it as SHA-256 ", verdict.Refusal);
            Assert.Equal(2, verdict.Refusal!.Split("SHA-256 ").Length - 1);

            var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
            using var metering = meter.Attach(Session(loaded, exe, register, new FakeSurface(Classic)), Pos);
            Assert.False(meter.Begin("## Situation", role: Pos).Admitted);

            // THE LEGACY PAIR'S CLI CLASS READS THE SAME FILE AS BEFORE.
            var chair = new AgentSession(loaded, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
                presence: new AgentPresence(), seatProbe: new SeatHomeTests.FakeProbe());
            Assert.False(chair.CheckSeat().Refused);
            File.Delete(RuntimeCatalog.OverridePath);
        }
    }

    /// <summary>
    /// (d) THE PROGRAM'S PIN IS THIS BUILD'S (fix pass 1, R1): a <c>runtimes.json</c> that swaps the program AND its digest — a script
    /// of the test's own, its SHA-256 written as the pin — is refused by <see cref="AgentSession.CheckSeat"/> naming the pin, before
    /// the CLI's own reports are read, so the swapped program never runs: not for its reports, not for the turn. The probe here is the
    /// real <see cref="CliSeatProbe"/>, the one that starts the program.
    ///
    /// <para>RED at the pre-fix tip: the pin was not among the compared fields, the manifest's own pin passed the pin check, and the
    /// real probe started the swapped program for its feature report.</para>
    /// </summary>
    [Fact]
    public async Task A_runtimes_json_that_swaps_the_program_and_its_pin_is_refused_and_the_program_never_runs()
    {
        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, _, _) = SeatHomeTests.Seat(nameof(A_runtimes_json_that_swaps_the_program_and_its_pin_is_refused_and_the_program_never_runs));
        var (swapped, ran) = SeatHomeTests.EnvDumper("swapped-program");
        codex.Executable = swapped;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(swapped);
        RuntimeCatalog.SaveOverrides([codex]);
        var loaded = RuntimeCatalog.Find("codex")!;

        var session = new AgentSession(loaded, () => swapped, () => Path.GetDirectoryName(swapped)!, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), role: Pos, seatProbe: CliSeatProbe.Instance, seats: register, door: () => DoorAt,
            surfaceProbe: new FakeSurface(Classic));
        var verdict = session.CheckSeat();
        Assert.False(File.Exists(ran), $"the swapped program ran during the seat check: {string.Join(" | ", SeatHomeTests.Argv(ran))}");
        Assert.True(verdict.Refused, "a runtimes.json that swapped the program and its pin was not refused");
        Assert.Contains("runtimes.json gives its program pin as SHA-256 ", verdict.Refusal);

        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
        using var metering = meter.Attach(session, Pos);
        Assert.False(meter.Begin("## Situation", role: Pos).Admitted, "a broker seat on a swapped program was admitted");
        await session.SendAsync("go");
        Assert.False(File.Exists(ran), "the swapped program ran for the turn");
        Assert.Empty(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    /// <summary>
    /// (d) THE HOME'S FORBIDDEN PATHS ARE THIS BUILD'S (fix pass 1, R2): a <c>runtimes.json</c> that drops codex's
    /// <c>environments.toml</c> from the seat home's forbidden list — the file that, present, replaces <c>CODEX_EXEC_SERVER_URL=none</c>
    /// — is refused at admission with one seeded in the seat home, although the CLI's reports and the offered surface would both pass:
    /// the surface probe's throwaway home has no such file, so its answer says nothing about the launched seat's (CRED-001, Law 2).
    ///
    /// <para>RED at the pre-fix tip: the forbidden list was not among the compared fields, the dropped entry was not looked for, and
    /// the seat was admitted.</para>
    /// </summary>
    [Fact]
    public async Task A_runtimes_json_that_drops_a_forbidden_home_path_is_refused_although_the_surface_passes()
    {
        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(A_runtimes_json_that_drops_a_forbidden_home_path_is_refused_although_the_surface_passes));
        var environments = "codex/" + CellPolicy.CodexEnvironments;
        Assert.Contains(environments, codex.Seat!.Forbidden);
        codex.Seat.Forbidden = [.. codex.Seat.Forbidden.Where(f => f != environments)];
        RuntimeCatalog.SaveOverrides([codex]);
        var loaded = RuntimeCatalog.Find("codex")!;
        var seeded = Path.Combine(loaded.Seat!.Root, environments);
        Directory.CreateDirectory(Path.GetDirectoryName(seeded)!);
        File.WriteAllText(seeded, "# seeded by the test: codex would read this in place of CODEX_EXEC_SERVER_URL\n");

        var surface = new FakeSurface(Classic);
        var session = Session(loaded, exe, register, surface);
        var verdict = session.CheckSeat();
        Assert.True(verdict.Refused, "a runtimes.json that dropped the seat home's environments.toml from its forbidden paths was admitted");
        Assert.Contains("runtimes.json gives its home's forbidden paths as SHA-256 ", verdict.Refusal);
        Assert.Equal(0, surface.Asks);

        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
        using var metering = meter.Attach(session, Pos);
        Assert.False(meter.Begin("## Situation", role: Pos).Admitted, "a broker seat with a dropped forbidden path was admitted");
        await session.SendAsync("go");
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
        Assert.Empty(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    /// <summary>
    /// (d) A LIST FIELD IS COMPARED ELEMENT BY ELEMENT (fix pass 2, the re-review's P1): a <c>runtimes.json</c> that replaces two adjacent
    /// entries of a compared list by ONE entry holding both, joined by the unit separator U+001F, is refused at admission by the
    /// compare itself, although the CLI's reports and the offered surface would pass — for the home's forbidden paths, with an
    /// environments.toml seeded in the seat home (the merged entry names no file, so the environments-file check is gone), and for the
    /// deny set (the merged argument disables nothing).
    ///
    /// <para>RED at the pre-fix tip: <c>Fields</c> joined each list with an unescaped U+001F, the merged list compared equal to this
    /// build's, and the seat was admitted.</para>
    /// </summary>
    [Theory]
    [InlineData("home's forbidden paths")]
    [InlineData("deny set")]
    public async Task A_runtimes_json_that_merges_two_entries_into_one_is_refused_although_the_surface_passes(string field)
    {
        static string[] Merge(string[] list, int at) =>
            [.. list[..at], list[at] + "\u001f" + list[at + 1], .. list[(at + 2)..]];

        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, exe, dump) = SeatHomeTests.Seat($"merge-{field.Replace(' ', '-').Replace("'", "")}");
        var environments = "codex/" + CellPolicy.CodexEnvironments;
        if (field == "home's forbidden paths")
        {
            var at = Array.IndexOf(codex.Seat!.Forbidden, environments);
            Assert.True(at > 0, "this build's forbidden list has no entry before environments.toml to merge it with");
            codex.Seat.Forbidden = Merge(codex.Seat.Forbidden, at - 1);
            Assert.DoesNotContain(environments, codex.Seat.Forbidden);
        }
        else
        {
            var deny = codex.Seat!.Args[SeatPlan.BrokerClass];
            var at = Enumerable.Range(0, deny.Length - 1).First(i => deny[i] == "--disable" && deny[i + 1] == "shell_tool");
            codex.Seat.Args[SeatPlan.BrokerClass] = Merge(deny, at);
            Assert.DoesNotContain("shell_tool", codex.Seat.Args[SeatPlan.BrokerClass]);
        }
        RuntimeCatalog.SaveOverrides([codex]);
        var loaded = RuntimeCatalog.Find("codex")!;
        if (field == "home's forbidden paths")
        {
            // Seeded only here: with the entry intact (the deny-set row) the home check would refuse the seat for the file itself.
            var seeded = Path.Combine(loaded.Seat!.Root, environments);
            Directory.CreateDirectory(Path.GetDirectoryName(seeded)!);
            File.WriteAllText(seeded, "# seeded by the test: codex would read this in place of CODEX_EXEC_SERVER_URL\n");
        }

        var surface = new FakeSurface(Classic);
        var session = Session(loaded, exe, register, surface);
        var verdict = session.CheckSeat();
        Assert.True(verdict.Refused, $"a runtimes.json that merged two entries of the broker class's {field} into one was admitted");
        Assert.Contains($"runtimes.json gives its {field} as SHA-256 ", verdict.Refusal);
        Assert.Equal(0, surface.Asks);

        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Broker);
        using var metering = meter.Attach(session, Pos);
        Assert.False(meter.Begin("## Situation", role: Pos).Admitted, $"a broker seat with a merged {field} was admitted");
        await session.SendAsync("go");
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
        Assert.Empty(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        File.Delete(RuntimeCatalog.OverridePath);
    }

    // ---- (e) armed live ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (e) A BROKER SEAT IS REFUSED WHILE LIVE IS ARMED, like every seat without a cell (ORCHESTRATION § 8): the turn says the armed
    /// configuration's sentence in the conversation and no process starts.
    ///
    /// <para>RED at base: it did not compile — no broker seat existed to refuse.</para>
    /// </summary>
    [Fact]
    public async Task A_broker_seat_is_refused_while_live_is_armed()
    {
        const string armed = "Real money is armed, and no sandbox confines the AI here.";
        using var db = TestEnv.NewDb();
        var register = Position(db);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(A_broker_seat_is_refused_while_live_is_armed));
        var session = Session(codex, exe, register, new FakeSurface(Classic), launchRefusal: () => armed);
        Assert.Equal(SeatClasses.Broker, session.SeatClass);

        await SeatHomeTests.Turn(session);
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text == armed);
        Assert.DoesNotContain(SeatHomeTests.Argv(dump), l => l.StartsWith("exec", StringComparison.Ordinal));
    }

    // ---- (g) GUARD -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) GUARD: THE CLI CLASS RUNS AS BEFORE. The legacy pair are class <c>cli</c>, unconfined; their argv carries exactly the deny set
    /// this build had before classes — <c>-c mcp_servers={}</c>, no door, no broker flag — their environment no
    /// <c>CODEX_EXEC_SERVER_URL</c>, their model the one chosen; their allowlists the CLI's; their profile hash the four-part one; and
    /// no surface is probed for them. Green at base (the deny set's text is base's, copied).
    /// </summary>
    [Fact]
    public async Task The_cli_class_runs_as_before()
    {
        using var db = TestEnv.NewDb();
        var register = ThinkingSeatsTests.Register(db, SeatClasses.Cli);
        var meter = ThinkingSeatsTests.Meter(db, _ => SeatClasses.Cli);
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(The_cli_class_runs_as_before));
        Assert.Equal(BaseCliDeny, codex.Seat!.ArgsFor(SeatPlan.CliClass));
        Assert.Equal(BaseCliFeatures, codex.Seat.FeaturesFor(SeatPlan.CliClass));
        Assert.Empty(codex.Seat.ServersFor(SeatPlan.CliClass));
        Assert.Equal(BaseCliDeny, AgentArgs.SeatArgs(codex));

        foreach (var role in CouncilRoles.All)
        {
            var surface = new FakeSurface(CodeMode);
            var probe = new SeatHomeTests.FakeProbe();
            var session = new AgentSession(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(),
                presence: new AgentPresence(), model: () => "gpt-5.6-sol", role: role, seatProbe: probe, seats: register,
                door: () => DoorAt, surfaceProbe: surface);
            Assert.Equal(SeatClasses.Cli, session.SeatClass);
            var verdict = session.CheckSeat();
            Assert.False(verdict.Refused, verdict.Refusal);
            Assert.Equal((SeatClasses.Cli, SeatAssurance.Unconfined), (verdict.SeatClass, verdict.Assurance));
            Assert.Equal(SeatProfile.ProfileHash(codex, exe, SeatProfile.ConfigBytes(codex.Seat), probe.Reading), verdict.Profile);
            Assert.Null(verdict.Offered);

            using var metering = meter.Attach(session, role);
            await session.SendAsync("what are you doing?");
            Assert.DoesNotContain(session.History, t => t.Role == ChatRole.System);
            Assert.Equal(0, surface.Asks);
        }

        var execs = SeatHomeTests.Argv(dump).Where(l => l.StartsWith("exec", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, execs.Count);
        Assert.All(new AiAttemptStore(db).Between(DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            a => Assert.Equal((SeatClasses.Cli, "unconfined"), (a.SeatClass, a.Assurance)));
        foreach (var exec in execs)
        {
            Assert.Contains("-c mcp_servers={}", exec, StringComparison.Ordinal);
            Assert.Contains("-m gpt-5.6-sol", exec, StringComparison.Ordinal);
            Assert.DoesNotContain("tradeagent", exec, StringComparison.Ordinal);
            Assert.DoesNotContain("agents.enabled", exec, StringComparison.Ordinal);
            Assert.DoesNotContain("--disable shell_tool", exec, StringComparison.Ordinal);
        }
        Assert.False(SeatHomeTests.ReadEnv(dump).ContainsKey("CODEX_EXEC_SERVER_URL"));
    }

    /// <summary>The CLI class's deny set as main <c>3e78e74d</c> wrote it, before seat classes had fields of their own.</summary>
    static readonly string[] BaseCliDeny =
    [
        "--ignore-user-config", "--ignore-rules", "--strict-config",
        "--disable", "apps", "--disable", "plugins", "--disable", "remote_plugin", "--disable", "plugin_sharing",
        "--disable", "browser_use", "--disable", "browser_use_external", "--disable", "browser_use_full_cdp_access",
        "--disable", "computer_use", "--disable", "in_app_browser", "--disable", "in_app_chat",
        "--disable", "in_app_local_automation", "--disable", "multi_agent", "--disable", "multi_agent_v2",
        "--disable", "memories", "--disable", "hooks", "--disable", "image_generation", "--disable", "goals",
        "--disable", "tool_suggest", "--disable", "skill_mcp_dependency_install", "--disable", "skill_search",
        "--disable", "workspace_dependencies", "--disable", "realtime_conversation", "--disable", "daemon_auto_start",
        "--disable", "auth_elicitation", "--disable", "tool_call_mcp_elicitation", "--disable", "worktrees",
        "--disable", "code_mode_host", "--disable", "in_app_updates",
        "-c", "mcp_servers={}", "-c", "marketplaces={}", "-c", "notify=[]", "-c", "web_search=\"disabled\"",
        "-c", "skills.bundled.enabled=false", "-c", "skills.include_instructions=false",
        "-c", "include_apps_instructions=false", "-c", "check_for_update_on_startup=false",
        "-c", "analytics.enabled=false", "-c", "feedback.enabled=false", "-c", "history.persistence=\"none\"",
        "-c", "cli_auth_credentials_store=\"file\"", "-c", "mcp_oauth_credentials_store=\"file\"",
        "-c", "shell_environment_policy.inherit=\"all\"", "-c", "shell_environment_policy.set={}"
    ];

    /// <summary>The CLI class's feature allowlist as main <c>3e78e74d</c> wrote it.</summary>
    static readonly string[] BaseCliFeatures =
    [
        "collaboration_modes", "compaction_image_budget", "content_item_kinds", "enable_request_compression", "fast_mode",
        "guardian_approval", "guardian_reuse_parent_compaction", "in_app_dictation", "item_ids", "mentions_v2",
        "resize_all_images", "shell_snapshot", "shell_tool", "sleep_tool", "sqlite", "steer", "system_proxy_fallback",
        "terminal_resize_reflow", "tool_search_always_defer_mcp_tools", "tui_app_server", "unbounded_connection_retries",
        "unified_exec", "unified_exec_tty", "unified_exec_zsh_fork", "view_image", "write_stdin_approval"
    ];

    // ---- the rig -------------------------------------------------------------------------------------------------------

    /// <summary>An active position beyond the legacy pair, a proven cell on this host, and a register declaring it broker.</summary>
    static SeatRegister Position(Database db)
    {
        ThinkingSeatsTests.Seat(db, Pos);
        ThinkingSeatsTests.ProveCell(db);
        return ThinkingSeatsTests.Register(db, SeatClasses.Broker);
    }

    /// <summary>A session for <see cref="Pos"/> over <paramref name="codex"/>: the door at <see cref="DoorAt"/>, a broker's reports.</summary>
    static AgentSession Session(RuntimeManifest codex, string exe, SeatRegister register, ISurfaceProbe surface, string? model = null,
        Func<string?>? launchRefusal = null) =>
        new(codex, () => exe, () => Path.GetDirectoryName(exe)!, () => new Dictionary<string, string>(), presence: new AgentPresence(),
            model: () => model, role: Pos, launchRefusal: launchRefusal, seatProbe: BrokerProbe(), seats: register,
            door: () => DoorAt, surfaceProbe: surface);

    /// <summary>The CLI's own reports under the broker's keys, canned: the broker allowlist on, its shell tools off, the door listed.</summary>
    internal static SeatHomeTests.FakeProbe BrokerProbe() => new(
        "unified_exec   stable   true\nshell_snapshot   stable   true\nshell_tool   stable   false\nview_image   stable   false\n" +
        "sleep_tool   stable   false\nplugins   stable   false\n",
        mcp: $"[{{\"name\": \"{BrokerSeat.DoorServer}\"}}]");

    /// <summary>A surface probe that answers what the test gives it and counts how often it is asked.</summary>
    internal sealed class FakeSurface(params string[] offered) : ISurfaceProbe
    {
        public int Asks;

        public Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, CancellationToken ct)
        {
            Interlocked.Increment(ref Asks);
            return Task.FromResult<IReadOnlyList<string>>(offered);
        }
    }
}
