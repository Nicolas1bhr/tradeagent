namespace TradeAgent.Core.Db;

/// <summary>
/// ONE KIND OF ARTIFACT AND WHERE ITS LINEAGE IS WRITTEN: the table, its id column, the column naming the position that
/// authored it and the column naming the attempt that made it. <paramref name="AppAuthors"/> are the app's own principals
/// that may author this kind with no attempt — the referee's holdout run is the app's measurement, not anybody's turn.
/// </summary>
public sealed record ArtifactKindSpec(string Kind, string Table, string IdColumn, string AuthorColumn, string AttemptColumn,
    IReadOnlyList<string> AppAuthors);

/// <summary>The kinds of artifact the contribution ledger will flow along (R26 stage 2 unit 1), by their one spelling.</summary>
public static class ArtifactKind
{
    public const string Version = "version", Run = "run", Delivery = "delivery", LedgerEntry = "ledger-entry",
        Decision = "decision", Forecast = "forecast";
}

/// <summary>What a lineage read found on one artifact row.</summary>
public static class LineageState
{
    /// <summary>The row names its author position and an attempt the launch ledger holds.</summary>
    public const string Recorded = "recorded";

    /// <summary>The app's own principal wrote it and no attempt made it (the referee's holdout run).</summary>
    public const string AppMeasured = "app-measured";

    /// <summary>The row names no author or no attempt: written before the rung that required both. Never backfilled.</summary>
    public const string NotRecorded = "not-recorded";

    /// <summary>The row names an attempt the launch ledger does not hold: a reference nothing resolves.</summary>
    public const string Dangling = "dangling";
}

/// <summary>One record an attempt's packet showed it (<c>ai_attempt_input</c>), by its kind and id — a reference, never a copy.</summary>
public sealed record AttemptGiven(string Kind, string RefId);

/// <summary>One call an attempt made through the app (<c>tool_call</c>): the tool, the app's summary of it, and whether it was served.</summary>
public sealed record AttemptRead(string Tool, string? Argument, long Bytes, bool Served);

/// <summary>
/// ONE ARTIFACT'S LINEAGE AS RECORDED: its author position, the attempt that made it, what that attempt's packet gave it and what it
/// read through the app. <see cref="Label"/> is the sentence a reader shows; for a row written before the rung it says so.
/// </summary>
/// <param name="PacketRecorded">Whether the attempt's packet record is held. It is written only with the turn's committed outcome, so
/// false with <see cref="Given"/> empty is UNKNOWN (the packet showed nothing, the turn has not committed, or it ended before its
/// commit) and never "nothing was shown".</param>
public sealed record ArtifactLineageRow(string Kind, string Id, string? Author, string? Attempt, string State, string Label,
    IReadOnlyList<AttemptGiven> Given, IReadOnlyList<AttemptRead> Read, bool PacketRecorded = false);

/// <summary>
/// EVERY ARTIFACT NAMES ITS AUTHOR POSITION AND THE ATTEMPT WHOSE READS PRODUCED IT (<c>U-lineage-check</c>; VISION § 7.2: "Lineage
/// not recorded when an artifact is made cannot be rebuilt"; § 11: "check that every artifact records its author position and its
/// inputs, what an attempt read included").
///
/// <para><b>What an attempt read is already recorded, by the attempt.</b> <c>ai_attempt_input</c> (rung 45) holds every record its
/// position packet showed it; <c>tool_call</c> holds every call it made through the app's door or harness, a data export included.
/// So an artifact's inputs are reached through ONE reference — its attempt — and this type makes that reference unskippable: an
/// artifact row without an author position, or without an attempt when a position wrote it, is refused.</para>
///
/// <para><b>Two layers, one list.</b> Each writer checks <see cref="Require"/> before it writes and refuses in words; and rung
/// <see cref="Versions.ArtifactLineageRung"/> puts a BEFORE INSERT trigger built from <see cref="Kinds"/> on every artifact table, so
/// a writer added later that forgets the check is refused by the database. The conformance test reads the schema: every table
/// with an attempt column is either in <see cref="Kinds"/> (and carries its trigger) or in <see cref="NotArtifacts"/> with the
/// reason it is not one.</para>
///
/// <para><b>Presence, not existence, at the database.</b> The trigger requires an attempt to be named; that it resolves to an
/// <c>ai_attempt</c> row is what <see cref="Of"/> reports (<see cref="LineageState.Dangling"/>), never a guess either way. Every
/// production writer takes the attempt from the launch grant, which the launch ledger opened.</para>
///
/// <para><b>Old rows stay as they were.</b> No backfill (CLAUDE.md "No backward compatibility while we build"): a row written
/// before the rung reads <see cref="LineageState.NotRecorded"/> with a label saying so — unknown, never inferred from a folder,
/// a neighbour row or the clock.</para>
///
/// <para><b>What stays unobserved</b>, stated rather than implied: a CLI seat reading files in its own folder, and a served
/// <c>trade</c> pipe call from a CLI seat (only its refusals are rows) — the attempt reference is there, its reads outside the
/// app's door are not.</para>
/// </summary>
public static class ArtifactLineage
{
    /// <summary>The artifact tables, their author and attempt columns. A kind added here takes a new rung for its trigger.</summary>
    public static readonly IReadOnlyList<ArtifactKindSpec> Kinds =
    [
        new(ArtifactKind.Version, "strategy_version", "id", "role", "attempt", []),
        new(ArtifactKind.Run, "strategy_run", "id", "role", "attempt", [AppPrincipals.Referee]),
        new(ArtifactKind.Delivery, "org_delivery", "id", "author", "attempt_id", []),
        new(ArtifactKind.LedgerEntry, "ledger_entry", "id", "author", "attempt", []),
        new(ArtifactKind.Decision, "org_decision", "id", "position", "attempt_id", []),
        new(ArtifactKind.Forecast, "org_forecast", "id", "forecaster", "attempt_id", []),
    ];

