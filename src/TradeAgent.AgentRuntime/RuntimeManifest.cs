using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

public enum InstallKind { None, Download, Npm, Winget, Manual }

public sealed class InstallPlan
{
    public InstallKind Kind { get; set; } = InstallKind.Manual;

    /// <summary>
    /// Pinned download URL for the Windows x64 build, used when the release API cannot be reached.
    /// GitHub's <c>/releases/latest/download/&lt;asset&gt;</c> form is preferred here because it stays
    /// correct as versions move. May contain {version}.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>"owner/repo". When set, the newest release is looked up at install time.</summary>
    public string? GitHubRepo { get; set; }

    /// <summary>Regex matched against release asset file names to pick the Windows x64 build.</summary>
    public string? AssetPattern { get; set; }

    /// <summary>Path of the program inside the unpacked archive, relative to the install directory.</summary>
    public string? ExecutableInArchive { get; set; }

    /// <summary>Expected SHA-256 of the download, when the publisher pins one. Optional.</summary>
    public string? Sha256 { get; set; }

    public string? ArchiveEntry { get; set; }

    /// <summary>
    /// npm package name. Doubles as the declared fallback for <see cref="InstallKind.Download"/>:
    /// if the archive route fails, this is tried through TradeAgent's own private Node.
    /// </summary>
    public string? NpmPackage { get; set; }

    public string? WingetId { get; set; }
    public string? ManualUrl { get; set; }
}

/// <summary>
/// How a runtime accepts a key the user pastes into TradeAgent's own window.
///
/// This exists because of the no-terminal rule, not in spite of it. OpenCode's interactive sign-in
/// reads the provider key from a TTY prompt and offers no headless equivalent, so without this the
/// only honest instruction was "sign in outside TradeAgent" — which means a terminal, which is the
/// one thing this product promises never to need. A password field in a window is not a terminal.
/// </summary>
public sealed class ApiKeyPlan
{
    /// <summary>What to call the key on screen, e.g. "your OpenAI API key".</summary>
    public string Label { get; set; } = "your API key";

    /// <summary>Where the user gets one. Opened in their browser.</summary>
    public string? HelpUrl { get; set; }

    /// <summary>Arguments that read the key from stdin, e.g. ["login", "--with-api-key"].</summary>
    public string[] StdinArgs { get; set; } = [];

    /// <summary>A credentials file to write instead, with environment variables expanded.</summary>
    public string? File { get; set; }

    /// <summary>The file's contents. "{key}" is replaced with the key, JSON-escaped.</summary>
    public string? FileTemplate { get; set; }
}

/// <summary>
/// How to drive one agent CLI, expressed as DATA rather than code.
///
/// This is deliberate. OpenCode and Codex change their install and login commands on their own
/// schedule, and a build that hard-codes today's flags becomes wrong silently. Shipping the commands
/// as an overridable manifest means a wrong command is a one-line data fix — in
/// <c>%LOCALAPPDATA%\TradeAgent\runtimes.json</c>, no rebuild — instead of a broken product.
///
/// <see cref="Verified"/> is the honest bit: false means at least one field here has not been
/// confirmed by running the real program on a real Windows machine. The Doctor surfaces that.
/// </summary>
public sealed class RuntimeManifest
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SignInDescription { get; set; } = "";

    public InstallPlan Install { get; set; } = new();

    /// <summary>Executable name. Resolved inside the managed tools directory first, then PATH.</summary>
    public string Executable { get; set; } = "";
    public string[] VersionArgs { get; set; } = ["--version"];
    public string[] AuthArgs { get; set; } = [];
    public string[] AuthStateArgs { get; set; } = [];
    public string? AuthStateSuccessPattern { get; set; }
    public string[] HealthArgs { get; set; } = ["--version"];

    /// <summary>Argument template for one-shot execution. "{prompt}" is replaced with the task text.</summary>
    public string[] TaskArgs { get; set; } = ["run", "{prompt}"];

    /// <summary>
    /// Argument template for an interactive session. Empty for every built-in runtime, and that is
    /// the point: TradeAgent hosts the conversation in its own window, so there is no terminal to
    /// put a text UI into. Kept only so an override can drive a runtime that needs a long-lived
    /// background process.
    /// </summary>
    public string[] InteractiveArgs { get; set; } = [];

    // ---- the headless conversation -------------------------------------------------------------
    // These four replace the console window. One message is one non-interactive run of the CLI;
    // the first starts a session and the rest resume it.

    /// <summary>One-shot run of a single message. "{prompt}" is replaced with the user's text.</summary>
    public string[] ExecArgs { get; set; } = [];

    /// <summary>Same, but continuing the session the previous message started.</summary>
    public string[] ResumeArgs { get; set; } = [];

    /// <summary>
    /// Flag that turns stdout into a machine-readable event stream, so the window can show the AI's
    /// text as it arrives and show which tools it is running. Whitespace-separated when the runtime
    /// spells it as two tokens ("--format json"). Null means this runtime has no stream and the app
    /// falls back to showing one message when the run finishes.
    /// </summary>
    public string? JsonFlag { get; set; }

    /// <summary>
    /// The approval/sandbox flags that stop the CLI blocking on a human it cannot reach. Without
    /// these a headless run waits forever for a keypress that will never come.
    /// </summary>
    public string[] UnattendedArgs { get; set; } = [];

    /// <summary>
    /// Regex with one capture group, applied to the sign-in command's output to pull out the URL the
    /// user has to visit. TradeAgent opens it in the browser itself, so the sign-in never needs a
    /// console.
    /// </summary>
    public string? AuthUrlPattern { get; set; }

    /// <summary>How this runtime takes a pasted key, or null if it signs in another way.</summary>
    public ApiKeyPlan? ApiKey { get; set; }

    /// <summary>The one TradeAgent puts first, because its sign-in works without leaving the window.</summary>
    public bool Recommended { get; set; }

    public bool RequiresNode { get; set; }
    public bool SelfContained { get; set; }

    /// <summary>False until every command above has been confirmed by running it on Windows.</summary>
    public bool Verified { get; set; }

    public string? DocsUrl { get; set; }
}

