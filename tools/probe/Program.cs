using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

// The evidence behind the claims in BUILD-STATUS.md, re-runnable:
//
//   probe install <runtime>   the AI tool installs itself from nothing, with no window
//   probe chat    <runtime>   a real conversation happens, and no window opens
//   probe atas                what Describe() reports on a live ATAS bridge, and what that means
//                             for autonomy — step 3 of docs/RESUME-HERE.md
//   probe atas --place-test-order --yes
//                             the same, and then ONE resting order on a provably simulated account,
//                             so the client-order-id round trip is MEASURED instead of inferred
//   probe atas --place-test-order --yes --leave-resting --yes-leave-it
//                             half 1 of the restart experiment: the same order, NOT cancelled, so
//                             it is still on the book after ATAS is restarted
//   probe atas --coid-restart-check
//                             half 2: places nothing, and reads the durable witness record against
//                             the live book. Proof, disproof, or not-answered — step 2 of
//                             docs/RESUME-HERE.md
//   probe atas --place-test-order --yes --via-async-overload
//                             the same order, submitted through ITradingManager.OpenOrderAsync
//                             instead of the synchronous OpenOrder, so that what that task waits
//                             for — submission or broker acknowledgement — is measured rather
//                             than argued about. Needs an ordinary run as its control.
//   probe atas --cancel-resting <client-order-id>
//                             removes what half 1 left behind
//
// Point TRADEAGENT_HOME at a scratch directory first, or it will install into the real one.
// See tools/README.md.

var verb = args.Length > 0 ? args[0] : "install";

// Handled before the runtime catalog is touched: `atas` names no AI runtime and must not be made to
// look up a manifest it will never use.
if (verb == "atas") return await AtasProbe.RunAsync(args[1..]);

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

if (verb != "chat") { Console.WriteLine("usage: probe [install|chat] <runtime-id>   |   probe atas [--wait <seconds>] [--wait-anyway]"); return 2; }

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

/// <summary>
/// <c>probe atas</c> — the instrument for step 3 of <c>docs/RESUME-HERE.md</c>.
///
/// It answers one question in one command and prints the evidence for it: on a live ATAS
/// connection, what do <c>SupportsClientOrderId</c> and <c>SupportsOrderHistory</c> actually
/// report, and therefore may this product ever trade unattended? Before this verb existed the only
/// consumer of either value was <c>TradingGateway</c>, internally — so the single most expensive,
/// hardest-to-repeat event in the project produced no record.
///
/// Read-only unless it is asked twice. Left alone it places no order, modifies none, cancels none,
/// and asks the bridge for nothing but a handshake, an account list and an order list. Every line is
/// labelled so the whole run survives being pasted into BUILD-STATUS.md.
///
/// The one exception is <c>--place-test-order</c>, which needs <c>--yes</c> beside it and exists
/// because the round trip rule 1 asks about cannot be observed without an order. With nothing ever
/// submitted, <c>SupportsClientOrderId</c> reports false for a reason that says nothing whatever
/// about ATAS — and that is the reading this project has been stuck on. It places ONE resting buy
/// limit far below the market on a provably simulated account, reads it back, reports what the
/// read-back found, and cancels it. It refuses outright, without submitting anything, when it cannot
/// prove the account is simulated. See <see cref="PlaceTestOrder"/>.
///
/// Two design points worth knowing before changing anything here:
///
///   * The hello frame is read off the pipe RAW, before TradeAgent's own AtasConnector is allowed
///     near it. That is not duplication for its own sake. AtasConnector refuses a hello whose
///     protocol version it does not recognise — the frame never becomes Capabilities — and while it
///     now keeps the version numbers for display (AtasConnector.Incompatible), it keeps only those.
///     Reading the frame raw is what lets this verb print everything a refused bridge claimed,
///     including the claims that were correctly thrown away. The mismatch case is exactly the one
///     where seeing what was discarded matters.
///   * The client-id round trip is reported TWICE, from two different sources, and each is labelled
///     with which it is. The bridge's own attempt/read-back counters are a report; the reading taken
///     from the live order book is an inference by this harness. A bridge older than those counters
///     reports neither, and then only the inference is printed — still labelled. When the two
///     disagree, that disagreement is the finding: believe neither until it is explained.
/// </summary>
static class AtasProbe
{
    const string BridgeDll = "TradeAgent.AtasBridge.dll";
    const int Label = 22;
    static readonly string Indent = new(' ', Label + 2);

    public static async Task<int> RunAsync(string[] rest)
    {
        var wait = TimeSpan.FromSeconds(60);
        var waitAnyway = false;
        var place = false;
        var yes = false;
        var leaveResting = false;
        var yesLeaveIt = false;
        var restartCheck = false;
        var viaAsync = false;
        string? cancelResting = null;

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--wait" && i + 1 < rest.Length && int.TryParse(rest[i + 1], out var secs) && secs >= 0)
            { wait = TimeSpan.FromSeconds(secs); i++; continue; }
            if (rest[i] == "--wait-anyway") { waitAnyway = true; continue; }
            if (rest[i] == "--place-test-order") { place = true; continue; }
            if (rest[i] == "--yes") { yes = true; continue; }
            if (rest[i] == "--leave-resting") { leaveResting = true; continue; }
            if (rest[i] == "--yes-leave-it") { yesLeaveIt = true; continue; }
            if (rest[i] == "--coid-restart-check") { restartCheck = true; continue; }
            if (rest[i] == "--via-async-overload") { viaAsync = true; continue; }
            if (rest[i] == "--cancel-resting" && i + 1 < rest.Length && !rest[i + 1].StartsWith("--", StringComparison.Ordinal))
            { cancelResting = rest[i + 1]; i++; continue; }

            Usage($"unrecognised argument '{rest[i]}'");
            return 2;
        }

        // Refused here, before the pipe is even opened: waiting sixty seconds for a bridge and then
        // declining to use it wastes the one thing this run is for. There is no prompt — this verb
        // runs unattended over ssh, and a prompt would hang forever with nobody to answer it.
        if (place && !yes)
        {
            Usage("--place-test-order needs --yes beside it.");
            Console.WriteLine();
            Console.WriteLine("  It PLACES A REAL ORDER on the connected ATAS account — simulated, resting,");
            Console.WriteLine("  and cancelled again at the end, but real as far as ATAS is concerned. Two");
            Console.WriteLine("  flags rather than one so that it cannot be reached by editing a --wait.");
            return 2;
        }

        // The same shape again, one level further out, because --leave-resting removes the one
        // safeguard --place-test-order still had: the cancel at the end. It is the difference
        // between a run that borrows the book for fifteen seconds and a run that leaves something
        // live on it, so it gets its own second act rather than riding on the first one's --yes.
        if (leaveResting && !yesLeaveIt)
        {
            Usage("--leave-resting needs --yes-leave-it beside it.");
            Console.WriteLine();
            Console.WriteLine("  It LEAVES A LIVE ORDER RESTING on the account when the run finishes. Nothing");
            Console.WriteLine("  cancels it afterwards — that is the entire point, because half 2 of the");
            Console.WriteLine("  restart experiment needs an order that is still there after ATAS is");
            Console.WriteLine("  restarted. A separate flag from --yes, so that authorising a test order that");
            Console.WriteLine("  cleans up after itself cannot be turned into authorising one that does not.");
            return 2;
        }

        if (leaveResting && !place)
        {
            Usage("--leave-resting only means anything with --place-test-order.");
            Console.WriteLine();
            Console.WriteLine("  On its own it authorises nothing and there is no order for it to leave. The");
            Console.WriteLine("  full half-1 command is:");
            Console.WriteLine();
            Console.WriteLine("      probe atas --place-test-order --yes --leave-resting --yes-leave-it");
            return 2;
        }

        // THE ONE COMBINATION THAT WOULD DESTROY THE READING IT IS TRYING TO TAKE. The cross-session
        // branch is reached ONLY when the identifier is absent from the bridge's in-memory
        // _submitted map — that absence is what makes the evidence come from the durable record
        // written by a process that has ended. Placing an order in the same run puts an identifier
        // back in _submitted, and any reading taken afterwards is an ordinary in-session one wearing
        // the restart check's name.
        if (restartCheck && place)
        {
            Usage("--coid-restart-check cannot be combined with --place-test-order.");
            Console.WriteLine();
            Console.WriteLine("  Half 2 places NOTHING, and that is not caution — it is the measurement. The");
            Console.WriteLine("  cross-session reading requires the identifier to be absent from the bridge's");
            Console.WriteLine("  in-memory map of what THIS session submitted. Place an order in the same run");
            Console.WriteLine("  and the reading you get back is an in-session one under another name.");
            Console.WriteLine();
            Console.WriteLine("  Run half 1, restart ATAS, then run half 2 on its own.");
            return 2;
        }

        if (cancelResting is not null && place)
        {
            Usage("--cancel-resting cannot be combined with --place-test-order.");
            Console.WriteLine("  One run either puts something on the book or takes something off it.");
            return 2;
        }

        // ONE FLAG, NOT TWO, AND THE ASYMMETRY WITH --leave-resting IS THE REASON. That one needs a
        // second act because it REMOVES the safeguard --place-test-order still had: the cancel at the
        // end. This one removes nothing. It is the same order, the same simulated-account guard, the
        // same read-back and the same cleanup — the only difference is which ITradingManager overload
        // the bridge submits it through, which is invisible to the broker and to the book. Requiring
        // a second --yes for it would say the exposure had changed, and it has not.
        if (viaAsync && !place)
        {
            Usage("--via-async-overload only means anything with --place-test-order.");
            Console.WriteLine();
            Console.WriteLine("  It does not place anything on its own — it changes HOW the test order is");
            Console.WriteLine("  submitted, so there has to be a test order. The full command is:");
            Console.WriteLine();
            Console.WriteLine("      probe atas --place-test-order --yes --via-async-overload");
            return 2;
        }

        Console.WriteLine(new string('=', 80));
        Console.WriteLine("probe atas — step 3 of docs/RESUME-HERE.md: what Describe() actually reports");
        Console.WriteLine(new string('=', 80));
        Line("WHEN", DateTimeOffset.Now.ToString("O"));
        Line("HOST", $"{RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.ProcessArchitecture}");
        Line("TRADEAGENT_HOME", Paths.Home);
        Line("BRIDGE PIPE NAME", Paths.BridgePipeName);
        Line("EXPECTED PROTOCOL", $"{Versions.BridgeProtocolVersion}  (Versions.BridgeProtocolVersion)");
        Line("WAIT FOR BRIDGE", $"{wait.TotalSeconds:0}s{(waitAnyway ? "   --wait-anyway: the ATAS detection gate is off" : "")}");

        if (place && leaveResting)
        {
            Line("THIS RUN WILL", "PLACE ONE ORDER AND LEAVE IT RESTING — a buy limit, quantity 1, on");
            Cont($"the chart's own instrument, {Pct(FarBelowBid)} below the live bid so that it rests");
            Cont("and cannot fill. It is read back and reported, and then NOT CANCELLED.");
            Cont("It refuses to submit anything at all unless the account it is about");
            Cont("to trade is provably simulated, and no flag can override that.");
            Cont("");
            Cont("THIS IS HALF 1 OF THE RESTART EXPERIMENT. The order has to still be");
            Cont("there after ATAS is restarted, which is why nothing cancels it. When");
            Cont("this run ends it will print the exact command that removes it — and");
            Cont("that command is the thing to run if the experiment is abandoned.");
        }
        else if (place)
        {
            Line("THIS RUN WILL", "PLACE ONE ORDER — a buy limit, quantity 1, on the chart's own");
            Cont($"instrument, {Pct(FarBelowBid)} below the live bid so that it rests and cannot");
            Cont("fill — read it back, report what came back, and then cancel it.");
            Cont("It refuses to submit anything at all unless the account it is about");
            Cont("to trade is provably simulated, and no flag can override that.");
            Cont("--place-test-order --yes were both given, which is what unlocked it.");
        }
        else if (restartCheck)
        {
            Line("THIS RUN WILL", "PLACE NOTHING. It reads the durable witness record left by an");
            Cont("earlier run against ATAS's live order book, and reports whether the");
            Cont("client order id survived the restart.");
            Cont("");
            Cont("HALF 2 OF THE RESTART EXPERIMENT, and placing nothing is the");
            Cont("measurement rather than the caution: the cross-session reading is");
            Cont("only available for an identifier the RUNNING bridge session did not");
            Cont("submit. Anything this run placed would be read back in-session.");
        }
        else if (cancelResting is not null)
        {
            Line("THIS RUN WILL", $"CANCEL ONE ORDER — the one carrying client order id");
            Cont($"'{cancelResting}' — and then re-read the book to find out whether the");
            Cont("cancel took. It places nothing and reads everything else as usual.");
            Cont("One flag rather than two, unlike --place-test-order: this only ever");
            Cont("REMOVES exposure, and it names its target explicitly, so it cannot");
            Cont("be reached by accident. Making the cleanup harder to run than the");
            Cont("thing it cleans up after would be the wrong way round.");
        }
        else
        {
            Line("THIS RUN WILL", "read only. No order is placed, modified or cancelled by this verb.");
            if (yes)
                Cont("--yes was given without --place-test-order, so it authorised nothing.");
            if (yesLeaveIt)
                Cont("--yes-leave-it was given without --leave-resting, so it authorised nothing.");
        }

        if (viaAsync)
        {
            Line("AND BY WHICH CALL", "ITradingManager.OpenOrderAsync, NOT the synchronous OpenOrder the");
            Cont("product uses. Same order, same guard, same cleanup — a different");
            Cont("overload inside the bridge, and that difference is the whole point.");
            Cont("");
            Cont("WHAT IT MEASURES. The four synchronous order calls are obsolete and");
            Cont("cannot be given a deadline, so a block inside one wedges the bridge's");
            Cont("frame loop while the heartbeat goes on reporting READY. Flipping them");
            Cont("to the Async overloads is what lets AtasCall.Block reach them, and");
            Cont("whether that is safe turns on ONE fact: does OpenOrderAsync's task");
            Cont("complete on SUBMISSION or on broker ACKNOWLEDGEMENT? Read it off the");
            Cont("PLACE TIMING line below: call= alike to an ordinary run's is");
            Cont("SUBMISSION, call= near this run's own settled= is ACKNOWLEDGEMENT.");
            Cont("");
            Cont("THE ROUTE IS UNPROVEN, WHICH IS WHY IT IS BEING RUN. If the task");
            Cont("never completes, AtasCall.Block gives up and the outcome is UNKNOWN —");
            Cont("not refused. An order may be resting. The cleanup below runs on every");
            Cont("path and verifies the book afterwards; read CLEANUP VERDICT.");
        }

        Line("EXIT CODES", "0 = the bridge answered and the answer below is the record");
        Cont("1 = could not reach the bridge    2 = bad arguments");
        Cont("A capability reading of false is a valid answer and still exits 0.");
        if (place || cancelResting is not null)
        {
            Cont("3 = --place-test-order refused to place. NOTHING WAS SUBMITTED.");
            Cont("4 = an order was placed and this run could NOT prove the book was left");
            Cont("    clean afterwards. Go and look at ATAS before doing anything else.");
        }
        if (place && leaveResting)
        {
            Cont("5 = an order was placed and DELIBERATELY LEFT RESTING, as asked. Not a");
            Cont("    failure — but it is not a clean book either, and a script that");
            Cont("    treats 0 as 'nothing was left behind' must not see a 0 here.");
        }
        if (restartCheck)
        {
            Cont("All three restart-check outcomes — proof, disproof and not-answered —");
            Cont("exit 0. Two of them are real answers and the third is an honest");
            Cont("absence of one; none of them is a failure of this harness, which is");
            Cont("what the non-zero codes are for. Read the verdict, not the code.");
        }

        // ------------------------------------------------------------------ ATAS on this machine

        var layout = AtasLayout.Load();
        var detection = AtasInstallation.Detect(layout);

        Section("ATAS ON THIS MACHINE");
        Line("ATAS INSTALLED", detection.Installed ? "YES" : "NO");
        Line("ATAS INSTALL DIR", detection.InstallDir ?? "<not found>");
        Cont($"candidates: {Join(layout.InstallDirCandidates)}");
        Line("ATAS VERSION", detection.Version ?? "<unknown>");
        Line("ATAS RUNTIME TFM", detection.RuntimeTfm ?? "<unknown>");
        Cont("read from the platform's own runtimeconfig. A bridge built for a different");
        Cont("framework is not rejected with an error — ATAS simply never lists it.");
        Line("ATAS RUNNING", detection.Running ? "YES" : "NO");
        Cont($"process names: {Join(layout.ProcessNames)}");
        Line("LAYOUT VERIFIED", detection.LayoutVerified ? "YES" : "NO — paths are guesses");
        Line("LAYOUT OVERRIDE", File.Exists(AtasLayout.OverridePath)
            ? $"IN USE — {AtasLayout.OverridePath}"
            : $"none (would be {AtasLayout.OverridePath})");

        // ------------------------------------------------------------------ where the bridge sits

        var indicatorDir = layout.IndicatorDirCandidates
            .Select(Environment.ExpandEnvironmentVariables)
            .FirstOrDefault(Directory.Exists);
        var bridgeInIndicators = indicatorDir is not null && File.Exists(Path.Combine(indicatorDir, BridgeDll));

        Section("WHERE THE BRIDGE SITS");
        Line("STRATEGY FOLDER", detection.StrategyDir ?? "<not found>");
        Cont($"candidates: {Join(layout.StrategyDirCandidates)}");
        Line("BRIDGE IN STRATEGIES", detection.BridgeInstalled ? $"YES — {BridgeDll} is there" : $"NO — no {BridgeDll}");
        Line("INDICATORS FOLDER", indicatorDir ?? "<not found>");
        Cont($"candidates: {Join(layout.IndicatorDirCandidates)}");
        Line("BRIDGE IN INDICATORS", bridgeInIndicators ? "YES — SEE THE WARNING BELOW" : "no");
        Cont("Indicators is a DIFFERENT folder from Strategies and is never a fallback:");
        Cont("ATAS silently ignores a strategy DLL there, the heartbeat never arrives, and");
        Cont("nothing anywhere says why. (Trap 7 in docs/RESUME-HERE.md.)");
        if (bridgeInIndicators && !detection.BridgeInstalled)
        {
            Line("WARNING", $"{BridgeDll} is in the INDICATORS folder and not in Strategies.");
            Cont("ATAS will never load it from there. Install the add-on again from");
            Cont("TradeAgent's setup step, which copies it into the Strategies folder.");
        }

        // ------------------------------------------------------------------ can anything dial in?

        var blockers = new List<string>();
        if (!detection.Installed)
            blockers.Add($"ATAS is not installed — no directory matched {Join(layout.InstallDirCandidates)}");
        if (detection.StrategyDir is null)
            blockers.Add($"the ATAS strategies folder does not exist — looked for {Join(layout.StrategyDirCandidates)}");
        else if (!detection.BridgeInstalled)
            blockers.Add($"{BridgeDll} is not in {detection.StrategyDir} — run TradeAgent's setup step \"Install the add-on\" first");

        if (blockers.Count > 0 && !waitAnyway)
        {
            Section("CANNOT PROCEED");
            Line("CANNOT PROCEED", "nothing on this machine can dial in to the bridge pipe.");
            foreach (var b in blockers) Cont($"- {b}");
            Cont("");
            Cont("This verb produces the step-3 answer only on Windows, with ATAS installed,");
            Cont("signed in, running, and the TradeAgent Bridge strategy started on a chart.");
            Cont("Nothing was waited for, because nothing could have answered.");
            Cont("(--wait-anyway waits for the pipe regardless; that is for driving the pipe");
            Cont("with a stand-in bridge, and it proves nothing about ATAS.)");
            return 1;
        }

        if (detection.Installed && !detection.Running)
        {
            Line("NOTE", "ATAS is installed but is not running right now. The bridge can only");
            Cont("dial in from inside a running ATAS, so this will wait and probably time out.");
        }

        // ------------------------------------------------------------------ phase A: the raw hello

        Section("THE BRIDGE HANDSHAKE");

