using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CODE IS SHOWN WITH WHAT IT NEEDS, ON EVERY PLATFORM (<c>U-window-door</c> item 5; R31 D10 F6). Two gaps the 2026-11-07 lock-out
/// showed beside the door itself: the onboarding's code well said nothing of when the code lapses, although codex prints it and the
/// Dashboard card reads it; and only the Linux onboarding said that device code login must first be switched on in ChatGPT's
/// security settings, while the card offers the code route on every platform, where a code the vendor refused left nothing in the
/// window saying why. Red at aa126ada: no <c>DeviceCodePlan.Needs</c>, no expiry on <c>AuthChallenge</c>, a well of one line.
/// </summary>
public class SignInCodeWordsTests
{
    const string Needs = "Device code login has to be switched on in ChatGPT's security settings first.";

    /// <summary>The device prompt as codex-cli 0.160.1 printed it on 2026-10-09, colour codes kept; the code is a stand-in.</summary>
    const string Printed =
        "Welcome to Codex [v\u001b[90m0.160.1\u001b[0m]\n\n1. Open this link in your browser and sign in to your account\n" +
        "   \u001b[94mhttps://auth.example.test/codex/device\u001b[0m\n\n" +
        "2. Enter this one-time code \u001b[90m(expires in 15 minutes)\u001b[0m\n" +
        "   \u001b[94mABC1-DE23F\u001b[0m\n";

    static RuntimeManifest Codex(HostOs os) =>
        RuntimeCatalog.BuiltIn(new HostPlatform(os, Architecture.X64)).Single(m => m.Id == "codex");

    static List<string> VisibleWords(Control root) =>
        root.GetLogicalDescendants().Prepend(root).OfType<TextBlock>()
            .Where(t => t.GetLogicalAncestors().Prepend(t).OfType<Control>().All(c => c.IsVisible))
            .Select(t => t.Text ?? "").Where(s => s.Length > 0).ToList();

    [Theory]
    [InlineData(HostOs.Windows)]
    [InlineData(HostOs.MacOS)]
    [InlineData(HostOs.Linux)]
    public void Every_platforms_code_route_carries_what_it_needs_and_says_it_once(HostOs os)
    {
        var m = Codex(os);
        Assert.NotNull(m.DeviceCode);
        Assert.Equal(Needs, m.DeviceCode!.Needs);
        // ONCE: the sign-in's own description no longer repeats it, so the Linux onboarding (which adds the plan's words to its
        // note before the press) does not say it twice.
        Assert.DoesNotContain("security settings", m.SignInDescription);
    }

    [Fact]
    public void The_dashboard_card_says_what_a_code_needs_before_the_press()
    {
        var codex = Codex(HostOs.Windows);
        using var service = new SeatSignIn(() => codex);
        var card = new SeatSignInCard(new SeatSignInSources { Service = service, Open = _ => { }, Activity = _ => { }, Ui = a => a() });
        var words = VisibleWords(card.Root);
        Assert.Contains(SeatSignInCard.Code, card.Root.GetLogicalDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content as string));
        Assert.Contains($"For a code: {Needs}", words);
    }

    [Fact]
    public void The_ordinary_sign_ins_code_lapses_when_the_card_would_say_it_does()
    {
        var runtime = new CliAgentRuntime(Codex(HostOs.Linux));
        var readAt = new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(readAt + TimeSpan.FromMinutes(15), runtime.CodeExpiry(Printed, readAt));
        // A PROGRAM THAT PRINTED NO LIFETIME: nothing claims one.
        Assert.Null(runtime.CodeExpiry(Printed.Replace(" \u001b[90m(expires in 15 minutes)\u001b[0m", ""), readAt));
    }

    [Fact]
    public void The_onboarding_code_well_says_when_the_code_expires_and_how_to_get_a_new_one()
    {
        var at = new DateTimeOffset(2026, 11, 13, 20, 15, 0, TimeSpan.Zero);
        var hhmm = at.ToLocalTime().ToString("HH:mm");

        var live = VisibleWords(OnboardingView.CodeWell("ABC1-DE23F", at, at - TimeSpan.FromMinutes(14)));
        Assert.Contains("ABC1-DE23F", live);
        Assert.Contains($"This code expires at {hhmm}.", live);

        var lapsed = VisibleWords(OnboardingView.CodeWell("ABC1-DE23F", at, at + TimeSpan.FromSeconds(1)));
        Assert.Contains($"This code expired at {hhmm}. {SeatSignInCard.Retry} to try again.", lapsed);

        // NO LIFETIME READ, NO CLAIM: the well is the code alone.
        Assert.DoesNotContain(OnboardingView.CodeWell("ABC1-DE23F").GetLogicalDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("expire"));
    }
}