    /// <summary>
    /// TABLES WITH AN ATTEMPT COLUMN THAT ARE NOT ARTIFACTS, and why. The conformance test refuses a table with an attempt column
    /// that is in neither list, so a new artifact cannot arrive without deciding which it is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotArtifacts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ai_attempt_input"] = "what an attempt was given: the attempt's own reads",
        ["tool_call"] = "what an attempt asked the app for: the attempt's own reads",
        ["ai_attempt_funding"] = "the launch's funding chain: the launch ledger's own record",
        ["ledger_revision"] = "a revision of an entry, whose entry is the artifact; its own attempt is required by trigger " + RevisionTrigger,
        ["ledger_link"] = "the app's link from an entry to a record it answered: a measurement, not authored",
        ["org_mandate"] = "an assignment: the decider's instruction, not work product",
        ["org_mandate_event"] = "a mandate's transition: a log row",
        ["org_escalation"] = "a message up the chart, not work product",
        ["org_lateral"] = "a note between positions, not work product",
        ["strategy_verdict"] = "a request for the referee's verdict: the app measures it",
        ["publication"] = "the council's published prose, written by the app on a role's behalf as often as by a turn",
        ["board_act"] = "a board seat's act: the board's own ledger, bound to its exact intent",
        ["board_act_event"] = "a board act's transition: a log row",
        ["board_forecast"] = "a board seat's forecast: the board's own ledger, outside the organisation's chart",
        ["cell_assurance"] = "the assurance a cell launch ran under: the app's measurement",
        ["cell_assurance_row"] = "one row of a cell launch's assurance: the app's measurement",
        ["board_power_proposal"] = "a board seat's proposal of a power change: the board's own ledger",
    };

    /// <summary>The spec of <paramref name="kind"/>; an unknown kind is a programming error.</summary>
    public static ArtifactKindSpec Spec(string kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "no such artifact kind");

    /// <summary>The trigger on <c>ledger_revision</c>: a revision is new text, so it names the attempt that wrote it (its author is its entry's).</summary>
    public const string RevisionTrigger = "lineage_ledger_revision";

    /// <summary>What counts as no value at all, in the triggers and in <see cref="Of"/> alike: NULL, or nothing but ASCII whitespace.</summary>
    static readonly char[] Space = [' ', '\t', '\n', '\v', '\f', '\r'];

    /// <summary>The SQL spelling of "blank" for <paramref name="column"/>, the same set as <see cref="Space"/>.</summary>
    static string BlankSql(string column) => $"({column} IS NULL OR trim({column}, ' ' || char(9, 10, 11, 12, 13)) = '')";

    static bool Blank(string? value) => value is null || value.Trim(Space).Length == 0;

    /// <summary>The revision trigger, from this assembly's constants only.</summary>
    internal static string RevisionTriggerSql() => $"""
        CREATE TRIGGER IF NOT EXISTS {RevisionTrigger} BEFORE INSERT ON ledger_revision
        WHEN {BlankSql("NEW.attempt")}
        BEGIN SELECT RAISE(ABORT, 'lineage: a ledger revision names the attempt that wrote it'); END;
        """;

    /// <summary>The trigger rung <see cref="Versions.ArtifactLineageRung"/> puts on <paramref name="k"/>'s table.</summary>
    public static string TriggerName(ArtifactKindSpec k) => $"lineage_{k.Table}";

    /// <summary>
    /// THE WRITER'S CHECK, before anything is written: <paramref name="author"/> named, and <paramref name="attempt"/> named unless the
    /// author is one of the kind's app principals. Refused in words a model can act on; the database's trigger is the backstop.
    /// </summary>
    /// <exception cref="TradeAgentException">The author or the attempt is missing; nothing was written.</exception>
    public static void Require(string kind, string? author, string? attempt)
    {
        var k = Spec(kind);
        if (string.IsNullOrWhiteSpace(author))
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"a {kind} records the position that authored it, and none was named. Nothing was written.");
        if (string.IsNullOrWhiteSpace(attempt) && !k.AppAuthors.Contains(author, StringComparer.Ordinal))
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                $"a {kind} records the attempt whose reads produced it, and this one came with none — a call made under a "
                + "launch carries its attempt from the launch grant TradeAgent issued. Nothing was written.");
    }

    /// <summary>
    /// THE TRIGGER FOR ONE KIND, composed from this assembly's own constants only. Refuses an insert whose author is missing, whose
    /// author is neither a position nor one of the kind's app principals, or whose attempt is missing while a position wrote it.
    /// </summary>
    internal static string TriggerSql(ArtifactKindSpec k)
    {
        var a = $"NEW.{k.AuthorColumn}";
        var x = $"NEW.{k.AttemptColumn}";
        var apps = k.AppAuthors.Count == 0 ? "" : string.Join(",", k.AppAuthors.Select(p => $"'{p.Replace("'", "''", StringComparison.Ordinal)}'"));
        var notApp = apps.Length == 0 ? "1" : $"{a} NOT IN ({apps})";
        return $"""
            CREATE TRIGGER IF NOT EXISTS {TriggerName(k)} BEFORE INSERT ON {k.Table}
            WHEN {BlankSql(a)}
              OR ({notApp} AND NOT EXISTS (SELECT 1 FROM org_position WHERE id = {a}))
              OR ({notApp} AND {BlankSql(x)})
            BEGIN SELECT RAISE(ABORT, 'lineage: a {k.Kind} names the position that authored it and the attempt that made it'); END;
            """;
    }

    /// <summary>The sentence a row written before the rung carries.</summary>
    public static string NotRecordedLabel(string kind) =>
        $"this {kind}'s author or attempt was not recorded: it was written before schema rung {Versions.ArtifactLineageRung} required "
        + "both, and nothing was filled in afterwards — unknown, never inferred.";

    /// <summary>
    /// ONE ARTIFACT'S LINEAGE, or null when no row of <paramref name="kind"/> has <paramref name="id"/>. Reads only: the author and
    /// attempt as the row recorded them, and the attempt's packet inputs and app-observed calls through that one reference.
    /// </summary>
    public static ArtifactLineageRow? Of(Database db, string kind, string id)
    {
        var k = Spec(kind);
        return db.Read(_ =>
        {
            string? author, attempt;
            using (var c = db.Cmd($"SELECT {k.AuthorColumn}, {k.AttemptColumn} FROM {k.Table} WHERE {k.IdColumn} = $id", ("$id", id)))
            using (var r = c.ExecuteReader())
            {
                if (!r.Read()) return null;
                author = r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                attempt = r.IsDBNull(1) ? null : r.GetString(1);
            }

            var noAttempt = Blank(attempt);
            if (author is not null && noAttempt && k.AppAuthors.Contains(author, StringComparer.Ordinal))
                return new ArtifactLineageRow(kind, id, author, null, LineageState.AppMeasured,
                    $"this {kind} is the app's own measurement ({author}): no attempt made it.", [], []);
            if (Blank(author) || noAttempt)
                return new ArtifactLineageRow(kind, id, author, attempt, LineageState.NotRecorded, NotRecordedLabel(kind), [], []);

            string? state;
            using (var c = db.Cmd("SELECT state FROM ai_attempt WHERE id = $a", ("$a", attempt)))
                state = c.ExecuteScalar() as string;
            if (state is null)
                    return new ArtifactLineageRow(kind, id, author, attempt, LineageState.Dangling,
                        $"this {kind} names attempt {attempt}, which the launch ledger does not hold: what it read cannot be shown.", [], []);

            var given = new List<AttemptGiven>();
            using (var c = db.Cmd("SELECT kind, ref_id FROM ai_attempt_input WHERE attempt_id = $a ORDER BY rowid", ("$a", attempt)))
            using (var r = c.ExecuteReader())
                while (r.Read()) given.Add(new AttemptGiven(r.GetString(0), r.GetString(1)));

            var read = new List<AttemptRead>();
            using (var c = db.Cmd("SELECT tool, argument, bytes, served FROM tool_call WHERE attempt = $a ORDER BY id", ("$a", attempt)))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    read.Add(new AttemptRead(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3) != 0));

            // THE PACKET RECORD IS WRITTEN ONLY WITH THE TURN'S COMMITTED OUTCOME (MissionLoop.Commit), so an empty one is not a zero.
            var packet = given.Count > 0
                ? $"{given.Count} record(s) its packet gave it"
                : state == nameof(AiAttemptState.LAUNCHED)
                    ? "its turn has not committed, and what its packet gave it is recorded only with the committed outcome"
                    : "no packet record is held: its packet showed nothing, or its turn ended before its commit; unknown which, never read as nothing shown";
            return new ArtifactLineageRow(kind, id, author, attempt, LineageState.Recorded,
                $"this {kind} was authored by {author} in attempt {attempt}: {packet}; "
                + $"{read.Count} call(s) it made through TradeAgent; reads in its own folder outside TradeAgent are not observed.",
                given, read, PacketRecorded: given.Count > 0);
        });
    }
}
