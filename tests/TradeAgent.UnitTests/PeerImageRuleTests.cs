using TradeAgent.Core;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHO MAY PRESENT A LAUNCH GRANT, as a rule rather than as a kernel call.
///
/// Separated from the call for the reason the ATAS bridge separates its own peer rule: the call runs
/// only on Windows, the rule decides, and a rule that can only be exercised on one platform is a rule
/// nobody exercises. The call is checked by <c>LaunchGrantTests</c> against a stand-in and by the
/// hosted Windows runner against the real thing.
/// </summary>
public class PeerImageRuleTests
{
    static PeerRule Rule(string? expected = "/app/bin/trade", string? hash = "abc") =>
        new(expected, hash, "/app/tools", "/app/workspace");

    [Fact]
    public void The_app_s_own_trade_command_with_the_hash_it_deployed_may()
    {
        Assert.Null(PeerImage.Verdict("/app/bin/trade", "abc", Rule()));
    }

    [Fact]
    public void A_peer_the_kernel_would_not_name_may_not()
    {
        var why = PeerImage.Verdict(null, null, Rule());
        Assert.Contains("could not be identified", why);
    }

    [Fact]
    public void A_program_in_the_managed_runtime_folder_may_not_even_if_the_record_names_it()
    {
        // The record naming it is the case worth pinning: a runtime that had rewritten TradeAgent's
        // own record to point at itself would pass "you are what we recorded" and must still fail.
        var why = PeerImage.Verdict("/app/tools/codex/codex", "abc",
            Rule(expected: "/app/tools/codex/codex"));
        Assert.Contains("AI-runtime folder", why);
    }

    [Fact]
    public void A_copy_inside_the_agents_workspace_may_not_even_when_the_bytes_match()
    {
        var why = PeerImage.Verdict("/app/workspace/agent/trade", "abc", Rule());
        // The WORKSPACE clause, named: the bare word appears in the path too, so an assertion on
        // "workspace" alone passes when the rule that actually refused was the path one.
        Assert.Contains("inside the agent's own workspace", why);
    }

    [Fact]
    public void A_program_somewhere_else_entirely_may_not()
    {
        var why = PeerImage.Verdict("/usr/local/bin/trade", "abc", Rule());
        Assert.Contains("TradeAgent's own", why);
    }

    [Fact]
    public void The_right_path_with_the_wrong_bytes_may_not()
    {
        var why = PeerImage.Verdict("/app/bin/trade", "0000", Rule());
        Assert.Contains("not the trade command TradeAgent installed", why);
    }

    [Fact]
    public void An_unrecorded_install_is_refused_rather_than_waved_through()
    {
        Assert.Contains("did not record", PeerImage.Verdict("/app/bin/trade", "abc", Rule(expected: null)));
        Assert.Contains("did not record", PeerImage.Verdict("/app/bin/trade", "abc", Rule(hash: null)));
    }

    /// <summary>
    /// The rule the product actually runs with, rather than the fixture above: the managed folders
    /// come from <see cref="Paths"/>, so a future rename cannot leave this rule pointing at a folder
    /// that no longer exists while still reading as configured.
    /// </summary>
    [Fact]
    public void The_products_own_rule_names_the_two_folders_a_peer_may_never_run_from()
    {
        var rule = AgentRuntime.Containment.PeerRuleNow();
        Assert.Equal(Paths.Tools, rule.ToolsDir);
        Assert.Equal(Paths.Workspace, rule.WorkspaceDir);
        Assert.Equal(Path.Combine(Paths.Bin, AgentRuntime.ToolDeployer.TradeCliName), rule.ExpectedPath);
    }
}

/// <summary>
/// A LAUNCH GRANT IS ONE PROCESS'S, FOR ONE TURN.
///
/// The register itself, away from the pipe: what it issues, what it refuses, and what "the turn
/// ended" does to a grant that is still in somebody's environment.
/// </summary>
public class LaunchGrantRegisterTests
{
    [Fact]
    public void A_token_this_register_never_issued_is_unknown()
    {
        var grants = new AgentGrants();
        Assert.Equal(GrantState.Unknown, grants.Verify("ff00").State);
    }

    [Fact]
    public void No_token_at_all_is_absent_rather_than_refused()
    {
        // Absent is a caller with no role, which the pipe serves and then refuses anything that
        // moves money. It is deliberately not the same answer as a bad token.
        var grants = new AgentGrants();
        Assert.Equal(GrantState.Absent, grants.Verify(null).State);
        Assert.Equal(GrantState.Absent, grants.Verify("").State);
    }

    [Fact]
    public void A_grant_names_the_role_and_the_attempt_it_was_minted_for()
    {
        var grants = new AgentGrants();
        using var g = grants.Issue(CouncilRoles.Research, "turn-20260912-abc");
        var v = grants.Verify(g.Token);

        Assert.Equal(GrantState.Valid, v.State);
        Assert.Equal(CouncilRoles.Research, v.Grant!.Role);
        Assert.Equal("turn-20260912-abc", v.Grant.AttemptId);
    }

    [Fact]
    public void The_end_of_the_turn_leaves_only_the_grace()
    {
        var now = DateTimeOffset.UtcNow;
        var grants = new AgentGrants(() => now);
        var g = grants.Issue(CouncilRoles.Operations, "turn-1");

        g.Dispose();                                    // the turn ended
        Assert.Equal(GrantState.Valid, grants.Verify(g.Token).State);   // a call already in flight

        now += AgentGrants.Grace + TimeSpan.FromSeconds(1);
        // Expired rather than Unknown while the register still holds the row: the peer already has
        // this token, so naming what is wrong with it costs nothing and saves whoever owns that peer
        // from hunting a credential fault. Either way it is refused.
        Assert.Equal(GrantState.Expired, grants.Verify(g.Token).State);
    }

    [Fact]
    public void One_roles_token_is_not_another_roles()
    {
        var grants = new AgentGrants();
        using var chair = grants.Issue(CouncilRoles.Operations, "a");
        using var research = grants.Issue(CouncilRoles.Research, "b");

        Assert.Equal(CouncilRoles.Operations, grants.Verify(chair.Token).Grant!.Role);
        Assert.Equal(CouncilRoles.Research, grants.Verify(research.Token).Grant!.Role);
        Assert.NotEqual(chair.Token, research.Token);
    }

    /// <summary>
    /// The rule the gateway reads. Here rather than only over the pipe because it is the sentence
    /// the doctrine states — Research gets no order permission — and a caller that proved no role is
    /// not the chair by default.
    /// </summary>
    [Fact]
    public void Only_the_chairs_role_may_move_money()
    {
        Assert.True(CouncilRoles.MayPlaceOrders(CouncilRoles.Operations));
        Assert.False(CouncilRoles.MayPlaceOrders(CouncilRoles.Research));
        Assert.False(CouncilRoles.MayPlaceOrders(null));
        Assert.False(CouncilRoles.MayPlaceOrders(""));
        Assert.False(CouncilRoles.MayPlaceOrders("something-nobody-defined"));
    }
}
