using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;

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
    readonly Border _paperInUse = Ui.Pill("IN USE", Theme.Positive);
    readonly Border _atasInUse = Ui.Pill("IN USE", Theme.Positive);
    readonly Button _fakeButton;
    readonly Button _paperButton;
    readonly Button _atasButton;
    bool _switching;

    // ---- what a paper fill pays ----
    /// <summary>The friction in force on the paper platform, in the owner's words. Updated in place on the tick.</summary>
    readonly TextBlock _paperFrictionValue = Ui.Mono("—");
    readonly TextBox _paperFee;
    readonly TextBox _paperSlippage;
    readonly TextBlock _paperFrictionNote = Ui.Muted("");
    readonly Button _paperFrictionSave;
    readonly Button _paperFrictionClear;

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

    // ---- market data ----
    readonly TextBox _dataPair;
    readonly TextBlock _dataValue = Ui.Mono("—");
    readonly TextBlock _dataNote = Ui.Muted("");
    readonly Control _dataBusy = Ui.Busy("Collecting months from Binance's public archive.");
    readonly Button _collect;

    /// <summary>The live series as it stands: how many bars, how old the newest one is, any holes.</summary>
    readonly TextBlock _liveValue = Ui.Mono("—");

    /// <inheritdoc cref="ToggleLiveBars"/>
    readonly Button _liveBars;

    /// <inheritdoc cref="ToggleMarketContext"/>
    readonly Button _marketContext;

    /// <inheritdoc cref="ToggleGdeltNews"/>
    readonly Button _gdeltNews;
    bool _collecting;

    /// <summary>
    /// THE PAIR'S INSTRUMENT CHECK, in the one sentence every surface says: "verified against … on …" with
    /// the venue's numbers, or "not verified: …" with the reason (<c>U-venue-verify</c>).
    /// </summary>
    readonly TextBlock _instrumentValue = Ui.Mono("—");

    /// <inheritdoc cref="CheckInstrumentNowAsync"/>
    readonly Button _checkInstrument;
    bool _checkingInstrument;

    // ---- the holdout ----
    readonly TextBox _holdoutFrom;
    readonly TextBlock _holdoutValue = Ui.Mono("—");

    /// <summary>The cost model the open campaign's verdicts are scored under. See <see cref="JudgeLine"/>.</summary>
    readonly TextBlock _judgeValue = Ui.Mono("—");

    readonly TextBlock _holdoutNote = Ui.Muted("");
    readonly Button _holdoutResearch;
    readonly Button _holdoutFixture;

    // ---- updates ----
    readonly TextBlock _versionValue = Ui.Mono("—");
    readonly TextBlock _newestValue = Ui.Mono("—");
    readonly TextBlock _autoValue = Ui.Mono("—");
    readonly TextBlock _updateNote = Ui.Muted("");
    readonly Button _checkNow;
    readonly Button _whatsNew;
    readonly Button _installUpdate;
    readonly Button _autoToggle;

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
        _fakeButton = Ui.Secondary("Use the practice simulator", () => SwitchPlatformAsync(Platforms.Connectors.Simulator));

        // ONE PRESS, like the simulator and unlike ATAS: this platform reaches no venue at all, so
        // moving to it cannot put anything at stake. The two-press rule is about money and
        // permission, and TradeAgent paper has neither to offer.
        _paperButton = Ui.Secondary("Use TradeAgent paper", () => SwitchPlatformAsync(Platforms.Connectors.Paper));
        _atasButton = Ui.Confirm("Use ATAS", "Confirm: switch to ATAS", () => SwitchPlatformAsync(Platforms.Connectors.Atas));

        _fakeInUse.IsVisible = false;
        _paperInUse.IsVisible = false;
        _atasInUse.IsVisible = false;
        _switchBusy.IsVisible = false;

        // WHAT A PAPER FILL PAYS, AND THE OWNER'S OVERRIDE OF IT (`U-paper-friction`). ONE press to set
        // and one to clear, like the paper platform's own button: the two numbers only make the
        // simulation dearer or cheaper, and move no money, change no limit and grant nothing. The boxes
        // open on the override in force — empty where there is none — and are never rewritten by the
        // tick, which would take a number out from under the owner's typing.
        _paperFee = Ui.TextField(PercentText(host.Gateway.Settings.PaperFeeOverride), "venue's");
        _paperSlippage = Ui.TextField(PercentText(host.Gateway.Settings.PaperSlippageOverride), "assumed");
        _paperFrictionSave = Ui.Secondary("Use my numbers", SavePaperFriction);
        _paperFrictionClear = Ui.Ghost("Clear my numbers", ClearPaperFriction);
        _paperFrictionValue.TextWrapping = TextWrapping.Wrap;
        _paperFrictionNote.IsVisible = false;

        var platform = Ui.Section("Trading platform", Ui.Col(Theme.S4,
            Ui.KeyValueLive("Platform in use", _platformValue),
            _platformNote,
            _switchBusy,
            Ui.Divider(),
            Option("Practice simulator", Ui.Pill("RECOMMENDED", Theme.Positive), _fakeInUse,
                "A built-in fake account. Nothing here is real and nothing can be lost.",
                _fakeButton),
            Ui.Divider(),
            Option("TradeAgent paper — real prices, simulated fills", null, _paperInUse,
                "The prices are the ones TradeAgent is collecting; the fills are simulated inside "
                + "TradeAgent and reach no exchange. It is how a strategy is tried on a market that "
                + "is still moving. A fill here is a simulation, never proof that the price could "
                + "have been traded.",
                _paperButton),
            PaperFrictionRow(),
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

        // MARKET DATA. ONE press, not two, and that is the whole judgement on this card: fetching a
        // public archive grants nothing, changes no limit, touches no order and cannot lose money.
        // The two-press rule is for widening what the AI may do; this widens what it may READ, and
        // making the owner confirm it twice would teach them to confirm things twice.
        _dataPair = Ui.TextField(host.Gateway.Settings.MarketDataPair, "BTCUSDT");
        _dataPair.Width = 180;
        _dataPair.HorizontalAlignment = HorizontalAlignment.Left;
        _collect = Ui.Secondary("Download 12 months", CollectDataAsync);
        _collect.HorizontalAlignment = HorizontalAlignment.Left;
        _dataBusy.IsVisible = false;
        _dataNote.IsVisible = false;

        // THE INSTRUMENT CHECK. ONE press, for the reason the download beside it is one: it reads the
        // venue's public definition of the pair with no key, grants nothing, changes no limit and touches
        // no order. A sentence rather than a figure, because the half that says WHY a pair is not verified
        // is the half the owner needs.
        _instrumentValue.TextWrapping = TextWrapping.Wrap;
        _checkInstrument = Ui.Secondary("Check now", CheckInstrumentNowAsync);
        _checkInstrument.HorizontalAlignment = HorizontalAlignment.Left;

        // LIVE BARS. ONE press, and the same judgement as the download above it: this reads Binance's
        // market-data-only host, which accepts no authenticated request at all, and it grants nothing,
        // changes no limit and touches no order. ON by default, because forward observation cannot be
        // retrofitted — a minute nobody collected is gone.
        _liveBars = Ui.Secondary("Stop collecting live bars", ToggleLiveBars);
        _liveBars.HorizontalAlignment = HorizontalAlignment.Left;

        // MARKET CONTEXT. ONE press, and the same judgement again: public futures market data, no key,
        // nothing granted, no limit changed, no order touched. ON by default, because a reading nobody
        // recorded as it arrived can never be shown to have been known then.
        _marketContext = Ui.Secondary("Stop recording market context", ToggleMarketContext);
        _marketContext.HorizontalAlignment = HorizontalAlignment.Left;

        // GDELT NEWS. ONE press, the same judgement — public data, no key, nothing granted, no order touched — and a
        // switch of its own because it is the heavy one: GDELT's files weigh hundreds of megabytes a day on the wire,
        // and an owner on a metered connection must be able to stop that without stopping the rest.
        _gdeltNews = Ui.Secondary("Stop recording GDELT news", ToggleGdeltNews);
        _gdeltNews.HorizontalAlignment = HorizontalAlignment.Left;

        var marketData = Ui.Section("Market data", Ui.Col(Theme.S4,
            Ui.KeyValueLive("History TradeAgent holds", _dataValue),
            _dataNote,
            Ui.Divider(),
            Ui.FieldRow("Pair", _dataPair, "Upper-case letters and digits, as Binance writes it — BTCUSDT, ETHUSDT."),
            _collect,
            _dataBusy,
            Ui.Divider(),
            Ui.KeyValueLive("Instrument check", _instrumentValue),
            _checkInstrument,
            Ui.Muted("TradeAgent reads the pair's price step and size step from Binance's own published instrument "
                     + "definition, at Binance's own address — when it starts, every six hours while it runs, when you "
                     + "change the pair here, and when you press Check now. Backtests, the judge, TradeAgent paper and "
                     + "paper runs size to those numbers while the last successful check is under seven days old; a "
                     + "check that fails, or an address that is not Binance's own, leaves the pair unverified and this "
                     + "line says why."),
            Ui.Micro("It reads public data with no key and places nothing. A check confirms what Binance published at "
                     + "that moment — not that every order sized to it would be accepted, and not that a size clears "
                     + "Binance's minimum order value, which TradeAgent records and does not yet apply."),
            Ui.Divider(),
            Ui.KeyValueLive("Live bars TradeAgent has collected", _liveValue),
            _liveBars,
            Ui.Muted("While TradeAgent is running it also collects that pair's CLOSED 1-minute bars as they happen, "
                     + "from Binance's market-data-only host — the one that serves public data and accepts no "
                     + "trading request at all. A bar is kept only once it has closed, the first reading of a minute "
                     + "is the one that stands, and a minute the host does not publish is recorded as missing and is "
                     + "never filled in or guessed at."),
            Ui.Micro("These bars carry NO vendor checksum — none is published for a live window — so they are not "
                     + "evaluation evidence and no verdict is ever taken over them. They are what a paper run is "
                     + "watched against, and the AI can read them and nothing else about them."),
            Ui.Divider(),
            _marketContext,
            Ui.Muted("While TradeAgent is running it also records the market's context for six Binance futures pairs "
                     + "— BTC, ETH, SOL, BNB, XRP and DOGE against USDT: the premium index with the live funding rate, "
                     + "open interest, the 5-minute long/short and taker ratios, and settled funding — each reading "
                     + "with the moment it arrived. Nothing is ever overwritten: a reading Binance later changes is "
                     + "kept as a new revision beside the first. The same switch records OKX's announcements for EU "
                     + "users, the newest twenty once a minute: each is kept whole, one that looks addressed to an AI "
                     + "is flagged and its text withheld when the tape is read for the AI, and no link in them is opened."),
            Ui.Micro("It reads public endpoints with no key and places nothing. A reading that arrived on time from "
                     + "Binance's own address is marked live; a late one, or one from any other address, is marked "
                     + "archive. It is context for research, not evaluation evidence, it is recorded only while "
                     + "TradeAgent is running, and the AI cannot start, stop or change it."),
            Ui.Divider(),
            _gdeltNews,
            Ui.Muted("While TradeAgent is running it also records news items about crypto from the GDELT Project "
                     + "(https://www.gdeltproject.org/), which publishes what the world's news media reported every fifteen "
                     + "minutes. TradeAgent reads each fifteen-minute file once, from GDELT's own address, keeps only the items "
                     + "about Bitcoin, Ethereum, Solana, Binance, BNB, XRP, Ripple or Dogecoin and throws the rest away; at a "
                     + "start it fetches the files it missed, up to seven days back. Each item keeps the time GDELT first saw it."),
            Ui.Micro("It downloads about 420 MB a day and keeps about 7 MB of it, never more than 25 MB a day — switch it off "
                     + "on a metered connection. An item read on time from GDELT's own address is marked live; one fetched later "
                     + "is marked point-in-time when GDELT's checksum matches and its file was published before its time, and "
                     + "archive otherwise. One that looks addressed to an AI is flagged and its text withheld, no link in them is "
                     + "opened, and the AI cannot start, stop or change it. Source: the GDELT Project."),
            Ui.Divider(),
            Ui.Muted("TradeAgent downloads the twelve most recent complete months of 1-minute bars from Binance's " +
                     "public archive and checks every file against the checksum Binance published beside it. A month " +
                     "that has not been published is recorded as such; a file whose bytes do not match is thrown away."),
            Ui.Micro("This is history to research with. It is not a trading connection, it does not decide what the AI " +
                     "may trade — that is the allowlist on the Safety page — and it moves no money. The AI can read " +
                     "this data and everything recorded about where it came from; it cannot collect, change or delete it.")));

        // THE HOLDOUT. Two presses, and here rather than on the Safety page because it is about the
        // DATA: the owner is drawing a line across the months they have just collected and saying that
        // everything after it is evidence the research process never sees. Two presses because the line
        // can only ever move forward — a bar that has been served cannot become holdout again — so a
        // mis-click is not something the owner can take back.
        _holdoutFrom = Ui.TextField(null, "2026-06-01");
        _holdoutFrom.Width = 180;
        _holdoutFrom.HorizontalAlignment = HorizontalAlignment.Left;
        _holdoutResearch = BuildHoldoutConfirm(EvaluationClass.Research, ReadCutoff, ApplyHoldout);
        _holdoutFixture = BuildHoldoutConfirm(EvaluationClass.Fixture, ReadCutoff, ApplyHoldout);

        // A DATE CHANGED UNDER A HALF-PRESSED BUTTON DISARMS IT. `Ui.Relabel` is what the unconfirmed
        // orders card uses for the same reason: a confirmation armed against one sentence must not be
        // completable against a different one, and this sentence carries the date.
        _holdoutFrom.TextChanged += (_, _) => RelabelHoldout();
        _holdoutNote.IsVisible = false;

        // A SENTENCE, NOT A FIGURE: the judge's model names its fee, its slippage, its step and its
        // capital, and a value that ran off the card would hide the half that says which is assumed.
        _judgeValue.TextWrapping = TextWrapping.Wrap;

        var holdout = Ui.Section("Private evaluation evidence", Ui.Col(Theme.S4,
            Ui.KeyValueLive("Held back", _holdoutValue),
            Ui.KeyValueLive("Judged under", _judgeValue),
            _holdoutNote,
            Ui.Divider(),
            Ui.FieldRow("From", _holdoutFrom,
                "A date, or a date and time, in UTC — 2026-06-01 or 2026-06-01T00:00:00Z. The bar at that " +
                "instant is already held back."),
            Ui.Wrap(Theme.S2, _holdoutResearch, _holdoutFixture),
            Ui.Divider(),
            Ui.Muted("Bars from this date on become evidence the research process never sees. The AI can neither " +
                     "read them nor backtest over them: a window that reaches the date is refused in words rather " +
                     "than quietly cut short, and every part of the AI is refused equally. TradeAgent itself reads " +
                     "them, to judge a finished strategy on months it was never shown."),
            Ui.Micro("The line can only be moved LATER. A bar that has already been served to the AI cannot become " +
                     "held-back afterwards, so TradeAgent refuses to move the date back rather than pretending the " +
                     "AI never saw it. Fixture bars are the other choice: made-up minutes that prove the machinery " +
                     "works, never counted as evidence and never charged against the AI's budget of attempts.")));

        // Updates are here rather than on Checks because this is where somebody looks for "what
        // version am I on". Nothing on this card happens on its own: the automatic half is the
        // asking, and installing is two presses, because it closes the program holding the open
        // orders and starts a different build of it.
        _checkNow = Ui.Secondary("Check for updates", () => _host.Updates.CheckAsync());
        _whatsNew = Ui.Ghost("What's new", () =>
        {
            var url = _host.Updates.Available?.ReleaseUrl;
            if (!string.IsNullOrWhiteSpace(url)) Browser.TryOpen(url);
        });
        _installUpdate = Ui.Confirm("Install update", "Confirm: close TradeAgent and install",
            () => MainWindow.InstallUpdateAsync(_host));
        _autoToggle = Ui.Ghost("Turn off automatic checks", ToggleAutomaticChecks);
        _whatsNew.IsVisible = false;
        _installUpdate.IsVisible = false;

        var updates = Ui.Section("Updates", Ui.Col(Theme.S4,
            Ui.KeyValueLive("This version", _versionValue),
            Ui.KeyValueLive("Newest published version", _newestValue),
            Ui.KeyValueLive("Automatic checks", _autoValue),
            _updateNote,
            Ui.Wrap(Theme.S2, _checkNow, _whatsNew, _installUpdate, _autoToggle),
            Ui.Divider(),
            Ui.Muted("TradeAgent never installs an update on its own, and the AI cannot ask it to. Installing " +
                     "closes TradeAgent, replaces it, and opens the new version. Your records, your settings and " +
                     "your ATAS installation are untouched."),
            Ui.Micro($"Releases come from github.com/{_host.Updates.Repository}. The download is checked against " +
                     "the checksum published beside it, which proves the file arrived intact — not who signed it. " +
                     "If that checksum is missing or does not match, TradeAgent refuses to install and says so here.")));

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

        // Always here, whatever the bridge row currently says. The Checks page shows this same card
        // only when the row calls for it, and that is the right rule for a page about what is wrong —
        // but an owner who has just been told by ATAS, or by a support message, to put the bridge
        // back needs somewhere it is always found. Settings is where the other two standing choices
        // about the trading platform already live.
        var bridge = Ui.Section("ATAS bridge", BridgeRepair.Body(_host));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };
        grid.Children.Add(Pages.Column(0, Ui.Col(Theme.S6, platform, account, marketData, holdout, bridge, updates)));
        grid.Children.Add(Pages.Column(1, explain));

        Root = Pages.Scroll(Ui.Col(0,
            Pages.Header("Settings",
                "Which trading platform TradeAgent uses, which account the AI is allowed to trade, and which version you are running."),
            grid));

        EnsureAccounts();
        ApplyPaperFriction();
        ApplyMarketData();
        RelabelHoldout();
        ApplyHoldoutValue();
        ApplyUpdates();
    }

    /// <summary>
    /// THE PAPER ROW'S FRICTION: what every fill pays, said with its source, and the two boxes that
    /// override it. Under the paper option rather than on the Safety page because it is not a risk
    /// limit — it is how the simulation is priced, and the place an owner reads about the simulation.
    /// </summary>
    Control PaperFrictionRow() => Ui.Col(Theme.S2,
        Ui.KeyValueLive("Fills pay", _paperFrictionValue),
        Ui.FieldRow("Your fee per fill, in %", _paperFee,
            "Leave it empty to pay the venue's published standard fee — what TradeAgent's own judge charges."),
        Ui.FieldRow("Your slippage per fill, in %", _paperSlippage,
            "Leave it empty to use TradeAgent's assumption of two basis points, which is not a measurement."),
        Ui.Wrap(Theme.S2, _paperFrictionSave, _paperFrictionClear),
        _paperFrictionNote,
        Ui.Micro("Your numbers are used exactly, 0 included, and each one on its own: a fee with the slippage "
                 + "left empty pays your fee and TradeAgent's slippage. Every paper fill records whose numbers "
                 + "it paid. This makes the simulation dearer or cheaper; it moves no money and allows nothing."));

    /// <summary>The friction in force, read off the settings by the rule the connector reads at each fill.</summary>
    void ApplyPaperFriction() =>
        _paperFrictionValue.Text = Core.Strategy.FrictionInForce.ForPaper(_host.Gateway.Settings).Line;

    /// <summary>
    /// The owner's one press. Each box is its own override: empty is "none", a number is used exactly,
    /// zero included. Refused in words rather than saved when a box does not hold a percentage, or holds
    /// more than any venue charges — the bound a backtest's declaration has, said in percent.
    /// </summary>
    void SavePaperFriction()
    {
        if (!TryPercent(_paperFee.Text, Core.Strategy.ExecutionModel.MaxFeeRate, "fee", out var fee, out var why)
            || !TryPercent(_paperSlippage.Text, Core.Strategy.ExecutionModel.MaxSlippageRate, "slippage", out var slippage, out why))
        {
            PaperFrictionNote(Theme.Caution, why!);
            return;
        }

        _host.Gateway.Update(s =>
        {
            s.PaperFeeOverride = fee;
            s.PaperSlippageOverride = slippage;
        });
        ApplyPaperFriction();
        var line = _paperFrictionValue.Text;
        _host.Gateway.Log.Activity($"Paper fills now pay {line}");
        PaperFrictionNote(Theme.TextMuted, $"Saved. Paper fills now pay {line}.");
    }

    /// <summary>One press clears both numbers, and the venue cost model is what every fill pays again.</summary>
    void ClearPaperFriction()
    {
        _host.Gateway.Update(s =>
        {
            s.PaperFeeOverride = null;
            s.PaperSlippageOverride = null;
        });
        _paperFee.Text = "";
        _paperSlippage.Text = "";
        ApplyPaperFriction();
        var line = _paperFrictionValue.Text;
        _host.Gateway.Log.Activity($"Paper fills pay TradeAgent's venue cost model again: {line}");
        PaperFrictionNote(Theme.TextMuted, $"Cleared. Paper fills now pay {line}.");
    }

    void PaperFrictionNote(IBrush tone, string text)
    {
        _paperFrictionNote.IsVisible = true;
        _paperFrictionNote.Foreground = tone;
        _paperFrictionNote.Text = text;
    }

    /// <summary>
    /// A box's percentage as the FRACTION the setting holds, or null for an empty box, or a refusal. A
    /// comma is read as the decimal point, because that is how a Belgian keyboard writes one.
    /// </summary>
    internal static bool TryPercent(string? text, decimal max, string name, out decimal? fraction, out string? why)
    {
        fraction = null;
        why = null;
        var typed = (text ?? "").Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (typed.Length == 0) return true;

        if (!decimal.TryParse(typed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent))
        {
            why = $"\u201c{text}\u201d is not a {name} TradeAgent can use: write a percentage such as 0.1 for a "
                  + "tenth of a per cent, or leave the box empty.";
            return false;
        }

        if (percent / 100m > max)
        {
            why = $"A {name} of {percent.ToString(CultureInfo.InvariantCulture)}% is more than any venue charges — "
                  + $"at most {(max * 100m).ToString("0.##", CultureInfo.InvariantCulture)}%. It reads like a number "
                  + "meant in basis points; 0.1 is a tenth of a per cent.";
            return false;
        }

        fraction = percent / 100m;
        return true;
    }

    /// <summary>An override as the box shows it, in percent; empty for none.</summary>
    static string PercentText(decimal? fraction) =>
        fraction is { } f ? (f * 100m).ToString("0.############", CultureInfo.InvariantCulture) : "";

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

    /// <summary>
    /// The owner's one press. It saves the pair, fetches the twelve months, normalises what arrived
    /// and records the provenance — all off the UI thread, because a twelve-month collection is
    /// twenty-five megabytes over a domestic line and a frozen window is not a progress indicator.
    ///
    /// A failure is a sentence on this card, never an exception into the app: the archive is a
    /// public bucket and being unable to reach it is an ordinary Tuesday.
    /// </summary>
    async Task CollectDataAsync()
    {
        if (_collecting) return;

        var pair = (_dataPair.Text ?? "").Trim().ToUpperInvariant();
        if (!BinanceArchive.IsPair(pair))
        {
            _dataNote.IsVisible = true;
            _dataNote.Foreground = Theme.Caution;
            _dataNote.Text = $"\u201c{pair}\u201d is not a pair TradeAgent will ask Binance for. Use upper-case letters " +
                             "and digits, for example BTCUSDT.";
            return;
        }

        _collecting = true;
        _dataPair.Text = pair;
        var before = _host.Gateway.Settings.MarketDataPair;
        _host.Gateway.Update(s => s.MarketDataPair = pair);
        ApplyMarketData();

        // A NEW PAIR IS CHECKED AT ONCE (U-venue-verify): the owner chose it, and until its definition is read
        // nothing will size to it. Beside the download, not after it — the two are independent reads.
        if (!string.Equals(before, pair, StringComparison.Ordinal)) _ = CheckInstrumentNowAsync();

        try
        {
            var progress = new Progress<ProvisionProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                _dataNote.IsVisible = true;
                _dataNote.Foreground = Theme.TextMuted;
                _dataNote.Text = p.Message;
            }));

            var result = await Task.Run(() => _host.MarketData.CollectAsync(pair, DateTimeOffset.UtcNow, progress));

            _dataNote.IsVisible = true;
            _dataNote.Foreground = Theme.TextMuted;
            _dataNote.Text = result.Summary;
        }
        catch (Exception ex)
        {
            _dataNote.IsVisible = true;
            _dataNote.Foreground = Theme.Caution;
            _dataNote.Text = $"The months could not be collected: {ex.Message}";
        }
        finally
        {
            _collecting = false;
            ApplyMarketData();
        }
    }

    /// <summary>
    /// What this installation holds, read back from the ledger and VERIFIED as it is read, so a
    /// dataset whose files have changed under it says so here rather than being described as though
    /// it were still what was measured.
    /// </summary>
    void ApplyMarketData()
    {
        _dataBusy.IsVisible = _collecting;
        _collect.IsEnabled = !_collecting;
        _dataPair.IsEnabled = !_collecting;

        ApplyLiveBars();
        ApplyMarketContext();
        ApplyGdeltNews();
        ApplyInstrumentCheck();

        try
        {
            var newest = _host.Gateway.Datasets.All().FirstOrDefault();
            if (newest is null) { _dataValue.Text = "none yet"; return; }

            var set = _host.Gateway.Datasets.Checked(newest);
            _dataValue.Text = set.State == DatasetState.REJECTED
                ? $"{set.Pair} {set.Interval} — REJECTED, collect it again"
                : $"{set.Pair} {set.Interval}, {set.Bars:N0} bars, "
                  + $"{set.FirstBar?.UtcDateTime:yyyy-MM-dd} to {set.LastBar?.UtcDateTime:yyyy-MM-dd} UTC, "
                  + $"{set.Gaps:N0} minutes missing";
        }
        catch (Exception) { _dataValue.Text = "could not be read"; }
    }

    /// <summary>
    /// ONE PRESS: READ THE PAIR'S DEFINITION FROM THE VENUE NOW (<c>U-venue-verify</c>). It grants nothing,
    /// changes no limit and touches no order; what it can change is whether TradeAgent's own readers size to
    /// the venue's numbers, and only to numbers the venue published. Off the UI thread, because it is a
    /// network request with a ten-second leash, and a second press while one is running does nothing.
    /// </summary>
    async Task CheckInstrumentNowAsync()
    {
        if (_checkingInstrument) return;
        _checkingInstrument = true;
        ApplyInstrumentCheck();
        try { await Task.Run(() => _host.CheckInstrumentAsync()); }
        finally
        {
            _checkingInstrument = false;
            ApplyInstrumentCheck();
        }
    }

    /// <summary>
    /// THE PAIR'S CHECK AS IT STANDS, off the served read every sizing reader uses, so this line can never
    /// say "verified" about a pair nothing is sizing to.
    /// </summary>
    void ApplyInstrumentCheck()
    {
        _checkInstrument.IsEnabled = !_checkingInstrument;
        _checkInstrument.Content = _checkingInstrument ? "Checking…" : "Check now";

        try
        {
            var pair = _host.Gateway.Settings.MarketDataPair;
            _instrumentValue.Text = $"{pair} — {_host.Gateway.Venues.Verification(VenueCatalog.BinanceSpot, pair).Says}";
        }
        catch (Exception) { _instrumentValue.Text = "could not be read"; }
    }

    /// <summary>
    /// ONE PRESS, AND IT ONLY EVER CHANGES WHAT TRADEAGENT READS. See
    /// <see cref="TradeAgentSettings.CollectLiveBars"/>: the host this reaches accepts no
    /// authenticated request, so there is nothing here for a second press to protect.
    ///
    /// <para>The collector itself reads the setting at every look, so nothing has to be restarted:
    /// switched off it writes nothing at all, and switched back on the next look carries on from the
    /// newest bar in the ledger — with the minutes in between recorded as the gap they are.</para>
    /// </summary>
    void ToggleLiveBars()
    {
        var on = !_host.Gateway.Settings.CollectLiveBars;
        _host.Gateway.Update(s => s.CollectLiveBars = on);
        _host.Gateway.Log.Activity(on
            ? "Live 1-minute bars: collection switched ON"
            : "Live 1-minute bars: collection switched off");
        ApplyLiveBars();
    }

    /// <summary>
    /// ONE PRESS, AND IT ONLY EVER CHANGES WHAT TRADEAGENT RECORDS. See
    /// <see cref="TradeAgentSettings.RecordMarketContext"/>: public market data, no key, nothing a
    /// second press could protect. The collector reads the setting at every look, so nothing restarts:
    /// off, it writes nothing at all; back on, the next look on each row's cadence carries on.
    /// </summary>
    void ToggleMarketContext()
    {
        var on = !_host.Gateway.Settings.RecordMarketContext;
        _host.Gateway.Update(s => s.RecordMarketContext = on);
        _host.Gateway.Log.Activity(on
            ? "Market context: recording switched ON"
            : "Market context: recording switched off");
        ApplyMarketContext();
    }

    /// <summary>The button says what pressing it will do. What the tape holds is <c>U-tape-read</c>'s to show.</summary>
    void ApplyMarketContext() =>
        _marketContext.Content = _host.Gateway.Settings.RecordMarketContext
            ? "Stop recording market context"
            : "Record market context";

    /// <summary>
    /// ONE PRESS, AND IT ONLY EVER CHANGES WHAT TRADEAGENT RECORDS. See <see cref="TradeAgentSettings.RecordGdeltNews"/>:
    /// public data, no key, nothing a second press could protect. The recorder reads the setting before every request,
    /// so nothing restarts: off, it asks nothing and writes nothing; back on, the next look at a quarter hour carries on.
    /// </summary>
    void ToggleGdeltNews()
    {
        var on = !_host.Gateway.Settings.RecordGdeltNews;
        _host.Gateway.Update(s => s.RecordGdeltNews = on);
        _host.Gateway.Log.Activity(on
            ? "GDELT news: recording switched ON"
            : "GDELT news: recording switched off");
        ApplyGdeltNews();
    }

    /// <summary>The button says what pressing it will do.</summary>
    void ApplyGdeltNews() =>
        _gdeltNews.Content = _host.Gateway.Settings.RecordGdeltNews
            ? "Stop recording GDELT news"
            : "Record GDELT news";

    /// <summary>
    /// THE LIVE SERIES AS IT STANDS, off the rows and never off "when the collector last ran": a
    /// collector running happily against a host publishing nothing is exactly the case this line must
    /// not report as healthy.
    /// </summary>
    void ApplyLiveBars()
    {
        var on = _host.Gateway.Settings.CollectLiveBars;
        _liveBars.Content = on ? "Stop collecting live bars" : "Collect live bars";

        try
        {
            var pair = _host.Gateway.Settings.MarketDataPair;
            var series = _host.Gateway.Forward.Series(pair);

            if (series.Bars == 0)
            {
                _liveValue.Text = on
                    ? $"{pair} — nothing collected yet"
                    : $"{pair} — not being collected";
                if (series.LastError is { Length: > 0 } why) _liveValue.Text += $" ({why})";
                return;
            }

            var age = _host.Gateway.Forward.Freshness(pair, DateTimeOffset.UtcNow);
            _liveValue.Text =
                $"{pair} {series.Interval}, {series.Bars:N0} bars, newest {Age(age)} old, "
                + $"{series.BarsMissing:N0} minutes missing"
                + (on ? "" : " — collection is off")
                + (series.LastError is { Length: > 0 } error ? $" — last look: {error}" : "");
        }
        catch (Exception) { _liveValue.Text = "could not be read"; }
    }

    /// <summary>A duration in the plainest words that are still exact enough to act on.</summary>
    static string Age(TimeSpan? age) => age switch
    {
        null => "unknown",
        { TotalSeconds: < 90 } d => $"{d.TotalSeconds:N0} s",
        { TotalMinutes: < 90 } d => $"{d.TotalMinutes:N0} min",
        var d => $"{d!.Value.TotalHours:N1} h"
    };

    // ---- the holdout -------------------------------------------------------------------------

    /// <summary>
    /// ONE OF THE TWO PRESSES THAT SET A HOLDOUT, built the way the page builds it so that what a test
    /// presses is the control the owner sees.
    ///
    /// <para><b>Two presses in both directions, which is unlike every other pair on these pages.</b>
    /// The usual rule is that widening asks twice and narrowing asks once, because hesitating on the way
    /// down costs money. This control has no way down: a cutoff cannot be moved back, so the first press
    /// is the only moment at which the owner can still change their mind. The armed sentence carries the
    /// DATE, because "Confirm" on its own would not say which months are about to become private.</para>
    ///
    /// <para>The cutoff is re-read at the moment of the second press rather than captured at build time
    /// — the owner types it into the field beside the button — and a date this build cannot read applies
    /// nothing at all. <see cref="RelabelHoldout"/> disarms the control whenever that text changes.</para>
    /// </summary>
    internal static Button BuildHoldoutConfirm(
        string evaluationClass, Func<DateTimeOffset?> cutoff, Action<DateTimeOffset, string> apply)
    {
        var label = evaluationClass == EvaluationClass.Fixture ? Labels.SetHoldoutFixture : Labels.SetHoldout;
        var b = Ui.Confirm(label, HoldoutArmed(evaluationClass, cutoff()), () =>
        {
            if (cutoff() is { } at) apply(at, evaluationClass);
        });
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>What the second press will do, in the owner's words, with the date they typed in it.</summary>
    internal static string HoldoutArmed(string evaluationClass, DateTimeOffset? at)
    {
        var date = at is { } when ? $"{when.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "that date";
        return evaluationClass == EvaluationClass.Fixture
            ? Labels.SetHoldoutFixtureArmed(date)
            : Labels.SetHoldoutArmed(date);
    }

    /// <summary>
    /// THE DATE THE OWNER TYPED, READ AS UTC, or null because there is nothing readable in the box.
    ///
    /// A bare date means midnight UTC, not midnight here: the cutoff decides which bars exist for the
    /// AI, and a boundary that moved with the machine's offset would hold back a different set of
    /// minutes on every computer.
    /// </summary>
    DateTimeOffset? ReadCutoff() =>
        DateTimeOffset.TryParse((_holdoutFrom.Text ?? "").Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>
    /// Keeps both armed sentences in step with the box, and disarms them as it goes. A control armed
    /// against one date must not be completable against another.
    /// </summary>
    void RelabelHoldout()
    {
        var at = ReadCutoff();
        Ui.Relabel(_holdoutResearch, Labels.SetHoldout, HoldoutArmed(EvaluationClass.Research, at));
        Ui.Relabel(_holdoutFixture, Labels.SetHoldoutFixture, HoldoutArmed(EvaluationClass.Fixture, at));

        var typed = (_holdoutFrom.Text ?? "").Trim().Length > 0;
        _holdoutResearch.IsEnabled = at is not null;
        _holdoutFixture.IsEnabled = at is not null;
        _holdoutNote.IsVisible = typed && at is null;
        if (typed && at is null)
        {
            _holdoutNote.Foreground = Theme.Caution;
            _holdoutNote.Text = "TradeAgent cannot read that as a date. Write it as 2026-06-01, or as "
                + "2026-06-01T13:00:00Z if the hour matters.";
        }
    }

    /// <summary>
    /// The owner's second press. It moves the cutoff on the newest dataset — the one the card above
    /// describes — and prints what the ledger did, refusal included.
    /// </summary>
    void ApplyHoldout(DateTimeOffset at, string evaluationClass)
    {
        _holdoutNote.IsVisible = true;

        try
        {
            var newest = _host.Gateway.Datasets.All().FirstOrDefault();
            if (newest is null)
            {
                _holdoutNote.Foreground = Theme.Caution;
                _holdoutNote.Text = "There is no market data to hold back yet. Download some months first.";
                return;
            }

            // ONE PRESS, TWO ROWS, ONE TRANSACTION. The gateway writes the cutoff and opens the campaign
            // that cutoff is the subject of together: months held back with nothing counting the attempts
            // made against them would be a holdout with no protocol behind it.
            var (done, campaign) = _host.Gateway.SetHoldout(newest.Id, at, evaluationClass);
            _holdoutNote.Foreground = done.Ok ? Theme.TextMuted : Theme.Caution;
            _holdoutNote.Text = done.Ok
                ? $"Bars from {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC on are held back. The AI cannot read them "
                  + "or backtest over them, and this date can no longer be moved earlier."
                  + (campaign is { } c
                      ? $" Campaign {c.Id} is measured against them: {c.TrialBudget:N0} research runs and "
                        + $"{c.VerdictBudget:N0} final judgements, and the standard it will be judged by is "
                        + "fixed as of now."
                      : "")
                : done.Why;
        }
        catch (Exception ex)
        {
            _holdoutNote.Foreground = Theme.Caution;
            _holdoutNote.Text = $"The holdout could not be set: {ex.Message}";
        }
        finally { ApplyHoldoutValue(); }
    }

    /// <summary>What is held back right now, read off the ledger rather than off the box.</summary>
    void ApplyHoldoutValue()
    {
        try
        {
            var newest = _host.Gateway.Datasets.All().FirstOrDefault();
            if (newest?.HoldoutFrom is not { } at)
            {
                _holdoutValue.Text = "nothing";
                _judgeValue.Text = JudgeLine(null);
                return;
            }

            var open = _host.Gateway.Campaigns.OpenForDataset(newest.Id);
            _holdoutValue.Text = $"{newest.Pair} from {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC on"
                + (newest.EvaluationClass == EvaluationClass.Fixture ? ", fixture bars" : "")
                + (open is { } c ? $", campaign {c.Id}" : "");
            _judgeValue.Text = JudgeLine(open);
        }
        catch (Exception)
        {
            _holdoutValue.Text = "could not be read";
            _judgeValue.Text = "could not be read";
        }
    }

    /// <summary>
    /// THE COST MODEL A CAMPAIGN'S VERDICTS ARE SCORED UNDER, as the owner reads it — the campaign's
    /// pinned <c>VenueCostModel</c>, never today's fee table or today's setting.
    ///
    /// <para>A campaign opened before TradeAgent pinned one says so rather than guessing which it will
    /// get: that is decided by its next verdict request, inside the charge, because it depends on
    /// whether a verdict was already charged and on whether the instrument's step is confirmed then.
    /// A pinned text this build cannot read back is said in those words; the referee refuses it.</para>
    /// </summary>
    internal static string JudgeLine(CampaignRow? campaign) => campaign switch
    {
        null => "—",
        { CostModelCanonical: null } =>
            "fixed at this campaign's next judgement — it opened before TradeAgent pinned the judge's costs",
        _ => Core.Strategy.VenueCostModel.Read(campaign.CostModelCanonical, campaign.CostModelSha256)?.Describe()
             ?? "a pinned cost model this version of TradeAgent cannot read; it will not judge under another"
    };

    // ---- refresh -----------------------------------------------------------------------------

    public void Update(GatewayStatus status)
    {
        ApplyPlatform(status.ConnectorId, Ui.PlatformLabel(status));
        // The VALUE only — never the two boxes, which hold whatever the owner is typing.
        ApplyPaperFriction();
        EnsureAccounts();
        ApplyAccountSelection();
        ApplyMarketData();
        // The VALUE only. `RelabelHoldout` is deliberately not on the tick: it disarms, and a
        // five-second refresh that disarms a half-pressed confirmation is the defect the dashboard
        // tree was built once to avoid.
        ApplyHoldoutValue();
        ApplyUpdates();
    }

    void ApplyPlatform(string? id, string label)
    {
        _platformValue.Text = label;

        _switchBusy.IsVisible = _switching;
        _platformNote.IsVisible = !_switching;
        _platformNote.Text = id switch
        {
            Platforms.Connectors.Atas =>
                "Orders go to ATAS on this computer. Whether real money is involved depends on the account below.",
            Platforms.Connectors.Paper =>
                "Orders are filled inside TradeAgent against the prices it has collected. Nothing reaches "
                + "an exchange and nothing can be lost.",
            _ => "Every order goes to the built-in simulator. Nothing reaches a broker and nothing can be lost."
        };

        // The simulator is what an id this build does not recognise falls back to, exactly as
        // Connectors.Create does, so the marked card and the platform actually in use agree.
        var paper = id == Platforms.Connectors.Paper;
        var atas = id == Platforms.Connectors.Atas;
        _fakeInUse.IsVisible = !paper && !atas;
        _paperInUse.IsVisible = paper;
        _atasInUse.IsVisible = atas;
        _fakeButton.IsEnabled = !_switching && (paper || atas);
        _paperButton.IsEnabled = !_switching && !paper;
        _atasButton.IsEnabled = !_switching && !atas;
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
        _paperButton.IsEnabled = false;
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

    // ---- updates -------------------------------------------------------------------------------

    /// <summary>
    /// The update card, in place and on every tick. Nothing here is rebuilt and nothing is relabelled
    /// unless the words actually changed: <see cref="Ui.Relabel"/> disarms the two-step button, and a
    /// five-second refresh that disarms a half-pressed "Confirm: close TradeAgent" is the same defect
    /// this whole page was written around.
    /// </summary>
    void ApplyUpdates()
    {
        var updates = _host.Updates;
        var info = updates.Available;

        _versionValue.Text = updates.CurrentVersion;
        _newestValue.Text = updates.Stage switch
        {
            UpdateStage.Idle => "not checked yet",
            UpdateStage.Checking => "asking GitHub…",
            UpdateStage.UpToDate => $"{updates.CurrentVersion} — you have the newest one",
            // "Could not be checked" is true of a machine that is offline and false of a release
            // TradeAgent looked at and refused. The reason for the second one is in _updateNote,
            // directly below this row, so this only has to stop contradicting it.
            UpdateStage.Failed when info is null && updates.Refused => "found, and not offered — see below",
            UpdateStage.Failed when info is null => "could not be checked",
            _ => info?.Version ?? "not checked yet"
        };

        var auto = _host.AutoCheckForUpdates;
        _autoValue.Text = auto ? "on — once at startup, then every six hours" : "off";
        _autoToggle.Content = auto ? "Turn off automatic checks" : "Turn on automatic checks";

        var checkedAt = updates.LastCheckedUtc is { } t
            ? $"Last checked {t.ToLocalTime():d MMMM, HH:mm}."
            : "Not checked yet.";

        // Said BEFORE the press, not after it. A release with no checksum file beside it is one this
        // product will refuse to install; the refusal is in UpdateService and stays there, but the
        // owner should not have to press a button twice to be told something we already know.
        var message = updates.Message;
        if (string.IsNullOrWhiteSpace(message) && info is not null && !updates.CanBeVerified)
            message = $"TradeAgent {info.Version} was published without the checksum file that proves what it is. " +
                      "It cannot be installed.";
        _updateNote.Text = string.IsNullOrWhiteSpace(message) ? checkedAt : $"{message} {checkedAt}";
        _updateNote.Foreground =
            updates.Stage == UpdateStage.Failed || (info is not null && !updates.CanBeVerified)
                ? Theme.Caution
                : Theme.TextMuted;

        var busy = updates.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;
        _checkNow.IsEnabled = !busy;
        _whatsNew.IsVisible = info is not null;
        _installUpdate.IsVisible = info is not null;

        // Deliberately NOT a copy of the banner's unconfirmed-order check. That check used to live on
        // a button, which is how this page ended up being a second way around it; it is now inside
        // UpdateService.InstallAsync, where both buttons meet. Leaving this pressable during an
        // unconfirmed order means the press produces the actual reason in _updateNote above rather
        // than a dead control with nothing to read.
        //
        // A release that published no checksum file is different: there is no press that can ever
        // succeed, and the note beside the button already says why.
        _installUpdate.IsEnabled = !busy && updates.CanBeVerified;

        if (info is not null)
            Ui.Relabel(_installUpdate, "Install update", $"Confirm: close TradeAgent and install {info.Version}");
    }

    /// <summary>
    /// One press, both ways. Turning the checks off risks nothing and turning them back on risks
    /// nothing either — the thing that spends and restarts is the install, and that is still two
    /// presses whatever this says.
    /// </summary>
    void ToggleAutomaticChecks()
    {
        var on = !_host.AutoCheckForUpdates;
        _host.AutoCheckForUpdates = on;
        _host.Gateway.Log.Activity(on
            ? "You turned on automatic checks for new TradeAgent versions"
            : "You turned off automatic checks for new TradeAgent versions");
        ApplyUpdates();
    }

    /// <summary>The same reading <see cref="Ui.ReportError"/> gives a failed press: repair text included.</summary>
    static string Describe(Exception ex) =>
        ex is TradeAgentException t ? $"{t.Info.UserMessage} {t.Info.Repair}".Trim() : ex.Message;
}
