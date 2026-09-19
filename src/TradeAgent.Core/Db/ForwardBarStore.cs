using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE FORWARD LEDGER: THE ONLY WRITER OF <c>forward_bar</c>, <c>forward_fetch</c> AND
/// <c>forward_gap</c>, and it writes all three in ONE transaction.
///
/// <para><b>Four properties, and the whole type exists to keep them.</b></para>
///
/// <list type="number">
/// <item><b>A bar is stored only when it is CLOSED.</b> Closed means its <c>close_time</c> precedes
/// the instant the answer arrived — the same test <c>KlineNormaliser</c> applies to an archive, with
/// the same instant playing the same part. The vendor's last row is usually the minute IN PROGRESS,
/// whose high, low, close and volume are all still moving; stored, it would be a bar that changes
/// after it was written, and every figure computed over it would be a figure about a candle that no
/// longer exists.</item>
/// <item><b>THE FIRST READING STANDS.</b> <c>INSERT … ON CONFLICT DO NOTHING</c>, enforced by SQLite
/// and not by a caller that checked first. A re-fetch of a minute already held does not overwrite
/// it, and a re-fetch that DISAGREES is counted in the fetch's own note rather than silently
/// replacing evidence a research run may already have been given. <c>INSERT OR REPLACE</c> here
/// would make this ledger's past editable by its vendor.</item>
/// <item><b>A gap is recorded and never filled.</b> Minutes with no bar are written down as a run,
/// once, where they were first seen. Nothing interpolates and nothing carries a price forward.</item>
/// <item><b>Every attempt is a row</b>, succeeded or failed, and the bars name the fetch they came
/// in. A stored bar whose fetch cannot be named is a bar with no provenance at all.</item>
/// </list>
///
/// <para><b>It is app-owned, like the dataset ledger.</b> There is no verb and no pipe op that
/// appends here; the collector in the app's own process is the only caller. What the agent can reach
/// is <see cref="Since"/> and <see cref="Series"/>, which are reads.</para>
/// </summary>
public sealed class ForwardBarStore(Database db)
{
    const string BarCols =
        "source, symbol, open_time, open, high, low, close, volume, close_time, received_at, fetch_id";

    /// <summary>
    /// WRITES ONE ATTEMPT AND WHATEVER OF IT MAY BE KEPT, in one transaction.
    ///
    /// <para>The fetch row goes in first because the bars reference it: a bar carries its fetch's id,
    /// so the window it arrived in is recoverable from the row itself. Then the CLOSED bars, then the
    /// gaps between what was already held and what arrived. A failure anywhere unwinds the lot —
    /// bars recorded against an attempt that was never written is the one state this cannot hold.</para>
    ///
    /// <para><paramref name="bars"/> is what the body PARSED to. A caller whose body did not parse
    /// passes none and puts the reason on <see cref="ForwardFetchAttempt.Note"/>; that is a recorded
    /// failure, and it is why this method cannot tell the difference between "no bars were published"
    /// and "the answer was unreadable" — the caller already has, in words.</para>
    /// </summary>
    public ForwardAppend Append(ForwardFetchAttempt fetch, IReadOnlyList<ForwardBars.Kline>? bars = null)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var arrived = bars ?? [];

