using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Features;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Core.Db;

/// <summary>What became of a text that was offered as a program. See <see cref="StrategyVersionRow"/>.</summary>
public static class ParseVerdict
{
    /// <summary>
    /// The parser produced a program, so it has a canonical form, a warm-up and an id.
    ///
    /// <para>It is the only verdict a row in <c>strategy_version</c> can carry in this build, and the
    /// column is here anyway: the table is keyed by <see cref="StrategyProgram.StrategyId"/>, a hash
    /// over the CANONICAL form, and a refusal has no canonical form to key a row by. A refusal is
    /// answered to the caller naming the line and is not recorded. The word is written down rather
    /// than implied so that a reader of one row does not have to know that.</para>
    /// </summary>
    public const string Accepted = "accepted";
}

/// <summary>
/// ONE FROZEN STRATEGY VERSION, IDENTIFIED BY WHAT IT MEANS AND NOT BY WHEN IT WAS SEEN.
///
/// <para><see cref="Id"/> is <see cref="StrategyProgram.StrategyId"/> — SHA-256 over the canonical
/// form, the parameters and the semantic-version manifest. That is the whole point of the table: the
/// same twelve lines offered on Monday and again on Friday are ONE version with one lineage, and
/// every run, metric and verdict recorded against that id is about one program. An id minted from
/// the attempt, the clock or a Guid would make every restart a new version and every accumulated
/// result a result about nothing — which is the mutant this unit watched go red.</para>
///
/// <para><see cref="InterpreterBuild"/> is provenance and is deliberately NOT in the id: the app's
/// version moves with every build and does not change what a program MEANS, while everything that
/// does change its meaning is already inside <see cref="Manifest"/>, which IS hashed into the id. The
/// row records the build that first accepted the text.</para>
/// </summary>
public sealed record StrategyVersionRow(
    string Id,
    string Source,
    string Canonical,
    string Manifest,
    string InterpreterBuild,
    string ParseVerdict,
    int WarmUpBars,
    DateTimeOffset CreatedAt,
    string? Role,
    string? Attempt)
{
    /// <summary>
    /// THE THREE EXECUTION BOUNDS THE PROGRAM DECLARED, COPIED OFF THE FROZEN PROGRAM — or null on
    /// all three, because it declared none.
    ///
    /// <para><c>docs/COUNCIL.md</c>:96-97 puts them on the STRATEGY, and they are already hashed into
    /// <see cref="Id"/> (<c>StrategyCanonical</c>), so this column pair is not a second source of
    /// truth: it is what lets a reader ask what a version's bounds are without re-parsing its source,
    /// which is a thing the owner's report and the dispatcher's own record both do. When the two
    /// could disagree the PROGRAM is right — see <c>Referee.Verdict</c>, which reads the bounds it
    /// writes onto a promotion from the parse and never from this row.</para>
    ///
    /// <para>Init-only with a default, like <c>StrategyRunRow.IncrementSource</c>, so adding them
    /// re-parameterised no construction site; null on a row written before schema 19 is the truth
    /// about that row, because the language could not spell a bound when it was written.</para>
    /// </summary>
    public TimeSpan? Timeframe { get; init; }

    public TimeSpan? DataFreshness { get; init; }

    public TimeSpan? MaxDecisionAge { get; init; }

    /// <summary>The three as one value, or null unless all three are there. See <c>FreshnessBounds</c>.</summary>
    public Strategy.FreshnessBounds? Freshness =>
        this is { Timeframe: { } t, DataFreshness: { } d, MaxDecisionAge: { } m }
            ? new Strategy.FreshnessBounds(t, d, m) : null;

    /// <summary>
    /// THE VERSION THIS ONE WAS DERIVED FROM, DECLARED BY THE SUBMITTER AND NEVER INFERRED — or null,
    /// which means this version is its own root.
    ///
    /// <para><c>docs/COUNCIL.md</c>:201, "evolution adds versioned parentage". Before schema 21 the only
    /// lineage in this build was the campaign's <c>renewed_from</c>: a variant of a promoted program was
    /// a fresh hash with no relation to anything, so the trial budget it was charged against was
    /// whichever campaign it happened to be run under (<see cref="CampaignStore.RegisterTrial"/>).</para>
    ///
    /// <para><b>It is OUTSIDE the strategy hash, deliberately.</b> <see cref="Id"/> is
    /// <c>StrategyProgram.StrategyId</c> — a hash over what the program MEANS — and folding a parent
    /// into it would make the same twelve lines two different versions depending on what their submitter
    /// said about where they came from, which is the one thing the id exists to prevent. Parentage
    /// therefore never moves an id, and the first declaration stands: <c>ON CONFLICT(id) DO NOTHING</c>
    /// means a second submission of the same program cannot restate its own ancestry.</para>
    ///
    /// <para><b>Never inferred.</b> The app does not guess a parent from the submitting role's previous
    /// version, from the clock or from any textual similarity: two unrelated programs from one role
    /// would read as parent and child, and every count taken over a lineage would then be a count over
    /// an invention. A version that declares none IS a root, and that is a statement rather than a
    /// gap.</para>
    /// </summary>
    public string? ParentVersionId { get; init; }
}

