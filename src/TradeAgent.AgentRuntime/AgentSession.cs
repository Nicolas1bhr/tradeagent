using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;

namespace TradeAgent.AgentRuntime;

public enum ChatRole { You, Ai, Tool, System }

public sealed record ChatTurn(ChatRole Role, string Text, DateTimeOffset At);

/// <summary>
/// The conversation with the AI, hosted by TradeAgent itself.
///
/// This is what replaces the console window. Nothing here shows a terminal, and there is no
/// interactive child process the user could be looking at: one message is one non-interactive run of
/// the CLI, and the run's machine-readable event stream is turned into turns the window draws.
///
/// Events can be raised from a worker thread. A UI consumer must marshal them onto its own thread.
/// </summary>
public interface IAgentConversation
{
    bool Busy { get; }
    IReadOnlyList<ChatTurn> History { get; }

    /// <summary>A complete turn was appended to <see cref="History"/>.</summary>
    event Action<ChatTurn>? TurnAdded;

    /// <summary>Streaming text for the AI turn currently in flight.</summary>
    event Action<string>? Delta;

    /// <summary><see cref="Busy"/> flipped.</summary>
    event Action? StateChanged;

    /// <summary>
    /// One run of the CLI finished, however it finished. Raised for every turn — the owner's and the
    /// mission's alike — and carrying only what was measured. `U-meter` prices turns from these.
    /// </summary>
    event Action<AgentTurnEnded>? TurnEnded;

    Task StartAsync(CancellationToken ct = default);
    Task SendAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Runs one turn on the mission's behalf. Identical to <see cref="SendAsync"/> except that the
    /// message is not shown as something the owner said, because nobody said it: the Situation block
    /// is written by the app. The reply and the tool activity appear exactly as they do for a typed
    /// message, so the Chat page still shows everything the AI does while it works on its own.
    /// </summary>
    Task SendMissionAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Carries one message to the top of the AI's NEXT turn instead of running a turn for it now,
    /// and shows it in the conversation as the owner's. This is what <see cref="SendAsync"/> does
    /// with anything typed while a turn is already in flight.
    /// </summary>
    void Queue(string message);

    /// <summary>
    /// Takes, and clears, what the owner typed while a turn was already running. The mission loop
    /// puts these at the top of the next turn's Situation; nothing else reads them, and a message
    /// taken here has already been shown in the conversation as theirs.
    /// </summary>
    IReadOnlyList<string> TakeTyped();

    Task CancelAsync();
    Task StopAsync();

    /// <summary>
    /// THE SAME STOP, ORDERED BY THE LOOP'S ROTATION, WITH WHY (<c>U-org-packets-a</c>; <see cref="SessionWhy"/>): the session is
    /// forgotten exactly as <see cref="StopAsync()"/> forgets it, and the next turn's record says why it is fresh. The default is
    /// the plain stop, for a conversation that records no session.
    /// </summary>
    Task StopAsync(string why) => StopAsync();
}

/// <summary>
/// What the user has to do to finish signing in, produced by running the runtime's own login command
/// headless and reading the URL out of its output. TradeAgent opens the URL; the user never sees a
/// console and is never asked for a key.
/// </summary>
public sealed record AuthChallenge(string? Url, string? Code, string Message);

/// <summary>
/// Strips terminal colour codes.
///
/// These CLIs are written for a terminal and dress their output up even when nobody is watching:
/// sign-in URLs arrive wrapped in blue, credential lists in grey. A regex looking for a URL or a
/// number finds the escape bytes instead, so they come off before anything is matched.
/// </summary>
internal static partial class Ansi
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Sequence();

    public static string Strip(string text) =>
        text.Contains('\x1B') ? Sequence().Replace(text, "") : text;
}

/// <summary>
/// Turns an argument template into a real argument list.
///
/// The unattended flags, the stream flag and the model flag have to land before the prompt, because
/// every CLI here treats the first non-flag word after the subcommand as the prompt.
///
/// <b>Public, and <see cref="For"/> with it.</b> The argv is a rule rather than a detail — the model
/// TradeAgent chose has to reach the RESUMED turn as well as the first one, and nineteen turns in
/// twenty are resumes — and a rule that can only be checked by starting a vendor's CLI is a rule
/// nobody is checking.
/// </summary>
public static class AgentArgs
{
    /// <summary>What an attempt records when its prompt travelled on the runtime's stdin (<c>U-prompt-stdin</c>).</summary>
    public const string OnStdin = "stdin";

    /// <summary>
    /// What an attempt records when its runtime cannot read stdin and the prompt rode on the command line, where every process
    /// of the user can read it. Today a custom manifest's legacy path; a gate that must not run such a seat reads
    /// <see cref="RuntimeManifest.PromptOnStdin"/>.
    /// </summary>
    public const string InArgv = "prompt in argv (runtime cannot read stdin)";

    /// <summary>
    /// THE ARGV FOR ONE TURN, first message or resumed, from one method so the two cannot drift.
    ///
    /// <paramref name="resuming"/> picks the template and NOTHING ELSE: every flag below is chosen
    /// the same way for both, which is the whole point. A resumed session does not remember which
    /// model it was run with — it is a fresh process reading a transcript — so a build that put the
    /// flag on the first turn only would run one turn on the model TradeAgent chose and every turn
    /// after it on whatever the CLI's own config file says.
    ///
    /// <para><b>The prompt is not on it</b> for a runtime that reads stdin (<see cref="RuntimeManifest.PromptOnStdin"/>,
    /// <c>U-prompt-stdin</c>): its slot carries <see cref="RuntimeManifest.StdinMarker"/> and the turn writes the prompt to the
    /// child's stdin, so the argv a seat is verified on at its admission is the argv it starts with.</para>
    /// </summary>
    public static List<string> For(RuntimeManifest manifest, string prompt, bool resuming, string? model) =>
        For(manifest, prompt, resuming, model, out _);

    /// <param name="promptInArgv">Whether the command line built carries the prompt — measured on it, see <see cref="Build"/>.</param>
    public static List<string> For(RuntimeManifest manifest, string prompt, bool resuming, string? model, out bool promptInArgv) =>
        For(manifest, prompt, resuming, model, SeatPlan.CliClass, null, out promptInArgv);

    /// <summary>
    /// THE ARGV FOR ONE TURN OF ONE SEAT CLASS (<c>U-thinking-seats-c</c>): the same builder, with <paramref name="seatClass"/>'s deny
    /// set and the app's door, <paramref name="door"/>, in its slot.
    /// </summary>
    public static List<string> For(RuntimeManifest manifest, string prompt, bool resuming, string? model, string seatClass, Uri? door,
        out bool promptInArgv)
    {
        var template =
            Resumes(manifest, resuming) ? manifest.ResumeArgs :
            manifest.ExecArgs.Length > 0 ? manifest.ExecArgs :
            manifest.TaskArgs;

        var streaming = !string.IsNullOrWhiteSpace(manifest.JsonFlag);
        return Build(template, prompt, streaming ? manifest.JsonFlag : null, manifest.UnattendedArgs,
            ModelArgs(manifest, model), [.. SeatArgs(manifest, seatClass, door), .. manifest.TestHostProviderArgs], manifest.PromptOnStdin,
            manifest.StdinMarker, out promptInArgv);
    }

    /// <summary>
    /// Whether a turn asked to resume launches the resume template — only where the runtime declares one; a runtime with none
    /// launches the fresh template every turn (fix pass 1 of <c>U-org-packets-a</c>: the record says what launched).
    /// </summary>
    public static bool Resumes(RuntimeManifest manifest, bool resuming) => resuming && manifest.ResumeArgs.Length > 0;

    /// <summary>
    /// Whether this runtime's command line carries the prompt, fresh or resumed — measured on the argv this build makes for it, so
    /// a line that says so (the agent runtime's row, <c>U-prompt-stdin</c>) says what the launch does, not what the manifest claims.
    /// </summary>
    public static bool CarriesPrompt(RuntimeManifest manifest)
    {
        For(manifest, "", resuming: false, model: null, out var fresh);
        For(manifest, "", resuming: true, model: null, out var resumed);
        return fresh || resumed;
    }

    /// <summary>
    /// THE SEAT'S DENY SET, on the first turn and on every resume alike (<c>U-seat-home</c>), or nothing for a runtime with no
    /// seat profile: <paramref name="seatClass"/>'s, the app's door in its slot (<c>U-thinking-seats-c</c>).
    /// </summary>
    public static string[] SeatArgs(RuntimeManifest manifest, string seatClass = SeatPlan.CliClass, Uri? door = null) =>
        manifest.Seat?.ArgsFor(seatClass, door) ?? [];

    /// <summary>
    /// The model flag as this runtime spells it, or nothing at all. Nothing when the runtime has no
    /// such flag, and nothing when no model was named: an empty <c>-m</c> would be a CLI refusing to
    /// start, which is a worse failure than a model TradeAgent did not choose.
    /// </summary>
    static string[] ModelArgs(RuntimeManifest manifest, string? model) =>
        manifest.ModelArgs.Length == 0 || model is not { Length: > 0 }
            ? []
            : [.. manifest.ModelArgs.Select(a => a.Replace("{model}", model, StringComparison.Ordinal))];

