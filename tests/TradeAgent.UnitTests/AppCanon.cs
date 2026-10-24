using TradeAgent.AgentRuntime;
using TradeAgent.Core;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE APP'S HARNESS CANON FOR A HOME A TEST MADE BY HAND (<c>U-exec-env-fix</c> item 2). A harness turn runs only on the canon the app
/// wrote into that home — there is no fallback text — so a test that hands a conversation a folder of its own first has the app's record
/// keep a canon for it, as a start does (<see cref="AppFileManifest.Keep"/>). The text is the one the old fallback sent, so a test that
/// sizes a request body measures what it measured before.
/// </summary>
internal static class AppCanon
{
    /// <summary>Keeps the canon for <paramref name="role"/> in <paramref name="home"/> on <paramref name="files"/> (the shared record when null); returns the home.</summary>
    internal static string Keep(string home, string role, AppFileManifest? files = null)
    {
        (files ?? AppFileManifest.Shared).Keep(CouncilRoles.HomeDir(role), Canon.HarnessFile, $"You are the {CouncilRoles.Title(role)}.", home);
        return home;
    }
}
