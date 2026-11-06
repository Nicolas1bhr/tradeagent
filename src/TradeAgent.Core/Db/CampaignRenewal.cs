using System.Globalization;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// ONE FRESH HOLDOUT THE APP ARMED WHEN A CAMPAIGN WAS SPENT (<c>U-evidence-campaign</c>; R25 D7): from <see cref="Cutoff"/> on,
/// the spent campaign's own pair (<see cref="Pair"/>, R30 D16) — its bars, its forward bars and its rows on the tape — is held back
/// from every caller on the agent channel, until the campaign that judges those months — <see cref="Successor"/> — opens over a download
/// holding at least <see cref="CampaignRenewal.MinimumFreshDays"/> days of them.
/// </summary>
public sealed record ForwardHoldout(long SpentCampaignId, DateTimeOffset Cutoff, DateTimeOffset ArmedAt, string Spent, long? Successor)
{
    /// <summary>
    /// THE ONE PAIR THIS WINDOW HOLDS, or null when it cannot be told: the spent campaign's dataset is gone, or the armed row cannot be
    /// read. Null holds EVERY pair, because an unknown pair is never another pair (<see cref="CampaignStore.SamePair"/>).
    /// </summary>
    public string? Pair { get; init; }

    /// <summary>Whether the window is still held open-ended: armed, and its campaign not yet open.</summary>
    public bool Holding => Successor is null;

    /// <summary>The earliest instant a download can hold the minimum: the cutoff plus the minimum span.</summary>
    public DateTimeOffset Earliest => Cutoff + CampaignRenewal.MinimumFresh;
}

/// <summary>
/// HOW A SPENT CAMPAIGN IS FOLLOWED, BY RULE AND UNATTENDED (<c>U-evidence-campaign</c> item 2; R25 D7, "campaigns renew only on data
/// no prior verdict or research could read").
///
/// <para><b>Why not <see cref="CampaignStore.Renew"/>.</b> A renewal carries the parent's holdout, and the parent's scoring policy
/// counts verdicts across the whole renewal lineage, so it buys research trials over months already judged and never a fresh
/// look. When a campaign's judgements are spent, the repo's own way on is "a holdout over OTHER months" (the spent-budget sentence,
/// <see cref="CampaignStore.ChargeVerdict"/>) — the owner's press on a fresh download, <c>TradingGateway.SetHoldout</c>. This class
/// is the rule that makes that press for him, and it makes it only on months no verdict and no research read could have seen:
/// FRESH FORWARD DATA. A renewal stays the owner's; nothing here calls it.</para>
///
/// <para><b>The rule, in four parts.</b></para>
/// <list type="number">
/// <item><b>Spent.</b> An open campaign over real history whose final judgements (<see cref="CampaignStore.JudgementsSpent"/>, every
/// judgement over its months) or whose research trials (<see cref="CampaignStore.TrialsCharged(long)"/>) have reached a budget
/// greater than zero, or whose exploration pot is full with no parent that can be promoted (<see cref="ExplorationSpent"/>).</item>
/// <item><b>Armed, once.</b> Its forward cutoff is the first UTC midnight STRICTLY AFTER the latest instant anything on record
/// reaches: now, the close of every dataset's last bar, the close of every forward bar, the end of every held window, and every
/// cutoff already armed. Nothing at or after it existed when it was armed, so nothing at or after it can have been served. It is
/// written once (<see cref="Database.AddKvOnce"/>, by the database and not by this code) and never moved: earlier would hold back
/// minutes already served, later would release minutes it held. One window is armed at a time FOR EACH PAIR, so each of a pair's
/// windows starts after the last (<c>U-evidence-breadth</c>: each pair's campaign is its own budget, so a spent BTCUSDT campaign
/// does not make a spent ETHUSDT campaign wait for BTCUSDT's fresh month first).</item>
/// <item><b>Held from that moment, on its own pair.</b> <see cref="TapeHoldout"/> holds <c>[cutoff, no end)</c> of the spent campaign's
/// pair for every pipe caller and the referee's research evidence (R30 D16: campaigns and renewal are per pair, and a window that held
/// every pair would take the recent data of all five from the first campaign spent). Correlated pairs therefore still leak a little
/// across a hold, and <c>docs/CONTRACTS.md</c> says so. NOT for the paper runner: it trades the present, which is what these minutes
/// are, and a paper run's figures were never held back (<c>docs/CONTRACTS.md</c>, The holdout, "NOT covered").</item>
/// <item><b>Opened by the app, on enough of it.</b> A fresh download of the spent campaign's own pair, source and interval, holding
/// no cutoff, with research months before the cutoff and at least <see cref="MinimumFreshDays"/> days of bars from it, while no
/// other held window OF THE SAME PAIR reaches past the cutoff: the app sets the cutoff on it through the owner's own press path, which opens the
/// next campaign with today's budgets and writes the successor here in the same transaction
/// (<c>TradingGateway.OpenForwardCampaign</c>). Until then every refusal says what it waits for, in days.</item>
/// </list>
///
/// <para><b>What keeps its keys.</b> Nothing is re-keyed, refunded or closed. The spent campaign stays open, so its cutoff never
/// moves later and its development months stay charged to it; a family whose root was charged under it is still charged there
/// (<see cref="CampaignStore.ChargedCampaignFor"/>), so a fresh holdout buys a known family no attempts its own campaign has spent.
/// The successor is a new lineage over new months: its judgements are counted over the months they read, which no earlier
/// judgement read, and its α-wealth runs over them from its first verdict, exactly as an owner's press on other months would.</para>
///
/// <para><b>No pipe op reaches any of it</b>, for the reason none reaches the campaign ledger: a caller that could arm or open a
/// campaign would have given itself an unlimited supply of looks.</para>
/// </summary>
public sealed class CampaignRenewal(Database db)
{
    /// <summary>
    /// THE LEAST FRESH MARKET TIME A SUCCESSOR OPENS ON, IN DAYS. Thirty, a choice and stated: long enough that a judgement reads a
    /// month of the market a version was never fitted to, short enough that a loop running for days is not stopped for a season.
    /// It is a floor, not the window: the window is everything the download holds from the cutoff on. Under a year a loss over it
    /// reads as inconclusive (<c>Inconclusive.ShortHoldout</c>), as any short holdout's does.
    /// </summary>
    public const int MinimumFreshDays = 30;

