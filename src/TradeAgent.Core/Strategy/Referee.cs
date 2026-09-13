using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// A VERDICT THAT HAS BEEN PAID FOR, or the refusal instead.
///
/// <para><b><see cref="Audience"/> is the whole value of this object, and it is <c>internal</c>.</b> It
/// is the only <see cref="BarAudience"/> in this product that may read past a dataset's
/// <c>holdout_from</c>, it exists only inside a charge that has already been written down, and no
/// assembly outside <c>TradeAgent.Core</c> can see it — so the gateway, the pipe server and the CLI
/// cannot obtain one at all, whatever they call. `HoldoutLedgerTests` holds the list of public doors
/// that hand out an audience to exactly one entry, by name, and that one never may.</para>
///
/// <para>What a caller outside Core gets is <see cref="Referee.HoldoutFeed"/>: it takes a charge and
/// answers a feed, which is the door, charged and recorded, rather than the key to it.</para>
/// </summary>
public sealed record VerdictCharge(bool Ok, string Why, int Spent, int Budget, long CampaignId, string VersionId)
{
    /// <summary>The referee's own audience, present only on a charge that was taken. See the type.</summary>
    internal BarAudience? Audience { get; init; }

    internal static VerdictCharge No(long campaignId, string versionId, string why, int spent, int budget) =>
        new(false, why, spent, budget, campaignId, versionId);
}

/// <summary>
/// THE REFEREE — CODE, NEVER A ROLE (<c>docs/COUNCIL.md</c>:55-57).
///
/// <para><b>What it is for.</b> "The referee protects a protocol, not a truth" (:131). The protocol is
/// the four things this unit built: holdout data the research process cannot reach, trials charged
/// against a campaign-wide budget that survives a team's replacement, a scoring policy fixed before the
/// work, and final evaluation kept scarce because every verdict leaks. No LLM occupies this; there is no
/// role called referee, no workspace folder for it, and nothing it does is anybody's opinion.</para>
///
/// <para><b>What is HERE and what is `U-referee-2`'s.</b> Here: the verdict budget, charged before
/// anything is computed, and the one in-process door onto the holdout bars. There: the holdout run
/// itself, the promotion record, invalidation, forward evidence and the delivery of the answer. The seam
/// is deliberate and it is a seam rather than a stub — <see cref="RequestVerdict"/> really charges, and
/// <see cref="HoldoutFeed"/> really serves the bars nothing else can reach, so the half that cannot be
/// retrofitted (a leaked holdout cannot become unseen, :212) is done and the half that can is not
/// pretended.</para>
///
/// <para><b>Why the charge comes first.</b> A budget checked after the holdout run refuses nothing that
/// matters: the bars have been read, the metric exists, and whoever asked has learnt something about
/// months they were never shown. So the row is written by <see cref="RequestVerdict"/>, in its own
/// transaction, and the audience that reads the holdout is handed back only on the far side of that
/// write. "Charged after the run" is the mutant this class was built against.</para>
///
/// <para><b>No pipe op and no `trade` verb reaches any of this.</b> There is no operation an agent can
/// send that asks for a verdict, and that is not an omission to be filled in later: the request is a
/// decision about the owner's private evidence, and the caller being judged is the one party that must
/// not be able to spend it. `HoldoutOverPipeTests` asks every op this build has and none of them serves
/// a held-back bar.</para>
/// </summary>
public sealed class Referee(Database db, Func<DateTimeOffset>? now = null)
{
    readonly CampaignStore _campaigns = new(db);
    readonly DatasetStore _datasets = new(db);
    readonly StrategyStore _strategies = new(db);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// WHAT A HOLDOUT RUN IS RECORDED UNDER, and it is deliberately not a council role.
    ///
    /// <para><c>CouncilRoles.IsKnown("referee")</c> is false, so this word grants nothing anywhere: it is
    /// attribution on a <c>strategy_run</c> row, in the column that otherwise says which director asked.
    /// The referee is CODE (<c>docs/COUNCIL.md</c>:55-57) — no workspace, no budget, no turn — and the
    /// reason the run needs a mark of its own is the daily report: a run over the held-back months is
    /// the one run whose figures must never be printed where an agent can read them, and the report
    /// tells it apart by this.</para>
    /// </summary>
    public const string RunRole = "referee";

