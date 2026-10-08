using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>
/// HOW A CREDENTIAL REACHES THE DISK: one publish, owner-only from its first byte, never truncated in place,
/// and on Windows not refused by a reader of the file it replaces (<c>U-credential-replace</c>).
///
/// <para>Every credential the app writes comes through here: the bridge pipe's key
/// (<c>BridgePipeAuth.Write</c>, republished on every turn of the connector's accept loop), the agent pipe's
/// key (<c>SecretStore.Write</c>, under <c>IpcToken.Ensure</c> on a start), and the key the owner pastes for a
/// runtime that reads it from a file (<c>CliAgentRuntime.SignInWithApiKeyAsync</c>). It was
/// <c>BridgePipeAuth</c>'s own (<c>U-bridge-auth-owner-only</c>), a copy of <c>SecretStore.Write</c>'s shape
/// kept there because the bridge DLL's closure does not reach Security, and the copy is where the two
/// drifted: the bridge's key gained the rename a reader does not refuse and the agent pipe's did not. It
/// lives in Core because Core is in every one of those closures, the bridge's included, so there is one
/// body and nothing to drift.</para>
/// </summary>
public static class OwnerOnlyFile
{
    /// <summary>
    /// PUBLISHES <paramref name="bytes"/> AT <paramref name="path"/> IN ONE RENAME, OWNER-ONLY FROM ITS FIRST BYTE.
    ///
    /// <para>The bytes go to a temp of this call's own beside <paramref name="path"/>, created new — on macOS
    /// and Linux created 0600 by the open itself — written, flushed to the device, and renamed over
    /// <paramref name="path"/>; a temp the rename did not consume is deleted. A reader sees the old file or the
    /// new one and nothing between them, and a failure anywhere before the rename leaves the old file whole. On
    /// Windows the temp inherits its folder's DACL, as each of these files always did, and the rename keeps it:
    /// the window adds no reader the published file lacks; and the rename is <see cref="Publish"/>'s, which a
    /// reader of the old file does not refuse.</para>
    ///
    /// <para><paramref name="beforeRename"/> is a test seam and production passes nothing: it is handed the
    /// temp's path once the bytes are in it, before anything else touches it — the instant at which "who can
    /// read this" is the question.</para>
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, Action<string>? beforeRename = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(temp, create))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            beforeRename?.Invoke(temp);
            Publish(temp, path);
        }
        finally
        {
            // Only after a failure is there anything here; the rename consumed it otherwise.
            try { File.Delete(temp); } catch (Exception) { /* a temp the OS will not let go of is litter, not a fault */ }
        }
    }

    /// <summary>
    /// THE RENAME, AND ON WINDOWS THE ONE A READER DOES NOT REFUSE (<c>U-bridge-auth-owner-only</c>).
    ///
    /// <para><c>File.Move</c> is <c>MoveFileExW</c>, whose replace is refused while any handle is open on the
    /// file it replaces, whatever that handle shares: measured on windows-latest, "Access to the path is
    /// denied" under a reader sharing read only (run 37675671465) and under one sharing read, write and
    /// delete (run 37675951756) — the finding behind git's <c>391bceae</c> and Rust's <c>#131072</c> as well.
    /// Windows' POSIX rename — <c>FileRenameInfoEx</c> with <c>FILE_RENAME_FLAG_POSIX_SEMANTICS</c>, NTFS on
    /// Windows 10 1607 and later — takes the name while a reader that shares delete reads on in the file it
    /// opened, as <c>rename(2)</c> does on macOS and Linux, and the file keeps the security descriptor it was
    /// created with. A volume without it (FAT, exFAT, ReFS on Server 2022, a network share) answers "invalid
    /// function", "not supported" or "invalid parameter", and there <c>MoveFileExW</c> is all there is: a
    /// reader still refuses that replace, and the next attempt is the caller's own — the accept loop's after
    /// its pause, the app's next start, the owner's next press.</para>
    /// </summary>
    static void Publish(string temp, string path)
    {
        if (OperatingSystem.IsWindows() && ReplaceWhileReadersRead(temp, path)) return;
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>True when the POSIX rename ran; false when this volume has none. Any other refusal throws.</summary>
    [SupportedOSPlatform("windows")]
    static bool ReplaceWhileReadersRead(string temp, string path)
    {
        const uint delete = 0x00010000, synchronize = 0x00100000;
        const int fileRenameInfoEx = 22, replaceIfExists = 0x1, posixSemantics = 0x2;
        const int invalidFunction = 1, notSupported = 50, invalidParameter = 87;

        using var handle = CreateFileW(temp, delete | synchronize, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw Win32Failure(Marshal.GetLastPInvokeError(), $"could not open {temp} to publish it");

        // FILE_RENAME_INFO: Flags (a DWORD in a pointer-aligned union), RootDirectory (a handle; none, the
        // name is absolute), FileNameLength (bytes, without the NUL), then the name. Never smaller than the
        // struct itself.
        var name = Path.GetFullPath(path);
        var nameAt = 2 * IntPtr.Size + 4;
        var size = Math.Max(nameAt + (name.Length + 1) * sizeof(char), (nameAt + sizeof(char) + IntPtr.Size - 1) & ~(IntPtr.Size - 1));
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteInt32(info, 0, replaceIfExists | posixSemantics);
            Marshal.WriteIntPtr(info, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(info, 2 * IntPtr.Size, name.Length * sizeof(char));
            Marshal.Copy(name.ToCharArray(), 0, info + nameAt, name.Length);

            if (SetFileInformationByHandle(handle, fileRenameInfoEx, info, (uint)size)) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is invalidFunction or notSupported or invalidParameter) return false;
            throw Win32Failure(error, $"could not publish {path}");
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>A Win32 refusal as the IOException <c>File.Move</c> would have thrown, carrying the error's own words.</summary>
    static IOException Win32Failure(int error, string what) =>
        new($"{what}: {new System.ComponentModel.Win32Exception(error).Message}", unchecked((int)0x80070000) | error);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);
}
