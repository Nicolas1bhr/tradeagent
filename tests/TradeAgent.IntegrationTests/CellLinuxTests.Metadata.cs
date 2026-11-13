using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE ATTRIBUTES OF WHAT A CELL DOES NOT OWN (<c>U-cell-metadata</c>; R29 F-D1; containment): Landlock has no right for a mode, an owner,
/// a time, an extended attribute or an inode flag, so before this unit a cell running as the owner's uid could change all of them on
/// anything the uid owns, granted or not. Every row goes through the product's own stage, and the truth is read from OUTSIDE the cell.
/// </summary>
public partial class CellLinuxTests
{
    /// <summary>
    /// EVERY VARIANT OF THE FAMILY, BY ITS OWN SYSTEM CALL NUMBER, so that what is measured is the call and not the one libc happens to
    /// choose. Each prints <c>name target ok</c> or <c>name target errno=N</c>. The same script is the control outside any cell: a variant
    /// that fails there proves nothing here, so the row asserts it works there.
    /// </summary>
    const string MetadataProbe = """
        import ctypes, fcntl, os, platform, struct, sys
        libc = ctypes.CDLL(None, use_errno=True)
        libc.syscall.restype = ctypes.c_long
        X64 = platform.machine() == "x86_64"
        N = dict(fchmod=91, fchmodat=268, fchown=93, fchownat=260, utimensat=280, setxattr=188, lsetxattr=189, fsetxattr=190,
                 removexattr=197, lremovexattr=198, fremovexattr=199) if X64 else \
            dict(fchmod=52, fchmodat=53, fchown=55, fchownat=54, utimensat=88, setxattr=5, lsetxattr=6, fsetxattr=7,
                 removexattr=14, lremovexattr=15, fremovexattr=16)
        N.update(fchmodat2=452, setxattrat=463, removexattrat=466, file_setattr=469)
        if X64: N.update(chmod=90, chown=92, lchown=94, utime=132, utimes=235, futimesat=261)
        AT_FDCWD, AT_SYMLINK_NOFOLLOW, AT_EMPTY_PATH = -100, 0x100, 0x1000
        O_PATH = 0o10000000
        gid = os.getegid()
        stamp = 1000000000
        def L(v): return ctypes.c_long(v)
        def B(v): return ctypes.c_char_p(v if isinstance(v, bytes) else os.fsencode(v))
        def times(n):
            global stamp
            stamp += 1
            return (ctypes.c_long * n)(*([stamp, stamp] if n == 2 else [stamp, 0, stamp, 0]))
        def say(name, tag, r):
            print(f"{name} {tag} ok" if r == 0 else f"{name} {tag} errno={ctypes.get_errno()}", flush=True)
        def call(name, tag, nr, *args):
            ctypes.set_errno(0)
            say(name, tag, libc.syscall(L(nr), *args))
        ACL = struct.pack("<I", 2) + b"".join(struct.pack("<HHI", t, p, 0xFFFFFFFF) for t, p in ((1, 7), (4, 4), (32, 0)))

        def by_path(tag, p):
            mode = 0o700
            def next_mode():
                nonlocal mode
                mode = 0o700 if mode == 0o750 else 0o750
                return L(mode)
            if X64:
                call("chmod", tag, N["chmod"], B(p), next_mode())
                call("chown", tag, N["chown"], B(p), L(-1), L(gid))
                call("lchown", tag, N["lchown"], B(p), L(-1), L(gid))
                call("utime", tag, N["utime"], B(p), times(2))
                call("utimes", tag, N["utimes"], B(p), times(4))
                call("futimesat", tag, N["futimesat"], L(AT_FDCWD), B(p), times(4))
            call("fchmodat", tag, N["fchmodat"], L(AT_FDCWD), B(p), next_mode())
            call("fchmodat2", tag, N["fchmodat2"], L(AT_FDCWD), B(p), next_mode(), L(0))
            call("fchownat", tag, N["fchownat"], L(AT_FDCWD), B(p), L(-1), L(gid), L(0))
            call("utimensat", tag, N["utimensat"], L(AT_FDCWD), B(p), times(4), L(0))
            call("setxattr", tag, N["setxattr"], B(p), B("user.cell"), B("x"), L(1), L(0))
            call("lsetxattr", tag, N["lsetxattr"], B(p), B("user.cell"), B("x"), L(1), L(0))
            call("setxattr-acl", tag, N["setxattr"], B(p), B("system.posix_acl_access"), ctypes.c_char_p(ACL), L(len(ACL)), L(0))
            call("removexattr", tag, N["removexattr"], B(p), B("user.p1"))
            call("lremovexattr", tag, N["lremovexattr"], B(p), B("user.p2"))
            args = struct.pack("<QII", ctypes.addressof(val := ctypes.create_string_buffer(b"x")), 1, 0)
            call("setxattrat", tag, N["setxattrat"], L(AT_FDCWD), B(p), L(0), B("user.cell"), ctypes.c_char_p(args), L(len(args)))
            call("removexattrat", tag, N["removexattrat"], L(AT_FDCWD), B(p), L(0), B("user.p3"))
            attr = bytes(24)
            call("file_setattr", tag, N["file_setattr"], L(AT_FDCWD), B(p), ctypes.c_char_p(attr), L(len(attr)), L(0))
            # THE SAME OBJECT THROUGH AN O_PATH DESCRIPTOR, WHICH LANDLOCK DOES NOT MEDIATE: AT_EMPTY_PATH, and its /proc/self/fd spelling.
            fd = os.open(p, O_PATH)
            call("fchmodat2-empty", tag, N["fchmodat2"], L(fd), B(""), next_mode(), L(AT_EMPTY_PATH))
            call("fchownat-empty", tag, N["fchownat"], L(fd), B(""), L(-1), L(gid), L(AT_EMPTY_PATH))
            call("utimensat-empty", tag, N["utimensat"], L(fd), B(""), times(4), L(AT_EMPTY_PATH))
            call("fchmodat-procfd", tag, N["fchmodat"], L(AT_FDCWD), B(f"/proc/self/fd/{fd}"), next_mode())
            os.close(fd)

        def by_fd(tag, p):
            # A READ-ONLY DESCRIPTOR IS ALL A GRANT OF READ GIVES: none of these calls asks more of it than that it is open.
            fd = os.open(p, os.O_RDONLY)
            call("fchmod", tag, N["fchmod"], L(fd), L(0o750))
            call("fchown", tag, N["fchown"], L(fd), L(-1), L(gid))
            call("futimens", tag, N["utimensat"], L(fd), None, times(4), L(0))
            call("fsetxattr", tag, N["fsetxattr"], L(fd), B("user.cell"), B("x"), L(1), L(0))
            call("fremovexattr", tag, N["fremovexattr"], L(fd), B("user.p4"))
            FS_IOC_GETFLAGS, FS_IOC_SETFLAGS, FS_IOC_FSGETXATTR, FS_IOC_FSSETXATTR = 0x80086601, 0x40086602, 0x801C581F, 0x401C5820
            def ioctl(name, cmd, buf):
                try:
                    fcntl.ioctl(fd, cmd, buf); print(f"{name} {tag} ok", flush=True)
                except OSError as e:
                    print(f"{name} {tag} errno={e.errno}", flush=True)
            flags = bytearray(8)
            try:
                fcntl.ioctl(fd, FS_IOC_GETFLAGS, flags)
            except OSError:
                pass
            ioctl("ioctl-setflags", FS_IOC_SETFLAGS, struct.pack("<Q", struct.unpack("<Q", bytes(flags))[0] | 0x80))
            attr = bytearray(28)
            try:
                fcntl.ioctl(fd, FS_IOC_FSGETXATTR, attr)
            except OSError:
                pass
            ioctl("ioctl-fssetxattr", FS_IOC_FSSETXATTR, struct.pack("<I", struct.unpack_from("<I", attr)[0] | 0x80) + bytes(attr[4:]))
            # EXT4_IOC_SETVERSION, both widths (U-cell-ceilings; A4 N1): a generation set through a read-only descriptor moves the file's ctime to now.
            ioctl("ioctl-ext4-setversion", 0x40086604, struct.pack("<Q", 7))
            ioctl("ioctl-ext4-setversion32", 0x40046604, struct.pack("<I", 7))
            os.close(fd)

        def hard_link(tag, p):
            # ONE NAME IS ALL THE SUPERVISOR JUDGES: a second name inside the workspace for what lies outside it would pass.
            try:
                os.link(p, os.path.join(os.path.dirname(os.path.abspath(sys.argv[0])), "hard-" + tag)); print(f"link {tag} ok", flush=True)
            except OSError as e:
                print(f"link {tag} errno={e.errno}", flush=True)

        for kind, tag, p in zip(sys.argv[1::3], sys.argv[2::3], sys.argv[3::3]):
            by_path(tag, p)
            if kind == "granted": by_fd(tag, p)
            if tag not in ("outside-dir", "via-symlink"): hard_link(tag, p)
        print("probe-done", flush=True)
        """;

