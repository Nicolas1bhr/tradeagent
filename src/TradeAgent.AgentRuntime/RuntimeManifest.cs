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
    /// HOW THIS RUNTIME IS TOLD WHICH MODEL TO RUN. <c>"{model}"</c> is replaced with the id.
    ///
    /// Data, like every other command here, because which flag a vendor spells this with is the
    /// vendor's business and changes on their schedule. Empty means the runtime has no such flag —
    /// and empty is load-bearing rather than merely absent: it is what keeps the "dearest model in
    /// the catalogue" estimate alive for a runtime whose model TradeAgent genuinely cannot choose,
    /// while a runtime that HAS this flag is priced at the model TradeAgent asked for.
    /// </summary>
    public string[] ModelArgs { get; set; } = [];

    /// <summary>
    /// The model TradeAgent asks for when the owner has not chosen one, or null for "let the
    /// runtime decide". The alternative is what shipped before: the model came from the CLI's own
    /// config file, so the loop ran whatever that said — measured on 2026-09-07, that was
    /// <c>gpt-6-astra</c> at about 1.5 USD a turn, chosen by nobody.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// THE MODEL THIS RUNTIME WILL BE ASKED FOR, given the owner's choice. Null means no model flag
    /// goes on the command line at all — either the runtime has none, or nothing has named one.
    /// </summary>
    public string? ModelFor(string? chosen) =>
        ModelArgs.Length == 0 ? null
        : chosen is { Length: > 0 } c ? c
        : DefaultModel is { Length: > 0 } d ? d
        : null;

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
/// <item>Conversation fields were read from the vendors' current published CLI documentation, not
/// from running the programs.</item>
/// <item>The sign-in fields — <c>AuthArgs</c>, <c>AuthUrlPattern</c>, <c>AuthStateArgs</c>,
/// <c>AuthStateSuccessPattern</c> and both <c>ApiKey</c> shapes — were executed against the vendors'
/// real binaries on <b>macOS 26.5.1 / arm64, 2026-08-27</b>: Codex 0.150.0 unpacked from
/// <c>codex-package-aarch64-apple-darwin.tar.gz</c> and OpenCode 1.18.23 from
/// <c>opencode-darwin-arm64.zip</c> — the macOS builds of the same two releases these manifests
/// install on Windows. Every per-field note below says what was run and what came back. Nothing
/// about that run speaks for the Windows-only halves of these manifests: the <c>.exe</c> names, the
/// <c>%USERPROFILE%\.local\share</c> spelling of OpenCode's credentials file, or Codex's Windows
/// archive layout.</item>
/// </list>
///
/// Both manifests nevertheless stay <c>Verified = false</c>. <c>Verified</c> means proven by running
/// the real CLI <b>on Windows</b>, which is the only bar that counts, and a macOS run does not move
/// it.
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
                //
                // Both halves executed on macOS 26.5.1, OpenCode 1.18.23, 2026-08-27, against a home
                // directory with no OpenCode state in it at all. `opencode auth list` names the file
                // itself, so the path is the program's own word rather than an inference:
                //     ┌  Credentials ~/.local/share/opencode/auth.json
                //     │
                //     └  0 credentials
                // TradeAgent then wrote exactly the template below through
                // CliAgentRuntime.SignInWithApiKeyAsync — 63 bytes,
                // {"openai":{"type":"api","key":"sk-FAKE-tradeagent-probe-0000"}} — and the same
                // command answered:
                //     ┌  Credentials ~/.local/share/opencode/auth.json
                //     │
                //     ●  OpenAI api
                //     │
                //     └  1 credentials
                // "OpenAI" and "api" are OpenCode reading the provider key and the type field back
                // out of the record, which is what makes this a proof of the JSON shape and not only
                // of the path. The Windows spelling of the same path has still never been exercised.
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
                // The zip holds exactly one file, opencode.exe, at its root. Read out of the live
                // asset's own central directory on 2026-08-27 with a ranged GET, rather than assumed:
                // 1 entry, "opencode.exe", 179,550,760 bytes uncompressed.
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
            //
            // Still true at 1.18.23: `opencode auth login --help` on macOS, 2026-08-27, lists only
            // "-p, --provider" and "-m, --method", which skip the two *selection* prompts. Nothing
            // there accepts a key.
            AuthArgs = [],
            AuthStateArgs = ["auth", "list"],
            // `opencode auth list` ends with "<n> credentials" and never sets an exit code, so the
            // exit code says nothing. A leading non-zero digit is what distinguishes "3 credentials"
            // from "0 credentials".
            //
            // Both measured on macOS, OpenCode 1.18.23, 2026-08-27: the command exits 0 whether it
            // prints "0 credentials" or "1 credentials", so the exit code really is worthless here.
            // It also prints "1 credentials", plural, so a pattern spelling the singular would miss.
            // Its output does carry ANSI colour — "Credentials \e[90m~/.local/share/opencode/..."
            // on stdout and a bare "\e[0m" on stderr — but on this version no escape lands between
            // the digit and the word, so this pattern happens to match before Ansi.Strip as well as
            // after. Do not read that as permission to drop the strip: the escape sits directly
            // against the path, one field over.
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
                //
                // Executed on macOS, Codex 0.150.0, 2026-08-27, through
                // CliAgentRuntime.SignInWithApiKeyAsync's stdin branch with a deliberately fake key:
                // the child read the line, exited 0, and wrote CODEX_HOME/auth.json as
                // {"auth_mode":"apikey","OPENAI_API_KEY":"..."}. `codex login status` then printed
                // "Logged in using an API key - sk-FAKE-***-0000" and exited 0.
                //
                // Note what that does and does not mean: Codex stores the key without contacting
                // OpenAI, so an accepted key proves the key was *stored*, never that it works. The
                // same is true of OpenCode. AuthState.Authenticated means "a credential is on disk".
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
            // On macOS at 0.150.0, `codex login` does NOT short-circuit when a credential is already
            // stored: with an API key on disk it still started the browser flow and printed a fresh
            // authorize URL. So BeginAuthenticationAsync's "finished without printing a URL" branch
            // is not reached by an already-signed-in Codex on this version, and a user who presses
            // Sign in twice gets a second sign-in, not a message saying they are done. Check the
            // state with AuthStateArgs before offering the button; do not infer it from this command.
            AuthArgs = ["login"],
            // `codex login status` prints to stderr and exits 0 when signed in, 1 when not. The exit
            // code is the reliable signal, so no success pattern is set here.
            //
            // Both polarities measured on macOS, Codex 0.150.0, 2026-08-27: with a credential on
            // disk, stderr "Logged in using an API key - sk-FAKE-***-0000", exit 0; against an empty
            // CODEX_HOME, stderr "Not logged in", exit 1. stdout is empty in both cases and neither
            // carries an ANSI escape.
            AuthStateArgs = ["login", "status"],
            // `codex login` prints two addresses: first the local callback server it just started,
            // then the one to actually visit. Requiring https and refusing localhost picks the
            // second, which is the one the user needs.
            //
            // Run on macOS, Codex 0.150.0, 2026-08-27, against an empty CODEX_HOME — the not-signed-in
            // state that had never been exercised. Everything goes to stderr, stdout is empty, and
            // the order is as described:
            //     Starting local login server on http://localhost:1455.
            //     If your browser did not open, navigate to this URL to authenticate:
            //
            //     https://auth.openai.com/oauth/authorize?response_type=code&client_id=...
            //
            //     On a remote or headless machine? Use `codex login --device-auth` instead.
            // This pattern picked the auth.openai.com address; BeginAuthenticationAsync returned it
            // in 0.2s with no window. Two details worth keeping: the callback address is printed as
            // plain http, so on this version the "https" requirement alone already excludes it and
            // the negative lookahead is belt-and-braces; and codex login emitted no ANSI escape at
            // all when its output was a pipe, so Ansi.Strip is insurance on this path rather than
            // the thing making it work. OpenCode's auth output does colour, so keep the strip.
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
            // THE MODEL IS TRADEAGENT'S CHOICE, NOT ~/.codex/config.toml's.
            //
            // Measured on this Mac, codex-cli 0.153.4, 2026-09-07, in a scratch directory, with the
            // machine's own config.toml saying `model = "gpt-6-astra"`:
            //
            //   codex exec --json --skip-git-repo-check -s read-only -m gpt-5.6-sol "<prompt>"
            //     -> exit 0, and the flag was accepted; the turn ran.
            //   codex exec resume --last --json --skip-git-repo-check -m gpt-5.6-sol "<prompt>"
            //     -> exit 0. `-m, --model <MODEL>` is on `codex exec resume --help` as well as on
            //        `codex exec`, so the resumed turn takes it too — which is the half that
            //        matters, since nineteen of every twenty turns are resumes.
            //
            // One flag that is NOT shared: `-s/--sandbox` is refused by `exec resume`
            // ("error: unexpected argument '-s' found", exit 2). UnattendedArgs above passes
            // --dangerously-bypass-approvals-and-sandbox rather than -s, and that one IS on both.
            //
            // gpt-5.6-sol as the default because it is the mid-priced current model on the
            // catalogue this build ships (4.00 in / 20.00 out per million against astra's
            // 10.00/50.00), so the day's ceiling buys roughly two and a half times the work.
            ModelArgs = ["-m", "{model}"],
            DefaultModel = "gpt-5.6-sol",
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

    /// <summary>
    /// The runtimes this build will start, or the reason there are none.
    ///
    /// AN UNREADABLE OVERRIDE FILE YIELDS NO RUNTIMES AT ALL, and that is the point of this method
    /// existing (milestone review 2026-09-05b, Codex F16 / UNVERIFIED 6). This used to catch every
    /// exception and return the built-ins, so an owner who edited <c>runtimes.json</c> to pin a
    /// version, change a flag or take the sandbox bypass OUT got the shipped command back on one
    /// misplaced comma — a different program, under a different sandbox policy, launched silently in
    /// place of the one they wrote. Failing safe was the argument for it, and it is not safe: the
    /// built-in <c>codex</c> manifest carries
    /// <c>--dangerously-bypass-approvals-and-sandbox</c>.
    ///
    /// So the file is trusted whole or not at all. Not "the entries that parsed": a file that ended
    /// mid-token has no honest partial reading, and one that does parse is either the owner's
    /// intent entire or nothing.
    ///
    /// An ABSENT file is a different fact and keeps its old meaning — the built-ins, no complaint.
    /// </summary>
    public static RuntimeCatalogRead Read()
    {
        var file = VendorFile.Read<List<RuntimeManifest>>(OverridePath);
        if (file.Unreadable is { } why) return new([], Labels.RuntimesCouldNotBeRead(why));

        var builtIn = BuiltIn();
        foreach (var o in file.Value ?? [])
        {
            var i = builtIn.FindIndex(b => b.Id == o.Id);
            if (i >= 0) builtIn[i] = o; else builtIn.Add(o);
        }
        return new(builtIn, null);
    }

    public static List<RuntimeManifest> Load() => [.. Read().Runtimes];

    public static void SaveOverrides(IEnumerable<RuntimeManifest> manifests) =>
        File.WriteAllText(OverridePath, Json.Write(manifests.ToList(), pretty: true));

    public static RuntimeManifest? Find(string id) => Read().Runtimes.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The manifest an agent is about to be STARTED from, or a refusal in the owner's own words.
    ///
    /// <see cref="Find"/> answers null for two situations that are not alike — an id nobody has a
    /// manifest for, and a catalogue that could not be read — and the caller that starts the AI has
    /// to tell the owner which. Every start path goes through here so that neither can end in a
    /// built-in being launched quietly.
    /// </summary>
    public static RuntimeManifest Require(string id)
    {
        var read = Read();
        if (read.Unreadable is not null) throw new TradeAgentException(ErrorCode.RUNTIME_COMMANDS_UNREADABLE, read.Unreadable);
        return read.Runtimes.FirstOrDefault(m => m.Id == id)
               ?? throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND, $"no manifest for '{id}'");
    }
}

