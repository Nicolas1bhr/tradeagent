using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Audit;

/// <summary>The code checks the line runs on every sample, by name.</summary>
public static class AuditCheckName
{
    /// <summary>The SHA-256 of the stored manifest is the delivery's id, and the manifest's parts are the row's columns.</summary>
    public const string Seal = "seal";

    /// <summary>Every record the delivery names is in the app's ledgers NOW, re-resolved through the register.</summary>
    public const string RefsExistNow = "refs-exist-now";

    /// <summary>Every record the delivery names was produced by a launch charged to its mandate, re-resolved NOW.</summary>
    public const string RefsOfThisMandateNow = "refs-of-this-mandate-now";

    /// <summary>No app check of the accepted delivery failed, unless the delivery named a deviation.</summary>
    public const string AcceptedClean = "accepted-clean";

    public static readonly string[] All = [Seal, RefsExistNow, RefsOfThisMandateNow, AcceptedClean];
}

/// <summary>
/// THE CODE CHECKS ON ONE SAMPLE (<c>U-org-audit-a</c>), each an <see cref="AuditCheck"/> whose reason is the app's own sentence — counts and
/// closed words, never a report's text, a ref's id or anything else an agent wrote. A failed check is an <see cref="AuditFinding"/> citing the
/// delivery, the mandate and the attempt by id (<c>severe</c> for the seal, <c>info</c> otherwise). A sample that named deviations and passed
/// every check is an <see cref="AuditFlag"/>: a model reader of the deviations would be warranted, and the audit agent is NOT RUN — dormant.
///
/// <para><b>Independent of the code that judged it.</b> The refs are re-resolved through <see cref="AuditEvidence"/> at audit time, apart
/// from <c>OrgStore</c>'s own checks at the seal; and <c>accepted-clean</c> deliberately overlaps the watcher's <c>accepted-failed-check</c>
/// detector — two lines measuring the same fact from their own code is the point (VISION § 5.3: the audit line and the watcher are
/// independent; neither rests on the other).</para>
///
/// <para><b>The seal</b> is what catches a delivery rewritten past its triggers — which an unconfined shell can drop (ORCHESTRATION § 2.3):
/// the manifest is re-hashed and compared with the id, and its parts with the row's columns, so neither a rewrite of the columns alone nor of
/// the columns and the manifest together goes unseen.</para>
/// </summary>
public static class AuditChecks
{
    /// <summary>The words of a flag: the reader a sample warrants, and that it did not run.</summary>
    public const string DormantFlag =
        "a reader of the deviations is warranted; audit agent NOT RUN — dormant (no proven cell; activation is the owner's)";

    /// <summary>What one sample comes to: its checks, a finding per failed check, and a flag or none.</summary>
    public sealed record Outcome(IReadOnlyList<AuditCheck> Checks, IReadOnlyList<AuditFinding> Findings, AuditFlag? Flag);

    /// <summary>Runs every check on <paramref name="sample"/>, reading through <paramref name="evidence"/> only.</summary>
    internal static Outcome Of(AuditEvidence evidence, AuditSample sample, DateTimeOffset now)
    {
        var d = sample.DeliveryId;
        var rows = evidence.Rows("SELECT mandate_id, attempt_id, report, deviations, refs, manifest, at FROM org_delivery WHERE id = $id", ("$id", d));
        if (rows.Count == 0)
        {
            const string gone = "no sealed delivery is in the app's database under the id its acceptance names";
            var none = AuditCheckName.All.Select(n => new AuditCheck(d, n, false, gone)).ToList();
            return new Outcome(none, [.. none.Select(k => Finding(sample, null, k.Name, now))], null);
        }
        var row = rows[0];
        var (mandate, attempt, report, deviations, refs, manifest, at) =
            ((string)row[0]!, (string?)row[1], (string)row[2]!, (string)row[3]!, (string)row[4]!, (string)row[5]!, (string)row[6]!);

        var named = JsonSerializer.Deserialize<MandateRef[]>(refs) ?? [];
        var deviationCount = JsonNode.Parse(deviations) is JsonArray a ? a.Count : 0;
        var checks = new List<AuditCheck>
        {
            Seal(d, sample.MandateId, mandate, attempt, report, deviations, refs, manifest, at),
            RefsExistNow(evidence, d, named),
            RefsOfThisMandateNow(evidence, d, sample.MandateId, named),
            AcceptedClean(evidence, d, deviationCount),
        };
        var findings = checks.Where(k => !k.Passed).Select(k => Finding(sample, attempt, k.Name, now)).ToList();
        var flag = deviationCount > 0 && findings.Count == 0 ? new AuditFlag(d, sample.MandateId, attempt, DormantFlag, now) : null;
        return new Outcome(checks, findings, flag);
    }

    static AuditFinding Finding(AuditSample s, string? attempt, string check, DateTimeOffset now) => new(
        AuditFinding.IdOf(s.DeliveryId, check), s.DeliveryId, s.MandateId, attempt, check,
        check == AuditCheckName.Seal ? AuditSeverity.Severe : AuditSeverity.Info, now);

