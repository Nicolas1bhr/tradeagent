using System.Runtime.CompilerServices;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// An <see cref="AgentSession"/> driven over a REAL child process that prints a canned stream.
///
/// Not a fake session: the point of these tests is the path from a CLI's stdout to what the app
/// records, and that path is the process start, the stdin close, the line pump and the event parse.
/// A stand-in for the session would exercise none of it, and the defect being guarded against — a
/// reported token count arriving nowhere — lives in exactly that stretch. The program is a one-line
/// script because the vendors' own binaries are not on a CI machine.
/// </summary>
internal static class AgentRuntimeProbe
{
    public static AgentSession SessionOverStream(string stream, bool streaming = true,
        [CallerMemberName] string name = "")
    {
        var dir = Path.Combine(TestEnv.Home, "meter", name);
        Directory.CreateDirectory(dir);

        var payload = Path.Combine(dir, "stream.txt");
        File.WriteAllText(payload, stream.Replace("\r\n", "\n") + "\n");

        string script;
        if (OperatingSystem.IsWindows())
        {
            script = Path.Combine(dir, "runtime.cmd");
            File.WriteAllText(script, $"@echo off\r\ntype \"{payload}\"\r\n");
        }
        else
        {
            script = Path.Combine(dir, "runtime.sh");
            File.WriteAllText(script, $"#!/bin/sh\ncat \"{payload}\"\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var manifest = new RuntimeManifest
        {
            Id = "probe",
            DisplayName = "Probe runtime",
            Executable = script,
            ExecArgs = ["{prompt}"],
            ResumeArgs = ["{prompt}"],
            JsonFlag = streaming ? "--json" : null
        };

        return new AgentSession(manifest, () => script, () => dir, () => new Dictionary<string, string>());
    }
}

/// <summary>
/// WHAT THE AI COSTS, TAKEN FROM THE RUNTIME'S OWN STREAM AND NEVER FROM A GUESS.
///
/// The stream below is not a fixture somebody wrote to make a parser pass. It is the whole of what
/// Codex CLI 0.153.4 printed on this Mac on 2026-09-06 for
/// <c>codex exec -s read-only --skip-git-repo-check --json "say hi"</c>, four lines, copied
/// verbatim. Every assertion about "what the CLI emits" in this file is an assertion about that
/// recording, so a vendor that changes the event shape breaks a test here rather than silently
/// billing zero.
/// </summary>
public class TurnMeterTests
{
    /// <summary>
    /// The measured run, verbatim. The last line is the only one carrying token counts, it carries
    /// no model name, and <c>cached_input_tokens</c> is a SUBSET of <c>input_tokens</c>.
    /// </summary>
    public const string CodexStream =
        """
        {"type":"thread.started","thread_id":"01a0772f-7817-7280-8592-1511135a8d2c"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Hi!"}}
        {"type":"turn.completed","usage":{"input_tokens":17232,"cached_input_tokens":12928,"cache_write_input_tokens":0,"output_tokens":6,"reasoning_output_tokens":0}}
        """;

    static TurnUsage? UsageOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return TurnUsage.Read(doc.RootElement);
    }

    /// <summary>
    /// The numbers off the real event, exactly as it wrote them — and the derived one the bill is
    /// actually computed from, because cached input is inside the input total rather than beside it.
    /// </summary>
    [Fact]
    public void The_usage_event_codex_emits_is_read_as_the_tokens_it_reports()
    {
        var lines = CodexStream.Replace("\r\n", "\n").Split('\n');
        var usage = UsageOf(lines[^1]);

        Assert.NotNull(usage);
        Assert.Equal(17232, usage!.InputTokens);
        Assert.Equal(12928, usage.CachedInputTokens);
        Assert.Equal(0, usage.CacheWriteInputTokens);
        Assert.Equal(6, usage.OutputTokens);
        Assert.Equal(0, usage.ReasoningOutputTokens);
        Assert.Equal(4304, usage.UncachedInputTokens);

        // Measured: nothing in that run named a model. A parser that produced one would be inventing.
        Assert.Null(usage.Model);
    }

    /// <summary>The other three lines carry no counts, and must not be read as a turn that used none.</summary>
    [Fact]
    public void The_events_that_carry_no_tokens_report_nothing_rather_than_zero()
    {
        foreach (var line in CodexStream.Replace("\r\n", "\n").Split('\n')[..^1])
            Assert.Null(UsageOf(line));
    }

    /// <summary>
    /// THE SEAM ITEM 1 IS ABOUT: a turn whose stream carried usage arrives at the mission loop's
    /// TurnEnded with that usage on it. Red before the parse was wired — the session read the same
    /// four lines, raised the same event, and <c>Usage</c> was null, so nothing downstream could
    /// record a token the CLI had plainly reported.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_stream_reported_usage_ends_carrying_it()
    {
        var session = AgentRuntimeProbe.SessionOverStream(CodexStream);

        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        await session.SendAsync("say hi");

        Assert.NotNull(ended);
        Assert.NotNull(ended!.Usage);
        Assert.Equal(17232, ended.Usage!.InputTokens);
        Assert.Equal(6, ended.Usage.OutputTokens);
    }

    /// <summary>
    /// A runtime that reports its tokens twice in one run is SUMMED, not last-wins. The direction is
    /// the point: over-stating a bill can only stop the AI early, and under-stating it lets the cap
    /// be walked past. Codex 0.153.4 emits one, so this is about the next runtime, not that one.
    /// </summary>
    [Fact]
    public async Task Two_usage_events_in_one_run_are_added_rather_than_replaced()
    {
        var stream =
            """
            {"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":7}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":3}}
            """;
        var session = AgentRuntimeProbe.SessionOverStream(stream);

        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        await session.SendAsync("go");

        Assert.Equal(110, ended!.Usage!.InputTokens);
        Assert.Equal(10, ended.Usage.OutputTokens);
    }

    /// <summary>
    /// A runtime with no stream at all reports nothing, and nothing is what is recorded. This is the
    /// "cost unknown" case the card has to say out loud rather than round to zero.
    /// </summary>
    [Fact]
    public async Task A_runtime_that_reports_no_usage_ends_carrying_none()
    {
        var session = AgentRuntimeProbe.SessionOverStream("I did the thing.", streaming: false);

        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        await session.SendAsync("go");

        Assert.NotNull(ended);
        Assert.Null(ended!.Usage);
    }

    /// <summary>The other CLIs' spellings, so a rename is a data problem and not a silent zero.</summary>
    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":11,"completion_tokens":2}}""", 11, 2)]
    [InlineData("""{"info":{"usage":{"input_tokens":5,"output_tokens":1}}}""", 5, 1)]
    [InlineData("""{"input_tokens":9,"output_tokens":4}""", 9, 4)]
    public void The_other_spellings_these_tools_use_are_read_too(string line, long input, long output)
    {
        var usage = UsageOf(line);
        Assert.Equal(input, usage!.InputTokens);
        Assert.Equal(output, usage.OutputTokens);
    }

    /// <summary>A model the runtime DOES name is taken from it, in preference to anything declared.</summary>
    [Fact]
    public void A_model_the_runtime_names_is_read_from_the_event()
    {
        var usage = UsageOf("""{"usage":{"input_tokens":1,"output_tokens":1,"model":"gpt-5.1-codex-max"}}""");
        Assert.Equal("gpt-5.1-codex-max", usage!.Model);
    }
}

