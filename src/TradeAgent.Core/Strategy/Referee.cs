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
public sealed class Referee(Database db, Func<DateTimeOffset>? now = null,
    CouncilBoundaries? boundaries = null)
{
    readonly CampaignStore _campaigns = new(db);
    readonly CouncilBoundaries _boundaries = boundaries ?? new CouncilBoundaries(db);
    readonly DatasetStore _datasets = new(db);
    readonly StrategyStore _strategies = new(db);
    readonly Promotions _promotions = new(db);
    readonly PublicationStore _publications = new(db);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The promotion ledger this referee writes. Read-only for a caller: it has one writer.</summary>
    public Promotions Promotions => _promotions;

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

    /// <summary>An id as it is printed for a person. The same twelve characters everything else uses.</summary>
    static string Short(string id) => id.Length <= 12 ? id : id[..12];

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
    /// THE VERDICT ITSELF: THE APP RUNS THE VERSION OVER THE HELD-BACK MONTHS AND SCORES IT.
    ///
    /// <para><b>Every step of this is the app's.</b> The charge is taken first (<see cref="RequestVerdict"/>,
    /// which is what opens the door at all), the program is the text this installation parsed and
    /// recorded, the bars are the campaign's own holdout read through the one in-process audience, the
    /// figures are computed from the app's own trace, and the clauses applied to them are
    /// <see cref="ScoringPolicyV1"/> — code, bound to the campaign's fixed policy TEXT by its sha. No
    /// model is asked anything and no role has an opinion: <c>docs/COUNCIL.md</c>:55-57, the referee is
    /// code and never a role.</para>
    ///
    /// <para><b>The run is recorded and is NOT a research trial.</b> It goes in <c>strategy_run</c> with
    /// its trace hash and its metrics, under <see cref="RunRole"/>, and nothing charges it against the
    /// campaign's trial budget: a trial is a peek the research process asked for, and this is the
    /// evaluation the verdict budget already paid for. Charging it as research would spend the
    /// submitter's allowance on the referee's own work — the mutant this method was built against.</para>
    ///
    /// <para><b>The execution model is the JUDGE'S, not the submission's.</b> It is a parameter of this
    /// call — the owner's, in-process — and it is hashed into the promotion, so a verdict taken under
    /// one declaration cannot be read as a verdict under another. The default is
    /// <see cref="ExecutionModel.Frictionless"/>, which invents no fee nobody measured and says so on
    /// the record it writes.</para>
    ///
    /// <para><b>A refusal is a VERDICT, and a failure is not.</b> A version that does not meet the
    /// policy gets a recorded <c>refused</c> promotion with its reason class — that is an answer, and it
    /// is what the budget was spent on. A referee that could not judge at all (no charge, no such
    /// campaign, a policy this build does not implement, a dataset that serves nothing, a program that
    /// declares none of the three execution bounds) writes no row and says why in
    /// <see cref="RefereeVerdict.Why"/>.</para>
    /// </summary>
    public RefereeVerdict Verdict(string versionId, long campaignId, ExecutionModel? model = null,
        CancellationToken stop = default)
    {
        // NO EXECUTION BOUNDS, NO PROMOTION — asked before anything else, and answered off the text
        // this installation already holds. See `BoundsRefusal`.
        if (BoundsRefusal(versionId) is { } unbounded) return RefereeVerdict.No(unbounded);

        // THE CHARGE COMES FIRST AND IT IS WHAT PRODUCES THE AUDIENCE. Nothing below can read a
        // held-back bar without it, because the audience is on the charge and is internal to Core.
        var charge = RequestVerdict(versionId, campaignId);
        if (charge.Audience is not { } audience)
            return RefereeVerdict.No($"no verdict was authorised, so nothing was computed: {charge.Why}");

        if (_campaigns.ById(campaignId) is not { } campaign)
            return RefereeVerdict.No($"there is no campaign {campaignId} in this installation's ledger.");

        if (_strategies.VersionById(versionId) is not { } version)
            return RefereeVerdict.No($"this installation has never accepted a version {versionId}.");

        // THE CODE THAT SCORES IS BOUND TO THE TEXT THAT WAS FIXED. A campaign whose policy is not the
        // one this build implements is refused rather than judged by a standard nobody agreed to: the
        // whole value of fixing a policy before a campaign is that the criteria were settled before the
        // outcome was known (docs/COUNCIL.md:212), and applying different clauses under its sha would
        // hollow that out silently.
        var policySha = CampaignPolicy.Sha256Of(CampaignPolicy.V1);
        if (!string.Equals(campaign.ScoringPolicySha256, policySha, StringComparison.OrdinalIgnoreCase))
            return RefereeVerdict.No(
                $"campaign {campaignId} fixed scoring policy {campaign.ScoringPolicySha256} at open and "
                + $"this build implements {policySha}. TradeAgent will not judge evidence by a standard "
                + "other than the one this campaign precommitted to.");

        var parse = StrategyParser.Parse(version.Source);
        if (parse.Program is not { } program)
            return RefereeVerdict.No(
                $"the recorded source of version {versionId} no longer parses — {parse.Why}");

        if (!string.Equals(program.StrategyId, version.Id, StringComparison.Ordinal))
            return RefereeVerdict.No(
                $"the recorded source of version {versionId} parses to {program.StrategyId}, which is a "
                + "different program. TradeAgent judges the program the id names and nothing else.");

        var judged = model ?? ExecutionModel.Frictionless;

        // THE HOLDOUT WINDOW IS THE CAMPAIGN'S OWN: everything from its cutoff onwards, and no `to`,
        // because a verdict wants the whole of the data the research process never saw. The dataset is
        // the campaign's `holdout_dataset_id` and not a number anybody passed in.
        var open = Backtest.Over(_datasets, campaign.HoldoutDatasetId, program, judged, audience,
            campaign.HoldoutFrom, null, stop: stop);

        if (open.Result is not { } run)
            return RefereeVerdict.No(
                $"the holdout of campaign {campaignId} could not be run: {open.Why}");

        var at = _now();

        // THE CAMPAIGN'S PRECOMMITTED POLICY IS ASKED FIRST AND IS UNCHANGED. Its answer decides
        // everything below, including whether a second standard is asked at all.
        var reason = ScoringPolicyV1.Reason(run, version.CreatedAt);
        var answer = reason == PromotionReason.Met ? PromotionVerdict.Promoted : PromotionVerdict.Refused;
        var scoredBy = campaign.ScoringPolicySha256;

        // THE PAPER ARM — `docs/PRINCIPLES.md` § Evidence, and it is entered on ONE answer only.
        //
        // "Forward paper evidence cannot be required before the very first paper run that produces
        // it." Under `ScoringPolicyV1` alone, a version frozen after the cutoff can be told nothing but
        // `evidence-precedes-the-freeze` — so the figures are never reached, the loop has no favourable
        // historical verdict to act on, and the first paper run is unreachable. Here the SAME RUN is
        // scored again, by `CampaignPolicy.PaperV1`, whose clauses are V1's performance clauses and
        // whose forward-evidence clause is deliberately absent.
        //
        // THE CLAUSES ARE REALLY EVALUATED — the mutant is mapping the freeze refusal straight to
        // paper-eligible, which makes a program that LOSES money over the held-back months eligible on
        // the strength of its date. A failure here is refused with the PERFORMANCE clause that failed,
        // because that is the informative answer: the freeze is a fact about this arm's existence and
        // tells the submitter nothing it can act on.
        //
        // AND THE ROW CARRIES THE SHA OF THE POLICY THAT PRODUCED THE ANSWER, which the campaign is
        // required to hold — a verdict recorded under the sha of a standard it was not judged by would
        // read, to `Promotions.Standing` and to every later reader, as having met a standard it never
        // faced.
        if (reason == PromotionReason.PrecedesTheFreeze)
        {
            var paperSha = CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1);

            if (!string.Equals(campaign.PaperPolicySha256, paperSha, StringComparison.OrdinalIgnoreCase))
                return RefereeVerdict.No(
                    $"campaign {campaignId} fixed paper policy {campaign.PaperPolicySha256} at open and "
                    + $"this build implements {paperSha}. TradeAgent will not judge historical holdout "
                    + "evidence by a standard other than the one this campaign precommitted to, so no "
                    + "verdict was recorded.");

            reason = PaperPolicyV1.Reason(run);

            if (reason == PromotionReason.MetOnHistory)
            {
                // ONLY A FAVOURABLE PAPER VERDICT IS RECORDED UNDER THE PAPER STANDARD. A REFUSAL down
                // this arm keeps the campaign's own precommitted sha, and that is not a shortcut: the
                // three clauses it failed are `ScoringPolicyV1`'s performance clauses word for word, so
                // `refused / not-profitable-after-costs` is a true and complete statement under V1 —
                // while a refusal stamped with the paper sha would make `Promotions.Standing` unable to
                // tell which standard to re-check it against, the two arms producing the same three
                // reason classes. Only the verdict that CONFERS something needs its own standard named
                // on the row, and it is the only one that gets it.
                answer = PromotionVerdict.PaperEligible;
                scoredBy = paperSha;
            }
            else
            {
                answer = PromotionVerdict.Refused;
            }
        }

        return db.Write(_ =>
        {
            // THE RUN, WITH ITS TRACE HASH AND ITS FIGURES, UNDER THE REFEREE'S OWN MARK — and NO
            // trial. `Backtests.Record` registers one for every research run in the same transaction;
            // this deliberately does not, and a test holds that.
            _strategies.RecordRun(new StrategyRunRow(
                run.RunId, run.VersionId, run.Request.DatasetId, run.Request.DatasetSha256,
                run.Request.From, run.Request.To, run.Request.Model.Canonical,
                run.Outcome.ToString(), run.FaultReason,
                run.Metrics.Bars, run.Metrics.Trades, run.Metrics.Wins, run.Metrics.Signals,
                run.Metrics.Fills, run.Metrics.ExposureBars, run.Metrics.MissingMinutes,
                run.Metrics.Faults, run.Metrics.GrossPnl, run.Metrics.Fees, run.Metrics.NetPnl,
                run.Metrics.MaxDrawdown, run.Trace.Sha256, at, RunRole, null),
                [.. run.Trades.Select(t => new StrategyTradeRow(
                    run.RunId, t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice,
                    t.Quantity, t.Reason.ToString(), t.Fees, t.Pnl))]);

            var promotion = _promotions.Record(new PromotionRow(
                "", version.Id, campaign.Id, scoredBy, StrategyStore.InterpreterBuild,
                run.Request.DatasetId, run.Request.DatasetSha256, run.Request.Model.Canonical,
                EvaluatorVersion, run.RunId, answer, reason, at)
            {
                // THE BOUNDS COME OFF `program`, WHICH IS THE FROZEN PROGRAM THIS METHOD JUST PARSED
                // AND JUST PROVED HASHES TO `version.Id` — never off `version`, whose columns are a
                // restatement somebody else wrote, and never off a parameter of this call.
                //
                // `docs/COUNCIL.md`:35: a changed assumption invalidates the evidence that rested on
                // it. A promotion that took its bounds from anywhere but the text it judged would be
                // a verdict about a program nobody submitted — and because the bounds are hashed into
                // the version id, the two can only disagree when the row is wrong.
                Timeframe = program.Freshness?.Timeframe,
                DataFreshness = program.Freshness?.DataFreshness,
                MaxDecisionAge = program.Freshness?.MaxDecisionAge
            });

Deliver(promotion, at);
            OpenBoundary(promotion, at);

            return new RefereeVerdict(true, "", promotion);
        });
    }

    /// <summary>
    /// WHY THIS VERSION CANNOT BE JUDGED AT ALL, or null because it can: the three execution bounds,
    /// asked of the FROZEN PROGRAM before a verdict is charged.
    ///
    /// <para><b>The rule.</b> <c>docs/COUNCIL.md</c>:96-97 — "a promoted strategy declares its
    /// timeframe, its required data freshness and its maximum decision age, and the runner checks them
    /// again when the intent reaches execution". The word in that sentence is PROMOTED, and the three
    /// declarations are OPTIONAL in the language (`U-freshness`, all three or none), so this is the
    /// place the sentence becomes true. A program declaring none emits an intent with no
    /// <c>IntentDecision</c> on it and <c>RefuseAStaleDecisionOrThrow</c> has nothing to refuse on —
    /// every order it decided would be sent however old the bars behind it were.</para>
    ///
    /// <para><b>Before the charge, and it is the one check that is.</b> The answer is in the source
    /// text this installation already recorded; reading the held-back months cannot change it. Charging
    /// the scarcest budget in the product for it would spend the submitter's allowance on a fact three
    /// lines of its own program would have settled — the reason <see cref="RequestVerdict"/> refuses an
    /// unregistered version without charging either.</para>
    ///
    /// <para><b>It refuses to JUDGE, so no promotion row is written — not even a refusal.</b> A
    /// recorded <c>refused</c> is a verdict about EVIDENCE: the budget was spent, the holdout was read,
    /// and the version lost on the figures. This one is about the submission, it costs no holdout
    /// access, and writing it as a verdict would put a permanent row in the ledger saying this program's
    /// evidence was judged and found wanting when it never was.</para>
    ///
    /// <para><b>A source that does not parse falls through</b> to <see cref="Verdict"/>'s own refusal,
    /// which says so in its own words; and a source that parses to a DIFFERENT program is refused there
    /// too, by the hash check. Neither can reach a promotion through here: this method only ever
    /// refuses, so the worst a tampered row can buy is a refusal it would have got anyway.</para>
    /// </summary>
    string? BoundsRefusal(string versionId)
    {
        if (_strategies.VersionById(versionId) is not { } version) return null;
        if (StrategyParser.Parse(version.Source).Program is not { Freshness: null }) return null;

        return $"version {Short(versionId)} declares no `timeframe`, no `data_freshness` and no "
            + "`max_decision_age`, so TradeAgent will not judge it: docs/COUNCIL.md:96-97 — a promoted "
            + "strategy declares its timeframe, its required data freshness and its maximum decision "
            + "age, and the runner checks them again when the intent reaches execution. A program that "
            + "declares none of the three decides with no bounds on it at all, so the gate at dispatch "
            + "has nothing to refuse a stale signal on and every order it decided would be sent however "
            + "old the bars behind it were. Declare all three and submit the program again — that is a "
            + "different text and a different id, and it gets its own evidence. No verdict was charged "
            + "and no promotion was recorded.";
    }

    /// <summary>
    /// TELLS THE TEAM, IN THE SAME TRANSACTION AS THE VERDICT, AND TELLS IT ONLY WHAT THE POLICY ALLOWS.
    ///
    /// <para>One publication through <c>PublicationStore.Commit</c> — the app's one committed transition
    /// for an artifact, its delivery and the single paid turn that reads it — and the wake is keyed by
    /// the PROMOTION, so re-delivering one judgement buys nobody a second turn
    /// (<c>docs/COUNCIL.md</c>:64).</para>
    ///
    /// <para><b>What crosses is the verdict and the reason class.</b> No metric, no trace, no figure at
    /// all: :196-197 keeps private evaluation disclosures referee-budgeted, and a note carrying the
    /// holdout's net result would hand the research process the months it was never shown, laundered
    /// through a sentence. <see cref="RefereeFeedback"/> is the only thing that decides what is in it,
    /// and a test reads every figure of the run back out of the database and asserts none of them is in
    /// the text.</para>
    ///
    /// <para>It goes to Research, which is the role that submits versions. Operations is not a recipient:
    /// the chair reads the owner's report, where section 8 lists every promotion, refusal and
    /// invalidation — and an extra recipient is an extra paid turn for a fact the app already wrote
    /// down.</para>
    /// </summary>
    void Deliver(PromotionRow promotion, DateTimeOffset at)
    {
        var content = RefereeFeedback.Text(promotion);

        _publications.Commit(new Publication
        {
            Id = Publication.IdOf(RunRole, PublicationKind.Verdict, content),
            Role = RunRole,
            Kind = PublicationKind.Verdict,
            Recipients = CouncilRoles.Research,
            Classification = PublicationClass.Council,
            CreatedAt = at,
            Content = content
        }, at, MissionEventIds.Verdict(promotion.Id));
    }

    /// <summary>
    /// OPENS THE CONSEQUENTIAL BOUNDARY THIS VERDICT IS, in the same transaction as the verdict itself.
    ///
    /// <para><c>docs/COUNCIL.md</c>:59 lists "promotion of a strategy" first among the boundaries the
    /// strongest model is spent at. THE ENTITY IS THE VERSION AND THE REVISION IS THE CAMPAIGN, which is
    /// :64's "deduplicate boundary events by entity and revision" read literally: asking for the same
    /// version's verdict twice under one campaign is one question and buys one boundary, and the same
    /// version judged again under a RENEWED campaign is a different question about different evidence
    /// and is worth the spend.</para>
    ///
    /// <para><b>The default is the POLICY'S answer, computed now and frozen on the row.</b> A promotion
    /// that stands deploys; anything else — refused, or promoted on evidence that has since been
    /// invalidated — holds. That is :62, "code applies the promotion and allocation policy so neither
    /// director can veto an eligible deployment forever": the directors are asked, and the answer they
    /// are asked about is already written down where they cannot reach it.</para>
    ///
    /// <para><b>The evidence carries no figure</b>, for the reason <see cref="RefereeFeedback"/> carries
    /// none: it is rendered into both directors' Situations and into the owner's report, which
    /// <c>trade report</c> serves to an agent verbatim. The verdict, the reason CLASS and two hashes.</para>
    /// </summary>
    void OpenBoundary(PromotionRow promotion, DateTimeOffset at) =>
        _boundaries.Open(
            BoundaryKind.Promotion, promotion.VersionId, promotion.CampaignId,
            _promotions.Standing(promotion.VersionId).IsPromoted
                ? BoundaryDisposition.Deploy
                : BoundaryDisposition.Hold,
            $"TradeAgent's referee {promotion.Verdict} version {Short(promotion.VersionId)} under campaign "
            + $"{promotion.CampaignId} on holdout run {Short(promotion.HoldoutRunId)}: "
            + PromotionReason.Words(promotion.Reason),
            at);

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

