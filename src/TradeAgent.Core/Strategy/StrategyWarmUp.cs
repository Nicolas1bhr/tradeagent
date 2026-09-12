namespace TradeAgent.Core.Strategy;

/// <summary>
/// HOW MANY CLOSED BARS A PROGRAM NEEDS BEFORE IT MAY BE ASKED ANYTHING — computed in ONE place.
///
/// <para>`docs/COUNCIL.md` requires "bounded lookbacks, explicit warm-up". The failure it names is
/// quiet: a 50-bar average evaluated on bar 30 is the mean of thirty bars wearing the name of the
/// other thing, and every signal it produces is one the strategy never expressed. Nothing in the
/// output looks wrong — the backtest simply contains trades the promoted program would not have
/// taken, so its evidence is evidence about a different program.</para>
///
/// <para>One place, because one bar short is the whole defect and a second copy of this arithmetic is
/// how a build ends up disagreeing with itself. The number is stated on the frozen program, hashed
/// into its id, and `U-runner-2` refuses to evaluate before it.</para>
///
/// <para><b>The rule.</b> The maximum over: every DECLARED indicator's own warm-up (declared, not
/// used — a program that became warm sooner because a rule stopped mentioning an indicator would
/// change its warm-up on an edit that changed nothing else), every history reference in every rule
/// condition, the extra bar a crossing reads, and the stop's own ATR period. At least 1: nothing can
/// be asked before one bar has closed.</para>
/// </summary>
public static class StrategyWarmUp
{
    /// <summary>The program's warm-up, in closed bars.</summary>
    public static int Of(StrategyProgram p)
    {
        var indicators = p.Indicators.ToDictionary(i => i.Name, StringComparer.Ordinal);

        var bars = 1;
        foreach (var indicator in p.Indicators) bars = Math.Max(bars, Bars(indicator));
        foreach (var rule in p.Rules) bars = Math.Max(bars, Bars(rule.Condition, indicators));

        // The stop's ATR is measured at the entry bar and then held: an entry taken before that ATR
        // is warm would place a stop off a number the program did not ask for — and with
        // `risk_fraction` that number is what the quantity is divided by.
        if (p.Stop.Kind == StopKind.EntryAtr)
            bars = Math.Max(bars, p.Stop.AtrPeriod + 1);

        return bars;
    }

    /// <summary>
    /// One indicator's warm-up, from the table in `docs/STRATEGY-LANGUAGE.md`. `rsi` and `atr` are
    /// over CHANGES between bars, so fourteen changes take fifteen bars; the opening-range
    /// accumulators are session-scoped rather than lookbacks and are defined on their first closed bar.
    /// </summary>
    public static int Bars(IndicatorDecl indicator) => indicator.Kind switch
    {
        IndicatorKind.Rsi or IndicatorKind.Atr => indicator.Period + 1,
        IndicatorKind.OpeningRangeHigh or IndicatorKind.OpeningRangeLow => 1,
        _ => indicator.Period
    };

    /// <summary>
    /// The bars one condition needs, nesting included. A literal needs none; a series reference needs
    /// the bar it names; an indicator reference needs its indicator warm PLUS the bars it reaches
    /// back; a crossing needs one bar more than the deeper of its two sides, because it reads both on
    /// this bar and on the one before.
    /// </summary>
    static int Bars(Expr e, Dictionary<string, IndicatorDecl> indicators) => e switch
    {
        NumberLiteral or BoolLiteral => 0,
        SeriesRef s => 1 + s.Back,
        IndicatorRef i => (indicators.TryGetValue(i.Name, out var d) ? Bars(d) : 1) + i.Back,
        UnaryExpr u => Bars(u.Operand, indicators),
        BinaryExpr b => Math.Max(Bars(b.Left, indicators), Bars(b.Right, indicators)),
        CrossExpr c => Math.Max(Bars(c.Left, indicators), Bars(c.Right, indicators)) + 1,
        _ => 1
    };
}