    /// <summary>What the probe's targets are, read from outside: every attribute a variant could move, ctime with them.</summary>
    const string MetadataSnapshot = """
        import os, sys
        for p in sys.argv[1:]:
            s = os.stat(p)
            names = sorted(os.listxattr(p))
            print(p, oct(s.st_mode), s.st_uid, s.st_gid, s.st_mtime_ns, s.st_ctime_ns, names, sep=" | ")
        """;

    /// <summary>The call failed for a reason of the host, not of a cell: the kernel or the filesystem has not got it, so this host cannot show it.</summary>
    static readonly string[] HostHasNoSuchCall = ["errno=38", "errno=25", "errno=95"];

    sealed record MetadataWorld(World W, Kit.View Kit, CellPolicy Policy, string[] Probe, string[] Paths)
    {
        public static MetadataWorld Make(string name)
        {
            var w = new World(name);
            var kit = KitFixture.Installed(w.Root);
            var cellHome = Path.Combine(w.Root, "cell-home");
            Directory.CreateDirectory(cellHome);
            var input = Path.Combine(cellHome, "in.txt");
            File.WriteAllText(input, "granted to read");
            var outside = Path.Combine(w.Root, "outside");
            Directory.CreateDirectory(outside);
            var file = Path.Combine(outside, "file");
            File.WriteAllText(file, "not granted");
            var ssh = Path.Combine(w.Home, ".ssh", "id_x");
            var tool = kit.ToolPath(KitFixture.Tool);
            // THE KIT WRITABLE BY ITS MODE, AS THE KIT'S OWN ROW HAS IT: a user attribute needs write permission, and a denial must be the cell's.
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(tool, File.GetUnixFileMode(tool) | UnixFileMode.UserWrite);
            var led = Path.Combine(w.Ws, "leads-out");
            File.CreateSymbolicLink(led, file);
            foreach (var p in new[] { input, file, outside, ssh, tool })
                foreach (var attr in new[] { "user.p1", "user.p2", "user.p3", "user.p4" }) Plant(p, attr);
            var policy = RouteA.Policy(w.Ws, "/usr/bin/python3", "/usr/bin", cellHome, cellHome, kit);
            return new(w, kit, policy,
                ["outside", "outside-file", file, "outside", "outside-dir", outside, "outside", "ssh-key", ssh, "outside", "via-symlink", led,
                 "granted", "kit-tool", tool, "granted", "read-input", input],
                [file, outside, ssh, tool, input]);
        }

