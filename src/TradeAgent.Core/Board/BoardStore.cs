using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>
/// THE ONE WRITER OF THE BOARD'S TABLES (<c>U-board-a1</c>, <c>U-board-b1</c>; R24 §§ 4.1–4.4), and their one reader. Every
/// <c>board_*</c> table is written here and nowhere else (a source guard holds that): the founding record, once, by the app at rung 49
/// (<see cref="Found"/>); the record of an earlier live setting set aside by the owner's press (<see cref="RecordEarlierLiveSetAside"/>);
/// and since rung 50 the board's ACTS — a narrowing by one active seat (<see cref="Narrow"/>), a proposal and another seat's decision on
/// it, their expiry, the owner's veto, which only narrows, and his in-process presses over the board's own power. No version is ever
/// written from a caller's POLICY: a version is the one in force with an act's checked intent applied, read back strictly, in the
/// act's own write — and no public writer takes a policy, a mode or an activation.
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
    /// Called by rung 49 alone; nothing is read from the settings row.
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
                            + $"seats {string.Join(", ", seats.Select(s => s.Id))} dormant; the home was at schema {homeSchema}; "
                            + BoardFounding.PaperValuesSource));
            return e.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// RUNG 50's COPY (<c>U-board-b1</c>): every row of rung 49's <c>board_policy</c> and <c>board_event</c>, set aside by the rung as
    /// <c>board_policy_49</c> and <c>board_event_49</c>, into the rebuilt tables BYTE FOR BYTE — the body, its hash, the time and every
    /// event's id as they were; no version names an act, no event a seat. Run inside the rung's own transaction, on its connection,
    /// and by nothing else.
    /// </summary>
    internal static void CopyForActsRung(Database db)
    {
        using (var p = db.Cmd("""
            INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes)
              SELECT version, decider, basis, NULL, written_at, mode, live_activated, body, body_sha256, supersedes
                FROM board_policy_49 ORDER BY version
            """))
            p.ExecuteNonQuery();
        using var e = db.Cmd("""
            INSERT INTO board_event(id, at, kind, policy_version, home_schema, budget_share, seat_id, detail)
              SELECT id, at, kind, policy_version, home_schema, budget_share, NULL, detail FROM board_event_49 ORDER BY id
            """);
        e.ExecuteNonQuery();
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

    /// <summary>The schema stamp the home had when rung 49 founded the board — 0 for a home this build made — or null for none recorded.</summary>
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
        if (!KnownDecision(top.Decider, top.Basis))
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

    /// <summary>The three pairs a version may stand on: the app's founding, a board act, the owner's veto. Anything else is unreadable.</summary>
    static bool KnownDecision(string decider, string basis) =>
        (decider, basis) is (BoardDecider.App, BoardBasis.Founding) or (BoardDecider.Board, BoardBasis.Act) or (BoardDecider.Owner, BoardBasis.Veto);

    /// <summary>
    /// EVERY VERSION, OLDEST FIRST — the owner's reader, never a seat's: the founding record names the app as its one decider and carries
    /// no rationale, class or score; a board act's version carries its rationale (the proposer's claim) and the seats that made it — the
    /// proposer, and for a confirmed proposal the confirmer; the owner's veto names him. No version is scored or classed yet.
    /// </summary>
    public IReadOnlyList<BoardPolicyVersion> History()
    {
        var acts = Acts().ToDictionary(a => a.Act.Id, StringComparer.Ordinal);
        var actOf = ActIds();
        return
        [
            .. Rows().Select(r =>
            {
                if (!actOf.TryGetValue(r.Version, out var actId) || actId is null || !acts.TryGetValue(actId, out var act))
                    return new BoardPolicyVersion(r.Version, r.Decider, r.Basis, r.WrittenAt, r.Sha, null, [r.Decider], null, null);
                if (r.Decider == BoardDecider.Owner)
                    return new BoardPolicyVersion(r.Version, r.Decider, r.Basis, r.WrittenAt, r.Sha, null, [BoardDecider.Owner],
                        act.Act.EvidenceClass, null);
                var deciders = new List<string> { act.Act.Seat };
                deciders.AddRange(act.Events.Where(e => e.Kind == BoardActEventKind.Confirmed).Select(e => e.Seat));
                return new BoardPolicyVersion(r.Version, r.Decider, r.Basis, r.WrittenAt, r.Sha, act.Act.Rationale, deciders,
                    act.Act.EvidenceClass, null);
            })
        ];
    }

    Dictionary<int, string?> ActIds() => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT version, act_id FROM board_policy ORDER BY version");
        using var r = c.ExecuteReader();
        var rows = new Dictionary<int, string?>();
        while (r.Read()) rows[r.GetInt32(0)] = Sql.S(r.GetValue(1));
        return rows;
    });

    /// <summary>
    /// THE BOARD'S SEATS, by id, each with its STATE NOW: the latest <c>board_seat_state</c> row — <c>dormant</c>, <c>active</c> or
    /// <c>retired</c> — or, with none, the row's own <c>dormant</c>. A seat's row is sealed, so its life is on the state table.
    /// </summary>
    public IReadOnlyList<BoardSeat> Seats() => _db.Read(_ =>
    {
        using var c = _db.Cmd("""
            SELECT s.id, s.seat_class, s.model_class, s.vendor_family,
                   COALESCE((SELECT t.state FROM board_seat_state t WHERE t.seat = s.id ORDER BY t.id DESC LIMIT 1), s.status),
                   s.founded_at
              FROM board_seat s ORDER BY s.id
            """);
        using var r = c.ExecuteReader();
        var seats = new List<BoardSeat>();
        while (r.Read())
            seats.Add(new BoardSeat(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Sql.Time(r.GetValue(5))));
        return (IReadOnlyList<BoardSeat>)seats;
    });

    /// <summary>One seat with its state now, or null for an id that is no board seat.</summary>
    public BoardSeat? Seat(string id) => Seats().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Whether any board seat is ACTIVE now — read from the state table. No product path writes <c>active</c> in this build.</summary>
    public bool AnyActive() => _db.Read(_ =>
    {
        using var c = _db.Cmd("""
            SELECT COUNT(*) FROM board_seat s
             WHERE (SELECT t.state FROM board_seat_state t WHERE t.seat = s.id ORDER BY t.id DESC LIMIT 1) = 'active'
            """);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    });

    /// <summary>
    /// THE TEST HOST'S SEAM OVER A SEAT'S STATE (<c>U-board-b1</c> item 6) — THE ONLY WRITER OF <c>active</c> in this build: internal,
    /// visible to the test projects alone, called from nowhere in <c>src/</c> (the <c>…UnderTest</c> guard). Nothing in the product
    /// activates a board seat before a RECORDED proven cell (<c>U-board-activate</c>); b1's tests stand in for that writer here.
    /// </summary>
    internal void SetSeatStateUnderTest(string seat, string state, DateTimeOffset at) => _db.Write(_ =>
    {
        using var c = _db.Cmd("INSERT INTO board_seat_state(seat, state, by, at) VALUES($seat, $state, 'app', $at)",
            ("$seat", seat), ("$state", state), ("$at", Sql.T(at)));
        return c.ExecuteNonQuery();
    });

    // ---- the acts ---------------------------------------------------------------------------------------------------------------

    /// <summary>The prefixes of the ids this store mints; a caller never names one.</summary>
    public const string ActPrefix = "bact-", ForecastPrefix = "bfc-", PowerPrefix = "bpow-";

    static string Mint(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>
    /// ONE SEAT NARROWS, AT ONCE (<c>U-board-b1</c> item 3; R24 § 4.2). Inside ONE write: the seat is ACTIVE; the intent parses and
    /// NARROWS — every change narrows or leaves alone, by <see cref="BoardDirection"/>'s one table; every <c>from</c> is the value in
    /// force; the refs are records the app holds; the forecasts are the closed vocabulary's — then the act, the version it writes
    /// (decided by <c>board</c> on the basis <c>act</c>, superseding the one in force, its body read back strictly), its
    /// <c>applied</c> event and its forecasts. Refused in words with nothing written otherwise.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public BoardActResult Narrow(BoardActRequest request, DateTimeOffset at) => _db.Write(_ =>
    {
        var (intent, inForce) = Checked(request, at);
        if (!intent.Narrows)
            throw Refused($"this intent widens ({Widening(intent)}): one seat narrows, and a widening is proposed with 'board-propose' "
                          + "and applied only when another seat confirms it.");
        var act = InsertAct(request, BoardActKind.Narrow, intent, inForce.Version, null, at);
        var version = InsertVersion(inForce, Applied(BodyOf(inForce), intent.Changes), BoardDecider.Board, BoardBasis.Act, act.Id, at);
        InsertEvent(act.Id, BoardActEventKind.Applied, request.Seat, request.Attempt, null, null, null, null, version,
            $"applied at once by {request.Seat}: version {version} narrows version {inForce.Version}", at);
        InsertForecasts(act.Id, request, at);
        return new BoardActResult(act, BoardActEventKind.Applied, version,
            $"Applied: version {version} of the policy, decided by {request.Seat} alone because every change narrows, is in force now.");
    });

    /// <summary>
    /// WHAT EVERY ACT IS CHECKED FOR, inside its write: the seat ACTIVE, the intent strict, the policy in force readable, every
    /// <c>from</c> the value in force, the rationale the seat's own words, the refs there, the forecasts at most the bound and each valid.
    /// </summary>
    (BoardIntent Intent, BoardPolicyInForce InForce) Checked(BoardActRequest request, DateTimeOffset at)
    {
        RequireActive(request.Seat);
        var (intent, why) = BoardIntent.Parse(request.Intent);
        if (intent is null) throw Refused($"the intent is refused: {why}.");
        var inForce = InForce();
        if (inForce.CouldNotBeRead)
            throw Refused($"the policy in force could not be read ({inForce.Why}), so no act can be weighed against it.");
        if (Moved(intent, BodyOf(inForce)) is { Count: > 0 } moved)
            throw Refused($"the intent's 'from' is not what version {inForce.Version} holds: {string.Join("; ", moved)}. "
                          + "An act names the value in force: read it again with 'board-show'.");
        RequireRationale(request.Rationale);
        if (OrgStore.RefsRefusal(_db, request.Refs) is { } badRef) throw Refused(badRef);
        if (request.Forecasts.Count > BoardLimits.MaxForecasts)
            throw Refused($"an act carries at most {BoardLimits.MaxForecasts} forecasts.");
        var org = new OrgStore(_db);
        foreach (var f in request.Forecasts) org.CheckForecast(request.Seat, f, at);
        return (intent, inForce);
    }

    /// <summary>The canonical body of the version in force, as stored.</summary>
    string BodyOf(BoardPolicyInForce inForce) => Rows().Single(r => r.Version == inForce.Version).Body;

    /// <summary>Each change whose <c>from</c> is not the value <paramref name="body"/> holds, in words — empty when none moved.</summary>
    static List<string> Moved(BoardIntent intent, string body)
    {
        var moved = new List<string>();
        foreach (var c in intent.Changes)
        {
            var now = ValueAt(body, c.Field);
            if (!BoardDirection.SameValue(now, c.From)) moved.Add($"{c.Field} holds {now}, not {c.From}");
        }
        return moved;
    }

    static string Widening(BoardIntent intent) =>
        string.Join(", ", intent.Changes.Where(c => BoardDirection.Of(c.Field, c.From, c.To) == BoardDirectionKind.Widens)
            .Select(c => $"{c.Field} {c.From} → {c.To}"));

    void RequireActive(string seat)
    {
        var state = BoardSeats.IsBoardId(seat) ? Seat(seat)?.Status : null;
        if (state is null)
            throw Forbidden($"'{seat}' is no seat of the security board: its acts are made by its active seats alone.");
        if (state != BoardSeatStatus.Active)
            throw Forbidden($"board seat {seat} is {state}, not active: only an active seat acts (ORCHESTRATION § 2.3).");
    }

    static void RequireRationale(string? rationale)
    {
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > BoardLimits.MaxRationaleChars)
            throw Refused($"an act says why, in the seat's own words: 1 to {BoardLimits.MaxRationaleChars} characters.");
    }

    /// <summary>The canonical text of the value at <paramref name="field"/> in <paramref name="body"/> (<c>null</c> where it holds none).</summary>
    internal static string ValueAt(string body, string field)
    {
        JsonNode? node = JsonNode.Parse(body);
        foreach (var part in field.Split('.'))
            node = node is JsonObject o && o.TryGetPropertyValue(part, out var next)
                ? next
                : throw Refused($"the policy's body holds no '{field}'.");
        return node is null ? "null" : TapeJson.Canonical(node.ToJsonString());
    }

    /// <summary>
    /// THE BODY <paramref name="changes"/> MAKE OF <paramref name="body"/>: each change's <c>to</c> set at its path, the whole canonical,
    /// and READ BACK STRICTLY (<see cref="BoardPolicyBody.Read"/>) — a body this build would not write, a live mode or real money on is
    /// refused, never stored.
    /// </summary>
    static string Applied(string body, IEnumerable<BoardChange> changes)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        foreach (var c in changes)
        {
            var parts = c.Field.Split('.');
            var parent = root;
            foreach (var part in parts[..^1])
                parent = parent[part] as JsonObject ?? throw Refused($"the policy's body holds no '{c.Field}'.");
            if (!parent.ContainsKey(parts[^1])) throw Refused($"the policy's body holds no '{c.Field}'.");
            parent[parts[^1]] = JsonNode.Parse(c.To);
        }
        var next = TapeJson.Canonical(root.ToJsonString());
        var (policy, why) = BoardPolicyBody.Read(next, BoardPolicyBody.Sha256(next));
        if (policy is null) throw Refused($"the policy this act would write is not one this build writes: {why}.");
        if (policy.ModeIsLive || policy.LiveActivated) throw Refused("no act of this build selects a real-money mode or switches real money on.");
        return next;
    }

    BoardActRow InsertAct(BoardActRequest request, string kind, BoardIntent intent, int policyVersion, DateTimeOffset? expires, DateTimeOffset at)
    {
        var row = new BoardActRow(Mint(ActPrefix), kind, request.Seat, request.Attempt, intent.Canonical, intent.Sha256, policyVersion,
            request.Rationale, request.Refs, null, expires, at);
        using var c = _db.Cmd("""
            INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at)
            VALUES($id, $kind, $seat, $attempt, $intent, $sha, $version, $rationale, $refs, NULL, $expires, $at)
            """, ("$id", row.Id), ("$kind", kind), ("$seat", row.Seat), ("$attempt", row.Attempt), ("$intent", row.Intent),
            ("$sha", row.IntentSha256), ("$version", policyVersion), ("$rationale", row.Rationale),
            ("$refs", JsonSerializer.Serialize(request.Refs.Select(r => new Dictionary<string, string> { ["kind"] = r.Kind, ["id"] = r.Id }))),
            ("$expires", expires is { } e ? Sql.T(e) : null), ("$at", Sql.T(at)));
        c.ExecuteNonQuery();
        return row;
    }

    /// <summary>The next version: <paramref name="body"/>, read back strictly, decided by <paramref name="decider"/> on <paramref name="basis"/>.</summary>
    int InsertVersion(BoardPolicyInForce inForce, string body, string decider, string basis, string actId, DateTimeOffset at)
    {
        var (policy, why) = BoardPolicyBody.Read(body, BoardPolicyBody.Sha256(body));
        if (policy is null) throw Refused($"the policy this act would write is not one this build writes: {why}.");
        var version = inForce.Version + 1;
        using var c = _db.Cmd("""
            INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes)
            VALUES($version, $decider, $basis, $act, $at, $mode, 0, $body, $sha, $supersedes)
            """, ("$version", version), ("$decider", decider), ("$basis", basis), ("$act", actId), ("$at", Sql.T(at)),
            ("$mode", policy.Mode.ToString()), ("$body", body), ("$sha", BoardPolicyBody.Sha256(body)), ("$supersedes", inForce.Version));
        c.ExecuteNonQuery();
        return version;
    }

    void InsertEvent(string actId, string kind, string seat, string? attempt, string? rationale, string? proposerFamily,
        string? deciderFamily, bool? sameFamily, int? version, string? detail, DateTimeOffset at)
    {
        using var c = _db.Cmd("""
            INSERT INTO board_act_event(act_id, kind, seat, attempt_id, rationale, proposer_family, decider_family, same_family, policy_version, detail, at)
            VALUES($act, $kind, $seat, $attempt, $rationale, $pf, $df, $same, $version, $detail, $at)
            """, ("$act", actId), ("$kind", kind), ("$seat", seat), ("$attempt", attempt), ("$rationale", rationale), ("$pf", proposerFamily),
            ("$df", deciderFamily), ("$same", sameFamily is { } s ? (s ? 1 : 0) : null), ("$version", version), ("$detail", detail),
            ("$at", Sql.T(at)));
        c.ExecuteNonQuery();
    }

    void InsertForecasts(string actId, BoardActRequest request, DateTimeOffset at)
    {
        var org = new OrgStore(_db);
        foreach (var f in request.Forecasts)
        {
            var (subject, unscored) = org.CheckForecast(request.Seat, f, at);
            using var c = _db.Cmd($"""
                INSERT INTO board_forecast({ForecastCols})
                VALUES($id, $act, $f, $attempt, $vocabulary, $predicate, $subject, $target, $by, $probability, $scored, $why, $at)
                """, ("$id", Mint(ForecastPrefix)), ("$act", actId), ("$f", request.Seat), ("$attempt", request.Attempt),
                ("$vocabulary", ForecastVocabulary.Version), ("$predicate", f.Predicate), ("$subject", subject), ("$target", f.Target),
                ("$by", Sql.T(f.By)), ("$probability", Sql.D(f.Probability)), ("$scored", unscored is null ? 1 : 0), ("$why", unscored),
                ("$at", Sql.T(at)));
            c.ExecuteNonQuery();
        }
    }

    // ---- reads of the acts ------------------------------------------------------------------------------------------------------

    const string ActCols = "id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at";

    const string EventCols =
        "id, act_id, kind, seat, attempt_id, rationale, proposer_family, decider_family, same_family, policy_version, detail, at";

    const string ForecastCols =
        "id, act_id, forecaster, attempt_id, vocabulary, predicate, subject, target, resolve_by, probability, scored, unscored_why, at";

    /// <summary>One act as its row holds it, or null.</summary>
    public BoardActRow? ActRow(string id) => _db.Read(_ =>
    {
        using var c = _db.Cmd($"SELECT {ActCols} FROM board_act WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadAct(r) : null;
    });

    /// <summary>An act's events, oldest first.</summary>
    public IReadOnlyList<BoardActEventRow> Events(string actId) => _db.Read(_ =>
    {
        using var c = _db.Cmd($"SELECT {EventCols} FROM board_act_event WHERE act_id=$id ORDER BY id", ("$id", actId));
        using var r = c.ExecuteReader();
        var rows = new List<BoardActEventRow>();
        while (r.Read()) rows.Add(ReadEvent(r));
        return (IReadOnlyList<BoardActEventRow>)rows;
    });

    /// <summary>An act's forecasts, oldest first.</summary>
    public IReadOnlyList<BoardForecastRow> Forecasts(string actId) => _db.Read(_ =>
    {
        using var c = _db.Cmd($"SELECT {ForecastCols} FROM board_forecast WHERE act_id=$id ORDER BY at, id", ("$id", actId));
        using var r = c.ExecuteReader();
        var rows = new List<BoardForecastRow>();
        while (r.Read()) rows.Add(ReadForecast(r));
        return (IReadOnlyList<BoardForecastRow>)rows;
    });

    /// <summary>An act's status now: vetoed if the owner vetoed it, else its closing event's kind, else <see cref="BoardActStatus.Open"/>.</summary>
    public string Status(string actId)
    {
        var events = Events(actId);
        if (events.Any(e => e.Kind == BoardActEventKind.Vetoed)) return BoardActEventKind.Vetoed;
        return events.LastOrDefault(e => BoardActEventKind.Closing.Contains(e.Kind))?.Kind ?? BoardActStatus.Open;
    }

    /// <summary>One act whole — the owner's view: every event with every rationale, the forecasts and their resolutions — or null.</summary>
    public BoardActRecord? Act(string id) => ActRow(id) is { } row ? Whole(row) : null;

    /// <summary>Every act whole, newest first — the owner's view.</summary>
    public IReadOnlyList<BoardActRecord> Acts() => [.. ActRows().Select(Whole)];

    IReadOnlyList<BoardActRow> ActRows() => _db.Read(_ =>
    {
        using var c = _db.Cmd($"SELECT {ActCols} FROM board_act ORDER BY at DESC, id DESC");
        using var r = c.ExecuteReader();
        var rows = new List<BoardActRow>();
        while (r.Read()) rows.Add(ReadAct(r));
        return (IReadOnlyList<BoardActRow>)rows;
    });

    BoardActRecord Whole(BoardActRow row)
    {
        var forecasts = Forecasts(row.Id);
        var resolutions = forecasts.Select(f => Resolution(f.Id)).OfType<ResolutionRow>().ToList();
        return new BoardActRecord(row, Status(row.Id), Events(row.Id), forecasts, resolutions);
    }

    /// <summary>The app's resolution of a board forecast, or null while it is open.</summary>
    public ResolutionRow? Resolution(string forecastId) => _db.Read(_ =>
    {
        using var c = _db.Cmd("SELECT forecast_id, outcome, record_kind, record_id, why, at FROM board_forecast_resolution WHERE forecast_id=$id",
            ("$id", forecastId));
        using var r = c.ExecuteReader();
        return r.Read()
            ? new ResolutionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), Sql.Time(r.GetValue(5)))
            : null;
    });

    /// <summary>
    /// THE BOARD'S OPEN FORECASTS, oldest first, in <see cref="ForecastRow"/>'s shape for <see cref="ForecastResolver"/> — the act's id
    /// in neither the decision's nor the mandate's place — and each open row the store cannot read, refused by id, never guessed.
    /// </summary>
    public OpenForecastList OpenForecasts() => _db.Read(_ =>
    {
        using var c = _db.Cmd($"""
            SELECT {ForecastCols} FROM board_forecast f
             WHERE NOT EXISTS (SELECT 1 FROM board_forecast_resolution r WHERE r.forecast_id = f.id)
             ORDER BY at, id
            """);
        using var r = c.ExecuteReader();
        var rows = new List<ForecastRow>();
        var refused = new List<string>();
        while (r.Read())
        {
            if (r.GetValue(9) is string text && ForecastVocabulary.IsProbabilityText(text))
            {
                var f = ReadForecast(r);
                rows.Add(new ForecastRow(f.Id, null, null, f.Forecaster, f.Attempt, f.Vocabulary, f.Predicate, f.Subject, f.Target,
                    f.ResolveBy, f.Probability, f.Scored, f.UnscoredWhy, f.At));
            }
            else
                refused.Add($"board forecast {r.GetString(0)} holds a probability that is not a decimal text from 0 to 1: it is left unresolved, never guessed.");
        }
        return new OpenForecastList(rows, refused);
    });

    /// <summary>
    /// THE APP'S RESOLUTION OF ONE BOARD FORECAST, one immutable row in its own write — written only where none stands, so a second
    /// pass or a restart writes nothing twice. Answers whether it wrote one.
    /// </summary>
    internal bool RecordResolution(string forecastId, string outcome, string recordKind, string recordId, string why, DateTimeOffset at) =>
        _db.Write(_ =>
        {
            using var c = _db.Cmd("""
                INSERT INTO board_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at)
                SELECT $f, $outcome, $kind, $id, $why, $at
                 WHERE NOT EXISTS (SELECT 1 FROM board_forecast_resolution WHERE forecast_id = $f)
                """, ("$f", forecastId), ("$outcome", outcome), ("$kind", recordKind), ("$id", recordId), ("$why", why), ("$at", Sql.T(at)));
            return c.ExecuteNonQuery() == 1;
        });

    static BoardActRow ReadAct(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Sql.S(r.GetValue(3)), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetString(7),
        ReadRefs(r.GetString(8)), Sql.S(r.GetValue(9)), Sql.TimeN(r.GetValue(10)), Sql.Time(r.GetValue(11)));

    static BoardActEventRow ReadEvent(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.S(r.GetValue(4)), Sql.S(r.GetValue(5)), Sql.S(r.GetValue(6)),
        Sql.S(r.GetValue(7)), r.IsDBNull(8) ? null : r.GetInt64(8) != 0, r.IsDBNull(9) ? null : r.GetInt32(9), Sql.S(r.GetValue(10)),
        Sql.Time(r.GetValue(11)));

    static BoardForecastRow ReadForecast(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Sql.S(r.GetValue(3)), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7),
        Sql.Time(r.GetValue(8)), decimal.Parse(r.GetString(9), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
        r.GetInt64(10) != 0, Sql.S(r.GetValue(11)), Sql.Time(r.GetValue(12)));

    static IReadOnlyList<OrgRef> ReadRefs(string json) =>
        [.. (JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json) ?? []).Select(d => new OrgRef(d["kind"], d["id"]))];

    static TradeAgentException Refused(string why) => new(ErrorCode.INVALID_REQUEST, why + " Nothing was written.");

    /// <summary>An act refused on RIGHTS — the caller is no active seat, decides its own act, or is the wrong family: MARKED a refusal.</summary>
    static TradeAgentException Forbidden(string why) => RefusalMark.Mark(Refused(why));
}

/// <summary>
/// WHAT AN EARLIER BUILD LEFT ARMED IN THIS HOME'S SETTINGS ROW (<c>U-board-a1</c> item 5): the raw <c>mode</c> and
/// <c>live_activated</c> values as found (null where the key was absent), the schema stamp the home had when this build first opened
/// it, and the sentence that refuses it — naming both versions and the repair. Read only; the row itself is never rewritten by
/// reading it.
/// </summary>
public sealed record EarlierLiveSetting(string? Mode, string? LiveActivated, int HomeSchema, string Sentence);
