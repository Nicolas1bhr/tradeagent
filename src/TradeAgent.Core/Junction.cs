using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Core;

/// <summary>
/// AN NTFS JUNCTION (a mount-point reparse point), made by the app without elevation or Developer Mode — which a Windows symlink needs
/// (<c>U-kit-socket-2</c>: codex's skill root in a harness's per-attempt codex home, <see cref="Kit"/>). Made empty-folder-first and
/// pointed at an absolute folder; removed as a link (<see cref="DirectoryInfo.Delete()"/> on the junction itself), never walked.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Junction
{
    const uint ReparseTagMountPoint = 0xA0000003;
    const uint FsctlSetReparsePoint = 0x000900A4;
    const uint GenericWrite = 0x40000000;
    const uint OpenExisting = 3;
    const uint FlagBackupSemantics = 0x02000000;
    const uint FlagOpenReparsePoint = 0x00200000;

    /// <summary>
    /// Makes <paramref name="link"/> — which must not exist — a junction to the existing folder <paramref name="target"/>. A failure
    /// removes the empty folder it made and throws <see cref="IOException"/> with Windows's error.
    /// </summary>
    public static void Create(string link, string target)
    {
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Directory.Exists(to)) throw new DirectoryNotFoundException($"{to} is not a folder a junction can lead to");
        if (Directory.Exists(link) || File.Exists(link)) throw new IOException($"{link} already exists");
        Directory.CreateDirectory(link);
        try
        {
            using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"Windows would not open {link} to make it a junction (error {Marshal.GetLastWin32Error()})");
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + to);
            var print = Encoding.Unicode.GetBytes(to);
            // REPARSE_DATA_BUFFER, MountPointReparseBuffer: tag, data length, reserved; the two names' offsets and lengths; the names,
            // each followed by a NUL.
            var pathBuffer = substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[8 + 8 + pathBuffer];
            BitConverter.GetBytes(ReparseTagMountPoint).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)(8 + pathBuffer)).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"Windows would not make {link} a junction to {to} (error {Marshal.GetLastWin32Error()})");
        }
        catch
        {
            try { Directory.Delete(link); } catch (Exception) { /* the empty folder stays; it leads nowhere */ }
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
        out int returned, IntPtr overlapped);
}
