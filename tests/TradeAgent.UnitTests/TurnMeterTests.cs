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
    /// <param name="sleepSeconds">
    /// Seconds the child waits before printing anything. Above zero it is a turn that is REALLY in
    /// flight — a live process holding the presence register — which is what a test about a turn
    /// killed before its usage arrived needs. Zero is the ordinary case and starts printing at once.
    /// </param>
    /// <param name="marker">
    /// A path the child TOUCHES before it prints anything. A test that has to prove no process was
    /// started asserts the file's absence, which is evidence about the operating system rather than
    /// about this class's own bookkeeping.
    /// </param>
    public static AgentSession SessionOverStream(string stream, bool streaming = true,
        int sleepSeconds = 0, Func<string?>? model = null, string? marker = null,
        [CallerMemberName] string name = "")
    {
        var dir = Path.Combine(TestEnv.Home, "meter", name);
        Directory.CreateDirectory(dir);

        var payload = Path.Combine(dir, "stream.txt");
        File.WriteAllText(payload, stream.Replace("\r\n", "\n") + "\n");

        string script;
        if (OperatingSystem.IsWindows())
        {
            // ping rather than timeout: `timeout` refuses to run with its input redirected, which is
            // exactly how every child here is started.
            var wait = sleepSeconds > 0 ? $"ping -n {sleepSeconds + 1} 127.0.0.1 > nul\r\n" : "";
            var touch = marker is null ? "" : $"type nul > \"{marker}\"\r\n";
            script = Path.Combine(dir, "runtime.cmd");
            File.WriteAllText(script, $"@echo off\r\n{touch}{wait}type \"{payload}\"\r\n");
        }
        else
        {
            var wait = sleepSeconds > 0 ? $"sleep {sleepSeconds}\n" : "";
            var touch = marker is null ? "" : $": > \"{marker}\"\n";
            script = Path.Combine(dir, "runtime.sh");
            File.WriteAllText(script, $"#!/bin/sh\n{touch}{wait}cat \"{payload}\"\n");
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

        // ITS OWN PRESENCE REGISTER, NOT THE PROCESS-WIDE ONE. AgentPresence.Shared is sticky by
        // design — once an agent has been alive in a process, no scan can attest a window that began
        // before it — so a child started here under the shared register would permanently downgrade
        // every inbox sighting in this assembly to InboxUnattested. Measured: it turned three of
        // MaterialLedgerTests red while both classes passed alone.
        return new AgentSession(manifest, () => script, () => dir, () => new Dictionary<string, string>(),
            presence: new AgentPresence(), model: model);
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
    /// TODAY'S TOTALS ARE SUMS OVER THE LAUNCH LEDGER, one row per turn, and the file above is the
    /// diagnostic mirror beside them.
    ///
    /// This test used to be <c>Todays_totals_are_kept_in_kv</c> and asserted two counters in the
    /// <c>kv</c> table. Those counters were written when a turn FINISHED, which is what made a
    /// killed turn free — see <c>AiAttemptStore</c>. <c>U-model</c> removed them, so the name and
    /// the assertions moved with the subject rather than being left describing a table the product
    /// no longer keeps this in.
    /// </summary>
    [Fact]
    public void Todays_totals_are_sums_over_the_launch_ledger()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);
        var meter = Meter();
        meter.Record(CodexTurn(DateTimeOffset.Now));
        meter.Record(CodexTurn(DateTimeOffset.Now));

        Assert.Equal(2, meter.Today.Turns);
        Assert.Equal(0, meter.Today.UnpricedTurns);

        var store = new AiAttemptStore(_db);
        var (from, to) = LocalDayOf(DateTimeOffset.Now);
        var rows = store.Between(from, to);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AiAttemptState.ENDED, r.State));
        Assert.All(rows, r => Assert.Equal(17232, r.InputTokens));
        Assert.Equal(meter.Today.Spent, rows.Sum(r => r.Cost ?? 0m));

        // The kv counters this replaces are gone rather than left running beside the table: two
        // sources for one number is how they come to disagree.
        Assert.Null(_db.GetKv("ai_meter_turns"));
        Assert.Null(_db.GetKv("ai_meter_cost"));
        Assert.Null(_db.GetKv("ai_meter_day"));
    }

    /// <summary>The same local-day window the meter sums over. See <c>TurnMeter.LocalDay</c>.</summary>
    static (DateTimeOffset From, DateTimeOffset To) LocalDayOf(DateTimeOffset now)
    {
        var start = now.ToLocalTime().Date;
        var end = start.AddDays(1);
        return (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
                new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)));
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
    /// A runtime with no list price of its own names no model and has nothing to be estimated
    /// against, so it stays unpriced until the owner's file names one — and the sentence says that,
    /// rather than the software choosing a model for them.
    ///
    /// <c>custom</c> is that runtime and is the honest example of it: its command, and therefore its
    /// provider, is written by an engineer in <c>runtimes.json</c>, so <see cref="ListPrices"/>
    /// deliberately ships nothing for it. <c>codex</c> USED to behave this way and no longer does —
    /// it now carries a dated catalogue and its unnamed model is charged at the dearest entry in it
    /// (<c>U-prices</c> item 2, asserted in <see cref="UnknownModelIsPricedHighTests"/>).
    /// </summary>
    [Fact]
    public void A_runtime_that_names_no_model_is_unpriced_until_costs_json_names_one_for_it()
    {
        CostsAre(input: 1.25m, cached: 0.125m, output: 10m);
        var usage = new TurnUsage(17232, 12928, 0, 6, 0, null);

        Assert.Null(CostCatalog.Price(usage, "custom").Cost);
        Assert.Contains(Labels.CostsFile, CostCatalog.Price(usage, "custom").Unpriced);
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

/// <summary>
/// THE PRICES THIS BUILD SHIPS: dated, sourced, per runtime id and model.
///
/// What is asserted here is NOT that OpenAI charges these amounts — no test can settle that, and a
/// test that pinned every figure would be this repository asserting a vendor's bill, which is the
/// claim <see cref="AgentCosts"/> refuses to make. What is asserted is the shape the figures must
/// keep to be checkable by the person who does settle it: every entry says which day it was read and
/// which page it was read from, every entry names a runtime this build actually installs, and a
/// model the vendor publishes no price for is ABSENT rather than filled in from a neighbour.
///
/// The one figure quoted in full is <c>gpt-5.3-codex</c>'s, because the arithmetic below has to have
/// one right answer; it was read from the page named in <see cref="ListPrices.OpenAiPrices"/> on
/// <see cref="ListPrices.ReadOn"/> and is quoted in this unit's report with that date.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class ShippedListPriceTests : IDisposable
{
    public void Dispose() => NoCosts();

    static void NoCosts()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    static void CostsAre(AgentCosts costs) =>
        File.WriteAllText(CostCatalog.OverridePath, Json.Write(costs, pretty: true));

    /// <summary>
    /// A PRICE WITH NO DATE IS A PRICE NOBODY CAN CHECK. These go stale on the vendor's schedule,
    /// so the day and the page travel with every one of them.
    /// </summary>
    [Fact]
    public void Every_shipped_price_carries_the_day_it_was_read_and_the_page_it_came_from()
    {
        Assert.NotEmpty(ListPrices.All);

        foreach (var p in ListPrices.All)
        {
            Assert.True(DateOnly.TryParse(p.PricedAt, out _), $"{p.Model} has no readable date: '{p.PricedAt}'");
            Assert.StartsWith("https://", p.Source, StringComparison.Ordinal);
            Assert.NotEqual("", p.Runtime);
            Assert.True(p.InputPerMillion > 0m, $"{p.Model} has no input price");
            Assert.True(p.OutputPerMillion > 0m, $"{p.Model} has no output price");
        }
    }

    /// <summary>
    /// THE CACHE-WRITE COLUMN IS ON THE PAGE, and every figure in it is DEARER than that model's
    /// input rate. <see cref="ModelPrice.CacheWritePerMillion"/> null means "charge the input rate",
    /// so leaving it null on a model the vendor prices separately under-charges every cache write —
    /// the one direction that lets the daily limit be walked past.
    ///
    /// Read from the standard table on <see cref="ListPrices.ReadOn"/>: the four Daybreak rows are
    /// the only ones the page gives that column for, and the rows below them stay null because the
    /// page gives them no such figure to carry.
    /// </summary>
    [Fact]
    public void The_rows_the_vendor_prices_cache_writes_for_carry_that_figure_and_the_rest_carry_none()
    {
        var published = new Dictionary<string, decimal>
        {
            ["gpt-6-astra"] = 12.50m,
            ["gpt-5.6-sol"] = 5.00m,
            ["gpt-5.6-terra"] = 2.50m,
            ["gpt-5.6-luna"] = 0.25m
        };

        foreach (var p in ListPrices.All)
        {
            if (published.TryGetValue(p.Model, out var write))
            {
                Assert.Equal(write, p.CacheWritePerMillion);
                Assert.True(write > p.InputPerMillion, $"{p.Model}: a cache write is dearer than input");
            }
            else
            {
                Assert.Null(p.CacheWritePerMillion);
            }
        }

        // And the charge follows the column rather than the input rate beside it.
        NoCosts();
        Assert.Equal(12.50m, CostCatalog.Price(new TurnUsage(0, 0, 1_000_000, 0, 0, "gpt-6-astra"), "codex").Cost);
    }

    /// <summary>
    /// Priced per runtime id, and only for the two whose provider this build knows. <c>custom</c> is
    /// an engineer's own command in <c>runtimes.json</c>: a price for it would be a guess about a
    /// vendor nobody here has heard of.
    /// </summary>
    [Fact]
    public void The_shipped_catalogue_covers_the_runtimes_whose_provider_is_known_and_no_others()
    {
        var runtimes = ListPrices.All.Select(p => p.Runtime).Distinct().Order().ToArray();
        Assert.Equal(["codex", "opencode"], runtimes);

        var known = RuntimeCatalog.BuiltIn().Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(runtimes, r => Assert.Contains(r, known));
        Assert.Null(CostCatalog.Highest(CostCatalog.BuiltIn(), "custom"));
    }

    /// <summary>
    /// A model the vendor's page does not price is left out. <c>gpt-5.3-codex-spark</c> is one of the
    /// five models the Codex model page recommends and the pricing page has no row for it, so it is
    /// absent — not estimated from the plain <c>gpt-5.3-codex</c> row that shares most of its name.
    /// </summary>
    [Fact]
    public void A_model_the_vendor_publishes_no_price_for_is_absent_rather_than_guessed()
    {
        Assert.DoesNotContain(ListPrices.All, p => p.Model == "gpt-5.3-codex-spark");
        Assert.Contains(ListPrices.All, p => p.Model == "gpt-5.3-codex");

        var price = CostCatalog.Price(new TurnUsage(1, 0, 0, 1, 0, "gpt-5.3-codex-spark"), "codex");
        Assert.Null(price.Cost);
        Assert.Contains("gpt-5.3-codex-spark", price.Unpriced!);
    }

    /// <summary>
    /// The arithmetic against a shipped figure, with no <c>costs.json</c> anywhere: one million
    /// uncached input tokens and one million output tokens at <c>gpt-5.3-codex</c>'s published rate.
    /// </summary>
    [Fact]
    public void A_model_the_stream_names_is_priced_from_the_shipped_list_with_no_file_at_all()
    {
        NoCosts();
        var price = CostCatalog.Price(new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "gpt-5.3-codex"), "codex");

        Assert.Null(price.Unpriced);
        Assert.Equal(1.75m + 14.00m, price.Cost);
        Assert.Equal("USD", price.Currency);
    }

    /// <summary>
    /// THE OWNER'S FILE WINS WHERE IT SPEAKS, and the shipped list stands where it does not — the
    /// arrangement <c>runtimes.json</c> already has. Both halves in one test, because it is the
    /// combination that is the claim.
    /// </summary>
    [Fact]
    public void The_owners_file_wins_for_a_model_it_prices_and_the_shipped_list_stands_for_the_rest()
    {
        CostsAre(new AgentCosts
        {
            Currency = "USD",
            Models = [new ModelPrice
            {
                Runtime = "codex", Model = "gpt-5.3-codex",
                InputPerMillion = 0.10m, OutputPerMillion = 0.20m
            }]
        });

        var mine = CostCatalog.Price(new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "gpt-5.3-codex"), "codex");
        Assert.Equal(0.10m + 0.20m, mine.Cost);

        var shipped = CostCatalog.Price(new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "gpt-5.6-luna"), "codex");
        Assert.Equal(0.20m + 1.20m, shipped.Cost);
    }

    /// <summary>
    /// A CATALOGUE IN ANOTHER CURRENCY STANDS ALONE. Merging dollar figures under a file that says
    /// EUR would produce a total labelled in euros that is partly not — the wrong bill that is
    /// hardest to notice, because every number in it looks reasonable.
    /// </summary>
    [Fact]
    public void Shipped_dollar_prices_are_not_merged_under_a_catalogue_in_another_currency()
    {
        CostsAre(new AgentCosts { Currency = "EUR", Models = [] });

        var price = CostCatalog.Price(new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "gpt-5.3-codex"), "codex");
        Assert.Null(price.Cost);
        Assert.Contains("gpt-5.3-codex", price.Unpriced!);
    }

    /// <summary>
    /// An entry that names no runtime prices a model BY NAME and is in no runtime's catalogue, so it
    /// cannot raise what an unidentified turn is charged. Both halves, again in one test.
    /// </summary>
    [Fact]
    public void A_price_that_names_no_runtime_prices_that_model_and_joins_no_runtimes_catalogue()
    {
        CostsAre(new AgentCosts
        {
            Currency = "USD",
            Models = [new ModelPrice { Model = "house-model", InputPerMillion = 3m, OutputPerMillion = 7m }]
        });

        Assert.Equal(3m + 7m,
            CostCatalog.Price(new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "house-model"), "custom").Cost);

        Assert.Null(CostCatalog.Highest(CostCatalog.Read().Costs!, "custom"));
    }
}

