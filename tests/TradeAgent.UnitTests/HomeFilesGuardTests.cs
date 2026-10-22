using System.Reflection;
using System.Reflection.Emit;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CLASS, NOT THE INSTANCES (<c>U-execution-environment</c> fix pass 4; the orchestrator's ruling). An executor's cell writes its whole
/// home while the UNCONFINED app reads, writes, moves, deletes and lists in that same folder, so every app-side act there has to go through
/// <see cref="HomeFiles"/>, which follows no link at any component. Three review passes found such acts one at a time; this guard makes the
/// next one fail a test instead of waiting for a review.
///
/// <para><b>What it scans:</b> the compiled IL of every type that touches a position's home — its methods, constructors, lambdas, local
/// functions and async state machines — for every call, <c>newobj</c> and method-address load whose target is path-based
/// <c>System.IO</c>: any member of <see cref="File"/>, <see cref="Directory"/>, <see cref="FileInfo"/>, <see cref="DirectoryInfo"/> or
/// <see cref="FileSystemInfo"/> (an overload whose first parameter is an open <c>SafeFileHandle</c> excepted: it names no path), and a
/// <see cref="FileStream"/>, <see cref="StreamReader"/> or <see cref="StreamWriter"/> constructed from a path. Every such call must be on
/// <see cref="Reviewed"/> — by the method it is in, the member it calls and how many times — with the one-line reason it never reaches into
/// an executor's home. A new call, or one more of a reviewed kind in the same method, fails; so does a reviewed entry no longer in the code.</para>
///
/// <para><b>What it cannot see, said:</b> it reads calls, not the strings handed to them, so a reviewed call whose path argument later
/// changes to one inside a home passes until its method gains or loses a call (the reason on its line is the review's claim, not a
/// proof); a type that is not in <see cref="Scanned"/> is not read at all, so a NEW class that acts in a home is outside it until it is
/// added; and a call made through a helper of another type (a path handed to <c>AppFileManifest</c>, <c>Json</c>, a process's working
/// directory) is that helper's, not seen here.</para>
/// </summary>
public class HomeFilesGuardTests(ITestOutputHelper log)
{
    /// <summary>Every type of the app's own that reads, writes, moves, deletes or lists in a position's home.</summary>
    static readonly Type[] Scanned =
    [
        typeof(WorkspaceBuilder), typeof(ResearchLibrary), typeof(GrantedWorkerTools), typeof(ToolPaths), typeof(CouncilRelay),
        typeof(WorkspaceRevisions), typeof(MissionLoop), typeof(Backtests), typeof(MaterialScanner), typeof(WorkspaceReads),
        typeof(AgentSession), typeof(AgentSupervisor), typeof(AppHost), typeof(Canon), typeof(ApiConversation), HarnessRuntime,
        typeof(CliAgentRuntime), typeof(SeatProfile), typeof(CliSeatProbe)
    ];

    /// <summary>
    /// The harness's runtime type, reached through one of its static members: this guard reads its IL and never builds one, and
    /// <c>SuiteReachesNoVendorTests</c> refuses a test that names the type itself unless it sets a base URL (static access is its stated
    /// exception, since it names no instance).
    /// </summary>
    static Type HarnessRuntime => ((Func<string, bool>)ApiAgentRuntime.Serves).Method.DeclaringType!;

    /// <summary>
    /// THE REVIEWED PATH-BASED <c>System.IO</c> CALLS IN <see cref="Scanned"/>: the method, the member, how many, and why it is never a
    /// path inside an executor's home. Adding a line here is a review decision, made in a diff a reviewer reads.
    /// </summary>
    static readonly (string Site, string Member, int Count, string Why)[] Reviewed =
    [
        // ---- a home's own folder, made by its path: its ancestry (workspace/, workspace/org/) is the app's and no cell writes it ----
        ("AgentSession.StartAsync", "Directory.CreateDirectory", 1, "the home folder itself, whose ancestry no cell can write"),
        ("ApiAgentRuntime.CreateEnvironmentAsync", "Directory.CreateDirectory", 1, "the home folder itself, whose ancestry no cell can write"),
        ("ApiConversation.StartAsync", "Directory.CreateDirectory", 1, "the home folder itself, whose ancestry no cell can write"),
        ("CliAgentRuntime.CreateEnvironmentAsync", "Directory.CreateDirectory", 1, "the chair's home folder itself, whose ancestry no cell can write"),
        ("WorkspaceBuilder.Build", "Directory.CreateDirectory", 3, "the workspace, the owner's inbox and the home folder itself — none below a home"),

        // ---- the app's install, never a home ------------------------------------------------------------------------------------
        ("CliAgentRuntime.Candidates", "File.Exists", 4, "the runtime's executable: the manifest's absolute path, tools/<id>/ and PATH"),
        ("CliAgentRuntime.InstallAsync", "Directory.CreateDirectory", 1, "tools/<id>/, the app's own install folder"),
        ("CliAgentRuntime.ManagedDirectories", "Directory.Exists", 1, "tools/<id>/, the app's own install folder"),
        ("CliAgentRuntime.ManagedDirectories", "Directory.GetDirectories", 1, "tools/<id>/, the app's own install folder"),
        ("ResearchLibrary.ReadAll", "File.ReadAllBytes", 1, "the shipped example programs beside the app's own assemblies"),
        ("ResearchLibrary.Reference", "File.Exists", 1, "the shipped language reference beside the app's own assemblies"),
        ("ResearchLibrary.Reference", "File.ReadAllText", 1, "the shipped language reference beside the app's own assemblies"),
        ("ResearchLibrary.Write", "File.Exists", 1, "the shipped example programs beside the app's own assemblies"),

        // ---- the owner's inbox, which no cell writes ------------------------------------------------------------------------------
        ("MaterialScanner.HashPending", "File.Open", 1, "an inbox row only (IsInInbox); a home's row is opened through HomeFiles"),
        ("WorkspaceReads..cctor", "Directory.GetDirectories", 1, "Disk: the inbox only in the product (Under), internal so no product code hands it a home"),
        ("WorkspaceReads..cctor", "Directory.GetFiles", 1, "Disk: the inbox only in the product (Under), internal so no product code hands it a home"),
        ("WorkspaceReads.FileOnDisk", "FileInfo..ctor", 1, "Disk: the inbox only in the product (Under)"),
        ("WorkspaceReads.FileOnDisk", "FileInfo.get_Length", 1, "Disk: the inbox only in the product (Under)"),
        ("WorkspaceReads.FileOnDisk", "FileSystemInfo.get_LastWriteTimeUtc", 1, "Disk: the inbox only in the product (Under)"),
        ("WorkspaceReads.FolderOnDisk", "File.GetAttributes", 1, "Disk: the inbox only in the product (Under)"),

        // ---- in a home, but neither reading, writing, moving, deleting nor listing what a link points at -------------------------
        ("ToolPaths.LinkInside", "Directory.Exists", 1, "the path check's early refusal of a link, in words; every open of the path goes through HomeFiles"),
        ("ToolPaths.LinkInside", "Directory.ResolveLinkTarget", 1, "the path check's early refusal of a link (the link's own target, not followed)"),
        ("ToolPaths.LinkInside", "File.Exists", 1, "the path check's early refusal of a link, in words; every open of the path goes through HomeFiles"),
        ("ToolPaths.LinkInside", "File.ResolveLinkTarget", 1, "the path check's early refusal of a link (the link's own target, not followed)"),
        ("Backtests.Link", "Directory.Exists", 1, "the program check's early refusal of a link out, in words; the read goes through HomeFiles"),
        ("Backtests.Link", "DirectoryInfo..ctor", 1, "the program check's early refusal of a link out, in words; the read goes through HomeFiles"),
        ("Backtests.Link", "FileInfo..ctor", 1, "the program check's early refusal of a link out, in words; the read goes through HomeFiles"),
        ("Backtests.Link", "FileSystemInfo.get_LinkTarget", 1, "the program check's early refusal of a link out, in words; the read goes through HomeFiles"),
        ("Backtests.Link", "FileSystemInfo.ResolveLinkTarget", 1, "where a link leads, to refuse it; nothing is read through it"),
        ("Backtests.Link", "FileSystemInfo.get_FullName", 1, "a path string, no disk access"),

        // ---- the seat's own home, the runtime's executable and the app's state: SeatProfile (fix pass 5, G2) ------------------------
        ("SeatProfile.Below", "Directory.EnumerateDirectories", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.Below", "Directory.EnumerateFileSystemEntries", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.Below", "Directory.Exists", 2, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.Foreign", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home — the position's home is looked at through HomeFiles (ForeignInHome)"),
        ("SeatProfile.Foreign", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home — the position's home is looked at through HomeFiles (ForeignInHome)"),
        ("SeatProfile.IsLink", "FileInfo..ctor", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.IsLink", "FileSystemInfo.get_Attributes", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (a link's own attributes, not followed)"),
        ("SeatProfile.IsLink", "FileSystemInfo.get_LinkTarget", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (a link's own target, not followed)"),
        ("SeatProfile.Look", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.Look", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NoEntryOfItsOwn", "FileInfo..ctor", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NotALink", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NotALink", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NotALink", "FileInfo..ctor", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NotALink", "FileSystemInfo.get_Attributes", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (a link's own attributes, not followed)"),
        ("SeatProfile.NotALink", "FileSystemInfo.get_LinkTarget", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (a link's own target, not followed)"),
        ("SeatProfile.NotForeign", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.NotForeign", "DirectoryInfo..ctor", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.OwnedHere", "DirectoryInfo..ctor", 2, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.OwnedHere", "FileSystemInfo.set_UnixFileMode", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.OwnedOnWindows", "FileSystemInfo.Refresh", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.OwnedOnWindows", "FileSystemInfo.get_FullName", 2, "a path string, no disk access"),
        ("SeatProfile.PrivateBelow", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.PrivateBelow", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.PrivateFile", "File.SetUnixFileMode", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.ReadNoFollow", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (its config file)"),
        ("SeatProfile.ReadNoFollow", "File.ReadAllBytes", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home (its config file)"),
        ("SeatProfile.RealFolder", "Directory.CreateDirectory", 2, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home and the folder above it"),
        ("SeatProfile.RealFolder", "Directory.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.RealFolder", "File.Exists", 1, "the seat's own home (seat-home/), which every cell grant refuses (Cell.Guarded); never a position's home"),
        ("SeatProfile.Sha256Of", "File.OpenRead", 1, "the runtime's executable (tools/<id>/ or the manifest's path), never a home"),
        ("SeatProfile.ProbeFolder", "Directory.CreateDirectory", 1, "state/seat-probe, the app's own empty folder the CLI's reports run in (G2)"),
        ("SeatProfile.ProbeFolder", "FileSystemInfo.get_FullName", 1, "a path string, no disk access"),

        // ---- pre-existing, not this unit's ---------------------------------------------------------------------------------------
        ("CouncilRelay.Deliver", "Directory.CreateDirectory", 1, "the legacy pair claims no containment — CARD"),
        ("CouncilRelay.Deliver", "File.WriteAllText", 1, "the legacy pair claims no containment — CARD"),
    ];

    [Fact]
    public void No_app_code_acts_in_a_home_through_System_IO_except_where_reviewed()
    {
        var found = Scanned.SelectMany(PathCalls)
            .GroupBy(c => (c.Site, c.Member))
            .ToDictionary(g => g.Key, g => g.Count());
        var reviewed = Reviewed.ToDictionary(r => (r.Site, r.Member), r => r.Count);

        foreach (var ((site, member), n) in found.OrderBy(f => f.Key.Site, StringComparer.Ordinal).ThenBy(f => f.Key.Member, StringComparer.Ordinal))
            log.WriteLine($"(\"{site}\", \"{member}\", {n}, \"\"),");

        var unreviewed = found.Where(f => !reviewed.TryGetValue(f.Key, out var n) || n != f.Value)
            .Select(f => $"{f.Key.Site} calls {f.Key.Member} ×{f.Value}" + (reviewed.TryGetValue(f.Key, out var r) ? $" (reviewed ×{r})" : ""))
            .ToList();
        var stale = reviewed.Where(r => !found.ContainsKey(r.Key)).Select(r => $"{r.Key.Site} → {r.Key.Member}").ToList();

        Assert.True(unreviewed.Count == 0,
            "app code acts on a path through System.IO where a cell may write, instead of through HomeFiles — route it through HomeFiles, "
            + "or review it onto the list with the reason it never reaches into an executor's home:\n  " + string.Join("\n  ", unreviewed));
        Assert.True(stale.Count == 0, "a reviewed call is no longer in the code; take it off the list:\n  " + string.Join("\n  ", stale));
        Assert.All(Reviewed, r => Assert.False(string.IsNullOrWhiteSpace(r.Why), $"{r.Site} → {r.Member} has no reason"));
    }

    /// <summary>
    /// THE GUARD SEES WHAT IT CLAIMS TO: a call in a lambda, an async method and a nested type, and none of the handle-based or
    /// non-<c>System.IO</c> calls beside them.
    /// </summary>
    [Fact]
    public void The_guard_reads_lambdas_state_machines_and_constructors_and_nothing_path_free()
    {
        var calls = PathCalls(typeof(Sample)).Select(c => $"{c.Site} → {c.Member}").Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
        [
            "HomeFilesGuardTests.Sample.Async → File.ReadAllTextAsync",
            "HomeFilesGuardTests.Sample.Lambda → File.Exists",
            "HomeFilesGuardTests.Sample.Nested.Make → FileStream..ctor",
            "HomeFilesGuardTests.Sample.Plain → Directory.CreateDirectory",
        ], calls);
    }

    static class Sample
    {
        public static void Plain(string p) { Directory.CreateDirectory(p); _ = Path.Combine(p, "x"); }
        public static Func<string, bool> Lambda() => p => File.Exists(p);
        public static async Task<string> Async(string p) => await File.ReadAllTextAsync(p);
        public static DateTime ByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h) => File.GetLastWriteTimeUtc(h);
        public static FileStream OverHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h) => new(h, FileAccess.Read);
        public static class Nested
        {
            public static FileStream Make(string p) => new(p, FileMode.Open);
        }
    }

    // ---- the IL reader ------------------------------------------------------------------------------------------------------

    static readonly Type[] PathTypes = [typeof(File), typeof(Directory), typeof(FileInfo), typeof(DirectoryInfo), typeof(FileSystemInfo)];
    static readonly Type[] PathStreams = [typeof(FileStream), typeof(StreamReader), typeof(StreamWriter)];

    /// <summary>Every path-based System.IO call in <paramref name="type"/> and its nested types, by the source method it is written in.</summary>
    static IEnumerable<(string Site, string Member)> PathCalls(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in SelfAndNested(type))
            foreach (var m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
                foreach (var target in Targets(m))
                    if (PathBased(target)) yield return (Site(t, m), $"{target.DeclaringType!.Name}.{target.Name}");
    }

    static bool PathBased(MethodBase target)
    {
        var owner = target.DeclaringType;
        if (owner is null) return false;
        var first = target.GetParameters().FirstOrDefault()?.ParameterType;
        if (PathTypes.Contains(owner)) return first != typeof(Microsoft.Win32.SafeHandles.SafeFileHandle);
        return PathStreams.Contains(owner) && target is ConstructorInfo && first == typeof(string);
    }

    /// <summary>The source method a body was written in: a lambda's, a local function's or a state machine's enclosing one, by its type.</summary>
    static string Site(Type t, MethodBase m)
    {
        var types = new List<string>();
        string? from = Inner(m.Name);
        for (var at = t; at is not null; at = at.DeclaringType)
        {
            if (at.Name.StartsWith('<')) { from ??= Inner(at.Name); continue; }
            types.Insert(0, at.Name);
        }
        return $"{string.Join('.', types)}.{from ?? m.Name}";
    }

    /// <summary>The name between the angle brackets of a compiler-made name (<c>&lt;Snapshot&gt;b__5_0</c> → Snapshot), or null.</summary>
    static string? Inner(string name) =>
        name.StartsWith('<') && name.IndexOf('>') is > 1 and var end ? name[1..end] : null;

    static IEnumerable<Type> SelfAndNested(Type t) =>
        new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(SelfAndNested));

    static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Every method a body calls, constructs or takes the address of — decoded opcode by opcode, never guessed from bytes.</summary>
    static IEnumerable<MethodBase> Targets(MethodBase m)
    {
        if (m.GetMethodBody()?.GetILAsByteArray() is not { } il) yield break;
        var t = m.DeclaringType!;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var op = Codes[value];
            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? target = null;
                try
                {
                    target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i),
                        t.IsGenericType ? t.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null);
                }
                catch (ArgumentException) { }
                if (target is not null) yield return target;
            }
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4
            };
        }
    }
}
