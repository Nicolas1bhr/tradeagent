using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
/// HOW THIS VENDOR SAYS IT HAS REFUSED A TURN FOR ITS OWN USAGE LIMIT, AND WHEN IT SAYS TO TRY AGAIN.
///
/// <para>Data, like every other vendor behaviour in a manifest: the sentence is the vendor's, it changes
/// on the vendor's schedule, and a wrong pattern has to be a one-line fix in <c>runtimes.json</c> rather
/// than a rebuild. A manifest with none recognises nothing, and every such turn stays the failed turn it
/// always was — which is the honest default for a vendor whose refusal nobody has recorded.</para>
///
/// <para><b>The retry time is the vendor's wall clock.</b> Codex prints it in the zone of the machine it
/// runs on, and the app reads it in <see cref="TimeZoneInfo.Local"/> of the same machine: the CLI is the
/// app's own child, so both are asking one operating system the same question.</para>
/// </summary>
public sealed class UsageLimitPlan
{
    /// <summary>Regex matched against the message of the stream's error event. A match IS the refusal.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>
    /// Regex whose group <c>at</c> captures the retry time out of the same message, or null for a vendor
    /// that never states one. A message it does not match states no time.
    /// </summary>
    public string? RetryAtPattern { get; set; }

    /// <summary>
    /// Exact formats for that capture, in the invariant culture. A format with no date in it is a time on
    /// the machine's current local date, which is how a vendor says "later today".
    /// </summary>
    public string[] RetryAtFormats { get; set; } = [];

    /// <summary>The patterns are owner-editable data, so a pathological one costs a second, never the turn.</summary>
    static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// THE REFUSAL THIS MESSAGE IS, or null because it is not one. Never throws: an override whose pattern
    /// does not compile, or runs away, recognises nothing, and the turn is the failed turn it was.
    /// </summary>
    /// <param name="message">The error event's message, as the stream carried it.</param>
    /// <param name="observedAt">When the event was read — which local date a time with no date is on.</param>
    /// <param name="zone">The machine's zone; <see cref="TimeZoneInfo.Local"/> unless a test names one.</param>
    public VendorLimit? Read(string? message, DateTimeOffset observedAt, TimeZoneInfo? zone = null)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(Pattern)) return null;
        try
        {
            if (!Regex.IsMatch(message, Pattern, RegexOptions.CultureInvariant, MatchTimeout)) return null;
            return new VendorLimit(message.Trim(), RetryAt(message, observedAt, zone ?? TimeZoneInfo.Local));
        }
        catch (ArgumentException) { return null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>
    /// THE FIRST INSTANT AT WHICH THE TIME THE VENDOR NAMED HAS CERTAINLY PASSED, or null where it named
    /// none this can read.
    ///
    /// <para><b>The END of the minute it printed.</b> Every format here stops at the minute and the vendor
    /// drops the seconds, so at HH:MM:00 its limit can still have 59 seconds to run — and a turn launched
    /// into them is refused, with the wakes it was answering spent on nothing.</para>
    ///
    /// <para><b>A wall-clock time the zone passes twice</b> — the hour a clock goes back — is read as the
    /// LATER of the two instants: the earlier one could be before the limit ends, and the cost of the
    /// later one is an hour of waiting once a year. A time the zone skips cannot be printed by a vendor
    /// converting a real instant, and is moved forward past the gap rather than refused.</para>
    /// </summary>
    DateTimeOffset? RetryAt(string message, DateTimeOffset observedAt, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(RetryAtPattern)) return null;

        var m = Regex.Match(message, RetryAtPattern, RegexOptions.CultureInvariant, MatchTimeout);
        if (!m.Success || m.Groups["at"] is not { Success: true } at) return null;

        if (!DateTime.TryParseExact(at.Value.Trim(), RetryAtFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowWhiteSpaces, out var stated))
            return null;

        // No date in the text is the vendor's "today": the local date of the moment the event was read.
        var local = stated.Date == DateTime.MinValue.Date
            ? TimeZoneInfo.ConvertTime(observedAt, zone).Date + stated.TimeOfDay
            : stated;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Min()     // the smaller offset is the later instant
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).AddMinutes(1);
    }
}

