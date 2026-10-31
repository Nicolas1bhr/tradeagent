using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// Owns the agent's lifecycle. One agent process at a time, deliberately: two agents sharing one
/// workspace and one trading account is a race with real money in it, and on a low-spec laptop it is
/// also simply too much load.
/// </summary>
/// <param name="selectedModel">
/// The model the owner chose on the Safety page, or null for the runtime's default. Handed to every
/// runtime this supervisor prepares, so the choice survives a restart of the agent.
/// </param>
/// <param name="attemptId">
/// The AI attempt open for ONE ROLE's launch about to happen, read at the launch. Handed to every
/// runtime this supervisor prepares so that the grant a turn's process carries names the attempt its
/// cost is committed against. Per role, because the meter holds one open attempt per role.
/// </param>
/// <param name="launchRefusal">
/// The protected configuration's answer, asked at every launch: a sentence when no AI runtime may be
/// started at all, null when one may. Handed to every runtime this supervisor prepares.
/// </param>
/// <param name="presence">
/// The register the processes of every runtime this supervisor prepares report to. Null is the
/// process-wide one, which is the only value the product passes; a test that drives the app's own
/// start path passes its own, because the shared register is sticky — see
/// <c>CliAgentRuntime.Presence</c>.
/// </param>
/// <param name="seats">
/// The activation gate's register, handed to every runtime this supervisor prepares and asked by each of their sessions immediately
/// before a process starts. Null is the process-wide one, which the product fills.
/// </param>
/// <param name="positions">
/// The chart's active positions beside the legacy pair, read at every prepare — each gets a home of its own under
/// <c>org/&lt;id&gt;</c>. Null is the legacy pair alone. A read that throws builds the legacy pair alone: a home that
/// could not be listed is a home not built, never one guessed.
/// </param>
public sealed class AgentSupervisor(HealthRegistry health, Func<string?>? selectedModel = null,
    Func<string, string?>? attemptId = null, Func<string?>? launchRefusal = null,
    AgentPresence? presence = null, Func<IReadOnlyList<string>>? positions = null, SeatRegister? seats = null,
    Func<Uri?>? door = null, CellRoute? cells = null)
{
    IReadOnlyList<string> Positions()
    {
        try { return positions?.Invoke() ?? []; }
        catch (Exception) { return []; }
    }

    readonly SemaphoreSlim _gate = new(1, 1);
    IAgentRuntime? _runtime;

    public IAgentRuntime? Current => _runtime;
    public string SessionId { get; private set; } = "";
    public bool Running { get; private set; }

    /// <summary>
    /// The CHAIR'S directory, as handed to the runtime. Empty until something is prepared.
    ///
    /// The mission loop reads <c>.tradeagent/next.json</c> from here — the AI's request to be left
    /// alone for a while — so this is the one place that answer can come from without the app
    /// guessing at a path the supervisor already knows. Every role's home is in
    /// <see cref="Workspaces"/>; this one is Operations', because it is the working directory the
    /// window's own conversation runs in and the one an install has always had.
    /// </summary>
    public string Workspace { get; private set; } = "";

    /// <summary>
    /// EVERY COUNCIL ROLE'S HOME, by role, as of the last prepare. Empty until something is prepared.
    ///
    /// Both are built on every prepare rather than lazily when a role first turns: the mission file
    /// is regenerated so it can never describe a stale world, and a role whose folder appears only
    /// once it is scheduled is a role whose first turn runs against a world nobody wrote down.
    /// </summary>
    public IReadOnlyDictionary<string, string> Workspaces
    {
        get { lock (_homes) return _workspaces; }
        private set { lock (_homes) _workspaces = value; }
    }

    IReadOnlyDictionary<string, string> _workspaces = new Dictionary<string, string>();

    /// <summary>Held while <see cref="Workspaces"/> is read, replaced, or grown by one position's build (<see cref="EnsureHome"/>).</summary>
    readonly object _homes = new();

    /// <summary>
    /// A POSITION HIRED AND ACTIVATED WHILE THE APP RUNS GETS ITS HOME BEFORE ITS FIRST LAUNCH (<c>U-home-on-hire</c> item 1; R25 D3): the
    /// home of <paramref name="position"/> as the last prepare — or an earlier call of this — built it; and where neither did and the chart
    /// holds it active now, built here, through the start-up pass's own body (<see cref="WorkspaceBuilder.BuildPosition"/>: seat home, canon
    /// and guide for each runtime it can run on, research library, context, its cell's in/out), with its kind and place from the same
    /// register. ATOMIC: the home joins <see cref="Workspaces"/> only once that body has returned — built, or refused in words that every
    /// launch of it then meets (<see cref="WorkspaceBuilder.RefusalFor"/>) — under the one lock the map is read and replaced under, so no
    /// launch is ever handed a half-written home and two callers never build it twice. Once per position per prepare: a refused home is
    /// rebuilt by the next start or the owner's repair, never by a launch after launch. Null before anything is prepared, and for a position
    /// the chart does not hold active — a home is never built for a guess.
    /// </summary>
    public string? EnsureHome(string position, Func<WorkspaceContext> ctx)
    {
        lock (_homes)
        {
            if (_workspaces.TryGetValue(position, out var built)) return built;
            if (_workspaces.Count == 0) return null;
        }
        // THE CHART AND THE CONTEXT ARE READ OUTSIDE THE LOCK (each takes locks of its own); the build and the map's growth inside it.
        if (!Positions().Contains(position, StringComparer.Ordinal)) return null;
        var context = ctx();
        var register = seats ?? PositionActivation.Seats;
        lock (_homes)
        {
            if (_workspaces.TryGetValue(position, out var built)) return built;
            var home = WorkspaceBuilder.BuildPosition(context, position, kindOf: register.KindOf, placeOf: register.PlaceOf);
            _workspaces = new Dictionary<string, string>(_workspaces, StringComparer.Ordinal) { [position] = home };
            return home;
        }
    }

    /// <summary>
    /// The version probe's deadline for every runtime this supervisor prepares. The product never
    /// sets it; a test of a program slower than the deadline sets it short — see
    /// <see cref="CliAgentRuntime.VersionDeadline"/>.
    /// </summary>
    internal TimeSpan VersionDeadline { get; set; } = CliAgentRuntime.DefaultVersionDeadline;

    public async Task<IAgentRuntime> PrepareAsync(RuntimeManifest manifest, WorkspaceContext ctx, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var runtime = new CliAgentRuntime(manifest, selectedModel, attemptId, launchRefusal, presence, seats: seats, door: door, cells: cells)
            {
                VersionDeadline = VersionDeadline
            };
            var detection = await runtime.DetectAsync(ct);
            health.Set(Components.AgentRuntime,
                detection.Installed ? HealthState.READY : HealthState.FAILED,
                detection.Installed
                    // AND A RUNTIME WHOSE COMMAND LINE CARRIES THE PROMPT SAYS SO HERE, on the line the owner reads as it is
                    // launched (U-prompt-stdin): every process of the user can read a command line. Measured on the argv this
                    // build makes for it, not taken from the manifest's word.
                    ? $"{manifest.DisplayName} {detection.Version}" + (AgentArgs.CarriesPrompt(manifest) ? $"; {AgentArgs.InArgv}" : "")
                    // The program's own words where there are any: a runtime whose file is on the
                    // disk and whose launcher will not start is not "not installed", and this row is
                    // the only thing the owner sees about it.
                    : detection.Reason ?? $"{manifest.DisplayName} is not installed");

            SessionId = $"agent-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var homes = BuildAll(ctx);
            Workspaces = homes;
            var workspace = homes[CouncilRoles.Operations];
            Workspace = workspace;
            health.Set(Components.Workspace, HealthState.READY, workspace);

            await runtime.CreateEnvironmentAsync(workspace, WorkspaceBuilder.EnvironmentFor(SessionId, workspace), ct);
            _runtime = runtime;
            return runtime;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_runtime is null) throw new TradeAgentException(ErrorCode.AI_RUNTIME_NOT_FOUND, "no runtime prepared");
        await _gate.WaitAsync(ct);
        try
        {
            health.Set(Components.AgentProcess, HealthState.STARTING);
            await _runtime.StartAsync(ct);
            Running = true;
            health.Set(Components.AgentProcess, HealthState.READY);
        }
        catch (Exception ex)
        {
            Running = false;
            health.Set(Components.AgentProcess, HealthState.FAILED, ex.Message);

            // A RUNTIME THAT WOULD NOT START IS NOT KEPT. Left here it would still answer Current,
            // and the app reads its conversation off Current: the mission loop would go on launching
            // turns through a runtime that was just refused — each one refused again at the launch,
            // and each one recorded as a launch held against the day's ceiling — while the card read
            // anything but "stopped". Stopped before it is dropped, so nothing it began before it
            // threw outlives it.
            var refused = _runtime;
            _runtime = null;
            if (refused is not null)
            {
                try { await refused.StopAsync(CancellationToken.None); }
                catch (Exception) { /* it did not start; there is nothing of it left to stop */ }
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_runtime is not null) await _runtime.StopAsync(ct);
            Running = false;
            health.Set(Components.AgentProcess, HealthState.PAUSED, "stopped");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Every position's home, rebuilt. A home that could not be rebuilt refuses only its own position (<see cref="WorkspaceBuilder.BuildAll"/>)
    /// — except the chair's, which is this supervisor's own working directory: a start whose chair has no home is no start.
    /// </summary>
    Dictionary<string, string> BuildAll(WorkspaceContext ctx)
    {
        var register = seats ?? PositionActivation.Seats;
        var homes = WorkspaceBuilder.BuildAll(ctx, positions: Positions(), kindOf: register.KindOf, placeOf: register.PlaceOf);
        if (WorkspaceBuilder.RefusalFor(homes[CouncilRoles.Operations]) is { } why) throw new IOException(why);
        return homes;
    }

    /// <summary>Refreshes the instruction file so a restarted agent never reads a stale world.</summary>
    public async Task RestartAsync(WorkspaceContext ctx, CancellationToken ct = default)
    {
        Workspaces = BuildAll(ctx);
        await StopAsync(ct);
        await StartAsync(ct);
    }
}