    /// <summary><see cref="MinimumFreshDays"/> as a span.</summary>
    public static readonly TimeSpan MinimumFresh = TimeSpan.FromDays(MinimumFreshDays);

    /// <summary>The kv key prefix of an armed window: the spent campaign's id follows. Written once.</summary>
    internal const string ArmedKey = "campaign_forward_holdout:";

    /// <summary>The kv key prefix of the campaign that opened over it: the spent campaign's id follows. Written once.</summary>
    internal const string SuccessorKey = "campaign_forward_successor:";

    /// <summary>What <see cref="SpentOn"/> answers for a campaign spent by its full exploration pot (<see cref="ExplorationSpent"/>).</summary>
    public const string ExplorationTrials = "exploration trials";

    /// <summary>
    /// WHAT THIS CAMPAIGN HAS SPENT, in words ("final judgements", "research trials", <see cref="ExplorationTrials"/>), or null while it
    /// has room, is closed, or was opened with no attempts of that kind at all (a zero budget is the owner's "none", not something to
    /// renew).
    /// </summary>
    public string? SpentOn(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        if (!campaign.IsOpen) return null;
        var campaigns = new CampaignStore(db);
        if (campaign.VerdictBudget > 0 && campaigns.JudgementsSpent(campaign.Id) >= campaign.VerdictBudget) return "final judgements";
        if (campaign.TrialBudget > 0 && campaigns.TrialsCharged(campaign.Id) >= campaign.TrialBudget) return "research trials";
        return ExplorationSpent(campaign) ? ExplorationTrials : null;
    }

    /// <summary>
    /// WHETHER THIS CAMPAIGN'S EXPLORATION POT HAS BEEN ITS LAST TRIAL (R30 D15; R28 E2): the pot is full and no parent can be promoted,
    /// so the refinement pot behind it can never be drawn on and every further trial is refused. Before this rule such a campaign sat
    /// open with no way on: <see cref="SpentOn"/> counted only the whole trial budget, which a campaign whose every trial is an
    /// exploration never reaches, so nothing ever armed its renewal. The budgets themselves do not change: not the trials, not the
    /// verdicts, not the pot. A campaign counted spent this way keeps any final judgements it has left.
    /// </summary>
    public bool ExplorationSpent(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return campaign.IsOpen && campaign.ExplorationBudget > 0
            && new CampaignStore(db).TrialsCharged(campaign.Id, exploration: true) >= campaign.ExplorationBudget
            && !ParentCanBePromoted(campaign);
    }

