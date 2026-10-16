using System.Text;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>What the seat home's sign-in status said, as the dashboard reads it.</summary>
public enum SeatSignInState
{
    /// <summary>Not read yet, or the chosen runtime keeps no seat home of TradeAgent's own: nothing to show.</summary>
    NotApplicable,

    /// <summary>The status command, under the seat home, answered signed in (exit 0).</summary>
    SignedIn,

    /// <summary>It answered not signed in.</summary>
    SignedOut,

    /// <summary>It could not be asked — no program, a program that is not the pin, a refused seat home, a deadline. Said as such.</summary>
    Unknown
}

/// <summary>One reading of the seat home's sign-in, and why it is <see cref="SeatSignInState.Unknown"/> when it is.</summary>
public sealed record SeatSignInReading(SeatSignInState State, string? Reason);

/// <summary>A sign-in with a one-time code: where to type it, the code, and how long it lives from when it was printed.</summary>
public sealed record DeviceChallenge(string Url, string Code, TimeSpan ExpiresIn);

/// <summary>
/// WHAT A PASTED KEY MAY NEVER LEAVE IN A WORD (CRED-001; <c>U-seat-signin</c> fix 1): the refusal, the card and the activity log
/// carry no piece of it in any form.
///
/// <list type="bullet">
/// <item><b>A key has no space or line break inside it.</b> One that does was copied over two lines; it is refused in
/// <see cref="BrokenWords"/> before it reaches any program, so no program can print half of it back.</item>
/// <item><b>Every word a program said is cleared first and cut to one line after</b>: the whole key wherever it appears, every
/// whitespace-separated piece of what was pasted of <see cref="Piece"/> characters or more, and every run of <see cref="Run"/> or more
/// consecutive characters of any piece — a program that echoes the key from its fifth character, or wraps it over its own lines, is
/// cleared as surely as one that echoes it whole. Each cleared stretch reads <see cref="Shown"/>.</item>
/// </list>
/// A piece under four characters is not cleared on its own (a key's public prefix is three, and clearing letters that short would
/// erase ordinary words); a run under eight is not either — the industry's own masks show four.
/// </summary>
public static class PastedKey
{
    public const string Shown = "(the key)";
    public const int Run = 8;
    public const int Piece = 4;

    public const string BrokenWords =
        "The key you pasted has a space or a line break inside it, and a key has none — it was probably copied over two lines. " +
        "Nothing was signed in and the key was handed to nothing; copy it again in one piece.";

    /// <summary>True when what was pasted, its ends trimmed, still has whitespace inside.</summary>
    public static bool Broken(string pasted) => pasted.Trim().Any(char.IsWhiteSpace);

    /// <summary>The text with every piece of the pasted key cleared, as the class says.</summary>
    public static string Redact(string text, string pasted)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(pasted)) return text;
        var pieces = pasted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hide = new bool[text.Length];
        var runs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in pieces)
        {
            if (pieces.Length == 1 || piece.Length >= Piece)
                for (var at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + 1, StringComparison.Ordinal))
                    Array.Fill(hide, true, at, piece.Length);
            for (var i = 0; i + Run <= piece.Length; i++) runs.Add(piece.Substring(i, Run));
        }
        for (var i = 0; i + Run <= text.Length; i++)
            if (runs.Contains(text.Substring(i, Run))) Array.Fill(hide, true, i, Run);

        var cleared = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (!hide[i]) { cleared.Append(text[i++]); continue; }
            cleared.Append(Shown);
            while (i < text.Length && hide[i]) i++;
        }
        return cleared.ToString();
    }
}

/// <summary>
/// THE APP'S ONE SIGN-IN INTO ITS SEAT HOME, after onboarding as much as during it (<c>U-seat-signin</c>; <c>docs/ORCHESTRATION.md</c>
/// § 2.7: "ONE sign-in … in the app"). It holds the last reading of the seat home's status — read OFF the window's thread, by the
/// runtime's own status command under the seat home, on the app's background cadence — and the three ways in the card offers:
/// the browser, a one-time code typed on another device, and a pasted key; and the sign-out.
///
/// <para>Every one of them is a <see cref="CliAgentRuntime"/> call, so every process it starts is the runtime's: no window, the
/// pinned program, the seat home applied last. It keeps no key: a pasted key goes straight to the runtime's stdin and this class
/// holds no field it could sit in. Nothing here reads, copies or names the owner's own <c>~/.codex</c>.</para>
///
/// <para>Operator authority, in-process only: nothing on the agent-facing pipe reaches this object.</para>
/// </summary>
/// <param name="manifest">
/// The runtime the owner chose, read at every call — the settings change while the app runs. Null is no runtime chosen (nothing to
/// show); a throw is a catalogue that could not answer — an unreadable <c>runtimes.json</c>, a chosen runtime it cannot name — and
/// reads <see cref="SeatSignInState.Unknown"/> with the refusal's words, never as nothing to show.
/// </param>
/// <param name="presence">
/// The register a sign-in's processes report to: null is the process-wide one, which is what the product passes; a test passes its
/// own, for the reason <see cref="CliAgentRuntime"/>'s own parameter gives.
/// </param>
public sealed class SeatSignIn(Func<RuntimeManifest?> manifest, AgentPresence? presence = null) : IDisposable
{
    readonly object _gate = new();
    CliAgentRuntime? _runtime;
    string? _signature;
    SeatSignInReading _reading = new(SeatSignInState.NotApplicable, null);
    Task<SeatSignInReading>? _inFlight;