/// <summary>
/// The catalogue, or the one sentence saying why there is none. <see cref="Unreadable"/> non-null
/// always means <see cref="Runtimes"/> is empty: a refusal and a fallback cannot both be true.
/// </summary>
public sealed record RuntimeCatalogRead(IReadOnlyList<RuntimeManifest> Runtimes, string? Unreadable);

/// <summary>
/// What one turn cost, or the sentence saying why nobody can say. Never both, and never neither.
/// </summary>
/// <param name="Estimated">
/// Non-null when <see cref="Cost"/> is an UPPER BOUND rather than a bill — the model was never
/// named, so the dearest entry in that runtime's catalogue was charged — and then it is the sentence
/// that travels with the figure onto every screen. Null on a figure priced against a named model.
/// </param>
/// <param name="ByOwner">
/// The figure came from the two numbers the owner typed on the Safety page rather than from any list
/// price. Then <paramref name="Estimated"/> is null even when nothing named a model: the rate is not
/// a guess over a catalogue, it is what the owner says they are charged.
/// </param>
public sealed record TurnPrice(decimal? Cost, string Currency, string? Unpriced, string? Estimated = null,
    bool ByOwner = false)
{
    /// <summary>
    /// WHERE THE RATE CAME FROM, for the launch ledger's <c>pricing_basis</c>: <c>owner</c> when the
    /// two numbers on the Safety page were used, and otherwise the day a list price was read and the
    /// page it was read from. Init-only rather than a sixth positional parameter, so every existing
    /// site that builds one of these keeps meaning what it meant.
    ///
    /// A figure with no basis is a figure nobody can check afterwards, and these go stale on the
    /// vendor's schedule rather than this repository's.
    /// </summary>
    public string? Basis { get; init; }

    public static TurnPrice Unknown(string why) => new(null, "", why);
}