/// <summary>
/// A VENDOR'S REFUSAL OF ONE TURN FOR ITS OWN USAGE LIMIT, as that turn's stream said it — the typed
/// reason a turn ended, where the only other evidence is an exit code of 1.
/// </summary>
/// <param name="Message">
/// The vendor's sentence, verbatim. It is what the conversation shows last, and the card with it: the
/// observed run's card showed stderr's first line instead, which codex prints on every run.
/// </param>
/// <param name="RetryAt">
/// When the limit has certainly ended — see <see cref="UsageLimitPlan"/> — or null where the vendor named
/// no time, and then the mission loop waits its own backoff exactly as it does after any failed turn.
/// </param>
public sealed record VendorLimit(string Message, DateTimeOffset? RetryAt)
{
    /// <summary>Who refused, as the owner knows them: the runtime's display name.</summary>
    public string Vendor { get; init; } = "";

    /// <summary>
    /// TRUE WHEN THE REFUSAL ARRIVED BEFORE THE STREAM HAD SHOWN ANY WORK: no item of any kind — a
    /// message, reasoning, a command, a tool — no text and no usage. Decided by the session at the moment
    /// of the refusal, from the stream it had parsed so far, and the one fact that lets a turn be charged
    /// nothing: a turn that did any of those may have been billed, and unknown is never zero.
    /// </summary>
    public bool BeforeAnyWork { get; init; }

    /// <summary>The word <c>ai_attempt.context</c> carries as <c>ended</c> for a turn that ended this way.</summary>
    public const string Ended = "VENDOR_USAGE_LIMIT";
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
    ///
    /// <para><b>A HARNESS manifest names its model in the request body</b>, not on a command line, so
    /// an empty <see cref="ModelArgs"/> is not "this runtime's model cannot be chosen" for one of
    /// those — it is simply that there is no command line to put a flag on. The
    /// <see cref="Endpoint"/> arm is what distinguishes the two, and it has to be here rather than at
    /// the call site: <c>TurnMeter</c> prices a reservation through this method, and a harness whose
    /// model answered null would be reserved at the dearest model in the catalogue while running on
    /// the cheapest.</para>
    /// </summary>
    public string? ModelFor(string? chosen) =>
        ModelArgs.Length == 0 && Endpoint.Length == 0 ? null
        : chosen is { Length: > 0 } c ? c
        : DefaultModel is { Length: > 0 } d ? d
        : null;

    /// <summary>
    /// Regex with one capture group, applied to the sign-in command's output to pull out the URL the
    /// user has to visit. TradeAgent opens it in the browser itself, so the sign-in never needs a
    /// console.
    /// </summary>
    public string? AuthUrlPattern { get; set; }

    /// <summary>
    /// Regex with one capture group, applied to the same output to pull out the ONE-TIME CODE the owner types
    /// on another device, or null for a sign-in that shows none. A sign-in with a code is finished away from
    /// this computer — on the owner's phone, say, because a host with no browser has nowhere to open one — so
    /// TradeAgent shows the address and the code and opens nothing. Data, because the shape of the code is the
    /// vendor's: a code that changes shape is a one-line fix in <c>runtimes.json</c>.
    /// </summary>
    public string? AuthCodePattern { get; set; }

    /// <summary>How this runtime takes a pasted key, or null if it signs in another way.</summary>
    public ApiKeyPlan? ApiKey { get; set; }

    /// <summary>
    /// How this vendor refuses a turn for its own usage limit, or null where nobody has recorded it. See
    /// <see cref="UsageLimitPlan"/>.
    /// </summary>
    public UsageLimitPlan? UsageLimit { get; set; }

    /// <summary>
    /// ENVIRONMENT VARIABLE NAMES THIS VENDOR'S CLI READS, and the only names outside TradeAgent's
    /// own whitelist that reach the agent process (<see cref="AgentEnvironment"/>).
    ///
    /// Here rather than in that whitelist because vendor commands are data: a CLI that starts
    /// reading a new variable is a one-line change to <c>runtimes.json</c>, not a rebuild. Names
    /// only — a value belongs to the machine, never to a manifest, and nothing here may name a
    /// variable TradeAgent would have to put a credential into.
    /// </summary>
    public string[] KeepEnvironment { get; set; } = [];

    /// <summary>
    /// WHERE THE APP-OWNED HARNESS SENDS ITS REQUESTS, or empty for a runtime that is a program on
    /// this machine rather than an endpoint.
    ///
    /// <para>Data, like every command here, and for a sharper version of the same reason: a provider
    /// moves a path or a version prefix on its own schedule, and an endpoint compiled into a build is
    /// an outage that needs a release. Non-empty is also the one thing that makes a manifest a HARNESS
    /// manifest — <see cref="RuntimeCatalog.Harnesses"/> is exactly the entries that carry one.</para>
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The path under <see cref="BaseUrl"/> that a turn's requests go to.</summary>
    public string CompletionsPath { get; set; } = "";

