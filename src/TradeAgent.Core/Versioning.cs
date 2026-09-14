using System.Reflection;

namespace TradeAgent.Core;

/// <summary>
/// Versions we compare explicitly. An ATAS update that moves the bridge protocol must pause
/// trading and ask for a repair, not produce unpredictable execution.
/// </summary>
public static class Versions
{
    public const int ProtocolVersion = 1;      // agent <-> gateway IPC

    /// <summary>
    /// gateway &lt;-&gt; ATAS bridge.
    ///
    /// 1 -> 2: the bridge pipe authenticates. Both ends now exchange a challenge and a proof before
    /// either says hello, and a hello that arrives without one is refused rather than served. That
    /// is a change to the WIRE, not only to a policy: a bridge built before it cannot complete a
    /// connection to this build at all, so it must not be allowed to present as a bridge that
    /// merely holds the wrong secret. Bumping the number is what routes it to
    /// <c>IncompatibleBridge</c> instead — "bridge 0.0.9 speaks protocol 1, this build speaks 2 —
    /// reinstall the add-on" — which is the true diagnosis and the actual repair. Left at 1, the
    /// same bridge would have surfaced as an authentication failure and sent whoever reads it
    /// hunting a secret problem that does not exist.
    /// 2 -> 3: the write-ahead record is a precondition for placing, not a diagnostic beside it. A
    /// version-2 bridge writes the witness, ignores whether the rewrite reached the disk, and sends
    /// the order anyway; it also omits <c>witness_failure</c> from its hello, so this build cannot
    /// see that it is doing so. Both halves are wire-visible changes to what the bridge PROMISES,
    /// and the older DLL is exactly the case the number exists to catch: it presents as healthy,
    /// reports SupportsClientOrderId on a witness it may have failed to write, and nothing in the
    /// data says which. Bumping routes it to <c>IncompatibleBridge</c> — "bridge 0.1.x speaks
    /// protocol 2, this build speaks 3 — reinstall the add-on" — which is the true diagnosis and the
    /// actual repair. Left at 2, a current app would accept it and trust it.
    /// </summary>
    public const int BridgeProtocolVersion = 3;