    /// <summary>
    /// THE MEASURING APPARATUS' OWN VERSION, hashed into every promotion.
    ///
    /// <para>Rule 9 binds promotion to the "evaluator" as well as to the code and the data: the trace
    /// this app produced, the metrics it computed from that trace, and the clauses it applied to those
    /// metrics. None of the three is inside the version id — that hashes the PROGRAM — so a build whose
    /// backtest, metrics or scoring changed would otherwise be able to write a new verdict over the id
    /// of an old one. It is a constant rather than a computed string because it must move only when
    /// somebody decides it has: a number that tracked the assembly version would invalidate every
    /// promotion on every rebuild.</para>
    /// </summary>
    public const string EvaluatorVersion = "backtest=1;metrics=1;scoring=1";

    /// <summary>The campaign ledger this referee charges against. Read-only for a caller.</summary>
    public CampaignStore Campaigns => _campaigns;

    /// <summary>
    /// ASKS FOR A FINAL VERDICT ON ONE VERSION, AND CHARGES IT BEFORE ANYTHING RUNS.
    ///
    /// <para>The order is the guarantee: the campaign is read, the version is checked to exist, the
    /// lineage's verdicts are counted, and the charge is WRITTEN — all before a
    /// <see cref="BarAudience"/> that may read the holdout comes back. Over budget, nothing is written
    /// and nothing is returned that could read a bar.</para>
    ///
    /// <para>The version must already be in <c>strategy_version</c>: a verdict is about a program this
    /// installation has parsed and measured, and there is nothing for the referee to judge in a text
    /// nobody registered. It is by the version's own hash rather than by a name, so the thing judged is
    /// the thing that was submitted.</para>
    ///
    /// <para>Asking twice for the same version is one verdict and one charge — the same version over the
    /// same holdout is the same answer — so a crash between this call and the run it authorises leaves
    /// the verdict obtainable rather than paid for and unreachable.</para>
    /// </summary>
    public VerdictCharge RequestVerdict(string versionId, long campaignId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            return VerdictCharge.No(campaignId, versionId ?? "",
                "a verdict is about one version, named by its own hash. 'version' is required.", 0, 0);

        if (_campaigns.ById(campaignId) is not { } campaign)
            return VerdictCharge.No(campaignId, versionId,
                $"there is no campaign {campaignId} in this installation's ledger. A campaign is opened when "
                + "the account owner sets a holdout cutoff on a dataset.", 0, 0);

        if (_strategies.VersionById(versionId) is null)
            return VerdictCharge.No(campaignId, versionId,
                $"this installation has never accepted a version {versionId}, so there is nothing to judge. A "
                + "verdict is about a program TradeAgent has parsed and measured, named by the program's own "
                + "hash.", _campaigns.VerdictsInLineage(campaignId), campaign.VerdictBudget);

        // THE CHARGE. Written here, in its own transaction, before the audience below exists.
        var charged = _campaigns.ChargeVerdict(campaignId, versionId, _now());
        if (!charged.Ok)
            return VerdictCharge.No(campaignId, versionId, charged.Why, charged.Spent, charged.Budget);

        return new VerdictCharge(true, "", charged.Spent, charged.Budget, campaignId, versionId)
        {
            Audience = BarAudience.Referee
        };
    }

    /// <summary>
    /// THE HOLDOUT BARS OF THIS CHARGE'S CAMPAIGN — the one door past a cutoff, and it needs a charge
    /// that was recorded.
    ///
    /// <para>The dataset is the CAMPAIGN's own <c>holdout_dataset_id</c> and not a number the caller
    /// passes: a charge for one campaign cannot be spent on another campaign's private months. The window
    /// defaults to everything, which is what a verdict wants — the whole of the data the research process
    /// never saw — and a narrower one is allowed because <c>U-referee-2</c> may want the holdout in
    /// pieces.</para>
    ///
    /// <para>A refused charge answers a refusal, not a feed. There is no overload that takes an audience,
    /// and <see cref="VerdictCharge.Audience"/> is internal, so this method is the whole of what an
    /// assembly outside <c>TradeAgent.Core</c> can do with the holdout.</para>
    /// </summary>
    public BarFeedOpen HoldoutFeed(VerdictCharge charge, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(charge);

        if (charge.Audience is not { } audience)
            return BarFeedOpen.No(
                "this verdict was not charged, so there is no holdout to read: " + charge.Why);

        if (_campaigns.ById(charge.CampaignId) is not { } campaign)
            return BarFeedOpen.No($"there is no campaign {charge.CampaignId} in this installation's ledger");

        return BarFeed.Open(_datasets, campaign.HoldoutDatasetId, audience, from, to);
    }
}
