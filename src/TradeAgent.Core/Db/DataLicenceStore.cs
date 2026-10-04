using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;

namespace TradeAgent.Core.Db;

/// <summary>
/// THE READINGS OF EACH SOURCE'S TERMS — <c>data_licence</c>, for reading.
///
/// <para><b>Reads and nothing else.</b> Not "no pipe op" — no write method at all: a reading is recorded by a
/// schema rung (rung 30 seeds the first two), so reclassifying a source is a new row in a later rung and
/// never gate code, never an op and never a verb. A class its subject could write would be the evidence
/// deciding its own eligibility, which is the split <see cref="MaterialStore"/> makes between what TradeAgent
/// measured and what somebody claimed.</para>
///
/// <para><b>Append-only, newest in force.</b> <see cref="Newest"/> is the latest row appended for a source;
/// the ones before it stay on the table, where a reader can see what was read when.</para>
/// </summary>
public sealed class DataLicences(Database db)
{
    const string Cols = "id, source, licence_class, terms_url, terms_version, terms_read_on, note, recorded_at";

    /// <summary>The reading in force for <paramref name="source"/>, or null because none has been recorded.</summary>
    public DataLicenceReading? Newest(string source) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM data_licence WHERE source=$s ORDER BY id DESC LIMIT 1",
            ("$s", source));
        return Read(c).FirstOrDefault();
    });

    /// <summary>Every reading this installation holds, oldest first.</summary>
    public IReadOnlyList<DataLicenceReading> All() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM data_licence ORDER BY id");
        return (IReadOnlyList<DataLicenceReading>)Read(c);
    });

    static List<DataLicenceReading> Read(SqliteCommand c)
    {
        var rows = new List<DataLicenceReading>();
        using var r = c.ExecuteReader();
        while (r.Read())
            rows.Add(new DataLicenceReading(
                r.GetInt64(0), r.GetString(1), r.GetString(2), Sql.S(r.GetValue(3)), Sql.S(r.GetValue(4)),
                Sql.S(r.GetValue(5)), Sql.S(r.GetValue(6)), Sql.Time(r.GetValue(7))));
        return rows;
    }
}
