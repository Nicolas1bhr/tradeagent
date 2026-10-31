using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Tests.PuppetIo;

// THE PUPPET SEAT (U-puppet-runtime; docs/ORCHESTRATION.md § 2.7 and § 7): a program that stands where an agent CLI stands and
// plays a script instead of asking a model. The real AgentSession starts it — the prompt on its stdin, the launch's own grant in
// its environment, held by the real ProcessContainment — and every effect it has goes through the same doors a seat's would:
// `trade` found on its PATH, files in its own home, and a codex `--json` event stream on stdout that the session parses and the
// meter prices. No model, no network, no cost. It references nothing of TradeAgent's.
//
// Command line:  TradeAgent.Puppet --scripts <dir> [anything else, ignored]    one turn: the prompt is read from stdin
//                TradeAgent.Puppet --version                                    answers its version and exits
//
// THE SCRIPT IS FOUND BY POSITION: the turn runs in the position's home, and the home's own folder name is the key —
// <dir>/<key>.json holds {"turns": [[step, …], [step, …], …]}. The n-th turn of that position plays turns[n-1], the last one
// again once they run out; a position with no script answers "Nothing to do." Steps, one string each:
//   trade <args…>          runs the `trade` on PATH with these words ("…" keeps spaces, \" is a quote kept as itself); its exit
//                          code and output are logged
//   write <path> <text…>   writes text to a file under the home (out/… for a publication)
//   usage <in> <cached> <out>   reports token usage as codex does (turn.completed), so the session's own parser meters it
//   final <text…>          the turn's answer (an agent_message item)
//   signal <name>          creates <dir>/<name>, telling the test the script reached this line
//   await <name>           waits until <dir>/<name> exists (five minutes at most, then exits 3)
//   exit <n>               ends the turn with that code
//   hang                   never ends on its own: only the turn's teardown ends it
//   crash                  dies at once, with nothing flushed and no answer
// In any step, {prompt:PATTERN} is replaced by PATTERN's first group as matched in this turn's prompt — what the seat was told, never
// the database (U-org-scorecards-b fix pass 1); PATTERN holds no '}'. A pattern the prompt does not match ends the turn with code 4.
// What it did is written beside the scripts: <key>.<n>.prompt (the prompt as received) and <key>.<n>.log (one JSON line per
// step that has a result). <dir>/launches.log gains a line before anything else, on every start but the version probe.

var argv = args.ToList();
if (argv.Contains("--version"))
{
    Console.WriteLine("tradeagent-puppet 1.0.0");
    return 0;
}

var at = argv.IndexOf("--scripts");
if (at < 0 || at + 1 >= argv.Count)
{
    Console.Error.WriteLine("the puppet needs --scripts <dir>");
    return 2;
}
var scripts = argv[at + 1];
Directory.CreateDirectory(scripts);

var home = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var key = Path.GetFileName(home);
File.AppendAllText(Path.Combine(scripts, "launches.log"), $"{key} {Environment.ProcessId}\n");

// The prompt, whole: AgentSession writes it and closes stdin (U-prompt-stdin).
string prompt;
using (var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
    prompt = stdin.ReadToEnd();

var n = NextTurn(scripts, key);
PuppetFiles.Write(Path.Combine(scripts, $"{key}.{n}.prompt"), prompt, new UTF8Encoding(false));
var log = Path.Combine(scripts, $"{key}.{n}.log");

var items = 0;
foreach (var scripted in StepsFor(scripts, key, n))
{
    if (FromPrompt(scripted, prompt) is not { } step)
    {
        Log(log, new JsonObject { ["step"] = "prompt", ["unmatched"] = scripted });
        return 4;
    }
    var (verb, rest) = Split(step);
    switch (verb)
    {
        case "trade":
        {
            var words = Words(rest);
            var (code, stdout, stderr) = Trade(words);
            var id = $"puppet_cmd_{++items}";
            Emit(new JsonObject
            {
                ["type"] = "item.completed",
                ["item"] = new JsonObject
                {
                    ["id"] = id, ["type"] = "command_execution", ["command"] = "trade " + rest,
                    ["aggregated_output"] = stdout, ["exit_code"] = code, ["status"] = code == 0 ? "completed" : "failed"
                }
            });
            Log(log, new JsonObject { ["step"] = "trade", ["args"] = new JsonArray([.. words.Select(w => (JsonNode?)w)]),
                ["exit"] = code, ["stdout"] = stdout, ["stderr"] = stderr });
            break;
        }
        case "write":
        {
            var (path, text) = Split(rest);
            var file = Path.GetFullPath(Path.Combine(home, path.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            Log(log, new JsonObject { ["step"] = "write", ["path"] = path });
            break;
        }
        case "usage":
        {
            var w = Words(rest).Select(long.Parse).ToArray();
            Emit(new JsonObject
            {
                ["type"] = "turn.completed",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = w[0], ["cached_input_tokens"] = w[1], ["cache_write_input_tokens"] = 0,
                    ["output_tokens"] = w[2], ["reasoning_output_tokens"] = 0
                }
            });
            break;
        }
        case "final":
            Emit(new JsonObject
            {
                ["type"] = "item.completed",
                ["item"] = new JsonObject { ["id"] = $"puppet_msg_{++items}", ["type"] = "agent_message", ["text"] = rest }
            });
            break;
        case "signal":
            File.WriteAllText(Path.Combine(scripts, rest), "");
            break;
        case "await":
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!File.Exists(Path.Combine(scripts, rest)))
            {
                if (DateTime.UtcNow > deadline) return 3;
                Thread.Sleep(50);
            }
            break;
        }
        case "exit":
            return int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture);
        case "hang":
            Thread.Sleep(Timeout.Infinite);
            break;
        case "crash":
            Process.GetCurrentProcess().Kill();
            break;
        default:
            Console.Error.WriteLine($"the puppet does not know the step '{step}'");
            return 2;
    }
}
return 0;

