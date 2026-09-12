using Microsoft.Data.Sqlite;
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
    string? Attempt);

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
    string? Attempt);

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
        "id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt";

    const string RunCols =
        "id, version_id, dataset_id, dataset_sha256, window_from, window_to, execution_model, outcome, " +
        "fault_reason, bars, trades, wins, intents, fills, exposure_bars, missing_minutes, faults, " +
        "gross_pnl, fees, net_pnl, max_drawdown, trace_sha256, created_at, role, attempt";

    /// <summary>The interpreter build a version row records: the app's own version and the language's.</summary>
    public static string InterpreterBuild =>
        $"app={Versions.App};language={StrategyVersions.LanguageVersion}";

    /// <summary>
    /// Records one accepted version, or leaves the row that is already there alone. Returns the id,
    /// which is the program's own and was not minted here.
    /// </summary>
    public string RecordVersion(StrategyVersionRow version) => db.Write(_ =>
    {
        using var c = db.Cmd($"""
            INSERT INTO strategy_version({VersionCols})
            VALUES($id,$src,$canon,$man,$build,$verdict,$warm,$at,$role,$attempt)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", version.Id), ("$src", version.Source), ("$canon", version.Canonical),
            ("$man", version.Manifest), ("$build", version.InterpreterBuild),
            ("$verdict", version.ParseVerdict), ("$warm", version.WarmUpBars),
            ("$at", Sql.T(version.CreatedAt)), ("$role", version.Role), ("$attempt", version.Attempt));
        c.ExecuteNonQuery();
        return version.Id;
    });

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
                   $fills,$exposure,$missing,$faults,$gross,$fees,$net,$dd,$trace,$at,$role,$attempt)
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
            ("$role", run.Role), ("$attempt", run.Attempt));

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

    /// <summary>How many runs this installation has measured. What section 8 of the owner's report asks.</summary>
    public int RunCount => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COUNT(*) FROM strategy_run");
        return Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    /// <summary>One run's closed trades, in the order they were taken.</summary>
    public IReadOnlyList<StrategyTradeRow> TradesOf(string runId) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT run_id, ordinal, entry_bar, entry_price, exit_bar, exit_price, quantity, exit_reason,
                   fees, pnl
            FROM strategy_trade WHERE run_id=$id ORDER BY ordinal
            """, ("$id", runId));

        var rows = new List<StrategyTradeRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyTradeRow(
                r.GetString(0), r.GetInt32(1), Sql.Time(r.GetString(2)), Sql.Dec(r.GetString(3)),
                Sql.Time(r.GetString(4)), Sql.Dec(r.GetString(5)), Sql.Dec(r.GetString(6)),
                r.GetString(7), Sql.Dec(r.GetString(8)), Sql.Dec(r.GetString(9))));
        return rows;
    });

    static List<StrategyVersionRow> ReadVersions(SqliteCommand c)
    {
        var rows = new List<StrategyVersionRow>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new StrategyVersionRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetInt32(6), Sql.Time(r.GetString(7)),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
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
                r.IsDBNull(23) ? null : r.GetString(23), r.IsDBNull(24) ? null : r.GetString(24)));
        return rows;
    }
}
