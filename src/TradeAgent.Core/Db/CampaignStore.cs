using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE SCORING POLICY, FIXED BEFORE A CAMPAIGN AND HASHED INTO IT.
///
/// <para><c>docs/COUNCIL.md</c>:134: "the scoring policy fixed before a campaign". The point is
/// PRECOMMITMENT — that the criteria were settled before the outcome was known (:212) — so the text is
/// copied onto the campaign row at open along with its SHA-256, and neither is ever updated. A policy
/// read from a constant at judging time would be a policy that could change between the hypothesis and
/// the verdict, which is the whole thing this is here to prevent.</para>
///
/// <para><b>It is the app's text and no agent writes it.</b> There is no op and no verb that sets a
/// scoring policy: it is not a setting, because a number the owner can nudge is a criterion and a
/// criterion is what is being precommitted. A different policy needs a new holdout and therefore a new
/// campaign — a RENEWAL deliberately carries the parent's text unchanged, so more attempts can never
/// come with an easier standard.</para>
/// </summary>
public static class CampaignPolicy
{
    /// <summary>
    /// Version 1, in the words a person reads. It describes what the app already enforces plus what
    /// `U-referee-2` will compute; it is a statement of the standard, not code, and the code that
    /// applies it is bound to this text by the sha on the campaign row.
    /// </summary>
    public const string V1 =
        "Scoring policy v1. A version is judged on evidence the app computed from its own trace, never "
        + "on a figure an agent reported. Research runs are made over bars before the campaign's holdout "
        + "cutoff and every one of them is charged against the campaign's trial budget, whatever role or "
        + "attempt asked for it. A final verdict is computed over the holdout bars themselves, by "
        + "TradeAgent alone, and is charged against the campaign's verdict budget across the whole "
        + "renewal lineage, because every verdict tells the research process something about data it was "
        + "never shown. A run over a fixture dataset establishes plumbing only: it is charged nothing and "
        + "is never evidence. Evidence is bound to the version's own hash, the dataset and its normalised "
        + "hash, the declared execution model, the interpreter build and this policy's hash; a change to "
        + "any of them invalidates the evidence that rested on it. A metric over bars establishes no "
        + "fill, no queue position and no intrabar ordering, so promotion on backtest evidence alone is "
        + "never enough — forward evidence collected after the version was frozen is required before "
        + "capital.";

    /// <summary>
    /// THE PAPER POLICY, A SECOND STANDARD WITH ITS OWN TEXT AND ITS OWN SHA — and it can confer
    /// eligibility for PAPER OBSERVATION and nothing else.
    ///
    /// <para><c>docs/PRINCIPLES.md</c> § Evidence asks for four distinct meanings — "acceptance of a
    /// program, a favourable historical verdict, eligibility for paper observation, and eligibility for
    /// live capital" — and then for the clause that makes a paper loop possible at all: "forward paper
    /// evidence cannot be required before the very first paper run that produces it". Under
    /// <see cref="V1"/> alone a version frozen after the cutoff can only ever be told its evidence
    /// predates the freeze, so the very first paper run is unreachable.</para>
    ///
    /// <para><b>It is V1's performance clauses and NOT its forward-evidence clause.</b> Identical
    /// arithmetic over the identical run — completed, at least one closed trade, net above zero after
    /// declared costs — asked ONLY of a version <see cref="V1"/> has already refused on the date. It
    /// is a SEPARATE text with a separate hash rather than a relaxation of V1, because a campaign
    /// precommitted to V1 and a standard that could be softened under the sha it fixed would hollow
    /// that out silently (<c>docs/COUNCIL.md</c>:212).</para>
    ///
    /// <para><b>It is the app's constant and no agent writes it</b>, exactly as <see cref="V1"/> is.
    /// A campaign copies it at open and a renewal carries the parent's, so more attempts never come
    /// with a different paper standard either.</para>
    /// </summary>
    public const string PaperV1 =
        "Paper policy v1 — historical holdout evidence: eligible for paper observation only. A version "
        + "is judged on evidence the app computed from its own trace, never on a figure an agent "
        + "reported. The clauses are the performance clauses of scoring policy v1 and nothing else: the "
        + "holdout run must have completed, it must have closed at least one trade, and its net after "
        + "its declared costs must be above zero. Scoring policy v1's forward-evidence clause is "
        + "deliberately absent here, because this standard is asked only of a version whose held-back "
        + "window does not post-date its own freeze — a result over months the submission may already "
        + "have been written around, which is why what this policy can confer is eligibility for paper "
        + "observation and nothing else. It is never a promotion, it allocates no capital, and forward "
        + "evidence collected after the version was frozen is still required before the account owner's "
        + "money. Evidence is bound to the version's own hash, the dataset and its normalised hash, the "
        + "declared execution model, the interpreter build and this policy's hash; a change to any of "
        + "them invalidates the evidence that rested on it.";

    /// <summary>The SHA-256 of <see cref="V1"/>, which is what a campaign row records beside the text.</summary>
    public static string Sha256Of(string text) => Sha256Hex.Of(text);
}

/// <summary>
/// ONE RESEARCH CAMPAIGN: a holdout, a standard fixed before the work, and a finite number of attempts.
///
/// <para><see cref="HoldoutFrom"/> is what was private WHEN THIS CAMPAIGN OPENED, and it is a record
/// rather than an enforcement point — the bars themselves are governed by <c>dataset.holdout_from</c>,
/// read on every access. It is on the row so that a renewal can be checked to carry the PARENT's
/// holdout rather than a fresh one (<c>docs/COUNCIL.md</c>:132, "campaign renewal authorised by code so
/// no new campaign resets holdout access").</para>
///
/// <para><see cref="RenewedFrom"/> is the whole of that check. A renewal that minted a fresh id with
/// nothing pointing back would be a campaign whose verdict budget started untouched over a holdout that
/// has already been read — the leak, with a new number on it.</para>
/// </summary>
public sealed record CampaignRow(
    long Id,
    string Name,
    string ScoringPolicy,
    string ScoringPolicySha256,
    int TrialBudget,
    int VerdictBudget,
    long HoldoutDatasetId,
    DateTimeOffset HoldoutFrom,
    DateTimeOffset OpenedAt,
    long? RenewedFrom,
    DateTimeOffset? ClosedAt)
{
    public bool IsOpen => ClosedAt is null;

    /// <summary>
    /// THE PART OF <see cref="TrialBudget"/> RESERVED FOR EXPLORATION, fixed at open like every other
    /// number on this row.
    ///
    /// <para><c>docs/COUNCIL.md</c>:220 asks for "exploration and diversity budgets" beside comparable
    /// opportunity. A trial is an EXPLORATION trial when the version it registers has no declared parent
    /// or has one nothing has promoted: the research process is trying something rather than refining
    /// something that has already been judged worth capital. Without a reserve those trials are simply
    /// trials, and a campaign can spend its entire allowance on them — or, once parentage exists, on
    /// refinements of one promoted program, and never look anywhere else.</para>
    ///
    /// <para><b>Two pots that sum to the budget.</b> Exploration is bounded by this number and
    /// refinement by <see cref="RefinementBudget"/>, which is the rest. So a declared parent moves a
    /// trial from one pot to the other and creates no allowance: a submitter cannot widen a campaign by
    /// claiming an ancestry, whatever it claims.</para>
    ///
    /// <para>A campaign opened with no reserve declared carries its whole trial budget here, which is
    /// what every campaign written before schema 21 genuinely had — no version had a parent, so every
    /// trial any of them registered was exploration.</para>
    /// </summary>
    public int ExplorationBudget { get; init; }

    /// <summary>What is left for versions whose declared parent is promoted. Never negative.</summary>
    public int RefinementBudget => Math.Max(0, TrialBudget - ExplorationBudget);

    /// <summary>
    /// THE SECOND STANDARD THIS CAMPAIGN FIXED AT OPEN — <see cref="CampaignPolicy.PaperV1"/>, copied
    /// onto the row beside <see cref="ScoringPolicy"/> and never updated, for the same reason that one
    /// is: a policy read from a constant at judging time could change between the hypothesis and the
    /// verdict.
    ///
    /// <para>Schema 23. Every campaign written before that rung was pinned to the build's own
    /// <see cref="CampaignPolicy.PaperV1"/> by the migration — the one backfill that rung makes — which
    /// is the truth about those rows: there was no second standard for any of them to have fixed, and
    /// leaving it empty would refuse a paper verdict on every installation that upgrades.</para>
    /// </summary>
    public string PaperPolicy { get; init; } = "";

    /// <summary>The SHA-256 of <see cref="PaperPolicy"/>, which is what the referee checks against.</summary>
    public string PaperPolicySha256 { get; init; } = "";

    /// <summary>
    /// THE COST MODEL THIS CAMPAIGN'S VERDICTS ARE JUDGED UNDER — a <c>Strategy.VenueCostModel</c> text,
    /// pinned when the campaign opened and never updated — or null, because the campaign was opened
    /// before schema 27 and has not been asked for a verdict since.
    ///
    /// <para>Pinned for the reason the two policies beside it are: a standard read from a table or a
    /// setting at judging time could move between the hypothesis and the verdict. It comes from the
    /// DATASET's recorded venue at the owner's press (<c>TradingGateway.SetHoldout</c>), so there is no
    /// picker and nothing the judged party chose; a renewal carries it, so more attempts never come
    /// with cheaper friction.</para>
    ///
    /// <para>Null is read by <c>Referee.RequestVerdict</c>, in the charge's own transaction: a lineage
    /// that already charged a verdict keeps the frictionless judge every such verdict was taken under,
    /// and one that never did is pinned from its dataset's venue there and then.</para>
    /// </summary>
    public string? CostModelCanonical { get; init; }

    /// <summary>The SHA-256 of <see cref="CostModelCanonical"/>, or null beside a null text.</summary>
    public string? CostModelSha256 { get; init; }
}

