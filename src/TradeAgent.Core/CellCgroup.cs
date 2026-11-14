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
/// <para><b>The ceilings</b> are named constants, said on the Mechanism line (<see cref="Ceilings"/>). PER ATTEMPT: <see cref="PidsMax"/> tasks;
/// <c>memory.max</c> half of this computer's memory or <see cref="MemoryCap"/>, the less; <c>memory.high</c> at four fifths of it, where the kernel
/// reclaims and slows the attempt before its OOM; <c>memory.swap.max</c> 0, so a cell at its ceiling is ended rather than pushed into swap, which
/// the owner's other services would pay for first; and <c>io.max</c> writes of <see cref="WriteBps"/> on the home's disk, so a cell's plain writes
/// cannot outrun the free-space guard at the device's own rate. ALL ATTEMPTS TOGETHER (<c>U-cell-ceilings</c>; A4 § 5): every attempt is made
/// beneath one parent, <c>attempts/</c>, whose own <c>memory.max</c> (half of this computer's memory), <c>pids.max</c> (<see cref="SumPidsMax"/>),
/// <c>memory.swap.max</c> and <c>io.max</c> bound their sum, not only each. Where this kernel accounts no swap per cgroup, or the io controller
/// is not delegated, or the home is on no one block device, that ceiling is not written and the line says so (<see cref="SwapGap"/>,
/// <see cref="IoGap"/>): cells still run there as before, because the memory and task ceilings hold, and the cell proof records it as a GAP
/// row, never as a pass.</para>
/// </summary>
public sealed class AttemptCgroups
{
    /// <summary>Where the unified (v2) hierarchy is mounted.</summary>
    public const string Mount = "/sys/fs/cgroup";

    /// <summary>Every attempt's cgroup is named with this prefix, and the restart sweep removes exactly these.</summary>
    public const string AttemptPrefix = "attempt-";

    /// <summary>The leaf the app moves itself into, so its root may enable controllers for the attempts.</summary>
    public const string SupervisorLeaf = "supervisor";

    /// <summary>
    /// THE ONE PARENT EVERY ATTEMPT IS MADE BENEATH (<c>U-cell-ceilings</c> item 2): its own ceilings bound the SUM of the attempts. It holds no process
    /// (it hands controllers down), and it is never removed: a restart finds it and writes its ceilings again.
    /// </summary>
    public const string AttemptsLeaf = "attempts";

    /// <summary>
    /// ROUTE (a)'S TWO LEAVES OF ONE ATTEMPT (<c>U-cell-linux-c</c> item 3): <c>harness/</c>, where the codex harness that holds the login
    /// runs and the only cgroup its launch grant answers to, and <c>cell/</c>, where its exec-server's cell runs. Both beneath the attempt,
    /// so the attempt's ceilings, its <c>cgroup.kill</c> and its sweep hold them both; neither is the other's.
    /// </summary>
    public const string HarnessLeaf = "harness", CellLeaf = "cell";

    /// <summary>The leaves <see cref="ContainedLaunch.CgroupFlag"/> takes beneath an <c>attempt-*</c> directory: <see cref="HarnessLeaf"/> and <see cref="CellLeaf"/>.</summary>
    public static readonly IReadOnlyList<string> Leaves = [HarnessLeaf, CellLeaf];

    /// <summary>The most tasks — processes and threads — one attempt may hold at once (<c>pids.max</c>).</summary>
    public const int PidsMax = 2048;

    /// <summary>
    /// ALL ATTEMPTS TOGETHER may hold at most this many tasks (the parent's <c>pids.max</c>): one runaway attempt at its own ceiling leaves the others
    /// half as much again between them, and the lanes' few attempts at once use a small part of it.
    /// </summary>
    public const int SumPidsMax = PidsMax / 2 * 3;

    /// <summary>All attempts together use at most MemTotal divided by this (the parent's <c>memory.max</c>): half of this computer's memory.</summary>
    public const int MemoryShare = 2;