/// <summary>
/// The built-in manifests, overridable from disk.
///
/// Where each value came from, so the next person does not have to guess:
///
/// <list type="bullet">
/// <item>Install fields (repo, asset pattern, path inside the archive, pinned URL, npm package) were
/// read from the vendors' own live release metadata and, for Codex, from OpenAI's own install
/// script, on 2026-08-26.</item>
/// <item>Conversation and sign-in fields were read from the vendors' current published CLI
/// documentation, not from running the programs.</item>
/// </list>
///
/// Both manifests are therefore <c>Verified = false</c>: nothing here has been proven by executing
/// the real CLI on Windows, which is the only bar that counts.
/// </summary>
public static class RuntimeCatalog
{
    public static string OverridePath => Path.Combine(Paths.Home, "runtimes.json");

    /// <summary>
    /// A URL in output, stopping at whitespace or a quote. Deliberately generic, and deliberately
    /// first-match-wins: which of several printed addresses is the one to open differs per runtime
    /// and per version, and this field exists precisely so that a wrong guess is fixed by tightening
    /// the pattern in <c>runtimes.json</c> rather than by shipping a new build.
    /// </summary>
    const string AnyUrl = @"(https?://[^\s""'<>\)\]]+)";

    public static List<RuntimeManifest> BuiltIn() =>
    [
        new RuntimeManifest
        {
            Id = "opencode",
            DisplayName = "OpenCode",
            Description = "An open-source coding agent that runs on your machine. Works with several AI providers.",
            // Honest, because the alternative is a button that does nothing: OpenCode's sign-in
            // reads the provider key from an interactive terminal prompt and offers no headless
            // path — no key flag, no device code, no URL to open. TradeAgent will not host a
            // terminal and will not ask anyone to type an API key into it, so this runtime is
            // signed in outside TradeAgent, or its provider key is already in the environment.
            SignInDescription =
                "OpenCode connects to an AI provider with a key from that provider. Paste it below and " +
                "TradeAgent stores it where OpenCode looks for it.",
            ApiKey = new ApiKeyPlan
            {
                Label = "your OpenAI API key",
                HelpUrl = "https://platform.openai.com/api-keys",
                // OpenCode resolves its data directory with xdg-basedir, which has no Windows branch
                // and so uses ~/.local/share on every platform. The shape below is its own Api record:
                // { "<provider>": { "type": "api", "key": "..." } }.
                File = OperatingSystem.IsWindows()
                    ? @"%USERPROFILE%\.local\share\opencode\auth.json"
                    : "~/.local/share/opencode/auth.json",
                FileTemplate = "{\"openai\":{\"type\":\"api\",\"key\":\"{key}\"}}"
            },
            Executable = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode",
            Install = new InstallPlan
            {
                Kind = InstallKind.Download,
                GitHubRepo = "anomalyco/opencode",
                AssetPattern = @"^opencode-windows-x64\.zip$",
                // The zip holds exactly one file, opencode.exe, at its root.
                ExecutableInArchive = "opencode.exe",
                Url = "https://github.com/anomalyco/opencode/releases/latest/download/opencode-windows-x64.zip",
                NpmPackage = "opencode-ai",
                ManualUrl = "https://opencode.ai/docs/"
            },
            VersionArgs = ["--version"],
            // Deliberately empty. OpenCode's `auth login` reads the key from an interactive terminal
            // prompt — there is no URL to open and no headless equivalent — so declaring it here
            // would put a Sign in button on screen that cannot do anything. The key field is the
            // sign-in for this runtime.
            AuthArgs = [],
            AuthStateArgs = ["auth", "list"],
            // `opencode auth list` ends with "<n> credentials" and never sets an exit code, so the
            // exit code says nothing. A leading non-zero digit is what distinguishes "3 credentials"
            // from "0 credentials".
            AuthStateSuccessPattern = @"\b[1-9]\d*\s+credentials\b",
            AuthUrlPattern = AnyUrl,
            TaskArgs = ["run", "{prompt}"],
            ExecArgs = ["run", "{prompt}"],
            ResumeArgs = ["run", "--continue", "{prompt}"],
            // `--format json` — OpenCode has no `--json`. Each line carries type/timestamp/sessionID
            // plus a payload; assistant text is type "text" with the words at part.text.
            JsonFlag = "--format json",
            // Non-interactive mode already auto-denies the permissions that would need a human;
            // --auto also auto-approves tool permissions, which is what lets it use `trade`.
            UnattendedArgs = ["--auto"],
            InteractiveArgs = [],
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://opencode.ai/docs/"
        },
        new RuntimeManifest
        {
            Id = "codex",
            DisplayName = "OpenAI Codex CLI",
            Description = "OpenAI's coding agent. Signs in with your ChatGPT account.",
            SignInDescription = "A browser window will open so you can sign in with your ChatGPT account.",
            Recommended = true,
            ApiKey = new ApiKeyPlan
            {
                Label = "an OpenAI API key instead",
                HelpUrl = "https://platform.openai.com/api-keys",
                // Codex removed its --api-key flag and now reads the key from stdin. It refuses when
                // stdin is a terminal, which is exactly the shape TradeAgent wants.
                StdinArgs = ["login", "--with-api-key"]
            },
            Executable = OperatingSystem.IsWindows() ? "codex.exe" : "codex",
            Install = new InstallPlan
            {
                Kind = InstallKind.Download,
                GitHubRepo = "openai/codex",
                AssetPattern = @"^codex-package-x86_64-pc-windows-msvc\.tar\.gz$",
                // OpenAI's own installer asserts this layout after unpacking the same archive.
                ExecutableInArchive = "bin/codex.exe",
                Url = "https://github.com/openai/codex/releases/latest/download/codex-package-x86_64-pc-windows-msvc.tar.gz",
                NpmPackage = "@openai/codex",
                ManualUrl = "https://developers.openai.com/codex/cli/"
            },
            VersionArgs = ["--version"],
            AuthArgs = ["login"],
            // `codex login status` prints to stderr and exits 0 when signed in, 1 when not. The exit
            // code is the reliable signal, so no success pattern is set here.
            AuthStateArgs = ["login", "status"],
            // `codex login` prints two addresses: first the local callback server it just started,
            // then the one to actually visit. Requiring https and refusing localhost picks the
            // second, which is the one the user needs.
            AuthUrlPattern = @"(https://(?!localhost|127\.0\.0\.1)[^\s""'<>\)\]]+)",
            TaskArgs = ["exec", "{prompt}"],
            ExecArgs = ["exec", "{prompt}"],
            // `resume --last` is filtered to the current working directory unless --all is passed,
            // and the working directory is TradeAgent's own workspace, so "the last session" can
            // only ever mean a session TradeAgent itself started.
            ResumeArgs = ["exec", "resume", "--last", "{prompt}"],
            JsonFlag = "--json",
            // --skip-git-repo-check because the agent's workspace is not a git repository and Codex
            // refuses to run outside one by default.
            //
            // On the second flag, plainly: --sandbox workspace-write would be the tighter choice,
            // but on Windows Codex ships a separate sandbox component with its own elevated setup
            // command, and nobody has confirmed that the sandbox modes work on a clean Windows 11
            // machine without running it. A sandbox that silently fails to start is a product that
            // silently cannot trade. The bypass flag is the combination that is documented to run
            // unattended, and TradeAgent's safety does not live in this sandbox anyway — every
            // order still goes through the gateway's modes, limits, approvals and kill switch,
            // none of which the agent can reach or change. Revisit once tested on Windows.
            UnattendedArgs = ["--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox"],
            InteractiveArgs = [],
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://developers.openai.com/codex/cli/"
        },
        new RuntimeManifest
        {
            Id = "custom",
            DisplayName = "Other AI assistant",
            Description = "Any command-line AI tool, described by a manifest. Developer-facing: the end user never edits this.",
            Executable = "",
            Install = new InstallPlan { Kind = InstallKind.None },
            Verified = false
        }
    ];

    public static List<RuntimeManifest> Load()
    {
        var builtIn = BuiltIn();
        if (!File.Exists(OverridePath)) return builtIn;
        try
        {
            var overrides = Json.Read<List<RuntimeManifest>>(File.ReadAllText(OverridePath)) ?? [];
            foreach (var o in overrides)
            {
                var i = builtIn.FindIndex(b => b.Id == o.Id);
                if (i >= 0) builtIn[i] = o; else builtIn.Add(o);
            }
        }
        catch (Exception) { /* a broken override file must not stop the app from starting */ }
        return builtIn;
    }

    public static void SaveOverrides(IEnumerable<RuntimeManifest> manifests) =>
        File.WriteAllText(OverridePath, Json.Write(manifests.ToList(), pretty: true));

    public static RuntimeManifest? Find(string id) => Load().FirstOrDefault(m => m.Id == id);
}
