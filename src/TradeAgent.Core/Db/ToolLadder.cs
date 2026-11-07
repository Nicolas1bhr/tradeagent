using System.Globalization;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Db;

/// <summary>The rungs a tool stands on (<c>U-admission-ladder</c>), by their one spelling.</summary>
public static class ToolRung
{
    /// <summary>An agent's own tool in its own folder: free, never named by the app, never offered. No row is ever written for it.</summary>
    public const string Scratch = "scratch";

    /// <summary>Named, hashed and copied by the app, its author position and attempt recorded; offered to no one.</summary>
    public const string Registered = "registered";

    /// <summary>Its latest conformance measurement, run by the app in a cell on its registered copy, passed.</summary>
    public const string ConformanceTested = "conformance-tested";

    /// <summary>Other seats in a scope run it, read-only, from its published hashed copy.</summary>
    public const string Offered = "offered";

    /// <summary>The owner's head-start kit: offered by his own install (<see cref="Kit"/>), with no ladder step.</summary>
    public const string OwnersKit = "offered by the owner's install";
}

/// <summary>The kinds of step on the ladder's own record (<c>tool_ladder_event</c>).</summary>
public static class ToolLadderEventKind
{
    public const string Registered = "registered", ConformanceTested = "conformance-tested", Offered = "offered", Demoted = "demoted",
        Superseded = "superseded";

    public static readonly string[] All = [Registered, ConformanceTested, Offered, Demoted, Superseded];
}

/// <summary>One registered tool as <c>tool_admission</c> holds it, with its rung as its record derives it.</summary>
public sealed record ToolRow(string Id, string Name, string Hash, string Author, string Attempt, bool FeedsEvidence, string Description,
    DateTimeOffset At, string Rung, string? Scope, long Reuse);

/// <summary>One step on the ladder's record.</summary>
public sealed record ToolLadderEvent(long Id, string Tool, string Kind, string Actor, string? Decision, string? Scope, long? Conformance,
    string Detail, DateTimeOffset At);

/// <summary>One conformance measurement, as the app recorded it.</summary>
public sealed record ToolConformanceRow(long Id, string Tool, string Hash, bool Passed, int Vectors, int Runs, string Detail, string? Policy,
    DateTimeOffset At);

/// <summary>A tool offered to a seat: where its published copy lies and what runs, as the grant reads it now.</summary>
public sealed record OfferedTool(string Id, string Name, string Dir, string Program, string Hash, string Description, bool FeedsEvidence,
    string Author, string Scope);

/// <summary>
/// THE ADMISSION LADDER (<c>U-admission-ladder</c>; VISION § 6.6 and § 9 "Tools"; EDGE-FACTORY § 4.7): how a tool an agent built moves from
/// its own scratch to being run by other seats. <b>Scratch stays free</b> (PRINCIPLES, "Autonomy is the default inside authority"): nothing
/// here reads, limits or records an agent's own folder until the agent itself registers a tool from it. Then:
/// <list type="number">
/// <item><b>registered</b> (<see cref="Register"/>) — the author's folder copied by the app into <c>ladder/&lt;id&gt;</c> under the app's home,
/// checked, made read-only and hashed; the author position and the attempt recorded (<see cref="ArtifactKind.Tool"/>, rung
/// <see cref="Versions.ToolLadderRung"/>'s lineage trigger). Offered to nobody: no seat is told of it and no cell is granted it.</item>
/// <item><b>conformance-tested</b> (<see cref="Test"/>) — the app runs the tool's declared vectors (<see cref="ToolConformance"/>) in a cell on
/// the registered copy and records the result as a measurement (<c>tool_conformance</c>, written here and nowhere else); a pass moves it
/// up, a failure moves a tool above registered back down.</item>
/// <item><b>offered</b> (<see cref="Offer"/>) — a recorded <c>tool-offer</c> decision of the HEAD of a unit whose subtree holds the author,
/// to that subtree. A tool that FEEDS EVIDENCE is offered only on a passing latest measurement of its registered copy — refused here in
/// words, and by the database's trigger whatever writer tried.</item>
/// </list>
/// <para><b>Nothing an agent writes reaches another seat except through "offered".</b> <see cref="OfferedTo"/> is the one read a canon and a
/// cell grant are made from: offered tools whose scope holds the seat's unit and whose published copy still hashes to its registration —
/// a copy changed after it is granted to nobody, and <see cref="Recheck"/> demotes it on the record. The owner's kit enters at offered by
/// his own install (<see cref="Kit"/>), never by a step here.</para>
/// <para><b>Reuse is counted per tool</b> (<see cref="NoteUse"/>): a launch of a position other than the author's whose runtime reported
/// running it — the input of the future contribution ledger, a count and never a claim of value.</para>
/// </summary>
public sealed class ToolLadder(Database db, string? home = null, string? workspace = null)
{
    /// <summary>The published copies' folder under the app's home: never inside a workspace, written only by the app.</summary>
    public static string Dir(string? home = null) => Path.Combine(home ?? Paths.Home, "ladder");

