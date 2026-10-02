using System.Globalization;

namespace TradeAgent.Core.Strategy;

/// <summary>Where one of a simulated fill's two friction numbers came from.</summary>
public enum FrictionSource
{
    /// <summary>
    /// Nothing declared and no venue cost model to take a number from: zero, said as FRICTIONLESS.
    /// The paper connector's own default, which is what it keeps when a host resolves nothing.
    /// </summary>
    None,

    /// <summary>The account owner's own number — zero included — used exactly as they set it.</summary>
    Owner,

    /// <summary>TradeAgent's venue cost model for the venue the prices are of (<see cref="VenueFriction"/>).</summary>
    VenueModel
}

/// <summary>
/// THE FRICTION A PAPER FILL PAYS, AND WHERE EACH OF ITS TWO NUMBERS CAME FROM (<c>U-paper-friction</c>).
///
/// <para><b>Per number, not per pair.</b> The owner's override is two settings and either may be set
/// on its own: a number the owner set is theirs, zero included, and a number they left unset is the
/// venue cost model's. That is the rule a research run follows for its own declared fields, so a paper
/// fill and a backtest that both leave the slippage alone are charged the same slippage.</para>
///
/// <para><b>Said, not inferred.</b> <see cref="Sentence"/> is what a fill records beside itself and
/// <see cref="Line"/> is what the owner reads; both name the source of each number, and a number taken
/// from the venue model names the model by its id and sha. A fill priced by TradeAgent's own table must
/// never read as a number the owner declared — which is what the connector used to write beside every
/// non-zero friction — and a zero must never read as a venue that charges nothing.</para>
/// </summary>
/// <param name="Fee">A FRACTION of each fill's notional: <c>0.001</c> is ten basis points.</param>
/// <param name="FeeSource">Where <paramref name="Fee"/> came from.</param>
/// <param name="Slippage">A FRACTION of the price, adverse, on a market order's fill.</param>
/// <param name="SlippageSource">Where <paramref name="Slippage"/> came from.</param>
/// <param name="Model">The venue cost model a number was taken from, or null because none was.</param>
public sealed record FrictionInForce(
    decimal Fee, FrictionSource FeeSource, decimal Slippage, FrictionSource SlippageSource, VenueFriction? Model)
{
    /// <summary>Nothing declared and nothing to take from: the paper connector's own default.</summary>
    public static readonly FrictionInForce Frictionless =
        new(0m, FrictionSource.None, 0m, FrictionSource.None, null);

    /// <summary>
    /// EACH NUMBER FROM ITS OWN SOURCE: the owner's override where there is one, zero included; else the
    /// venue cost model's; else zero, said as FRICTIONLESS. <see cref="Model"/> is kept only when a number
    /// was actually taken from it, so an owner who overrode both is never described as charged by a table
    /// that charged them nothing.
    /// </summary>
    public static FrictionInForce Resolve(decimal? feeOverride, decimal? slippageOverride, VenueFriction? venue)
    {
        var (fee, feeFrom) = feeOverride is { } f ? (f, FrictionSource.Owner)
            : venue is { } v ? (v.FeeRate, FrictionSource.VenueModel)
            : (0m, FrictionSource.None);
        var (slippage, slippageFrom) = slippageOverride is { } s ? (s, FrictionSource.Owner)
            : venue is { } w ? (w.SlippageRate, FrictionSource.VenueModel)
            : (0m, FrictionSource.None);

        var taken = feeFrom == FrictionSource.VenueModel || slippageFrom == FrictionSource.VenueModel;
        return new FrictionInForce(fee, feeFrom, slippage, slippageFrom, taken ? venue : null);
    }

    /// <summary>True when nothing is charged, whoever said so.</summary>
    public bool IsFrictionless => Fee <= 0m && Slippage <= 0m;

    /// <summary>
    /// THE SOURCE AS ONE WORD, for a reader that is a program: <c>owner</c>, <c>venue_model</c> or
    /// <c>none</c> when both numbers came from the same place, and <c>fee/slippage</c> —
    /// <c>owner/venue_model</c>, say — when they did not.
    /// </summary>
    public string Source => FeeSource == SlippageSource
        ? Word(FeeSource)
        : $"{Word(FeeSource)}/{Word(SlippageSource)}";

    /// <summary>
    /// THE SENTENCE A FILL RECORDS BESIDE ITSELF, and the connector repeats in its status line. Written
    /// on the fill rather than derived at read time, because the owner can change the override between
    /// one fill and the next and the venue table can be re-read between two releases: a fill has to keep
    /// saying what it was actually simulated under.
    /// </summary>
    public string Sentence
    {
        get
        {
            // NOTHING DECLARED AND NOTHING TO TAKE FROM — the connector's own default, in the words it
            // has always used, so a fill written today reads like one written before this existed.
            if (FeeSource == FrictionSource.None && SlippageSource == FrictionSource.None)
                return "FRICTIONLESS: no fee and no slippage were declared, so this simulated fill was charged "
                       + "nothing. That is a declaration that nothing was modelled, not a measurement of a venue.";

            if (FeeSource == FrictionSource.Owner && SlippageSource == FrictionSource.Owner)
                return IsFrictionless
                    ? "FRICTIONLESS by the account owner's own declaration: they set both the fee and the "
                      + "slippage to 0, so this simulated fill was charged nothing. That is a declaration, not a "
                      + "measurement of a venue."
                    : $"declared fee fraction {Plain(Fee)} and slippage fraction {Plain(Slippage)}, declared by "
                      + "the account owner and applied to this simulated fill.";

            // BOTH FROM THE TABLE: the model named once, by id and sha, and each number said for what it is
            // — a rate the venue published, and an assumption of TradeAgent's.
            if (FeeSource == FrictionSource.VenueModel && SlippageSource == FrictionSource.VenueModel
                && Model is { } m)
                return $"fee fraction {Plain(Fee)} and slippage fraction {Plain(Slippage)} from {m.Named}: "
                       + $"the fee is {m.FeeWords}, and the slippage is {m.SlippageWords}. Applied to this "
                       + "simulated fill.";

            return $"{Part("fee", Fee, FeeSource, Model?.FeeWords)}; "
                   + $"{Part("slippage", Slippage, SlippageSource, Model?.SlippageWords)}. "
                   + "Applied to this simulated fill.";
        }
    }

    /// <summary>
    /// WHAT A PAPER FILL PAYS, AS THE OWNER READS IT — the value after "fills pay" on the paper row of
    /// the Trading platform card and in the daily report: <c>0.1% + 0.02% (assumption) — Binance spot
    /// standard taker, 2026-10-02</c>. Percent, because that is how a fee is read; the fractions are in
    /// <see cref="Sentence"/>.
    /// </summary>
    public string Line
    {
        get
        {
            if (FeeSource == FrictionSource.None && SlippageSource == FrictionSource.None)
                return "nothing — FRICTIONLESS: no fee and no slippage are modelled";

            if (FeeSource == FrictionSource.VenueModel && SlippageSource == FrictionSource.VenueModel
                && Model is { } m)
                return $"{Percent(Fee)}% + {Percent(Slippage)}% (assumption) — {m.Venue} standard taker, {Day(m)}";

            if (FeeSource == FrictionSource.Owner && SlippageSource == FrictionSource.Owner)
                return $"{Percent(Fee)}% + {Percent(Slippage)}% — your override"
                       + (IsFrictionless ? ", so nothing: FRICTIONLESS" : "");

            return $"{Percent(Fee)}%{Tag(FeeSource, fee: true)} + {Percent(Slippage)}%{Tag(SlippageSource, fee: false)}";
        }
    }

    string Part(string name, decimal value, FrictionSource source, string? words) => source switch
    {
        FrictionSource.Owner => $"{name} fraction {Plain(value)}, declared by the account owner",
        FrictionSource.VenueModel => $"{name} fraction {Plain(value)} from "
                                     + $"{Model?.Named ?? "TradeAgent's venue cost model"}: {words}",
        _ => $"{name} fraction 0, with nothing declared and no venue cost model to take one from"
    };

    string Tag(FrictionSource source, bool fee) => source switch
    {
        FrictionSource.Owner => " (your override)",
        FrictionSource.VenueModel when fee => Model is { } m ? $" ({m.Venue} standard taker, {Day(m)})" : " (venue cost model)",
        FrictionSource.VenueModel => " (assumption)",
        _ => " (nothing modelled)"
    };

    static string Word(FrictionSource source) => source switch
    {
        FrictionSource.Owner => "owner",
        FrictionSource.VenueModel => "venue_model",
        _ => "none"
    };

    static string Day(VenueFriction m) => m.FeeReadOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A fraction as the fill's sentence has always printed one: invariant, trailing zeros gone.</summary>
    static string Plain(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>A fraction as a percentage, trailing zeros gone: 0.001 is "0.1".</summary>
    static string Percent(decimal d) => StrategyParser.Number(d * 100m);
}
