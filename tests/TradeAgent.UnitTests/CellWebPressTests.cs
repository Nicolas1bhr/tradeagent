using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S ONE PRESS FOR WEB ON WINDOWS AND THE ONE FIXED HELPER BEHIND IT (<c>U-cell-windows-egress</c> item 1), on every OS: the helper's
/// entry refuses anything but its one argument before touching anything; the press's words for each outcome — offered, Windows' prompt open,
/// declined (egress none, said, offered again), failed, placed and measured, placed and NOT measured on every cell (still none) — and the
/// Organisation page drawing them. The elevated placement itself is <c>CellWindowsEgressTests</c>' (CI's elevated runner); the UAC prompt
/// is shown nowhere a test can see it.
/// </summary>
public class CellWebPressTests
{
    [Fact]
    public void The_helper_answers_exactly_its_one_argument_and_refuses_everything_else_before_touching_anything()
    {
        Assert.Null(WindowsCellEgress.Refusal([WindowsCellEgress.Verb]));
        foreach (var args in new[]
                 {
                     Array.Empty<string>(), [WindowsCellEgress.Verb, "--open-every-port"], ["--open-every-port", WindowsCellEgress.Verb],
                     [WindowsCellEgress.Verb.ToUpperInvariant()], [" " + WindowsCellEgress.Verb], ["S-1-15-2-1"]
                 })
        {
            Assert.NotNull(WindowsCellEgress.Refusal(args));
            Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper(args));
        }

        // THE APP'S ENTRY HANDS EVERY LAUNCH THAT NAMES THE HELPER TO IT — never its window — and the helper then refuses all but the exact one.
        Assert.True(WindowsCellEgress.Named([WindowsCellEgress.Verb, "--open-every-port"]));
        Assert.True(WindowsCellEgress.Named([WindowsCellEgress.Verb.ToUpperInvariant()]));
        Assert.False(WindowsCellEgress.Named([]));
        Assert.False(WindowsCellEgress.Named(["--minimised"]));
    }

    [Fact]
    public void Off_windows_even_the_exact_argument_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;   // there it needs elevation: CellWindowsEgressTests, CI's elevated runner
        Assert.Equal(WindowsCellEgress.Refused, WindowsCellEgress.RunHelper([WindowsCellEgress.Verb]));
    }

    sealed class Fake
    {
        public bool? Web = false;
        public (EgressAsk, string) Answer = (EgressAsk.Placed, "");
        public bool MeasureProves;
        public int Asked, Measured;
        public TaskCompletionSource? Gate;

        public CellWebPress Press() => new(async () =>
            {
                Asked++;
                if (Gate is { } g) await g.Task;
                return Answer;
            },
            () =>
            {
                Measured++;
                if (MeasureProves) Web = true;
                return Task.FromResult("cell proof 7 written, egress none: web-nothing-else (slot 8 80 NOT refused)");
            },
            () => Web);
    }

    [Fact]
    public async Task Declined_at_windows_prompt_is_egress_none_said_in_words_and_offered_again()
    {
        var f = new Fake { Answer = (EgressAsk.Declined, "") };
        var press = f.Press();
        Assert.Equal(CellWebPhase.Offered, press.Phase);
        Assert.True(press.CanPress);
        Assert.Equal(Labels.CellWebOffered, press.Words);

        await press.Press();
        Assert.Equal(CellWebPhase.Declined, press.Phase);
        Assert.Contains("no internet", press.Words, StringComparison.Ordinal);
        Assert.True(press.CanPress);
        Assert.Equal(0, f.Measured);
    }

    [Fact]
    public async Task A_failed_helper_is_said_and_nothing_is_measured()
    {
        var f = new Fake { Answer = (EgressAsk.Failed, "Windows refused to set the rule (the helper ended with 4)") };
        var press = f.Press();
        await press.Press();
        Assert.Equal(CellWebPhase.Failed, press.Phase);
        Assert.Contains("the helper ended with 4", press.Words, StringComparison.Ordinal);
        Assert.Contains("no internet", press.Words, StringComparison.Ordinal);
        Assert.Equal(0, f.Measured);
    }

    [Fact]
    public async Task A_placed_rule_counts_only_when_the_benchmark_then_proves_web()
    {
        // PLACED, AND THE CHECK DID NOT SHOW IT ON EVERY CELL: still none, the benchmark's own sentence said.
        var f = new Fake();
        var press = f.Press();
        await press.Press();
        Assert.Equal(1, f.Measured);
        Assert.Equal(CellWebPhase.Failed, press.Phase);
        Assert.Contains("slot 8 80 NOT refused", press.Words, StringComparison.Ordinal);

        // PLACED AND PROVED: the proof's word, and the press is no longer drawn.
        f.MeasureProves = true;
        await press.Press();
        Assert.Equal(CellWebPhase.Proved, press.Phase);
        Assert.Equal(Labels.CellWebProved, press.Words);
        Assert.False(press.CanPress);

        // A LATER PROOF WITHOUT WEB (a new boot measured none): offered again, never the old "proved".
        f.Web = false;
        Assert.Equal(CellWebPhase.Offered, press.Phase);
    }

    [Fact]
    public async Task One_press_at_a_time_and_nothing_drawn_before_cells_run()
    {
        var f = new Fake { Web = null };
        var press = f.Press();
        Assert.Equal(CellWebPhase.Hidden, press.Phase);
        Assert.False(press.CanPress);
        Assert.Equal("", press.Words);

        f.Web = false;
        f.Gate = new TaskCompletionSource();
        var first = press.Press();
        Assert.Equal(CellWebPhase.Asking, press.Phase);
        Assert.Equal(Labels.CellWebAsking, press.Words);
        Assert.False(press.CanPress);
        await press.Press();   // a second press while Windows' prompt is open does nothing
        f.Gate.SetResult();
        await first;
        Assert.Equal(1, f.Asked);
    }

    [Fact]
    public async Task The_organisation_page_draws_the_press_and_its_outcome()
    {
        using var db = TestEnv.NewDb();
        var f = new Fake { Answer = (EgressAsk.Declined, "") };
        var press = f.Press();
        CellWebPress? source = null;
        var page = new OrganisationPage(new OrgSurfaceSources
        {
            Org = () => new OrgStore(db),
            Attempts = () => new AiAttemptStore(db),
            ProofHeld = () => true,
            CellWeb = () => source
        });

        page.Update();
        Assert.False(page.WebText.IsVisible, "a web line was drawn where there is no press (not Windows)");
        Assert.Null(page.WebPress);

        source = press;
        page.Update();
        Assert.Equal(Labels.CellWebOffered, page.WebText.Text);
        Assert.True(page.WebPress!.IsVisible);
        Assert.Equal(Labels.CellWebPress, page.WebPress!.Content);

        page.WebPress!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && press.Phase != CellWebPhase.Declined; i++) await Task.Delay(10);
        page.Update();
        Assert.Equal(Labels.CellWebDeclined, page.WebText.Text);
        Assert.True(page.WebPress!.IsVisible, "the press is not offered again after a No");

        f.Web = true;
        page.Update();
        Assert.Equal(Labels.CellWebProved, page.WebText.Text);
        Assert.False(page.WebPress!.IsVisible);
    }
}
