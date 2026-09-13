using Avalonia.Controls;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 4 — ONE ROLE ON THE HARNESS, THE OWNER'S CHOICE, AND A KEY THAT IS NEVER WRITTEN DOWN.
///
/// <para><b>The rule, and why it is conditional.</b> A role on the harness with no key starts nothing at
/// all, so defaulting Research onto it unconditionally would silence the Research Director on a machine
/// that has never been given a key — worse than running it on the vendor CLI. So the default follows the
/// key: held, and Research is on the harness; not held, and it is on the AI tool the owner chose during
/// setup. Either way the owner's own explicit choice beats the default in both directions.</para>
///
/// <para><b>And the key is not on disk.</b> Round 4 of <c>docs/COUNCIL.md</c> says keys "are not
/// retained beside an unsandboxed CLI process until containment lands"; <c>U-containment</c> landed and
/// left the deciding half open in its own words — the agent is the SAME OS USER and
/// <c>Containment.Sandbox()</c> answers <c>NONE</c> on every platform this builds on. A key under
/// <c>%LOCALAPPDATA%\TradeAgent</c> is a key the vendor CLI's own process can read. The cost is real and
/// is stated beside the box: the owner pastes it again after a restart.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HarnessRoleTests : IDisposable
{
    readonly HarnessKey _key = new();

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
    }

    const string Harness = ApiAgentRuntime.RuntimeId;

    // ---- which runtime a role is on ---------------------------------------------------------------

    /// <summary>
    /// THE GUARD. Research moves onto the harness the moment a key is held, and the chair does not move
    /// at all — its conversation IS the Chat page's, and the chair on the harness is its own unit.
    ///
    /// Take the <c>keyHeld</c> arm out of <c>AppHost.RuntimeForRole</c> and this goes red: Research is
    /// left on the vendor CLI with a key held and a harness the owner cannot reach.
    /// </summary>
    [Fact]
    public void Research_runs_on_the_harness_once_a_key_is_held_and_the_chair_never_does()
    {
        Assert.Equal(Harness, AppHost.RuntimeForRole(
            CouncilRoles.Research, chosenForRole: null, appWide: "codex", keyHeld: true));

        Assert.Equal("codex", AppHost.RuntimeForRole(
            CouncilRoles.Research, chosenForRole: null, appWide: "codex", keyHeld: false));

        // The chair stays where the owner put it, key or no key.
        foreach (var held in new[] { true, false })
            Assert.Equal("codex", AppHost.RuntimeForRole(
                CouncilRoles.Operations, chosenForRole: null, appWide: "codex", keyHeld: held));
    }

    /// <summary>
    /// THE OWNER'S OWN CHOICE BEATS THE DEFAULT IN BOTH DIRECTIONS — onto the harness with no key held
    /// (so the page can be set up before the key arrives) and back off it with one held.
    /// </summary>
    [Fact]
    public void An_explicit_choice_for_a_role_beats_the_default_either_way()
    {
        Assert.Equal(Harness, AppHost.RuntimeForRole(
            CouncilRoles.Research, chosenForRole: Harness, appWide: "codex", keyHeld: false));

        Assert.Equal("codex", AppHost.RuntimeForRole(
            CouncilRoles.Research, chosenForRole: "codex", appWide: "opencode", keyHeld: true));
    }

    /// <summary>The settings row is what carries that choice, and absent means "the app's default".</summary>
    [Fact]
    public void The_settings_row_carries_one_runtime_per_role_and_absent_means_the_default()
    {
        var settings = new TradeAgentSettings();
        Assert.Null(settings.RuntimeForRole(CouncilRoles.Research));

        settings.RoleRuntime[CouncilRoles.Research] = Harness;
        Assert.Equal(Harness, settings.RuntimeForRole(CouncilRoles.Research));
        Assert.Null(settings.RuntimeForRole(CouncilRoles.Operations));

        // An empty string is not a choice: it reads as absent rather than as a runtime with no name.
        settings.RoleRuntime[CouncilRoles.Research] = "";
        Assert.Null(settings.RuntimeForRole(CouncilRoles.Research));
    }

    /// <summary>
    /// A ROLE ON THE HARNESS WITH NO KEY STARTS NOTHING, and the owner is told which of the two facts is
    /// missing: the runtime is present (there is nothing to install) and the KEY is not held.
    /// </summary>
    [Fact]
    public async Task A_role_on_the_harness_with_no_key_starts_nothing_and_says_which_fact_is_missing()
    {
        var manifest = RuntimeCatalog.Require(Harness);
        using var runtime = new ApiAgentRuntime(manifest, _key.Read);

        Assert.False(_key.Held);
        Assert.True((await runtime.DetectAsync()).Installed);
        Assert.Equal(AuthState.NotAuthenticated, await runtime.GetAuthenticationStateAsync());
        Assert.Equal(HealthState.FAILED, await runtime.GetHealthAsync());

        var refused = await Assert.ThrowsAsync<TradeAgentException>(() => runtime.StartAsync());
        Assert.Equal(ErrorCode.AI_AUTH_REQUIRED, refused.Info.Code);
        Assert.Equal(Labels.HarnessKeyNotHeld, refused.Message);

        _key.Set("not-a-real-credential");
        Assert.Equal(AuthState.Authenticated, await runtime.GetAuthenticationStateAsync());
        Assert.Equal(HealthState.READY, await runtime.GetHealthAsync());
        await runtime.StartAsync();
    }

    // ---- the key ----------------------------------------------------------------------------------

    /// <summary>
    /// THE KEY IS HELD IN MEMORY AND WRITTEN NOWHERE. The whole managed tree is walked afterwards,
    /// because "we do not write it" is a claim about every file this app owns rather than about the one
    /// file somebody remembered not to write to.
    /// </summary>
    [Fact]
    public void A_pasted_key_is_held_for_the_session_and_appears_in_no_file_this_app_owns()
    {
        // A value no other test could have written, so finding it anywhere is proof rather than noise.
        var pasted = $"not-a-real-credential-{Guid.NewGuid():n}";
        _key.Set($"  {pasted}  ");

        Assert.True(_key.Held);
        Assert.Equal(pasted, _key.Read());

        var found = new List<string>();
        foreach (var file in Directory.GetFiles(Paths.Home, "*", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception) { continue; }          // a database mid-write is not evidence either way
            if (text.Contains(pasted, StringComparison.Ordinal)) found.Add(file);
        }
        Assert.True(found.Count == 0, "the key reached these files:\n  " + string.Join("\n  ", found));

        _key.Clear();
        Assert.False(_key.Held);
        Assert.Null(_key.Read());
    }

    /// <summary>Whitespace only is a clear, not a credential of spaces the provider would refuse.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_box_with_nothing_in_it_holds_no_key(string? typed)
    {
        _key.Set("not-a-real-credential");
        _key.Set(typed);

        Assert.False(_key.Held);
        Assert.Null(_key.Read());
    }

    /// <summary>
    /// CLEARED WHEN THE APP CLOSES. Asserted over the source of the composition root, which is where the
    /// only lifetime that matters is spelled: there is no file to delete, so "it does not outlive this
    /// process" is exactly the line in <c>DisposeAsync</c> and nothing else.
    /// </summary>
    [Fact]
    public void The_composition_root_clears_the_key_when_it_closes()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradeAgent.App", "AppHost.cs"));
        var dispose = source[source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)..];

        Assert.Contains("HarnessKey.Clear();", dispose, StringComparison.Ordinal);
        // And nothing anywhere writes it: no store, no settings field, no kv key.
        Assert.DoesNotContain("SecretStore.Write", source, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    // ---- the Safety page --------------------------------------------------------------------------

    /// <summary>
    /// THE BOX IS MASKED. The owner may be sharing a screen, and this is the one control in the product
    /// a provider credential passes through.
    /// </summary>
    [Fact]
    public void The_key_box_on_the_safety_page_is_masked()
    {
        var field = SafetyPage.BuildHarnessKeyField();

        Assert.Equal('•', field.PasswordChar);
        Assert.Equal(Labels.HarnessKey, field.PlaceholderText);
        // Empty when the page is built: a control that opened holding a credential would be one.
        Assert.True(string.IsNullOrEmpty(field.Text));
    }

    /// <summary>
    /// THE ROW OFFERS THE TWO KINDS OF THING AND NOTHING ELSE, and each button hands back exactly what
    /// the settings row stores — null for "the AI tool the owner chose", the harness id for the other.
    /// </summary>
    [Fact]
    public void The_safety_page_offers_the_ai_tool_and_the_apps_own_worker()
    {
        var chosen = new List<string?>();
        var row = SafetyPage.BuildRuntimeRow(chosen.Add);

        Assert.Equal(2, row.Children.Count);
        var cli = Assert.IsType<Button>(row.Children[0]);
        var own = Assert.IsType<Button>(row.Children[1]);
        Assert.Equal(Labels.RuntimeIsTheCli, cli.Content);
        Assert.Equal(Labels.RuntimeIsTheHarness, own.Content);

        Press(cli);
        Press(own);
        Assert.Equal([null, Harness], chosen);
    }

    static void Press(Button b) =>
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>The sentence beside the box says WHY it is not saved, which is the question it raises.</summary>
    [Fact]
    public void The_sentence_beside_the_box_says_why_the_key_is_not_saved()
    {
        Assert.Contains("memory", Labels.HarnessKeyHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never writes it to disk", Labels.HarnessKeyHint, StringComparison.Ordinal);
        Assert.Contains("Paste it again after a restart", Labels.HarnessKeyHint, StringComparison.Ordinal);
        Assert.Contains("confines", Labels.HarnessKeyHint, StringComparison.Ordinal);
    }

    // ---- the owner's report -----------------------------------------------------------------------

    /// <summary>
    /// THE DAILY REPORT'S AI-SPENDING SECTION SAYS WHETHER A KEY IS HELD, in two words and never the key.
    /// Without it a day of zero research turns has no reason beside it, and the reason is a key that does
    /// not survive a restart.
    /// </summary>
    [Theory]
    [InlineData(true, "held")]
    [InlineData(false, "not held")]
    public async Task The_reports_ai_spending_section_says_whether_a_key_is_held(bool held, string says)
    {
        var (gw, _, _) = await TestEnv.Ready();
        await using var _ = gw;

        var report = gw.Reports.Compose(Midday(), new DailyReportInputs
        {
            Runtime = Harness,
            HarnessKeyHeld = held
        });

        Assert.Equal(says, report.Spending.HarnessKey);
        Assert.Contains($"harness key: {says}", DailyReportText.Render(report), StringComparison.Ordinal);
    }

    /// <summary>A build with no harness says nothing about a key, rather than guessing "not held".</summary>
    [Fact]
    public async Task A_report_from_a_host_with_no_harness_says_nothing_about_a_key()
    {
        var (gw, _, _) = await TestEnv.Ready();
        await using var _ = gw;

        var report = gw.Reports.Compose(Midday(), new DailyReportInputs { Runtime = "codex" });

        Assert.Null(report.Spending.HarnessKey);
        Assert.DoesNotContain("harness key", DailyReportText.Render(report), StringComparison.Ordinal);
    }

    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    // ---- the price follows the role's runtime -----------------------------------------------------

    /// <summary>
    /// A ROLE'S RESERVATION IS PRICED AT THE RUNTIME IT IS ON. A price is looked up by (runtime, model),
    /// so a Research turn on the harness priced against the chair's <c>codex</c> catalogue is a
    /// commitment against a rate nobody is charged — and for a model only the harness offers it falls all
    /// the way back to "the dearest entry", which the row then reports as an estimate when the app knew
    /// exactly what it asked for.
    ///
    /// Drop <c>roleRuntime</c> from the meter and this goes red on the estimate label.
    /// </summary>
    [Fact]
    public void A_roles_reservation_is_priced_at_the_runtime_that_role_is_on()
    {
        using var db = TestEnv.NewDb();
        var records = Path.Combine(TestEnv.Home, $"role-runtime-{Guid.NewGuid():n}.jsonl");

        // gpt-5-nano is on the harness's catalogue and NOT on codex's — the case that separates the two.
        var meter = new TurnMeter(db, cap: () => 50m,
            runtimeId: () => "codex", recordPath: records,
            model: () => "gpt-5.6-sol",
            allowance: () => TurnAllowance.From(1_000_000, 10_000),
            share: _ => 1m,
            roleModel: role => role == CouncilRoles.Research ? "gpt-5-nano" : "gpt-5.6-sol",
            roleRuntime: role => role == CouncilRoles.Research ? Harness : "codex");

        var research = meter.Reservation(CouncilRoles.Research);
        // 1 M input at the dearer of 0.05 and its (absent) cache-write rate, plus 10 k output at 0.40.
        Assert.Null(research.Unpriced);
        Assert.Equal(1_000_000m * 0.05m / 1_000_000m + 10_000m * 0.40m / 1_000_000m, research.Cost);
        // Priced at the model TradeAgent asked for — NOT at "the dearest entry in the catalogue", which
        // is what a model the runtime's own list does not carry falls back to. That fallback is the
        // failure this test is about: on codex's catalogue gpt-5-nano has no row at all.
        Assert.Equal(Labels.PricedAtTheModelAskedFor("gpt-5-nano"), research.Estimated);
        Assert.NotEqual(Labels.PricedAtHighestListPrice, research.Estimated);

        // And the chair is still priced on its own runtime and model: 4.00/5.00 cache write, 20.00 out.
        var chair = meter.Reservation(CouncilRoles.Operations);
        Assert.Equal(1_000_000m * 5.00m / 1_000_000m + 10_000m * 20.00m / 1_000_000m, chair.Cost);
    }
}
