using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Security;

namespace TradeAgent.App;

/// <summary>
/// WHAT THE PERCEPTION CARD IS HANDED — every fact it shows and every act it can do, as delegates, so the card the owner
/// sees and the card a test presses are the same control over different sources (<c>U-decision-card</c>). The app builds
/// it from <see cref="AppHost"/> (<see cref="PerceptionCard.For"/>); nothing here reaches the agent-facing pipe.
/// </summary>
sealed record PerceptionSources
{
    /// <summary>The decision models' own key holder — <see cref="AppHost.PerceptionKey"/>, never the worker's.</summary>
    public required HarnessKey Key { get; init; }

    /// <summary>The instruments as they stand, with the file's refusals: <see cref="DecisionInstruments.Read"/>.</summary>
    public required Func<DecisionInstrumentsRead> Instruments { get; init; }

    /// <summary>The model for one instrument, or null where it may not be called: <see cref="AppHost.DecisionModel"/>.</summary>
    public required Func<string, IDecisionModel?> Model { get; init; }

    /// <summary>Perception's daily budget as saved, and the in-process write of a new one.</summary>
    public required Func<decimal> Budget { get; init; }

    public required Action<decimal> SaveBudget { get; init; }

    /// <summary>The owner's one daily AI cap — the same figure every call is admitted against.</summary>
    public required Func<decimal> Cap { get; init; }

    /// <summary>The currency the AI's spending is kept in.</summary>
    public required Func<string> Currency { get; init; }

    /// <summary>The launch ledger the day's spending is read from, or null while none is open.</summary>
    public required Func<AiAttemptStore?> Ledger { get; init; }

    /// <summary>The activity log. Told what a press did — never a key, never any part of one.</summary>
    public required Action<string> Activity { get; init; }

    /// <summary>How work that finished off the window's thread gets back onto it: the dispatcher in the app.</summary>
    public required Action<Action> Ui { get; init; }

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>
/// THE CARD'S WORDS FOR ONE PASS, computed without a control so a test can read every one of them. Every figure in them is
/// read off the instrument as it stands — its price with the day and page it was read from, its rates with theirs, or "not
/// dated" where <c>decision-models.json</c> moved one — and TradeAgent's own bound from the gate's own constants, named as
/// TradeAgent's. The card prints no limit and no price of its own.
/// </summary>
sealed record PerceptionWords(
    string Model, string? Stopped, string Price, string Tokens, string Requests, string OwnBound, string Worst,
    string KeyDestination, bool KeyBuiltIn, string KeyState, string Spent, bool CapBindsFirst);

/// <summary>
/// THE OWNER'S PERCEPTION CARD (<c>U-decision-card</c>) — on the Safety page, after "What the AI costs", because its budget
/// is a slice of that cap. Built once and updated in place on the five-second pass, like every card on that page.
///
/// <list type="bullet">
/// <item><b>Which decision model</b>: Jev at TypeSafe or through OpenRouter, one press each — the card's choice and no
/// setting: it decides which address the box binds a key to and whose price and limits are shown, and it grants
/// nothing.</item>
/// <item><b>The key</b>: pasted into a masked box, taken by <see cref="SafetyPage.BuildSaveHarnessKey"/> into
/// <see cref="AppHost.PerceptionKey"/> for the chosen instrument's address — one press for TradeAgent's built-in address,
/// two naming it for any other — the box emptied, the address shown, never a character of the key; forgotten in one
/// press. Memory only, for the reason the worker's is (<see cref="Labels.HarnessKeyHint"/>).</item>
/// <item><b>The budget</b>: perception's own daily ceiling inside the owner's cap. Raising it asks twice and names the
/// figure; lowering it, or zero, is one press. Beside it what perception spent today, and the cap when the cap binds
/// first.</item>
/// <item><b>What it costs and how fast it may be asked</b>: the instrument's price and rates, each dated and paged, and
/// TradeAgent's own bound where none is documented — the figures <see cref="DecisionRateGate"/> enforces.</item>
/// <item><b>The terms</b>, in two sentences with both agreements' addresses: stated, never a click-through — accepting
/// them is the owner's act at the vendor.</item>
/// <item><b>The Test press</b>: ONE press, ONE fixed question, to the instrument whose address the key was pasted for and
/// never the other; its worst case named beside it, disabled while it flies; the answer shown as the port returned it.</item>
/// </list>
/// </summary>
sealed class PerceptionCard
{
    /// <summary>The two agreements, by address: stated on the card, linked, never copied.</summary>
    public const string TypeSafeTerms = "https://typesafe.ai/legal/mca";