/// <summary>
/// AN UNKNOWN IS PRICED HIGH, NEVER ZERO AND NEVER ABSENT.
///
/// Codex 0.153.4's <c>--json</c> stream names no model anywhere — measured, see
/// <see cref="TurnUsage"/> — so on the runtime this build recommends, EVERY turn is a turn whose
/// model nobody can name. <c>U-meter</c> answered that with "unpriced", which is honest about the
/// arithmetic and dishonest about the consequence: an unpriced turn cannot reach the daily cap, so
/// the ceiling the owner set held nothing back on the ordinary installation.
///
/// The answer here is to charge the DEAREST model in that runtime's own catalogue and say so in
/// those words. The direction matters and is the whole design: over-charging an unidentified turn
/// can only stop the AI early, which is recoverable at midnight or with the owner's own two numbers
/// on the Safety page; under-charging it lets the cap be walked past, which is not.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class UnknownModelIsPricedHighTests : IDisposable
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

    /// <summary>The usage Codex actually reported on this Mac — and it names no model.</summary>
    static AgentTurnEnded CodexTurn(DateTimeOffset at) =>
        new(0, TimeSpan.FromSeconds(12.5), "…", at) { Usage = new TurnUsage(17232, 12928, 0, 6, 0, null) };

    /// <summary>
    /// The dearest entry in <c>codex</c>'s shipped catalogue is <c>gpt-6-astra</c> at $10.00 input,
    /// $1.00 cached and $50.00 output per million, read from OpenAI's pricing page on 2026-09-06.
    /// 4,304 uncached input + 12,928 cached + 6 output, the real measured turn.
    /// </summary>
    const decimal HighestOnCodex = (4304m * 10.00m + 12928m * 1.00m + 6m * 50.00m) / 1_000_000m;

    [Fact]
    public void A_turn_whose_model_the_stream_never_named_is_charged_at_the_highest_list_price()
    {
        NoCosts();
        var price = CostCatalog.Price(new TurnUsage(17232, 12928, 0, 6, 0, null), "codex");

        Assert.Null(price.Unpriced);
        Assert.Equal(HighestOnCodex, price.Cost);
    }

    /// <summary>
    /// THE POINT OF THE WHOLE ITEM: the cap bites out of the box. One measured Codex turn is dearer
    /// than a cap of five cents, and the loop stops on it.
    /// </summary>
    [Fact]
    public void The_daily_cap_is_reached_by_turns_whose_model_was_never_named()
    {
        NoCosts();
        var meter = new TurnMeter(_db, () => 0.05m, runtimeId: () => "codex", recordPath: _records);

        Assert.False(meter.Today.CapReached);
        meter.Record(CodexTurn(DateTimeOffset.Now));

        Assert.Equal(HighestOnCodex, meter.Today.Spent);
        Assert.Equal(0, meter.Today.UnpricedTurns);
        Assert.True(meter.Today.CapReached);
    }

    /// <summary>
    /// AND IT SAYS SO, IN THOSE WORDS, EVERYWHERE THE FIGURE GOES. A high estimate that is not
    /// labelled is worse than an unpriced turn: it looks like a measurement, so nobody corrects it.
    /// </summary>
    [Fact]
    public void The_estimate_is_labelled_on_the_price_on_the_line_and_on_the_days_total()
    {
        NoCosts();
        Assert.Equal(Labels.PricedAtHighestListPrice,
            CostCatalog.Price(new TurnUsage(17232, 12928, 0, 6, 0, null), "codex").Estimated);

        var meter = new TurnMeter(_db, () => 5m, runtimeId: () => "codex", recordPath: _records);
        meter.Record(CodexTurn(DateTimeOffset.Now));

        var record = Json.Read<TurnRecord>(File.ReadAllLines(_records)[0])!;
        Assert.Equal(Labels.PricedAtHighestListPrice, record.Estimated);
        Assert.Null(record.Unpriced);
        Assert.Equal(HighestOnCodex, record.Cost);

        Assert.Equal(1, meter.Today.EstimatedTurns);
        Assert.Equal(Labels.PricedAtHighestListPrice, meter.Today.Estimated);
        Assert.True(meter.Today.CanPrice);
    }

    /// <summary>
    /// A figure priced against a model something actually NAMED is not labelled an estimate, and the
    /// day it belongs to is not either. Without this the label would be decoration rather than a
    /// distinction, and the owner would have no way to tell the two apart on the card.
    /// </summary>
    [Fact]
    public void A_turn_whose_model_was_named_is_not_labelled_an_estimate()
    {
        NoCosts();
        var price = CostCatalog.Price(new TurnUsage(17232, 12928, 0, 6, 0, "gpt-5.6-luna"), "codex");
        Assert.Null(price.Estimated);
        Assert.NotNull(price.Cost);

        var meter = new TurnMeter(_db, () => 5m, runtimeId: () => "custom", recordPath: _records);
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "…", DateTimeOffset.Now)
        {
            Usage = new TurnUsage(10, 0, 0, 10, 0, "gpt-5.6-luna")
        });

        Assert.Equal(0, meter.Today.EstimatedTurns);
        Assert.Null(meter.Today.Estimated);
    }

    /// <summary>
    /// The estimate never reaches for a model in ANOTHER runtime's catalogue. <c>opencode</c> carries
    /// the two $30/$180 pro models — Codex's own model page does not list them, so Codex cannot be
    /// set to one — and charging a Codex turn at a rate Codex cannot incur would be a fabricated
    /// bill, not a conservative one.
    /// </summary>
    [Fact]
    public void The_estimate_is_taken_from_that_runtimes_catalogue_and_not_from_another()
    {
        NoCosts();
        var usage = new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, null);

        Assert.Equal(10.00m + 50.00m, CostCatalog.Price(usage, "codex").Cost);
        Assert.Equal(30.00m + 180.00m, CostCatalog.Price(usage, "opencode").Cost);
    }
}