/// <summary>
/// WHAT THE REFEREE ANSWERED, OR WHY IT COULD NOT ANSWER AT ALL.
///
/// <para>The two are different and are kept apart. <see cref="Ok"/> with a <c>refused</c>
/// <see cref="Promotion"/> is a VERDICT: the budget was spent, the holdout was read, and the answer was
/// no — recorded, immutable, and delivered like any other. <see cref="Ok"/> false is the referee
/// declining to judge — no charge, no such campaign, a scoring policy this build does not implement, a
/// dataset that serves nothing, a program that declares none of the three execution bounds — and
/// nothing is written.</para>
///
/// <para><b>The figures are not on here.</b> A caller gets the promotion and the run's id; the trace and
/// the metrics stay in <c>strategy_run</c>, which is the owner's table. <c>docs/COUNCIL.md</c>:196-197
/// keeps private evaluation disclosures referee-budgeted, and a return value that carried the holdout's
/// net result would put them one careless caller away from a publication.</para>
/// </summary>
public sealed record RefereeVerdict(bool Ok, string Why, PromotionRow? Promotion)
{
    /// <summary>Whether the version was promoted. False for a refusal AND for a failure to judge.</summary>
    public bool Promoted => Promotion is { IsPromoted: true };

    /// <summary>
    /// Whether the version may now be observed forward on paper. Disjoint from <see cref="Promoted"/>,
    /// and never a weaker way of asking it: nothing on the money path reads this.
    /// </summary>
    public bool PaperEligible => Promotion is { IsPaperEligible: true };

