using System.Text;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A PATH PAST MAX_PATH IS PUBLISHED LIKE ANY OTHER (<c>U-catalog-windows-path</c>): the tests laptop refused
/// <c>ModelCatalogTests</c>' seat-home catalog in three runs (20261025-055630, -071030, -082528) with "could not open …\model-catalog.json.&lt;guid&gt;.tmp
/// to publish it: the path specified cannot be found" — a 263-character temp handed to <see cref="OwnerOnlyFile"/>'s own <c>CreateFileW</c>
/// as it was, on a Windows whose long paths are off, where .NET's own calls had made the same folders without a word.
/// </summary>
public class Win32LongPathTests
{
    /// <summary>
    /// (1) THE NAME, ON EVERY OS: below MAX_PATH unchanged; at it and past it verbatim, a drive path as <c>\\?\C:\…</c> and a share as
    /// <c>\\?\UNC\server\share\…</c>; a name already verbatim or a device name never prefixed twice.
    /// </summary>
    [Fact]
    public void A_name_at_max_path_or_past_it_is_handed_to_win32_verbatim()
    {
        var shortName = @"C:\home\seat-home\codex\model-catalog.json";
        Assert.Equal(shortName, Win32Path.Verbatim(shortName));

        var drive = @"C:\" + new string('d', Win32Path.MaxPath - 3);
        Assert.Equal(Win32Path.MaxPath, drive.Length);
        Assert.Equal(@"\\?\" + drive, Win32Path.Verbatim(drive));
        var below = drive[..^1];
        Assert.Equal(below, Win32Path.Verbatim(below));

        var share = @"\\server\share\" + new string('s', Win32Path.MaxPath);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('s', Win32Path.MaxPath), Win32Path.Verbatim(share));

        var verbatim = @"\\?\" + drive;
        Assert.Equal(verbatim, Win32Path.Verbatim(verbatim));
        var device = @"\\.\" + drive;
        Assert.Equal(device, Win32Path.Verbatim(device));
    }

    /// <summary>
    /// (2) THE PUBLISH ITSELF, ON EVERY OS (RED on a Windows whose long paths are off before the fix): a file whose own name and whose
    /// temp's are both past MAX_PATH is written, then replaced, and no temp is left beside it.
    /// </summary>
    [Fact]
    public void A_file_past_max_path_is_published_and_replaced_with_no_temp_left()
    {
        var top = Path.Combine(TestEnv.Root, Guid.NewGuid().ToString("n"));
        var folder = Path.Combine(top, new string('a', 60), new string('b', 60), new string('c', 60), "seat-home", "codex");
        var path = Path.Combine(folder, "model-catalog.json");
        Assert.True(Path.GetFullPath(path).Length >= Win32Path.MaxPath, $"{path} is only {path.Length} characters");
        try
        {
            OwnerOnlyFile.Write(path, Encoding.UTF8.GetBytes("first"));
            Assert.Equal("first", File.ReadAllText(path));
            OwnerOnlyFile.Write(path, Encoding.UTF8.GetBytes("second"));
            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFiles(folder));
        }
        finally
        {
            try { Directory.Delete(top, recursive: true); }
            catch (Exception) { /* litter in the run's own temp, not a fault */ }
        }
    }
}
