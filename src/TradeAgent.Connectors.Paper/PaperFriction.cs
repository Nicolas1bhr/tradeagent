using System.Globalization;

namespace TradeAgent.Connectors.Paper;

/// <summary>
/// WHAT A SIMULATED FILL IS CHARGED, AS FRACTIONS, AND WHAT IT SAYS WHEN IT IS CHARGED NOTHING.
///
/// <para>The same two numbers a backtest declares (<c>docs/CONTRACTS.md</c>, "The backtest": fees and
/// slippage are FRACTIONS, <c>0.001</c> is ten basis points) and the same rule about declaring
/// neither: a run with no fee and no slippage is FRICTIONLESS and the answer says so in that word,
/// rather than letting a zero fee read as a measurement of a venue that charges nothing.</para>
///
/// <para>Both default to zero, so the shipped paper connector is frictionless and states it — on
/// every fill it writes and in the connector's own status line. Zero here is a DECLARATION that
/// nothing was modelled, never a reading taken from a venue.</para>
/// </summary>
public sealed record PaperFriction(decimal FeeFraction, decimal SlippageFraction)
{
    /// <summary>Nothing declared: no fee, no slippage. The shipped default, and it says so.</summary>
    public static readonly PaperFriction None = new(0m, 0m);

    /// <summary>True when neither a fee nor a slippage was declared. See the sentence below.</summary>
    public bool IsFrictionless => FeeFraction <= 0m && SlippageFraction <= 0m;

    /// <summary>
    /// The sentence stored beside every fill this friction produced, and repeated in the connector's
    /// status detail. It is written on the fill rather than derived at read time because the numbers
    /// can be changed by the owner between one fill and the next, and a fill has to keep saying what
    /// it was actually simulated under.
    /// </summary>
    public string Sentence => IsFrictionless
        ? "FRICTIONLESS: no fee and no slippage were declared, so this simulated fill was charged "
          + "nothing. That is a declaration that nothing was modelled, not a measurement of a venue."
        : $"declared fee fraction {Plain(FeeFraction)} and slippage fraction {Plain(SlippageFraction)}, "
          + "declared by the account owner and applied to this simulated fill.";

    static string Plain(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);
}
