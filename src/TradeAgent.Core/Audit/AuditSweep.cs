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
/// <para><b>App code, never agent-runtime code:</b> run from the app's own timer (<c>AuditLine</c>), never from the mission loop; it
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
        var accepted = evidence.Rows("""
            SELECT e.id, e.mandate_id, e.evidence, m.unit_id
              FROM org_mandate_event e JOIN org_mandate m ON m.id = e.mandate_id
             WHERE e.kind = $kind AND e.id > $after AND e.id <= $top
             ORDER BY e.id
            """, ("$kind", MandateEventKind.Accepted), ("$after", after), ("$top", last));
        foreach (var row in accepted)
        {
            // THE APP WROTE THE EVIDENCE OF AN ACCEPTANCE: the accepted delivery's id, first (OrgStore.Accept).
            var ids = JsonSerializer.Deserialize<string[]>((string)row[2]!) ?? [];
            if (ids.Length == 0) continue;
            var delivery = ids[0];
            if (!AuditSampler.Draws(salt, delivery, rate)) continue;
            var sample = new AuditSample(delivery, (string)row[1]!, (string)row[3]!, Convert.ToInt64(row[0], CultureInfo.InvariantCulture), now);
            samples.Add(sample);
            var outcome = AuditChecks.Of(evidence, sample, now);
            checks.AddRange(outcome.Checks);
            findings.AddRange(outcome.Findings);
            if (outcome.Flag is { } flag) flags.Add(flag);
        }
        return new AuditSweepWrite(samples, checks, findings, flags, last);
    }
}
