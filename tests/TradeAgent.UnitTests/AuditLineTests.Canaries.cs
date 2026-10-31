using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Audit;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUBLIC AUDIT LINE, PART B (<c>U-org-audit-b</c>; ORCHESTRATION § 2.13, VISION §§ 5.3 and 7.7, ORGANISATION § 4; the owner's Q1–Q3 of
/// 2026-10-22): its own budget, the owner's, carved off the top of each daily ceiling; canaries and impossible assignments it plants in a
/// scratch proving home of its own and scores; catch rates per detector as measurements in their own table. No model, no real money.
/// </summary>
public partial class AuditLineTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- (k) the budget, off the top --------------------------------------------------------------------------------------

    /// <summary>
    /// (k) GUARD — BUDGET-001, the owner's Q1: THE ORGANISATION IS ADMITTED AGAINST EACH CEILING LESS THE AUDIT LINE'S BUDGET. Over the
    /// product's own composition (the meter the host builds, its two ceiling delegates): with no budget set the money cap and the board's plan
    /// day-total are the organisation's whole; the owner keeps 2 of real money and 15 of plan capacity for the line and the meter admits the
    /// organisation against 3 and 25; a budget above its ceiling leaves the organisation nothing, never a negative room; and a line whose file
    /// refused a read admits the organisation NOTHING — its budget unknown, never zero.
    /// </summary>
    [Fact]
    public async Task The_organisation_is_admitted_against_each_ceiling_less_the_audit_budget()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            host.Gateway.Update(s => s.AiDailyCostCap = 5m);
            var plan = host.Gateway.Policy.PlanDayTotal;
            Assert.True(plan > 0m);
            var line = host.ComposeAuditLine(NewFile());
            var store = line.Store!;
            (decimal Money, decimal Plan) Caps() => (host.Meter!.TodayIn(BillingClasses.Key).Cap, host.Meter!.TodayIn(BillingClasses.Plan).Cap);

            Assert.Equal((5m, plan), Caps());
            store.SetBudget(BillingClasses.Key, 2m, Now);
            store.SetBudget(BillingClasses.Plan, 15m, Now);
            log.WriteLine($"with 2 and 15 kept: {Caps()}");
            Assert.Equal((3m, plan - 15m), Caps());
            store.SetBudget(BillingClasses.Key, 7m, Now);
            Assert.Equal(0m, Caps().Money);
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetBudget(BillingClasses.Key, -1m, Now));
            Assert.Throws<ArgumentException>(() => store.SetBudget(BillingClasses.Unknown, 1m, Now));

            // A FILE THE LINE REFUSED: its budget unknown, the organisation admitted nothing.
            store.Dispose();
            Assert.Equal((0m, 0m), Caps());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// (l) GUARD — two presses to raise (the owner's Q1): RAISING THE LINE'S BUDGET ASKS TWICE AND NAMES THE FIGURE; LOWERING IT, OR ZERO,
    /// SAVES IN ONE; AN EDIT DISARMS AN ARMED RAISE. Pressed on the control the owner sees; each figure is a new row of the line's own file.
    /// </summary>
    [Fact]
    public void Raising_an_audit_budget_takes_two_presses_and_lowering_one()
    {
        using var store = new AuditStore(NewFile(), Now);
        var section = new AuditSection(new AuditSources
        {
            Store = () => store, Refusal = () => null, Currency = () => "USD", MoneyCeiling = () => 5m, PlanCeiling = () => 40m, Now = () => Now,
        });

        section.MoneyBox.Box.Text = "2";
        Press(section.MoneySave);
        Assert.Equal(0m, store.Budget(BillingClasses.Key));
        Assert.Equal(Labels.RaiseAuditBudgetArmed("2 USD", "real money"), section.MoneySave.Content);
        Press(section.MoneySave);
        Assert.Equal(2m, store.Budget(BillingClasses.Key));
        Assert.Contains("the organisation may use 3 USD of your 5 USD", section.MoneyText.Text, StringComparison.Ordinal);

        section.PlanBox.Box.Text = "10";
        Press(section.PlanSave);
        section.PlanBox.Box.Text = "12";
        Press(section.PlanSave);
        Assert.Equal(0m, store.Budget(BillingClasses.Plan));
        Assert.Equal(Labels.RaiseAuditBudgetArmed("12 USD", "plan capacity"), section.PlanSave.Content);
        Press(section.PlanSave);
        Assert.Equal(12m, store.Budget(BillingClasses.Plan));

        section.MoneyBox.Box.Text = "1";
        Press(section.MoneySave);
        Assert.Equal(1m, store.Budget(BillingClasses.Key));
        section.MoneyBox.Box.Text = "0";
        Press(section.MoneySave);
        Assert.Equal(0m, store.Budget(BillingClasses.Key));
        Assert.Contains("Nothing of real money is kept", section.MoneyText.Text, StringComparison.Ordinal);
    }

    // ---- (m) canaries and catch rates -------------------------------------------------------------------------------------

    /// <summary>
    /// (m) GUARD — evidence (measurement vs claim): EVERY CANARY IS CAUGHT BY ITS DETECTOR AND NO CONTROL RAISES ONE, MEASURED. One run plants,
    /// in a scratch proving home beside the line's file, a known-bad delivery for every detector the line runs — made by the app's own
    /// <c>OrgStore</c> — beside clean controls (an impossible assignment answered honestly among them), runs the line's real sweep over it and
    /// appends one catch row per detector: planted, caught, controls, false alarms. Read back from the line's own file; the scratch home is
    /// gone; the app's own findings table holds nothing of it.
    /// </summary>
    [Fact]
    public void Every_canary_is_caught_by_its_detector_and_no_control_raises_one()
    {
        using var store = new AuditStore(NewFile(), Now);
        var run = AuditCanaries.Run(store, Now);
        foreach (var k in run.Catches) log.WriteLine($"{k.Detector}: caught {k.Caught} of {k.Planted}; false alarms {k.FalseAlarms} of {k.Controls}");
        Assert.Null(run.Failure);
        Assert.Equal(AuditCanaries.Detectors.Order(StringComparer.Ordinal), run.Catches.Select(k => k.Detector).Order(StringComparer.Ordinal));
        Assert.All(run.Catches, k =>
        {
            Assert.True(k.Planted >= 1, $"{k.Detector}: nothing planted");
            Assert.True(k.Caught == k.Planted, $"{k.Detector}: caught {k.Caught} of {k.Planted}");
            Assert.True(k.Controls >= 3 && k.FalseAlarms == 0, $"{k.Detector}: {k.FalseAlarms} false alarms on {k.Controls} controls");
        });

        var last = store.LastCanaryRun()!;
        Assert.Equal(run.Seq, last.Seq);
        Assert.Equal(run.Catches.OrderBy(k => k.Detector, StringComparer.Ordinal), last.Catches.OrderBy(k => k.Detector, StringComparer.Ordinal));
        Assert.False(Directory.Exists(store.File + AuditCanaries.ScratchSuffix), "the scratch home was left behind");
        Assert.Empty(store.Findings());
        Assert.Empty(store.Samples());
    }

    /// <summary>
    /// (n) GUARD: A CANARY ITS DETECTOR MISSES IS RECORDED AS A MEASURED MISS. The seal's canary planted without its defect (the test's seam):
    /// the seal's row says caught 0 of 1, every other detector still caught its own, and the owner's section says MISSED in so many words.
    /// </summary>
    [Fact]
    public void A_canary_its_detector_misses_is_recorded_as_a_measured_miss()
    {
        using var store = new AuditStore(NewFile(), Now);
        var run = AuditCanaries.Run(store, Now, benign: AuditCheckName.Seal);
        Assert.Null(run.Failure);
        var seal = Assert.Single(store.LastCanaryRun()!.Catches, k => k.Detector == AuditCheckName.Seal);
        log.WriteLine($"seal: caught {seal.Caught} of {seal.Planted}, missed {seal.Missed}");
        Assert.Equal((1, 0, 1), (seal.Planted, seal.Caught, seal.Missed));
        Assert.All(run.Catches.Where(k => k.Detector != AuditCheckName.Seal), k => Assert.Equal(k.Planted, k.Caught));

        var words = AuditWords.Of(store, null, "USD", 5m, 40m);
        Assert.Contains("seal: MISSED 1 of 1; no false alarm on", string.Join("\n", words.Catches), StringComparison.Ordinal);
    }

    /// <summary>
    /// (o) THE CANARIES RUN ONCE A DAY, FROM THE LINE'S OWN FILE: due with none recorded, not again within the day, due a day later — so a
    /// restart neither repeats nor skips one; the line's own tick runs them when due and only then.
    /// </summary>
    [Fact]
    public async Task The_canaries_run_once_a_day_from_the_lines_own_file()
    {
        using var db = TestEnv.NewDb();
        var file = NewFile();
        await using var line = new AuditLine(db.FilePath, file);
        var store = line.Store!;
        Assert.True(AuditCanaries.Due(store, Now));
        line.Tick(Now);
        Assert.NotNull(store.LastCanaryRun());
        var first = store.LastCanaryRun()!.Seq;
        line.Tick(Now.AddHours(23));
        Assert.Equal(first, store.LastCanaryRun()!.Seq);
        Assert.True(AuditCanaries.Due(store, Now.AddHours(24)));
        line.Tick(Now.AddHours(24));
        Assert.NotEqual(first, store.LastCanaryRun()!.Seq);
    }

    // ---- (p) impossible assignments ---------------------------------------------------------------------------------------

    /// <summary>
    /// (p) GUARD — evidence: A PLANTED IMPOSSIBLE ASSIGNMENT ACCEPTED AS DONE, NAMING NO DEVIATION, IS A SEVERE FABRICATION FINDING, AND A TRAP
    /// IS ALWAYS DRAWN. With the sampling rate at ZERO, the two mandates the line registered as traps are still drawn: the one accepted naming
    /// no deviation fails <c>impossible-claimed</c> (severe, ids only); the one that said it could not be done passes it. An ordinary acceptance
    /// at rate zero is not drawn, and is never run through the fabrication check.
    /// </summary>
    [Fact]
    public void A_trap_accepted_as_done_is_a_severe_fabrication_finding_and_always_drawn()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var now = DateTimeOffset.UtcNow;
        var fabricated = Accepted(org, now, "done: here is the record");
        var honest = Accepted(org, now, "it cannot be done", [new MandateDeviation("no such record exists", "nothing was written under it", "none")]);
        var ordinary = Accepted(org, now, "an ordinary answer");

        using var store = new AuditStore(NewFile(), now);
        store.RegisterTrap(org.Delivery(fabricated)!.MandateId, now);
        store.RegisterTrap(org.Delivery(honest)!.MandateId, now);
        Assert.Null(new AuditSweep(db.FilePath, store, rate: 0).Run(now).Failure);

        Assert.Equal([fabricated, honest], store.Samples().Select(s => s.DeliveryId));
        var finding = Assert.Single(store.Findings());
        log.WriteLine($"{finding.Check} {finding.Severity} on {finding.DeliveryId[..12]}");
        Assert.Equal((fabricated, AuditCheckName.ImpossibleClaimed, AuditSeverity.Severe), (finding.DeliveryId, finding.Check, finding.Severity));
        Assert.True(Assert.Single(store.Checks(honest), k => k.Name == AuditCheckName.ImpossibleClaimed).Passed);
        Assert.DoesNotContain(store.Checks(), k => k.DeliveryId == ordinary);
        Assert.Equal(0d, AuditPolicy.RealTrapRate);
    }

    /// <summary>
    /// (q) GUARD — authority (no organisation path funds the line): ONLY THE OWNER'S PAGE SETS THE AUDIT LINE'S BUDGET. Across every source of
    /// the product, the one caller of <c>SetBudget</c> is the owner's audit section in the app; no store, gateway, door, board or runtime code
    /// names it. Pins an absence.
    /// </summary>
    [Fact]
    public void Only_the_owners_page_sets_the_audit_budget()
    {
        var root = RepoRoot();
        var callers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".SetBudget(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        Assert.Equal(["src/TradeAgent.App/AuditView.cs"], callers);
    }
}