    /// <summary>The holdout run this verdict was computed from, or null where there was none.</summary>
    public string? RunId => Promotion?.HoldoutRunId;

    internal static RefereeVerdict No(string why) => new(false, why, null);
}

/// <summary>
/// THE SCORING POLICY, AS CODE — <see cref="CampaignPolicy.V1"/> in clauses, applied in this order.
///
/// <para><b>It is bound to the text by the sha on the campaign row.</b> <see cref="Referee.Verdict"/>
/// refuses to judge a campaign whose fixed policy is not the one this build implements, so a rewrite of
/// the words is a rewrite of the standard and cannot be applied to evidence collected under the old one
/// (<c>docs/COUNCIL.md</c>:212, precommitment).</para>
///
/// <para><b>Forward evidence is the FIRST clause, before any figure is looked at.</b> "Because public
/// history may already be known or hard-coded into a submission, forward evidence collected after the
/// strategy's freeze is required before capital" (:135-136). A result over months that predate the
/// freeze is not weak evidence to be weighed against the rest — it is not evidence at all, so it is
/// refused whatever the figures say.</para>
///
/// <para><b>Every clause answers a REASON CLASS and never a number.</b> The class is the only thing that
/// crosses back to the team that submitted the version, and a vocabulary cannot leak a metric.</para>
/// </summary>
public static class ScoringPolicyV1
{
    /// <summary>
    /// WHICH CLAUSE THIS RUN LANDS ON: <see cref="PromotionReason.Met"/> when it passes them all.
    ///
    /// <para><paramref name="frozenAt"/> is the version's <c>created_at</c> — when this installation
    /// accepted and hashed the program. The comparison is against the WINDOW the run covered and never
    /// against when the run was made: a run recorded today over last year's bars is not forward
    /// evidence, and comparing the run's own timestamp is the mutant this clause was built against.</para>
    /// </summary>
    public static string Reason(BacktestResult run, DateTimeOffset frozenAt)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Request.From is not { } from || from <= frozenAt) return PromotionReason.PrecedesTheFreeze;
        if (run.Faulted) return PromotionReason.DidNotComplete;
        if (run.Metrics.Trades <= 0) return PromotionReason.NoTrade;
        if (run.Metrics.NetPnl is not { } net || net <= 0m) return PromotionReason.NotProfitable;

        return PromotionReason.Met;
    }
}