    public const string OpenRouterTerms = "https://openrouter.ai/terms";

    /// <summary>
    /// THE TERMS, IN TWO SENTENCES — both pages re-read 2026-10-10, unchanged since the dates they carry (TypeSafe's
    /// agreement of 2026-09-23 § 2.3(b) and § 4; OpenRouter's terms of 2026-08-31 § 5, "Model Terms"). Paraphrase, never
    /// a copy (R02 § 2 item 4), and what binds an OpenRouter user's Jev answers is said to be UNKNOWN because it is.
    /// </summary>
    public const string Terms =
        "At TypeSafe, its Master Customer Agreement (" + TypeSafeTerms + ", last updated 2026-09-23, read 2026-10-10) governs: "
        + "TypeSafe may use what you send it to derive service telemetry, and Jev's answers may never be used to train or "
        + "imitate a model — TradeAgent keeps them only to replay and judge them. Through OpenRouter (" + OpenRouterTerms
        + ", last updated 2026-08-31, read 2026-10-10), which agreement covers Jev's answers is UNKNOWN, and TradeAgent keeps "
        + "TypeSafe's rule either way.";

    readonly PerceptionSources _src;
    string _chosen;

    readonly Panel _choiceRow;
    readonly TextBlock _stopped = Ui.With(Ui.Micro(""), t => { t.Foreground = Theme.Caution; t.IsVisible = false; });
    readonly TextBlock _price = Ui.Micro("");
    readonly TextBlock _tokens = Ui.Micro("");
    readonly TextBlock _requests = Ui.Micro("");
    readonly TextBlock _ownBound = Ui.Micro("");
    readonly TextBlock _keyDestination = Ui.Muted("");
    readonly TextBlock _keyNote = Ui.Micro("");
    readonly TextBlock _budgetNote = Ui.Micro("");
    readonly TextBlock _spent = Ui.Micro("");

    /// <summary>The address the key box last showed; a change disarms a half-made press.</summary>
    string? _destinationShown;

    /// <summary>Why the last press did not take the key, until the next press.</summary>
    string? _keyNotTaken;

    internal TextBox KeyBox { get; }
    internal Button SaveKey { get; }
    internal Button ForgetKey { get; }
    internal NumericUpDown BudgetBox { get; }
    internal Button SaveBudget { get; }

    /// <summary>The choice buttons, in <see cref="DecisionInstruments.BuiltIn"/>'s order.</summary>
    internal IReadOnlyList<Button> Choices => [.. _choiceRow.Children.OfType<Button>()];

    /// <summary>What the last pass and the last press put on the card, for a test to read.</summary>
    internal PerceptionWords? Shown { get; private set; }

    public Control Root { get; }

    /// <summary>The card as the app builds it, over the host's own key holder, models, settings and ledger.</summary>
    public static PerceptionCard For(AppHost host) => new(new PerceptionSources
    {
        Key = host.PerceptionKey,
        Instruments = () => DecisionInstruments.Read(),
        Model = host.DecisionModel,
        Budget = () => host.Gateway.Settings.PerceptionDailyBudget,
        SaveBudget = budget => host.Gateway.Update(s => s.PerceptionDailyBudget = budget),
        Cap = () => host.Gateway.Settings.AiDailyCostCap,
        Currency = () => host.SpendToday.Currency,
        Ledger = () => new AiAttemptStore(host.Db),
        Activity = line => host.Gateway.Log.Activity(line),
        Ui = work => Dispatcher.UIThread.Post(work)
    });

    public PerceptionCard(PerceptionSources sources)
    {
        _src = sources;

        // THE KEY'S OWN INSTRUMENT FIRST, when one is held: the card opens on what the Test press would ask.
        var shipped = DecisionInstruments.BuiltIn();
        _chosen = Read().Instruments.FirstOrDefault(i => _src.Key.Held && UrlOrigin.Of(i.Endpoint) == _src.Key.Origin)?.Id
                  ?? shipped[0].Id;

        // ONE PRESS EACH: choosing which model the card shows takes no permission and gives none.
        _choiceRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = Theme.S2, LineSpacing = Theme.S2 };
        foreach (var instrument in shipped)
        {
            var id = instrument.Id;
            _choiceRow.Children.Add(Ui.Button(instrument.DisplayName, () => Choose(id)));
        }