    /// <summary>
    /// WHETHER A TRIAL CAN STILL DECLARE A PROMOTED PARENT UNDER THIS CAMPAIGN, which is the only thing that draws on its refinement pot
    /// (<see cref="CampaignStore.IsExploration"/>). True in two cases:
    /// <list type="bullet">
    /// <item><b>A version stands Promoted.</b> Of any pair, as <see cref="CampaignStore.IsExploration"/> reads it: a child ported to this
    /// pair is a refinement charged here. Read through <see cref="Promotions.Standing"/>, so a promotion since invalidated is none.</item>
    /// <item><b>This campaign could still promote one.</b> It has a judgement left and holds a version of its pair frozen BEFORE its
    /// cutoff, not retired, bounded, and not yet judged under it. Only such a version can pass the policy's forward-evidence clause
    /// (<see cref="Strategy.ScoringPolicyV1.Reason"/>). On a campaign over history no version qualifies, so this is what makes a first campaign's
    /// pot its end; on one over fresh months, every version frozen before them does until it is judged.</item>
    /// </list>
    /// <para><b>A paper-eligible version is not a promotable parent</b> (R30 D15): it was measured on months that predate its freeze, and
    /// counting it would widen the search budget on an unproven basis. Once this answers false it stays false, because a version made
    /// later is frozen after the cutoff.</para>
    /// </summary>
    public bool ParentCanBePromoted(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return db.Read(_ =>
        {
            var promotions = new Promotions(db);
            if (promotions.PromotedVersions().Any(version => promotions.Standing(version).IsPromoted)) return true;

            var campaigns = new CampaignStore(db);
            if (campaigns.JudgementsSpent(campaign.Id) >= campaign.VerdictBudget) return false;
            if (new DatasetStore(db).ById(campaign.HoldoutDatasetId) is not { } set) return false;

            var retired = new Retirements(db);
            return new StrategyStore(db).VersionsFrozenBefore(campaign.HoldoutFrom).Any(version =>
                version.Freshness is not null
                && !campaigns.VerdictCharged(campaign.Id, version.Id)
                && retired.Refusal(version.Id) is null
                && Strategy.StrategyParser.Parse(version.Source).Program is { } program
                && Strategy.InstrumentMatch.Refusal(program, set) is null);
        });
    }

    /// <summary>
    /// WHAT EVERY TURN IS TOLD BEFORE IT ACTS (R30 D15, "turns must not be sold a refusal twice without learning it"): one line per open
    /// campaign over real history that has spent something, oldest pair first, each saying what is spent, what it can no longer be
    /// charged and when the next one can come. The refusal itself reaches only the turn that asked, and a session can be rotated away
    /// between turns, so without this every later turn pays again to learn the same sentence.
    /// </summary>
    public IReadOnlyList<string> SpentLines()
    {
        var campaigns = new CampaignStore(db);
        var datasets = new DatasetStore(db);
        var lines = new List<string>();
        foreach (var campaign in campaigns.All().Where(c => c.IsOpen).OrderBy(c => c.Id))
        {
            if (datasets.ById(campaign.HoldoutDatasetId) is not { } set
                || EvaluationClass.Or(set.EvaluationClass) != EvaluationClass.Research
                || SpentOn(campaign) is not { } spent) continue;

            var left = Math.Max(0, campaign.VerdictBudget - campaigns.JudgementsSpent(campaign.Id));
            var charged = spent switch
            {
                ExplorationTrials => "it takes no further run of a version without a promoted parent, and nothing can be promoted under it",
                "research trials" => "it takes no further research run",
                _ => "it takes no further verdict"
            };
            var kept = spent == "final judgements" || left == 0 ? "" : $", though {left} of its final judgements can still be asked for";
            var next = Of(campaign.Id) is { } held
                ? held.Successor is { } open ? $"Campaign {open} follows it over fresh months."
                  : $"The next {set.Pair} campaign opens no earlier than {held.Earliest:yyyy-MM-dd}."
                : $"The next {set.Pair} campaign is at least {MinimumFreshDays} days away.";
            lines.Add($"{set.Pair}: campaign {campaign.Id} has spent its {spent}: {charged}{kept}. {next}");
        }
        return lines;
    }