    /// <summary>
    /// 1 -&gt; 2: the material ledger. Everything the account owner hands the agent, and everything
    /// the agent produces, is recorded with a hash and a timestamp. Purely additive — two new
    /// tables, nothing existing altered — so an older database opens and is migrated in place.
    ///
    /// 3 -&gt; 4: <c>material.version</c>, so a sighting at a tuple the ledger has already watched
    /// disappear becomes a new row instead of un-removing the old one and inheriting its hash
    /// (REVIEW 2026-09-05b finding 6). Additive too — one column with a default, and the
    /// observation index widened to carry it — and every existing row reads as version 0.
    ///
    /// 4 -&gt; 5: the <c>fill</c> table. One row per execution, keyed <c>(account_id, execution_id)</c>,
    /// written by the gateway and never updated or deleted, so the money the AI made or lost is a
    /// number somebody can read rather than a line in an activity log. Additive — one new table —
    /// and an older database gains it empty, which is the honest starting point: the ledger covers
    /// what it saw, and <c>trade pnl</c> says from when.
    ///
    /// 5 -&gt; 6: the <c>ai_attempt</c> table. One row per launch of the agent CLI, written BEFORE the
    /// process starts and completed when it ends, so a turn that was killed before its usage came
    /// back is a charge somebody can see rather than a turn that cost nothing. It replaces the
    /// <c>ai_meter_*</c> kv totals as what the daily ceiling is measured against. Additive — one new
    /// table — and an older database gains it empty, so the first day after an upgrade starts at
    /// zero rather than inheriting a total whose detail nobody kept.
    ///
    /// 6 -&gt; 7: the <c>mission_event</c> table. One row per reason the AI is allowed to be woken —
    /// the owner's message, new material, a fill, an order reaching a terminal state, the day's
    /// renewal, a delay the AI asked for, a scheduled review — each with a deterministic id, so a
    /// second raise of the same fact is a no-op and a replay of a whole day's events wakes nothing.
    /// It is what replaces the immediate re-turn: before it the loop asked for another turn the
    /// instant one ended, and the only thing that ever stopped it was the day's cost ceiling.
    /// Additive — one new table — and an older database gains it empty, which is the honest
    /// starting point: nothing that happened before the upgrade is a reason to wake now.
    ///
    /// 7 -&gt; 8: the <c>dataset</c> and <c>dataset_file</c> tables. What market data this installation
    /// collected, the URL and both hashes of every raw archive file it was built from, and the
    /// counts that say what the normalised file does not claim — gaps, duplicates, bars that had not
    /// closed. Written by the app only; there is no verb and no pipe op that reaches them. Additive
    /// — two new tables — and an older database gains them empty, which reads correctly as "this
    /// installation has collected no data yet".
    ///
    /// 8 -&gt; 9: the council. A <c>role</c> column on <c>ai_attempt</c> and on <c>mission_event</c>,
    /// and the <c>publication</c> and <c>delivery</c> tables the relay between the two roles commits
    /// into. Additive — two nullable columns and two new tables — and every row written before it
    /// reads as the chair's, because the single agent this replaces was Operations and attributing
    /// its history to nobody would lose the one thing round 4 called unrecoverable. A publication's
    /// id is the SHA-256 of its content, which is what makes a crash inside the relay recover to
    /// exactly one committed task rather than to none or to two.
    ///
    /// 9 -&gt; 10: <c>mission_event.disposition_detail</c>. The wake queue could say WHAT became of a
    /// wake and not what that pointed at, which is the whole of the answer for the three outcomes the
    /// app reaches without a turn — the publication an owner's message was delegated into, the later
    /// message that superseded it, the reason nothing could take it. Additive: one nullable column,
    /// and every row written before it reads as a disposition that points at nothing, which it did.
    ///
    /// 10 -&gt; 11: <c>ix_publication_kind</c>. A role's <c>trading/PLAN.md</c> and
    /// <c>trading/JOURNAL.md</c> become revisions in <c>publication</c> — no new column, because a
    /// revision IS an artifact and a second table would put two answers to "what did this role
    /// publish" in two places — and the restore that puts the last valid plan back reads the newest
    /// revision of ONE kind for ONE role at the end of every turn. Index only: an older database
    /// gains it and loses nothing.
    ///
    /// 11 -&gt; 12: the strategy ledger — <c>strategy_version</c>, <c>strategy_run</c> and
    /// <c>strategy_trade</c>. Nothing measured a strategy before this and the owner's report said so;
    /// a program was a file in <c>strategies/</c> with no identity, and a run had no trace, no
    /// declared execution model and no lineage. Each of the three is keyed by WHAT IT IS rather than
    /// by when it was seen: a version by <c>StrategyProgram.StrategyId</c>, a run by a hash over the
    /// version id, the dataset id, the dataset's normalised sha256, the window and the declared fees,
    /// slippage, quantity increment and initial capital. That is the whole of why accumulated evidence
    /// means anything — an id minted from the attempt would make every restart a new version, and
    /// every result recorded against it a result about nothing. Written by the app only, like
    /// <c>dataset</c>, <c>material</c> and <c>fill</c>: there is a pipe op that ASKS for a run and
    /// none that writes, alters or deletes a row, because a strategy's record is the evidence its
    /// author is judged on. Additive — three new tables and one index — and an older database gains
    /// them empty, which reads correctly as "this installation has measured no strategy yet".
    ///
    /// 11/12 -&gt; 13: the <c>tool_call</c> table — one row per tool the app-owned harness was asked
    /// for, served or refused, with the attempt it belongs to. It is round 4's "observed deliveries"
    /// for a worker, and the one thing that sentence said could not be had from an unrestricted CLI.
    /// Written by the app only, like every other ledger here. Additive — one new table — and an older
    /// database gains it empty, which reads correctly as "no worker has run on the harness yet".
    /// (12 is <c>U-runner-3</c>'s <c>backtest</c> op; the two numbers were assigned at dispatch so the
    /// units can land in either order.)
    ///
    /// 13 -&gt; 14: THE REFEREE'S PROTOCOL. <c>dataset.holdout_from</c> and
    /// <c>dataset.evaluation_class</c>, so a dataset can hold its last months back as private
    /// evaluation evidence; <c>strategy_campaign</c>, whose scoring policy text and SHA-256 are fixed
    /// at open and whose trial and verdict budgets are finite; <c>strategy_trial</c>, one row per
    /// registered research run, keyed by campaign, version and run and by NOTHING an agent chooses, so
    /// that replacing a team inherits the count instead of resetting it; and <c>strategy_verdict</c>,
    /// one row per verdict REQUESTED, written before any holdout bar is read because every verdict
    /// leaks (<c>docs/COUNCIL.md</c>:134). Nothing measured a holdout before this: <c>dataset</c> had
    /// two states and no class, and a thousand backtests of one program cost nothing at all. This rung
    /// was written as <c>if (have &lt; 14)</c> while 13 was still in flight beside it, and it needed no
    /// edit when 13 landed: the two are independent and additive, which is what let the ladder come out
    /// 11-12-13-14 whatever order the units landed in. Additive — two columns and three tables, the app
    /// the only writer, no pipe op that touches any of them — and an older database gains them empty,
    /// which reads correctly as "this installation holds nothing back and has run no campaign".
    ///
    /// 14 -&gt; 15: THE VERDICT ITSELF. <c>strategy_promotion</c>, one immutable row per judgement, whose
    /// id is the SHA-256 of the nine facts it binds — version, campaign, scoring-policy hash,
    /// interpreter build, holdout dataset and its hash at run time, declared execution model, evaluator
    /// version and the holdout run. That is rule 9's list of what promotion must be bound to, written
    /// down once and addressable: before it, <c>strategy_version</c> carried no state at all and nothing
    /// compared a version's freeze with the window it was judged over. There is no <c>invalidated</c>
    /// column, deliberately — "a changed assumption invalidates the evidence that rested on it" is
    /// computed at READ time from those hashes against the current facts (<c>Promotions.Standing</c>),
    /// because a column would make a version's truth depend on a sweep having run. Written by the app
    /// alone with ON CONFLICT DO NOTHING, never updated and never deleted, and no pipe op or
    /// <c>trade</c> verb reaches it. Additive — one table and one index — and an older database gains it
    /// empty, which reads correctly as "nothing has been judged here".
    ///
    /// 15 -&gt; 16: THE CONSEQUENTIAL BOUNDARY. <c>boundary_event</c>, one row per boundary the app fixes
    /// — a promotion today, a retirement under the same shape later (<c>docs/COUNCIL.md</c>:222) — keyed
    /// by <c>kind:entity:revision</c> and by nothing about the attempt, the clock or the process, so a
    /// repeated proposal collides with the row already there and buys no senior turn (:64, "deduplicate
    /// boundary events by entity and revision, so repeated proposals cannot manufacture senior spend");
    /// and <c>boundary_submission</c>, which records which boundary each <c>assessment</c> or
    /// <c>challenge</c> publication answers and whose keys are the two refusals — a second assessment
    /// from one director over one boundary, and a second challenge over it from either. The deadline's
    /// default is on the row AT OPEN (<c>default_disposition</c>) because "a deadline with a
    /// predetermined default" is precommitment, and <c>disposed_by</c> can only ever read <c>policy</c>:
    /// there is no method by which a director disposes a boundary. Before this rung
    /// <c>grep -rn "assessment\|challenge" src</c> found nothing at all, so the one paragraph of the
    /// doctrine that spends the strongest model had no code under it. Additive — two tables and three
    /// indexes, the app the only writer, no pipe op and no <c>trade</c> verb near either — and an older
    /// database gains them empty, which reads correctly as "no boundary has been opened here".
    ///
    /// 15/16 -&gt; 17: THE VENUE CATALOGUE. <c>venue</c> (id, display name, calendar kind) and
    /// <c>venue_instrument</c> (symbol, tick size, quantity increment), every row carrying
    /// <c>source</c>, <c>recorded_at</c> and <c>verified</c> — the <c>runtimes.json</c> honesty flag
    /// (<c>docs/DECISIONS.md</c>:73-78) applied to an instrument definition. Before it the word "venue"
    /// was in this build's comments and nowhere else, so the increment a backtest rounded a size down
    /// to was whatever number arrived on the request and defaulted to 1, a whole Bitcoin on a pair
    /// whose step is 0.00001 (<c>docs/COUNCIL.md</c>:145,152). The rows are NOT seeded here:
    /// <c>VenueCatalog</c> ships them, <c>venues.json</c> overrides them and <c>VenueStore.Sync</c>
    /// writes the table, because a vendor fact frozen into a migration is one nobody can correct with a
    /// one-line edit. Beside them <c>dataset.venue_id</c> and <c>dataset.instrument_symbol</c>, copied
    /// onto the row rather than joined and with no foreign key — a catalogue edited next month must not
    /// rewrite what last month's evidence was collected from — backfilled to <c>binance-spot</c> and the
    /// pair, which is what every row that exists today was; and <c>strategy_run.increment_source</c>,
    /// which says whether a run's increment was the caller's own or was read out of the catalogue.
    /// That column is deliberately NOT in the run id's hash (<c>ExecutionModel.Canonical</c>): a run's
    /// identity is the model it ran under, not the provenance of how that model was assembled, and
    /// folding it in would move every run id already recorded. This rung was written as
    /// <c>if (have &lt; 17)</c> while 16 was in flight beside it, and landing 16 first cost it nothing
    /// but its place in the ladder, for the reason 14 cost 13 nothing. Additive — two tables, three
    /// columns, no fee field and no minimum notional, because <c>docs/COUNCIL.md</c> is silent on a fee
    /// table and :155 keeps fees declared per backtest — and an older database gains them, the datasets
    /// backfilled and the catalogue empty until the app syncs it.
    ///
    /// <para><b>18 is <c>U-data-2</c>:</b> three columns on <c>dataset</c> — the coverage TARGET in
    /// UTC days and whether the SOURCE said its candles always carry a traded volume, both of them the
    /// source's own declarations, and the count of bars that arrived with none, which is the
    /// normaliser's own measurement (<c>docs/COUNCIL.md</c>:164-172). No <c>coverage_actual_days</c>:
    /// the actual depth is the span between <c>first_bar</c> and <c>last_bar</c>, which are already on
    /// the row. Additive — an older database gains all three, backfilled with what every row that
    /// exists today IS: twelve months stated in days, a source that carries volume, and no
    /// midpoint-derived bars.</para>
    /// </summary>
    public const int DatabaseSchemaVersion = 18;

    /// <summary>
    /// THE GRANT-POLICY REVISION EVERY LAUNCH RECORD CARRIES (<c>docs/COUNCIL.md</c>, round 4).
    ///
    /// 0 is the honest number for this build: there are no grants yet — no roles, no artifact
    /// revisions, no recipients — so every attempt runs under the same absent policy. The column
    /// exists now rather than later because a revision cannot be retrofitted onto attempts that ran
    /// before it: which policy was in force when a model request was made is one of the four things
    /// round 4 named as unrecoverable if it is not recorded at the time.
    /// </summary>
    public const int GrantPolicyVersion = 0;

    public static string App =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool BridgeCompatible(int bridgeReported) => bridgeReported == BridgeProtocolVersion;
}
