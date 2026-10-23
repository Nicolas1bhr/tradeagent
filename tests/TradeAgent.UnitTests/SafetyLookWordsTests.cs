using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (g) THE BOARD PAGE SAYS WHAT THE LOOK SETTING REALLY IS (<c>U-reconcile-wakes</c>, owed by <c>U-quiet-review</c>).
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
    /// often it is woken to look around anyway". Re-targeted by <c>U-settings-shed</c>: the interval left the owner's page with
    /// the board's decisions, and its words now stand on the Board page — "at most every", never "every".
    /// </summary>
    [Fact]
    public void The_board_page_says_the_look_is_the_fastest_pace()
    {
        Assert.Contains("at most", Labels.ReviewEvery, StringComparison.Ordinal);

        // The page's paragraph left with its box (U-board-a2: the review interval is the security board's policy); what is left of
        // the page must not say the old words either.
        var page = Regex.Replace(
            File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "OwnerView.cs")), "\"\\s*\\+\\s*\"", "");
        Assert.DoesNotContain("This is how often it is woken to", page, StringComparison.Ordinal);
        Assert.Contains("at most every", Labels.BoardField("review_minutes")!.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("how often", Labels.BoardField("review_minutes")!.Label, StringComparison.OrdinalIgnoreCase);

        // AND THE GUIDE AND THE CONTRACT SAY THE SAME.
        var guide = File.ReadAllText(Path.Combine(Repo(), "docs", "USER-GUIDE.md"));
        Assert.Contains("the fastest it is woken to look around", guide, StringComparison.Ordinal);
        var contracts = File.ReadAllText(Path.Combine(Repo(), "docs", "CONTRACTS.md"));
        Assert.Contains("**Each role is woken for what it owns.**", contracts, StringComparison.Ordinal);
    }
}
