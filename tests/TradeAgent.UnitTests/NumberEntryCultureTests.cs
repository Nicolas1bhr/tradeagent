using System.Globalization;
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
}
