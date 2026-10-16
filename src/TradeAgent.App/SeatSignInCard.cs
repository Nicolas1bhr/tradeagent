using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE AI SIGN-IN CARD IS HANDED, as delegates, so the card the owner sees and the card a test presses are one control over
/// different sources (the <see cref="PerceptionSources"/> arrangement). Nothing here reaches the agent-facing pipe.
/// </summary>
sealed record SeatSignInSources
{
    /// <summary>The app's one sign-in into its seat home: <see cref="AppHost.SeatSignIn"/>.</summary>
    public required SeatSignIn Service { get; init; }

    /// <summary>Opens an address in the owner's browser — the app opens it, so the owner never copies a link out of anything.</summary>
    public required Action<string> Open { get; init; }

    /// <summary>The activity log. Told what a press did — never a key, never any part of one.</summary>
    public required Action<string> Activity { get; init; }

    /// <summary>How work that finished off the window's thread gets back onto it: the dispatcher in the app.</summary>
    public required Action<Action> Ui { get; init; }

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>
/// THE DASHBOARD'S "AI sign-in" CARD (<c>U-seat-signin</c>; <c>docs/ORCHESTRATION.md</c> § 2.7): whenever the seat home holds no
/// credential — or nobody can tell — the owner signs TradeAgent's AI in from here, at any time, never from a terminal.
///
/// <list type="bullet">
/// <item><b>Visible iff</b> the last status reading is signed out or unknown; it disappears when status answers signed in.</item>
/// <item><b>Three ways in</b>, each a <see cref="SeatSignIn"/> call run off the window's thread: the browser sign-in (the app opens
/// the address), a one-time code for another device (the address, the code and when it expires, a Retry once it lapses), and a
/// pasted key (masked, handed to the runtime's stdin, the field emptied at once, never shown back or logged).</item>
/// <item><b>Sign out</b> on its signed-in face (<see cref="SignedIn"/>), TWO presses: it removes permission (CLAUDE.md).</item>
/// </list>
///
/// <para><b>Built once and updated in place.</b> <see cref="Update"/> runs on the five-second tick and sets text and visibility;
/// it never adds or removes a control, so a half-typed key, a code being read and a half-pressed sign-out all survive it.</para>
/// </summary>
sealed class SeatSignInCard
{
    /// <summary>The armed sentence of the one control here that removes permission.</summary>
    public const string SignOut = "Sign out of TradeAgent's AI";

    public const string SignOutArmed = "Confirm: sign TradeAgent's AI out — it stops working until you sign in again";

    public const string Browser = "Sign in in your browser";
    public const string Code = "Sign in with a code on another device";
    public const string Retry = "Get a new code";
    public const string Key = "Sign in with this key";

    readonly SeatSignInSources _sources;

    readonly TextBlock _headline = Ui.Body("");
    readonly TextBlock _note = Ui.Body("");
    readonly Button _browser;
    readonly Button _code;
    readonly Control _keyRow;
    readonly TextBox _keyField;
    readonly Button _keyButton;
    readonly StackPanel _browserWell;
    readonly TextBlock _browserUrl = Ui.Mono("");
    readonly StackPanel _codeWell;
    readonly TextBlock _codeUrl = Ui.Mono("");
    readonly TextBlock _codeText;
    readonly TextBlock _codeExpiry = Ui.Micro("");
    readonly Button _retry;
    readonly Control _working;

    DeviceChallenge? _challenge;
    DateTimeOffset _challengeAt;
    string? _browserAddress;
    bool _busy;

    /// <summary>The card the dashboard shows while the seat home is signed out or unknown.</summary>
    public Border Root { get; }

    /// <summary>The card's signed-in face: one line and the two-press sign-out, visible iff status answered signed in.</summary>
    public Control SignedIn { get; }

    /// <summary>The sign-out control itself, so a test presses what the owner presses.</summary>
    public Button SignOutButton { get; }

    /// <summary>The press in flight, for a test to wait on; null before the first.</summary>
    public Task? Pressing { get; private set; }

    public SeatSignInCard(SeatSignInSources sources)
    {
        _sources = sources;

        _browser = Ui.Primary(Browser, StartBrowser);
        _code = Ui.Secondary(Code, StartCode);
        _retry = Ui.Secondary(Retry, StartCode);

        _keyField = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = "Or paste an OpenAI API key",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Width = double.NaN
        };
        _keyButton = Ui.Secondary(Key, UseKey);
        _keyRow = Ui.Col(Theme.S2,
            Ui.Eyebrow("Or use a key"),
            _keyField,
            Ui.With(Ui.Row(Theme.S2, _keyButton), r => r.Margin = new Thickness(0, Theme.S1, 0, 0)),
            Ui.Micro("TradeAgent hands the key straight to the AI tool, in its own folder, and keeps no copy of it."));