        static void Plant(string path, string attr)
        {
            var ran = Outside("/usr/bin/python3", "-I", "-c", $"import os, sys; os.setxattr(sys.argv[1], '{attr}', b'planted')", path).GetAwaiter().GetResult();
            Require(ran.Code == 0, $"user extended attributes on the test home's filesystem ({ran.Err.Trim()})");
        }
    }

    /// <summary>
    /// NO MODE, OWNER, TIME, EXTENDED ATTRIBUTE OR FLAG MOVES ON ANYTHING BUT THE WORKSPACE (F-D1). In a route (a) cell under the product's
    /// policy, every variant of the family on a file and a folder no grant names, on the owner's key, through a workspace symlink leading
    /// out, and on the two things a cell may only read — the kit's tool and an input, through the read-only descriptor a grant of read
    /// gives. Each variant is shown to work outside the cell first, so a denial here is the cell's. EPERM from every one, and the
    /// snapshot taken from outside is unchanged byte for byte.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_moves_no_attribute_of_anything_outside_its_workspace()
    {
        PlatformIsACells();
        var control = MetadataWorld.Make("meta-ctl");
        var script = control.W.Script("metadata.py", MetadataProbe);
        var outside = await Outside("/usr/bin/python3", ["-I", script, .. control.Probe]);
        _output.WriteLine("--- control, outside any cell ---\n" + outside.Out + outside.Err);
        Says(outside, "probe-done");
        var lines = outside.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l != "probe-done").ToList();
        // A NOFOLLOW VARIANT AIMED AT THE SYMLINK ITSELF MEETS THE SYMLINK, NOT WHAT IT LEADS TO: not a control for what it leads to.
        var wrong = lines.Where(l => !l.EndsWith(" ok", StringComparison.Ordinal) && !HostHasNoSuchCall.Any(l.EndsWith) && !l.Contains(" via-symlink ", StringComparison.Ordinal)).ToList();
        Assert.True(wrong.Count == 0, "the control did not show these variants working outside a cell, so a denial proves nothing:\n" + string.Join('\n', wrong));

