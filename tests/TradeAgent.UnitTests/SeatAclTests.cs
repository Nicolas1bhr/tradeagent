using System.Diagnostics;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>A seat-home access-list fact that only one platform can state: run there, skipped elsewhere with the reason.</summary>
public sealed class SeatAclFactAttribute : FactAttribute
{
    public SeatAclFactAttribute(string why, params string[] platforms)
    {
        if (!platforms.Any(OperatingSystem.IsOSPlatform)) Skip = $"{string.Join(" and ", platforms)} only: {why}";
        // THE PIN NEEDS THE TOOL THAT WRITES A POSIX ACL; a runner without it has nothing to pin.
        else if (OperatingSystem.IsLinux() && !File.Exists("/usr/bin/setfacl") && !File.Exists("/bin/setfacl"))
            Skip = "setfacl is not installed on this Linux machine, so no POSIX ACL entry can be written to pin";
    }
}

/// <summary>
/// U-SEAT-ACL: THE SEAT HOME IS THIS ACCOUNT'S ALONE ON EVERY PLATFORM (CRED-001) — no access entry outlives "owner-only", on the
/// folders or on the files already in them. <c>U-seat-home</c> made every folder 0700, or on Windows gave the FOLDER a protected
/// owner-only list; its last review left two holes: macOS keeps extended ACL entries the mode bits do not touch (and copies an
/// inheritable one onto the <c>auth.json</c> codex writes at sign-in), and on Windows an EXISTING file's explicit entries survive a
/// new list on its folder. Linux needs nothing: 0700 sets the POSIX ACL mask to <c>---</c>, which voids every named entry — pinned.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class SeatAclTests
{
    const string Inheritable = "everyone allow read,file_inherit,directory_inherit";

    /// <summary>
    /// (a) AN EXTENDED ACL ENTRY ON ANY SEAT-HOME FOLDER, OR ON THE FILE THAT HOLDS THE SIGN-IN, DOES NOT SURVIVE A PREPARATION — the
    /// root, the two homes and a folder codex made deep under them. Red at base: the entry is still listed by <c>ls -le</c>, 0700 or not.
    /// </summary>
    [SeatAclFact("macOS keeps extended ACL entries apart from the mode bits", "macos")]
    public void A_macOS_acl_entry_on_a_seat_folder_does_not_survive_preparation()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (codex, _, _) = SeatHomeTests.Seat(nameof(A_macOS_acl_entry_on_a_seat_folder_does_not_survive_preparation));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var root = seat.Root;
        var deep = Path.Combine(root, "codex", "sessions", "2026", "10");
        Directory.CreateDirectory(deep);
        var auth = Path.Combine(root, "codex", "auth.json");
        File.WriteAllText(auth, "{}");
        File.SetUnixFileMode(auth, (UnixFileMode)0b110_100_100);

        string[] all = [root, Path.Combine(root, "codex"), Path.Combine(root, "home"), deep, auth];
        foreach (var path in all)
        {
            Tool("/bin/chmod", "+a", path == auth ? "everyone allow read" : Inheritable, path);
            Assert.NotEmpty(AclEntries(path));
        }

        SeatProfile.Prepare(seat);

        foreach (var path in all)
            Assert.True(AclEntries(path).Length == 0, $"{path} still lists: {string.Join(" | ", AclEntries(path))}");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(auth));
    }

    /// <summary>
    /// (b) AN INHERITABLE macOS ENTRY ON THE CREDENTIAL'S FOLDER DOES NOT REACH A FILE OR FOLDER WRITTEN THERE AFTER THE PREPARATION —
    /// as codex writes <c>auth.json</c> at sign-in. Red at base: the new file lists the entry, inherited.
    /// </summary>
    [SeatAclFact("macOS copies an inheritable extended ACL entry onto what is created below it", "macos")]
    public void An_inheritable_macOS_acl_entry_does_not_reach_a_file_written_later()
    {
        var (codex, _, _) = SeatHomeTests.Seat(nameof(An_inheritable_macOS_acl_entry_does_not_reach_a_file_written_later));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var folder = Path.Combine(seat.Root, "codex");
        Tool("/bin/chmod", "+a", Inheritable, folder);

        SeatProfile.Prepare(seat);

        var auth = Path.Combine(folder, "auth.json");
        File.WriteAllText(auth, "{}");
        var sessions = Directory.CreateDirectory(Path.Combine(folder, "sessions")).FullName;
        foreach (var path in new[] { auth, sessions })
            Assert.True(AclEntries(path).Length == 0, $"{path} inherited: {string.Join(" | ", AclEntries(path))}");
    }

    /// <summary>
    /// (c) AN EXPLICIT GRANT ON AN EXISTING <c>codex/auth.json</c> — or on a protected folder below the root — DOES NOT SURVIVE A
    /// PREPARATION ON WINDOWS: only inherited entries follow a folder's new list. Red at base: Everyone still holds an entry there.
    /// </summary>
    [SeatAclFact("Windows keeps a file's explicit entries when its folder's list is replaced", "windows")]
    public void An_explicit_grant_on_an_existing_auth_json_does_not_survive_on_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (codex, _, _) = SeatHomeTests.Seat(nameof(An_explicit_grant_on_an_existing_auth_json_does_not_survive_on_Windows));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var auth = Path.Combine(seat.Root, "codex", "auth.json");
        File.WriteAllText(auth, "{}");
        var sessions = Directory.CreateDirectory(Path.Combine(seat.Root, "codex", "sessions", "2026")).FullName;

        var file = new FileInfo(auth);
        var fileAcl = file.GetAccessControl();
        fileAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(Everyone,
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        file.SetAccessControl(fileAcl);
        var folder = new DirectoryInfo(sessions);
        var folderAcl = folder.GetAccessControl();
        folderAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        folderAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(Everyone,
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.AccessControlType.Allow));
        folder.SetAccessControl(folderAcl);
        Assert.NotEmpty(Foreign(auth));
        Assert.NotEmpty(Foreign(sessions));

        SeatProfile.Prepare(seat);

        foreach (var path in new[] { auth, sessions })
            Assert.True(Foreign(path).Length == 0, $"{path} still grants: {string.Join(" | ", Foreign(path))}");
    }

    /// <summary>
    /// (d) A SEAT-HOME FILE THAT CANNOT BE MADE PRIVATE REFUSES THE SEAT IN WORDS, AND NOTHING IS STARTED: an <c>auth.json</c> others
    /// may read that this account cannot change — locked (<c>chflags uchg</c>) on macOS; on Windows, its owner denied the right to
    /// change its list. Red at base: the file is never looked at, and the seat is taken.
    /// </summary>
    [SeatAclFact("a file of this account's own it cannot make private needs a lock flag (macOS) or a deny entry (Windows); Linux has neither without root",
        "macos", "windows")]
    public async Task A_seat_home_file_that_cannot_be_made_private_refuses_the_seat_in_words()
    {
        var (codex, exe, dump) = SeatHomeTests.Seat(nameof(A_seat_home_file_that_cannot_be_made_private_refuses_the_seat_in_words));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var auth = Path.Combine(seat.Root, "codex", "auth.json");
        File.WriteAllText(auth, "{}");
        Lock(auth);
        try
        {
            var refused = Assert.Throws<TradeAgentException>(() => SeatProfile.Prepare(seat));
            Assert.Equal(ErrorCode.AI_SEAT_REFUSED, refused.Code);
            Assert.Contains(auth, refused.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be made private", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written or started", refused.Message, StringComparison.Ordinal);

            var detected = await new CliAgentRuntime(codex, presence: new AgentPresence(), seatProbe: new SeatHomeTests.FakeProbe()).DetectAsync();
            Assert.False(detected.Installed, $"a seat home whose {auth} others may read was taken");
            Assert.Contains(auth, detected.Reason, StringComparison.Ordinal);

            var workspace = Path.Combine(Path.GetDirectoryName(exe)!, "role-home");
            Directory.CreateDirectory(workspace);
            var verdict = SeatHomeTests.Session(codex, exe, workspace, new SeatHomeTests.FakeProbe()).CheckSeat();
            Assert.True(verdict.Refused);
            Assert.Equal(ErrorCode.AI_SEAT_REFUSED, verdict.Code);
            Assert.Contains(auth, verdict.Refusal, StringComparison.Ordinal);
            Assert.Empty(SeatHomeTests.Argv(dump));
        }
        finally { Unlock(auth); }
    }

    /// <summary>
    /// (e) LINUX, PINNED: a POSIX ACL entry naming another uid grants nothing after a preparation — 0700 sets the mask to <c>---</c>,
    /// and every named entry is masked by it. Green at base by design; a change that stops setting the group bits turns it red.
    /// Read with <c>getfacl -n</c>: by default it prints the uid's NAME (65534 is <c>nobody</c> on Ubuntu), never the number.
    /// </summary>
    [SeatAclFact("POSIX ACLs and their mask", "linux")]
    public void Linux_posix_acl_entries_grant_nothing_after_0700()
    {
        var (codex, _, _) = SeatHomeTests.Seat(nameof(Linux_posix_acl_entries_grant_nothing_after_0700));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var folder = Path.Combine(seat.Root, "codex");
        Tool("setfacl", "-m", "u:65534:rwx", folder);
        var before = Tool("getfacl", "-p", "-n", folder);
        Assert.StartsWith("user:65534:rwx", Named(before), StringComparison.Ordinal);
        Assert.DoesNotContain("#effective:", Named(before), StringComparison.Ordinal);
        Assert.Contains("mask::rwx", before, StringComparison.Ordinal);

        SeatProfile.Prepare(seat);

        var acl = Tool("getfacl", "-p", "-n", folder);
        Assert.Contains("mask::---", acl, StringComparison.Ordinal);
        Assert.StartsWith("user:65534:rwx", Named(acl), StringComparison.Ordinal);
        Assert.EndsWith("#effective:---", Named(acl), StringComparison.Ordinal);

        // The one line naming uid 65534: its entry, and what the mask leaves of it.
        static string Named(string acl) =>
            acl.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("user:65534:", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The extended ACL entries <c>ls -lde</c> lists for <paramref name="path"/> (macOS), e.g. <c>0: group:everyone allow read</c>.</summary>
    internal static string[] AclEntries(string path) =>
        [.. Tool("/bin/ls", "-lde", path).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2 && char.IsDigit(l[0]) && l.Contains(": ", StringComparison.Ordinal))];

    /// <summary>Runs a tool (tests and evidence may; the product never shells out for this) and returns what it printed; a failure fails.</summary>
    internal static string Tool(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {string.Join(' ', args)} exited {p.ExitCode}: {error}");
        return output;
    }

    static readonly System.Security.Principal.SecurityIdentifier Everyone =
        OperatingSystem.IsWindows() ? new(System.Security.Principal.WellKnownSidType.WorldSid, null) : null!;

    /// <summary>Every entry on <paramref name="path"/> naming anyone but this account, SYSTEM or the Administrators group (Windows).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string[] Foreign(string path)
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var ok = new HashSet<System.Security.Principal.SecurityIdentifier>
        {
            me.User!,
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        System.Security.AccessControl.FileSystemSecurity acl = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return
        [
            .. acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Where(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                            && !ok.Contains((System.Security.Principal.SecurityIdentifier)r.IdentityReference))
                .Select(r => $"{r.IdentityReference} {r.FileSystemRights}{(r.IsInherited ? " (inherited)" : "")}")
        ];
    }

    /// <summary>Opens <paramref name="file"/> to others and makes that unchangeable by this account.</summary>
    static void Lock(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(file);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(Everyone,
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            // OWNER RIGHTS (S-1-3-4) replaces the owner's implicit right to change the list; denied, the owner cannot.
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier("S-1-3-4"),
                System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
            Assert.NotEmpty(Foreign(file));
            return;
        }
        File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);
        Tool("/usr/bin/chflags", "uchg", file);
    }

    static void Unlock(string file)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(file)) return;
        Tool("/usr/bin/chflags", "nouchg", file);
    }
}
