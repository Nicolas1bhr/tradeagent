using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Oversight;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE OWNER'S PRIVATE LINE IS INVISIBLE TO THE ORGANISATION (<c>U-org-watcher-a</c>; ORGANISATION § 9 "Invisible", ORCHESTRATION § 5).
/// Three guards, the survey's Q1 list: DIFFERENTIAL — nothing an agent can reach answers differently because the line ran; STRUCTURAL — no
/// agent-reachable assembly holds, takes or returns anything of it and no source names it; UNTOUCHED — a sweep leaves the organisation's
/// database and the logs exactly as they were. And the list of totals an agent can read, pinned.
/// </summary>
[Collection("containment")]
public partial class OversightInvisibilityTests(ITestOutputHelper log)
{
    /// <summary>
    /// SURFACES THAT ANSWER DIFFERENTLY BETWEEN TWO IDENTICALLY SEEDED HOMES NEITHER OF WHICH WAS SWEPT, each with its reason — the only ones the
    /// differential cannot hold. Discovered by the test (control against control) and pinned here, so a new unstable surface fails until it is
    /// named.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> Unstable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
    };

    // ---- (b) the differential -----------------------------------------------------------------------------------------

    /// <summary>One home the organisation can reach: its gateway, database, pipe, broker door per role, and meter.</summary>
    sealed record Reach(TradingGateway Gw, Database Db, GatewayPipeServer Server, string Folder,
        IReadOnlyList<(string Role, IWorkerTools Tools)> Doors, TurnMeter Meter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Gw.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>A home seeded with every detector's records under fixed ids, so two of them hold the same rows.</summary>
    static async Task<Reach> Seeded(DateTimeOffset now)
    {
        var (gw, _, db) = await TestEnv.Ready();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-oversight-" + Guid.NewGuid().ToString("n")[..12]);
        foreach (var d in OversightSeed.Detectors) OversightSeed.Seed(db.FilePath, d, now, fixedTag: "fixed");
        // AND SPEND IN OPERATIONS, where nothing is accepted: Research's seeded acceptances answer its own spend.
        OversightSeed.Seed(db.FilePath, "spend-no-accept", now, OrgIds.Operations, fixedTag: "fixed-ops");
        var grants = new AgentGrants();
        var folder = Path.Combine(TestEnv.Home, "oversight-door-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(folder);
        var ledger = new ToolCallStore(db);
        var doors = new[] { CouncilRoles.Research, CouncilRoles.Operations }
            .Select(r => (r, BrokerDoor.ToolsFor(grants.IssueHere(r, "attempt-oversight-" + r), _ => folder, () => server, ledger)))
            .ToList();
        var meter = new TurnMeter(db, cap: () => 5m, recordPath: Path.Combine(folder, "turns.jsonl"), live: new LiveAttempts());
        return new Reach(gw, db, server, folder, doors, meter);
    }

    /// <summary>
    /// EVERYTHING ONE HOME ANSWERS THE ORGANISATION: every <see cref="Ops"/> const on the pipe as Research and as Operations under a brokered
    /// attempt; every door verb the broker offers each; the Situation as the app's own readers compose it from the database, and its spend
    /// and share lines; every pinned total. This home's own folder and file are masked, as is every ISO instant (the wall clock it was asked at).
    /// </summary>
    static async Task<SortedDictionary<string, string>> Pass(Reach h, DateTimeOffset now)
    {
        var seen = new Seen(h.Folder, h.Db.FilePath);
        foreach (var op in AllOps())
            foreach (var role in new[] { CouncilRoles.Research, CouncilRoles.Operations })
            {
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var reply = await h.Server.CallAsync(new IpcRequest { Id = "oversight-1", Op = op, Session = "oversight-" + role, RequestId = "oversight-req", Args = [] },
                    role, "attempt-oversight-" + role, bound.Token);
                seen[$"op {op} as {role}"] = Json.Write(reply);
            }
        foreach (var (role, tools) in h.Doors)
        {
            seen[$"door list as {role}"] = string.Join(",", tools.Offered.Select(t => t.Name));
            foreach (var t in tools.Offered)
            {
                var answer = await tools.InvokeAsync(new ToolRequest("call-1", t.Name, "{}"));
                seen[$"door {t.Name} as {role}"] = $"{answer.Served} {answer.Content}";
            }
        }
        foreach (var role in new[] { CouncilRoles.Research, CouncilRoles.Operations })
        {
            var situation = new MissionSituation
            {
                Role = role, LocalTime = now, Spend = h.Meter.TodayFor(role), HeldOutputs = CouncilRelay.HeldNotices(h.Db, role),
                Wanted = RoleObjectives.Open(h.Db, role, null),
            };
            seen[$"situation of {role}"] = situation.Text();
            seen[$"spend line of {role}"] = MissionSituation.SpendLine(h.Meter.TodayFor(role)) ?? "none";
            seen[$"share line of {role}"] = MissionSituation.ShareLine(h.Meter.TodayFor(role)) ?? "none";
        }
        seen["spend line of the day"] = MissionSituation.SpendLine(h.Meter.Today) ?? "none";
        foreach (var (name, value) in Totals(h.Db, now)) seen["total " + name] = value;
        return seen.All;
    }

    /// <summary>
    /// (b) GUARD: NO AGENT-REACHABLE SURFACE ANSWERS DIFFERENTLY WHEN THE LINE HAS RUN. Three homes seeded identically with every detector's
    /// records; in one, a sweep fires every detector over two scopes and writes a digest; then each answers everything it can answer the
    /// organisation (<see cref="Pass"/>). The swept home answers byte for byte as the unswept one, but for the surfaces the two unswept
    /// homes already disagree on, which are named in <see cref="Unstable"/> with their reasons. Pins an absence: watched red with the
    /// sweep writing a severe finding's sentence to the activity log (the unit's mutant), which the daily report serves.
    /// </summary>
    [Fact]
    public Task No_agent_reachable_surface_answers_differently_when_the_line_has_run() => Differential(null);

    /// <summary>
    /// (d) GUARD: THE OWNER'S CAP IS READ BY NO ADMISSION GATE AND NO AGENT-REACHABLE TOTAL (BUDGET-001, <c>U-org-watcher-b</c>). The
    /// differential (b) re-run with a cap raised, then set to zero, and a finding acknowledged in the swept home's file — nothing the
    /// organisation can reach answers differently; the totals (b2) are the pinned list still; and no source but the line's own store and
    /// the owner's page names the cap, its reader or its table, so <c>AiAttemptStore.Begin</c> and every total read nothing of it. RED at
    /// base: no cap existed to set.
    /// </summary>
    [Fact]
    public async Task The_cap_is_read_by_no_admission_gate_and_no_agent_reachable_total()
    {
        await Differential(store =>
        {
            var now = OversightSeed.Now();
            store.SetCap(7m, "USD", now);
            store.SetCap(0m, "USD", now);
            store.Acknowledge(store.Findings()[0].Id, now);
            Assert.Equal(0m, store.Cap()!.Amount);
        });
        Every_total_an_agent_can_read_is_on_the_enumerated_list();

        var root = RepoRoot();
        var cap = new System.Text.RegularExpressions.Regex(@"\bOversightCap\b|oversight_cap|\bSetCap\(|\.Cap\(\)");
        var readers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => cap.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToList();
        log.WriteLine(string.Join(", ", readers));
        Assert.Equal(["src/TradeAgent.App/OversightView.cs", "src/TradeAgent.Core/Oversight/OversightStore.cs"], readers);
    }

    /// <summary>
    /// THE DIFFERENTIAL: three homes seeded alike, one swept over two scopes — and, when <paramref name="owner"/> is given, the owner's own
    /// acts on that file after the sweep — then everything each answers the organisation compared.
    /// </summary>
    async Task Differential(Action<OversightStore>? owner)
    {
        var now = OversightSeed.Now();
        await using var swept = await Seeded(now);
        await using var control = await Seeded(now);
        await using var second = await Seeded(now);

        using (var store = new OversightStore(Path.Combine(TestEnv.Home, $"oversight-{Guid.NewGuid():n}.db"), now))
        {
            store.AddScope("dept-research", OrgIds.Research, now);
            var result = new OversightSweep(swept.Db.FilePath, store).Run(now);
            Assert.Null(result.Failure);
            Assert.Equal(OversightSeed.Detectors.Order(StringComparer.Ordinal),
                result.Added.Select(f => f.Detector).Distinct().Order(StringComparer.Ordinal));
            Assert.Contains(result.Added, f => f.ScopeId == "dept-research");
            Assert.NotEmpty(result.Digests);
            owner?.Invoke(store);
        }

        var c = await Pass(control, now);
        var d = await Pass(second, now);
        var s = await Pass(swept, now);
        Assert.Equal(c.Keys, s.Keys);
        var unstable = c.Keys.Where(k => c[k] != d[k]).ToList();
        foreach (var k in unstable) log.WriteLine($"unstable between two unswept homes: {k}\n  1: {Around(c[k], d[k])}\n  2: {Around(d[k], c[k])}");
        Assert.Equal(Unstable.Keys.Order(StringComparer.Ordinal), unstable.Select(Surface).Distinct().Order(StringComparer.Ordinal));

        log.WriteLine($"{s.Count} surfaces compared ({AllOps().Count} ops × 2 roles, {swept.Doors.Sum(x => x.Tools.Offered.Count)} door verbs, 2 Situations, the totals)");
        var moved = s.Keys.Where(k => !Unstable.ContainsKey(Surface(k)) && s[k] != c[k]).ToList();
        foreach (var k in moved) log.WriteLine($"MOVED: {k}\n  unswept: {Around(c[k], s[k])}\n  swept:   {Around(s[k], c[k])}");
        Assert.True(moved.Count == 0, "the organisation can see that the line ran: " + string.Join(", ", moved));
    }

    /// <summary>
    /// THE ANSWERS OF ONE PASS, ON A FIXED CLOCK: every instant an answer carries, ISO or as the report prints it — the wall clock it was asked at (a report's
    /// <c>snapshot_at</c> and "snapshot taken", a status's health times, pnl's <c>as_of</c>) — is masked to one token, so two passes compare everything but the
    /// moment they ran. A seeded row's own instant is the same in both and masked in both alike.
    /// </summary>
    sealed partial class Seen(params string[] own)
    {
        public SortedDictionary<string, string> All { get; } = new(StringComparer.Ordinal);

        public string this[string key]
        {
            set
            {
                var v = value;
                foreach (var o in own) v = v.Replace(o, "<this home>", StringComparison.Ordinal).Replace(JsonEncoded(o), "<this home>", StringComparison.Ordinal);
                All[key] = Instant().Replace(v, "<instant>");
            }
        }

        static string JsonEncoded(string s) => JsonSerializer.Serialize(s)[1..^1];

        [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d\d-\d\d[T ]\d\d:\d\d(:\d\d(\.\d+)?)?(Z| UTC|(\\u002B|[+-])\d\d:\d\d)?")]
        private static partial System.Text.RegularExpressions.Regex Instant();
    }

    /// <summary>
    /// THE MASK COVERS EVERY WALL-CLOCK FORM THE ANSWERS PRINT — ISO, the report's "snapshot taken" and the minute a home began recording fills
    /// ("since 2026-10-22 00:27 UTC"), which two homes made either side of a minute boundary print differently. RED before the minute form was
    /// masked: a windows-latest run whose swept home was made at 00:26 and its control at 00:27 read as the line being seen.
    /// </summary>
    [Fact]
    public void The_differential_masks_every_wall_clock_form_the_answers_print()
    {
        var seen = new Seen();
        seen["iso"] = "\"as_of\":\"2026-10-22T00:15:43.770191+00:00\"";
        seen["printed"] = "snapshot taken: 2026-10-22 02:18:49\\u002B02:00";
        seen["minute"] = "recording your fills since 2026-10-22 00:27 UTC; anything";
        Assert.Equal("\"as_of\":\"<instant>\"", seen.All["iso"]);
        Assert.Equal("snapshot taken: <instant>", seen.All["printed"]);
        Assert.Equal("recording your fills since <instant>; anything", seen.All["minute"]);
    }

    /// <summary>A surface's name without the role it was asked as.</summary>
    static string Surface(string key) => key.Split(" as ")[0];

    /// <summary>The stretch of <paramref name="s"/> around its first difference from <paramref name="other"/>.</summary>
    static string Around(string s, string other)
    {
        var i = 0;
        while (i < s.Length && i < other.Length && s[i] == other[i]) i++;
        var from = Math.Max(0, i - 120);
        return s.Substring(from, Math.Min(240, s.Length - from));
    }

    static string Cut(string s) => s.Length > 400 ? s[..400] + "…" : s;

    static IReadOnlyList<string> AllOps() => [.. typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Distinct().Order(StringComparer.Ordinal)];

    // ---- (b2) the totals ----------------------------------------------------------------------------------------------

    /// <summary>THE TOTALS AN AGENT CAN READ, by the method that answers each: the list (b2) pins and (b) holds still across a sweep.</summary>
    static readonly string[] PinnedTotals =
    [
        "AiAttemptStore.TotalsBetween", "AiAttemptStore.UnitTotalsBetween", "OrgStore.SpendOf", "TurnMeter.Today", "TurnMeter.TodayFor",
        // U-billing-classes: today against one class's ceiling — the same rows read through the class filter the store's two take.
        "TurnMeter.TodayIn",
    ];

    /// <summary>Every pinned total, asked of this home for today and for each unit and mandate.</summary>
    static IEnumerable<(string Name, string Value)> Totals(Database db, DateTimeOffset now)
    {
        var attempts = new AiAttemptStore(db);
        var org = new OrgStore(db);
        var (from, to) = (now.AddDays(-30), now.AddDays(1));
        yield return ("TotalsBetween", JsonSerializer.Serialize(attempts.TotalsBetween(from, to)));
        foreach (var role in CouncilRoles.All) yield return ($"TotalsBetween {role}", JsonSerializer.Serialize(attempts.TotalsBetween(from, to, role)));
        foreach (var u in org.Units()) yield return ($"UnitTotalsBetween {u.Id}", JsonSerializer.Serialize(attempts.UnitTotalsBetween(from, to, u.Id)));
        // AND THROUGH EACH CEILING'S FILTER (U-billing-classes): a class's rows, its own and the unknown ones.
        foreach (var ceiling in new[] { BillingClasses.Plan, BillingClasses.Key })
        {
            yield return ($"TotalsBetween ceiling {ceiling}", JsonSerializer.Serialize(attempts.TotalsBetween(from, to, null, ceiling)));
            foreach (var u in org.Units())
                yield return ($"UnitTotalsBetween {u.Id} ceiling {ceiling}", JsonSerializer.Serialize(attempts.UnitTotalsBetween(from, to, u.Id, ceiling)));
        }
        using var c = OversightSeed.Open(db.FilePath);
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM org_mandate ORDER BY id";
        var mandates = new List<string>();
        using (var r = q.ExecuteReader()) while (r.Read()) mandates.Add(r.GetString(0));
        foreach (var m in mandates) yield return ($"SpendOf {m}", JsonSerializer.Serialize(org.SpendOf(m)));
    }

    /// <summary>
    /// (b2) GUARD: EVERY TOTAL AN AGENT CAN READ IS ON THE ENUMERATED LIST. The public methods and properties of <see cref="AiAttemptStore"/>,
    /// <see cref="OrgStore"/> and <see cref="TurnMeter"/> answering an <see cref="AiAttemptTotals"/>, an <see cref="AiSpendToday"/> or a
    /// <see cref="MandateSpend"/> are discovered and must be exactly the pinned list, which (b) asks before and after a sweep — so a new total
    /// fails here until it joins. The line itself writes no <c>ai_attempt</c> row, so every one of them excludes it by construction.
    /// </summary>
    [Fact]
    public void Every_total_an_agent_can_read_is_on_the_enumerated_list()
    {
        Type[] totals = [typeof(AiAttemptTotals), typeof(AiSpendToday), typeof(MandateSpend)];
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new[] { typeof(AiAttemptStore), typeof(OrgStore), typeof(TurnMeter) }
            .SelectMany(t => t.GetMethods(flags).Where(m => !m.IsSpecialName && totals.Contains(Unwrap(m.ReturnType))).Select(m => $"{t.Name}.{m.Name}")
                .Concat(t.GetProperties(flags).Where(p => totals.Contains(Unwrap(p.PropertyType))).Select(p => $"{t.Name}.{p.Name}")))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        log.WriteLine(string.Join(", ", found));
        Assert.Equal(PinnedTotals.Order(StringComparer.Ordinal), found);
    }

    static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>) ? t.GetGenericArguments()[0] : t);

    // ---- (c) structural -----------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) GUARD: NO AGENT-REACHABLE ASSEMBLY OR SOURCE NAMES THE LINE. No type of <c>TradeAgent.Gateway</c>, <c>TradeAgent.AgentRuntime</c>
    /// or <c>TradeAgent.TradeCli</c> holds, takes or returns a <c>TradeAgent.Core.Oversight</c> type; no source of those three or of
    /// <c>TradeAgent.GatewayHost</c> names the namespace, the file or a table of it; and the canon and the role objectives — what an agent
    /// is told — name neither "watcher" nor "oversight". Pins an absence: watched red with a planted field.
    /// </summary>
    [Fact]
    public void No_agent_reachable_assembly_or_source_names_the_line()
    {
        const string ns = "TradeAgent.Core.Oversight";
        static bool Of(Type t) =>
            (t.Namespace == ns) || (t.HasElementType && Of(t.GetElementType()!)) || (t.IsGenericType && t.GetGenericArguments().Any(Of));
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Assembly[] reachable = [typeof(GatewayPipeServer).Assembly, typeof(BrokerDoor).Assembly, typeof(TradeAgent.TradeCli.PipeClient).Assembly];
        var holders = reachable.SelectMany(a => a.GetTypes()).SelectMany(t =>
                t.GetFields(all).Where(f => Of(f.FieldType)).Select(f => $"{t.FullName}.{f.Name}")
                    .Concat(t.GetProperties(all).Where(p => Of(p.PropertyType)).Select(p => $"{t.FullName}.{p.Name}"))
                    .Concat(t.GetMethods(all).Where(m => Of(m.ReturnType) || m.GetParameters().Any(p => Of(p.ParameterType)))
                        .Select(m => $"{t.FullName}.{m.Name}()")))
            .ToList();
        Assert.True(holders.Count == 0, "an agent-reachable assembly holds the owner's line: " + string.Join(", ", holders));

        var root = RepoRoot();
        // AND THE OWNER'S PAGE (U-org-watcher-b): its words, its controls and their sources — the App's alone.
        string[] named = [ns, OversightStore.FileName, "oversight_", "OversightPage", "OversightWords", "OversightSources", "OversightRead",
            "OversightCap", "SaveOversightCap", "RaiseOversightCapArmed", "OversightBanner"];
        var sources = new[] { "TradeAgent.Gateway", "TradeAgent.AgentRuntime", "TradeAgent.TradeCli", "TradeAgent.GatewayHost" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sources);
        var naming = sources.Where(f => named.Any(n => File.ReadAllText(f).Contains(n, StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(naming.Count == 0, "an agent-reachable source names the owner's line: " + string.Join(", ", naming));

        foreach (var told in new[] { "Canon.cs", "RoleObjectives.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, "src", "TradeAgent.AgentRuntime", told));
            foreach (var word in new[] { "watcher", "oversight" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- (d) untouched -------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) GUARD: A SWEEP LEAVES THE ORGANISATION'S DATABASE AND LOGS UNCHANGED. Every table of the app's database, every row, before and after
    /// a sweep that fires every detector and writes a digest; and the <c>logs/</c> folder, file by file. The unit's watched mutant — the
    /// sweep also writing a severe finding's sentence through the activity log — turns this red.
    /// </summary>
    [Fact]
    public void A_sweep_leaves_the_organisations_database_and_logs_unchanged()
    {
        string file;
        using (var db = TestEnv.NewDb()) file = db.FilePath;
        var now = OversightSeed.Now();
        foreach (var d in OversightSeed.Detectors) OversightSeed.Seed(file, d, now);
        Directory.CreateDirectory(Paths.Logs);

        var beforeRows = Dump(file);
        var beforeLogs = LogFiles();
        using var store = new OversightStore(Path.Combine(TestEnv.Home, $"oversight-{Guid.NewGuid():n}.db"), now);
        var swept = new OversightSweep(file, store).Run(now);
        Assert.Null(swept.Failure);
        Assert.Contains(swept.Added, f => f.Severity == OversightSeverity.Severe);
        Assert.NotEmpty(swept.Digests);

        var afterRows = Dump(file);
        Assert.Equal(beforeRows.Keys, afterRows.Keys);
        foreach (var table in beforeRows.Keys)
            Assert.True(beforeRows[table] == afterRows[table], $"the sweep changed the organisation's table {table}");
        Assert.Equal(beforeLogs, LogFiles());
    }

    /// <summary>Every table's rows, in a fixed order, as text.</summary>
    static SortedDictionary<string, string> Dump(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var t = c.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            using var r = t.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        var dump = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT * FROM \"{table}\"";
            var rows = new List<string>();
            using var r = q.ExecuteReader();
            while (r.Read())
                rows.Add(string.Join("\u001f", Enumerable.Range(0, r.FieldCount).Select(i =>
                    r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
            rows.Sort(StringComparer.Ordinal);
            dump[table] = string.Join("\n", rows);
        }
        return dump;
    }

    static IReadOnlyList<string> LogFiles() =>
        [.. Directory.GetFiles(Paths.Logs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(Paths.Logs, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}")];

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }
}