/// <summary>
/// THE BILL: one line per turn in the app's own state directory, today's totals in <c>kv</c>, and a
/// price only where <c>costs.json</c> supplies one.
///
/// It writes the real <c>costs.json</c> under the assembly's test home, so it shares the vendor-file
/// collection with the classes that corrupt the other two on purpose.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TurnRecordTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl");

    public void Dispose()
    {
        NoCosts();
        _db.Dispose();
    }

    static void NoCosts()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    /// <summary>
    /// The owner's own price list, as a test fixture and nothing else. These are NOT this repository
    /// asserting what OpenAI charges — that is exactly the claim <see cref="AgentCosts"/> refuses to
    /// ship — they are numbers chosen so the arithmetic below has one right answer.
    /// </summary>
    static void CostsAre(decimal input, decimal cached, decimal output) =>
        File.WriteAllText(CostCatalog.OverridePath, Json.Write(new AgentCosts
        {
            Currency = "USD",
            Models = [new ModelPrice
            {
                Model = "test-model", InputPerMillion = input,
                CachedInputPerMillion = cached, OutputPerMillion = output
            }],
            RuntimeModels = new(StringComparer.OrdinalIgnoreCase) { ["probe"] = "test-model" }
        }, pretty: true));

    /// <summary>The usage Codex actually reported on this Mac, as an ended turn.</summary>
    static AgentTurnEnded CodexTurn(DateTimeOffset at, int exitCode = 0) =>
        new(exitCode, TimeSpan.FromSeconds(12.5), "…", at)
        {
            Usage = new TurnUsage(17232, 12928, 0, 6, 0, null)
        };

    TurnMeter Meter(decimal cap = 5m, Func<DateTimeOffset>? now = null) =>
        new(_db, () => cap, session: () => "thread-1", runtimeId: () => "probe",
            now: now ?? (() => DateTimeOffset.Now), recordPath: _records);

    /// <summary>
    /// WHERE THE RECORD LIVES IS A SAFETY PROPERTY, not a tidiness one. The agent may write anywhere
    /// under its own home, so a bill kept there is a bill the party being billed can rewrite — the
    /// same reason the material ledger keeps the scanner's measurements out of the agent's reach.
    /// </summary>
    [Fact]
    public void The_record_is_in_the_apps_state_directory_and_not_the_agents_home()
    {
        Assert.StartsWith(Paths.State, TurnMeter.RecordPath, StringComparison.Ordinal);
        Assert.DoesNotContain(Paths.AgentHome, TurnMeter.RecordPath, StringComparison.Ordinal);
        Assert.EndsWith("agent-turns.jsonl", TurnMeter.RecordPath, StringComparison.Ordinal);
    }

    [Fact]
    public void One_line_per_turn_carries_what_was_measured_about_that_turn()
    {
        NoCosts();
        var at = DateTimeOffset.Now;
        var meter = Meter();
        meter.Record(CodexTurn(at));
        meter.Record(CodexTurn(at.AddSeconds(30), exitCode: 2));

        var lines = File.ReadAllLines(_records).Where(l => l.Trim().Length > 0).ToArray();
        Assert.Equal(2, lines.Length);

        var first = Json.Read<TurnRecord>(lines[0])!;
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("thread-1", first.Session);
        Assert.Equal("probe", first.Runtime);
        Assert.Equal(17232, first.InputTokens);
        Assert.Equal(12928, first.CachedInputTokens);
        Assert.Equal(6, first.OutputTokens);
        Assert.Equal(12.5, first.Seconds, 3);
        Assert.Equal(at, first.Ended);
        Assert.Equal(at.AddSeconds(-12.5), first.Started);

        Assert.Equal(2, Json.Read<TurnRecord>(lines[1])!.ExitCode);
    }

    /// <summary>
    /// The two totals, in <c>kv</c> — no new table, and the file above is the detail behind them.
    /// </summary>
    [Fact]
    public void Todays_totals_are_kept_in_kv()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);
        var meter = Meter();
        meter.Record(CodexTurn(DateTimeOffset.Now));
        meter.Record(CodexTurn(DateTimeOffset.Now));

        Assert.Equal(2, meter.Today.Turns);
        Assert.Equal(0, meter.Today.UnpricedTurns);
        Assert.Equal(2, int.Parse(_db.GetKv(TurnMeter.TurnsKey)!));
        Assert.NotNull(_db.GetKv(TurnMeter.CostKey));
        Assert.Equal(DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd"), _db.GetKv(TurnMeter.DayKey));
    }

    /// <summary>
    /// MIDNIGHT IS THE OWNER'S, NOT UTC'S. An owner in Ljubljana whose day rolled over at 01:00 or
    /// 02:00 would be reading a "today" that is not the one on their wall.
    /// </summary>
    [Fact]
    public void The_totals_reset_at_local_midnight_and_the_resume_time_is_that_midnight()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);

        var lateLastNight = new DateTimeOffset(2026, 9, 6, 23, 59, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 6)));
        var now = lateLastNight;
        var meter = Meter(now: () => now);

        meter.Record(CodexTurn(now));
        Assert.Equal(1, meter.Today.Turns);
        Assert.True(meter.Today.Spent > 0m);

        // The same wall clock, two minutes later, on the other side of midnight.
        Assert.Equal(lateLastNight.ToLocalTime().Date.AddDays(1), meter.Today.ResumesAt.LocalDateTime);
        now = lateLastNight.AddMinutes(2);

        Assert.Equal(0, meter.Today.Turns);
        Assert.Equal(0m, meter.Today.Spent);
    }

    /// <summary>
    /// The arithmetic, on the real event. Cached input is a SUBSET of the input count, so billing
    /// both in full would charge the cached three quarters of that turn twice.
    /// </summary>
    [Fact]
    public void Cached_input_is_billed_once_at_the_cached_rate()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);

        // 4,304 uncached at 1.25 + 12,928 cached at 0.125 + 6 output at 10, per million.
        var expected = (4304m * 1.25m + 12928m * 0.125m + 6m * 10m) / 1_000_000m;
        var price = CostCatalog.Price(new TurnUsage(17232, 12928, 0, 6, 0, null), "probe");

        Assert.Null(price.Unpriced);
        Assert.Equal(expected, price.Cost);
        Assert.Equal("USD", price.Currency);

        var meter = Meter();
        meter.Record(CodexTurn(DateTimeOffset.Now));
        Assert.Equal(expected, meter.Today.Spent);
    }

    /// <summary>
    /// NEVER A GUESSED NUMBER. With no prices on the machine the turn is still recorded, with its
    /// tokens, and its cost is absent and says why — not zero, which would read as a free turn.
    /// </summary>
    [Fact]
    public void With_no_prices_the_turn_is_recorded_unpriced_rather_than_free()
    {
        NoCosts();
        var meter = Meter();
        meter.Record(CodexTurn(DateTimeOffset.Now));

        var record = Json.Read<TurnRecord>(File.ReadAllLines(_records)[0])!;
        Assert.Null(record.Cost);
        Assert.Null(record.Currency);
        Assert.NotNull(record.Unpriced);
        Assert.Equal(17232, record.InputTokens);

        var today = meter.Today;
        Assert.Equal(1, today.Turns);
        Assert.Equal(1, today.UnpricedTurns);
        Assert.Equal(0m, today.Spent);
        Assert.NotNull(today.WhyNoPrice);
    }

    /// <summary>
    /// Codex 0.153.4 names no model, so without an entry the owner wrote there is nothing to price
    /// against — and the sentence says that, rather than the software choosing a model for them.
    /// </summary>
    [Fact]
    public void A_runtime_that_names_no_model_is_unpriced_until_costs_json_names_one_for_it()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);
        var usage = new TurnUsage(17232, 12928, 0, 6, 0, null);

        Assert.Null(CostCatalog.Price(usage, "codex").Cost);
        Assert.Contains(Labels.CostsFile, CostCatalog.Price(usage, "codex").Unpriced);
        Assert.NotNull(CostCatalog.Price(usage, "probe").Cost);
    }

    /// <summary>A turn the runtime said nothing about is unpriced for a different, named reason.</summary>
    [Fact]
    public void A_turn_the_runtime_reported_nothing_about_is_unpriced_and_says_so()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);
        var price = CostCatalog.Price(null, "probe");
        Assert.Null(price.Cost);
        Assert.Contains("did not report", price.Unpriced);
    }

    /// <summary>
    /// An override file that EXISTS and does not parse is not an absent one — <c>U-batch-2b</c>'s
    /// rule, applied to the third of these files. It cannot stop the AI the way an unreadable
    /// <c>runtimes.json</c> does, because it only prices work already done; what it does is make the
    /// cost unknown and say so, in the sentence the owner reads.
    /// </summary>
    [Fact]
    public void An_unreadable_costs_file_makes_every_turn_unpriced_in_the_owners_words()
    {
        File.WriteAllText(CostCatalog.OverridePath, "{ \"currency\": ");
        try
        {
            var read = CostCatalog.Read();
            Assert.Null(read.Costs);
            Assert.NotNull(read.Unreadable);

            var price = CostCatalog.Price(new TurnUsage(1, 0, 0, 1, 0, "test-model"), "probe");
            Assert.Null(price.Cost);
            Assert.Contains(Labels.CostsFile, price.Unpriced);
            Assert.Contains("daily limit", price.Unpriced);
        }
        finally { NoCosts(); }
    }

    /// <summary>An ABSENT file is the shipped configuration and is not a refusal. It ships no prices.</summary>
    [Fact]
    public void An_absent_costs_file_is_not_a_refusal_and_ships_no_prices()
    {
        NoCosts();
        var read = CostCatalog.Read();
        Assert.Null(read.Unreadable);
        Assert.NotNull(read.Costs);
        Assert.Empty(read.Costs!.Models);
    }
}
