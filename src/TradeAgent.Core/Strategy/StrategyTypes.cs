namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE TWO TYPES, CHECKED WHILE IT IS STILL TEXT.
///
/// <para>`docs/COUNCIL.md`: "invalid periods and undefined sizing rejected at parse". The same goes
/// for a type: `entry when close` is a number where a question belongs, and there are exactly two
/// moments it can be caught — now, while there is a line number and a text to hand back, or on the
/// bar where the interpreter is asked whether 41,803.25 is true. The second one is a fault in a
/// promoted strategy, which `docs/COUNCIL.md` makes a defined outcome with no new exposure — a
/// correct outcome for a program nobody could have known was broken, and an avoidable one here.</para>
///
/// <para>Two types is the whole system: <see cref="ValueKind.Number"/> and
/// <see cref="ValueKind.Boolean"/>. No strings, no null, no coercion in either direction — a number
/// is never "truthy" and a condition is never 1 — so every mismatch is decidable by one walk over
/// the tree and there is no case where the answer depends on a value.</para>
/// </summary>
public static class StrategyTypes
{
    /// <summary>
    /// The type of an expression, or null with <paramref name="problem"/> naming the first mismatch
    /// found. The message is written for the model that wrote the program: it says which operator,
    /// which side, and what it wanted.
    /// </summary>
    public static ValueKind? TypeOf(Expr expr, out string? problem)
    {
        problem = null;
        return Walk(expr, ref problem);
    }

    static ValueKind? Walk(Expr expr, ref string? problem)
    {
        switch (expr)
        {
            case NumberLiteral or SeriesRef or IndicatorRef:
                return ValueKind.Number;

            case BoolLiteral:
                return ValueKind.Boolean;

            case UnaryExpr u:
            {
                var operand = Walk(u.Operand, ref problem);
                if (operand is null) return null;

                var wanted = u.Op == UnaryOp.Not ? ValueKind.Boolean : ValueKind.Number;
                if (operand != wanted)
                {
                    problem = u.Op == UnaryOp.Not
                        ? "`not` negates a true-or-false condition, and this one is over a number"
                        : "`-` negates a number, and this one is over a true-or-false value";
                    return null;
                }
                return wanted;
            }

            case CrossExpr c:
            {
                var left = Walk(c.Left, ref problem);
                if (left is null) return null;
                var right = Walk(c.Right, ref problem);
                if (right is null) return null;

                var name = c.Direction == CrossDirection.Above ? "crosses_above" : "crosses_below";
                if (left != ValueKind.Number || right != ValueKind.Number)
                {
                    problem = $"`{name}` crosses two numbers, and the " +
                        (left != ValueKind.Number ? "first" : "second") + " side of this one is a true-or-false value";
                    return null;
                }
                return ValueKind.Boolean;
            }

            case BinaryExpr b:
            {
                var left = Walk(b.Left, ref problem);
                if (left is null) return null;
                var right = Walk(b.Right, ref problem);
                if (right is null) return null;

                switch (b.Op)
                {
                    case BinaryOp.And or BinaryOp.Or:
                        if (left != ValueKind.Boolean || right != ValueKind.Boolean)
                        {
                            problem = $"`{Symbol(b.Op)}` joins two true-or-false conditions, and the " +
                                (left != ValueKind.Boolean ? "left" : "right") + " side of this one is a number";
                            return null;
                        }
                        return ValueKind.Boolean;

                    case BinaryOp.Equal or BinaryOp.NotEqual:
                        if (left != right)
                        {
                            problem = $"`{Symbol(b.Op)}` compares two values of the same kind, and this one has a " +
                                "number on one side and a true-or-false value on the other";
                            return null;
                        }
                        return ValueKind.Boolean;

                    case BinaryOp.Less or BinaryOp.LessOrEqual or BinaryOp.Greater or BinaryOp.GreaterOrEqual:
                        if (left != ValueKind.Number || right != ValueKind.Number)
                        {
                            problem = $"`{Symbol(b.Op)}` compares two numbers, and the " +
                                (left != ValueKind.Number ? "left" : "right") + " side of this one is a true-or-false value";
                            return null;
                        }
                        return ValueKind.Boolean;

                    default:
                        if (left != ValueKind.Number || right != ValueKind.Number)
                        {
                            problem = $"`{Symbol(b.Op)}` works on two numbers, and the " +
                                (left != ValueKind.Number ? "left" : "right") + " side of this one is a true-or-false value";
                            return null;
                        }
                        return ValueKind.Number;
                }
            }

            default:
                // Unreachable while every node kind above is covered, and a refusal rather than a
                // crash if a node kind is ever added without a case here.
                problem = "this condition holds a value this build does not know how to type";
                return null;
        }
    }

    static string Symbol(BinaryOp op) => op switch
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
