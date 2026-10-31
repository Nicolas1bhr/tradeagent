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

    /// <summary>THE COUNTER: <c>&lt;key&gt;.turns</c>, read the way <c>PuppetLife.Turns</c> reads it, while the puppet's writer rewrites it.</summary>
    [Fact]
    public void The_turn_counter_is_never_read_empty_while_the_puppet_rewrites_it()
    {
        var counter = Path.Combine(Folder(), "chair.turns");
        PuppetFiles.Write(counter, "0");
        var torn = Race(i => PuppetFiles.Write(counter, i.ToString()), () => PuppetFiles.ReadCount(counter), output);
        Assert.True(torn.Count == 0, $"a reader met a torn counter {torn.Count} time(s); first: {torn.FirstOrDefault()}");
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
}
