using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class CliCodeModeTests
{
    // ---- (3a) the CLI class's one-call dry run --------------------------------------------------------------------------

    static async Task<(SeatVerdict Verdict, SeatHomeTests.FakeProbe Probe)> VerifyCli(RuntimeManifest codex, string exe, string model,
        ToolRun? tools = null, SeatHomeTests.FakeProbe? probe = null)
    {
        probe ??= new SeatHomeTests.FakeProbe();
        if (tools is not null) probe.Tools = tools;
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "role-home")).FullName;
        var argv = AgentArgs.For(codex, "", resuming: false, model);
        var verdict = await SeatProfile.VerifyAsync(codex, exe, argv, workspace, probe, new SeatLaunch(SeatPlan.CliClass, Model: model), default);
        return (verdict, probe);
    }

    /// <summary>
    /// (3a) A CLI SEAT WHOSE COMMAND TOOL DID NOT RUN IN THE DRY RUN IS REFUSED IN WORDS BEFORE ADMISSION — attempt 4's exec answering
    /// "code-mode host is disabled", quoted — and a refusal is never kept: the next verify asks again. Red at base: the CLI class had no
    /// dry run (<c>if (!broker)</c> returned before it), and the seat was admitted.
    /// </summary>
    [Fact]
    public async Task A_cli_seat_whose_exec_answered_code_mode_host_is_disabled_is_refused_in_words()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_cli_seat_whose_exec_answered_code_mode_host_is_disabled_is_refused_in_words));
        var dead = new ToolRun(["exec[custom]", "wait[custom]"], "exec", "code-mode host is disabled", "probe-0123456789");

        var (verdict, probe) = await VerifyCli(codex, exe, "gpt-6-luna", dead);
        Assert.True(verdict.Refused, "a CLI seat whose exec call failed closed was admitted");
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, verdict.Code);
        Assert.Contains("code-mode host is disabled", verdict.Refusal, StringComparison.Ordinal);
        Assert.Contains("gpt-6-luna", verdict.Refusal, StringComparison.Ordinal);
        Assert.Equal((SeatPlan.CliClass, "gpt-6-luna"), (probe.LastToolAsk!.SeatClass, probe.LastToolAsk.Model));
        Assert.Equal(codex.Install.ExecutableSha256, probe.LastToolAsk.ExeSha256);

        await VerifyCli(codex, exe, "gpt-6-luna", probe: probe);
        Assert.Equal(2, probe.ToolAsks);
    }

    /// <summary>(3a) A CLI SEAT OFFERED NO COMMAND TOOL, OR WHOSE CALL WAS NEVER ANSWERED, IS REFUSED IN WORDS.</summary>
    [Fact]
    public async Task A_cli_seat_offered_no_command_tool_or_never_answered_is_refused_in_words()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_cli_seat_offered_no_command_tool_or_never_answered_is_refused_in_words));

        var (none, _) = await VerifyCli(codex, exe, "gpt-6-luna", new ToolRun(["request_user_input"], null, null, "probe-x"));
        Assert.True(none.Refused);
        Assert.Contains("no tool that runs a command", none.Refusal, StringComparison.Ordinal);
        Assert.Contains("request_user_input", none.Refusal, StringComparison.Ordinal);

        var (silent, _) = await VerifyCli(codex, exe, "gpt-6-luna", new ToolRun(["exec_command"], "exec_command", null, "probe-x"));
        Assert.True(silent.Refused);
        Assert.Contains("never answered", silent.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// (3a) A PASSING DRY RUN IS KEPT BY BINARY, TEMPLATE AND MODEL: the next verify of the same seat asks nothing, another model asks
    /// again. Broker seats never ask the CLI class's dry run.
    /// </summary>
    [Fact]
    public async Task A_passing_dry_run_is_kept_by_binary_template_and_model()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_passing_dry_run_is_kept_by_binary_template_and_model));
        var probe = new SeatHomeTests.FakeProbe { Tools = new ToolRun(["exec[custom]"], "exec", "probe-0123456789", "probe-0123456789") };

        var (first, _) = await VerifyCli(codex, exe, "gpt-6-luna", probe: probe);
        Assert.False(first.Refused, first.Refusal);
        var (again, _) = await VerifyCli(codex, exe, "gpt-6-luna", probe: probe);
        Assert.False(again.Refused, again.Refusal);
        Assert.Equal(1, probe.ToolAsks);

        await VerifyCli(codex, exe, "gpt-6-astra", probe: probe);
        Assert.Equal(2, probe.ToolAsks);
    }
}
