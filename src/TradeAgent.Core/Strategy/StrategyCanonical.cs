using System.Text;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE FORM A STRATEGY IS HASHED IN, AND THEREFORE THE FORM IT IS.
///
/// <para>`docs/COUNCIL.md`: "SHA-256 over the canonical typed program, parameters and semantic-version
/// manifest, source retained". Two things follow, and they are the whole reason this file is separate
/// from the parser.</para>
///
/// <para><b>One meaning, one text.</b> Comments, spacing, letter case, declaration order and trailing
/// zeros are not part of a strategy; a hash over the source would make each of them part of its
/// IDENTITY, and then one strategy, saved twice by a model that reformatted its own output, would be
/// two strategies — two lineages, two trial budgets against the referee, two sets of results that
/// cannot be pooled. So the form here is typed and ordered: constants resolved to their values,
/// indicators sorted by name, conditions in prefix form where precedence cannot be misread, numbers
/// with their trailing zeros gone.</para>
///
/// <para><b>Every name here is written out, never taken from an enum.</b> `IndicatorKind.Sma`
/// serialises as `sma` because this file says so, not because that is what `ToString()` returns: a
/// rename in `StrategyAst.cs` would otherwise re-identify every program in every installation, and
/// the diff that did it would look like a tidy-up.</para>
///
/// <para>Order of the lines is fixed, and the RULES keep their declared order because that order is
/// their meaning. Everything else is sorted.</para>
/// </summary>
public static class StrategyCanonical
{
    /// <summary>The canonical text's own version, in its first line. It moves when this layout moves.</summary>
    public const string Header = "program/1";

    /// <summary>The typed, ordered form. No comments, no source whitespace, no letter-case choices.</summary>
    public static string Of(StrategyProgram p)
    {
        var text = new StringBuilder();

        text.Append(Header).Append('\n');
        text.Append("instrument ").Append(p.Instrument).Append('\n');
        text.Append("zone ").Append(p.Time.TimeZone).Append('\n');
        text.Append("days ").Append(Days(p.Time.Days)).Append('\n');

        foreach (var window in p.Time.EntryWindows.OrderBy(w => w.From.MinuteOfDay).ThenBy(w => w.To.MinuteOfDay))
            text.Append("window ").Append(window).Append('\n');

        if (p.Time.OpeningRange is { } range) text.Append("openrange ").Append(range).Append('\n');
        if (p.Time.SessionExit is { } exit) text.Append("sessionexit ").Append(exit).Append('\n');

        foreach (var indicator in p.Indicators.OrderBy(i => i.Name, StringComparer.Ordinal))
            text.Append("ind ").Append(indicator.Name).Append('=').Append(Call(indicator)).Append('\n');

        text.Append("size ").Append(Size(p.Sizing)).Append('\n');
        text.Append("stop ").Append(Stop(p.Stop)).Append('\n');
        text.Append("target ").Append(Target(p.Target)).Append('\n');
        text.Append("hold ").Append(p.MaxHoldBars is { } bars ? bars.ToString() : "none").Append('\n');

        foreach (var rule in p.Rules)
            text.Append(rule.Kind == RuleKind.Exit ? "exit " : "entry ").Append(Condition(rule.Condition)).Append('\n');

        return text.ToString();
    }

