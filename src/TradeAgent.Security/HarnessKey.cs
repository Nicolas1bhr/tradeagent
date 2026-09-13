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
/// a ledger, a report, a Situation or the agent's environment; the only thing that leaves this class is
/// <see cref="Held"/>, which is a boolean.</para>
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

    /// <summary>Whether a key is held. The only fact about it anything outside this class may learn.</summary>
    public bool Held
    {
        get { lock (_gate) return _key is { Length: > 0 }; }
    }

    /// <summary>
    /// Takes what the owner pasted, trimmed. Whitespace only is a CLEAR rather than a key of spaces —
    /// an empty box means "I have not given you one", and storing that as a credential would produce a
    /// worker that starts and is refused by the provider.
    /// </summary>
    public void Set(string? key)
    {
        var trimmed = key?.Trim();
        lock (_gate) _key = trimmed is { Length: > 0 } ? trimmed : null;
    }

    /// <summary>Forgets it. Called when the owner presses clear and when the app closes.</summary>
    public void Clear()
    {
        lock (_gate) _key = null;
    }

    /// <summary>
    /// The key, for the one caller that has to send it, or null when none is held.
    ///
    /// It is a method rather than a property so that reading a credential looks like an act at the call
    /// site. There is exactly one caller in the product — the harness's request builder — and there is
    /// deliberately no formatter, no <c>ToString</c> and no logging helper on this class that could put
    /// the value anywhere else.
    /// </summary>
    public string? Read()
    {
        lock (_gate) return _key;
    }
}