// ---- the script ---------------------------------------------------------------------------------------------------------

static int NextTurn(string scripts, string key)
{
    var counter = Path.Combine(scripts, $"{key}.turns");
    var n = PuppetFiles.ReadCount(counter) + 1;
    PuppetFiles.Write(counter, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return n;
}

static List<string> StepsFor(string scripts, string key, int n)
{
    var file = Path.Combine(scripts, $"{key}.json");
    if (!File.Exists(file)) return ["final Nothing to do."];
    var turns = JsonNode.Parse(PuppetFiles.ReadAllText(file))?["turns"]?.AsArray() ?? [];
    if (turns.Count == 0) return ["final Nothing to do."];
    var turn = turns[Math.Min(n, turns.Count) - 1]?.AsArray() ?? [];
    return [.. turn.Select(s => s?.GetValue<string>() ?? "")];
}

/// <summary>The step with each {prompt:PATTERN} replaced by PATTERN's first group in the prompt, or null where the prompt does not match.</summary>
static string? FromPrompt(string step, string prompt)
{
    const string open = "{prompt:";
    for (var at = step.IndexOf(open, StringComparison.Ordinal); at >= 0; at = step.IndexOf(open, StringComparison.Ordinal))
    {
        var end = step.IndexOf('}', at);
        if (end < 0) return null;
        var match = System.Text.RegularExpressions.Regex.Match(prompt, step[(at + open.Length)..end]);
        if (!match.Success || match.Groups.Count < 2) return null;
        step = step[..at] + match.Groups[1].Value + step[(end + 1)..];
    }
    return step;
}

static (string Verb, string Tail) Split(string s)
{
    s = s.Trim();
    var space = s.IndexOf(' ');
    return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
}

/// <summary>Whitespace-separated words; a double-quoted run is one word, without its quotes.</summary>
static List<string> Words(string s)
{
    var words = new List<string>();
    var current = new StringBuilder();
    var quoted = false;
    var any = false;
    for (var i = 0; i < s.Length; i++)
    {
        var c = s[i];
        // \" IS A QUOTE KEPT AS ITSELF (U-org-verbs-a): a JSON argument such as --refs carries its own double quotes.
        if (c == '\\' && i + 1 < s.Length && s[i + 1] == '"') { current.Append('"'); any = true; i++; continue; }
        if (c == '"') { quoted = !quoted; any = true; continue; }
        if (char.IsWhiteSpace(c) && !quoted)
        {
            if (any) words.Add(current.ToString());
            current.Clear();
            any = false;
            continue;
        }
        current.Append(c);
        any = true;
    }
    if (any) words.Add(current.ToString());
    return words;
}

// ---- the doors a seat has ---------------------------------------------------------------------------------------------

/// <summary>
/// THE `trade` ON PATH, found as a shell finds it, run with the puppet's own environment — the launch's grant, the pipe, the
/// home — by inheritance, exactly as a vendor CLI's shell would run it.
/// </summary>
static (int Code, string Out, string Err) Trade(List<string> words)
{
    var exe = OnPath("trade");
    if (exe is null) return (127, "", "trade: not found on PATH");
    var psi = new ProcessStartInfo(exe)
    {
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    foreach (var w in words) psi.ArgumentList.Add(w);
    using var p = Process.Start(psi)!;
    p.StandardInput.Close();
    var so = p.StandardOutput.ReadToEndAsync();
    var se = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(TimeSpan.FromMinutes(2)))
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception) { /* gone */ }
        return (124, so.IsCompleted ? so.Result : "", "trade did not answer within two minutes");
    }
    return (p.ExitCode, so.Result, se.Result);
}

static string? OnPath(string name)
{
    var names = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(ext => name + ext.ToLowerInvariant()).Prepend(name + ".exe").Distinct().ToArray()
        : [name];
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var n in names)
        {
            var candidate = Path.Combine(dir, n);
            if (File.Exists(candidate)) return candidate;
        }
    return null;
}

static void Emit(JsonObject e) => Console.Out.WriteLine(e.ToJsonString());

static void Log(string file, JsonObject entry) => PuppetFiles.AppendLine(file, entry.ToJsonString());