/// <summary>
/// THE PAPER POLICY, AS CODE — <see cref="CampaignPolicy.PaperV1"/> in clauses, applied in this order.
///
/// <para><b>It is <see cref="ScoringPolicyV1"/>'s performance clauses and nothing else.</b> The same
/// three questions, in the same order, over the same run: did it complete, did it close a trade, did
/// it come out ahead after its declared costs. What is missing is the forward-evidence clause, and
/// only because this policy is asked ONLY of a run <see cref="ScoringPolicyV1"/> has already refused
/// on exactly that clause — so nothing here loosens anything: a version that could have been promoted
/// never reaches this code at all.</para>
///
/// <para><b>What it can confer is eligibility for paper observation and never capital.</b>
/// <c>docs/PRINCIPLES.md</c> § Evidence keeps the four meanings apart, and the live allocation path
/// refuses a paper-eligible version by name. A relaxation applied under the campaign's own precommitted
/// sha would be the mutant; this is a SEPARATE text with a separate hash, and the hash of whichever
/// policy produced a verdict is what that verdict's row carries.</para>
///
/// <para><b>Every clause answers a REASON CLASS and never a number</b>, for the reason
/// <see cref="ScoringPolicyV1"/>'s do: the class is the only thing that crosses back to the team that
/// submitted the version.</para>
/// </summary>
public static class PaperPolicyV1
{
    /// <summary>
    /// WHICH CLAUSE THIS RUN LANDS ON: <see cref="PromotionReason.MetOnHistory"/> when it passes them
    /// all. There is no <c>frozenAt</c> parameter and that is the whole difference — the freeze has
    /// already been asked and already answered, and asking it again here could only produce the
    /// refusal that sent the run down this arm.
    /// </summary>
    public static string Reason(BacktestResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Faulted) return PromotionReason.DidNotComplete;
        if (run.Metrics.Trades <= 0) return PromotionReason.NoTrade;
        if (run.Metrics.NetPnl is not { } net || net <= 0m) return PromotionReason.NotProfitable;

