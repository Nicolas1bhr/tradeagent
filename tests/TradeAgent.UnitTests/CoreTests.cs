using System.Runtime.CompilerServices;
using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Unit;

public class OrderStateMachineTests
{
    [Fact]
    public void Unknown_can_never_go_back_on_the_wire()
    {
        // The single most important rule in the product: a naive retry would do exactly this.
        Assert.False(OrderStateMachine.CanTransition(ExecutionState.UNKNOWN, ExecutionState.DISPATCHING));
        Assert.True(OrderStateMachine.CanTransition(ExecutionState.UNKNOWN, ExecutionState.RECONCILING));
    }

    [Fact]
    public void Every_state_is_described()
    {
        foreach (var s in Enum.GetValues<ExecutionState>())
            _ = OrderStateMachine.IsTerminal(s); // throws if a state was added without a transition set
    }

    [Fact]
    public void Terminal_states_are_terminal()
    {
        foreach (var s in new[] { ExecutionState.FILLED, ExecutionState.CANCELLED, ExecutionState.REJECTED })
        {
            Assert.True(OrderStateMachine.IsTerminal(s));
            foreach (var to in Enum.GetValues<ExecutionState>())
                Assert.False(OrderStateMachine.CanTransition(s, to));
        }
    }

    [Fact]
    public void Dispatching_may_end_in_unknown_but_not_in_nothing()
    {
        Assert.True(OrderStateMachine.CanTransition(ExecutionState.DISPATCHING, ExecutionState.UNKNOWN));
        Assert.True(OrderStateMachine.CanTransition(ExecutionState.DISPATCHING, ExecutionState.REJECTED));
        Assert.False(OrderStateMachine.CanTransition(ExecutionState.DISPATCHING, ExecutionState.CREATED));
    }

    [Fact]
    public void Require_throws_on_an_illegal_move()
    {
        var ex = Assert.Throws<TradeAgentException>(() =>
            OrderStateMachine.Require(ExecutionState.FILLED, ExecutionState.DISPATCHING));
        Assert.Equal(ErrorCode.ILLEGAL_STATE_TRANSITION, ex.Code);
    }
}

public class ExecutionRequestStoreTests
{
    static ExecutionRequest Sample(string id) => new()
    {
        RequestId = id, ConnectorId = "fake", AccountId = "SIM-001", Instrument = "ES",
        Intent = RequestIntent.PLACE, ParametersJson = "{}", ClientOrderId = $"TA-{id}",
        CreatedAt = DateTimeOffset.UtcNow, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
    };

    [Fact]
    public void A_repeated_request_id_is_collapsed_onto_the_first_record()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);

        var (created1, r1) = store.TryCreate(Sample("dup-1"));
        var (created2, r2) = store.TryCreate(Sample("dup-1"));

        Assert.True(created1);
        Assert.False(created2);           // this false is the duplicate-order defence
        Assert.Equal(r1.ClientOrderId, r2.ClientOrderId);
        Assert.Single(store.Query("request_id='dup-1'"));
    }

    [Fact]
    public void Transition_refuses_when_the_stored_state_is_not_what_the_caller_believed()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        store.TryCreate(Sample("cas-1"));
        store.Transition("cas-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);

        // A second dispatcher that still thinks the record is CREATED must lose.
        var ex = Assert.Throws<TradeAgentException>(() =>
            store.Transition("cas-1", ExecutionState.CREATED, ExecutionState.DISPATCHING));
        Assert.Equal(ErrorCode.ILLEGAL_STATE_TRANSITION, ex.Code);
    }

    [Fact]
    public void Unknown_requests_are_findable_for_reconciliation()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        store.TryCreate(Sample("u-1"));
        store.Transition("u-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("u-1", ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, needsReconciliation: true);

        Assert.Single(store.NeedingReconciliation());
        Assert.Equal(ExecutionState.UNKNOWN, store.Get("u-1")!.State);
    }

    [Fact]
    public void Money_amounts_survive_a_round_trip_exactly()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        store.TryCreate(Sample("dec-1"));
        store.Transition("dec-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("dec-1", ExecutionState.DISPATCHING, ExecutionState.PARTIALLY_FILLED,
            filled: 0.3333m, avgPrice: 4321.1234567m);

        var r = store.Get("dec-1")!;
        Assert.Equal(0.3333m, r.FilledQuantity);
        Assert.Equal(4321.1234567m, r.AveragePrice);
    }
}

