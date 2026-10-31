using System.Globalization;
using System.Text;

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
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
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