/// <summary>One model's prices, per MILLION tokens, in <see cref="AgentCosts.Currency"/>.</summary>
public sealed class ModelPrice
{
    public string Model { get; set; } = "";

    /// <summary>
    /// The <see cref="RuntimeManifest.Id"/> this price belongs to, or empty for "any runtime".
    ///
    /// The distinction is not decoration. A price WITH a runtime is part of that runtime's
    /// catalogue, and the catalogue is what an unknown model is estimated against
    /// (<see cref="CostCatalog.Highest"/>) — so an entry here can raise what a turn nobody could
    /// identify is charged at. A price with none can only ever be looked up BY NAME, which is the
    /// safe half: an owner adding one model's price to <c>costs.json</c> does not thereby change
    /// what an unidentified turn costs.
    /// </summary>
    public string Runtime { get; set; } = "";

    /// <summary>
    /// The date this figure was read from <see cref="Source"/>, as <c>yyyy-MM-dd</c>. Empty on a
    /// price the owner wrote, who knows when theirs was true; never empty on a shipped one.
    /// </summary>
    public string PricedAt { get; set; } = "";

    /// <summary>The vendor page the figure was read from. Shown to the owner beside the default.</summary>
    public string Source { get; set; } = "";

    public decimal InputPerMillion { get; set; }

