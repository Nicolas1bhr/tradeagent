namespace TradeAgent.Core.Strategy;

/// <summary>The five series of one closed bar. A series names no instrument; the program names one.</summary>
public enum BarSeries { Open, High, Low, Close, Volume }

/// <summary>
/// EVERY INDICATOR THE LANGUAGE HAS. A closed set, in one place, so that "is this a real indicator"
/// is a lookup rather than a decision taken at a call site. An unknown name is a refusal, never a
/// node: a misspelt `smaa(20)` that parsed into something generic would be a program whose author
/// believes it has a 20-bar average and whose behaviour is whatever the fall-through did.
/// </summary>
public enum IndicatorKind { Sma, Ema, Rsi, Atr, Highest, Lowest, OpeningRangeHigh, OpeningRangeLow }

/// <summary>The only two types an expression can have. There is no string, no list and no null.</summary>
public enum ValueKind { Number, Boolean }

public enum UnaryOp { Negate, Not }

public enum BinaryOp
{
    Add, Subtract, Multiply, Divide,
    Less, LessOrEqual, Greater, GreaterOrEqual, Equal, NotEqual,
    And, Or
}

public enum CrossDirection { Above, Below }

/// <summary>
/// Exit or entry, and the order is the enum's order for a reason: `docs/COUNCIL.md` says exits are
/// evaluated before entries, and a program that declares an entry above an exit is refused rather
/// than silently reordered.
/// </summary>
public enum RuleKind { Exit, Entry }

/// <summary>
/// The three ways a program may state size. There is no fourth, and none of them takes a side: a
/// program sizes a LONG entry, because long/flat is the whole of what it can do.
/// </summary>
public enum SizingKind { FixedQuantity, CapitalFraction, EquityRiskFraction }

public enum StopKind { None, FixedPrice, Percent, EntryAtr }

public enum TargetKind { None, FixedPrice, Percent }

/// <summary>
/// ONE EXPRESSION NODE.
///
/// <para>The constructor is `private protected`, so the set of node kinds is closed to this assembly:
/// nothing outside `TradeAgent.Core` can add a case, and there is therefore no node for a statement,
/// a loop, a function call of its own, a clock read, a random draw, a file, a socket, a model request
/// or a second instrument. COUNCIL's "Never:" line is enforced by the SHAPE of this type rather than
/// by a list of names the parser rejects — a rejected name is a blocklist somebody has to keep up to
/// date, and an absent node kind is not.</para>
///
/// <para>A node carries no source position. Positions belong to refusals, which are produced while
/// the line is still in hand; a frozen program is a meaning, and two programs that mean the same
/// thing must not differ because one was written on line 9.</para>
/// </summary>
public abstract record Expr
{
    private protected Expr() { }

    /// <summary>This node and everything under it, which is what <see cref="StrategyLimits.MaxNodes"/> counts.</summary>
    public abstract int NodeCount { get; }
}

/// <summary>A literal number. Constant references are resolved into these at parse time.</summary>
public sealed record NumberLiteral(decimal Value) : Expr
{
    public override int NodeCount => 1;
}

public sealed record BoolLiteral(bool Value) : Expr
{
    public override int NodeCount => 1;
}

/// <summary>
/// One series of one bar, `Back` bars ago: `close` is `Back` 0, `close[1]` is the bar before it.
/// Only closed bars exist, so `Back` 0 is the bar that has just closed and there is no way to spell
/// the bar that is forming.
/// </summary>
public sealed record SeriesRef(BarSeries Series, int Back) : Expr
{
    public override int NodeCount => 1;
}

/// <summary>A declared indicator's value, `Back` bars ago.</summary>
public sealed record IndicatorRef(string Name, int Back) : Expr
{
    public override int NodeCount => 1;
}

public sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr
{
    public override int NodeCount => 1 + Operand.NodeCount;
}

public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr
{
    public override int NodeCount => 1 + Left.NodeCount + Right.NodeCount;
}

