using System.Runtime.InteropServices;

namespace TradeAgent.Core;

/// <summary>
/// THE UNIX HALF OF "THE AGENT PROCESS DIES WITH THE APP", and it exists because .NET cannot ask for
/// it at the point where it would belong.
///
/// On Windows a Job Object holds every process a turn starts, whatever it does to its parent links,
/// and closing the job's handle kills the lot. The POSIX equivalent is a process GROUP: a grandchild
/// that detaches — its parent exits, the kernel reparents it to init — keeps the group it was born
/// in, so a group kill still reaches it while <c>Kill(entireProcessTree: true)</c>, which walks
/// parent links, cannot see it at all.
///
/// Measured on this Mac before any of this was written: <c>Process.Start</c> leaves the child in
/// TradeAgent's OWN process group (self pgid=39402, child pgid=39402), and the group is therefore
/// unkillable — the app is in it. <c>setpgid</c> from the parent after the child has exec'd fails, as
/// POSIX says it must. There is no <c>ProcessStartInfo</c> flag for a new session.
///
/// So the child is started through a launcher that IS ours: TradeAgent's own <c>trade</c>, invoked
/// with <see cref="Flag"/>, which calls <c>setsid()</c> and then <c>execv()</c>s the real command.
/// After exec the process keeps its pid, so the pid the app already holds IS the session and group
/// leader, and <c>kill(-pid)</c> is the whole teardown. Measured the same day: child pgid = sid =
/// its own pid, a detached grandchild at ppid 1 still carried that group, and the group kill took it.
///
/// It is not a sandbox and this file does not pretend otherwise: a process that wants out can call
/// <c>setsid</c> itself, in one line of any scripting language on the machine. That is why the
/// Doctor says "OS sandbox: NONE" in the owner's own words and why the live configuration refuses to
/// start an uncontained runtime at all. What this buys is that a turn TradeAgent cancelled is over —
/// including the parts of it that stopped answering to their parent.
/// </summary>
public static class ContainedLaunch
{
    /// <summary>
    /// The launcher's flag. Deliberately a flag on <c>trade</c> rather than a verb: verbs map to
    /// gateway operations, and this one never opens the pipe, reads the token or touches the
    /// gateway. It grants nothing either — it execs a program its caller could already exec, as the
    /// same user, in the same session it would have inherited anyway.
    /// </summary>
    public const string Flag = "--spawn-contained";

    /// <summary>
    /// The launcher itself, run inside whatever process was started with <see cref="Flag"/>.
    ///
    /// Returns false when this is an ordinary invocation, so the caller goes on to be the CLI. When
    /// it returns true the process is either already gone (exec succeeded, and this code no longer
    /// exists) or is about to exit with <paramref name="exitCode"/>.
    /// </summary>
    public static bool TryRun(IReadOnlyList<string> argv, TextWriter error, out int exitCode)
    {
        exitCode = 0;
        if (argv.Count == 0 || argv[0] != Flag) return false;

        if (argv.Count < 2)
        {
            error.WriteLine($"trade: {Flag} needs a program to run");
            exitCode = 2;
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            // Not a fallback: on Windows the app assigns the child to a Job Object, which is
            // stronger than a session and cannot be broken out of. A build that got here would be
            // running the agent WITHOUT the job while believing it was contained, and starting the
            // program anyway is the failure mode this refusal exists to prevent.
            error.WriteLine($"trade: {Flag} is a macOS/Linux launcher; on Windows the app uses a Job Object");
            exitCode = 2;
            return true;
        }

        if (Posix.setsid() < 0)
        {
            error.WriteLine($"trade: could not start a new session (errno {Marshal.GetLastPInvokeError()})");
            exitCode = 126;
            return true;
        }

        // From here this process is its own session and group leader, and so is everything it goes
        // on to start. execv keeps the pid, so the handle the app already holds names the group.
        var command = argv.Skip(1).ToArray();
        Posix.Exec(command[0], command);

        error.WriteLine($"trade: could not run {command[0]} (errno {Marshal.GetLastPInvokeError()})");
        exitCode = 127;
        return true;
    }
}

/// <summary>The three calls the launcher and the teardown need. Nothing here runs on Windows.</summary>
public static class Posix
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    internal static extern int getsid(int pid);

    [DllImport("libc", SetLastError = true)]
    internal static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    internal static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true, EntryPoint = "execv")]
    static extern int execv(IntPtr path, IntPtr argv);

    const int SIGKILL = 9;

    /// <summary>The session id of a process, or -1 when it cannot be read (including on Windows).</summary>
    public static int SessionOf(int pid)
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getsid(pid); }
        catch (Exception) { return -1; }
    }

    /// <summary>The process group of a process, or -1 when it cannot be read (including on Windows).</summary>
    public static int GroupOf(int pid)
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getpgid(pid); }
        catch (Exception) { return -1; }
    }

    /// <summary>Whether a process is still there. False on Windows, where this is not the question asked.</summary>
    public static bool Alive(int pid)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return kill(pid, 0) == 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// SIGKILL to a whole process group, named by its leader's pid. The negative pid IS the group:
    /// <c>kill(-pid)</c> reaches every process in it, including the ones that have been reparented
    /// away from anything the app could walk to.
    ///
    /// Refuses to kill the group TradeAgent is itself in, which would take the app down with the
    /// turn. That is not a defensive nicety: before the launcher existed the child WAS in this
    /// group, so a build that lost the launcher and kept the group kill would shut itself down on
    /// the first Stop.
    /// </summary>
    public static bool KillGroup(int group)
    {
        if (OperatingSystem.IsWindows() || group <= 1) return false;
        try
        {
            if (getpgid(Environment.ProcessId) == group) return false;
            return kill(-group, SIGKILL) == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Becomes <paramref name="path"/>. Returns only on failure — on success this process no longer
    /// exists to return into. The argument array is NUL-terminated by hand because that is the shape
    /// <c>execv</c> reads, and UTF-8 because that is what a POSIX path is.
    /// </summary>
    internal static void Exec(string path, IReadOnlyList<string> argv)
    {
        var strings = new IntPtr[argv.Count + 1];
        for (var i = 0; i < argv.Count; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
        strings[^1] = IntPtr.Zero;

        var block = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
        Marshal.Copy(strings, 0, block, strings.Length);
        var file = Marshal.StringToCoTaskMemUTF8(path);
        execv(file, block);
    }
}