    /// <summary>Where a registration is staged, beside <see cref="Dir"/>, never inside it.</summary>
    public static string StagingDir(string? home = null) => Path.Combine(home ?? Paths.Home, "ladder-staging");

    /// <summary>The app principal a step the app takes on its own is recorded under (a measurement's move, a demotion, a supersession).</summary>
    public const string App = "app";

    /// <summary>The most files one tool's folder may hold.</summary>
    public const int MaxFiles = 500;

    /// <summary>The most bytes one tool's folder may hold.</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>The longest description a tool may carry: its author's claim, printed in a canon.</summary>
    public const int MaxDescription = 300;

    /// <summary>The most offered tools one seat is granted; past it the newest offers stand and the rest are said, not granted.</summary>
    public const int MaxGranted = 24;

    static readonly Regex NameRule = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    string LadderDir => Dir(home);

    /// <summary>A tool's published copy.</summary>
    public string CopyOf(string id) => Path.Combine(LadderDir, id);

    // ---- reads ----------------------------------------------------------------------------------------------------------------

    const string ToolCols = "id, name, hash, author, attempt, feeds_evidence, description, at";

    /// <summary>One registered tool with its rung, or null.</summary>
    public ToolRow? Tool(string id) => db.Read(_ => Tools("WHERE t.id = $id", ("$id", id)).FirstOrDefault());

    /// <summary>The tools <paramref name="author"/> registered, newest first.</summary>
    public IReadOnlyList<ToolRow> RegisteredBy(string author) => db.Read(_ => Tools("WHERE t.author = $a", ("$a", author)));

    /// <summary>Every registered tool, newest first.</summary>
    public IReadOnlyList<ToolRow> All() => db.Read(_ => Tools(""));

