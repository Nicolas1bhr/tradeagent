using System.Globalization;

namespace TradeAgent.Core;

/// <summary>What a box takes: a figure with an optional decimal part, or a whole count.</summary>
public enum AmountKind { Decimal, Whole }

/// <summary>
/// THE ONE WAY A NUMBER THE OWNER TYPES IS READ, AND THE ONE WAY A READ FIGURE IS PRINTED BACK
/// (<c>U-number-entry-culture</c>).
///
/// <para>The owner's live defect of 2026-10-22: on a Mac whose language is US English and whose region
/// is Belgium, the Safety page read his "0,05" as <b>5</b> — a position cap a hundred times larger — and
/// drew his "0.05" back as 0. Every box was a stock number control reading in the ambient culture with
/// every style allowed, and the ambient culture is the invariant one on every machine this ships to
/// (<c>InvariantGlobalization</c>), where a comma is a thousands separator. Following the owner's own
/// culture instead would not have helped: he typed BOTH spellings within minutes, and every single
/// culture misreads one of them.</para>
///
/// <para>So the reading needs no culture at all, by construction: digits, and at most ONE decimal mark
/// — a point or a comma, either one, meaning the same thing — and digits. No thousands separators, no
/// sign, no exponent, no currency, no percent and no inner spaces; outer spaces are trimmed. A single
/// mark followed by exactly three digits after one to three leading digits that do not start with 0
/// ("1.000", "1,000", "12,500") is how a thousands separator looks as well as a decimal one, and it is
/// REFUSED as ambiguous rather than read either way: refuse, never guess. A whole-number box refuses any
/// mark at all, and an empty box is refused — nothing typed is never 0 and never the saved figure.</para>
///
/// <para>The normalised digits are handed to <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/>
/// with <see cref="NumberStyles.AllowDecimalPoint"/> under the invariant culture, so no ambient
/// culture is reached on any path.</para>
/// </summary>
public static class Amount
{
    /// <summary>
    /// Reads <paramref name="text"/> by the one rule above. True with the figure, or false with the
    /// sentence the owner reads — which names what they typed and the form that is taken.
    /// </summary>
    public static bool TryRead(string? text, AmountKind kind, out decimal value, out string? why)
    {
        value = 0m;
        why = null;
        var typed = (text ?? "").Trim();
        var shown = typed;
        if (typed.Length == 0)
        {
            why = Labels.AmountEmpty;
            return false;
        }

        var mark = -1;
        for (var i = 0; i < typed.Length; i++)
        {
            var c = typed[i];
            if (char.IsAsciiDigit(c)) continue;
            if ((c == '.' || c == ',') && mark < 0)
            {
                mark = i;
                continue;
            }
            why = Labels.AmountUnreadable(typed);
            return false;
        }

        var whole = mark < 0 ? typed : typed[..mark];
        var fraction = mark < 0 ? "" : typed[(mark + 1)..];
        if (mark >= 0 && (whole.Length == 0 || fraction.Length == 0))
        {
            why = Labels.AmountUnreadable(typed);
            return false;
        }

        if (mark >= 0 && kind == AmountKind.Whole)
        {
            why = Labels.AmountNotWhole(typed);
            return false;
        }

        // THE GROUPING-SHAPED MARK: "1.000" is one thousand where the point groups thousands and one
        // where it marks decimals, and the owner's own two spellings of 2026-10-22 show both are typed.
        if (mark >= 0 && fraction.Length == 3 && whole.Length is >= 1 and <= 3 && whole[0] != '0')
        {
            why = Labels.AmountAmbiguous(typed, whole + fraction, Exact(Parsed(whole, fraction)));
            return false;
        }

        // THE COMMA IS A DECIMAL MARK, exactly as the point is: one spelling for the reader below.
        if (mark >= 0 && typed[mark] == ',')
            typed = whole + "." + fraction;

        if (!decimal.TryParse(typed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            || (kind == AmountKind.Whole && value > int.MaxValue))
        {
            value = 0m;
            why = Labels.AmountTooLarge(shown);
            return false;
        }
        return true;
    }

    /// <summary>
    /// THE MOST A DURATION BOX TAKES, in its own unit (<c>U-number-entry-culture</c>, fix pass 1): a hundred years. A figure past it is
    /// refused in the box's reading line, because a date or a span that far out does not exist on any clock — "3000000" days was
    /// thrown out of the keystroke that typed it. A hundred years limits nothing an owner means; it only keeps every figure a date.
    /// </summary>
    public const decimal MostDays = 36_500m;

    /// <inheritdoc cref="MostDays"/>
    public const decimal MostHours = MostDays * 24m;

    /// <inheritdoc cref="MostDays"/>
    public const decimal MostMinutes = MostHours * 60m;

    /// <summary>
    /// A read figure as it is printed back to the owner: every digit it has, trailing zeros dropped,
    /// never rounded and never in a culture's spelling — 0.00005 is "0.00005", not "0.0001".
    /// </summary>
    public static string Exact(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>A sum of money as an armed sentence or a saved note names it: exact, with its currency where one is known.</summary>
    public static string ExactMoney(decimal value, string currency) =>
        currency.Length == 0 ? Exact(value) : $"{Exact(value)} {currency}";

    static decimal Parsed(string whole, string fraction) =>
        decimal.Parse(whole + "." + fraction, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
