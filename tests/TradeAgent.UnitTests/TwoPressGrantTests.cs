using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Data;
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

    // ---- 1. the mode row: DELETED with the row itself (U-board-a1, R24 § 4.5) — the mode is the security board's policy, never
    // a press on the Safety page, and no path selects a real-money mode (BoardTests.No_app_path_selects_a_live_mode).

    // ---- 2. the emergency toggle -----------------------------------------------------------------

    /// <summary>
    /// STOP is one press in the taking-away direction, always: hesitating there costs money. RESUME
    /// hands the permission back to something that trades real money, so it is two, and it says so.
    /// </summary>
    [Fact]
    public void The_owners_pages_kill_switch_stops_in_one_press_and_resumes_in_two()
    {
        var stopped = false;
        var b = OwnerPage.BuildKillSwitch(() => stopped, () => stopped = true, () => stopped = false);

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
        var b = OwnerPage.BuildKillSwitch(() => stopped, () => stopped = true, () => stopped = false);

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
        int perMinute = 6, string[]? allow = null, decimal trade = 200m, decimal daily = 500m) =>
        new()
        {
            MaxOrderQuantity = qty, MaxNotionalPerOrder = notional, MaxOpenPositions = positions,
            MaxOrdersPerMinute = perMinute, InstrumentAllowlist = [.. allow ?? ["ES"]],
            MaxLossPerTrade = trade, MaxDailyLoss = daily
        };

    /// <summary>
    /// The comparison alone, on the two fields where turning the limit OFF is the widest thing the
    /// owner can do to it — and where nothing, however large, widens a limit that is already off.
    /// </summary>
    [Fact]
    public void Zero_is_the_widest_value_both_loss_budgets_have()
    {
        Assert.Equal(new[] { Labels.MaxLossPerTrade },
            RiskPolicy.Widenings(Policy(trade: 200m), Policy(trade: 0m)));
        Assert.Equal(new[] { Labels.MaxDailyLoss },
            RiskPolicy.Widenings(Policy(daily: 500m), Policy(daily: 0m)));

        Assert.Empty(RiskPolicy.Widenings(Policy(trade: 0m), Policy(trade: 1_000_000m)));
        Assert.Empty(RiskPolicy.Widenings(Policy(daily: 0m), Policy(daily: 1_000_000m)));

        Assert.Equal(new[] { Labels.MaxLossPerTrade, Labels.MaxDailyLoss },
            RiskPolicy.Widenings(Policy(), Policy(trade: 0m, daily: 0m)));
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
    /// THE EMERGENCY CONTROL PAINTS ITS OWN TEXT WHILE ARMED. Arming swaps a two-step button to the
    /// "danger" class, whose foreground is red — and this button's fill is red, so the armed
    /// sentence was painted red on red and the half-pressed RESUME was a blank red block on the
    /// running app. The fill and the text are both local values here, and a local value beats a
    /// style, so the repaint has to set both or neither.
    /// </summary>
    [Fact]
    public void The_armed_emergency_kill_switch_paints_text_that_is_not_its_own_fill()
    {
        var b = OwnerPage.BuildKillSwitch(() => true, () => { }, () => { });
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
    public void The_owners_page_builds_all_three_emergency_controls_through_those_factories()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TradeAgent.App", "OwnerView.cs"));

        Assert.Contains("_stopButton = BuildKillSwitch(", text);
        // U-board-a2: the limits are the security board's policy, so the Save limits press left the page with its boxes.
        Assert.DoesNotContain("BuildSaveLimits(() =>", text);
        Assert.Contains("Ui.Confirm(Labels.CancelAllWorkingOrders, \"Confirm: cancel all working orders\"", text);
        Assert.Contains("Ui.Confirm(Labels.CloseAllPositions, \"Confirm: close all positions with market orders\"", text);

        // The one-press versions, so a revert cannot pass by adding rather than editing.
        Assert.DoesNotContain("Ui.Big(", text);
        Assert.DoesNotContain("Ui.Danger(Labels.CancelAllWorkingOrders", text);
        Assert.DoesNotContain("Ui.Danger(Labels.CloseAllPositions", text);
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
        // NOR A REAL-MONEY MODE PRESS AT ALL (U-settings-shed fix pass 1): the mode is the security board's decision and no build has the
        // press, so the guide no longer quotes its armed sentence — the sentence this test once required is the control it no longer has.
        Assert.DoesNotContain(Labels.ModeAutonomousArmed, guide);
    }

    // ---- 4. the holdout card (U-referee-1) -------------------------------------------------------

    /// <summary>
    /// THE HOLDOUT IS TWO PRESSES IN BOTH DIRECTIONS, WHICH NO OTHER CONTROL IN THIS APP IS.
    ///
    /// <para>The rule everywhere else is "widening asks twice, narrowing asks once", because hesitating
    /// on the way down costs money. This control has no way down: <c>DatasetStore.SetHoldout</c> refuses
    /// to move a cutoff earlier, since the bars in between have already been served to the research
    /// process. So the first press is the last moment the owner can change their mind, and the armed
    /// sentence has to carry the DATE — "Confirm" alone would not say which months are about to become
    /// private evidence.</para>
    /// </summary>
    [Theory]
    [InlineData(EvaluationClass.Research,
        "Confirm: bars from 2026-06-01 00:00 UTC on are evidence the research process never sees")]
    [InlineData(EvaluationClass.Fixture,
        "Confirm: these are fixture bars from 2026-06-01 00:00 UTC on \u2014 runs over them are never evidence")]
    public void The_holdout_card_takes_two_presses_and_the_armed_sentence_names_the_date(
        string evaluationClass, string armed)
    {
        var typed = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        (DateTimeOffset At, string Class)? applied = null;
        var b = SettingsPage.BuildHoldoutConfirm(evaluationClass, () => typed, (at, c) => applied = (at, c));

        Press(b);
        Assert.Null(applied);
        Assert.Equal(armed, b.Content);

        Press(b);
        Assert.Equal(typed, applied?.At);
        Assert.Equal(evaluationClass, applied?.Class);
    }

    /// <summary>
    /// A DATE CHANGED UNDER A HALF-PRESSED BUTTON DISARMS IT. The sentence the owner read named one
    /// instant; completing it against another would hold back a different set of months from the one
    /// they agreed to, and that cannot be undone afterwards.
    /// </summary>
    [Fact]
    public void Changing_the_date_disarms_a_half_pressed_holdout()
    {
        var typed = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? applied = null;
        var b = SettingsPage.BuildHoldoutConfirm(EvaluationClass.Research, () => typed, (at, _) => applied = at);

        Press(b);
        Assert.True(Ui.IsArmed(b));

        typed = typed.AddMonths(1);
        Ui.Relabel(b, Labels.SetHoldout, SettingsPage.HoldoutArmed(EvaluationClass.Research, typed));

        Assert.False(Ui.IsArmed(b));
        Assert.Equal(Labels.SetHoldout, b.Content);
        Assert.Null(applied);
    }

    /// <summary>
    /// A date this build cannot read applies NOTHING, even on a second press. The cutoff decides which
    /// bars exist for the AI, and a guess at what the owner meant is the one thing this control must
    /// never do.
    /// </summary>
    [Fact]
    public void An_unreadable_date_applies_no_holdout_on_either_press()
    {
        var applied = 0;
        var b = SettingsPage.BuildHoldoutConfirm(EvaluationClass.Research, () => null, (_, _) => applied++);

        Press(b);
        Press(b);

        Assert.Equal(0, applied);
    }

    // ---- 5. the capital allocation card: shed (U-settings-shed) — allocating capital is the security board's decision -----------

    // ---- 6. the review-hold release card (U-reopen-2) --------------------------------------------

    /// <summary>
    /// RELEASING A REVIEW HOLD IS TWO PRESSES, AND THE ARMED SENTENCE NAMES WHAT IT LETS BACK IN.
    ///
    /// <para>A scope that reached the loss budget twice inside the strike window is not reopened by
    /// code at all, and this press is the only thing anywhere in the product that changes that. One
    /// press must arm and release nothing — the mutant this pins is the card built with
    /// <c>Ui.Button</c> instead, where the hold lifts on the first click — and the second must carry
    /// the whole sentence, naming the account and the instruments rather than counting them.</para>
    /// </summary>
    [Fact]
    public void Releasing_a_review_hold_takes_two_presses_and_the_armed_sentence_names_the_scopes()
    {
        string? released = null;
        var b = OwnerPage.BuildReleaseConfirm(() => ["your account", "ES"], () => "checked ATAS",
            note => released = note);
        b.IsEnabled = true;

        Press(b);
        Assert.Null(released);
        Assert.Equal(
            "Confirm: release the review hold on your account, ES — TradeAgent may reopen them once "
            + "the closure has run its time",
            b.Content);

        Press(b);
        Assert.Equal("checked ATAS", released);
        Assert.Equal(Labels.ReopenAfterReview, b.Content);
    }

    /// <summary>
    /// AN EMPTY NOTE RELEASES NOTHING, ON EITHER PRESS. The note is the only durable trace of a
    /// person overruling the software's own refusal to let an account back in; the page disables the
    /// button until something is typed, and this is what stops a control that was enabled and then
    /// emptied from applying a release with no words on it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_note_releases_nothing_on_either_press(string note)
    {
        var released = 0;
        var b = OwnerPage.BuildReleaseConfirm(() => ["your account"], () => note, _ => released++);
        b.IsEnabled = true;

        Press(b);
        Press(b);

        Assert.Equal(0, released);
    }

    /// <summary>
    /// A CHANGED NOTE UNDER A HALF-PRESSED BUTTON DISARMS IT, and so does a change in what is held.
    /// The sentence the owner read named the scopes that were held when they read it, and the note
    /// is the assertion the second press writes down — completing one against the other's words is
    /// the disagreement the unconfirmed-orders card already refuses.
    /// </summary>
    [Fact]
    public void Changing_what_is_held_disarms_a_half_pressed_release()
    {
        var released = 0;
        var b = OwnerPage.BuildReleaseConfirm(() => ["your account", "ES"], () => "checked ATAS",
            _ => released++);
        b.IsEnabled = true;

        Press(b);
        Assert.True(Ui.IsArmed(b));

        Ui.Relabel(b, Labels.ReopenAfterReview, Labels.ReleaseHoldArmed(["your account"]));

        Assert.False(Ui.IsArmed(b));
        Assert.Equal(Labels.ReopenAfterReview, b.Content);
        Assert.Equal(0, released);
    }

    /// <summary>
    /// THE OWNER'S PAGE BUILDS ITS RELEASE CARD WITH THAT FACTORY — the line that ties the control
    /// pressed above to the one the owner sees, which no test off a running app can otherwise reach.
    /// </summary>
    [Fact]
    public void The_owners_page_builds_its_release_card_with_the_two_press_factory()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "TradeAgent.App", "OwnerView.cs"));
        Assert.Contains("BuildReleaseConfirm(HeldScopes, () => _releaseNote.Text ?? \"\", ApplyRelease)", text);
        Assert.DoesNotContain("Ui.Button(Labels.ReopenAfterReview", text);
    }

    static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
