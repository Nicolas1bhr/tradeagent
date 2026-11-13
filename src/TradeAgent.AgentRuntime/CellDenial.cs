namespace TradeAgent.AgentRuntime;

/// <summary>
/// One thing a Windows denial row tries: <paramref name="Word"/> names it in the output; <paramref name="Command"/> is the line, <c>{path}</c> where the target
/// goes and <c>{sink}</c> a file name for what it prints; <paramref name="Denied"/> the target the cell must not reach; <paramref name="Control"/> a target of
/// the same kind in the workspace, which it may; <paramref name="OutsideTarget"/> what the line tries outside a cell when that may not be <paramref name="Denied"/>
/// itself (a file before the real inbox's scanner, a pipe that must see no client).
/// </summary>
public sealed record DenialProbe(string Word, string Command, string Denied, string Control, string? OutsideTarget = null);

/// <summary>
/// HOW A WINDOWS BENCHMARK ROW MAY SAY DENIED, platform-free so its verdict is tested everywhere (<c>U-cell-windows-egress</c> item 3;
/// <see cref="WindowsCellBenchmark.DenialRowAsync"/> runs it in a real cell). Windows' words are in the system's language and are never read: the verdict is
/// three runs of one line, and a failure that is not the guard's fails outside a cell too.
/// </summary>
public static class CellDenial
{
    /// <summary>The cmd body of <paramref name="probes"/>: each one's line on the workspace target (<c>CTL-word-…</c>) and on the forbidden one; and the paths each run fills it with.</summary>
    public static (string Body, IReadOnlyList<string> CellPaths, IReadOnlyList<string> OutsidePaths) Lines(IReadOnlyList<DenialProbe> probes)
    {
        var lines = new List<string>();
        var cell = new List<string>();
        var free = new List<string>();
        for (var i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            string Line(string word, int slot, string sink) =>
                p.Command.Replace("{path}", $"{{{slot}}}", StringComparison.Ordinal).Replace("{sink}", sink, StringComparison.Ordinal) + $" && (echo {word}-OPEN) || (echo {word}-DENIED)";
            lines.Add(Line("CTL-" + p.Word, 2 * i, $"ctl{i}.out"));
            lines.Add(Line(p.Word, 2 * i + 1, $"try{i}.out"));
            cell.AddRange([p.Control, p.Denied]);
            free.AddRange([p.Control, p.OutsideTarget ?? p.Denied]);
        }
        return (string.Join('\n', lines), cell, free);
    }

    /// <summary>
    /// THE VERDICT OF ONE DENIAL ROW, from what its two runs printed, read by whole lines. Held only when, for every word, the workspace target opened in the
    /// cell, the forbidden one was denied there and never opened, and the same line opened outside any cell.
    /// </summary>
    public static (bool Held, string Seen) Judge(IReadOnlyList<string> words, string cell, string outside)
    {
        static HashSet<string> Printed(string o) => [.. o.Split('\n').Select(l => l.Trim())];
        var (inside, free) = (Printed(cell), Printed(outside));
        var problems = new List<string>();
        foreach (var w in words)
        {
            if (!inside.Contains($"CTL-{w}-OPEN")) problems.Add($"{w}: the line does not run in a cell even on the workspace, so its failure proves nothing");
            if (!inside.Contains($"{w}-DENIED")) problems.Add($"{w}: not denied");
            if (inside.Contains($"{w}-OPEN")) problems.Add($"{w}: OPEN in the cell");
            if (!free.Contains($"{w}-OPEN")) problems.Add($"{w}: the same line fails outside a cell too, so the cell's failure proves nothing");
        }
        static string Flat(string s) => s.Trim().Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
        return problems.Count == 0
            ? (true, $"{string.Join(", ", words)}: denied in the cell; the same line opens its control inside it, and this target outside it")
            : (false, $"{string.Join("; ", problems)} (in the cell: {Flat(cell)}; outside: {Flat(outside)})");
    }

    /// <summary>
    /// THE WINDOWS CELL PROBE'S FILE NAME (<c>U-cell-probe</c>; <c>src/TradeAgent.CellProbe</c>): a compiled program shipped beside the app, run in a cell for the rows
    /// cmd's own lines cannot make, in place of PowerShell 5.1, which an enforced App Control policy (Smart App Control On included) puts in ConstrainedLanguage
    /// (R31 D5). Its lines say DENIED only on Windows' error 5, never on words; any other failure is <c>&lt;WORD&gt;-FAILED &lt;number&gt;</c>, which <see cref="Judge"/> fails.
    /// </summary>
    public const string ProbeExe = "cell-probe.exe";

    /// <summary>Where the installed app keeps the probe: beside itself (<c>packaging/build.ps1</c> publishes it there).</summary>
    public static string ProbeBesideTheApp => Path.Combine(AppContext.BaseDirectory, ProbeExe);

    /// <summary>
    /// THE KILL PROBE, ONE CALL FOR A CELL AND FOR OUTSIDE ONE: the probe starts a process of its own and asks Windows itself, <c>OpenProcess</c> for termination then
    /// <c>TerminateProcess</c>, for that one (<c>CTL-KILL-…</c>: the call works from where it runs) and for <paramref name="target"/> (<c>KILL-…</c>). Never by the
    /// process list: a cell cannot even list a process outside it, so going by the list would report a process it never tried to open.
    /// </summary>
    public static string[] KillProbe(int target) => ["kill", target.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary>
    /// THE LISTING PROBE, ONE CALL FOR A CELL AND FOR OUTSIDE ONE: .NET's own directory call on <paramref name="control"/> (<c>CTL-LIST-…</c>) and on <paramref name="denied"/> (<c>LIST-…</c>).
    /// </summary>
    public static string[] ListProbe(string control, string denied) => ["list", control, denied];

    /// <summary>
    /// A cmd line as ARGUMENTS: <paramref name="body"/> split on spaces ('\n' the statement separator, as " &amp; "), each token <c>{i}</c> replaced by
    /// <paramref name="paths"/>[i] whole. The quoting of a cell's command line (<c>AppContainerCell.CommandLine</c>) puts an argument holding a space in plain
    /// quotes, as cmd reads them, and no quote is ever inside one, so nothing reaches cmd as the C runtime's <c>\"</c>. A path that has no space is not quoted, so what cmd
    /// would read as an operator is escaped with its caret. A path may not end in a backslash or hold a quote.
    /// </summary>
    public static IReadOnlyList<string> CmdTokens(string body, params string[] paths)
    {
        foreach (var path in paths)
            if (path.Contains('"') || path.EndsWith('\\'))
                throw new ArgumentException($"a cell row's path cannot be passed to cmd as one argument: {path}");
        return [.. body.Replace("\n", " & ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
            t.Length >= 3 && t[0] == '{' && t[^1] == '}' && int.TryParse(t[1..^1], out var i) && i < paths.Length ? Escaped(paths[i]) : t)];
    }

    static string Escaped(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? path : string.Concat(path.Select(c => c is '&' or '|' or '<' or '>' or '^' ? $"^{c}" : c.ToString()));
}
