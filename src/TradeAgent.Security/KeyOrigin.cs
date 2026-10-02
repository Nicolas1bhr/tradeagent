using TradeAgent.Core;

namespace TradeAgent.Security;

/// <summary>
/// WHERE A KEY GOES, SPELLED ONE WAY: the scheme, the host and the port of a URL, and nothing else
/// (<c>U-key-host-pin</c>).
///
/// <para><b>The rule itself is <see cref="UrlOrigin"/>, in Core, and this answers through it
/// unchanged.</b> <c>docs/EDGE-FACTORY.md</c> § 6.10 makes the origin a key may be released to and the
/// origin a tape row must come from to be <c>O-LIVE</c> the SAME rule, and Core — where the tape's
/// store lives — cannot reference this assembly. So the one definition moved down a layer
/// (<c>U-tape-store</c>), and this name stays where every caller and test of the key holder already
/// reaches it. Why an origin and not a host, and why not <see cref="Uri.GetLeftPart"/>, is written on
/// <see cref="UrlOrigin"/>, beside the code it explains.</para>
/// </summary>
public static class KeyOrigin
{
    /// <summary>The origin of <paramref name="url"/>, or null where it has none a key could go to.</summary>
    public static string? Of(string? url) => UrlOrigin.Of(url);
}
