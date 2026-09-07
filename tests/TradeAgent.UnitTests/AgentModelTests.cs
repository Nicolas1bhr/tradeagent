using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHICH MODEL THE AI RUNS ON IS TRADEAGENT'S CHOICE, AND IT REACHES THE COMMAND LINE OF EVERY TURN.
///
/// Before this the argv was <c>codex exec --json … "&lt;prompt&gt;"</c> with no model on it at all, so
/// the model came from <c>~/.codex/config.toml</c> — a file the owner has never opened and the
/// product never mentions. Measured on 2026-09-07 that file said <c>gpt-6-astra</c>, and the mission
/// loop's first run on a screen spent about 1.5 USD a turn on a model nobody chose.
///
/// The argv shape is asserted rather than merely "contains -m", because the position is the rule:
/// every CLI here takes the first non-flag word after the subcommand as the prompt, so a model flag
/// that landed after it would be part of the prompt instead. <see cref="AgentArgs.For"/> is the one
/// method <c>AgentSession.RunTurnAsync</c> builds a turn's arguments with; there is no second path.
///
/// The flag itself was measured, not read from documentation. codex-cli 0.153.4 on this Mac,
/// 2026-09-07, in a scratch directory, with the machine's own config naming a different model:
/// <c>codex exec --json --skip-git-repo-check -s read-only -m gpt-5.6-sol "…"</c> exited 0, and
/// <c>codex exec resume --last --json --skip-git-repo-check -m gpt-5.6-sol "…"</c> exited 0.
/// </summary>
public class AgentModelTests
{
    static RuntimeManifest Codex() => RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");

    /// <summary>
    /// THE GUARD. Both turns carry the model, in the same place, and the resumed one is the half
    /// that matters: a fresh CLI process reading a transcript does not remember which model wrote
    /// it, and the loop starts one new session per twenty turns — so a build that flagged only the
    /// first turn would run one turn on TradeAgent's choice and nineteen on the config file's.
    /// </summary>
    [Fact]
    public void The_codex_argv_names_the_model_on_the_first_turn_and_on_every_resume()
    {
        var codex = Codex();

        Assert.Equal(
            ["exec", "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox",
             "-m", "gpt-5.6-sol", "PROMPT"],
            AgentArgs.For(codex, "PROMPT", resuming: false, model: "gpt-5.6-sol"));

        Assert.Equal(
            ["exec", "resume", "--last", "--json", "--skip-git-repo-check",
             "--dangerously-bypass-approvals-and-sandbox", "-m", "gpt-5.6-sol", "PROMPT"],
            AgentArgs.For(codex, "PROMPT", resuming: true, model: "gpt-5.6-sol"));
    }

    /// <summary>
    /// The default is the manifest's, the owner's choice beats it, and a runtime with no model flag
    /// answers null however loudly it is asked — which is what keeps the dearest-model estimate
    /// alive for exactly the runtimes whose model TradeAgent cannot choose.
    /// </summary>
    [Fact]
    public void The_model_asked_for_is_the_owners_choice_then_the_manifests_default_then_none()
    {
        var codex = Codex();
        Assert.Equal("gpt-5.6-sol", codex.ModelFor(null));
        Assert.Equal("gpt-5.6-sol", codex.ModelFor(""));
        Assert.Equal("gpt-5.6-luna", codex.ModelFor("gpt-5.6-luna"));

        var noFlag = new RuntimeManifest { Id = "custom", DefaultModel = "does-not-matter" };
        Assert.Null(noFlag.ModelFor(null));
        Assert.Null(noFlag.ModelFor("gpt-5.6-sol"));
    }

    /// <summary>
    /// A runtime with no <c>ModelArgs</c> gets no model on its command line, and neither does one
    /// nobody named a model for. An empty <c>-m</c> is a CLI that will not start, which is a worse
    /// failure than a model TradeAgent did not choose.
    /// </summary>
    [Fact]
    public void A_runtime_with_no_model_flag_is_given_no_model_on_its_command_line()
    {
        var opencode = RuntimeCatalog.BuiltIn().Single(m => m.Id == "opencode");
        Assert.Empty(opencode.ModelArgs);
        Assert.DoesNotContain("gpt-5.6-sol", AgentArgs.For(opencode, "PROMPT", resuming: false, model: "gpt-5.6-sol"));

        Assert.DoesNotContain("-m", AgentArgs.For(Codex(), "PROMPT", resuming: false, model: null));
        Assert.DoesNotContain("-m", AgentArgs.For(Codex(), "PROMPT", resuming: true, model: ""));
    }

    /// <summary>
    /// The setting the row on the Safety page writes. Null is "the model TradeAgent ships", which is
    /// how an installation that has never been touched still runs on a model somebody chose.
    /// </summary>
    [Fact]
    public void An_untouched_installation_still_runs_on_a_model_somebody_chose()
    {
        Assert.Null(new TradeAgentSettings().SelectedModelId);
        Assert.Equal("gpt-5.6-sol", Codex().ModelFor(new TradeAgentSettings().SelectedModelId));
    }
}
