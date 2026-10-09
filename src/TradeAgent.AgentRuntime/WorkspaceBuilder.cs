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
    bool ConnectorIsBuiltInSimulator = false, string Role = CouncilRoles.Operations, int? TurnsPerSession = null);

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
        // Only for the role whose home IS the old single workspace. A role added later has nothing
        // at an older name to carry, and running this for it would move the chair's work into it.
        if (ctx.Role == CouncilRoles.Operations) MoveOlderLayout(ws, home);
        Directory.CreateDirectory(home);
        foreach (var d in SubDirs) Directory.CreateDirectory(Path.Combine(home, d));
        // WHERE THE JOURNAL'S OLDER ENTRIES GO. The app caps `trading/JOURNAL.md` and refuses one
        // that has outgrown the cap, so the agent needs somewhere to put what no longer fits that
        // is still tracked — this folder, which nothing caps and nothing versions.
        Directory.CreateDirectory(Path.Combine(home,
            WorkspaceRevisions.ArchiveDir.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(home, InDir));
        Directory.CreateDirectory(Path.Combine(home, OutDir));
        Directory.CreateDirectory(Path.Combine(home, AgentReach.TopFolder(MissionLoop.WakeFile)));

        // THE CANON AND THE GUIDE, FOR EVERY RUNTIME THIS ROLE CAN RUN ON, written at the same start. The CLI's go where
        // a vendor CLI reads them by name; the harness's into the role's app plumbing, where ApiConversation reads its
        // system text — so a key pasted mid-session moves the role onto a canon written for the harness, with no rewrite.
        var homeDir = CouncilRoles.HomeDir(ctx.Role);
        foreach (var runtime in RuntimesOf(ctx.Role))
        {
            var reach = AgentReach.For(ctx.Role, runtime, ctx.TurnsPerSession);
            Write(home, homeDir, Canon.FileFor(runtime), Canon.Render(ctx, reach).Text, files);
            Write(home, homeDir, Canon.GuideFor(runtime), Canon.Guide(ctx, reach).Text, files);
        }
        // THE LANGUAGE AND ITS WORKED PROGRAMS, app-owned exactly as the mission file above is. A role
        // that has to write a strategy has no other source for the grammar: there is no terminal here.
        ResearchLibrary.Write(home, homeDir, files);
        File.WriteAllText(Path.Combine(home, ".tradeagent", "context.json"), Json.Write(ctx, pretty: true));
        return home;
    }

    /// <summary>
    /// The runtimes a role can run on: a vendor CLI for every role, and the app-owned harness for a role
    /// <see cref="ApiAgentRuntime.Serves"/> — so the chair, which never leaves the CLI, is written no harness canon.
    /// </summary>
    public static RuntimeClass[] RuntimesOf(string role) =>
        ApiAgentRuntime.Serves(role) ? [RuntimeClass.Cli, RuntimeClass.Harness] : [RuntimeClass.Cli];

    /// <summary>One app-owned text into the role's home, and onto the app's record of what it wrote, at that moment.</summary>
    static void Write(string home, string homeDir, string relPath, string text, AppFileManifest files)
    {
        var full = Path.Combine(home, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        files.Record(homeDir, relPath, text);
    }

    /// <summary>Builds every role's home in one pass and returns them by role.</summary>
    public static Dictionary<string, string> BuildAll(WorkspaceContext ctx, string? root = null,
        AppFileManifest? appFiles = null) =>
        CouncilRoles.All.ToDictionary(r => r, r => Build(ctx with { Role = r }, root, appFiles));

    /// <summary>
    /// Carries an install built before the agent's home moved. Its work sat directly in the
    /// workspace; leaving it there would not lose the files but would strand them — unscanned,
    /// invisible to the agent, and impossible to explain to the owner. Runs once: after the move
    /// there is nothing at the old names to find.
    ///
    /// Nothing is overwritten and nothing is deleted. A directory that already exists at the new
    /// name is left exactly as it is, and its old twin is left on disk rather than merged, because
    /// a merge here would silently choose between two versions of a file.
    /// </summary>
    static void MoveOlderLayout(string ws, string home)
    {
        foreach (var d in SubDirs)
        {
            var was = Path.Combine(ws, d);
            var now = Path.Combine(home, d);
            if (!Directory.Exists(was) || Directory.Exists(now)) continue;
            try
            {
                Directory.CreateDirectory(home);
                Directory.Move(was, now);
            }
            catch (IOException) { }                 // a file open in it; the next start tries again
            catch (UnauthorizedAccessException) { }
        }

        var oldAgents = Path.Combine(ws, "AGENTS.md");
        try { if (File.Exists(oldAgents)) File.Delete(oldAgents); }
        catch (IOException) { }                     // it is regenerated every start; a stale copy
        catch (UnauthorizedAccessException) { }     // outside the agent's home is only clutter
    }

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
