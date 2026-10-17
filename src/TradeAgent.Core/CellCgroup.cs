using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TradeAgent.Core;

/// <summary>
/// THE CGROUP SUBTREE TRADEAGENT'S SERVICE DELEGATES TO IT, AND ONE CGROUP PER ATTEMPT INSIDE IT (<c>U-cell-linux-b</c>,
/// <c>docs/ORCHESTRATION.md</c> §§ 2.3 and 2.8). Linux only; everywhere else it is <see cref="Unavailable"/>.
///
/// <para><b>Why a cgroup.</b> A session proves membership only while its members stay in it, and a process can leave its session
/// with one <c>setsid</c> — a double-forked daemon reparented to init is then nobody's by every test the session teardown can
/// make. A cgroup is per process, not per session: every process an attempt starts is BORN in the attempt's cgroup (the
/// launcher joins it before it starts anything), whatever session it makes, and <c>cgroup.kill</c> ends all of it at once. A
/// process can move itself out only by writing another cgroup's <c>cgroup.procs</c>, which a cell cannot (Landlock grants it
/// nothing beneath <c>/sys/fs/cgroup</c>, and its socket filter denies the D-Bus call that would ask systemd); for a launch that
/// is not a cell the session proof stays beside this one, ANDed, never replaced (RULING 1).</para>
///
/// <para><b>Where it comes from.</b> <c>tradeagent.service</c> says <c>Delegate=yes</c> (one root-owned line, installed once):
/// systemd then gives the service's cgroup to the service's own account. At start the app proves that
/// (<see cref="ForThisProcess"/>), moves itself into <c>supervisor/</c> — a cgroup holding processes cannot hand controllers down —
/// and enables <c>+pids +memory</c> below its root. The app never uses sudo. A root this build cannot prove is refused, never
/// guessed: a cell is then refused naming <c>Delegate=yes</c>, and a launch that is not a cell keeps the session alone, saying so
/// on its Mechanism line.</para>
///
/// <para><b>The ceilings</b> are named constants, said on the Mechanism line: <see cref="PidsMax"/> tasks and half of this
/// computer's memory per attempt.</para>
/// </summary>
public sealed class AttemptCgroups
{
    /// <summary>Where the unified (v2) hierarchy is mounted.</summary>
    public const string Mount = "/sys/fs/cgroup";

    /// <summary>Every attempt's cgroup is named with this prefix, and the restart sweep removes exactly these.</summary>
    public const string AttemptPrefix = "attempt-";

    /// <summary>The leaf the app moves itself into, so its root may enable controllers for the attempts.</summary>
    public const string SupervisorLeaf = "supervisor";

    /// <summary>The most tasks — processes and threads — one attempt may hold at once (<c>pids.max</c>).</summary>
    public const int PidsMax = 2048;

    /// <summary>One attempt may use at most MemTotal divided by this (<c>memory.max</c>): half of this computer's memory.</summary>
    public const int MemoryShare = 2;

    /// <summary>The controllers enabled below the root, in <c>cgroup.subtree_control</c>'s own words.</summary>
    public static readonly IReadOnlyList<string> Controllers = ["pids", "memory"];

    static AttemptCgroups _current = Unavailable("TradeAgent has not yet looked for the cgroup its service delegates to it");

    /// <summary>
    /// What this process holds: set once by <see cref="ForThisProcess"/> at the app's start, and read by every launch that is
    /// not handed a root of its own (<see cref="Open"/>, the test seam). Never read from the environment.
    /// </summary>
    public static AttemptCgroups Current => Volatile.Read(ref _current);

    readonly bool _standIn;

    AttemptCgroups(string? root, string? refusal, bool standIn = false)
    {
        Root = root;
        Refusal = refusal;
        _standIn = standIn;
    }

    /// <summary>The delegated root the attempts are made in, or null when there is none (<see cref="Refusal"/> says why).</summary>
    public string? Root { get; }

