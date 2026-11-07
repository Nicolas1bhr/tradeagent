using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeAgent.Core;

/// <summary>One declared conformance vector: its name, the records fed to the tool in time order, and, when declared, what it must answer.</summary>
public sealed record ConformanceVector(string Name, IReadOnlyList<string> Input, IReadOnlyList<string>? Expect);

/// <summary>One run of a tool in a cell: its exit code, what it wrote to standard output, whether it was stopped at the time limit, and the policy it ran under.</summary>
public sealed record ToolCellRun(int ExitCode, string Stdout, bool TimedOut, string PolicyHash);

/// <summary>A conformance measurement as the app composed it: passed or not, how many vectors and runs, and what failed first in the app's words.</summary>
public sealed record ConformanceResult(bool Passed, int Vectors, int Runs, string Detail, string? PolicyHash);

/// <summary>
/// RUNS A TOOL'S PUBLISHED COPY IN A CELL (<c>U-admission-ladder</c>): the tool's folder read and run, a fresh empty workspace, no network,
/// nothing else — and never any other way. <see cref="Unavailable"/> says why this computer cannot (no cell), and then nothing is run.
/// </summary>
public interface IToolCellRunner
{
    /// <summary>Why no tool can be run in a cell on this computer, or null when one can.</summary>
    string? Unavailable { get; }

    /// <summary>Runs <paramref name="program"/> (inside <paramref name="toolDir"/>) with <paramref name="input"/> on its standard input, one record a line.</summary>
    ToolCellRun Run(string toolDir, string program, IReadOnlyList<string> input);
}

/// <summary>
/// THE CONFORMANCE CONTRACT OF A TOOL THAT FEEDS EVIDENCE (<c>U-admission-ladder</c>; EDGE-FACTORY § 4.7: "conformance-tested (point-in-time
/// vectors; a seeded look-ahead adapter must fail)"). The tool declares its vectors in <see cref="FileName"/> at the top of its folder:
/// <code>{"conformance": 1, "vectors": [{"name": "…", "input": ["record 1", "record 2", …], "expect": ["answer 1", …]}]}</code>
/// <para><b>A point-in-time tool answers one line per record, and its answer for a record never changes when later records are
/// withheld.</b> The app feeds every vector whole, then cut at a few points (<see cref="Cuts"/>), each in its own cell run: the answer
/// for the whole must be one line per record and equal <c>expect</c> where declared, and the answer for each cut must equal the first
/// lines of the whole's. A tool that reads ahead — its answer for record <c>i</c> uses record <c>i+1</c> — answers its last kept record
/// differently once the later ones are withheld, and fails. The app composes the detail from its own words, numbers and the vector's
/// checked name, never from what the tool wrote.</para>
/// </summary>
public static class ToolConformance
{
    /// <summary>The file a tool declares its vectors in, at the top of its folder.</summary>
    public const string FileName = "conformance.json";

    /// <summary>The one form this build reads.</summary>
    public const int Version = 1;

    /// <summary>At most this many vectors.</summary>
    public const int MaxVectors = 16;

    /// <summary>Each vector holds at least this many records, so that it can be cut.</summary>
    public const int MinRecords = 2;

    /// <summary>Each vector holds at most this many records.</summary>
    public const int MaxRecords = 200;

    /// <summary>The longest record or answer line.</summary>
    public const int MaxLine = 1000;

    static readonly Regex NameRule = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>Reads and checks a vectors file's bytes, or says why not.</summary>
    public static (IReadOnlyList<ConformanceVector>? Vectors, string? Problem) Read(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, $"{FileName} is not a JSON object");
            if (!root.TryGetProperty("conformance", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
                return (null, $"{FileName} does not say \"conformance\": {Version}");
            if (!root.TryGetProperty("vectors", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxVectors)
                return (null, $"{FileName} holds 1 to {MaxVectors} vectors");
            var vectors = new List<ConformanceVector>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return (null, "a vector is not a JSON object");
                var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                if (!NameRule.IsMatch(name)) return (null, "a vector's name is lower-case letters, digits, '.', '_' and '-', at most 64, starting with a letter or digit");
                if (vectors.Any(x => x.Name == name)) return (null, $"two vectors are named '{name}'");
                var input = Lines(e, "input");
                if (input is null || input.Count is < MinRecords or > MaxRecords)
                    return (null, $"vector '{name}' feeds {MinRecords} to {MaxRecords} records, each a string of at most {MaxLine} characters on one line");
                IReadOnlyList<string>? expect = null;
                if (e.TryGetProperty("expect", out _))
                {
                    expect = Lines(e, "expect");
                    if (expect is null || expect.Count != input.Count)
                        return (null, $"vector '{name}' expects one answer per record ({input.Count}), each a string of at most {MaxLine} characters on one line");
                }
                vectors.Add(new ConformanceVector(name, input, expect));
            }
            return (vectors, null);
        }
        catch (JsonException) { return (null, $"{FileName} is not valid JSON"); }
    }

