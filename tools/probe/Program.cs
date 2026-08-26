using System.Diagnostics;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;

// The evidence behind two claims in BUILD-STATUS.md, re-runnable:
//
//   probe install <runtime>   the AI tool installs itself from nothing, with no window
//   probe chat    <runtime>   a real conversation happens, and no window opens
//
// Point TRADEAGENT_HOME at a scratch directory first, or it will install into the real one.
// See tools/README.md.

var verb = args.Length > 0 ? args[0] : "install";
var id = args.Length > 1 ? args[1] : "codex";

var manifest = RuntimeCatalog.Find(id);
if (manifest is null) { Console.WriteLine($"no manifest for '{id}'"); return 2; }

Console.WriteLine($"TRADEAGENT_HOME = {Paths.Home}");
Console.WriteLine($"runtime         = {manifest.DisplayName}  install={manifest.Install.Kind}  repo={manifest.Install.GitHubRepo}");

var rt = new CliAgentRuntime(manifest);

if (verb == "install")
{
    var before = await rt.DetectAsync();
    Console.WriteLine($"before install  : installed={before.Installed} path={before.Path ?? "<none>"}");

    var sw = Stopwatch.StartNew();
    try
    {
        var d = await rt.InstallAsync(new Progress<string>(s => Console.WriteLine($"  [{sw.Elapsed:mm\\:ss}] {s}")));
        Console.WriteLine($"INSTALL OK      : path={d.Path} version={d.Version} managed={d.Managed} in {sw.Elapsed:mm\\:ss}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"INSTALL FAILED  : {ex.GetType().Name}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"auth state      : {await rt.GetAuthenticationStateAsync()}");
    return 0;
}

if (verb != "chat") { Console.WriteLine("usage: probe [install|chat] <runtime-id>"); return 2; }

var detected = await rt.DetectAsync();
Console.WriteLine($"runtime path    : {detected.Path ?? "<not installed>"} v{detected.Version}");
if (!detected.Installed) { Console.WriteLine("not installed — run `probe install` first"); return 1; }

var workspace = Path.Combine(Paths.Home, "workspace");
Directory.CreateDirectory(workspace);
await rt.CreateEnvironmentAsync(workspace, new Dictionary<string, string>());

var chat = rt.OpenConversation();
var deltas = 0;
chat.Delta += _ => deltas++;
chat.TurnAdded += t => Console.WriteLine($"  TURN [{t.Role}] {Trim(t.Text)}");
chat.StateChanged += () => Console.WriteLine($"  busy={chat.Busy}");

// The number this whole harness exists to print. Counted across every process, before and after,
// because "no terminal" is a claim about what the user sees and nothing else measures that.
var windowsBefore = VisibleWindows();

await chat.StartAsync();
var clock = Stopwatch.StartNew();
await chat.SendAsync("Reply with exactly this and nothing else: TRADEAGENT_OK");
Console.WriteLine($"elapsed         : {clock.Elapsed:mm\\:ss}   deltas={deltas}   turns={chat.History.Count}");

var windowsAfter = VisibleWindows();
Console.WriteLine($"visible windows : before={windowsBefore} after={windowsAfter}  ->  " +
                  (windowsAfter <= windowsBefore ? "NO WINDOW OPENED" : "A WINDOW OPENED"));

var reply = chat.History.LastOrDefault(t => t.Role == ChatRole.Ai)?.Text ?? "<no AI turn>";
Console.WriteLine($"reply           : {Trim(reply)}");
await chat.StopAsync();

var ok = reply.Contains("TRADEAGENT_OK") && windowsAfter <= windowsBefore;
Console.WriteLine(ok ? "CONVERSATION OK" : "CONVERSATION FAILED");
return ok ? 0 : 1;

static string Trim(string s)
{
    var t = s.Replace("\r", "").Replace("\n", " / ");
    return t.Length > 160 ? t[..160] + "..." : t;
}

static int VisibleWindows() =>
    Process.GetProcesses().Count(p => { try { return p.MainWindowHandle != 0; } catch { return false; } });
