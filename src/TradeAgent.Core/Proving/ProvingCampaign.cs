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
        if (Unbound(home, market, spec) is { } why)
            return new CampaignOpened(false, why + " Nothing was written.", null);
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

    /// <summary>
    /// WHY <paramref name="spec"/> IS NOT THE SPEC THAT WROTE <paramref name="market"/>, in words — or null. A market is judged
    /// only on the spec it was generated from: its manifest's holdout first, in words of its own, since an in-sample trap is
    /// planted up to its market's own cutoff and judged from another is not the trap it names; then every other generating
    /// field, through the name the spec gives the market's truth, which is the SHA-256 of all of them.
    /// </summary>
    static string? Unbound(ProvingHome home, SyntheticMarketWritten market, SyntheticMarketSpec spec)
    {
        if (!market.TruthFile.StartsWith(home.TruthDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(market.TruthFile))
            return $"the market's truth at {market.TruthFile} is not in this proving home's truth folder, {home.TruthDir}, so there is no "
                   + "manifest to bind this spec to.";

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(market.TruthFile));
        var written = manifest.RootElement.TryGetProperty("holdout", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Object
            ? new SyntheticHoldout(h.GetProperty("from_day").GetInt32(), h.GetProperty("trial_budget").GetInt32(), h.GetProperty("verdict_budget").GetInt32())
            : null;
        if (written != spec.Holdout)
            return $"this spec's holdout ({Words(spec.Holdout)}) is not the holdout the market was written with ({Words(written)}): an "
                   + "in-sample trap is on up to its market's own cutoff, and judged from another cutoff it is not the trap its manifest names.";

        var named = SyntheticMarket.TruthFile(home, spec);
        if (!ProvingHome.Same(market.TruthFile, named) || !File.Exists(named))
            return $"this spec is not the spec that wrote the market: it names its market's truth {Path.GetFileName(named)}, and the market's "
                   + $"truth is {Path.GetFileName(market.TruthFile)} — its seed, instrument, venue, start, span, series, edges or holdout differ.";
        return null;
    }

    static string Words(SyntheticHoldout? holdout) => holdout is { } h
        ? $"from day {h.FromDay}, {h.TrialBudget} trial(s), {h.VerdictBudget} verdict(s)"
        : "none";
}