    /// <summary>The last reading. Cheap — no process — so the five-second tick may ask it as often as it likes.</summary>
    public SeatSignInReading State()
    {
        lock (_gate) return _reading;
    }

    /// <summary>True while the card has something to say: the seat home is signed out, or nobody can tell.</summary>
    public bool Wanted => State().State is SeatSignInState.SignedOut or SeatSignInState.Unknown;

    /// <summary>True while a sign-in this app started is still waiting for the owner.</summary>
    public bool SigningIn
    {
        get { lock (_gate) return _runtime?.SigningIn ?? false; }
    }

    /// <summary>
    /// READS THE STATUS AGAIN, on the thread pool, never the caller's: the window's thread presses and refreshes, and a codex start
    /// is a wait it must not make. One read at a time — a second caller is handed the read in flight.
    /// </summary>
    public Task<SeatSignInReading> RefreshAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false } running) return running;
            return _inFlight = Task.Run(async () =>
            {
                SeatSignInReading read;
                try
                {
                    read = Runtime() is { } rt
                        ? await rt.ReadSignInAsync(ct)
                        : new SeatSignInReading(SeatSignInState.NotApplicable, null);
                }
                catch (OperationCanceledException) { return State(); }
                catch (Exception ex) { read = new SeatSignInReading(SeatSignInState.Unknown, ex.Message); }
                lock (_gate) _reading = read;
                return read;
            }, CancellationToken.None);
        }
    }

    /// <summary>The runtime's own sign-in, which on a desktop prints the address the app opens in the browser.</summary>
    public Task<AuthChallenge> BeginBrowserAsync(CancellationToken ct = default) =>
        Task.Run(() => Require().BeginAuthenticationAsync(ct), CancellationToken.None);

    /// <summary>The sign-in with a one-time code — again, for a Retry: the code that lapsed is stopped first.</summary>
    public Task<DeviceChallenge> BeginDeviceCodeAsync(CancellationToken ct = default) =>
        Task.Run(() => Require().BeginDeviceCodeAsync(ct), CancellationToken.None);

    /// <summary>A pasted key, handed to the runtime's stdin — a sign-in still waiting is stopped first. Not kept here or anywhere.</summary>
    public Task SignInWithKeyAsync(string key, CancellationToken ct = default) =>
        Task.Run(() => Require().SignInWithApiKeyAsync(key, ct), CancellationToken.None);

    /// <summary>Removes the seat home's credential. The card asks twice before it calls this.</summary>
    public Task SignOutAsync(CancellationToken ct = default) =>
        Task.Run(() => Require().SignOutAsync(ct), CancellationToken.None);

    /// <summary>Which of the three ways in the chosen runtime offers.</summary>
    public (bool Browser, bool Code, bool Key) Offers()
    {
        if (Manifest() is not { Seat: not null } m) return (false, false, false);
        // ON A HOST WHOSE OWN SIGN-IN IS ALREADY A CODE (Linux: `login --device-auth`), there is no browser sign-in to offer.
        return (m.AuthArgs.Length > 0 && m.AuthCodePattern is null, m.DeviceCode is { Args.Length: > 0 },
            m.ApiKey is { StdinArgs.Length: > 0 } or { File.Length: > 0 });
    }

    /// <summary>The words the seat profile gives a signed-out seat home, or a plain sentence where it has none.</summary>
    public string SignedOutWords() =>
        Manifest()?.Seat?.SignedOut is { Length: > 0 } words ? words : "TradeAgent keeps its own AI sign-in. Sign in again.";

    /// <summary>The chosen runtime's name, for the card's sentences.</summary>
    public string DisplayName => Manifest()?.DisplayName ?? "the AI";

    RuntimeManifest? Manifest()
    {
        try { return manifest(); }
        catch (Exception) { return null; }
    }

    CliAgentRuntime Require() =>
        Runtime() ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
            "The AI you chose keeps no sign-in of TradeAgent's own, so there is nothing to sign in here.");

    /// <summary>
    /// ONE RUNTIME OBJECT PER MANIFEST, KEPT: a sign-in in progress is a process this object holds, and a Retry or a sign-out must
    /// reach the same one. A changed manifest — another runtime chosen, or <c>runtimes.json</c> edited — is a new one, and the old
    /// one's waiting sign-in is stopped.
    /// </summary>
    CliAgentRuntime? Runtime()
    {
        // THE CATALOGUE'S REFUSAL GOES THROUGH: a read turns it into Unknown with its words, a press into the card's words.
        if (manifest() is not { Seat: not null } m) return null;
        var signature = Json.Write(m);
        lock (_gate)
        {
            if (_runtime is not null && signature == _signature) return _runtime;
            _ = _runtime?.StopAsync();
            _signature = signature;
            return _runtime = new CliAgentRuntime(m, presence: presence);
        }
    }

    /// <summary>Stops a sign-in still waiting. The app's quit calls it; nothing is signed out.</summary>
    public void Dispose()
    {
        CliAgentRuntime? held;
        lock (_gate) { held = _runtime; _runtime = null; }
        try { held?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception) { /* a quit goes on */ }
    }
}
