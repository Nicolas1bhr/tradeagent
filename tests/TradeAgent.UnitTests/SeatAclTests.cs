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

    /// <summary>
    /// The fact needs a file this account owns but cannot change the list of — on Windows, its owner denied that right. AN
    /// ADMINISTRATOR'S TOKEN (CI's windows runners) changes the list past that deny, so there the file is not unprivatisable and the
    /// preparation rightly makes it private: tried on a throwaway file first, and skipped with that reason when the lock does not hold.
    /// </summary>
    bool needsWindowsLock;

    public bool NeedsWindowsLock
    {
        get => needsWindowsLock;
        set
        {
            needsWindowsLock = value;
            if (value && Skip is null && OperatingSystem.IsWindows() && !SeatAclTests.WindowsLockHolds())
                Skip = "this Windows account changes a file's access list past a deny on its owner's right to (an administrator's token, as " +
                       "on CI's windows runners), so no file of its own is unprivatisable here; the row runs on a standard account (the tests box)";
        }
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
    /// (f) AN ACCESS ENTRY ON ANY EXISTING FILE OF THE SEAT HOME DOES NOT SURVIVE A PREPARATION (the review's P1; CRED-001) — a session
    /// transcript deep under <c>codex/sessions</c> and a file in <c>home</c>, not only the credential: on Windows an explicit entry on a
    /// file outlives its folder's protected list, and traverse checking is bypassed by default, so <c>Everyone: Read</c> on a transcript
    /// stays readable by path. A file that is a link is stepped over, never followed: what it points to keeps its entry. Red at base:
    /// the entry is still listed.
    /// </summary>
    [SeatAclFact("an access entry of a file's own outlives its folder's list on macOS and Windows; Linux files keep no ACL a 0700 folder lets through",
        "macos", "windows")]
    public void An_access_entry_on_an_existing_session_file_does_not_survive_preparation()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(An_access_entry_on_an_existing_session_file_does_not_survive_preparation));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var transcript = Path.Combine(Directory.CreateDirectory(Path.Combine(seat.Root, "codex", "sessions", "2026", "10")).FullName,
            "rollout-2026-10-16.jsonl");
        var note = Path.Combine(seat.Root, "home", "notes.txt");
        var outside = Path.Combine(Path.GetDirectoryName(exe)!, "outside.txt");
        foreach (var file in new[] { transcript, note, outside }) File.WriteAllText(file, "{}");
        foreach (var file in new[] { transcript, note, outside }) OpenFileToEveryone(file);
        var link = Path.Combine(seat.Root, "codex", "sessions", "linked.jsonl");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(link, outside);

        SeatProfile.Prepare(seat);

        foreach (var file in new[] { transcript, note })
            Assert.True(Granted(file).Length == 0, $"{file} still grants: {string.Join(" | ", Granted(file))}");
        Assert.NotEmpty(Granted(outside));
    }

    /// <summary>
    /// (d) A SEAT-HOME FILE THAT CANNOT BE MADE PRIVATE REFUSES THE SEAT IN WORDS, AND NOTHING IS STARTED: an <c>auth.json</c> others
    /// may read that this account cannot change — locked (<c>chflags uchg</c>) on macOS; on Windows, its owner denied the right to
    /// change its list. Red at base: the file is never looked at, and the seat is taken.
    /// </summary>
    [SeatAclFact("a file of this account's own it cannot make private needs a lock flag (macOS) or a deny entry (Windows); Linux has neither without root",
        "macos", "windows", NeedsWindowsLock = true)]
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

    /// <summary>
    /// (g) A CREDENTIAL OR CONFIG PATH THAT LEADS OUT OF THE SEAT HOME IS REFUSED BEFORE ANYTHING IS CHANGED, ANYWHERE (the review's P2;
    /// CRED-001, refuse never guess): <c>config_file</c> and <c>private_files</c> are manifest data a <c>runtimes.json</c> entry
    /// replaces whole, so an absolute path or a <c>..</c> one would otherwise have its mode — on Windows its access list — changed before
    /// the later look at its folder refused it: the owner's own files are never touched. The outside file keeps its mode, list and
    /// bytes, the seat home its mode, and the refusal names the entry and the repair. Red at base: the file is made 0600 (a new list).
    /// </summary>
    [Theory]
    [InlineData("config", true)]
    [InlineData("config", false)]
    [InlineData("private", true)]
    [InlineData("private", false)]
    public void A_seat_file_path_outside_the_seat_home_is_refused_with_nothing_changed(string which, bool absolute)
    {
        var (codex, exe, _) = SeatHomeTests.Seat($"outside-{which}-{absolute}");
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var outside = Path.Combine(Path.GetDirectoryName(exe)!, "owners-own.toml");
        File.WriteAllText(outside, "model = \"owner\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(outside, (UnixFileMode)0b110_100_100);
            File.SetUnixFileMode(seat.Root, (UnixFileMode)0b111_101_101);
        }
        var before = State(outside);
        var rootBefore = State(seat.Root);
        var rel = absolute ? outside : "../owners-own.toml";
        if (which == "config") seat.ConfigFile = rel;
        else seat.PrivateFiles = [.. seat.PrivateFiles, rel];

        var thrown = Record.Exception(() => SeatProfile.Prepare(seat));

        Assert.Equal(before, State(outside));
        Assert.Equal("model = \"owner\"\n", File.ReadAllText(outside));
        Assert.Equal(rootBefore, State(seat.Root));
        var refused = Assert.IsType<TradeAgentException>(thrown);
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, refused.Code);
        Assert.Contains(rel, refused.Message, StringComparison.Ordinal);
        Assert.Contains(which == "config" ? "config_file" : "private_files", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written or started", refused.Message, StringComparison.Ordinal);

        // Its mode and, on Windows, its owner and access list: what a preparation would have changed.
        static string State(string path) => OperatingSystem.IsWindows() ? Sddl(path) : File.GetUnixFileMode(path).ToString();
    }

    /// <summary>
    /// (g′) A CREDENTIAL PATH WHOSE FOLDER IS A LINK OUT OF THE SEAT HOME IS REFUSED, AND WHAT THE LINK POINTS TO KEEPS ITS MODE: every
    /// component from the root is looked at, not only the last (the review's P2). The walk steps over the linked folder; at base the
    /// credential pass then changed the mode of the owner's file through it. Linux and macOS (a Windows link needs a privilege).
    /// </summary>
    [Fact]
    public void A_credential_path_through_a_linked_folder_is_refused_with_its_target_unchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_credential_path_through_a_linked_folder_is_refused_with_its_target_unchanged));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var owners = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "owners-codex")).FullName;
        var auth = Path.Combine(owners, "auth.json");
        File.WriteAllText(auth, "{}");
        File.SetUnixFileMode(auth, (UnixFileMode)0b110_100_100);
        var link = Path.Combine(seat.Root, "codex", "linked");
        Directory.CreateSymbolicLink(link, owners);
        seat.PrivateFiles = [.. seat.PrivateFiles, "codex/linked/auth.json"];

        var thrown = Record.Exception(() => SeatProfile.Prepare(seat));

        Assert.Equal((UnixFileMode)0b110_100_100, File.GetUnixFileMode(auth));
        var refused = Assert.IsType<TradeAgentException>(thrown);
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, refused.Code);
        Assert.Contains(link, refused.Message, StringComparison.Ordinal);
        Assert.Contains("is a link", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (i) A CREDENTIAL PATH THROUGH A LINKED FOLDER IS REFUSED BEFORE ANYTHING IS CHANGED, INSIDE THE SEAT HOME TOO (the re-review's P2):
    /// every component of every manifest path is looked at before the root's mode, the walk or any list is changed. The root keeps its
    /// 0755 and, on macOS, a session file its access entry. Linux and macOS (a Windows link needs a privilege). Red at base: the root is
    /// made 0700 before the refusal.
    /// </summary>
    [Fact]
    public void A_credential_path_through_a_linked_folder_is_refused_before_anything_is_changed()
    {
        if (OperatingSystem.IsWindows()) return;
        var (codex, exe, _) = SeatHomeTests.Seat("linked-folder-before-any-change");
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var owners = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "owners-codex")).FullName;
        var link = Path.Combine(seat.Root, "codex", "linked");
        Directory.CreateSymbolicLink(link, owners);
        seat.PrivateFiles = [.. seat.PrivateFiles, "codex/linked/auth.json"];
        var transcript = Path.Combine(Directory.CreateDirectory(Path.Combine(seat.Root, "codex", "sessions")).FullName, "rollout.jsonl");
        File.WriteAllText(transcript, "{}");
        if (OperatingSystem.IsMacOS()) OpenFileToEveryone(transcript);
        File.SetUnixFileMode(seat.Root, (UnixFileMode)0b111_101_101);

        var thrown = Record.Exception(() => SeatProfile.Prepare(seat));

        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(seat.Root));
        if (OperatingSystem.IsMacOS()) Assert.NotEmpty(Granted(transcript));
        var refused = Assert.IsType<TradeAgentException>(thrown);
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, refused.Code);
        Assert.Contains(link, refused.Message, StringComparison.Ordinal);
        Assert.Contains("is a link", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// (h) A SESSION FILE HARD-LINKED FROM OUTSIDE THE SEAT HOME IS REFUSED BEFORE ANYTHING IS CHANGED, AND THE OUTSIDE FILE KEEPS ITS
    /// ACCESS ENTRY (the re-review's P1; CRED-001): a hard link is no link to <c>IsLink</c> — it is the same file under a second name,
    /// so a same-user process can give the owner's own <c>~/.codex/auth.json</c> a name under <c>sessions/</c>, and clearing that name's
    /// entries clears his. The seat home's root keeps its mode too: the check runs before any change. Red at base: the outside file's
    /// entry is gone, and the seat is taken.
    /// </summary>
    [SeatAclFact("only macOS and Windows change a session file's own access entries; a Linux preparation never touches one", "macos", "windows")]
    public void A_session_file_hard_linked_from_outside_is_refused_with_its_target_unchanged()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_session_file_hard_linked_from_outside_is_refused_with_its_target_unchanged));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var outside = Path.Combine(Path.GetDirectoryName(exe)!, "owners-auth.json");
        File.WriteAllText(outside, "{}");
        OpenFileToEveryone(outside);
        var granted = Granted(outside);
        var linked = Path.Combine(Directory.CreateDirectory(Path.Combine(seat.Root, "codex", "sessions", "2026", "10")).FullName,
            "rollout-2026-10-16.jsonl");
        HardLink(outside, linked);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(seat.Root, (UnixFileMode)0b111_101_101);
        var rootBefore = State(seat.Root);

        var thrown = Record.Exception(() => SeatProfile.Prepare(seat));

        Assert.Equal(granted, Granted(outside));
        Assert.Equal(rootBefore, State(seat.Root));
        HardLinkRefused(thrown, linked);
    }

    /// <summary>
    /// (h′) A CREDENTIAL FILE HARD-LINKED FROM OUTSIDE THE SEAT HOME IS REFUSED, AND THE OUTSIDE FILE KEEPS ITS MODE AND ITS LIST — on
    /// every platform, Linux included: <c>codex/auth.json</c> is made 0600 (on Windows, left with no entry of its own), and a second name
    /// for the owner's own file would carry that change to him. Red at base: the outside file is made 0600 (a new list).
    /// </summary>
    [Fact]
    public void A_credential_file_hard_linked_from_outside_is_refused_with_its_target_unchanged()
    {
        var (codex, exe, _) = SeatHomeTests.Seat(nameof(A_credential_file_hard_linked_from_outside_is_refused_with_its_target_unchanged));
        var seat = codex.Seat!;
        SeatProfile.Prepare(seat);
        var outside = Path.Combine(Path.GetDirectoryName(exe)!, "owners-auth.json");
        File.WriteAllText(outside, "{}");
        if (OperatingSystem.IsWindows()) OpenFileToEveryone(outside);
        else File.SetUnixFileMode(outside, (UnixFileMode)0b110_100_100);
        var before = State(outside);
        var auth = Path.Combine(seat.Root, "codex", "auth.json");
        HardLink(outside, auth);

        var thrown = Record.Exception(() => SeatProfile.Prepare(seat));

        Assert.Equal(before, State(outside));
        HardLinkRefused(thrown, auth);
    }

    static void HardLinkRefused(Exception? thrown, string path)
    {
        var refused = Assert.IsType<TradeAgentException>(thrown);
        Assert.Equal(ErrorCode.AI_SEAT_REFUSED, refused.Code);
        Assert.Contains(path, refused.Message, StringComparison.Ordinal);
        Assert.Contains("hard link", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written or started", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A second name <paramref name="linked"/> for <paramref name="file"/>: <c>ln</c>, or <c>CreateHardLinkW</c> on Windows.</summary>
    static void HardLink(string file, string linked)
    {
        if (!OperatingSystem.IsWindows()) Tool("/bin/ln", file, linked);
        else Assert.True(CreateHardLinkW(linked, file, IntPtr.Zero),
            $"CreateHardLinkW {linked}: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        Assert.True(File.Exists(linked));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    /// <summary>Its mode (Linux, macOS) or its owner and access list (Windows): what a preparation would change.</summary>
    static string State(string path) => OperatingSystem.IsWindows() ? Sddl(path) : File.GetUnixFileMode(path).ToString();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string Sddl(string path)
    {
        const System.Security.AccessControl.AccessControlSections ownerAndAccess =
            System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access;
        return Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(ownerAndAccess)
            : new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(ownerAndAccess);
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

    /// <summary>Gives Everyone read on <paramref name="file"/> by an entry of its own, and checks that it took.</summary>
    static void OpenFileToEveryone(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(file);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(Everyone,
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else Tool("/bin/chmod", "+a", "everyone allow read", file);
        Assert.NotEmpty(Granted(file));
    }

    /// <summary>What <paramref name="file"/> grants beyond this account: its ACL entries (macOS), or entries naming another account (Windows).</summary>
    static string[] Granted(string file) => OperatingSystem.IsWindows() ? Foreign(file) : AclEntries(file);

    /// <summary>Opens <paramref name="file"/> to others and makes that unchangeable by this account.</summary>
    static void Lock(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            LockOnWindows(file);
            Assert.NotEmpty(Foreign(file));
            return;
        }
        File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);
        Tool("/usr/bin/chflags", "uchg", file);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void LockOnWindows(string file)
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
    }

    /// <summary>
    /// WINDOWS: whether <see cref="LockOnWindows"/> holds against this account — a throwaway file locked so, then its explicit
    /// entries purged and written back as the preparation does: refused means it holds. Removed afterwards either way.
    /// </summary>
    internal static bool WindowsLockHolds() => !OperatingSystem.IsWindows() || ProbeLock();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool ProbeLock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tradeagent-tests", $"lock-probe-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "probe.json");
        try
        {
            File.WriteAllText(file, "{}");
            LockOnWindows(file);
            var info = new FileInfo(file);
            var acl = info.GetAccessControl();
            foreach (var who in acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                         .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference).Distinct().ToList())
                acl.PurgeAccessRules(who);
            try { info.SetAccessControl(acl); return false; }
            catch (UnauthorizedAccessException) { return true; }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static void Unlock(string file)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(file)) return;
        Tool("/usr/bin/chflags", "nouchg", file);
    }
}