        // The bridge will not serve a single operation to a process that cannot prove it holds this
        // installation's bridge secret, and this probe is such a process. It gets the secret the
        // same way the app does — by asking for it — and that call also records THIS executable as
        // the program entitled to own the bridge pipe.
        //
        // SAY SO OUT LOUD, BECAUSE IT IS THE RESIDUAL WEAKNESS ITSELF. Nothing here was granted to
        // the probe that is not equally available to any other program running as this user,
        // including the AI runtime TradeAgent starts. The authentication keeps out other accounts
        // and anything that merely knows the pipe name; it does not keep out this user. See the
        // class comment on BridgePipeAuth.
        var credential = BridgePipeAuth.EnsureForServer();
        Line("BRIDGE SECRET", $"published at {BridgePipeAuth.CredentialFile}");
        Cont($"this run recorded itself as the pipe's owner: {Environment.ProcessPath ?? "<unknown>"}");
        Cont("A same-user process can do exactly this, which is the honest limit of the");
        Cont("defence. It is not a boundary against anything running as you.");

        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(Paths.BridgePipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            Line("BRIDGE PIPE", "COULD NOT BE OPENED");
            Cont($"{ex.GetType().Name}: {ex.Message}");
            Cont($"The name '{Paths.BridgePipeName}' is owned by another process. TradeAgent itself");
            Cont("holds this pipe for as long as it is running, and so does a second copy of this");
            Cont("probe. Close TradeAgent, and any other probe, then run this again.");
            return 1;
        }

        string? raw;
        BridgeFrame? frame;
        string auth;
        var dialledIn = false;
        var elapsed = Stopwatch.StartNew();
        try
        {
            using var deadline = new CancellationTokenSource(wait);
            using var ticking = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var ticker = Ticker(elapsed, wait, ticking.Token);
            try
            {
                await server.WaitForConnectionAsync(deadline.Token);
                dialledIn = true;
                Line("BRIDGE PIPE", $"ANSWERED after {elapsed.Elapsed:mm\\:ss}");
                Line("WHO DIALLED IN", BridgePipeAuth.ClientImagePath(server) is { } who
                    ? who
                    : OperatingSystem.IsWindows()
                        ? "NOT REPORTED — Windows would not name the process on the other end"
                        : "not asked — this is a Windows-only question");
                (raw, frame, auth) = await Handshake(server, credential, deadline.Token);
            }
            finally { ticking.Cancel(); await ticker; }
        }
        catch (OperationCanceledException)
        {
            // Two different failures share this exception, and they are not the same news: nothing
            // ever connected, or something connected and then never introduced itself.
            if (dialledIn)
            {
                Line("HELLO FRAME", $"NOT SENT within {wait.TotalSeconds:0}s of connecting.");
                Cont("Something dialled in to the pipe and then said nothing. A bridge from this");
                Cont("build authenticates and then says hello, both immediately, so whatever");
                Cont("connected is not a bridge this build can talk to.");
            }
            else
            {
                Line("BRIDGE PIPE", $"NO ANSWER within {wait.TotalSeconds:0}s");
                NoAnswerAdvice(detection);
            }
            server.Dispose();
            return 1;
        }
        catch (Exception ex)
        {
            Line("BRIDGE PIPE", $"FAILED after {elapsed.Elapsed:mm\\:ss}");
            Cont($"{ex.GetType().Name}: {ex.Message}");
            if (ex is IOException)
                Cont("If that says the pipe is busy, another process owns the name — TradeAgent " +
                     "itself, or a second probe. Close it and run this again.");
            server.Dispose();
            return 1;
        }

        // The authentication verdict comes FIRST, before anything the bridge claimed about itself,
        // because it decides how much of the rest is worth reading.
        Line("BRIDGE AUTH", auth);
        if (auth.StartsWith("NOT PRESENTED", StringComparison.Ordinal))
        {
            Cont("");
            Cont("This is what an ATAS bridge older than this build looks like: it does not know");
            Cont("how to authenticate, so it never offered a proof. The DLL in the ATAS Strategies");
            Cont("folder is not the one this repository builds. Reinstall the add-on from");
            Cont("TradeAgent's setup step, restart the strategy, and run this again.");
            Cont("");
            Cont("It is NOT a hang and it is NOT trap 12, 7 or 24 — those all present as no answer");
            Cont("at all. Something answered; it is the wrong build.");
        }

        if (auth.StartsWith("REFUSED", StringComparison.Ordinal) || auth.StartsWith("MISMATCH", StringComparison.Ordinal))
        {
            Cont("");
            Cont("The bridge is loaded, running, and talking — it declined THIS process. That is a");
            Cont("credential problem, not a missing bridge: the copy of TradeAgent that published");
            Cont($"{BridgePipeAuth.CredentialFile}");
            Cont("is not the one whose secret the bridge read. Two installations, a copied profile,");
            Cont("or a TRADEAGENT_HOME pointing somewhere else than the app's. Nothing below can be");
            Cont("answered, and nothing about ATAS is in question.");
            server.Dispose();
            return 1;
        }

        if (raw is null || frame is null)
        {
            Line("HELLO FRAME", "NONE — something connected to the pipe and then said nothing,");
            Cont("or it closed the connection before sending a hello. That is not a bridge");
            Cont("TradeAgent can use: a bridge from this build authenticates and then says hello.");
            server.Dispose();
            return 1;
        }

        Line("HELLO FRAME (raw)", "as received, one line, exactly as it came off the pipe:");
        Console.WriteLine(raw);
        Line("HELLO FRAME (pretty)", "the same bytes, re-indented for reading:");
        Console.WriteLine(Prettify(raw));

        BridgeHello? hello = null;
        try { hello = frame.Data?.Deserialize<BridgeHello>(Json.Options); }
        catch (JsonException ex) { Line("HELLO PAYLOAD", $"UNREADABLE — {ex.Message}"); }

        if (hello is null)
        {
            Line("HELLO PAYLOAD", "MISSING — the hello frame carried no readable 'data' object, so no");
            Cont("capability could be read from it. Nothing below can be answered.");
            server.Dispose();
            return 1;
        }

        Line("FRAME ENVELOPE v", frame.V.ToString());
        Line("BRIDGE PROTOCOL", hello.BridgeProtocolVersion.ToString());
        Line("TRADEAGENT EXPECTS", Versions.BridgeProtocolVersion.ToString());

        var compatible = Versions.BridgeCompatible(hello.BridgeProtocolVersion);
        if (compatible)
            Line("PROTOCOL VERDICT", $"MATCH — Versions.BridgeCompatible({hello.BridgeProtocolVersion}) = True");
        else
        {
            Line("PROTOCOL VERDICT", $"MISMATCH — the bridge speaks protocol {hello.BridgeProtocolVersion}, " +
                                     $"TradeAgent speaks {Versions.BridgeProtocolVersion}.");
            Cont("AtasConnector refuses a mismatched bridge outright rather than half-trusting");
            Cont("it: the connection reports FAILED and no capability is ever read. Step 3");
            Cont("cannot be answered until the two versions agree — reinstall the add-on so");
            Cont("the bridge in the Strategies folder is the one this build shipped.");
            Cont("");
            Cont("Worth knowing: AtasConnector does not keep the mismatched hello, so the app");
            Cont("itself cannot show these two numbers. This line is the only place they appear.");
        }

        Line("BRIDGE VERSION", Blank(hello.BridgeVersion));
        Line("ATAS VERSION (hello)", Blank(hello.AtasVersion));
        Line("ACCOUNT ID (hello)", Blank(hello.AccountId));

        // The adapter's own account of what it bound to. This is the line that separates "I could
        // not look" from "I looked and there was nothing" — and the line that would have caught
        // ChartStrategy.Connector being null without costing a live run to discover it.
        Line("TRADING SURFACE", hello.TradingSurface is { Length: > 0 } surface
            ? surface
            : "NOT REPORTED — this bridge predates the trading-surface field");

        if (!compatible) { server.Dispose(); return 1; }

        // ---------------------------------------------- phase B: the product's own connector path

        // The raw server has to go before AtasConnector can take the name. The bridge reconnects on
        // its own — BridgeServer retries every ReconnectDelay for as long as it is loaded — so the
        // handshake below is also a small, free proof that reconnection works.
        server.Dispose();

        Section("TRADEAGENT'S OWN CONNECTOR");
        await using var connector = new AtasConnector();
        await connector.ConnectAsync();
        var reconnectLimit = TimeSpan.FromSeconds(30);
        var handshake = await Until(() => connector.Bridge is not null, reconnectLimit);

        if (handshake)
        {
            Line("CONNECTOR HANDSHAKE", "OK — AtasConnector accepted the same bridge, so the capability");
            Cont("lines below are the product's own reading, not this harness's.");

            // Whether the CONNECTOR is satisfied with the peer, as opposed to whether this probe
            // was. They are separate handshakes over separate connections, and on a machine whose
            // deployed bridge is older than the repository they give different answers.
            //
            // No wait here any more, and the change is worth knowing about. The connector used to
            // accept an unproved hello and merely NAME the peer once AuthGrace expired, so this had
            // to sleep out the grace or it would report "fine" about every bridge. It now REFUSES an
            // unproved hello, so reaching this line at all means the peer proved itself — Bridge is
            // non-null only for an authenticated peer. The reading is immediate and it is a check on
            // that invariant rather than a measurement.
            Line("CONNECTOR AUTH", connector.Unauthenticated is { } gap
                ? $"NOT PROVED, YET THE HANDSHAKE COMPLETED — {gap.Reason}"
                : "OK — the bridge proved itself to AtasConnector as well");
            if (connector.PeerImage is { } peer) Cont($"peer image, as Windows reports it: {peer}");

            // Describe() decides itself at runtime, and the answer it gives at the handshake is not
            // necessarily the answer a minute later — SupportsClientOrderId in particular can only
            // ever turn true after the handshake. So this does not take the first reading and run:
            // it watches the live connection for a short while and reports the answer the connector
            // is holding at the end of it, plus whether that differs from the hello above.
            var atHandshake = connector.Bridge!;
            var watch = TimeSpan.FromSeconds(12);
            await Until(() => Differences(atHandshake, connector.Bridge ?? atHandshake).Count > 0, watch);
            var current = connector.Bridge ?? atHandshake;
            var differences = Differences(hello, current);

            Line("CAPABILITY REFRESH", $"watched the live connection for up to {watch.TotalSeconds:0}s and re-read");
            Cont("the connector's current answer, because Describe() decides itself at");
            Cont("runtime and can change after the handshake.");
            Line("CURRENT vs HELLO", differences.Count == 0
                ? "identical — the answer did not move while this was watching"
                : $"CHANGED: {string.Join("; ", differences)}");
            if (differences.Count > 0)
                Cont("The later reading is the current answer and is what is reported below.");
            hello = current;
        }
        else
        {
            // WHY THIS BRANCH NOW HAS A REASON IN IT. The connector refuses a bridge that is the
            // wrong protocol version or cannot prove the pipe secret, and a refused peer never sets
            // Bridge — so this branch, which used to mean "nothing turned up in time", is now also
            // where a bridge that turned up and was TURNED AWAY lands. Those want opposite actions,
            // and the connector already knows which happened. Printing the timeout alone would send
            // a reader hunting a bridge that is answering perfectly well.
            Line("CONNECTOR HANDSHAKE", $"NOT COMPLETED within {reconnectLimit.TotalSeconds:0}s.");
            if (connector.Incompatible is { } bad)
            {
                Cont("");
                Cont($"AND THE REASON IS KNOWN: {bad}");
                Cont("The bridge answered and AtasConnector turned it away on the protocol version.");
                Cont("This is the expected reading against a bridge built before the version bump —");
                Cont("rebuild and redeploy the bridge, then run this again. It is NOT a bridge that");
                Cont("failed to load, NOT the wrong Strategies folder and NOT a strategy restored");
                Cont("stopped: all three of those are silence, and this is an answer.");
            }
            else if (connector.Unauthenticated is { } why)
            {
                Cont("");
                Cont($"AND THE REASON IS KNOWN: {why.Reason}");
                Cont("The bridge answered and AtasConnector turned it away over the pipe secret.");
                Cont("Rebuild and redeploy the bridge if it predates authentication. If it does not,");
                Cont("this is two installations or a copied profile — or a peer that is not the");
                Cont("bridge at all, which is the case this refusal exists for.");
            }
            else
            {
                Cont("Nothing answered AtasConnector's pipe in time, and it has no complaint on");
                Cont("record — so this is silence rather than a refusal. Traps 12, 7 and 24 all");
                Cont("look exactly like this.");
            }
            Cont("");
            Cont("The hello above was still received by THIS harness, so the capability lines below");
            Cont("are derived from it with the same expression AtasConnector.Capabilities uses —");
            Cont("note that means they are what the bridge CLAIMS, not what the product accepted.");
            Cont("The order evidence further down could not be gathered.");
        }

        var caps = handshake
            ? connector.Capabilities
            : new ConnectorCapabilities(hello.IsSimulated, hello.SupportsClientOrderId, hello.SupportsOrderHistory,
                hello.SupportsModify, hello.SupportsClosePosition, true);

        // ------------------------------------------------------------------ the answer to step 3

        Section("THE ANSWER TO STEP 3");
        Line("SupportsClientOrderId", Yn(caps.SupportsClientOrderId));
        Line("SupportsOrderHistory", Yn(caps.SupportsOrderHistory));
        Line("IsSimulated", Yn(caps.IsPaper));
        Line("SupportsModify", Yn(caps.SupportsModify));
        Line("SupportsClosePosition", Yn(caps.SupportsClosePosition));
        Line("ReconciliationProvable", $"{Yn(caps.ReconciliationProvable)}   " +
                                       "(= SupportsClientOrderId AND SupportsOrderHistory)");
        Line("READ FROM", handshake
            ? "AtasConnector.Capabilities on a live connection"
            : "the raw hello frame above (the connector handshake did not complete)");
        if (place)
        {
            Line("READ WHEN", "BEFORE the test order. This is the 'before' picture and it is");
            Cont("about to be superseded: the reading that answers rule 1 is the one");
            Cont("under THE TEST ORDER below. Do not quote this one as the answer.");
        }

        // ------------------------------- narrowing what a false SupportsClientOrderId actually means

        Section(caps.SupportsClientOrderId
            ? "WHAT SupportsClientOrderId PROVES HERE"
            : "WHAT A false SupportsClientOrderId MEANS HERE");

        IReadOnlyList<AccountInfo>? accounts = null;
        IReadOnlyList<OrderInfo>? orders = null;
        string? accountsError = null, ordersError = null;

