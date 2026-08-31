using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Gateway;

namespace TradeAgent.App;

/// <summary>
/// The two choices setup makes and then never lets go of again: which trading platform TradeAgent
/// talks to, and which account on it the AI is allowed to see.
///
/// Setup says "you can switch later" while it asks the first of these. Until this page existed that
/// sentence was false — <c>SwitchConnectorAsync</c> and <c>SelectedAccountId</c> were written only by
/// the wizard, the wizard is only entered while setup is unfinished, and the only way to change
/// either afterwards was to edit the database by hand. That is what this page is for.
///
/// Three things it has to get right:
///
/// <b>Widening risk is two-press, narrowing it is one.</b> Moving to ATAS, or pointing the AI at an
/// account that spends real money, arms first and acts on the second press. Moving back to the
/// practice simulator, or picking a simulated account, is one press — hesitating there costs
/// nothing and mis-clicking there costs nothing either.
///
/// <b>Real money never looks like practice.</b> The account cards carry the same pill the setup
/// wizard uses, and it is the loudest thing on the card.
///
/// <b>Nothing here blocks the UI thread.</b> Listing accounts is a round trip to ATAS over a named
/// pipe and can take seconds or time out, so the list is fetched in the background, null means
/// "still looking", and a failure degrades to an empty list with a sentence rather than a frozen
/// window. Everything else is built once and updated in place: the account cards are rebuilt only
/// when the platform's answer actually changed shape, because rebuilding them on the five-second
/// tick would disarm a half-pressed "Confirm: use this REAL-MONEY account".
/// </summary>
sealed class SettingsPage
{
    readonly AppHost _host;

    // ---- platform ----
    readonly TextBlock _platformValue = Ui.Mono("—");
    readonly TextBlock _platformNote = Ui.Muted("");
    readonly Control _switchBusy = Ui.Busy("Switching platform. The old connection is being closed.");
    readonly Border _fakeInUse = Ui.Pill("IN USE", Theme.Positive);
    readonly Border _atasInUse = Ui.Pill("IN USE", Theme.Positive);
    readonly Button _fakeButton;
    readonly Button _atasButton;
    bool _switching;

    // ---- account ----
    readonly TextBlock _accountValue = Ui.Mono("—");
    readonly TextBlock _accountNote = Ui.Muted("");
    readonly Control _accountBusy = Ui.Busy("Looking for accounts.");
    readonly TextBlock _accountEmpty = Ui.Muted(
        "TradeAgent could not find any accounts on this trading connection. If you have just signed in, " +
        "give it a moment and press Look again.");
    readonly TextBlock _accountProblem = Ui.Body("");
    readonly StackPanel _accountList = new() { Spacing = Theme.S3 };
    readonly Button _lookAgain;
    readonly List<(string Id, Border InUse, Button Choose)> _rows = [];

    /// <summary>The platform's answer, or null while we are still waiting for it.</summary>
    IReadOnlyList<AccountInfo>? _accounts;
    string? _accountsProblem;
    bool _loadingAccounts;

    /// <summary>
    /// Bumped whenever the question changes — a platform switch, or a press of Look again. An answer
    /// that comes back carrying an older number is an answer about a platform we have already left,
    /// and is thrown away rather than shown as this platform's accounts.
    /// </summary>
    int _accountGeneration;

    string _accountSignature = "";

    public Control Root { get; }