        _browserUrl.TextWrapping = TextWrapping.Wrap;
        _browserWell = Ui.Col(Theme.S2,
            Ui.Muted("Finish signing in in the browser window TradeAgent opened. If none appeared, open this address:"),
            Well(_browserUrl));
        _browserWell.IsVisible = false;

        _codeUrl.TextWrapping = TextWrapping.Wrap;
        _codeText = new TextBlock
        {
            FontFamily = Theme.Mono,
            FontSize = Theme.Display,
            FontWeight = FontWeight.SemiBold,
            Foreground = Theme.Text,
            LetterSpacing = 3
        };
        _codeWell = Ui.Col(Theme.S2,
            Ui.Eyebrow("On your phone or another computer, open this address"),
            Well(_codeUrl),
            Ui.Eyebrow("And enter this code"),
            Well(_codeText),
            _codeExpiry,
            Ui.With(Ui.Row(Theme.S2, _retry), r => r.Margin = new Thickness(0, Theme.S1, 0, 0)));
        _codeWell.IsVisible = false;

        _working = Ui.Busy("Waiting for the AI tool…");
        _working.IsVisible = false;
        _note.IsVisible = false;
        _note.TextWrapping = TextWrapping.Wrap;
        _headline.TextWrapping = TextWrapping.Wrap;

        Root = new Border
        {
            Background = Theme.CautionSoft,
            BorderBrush = Theme.Caution,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S5),
            IsVisible = false,
            Child = Ui.Col(Theme.S4,
                Ui.With(Ui.Eyebrow("AI sign-in"), t => t.Foreground = Theme.Caution),
                _headline,
                Ui.Row(Theme.S2, _browser, _code),
                _browserWell,
                _codeWell,
                _working,
                _note,
                Ui.Divider(),
                _keyRow)
        };

        SignOutButton = Ui.Confirm(SignOut, SignOutArmed, StartSignOut);
        SignOutButton.HorizontalAlignment = HorizontalAlignment.Left;
        SignedIn = Ui.Col(Theme.S2,
            Ui.Micro("TradeAgent's AI is signed in, in a folder of its own — separate from your own AI tools."),
            SignOutButton);
        SignedIn.IsVisible = false;