    /// <summary>THE SEAL: re-hash the stored manifest and compare it with the id; compare each of its parts with the row's column.</summary>
    static AuditCheck Seal(string id, string acceptedOn, string mandate, string? attempt, string report, string deviations, string refs,
        string manifest, string at)
    {
        if (Sha256Hex.Of(manifest) != id)
            return new AuditCheck(id, AuditCheckName.Seal, false, "the SHA-256 of the stored manifest is not the delivery's id");
        JsonObject sealedParts;
        try
        {
            sealedParts = JsonNode.Parse(manifest) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return new AuditCheck(id, AuditCheckName.Seal, false, "the stored manifest is not a JSON object TradeAgent wrote");
        }
        var differ = new List<string>();
        if (mandate != acceptedOn) differ.Add("the mandate its acceptance was recorded on");
        if (Text(sealedParts["mandate"]) != mandate) differ.Add("mandate");
        if (Text(sealedParts["attempt"]) != attempt) differ.Add("attempt");
        if (Text(sealedParts["at"]) != at) differ.Add("time");
        if (Text(sealedParts["report"]) != report) differ.Add("report");
        if (!Same(sealedParts["deviations"], deviations)) differ.Add("deviations");
        if (!Same(sealedParts["refs"], refs)) differ.Add("refs");
        return differ.Count == 0
            ? new AuditCheck(id, AuditCheckName.Seal, true,
                "the SHA-256 of the stored manifest is the delivery's id, and its mandate, attempt, time, report, deviations and refs are the row's")
            : new AuditCheck(id, AuditCheckName.Seal, false, $"the row differs from its sealed manifest in: {string.Join(", ", differ)}");

        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        static bool Same(JsonNode? sealedPart, string column)
        {
            try { return JsonNode.DeepEquals(sealedPart, JsonNode.Parse(column)); }
            catch (JsonException) { return false; }
        }
    }

    /// <summary>The launches a ref's record came from, re-read now — or null when it is not in the ledgers. A lens is not resolvable here.</summary>
    static IReadOnlyList<string?>? Origin(AuditEvidence evidence, MandateRef r)
    {
        switch (r.Kind)
        {
            case MandateRefKind.Version:
                return evidence.Rows("SELECT attempt FROM strategy_version WHERE id = $id", ("$id", r.Id)) is [var v] ? [(string?)v[0]] : null;
            case MandateRefKind.Run:
                return evidence.Rows("""
                    SELECT r.attempt, v.attempt FROM strategy_run r JOIN strategy_version v ON v.id = r.version_id WHERE r.id = $id
                    """, ("$id", r.Id)) is [var x] ? [(string?)x[0], (string?)x[1]] : null;
            case MandateRefKind.Ledger:
                if (!long.TryParse(r.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var entry)) return null;
                return evidence.Rows("SELECT attempt FROM ledger_entry WHERE id = $id", ("$id", entry)) is [var l] ? [(string?)l[0]] : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The sentence a lens ref adds: the lens registry is the tape's file, which this line does not open (it opens the app's database and
    /// its own file, nothing else), so a lens is NOT re-resolved — said, never counted as passed or failed.
    /// </summary>
    static string LensNote(IReadOnlyList<MandateRef> refs)
    {
        var lenses = refs.Count(r => r.Kind == MandateRefKind.Lens);
        return lenses == 0 ? "" : $"; {lenses} lens record{(lenses == 1 ? " was" : "s were")} not re-resolved — the lens registry is not a file this line opens";
    }

    static AuditCheck RefsExistNow(AuditEvidence evidence, string id, IReadOnlyList<MandateRef> refs)
    {
        var checkable = refs.Where(r => r.Kind != MandateRefKind.Lens).ToList();
        var missing = checkable.Count(r => Origin(evidence, r) is null);
        return new AuditCheck(id, AuditCheckName.RefsExistNow, missing == 0, (missing == 0
            ? $"every one of the {checkable.Count} records named is in the app's ledgers now"
            : $"{missing} of the {checkable.Count} records named {(missing == 1 ? "is" : "are")} not in the app's ledgers now") + LensNote(refs));
    }

    static AuditCheck RefsOfThisMandateNow(AuditEvidence evidence, string id, string mandate, IReadOnlyList<MandateRef> refs)
    {
        var checkable = refs.Where(r => r.Kind != MandateRefKind.Lens).ToList();
        var foreign = checkable.Count(r => Origin(evidence, r) is not { } o || !o.Any(a => a is not null && MandateOf(evidence, a) == mandate));
        return new AuditCheck(id, AuditCheckName.RefsOfThisMandateNow, foreign == 0, (foreign == 0
            ? $"every one of the {checkable.Count} records named was produced by a launch charged to the mandate, as the ledgers read now"
            : $"{foreign} of the {checkable.Count} records named {(foreign == 1 ? "was" : "were")} not produced by a launch charged to the mandate, as the ledgers read now")
            + LensNote(refs));
    }

    static string? MandateOf(AuditEvidence evidence, string attempt) =>
        evidence.Rows("SELECT mandate_id FROM ai_attempt WHERE id = $id", ("$id", attempt)) is [var m] ? (string?)m[0] : null;

    static AuditCheck AcceptedClean(AuditEvidence evidence, string id, int deviations)
    {
        var results = evidence.Rows("SELECT passed FROM org_check WHERE delivery_id = $id", ("$id", id));
        var failed = results.Count(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture) == 0);
        var clean = failed == 0 || deviations > 0;
        return new AuditCheck(id, AuditCheckName.AcceptedClean, clean, failed == 0
            ? $"none of the app's {results.Count} checks on the delivery failed"
            : deviations > 0
                ? $"{failed} of the app's {results.Count} checks on the delivery failed, and it named {deviations} deviation{(deviations == 1 ? "" : "s")}"
                : $"{failed} of the app's {results.Count} checks on the delivery failed, and it was accepted naming no deviation");
    }
}