    /// <summary>Null means the input rate: a vendor that does not discount cache reads charges it.</summary>
    public decimal? CachedInputPerMillion { get; set; }

    /// <summary>Null means the input rate. OpenAI does not bill cache writes separately; others do.</summary>
    public decimal? CacheWritePerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}

/// <summary>
/// <c>costs.json</c>: what the owner's AI tool charges them, beside <c>runtimes.json</c> and read
/// the same way.
///
/// <b>THE FILE ships empty, and the BUILD does not.</b> The file's own catalogue is what an engineer
/// writes here; the dated list prices this build was shipped with are <see cref="ListPrices"/>, in
/// this same shape, and the two are merged by <see cref="CostCatalog"/> with the file winning
/// wherever it speaks. That is <c>runtimes.json</c>'s arrangement — built-ins in code, an override
/// file on top — and it is why an absent file is still not a refusal and still ships no catalogue of
/// its own.
///
/// The reason the built-ins are dated and sourced rather than simply correct is the one
/// <c>U-meter</c> gave for shipping none at all: a price is a claim about somebody else's bill, and
/// this software cannot see that bill. The same Codex CLI costs per-token on an API key and nothing
/// per-token on a ChatGPT subscription. What changed is the conclusion — an owner who will never
/// open a text editor was left with a daily cap that could not bite — so the list price is charged,
/// said to be a list price, and beaten by the two numbers on the Safety page.
/// </summary>
public sealed class AgentCosts
{
    /// <summary>The currency every number here is in, and the one the cap is read in.</summary>
    public string Currency { get; set; } = "USD";

