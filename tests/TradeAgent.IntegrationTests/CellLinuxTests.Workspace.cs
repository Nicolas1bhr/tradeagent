using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// WHAT A CELL'S WORKSPACE TAKES IN, AND WHAT IT NEVER REACHES (<c>U-cell-linux-d</c> items 4 and 5): packages install into the
/// workspace from a loopback index and nothing outside it changes; the owner's inbox is no cell's to write, by the kernel and by the
/// policy's own refusal.
/// </summary>
public partial class CellLinuxTests
{
    /// <summary>
    /// A listing of everything beneath <paramref name="root"/> except <paramref name="except"/> (and what is beneath it): one line
    /// per entry, with a file's size and last write when <paramref name="detail"/>. Unreadable corners are skipped, not guessed.
    /// </summary>
    static SortedSet<string> Listing(string root, bool detail, params string[] except)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
        {
            if (except.Any(e => CellPolicy.Within(path, e))) continue;
            var info = new FileInfo(path);
            lines.Add(detail && (info.Attributes & FileAttributes.Directory) == 0
                ? $"{path} {info.Length} {info.LastWriteTimeUtc:O}"
                : path);
        }
        return lines;
    }

    /// <summary>
    /// Before and after a cell ran: the cell's HOME in full detail, everything beside its workspace in the world, and <c>/tmp</c>
    /// outside the test homes (where every world, and so the workspace, lives).
    /// </summary>
    sealed record Outside_(SortedSet<string> Home, SortedSet<string> Beside, SortedSet<string> Tmp);

    static Outside_ Snapshot(World w) =>
        new(Listing(w.Home, detail: true), Listing(w.Root, detail: true, w.Ws), Listing("/tmp", detail: false, TestEnv.Root));

    void Unchanged(Outside_ before, Outside_ after, string cellPid)
    {
        Assert.Equal(before.Home, after.Home);
        Assert.Equal(before.Beside, after.Beside);

        // A .NET RUNTIME'S OWN IPC FILES (dotnet-diagnostic-<pid>-…, clr-debug-pipe-<pid>-…) come and go with every .NET process on
        // the runner — the test hosts of the other assemblies, the launcher's supervisor — and are set aside by name. The cell
        // stage's (its pid is the cell's: execve keeps it) is named in the output: it became the program before it could remove it.
        static bool Runtimes(string p) =>
            Path.GetFileName(p).StartsWith("dotnet-diagnostic-", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("clr-debug-pipe-", StringComparison.Ordinal);
        var added = after.Tmp.Except(before.Tmp).ToList();
        foreach (var p in added.Where(p => Runtimes(p) && Path.GetFileName(p).Contains($"-{cellPid}-", StringComparison.Ordinal)))
            _output.WriteLine($"/tmp gained {p}: the cell stage's own runtime file, left by execve (pid {cellPid})");
        var changed = added.Concat(before.Tmp.Except(after.Tmp)).Where(p => !Runtimes(p)).ToList();
        Assert.True(changed.Count == 0, "outside the workspace, /tmp changed: " + string.Join(", ", changed));
    }

    /// <summary>The environment a package row's cell starts with: no TMPDIR or cache of the test's choosing — the product's <see cref="CellPolicy.ToolEnvironment"/>.</summary>
    static CellPolicy ToolPolicy(World w, CellEgress egress) =>
        new(w.Ws, [w.Ro], CellPolicy.SystemRoots,
            [new("PATH", CellPath), new("HOME", w.Home), new("LANG", "C.UTF-8"), .. CellPolicy.ToolEnvironment(w.Ws)], egress);

    /// <summary>The pid a row's shell printed as <c>cell-pid=N</c> — the cell stage's own, since it became the shell.</summary>
    static string CellPid(Ran ran) =>
        ran.Out.Split('\n').FirstOrDefault(l => l.StartsWith("cell-pid=", StringComparison.Ordinal))?["cell-pid=".Length..].Trim() ?? "none";

    /// <summary>
    /// ROW (f): A CELL INSTALLS A PYTHON PACKAGE INTO ITS WORKSPACE FROM A LOOPBACK INDEX — <c>pip install --target</c> from a PEP 503
    /// index on 127.0.0.1 serving a wheel the test built, its cache and temporary files in the workspace (the product's
    /// <see cref="CellPolicy.ToolEnvironment"/>), the import working inside the cell, and nothing outside the workspace changed.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_installs_a_python_package_into_its_workspace_from_a_loopback_index()
    {
        PlatformIsACells();
        var w = new World("pip");
        using var index = LoopbackIndex.Start(IPAddress.Loopback);
        index.ServePython(Wheel.Build("ta-cell-pkg", "1.0", "ta_cell_pkg", "VALUE = 'from-the-loopback-index'\n"));
        var site = Path.Combine(w.Ws, "site");
        var before = Snapshot(w);

        var ran = await InCell(ToolPolicy(w, CellEgress.ForTests([index.Port])), "/bin/sh", "-c",
            "echo cell-pid=$$; " +
            $"python3 -I -m pip install --no-input --retries 0 --timeout 20 --index-url http://127.0.0.1:{index.Port}/simple/ --target '{site}' ta-cell-pkg " +
            "> pip.log 2>&1; echo pip-rc=$?; tail -n 5 pip.log; " +
            $"python3 -I -c \"import sys; sys.path.insert(0, '{site}'); import ta_cell_pkg; print('imported-' + ta_cell_pkg.VALUE)\"");
        _output.WriteLine("row (f): " + ran.Out.Replace('\n', ' ') + " | requests " + string.Join(" ", index.Requests));

        Says(ran, "pip-rc=0", "imported-from-the-loopback-index");
        Assert.True(File.Exists(Path.Combine(site, "ta_cell_pkg", "__init__.py")));
        Assert.Contains("/files/ta_cell_pkg-1.0-py3-none-any.whl", index.Requests);
        Unchanged(before, Snapshot(w), CellPid(ran));
    }

    /// <summary>
    /// ROW (g): A CELL INSTALLS AN npm PACKAGE INTO ITS WORKSPACE FROM A LOOPBACK REGISTRY — the packument and a tarball the test built,
    /// served on 127.0.0.1, <c>npm install --prefix</c> into the workspace with npm's cache there, <c>require</c> working inside the
    /// cell, and nothing outside the workspace changed.
    /// </summary>
    [LinuxFact]
    public async Task A_cell_installs_an_npm_package_into_its_workspace_from_a_loopback_registry()
    {
        PlatformIsACells();
        var w = new World("npm");
        Require(Which("npm") is not null, $"npm on the cell's PATH ({CellPath})");
        using var index = LoopbackIndex.Start(IPAddress.Loopback);
        index.ServeNpm("ta-cell-pkg", "1.0.0", NpmTarball("ta-cell-pkg", "1.0.0", "module.exports = 'from-the-loopback-registry';\n"));
        var app = Path.Combine(w.Ws, "app");
        var before = Snapshot(w);

        var ran = await InCell(ToolPolicy(w, CellEgress.ForTests([index.Port])), "/bin/sh", "-c",
            "echo cell-pid=$$; " +
            $"npm install --no-audit --no-fund --prefix '{app}' --registry http://127.0.0.1:{index.Port}/ ta-cell-pkg > npm.log 2>&1; " +
            "echo npm-rc=$?; tail -n 8 npm.log; " +
            $"node -e \"console.log('imported-' + require('{app}/node_modules/ta-cell-pkg'))\"");
        _output.WriteLine("row (g): " + ran.Out.Replace('\n', ' ') + " | requests " + string.Join(" ", index.Requests));

        Says(ran, "npm-rc=0", "imported-from-the-loopback-registry");
        Assert.True(File.Exists(Path.Combine(app, "node_modules", "ta-cell-pkg", "index.js")));
        Assert.True(Directory.Exists(Path.Combine(w.Ws, ".cache", "npm")), "npm's cache, in the workspace");
        Assert.Contains("/ta-cell-pkg/-/ta-cell-pkg-1.0.0.tgz", index.Requests);
        Unchanged(before, Snapshot(w), CellPid(ran));
    }

    /// <summary>An npm package tarball: <c>package/package.json</c> and <c>package/index.js</c>, gzipped, as <c>npm pack</c> lays it out.</summary>
    static byte[] NpmTarball(string name, string version, string code)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (path, text) in new[]
            {
                ("package/package.json", $"{{\"name\":\"{name}\",\"version\":\"{version}\",\"main\":\"index.js\"}}\n"),
                ("package/index.js", code)
            })
            {
                var entry = new UstarTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)
                };
                tar.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }
}
