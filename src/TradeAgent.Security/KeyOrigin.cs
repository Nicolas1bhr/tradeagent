namespace TradeAgent.Security;

/// <summary>
/// WHERE A KEY GOES, SPELLED ONE WAY: the scheme, the host and the port of a URL, and nothing else
/// (<c>U-key-host-pin</c>).
///
/// <para><b>Why an origin and not a host.</b> Two listeners on one host are two destinations — a
/// local program on another port is not the provider — and <c>http://</c> for <c>https://</c> is the
/// same address with the key in clear text on the wire. So the comparison is all three, and the
/// default port is written the way the browser writes it: <c>https://x:443</c> is <c>https://x</c>,
/// because it is the same place.</para>
///
/// <para><b>Why not <see cref="Uri.GetLeftPart"/>.</b> Measured on .NET 10.0.400 / macOS 26.5.1:
/// <c>GetLeftPart(UriPartial.Authority)</c> keeps the user-info, so
/// <c>https://&lt;provider&gt;@other.host/v1</c> came back as a string that STARTS with the provider's
/// name and goes to <c>other.host</c> — exactly what the Safety page must never show the owner as the
/// place their key is going. It also returns a look-alike Unicode host as Unicode: a Cyrillic
/// <c>а</c> in place of a Latin <c>a</c> reads identically on screen. This uses
/// <see cref="Uri.IdnHost"/> instead, which writes that host as <c>xn--…</c>, so two origins that
/// compare unequal also LOOK unequal.</para>
///
/// <para>Only <c>http</c> and <c>https</c> have an origin here. Anything else — a relative path,
/// which .NET on macOS reads as an absolute <c>file://</c> URI, an <c>ftp:</c> address, an empty
/// string — has none, and a key can neither be bound to it nor released for it.</para>
/// </summary>
public static class KeyOrigin
{
    /// <summary>The origin of <paramref name="url"/>, or null where it has none a key could go to.</summary>
    public static string? Of(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        var host = uri.IdnHost;
        if (host.Length == 0) return null;
        if (uri.HostNameType == UriHostNameType.IPv6) host = $"[{host}]";

        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }
}
