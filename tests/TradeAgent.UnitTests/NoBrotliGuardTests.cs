using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-security-sweep-a — NO HTTP CLIENT IN <c>src/</c> ADVERTISES BROTLI (<c>Accept-Encoding: br</c>).
///
/// <para>.NET bundles brotli 1.1.0 and Microsoft ships no fix for CVE-2026-111497 (CVSS 9.2): a response body a server chose to
/// compress with brotli is parsed by the vulnerable copy. A handler that asks for <c>DecompressionMethods.All</c> or
/// <c>.Brotli</c> sends <c>br</c> in its Accept-Encoding, so every site the app talks to may answer in it. The decompressed body is
/// identical either way, so refusing Brotli changes no recorded data. THE REVERT CONDITION: a .NET 10 servicing release that lists
/// brotli at 1.1.1 or later in <c>src/native/external/brotli-version.txt</c> on <c>release/10.0</c>, or in its release notes.</para>
///
/// <para><b>What it scans:</b> every <c>.cs</c> file under <c>src/</c> (outside bin and obj), comment lines excepted, for the two
/// spellings that advertise <c>br</c>. <b>What it cannot see:</b> a handler configured by a computed enum value, or a request header
/// written by hand; neither exists in the code today.</para>
/// </summary>
public class NoBrotliGuardTests
{
    static readonly Regex Advertises = new(@"DecompressionMethods\s*\.\s*(All|Brotli)\b|DecompressionMethods\s*\)\s*(-1|0xFF)", RegexOptions.Compiled);

    [Fact]
    public void NoHandlerInSrcAdvertisesBrotli()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (Advertises.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "a handler advertises br (brotli 1.1.0, CVE-2026-111497); use GZip | Deflate:\n" + string.Join("\n", hits));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