/// <summary>
/// ONE BACKTEST RUN, IDENTIFIED BY EVERYTHING THAT DECIDED ITS RESULT.
///
/// <para><see cref="Id"/> is a hash over the version id, the dataset id, the dataset's normalised
/// SHA-256, the window and the declared execution model — <c>Backtest.RunId</c> — so two runs of the
/// same program over the same bytes under the same fees ARE one run, and a run under a different fee
/// is a different one that cannot silently overwrite it.</para>
///
/// <para><see cref="DatasetSha256"/> is the dataset's hash AS IT WAS AT RUN TIME, copied onto the
/// run. The dataset row can later be REJECTED — a raw archive changed under it — and when that
/// happens this column is what lets every run that fed on those bytes be found, rather than leaving a
/// result attached to a dataset id whose contents nobody can identify any more.</para>
///
/// <para>The nullable money columns are UNKNOWNS and never zeros: <see cref="NetPnl"/> and
/// <see cref="MaxDrawdown"/> are null for a run that faulted before its first complete bar, and a
/// reader that printed 0 there would be reporting a flat result for a run that produced none.</para>
/// </summary>
public sealed record StrategyRunRow(
    string Id,
    string VersionId,
    long DatasetId,
    string DatasetSha256,
    DateTimeOffset? WindowFrom,
    DateTimeOffset? WindowTo,
    string ExecutionModel,
    string Outcome,
    string? FaultReason,
    long Bars,
    int Trades,
    int Wins,
    long Intents,
    int Fills,
    long ExposureBars,
    long MissingMinutes,
    long Faults,
    decimal? GrossPnl,
    decimal? Fees,
    decimal? NetPnl,
    decimal? MaxDrawdown,
    string TraceSha256,
    DateTimeOffset CreatedAt,
    string? Role,
    string? Attempt)
{
    /// <summary>
    /// WHERE THIS RUN'S QUANTITY INCREMENT CAME FROM, or null because no run before schema 17 recorded
    /// one.
    ///
    /// <para>The increment itself is already in <see cref="ExecutionModel"/> and is part of the run's
    /// id. This says whether the caller DECLARED it or whether the app read it out of the venue
    /// catalogue, and from which row — the difference between a number an agent typed and a recorded
    /// fact with a source, which is the whole point of <c>venue_instrument</c> existing.</para>
    ///
    /// <para>Init-only with a default, like <c>DatasetRecord.HoldoutFrom</c>, so adding it did not
    /// silently re-parameterise every construction site; and deliberately NOT in the run id's hash, so
    /// that recording it moved no id this installation had already written.</para>
    /// </summary>
    public string? IncrementSource { get; init; }

    /// <summary>
    /// WHERE THIS RUN'S FEE AND SLIPPAGE CAME FROM, number by number (<c>U-paper-friction</c>) — declared
    /// by the caller, or TradeAgent's venue cost model for the dataset's venue, named by id and sha — or
    /// null because the run was recorded before a build that said.
    ///
    /// <para>The numbers are in <see cref="ExecutionModel"/> and in the run's id; this is who said so, for
    /// the reason <see cref="IncrementSource"/> exists, and like it it is not hashed.</para>
    ///
    /// <para><b>Stored in the run's one provenance column, after the increment's sentence.</b> The unit
    /// that added it allowed no schema change, and <c>strategy_run.increment_source</c> is the only column
    /// a run's provenance has: the column holds the increment's sentence, then a line of its own beginning
    /// <see cref="StrategyStore.FrictionMark"/>. <see cref="StrategyStore"/> writes both and reads them back
    /// apart, so neither property ever carries the other's words, and a row written before this holds no
    /// such line and reads back with this null.</para>
    /// </summary>
    public string? FrictionSource { get; init; }
}

/// <summary>
/// ONE CLOSED TRADE OF ONE RUN: when it was entered and left, at what prices, how much, why it
/// ended, what it cost in fees and what it made.
///
/// <para>A trade that is still OPEN when the run's last bar closes is not in here, and that is not an
/// omission: it has no exit and no realised result. It is in the run's equity — and therefore in the
/// drawdown — because <c>docs/briefs/U-runner-3.md</c> requires the drawdown to include the open
/// trade, and a drawdown that ignored it would be measured on an account that does not exist.</para>
/// </summary>
public sealed record StrategyTradeRow(
    string RunId,
    int Ordinal,
    DateTimeOffset EntryBar,
    decimal EntryPrice,
    DateTimeOffset ExitBar,
    decimal ExitPrice,
    decimal Quantity,
    string ExitReason,
    decimal Fees,
    decimal Pnl);

