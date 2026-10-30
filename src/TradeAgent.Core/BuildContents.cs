namespace TradeAgent.Core;

/// <summary>
/// WHAT THIS INSTALLED BUILD CARRIES, MEASURED FROM ITS OWN FILES (<c>U-windows-release</c>) — never from a switch, a setting or the
/// name of the installer it came out of.
///
/// <para><b>The ATAS adapter is in a build only when its bridge assembly carries it.</b> The bridge's ATAS-facing half
/// (<c>AtasStrategyAdapter</c>) compiles only against ATAS's own assemblies, on a machine with ATAS; a build made anywhere else — every
/// CI build, so every release the release workflow cuts — ships the bridge's protocol half alone, and that build cannot trade through
/// ATAS at all. <c>packaging/build.ps1</c> reads the same marker out of the same file before it names the installer; this is the app
/// reading it out of what was installed. The type name is in the assembly's metadata as ASCII, and nothing else in that assembly can
/// match it: C# string literals are stored as UTF-16 and comments never reach the file.</para>
///
/// <para>A missing or unreadable bridge is a build without the adapter: refuse, never guess — a build that cannot show its adapter is
/// not one that offers ATAS, and not one that an update may treat as carrying it.</para>
/// </summary>
public static class BuildContents
{
    /// <summary>The bridge's ATAS-facing type, whose name is the marker.</summary>
    public const string AtasAdapterType = "AtasStrategyAdapter";

    /// <summary>The bridge assembly the installer places under <c>{app}\bridge\</c>.</summary>
    public const string BridgeAssembly = "TradeAgent.AtasBridge.dll";

    /// <summary>
    /// What the owner reads wherever ATAS would be offered by a build without its adapter — onboarding, Settings, a switch refused.
    /// The installer's welcome page says the same words (<c>packaging/TradeAgent.iss</c>).
    /// </summary>
    public const string AtasNotInThisBuild = "ATAS is not in this build; paper runs on TradeAgent's own venue.";

    /// <summary>Where this running build's bridge was installed: <c>bridge\</c> beside the executable.</summary>
    public static string BridgeDir => Path.Combine(AppContext.BaseDirectory, "bridge");

    static readonly Lazy<bool> Here = new(() => AtasAdapterIn(BridgeDir));

    /// <summary>Whether THIS running build carries the ATAS adapter, read once from its own installed bridge.</summary>
    public static bool AtasAdapterInThisBuild => Here.Value;

    /// <summary>
    /// Whether the bridge assembly in <paramref name="bridgeDir"/> carries the ATAS adapter. False for a missing directory, a missing
    /// or unreadable assembly, and an assembly without the marker.
    /// </summary>
    public static bool AtasAdapterIn(string bridgeDir)
    {
        try
        {
            var dll = Path.Combine(bridgeDir, BridgeAssembly);
            if (!File.Exists(dll)) return false;
            ReadOnlySpan<byte> marker = "AtasStrategyAdapter"u8;
            return File.ReadAllBytes(dll).AsSpan().IndexOf(marker) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