    public List<ModelPrice> Models { get; set; } = [];

    /// <summary>
    /// The model to price a runtime's turns at WHEN THE RUNTIME DOES NOT SAY WHICH IT USED — keyed
    /// by <see cref="RuntimeManifest.Id"/>. Codex 0.153.4's <c>--json</c> stream names no model
    /// anywhere (see <see cref="TurnUsage"/>), so without an entry here its turns are unpriced. The
    /// owner names it because only they know what they are signed in as.
    /// </summary>
    public Dictionary<string, string> RuntimeModels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The prices, or the one sentence saying why there are none. Never both.</summary>
public sealed record CostCatalogRead(AgentCosts? Costs, string? Unreadable);

/// <summary>
/// Reading <c>costs.json</c>, under <c>VendorFile</c>'s rule: an ABSENT file is the shipped
/// configuration and says nothing; a file that EXISTS and cannot be parsed is a refusal, in the
/// owner's words, and nothing shipped stands in for it.
///
/// What a refusal means here is narrower than it is for <c>runtimes.json</c>, deliberately. That
/// file decides which program runs and under what sandbox, so an unreadable one stops the AI. This
/// one only prices what the AI already did: an unreadable one makes every turn "cost unknown" and
/// therefore makes the daily cap unenforceable — which the card says, rather than the software
/// quietly pricing turns at zero and reporting a cap that is holding nothing back.
/// </summary>
public static class CostCatalog
{
    public static string OverridePath => Path.Combine(Paths.Home, Labels.CostsFile);

    /// <summary>
    /// THE FILE's shipped contents, which are empty. Not the BUILD's prices — those are
    /// <see cref="ListPrices.All"/>, dated and sourced, and <see cref="Applicable"/> is where the
    /// two meet. An absent <c>costs.json</c> therefore still contributes no catalogue of its own,
    /// which is what makes "absent is not a refusal" true of this layer as well.
    /// </summary>
    public static AgentCosts BuiltIn() => new();

    public static CostCatalogRead Read()
    {
        var file = VendorFile.Read<AgentCosts>(OverridePath);
        if (file.Unreadable is { } why) return new(null, Labels.CostsCouldNotBeRead(why));
        return new(file.Value ?? BuiltIn(), null);
    }

    /// <summary>
    /// MAY THE SHIPPED FIGURES BE MERGED UNDER THIS CATALOGUE? Only where the currencies agree.
    ///
    /// <see cref="ListPrices"/> is in dollars. An owner whose <c>costs.json</c> says <c>EUR</c> has
    /// told this software what its numbers mean, and adding dollar figures underneath would make a
    /// total labelled in euros that is partly not — a wrong bill, in the direction hardest to
    /// notice. Their file then stands alone, which is what an override is for.
    /// </summary>
    public static bool ListPricesApply(AgentCosts costs) =>
        string.Equals(costs.Currency, ListPrices.Currency, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every price that can be applied to <paramref name="runtimeId"/>, THE OWNER'S FILE FIRST.
    ///
    /// A model in both is the owner's: they are describing a bill they have seen and this build is
    /// quoting a page. Order is the mechanism — <see cref="Price"/> takes the first match — and
    /// <see cref="Highest"/> takes a maximum, which is order-blind, so a model named twice is
    /// dropped by name here rather than counted twice there.
    /// </summary>
    public static IEnumerable<ModelPrice> Applicable(AgentCosts costs, string? runtimeId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in costs.Models.Where(p => Belongs(p, runtimeId)))
            if (seen.Add(p.Model)) yield return p;

        if (!ListPricesApply(costs)) yield break;

        foreach (var p in ListPrices.All.Where(p => Belongs(p, runtimeId)))
            if (seen.Add(p.Model)) yield return p;
    }