public class OnboardingTests
{
    [Fact]
    public void Progress_resumes_at_the_first_unfinished_step()
    {
        using var db = TestEnv.NewDb();
        var store = new OnboardingStore(db);
        Assert.Equal(OnboardingStep.WELCOME, store.Current());

        store.Complete(OnboardingStep.WELCOME);
        store.Complete(OnboardingStep.SYSTEM_CHECK);
        Assert.Equal(OnboardingStep.AI_RUNTIME_SELECTED, store.Current());
        Assert.False(store.IsComplete());
    }

    [Fact]
    public void Progress_survives_reopening_the_database()
    {
        var file = Path.Combine(TestEnv.Home, $"onb-{Guid.NewGuid():n}.db");
        using (var db = new Database(file)) new OnboardingStore(db).Complete(OnboardingStep.WELCOME);
        using (var db = new Database(file))
            Assert.Equal(OnboardingStep.SYSTEM_CHECK, new OnboardingStore(db).Current());
    }

    [Fact]
    public void A_failed_step_can_be_cleared_and_redone()
    {
        using var db = TestEnv.NewDb();
        var store = new OnboardingStore(db);
        store.Complete(OnboardingStep.WELCOME);
        store.Complete(OnboardingStep.SYSTEM_CHECK);
        store.Clear(OnboardingStep.SYSTEM_CHECK);
        Assert.Equal(OnboardingStep.SYSTEM_CHECK, store.Current());
    }

    [Fact]
    public void Every_step_has_wording_a_nontechnical_person_can_read()
    {
        foreach (var s in Enum.GetValues<OnboardingStep>())
        {
            var t = s.Title();
            Assert.False(string.IsNullOrWhiteSpace(t));
            Assert.NotEqual(s.ToString(), t); // not just the enum name shouted at the user
        }
    }
}

public class ErrorCatalogueTests
{
    [Fact]
    public void Every_error_code_has_a_plain_language_explanation_and_a_repair()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var info = Errors.Get(code);
            Assert.False(string.IsNullOrWhiteSpace(info.UserMessage), $"{code} has no user message");
            Assert.False(string.IsNullOrWhiteSpace(info.Repair), $"{code} has no suggested repair");
            Assert.DoesNotContain("Exception", info.UserMessage);
        }
    }
}

public class HealthTests
{
    [Fact]
    public void Execution_is_not_trustable_until_the_whole_chain_is_ready()
    {
        var h = new HealthRegistry();
        Assert.False(h.ExecutionTrustable(out var why));
        Assert.Contains("Gateway", why);

        h.Set(Components.Gateway, HealthState.READY);
        h.Set(Components.TradingConnection, HealthState.READY);
        h.Set(Components.Account, HealthState.READY);
        h.Set(Components.ExecutionCapability, HealthState.READY);
        Assert.True(h.ExecutionTrustable(out _));

        h.Set(Components.TradingConnection, HealthState.DEGRADED, "flaky");
        Assert.False(h.ExecutionTrustable(out var why2));
        Assert.Contains("flaky", why2);
    }
}

public class WorkspaceTests
{
    static WorkspaceContext Ctx(RiskPolicy? risk = null) => new(
        "Simulator", true, "SIM-001", TradingMode.PAPER, true, null, risk ?? new RiskPolicy());