        KeyBox = Ui.With(Ui.TextField(placeholder: Labels.PerceptionKey), t =>
        {
            t.PasswordChar = '•';
            t.Width = double.NaN;
            t.HorizontalAlignment = HorizontalAlignment.Stretch;
        });
        SaveKey = SafetyPage.BuildSaveHarnessKey(_src.Key, () => KeyBox.Text, Destination, AfterKeyPress);
        ForgetKey = Ui.Button(Labels.ForgetHarnessKey, Forget);

        BudgetBox = Ui.NumberField(ReadOr(_src.Budget, 0m), 0m, 0.5m);
        SaveBudget = BuildSaveBudget(() => ReadOr(_src.Budget, 0m), PendingBudget, () => ReadOr(_src.Currency, ""), Save);

        Root = Ui.Section("Perception", Ui.Col(Theme.S2,
            Ui.Muted("Perception is a decision model asked short, typed questions — Jev, a model that answers each one with a "
                     + "probability. Nothing in TradeAgent asks it anything yet except the test at the bottom of this card. Its "
                     + "key, its daily budget and what it costs are set here, and no AI can reach any of them."),
            Ui.Spacer(Theme.S2),
            Ui.FieldRow(Labels.PerceptionInstrument, _choiceRow),
            _stopped,
            _price,
            _tokens,
            _requests,
            _ownBound,
            Ui.Micro(Terms),
            Ui.Divider(),
            Ui.FieldRow(Labels.PerceptionKey, KeyBox, Labels.HarnessKeyHint),
            _keyDestination,
            Ui.Spacer(Theme.S2),
            // A ROW THAT WRAPS: armed, the press names the whole address.
            Ui.Wrap(Theme.S3, SaveKey, ForgetKey),
            _keyNote,
            Ui.Divider(),
            Ui.FieldRow(Labels.PerceptionDailyBudget, BudgetBox,
                "Inside your daily AI limit, never on top of it. 0 means perception is never asked. Raising it lets "
                + "perception spend more of your money, so it asks again first."),
            Ui.Spacer(Theme.S2),
            SaveBudget,
            _budgetNote,
            _spent));

        Refresh();
    }

    /// <summary>The five-second pass: every reading in place, nothing rebuilt, a half-made press kept unless its address moved.</summary>
    public void Update() => Refresh();

    // ---- the choice and the facts -------------------------------------------------------------------------------

    void Choose(string id)
    {
        _chosen = id;
        Refresh();
    }

    DecisionInstrumentsRead Read()
    {
        try { return _src.Instruments(); }
        catch (Exception ex) { return new([], $"TradeAgent could not read its decision models ({ex.Message}), so none is called.", []); }
    }