        return db.Write(_ =>
        {
            // CLOSED, MEASURED AGAINST THE INSTANT THE ANSWER ARRIVED. Strictly before: a bar whose
            // close time EQUALS the read instant closed at that instant and the vendor may still be
            // writing it, and a boundary that admits it is a boundary that admits the open minute on
            // a fast machine.
            var closed = arrived.Where(b => b.CloseTime < fetch.ReceivedAt).ToList();
            var notClosed = arrived.Count - closed.Count;

            var lastHeld = LastOpen(fetch.Source, fetch.Symbol);

            var id = InsertFetch(fetch, closed);

            var stored = 0;
            var held = 0;
            var disagreed = 0;

            foreach (var bar in closed)
            {
                // THE READ DECIDES NOTHING ABOUT THE WRITE. It asks only whether the vendor has
                // changed its mind about a minute, so a disagreement can be counted — and the insert
                // below runs either way.
                var existing = Bar(fetch.Source, fetch.Symbol, bar.OpenTime);

                // THE INSERT IS WHAT REFUSES, NOT A BRANCH ABOVE IT. Measured here, and it is the
                // reason this loop is shaped the way it is: an earlier draft read the row and
                // `continue`d when it found one, which left `INSERT OR REPLACE` — the mutant this
                // table exists to stop — passing every test, because the replacing statement was
                // never reached. A check-then-skip cannot state that nothing was overwritten; only
                // the conflict clause can. It is the reading <see cref="Database.AddKvOnce"/> has
                // already had to take once, for a row whose existence ends a refusal.
                using var c = db.Cmd($"""
                    INSERT INTO forward_bar({BarCols})
                    VALUES($src,$sym,$open,$o,$h,$l,$c,$v,$close,$recv,$fetch)
                    ON CONFLICT(source, symbol, open_time) DO NOTHING
                    """,
                    ("$src", fetch.Source), ("$sym", fetch.Symbol), ("$open", Sql.T(bar.OpenTime)),
                    ("$o", Sql.D(bar.Open)), ("$h", Sql.D(bar.High)), ("$l", Sql.D(bar.Low)),
                    ("$c", Sql.D(bar.Close)), ("$v", Sql.D(bar.Volume)),
                    ("$close", Sql.T(bar.CloseTime)), ("$recv", Sql.T(fetch.ReceivedAt)), ("$fetch", id));

                if (c.ExecuteNonQuery() == 1) { stored++; continue; }

                held++;
                if (existing is not null
                    && (existing.Open != bar.Open || existing.High != bar.High || existing.Low != bar.Low
                        || existing.Close != bar.Close || existing.Volume != bar.Volume
                        || existing.CloseTime != bar.CloseTime))
                    disagreed++;
            }

            var gaps = RecordGaps(fetch, lastHeld, closed);

            // AND THE DISAGREEMENTS GO ON THE FETCH'S OWN NOTE, not on the bar: the bar is the first
            // reading and is untouched by definition, so the only place a later contradiction can be
            // recorded without editing evidence is the attempt that contradicted it.
            if (disagreed > 0)
                Note(id, $"{disagreed} of {closed.Count} bars in this answer DIFFER from the reading "
                         + "already held; the first reading stands and nothing was overwritten");

            return new ForwardAppend(id, stored, notClosed, held, disagreed, gaps)
            {
                LastOpen = closed.Count > 0 ? closed[^1].OpenTime : null
            };
        });
    }

    long InsertFetch(ForwardFetchAttempt fetch, IReadOnlyList<ForwardBars.Kline> closed)
    {
        using var c = db.Cmd("""
            INSERT INTO forward_fetch(source, symbol, url, requested_at, received_at, http_status,
                                      bars, first_open, last_open, body_sha256, note)
            VALUES($src,$sym,$url,$req,$recv,$status,$bars,$first,$last,$sha,$note);
            SELECT last_insert_rowid();
            """,
            ("$src", fetch.Source), ("$sym", fetch.Symbol), ("$url", fetch.Url),
            ("$req", Sql.T(fetch.RequestedAt)), ("$recv", Sql.T(fetch.ReceivedAt)),
            ("$status", fetch.HttpStatus), ("$bars", closed.Count),
            ("$first", closed.Count > 0 ? Sql.T(closed[0].OpenTime) : null),
            ("$last", closed.Count > 0 ? Sql.T(closed[^1].OpenTime) : null),
            ("$sha", fetch.BodySha256), ("$note", fetch.Note));

        return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    void Note(long fetchId, string extra)
    {
        using var c = db.Cmd(
            "UPDATE forward_fetch SET note = CASE WHEN note IS NULL OR note='' THEN $n "
            + "ELSE note || ' — ' || $n END WHERE id=$id",
            ("$n", extra), ("$id", fetchId));
        c.ExecuteNonQuery();
    }

    /// <summary>
    /// WRITES DOWN THE MINUTES THAT ARE NOT THERE — between the newest bar already held and the
    /// first bar of this answer, and between consecutive bars INSIDE it.
    ///
    /// <para>Both kinds, because they are the same fact seen from two sides: a collector that was
    /// down for ten minutes and a vendor that published nine of ten minutes both leave a hole, and a
    /// ledger that recorded only one of them would report a coverage it does not have.</para>
    ///
    /// <para>ON CONFLICT DO NOTHING on the run's first missing minute, so re-reading a window whose
    /// hole is already recorded writes nothing: a gap seen twice is one gap, and a second row would
    /// make the same absence count twice in every figure taken over this table.</para>
    ///
    /// <para>A hole is NOT recorded before the first bar this installation ever collected: there is
    /// no claim to make about minutes that passed before the collector existed.</para>
    /// </summary>
    IReadOnlyList<ForwardGapRun> RecordGaps(
        ForwardFetchAttempt fetch, DateTimeOffset? lastHeld, IReadOnlyList<ForwardBars.Kline> closed)
    {
        if (closed.Count == 0) return [];

        var runs = new List<ForwardGapRun>();
        var previous = lastHeld;

        foreach (var bar in closed)
        {
            if (previous is { } last && bar.OpenTime > last + ForwardBars.BarLength)
            {
                var missing = (int)((bar.OpenTime - last) / ForwardBars.BarLength) - 1;
                var run = new ForwardGapRun(fetch.Source, fetch.Symbol,
                    last + ForwardBars.BarLength, bar.OpenTime - ForwardBars.BarLength, missing,
                    fetch.ReceivedAt);

                using var c = db.Cmd("""
                    INSERT INTO forward_gap(source, symbol, from_open, to_open, bars_missing, seen_at)
                    VALUES($src,$sym,$from,$to,$n,$seen)
                    ON CONFLICT(source, symbol, from_open) DO NOTHING
                    """,
                    ("$src", run.Source), ("$sym", run.Symbol), ("$from", Sql.T(run.FromOpen)),
                    ("$to", Sql.T(run.ToOpen)), ("$n", run.BarsMissing), ("$seen", Sql.T(run.SeenAt)));

                if (c.ExecuteNonQuery() == 1) runs.Add(run);
            }

            if (previous is null || bar.OpenTime > previous) previous = bar.OpenTime;
        }

        return runs;
    }

    // ---------------------------------------------------------------------------------- the reads

    /// <summary>
    /// THE MOST ROWS ONE <see cref="Since"/> MAY RETURN, AND IT IS ONE PAST THE CAP A CALLER IS TOLD
    /// ABOUT.
    ///
    /// <para>That one row is the whole point. A reader bounded by <see cref="DatasetReader.MaxBars"/>
    /// asks for one more, sees that there is one, and REFUSES the window — which is the rule the
    /// archive reader already follows (<c>DatasetReader.Read</c> stops one bar past the cap). Clamped
    /// at the cap exactly, the pipe op could never tell "exactly ten thousand bars" from "more than
    /// ten thousand" and would hand back a silently shortened window. Measured here: a request for
    /// 10,050 forward bars came back as 10,000 with nothing in the reply saying so.</para>
    /// </summary>
    public const int MaxRows = DatasetReader.MaxBars + 1;

    /// <summary>
    /// THE BARS AFTER <paramref name="openExclusive"/>, ascending, at most <paramref name="limit"/>.
    ///
    /// <para>EXCLUSIVE, because the caller is a runner that has already consumed a bar and is asking
    /// what has happened since: inclusive would hand it the same minute twice on every poll, and a
    /// strategy that acted on each would be acting twice on one candle.</para>
    ///
    /// <para>The limit is capped at <see cref="MaxRows"/> rather than refused, because this read has
    /// no window to be a different window from: "the next N after X" is answered exactly, and the
    /// caller asks again with the last open time it got. A caller that DOES own a window — the pipe
    /// op — asks for one past the cap and refuses on what comes back.</para>
    /// </summary>
    public IReadOnlyList<ForwardBar> Since(string symbol, DateTimeOffset? openExclusive = null,
        int limit = DatasetReader.MaxBars, string source = ForwardBars.Source) => db.Read(_ =>
    {
        var take = Math.Clamp(limit, 0, MaxRows);
        if (take == 0) return (IReadOnlyList<ForwardBar>)[];

        using var c = db.Cmd($"""
            SELECT {BarCols} FROM forward_bar
            WHERE source=$src AND symbol=$sym AND ($after IS NULL OR open_time > $after)
            ORDER BY open_time LIMIT $n
            """,
            ("$src", source), ("$sym", symbol),
            ("$after", openExclusive is { } at ? Sql.T(at) : null), ("$n", take));

        var rows = new List<ForwardBar>();
        using var r = c.ExecuteReader();
        while (r.Read()) rows.Add(Read(r));
        return rows;
    });

    /// <summary>
    /// HOW OLD THE NEWEST CLOSED BAR IS AT <paramref name="now"/>, or null because there is none.
    ///
    /// <para>This is the FACT the program's <c>data_freshness</c> bound is checked against at
    /// dispatch. It is answered here, off the rows, and is never computed from "when the collector
    /// last ran": a collector that has been running happily against a vendor publishing nothing is
    /// exactly the case a liveness check must not report as fresh.</para>
    ///
    /// <para>A negative age — a bar newer than <paramref name="now"/>, which a clock moving backwards
    /// can produce — is answered as <see cref="TimeSpan.Zero"/> rather than as a bar from the future.</para>
    /// </summary>
    public TimeSpan? Freshness(string symbol, DateTimeOffset now, string source = ForwardBars.Source)
    {
        if (LastOpen(source, symbol) is not { } open) return null;
        var age = now - (open + ForwardBars.BarLength);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>The newest stored bar's OPEN time for one series, or null because there is none.</summary>
    public DateTimeOffset? LastOpen(string source, string symbol) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT MAX(open_time) FROM forward_bar WHERE source=$src AND symbol=$sym",
            ("$src", source), ("$sym", symbol));
        return Sql.TimeN(c.ExecuteScalar() is DBNull ? null : c.ExecuteScalar());
    });

    /// <summary>Every series this installation has collected forward, symbol order.</summary>
    public IReadOnlyList<ForwardSeries> All() => db.Read(_ =>
    {
        var keys = new List<(string Source, string Symbol)>();
        using (var c = db.Cmd("SELECT DISTINCT source, symbol FROM forward_bar ORDER BY source, symbol"))
        using (var r = c.ExecuteReader())
            while (r.Read()) keys.Add((r.GetString(0), r.GetString(1)));

        // A SERIES WITH NO BARS YET IS STILL A SERIES, when something has been ASKED for it: an
        // installation whose collector has been failing all morning must appear in `data-list` with
        // its error, not be absent as though nobody had tried.
        using (var c = db.Cmd("SELECT DISTINCT source, symbol FROM forward_fetch ORDER BY source, symbol"))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!keys.Contains(key)) keys.Add(key);
            }

        return (IReadOnlyList<ForwardSeries>)[.. keys.Select(k => Series(k.Symbol, k.Source))];
    });

    /// <summary>What one series is, computed from its rows. Never null: an unknown one reads empty.</summary>
    public ForwardSeries Series(string symbol, string source = ForwardBars.Source) => db.Read(_ =>
    {
        int bars;
        DateTimeOffset? first = null, last = null;
        using (var c = db.Cmd(
            "SELECT COUNT(*), MIN(open_time), MAX(open_time) FROM forward_bar WHERE source=$src AND symbol=$sym",
            ("$src", source), ("$sym", symbol)))
        using (var r = c.ExecuteReader())
        {
            r.Read();
            bars = r.GetInt32(0);
            if (!r.IsDBNull(1)) first = Sql.Time(r.GetString(1));
            if (!r.IsDBNull(2)) last = Sql.Time(r.GetString(2));
        }

        int gaps, missing;
        using (var c = db.Cmd(
            "SELECT COUNT(*), COALESCE(SUM(bars_missing),0) FROM forward_gap WHERE source=$src AND symbol=$sym",
            ("$src", source), ("$sym", symbol)))
        using (var r = c.ExecuteReader())
        {
            r.Read();
            gaps = r.GetInt32(0);
            missing = r.GetInt32(1);
        }

        DateTimeOffset? received = null;
        string? error = null;
        DateTimeOffset? erroredAt = null;
        using (var c = db.Cmd(
            "SELECT received_at, note, bars FROM forward_fetch WHERE source=$src AND symbol=$sym "
            + "ORDER BY id DESC LIMIT 1",
            ("$src", source), ("$sym", symbol)))
        using (var r = c.ExecuteReader())
            if (r.Read())
            {
                received = Sql.Time(r.GetString(0));
                // THE LAST ATTEMPT'S NOTE, AND ONLY WHEN IT IS THE LAST ONE. A note from an hour ago
                // reported beside a collector that has been working since is a fault the owner cannot
                // clear, and one they would eventually learn to ignore.
                if (!r.IsDBNull(1)) { error = r.GetString(1); erroredAt = received; }
            }

        return new ForwardSeries(source, symbol, ForwardBars.Interval, bars, first, last, gaps, missing,
            received)
        {
            LastError = error,
            LastErrorAt = erroredAt
        };
    });

    /// <summary>The gap runs of one series, oldest first. Listed, never filled.</summary>
    public IReadOnlyList<ForwardGapRun> Gaps(string symbol, string source = ForwardBars.Source,
        int limit = KlineNormaliser.MaxGapRunsListed) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT source, symbol, from_open, to_open, bars_missing, seen_at FROM forward_gap "
            + "WHERE source=$src AND symbol=$sym ORDER BY from_open LIMIT $n",
            ("$src", source), ("$sym", symbol), ("$n", Math.Max(0, limit)));

        var rows = new List<ForwardGapRun>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new ForwardGapRun(r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)),
                Sql.Time(r.GetString(3)), r.GetInt32(4), Sql.Time(r.GetString(5))));
        return (IReadOnlyList<ForwardGapRun>)rows;
    });

    /// <summary>How many gap RUNS were first seen in a window. The status line's <c>gaps_today</c>.</summary>
    public int GapsSeen(string symbol, DateTimeOffset from, DateTimeOffset to,
        string source = ForwardBars.Source) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*) FROM forward_gap WHERE source=$src AND symbol=$sym "
            + "AND seen_at >= $from AND seen_at < $to",
            ("$src", source), ("$sym", symbol), ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        return Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    /// <summary>How many attempts were made in a window, and how many of them recorded a failure.</summary>
    public (int Attempts, int Failed) Fetches(string symbol, DateTimeOffset from, DateTimeOffset to,
        string source = ForwardBars.Source) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT COUNT(*), COALESCE(SUM(CASE WHEN note IS NOT NULL THEN 1 ELSE 0 END),0) "
            + "FROM forward_fetch WHERE source=$src AND symbol=$sym AND received_at >= $from AND received_at < $to",
            ("$src", source), ("$sym", symbol), ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        using var r = c.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    });

    /// <summary>One stored bar, or null. The read <see cref="Append"/> uses to spot a disagreement.</summary>
    public ForwardBar? Bar(string source, string symbol, DateTimeOffset openTime) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {BarCols} FROM forward_bar WHERE source=$src AND symbol=$sym AND open_time=$open",
            ("$src", source), ("$sym", symbol), ("$open", Sql.T(openTime)));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>Every attempt against one series, newest first. The evidence a fetch really happened.</summary>
    public IReadOnlyList<(long Id, DateTimeOffset ReceivedAt, int? Status, int Bars, string? Note, string Url)>
        Attempts(string symbol, int limit = 50, string source = ForwardBars.Source) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT id, received_at, http_status, bars, note, url FROM forward_fetch "
            + "WHERE source=$src AND symbol=$sym ORDER BY id DESC LIMIT $n",
            ("$src", source), ("$sym", symbol), ("$n", Math.Max(0, limit)));

        var rows = new List<(long, DateTimeOffset, int?, int, string?, string)>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetInt64(0), Sql.Time(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt32(2),
                r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5)));
        return (IReadOnlyList<(long, DateTimeOffset, int?, int, string?, string)>)rows;
    });

    static ForwardBar Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Sql.Time(r.GetString(2)), Sql.Dec(r.GetString(3)),
        Sql.Dec(r.GetString(4)), Sql.Dec(r.GetString(5)), Sql.Dec(r.GetString(6)),
        Sql.Dec(r.GetString(7)), Sql.Time(r.GetString(8)), Sql.Time(r.GetString(9)), r.GetInt64(10));
}
