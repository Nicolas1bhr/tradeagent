namespace TradeAgent.Core.Strategy;

/// <summary>
/// A NUMBER, A TRUE-OR-FALSE, OR NOTHING AT ALL.
///
/// <para>The third case is the one that matters. An indicator warming up, an opening range whose
/// session has not opened, a history reference into bars the run has not seen — none of those is a
/// zero and none of them is false, and a language that answered 0 would let a program trade on the
/// absence of data. So a value is UNDEFINED until it is not, undefined propagates, and an event whose
/// rules come back undefined is not evaluated at all.</para>
/// </summary>
readonly struct Val
{
    const byte Nothing = 0;
    const byte IsNumber = 1;
    const byte IsBoolean = 2;

    readonly byte tag;
    readonly decimal number;
    readonly bool boolean;

    Val(byte tag, decimal number, bool boolean)
    {
        this.tag = tag;
        this.number = number;
        this.boolean = boolean;
    }

    /// <summary>No value. The default, so a forgotten assignment is undefined rather than zero.</summary>
    public static readonly Val Undefined = default;

    public bool Defined => tag != Nothing;

    public bool Boolean => boolean;

    public decimal Number => number;

    /// <summary>Which of the two types this is. The parser has already checked that it is the right one.</summary>
    public bool IsBool => tag == IsBoolean;

    public static Val Of(decimal value) => new(IsNumber, value, false);

    public static Val Of(bool value) => new(IsBoolean, 0m, value);
}

/// <summary>
/// A FAULT, ON ITS WAY OUT OF THE INTERPRETER AND NO FURTHER.
///
/// <para>Thrown inside the expression walk — a division by zero, an over-budget event, arithmetic
/// that overflowed — and caught at the one boundary in <see cref="StrategyEvaluator"/>, where it
/// becomes <see cref="EvaluationOutcome.Faulted"/> with this reason. An exception is the readable way
/// to abandon a recursive descent; it is not the way a caller finds out, which is why nothing outside
/// this assembly can see this type.</para>
/// </summary>
sealed class EvaluationFault(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// ONE CONDITION, ON ONE BAR, IN THREE-VALUED LOGIC.
///
/// <para>Every leaf is read from the run's state — a bar series from the bar history, an indicator
/// from its window — and both can be absent, so the walk is over three values rather than two.
/// `and` and `or` are Kleene's: a definitely-false side makes `and` false even when the other side is
/// unknown, because that is the answer a reader would give and it saves evaluating the rest.</para>
///
/// <para><b>`back` is how a crossing reads yesterday.</b> `crosses_above(a, b)` needs both sides on
/// this bar and on the one before, so it evaluates its operands twice with the whole subtree shifted
/// one bar — which is exactly why `StrategyWarmUp` charges a crossing one bar more than its deeper
/// side, and why a crossing of a crossing is warm one bar later still.</para>
/// </summary>
sealed class StrategyInterpreter(EvaluationState state)
{
    /// <summary>Expression nodes visited on this event.</summary>
    public int Operations { get; private set; }

    /// <summary>One condition's value, with every reference shifted <paramref name="back"/> bars.</summary>
    public Val Eval(Expr expr, int back = 0)
    {
        Operations++;

        switch (expr)
        {
            case NumberLiteral n:
                return Val.Of(n.Value);

            case BoolLiteral b:
                return Val.Of(b.Value);

            case SeriesRef s:
                return state.History.TryAt(s.Back + back, out var bar)
                    ? Val.Of(Series(bar, s.Series))
                    : Val.Undefined;

            case IndicatorRef i:
                return state.IndicatorValue(i.Name, i.Back + back) is { } value
                    ? Val.Of(value)
                    : Val.Undefined;

            case UnaryExpr u:
            {
                var operand = Eval(u.Operand, back);
                if (!operand.Defined) return Val.Undefined;

                return u.Op == UnaryOp.Not ? Val.Of(!operand.Boolean) : Val.Of(-operand.Number);
            }

            case CrossExpr c:
            {
                var leftNow = Eval(c.Left, back);
                var rightNow = Eval(c.Right, back);
                var leftBefore = Eval(c.Left, back + 1);
                var rightBefore = Eval(c.Right, back + 1);

                if (!leftNow.Defined || !rightNow.Defined || !leftBefore.Defined || !rightBefore.Defined)
                    return Val.Undefined;

                return Val.Of(c.Direction == CrossDirection.Above
                    ? leftNow.Number > rightNow.Number && leftBefore.Number <= rightBefore.Number
                    : leftNow.Number < rightNow.Number && leftBefore.Number >= rightBefore.Number);
            }

            case BinaryExpr b:
                return Binary(b, back);

            default:
                // Unreachable while every node kind is covered, and a defined fault rather than a
                // crash if a kind is ever added without a case here.
                throw new EvaluationFault("a rule holds a value this build does not know how to evaluate");
        }
    }

    Val Binary(BinaryExpr b, int back)
    {
        if (b.Op == BinaryOp.And)
        {
            var left = Eval(b.Left, back);
            if (left.Defined && !left.Boolean) return Val.Of(false);

            var right = Eval(b.Right, back);
            if (right.Defined && !right.Boolean) return Val.Of(false);

            return left.Defined && right.Defined ? Val.Of(true) : Val.Undefined;
        }

        if (b.Op == BinaryOp.Or)
        {
            var left = Eval(b.Left, back);
            if (left.Defined && left.Boolean) return Val.Of(true);

            var right = Eval(b.Right, back);
            if (right.Defined && right.Boolean) return Val.Of(true);

            return left.Defined && right.Defined ? Val.Of(false) : Val.Undefined;
        }

        var a = Eval(b.Left, back);
        var c = Eval(b.Right, back);
        if (!a.Defined || !c.Defined) return Val.Undefined;

        switch (b.Op)
        {
            case BinaryOp.Add: return Val.Of(a.Number + c.Number);
            case BinaryOp.Subtract: return Val.Of(a.Number - c.Number);
            case BinaryOp.Multiply: return Val.Of(a.Number * c.Number);

            // A DIVISION BY ZERO IS A DEFINED OUTCOME. `docs/COUNCIL.md` makes an interpreter fault
            // one, and the alternative here is a `DivideByZeroException` from a text an agent wrote.
            case BinaryOp.Divide:
                if (c.Number == 0m)
                    throw new EvaluationFault(
                        $"a rule divided {a.Number} by zero, which has no value to compare or size against");
                return Val.Of(a.Number / c.Number);

            case BinaryOp.Less: return Val.Of(a.Number < c.Number);
            case BinaryOp.LessOrEqual: return Val.Of(a.Number <= c.Number);
            case BinaryOp.Greater: return Val.Of(a.Number > c.Number);
            case BinaryOp.GreaterOrEqual: return Val.Of(a.Number >= c.Number);

            // The two types never compare across each other: the parser refused that while it was
            // still text, so one side's type settles which comparison this is.
            case BinaryOp.Equal:
                return Val.Of(a.IsBool ? a.Boolean == c.Boolean : a.Number == c.Number);

            default:
                return Val.Of(a.IsBool ? a.Boolean != c.Boolean : a.Number != c.Number);
        }
    }

    static decimal Series(TradeAgent.Core.Data.KlineBar bar, BarSeries series) => series switch
    {
        BarSeries.Open => bar.Open,
        BarSeries.High => bar.High,
        BarSeries.Low => bar.Low,
        BarSeries.Close => bar.Close,
        _ => bar.Volume
    };
}