    [Fact]
    public void The_agent_is_told_the_things_it_must_not_get_wrong()
    {
        var root = Path.Combine(TestEnv.Home, $"ws-{Guid.NewGuid():n}");
        WorkspaceBuilder.Build(Ctx(), root);
        var text = File.ReadAllText(Path.Combine(root, "AGENTS.md"));

        Assert.Contains("trade schema --json", text);
        Assert.Contains("request id", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does **not** mean the order failed", text);
        Assert.Contains("Broker credentials are not here", text);
        // The lost-reply instruction is only actionable if the agent knows the replay does not need
        // the platform: an agent told to re-send while the connection is down has to know that works.
        Assert.Contains("reads nothing from the platform", text);
        foreach (var d in WorkspaceBuilder.SubDirs)
            Assert.True(Directory.Exists(Path.Combine(root, d)), $"{d} was not created");
    }

    [Fact]
    public void The_current_limits_appear_in_the_instructions()
    {
        var root = Path.Combine(TestEnv.Home, $"ws-{Guid.NewGuid():n}");
        WorkspaceBuilder.Build(Ctx(new RiskPolicy { MaxOrderQuantity = 3m, MaxOpenPositions = 7 }), root);
        var text = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        Assert.Contains("**3**", text);
        Assert.Contains("**7**", text);
    }

    [Fact]
    public void An_uncapped_order_value_is_not_described_as_a_limit_of_zero()
    {
        // MaxNotionalPerOrder == 0 means "not enforced". Rendered naively it read as
        // "at most 0 order value", which tells the agent it may not trade at all.
        var root = Path.Combine(TestEnv.Home, $"ws-{Guid.NewGuid():n}");
        WorkspaceBuilder.Build(Ctx(new RiskPolicy { MaxNotionalPerOrder = 0m }), root);
        var text = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        Assert.DoesNotContain("**0** order value", text);
        Assert.Contains("not capped", text);

        var root2 = Path.Combine(TestEnv.Home, $"ws-{Guid.NewGuid():n}");
        WorkspaceBuilder.Build(Ctx(new RiskPolicy { MaxNotionalPerOrder = 7500m }), root2);
        Assert.Contains("**7,500** order value", File.ReadAllText(Path.Combine(root2, "AGENTS.md")));
    }

    [Fact]
    public void The_agent_environment_carries_no_secret()
    {
        var env = WorkspaceBuilder.EnvironmentFor("session-1", TestEnv.Home);
        var token = Security.IpcToken.Ensure();

        Assert.Contains("TRADEAGENT_SESSION", env.Keys);
        Assert.DoesNotContain(env, kv => kv.Value.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env, kv => kv.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env, kv => kv.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase));
    }
}

public class RiskPolicyTests
{
    [Fact]
    public void An_empty_allowlist_means_everything_is_allowed()
    {
        var p = new RiskPolicy();
        Assert.True(p.InstrumentAllowed("ES"));
        p.InstrumentAllowlist.Add("MES");
        Assert.False(p.InstrumentAllowed("ES"));
        Assert.True(p.InstrumentAllowed("mes"));
    }

    [Fact]
    public void Defaults_are_small_enough_to_be_survivable()
    {
        // A default that quietly allows a large position is a defect, not a convenience.
        var p = new RiskPolicy();
        Assert.True(p.MaxOrderQuantity <= 1m);
        Assert.True(p.MaxOpenPositions <= 2);
    }
}

public class ProtocolTests
{
    [Fact]
    public void Requests_round_trip_including_numbers_sent_as_text()
    {
        var json = """{"v":1,"id":"abc","op":"buy","args":{"symbol":"ES","quantity":"2.5","limit":4300.25}}""";
        var req = Json.Read<IpcRequest>(json)!;
        Assert.Equal("buy", req.Op);
        Assert.Equal("ES", req.Str("symbol"));
        Assert.Equal(2.5m, req.Dec("quantity"));
        Assert.Equal(4300.25m, req.Dec("limit"));
    }

    [Fact]
    public void Errors_travel_with_a_user_message_and_a_repair()
    {
        var r = IpcResponse.Fail("1", ErrorCode.AI_TRADING_STOPPED);
        var round = Json.Read<IpcResponse>(Json.Write(r))!;
        Assert.False(round.Ok);
        Assert.Equal("AI_TRADING_STOPPED", round.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(round.Error.UserMessage));
        Assert.False(string.IsNullOrWhiteSpace(round.Error.Repair));
    }

    [Fact]
    public void Mutating_operations_are_correctly_identified()
    {
        Assert.True(Ops.IsMutating(Ops.Buy));
        Assert.True(Ops.IsMutating(Ops.CloseAll));
        Assert.False(Ops.IsMutating(Ops.Status));
        Assert.False(Ops.IsMutating(Ops.Quote));
    }

