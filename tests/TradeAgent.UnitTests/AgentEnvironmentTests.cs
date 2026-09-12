using System.Diagnostics;
using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE AGENT'S ENVIRONMENT IS A LIST OF NAMES, NOT A COPY OF THE APP'S.
///
/// <c>CoreTests.The_agent_environment_carries_no_secret</c> already checks the three entries
/// TradeAgent ADDS. That was never the question: <c>ProcessStartInfo.Environment</c> begins as a copy
/// of this process's own environment, so the entries the app adds are the only ones anybody had
/// looked at, and everything TradeAgent itself was started with went to the AI unexamined.
/// </summary>
public class AgentEnvironmentTests
{
    /// <summary>
    /// A name chosen so it is obvious in a diff and trips no scanner: the point is that ANY name the
    /// app inherited is dropped, not that this build recognises the dangerous ones.
    /// </summary>
    const string Exported = "TA_TEST_EXPORTED_CREDENTIAL";

    [Fact]
    public void A_credential_exported_in_the_apps_environment_does_not_reach_the_agent()
    {
        Environment.SetEnvironmentVariable(Exported, "only-the-app-was-given-this");
        try
        {
            var psi = new ProcessStartInfo("/bin/true");
            AgentEnvironment.Apply(psi, WorkspaceBuilder.EnvironmentFor("session-1", TestEnv.Home));

            Assert.False(psi.Environment.ContainsKey(Exported),
                $"{Exported} was exported in TradeAgent's own environment and reached the agent " +
                "process; the child inherits the app's environment unless the whitelist replaces it");
        }
        finally { Environment.SetEnvironmentVariable(Exported, null); }
    }

    [Fact]
    public void The_whitelist_still_carries_what_the_agent_cannot_work_without()
    {
        var psi = new ProcessStartInfo("/bin/true");
        AgentEnvironment.Apply(psi, WorkspaceBuilder.EnvironmentFor("session-2", TestEnv.Home));

        // The trade command's folder, TradeAgent's own variables, and the pointer at THIS install —
        // a whitelist that dropped the last one would send the agent's `trade` to another install.
        Assert.Contains("PATH", psi.Environment.Keys);
        Assert.Contains("TRADEAGENT_SESSION", psi.Environment.Keys);
        Assert.Contains("TRADEAGENT_WORKSPACE", psi.Environment.Keys);
        Assert.Contains("TRADEAGENT_HOME", psi.Environment.Keys);
        Assert.Equal(TestEnv.Home, psi.Environment["TRADEAGENT_HOME"]);
        Assert.Contains(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", psi.Environment.Keys);
    }

    [Fact]
    public void A_vendors_own_variable_crosses_only_because_its_manifest_names_it()
    {
        const string vendor = "TA_TEST_VENDOR_HOME";
        Environment.SetEnvironmentVariable(vendor, "/somewhere/the/cli/keeps/its/state");
        try
        {
            var without = new ProcessStartInfo("/bin/true");
            AgentEnvironment.Apply(without, WorkspaceBuilder.EnvironmentFor("s", TestEnv.Home));
            Assert.False(without.Environment.ContainsKey(vendor));

            var with = new ProcessStartInfo("/bin/true");
            AgentEnvironment.Apply(with, WorkspaceBuilder.EnvironmentFor("s", TestEnv.Home), [vendor]);
            Assert.Equal("/somewhere/the/cli/keeps/its/state", with.Environment[vendor]);
        }
        finally { Environment.SetEnvironmentVariable(vendor, null); }
    }
}
