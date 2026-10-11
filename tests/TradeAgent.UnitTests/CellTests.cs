using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CELL POLICY IS AN APP-OWNED FILE NAMED BY ITS HASH, AND A POLICY THAT WOULD GRANT THE APP'S OWN HOME IS REFUSED
/// (<c>U-cell-linux-a</c> item 1). Every OS: nothing here confines anything; it is the policy's form and its refusals.
/// </summary>
public class CellPolicyTests
{
    static string Dir(string name)
    {
        var d = Path.Combine(TestEnv.Home, "cellpolicy-" + name + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static CellPolicy Policy(string workspace, params string[] readOnly) =>
        new(workspace, readOnly, CellPolicy.SystemRoots, [new("PATH", "/usr/bin:/bin"), new("HOME", workspace)], [8443]);

    [Fact]
    public void A_policy_file_is_named_by_its_hash_and_reads_back_whole()
    {
        var ws = Dir("ws");
        var policy = Policy(ws);
        var path = policy.Write(Dir("cells"));

        Assert.Equal(policy.Hash + ".cell.json", Path.GetFileName(path));
        var back = CellPolicy.Read(path);
        Assert.Equal(policy.Hash, back.Hash);
        Assert.Equal(ws, back.Workspace);
        Assert.Equal(["HOME", "PATH"], back.Environment.Select(e => e.Key));
        Assert.Equal([8443], back.TcpPorts);
    }

    [Fact]
    public void A_policy_file_changed_after_it_was_written_is_refused()
    {
        var path = Policy(Dir("ws")).Write(Dir("cells"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("8443", "8444"));

        var ex = Assert.Throws<TradeAgentException>(() => CellPolicy.Read(path));
        Assert.Equal(ErrorCode.CELL_UNAVAILABLE, ex.Code);
        Assert.Contains("does not hash to its name", ex.Message);
    }

    [Fact]
    public void A_policy_that_grants_the_app_home_or_its_state_is_refused()
    {
        var ws = Dir("ws");
        Assert.Null(Policy(ws).Problem());

        Assert.Contains("TradeAgent's own home", Policy(ws, Path.GetDirectoryName(Paths.Home)!).Problem());
        Assert.Contains("TradeAgent's own home", Policy(ws, "/").Problem());
        Assert.Contains("state", Policy(ws, Paths.State).Problem());
        Assert.Contains("state", Policy(ws, Path.Combine(Paths.State, "data")).Problem());
        Assert.Contains("not an absolute path", Policy(ws, "relative/in").Problem());
        Assert.Throws<TradeAgentException>(() => Policy(Paths.Home).Write(Dir("cells")));
    }
}
