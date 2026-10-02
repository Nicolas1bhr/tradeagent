namespace TradeAgent.Security;

/// <summary>
/// THE PROVIDER KEY THE APP-OWNED HARNESS SENDS, HELD IN MEMORY AND NOWHERE ELSE.
///
/// <para><b>Why not on disk, when <see cref="SecretStore"/> is right there.</b> Round 4 of
/// <c>docs/COUNCIL.md</c>: keys "are not retained beside an unsandboxed CLI process until containment
/// lands". <c>U-containment</c> landed and closed the job object, the environment whitelist and the
/// per-launch grant — and left the thing that decides this open, in its own words: the agent is the
/// SAME OS USER, so same-user reads and writes of <c>state/</c> stand, and
/// <c>Containment.Sandbox()</c> answers <c>NONE</c> on every platform this builds on. A key written
/// under <c>%LOCALAPPDATA%\TradeAgent</c> is a key the vendor CLI's own process can read. So it is not
/// written, and the owner pastes it again after a restart. That is a real cost, it is stated beside the
/// box, and it is the honest trade until <c>U-contain-2</c>.</para>
///
/// <para><b>What holding it in memory does and does not buy.</b> It is not secrecy from the operating
/// system — a debugger on this process reads it, and so does a core dump. It buys the one thing that
/// matters here: there is no FILE, so a second process running as the same user has nothing to open,
/// and a backup, a sync client or a support archive cannot carry it away. The value never enters a log,
/// a ledger, a report, a Situation or the agent's environment; what leaves this class is
/// <see cref="Held"/>, a boolean, and <see cref="Origin"/>, an address.</para>
///
/// <para><b>And it is held FOR ONE ORIGIN</b> (<c>U-key-host-pin</c>). Where a request goes is
/// manifest data, and the manifest can be overridden by <c>runtimes.json</c> — a file the vendor CLI,
/// running unconfined as the same user, can write. Until this unit a key pasted after a restart went
/// wherever that file said. Now the key is bound to the origin the Safety page showed the owner when
/// they pasted it (<see cref="KeyOrigin"/>: scheme, host and port), and it leaves this class only
/// through <see cref="ReadFor"/>, for a request to that same origin. Any other origin — another port on
/// the same host, <c>http://</c> for <c>https://</c> — gets a typed refusal instead, and the key is
/// FORGOTTEN on the spot: a key that something tried to send elsewhere is a key the owner should look
/// at again before it goes anywhere.</para>
///
/// <para><b>Presence is not a send.</b> <see cref="Held"/> answers the health row, the auth state and
/// both <c>StartAsync</c>s, and it never clears anything: a check that cleared the key would make a
/// health poll the thing that loses it.</para>
///
/// <para>Static, for the reason <c>AgentGrants.Shared</c> is: the half that takes the paste (the Safety
/// page) and the half that sends the request (the harness) are different objects in different
/// assemblies, and a key set on one instance and read from another is a worker that will not start.
/// A test that needs its own gets one with <c>new</c>.</para>
/// </summary>
public sealed class HarnessKey
{
    /// <summary>The process-wide holder — the one the Safety page writes and the harness reads.</summary>
    public static HarnessKey Shared { get; } = new();

    readonly Lock _gate = new();
    string? _key;
    string? _origin;

    /// <summary>
    /// Whether a key is held — the PRESENCE check, and the only fact about the key itself anything
    /// outside this class may learn. Reading it never clears anything.
    /// </summary>
    public bool Held
    {
        get { lock (_gate) return _key is { Length: > 0 }; }
    }

    /// <summary>
    /// The origin the held key was pasted for, or null when none is held. An address, not a
    /// credential: the Safety page shows it beside the box.
    /// </summary>
    public string? Origin
    {
        get { lock (_gate) return _key is { Length: > 0 } ? _origin : null; }
    }