/// <summary>
/// ONE PAGE OF ONE RECORDED RUN'S CLOSED TRADES, OR WHY THERE IS NONE (<c>U-run-trace</c>) — what
/// <see cref="StrategyStore.ReadTrades"/> answers.
///
/// <para><see cref="Run"/> is the run's row as recorded, <see cref="TradeCount"/> how many closed trades it recorded in
/// all, and <see cref="Trades"/> the page, in ordinal order. <see cref="More"/> says a bound stopped the page with another
/// trade after it, and <see cref="CappedBy"/> which: <see cref="TapeReader.CappedByLimit"/> or
/// <see cref="TapeReader.CappedByBytes"/>, the tape's own words for the same two bounds.</para>
///
/// <para><b>A refusal carries NO row and no trade</b>, deliberately, as <c>BarWindow.Refusal</c> and
/// <c>TapeWindow.Refusal</c> do: a caller that forgets to look at it is handed nothing of the run. <see cref="Withheld"/>
/// says the refusal is the holdout's — the referee's holdout run, or a run whose bars or feature reads a holdout window
/// reaches now — rather than a name that matched no one run.</para>
/// </summary>
public sealed record RunTradesPage(
    StrategyRunRow? Run, int TradeCount, IReadOnlyList<StrategyTradeRow> Trades, bool More, string? CappedBy,
    string? Refusal = null, bool Withheld = false)
{
    internal static RunTradesPage No(string why) => new(null, 0, [], false, null, why);

    internal static RunTradesPage Held(string why) => new(null, 0, [], false, null, why, Withheld: true);
}

/// <summary>
/// THE STRATEGY LEDGER — what this installation ran, and the app is the only writer.
///
/// <para><b>No pipe op writes here and none deletes.</b> The `backtest` op asks for a run; the APP
/// parses the program, opens the dataset, evaluates the bars, computes the metrics from its own trace
/// and writes these rows. Nothing an agent sends chooses a run id, a metric, a trade or a version id,
/// for the reason <see cref="DatasetStore"/> and <see cref="MaterialStore"/> exist: a strategy's
/// record is the evidence its author is judged on, and an author who could edit it could report a
/// profitable year it never had.</para>
///
/// <para><b>Both writes are idempotent on the id, and the FIRST writer wins.</b> The ids are content
/// hashes, so a re-request of the same program over the same bytes under the same model is the same
/// row; <c>ON CONFLICT DO NOTHING</c> means the earlier record — with its own <c>created_at</c>,
/// role and attempt — is what stands. A second run cannot quietly restate the first one's result as
/// its own, and a crash between the run and the record leaves the next request to write it.</para>
/// </summary>
public sealed class StrategyStore(Database db)
{
    const string VersionCols =
        "id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt, " +
        // LAST, so every positional read above them keeps its index. See `StrategyVersionRow.Timeframe`.
        "timeframe, data_freshness, max_decision_age, " +
        // LAST AGAIN, for the same reason, at schema 21. See `StrategyVersionRow.ParentVersionId`.
        "parent_version_id";

    const string RunCols =
        "id, version_id, dataset_id, dataset_sha256, window_from, window_to, execution_model, outcome, " +
        "fault_reason, bars, trades, wins, intents, fills, exposure_bars, missing_minutes, faults, " +
        "gross_pnl, fees, net_pnl, max_drawdown, trace_sha256, created_at, role, attempt, " +
        // LAST, so every positional read above it keeps its index. See `StrategyRunRow.IncrementSource`.
        "increment_source";

    /// <summary>
    /// The interpreter build a version row and a promotion record: the app's own version and the
    /// language's. PROVENANCE — which release did the work — and nothing withdraws a verdict on it: a
    /// release moves it whether or not anything a program means has moved. What does withdraw one is
    /// the evaluation semantics (<c>Strategy.EvaluationSemantics</c>, <c>Promotions.Standing</c>).
    /// </summary>
    public static string InterpreterBuild => string.Create(CultureInfo.InvariantCulture,
        $"app={Versions.App};language={StrategyVersions.LanguageVersion}");

