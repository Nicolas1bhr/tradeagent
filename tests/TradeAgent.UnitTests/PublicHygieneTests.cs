using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-public-hygiene — THIS REPOSITORY IS PUBLIC, SO NO TRACKED FILE CARRIES THE SHAPE OF A PRIVATE MACHINE DETAIL.
///
/// <para><b>Why.</b> The public-history audit of 2026-11-13 found the owner's home folders, the Windows test box's tailnet
/// address and Windows host name, an agent-memory folder named after a home folder, and an iCloud Drive folder in docs at
/// main's tip. None was a credential, but each fingerprints the owner's machines. The forward fix wrote them as <c>~</c>,
/// <c>%USERPROFILE%</c>, placeholders or role words ("the Windows test box"); this test keeps them from coming back.</para>
///
/// <para><b>Shapes only, never values.</b> A rule that spelled out a private name would publish it. So this test matches
/// shapes (a home folder whose user is not a placeholder, a tailnet host address, a Windows or Mac default host name, an
/// iCloud Drive folder, a tailnet DNS name). Private VALUES (machine names, project names) are guarded by the fleet's
/// pre-push scan, which lives outside this repository.</para>
/// </summary>
public class PublicHygieneTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    /// <summary>User folders that name nobody: placeholders, CI runners, service accounts, and the cell tests' one- and
    /// two-letter fixtures (any segment of at most two characters).</summary>
    static readonly HashSet<string> PlaceholderUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "owner", "someone", "runner", "user", "username", "you", "me", "ubuntu", "tradeagent", "public", "default", "shared",
    };

    // A path segment begins with a letter, digit or underscore: "/home/.ssh" names no user.
    const string Seg = @"(?<user>[A-Za-z0-9_][A-Za-z0-9._-]*)";

    // The home-folder shapes are anchored where a path starts, so a URL's own "/home/" path segment is not a home folder.
    static readonly Regex HomeFolder = new(
        @"(?<![A-Za-z0-9._/-])(?:/Users/|/home/|/c/Users/|[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2})" + Seg, RegexOptions.Compiled);

    static readonly (string Kind, Regex Shape)[] Shapes =
    [
        // Claude Code names a project's memory folder after its path, so a home folder shows up dash-joined at the start of a
        // path segment ("-Users-<name>-…", "C--Users-<name>-…", "-home-<name>-…"); a prose compound such as "per-home-folder" is not one.
        ("an agent-memory folder named after a home folder",
            new Regex(@"(?<![A-Za-z0-9-])(?:[A-Za-z]-)?-(?:Users|home)-[A-Za-z0-9_][A-Za-z0-9._]*-", RegexOptions.Compiled)),
        // 100.64.0.0/10 host addresses; the range itself, written as a CIDR (a firewall rule), is not a host.
        ("a tailnet (CGNAT) host address",
            new Regex(@"(?<![\d.])100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}(?![\d.]|/\d)", RegexOptions.Compiled)),
        ("a Windows default host name", new Regex(@"\b(?:DESKTOP-[A-Z0-9]{7}|WIN-[A-Z0-9]{11})\b", RegexOptions.Compiled)),
        ("a Mac default host name",
            new Regex(@"\b[A-Za-z]+-(?:MacBook|iMac|Mac-mini|Mac-Studio|Mac-Pro)(?:-[A-Za-z0-9]+)*\b|\b[A-Z][a-z]+['’]s (?:MacBook|iMac|Mac mini|Mac Studio|Mac Pro)\b",
                RegexOptions.Compiled)),
        ("an iCloud Drive folder", new Regex(@"com~apple~Cloud(?:Docs)|Mobile[ ]Documents/", RegexOptions.Compiled)),
        // A machine's MagicDNS name; the generic "*.ts.net" spelling in a how-to names no machine.
        ("a tailnet DNS name", new Regex(@"\b[a-z0-9][a-z0-9-]*\.tail[a-z0-9]+\.ts\.net\b", RegexOptions.Compiled)),
    ];

    /// <summary>Every private-machine shape in one text, as "kind" rows with their line numbers (never the matched value).</summary>
    internal static List<(int Line, string Kind)> Findings(string text)
    {
        var found = new List<(int, string)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match m in HomeFolder.Matches(lines[i]))
            {
                var user = m.Groups["user"].Value;
                if (user.Length > 2 && !PlaceholderUsers.Contains(user)) found.Add((i + 1, "a home folder that names its user"));
            }
            foreach (var (kind, shape) in Shapes)
                if (shape.IsMatch(lines[i])) found.Add((i + 1, kind));
        }
        return found;
    }

    static string[] TrackedFiles(string root)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", root, "ls-files", "-z" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "git ls-files failed: " + p.StandardError.ReadToEnd());
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// NO TRACKED TEXT FILE CARRIES A PRIVATE-MACHINE SHAPE. Each row names the file, the line and the kind, never the value,
    /// so the failure itself publishes nothing. The fix is a neutral form: <c>~/…</c>, <c>%USERPROFILE%\…</c>, a placeholder
    /// such as <c>&lt;the box&gt;</c>, or a role word.
    /// </summary>
    [Fact]
    public void No_tracked_file_names_a_private_machine()
    {
        var root = RepoRoot();
        var files = TrackedFiles(root);
        Assert.True(files.Length > 100, $"git listed only {files.Length} tracked files under {root}: the walk would prove nothing");
        var rows = new List<string>();
        foreach (var f in files)
        {
            var path = Path.Combine(root, f);
            if (!File.Exists(path)) continue; // deleted in the working tree, not yet committed
            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) continue; // binary
            foreach (var (line, kind) in Findings(System.Text.Encoding.UTF8.GetString(bytes)))
                rows.Add($"{f}:{line}: {kind}");
        }
        Assert.True(rows.Count == 0, "private-machine shapes in tracked files:\n" + string.Join('\n', rows));
    }

    /// <summary>
    /// THE MATCHER CATCHES EACH SHAPE AND PASSES EACH NEUTRAL FORM, on made-up values only, so the walk above cannot pass by
    /// matching nothing. Each made-up value is split by a <c>+</c> where its shape would form, so this file itself carries none.
    /// </summary>
    [Theory]
    [InlineData("read /Users/" + "jdoe/Projects/x/CLAUDE.md", "a home folder that names its user")]
    [InlineData(@"TRADEAGENT_HOME = C:\Users\" + @"Jdoe\AppData\Local\Temp\x", "a home folder that names its user")]
    [InlineData("C:/Users/" + "Jdoe/AppData", "a home folder that names its user")]
    [InlineData("cd /c/Users/" + "Jdoe/Projects", "a home folder that names its user")]
    [InlineData("ssh to /home/" + "jdoe/kit", "a home folder that names its user")]
    [InlineData("~/.claude/projects/-Users-" + "jdoe-Projects/memory/", "an agent-memory folder named after a home folder")]
    [InlineData(@"%USERPROFILE%\.claude\projects\C--Users-" + "Jdoe-Projects", "an agent-memory folder named after a home folder")]
    [InlineData("- **Box** (100.101." + "102.103): left running", "a tailnet (CGNAT) host address")]
    [InlineData("host `DESKTOP-" + "AB12CD3`, session Active", "a Windows default host name")]
    [InlineData("host WIN-" + "ABCDEFGH123 answered", "a Windows default host name")]
    [InlineData("computerName=\"Jdoes-" + "MacBook-Pro.local\"", "a Mac default host name")]
    [InlineData("on Jdoe's " + "MacBook Pro", "a Mac default host name")]
    [InlineData("~/Library/Mobile " + "Documents/com~apple~" + "CloudDocs/x", "an iCloud Drive folder")]
    [InlineData("ssh box.tail1a2b3c." + "ts.net", "a tailnet DNS name")]
    public void The_matcher_catches_each_private_machine_shape(string text, string kind) =>
        Assert.Contains(Findings(text), r => r.Kind == kind);

    [Theory]
    [InlineData("read ~/Projects/x/CLAUDE.md")]
    [InlineData(@"TRADEAGENT_HOME = %USERPROFILE%\AppData\Local\Temp\x")]
    [InlineData(@"const string state = @""C:\Users\Owner\AppData\Local\TradeAgent\state"";")]
    [InlineData(@"C:\Users\X\AppData and /home/a/ws and /home/.ssh")]
    [InlineData("https://www.federalreserve.gov/monetarypolicy/home/html")]
    [InlineData("port 22 open to Tailscale addresses (100.64.0.0/10) only")]
    [InlineData("host `<the box>`, the Windows test box, the owner's Mac")]
    [InlineData("use its full `*.ts.net` name or its IP")]
    [InlineData("~/.claude/projects/<project-folder>/memory/")]
    [InlineData("a per-home-folder lock and an app-home-scoped file")]
    public void The_matcher_passes_each_neutral_form(string text) => Assert.Empty(Findings(text));
}
