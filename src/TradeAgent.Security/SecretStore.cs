using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TradeAgent.Core;

namespace TradeAgent.Security;

/// <summary>
/// TradeAgent's own secrets — currently just the IPC token. Broker credentials deliberately never
/// come near this: ATAS owns broker authentication, and the agent workspace never sees either.
/// On Windows the bytes are DPAPI-protected to the current user; elsewhere the file is 0600.
/// </summary>
public static class SecretStore
{
    static readonly byte[] Entropy = "TradeAgent.v1"u8.ToArray();

    public static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = System.Text.Encoding.UTF8.GetBytes(value);
        byte[] bytes;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            bytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        else
        {
            bytes = plain;
        }
        File.WriteAllBytes(path, bytes);
        Restrict(path);
    }

    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        try
        {
            var plain = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)
                : bytes;
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // written by another user or corrupt: treat as absent, regenerate.
        }
    }

    /// <summary>Owner-only permissions. On Windows, DPAPI already binds the bytes to the user account.</summary>
    public static void Restrict(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* best effort: a hostile filesystem is a diagnostics problem, not a crash */ }
    }
}

/// <summary>The shared secret an agent must present before the gateway will talk to it.</summary>
public static class IpcToken
{
    public static string Ensure()
    {
        var existing = SecretStore.Read(Paths.IpcTokenFile);
        if (!string.IsNullOrWhiteSpace(existing) && existing.Length >= 32) return existing;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        SecretStore.Write(Paths.IpcTokenFile, token);
        return token;
    }

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