    /// <param name="promptOnStdin">
    /// THE PROMPT TRAVELS ON STDIN when this is true (<c>U-prompt-stdin</c>), and then it is NEVER on the command line: the slot
    /// carries <paramref name="stdinMarker"/>, or nothing at all when there is none — empty and null alike, since an override file
    /// can write an explicit null into it — and the caller writes the prompt to the child's stdin. False is a runtime that cannot
    /// read stdin, whose prompt is the slot, as it always was, and whose attempts say so.
    /// </param>
    /// <param name="promptInArgv">
    /// WHETHER THE COMMAND LINE BUILT CARRIES THE PROMPT, MEASURED ON IT rather than read off the manifest: the same command built
    /// for a prompt one character longer, compared word for word. A line that does not change with the prompt does not carry it;
    /// one that does, does. What an attempt records and whether its prompt is written to stdin both follow this.
    /// </param>
    public static List<string> Build(string[] template, string prompt, string? jsonFlag, string[] unattended,
        string[]? modelArgs, string[]? seatArgs, bool promptOnStdin, string? stdinMarker, out bool promptInArgv)
    {
        var args = Assemble(template, prompt, jsonFlag, unattended, modelArgs, seatArgs, promptOnStdin, stdinMarker);
        promptInArgv = !args.SequenceEqual(
            Assemble(template, prompt + "\0", jsonFlag, unattended, modelArgs, seatArgs, promptOnStdin, stdinMarker), StringComparer.Ordinal);
        return args;
    }

    static List<string> Assemble(string[] template, string prompt, string? jsonFlag, string[] unattended,
        string[]? modelArgs, string[]? seatArgs, bool promptOnStdin, string? stdinMarker)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(jsonFlag))
            flags.AddRange(jsonFlag.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        flags.AddRange(unattended.Where(a => !string.IsNullOrEmpty(a)));
        if (seatArgs is not null) flags.AddRange(seatArgs.Where(a => !string.IsNullOrEmpty(a)));
        if (modelArgs is not null) flags.AddRange(modelArgs.Where(a => !string.IsNullOrEmpty(a)));

        // THE ONE DECISION, MADE ONCE: what stands where the message goes. On stdin, the marker or nothing — never the prompt.
        var marker = promptOnStdin ? (string.IsNullOrEmpty(stdinMarker) ? [] : new[] { stdinMarker }) : null;

        var promptIndex = Array.FindIndex(template, a => a.Contains("{prompt}", StringComparison.Ordinal));
        var args = new List<string>();

        for (var i = 0; i < template.Length; i++)
        {
            if (i == promptIndex) args.AddRange(flags);
            if (marker is null) args.Add(template[i].Replace("{prompt}", prompt));
            // THE SLOT IS THE MARKER, WHOLE: never the prompt, and never a template word with the prompt spliced into it.
            else if (i != promptIndex) args.Add(template[i]);
            else args.AddRange(marker);
        }

        // A template with no placeholder still has to carry the message somewhere — or say where it went instead.
        if (promptIndex < 0)
        {
            args.AddRange(flags);
            if (marker is null) args.Add(prompt);
            else args.AddRange(marker);
        }

        return args;
    }
}

