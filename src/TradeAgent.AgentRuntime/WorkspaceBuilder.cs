using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// The world the agent is told about, as it is at the moment its instruction file is written.
///
/// <see cref="ConnectorIsBuiltInSimulator"/> is NOT <see cref="ConnectorIsPaper"/> narrowed. A broker's
/// own demo account is paper and quotes the real market; TradeAgent's built-in simulator is paper and
/// quotes four fixed numbers. Told apart because the agent's answer to "is a result from this platform
/// worth anything?" differs completely between them, and the default is false — a platform this build
/// does not recognise is not described as a fixture.
///
/// <para><see cref="TurnsPerSession"/> is the owner's setting for how many turns a CLI session carries before a fresh
/// one starts (<c>TradeAgentSettings.MissionTurnsPerSession</c>), or null for the loop's own default: the canon's reach
/// says it, so it is what the loop runs on.</para>
/// </summary>
public sealed record WorkspaceContext(string ConnectorName, bool ConnectorIsPaper, string? AccountId,
    TradingMode Mode, bool ExecutionAvailable, string? ExecutionBlockedReason, RiskPolicy Risk,
    bool ConnectorIsBuiltInSimulator = false, string Role = CouncilRoles.Operations, int? TurnsPerSession = null,
    string? Kind = null, string? Place = null);

/// <summary>
/// What the owner's repair of a home did (<see cref="WorkspaceBuilder.RepairHome"/>): whether the home was rebuilt, the words, and where the
/// old folder is kept — set whenever it was moved, rebuilt or not.
/// </summary>
public sealed record HomeRepair(bool Repaired, string Words, string? Aside);

/// <summary>
/// Creates and maintains the agent's home. The agent is broadly free inside that directory — shell,
/// subprocesses, packages, internet, its own code — and has no authority outside it. The instruction
/// file below is regenerated on every start so it can never describe a stale world.
///
/// <b>The agent's home is <c>workspace/agent</c>, beside <c>workspace/inbox</c> rather than around
/// it.</b> It used to be the workspace itself, which made the owner's drop folder a subdirectory of
/// the agent's working directory; a file the agent wrote one relative path away was then recorded
/// as material the OWNER had handed over (REVIEW 2026-09-05b finding 5). Unchanged for the owner:
/// the drop folder is in the same place it has always been, and the Inbox page still opens it.
/// </summary>
public static class WorkspaceBuilder
{
    /// <summary>The agent's own directories, inside <see cref="Paths.AgentHome"/>.</summary>
    public static readonly string[] SubDirs =
        ["trading", "research", "strategies", "data", "scripts", "logs", "scratch"];

    /// <summary>
    /// WHAT THE APP DELIVERS INTO. Written only by TradeAgent — a report published to this role, a
    /// brief sent down to it — and read by the agent. It is inside the role's own folder rather
    /// than in a shared place precisely so the two roles' deliveries cannot be confused.
    /// </summary>
    public const string InDir = CouncilRoles.InDir;

    /// <summary>
    /// WHAT THE ROLE HANDS BACK, and the only path out of a role's folder. The agent writes a file
    /// here; the app validates it, publishes it and delivers it. The agent never writes into
    /// another role's folder and has no command that would.
    /// </summary>
    public const string OutDir = CouncilRoles.OutDir;

    /// <summary>Where one role's home is, given the recorded tree. See <see cref="CouncilRoles.HomeDir"/>.</summary>
    public static string HomeOf(string root, string role) => Path.Combine(root, CouncilRoles.HomeDir(role));

    /// <summary>
    /// Builds the tree for ONE ROLE and returns that role's directory — the one handed to the
    /// runtime as its working directory. <paramref name="root"/> is the recorded tree
    /// (<see cref="Paths.Workspace"/>), which holds every role's home and the owner's inbox side by
    /// side.
    ///
    /// The role comes off <see cref="WorkspaceContext.Role"/> rather than being a parameter of its
    /// own, because the mission this writes is a function of the WHOLE context — the role decides
    /// which section it gets, and the rest of the context decides what that section can truthfully
    /// say about the platform, the account and the limits.
    ///
    /// <paramref name="appFiles"/> is the app's record of what IT wrote into the home
    /// (<see cref="AppFileManifest"/>), and it is written here rather than inferred later: what the
    /// material ledger can measure as the app's own file is exactly what this pass wrote down at the
    /// moment it wrote the bytes. Tests pass their own so that one assembly's runs cannot record
    /// over each other's.
    /// </summary>
    public static string Build(WorkspaceContext ctx, string? root = null, AppFileManifest? appFiles = null)
    {
        var files = appFiles ?? AppFileManifest.Shared;
        var ws = root ?? Paths.Workspace;
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(ws, MaterialScanner.InboxDir));

