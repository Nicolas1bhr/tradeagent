using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Proving;

/// <summary>
/// THE HOLDOUT AND THE CAMPAIGN OF A PROVING MARKET, SET BY THE PROVING PROCESS AS THE OWNER WOULD SET THEM
/// (<c>U-proving-ground-b</c> item 4; the survey's Q2: "in a shadow home the proving host plays the owner in-process").
///
/// <para><b>The owner's press, in-process, in a proving home only.</b> What <c>TradingGateway.SetHoldout</c> does on the
/// owner's card — the judge's cost model settled first and nothing written without it, then <see cref="DatasetStore.SetHoldout"/>
/// and <see cref="CampaignStore.Open"/> in one transaction — with the cutoff and the budgets the market's spec names, never
/// the owner's settings. No pipe op and no verb reach it, as none reaches the owner's press. A database that is a real
/// home's, or one that is not the proving home's own, is refused before anything is read or written (SIM-001): this is the
/// owner's press played inside a proving ground, and never a press on his.</para>
/// </summary>
public static class ProvingCampaign
{
    /// <summary>
    /// SETS <paramref name="market"/>'s HOLDOUT FROM <paramref name="spec"/> AND OPENS ITS CAMPAIGN, at
    /// <paramref name="at"/>: the evaluation class is research — a proving market's runs are charged as the real ones are,
    /// which is what an exam of the process needs. A refusal of either store comes back as it was written, with nothing
    /// written.
    /// </summary>
    public static CampaignOpened Open(ProvingHome home, Database db, SyntheticMarketWritten market, SyntheticMarketSpec spec, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(spec);
        if (db.Kind != HomeKind.Proving)
            throw new InvalidOperationException(
                "a proving market's holdout and campaign are set only in a proving home, and this database is a real home's (SIM-001). "
                + "Nothing was read or written.");
        if (!ProvingHome.Same(db.FilePath, home.DatabaseFile))
            throw new InvalidOperationException(
                $"a proving market's holdout and campaign are set only in the database of the home it was written into, and the database at "
                + $"{db.FilePath} does not live in the proving home at {home.Dir} (SIM-001). Nothing was read or written.");
        if (spec.Holdout is not { } holdout)
            return new CampaignOpened(false, "this market's spec sets no holdout, so there is no cutoff to set and no campaign to open.", null);

        var datasets = new DatasetStore(db);
        return db.Write(_ =>
        {
            if (datasets.ById(market.Dataset.Id) is not { } set
                || !string.Equals(set.NormalisedSha256, market.Dataset.NormalisedSha256, StringComparison.OrdinalIgnoreCase)
                || set.Pair != spec.Instrument || set.FirstBar != spec.Start.ToUniversalTime())
                return new CampaignOpened(false,
                    $"dataset {market.Dataset.Id} in this proving home is not the market this spec wrote ({spec.Instrument} from "
                    + $"{spec.Start.ToUniversalTime():O}). Nothing was written.", null);

            // THE JUDGE FIRST, as the owner's press settles it: a cutoff without the campaign it is the subject of is months held
            // back with nothing counting the attempts.
            if (Strategy.VenueCostModel.For(set, new VenueStore(db, () => at), Strategy.VenueCostModel.DefaultCapital) is { Ok: false } judge)
                return new CampaignOpened(false, $"Nothing was held back: {judge.Why} No cutoff was written and no campaign was opened.", null);

            var cutoff = spec.Start.ToUniversalTime().AddDays(holdout.FromDay);
            var held = datasets.SetHoldout(set.Id, cutoff, EvaluationClass.Research);
            if (!held.Ok) return new CampaignOpened(false, held.Why, null);

            return new CampaignStore(db).Open(
                $"proving {set.Pair} {set.Interval} from {cutoff:yyyy-MM-dd}", datasets.ById(set.Id)!,
                holdout.TrialBudget, holdout.VerdictBudget, at);
        });
    }
}
