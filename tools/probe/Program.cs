using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;

// The evidence behind the claims in BUILD-STATUS.md, re-runnable:
//
//   probe install <runtime>   the AI tool installs itself from nothing, with no window
//   probe chat    <runtime>   a real conversation happens, and no window opens
//   probe atas                what Describe() reports on a live ATAS bridge, and what that means
//                             for autonomy — step 3 of docs/RESUME-HERE.md
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
/// Read-only by construction. It places no order, modifies none, cancels none, and asks the bridge
/// for nothing but a handshake, an account list and an order list. Every line is labelled so the
/// whole run survives being pasted into BUILD-STATUS.md.
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

        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--wait" && i + 1 < rest.Length && int.TryParse(rest[i + 1], out var secs) && secs >= 0)
            { wait = TimeSpan.FromSeconds(secs); i++; continue; }
            if (rest[i] == "--wait-anyway") { waitAnyway = true; continue; }

            Console.WriteLine("usage: probe atas [--wait <seconds>] [--wait-anyway]");
            Console.WriteLine($"  unrecognised argument '{rest[i]}'");
            Console.WriteLine("  --wait <seconds>   how long to wait for the bridge to dial in (default 60)");
            Console.WriteLine("  --wait-anyway      wait for the pipe even though ATAS was not detected;");
            Console.WriteLine("                     only useful when driving the pipe with a stand-in bridge");
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
        Line("THIS RUN WILL", "read only. No order is placed, modified or cancelled by this verb.");
        Line("EXIT CODES", "0 = the bridge answered and the answer below is the record");
        Cont("1 = could not reach the bridge    2 = bad arguments");
        Cont("A capability reading of false is a valid answer and still exits 0.");

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
                (raw, frame) = await ReadHello(server, deadline.Token);
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
                Cont("Something dialled in to the pipe and then said nothing. BridgeServer sends");
                Cont("the hello as its first act, so whatever connected is not a bridge this");
                Cont("build can talk to.");
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

        if (raw is null || frame is null)
        {
            Line("HELLO FRAME", "NONE — something connected to the pipe and then said nothing,");
            Cont("or it closed the connection before sending a hello. That is not a bridge");
            Cont("TradeAgent can use: BridgeServer sends the hello as its first act.");
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
            Line("CONNECTOR HANDSHAKE", $"NOT COMPLETED within {reconnectLimit.TotalSeconds:0}s.");
            Cont("The hello above was still received, so the capability lines below are derived");
            Cont("from it with the same expression AtasConnector.Capabilities uses. The order");
            Cont("evidence further down could not be gathered.");
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
        var reported = ReportedClientIdVerdict(caps.SupportsClientOrderId, attempts, checks)?.ToList();

        Line("SUBMITTED WITH AN ID", attempts is null
            ? "NOT REPORTED — this bridge predates the attempt counters"
            : $"{attempts}   (orders this bridge sent to ATAS carrying a client order id)");
        Line("READ-BACKS PERFORMED", checks is null
            ? "NOT REPORTED"
            : $"{checks}   (times it then looked one of them up in ATAS's own order");
        if (checks is not null) Cont("collection — the check that can set the flag)");

        var verdict = ClientIdVerdict(caps.SupportsClientOrderId, orders, ordersError, withClientId, withBothIds).ToList();

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

        // ------------------------------------------------------------------ autonomy

        Section("WHAT THIS MEANS FOR AUTONOMY");
        if (caps.ReconciliationProvable)
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
            $"| SupportsClientOrderId={Yn(caps.SupportsClientOrderId)} SupportsOrderHistory={Yn(caps.SupportsOrderHistory)} " +
            $"IsSimulated={Yn(caps.IsPaper)} | ReconciliationProvable={Yn(caps.ReconciliationProvable)} " +
            $"| autonomy={(caps.ReconciliationProvable ? "permitted" : "refused")}");

        return 0;
    }

    // ------------------------------------------------------------------------------------ pieces

    /// <summary>
    /// What the bridge itself says about rule 1, or null when it does not keep the count.
    ///
    /// Null is a real answer here and must stay distinguishable from zero: a bridge that reports
    /// nothing has not told us it attempted nothing. Returning "0 attempts" for a silent bridge
    /// would reintroduce, one field lower, the exact ambiguity these counters were added to remove.
    /// </summary>
    static IEnumerable<string>? ReportedClientIdVerdict(bool proven, int? attempts, int? checks)
    {
        if (attempts is null || checks is null) return null;
        return Lines(proven, attempts.Value, checks.Value);

        static IEnumerable<string> Lines(bool proven, int attempts, int checks)
        {
            if (proven)
            {
                yield return "PROVEN, AND THE BRIDGE SAYS SO ITSELF.";
                yield return $"It submitted {attempts} order(s) carrying a client order id, performed";
                yield return $"{checks} read-back(s) against ATAS's own order collection, and one of them";
                yield return "found the identifier alongside a broker-assigned id. Rule 1 is satisfied";
                yield return "by observation. It still does not prove the id survives an ATAS restart —";
                yield return "nothing observable from inside a strategy can prove that.";
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
        string? ordersError, int withClientId, int withBothIds)
    {
        if (proven)
        {
            yield return "PROVEN. The bridge has read one of its own client order ids back off an";
            yield return "order sitting in ATAS's live order collection, with a broker-assigned id";
            yield return "on it. That is what rule 1 asks for, and it was observed, not assumed.";
            yield return "What it still does not prove: that the identifier survives ATAS itself";
            yield return "being restarted. Nothing observable from inside a strategy can prove that.";
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

    static async Task<(string? Raw, BridgeFrame? Frame)> ReadHello(Stream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false), false, 8192, leaveOpen: true);
        var skipped = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            BridgeFrame? f = null;
            try { f = Json.Read<BridgeFrame>(line); } catch (JsonException) { }
            if (f?.Op == BridgeOps.Hello) return (line, f);
            skipped++;
            if (skipped == 1)
            {
                Line("BEFORE THE HELLO", $"a frame arrived before the hello did: {Truncate(line)}");
                Cont("BridgeServer sends the hello as its first act, so this is unexpected.");
            }
        }
        return (null, null);
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
