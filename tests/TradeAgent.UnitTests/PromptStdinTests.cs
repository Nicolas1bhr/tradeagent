using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-PROMPT-STDIN: A SEAT'S PACKET AND THE OWNER'S WORDS REACH THE RUNTIME ON STDIN, NEVER ON ITS COMMAND LINE
/// (<c>docs/ORCHESTRATION.md</c> § 2.7; INBOX-001, assessor independence).
///
/// <para>Before this the whole prompt was the last argument of <c>codex exec</c>, where any same-user process — a confined cell
/// included — reads it from <c>/proc/&lt;pid&gt;/cmdline</c> or the Windows process command line, where cmd.exe parsed it on the
/// <c>.cmd</c>-shim path, and where argv capped it (128 KiB per argument on Linux). CI has no codex, so the program is a script
/// that records the command line it was started with and everything it read on stdin; the manifest is the SHIPPED codex
/// manifest with its seat profile, so what is under test is the vendor's own data and the seat check that verifies it.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PromptStdinTests
{
    /// <summary>
    /// (a) THE PROMPT IS NOT IN THE CHILD'S ARGV; it arrives whole on stdin, and the slot carries the vendor's stdin marker.
    /// </summary>
    [Fact]
    public async Task A_turn_never_puts_the_prompt_in_the_childs_argv()
    {
        var (codex, exe, rec) = Recorder(nameof(A_turn_never_puts_the_prompt_in_the_childs_argv));
        var session = SeatHomeTests.Session(codex, exe, rec.Dir, new SeatHomeTests.FakeProbe());
        const string Prompt = "Owner-words-7f3a: buy nothing today, read the inbox first";

        var ended = await SeatHomeTests.Turn(session, Prompt);

        Assert.Equal(0, ended.ExitCode);
        var argv = Assert.Single(rec.Argv());
        Assert.DoesNotContain("7f3a", argv, StringComparison.Ordinal);
        Assert.StartsWith("exec ", argv, StringComparison.Ordinal);
        Assert.EndsWith(" -", argv, StringComparison.Ordinal);
        Assert.Equal(Prompt, rec.Stdin());
    }

    /// <summary>(b) THE RESUMED TURN TOO: <c>exec resume --last -</c>, its prompt on stdin and nowhere on its command line.</summary>
    [Fact]
    public async Task A_resumed_turn_reads_its_prompt_from_stdin_too()
    {
        var (codex, exe, rec) = Recorder(nameof(A_resumed_turn_reads_its_prompt_from_stdin_too));
        var session = SeatHomeTests.Session(codex, exe, rec.Dir, new SeatHomeTests.FakeProbe());

        Assert.Equal(0, (await SeatHomeTests.Turn(session, "first-turn-words-1111")).ExitCode);
        Assert.Equal("first-turn-words-1111", rec.Stdin());
        Assert.Equal(0, (await SeatHomeTests.Turn(session, "second-turn-words-2222")).ExitCode);

        var argv = rec.Argv();
        Assert.Equal(2, argv.Length);
        Assert.StartsWith("exec resume --last ", argv[1], StringComparison.Ordinal);
        Assert.EndsWith(" -", argv[1], StringComparison.Ordinal);
        Assert.DoesNotContain(argv, a => a.Contains("1111", StringComparison.Ordinal) || a.Contains("2222", StringComparison.Ordinal));
        Assert.Equal("second-turn-words-2222", rec.Stdin());
    }

    /// <summary>
    /// (c) A PROMPT OVER THE ARGV CAP REACHES THE RUNTIME WHOLE: 300 KiB and more, over Linux's 128 KiB per argument and
    /// Windows' 32,767 characters per command line. Lines under 8 KiB, because the Windows recorder is <c>findstr</c>.
    /// </summary>
    [Fact]
    public async Task A_prompt_over_the_argv_cap_reaches_the_runtime_whole()
    {
        var (codex, exe, rec) = Recorder(nameof(A_prompt_over_the_argv_cap_reaches_the_runtime_whole));
        var session = SeatHomeTests.Session(codex, exe, rec.Dir, new SeatHomeTests.FakeProbe());
        var prompt = string.Join('\n', Enumerable.Range(0, 3100).Select(i => $"{i:D6} " + new string((char)('a' + i % 26), 92)));
        Assert.True(Encoding.UTF8.GetByteCount(prompt) > 300 * 1024);

        var ended = await SeatHomeTests.Turn(session, prompt);

        Assert.Equal(0, ended.ExitCode);
        Assert.True(prompt == rec.Stdin(), $"stdin carried {rec.Stdin().Length} of {prompt.Length} characters");
        Assert.DoesNotContain(rec.Argv(), a => a.Contains("000001 b", StringComparison.Ordinal));
    }

    /// <summary>
    /// (e) THE <c>.cmd</c>-SHIM PATH CARRIES NO PROMPT ON ITS COMMAND LINE. On Windows the recorder is a <c>.cmd</c>, so the turn
    /// runs through <c>ComSpec /c</c>, whose parser expands <c>%VAR%</c> and splits on <c>&amp;</c> even inside quotes: a prompt
    /// on that line was the owner's words rewritten by cmd.exe. On stdin it arrives byte for byte. Everywhere else the same
    /// prompt proves only that stdin is verbatim.
    /// </summary>
    [Fact]
    public async Task The_cmd_shim_path_carries_no_prompt_on_its_command_line()
    {
        var (codex, exe, rec) = Recorder(nameof(The_cmd_shim_path_carries_no_prompt_on_its_command_line));
        if (OperatingSystem.IsWindows()) Assert.EndsWith(".cmd", exe, StringComparison.OrdinalIgnoreCase);
        var session = SeatHomeTests.Session(codex, exe, rec.Dir, new SeatHomeTests.FakeProbe());
        const string Prompt = "Shim-9c2e: 50% & \"quoted\" <tag> ^caret %PATH% | done";

        var ended = await SeatHomeTests.Turn(session, Prompt);

        Assert.Equal(0, ended.ExitCode);
        Assert.DoesNotContain(rec.Argv(), a => a.Contains("9c2e", StringComparison.Ordinal));
        Assert.Equal(Prompt, rec.Stdin());
    }

    /// <summary>
    /// THE SEAT'S ARGV CHECK EXPECTS THE STDIN MARKER, NEVER THE PROMPT: the command line a seat is verified on, fresh or resumed,
    /// is refused in words when its slot carries the prompt instead of <c>-</c> — the last check before a start, whoever built it.
    /// </summary>
    [Fact]
    public async Task A_seat_argv_with_the_prompt_in_its_slot_is_refused()
    {
        var (codex, exe, rec) = Recorder(nameof(A_seat_argv_with_the_prompt_in_its_slot_is_refused));
        foreach (var resuming in new[] { false, true })
        {
            var good = AgentArgs.For(codex, "PROMPT", resuming, "gpt-5.6-sol");
            Assert.False((await SeatProfile.VerifyAsync(codex, exe, good, rec.Dir, new SeatHomeTests.FakeProbe(), default)).Refused);

            var leaked = good.ToList();
            leaked[^1] = "PROMPT";
            var verdict = await SeatProfile.VerifyAsync(codex, exe, leaked, rec.Dir, new SeatHomeTests.FakeProbe(), default);
            Assert.True(verdict.Refused, $"resuming: {resuming}");
            Assert.Equal(ErrorCode.AI_SEAT_REFUSED, verdict.Code);
            Assert.Contains("travels on its input and not on its command line", verdict.Refusal, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// (d) A RUNTIME THAT CANNOT READ STDIN IS NAMED IN THE LAUNCH, NOT SILENTLY FED ARGV. The flag is data on every built-in —
    /// codex and OpenCode read stdin, a custom tool is not assumed to — for <c>U-thinking-seats</c>' gate to read; a runtime
    /// without it still runs as it always did, and every attempt it makes says "prompt in argv (runtime cannot read stdin)" in
    /// the row, where a runtime that reads stdin says <c>stdin</c>.
    /// </summary>
    [Fact]
    public async Task A_runtime_that_cannot_read_stdin_is_named_in_the_launch_not_silently_fed_argv()
    {
        var builtIn = RuntimeCatalog.BuiltIn();
        Assert.True(builtIn.Single(m => m.Id == "codex").PromptOnStdin);
        Assert.Equal("-", builtIn.Single(m => m.Id == "codex").StdinMarker);
        Assert.True(builtIn.Single(m => m.Id == "opencode").PromptOnStdin);
        Assert.Equal("", builtIn.Single(m => m.Id == "opencode").StdinMarker);
        Assert.False(builtIn.Single(m => m.Id == "custom").PromptOnStdin);

        foreach (var onStdin in new[] { false, true })
        {
            using var db = TestEnv.NewDb();
            var dir = Path.Combine(TestEnv.Home, "prompt-stdin", $"meter-{onStdin}-{Guid.NewGuid():n}"[..30]);
            Directory.CreateDirectory(dir);
            var meter = new TurnMeter(db, () => 100m, runtimeId: () => "probe", recordPath: Path.Combine(dir, "turns.jsonl"),
                live: new LiveAttempts());
            var session = AgentRuntimeProbe.SessionOverStream(
                """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}""",
                manifest: script => new RuntimeManifest
                {
                    Id = "probe", DisplayName = "Probe runtime", Executable = script,
                    ExecArgs = ["{prompt}"], ResumeArgs = ["{prompt}"], JsonFlag = "--json",
                    PromptOnStdin = onStdin, StdinMarker = onStdin ? "-" : ""
                },
                name: $"{nameof(A_runtime_that_cannot_read_stdin_is_named_in_the_launch_not_silently_fed_argv)}-{onStdin}");
            using (meter.Attach(session, CouncilRoles.Operations))
                await session.SendAsync("words-5b1d");

            var context = Context(db);
            Assert.True(context.TryGetProperty("prompt_in", out var promptIn), $"the attempt's context carries no prompt_in: {context}");
            Assert.Equal(onStdin ? "stdin" : "prompt in argv (runtime cannot read stdin)", promptIn.GetString());
        }
    }

    /// <summary>
    /// A RUNTIME WHOSE MANIFEST SAYS STDIN NEVER CARRIES THE PROMPT ON ITS COMMAND LINE, WHATEVER STANDS FOR ITS MARKER, and its
    /// attempt says what the command line actually carried. Read through the real <c>runtimes.json</c> reader, which writes an
    /// explicit JSON <c>null</c> into the marker: codex's shape (true, "-"), OpenCode's (true, ""), the null shape (true, null) —
    /// the slot gets the marker or nothing, never the prompt — and a runtime that cannot read stdin (false), whose prompt is on
    /// its command line and whose attempt says so.
    /// </summary>
    [Fact]
    public async Task A_stdin_runtime_never_carries_the_prompt_on_its_command_line_whatever_stands_for_its_marker()
    {
        const string Prompt = "Owner-words-4c8e read the inbox first";
        var rows = new (bool OnStdin, string MarkerJson, string Argv)[]
        {
            (true, "\"-\"", "--json -"),
            (true, "\"\"", "--json"),
            (true, "null", "--json"),
            (false, "\"\"", $"--json {Prompt}")
        };
        try
        {
            foreach (var (onStdin, markerJson, expected) in rows)
            {
                var row = $"({onStdin}, {markerJson})";
                var (script, rec) = StreamRecorder($"marker-{onStdin}-{markerJson.Trim('"')}");
                File.WriteAllText(RuntimeCatalog.OverridePath,
                    "[{\"id\":\"probe-stdin\",\"display_name\":\"Probe runtime\",\"executable\":" +
                    System.Text.Json.JsonSerializer.Serialize(script) +
                    ",\"exec_args\":[\"{prompt}\"],\"resume_args\":[\"{prompt}\"],\"json_flag\":\"--json\"," +
                    $"\"prompt_on_stdin\":{(onStdin ? "true" : "false")},\"stdin_marker\":{markerJson}}}]");
                var manifest = RuntimeCatalog.Require("probe-stdin");
                if (markerJson == "null") Assert.Null(manifest.StdinMarker);

                using var db = TestEnv.NewDb();
                var meter = new TurnMeter(db, () => 100m, runtimeId: () => "probe-stdin",
                    recordPath: Path.Combine(rec.Dir, "turns.jsonl"), live: new LiveAttempts());
                var session = new AgentSession(manifest, () => script, () => rec.Dir, () => new Dictionary<string, string>(),
                    presence: new AgentPresence()) { Admit = _ => TradeAgent.Core.Db.AiAdmission.NoLedger };
                using (meter.Attach(session, CouncilRoles.Operations))
                    await session.SendAsync(Prompt);

                // Quotes dropped: cmd.exe's echo prints the quoting the command line was given, which is not the question.
                var line = Assert.Single(rec.Argv()).Replace("\"", "", StringComparison.Ordinal);
                Assert.True(expected == line, $"{row}: the command line was \"{line}\"");
                Assert.True((onStdin ? Prompt : "") == rec.Stdin(), $"{row}: stdin carried \"{rec.Stdin()}\"");
                Assert.True(Context(db).TryGetProperty("prompt_in", out var promptIn), $"{row}: no prompt_in");
                Assert.True((onStdin ? AgentArgs.OnStdin : AgentArgs.InArgv) == promptIn.GetString(),
                    $"{row}: the attempt says \"{promptIn.GetString()}\" of a command line \"{line}\"");
            }
        }
        finally { if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath); }
    }

    /// <summary>
    /// THE SEAT'S CHECK READS A MISSING MARKER AS NO MARKER, never as a crash: the shipped codex manifest with the null an override
    /// file can write into its marker still starts, its slot empty and its prompt on stdin.
    /// </summary>
    [Fact]
    public async Task A_seat_whose_marker_is_null_starts_with_its_prompt_on_stdin_and_not_on_its_command_line()
    {
        var (codex, exe, rec) = Recorder(nameof(A_seat_whose_marker_is_null_starts_with_its_prompt_on_stdin_and_not_on_its_command_line));
        codex.StdinMarker = null!;
        var session = SeatHomeTests.Session(codex, exe, rec.Dir, new SeatHomeTests.FakeProbe());
        const string Prompt = "Seat-words-6d0b: hold, read the inbox";

        var ended = await SeatHomeTests.Turn(session, Prompt);

        Assert.True(ended.ExitCode == 0, $"the turn ended {ended.ExitCode}: {ended.Raw}");
        Assert.DoesNotContain(rec.Argv(), a => a.Contains("6d0b", StringComparison.Ordinal));
        Assert.Equal(Prompt, rec.Stdin());
        Assert.Equal(AgentArgs.OnStdin, ended.PromptIn);
    }

    /// <summary>
    /// (d, THE LAUNCH) A RUNTIME WHOSE COMMAND LINE CARRIES THE PROMPT IS NAMED ON THE LINE THE OWNER READS AS IT IS LAUNCHED: the
    /// agent runtime's row says "prompt in argv (runtime cannot read stdin)" beside its name and version, and a runtime that reads
    /// stdin says nothing of the kind. The record in the attempt's row is not a line anybody reads at the launch.
    /// </summary>
    [Fact]
    public async Task A_runtime_whose_command_line_carries_the_prompt_says_so_on_its_launch_line()
    {
        foreach (var onStdin in new[] { false, true })
        {
            var dir = Path.Combine(TestEnv.Home, "prompt-stdin", $"launch-{onStdin}-{Guid.NewGuid():n}"[..32]);
            Directory.CreateDirectory(dir);
            string exe;
            if (OperatingSystem.IsWindows())
            {
                exe = Path.Combine(dir, "probe.cmd");
                File.WriteAllText(exe, "@echo off\r\necho probe 1.2.3\r\nexit /b 0\r\n");
            }
            else
            {
                exe = Path.Combine(dir, "probe.sh");
                File.WriteAllText(exe, "#!/bin/sh\necho probe 1.2.3\nexit 0\n");
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var manifest = new RuntimeManifest
            {
                Id = "probe-launch", DisplayName = "Probe runtime", Executable = exe, VersionArgs = [],
                ExecArgs = ["{prompt}"], ResumeArgs = ["{prompt}"], PromptOnStdin = onStdin, StdinMarker = onStdin ? "-" : ""
            };
            var health = new HealthRegistry();

            await new AgentSupervisor(health).PrepareAsync(manifest, new WorkspaceContext(
                ConnectorName: "Simulator (built in)", ConnectorIsPaper: true, AccountId: "SIM-001",
                Mode: TradingMode.PAPER, ExecutionAvailable: true, ExecutionBlockedReason: null,
                Risk: new RiskPolicy { MaxDailyLoss = 500m, InstrumentAllowlist = ["ES"] }));

            var row = health.Get(Components.AgentRuntime);
            Assert.Equal(HealthState.READY, row.State);
            Assert.StartsWith("Probe runtime 1.2.3", row.Detail, StringComparison.Ordinal);
            if (onStdin) Assert.DoesNotContain("prompt in argv", row.Detail, StringComparison.Ordinal);
            else Assert.Contains(AgentArgs.InArgv, row.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A recorder that also answers like a runtime: its command line to <c>argv.txt</c>, its stdin to <c>stdin.txt</c>, then one
    /// completed turn on stdout so the meter closes the attempt.
    /// </summary>
    static (string Script, Recording Rec) StreamRecorder(string name)
    {
        var dir = Path.Combine(TestEnv.Home, "prompt-stdin", $"{name}-{Guid.NewGuid():n}"[..32]);
        Directory.CreateDirectory(dir);
        var argv = Path.Combine(dir, "argv.txt");
        var stdin = Path.Combine(dir, "stdin.txt");
        var payload = Path.Combine(dir, "stream.txt");
        File.WriteAllText(payload, """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}""" + "\n");
        string script;
        if (OperatingSystem.IsWindows())
        {
            script = Path.Combine(dir, "runtime.cmd");
            File.WriteAllText(script,
                $"@echo off\r\necho %* >> \"{argv}\"\r\nfindstr \"^\" > \"{stdin}\"\r\ntype \"{payload}\"\r\nexit /b 0\r\n");
        }
        else
        {
            script = Path.Combine(dir, "runtime.sh");
            File.WriteAllText(script, $"#!/bin/sh\necho \"$*\" >> \"{argv}\"\ncat > \"{stdin}\"\ncat \"{payload}\"\nexit 0\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (script, new Recording(dir));
    }

    /// <summary>The newest attempt's <c>context</c>, as the app wrote it.</summary>
    static System.Text.Json.JsonElement Context(TradeAgent.Core.Db.Database db)
    {
        using var c = db.Cmd("SELECT id FROM ai_attempt ORDER BY started_at DESC, rowid DESC LIMIT 1");
        var id = Convert.ToString(c.ExecuteScalar())!;
        var row = new TradeAgent.Core.Db.AiAttemptStore(db).Get(id)!;
        using var doc = System.Text.Json.JsonDocument.Parse(row.Context!);
        return doc.RootElement.Clone();
    }

    /// <summary>What the recorder wrote: one command line per start, and the stdin of the last start.</summary>
    internal sealed record Recording(string Dir)
    {
        public string[] Argv()
        {
            var log = Path.Combine(Dir, "argv.txt");
            return File.Exists(log) ? [.. File.ReadAllLines(log).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
        }

        /// <summary>The last start's stdin, line endings normalised: <c>findstr</c> ends the last line with CRLF.</summary>
        public string Stdin()
        {
            var file = Path.Combine(Dir, "stdin.txt");
            return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n").TrimEnd('\n') : "";
        }
    }

    /// <summary>
    /// THE SHIPPED CODEX MANIFEST, seat profile and all, pointed at a recorder: a program that appends its command line to
    /// <c>argv.txt</c>, copies its stdin to <c>stdin.txt</c> and exits 0. The script is the pin; the seat home is the test's own.
    /// </summary>
    internal static (RuntimeManifest Codex, string Exe, Recording Rec) Recorder(string name)
    {
        var dir = Path.Combine(TestEnv.Home, "prompt-stdin", $"{name}-{Guid.NewGuid():n}"[..40]);
        Directory.CreateDirectory(dir);
        var argv = Path.Combine(dir, "argv.txt");
        var stdin = Path.Combine(dir, "stdin.txt");
        string exe;
        if (OperatingSystem.IsWindows())
        {
            exe = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(exe, $"@echo off\r\necho %* >> \"{argv}\"\r\nfindstr \"^\" > \"{stdin}\"\r\nexit /b 0\r\n");
        }
        else
        {
            exe = Path.Combine(dir, "codex");
            File.WriteAllText(exe, $"#!/bin/sh\necho \"$*\" >> \"{argv}\"\ncat > \"{stdin}\"\nexit 0\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        codex.Executable = exe;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(exe);
        codex.Seat!.Home = Path.Combine(dir, "seat-home");
        return (codex, exe, new Recording(dir));
    }
}