    /// <summary>
    /// A price belongs to a runtime when it names that runtime, or when it names none at all — the
    /// owner's shorthand for "whatever is running, this is the rate".
    /// </summary>
    static bool Belongs(ModelPrice p, string? runtimeId) =>
        p.Runtime.Length == 0 ||
        (runtimeId is { Length: > 0 } id && string.Equals(p.Runtime, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// THE DEAREST ENTRY IN ONE RUNTIME'S OWN CATALOGUE — what an unidentified turn is charged at,
    /// and the default the Safety page shows the owner.
    ///
    /// Runtime-agnostic entries are deliberately not in it, though <see cref="Belongs"/> lets them
    /// price a NAMED model: an entry that names no runtime is in no runtime's catalogue, and the
    /// practical half is that an owner pricing one model in <c>costs.json</c> must not thereby raise
    /// what every unidentified turn on every runtime is charged.
    ///
    /// Dearest is decided by output rate then input rate, NOT by pricing a specimen turn: the answer
    /// must not depend on the shape of the turn being asked about, or the same installation would
    /// show the owner one default and charge them against another.
    /// </summary>
    public static ModelPrice? Highest(AgentCosts costs, string? runtimeId) =>
        runtimeId is not { Length: > 0 }
            ? null
            : Applicable(costs, runtimeId)
                .Where(p => p.Runtime.Length > 0)
                .OrderByDescending(p => p.OutputPerMillion)
                .ThenByDescending(p => p.InputPerMillion)
                .FirstOrDefault();

    /// <summary>The dearest entry for a runtime, under the catalogue on disk. For the screens.</summary>
    public static ModelPrice? Highest(string? runtimeId) =>
        Read().Costs is { } costs ? Highest(costs, runtimeId) : null;

    /// <summary>
    /// THE MODELS THE OWNER MAY CHOOSE BETWEEN on one runtime — its own catalogue, in the order the
    /// catalogue lists them, with each entry carrying the two rates the Safety page prints beside it.
    ///
    /// Runtime-agnostic entries are excluded for the same reason <see cref="Highest"/> excludes
    /// them: an owner pricing one model in <c>costs.json</c> is describing a bill, not adding a
    /// model to a vendor's menu, and a button that asks a CLI for a model it does not have is a
    /// button that stops the AI.
    /// </summary>
    public static IReadOnlyList<ModelPrice> Choices(string? runtimeId) =>
        Read().Costs is { } costs
            ? [.. Applicable(costs, runtimeId).Where(p => p.Runtime.Length > 0)]
            : [];

    /// <summary>
    /// WHAT THIS TURN COST, or why nobody can say. Every branch that cannot produce a number
    /// produces a sentence instead; none of them produces a zero.
    /// </summary>
    public static TurnPrice Price(TurnUsage? usage, string? runtimeId, CostCatalogRead? catalogue = null,
        OwnerPrice? owner = null)
    {
        var read = catalogue ?? Read();
        if (read.Unreadable is { } why) return TurnPrice.Unknown(why);

        var costs = read.Costs ?? BuiltIn();
        if (usage is null)
            return TurnPrice.Unknown("the AI tool did not report how many tokens the turn used");

        // THE OWNER'S OWN RATE IS THE LAST WORD, and it is checked before the model is even looked
        // for. They are the only party who can see the bill; a list price is this build quoting a
        // vendor's page at them. It applies whether or not anything named a model, which is the
        // whole point — the ordinary Codex installation never names one, and asking the owner to
        // identify a model in order to correct a rate would be asking them the question they cannot
        // answer.
        //
        // An unreadable costs.json still refuses above, deliberately: that file failing is not the
        // owner saying anything, and a currency read out of it is part of what the figure means.
        if (owner is not null)
            return new TurnPrice(Charge(usage, owner), costs.Currency, null, null, ByOwner: true)
                { Basis = OwnerBasis };

        var model = usage.Model ?? Declared(costs, runtimeId);

        // AN UNKNOWN IS PRICED HIGH, NEVER ZERO AND NEVER ABSENT.
        //
        // Codex names no model in any of its events, so on the runtime this build recommends EVERY
        // turn arrives here. `U-meter` returned "unpriced", which was honest about the arithmetic
        // and wrong about the consequence: an unpriced turn cannot reach the daily cap, so the
        // ceiling the owner set held nothing back on the ordinary installation.
        //
        // The dearest entry in that runtime's own catalogue is charged instead, and the figure says
        // so wherever it is shown. The direction is the design: over-charging an unidentified turn
        // can only stop the AI early, which midnight or the owner's own two numbers undo; charging
        // it low, or nothing, lets the cap be walked past, which nothing undoes.
        if (model is null)
            return Highest(costs, runtimeId) is { } highest
                ? new TurnPrice(Charge(usage, highest), costs.Currency, null, Labels.PricedAtHighestListPrice)
                    { Basis = BasisOf(highest) }
                : TurnPrice.Unknown(
                    $"the AI tool did not say which model it used, and {Labels.CostsFile} does not name one for it");

        var price = Applicable(costs, runtimeId)
            .FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));
        if (price is null)
            return TurnPrice.Unknown($"{Labels.CostsFile} has no price for {model}");

        return new TurnPrice(Charge(usage, price), costs.Currency, null) { Basis = BasisOf(price) };
    }

