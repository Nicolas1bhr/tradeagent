using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-CLI-CODE-MODE: THE CLI SEAT CLASS GETS ITS TOOLS BACK ON A CODE-MODE-ONLY MODEL; BROKER AND CELL KEEP CODE MODE CLOSED; A SEAT
/// WHOSE TOOLS CANNOT RUN IS REFUSED IN WORDS (<c>fleet/tmp/M4-regression/report.md</c>). Attempt 4's legacy pair ran gpt-6-luna — like
/// every model on the owner's account, <c>code_mode_only</c> — under the CLI class's <c>--disable code_mode_host</c>, so every
/// <c>exec</c> call failed closed with "code-mode host is disabled", the turns exited 0, and the turn reader counted codex's warning
/// as a command.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public partial class CliCodeModeTests
{
    /// <summary>The vendor's own warning at rust-v0.160.1 (core/src/tools/code_mode/mod.rs:108-121), with the error attempt 4 met.</summary>
    internal const string Warning =
        "Code Mode is unavailable because code-mode host is disabled. Code mode will fail closed; enable `features.code_mode_host` " +
        "and install `codex-code-mode-host`.";

    /// <summary>
    /// ONE OF ATTEMPT 4'S TURNS AS <c>codex exec --json</c> PRINTS IT: the warning as an <c>error</c> item
    /// (exec/src/event_processor_with_jsonl_output.rs:409-419), then the model's own account, then usage — exit 0.
    /// </summary>
    internal const string DeadTurn =
        """
        {"type":"thread.started","thread_id":"00000000-0000-0000-0000-000000000004"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"error","message":"Code Mode is unavailable because code-mode host is disabled. Code mode will fail closed; enable `features.code_mode_host` and install `codex-code-mode-host`."}}
        {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"I couldn't access the workspace: the command tool returned \"code-mode host is disabled.\""}}
        {"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":0,"output_tokens":50}}
        """;

    static bool Pair(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == flag && args[i + 1] == value) return true;
        return false;
    }

    // ---- (A) the flag moved -------------------------------------------------------------------------------------------

    /// <summary>
    /// (A) THE CLI CLASS RUNS CODE MODE'S HOST; BROKER AND CELL KEEP IT DISABLED. The CLI class's argv carries no adjacent
    /// <c>--disable code_mode_host</c>, the seat's config file no <c>features.code_mode_host = false</c>, and its feature allowlist holds
    /// <c>code_mode_host</c>; the broker's and the cell's argv carry the pair, and their allowlists refuse the host reported on.
    /// </summary>
    [Fact]
    public void The_cli_class_runs_the_code_mode_host_and_broker_and_cell_keep_it_disabled()
    {
        var seat = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex").Seat!;

        Assert.False(Pair(seat.ArgsFor(SeatPlan.CliClass), "--disable", "code_mode_host"),
            "the CLI class still disables codex's code-mode host: on a code-mode-only model every exec call fails closed");
        Assert.DoesNotContain("features.code_mode_host = false", System.Text.Encoding.UTF8.GetString(SeatProfile.ConfigBytes(seat)),
            StringComparison.Ordinal);
        Assert.Contains("code_mode_host", seat.FeaturesFor(SeatPlan.CliClass));

        foreach (var verified in new[] { SeatPlan.BrokerClass, SeatPlan.CellClass })
        {
            Assert.True(Pair(seat.ArgsFor(verified), "--disable", "code_mode_host"),
                $"the {verified} class lost --disable code_mode_host: model-written JavaScript would run beside the sign-in (Law 2)");
            Assert.DoesNotContain("code_mode_host", seat.FeaturesFor(verified));
        }
    }
}