    /// <summary>
    /// Takes what the owner pasted, trimmed, FOR the address the box showed them. Whitespace only is a
    /// CLEAR rather than a key of spaces — an empty box means "I have not given you one", and storing
    /// that as a credential would produce a worker that starts and is refused by the provider.
    ///
    /// An address with no origin (<see cref="KeyOrigin.Of"/> answers null) is a clear too: a key bound
    /// to nowhere can never be released, and holding it would make <see cref="Held"/> say "ready" about
    /// a worker that cannot send anything.
    /// </summary>
    /// <param name="key">What was pasted.</param>
    /// <param name="pastedFor">The address the owner was shown — a URL or an origin; only its origin is kept.</param>
    public void Set(string? key, string? pastedFor)
    {
        var trimmed = key?.Trim();
        var origin = KeyOrigin.Of(pastedFor);
        lock (_gate)
        {
            if (trimmed is { Length: > 0 } && origin is not null) (_key, _origin) = (trimmed, origin);
            else (_key, _origin) = (null, null);
        }
    }

    /// <summary>Forgets it. Called when the owner presses clear and when the app closes.</summary>
    public void Clear()
    {
        lock (_gate) (_key, _origin) = (null, null);
    }

    /// <summary>
    /// THE KEY, FOR A REQUEST TO ONE ORIGIN — the one way it leaves this class, and only the SEND path
    /// calls it.
    ///
    /// <para>The origin the caller presents is the one its request is about to go to. When it is the
    /// origin the key was pasted for, the key comes back. When it is not — or has no origin at all —
    /// the key is cleared inside the same lock and a <see cref="KeyRefusal"/> naming both origins comes
    /// back instead, so the caller can tell the owner what happened without ever having held the key.
    /// With nothing held the answer is <see cref="KeyRelease.NoneHeld"/>, which is neither.</para>
    ///
    /// <para>It is a method rather than a property so that reading a credential looks like an act at
    /// the call site. There is exactly one caller in the product — the harness's request path — and
    /// there is deliberately no formatter, no <c>ToString</c> and no logging helper on this class, or
    /// on <see cref="KeyRelease"/>, that could put the value anywhere else.</para>
    /// </summary>
    /// <param name="origin">Where the request is going: a URL or an origin; it is compared as an origin.</param>
    public KeyRelease ReadFor(string? origin)
    {
        var asked = KeyOrigin.Of(origin);
        lock (_gate)
        {
            if (_key is not { Length: > 0 } key) return KeyRelease.NoneHeld;
            if (asked is not null && string.Equals(asked, _origin, StringComparison.Ordinal))
                return KeyRelease.For(key);

            var pastedFor = _origin ?? "";
            (_key, _origin) = (null, null);
            return KeyRelease.Refused(new KeyRefusal(pastedFor, asked));
        }
    }
}

/// <summary>
/// WHAT <see cref="HarnessKey.ReadFor"/> ANSWERED: the key, a refusal, or nothing held — exactly one.
///
/// A class rather than a record ON PURPOSE: a record writes every property into its generated
/// <c>ToString</c>, and this one carries the key. <see cref="object.ToString"/> here is the type's name.
/// </summary>
public sealed class KeyRelease
{
    KeyRelease(string? key, KeyRefusal? refusal) => (Key, Refusal) = (key, refusal);

    /// <summary>No key is held. Not a refusal: nothing was withheld, because there was nothing.</summary>
    public static KeyRelease NoneHeld { get; } = new(null, null);

    internal static KeyRelease For(string key) => new(key, null);

    internal static KeyRelease Refused(KeyRefusal refusal) => new(null, refusal);

    /// <summary>The key, when the origin asked for is the one it was pasted for; otherwise null.</summary>
    public string? Key { get; }

    /// <summary>Why it was withheld, when one was held for another origin. By then it is forgotten.</summary>
    public KeyRefusal? Refusal { get; }
}

/// <summary>
/// A KEY WITHHELD, IN THE TWO ADDRESSES THAT EXPLAIN IT. Neither is a credential, so this is a record.
/// </summary>
/// <param name="PastedFor">The origin the owner pasted the key for.</param>
/// <param name="PointsAt">
/// The origin the request was about to go to, or null where that address has no origin a key could go to.
/// </param>
public sealed record KeyRefusal(string PastedFor, string? PointsAt);
