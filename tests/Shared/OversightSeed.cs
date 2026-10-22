using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Tests;

/// <summary>
/// SEEDS THE RECORDS EACH OVERSIGHT DETECTOR MEASURES (<c>U-org-watcher-a</c>), into an app database a test made, on the test's own
/// connection with foreign keys off — so a refused promotion needs no campaign, dataset and holdout run behind it. Every NOT NULL column a
/// seed does not name is filled with a placeholder; what the detectors read is named.
/// </summary>
public static class OversightSeed
{
    /// <summary>The eight detectors of layer 1, by name.</summary>
    public static readonly string[] Detectors =
    [
        "spend-no-accept", "stall", "owner-words-late", "material-unwoken", "refusals", "claim-without-evidence",
        "accepted-failed-check", "dead-version-rerun",
    ];

    /// <summary>A sweep instant a minute after a database seeded now, whole-second, so the seeded chart is fresh.</summary>
    public static DateTimeOffset Now() =>
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);

    /// <summary>Seeds what <paramref name="detector"/> measures, in <paramref name="unit"/> (a division), relative to <paramref name="now"/>.</summary>
    /// <param name="fixedTag">The ids' suffix; null mints one. Two homes seeded with the same tag hold the same ids.</param>
    public static void Seed(string databaseFile, string detector, DateTimeOffset now, string unit = OrgIds.Research, string? fixedTag = null)
    {
        var role = unit == OrgIds.Operations ? CouncilRoles.Operations : CouncilRoles.Research;
        var tag = detector.Replace("-", "", StringComparison.Ordinal) + "-" + (fixedTag ?? Guid.NewGuid().ToString("n")[..8]);
        using var c = Open(databaseFile);
        switch (detector)
        {
            case "spend-no-accept":
                Insert(c, "ai_attempt", ("id", "att-" + tag), ("started_at", T(now.AddDays(-2))), ("state", "ENDED"), ("cost", "2.5"),
                    ("reserved_cost", "0"), ("role", role));
                Insert(c, "ai_attempt_funding", ("attempt_id", "att-" + tag), ("unit_id", unit), ("depth", 0));
                Insert(c, "ai_attempt_funding", ("attempt_id", "att-" + tag), ("unit_id", OrgIds.Root), ("depth", 1));
                break;
            case "stall":
                Insert(c, "org_unit", ("id", "team-" + tag), ("parent_id", unit), ("kind", "team"), ("status", "active"),
                    ("created_at", T(now.AddDays(-5))));
                break;
            case "owner-words-late":
                Insert(c, "mission_event", ("id", "evt-" + tag), ("kind", "owner"), ("role", role), ("created_at", T(now.AddHours(-30))),
                    ("due_at", T(now.AddHours(-30))));
                break;
            case "material-unwoken":
                Insert(c, "mission_event", ("id", "evt-" + tag), ("kind", "boundary"), ("role", role), ("created_at", T(now.AddDays(-2))),
                    ("due_at", T(now.AddDays(-2))));
                break;
            case "refusals":
                Insert(c, "tool_call", ("at", T(now.AddMinutes(-5))), ("role", role), ("tool", "read_file"), ("served", 0), ("refusal", "outside the home"));
                Insert(c, "engineering_log", ("at", T(now.AddMinutes(-4))), ("component", "Ipc"), ("event", "operator_session_refused"),
                    ("severity", "warn"), ("session", "operator"));
                break;
            case "claim-without-evidence":
                Accepted(c, tag, unit, refs: "[]", passed: true, now);
                // AND A LEDGER CLAIM WITH NO LINK AT ITS REVISION, past the grace.
                Insert(c, "ledger_entry", ("kind", "finding"), ("author", role), ("created_at", T(now.AddHours(-2))));
                using (var id = c.CreateCommand())
                {
                    id.CommandText = "SELECT last_insert_rowid()";
                    Insert(c, "ledger_revision", ("entry_id", (long)id.ExecuteScalar()!), ("revision", 1), ("at", T(now.AddHours(-2))),
                        ("text", "it works"), ("mark", "claim"), ("status", "open"));
                }
                break;
            case "accepted-failed-check":
                Accepted(c, tag, unit, refs: "[\"run:x\"]", passed: false, now);
                break;
            case "dead-version-rerun":
                Insert(c, "strategy_promotion", ("id", "prom-" + tag), ("version_id", "ver-" + tag), ("verdict", "refused"), ("reason", "failed"),
                    ("at", T(now.AddDays(-3))));
                Insert(c, "strategy_run", ("id", "run-" + tag), ("version_id", "ver-" + tag), ("created_at", T(now.AddDays(-1))), ("role", role));
                break;
            default:
                throw new ArgumentException("no seed for " + detector);
        }
    }

    static void Accepted(SqliteConnection c, string tag, string unit, string refs, bool passed, DateTimeOffset now)
    {
        var mandate = "man-" + tag;
        Insert(c, "org_mandate", ("id", mandate), ("unit_id", unit), ("decider", CouncilRoles.Research), ("holder", "pos-" + tag),
            ("kind", "task"), ("checks", "[]"), ("latitude", "narrow"), ("deadline", T(now.AddDays(1))), ("allocation_ai", "1"),
            ("retry_limit", 1), ("created_at", T(now.AddHours(-3))));
        Insert(c, "org_delivery", ("id", "dlv-" + tag), ("mandate_id", mandate), ("report", "done"), ("deviations", "[]"), ("refs", refs),
            ("manifest", "{}"), ("at", T(now.AddHours(-2))));
        Insert(c, "org_check", ("delivery_id", "dlv-" + tag), ("check_name", "shape"), ("passed", passed ? 1 : 0), ("reason", "measured"));
        Insert(c, "org_mandate_event", ("mandate_id", mandate), ("at", T(now.AddHours(-1))), ("kind", "accepted"), ("actor", CouncilRoles.Research),
            ("evidence", $"[\"dlv-{tag}\"]"));
    }

    /// <summary>The test's own connection to the app's database, foreign keys off.</summary>
    public static SqliteConnection Open(string databaseFile)
    {
        var c = new SqliteConnection($"Data Source={databaseFile};Pooling=False");
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=OFF;";
        p.ExecuteNonQuery();
        return c;
    }

    /// <summary>Inserts a row, filling every NOT NULL column without a default that the caller did not name.</summary>
    public static void Insert(SqliteConnection c, string table, params (string Column, object Value)[] values)
    {
        var given = values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal);
        using (var info = c.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var r = info.ExecuteReader();
            while (r.Read())
            {
                var (name, type, notNull, hasDefault, pk) = (r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, !r.IsDBNull(4), r.GetInt64(5) > 0);
                if (given.ContainsKey(name) || !notNull || hasDefault || (pk && type.Contains("INT", StringComparison.OrdinalIgnoreCase))) continue;
                given[name] = type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? 1 : type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ? 0.0 : "x";
            }
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}({string.Join(", ", given.Keys)}) VALUES({string.Join(", ", given.Keys.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)))})";
        var n = 0;
        foreach (var v in given.Values) cmd.Parameters.AddWithValue("$p" + (n++).ToString(CultureInfo.InvariantCulture), v);
        cmd.ExecuteNonQuery();
    }

    static string T(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
