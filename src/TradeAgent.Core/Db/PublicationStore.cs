using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>What a role hands upward or downward. Two kinds today; the app decides, never the role.</summary>
public static class PublicationKind
{
    /// <summary>The Research Director's report, delivered to the chair. At most 20 lines.</summary>
    public const string Report = "report";

    /// <summary>The chair's agenda, delivered down to Research. At most 40 lines.</summary>
    public const string Brief = "brief";

    /// <summary>
    /// THE OPERATIONS DIRECTOR'S NOTE ON THE OWNER'S DAILY REPORT, at most 20 lines
    /// (<c>docs/COUNCIL.md</c>, "The owner's report, every day, from the app").
    ///
    /// <para>It is the ONE paid thing anywhere near that report, and it exists only when an app
    /// predicate changed: "No material change: the app says so and pays nobody." The report itself is
    /// written by the app from what it measured and costs nothing; a note is a publication like any
    /// other, so it is recorded, attributed and size-limited by the same machinery rather than being
    /// pasted into the facts.</para>
    ///
    /// <para><b>Nothing in this build produces one.</b> The constant is here because the report page
    /// and <c>DailyReports.NoteFor</c> LOOK for one and must show its absence honestly — a report
    /// that silently showed yesterday's note, or the chair's agenda, as today's commentary would be
    /// exactly the merge of measurement and claim the ledger rules forbid.</para>
    /// </summary>
    public const string Note = "note";
}

/// <summary>
/// WHO MAY SEE A PUBLICATION, recorded on the row rather than inferred from where the file ended up.
///
/// <c>docs/COUNCIL.md</c> round 4: "Agents propose staged output; only the app validates, publishes
/// and commits recipient-scoped artifacts." A classification is not a permission by itself — the
/// grants are — but a publication whose intended audience was never written down is one nobody can
/// later show was correctly scoped.
/// </summary>
public static class PublicationClass
{
    /// <summary>Between council roles and the app. Not the owner's, and not any outside party's.</summary>
    public const string Council = "council";
}

/// <summary>How far a publication has got towards the recipient's <c>in/</c> folder.</summary>
public static class DeliveryState
{
    /// <summary>The transaction landed. The file may or may not be on disk yet.</summary>
    public const string Committed = "committed";

    /// <summary>The file is in the recipient's folder. Only ever written after the copy.</summary>
    public const string Delivered = "delivered";
}

/// <summary>
/// ONE IMMUTABLE ARTIFACT ONE ROLE HANDED OVER, and the app's own copy of its content.
///
/// <para><b><see cref="Id"/> is a hash of what is in it</b>, which is the whole of what makes the
/// relay safe to crash inside: publishing the same text twice is publishing the same row twice, and
/// the primary key refuses the second. An id minted from the attempt, the clock or a Guid would make
/// every restart a second publication, a second delivery and a second paid task — which is exactly
/// the mutant this unit's property test watches go red.</para>
///
/// <para><b>The content is stored here rather than left in the role's <c>out/</c> file.</b> That file
/// belongs to the agent and may be overwritten, moved or deleted between the commit and the copy; a
/// delivery that cannot be re-made from the app's own record is a delivery a crash can lose.</para>
/// </summary>
public sealed record Publication
{
    public required string Id { get; init; }

    /// <summary>The role that produced it.</summary>
    public required string Role { get; init; }

    /// <summary>The launch that produced it, where the app knew which. Provenance, never authority.</summary>
    public string? Attempt { get; init; }

    /// <summary>This role's nth publication. For a person reading the table, and for an ordering.</summary>
    public int Revision { get; init; }

    public required string Kind { get; init; }

    /// <summary>The roles this is for, comma separated. See <see cref="RecipientList"/>.</summary>
    public required string Recipients { get; init; }

    public string Classification { get; init; } = PublicationClass.Council;
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The file it was read from, relative to the publishing role's home.</summary>
    public string? Source { get; init; }

    public required string Content { get; init; }

