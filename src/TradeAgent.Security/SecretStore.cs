using System.Security.Cryptography;
using TradeAgent.Core;

namespace TradeAgent.Security;

/// <summary>
/// TradeAgent's own secrets — currently just the IPC token. Broker credentials deliberately never
/// come near this: ATAS owns broker authentication, and the agent workspace never sees either.
/// On Windows the bytes are DPAPI-protected to the current user; elsewhere the file is 0600, and it
/// is 0600 from its first byte (see <see cref="Write"/>).
/// </summary>
public static class SecretStore
{
    static readonly byte[] Entropy = "TradeAgent.v1"u8.ToArray();

    /// <summary>
    /// PUTS <paramref name="value"/> AT <paramref name="path"/> IN ONE STEP, AND OWNER-ONLY FROM ITS FIRST
    /// BYTE (<c>U-test-hygiene-2</c>), THROUGH THE ONE PUBLISH EVERY CREDENTIAL TAKES (<c>U-credential-replace</c>).
    ///
    /// <para>It used to be <c>File.WriteAllBytes</c> on the file itself, then a chmod. The first truncates
    /// before it writes, so a reader in between read an empty file and took it for "absent"; the second
    /// left the bytes readable by every account for as long as the write took. Then it was a temp of its
    /// own renamed over the file by <c>File.Move</c> — on Windows <c>MoveFileExW</c>, whose replace is
    /// refused while ANY handle is open on the file it replaces, sharing delete included. MEASURED on
    /// windows-latest, "Access to the path is denied": runs 37675671465 and 37675951756 on the bridge's key,
    /// and run 37736395707 on this file — a rewrite under a reader sharing read, write and delete, and
    /// <see cref="IpcToken.Ensure(string)"/> over an unusable file such a reader held. Ensure writes here only
    /// over a file holding nothing usable, on a start, so a reader holding the file at that moment — the
    /// <c>trade</c> command, a scanner — made the start fail with those words and nothing else. Now the
    /// bytes, sealed to this account first on Windows as always, go through <see cref="OwnerOnlyFile.Write"/>:
    /// a temp of its own, created 0600 off Windows, flushed, and renamed over <paramref name="path"/> by a
    /// rename a reader that shares delete does not refuse. A reader sees the old file or the new one and
    /// nothing between them; a rename the platform refuses leaves the file as it was and throws.</para>
    /// </summary>
    public static void Write(string path, string value)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(value);
        var bytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : plain;
        OwnerOnlyFile.Write(path, bytes);
    }

    /// <summary>
    /// The secret at <paramref name="path"/>, or null when there is none or this account cannot unseal it.
    /// Opened sharing write and delete (<see cref="OpenToRead"/>). That is half of what lets
    /// <see cref="Write"/> replace the file while a reader holds it, and measured alone it is nothing: on
    /// windows-latest <c>MoveFileExW</c> refused the replace under a reader sharing read, write and delete
    /// (run 37675951756). The other half is <see cref="OwnerOnlyFile.Write"/>'s POSIX rename, which a reader
    /// that shares delete does not refuse.
    /// </summary>
    public static string? Read(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = OpenToRead(path);
            using var copy = new MemoryStream();
            fs.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            var plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes;
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // written by another user or corrupt: treat as absent, regenerate.
        }
    }

    /// <summary>
    /// How a reader holds the file while it reads it: the one open <see cref="Read"/> makes, so a test that
    /// holds a handle across <see cref="Write"/> holds exactly the handle a reader does — the <c>trade</c>
    /// command's <see cref="IpcToken.Peek"/>, from any process, at any moment the app is starting.
    /// </summary>
    internal static FileStream OpenToRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

/// <summary>The shared secret an agent must present before the gateway will talk to it.</summary>
public static class IpcToken
{
    /// <summary>One writer at a time in this process. See <see cref="Ensure(string)"/>.</summary>
    static readonly Lock Writer = new();

    public static string Ensure() => Ensure(Paths.IpcTokenFile);

    /// <summary>
    /// THE TOKEN AT <paramref name="path"/>, WRITTEN ONCE — whoever asks first and however many ask at once
    /// (<c>U-test-hygiene-2</c>, windows-latest run 37443989301).
    ///
    /// <para>It was an unlocked read-then-write: every caller that found no token minted its own and wrote
    /// it, so two first callers each walked away holding a token, one of them a token the file no longer
    /// had, and on Windows the second writer met the first one's handle and threw. Now a caller that finds
    /// a token returns it without waiting for anything, and a caller that does not takes
    /// <see cref="Writer"/>, reads AGAIN, and writes only if there is still nothing usable there — so a
    /// caller that loses the race returns the winner's token, and the write itself is
    /// <see cref="SecretStore.Write"/>'s: one rename, owner-only from its first byte.</para>
    ///
    /// <para><b>Across processes</b> the lock is in-process on purpose: the two writers the product has,
    /// the app and the headless gateway host, each take <see cref="SingleInstanceLock"/> over the same
    /// installation before they come here (<c>AppHost.StartAsync</c>, <c>GatewayHost/Program.cs</c>), so
    /// one installation never has two writing processes. Every other process only reads (<see cref="Peek"/>,
    /// the <c>trade</c> command), and a reader sees a whole token or none.</para>
    /// </summary>
    public static string Ensure(string path)
    {
        if (Usable(SecretStore.Read(path)) is { } held) return held;
        lock (Writer)
        {
            if (Usable(SecretStore.Read(path)) is { } written) return written;
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            SecretStore.Write(path, token);
            return token;
        }
    }

    /// <summary>A token is what <see cref="Ensure(string)"/> mints; anything shorter is regenerated.</summary>
    static string? Usable(string? held) => !string.IsNullOrWhiteSpace(held) && held.Length >= 32 ? held : null;

    public static string? Peek() => SecretStore.Read(Paths.IpcTokenFile);

    /// <summary>Constant-time compare so a wrong token cannot be guessed a byte at a time.</summary>
    public static bool Matches(string? presented, string expected) =>
        presented is not null &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(expected));
}

/// <summary>
/// One gateway per machine. A second instance must refuse rather than race the first one onto the
/// same broker account — two dispatchers over one book is how you get orders nobody asked for.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    FileStream? _fs;

    public static SingleInstanceLock? TryAcquire(string? path = null)
    {
        var file = path ?? Paths.InstanceLockFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            var fs = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(0);
            var pid = System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            fs.Write(pid);
            fs.Flush(true);
            return new SingleInstanceLock { _fs = fs };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() { _fs?.Dispose(); _fs = null; }
}
