using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
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

    // ---- the Safety page, built the way the app builds it --------------------------------------------------------

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The Safety page over a composed host and the fake platform, handed to <paramref name="body"/> and disposed after.</summary>
    static async Task OnThePage(Func<SafetyPage, AppHost, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(new SafetyPage(host), host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static T Field<T>(SafetyPage page, string name) =>
        (T)typeof(SafetyPage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    /// <summary>
    /// (item 3) EVERY NUMBER BOX ON THE SAFETY PAGE IS THE ONE FIELD: the built tree holds no stock number control at all, and the
    /// twenty-six money and limit boxes — nine limits, the two ceilings, the allowance, the review interval, three lanes, two prices,
    /// the research share, two allocation and three envelope figures, and perception's budget — are each an <see cref="AmountField"/>.
    /// </summary>
    [Fact]
    public Task Every_number_box_on_the_safety_page_is_an_amount_field() => OnThePage((page, _) =>
    {
        var all = page.Root.GetLogicalDescendants().ToList();
        Assert.Empty(all.OfType<NumericUpDown>());
        Assert.Equal(26, all.OfType<AmountField>().Count());
        return Task.CompletedTask;
    });

    /// <summary>
    /// (item 3) THE OWNER'S COMMA READS AS FIVE HUNDREDTHS IN THE ENVELOPE BOX — "The most any one of them may hold at once", the box of
    /// his defect. RED at 988c9487: "0,05" typed into the stock number box and committed with Enter read as 5.
    /// </summary>
    [Fact]
    public Task The_owners_comma_reads_as_five_hundredths_in_the_envelope_box() => OnThePage((page, _) =>
    {
        Field<TextBox>(page, "_envelopeSymbol").Text = "ES";
        var quantity = Field<AmountField>(page, "_envelopeQuantity");
        quantity.Box.Text = "0,05";

        Assert.Equal("0,05", quantity.Box.Text);
        Assert.Equal(0.05m, quantity.Value);
        Assert.Equal(Labels.AmountReads("0.05"), quantity.Reading.Text);

        var grant = Field<Button>(page, "_grantEnvelope");
        Press(grant);
        Assert.True(Ui.IsArmed(grant));
        Assert.Contains("in ES, up to 0.05 at a time", (string)grant.Content!, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    /// <summary>The page's button that says <paramref name="label"/> at rest, found in the built tree the owner sees.</summary>
    static Button PressLabelled(SafetyPage page, string label) =>
        page.Root.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, label));

    /// <summary>
    /// (item 4) A BOX THAT READS AS NOTHING REFUSES THE PRESS — EITHER PRESS — AND NEVER SAVES A FALLBACK. RED at 988c9487: the envelope's
    /// "days" box left empty read as zero days, so the grant named an envelope ending the moment it was granted; an empty cap or
    /// limit box read as the figure already in force.
    /// </summary>
    [Fact]
    public Task An_unreadable_box_refuses_the_press_never_saves_a_fallback() => OnThePage((page, host) =>
    {
        // THE ENVELOPE, DAYS EMPTY: no arming, no grant, on either press — the note names the box and the form.
        Field<TextBox>(page, "_envelopeSymbol").Text = "ES";
        Field<AmountField>(page, "_envelopeQuantity").Box.Text = "1";
        Field<AmountField>(page, "_envelopeDays").Box.Text = "";
        var grant = Field<Button>(page, "_grantEnvelope");
        var envelopeNote = Field<TextBlock>(page, "_envelopeNote");
        foreach (var _ in new[] { 1, 2 })
        {
            Press(grant);
            Assert.False(Ui.IsArmed(grant));
            Assert.Equal(Labels.PressRefused(Labels.EnvelopeUntil, Labels.AmountEmpty), envelopeNote.Text);
            Assert.True(envelopeNote.IsVisible);
            Assert.Equal(Theme.Caution, envelopeNote.Foreground);
        }
        Assert.Null(host.Gateway.Envelopes.Standing(host.Gateway.Connector.Id, host.Gateway.ClosureAccountId, DateTimeOffset.UtcNow));

        // ARMED FOR A READ FIGURE, THEN EDITED INTO A REFUSAL: the edit disarms, and the next press refuses rather than granting.
        Field<AmountField>(page, "_envelopeDays").Box.Text = "30";
        Press(grant);
        Assert.True(Ui.IsArmed(grant));
        Field<AmountField>(page, "_envelopeDays").Box.Text = "30,5";
        Assert.False(Ui.IsArmed(grant));
        Press(grant);
        Assert.False(Ui.IsArmed(grant));
        Assert.Equal(Labels.PressRefused(Labels.EnvelopeUntil, Labels.AmountNotWhole("30,5")), envelopeNote.Text);

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

        // A LIMIT THAT COUNTS WHOLE THINGS, given the owner's comma: refused as "a whole number", the limits in force unchanged.
        var positionsWas = host.Gateway.Settings.Risk.MaxOpenPositions;
        Field<AmountField>(page, "_maxPositions").Box.Text = "0,05";
        var saveLimits = PressLabelled(page, Labels.SaveLimits);
        Press(saveLimits);
        Press(saveLimits);
        Assert.Equal(positionsWas, host.Gateway.Settings.Risk.MaxOpenPositions);
        Assert.Contains("a whole number", Field<TextBlock>(page, "_limitsNote").Text, StringComparison.Ordinal);
        Assert.StartsWith(Labels.PressRefused(Labels.MaxOpenPositions, ""), Field<TextBlock>(page, "_limitsNote").Text, StringComparison.Ordinal);

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
    /// under a sentence naming another.
    /// </summary>
    [Fact]
    public Task The_armed_grant_names_the_exact_figure_typed() => OnThePage((page, host) =>
    {
        // THE OWNER'S COMMA IN THE ENVELOPE, AND A VALUE CEILING PAST FOUR PLACES.
        Field<TextBox>(page, "_envelopeSymbol").Text = "ES";
        Field<AmountField>(page, "_envelopeQuantity").Box.Text = "0,05";
        Field<AmountField>(page, "_envelopeNotional").Box.Text = "0,00005";
        var grant = Field<Button>(page, "_grantEnvelope");
        Press(grant);
        Assert.True(Ui.IsArmed(grant));
        Assert.Contains("up to 0.05 at a time, worth at most 0.00005", (string)grant.Content!, StringComparison.Ordinal);

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
}
