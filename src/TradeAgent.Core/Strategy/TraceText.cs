using System.Globalization;
using System.Runtime.CompilerServices;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// WORDS THAT END UP IN A TRACE, WITH EVERY NUMBER IN THEM SPELLED THE WAY THE TRACE SPELLS IT — on every
/// machine, whatever culture it runs in (<c>U-invariant-traces</c>).
///
/// <para><b>Why a type and not a habit.</b> A fault's words are a trace's last line: they are hashed into
/// <c>trace_sha256</c>, stored as <c>fault_reason</c> and repeated in the metrics' account of why a run
/// stopped, and a no-trade's words are a line of the same trace. An interpolated string formats its holes in
/// the THREAD's culture, so <c>$"divided {x}"</c> wrote <c>1026,70</c> on a machine set to Dutch and
/// <c>1026.70</c> on CI — two traces, two hashes, two different records of one request. So every place
/// that makes a fault's or a no-trade's words takes this type and nothing else —
/// <see cref="EvaluationOutcome.Faulted"/>, the evaluation state's own fault, the interpreter's fault, and
/// <see cref="BacktestEvent"/>'s fault and no-trade lines, which carry every halt a backtest makes itself. A
/// new one written as <c>$"…"</c> is spelled here without anybody remembering to, and one built any other
/// way — <c>"… " + x</c>, <c>string.Format</c>, a string from somewhere else — does not compile, because
/// there is no conversion from <see cref="string"/> to this type.</para>
///
/// <para><b>How it spells.</b> A decimal through <see cref="StrategyParser.Number"/>, exactly as every number
/// on a trace line is spelled: invariant, trailing zeros gone, so <c>1026.70</c> is <c>1026.7</c> in the
/// words as it is in the line beside them. Everything else in the invariant culture — an integer, a date in
/// the format its hole names, a <see cref="TimeSpan"/> — and a format a hole names is honoured, invariantly.
/// A string is copied as it is, which is how words already spelled here — a grid's refusal, a fault being
/// handed on — are carried without being spelled twice.</para>
/// </summary>
[InterpolatedStringHandler]
public ref struct TraceText
{
    DefaultInterpolatedStringHandler _words;

    public TraceText(int literalLength, int formattedCount) =>
        _words = new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture);

    public void AppendLiteral(string value) => _words.AppendLiteral(value);

    /// <summary>A decimal as a trace line writes it: <see cref="StrategyParser.Number"/>.</summary>
    public void AppendFormatted(decimal value) => _words.AppendLiteral(StrategyParser.Number(value));

    /// <summary>The same, and nothing at all for a value that is not there.</summary>
    public void AppendFormatted(decimal? value)
    {
        if (value is { } d) AppendFormatted(d);
    }

    /// <summary>Words copied as they are.</summary>
    public void AppendFormatted(string? value) => _words.AppendFormatted(value);

    /// <summary>Anything else, in the invariant culture.</summary>
    public void AppendFormatted<T>(T value) => _words.AppendFormatted(value);

    /// <summary>Anything else in the format the hole names, in the invariant culture.</summary>
    public void AppendFormatted<T>(T value, string? format) => _words.AppendFormatted(value, format);

    /// <summary>The words, spelled. The buffer goes back to its pool, so this is the last thing done with them.</summary>
    public string ToStringAndClear() => _words.ToStringAndClear();

    /// <summary>
    /// Words spelled here, as a string — for a method that answers words or nothing
    /// (<see cref="BarGrid.Refusal"/>, <see cref="BarResampler.Refusal"/>), whose caller hands them to a fault.
    /// </summary>
    public static string Spell(TraceText words) => words.ToStringAndClear();
}
