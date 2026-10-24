using System.Text;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Oversight;

/// <summary>
/// THE DIGEST, COMPOSED BY CODE (ORGANISATION § 9: "on plans only, the watcher is layer 1 plus a digest composed by code"). A CLOSED LIST of
/// fact templates — one per detector, two where a detector measures two kinds of fact — and nothing else: no recommendation, because for its
/// first eight weeks the line reports facts and recommends nothing (§ 9). One sentence per new finding, ending in its citations: the finding's
/// own id and every record it cites. Written only when a finding newer than the scope's last digest exists — no digest on a quiet window.
/// </summary>
public static partial class DigestComposer
{
    /// <summary>The closed template list, keyed by detector (and variant). Placeholders are the finding's measured keys and <c>{unit}</c>.</summary>
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["spend-no-accept"] = "Unit {unit} spent {spent} on AI in {attempts} attempts between {from} and {to}, and nothing in its subtree was accepted in that time.",
        ["stall"] = "Unit {unit} has had no accepted delivery since {since} ({days} days; last accepted: {last_accepted}).",
        ["owner-words-late"] = "The owner's words ({kind}) raised at {raised} for unit {unit} took {hours} hours to disposition (dispositioned: {dispositioned}).",
        ["material-unwoken"] = "A {kind} wake ({class}) for {role} in unit {unit}, due at {due}, has gone {hours} hours unconsumed.",
        ["refusals/tool"] = "The app refused {refused} tool calls of {role} in unit {unit}.",
        ["refusals/pipe"] = "The pipe refused a request ({event}) at {at}.",
        ["refusals/op"] = "The pipe refused {op} ({code}) of {role} at {at}.",
        ["claim-without-evidence/delivery"] = "A delivery in unit {unit} was accepted at {accepted} by {by} naming no evidence.",
        ["claim-without-evidence/ledger"] = "Ledger claim revision {revision} by {author} at {at} in unit {unit} has no ledger link.",
        ["accepted-failed-check"] = "A delivery in unit {unit} was accepted at {accepted} by {by} although the app's checks {failed} failed on it.",
        ["dead-version-rerun"] = "Version {version}, refused at {refused}, was run again at {ran} by {role} in unit {unit}.",
    };

    /// <summary>The template a finding is told by.</summary>
    public static string TemplateKey(OversightFinding f) => f.Detector switch
    {
        "refusals" => f.Measured.ContainsKey("op") ? "refusals/op" : f.Measured.ContainsKey("event") ? "refusals/pipe" : "refusals/tool",
        "claim-without-evidence" => f.Measured.ContainsKey("revision") ? "claim-without-evidence/ledger" : "claim-without-evidence/delivery",
        _ => f.Detector,
    };

    /// <summary>
    /// One finding's sentence: its template filled, then its citations — the finding's own id and every cited record
    /// <paramref name="stillThere"/> still finds in the app's database.
    ///
    /// <para><b>A CITED RECORD THE APP HAS SINCE PRUNED</b> (its engineering log is rotated — <c>LogStore.Rotate</c>) is never dropped in silence and never
    /// bracketed as a citation it can no longer be: the sentence says, in words of this closed list, which cited record is no longer in the app's
    /// database, and still ends in the finding's own id, which resolves in the oversight file and keeps what was measured. So a pruned source
    /// neither blocks the scope's digests nor lets a sentence through uncited (fix pass 1, R1).</para>
    /// </summary>
    public static string Sentence(OversightFinding f, Func<OversightCite, bool> stillThere)
    {
        ArgumentNullException.ThrowIfNull(stillThere);
        var text = Filled(f);
        var gone = f.Cites.Where(c => !stillThere(c)).ToList();
        if (gone.Count > 0)
            text += $" {string.Join(", ", gone)} {(gone.Count == 1 ? "is" : "are")} no longer in the app's database; the finding keeps what was measured.";
        var cites = new StringBuilder($" [{f.Id}]");
        foreach (var c in f.Cites.Except(gone)) cites.Append(" [").Append(c).Append(']');
        return text + cites;
    }

    /// <summary>
    /// One finding's sentence when its cited records COULD NOT BE CHECKED — the app's database is missing or would not be read
    /// (<c>U-watcher-b-fix</c>, EVID-001): its template filled, then, in words of this closed list, which cited records are NOT VERIFIED, and
    /// only the finding's own id bracketed, which resolves in the oversight file the finding was just read from. A cite is bracketed only
    /// when a check RAN and found it; a check that did not run neither brackets it nor says it is gone.
    /// </summary>
    public static string Unchecked(OversightFinding f)
    {
        var text = Filled(f);
        if (f.Cites.Count > 0)
            text += $" {string.Join(", ", f.Cites)} {(f.Cites.Count == 1 ? "is" : "are")} NOT VERIFIED: the app's database could not be read "
                    + $"to check {(f.Cites.Count == 1 ? "it" : "them")}.";
        return text + $" [{f.Id}]";
    }

    static string Filled(OversightFinding f)
    {
        if (!Templates.TryGetValue(TemplateKey(f), out var template))
            throw new InvalidOperationException($"no digest template tells a '{f.Detector}' finding");
        return Placeholder().Replace(template, m => m.Groups[1].Value == "unit" ? f.UnitId
            : f.Measured.TryGetValue(m.Groups[1].Value, out var v) ? Plain(v) : "?");
    }

    /// <summary>
    /// The scope's digest over <paramref name="fresh"/> — the findings newer than its last digest — or null when there are none.
    /// <paramref name="stillThere"/> says whether a cited record is still in the app's database (see <see cref="Sentence"/>).
    /// </summary>
    public static OversightDigest? Compose(string scope, IReadOnlyList<OversightFinding> fresh, DateTimeOffset at, Func<OversightCite, bool> stillThere)
    {
        if (fresh.Count == 0) return null;
        var text = string.Join('\n', fresh.Select(f => Sentence(f, stillThere)));
        var through = fresh.Max(f => f.Seq);
        return new OversightDigest("wd-" + Sha256Hex.Of(scope + "\n" + through.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            scope, at, text, [.. fresh.Select(f => f.Id)], through);
    }

    // A measured value never carries a bracket or a line break into a sentence, so a value cannot pose as a citation or a second sentence.
    static string Plain(string v) => v.Replace('[', '(').Replace(']', ')').ReplaceLineEndings(" ");

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// THE CHECK EVERY DIGEST PASSES BEFORE IT IS WRITTEN (ORGANISATION § 9: "every sentence of a digest cites a detector output or a measurement
/// id, checked by code"). One sentence per line; each must END in its citations; each citation must be a finding id (<c>wf-…</c>) or a
/// <c>kind:id</c> of the closed list, and must RESOLVE — in the oversight file or the app's database. Anything else refuses the whole digest.
/// </summary>
public static partial class DigestCheck
{
    /// <summary>
    /// Null when every sentence of <paramref name="text"/> cites and every citation resolves through <paramref name="resolves"/> (a finding id
    /// is passed as kind <c>wf</c>); otherwise the refusal, naming the sentence.
    /// </summary>
    public static string? Verify(string text, Func<OversightCite, bool> resolves)
    {
        ArgumentNullException.ThrowIfNull(resolves);
        var lines = text.Split('\n');
        if (lines.All(string.IsNullOrWhiteSpace)) return "the digest has no sentence";
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) return "the digest has an empty line";
            var trailing = Trailing().Match(line);
            if (!trailing.Success) return $"a sentence does not end in a citation: '{line}'";
            var body = line[..trailing.Index];
            if (Bracket().IsMatch(body)) return $"a sentence carries a bracket before its citations: '{line}'";
            foreach (Match m in Bracket().Matches(trailing.Value))
            {
                var token = m.Groups[1].Value;
                OversightCite cite;
                if (token.StartsWith("wf-", StringComparison.Ordinal)) cite = new OversightCite("wf", token);
                else if (token.Split(':', 2) is [var kind, var id] && Array.IndexOf(OversightCite.Kinds, kind) >= 0 && id.Length > 0) cite = new OversightCite(kind, id);
                else return $"a citation '{token}' is neither a finding nor a record of the closed list: '{line}'";
                if (!resolves(cite)) return $"the citation '{token}' resolves in neither the oversight file nor the app's database: '{line}'";
            }
        }
        return null;
    }

    [GeneratedRegex(@"(\s*\[[^\[\]\n]+\])+\s*$")]
    private static partial Regex Trailing();

    [GeneratedRegex(@"\[([^\[\]\n]+)\]")]
    private static partial Regex Bracket();
}