        var m = MetadataWorld.Make("meta");
        var inCell = m.W.Script("metadata.py", MetadataProbe);
        var snap = m.W.Script("snapshot.py", MetadataSnapshot);
        var before = await Outside("/usr/bin/python3", ["-I", snap, .. m.Paths]);
        var ran = await InCell(m.Policy, "/usr/bin/python3", ["-I", inCell, .. m.Probe]);
        var after = await Outside("/usr/bin/python3", ["-I", snap, .. m.Paths]);
        _output.WriteLine("--- in the cell ---\n" + ran.Out + ran.Err);
        _output.WriteLine("--- before ---\n" + before.Out + "--- after ---\n" + after.Out);
        Says(ran, "probe-done");

        var open = ran.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.EndsWith(" ok", StringComparison.Ordinal)).ToList();
        Assert.True(open.Count == 0, $"these calls were let through in the cell ({open.Count}):\n" + string.Join('\n', open));
        Assert.Equal(before.Out, after.Out);
        var refused = ran.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l != "probe-done").ToList();
        // EPERM FROM EVERY ONE, AND ONE KIND OF NOT-FOUND: /proc/self/fd/<n> is the SUPERVISOR's table when the supervisor reads it, so it either refuses a
        // magic link (EPERM) or finds no such descriptor (ENOENT). Neither reaches the object, and the snapshot above says so.
        Assert.All(refused, l => Assert.True(l.EndsWith(" errno=1", StringComparison.Ordinal) || (l.StartsWith("fchmodat-procfd ", StringComparison.Ordinal) && l.EndsWith(" errno=2", StringComparison.Ordinal))
            || (l.StartsWith("link ", StringComparison.Ordinal) && l.EndsWith(" errno=18", StringComparison.Ordinal)), l));

        // THE KIT'S HASH COVERS ITS RUN BITS: one cell's flip would have withdrawn the kit from every other cell.
        Assert.Equal(m.Kit.Hash, Kit.HashOf(m.Kit.Dir));
    }

    /// <summary>
    /// A CELL'S OWN WORKSPACE STAYS ITS OWN (the other half of the same row): what is shut outside is NOT shut inside, because the tools a cell
    /// runs depend on it — <c>chmod +x</c> and a run, <c>touch</c>, <c>cp -p</c>, an archive's times and modes, <c>git</c>, <c>sed -i</c>,
    /// Python's <c>copy2</c>. Each changes what it says it changes, read from outside. A filter that refuses the family outright goes red
    /// here (and in the executor's and package rows), which is why the family is judged by what it would touch and not refused by number.
    /// </summary>
    [LinuxFact]
    public async Task A_cells_own_workspace_keeps_its_modes_times_and_tools()
    {
        PlatformIsACells();
        if (!OperatingSystem.IsLinux()) return;
        var w = new World("meta-ws");
        var script = w.Script("own.sh", """
            set -u
            export HOME="$PWD"
            printf '#!/bin/sh\necho ran-it\n' > tool.sh && chmod +x tool.sh && ./tool.sh
            echo x > a && chmod 600 a && echo chmod-rc=$?
            touch fresh && echo touch-rc=$?
            touch -d 2001-09-09T01:46:40Z a && echo touch-d-rc=$?
            cp -p a a.copy && echo cp-p-rc=$?
            mkdir -p tree/sub && echo y > tree/sub/f && chmod 755 tree/sub/f && tar cf t.tar tree && mkdir out && tar xpf t.tar -C out && echo tar-rc=$?
            ln -s tool.sh lnk && touch -h -d 2001-09-09T01:46:40Z lnk && echo touch-h-rc=$?
            git init -q repo && cd repo && git config user.email x@example.invalid && git config user.name x && echo f > f && git add f && git commit -qm one && echo git-rc=$?
            cd .. ; sed -i s/x/z/ a && echo sed-rc=$?
            echo fds=$(ls /proc/self/fd | tr '\n' ' ')
            # A PROCESS WHOSE PARENT HAS GONE: said by the row, not asserted, because the answer is Yama's and a host's to set.
            echo o > orphan && (sh -c 'sleep 0.2; chmod 640 orphan; echo orphan-chmod-rc=$? > orphan.said; python3 -I -c "
            import os
            fd = os.open(\"orphan\", os.O_RDWR); os.fchmod(fd, 0o600); print(\"orphan-fchmod-rc=0\")" >> orphan.said 2>&1' &)
            for i in 1 2 3 4 5 6 7 8 9 10; do grep -q fchmod orphan.said 2>/dev/null && break; sleep 0.2; done; cat orphan.said
            python3 -I -c "
            import os, shutil, stat
            shutil.copy2('tool.sh', 'tool.copy')
            fd = os.open('a', os.O_RDWR)
            os.fchmod(fd, 0o640)
            os.utime(fd, (1000000000, 1000000000))
            os.close(fd)
            # A FILE UNLINKED WHILE OPEN, AND ONE THAT NEVER HAD A NAME: what lies in the workspace is the workspace's.
            fd = os.open('gone', os.O_RDWR | os.O_CREAT); os.unlink('gone'); os.fchmod(fd, 0o600); os.utime(fd); os.close(fd)
            fd = os.open('.', os.O_RDWR | os.O_TMPFILE, 0o600); os.fchmod(fd, 0o640); os.close(fd)
            # AN ABSOLUTE PATH IGNORES ITS DIRECTORY, HOWEVER BAD THAT IS.
            import ctypes
            libc = ctypes.CDLL(None, use_errno=True); libc.syscall.restype = ctypes.c_long
            said = libc.syscall(ctypes.c_long(268 if os.uname().machine == 'x86_64' else 53), ctypes.c_long(-1), ctypes.c_char_p(os.path.abspath('a').encode()), ctypes.c_long(0o640))
            print('absolute-rc=' + str(said), flush=True)
            print('python-rc=0', flush=True)
            "
            """);
        var ran = await InCell(w.Policy(), "/bin/sh", script);
        _output.WriteLine(ran.Out + ran.Err);
        Says(ran, "ran-it", "chmod-rc=0", "touch-rc=0", "touch-d-rc=0", "cp-p-rc=0", "tar-rc=0", "touch-h-rc=0", "git-rc=0", "sed-rc=0", "python-rc=0", "absolute-rc=0");
        // NOTHING OF THE CHANNEL OR THE LISTENER IS INHERITED: a listener in the cell's hands would let it answer its own calls.
        Says(ran, "fds=0 1 2 3");
        var a = Path.Combine(w.Ws, "a");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(a));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000000000).UtcDateTime, File.GetLastWriteTimeUtc(a));
        Assert.True((File.GetUnixFileMode(Path.Combine(w.Ws, "tool.copy")) & UnixFileMode.UserExecute) != 0, "copy2 lost the run bit");
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(w.Ws, "tool.sh")), File.GetLastWriteTimeUtc(Path.Combine(w.Ws, "tool.copy")));
        Assert.True((File.GetUnixFileMode(Path.Combine(w.Ws, "out", "tree", "sub", "f")) & UnixFileMode.UserExecute) != 0, "tar lost the run bit");
        // THE LINK'S OWN TIME, read without following it: the file it leads to was written just now, and keeps that.
        Assert.Equal("1000000000\n", (await Outside("/usr/bin/stat", "-c", "%Y", Path.Combine(w.Ws, "lnk"))).Out);
        Assert.NotEqual(DateTimeOffset.FromUnixTimeSeconds(1000000000).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(w.Ws, "tool.sh")));
    }
}