/// <summary>What opening or renewing a campaign did, or why it did nothing. A value, not an exception.</summary>
public sealed record CampaignOpened(bool Ok, string Why, CampaignRow? Campaign)
{
    internal static CampaignOpened Yes(CampaignRow row) => new(true, "", row);
    internal static CampaignOpened No(string why) => new(false, why, null);
}

/// <summary>
/// ONE REGISTERED RESEARCH RUN, CHARGED AGAINST ITS CAMPAIGN.
///
/// <para>There is no role and no attempt on this record, and their absence is the design: a trial keyed
/// by the attempt would make a restart a fresh budget, and one keyed by the role would let a replacement
/// team start again — <c>docs/COUNCIL.md</c>:131 asks for a budget "that survives team replacement". Who
/// asked is on the run row (<see cref="StrategyRunRow.Role"/>), where it belongs; what it COST is here,
/// keyed by what was asked.</para>
///
/// <para><see cref="Kind"/> is the dataset's evaluation class as it stood when the run was registered,
/// copied rather than joined, so a dataset reclassified later cannot rewrite what past runs cost.
/// <see cref="Charged"/> is the arithmetic that follows from it, written down so a reader of one row need
/// not know the rule.</para>
/// </summary>
public sealed record TrialRow(
    long CampaignId,
    string VersionId,
    string RunId,
    string Kind,
    bool Charged,
    DateTimeOffset RegisteredAt)
{
    /// <summary>
    /// THE CAMPAIGN WHOSE BUDGET THIS TRIAL WAS CHARGED AGAINST, which is not always the campaign the
    /// run was made under.
    ///
    /// <para><see cref="CampaignId"/> is the PEEK: which campaign's development months this run read — through
    /// its own dataset or, since <c>U-holdout-campaign</c>, through any dataset of any pair. This is the COST: which
    /// family of versions paid for it, the campaign lineage the family — the version and every parent it declares —
    /// was first charged to. They are the same number for the first trial of a family, and they differ for a
    /// variant — <c>docs/COUNCIL.md</c>:201's "comparable trials" — and for any later run of a family charged under
    /// another campaign, a run charged through several campaigns included. A child is charged to the campaign
    /// lineage its ancestry was already being charged to, so submitting a variant against a fresh holdout cannot
    /// buy a family an untouched trial budget by being a new hash.</para>
    ///
    /// <para>Null on a row written before schema 21 only until the rung backfills it to
    /// <see cref="CampaignId"/> — which is what every trial registered before this unit genuinely was:
    /// there was no ancestry to charge one anywhere else.</para>
    /// </summary>
    public long? ChargedTo { get; init; }

    /// <summary>
    /// WHETHER THIS TRIAL CAME OUT OF THE CAMPAIGN'S EXPLORATION RESERVE, as the question stood WHEN IT
    /// WAS REGISTERED.
    ///
    /// <para>Copied onto the row rather than worked out from the version's ancestry at read time, for
    /// the reason <see cref="Kind"/> is copied: a parent promoted next month must not reclassify what a
    /// trial made last month cost. A count that changed under a promotion would be a budget the process
    /// being measured can move.</para>
    /// </summary>
    public bool Exploration { get; init; }
}

/// <summary>What registering a trial did: whether it was charged, and where the campaign now stands.</summary>
public sealed record TrialRegistered(bool Ok, string Why, bool Charged, int Spent, int Budget);

/// <summary>
/// THE RULE A TRIAL IS ADMITTED AGAINST, built once and asked by BOTH the cheap look before a run and
/// the transaction that writes the row — the shape <c>AiAdmissionRule</c> has, for the same reason it
/// has it: two comparisons that could drift apart would be two budgets.
///
/// <para>It carries everything the arithmetic needs EXCEPT the counts, and that omission is the whole
/// design. The counts are the one part another caller can change between a look and a commit, so they
/// are read where nothing can move them — inside <see cref="CampaignStore.RegisterTrial"/>'s own
/// <see cref="Database.Write"/>.</para>
/// </summary>
public sealed record TrialAdmission
{
    /// <summary>The campaign the RUN is made under: whose held-back months this is a peek before.</summary>
    public required CampaignRow? RunCampaign { get; init; }

    /// <summary>
    /// The campaign this version's FAMILY is charged to — <see cref="CampaignStore.ChargedCampaignFor"/>.
    /// The same row as <see cref="RunCampaign"/> for a family's first trial, and for every later trial of a
    /// family under the campaign lineage it was first charged to.
    /// </summary>
    public required CampaignRow? HomeCampaign { get; init; }

    /// <summary>A fixture run is charged nothing, so no budget has anything to refuse.</summary>
    public required bool Charged { get; init; }

    /// <summary>
    /// Whether this trial comes out of the exploration reserve: the version declared no parent, or it
    /// declared one that nothing has promoted. See <see cref="CampaignRow.ExplorationBudget"/>.
    /// </summary>
    public required bool Exploration { get; init; }

    /// <summary>Whether the two campaigns above are one row, which is the ordinary case.</summary>
    public bool OneCampaign => RunCampaign?.Id == HomeCampaign?.Id;

    /// <summary>The pot this trial comes out of, on whichever campaign is being asked about.</summary>
    public int PotOf(CampaignRow campaign) =>
        Exploration ? campaign.ExplorationBudget : campaign.RefinementBudget;
}

/// <summary>
/// THE COUNTS A <see cref="TrialAdmission"/> IS DECIDED ON — read INSIDE the transaction that writes,
/// never carried into it. See <c>AiAdmissionRule</c>, which omits the day's totals for the same reason.
/// </summary>
public sealed record TrialCounts(int Run, int RunPot, int Home, int HomePot);

/// <summary>
/// ONE OPEN CAMPAIGN A RESEARCH RUN IS CHARGED TO — <see cref="CampaignStore.ChargedBy"/>'s answer, one per campaign.
///
/// <para><see cref="Through"/> is null for the campaign over the run's OWN dataset, which is charged as it always was. For
/// every other it is the sentence a refusal adds: the dataset the run read, the market time its bars span, and the
/// campaign's development months that span overlaps — because "campaign 4 has spent its trials" is a mystery to a caller
/// that asked about dataset 7 until it is told why campaign 4 is in the question at all.</para>
/// </summary>
public sealed record CampaignCharge(CampaignRow Campaign, string? Through);

/// <summary>
/// ONE VERDICT THAT WAS ASKED FOR. The row exists because the question was put, not because it was
/// answered: <c>docs/COUNCIL.md</c>:134 makes final evaluation scarce "because every verdict leaks", and a
/// budget checked after the holdout run has already let the leak happen.
///
/// <para><see cref="HoldoutFrom"/> is the cutoff as it stood when the verdict was charged — the record of
/// what was private when the answer was taken. The dataset's own cutoff does not move while the campaign
/// judges from it, in either direction (<c>DatasetStore.SetHoldout</c>, <c>U-holdout-later</c>).</para>
///
/// <para>The verdict's OUTCOME is not here. The holdout run, the promotion record and the delivery are
/// <c>U-referee-2</c>; this is the budget and the precommitment, which had to exist first because they are
/// the half that cannot be added afterwards.</para>
/// </summary>
public sealed record VerdictRow(
    long CampaignId,
    string VersionId,
    DateTimeOffset RequestedAt,
    DateTimeOffset HoldoutFrom);

/// <summary>
/// What charging a verdict did, and where the campaign now stands: <see cref="Spent"/> is
/// <see cref="CampaignStore.JudgementsSpent"/> — every judgement over the months it holds, its lineage's and every other
/// campaign's alike.
/// </summary>
public sealed record VerdictCharged(bool Ok, string Why, int Spent, int Budget);

/// <summary>
/// ANOTHER CAMPAIGN WHOSE JUDGEMENTS READ THE SAME HELD MONTHS AS ONE CAMPAIGN'S — not of its renewal lineage — with its
/// dataset as the ledger holds it now (null when it holds none), and the judgements it has taken over those months
/// (<c>U-holdout-campaign</c>, rule 2). What the owner's press and a spent judgement budget name.
/// </summary>
public sealed record CampaignOverMonths(CampaignRow Campaign, DatasetRecord? Dataset, int Judgements);

/// <summary>
/// THE CAMPAIGN LEDGER — the referee's protocol, and the app is the only writer.
///
/// <para><b>A campaign is opened when the owner sets a holdout, and there is at most ONE open campaign
/// per holdout dataset.</b> That is a partial unique index rather than a check in this code, so two
/// presses racing cannot make two. Renewal is the only way a second campaign appears over the same
/// dataset: <see cref="Renew"/> closes the parent and opens a child in ONE transaction, carrying the
/// parent's holdout, the parent's scoring policy text and sha, and <c>renewed_from</c>.</para>
///
/// <para><b>No pipe op reaches any of this.</b> There is no op that opens, renews, closes or re-budgets
/// a campaign, for the reason the dataset ledger has none: it is the record of the standard the caller
/// is being judged against, and a caller that could open a fresh campaign would have given itself an
/// unlimited supply of attempts.</para>
/// </summary>
public sealed class CampaignStore(Database db)
{
    const string Cols =
        "id, name, scoring_policy, scoring_policy_sha256, trial_budget, verdict_budget, " +
        "holdout_dataset_id, holdout_from, opened_at, renewed_from, closed_at, " +
        // LAST, so every positional read above it keeps its index. See `CampaignRow.ExplorationBudget`.
        "exploration_budget, paper_policy, paper_policy_sha256, " +
        // AND THE JUDGE'S COST MODEL AFTER THEM, for the same reason. See `CampaignRow.CostModelCanonical`.
        "cost_model_canonical, cost_model_sha";