        return PromotionReason.MetOnHistory;
    }
}

/// <summary>
/// WHAT THE TEAM IS TOLD ABOUT A VERDICT — the whole of it, in one place, so that what crosses the
/// boundary is a decision somebody made rather than whatever a caller happened to pass.
///
/// <para><b>The verdict and the reason CLASS, and nothing else.</b> <c>docs/COUNCIL.md</c>:196-197:
/// "Teams receive their operational record and actionable feedback; private evaluation disclosures
/// remain referee-budgeted." A metric computed over the held-back months is exactly such a disclosure —
/// telling Research that the holdout returned 4.2% tells it something about months it was never shown,
/// and it cannot be untold (:212). So this function reads ONLY the promotion row, whose reason column is
/// a closed vocabulary that cannot hold a figure.</para>
///
/// <para>It is a pure function of the promotion, which is also what makes the delivery idempotent: the
/// publication's id is the hash of this text, so one judgement is one artifact however many times it is
/// committed.</para>
/// </summary>
public static class RefereeFeedback
{
    public static string Text(PromotionRow promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);

        return $"""
            # TradeAgent's referee has judged version {promotion.VersionId}

            The app ran this version over the months campaign {promotion.CampaignId} holds back from
            research, scored it by the policy that campaign fixed before the work, and recorded the
            answer as promotion {promotion.Id}.

            - verdict: {promotion.Verdict}
            - reason: {promotion.Reason}
            - what that means: {PromotionReason.Words(promotion.Reason)}
            {PaperParagraph(promotion)}
            No figure from those months is in this note and none ever will be. The run's metrics and its
            trace are the account owner's private evaluation evidence; what you may have is the verdict
            and the reason, and a verdict is charged against a small budget, so there are few of them.
            You cannot ask for one: TradeAgent decides when a version is judged.
            """;
    }

    /// <summary>
    /// WHAT PAPER-ELIGIBLE MEANS, IN WORDS, AND WHY IT IS NOT A PROMOTION — or an empty line, because
    /// this verdict is one of the other two.
    ///
    /// <para>It is here rather than left to the reason class because the class is a word and the
    /// distinction is a rule: the research process that reads this has to know that a favourable
    /// verdict has just been recorded AND that no capital can follow from it, or it will plan a
    /// deployment the gateway refuses. <c>docs/PRINCIPLES.md</c> § Evidence asks for exactly that
    /// separation of meanings.</para>
    ///
    /// <para><b>It carries no figure, like everything else that crosses.</b> It is a function of the
    /// promotion's VERDICT alone — it reads no metric, and there is no number in the text at all, so
    /// the test that counts the digits in this note is the same test either way.</para>
    /// </summary>
    static string PaperParagraph(PromotionRow promotion) => !promotion.IsPaperEligible ? "" : """

        This verdict is PAPER-ELIGIBLE and it is not promoted. The held-back months it was measured
        over do not post-date this version's freeze, so they are history the submission may already
        have been written around, and no result over them can be evidence for the account owner's
        capital. What it does buy is eligibility for paper observation: an ordinary experiment, run
        forward, on simulated money. Only forward evidence collected after the freeze can promote a
        version, and only the account owner allocates capital.
        """;
}
