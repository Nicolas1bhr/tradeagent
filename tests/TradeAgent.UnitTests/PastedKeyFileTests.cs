using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (c) THE KEY THE OWNER PASTES INTO TRADEAGENT'S OWN WINDOW, ON DISK (<c>U-credential-replace</c>).
///
/// <para>For a runtime whose manifest names a credentials file rather than a stdin sign-in — OpenCode's
/// <c>auth.json</c> — <see cref="CliAgentRuntime.SignInWithApiKeyAsync(string, CancellationToken)"/> puts the key
/// in that file. It wrote it IN PLACE, with <c>File.WriteAllTextAsync</c>: truncated first, so a failure between
/// the truncation and the write left the file empty and the owner signed out; and, off Windows, created with no
/// mode of its own — 0666 less the umask, 0644 under the usual 022 — so every account on the machine could read
/// the key. Driven through the product's own OpenCode manifest, pointed at a scratch folder instead of the
/// owner's home; the keys are made for the run.</para>
/// </summary>
public class PastedKeyFileTests(ITestOutputHelper output)
{
    /// <summary>
    /// OWNER-ONLY FROM ITS FIRST BYTE: at the seam the key is in a file and nothing has published it yet. On
    /// macOS and Linux that file is 0600, and so is the published one. On Windows there is no mode: the file
    /// inherits its folder's DACL as it always did, so what is read is that the write adds no entry of its own
    /// and that the published file carries the DACL the key sat under at the seam. The bytes are the ones the
    /// sign-in has always written.
    /// </summary>
    [Fact]
    public async Task The_pasted_key_is_owner_only_from_its_first_byte()
    {
        using var home = TestEnv.NewScratch("pasted-key-mode");
        var path = Path.Combine(home.Dir, "opencode", "auth.json");
        var key = NewKey();

        if (OperatingSystem.IsWindows()) await OwnerOnlyOnWindows(path, key);
        else await OwnerOnlyOnUnix(path, key);

        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(Written(key)),
            "the file does not hold the bytes the sign-in has always written for this key");
    }

    [UnsupportedOSPlatform("windows")]
    async Task OwnerOnlyOnUnix(string path, string key)
    {
        UnixFileMode? atSeam = null;
        await OpenCodeWritingTo(path).SignInWithApiKeyAsync(key, default, beforeRename: temp => atSeam = File.GetUnixFileMode(temp));
        var published = File.GetUnixFileMode(path);

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var said = $"the key was on disk at {Octal(atSeam)} before the rename and is published at {Octal(published)}; " +
                   $"owner-only is {Octal(ownerOnly)} — {Umask(Path.GetDirectoryName(path)!)}";
        output.WriteLine(said);

        Assert.True(atSeam == ownerOnly, said);
        Assert.True(published == ownerOnly, said);
    }

    [SupportedOSPlatform("windows")]
    async Task OwnerOnlyOnWindows(string path, string key)
    {
        FileSecurity? atSeam = null;
        await OpenCodeWritingTo(path).SignInWithApiKeyAsync(key, default,
            beforeRename: temp => atSeam = new FileInfo(temp).GetAccessControl(AccessControlSections.Access));
        Assert.True(atSeam is not null, "the key never sat in a file of its own before it was published: it was written in place");
        var published = new FileInfo(path).GetAccessControl(AccessControlSections.Access);

        static string Dacl(FileSecurity s) => s.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var own = atSeam.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count;
        var said = $"the file at the seam has {own} entr(y/ies) of its own and is {(atSeam.AreAccessRulesProtected ? "" : "not ")}protected; " +
                   $"its DACL {(Dacl(atSeam) == Dacl(published) ? "is" : "is not")} the published file's";
        output.WriteLine(said);

        Assert.True(Dacl(atSeam) == Dacl(published), said);
        Assert.True(own == 0 && !atSeam.AreAccessRulesProtected, said);
    }

    /// <summary>
    /// A FAILED WRITE LEAVES THE OLD FILE WHOLE. The seam throws at the last instant before the new file would
    /// be published — where the process dying, a full disk or a refused rename would land — and the file must
    /// still hold the key that was working, every byte of it, with nothing left beside it.
    /// </summary>
    [Fact]
    public async Task A_failed_write_of_the_pasted_key_leaves_the_old_file_whole()
    {
        using var home = TestEnv.NewScratch("pasted-key-refused");
        var path = Path.Combine(home.Dir, "opencode", "auth.json");
        var first = NewKey();
        var runtime = OpenCodeWritingTo(path);
        await runtime.SignInWithApiKeyAsync(first);

        var refused = await Record.ExceptionAsync(() => runtime.SignInWithApiKeyAsync(NewKey(), default,
            beforeRename: _ => throw new IOException("the rename was refused")));

        var now = File.ReadAllBytes(path);
        var whole = now.AsSpan().SequenceEqual(Written(first));
        var left = Directory.GetFiles(Path.GetDirectoryName(path)!).Where(f => f != path).Select(Path.GetFileName).ToList();
        var said = $"the second write {(refused is null ? "never failed: it reached no instant before which the old file survives" : $"failed with {refused.GetType().Name}: {refused.Message}")}; " +
                   $"the file holds {(whole ? "the first key, whole" : $"{now.Length} byte(s) that are not the first key's")}; " +
                   $"{left.Count} file(s) beside it{(left.Count > 0 ? ": " + string.Join(", ", left) : "")}";
        output.WriteLine(said);

        Assert.True(refused is IOException { Message: "the rename was refused" }, said);
        Assert.True(whole, said);
        Assert.True(left.Count == 0, said);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The product's own OpenCode manifest, its credentials file moved to <paramref name="file"/>.</summary>
    static CliAgentRuntime OpenCodeWritingTo(string file)
    {
        var manifest = RuntimeCatalog.BuiltIn().Single(m => m.Id == "opencode");
        manifest.ApiKey!.File = file;
        return new CliAgentRuntime(manifest);
    }

    /// <summary>What the sign-in has always written for <paramref name="key"/>: the manifest's template with the key
    /// JSON-escaped, as UTF-8 with no byte-order mark (<c>File.WriteAllTextAsync</c>'s encoding).</summary>
    static byte[] Written(string key) => Encoding.UTF8.GetBytes(
        RuntimeCatalog.BuiltIn().Single(m => m.Id == "opencode").ApiKey!.FileTemplate!
            .Replace("{key}", JsonEncodedText.Encode(key).ToString()));

    /// <summary>Made for this run and never a real one; a quote and a backslash, so the escaping is in the bytes compared.</summary>
    static string NewKey() => "k\"\\" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    static string Octal(UnixFileMode? mode) => mode is { } m ? Convert.ToString((int)m, 8).PadLeft(4, '0') : "<never seen>";

    /// <summary>What this process's umask makes of a file created without a mode — which is what the in-place write made.</summary>
    [UnsupportedOSPlatform("windows")]
    static string Umask(string dir)
    {
        var probe = Path.Combine(dir, $"umask-{Guid.NewGuid():n}.probe");
        File.WriteAllText(probe, "");
        try
        {
            var made = File.GetUnixFileMode(probe);
            return $"umask {Octal((UnixFileMode)0b110_110_110 & ~made)}: a file created without a mode comes out {Octal(made)}";
        }
        finally { File.Delete(probe); }
    }
}
