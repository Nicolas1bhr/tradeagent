using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PROTECTIONS THE DOCS CLAIMED AND NO TEST PROVED (<c>U-invariant-proofs</c>; the cards of <c>U-invariant-registry</c>): each test here
/// would fail with its protection broken, and each is named by <see cref="InvariantRegistry"/> as the proof of what was its open part.
/// The compiled checks read every shipped assembly's IL opcode by opcode (<see cref="Il"/>), so a call written in a lambda, a local
/// function or an async state machine is read where it is written; the source checks read the code with comments blanked.
/// </summary>
public class InvariantProofTests
{
    // ---- EVID-001: only the scanner writes `material` -------------------------------------------------------------------------------

    /// <summary>
    /// ONLY THE MATERIAL SCANNER WRITES THE MATERIAL LEDGER (EVID-001; CLAUDE.md "material is written only by the scanner and the agent
    /// cannot edit it"). (1) Every SQL statement in <c>src/</c> that inserts, replaces, updates or deletes rows of <c>material</c> sits in
    /// <c>MaterialStore.cs</c> (its schema's <c>ALTER TABLE</c> in <c>Database.cs</c> aside); the methods holding them are the ledger's
    /// writers. (2) Across every shipped assembly, the only code that calls a writer is <see cref="MaterialScanner"/>'s own. (3) The
    /// scanner's methods that reach a writer are called from the app's own process alone (<c>TradeAgent.App</c>): no pipe op, door verb,
    /// <c>trade</c> verb or agent runtime reaches one. Watched red with the pipe server's <c>material-note</c> handler calling
    /// <see cref="MaterialStore.SetHash"/>.
    /// </summary>
    [Fact]
    public void Only_the_material_scanner_writes_the_material_ledger()
    {
        var root = DayOnePrograms.RepoRoot();
        var write = new Regex(@"\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+\w+)?|DELETE\s+FROM)\s+[""`\[]?material\b(?![_\w])",
            RegexOptions.IgnoreCase);
        var schema = new Regex(@"\b(?:ALTER|DROP)\s+TABLE(?:\s+IF\s+EXISTS)?\s+[""`\[]?material\b(?![_\w])", RegexOptions.IgnoreCase);
        var writersIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var schemaIn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root, "src"))
        {
            var code = InvariantRegistryTests.Strip(File.ReadAllText(file), keepStrings: true);
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (Match m in write.Matches(code))
                (writersIn.TryGetValue(rel, out var set) ? set : writersIn[rel] = new(StringComparer.Ordinal)).Add(EnclosingMember(code, m.Index));
            if (schema.IsMatch(code)) schemaIn.Add(rel);
        }
        Assert.True(writersIn.Keys.SequenceEqual(["src/TradeAgent.Core/Db/MaterialStore.cs"]),
            "a statement writing `material` outside its store: " + string.Join(", ", writersIn.Keys));
        Assert.True(schemaIn.All(f => f == "src/TradeAgent.Core/Db/Database.cs"), "`material`'s shape changed outside the schema: " + string.Join(", ", schemaIn));
        var writers = writersIn["src/TradeAgent.Core/Db/MaterialStore.cs"];
        Assert.Contains("Observe", writers); // not vacuous: the sighting is a writer

        // (2) Every caller of a writer, in every shipped assembly: a writer's helper inside the store makes its callers writers in turn.
        var calls = Il.ShippedCalls().ToList();
        static bool IsStore(MethodBase m) => m.DeclaringType == typeof(MaterialStore);
        bool grew;
        do
        {
            grew = false;
            foreach (var c in calls.Where(c => IsStore(c.Target) && writers.Contains(c.Target.Name) && c.Type == typeof(MaterialStore)))
                grew |= writers.Add(Il.Site(c.Type, c.Method).Split('.')[^1]);
        } while (grew);
        var outside = calls.Where(c => IsStore(c.Target) && writers.Contains(c.Target.Name) && Il.Outer(c.Type) != typeof(MaterialStore)).ToList();
        Assert.NotEmpty(outside); // not vacuous: the scanner's own calls are found
        var strangers = outside.Where(c => Il.Outer(c.Type) != typeof(MaterialScanner)).Select(c => Il.Describe(c)).Distinct().ToList();
        Assert.True(strangers.Count == 0, "material is written by something other than the scanner: " + string.Join("; ", strangers));

        // (3) The scanner's methods that reach a writer, and who calls them.
        var reaching = new HashSet<string>(outside.Select(c => Il.Site(c.Type, c.Method).Split('.')[^1]), StringComparer.Ordinal);
        do
        {
            grew = false;
            foreach (var c in calls.Where(c => c.Target.DeclaringType == typeof(MaterialScanner) && reaching.Contains(c.Target.Name)
                                               && Il.Outer(c.Type) == typeof(MaterialScanner)))
                grew |= reaching.Add(Il.Site(c.Type, c.Method).Split('.')[^1]);
        } while (grew);
        var agentFacing = calls.Where(c => c.Target.DeclaringType == typeof(MaterialScanner) && reaching.Contains(c.Target.Name)
                                           && Il.Outer(c.Type) != typeof(MaterialScanner) && c.Type.Assembly != Il.App)
            .Select(c => Il.Describe(c)).Distinct().ToList();
        Assert.True(agentFacing.Count == 0, "a scan that writes material is started outside the app's own process: " + string.Join("; ", agentFacing));
        Assert.Contains(nameof(MaterialScanner.Scan), reaching);
    }

    // ---- ORDER-005: no UI automation on the order side -------------------------------------------------------------------------------

    /// <summary>The bridge, the connectors and the gateway that dispatches to them: everything an order passes through on its way out.</summary>
    static readonly string[] OrderSide =
        ["TradeAgent.AtasBridge", "TradeAgent.ConnectorSdk", "TradeAgent.Connectors.Atas", "TradeAgent.Connectors.Fake", "TradeAgent.Connectors.Paper", "TradeAgent.Gateway"];

    /// <summary>Assemblies and packages that drive another program's interface: UI Automation, MSAA wrappers, input synthesis, browser drivers.</summary>
    static readonly string[] UiAutomationAssemblies =
    [
        "UIAutomationClient", "UIAutomationTypes", "UIAutomationProvider", "UIAutomationClientSideProviders", "Interop.UIAutomationClient",
        "FlaUI", "TestStack.White", "White.Core", "System.Windows.Forms", "WindowsInput", "InputSimulator", "AutoItX3", "Appium", "WebDriver",
        "Selenium", "Microsoft.Playwright", "PuppeteerSharp", "SharpHook", "Accessibility"
    ];

    /// <summary>Native libraries whose job is input, windows or accessibility on the three platforms.</summary>
    static readonly string[] UiAutomationLibraries =
        ["user32", "oleacc", "uiautomationcore", "ApplicationServices", "CoreGraphics", "Carbon", "AppKit", "libX11", "libXtst", "libxdo"];

    /// <summary>The words of those APIs in source: what a file that drives an interface cannot avoid writing.</summary>
    static readonly Regex UiAutomationWords = new(
        @"\b(?:System\.Windows\.Automation|AutomationElement|UIAutomation\w*|SendKeys|SendInput|keybd_event|mouse_event|SetCursorPos|"
        + @"SetForegroundWindow|FindWindow(?:Ex)?[AW]?|PostMessage[AW]?|SendMessage[AW]|FlaUI|TestStack|InputSimulator|WindowsInput|"
        + @"AXUIElement\w*|CGEventPost|CGEventCreate\w*|XTestFake\w*|xdo_\w+|user32|oleacc|UseWindowsForms|Selenium|Playwright|Puppeteer|Appium)\b",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// THE BRIDGE AND THE CONNECTORS USE NO UI AUTOMATION (ORDER-005; money rule 4 on <c>IAtasAdapter</c>: "Never place orders by
    /// driving a user interface. Programmatic API only."). Compiled: no order-side assembly references a UI-automation assembly, imports a
    /// function from an input, window or accessibility library, or calls into <c>System.Windows.Automation</c>, <c>System.Windows.Forms</c>
    /// or a <c>SendKeys</c>. Source: no file of those projects — the ATAS adapter that is compiled only on a box with ATAS included —
    /// and no project file names one. Watched red with a <c>user32</c> <c>SendInput</c> import added to the loopback adapter, and with
    /// one written into <c>AtasStrategyAdapter.cs</c> alone (the compiled half cannot see it; the source half does).
    /// </summary>
    [Fact]
    public void The_bridge_and_the_connectors_use_no_ui_automation()
    {
        var found = new List<string>();
        var assemblies = OrderSide.Select(n => Assembly.Load(new AssemblyName(n))).ToList();
        foreach (var a in assemblies)
        {
            foreach (var r in a.GetReferencedAssemblies())
                if (UiAutomationAssemblies.Any(u => r.Name!.StartsWith(u, StringComparison.OrdinalIgnoreCase)))
                    found.Add($"{a.GetName().Name} references {r.Name}");
            foreach (var t in Il.Types(a))
                foreach (var m in t.GetMethods(Il.All))
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0 && m.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>() is { } import
                        && UiAutomationLibraries.Any(l => Path.GetFileNameWithoutExtension(import.Value).StartsWith(l, StringComparison.OrdinalIgnoreCase)))
                        found.Add($"{t.FullName}.{m.Name} imports {import.Value}");
        }
        var calls = assemblies.SelectMany(Il.Calls).ToList();
        Assert.True(calls.Count > 1000, $"only {calls.Count} calls read"); // not vacuous
        found.AddRange(calls.Where(c => c.Target.DeclaringType is { } d
                                        && (d.Namespace?.StartsWith("System.Windows.Automation", StringComparison.Ordinal) == true
                                            || d.Namespace?.StartsWith("System.Windows.Forms", StringComparison.Ordinal) == true
                                            || d.Name == "SendKeys"))
            .Select(c => Il.Describe(c)));

        var root = DayOnePrograms.RepoRoot();
        var files = OrderSide.SelectMany(p => Directory.GetFiles(Path.Combine(root, "src", p), "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal)) && !Generated(f))
            .ToList();
        Assert.Contains(files, f => f.EndsWith("AtasStrategyAdapter.cs", StringComparison.Ordinal)); // the real adapter is read
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            var code = f.EndsWith(".cs", StringComparison.Ordinal) ? InvariantRegistryTests.Strip(text, keepStrings: true)
                : Regex.Replace(text, "<!--.*?-->", " ", RegexOptions.Singleline);
            foreach (Match m in UiAutomationWords.Matches(code))
                found.Add($"{Path.GetRelativePath(root, f)} writes {m.Value}");
        }
        Assert.True(found.Count == 0, "the order side drives an interface: " + string.Join("; ", found.Distinct()));
    }

    // ---- AUTH-001: the owner's authority against every op and verb --------------------------------------------------------------------

    /// <summary>
    /// The owner's in-process authority as code: the kill switch (stop, resume, cancel-all, flatten; the AI loop's pause and start, and
    /// the settings that hold them), his credentials (a runtime's sign-in and pasted key), his account connections (the connector and
    /// account chosen) and updates (the app's own and a runtime's install and update).
    /// </summary>
    static IReadOnlyList<MethodBase> Authority()
    {
        var gateway = new[] { nameof(TradingGateway.StopAiTrading), nameof(TradingGateway.EnableAiTrading),
                              nameof(TradingGateway.OperatorCancelAllAsync), nameof(TradingGateway.OperatorCloseAllAsync) };
        var settings = new[] { nameof(TradeAgentSettings.AiTradingStopped), nameof(TradeAgentSettings.AiWorksOnItsOwn),
                               nameof(TradeAgentSettings.ResumeAiOnStart), nameof(TradeAgentSettings.SelectedConnectorId),
                               nameof(TradeAgentSettings.SelectedAccountId) };
        var runtime = new[] { nameof(IAgentRuntime.SignInWithApiKeyAsync), nameof(IAgentRuntime.BeginAuthenticationAsync),
                              nameof(IAgentRuntime.InstallAsync), nameof(IAgentRuntime.UpdateAsync) };
        var update = new[] { nameof(UpdateService.CheckAsync), nameof(UpdateService.InstallAsync), nameof(UpdateService.Dismiss) };
        var runtimes = typeof(IAgentRuntime).Assembly.GetTypes().Where(t => typeof(IAgentRuntime).IsAssignableFrom(t)).ToList();
        Assert.Contains(typeof(CliAgentRuntime), runtimes);
        var all = typeof(TradingGateway).GetMethods(Il.All).Where(m => gateway.Contains(m.Name)).Cast<MethodBase>()
            .Concat(settings.Select(s => (MethodBase)typeof(TradeAgentSettings).GetProperty(s)!.SetMethod!))
            .Concat(runtimes.SelectMany(t => t.GetMethods(Il.All)).Where(m => runtime.Contains(m.Name)))
            .Concat(typeof(UpdateService).GetMethods(Il.All).Where(m => update.Contains(m.Name)))
            .Concat(typeof(MissionLoop).GetMethods(Il.All).Where(m => m.Name is nameof(MissionLoop.Start) or nameof(MissionLoop.PauseAsync)))
            .ToList();
        Assert.Equal(gateway.Length + settings.Length + 3 + 2, all.Count(m => m.DeclaringType != typeof(IAgentRuntime) && !runtimes.Contains(m.DeclaringType!)));
        return all;
    }

    /// <summary>
    /// The places allowed to touch the authority outside the owner's own process, each with its reason: the gateway's kill switch sets
    /// its own setting; the settings' own defaults; a runtime implementing its own sign-in; and the owner's sign-in card's engine
    /// (<see cref="SeatSignIn"/>), itself constructed by the app alone (asserted below).
    /// </summary>
    static bool Conduit(MethodBase site, Type type, MethodBase target)
    {
        var outer = Il.Outer(type);
        if (outer == typeof(TradingGateway) && site.Name is nameof(TradingGateway.StopAiTrading) or nameof(TradingGateway.EnableAiTrading)
            && target.Name == "set_" + nameof(TradeAgentSettings.AiTradingStopped)) return true;
        if (outer == typeof(TradeAgentSettings)) return true;
        if (typeof(IAgentRuntime).IsAssignableFrom(outer) && target.DeclaringType is { } d && (d == outer || d == typeof(IAgentRuntime))) return true;
        return outer == typeof(SeatSignIn);
    }

    /// <summary>
    /// NO PIPE OP AND NO VERB REACHES THE OWNER'S PAUSE, FLATTEN, CREDENTIALS, CONNECTIONS OR UPDATES (AUTH-001; CLAUDE.md "in-process only
    /// and is not reachable from the agent-facing pipe or from any verb, the board's included"). (1) The whole op list — every op the
    /// gateway describes with its <c>trade</c> form and arguments, every op constant, every organisation (door) verb, the board's
    /// included — names none of them: no op, CLI word or argument speaks of a pause, a resume, a kill, a flatten, a credential, a key, a
    /// sign-in, a connection, an account choice, an install or an update; the one op with "stop" in it is <c>deployment-stop</c>, a
    /// deployment's own stop. (2) Compiled: outside the owner's own process (<c>TradeAgent.App</c>, which holds no listener — asserted),
    /// no shipped assembly — the gateway's pipe server and every op handler in it, the broker door, the <c>trade</c> command, the board's
    /// verbs, the agent runtimes — calls, constructs a delegate to, or sets any of them, but the named conduits; and the one conduit that
    /// is a whole type, <see cref="SeatSignIn"/>, is built by the app alone. Watched red with the pipe server's <c>status</c> op calling
    /// <see cref="TradingGateway.EnableAiTrading"/>.
    /// </summary>
    [Fact]
    public void No_pipe_op_or_verb_reaches_the_owners_pause_flatten_credentials_connections_or_updates()
    {
        // (1) the whole op list, by its words.
        string[] words = ["pause", "unpause", "resume", "enable", "disable", "kill", "halt", "flatten", "credential", "credentials", "secret",
            "password", "key", "apikey", "token", "signin", "sign", "login", "logout", "auth", "connect", "connection", "connector", "disconnect",
            "install", "update", "upgrade", "selected"];
        var constants = typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var specs = GatewaySchema.Ops();
        Assert.True(specs.Length > 40 && constants.Count > 40, $"{specs.Length} ops described, {constants.Count} constants"); // the whole list
        var names = constants.Concat(Ops.Organisation).Concat(specs.Select(s => s.Op)).Distinct().ToList();
        var spoken = new List<string>();
        foreach (var op in names)
            foreach (var w in Tokens(op).Where(words.Contains)) spoken.Add($"op {op}: {w}");
        foreach (var s in specs)
        {
            foreach (var w in Tokens(s.Cli).Where(words.Contains)) spoken.Add($"{s.Op} as '{s.Cli}': {w}");
            foreach (var a in s.Args) foreach (var w in Tokens(a.Name).Where(words.Contains)) spoken.Add($"{s.Op} argument {a.Name}: {w}");
        }
        // "connectors" is the read-only list of what is installed — a reading, never a choice: its op takes no argument.
        spoken.RemoveAll(x => x == $"op {Ops.Connectors}: connector");
        Assert.True(spoken.Count == 0, "an op or verb speaks of the owner's authority: " + string.Join("; ", spoken));
        if (specs.SingleOrDefault(s => s.Op == Ops.Connectors) is { } list) Assert.Empty(list.Args);
        Assert.Equal([Ops.DeploymentStop], names.Where(n => Tokens(n).Contains("stop")));

        // (2) compiled: who calls, sets or takes the address of the authority.
        var authority = Authority();
        var calls = Il.ShippedCalls().ToList();
        Assert.Contains(calls, c => c.Type.Assembly == typeof(GatewayPipeServer).Assembly); // the pipe server's code is read
        Assert.Contains(calls, c => c.Type.Assembly.GetName().Name == "trade");               // and the trade command's
        var reaching = calls.Where(c => authority.Any(a => Il.Same(a, c.Target)) && c.Type.Assembly != Il.App && !Conduit(c.Method, c.Type, c.Target))
            .Select(c => Il.Describe(c)).Distinct().ToList();
        Assert.True(reaching.Count == 0, "the owner's authority is reached outside his own process: " + string.Join("; ", reaching));
        var signIn = calls.Where(c => c.Target is ConstructorInfo && c.Target.DeclaringType == typeof(SeatSignIn) && c.Type.Assembly != Il.App)
            .Select(c => Il.Describe(c)).ToList();
        Assert.True(signIn.Count == 0, "the sign-in card's engine is built outside the app: " + string.Join("; ", signIn));

        // The app is the owner's process only while it serves nothing: no listener of any kind is opened in it.
        var listening = Il.Calls(Il.App).Where(c => c.Target.DeclaringType is { } d
                && (d.Name is "NamedPipeServerStream" or "TcpListener" or "HttpListener" or "Socket" or "WebSocket" or "HttpListenerContext"))
            .Select(c => Il.Describe(c)).Distinct().ToList();
        Assert.True(listening.Count == 0, "the app opens a listener of its own: " + string.Join("; ", listening));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    static IEnumerable<string> Tokens(string? s) =>
        string.IsNullOrEmpty(s) ? [] : Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0);

    static bool Generated(string path) =>
        path.Replace('\\', '/') is var p && (p.Contains("/obj/", StringComparison.Ordinal) || p.Contains("/bin/", StringComparison.Ordinal));

    static IEnumerable<string> SourceFiles(string root, string dir) =>
        Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories).Where(f => !Generated(f));

    /// <summary>The name of the member declared at the class's indentation nearest above <paramref name="index"/>.</summary>
    static string EnclosingMember(string code, int index)
    {
        var lines = code[..index].Split('\n');
        var decl = new Regex(@"^    (?:(?:public|internal|private|protected|static|async|override|virtual|sealed|readonly|unsafe)\s+)+[^=;]*?\b(\w+)\s*\(");
        for (var i = lines.Length - 1; i >= 0; i--)
            if (decl.Match(lines[i]) is { Success: true } m) return m.Groups[1].Value;
        return "?";
    }

    /// <summary>
    /// THE IL READER: every method a compiled body calls, constructs or takes the address of, decoded opcode by opcode (never guessed
    /// from bytes), in every type of the assemblies the product ships — compiler-made types (lambdas, local functions, state machines)
    /// included, each read where it was written (<see cref="Outer"/>, <see cref="Site"/>).
    /// </summary>
    internal static class Il
    {
        public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>The owner's own process: the desktop app.</summary>
        public static Assembly App => typeof(TradeAgent.App.AppHost).Assembly;

        /// <summary>Every TradeAgent assembly beside this test (the app and the <c>trade</c> command included), the tests themselves aside.</summary>
        public static IReadOnlyList<Assembly> Shipped() =>
        [
            .. Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("TradeAgent", StringComparison.Ordinal) || n == "trade.dll")
                            && !n.Contains("Tests", StringComparison.Ordinal))
                .Select(f => Assembly.Load(AssemblyName.GetAssemblyName(f)))
        ];

        public static IEnumerable<(Type Type, MethodBase Method, MethodBase Target)> ShippedCalls()
        {
            var shipped = Shipped();
            Assert.Contains(App, shipped);
            Assert.Contains(typeof(GatewayPipeServer).Assembly, shipped);
            Assert.Contains(shipped, a => a.GetName().Name == "trade");
            return shipped.SelectMany(Calls);
        }

        public static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
        }

        public static IEnumerable<(Type Type, MethodBase Method, MethodBase Target)> Calls(Assembly a)
        {
            foreach (var t in Types(a))
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                    foreach (var target in Targets(m))
                        yield return (t, m, target);
        }

        /// <summary>The same method, generic instantiation or not.</summary>
        public static bool Same(MethodBase a, MethodBase b) => a.Module == b.Module && a.MetadataToken == b.MetadataToken;

        /// <summary>The type a compiler-made type was written inside.</summary>
        public static Type Outer(Type t)
        {
            while (t.DeclaringType is { } d && t.Name.StartsWith('<')) t = d;
            return t;
        }

        public static string Describe((Type Type, MethodBase Method, MethodBase Target) c) =>
            $"{c.Type.Assembly.GetName().Name}: {Site(c.Type, c.Method)} → {c.Target.DeclaringType?.Name}.{c.Target.Name}";

        /// <summary>The source method a body was written in: a lambda's, a local function's or a state machine's enclosing one.</summary>
        public static string Site(Type t, MethodBase m)
        {
            var types = new List<string>();
            var from = Inner(m.Name);
            for (var at = t; at is not null; at = at.DeclaringType)
            {
                if (at.Name.StartsWith('<')) { from ??= Inner(at.Name); continue; }
                types.Insert(0, at.Name);
            }
            return $"{string.Join('.', types)}.{from ?? m.Name}";
        }

        static string? Inner(string name) => name.StartsWith('<') && name.TrimStart('<') is var n && n.IndexOf('>') is > 0 and var end ? n[..end] : null;

        static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

        static IEnumerable<MethodBase> Targets(MethodBase m)
        {
            byte[]? il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { il = null; }
            if (il is null) yield break;
            var t = m.DeclaringType!;
            var i = 0;
            while (i < il.Length)
            {
                var value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                var op = Codes[value];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    MethodBase? target = null;
                    try
                    {
                        target = m.Module.ResolveMethod(BitConverter.ToInt32(il, i),
                            t.IsGenericType ? t.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null);
                    }
                    catch (Exception e) when (e is ArgumentException or BadImageFormatException or TypeLoadException or MissingMemberException
                                                  or FileNotFoundException) { }
                    if (target is not null) yield return target;
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4
                };
            }
        }
    }
}