    [Fact]
    public void A_stale_quote_knows_it_is_stale()
    {
        var fresh = new QuoteInfo("ES", 1, 2, 1.5m, null, null, DateTimeOffset.UtcNow);
        var old = new QuoteInfo("ES", 1, 2, 1.5m, null, null, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.False(fresh.IsStale(TimeSpan.FromSeconds(30)));
        Assert.True(old.IsStale(TimeSpan.FromSeconds(30)));
    }
}

public class CapabilityTests
{
    [Fact]
    public void Reconciliation_is_only_provable_with_client_ids_and_history()
    {
        Assert.True(new ConnectorCapabilities(true, true, true, true, true, true).ReconciliationProvable);
        Assert.False(new ConnectorCapabilities(true, false, true, true, true, true).ReconciliationProvable);
        Assert.False(new ConnectorCapabilities(true, true, false, true, true, true).ReconciliationProvable);
    }
}

/// <summary>
/// The refusal of fully automatic live trading is correct; learning about it from a turned-down
/// order is not. These pin the exact words the user reads, because the wording is the fix.
/// </summary>
public class DoctorReconciliationCheckTests
{
    const string Facts = "ATAS — carries TradeAgent's own order reference: ";
    const string Action =
        "Nothing is broken and there is nothing to press: this is what the trading platform reports " +
        "about itself, and some platforms only confirm the order reference after TradeAgent has " +
        "placed an order and read it back. “Watch only”, “Practice” and “Real, ask me first” all " +
        "work normally — only “Real, fully automatic” is withheld.";

    static ConnectorCapabilities Caps(bool clientOrderId, bool orderHistory) =>
        new(IsPaper: false, SupportsClientOrderId: clientOrderId, SupportsOrderHistory: orderHistory,
            SupportsModify: true, SupportsClosePosition: true, SupportsStreaming: true);

    [Fact]
    public void Both_confirmed_reads_as_ready_and_says_the_mode_is_available()
    {
        var c = Doctor.ReconciliationCheck("ATAS", Caps(true, true));

        Assert.Equal("Fully automatic trading", c.Name);
        Assert.Equal(HealthState.READY, c.State);
        Assert.Equal(Facts + "confirmed; serves order history reaching far enough back: confirmed. " +
                     "The mode “Real, fully automatic” is available.", c.Detail);
        Assert.False(c.AutoRepairable);
    }

    [Fact]
    public void No_order_reference_names_that_capability_and_leaves_the_other_confirmed()
    {
        var c = Doctor.ReconciliationCheck("ATAS", Caps(false, true));

        Assert.Equal(HealthState.DEGRADED, c.State);
        Assert.Equal(Facts + "not confirmed; serves order history reaching far enough back: confirmed. " +
                     "Both are needed to prove what happened to an order after a disconnection, so the " +
                     "mode “Real, fully automatic” is refused.", c.Detail);
        Assert.Equal(Action, c.UserAction);
        Assert.Equal(ErrorCode.AUTONOMY_REQUIRES_PROVABLE_STATE, c.Code);
    }

    [Fact]
    public void No_order_history_names_that_capability_and_leaves_the_other_confirmed()
    {
        var c = Doctor.ReconciliationCheck("ATAS", Caps(true, false));

        Assert.Equal(HealthState.DEGRADED, c.State);
        Assert.Equal(Facts + "confirmed; serves order history reaching far enough back: not confirmed. " +
                     "Both are needed to prove what happened to an order after a disconnection, so the " +
                     "mode “Real, fully automatic” is refused.", c.Detail);
        Assert.Equal(Action, c.UserAction);
    }

