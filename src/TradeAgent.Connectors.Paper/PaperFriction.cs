using TradeAgent.Core.Strategy;

namespace TradeAgent.Connectors.Paper;

/// <summary>
/// WHAT A SIMULATED FILL IS CHARGED, AS FRACTIONS — AND WHO SAID SO.
///
/// <para>The same two numbers a backtest uses (<c>docs/CONTRACTS.md</c>, "The backtest": fees and
/// slippage are FRACTIONS, <c>0.001</c> is ten basis points), each with its SOURCE
/// (<c>U-paper-friction</c>): the account owner's override, TradeAgent's venue cost model — named by id
/// and sha in <see cref="Model"/> — or nothing at all. The fill records the sentence, and the sentence
/// says which, because a fee TradeAgent took from its own table and a fee the owner declared are two
/// different facts about the same number.</para>
///
/// <para><b>The connector's own default is still <see cref="None"/></b>: no fee, no slippage, and the
/// word FRICTIONLESS on every fill, which is a declaration that nothing was modelled and never a
/// reading taken from a venue. The venue model is chosen by the HOST that builds the connector
/// (<c>AppHost.PaperChoice</c>), from the owner's settings; a connector built with nothing handed to
/// it charges nothing and says so.</para>
///
/// <para>Two numbers handed over with no source say what they always said: declared by the account
/// owner.</para>
/// </summary>
public sealed record PaperFriction(decimal FeeFraction, decimal SlippageFraction)
{
    /// <summary>Nothing declared: no fee, no slippage. The connector's own default, and it says so.</summary>
    public static readonly PaperFriction None = new(0m, 0m)
    {
        FeeSource = FrictionSource.None,
        SlippageSource = FrictionSource.None
    };

    /// <summary>Where <see cref="FeeFraction"/> came from. The owner, unless this was built from a resolution that said otherwise.</summary>
    public FrictionSource FeeSource { get; init; } = FrictionSource.Owner;

    /// <summary>Where <see cref="SlippageFraction"/> came from. See <see cref="FeeSource"/>.</summary>
    public FrictionSource SlippageSource { get; init; } = FrictionSource.Owner;

    /// <summary>The venue cost model a number was taken from — its id and sha are in the sentence — or null because none was.</summary>
    public VenueFriction? Model { get; init; }

    /// <summary>The friction in force a host resolved, as the connector takes it.</summary>
    public static PaperFriction Of(FrictionInForce friction) => new(friction.Fee, friction.Slippage)
    {
        FeeSource = friction.FeeSource,
        SlippageSource = friction.SlippageSource,
        Model = friction.Model
    };

    /// <summary>The same numbers and sources as the one record that words them.</summary>
    public FrictionInForce InForce => new(FeeFraction, FeeSource, SlippageFraction, SlippageSource, Model);

    /// <summary>True when neither a fee nor a slippage is charged. See the sentence below.</summary>
    public bool IsFrictionless => FeeFraction <= 0m && SlippageFraction <= 0m;

    /// <summary>
    /// The sentence stored beside every fill this friction produced, and repeated in the connector's
    /// status detail — <see cref="FrictionInForce.Sentence"/>, so a fill, the status and the owner's
    /// screen cannot word one friction three ways. It is written on the fill rather than derived at
    /// read time because the numbers can change between one fill and the next, and a fill has to keep
    /// saying what it was actually simulated under.
    /// </summary>
    public string Sentence => InForce.Sentence;
}