    /// <summary>
    /// THE MOST ONE ATTEMPT MAY USE, whatever this computer's memory (<c>U-cell-ceilings</c> item 1): 8 GiB. What runs in a cell is a coding agent's harness
    /// and the tools it starts — a shell, Python, a compiler, a script over a dataset — never the app's own backtests or tape, which run outside any cell.
    /// 8 GiB is a large Python process's worth with room to spare; a computer of 16 GB is held at half its memory as before, and a server of 64 GB keeps
    /// 56 GB for the owner's other services while one attempt runs.
    /// </summary>
    public const long MemoryCap = 8L << 30;

    /// <summary>
    /// <c>memory.high</c> is <c>memory.max</c> times this over five (four fifths): above it the kernel reclaims the attempt's own pages and slows it, so it
    /// meets its ceiling slowly, and the reclaim is its own rather than the owner's services'.
    /// </summary>
    public const int HighFifths = 4;

    /// <summary>
    /// <c>memory.swap.max</c>, per attempt and for all of them: 0. An attempt at its ceiling is ended by the kernel's OOM within its own cgroup, never paged
    /// out to swap, whose I/O and reclaim the owner's other services would pay for first (A4 § 5).
    /// </summary>
    public const long SwapMax = 0;

    /// <summary>
    /// <c>io.max</c>'s write rate on the home's disk, per attempt and for all of them: 200 MiB/s (<c>U-cell-ceilings</c> item 3; A4-1(b)). The free-space
    /// guard reads every two seconds, so about 400 MiB of written blocks get through between two reads (plus what the kernel lets a cgroup dirty in
    /// memory before its writeback is throttled), against a floor of up to 10 GiB — where an NVMe disk unbounded takes 5 GB or more. It leaves most of
    /// a modern disk's rate to the owner's other services, and is more than a coding agent's tools write.
    /// </summary>
    public const long WriteBps = 200L << 20;

    /// <summary>The controllers enabled below the root and below <see cref="AttemptsLeaf"/>, in <c>cgroup.subtree_control</c>'s own words: required.</summary>
    public static readonly IReadOnlyList<string> Controllers = ["pids", "memory"];

    /// <summary>The io controller, enabled beside <see cref="Controllers"/> where it is delegated: optional, and said when it is not (<see cref="IoGap"/>).</summary>
    public const string IoController = "io";

    static AttemptCgroups _current = Unavailable("TradeAgent has not yet looked for the cgroup its service delegates to it");

    /// <summary>
    /// What this process holds: set once by <see cref="ForThisProcess"/> at the app's start, and read by every launch that is
    /// not handed a root of its own (<see cref="Open"/>, the test seam). Never read from the environment.
    /// </summary>
    public static AttemptCgroups Current => Volatile.Read(ref _current);

    readonly bool _standIn;

    AttemptCgroups(string? root, string? refusal, bool standIn = false, Bounds? bounds = null)
    {
        Root = root;
        Refusal = refusal;
        _standIn = standIn;
        Attempts = root is null ? null : Path.Combine(root, AttemptsLeaf);
        Io = bounds?.Io;
        IoGap = root is null ? null : bounds is null ? "no write ceiling was looked for" : bounds.IoGap;
        SwapGap = bounds?.SwapGap;
    }

    /// <summary>The delegated root, or null when there is none (<see cref="Refusal"/> says why).</summary>
    public string? Root { get; }

    /// <summary>The parent every attempt is made beneath (<see cref="AttemptsLeaf"/> under <see cref="Root"/>), or null with no root.</summary>
    public string? Attempts { get; }

    /// <summary>The home's disk as <c>io.max</c> names it (<c>major:minor</c>), when the write rate is bounded; null otherwise (<see cref="IoGap"/>).</summary>
    public string? Io { get; }

    /// <summary>Why no attempt's write rate is bounded here — the io controller not delegated, or the home on no one block device — or null when it is.</summary>
    public string? IoGap { get; }

    /// <summary>Why no attempt's swap is bounded here (this kernel accounts none per cgroup: no <c>memory.swap.max</c>), or null when it is.</summary>
    public string? SwapGap { get; }