/// <summary>
/// THE OWNER'S OWN RATE, TYPED IN A WINDOW, IS THE LAST WORD.
///
/// The two numbers on the Safety page exist because of who knows what: only the owner can see the
/// bill, and neither this build nor the CLI can tell them which model was selected. Everything else
/// in this unit is TradeAgent quoting a vendor's page at them; this is them correcting it, and it
/// therefore beats the shipped list price AND <c>costs.json</c>, for every turn, named model or not.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OwnerPriceTests : IDisposable
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

    static TradeAgentSettings Priced(decimal? input, decimal? output) =>
        new() { AiPriceInputPerMillion = input, AiPriceOutputPerMillion = output };

    [Fact]
    public void The_owners_rate_beats_the_shipped_list_price_and_the_costs_file_alike()
    {
        File.WriteAllText(CostCatalog.OverridePath, Json.Write(new AgentCosts
        {
            Currency = "USD",
            Models = [new ModelPrice
            {
                Runtime = "codex", Model = "gpt-5.3-codex",
                InputPerMillion = 99m, OutputPerMillion = 99m
            }]
        }, pretty: true));

        var owner = OwnerPrice.From(Priced(2m, 8m));
        var usage = new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, "gpt-5.3-codex");

        var price = CostCatalog.Price(usage, "codex", owner: owner);
        Assert.Equal(2m + 8m, price.Cost);
        Assert.True(price.ByOwner);
        Assert.Null(price.Estimated);
        Assert.Null(price.Unpriced);
    }

    /// <summary>
    /// AND IT PRICES THE TURN CODEX NEVER IDENTIFIES — which is the ordinary turn, and the reason
    /// two numbers were the right control rather than a model name. An owner cannot be asked which
    /// model their CLI chose; they can be asked what they are charged.
    /// </summary>
    [Fact]
    public void The_owners_rate_prices_a_turn_nothing_named_a_model_for_and_is_not_an_estimate()
    {
        NoCosts();
        var meter = new TurnMeter(_db, () => 5m, runtimeId: () => "codex", recordPath: _records,
            owner: () => OwnerPrice.From(Priced(1m, 4m)));

        // The measured Codex turn: 17,232 input (12,928 of it cached) and 6 output, no model.
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(12.5), "…", DateTimeOffset.Now)
        {
            Usage = new TurnUsage(17232, 12928, 0, 6, 0, null)
        });

        // Cached input at the FULL input rate: the owner gave a rate for input and no discount for
        // cache reads, and inventing one would under-charge every turn.
        Assert.Equal((17232m * 1m + 6m * 4m) / 1_000_000m, meter.Today.Spent);

        var record = Json.Read<TurnRecord>(File.ReadAllLines(_records)[0])!;
        Assert.True(record.PricedByOwner);
        Assert.Null(record.Estimated);

        Assert.True(meter.Today.PricedByOwner);
        Assert.Null(meter.Today.Estimated);
        Assert.Equal(0, meter.Today.EstimatedTurns);
    }

    /// <summary>
    /// HALF A PRICE IS NOT A PRICE. Applying one number to a whole turn would under-charge it, which
    /// is the one direction that lets the daily limit be walked past, so the pair is all or nothing
    /// and the dearer list price stands until both are set.
    /// </summary>
    [Fact]
    public void One_of_the_two_numbers_on_its_own_is_not_a_price()
    {
        Assert.Null(OwnerPrice.From(Priced(2m, null)));
        Assert.Null(OwnerPrice.From(Priced(null, 8m)));
        Assert.Null(OwnerPrice.From(Priced(null, null)));
        Assert.NotNull(OwnerPrice.From(Priced(2m, 8m)));
    }

    /// <summary>
    /// AND A ZERO IS NOT A CHEAP PRICE, IT IS THE ABSENCE OF ONE. Every turn would cost nothing, the
    /// day's total would never move, and the daily cap — the whole of what bounds an AI working
    /// non-stop — would stop existing while the card went on reading as though it were in force.
    ///
    /// It is reachable rather than theoretical: the Safety page's boxes open on the rate in force,
    /// which is 0 on a runtime this build ships no list price for, and nothing about pressing Save
    /// on an untouched pair of boxes asks twice. So the pair is refused here and the list price —
    /// or the honest "unpriced" — stands instead.
    /// </summary>
    [Fact]
    public void A_zero_is_not_a_price_and_leaves_the_list_price_standing()
    {
        Assert.Null(OwnerPrice.From(Priced(0m, 0m)));
        Assert.Null(OwnerPrice.From(Priced(0m, 8m)));
        Assert.Null(OwnerPrice.From(Priced(2m, 0m)));

        NoCosts();
        var meter = new TurnMeter(_db, () => 5m, runtimeId: () => "codex", recordPath: _records,
            owner: () => OwnerPrice.From(Priced(0m, 0m)));

        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "…", DateTimeOffset.Now)
        {
            Usage = new TurnUsage(1_000_000, 0, 0, 1_000_000, 0, null)
        });

        // The dearest entry in the codex catalogue, not nothing: gpt-6-astra at 10.00 in, 50.00 out.
        Assert.Equal(10.00m + 50.00m, meter.Today.Spent);
        Assert.False(meter.Today.PricedByOwner);
        Assert.Equal(Labels.PricedAtHighestListPrice, meter.Today.Estimated);
    }

    /// <summary>
    /// A settings row nobody could read has no owner price, so the LIST price stands — which is the
    /// dearer reading and therefore the restrictive one, the rule every other field on
    /// <see cref="TradeAgentSettings.Unreadable"/> keeps.
    /// </summary>
    [Fact]
    public void A_settings_row_that_could_not_be_read_carries_no_owner_price()
    {
        Assert.Null(OwnerPrice.From(TradeAgentSettings.Unreadable()));
        Assert.Null(OwnerPrice.From(new TradeAgentSettings()));
    }
}