    /// <summary>Why this process holds no delegated root, in words that name <c>Delegate=yes</c> — or null when it holds one.</summary>
    public string? Refusal { get; }

    /// <summary>Whether attempts can be made here.</summary>
    public bool Held => Root is not null;

    /// <summary>No delegated root, and why. The sentence is completed so it names the repair.</summary>
    public static AttemptCgroups Unavailable(string why) =>
        new(null, $"{why}. TradeAgent holds each attempt in a cgroup of its own only where its service delegates one to it " +
                  "(Delegate=yes in tradeagent.service, which TradeAgent's installer writes)", standIn: false);

    /// <summary>
    /// A PLAIN DIRECTORY STANDING IN FOR A ROOT, for a unit test that must reach the start of a launch on a computer with no
    /// delegated cgroup. The launcher refuses its attempts (they are not under <see cref="Mount"/>), so nothing ever runs in one.
    /// </summary>
    internal static AttemptCgroups StandIn(string directory)
    {
        Directory.CreateDirectory(directory);
        return new AttemptCgroups(Path.GetFullPath(directory), null, standIn: true);
    }

    /// <summary>
    /// THE APP'S OWN ROOT, PROVED, AND THE APP MOVED INTO <c>supervisor/</c> BELOW IT — once per process; a second call returns what
    /// the first found. <c>/proc/self/cgroup</c> must hold exactly one <c>0::</c> line; that cgroup must be this account's with a
    /// writable <c>cgroup.procs</c> (what <c>Delegate=yes</c> gives a service with <c>User=</c>) and hold no process but this one;
    /// then this process moves into <c>supervisor/</c> and <c>+pids +memory</c> is written to the root's <c>subtree_control</c>.
    /// Every failure is an <see cref="Unavailable"/> naming the step.
    /// </summary>
    public static AttemptCgroups ForThisProcess()
    {
        lock (Gate)
        {
            if (_looked) return Current;
            _looked = true;
            var found = Adopt();
            Volatile.Write(ref _current, found);
            return found;
        }
    }

    static readonly Lock Gate = new();
    static bool _looked;

