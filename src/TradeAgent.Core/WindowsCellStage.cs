using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>The Windows cell slots' count, readable on every platform (a profile names it).</summary>
public static class WindowsCellSlots
{
    /// <summary>One AppContainer profile per lane the app can run at once: equal to <c>LanePolicy.MaxLanes</c>, asserted by a test.</summary>
    public const int Count = 8;
}

/// <summary>
/// THE WINDOWS CELL STAGE (<c>U-cell-windows</c> item 2): <c>trade --spawn-contained --cell &lt;policy&gt; &lt;program&gt; [args]</c> on Windows.
/// Linux's stage confines ITSELF and becomes the program; Windows has no exec, so this one stays outside — TradeAgent's own code,
/// reading only the app's hash-named policy file — and starts the program in a cell (<see cref="AppContainerCell"/>): a SLOT's
/// AppContainer, exactly the folders the policy names granted to that slot's SID, a window station of its own, a kill-on-close job
/// without breakaway, its standard streams this stage's own passed through, its environment the policy's alone. It waits, exits with
/// the program's code, and takes back everything it granted.
///
/// <para><b>Slots, not one container.</b> An AppContainer's SID is what Windows grants a folder to, so two cells under one SID would
/// reach each other's workspaces. Each running cell holds one of <see cref="Slots"/> fixed profiles (one per lane the app can run at
/// once, <c>LanePolicy.MaxLanes</c>), by a lock file only it holds; the SIDs are fixed by name, so the one firewall rule set that
/// holds egress <c>web</c> to TCP 443 names all of them, made once. A slot is SWEPT before it is used and after: every grant its last
/// cell was given taken back (from the slot's own record, written before any grant), and the container's own storage — its
/// <c>AC</c> folder and registry — emptied, so nothing one cell leaves reaches the next.</para>
///
/// <para><b>Egress.</b> A policy of class <c>none</c> starts with no capability: no network at all. Class <c>web</c> adds
/// <c>internetClient</c> — which opens EVERY outbound port; port 443 alone is the firewall rule's, and a launch is admitted under
/// <c>web</c> only on a proof that measured the rule holding (<c>RouteA.Admission</c>). Any other class is refused here.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsCellStage
{
    /// <summary>How many cells may run at once: one profile each (<see cref="WindowsCellSlots.Count"/>).</summary>
    public const int Slots = WindowsCellSlots.Count;

    /// <summary>
    /// The profile names' prefix: <c>TradeAgent.Cell.&lt;home&gt;.</c>, the home part twelve hex digits of the SHA-256 of the app home's
    /// full path. A PROFILE IS THE ACCOUNT'S, the slots' locks are a home's: two homes of one account (a test's beside the app's) never
    /// share a SID, so a cell of one is never granted what a cell of the other was.
    /// </summary>
    public static string ProfilePrefix => "TradeAgent.Cell." + Sha256Hex.Of(Path.GetFullPath(Paths.Home).ToLowerInvariant())[..12] + ".";

    /// <summary>Deletes every slot's profile of this home (a test home's, at its end). Never throws; true when all are gone.</summary>
    public static bool DeleteProfiles() => Enumerable.Range(1, Slots).Select(i => AppContainerCell.DeleteProfile(ProfileName(i))).ToList().All(ok => ok);

    /// <summary>Exit code of a launch whose cell could not be made: nothing was started.</summary>
    public const int Failed = 126;

    /// <summary>Slot <paramref name="slot"/>'s profile name.</summary>
    public static string ProfileName(int slot) => ProfilePrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every slot's container SID, in slot order — what the egress rule names.</summary>
    public static IReadOnlyList<string> SlotSids() => [.. Enumerable.Range(1, Slots).Select(i => AppContainerCell.SidOf(ProfileName(i)))];

    /// <summary>Where the slots' locks and grant records live: the app's own cell folder, which no cell is ever granted.</summary>
    public static string SlotDirectory => Path.Combine(CellPolicy.Directory, "slots");

    /// <summary>
    /// Runs the stage. Returns the program's exit code, or <see cref="Failed"/> having said why on <paramref name="error"/> — and in that
    /// case nothing was started.
    /// </summary>
    public static int Run(string policyFile, string program, IReadOnlyList<string> arguments, TextWriter error)
    {
        int Refuse(string why)
        {
            error.WriteLine($"trade: the cell was not entered: {why}");
            error.Flush();
            CellStage.Discard(policyFile);
            return Failed;
        }

        if (CellPlatform.Read().Refusal() is { } refusal) return Refuse(refusal);
        CellPolicy policy;
        try
        {
            if (CellStage.AppOwned(policyFile) is { } notOurs) return Refuse(notOurs);
            policy = CellPolicy.Read(policyFile);
        }
        catch (Exception ex) when (ex is TradeAgentException or IOException or UnauthorizedAccessException) { return Refuse(ex.Message); }

        IReadOnlyList<string> caps;
        if (policy.Egress.Name == CellEgress.None.Name) caps = [];
        else if (policy.Egress.Name == CellEgress.Web.Name) caps = [AppContainerCell.InternetClient];
        else return Refuse($"a cell on Windows has egress none or web, and this policy names '{policy.Egress.Name}'");

        if (Resolve(program, policy) is not { } exe) return Refuse($"{program} is not a program at an absolute path or on the cell's PATH");

        using var slot = Slot.Claim();
        if (slot is null) return Refuse($"all {Slots} cell slots are in use");
        try
        {
            var sid = AppContainerCell.Profile(slot.Profile, "A TradeAgent cell: an AI's programs, confined");
            Sweep(slot, sid);
            Grants(slot, sid, policy);
            var env = policy.Environment;
            // WINDOWS REWRITES TEMP AND TMP to <LOCALAPPDATA>\Packages\<profile>\AC\Temp for an AppContainer (measured, 26200): where the
            // policy puts LOCALAPPDATA in the workspace, that folder is made so the cell's temporary files stay in the workspace too.
            if (env.FirstOrDefault(e => e.Key.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } local
                && CellPolicy.Within(Path.GetFullPath(local), Path.GetFullPath(policy.Workspace)))
                Directory.CreateDirectory(Path.Combine(local, "Packages", slot.Profile, "AC", "Temp"));

            using var input = new SafeFileHandle(GetStdHandle(-10), ownsHandle: false);
            using var output = new SafeFileHandle(GetStdHandle(-11), ownsHandle: false);
            using var err = new SafeFileHandle(GetStdHandle(-12), ownsHandle: false);
            error.Flush();
            using var cell = AppContainerCell.Start(exe, arguments, policy.Workspace, env, sid, caps, new AppContainerStdio(input, output, err));
            cell.WaitForExit(-1);
            return cell.ExitCode;
        }
        catch (TradeAgentException ex) { return Refuse(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Refuse(ex.Message); }
        finally
        {
            try { Sweep(slot, AppContainerCell.SidOf(slot.Profile)); }
            catch (Exception ex) when (ex is TradeAgentException or IOException or UnauthorizedAccessException) { error.WriteLine($"trade: the cell's slot was not swept: {ex.Message}"); }
            CellStage.Discard(policyFile);
        }
    }

    /// <summary>
    /// THE GRANTS, RECORDED FIRST: the slot's record names every path before any is granted, so a stage killed between the two leaves
    /// nothing the next sweep does not take back. The workspace read, write and delete; the read-only inputs and program roots read
    /// and run. Nothing else is granted — the system's own folders Windows grants every AppContainer already.
    /// </summary>
    static void Grants(Slot slot, string sid, CellPolicy policy)
    {
        var paths = new List<(string Path, FileSystemRights Rights)> { (policy.Workspace, FileSystemRights.Modify) };
        paths.AddRange(policy.ReadOnly.Concat(policy.Executable).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => Directory.Exists(p) || File.Exists(p)).Select(p => (p, FileSystemRights.ReadAndExecute)));
        OwnerOnlyFile.Write(slot.Record, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', paths.Select(p => p.Path))));
        foreach (var (path, rights) in paths) AppContainerCell.Grant(path, sid, rights);
    }

    /// <summary>
    /// TAKES BACK what slot <paramref name="slot"/>'s last cell was given and left: every path on its record revoked, the record removed,
    /// and the container's own storage emptied — the files beneath its <c>AC</c> folder and its registry storage.
    /// </summary>
    static void Sweep(Slot slot, string sid)
    {
        if (File.Exists(slot.Record))
        {
            foreach (var path in File.ReadAllLines(slot.Record).Where(l => l.Length > 0))
                try { AppContainerCell.Revoke(path, sid); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { /* a path gone grants nothing */ }
            File.Delete(slot.Record);
        }
        var ac = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", slot.Profile, "AC");
        if (Directory.Exists(ac))
            foreach (var top in Directory.EnumerateDirectories(ac))
            {
                foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use by a process the job already ended */ }
                foreach (var d in Directory.EnumerateDirectories(top))
                    try { Directory.Delete(d, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* as above */ }
            }
        using var storage = Registry.CurrentUser.OpenSubKey(StorageKey + sid, writable: true);
        if (storage is not null)
        {
            foreach (var sub in storage.GetSubKeyNames()) storage.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            foreach (var value in storage.GetValueNames()) storage.DeleteValue(value, throwOnMissingValue: false);
        }
    }

    /// <summary>Where Windows keeps an AppContainer's own registry, by SID, under the account's hive.</summary>
    const string StorageKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\";

    /// <summary>The program's absolute path: as given when it is one, else the first match on the policy's PATH (with <c>.exe</c> tried).</summary>
    static string? Resolve(string program, CellPolicy policy)
    {
        if (Path.IsPathFullyQualified(program)) return File.Exists(program) ? Path.GetFullPath(program) : null;
        var path = policy.Environment.FirstOrDefault(e => e.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified))
            foreach (var name in new[] { program, program + ".exe" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// <summary>One slot, held by an exclusive lock file for as long as this stage lives (Windows releases it if the stage dies).</summary>
    sealed class Slot : IDisposable
    {
        readonly FileStream _lock;

        Slot(int number, FileStream held)
        {
            Number = number;
            _lock = held;
        }

        public int Number { get; }

        public string Profile => ProfileName(Number);

        public string Record => Path.Combine(SlotDirectory, Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".grants");

        public static Slot? Claim()
        {
            Directory.CreateDirectory(SlotDirectory);
            for (var i = 1; i <= Slots; i++)
                try
                {
                    var held = new FileStream(Path.Combine(SlotDirectory, i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Slot(i, held);
                }
                catch (IOException) { /* held by another cell */ }
            return null;
        }

        public void Dispose() => _lock.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int which);
}