    public SettingsPage(AppHost host)
    {
        _host = host;

        // Back to the simulator is one press: it can only ever reduce what is at stake. Forward to
        // ATAS is two, and the armed label says which platform it is about to move to rather than
        // the word "Confirm" on its own.
        _fakeButton = Ui.Secondary("Use the practice simulator", () => SwitchPlatformAsync("fake"));
        _atasButton = Ui.Confirm("Use ATAS", "Confirm: switch to ATAS", () => SwitchPlatformAsync("atas"));

        _fakeInUse.IsVisible = false;
        _atasInUse.IsVisible = false;
        _switchBusy.IsVisible = false;

        var platform = Ui.Section("Trading platform", Ui.Col(Theme.S4,
            Ui.KeyValueLive("Platform in use", _platformValue),
            _platformNote,
            _switchBusy,
            Ui.Divider(),
            Option("Practice simulator", Ui.Pill("RECOMMENDED", Theme.Positive), _fakeInUse,
                "A built-in fake account. Nothing here is real and nothing can be lost.",
                _fakeButton),
            Ui.Divider(),
            Option("ATAS", null, _atasInUse,
                "Your real trading platform. TradeAgent connects to it and stays inside the limits you set.",
                _atasButton)));

        _lookAgain = Ui.Secondary("Look again", LookAgain);
        _lookAgain.HorizontalAlignment = HorizontalAlignment.Left;
        _accountEmpty.IsVisible = false;
        _accountProblem.IsVisible = false;
        _accountProblem.Foreground = Theme.Caution;

        // Both notes are written by the first refresh. Left visible they would be empty lines
        // holding open a gap in a card the user is reading for the first time.
        _platformNote.IsVisible = false;
        _accountNote.IsVisible = false;

        var account = Ui.Section("Account", Ui.Col(Theme.S4,
            Ui.KeyValueLive("Account in use", _accountValue),
            _accountNote,
            Ui.Divider(),
            _accountBusy,
            _accountProblem,
            _accountEmpty,
            _accountList,
            Ui.With(_lookAgain, b => b.Margin = new Thickness(0, Theme.S2, 0, 0))));

        // The right-hand column answers the question this page raises and nothing else answers:
        // what does pressing one of these actually do to my money and my open orders?
        var explain = Ui.Section("What these change", Ui.Col(Theme.S3,
            Ui.Body("The platform is where orders actually go. The practice simulator is built into TradeAgent " +
                    "and risks nothing. ATAS is your own trading program on this computer."),
            Ui.Divider(),
            Ui.Body("The account is the only one the AI is allowed to see and trade. It never touches another one."),
            Ui.Divider(),
            Ui.Muted("Changing the platform closes the current connection and clears your account choice — an " +
                     "account on one platform does not exist on the other. Neither change moves money, cancels " +
                     "an order or closes a position."),
            Ui.Divider(),
            Ui.Muted("Whether real money is allowed at all is a separate switch, on the Safety page.")));
        explain.Margin = new Thickness(Theme.S5, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, platform, account)));
        grid.Children.Add(Pages.Column(1, explain));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Settings",
                "Which trading platform TradeAgent uses, and which account the AI is allowed to trade."),
            grid));

        EnsureAccounts();
    }

    /// <summary>
    /// One platform, as a heading, a sentence and its button. Deliberately the same shape as an
    /// account row below it, so the two choices on this page read as the same kind of choice.
    /// </summary>
    static Control Option(string title, Control? badge, Control inUse, string body, Button action)
    {
        var heading = Ui.Row(Theme.S3, Ui.H2(title));
        if (badge is not null) heading.Children.Add(badge);
        heading.Children.Add(inUse);

        action.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Col(Theme.S2, heading, Ui.Muted(body),
            Ui.With(action, b => b.Margin = new Thickness(0, Theme.S2, 0, 0)));
    }

    // ---- refresh -----------------------------------------------------------------------------

    public void Update(GatewayStatus status)
    {
        ApplyPlatform(status.ConnectorId, Ui.PlatformLabel(status));
        EnsureAccounts();
        ApplyAccountSelection();
    }

    void ApplyPlatform(string? id, string label)
    {
        _platformValue.Text = label;

        _switchBusy.IsVisible = _switching;
        _platformNote.IsVisible = !_switching;
        _platformNote.Text = id == "atas"
            ? "Orders go to ATAS on this computer. Whether real money is involved depends on the account below."
            : "Every order goes to the built-in simulator. Nothing reaches a broker and nothing can be lost.";

        _fakeInUse.IsVisible = id != "atas";
        _atasInUse.IsVisible = id == "atas";
        _fakeButton.IsEnabled = !_switching && id == "atas";
        _atasButton.IsEnabled = !_switching && id != "atas";
        ApplyLookAgain();
    }

    /// <summary>
    /// Look again puts a question to the platform. There is no point asking twice at once, and no
    /// point at all while the platform is being swapped — mid-switch there is briefly no gateway to
    /// ask.
    /// </summary>
    void ApplyLookAgain() => _lookAgain.IsEnabled = !_loadingAccounts && !_switching;

    /// <summary>
    /// Which card is marked, and what the summary line says. Read from the stored setting rather than
    /// from <see cref="GatewayStatus.AccountId"/>: that one falls back to whichever account the
    /// platform happens to list first, so it is what the gateway would USE, not what the owner CHOSE.
    /// Reporting the fallback as a choice is how "not selected" stops being visible at all.
    /// </summary>
    void ApplyAccountSelection()
    {
        var selected = _host.Gateway.Settings.SelectedAccountId;
        var chosen = selected is null ? null : _accounts?.FirstOrDefault(a => a.Id == selected);

        foreach (var (id, inUse, choose) in _rows)
        {
            inUse.IsVisible = id == selected;
            choose.IsEnabled = id != selected;
        }

        // ATAS names a portfolio after its own id, so "CRYPTO5EB41 · CRYPTO5EB41" is what the obvious
        // formatting produces on the one platform this line matters most on. Seen on Windows.
        _accountValue.Text = selected is null ? "not chosen yet"
            : chosen is null ? selected
            : chosen.Name == chosen.Id ? chosen.Id
            : $"{chosen.Name} · {chosen.Id}";

        if (chosen is { IsSimulated: false })
        {
            Note(Theme.Danger, "This is a real-money account. Orders placed here spend your own money.");
        }
        else if (selected is null)
        {
            Note(Theme.Caution, "No account chosen yet. Choose one below — until you do, TradeAgent falls back " +
                                "to whichever account this platform lists first.");
        }
        else if (_accounts is { Count: > 0 } && chosen is null)
        {
            Note(Theme.Caution, "The account you chose is not on this platform's list any more. Choose one below.");
        }
        else
        {
            _accountNote.IsVisible = false;
        }

        void Note(IBrush tone, string text)
        {
            _accountNote.IsVisible = true;
            _accountNote.Text = text;
            _accountNote.Foreground = tone;
        }
    }

    // ---- the account list --------------------------------------------------------------------

    /// <summary>
    /// Asks the platform for its accounts, once, off the UI thread.
    ///
    /// This used to be done inside the wizard with <c>GetAwaiter().GetResult()</c> while building the
    /// screen, which blocks the UI thread on a broker round trip — a frozen window on any connection
    /// slower than the simulator. The list is not re-fetched on the five-second tick: it costs a
    /// named-pipe round trip and it changes when the user does something, which is what Look again
    /// and the platform switch are for.
    /// </summary>
    void EnsureAccounts()
    {
        RenderAccounts();

        if (_accounts is null && !_loadingAccounts)
        {
            _loadingAccounts = true;
            var generation = _accountGeneration;
            _ = Task.Run(async () =>
            {
                IReadOnlyList<AccountInfo> found;
                string? problem = null;
                try { found = await _host.Gateway.AccountsAsync(); }
                catch (Exception ex) { found = []; problem = Describe(ex); }

                Dispatcher.UIThread.Post(() =>
                {
                    _loadingAccounts = false;

                    // An answer about a platform we have since left. Drop it and ask the current one
                    // — showing it would put the old platform's accounts under the new platform's
                    // name, which is the one mistake this page must never make.
                    if (generation != _accountGeneration) { EnsureAccounts(); return; }

                    _accounts = found;
                    _accountsProblem = problem;
                    RenderAccounts();
                    ApplyAccountSelection();
                    ApplyLookAgain();
                });
            });
        }

        ApplyLookAgain();
    }

    void LookAgain()
    {
        _accountGeneration++;
        _accounts = null;
        _accountsProblem = null;
        EnsureAccounts();
    }

    /// <summary>
    /// Rebuilds the cards, but only when the platform's answer genuinely changed shape. A tick that
    /// returned the same accounts must not touch this tree: rebuilding it throws away a half-pressed
    /// confirmation and the scroll position of whoever is reading it.
    /// </summary>
    void RenderAccounts()
    {
        var signature = _accounts is null
            ? "looking"
            : $"{_accountsProblem}|" + string.Join('|', _accounts.Select(a =>
                $"{a.Id}:{a.Name}:{a.Balance}:{a.Currency}:{a.IsSimulated}:{a.TradingEnabled}"));
        if (signature == _accountSignature) return;
        _accountSignature = signature;

        _rows.Clear();
        _accountList.Children.Clear();

        _accountBusy.IsVisible = _accounts is null;
        _accountProblem.IsVisible = _accountsProblem is not null;
        _accountProblem.Text = _accountsProblem ?? "";
        _accountEmpty.IsVisible = _accounts is { Count: 0 } && _accountsProblem is null;

        if (_accounts is null) return;

        var first = true;
        foreach (var account in _accounts)
        {
            if (!first) _accountList.Children.Add(Ui.Divider());
            first = false;
            _accountList.Children.Add(AccountRow(account));
        }
    }

    Control AccountRow(AccountInfo account)
    {
        // This is the screen where someone points the AI at a live account by accident, so the two
        // kinds never look alike: the pill is the loudest thing on the row.
        var badge = account.IsSimulated
            ? Ui.Pill("SIMULATION", Theme.Info)
            : Ui.Pill("REAL MONEY", Theme.Danger);

        var inUse = Ui.Pill("IN USE", Theme.Positive);
        inUse.IsVisible = false;

        // Picking a simulated account risks nothing, so it is one press. Pointing the AI at an
        // account that spends real money is not, so it arms first and says so.
        var choose = account.IsSimulated
            ? Ui.Secondary("Use this account", () => SelectAccount(account))
            : Ui.Confirm("Use this account", "Confirm: use this REAL-MONEY account", () => SelectAccount(account));
        choose.HorizontalAlignment = HorizontalAlignment.Left;

        _rows.Add((account.Id, inUse, choose));

        var body = Ui.Col(Theme.S2,
            Ui.Row(Theme.S3, Ui.H2(account.Name), badge, inUse),
            Ui.Row(Theme.S3,
                Ui.Mono($"{account.Balance:N2} {account.Currency}", Theme.TextMuted),
                Ui.Mono(account.Id, Theme.TextFaint)));

        if (!account.TradingEnabled)
            body.Children.Add(Ui.Micro("Your platform has trading switched off for this account."));

        body.Children.Add(Ui.With(choose, b => b.Margin = new Thickness(0, Theme.S2, 0, 0)));
        return body;
    }

    // ---- the two things this page actually does -----------------------------------------------

    void SelectAccount(AccountInfo account)
    {
        _host.Gateway.Update(s => s.SelectedAccountId = account.Id);

        // Real money is recorded loudly and at warn level, so the account the AI was pointed at is
        // legible in the activity history months later without anyone having to know which id meant
        // what at the time.
        _host.Gateway.Log.Activity(
            account.IsSimulated
                ? $"You chose the simulated account {account.Name} ({account.Id})"
                : $"You chose the REAL-MONEY account {account.Name} ({account.Id})",
            account.IsSimulated ? "info" : "warn");

        ApplyAccountSelection();
    }

    /// <summary>
    /// The platform change itself. <see cref="AppHost.SwitchConnectorAsync"/> logs it and clears the
    /// account choice, so nothing is logged twice here — but the cached account list belongs to the
    /// platform we are leaving, and has to be asked again of the one we arrive at.
    /// </summary>
    async Task SwitchPlatformAsync(string id)
    {
        if (_switching) return;
        _switching = true;
        _switchBusy.IsVisible = true;
        _platformNote.IsVisible = false;
        _fakeButton.IsEnabled = false;
        _atasButton.IsEnabled = false;
        ApplyLookAgain();

        try
        {
            await _host.SwitchConnectorAsync(id);
        }
        catch (Exception ex)
        {
            Ui.ReportError?.Invoke(Describe(ex));
        }
        finally
        {
            _switching = false;

            // The account list belonged to the platform we just left. SwitchConnectorAsync has
            // already cleared the chosen account for the same reason; this asks the new platform
            // what it has instead.
            LookAgain();

            // Posted at Background priority, which is below the priority an await continuation
            // resumes at — so this lands AFTER the two-step button's own finally re-enables itself.
            // Applied inline it would be overwritten a moment later, leaving "Use ATAS" pressable on
            // the platform already in use.
            var connector = _host.Connector;
            Dispatcher.UIThread.Post(() =>
            {
                // A PLATFORM JUST SWITCHED TO HAS NOT ANSWERED YET, so its capabilities are the
                // all-false placeholder and `IsPaper` false here means "no handshake", not "real
                // money". This is the worst possible place to get that wrong — the owner is reading
                // the label precisely because they just chose the platform. Say it has not answered;
                // the next health tick replaces this with the real reading a few seconds later.
                ApplyPlatform(connector.Id,
                    Ui.PlatformLabel(connector.DisplayName, connector.Capabilities.IsPaper, platformAnswered: false));
                ApplyAccountSelection();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>The same reading <see cref="Ui.ReportError"/> gives a failed press: repair text included.</summary>
    static string Describe(Exception ex) =>
        ex is TradeAgentException t ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim() : ex.Message;
}
