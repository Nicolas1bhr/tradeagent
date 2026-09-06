using System.Runtime.CompilerServices;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

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
