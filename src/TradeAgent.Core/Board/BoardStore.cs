using Microsoft.Data.Sqlite;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE ONE WRITER OF THE BOARD'S TABLES (<c>U-board-a1</c>; R24 § 4.1), and their one reader. <c>board_seat</c>, <c>board_policy</c>
/// and <c>board_event</c> are written here and nowhere else (a source guard holds that): the founding record, once, by the app at
/// rung 47 (<see cref="Found"/>), and the record of an earlier live setting set aside by the owner's press
/// (<see cref="RecordEarlierLiveSetAside"/>). Nothing here writes a policy version from a caller's value: there is no public writer of
/// a policy, a mode or an activation at all, and the board's acts (<c>U-board-b</c>) are not in this build.
///
/// <para><b>The policy in force is the highest version, read STRICTLY</b> (<see cref="BoardPolicyBody.Read"/>): a body that does not
/// verify against its hash, a field missing or unknown, or a row whose mode or activation differs from its body is refused — and the
/// answer is <see cref="BoardPolicy.Unreadable"/>, never the founding defaults re-derived.</para>
/// </summary>
public sealed class BoardStore(Database db)
{
    readonly Database _db = db;

    /// <summary>
    /// THE FOUNDING WRITE, ONCE (<c>U-board-a1</c> item 2): version 1 of the policy (<see cref="BoardFounding.V1"/>), the three seats
    /// of the founding composition and the <c>founded</c> event naming the schema the home was at and the board's budget share — in
    /// ONE transaction, and only while no policy row exists, so a rung re-run after a crash before its stamp writes nothing twice.
    /// Called by rung 47 alone; nothing is read from the settings row.
    /// </summary>
    internal static void Found(Database db, int homeSchema, DateTimeOffset at)
    {
        var seats = BoardFounding.Seats;
        if (seats.Any(s => Array.IndexOf(BoardSeats.SeatClasses, s.SeatClass) < 0)
            || seats.Select(s => s.ModelClass).Distinct(StringComparer.Ordinal).Count() < 2
            || seats.Any(s => !BoardSeats.IsBoardId(s.Id)))
            throw new InvalidOperationException("the founding composition is not three board seats on two model classes, none a cli");

        var policy = BoardFounding.V1;
        var body = BoardPolicyBody.Canonical(policy);
        var when = at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        db.Write(_ =>
        {
            using (var any = db.Cmd("SELECT COUNT(*) FROM board_policy"))
                if (Convert.ToInt64(any.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0) return 0;

            using (var p = db.Cmd("""
                INSERT INTO board_policy(version, decider, basis, written_at, mode, live_activated, body, body_sha256, supersedes)
                VALUES(1, $decider, $basis, $at, $mode, $live, $body, $sha, NULL)
                """, ("$decider", BoardDecider.App), ("$basis", BoardBasis.Founding), ("$at", when),
                ("$mode", policy.Mode.ToString()), ("$live", policy.LiveActivated ? 1 : 0), ("$body", body),
                ("$sha", BoardPolicyBody.Sha256(body))))
                p.ExecuteNonQuery();

            foreach (var s in seats)
            {
                using var c = db.Cmd("""
                    INSERT INTO board_seat(id, seat_class, model_class, vendor_family, status, founded_at)
                    VALUES($id, $class, $model, $vendor, $status, $at)
                    """, ("$id", s.Id), ("$class", s.SeatClass), ("$model", s.ModelClass), ("$vendor", s.VendorFamily),
                    ("$status", BoardSeatStatus.Dormant), ("$at", when));
                c.ExecuteNonQuery();
            }

            using var e = db.Cmd("""
                INSERT INTO board_event(at, kind, policy_version, home_schema, budget_share, detail)
                VALUES($at, 'founded', 1, $schema, $share, $detail)
                """, ("$at", when), ("$schema", homeSchema),
                ("$share", BoardFounding.BudgetShare.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("$detail", $"the app founded the board: policy version 1 decided by {BoardDecider.App} on the {BoardBasis.Founding} basis; "
                            + $"seats {string.Join(", ", seats.Select(s => s.Id))} dormant; the home was at schema {homeSchema}"));
            return e.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// THE RECORD OF THE OWNER'S PRESS OVER AN EARLIER ARMED HOME (<c>U-board-a1</c> item 5), written in the caller's own transaction
    /// beside the settings rows it moves. A record, not a decision: it names no policy version and changes none.
    /// </summary>
    public void RecordEarlierLiveSetAside(DateTimeOffset at, string detail) => _db.Write(_ =>
    {
        using var c = _db.Cmd("""
            INSERT INTO board_event(at, kind, policy_version, home_schema, budget_share, detail)
            VALUES($at, 'earlier-live-set-aside', NULL, NULL, NULL, $detail)
            """, ("$at", at.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("$detail", detail));
        return c.ExecuteNonQuery();
    });

    /// <summary>The schema stamp the home had when rung 47 founded the board — 0 for a home this build made — or null for none recorded.</summary>
    public int? FoundingHomeSchema() => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT home_schema FROM board_event WHERE kind='founded' ORDER BY id LIMIT 1");
        return c.ExecuteScalar() is long v ? (int?)v : null;
    });

    /// <summary>The board's founding budget share of the plan's day-total, as the founding event records it, or null for none.</summary>
    public decimal? BudgetShare() => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT budget_share FROM board_event WHERE kind='founded' ORDER BY id LIMIT 1");
        return c.ExecuteScalar() is string s
               && decimal.TryParse(s, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : (decimal?)null;
    });

    sealed record Row(int Version, string Decider, string Basis, DateTimeOffset WrittenAt, string Mode, long Live, string Body, string Sha);

    IReadOnlyList<Row> Rows() => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT version, decider, basis, written_at, mode, live_activated, body, body_sha256 FROM board_policy ORDER BY version");
        using var r = c.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
            rows.Add(new Row(r.GetInt32(0), r.GetString(1), r.GetString(2), Sql.Time(r.GetValue(3)), r.GetString(4), r.GetInt64(5),
                r.GetString(6), r.GetString(7)));
        return (IReadOnlyList<Row>)rows;
    });

    /// <summary>
    /// THE POLICY IN FORCE: the highest version, read strictly, with every field and the version that set it — or, when no version
    /// verifies, the unreadable shape with the reason. Never throws for a store that cannot be read: that is an unreadable policy too.
    /// </summary>
    public BoardPolicyInForce InForce()
    {
        IReadOnlyList<Row> rows;
        try { rows = Rows(); }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or FormatException or InvalidCastException)
        {
            return Unreadable(0, $"the board's policy could not be read from the store ({ex.Message})");
        }
        if (rows.Count == 0) return Unreadable(0, "no policy version is recorded");

        var top = rows[^1];
        if (top.Decider != BoardDecider.App || top.Basis != BoardBasis.Founding)
            return Unreadable(top.Version, $"version {top.Version} names a decider or basis this build does not know");
        var (policy, why) = BoardPolicyBody.Read(top.Body, top.Sha);
        if (policy is null) return Unreadable(top.Version, $"version {top.Version} does not verify: {why}");
        if (policy.Mode.ToString() != top.Mode || (policy.LiveActivated ? 1L : 0L) != top.Live)
            return Unreadable(top.Version, $"version {top.Version}'s mode or activation differs from its body's");

        // EACH FIELD'S VERSION: the latest version whose value for it differs from the one before — the founding record's for all, today.
        var setBy = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string>? previous = null;
        foreach (var row in rows)
        {
            Dictionary<string, string> fields;
            try { fields = BoardPolicyBody.Flatten(row.Body).ToDictionary(f => f.Path, f => f.Value, StringComparer.Ordinal); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException) { fields = []; }
            foreach (var (path, value) in fields)
                if (previous is null || !previous.TryGetValue(path, out var was) || was != value) setBy[path] = row.Version;
            previous = fields;
        }
        var shown = BoardPolicyBody.Flatten(top.Body)
            .Select(f => new BoardPolicyField(f.Path, f.Value, setBy.TryGetValue(f.Path, out var v) ? v : top.Version))
            .ToList();
        return new BoardPolicyInForce(top.Version, top.Decider, top.Basis, top.WrittenAt, top.Sha, policy, shown);
    }