        if (handshake)
        {
            try { accounts = await connector.GetAccountsAsync(); }
            catch (Exception ex) { accountsError = $"{ex.GetType().Name}: {ex.Message}"; }

            // An EMPTY account id on purpose. AccountMatches treats it as "every account", and with a
            // blank account id the adapter does not consult ATAS's history cache at all — so what
            // comes back is exactly ATAS's live order collection, which is exactly the collection
            // ProveClientOrderId scans. Any other account id would mix in cached orders that can
            // never set the flag, and the count below would stop meaning anything.
            try { orders = await connector.GetOrdersAsync("", includeInactive: true, since: null); }
            catch (Exception ex) { ordersError = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        Line("ACCOUNTS VISIBLE", accounts is null
            ? $"COULD NOT READ — {accountsError ?? "the connector handshake did not complete"}"
            : accounts.Count == 0
                ? "0 — ATAS reported no account. It is running without a trading connection."
                : $"{accounts.Count} — {string.Join(", ", accounts.Select(a => $"{a.Id} ({a.Currency}, simulated={Yn(a.IsSimulated)}, trading={Yn(a.TradingEnabled)})"))}");

        Line("ORDERS IN LIVE BOOK", orders is null
            ? $"COULD NOT READ — {ordersError ?? "the connector handshake did not complete"}"
            : orders.Count.ToString());
        if (orders is not null)
            Cont("account_id=\"\", include_inactive=true, since=<none> — ATAS's live order");

        var withClientId = orders?.Count(o => !string.IsNullOrEmpty(o.ClientOrderId)) ?? 0;
        var withBothIds = orders?.Count(o => !string.IsNullOrEmpty(o.ClientOrderId)
                                             && !string.IsNullOrEmpty(o.ConnectorOrderId)) ?? 0;
        if (orders is not null)
        {
            Cont("collection only, which is the collection that can set the flag.");
            Line("CARRYING A CLIENT ID", withClientId.ToString());
            Line("CARRYING BOTH IDS", $"{withBothIds}   (a client order id AND a broker-assigned order id —");
            Cont("exactly the pair the bridge requires before it reports true)");
        }

        // The bridge's OWN account of why, when it keeps one. This is not the same class of
        // statement as the order-book reading below it: one is reported, the other is inferred, and
        // conflating them is how an inference ends up quoted as a measurement.
        // Same fallback the capability block above uses: the live handshake when there is one, the
        // raw hello when the handshake did not complete, so a refused bridge still gets to explain.
        var reporting = connector.Bridge ?? hello;
        var attempts = reporting?.ClientOrderIdAttempts;
        var checks = reporting?.ClientOrderIdChecks;
        // The coid= token, because the boolean can no longer explain itself. Since the bridge
        // stopped reporting true for a same-reference match, "the evidence is present and the flag
        // is false" became the CORRECT reading on ATAS rather than a contradiction — and both
        // verdicts below were written when that combination could only mean something was broken.
        var coid = Token(reporting?.TradingSurface, "coid");
        var reported = ReportedClientIdVerdict(caps.SupportsClientOrderId, attempts, checks, coid)?.ToList();

        Line("SUBMITTED WITH AN ID", attempts is null
            ? "NOT REPORTED — this bridge predates the attempt counters"
            : $"{attempts}   (orders this bridge sent to ATAS carrying a client order id)");
        Line("READ-BACKS PERFORMED", checks is null
            ? "NOT REPORTED"
            : $"{checks}   (times it then looked one of them up in ATAS's own order");
        if (checks is not null) Cont("collection — the check that can set the flag)");

        var verdict = ClientIdVerdict(caps.SupportsClientOrderId, orders, ordersError, withClientId, withBothIds, coid).ToList();

        if (reported is not null)
        {
            Line("CLIENT ID VERDICT", reported[0]);
            foreach (var l in reported.Skip(1)) Cont(l);
            Line("HOW THIS WAS DERIVED", "REPORTED BY THE BRIDGE. It carries the two counters above, so \"never");
            Cont("attempted\" and \"attempted and the read-back failed\" are different values");
            Cont("on the wire rather than the same false. This verdict is the bridge's own");
            Cont("account of what it did, not this harness's reading of the order book.");
            Line("AND, INDEPENDENTLY", verdict[0]);
            foreach (var l in verdict.Skip(1)) Cont(l);
            Cont("");
            Cont("That second reading is INFERRED from the live order book by this harness.");
            Cont("It is printed because it is derived from a different source than the");
            Cont("counters: if the two disagree, believe neither and find out why.");
        }
        else
        {
            Line("CLIENT ID VERDICT", verdict[0]);
            foreach (var l in verdict.Skip(1)) Cont(l);

            Line("HOW THIS WAS DERIVED", "This bridge reports no attempt counters, so \"not proven yet\" and \"the");
            Cont("round trip failed\" are the same value on the wire. The verdict above is");
            Cont("INFERRED from the order list by this harness — it is not something the");
            Cont("bridge reported. Treat it as a reading of the evidence, and re-run after");
            Cont("placing one order if you need the round trip itself answered.");
        }

        // ------------------------------------------------------------------ order history

        Section("WHAT SupportsOrderHistory MEANS HERE");
        if (caps.SupportsOrderHistory)
        {
            Line("ORDER HISTORY", "REACHABLE — the bridge found a real ATAS order cache and will serve");
            Cont("finished orders from it. Reconciliation after a dropped connection can");
            Cont("ask whether an order exists and get an answer that is worth trusting.");
            Cont("Note what this does NOT say: it does not say how far back the cache");
            Cont("reaches. A request older than ATAS's own retention is refused outright");
            Cont("rather than answered with a short list, which is the correct behaviour.");
        }
        else
        {
            Line("ORDER HISTORY", "NOT REACHABLE — no order cache was found in the running platform, so");
            Cont("the bridge can only see the live order collection. After a disconnect");
            Cont("there is no way to ask \"did this finished order ever exist\", and a");
            Cont("partial answer would be worse than none: it would make \"this order");
            Cont("does not exist\" look provable when it is not. Reported false on");
            Cont("purpose. Do not hard-code it true.");
        }

        // ------------------------------------------------------------------ the test order

        // Placed here, between the readings and the conclusion, on purpose. Everything above is the
        // BEFORE picture; autonomy is a conclusion and must be drawn from the newest reading there
        // is, which — if an order has just been placed — is the one taken after it.
        var test = place ? await PlaceTestOrder(connector, handshake, orders, leaveResting, viaAsync) : (TestOrderOutcome?)null;

        // Half 2, and the cleanup. Neither places anything, so neither disturbs the readings above.
        //
        // Kept in their own variables rather than folded into `test`: the autonomy section below
        // reads test.Placed to decide whether its readings were taken before or after an order was
        // submitted, and a cancel-only run would answer "after the test order" about a test order
        // that never existed.
        var restart = restartCheck ? await CoidRestartCheck(connector, handshake, hello) : (RestartCheckOutcome?)null;
        TestOrderOutcome? cancelled = null;
        if (cancelResting is not null)
            cancelled = handshake
                ? await CleanUp(connector, cancelResting, placed: null, rejected: false, everSeen: false,
                                standalone: true)
                : NoCancel(cancelResting);

        // ------------------------------------------------------------------ autonomy

        // Re-read rather than reusing `caps`. SupportsClientOrderId can only ever turn true after an
        // order has proved it, so on a --place-test-order run the capability block above is stale by
        // exactly the event this verb exists to cause.
        var final = handshake ? connector.Capabilities : caps;

        Section("WHAT THIS MEANS FOR AUTONOMY");
        if (test is { Placed: true })
        {
            Line("READ WHEN", "AFTER the test order above. This is the current answer.");
            if (final.SupportsClientOrderId != caps.SupportsClientOrderId
                || final.SupportsOrderHistory != caps.SupportsOrderHistory)
                Cont($"It MOVED: SupportsClientOrderId {Yn(caps.SupportsClientOrderId)}->{Yn(final.SupportsClientOrderId)}, " +
                     $"SupportsOrderHistory {Yn(caps.SupportsOrderHistory)}->{Yn(final.SupportsOrderHistory)}.");
            else
                Cont("It did not move: the test order changed neither capability.");
        }
        else if (test is not null)
        {
            Line("READ WHEN", "the test order was refused before anything was submitted, so");
            Cont("nothing below was influenced by it. This is the same reading as above.");
        }

        if (final.ReconciliationProvable)
        {
            Line("AUTONOMY", "PERMITTED BY THE GATEWAY — both halves are proven on this connection.");
            Cont("TradingGateway stops refusing TradingMode.LIVE_AUTONOMOUS on this");
            Cont("connector, and reconciliation after a disconnect can resolve orders by");
            Cont("client order id instead of stopping to ask a human.");
            Cont("");
            Cont("That is a gateway permission, not a recommendation. The staged live");
            Cont("trial in docs/RESUME-HERE.md still stands: paper, extended paper run,");
            Cont("one tiny live order, a disconnect/recovery test, and only then this.");
        }
        else
        {
            Line("AUTONOMY", "REFUSED BY THE GATEWAY, CORRECTLY.");
            Cont("While ReconciliationProvable is false, TradingGateway refuses");
            Cont("TradingMode.LIVE_AUTONOMOUS on this connector and answers");
            Cont("AUTONOMY_REQUIRES_PROVABLE_STATE, and reconciliation of an unknown");
            Cont("order stops and asks for a human instead of guessing. Paper trading and");
            Cont("attended live trading are unaffected.");
            Cont("");
            Cont("THIS IS CORRECT BEHAVIOUR AND MUST NOT BE \"FIXED\" BY HARD-CODING");
            Cont("EITHER BOOLEAN TRUE. The refusal is the whole point: after a dropped");
            Cont("connection the product must be able to PROVE what happened to an order.");
            Cont("A hard-coded true does not make the state provable, it only makes the");
            Cont("gateway believe it is — and then \"this order does not exist\" looks");
            Cont("provable when it is not, on a machine placing real orders unattended.");
        }

        // ------------------------------------------------------------------ paste-ready summary

        Section("ONE-LINE SUMMARY");
        Console.WriteLine(
            $"atas={detection.Version ?? Blank(hello.AtasVersion)} bridge={Blank(hello.BridgeVersion)} proto={hello.BridgeProtocolVersion} " +
            $"| SupportsClientOrderId={Yn(final.SupportsClientOrderId)} SupportsOrderHistory={Yn(final.SupportsOrderHistory)} " +
            $"IsSimulated={Yn(final.IsPaper)} | ReconciliationProvable={Yn(final.ReconciliationProvable)} " +
            $"| autonomy={(final.ReconciliationProvable ? "permitted" : "refused")}" +
            $" | auth={AuthTag(auth)}" +
            (test is { } t ? $" | test-order={t.Summary}" : "") +
            (cancelled is { } c ? $" | cancel-resting={c.Summary}" : "") +
            (restart is { } r ? $" | coid-restart={r.Verdict}" : ""));

        // Repeated last because last is what a person sees on a terminal they scrolled away from,
        // and because this one line is the difference between a tidy run and an order left resting.
        if ((test ?? cancelled) is { ExitCode: 4 } unclean)
        {
            Console.WriteLine();
            Console.WriteLine(new string('!', 80));
            Console.WriteLine(test is null
                ? $"THIS RUN COULD NOT PROVE THE BOOK WAS LEFT CLEAN: {unclean.Summary}"
                : $"THIS RUN PLACED AN ORDER AND COULD NOT PROVE THE BOOK WAS LEFT CLEAN: {unclean.Summary}");
            Console.WriteLine("Read THE TEST ORDER — CLEANING UP above, then look at ATAS. Exit code 4.");
            Console.WriteLine(new string('!', 80));
        }

        // The same discipline for the order this run left on purpose. "On purpose" is a reason, not
        // an excuse: something is live on the account and the person who runs this must leave with
        // the command that removes it, not with a memory of having read one.
        if (test is { ExitCode: 5 } left)
        {
            Console.WriteLine();
            Console.WriteLine(new string('!', 80));
            Console.WriteLine($"THIS RUN HAS LEFT A LIVE ORDER RESTING ON THE ACCOUNT: {left.Summary}");
            if (left.RestingClientOrderId is { } id)
            {
                Console.WriteLine();
                Console.WriteLine("  Next: restart ATAS, wait for the bridge to come back, then read it with");
                Console.WriteLine("      probe atas --coid-restart-check");
                Console.WriteLine();
                Console.WriteLine("  To abandon the experiment and remove the order:");
                Console.WriteLine($"      probe atas --cancel-resting {id}");
                Console.WriteLine("  or cancel it by hand in ATAS's order book. It is a Day order, so it also");
                Console.WriteLine("  expires with the session — that is a backstop, not a plan.");
            }
            Console.WriteLine(new string('!', 80));
        }

        return test?.ExitCode ?? cancelled?.ExitCode ?? restart?.ExitCode ?? 0;
    }

    // ------------------------------------------------------------------------- the restart check

    /// <summary>What half 2 concluded, in one grep-able word, and the exit code that goes with it.</summary>
    readonly record struct RestartCheckOutcome(int ExitCode, string Verdict);

    /// <summary>
    /// HALF 2 OF THE RESTART EXPERIMENT — <c>--coid-restart-check</c>. IT PLACES NOTHING.
    ///
    /// That is the measurement and not the caution. The bridge's cross-session reading is available
    /// only for an identifier the RUNNING session did not submit: the whole claim is that the
    /// evidence was written down by a process that has since ended, so an order placed by this
    /// session would be read back in-session and the reading would be an ordinary one wearing this
    /// verb's name. RunAsync refuses <c>--place-test-order</c> alongside it for that reason.
    ///
    /// THREE OUTCOMES, AND KEEPING THEM APART IS THE POINT:
    ///
    ///   PROOF        the bridge reports coid=proven-crosssession. An identifier a previous run
    ///                wrote down BEFORE submitting is on an order in ATAS's book, carrying the
    ///                broker id that run recorded. The identifier outlived the process that made it.
    ///   DISPROOF     the ORDER survived and the IDENTIFIER did not — an order carrying the recorded
    ///                broker order id is in the book, and its client order id is not ours. That is a
    ///                real, negative, shippable answer about ATAS: on this platform the comment does
    ///                not survive a restart, so rule 1 cannot be satisfied and the product may never
    ///                trade unattended on this backend.
    ///   NOT ANSWERED the order itself did not survive. This says NOTHING about the identifier, and
    ///                recording it as a negative would be the worst mistake available here: it would
    ///                look like evidence against ATAS produced by an experiment that never ran.
    ///
    /// The fourth case is the one that wastes an afternoon, so it is checked first: the bridge is
    /// the SAME SESSION that wrote the record, which means ATAS was never restarted. The witness
    /// token carries a session prefix precisely so that this is visible from out here.
    /// </summary>
    static async Task<RestartCheckOutcome> CoidRestartCheck(AtasConnector connector, bool handshake, BridgeHello hello)
    {
        Section("THE RESTART CHECK — THE WITNESS RECORD");
        Line("WHAT THIS IS", "half 2 of the experiment that settles rule 1. It places nothing,");
        Cont("modifies nothing and cancels nothing.");

        // The bridge writes this file from inside ATAS; this process reads it from beside ATAS.
        // They agree only because both resolve it through Paths, from the same TRADEAGENT_HOME —
        // the same thing that makes the bridge pipe secret work. Printed rather than assumed,
        // because a TRADEAGENT_HOME pointing at a scratch directory is the single most likely way
        // for this verb to read an empty file and report "not answered" about a perfectly good
        // experiment.
        var witness = new CoidWitness(Path.Combine(Paths.BridgeDir, CoidWitness.FileName));
        Line("WITNESS FILE", witness.Path ?? "<none>");
        Cont("Written by the bridge inside ATAS and read here, both through Paths, so");
        Cont("both depend on TRADEAGENT_HOME being the same for the two processes — the");
        Cont("same requirement the bridge pipe secret has. If the path above is not the");
        Cont("one the bridge uses, everything below reads as 'nothing was recorded'.");
        Line("FILE EXISTS", witness.Path is not null && File.Exists(witness.Path) ? "YES" : "NO");

        // THE SIDECAR, PRINTED BEFORE ANY VERDICT. It holds the rewrites that never landed, and a
        // durability gap in the write-ahead record changes how everything below should be read: an
        // identifier can be missing from the file because no run ever submitted it, or because the
        // run that did could not get it committed. Those are different findings and only this file
        // tells them apart.
        // RESOLVED IS NOT THE SAME AS FAILING, and shouting at both is how a report stops being read.
        // The witness itself decides — Trouble is null once a clean commit has closed the gap — so
        // this asks rather than inferring from the file merely existing.
        //
        // THE DECISION AND THE WORDING LIVE IN CoidWitnessReport, not here, because this whole block
        // sits behind a live bridge-pipe connection: it cannot execute on a machine that is not
        // running ATAS, no test project references this program, and a mutant that made every sidecar
        // read as UNRESOLVED left the entire suite green. What an operator actually reads was the
        // least-verified thing in the unit. This is now a renderer.
        var sidecar = witness.ErrorLogPath;
        var standing = CoidWitnessReport.Standing(witness);

        Line("WITNESS FAILURES", CoidWitnessReport.Headline(standing, sidecar ?? "<none>"));

        // EVERY SIDECAR, NOT JUST THE CANONICAL ONE. A second bridge the lease turned away writes its
        // own file beside the witness, and printing only the owner's left the report describing a
        // rejected candidate for a state that was in fact a contested witness — the one thing an
        // operator would actually act on.
        foreach (var file in witness.SidecarPaths)
        {
            Cont(file);
            foreach (var note in ReadTail(file, 10)) Cont("  " + note);
        }
        foreach (var line in CoidWitnessReport.Explanation(standing)) Cont(line);

        var records = witness.All();
        Line("RECORDS ON FILE", records.Count.ToString());

        // ASKED BEFORE THE ZERO IS INTERPRETED. An unreadable witness and an empty one both hand
        // back no records, and they are opposite answers: one says the claims are unreadable, the
        // other says this product never submitted anything on this machine. Reporting the second
        // when the first is true is the worst mistake available in this verb — it reads as evidence
        // about ATAS produced by an experiment that was never run.
        if (witness.Unreadable)
        {
            Line("VERDICT", "NOT ANSWERED — THE WITNESS FILE COULD NOT BE READ.");
            Cont("Something is at that path and this build could not parse it: a rewrite that was");
            Cont("interrupted, a hand edit, or a file belonging to something else. This is NOT the");
            Cont("same as 'nothing was ever recorded', and it must not be read as one.");
            Cont("");
            Cont("Look at the file named above, and at any coid-witness.errors.log beside it.");
            return new RestartCheckOutcome(0, "unreadable");
        }

        if (records.Count == 0)
        {
            // ASKED BEFORE THE ZERO IS INTERPRETED, and this is the second half of that rule. "No
            // records" and "this product never submitted that identifier" are the same sentence to a
            // reader, and they are only the same FACT when nothing was refused on the way to
            // counting them.
            if (CoidWitnessReport.ZeroIsProvisional(standing))
            {
                Line("VERDICT", "NOT ANSWERED — NO RECORDS, AND SOMETHING WAS REFUSED.");
                Cont("The count below is zero and the line above says a candidate beside the witness");
                Cont("was declined, or a rewrite did not land. Read the zero as provisional: it is not");
                Cont("evidence that nothing was ever submitted on this machine.");
                return new RestartCheckOutcome(0, "provisional-zero");
            }

            Line("VERDICT", "NOT ANSWERED — NO EXPERIMENT HAS BEEN SET UP.");
            Cont("No run of this product has recorded submitting a client order id on this");
            Cont("machine, so there is nothing to look for. This is not a reading about");
            Cont("ATAS. Run half 1 first:");
            Cont("");
            Cont("    probe atas --place-test-order --yes --leave-resting --yes-leave-it");
            return new RestartCheckOutcome(0, "no-record");
        }

        // The bridge's own view, which is the authoritative one: it can see object identity and the
        // in-memory map of what THIS session submitted, and this harness can see neither.
        var surface = connector.Bridge?.TradingSurface ?? hello.TradingSurface;
        var witnessToken = Token(surface, "witness");
        var coid = Token(surface, "coid");
        Line("BRIDGE witness=", witnessToken ?? "NOT REPORTED — this bridge predates the witness record");
        Line("BRIDGE coid=", coid ?? "NOT REPORTED — this bridge predates the coid reading");

        // Newest last in the file, so the newest acknowledged record is the one half 1 left.
        var candidate = records.LastOrDefault(r => !string.IsNullOrEmpty(r.BrokerOrderId));
        if (candidate is null)
        {
            Line("VERDICT", "NOT ANSWERED — NOTHING ON FILE WAS EVER ACKNOWLEDGED.");
            Cont($"{records.Count} record(s) exist and none carries a broker order id, so no run");
            Cont("ever saw ATAS assign an id to what it submitted. Without that half — the");
            Cont("half this product did not choose — a match on the comment alone is");
            Cont("satisfiable by any order carrying that text, and the bridge refuses it.");
            Cont("Re-run half 1 and check that it reports RESTING, CONFIRMED.");
            return new RestartCheckOutcome(0, "never-acknowledged");
        }

        Line("THE RECORD", $"client_order_id = {candidate.ClientOrderId}");
        Cont($"broker_order_id = {candidate.BrokerOrderId}");
        Cont($"written_at      = {candidate.WrittenAt:O}   (BEFORE the order was submitted)");
        Cont($"identified_at   = {candidate.IdentifiedAt:O}");
        Cont($"order           = {Blank(candidate.Side)} {Num(candidate.Quantity)} {Blank(candidate.Symbol)}" +
             $"{(candidate.Price is { } p ? $" @ {Num(p)}" : "")} on {Blank(candidate.AccountId)}");
        Cont($"session_id      = {candidate.SessionId}");
        Line("WHY IT IS EVIDENCE", "the written_at above is earlier than the order it describes. The claim");
        Cont("'this product submitted this identifier' was made before there was an");
        Cont("order to fit it to, by a process that has since ended — so it cannot be a");
        Cont("story composed afterwards around an order somebody found in the book.");

        // ------------------------------------------------------------------ did ATAS restart?

        var bridgeSession = witnessToken is null ? null : Field(witnessToken, "session");
        var recordSession = candidate.SessionId.Length >= 8 ? candidate.SessionId[..8] : candidate.SessionId;
        Line("BRIDGE SESSION", bridgeSession ?? "UNKNOWN — the bridge reports no witness token");
        Line("RECORD SESSION", recordSession);

        if (bridgeSession is not null && string.Equals(bridgeSession, recordSession, StringComparison.Ordinal))
        {
            Line("VERDICT", "NOT ANSWERED — ATAS HAS NOT BEEN RESTARTED.");
            Cont("The bridge answering right now is the SAME RUN that wrote that record.");
            Cont("Nothing has crossed a process boundary, so there is no cross-session");
            Cont("reading to take and there never will be from this state — the bridge");
            Cont("still has that identifier in memory and will go on reading it back");
            Cont("in-session, which is the vacuous reading this experiment exists to");
            Cont("escape.");
            Cont("");
            Cont("Close ATAS (saving the workspace), start it again, ACTIVATE the");
            Cont("TradeAgent Bridge strategy — it comes back stopped — and run this again.");
            return new RestartCheckOutcome(0, "not-restarted");
        }

        // ------------------------------------------------------------------ the live book

        Section("THE RESTART CHECK — THE LIVE BOOK");
        if (!handshake)
        {
            Line("VERDICT", "NOT ANSWERED — THE CONNECTOR HANDSHAKE NEVER COMPLETED.");
            Cont("The order book could not be read at all, so nothing was looked at. This");
            Cont("is the read failing, not the identifier. Do not record it as either.");
            return new RestartCheckOutcome(0, "no-connection");
        }

        IReadOnlyList<OrderInfo>? book = null;
        string? bookError = null;
        try { book = await connector.GetOrdersAsync("", includeInactive: true, since: null); }
        catch (Exception ex) { bookError = $"{ex.GetType().Name}: {ex.Message}"; }

        if (book is null)
        {
            Line("VERDICT", $"NOT ANSWERED — THE ORDER BOOK COULD NOT BE READ ({bookError}).");
            Cont("The read failed. That says nothing about ATAS and nothing about the");
            Cont("identifier.");
            return new RestartCheckOutcome(0, "read-failed");
        }

        Line("ORDERS IN LIVE BOOK", book.Count.ToString());
        Cont("account_id=\"\", include_inactive=true — ATAS's live order collection, the");
        Cont("same collection the bridge's own read-back scans.");

        var byBrokerId = book.Where(o => string.Equals(o.ConnectorOrderId, candidate.BrokerOrderId, StringComparison.Ordinal)).ToList();
        var byClientId = book.Where(o => Mine(o, candidate.ClientOrderId)).ToList();

        Line("ORDER SURVIVED", byBrokerId.Count > 0
            ? $"YES — an order with broker id {candidate.BrokerOrderId} is in the book"
            : $"NO — no order in the book carries broker id {candidate.BrokerOrderId}");
        Line("IDENTIFIER SURVIVED", byClientId.Count > 0
            ? $"YES — an order carries client_order_id = {candidate.ClientOrderId}"
            : $"NO — nothing in the book carries '{candidate.ClientOrderId}'");

        foreach (var o in byBrokerId.Concat(byClientId).DistinctBy(o => o.ConnectorOrderId))
        {
            Line("ORDER (raw)", "as it came off ATAS's own collection after the restart:");
            Console.WriteLine(Json.Write(o));
        }

        // ------------------------------------------------------------------ the verdict

        Section("THE RESTART CHECK — WHAT THIS PROVES");

        if (coid == "proven-crosssession")
        {
            Line("RULE 1", "PROVEN ACROSS A PROCESS RESTART. THIS IS THE ANSWER.");
            Cont($"'{candidate.ClientOrderId}' was written to the witness record BEFORE the order");
            Cont("was submitted, by a run of this product that has since ended. It is now on");
            Cont($"an order in ATAS's own collection carrying broker id {candidate.BrokerOrderId} —");
            Cont("the half that run did not choose. Nothing in this reading can be an object");
            Cont("this process constructed, because this process constructed no order at all.");
            Cont("");
            Cont("SupportsClientOrderId reports true from this, and unlike proven-sameref it");
            Cont("should be believed: reconciliation after a dropped connection can resolve");
            Cont("orders by client order id, because the identifier demonstrably survives");
            Cont("whatever dropped it.");
            Cont("");
            Cont("WHAT IT STILL DOES NOT PROVE, and this bound is real. A cross-session match");
            Cont("cannot separate ATAS rebuilding the order from THE BROKER'S own answer on");
            Cont("reconnect from ATAS rehydrating it out of its own local store. All three");
            Cont("survive a restart and look identical from inside a chart strategy. So the");
            Cont("identifier survives ATAS being restarted; whether it ever reached the");
            Cont("broker is a different question and only the broker's own report answers it.");
            Cont("");
            Cont("ReconciliationProvable still needs the other half — SupportsOrderHistory,");
            Cont("which is false for a known reason. Autonomy stays refused on that.");
            return new RestartCheckOutcome(0, "proof");
        }

        if (byBrokerId.Count > 0 && byClientId.Count == 0)
        {
            Line("RULE 1", "DISPROVEN. THE ORDER SURVIVED AND THE IDENTIFIER DID NOT.");
            Cont($"The order this run went looking for IS in ATAS's collection — matched on");
            Cont($"broker id {candidate.BrokerOrderId}, which a previous run of this product");
            Cont("recorded before ATAS was restarted — and it does NOT carry the client order");
            Cont($"id '{candidate.ClientOrderId}' that was submitted with it.");
            Cont("");
            Cont("THIS IS A REAL, NEGATIVE, SHIPPABLE ANSWER. It is not an absence of");
            Cont("evidence: the control is present, because the order came back. On this");
            Cont("platform the client order id does not survive an ATAS restart, so after a");
            Cont("crash or a disconnect there is no identifier to reconcile by and 'did my");
            Cont("order land' has no answer. SupportsClientOrderId must stay false and the");
            Cont("gateway must go on refusing LIVE_AUTONOMOUS on this backend.");
            Cont("");
            Cont("Before recording it as final: confirm it is repeatable, and read the raw");
            Cont("order above for where the identifier went. The bridge writes it to");
            Cont("Order.Comment, the only client-settable string on an ATAS order.");
            return new RestartCheckOutcome(0, "disproof");
        }

        if (byBrokerId.Count == 0)
        {
            Line("RULE 1", "NOT ANSWERED — THE ORDER ITSELF DID NOT SURVIVE.");
            Cont($"Nothing in ATAS's collection carries broker id {candidate.BrokerOrderId}, so the");
            Cont("order half 1 left resting is not there. It may have been cancelled, expired");
            Cont("with the session (it is a Day order), filled, or been dropped from the live");
            Cont("collection when the platform restarted.");
            Cont("");
            Cont("THIS SAYS NOTHING ABOUT THE IDENTIFIER AND MUST NOT BE RECORDED AS A");
            Cont("NEGATIVE. The experiment needs an order to survive so that the question");
            Cont("'did the comment survive with it' can be asked at all; with no order there");
            Cont("is no question, only an empty book — which is exactly what a working");
            Cont("platform looks like the morning after. Set it up again and read it sooner:");
            Cont("");
            Cont("    probe atas --place-test-order --yes --leave-resting --yes-leave-it");
            return new RestartCheckOutcome(0, "not-answered");
        }

        // Both ids are in the book and the bridge has not reported the cross-session reading. That
        // is the two sources disagreeing, and the disagreement is the finding.
        Line("RULE 1", "THE EVIDENCE IS PRESENT AND THE BRIDGE HAS NOT READ IT — INVESTIGATE.");
        Cont("An order in ATAS's collection carries BOTH the recorded broker id and the");
        Cont($"recorded client order id, and the bridge reports coid={Blank(coid)} rather than");
        Cont("proven-crosssession. This harness cannot see what the bridge can — object");
        Cont("identity, and its own in-memory map — so it cannot tell you which is right.");
        Cont("");
        Cont("Two ordinary explanations before suspecting a defect: the read-back may not");
        Cont("have run yet (it is driven by the handshake and every heartbeat, so wait a");
        Cont("few seconds and re-run), or the bridge deployed in ATAS may predate the");
        Cont("cross-session reading — check that BRIDGE witness= above is reported at all.");
        Cont("Do not record a proof from this line: the bridge's reading is the one with");
        Cont("the guards on it, and it has not been taken.");
        return new RestartCheckOutcome(0, "unread");
    }

    /// <summary>
    /// Pulls one "name:value" out of a comma-joined token such as the witness report. Same job as
    /// <see cref="Token"/> one level down, and null when the field is absent — which is a real case
    /// for a bridge older than the field.
    /// </summary>
    static string? Field(string token, string name)
    {
        foreach (var part in token.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length > name.Length + 1 && part[name.Length] == ':'
                && part.AsSpan(0, name.Length).SequenceEqual(name))
                return part[(name.Length + 1)..];
        return null;
    }

    // -------------------------------------------------------------------------------- test order

    /// <summary>
    /// How far below the bid the resting limit is placed, as a FRACTION OF THE LIVE BID and never as
    /// a price. A hard-coded price is the exact failure this is shaped to avoid: a constant that is
    /// safely far below the market today is above it the day the contract rolls or the market halves,
    /// and then the order that was supposed to rest fills the instant it is submitted.
    ///
    /// Large enough that no ordinary move reaches it; small enough that a venue is unlikely to refuse
    /// it for sitting outside a price band. If a run comes back with a definite refusal that names a
    /// price limit, this is the one line to change.
    /// </summary>
    const decimal FarBelowBid = 0.10m;

    /// <summary>How long to keep re-reading ATAS's order collection while waiting for the order to
    /// appear. ATAS assigns its own order id asynchronously, so a single read taken straight after
    /// the place call reports "no broker id" for an order that is about to get one — a different
    /// finding, and the wrong one.</summary>
    static readonly TimeSpan ReadBackLimit = TimeSpan.FromSeconds(15);

    /// <summary>The same, waiting for the cancel to show up in the collection.</summary>
    static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Exit code, the fragment this contributes to the one-line summary, and whether anything was
    /// actually submitted.
    ///
    /// <paramref name="Placed"/> is not decoration: a run the guard refused took its readings before
    /// any order existed, and a later section that described them as "after the test order" would be
    /// claiming a measurement that never happened.
    ///
    /// <paramref name="RestingClientOrderId"/> is set only when this run DELIBERATELY left an order
    /// on the book, and it exists so the banner at the very end of the run can print the exact
    /// command that removes it. A person who has to reconstruct that identifier by scrolling back
    /// through a thousand lines of output is a person who will not bother.
    /// </summary>
    readonly record struct TestOrderOutcome(int ExitCode, string Summary, bool Placed,
                                            string? RestingClientOrderId = null);

    /// <summary>One round of re-reading the order collection: what came back, and how hard we looked.</summary>
    readonly record struct OrderPoll(IReadOnlyList<OrderInfo>? Orders, string? Error, int Reads, TimeSpan Took);

    /// <summary>
    /// <c>--place-test-order</c> — the only part of this harness that writes anything.
    ///
    /// It exists because rule 1 cannot be observed without an order. Until one is placed,
    /// <c>SupportsClientOrderId</c> is false for a reason that says nothing at all about ATAS
    /// ("NOTHING WAS EVER ATTEMPTED"), and that non-answer is what has been blocking the product's
    /// single most important question: may this ever trade unattended.
    ///
    /// The shape of it:
    ///
    ///   * Through <see cref="AtasConnector"/>, the product's own connector — the same call
    ///     TradingGateway makes. Hand-rolling a bridge frame would measure a path nothing uses.
    ///   * ONE buy limit, quantity 1, on the chart's own instrument, priced off the LIVE BID and
    ///     rounded DOWN, so it rests instead of filling. No quote means no order: an order at a
    ///     price nobody measured is the thing this codebase refuses to send.
    ///   * A guard that refuses to submit anything unless the account is PROVABLY simulated, from
    ///     two independent readings, with no flag to override it.
    ///   * A cancel at the end, on every path including the failures, and then a re-read to find out
    ///     whether the cancel actually took.
    ///
    /// Note what is NOT here: the gateway. This goes straight at the connector, so the risk policy,
    /// the approval queue, the kill switch and the trading mode are all absent. The guard below is
    /// not a second line of defence behind those — it is the only one.
    /// </summary>
    static async Task<TestOrderOutcome> PlaceTestOrder(AtasConnector connector, bool handshake,
                                                       IReadOnlyList<OrderInfo>? ordersBefore,
                                                       bool leaveResting, bool viaAsync)
    {
        Section("THE TEST ORDER — THE GUARD");
        if (leaveResting)
        {
            Line("AND IT WILL REMAIN", "--leave-resting --yes-leave-it were both given, so the cancel at the");
            Cont("end of this section DOES NOT RUN. Every guard below is unchanged and");
            Cont("still refuses on anything but a provably simulated account — what");
            Cont("changes is only what happens after the read-back.");
        }
        Line("WHAT THIS GUARD IS", "the only thing standing between this harness and a real account.");
        Cont("There is deliberately no --force, no --account and no --symbol: every");
        Cont("check below either passes on evidence read from the live connection, or");
        Cont("nothing is submitted at all. A probe that CAN place an order on a real");
        Cont("account is a probe that eventually WILL — the override gets added for a");
        Cont("good reason on a bad day, and then it is in the shell history forever.");

        if (!handshake)
            return Refuse("THE CONNECTOR HANDSHAKE NEVER COMPLETED.",
                "The capabilities above could only be derived from the raw hello frame, and a",
                "hello is not a live connection. An order placed onto a bridge that is not",
                "answering could be neither read back nor cancelled, which is the one thing",
                "this verb must never do.");

        // Read at the moment of the decision rather than inherited from the capability block above.
        // Heartbeats carry a fresh Describe() every five seconds, so that reading is already seconds
        // old, and a guard has to act on the newest answer there is rather than a remembered one.
        var live = connector.Bridge;
        if (live is null)
            return Refuse("THE BRIDGE IS NO LONGER ON THE PIPE.",
                "AtasConnector.Bridge is null, so the connection dropped somewhere between the",
                "capability block above and this line.");

        Line("IsSimulated (hello)", $"{Yn(live.IsSimulated)}   read off the live handshake, at this moment");
        if (!live.IsSimulated)
            return Refuse("THE ACCOUNT IS NOT REPORTED AS SIMULATED.",
                "The bridge's own handshake says is_simulated=false. That is either a real",
                "account or one whose nature ATAS did not report, and here those two are the",
                "same answer: neither is proof of a simulated account. This harness places",
                "orders on proven simulation and on nothing else.");

        var wanted = live.AccountId;
        Line("ACCOUNT ID (hello)", Blank(wanted));
        if (string.IsNullOrWhiteSpace(wanted))
            return Refuse("THE BRIDGE REPORTS NO ACCOUNT ID.",
                "is_simulated is true, but about nothing in particular. With no account id in",
                "the handshake there is no way to check that the account an order would land",
                "on is the account that boolean is describing. Unknown is not simulated.");

        // Read again here rather than reusing the list from the section above, so that the guard
        // rests on nothing it did not fetch itself.
        IReadOnlyList<AccountInfo>? accounts = null;
        string? accountsError = null;
        try { accounts = await connector.GetAccountsAsync(); }
        catch (Exception ex) { accountsError = $"{ex.GetType().Name}: {ex.Message}"; }

        if (accounts is null)
            return Refuse("THE ACCOUNT LIST COULD NOT BE READ.",
                accountsError ?? "GetAccounts returned nothing.",
                "The hello's is_simulated cannot be corroborated against ATAS's own portfolio,",
                "so it stands alone — and one unverifiable boolean is not proof of anything.");

        var account = accounts.FirstOrDefault(a => string.Equals(a.Id, wanted, StringComparison.Ordinal));
        if (account is null)
            return Refuse("NO ACCOUNT MATCHES THE ONE THE HANDSHAKE NAMED.",
                $"The hello says '{wanted}'. ATAS reports: " +
                (accounts.Count == 0 ? "no account at all." : string.Join(", ", accounts.Select(a => a.Id))),
                "The is_simulated flag describes the account the hello named, so it says",
                "nothing about an account that is not in that list. An order placed now would",
                "be placed onto something nothing in this run has vouched for.");

        Line("ACCOUNT MATCHED", $"{account.Id} — {account.Name} ({account.Currency})");
        Line("IsSimulated (account)", $"{Yn(account.IsSimulated)}   read off ATAS's own portfolio — a second, " +
                                      "independent source");
        if (!account.IsSimulated)
            return Refuse("THE TWO SOURCES DISAGREE ABOUT THIS ACCOUNT.",
                $"The handshake says is_simulated=true for '{wanted}'; ATAS's own portfolio object",
                "for that same account says false. One of the two is wrong and this run cannot",
                "tell which. A disagreement about whether an account is real is not a thing to",
                "resolve by picking the more convenient answer.");

        Line("TradingEnabled", $"{Yn(account.TradingEnabled)}" + (account.TradingEnabled
            ? ""
            : "   — ATAS reports this account cannot trade. Proceeding anyway:"));
        if (!account.TradingEnabled)
        {
            Cont("a refusal from ATAS is itself a reading, and refusing to ask would");
            Cont("produce no reading at all. Expect the placement below to fail.");
        }

        // ------------------------------------------------------------------------------ the price

        Section("THE TEST ORDER — THE PRICE");

        IReadOnlyList<InstrumentInfo>? instruments = null;
        string? instrumentsError = null;
        try { instruments = await connector.GetInstrumentsAsync(); }
        catch (Exception ex) { instrumentsError = $"{ex.GetType().Name}: {ex.Message}"; }

        if (instruments is null || instruments.Count == 0)
            return Refuse("THERE IS NO INSTRUMENT TO TRADE.",
                instrumentsError ?? "ATAS reported an empty instrument list.",
                "The adapter returns the chart's own instrument first because that is the one a",
                "chart strategy can trade. With nothing in the list there is nothing to name in",
                "an order, and naming one anyway would be inventing it.");

        // Deliberately instruments[0] and not a search by name: the adapter documents that the
        // chart's own instrument comes first, and a --symbol flag would be one more way to point
        // this at something nobody intended.
        var instrument = instruments[0];
        Line("INSTRUMENT", $"{instrument.Symbol} — {instrument.Description} ({instrument.Exchange})");
        Cont($"tick={Num(instrument.TickSize)} tick_value={Num(instrument.TickValue)}" +
             $"{(instruments.Count > 1 ? $"   ({instruments.Count} instruments visible; the chart's own comes first)" : "")}");

        QuoteInfo? quote = null;
        string? quoteError = null;
        try { quote = await connector.GetQuoteAsync(instrument.Symbol); }
        catch (Exception ex) { quoteError = $"{ex.GetType().Name}: {ex.Message}"; }

        if (quote is null)
            return Refuse("THERE IS NO QUOTE FOR THIS INSTRUMENT.",
                quoteError ?? "GetQuote returned nothing.",
                "The resting price is derived from the live bid and from nothing else. With no",
                "quote there is no bid, and the only way to continue would be to invent a",
                "number — which is precisely what makes an order dangerous. Check that the",
                "chart is receiving data, and run this again.");

        Line("QUOTE (raw)", "as the bridge answered, one line:");
        Console.WriteLine(Json.Write(quote));

        if (quote.Bid is not { } bid || bid <= 0m)
            return Refuse("THE QUOTE CARRIES NO USABLE BID.",
                $"bid={(quote.Bid is { } b ? Num(b) : "<none>")}. The adapter reports a zero bid as",
                "null rather than as a price, so this is ATAS saying it does not have one. The",
                "ask and the last trade are NOT substituted for it: an order priced off a",
                "different side than the one this was designed around is a different order.");

        Line("QUOTE TIMESTAMP", quote.At == DateTimeOffset.MinValue
            ? "MinValue — the bridge has never watched this quote move."
            : $"{quote.At:O}   ({(DateTimeOffset.UtcNow - quote.At).TotalSeconds:0}s ago)");
        if (quote.At == DateTimeOffset.MinValue)
        {
            Cont("That is the adapter being honest rather than a fault: it stamps a quote");
            Cont("with the moment it was OBSERVED to move, and it has seen no tick here");
            Cont("yet. The prices themselves are read off ATAS's security object at the");
            Cont("instant of the call, so they are current — but nothing in this run proves");
            Cont("the feed is live, and the offset below is what carries that risk.");
        }

        var price = SnapDown(bid * (1m - FarBelowBid), instrument.TickSize);
        Line("RESTING PRICE", instrument.TickSize > 0m
            ? $"{Num(price)}  =  bid {Num(bid)} less {Pct(FarBelowBid)}, rounded DOWN to the {Num(instrument.TickSize)} tick."
            : $"{Num(price)}  =  bid {Num(bid)} less {Pct(FarBelowBid)}, not rounded here.");
        if (instrument.TickSize > 0m)
        {
            Cont("Down, and never to nearest: a rounding error has to move the price");
            Cont("further from the market, never closer to it.");
        }
        else
        {
            Cont("ATAS reports no tick size for this instrument, so there is nothing to");
            Cont("round to. The bridge's own ShrinkPrice rounds it on the way out, and");
            Cont("whatever that does is smaller than one tick either way.");
        }

        // The offset above is arithmetic. This is the check on it — a price that is not strictly
        // below every price in the quote is not a resting order, whatever the arithmetic said.
        if (price <= 0m)
            return Refuse("THE DERIVED PRICE IS NOT A POSITIVE NUMBER.",
                $"bid {Num(bid)} less {Pct(FarBelowBid)}, snapped, came to {Num(price)}.");

        var notBelow = new List<string>();
        if (price >= bid) notBelow.Add($"bid {Num(bid)}");
        if (quote.Ask is { } ask && price >= ask) notBelow.Add($"ask {Num(ask)}");
        if (quote.Last is { } last && price >= last) notBelow.Add($"last {Num(last)}");
        if (notBelow.Count > 0)
            return Refuse("THE DERIVED PRICE IS NOT BELOW THE MARKET.",
                $"{Num(price)} is at or above {string.Join(" and ", notBelow)}.",
                "A buy limit at or above the market fills immediately, which is the one",
                "outcome this order is shaped to avoid. The arithmetic says it should be",
                "below; the quote says it is not. Nothing was submitted, and the",
                "disagreement is the finding — do not place anything here until it is",
                "explained.");
        Line("PRICE CHECKED", $"{Num(price)} is strictly below the bid" +
                              $"{(quote.Ask is not null ? ", the ask" : "")}" +
                              $"{(quote.Last is not null ? " and the last trade" : "")}. It rests.");

        // ----------------------------------------------------------------------------- placing it

        // Unique per run, and unmistakably ours: the read-back must not be satisfiable by somebody
        // else's order that happens to carry a comment. That was a real defect once — see the note
        // on ProveClientOrderId — and this is the harness end of the same discipline.
        var clientOrderId = $"TA-PROBE-{DateTimeOffset.Now:yyyyMMddHHmmss}";

        Section("THE TEST ORDER — PLACING IT");
        Line("CLIENT ORDER ID", clientOrderId);
        Line("THE ORDER", $"BUY LIMIT 1 {instrument.Symbol} @ {Num(price)}  TIF=Day  on {account.Id}");
        Cont("TIF=Day is the last line of defence: if every cleanup path below fails,");
        Cont("a Day order still expires with the session instead of resting for weeks.");
        if (viaAsync)
        {
            Line("PATH", "AtasConnector.PlaceOrderViaAsyncOverloadAsync — the MEASUREMENT route.");
            Cont("Still the product's own connector and still the real wire, but it sends");
            Cont("BridgeOps.PlaceViaAsyncOverload, which nothing in the product sends and");
            Cont("which TradingGateway cannot reach: the only placement on");
            Cont("ITradingConnector is PlaceOrderAsync, and that sends BridgeOps.Place.");
            Cont("Inside the bridge this is the only thing that selects PlaceRoute.");
            Cont("MeasureAsync, and it submits through ITradingManager.OpenOrderAsync.");
            Cont("Everything else about the order is the same code as the ordinary path.");
        }
        else
        {
            Line("PATH", "AtasConnector.PlaceOrderAsync — the product's own connector, the");
            Cont("same call TradingGateway makes. Nothing is hand-rolled onto the wire");
            Cont("here, so what this measures is the path that would actually be traded.");
        }

        var attemptsBefore = connector.Bridge?.ClientOrderIdAttempts;
        var checksBefore = connector.Bridge?.ClientOrderIdChecks;
        var surfaceBefore = connector.Bridge?.TradingSurface;

        var cmd = new PlaceOrderCommand(clientOrderId, account.Id, instrument.Symbol, OrderSide.Buy,
            OrderType.Limit, 1m, price, null, TimeInForce.Day,
            // Comment stays null on purpose. The adapter puts the CLIENT ORDER ID on Order.Comment —
            // the only client-settable string ATAS offers — and deliberately does not merge this
            // field in, so anything put here would be silently dropped rather than carried.
            null);

        OrderInfo? placed = null;
        string? placeError = null;
        var rejected = false;
        var reading = "unknown";
        var everSeen = false;
        TestOrderOutcome cleanup;

        // try/finally, not a linear path: the cancel below must happen even if the reporting between
        // here and there throws for a reason nobody predicted. An order that was placed and then not
        // cleaned up is the single outcome this verb is not allowed to produce.
        try
        {
            try
            {
                placed = viaAsync
                    ? await connector.PlaceOrderViaAsyncOverloadAsync(cmd)
                    : await connector.PlaceOrderAsync(cmd);
                Line("PLACE CALL", "RETURNED — ATAS took the order without a definite refusal.");
            }
            catch (ConnectorRejectedException ex)
            {
                rejected = true;
                placeError = $"{ex.GetType().Name}: {ex.Message}";
                Line("PLACE CALL", "DEFINITELY REFUSED.");
                Cont(placeError);
                Cont("That is the bridge's AtasRejectedException arriving as the wire's");
                Cont("rejected:true, and rule 3 reserves it for a definite broker refusal and");
                Cont("nothing else. Taken at its word, nothing is live at the broker — and the");
                Cont("cancel below runs anyway, because a word is not a reading.");
            }
            catch (Exception ex)
            {
                placeError = $"{ex.GetType().Name}: {ex.Message}";
                Line("PLACE CALL", "FAILED, AND THE OUTCOME IS UNKNOWN.");
                Cont(placeError);
                Cont("Not a rejection: the bridge did not say the broker refused. Under rule 3");
                Cont("this means the order MAY BE LIVE — a timeout or a dropped connection");
                Cont("looks exactly like this and neither one un-places an order. Everything");
                Cont("below proceeds on the assumption that it might be resting.");
            }

            try
            {
                (reading, everSeen) = await ReportReadBack(connector, clientOrderId, placed, rejected,
                                                           ordersBefore, attemptsBefore, checksBefore, surfaceBefore,
                                                           viaAsync);
            }
            catch (Exception ex)
            {
                Line("READ-BACK", $"COULD NOT BE REPORTED — {ex.GetType().Name}: {ex.Message}");
                Cont("The cleanup below still runs; it does not depend on any of this.");
            }
        }
        finally
        {
            // The cancel is skipped ONLY when this run was asked twice to skip it, and only when
            // something was actually submitted. A refusal or a rejection leaves nothing to leave
            // behind, so those still go through the ordinary cleanup — which is what makes the
            // "nothing was placed" cases exit 0 rather than 5.
            cleanup = leaveResting && !rejected
                ? await LeaveResting(connector, clientOrderId, placed, everSeen)
                : await CleanUp(connector, clientOrderId, placed, rejected, everSeen);
        }

        return cleanup with { Summary = $"{reading}/{cleanup.Summary}" };
    }

    /// <summary>
    /// HALF 1's ENDING: do not cancel, and then prove that what was left behind is really there.
    ///
    /// The point of not cancelling is that half 2 needs an order to still exist after ATAS has been
    /// restarted. So the one thing this must not do is announce "left resting" without checking —
    /// an experiment set up on an order that was never on the book produces a "not answered" two
    /// hours later, and nothing would say the setup was what failed.
    ///
    /// Three endings, and only the first is the experiment:
    ///
    ///   * RESTING, CONFIRMED. Read back off ATAS's own collection in a live state. Exit 5.
    ///   * FINISHED ALREADY. It filled or was killed. Nothing is resting, so there is nothing for
    ///     half 2 to find, and saying "left resting" would be false. Exit 4 — the book is not in
    ///     the state this run intended and somebody should look.
    ///   * NOT VISIBLE. It may be live and unreadable, or it may never have landed. Exit 4, loudly:
    ///     this is the case where an order might be resting that nothing is tracking.
    /// </summary>
    static async Task<TestOrderOutcome> LeaveResting(AtasConnector connector, string clientOrderId,
                                                     OrderInfo? placed, bool everSeen)
    {
        Section("THE TEST ORDER — LEAVING IT RESTING");
        Line("NOT CANCELLING", "--leave-resting --yes-leave-it. The order below is meant to survive");
        Cont("this run, an ATAS restart, and the gap in between.");

        // Wait for it to be visibly live rather than reading once: ATAS assigns state and id
        // asynchronously, and "not resting yet" a second after the place call is not an answer.
        var poll = await PollOrders(connector,
            list => list.Any(o => Mine(o, clientOrderId) && OrderStateMachine.IsLive(o.State)), CleanupLimit);

        if (poll.Orders is null)
            return Loud("LEFT-UNVERIFIED", "THE ORDER WAS NOT CANCELLED AND THE BOOK COULD NOT BE RE-READ.",
                $"Nothing was cancelled, so whatever was placed is still there — but this run",
                $"cannot show it ({poll.Error}). Open ATAS and look for an order whose comment",
                $"is '{clientOrderId}' before starting half 2, because half 2 reads a 'not",
                "answered' the same way whether the order vanished or was never there.");

        var mine = poll.Orders.Where(o => Mine(o, clientOrderId)).ToList();
        var resting = mine.Where(o => OrderStateMachine.IsLive(o.State)).ToList();
        Line("BOOK RE-READ", $"{poll.Orders.Count} order(s) in the collection, {mine.Count} carrying this run's id" +
                             $"   (read {poll.Reads} time(s) over {poll.Took.TotalSeconds:0.0}s)");
        foreach (var o in mine)
            Cont($"  {o.State}  filled={Num(o.FilledQuantity)}/{Num(o.Quantity)}  id={Blank(o.ConnectorOrderId)}");

        if (resting.Count == 0)
        {
            if (mine.Count > 0)
                return Loud("LEFT-BUT-FINISHED", "THE ORDER WAS NOT CANCELLED AND IS NOT RESTING EITHER.",
                    $"It is in ATAS's collection carrying '{clientOrderId}' and it has finished:",
                    string.Join(", ", mine.Select(o => $"{o.ConnectorOrderId} {o.State}")),
                    "",
                    "Half 2 has nothing to find, so do not run it against this — it would report",
                    "'not answered', which is the correct reading of an order that is not there",
                    "and says nothing whatever about the identifier. Find out why it finished",
                    "first: a buy limit far below the bid should not have.");

            return Loud("LEFT-UNVERIFIED", "THE ORDER WAS NOT CANCELLED AND NOTHING OF THIS RUN'S IS VISIBLE.",
                "The placement was not refused, so it may have reached the broker, and nothing",
                $"carrying '{clientOrderId}' is in ATAS's collection — before or now.",
                everSeen ? "It WAS visible earlier in this run, which makes its absence now the finding."
                         : "It has not been visible at any point in this run.",
                "",
                "Two different states share this reading: an order resting where this bridge",
                "cannot see it, and an order that never landed. Look in ATAS before doing",
                "anything else, and do not start half 2 until you know which.");
        }

        var it = resting[0];
        Line("RESTING, CONFIRMED", $"{it.State}  id={Blank(it.ConnectorOrderId)}  filled={Num(it.FilledQuantity)}/{Num(it.Quantity)}");
        Cont("Observed in ATAS's own order collection, not inferred from the place call");
        Cont("having returned. This is the order half 2 goes looking for.");
        Line("CLIENT ORDER ID", clientOrderId);
        Cont("Written to the bridge's durable witness record BEFORE this order was");
        Cont("submitted, together with the broker id above once ATAS assigned it. Half 2");
        Cont("reads that record; you do not need to carry this identifier anywhere.");

        Section("HALF 1 IS COMPLETE — WHAT TO DO NEXT");
        Line("1. RESTART ATAS", "close it (saving the workspace, or the strategy does not come back)");
        Cont("and start it again. The TradeAgent Bridge strategy comes back STOPPED —");
        Cont("press Activate on it, or the bridge never dials in. (Traps 22-24.)");
        Line("2. RUN HALF 2", "probe atas --coid-restart-check");
        Cont("It places nothing. That is the measurement, not the caution.");
        Line("IF YOU STOP HERE", $"probe atas --cancel-resting {clientOrderId}");
        Cont("removes the order. Nothing else will: it is not cancelled by this run, by");
        Cont("half 2, or by the bridge restarting.");

        return new TestOrderOutcome(5, "left-resting", Placed: true, RestingClientOrderId: clientOrderId);
    }

    /// <summary>
    /// What came back, reported as separate facts rather than as one verdict.
    ///
    /// The read-back searches on TWO keys — this run's client order id, and the connector order id
    /// the place call returned — because the case where the order is in ATAS's collection under a
    /// broker id but WITHOUT the client identifier is the single most important thing this verb can
    /// discover. Searching only on the client id would report that as "nothing came back", which is
    /// a completely different fact and the reassuring one.
    /// </summary>
    static async Task<(string Summary, bool EverSeen)> ReportReadBack(AtasConnector connector, string clientOrderId,
        OrderInfo? placed, bool rejected, IReadOnlyList<OrderInfo>? ordersBefore, int? attemptsBefore, int? checksBefore,
        string? surfaceBefore, bool viaAsync)
    {
        Section("THE TEST ORDER — READING IT BACK");

        if (placed is not null)
        {
            Line("ORDER (raw)", "as PlaceOrderAsync handed it back, one line:");
            Console.WriteLine(Json.Write(placed));
            Line("ORDER (pretty)", "the same, re-indented for reading:");
            Console.WriteLine(Json.Write(placed, pretty: true));
        }

        var placedId = placed is { ConnectorOrderId.Length: > 0 } ? placed.ConnectorOrderId : null;

        // A definite refusal means nothing was submitted, so nothing is on its way and waiting out
        // the full budget only delays the report. The look still happens — rule 3 is trusted, and
        // trusting it is still not the same act as checking it.
        var limit = rejected ? TimeSpan.FromSeconds(2) : ReadBackLimit;
        var poll = await PollOrders(connector,
            list => list.Any(o => Mine(o, clientOrderId)) || (placedId is not null && list.Any(o => o.ConnectorOrderId == placedId)),
            limit);

        Line("ORDERS BEFORE", ordersBefore is null ? "COULD NOT BE READ" : ordersBefore.Count.ToString());
        Line("ORDERS AFTER", poll.Orders is null
            ? $"COULD NOT BE READ — {poll.Error}"
            : poll.Orders.Count.ToString());
        Cont($"re-read {poll.Reads} time(s) over {poll.Took.TotalSeconds:0.0}s, with account_id=\"\" and");
        Cont("include_inactive=true — the same reading as the section above, so the two");
        Cont("counts are directly comparable.");
        if (rejected)
        {
            Cont("A short budget, because the placement was definitively refused: nothing");
            Cont("is on its way, and this look is a check rather than a wait.");
        }

        var byClientId = poll.Orders?.Where(o => Mine(o, clientOrderId)).ToList();
        var byPlacedId = placedId is null ? null : poll.Orders?.Where(o => o.ConnectorOrderId == placedId).ToList();
        var found = byClientId is { Count: > 0 } ? byClientId[0] : byPlacedId is { Count: > 0 } ? byPlacedId[0] : null;
        var everSeen = found is not null;

        Line("CAME BACK AT ALL", poll.Orders is null
            ? "UNKNOWN — the order collection could not be read"
            : found is null
                ? "NO — nothing in ATAS's collection matches this run, on either key"
                : $"YES — matched on {(byClientId is { Count: > 0 } ? "this run's client order id" : "the connector order id the place call returned")}");

        if (found is not null)
        {
            Line("READ BACK (raw)", "the order as it came back off ATAS's own collection, one line:");
            Console.WriteLine(Json.Write(found));
            Line("READ BACK (pretty)", "the same, re-indented for reading:");
            Console.WriteLine(Json.Write(found, pretty: true));
        }

        Line("CARRIES OUR ID", poll.Orders is null
            ? "UNKNOWN — the collection could not be read"
            : byClientId is { Count: > 0 }
                ? $"YES — client_order_id = {byClientId[0].ClientOrderId}"
                : found is not null
                    ? $"NO — the order is there and its client_order_id is {Blank(found.ClientOrderId)}"
                    : "NOT ANSWERED — nothing came back to carry it");

        var broker = found is null ? null : found.ConnectorOrderId;
        Line("CARRIES A BROKER ID", found is null
            ? "NOT ANSWERED — nothing came back to carry it"
            : BrokerAssigned(broker)
                ? $"YES — connector_order_id = {broker}"
                : $"NO — connector_order_id = {Blank(broker)}");
        if (found is not null && !BrokerAssigned(broker))
        {
            Cont("An id beginning 'ext:' is the adapter's own synthetic handle, produced by");
            Cont("OrderKey when ATAS has not assigned Order.Id yet. It is not broker-assigned,");
            Cont("and the bridge's own read-back skips an order without a real Order.Id — so");
            Cont("an 'ext:' id cannot satisfy rule 1 either. Re-run in a moment if the broker");
            Cont("was simply slow to acknowledge.");
        }

        // The counters ride on the heartbeat, once every five seconds, so a reading taken the instant
        // after the place call can legitimately still show the old numbers. Wait for them rather than
        // reporting "the bridge did not count it" about a frame that had not arrived yet.
        if (attemptsBefore is { } ab)
            await Until(() => connector.Bridge?.ClientOrderIdAttempts is { } now && now > ab, TimeSpan.FromSeconds(15));

        var attemptsAfter = connector.Bridge?.ClientOrderIdAttempts;
        var checksAfter = connector.Bridge?.ClientOrderIdChecks;
        Line("SUBMITTED WITH AN ID", Counter(attemptsBefore, attemptsAfter));
        Line("READ-BACKS PERFORMED", Counter(checksBefore, checksAfter));
        Cont("the bridge's own counters, reported by it rather than inferred here.");

        // ---- the OpenOrderAsync question, measured through the path actually in use ----
        //
        // The bridge times its own submission and the wait that follows it. That wait ends on a state
        // change or an assigned Id, which IS acknowledgement arriving — so `gap` is this platform's
        // acknowledgement latency, and it decides whether the question can be answered here at all.
        // A platform that acknowledges instantly cannot separate "the task completed on submission"
        // from "the task completed on acknowledgement", and a fast reading on it proves nothing.
        // WAIT FOR THE TOKEN, DO NOT JUST READ IT. The surface report rides on the heartbeat, once
        // every five seconds, so the reading taken the instant after a place call is very often the
        // one from BEFORE it — which the freshness check below would then correctly, and uselessly,
        // report as "not this run's". Waiting for it to change turns that from a coin toss into an
        // answer. Bounded, and a timeout is not fatal: the check below still says what happened.
        //
        // It is deliberately its own wait rather than a lean on the counter wait above. That one is
        // conditioned on the bridge reporting attempt counters at all, so on a bridge that does not,
        // this section would silently go back to reading whatever had arrived by then.
        var placeBefore = Token(surfaceBefore, "place");
        await Until(() => Token(connector.Bridge?.TradingSurface, "place") is { } now && now != placeBefore,
                    TimeSpan.FromSeconds(15));

        var place = Token(connector.Bridge?.TradingSurface, "place");
        Line("PLACE TIMING", place ?? "not reported — the bridge is older than this probe");
        Cont("route;call=<submission call>;atreturn=<state/id when it returned>;");
        Cont("settled=<when ATAS acknowledged>;gap=settled-call;now=<state/id since>");

        // IS THIS TOKEN THIS RUN'S, OR THE LAST ORDER'S? The bridge writes it BELOW its submission
        // and that path has no catch, so an exception raised at or before the submission — a
        // pre-flight refusal, or an AtasCallTimeoutException out of the async route — leaves the
        // PREVIOUS order's reading standing. (A refusal ATAS reports through its order-failure event
        // is detected after the write, so that one IS this order's reading. The token cannot tell
        // the two apart, which is the point.)
        //
        // Reading a stale token as this run's answer is the one way this section can lie, and it is
        // the likeliest failure of the async route specifically: a task that never completes is
        // exactly the outcome the measurement exists to rule out, and it is the case that produces
        // no reading at all. Comparing against the token read before placing settles it, at no cost.
        var fresh = place is not null && place != placeBefore;
        if (place is not null)
        {
            Line("IS IT THIS RUN'S?", fresh
                ? "YES — the token CHANGED across the placement, so it describes this order."
                : "NO — THE TOKEN IS UNCHANGED FROM BEFORE THIS RUN PLACED ANYTHING.");
            if (!fresh)
            {
                Cont($"before: {Blank(placeBefore)}");
                Cont("The bridge writes this below its submission and there is no catch on that");
                Cont("path, so an exception at or before the submission leaves the previous");
                Cont("order's reading in place. Read PLACE CALL above for what happened, and do");
                Cont("not read a single number below as this run's. (It can also be a heartbeat");
                Cont("that has not arrived yet — heartbeats carry the surface every five");
                Cont("seconds. Re-running is the cheapest way to tell those two apart.)");
            }
        }

        // WHICH CALL WAS TIMED. Three routes report through this one token and their call= readings
        // mean three different things: sync is ITradingManager.OpenOrder, connector is
        // IDataFeedConnector.RegisterOrderAsync, asyncoverload is ITradingManager.OpenOrderAsync.
        // The bridge chooses the connector route for an off-chart order regardless of what was
        // asked — correctness for the ORDER outranks a measurement — so asking for the async
        // overload does not guarantee getting it, and the token is the only thing that says.
        var routeToken = PlaceField(place, route: true);
        var wantedRoute = viaAsync ? "asyncoverload" : "sync";
        if (place is not null && fresh)
        {
            var got = routeToken ?? "unreadable";
            Line("ROUTE ACTUALLY USED", got == wantedRoute
                ? $"{got} — as asked."
                : $"{got} — NOT the {wantedRoute} route this run asked for.");
            if (got != wantedRoute && viaAsync)
            {
                Cont("THIS RUN DID NOT MEASURE THE QUESTION. 'connector' means the bridge took");
                Cont("the off-chart route and timed RegisterOrderAsync instead, which is a");
                Cont("different call and says nothing about OpenOrderAsync. Place on the");
                Cont("chart's own instrument and portfolio — the ones the surface report's");
                Cont("security= and portfolio= tokens name — and run it again.");
            }
        }

        if (PlaceGapUs(place) is { } gapUs)
        {
            Line("ACK LATENCY", $"{gapUs} us  ({gapUs / 1000.0:0.0} ms)");
            // WHOSE LATENCY IS THIS, ACTUALLY. Every use of this number downstream turns on the
            // answer, and the output cannot tell: a simulated account with no broker attached is
            // answered by ATAS's own simulator, and one attached to a broker's demo is answered
            // over the wire. Those differ by orders of magnitude and the token looks identical.
            // Naming the ambiguity is the only honest thing this line can do about it.
            Cont("WHOSE LATENCY: this account's, whatever it is. An ATAS account with no broker");
            Cont("attached is answered by ATAS's own simulator, not by a venue — and a real one");
            Cont("can be materially slower. Use this to decide whether the two events are");
            Cont("SEPARABLE here. Do not carry it off this machine as a product characteristic.");
            if (gapUs >= 20_000 && !viaAsync)
            {
                Cont("SEPARABLE. Submission and acknowledgement are far enough apart on this");
                Cont("platform to tell them apart. A follow-up run that submits through");
                Cont("ITradingManager.OpenOrderAsync answers the real question: if its task");
                Cont("completes near call= it waits for SUBMISSION and the four obsolete call");
                Cont("sites can be flipped; if it completes near settled= it waits for");
                Cont("ACKNOWLEDGEMENT and flipping them puts Place past CallTimeout.");
                Cont("");
                Cont("    probe atas --place-test-order --yes --via-async-overload");
            }
            else if (gapUs >= 20_000)
            {
                Cont("SEPARABLE, and on this route that is the CONTROL rather than the reading:");
                Cont("call= above timed OpenOrderAsync, so gap= is what remained between its");
                Cont("task completing and the acknowledgement landing. A gap this size means");
                Cont("the two did not happen together. OPENORDERASYNC below draws the");
                Cont("conclusion; this line is the evidence it rests on.");
            }
            else
            {
                Cont("NOT SEPARABLE. This platform acknowledged in under 20 ms, so submission and");
                Cont("acknowledgement happen at effectively the same instant here. A fast");
                Cont("OpenOrderAsync completion would be consistent with BOTH answers and is");
                Cont("therefore no evidence at all. THE QUESTION CANNOT BE ANSWERED ON THIS");
                Cont("ACCOUNT. Do not flip the four call sites on the strength of it. Answering");
                Cont("it needs a venue whose acknowledgement is measurably slower than its");
                Cont("submission — a real broker, or a connection deliberately degraded.");
            }
        }

        // ---- and on a --via-async-overload run, the answer itself ----
        //
        // TWO WITNESSES, AND THEY ARE INDEPENDENT OF EACH OTHER.
        //
        //   TIMING. On this route call= times OpenOrderAsync. gap = settled - call, and settled is
        //     the same acknowledgement the ordinary route waits for. So a LARGE gap means the task
        //     finished long before the acknowledgement did — SUBMISSION. A gap near zero means the
        //     task and the acknowledgement finished together — ACKNOWLEDGEMENT.
        //   STATE. atreturn= is the order's state and id at the instant the call returned, read
        //     rather than timed. None/noid means nothing had come back yet, so the task completed
        //     on SUBMISSION; a state or an id already assigned means it waited for the broker.
        //
        // The timing witness alone is not conclusive and this says so: a gap near zero is also what
        // a platform that acknowledges instantly produces, whatever the task waited for. That is
        // what the ACK LATENCY reading from an ordinary place=sync run is the control for. The state
        // witness does not have that weakness — it is categorical — which is why both are printed
        // and why they are printed separately rather than reduced to one verdict.
        if (viaAsync && fresh && routeToken == "asyncoverload")
        {
            var callUs = PlaceUs(place, "call");
            var atReturn = PlaceField(place, "atreturn");
            Line("OPENORDERASYNC", "THE QUESTION THIS RUN EXISTS TO ANSWER: does its task complete on");
            Cont("SUBMISSION or on broker ACKNOWLEDGEMENT?");

            if (callUs is { } cu)
                Cont($"call={cu} us ({cu / 1000.0:0.0} ms) — this is OpenOrderAsync, not OpenOrder.");

            if (PlaceGapUs(place) is { } g && callUs is not null)
            {
                Line("READING — TIMING", g >= 20_000
                    ? "SUBMISSION. The task completed well before the acknowledgement did."
                    : "ACKNOWLEDGEMENT, OR AN INSTANT VENUE. The task and the acknowledgement");
                if (g >= 20_000)
                {
                    Cont("Blocking on it therefore costs about what the synchronous call costs,");
                    Cont("and AtasCall.Block can be given the four obsolete call sites.");
                }
                else
                {
                    Cont("finished together. That is what waiting for the broker looks like — but");
                    Cont("it is ALSO what an instantly-acknowledging venue looks like. It is only");
                    Cont("evidence against a place=sync run on this same account whose ACK LATENCY");
                    Cont("was well above 20 ms. Find that run before concluding anything.");
                }
            }

            Line("READING — STATE", atReturn is null
                ? "NOT REPORTED — the bridge did not record the order's shape at return."
                : atReturn.StartsWith("None/noid", StringComparison.Ordinal)
                    ? $"SUBMISSION. atreturn={atReturn} — the order had no state and no broker id"
                    : $"ACKNOWLEDGEMENT. atreturn={atReturn} — the order already carried a state");
            if (atReturn is not null)
                Cont(atReturn.StartsWith("None/noid", StringComparison.Ordinal)
                    ? "when the task completed, so nothing had come back from the broker yet."
                    : "or an id when the task completed, so it had waited for the broker.");
            Cont("This witness is categorical rather than a duration, so it does not need the");
            Cont("control run the timing one does. When the two disagree, that disagreement is");
            Cont("the finding — do not average them.");

            // WHICH HALF OF THIS TRANSFERS OFF THIS MACHINE, AND WHICH DOES NOT. The line above is
            // an API-semantics fact about ATAS and it travels: what OpenOrderAsync's task waits for
            // is a property of the platform, the same on any account. The DURATIONS do not travel.
            // They are whatever answered here, and an account with no broker attached is answered by
            // ATAS's own simulator — so a margin computed from them is a fact about this machine and
            // not a characteristic of the product. Saying so here rather than in a document is
            // deliberate: this output is what gets pasted into the decision.
            Line("WHAT TRANSFERS", "the ANSWER does; the DURATIONS do not.");
            Cont("\"OpenOrderAsync waits for submission\" (or for acknowledgement) is a fact about");
            Cont("ATAS's API and holds wherever this bridge runs. Every microsecond above is a");
            Cont("fact about THIS account on THIS machine. If there is no broker attached, they");
            Cont("are the simulator's latency, and a real venue can be materially slower.");
            Cont("Quote the answer. Do not quote the numbers as a property of the product.");

            Line("WHAT IT DOESN'T SETTLE", "the flip itself, and one tempting argument for it is");
            Cont("unsound. \"The acknowledgement lands far inside CallTimeout, so blocking on the");
            Cont("async call cannot turn orders into UNKNOWN\" is computed from the latency");
            Cont("measured above — so it is only ever true of the venue that produced it. Do not");
            Cont("carry that margin forward as a property of the product; it is not one.");
            Cont("");
            Cont("What IS structural: Place ALREADY waits for acknowledgement in");
            Cont("WaitFor(AckTimeout), on the same condition the async task would wait for. So");
            Cont("the switch moves where the time is spent rather than adding any, whatever the");
            Cont("venue. The real difference is what a SLOW acknowledgement does: today WaitFor");
            Cont("gives up and returns the order in whatever state it is really in, with no");
            Cont("exception; after the switch the same slowness raises AtasCallTimeoutException");
            Cont("and the gateway records UNKNOWN. Arguably more correct under rule 3, and still");
            Cont("a behaviour change on the money path. Its own change, its own reasoning.");
        }
        else if (viaAsync)
        {
            Line("OPENORDERASYNC", "NOT ANSWERED BY THIS RUN.");
            Cont(!fresh
                ? "The place= token is not this run's, so there is no reading to interpret."
                : $"The bridge reported route '{Blank(routeToken)}', not 'asyncoverload', so what");
            Cont(!fresh
                ? "See PLACE CALL and IS IT THIS RUN'S above for what happened instead."
                : "was timed is not OpenOrderAsync. See ROUTE ACTUALLY USED above.");
        }

        // Are ITradingManager.Orders and ChartStrategy.Orders the same list? The counts have always
        // been reported, but every capture ever taken read them at the HELLO — before anything was
        // placed — so `strategyorders=0` was consistent with both answers and settled nothing across
        // two sessions. The heartbeat waited on just above carries a fresh surface report, so this is
        // the first reading in this harness taken AFTER an order this strategy instance placed.
        //
        // It matters because LiveOrders() reads both collections and de-duplicates by reference: if
        // they are one list, a caller that SUMS per order would double-count a partial fill and read
        // it as FILLED.
        var ordersNow = Token(connector.Bridge?.TradingSurface, "orders");
        var strategyNow = Token(connector.Bridge?.TradingSurface, "strategyorders");
        Line("ORDER COLLECTIONS", $"before: orders={Blank(Token(surfaceBefore, "orders"))} " +
                                  $"strategyorders={Blank(Token(surfaceBefore, "strategyorders"))}   " +
                                  $"after: orders={Blank(ordersNow)} strategyorders={Blank(strategyNow)}");
        Cont(ordersNow is null || strategyNow is null
            ? "the bridge did not report both counts, so this run does not answer it."
            : ordersNow == strategyNow
                ? "the two counts AGREE. That is consistent with one shared list and with two lists"
                : "the two counts DIFFER, so ITradingManager.Orders and ChartStrategy.Orders are NOT");
        Cont(ordersNow is null || strategyNow is null
            ? "Re-run once the bridge has sent a heartbeat."
            : ordersNow == strategyNow
                ? "that happen to hold the same orders — it does not separate them on its own."
                : "the same collection. LiveOrders' de-duplication is defensive, not load-bearing.");

        var after = connector.Capabilities;
        Line("SupportsClientOrderId", $"{Yn(after.SupportsClientOrderId)}   AFTER the attempt — this is the reading that " +
                                      "counts.");

        // The adapter measures something this harness structurally cannot see: whether the order it
        // found in ATAS's collection is the SAME OBJECT it handed to ATAS. Place() constructs an
        // Order, sets Comment on it, and passes that instance in. If ATAS's Orders collection simply
        // contains that instance, then "the comment came back" is true by construction and the only
        // thing proven is that an Id was assigned. That is rule 1 being faked, which the rule
        // forbids by name — so the reading is printed next to the boolean it qualifies.
        var coid = Token(connector.Bridge?.TradingSurface, "coid");
        Line("ROUND TRIP, MEASURED", coid switch
        {
            "proven-crosssession" => "proven-crosssession — an identifier a PREVIOUS run of this "
                                   + "product recorded before submitting came back on an order in "
                                   + "ATAS's book, with the broker id that run recorded. It "
                                   + "outlived the process that made it. The strongest reading.",
            "proven-distinct" => "proven-distinct — a genuinely SEPARATE object came back carrying our "
                               + "identifier. This is the real round trip, within one session.",
            "proven-sameref"  => "proven-sameref — ATAS handed back AN OBJECT THIS ADAPTER TOUCHED. "
                               + "THE PROOF IS VACUOUS; see below.",
            "notfound"        => "notfound — a read-back ran and no order carried our id with a broker id.",
            "unchecked"       => "unchecked — submitted, but no read-back ever ran.",
            "unattempted"     => "unattempted — nothing carrying a client order id has been submitted.",
            null or ""        => "NOT REPORTED — this bridge predates the coid reading.",
            _                 => coid
        });

        // The witness record beside the reading it produced, so a run that reports a cross-session
        // proof also shows the file that made it one — and a run that reports nothing shows whether
        // there was anything on file to read.
        var witnessToken = Token(connector.Bridge?.TradingSurface, "witness");
        Line("WITNESS RECORD", witnessToken ?? "NOT REPORTED — this bridge predates the witness record");
        if (witnessToken is not null)
        {
            Cont("session: which run of the bridge is answering. records/prior: what is on");
            Cont("file, and how many of those a PREVIOUS run left acknowledged — the ones a");
            Cont("cross-session reading could be taken from. io: whether the file is writable.");
        }

        // ------------------------------------------------------------------------- what it proves

        Section("THE TEST ORDER — WHAT THIS PROVES");

        if (poll.Orders is null)
        {
            Line("RULE 1", "NOT ANSWERED — the order collection could not be read after the");
            Cont($"placement ({poll.Error}).");
            Cont("This says nothing about ATAS and nothing about the identifier. It is the");
            Cont("read that failed, not the round trip. Do not record it as either.");
            return ("read-failed", everSeen);
        }

        if (found is null)
        {
            if (rejected)
            {
                Line("RULE 1", "NOT ANSWERED — the order was definitely refused, so no round trip");
                Cont("ever happened and there was nothing to read back. Nothing is live at");
                Cont("the broker. The refusal itself is the finding: read the PLACE CALL");
                Cont($"message above. If it names a price band or a price limit, the {Pct(FarBelowBid)}");
                Cont("offset is further from the market than this venue accepts, and");
                Cont("FarBelowBid in this file is the one line to change. Any other message");
                Cont("is about the order, the account or the instrument — and either way");
                Cont("rule 1 is still unmeasured.");
                return ("place-rejected", everSeen);
            }

            Line("RULE 1", "UNKNOWN — AND UNKNOWN IS NOT A FAILURE.");
            Cont("The placement was not refused, yet nothing carrying this run's client");
            Cont("order id, and nothing carrying the connector id the place call returned,");
            Cont("is in ATAS's collection. The order may have reached the broker and be");
            Cont("invisible to the collection this bridge can read; it may never have");
            Cont("landed. Those are different facts and this run cannot separate them.");
            Cont("Record UNKNOWN — and read the cleanup section below before walking away.");
            return ("unknown", everSeen);
        }

        if (byClientId is not { Count: > 0 })
        {
            Line("RULE 1", "NOT SATISFIED — AND THIS IS GENUINE EVIDENCE ABOUT ATAS.");
            Cont("The order IS in ATAS's own order collection — it was matched by the");
            Cont("connector order id the place call returned — and it does NOT carry the");
            Cont("client order id TradeAgent submitted with it. That is the round trip");
            Cont("failing, observed, not inferred from an empty book.");
            Cont("");
            Cont("What follows from it: SupportsClientOrderId must stay false, the gateway");
            Cont("must go on refusing LIVE_AUTONOMOUS on this connection, and THIS PRODUCT");
            Cont("CANNOT TRADE UNATTENDED ON THIS BACKEND. After a dropped connection there");
            Cont("is no identifier to reconcile by, so 'did my order land' has no answer.");
            Cont("Do not repair this by hard-coding the boolean true — that would not make");
            Cont("the state provable, only make the gateway believe it is.");
            Cont("");
            Cont("Before recording it as final: confirm it is repeatable, and check the raw");
            Cont("order above for where the identifier went. The bridge writes it to");
            Cont("Order.Comment, which is the only client-settable string on an ATAS order.");
            return ("no-client-id", everSeen);
        }

        if (!BrokerAssigned(broker))
        {
            Line("RULE 1", "NOT YET SATISFIED — THE ROUND TRIP IS INCOMPLETE, NOT FAILED.");
            Cont("The client order id came back off ATAS's own collection, so the");
            Cont("identifier reaches ATAS and survives being stored. What is missing is a");
            Cont("broker-assigned order id on the same order, and the bridge requires both");
            Cont("before it will report true.");
            Cont("");
            Cont("Two different futures: the broker has not acknowledged yet, in which case");
            Cont("re-running this in a moment answers it — or it never assigns an id ATAS");
            Cont("surfaces, in which case false is the permanent and correct answer. This");
            Cont("run does not distinguish them. Do not record either as settled.");
            return ("no-broker-id", everSeen);
        }

        // THE ORDER OF THE THREE GUARDS BELOW IS THE WHOLE OF THEIR CORRECTNESS, AND IT WAS WRONG.
        //
        // Until 2026-08-30 the `!after.SupportsClientOrderId` disagreement branch stood FIRST, so on
        // real ATAS — where the reading is proven-sameref and the boolean is therefore correctly
        // false — this printed "THE EVIDENCE IS PRESENT AND THE BRIDGE STILL SAYS false —
        // INVESTIGATE" and accused the bridge of a defect for behaving exactly as designed. The
        // sameref branch written for precisely that run was unreachable on it. Measured on
        // BTCUSDT / CRYPTO5EB41, 2026-08-30.
        //
        // The cause is that "the book shows both ids and the bridge says false" STOPPED being a
        // contradiction when a same-reference match stopped setting the capability: from out here
        // the pair looks complete, and only the bridge can see that the order carrying it is its own
        // object. So the coid= reading — the better-informed source — has to be consulted before any
        // verdict is drawn from the boolean, and the disagreement branch keeps only the case it was
        // actually written for: evidence present, bridge says false, and coid explains nothing.
        if (coid == "proven-crosssession")
        {
            Line("RULE 1", "SATISFIED ACROSS A PROCESS RESTART — THE STRONGEST READING THERE IS.");
            Cont($"'{clientOrderId}' came back off ATAS's collection with the broker id {broker},");
            Cont("and the bridge reports the identifier it matched was recorded by an EARLIER");
            Cont("run of this product — written down before that order existed, by a process");
            Cont("that had ended by the time this one read it. That cannot be our own object:");
            Cont("our own objects do not survive the process that made them.");
            Cont("");
            Cont("SupportsClientOrderId reads true from this and it SHOULD be believed. Note");
            Cont("this is a surprising reading for a --place-test-order run, which submits in");
            Cont("this session — it means the match was taken against a record from a previous");
            Cont("one. Run --coid-restart-check for the reading in its proper setting.");
            return ("proven-crosssession", everSeen);
        }

        if (coid == "proven-sameref")
        {
            Line("RULE 1", "NOT SATISFIED — THE MATCH IS REAL AND IT PROVES NOTHING.");
            Cont($"'{clientOrderId}' did come back off ATAS's collection with the broker id");
            Cont($"{broker}. But the adapter reports the matched order is an object THIS ADAPTER");
            Cont("TOUCHED — the Order instance it constructed, set Comment on, and handed to");
            Cont("ATAS, or a clone of one. So the comment came back because it never left: the");
            Cont("collection is holding our own object. The only thing actually observed is");
            Cont("that ATAS assigned an Order.Id.");
            Cont("");
            Cont("SupportsClientOrderId reads FALSE from this, and that is the bridge being");
            Cont("correct rather than a contradiction with the evidence above. From out here");
            Cont("the pair of ids looks like exactly what rule 1 asks for; the bridge can see");
            Cont("one thing this harness structurally cannot, which is whose object carried");
            Cont("them. Believe the bridge. Do not 'fix' this by trusting the pair.");
            Cont("");
            Cont("THIS IS THE EXPECTED READING ON THIS PLATFORM. It is not a defect and it is");
            Cont("not a round-trip failure. What would settle rule 1: a source that cannot be");
            Cont("our own object — which is what --leave-resting plus --coid-restart-check");
            Cont("across an ATAS restart is for.");
            return ("proven-sameref", everSeen);
        }

        if (!after.SupportsClientOrderId)
        {
            Line("RULE 1", "THE EVIDENCE IS PRESENT AND THE BRIDGE STILL SAYS false — INVESTIGATE.");
            Cont("The order in ATAS's collection carries BOTH this run's client order id");
            Cont("and a broker-assigned order id, which is exactly the pair the bridge");
            Cont("says it needs, and SupportsClientOrderId is still false — with the coid");
            Cont($"reading ({Blank(coid)}) explaining neither. A same-reference match WOULD");
            Cont("explain it and is handled above, so this is not that. It is the two");
            Cont("sources disagreeing, and the disagreement IS the finding. Believe");
            Cont("neither until it is explained, and do not go near autonomous trading on");
            Cont("this connection meanwhile.");
            return ("disagreement", everSeen);
        }

        Line("RULE 1", "SATISFIED, AND MEASURED HERE FOR THE FIRST TIME.");
        Cont($"TradeAgent submitted '{clientOrderId}', and that identifier came back off");
        Cont($"ATAS's own order collection alongside the broker-assigned id {broker},");
        Cont("on an order this run placed, ON AN OBJECT THE ADAPTER NEVER TOUCHED. That");
        Cont("is what rule 1 asks for. It was observed, not assumed, and the bridge");
        Cont("reports SupportsClientOrderId = true from the same observation rather than");
        Cont("from this harness.");
        Cont("");
        Cont("What it still does NOT prove: that the identifier survives ATAS itself");
        Cont("being restarted. This reading is taken within one session, where the");
        Cont("adapter's own objects are the thing being ruled out; it says nothing about");
        Cont("what happens when there are no such objects left. --leave-resting plus");
        Cont("--coid-restart-check is the reading that answers that. And it says nothing");
        Cont("about order history — ReconciliationProvable needs both halves, and the");
        Cont("other half is reported above.");
        return ("round-trip-proven", everSeen);
    }

    /// <summary>
    /// The cancel, and then the only thing that settles whether it took: another read.
    ///
    /// This runs on every path, including the ones where the placement threw and the ones where
    /// nothing came back. "The read failed" is not evidence that nothing landed, and an order that
    /// was placed but never seen is exactly the one that must not be left resting.
    /// </summary>
    /// <param name="standalone">
    /// True for <c>--cancel-resting</c>, where this run placed nothing and is removing an order an
    /// EARLIER run left behind. It changes exactly one branch — the final one — and it changes it
    /// because the reasoning there does not hold for this case. In the place path, "nothing
    /// carrying this id is on the book" is hedged, because the order may have landed somewhere the
    /// bridge cannot see and never became visible. Here it never had to become visible in this run
    /// at all: the target was confirmed resting by the run that placed it, read out of this same
    /// collection, and it is not in it now. Reporting exit 4 for that would cry wolf on the ordinary
    /// success of a cleanup verb, and the file's own note on STILL-RESTING says why that is the one
    /// thing not to do with a banner that must never be ignored.
    /// </param>
    static async Task<TestOrderOutcome> CleanUp(AtasConnector connector, string clientOrderId,
                                                OrderInfo? placed, bool rejected, bool everSeen,
                                                bool standalone = false)
    {
        Section(standalone ? "CANCELLING A RESTING ORDER" : "THE TEST ORDER — CLEANING UP");
        Line("WHY THIS ALWAYS RUNS", "a resting order left behind is the one outcome nobody should discover");
        Cont("later, from ATAS, on another day. So the cancel is attempted even when");
        Cont("the placement was refused, even when it threw, and even when the");
        Cont("read-back found nothing at all to cancel.");

        var placedId = placed is { ConnectorOrderId.Length: > 0 } ? placed.ConnectorOrderId : null;
        var target = placedId ?? clientOrderId;
        Line("CANCELLING", target);
        if (placedId is null)
        {
            Cont("— the CLIENT order id, because the place call returned no connector id.");
            Cont("The bridge resolves a cancel by client order id as well: it keeps the");
            Cont("orders it submitted keyed by the id it submitted them with, and it does");
            Cont("so BEFORE handing anything to ATAS. That is what makes cleanup possible");
            Cont("after a placement that told us nothing at all.");
        }
        else Cont("— the connector order id the place call returned.");

        var sent = false;
        string? lastError = null;
        var poll = default(OrderPoll);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await connector.CancelOrderAsync(target);
                sent = true;
                lastError = null;
                Line($"CANCEL {attempt}", "SENT — the bridge returned without an error.");
            }
            catch (ConnectorRejectedException ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                Line($"CANCEL {attempt}", $"DEFINITELY REFUSED — {ex.Message}");
                Cont("Usually 'this order is not cancellable' — already finished, or never");
                Cont("known to ATAS at all. Neither is taken as proof here: the re-read");
                Cont("below is what settles whether anything is still on the book.");
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                Line($"CANCEL {attempt}", $"FAILED, OUTCOME UNKNOWN — {lastError}");
                Cont("The cancel may or may not have reached ATAS. Same as above: the");
                Cont("re-read decides, not this.");
            }

            poll = await PollOrders(connector, list => !list.Any(o => Mine(o, clientOrderId) && OrderStateMachine.IsLive(o.State)), CleanupLimit);
            var stillResting = poll.Orders?.Where(o => Mine(o, clientOrderId) && OrderStateMachine.IsLive(o.State)).ToList();
            if (stillResting is not { Count: > 0 } || attempt == 2) break;

            // One more go. Where ATAS has since assigned an id, use it: the first cancel may have
            // gone out before the order had a broker id to be cancelled by, which is a real and
            // recoverable reason for it not to have taken. Where it has not, retry the same id
            // anyway — an order that was not cancellable a moment ago can be cancellable now, and a
            // redundant cancel is harmless in a way that a resting order is not. Cancelling twice
            // cannot fill anything; that asymmetry is the whole argument for trying again.
            var better = stillResting[0].ConnectorOrderId;
            if (!string.IsNullOrEmpty(better) && better != target)
            {
                Line("RETRYING", $"still resting. Trying again with the id it now carries: {better}");
                target = better;
            }
            else Line("RETRYING", "still resting, and no newer id to try. Sending the cancel again.");
        }

        var book = poll.Orders;
        if (book is null)
        {
            Line("BOOK RE-READ", $"COULD NOT BE READ — {poll.Error}");
            return Loud("CLEANUP-NOT-CONFIRMED", "THE CANCEL COULD NOT BE CONFIRMED — THE BOOK COULD NOT BE RE-READ.",
                $"The cancel was {(sent ? "sent without error" : "not accepted")}" +
                $"{(lastError is null ? "" : $" ({lastError})")}, and then the order",
                "collection could not be read to check it. Whether anything of this run's is",
                "still resting is UNKNOWN. Open ATAS and look at the order book for an order",
                $"whose comment is '{clientOrderId}' before doing anything else.");
        }

        var mine = book.Where(o => Mine(o, clientOrderId)).ToList();
        Line("BOOK RE-READ", $"{book.Count} order(s) in the collection, {mine.Count} carrying this run's id" +
                             $"   (read {poll.Reads} time(s) over {poll.Took.TotalSeconds:0.0}s)");
        foreach (var o in mine)
            Cont($"  {o.State}  filled={Num(o.FilledQuantity)}/{Num(o.Quantity)}  id={Blank(o.ConnectorOrderId)}");

        // Resting means THE BROKER MAY STILL ACT ON OUR BEHALF, which is IsLive — not merely
        // "not terminal". UNKNOWN is non-terminal (it leaves only through RECONCILING), so
        // !Terminal() called a finished-but-undescribed order "still on the book" and sent the
        // operator hunting in ATAS for an order that is not there. That matters more than it
        // sounds: the adapter now reports Done-with-no-fill-evidence as UNKNOWN rather than
        // inventing a CANCELLED, so this is the ORDINARY outcome of a successful cancel, and
        // crying STILL-RESTING over it would train everyone to ignore the one banner that must
        // never be ignored.
        var resting = mine.Where(o => OrderStateMachine.IsLive(o.State)).ToList();
        if (resting.Count > 0)
            return Loud("STILL-RESTING", "AN ORDER FROM THIS RUN IS STILL ON THE BOOK.",
                $"{resting.Count} order(s) carrying '{clientOrderId}' are in a non-terminal state" ,
                $"after {(sent ? "a cancel that returned without error" : "the cancel could not be sent")}:",
                string.Join(", ", resting.Select(o => $"{o.ConnectorOrderId} {o.State}")),
                "",
                "CANCEL IT BY HAND IN ATAS NOW. It is a simulated account and a resting buy",
                "limit far below the market, so it is not going to do anything — but it is",
                "there, this run put it there, and nothing else is going to remove it.");

        var filled = mine.Where(o => o.State == ExecutionState.FILLED || o.FilledQuantity > 0m).ToList();
        if (filled.Count > 0)
            return Loud("FILLED", "THE TEST ORDER FILLED. IT WAS PRICED SO THAT IT COULD NOT.",
                string.Join(", ", filled.Select(o => $"{o.ConnectorOrderId} {o.State} filled={Num(o.FilledQuantity)}")),
                "",
                "There is now a position on this account that this run opened. Flatten it in",
                "ATAS. Then find out why: a buy limit placed a long way below the bid does not",
                $"fill unless the price this run derived was wrong ({Pct(FarBelowBid)} below the quoted bid),",
                "or the quote it derived it from did not describe the market. Either one",
                "matters far more than the client-order-id reading above, and neither should",
                "be left unexplained before anything else is placed.");

        // ATAS says finished and described no outcome. Almost certainly the cancel, since a buy
        // limit far below the bid does not fill — but "almost certainly" is not what this file
        // reports. Separated from CONFIRMED so nobody reads a guess as an observation, and from
        // STILL-RESTING so nobody goes looking for an order that is not on the book.
        var undescribed = mine.Where(o => o.State == ExecutionState.UNKNOWN).ToList();
        if (undescribed.Count > 0)
        {
            Line("CLEANUP VERDICT", "FINISHED, OUTCOME NOT DESCRIBED — nothing is resting, and nothing proves why.");
            Cont($"{undescribed.Count} order(s) carrying '{clientOrderId}' are finished in ATAS's own");
            Cont("collection, and ATAS reported no fill evidence for them — no MyTrade, and no");
            Cont("Unfilled it had written. The adapter therefore reports UNKNOWN rather than");
            Cont("inventing a CANCELLED, because asserting a cancellation from silence is the");
            Cont("same mistake as asserting a fill from silence.");
            Cont("");
            Cont("Nothing from this run can still be acted on by the broker, so nothing needs");
            Cont("cancelling by hand. What is NOT settled is whether it ended cancelled or");
            Cont("filled-but-unreported. Look at the account's position and Trading Journal in");
            Cont($"ATAS for '{clientOrderId}' before placing anything else, and if it ended");
            Cont("cancelled, Order.Canceled is the field that would let this be reported as");
            Cont("evidence rather than as this paragraph.");
            return new TestOrderOutcome(4, "cleanup-undescribed", Placed: true);
        }

        if (mine.Count > 0)
        {
            Line("CLEANUP VERDICT", $"CONFIRMED — the order is finished: {string.Join(", ", mine.Select(o => o.State.ToString()))}.");
            Cont("Observed in ATAS's own collection after the cancel, not inferred from the");
            Cont("cancel returning without an error. Nothing from this run is resting.");
            return new TestOrderOutcome(0, "cancelled", Placed: true);
        }

        if (rejected && !everSeen)
        {
            Line("CLEANUP VERDICT", "CONFIRMED — nothing was ever submitted, so there was nothing to cancel.");
            Cont("The placement was definitively refused and nothing carrying this run's id");
            Cont("has been in the collection at any point. The cancel was attempted anyway.");
            return new TestOrderOutcome(0, "nothing-placed", Placed: true);
        }

        if (everSeen)
        {
            Line("CLEANUP VERDICT", "CONFIRMED, THOUGH WEAKLY — nothing of this run's is on the book now.");
            Cont("The order was visible in the collection before the cancel and is not");
            Cont("visible now. That is consistent with the cancel having taken and ATAS");
            Cont("having dropped the finished order from the live collection — but it is");
            Cont("NOT the same as observing it in CANCELLED, and this run cannot tell the");
            Cont("two apart. What it does say is the thing that matters: nothing carrying");
            Cont("this run's identifier is resting.");
            return new TestOrderOutcome(0, "gone", Placed: true);
        }

        if (standalone)
        {
            Line("CLEANUP VERDICT", $"CONFIRMED — nothing carrying '{clientOrderId}' is on the book.");
            Cont("The cancel was sent and ATAS's live order collection — the same collection");
            Cont("the run that placed it read it out of — no longer holds anything under that");
            Cont("identifier. Nothing from it can still be acted on by the broker.");
            Cont("");
            Cont("What this does NOT say: whether it ended cancelled by this run, or had");
            Cont("already gone before this run started. Both look identical from here and");
            Cont("neither leaves anything resting, which is the question this verb answers.");
            return new TestOrderOutcome(0, "gone", Placed: false);
        }

        return Loud("CLEANUP-NOT-CONFIRMED", "THE CANCEL COULD NOT BE CONFIRMED — NOTHING OF THIS RUN'S WAS EVER VISIBLE.",
            "The placement was not definitively refused, so it may have reached the",
            "broker; and nothing carrying this run's client order id has appeared in",
            "ATAS's collection at any point, before or after the cancel. So the book",
            "cannot say whether an order is resting — the same empty reading is produced",
            "by 'it never landed' and by 'it landed somewhere this bridge cannot see'.",
            "",
            $"Open ATAS and look for an order whose comment is '{clientOrderId}'. It is a",
            "Day order, so it expires with the session even if it is missed — that is a",
            "backstop, not a reason to skip looking.");
    }

    // ------------------------------------------------------------------------- test-order pieces

    /// <summary>
    /// <c>--cancel-resting</c> with no live connection. Deliberately NOT <see cref="Refuse"/>, whose
    /// closing line is "nothing was placed, so there is nothing resting on the book as a result of
    /// this run" — true here and exactly the wrong thing to leave a reader with, because the whole
    /// reason to run this verb is that something IS resting from an earlier one.
    /// </summary>
    static TestOrderOutcome NoCancel(string clientOrderId)
    {
        Section("CANCELLING A RESTING ORDER");
        Line("NOTHING WAS SENT", "THE CONNECTOR HANDSHAKE NEVER COMPLETED.");
        Cont("There is no live connection to send a cancel over, so no cancel was");
        Cont($"attempted. WHATEVER IS RESTING IS STILL RESTING — this run changed nothing.");
        Cont("");
        Cont("Get the bridge answering and run this again, or cancel it by hand in ATAS:");
        Cont($"look for an order whose comment is '{clientOrderId}'.");
        return new TestOrderOutcome(4, "not-sent", Placed: false);
    }

    /// <summary>Prints a refusal that names its reason, and returns the exit code for one.</summary>
    static TestOrderOutcome Refuse(string headline, params string[] why)
    {
        Line("REFUSED TO PLACE", headline);
        foreach (var l in why) Cont(l);
        Cont("");
        Cont("NOTHING WAS SUBMITTED. No order was placed, so there is nothing resting on");
        Cont("the book as a result of this run and nothing to cancel.");
        return new TestOrderOutcome(3, "refused", Placed: false);
    }

    /// <summary>A cleanup outcome nobody should be able to scroll past.</summary>
    static TestOrderOutcome Loud(string summary, string headline, params string[] lines)
    {
        Console.WriteLine();
        Console.WriteLine(new string('!', 80));
        Line("CLEANUP VERDICT", headline);
        foreach (var l in lines) Cont(l);
        Console.WriteLine(new string('!', 80));
        return new TestOrderOutcome(4, summary, Placed: true);
    }

    /// <summary>
    /// Re-reads ATAS's live order collection until <paramref name="until"/> is satisfied or the time
    /// runs out, and reports how hard it looked.
    ///
    /// account_id="" for the same reason the section above uses it: a blank account id means "every
    /// account" AND stops the adapter consulting the history cache, so what comes back is exactly
    /// ATAS's live order collection — the collection that can settle rule 1.
    /// </summary>
    static async Task<OrderPoll> PollOrders(AtasConnector connector, Func<IReadOnlyList<OrderInfo>, bool> until, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<OrderInfo>? last = null;
        string? error = null;
        var reads = 0;
        while (true)
        {
            try { last = await connector.GetOrdersAsync("", includeInactive: true, since: null); error = null; }
            catch (Exception ex) { last = null; error = $"{ex.GetType().Name}: {ex.Message}"; }
            reads++;
            if (last is not null && until(last)) break;
            if (sw.Elapsed >= limit) break;
            await Task.Delay(500);
        }
        return new OrderPoll(last, error, reads, sw.Elapsed);
    }

    static bool Mine(OrderInfo o, string clientOrderId) =>
        string.Equals(o.ClientOrderId, clientOrderId, StringComparison.Ordinal);

    static bool Terminal(OrderInfo o) => OrderStateMachine.IsTerminal(o.State);

    /// <summary>
    /// Pulls one "key=value" out of the bridge's space-joined trading_surface string.
    ///
    /// The field is deliberately a flat, greppable line rather than structured JSON, because its
    /// whole job is to be readable in a terminal next to the boolean it explains. That makes this
    /// parse the price of admission. Returns null when the token is absent, which is a real case:
    /// a bridge older than the token reports nothing rather than reporting a default, and those
    /// must not read the same.
    /// </summary>
    /// <summary>
    /// The `gap=` microseconds out of the bridge's `place=` token, or null when it is absent or
    /// unreadable. Parsed with InvariantCulture on purpose: the machine this runs on formats numbers
    /// with a comma, and a silently mis-parsed latency is worse than an absent one.
    /// </summary>
    static long? PlaceGapUs(string? place) => PlaceUs(place, "gap");

    /// <summary>
    /// One microsecond field out of the `place=` token, or null when it is absent or unreadable.
    ///
    /// Parsed with InvariantCulture on purpose: the machine this runs on formats numbers with a
    /// comma, and a silently mis-parsed latency is worse than an absent one. The bridge writes these
    /// as integers for the same reason — a comma would be indistinguishable from a separator.
    /// </summary>
    static long? PlaceUs(string? place, string key)
    {
        if (PlaceField(place, key) is not { } raw) return null;
        var digits = raw.TrimEnd('u', 's');
        return long.TryParse(digits, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// One field out of the bridge's semicolon-joined `place=` token, as text.
    ///
    /// The ROUTE is the exception and is why <paramref name="route"/> exists: it is the FIRST field
    /// and it is a bare value with no `key=` in front of it, because it is what every other field in
    /// the token has to be read against. `sync`, `connector` and `asyncoverload` are three different
    /// platform calls, and a `call=` reading is only comparable with another taken through the same
    /// one — so a caller that reads a duration without reading this first is reading a number whose
    /// meaning it does not know.
    /// </summary>
    static string? PlaceField(string? place, string key = "", bool route = false)
    {
        if (string.IsNullOrWhiteSpace(place)) return null;
        var parts = place.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (route) return parts.Length > 0 && !parts[0].Contains('=') ? parts[0] : null;
        foreach (var part in parts)
            if (part.Length > key.Length + 1 && part[key.Length] == '=' && part.AsSpan(0, key.Length).SequenceEqual(key))
                return part[(key.Length + 1)..];
        return null;
    }

    /// <summary>The last few lines of a file, clipped, for printing. Never throws.</summary>
    static string[] ReadTail(string path, int lines)
    {
        try
        {
            var all = File.ReadAllLines(path);
            return all.Skip(Math.Max(0, all.Length - lines))
                      .Select(l => l.Length > 200 ? l[..200] + "…" : l)
                      .ToArray();
        }
        catch (Exception e) { return [$"(could not be read: {e.GetType().Name})"]; }
    }

    static string? Token(string? surface, string key)
    {
        if (string.IsNullOrWhiteSpace(surface)) return null;
        foreach (var part in surface.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length > key.Length + 1 && part[key.Length] == '=' && part.AsSpan(0, key.Length).SequenceEqual(key))
                return part[(key.Length + 1)..];
        return null;
    }

    /// <summary>
    /// Whether an id is one the BROKER assigned, as opposed to one the adapter made up.
    ///
    /// OrderKey falls back to "ext:&lt;ExtId&gt;" when ATAS has not set Order.Id yet, so an id with
    /// that prefix is the bridge's own synthetic handle. It must not be read as a broker-assigned
    /// id: the bridge's own read-back requires a non-empty Order.Id, so an order carrying only an
    /// "ext:" handle cannot satisfy rule 1 — and reporting it as though it could would fake exactly
    /// the proof rule 1 says not to fake.
    /// </summary>
    static bool BrokerAssigned(string? connectorOrderId) =>
        !string.IsNullOrEmpty(connectorOrderId) && !connectorOrderId.StartsWith("ext:", StringComparison.Ordinal);

    static string Counter(int? before, int? after) =>
        after is null ? "NOT REPORTED — this bridge predates the attempt counters"
        : before is null ? $"{after}   (nothing to compare against: no reading before the order)"
        : after == before ? $"{after}   UNCHANGED from before the order"
        : $"{after}   (was {before} before the order, +{after - before})";

    /// <summary>Rounds DOWN to a tick, never to nearest: every rounding error must move a resting buy
    /// further from the market rather than closer to it.</summary>
    static decimal SnapDown(decimal price, decimal tick) =>
        tick <= 0m ? price : Math.Floor(price / tick) * tick;

    static string Num(decimal d) => d.ToString("0.##########", CultureInfo.InvariantCulture);
    static string Pct(decimal fraction) => $"{fraction * 100m:0.##}%";

    static void Usage(string problem)
    {
        Console.WriteLine("usage: probe atas [--wait <seconds>] [--wait-anyway] [--place-test-order --yes]");
        Console.WriteLine("                  [--leave-resting --yes-leave-it] [--via-async-overload]");
        Console.WriteLine("                  [--coid-restart-check] [--cancel-resting <client-order-id>]");
        Console.WriteLine($"  {problem}");
        Console.WriteLine("  --wait <seconds>      how long to wait for the bridge to dial in (default 60)");
        Console.WriteLine("  --wait-anyway         wait for the pipe even though ATAS was not detected;");
        Console.WriteLine("                        only useful when driving the pipe with a stand-in bridge");
        Console.WriteLine("  --place-test-order    PLACES A REAL ORDER: one buy limit, quantity 1, on the");
        Console.WriteLine("                        chart's own instrument, priced far below the live bid so");
        Console.WriteLine("                        that it rests and cannot fill. It is read back and then");
        Console.WriteLine("                        cancelled. Refuses to submit anything unless the account");
        Console.WriteLine("                        is provably simulated; needs --yes as a second, separate");
        Console.WriteLine("                        act. This is how rule 1 gets measured instead of guessed.");
        Console.WriteLine("  --yes                 authorises --place-test-order. Does nothing on its own.");
        Console.WriteLine();
        Console.WriteLine("  THE RESTART EXPERIMENT — the only thing that can settle rule 1, in two halves.");
        Console.WriteLine("  An in-session read-back can only ever show that ATAS carries our identifier on");
        Console.WriteLine("  an object we did not build. It cannot show the identifier surviving the process");
        Console.WriteLine("  that submitted it, because our objects do not survive it either.");
        Console.WriteLine();
        Console.WriteLine("  --leave-resting       with --place-test-order: do NOT cancel the order at the");
        Console.WriteLine("                        end. It is left live on the account so that it is still");
        Console.WriteLine("                        there after ATAS is restarted. Needs --yes-leave-it as a");
        Console.WriteLine("                        second, separate act, exactly as --place-test-order needs");
        Console.WriteLine("                        --yes. Exits 5, and prints the command that removes it.");
        Console.WriteLine("  --yes-leave-it        authorises --leave-resting. Does nothing on its own.");
        Console.WriteLine("  --coid-restart-check  HALF 2. PLACES NOTHING — that is the measurement, not the");
        Console.WriteLine("                        caution: the cross-session reading is only available for");
        Console.WriteLine("                        an identifier the running bridge session did not submit.");
        Console.WriteLine("                        Reads the durable witness record against the live book and");
        Console.WriteLine("                        reports proof, disproof, or not-answered. Cannot be");
        Console.WriteLine("                        combined with --place-test-order.");
        Console.WriteLine("  --via-async-overload  with --place-test-order: submit through ATAS's");
        Console.WriteLine("                        ITradingManager.OpenOrderAsync instead of the obsolete");
        Console.WriteLine("                        synchronous OpenOrder the product uses, so that the");
        Console.WriteLine("                        completion point of that task can be timed. Same order,");
        Console.WriteLine("                        same simulated-account guard, same cleanup. One flag and");
        Console.WriteLine("                        not two, unlike --leave-resting: it removes no safeguard");
        Console.WriteLine("                        and changes no exposure — only which overload submits.");
        Console.WriteLine("                        Read PLACE TIMING and OPENORDERASYNC in the output; it");
        Console.WriteLine("                        needs an ordinary place=sync run on the same account as");
        Console.WriteLine("                        its control, because a fast completion on an instantly-");
        Console.WriteLine("                        acknowledging venue is evidence for neither answer.");
        Console.WriteLine("  --cancel-resting <id> cancels the order carrying that client order id and");
        Console.WriteLine("                        re-reads the book to confirm. One flag, not two: it only");
        Console.WriteLine("                        ever removes exposure and it names its target.");
        Console.WriteLine();
        Console.WriteLine("      probe atas --place-test-order --yes --leave-resting --yes-leave-it");
        Console.WriteLine("      # restart ATAS, re-activate the bridge strategy on the chart");
        Console.WriteLine("      probe atas --coid-restart-check");
        Console.WriteLine();
        Console.WriteLine("  THE OpenOrderAsync MEASUREMENT — the control run first, then the reading.");
        Console.WriteLine("      probe atas --place-test-order --yes                       # place=sync");
        Console.WriteLine("      probe atas --place-test-order --yes --via-async-overload  # place=asyncoverload");
    }

    // ------------------------------------------------------------------------------------ pieces

    /// <summary>
    /// What the bridge itself says about rule 1, or null when it does not keep the count.
    ///
    /// Null is a real answer here and must stay distinguishable from zero: a bridge that reports
    /// nothing has not told us it attempted nothing. Returning "0 attempts" for a silent bridge
    /// would reintroduce, one field lower, the exact ambiguity these counters were added to remove.
    /// </summary>
    static IEnumerable<string>? ReportedClientIdVerdict(bool proven, int? attempts, int? checks, string? coid)
    {
        if (attempts is null || checks is null) return null;
        return Lines(proven, attempts.Value, checks.Value, coid);

        static IEnumerable<string> Lines(bool proven, int attempts, int checks, string? coid)
        {
            if (proven && coid == "proven-crosssession")
            {
                yield return "PROVEN ACROSS A PROCESS RESTART, AND THE BRIDGE SAYS SO ITSELF.";
                yield return "It found an identifier a PREVIOUS run of this product recorded before";
                yield return "submitting, on an order in ATAS's own collection, carrying the broker id";
                yield return "that run recorded. The identifier outlived the process that made it, which";
                yield return "is the reading reconciliation after a dropped pipe actually rests on.";
                yield return "";
                yield return "The bound that remains: it cannot separate ATAS rebuilding the order from";
                yield return "the BROKER'S answer on reconnect from ATAS rehydrating it out of its own";
                yield return "store. All three look identical from inside a chart strategy.";
                yield break;
            }

            if (proven)
            {
                yield return "PROVEN, AND THE BRIDGE SAYS SO ITSELF.";
                yield return $"It submitted {attempts} order(s) carrying a client order id, performed";
                yield return $"{checks} read-back(s) against ATAS's own order collection, and one of them";
                yield return "found the identifier alongside a broker-assigned id, ON AN OBJECT IT NEVER";
                yield return "TOUCHED. Rule 1 is satisfied by observation, within this session. It does";
                yield return "not yet prove the id survives an ATAS restart — --leave-resting plus";
                yield return "--coid-restart-check is the reading that answers that.";
                yield break;
            }

            if (attempts == 0)
            {
                yield return "false BECAUSE NOTHING WAS EVER ATTEMPTED. This says nothing about ATAS.";
                yield return "The bridge has submitted no order carrying a client order id this session,";
                yield return "so the round trip has not been tried, let alone failed. This is the";
                yield return "EXPECTED reading on a fresh connection.";
                yield return "";
                yield return "To get an answer: place one order through TradeAgent on this connection —";
                yield return "paper first — and run this verb again.";
                yield break;
            }

            if (checks == 0)
            {
                yield return "false, ATTEMPTED BUT NEVER CHECKED — the round trip has not failed either.";
                yield return $"{attempts} order(s) went out carrying a client order id, but the bridge has";
                yield return "not once been able to look one up in ATAS's order collection. Nothing came";
                yield return "back to examine: the orders may not have reached the book, or ATAS has not";
                yield return "raised the events that trigger the read-back. Investigate the orders";
                yield return "themselves before drawing any conclusion about the identifier.";
                yield break;
            }

            if (coid == "proven-sameref")
            {
                yield return "false, AND THE READ-BACK FOUND THE PAIR — ON OUR OWN OBJECT. Not a failure.";
                yield return $"{attempts} order(s) went out carrying a client order id and the bridge";
                yield return $"performed {checks} read-back(s), which DID find the identifier alongside a";
                yield return "broker-assigned id. The bridge reports false anyway, and that is it being";
                yield return "CORRECT: the order it matched is reference-equal to the Order instance it";
                yield return "constructed and handed to ATAS, so the comment came back because it never";
                yield return "left. Nothing was carried anywhere, so nothing was proven.";
                yield return "";
                yield return "This is the EXPECTED reading on this platform. It is not a defect and it";
                yield return "is not a round-trip failure — do not 'fix' it by trusting the boolean.";
                yield return "What would settle rule 1: a source that cannot be our own object — a fresh";
                yield return "ATAS session, the platform's order history, or the broker's own report.";
                yield break;
            }

            yield return "false, AND THE READ-BACK GENUINELY FAILED. This IS evidence about ATAS.";
            yield return $"{attempts} order(s) went out carrying a client order id and the bridge";
            yield return $"performed {checks} read-back(s) against ATAS's own order collection without";
            yield return "once finding the identifier alongside a broker-assigned id. On this backend";
            yield return "the round trip does not complete, so rule 1 cannot be satisfied and fully";
            yield return "autonomous live trading must stay refused. Confirm against the order-book";
            yield return "reading below before treating it as final.";
        }
    }

    static IEnumerable<string> ClientIdVerdict(bool proven, IReadOnlyList<OrderInfo>? orders,
        string? ordersError, int withClientId, int withBothIds, string? coid)
    {
        if (proven && coid == "proven-crosssession")
        {
            yield return "PROVEN, AND ACROSS A PROCESS RESTART. The bridge has read a client order id";
            yield return "that a PREVIOUS run of this product recorded before submitting, back off an";
            yield return "order in ATAS's live collection, carrying the broker id that run recorded.";
            yield return "That is rule 1 answered from a source that cannot be our own object.";
            yield return "What it still does not prove: that the identifier ever reached the BROKER.";
            yield return "ATAS rebuilding the order, the broker's answer on reconnect and ATAS";
            yield return "rehydrating its own store all look identical from inside a chart strategy.";
            yield break;
        }

        if (proven)
        {
            yield return "PROVEN. The bridge has read one of its own client order ids back off an";
            yield return "order sitting in ATAS's live order collection, with a broker-assigned id";
            yield return "on it, on an object it never touched. That is what rule 1 asks for, and it";
            yield return "was observed, not assumed. What it still does not prove: that the identifier";
            yield return "survives ATAS itself being restarted — that reading needs a separate run,";
            yield return "with --leave-resting and then --coid-restart-check across a restart.";
            yield break;
        }

        if (orders is null)
        {
            yield return "false, NOT NARROWED. The order list could not be read";
            yield return $"({ordersError ?? "the connector handshake did not complete"}),";
            yield return "so this run cannot tell you whether the round trip has merely not been";
            yield return "attempted yet or has been attempted and failed. Those are completely";
            yield return "different answers. Fix the read and run this again before concluding";
            yield return "anything from the false above.";
            yield break;
        }

        if (withClientId == 0)
        {
            yield return "false BECAUSE NOTHING HAS BEEN PLACED YET — not because a round trip failed.";
            yield return orders.Count == 0
                ? "ATAS's live order collection is empty, so the bridge has had nothing to read"
                : $"None of the {orders.Count} order(s) in ATAS's live order collection carries a client";
            yield return orders.Count == 0
                ? "back. On a freshly connected session this is the truthful reading and it is"
                : "order id, so none was placed through TradeAgent and the bridge has had nothing";
            yield return orders.Count == 0
                ? "EXPECTED: the flag starts false and only ever turns true on observation."
                : "to read back. That is the expected reading: the flag starts false and only";
            if (orders.Count > 0)
                yield return "ever turns true on observation.";
            yield return "";
            yield return "To answer the round trip itself: place one order through TradeAgent on this";
            yield return "connection — paper first — and run this verb again.";
            yield break;
        }

        if (withBothIds == 0)
        {
            yield return "false, ROUND TRIP INCOMPLETE — and this is NOT the fresh-session reading.";
            yield return $"{withClientId} order(s) carry a client order id, so the identifier is reaching";
            yield return "ATAS. None of them yet carries a broker-assigned order id, and the bridge";
            yield return "requires both before it will report true. Either the broker has not";
            yield return "acknowledged those orders yet — re-run in a moment — or it never assigns an";
            yield return "id that ATAS surfaces, in which case the round trip cannot be completed on";
            yield return "this backend and false is the permanent, correct answer.";
            yield break;
        }

        if (coid == "proven-sameref")
        {
            yield return "false WITH THE EVIDENCE PRESENT — AND THAT IS THE BRIDGE BEING RIGHT.";
            yield return $"{withBothIds} order(s) in the live book carry BOTH a client order id and a";
            yield return "broker-assigned order id, which from out here looks like exactly the pair";
            yield return "rule 1 asks for. The bridge can see one thing this harness cannot: the order";
            yield return "carrying that pair is the very Order instance it handed to ATAS. Reading our";
            yield return "own field off our own object is not a round trip, so it reports false.";
            yield return "";
            yield return "The two sources are NOT disagreeing here. This reading is inferred from the";
            yield return "order book, which cannot see object identity; the bridge's coid=proven-sameref";
            yield return "above is the better-informed of the two. Believe that one.";
            yield break;
        }

        yield return "false WITH THE EVIDENCE ALREADY PRESENT — INVESTIGATE BEFORE TRUSTING ANYTHING.";
        yield return $"{withBothIds} order(s) in the live book carry BOTH a client order id and a";
        yield return "broker-assigned order id, which is exactly the pair the bridge says it needs,";
        yield return "yet it still reports false. That is not the fresh-session reading and it is";
        yield return "not a round-trip failure either — it is the two disagreeing. Something is";
        yield return "wrong in the bridge or in what it is reading. Do not proceed toward";
        yield return "autonomous trading on this connection until it is explained.";
    }

    static List<string> Differences(BridgeHello a, BridgeHello b)
    {
        var diffs = new List<string>();
        void Cmp(string name, object? x, object? y)
        {
            if (Equals(x, y)) return;
            static string S(object? v) => v switch { null => "<none>", bool b => Yn(b), _ => v.ToString() ?? "<none>" };
            diffs.Add($"{name} {S(x)}->{S(y)}");
        }
        Cmp("bridge_protocol_version", a.BridgeProtocolVersion, b.BridgeProtocolVersion);
        Cmp("bridge_version", a.BridgeVersion, b.BridgeVersion);
        Cmp("atas_version", a.AtasVersion, b.AtasVersion);
        Cmp("account_id", a.AccountId, b.AccountId);
        Cmp("is_simulated", a.IsSimulated, b.IsSimulated);
        Cmp("supports_client_order_id", a.SupportsClientOrderId, b.SupportsClientOrderId);
        Cmp("client_order_id_attempts", a.ClientOrderIdAttempts, b.ClientOrderIdAttempts);
        Cmp("client_order_id_checks", a.ClientOrderIdChecks, b.ClientOrderIdChecks);
        Cmp("supports_order_history", a.SupportsOrderHistory, b.SupportsOrderHistory);
        Cmp("trading_surface", a.TradingSurface, b.TradingSurface);
        Cmp("supports_modify", a.SupportsModify, b.SupportsModify);
        Cmp("supports_close_position", a.SupportsClosePosition, b.SupportsClosePosition);
        return diffs;
    }

    static void NoAnswerAdvice(AtasDetection d)
    {
        Cont("Nothing dialled in. In the order worth checking:");
        if (!d.Running)
            Cont("- ATAS was not running. The bridge lives inside ATAS; start ATAS first.");
        else
            Cont("- ATAS is running, so the strategy itself is the likely gap.");
        Cont("- ATAS does not watch the Strategies folder. Even with the add-on file in");
        Cont("  place, the strategy has to be added and started by hand, once, inside");
        Cont("  ATAS: open a chart, open Strategies for that chart, choose TradeAgent");
        Cont("  Bridge, press Add, then press Start.");
        Cont("- A bridge built for a different .NET than the platform is not rejected with");
        Cont("  an error; it simply never appears in the strategy list. Compare the runtime");
        Cont("  TFM printed above against what the bridge was built for.");
        Cont("- TradeAgent itself must not be running: it owns this pipe and the bridge");
        Cont("  would have connected to it instead of to this probe.");
    }

    /// <summary>
    /// Stands in for TradeAgent for the length of one handshake: answers the bridge's
    /// authentication challenge, then reads the hello it sends once it is satisfied.
    ///
    /// ONE READER, DELIBERATELY. The bridge sends the challenge and the hello back to back, so a
    /// second StreamReader created for the hello would drop the bytes the first had already
    /// buffered — and the symptom would be "the bridge never said hello", which is the same shape
    /// as three traps that have each cost a session.
    ///
    /// The third element of the result is the verdict, in a form the caller prints verbatim. A
    /// bridge that never offers a proof is reported and NOT refused: this verb's job is to say what
    /// is on the pipe, and "the deployed DLL predates authentication" is the single most useful
    /// thing it can say on a machine whose bridge is behind the repository.
    /// </summary>
    static async Task<(string? Raw, BridgeFrame? Frame, string Auth)> Handshake(
        NamedPipeServerStream pipe, BridgeCredential credential, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false), false, 8192, leaveOpen: true);
        var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        var auth = "NOT PRESENTED — this bridge offered no proof at all before saying hello";
        var skipped = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            BridgeFrame? f = null;
            try { f = Json.Read<BridgeFrame>(line); } catch (JsonException) { }

            if (f?.Op == BridgePipeAuth.Challenge)
            {
                var nonce = f.Data.HasValue && f.Data.Value.TryGetProperty("nonce", out var n) ? n.GetString() : null;
                var proof = f.Data.HasValue && f.Data.Value.TryGetProperty("proof", out var pr) ? pr.GetString() : null;

                if (!BridgePipeAuth.IsNonce(nonce) ||
                    !BridgePipeAuth.ProofMatches(credential.Secret, BridgePipeAuth.BridgeRole, nonce!, proof))
                {
                    auth = "MISMATCH — the bridge presented a proof and it is not the one this " +
                           "machine's bridge secret produces. Two installations, or a copied profile.";
                    await writer.WriteLineAsync(Json.Write(new
                    {
                        v = Versions.BridgeProtocolVersion,
                        op = BridgePipeAuth.Refused,
                        error = "this probe holds a different bridge secret"
                    }));
                    continue;
                }

                auth = "OK — the bridge proved it holds this machine's bridge secret, and this probe " +
                       "answered its challenge in TradeAgent's place";
                await writer.WriteLineAsync(Json.Write(new
                {
                    v = Versions.BridgeProtocolVersion,
                    op = BridgePipeAuth.Response,
                    data = new { proof = BridgePipeAuth.Proof(credential.Secret, BridgePipeAuth.ServerRole, nonce!) }
                }));
                continue;
            }

            if (f?.Op == BridgePipeAuth.Refused)
            {
                // A mismatch we diagnosed ourselves is the more precise finding, and the bridge's
                // refusal is only the echo of the refusal we just sent it. Do not let the echo
                // overwrite the diagnosis.
                if (!auth.StartsWith("MISMATCH", StringComparison.Ordinal))
                    auth = $"REFUSED BY THE BRIDGE — {Truncate(f.Error ?? "no reason given")}";
                return (null, null, auth);
            }

            if (f?.Op == BridgeOps.Hello) return (line, f, auth);

            skipped++;
            if (skipped == 1)
            {
                Line("BEFORE THE HELLO", $"an unexpected frame arrived: {Truncate(line)}");
                Cont("A bridge from this build sends the authentication challenge and then the");
                Cont("hello, and nothing else, so this is unexpected.");
            }
        }
        return (null, null, auth);
    }

    static async Task Ticker(Stopwatch sw, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                Console.WriteLine($"{Indent}... waiting for the bridge to dial in ({sw.Elapsed:mm\\:ss} of {limit:mm\\:ss})");
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The BRIDGE AUTH verdict as one grep-able token for the paste-ready summary.</summary>
    static string AuthTag(string verdict) =>
        verdict.StartsWith("OK", StringComparison.Ordinal) ? "ok"
        : verdict.StartsWith("MISMATCH", StringComparison.Ordinal) ? "secret-mismatch"
        : verdict.StartsWith("REFUSED", StringComparison.Ordinal) ? "refused-by-bridge"
        : "not-presented";

    static async Task<bool> Until(Func<bool> condition, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    static string Prettify(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex) { return $"(could not be re-indented: {ex.Message}. The raw line above is the record.)"; }
    }

    static string Truncate(string s) => s.Length > 200 ? s[..200] + "..." : s;
    static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "<none>" : s;
    static string Yn(bool b) => b ? "true" : "false";
    static string Join(string[] parts) => parts.Length == 0 ? "<none>" : string.Join("  |  ", parts);

    static void Line(string label, string value) => Console.WriteLine($"{label.PadRight(Label)}: {value}");
    static void Cont(string text) => Console.WriteLine(text.Length == 0 ? "" : Indent + text);

    static void Section(string title)
    {
        Console.WriteLine();
        var head = $"-- {title} ";
        Console.WriteLine(head + new string('-', Math.Max(3, 80 - head.Length)));
    }
}
