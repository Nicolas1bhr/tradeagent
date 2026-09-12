using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Security;

/// <summary>What a peer must be to hold a grant on the agent pipe. Null fields mean "not recorded".</summary>
/// <param name="ExpectedPath">TradeAgent's own <c>trade</c>, where this app deployed it.</param>
/// <param name="ExpectedHash">The SHA-256 of the CLI this app deployed, lowercase hex.</param>
/// <param name="ToolsDir">The managed AI-runtime folder. Nothing under it may ever be the peer.</param>
/// <param name="WorkspaceDir">The agent's own tree. Nothing under it may ever be the peer either.</param>
public sealed record PeerRule(string? ExpectedPath, string? ExpectedHash, string? ToolsDir, string? WorkspaceDir);

/// <summary>
/// WHO IS ON THE OTHER END OF THE AGENT PIPE, decided the way the ATAS bridge already decides it
/// (<c>BridgePipeAuth.ImageVerdict</c>): the kernel is asked which process holds the handle, and a
/// rule this codebase owns says whether that program may.
///
/// The point is the same one the bridge makes. A token is something a peer HAS, and everything on
/// the agent's side of the fence can read the agent's environment; the image is something a peer IS.
/// So the strong claim — "I am the Operations Director's launch" — is accepted only from TradeAgent's
/// own <c>trade</c>, at the path this app deployed it to, hashing to what this app deployed. A copy
/// of <c>trade.exe</c> the agent made in its workspace is a different program by this rule even
/// though it is byte-identical, because the agent can rewrite a file it owns and cannot rewrite one
/// under the app's bin.
///
/// The verdict is a pure function so it can be tested on any platform; the kernel call is Windows
/// only, and off Windows the caller gets null and must treat it as "not identified" rather than
/// "fine" — which is why the rule is only ENFORCED where a <see cref="PeerRule"/> was configured.
/// </summary>
public static class PeerImage
{
    /// <summary>
    /// Null when a program at <paramref name="actualPath"/> may hold a grant on this pipe, and the
    /// reason it may not otherwise.
    ///
    /// The order matters and is deliberate. The two forbidden FOLDERS come first, ahead of the
    /// recorded path, because they are the rule that does not depend on a record: a runtime that had
    /// rewritten TradeAgent's record to name itself would satisfy "you are what we recorded" and
    /// still fail "you are not allowed to run from there".
    /// </summary>
    public static string? Verdict(string? actualPath, string? actualHash, PeerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(actualPath))
            return "the operating system would not say which program is holding this connection, so " +
                   "the caller could not be identified at all";

        if (!string.IsNullOrWhiteSpace(rule.ToolsDir) && Inside(actualPath!, rule.ToolsDir!))
            return $"the program holding this connection ({Show(actualPath)}) runs from the managed " +
                   "AI-runtime folder. No AI runtime may present a launch grant";

        if (!string.IsNullOrWhiteSpace(rule.WorkspaceDir) && Inside(actualPath!, rule.WorkspaceDir!))
            return $"the program holding this connection ({Show(actualPath)}) runs from inside the " +
                   "agent's own workspace, which the agent can write to";

        if (string.IsNullOrWhiteSpace(rule.ExpectedPath))
            return "TradeAgent did not record where its own trade command is, so the caller could not be checked";

        if (!Same(actualPath!, rule.ExpectedPath!))
            return $"the program holding this connection is {Show(actualPath)}, but TradeAgent's own " +
                   $"trade command is {Show(rule.ExpectedPath)}";

        if (string.IsNullOrWhiteSpace(rule.ExpectedHash))
            return "TradeAgent did not record which trade command it installed, so the caller could not be checked";

        if (string.IsNullOrWhiteSpace(actualHash))
            return $"the program at {Show(actualPath)} could not be read back, so it could not be checked " +
                   "against the one TradeAgent installed";

        if (!string.Equals(actualHash, rule.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            return $"the program at {Show(actualPath)} is not the trade command TradeAgent installed";

        return null;
    }

    /// <summary>
    /// The image path of the process that dialled in, as the kernel reports it. Null off Windows and
    /// null when the query fails — both of which the caller must read as "not identified".
    /// </summary>
    public static string? ClientPath(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? ImagePathOf(pid) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The hash of a file on disk, or null when it cannot be read.
    ///
    /// Cached on (path, length, last write) because this runs once per connection and the file is a
    /// whole CLI. A swap changes at least one of the three, and a swap that somehow changed none of
    /// them still fails the PATH rule when it is anywhere but the app's own bin.
    /// </summary>
    public static string? HashOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists) return null;
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

            lock (Cache)
                if (Cache.TryGetValue(key, out var known)) return known;

            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = Core.Sha256Hex.Of(stream);

            lock (Cache)
            {
                if (Cache.Count > 32) Cache.Clear();
                Cache[key] = hash;
            }
            return hash;
        }
        catch (Exception) { return null; }
    }

    static readonly Dictionary<string, string> Cache = [];

    [SupportedOSPlatform("windows")]
    static string? ImagePathOf(uint pid)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION and QueryFullProcessImageName, the same pair the bridge
        // uses and for the same reason: reading another process's module list fails across a
        // bitness boundary, and this call does not care.
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            var size = (uint)buf.Length;
            return QueryFullProcessImageName(h, 0, buf, ref size) && size > 0
                ? new string(buf, 0, (int)size)
                : null;
        }
        finally { CloseHandle(h); }
    }

    static string Norm(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');

    static bool Same(string a, string b) =>
        string.Equals(Norm(a), Norm(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool Inside(string path, string dir)
    {
        var d = Norm(dir);
        if (d.Length == 0) return false;
        var p = Norm(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.StartsWith(d + "/", cmp);
    }

    /// <summary>A path on its way to a refusal message: one line, printable, and short.</summary>
    static string Show(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "'<unknown>'"
            : "'" + new string(path!.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim() + "'";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
