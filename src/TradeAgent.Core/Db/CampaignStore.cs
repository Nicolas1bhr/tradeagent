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
    DateTimeOffset RegisteredAt);

/// <summary>What registering a trial did: whether it was charged, and where the campaign now stands.</summary>
public sealed record TrialRegistered(bool Ok, string Why, bool Charged, int Spent, int Budget);

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
        "holdout_dataset_id, holdout_from, opened_at, renewed_from, closed_at";

    /// <summary>
    /// Opens the campaign for a dataset the owner has just held back, or refuses in words.
    ///
    /// <para>The budgets come from the caller — the app reads them off the owner's settings — and are
    /// COPIED onto the row, so a campaign's allowance is what it was opened with and not whatever the
    /// settings say today. A setting changed half way through a campaign would otherwise move the
    /// standard under evidence already collected, which is the same defect as an editable policy.</para>
    /// </summary>
    public CampaignOpened Open(string name, DatasetRecord holdout, int trialBudget, int verdictBudget,
        DateTimeOffset at, string? policy = null) => db.Write(_ =>
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
            holdout.Id, cutoff, at, null, null)));
    });

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
    public CampaignOpened Renew(long parentId, int trialBudget, int verdictBudget, DateTimeOffset at) =>
        db.Write(_ =>
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
                parent.HoldoutDatasetId, parent.HoldoutFrom, at, parent.Id, null)));
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

    /// <summary>How many CHARGED trials this campaign has registered. A fixture run is not among them.</summary>
    public int TrialsCharged(long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM strategy_trial WHERE campaign_id=$id AND charged=1", ("$id", campaignId));
        return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Every trial registered against this campaign, oldest first.</summary>
    public IReadOnlyList<TrialRow> Trials(long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT campaign_id, version_id, run_id, kind, charged, registered_at
            FROM strategy_trial WHERE campaign_id=$id ORDER BY registered_at, run_id
            """, ("$id", campaignId));

        var rows = new List<TrialRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new TrialRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4) != 0, Sql.Time(r.GetString(5))));
        return rows;
    });

    /// <summary>
    /// WHY THE NEXT RUN OF THIS KIND CANNOT BE REGISTERED, in words, or null because it can.
    ///
    /// <para>Asked BEFORE the run rather than after it, which is the whole point of having it separate
    /// from <see cref="RegisterTrial"/>: a caller told "your budget is spent" after twenty minutes of
    /// evaluation has spent the budget to learn that it was spent. A <c>fixture</c> run is never refused
    /// here — it is charged nothing, so there is nothing for a budget to refuse.</para>
    /// </summary>
    public string? TrialRefusal(long campaignId, string kind)
    {
        if (kind == EvaluationClass.Fixture) return null;
        if (ById(campaignId) is not { } campaign) return null;

        var spent = TrialsCharged(campaignId);
        if (spent < campaign.TrialBudget) return null;

        return $"campaign {campaignId} has registered all {campaign.TrialBudget} of its research trials, so "
            + "this run is refused before it is made. A trial is one registered run of one version over one "
            + "dataset under one execution model, and the count is the CAMPAIGN's: it is keyed by the "
            + "campaign, the version and the run and by nothing about who asked, so it is not reset by a "
            + "restart, by a fresh attempt or by a replacement team. What is left is a renewal, which the "
            + "account owner authorises in TradeAgent's own window and which carries this campaign's "
            + "holdout and its lineage forward. Runs over a fixture dataset are still free.";
    }

    /// <summary>
    /// REGISTERS ONE TRIAL, or leaves the row that is already there alone.
    ///
    /// <para><b>Idempotent on (campaign, version, run), and the FIRST registration stands.</b> All three
    /// are content hashes or the app's own id, so a re-request of the same program over the same bytes
    /// under the same model is the same trial and is not charged twice — and a restart, a fresh attempt
    /// or a new team asking the identical question inherits the count rather than starting one.</para>
    ///
    /// <para>It does not check the budget: <see cref="TrialRefusal"/> does that before the work. A run
    /// that has already been made is recorded whatever the budget says, because the alternative is a run
    /// the app measured and did not admit to.</para>
    /// </summary>
    public TrialRegistered RegisterTrial(long campaignId, string versionId, string runId, string kind,
        DateTimeOffset at) => db.Write(_ =>
    {
        if (ById(campaignId) is not { } campaign)
            return new TrialRegistered(false, $"there is no campaign {campaignId}.", false, 0, 0);

        var charged = kind != EvaluationClass.Fixture;

        using var c = db.Cmd("""
            INSERT INTO strategy_trial(campaign_id, version_id, run_id, kind, charged, registered_at)
            VALUES($id,$ver,$run,$kind,$charged,$at)
            ON CONFLICT(campaign_id, version_id, run_id) DO NOTHING
            """,
            ("$id", campaignId), ("$ver", versionId), ("$run", runId), ("$kind", kind),
            ("$charged", charged ? 1 : 0), ("$at", Sql.T(at)));
        c.ExecuteNonQuery();

        return new TrialRegistered(true, "", charged, TrialsCharged(campaignId), campaign.TrialBudget);
    });


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
                                          renewed_from, closed_at)
            VALUES($name,$policy,$sha,$trials,$verdicts,$ds,$cut,$at,$from,NULL);
            SELECT last_insert_rowid();
            """,
            ("$name", row.Name), ("$policy", row.ScoringPolicy), ("$sha", row.ScoringPolicySha256),
            ("$trials", row.TrialBudget), ("$verdicts", row.VerdictBudget),
            ("$ds", row.HoldoutDatasetId), ("$cut", Sql.T(row.HoldoutFrom)), ("$at", Sql.T(row.OpenedAt)),
            ("$from", row.RenewedFrom));

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
                Sql.TimeN(r.IsDBNull(10) ? null : r.GetString(10))));
        return rows;
    }
}