    /// <summary>
    /// ARMS THE FRESH HOLDOUT OF EVERY SPENT CAMPAIGN THAT HAS NONE, oldest first, one at a time FOR EACH PAIR: while one of a pair's
    /// windows is still held open-ended, no other campaign of that pair is armed, so its next starts after it. Answers what this call
    /// armed — nothing when nothing was spent, when every spent campaign's pair is still holding, or when nothing on record has a
    /// measurable end (<see cref="NextCutoff"/>).
    ///
    /// <para><b>Per pair, and why that keeps the rule</b> (<c>U-evidence-breadth</c>; R30 D16). Each cutoff is still after everything on
    /// record — every armed cutoff of every pair included — so nothing at or after it was served to anyone. What a window is FOR is one
    /// pair's next campaign, whose judgements count only that pair's (<see cref="CampaignStore.JudgementsSpent"/>), so it holds that
    /// pair and two pairs collecting at once share no budget. A spent campaign whose pair cannot be read (its dataset gone) holds every
    /// pair back: unknown is never "another pair".</para>
    /// </summary>
    public IReadOnlyList<ForwardHoldout> Arm(DateTimeOffset now) => db.Write(_ =>
    {
        var armed = new List<ForwardHoldout>();
        var datasets = new DatasetStore(db);
        var campaigns = new CampaignStore(db);

        // THE PAIRS STILL COLLECTING: one open-ended window each. A window whose campaign's pair is unknown holds them all.
        var collecting = Holding().Select(h => PairOf(campaigns, datasets, h.SpentCampaignId)).ToList();

        foreach (var campaign in campaigns.All().Where(c => c.IsOpen).OrderBy(c => c.Id))
        {
            if (datasets.ById(campaign.HoldoutDatasetId) is not { } set
                || EvaluationClass.Or(set.EvaluationClass) != EvaluationClass.Research) continue;
            if (collecting.Any(pair => CampaignStore.SamePair(pair, set.Pair))) continue;
            if (SpentOn(campaign) is not { } spent) continue;
            if (NextCutoff(now) is not { } cutoff) break;

            // THE DATABASE KEEPS IT WRITTEN ONCE. A second arming of the same campaign — a later pass, a restart, a race — writes
            // nothing, and the cutoff the first one recorded is the one every reader keeps holding from.
            if (!db.AddKvOnce(ArmedKey + Id(campaign.Id), $"{Sql.T(cutoff)}|{Sql.T(now)}|{spent}")) continue;

            armed.Add(new ForwardHoldout(campaign.Id, cutoff, now.ToUniversalTime(), spent, null) { Pair = set.Pair });
            collecting.Add(set.Pair);
        }
        return (IReadOnlyList<ForwardHoldout>)armed;
    });

    /// <summary>The pair of a campaign's dataset, or null when the campaign or its dataset is not in this ledger.</summary>
    static string? PairOf(CampaignStore campaigns, DatasetStore datasets, long campaignId) =>
        campaigns.ById(campaignId) is { } campaign ? datasets.ById(campaign.HoldoutDatasetId)?.Pair : null;

    /// <summary>
    /// THE FIRST UTC MIDNIGHT STRICTLY AFTER EVERYTHING ON RECORD — now, every dataset's last bar's close, every forward bar's close,
    /// every held window's end and every cutoff already armed — or null when something on record has no end this build can measure
    /// (a held window with no end, a dataset whose bar length it cannot read): then nothing can be shown to be later, and nothing is
    /// armed on a guess.
    /// </summary>
    public DateTimeOffset? NextCutoff(DateTimeOffset now)
    {
        var latest = now.ToUniversalTime();
        var datasets = new DatasetStore(db);
        var all = datasets.All();

        foreach (var set in all)
        {
            if (set.LastBar is not { } last) continue;
            if (Close(set.Interval, last) is not { } close) return null;
            if (close > latest) latest = close;
        }
        foreach (var window in TapeHoldout.WindowsOf(all))
        {
            if (window.Until is not { } end) return null;
            if (end > latest) latest = end;
        }
        foreach (var series in new ForwardBarStore(db).All())
            if (series.LastBar is { } open && open + ForwardBars.BarLength > latest) latest = open + ForwardBars.BarLength;
        foreach (var held in All())
            if (held.Cutoff > latest) latest = held.Cutoff;

        var day = latest.UtcDateTime.Date;
        return new DateTimeOffset(day, TimeSpan.Zero).AddDays(1);
    }

    /// <summary>The fresh holdout armed for this campaign, or null.</summary>
    public ForwardHoldout? Of(long spentCampaignId) =>
        db.GetKv(ArmedKey + Id(spentCampaignId)) is { } value ? Parse(spentCampaignId, value) : null;

