using System.Text.RegularExpressions;
using TradeAgent.App;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class InvariantProofTests
{
    // ---- AUTH-001, the persisted half: the kv rows that hold the owner's authority -------------------------------------------------

    /// <summary>
    /// The owner's authority as it is kept on disk, in the home's <c>kv</c> table: the settings row (the kill switch, the AI's pause and
    /// start, the connector and account chosen), the press kept beside an armed row and the earlier row set aside, the connection chosen,
    /// the automatic-update choice, and the paper stand-in and held account of a home without ATAS. A pipe op that wrote one of these rows
    /// would reach his authority without touching the in-memory setters <see cref="Authority"/> lists.
    /// </summary>
    internal static readonly string[] AuthorityKeys =
        ["settings", TradingGateway.SettingsWhileArmedKey, TradingGateway.SettingsBeforeBoardKey, "connector", "updates.auto",
         AtasHome.StandInKey, AtasHome.HeldAccountKey];

    /// <summary>
    /// Outside the owner's own process (every file of <c>TradeAgent.App</c>), the only members that may write an authority row, each with
    /// its reason: the gateway's save of its own settings (whose authority fields only <see cref="Authority"/>'s setters change), and the
    /// owner's set-aside press of an earlier armed row (in <see cref="Authority"/>, so only the app calls it).
    /// </summary>
    static readonly (string File, string Member)[] AuthorityWriters =
    [
        ("src/TradeAgent.Gateway/TradingGateway.cs", "SaveSettings"),
        ("src/TradeAgent.Gateway/TradingGateway.cs", "SetAsideEarlierLiveSetting"),
    ];

    /// <summary>Where SQL text may write <c>kv</c> itself: the two writes every other caller goes through, and the set-aside press.</summary>
    static readonly (string File, string Member)[] KvStatements =
    [
        ("src/TradeAgent.Core/Db/Database.cs", "SetKv"),
        ("src/TradeAgent.Core/Db/Database.cs", "AddKvOnce"),
        ("src/TradeAgent.Gateway/TradingGateway.cs", "SetAsideEarlierLiveSetting"),
    ];

    /// <summary>
    /// NO PIPE OP AND NO VERB WRITES THE OWNER'S AUTHORITY WHERE IT IS KEPT (AUTH-001, its persisted half). Every call of
    /// <c>SetKv</c> or <c>AddKvOnce</c> in <c>src/</c> outside the app has its key read from the source (<see cref="KvKeys"/>): a literal,
    /// a constant, the head of a concatenation or an interpolation, a key-making method's returns, a local, or a parameter followed to
    /// every caller. A key the reader cannot follow to a fixed beginning fails the test — a key a caller names (an op's argument) is
    /// exactly that. A key that is, or may begin as, an authority row is allowed only in <see cref="AuthorityWriters"/>; every other key
    /// must begin with text no authority key begins with. No other SQL statement writes <c>kv</c> but <see cref="KvStatements"/>, and
    /// no method group of either write is handed anywhere. Watched red (2026-11-14) with the gateway's status read writing kv
    /// <c>connector</c>, and with it writing a key named by the connector's id (unreadable).
    /// </summary>
    [Fact]
    public void No_pipe_op_or_verb_writes_the_owners_authority_where_it_is_kept()
    {
        var keys = new KvKeys(DayOnePrograms.RepoRoot());
        var sites = keys.Sites().ToList();
        Assert.True(sites.Count > 25, $"only {sites.Count} kv writes found"); // not vacuous: the whole tree is read
        var found = new List<string>();
        foreach (var s in sites)
        {
            var where = $"{s.File} {s.Member}: {s.Key}";
            if (s.File.StartsWith("src/TradeAgent.App/", StringComparison.Ordinal)) continue; // the owner's own process
            if (s.Heads is null) { found.Add($"{where} — a key the scan cannot follow to a fixed beginning"); continue; }
            foreach (var h in s.Heads)
            {
                var hits = AuthorityKeys.Where(k => h.Exact ? k == h.Text : k.StartsWith(h.Text, StringComparison.Ordinal)).ToList();
                if (hits.Count > 0 && !AuthorityWriters.Contains((s.File, s.Member)))
                    found.Add($"{where} — writes the owner's {string.Join(", ", hits)} ({(h.Exact ? "the key" : "a key beginning")} \"{h.Text}\")");
            }
        }
        foreach (var (file, member) in keys.Statements())
            if (!KvStatements.Contains((file, member))) found.Add($"{file} {member}: an SQL statement writes kv");
        foreach (var (file, member) in keys.MethodGroups()) found.Add($"{file} {member}: a kv write handed on as a method group");
        Assert.True(found.Count == 0, "the owner's authority is written outside his own process: " + string.Join("; ", found));

        // Not vacuous: the reader resolves what it must, both ways.
        Assert.Contains(sites, s => s.Member == "SaveSettings" && s.Heads is { } h && h.Contains(new("settings", true)));
        Assert.Contains(sites, s => s.Member == "SaveSettings" && s.Heads is { } h && h.Contains(new(TradingGateway.SettingsWhileArmedKey, true)));
        Assert.Contains(sites, s => s.File.EndsWith("WorkspaceRevisions.cs", StringComparison.Ordinal) && s.Heads is { } h
                                    && h.Contains(new("revision_rejected:", false)));
        Assert.Contains(sites, s => s.Member == "Close" && s.Heads is { Count: > 0 } h && h.All(x => x.Text.StartsWith("loss_", StringComparison.Ordinal)));
        Assert.Contains(sites, s => s.File.StartsWith("src/TradeAgent.App/", StringComparison.Ordinal) && s.Key == "\"connector\"");
        Assert.Equal(2, keys.Statements().Count(s => s.File == "src/TradeAgent.Core/Db/Database.cs"));
    }

    /// <summary>A key as far as the source fixes it: the key itself (<see cref="Exact"/>), or the text every key from there begins with.</summary>
    internal readonly record struct Head(string Text, bool Exact);

    /// <summary>One write of a kv row: where it is, the key as written, and what the key resolves to (null: it cannot be followed).</summary>
    internal sealed record KvSite(string File, string Member, string Key, IReadOnlyList<Head>? Heads);

    /// <summary>
    /// THE KV KEY READER: every <c>SetKv</c>/<c>AddKvOnce</c> call in <c>src/</c> (comments blanked, strings kept), and what its key can
    /// be — followed through literals, constants and readonly fields, <c>+</c> and interpolations (by their first part), <c>?:</c> and
    /// <c>??</c> (every branch), string-returning methods (every return, the arguments bound to the parameters), locals, a
    /// <c>foreach</c> over <c>KvStartingWith(prefix)</c>, and a parameter of the enclosing member (every call of it). Anything else is
    /// unreadable, and the test above turns red on it: the reader never guesses.
    /// </summary>
    internal sealed class KvKeys
    {
        readonly Dictionary<string, string> code = new(StringComparer.Ordinal);

        public KvKeys(string root)
        {
            foreach (var f in SourceFiles(root, "src"))
                code[Path.GetRelativePath(root, f).Replace('\\', '/')] = InvariantRegistryTests.Strip(File.ReadAllText(f), keepStrings: true);
        }

        sealed record Scope(string File, int Start, int At, string Member, IReadOnlyList<string> Params,
            IReadOnlyDictionary<string, (string Expr, Scope In)> Env);

        static readonly Regex Write = new(@"\b(SetKv|AddKvOnce)\b");
        static readonly Regex KvSql = new(@"\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+\w+)?|DELETE\s+FROM)\s+[""`\[]?kv\b(?![_\w])",
            RegexOptions.IgnoreCase);
        static readonly string[] NotATypeWord = ["return", "await", "new", "throw", "yield", "else", "case", "using", "if", "while", "for", "foreach",
            "switch", "catch", "lock", "when", "is", "as", "in", "nameof", "typeof", "sizeof", "default", "checked", "unchecked", "fixed", "goto", "var"];

        public IEnumerable<KvSite> Sites()
        {
            foreach (var (file, text) in code)
                foreach (Match m in Write.Matches(text))
                {
                    var after = SkipSpace(text, m.Index + m.Length);
                    if (after >= text.Length || text[after] != '(' || IsDeclaration(text, m.Index)) continue;
                    var args = Args(text, after, out _);
                    if (args.Count == 0) continue;
                    var scope = ScopeAt(file, m.Index);
                    yield return new(file, scope.Member, args[0].Trim(), Resolve(args[0], scope, 0));
                }
        }

        public IEnumerable<(string File, string Member)> Statements() =>
            code.SelectMany(kv => KvSql.Matches(kv.Value).Select(m => (kv.Key, ScopeAt(kv.Key, m.Index).Member)));

        public IEnumerable<(string File, string Member)> MethodGroups() =>
            code.SelectMany(kv => Write.Matches(kv.Value)
                .Where(m => SkipSpace(kv.Value, m.Index + m.Length) is var a && (a >= kv.Value.Length || kv.Value[a] != '(')
                            && !IsDeclaration(kv.Value, m.Index))
                .Select(m => (kv.Key, ScopeAt(kv.Key, m.Index).Member)));

        static bool IsDeclaration(string text, int at) =>
            Regex.IsMatch(text[LineStart(text, at)..at], @"^\s*(?:(?:public|internal|private|protected|static)\s+)*(?:void|bool)\s+$");

        // ---- resolving ------------------------------------------------------------------------------------------------------------

        List<Head>? Resolve(string expr, Scope s, int depth)
        {
            if (depth > 14) return null;
            var e = Unwrap(expr);
            if (e.Length == 0) return null;
            if (Ternary(e) is var (a, b)) return Union(Resolve(a, s, depth + 1), Resolve(b, s, depth + 1));
            if (SplitTop(e, "??") is { Count: > 1 } alts)
            {
                List<Head>? all = [];
                foreach (var alt in alts) all = Union(all, Resolve(alt, s, depth + 1));
                return all;
            }
            if (SplitTop(e, "+") is { Count: > 1 } parts) return Concat(parts.Select(p => (Func<List<Head>?>)(() => Resolve(p, s, depth + 1))).ToList());
            if (e.StartsWith('"') && e.EndsWith('"') && e.Length >= 2 && !e.StartsWith("\"\"\"", StringComparison.Ordinal))
                return [new(Regex.Unescape(e[1..^1]), true)];
            if (e.StartsWith("@\"", StringComparison.Ordinal) && e.EndsWith('"')) return [new(e[2..^1].Replace("\"\"", "\""), true)];
            if ((e.StartsWith("$\"", StringComparison.Ordinal) || e.StartsWith("$@\"", StringComparison.Ordinal) || e.StartsWith("@$\"", StringComparison.Ordinal))
                && e.EndsWith('"') && !e.Contains("\"\"\"", StringComparison.Ordinal))
                return Concat(Interpolation(e).Select(p => p.Hole
                    ? (Func<List<Head>?>)(() => Resolve(p.Text, s, depth + 1))
                    : () => [new(p.Text, true)]).ToList());
            if (e.EndsWith(']')) return null; // an index or a range of something: its beginning is not its source's
            if (Regex.Match(e, @"^(?<q>(?:[A-Za-z_]\w*\.)*)(?<m>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(") is { Success: true } call
                && Close(e, call.Length - 1) == e.Length - 1)
                return Method(call.Groups["q"].Value.TrimEnd('.'), call.Groups["m"].Value, Args(e, call.Length - 1, out _), s, depth);
            if (Regex.IsMatch(e, @"^(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*$")) return Name(e.StartsWith("this.", StringComparison.Ordinal) ? e[5..] : e, s, depth);
            return null;
        }

        List<Head>? Name(string e, Scope s, int depth)
        {
            var dot = e.LastIndexOf('.');
            var (q, n) = dot < 0 ? ("", e) : (e[..dot], e[(dot + 1)..]);
            if (q.Length == 0)
            {
                if (s.Env.TryGetValue(n, out var bound)) return Resolve(bound.Expr, bound.In, depth + 1);
                if (Local(n, s, depth) is var (found, heads)) return found ? heads : null;
                if (s.Params.Contains(n)) return Callers(n, s, depth);
            }
            // A constant, a readonly field or a string property with an initializer, in this file, the qualifier's type, or anywhere.
            var decl = new Regex(@"\b(?:const\s+string|static\s+readonly\s+string\??|static\s+string\??)\s+" + Regex.Escape(n) + @"\s*(?:=>|=(?!=))");
            foreach (var files in Candidates(q, s.File))
            {
                List<Head>? all = null;
                foreach (var f in files)
                    foreach (Match m in decl.Matches(code[f]))
                    {
                        var start = m.Index + m.Length;
                        var init = code[f][start..End(code[f], start)];
                        all = Union(all ?? [], Resolve(init, ScopeAt(f, m.Index), depth + 1));
                        if (all is null) return null;
                    }
                if (all is not null) return all;
            }
            return null;
        }

        /// <summary>A local declared before the use in the same member: (true, heads) when one is found, null when none is.</summary>
        (bool, List<Head>?)? Local(string n, Scope s, int depth)
        {
            var text = code[s.File][s.Start..s.At];
            var decl = Regex.Matches(text, @"\b(?:var|string\??)\s+" + Regex.Escape(n) + @"\s*=(?!=)").LastOrDefault();
            var loop = Regex.Matches(text, @"\bforeach\s*\(\s*var\s*\(\s*" + Regex.Escape(n) + @"\s*,[^)]*\)\s*in\s+").LastOrDefault();
            var other = Regex.Matches(text, @"(?:\bout\s+(?:var|string\??)|\bis\s+(?:\{\s*\}|string\??)|\bforeach\s*\(\s*(?:var|string\??))\s+" + Regex.Escape(n) + @"\b")
                .LastOrDefault();
            var last = new[] { decl, loop, other }.Where(m => m is not null).MaxBy(m => m!.Index);
            if (last is null) return null;
            var at = s.Start + last.Index + last.Length;
            var inner = s with { At = s.Start + last.Index };
            if (last == decl) return (true, Resolve(code[s.File][at..End(code[s.File], at)], inner, depth + 1));
            if (last == loop)
            {
                var source = code[s.File][at..End(code[s.File], at, ')')].Trim();
                var scan = Regex.Match(source, @"\.KvStartingWith\s*\(");
                if (!scan.Success || Close(source, scan.Index + scan.Length - 1) != source.Length - 1) return (true, null);
                var heads = Resolve(source[(scan.Index + scan.Length)..^1], inner, depth + 1);
                return (true, heads?.Select(h => h with { Exact = false }).ToList());
            }
            return (true, null);
        }

        List<Head>? Callers(string param, Scope s, int depth)
        {
            var index = s.Params.ToList().IndexOf(param);
            var call = new Regex(@"(?<![\w.])(?:this\.)?" + Regex.Escape(s.Member) + @"\s*\(");
            List<Head>? all = null;
            foreach (var (f, text) in code.Where(c => c.Key == s.File))
                foreach (Match m in call.Matches(text))
                {
                    var site = ScopeAt(f, m.Index);
                    if (site.Start == s.Start && m.Index < s.Start + 200 && IsDeclarationOf(text, m.Index)) continue;
                    if (IsDeclarationOf(text, m.Index)) continue;
                    var args = Args(text, m.Index + m.Length - 1, out _);
                    var arg = Argument(args, index, param);
                    if (arg is null) return null;
                    all = Union(all ?? [], Resolve(arg, site, depth + 1));
                    if (all is null) return null;
                }
            return all;
        }

        List<Head>? Method(string q, string name, IReadOnlyList<string> args, Scope s, int depth)
        {
            if (q.Length > 0 && !char.IsUpper(q.Split('.')[^1][0])) return null; // an instance's method: a value, not a key maker
            var decl = new Regex(@"(?m)^[ \t]*(?:(?:public|internal|private|protected|static|override|virtual|sealed|new|partial)\s+)*string\??\s+"
                                 + Regex.Escape(name) + @"\s*\(");
            foreach (var files in Candidates(q, s.File, sameFileOnly: q.Length == 0))
            {
                List<Head>? all = null;
                foreach (var f in files)
                    foreach (Match m in decl.Matches(code[f]))
                    {
                        var text = code[f];
                        var ps = Args(text, m.Index + m.Length - 1, out var close).Select(ParamName).ToList();
                        var required = Args(text, m.Index + m.Length - 1, out _).Count(p => !p.Contains('='));
                        if (args.Count < required || args.Count > ps.Count) continue;
                        var env = new Dictionary<string, (string, Scope)>(StringComparer.Ordinal);
                        for (var i = 0; i < ps.Count; i++)
                            if (Argument(args, i, ps[i]) is { } a) env[ps[i]] = (a, s);
                        var body = new Scope(f, m.Index, close + 1, name, ps, env);
                        List<Head>? returns = [];
                        foreach (var r in Returns(text, close + 1))
                            returns = Union(returns, Resolve(r.Expr, body with { At = r.At }, depth + 1));
                        all = Union(all ?? [], returns);
                        if (all is null) return null;
                    }
                if (all is not null) return all.Count == 0 ? null : all;
            }
            return null;
        }

        /// <summary>The expressions a body returns: an expression body, or every <c>return</c> of a block.</summary>
        static IEnumerable<(string Expr, int At)> Returns(string text, int from)
        {
            var i = SkipSpace(text, from);
            if (text.AsSpan(i).StartsWith("=>")) { yield return (text[(i + 2)..End(text, i + 2)], i); yield break; }
            if (i >= text.Length || text[i] != '{') yield break;
            var close = Close(text, i);
            foreach (Match r in Regex.Matches(text[i..close], @"\breturn\s+"))
            {
                var at = i + r.Index + r.Length;
                yield return (text[at..End(text, at)], at);
            }
        }

        IEnumerable<IReadOnlyList<string>> Candidates(string q, string file, bool sameFileOnly = false)
        {
            if (q.Length == 0) { yield return [file]; if (sameFileOnly) yield break; }
            var type = q.Length == 0 ? null : q.Split('.')[^1];
            if (type is not null)
                yield return code.Keys.Where(f => Regex.IsMatch(code[f], @"\b(?:class|record|struct|interface)\s+" + Regex.Escape(type) + @"\b")).ToList();
            if (q.Length == 0) yield return code.Keys.ToList();
        }

        static string? Argument(IReadOnlyList<string> args, int index, string name)
        {
            foreach (var a in args)
                if (Regex.Match(a, @"^\s*(\w+)\s*:(?!:)") is { Success: true } named && named.Groups[1].Value == name) return a[named.Length..];
            return index < args.Count && !Regex.IsMatch(args[index], @"^\s*\w+\s*:(?!:)") ? args[index] : null;
        }

        static string ParamName(string p)
        {
            var noDefault = SplitTop(p, "=")[0];
            return Regex.Match(noDefault, @"(\w+)\s*$").Groups[1].Value;
        }

        // ---- scopes -----------------------------------------------------------------------------------------------------------

        /// <summary>A member's declaration line: modifiers, ONE return type (a tuple type is not read: its member is then unreadable), the name.</summary>
        static readonly Regex MemberLine = new(
            @"^[ \t]{4,}(?<pre>(?:(?:public|internal|private|protected|static|async|override|virtual|sealed|readonly|unsafe|new|partial|extern|abstract)\s+)*"
            + @"[A-Za-z_][\w.]*(?:<[^()=;]*>)?(?:\[\])*\??)\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(");

        Scope ScopeAt(string file, int at)
        {
            var text = code[file];
            var i = LineStart(text, at);
            while (i > 0)
            {
                var end = text.IndexOf('\n', i) is var n && n < 0 ? text.Length : n;
                if (MemberLine.Match(text[i..end]) is { Success: true } m
                    && !NotATypeWord.Contains(m.Groups["pre"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                    && !m.Groups["pre"].Value.EndsWith('.'))
                {
                    var open = i + m.Length - 1;
                    var ps = Args(text, open, out _).Select(ParamName).Where(x => x.Length > 0).ToList();
                    return new(file, i, at, m.Groups["name"].Value, ps, new Dictionary<string, (string, Scope)>());
                }
                i = LineStart(text, i - 1);
            }
            return new(file, 0, at, "?", [], new Dictionary<string, (string, Scope)>());
        }

        static bool IsDeclarationOf(string text, int at)
        {
            var line = text[LineStart(text, at)..at];
            return MemberLine.IsMatch(line + "x(") && !NotATypeWord.Contains(line.Trim().Split(' ')[0]) && !line.TrimEnd().EndsWith('.')
                   && !line.Contains('=') && !line.Contains('(');
        }

        // ---- text -----------------------------------------------------------------------------------------------------------

        static List<Head>? Union(List<Head>? a, List<Head>? b) => a is null || b is null ? null : [.. a.Concat(b).Distinct()];

        /// <summary>The heads of a concatenation: its first part, extended by the next while every head so far is exact.</summary>
        static List<Head>? Concat(IReadOnlyList<Func<List<Head>?>> parts)
        {
            var acc = parts[0]();
            if (acc is null) return null;
            for (var i = 1; i < parts.Count && acc.Any(h => h.Exact); i++)
            {
                var next = parts[i]();
                acc = [.. acc.SelectMany(h => !h.Exact ? [h]
                    : next is null ? [new Head(h.Text, false)]
                    : next.Select(x => new Head(h.Text + x.Text, x.Exact))).Distinct()];
            }
            return acc;
        }

        /// <summary>An interpolated string as its literal parts and its holes (a hole's format and alignment dropped).</summary>
        static IEnumerable<(string Text, bool Hole)> Interpolation(string e)
        {
            var body = e[(e.IndexOf('"') + 1)..^1];
            var lit = new System.Text.StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '{' && i + 1 < body.Length && body[i + 1] == '{') { lit.Append('{'); i++; continue; }
                if (body[i] == '}' && i + 1 < body.Length && body[i + 1] == '}') { lit.Append('}'); i++; continue; }
                if (body[i] != '{') { lit.Append(body[i]); continue; }
                if (lit.Length > 0) { yield return (lit.ToString(), false); lit.Clear(); }
                var close = Close(body, i);
                var hole = body[(i + 1)..close];
                yield return (SplitTop(SplitTop(hole, ":")[0], ",")[0], true);
                i = close;
            }
            if (lit.Length > 0) yield return (lit.ToString(), false);
        }

        static string Unwrap(string expr)
        {
            var e = expr.Trim();
            while (true)
            {
                if (e.EndsWith('!')) { e = e[..^1].TrimEnd(); continue; }
                if (e.StartsWith('(') && Close(e, 0) == e.Length - 1) { e = e[1..^1].Trim(); continue; }
                return e;
            }
        }

        static (string, string)? Ternary(string e)
        {
            var depth = 0;
            for (var i = 0; i < e.Length; i++)
            {
                var c = e[i];
                if (c == '"') { i = InvariantRegistryTests.StringEnd(e, i) - 1; continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == '?' && depth == 0 && i + 1 < e.Length && e[i + 1] is not ('.' or '?' or '[') && (i == 0 || e[i - 1] != '?'))
                {
                    var rest = e[(i + 1)..];
                    var colon = TopColon(rest);
                    return colon < 0 ? null : (rest[..colon], rest[(colon + 1)..]);
                }
            }
            return null;
        }

        static int TopColon(string s)
        {
            int depth = 0, nested = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') { i = InvariantRegistryTests.StringEnd(s, i) - 1; continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (depth == 0 && c == '?' && i + 1 < s.Length && s[i + 1] is not ('.' or '?' or '[') && s[i - 1] != '?') nested++;
                else if (depth == 0 && c == ':' && (i + 1 >= s.Length || s[i + 1] != ':')) { if (nested == 0) return i; nested--; }
            }
            return -1;
        }

        /// <summary><paramref name="e"/> split on <paramref name="op"/> outside brackets and strings (a unary <c>+</c> and <c>==</c> are not splits).</summary>
        static List<string> SplitTop(string e, string op)
        {
            var parts = new List<string>();
            int depth = 0, from = 0;
            for (var i = 0; i < e.Length; i++)
            {
                var c = e[i];
                if (c == '"') { i = InvariantRegistryTests.StringEnd(e, i) - 1; continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (depth == 0 && string.CompareOrdinal(e, i, op, 0, op.Length) == 0
                         && !(op == "=" && (i + 1 < e.Length && e[i + 1] is '=' or '>' || i > 0 && e[i - 1] is '=' or '!' or '<' or '>'))
                         && !(op == "+" && (i + 1 < e.Length && e[i + 1] is '+' or '=' || e[..i].Trim().Length == 0)))
                {
                    parts.Add(e[from..i]);
                    from = i + op.Length;
                    i += op.Length - 1;
                }
            }
            parts.Add(e[from..]);
            return parts;
        }

        /// <summary>The comma-separated arguments inside the bracket at <paramref name="open"/>; <paramref name="close"/> is its match.</summary>
        static List<string> Args(string text, int open, out int close)
        {
            close = Close(text, open);
            var inside = text[(open + 1)..close];
            return inside.Trim().Length == 0 ? [] : SplitTop(inside, ",");
        }

        static int Close(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') { i = InvariantRegistryTests.StringEnd(text, i) - 1; continue; }
                if (c == '\'' && i + 2 < text.Length) { i = text.IndexOf('\'', i + (text[i + 1] == '\\' ? 3 : 2)); continue; }
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}' && --depth == 0) return i;
            }
            return text.Length - 1;
        }

        /// <summary>Where the expression starting at <paramref name="from"/> ends: the first <paramref name="stop"/> (or <c>;</c>) outside brackets.</summary>
        static int End(string text, int from, char stop = ';')
        {
            var depth = 0;
            for (var i = from; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') { i = InvariantRegistryTests.StringEnd(text, i) - 1; continue; }
                if (depth == 0 && (c == stop || c == ';')) return i;
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
            }
            return text.Length;
        }

        static int SkipSpace(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i;
        }

        static int LineStart(string text, int at) => at <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(at, text.Length) - 1) + 1;
    }
}
