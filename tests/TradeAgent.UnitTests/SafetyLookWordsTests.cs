using System.Text.RegularExpressions;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (g) THE SAFETY PAGE SAYS WHAT THE LOOK SETTING REALLY IS (<c>U-reconcile-wakes</c>, owed by <c>U-quiet-review</c>).
/// The setting is the FASTEST the AI is woken to look around: the look slows while nothing happens, and a role is
/// looked at only while it has open work it owns. A page that said "this is how often it is woken" described a clock
/// the app no longer runs.
/// </summary>
public class SafetyLookWordsTests
{
    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>
    /// RED at base 604a3133: the label read "Wake the AI to look around every, minutes" and the paragraph "This is how
    /// often it is woken to look around anyway".
    /// </summary>
    [Fact]
    public void The_safety_page_says_the_look_is_the_fastest_pace()
    {
        Assert.Contains("at most", Labels.ReviewEvery, StringComparison.Ordinal);

        // The page's words as one string: its literals are split across lines with `" + "`, and the joins are not
        // part of what the owner reads.
        var page = Regex.Replace(
            File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "DashboardView.cs")), "\"\\s*\\+\\s*\"", "");
        Assert.DoesNotContain("This is how often it is woken to", page, StringComparison.Ordinal);
        Assert.Contains("the fastest it is woken to look around", page, StringComparison.Ordinal);
        Assert.Contains("slows while nothing happens", page, StringComparison.Ordinal);
        Assert.Contains("only while it has open work it owns", page, StringComparison.Ordinal);

        // AND THE GUIDE AND THE CONTRACT SAY THE SAME.
        var guide = File.ReadAllText(Path.Combine(Repo(), "docs", "USER-GUIDE.md"));
        Assert.Contains("the fastest it is woken to look around", guide, StringComparison.Ordinal);
        var contracts = File.ReadAllText(Path.Combine(Repo(), "docs", "CONTRACTS.md"));
        Assert.Contains("**Each role is woken for what it owns.**", contracts, StringComparison.Ordinal);
    }
}
