using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TradeAgent.Tests.PuppetIo;

/// <summary>
/// THE FILES A PUPPET LEAVES FOR THE TEST THAT STARTED IT (<c>&lt;key&gt;.turns</c>, <c>&lt;key&gt;.n.prompt</c>, <c>&lt;key&gt;.n.log</c>,
/// <c>&lt;key&gt;.json</c>) — written here and read here, so a reader never sees half of one.
///
/// <para>This file is compiled into the puppet program AND into the unit-test assembly (a linked source file), so the writer the
/// puppet ships and the reader the tests use are the ones the stress test drives. It references nothing of TradeAgent's.</para>
/// </summary>
internal static class PuppetFiles
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> so that a reader sees the old file or the new one, never a partial:
    /// a temp file in the same folder (a rename is atomic only inside one volume), flushed, then renamed over the target.
    /// </summary>
    public static void Write(string path, string text, Encoding? encoding = null)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():n}.tmp");
        try
        {
            File.WriteAllText(temp, text, encoding ?? new UTF8Encoding(false));
            Rename(temp, path);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// The rename over the target. WINDOWS HAS TWO GAPS HERE AND THE FIRST ANSWER CLOSED ONE. An ordinary rename over a file a reader has open at
    /// that instant is refused, even when the reader allows <see cref="FileShare.Delete"/> (CI run 41185326828, windows shard 5), so the writer
    /// tries again: that is the loop below, and it stays for a reader that does not allow it. But an ordinary rename also takes the old file through
    /// delete-pending under its name, and a reader whose open lands in that instant is refused in its turn: <see cref="UnauthorizedAccessException"/>
    /// on the read, 2 in 8 015 261 reads on main run 42340766269 and 1 in 12 263 332 on 42309831845, a refusal and no bytes, never a partial file.
    /// So the rename itself is the POSIX one (<see cref="WindowsRename"/>): the name moves to the new file in one step and the old one leaves the
    /// folder at once, while a reader that has it open keeps reading it. The READER is never made to wait or retry. Anywhere else the first try lands.
    /// </summary>
    static void Rename(string temp, string path)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !WindowsRename.Over(temp, path)) File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="line"/> to <paramref name="path"/>. One writer per file (a turn's own log), so the whole file is written
    /// again, atomically, with the line on its end; the file is never seen with half a line.
    /// </summary>
    public static void AppendLine(string path, string line)
    {
        var before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        Write(path, before + line + "\n");
    }

    /// <summary>
    /// The whole text of <paramref name="path"/>, opened so that the writer's rename over it is allowed while this read is open
    /// (<see cref="FileShare.Delete"/>: Windows refuses a rename over a file somebody holds open without it).
    /// </summary>
    public static string ReadAllText(string path, Encoding? encoding = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The number in the counter file <paramref name="path"/>, or 0 where there is no such file yet.</summary>
    public static int ReadCount(string path) =>
        File.Exists(path) ? int.Parse(ReadAllText(path).Trim(), CultureInfo.InvariantCulture) : 0;
}

/// <summary>
/// RENAME WITH POSIX SEMANTICS (<c>FILE_RENAME_FLAG_POSIX_SEMANTICS</c>, NTFS on Windows 10 1607 and later): <c>File.Move</c> asks for the ordinary
/// rename and offers no other, so the call is made here. Answers false where the volume does not offer it, and the caller makes the ordinary rename.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRename
{
    const uint Delete = 0x0001_0000, Synchronize = 0x0010_0000, FileAttributeNormal = 0x80;
    const int FileRenameInfoEx = 22, ReplaceIfExists = 1, PosixSemantics = 2;
    const int ErrorInvalidFunction = 1, ErrorNotSupported = 50, ErrorInvalidParameter = 87;

    /// <summary><c>FILE_RENAME_INFO</c> on 64-bit Windows: the flags, padding, the root handle, the name's length in bytes, then the name.</summary>
    const int FlagsAt = 0, LengthAt = 16, NameAt = 20;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);

    /// <summary>Moves <paramref name="temp"/> over <paramref name="path"/>. False where POSIX semantics are not on offer; a refusal throws as <c>File.Move</c> would.</summary>
    public static bool Over(string temp, string path)
    {
        using var source = CreateFileW(temp, Delete | Synchronize, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileAttributeNormal, IntPtr.Zero);
        if (source.IsInvalid) throw Refused(Marshal.GetLastPInvokeError());

        var target = Path.GetFullPath(path);
        var bytes = target.Length * sizeof(char);
        var size = NameAt + bytes + sizeof(char);
        var info = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(info, i, 0);
            Marshal.WriteInt32(info, FlagsAt, ReplaceIfExists | PosixSemantics);
            Marshal.WriteInt32(info, LengthAt, bytes);
            Marshal.Copy(target.ToCharArray(), 0, info + NameAt, target.Length);
            if (SetFileInformationByHandle(source, FileRenameInfoEx, info, (uint)size)) return true;
            return Marshal.GetLastPInvokeError() is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter
                ? false
                : throw Refused(Marshal.GetLastPInvokeError());
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>The Win32 error as the exception <c>File.Move</c> would have thrown for it: access denied or a sharing violation, both of which the writer retries.</summary>
    static Exception Refused(int error) => Marshal.GetExceptionForHR(unchecked((int)0x8007_0000) | error) ?? new IOException($"Windows error {error}");
}
