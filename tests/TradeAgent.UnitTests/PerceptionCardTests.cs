using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-decision-card items 2 and 3 — THE OWNER'S PERCEPTION CARD: the decision model's key pasted in the app's own window,
/// shown only as an address and forgotten in one press; perception's daily budget, raised only by two presses; every price
/// and limit read off the instrument, dated; and the one-press Test press, which asks the key's own instrument one fixed
/// question and shows what it cost.
///
/// <para>Each test presses the card the Safety page builds — <see cref="PerceptionCard"/>, over sources a test can hold —
/// on ONE thread, synchronously: an Avalonia control belongs to the thread that made it, so the Test press runs its call off
/// that thread and hands the result back through <see cref="PerceptionSources.Ui"/>, which this rig queues and the test
/// drains. Hosts are <see cref="FakeProvider"/> on loopback; a rig given no host for an instrument builds no model for it,
/// so nothing here can reach a vendor. The layout and colours are not asserted and not claimed: what is read back is the
/// words and what the presses did.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class PerceptionCardTests(ITestOutputHelper log) : IDisposable
{
    /// <summary>A pretend key nobody else could have produced, so finding it anywhere is evidence.</summary>
    readonly string _pasted = $"not-a-real-perception-key-{Guid.NewGuid():n}";

    public void Dispose()
    {
        if (File.Exists(DecisionInstruments.OverridePath)) File.Delete(DecisionInstruments.OverridePath);
    }

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static readonly DecisionInstrument TypeSafe = DecisionInstruments.BuiltIn().Single(i => i.Id == DecisionInstruments.TypeSafeDirect);
    static readonly DecisionInstrument OpenRouter = DecisionInstruments.BuiltIn().Single(i => i.Id == DecisionInstruments.OpenRouterJev);

    /// <summary>The canned answer to the card's one test question, spelled the way a host serves it.</summary>
    const string TestAnswers = """{"is_a_test":{"type":"noul","noul":0.96875}}""";

    /// <summary>
    /// THE CARD OVER A TEST'S OWN SOURCES: the instruments as <c>decision-models.json</c> leaves them, each pointed at its
    /// loopback host where the test gave one; a model only for an instrument with a host; the rig's ledger and tape, its own
    /// key holder, budget, cap and activity log; and a queue standing in for the window's dispatcher.
    /// </summary>
    sealed class CardRig : IDisposable
    {
        readonly ConcurrentQueue<Action> _posted = new();
        readonly Dictionary<string, FakeProvider> _hosts = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeSafeWire> _wires = new(StringComparer.Ordinal);

        public CardRig(FakeProvider? typeSafe = null, FakeProvider? openRouter = null)
        {
            if (typeSafe is not null) _hosts[DecisionInstruments.TypeSafeDirect] = typeSafe;
            if (openRouter is not null) _hosts[DecisionInstruments.OpenRouterJev] = openRouter;
            Card = new PerceptionCard(new PerceptionSources
            {
                Key = Key,
                Instruments = Instruments,
                Model = Model,
                Budget = () => Budget,
                SaveBudget = b => Budget = b,
                Cap = () => Cap,
                Currency = () => "USD",
                Ledger = () => new AiAttemptStore(Store.Db),
                Activity = Activity.Add,
                Ui = _posted.Enqueue
            });
        }

        public DecisionPortTests.Rig Store { get; } = new();
        public HarnessKey Key { get; } = new();
        public decimal Budget { get; set; } = 1m;
        public decimal Cap { get; set; } = 5m;

        /// <summary>The wires' clock — the rate gate's too — which a test moves on by hand when it needs a new second.</summary>
        public DateTimeOffset Clock { get; set; } = DateTimeOffset.Now;

        public List<string> Activity { get; } = [];
        public PerceptionCard Card { get; }

        DecisionInstrument Point(DecisionInstrument i) =>
            _hosts.TryGetValue(i.Id, out var host)
                ? i with
                {
                    Endpoint = i.Id == DecisionInstruments.OpenRouterJev
                        ? $"http://127.0.0.1:{host.Port}/api/v1/systemone"
                        : $"{host.BaseUrl}/systemone"
                }
                : i;

        DecisionInstrumentsRead Instruments()
        {
            var read = DecisionInstruments.Read();
            return read with { Instruments = [.. read.Instruments.Select(Point)] };
        }

        /// <summary>A wire per instrument, as the app keeps one — and NONE for an instrument this rig gave no host.</summary>
        IDecisionModel? Model(string id)
        {
            if (!_hosts.ContainsKey(id) || Instruments().Instruments.FirstOrDefault(i => i.Id == id) is not { } live) return null;
            lock (_wires)
            {
                if (_wires.TryGetValue(id, out var held) && held.Instrument == live) return held;
                held?.Dispose();
                return _wires[id] = new TypeSafeWire(live, Key, Store.Db, () => Store.Tape, () => Cap, () => Budget, Store.Live,
                    () => "USD", requestTimeout: TimeSpan.FromSeconds(10), now: () => Clock, gate: new DecisionRateGate());
            }
        }

        /// <summary>Waits for the Test press in flight and runs what it handed back, on this thread.</summary>
        public void Settle()
        {
            Assert.True(Card.Pressing?.Wait(TimeSpan.FromSeconds(60)) ?? true, "the Test press never finished");
            while (_posted.TryDequeue(out var work)) work();
        }

        /// <summary>Every word the card shows: each text, button, placeholder and box on it.</summary>
        public List<string> Words()
        {
            var words = new List<string>();
            foreach (var node in Card.Root.GetLogicalDescendants().Prepend(Card.Root))
                switch (node)
                {
                    case TextBlock t when t.Text is { Length: > 0 } text: words.Add(text); break;
                    case Button b when b.Content is string label: words.Add(label); break;
                    case TextBox box:
                        if (box.PlaceholderText is { Length: > 0 } placeholder) words.Add(placeholder);
                        if (box.Text is { Length: > 0 } typed) words.Add(typed);
                        break;
                }
            return words;
        }

        public void Dispose()
        {
            foreach (var w in _wires.Values) w.Dispose();
            Store.Dispose();
        }
    }

    // ---- (e) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (e) THE PERCEPTION KEY IS PASTED, SHOWN AND FORGOTTEN ON THE CARD — AND NEVER ECHOED. The box is masked and empty;
    /// choosing OpenRouter (one press) binds it to OpenRouter's built-in address, said beside the box before anything is
    /// pasted; one press takes the key into the decision models' own holder for that address and empties the box; the card
    /// says it is held, for which address and which model, and the log says only that a key is held. Not one control, log
    /// line or file holds a character of it. "Forget the key" is one press.
    /// </summary>
    [Fact]
    public void The_perception_key_is_pasted_shown_and_forgotten_on_the_card_and_never_echoed()
    {
        var started = DateTime.UtcNow.AddSeconds(-1);
        using var rig = new CardRig();
        var card = rig.Card;
        var origin = UrlOrigin.Of(OpenRouter.Endpoint)!;

        Assert.Equal('•', card.KeyBox.PasswordChar);
        Assert.Equal(Labels.PerceptionKey, card.KeyBox.PlaceholderText);
        Assert.True(string.IsNullOrEmpty(card.KeyBox.Text));
        Assert.Equal([TypeSafe.DisplayName, OpenRouter.DisplayName], card.Choices.Select(b => b.Content as string));
        Assert.Equal(Labels.PerceptionKeyState(false, null, null), card.Shown!.KeyState);

        Press(card.Choices[1]);
        Assert.Equal($"A key pasted here is sent only to {origin}, TradeAgent's built-in address for {OpenRouter.DisplayName}.",
            card.Shown!.KeyDestination);
        Assert.True(card.Shown.KeyBuiltIn);

        card.KeyBox.Text = _pasted;
        Press(card.SaveKey);
        log.WriteLine(card.Shown.KeyState);

        Assert.True(rig.Key.Held);
        Assert.Equal(origin, rig.Key.Origin);
        Assert.Equal("", card.KeyBox.Text);
        Assert.False(Ui.IsArmed(card.SaveKey));
        Assert.Equal(Labels.PerceptionKeyState(true, origin, OpenRouter.DisplayName), card.Shown.KeyState);
        Assert.Contains(card.Shown.KeyState, rig.Words());
        Assert.Equal(["A key for the decision model is held for this session"], rig.Activity);
        Assert.DoesNotContain(rig.Words(), w => w.Contains(_pasted, StringComparison.Ordinal));
        Assert.Contains(Labels.HarnessKeyHint, rig.Words());

        Press(card.ForgetKey);
        Assert.False(rig.Key.Held);
        Assert.Equal(Labels.PerceptionKeyState(false, null, null), card.Shown.KeyState);
        Assert.Equal("The decision model's key was forgotten", rig.Activity[^1]);
        Assert.All(rig.Activity, line => Assert.DoesNotContain(_pasted, line, StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Words(), w => w.Contains(_pasted, StringComparison.Ordinal));

        // AND NO FILE: nothing under the app's folder holds a byte of it.
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(Paths.Home, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) < started) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception) { continue; }          // a database mid-write is not evidence either way
            if (text.Contains(_pasted, StringComparison.Ordinal)) found.Add(file);
        }
        Assert.True(found.Count == 0, "the key was written to disk:\n  " + string.Join("\n  ", found));
    }

    // ---- (f) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) RAISING THE PERCEPTION BUDGET ASKS TWICE AND LOWERING DOES NOT. The press's factory: a raise arms a sentence that
    /// names the new figure and saves nothing; the second press saves; a lower figure, zero, and the same figure save at
    /// once. Then the card itself: its box and its press write the budget, the line beside it reads the day's spending
    /// against it, zero says perception is off, and the owner's daily AI limit is named when it binds first.
    ///
    /// <para>Make the press save on its first press and the raise goes through unasked. Let an edit of the box leave a
    /// half-made press armed and a confirmation that named one figure saves another.</para>
    /// </summary>
    [Fact]
    public void Raising_the_perception_budget_asks_twice_and_lowering_does_not()
    {
        var saved = 0;
        var current = 1m;
        var pending = 3m;
        var b = PerceptionCard.BuildSaveBudget(() => current, () => pending, () => "USD", () => saved++);

        Press(b);
        Assert.Equal(0, saved);
        Assert.True(Ui.IsArmed(b));
        Assert.Equal(Labels.RaisePerceptionBudgetArmed(Labels.Money(3m, "USD")), b.Content);
        Press(b);
        Assert.Equal(1, saved);
        Assert.Equal(Labels.SavePerceptionBudget, b.Content);

        foreach (var lower in new[] { 0.5m, 0m, current })
        {
            pending = lower;
            Press(b);
            Assert.False(Ui.IsArmed(b));
        }
        Assert.Equal(4, saved);

        // THE CARD'S OWN BOX AND PRESS.
        using var rig = new CardRig();
        var card = rig.Card;
        card.BudgetBox.Value = 4m;
        Press(card.SaveBudget);
        Assert.Equal(1m, rig.Budget);
        Assert.True(Ui.IsArmed(card.SaveBudget));
        Press(card.SaveBudget);
        Assert.Equal(4m, rig.Budget);
        Assert.Equal("Perception has spent or set aside 0 of 4 USD today.", card.Shown!.Spent);
        Assert.Equal("Perception may now spend up to 4 USD a day", rig.Activity[^1]);

        // A CONFIRMATION COMPLETES ONLY THE AMOUNT IT NAMED: armed for 5, the box changed to 100, the armed press saves
        // nothing — editing the box changed what the press meant — and 100 asks twice of its own, naming 100.
        card.BudgetBox.Value = 5m;
        Press(card.SaveBudget);
        Assert.True(Ui.IsArmed(card.SaveBudget));
        Assert.Equal(Labels.RaisePerceptionBudgetArmed(Labels.Money(5m, "USD")), card.SaveBudget.Content);
        card.BudgetBox.Value = 100m;
        Press(card.SaveBudget);
        Assert.Equal(4m, rig.Budget);
        Assert.True(Ui.IsArmed(card.SaveBudget));
        Assert.Equal(Labels.RaisePerceptionBudgetArmed(Labels.Money(100m, "USD")), card.SaveBudget.Content);
        Press(card.SaveBudget);
        Assert.Equal(100m, rig.Budget);

        card.BudgetBox.Value = 0m;
        Press(card.SaveBudget);
        Assert.Equal(0m, rig.Budget);
        Assert.StartsWith("Perception is off: its budget is 0, so no question is sent.", card.Shown!.Spent, StringComparison.Ordinal);

        // THE CAP BINDS FIRST: the council has spent all but 0.1 of a 0.5 limit, and perception's budget is 1.
        rig.Budget = 1m;
        rig.Cap = 0.5m;
        var store = new AiAttemptStore(rig.Store.Db);
        var now = DateTimeOffset.Now;
        var turn = $"turn-chair-{Guid.NewGuid():n}";
        store.Begin(new AiAttempt { Id = turn, StartedAt = now, ReservedCost = 0.4m, Role = CouncilRoles.Operations });
        Assert.True(store.End(turn, 0, now, 1000, 0, 0, 100, 0, "gpt-5.6-luna", 0.4m, null, null));
        card.Update();
        log.WriteLine(card.Shown!.Spent);
        Assert.True(card.Shown.CapBindsFirst);
        Assert.Equal("Perception has spent or set aside 0 of 1 USD today. Your daily AI limit of 0.5 USD binds first: 0.1 USD of "
                     + "it is left today, for everything the AI spends.", card.Shown.Spent);
    }

    // ---- (g) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) THE TEST PRESS ASKS THE KEY'S OWN INSTRUMENT ONCE AND SHOWS WHAT IT COST. With the key pasted for OpenRouter's
    /// address, one press sends exactly one request — to OpenRouter's host and never TypeSafe's, whose address would make the
    /// holder forget the key — disabled while it flies, a second press sending nothing; the call is a <c>decision_call</c>
    /// row labelled <c>owner-test</c>, and the card shows who answered and that it is pinned, the probability as served, the
    /// latency and tokens, and the BILLED cost — never as an estimate. Pasted for TypeSafe, the same press asks TypeSafe and
    /// shows the ESTIMATE with its basis — never as a bill; a host error says the reservation stands; and an instrument the
    /// file stopped says why in the file's own words, asking no one. The key is still held throughout.
    ///
    /// <para>Make the press ask the first instrument instead of the key's and OpenRouter's host sees nothing — and the key is
    /// gone.</para>
    /// </summary>
    [Fact]
    public void The_test_press_asks_the_keys_own_instrument_once_and_shows_what_it_cost()
    {
        using var typeSafeHost = new FakeProvider();
        using var openRouterHost = new FakeProvider();
        typeSafeHost.Answer(FakeProvider.SystemOne("jev-1.13.0", TestAnswers, 300, 20));
        openRouterHost.Answer(FakeProvider.SystemOne("typesafe/jev-1.13-20260917", TestAnswers, 300, 20,
            id: "gen-owner-test", provider: "TypeSafe", cost: 0.0000311m));
        using var rig = new CardRig(typeSafeHost, openRouterHost);
        var card = rig.Card;

        // THE KEY, FOR OPENROUTER'S LOOPBACK ADDRESS: not TradeAgent's built-in one, so the press asks twice, naming it.
        Press(card.Choices[1]);
        var openRouterOrigin = UrlOrigin.Of($"http://127.0.0.1:{openRouterHost.Port}")!;
        Assert.False(card.Shown!.KeyBuiltIn);
        card.KeyBox.Text = _pasted;
        Press(card.SaveKey);
        Assert.Equal(Labels.SendHarnessKeyArmed(openRouterOrigin), card.SaveKey.Content);
        Press(card.SaveKey);
        Assert.Equal(openRouterOrigin, rig.Key.Origin);

        // ITS WORST CASE, NAMED BESIDE IT: one reservation.
        Assert.Equal($"Asks {OpenRouter.DisplayName} one fixed question, once. The most it can cost is "
                     + $"{PerceptionCard.Exact(OpenRouter.Reservation)} USD — 65,536 tokens at the price above — taken from the "
                     + "perception budget.", card.Shown.Worst);
        Assert.Contains(card.Shown.Worst, rig.Words());

        // ONE PRESS, ONE REQUEST, DISABLED WHILE IT FLIES.
        using var hold = new ManualResetEventSlim();
        openRouterHost.Arriving = () => hold.Wait(TimeSpan.FromSeconds(30));
        Press(card.TestButton);
        for (var i = 0; i < 1500 && openRouterHost.Requests.Count < 1; i++) Thread.Sleep(20);
        Assert.False(card.TestButton.IsEnabled);
        Press(card.TestButton);
        hold.Set();
        rig.Settle();
        log.WriteLine(card.ResultText);

        Assert.True(card.TestButton.IsEnabled);
        Assert.Single(openRouterHost.Requests);
        Assert.Empty(typeSafeHost.Requests);
        Assert.Equal([$"Bearer {_pasted}"], openRouterHost.Keys);
        Assert.True(rig.Key.Held);
        Assert.Equal(openRouterOrigin, rig.Key.Origin);

        var record = Assert.Single(rig.Store.Tape.RecordedCalls());
        Assert.Equal(PerceptionCard.TestSchema, record.Call.Schema.Id);
        Assert.Equal(1, record.Call.Schema.Version);
        Assert.Equal([PerceptionCard.TestSource], record.Call.Sources);
        Assert.Equal(DecisionInstruments.OpenRouterJev, record.Call.Instrument);
        Assert.False(record.Unpinned);

        var result = card.ResultText;
        Assert.StartsWith("ANSWERED by typesafe/jev-1.13-20260917 — PINNED.", result, StringComparison.Ordinal);
        Assert.Contains("The probability it gave that this is a test message: 0.96875.", result, StringComparison.Ordinal);
        Assert.Matches(@"Latency: \d+ ms, measured by TradeAgent\.", result);
        Assert.Contains("Tokens: 300 in, 20 out, as the host reported them.", result, StringComparison.Ordinal);
        Assert.Contains("Billed by the host: 0.0000311 USD.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Estimated", result, StringComparison.Ordinal);
        Assert.Equal($"The Perception card's test question to {OpenRouter.DisplayName}: ANSWERED", rig.Activity[^1]);
        Assert.Equal(0.0000311m, new AiAttemptStore(rig.Store.Db).Get(record.Call.AttemptId)!.Cost);

        // PASTED FOR TYPESAFE: the same press asks TypeSafe, and its figure is an ESTIMATE with its basis.
        Press(card.Choices[0]);
        card.KeyBox.Text = _pasted;
        Press(card.SaveKey);
        Press(card.SaveKey);
        Assert.Equal(UrlOrigin.Of(typeSafeHost.BaseUrl), rig.Key.Origin);
        Press(card.TestButton);
        rig.Settle();
        log.WriteLine(card.ResultText);
        Assert.Single(typeSafeHost.Requests);
        Assert.Single(openRouterHost.Requests);
        Assert.StartsWith("ANSWERED by jev-1.13.0 — PINNED.", card.ResultText, StringComparison.Ordinal);
        Assert.Contains($"Estimated, not billed — the host reports no bill: {PerceptionCard.Exact(300m * 0.042m / 1_000_000m)} USD, "
                        + $"at the {TypeSafe.PriceBasis}.", card.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Billed", card.ResultText, StringComparison.Ordinal);

        // A HOST ERROR: asked once, and the reservation stands as its cost.
        typeSafeHost.AlwaysAnswer = HttpStatusCode.InternalServerError;
        Press(card.TestButton);
        rig.Settle();
        log.WriteLine(card.ResultText);
        Assert.Equal(2, typeSafeHost.Requests.Count);
        Assert.StartsWith("FAILED — the host answered HTTP 500 (http-500).\n", card.ResultText, StringComparison.Ordinal);
        Assert.EndsWith($"\nIt was asked once, and its reservation of {PerceptionCard.Exact(TypeSafe.Reservation)} USD stands as "
                        + "its cost, because nothing says what it used.", card.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Billed", card.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("Estimated", card.ResultText, StringComparison.Ordinal);

        // A REFUSAL IS SHOWN IN THE PORT'S OWN WORDS: perception's budget is too small for one reservation. A second on, so
        // the failed call's tokens — counted at its bound, since it reported none — have left the rate gate's window.
        typeSafeHost.AlwaysAnswer = null;
        rig.Budget = TypeSafe.Reservation / 2m;
        rig.Clock += DecisionRateGate.Window;
        Press(card.TestButton);
        rig.Settle();
        Assert.Equal($"REFUSED — nothing was sent and nothing was charged.\n{Labels.PerceptionBudgetReached}", card.ResultText);
        Assert.Equal(2, typeSafeHost.Requests.Count);
        rig.Budget = 1m;

        // AN INSTRUMENT THE FILE STOPPED: the card binds the key to its built-in address, and the press asks no one and says
        // why in the file's own words.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","pin":"jev-9.9.9"}]
            """);
        card.Update();
        Assert.NotNull(card.Shown!.Stopped);
        Assert.True(card.Shown.KeyBuiltIn);
        card.KeyBox.Text = _pasted;
        Press(card.SaveKey);
        Assert.Equal(UrlOrigin.Of(TypeSafe.Endpoint), rig.Key.Origin);
        Press(card.TestButton);
        rig.Settle();
        log.WriteLine(card.ResultText);
        Assert.Equal(PerceptionCard.Stopped(DecisionInstruments.Read(), DecisionInstruments.TypeSafeDirect), card.ResultText);
        Assert.StartsWith("'typesafe-direct' in decision-models.json names 'pin'", card.ResultText, StringComparison.Ordinal);
        Assert.Equal(2, typeSafeHost.Requests.Count);
        Assert.True(rig.Key.Held);

        // AND NO KEY, NO ONE ASKED.
        Press(card.ForgetKey);
        Press(card.TestButton);
        rig.Settle();
        Assert.Equal(Labels.PerceptionTestNoKey, card.ResultText);
        Assert.Equal(2, typeSafeHost.Requests.Count);
        Assert.Single(openRouterHost.Requests);
    }

    // ---- (h) ------------------------------------------------------------------------------------------------------

    /// <summary>What no word on the card may say: an instruction to run, export or install anything — or a terminal.</summary>
    static readonly Regex Terminal = new(
        @"\b(run|runs|ran|running|export|exports|exporting|install|installs|installing|installed|terminal|command)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// (h) THE CARD STATES NO LIMIT OR PRICE IT DID NOT READ. Every figure on it is read off the instrument as it stands —
    /// the price with the day and page it was read from, each rate with its own, OpenRouter's absent rate as "not documented"
    /// with the page that says so — and TradeAgent's own bound from the gate's own constants, named as TradeAgent's. A rate
    /// <c>decision-models.json</c> moved reads "not dated", and a price it moved carries the file's date and page. The terms
    /// name both agreements and say OpenRouter's is UNKNOWN; and no word anywhere on the card tells the owner to run, export
    /// or install anything.
    ///
    /// <para>Print a figure on the card that is not the instrument's — 80 requests a second, say — and the moved rate is
    /// wrong; add a hint that sends the owner to a command and the scan finds it.</para>
    /// </summary>
    [Fact]
    public void The_card_states_no_limit_or_price_it_did_not_read()
    {
        using var rig = new CardRig();
        var card = rig.Card;
        var w = card.Shown!;

        Assert.Equal($"Price: {PerceptionCard.Exact(TypeSafe.InputPerMillion)} {TypeSafe.Currency} per million input tokens and "
                     + $"{PerceptionCard.Exact(TypeSafe.OutputPerMillion)} per million output — read {TypeSafe.PricedOn} from "
                     + $"{TypeSafe.PriceSource}.", w.Price);
        Assert.Equal($"Tokens a second: {TypeSafe.Limits.TokensPerSecond:N0} — the host's figure, read {TypeSafe.RatesReadOn} from "
                     + $"{TypeSafe.RatesSource}.", w.Tokens);
        Assert.Equal($"Requests a second: {TypeSafe.Limits.RequestsPerSecond:N0} — the host's figure, read {TypeSafe.RatesReadOn} "
                     + $"from {TypeSafe.RatesSource}.", w.Requests);
        Assert.Equal($"Where a rate is not documented TradeAgent applies its own bound — {DecisionRateGate.OwnCallsInFlight} call in "
                     + $"flight at a time and {DecisionRateGate.OwnRequestsPerSecond} a second; that is TradeAgent's figure, not the "
                     + $"host's. A call is counted at the most it could use — {TypeSafe.ReservedTokens:N0} tokens — until its answer "
                     + "says what it used, so 1 call can be in flight at once here.", w.OwnBound);
        Assert.Null(w.Stopped);

        Press(card.Choices[1]);
        w = card.Shown!;
        Assert.Equal($"Tokens a second: not documented by the host (read {OpenRouter.RatesReadOn} from {OpenRouter.RatesSource}).", w.Tokens);
        Assert.Equal($"Requests a second: not documented by the host (read {OpenRouter.RatesReadOn} from {OpenRouter.RatesSource}).", w.Requests);
        Assert.Equal($"Price: {PerceptionCard.Exact(OpenRouter.InputPerMillion)} USD per million input tokens and "
                     + $"{PerceptionCard.Exact(OpenRouter.OutputPerMillion)} per million output — read {OpenRouter.PricedOn} from "
                     + $"{OpenRouter.PriceSource}.", w.Price);

        // A FILE MOVES TYPESAFE'S REQUESTS AND ITS PRICE: the rate is not dated, the price carries the file's day and page.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","requests_per_second":40,"input_per_million":0.05,"priced_on":"2026-10-10","price_source":"https://prices.example/jev"}]
            """);
        Press(card.Choices[0]);
        w = card.Shown!;
        log.WriteLine($"{w.Price}\n{w.Tokens}\n{w.Requests}");
        Assert.Equal("Requests a second: 40 — set in decision-models.json, not dated.", w.Requests);
        Assert.Equal($"Tokens a second: {TypeSafe.Limits.TokensPerSecond:N0} — the host's figure, read {TypeSafe.RatesReadOn} from "
                     + $"{TypeSafe.RatesSource}.", w.Tokens);
        Assert.Equal("Price: 0.05 USD per million input tokens and 0 per million output — set in decision-models.json, read "
                     + "2026-10-10 from https://prices.example/jev.", w.Price);
        Assert.Contains(w.Requests, rig.Words());
        Assert.Contains(w.Price, rig.Words());

        // THE CARD'S IN-FLIGHT FIGURE IS THE GATE'S: with either rate undocumented the app's own bound applies whole, so a
        // tokens figure that would hold three calls at their bound beside no requests figure still means one.
        var oneRate = TypeSafe with { Limits = TypeSafe.Limits with { TokensPerSecond = 200_000, RequestsPerSecond = 0 } };
        var alone = PerceptionCard.Words(TypeSafe.Id, new DecisionInstrumentsRead([oneRate], null, []), false, null, 1m, 5m, "USD",
            null, null);
        Assert.EndsWith("until its answer says what it used, so 1 call can be in flight at once here.", alone.OwnBound,
            StringComparison.Ordinal);

        // THE TERMS, AND NOTHING THAT SENDS THE OWNER TO A COMMAND — after a pass and after a press.
        Assert.Contains(PerceptionCard.Terms, rig.Words());
        Assert.Contains(PerceptionCard.TypeSafeTerms, PerceptionCard.Terms, StringComparison.Ordinal);
        Assert.Contains(PerceptionCard.OpenRouterTerms, PerceptionCard.Terms, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", PerceptionCard.Terms, StringComparison.Ordinal);

        Press(card.TestButton);
        rig.Settle();
        Assert.Equal(Labels.PerceptionTestNoKey, card.ResultText);
        var words = rig.Words();
        var told = words.Where(t => Terminal.IsMatch(t)).ToList();
        foreach (var t in told) log.WriteLine($"tells the owner to: {t}");
        Assert.True(told.Count == 0, "the card tells the owner to run, export or install something:\n  " + string.Join("\n  ", told));
        Assert.True(words.Count > 20, $"the card's words were not all read: {words.Count}");
    }
}