    /// <summary>
    /// Opens the campaign for a dataset the owner has just held back, or refuses in words.
    ///
    /// <para>The budgets come from the caller — the app reads them off the owner's settings — and are
    /// COPIED onto the row, so a campaign's allowance is what it was opened with and not whatever the
    /// settings say today. A setting changed half way through a campaign would otherwise move the
    /// standard under evidence already collected, which is the same defect as an editable policy.</para>
    ///
    /// <para><b>The judge's cost model is pinned here too</b>, in this transaction, from the DATASET's
    /// recorded venue — <c>Strategy.VenueCostModel.For</c> — with <paramref name="judgeCapital"/> as its
    /// capital (the owner's setting; the shipped ten thousand when none is passed). A dataset that
    /// records no venue pins the labelled frictionless judge; one whose step nobody confirmed REFUSES
    /// the open in words, because a campaign whose every verdict would have to guess the step is a
    /// campaign that can only judge strategies nobody submitted.</para>
    /// </summary>
    public CampaignOpened Open(string name, DatasetRecord holdout, int trialBudget, int verdictBudget,
        DateTimeOffset at, string? policy = null, int? exploration = null, decimal? judgeCapital = null) =>
        db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(holdout);

        if (holdout.HoldoutFrom is not { } cutoff)
            return CampaignOpened.No(
                $"dataset {holdout.Id} holds nothing back, so there is nothing for a campaign to be about. "
                + "A campaign is opened when the account owner sets a holdout cutoff.");

        if (OpenForDataset(holdout.Id) is { } already)
            return CampaignOpened.No(
                $"campaign {already.Id} is already open over dataset {holdout.Id}. There is one campaign per "
                + "holdout dataset: renew that one, which carries its holdout and its trial history forward.");

        var judge = Strategy.VenueCostModel.For(holdout, new VenueStore(db, () => at),
            judgeCapital ?? Strategy.VenueCostModel.DefaultCapital);
        if (judge.Model is not { } costModel) return CampaignOpened.No(judge.Why);