    /// <summary>
    /// THE PROVIDER'S OWN NAME FOR THE MAXIMUM-OUTPUT PARAMETER, which is the only per-request cap
    /// the provider itself enforces and therefore the only one that is not advisory.
    ///
    /// It is data because this is the field vendors rename: OpenAI's <c>max_tokens</c> became
    /// <c>max_completion_tokens</c>, and a build that sent the old name against a provider that had
    /// moved on would send NO bound at all and would not be told so.
    /// </summary>
    public string MaxOutputParam { get; set; } = "max_completion_tokens";

    /// <summary>The full URL one turn's requests go to. Empty for a runtime with no endpoint.</summary>
    public string Endpoint =>
        BaseUrl.Length == 0 ? "" : $"{BaseUrl.TrimEnd('/')}/{CompletionsPath.TrimStart('/')}";

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

    /// <summary>The built-ins for THIS computer.</summary>
    public static List<RuntimeManifest> BuiltIn() => BuiltIn(HostPlatform.Current);

    /// <summary>The built-ins for the computer <paramref name="on"/> describes.</summary>
    public static List<RuntimeManifest> BuiltIn(HostPlatform on) =>
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
                File = on.IsWindows
                    ? @"%USERPROFILE%\.local\share\opencode\auth.json"
                    : "~/.local/share/opencode/auth.json",
                FileTemplate = "{\"openai\":{\"type\":\"api\",\"key\":\"{key}\"}}"
            },
            Executable = on.IsWindows ? "opencode.exe" : "opencode",
            Install = OpenCodeInstall(on),
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
            // Where Codex keeps its own state and its auth.json. Unset on an ordinary machine — the
            // CLI then uses ~/.codex — and honoured here so an install that DOES set it keeps
            // working once the agent's environment became a whitelist.
            KeepEnvironment = ["CODEX_HOME"],
            Description = "OpenAI's coding agent. Signs in with your ChatGPT account.",
            SignInDescription = on.Os == HostOs.Linux
                ? "Codex signs in with your ChatGPT account on another device: open the address below on your phone " +
                  "or another computer, sign in there and type the one-time code shown here. Device code login has " +
                  "to be switched on in ChatGPT's security settings first."
                : "A browser window will open so you can sign in with your ChatGPT account.",
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
            Executable = on.IsWindows ? "codex.exe" : "codex",
            Install = CodexInstall(on),
            VersionArgs = ["--version"],
            // On macOS at 0.150.0, `codex login` does NOT short-circuit when a credential is already
            // stored: with an API key on disk it still started the browser flow and printed a fresh
            // authorize URL. So BeginAuthenticationAsync's "finished without printing a URL" branch
            // is not reached by an already-signed-in Codex on this version, and a user who presses
            // Sign in twice gets a second sign-in, not a message saying they are done. Check the
            // state with AuthStateArgs before offering the button; do not infer it from this command.
            //
            // ON LINUX, THE DEVICE CODE (U-linux-host). A host with no browser cannot complete `login`'s
            // localhost:1455 callback, and the vendor's route for one is `codex login --device-auth`
            // (learn.chatgpt.com/docs/auth, read 2026-10-08: "Enable device code login in your ChatGPT security
            // settings (personal account)"), which prints a link and a one-time code to type on another device.
            // Run ONCE on 2026-10-09T09:07Z — codex-cli 0.160.1 on macOS, a fresh empty CODEX_HOME, stopped by an
            // alarm after 12 s at the code and never authorised, the owner's own sign-in untouched, nothing
            // downloaded. stdout, colour kept, the code masked to its shape (A a letter, 9 a digit):
            //     Welcome to Codex [v^[[90m0.160.1^[[0m]
            //     ...
            //     1. Open this link in your browser and sign in to your account
            //        ^[[94mhttps://auth.openai.com/codex/device^[[0m
            //
            //     2. Enter this one-time code ^[[90m(expires in 15 minutes)^[[0m
            //        ^[[94mAAA9-AA99A^[[0m
            // — device_code_prompt of codex-rs/login/src/device_code_auth.rs, the same at rust-v0.160.1 and
            // rust-v0.162.0. Four characters and FIVE, so the generic XXXX-XXXX this app used to look for found
            // no code at all. The code is the server's; its shape beyond this one run is NOT claimed, and a new
            // shape is a one-line fix in runtimes.json. AuthUrlPattern below takes the link as it is.
            AuthArgs = on.Os == HostOs.Linux ? ["login", "--device-auth"] : ["login"],
            AuthCodePattern = on.Os == HostOs.Linux ? @"\b([A-Z0-9]{4}-[A-Z0-9]{5})\b" : null,
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
            // THE VENDOR'S OWN REFUSAL, RECORDED. codex-cli 0.153.4 on macOS, 2026-10-01T18:20:07Z, run
            // once in a scratch folder while the owner's plan was limited:
            //
            //   codex exec --json --skip-git-repo-check "reply ok"     -> exit 1 after 4 s; stdout
            //     {"type":"thread.started","thread_id":"…"}
            //     {"type":"turn.started"}
            //     {"type":"error","message":"You've hit your usage limit. Upgrade to Pro
            //       (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to
            //       purchase more credits or try again at 10:30 PM."}
            //     {"type":"turn.failed","error":{"message":"<the same sentence>"}}
            //   and on stderr only "Reading additional input from stdin...".
            //
            // No item and no usage: the first request was refused. "10:30 PM" was the machine's own
            // wall clock — 22:30 CEST, 20:30Z — so the CLI prints the reset in the local zone. The rest
            // is the binary's own text, read out of that executable with `strings`, not seen in a
            // stream: every refusal it can print opens "You've hit your usage limit"; the suffixes are
            // " or try again at " and " or try again later."; the time is "%-I:%M %p" on the same
            // local date and "%b %-d" + st/nd/rd/th + ", %Y %-I:%M %p" on another one. The dated form
            // is therefore in the formats below and has NOT been seen in a stream.
            //
            // AND SINCE 2026-10-05 WITH A TYPOGRAPHIC APOSTROPHE. Read on 2026-10-07 in this Mac's codex
            // session logs, for `usage_limit_exceeded` events only, the sentence the only text taken: the
            // three refusals of 2026-10-01 carry "You've" (U+0027); the eight of 2026-10-05/06, from Codex
            // Desktop sessions under codex-cli 0.159.2 and 0.160.1, carry "You’ve" (U+2019):
            //
            //   "You’ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit
            //    https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 8:49 PM."
            //
            // and the same sentence ending "try again at Oct 7th, 2026 1:10 AM." — the dated form, in a
            // vendor sentence for the first time. So the pattern takes either apostrophe at that one place
            // (U+2019 written as \u2019 below) and is widened nowhere else: no IgnoreCase and no bare "usage
            // limit", because another error read as the limit would be held and charged nothing. NOT
            // VERIFIED: that `codex exec --json`'s stdout carries the U+2019 sentence under 0.160.1 — the
            // session log's error message does; on 2026-10-01 both carried U+0027.
            UsageLimit = new UsageLimitPlan
            {
                Pattern = "You['\u2019]ve hit your usage limit",
                RetryAtPattern = @"try again at (?<at>.+?)\.?\s*$",
                RetryAtFormats =
                [
                    "h:mm tt",
                    "MMM d'st', yyyy h:mm tt", "MMM d'nd', yyyy h:mm tt",
                    "MMM d'rd', yyyy h:mm tt", "MMM d'th', yyyy h:mm tt"
                ]
            },
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
            // THE DEFAULT PREDATES THE GPT-6 SOL AND LUNA ROWS. gpt-5.6-sol was chosen on 2026-09-07 as
            // the mid-priced current model on the catalogue this build then shipped (4.00 in / 20.00 out
            // per million against gpt-6-astra's 10.00/50.00), so the day's ceiling bought about two and
            // a half times astra's work. As ListPrices reads OpenAI's page on 2026-10-02 it is no longer
            // the middle: gpt-6.1-sol and gpt-6-sol list at 2.00/10.00, half its figure, and gpt-6-luna
            // at 0.10/0.50. And its own 4.00/20.00 is promotional — the only figure the page prints for
            // it, standing at least through 2026-11-21 (its row's comment in ListPrices). Which model a
            // seat defaults to is the owner's decision, not this comment's, so DefaultModel is unchanged.
            ModelArgs = ["-m", "{model}"],
            DefaultModel = "gpt-5.6-sol",
            InteractiveArgs = [],
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://developers.openai.com/codex/cli/"
        },
        new RuntimeManifest
        {
            // THE APP-OWNED HARNESS. Not a program on this machine: TradeAgent calls the provider
            // itself, so there is nothing to install, nothing to detect and no console anywhere near
            // it. See ApiAgentRuntime for what that buys — every model-and-tool boundary is the app's.
            Id = ApiAgentRuntime.RuntimeId,
            DisplayName = "TradeAgent's own worker (OpenAI API)",
            Description =
                "TradeAgent calls the AI provider itself, with the tools it chooses and a budget it "
                + "enforces per request. No separate program is installed and no console is ever opened.",
            // NOT A SIGN-IN FLOW: there is no login command to run headless, so the honest path is the
            // one CLAUDE.md allows — a pasted key in the app's own window. It is held in memory for
            // the session and written nowhere, which is what the sentence beside the box says.
            SignInDescription =
                "Paste your OpenAI API key on the Safety page. TradeAgent keeps it in memory for this "
                + "session only, never writes it to disk, and clears it when it closes — so it is never "
                + "left sitting beside an AI process nothing on this computer confines.",
            ApiKey = new ApiKeyPlan
            {
                Label = "your OpenAI API key",
                HelpUrl = "https://platform.openai.com/api-keys"
            },
            // No executable, no install plan, no auth args: every one of those describes a program,
            // and this runtime is code inside this build.
            Executable = "",
            Install = new InstallPlan { Kind = InstallKind.None },
            // The OpenAI-compatible chat-completions shape. UNVERIFIED against the real provider by
            // design — the brief forbids a real provider call — and every test drives it against a
            // loopback HttpListener. What IS verified is that the app sends the model, the granted
            // tools and the max-output parameter, and reads back text, tool calls and usage.
            BaseUrl = "https://api.openai.com/v1",
            CompletionsPath = "chat/completions",
            MaxOutputParam = "max_completion_tokens",
            // THE DEFAULT PREDATES THE GPT-6 SOL AND LUNA ROWS. gpt-5.6-luna was chosen on 2026-09-13 as
            // the cheapest current model on the catalogue this build then shipped (0.20 in / 1.20 out per
            // million against gpt-5.6-sol's 4.00/20.00), because a WORKER is the case the harness exists
            // for: "can run the cheapest capable model per task" (docs/COUNCIL.md). As ListPrices reads
            // OpenAI's page on 2026-10-02 it is no longer the cheapest current model: gpt-6-luna lists at
            // 0.10/0.50. That a GPT-6 model is a CAPABLE worker here is not claimed — tool calls on the
            // harness for the GPT-6 models are U-harness-responses' work, not yet built
            // (docs/ORGANISATION.md § 15). Which model a seat defaults to is the owner's decision, so
            // DefaultModel is unchanged. The owner picks another on the Safety page; the reservation is
            // priced at whichever is in force.
            DefaultModel = "gpt-5.6-luna",
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://platform.openai.com/docs/api-reference/chat"
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
    /// OPENCODE'S DOWNLOAD FOR THE COMPUTER <paramref name="on"/> DESCRIBES.
    ///
    /// <para><b>Windows</b>, as it always was: the zip holds exactly one file, opencode.exe, at its root — read
    /// out of the live asset's own central directory on 2026-08-27 with a ranged GET, rather than assumed:
    /// 1 entry, "opencode.exe", 179,550,760 bytes uncompressed.</para>
    ///
    /// <para><b>Linux</b>: <c>opencode-linux-{x64|arm64}.tar.gz</c>, the glibc build — 60,669,097 B for x64 at
    /// v1.18.35, read through GitHub's API on 2026-10-08 and again on 2026-10-09. OpenCode's own release script
    /// packs it as <c>tar -czf … *</c> inside the build's <c>bin/</c> folder (<c>packages/opencode/script/build.ts</c>
    /// at v1.18.35), so the program is at the root and named <c>opencode</c>.</para>
    ///
    /// <para><b>Anywhere else</b> — macOS, which no owner runs TradeAgent on — there is no plan, and the install
    /// says so rather than unpacking a Windows program onto it.</para>
    /// </summary>
    static InstallPlan OpenCodeInstall(HostPlatform on) => on switch
    {
        { IsWindows: true } => new InstallPlan
        {
            Kind = InstallKind.Download,
            GitHubRepo = "anomalyco/opencode",
            AssetPattern = @"^opencode-windows-x64\.zip$",
            ExecutableInArchive = "opencode.exe",
            Url = "https://github.com/anomalyco/opencode/releases/latest/download/opencode-windows-x64.zip",
            NpmPackage = "opencode-ai",
            ManualUrl = "https://opencode.ai/docs/"
        },
        { Os: HostOs.Linux, Arch: Architecture.X64 or Architecture.Arm64 } => new InstallPlan
        {
            Kind = InstallKind.Download,
            GitHubRepo = "anomalyco/opencode",
            AssetPattern = $@"^opencode-linux-{LinuxCpu(on, "x64", "arm64")}\.tar\.gz$",
            ExecutableInArchive = "opencode",
            Url = $"https://github.com/anomalyco/opencode/releases/latest/download/opencode-linux-{LinuxCpu(on, "x64", "arm64")}.tar.gz",
            NpmPackage = "opencode-ai",
            ManualUrl = "https://opencode.ai/docs/"
        },
        _ => new InstallPlan { Kind = InstallKind.None }
    };

    /// <summary>
    /// CODEX'S DOWNLOAD FOR THE COMPUTER <paramref name="on"/> DESCRIBES.
    ///
    /// <para><b>Windows</b>, as it always was: OpenAI's own installer asserts <c>bin/codex.exe</c> after
    /// unpacking the same archive.</para>
    ///
    /// <para><b>Linux</b>: <c>codex-package-{x86_64|aarch64}-unknown-linux-musl.tar.gz</c> — statically linked, so
    /// it runs on any distribution; 162,956,804 B for x86_64 at rust-v0.162.0, read through GitHub's API on
    /// 2026-10-08 and again on 2026-10-09. Its layout is the vendor's <c>scripts/codex_package/layout.py</c> at
    /// that tag — the entry point at <c>bin/codex</c>, beside <c>codex-resources/</c> and <c>codex-path/</c> — and
    /// its <c>scripts/install/install.sh</c> runs <c>bin/codex</c> from the unpacked release.</para>
    ///
    /// <para><b>Anywhere else</b> there is no plan, and the install says so.</para>
    /// </summary>
    static InstallPlan CodexInstall(HostPlatform on) => on switch
    {
        { IsWindows: true } => new InstallPlan
        {
            Kind = InstallKind.Download,
            GitHubRepo = "openai/codex",
            AssetPattern = @"^codex-package-x86_64-pc-windows-msvc\.tar\.gz$",
            ExecutableInArchive = "bin/codex.exe",
            Url = "https://github.com/openai/codex/releases/latest/download/codex-package-x86_64-pc-windows-msvc.tar.gz",
            NpmPackage = "@openai/codex",
            ManualUrl = "https://developers.openai.com/codex/cli/"
        },
        { Os: HostOs.Linux, Arch: Architecture.X64 or Architecture.Arm64 } => new InstallPlan
        {
            Kind = InstallKind.Download,
            GitHubRepo = "openai/codex",
            AssetPattern = $@"^codex-package-{LinuxCpu(on, "x86_64", "aarch64")}-unknown-linux-musl\.tar\.gz$",
            ExecutableInArchive = "bin/codex",
            Url = $"https://github.com/openai/codex/releases/latest/download/codex-package-{LinuxCpu(on, "x86_64", "aarch64")}-unknown-linux-musl.tar.gz",
            NpmPackage = "@openai/codex",
            ManualUrl = "https://developers.openai.com/codex/cli/"
        },
        _ => new InstallPlan { Kind = InstallKind.None }
    };

    /// <summary>The vendor's own word for a Linux computer's processor.</summary>
    static string LinuxCpu(HostPlatform on, string x64, string arm64) => on.Arch == Architecture.Arm64 ? arm64 : x64;

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
    /// THE MANIFESTS THAT ARE HARNESSES — the ones TradeAgent runs itself rather than starting a
    /// vendor program. It is exactly the entries that carry a <see cref="RuntimeManifest.BaseUrl"/>,
    /// because that is what makes one: a runtime with an endpoint has no executable, no install and no
    /// sign-in command, and every model-and-tool boundary in its turns is this app's.
    ///
    /// Derived rather than flagged, so an override that adds a provider gets the same treatment
    /// without also having to remember a boolean.
    /// </summary>
    public static IReadOnlyList<RuntimeManifest> Harnesses() =>
        [.. Read().Runtimes.Where(m => m.Endpoint.Length > 0)];

    /// <summary>Whether an id names a harness. Null and unknown ids are not.</summary>
    public static bool IsHarness(string? id) =>
        id is { Length: > 0 } && Find(id) is { } m && m.Endpoint.Length > 0;

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

/// <summary>The operating systems a built-in manifest can describe.</summary>
public enum HostOs { Windows, Linux, MacOS, Other }

/// <summary>
/// THE COMPUTER A BUILT-IN MANIFEST DESCRIBES: its operating system and its processor. The product asks for
/// <see cref="Current"/>; a test names another, so the Linux plans are proven on every runner and the Windows
/// plans stay exactly what they were.
/// </summary>
public sealed record HostPlatform(HostOs Os, Architecture Arch)
{
    public static HostPlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsLinux() ? HostOs.Linux
        : OperatingSystem.IsMacOS() ? HostOs.MacOS
        : HostOs.Other,
        RuntimeInformation.OSArchitecture);

    public static HostPlatform WindowsX64 { get; } = new(HostOs.Windows, Architecture.X64);
    public static HostPlatform LinuxX64 { get; } = new(HostOs.Linux, Architecture.X64);
    public static HostPlatform LinuxArm64 { get; } = new(HostOs.Linux, Architecture.Arm64);

    public bool IsWindows => Os == HostOs.Windows;
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
    /// <param name="requestedModel">
    /// THE MODEL TRADEAGENT PUT ON THE COMMAND LINE, where it put one. It is a stronger claim than
    /// the dearest-in-the-catalogue fallback and a weaker one than a model the runtime named itself,
    /// so it sits between them — and the figure is still labelled, because the vendor could serve
    /// something else and no event in the measured stream would say so.
    /// </param>
    public static TurnPrice Price(TurnUsage? usage, string? runtimeId, CostCatalogRead? catalogue = null,
        OwnerPrice? owner = null, string? requestedModel = null) =>
        Compute(usage is null ? null : new Bill(usage), usage?.Model, runtimeId, catalogue, owner,
            requestedModel, "the AI tool did not report how many tokens the turn used");

    /// <summary>
    /// WHAT ONE TURN MAY COST AT MOST, on the same model resolution the bill above uses — the number
    /// committed before the process starts.
    ///
    /// <para><b>The formula, stated once here and in <c>CONTRACTS.md</c> and the guide:</b> the
    /// allowance's input tokens at the DEARER of the uncached and cache-write rates, plus its output
    /// tokens at the output rate, reasoning inside output.</para>
    ///
    /// <para><b>Why the dearer of the two.</b> A reservation is an enforceable BOUND, and a bound
    /// that assumed the cheaper of two rates the same tokens can be billed at is not one: a vendor
    /// whose cache writes cost more than plain input would bill a turn above its own reservation,
    /// and the ceiling would be walked past by the difference. The direction is the whole design —
    /// over-reserving costs a turn the owner gets back at midnight, under-reserving is a cap that
    /// does not hold. It is a maximum rather than a sum because the same input tokens are charged at
    /// one of the two rates and not at both; adding them would reserve twice the input a turn can
    /// have and make the shipped ceiling unable to fund a single turn.</para>
    ///
    /// <para>The owner's own two numbers have no cache-write rate — they gave one input rate, and
    /// <see cref="Bill"/> charges cache writes at it too — so for them the maximum is that rate.</para>
    /// </summary>
    public static TurnPrice Reserve(TurnAllowance allowance, string? runtimeId,
        CostCatalogRead? catalogue = null, OwnerPrice? owner = null, string? requestedModel = null) =>
        Compute(new Bound(allowance), null, runtimeId, catalogue, owner, requestedModel, "");

    /// <summary>
    /// THE MODEL RESOLUTION BOTH QUESTIONS SHARE, with the arithmetic as the parameter.
    ///
    /// One method rather than two because "which model, and at whose rate" is the part that has to
    /// agree: a reservation resolved against one model and a bill against another would commit
    /// against a rate nobody is charged, in whichever direction the two happened to differ.
    /// </summary>
    /// <param name="charge">The arithmetic — a bill over reported tokens, or a bound over an allowance.</param>
    /// <param name="namedModel">The model the RUNTIME'S OWN STREAM named, where it named one.</param>
    static TurnPrice Compute(Charge? charge, string? namedModel, string? runtimeId,
        CostCatalogRead? catalogue, OwnerPrice? owner, string? requestedModel, string whenNothingToCharge)
    {
        var read = catalogue ?? Read();
        if (read.Unreadable is { } why) return TurnPrice.Unknown(why);

        var costs = read.Costs ?? BuiltIn();
        if (charge is null)
            return TurnPrice.Unknown(whenNothingToCharge);

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
            return new TurnPrice(charge.Of(owner), costs.Currency, null, null, ByOwner: true)
                { Basis = OwnerBasis };

        var model = namedModel ?? Declared(costs, runtimeId);

        // THE MODEL TRADEAGENT ASKED FOR, when nothing better is available. Codex 0.153.4 names no
        // model in any of its events even with `-m` on the command line — measured twice, on
        // 2026-09-06 and again on 2026-09-07 — so on the runtime this build recommends the requested
        // model is the only identification there is, and it is a real one: the app wrote it.
        //
        // `askedFor` is remembered separately because it decides the LABEL. A figure priced this way
        // is not a bill, and the sentence says which model it is a list price for.
        var askedFor = model is null && requestedModel is { Length: > 0 } ? requestedModel : null;
        model ??= askedFor;

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
                ? new TurnPrice(charge.Of(highest), costs.Currency, null, Labels.PricedAtHighestListPrice)
                    { Basis = BasisOf(highest) }
                : TurnPrice.Unknown(
                    $"the AI tool did not say which model it used, and {Labels.CostsFile} does not name one for it");

        var price = Applicable(costs, runtimeId)
            .FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));
        if (price is null)
        {
            // A model TRADEAGENT asked for that this build ships no price for — gpt-5.3-codex-spark
            // is the live example — falls back to the dearest in the catalogue rather than becoming
            // unpriced. Over-charging can only stop the AI early; unpriced turns cannot reach the
            // ceiling at all, which is the one direction nothing undoes.
            if (askedFor is not null && Highest(costs, runtimeId) is { } fallback)
                return new TurnPrice(charge.Of(fallback), costs.Currency, null,
                    Labels.PricedAtHighestListPrice) { Basis = BasisOf(fallback) };

            return TurnPrice.Unknown($"{Labels.CostsFile} has no price for {model}");
        }

        return new TurnPrice(charge.Of(price), costs.Currency, null,
            askedFor is null ? null : Labels.PricedAtTheModelAskedFor(askedFor)) { Basis = BasisOf(price) };
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
    /// TOKENS INTO MONEY — the one part of pricing that differs between a bill and a bound, and the
    /// reason <see cref="Compute"/> takes it as a parameter instead of existing twice.
    /// </summary>
    abstract class Charge
    {
        public abstract decimal Of(ModelPrice price);
        public abstract decimal Of(OwnerPrice owner);
    }

    /// <summary>
    /// WHAT A TURN ACTUALLY COST, over the tokens the runtime reported. Cached input is a SUBSET of
    /// the input count and reasoning output a subset of the output count, so each is billed exactly
    /// once — see <see cref="TurnUsage"/>, where both subsets were measured rather than assumed.
    ///
    /// The owner's own two numbers charge CACHED INPUT AT THE FULL INPUT RATE, because they gave a
    /// rate for input and did not give a discount for cache reads, and inventing one would
    /// under-charge every turn — the direction that lets the cap be walked past. An owner whose
    /// vendor discounts cache reads has <c>costs.json</c>, which takes all four figures.
    /// </summary>
    sealed class Bill(TurnUsage usage) : Charge
    {
        public override decimal Of(ModelPrice price) =>
            (usage.UncachedInputTokens * price.InputPerMillion +
             usage.CachedInputTokens * (price.CachedInputPerMillion ?? price.InputPerMillion) +
             usage.CacheWriteInputTokens * (price.CacheWritePerMillion ?? price.InputPerMillion) +
             usage.OutputTokens * price.OutputPerMillion) / 1_000_000m;

        public override decimal Of(OwnerPrice owner) =>
            (usage.InputTokens * owner.InputPerMillion +
             usage.CacheWriteInputTokens * owner.InputPerMillion +
             usage.OutputTokens * owner.OutputPerMillion) / 1_000_000m;
    }

    /// <summary>
    /// WHAT A TURN MAY COST AT MOST. See <see cref="Reserve"/> for why the input rate is the DEARER
    /// of the two the same tokens can be billed at, and why it is a maximum rather than a sum.
    /// </summary>
    sealed class Bound(TurnAllowance allowance) : Charge
    {
        public override decimal Of(ModelPrice price) =>
            (allowance.InputTokens * Math.Max(price.InputPerMillion,
                                              price.CacheWritePerMillion ?? price.InputPerMillion) +
             allowance.OutputTokens * price.OutputPerMillion) / 1_000_000m;

        public override decimal Of(OwnerPrice owner) =>
            (allowance.InputTokens * owner.InputPerMillion +
             allowance.OutputTokens * owner.OutputPerMillion) / 1_000_000m;
    }

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