    static AttemptCgroups Adopt()
    {
        if (!OperatingSystem.IsLinux()) return Unavailable("this computer does not run Linux, whose cgroups hold an attempt");
        if (OwnCgroup() is not { } mine) return Unavailable(_ownCgroupWhy);

        // A SECOND START OF THE SAME PROCESS'S LOOK finds itself already in supervisor/: the root is its parent.
        var root = Path.GetFileName(mine) == SupervisorLeaf ? Path.GetDirectoryName(mine)! : mine;
        if (Prove(root) is { } notDelegated) return Unavailable(notDelegated);

        var self = Environment.ProcessId;
        if (Procs(root) is not { } inRoot) return Unavailable($"the cgroup {root} would not list its processes");
        if (inRoot.Any(p => p != self))
            return Unavailable($"the cgroup {root} holds other processes beside TradeAgent ({string.Join(", ", inRoot.Where(p => p != self).Take(8))}), " +
                               "so it is not a service's own");

        var supervisor = Path.Combine(root, SupervisorLeaf);
        try
        {
            Directory.CreateDirectory(supervisor);
            Write(Path.Combine(supervisor, "cgroup.procs"), self.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable($"TradeAgent could not move itself into {supervisor} ({ex.Message})");
        }
        if (OwnCgroup() != supervisor) return Unavailable($"TradeAgent wrote itself into {supervisor} and the kernel still places it elsewhere");

        return Enable(root) is { } why ? Unavailable(why) : new AttemptCgroups(root, null);
    }

    /// <summary>
    /// THE TEST SEAM: <paramref name="root"/> as a delegated root — made when missing, proved as <see cref="ForThisProcess"/> proves
    /// its own (this account's, a writable <c>cgroup.procs</c>, <c>cgroup.kill</c> present), holding no process, with
    /// <c>+pids +memory</c> enabled below it. Never moves this process, and never throws: a root it cannot prove is
    /// <see cref="Unavailable"/>, naming the step.
    /// </summary>
    public static AttemptCgroups Open(string root)
    {
        if (!OperatingSystem.IsLinux()) return Unavailable("this computer does not run Linux, whose cgroups hold an attempt");
        var full = Path.GetFullPath(root);
        if (!Under(full)) return Unavailable($"{full} is not beneath the cgroup filesystem at {Mount}");
        try { Directory.CreateDirectory(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable($"the cgroup {full} could not be made ({ex.Message})");
        }
        if (Prove(full) is { } notDelegated) return Unavailable(notDelegated);
        if (Procs(full) is not { Count: 0 }) return Unavailable($"the cgroup {full} holds processes, so it cannot hand controllers down");
        return Enable(full) is { } why ? Unavailable(why) : new AttemptCgroups(full, null);
    }

    /// <summary>
    /// A NEW ATTEMPT'S CGROUP, empty, with its ceilings written: <c>attempt-&lt;id&gt;-&lt;8 hex&gt;</c> under the root (the
    /// suffix keeps two launches of one attempt id apart). Throws <see cref="TradeAgentException"/>
    /// (<see cref="ErrorCode.CONTAINMENT_UNAVAILABLE"/>) when it cannot be made — the caller decides what that refuses.
    /// </summary>
    public AttemptCgroup Create(string? attemptId)
    {
        if (Root is null) throw new InvalidOperationException("no delegated cgroup is held: " + Refusal);
        var dir = Path.Combine(Root, AttemptPrefix + Safe(attemptId) + "-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var made = new AttemptCgroup(dir, _standIn);
            if (_standIn)
            {
                File.WriteAllText(Path.Combine(dir, "pids.max"), PidsMax.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(dir, "memory.max"), MemoryMax.ToString(CultureInfo.InvariantCulture));
                return made;
            }
            Write(Path.Combine(dir, "pids.max"), PidsMax.ToString(CultureInfo.InvariantCulture));
            Write(Path.Combine(dir, "memory.max"), MemoryMax.ToString(CultureInfo.InvariantCulture));
            if (Read(Path.Combine(dir, "pids.max")) != PidsMax.ToString(CultureInfo.InvariantCulture))
                throw new IOException($"{dir}/pids.max does not read back {PidsMax}");
            return made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            new AttemptCgroup(dir, _standIn).Remove();
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE,
                $"TradeAgent could not make the attempt's cgroup {dir} ({ex.Message})", ex);
        }
    }

    /// <summary>
    /// THE RESTART SWEEP: every <c>attempt-*</c> under the root killed (<c>cgroup.kill</c>) and removed, and every file in
    /// <see cref="CellPolicy.Directory"/> written before <paramref name="before"/> — this process's start, by default — discarded
    /// (<see cref="CellStage.Discard"/>: only the app's own). It closes what a crash of the app AND a launch's supervisor leaves:
    /// a turn's processes nothing was left to end, and a cell's policy file nothing was left to remove. The policy files are swept
    /// with no root too. Never throws; what it could not end or remove is in the report.
    /// </summary>
    public SweepReport Sweep(DateTime? before = null)
    {
        var killed = new List<string>();
        var left = new List<string>();
        var survivors = new List<ProcessEntry>();
        if (Root is not null)
        {
            IEnumerable<string> attempts;
            try { attempts = Directory.EnumerateDirectories(Root, AttemptPrefix + "*").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { attempts = []; left.Add($"{Root} ({ex.Message})"); }
            foreach (var dir in attempts)
            {
                var attempt = new AttemptCgroup(dir, _standIn);
                if (attempt.Kill(TreeTeardown.Bound) && attempt.Remove()) killed.Add(dir);
                else
                {
                    left.Add(dir);
                    // WHAT IT COULD NOT END IS STILL RUNNING (fix pass 1, R3): a leftover the kill did not empty is held by the meter
                    // and counted present until it is gone — a warning alone let paid turns and the material pass start beside it.
                    var unlisted = attempt.Pids() is null;
                    if (unlisted || attempt.Populated()) survivors.Add(AttemptCgroup.Survivor(dir, unlisted));
                }
            }
        }

        var cutoff = before ?? StartedAt;
        var discarded = 0;
        try
        {
            if (Directory.Exists(CellPolicy.Directory))
                foreach (var file in Directory.EnumerateFiles(CellPolicy.Directory).ToList())
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    CellStage.Discard(file);
                    if (!File.Exists(file)) discarded++;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add($"{CellPolicy.Directory} ({ex.Message})"); }

        return new SweepReport(killed, left, discarded) { Survivors = survivors };
    }

    /// <summary>What a sweep did: the attempts it ended and removed, what it could not, and how many policy files it discarded.</summary>
    public sealed record SweepReport(IReadOnlyList<string> Removed, IReadOnlyList<string> Left, int PolicyFiles)
    {
        /// <summary>What the sweep could not end, as survivors the meter holds every turn on until each is gone.</summary>
        public IReadOnlyList<ProcessEntry> Survivors { get; init; } = [];

        public string Sentence =>
            $"removed {Removed.Count} leftover attempt cgroup(s) and {PolicyFiles} leftover cell policy file(s)" +
            (Left.Count > 0 ? $"; could not remove {string.Join(", ", Left)}" : "") +
            (Survivors.Count > 0 ? $"; no AI turn starts until {string.Join(", ", Survivors.Select(s => Path.GetFileName(s.Cgroup)))} " +
                                   "holds no process" : "");
    }

    /// <summary>One line for a Mechanism row: the cgroup and its ceilings.</summary>
    public static string Ceilings => $"pids.max {PidsMax}, memory.max {MemoryMax / (1024 * 1024)} MiB (half of this computer's memory)";

    /// <summary>
    /// Half of MemTotal in bytes, in whole pages — the kernel keeps <c>memory.max</c> in pages and reads back a byte count rounded
    /// down to one (run 38970487186) — or, where MemTotal cannot be read, 1 GiB, which the Mechanism line then names.
    /// </summary>
    public static long MemoryMax { get; } = ReadMemoryMax();

    static long ReadMemoryMax()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal) &&
                    long.TryParse(line[9..].Trim().Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb > 0)
                {
                    var page = Math.Max(Environment.SystemPageSize, 1);
                    return kb * 1024 / MemoryShare / page * page;
                }
        }
        catch (Exception) { /* not Linux, or /proc unreadable: the floor below */ }
        return 1L << 30;
    }

    static DateTime StartedAt { get; } = ReadStart();

    static DateTime ReadStart()
    {
        try { using var me = Process.GetCurrentProcess(); return me.StartTime.ToUniversalTime(); }
        catch (Exception) { return DateTime.UtcNow; }
    }

    /// <summary>This process's own cgroup directory, from its one <c>0::</c> line — or null, with the reason kept for the refusal.</summary>
    public static string? OwnCgroup() => CgroupOf("/proc/self/cgroup", out _ownCgroupWhy);

    static string _ownCgroupWhy = "";

    /// <summary>The directory a <c>/proc/&lt;pid&gt;/cgroup</c> file names, when it is exactly one <c>0::</c> line.</summary>
    static string? CgroupOf(string file, out string why)
    {
        why = "";
        string[] lines;
        try { lines = File.ReadAllText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = $"{file} could not be read ({ex.Message})";
            return null;
        }
        if (lines.Length != 1 || !lines[0].StartsWith("0::/", StringComparison.Ordinal))
        {
            why = $"{file} is not one cgroup v2 line (it holds {lines.Length} line(s)), so this computer does not run the unified cgroup hierarchy alone";
            return null;
        }
        var rel = lines[0][3..];
        if (rel.Split('/').Any(s => s == ".."))
        {
            why = $"{file} names {rel}, outside this process's cgroup namespace";
            return null;
        }
        return rel == "/" ? Mount : Mount + rel;
    }

    /// <summary>Why <paramref name="root"/> is not a cgroup this account was delegated, or null when it is.</summary>
    static string? Prove(string root)
    {
        if (!File.Exists(Path.Combine(Mount, "cgroup.controllers")))
            return $"no unified (v2) cgroup hierarchy is mounted at {Mount}";
        if (!Under(root) || CellNative.RealPath(root) != root)
            return $"{root} is not a canonical path beneath {Mount}";
        if (CellNative.NotOwnedByThisAccount(root) is { } owner)
            return $"the cgroup {root} was not delegated to this account: {owner}";
        var procs = Path.Combine(root, "cgroup.procs");
        if (access(procs, W_OK) != 0)
            return $"the cgroup {root} was not delegated to this account: its cgroup.procs is not writable (errno {Marshal.GetLastPInvokeError()})";
        if (!File.Exists(Path.Combine(root, "cgroup.kill")))
            return $"the cgroup {root} has no cgroup.kill (Linux 5.14 or later), so an attempt could not be ended whole";
        return null;
    }

    /// <summary>Writes <c>+pids +memory</c> to the root's <c>subtree_control</c> and reads both back; why not, or null.</summary>
    static string? Enable(string root)
    {
        var control = Path.Combine(root, "cgroup.subtree_control");
        try
        {
            Write(control, string.Join(' ', Controllers.Select(c => "+" + c)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the cgroup {root} would not enable {string.Join(" and ", Controllers)} for its attempts ({ex.Message}; " +
                   $"offered: {Read(Path.Combine(root, "cgroup.controllers")) ?? "unreadable"})";
        }
        var enabled = (Read(control) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var missing = Controllers.Where(c => !enabled.Contains(c)).ToList();
        return missing.Count == 0 ? null
            : $"the cgroup {root} enabled {string.Join(" ", enabled)} and not {string.Join(" and ", missing)} for its attempts";
    }

    /// <summary>Whether <paramref name="path"/> is strictly beneath <see cref="Mount"/>, by whole components.</summary>
    internal static bool Under(string path) => path.StartsWith(Mount + "/", StringComparison.Ordinal) && !path.Split('/').Contains("..");

    /// <summary>The processes a cgroup lists directly, or null when it cannot be read.</summary>
    internal static List<int>? Procs(string dir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(dir, "cgroup.procs")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    static string Safe(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "launch";
        var s = new string(id.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_').Take(48).ToArray());
        return s.Length == 0 ? "launch" : s;
    }

    /// <summary>
    /// One write into a cgroup file: opened for writing only (no create, no truncate — a cgroup file is not a file), written at
    /// offset 0 in one call, as the kernel reads a cgroup command.
    /// </summary>
    internal static void Write(string path, string text)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write);
        RandomAccess.Write(handle, Encoding.ASCII.GetBytes(text), 0);
    }

    internal static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    const int W_OK = 2;

    [DllImport("libc", SetLastError = true)]
    static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}

/// <summary>
/// ONE ATTEMPT'S CGROUP: where the launcher puts itself before it starts anything, so every process of the attempt is born in it;
/// ended whole by <c>cgroup.kill</c>, then removed. See <see cref="AttemptCgroups"/>.
/// </summary>
public sealed class AttemptCgroup
{
    readonly bool _standIn;

    internal AttemptCgroup(string dir, bool standIn)
    {
        Dir = dir;
        _standIn = standIn;
    }

    /// <summary>The attempt's directory under the delegated root.</summary>
    public string Dir { get; }

    /// <summary>
    /// Writes <c>1</c> to <c>cgroup.kill</c> — SIGKILL to every process in the attempt and below it, whatever session or parent it
    /// has — and waits, bounded, for <c>cgroup.events</c> to say <c>populated 0</c>. True when nothing of the attempt runs (or the
    /// cgroup is already gone); false when the bound passed first.
    /// </summary>
    public bool Kill(TimeSpan bound)
    {
        if (!Directory.Exists(Dir)) return true;
        if (_standIn) return true;
        try { AttemptCgroups.Write(Path.Combine(Dir, "cgroup.kill"), "1"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(Dir)) return true;
        }
        var until = DateTime.UtcNow + bound;
        while (true)
        {
            if (!Populated()) return true;
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// A SURVIVOR STANDING FOR EVERYTHING IN <paramref name="dir"/> (<see cref="ProcessEntry.Cgroup"/>): what a teardown or the
    /// restart's sweep could not end, held by the meter until <see cref="StillHolds"/> says no. <paramref name="unlisted"/>: its
    /// processes could not be listed either.
    /// </summary>
    public static ProcessEntry Survivor(string dir, bool unlisted) => new(0, 0, 0, 0, 0, false, false, unlisted, dir);

    /// <summary>Whether <paramref name="dir"/> still holds a process — yes while the kernel will not say, never ended on a guess.</summary>
    public static bool StillHolds(string dir) => new AttemptCgroup(dir, standIn: false).Populated();

    /// <summary>Whether any process is in the attempt or below it; a cgroup that is gone holds none, and one that cannot be read does.</summary>
    public bool Populated()
    {
        if (!Directory.Exists(Dir)) return false;
        var events = AttemptCgroups.Read(Path.Combine(Dir, "cgroup.events"));
        if (events is null) return Directory.Exists(Dir);
        return !events.Split('\n').Any(l => l.Trim() == "populated 0");
    }

    /// <summary>
    /// Every process listed in the attempt and below it — what a teardown names when the kill did not end them — or NULL when any
    /// part of it could not be read (fix pass 1, R4): a listing with a hole in it is not a list, and an empty one read as "none" would
    /// report a populated cgroup ended. A cgroup that is gone lists none.
    /// </summary>
    public IReadOnlyList<int>? Pids()
    {
        var all = new List<int>();
        try
        {
            if (!Directory.Exists(Dir)) return all;
            foreach (var dir in Directory.EnumerateDirectories(Dir, "*", SearchOption.AllDirectories).Prepend(Dir))
            {
                if (AttemptCgroups.Procs(dir) is not { } listed) return Directory.Exists(Dir) ? null : [];
                all.AddRange(listed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Directory.Exists(Dir) ? null : []; }
        return all;
    }

    /// <summary>
    /// WHAT A KILL THAT DID NOT EMPTY THIS CGROUP LEAVES (fix pass 1, R4): every listed process still running, by pid and start, AND
    /// the cgroup itself as a survivor while it is populated or could not be listed — unknown is never ended, and a process forked
    /// after the listing is still held by the cgroup. Empty only when the cgroup has emptied since.
    /// </summary>
    public IReadOnlyList<ProcessEntry> Left()
    {
        var listed = Pids();
        var left = new List<ProcessEntry>();
        foreach (var pid in listed ?? [])
            if (ProcessTable.Read(pid) is { Zombie: false } running) left.Add(running);
        if (listed is null || Populated()) left.Add(Survivor(Dir, unlisted: listed is null));
        return left;
    }

    /// <summary>Removes the attempt's cgroup and any it made below it, deepest first (<c>rmdir</c> each). True once it is gone.</summary>
    public bool Remove()
    {
        try
        {
            if (!Directory.Exists(Dir)) return true;
            if (_standIn)
            {
                Directory.Delete(Dir, recursive: true);
                return true;
            }
            foreach (var sub in Directory.EnumerateDirectories(Dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Count(c => c == '/')).ToList())
                Directory.Delete(sub, recursive: false);
            Directory.Delete(Dir, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return !Directory.Exists(Dir); }
    }
}
