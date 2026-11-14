using System.Text.Json.Nodes;
using TradeAgent.Tests.PuppetIo;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A READER OF A PUPPET'S FILE NEVER SEES A PARTIAL ONE (main CI run 41167678294: <c>PuppetLife.Turns</c> threw
/// <c>FormatException: The input string '' was not in a correct format</c> on ubuntu). The puppet rewrote <c>&lt;key&gt;.turns</c>
/// in place — truncate, then write — and a reader that came between the two read an empty file. These tests drive the writer the
/// puppet ships (<see cref="PuppetFiles"/>, one source file in both assemblies) against the reader the lives use, with no retry,
/// no sleep and nothing caught on the reader's side.
/// </summary>
public class PuppetFilesTests(ITestOutputHelper output)
{
    const int Rewrites = 20_000;
    const int LogLines = 1_500;

    static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "puppet-files-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A writer thread rewrites until done; a reader thread reads in a loop meanwhile. Returns the reads that threw.</summary>
    static List<string> Race(Action<int> write, Func<int> read, ITestOutputHelper output, int rewrites = Rewrites)
    {
        var failures = new List<string>();
        var done = false;
        var reads = 0;
        var writer = new Thread(() =>
        {
            for (var i = 1; i <= rewrites; i++) write(i);
            Volatile.Write(ref done, true);
        });
        var reader = new Thread(() =>
        {
            var last = 0;
            while (!Volatile.Read(ref done))
            {
                try
                {
                    var seen = read();
                    reads++;
                    if (seen < last) failures.Add($"the file went backwards: {last} then {seen}");
                    last = seen;
                }
                catch (Exception e) { failures.Add($"{e.GetType().Name}: {e.Message}"); if (failures.Count > 20) return; }
            }
        });
        reader.Start();
        writer.Start();
        writer.Join();
        reader.Join();
        output.WriteLine($"{reads} reads against {rewrites} rewrites; {failures.Count} torn");
        return failures;
    }

    /// <summary>
    /// THE COUNTER: <c>&lt;key&gt;.turns</c>, read the way <c>PuppetLife.Turns</c> reads it, while the puppet's writer bumps it. CHANGED IN THE OPEN
    /// (<c>U-turn-cards-2</c> item 4): the writer the puppet ships is now <see cref="PuppetFiles.Bump"/>, an append, where it was a rewrite renamed
    /// over the file (<see cref="PuppetFiles.Write"/>), so the race drives that writer; the reader, its flags and the zero-failure bar are unchanged,
    /// and the count must end at every bump.
    /// </summary>
    [Fact]
    public void The_turn_counter_is_never_read_empty_while_the_puppet_rewrites_it()
    {
        var counter = Path.Combine(Folder(), "chair.turns");
        var torn = Race(_ => PuppetFiles.Bump(counter), () => PuppetFiles.ReadCount(counter), output);
        Assert.True(torn.Count == 0, $"a reader met a torn counter {torn.Count} time(s); first: {torn.FirstOrDefault()}");
        Assert.Equal(Rewrites, PuppetFiles.ReadCount(counter));
    }

    /// <summary>THE LOG: <c>&lt;key&gt;.n.log</c> grows one JSON line per step; a reader parses every line it sees and none is half a line.</summary>
    [Fact]
    public void A_turns_log_is_never_read_with_half_a_line_while_the_puppet_adds_to_it()
    {
        var log = Path.Combine(Folder(), "chair.1.log");
        var next = 0;
        var torn = Race(
            _ => PuppetFiles.AppendLine(log, new JsonObject { ["step"] = "write", ["n"] = ++next, ["pad"] = new string('x', 64) }.ToJsonString()),
            () => File.Exists(log)
                ? PuppetFiles.ReadAllText(log).Split('\n').Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!["n"]!.GetValue<int>()).DefaultIfEmpty(0).Max()
                : 0,
            output, LogLines);
        Assert.True(torn.Count == 0, $"a reader met a torn log {torn.Count} time(s); first: {torn.FirstOrDefault()}");
    }

    /// <summary>
    /// THE LOG IS ADDED TO IN PLACE, NEVER RENAMED OVER (<c>U-ci-precheck</c> item 3). A reader that opened the log before a step's line was added
    /// reads that line through the handle it already holds: the file it opened is the file that grew. A rewrite renamed over the log left that
    /// handle on the old file, unlinked, without the line, on every platform; and on Windows the rename is what refused a reader whose open landed
    /// while the old file was being deleted (the counter's red, which <see cref="PuppetFiles.Bump"/> closed the same way).
    /// </summary>
    [Fact]
    public void A_steps_line_reaches_a_reader_that_opened_the_log_before_it()
    {
        var log = Path.Combine(Folder(), "chair.1.log");
        PuppetFiles.AppendLine(log, """{"step":"prompt"}""");
        using var held = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        PuppetFiles.AppendLine(log, """{"step":"write"}""");

        using var reader = new StreamReader(held);
        Assert.Equal("{\"step\":\"prompt\"}\n{\"step\":\"write\"}\n", reader.ReadToEnd());
    }

    /// <summary>
    /// NO STEP'S LINE CROSSES A PAGE (<see cref="PuppetFiles.Page"/>): a line that would is moved to the next page by newlines, so a reader on Linux,
    /// where an appended write becomes visible a page at a time, sees each line whole or not at all; and the readers, which drop empty lines, read
    /// every line and nothing else.
    /// </summary>
    [Fact]
    public void No_steps_line_crosses_a_page_and_the_readers_see_every_line()
    {
        var log = Path.Combine(Folder(), "chair.1.log");
        var lines = Enumerable.Range(1, 40).Select(n => new JsonObject { ["step"] = "write", ["n"] = n, ["pad"] = new string('x', 100 + n * 37 % 900) }.ToJsonString()).ToList();
        foreach (var line in lines) PuppetFiles.AppendLine(log, line);

        var bytes = File.ReadAllBytes(log);
        var start = 0;
        var crossed = new List<string>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (i > start && start / PuppetFiles.Page != i / PuppetFiles.Page) crossed.Add($"bytes {start}..{i}");
            start = i + 1;
        }
        Assert.True(crossed.Count == 0, "a step's line crosses a page: " + string.Join(", ", crossed));
        Assert.Equal(lines, PuppetFiles.ReadAllText(log).Split('\n').Where(l => l.Length > 0));
    }
}
