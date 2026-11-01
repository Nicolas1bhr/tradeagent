using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;

namespace TradeAgent.App;

/// <summary>
/// What the setup step's one press did, or why it did nothing, in the owner's words: one sentence per pair asked for, the
/// campaigns it opened — one per pair (<c>U-evidence-breadth</c>) — and the pairs whose download stopped part way, which the next
/// press finishes (<c>U-evidence-holdout-length</c>).
/// </summary>
public sealed record EvidenceSetup(bool Ok, string Words, IReadOnlyList<CampaignRow> Campaigns, IReadOnlyList<string> Unfinished);

/// <summary>
/// ONE OPEN CAMPAIGN OVER REAL HISTORY AS THE OWNER'S PAGE SHOWS IT (<c>U-evidence-breadth</c> item 4): its pair, the judgements and
/// research runs it has spent — counted as the budgets count them (<see cref="CampaignStore.JudgementsSpent"/>,
/// <see cref="CampaignStore.TrialsCharged(long)"/>) — the fresh holdout armed for it, if any, and how long its own holdout is
/// (<see cref="CampaignStore.HoldoutSpan"/>; null when its end cannot be read), which says whether a verdict over it can be
/// conclusive (<c>U-evidence-holdout-length</c> item 3).
/// </summary>
public sealed record CampaignStanding(string Pair, CampaignRow Campaign, int Judgements, int Trials, string? Spent,
    ForwardHoldout? Renewal, string Next, TimeSpan? Holdout)
{
    public int JudgementsLeft => Math.Max(0, Campaign.VerdictBudget - Judgements);
    public int TrialsLeft => Math.Max(0, Campaign.TrialBudget - Trials);

    /// <summary>The line the owner reads: what is left, and — once spent — what the next campaign of this pair waits for and until when.</summary>
    public string Line(DateTimeOffset now)
    {
        var line = $"{Pair}, campaign {Campaign.Id}, held back from {Campaign.HoldoutFrom.UtcDateTime:yyyy-MM-dd} UTC: "
            + $"{JudgementsLeft:N0} of {Campaign.VerdictBudget:N0} final judgements and {TrialsLeft:N0} of {Campaign.TrialBudget:N0} "
            + "research runs left. " + HoldoutLength.Words(Holdout);
        if (Spent is null) return line;
        if (Renewal is { Successor: { } next }) return line + $" Its {Spent} are spent; campaign {next} follows it over fresh months.";
        if (Renewal is { } held)
        {
            var days = Math.Max(0, Math.Ceiling((held.Earliest - now).TotalDays));
            return line + $" Its {Spent} are spent; the next {Pair} campaign waits for fresh bars from "
                + $"{held.Cutoff.UtcDateTime:yyyy-MM-dd} UTC — at least {CampaignRenewal.MinimumFreshDays} days of them — so it opens no "
                + $"earlier than {held.Earliest.UtcDateTime:yyyy-MM-dd} ({days:N0} days from now).";
        }
        return line + $" Its {Spent} are spent. {Next}";
    }
}

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
/// <param name="check">
/// The instrument check for a pair (<c>AppHost.CheckInstrumentAsync</c>): the venue's own step, without which no campaign can pin
/// its judge's cost model and no run can round a size. Asked before each pair is held back; null asks nothing.
/// </param>
public sealed class EvidenceCampaigns(
    Func<TradingGateway> gateway,
    Func<string, DateTimeOffset, IProgress<ProvisionProgress>?, CancellationToken, Task<DataCollection>> collect,
    Func<string, CancellationToken, Task>? check = null)
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
    /// THE PAIRS UNDER EVIDENCE NOW: every pair with an open campaign over real history, oldest campaign first, each once
    /// (<c>U-evidence-breadth</c>). What the instrument check keeps verified and the pages list.
    /// </summary>
    public IReadOnlyList<string> Pairs()
    {
        var gw = gateway();
        return [.. gw.Campaigns.All().Where(c => c.IsOpen).OrderBy(c => c.Id)
            .Select(c => gw.Datasets.ById(c.HoldoutDatasetId))
            .Where(set => set is not null && EvaluationClass.Or(set.EvaluationClass) == EvaluationClass.Research)
            .Select(set => set!.Pair)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// EVERY OPEN CAMPAIGN OVER REAL HISTORY, ONE PER LINE ON THE OWNER'S PAGE (<c>U-evidence-breadth</c> item 4): by pair, then oldest
    /// first, each with what it has left and what its renewal waits for. Read off the ledger at each call.
    /// </summary>
    public IReadOnlyList<CampaignStanding> Standings()
    {
        var gw = gateway();
        var renewals = gw.Renewals;
        var rows = new List<CampaignStanding>();
        foreach (var campaign in gw.Campaigns.All().Where(c => c.IsOpen))
        {
            if (gw.Datasets.ById(campaign.HoldoutDatasetId) is not { } set
                || EvaluationClass.Or(set.EvaluationClass) != EvaluationClass.Research) continue;
            var spent = renewals.SpentOn(campaign);
            rows.Add(new CampaignStanding(set.Pair, campaign, gw.Campaigns.JudgementsSpent(campaign.Id),
                gw.Campaigns.TrialsCharged(campaign.Id), spent, renewals.Of(campaign.Id),
                spent is null ? "" : renewals.Next(campaign.Id), gw.Campaigns.HoldoutSpan(campaign.Id)));
        }
        return [.. rows.OrderBy(r => r.Pair, StringComparer.Ordinal).ThenBy(r => r.Campaign.Id)];
    }

    /// <summary>
    /// THE SETUP STEP'S ONE PRESS, OVER THE PAIRS THE OWNER LEFT TICKED (<c>U-evidence-breadth</c> item 1): each pair in turn —
    /// one already under a campaign over real history is left alone — gets the default download (its newest one when that already
    /// holds the owner's cutoff), its instrument check, and its own holdout from <see cref="EvidenceDefaults.Cutoff"/> — or from the
    /// cutoff it already has, which is never moved — with its OWN campaign opened with the owner's budgets, through
    /// <see cref="TradingGateway.SetHoldout"/>. Nothing is held back over a download that stopped part way: the pair is
    /// <see cref="EvidenceSetup.Unfinished"/> and the next press finishes it (<c>U-evidence-holdout-length</c>). Ok when a campaign over
    /// real history is open afterwards; one sentence per pair says what happened to it, a pair that failed included.
    /// </summary>
    public async Task<EvidenceSetup> SetUpAsync(DateTimeOffset now, IReadOnlyList<string> pairs,
        IProgress<ProvisionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var asked = pairs.Distinct(StringComparer.Ordinal).ToList();
        if (asked.Count == 0)
            return new EvidenceSetup(Ready(), "No pair was ticked, so nothing was downloaded or held back.", [], []);

        // ONE PRESS AT A TIME (the Codex review's P2): two presses overlapping would each download the same pair and each open a
        // campaign over its months — counted together, but two campaigns where one was asked for.
        if (Interlocked.Exchange(ref _settingUp, 1) == 1)
            return new EvidenceSetup(Ready(), "A setup press is already downloading and holding back, so nothing more was started.", [], []);
        try
        {
            var words = new List<string>();
            var opened = new List<CampaignRow>();
            var unfinished = new List<string>();
            foreach (var pair in asked)
            {
                ct.ThrowIfCancellationRequested();
                var (said, campaign, stopped) = await SetUpPairAsync(pair, now, progress, ct);
                words.Add(said);
                if (campaign is not null) opened.Add(campaign);
                if (stopped) unfinished.Add(pair);
            }
            return new EvidenceSetup(Ready(), string.Join(" ", words), opened, unfinished);
        }
        finally { Volatile.Write(ref _settingUp, 0); }
    }

    int _settingUp;

    /// <summary>
    /// One pair of <see cref="SetUpAsync"/>: its sentence, the campaign it opened or null, and whether its download stopped part way
    /// (so the next press finishes it).
    /// </summary>
    async Task<(string Words, CampaignRow? Campaign, bool Stopped)> SetUpPairAsync(string pair, DateTimeOffset now,
        IProgress<ProvisionProgress>? progress, CancellationToken ct)
    {
        var gw = gateway();
        if (!BinanceArchive.IsPair(pair))
            return ($"“{pair}” is not a pair as Binance writes one, so nothing was held back for it.", null, false);
        if (Pairs().Contains(pair, StringComparer.Ordinal))
            return ($"{pair}: a campaign is already open over real history, so nothing was changed.", null, false);

        // A DOWNLOAD THAT ALREADY HOLDS A CUTOFF IS THE OWNER'S, AND IS HELD BACK AS IT IS. Every other pair is downloaded now, by the
        // default press (U-evidence-holdout-length): a month this installation already keeps with exactly the bytes the vendor's
        // checksum names is not fetched again (`CandleSourceClient`), so a download that stopped part way is FINISHED by this press
        // rather than restarted, and one shorter than the default — an earlier press — is lengthened, before anything is held back.
        var newest = gw.Datasets.Newest(pair) is { } row ? gw.Datasets.Checked(row) : null;
        DatasetRecord set;
        if (newest is { State: DatasetState.ACCEPTED, HoldoutFrom: not null }) set = newest;
        else
        {
            var collected = await collect(pair, now, progress, ct);
            var missing = Unfetched(collected.Months);
            if (collected.Dataset is not { } fresh || fresh.State != DatasetState.ACCEPTED)
                return ($"{pair}: nothing was held back: {collected.Summary}" + (missing > 0 ? " Press again to ask once more." : ""),
                    null, missing > 0);

            // NEVER HALF-HELD: a month that did not arrive — the vendor unreachable, a checksum that did not match, no checksum to pin it
            // to, its name taken here — would leave the holdout short or holed, or the research months cut, and a cutoff never moves
            // once set. So nothing is held back over a download any month of which is still to come; a month the vendor answered it
            // does not publish is its answer, not a failure.
            if (missing > 0)
                return ($"{pair}: nothing was held back, because the download stopped part way — {collected.Summary} Press again: the "
                    + $"months already checked are kept and only the {missing:N0} that did not arrive are fetched.", null, true);
            set = fresh;
        }

        // THE VENUE'S OWN STEP FOR THIS PAIR, read before the holdout: the campaign pins its judge from it, and a press on a pair
        // whose step is unconfirmed is refused by SetHoldout in its own words, writing nothing.
        if (check is not null) await check(pair, ct);

        // A CUTOFF ALREADY THERE IS THE OWNER'S AND IS KEPT: moving it later with no campaign holding it would release months he
        // held back, and the store refuses earlier. The default is chosen only for a download that holds none.
        if ((set.HoldoutFrom ?? EvidenceDefaults.Cutoff(set)) is not { } cutoff)
            return ($"{pair}: nothing was held back: the download holds too few months to keep the last "
                + $"{EvidenceDefaults.HoldoutMonths} back and still leave a month to research.", null, false);

        // ASKED AGAIN AFTER THE DOWNLOAD AND THE CHECK: the owner's Settings press may have opened this pair's campaign meanwhile.
        if (Pairs().Contains(pair, StringComparer.Ordinal))
            return ($"{pair}: a campaign was opened over real history meanwhile, so nothing more was held back.", null, false);

        var (done, campaign) = gw.SetHoldout(set.Id, cutoff, EvaluationClass.Research);
        if (!done.Ok || campaign is null) return ($"{pair}: {done.Why}", null, false);

        return ($"{set.Pair} bars from {cutoff.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC on are "
            + "held back: the AI cannot read them or backtest over them, and this date can no longer be moved. "
            + CampaignStore.PressNote(campaign, gw.Campaigns.JudgementsSpent(campaign.Id), gw.Campaigns.OverTheSameMonths(campaign.Id)),
            campaign, false);
    }

    /// <summary>
    /// HOW MANY OF A DOWNLOAD'S MONTHS ARE STILL TO COME: every one that did not arrive for a reason other than the vendor answering that
    /// it does not publish it — which is the archive's answer, and pressing again would ask the same question to the same answer.
    /// </summary>
    static int Unfetched(IReadOnlyList<MonthResult> months) =>
        months.Count(m => m.Outcome is not (MonthOutcome.Collected or MonthOutcome.NotPublished));

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