    /// <summary>What the parent's set-up found: the disk <c>io.max</c> names, or why none; why swap is not bounded, or null.</summary>
    internal sealed record Bounds(string? Io, string? IoGap, string? SwapGap);

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
    internal static AttemptCgroups StandIn(string directory, string? io = null, bool swap = true)
    {
        Directory.CreateDirectory(directory);
        var full = Path.GetFullPath(directory);
        var attempts = Path.Combine(full, AttemptsLeaf);
        Directory.CreateDirectory(attempts);
        foreach (var (file, value) in ParentCeilings(swap, io))
            File.WriteAllText(Path.Combine(attempts, file), value);
        return new AttemptCgroups(full, null, standIn: true,
            new Bounds(io, io is null ? "the stand-in names no disk" : null, swap ? null : NoSwapAccounting));
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

        return Bound(root);
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
        return Bound(full);
    }

    /// <summary>
    /// THE ROOT'S CONTROLLERS ENABLED AND THE ONE PARENT OF ALL ATTEMPTS MADE WITH ITS CEILINGS (<c>U-cell-ceilings</c> item 2): <c>+pids +memory</c> (and
    /// <c>+io</c> where it is delegated) below the root, then <see cref="AttemptsLeaf"/> with its <c>memory.max</c>, <c>memory.high</c>, <c>pids.max</c>,
    /// <c>memory.swap.max</c> and <c>io.max</c>, and the same controllers below it. A ceiling the kernel will not take is an <see cref="Unavailable"/> naming
    /// it — except swap where this kernel accounts none, and writes where io is not delegated or the home is on no one block device: those are gaps, said.
    /// </summary>
    static AttemptCgroups Bound(string root)
    {
        if (Enable(root, out var io, out var ioGap) is { } why) return Unavailable(why);
        var attempts = Path.Combine(root, AttemptsLeaf);
        try
        {
            Directory.CreateDirectory(attempts);
            if (Procs(attempts) is not { Count: 0 })
                return Unavailable($"the cgroup {attempts} holds processes, so it cannot hand controllers down to the attempts");
            var swap = File.Exists(Path.Combine(attempts, "memory.swap.max"));
            if (io is not null)
            {
                var line = IoLine(io);
                try
                {
                    Write(Path.Combine(attempts, "io.max"), line);
                    if (!ReadsBack(Read(Path.Combine(attempts, "io.max")), io)) (io, ioGap) = (null, $"{attempts}/io.max does not read back {line}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    (io, ioGap) = (null, $"the kernel would not bound writes ({line}) ({ex.Message})");
                }
            }
            foreach (var (file, value) in ParentCeilings(swap, io))
                if (file != "io.max") Write(Path.Combine(attempts, file), value);
            if (Read(Path.Combine(attempts, "pids.max")) != SumPidsMax.ToString(CultureInfo.InvariantCulture))
                return Unavailable($"{attempts}/pids.max does not read back {SumPidsMax}");
            if (Read(Path.Combine(attempts, "memory.max")) != SumMemoryMax.ToString(CultureInfo.InvariantCulture))
                return Unavailable($"{attempts}/memory.max does not read back {SumMemoryMax}");
            if (swap && Read(Path.Combine(attempts, "memory.swap.max")) != SwapMax.ToString(CultureInfo.InvariantCulture))
                return Unavailable($"{attempts}/memory.swap.max does not read back {SwapMax}");
            var below = Controllers.Concat(io is null ? [] : [IoController]).ToList();
            Write(Path.Combine(attempts, "cgroup.subtree_control"), string.Join(' ', below.Select(c => "+" + c)));
            var enabled = (Read(Path.Combine(attempts, "cgroup.subtree_control")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (below.FirstOrDefault(c => !enabled.Contains(c)) is { } missing)
                return Unavailable($"the cgroup {attempts} enabled {string.Join(" ", enabled)} and not {missing} for the attempts");
            return new AttemptCgroups(root, null, bounds: new Bounds(io, ioGap, swap ? null : NoSwapAccounting));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable($"TradeAgent could not make the cgroup {attempts}, the one parent of every attempt, with its ceilings ({ex.Message})");
        }
    }

    const string NoSwapAccounting =
        "this kernel accounts no swap per cgroup (no memory.swap.max), so an attempt's pages may still be swapped out at its memory.max";

    /// <summary>The parent's ceilings, file by file, as <see cref="Bound"/> writes them: the SUM of all attempts.</summary>
    internal static IEnumerable<(string File, string Value)> ParentCeilings(bool swap, string? io)
    {
        yield return ("memory.max", SumMemoryMax.ToString(CultureInfo.InvariantCulture));
        yield return ("memory.high", HighOf(SumMemoryMax).ToString(CultureInfo.InvariantCulture));
        yield return ("pids.max", SumPidsMax.ToString(CultureInfo.InvariantCulture));
        if (swap) yield return ("memory.swap.max", SwapMax.ToString(CultureInfo.InvariantCulture));
        if (io is not null) yield return ("io.max", IoLine(io));
    }

    /// <summary>One attempt's ceilings, file by file, as <see cref="Create"/> writes them.</summary>
    internal IEnumerable<(string File, string Value)> AttemptCeilings()
    {
        yield return ("pids.max", PidsMax.ToString(CultureInfo.InvariantCulture));
        yield return ("memory.max", MemoryMax.ToString(CultureInfo.InvariantCulture));
        yield return ("memory.high", MemoryHigh.ToString(CultureInfo.InvariantCulture));
        if (SwapGap is null) yield return ("memory.swap.max", SwapMax.ToString(CultureInfo.InvariantCulture));
        if (Io is not null) yield return ("io.max", IoLine(Io));
    }

    /// <summary>What <c>io.max</c> is told for the disk <paramref name="device"/>: its write rate, the rest left at the kernel's <c>max</c>.</summary>
    internal static string IoLine(string device) => $"{device} wbps={WriteBps.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether an <c>io.max</c> read back names <paramref name="device"/> with the write rate (the kernel prints every key, <c>max</c> for the rest).</summary>
    public static bool ReadsBack(string? ioMax, string device) =>
        (ioMax ?? "").Split('\n').Any(l => l.StartsWith(device + " ", StringComparison.Ordinal) &&
                                            l.Split(' ').Contains("wbps=" + WriteBps.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// A NEW ATTEMPT'S CGROUP, empty, with its ceilings written: <c>attempt-&lt;id&gt;-&lt;8 hex&gt;</c> under the root (the
    /// suffix keeps two launches of one attempt id apart). Throws <see cref="TradeAgentException"/>
    /// (<see cref="ErrorCode.CONTAINMENT_UNAVAILABLE"/>) when it cannot be made — the caller decides what that refuses.
    /// </summary>
    public AttemptCgroup Create(string? attemptId)
    {
        if (Root is null) throw new InvalidOperationException("no delegated cgroup is held: " + Refusal);
        var dir = Path.Combine(Attempts!, AttemptPrefix + Safe(attemptId) + "-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var made = new AttemptCgroup(dir, _standIn) { Ceilings = Ceilings };
            foreach (var (file, value) in AttemptCeilings())
                if (_standIn) File.WriteAllText(Path.Combine(dir, file), value);
                else Write(Path.Combine(dir, file), value);
            if (_standIn) return made;
            if (Read(Path.Combine(dir, "pids.max")) != PidsMax.ToString(CultureInfo.InvariantCulture))
                throw new IOException($"{dir}/pids.max does not read back {PidsMax}");
            if (SwapGap is null && Read(Path.Combine(dir, "memory.swap.max")) != SwapMax.ToString(CultureInfo.InvariantCulture))
                throw new IOException($"{dir}/memory.swap.max does not read back {SwapMax}");
            if (Io is not null && !ReadsBack(Read(Path.Combine(dir, "io.max")), Io))
                throw new IOException($"{dir}/io.max does not read back {IoLine(Io)}");
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
            try { attempts = Directory.EnumerateDirectories(Attempts!, AttemptPrefix + "*").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { attempts = []; left.Add($"{Attempts} ({ex.Message})"); }
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

    /// <summary>One line for a Mechanism row: an attempt's ceilings, all attempts' together, and what is not bounded here and why.</summary>
    public string Ceilings =>
        $"pids.max {PidsMax}, memory.max {MemoryMax / (1024 * 1024)} MiB (half of this computer's memory or {MemoryCap >> 30} GiB, the less), " +
        $"memory.high {MemoryHigh / (1024 * 1024)} MiB, " +
        (SwapGap is null ? "memory.swap.max 0, " : $"swap not bounded ({SwapGap}), ") +
        (Io is not null ? $"io.max writes {WriteBps >> 20} MiB/s on the home's disk {Io}; " : $"writes not bounded ({IoGap}); ") +
        $"all attempts together pids.max {SumPidsMax} and memory.max {SumMemoryMax / (1024 * 1024)} MiB (half of this computer's memory)";

    /// <summary>
    /// ONE ATTEMPT'S <c>memory.max</c>: half of MemTotal or <see cref="MemoryCap"/>, the less, in whole pages — the kernel keeps <c>memory.max</c> in pages
    /// and reads back a byte count rounded down to one (run 38970487186) — or, where MemTotal cannot be read, 1 GiB, which the Mechanism line then names.
    /// </summary>
    public static long MemoryMax { get; } = Pages(Math.Min(ReadMemoryMax(), MemoryCap));

    /// <summary>One attempt's <c>memory.high</c>: four fifths of <see cref="MemoryMax"/>, in whole pages.</summary>
    public static long MemoryHigh { get; } = HighOf(MemoryMax);

    /// <summary>ALL ATTEMPTS' <c>memory.max</c> together (the parent's): half of MemTotal in whole pages, or 1 GiB where it cannot be read.</summary>
    public static long SumMemoryMax { get; } = ReadMemoryMax();

    /// <summary>Four fifths of <paramref name="max"/>, in whole pages: a <c>memory.high</c>.</summary>
    internal static long HighOf(long max) => Pages(max / 5 * HighFifths);

    static long Pages(long bytes)
    {
        var page = Math.Max(Environment.SystemPageSize, 1);
        return bytes / page * page;
    }

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

    /// <summary>
    /// Writes <c>+pids +memory</c> to the root's <c>subtree_control</c> and reads both back; why not, or null. Then <c>+io</c> on its own, where the root
    /// is offered it and the home is on one block device: <paramref name="io"/> is that disk when it took, and <paramref name="ioGap"/> says why not
    /// otherwise — never a refusal.
    /// </summary>
    static string? Enable(string root, out string? io, out string? ioGap)
    {
        (io, ioGap) = (null, null);
        if (EnableRequired(root) is { } why) return why;
        var control = Path.Combine(root, "cgroup.subtree_control");
        var offered = (Read(Path.Combine(root, "cgroup.controllers")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!offered.Contains(IoController))
        {
            ioGap = $"the io controller is not delegated to {root} (offered: {string.Join(" ", offered)})";
            return null;
        }
        if (BlockDevice.Of(Paths.Home, out var notOne) is not { } device)
        {
            ioGap = notOne;
            return null;
        }
        try { Write(control, "+" + IoController); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ioGap = $"the cgroup {root} would not enable io for its attempts ({ex.Message})";
            return null;
        }
        if (!(Read(control) ?? "").Split(' ').Contains(IoController)) ioGap = $"the cgroup {root} did not enable io for its attempts";
        else io = device;
        return null;
    }

    static string? EnableRequired(string root)
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

    /// <summary>The attempt's directory, beneath the delegated root's parent of attempts.</summary>
    public string Dir { get; }

    /// <summary>What its ceilings are, for its Mechanism line (<see cref="AttemptCgroups.Ceilings"/> when it was made).</summary>
    public string Ceilings { get; init; } = "";

    /// <summary>Route (a)'s harness leaf, <c>&lt;attempt&gt;/harness</c> (made by <see cref="MakeLeaves"/>).</summary>
    public string Harness => Path.Combine(Dir, AttemptCgroups.HarnessLeaf);

    /// <summary>Route (a)'s cell leaf, <c>&lt;attempt&gt;/cell</c> (made by <see cref="MakeLeaves"/>).</summary>
    public string Cell => Path.Combine(Dir, AttemptCgroups.CellLeaf);

    /// <summary>
    /// MAKES ROUTE (a)'S TWO LEAVES, empty, before anything of the attempt starts (<c>U-cell-linux-c</c> item 3). No controller is
    /// enabled beneath the attempt, so the attempt's own ceilings hold both leaves together and neither is a cgroup with processes and
    /// controllers. Throws <see cref="TradeAgentException"/> (<see cref="ErrorCode.CONTAINMENT_UNAVAILABLE"/>) when either cannot be made.
    /// </summary>
    public void MakeLeaves()
    {
        try
        {
            Directory.CreateDirectory(Harness);
            Directory.CreateDirectory(Cell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE,
                $"TradeAgent could not make route (a)'s harness and cell leaves under {Dir} ({ex.Message})", ex);
        }
    }

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

/// <summary>
/// THE DISK A PATH'S FILESYSTEM LIVES ON, as <c>io.max</c> names it (<c>U-cell-ceilings</c> item 3): the <c>major:minor</c> of the whole block device, read
/// from <c>/proc/self/mountinfo</c> (the mount holding the path, by whole components) and <c>/sys/dev/block</c> (a partition's disk is its parent: the
/// kernel bounds a disk, never a partition). Null, and why, for a filesystem on no one block device — btrfs, overlay, tmpfs and network filesystems
/// report a device numbered 0 — or one sysfs does not list.
/// </summary>
public static class BlockDevice
{
    /// <summary>The whole disk under <paramref name="path"/>, or null with <paramref name="why"/>. Never throws.</summary>
    public static string? Of(string path, out string why)
    {
        why = "";
        try
        {
            var real = CellNative.RealPath(path) ?? Path.GetFullPath(path);
            if (Mount(File.ReadLines("/proc/self/mountinfo"), real) is not { } m)
            {
                why = $"no mount of /proc/self/mountinfo holds {real}";
                return null;
            }
            if (m.Device.StartsWith("0:", StringComparison.Ordinal))
            {
                why = $"the home's filesystem at {m.Point} is {m.Type} on no one block device ({m.Device}), so no disk's write rate can be bounded for it";
                return null;
            }
            var sys = "/sys/dev/block/" + m.Device;
            if (!Directory.Exists(sys))
            {
                why = $"the home's filesystem at {m.Point} ({m.Type}) is on device {m.Device}, which /sys/dev/block does not list";
                return null;
            }
            if (!File.Exists(Path.Combine(sys, "partition"))) return m.Device;
            var disk = Path.GetDirectoryName(CellNative.RealPath(sys) ?? sys);
            if (disk is not null && File.Exists(Path.Combine(disk, "dev")) && File.ReadAllText(Path.Combine(disk, "dev")).Trim() is { Length: > 0 } dev)
                return dev;
            why = $"the home's filesystem at {m.Point} is on partition {m.Device}, whose disk could not be read";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            why = $"the home's disk could not be read ({ex.Message})";
            return null;
        }
    }

    /// <summary>One mount: its point, its filesystem's type and its device's <c>major:minor</c>.</summary>
    public sealed record Mounted(string Point, string Type, string Device);

    /// <summary>
    /// THE MOUNT HOLDING <paramref name="path"/> in <c>mountinfo</c>'s lines: the deepest mount point that is <paramref name="path"/> or one of its parents by
    /// whole components, the last such line where a point is mounted over (the kernel lists a later mount after the one it hides). Fields: id, parent,
    /// <c>major:minor</c>, root, point (octal escapes), options, optional fields to a lone <c>-</c>, then the type.
    /// </summary>
    public static Mounted? Mount(IEnumerable<string> mountinfo, string path)
    {
        Mounted? best = null;
        foreach (var line in mountinfo)
        {
            var f = line.Split(' ');
            var dash = Array.IndexOf(f, "-");
            if (f.Length < 7 || dash < 6 || dash + 1 >= f.Length) continue;
            var point = Unescape(f[4]);
            var under = point == "/" || path == point || path.StartsWith(point + "/", StringComparison.Ordinal);
            if (under && (best is null || point.Length >= best.Point.Length)) best = new Mounted(point, f[dash + 1], f[2]);
        }
        return best;
    }

    /// <summary>mountinfo's octal escapes (<c>\040</c> for a space, <c>\011</c>, <c>\012</c>, <c>\134</c>) read back.</summary>
    static string Unescape(string field)
    {
        var b = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
            if (field[i] == '\\' && i + 3 < field.Length && field.Substring(i + 1, 3).All(c => c is >= '0' and <= '7'))
            {
                b.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else b.Append(field[i]);
        return b.ToString();
    }
}