    List<ToolRow> Tools(string where, params (string, object?)[] ps)
    {
        using var c = db.Cmd($"""
            SELECT t.id, t.name, t.hash, t.author, t.attempt, t.feeds_evidence, t.description, t.at,
                   (SELECT e.kind FROM tool_ladder_event e WHERE e.tool = t.id ORDER BY e.id DESC LIMIT 1),
                   (SELECT e.scope FROM tool_ladder_event e WHERE e.tool = t.id ORDER BY e.id DESC LIMIT 1),
                   (SELECT COUNT(*) FROM tool_reuse u WHERE u.tool = t.id)
              FROM tool_admission t {where} ORDER BY t.at DESC, t.id DESC
            """, ps);
        using var r = c.ExecuteReader();
        var rows = new List<ToolRow>();
        while (r.Read())
        {
            var last = r.IsDBNull(8) ? ToolLadderEventKind.Registered : r.GetString(8);
            var rung = RungAfter(last);
            rows.Add(new ToolRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) == 1,
                r.GetString(6), Sql.Time(r.GetString(7)), rung, rung == ToolRung.Offered && !r.IsDBNull(9) ? r.GetString(9) : null, r.GetInt64(10)));
        }
        return rows;
    }

    /// <summary>The rung a tool stands on after the last step its record holds.</summary>
    public static string RungAfter(string lastEvent) => lastEvent switch
    {
        ToolLadderEventKind.ConformanceTested => ToolRung.ConformanceTested,
        ToolLadderEventKind.Offered => ToolRung.Offered,
        _ => ToolRung.Registered
    };

    /// <summary>A tool's steps, oldest first.</summary>
    public IReadOnlyList<ToolLadderEvent> Events(string tool) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, tool, kind, actor, decision, scope, conformance, detail, at FROM tool_ladder_event WHERE tool = $t ORDER BY id",
            ("$t", tool));
        using var r = c.ExecuteReader();
        var rows = new List<ToolLadderEvent>();
        while (r.Read())
            rows.Add(new ToolLadderEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Sql.S(r.GetValue(4)), Sql.S(r.GetValue(5)),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), Sql.Time(r.GetString(8))));
        return rows;
    });

    /// <summary>A tool's conformance measurements, oldest first.</summary>
    public IReadOnlyList<ToolConformanceRow> Measurements(string tool) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT id, tool, hash, passed, vectors, runs, detail, policy, at FROM tool_conformance WHERE tool = $t ORDER BY id",
            ("$t", tool));
        using var r = c.ExecuteReader();
        var rows = new List<ToolConformanceRow>();
        while (r.Read())
            rows.Add(new ToolConformanceRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, r.GetInt32(4), r.GetInt32(5),
                r.GetString(6), Sql.S(r.GetValue(7)), Sql.Time(r.GetString(8))));
        return rows;
    });

    /// <summary>
    /// WHAT IS OFFERED TO <paramref name="position"/>, AS A GRANT MAY READ IT NOW — the one read every canon and every cell grant is made
    /// from: tools whose last step is <c>offered</c>, whose scope unit is the position's own unit or one above it, and whose published copy
    /// is there and hashes to its registration (every byte read: a copy changed after it is granted to nobody). Newest offer first, at most
    /// <see cref="MaxGranted"/>. Empty for a position the chart does not hold. Never throws for a read that fails: nothing is granted.
    /// </summary>
    public IReadOnlyList<OfferedTool> OfferedTo(string position)
    {
        try
        {
            return db.Read(_ =>
            {
                var org = new OrgStore(db);
                if (org.UnitOf(position) is not { } unit) return (IReadOnlyList<OfferedTool>)[];
                var line = new HashSet<string>(org.Ancestors(unit.Id).Select(u => u.Id).Prepend(unit.Id), StringComparer.Ordinal);
                using var c = db.Cmd("""
                    SELECT t.id, t.name, t.hash, t.description, t.feeds_evidence, t.author, e.scope
                      FROM tool_admission t
                      JOIN tool_ladder_event e ON e.id = (SELECT MAX(id) FROM tool_ladder_event WHERE tool = t.id)
                     WHERE e.kind = 'offered'
                     ORDER BY e.id DESC
                    """);
                using var r = c.ExecuteReader();
                var offered = new List<OfferedTool>();
                while (r.Read() && offered.Count < MaxGranted)
                {
                    var scope = r.GetString(6);
                    if (!line.Contains(scope)) continue;
                    var id = r.GetString(0);
                    var name = r.GetString(1);
                    var dir = CopyOf(id);
                    if (!Hashes(dir, r.GetString(2))) continue;
                    offered.Add(new OfferedTool(id, name, dir, Path.Combine(dir, name), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1,
                        r.GetString(5), scope));
                }
                return (IReadOnlyList<OfferedTool>)offered;
            });
        }
        catch (Exception ex) when (ex is TradeAgentException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    static bool Hashes(string dir, string hash)
    {
        try { return Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is null && Kit.HashOf(dir) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- register ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// REGISTERS A TOOL FROM ITS AUTHOR'S OWN FOLDER — the author's act, and the first time the app looks at it. <paramref name="folder"/>
    /// is a folder inside <paramref name="author"/>'s own home, reached through no link, holding a program named <paramref name="name"/> at
    /// its top (run by that name) and, for a tool that <paramref name="feedsEvidence"/>, its vectors (<see cref="ToolConformance.FileName"/>).
    /// The folder is copied, checked (no link, device, hidden entry or set-id file; at most <see cref="MaxFiles"/> files and
    /// <see cref="MaxBytes"/>), made read-only, hashed and published under <c>ladder/&lt;id&gt;</c>; then the row and its
    /// <c>registered</c> step are written in one write. The name belongs to its first author, and is never a name the owner's kit holds.
    /// Refused in words, nothing kept.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written or kept.</exception>
    public ToolRow Register(string author, string? attempt, string name, string folder, string description, bool feedsEvidence, DateTimeOffset now)
    {
        ArtifactLineage.Require(ArtifactKind.Tool, author, attempt);
        var org = new OrgStore(db);
        if (OrgStore.IsOffChart(author) || org.Position(author) is not { Status: OrgPositionStatus.Active })
            throw Refused($"'{author}' is no active position: a tool is registered by the position whose launch built it.");
        if (!NameRule.IsMatch(name))
            throw Refused($"'{name}' is not a tool name: lower-case letters, digits, '.', '_' and '-', at most 64, starting with a letter or digit.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > MaxDescription || description.Any(char.IsControl))
            throw Refused($"a tool's description is your words on one line, at most {MaxDescription} characters, never empty.");
        if (Kit.Granted(home) is { } kit && kit.Index.Tools.Concat(kit.Index.Skills).Any(e => e.Name == name))
            throw Refused($"'{name}' is a name the owner's head-start kit holds: a registered tool takes a name of its own.");
        if (db.Read(_ => { using var c = db.Cmd("SELECT author FROM tool_admission WHERE name = $n LIMIT 1", ("$n", name)); return c.ExecuteScalar() as string; })
            is { } owner && owner != author)
            throw Refused($"the tool name '{name}' is {owner}'s: a name belongs to the position that registered it first.");

        var source = AuthorFolder(author, folder);
        var staging = Path.Combine(StagingDir(home), Guid.NewGuid().ToString("n"));
        string? published = null;
        try
        {
            Directory.CreateDirectory(staging);
            if (CopyTree(source, staging) is { } bad) throw Refused($"'{folder}' cannot be registered: {bad}.");
            var program = Path.Combine(staging, name);
            if (!File.Exists(program) || new FileInfo(program).LinkTarget is not null)
                throw Refused($"'{folder}' holds no program named '{name}' at its top: a tool is run by its name.");
            var vectors = Path.Combine(staging, ToolConformance.FileName);
            if (File.Exists(vectors))
            {
                if (ToolConformance.Read(File.ReadAllBytes(vectors)).Problem is { } problem)
                    throw Refused($"its {ToolConformance.FileName} cannot be read: {problem}.");
            }
            else if (feedsEvidence)
                throw Refused($"a tool that feeds evidence declares its conformance vectors in {ToolConformance.FileName} at its top: "
                              + "the app measures it on them before it can be offered.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
            SealReadOnly(staging, program);
            var hash = Kit.HashOf(staging);
            var id = "tool-" + hash[..16];
            if (Tool(id) is not null) throw Refused($"exactly this tool is registered already, as {id}.");

            published = CopyOf(id);
            Directory.CreateDirectory(LadderDir);
            if (Directory.Exists(published)) DeleteTree(published);
            MoveDir(staging, published);
            if (Kit.HashOf(published) != hash) throw Refused("its copy changed while it was published; nothing was registered.");

            db.Write(_ =>
            {
                using (var c = db.Cmd($"INSERT INTO tool_admission({ToolCols}) VALUES($id,$n,$h,$a,$x,$e,$d,$at)",
                    ("$id", id), ("$n", name), ("$h", hash), ("$a", author), ("$x", attempt), ("$e", feedsEvidence ? 1 : 0), ("$d", description),
                    ("$at", Sql.T(now))))
                    c.ExecuteNonQuery();
                Step(id, ToolLadderEventKind.Registered, author, null, null, null,
                    $"registered by {author} from its own folder; hash {hash}; feeds evidence: {(feedsEvidence ? "yes" : "no")}", now);
                return 0;
            });
            published = null;
            return Tool(id)!;
        }
        finally
        {
            DeleteTree(staging);
            if (published is not null) DeleteTree(published);
        }
    }

    /// <summary>The author's folder, inside its own home, reached through no link — or refused.</summary>
    string AuthorFolder(string author, string folder)
    {
        var authorHome = Path.GetFullPath(Path.Combine(workspace ?? Paths.Workspace, CouncilRoles.HomeDir(author)));
        if (string.IsNullOrWhiteSpace(folder) || Path.IsPathRooted(folder) || folder.Contains('\0'))
            throw Refused($"'{folder}' is not a folder inside your own home: name it relative to your home.");
        var source = Path.GetFullPath(Path.Combine(authorHome, folder));
        var rel = Path.GetRelativePath(authorHome, source);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            throw Refused($"'{folder}' is not a folder inside your own home: a tool is registered from its author's own folder and nowhere else.");
        var walk = authorHome;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            walk = Path.Combine(walk, part);
            if (new DirectoryInfo(walk).LinkTarget is not null)
                throw Refused($"'{folder}' is reached through a link: a tool is registered from a real folder of its author's own.");
        }
        if (!Directory.Exists(source)) throw Refused($"'{folder}' is no folder in your home.");
        return source;
    }

    // ---- conformance ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// MEASURES A TOOL ON ITS DECLARED VECTORS, IN A CELL (<see cref="ToolConformance.Measure"/> through <paramref name="runner"/>), on its
    /// registered copy — asked by its author or by a head whose subtree holds the author. The result is the app's measurement, recorded
    /// whatever it says; a pass moves a registered tool to conformance-tested, a failure moves a tool above registered back down. A copy
    /// that no longer hashes to its registration is demoted and not run. No cell on this computer: refused, nothing measured.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was measured.</exception>
    public ToolConformanceRow Test(string tool, string caller, IToolCellRunner? runner, DateTimeOffset now)
    {
        var t = Tool(tool) ?? throw Refused($"'{tool}' is no registered tool.");
        if (caller != t.Author && HeadOver(caller, t.Author) is null)
            throw Forbidden($"'{tool}' is {t.Author}'s: its conformance is asked for by its author or by a head whose subtree holds it.");
        if (runner is null || runner.Unavailable is { } none)
            throw Refused($"this computer cannot run a tool in a cell ({runner?.Unavailable ?? "no cell runner"}), and a tool is measured only in one.");
        var dir = CopyOf(t.Id);
        if (!Hashes(dir, t.Hash))
        {
            Demote(t, "its published copy no longer hashes to its registration", now);
            throw Refused($"'{tool}''s published copy no longer hashes to its registration: it was demoted and not run. Register it again.");
        }
        var vectorsFile = Path.Combine(dir, ToolConformance.FileName);
        if (!File.Exists(vectorsFile))
            throw Refused($"'{tool}' declares no conformance vectors ({ToolConformance.FileName}), so there is nothing to measure.");
        var (vectors, problem) = ToolConformance.Read(File.ReadAllBytes(vectorsFile));
        if (vectors is null) throw Refused($"its {ToolConformance.FileName} cannot be read: {problem}.");

        var result = ToolConformance.Measure(vectors, input => runner.Run(dir, t.Name, input));
        return Record(t, result, now);
    }

    /// <summary>
    /// RECORDS ONE MEASUREMENT AND THE STEP IT MOVES, in one write — the only writer of <c>tool_conformance</c>, and only the app's
    /// <see cref="Test"/> reaches it. Internal for the tests that stand in for a cell.
    /// </summary>
    internal ToolConformanceRow Record(ToolRow t, ConformanceResult result, DateTimeOffset now) => db.Write(_ =>
    {
        long id;
        using (var c = db.Cmd("""
            INSERT INTO tool_conformance(tool, hash, passed, vectors, runs, detail, policy, at) VALUES($t,$h,$p,$v,$r,$d,$pol,$at);
            SELECT last_insert_rowid();
            """, ("$t", t.Id), ("$h", t.Hash), ("$p", result.Passed ? 1 : 0), ("$v", result.Vectors), ("$r", result.Runs), ("$d", result.Detail),
            ("$pol", result.PolicyHash), ("$at", Sql.T(now))))
            id = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        var rung = Tool(t.Id)!.Rung;
        if (result.Passed && rung == ToolRung.Registered)
            Step(t.Id, ToolLadderEventKind.ConformanceTested, App, null, null, id, $"conformance measurement {id} passed on hash {t.Hash}", now);
        else if (!result.Passed && rung != ToolRung.Registered)
            Step(t.Id, ToolLadderEventKind.Demoted, App, null, null, id, $"conformance measurement {id} failed on hash {t.Hash}", now);
        return Measurements(t.Id).Single(m => m.Id == id);
    });

    // ---- offer ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// OFFERS A TOOL TO A SUBTREE — the recorded <c>tool-offer</c> decision of <paramref name="decider"/>, the head of the unit it is offered
    /// to, whose subtree holds the tool's author (a head may offer its own). The decision, its rationale (the decider's claim) and the
    /// <c>offered</c> step are one write; an offered tool of the same name (an older version) is superseded in it. Refused in words:
    /// a decider that heads no unit, or none whose subtree holds the author; a tool offered already; a copy that no longer hashes to its
    /// registration; a tool that FEEDS EVIDENCE without a passing latest conformance measurement on its registered copy — the database's
    /// trigger refuses that last one whatever writer tried.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ToolRow Offer(string decider, string? attempt, string tool, string rationale, IReadOnlyList<string> options, DateTimeOffset now) => db.Write(_ =>
    {
        var org = new OrgStore(db);
        if (OrgStore.IsOffChart(decider) || org.Position(decider) is not { Status: OrgPositionStatus.Active })
            throw Forbidden($"'{decider}' is no active position: a tool is offered by the head whose launch decides it.");
        var t = Tool(tool) ?? throw Refused($"'{tool}' is no registered tool.");
        var scope = HeadOver(decider, t.Author)
                    ?? throw Forbidden($"'{decider}' heads no unit whose subtree holds {t.Author}, the author of '{tool}': a tool is offered by "
                                       + "the head of a unit its author works in or under, to that unit's subtree.");
        if (t.Rung == ToolRung.Offered) throw Refused($"'{tool}' is offered already, to {t.Scope}.");
        if (!Hashes(CopyOf(t.Id), t.Hash))
        {
            Demote(t, "its published copy no longer hashes to its registration", now);
            throw Refused($"'{tool}''s published copy no longer hashes to its registration: it was demoted. Its author registers it again.");
        }
        if (t.FeedsEvidence && !(Measurements(t.Id).LastOrDefault() is { Passed: true } last && last.Hash == t.Hash))
            throw Refused($"'{tool}' feeds evidence, and a tool that feeds evidence is offered only after its latest conformance measurement, run by "
                          + "the app in a cell on its registered copy, passed. Ask for one with 'tool test'.");

        var choice = $"offer {t.Name} ({t.Id}) to {scope} and every unit below it";
        var decision = org.WriteToolDecision(decider, attempt, options.Count > 0 ? options : [choice], choice, rationale, now);
        foreach (var older in Tools("WHERE t.name = $n AND t.id <> $id", ("$n", t.Name), ("$id", t.Id)).Where(o => o.Rung == ToolRung.Offered))
            Step(older.Id, ToolLadderEventKind.Superseded, App, null, null, null, $"superseded by {t.Id}, offered by {decider} in {decision.Id}", now);
        Step(t.Id, ToolLadderEventKind.Offered, decider, decision.Id, scope, null,
            $"offered by {decider} to {scope} in {decision.Id}; hash {t.Hash}", now);
        return Tool(t.Id)!;
    });

    /// <summary>
    /// THE UNIT <paramref name="head"/> HEADS WHOSE SUBTREE HOLDS <paramref name="author"/>'s unit, or null: the head of the author's own
    /// unit or of any unit above it. An ended author's tool is judged by the unit it was in.
    /// </summary>
    string? HeadOver(string head, string author)
    {
        var org = new OrgStore(db);
        if (org.Position(head) is not { Status: OrgPositionStatus.Active }) return null;
        var authorUnit = org.Position(author)?.UnitId;
        if (authorUnit is null) return null;
        var units = org.Units().ToDictionary(u => u.Id, StringComparer.Ordinal);
        if (!units.TryGetValue(authorUnit, out var unit)) return null;
        foreach (var u in org.Ancestors(unit.Id).Prepend(unit))
            if (u.HeadPositionId == head && u.Status != OrgUnitStatus.Closed) return u.Id;
        return null;
    }

    // ---- recheck and demotion ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE RE-CHECK EVERY GRANT IS PRECEDED BY: each tool above registered whose published copy is gone or no longer hashes to its
    /// registration is demoted on the record. Answers the tools demoted. Never throws.
    /// </summary>
    public IReadOnlyList<string> Recheck(DateTimeOffset now)
    {
        var demoted = new List<string>();
        try
        {
            foreach (var t in All().Where(t => t.Rung != ToolRung.Registered))
                if (!Hashes(CopyOf(t.Id), t.Hash))
                {
                    Demote(t, "its published copy is gone or no longer hashes to its registration", now);
                    demoted.Add(t.Id);
                }
        }
        catch (Exception ex) when (ex is TradeAgentException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A re-check that could not finish demotes what it reached; OfferedTo still grants no copy that fails its hash.
        }
        return demoted;
    }

    void Demote(ToolRow t, string why, DateTimeOffset now)
    {
        if (t.Rung == ToolRung.Registered) return;
        db.Write(_ => { Step(t.Id, ToolLadderEventKind.Demoted, App, null, null, null, why, now); return 0; });
    }

    // ---- reuse ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// COUNTS ONE USE OF AN OFFERED TOOL: a launch (<paramref name="attempt"/>) of <paramref name="position"/>, not its author, whose runtime
    /// reported running it. Once per launch per tool; the author's own use is not reuse. Never throws: a count that could not be written
    /// is a gap, never a broken turn.
    /// </summary>
    public bool NoteUse(string tool, string position, string attempt, DateTimeOffset now)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(attempt)) return false;
            return db.Write(_ =>
            {
                using var c = db.Cmd("""
                    INSERT INTO tool_reuse(tool, position, attempt, at)
                    SELECT $t, $p, $x, $at FROM tool_admission WHERE id = $t AND author <> $p
                    ON CONFLICT(tool, attempt) DO NOTHING
                    """, ("$t", tool), ("$p", position), ("$x", attempt), ("$at", Sql.T(now)));
                return c.ExecuteNonQuery() == 1;
            });
        }
        catch (Exception ex) when (ex is TradeAgentException or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    // ---- the record ---------------------------------------------------------------------------------------------------------

    void Step(string tool, string kind, string actor, string? decision, string? scope, long? conformance, string detail, DateTimeOffset now)
    {
        using var c = db.Cmd("""
            INSERT INTO tool_ladder_event(tool, kind, actor, decision, scope, conformance, detail, at)
            VALUES($t,$k,$a,$d,$s,$c,$detail,$at)
            """, ("$t", tool), ("$k", kind), ("$a", actor), ("$d", decision), ("$s", scope), ("$c", conformance), ("$detail", detail), ("$at", Sql.T(now)));
        c.ExecuteNonQuery();
    }

    static TradeAgentException Refused(string why) => new(ErrorCode.INVALID_REQUEST, why + " Nothing was written.");

    static TradeAgentException Forbidden(string why) => RefusalMark.Mark(Refused(why));

    // ---- the published copy -----------------------------------------------------------------------------------------------------

    static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Copies a tool's folder, refusing a link, device, hidden or ill-named entry, a set-id file, or the bounds broken.</summary>
    static string? CopyTree(string source, string into)
    {
        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*",
                     new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            var rel = Rel(source, path);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return $"'{rel}' is a link: a tool holds only real files and folders";
            var leaf = Path.GetFileName(path);
            if (leaf.StartsWith('.') || leaf.Any(char.IsControl) || leaf.Length > 128 || leaf.Contains('\\') || leaf.Contains(':'))
                return $"'{rel}' is hidden or has a name a tool may not hold";
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(Path.Combine(into, rel));
                continue;
            }
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0)
                return $"'{rel}' is set-user or set-group: a tool may not hold one";
            if (!File.Exists(path)) return $"'{rel}' is not a regular file";
            if (++files > MaxFiles) return $"it holds more than {MaxFiles} files";
            bytes += info.Length;
            if (bytes > MaxBytes) return $"it is larger than {MaxBytes / (1024 * 1024)} MiB";
            var to = Path.Combine(into, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(path, to);
            if (new FileInfo(to).LinkTarget is not null) return $"'{rel}' became a link while it was copied";
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(to, File.GetUnixFileMode(to) | UnixFileMode.UserExecute);
        }
        return null;
    }

    const UnixFileMode ReadRun = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    const UnixFileMode ReadOnly = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Every folder and file read-only for everyone, owner included; the program and any file its author marked runnable read and run.</summary>
    static void SealReadOnly(string root, string program)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
            return;
        }
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(f, f == program || (File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0 ? ReadRun : ReadOnly);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            File.SetUnixFileMode(d, ReadRun);
        File.SetUnixFileMode(root, ReadRun);
    }

    /// <summary>Moves a sealed folder: Linux lets a folder change parent only with write on it, so it is writable for the move alone.</summary>
    static void MoveDir(string from, string to)
    {
        if (OperatingSystem.IsWindows()) { Directory.Move(from, to); return; }
        var mode = File.GetUnixFileMode(from);
        File.SetUnixFileMode(from, mode | UnixFileMode.UserWrite);
        Directory.Move(from, to);
        File.SetUnixFileMode(to, mode);
    }

    /// <summary>Deletes a tree the ladder sealed: writable again first. Never throws.</summary>
    static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            if (!OperatingSystem.IsWindows())
                foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Prepend(path))
                    File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* litter beside the ladder, never granted */ }
    }
}
