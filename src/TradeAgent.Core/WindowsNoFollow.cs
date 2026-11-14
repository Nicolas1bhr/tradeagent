using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>
/// WINDOWS' <c>openat</c>, AND A RENAME INSIDE THE FOLDER A HANDLE HOLDS (<c>U-windows-cards-3</c>): a name opened from the folder above
/// it by that folder's handle (<c>NtCreateFile</c> with <c>RootDirectory</c>), never by a path, and never through a reparse point at it
/// (<c>FILE_OPEN_REPARSE_POINT</c>: a junction, a symbolic link or a mount point is opened as itself). What is checked is the handle that
/// is used, so there is no window between a look and an act for a link to be made in.
///
/// <para><b>Measured on Windows 11 (2026-11-14), and why the documented call is not used:</b> <c>SetFileInformationByHandle</c> with
/// <c>FileRenameInfo</c> or <c>FileRenameInfoEx</c> refuses a <c>RootDirectory</c> handle (error 87), and with none it resolves a bare new
/// name against the PROCESS's current folder — it moved a test folder out of its parent into the program's working folder. So a rename here
/// is <c>NtSetInformationFile(FileRenameInformation)</c> with the parent's handle as its root, which renames inside that folder.</para>
///
/// <para>Also measured: while a handle is open on a name, no folder above it can be renamed (Windows answers access denied), so a folder
/// on the way cannot be swapped for a link while what was opened below it is held.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsNoFollow
{
    public const uint ReadAttributes = 0x0080, Delete = 0x0001_0000, Synchronize = 0x0010_0000;

    /// <summary>FILE_GENERIC_WRITE: write data, append, attributes, extended attributes, read control, synchronize.</summary>
    public const uint GenericWrite = 0x0012_0116;

    public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4, ShareAll = 7;

    /// <summary>CreateDisposition: open what is there; create only where nothing is.</summary>
    public const uint Open = 1, Create = 2;

    /// <summary>CreateOptions.</summary>
    public const uint DirectoryFile = 0x0001, NonDirectoryFile = 0x0040;

    const uint SynchronousIo = 0x0020, OpenReparsePoint = 0x0020_0000;

    public const int Success = 0;
    public const int NameNotFound = unchecked((int)0xC000_0034), PathNotFound = unchecked((int)0xC000_003A);
    public const int NameCollision = unchecked((int)0xC000_0035), AccessDenied = unchecked((int)0xC000_0022);

    /// <summary>
    /// A folder by its path, FOLLOWED: the root of a walk, whose ancestry is the caller's choice and not the writer of what is below it.
    /// Everything below it is opened from it by handle.
    /// </summary>
    public static SafeFileHandle OpenRoot(string path)
    {
        var handle = CreateFileW(path, ReadAttributes | Synchronize, ShareAll, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x0200_0000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Win32Failure(error, path, "opened");
        }
        return handle;
    }

    /// <summary>
    /// The name <paramref name="name"/> inside the folder <paramref name="parent"/> holds, opened from that handle and never through a
    /// reparse point at it, or the NTSTATUS that refused it (<see cref="NameNotFound"/>, <see cref="NameCollision"/>…). One name only: a
    /// separator, a stream's colon, <c>.</c> or <c>..</c> is refused before anything is opened.
    /// </summary>
    public static int TryOpen(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options,
        out SafeFileHandle? opened)
    {
        opened = null;
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new IOException($"'{name}' is not one name inside a folder, so TradeAgent did not open it.");
        var buffer = Marshal.StringToHGlobalUni(name);
        var text = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = buffer
            }, text, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = text,
                Attributes = 0x40 /* OBJ_CASE_INSENSITIVE, as Windows' own path opens are */
            };
            var status = NtCreateFile(out var handle, access | Synchronize, ref attributes, out _, IntPtr.Zero, 0, share, disposition,
                options | SynchronousIo | OpenReparsePoint, IntPtr.Zero, 0);
            if (status == Success) opened = new SafeFileHandle(handle, ownsHandle: true);
            return status;
        }
        finally
        {
            if (added) parent.DangerousRelease();
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The attributes of what <paramref name="handle"/> holds — a reparse point's own, since nothing here follows one.</summary>
    public static FileAttributes AttributesOf(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var tag, 8))
            throw Win32Failure(Marshal.GetLastPInvokeError(), "an open name", "read");
        return (FileAttributes)tag.FileAttributes;
    }

    /// <summary>What Windows knows the object by on its volume — the volume's serial and the file's index — whatever it is named now.</summary>
    public static string IdentityOf(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw Win32Failure(Marshal.GetLastPInvokeError(), "an open name", "read");
        return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    /// <summary>
    /// <see cref="IdentityOf"/> of the name at <paramref name="path"/> itself — a link as itself, never its target — or null where it cannot
    /// be opened. A look, never an act: an act opens by handle from a root.
    /// </summary>
    public static string? IdentityAt(string path)
    {
        using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x0200_0000 | 0x0020_0000 /* FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        try { return IdentityOf(handle); }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Renames what <paramref name="item"/> holds to <paramref name="newName"/> INSIDE the folder <paramref name="parent"/> holds, never
    /// over anything there; the NTSTATUS (<see cref="NameCollision"/> where the name is taken). The item was opened with
    /// <see cref="Delete"/> access.
    /// </summary>
    public static int RenameIn(SafeFileHandle item, SafeFileHandle parent, string newName)
    {
        if (newName.Length == 0 || newName is "." or ".." || newName.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new IOException($"'{newName}' is not one name inside a folder, so TradeAgent did not rename to it.");
        // FILE_RENAME_INFORMATION on a 64-bit process: ReplaceIfExists at 0, RootDirectory at 8, FileNameLength at 16, FileName at 20.
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("TradeAgent renames by handle only as a 64-bit process.");
        var size = 20 + newName.Length * 2 + 2;
        var info = Marshal.AllocHGlobal(size);
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            for (var i = 0; i < size; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteIntPtr(info, 8, parent.DangerousGetHandle());
            Marshal.WriteInt32(info, 16, newName.Length * 2);
            Marshal.Copy(newName.ToCharArray(), 0, info + 20, newName.Length);
            return NtSetInformationFile(item, out _, info, (uint)size, 10 /* FileRenameInformation */);
        }
        finally
        {
            if (added) parent.DangerousRelease();
            Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>
    /// Removes the name <paramref name="item"/> holds — a link as itself, never its target — at once, even while another program has the
    /// file open (POSIX semantics); the NTSTATUS. The item was opened with <see cref="Delete"/> access.
    /// </summary>
    public static int RemoveByHandle(SafeFileHandle item)
    {
        var flags = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(flags, 0x1 | 0x2 /* FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS */);
            return NtSetInformationFile(item, out _, flags, 4, 64 /* FileDispositionInformationEx */);
        }
        finally { Marshal.FreeHGlobal(flags); }
    }

    /// <summary>An NTSTATUS as the exception a path-based call would have thrown, naming <paramref name="what"/> and never a byte of it.</summary>
    public static Exception Failure(int status, string what, string verb) => Win32Failure((int)RtlNtStatusToDosError(status), what, verb);

    static Exception Win32Failure(int error, string what, string verb)
    {
        var words = $"'{what}' could not be {verb}: {Marshal.GetPInvokeErrorMessage(error)} (Windows error {error}).";
        return error switch
        {
            2 => new FileNotFoundException(words),
            3 => new DirectoryNotFoundException(words),
            5 => new UnauthorizedAccessException(words),
            _ => new IOException(words)
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;   // FILETIMEs: two DWORDs, 4-aligned
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("ntdll.dll")]
    static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock io,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);

    [DllImport("ntdll.dll")]
    static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock io, IntPtr information, uint length, int informationClass);

    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInformation information);
}
