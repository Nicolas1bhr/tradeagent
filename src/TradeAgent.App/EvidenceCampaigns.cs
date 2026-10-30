using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;

namespace TradeAgent.App;

/// <summary>What the setup step's one press did, or why it did nothing, in the owner's words.</summary>
public sealed record EvidenceSetup(bool Ok, string Words, CampaignRow? Campaign);

/// <summary>
/// THE DATA, THE HOLDOUT AND THE CAMPAIGN — SET UP IN ONE PRESS AND FOLLOWED BY RULE, UNATTENDED (<c>U-evidence-campaign</c>; R25 D7).
///
/// <para><b>In-process only.</b> It calls the Download press's own code (<see cref="MarketDataService.CollectAsync(string,
/// DateTimeOffset, IProgress{ProvisionProgress}, CancellationToken, Action{string})"/>, through <c>collect</c>) and the holdout
/// press's own path (<see cref="TradingGateway.SetHoldout"/>, <see cref="TradingGateway.OpenForwardCampaign"/>); nothing on the agent
/// pipe reaches it, exactly as nothing reaches those presses.</para>
/// </summary>
/// <param name="gateway">The gateway as it is now — read at each call, because a platform switch replaces it.</param>
/// <param name="collect">The Download press: a pair and the instant it is asked at, to what it recorded.</param>
public sealed class EvidenceCampaigns(
    Func<TradingGateway> gateway,
    Func<string, DateTimeOffset, IProgress<ProvisionProgress>?, CancellationToken, Task<DataCollection>> collect)
{
    /// <summary>How long after a download that did not hold the minimum the next one is asked for.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

    readonly Dictionary<long, DateTimeOffset> _asked = [];
    int _renewing;

    /// <summary>Whether a campaign over real history is open: the setup step's question, answered from the ledger.</summary>
    public bool Ready()
    {
        var gw = gateway();
        return gw.Campaigns.All().Any(c => c.IsOpen
            && gw.Datasets.ById(c.HoldoutDatasetId) is { } set
            && EvaluationClass.Or(set.EvaluationClass) == EvaluationClass.Research);
    }

    /// <summary>
    /// THE SETUP STEP'S ONE PRESS: the newest accepted download of the owner's pair (collected now when there is none), held back
    /// from <see cref="EvidenceDefaults.Cutoff"/> — or from the cutoff it already has, which is never moved — and its campaign
    /// opened with the owner's budgets, through <see cref="TradingGateway.SetHoldout"/>. A press when a campaign is already open
    /// changes nothing.
    /// </summary>
    public async Task<EvidenceSetup> SetUpAsync(DateTimeOffset now, IProgress<ProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var gw = gateway();
        if (Ready())
            return new EvidenceSetup(true, "A campaign is already open over real history, so nothing was changed.", null);

        var pair = gw.Settings.MarketDataPair;
        var set = gw.Datasets.Newest(pair) is { } newest ? gw.Datasets.Checked(newest) : null;
        if (set is null || set.State != DatasetState.ACCEPTED)
        {
            var collected = await collect(pair, now, progress, ct);
            if (collected.Dataset is not { } fresh || fresh.State != DatasetState.ACCEPTED)
                return new EvidenceSetup(false, $"Nothing was held back: {collected.Summary}", null);
            set = fresh;
        }

        // A CUTOFF ALREADY THERE IS THE OWNER'S AND IS KEPT: moving it later with no campaign holding it would release months he
        // held back, and the store refuses earlier. The default is chosen only for a download that holds none.
        if ((set.HoldoutFrom ?? EvidenceDefaults.Cutoff(set)) is not { } cutoff)
            return new EvidenceSetup(false,
                $"Nothing was held back: the {set.Pair} download holds too few months to keep the last {EvidenceDefaults.HoldoutMonths} "
                + "back and still leave a month to research.", null);

        var (done, campaign) = gw.SetHoldout(set.Id, cutoff, EvaluationClass.Research);
        if (!done.Ok || campaign is null) return new EvidenceSetup(false, done.Why, null);

        return new EvidenceSetup(true,
            $"{set.Pair} bars from {cutoff.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC on are held "
            + "back: the AI cannot read them or backtest over them, and this date can no longer be moved. "
            + CampaignStore.PressNote(campaign, gw.Campaigns.JudgementsSpent(campaign.Id), gw.Campaigns.OverTheSameMonths(campaign.Id)),
            campaign);
    }

    /// <summary>
    /// ONE PASS OF THE RULE (R25 D7), on the app's clock: arm the fresh holdout of a spent campaign; and for one armed long enough
    /// ago to hold the minimum, download its series again (at most once a day) and open the campaign over it. Answers the activity
    /// lines it has to say — each is said once, by the pass that did it. A pass while one is still running does nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> RenewAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _renewing, 1) == 1) return [];
        try
        {
            var gw = gateway();
            var renewals = gw.Renewals;
            var said = new List<string>();

            foreach (var armed in renewals.Arm(now))
                said.Add($"Campaign {armed.SpentCampaignId} has spent its {armed.Spent}. " + CampaignRenewal.Waiting(armed, null));

            foreach (var held in renewals.Holding())
            {
                if (now < held.Earliest) continue;
                if (_asked.TryGetValue(held.SpentCampaignId, out var last) && now - last < RetryAfter) continue;
                if (gw.Campaigns.ById(held.SpentCampaignId) is not { } spent
                    || gw.Datasets.ById(spent.HoldoutDatasetId) is not { } parent) continue;

                _asked[held.SpentCampaignId] = now;
                DataCollection collected;
                try { collected = await collect(parent.Pair, now, null, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    said.Add($"The fresh {parent.Pair} months for the campaign after campaign {held.SpentCampaignId} could not be "
                             + $"downloaded: {ex.Message.ReplaceLineEndings(" ")} TradeAgent asks again in a day.");
                    continue;
                }

                var opened = collected.Dataset is { } fresh
                    ? gw.OpenForwardCampaign(held.SpentCampaignId, fresh.Id)
                    : (false, $"nothing was downloaded: {collected.Summary}", null);
                said.Add(opened.Ok
                    ? opened.Why
                    : $"The campaign after campaign {held.SpentCampaignId} did not open yet: {opened.Why} TradeAgent asks again in a day.");
            }
            return said;
        }
        finally { Volatile.Write(ref _renewing, 0); }
    }
}