    /// <summary>Every fresh holdout ever armed, oldest campaign first.</summary>
    public IReadOnlyList<ForwardHoldout> All() =>
        [.. db.KvStartingWith(ArmedKey)
            .Select(kv => long.TryParse(kv.Key[ArmedKey.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? Parse(id, kv.Value) : null)
            .OfType<ForwardHoldout>()
            .OrderBy(h => h.SpentCampaignId)];

    /// <summary>The fresh holdouts still held open-ended: armed, and no campaign open over them yet.</summary>
    public IReadOnlyList<ForwardHoldout> Holding() => [.. All().Where(h => h.Holding)];

    ForwardHoldout? Parse(long spentCampaignId, string value)
    {
        // A ROW THIS BUILD CANNOT READ IS HELD FROM THE BEGINNING OF TIME, never skipped: an armed window that silently stopped
        // holding would serve the minutes it was armed to keep. Its successor cannot open (no cutoff to open on).
        var parts = value.Split('|');
        var cutoff = parts.Length == 3 && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var c) ? c : DateTimeOffset.MinValue;
        var armedAt = parts.Length == 3 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var a) ? a : DateTimeOffset.MinValue;
        var successor = db.GetKv(SuccessorKey + Id(spentCampaignId)) is { } s
            && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var next) ? next : (long?)null;