    static T ReadOr<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    /// <summary>
    /// THE CARD'S WORDS for the chosen instrument as it stands, its key and today's spending. Static, so the words are
    /// tested without a window.
    /// </summary>
    internal static PerceptionWords Words(string chosen, DecisionInstrumentsRead read, bool held, string? keyOrigin,
        decimal budget, decimal cap, string currency, AiAttemptTotals? perception, AiAttemptTotals? day)
    {
        var shipped = DecisionInstruments.BuiltIn().Single(i => i.Id == chosen);
        var live = read.Instruments.FirstOrDefault(i => i.Id == chosen);
        var i = live ?? shipped;
        var limits = i.Limits;
        var rates = DecisionInstruments.BuiltInLimits(chosen) ?? shipped.Limits;
        var dated = $"read {i.RatesReadOn} from {i.RatesSource}";

        var priceMoved = i.InputPerMillion != shipped.InputPerMillion || i.OutputPerMillion != shipped.OutputPerMillion
                         || i.PricedOn != shipped.PricedOn || i.PriceSource != shipped.PriceSource;
        var price = $"Price: {Exact(i.InputPerMillion)} {i.Currency} per million input tokens and {Exact(i.OutputPerMillion)} per "
                    + $"million output — {(priceMoved ? "set in decision-models.json, " : "")}read {i.PricedOn} from {i.PriceSource}.";

        var tokens = limits.TokensPerSecond > 0
            ? $"Tokens a second: {limits.TokensPerSecond:N0} — "
              + (limits.TokensPerSecond == rates.TokensPerSecond ? $"the host's figure, {dated}." : "set in decision-models.json, not dated.")
            : "Tokens a second: " + (rates.TokensPerSecond == 0
                ? $"not documented by the host ({dated})."
                : "set to zero in decision-models.json, not dated.");
        var requests = limits.RequestsPerSecond > 0
            ? $"Requests a second: {limits.RequestsPerSecond:N0} — "
              + (limits.RequestsPerSecond == rates.RequestsPerSecond ? $"the host's figure, {dated}." : "set in decision-models.json, not dated.")
            : "Requests a second: " + (rates.RequestsPerSecond == 0
                ? $"not documented by the host ({dated})."
                : "set to zero in decision-models.json, not dated.");

        var inFlight = limits.TokensPerSecond > 0 ? limits.TokensPerSecond / i.ReservedTokens : DecisionRateGate.OwnCallsInFlight;
        var own = $"Where a rate is not documented TradeAgent applies its own bound — {DecisionRateGate.OwnCallsInFlight} call in "
                  + $"flight at a time and {DecisionRateGate.OwnRequestsPerSecond} a second; that is TradeAgent's figure, not the "
                  + $"host's. A call is counted at the most it could use — {i.ReservedTokens:N0} tokens — until its answer says "
                  + $"what it used, so {inFlight:N0} call{(inFlight == 1 ? "" : "s")} can be in flight at once here.";

        var worst = $"Asks {i.DisplayName} one fixed question, once. The most it can cost is {Exact(i.Reservation)} {i.Currency} — "
                    + $"{i.ReservedTokens:N0} tokens at the price above — taken from the perception budget.";

        var origin = UrlOrigin.Of(i.Endpoint);
        var builtInOrigin = UrlOrigin.Of(shipped.Endpoint);
        var builtIn = origin is not null && origin == builtInOrigin;
        var destination = origin is null
            ? $"{i.DisplayName} has no address a key could be sent to, so a key pasted here is not taken."
            : builtIn
                ? $"A key pasted here is sent only to {origin}, TradeAgent's built-in address for {i.DisplayName}."
                : $"A key pasted here would be sent only to {origin} — not TradeAgent's built-in address for {i.DisplayName}, "
                  + $"which is {builtInOrigin}.";

        var heldFor = held ? read.Instruments.FirstOrDefault(x => UrlOrigin.Of(x.Endpoint) == keyOrigin)?.DisplayName : null;

        string spent;
        var capFirst = false;
        if (perception is null || day is null)
            spent = "Today's perception spending could not be read.";
        else if (budget <= 0m)
            spent = $"Perception is off: its budget is 0, so no question is sent. Spent or set aside today: "
                    + $"{Exact(perception.Spent + perception.Reserved)} {currency}.";
        else
        {
            var mine = perception.Spent + perception.Reserved;
            var all = day.Spent + day.Reserved;
            var budgetRoom = Math.Max(0m, budget - mine);
            var capRoom = Math.Max(0m, cap - all);
            capFirst = capRoom < budgetRoom;
            spent = $"Perception has spent or set aside {Exact(mine)} of {Exact(budget)} {currency} today."
                    + (capFirst
                        ? $" Your daily AI limit of {Exact(cap)} {currency} binds first: {Exact(capRoom)} {currency} of it is left "
                          + "today, for everything the AI spends."
                        : "");
        }

        return new(i.DisplayName, Stopped(read, chosen), price, tokens, requests, own, worst, destination, builtIn,
            Labels.PerceptionKeyState(held, keyOrigin, heldFor), spent, capFirst);
    }

    /// <summary>
    /// WHY AN INSTRUMENT CANNOT BE CALLED, in <see cref="DecisionInstrumentsRead"/>'s own words — the file unreadable, or a
    /// row about it refused — or null because it can.
    /// </summary>
    internal static string? Stopped(DecisionInstrumentsRead read, string id)
    {
        if (read.Instruments.Any(i => i.Id == id)) return null;
        if (read.Unreadable is { } why) return why;
        var about = read.Refused.Where(r => r.StartsWith($"'{id}'", StringComparison.Ordinal)).ToList();
        return about.Count > 0
            ? string.Join(" ", about)
            : $"{id} is not one of the decision models TradeAgent can call.";
    }