    [Fact]
    public void Neither_confirmed_is_still_a_warning_and_still_names_the_three_modes_that_work()
    {
        var c = Doctor.ReconciliationCheck("ATAS", Caps(false, false));

        Assert.Equal(HealthState.DEGRADED, c.State);
        Assert.Equal(Facts + "not confirmed; serves order history reaching far enough back: not confirmed. " +
                     "Both are needed to prove what happened to an order after a disconnection, so the " +
                     "mode “Real, fully automatic” is refused.", c.Detail);
        Assert.Equal(Action, c.UserAction);
        // Ui.ModeLabel's own words for OBSERVE, PAPER and LIVE_CONFIRM. A second vocabulary for the
        // same four modes is how a product starts describing itself two ways.
        Assert.Contains("“Watch only”", c.UserAction);
        Assert.Contains("“Practice”", c.UserAction);
        Assert.Contains("“Real, ask me first”", c.UserAction);
    }

    [Fact]
    public void Nothing_is_ever_a_failure_and_nothing_is_ever_offered_as_a_repair()
    {
        // The user cannot fix this by clicking. FAILED would cry wolf over an installation that
        // works in three of its four modes; AutoRepairable would promise a button that cannot exist.
        foreach (var (id, hist) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var c = Doctor.ReconciliationCheck("ATAS", Caps(id, hist));
            Assert.NotEqual(HealthState.FAILED, c.State);
            Assert.False(c.AutoRepairable);
        }
    }

    [Fact]
    public void The_wording_never_calls_the_platform_incapable()
    {
        // A false is ambiguous and cannot be resolved from ConnectorCapabilities: the ATAS adapter
        // reports SupportsClientOrderId only as "proven", and proven is false on a freshly connected
        // session until an order has been placed and its reference read back. Saying "cannot" would
        // tell that user their broker is broken when nothing has been tried yet.
        foreach (var (id, hist) in new[] { (true, false), (false, true), (false, false) })
        {
            var c = Doctor.ReconciliationCheck("ATAS", Caps(id, hist));
            var text = c.Detail + " " + c.UserAction;
            foreach (var claim in new[] { "cannot", "can't", "unable", "incapable", "does not support", "not supported" })
                Assert.False(text.Contains(claim, StringComparison.OrdinalIgnoreCase),
                    $"the wording claims the platform {claim}, which is not knowable from a boolean");
        }
    }

    [Fact]
    public void A_simulator_with_order_history_hidden_reports_it_by_name()
    {
        // Driven through the connector's own fault seam rather than a hand-made capability record,
        // so this breaks if FakeConnector ever stops reflecting HideOrderHistory into Capabilities.
        var conn = new FakeConnector(faults: new FaultProfile { HideOrderHistory = true });
        var c = Doctor.ReconciliationCheck(conn.DisplayName, conn.Capabilities);

        Assert.Equal(HealthState.DEGRADED, c.State);
        Assert.Equal("Simulator (built in) — carries TradeAgent's own order reference: confirmed; " +
                     "serves order history reaching far enough back: not confirmed. " +
                     "Both are needed to prove what happened to an order after a disconnection, so the " +
                     "mode “Real, fully automatic” is refused.", c.Detail);
    }

    [Fact]
    public async Task The_full_report_carries_the_check_and_lists_it_among_the_problems()
    {
        // The point of the whole change: "Check everything" prints report.Problems, so a backend that
        // will refuse the fully automatic mode has to appear there, before an order depends on it.
        var (gw, _, db) = await TestEnv.Ready(faults: new FaultProfile { HideOrderHistory = true });
        using var _2 = db;
        await using var _3 = gw;

        var report = await new Doctor(gw, allowNetwork: false).RunAsync();
        var check = Assert.Single(report.Checks, c => c.Name == "Fully automatic trading");

        Assert.Equal(HealthState.DEGRADED, check.State);
        Assert.Contains("serves order history reaching far enough back: not confirmed", check.Detail);
        Assert.Contains(report.Problems, p => p.Name == "Fully automatic trading");
    }