        // ITS PAIR, READ THROUGH THE CAMPAIGN'S DATASET; A ROW THAT CANNOT BE READ HOLDS EVERY PAIR, as it holds every instant.
        var pair = cutoff == DateTimeOffset.MinValue
            ? null
            : PairOf(new CampaignStore(db), new DatasetStore(db), spentCampaignId);
        return new ForwardHoldout(spentCampaignId, cutoff, armedAt, parts.Length == 3 ? parts[2] : "an unreadable record", successor) { Pair = pair };
    }

    /// <summary>
    /// WHY <paramref name="fresh"/> MAY NOT BE THE HOLDOUT OF THE CAMPAIGN THAT FOLLOWS <paramref name="spentCampaignId"/>, in words,
    /// or null because it may. Read inside the opening's own write (<c>TradingGateway.OpenForwardCampaign</c>).
    /// </summary>
    public string? OpenRefusal(long spentCampaignId, DatasetRecord? fresh) => db.Read(_ =>
    {
        if (Of(spentCampaignId) is not { } held)
            return $"campaign {spentCampaignId} has no fresh holdout armed, so there are no months for a campaign after it to be "
                   + "about: one is armed only when a campaign's judgements or trials are spent.";
        if (held.Successor is { } open)
            return $"campaign {spentCampaignId} is already followed by campaign {open}, opened over its fresh months; it is "
                   + "followed once.";
        if (held.Cutoff == DateTimeOffset.MinValue)
            return $"the fresh holdout recorded for campaign {spentCampaignId} cannot be read by this build, so no campaign is "
                   + "opened over it and every bar stays held back.";

        if (fresh is null) return "there is no fresh download to hold back. " + Waiting(held, null);
        var spent = new CampaignStore(db).ById(spentCampaignId);
        var parent = spent is null ? null : new DatasetStore(db).ById(spent.HoldoutDatasetId);
        if (spent is null || parent is null)
            return $"campaign {spentCampaignId} or its dataset is not in this installation's ledger.";

        var named = $"dataset {fresh.Id} ({fresh.Pair} {fresh.Interval} {fresh.Version})";
        if (fresh.HoldoutFrom is { } cut)
            return $"{named} already holds back every bar from {cut:u}; a fresh holdout is set on a download that holds none.";
        if (fresh.State != DatasetState.ACCEPTED)
            return $"{named} is {fresh.State}: its files no longer hash to what the ledger recorded, so it judges nothing.";
        if (!string.Equals(fresh.Source, parent.Source, StringComparison.Ordinal)
            || !string.Equals(fresh.Pair, parent.Pair, StringComparison.Ordinal)
            || !string.Equals(fresh.Interval, parent.Interval, StringComparison.Ordinal))
            return $"{named} is not the bars campaign {spentCampaignId} judged ({parent.Source} {parent.Pair} {parent.Interval}); "
                   + "the campaign after it is held over a fresh download of the same series.";
        if (fresh.FirstBar is not { } first || first >= held.Cutoff)
            return $"{named} holds no bar before {held.Cutoff:u}, so there would be no research months for anyone to run a trial "
                   + "over before asking for a judgement.";
        if (fresh.LastBar is not { } last || Close(fresh.Interval, last) is not { } close || close - held.Cutoff < MinimumFresh)
            return Waiting(held, fresh);

        // NOTHING ELSE OF THE PAIR HELD REACHES PAST THE CUTOFF: months another campaign of this pair holds are months its judgements
        // read, and they count against this pair's next campaign (`CampaignStore.JudgementsSpent`). Another pair's held months are its
        // own campaign's budget (U-evidence-breadth).
        foreach (var window in TapeHoldout.WindowsOf(new DatasetStore(db).All()
                     .Where(s => s.Id != fresh.Id && CampaignStore.SamePair(s.Pair, fresh.Pair))))
            if (window.Until is not { } end || end > held.Cutoff)
                return $"{window.Words}, which reaches past {held.Cutoff:u}: those months are held for that dataset's campaign, so "
                       + "they are not fresh, and no campaign is opened over them.";

        return null;
    });

    /// <summary>
    /// Records the campaign that opened over this fresh holdout. Written once; false when one is already recorded. Its one caller is
    /// <c>TradingGateway.OpenForwardCampaign</c>, inside the write that opened the campaign.
    /// </summary>
    public bool RecordSuccessor(long spentCampaignId, long successorId) =>
        db.AddKvOnce(SuccessorKey + Id(spentCampaignId), Id(successorId));

    /// <summary>
    /// WHAT A SPENT CAMPAIGN WAITS FOR, in the words a refusal carries: the cutoff, the minimum, the earliest date, and how much a
    /// download already holds.
    /// </summary>
    public static string Waiting(ForwardHoldout held, DatasetRecord? newest)
    {
        ArgumentNullException.ThrowIfNull(held);
        var have = newest?.LastBar is { } last && Close(newest.Interval, last) is { } close && close > held.Cutoff
            ? (close - held.Cutoff).TotalDays
            : 0;
        return $"TradeAgent opens the next campaign by itself, and only on FRESH market data no verdict and no research read could "
               + $"have seen: every bar of {held.Pair ?? "every pair"} from {held.Cutoff:u} on, which no caller on this channel is served "
               + "until then. It waits for at "
               + $"least {MinimumFreshDays} days of those bars in a download — {have.ToString("0.#", CultureInfo.InvariantCulture)} of "
               + $"{MinimumFreshDays} so far — so the earliest it can open is {held.Earliest:u}; Binance publishes a month once the "
               + "month is complete.";
    }

    /// <summary>
    /// THE WAY ON FROM A SPENT CAMPAIGN, for every sentence that refuses a judgement or a trial on one: its successor when one is
    /// open, what its fresh holdout waits for when one is armed, and otherwise when one will be.
    /// </summary>
    public string Next(long campaignId)
    {
        if (Of(campaignId) is { } held)
            return held.Successor is { } open && new CampaignStore(db).ById(open) is { } next
                ? $"Campaign {open} has opened after it over fresh months from {held.Cutoff:u} — dataset {next.HoldoutDatasetId}: run "
                  + "trials over that dataset's months before the cutoff and ask for judgements there."
                : Waiting(held, null);

        var campaigns = new CampaignStore(db);
        var datasets = new DatasetStore(db);
        var pair = PairOf(campaigns, datasets, campaignId);
        return Holding().FirstOrDefault(h => CampaignStore.SamePair(PairOf(campaigns, datasets, h.SpentCampaignId), pair)) is { } other
            ? $"TradeAgent arms this campaign's fresh holdout once the one it is collecting for campaign {other.SpentCampaignId} has "
              + $"opened — no earlier than {other.Earliest:u} — so that each fresh window of {pair ?? "its pair"} starts after the last."
            : "TradeAgent arms its fresh holdout within minutes: the next campaign opens on market data from the next UTC midnight "
              + $"on, which nothing has seen, once a download holds at least {MinimumFreshDays} days of it.";
    }

    /// <summary>The close of a bar opening at <paramref name="open"/>, or null for an interval this build cannot read.</summary>
    static DateTimeOffset? Close(string interval, DateTimeOffset open)
    {
        try
        {
            var bar = KlineNormaliser.BarLength(interval);
            return open <= DateTimeOffset.MaxValue - bar ? open.ToUniversalTime() + bar : null;
        }
        catch (TradeAgentException) { return null; }
    }

    static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
