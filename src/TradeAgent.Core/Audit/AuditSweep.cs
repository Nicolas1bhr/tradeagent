using System.Globalization;
using System.Text.Json;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Audit;

/// <summary>What one sweep did: the samples it drew new, and the failure it recorded instead, if any.</summary>
public sealed record AuditSweepResult(IReadOnlyList<AuditSample> Drawn, string? Failure);

/// <summary>
/// ONE SWEEP OF THE PUBLIC AUDIT LINE (<c>U-org-audit-a</c>): reads the app's <c>org_mandate_event</c> rows of kind <c>accepted</c> past
/// its cursor, through <see cref="AuditEvidence"/> in one read; draws each accepted delivery or not (<see cref="AuditSampler"/>); runs the
/// code checks on each sample (<see cref="AuditChecks"/>); and appends what it drew, with its checks, findings and flags and the cursor, to
/// <c>audit.db</c> in one write. A rejected or unjudged delivery has no <c>accepted</c> event and is never
/// drawn.
///
/// <para><b>App code, never agent-runtime code:</b> run from the app's own timer (<see cref="AuditLine"/>), never from the mission loop; it
/// raises no wake, writes nothing to the app's database or its logs, and spends nothing (no launch-ledger row — BUDGET-001). It can
/// neither instruct nor block. Idempotent: a sample is keyed by its delivery and the cursor moves forward only, so a restart re-sweeps to
/// the same rows.</para>
/// </summary>
public sealed class AuditSweep(string databaseFile, AuditStore store, double rate = AuditPolicy.SampleRate)
{
    /// <summary>The app's database this sweep reads.</summary>
    public string DatabaseFile { get; } = Path.GetFullPath(databaseFile);

    /// <summary>
    /// Runs one sweep at <paramref name="now"/>. Never throws for a failure of its own: that is written to the audit file's own failure
    /// table and returned, and nothing anywhere else.
    /// </summary>
    public AuditSweepResult Run(DateTimeOffset now)
    {
        try
        {
            var salt = store.Salt();
            var after = store.Cursor() ?? 0;
            using var evidence = new AuditEvidence(DatabaseFile);
            var write = evidence.InOneRead(() => Read(evidence, salt, after, now));
            return new AuditSweepResult(store.Commit(write, now), null);
        }
        catch (Exception ex)
        {
            var what = $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}";
            try { store.RecordFailure(now, "a sweep failed: " + what); }
            catch (Exception) { /* the file itself cannot be written; nothing else is told */ }
            return new AuditSweepResult([], what);
        }
    }

    /// <summary>Everything one sweep writes, read in one read transaction of the app's database.</summary>
    AuditSweepWrite Read(AuditEvidence evidence, byte[] salt, long after, DateTimeOffset now)
    {
        var top = evidence.Rows("SELECT MAX(id) FROM org_mandate_event")[0][0] is long t ? t : (long?)null;
        if (top is not { } last || last <= after) return new AuditSweepWrite([], [], [], [], null);

        var samples = new List<AuditSample>();
        var checks = new List<AuditCheck>();
        var findings = new List<AuditFinding>();
        var flags = new List<AuditFlag>();
        var unreadable = new List<string>();
        var named = new Dictionary<string, long>(StringComparer.Ordinal);
        var accepted = evidence.Rows("""
            SELECT e.id, e.mandate_id, e.evidence, m.unit_id, m.id
              FROM org_mandate_event e LEFT JOIN org_mandate m ON m.id = e.mandate_id
             WHERE e.kind = $kind AND e.id > $after AND e.id <= $top
             ORDER BY e.id
            """, ("$kind", MandateEventKind.Accepted), ("$after", after), ("$top", last));
        foreach (var row in accepted)
        {
            // THE APP WROTE THE EVIDENCE OF AN ACCEPTANCE: the accepted delivery's id, first (OrgStore.Accept). An event that names none
            // TradeAgent can read is the line's own failure, written with this sweep in a closed word — never a reason to stop auditing the
            // rest, so the cursor moves past it (fix pass 1: no value of any type stops the sweep).
            var eventId = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
            string? delivery;
            try { delivery = row[2] is string evidenceText ? (JsonSerializer.Deserialize<string[]>(evidenceText) ?? []).FirstOrDefault() : null; }
            catch (Exception ex) when (ex is not Microsoft.Data.Sqlite.SqliteException) { delivery = null; }
            if (string.IsNullOrEmpty(delivery))
            {
                unreadable.Add($"accepted event {eventId} names no delivery TradeAgent can read; it was not drawn");
                continue;
            }
            // AN ACCEPTANCE OF NO MANDATE (fix pass 2): the outer join keeps it, so it is the line's own failure — never passed in silence.
            if (row[4] is null)
            {
                unreadable.Add($"accepted event {eventId} names no mandate in the app's database; it was not drawn");
                continue;
            }
            if (row[1] is not string mandate || row[3] is not string unit)
            {
                unreadable.Add($"accepted event {eventId} names a mandate or unit TradeAgent cannot read; it was not drawn");
                continue;
            }
            // ONE DELIVERY, ONE ACCEPTANCE (fix pass 2): a delivery is accepted once (its acceptance closes the mandate), so a later event
            // naming one an earlier event of this sweep already named is a rewritten record — the line's own failure, never a second sample.
            if (named.TryGetValue(delivery, out var earlier))
            {
                unreadable.Add($"accepted event {eventId} names a delivery that accepted event {earlier} already named; it was not drawn again");
                continue;
            }
            named[delivery] = eventId;
            if (!AuditSampler.Draws(salt, delivery, rate)) continue;
            var sample = new AuditSample(delivery, mandate, unit, eventId, now);
            samples.Add(sample);
            var outcome = AuditChecks.Of(evidence, sample, now);
            checks.AddRange(outcome.Checks);
            findings.AddRange(outcome.Findings);
            if (outcome.Flag is { } flag) flags.Add(flag);
        }
        return new AuditSweepWrite(samples, checks, findings, flags, last, unreadable);
    }
}
