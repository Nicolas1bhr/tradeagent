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
    /// <para><see cref="CampaignId"/> is the PEEK: which holdout's pre-cutoff bars this run read. This
    /// is the COST: which family of versions paid for it. They are the same number for a version that
    /// declared no parent, and they differ for a variant — <c>docs/COUNCIL.md</c>:201's "comparable
    /// trials". A child is charged to the campaign lineage its ancestry was already being charged to,
    /// so submitting a variant against a fresh holdout cannot buy a family an untouched trial budget
    /// by being a new hash.</para>
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
    /// The same row as <see cref="RunCampaign"/> for a version that declared no parent.
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
/// ONE VERDICT THAT WAS ASKED FOR. The row exists because the question was put, not because it was
/// answered: <c>docs/COUNCIL.md</c>:134 makes final evaluation scarce "because every verdict leaks", and a
/// budget checked after the holdout run has already let the leak happen.
///
/// <para><see cref="HoldoutFrom"/> is the cutoff as it stood when the verdict was charged. A cutoff can
/// later move LATER, so this is the record of what was private when the answer was taken.</para>
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

/// <summary>What charging a verdict did, and where the campaign's LINEAGE now stands.</summary>
public sealed record VerdictCharged(bool Ok, string Why, int Spent, int Budget);

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
        "exploration_budget";

    /// <summary>
    /// Opens the campaign for a dataset the owner has just held back, or refuses in words.
    ///
    /// <para>The budgets come from the caller — the app reads them off the owner's settings — and are
    /// COPIED onto the row, so a campaign's allowance is what it was opened with and not whatever the
    /// settings say today. A setting changed half way through a campaign would otherwise move the
    /// standard under evidence already collected, which is the same defect as an editable policy.</para>
    /// </summary>
    public CampaignOpened Open(string name, DatasetRecord holdout, int trialBudget, int verdictBudget,
        DateTimeOffset at, string? policy = null, int? exploration = null) => db.Write(_ =>
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

        var text = policy ?? CampaignPolicy.V1;
        return CampaignOpened.Yes(Insert(new CampaignRow(
            0, name, text, CampaignPolicy.Sha256Of(text), Budget(trialBudget), Budget(verdictBudget),
            holdout.Id, cutoff, at, null, null)
        {
            ExplorationBudget = Reserve(exploration, Budget(trialBudget))
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
    /// policy text and its sha, and <c>renewed_from</c>. So renewal buys trials and nothing else — not a
    /// new standard, not a different holdout, and not untouched holdout access, because
    /// <c>Referee.RequestVerdict</c> counts verdicts across the whole lineage.</para>
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
                ExplorationBudget = Reserve(exploration, Budget(trialBudget))
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
    /// HOW MANY CHARGED TRIALS THIS CAMPAIGN HAS REGISTERED. A fixture run is not among them.
    ///
    /// <para>A trial counts here when this campaign was the PEEK (<c>campaign_id</c>) or when it was
    /// the COST (<c>charged_to</c>) — a variant run over another holdout, charged back to the campaign
    /// lineage its ancestry was already being charged to. A row that is both counts once, which is what
    /// <c>COUNT(*)</c> over an OR does and what every version that declared no parent produces.</para>
    /// </summary>
    public int TrialsCharged(long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM strategy_trial WHERE charged=1 AND (campaign_id=$id OR charged_to=$id)",
            ("$id", campaignId));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// How many charged trials of ONE POT this campaign has registered — the exploration reserve, or
    /// the rest of the trial budget. The two together are <see cref="TrialsCharged(long)"/>.
    /// </summary>
    public int TrialsCharged(long campaignId, bool exploration) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM strategy_trial WHERE charged=1 AND exploration=$e "
            + "AND (campaign_id=$id OR charged_to=$id)",
            ("$id", campaignId), ("$e", exploration ? 1 : 0));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

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
    /// </summary>
    public string? TrialRefusal(long campaignId, string versionId, string kind)
    {
        if (kind == EvaluationClass.Fixture) return null;
        if (ById(campaignId) is null) return null;

        var rule = Admission(campaignId, versionId, kind);
        return Refusal(rule, Counts(rule), made: false);
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
    /// cheap honest answer, and by the GATE inside the write, where nothing can move them.
    /// </summary>
    TrialCounts Counts(TrialAdmission rule) => new(
        rule.RunCampaign is { } run ? TrialsCharged(run.Id) : 0,
        rule.RunCampaign is { } runPot ? TrialsCharged(runPot.Id, rule.Exploration) : 0,
        rule.HomeCampaign is { } home ? TrialsCharged(home.Id) : 0,
        rule.HomeCampaign is { } homePot ? TrialsCharged(homePot.Id, rule.Exploration) : 0);

    /// <summary>
    /// WHY THIS TRIAL WOULD NOT BE REGISTERED, in words, or null because it would. One rule, both
    /// callers; <paramref name="made"/> only chooses the tense of the sentence.
    /// </summary>
    static string? Refusal(TrialAdmission rule, TrialCounts spent, bool made)
    {
        if (!rule.Charged || rule.RunCampaign is not { } run) return null;

        if (spent.Run >= run.TrialBudget) return Spent(run.Id, run.TrialBudget, made);

        // THE POT, AND IT IS READ AT THE GATE. A campaign's trial budget is two budgets: the reserve
        // kept for versions with no promoted parent, and the rest. Both ceilings are inside the
        // transaction that writes the row, because a reservation taken outside it is the look
        // `U-referee-1` named — two roles both honestly told there is room, and both taking it.
        if (spent.RunPot >= rule.PotOf(run)) return Pot(rule, run, made);

        // AND THE FAMILY'S OWN CAMPAIGN, which is a second ceiling and never a looser one: a variant
        // run against a fresh holdout is charged back to the campaign lineage its ancestry is already
        // being charged to (`docs/COUNCIL.md`:201, comparable trials).
        if (rule.HomeCampaign is not { } home || rule.OneCampaign) return null;

        if (spent.Home >= home.TrialBudget) return Spent(home.Id, home.TrialBudget, made) + Elsewhere;

        return spent.HomePot >= rule.PotOf(home) ? Pot(rule, home, made) + Elsewhere : null;
    }

    /// <summary>The sentence a spent POT answers with, naming which of the two had no room.</summary>
    static string Pot(TrialAdmission rule, CampaignRow campaign, bool made) =>
        $"campaign {campaign.Id} has registered all {rule.PotOf(campaign)} of the trials it reserves for "
        + (rule.Exploration
            ? "EXPLORATION — versions that declare no parent, or one nothing has promoted — so "
            : "REFINEMENT — versions whose declared parent is promoted — so ")
        + (made ? "this run is not recorded and its result is not served. " : "this run is refused before it is made. ")
        + $"The reserve is {campaign.ExplorationBudget} of this campaign's {campaign.TrialBudget} trials "
        + $"for exploration and {campaign.RefinementBudget} for refinement, fixed when the campaign "
        + "opened. The two sum to the trial budget, so declaring a parent moves a trial from one pot to "
        + "the other and creates no allowance: what is left is a renewal, which the account owner "
        + "authorises in TradeAgent's own window.";

    /// <summary>The clause a refusal adds when the budget that had no room was the family's, not this run's.</summary>
    const string Elsewhere =
        " This run is over another holdout and is charged there as well as here, because the version it "
        + "runs declares a parent: a variant is charged to the campaign lineage its ancestry is already "
        + "being charged to, so a family cannot buy itself an untouched trial budget by submitting a new "
        + "hash against a second dataset.";

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
    /// </summary>
    public TrialRegistered RegisterTrial(long campaignId, string versionId, string runId, string kind,
        DateTimeOffset at) => db.Write(_ =>
    {
        if (ById(campaignId) is not { } campaign)
            return new TrialRegistered(false, $"there is no campaign {campaignId}.", false, 0, 0);

        var charged = kind != EvaluationClass.Fixture;

        // THE RULE AND ITS COUNTS, BOTH READ HERE. The rule is the same one `TrialRefusal` asked before
        // the run; the counts are the part another caller can move in between, so they are read inside
        // this transaction and nowhere else. That is `AiAttemptStore.Begin`'s shape and it is the whole
        // of the guard.
        var rule = Admission(campaignId, versionId, kind);
        var spent = Counts(rule);
        var chargedTo = rule.HomeCampaign?.Id ?? campaignId;

        // ALREADY REGISTERED: the same question, asked again. The first row stands and the answer is Ok
        // whatever the budget now says — see the doc comment.
        if (Registered(campaignId, versionId, runId))
            return new TrialRegistered(true, "", charged, spent.Run, campaign.TrialBudget);

        // THE GATE, AND IT IS THIS TRANSACTION'S OWN READING RATHER THAN THE CALLER'S.
        if (Refusal(rule, spent, made: true) is { } why)
            return new TrialRegistered(false, why, false, spent.Run, campaign.TrialBudget);

        using var c = db.Cmd("""
            INSERT INTO strategy_trial(campaign_id, version_id, run_id, kind, charged, registered_at,
                                       charged_to, exploration)
            VALUES($id,$ver,$run,$kind,$charged,$at,$home,$explore)
            ON CONFLICT(campaign_id, version_id, run_id) DO NOTHING
            """,
            ("$id", campaignId), ("$ver", versionId), ("$run", runId), ("$kind", kind),
            ("$charged", charged ? 1 : 0), ("$at", Sql.T(at)),
            // THE COST, BESIDE THE PEEK. They are one number for a version that declared no parent.
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
    static string Spent(long campaignId, int budget, bool made) =>
        $"campaign {campaignId} has registered all {budget} of its research trials, so "
        + (made
            ? "this run is not recorded and its result is not served: the count is charged in the same "
              + "transaction that reads it, so two roles asking for the last trial at once cannot both "
              + "take it. "
            : "this run is refused before it is made. ")
        + "A trial is one registered run of one version over one "
        + "dataset under one execution model, and the count is the CAMPAIGN's: it is keyed by the "
        + "campaign, the version and the run and by nothing about who asked, so it is not reset by a "
        + "restart, by a fresh attempt or by a replacement team. What is left is a renewal, which the "
        + "account owner authorises in TradeAgent's own window and which carries this campaign's "
        + "holdout and its lineage forward. Runs over a fixture dataset are still free.";


    // ---- verdicts ---------------------------------------------------------------------------------

    /// <summary>
    /// HOW MANY VERDICTS HAVE BEEN CHARGED ACROSS THIS CAMPAIGN'S WHOLE RENEWAL LINEAGE.
    ///
    /// <para>The lineage and not the campaign, and that is the point of <c>renewed_from</c>: renewal buys
    /// ATTEMPTS, and it must not buy holdout access (<c>docs/COUNCIL.md</c>:132, "campaign renewal
    /// authorised by code so no new campaign resets holdout access"). Every verdict already taken read the
    /// same months, so every verdict already taken still counts.</para>
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

        var spent = VerdictsInLineage(campaignId);

        if (VerdictCharged(campaignId, versionId))
            return new VerdictCharged(true, "", spent, campaign.VerdictBudget);

        if (!campaign.IsOpen)
            return new VerdictCharged(false,
                $"campaign {campaignId} closed at {campaign.ClosedAt:u} and takes no further verdict. Its "
                + "renewal is the campaign that does, and it carries this one's holdout and its verdicts.",
                spent, campaign.VerdictBudget);

        if (spent >= campaign.VerdictBudget)
            return new VerdictCharged(false,
                $"this campaign's lineage has spent all {campaign.VerdictBudget} of its final judgements, so "
                + "this one is refused BEFORE anything is computed over the held-back bars. Every verdict "
                + "leaks: its answer tells the research process something about months it was never shown, "
                + "which is why the number is small and why it is counted across every renewal of this "
                + "campaign rather than per campaign — a renewal buys attempts and never holdout access. "
                + "What is left is a new holdout, which the account owner sets in TradeAgent's own window.",
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

    /// <summary>A short list of campaign ids for an IN clause. They are longs this code read itself.</summary>
    static string Ids(IReadOnlyList<long> ids) =>
        string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

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
                                          renewed_from, closed_at, exploration_budget)
            VALUES($name,$policy,$sha,$trials,$verdicts,$ds,$cut,$at,$from,NULL,$reserve);
            SELECT last_insert_rowid();
            """,
            ("$name", row.Name), ("$policy", row.ScoringPolicy), ("$sha", row.ScoringPolicySha256),
            ("$trials", row.TrialBudget), ("$verdicts", row.VerdictBudget),
            ("$ds", row.HoldoutDatasetId), ("$cut", Sql.T(row.HoldoutFrom)), ("$at", Sql.T(row.OpenedAt)),
            ("$from", row.RenewedFrom), ("$reserve", row.ExplorationBudget));

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
                ExplorationBudget = r.GetInt32(11)
            });
        return rows;
    }
}