/// <summary>
/// A crossing, which is the one thing in the language that reads two bars by itself: `Above` is true
/// on the bar where the left side is above the right side and was at or below it on the bar before.
/// It is a node rather than sugar over `&gt;` and `[1]` because its warm-up is one bar deeper than
/// its operands' and that has to be computable without re-deriving it.
/// </summary>
public sealed record CrossExpr(CrossDirection Direction, Expr Left, Expr Right) : Expr
{
    public override int NodeCount => 1 + Left.NodeCount + Right.NodeCount;
}

/// <summary>A declared constant: a name and a typed value. Nothing else; there are no expressions here.</summary>
public sealed record StrategyConstant(string Name, ValueKind Type, decimal Number, bool Boolean);

/// <summary>
/// A declared indicator.
///
/// <para><paramref name="Source"/> is meaningless for <see cref="IndicatorKind.Atr"/> (a true range
/// is over high, low and the previous close) and for the two opening-range accumulators (each takes
/// the side its own name says), and in those cases it is <see cref="BarSeries.Close"/> by
/// convention. <paramref name="Period"/> is 0 for the accumulators, whose window is the declared
/// `opening_range` interval rather than a bar count.</para>
/// </summary>
public sealed record IndicatorDecl(string Name, IndicatorKind Kind, BarSeries Source, int Period);

/// <summary>One rule: what it does, and the condition under which it does it.</summary>
public sealed record StrategyRule(RuleKind Kind, Expr Condition);

/// <summary>How much to buy. <paramref name="Value"/> is a quantity or a fraction, per the kind.</summary>
public sealed record Sizing(SizingKind Kind, decimal Value);

/// <summary>
/// The protective stop. <paramref name="AtrPeriod"/> is 0 unless the kind is
/// <see cref="StopKind.EntryAtr"/>, in which case the distance is that many bars of ATR measured at
/// the bar the entry was taken on and then held fixed — an ATR that kept moving would be a stop the
/// program cannot state and the evaluator cannot reproduce.
/// </summary>
public sealed record StopRule(StopKind Kind, decimal Value, int AtrPeriod)
{
    public static readonly StopRule None = new(StopKind.None, 0m, 0);
}

public sealed record TargetRule(TargetKind Kind, decimal Value)
{
    public static readonly TargetRule None = new(TargetKind.None, 0m);
}

/// <summary>Minutes past midnight in the program's declared zone, as a wall clock and never an instant.</summary>
public sealed record TimeOfDay(int Hour, int Minute)
{
    public int MinuteOfDay => Hour * 60 + Minute;
    public override string ToString() => $"{Hour:00}:{Minute:00}";
}

/// <summary>A half-open wall-clock interval, `From` included and `To` excluded.</summary>
public sealed record TimeWindow(TimeOfDay From, TimeOfDay To)
{
    public override string ToString() => $"{From}-{To}";
}

[Flags]
public enum Weekdays
{
    None = 0, Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64,
    All = Mon | Tue | Wed | Thu | Fri | Sat | Sun
}

/// <summary>
/// When the program is allowed to act, in wall-clock terms.
///
/// <para><paramref name="TimeZone"/> is one of <see cref="StrategyLimits.TimeZones"/>, recorded and
/// not resolved: which tz data turned it into instants is the evaluator's record to keep, not the
/// parser's guess. <paramref name="EntryWindows"/> empty means "any time of day"; the weekday set is
/// all seven by default.</para>
///
/// <para><b>Do not compare two of these with <c>==</c>.</b> The synthesised record equality compares
/// <paramref name="EntryWindows"/> by REFERENCE, as it does for any collection member, so two filter
/// sets that say the same thing can be unequal. Two programs are compared by
/// <see cref="StrategyProgram.StrategyId"/>, which is over the canonical form, where the windows are
/// written out in a fixed order.</para>
/// </summary>
public sealed record TimeFilters(
    string TimeZone,
    Weekdays Days,
    IReadOnlyList<TimeWindow> EntryWindows,
    TimeWindow? OpeningRange,
    TimeOfDay? SessionExit)
{
    public static readonly TimeFilters Default = new("UTC", Weekdays.All, [], null, null);
}
