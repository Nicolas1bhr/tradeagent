using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE INVARIANT REGISTRY CANNOT ROT (<c>U-invariant-registry</c>; VISION § 6.3; PRINCIPLES "Claim only the properties actually
/// demonstrated"): every test it names is a real [Fact]/[Theory] in the tree under its class, every component it names is in the code,
/// every id VISION § 6.3 names is in it, a class no named test backs is SHOWN as advisory, and the owner's Oversight page shows it read-only.
/// </summary>
public class InvariantRegistryTests
{
    static readonly Lazy<HashSet<string>> TreeTests = new(() => TestMethods(Path.Combine(DayOnePrograms.RepoRoot(), "tests")));

    /// <summary>EVERY TEST THE REGISTRY NAMES EXISTS — removing or renaming one fails here, so the registry cannot keep claiming it.</summary>
    [Fact]
    public void Every_test_the_registry_names_is_a_test_in_the_tree_under_that_class()
    {
        var named = InvariantRegistry.All.SelectMany(i => i.Tests.Concat(i.Seats.SelectMany(s => s.Tests))).Distinct().ToList();
        Assert.NotEmpty(named);
        var missing = named.Where(n => !TreeTests.Value.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "the registry names tests that do not exist: " + string.Join(", ", missing));
    }

    /// <summary>
    /// THE CHECK ABOVE IS NOT VACUOUS: the scan finds tests across the three test projects, in partial classes, a nested helper class
    /// beside them and a Fact subclass — and refuses a made-up name and a real method under the wrong class.
    /// </summary>
    [Fact]
    public void The_tree_scan_finds_real_tests_and_refuses_a_made_up_name_or_the_wrong_class()
    {
        var tests = TreeTests.Value;
        Assert.True(tests.Count > 1000, $"only {tests.Count} tests found");
        Assert.Contains("InvariantRegistryTests.The_tree_scan_finds_real_tests_and_refuses_a_made_up_name_or_the_wrong_class", tests);
        Assert.Contains("DisconnectTests.A_backend_that_cannot_prove_its_own_history_never_auto_resolves", tests); // FaultTests, 2nd class in its file
        Assert.Contains("BrokerDoorTests.An_operations_grant_issued_beside_a_neighbour_may_not_buy_through_the_door", tests); // IntegrationTests
        Assert.Contains("CellPolicyTests.A_policy_that_grants_the_app_home_or_its_state_is_refused", tests); // [UnixFact]
        Assert.DoesNotContain("InvariantRegistryTests.Not_a_test_anywhere", tests);
        Assert.DoesNotContain("FaultTests.A_backend_that_cannot_prove_its_own_history_never_auto_resolves", tests); // the file's name, not its class

        var dir = Sample();
        try
        {
            Assert.Equal(["Nested.Inner_helper_is_its_own_class", "Outer.AfterNested", "Outer.Last", "Outer.Real", "Outer.Theory_one"],
                TestMethods(dir).Order(StringComparer.Ordinal).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>EVERY COMPONENT THE REGISTRY NAMES IS IN THE CODE: the file exists and holds each part of the symbol as a word outside comments.</summary>
    [Fact]
    public void Every_component_the_registry_names_exists_in_the_code()
    {
        var root = DayOnePrograms.RepoRoot();
        var bad = new List<string>();
        foreach (var e in InvariantRegistry.All.SelectMany(i => i.EnforcedBy))
        {
            var colon = e.IndexOf(':');
            Assert.True(colon > 0, $"'{e}' is not path:Symbol");
            var file = Path.Combine(root, e[..colon]);
            if (!File.Exists(file)) { bad.Add($"{e} (no file)"); continue; }
            var code = Strip(File.ReadAllText(file), keepStrings: true);
            foreach (var part in e[(colon + 1)..].Split('.'))
                if (!Regex.IsMatch(code, $@"\b{Regex.Escape(part)}\b")) bad.Add($"{e} (no '{part}')");
        }
        Assert.True(bad.Count == 0, string.Join(", ", bad));
    }

    /// <summary>EVERY ID VISION § 6.3 NAMES IS IN THE REGISTRY (ORDER-002…005 read as four), once, with one claim per seat type.</summary>
    [Fact]
    public void Every_id_VISION_names_is_in_the_registry_once_with_a_claim_per_seat_type()
    {
        var vision = File.ReadAllText(Path.Combine(DayOnePrograms.RepoRoot(), "docs", "VISION.md"));
        var section = vision[vision.IndexOf("### 6.3 ", StringComparison.Ordinal)..vision.IndexOf("### 6.4 ", StringComparison.Ordinal)];
        var ids = new List<string>();
        foreach (Match m in Regex.Matches(section, @"^\| ([A-Z]+)-(\d{3})(?:…(\d{3}))? \|", RegexOptions.Multiline))
        {
            var from = int.Parse(m.Groups[2].Value);
            var to = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : from;
            for (var n = from; n <= to; n++) ids.Add($"{m.Groups[1].Value}-{n:000}");
        }
        Assert.True(ids.Count >= 16, $"only {ids.Count} ids read from VISION § 6.3");

        var registry = InvariantRegistry.All.Select(i => i.Id).ToList();
        Assert.Equal(registry.Count, registry.Distinct().Count());
        Assert.Empty(ids.Except(registry));
        foreach (var inv in InvariantRegistry.All)
            Assert.Equal(InvariantRegistry.SeatTypes.Select(s => s.SeatClass), inv.Seats.Select(s => s.SeatClass));
    }

    /// <summary>
    /// THE HONESTY RULE: a class above advisory is shown only with a named test of the property, no part of it left unproven, AND a named
    /// test of the seat's side; any of these missing, it is shown advisory and says which. Advisory and a gap are shown as claimed.
    /// </summary>
    [Fact]
    public void A_class_no_named_test_backs_is_shown_advisory_and_says_why()
    {
        var hard = new SeatClaim(SeatClasses.Harness, EnforcementClass.HardApp, "b", ["T.Seat"]);
        var bare = new SeatClaim(SeatClasses.Harness, EnforcementClass.HardApp, "b", []);
        var proved = new Invariant("X-001", "s", "w", [], ["T.Prop"], [hard, bare]);
        var unproved = proved with { Tests = [] };

        Assert.Equal((EnforcementClass.HardApp, (string?)null), InvariantRegistry.Shown(proved, hard));
        var (c1, why1) = InvariantRegistry.Shown(proved, bare);
        Assert.Equal(EnforcementClass.Advisory, c1);
        Assert.Contains("no named test proves this seat's side", why1);
        var (c2, why2) = InvariantRegistry.Shown(unproved, hard);
        Assert.Equal(EnforcementClass.Advisory, c2);
        Assert.Contains("no named test proves the property", why2);
        Assert.Equal(EnforcementClass.Gap, InvariantRegistry.Shown(unproved, bare with { Claimed = EnforcementClass.Gap }).Class);
        var (c3, why3) = InvariantRegistry.Shown(proved with { Open = "one clause has no test" }, hard);
        Assert.Equal(EnforcementClass.Advisory, c3);
        Assert.Contains("part of it no named test proves", why3);

        // AND ON THE REAL REGISTRY: every cell shown above advisory carries tests on both sides.
        foreach (var inv in InvariantRegistry.All)
            foreach (var s in inv.Seats.Where(s => InvariantRegistry.Shown(inv, s).Class > EnforcementClass.Advisory))
                Assert.True(inv.Tests.Count > 0 && s.Tests.Count > 0 && inv.Open is null, $"{inv.Id}/{s.SeatClass}");
    }

    /// <summary>IT RECORDS AND ENFORCES NOTHING: no code but its own file and the owner's Oversight page reads the registry.</summary>
    [Fact]
    public void No_code_path_but_the_owners_oversight_page_reads_the_registry()
    {
        var src = Path.Combine(DayOnePrograms.RepoRoot(), "src");
        var readers = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(Strip(File.ReadAllText(f), keepStrings: false), @"\bInvariantRegistry\b"))
            .Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["InvariantRegistry.cs", "OversightView.cs", "ProtectionsView.cs"], readers);
    }

    /// <summary>
    /// THE OWNER SEES IT: his Oversight page carries the section, read-only (no press), naming every protection and every seat type; and a claim no
    /// test backs is drawn "advisory" with its reason, whatever it claimed.
    /// </summary>
    [Fact]
    public async Task The_oversight_page_shows_every_protection_read_only_and_an_unbacked_claim_reads_advisory()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var page = new OversightPage(OversightSources.For(host));
            var text = Texts(page.Root);
            Assert.Contains(ProtectionsSection.Title.ToUpperInvariant(), text);
            foreach (var inv in InvariantRegistry.All) Assert.Contains(inv.Id, text);
            foreach (var (_, title) in InvariantRegistry.SeatTypes) Assert.Contains(title, text);
            Assert.Contains("open gap — open: operations still places", text);
        }
        finally { await host.Gateway.DisposeAsync(); }

        var unbacked = new Invariant("X-001", "a made-up protection", "nowhere", [], [],
            [new SeatClaim(SeatClasses.Harness, EnforcementClass.HardApp, "it says so", ["T.Seat"])]);
        var section = ProtectionsSection.Build([unbacked]);
        var drawn = Texts(section);
        Assert.Contains("advisory — it says so (claimed enforced by code, shown advisory: no named test proves the property)", drawn);
        Assert.DoesNotContain("enforced by code — it says so", drawn);
        Assert.Contains("Proved by: no named test.", drawn);
        Assert.Empty(section.GetLogicalDescendants().OfType<Button>());
    }

    // ---- the tree scan --------------------------------------------------------------------------------------------------------------

    static string Texts(Control root) =>
        string.Join("\n", root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

    /// <summary>
    /// A tests folder holding one file with a nested class between two tests, a theory, a commented-out test, braces in strings, a test's
    /// text inside an interpolation hole and a raw one, and a test compiled out by <c>#if false</c>.
    /// </summary>
    static string Sample()
    {
        var dir = Path.Combine(Path.GetTempPath(), "invariant-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Sample.cs"), """"
            public class Outer
            {
                [Fact] public void Real() { var s = "}}}"; var c = '{'; }
                sealed class Nested { [Fact] public void Inner_helper_is_its_own_class() { } }
                // [Fact] public void Commented_out() { }
                /* [Fact] public void Blocked_out() { } */
                [Fact]
                public async Task AfterNested() { var r = $"{(true ? "{" : "}")}"; }
                [Theory, InlineData(1)]
                public void Theory_one(int x) { }
                public void Not_a_test() { }
                string S = $"{"[Fact] public void Inside_a_hole() { }"}";
                string R = $$"""{{"[Fact] public void Inside_a_raw_hole() { }"}}""";
            #if false
                [Fact] public void Compiled_out() { }
            #endif
                [Fact] public void Last() { var v = @"say ""}"" "; }
            }
            """");
        return dir;
    }

    /// <summary>
    /// EVERY [Fact]/[Theory] METHOD UNDER <paramref name="dir"/> AS <c>Class.Method</c> — the class that ENCLOSES it (by braces, comments
    /// and strings stripped first), so a nested helper class or a second class in a file is never confused with its neighbour. Any
    /// attribute whose name ends in Fact or Theory counts (<c>[UnixFact]</c>).
    /// </summary>
    static HashSet<string> TestMethods(string dir)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)) continue;
            var code = Strip(WithoutConditionals(File.ReadAllText(file)), keepStrings: false);
            var scopes = new Stack<string?>();
            string? pendingClass = null;
            var pendingTest = false;
            var bracket = 0;
            string? lastWord = null;
            var i = 0;
            while (i < code.Length)
            {
                var ch = code[i];
                if (char.IsLetter(ch) || ch == '_' || ch == '@')
                {
                    var start = i;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '@')) i++;
                    var word = code[start..i];
                    if (bracket > 0 && (word.EndsWith("Fact", StringComparison.Ordinal) || word.EndsWith("Theory", StringComparison.Ordinal)))
                        pendingTest = true;
                    if (bracket == 0 && lastWord is "class" or "struct" or "interface" or "record") pendingClass = word;
                    lastWord = word;
                    continue;
                }
                switch (ch)
                {
                    case '[': bracket++; break;
                    case ']': bracket = Math.Max(0, bracket - 1); break;
                    case '{':
                        scopes.Push(pendingClass);
                        pendingClass = null;
                        pendingTest = false;
                        break;
                    case '}':
                        if (scopes.Count > 0) scopes.Pop();
                        break;
                    case ';':
                        pendingTest = false;
                        break;
                    case '(':
                        if (bracket == 0 && pendingTest && pendingClass is null && lastWord is not null
                            && scopes.Count > 0 && scopes.Peek() is { } owner)
                            found.Add($"{owner}.{lastWord}");
                        if (bracket == 0) pendingTest = false;
                        break;
                }
                i++;
            }
        }
        return found;
    }

    /// <summary>
    /// <paramref name="text"/> with comments blanked, and string and char literals too unless <paramref name="keepStrings"/>: regular,
    /// verbatim, interpolated and raw (<c>"""</c>) strings, an interpolation hole read as code with its own strings skipped in turn
    /// (<see cref="StringEnd"/>), so no text inside a string is ever read as a declaration.
    /// </summary>
    static string Strip(string text, bool keepStrings)
    {
        var o = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                o.Append(' ');
                continue;
            }
            if (c == '"' || c == '\'')
            {
                var start = i;
                i = c == '"' ? StringEnd(text, i) : CharEnd(text, i);
                if (i > start + 1 || c == '"')
                {
                    o.Append(keepStrings ? text[start..i] : " \"\" ");
                    continue;
                }
                i = start;
            }
            o.Append(c);
            i++;
        }
        return o.ToString();
    }

    /// <summary>
    /// WHERE THE STRING WHOSE FIRST QUOTE IS AT <paramref name="i"/> ENDS (one past it). Its prefix — <c>$</c>s and <c>@</c> before the
    /// quote — says whether it is verbatim and how many braces open a hole; a hole is read as code to its closing brace, any string or
    /// char literal inside it skipped by the same rules.
    /// </summary>
    static int StringEnd(string t, int i)
    {
        var start = i;
        var quotes = 0;
        while (i < t.Length && t[i] == '"') { quotes++; i++; }
        var verbatim = false;
        var dollars = 0;
        for (var p = start - 1; p >= 0 && (t[p] == '$' || t[p] == '@'); p--)
            if (t[p] == '@') verbatim = true; else dollars++;

        if (quotes >= 3 && !verbatim)
        {
            var close = new string('"', quotes);
            while (i < t.Length)
            {
                if (string.CompareOrdinal(t, i, close, 0, quotes) == 0) return i + quotes;
                if (dollars > 0 && t[i] == '{')
                {
                    var run = 0;
                    while (i + run < t.Length && t[i + run] == '{') run++;
                    i = run >= dollars ? HoleEnd(t, i + run) : i + run;
                    continue;
                }
                i++;
            }
            return t.Length;
        }
        if (quotes == 2 && !verbatim) return i; // the empty string

        i = start + 1;
        while (i < t.Length)
        {
            var c = t[i];
            var next = i + 1 < t.Length ? t[i + 1] : '\0';
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == '"')
            {
                if (verbatim && next == '"') { i += 2; continue; }
                return i + 1;
            }
            if (dollars > 0 && c == '{')
            {
                if (next == '{') { i += 2; continue; }
                i = HoleEnd(t, i + 1);
                continue;
            }
            i++;
        }
        return t.Length;
    }

    /// <summary>One past the brace that closes the interpolation hole whose code starts at <paramref name="i"/>.</summary>
    static int HoleEnd(string t, int i)
    {
        var depth = 1;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '"') { i = StringEnd(t, i); continue; }
            if (c == '\'') { var e = CharEnd(t, i); i = e > i + 1 ? e : i + 1; continue; }
            if (c == '{') depth++;
            if (c == '}' && --depth == 0) return i + 1;
            i++;
        }
        return t.Length;
    }

    /// <summary>One past a char literal at <paramref name="i"/> ('x', '\n', '\u0000'), or <paramref name="i"/> itself when there is none.</summary>
    static int CharEnd(string t, int i)
    {
        var next = i + 1 < t.Length ? t[i + 1] : '\0';
        var end = t.IndexOf('\'', i + (next == '\\' ? 3 : 2));
        return end > 0 && end - i <= 8 ? end + 1 : i;
    }

    /// <summary>
    /// <paramref name="text"/> with every line inside a <c>#if</c>…<c>#endif</c> region blanked, every branch of it: the scan does not
    /// evaluate symbols, so a test that compiles only under one is counted as missing — the safe side for an existence check.
    /// </summary>
    static string WithoutConditionals(string text)
    {
        var lines = text.Split('\n');
        var depth = 0;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimStart();
            if (line.StartsWith("#if", StringComparison.Ordinal)) depth++;
            if (depth > 0) lines[n] = "";
            if (line.StartsWith("#endif", StringComparison.Ordinal) && depth > 0) depth--;
        }
        return string.Join('\n', lines);
    }
}