        Update();
    }

    static Border Well(Control child) => new()
    {
        Background = Theme.BgSunken,
        CornerRadius = Theme.RadiusSm,
        Padding = new Thickness(Theme.S4, Theme.S3),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = child
    };

    /// <summary>
    /// THE FIVE-SECOND PASS: text and visibility from the last reading, nothing built. Called by the dashboard's tick and after
    /// every press settles.
    /// </summary>
    public void Update()
    {
        var reading = _sources.Service.State();
        var wanted = reading.State is SeatSignInState.SignedOut or SeatSignInState.Unknown;
        Root.IsVisible = wanted;
        SignedIn.IsVisible = reading.State is SeatSignInState.SignedIn;
        // A SIGN-IN THAT FINISHED leaves nothing of itself behind for the next time the card appears.
        if (!wanted) { _challenge = null; _browserAddress = null; }

        var (browser, code, key) = _sources.Service.Offers();
        // NO PROMISE OF A WAY IN THE CARD CANNOT OFFER: an unreadable catalogue names no runtime to sign in.
        _headline.Text = reading.State is SeatSignInState.Unknown
            ? $"TradeAgent could not tell whether its AI is signed in: {(reading.Reason ?? "the AI tool did not say").TrimEnd('.')}." +
              (browser || code || key ? " You can sign it in below." : "")
            : _sources.Service.SignedOutWords();

        _browser.IsVisible = browser;
        _code.IsVisible = code && _challenge is null;
        _keyRow.IsVisible = key;
        _browser.IsEnabled = _code.IsEnabled = _retry.IsEnabled = _keyButton.IsEnabled = !_busy;
        _working.IsVisible = _busy;

        _browserWell.IsVisible = _browserAddress is not null;
        _browserUrl.Text = _browserAddress ?? "";

        _codeWell.IsVisible = _challenge is not null;
        if (_challenge is { } c)
        {
            _codeUrl.Text = c.Url;
            _codeText.Text = c.Code;
            var expires = _challengeAt + c.ExpiresIn;
            var lapsed = _sources.Now() >= expires;
            var ended = !lapsed && !_busy && !_sources.Service.SigningIn;
            _codeExpiry.Text = lapsed
                ? $"This code expired at {expires.ToLocalTime():HH:mm}. Get a new one to try again."
                : ended
                    ? "The sign-in with this code ended without finishing. Get a new code to try again."
                    : $"This code expires at {expires.ToLocalTime():HH:mm}.";
            _codeExpiry.Foreground = lapsed || ended ? Theme.Caution : Theme.TextMuted;
            // THE RETRY ONLY ONCE IT CAN HELP: while the code is live, a new one would end the sign-in the owner is typing.
            _retry.IsVisible = lapsed || ended;
        }
    }

    void Say(string? words, bool problem)
    {
        _note.IsVisible = words is not null;
        _note.Text = words ?? "";
        _note.Foreground = problem ? Theme.Caution : Theme.TextMuted;
    }

    /// <summary>
    /// ONE PRESS'S WORK, OFF THE WINDOW'S THREAD, and its answer back on it: the card says it is waiting, the call runs on the pool,
    /// and what it returned — or the words of what it refused — is applied through <see cref="SeatSignInSources.Ui"/>. After every
    /// press the status is read again, so a sign-in that took makes the card go away on the next pass.
    /// </summary>
    void Press<T>(Func<Task<T>> work, Action<T> done, Func<string, string>? scrub = null)
    {
        _busy = true;
        Say(null, false);
        Update();
        Pressing = Task.Run(async () =>
        {
            T result = default!;
            string? failed = null;
            try { result = await work(); }
            catch (Exception ex)
            {
                // The program's own words where the runtime gave some; the catalogue's sentence for a bare code.
                var words = ex is TradeAgentException t && t.Message == t.Code.ToString()
                    ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim()
                    : ex.Message;
                failed = scrub is null ? words : scrub(words);
            }
            try { await _sources.Service.RefreshAsync(); } catch (Exception) { /* the next pass reads it */ }
            _sources.Ui(() =>
            {
                _busy = false;
                if (failed is null) done(result); else Say(failed, problem: true);
                Update();
            });
        });
    }

    void StartBrowser() => Press(() => _sources.Service.BeginBrowserAsync(), challenge =>
    {
        _challenge = null;
        if (challenge.Url is { Length: > 0 } url)
        {
            _browserAddress = url;
            _sources.Open(url);
            _sources.Activity($"Started signing TradeAgent's AI in with {_sources.Service.DisplayName} in the browser");
        }
        else Say(challenge.Message, problem: false);
    });

    void StartCode()
    {
        // THE OLD CODE GOES AT ONCE: it is stopped by the new start, and a refusal must not leave a dead code on the screen.
        _challenge = null;
        StartCodePress();
    }

    void StartCodePress() => Press(() => _sources.Service.BeginDeviceCodeAsync(), challenge =>
    {
        _browserAddress = null;
        _challenge = challenge;
        _challengeAt = _sources.Now();
        _sources.Activity($"Started signing TradeAgent's AI in with a code on another device ({_sources.Service.DisplayName})");
    });

    /// <summary>
    /// THE PASTED KEY: taken out of the field and the field emptied BEFORE anything runs, handed to the runtime's stdin, and never
    /// put back into any word this card shows or logs — a refusal that quotes any piece of what was pasted has it cleared
    /// (<see cref="PastedKey.Redact"/>), on top of the runtime's own clearing.
    /// </summary>
    void UseKey()
    {
        var pasted = _keyField.Text ?? "";
        _keyField.Text = "";
        var key = pasted.Trim();
        if (key.Length == 0) { Say("Paste a key into the box first.", problem: true); return; }
        Press(async () => { await _sources.Service.SignInWithKeyAsync(key); return true; },
            _ => _sources.Activity($"Signed TradeAgent's AI in with a pasted key ({_sources.Service.DisplayName}); TradeAgent kept no copy"),
            words => PastedKey.Redact(words, pasted));
    }

    Task StartSignOut()
    {
        Press(async () => { await _sources.Service.SignOutAsync(); return true; },
            _ => _sources.Activity($"Signed TradeAgent's AI out of {_sources.Service.DisplayName}, on the owner's two presses"));
        return Task.CompletedTask;
    }
}