        var text = policy ?? CampaignPolicy.V1;
        return CampaignOpened.Yes(Insert(new CampaignRow(
            0, name, text, CampaignPolicy.Sha256Of(text), Budget(trialBudget), Budget(verdictBudget),
            holdout.Id, cutoff, at, null, null)
        {
            ExplorationBudget = Reserve(exploration, Budget(trialBudget)),

            // THE PAPER STANDARD IS COPIED AT OPEN TOO, and it is the APP's constant rather than the
            // `policy` parameter above: `policy` exists so a test can prove the referee refuses a
            // campaign whose scoring policy this build does not implement, and a caller that could
            // also choose the paper standard would be a caller that could choose how little a paper
            // verdict has to prove.
            PaperPolicy = CampaignPolicy.PaperV1,
            PaperPolicySha256 = CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1),

            // AND THE FRICTION THE VERDICTS WILL BE SCORED UNDER — the dataset's venue's, never the
            // submitter's and never a later setting's.
            CostModelCanonical = costModel.Canonical,
            CostModelSha256 = costModel.Sha256
        }));
    });

    /// <summary>
    /// The exploration reserve a campaign opens with: what was declared, never more than the trial
    /// budget it is carved out of and never less than nothing.
    ///
    /// <para>NULL is "no reserve declared", and it records the WHOLE trial budget — which is what a
    /// campaign with no reserve genuinely has, and what every campaign written before schema 21 had:
    /// no version could declare a parent, so every trial any of them registered was exploration. It is
    /// not a licence: a reserve equal to the budget leaves nothing for refinement, which is exactly the
    /// state those campaigns were in.</para>
    /// </summary>
    static int Reserve(int? declared, int trialBudget) =>
        declared is not { } n ? trialBudget : Math.Clamp(n, 0, trialBudget);

    /// <summary>
    /// RENEWS A CAMPAIGN: the parent closes, a child opens with fresh ATTEMPTS and nothing else fresh.
    ///
    /// <para>The child carries the parent's holdout dataset, the parent's cutoff, the parent's scoring
    /// policy text and its sha, the parent's pinned cost model, and <c>renewed_from</c>. So renewal buys
    /// trials and nothing else — not a new standard, not cheaper friction, not a different holdout, and
    /// not untouched holdout access, because <c>Referee.RequestVerdict</c> counts verdicts across the
    /// whole lineage.</para>
    ///
    /// <para>It is BY CODE, as <c>docs/COUNCIL.md</c>:132 requires: there is no pipe op behind it. The
    /// only caller in this build is the owner's own window; a scheduled renewal is a later unit's, and it
    /// will call this same method.</para>
    /// </summary>
    public CampaignOpened Renew(long parentId, int trialBudget, int verdictBudget, DateTimeOffset at,
        int? exploration = null) => db.Write(_ =>
        {
            if (ById(parentId) is not { } parent)
                return CampaignOpened.No($"there is no campaign {parentId} in this installation's ledger.");

            if (!parent.IsOpen)
                return CampaignOpened.No(
                    $"campaign {parentId} was already closed at {parent.ClosedAt:u}. A closed campaign is "
                    + "renewed once; renewing it again would mint a second child over one parent's holdout.");

            Close(parentId, at);

            return CampaignOpened.Yes(Insert(new CampaignRow(
                0, parent.Name, parent.ScoringPolicy, parent.ScoringPolicySha256,
                Budget(trialBudget), Budget(verdictBudget),
                parent.HoldoutDatasetId, parent.HoldoutFrom, at, parent.Id, null)
            {
                // THE RESERVE IS DECLARED AGAIN, because the trial budget is: a renewal buys attempts,
                // and how many of them are kept for exploration is a fact about the attempts bought and
                // not about the parent's. Carrying the parent's number onto a different budget would
                // silently widen or narrow the reserve nobody decided to move.
                ExplorationBudget = Reserve(exploration, Budget(trialBudget)),

                // THE PAPER STANDARD IS THE PARENT'S, exactly as the scoring policy is: a renewal
                // buys attempts and never an easier standard, and there are now two standards for
                // that sentence to be true of.
                PaperPolicy = parent.PaperPolicy,
                PaperPolicySha256 = parent.PaperPolicySha256,

                // AND THE JUDGE'S COST MODEL IS THE PARENT'S, null included: a renewal buys attempts and
                // never cheaper friction, and the owner's judge capital as it stands today is not a
                // fact about this lineage. A parent that predates the model hands the child the same
                // open question, which the child's next verdict request answers for the whole lineage.
                CostModelCanonical = parent.CostModelCanonical,
                CostModelSha256 = parent.CostModelSha256
            }));
        });

    /// <summary>One campaign by its id, or null when this installation has no such row.</summary>
    public CampaignRow? ById(long id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_campaign WHERE id=$id", ("$id", id));
        return Read(c).FirstOrDefault();
    });

    /// <summary>The open campaign over this dataset, or null. At most one can exist: see the type.</summary>
    public CampaignRow? OpenForDataset(long datasetId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM strategy_campaign WHERE holdout_dataset_id=$id AND closed_at IS NULL",
            ("$id", datasetId));
        return Read(c).FirstOrDefault();
    });

    /// <summary>Every campaign this installation has run, newest first.</summary>
    public IReadOnlyList<CampaignRow> All() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_campaign ORDER BY id DESC");
        return Read(c);
    });

    /// <summary>Every campaign still open, oldest first — the order <see cref="ChargedBy"/> charges them in.</summary>
    IReadOnlyList<CampaignRow> OpenCampaigns() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM strategy_campaign WHERE closed_at IS NULL ORDER BY id");
        return Read(c);
    });

    /// <summary>
    /// THE OPEN CAMPAIGNS A RESEARCH RUN OF <paramref name="set"/> FROM <paramref name="from"/> TO <paramref name="to"/>
    /// (both inclusive, either null) IS CHARGED TO: the campaign over its own dataset first, then, by id, every other open
    /// campaign whose DEVELOPMENT MONTHS the run's bars overlap — through any dataset, of any pair
    /// (<c>U-holdout-campaign</c>, rule 1; seat A's decision of 2026-10-09).
    ///
    /// <para><b>Why through any dataset.</b> A cutoff holds MARKET TIME for every pair (<c>U-bar-holdout</c>): every
    /// Download press records a new version of the pair with no cutoff, a correlated pair carries the same months' regime,
    /// and both say what those months did. So a campaign is about market time, and the months before its cutoff are what
    /// its research is about whichever dataset serves them. Its own precommitted policy says so already —
    /// <see cref="CampaignPolicy.V1"/>: "Research runs are made over bars before the campaign's holdout cutoff and every
    /// one of them is charged" — and its sha is on every campaign row, so the code is brought up to the text rather than
    /// the text edited. Charged to the dataset's own campaign alone, a run over a second download of the same months was
    /// served past a spent budget and counted by nobody (the survey's probe, Q1.3 and Q1.4, RED at <c>ea88e72b</c>).</para>
    ///
    /// <para><b>The two spans, both read from the ledger at each call.</b> A campaign's development months are its
    /// dataset's bars before the cutoff, <c>[first bar, holdout_from)</c> — a dataset that records no first bar reads as
    /// one with no start. The run's span is the market its bars cover: from the open of the first it reads to the close of
    /// the last, one bar of the set's interval after it — <c>TapeHoldout</c>'s span — with an open or out-of-range side
    /// read as the set's own first or last bar, as <c>Backtest.ClosesOf</c> reads it, and a bar length this build cannot
    /// read as no end. A window that reads no bar overlaps nothing. One instant shared is no overlap: a run whose last bar
    /// closes as a campaign's first opens has read none of it.</para>
    ///
    /// <para><b>What is not charged here, and why.</b> A FIXTURE run is charged nothing: it is registered under its own
    /// campaign, uncharged, exactly as before, and against no other — it establishes plumbing, never evidence. A campaign
    /// over a FIXTURE or a REJECTED dataset is charged through its own dataset only: its months are not evidence, or not
    /// bars TradeAgent vouches for. And a FEATURE's reach is not counted: a program that reads features reads the tape back
    /// from its first close by its longest reach, and a run whose bars start after a campaign's months while its features
    /// reach into them is not charged by this rule — the tape's own holdout is what keeps that reach out of the held
    /// window, and <c>docs/CONTRACTS.md</c> says so.</para>
    ///
    /// <para><b>Asked by the look and registered by the gate</b>: <c>Backtests.Run</c> asks <see cref="TrialRefusal"/>
    /// of each before the run, and registers each with <see cref="RegisterTrial"/> inside the run's own write, so any
    /// refusal rolls the run back. The order matters once: the first registration decides a never-charged version's home
    /// (<see cref="ChargedCampaignFor"/> reads the row just written), so the run's own campaign is first.</para>
    /// </summary>
    public IReadOnlyList<CampaignCharge> ChargedBy(DatasetRecord set, DateTimeOffset? from, DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(set);

        return db.Read(_ =>
        {
            var charges = new List<CampaignCharge>();
            if (OpenForDataset(set.Id) is { } own) charges.Add(new CampaignCharge(own, null));

            // A FIXTURE RUN STOPS HERE: charged nothing anywhere, and no row under any campaign but its own.
            if (EvaluationClass.Or(set.EvaluationClass) == EvaluationClass.Fixture) return charges;
            if (RunSpan(set, from, to) is not { } span) return charges;

            var datasets = new DatasetStore(db);
            foreach (var other in OpenCampaigns())
            {
                if (other.HoldoutDatasetId == set.Id) continue;

                // CHARGED THROUGH ITS OWN DATASET ONLY: a campaign whose bars are fixture or rejected — or whose dataset this
                // ledger no longer holds — has no months anybody else's research could be evidence about.
                if (datasets.ById(other.HoldoutDatasetId) is not { } held
                    || EvaluationClass.Or(held.EvaluationClass) == EvaluationClass.Fixture
                    || held.State == DatasetState.REJECTED)
                    continue;

                var months = (From: held.FirstBar, Until: held.HoldoutFrom ?? other.HoldoutFrom);
                if (!Overlaps(span.From, span.Until, months.From, months.Until)) continue;

                charges.Add(new CampaignCharge(other, Through(set, span, other, held, months)));
            }

            return charges;
        });
    }

    /// <summary>
    /// THE MARKET A RUN'S BARS COVER, or null because the window reads no bar of the set: from the open of the first to
    /// the close of the last. See <see cref="ChargedBy"/>.
    /// </summary>
    static (DateTimeOffset? From, DateTimeOffset? Until)? RunSpan(DatasetRecord set, DateTimeOffset? from, DateTimeOffset? to)
    {
        var first = from is { } f && (set.FirstBar is not { } fb || f >= fb) ? f : set.FirstBar ?? from;
        var last = to is { } t && (set.LastBar is not { } lb || t <= lb) ? t : set.LastBar ?? to;
        DateTimeOffset? until = last is { } l && BarLength(set.Interval) is { } bar && l <= DateTimeOffset.MaxValue - bar
            ? l + bar
            : null;

        if (first is { } a && until is { } b && a >= b) return null;
        return (first, until);
    }

    /// <summary>One bar of <paramref name="interval"/>, or null for one this build cannot read — which reads as no end.</summary>
    static TimeSpan? BarLength(string interval)
    {
        try { return KlineNormaliser.BarLength(interval); }
        catch (TradeAgentException) { return null; }
    }

    /// <summary>
    /// Whether <c>[a, b)</c> and <c>[c, d)</c> share market time; a null start or end is no bound on that side. One
    /// instant shared is not an overlap: a span that closes as the other opens holds none of it.
    /// </summary>
    static bool Overlaps(DateTimeOffset? a, DateTimeOffset? b, DateTimeOffset? c, DateTimeOffset? d) =>
        (b is not { } hi || c is not { } lo || hi > lo) && (d is not { } end || a is not { } start || start < end);

    /// <summary>The sentence a refusal adds for a campaign charged through another dataset. See <see cref="CampaignCharge"/>.</summary>
    static string Through(DatasetRecord read, (DateTimeOffset? From, DateTimeOffset? Until) span, CampaignRow campaign,
        DatasetRecord held, (DateTimeOffset? From, DateTimeOffset Until) months) =>
        $"This run reads dataset {read.Id} ({read.Pair} {read.Interval} {read.Version}), whose bars "
        + (span switch
        {
            ({ } lo, { } hi) => $"span the market from {lo:u} to the close of the last at {hi:u}",
            ({ } lo, null) => $"span the market from {lo:u} with no end this build can measure",
            (null, { } hi) => $"span the market up to the close of the last at {hi:u}, from no recorded start",
            _ => "span the market with neither a recorded start nor an end"
        })
        + $", and that overlaps the development months of campaign {campaign.Id}: the bars of dataset {held.Id} "
        + $"({held.Pair} {held.Interval} {held.Version}) "
        + (months.From is { } first ? $"from {first:u}" : "from its first")
        + $" up to its cutoff at {months.Until:u}. Research over a campaign's months is charged to it through any dataset "
        + "of any pair — another download of the same months, or a pair that moved with them, says what those months did "
        + "as surely as the campaign's own bars — because its precommitted policy charges every research run made over bars "
        + "before its cutoff. ";

    /// <summary>
    /// THIS CAMPAIGN AND EVERY CAMPAIGN IT WAS RENEWED FROM, newest first — the chain
    /// <c>Referee.RequestVerdict</c> counts verdicts over.
    ///
    /// <para>It walks <c>renewed_from</c> and stops at a row it has already seen, so a cycle written by a
    /// future bug is a short list rather than a hang. An id this installation does not have answers an
    /// empty list.</para>
    /// </summary>
    public IReadOnlyList<long> Lineage(long id)
    {
        var chain = new List<long>();
        var seen = new HashSet<long>();
        var at = (long?)id;

        while (at is { } current && seen.Add(current) && ById(current) is { } row)
        {
            chain.Add(current);
            at = row.RenewedFrom;
        }

        return chain;
    }


    // ---- trials -----------------------------------------------------------------------------------

    /// <summary>
    /// HOW MANY CHARGED RUNS THIS CAMPAIGN HAS REGISTERED. A fixture run is not among them.
    ///
    /// <para>A trial counts here when this campaign was the PEEK (<c>campaign_id</c>) or when it was
    /// the COST (<c>charged_to</c>) — a variant run over another holdout, charged back to the campaign
    /// lineage its ancestry was already being charged to.</para>
    ///
    /// <para><b>RUNS, not rows: <c>COUNT(DISTINCT run_id)</c></b> (<c>U-holdout-campaign</c>). Since one run is charged to
    /// every open campaign whose months its bars overlap (<see cref="ChargedBy"/>), one run can stand on two rows that
    /// both count here — under campaign X, and under campaign Y with X as its home — and <c>COUNT(*)</c> read that as two
    /// trials of X's budget for one peek. A run id hashes its version, its bars and its execution model, so one id is one
    /// question asked once.</para>
    ///
    /// <para><b>And every inconclusive paper slot charged to its lineage</b> (<c>U-referee-v2b</c>): one trial per version
    /// that has ever held an <see cref="AllocationPolicy.InconclusiveV1"/> row whose verdict was taken under this campaign
    /// or one it was renewed from (<see cref="IncubationsCharged"/>). Counted from the allocation ledger, which has no update
    /// and no delete, so a charge is never refunded and an ended term still costs what it cost.</para>
    /// </summary>
    public int TrialsCharged(long campaignId) => TrialsCharged(campaignId, null, null);

    /// <summary>
    /// How many charged runs of ONE POT this campaign has registered — the exploration reserve, or
    /// the rest of the trial budget. The two together, with <see cref="IncubationsCharged"/>, are
    /// <see cref="TrialsCharged(long)"/>: an inconclusive slot is not a run and comes out of neither pot, only out of the
    /// whole budget, which it can only tighten.
    /// </summary>
    public int TrialsCharged(long campaignId, bool exploration) => TrialsCharged(campaignId, exploration, null);

    /// <summary>
    /// The count both public overloads read, and the GATE's reading of it: <paramref name="exceptRun"/> leaves out the run
    /// being registered, because a run already counted here through an earlier row of the same write costs this
    /// campaign nothing more — and a ceiling that counted it would refuse the run for its own trial.
    /// </summary>
    int TrialsCharged(long campaignId, bool? exploration, string? exceptRun) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(DISTINCT run_id) FROM strategy_trial WHERE charged=1 "
            + "AND ($e IS NULL OR exploration=$e) AND ($run IS NULL OR run_id<>$run) "
            + "AND (campaign_id=$id OR charged_to=$id)",
            ("$id", campaignId), ("$e", exploration is { } pot ? pot ? 1 : 0 : null), ("$run", exceptRun));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture)
               + (exploration is null ? IncubationsCharged(campaignId) : 0);
    });

    // ---- the inconclusive quota's charge (`U-referee-v2b`) ------------------------------------------

    /// <summary>
    /// HOW MANY INCONCLUSIVE PAPER SLOTS THIS CAMPAIGN'S LINEAGE HAS BEEN CHARGED: the distinct versions that have ever
    /// held an <see cref="AllocationPolicy.InconclusiveV1"/> row whose promotion was taken under this campaign or one it
    /// was renewed from (<see cref="Lineage"/>). Every row ever written, standing or ended — the ledger keeps them all.
    /// </summary>
    public int IncubationsCharged(long campaignId) => db.Read(_ =>
    {
        var lineage = Lineage(campaignId).ToHashSet();
        if (lineage.Count == 0) return 0;

        using var c = db.Cmd("""
            SELECT DISTINCT p.campaign_id, a.version_id
              FROM strategy_allocation a JOIN strategy_promotion p ON p.id = a.promotion_id
             WHERE a.policy_version = $policy
            """, ("$policy", AllocationPolicy.InconclusiveV1));

        var versions = new HashSet<string>(StringComparer.Ordinal);
        using var r = c.ExecuteReader();
        while (r.Read())
            if (lineage.Contains(r.GetInt64(0))) versions.Add(r.GetString(1));
        return versions.Count;
    });

    /// <summary>
    /// WHY AN INCONCLUSIVE PAPER SLOT FOR THIS VERSION, JUDGED UNDER THIS CAMPAIGN, WOULD NOT BE CHARGED — in words — or
    /// null because it would. The slot costs one trial of the verdict's campaign; a campaign this ledger does not hold, or
    /// one whose trial budget is spent, refuses it, and nothing is written. Read inside the allocation ledger's write
    /// (<c>Allocations.RecordPaper</c>), so the count and the row are one transaction.
    /// </summary>
    public string? IncubationRefusal(long campaignId, string versionId) => db.Read(_ =>
    {
        if (ById(campaignId) is not { } campaign)
            return $"the campaign {campaignId} version {Short(versionId)} was judged under is not in this installation's "
                   + "ledger, so there is no trial budget to charge an inconclusive paper slot to and nothing was written.";

        var spent = TrialsCharged(campaignId);
        if (spent < campaign.TrialBudget) return null;

        return $"campaign {campaign.Id} has spent all {campaign.TrialBudget} of its trials — "
               + $"{IncubationsCharged(campaignId)} of them on inconclusive paper slots — so version {Short(versionId)} was "
               + "not given an inconclusive paper slot and nothing was written. A slot is charged one trial to the campaign its "
               + "verdict was taken under, once, and never refunded, so observing a version the referee could not judge is "
               + "paid for out of the same budget as every other attempt. What is left is a renewal, which the account owner "
               + "authorises in TradeAgent's own window.";
    });

    /// <summary>
    /// THE HOLDOUT A VERDICT UNDER THIS CAMPAIGN SPANNED: the campaign's <c>holdout_from</c> to the close of its dataset's
    /// last bar — or null when that end cannot be read (<see cref="Close"/>), which <c>Strategy.Inconclusive</c> reads as
    /// short. What decides whether a loss is inconclusive.
    /// </summary>
    public TimeSpan? HoldoutSpan(long campaignId) => db.Read<TimeSpan?>(_ =>
        ById(campaignId) is { } campaign && Close(campaign.HoldoutDatasetId, new Dictionary<long, DateTimeOffset?>()) is { } close
            ? close - campaign.HoldoutFrom
            : null);

    /// <summary>
    /// THE ROOT OF THIS CAMPAIGN'S RENEWAL LINEAGE — the eldest campaign <see cref="Lineage"/> reaches — or null for a
    /// campaign this ledger does not hold. Two campaigns with one root are one lineage: the stand-in for a family the
    /// inconclusive quota allows one slot at a time (no family exists in this build; that is <c>U-experiments-op</c>).
    /// </summary>
    public long? LineageRoot(long campaignId) => Lineage(campaignId) is { Count: > 0 } chain ? chain[^1] : null;

    static string Short(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>Every trial registered against this campaign, oldest first.</summary>
    public IReadOnlyList<TrialRow> Trials(long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT campaign_id, version_id, run_id, kind, charged, registered_at, charged_to,
                   exploration
            FROM strategy_trial WHERE campaign_id=$id ORDER BY registered_at, run_id
            """, ("$id", campaignId));

        var rows = new List<TrialRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new TrialRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4) != 0, Sql.Time(r.GetString(5)))
            {
                ChargedTo = r.IsDBNull(6) ? null : r.GetInt64(6),
                Exploration = r.GetInt32(7) != 0
            });
        return rows;
    });

    /// <summary>
    /// THE CAMPAIGN A TRIAL FOR THIS VERSION IS CHARGED TO — its ancestry's, when its ancestry has one,
    /// and otherwise the campaign the run was made under.
    ///
    /// <para><c>docs/COUNCIL.md</c>:201, "comparable trials". Without this a variant is a fresh hash
    /// related to nothing: run it against a second holdout and it is charged against a second campaign's
    /// untouched budget, so a family can buy itself as many attempts as the owner has datasets. The
    /// charge follows the DECLARED ancestry (<c>StrategyVersionRow.ParentVersionId</c>) to the ELDEST
    /// ancestor that has already been charged somewhere, and then forward through that campaign's
    /// renewals to the one now open — a renewal buys the family trials, which is what renewal is for,
    /// and a closed campaign is not where a new charge belongs.</para>
    ///
    /// <para><b>It can only ever tighten.</b> The run's own campaign is still counted exactly as it
    /// was: <see cref="TrialsCharged"/> counts a row under both numbers, and
    /// <see cref="RegisterTrial"/> refuses when EITHER budget is full. A declared parent therefore
    /// cannot buy a trial the run's own campaign would have refused.</para>
    /// </summary>
    public long ChargedCampaignFor(string versionId, long campaignId)
    {
        var ancestry = new StrategyStore(db).Ancestry(versionId);

        // ELDEST FIRST, so the whole family answers to where its root was charged rather than to
        // wherever its newest member happened to be run.
        for (var i = ancestry.Count - 1; i >= 0; i--)
            if (ChargedCampaignOf(ancestry[i]) is { } home)
                return Current(home);

        return campaignId;
    }

    /// <summary>The campaign the earliest charged trial of this version was charged to, or null.</summary>
    long? ChargedCampaignOf(string versionId) => db.Read<long?>(_ =>
    {
        using var c = db.Cmd("""
            SELECT COALESCE(charged_to, campaign_id) FROM strategy_trial
             WHERE version_id=$v AND charged=1 ORDER BY registered_at, run_id LIMIT 1
            """, ("$v", versionId));
        var o = c.ExecuteScalar();
        return o is null or DBNull ? null : Convert.ToInt64(o, CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// The newest campaign in this one's renewal lineage — <see cref="Lineage"/> walked the other way.
    /// It stops on a row it has already seen, so a cycle is a short walk rather than a hang.
    /// </summary>
    long Current(long campaignId) => db.Read<long>(_ =>
    {
        var at = campaignId;
        var seen = new HashSet<long> { at };

        while (true)
        {
            using var c = db.Cmd("SELECT id FROM strategy_campaign WHERE renewed_from=$id LIMIT 1",
                ("$id", at));
            var o = c.ExecuteScalar();
            if (o is null or DBNull) return at;

            var next = Convert.ToInt64(o, CultureInfo.InvariantCulture);
            if (!seen.Add(next)) return at;
            at = next;
        }
    });

    /// <summary>
    /// WHY THE NEXT RUN OF THIS KIND WOULD NOT BE REGISTERED, in words, or null because it would.
    ///
    /// <para><b>A LOOK, AND NEVER THE GATE.</b> It is taken BEFORE the run so that a caller is not told
    /// "your budget is spent" after twenty minutes of evaluation has spent the budget to learn that it
    /// was spent — but it reads the count in its own transaction and the row goes in later, so two roles
    /// that take this look together both pass it. That is not a defect in the look: it is what a cheap
    /// first look can honestly say, and it is why <see cref="RegisterTrial"/> asks again inside the
    /// transaction that writes. Exactly the shape <c>AiAttemptStore.Begin</c> has beside
    /// <c>AdmitsAnotherTurn</c>, for exactly the same reason.</para>
    ///
    /// <para>A <c>fixture</c> run is never refused here — it is charged nothing, so there is nothing for
    /// a budget to refuse.</para>
    ///
    /// <para><paramref name="through"/> is <see cref="CampaignCharge.Through"/>: null for the campaign over the run's own
    /// dataset, and for any other the sentence a refusal carries naming its months and the dataset the run reads.</para>
    /// </summary>
    public string? TrialRefusal(long campaignId, string versionId, string kind, string? through = null)
    {
        // THE FENCE COMES FIRST AND IT IS NOT A BUDGET. A retired candidate takes no new assignment at
        // all, so a fixture run of one is refused here too — the exemption above is about the trial
        // budget, and this is about whether there is an assignment to make.
        if (new Retirements(db).Refusal(versionId) is { } retired) return retired;

        if (kind == EvaluationClass.Fixture) return null;
        if (ById(campaignId) is null) return null;

        var rule = Admission(campaignId, versionId, kind);
        return Refusal(rule, Counts(rule, null), made: false, through);
    }

    /// <summary>
    /// THE ONE PIECE OF ARITHMETIC BOTH THE LOOK AND THE GATE ASK — the shape
    /// <c>AiAdmissionRule.Reading</c> has, and for the same reason: two comparisons that could drift
    /// apart would be two budgets.
    ///
    /// <para>It carries every count it was decided on, so a refusal can name WHICH ceiling had no room:
    /// the campaign the run peeks at, the campaign the version's family is charged to, or the part of
    /// the trial budget reserved for exploration.</para>
    /// </summary>
    TrialAdmission Admission(long campaignId, string versionId, string kind) => new()
    {
        Charged = kind != EvaluationClass.Fixture,
        Exploration = IsExploration(versionId),
        RunCampaign = ById(campaignId),
        HomeCampaign = ById(ChargedCampaignFor(versionId, campaignId))
    };

    /// <summary>
    /// WHETHER THIS VERSION IS AN EXPLORATION: it declared no parent, or it declared one that nothing
    /// has promoted.
    ///
    /// <para>The promotion is read through <c>Promotions.Standing</c> and never as "a promotion row
    /// exists", the reading <c>Allocations.Record</c> takes for the same reason: a verdict TradeAgent
    /// has since withdrawn is not evidence that a parent is worth refining.</para>
    /// </summary>
    public bool IsExploration(string versionId)
    {
        if (new StrategyStore(db).VersionById(versionId)?.ParentVersionId is not { } parent) return true;
        return !new Promotions(db).Standing(parent).IsPromoted;
    }

    /// <summary>
    /// The counts this rule is decided on. Read by the LOOK in its own transaction, where they are a
    /// cheap honest answer, and by the GATE inside the write, where nothing can move them — the gate with
    /// <paramref name="exceptRun"/>, the run it is registering, left out (see <see cref="TrialsCharged(long)"/>).
    /// </summary>
    TrialCounts Counts(TrialAdmission rule, string? exceptRun) => new(
        rule.RunCampaign is { } run ? TrialsCharged(run.Id, null, exceptRun) : 0,
        rule.RunCampaign is { } runPot ? TrialsCharged(runPot.Id, rule.Exploration, exceptRun) : 0,
        rule.HomeCampaign is { } home ? TrialsCharged(home.Id, null, exceptRun) : 0,
        rule.HomeCampaign is { } homePot ? TrialsCharged(homePot.Id, rule.Exploration, exceptRun) : 0);

    /// <summary>
    /// WHY THIS TRIAL WOULD NOT BE REGISTERED, in words, or null because it would. One rule, both
    /// callers; <paramref name="made"/> only chooses the tense of the sentence, and <paramref name="through"/> is the
    /// sentence naming the months and the dataset read when the run campaign is not the run's own dataset's.
    /// </summary>
    static string? Refusal(TrialAdmission rule, TrialCounts spent, bool made, string? through)
    {
        if (!rule.Charged || rule.RunCampaign is not { } run) return null;

        if (spent.Run >= run.TrialBudget) return Spent(run.Id, run.TrialBudget, made, through);

        // THE POT, AND IT IS READ AT THE GATE. A campaign's trial budget is two budgets: the reserve
        // kept for versions with no promoted parent, and the rest. Both ceilings are inside the
        // transaction that writes the row, because a reservation taken outside it is the look
        // `U-referee-1` named — two roles both honestly told there is room, and both taking it.
        if (spent.RunPot >= rule.PotOf(run)) return Pot(rule, run, made, through);

        // AND THE FAMILY'S OWN CAMPAIGN, which is a second ceiling and never a looser one: a version is
        // charged back to the campaign lineage its family was first charged to (`docs/COUNCIL.md`:201,
        // comparable trials).
        if (rule.HomeCampaign is not { } home || rule.OneCampaign) return null;

        if (spent.Home >= home.TrialBudget) return Spent(home.Id, home.TrialBudget, made, null) + Elsewhere;

        return spent.HomePot >= rule.PotOf(home) ? Pot(rule, home, made, null) + Elsewhere : null;
    }

    /// <summary>The sentence a spent POT answers with, naming which of the two had no room.</summary>
    static string Pot(TrialAdmission rule, CampaignRow campaign, bool made, string? through) =>
        $"campaign {campaign.Id} has registered all {rule.PotOf(campaign)} of the trials it reserves for "
        + (rule.Exploration
            ? "EXPLORATION — versions that declare no parent, or one nothing has promoted — so "
            : "REFINEMENT — versions whose declared parent is promoted — so ")
        + (made ? "this run is not recorded and its result is not served. " : "this run is refused before it is made. ")
        + through
        + $"The reserve is {campaign.ExplorationBudget} of this campaign's {campaign.TrialBudget} trials "
        + $"for exploration and {campaign.RefinementBudget} for refinement, fixed when the campaign "
        + "opened. The two sum to the trial budget, so declaring a parent moves a trial from one pot to "
        + "the other and creates no allowance: what is left is a renewal, which the account owner "
        + "authorises in TradeAgent's own window.";

    /// <summary>
    /// The clause a refusal adds when the budget that had no room was the family's, not this run's. A family is the
    /// version and every ancestor it declares, and its home is where the eldest of them was first charged — which, since
    /// a run is charged through any dataset (<see cref="ChargedBy"/>), can be another campaign for a version that
    /// declared no parent at all.
    /// </summary>
    const string Elsewhere =
        " This run is charged there as well as here, because a version is charged to the campaign lineage its "
        + "family — itself and every parent it declares — was first charged to, so a family cannot buy itself an "
        + "untouched trial budget by running against a second dataset or by submitting a new hash.";

    /// <summary>
    /// REGISTERS ONE TRIAL, CHARGING IT AGAINST THE BUDGET IN THE SAME TRANSACTION THAT READS IT — or
    /// refuses in words, having written nothing.
    ///
    /// <para><b>The count and the insert are ONE <see cref="Database.Write"/>, and that is this method's
    /// property.</b> They used to be two: <see cref="TrialRefusal"/> read the count before the run and
    /// this wrote the row after it, so two roles asking for the last trial at once both read 199 of 200
    /// and both registered. A campaign-wide budget that two concurrent callers can exceed by one is a
    /// budget the doctrine's "campaign-wide trial limits that survive a team's replacement"
    /// (<c>docs/COUNCIL.md</c>:36) does not describe — and the overrun is a peek at the owner's data
    /// nobody was charged for.</para>
    ///
    /// <para><b>Idempotent on (campaign, version, run), and the FIRST registration stands — even over
    /// budget.</b> All three are content hashes or the app's own id, so re-asking the identical question
    /// is the same trial, is not charged twice, and must still answer Ok: the row is already there, and
    /// refusing it would make a restart look like an overrun.</para>
    ///
    /// <para><b>A refusal here means the run is not recorded at all</b>, because the caller rolls its own
    /// transaction back on it. That is deliberate and it is the lesser of the two wrongs: the compute is
    /// already spent either way, and the alternative is a run over the campaign's data standing in the
    /// ledger with no trial against it — which is the peek the count exists to bound, recorded as free.
    /// The caller is told in these words rather than quietly served the figures.</para>
    ///
    /// <para><b>One run may be registered under several campaigns</b> — every open campaign whose months its bars overlap
    /// (<see cref="ChargedBy"/>), <paramref name="through"/> naming them for a campaign that is not the run's own
    /// dataset's. Each row is its own trial of its own campaign, and the count reads RUNS, so the second row of one run
    /// costs a campaign that already counted it nothing: the gate's counts leave this run out.</para>
    /// </summary>
    public TrialRegistered RegisterTrial(long campaignId, string versionId, string runId, string kind,
        DateTimeOffset at, string? through = null) => db.Write(_ =>
    {
        if (ById(campaignId) is not { } campaign)
            return new TrialRegistered(false, $"there is no campaign {campaignId}.", false, 0, 0);

        var charged = kind != EvaluationClass.Fixture;

        // THE RULE AND ITS COUNTS, BOTH READ HERE. The rule is the same one `TrialRefusal` asked before
        // the run; the counts are the part another caller can move in between, so they are read inside
        // this transaction and nowhere else. That is `AiAttemptStore.Begin`'s shape and it is the whole
        // of the guard.
        var rule = Admission(campaignId, versionId, kind);
        var spent = Counts(rule, runId);
        var chargedTo = rule.HomeCampaign?.Id ?? campaignId;

        // ALREADY REGISTERED: the same question, asked again. The first row stands and the answer is Ok
        // whatever the budget now says — see the doc comment.
        if (Registered(campaignId, versionId, runId))
            return new TrialRegistered(true, "", charged, TrialsCharged(campaignId), campaign.TrialBudget);

        // AND THE RETIREMENT FENCE, AFTER IT. `docs/COUNCIL.md`:223: retirement "stops new assignments
        // and fences attempts". A trial already on the table is not a new assignment — it is a row that
        // is already there, and refusing it would make a restart look like something a retirement had
        // taken away, which is exactly what a retirement never does.
        if (new Retirements(db).Refusal(versionId) is { } retired)
            return new TrialRegistered(false, retired, false, spent.Run, campaign.TrialBudget);

        // THE GATE, AND IT IS THIS TRANSACTION'S OWN READING RATHER THAN THE CALLER'S.
        if (Refusal(rule, spent, made: true, through) is { } why)
            return new TrialRegistered(false, why, false, spent.Run, campaign.TrialBudget);

        using var c = db.Cmd("""
            INSERT INTO strategy_trial(campaign_id, version_id, run_id, kind, charged, registered_at,
                                       charged_to, exploration)
            VALUES($id,$ver,$run,$kind,$charged,$at,$home,$explore)
            ON CONFLICT(campaign_id, version_id, run_id) DO NOTHING
            """,
            ("$id", campaignId), ("$ver", versionId), ("$run", runId), ("$kind", kind),
            ("$charged", charged ? 1 : 0), ("$at", Sql.T(at)),
            // THE COST, BESIDE THE PEEK. They are one number for a family's first trial and for every
            // later one under the lineage it was first charged to.
            ("$home", chargedTo),
            // AND WHICH POT IT CAME OUT OF, as the question stood now. Copied, never re-derived: a
            // parent promoted later must not reclassify what this trial cost.
            ("$explore", rule.Exploration ? 1 : 0));
        c.ExecuteNonQuery();

        return new TrialRegistered(true, "", charged, TrialsCharged(campaignId), campaign.TrialBudget);
    });

    /// <summary>Whether this exact trial is already on the table. Read inside the caller's transaction.</summary>
    public bool Registered(long campaignId, string versionId, string runId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM strategy_trial WHERE campaign_id=$id AND version_id=$ver AND run_id=$run",
            ("$id", campaignId), ("$ver", versionId), ("$run", runId));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    });

    /// <summary>
    /// THE SENTENCE A SPENT TRIAL BUDGET ANSWERS WITH, in whichever tense applies. One text with two
    /// halves rather than two texts, because everything after the first clause is the same fact and a
    /// second copy of it is a second thing to keep true.
    /// </summary>
    static string Spent(long campaignId, int budget, bool made, string? through) =>
        $"campaign {campaignId} has registered all {budget} of its research trials, so "
        + (made
            ? "this run is not recorded and its result is not served: the count is charged in the same "
              + "transaction that reads it, so two roles asking for the last trial at once cannot both "
              + "take it. "
            : "this run is refused before it is made. ")
        + through
        + "A trial is one registered run of one version over one "
        + "dataset under one execution model, and the count is the CAMPAIGN's: it is keyed by the "
        + "campaign, the version and the run and by nothing about who asked, so it is not reset by a "
        + "restart, by a fresh attempt or by a replacement team. What is left is a renewal, which the "
        + "account owner authorises in TradeAgent's own window and which carries this campaign's "
        + "holdout and its lineage forward. Runs over a fixture dataset are still free.";


    // ---- verdicts ---------------------------------------------------------------------------------

    /// <summary>
    /// HOW MANY VERDICTS HAVE BEEN CHARGED ACROSS THIS CAMPAIGN'S WHOLE RENEWAL LINEAGE — and no other campaign's.
    ///
    /// <para>The lineage and not the campaign, and that is the point of <c>renewed_from</c>: renewal buys
    /// ATTEMPTS, and it must not buy holdout access (<c>docs/COUNCIL.md</c>:132, "campaign renewal
    /// authorised by code so no new campaign resets holdout access"). Every verdict already taken read the
    /// same months, so every verdict already taken still counts.</para>
    ///
    /// <para><b>It is no longer the judgement budget's count</b> (<c>U-holdout-campaign</c>): that is
    /// <see cref="JudgementsSpent"/>, which also counts every other campaign's judgements over the same months. One
    /// question still wants this one — <c>Referee</c>'s legacy cost-model pin, "was THIS lineage already judged
    /// frictionless", because another campaign's verdicts were judged under that campaign's own pin.</para>
    /// </summary>
    public int VerdictsInLineage(long campaignId)
    {
        var chain = Lineage(campaignId);
        if (chain.Count == 0) return 0;

        return db.Read(_ =>
        {
            using var c = db.Cmd(
                $"SELECT COUNT(*) FROM strategy_verdict WHERE campaign_id IN ({Ids(chain)})");
            return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    /// <summary>
    /// THE FINAL JUDGEMENTS THIS CAMPAIGN HAS SPENT: every verdict, of any campaign and any pair, whose read span overlaps
    /// the span this campaign's own judgements read — its renewal lineage's included (<c>U-holdout-campaign</c>, rule 2;
    /// seat A's decision of 2026-10-09).
    ///
    /// <para><b>Why across campaigns.</b> A judgement reads the held months themselves — <c>Referee.Verdict</c> runs from
    /// the cutoff on its row to the close of its dataset's last bar — and its answer tells the research process something
    /// about those months, whichever campaign took it. A cutoff holds market time for every pair, so a second download of
    /// the same months held back by a second press opens a second campaign over months already judged: counted over its
    /// own lineage alone, its budget started untouched and one version stood judged twice over one held hour, "1 of 3"
    /// each time (the survey's probe, S.5-S.9). A second press over held months is TOLD and COUNTED — never refused,
    /// because a REJECTED dataset's window stays held (<c>TapeHoldout.WindowsOf</c>, "whatever its state") and a refusal
    /// would leave its months nothing but a dead end.</para>
    ///
    /// <para><b>The spans, from recorded facts only.</b> A verdict's read span is <c>[holdout_from on its row, the close
    /// of its campaign's dataset's last bar)</c>; this campaign's is the same from its own row's cutoff. A dataset whose
    /// last bar or bar length this build cannot read spans with no end — refused, never guessed short — and a span that
    /// ends where it starts reads nothing, so it overlaps nothing; its lineage's verdicts count whatever their spans.</para>
    /// </summary>
    public int JudgementsSpent(long campaignId) => db.Read(_ =>
    {
        if (ById(campaignId) is not { } campaign) return 0;

        var lineage = Lineage(campaignId).ToHashSet();
        var closes = new Dictionary<long, DateTimeOffset?>();
        var (from, until) = (campaign.HoldoutFrom, Close(campaign.HoldoutDatasetId, closes));

        return JudgementSpans(closes).Count(v => lineage.Contains(v.CampaignId) || Overlaps(v.From, v.Until, from, until));
    });

    /// <summary>
    /// EVERY OTHER CAMPAIGN OVER THE MONTHS THIS ONE JUDGES — not this campaign and not its renewal lineage — oldest
    /// first, each with its dataset and the judgements it has taken over them: a campaign whose own read span overlaps
    /// this one's, counting its verdicts whose spans do (<see cref="JudgementsSpent"/>). What the owner's press names, and
    /// what a spent judgement budget names.
    /// </summary>
    public IReadOnlyList<CampaignOverMonths> OverTheSameMonths(long campaignId) => db.Read(_ =>
    {
        if (ById(campaignId) is not { } campaign) return (IReadOnlyList<CampaignOverMonths>)[];

        var lineage = Lineage(campaignId).ToHashSet();
        var closes = new Dictionary<long, DateTimeOffset?>();
        var (from, until) = (campaign.HoldoutFrom, Close(campaign.HoldoutDatasetId, closes));
        var judged = JudgementSpans(closes);
        var datasets = new DatasetStore(db);

        var shared = new List<CampaignOverMonths>();
        foreach (var other in All().OrderBy(c => c.Id))
        {
            if (lineage.Contains(other.Id)) continue;
            if (!Overlaps(other.HoldoutFrom, Close(other.HoldoutDatasetId, closes), from, until)) continue;

            shared.Add(new CampaignOverMonths(other, datasets.ById(other.HoldoutDatasetId),
                judged.Count(v => v.CampaignId == other.Id && Overlaps(v.From, v.Until, from, until))));
        }

        return (IReadOnlyList<CampaignOverMonths>)shared;
    });

    /// <summary>Every verdict ever charged, as the market time it read: its campaign, its row's cutoff, its dataset's close.</summary>
    List<(long CampaignId, DateTimeOffset From, DateTimeOffset? Until)> JudgementSpans(Dictionary<long, DateTimeOffset?> closes)
    {
        var rows = new List<(long CampaignId, DateTimeOffset From, long Dataset)>();
        using (var c = db.Cmd("""
            SELECT v.campaign_id, v.holdout_from, c.holdout_dataset_id
              FROM strategy_verdict v JOIN strategy_campaign c ON c.id = v.campaign_id
            """))
        using (var r = c.ExecuteReader())
            while (r.Read())
                rows.Add((r.GetInt64(0), Sql.Time(r.GetString(1)), r.GetInt64(2)));

        return [.. rows.Select(v => (v.CampaignId, v.From, Close(v.Dataset, closes)))];
    }

    /// <summary>
    /// THE CLOSE OF A HOLDOUT DATASET'S LAST BAR — where a judgement over it stops reading — or null for no end: a dataset
    /// this ledger does not hold, one that records no last bar, or a bar length this build cannot read. Asked once per
    /// dataset per question, through <paramref name="closes"/>.
    /// </summary>
    DateTimeOffset? Close(long datasetId, Dictionary<long, DateTimeOffset?> closes)
    {
        if (closes.TryGetValue(datasetId, out var known)) return known;

        var close = new DatasetStore(db).ById(datasetId) is { LastBar: { } last } set
                    && BarLength(set.Interval) is { } bar && last <= DateTimeOffset.MaxValue - bar
            ? last + bar
            : (DateTimeOffset?)null;
        closes[datasetId] = close;
        return close;
    }

    /// <summary>
    /// WHAT THE OWNER'S PRESS SAYS ABOUT THE CAMPAIGN IT OPENED, in his words: what it is measured against, and — when other
    /// campaigns already hold the same months — each of them, the judgements each has taken over them, and how many of this
    /// campaign's are therefore spent already (<c>U-holdout-campaign</c>, rule 2). A static so the sentence the card prints
    /// is the sentence a test reads; <paramref name="spent"/> is <see cref="JudgementsSpent"/> and
    /// <paramref name="others"/> <see cref="OverTheSameMonths"/>, both read after the press.
    /// </summary>
    public static string PressNote(CampaignRow campaign, int spent, IReadOnlyList<CampaignOverMonths> others)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(others);

        var note = $"Campaign {campaign.Id} is measured against them: {campaign.TrialBudget:N0} research runs and "
            + $"{campaign.VerdictBudget:N0} final judgements, and the standard it will be judged by is fixed as of now.";
        if (others.Count == 0) return note;

        return note + " The same months are already held back by "
            + string.Join("; ", others.Select(o => $"campaign {o.Campaign.Id} ({Named(o)}), which has taken "
                + (o.Judgements switch { 0 => "no judgement", 1 => "1 judgement", var n => $"{n} judgements" }) + " over them"))
            + ", so "
            + (spent == 0
                ? $"none of campaign {campaign.Id}'s {campaign.VerdictBudget:N0} final judgements is spent yet"
                : $"{spent:N0} of campaign {campaign.Id}'s {campaign.VerdictBudget:N0} final judgements "
                  + (spent == 1 ? "is" : "are") + " already spent")
            + ": a judgement over months already held counts against every campaign that holds them, and research over "
            + "them is charged to each — a fresh download buys no fresh look at them. To judge strategies on months no "
            + "judgement has read, hold back OTHER months.";
    }

    /// <summary>A campaign's dataset as a refusal or a note names it: its pair, interval and version, or its id when the ledger holds none.</summary>
    static string Named(CampaignOverMonths over) =>
        over.Dataset is { } set ? $"{set.Pair} {set.Interval} {set.Version}" : $"dataset {over.Campaign.HoldoutDatasetId}";

    /// <summary>Every verdict charged against this campaign alone, oldest first.</summary>
    public IReadOnlyList<VerdictRow> Verdicts(long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT campaign_id, version_id, requested_at, holdout_from
            FROM strategy_verdict WHERE campaign_id=$id ORDER BY requested_at, version_id
            """, ("$id", campaignId));

        var rows = new List<VerdictRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new VerdictRow(r.GetInt64(0), r.GetString(1), Sql.Time(r.GetString(2)),
                Sql.Time(r.GetString(3))));
        return rows;
    });

    /// <summary>Whether this exact verdict has already been charged. See <c>Strategy.Referee</c>.</summary>
    public bool VerdictCharged(long campaignId, string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM strategy_verdict WHERE campaign_id=$id AND version_id=$ver",
            ("$id", campaignId), ("$ver", versionId));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    });

    /// <summary>
    /// CHARGES ONE VERDICT, or refuses in words — and it is the CHARGE, not a reservation: the row is
    /// written here, before anything reads a holdout bar.
    ///
    /// <para>Idempotent on (campaign, version), and an already-charged verdict is answered Ok EVEN IF the
    /// budget is now full. That is deliberate: the charge is taken before the computation, so a crash in
    /// between must leave the verdict obtainable rather than paid for and unreachable. It is not a second
    /// leak — the same version over the same holdout is the same answer.</para>
    ///
    /// <para>The whole of this runs in one write transaction, so the count and the insert cannot be
    /// separated by another charge.</para>
    /// </summary>
    public VerdictCharged ChargeVerdict(long campaignId, string versionId, DateTimeOffset at) => db.Write(_ =>
    {
        if (ById(campaignId) is not { } campaign)
            return new VerdictCharged(false, $"there is no campaign {campaignId}.", 0, 0);

        // EVERY JUDGEMENT OVER THE MONTHS THIS CAMPAIGN HOLDS, its lineage's and every other campaign's alike
        // (U-holdout-campaign, rule 2), read inside the write that adds one.
        var spent = JudgementsSpent(campaignId);

        if (VerdictCharged(campaignId, versionId))
            return new VerdictCharged(true, "", spent, campaign.VerdictBudget);

        // THE RETIREMENT FENCE, AFTER the already-charged answer and before the budget. A verdict the
        // campaign has already paid for stays obtainable — the charge is taken before the computation,
        // and a retirement must not turn a paid-for verdict into an unreachable one. What is refused is
        // a NEW one (`docs/COUNCIL.md`:223, "stops new assignments").
        if (new Retirements(db).Refusal(versionId) is { } retired)
            return new VerdictCharged(false, retired, spent, campaign.VerdictBudget);

        if (!campaign.IsOpen)
            return new VerdictCharged(false,
                $"campaign {campaignId} closed at {campaign.ClosedAt:u} and takes no further verdict. Its "
                + "renewal is the campaign that does, and it carries this one's holdout and its verdicts.",
                spent, campaign.VerdictBudget);

        if (spent >= campaign.VerdictBudget)
            return new VerdictCharged(false, JudgementsGone(campaign, OverTheSameMonths(campaignId)),
                spent, campaign.VerdictBudget);

        using var c = db.Cmd("""
            INSERT INTO strategy_verdict(campaign_id, version_id, requested_at, holdout_from)
            VALUES($id,$ver,$at,$cut)
            """,
            ("$id", campaignId), ("$ver", versionId), ("$at", Sql.T(at)),
            ("$cut", Sql.T(campaign.HoldoutFrom)));
        c.ExecuteNonQuery();

        return new VerdictCharged(true, "", spent + 1, campaign.VerdictBudget);
    });

    /// <summary>
    /// THE SENTENCE A SPENT JUDGEMENT BUDGET ANSWERS WITH: counted across every renewal and every other campaign over the
    /// same months — each named, with what it took — and the way on, which is a holdout over OTHER months, because a fresh
    /// copy of the same months shares these judgements (<c>U-holdout-campaign</c>, rule 2).
    /// </summary>
    static string JudgementsGone(CampaignRow campaign, IReadOnlyList<CampaignOverMonths> others) =>
        $"campaign {campaign.Id} has spent all {campaign.VerdictBudget} of its final judgements, so this one is refused "
        + "BEFORE anything is computed over the held-back bars. Every verdict leaks: its answer tells the research process "
        + "something about months it was never shown, which is why the number is small and why it is counted across every "
        + "renewal of this campaign — a renewal buys attempts and never holdout access — and across every judgement any "
        + "other campaign has taken over the same held months, whichever download of them it holds"
        + (others.Count == 0
            ? ""
            : " (" + string.Join("; ", others.Select(o => $"campaign {o.Campaign.Id} over dataset {o.Campaign.HoldoutDatasetId} "
                + $"({Named(o)}) has taken {o.Judgements}")) + ")")
        + ". What is left is a holdout over OTHER months, which the account owner sets in TradeAgent's own window: a fresh "
        + "copy of the history held back over these same months shares these judgements.";

    /// <summary>A short list of campaign ids for an IN clause. They are longs this code read itself.</summary>
    static string Ids(IReadOnlyList<long> ids) =>
        string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// PINS THE JUDGE'S COST MODEL ON A CAMPAIGN THAT HAS NONE — once, and never over one that is there.
    ///
    /// <para>The one writer is <c>Referee.RequestVerdict</c>, in the transaction that charges a legacy
    /// campaign's next verdict; that is why this is <c>internal</c> to Core and why the statement is
    /// guarded by <c>cost_model_canonical IS NULL</c>: a pin is a precommitment, and a second write that
    /// could replace the first would be a standard rewritten after the evidence.</para>
    /// </summary>
    internal bool PinCostModel(long campaignId, Strategy.VenueCostModel model) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(model);

        using var c = db.Cmd("""
            UPDATE strategy_campaign SET cost_model_canonical=$text, cost_model_sha=$sha
             WHERE id=$id AND cost_model_canonical IS NULL
            """, ("$text", model.Canonical), ("$sha", model.Sha256), ("$id", campaignId));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>Closes a campaign. Renewal does this to the parent; nothing else calls it yet.</summary>
    public void Close(long id, DateTimeOffset at) => db.Write(_ =>
    {
        using var c = db.Cmd("UPDATE strategy_campaign SET closed_at=$at WHERE id=$id AND closed_at IS NULL",
            ("$at", Sql.T(at)), ("$id", id));
        return c.ExecuteNonQuery();
    });

    /// <summary>A budget is never negative. Zero is "no attempts", which is a real answer — see the settings.</summary>
    static int Budget(int asked) => asked < 0 ? 0 : asked;

    CampaignRow Insert(CampaignRow row) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO strategy_campaign(name, scoring_policy, scoring_policy_sha256, trial_budget,
                                          verdict_budget, holdout_dataset_id, holdout_from, opened_at,
                                          renewed_from, closed_at, exploration_budget,
                                          paper_policy, paper_policy_sha256,
                                          cost_model_canonical, cost_model_sha)
            VALUES($name,$policy,$sha,$trials,$verdicts,$ds,$cut,$at,$from,NULL,$reserve,$paper,$papersha,
                   $cost,$costsha);
            SELECT last_insert_rowid();
            """,
            ("$name", row.Name), ("$policy", row.ScoringPolicy), ("$sha", row.ScoringPolicySha256),
            ("$trials", row.TrialBudget), ("$verdicts", row.VerdictBudget),
            ("$ds", row.HoldoutDatasetId), ("$cut", Sql.T(row.HoldoutFrom)), ("$at", Sql.T(row.OpenedAt)),
            ("$from", row.RenewedFrom), ("$reserve", row.ExplorationBudget),
            ("$paper", row.PaperPolicy), ("$papersha", row.PaperPolicySha256),
            ("$cost", row.CostModelCanonical), ("$costsha", row.CostModelSha256));

        return row with { Id = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) };
    });

    static List<CampaignRow> Read(SqliteCommand c)
    {
        var rows = new List<CampaignRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new CampaignRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetInt32(5), r.GetInt64(6), Sql.Time(r.GetString(7)), Sql.Time(r.GetString(8)),
                r.IsDBNull(9) ? null : r.GetInt64(9),
                Sql.TimeN(r.IsDBNull(10) ? null : r.GetString(10)))
            {
                ExplorationBudget = r.GetInt32(11),
                PaperPolicy = r.GetString(12),
                PaperPolicySha256 = r.GetString(13),
                CostModelCanonical = r.IsDBNull(14) ? null : r.GetString(14),
                CostModelSha256 = r.IsDBNull(15) ? null : r.GetString(15)
            });
        return rows;
    }
}
