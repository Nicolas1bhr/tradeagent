using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE APP'S OWN WORDS CITE NO DOC SECTION (<c>U-owner-words-3</c> item 1). The owner-pages guard (<c>OwnerPagesRenderTests</c>) reads the
/// pages in the states its seed reaches; 33 lines of refusals of the board, the proving ground and the organisation's store still said
/// "(R24 § 4.1)" or "(ORGANISATION § 2)" and reach an owner page only in states no seed renders — the For-you list, a held seat's line, a
/// refused press. So the literals themselves are read: no line of code under <c>src/</c> outside a comment carries a section mark, a bare
/// "(R24)" or an invariant id cited in parentheses.
///
/// <para><b>Exempt, by file and for a reason each:</b> <c>InvariantRegistry.cs</c>, the protections register, which names where each
/// protection is written by design and is shown only inside the Oversight page's "For reviewers" fold (item 3); <c>DataLicence.cs</c> and
/// <c>TapeSourceCatalog.cs</c>, the records of a vendor's terms as they were read, whose "§ 4.1" is the vendor's own clause number in the
/// licence reading the evidence stands on — rewording it would change what the record says was read.</para>
/// </summary>
public class OwnerWordsLiteralTests
{
    static readonly string[] Exempt = ["InvariantRegistry.cs", "DataLicence.cs", "TapeSourceCatalog.cs"];

    /// <summary>
    /// A section mark, a bare "(R24)", an invariant id in parentheses — and, since <c>U-owner-words-4</c> item 1, a decision cited by its
    /// number: "(R25 D11)", "R30 D18" inside a sentence, "(D20)" or "(D11 amended: …". The founding event's recorded detail carried all
    /// four, and none of them is a doc-section shape the first reader looked for.
    /// </summary>
    internal static readonly Regex Citation = new(@"§|\((R\d{2}|ORDER-\d{3})\)|\bR\d{2} D\d{1,2}\b|\(D\d{1,2}(\)| amended)");

    /// <summary>Every line of code under <c>src/</c> that carries a citation outside a comment, as "file:line: text".</summary>
    internal static List<string> CitationsIn(string src)
    {
        var hits = new List<string>();
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (Exempt.Contains(Path.GetFileName(file)) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            var block = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (block) { if (t.Contains("*/")) block = false; continue; }
                if (t.StartsWith("/*", StringComparison.Ordinal)) { block = !t.Contains("*/"); continue; }
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*')) continue;
                // A trailing comment after the last quote of the line is a comment, not words.
                var code = t;
                var lastQuote = code.LastIndexOf('"');
                var comment = code.IndexOf("//", Math.Max(lastQuote, 0), StringComparison.Ordinal);
                if (comment >= 0 && (lastQuote < 0 || comment > lastQuote)) code = code[..comment];
                if (Citation.IsMatch(code)) hits.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {code}");
            }
        }
        return hits;
    }

    [Fact]
    public void No_string_the_app_says_cites_a_doc_section()
    {
        var hits = CitationsIn(Path.Combine(DayOnePrograms.RepoRoot(), "src"));
        Assert.True(hits.Count == 0, $"{hits.Count} lines of the app's own words cite a doc section:\n{string.Join("\n", hits.Take(12))}");
    }

    /// <summary>The reader itself: a citation in a string is found, one in a comment (whole-line, trailing or a block) is not.</summary>
    [Fact]
    public void The_reader_finds_a_citation_in_a_string_and_none_in_a_comment()
    {
        var dir = Directory.CreateTempSubdirectory("ow3-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.cs"), """
                // a comment citing ORCHESTRATION § 2.3
                /// <summary>R24 § 4.1</summary>
                /* a block
                   § 9 */
                var ok = "plain words"; // and a trailing (R24 § 4.1)
                var bad = "only an active seat acts (R24 § 4.1).";
                var bare = $"the board's now (R24): it is a board act";
                var id = "never shaped by a note (ORDER-001).";
                var ruled = "the envelopes ruled on his behalf (R25 D11), set here";
                var inline = "its allocation, each pair's quantity divided by its three runs (R30 D18 as ruled)";
                var bare2 = "an entry and its two protections (D20), and every other";
                var amended = "each its third (D11 amended: one inconclusive slot)";
                var plain = "a D-day and R2-D2 are no citation, nor is (Day 20)";
                x = ((D0 * q + D1) * q + 1);
                """);
            File.WriteAllText(Path.Combine(dir, "InvariantRegistry.cs"), "var x = \"VISION § 6.3\";");
            var hits = CitationsIn(dir);
            Assert.Equal(7, hits.Count);
            Assert.Contains(hits, h => h.StartsWith("A.cs:6:", StringComparison.Ordinal));
            Assert.Contains(hits, h => h.StartsWith("A.cs:7:", StringComparison.Ordinal));
            Assert.Contains(hits, h => h.StartsWith("A.cs:8:", StringComparison.Ordinal));
            foreach (var line in new[] { 9, 10, 11, 12 })
                Assert.Contains(hits, h => h.StartsWith($"A.cs:{line}:", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, true); }
    }
}