    static List<string>? Lines(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var a) || a.ValueKind != JsonValueKind.Array) return null;
        var lines = new List<string>();
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) return null;
            var s = x.GetString()!;
            if (s.Length > MaxLine || s.Contains('\n') || s.Contains('\r')) return null;
            lines.Add(s);
        }
        return lines;
    }

    /// <summary>
    /// WHERE A VECTOR OF <paramref name="records"/> IS CUT: the last record withheld, and the first, a quarter, a half and three quarters
    /// kept — each a count of records kept, distinct, at least one and fewer than all.
    /// </summary>
    public static IReadOnlyList<int> Cuts(int records) =>
        new[] { records - 1, 1, records / 4, records / 2, records * 3 / 4 }.Where(k => k >= 1 && k < records).Distinct().OrderBy(k => k).ToList();

    /// <summary>The answer lines of one run: split on new lines, a carriage return before one dropped, one final empty line dropped.</summary>
    public static IReadOnlyList<string> Answers(string stdout)
    {
        var lines = stdout.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// MEASURES <paramref name="vectors"/> through <paramref name="run"/> — every vector whole, then cut — and stops at the first failure.
    /// Never throws for what the tool did: a run that throws is a failure in words.
    /// </summary>
    public static ConformanceResult Measure(IReadOnlyList<ConformanceVector> vectors, Func<IReadOnlyList<string>, ToolCellRun> run)
    {
        var runs = 0;
        string? policy = null;
        foreach (var v in vectors)
        {
            var whole = Once(v.Input);
            if (whole.Failure is { } f) return Failed($"vector '{v.Name}' with all {v.Input.Count} records: {f}");
            var answers = whole.Answers!;
            if (answers.Count != v.Input.Count)
                return Failed($"vector '{v.Name}': {v.Input.Count} records were fed and {answers.Count} answer lines came back; a point-in-time tool answers one line per record");
            if (v.Expect is { } expect)
                for (var i = 0; i < expect.Count; i++)
                    if (!string.Equals(answers[i], expect[i], StringComparison.Ordinal))
                        return Failed($"vector '{v.Name}': the answer for record {(i + 1).ToString(CultureInfo.InvariantCulture)} is not the one the vector expects");
            foreach (var k in Cuts(v.Input.Count))
            {
                var cut = Once(v.Input.Take(k).ToList());
                if (cut.Failure is { } g) return Failed($"vector '{v.Name}' with its first {k} records: {g}");
                var part = cut.Answers!;
                if (part.Count != k)
                    return Failed($"vector '{v.Name}': with its first {k} records kept, {part.Count} answer lines came back; a point-in-time tool answers one line per record");
                for (var i = 0; i < k; i++)
                    if (!string.Equals(part[i], answers[i], StringComparison.Ordinal))
                        return Failed($"vector '{v.Name}': the answer for record {(i + 1).ToString(CultureInfo.InvariantCulture)} changes when the records after "
                                      + $"record {k} are withheld — it reads later records (look-ahead), so it is not point-in-time");
            }
        }
        return new ConformanceResult(true, vectors.Count, runs,
            $"every vector answered one line per record, as expected where declared, and the same when later records were withheld ({runs} cell runs)", policy);

        ConformanceResult Failed(string why) => new(false, vectors.Count, runs, why, policy);

        (IReadOnlyList<string>? Answers, string? Failure) Once(IReadOnlyList<string> input)
        {
            runs++;
            ToolCellRun r;
            try { r = run(input); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, "the cell run could not be made (" + ex.GetType().Name + ")");
            }
            policy ??= r.PolicyHash;
            if (r.TimedOut) return (null, "the tool did not finish within the time limit");
            if (r.ExitCode != 0) return (null, $"the tool exited with code {r.ExitCode.ToString(CultureInfo.InvariantCulture)}");
            return (Answers(r.Stdout), null);
        }
    }
}