    static BoardPolicyInForce Unreadable(int version, string why)
    {
        var policy = BoardPolicy.Unreadable();
        return new BoardPolicyInForce(version, "", "", default, "", policy,
            [.. BoardPolicyBody.Flatten(BoardPolicyBody.Canonical(policy)).Select(f => new BoardPolicyField(f.Path, f.Value, 0))])
        {
            CouldNotBeRead = true,
            Why = why
        };
    }

    /// <summary>Every version, oldest first: the founding record names the app as its one decider and carries no rationale, class or score.</summary>
    public IReadOnlyList<BoardPolicyVersion> History() =>
        [.. Rows().Select(r => new BoardPolicyVersion(r.Version, r.Decider, r.Basis, r.WrittenAt, r.Sha, null, [r.Decider], null, null))];

    /// <summary>The board's seats as their rows hold them, by id.</summary>
    public IReadOnlyList<BoardSeat> Seats() => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT id, seat_class, model_class, vendor_family, status, founded_at FROM board_seat ORDER BY id");
        using var r = c.ExecuteReader();
        var seats = new List<BoardSeat>();
        while (r.Read())
            seats.Add(new BoardSeat(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Sql.Time(r.GetValue(5))));
        return (IReadOnlyList<BoardSeat>)seats;
    });
}

/// <summary>
/// WHAT AN EARLIER BUILD LEFT ARMED IN THIS HOME'S SETTINGS ROW (<c>U-board-a1</c> item 5): the raw <c>mode</c> and
/// <c>live_activated</c> values as found (null where the key was absent), the schema stamp the home had when this build first opened
/// it, and the sentence that refuses it — naming both versions and the repair. Read only; the row itself is never rewritten by
/// reading it.
/// </summary>
public sealed record EarlierLiveSetting(string? Mode, string? LiveActivated, int HomeSchema, string Sentence);
