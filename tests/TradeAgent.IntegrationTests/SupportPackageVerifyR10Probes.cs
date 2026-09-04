using System.IO.Compression;
using TradeAgent.AtasBridge;
using TradeAgent.Core;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-10 VERIFIER, leg [2]. Target 1/7 — the round-10 directive says every consumer, "the probe,
/// the support package, and ROTATION", reads from the snapshot and never touches the filesystem
/// itself. The support package still enumerates and copies the sidecar set with its own
/// `Directory.GetFiles` under a swallowing catch. Probes, not fixes.
/// </summary>
public class SupportPackageVerifyR10Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "r10sp-" + Guid.NewGuid().ToString("n")[..8]);
    readonly List<string> _made = [];

    public SupportPackageVerifyR10Probes() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in _made)
        {
            if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { } }
            try { File.Delete(f); } catch (Exception) { }
        }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    string Make(string name, string text)
    {
        var p = Path.Combine(Paths.BridgeDir, name);
        File.WriteAllText(p, text);
        _made.Add(p);
        return p;
    }

    /// <summary>
    /// A SIDECAR THIS RUN CANNOT READ LEAVES THE SUPPORT PACKAGE SILENTLY EMPTY OF THE WHOLE SET —
    /// and the readable generations beside it go too, because the `foreach` is inside the one try.
    /// The zip carries no note that anything was skipped.
    ///
    /// This is the class the round closed inside `CoidWitness`: "I could not read it" arriving as
    /// "there is nothing there", one consumer over.
    /// </summary>
    [Fact]
    public void A_denied_sidecar_drops_the_whole_set_from_the_support_package_without_saying_so()
    {
        if (OperatingSystem.IsWindows()) return;

        var readable = Make(CoidWitness.ErrorLogName,
            "2026-09-04T00:00:00.0000000+00:00 ERROR coid-witness rewrite did not land. claim=TA-READABLE");
        var denied = Make(CoidWitness.ErrorLogName + "-9999-deadbeef",
            "2026-09-04T00:00:00.0000000+00:00 ERROR claim=TA-DENIED another writer owns this witness");
        File.SetUnixFileMode(denied, UnixFileMode.None);

        var zip = Doctor.CreateSupportPackage(TestEnv.NewDb(), Path.Combine(_dir, "support.zip"));
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).Order().ToArray();

        var sidecars = names.Where(n => n.Contains("errors.log", StringComparison.Ordinal)).ToArray();
        Assert.True(sidecars.Length == 2 || names.Any(n => n.Contains("skipped", StringComparison.OrdinalIgnoreCase)),
            "a sidecar this run could not read is missing from the support package and nothing in it "
            + $"says so. sidecars=[{string.Join(", ", sidecars)}] all=[{string.Join(", ", names)}] "
            + $"readable={Path.GetFileName(readable)} denied={Path.GetFileName(denied)}");
    }

    /// <summary>
    /// CONTROL: the same two files, both readable. Both are collected — so the assertion above is
    /// about the denial and not about the fixture.
    /// </summary>
    [Fact]
    public void CONTROL_two_readable_sidecars_are_both_collected()
    {
        Make(CoidWitness.ErrorLogName,
            "2026-09-04T00:00:00.0000000+00:00 ERROR coid-witness rewrite did not land. claim=TA-READABLE");
        Make(CoidWitness.ErrorLogName + "-9999-deadbeef",
            "2026-09-04T00:00:00.0000000+00:00 ERROR claim=TA-OTHER another writer owns this witness");

        var zip = Doctor.CreateSupportPackage(TestEnv.NewDb(), Path.Combine(_dir, "support2.zip"));
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(2, archive.Entries.Count(e => e.FullName.Contains("errors.log", StringComparison.Ordinal)));
    }

    /// <summary>
    /// AND THE OTHER HALF OF THE SAME CALL: the collector globs with `Directory.GetFiles`, so a
    /// DIRECTORY sitting at a sidecar's name is not returned at all — the same distinction round 10
    /// changed `CoidWitness`'s own seam default to `GetFileSystemEntries` to make.
    /// </summary>
    [Fact]
    public void A_directory_at_a_sidecars_name_is_invisible_to_the_support_package()
    {
        var asDir = Path.Combine(Paths.BridgeDir, CoidWitness.ErrorLogName + ".2");
        Directory.CreateDirectory(asDir);
        try
        {
            var seen = Directory.GetFiles(Paths.BridgeDir, "*.errors.log*").Select(Path.GetFileName).ToArray();
            var entries = Directory.GetFileSystemEntries(Paths.BridgeDir, "*.errors.log*").Select(Path.GetFileName).ToArray();
            Assert.True(seen.Contains(CoidWitness.ErrorLogName + ".2"),
                $"GetFiles=[{string.Join(", ", seen)}] GetFileSystemEntries=[{string.Join(", ", entries)}]");
        }
        finally { try { Directory.Delete(asDir); } catch (Exception) { } }
    }
}