    /// <summary>
    /// Records one accepted version, or leaves the row that is already there alone. Returns the id,
    /// which is the program's own and was not minted here.
    /// </summary>
    public string RecordVersion(StrategyVersionRow version) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO strategy_version({VersionCols})
            VALUES($id,$src,$canon,$man,$build,$verdict,$warm,$at,$role,$attempt,$tf,$fresh,$age,$parent)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", version.Id), ("$src", version.Source), ("$canon", version.Canonical),
            ("$man", version.Manifest), ("$build", version.InterpreterBuild),
            ("$verdict", version.ParseVerdict), ("$warm", version.WarmUpBars),
            ("$at", Sql.T(version.CreatedAt)), ("$role", version.Role), ("$attempt", version.Attempt),
            ("$tf", Sql.Seconds(version.Timeframe)), ("$fresh", Sql.Seconds(version.DataFreshness)),
            ("$age", Sql.Seconds(version.MaxDecisionAge)),
            // WHAT THE SUBMITTER DECLARED, AND NOTHING THIS METHOD WORKED OUT FOR ITSELF. There is no
            // fallback here on purpose: no "the role's last version", no "the newest row", no guess.
            ("$parent", version.ParentVersionId));
        c.ExecuteNonQuery();
        return version.Id;
    });

    /// <summary>
    /// THIS VERSION AND EVERY VERSION IT DECLARED ITSELF DERIVED FROM, child first — the chain
    /// <see cref="CampaignStore"/> charges a trial over.
    ///
    /// <para>It walks <c>parent_version_id</c> and stops at a row it has already seen, so a cycle
    /// written by a future bug is a short list rather than a hang — the shape
    /// <c>CampaignStore.Lineage</c> already has over <c>renewed_from</c>. An id this installation does
    /// not hold answers an empty list, because a version it has never accepted has no ancestry here to
    /// report.</para>
    /// </summary>
    /// <summary>
    /// EVERY VERSION THAT DECLARED THIS ONE AS ITS PARENT, newest first — the successors a replacement
    /// policy is about.
    ///
    /// <para>One level and not a subtree: "bounded replacement" (<c>docs/COUNCIL.md</c>:201) is about a
    /// candidate and the thing put up in its place, and a grandchild answers to its own parent.</para>
    /// </summary>
    public IReadOnlyList<StrategyVersionRow> ChildrenOf(string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {VersionCols} FROM strategy_version WHERE parent_version_id=$id "
            + "ORDER BY created_at DESC, id DESC", ("$id", versionId));
        return (IReadOnlyList<StrategyVersionRow>)ReadVersions(c);
    });

    public IReadOnlyList<string> Ancestry(string versionId)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = versionId;

        while (at is not null && seen.Add(at) && VersionById(at) is { } row)
        {
            chain.Add(at);
            at = row.ParentVersionId;
        }

        return chain;
    }

    /// <summary>One version by its id, or null when this installation has never accepted that program.</summary>
    public StrategyVersionRow? VersionById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {VersionCols} FROM strategy_version WHERE id=$id", ("$id", id));
        return ReadVersions(c).FirstOrDefault();
    });

    /// <summary>Every version this installation has accepted, newest first.</summary>
    public IReadOnlyList<StrategyVersionRow> AllVersions(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {VersionCols} FROM strategy_version ORDER BY created_at DESC, id DESC LIMIT $n",
            ("$n", limit));
        return ReadVersions(c);
    });

    /// <summary>
    /// Records one run and its closed trades in ONE transaction, or leaves the run that is already
    /// there alone — trades included, because a run's trades are part of the run and half of them is
    /// not a result. Returns the run id.
    /// </summary>
    public string RecordRun(StrategyRunRow run, IReadOnlyList<StrategyTradeRow> trades) => db.Write(_ =>
    {
        using var insert = db.Cmd($"""
            INSERT INTO strategy_run({RunCols})
            VALUES($id,$ver,$ds,$sha,$from,$to,$model,$outcome,$fault,$bars,$trades,$wins,$intents,
                   $fills,$exposure,$missing,$faults,$gross,$fees,$net,$dd,$trace,$at,$role,$attempt,
                   $incsrc)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", run.Id), ("$ver", run.VersionId), ("$ds", run.DatasetId), ("$sha", run.DatasetSha256),
            ("$from", run.WindowFrom is { } f ? Sql.T(f) : null),
            ("$to", run.WindowTo is { } t ? Sql.T(t) : null),
            ("$model", run.ExecutionModel), ("$outcome", run.Outcome), ("$fault", run.FaultReason),
            ("$bars", run.Bars), ("$trades", run.Trades), ("$wins", run.Wins),
            ("$intents", run.Intents), ("$fills", run.Fills), ("$exposure", run.ExposureBars),
            ("$missing", run.MissingMinutes), ("$faults", run.Faults),
            ("$gross", run.GrossPnl is { } g ? Sql.D(g) : null),
            ("$fees", run.Fees is { } fee ? Sql.D(fee) : null),
            ("$net", run.NetPnl is { } n ? Sql.D(n) : null),
            ("$dd", run.MaxDrawdown is { } d ? Sql.D(d) : null),
            ("$trace", run.TraceSha256), ("$at", Sql.T(run.CreatedAt)),
            ("$role", run.Role), ("$attempt", run.Attempt), ("$incsrc", Provenance(run)));

        if (insert.ExecuteNonQuery() == 0) return run.Id;

        foreach (var trade in trades)
        {
            using var c = db.Cmd("""
                INSERT INTO strategy_trade(run_id, ordinal, entry_bar, entry_price, exit_bar, exit_price,
                                           quantity, exit_reason, fees, pnl)
                VALUES($run,$ord,$eb,$ep,$xb,$xp,$qty,$why,$fees,$pnl)
                """,
                ("$run", trade.RunId), ("$ord", trade.Ordinal), ("$eb", Sql.T(trade.EntryBar)),
                ("$ep", Sql.D(trade.EntryPrice)), ("$xb", Sql.T(trade.ExitBar)),
                ("$xp", Sql.D(trade.ExitPrice)), ("$qty", Sql.D(trade.Quantity)),
                ("$why", trade.ExitReason), ("$fees", Sql.D(trade.Fees)), ("$pnl", Sql.D(trade.Pnl)));
            c.ExecuteNonQuery();
        }

        return run.Id;
    });

    /// <summary>One run by its id, or null when this installation has never produced it.</summary>
    public StrategyRunRow? RunById(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {RunCols} FROM strategy_run WHERE id=$id", ("$id", id));
        return ReadRuns(c).FirstOrDefault();
    });

    /// <summary>Every run this installation has measured, newest first.</summary>
    public IReadOnlyList<StrategyRunRow> Runs(int limit = 100) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {RunCols} FROM strategy_run ORDER BY created_at DESC, id DESC LIMIT $n", ("$n", limit));
        return ReadRuns(c);
    });

    /// <summary>
    /// EVERY RUN OF ONE VERSION THAT ONE ROLE COMPLETED, newest first — the measurement a caller has
    /// to have made before the app will spend a verdict on its program.
    ///
    /// <para>COMPLETED and not merely recorded: a FAULTED run halted at the bar it reached, so its
    /// figures cover a window nobody asked for and it is not evidence that the program runs. The ROLE
    /// is the one on the row and never <c>CouncilRoles.Or</c>'s reading of a missing one — a verdict
    /// asked for on the strength of somebody else's run would let a role that measured nothing spend
    /// the owner's evaluation budget.</para>
    ///
    /// <para>A read, like every other method on this store that is not <c>RecordRun</c>.</para>
    /// </summary>
    public IReadOnlyList<StrategyRunRow> CompletedRunsOf(string versionId, string role) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"""
             SELECT {RunCols} FROM strategy_run
              WHERE version_id=$v AND role=$r AND outcome=$o
              ORDER BY created_at DESC, id DESC
             """,
            ("$v", versionId), ("$r", role), ("$o", nameof(Strategy.BacktestOutcome.COMPLETED)));
        return ReadRuns(c);
    });

    /// <summary>
    /// EVERY RUN THAT FED ON ONE DATASET, newest first — which is what makes a later rejection
    /// traceable.
    ///
    /// <para>A dataset row can be moved to REJECTED months after the fact, because a raw archive file
    /// changed under it. The question that then matters is "which of my results were computed over
    /// those bytes", and this is it: the run rows carry the dataset's sha AS IT WAS AT RUN TIME, so a
    /// caller can tell a run over the bytes the ledger measured from a run over bytes it no longer
    /// can.</para>
    /// </summary>
    public IReadOnlyList<StrategyRunRow> RunsOfDataset(long datasetId) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {RunCols} FROM strategy_run WHERE dataset_id=$id ORDER BY created_at DESC, id DESC",
            ("$id", datasetId));
        return ReadRuns(c);
    });

    /// <summary>How many runs this installation has measured. What section 8 of the owner's report asks.</summary>
    public int RunCount => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM strategy_run");
        return Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// One run's closed trades, in the order they were taken — THE IN-PROCESS READER, for the app's own code. It takes no
    /// audience, so nothing on the agent-facing pipe calls it: <see cref="ReadTrades"/> is that reader.
    /// </summary>
    public IReadOnlyList<StrategyTradeRow> TradesOf(string runId) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {TradeCols} FROM strategy_trade WHERE run_id=$id ORDER BY ordinal", ("$id", runId));
        return (IReadOnlyList<StrategyTradeRow>)ReadTradeRows(c);
    });

    /// <summary>The most closed trades one page of <see cref="ReadTrades"/> serves. Named in every refusal of a larger limit.</summary>
    public const int MaxTradeRows = 1_000;

    /// <summary>
    /// THE SHORTEST START OF A RUN'S ID THAT NAMES IT: the twelve characters every report prints of one
    /// (<c>DailyReports.Short</c>).
    /// </summary>
    public const int ShortestRunId = 12;

    /// <summary>
    /// EVERY CLOSED TRADE OF ONE RECORDED RUN, A PAGE AT A TIME, FOR AN AUDIENCE — the agent-facing read (<c>run-trades</c>,
    /// <c>U-run-trace</c>). <see cref="TradesOf"/> stays the in-process reader.
    ///
    /// <para><b><paramref name="run"/></b> is the run's full id, or a start of it of <see cref="ShortestRunId"/> characters or
    /// more that no other run's id shares. Fewer, a start two runs share, and one that names no run are each REFUSED in words
    /// naming what was asked — never answered with the nearest run.</para>
    ///
    /// <para><b>The audience is INSIDE the reader</b>, as on every bar and tape reader (<see cref="Holdout"/>'s rule: a
    /// required argument, and the check inside the read). <paramref name="holdout"/> is the caller's, with the dataset ledger
    /// its windows are read from at this read (<see cref="TapeHoldout.Pipe"/>), and for every audience but the referee's it
    /// REFUSES, with no row and no trade: (a) THE REFEREE'S HOLDOUT RUN — a row marked <see cref="Referee.RunRole"/>, or one
    /// any promotion names as its <c>holdout_run_id</c> — in words naming it so and <c>trade verdict</c> as what serves its
    /// verdict and reason class, and no figure, count, trace hash or instant of it; (b) A RUN WHOSE BARS A HOLDOUT WINDOW
    /// REACHES NOW — its own dataset's cutoff (<see cref="Holdout.Refusal"/>), then every other dataset's window over the
    /// bars' market span (<see cref="TapeHoldout.Refusal(DatasetRecord, DateTimeOffset?, DateTimeOffset?)"/>) — over the
    /// dataset and the window the run RECORDED, the dataset read from the ledger at this read: a cutoff set after the run
    /// counts, and a dataset the ledger no longer holds is refused rather than guessed at; (c) A RUN OF A VERSION THAT READS
    /// FEATURES WHOSE READS A HOLDOUT WINDOW REACHES NOW — exactly what <c>Backtest.Over</c> would refuse today, by its own
    /// arithmetic (<see cref="Backtest.ClosesOf"/>, <see cref="FeatureFeed.Reaching"/>); a version this build cannot read
    /// back is refused rather than guessed at. A run's trades say what its bars and its readings did, so they are held back
    /// exactly as those are — and never clipped: a page is the run's trades or nothing.</para>
    ///
    /// <para><b>Bounded twice and never silently.</b> At most <paramref name="limit"/> trades — 1 to
    /// <see cref="MaxTradeRows"/>, anything else is the caller's mistake and throws — and, as <paramref name="size"/> measures
    /// each, at most <paramref name="maxBytes"/> of them: a trade is never split, so the first is served whatever it costs.
    /// A bound that stops the page with another trade after it says so (<see cref="RunTradesPage.More"/>,
    /// <see cref="RunTradesPage.CappedBy"/>). <paramref name="after"/> is the ordinal the page starts after — −1 for the first
    /// trade, whose ordinal is 0 — so the last ordinal of one page continues it exactly.</para>
    ///
    /// <para>A read in one snapshot, like every method here but <see cref="RecordRun"/>: nothing is run again, nothing is
    /// charged and nothing is written.</para>
    /// </summary>
    public RunTradesPage ReadTrades(string run, TapeHoldout holdout, long after, int limit, long maxBytes,
        Func<StrategyTradeRow, long> size)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(holdout);
        ArgumentNullException.ThrowIfNull(size);
        if (limit is < 1 or > MaxTradeRows)
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                $"a page of a run's trades serves 1 to {MaxTradeRows} of them, and {limit} were asked for");
        if (after < -1)
            throw new ArgumentOutOfRangeException(nameof(after), after,
                "a page starts after an ordinal of 0 or more, or before the first trade at -1");
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "a page of a run's trades needs a byte budget above 0");

        return db.Read(_ =>
        {
            var asked = run.Trim();
            var (row, unnamed) = Named(asked);
            if (row is null) return RunTradesPage.No(unnamed!);

            if (Withheld(row, asked, holdout) is { } withheld) return RunTradesPage.Held(withheld);

            int count;
            using (var counted = db.Cmd("SELECT COUNT(*) FROM strategy_trade WHERE run_id=$id", ("$id", row.Id)))
                count = Convert.ToInt32(counted.ExecuteScalar(), CultureInfo.InvariantCulture);

            // ONE PAST THE LIMIT IS ENOUGH TO KNOW THERE IS MORE, and the last row this page ever asks the disk for.
            using var c = db.Cmd(
                $"SELECT {TradeCols} FROM strategy_trade WHERE run_id=$id AND ordinal > $after ORDER BY ordinal LIMIT $n",
                ("$id", row.Id), ("$after", after), ("$n", limit + 1));

            var page = new List<StrategyTradeRow>();
            long spent = 0;
            foreach (var trade in ReadTradeRows(c))
            {
                if (page.Count == limit) return new RunTradesPage(row, count, page, true, TapeReader.CappedByLimit);

                var cost = size(trade);
                if (page.Count > 0 && spent + cost > maxBytes)
                    return new RunTradesPage(row, count, page, true, TapeReader.CappedByBytes);

                page.Add(trade);
                spent += cost;
            }

            return new RunTradesPage(row, count, page, false, null);
        });
    }

    /// <summary>
    /// THE ONE RUN <paramref name="asked"/> NAMES — by its full id, or by a start of it no other run's id shares — or the
    /// words saying why it names none.
    /// </summary>
    (StrategyRunRow? Row, string? Why) Named(string asked)
    {
        var shown = Shown(asked);
        if (asked.Length < ShortestRunId)
            return (null, asked.Length == 0
                ? $"a run is named by its id, as 'backtest' answered it, or by the first {ShortestRunId} characters of it or "
                  + "more, as 'trade report' prints it — and none was given."
                : $"'{shown}' is {asked.Length} character(s) long, and a run is named by its whole id or by the first "
                  + $"{ShortestRunId} characters of it or more, as 'trade report' prints it: fewer could name several runs, "
                  + "and TradeAgent does not pick one for you.");

        if (RunById(asked) is { } exact) return (exact, null);

        using var c = db.Cmd($"SELECT {RunCols} FROM strategy_run WHERE substr(id, 1, $n) = $p ORDER BY id LIMIT 2",
            ("$n", asked.Length), ("$p", asked));
        var found = ReadRuns(c);
        return found.Count switch
        {
            1 => (found[0], null),
            0 => (null, $"there is no run '{shown}' in this installation's strategy ledger, by its whole id or as the start "
                        + $"of one. Every 'backtest' answer carries its 'run_id', and 'trade report' names the newest runs by "
                        + $"their first {ShortestRunId} characters."),
            _ => (null, $"'{shown}' is the start of more than one run's id, so it names none of them: give more of it — "
                        + "'backtest' answered the whole id.")
        };
    }

    /// <summary>
    /// WHY <paramref name="row"/>'S TRADES ARE NOT SERVED TO <paramref name="holdout"/>'S AUDIENCE, in its words, or null when
    /// they are. The three refusals of <see cref="ReadTrades"/>, in order; null at once for the referee's own.
    /// </summary>
    string? Withheld(StrategyRunRow row, string asked, TapeHoldout holdout)
    {
        if (holdout.MayReadHoldout) return null;

        // (a) THE REFEREE'S HOLDOUT RUN, by its mark or by any promotion naming it — a run a research row collided with
        // carries the research role and is still the one a verdict was judged on. Nothing of it is named but that it is
        // one: no figure, no count, no trace hash and no instant, not even the cutoff it starts at.
        if (string.Equals(row.Role, Referee.RunRole, StringComparison.Ordinal) || NamedByAPromotion(row.Id))
            return $"run {Shown(asked)} is the referee's holdout run of version {Short(row.VersionId)}: TradeAgent's "
                   + "referee ran it over months held back from research, and nothing of it is served on this channel — "
                   + "not one of its trades, figures or counts, not its trace hash and not one instant of it — to "
                   + $"{holdout.Who}, to either director, or to a connection that proved no role at all. What crosses back "
                   + "from a holdout run is its verdict and its reason class: 'trade verdict --version <its version's id>' "
                   + "answers them. It is refused rather than shown in part, because a figure from those months, once read, "
                   + "cannot be unread.";

        // (b) THE BARS' HOLDOUT, over the dataset and the window the run RECORDED, the dataset read from the holdout's own
        // ledger NOW: its own cutoff first, unchanged, then every other dataset's window over the bars' market span. A
        // cutoff set after the run counts — it was served when it ran, and is held back from then on.
        if (holdout.Dataset(row.DatasetId) is not { } set)
            return $"the trades of run {Shown(asked)} are held back: it ran over dataset {row.DatasetId}, which this "
                   + "installation's ledger no longer holds, so whether a holdout window reaches its bars cannot be read "
                   + "now — and a run's trades are not served on a guess.";

        if ((Holdout.Refusal(set, holdout.Audience, row.WindowFrom, row.WindowTo)
             ?? holdout.Refusal(set, row.WindowFrom, row.WindowTo)) is { } bars)
            return $"the trades of run {Shown(asked)} are held back: it ran over {Span(row.WindowFrom, row.WindowTo)} of "
                   + $"dataset {set.Id} ({set.Pair} {set.Interval} {set.Version}), and a run's trades say what those bars "
                   + $"did, so they are held back exactly as the bars are — {bars} A run's trades are not cut short at the "
                   + "window either: a run of the same version over a window wholly outside it is served, and 'trade "
                   + "backtest' records one.";

        // (c) THE TAPE'S HOLDOUT, over the reads its version's features made — exactly what `Backtest.Over` would refuse
        // a run of it over the same window today.
        return FeatureReads(row, asked, set, holdout);
    }

    /// <summary>
    /// WHY THE FEATURE READS OF <paramref name="row"/> ARE HELD BACK NOW, in words, or null for a version that reads none or
    /// whose reads reach no window. Its program is read back from the version it records; one this build cannot read back
    /// is refused, because whether its decisions read the tape cannot then be told.
    /// </summary>
    string? FeatureReads(StrategyRunRow row, string asked, DatasetRecord set, TapeHoldout holdout)
    {
        var version = VersionById(row.VersionId);
        var program = version is null ? null : StrategyParser.Parse(version.Source).Program;
        if (program is null)
            return $"the trades of run {Shown(asked)} are held back: TradeAgent cannot read back version "
                   + $"{Short(row.VersionId)}, which ran it — "
                   + (version is null ? "this installation's ledger no longer holds it" : "its recorded source does not parse in this build")
                   + " — so it cannot tell what market context that run's decisions read, or whether a holdout window "
                   + "reaches it now; a run's trades are not served on a guess.";

        if (program.Features.Count == 0) return null;

        if (!string.Equals(program.StrategyId, version!.Id, StringComparison.Ordinal))
            return $"the trades of run {Shown(asked)} are held back: the recorded source of version "
                   + $"{Short(row.VersionId)}, which ran it, reads features and parses in this build to a different program, "
                   + "so TradeAgent cannot tell how far back that run's readings reached, or whether a holdout window "
                   + "reaches them now; a run's trades are not served on a guess.";

        var closes = Backtest.ClosesOf(program, set, row.WindowFrom, row.WindowTo);
        var first = closes.FirstClose.ToUniversalTime();
        var until = closes.Until?.ToUniversalTime();
        var reach = FeatureFeed.ReachOf(program.Features);
        if (FeatureFeed.Reaching(holdout, reach, first, until) is not { } reached) return null;

        var names = string.Join(", ", program.Features.Select(f => $"`{f.Name}`"));
        return $"the trades of run {Shown(asked)} are held back: its version reads the feature(s) {names} at the close of "
               + $"every bar it evaluates, from the tape stamped up to {((long)reach.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s "
               + $"before it, so its closes from {FeatureEvaluator.Stamp(first)} to "
               + (until is { } end ? FeatureEvaluator.Stamp(end) : "the end of a dataset that records no last bar")
               + $" read it from {FeatureEvaluator.Stamp(reached.From)} — which the tape's holdout withholds: {reached.Why} "
               + "A run's decisions say what those readings were, so its trades are held back as the readings are, exactly "
               + "as a backtest of it over the same window is refused today.";
    }

    /// <summary>
    /// WHETHER ANY PROMOTION NAMES <paramref name="runId"/> AS ITS HOLDOUT RUN — read off <c>strategy_promotion</c>, which
    /// <see cref="Promotions"/> alone writes; asked here rather than through it, so neither store builds the other.
    /// </summary>
    bool NamedByAPromotion(string runId)
    {
        using var c = db.Cmd("SELECT 1 FROM strategy_promotion WHERE holdout_run_id=$id LIMIT 1", ("$id", runId));
        return c.ExecuteScalar() is not null;
    }

    /// <summary>A recorded window in words, for a refusal: an open side is the dataset's own first or last bar.</summary>
    static string Span(DateTimeOffset? from, DateTimeOffset? to) => (from, to) switch
    {
        ({ } lo, { } hi) => $"the bars from {lo:u} to {hi:u}",
        ({ } lo, null) => $"the bars from {lo:u} to the last",
        (null, { } hi) => $"the bars from the first to {hi:u}",
        _ => "every bar"
    };

    /// <summary>What was asked, as a refusal quotes it: at most a whole id's length of it.</summary>
    static string Shown(string asked) => asked.Length <= 64 ? asked : asked[..64] + "…";

    /// <summary>An id as it is printed for a person. The same twelve characters everything else uses.</summary>
    static string Short(string id) => id.Length <= ShortestRunId ? id : id[..ShortestRunId];

    const string TradeCols =
        "run_id, ordinal, entry_bar, entry_price, exit_bar, exit_price, quantity, exit_reason, fees, pnl";

    static List<StrategyTradeRow> ReadTradeRows(SqliteCommand c)
    {
        var rows = new List<StrategyTradeRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyTradeRow(
                r.GetString(0), r.GetInt32(1), Sql.Time(r.GetString(2)), Sql.Dec(r.GetString(3)),
                Sql.Time(r.GetString(4)), Sql.Dec(r.GetString(5)), Sql.Dec(r.GetString(6)),
                r.GetString(7), Sql.Dec(r.GetString(8)), Sql.Dec(r.GetString(9))));
        return rows;
    }

    static List<StrategyVersionRow> ReadVersions(SqliteCommand c)
    {
        var rows = new List<StrategyVersionRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyVersionRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetInt32(6), Sql.Time(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9))
            {
                Timeframe = Sql.Span(r, 10),
                DataFreshness = Sql.Span(r, 11),
                MaxDecisionAge = Sql.Span(r, 12),
                ParentVersionId = r.IsDBNull(13) ? null : r.GetString(13)
            });
        return rows;
    }

    static List<StrategyRunRow> ReadRuns(SqliteCommand c)
    {
        var rows = new List<StrategyRunRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyRunRow(
                r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                Sql.TimeN(r.IsDBNull(4) ? null : r.GetString(4)),
                Sql.TimeN(r.IsDBNull(5) ? null : r.GetString(5)),
                r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt64(9), r.GetInt32(10), r.GetInt32(11), r.GetInt64(12), r.GetInt32(13),
                r.GetInt64(14), r.GetInt64(15), r.GetInt64(16),
                Sql.DecN(r.IsDBNull(17) ? null : r.GetString(17)),
                Sql.DecN(r.IsDBNull(18) ? null : r.GetString(18)),
                Sql.DecN(r.IsDBNull(19) ? null : r.GetString(19)),
                Sql.DecN(r.IsDBNull(20) ? null : r.GetString(20)),
                r.GetString(21), Sql.Time(r.GetString(22)),
                r.IsDBNull(23) ? null : r.GetString(23), r.IsDBNull(24) ? null : r.GetString(24))
            {
                IncrementSource = Increment(r.IsDBNull(25) ? null : r.GetString(25)),
                FrictionSource = Friction(r.IsDBNull(25) ? null : r.GetString(25))
            });
        return rows;
    }

    /// <summary>
    /// WHAT OPENS THE FRICTION'S LINE IN THE RUN'S PROVENANCE COLUMN. See <see cref="StrategyRunRow.FrictionSource"/>.
    /// A newline first, so it can only ever begin a line, and both sentences are written on one line
    /// each, so neither can contain it.
    /// </summary>
    public const string FrictionMark = "\nfriction: ";

    /// <summary>The column as written: the increment's sentence, then the friction's line when there is one.</summary>
    static string? Provenance(StrategyRunRow run) =>
        run.FrictionSource is { } friction
            ? OneLine(run.IncrementSource ?? "") + FrictionMark + OneLine(friction)
            : run.IncrementSource;

    /// <summary>The increment's half of the column: everything before the friction's line, or the whole of a row written before it.</summary>
    static string? Increment(string? stored)
    {
        if (stored is null) return null;
        var at = stored.IndexOf(FrictionMark, StringComparison.Ordinal);
        return at < 0 ? stored : at == 0 ? null : stored[..at];
    }

    /// <summary>The friction's half of the column, or null for a row that holds none.</summary>
    static string? Friction(string? stored)
    {
        var at = stored?.IndexOf(FrictionMark, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : stored![(at + FrictionMark.Length)..];
    }

    static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