    public IReadOnlyList<string> RecipientList =>
        Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// THE ID: SHA-256 over the role, the kind and the text, lower-case hex.
    ///
    /// The role and the kind are hashed alongside the content rather than the content alone, because
    /// two roles that happen to publish identical text are two artifacts and one primary key would
    /// silently drop the second. It is still content-addressed and still nothing to do with the
    /// attempt, the clock or a counter, which is the property that matters.
    /// </summary>
    public static string IdOf(string role, string kind, string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{role}\n{kind}\n{content}")))
            .ToLowerInvariant();
}

/// <summary>One recipient's copy of a publication, and whether the file has actually been written.</summary>
public sealed record Delivery(
    string PublicationId, string Recipient, string State, DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

/// <summary>
/// THE RELAY'S TABLES: what one role published, who it was for, and whether it arrived.
///
/// <para><b>Written by the app only.</b> There is no verb, no pipe op and no path from an agent to
/// either table — the rule <c>material</c>, <c>ai_attempt</c> and <c>mission_event</c> already keep.
/// A role that could insert here could hand itself work, publish in the other's name, or delete the
/// record of what it was asked to do.</para>
///
/// <para><b><see cref="Commit"/> is one transaction and that is the unit's property.</b> The
/// publication, its deliveries and the ONE task event that will cost the recipient a paid turn go in
/// together, so there is no window in which a task exists for an artifact that does not, or an
/// artifact exists that nobody will ever be told about. The copy onto disk comes AFTER, and is
/// re-made from <see cref="Publication.Content"/> for as long as the delivery is still
/// <see cref="DeliveryState.Committed"/>.</para>
/// </summary>
public sealed class PublicationStore(Database db)
{
    const string Cols =
        "id, role, attempt, revision, kind, recipients, classification, created_at, source, content";

    /// <summary>
    /// PUBLISHES ONE ARTIFACT, DELIVERS IT AND SCHEDULES THE RECIPIENT'S TURN, IN ONE
    /// <see cref="Database.Write"/>. Returns true when the publication row was new.
    ///
    /// Every insert is idempotent by key, so calling this twice with the same content — a restart
    /// re-reading a file it had already published, a turn that wrote the same report again — writes
    /// nothing the second time and costs nobody a turn. That is why the caller does not check first:
    /// a read-then-write would be a race, and the keys are the answer.
    /// </summary>
    public bool Commit(Publication p, DateTimeOffset at) => db.Write(_ =>
    {
        using var pub = db.Cmd($"""
            INSERT INTO publication({Cols})
            VALUES($id,$role,$attempt,$rev,$kind,$to,$class,$at,$src,$content)
            ON CONFLICT(id) DO NOTHING
            """,
            ("$id", p.Id), ("$role", p.Role), ("$attempt", p.Attempt), ("$rev", p.Revision),
            ("$kind", p.Kind), ("$to", p.Recipients), ("$class", p.Classification),
            ("$at", Sql.T(p.CreatedAt)), ("$src", p.Source), ("$content", p.Content));
        var fresh = pub.ExecuteNonQuery() == 1;

        foreach (var recipient in p.RecipientList)
        {
            using var d = db.Cmd("""
                INSERT INTO delivery(publication_id, recipient, state, created_at, delivered_at)
                VALUES($id,$to,$state,$at,NULL)
                ON CONFLICT(publication_id, recipient) DO NOTHING
                """,
                ("$id", p.Id), ("$to", recipient), ("$state", DeliveryState.Committed),
                ("$at", Sql.T(at)));
            d.ExecuteNonQuery();

            // THE ONE PAID CONSEQUENCE, KEYED BY THE PUBLICATION. A second relay pass over the same
            // file raises the same id and buys nothing; an id taken from the attempt would buy a
            // turn every time the app restarted.
            using var e = db.Cmd($"""
                INSERT INTO mission_event({MissionEventStore.EventCols})
                VALUES($id,$kind,$at,$at,$payload,NULL,NULL,NULL,$role,NULL)
                ON CONFLICT(id) DO NOTHING
                """,
                ("$id", MissionEventIds.ForRole(MissionEventIds.Task(p.Id), recipient)),
                ("$kind", p.Kind), ("$at", Sql.T(at)),
                ("$payload", Json.Write(new MissionTask(p.Id, p.Kind, p.Role))),
                ("$role", recipient));
            e.ExecuteNonQuery();
        }

        return fresh;
    });

    /// <summary>
    /// Marks one delivery arrived. ONLY from <see cref="DeliveryState.Committed"/>, and only ever
    /// called once the file is on disk: a delivery claimed before the copy is a delivery a crash
    /// turns into a task pointing at a file that is not there.
    /// </summary>
    public bool MarkDelivered(string publicationId, string recipient, DateTimeOffset at) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE delivery SET state=$state, delivered_at=$at
             WHERE publication_id=$id AND recipient=$to AND state=$from
            """,
            ("$state", DeliveryState.Delivered), ("$at", Sql.T(at)), ("$id", publicationId),
            ("$to", recipient), ("$from", DeliveryState.Committed));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>Every delivery that has been committed and not yet written to disk, oldest first.</summary>
    public List<Delivery> Undelivered() => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT publication_id, recipient, state, created_at, delivered_at FROM delivery "
            + "WHERE state=$state ORDER BY created_at, rowid", ("$state", DeliveryState.Committed));
        using var r = c.ExecuteReader();
        var list = new List<Delivery>();
        while (r.Read()) list.Add(ReadDelivery(r));
        return list;
    });

    /// <summary>Every delivery to one role, newest last. What the Situation reads.</summary>
    public List<Delivery> To(string recipient) => db.Read(_ =>
    {
        using var c = db.Cmd(
            "SELECT publication_id, recipient, state, created_at, delivered_at FROM delivery "
            + "WHERE recipient=$to ORDER BY created_at, rowid", ("$to", recipient));
        using var r = c.ExecuteReader();
        var list = new List<Delivery>();
        while (r.Read()) list.Add(ReadDelivery(r));
        return list;
    });

    public Publication? Get(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM publication WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>Everything one role published, oldest first.</summary>
    public List<Publication> By(string role) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM publication WHERE role=$role ORDER BY revision, rowid",
            ("$role", role));
        using var r = c.ExecuteReader();
        var list = new List<Publication>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    /// <summary>This role's next revision number. Taken from the rows, so nothing else has to survive.</summary>
    public int NextRevision(string role) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT COALESCE(MAX(revision), 0) FROM publication WHERE role=$role",
            ("$role", role));
        var o = c.ExecuteScalar();
        return (o is null or DBNull ? 0 : Convert.ToInt32(o)) + 1;
    });

    static Delivery ReadDelivery(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Sql.Time(r.GetValue(3)),
        Sql.TimeN(r.GetValue(4)));

    static Publication Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Role = r.GetString(1),
        Attempt = Sql.S(r.GetValue(2)),
        Revision = r.GetInt32(3),
        Kind = r.GetString(4),
        Recipients = r.GetString(5),
        Classification = r.GetString(6),
        CreatedAt = Sql.Time(r.GetValue(7)),
        Source = Sql.S(r.GetValue(8)),
        Content = r.GetString(9)
    };
}

/// <summary>
/// The payload of a <see cref="PublicationKind.Report"/> or <see cref="PublicationKind.Brief"/> wake:
/// which artifact arrived, what kind it is and who sent it. Small, like every other payload here —
/// the text itself is in <c>publication</c>, and the queue is not a store of documents.
/// </summary>
public sealed record MissionTask(string Publication, string Kind, string From);