    /// <summary>
    /// THE PARAMETERS, SEPARATELY. A parameter sweep is a family of programs with one structure and
    /// different numbers, and `docs/COUNCIL.md` hashes the two halves separately for exactly that
    /// reason: the canonical form above says what the strategy DOES, and this says what it was tuned
    /// to. Sorted by name, so the order two constants were written in is not part of the identity.
    /// </summary>
    public static string Parameters(StrategyProgram p) =>
        string.Join("\n", p.Constants
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Type == ValueKind.Number
                ? $"{c.Name}=number:{StrategyParser.Number(c.Number)}"
                : $"{c.Name}=boolean:{(c.Boolean ? "true" : "false")}"));

    static string Days(Weekdays days)
    {
        if (days == Weekdays.All) return "all";

        var names = new List<string>();
        if (days.HasFlag(Weekdays.Mon)) names.Add("mon");
        if (days.HasFlag(Weekdays.Tue)) names.Add("tue");
        if (days.HasFlag(Weekdays.Wed)) names.Add("wed");
        if (days.HasFlag(Weekdays.Thu)) names.Add("thu");
        if (days.HasFlag(Weekdays.Fri)) names.Add("fri");
        if (days.HasFlag(Weekdays.Sat)) names.Add("sat");
        if (days.HasFlag(Weekdays.Sun)) names.Add("sun");
        return string.Join(",", names);
    }

    static string Call(IndicatorDecl i) => i.Kind switch
    {
        IndicatorKind.Sma => $"sma({Series(i.Source)},{i.Period})",
        IndicatorKind.Ema => $"ema({Series(i.Source)},{i.Period})",
        IndicatorKind.Rsi => $"rsi({Series(i.Source)},{i.Period})",
        IndicatorKind.Highest => $"highest({Series(i.Source)},{i.Period})",
        IndicatorKind.Lowest => $"lowest({Series(i.Source)},{i.Period})",
        IndicatorKind.Atr => $"atr({i.Period})",
        IndicatorKind.OpeningRangeHigh => "opening_range_high()",
        _ => "opening_range_low()"
    };

    static string Series(BarSeries s) => s switch
    {
        BarSeries.Open => "open",
        BarSeries.High => "high",
        BarSeries.Low => "low",
        BarSeries.Close => "close",
        _ => "volume"
    };

    static string Size(Sizing s) => s.Kind switch
    {
        SizingKind.FixedQuantity => $"fixed:{StrategyParser.Number(s.Value)}",
        SizingKind.CapitalFraction => $"capital_fraction:{StrategyParser.Number(s.Value)}",
        _ => $"risk_fraction:{StrategyParser.Number(s.Value)}"
    };

    static string Stop(StopRule s) => s.Kind switch
    {
        StopKind.FixedPrice => $"fixed:{StrategyParser.Number(s.Value)}",
        StopKind.Percent => $"percent:{StrategyParser.Number(s.Value)}",
        StopKind.EntryAtr => $"atr:{StrategyParser.Number(s.Value)}:{s.AtrPeriod}",
        _ => "none"
    };

    static string Target(TargetRule t) => t.Kind switch
    {
        TargetKind.FixedPrice => $"fixed:{StrategyParser.Number(t.Value)}",
        TargetKind.Percent => $"percent:{StrategyParser.Number(t.Value)}",
        _ => "none"
    };

    /// <summary>
    /// A condition in prefix form: `(and (&gt; close @fastma) (&lt; volume 100))`. Prefix because
    /// precedence is then not part of reading it — two texts that mean the same comparison are the
    /// same text here, and no reader has to know whether `and` binds tighter than `&gt;`.
    /// </summary>
    static string Condition(Expr e) => e switch
    {
        NumberLiteral n => StrategyParser.Number(n.Value),
        BoolLiteral b => b.Value ? "true" : "false",
        SeriesRef s => s.Back == 0 ? Series(s.Series) : $"{Series(s.Series)}[{s.Back}]",
        IndicatorRef i => i.Back == 0 ? $"@{i.Name}" : $"@{i.Name}[{i.Back}]",
        UnaryExpr u => $"({(u.Op == UnaryOp.Not ? "not" : "neg")} {Condition(u.Operand)})",
        CrossExpr c => $"({(c.Direction == CrossDirection.Above ? "crosses_above" : "crosses_below")} " +
                       $"{Condition(c.Left)} {Condition(c.Right)})",
        BinaryExpr b => $"({Operator(b.Op)} {Condition(b.Left)} {Condition(b.Right)})",
        _ => "?"
    };

    static string Operator(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Less => "<",
        BinaryOp.LessOrEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterOrEqual => ">=",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.And => "and",
        _ => "or"
    };
}