    /// <summary>What <see cref="TurnPrice.Basis"/> says when the owner's own two numbers were used.</summary>
    public const string OwnerBasis = "owner";

    /// <summary>
    /// The day a price was read and the page it was read from, as one line for the launch ledger.
    /// A price the owner wrote carries no date — they know when theirs was true — and then the basis
    /// names the file rather than claiming a day nobody recorded.
    /// </summary>
    static string BasisOf(ModelPrice p) =>
        p.PricedAt.Length > 0 && p.Source.Length > 0 ? $"{p.PricedAt} {p.Source}"
        : p.Source.Length > 0 ? p.Source
        : Labels.CostsFile;

    /// <summary>
    /// The same arithmetic on the owner's two numbers, over the same token totals the list-price
    /// overload bills. CACHED INPUT IS CHARGED AT THE FULL INPUT RATE here, because they gave a rate
    /// for input and did not give a discount for cache reads, and inventing one would under-charge
    /// every turn — the direction that lets the cap be walked past. An owner whose vendor discounts
    /// cache reads has <c>costs.json</c>, which takes all four figures.
    /// </summary>
    static decimal Charge(TurnUsage usage, OwnerPrice owner) =>
        (usage.InputTokens * owner.InputPerMillion +
         usage.CacheWriteInputTokens * owner.InputPerMillion +
         usage.OutputTokens * owner.OutputPerMillion) / 1_000_000m;

    /// <summary>
    /// The arithmetic, on one price. Cached input is a SUBSET of the input count and reasoning
    /// output a subset of the output count, so each is billed exactly once — see
    /// <see cref="TurnUsage"/>, where both subsets were measured rather than assumed.
    /// </summary>
    static decimal Charge(TurnUsage usage, ModelPrice price) =>
        (usage.UncachedInputTokens * price.InputPerMillion +
         usage.CachedInputTokens * (price.CachedInputPerMillion ?? price.InputPerMillion) +
         usage.CacheWriteInputTokens * (price.CacheWritePerMillion ?? price.InputPerMillion) +
         usage.OutputTokens * price.OutputPerMillion) / 1_000_000m;

    static string? Declared(AgentCosts costs, string? runtimeId) =>
        runtimeId is { Length: > 0 } id && costs.RuntimeModels.TryGetValue(id, out var m) && m.Length > 0
            ? m
            : null;
}

/// <summary>
/// Puts an unreadable <c>runtimes.json</c> on the health row the owner is already looking at.
///
/// A class rather than a static call because it has to give the row BACK. Nothing else writes
/// <see cref="Components.AgentRuntime"/> until an agent is prepared, so a reporter that only ever
/// set FAILED would leave the row red for the rest of the session after the file was corrected —
/// a repair that worked, reported as one that did not, which is the same defect the ATAS bridge
/// row's cache had. It therefore remembers whether the standing row is its own, and hands that one
/// back to UNKNOWN — never a row somebody else wrote.
/// </summary>
public sealed class RuntimeFileHealth
{
    bool _owned;

    public void Report(HealthRegistry health)
    {
        var why = RuntimeCatalog.Read().Unreadable;
        if (why is not null) { health.Set(Components.AgentRuntime, HealthState.FAILED, why); _owned = true; }
        else if (_owned) { health.Set(Components.AgentRuntime, HealthState.UNKNOWN, ""); _owned = false; }
    }
}