        var home = HomeOf(ws, ctx.Role);
        // NOTHING OUTSIDE THIS ROLE'S HOME IS MOVED OR DELETED HERE. The chair's start once carried an
        // install from before the agent's home moved by moving every `workspace/<dir>` into
        // `workspace/agent/<dir>` — and `workspace/research` is the Research Director's own home, so
        // the first start whose chair folder had no `research/` moved that role's programs into the
        // chair's (U-friction-home). An earlier layout is owed nothing while we build (CLAUDE.md).
        Directory.CreateDirectory(home);
        // EVERYTHING BELOW THE HOME IS WRITTEN WITHOUT FOLLOWING A LINK (fix pass 3, the re-review's P1-b): an executor's cell writes its
        // whole home, and this start runs unconfined, so a name the cell made a link — a file or a folder — is replaced by the app's own
        // (the link itself unlinked, never what it points at) and nothing is ever written where it pointed (HomeFiles).
        foreach (var d in SubDirs) HomeFiles.CreateDirectory(home, d, replaceLinks: true);
        // WHERE THE JOURNAL'S OLDER ENTRIES GO. The app caps `trading/JOURNAL.md` and refuses one
        // that has outgrown the cap, so the agent needs somewhere to put what no longer fits that
        // is still tracked — this folder, which nothing caps and nothing versions.
        HomeFiles.CreateDirectory(home, WorkspaceRevisions.ArchiveDir, replaceLinks: true);
        HomeFiles.CreateDirectory(home, InDir, replaceLinks: true);
        HomeFiles.CreateDirectory(home, OutDir, replaceLinks: true);
        HomeFiles.CreateDirectory(home, AgentReach.TopFolder(MissionLoop.WakeFile), replaceLinks: true);

