using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY CONTROL THAT GRANTS AUTHORITY IS TWO-PRESS; EVERY CONTROL THAT REMOVES IT STAYS ONE.
///
/// REVIEW 2026-09-05b finding 3 and Codex F12. Three controls on the Safety page applied a
/// permission-widening change on a single click while the two beside them — the real-money switch
/// and the account picker — already asked twice: the mode row (one press moved a live-activated
/// installation from "Real, ask me first" to "Real, fully automatic", and probe P1 watched the
/// AI's next order reach a non-simulated account FILLED with the approval row still waiting), the
/// emergency toggle (one press each way, so RESUME was one press too), and the limits save (any cap
/// raised, in one click).
///
/// These press the widgets themselves. Each control is built by the static factory its screen
/// builds it with, so what is pressed here is the control the owner sees rather than a
/// reconstruction of it; the two source assertions at the bottom are what tie the factories to the
/// screens, which no test off a running app can otherwise reach.
/// </summary>
public class TwoPressGrantTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static Button ModeButton(Panel row, TradingMode m) =>
        (Button)row.Children[Array.IndexOf(Enum.GetValues<TradingMode>(), m)];

    // ---- 1. the mode row -------------------------------------------------------------------------

    /// <summary>
    /// The finding itself. One press must arm and change nothing; the second must apply it, and the
    /// armed control must say what that second press does in full — never the bare word "Confirm".
    /// </summary>
    [Theory]
    [InlineData(TradingMode.LIVE_AUTONOMOUS, "Confirm: let the AI place real orders without asking")]
    [InlineData(TradingMode.LIVE_CONFIRM, "Confirm: let the AI propose real orders")]
    public void A_real_money_mode_takes_two_presses_and_says_what_the_second_one_does(TradingMode mode, string armed)
    {
        TradingMode? applied = null;
        var row = SafetyPage.BuildModeRow(m => applied = m);
        var b = ModeButton(row, mode);

        Press(b);
        Assert.Null(applied);
        Assert.Equal(armed, b.Content);

        Press(b);
        Assert.Equal(mode, applied);
        Assert.Equal(Ui.ModeLabel(mode), b.Content);
    }

    /// <summary>
    /// The other direction, which is the whole reason this is not "make the row two-press". Watch
    /// only and Practice can only ever take authority away, and a mode row where leaving a live
    /// mode costs two presses is a mode row that argues with someone trying to stop.
    /// </summary>
    [Theory]
    [InlineData(TradingMode.OBSERVE)]
    [InlineData(TradingMode.PAPER)]
    public void A_mode_that_only_reduces_stays_one_press(TradingMode mode)
    {
        TradingMode? applied = null;
        var row = SafetyPage.BuildModeRow(m => applied = m);

        Press(ModeButton(row, mode));

        Assert.Equal(mode, applied);
    }

    /// <summary>
    /// The refresh tick marks the selected mode by painting the buttons, and it runs every five
    /// seconds. A half-pressed real-money mode has to survive it — both the arming and the sentence
    /// on the face of the control, which is what the owner is reading when the tick lands.
    /// </summary>
    [Fact]
    public void The_five_second_refresh_does_not_undo_a_half_pressed_real_money_mode()
    {
        TradingMode? applied = null;
        var row = SafetyPage.BuildModeRow(m => applied = m);
        var b = ModeButton(row, TradingMode.LIVE_AUTONOMOUS);

        Press(b);
        foreach (var mode in Enum.GetValues<TradingMode>())
            Ui.Emphasise(ModeButton(row, mode), mode == TradingMode.PAPER);

        Assert.True(Ui.IsArmed(b));
        Assert.Equal(Labels.ModeAutonomousArmed, b.Content);
        Assert.Null(applied);

        Press(b);
        Assert.Equal(TradingMode.LIVE_AUTONOMOUS, applied);
    }

    // ---- 2. the emergency toggle -----------------------------------------------------------------

    /// <summary>
    /// STOP is one press in the taking-away direction, always: hesitating there costs money. RESUME
    /// hands the permission back to something that trades real money, so it is two, and it says so.
    /// </summary>
    [Fact]
    public void The_safety_pages_kill_switch_stops_in_one_press_and_resumes_in_two()
    {
        var stopped = false;
        var b = SafetyPage.BuildKillSwitch(() => stopped, () => stopped = true, () => stopped = false);

        Assert.Equal(Labels.StopAiTrading, b.Content);
        Press(b);
        Assert.True(stopped);

        Ui.SetResting(b, Labels.ResumeAiTrading, "emergency");
        Press(b);
        Assert.True(stopped);
        Assert.Equal(Labels.ResumeAiTradingArmed, b.Content);

        Press(b);
        Assert.False(stopped);
    }

    /// <summary>
    /// The copy in the window chrome is the one an owner reaches from every page. If it resumed on
    /// a single press the Safety page's second press would be decoration.
    /// </summary>
    [Fact]
    public void The_window_chromes_kill_switch_stops_in_one_press_and_resumes_in_two()
    {
        var stopped = true;
        var b = MainWindow.BuildKillSwitch(() => stopped, () => stopped = true, () => stopped = false);

        Ui.SetResting(b, Labels.ResumeAiTrading, "primary");
        Press(b);
        Assert.True(stopped);
        Assert.Equal(Labels.ResumeAiTradingArmed, b.Content);

        Press(b);
        Assert.False(stopped);

        Ui.SetResting(b, Labels.StopAiTrading, "danger");
        Press(b);
        Assert.True(stopped);
    }

    /// <summary>
    /// The tick relabels this control on every refresh. A half-pressed RESUME must survive one that
    /// says nothing new — and must NOT survive the gateway saying the AI is trading again, because
    /// then the sentence on the control is no longer true of the button underneath it.
    /// </summary>
    [Fact]
    public void A_half_pressed_resume_survives_a_refresh_and_is_abandoned_when_the_state_moves_on()
    {
        var stopped = true;
        var b = SafetyPage.BuildKillSwitch(() => stopped, () => stopped = true, () => stopped = false);

        Ui.SetResting(b, Labels.ResumeAiTrading, "emergency");
        Press(b);
        Ui.SetResting(b, Labels.ResumeAiTrading, "emergency");
        Assert.True(Ui.IsArmed(b));
        Assert.Equal(Labels.ResumeAiTradingArmed, b.Content);

        stopped = false;
        Ui.SetResting(b, Labels.StopAiTrading, "emergency");
        Assert.False(Ui.IsArmed(b));
        Assert.Equal(Labels.StopAiTrading, b.Content);
    }

    // ---- 3. a raised cap is a grant (Codex F12) --------------------------------------------------

    static RiskPolicy Policy(decimal qty = 1m, decimal notional = 500m, int positions = 2,
        int perMinute = 6, string[]? allow = null) =>
        new()
        {
            MaxOrderQuantity = qty, MaxNotionalPerOrder = notional, MaxOpenPositions = positions,
            MaxOrdersPerMinute = perMinute, InstrumentAllowlist = [.. allow ?? ["ES"]]
        };

    [Fact]
    public void A_save_that_widens_one_cap_takes_two_presses_and_names_it()
    {
        (RiskPolicy Pending, string Named)[] cases =
        [
            (Policy(qty: 2m), Labels.MaxOrderQuantity),
            (Policy(notional: 600m), Labels.MaxNotionalPerOrder),
            // Zero is "not enforced" on this field alone, so it is the widest value it has.
            (Policy(notional: 0m), Labels.MaxNotionalPerOrder),
            (Policy(positions: 3), Labels.MaxOpenPositions),
            (Policy(perMinute: 7), Labels.MaxOrdersPerMinute),
            (Policy(allow: ["ES", "NQ"]), Labels.InstrumentAllowlist)
        ];

        foreach (var (pending, named) in cases)
        {
            var saved = 0;
            var b = SafetyPage.BuildSaveLimits(() => Policy(), () => pending, () => saved++);

            Press(b);
            Assert.Equal($"{named}: {0}", $"{named}: {saved}");
            Assert.Equal($"Confirm: widen “{named}”", b.Content);

            Press(b);
            Assert.Equal($"{named}: {1}", $"{named}: {saved}");
            Assert.Equal(Labels.SaveLimits, b.Content);
        }
    }

    /// <summary>
    /// The other direction, and the reason the whole button is not simply two-press: an owner
    /// taking authority away — including the one clearing the allowlist to stop everything — should
    /// not have to argue with the software about it.
    /// </summary>
    [Fact]
    public void A_save_that_only_narrows_takes_one_press()
    {
        (RiskPolicy Pending, string What)[] cases =
        [
            (Policy(), "unchanged"),
            (Policy(qty: 0m), "quantity to zero"),
            (Policy(notional: 400m), "a smaller money cap"),
            (Policy(positions: 0), "no open positions"),
            (Policy(perMinute: 1), "fewer orders a minute"),
            (Policy(allow: ["es"]), "the same instrument, cased differently"),
            (Policy(allow: []), "the allowlist cleared, which allows nothing")
        ];

        foreach (var (pending, what) in cases)
        {
            var saved = 0;
            var b = SafetyPage.BuildSaveLimits(() => Policy(), () => pending, () => saved++);

            Press(b);

            Assert.Equal($"{what}: saved 1, button says {Labels.SaveLimits}",
                $"{what}: saved {saved}, button says {b.Content}");
        }
    }

    /// <summary>Five limit names on one button is a sentence nobody reads, so several are counted.</summary>
    [Fact]
    public void A_save_that_widens_several_caps_counts_them()
    {
        var saved = 0;
        var b = SafetyPage.BuildSaveLimits(() => Policy(), () => Policy(qty: 9m, perMinute: 60), () => saved++);

        Press(b);

        Assert.Equal(0, saved);
        Assert.Equal("Confirm: widen 2 of your safety limits", b.Content);
    }

    /// <summary>
    /// The comparison on its own, on the field where "wider" is not "larger". Zero means "not
    /// enforced" for the money cap and nothing else, so it widens every bounded value and nothing
    /// widens it — including a large number, which is narrower than no cap at all.
    /// </summary>
    [Fact]
    public void Zero_is_the_widest_value_the_money_cap_has_and_the_narrowest_everywhere_else()
    {
        Assert.Equal(new[] { Labels.MaxNotionalPerOrder },
            RiskPolicy.Widenings(Policy(notional: 500m), Policy(notional: 0m)));
        Assert.Empty(RiskPolicy.Widenings(Policy(notional: 0m), Policy(notional: 1_000_000m)));

        Assert.Empty(RiskPolicy.Widenings(Policy(qty: 1m), Policy(qty: 0m)));
        Assert.Empty(RiskPolicy.Widenings(Policy(perMinute: 6), Policy(perMinute: 0)));
        Assert.Empty(RiskPolicy.Widenings(Policy(positions: 2), Policy(positions: 0)));
    }

    // ---- 4. what the armed control looks like, which the running app settled ----------------------

    /// <summary>
    /// THE ARMED SENTENCE HAS TO FIT ON THE SCREEN. A horizontal <c>StackPanel</c> gave the row a
    /// width it could not have: an armed real-money button carries a whole sentence, three times
    /// the width of the label it replaces, and on the running app the row ran past its card and the
    /// sentence was cut off mid-word — "Confirm: let the AI place real or". A control whose only
    /// job is to say what the second press does had become unreadable at exactly the moment it
    /// mattered. A wrapping row gives it its own line instead; the resting row still fits on one.
    /// This asserts the panel, because measuring text needs a running app — the screenshot is in
    /// the unit's report.
    /// </summary>
    [Fact]
    public void The_mode_row_wraps_so_an_armed_sentence_is_not_clipped()
    {
        var row = SafetyPage.BuildModeRow(_ => { });

        Assert.IsType<WrapPanel>(row);
        Assert.Equal(Enum.GetValues<TradingMode>().Length, row.Children.Count);
    }

    /// <summary>
    /// THE EMERGENCY CONTROL PAINTS ITS OWN TEXT WHILE ARMED. Arming swaps a two-step button to the
    /// "danger" class, whose foreground is red — and this button's fill is red, so the armed
    /// sentence was painted red on red and the half-pressed RESUME was a blank red block on the
    /// running app. The fill and the text are both local values here, and a local value beats a
    /// style, so the repaint has to set both or neither.
    /// </summary>
    [Fact]
    public void The_armed_emergency_kill_switch_paints_text_that_is_not_its_own_fill()
    {
        var b = SafetyPage.BuildKillSwitch(() => true, () => { }, () => { });
        Ui.SetResting(b, Labels.ResumeAiTrading, "emergency");

        Press(b);

        Assert.True(Ui.IsArmed(b));
        Assert.Equal(Labels.ResumeAiTradingArmed, b.Content);
        Assert.Equal(Theme.Danger, b.Background);
        Assert.Equal(Theme.TextOnEmergency, b.Foreground);
        Assert.NotEqual(b.Background, b.Foreground);
    }

    // ---- 5. the composition ----------------------------------------------------------------------

    /// <summary>
    /// What ties the controls above to the screens that show them. Everything the factories DO is
    /// pressed above; this is the one line of composition per screen that no test off a running app
    /// can reach, so — like <c>UpdateTrustTests.The_composition_root_still_calls_the_seam</c> — it
    /// catches a revert or a deletion and not a rewrite. The armed labels were also read off the
    /// running app; see the unit's report.
    /// </summary>
    [Fact]
    public void The_safety_page_builds_all_three_controls_through_those_factories()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TradeAgent.App", "DashboardView.cs"));

        Assert.Contains("_modeRow = BuildModeRow(m => _host.Gateway.SetMode(m));", text);
        Assert.Contains("_stopButton = BuildKillSwitch(", text);
        Assert.Contains("BuildSaveLimits(() => _host.Gateway.Settings.Risk, PendingLimits, SaveLimits)", text);

        // The one-press versions of all three, so a revert cannot pass by adding rather than editing.
        Assert.DoesNotContain("Ui.Secondary(Ui.ModeLabel(", text);
        Assert.DoesNotContain("Ui.Primary(Labels.SaveLimits", text);
        Assert.DoesNotContain("Ui.Big(", text);
    }

    [Fact]
    public void The_window_chrome_builds_its_kill_switch_through_the_factory()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TradeAgent.App", "MainWindow.cs"));

        Assert.Contains("_stopButton = BuildKillSwitch(", text);
        Assert.DoesNotContain($"Ui.Danger(\"{Labels.StopAiTrading}\"", text);
    }

    /// <summary>
    /// The guide justified a one-press RESUME with "a mis-press costs nothing", which is true of
    /// STOP and false of RESUME in the fully automatic mode with real money switched on. A sentence
    /// that describes a control the product no longer has is how an owner learns not to trust it.
    /// </summary>
    [Fact]
    public void The_guides_no_longer_promise_a_one_press_resume_or_a_one_press_real_money_mode()
    {
        foreach (var doc in new[] { "USER-GUIDE.md", "MONITORING-PHASE.md", "DEPLOYMENT.md" })
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", doc));
            Assert.DoesNotContain("one press, in both directions", text);
            Assert.DoesNotContain("One press each way", text);
            Assert.DoesNotContain("one press each way", text);
        }

        var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "USER-GUIDE.md"));
        Assert.Contains(Labels.ResumeAiTradingArmed, guide);
        Assert.Contains(Labels.ModeAutonomousArmed, guide);
    }

    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
