using System.Globalization;
using System.Text;

namespace TradeAgent.Core.Features;

/// <summary>
/// THE FORM A FEATURE SPEC IS HASHED IN, AND THEREFORE THE FORM IT IS — by <c>StrategyCanonical</c>'s rules.
///
/// <para><b>One meaning, one text.</b> A spec arrives as JSON an agent wrote, and JSON lets one meaning be spelled
/// many ways: keys in any order, any spacing, a letter written as an escape. None of that is part of a feature, so
/// none of it is in here: one fact per line, in a fixed order, every duration as whole seconds with an <c>s</c>,
/// every number in the invariant culture, every name written out from this file's own words and never from an
/// enum's <c>ToString()</c>. Two spellings of one spec are one text and therefore one id.</para>
///
/// <para><b>Every fact the spec states is here, and nothing it does not.</b> A line is written only for the kind
/// that has it — <c>mode</c> and <c>lookback</c> for a change, <c>window</c> and <c>minrows</c> for a window — so a
/// kind without a line cannot share a text with one that has it. There are no defaults to state: the parse requires
/// every key a kind has, so a spec that parsed has said every fact written here.</para>
/// </summary>
public static class FeatureCanonical
{
    /// <summary>The canonical text's own version, in its first line. It moves when this layout moves.</summary>
    public const string Header = "feature/1";

    /// <summary>The typed, ordered form. No JSON, no source whitespace, no key order.</summary>
    public static string Of(FeatureSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var text = new StringBuilder();

        text.Append(Header).Append('\n');
        text.Append("kind ").Append(spec.Kind).Append('\n');
        if (spec.Mode is { } mode) text.Append("mode ").Append(mode).Append('\n');

        // ONE LINE PER INPUT, its four names as key=value: none of them can hold a space, an '=' or a line break
        // (the parse refuses any that could), so the line reads one way.
        foreach (var input in spec.Inputs)
            text.Append("input source=").Append(input.Source)
                .Append(" series=").Append(input.Series)
                .Append(" subject=").Append(input.Subject)
                .Append(" field=").Append(input.Field)
                .Append('\n');

        text.Append("latency ").Append(Seconds(spec.Latency)).Append('\n');
        text.Append("maxage ").Append(Seconds(spec.MaxAge)).Append('\n');
        if (spec.Lookback is { } lookback) text.Append("lookback ").Append(Seconds(lookback)).Append('\n');
        if (spec.Window is { } window) text.Append("window ").Append(Seconds(window)).Append('\n');
        if (spec.MinRows is { } rows) text.Append("minrows ").Append(rows.ToString(CultureInfo.InvariantCulture)).Append('\n');

        return text.ToString();
    }

    /// <summary>A duration as whole seconds. Spelled here so a unit's name is never in a hash.</summary>
    static string Seconds(TimeSpan span) =>
        ((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
}