        // THE CANON AND THE GUIDE, FOR EVERY RUNTIME THIS ROLE CAN RUN ON, written at the same start. The CLI's go where
        // a vendor CLI reads them by name; the harness's into the role's app plumbing, where ApiConversation reads its
        // system text — so a key pasted mid-session moves the role onto a canon written for the harness, with no rewrite.
        var homeDir = CouncilRoles.HomeDir(ctx.Role);
        foreach (var runtime in RuntimesOf(ctx.Role, ctx.Kind))
        {
            var reach = AgentReach.For(ctx.Role, runtime, ctx.TurnsPerSession);
            Write(home, homeDir, Canon.FileFor(runtime), Canon.Render(ctx, reach).Text, files);
            Write(home, homeDir, Canon.GuideFor(runtime), Canon.Guide(ctx, reach).Text, files);
        }
        // THE LANGUAGE AND ITS WORKED PROGRAMS, app-owned exactly as the mission file above is. A role
        // that has to write a strategy has no other source for the grammar: there is no terminal here.
        ResearchLibrary.Write(home, homeDir, files);
        HomeFiles.WriteAllText(home, ".tradeagent/context.json", Json.Write(ctx, pretty: true), replaceLinks: true);
        return home;
    }

    /// <summary>
    /// The runtimes a role can run on: a vendor CLI for each of the legacy pair, and the app-owned harness for a role
    /// <see cref="ApiAgentRuntime.Serves"/> — so the chair, which never leaves the CLI, is written no harness canon. A position
    /// beyond the legacy pair runs a vendor CLI only as a broker-only seat (<c>U-thinking-seats-c</c>;
    /// <see cref="RuntimeManifest.SessionSeatClassFor"/>), so its <c>AGENTS.md</c> is the broker's canon, never one that tells it
    /// of a shell — unless it is an EXECUTOR (<c>U-execution-environment</c>): a Member beyond the pair runs a vendor CLI as a route
    /// (a) cell seat, so its <c>AGENTS.md</c> is the cell's canon. The class is <see cref="SeatPlan.ClassFor"/>'s, the session's own rule.
    /// </summary>
    public static RuntimeClass[] RuntimesOf(string role, string? kind = null) =>
        !CouncilRoles.IsKnown(role)
            ? [SeatPlan.ClassFor(role, kind, _ => true) == SeatPlan.CellClass ? RuntimeClass.Cell : RuntimeClass.Broker, RuntimeClass.Harness]
        : ApiAgentRuntime.Serves(role) ? [RuntimeClass.Cli, RuntimeClass.Harness] : [RuntimeClass.Cli];

    /// <summary>One app-owned text into the role's home, and onto the app's record of what it wrote, at that moment.</summary>
    static void Write(string home, string homeDir, string relPath, string text, AppFileManifest files)
    {
        HomeFiles.WriteAllText(home, relPath, text, replaceLinks: true);
        // THE CLI'S CANON IS KEPT AS WELL AS RECORDED (U-execution-environment item 3): an executor's cell writes its whole home, so
        // its session puts the app's canon back before every turn (AgentSession.RestoreCanon), from this text. THE HARNESS'S TOO
        // (U-exec-env-fix item 2): its turn sends this text as its system message, never what the cell left at its name.
        if (relPath is Canon.CliFile or Canon.HarnessFile) files.Keep(homeDir, relPath, text, home);
        else files.Record(homeDir, relPath, text);
    }

    /// <summary>
    /// Builds every position's home in one pass and returns them by position id: the legacy pair always, and
    /// <paramref name="positions"/> beside them — the chart's active positions, as the composition root reads them. A
    /// home is representable, never runnable: building one opens no conversation (<c>U-org-principals</c>).
    /// </summary>
    /// <param name="kindOf">Each position's kind as the chart derives it (<c>U-execution-environment</c>), which picks its canon's class.</param>
    /// <param name="placeOf">Each position's place on the chart (<c>OrgPlace</c>; <c>U-org-canon</c>), which picks its canon's role part.</param>
    /// <remarks>
    /// <para><b>A HOME THAT CANNOT BE REBUILT REFUSES ITS OWN POSITION AND NO OTHER</b> (<c>U-execution-environment</c> fix pass 4, the
    /// second re-review's [P2] 5; the orchestrator's ruling). An executor's cell writes its whole home, so it can leave a real folder where
    /// the app's canon belongs, or a file where <c>in/</c>, <c>out/</c> or <c>.tradeagent</c> does — and <see cref="HomeFiles"/> refuses
    /// to replace either, rightly. That refusal is caught HERE, per position: its home is still returned (its path does not change), its
    /// words are recorded (<see cref="RefusalFor"/>) and every launch of that position is refused in them, lasting — while every other
    /// position, the legacy pair included, is built and starts. Nothing of the cell's is deleted beyond the app-owned names
    /// <see cref="Build"/> replaces, and the next start that can rebuild the home clears the refusal.</para>
    /// </remarks>
    public static Dictionary<string, string> BuildAll(WorkspaceContext ctx, string? root = null,
        AppFileManifest? appFiles = null, IEnumerable<string>? positions = null, Func<string, string?>? kindOf = null,
        Func<string, string?>? placeOf = null)
    {
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in CouncilRoles.All.Concat(positions ?? []).Distinct(StringComparer.Ordinal))
            homes[r] = BuildPosition(ctx, r, root, appFiles, kindOf, placeOf);
        return homes;
    }

    /// <summary>
    /// ONE POSITION'S HOME, THROUGH THE START-UP PASS'S OWN BODY (<see cref="BuildAll"/> builds each of its positions here): a board seat's
    /// (<see cref="BuildBoardHome"/>) or a position's (<see cref="Build"/>, with its kind and place), its refusal cleared when it is built and
    /// recorded in words when it cannot be (<see cref="RefusalFor"/>) — its home's path returned either way. <c>U-home-on-hire</c>: the
    /// supervisor builds a position hired and activated while the app runs here, before its first launch, rather than at the next start.
    /// </summary>
    public static string BuildPosition(WorkspaceContext ctx, string position, string? root = null, AppFileManifest? appFiles = null,
        Func<string, string?>? kindOf = null, Func<string, string?>? placeOf = null)
    {
        var home = HomeOf(root ?? Paths.Workspace, position);
        try
        {
            var built = Core.Board.BoardSeats.IsBoardId(position)
                ? BuildBoardHome(position, root, appFiles)
                : Build(ctx with { Role = position, Kind = kindOf?.Invoke(position), Place = placeOf?.Invoke(position) }, root, appFiles);
            Unbuilt.TryRemove(Key(built), out _);
            return built;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unbuilt[Key(home)] = $"TradeAgent did not start the position {position}: the last start could not rebuild its folder — {ex.Message} "
                                 + "Nothing in the folder was deleted. This position stays refused until a start can rebuild its folder; "
                                 + "every other position started.";
            return home;
        }
    }

    /// <summary>
    /// RECORDS <paramref name="home"/> AS ONE ITS LAUNCHES ARE REFUSED IN, as a start that could not rebuild it does (<c>U-home-on-hire</c>
    /// item 2): a launch's own hard line found it unbuilt or stale — a canon that is not the app's, one it could not put back — so every
    /// later look (<see cref="RefusalFor"/>) refuses the position before admission, lasting, its wakes kept, until a start or the owner's
    /// repair rebuilds the folder. That is the bound: such a refusal reaches an admitted launch at most once, never a launch after launch.
    /// An earlier refusal's words stand. Returns the words the position is now refused in.
    /// </summary>
    public static string Refuse(string home, string position, string why) =>
        Unbuilt.GetOrAdd(Key(home), _ => $"TradeAgent did not start the position {position}: {why}. Nothing in its folder was deleted. This "
                                         + "position stays refused until a start or a repair of its folder rebuilds it; every other position runs.");

    /// <summary>
    /// A SEAT OF THE SECURITY BOARD'S HOME (<c>U-board-b2</c> item 3): its own folder under <c>workspace/board/</c> — never under an org
    /// position's and never shared between seats — holding the app's board canon for each runtime class it can run on, the folder a
    /// session reads its request from, and nothing else: no research library, no context file, no inbox and no other seat's or position's
    /// file, so its home is no channel around another seat's blind read. Everything below it written without following a link.
    /// </summary>
    public static string BuildBoardHome(string seat, string? root = null, AppFileManifest? appFiles = null)
    {
        var files = appFiles ?? AppFileManifest.Shared;
        var ws = root ?? Paths.Workspace;
        Directory.CreateDirectory(ws);
        var home = HomeOf(ws, seat);
        Directory.CreateDirectory(home);
        HomeFiles.CreateDirectory(home, AgentReach.TopFolder(MissionLoop.WakeFile), replaceLinks: true);
        var homeDir = CouncilRoles.HomeDir(seat);
        var canon = Core.Board.BoardPacket.Canon(seat);
        Write(home, homeDir, Canon.CliFile, canon, files);
        Write(home, homeDir, Canon.HarnessFile, canon, files);
        return home;
    }

    /// <summary>
    /// Why the position whose home is <paramref name="home"/> may not start, because the last start could not rebuild that home (see
    /// <see cref="BuildAll"/>), or null. Asked by its session before every launch.
    /// </summary>
    public static string? RefusalFor(string home) => Unbuilt.TryGetValue(Key(home), out var why) ? why : null;

    /// <summary>Where a repaired home's old folder is kept, under the recorded tree: <c>workspace/.refused</c>. No home, walk or listing names it.</summary>
    public const string RefusedDir = ".refused";

    /// <summary>
    /// THE OWNER'S REPAIR OF A HOME THE LAST START COULD NOT REBUILD (<c>U-repair-position-home</c>): the refused folder is MOVED WHOLE to
    /// <c>workspace/.refused/&lt;home-name&gt;-&lt;UTC stamp&gt;</c> and the home is built afresh from the app's templates (<see cref="Build"/>).
    /// <b>Nothing is deleted.</b> The move is one rename on the same volume of the folder by its own name — never a copy and a delete, and a
    /// link inside it stays a link that is never followed. Nor is it ever made THROUGH a link: the workspace, every folder down to the home
    /// and to the kept folder, and those two, are asked first and again right before the rename (<see cref="LinkOnTheWay"/>), and a link
    /// anywhere there refuses in words. If the rename cannot be made nothing changes and the refusal stands; if the
    /// rebuild fails again the refusal is recorded again, in words, and the old folder stays where it was moved. Whether a launch of the
    /// position is open is the caller's question (the host's), asked before this.
    /// </summary>
    /// <remarks>Linux and macOS only: on Windows, where no cell runs, it refuses in words and changes nothing.</remarks>
    public static HomeRepair RepairHome(WorkspaceContext ctx, string role, string? root, AppFileManifest? files, Func<DateTimeOffset> now)
    {
        var ws = root ?? Paths.Workspace;
        var home = HomeOf(ws, role);
        if (RefusalFor(home) is null)
            return new HomeRepair(false, $"The folder of {role} has nothing to repair: the last start rebuilt it. Nothing was changed.", null);
        if (OperatingSystem.IsWindows())
            return new HomeRepair(false, $"TradeAgent did not repair the folder of {role}: a repair moves the folder aside by renaming it, which this "
                                         + "build does on Linux and macOS only, and this computer runs Windows. Nothing was changed; the position stays refused.", null);

        var aside = Path.Combine(ws, RefusedDir, $"{Path.GetFileName(Key(home))}-{now().UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
        if (LinkOnTheWay(ws, home, aside) is { } link)
            return new HomeRepair(false, ThroughALink(role, link, "Nothing was changed"), null);
        if (File.Exists(aside) || Directory.Exists(aside) || new FileInfo(aside).LinkTarget is not null)
            return new HomeRepair(false, $"TradeAgent did not repair the folder of {role}: a kept folder named {aside} already exists, and a repair "
                                         + "never writes over one. Nothing was changed; press again in a second.", null);
        try
        {
            Directory.CreateDirectory(Path.Combine(ws, RefusedDir));
            // ASKED AGAIN RIGHT BEFORE THE MOVE, so a link made while the kept folders' folder was being made is caught too.
            if (LinkOnTheWay(ws, home, aside) is { } planted)
                return new HomeRepair(false, ThroughALink(role, planted, "Nothing was moved, and the folder is as it was"), null);
            Directory.Move(home, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HomeRepair(false, $"TradeAgent did not repair the folder of {role}: it could not move {home} aside to {aside} ({ex.Message}). "
                                         + "Nothing was changed; the position stays refused.", null);
        }

        try
        {
            Build(ctx with { Role = role }, root, files);
            Unbuilt.TryRemove(Key(home), out _);
            return new HomeRepair(true, $"The folder of {role} was repaired; the old one is kept at {aside}.", aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var why = $"TradeAgent did not start the position {role}: its folder was moved aside to {aside}, and the fresh one could not be built "
                      + $"({ex.Message}). Nothing was deleted. This position stays refused until a start or a repair can build its folder.";
            Unbuilt[Key(home)] = why;
            return new HomeRepair(false, why, aside);
        }
    }

    /// <summary>
    /// A REPAIR NEVER MOVES A FOLDER THROUGH A LINK (the Codex review's [P1] on <c>U-repair-position-home</c>): the first of the workspace,
    /// every folder from it down to the home and down to the kept folder, and those two themselves, that is a link, a junction or any other
    /// reparse point — looked at without following it — in words naming it; or null. A <c>.refused</c> that links into the owner's inbox
    /// would otherwise carry the cell's folder there, and one that links out of the workspace would carry it anywhere. What is above the
    /// workspace is the owner's computer, not asked.
    /// </summary>
    static string? LinkOnTheWay(string ws, string home, string aside)
    {
        foreach (var at in StepsTo(ws, home).Concat(StepsTo(ws, aside)))
        {
            var info = new FileInfo(at);
            string? target = null;
            try { target = info.LinkTarget; }
            catch (IOException) { /* a reparse point this platform cannot read the target of; the attribute below says */ }
            if (target is not null) return $"{at} is a link to {target}";
            if ((File.Exists(at) || Directory.Exists(at)) && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{at} is a link";
        }
        return null;
    }

    /// <summary>The workspace, then each folder from it down to <paramref name="to"/>, ending with <paramref name="to"/> itself.</summary>
    static IEnumerable<string> StepsTo(string ws, string to)
    {
        var at = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ws));
        yield return at;
        foreach (var part in Path.GetRelativePath(at, Path.GetFullPath(to)).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            yield return at = Path.Combine(at, part);
    }

    static string ThroughALink(string role, string link, string unchanged) =>
        $"TradeAgent did not repair the folder of {role}: {link}, and a repair never moves a folder through a link — it could carry the "
        + $"cell's folder into your inbox or out of TradeAgent's workspace. {unchanged}; the position stays refused.";

    /// <summary>The homes the last start of this process could not rebuild, by full path, with the words their positions are refused in.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Unbuilt = new(StringComparer.Ordinal);

    static string Key(string home) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));

    /// <summary>Environment handed to the agent process. The trade CLI is on PATH; no secrets are present.</summary>
    public static Dictionary<string, string> EnvironmentFor(string sessionId, string workspace)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return new Dictionary<string, string>
        {
            ["PATH"] = $"{Paths.Bin}{Path.PathSeparator}{path}",
            ["TRADEAGENT_SESSION"] = sessionId,
            ["TRADEAGENT_WORKSPACE"] = workspace,
            // Deliberately absent: broker credentials, the IPC token, anything from the user's
            // credential stores. The agent authenticates to the gateway via the trade CLI, which
            // reads the token from a user-only file it never prints.
        };
    }
}
