using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Oversight;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EVERY NUMBER THE OWNER TYPES ON A MONEY OR LIMIT SURFACE IS READ ONE STATED WAY, CULTURE-FREE (<c>U-number-entry-culture</c>).
///
/// <para>The owner's live defect of 2026-10-22: on a Mac with a US language and a Belgian region the Safety page read his "0,05" as
/// 5 — a position cap a hundred times larger — and drew his "0.05" back as 0. RED at 988c9487, typing through the stock number box's
/// own commit under clones of the invariant culture carrying de-BE, fr-BE and nl-BE separators: "0.05" read as 5 under all three.</para>
/// </summary>
public class NumberEntryCultureTests
{
    /// <summary>
    /// The invariant culture with another culture's separators — named cultures cannot be built under <c>InvariantGlobalization</c>
    /// (<c>CoreTests.A_price_is_read_the_same_way_whatever_the_threads_culture_says</c>'s precedent). The currency and percent
    /// separators are set too: a number style that allows a currency symbol reads THOSE separators as well, and a clone that left them
    /// invariant would let a point through as a decimal mark that the real culture does not.
    /// </summary>
    internal static CultureInfo Clone(string dec, string grp)
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var n = c.NumberFormat;
        n.NumberDecimalSeparator = n.CurrencyDecimalSeparator = n.PercentDecimalSeparator = dec;
        n.NumberGroupSeparator = n.CurrencyGroupSeparator = n.PercentGroupSeparator = grp;
        return c;
    }

    internal static readonly (string Name, CultureInfo Culture)[] Cultures =
    [
        ("de-BE", Clone(",", ".")), ("fr-BE", Clone(",", " ")), ("nl-BE", Clone(",", ".")), ("de-DE", Clone(",", ".")),
        ("en-GB", Clone(".", ",")), ("en-US", Clone(".", ",")), ("invariant", CultureInfo.InvariantCulture)
    ];

    static string Read(string text, AmountKind kind = AmountKind.Decimal) =>
        Amount.TryRead(text, kind, out var v, out var why) ? Amount.Exact(v) : "refused: " + why;

    [Fact]
    public void The_owners_strings_read_one_way_under_every_culture()
    {
        var expected = new (string Text, string Reads)[]
        {
            ("0,05", "0.05"), ("0.05", "0.05"), ("5", "5"), (" 0,5 ", "0.5"), ("12.5", "12.5"), ("0.000", "0"), ("0,00005", "0.00005"),
            ("1.000", "refused: " + Labels.AmountAmbiguous("1.000", "1000", "1")),
            ("1,000", "refused: " + Labels.AmountAmbiguous("1,000", "1000", "1")),
            ("12,500", "refused: " + Labels.AmountAmbiguous("12,500", "12500", "12.5")),
            ("1e3", "refused: " + Labels.AmountUnreadable("1e3")),
            ("-1", "refused: " + Labels.AmountUnreadable("-1")),
            ("1.000,5", "refused: " + Labels.AmountUnreadable("1.000,5")),
            ("1 000", "refused: " + Labels.AmountUnreadable("1 000")),
            ("5%", "refused: " + Labels.AmountUnreadable("5%")),
            ("$5", "refused: " + Labels.AmountUnreadable("$5")),
            (".5", "refused: " + Labels.AmountUnreadable(".5")),
            ("5.", "refused: " + Labels.AmountUnreadable("5.")),
            ("", "refused: " + Labels.AmountEmpty),
            ("   ", "refused: " + Labels.AmountEmpty),
            ("99999999999999999999999999999999", "refused: " + Labels.AmountTooLarge("99999999999999999999999999999999")),
        };

        var was = CultureInfo.CurrentCulture;
        try
        {
            foreach (var (name, culture) in Cultures)
            {
                CultureInfo.CurrentCulture = culture;
                foreach (var (text, reads) in expected)
                    Assert.True(Read(text) == reads, $"under {name}, “{text}” read as “{Read(text)}”, not “{reads}”");
            }
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    /// <summary>A whole-number box (positions, orders a minute, days, lanes, tokens) refuses any mark, the owner's comma included.</summary>
    [Fact]
    public void A_whole_number_box_refuses_any_decimal_mark()
    {
        Assert.Equal("5", Read("5", AmountKind.Whole));
        Assert.Equal("0", Read("0", AmountKind.Whole));
        Assert.Equal("refused: " + Labels.AmountNotWhole("0,05"), Read("0,05", AmountKind.Whole));
        Assert.Equal("refused: " + Labels.AmountNotWhole("5.0"), Read("5.0", AmountKind.Whole));
        Assert.Equal("refused: " + Labels.AmountNotWhole("1.000"), Read("1.000", AmountKind.Whole));
        Assert.Equal("refused: " + Labels.AmountTooLarge("3000000000"), Read("3000000000", AmountKind.Whole));
        Assert.Contains("a whole number", Labels.AmountNotWhole("0,05"), StringComparison.Ordinal);
    }

    /// <summary>
    /// (fix pass 1) OVER-LONG INPUT IS REFUSED, NEVER ROUNDED: past a decimal figure's exact range — more than 28 places after the mark, or
    /// more than 29 digits in all — the text is refused in words rather than read as a figure that is not what was typed. RED at
    /// 46bfc570: "0.00000000000000000000000000001" read as 0, and a 29-digit figure whose digits a decimal cannot hold lost its tail.
    /// </summary>
    [Fact]
    public void Over_long_input_is_refused_never_rounded()
    {
        const string tiny = "0.00000000000000000000000000001";                 // 29 places: read as 0 before
        const string tail = "9234567890.1234567890123456789";                  // 29 digits a decimal cannot hold: its tail rounded
        const string thirty = "123456789012345678901234567890";                // 30 digits
        const string zeros = "0.100000000000000000000000000000";               // 30 places, all of them typed
        Assert.Equal("refused: " + Labels.AmountTooPrecise(tiny), Read(tiny));
        Assert.Equal("refused: " + Labels.AmountTooPrecise(tail), Read(tail));
        Assert.Equal("refused: " + Labels.AmountTooPrecise(zeros), Read(zeros));
        Assert.StartsWith("refused: ", Read(thirty), StringComparison.Ordinal);
        Assert.StartsWith("refused: ", Read(thirty, AmountKind.Whole), StringComparison.Ordinal);
        Assert.Contains("at most 28", Labels.AmountTooPrecise(tiny), StringComparison.Ordinal);

        // AND WHAT A DECIMAL HOLDS EXACTLY STILL READS, every digit of it.
        Assert.Equal("0.0000000000000000000000000001", Read("0.0000000000000000000000000001"));
        Assert.Equal("1234567890.1234567890123456789", Read("1234567890.1234567890123456789"));
        Assert.Equal("12345678901234567890123456789", Read("12345678901234567890123456789"));
    }

    /// <summary>
    /// A READ FIGURE IS PRINTED BACK EXACTLY: every digit, trailing zeros dropped, never rounded — the money formatter's four places
    /// would turn the owner's 0.00005 into 0.0001.
    /// </summary>
    [Fact]
    public void A_read_figure_is_printed_back_exactly()
    {
        Assert.Equal("0.00005", Amount.Exact(0.00005m));
        Assert.Equal("5", Amount.Exact(5.000m));
        Assert.Equal("0.05 USD", Amount.ExactMoney(0.05m, "USD"));
        Assert.Equal("0.00005", Amount.ExactMoney(0.00005m, ""));
        Assert.Equal("0.0001", Labels.Money(0.00005m, ""));   // why the armed sentences stopped using it
    }

    /// <summary>
    /// (item 2) THE BOX KEEPS EXACTLY WHAT WAS TYPED, and says under it what that reads as. RED at 988c9487 through the stock number
    /// box: "0.05" typed into the envelope's quantity, then the commit the box runs when the owner leaves it, redrew the text as "0".
    /// </summary>
    [Fact]
    public void A_typed_amount_is_never_rewritten_or_rescaled()
    {
        var box = Ui.AmountField(0m, AmountKind.Decimal);
        var changes = 0;
        box.ValueChanged += (_, _) => changes++;
        Assert.Equal("0", box.Box.Text);
        Assert.Equal(0m, box.Value);

        box.Box.Text = "0.05";
        Assert.Equal("0.05", box.Box.Text);
        Assert.Equal(0.05m, box.Value);
        Assert.Null(box.Refusal);
        Assert.Equal(Labels.AmountReads("0.05"), box.Reading.Text);
        Assert.Equal(1, changes);

        box.Box.Text = "0,05";                                  // the same figure, the owner's other spelling: no change raised
        Assert.Equal("0,05", box.Box.Text);
        Assert.Equal(0.05m, box.Value);
        Assert.Equal(Labels.AmountReads("0.05"), box.Reading.Text);
        Assert.Equal(1, changes);

        box.Box.Text = "abc";
        Assert.Equal("abc", box.Box.Text);
        Assert.Null(box.Value);
        Assert.Equal(Labels.AmountUnreadable("abc"), box.Refusal);
        Assert.Equal(box.Refusal, box.Reading.Text);
        Assert.Equal(Theme.Caution, box.Reading.Foreground);
        Assert.Equal(2, changes);

        box.Box.Text = "1.000";
        Assert.Equal("1.000", box.Box.Text);
        Assert.Null(box.Value);
        Assert.Equal(Labels.AmountAmbiguous("1.000", "1000", "1"), box.Reading.Text);

        box.Box.Text = "";
        Assert.Null(box.Value);
        Assert.Equal(Labels.AmountEmpty, box.Reading.Text);

        var whole = Ui.AmountField(2m, AmountKind.Whole);
        whole.Box.Text = "0,05";
        Assert.Null(whole.Value);
        Assert.Equal(Labels.AmountNotWhole("0,05"), whole.Reading.Text);

        var lanes = Ui.AmountField(1m, AmountKind.Whole, 1m, 8m);
        lanes.Box.Text = "0";
        Assert.Equal(Labels.AmountBelow("0", "1"), lanes.Refusal);
        lanes.Box.Text = "9";
        Assert.Equal(Labels.AmountAbove("9", "8"), lanes.Refusal);
        lanes.Box.Text = "8";
        Assert.Equal(8m, lanes.Value);
    }

    // ---- the owner's page, built the way the app builds it (U-settings-shed: the Safety page's board boxes are shed) -----------

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The owner's page over a composed host and the fake platform, handed to <paramref name="body"/> and disposed after.</summary>
    static async Task OnThePage(Func<OwnerPage, AppHost, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(OwnerPageTests.Build(host), host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static T Field<T>(OwnerPage page, string name) =>
        (T)typeof(OwnerPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    /// <summary>
    /// (item 3) EVERY NUMBER BOX ON THE OWNER'S PAGE IS THE ONE FIELD: the built tree holds no stock number control at all, and the four
    /// money boxes that stay the owner's (<c>U-settings-shed</c>; R24 § 4.3) — the real-money cap, the AI's two prices and perception's
    /// budget — are each an <see cref="AmountField"/>. The twenty-two limit, allowance, lane, allocation and envelope boxes left with the
    /// security board's decisions; none of them is on any page now.
    /// </summary>
    [Fact]
    public Task Every_number_box_on_the_owners_page_is_an_amount_field() => OnThePage((page, _) =>
    {
        var all = page.Root.GetLogicalDescendants().ToList();
        Assert.Empty(all.OfType<NumericUpDown>());
        Assert.Equal(4, all.OfType<AmountField>().Count());
        Assert.True(new HashSet<AmountField>([Field<AmountField>(page, "_dailyCap"), Field<AmountField>(page, "_priceIn"),
                Field<AmountField>(page, "_priceOut"), Field<PerceptionCard>(page, "_perception").BudgetBox])
            .SetEquals(all.OfType<AmountField>()));

        // AND THE OVERSIGHT PAGE'S CAP (fix pass 2): the one money box there, built the way the window builds the page.
        using var store = new OversightStore(Path.Combine(TestEnv.Home, $"oversight-entry-{Guid.NewGuid():n}.db"), DateTimeOffset.UtcNow);
        var oversight = new OversightPage(OversightOver(store)).Root.GetLogicalDescendants().ToList();
        Assert.Empty(oversight.OfType<NumericUpDown>());
        Assert.Single(oversight.OfType<AmountField>());
        return Task.CompletedTask;
    });

    static OversightSources OversightOver(OversightStore? store) => new()
    {
        Store = () => store,
        Refusal = () => null,
        Currency = () => "USD",
        Now = () => DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// (fix pass 2) THE OVERSIGHT CAP IS THE ONE MONEY BOX: the owner's comma reads, the raise names the exact figure, an unreadable or empty
    /// box refuses either press and saves nothing, an edit disarms, and a wheel changes nothing. It opens EMPTY while his file is not read —
    /// never on a 0 nobody chose — and is opened on the cap at the first read only while nothing has been typed. RED at 5fa0bf73: the page,
    /// landed by U-org-watcher-b on the stock number box this unit removed, did not build (CS0117 'Ui' has no 'NumberField').
    /// </summary>
    [Fact]
    public void The_oversight_cap_is_the_one_money_box()
    {
        using var store = new OversightStore(Path.Combine(TestEnv.Home, $"oversight-entry-{Guid.NewGuid():n}.db"), DateTimeOffset.UtcNow);
        var page = new OversightPage(OversightOver(store));
        page.Update();
        var box = page.CapBox;
        Assert.Equal("0", box.Box.Text);

        // THE OWNER'S COMMA, PAST FOUR PLACES: read before the press, and the raise names it exactly.
        box.Box.Text = "0,00005";
        Assert.Equal(Labels.AmountReads("0.00005"), box.Reading.Text);
        Press(page.CapSave);
        Assert.Equal(Labels.RaiseOversightCapArmed("0.00005 USD"), page.CapSave.Content);

        // AN EDIT DISARMS; "1,000" — a thousand or one — refuses either press and saves nothing.
        box.Box.Text = "1,000";
        Assert.False(Ui.IsArmed(page.CapSave));
        Press(page.CapSave);
        Press(page.CapSave);
        Assert.Null(store.Cap());
        Assert.Equal(Labels.PressRefused(OversightPage.CapLabel, Labels.AmountAmbiguous("1,000", "1000", "1")), page.CapNote.Text);

        // EMPTY IS NEVER 0.
        box.Box.Text = "";
        Press(page.CapSave);
        Assert.Null(store.Cap());
        Assert.Equal(Labels.PressRefused(OversightPage.CapLabel, Labels.AmountEmpty), page.CapNote.Text);

        // A READ FIGURE SAVES, AND THE NOTE NAMES IT EXACTLY.
        box.Box.Text = "0,00005";
        Press(page.CapSave);
        Press(page.CapSave);
        Assert.Equal(0.00005m, store.Cap()!.Amount);
        Assert.Contains("up to 0.00005 USD a day", page.CapNote.Text, StringComparison.Ordinal);

        // A WHEEL, CLICKED INTO OR NOT, CHANGES NOTHING.
        foreach (var focused in new[] { false, true })
        {
            if (focused) { Focused(box); Focused(box.Box); }
            var wheel = Wheel(box.Box, page.Root);
            box.Box.RaiseEvent(wheel);
            Assert.Equal(("0,00005", (decimal?)0.00005m), (box.Box.Text, box.Value));
            Assert.False(wheel.Handled);
        }

        // OPENED BEFORE THE FILE IS READ: empty and refusing; the first read opens it on the cap — unless the owner typed first.
        OversightStore? late = null;
        var waiting = new OversightPage(new OversightSources
        {
            Store = () => late, Refusal = () => null, Currency = () => "USD", Now = () => DateTimeOffset.UtcNow,
        });
        Assert.Equal("", waiting.CapBox.Box.Text);
        Assert.Null(waiting.CapBox.Value);
        late = store;
        waiting.Update();
        Assert.Equal("0.00005", waiting.CapBox.Box.Text);

        late = null;
        var typedFirst = new OversightPage(new OversightSources
        {
            Store = () => late, Refusal = () => null, Currency = () => "USD", Now = () => DateTimeOffset.UtcNow,
        });
        typedFirst.CapBox.Box.Text = "2";
        late = store;
        typedFirst.Update();
        Assert.Equal("2", typedFirst.CapBox.Box.Text);
    }

    /// <summary>
    /// (item 3) THE OWNER'S COMMA READS AS FIVE HUNDREDTHS IN THE REAL-MONEY CAP'S BOX — re-targeted by <c>U-settings-shed</c> from the
    /// envelope's box of his defect, which left with the board's decisions, to the money box that stays his. RED at 988c9487: "0,05" typed
    /// into the stock number box and committed with Enter read as 5.
    /// </summary>
    [Fact]
    public Task The_owners_comma_reads_as_five_hundredths_in_the_real_money_cap_box() => OnThePage((page, host) =>
    {
        host.Gateway.Update(s => s.AiDailyCostCap = 0m);
        var cap = Field<AmountField>(page, "_dailyCap");
        cap.Box.Text = "0,05";

        Assert.Equal("0,05", cap.Box.Text);
        Assert.Equal(0.05m, cap.Value);
        Assert.Equal(Labels.AmountReads("0.05"), cap.Reading.Text);

        var save = PressLabelled(page, Labels.SaveDailyCap);
        Press(save);
        Assert.True(Ui.IsArmed(save));
        Assert.Equal(Labels.RaiseDailyCapArmed(Amount.ExactMoney(0.05m, host.SpendToday.Currency)), save.Content);
        return Task.CompletedTask;
    });

    /// <summary>The page's button that says <paramref name="label"/> at rest, found in the built tree the owner sees.</summary>
    static Button PressLabelled(OwnerPage page, string label) =>
        page.Root.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, label));

    /// <summary>
    /// (item 4) A BOX THAT READS AS NOTHING REFUSES THE PRESS — EITHER PRESS — AND NEVER SAVES A FALLBACK. RED at 988c9487: an empty cap or
    /// limit box read as the figure already in force. (<c>U-settings-shed</c>: the envelope and limit halves left with the board's boxes;
    /// the AI's price, which stays his, takes the limit's place.)
    /// </summary>
    [Fact]
    public Task An_unreadable_box_refuses_the_press_never_saves_a_fallback() => OnThePage((page, host) =>
    {
        // THE DAILY CAP, "1,000" — a thousand or one: nothing saved, the note names the form. And an armed raise edited into a
        // refusal is disarmed, so its second press saves nothing either.
        var capWas = host.Gateway.Settings.AiDailyCostCap;
        var cap = Field<AmountField>(page, "_dailyCap");
        var saveCap = PressLabelled(page, Labels.SaveDailyCap);
        var capNote = Field<TextBlock>(page, "_capNote");
        cap.Box.Text = "1,000";
        Press(saveCap);
        Press(saveCap);
        Assert.Equal(capWas, host.Gateway.Settings.AiDailyCostCap);
        Assert.Equal(Labels.PressRefused(Labels.DailyCostCap, Labels.AmountAmbiguous("1,000", "1000", "1")), capNote.Text);
        Assert.Contains("write 1000 or 1", capNote.Text, StringComparison.Ordinal);

        cap.Box.Text = Amount.Exact(capWas + 100m);
        Press(saveCap);
        Assert.True(Ui.IsArmed(saveCap));
        cap.Box.Text = "";
        Assert.False(Ui.IsArmed(saveCap));
        Press(saveCap);
        Assert.Equal(capWas, host.Gateway.Settings.AiDailyCostCap);
        Assert.Equal(Labels.PressRefused(Labels.DailyCostCap, Labels.AmountEmpty), capNote.Text);

        // THE AI'S PRICE, ONE HALF EMPTY: refused in words on either press, the rate in force unchanged.
        host.Gateway.Update(s => { s.AiPriceInputPerMillion = 10m; s.AiPriceOutputPerMillion = 50m; });
        Field<AmountField>(page, "_priceIn").Box.Text = "";
        Field<AmountField>(page, "_priceOut").Box.Text = "50";
        var savePrice = PressLabelled(page, Labels.SaveAiPrice);
        Press(savePrice);
        Press(savePrice);
        Assert.Equal((10m, 50m), (host.Gateway.Settings.AiPriceInputPerMillion, host.Gateway.Settings.AiPriceOutputPerMillion));
        Assert.Equal(Labels.PressRefused(Labels.AiPriceIn, Labels.AmountEmpty), Field<TextBlock>(page, "_priceNote").Text);

        // AND A BOX THAT READS AGAIN SAVES, back in the note's resting colour.
        cap.Box.Text = "0,5";
        Press(saveCap);
        Assert.Equal(0.5m, host.Gateway.Settings.AiDailyCostCap);
        Assert.Equal(Theme.TextFaint, capNote.Foreground);
        return Task.CompletedTask;
    });

    /// <summary>
    /// (item 5) THE ARMED GRANT NAMES THE EXACT FIGURE TYPED — and a one-press save's note names the exact figure written. The money
    /// formatter rounds past four places, so before this a raise to 0.00005 asked "up to 0.0001": the second press wrote one figure
    /// under a sentence naming another. (<c>U-settings-shed</c>: the envelope's half left with the board's boxes.)
    /// </summary>
    [Fact]
    public Task The_armed_grant_names_the_exact_figure_typed() => OnThePage((page, host) =>
    {
        // THE DAILY CAP RAISED TO 0.00005: the sentence names 0.00005, not 0.0001, and the second press writes exactly that.
        host.Gateway.Update(s => s.AiDailyCostCap = 0m);
        var cap = Field<AmountField>(page, "_dailyCap");
        var saveCap = PressLabelled(page, Labels.SaveDailyCap);
        cap.Box.Text = "0.00005";
        Press(saveCap);
        Assert.True(Ui.IsArmed(saveCap));
        Assert.Equal(Labels.RaiseDailyCapArmed(Amount.ExactMoney(0.00005m, host.SpendToday.Currency)), saveCap.Content);
        Assert.DoesNotContain("0.0001", (string)saveCap.Content!, StringComparison.Ordinal);
        Press(saveCap);
        Assert.Equal(0.00005m, host.Gateway.Settings.AiDailyCostCap);
        Assert.Contains("up to " + Amount.ExactMoney(0.00005m, host.SpendToday.Currency) + " a day",
            Field<TextBlock>(page, "_capNote").Text, StringComparison.Ordinal);

        // A ONE-PRESS SAVE (lowering) NAMES THE EXACT FIGURE WRITTEN.
        cap.Box.Text = "0,00001";
        Press(saveCap);
        Assert.Equal(0.00001m, host.Gateway.Settings.AiDailyCostCap);
        Assert.Contains("up to " + Amount.ExactMoney(0.00001m, host.SpendToday.Currency) + " a day",
            Field<TextBlock>(page, "_capNote").Text, StringComparison.Ordinal);

        // PERCEPTION'S BUDGET, ON THE SAME PAGE: the raise names 0.00005 as typed with a comma.
        host.Gateway.Update(s => s.PerceptionDailyBudget = 0m);
        var perception = Field<PerceptionCard>(page, "_perception");
        perception.BudgetBox.Box.Text = "0,00005";
        Press(perception.SaveBudget);
        Assert.True(Ui.IsArmed(perception.SaveBudget));
        Assert.Contains("up to 0.00005", (string)perception.SaveBudget.Content!, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    /// <summary>
    /// (fix pass 1) AN EDIT UNDER AN ARMED PRESS DISARMS IT, AND THE ARMED SENTENCE NAMES THE FIGURE IT WILL WRITE — on the AI's price and
    /// the real-money cap, the two presses over his own boxes. RED at 46bfc570 (on the limits' press, shed by <c>U-settings-shed</c>): "2"
    /// typed, the press armed, "2000" typed under it — the press stayed armed and its next press wrote 2000 under a sentence naming no figure.
    /// </summary>
    [Fact]
    public Task An_edit_under_an_armed_press_disarms_it_and_the_sentence_names_the_figure() => OnThePage((page, host) =>
    {
        // THE AI'S PRICE: a lower price armed for one figure is disarmed by the next one typed.
        host.Gateway.Update(s =>
        {
            s.AiPriceInputPerMillion = 10m;
            s.AiPriceOutputPerMillion = 50m;
        });
        var priceIn = Field<AmountField>(page, "_priceIn");
        Field<AmountField>(page, "_priceOut").Box.Text = "50";
        var savePrice = PressLabelled(page, Labels.SaveAiPrice);
        priceIn.Box.Text = "0.1";
        Press(savePrice);
        Assert.True(Ui.IsArmed(savePrice));
        priceIn.Box.Text = "0.0001";
        Assert.False(Ui.IsArmed(savePrice));
        Press(savePrice);
        Assert.Equal(10m, host.Gateway.Settings.AiPriceInputPerMillion);
        Assert.Contains("0.0001", (string)savePrice.Content!, StringComparison.Ordinal);

        // THE REAL-MONEY CAP: a raise armed for 2 is disarmed by 2000 typed under it; armed again, the sentence names 2000.
        host.Gateway.Update(s => s.AiDailyCostCap = 1m);
        var cap = Field<AmountField>(page, "_dailyCap");
        var saveCap = PressLabelled(page, Labels.SaveDailyCap);
        cap.Box.Text = "2";
        Press(saveCap);
        Assert.True(Ui.IsArmed(saveCap));
        cap.Box.Text = "2000";
        Assert.False(Ui.IsArmed(saveCap));
        Press(saveCap);
        Assert.Equal(1m, host.Gateway.Settings.AiDailyCostCap);
        Assert.True(Ui.IsArmed(saveCap));
        Assert.Equal(Labels.RaiseDailyCapArmed(Amount.ExactMoney(2000m, host.SpendToday.Currency)), saveCap.Content);
        Press(saveCap);
        Assert.Equal(2000m, host.Gateway.Settings.AiDailyCostCap);
        return Task.CompletedTask;
    });

    /// <summary>
    /// (fix pass 1) NO TYPED FIGURE CRASHES THE APP: a figure past a box's stated most is refused in the reading line, never thrown out of
    /// the keystroke, and the press over it refuses. RED at 46bfc570 on the envelope's days ("3000000" threw ArgumentOutOfRangeException
    /// out of the box's own text change); <c>U-settings-shed</c> shed that box and the durations, so the guard now walks every box that stays.
    /// </summary>
    [Fact]
    public Task No_typed_figure_throws_and_a_figure_past_a_boxs_stated_most_is_refused() => OnThePage((page, host) =>
    {
        var capWas = host.Gateway.Settings.AiDailyCostCap;
        var cap = Field<AmountField>(page, "_dailyCap");
        var saveCap = PressLabelled(page, Labels.SaveDailyCap);
        foreach (var box in page.Root.GetLogicalDescendants().OfType<AmountField>())
        {
            Assert.Null(Record.Exception(() => box.Box.Text = "99999999999999999999"));
            Assert.Null(box.Value);
            Assert.NotNull(box.Refusal);
            Assert.Equal(box.Refusal, box.Reading.Text);
            Assert.Equal(Theme.Caution, box.Reading.Foreground);
        }
        Assert.Null(Record.Exception(() => Press(saveCap)));
        Assert.Null(Record.Exception(() => Press(saveCap)));
        Assert.Equal(capWas, host.Gateway.Settings.AiDailyCostCap);
        Assert.Equal(Labels.PressRefused(Labels.DailyCostCap, cap.Refusal!), Field<TextBlock>(page, "_capNote").Text);

        // THE MOST IT TAKES STILL READS.
        Assert.Null(Record.Exception(() => cap.Box.Text = "1000000000"));
        Assert.Equal(1_000_000_000m, cap.Value);
        return Task.CompletedTask;
    });

    /// <summary>
    /// (fix pass 1) THE PAPER FEE AND SLIPPAGE SHOW THEIR READING BEFORE THEIR SAVE, as every money and limit box does: "reads as …" or the
    /// refusal under each box, live, and the save refused while either is refused; empty keeps its meaning — no override — and says it in
    /// words. RED at 46bfc570: the Settings page had no reading line under either box (the save read them correctly, but only on the press).
    /// </summary>
    [Fact]
    public async Task The_paper_fee_and_slippage_show_their_reading_before_their_save()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var page = new SettingsPage(host);
            T Of<T>(string name) =>
                (T)(typeof(SettingsPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Xunit.Sdk.XunitException($"the Settings page has no {name}")).GetValue(page)!;
            var fee = Of<TextBox>("_paperFee");
            var slippage = Of<TextBox>("_paperSlippage");
            var feeReading = Of<TextBlock>("_paperFeeReading");
            var slippageReading = Of<TextBlock>("_paperSlippageReading");

            // EMPTY IS NO OVERRIDE, SAID IN WORDS.
            fee.Text = "";
            slippage.Text = "";
            Assert.Equal(Labels.PaperFeeEmpty, feeReading.Text);
            Assert.Equal(Labels.PaperSlippageEmpty, slippageReading.Text);
            Assert.Equal(Theme.TextMuted, feeReading.Foreground);

            // THE OWNER'S COMMA READS, BEFORE ANY PRESS.
            fee.Text = "0,5";
            Assert.StartsWith(Labels.AmountReads("0.5"), feeReading.Text, StringComparison.Ordinal);
            Assert.Equal(Theme.TextMuted, feeReading.Foreground);

            // "1.000" — a thousand or one: refused under the box, and the save writes nothing.
            slippage.Text = "1.000";
            Assert.Contains(Labels.AmountAmbiguous("1.000", "1000", "1"), slippageReading.Text, StringComparison.Ordinal);
            Assert.Equal(Theme.Caution, slippageReading.Foreground);
            Press(Of<Button>("_paperFrictionSave"));
            Assert.Null(host.Gateway.Settings.PaperFeeOverride);
            Assert.Null(host.Gateway.Settings.PaperSlippageOverride);
            Assert.Equal(slippageReading.Text, Of<TextBlock>("_paperFrictionNote").Text);

            // AND WITH THE SLIPPAGE LEFT EMPTY, THE FEE ALONE IS SAVED — exactly what its line said.
            slippage.Text = "";
            Press(Of<Button>("_paperFrictionSave"));
            Assert.Equal(0.005m, host.Gateway.Settings.PaperFeeOverride);
            Assert.Null(host.Gateway.Settings.PaperSlippageOverride);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>
    /// (item 6) NO REFRESH WRITES A BOX: the owner types into the cap box, the five-second tick's page pass runs — the Safety page's
    /// <c>Update</c>, its own <c>Refresh</c>, and the perception card's — and the half-typed text, and what it reads as, are exactly
    /// as typed. And the guard behind it: nothing in the app assigns a field's text or figure after construction — the only writer
    /// of an <see cref="AmountField"/>'s text is the owner's keyboard, and its figure has no setter outside the field.
    /// </summary>
    [Fact]
    public Task A_refresh_never_rewrites_a_box_being_edited() => OnThePage(async (page, host) =>
    {
        var cap = Field<AmountField>(page, "_dailyCap");
        var budget = Field<PerceptionCard>(page, "_perception").BudgetBox;
        cap.Box.Text = "0,0";
        budget.Box.Text = "1.000";
        var fields = page.Root.GetLogicalDescendants().OfType<AmountField>().ToList();
        var before = fields.Select(f => (f.Box.Text, f.Value, f.Reading.Text)).ToList();

        host.Gateway.Update(s => s.AiDailyCostCap = 42m);           // the setting moves under the box the owner is typing in
        page.Update(await host.Gateway.StatusAsync());
        typeof(OwnerPage).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Field<PerceptionCard>(page, "_perception").Update();

        Assert.Equal(before, fields.Select(f => (f.Box.Text, f.Value, f.Reading.Text)).ToList());
        Assert.Equal("0,0", cap.Box.Text);
        Assert.Equal(0m, cap.Value);
        Assert.Equal("1.000", budget.Box.Text);
        Assert.Null(budget.Value);

        // THE GUARD: no app code outside the field writes a field's text, and the figure's setter is the field's own.
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "src", "TradeAgent.App"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\.Box\.Text\s*=[^=]", text);
            Assert.DoesNotMatch(@"\.Box\.SetValue\(|\.Box\.Clear\(|\.Box\.Paste\(", text);
        }
        Assert.False(typeof(AmountField).GetProperty(nameof(AmountField.Value))!.SetMethod!.IsPublic);
        Assert.False(typeof(AmountField).GetProperty(nameof(AmountField.Value))!.SetMethod!.IsAssembly);
    });

    /// <summary>A wheel or trackpad scroll over <paramref name="over"/>, as the platform raises it.</summary>
    static PointerWheelEventArgs Wheel(Interactive over, Visual root) =>
        new(over, new Avalonia.Input.Pointer(1, PointerType.Mouse, true), root, new Point(1, 1), 0,
            new PointerPointProperties(), KeyModifiers.None, new Vector(0, 1)) { RoutedEvent = InputElement.PointerWheelChangedEvent };

    /// <summary>The box clicked into, as the focus manager marks it — there is no window here to click in.</summary>
    static void Focused(InputElement element)
    {
        var p = typeof(InputElement);
        p.GetProperty(nameof(InputElement.IsKeyboardFocusWithin))!.SetMethod!.Invoke(element, [true]);
        p.GetProperty(nameof(InputElement.IsFocused))!.SetMethod!.Invoke(element, [true]);
    }

    /// <summary>
    /// (item 7) NO SCROLL CHANGES A NUMBER — the owner, 2026-10-22: "it's also important that values cannot be changed by scrolling over
    /// them if they weren't clicked in the first place". A wheel over a money box, not clicked into and then clicked into, changes neither
    /// its figure nor its text, and the event is left unhandled so the page scrolls. Decided: a FOCUSED box takes no wheel change either —
    /// typing is the one way a figure changes. (<c>U-settings-shed</c>: the walk is the owner's four boxes.)
    ///
    /// <para>RED at 988c9487 through the stock number box with its spinner: unfocused, the wheel was already left alone (a guard,
    /// green at base); clicked into, one wheel notch stepped 7 to 8 and swallowed the scroll — Expected (7, False), Actual (8, True).</para>
    /// </summary>
    [Fact]
    public Task A_scroll_over_a_money_box_never_changes_its_value_and_scrolls_the_page() => OnThePage((page, _) =>
    {
        var fields = page.Root.GetLogicalDescendants().OfType<AmountField>().ToList();
        Assert.Equal(4, fields.Count);
        Field<AmountField>(page, "_dailyCap").Box.Text = "0,05";

        foreach (var field in fields)
        {
            var before = (field.Box.Text, field.Value);

            var unfocused = Wheel(field.Box, page.Root);
            field.Box.RaiseEvent(unfocused);
            Assert.Equal(before, (field.Box.Text, field.Value));
            Assert.False(unfocused.Handled);

            Focused(field);
            Focused(field.Box);
            var focused = Wheel(field.Box, page.Root);
            field.Box.RaiseEvent(focused);
            Assert.Equal(before, (field.Box.Text, field.Value));
            Assert.False(focused.Handled);
        }
        Assert.Equal(0.05m, Field<AmountField>(page, "_dailyCap").Value);
        return Task.CompletedTask;
    });
}