    [Fact]
    public async Task A_backend_that_confirms_both_does_not_add_a_problem()
    {
        // The other half of not crying wolf: on the default simulator this check is silent.
        var (gw, _, db) = await TestEnv.Ready();
        using var _2 = db;
        await using var _3 = gw;

        var report = await new Doctor(gw, allowNetwork: false).RunAsync();
        var check = Assert.Single(report.Checks, c => c.Name == "Fully automatic trading");

        Assert.Equal(HealthState.READY, check.State);
        Assert.DoesNotContain(report.Problems, p => p.Name == "Fully automatic trading");
    }
}

public class RuntimeCatalogTests
{
    [Fact]
    public void Built_in_runtimes_are_honest_about_being_unverified()
    {
        foreach (var m in RuntimeCatalog.BuiltIn().Where(m => m.Id != "custom"))
        {
            Assert.False(m.Verified, $"{m.Id} claims verified without a real-machine check");
            Assert.False(string.IsNullOrWhiteSpace(m.DocsUrl));
            Assert.False(string.IsNullOrWhiteSpace(m.Executable));
        }
    }

    [Fact]
    public void An_override_file_replaces_a_built_in_manifest()
    {
        var original = RuntimeCatalog.Find("opencode")!;
        try
        {
            RuntimeCatalog.SaveOverrides([new RuntimeManifest
            {
                Id = "opencode", DisplayName = "OpenCode", Executable = "oc-test",
                AuthArgs = ["login"], Verified = true
            }]);
            var loaded = RuntimeCatalog.Find("opencode")!;
            Assert.Equal("oc-test", loaded.Executable);
            Assert.True(loaded.Verified);
        }
        finally { if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath); }

        Assert.Equal(original.Executable, RuntimeCatalog.Find("opencode")!.Executable);
    }

    [Fact]
    public void A_corrupt_override_file_does_not_stop_the_app()
    {
        try
        {
            File.WriteAllText(RuntimeCatalog.OverridePath, "{ this is not json");
            Assert.NotEmpty(RuntimeCatalog.Load());
        }
        finally { if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath); }
    }
}

public class RuntimeResolutionTests
{
    static string Scratch([CallerMemberName] string name = "")
    {
        var dir = Path.Combine(TestEnv.Home, "resolve", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static CliAgentRuntime Runtime(string executable) =>
        new(new RuntimeManifest { Id = "resolve-test", DisplayName = "t", Executable = executable });

    [Fact]
    public void An_exact_name_on_PATH_is_found()
    {
        var dir = Scratch();
        var exe = Path.Combine(dir, "tool.exe");
        File.WriteAllText(exe, "");
        using var _ = new PathEntry(dir);
        Assert.Equal(exe, Runtime("tool.exe").ResolveExecutable());
    }

    [Fact]
    public void A_cmd_shim_satisfies_a_manifest_that_names_an_exe()
    {
        // npm installs its CLIs as a .cmd shim on Windows and hides the real binary in node_modules.
        // Matching only "codex.exe" reported an installed, signed-in tool as missing.
        if (!OperatingSystem.IsWindows()) return;

        var dir = Scratch();
        var shim = Path.Combine(dir, "tool.cmd");
        File.WriteAllText(shim, "@echo off");
        using var _ = new PathEntry(dir);
        Assert.Equal(shim, Runtime("tool.exe").ResolveExecutable());
    }

    [Fact]
    public void A_real_exe_wins_over_a_shim_with_the_same_stem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Scratch();
        File.WriteAllText(Path.Combine(dir, "tool.cmd"), "@echo off");
        var exe = Path.Combine(dir, "tool.exe");
        File.WriteAllText(exe, "");
        using var _ = new PathEntry(dir);
        Assert.Equal(exe, Runtime("tool.exe").ResolveExecutable());
    }

    [Fact]
    public void An_absolute_path_in_the_manifest_is_used_as_given()
    {
        var dir = Scratch();
        var exe = Path.Combine(dir, "somewhere-else.bin");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, Runtime(exe).ResolveExecutable());
        Assert.Null(Runtime(Path.Combine(dir, "absent.bin")).ResolveExecutable());
    }

    [Fact]
    public void Nothing_installed_resolves_to_nothing()
    {
        using var _ = new PathEntry(Scratch());
        Assert.Null(Runtime("definitely-not-installed.exe").ResolveExecutable());
    }

    /// <summary>Prepends a directory to PATH for the life of the test.</summary>
    sealed class PathEntry : IDisposable
    {
        readonly string? _previous = Environment.GetEnvironmentVariable("PATH");
        public PathEntry(string dir) =>
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _previous);
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", _previous);
    }
}