    /// <summary>A sum of money exactly as the ledger holds it: no rounding, because a call costs a fraction of a cent.</summary>
    internal static string Exact(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    void Refresh()
    {
        var read = Read();
        var held = _src.Key.Held;
        var origin = _src.Key.Origin;

        AiAttemptTotals? perception = null, day = null;
        try
        {
            if (_src.Ledger() is { } ledger)
            {
                var (from, to) = OwnerDay.Window(_src.Now());
                perception = ledger.TotalsBetween(from, to, AppPrincipals.Perception);
                day = ledger.TotalsBetween(from, to);
            }
        }
        catch (Exception) { perception = day = null; }

        var w = Words(_chosen, read, held, origin, ReadOr(_src.Budget, 0m), ReadOr(_src.Cap, 0m), ReadOr(_src.Currency, ""),
            perception, day);
        Shown = w;

        var i = 0;
        foreach (var instrument in DecisionInstruments.BuiltIn())
            if (i < _choiceRow.Children.Count && _choiceRow.Children[i++] is Button b) Ui.Emphasise(b, instrument.Id == _chosen);

        _stopped.Text = w.Stopped ?? "";
        _stopped.IsVisible = w.Stopped is not null;
        _price.Text = w.Price;
        _tokens.Text = w.Tokens;
        _requests.Text = w.Requests;
        _ownBound.Text = w.OwnBound;

        // WHERE A PASTE WOULD GO. A press half-made against one address is disarmed the moment the card shows another.
        var destination = Destination();
        if (!string.Equals(destination.Origin, _destinationShown, StringComparison.Ordinal))
        {
            Ui.DisarmConfirm(SaveKey);
            _destinationShown = destination.Origin;
        }
        _keyDestination.Text = w.KeyDestination;
        _keyDestination.Foreground = w.KeyBuiltIn ? Theme.TextMuted : Theme.Caution;
        _keyNote.Text = _keyNotTaken ?? w.KeyState;
        _keyNote.Foreground = _keyNotTaken is null && held ? Theme.TextMuted : Theme.Caution;

        _spent.Text = w.Spent;
        _spent.Foreground = w.CapBindsFirst ? Theme.Caution : Theme.TextMuted;
    }

    // ---- the key ------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE ADDRESS THE BOX BINDS A KEY TO: the chosen instrument's, compared with the one this build ships for it — never
    /// with anything a file says, and a file cannot move it anyway (<see cref="DecisionInstruments"/>).
    /// </summary>
    SafetyPage.KeyDestination Destination()
    {
        var shipped = DecisionInstruments.BuiltIn().Single(i => i.Id == _chosen);
        var live = Read().Instruments.FirstOrDefault(i => i.Id == _chosen) ?? shipped;
        var origin = UrlOrigin.Of(live.Endpoint);
        var builtInOrigin = UrlOrigin.Of(shipped.Endpoint);
        return new(origin, origin is not null && origin == builtInOrigin, builtInOrigin);
    }

    /// <summary>Empties the box and says what happened WITHOUT naming any part of the key, nor the address, in the log.</summary>
    void AfterKeyPress(bool taken)
    {
        KeyBox.Text = "";
        _keyNotTaken = taken ? null : Labels.HarnessKeyNotTaken;
        if (taken)
            _src.Activity(_src.Key.Held
                ? "A key for the decision model is held for this session"
                : "No key for the decision model is held");
        Refresh();
    }

    /// <summary>ONE PRESS: forgetting takes room away, and that happens at once.</summary>
    void Forget()
    {
        _src.Key.Clear();
        KeyBox.Text = "";
        _keyNotTaken = null;
        _src.Activity("The decision model's key was forgotten");
        Refresh();
    }

    // ---- the budget ---------------------------------------------------------------------------------------------

    /// <summary>
    /// PERCEPTION'S DAILY BUDGET: RAISING IT ASKS TWICE AND NAMES THE FIGURE; LOWERING IT, OR ZERO, SAVES IN ONE PRESS.
    /// The cap's rule (<see cref="SafetyPage.BuildSaveDailyCap"/>): a raise gives perception more of the owner's money,
    /// and hesitating on the way down costs it. A factory, so a test presses the control the owner sees.
    /// </summary>
    internal static Button BuildSaveBudget(Func<decimal> current, Func<decimal> pending, Func<string> currency, Action save)
    {
        var b = Ui.ConfirmIf(Labels.SavePerceptionBudget,
            () => pending() > current() ? Labels.RaisePerceptionBudgetArmed(Labels.Money(pending(), currency())) : null,
            save, "primary");
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    decimal PendingBudget() => BudgetBox.Value ?? ReadOr(_src.Budget, 0m);

    void Save()
    {
        var budget = PendingBudget();
        _src.SaveBudget(budget);
        var money = Labels.Money(budget, ReadOr(_src.Currency, ""));
        _src.Activity($"Perception may now spend up to {money} a day");
        _budgetNote.Text = $"Saved. Perception may spend up to {money} a day.";
        Refresh();
    }
}