/// <summary>
/// One conversation, backed by the CLI's non-interactive execution mode.
///
/// The first message starts a session; every later message resumes it with the manifest's
/// <see cref="RuntimeManifest.ResumeArgs"/>. When the manifest declares a stream flag, stdout is
/// parsed line by line so assistant text appears while it is being written and tool activity — the
/// AI checking a price, placing an order — is visible as it happens rather than after the fact.
/// </summary>
/// <param name="presence">
/// The register a running turn reports itself to. Null is the process-wide one and is what the
/// product always uses; only a test that starts a child in the agent's role passes its own.
/// </param>
/// <param name="model">
/// THE MODEL TRADEAGENT ASKS FOR, read at the start of every turn rather than captured once, so an
/// owner who changes it on the Safety page is obeyed by the next turn and not by the next restart.
/// Null, or a null answer, means no model flag goes on the command line.
/// </param>
/// <param name="role">
/// Which council role's launches these are. It is what the launch grant NAMES, so it decides what
/// the process may ask the gateway for — the chair may move money, Research may not — and it is
/// TradeAgent's own answer rather than anything the agent says about itself.
/// </param>
/// <param name="attempt">
/// The AI attempt the meter opened for the launch about to happen, or null when nothing opened one.
/// Stamped on the grant so the gateway can record which attempt a request came from.
/// </param>
/// <param name="grants">
/// Where launch grants are minted. The process-wide register by default — the same one the pipe
/// server checks against.
/// </param>
/// <param name="launchRefusal">
/// THE PROTECTED CONFIGURATION, asked at every launch: a sentence when this build will not start the
/// vendor's CLI at all, null when it will. A function because the owner arms and disarms real money
/// while the AI is working, and the turn that has to obey is the next one.
/// </param>
/// <param name="seats">
/// The activation gate's register, asked immediately before the process starts (<see cref="PositionActivation.AtLaunch"/>). Null
/// is the process-wide one, which the product fills; a test's host passes its own.
/// </param>
/// <param name="door">
/// THE APP'S DOOR (<c>AppHost.DoorUrl</c>; <c>U-thinking-seats-b</c>), read at every launch: a broker-only seat's one tool server,
/// written into its deny set's slot (<see cref="SeatPlan.DoorSlot"/>). Null, or a null answer, refuses a broker seat.
/// </param>
/// <param name="surfaceProbe">
/// What reads the tools a broker seat would be offered: the real dry run (<see cref="CodexSurfaceProbe"/>) unless a test passes a fake.
/// </param>
public sealed class AgentSession(
    RuntimeManifest manifest,
    Func<string?> resolveExecutable,
    Func<string> workspace,
    Func<IReadOnlyDictionary<string, string>> environment,
    AgentPresence? presence = null,
    Func<string?>? model = null,
    string role = CouncilRoles.Operations,
    Func<string?>? attempt = null,
    AgentGrants? grants = null,
    Func<string?>? launchRefusal = null,
    AppFileManifest? appFiles = null,
    ISeatProbe? seatProbe = null,
    SeatRegister? seats = null,
    Func<Uri?>? door = null,
    ISurfaceProbe? surfaceProbe = null,
    CellRoute? cells = null) : IAgentConversation, IAdmittedConversation, ISeatLaunch, ILaunchGate
{
    /// <summary>The CLI asked about its own seat: the real one unless a test passed a fake (<c>U-seat-home</c>).</summary>
    ISeatProbe SeatProbe => seatProbe ?? CliSeatProbe.Instance;

    /// <summary>
    /// THE CLASS OF THE SEAT THIS SESSION STARTS (<see cref="RuntimeManifest.SessionSeatClassFor"/>): <c>broker</c> for a position
    /// beyond the legacy pair on a runtime with a broker class, a test host's puppet's declared class, otherwise <c>cli</c> — and
    /// <c>cell</c> for an executor, a Member beyond the pair (<c>U-execution-environment</c>): its kind read off the chart, through the
    /// gate's own register, where asked.
    /// </summary>
    public string SeatClass => manifest.SessionSeatClassFor(role, (seats ?? PositionActivation.Seats).KindOf(role));

    bool Broker => BrokerSeat.IsVerified(SeatClass);

    /// <summary>The door's address now, or null — a read that throws is no door.</summary>
    Uri? Door
    {
        get
        {
            try { return door?.Invoke(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// THE MODEL THE NEXT LAUNCH NAMES ON ITS COMMAND LINE: a broker seat's pin, whatever its account defaults to; otherwise the model
    /// TradeAgent asks for (<see cref="RequestedModel"/>).
    /// </summary>
    string? LaunchModel => Broker ? BrokerSeat.ModelOf(SeatClass) : RequestedModel;

    /// <summary>
    /// HOW THIS LAUNCH'S SEAT IS VERIFIED: its class, the door, and — for a broker seat — the owner's own choice of model for the
    /// position, which refuses when it names another model than the pin.
    /// </summary>
    SeatLaunch Launch => new(SeatClass, Door, Broker ? RequestedModel : null, surfaceProbe);

    /// <inheritdoc />
    public SeatVerdict CheckSeat()
    {
        // A PUPPET OUTSIDE A TEST HOST IS REFUSED HERE TOO, so the meter writes no row and reserves nothing for it (U-puppet-runtime).
        if (PuppetSeats.Refusal(manifest) is { } puppet) return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, puppet);
        // A CLI SEAT, WITH A SEAT PLAN OR WITHOUT ONE (U-thinking-seats-a): every session here starts its process with a shell and
        // outside any cell, so its class is `cli` and its assurance unconfined — or, for a test host's puppet, the class its test
        // declared (fix pass 2, item 7) — the app's own answer in code, never a field of the manifest's file.
        // A BROKER SEAT IS ADMITTED ONLY VERIFIED (U-thinking-seats-c): its mediated assurance is a claim about a profile, a door and an
        // offered surface the app has checked, never one about a manifest with nothing to check.
        var seatClass = SeatClass;
        if (manifest.Seat is null && !BrokerSeat.IsVerified(seatClass)) return Seated;
        // NOTHING TO CHECK, AND NOTHING TO START: the turn itself says the runtime is not installed.
        if (resolveExecutable() is not { } exe)
            return BrokerSeat.IsVerified(seatClass)
                ? SeatVerdict.Refuse(ErrorCode.AI_RUNTIME_NOT_FOUND, $"{manifest.DisplayName} is not installed, so no {seatClass} seat can start.")
                : Seated;
        var launch = Launch;
        var argv = AgentArgs.For(manifest, "", resuming: Resumes(seatClass), model: LaunchModel, seatClass, launch.Door, out _);
        var home = workspace();
        try
        {
            // ON THE POOL, because the meter that asks is synchronous and may be on the window's thread.
            var verdict = Task.Run(() => SeatProfile.VerifyAsync(manifest, exe, argv, home, SeatProbe, launch, CancellationToken.None))
                .GetAwaiter().GetResult();
            // THE PROFILE THE ROW WILL CARRY, held for the start to compare against what it verifies itself.
            Volatile.Write(ref _admittedProfile, verdict.Profile);
            return verdict.Refused ? verdict : verdict with { SeatClass = seatClass };
        }
        catch (Exception ex)
        {
            return SeatVerdict.Refuse(ErrorCode.AI_SEAT_REFUSED, $"TradeAgent could not check the AI's own settings: {ex.Message}");
        }
    }

    /// <summary>The verdict on this session's seat with nothing to verify: nothing refused, and its class.</summary>
    SeatVerdict Seated => SeatVerdict.None with { SeatClass = SeatClass };

    /// <summary>
    /// WHY A LAUNCH OF THIS ROLE MAY NOT START NOW, or null (<c>U-grant-tree</c>) — the look the mission loop and the owner's
    /// chat take before anything is admitted, so a refused launch writes no row and consumes no wake. Lasting when this
    /// computer cannot hold the launch at all (RULING 2); passing while the Operations Director's turn holds order
    /// permission (ORDER-001). The launch itself meets the same two refusals as hard lines.
    /// </summary>
    public LaunchBar? Barred()
    {
        if (ProcessContainment.CannotHold() is { } unheld) return new LaunchBar(unheld, Lasting: true);
        // AN EXECUTOR WHOSE CELL CANNOT START IS REFUSED HERE, BEFORE ANYTHING IS WRITTEN (U-execution-environment item 2;
        // ORCHESTRATION § 2.3): no counted proof, one that did not measure the cell's egress, a profile that changed, a platform no
        // cell runs on — in RouteA.Admission's own words, lasting until a start measures a proof, its wakes kept. NEVER RE-DECLARED:
        // nothing here or anywhere moves a Member to another class; the launch itself meets the same admission as its hard line.
        if (CellRefusal() is { } cell) return new LaunchBar(cell, Lasting: true);
        return (grants ?? AgentGrants.Shared).HeldBy(role) is { } held ? new LaunchBar(held, Lasting: false) : null;
    }

    /// <summary>
    /// PUTS THE APP'S CANON BACK in <paramref name="home"/>, an executor's — a folder its cell writes whole, so every name in it is the
    /// executor's to have planted (<c>U-execution-environment</c> item 3; review fix pass R1–R3). This runs in the UNCONFINED app, so:
    /// <list type="bullet">
    /// <item>NEVER THROUGH A LINK (R1): whatever lies at the canon's name — a file, a symbolic link, anything but a folder — is unlinked
    /// without being followed or read, and the canon is created fresh with exclusive-create semantics (<c>O_CREAT|O_EXCL</c>, which follows
    /// no link), so a link the cell aimed outside its home can never have the app write there.</item>
    /// <item>NOTHING THAT OUTRANKS IT (R2): codex 0.160.1 reads <see cref="Canon.OverrideFile"/> IN PLACE OF <see cref="Canon.CliFile"/>
    /// in the same folder (the class's config names no fallback file, and the cell cannot write the harness's codex home), so it is
    /// unlinked the same way before the turn.</item>
    /// <item>RESTORED OR REFUSED (R3): a name that cannot be unlinked or created — a folder planted there, a write that fails — refuses the
    /// launch in words (<see cref="ErrorCode.CELL_UNAVAILABLE"/>), its wakes kept; the turn never runs under a canon the app did not
    /// write.</item>
    /// </list>
    /// With nothing kept (<see cref="AppFileManifest.Kept"/>) — no start wrote this home's canon in this process — the override is still
    /// removed, the canon's name is left as it lies, and the attempt's record says what the turn read (<see cref="Canon.SeenByCli"/>).
    /// </summary>
    void RestoreCanon(string home)
    {
        var files = appFiles ?? AppFileManifest.Shared;
        var text = files.Kept(AppFileManifest.Key(CouncilRoles.HomeDir(role), Canon.CliFile));
        // ONE BODY FOR EVERY APP-SIDE WRITE INTO A HOME A CELL WRITES (fix pass 3): HomeFiles unlinks the name itself, never what it
        // points at, and creates the canon exclusively; a folder at the name refuses, never walked into.
        try
        {
            HomeFiles.Remove(home, Canon.OverrideFile);
            if (text is null) return;
            HomeFiles.Write(home, Canon.CliFile, Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                $"TradeAgent did not start the AI as a cell seat: it could not put its own canon back in the position's folder ({ex.Message}). " +
                "Nothing was started; the next turn tries again.", ex);
        }
    }

    /// <summary>
    /// Why this session's cell seat may not start now (<see cref="RouteA.Admission"/> under <see cref="RouteA.Egress"/>), or null — and
    /// always null for a seat of any other class. A read that throws is a refusal, never a pass.
    /// </summary>
    string? CellRefusal()
    {
        if (SeatClass != SeatPlan.CellClass) return null;
        try { return RouteA.Admission(cells, manifest.Install.ExecutableSha256 ?? "", CellPlatform.Read(), RouteA.Egress).Refusal; }
        catch (Exception ex) { return $"TradeAgent did not start the AI as a cell seat: it could not read its record of proven cells ({ex.Message})."; }
    }

    /// <summary>
    /// THE NEXT LAUNCH'S GRANT, ISSUED BEFORE ITS ADMISSION and held until <see cref="RunTurnAsync"/> takes it or
    /// <see cref="Unclaim"/> gives it back (<c>U-grant-tree</c> fix pass 1). One at a time: a second claim while one is held
    /// is a launch of this role already being prepared.
    /// </summary>
    (AgentGrant Grant, PreparedHold Hold)? _claim;
    readonly Lock _claimGate = new();

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>WHY BEFORE ADMISSION.</b> The look in <see cref="Barred"/> is only a look: an Operations launch issued after
    /// it and before this launch's grant used to leave the register refusing this launch only once the ledger had already
    /// written its row, reserved its spend and consumed its wakes — none of which a launch that never starts may do, and
    /// none of which can be undone in an append-only ledger. So the grant itself is issued here, under the register's lock,
    /// before the caller asks the ledger; <see cref="AgentGrants.Issue"/>'s refusal arrives while nothing has been written.</para>
    /// <para><b>AND WHAT WILL HOLD IT, MADE HERE TOO</b> (RULING 2): the Windows job is created with the claim, so a job Windows
    /// will not make is a lasting refusal while nothing has been written — never a reservation charged and a wake consumed
    /// for a launch that then could not start.</para>
    /// </remarks>
    public LaunchBar? Claim(out string? ordersWithheld)
    {
        ordersWithheld = null;
        var register = grants ?? AgentGrants.Shared;
        lock (_claimGate)
        {
            if (_claim is not null) return new LaunchBar(Labels.HeldWhileTheAiIsWorking, Lasting: false);
            PreparedHold hold;
            try { hold = ProcessContainment.Prepare(); }
            catch (TradeAgentException ex) when (ex.Code == ErrorCode.CONTAINMENT_UNAVAILABLE) { return new LaunchBar(ex.Message, Lasting: true); }
            AgentGrant grant;
            try { grant = register.Issue(role, ""); }
            catch (TradeAgentException ex) when (ex.Code == ErrorCode.LAUNCH_HELD)
            {
                hold.Dispose();
                return new LaunchBar(ex.Message, Lasting: false);
            }
            _claim = (grant, hold);
            ordersWithheld = grant.OrdersWithheld;
            return null;
        }
    }

    /// <inheritdoc />
    public void Unclaim()
    {
        if (TakeClaim() is not { } claim) return;
        // NO PROCESS EVER HELD IT: revoked outright, never given the grace a finished turn's grant has; its job closed.
        (grants ?? AgentGrants.Shared).Revoke(claim.Grant.Token);
        claim.Hold.Dispose();
    }

    /// <summary>The held claim, taken for the launch about to start, or null when nothing claimed one.</summary>
    (AgentGrant Grant, PreparedHold Hold)? TakeClaim()
    {
        lock (_claimGate)
        {
            var claim = _claim;
            _claim = null;
            return claim;
        }
    }

    /// <summary>
    /// THE CANON THE TURN IN FLIGHT WAS LAUNCHED UNDER, read in <see cref="RunTurnAsync"/> before the process starts and
    /// carried out on <see cref="TurnEnded"/>. Null until a turn launches; reset with every turn.
    /// </summary>
    CanonSeen? _canon;

    /// <summary>How the turn in flight handed over its prompt (<see cref="AgentTurnEnded.PromptIn"/>); null until it launches.</summary>
    string? _promptIn;

    /// <summary>The runtime profile the last admission verified, taken by the start that follows it (<c>U-seat-home</c>).</summary>
    string? _admittedProfile;

    /// <summary>The runtime profile the CLI session this conversation resumes was opened under, or null for none yet.</summary>
    string? _sessionProfile;

    readonly List<ChatTurn> _history = [];
    readonly Lock _historyLock = new();
    readonly List<string> _typedMeanwhile = [];

    ContainedProcess? _current;

    /// <summary>
    /// A TEST'S SEAM (<c>U-turn-tail-drain</c>), set only by the test assembly (<c>InternalsVisibleTo</c>) on its own async flow:
    /// called with the launch's container after it is started and before its stdout and stderr are read. Nothing in the product
    /// sets it, and no file, setting or pipe op reaches it.
    /// </summary>
    internal static readonly AsyncLocal<Action<ContainedProcess>?> BeforeTheRead = new();
    CancellationTokenSource? _cts;

    /// <summary>
    /// THE TURN'S TEARDOWN: disposing what holds it, which ends its whole tree and says what it could not.
    /// A seam for the one test that needs a teardown that failed — a process the kill cannot reach is not
    /// something a test can make — and nothing else sets it.
    /// </summary>
    internal Func<ContainedProcess, TreeEnd?> EndTree { get; set; } = contained =>
    {
        contained.Dispose();
        return contained.Ended;
    };

    /// <summary>
    /// What the last turn's teardown left running, or null when it ended everything. Set by the turn's own
    /// <c>finally</c> and carried out on <see cref="TurnEnded"/>, so the meter that closes the turn's row can
    /// refuse the next turn while any of it runs.
    /// </summary>
    IReadOnlyList<ProcessEntry>? _survivors;

    /// <summary>
    /// THE PRESENCE OF A LAUNCH THE APP COULD NOT HOLD WHOSE TEARDOWN LEFT SOME OF IT RUNNING (<c>U-unjobbed-presence</c>), or
    /// null. Carried out on <see cref="TurnEnded"/> beside <see cref="_survivors"/>, and closed by whoever holds those — the
    /// meter, once a listing shows none of them alive — so a material pass beside a survivor never attests the inbox.
    /// </summary>
    IDisposable? _survivorPresence;
    bool _busy;
    bool _sessionExists;

    /// <summary>
    /// WHETHER THIS LAUNCH RESUMES THE CLI SESSION A TURN BEFORE IT OPENED — never for a route (a) cell seat (<c>U-execution-environment</c>):
    /// its harness's codex home, which holds the session's rollout, is the attempt's own and is swept when the turn ends
    /// (<see cref="RouteA.Remove"/>), so there is nothing to resume. Every executor turn starts fresh; its files are its memory.
    /// </summary>
    bool Resumes(string seatClass) => _sessionExists && seatClass != SeatPlan.CellClass;
    string? _threadId;

    /// <summary>
    /// WHY THE NEXT SESSION THIS CONVERSATION OPENS IS FRESH (<c>U-org-packets-a</c>; <see cref="SessionWhy"/>): its first in this run
    /// of the app until a stop says otherwise — the loop's rotation with its reason, any other stop as a restart. Kept until a
    /// session is open, so a turn that opened none leaves the reason standing for the next.
    /// </summary>
    string _freshWhy = SessionWhy.First;

    /// <summary>The session the turn in flight runs in (<see cref="AgentTurnEnded.Session"/>); null until it launches.</summary>
    TurnSession? _session;

    public bool Busy => _busy;

    /// <summary>
    /// THE REGISTER THIS SESSION'S CHILD PROCESSES REPORT THEMSELVES TO — the process-wide
    /// <see cref="AgentPresence.Shared"/> unless a test passed its own.
    ///
    /// Exposed for the same reason <see cref="MaterialScanner.Attests"/> is: the inbox attestation
    /// is worth nothing unless the scanner and every agent process are looking at ONE register, and
    /// a chat session on a register nobody reads is a running agent the ledger cannot see. Reading
    /// this does not enter the register, which matters — entering the shared one from a test would
    /// permanently downgrade every inbox sighting in that assembly.
    /// </summary>
    public AgentPresence Presence => presence ?? AgentPresence.Shared;

    public IReadOnlyList<ChatTurn> History
    {
        get { lock (_historyLock) return _history.ToArray(); }
    }

    /// <summary>The runtime's own session identifier, once it has told us one. Diagnostics only.</summary>
    public string? ThreadId => _threadId;

    /// <summary>
    /// The model this session's next turn will ask for, or null for none. A settings read that threw
    /// answers null — the turn runs on the runtime's own default rather than not running at all,
    /// which is the same rule the meter keeps about a rate it could not read.
    /// </summary>
    public string? RequestedModel
    {
        get
        {
            try { return model?.Invoke(); }
            catch (Exception) { return null; }
        }
    }

    public event Action<ChatTurn>? TurnAdded;
    public event Action<string>? Delta;
    public event Action? StateChanged;
    public event Action<AgentTurnEnded>? TurnEnded;

    /// <summary>
    /// Checks the runtime is actually there and clears any previous session, so the next message
    /// starts a fresh one. No process is started: in exec mode there is nothing to keep running
    /// between messages.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        var exe = resolveExecutable();
        if (exe is null)
            throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND,
                $"{manifest.DisplayName} is not installed on this computer");

        _sessionExists = false;
        _threadId = null;
        _freshWhy = SessionWhy.First;
        Directory.CreateDirectory(workspace());
        Append(new ChatTurn(ChatRole.System, $"{manifest.DisplayName} is ready.", DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends one message the OWNER typed, and returns when the AI's reply is complete.
    ///
    /// Failures are reported as a System turn rather than thrown: this drives a chat panel, and a
    /// conversation that throws its errors somewhere else is a conversation that loses them.
    ///
    /// <b>Typing while the AI is working no longer throws it away.</b> It used to raise
    /// INVALID_REQUEST, which was defensible when every turn was one the owner had asked for: they
    /// pressed send twice and the second press was a mistake. It is not defensible once the mission
    /// loop is taking turns on its own, because then the AI is busy almost all the time and the
    /// owner's question would have nowhere to go. The message is shown as theirs and carried to the
    /// top of the next turn's Situation instead — see <see cref="TakeTyped"/>.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (_busy) { Queue(message); return; }

        // NOT STARTED, NOT ADMITTED, NOT LOST (U-grant-tree): a launch this computer cannot hold, or one that would start
        // beside the Operations Director's order-capable turn, is refused before the ledger is asked — the owner's words
        // are held with the reason, exactly as a turn the ceiling stopped holds them.
        if (Barred() is { } bar)
        {
            Hold(message, bar.Why);
            return;
        }

        // THE LAUNCH'S GRANT, DECIDED BEFORE THE LEDGER IS ASKED (fix pass 1): a launch the register refuses — another one
        // issued since the look above holds order permission — is held exactly as the look's refusal is, with nothing written.
        if (Claim(out var withheld) is { } held)
        {
            Hold(message, held.Why);
            return;
        }

        // THIS TURN HOLDS NO ORDER PERMISSION, AND IS TOLD SO IN ITS OWN MESSAGE (ORDER-001) — in the grant's own words, before
        // the ledger hashes it, so the record is of the words actually sent.
        if (withheld is not null) message = LaunchBar.Told(withheld) + message;

        // THE OWNER'S TURN IS ADMITTED AND RESERVED LIKE ANY OTHER, BEFORE ANYTHING IS STARTED.
        //
        // It used to be metered and never reserved: this line started the CLI, and the row was
        // written when the usage came back. So a typed question could run beside a mission turn,
        // spend the last of the day's allowance and be recorded only after the vendor had been paid
        // — and, holding the meter's single open slot, resolve the OTHER turn's reservation with its
        // own usage. Every launch has a commitment made before it (docs/COUNCIL.md rule 3), and the
        // person typing is not an exception to a bill they are the one paying.
        // NO ADMISSION ATTACHED IS A REFUSAL, NOT A PASS (U-admission-fail-closed): a conversation the meter is not
        // attached to — never attached, or detached when its runtime was replaced — has nothing to reserve its turn.
        AiAdmission admission;
        try { admission = Admit?.Invoke(message) ?? AiAdmission.NoLedgerOpen; }
        catch (Exception) { Unclaim(); throw; }
        if (!admission.Admitted)
        {
            Unclaim();
            // NOT LAUNCHED, NOT LOST. Their words are held exactly as a message typed while the AI
            // is working is held, and the System line says which ceiling stopped it rather than
            // leaving a chat that simply went quiet.
            Hold(message, admission.Refusal!);
            return;
        }

        Append(new ChatTurn(ChatRole.You, message, DateTimeOffset.UtcNow));

        // REFUSED BY THE GATE AT ITS LAST CHECK, AFTER ADMISSION: nothing answered them, so their words are kept exactly as a
        // refused admission keeps them — the System line the turn wrote says why (review of U-thinking-seats-a, item 1).
        if (await RunAsync(message, ct) is not null) Keep(message);
    }

    /// <summary>
    /// OPENS THE DURABLE RECORD AND COMMITS THE COST OF A TURN THE OWNER TYPED, before it is
    /// started, and answers whether it may be started at all.
    ///
    /// Set by <see cref="TurnMeter.Attach"/>, which is the one place a conversation is metered, and
    /// cleared when it detaches. NULL REFUSES (<see cref="AiAdmission.NoLedgerOpen"/>,
    /// <c>U-admission-fail-closed</c>): it used to be the permissive default — the turn ran and the row
    /// was written when it ended — and that is a turn no ceiling saw before it was paid for. A host
    /// built without a ledger says so explicitly, with an admission that answers
    /// <see cref="AiAdmission.NoLedger"/>.
    /// </summary>
    public Func<string, AiAdmission>? Admit { get; set; }

    /// <summary>
    /// WRITES THE OWNER'S WORDS SOMEWHERE THAT SURVIVES THIS PROCESS, and answers whether it did.
    ///
    /// The queue below it is a list in memory. That was defensible while a queued message was only
    /// ever a few seconds old — the AI was working and would take it on the next turn — and it stops
    /// being defensible the moment the loop can be waiting half an hour, or the app can be closed
    /// and reopened, with a question sitting in it. A restart lost the words AND the receipt the
    /// owner had already been shown, which is worse than losing them: the software had said it would
    /// deal with it.
    ///
    /// Set by the composition root to the wake queue. Null in every host that has none, and then
    /// this behaves exactly as it did.
    /// </summary>
    public Func<string, bool>? RecordTyped { get; set; }

    /// <inheritdoc />
    public void Queue(string message) => Hold(message, Labels.HeldWhileTheAiIsWorking);

    /// <summary>
    /// KEEPS THE OWNER'S WORDS AND SAYS WHERE THEY WENT. Two callers, one behaviour: the AI is busy,
    /// or the day's ceiling refused the turn. The words are recorded the same way in both — the
    /// difference is one sentence, and a message that disappeared into a queue with no
    /// acknowledgement reads exactly like a message that was dropped.
    /// </summary>
    void Hold(string message, string said)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Keep(message);
        Append(new ChatTurn(ChatRole.You, message, DateTimeOffset.UtcNow));
        Append(new ChatTurn(ChatRole.System, said, DateTimeOffset.UtcNow));
    }

    /// <summary>Keeps the owner's words for the next turn, where <see cref="Hold"/> keeps them, without a second line in the chat.</summary>
    void Keep(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = message.Trim();

        // EXACTLY ONE OF THE TWO HOLDS IT. Writing to both would hand the same words to the next
        // turn twice — once off the table and once off the list — and an owner who asked one
        // question would watch it answered twice.
        var recorded = false;
        try { recorded = RecordTyped?.Invoke(text) ?? false; }
        catch (Exception) { recorded = false; }
        if (!recorded) lock (_historyLock) _typedMeanwhile.Add(text);
    }

    /// <inheritdoc />
    public Task SendMissionAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;
        if (_busy)
            throw new TradeAgentException(ErrorCode.INVALID_REQUEST,
                "The AI is still working on the previous message.");
        return RunAsync(message, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> TakeTyped()
    {
        lock (_historyLock)
        {
            if (_typedMeanwhile.Count == 0) return [];
            var taken = _typedMeanwhile.ToArray();
            _typedMeanwhile.Clear();
            return taken;
        }
    }

    /// <summary>
    /// The run itself, shared by the owner's messages and the mission's. Everything that differs
    /// between the two — whose words they are, and whether they are shown as such — has already
    /// happened by the time this is called.
    ///
    /// <see cref="TurnEnded"/> is raised on EVERY path out, including the ones where no child ever
    /// started, because the mission loop's backoff and `U-meter`'s prices are both counting turns and
    /// a turn that vanished silently is one neither of them can account for.
    /// </summary>
    async Task<LateRefusal?> RunAsync(string message, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var exitCode = -1;
        var raw = "";
        TurnUsage? usage = null;
        VendorLimit? limit = null;
        LateRefusal? gateRefused = null;

        var exe = resolveExecutable();
        if (exe is null)
        {
            Unclaim();
            raw = $"{manifest.DisplayName} is not installed, so the message was not sent.";
            Append(new ChatTurn(ChatRole.System, raw, DateTimeOffset.UtcNow));
            TurnEnded?.Invoke(new AgentTurnEnded(exitCode, DateTimeOffset.UtcNow - startedAt, raw, DateTimeOffset.UtcNow));
            return null;
        }

        SetBusy(true);
        _survivors = null;
        _survivorPresence = null;
        _canon = null;
        _promptIn = null;
        _session = null;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            (exitCode, raw, usage, limit) = await RunTurnAsync(exe, message, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            raw = "Stopped.";
            Append(new ChatTurn(ChatRole.System, raw, DateTimeOffset.UtcNow));
        }
        catch (GateRefusedException ex)
        {
            // THE GATE'S LAST CHECK REFUSED IT, BEFORE THE PROCESS: nothing started. Its kind travels out as data.
            raw = ex.Message;
            gateRefused = new LateRefusal(ex.Refusal, BeforeAnyWork: true);
            Append(new ChatTurn(ChatRole.System, ex.Message, DateTimeOffset.UtcNow));
        }
        catch (TradeAgentException ex)
        {
            raw = ex.Message;
            Append(new ChatTurn(ChatRole.System, ex.Message, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            raw = ex.Message;
            Append(new ChatTurn(ChatRole.System, $"The AI could not be reached: {ex.Message}", DateTimeOffset.UtcNow));
        }
        finally
        {
            // A CLAIM THE TURN NEVER TOOK — it threw before its launch — is given back, not left holding the next one.
            Unclaim();
            _current = null;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            TurnEnded?.Invoke(new AgentTurnEnded(exitCode, DateTimeOffset.UtcNow - startedAt, raw, DateTimeOffset.UtcNow)
            {
                // What the RUNTIME said this turn used, or null where it said nothing. Carried out
                // of the turn rather than re-derived from Raw by whoever prices it, because the
                // stream is parsed once, here, and a second parser somewhere else would be a second
                // thing to keep correct as these CLIs change their event shapes.
                Usage = usage,
                // And the vendor's refusal, for the same reason: one reading of the stream, carried
                // to the loop that has to wait for it and the meter that has to price it.
                Limit = limit,
                // AND WHAT THE TEARDOWN COULD NOT END. The meter closes this turn's row on this event; a
                // process of the turn still running past that is spend no ceiling sees, so the meter holds
                // the next launch while any of these is the same process and still running.
                Survivors = _survivors,
                // AND THEIR PRESENCE, for a launch the app could not hold (U-unjobbed-presence): open while any of them runs.
                SurvivorPresence = _survivorPresence,
                // AND THE CANON IT WAS LAUNCHED UNDER, as found on disk the moment before the process started.
                Canon = _canon,
                // AND HOW ITS PROMPT TRAVELLED: on stdin, or on the command line of a runtime that cannot read stdin, said.
                PromptIn = _promptIn,
                // AND THE GATE'S LAST-CHECK REFUSAL, with its kind, where there was one.
                GateRefused = gateRefused,
                // AND THE SESSION IT RAN IN: fresh, with why, or resumed — null for a turn that launched nothing (U-org-packets-a).
                Session = _session
            });
        }
        return gateRefused;
    }

    async Task<(int ExitCode, string Raw, TurnUsage? Usage, VendorLimit? Limit)> RunTurnAsync(string exe, string message,
        CancellationToken ct)
    {
        // A PUPPET SEAT RUNS IN A TEST HOST AND NOWHERE ELSE (U-puppet-runtime), refused before the profile is read, before any
        // grant is issued and before any process exists: a puppet is handed the launch's real grant, and the chair's may trade.
        if (PuppetSeats.Refusal(manifest) is { } puppet)
            throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED, puppet);

        // REFUSED BEFORE ANYTHING IS STARTED, AND THIS IS THE ONLY PLACE A TURN BEGINS.
        //
        // The armed live configuration will not run an AI runtime that no operating-system sandbox
        // confines — see Containment.RefusalToLaunch. It throws rather than returning quietly: the
        // caller turns a TradeAgentException into a System turn in the conversation, so the owner
        // reads the sentence in the window where they armed the switch, which is the only place it
        // means anything.
        if (launchRefusal?.Invoke() is { Length: > 0 } refusal)
            throw new TradeAgentException(ErrorCode.CONTAINMENT_REQUIRED, refusal);

        // A LAUNCH THIS COMPUTER CANNOT HOLD NEVER STARTS (U-grant-tree, RULING 2): its grant could be bound to nothing,
        // so every request it made would be refused while the vendor billed for the turn.
        // Before the grant is minted; a grant claimed before admission is given back by RunAsync.
        if (ProcessContainment.CannotHold() is { } unheld)
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE, unheld);

        // THE GRANT THIS LAUNCH WILL CARRY: the one claimed before its admission (Claim), or — for a caller that admitted
        // nothing first — issued now, refused here as the hard line. Taken here, so every way out below ends it.
        var register = grants ?? AgentGrants.Shared;
        var claimed = TakeClaim();
        using var hold = claimed?.Hold ?? ProcessContainment.Prepare();
        using var launch = claimed?.Grant ?? register.Issue(role, "");

        var streaming = !string.IsNullOrWhiteSpace(manifest.JsonFlag);

        // The model is read here, at the turn, and handed to the SAME builder for a first message and
        // a resumed one. See AgentArgs.For: `resuming` picks the template and nothing else.
        var seatClass = SeatClass;
        var launchSeat = Launch;
        var args = AgentArgs.For(manifest, message, resuming: Resumes(seatClass), model: LaunchModel, seatClass, launchSeat.Door,
            out var promptInArgv);

        // THE SEAT'S PROFILE, VERIFIED AS IT STARTS, fresh or resumed (U-seat-home): the pin, the deny set on this very argv, the
        // config file, the home's and the workspace's inventory, and the CLI's own report — and for a broker seat its door, its
        // pinned model and its offered tools (U-thinking-seats-c). Checked again here although the meter checked it before admitting
        // the turn: a host with no meter starts turns too, and this is the last moment before the start.
        var seat = await SeatProfile.VerifyAsync(manifest, exe, args, workspace(), SeatProbe, launchSeat, ct);
        if (seat.Refused) throw new TradeAgentException(seat.Code ?? ErrorCode.AI_SEAT_REFUSED, seat.Refusal);

        // THE PROFILE THE LEDGER RECORDED IS THE PROFILE THAT STARTS: a launch whose seat changed between its admission and this
        // line would run under a profile its row does not name.
        if (Interlocked.Exchange(ref _admittedProfile, null) is { } admitted && seat.Profile is { } now && admitted != now)
            throw new TradeAgentException(ErrorCode.AI_SEAT_REFUSED,
                "TradeAgent did not start the AI: its own settings changed between the moment the turn was recorded and its start. " +
                "The next turn checks them again.");

        // THE ACTIVATION GATE, ONCE MORE, IMMEDIATELY BEFORE THE PROCESS (U-thinking-seats-a; ORCHESTRATION § 2.3): the meter asked
        // it before admitting the turn, and a host with no meter starts turns too. This seat is a CLI seat outside any cell — or a
        // test host's puppet playing its declared class — the seat actually about to start, so a position beyond the legacy pair is
        // refused here whatever the meter answered.
        if (PositionActivation.AtLaunch(role, seatClass, seats) is { } gate)
            throw new GateRefusedException(gate, beforeAnyWork: true);

        // A SESSION OPENED UNDER ANOTHER PROFILE IS NOT RESUMED: the rollout it would continue was written under other flags, by
        // another build or with another deny set, and the turn starts a fresh one instead — the files are the memory.
        if (_sessionExists && seat.Profile != _sessionProfile)
        {
            _sessionExists = false;
            _threadId = null;
            _freshWhy = SessionWhy.Profile;
            args = AgentArgs.For(manifest, message, resuming: false, model: LaunchModel, seatClass, launchSeat.Door, out promptInArgv);
        }
        if (!_sessionExists) _sessionProfile = seat.Profile;

        // THE SESSION THIS TURN RUNS IN, as decided on this very argv (U-org-packets-a): resumed only where the resume template is
        // what launches — a runtime that declares none starts fresh every turn, and says so (fix pass 1) — or fresh with why.
        // RECORDED ONLY ONCE THE PROCESS EXISTS (fix pass 1): a start that throws launched nothing, and its turn names no session.
        // AND A CELL SEAT NEVER RESUMES (U-execution-environment): its record says fresh, why cell — never resumed for a launch that was not.
        var session = AgentArgs.Resumes(manifest, Resumes(seatClass)) ? TurnSession.Resumed
            : TurnSession.FreshBecause(!_sessionExists ? _freshWhy : seatClass == SeatPlan.CellClass ? SessionWhy.Cell : SessionWhy.NoResume);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected so it can be CLOSED immediately, below. Not redirecting it is the bug this
            // line exists to prevent: Codex reads stdin IN ADDITION to the prompt argument — it
            // prints "Reading additional input from stdin..." and waits for end-of-file. An
            // unredirected child inherits our stdin, and TradeAgent is a window with no console, so
            // that handle never reaches end-of-file. Measured on Windows 11: the run hung forever
            // with the turn stuck at Busy, and the same command with stdin closed answered in
            // seconds. A conversation that never returns is the worst possible failure here,
            // because it looks exactly like the AI thinking.
            //
            // AND, FOR A RUNTIME THAT READS IT, THE PROMPT TRAVELS HERE (U-prompt-stdin): written whole, then closed — the
            // same end-of-file, one message later. UTF-8 without a byte-order mark, set rather than defaulted: on Windows the
            // default is the console's code page, and the owner's words are not ASCII.
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        CliAgentRuntime.SetCommand(psi, exe, args);

        // ONE LAUNCH, ONE GRANT, AND IT TRAVELS IN THE ENVIRONMENT ONLY.
        //
        // Minted here rather than by whoever built the environment, because this is the only line in
        // the product that knows a process is about to exist: the chair's environment is a dictionary
        // made once at prepare, and a token minted there would be one token for every turn of the
        // app's life. Disposed when the turn returns, which pulls its expiry in to the grace — see
        // AgentGrants.
        // THE ATTEMPT THE LEDGER OPENED FOR THIS LAUNCH, stamped on the grant while it is still pending.
        var attemptId = attempt?.Invoke() ?? "";
        register.Attribute(launch, attemptId);

        // A WHITELIST, NOT THE APP'S OWN ENVIRONMENT WITH A FEW NAMES ADDED. `psi.Environment` starts
        // as a copy of this process's, so everything TradeAgent was started with used to reach the
        // AI — see AgentEnvironment. The additions are still the only thing that puts `trade` on the
        // agent's PATH; losing them is how the agent ends up reading its own instructions about a
        // command it cannot run.
        var env = new Dictionary<string, string>(environment()) { [AgentGrants.Variable] = launch.Token };
        AgentEnvironment.Apply(psi, env, manifest, seatClass);

        // AN EXECUTOR'S CANON IS THE APP'S AGAIN BEFORE ITS TURN (U-execution-environment item 3): its cell writes its whole home,
        // AGENTS.md included, so the text the app last wrote there is put back — the record below then reads the app's own.
        if (seatClass == SeatPlan.CellClass) RestoreCanon(psi.WorkingDirectory);

        // THE CANON THIS TURN RECEIVES, AS IT IS ON DISK NOW: the vendor reads it by name from the working directory as it
        // starts, so this is the last moment the app can say what it read — and whether it is still the app's own.
        _canon = Canon.SeenByCli(psi.WorkingDirectory, role, appFiles ?? AppFileManifest.Shared);
        // MEASURED ON THE COMMAND LINE THAT STARTS, not read off the manifest: an attempt says what its argv carried.
        _promptIn = promptInArgv ? AgentArgs.InArgv : AgentArgs.OnStdin;

        // HELD, NOT MERELY STARTED. A Job Object on Windows, a session of its own on macOS and
        // Linux: Kill(entireProcessTree) walks parent links, and a grandchild whose parent has
        // exited has none left to walk — measured before this line existed, a detached grandchild
        // outlived CancelAsync on both platforms. See ProcessContainment.
        // AND ON LINUX, IN THE ATTEMPT'S OWN CGROUP where the app holds a delegated one (U-cell-linux-b): every process the launch
        // starts is born in it, whatever session it makes, and the grant below is served only from inside it.
        //
        // AND IN THE PRESENCE REGISTER BEFORE IT CAN RUN (U-unjobbed-presence): the material scanner attests an inbox sighting
        // only across a window no agent process was alive in, and a process is alive from the start below — not from the line
        // that learns it is held. Entered after the start, a launch Windows would not put in its job ran, and was torn down,
        // outside the register: a pass beside it recorded an inbox write of that window as the owner's. The handle is the
        // launch's on every path: closed here if the start throws, by the branch below on the launch it refuses, by the finally
        // on every other — once.
        var alive = Presence.Enter();
        ContainedProcess contained;
        RouteA.Layout? cellHomes = null;
        IReadOnlyList<ProcessEntry>? cellSurvivors = null;
        try
        {
            contained = seatClass == SeatPlan.CellClass
                ? StartInCell(psi, exe, attemptId, out cellHomes, out cellSurvivors)
                : ProcessContainment.Start(psi, hold, attemptId: attemptId);
        }
        catch (Exception)
        {
            if (cellHomes is not null) RouteA.Remove(cellHomes);
            // A CELL LAUNCH THAT FAILED AFTER SOMETHING OF IT STARTED, AND WHOSE TEARDOWN COULD NOT END ALL OF IT (fix pass 1, R2): what
            // still runs is carried out on TurnEnded and its presence stays open with it, exactly as for a launch the app could not
            // hold — the meter admits no launch while any of it runs, and unknown is never "ended".
            if (cellSurvivors is not null)
            {
                _survivors = cellSurvivors;
                _survivorPresence = alive;
            }
            else alive.Dispose();
            throw;
        }
        var process = contained.Process;
        _current = contained;
        _session = session;

        // THE GRANT IS BOUND TO WHAT HOLDS THE LAUNCH, NOW AND NEVER BEFORE (U-grant-tree). Until this line it is pending
        // and the pipe serves it to nobody; from it, only to a peer inside this container — the job the process was put
        // in, or the session the launcher makes. A launch with no container is torn down at once and refused (RULING 2):
        // the attempt keeps its reservation, because a process that ran for any time is spend nobody can call zero.
        //
        // AND WHAT THAT TEARDOWN COULD NOT END IS STILL RUNNING (fix pass 1): carried out on TurnEnded as the finally's are,
        // so the meter admits no launch while any of it runs. Dropped, it was an uncontained process spending beside the next
        // launch with nothing anywhere saying so.
        if (contained.Container is not { } container)
        {
            _current = null;
            var end = EndTree(contained);
            // ITS PRESENCE ENDS WITH THE LAST OF IT, NOT WITH THE TEARDOWN THAT COULD NOT END IT: what still runs is carried out
            // with the survivors, and the meter that holds every launch while any of them runs closes it once a listing shows none
            // alive. A teardown that throws keeps it open — unknown is never "ended".
            if (end is { Ended: false })
            {
                _survivors = end.Survivors;
                _survivorPresence = alive;
            }
            else alive.Dispose();
            throw new TradeAgentException(ErrorCode.CONTAINMENT_UNAVAILABLE,
                $"TradeAgent could not hold the AI's program it had just started ({contained.Mechanism}), so it " +
                (end is { Ended: false }
                    ? end.Survivors.FirstOrDefault(p => p.Cgroup is not null) is { Cgroup: { } cgroup } stuck
                        ? $"tried to end it at once and could not{(stuck.Unlisted ? " tell whether it ended" : "")}: its attempt's cgroup " +
                          $"{Path.GetFileName(cgroup)} {(stuck.Unlisted ? "could not be listed" : "still holds processes")}, and no AI turn starts " +
                          "until that cgroup is empty. "
                    : end.Survivors.Any(p => p.Unlisted)
                        ? "tried to end it at once and could not tell whether it ended: Windows would not list the programs it " +
                          $"started (under process {end.Survivors.First(p => p.Unlisted).Pid}), and no AI turn starts until Windows lists them and " +
                          "none is left running. "
                        : $"tried to end it at once and could not: process {string.Join(", ", end.Survivors.Select(p => p.Pid))} " +
                          "still runs, and no AI turn starts until it has ended. "
                    : "ended it at once. ") +
                "Restart TradeAgent — it puts back what it needs to hold the AI as it starts. If this stays, reinstall TradeAgent.");
        }
        register.Bind(launch.Token, container);

        // THE TURN'S PRESENCE: the material scanner cannot attest an inbox sighting to the account owner
        // across a window any process of this turn was inside (REVIEW 2026-09-05b f5). Opened before the start
        // above and closed by the finally below AFTER the teardown — never by the leader's exit, which says
        // nothing about the processes it started, and which is how presence used to read 0 while a turn's
        // detached children still ran (U-agent-tree).
        try
        {
            // End-of-file on stdin, at once — after the prompt, for a runtime that reads it there. See the comment on
            // RedirectStandardInput above. Written beside the reads below rather than before them: a child that answers
            // before it has read a long prompt would otherwise fill its stdout pipe while the app is still writing.
            var feed = !promptInArgv
                ? Task.Run(() => FeedAsync(process, message), CancellationToken.None)
                : CloseInput(process);

            // THE LEADER'S EXIT IS THE TURN'S END, AND ITS TREE ENDS WITH IT, AT ONCE (U-agent-tree). A
            // process the turn left behind that still holds the turn's stdout or stderr used to hold the
            // turn itself open — the reads below wait for the last writer — so the teardown in the finally
            // was never reached and the leftover lived as long as it liked: measured on windows-latest, a
            // finished turn whose leftover held stderr never committed. What the leader wrote stays in the
            // pipes and is still read; only the processes it left are ended. On macOS and Linux the
            // resident launcher has already swept its session by the time it exits; this is the same end.
            _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(exited =>
            {
                _ = exited.Exception;
                try { contained.End(); }
                catch (Exception) { /* the finally's teardown runs regardless and says what it could not end */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            // A TEST'S SEAM AND NOTHING ELSE (U-turn-tail-drain): run once the reads are about to begin, so a test can end the
            // turn's tree before a single byte of its stream is read — the order a slow reader meets on a loaded machine.
            BeforeTheRead.Value?.Invoke(contained);

            var stderr = process.StandardError.ReadToEndAsync(ct);
            // A read the turn abandons — cancelled, or a pipe the teardown closed — is observed here, so
            // an ending nobody waits for never reaches the app's last-resort error line.
            _ = stderr.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var raw = new StringBuilder();
            var state = new TurnState();

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                raw.AppendLine(line);
                if (streaming) HandleStreamLine(line, state);
            }

            await process.WaitForExitAsync(ct);
            var errorText = (await stderr).Trim();
            await feed;

            FinishTurn(state, raw.ToString(), streaming, process.ExitCode, errorText);
            return (process.ExitCode, raw.ToString(), state.Usage, state.Limit);
        }
        finally
        {
            // THE TURN'S WHOLE TREE ENDED, ON EVERY PATH OUT — finished, failed, cancelled — leader exited
            // or not (ContainedProcess.End). What it could not end is carried to the meter, and the grant
            // this launch carries is revoked outright rather than given its grace: the grace exists for a
            // `trade` the turn started a moment before it ended, and a turn whose processes are still
            // running past their teardown is not one whose work deserves another minute on the pipe.
            if (EndTree(contained) is { Ended: false } end)
            {
                _survivors = end.Survivors;
                register.Revoke(launch.Token);
            }
            _current = null;
            if (cellHomes is not null) RouteA.Remove(cellHomes);
            alive.Dispose();
        }
    }

    /// <summary>
    /// STARTS A ROUTE (a) CELL SEAT (<c>U-cell-linux-c</c> item 4): admitted only under a counted proof whose profile still holds (else
    /// refused, never degraded); the attempt's cgroup with its two leaves; the cell's policy written; the per-attempt codex homes made
    /// (<see cref="RouteA.Make"/>) with the harness's <c>CODEX_HOME</c> pointed at its own; the harness started in its leaf; and the
    /// launch's assurance written once (<see cref="RouteA.AssuranceRows"/>). Every failure throws <see cref="ErrorCode.CELL_UNAVAILABLE"/>
    /// after ending what it started — and what that teardown could not end is handed back in <paramref name="survivors"/> (fix pass 1,
    /// R2): the attempt's cgroup is then kept, never removed under a process, and the caller carries them out with the launch's presence.
    /// </summary>
    ContainedProcess StartInCell(ProcessStartInfo psi, string exe, string attemptId, out RouteA.Layout? homes, out IReadOnlyList<ProcessEntry>? survivors)
    {
        homes = null;
        survivors = null;
        var platform = CellPlatform.Read();
        // THE EXECUTOR CELL'S ONE EGRESS (item 3): the constant the policy below is written with, admitted under a proof that measured it.
        var (proof, refusal) = RouteA.Admission(cells, manifest.Install.ExecutableSha256 ?? "", platform, RouteA.Egress);
        if (refusal is not null || proof is null || cells is null || manifest.Seat is not { } seat)
            throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, refusal ?? "TradeAgent did not start the AI as a cell seat: it has no seat profile.");
        var attempt = ProcessContainment.RouteAAttempt(cells.Cgroups, attemptId);
        string? policyFile = null;
        ContainedProcess? started = null;
        try
        {
            var layout = RouteA.LayoutFor(Path.GetFileName(attempt.Dir));
            var policy = RouteA.Policy(workspace(), exe, RouteA.CodexRoot(exe), layout.CellCodexHome, layout.CellHome);
            var launcher = cells.Launcher ?? throw new TradeAgentException(ErrorCode.CELL_UNAVAILABLE,
                "TradeAgent's own trade command, which starts a cell, is not installed; nothing was started");
            policyFile = policy.Write();
            homes = layout;
            RouteA.Make(layout, SeatProfile.ConfigBytes(seat), RouteA.EnvironmentsToml(launcher, attempt.Cell, policyFile, exe, Paths.Home),
                Path.Combine(seat.Root, "codex", "auth.json"));
            psi.Environment["CODEX_HOME"] = layout.HarnessCodexHome;
            started = ProcessContainment.StartHarness(psi, attempt, launcher);
            cells.Assurance.Record(new CellAssurance(attemptId.Length > 0 ? attemptId : Path.GetFileName(attempt.Dir), RouteA.Provider,
                policy.Hash, proof.ProfileHash, proof.Id, platform.Kernel, platform.LandlockAbi, platform.Lsm ?? "", policy.Egress.Name,
                attempt.Dir, RouteA.Harness, RouteA.AssuranceRows(proof.Id, policy.Egress)));
            return started;
        }
        catch (Exception ex)
        {
            // WHAT STARTED IS ENDED — the harness by its own teardown (its attempt's cgroup, then its session), anything else by the
            // attempt's cgroup.kill — AND WHAT COULD NOT BE ENDED IS SAID (fix pass 1, R2): a kill that failed leaves the cgroup and its
            // processes to the caller, never reported ended and never removed from under them.
            if (started is not null)
            {
                if (EndTree(started) is { Ended: false } end) survivors = end.Survivors;
            }
            else if (!attempt.Kill(TimeSpan.FromSeconds(3))) survivors = attempt.Left();
            if (survivors is null) attempt.Remove();
            if (policyFile is not null) CellStage.Discard(policyFile);
            throw ex is TradeAgentException { Code: ErrorCode.CELL_UNAVAILABLE } ? ex
                : new TradeAgentException(ErrorCode.CELL_UNAVAILABLE, $"TradeAgent did not start the AI as a cell seat: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// THE PROMPT, WRITTEN TO THE CHILD'S STDIN AND CLOSED (<c>U-prompt-stdin</c>), as bytes: the writer over that stream could
    /// put a byte-order mark first. A child that exits without reading it — refused at its start, say — closes the pipe under
    /// the write; that turn ends on its own exit code and words, which say more than a broken pipe does.
    /// </summary>
    static async Task FeedAsync(Process process, string prompt)
    {
        try
        {
            var stream = process.StandardInput.BaseStream;
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(prompt));
            await stream.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the child left */ }
        finally { await CloseInput(process); }
    }

    /// <summary>End-of-file on the child's stdin.</summary>
    static Task CloseInput(Process process)
    {
        try { process.StandardInput.Close(); } catch (Exception) { /* already gone */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Everything that has to happen once the child has exited: flush partial assistant text, apply
    /// the non-streaming fallback, and say something useful if the run failed.
    /// </summary>
    void FinishTurn(TurnState state, string raw, bool streaming, int exitCode, string errorText)
    {
        // Assistant text that streamed but never got a "completed" event still belongs in history.
        foreach (var pending in state.TakePendingMessages())
            AppendAi(pending);

        if (!streaming)
        {
            // FALLBACK PATH — this runtime has no machine-readable stream, so nothing could be shown
            // while it was thinking and there is no way to see which tools it used. Everything the
            // program printed becomes one assistant turn at the end. Deliberately plain: a guess at
            // structure here would be a guess presented to the user as fact.
            var text = raw.Trim();
            if (text.Length > 0) AppendAi(text);
        }
        else if (!state.ProducedAnyMessage && state.Limit is null)
        {
            // Streaming was asked for but nothing recognisable arrived — a flag the runtime does not
            // have, or an event shape that changed. Showing the raw output is worse than useless
            // only if it is empty, so show it when it is not. A refusal the manifest recognised IS
            // something recognisable, and its raw events are not words the AI said.
            var text = raw.Trim();
            if (text.Length > 0) AppendAi(text);
        }

        // THE VENDOR'S OWN WORDS, AND LAST. The card shows the last thing the conversation said, and
        // for a refused turn that used to be "did not finish: Reading additional input from stdin..." —
        // stderr's first line, which codex prints on every run and which says nothing about why this
        // one ended. The sentence that does say, and says until when, is the vendor's.
        if (state.Limit is { } limit)
        {
            Append(new ChatTurn(ChatRole.System, limit.Message, DateTimeOffset.UtcNow));
        }
        else if (exitCode != 0)
        {
            var detail = errorText.Length > 0 ? Tail(errorText) : $"it stopped with code {exitCode}";
            Append(new ChatTurn(ChatRole.System, $"{manifest.DisplayName} did not finish: {detail}", DateTimeOffset.UtcNow));
        }

        if (exitCode == 0) _sessionExists = true;
        if (_threadId is not null) _sessionExists = true;
    }

    // ---- stream parsing ------------------------------------------------------------------------

    sealed class TurnState
    {
        /// <summary>Assistant text seen so far per stream item, so only the new part is emitted.</summary>
        public Dictionary<string, string> Partial { get; } = [];

        /// <summary>Tool items already shown, so a start and its completion are one line, not two.</summary>
        public HashSet<string> AnnouncedTools { get; } = [];

        public bool ProducedAnyMessage { get; set; }

        /// <summary>
        /// What the runtime has reported this turn using so far, or null while it has reported
        /// nothing. Null and zero are different answers — "it did not say" against "it used none" —
        /// and only the first is honest about a CLI with no usage event.
        /// </summary>
        public TurnUsage? Usage { get; private set; }

        public void Add(TurnUsage usage) => Usage = Usage is null ? usage : Usage.Plus(usage);

        /// <summary>Item events of every kind the stream has carried — a message, reasoning, a command, a tool.</summary>
        public int Items { get; set; }

        /// <summary>
        /// WHETHER THE STREAM HAS SHOWN ANY WORK AT ALL: an item of any kind, text, a tool or usage. A
        /// turn with none of them when the vendor refused it is a turn whose first request was refused.
        /// </summary>
        public bool Worked => Items > 0 || ProducedAnyMessage || AnnouncedTools.Count > 0 || Usage is not null;

        /// <summary>The vendor's refusal of this turn for its usage limit, or null. The first one stands.</summary>
        public VendorLimit? Limit { get; set; }

        readonly List<string> _pending = [];

        public void Remember(string id, string text) => Partial[id] = text;

        public void Complete(string id) => Partial.Remove(id);

        public void Pend(string text) => _pending.Add(text);

        public IEnumerable<string> TakePendingMessages()
        {
            // Anything still half-written when the process exited.
            var leftovers = Partial.Values.Where(v => v.Trim().Length > 0).ToList();
            Partial.Clear();
            var all = _pending.Concat(leftovers).ToList();
            _pending.Clear();
            return all;
        }
    }

    void HandleStreamLine(string line, TurnState state)
    {
        var trimmed = Ansi.Strip(line).Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return;   // progress chatter, not an event

        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed); }
        catch (JsonException) { return; }

        using (doc)
        {
            try { HandleEvent(doc.RootElement, state); }
            catch (Exception) { /* one unreadable event must not end the conversation */ }
        }
    }

    void HandleEvent(JsonElement e, TurnState state)
    {
        if (e.ValueKind != JsonValueKind.Object) return;

        // WHAT THE TURN COST, READ BEFORE ANY OTHER BRANCH CAN RETURN.
        //
        // It has to be first because the event carrying it is not one this method otherwise cares
        // about: Codex 0.153.4 reports tokens on `turn.completed`, which names no message, no tool
        // and no error, and would fall out of the bottom of this method untouched. Every branch
        // below returns, so anywhere else is a place the counts are silently dropped for one runtime
        // and not another.
        if (TurnUsage.Read(e) is { } usage) state.Add(usage);

        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "" : "";

        // Codex announces its session once, as thread_id. OpenCode puts sessionID on every line.
        // Neither is needed to resume — both runtimes take "continue the last one" — but knowing
        // which session the window is looking at is worth having when something goes wrong.
        if (_threadId is null)
            _threadId = Text(e, "thread_id") ?? Text(e, "sessionID") ?? Text(e, "session_id");

        if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            var msg = Text(e, "message") ?? Text(e, "error") ?? FindText(e, 3) ?? "the AI reported an error";

            // THE VENDOR REFUSING THE TURN FOR ITS OWN USAGE LIMIT, recognised by the manifest's data and
            // never by a sentence written here. Held rather than shown now, so that it is the LAST thing
            // the turn says (FinishTurn). Whether any work came before it is settled HERE, at the moment
            // of the refusal, from what the stream had shown so far — the meter charges a turn nothing on
            // that answer, so it must not be read off anything that arrived afterwards.
            if (manifest.UsageLimit?.Read(msg, DateTimeOffset.UtcNow) is { } limit)
            {
                state.Limit ??= limit with { Vendor = manifest.DisplayName, BeforeAnyWork = !state.Worked };
                return;
            }

            Append(new ChatTurn(ChatRole.System, msg, DateTimeOffset.UtcNow));
            return;
        }

        // Codex: every unit of work arrives as an "item" with its own type.
        if (e.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            state.Items++;
            HandleItem(type, item, state);
            return;
        }

        // OpenCode: the event type IS the kind, and the payload sits in "part".
        // "text" is the answer; "tool_use" is the AI doing something; "reasoning" is thinking and
        // is left out for the same reason it is left out for Codex.
        if (type.Equals("tool_use", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            var part = e.TryGetProperty("part", out var p) && p.ValueKind == JsonValueKind.Object ? p : e;
            var name = Text(part, "tool") ?? Text(part, "name") ?? "a tool";
            var described = $"Used {name}";
            // No stable per-call id is documented, so the description itself is the identity —
            // enough to stop a tool that reports twice from appearing twice.
            if (state.AnnouncedTools.Add(described))
                Append(new ChatTurn(ChatRole.Tool, described, DateTimeOffset.UtcNow));
            return;
        }

        if (type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return;

        // Anything else: only trust it when the event calls itself a message.
        if (type.Contains("message", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("assistant", StringComparison.OrdinalIgnoreCase))
        {
            var text = FindText(e, 3);
            if (string.IsNullOrWhiteSpace(text)) return;

            // OpenCode emits one complete text block per event rather than a growing string, so
            // each event is its own finished piece of the answer.
            var id = Text(e, "id") ?? $"text-{state.Partial.Count}-{text.Length}";
            Stream(id, text, state, complete: true);
        }
    }

    void HandleItem(string eventType, JsonElement item, TurnState state)
    {
        var itemType = Text(item, "type") ?? "";
        var id = Text(item, "id") ?? itemType;
        var completed = eventType.EndsWith(".completed", StringComparison.Ordinal);

        if (itemType.Contains("agent_message", StringComparison.OrdinalIgnoreCase) ||
            itemType.Equals("assistant_message", StringComparison.OrdinalIgnoreCase) ||
            itemType.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            var text = Text(item, "text") ?? FindText(item, 3);
            if (string.IsNullOrEmpty(text)) return;
            Stream(id, text, state, completed);
            return;
        }

        // Reasoning is deliberately not shown: it is long, it is not a decision, and putting it in
        // the same panel as trading actions makes the actions harder to see.
        if (itemType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) return;

        // Tool activity. Announced once, when it starts — or on completion if that is all we saw.
        if (!eventType.EndsWith(".started", StringComparison.Ordinal) && !completed) return;
        if (!state.AnnouncedTools.Add(id)) return;

        var described = DescribeTool(itemType, item);
        if (described is not null) Append(new ChatTurn(ChatRole.Tool, described, DateTimeOffset.UtcNow));
    }

    static string? DescribeTool(string itemType, JsonElement item)
    {
        if (itemType.Contains("command", StringComparison.OrdinalIgnoreCase))
        {
            var command = Text(item, "command") ?? Text(item, "cmd");
            return command is null ? null : $"$ {Shorten(command, 300)}";
        }
        if (itemType.Contains("mcp", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            var name = Text(item, "tool") ?? Text(item, "name") ?? "a tool";
            var server = Text(item, "server");
            return server is null ? $"Used {name}" : $"Used {server}.{name}";
        }
        if (itemType.Contains("file", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("patch", StringComparison.OrdinalIgnoreCase))
        {
            var path = Text(item, "path") ?? Text(item, "file");
            return path is null ? "Edited files" : $"Edited {path}";
        }
        if (itemType.Contains("search", StringComparison.OrdinalIgnoreCase))
        {
            var query = Text(item, "query") ?? Text(item, "text");
            return query is null ? "Searched the web" : $"Searched: {Shorten(query, 200)}";
        }
        if (itemType.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
            itemType.Contains("todo", StringComparison.OrdinalIgnoreCase))
            return "Updated its plan";

        return null;
    }

    /// <summary>Emits only the part of the assistant text that has not been shown yet.</summary>
    void Stream(string id, string text, TurnState state, bool complete)
    {
        var seen = state.Partial.TryGetValue(id, out var s) ? s : "";
        if (text.Length > seen.Length && text.StartsWith(seen, StringComparison.Ordinal))
        {
            var suffix = text[seen.Length..];
            if (suffix.Length > 0) Delta?.Invoke(suffix);
        }
        else if (!string.Equals(text, seen, StringComparison.Ordinal))
        {
            // The runtime rewrote the message rather than extending it: start the delta over.
            Delta?.Invoke(text);
        }

        if (complete)
        {
            state.Complete(id);
            AppendAi(text);
            state.ProducedAnyMessage = true;
        }
        else
        {
            state.Remember(id, text);
            state.ProducedAnyMessage = true;
        }
    }

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Finds the assistant text in an event whose exact shape is not known, by looking a few levels
    /// down for the property names every one of these tools uses. Used only for runtimes whose
    /// stream format has not been confirmed.
    /// </summary>
    static string? FindText(JsonElement e, int depth)
    {
        if (depth < 0 || e.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "text", "content", "delta", "message" })
        {
            if (!e.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var nested = v.ValueKind == JsonValueKind.Object
                    ? FindText(v, depth - 1)
                    : v.EnumerateArray().Select(x => FindText(x, depth - 1)).FirstOrDefault(x => x is not null);
                if (nested is not null) return nested;
            }
        }

        foreach (var name in new[] { "part", "item", "info", "data" })
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            {
                var nested = FindText(v, depth - 1);
                if (nested is not null) return nested;
            }

        return null;
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    /// <summary>
    /// Kills the run in flight, and everything it started, without touching the session.
    ///
    /// "Everything it started" is what <see cref="ContainedProcess.Kill"/> buys and what the bare
    /// tree kill could not: a grandchild that detached is no longer anybody's child, so a walk down
    /// parent links stops one level above it. The job still names it on Windows; on macOS and Linux
    /// the turn's session does, and its parent links are frozen before anything is killed.
    /// </summary>
    public async Task CancelAsync()
    {
        var contained = _current;
        // THE TEARDOWN, AND IT IS AWAITED: Pause and Stop return once the tree is gone, not once a signal
        // has been sent. Off the caller's thread, because the caller is a button.
        if (contained is not null)
        {
            try { await Task.Run(contained.Kill); }
            catch (Exception) { /* already gone */ }
        }
        try { _cts?.Cancel(); }
        catch (Exception) { }
    }

    /// <summary>
    /// Cancels anything running and forgets the session, so the next message starts fresh — a stop no rotation ordered, which the
    /// next turn's record calls a <see cref="SessionWhy.Restart"/>.
    /// </summary>
    public Task StopAsync() => StopAsync(SessionWhy.Restart);

    /// <summary>The same stop, with why the next session is fresh (<c>U-org-packets-a</c>): the loop's rotation names its reason.</summary>
    public async Task StopAsync(string why)
    {
        await CancelAsync();
        _sessionExists = false;
        _threadId = null;
        _freshWhy = why;
        SetBusy(false);
    }

    void SetBusy(bool value)
    {
        if (_busy == value) return;
        _busy = value;
        StateChanged?.Invoke();
    }

    void AppendAi(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        Append(new ChatTurn(ChatRole.Ai, trimmed, DateTimeOffset.UtcNow));
    }

    void Append(ChatTurn turn)
    {
        lock (_historyLock) _history.Add(turn);
        TurnAdded?.Invoke(turn);
    }

    static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "…" + trimmed[^400..];
    }
}
