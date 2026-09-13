using System.Collections.Concurrent;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Gateway;

/// <summary>
/// WHAT ONE `backtest` REQUEST ASKED FOR. Every field arrived from an agent and not one of them is
/// trusted: the path is checked against the role homes, the numbers are checked by
/// <see cref="ExecutionModel.Declare"/>, and the program is parsed by the app.
/// </summary>
public sealed record BacktestAsk(
    string Strategy,
    long Dataset,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    decimal? Fees = null,
    decimal? Slippage = null,
    decimal? Increment = null,
    decimal? Capital = null);

/// <summary>
/// THE APP'S OWN RUNNER, ON THE AGENT'S REQUEST — AND IT IS A READ AS FAR AS TRADING IS CONCERNED.
///
/// <para><b>Nothing here places an order or grants anything.</b> `CLAUDE.md`: a backtest places no
/// order and grants no authority. This class touches no connector, reads no mode, checks no kill
/// switch and cannot change one — it parses a text, streams bars off a hashed file, evaluates them and
/// writes a measurement. The only thing an agent can do through it is find out what its own rule set
/// would have done, which is the work `docs/COUNCIL.md` says is the job.</para>
///
/// <para><b>It runs under the CALLER'S OWN launch identity.</b> The role and the attempt come from
/// <see cref="AgentContext"/> — the launch grant the app minted for that process, which `U-containment`
/// made the pipe's idea of who is calling — and never from the folder a file happened to be in. A
/// caller that proved no launch is refused the op outright: a run is recorded under a role, and the
/// reading every other table uses for a missing role (a row with no role is the chair's) is exactly the
/// one that must not be applied to a live caller.</para>
///
/// <para><b>The program is read from inside THAT role's home and nowhere else.</b> Not "a" role home:
/// the caller's own. An absolute path outside it is refused, so is a relative one that climbs out with
/// <c>..</c>, and so is a symlink that points out — <see cref="Resolve"/> compares lexically first and
/// then against every link on the way down. Without that check this op would be a general file-read
/// primitive for any file the app's own process can open, the database and the IPC token included, and
/// it would hand the contents back in a parse refusal that echoes the line it failed on; without the
/// role half of it, one role's launch could run and be credited with the other's program.</para>
///
/// <para><b>One run at a time per role.</b> A run is unbounded work on the caller's own connection, so
/// a second request from the same role is refused rather than queued: queueing would hold a pipe
/// handler open behind a run nobody is waiting for, and the honest answer to "you are already running
/// one" is to say so.</para>
///
/// <para><b>The app records the version and the run; the agent records nothing.</b> Ids are content
/// hashes computed here (`Backtest`), the metrics are computed from the app's own trace, and the tables
/// have no write path on the pipe at all.</para>
/// </summary>
public sealed class Backtests(TradingGateway gateway, Database db, Func<DateTimeOffset>? now = null)
{
    readonly StrategyStore _strategies = new(db);
    readonly CampaignStore _campaigns = new(db);
    readonly VenueStore _venues = new(db);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Roles with a run in flight right now. See the type's summary.</summary>
    readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <summary>The most closed trades one answer lists in full. The rest are counted.</summary>
    public const int TradesShown = 20;

    /// <summary>The ledger, for the report and for a reader. Written by this class and by nothing else.</summary>
    public StrategyStore Strategies => _strategies;

    /// <summary>
    /// Runs one backtest and records it, or refuses in words. The refusal is a
    /// <see cref="GatewayDeniedException"/> because every caller of this is answering a pipe request,
    /// and the agent has to be able to read what was wrong and fix it.
    /// </summary>
    public BacktestRan Run(AgentContext caller, BacktestAsk ask, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ask);

        var role = RoleOf(caller);
        var path = Resolve(role, ask.Strategy);

        // WHAT THE CALLER DECLARED IS CHECKED FIRST, AND ON ITS OWN. `Increment` can refuse — an
        // instrument nothing has recorded is not a run TradeAgent will guess its way through — and a
        // request that also names a fee of 10% has to hear about the FEE, which is the mistake it
        // actually made. So the caller's own four numbers are validated here, with the increment left
        // to its default when none was given, and the catalogue is not consulted until they pass.
        var asDeclared = ExecutionModel.Declare(ask.Fees, ask.Slippage, ask.Increment, ask.Capital);
        if (!asDeclared.Ok)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"that execution model cannot be run: {asDeclared.Why}.");

        string text;
        try
        {
            // NORMALISED AS IT IS READ. The parser is line-based and the file was written by an agent
            // on whatever platform this installation runs on; a CRLF file that parsed on one machine
            // and not on another would be the `U-crlf-strategy-win` defect with a strategy in it.
            text = File.ReadAllText(path).ReplaceLineEndings("\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{ask.Strategy}' could not be read: {ex.Message}");
        }

        var parse = StrategyParser.Parse(text);
        if (parse.Program is not { } program)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"that is not a program TradeAgent will run — {parse.Why}");

        // AFTER the parse, so a text that is not a program is refused for being one rather than for
        // the instrument of a dataset it was never going to be run over.
        var step = Increment(ask);
        var declared = ExecutionModel.Declare(ask.Fees, ask.Slippage, step.Value, ask.Capital);
        if (declared.Model is not { } model)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"that execution model cannot be run: {declared.Why}.");

        // THE TRIAL BUDGET IS ASKED BEFORE THE RUN, NOT AFTER IT. A caller told "your budget is spent"
        // after twenty minutes of evaluation has spent the budget to learn that it was spent — and the
        // run it just made is a peek at the data that the count was supposed to bound. There is a
        // campaign only where the owner has set a holdout; over a dataset with none, nothing is charged
        // and nothing is refused. A `fixture` dataset is free, so it is never refused here either.
        var campaign = _campaigns.OpenForDataset(ask.Dataset);
        var kind = EvaluationClass.Or(gateway.Datasets.ById(ask.Dataset)?.EvaluationClass);
        if (campaign is { } open && _campaigns.TrialRefusal(open.Id, kind) is { } spent)
            throw new GatewayDeniedException(ErrorCode.CAMPAIGN_BUDGET_REACHED, spent);

        if (!_running.TryAdd(role, 0))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"a backtest for {CouncilRoles.Title(role)} is already running. One at a time per role: "
                + "wait for that one to answer, then ask for this one.");

        try
        {
            // THE AUDIENCE IS THE CALLER'S, AND IT IS A PIPE CALLER WHATEVER ROLE IT PROVED. Both
            // directors and a connection that proved nothing get the same one: `BarAudience.Pipe` can
            // never read a holdout bar, and the role on it is only for the wording of the refusal.
            var run = Backtest.Over(
                gateway.Datasets, ask.Dataset, program, model, BarAudience.Pipe(role),
                ask.From, ask.To, stop: stop);

            if (run.Result is not { } result)
                throw new GatewayDeniedException(
                    run.IsHoldout ? ErrorCode.HOLDOUT_WITHHELD : ErrorCode.MARKET_DATA_UNAVAILABLE,
                    run.Why + ".");

            // A RUN THE APP ITSELF STOPPED IS NOT RECORDED. The fault is this process shutting down,
            // not the program's, and a FAULTED row blaming the strategy for it would be a record of
            // something that did not happen.
            if (!stop.IsCancellationRequested)
                Record(result, program, role, caller.AttemptId, campaign, kind, step.Source);

            return new BacktestRan(result, program, role, gateway.Datasets.ById(ask.Dataset)!, step.Source);
        }
        finally
        {
            _running.TryRemove(role, out _);
        }
    }

    /// <summary>
    /// THE ROLE THIS CALLER PROVED IT IS, or a refusal. Never a default and never the folder's.
    ///
    /// <para>A backtest is recorded under a role, and the only honest source of that role is the launch
    /// grant the caller presented. <c>CouncilRoles.Or</c> — which reads a missing role as the chair,
    /// correctly, for rows written before the council existed — is deliberately NOT used here: applied
    /// to a live caller it is the defect `U-containment` closed, a process holding nothing but the
    /// machine token being served as the Operations Director.</para>
    ///
    /// <para>The in-process operator is refused too, and that is not an oversight: the owner at the
    /// keyboard is not a council role, and recording their run as one role's work would put a figure in
    /// a lineage that role did not produce. A future in-process caller passes a role rather than being
    /// special-cased here.</para>
    /// </summary>
    static string RoleOf(AgentContext caller)
    {
        if (CouncilRoles.IsKnown(caller.Role)) return caller.Role!;

        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            "a backtest is recorded under the role that asked for it, and this connection presented no "
            + "launch grant — so there is no role to record it under and no folder to read the program "
            + "from. TradeAgent puts the grant in the environment of the process it starts; a caller "
            + "holding only the machine token is authenticated and is nobody.");
    }

    /// <summary>
    /// THE FULL PATH THIS PROGRAM IS AT, INSIDE <paramref name="role"/>'S OWN HOME.
    ///
    /// <para>A relative path is resolved against that home, which is where the agent typed it from and
    /// the only place it means anything. An absolute path is taken as given and then required to be
    /// under the same home. Both are checked TWICE: lexically after normalisation, so <c>..</c> is
    /// refused before anything is read, and against the real path with every symlink on the way down
    /// resolved, so a link inside the folder is not a door out of it.</para>
    /// </summary>
    static string Resolve(string role, string strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'strategy' is required: the path of a program file inside your own role folder, for "
                + "example strategies/ma-crossover.strategy.");

        var home = Full(Paths.RoleHome(role));

        // A PATH THE OS WILL NOT EVEN NORMALISE IS A REFUSAL, NOT AN EXCEPTION. `strategy` arrives from
        // an agent, and `Path.GetFullPath` throws on an embedded NUL, on a path past the platform's
        // limit and on a few other shapes; unhandled, those reach the agent as UNKNOWN_ERROR, which
        // tells it nothing it can act on. Totality is the rule the parser already follows for the same
        // reason — the text comes from a cheap model, so every input has an answer.
        string candidate;
        try
        {
            candidate = Path.IsPathRooted(strategy) ? Full(strategy) : Full(Path.Combine(home, strategy));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{strategy}' is not a path this machine can read as one ({ex.Message}). Name a file "
                + "inside your own role folder, for example strategies/ma-crossover.strategy.");
        }

        if (!Inside(home, candidate) || !Inside(Real(home), Real(candidate)))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{strategy}' is outside the {CouncilRoles.Title(role)}'s own folder, and a backtest reads "
                + "a program from inside it and from nowhere else — not from the other role's folder, and "
                + "not from anywhere else on this machine. Put the program in your own folder; "
                + "strategies/ is what it is for.");

        if (!File.Exists(candidate))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"there is no file at '{strategy}' in the {CouncilRoles.Title(role)}'s folder. Write the "
                + "program there first; 'trade material list' shows what TradeAgent can see.");

        return candidate;
    }

    /// <summary>
    /// The path with every symlink ON IT resolved, segment by segment — not only the last one.
    ///
    /// <para>Resolving only the final component would leave a symlinked DIRECTORY inside the folder as a
    /// door out of it, which is the same hole one level up. A link that is broken, looping or
    /// unreadable resolves to itself, which keeps it inside the folder and therefore refusable by the
    /// ordinary rule rather than by an exception.</para>
    /// </summary>
    static string Real(string path)
    {
        var full = Full(path);
        var root = Path.GetPathRoot(full) ?? "";
        var here = root;

        foreach (var segment in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            here = Path.Combine(here, segment);
            if (Link(here) is { } target) here = Full(target);
        }

        return here;
    }

    static string? Link(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException) { return null; }                  // a broken or looping link is not a way out
        catch (UnauthorizedAccessException) { return null; }
    }

    static string Full(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Whether <paramref name="path"/> is inside <paramref name="directory"/>, both already full paths.
    ///
    /// <para>The separator is appended before the comparison, so <c>workspace/agent-elsewhere</c> is
    /// not inside <c>workspace/agent</c> — a prefix test without it is the classic way a containment
    /// check lets a sibling through. Case-insensitive on Windows and on macOS, where the file systems
    /// are; ordinal elsewhere.</para>
    /// </summary>
    static bool Inside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Full(directory)) + Path.DirectorySeparatorChar;
        var how = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return path.StartsWith(root, how);
    }

    /// <summary>
    /// WHAT INCREMENT THIS RUN USES, AND WHO SAID SO.
    ///
    /// <para><b>A declared one always wins.</b> The catalogue is a default for a caller that gave no
    /// number, never an override of one that did: a run declared at a coarser step is a legitimate
    /// question about a coarser step, and substituting the app's own number would answer a different
    /// one. The caller's is recorded as the caller's.</para>
    ///
    /// <para><b>Otherwise it comes off the DATASET's own row.</b> The venue and the instrument are what
    /// the collector wrote down beside the bytes — not the instrument named in the program, which is a
    /// line an agent types and can arrange, and a run whose size came from a symbol the agent chose
    /// rather than from the evidence it ran over would be bound to an instrument it was never measured
    /// on.</para>
    ///
    /// <para><b>The provenance is not part of the run's identity.</b> The number goes into
    /// <see cref="ExecutionModel"/> and is hashed there, exactly as a declared one is; where it came
    /// FROM is recorded beside the run. Folding it into <c>Canonical</c> would move every run id this
    /// installation has already written.</para>
    /// </summary>
    IncrementChosen Increment(BacktestAsk ask)
    {
        if (ask.Increment is { } declared)
            return new IncrementChosen(declared, $"declared by the caller: {Plain(declared)}");

        // A dataset id nobody has a row for is not this method's refusal to make: `Backtest.Over`
        // answers it with the sentence it already has, and a second wording of the same fact here
        // would be a second answer to one question.
        if (gateway.Datasets.ById(ask.Dataset) is not { } set) return new IncrementChosen(null, null);

        if (set.VenueId is not { Length: > 0 } venue || set.InstrumentSymbol is not { Length: > 0 } symbol)
            throw Guessing(
                $"dataset {ask.Dataset} records no venue and no instrument, so there is nothing to read a "
                + "quantity increment from");

        if (_venues.Instrument(venue, symbol) is not { } row)
            throw Guessing(
                $"this installation's venue catalogue holds no instrument '{symbol}' on '{venue}', which "
                + $"is what dataset {ask.Dataset} records its bars as being of");

        if (!row.Verified)
            throw Guessing(
                $"'{symbol}' on '{venue}' is recorded with a quantity increment of "
                + $"{Plain(row.QuantityIncrement)}, and that row has not been verified — its source is "
                + $"“{row.Source}”, so nothing has confirmed the number against the venue's own "
                + "instrument definition");

        return new IncrementChosen(row.QuantityIncrement,
            $"the venue catalogue: {venue}/{symbol}, recorded from {row.Source}");
    }

    /// <summary>The increment a run will use and the sentence that says where it came from.</summary>
    readonly record struct IncrementChosen(decimal? Value, string? Source);

    /// <summary>
    /// A REFUSAL, NEVER A GUESS — the one shape this whole unit exists to produce.
    ///
    /// <para>The alternative is <c>ExecutionModel.Frictionless</c>'s 1, and a whole unit is not a
    /// conservative fallback on an instrument nobody recorded: it is a number with no relationship to
    /// the venue, and every figure a run computes under it is about a position that could not have been
    /// taken. <c>FakeBroker.TickSize</c> answers null for an unknown symbol for exactly this reason —
    /// substituting a grid makes "this platform cannot tell you" indistinguishable from a
    /// measurement.</para>
    ///
    /// <para>Both routes out are named, because the two are for different people. The AGENT can declare
    /// <c>--increment</c> and own the number, which the run then records as the caller's. The ACCOUNT
    /// OWNER can confirm the row in <c>venues.json</c>, which is the one-line data fix
    /// <c>docs/DECISIONS.md</c>:73-78 asks for and needs no rebuild.</para>
    /// </summary>
    static GatewayDeniedException Guessing(string why) => new(ErrorCode.INVALID_REQUEST,
        $"this run declared no quantity increment and TradeAgent will not invent one: {why}. A size is "
        + "rounded DOWN to the increment, so a number nobody recorded would make every figure in the "
        + "result about a position that could not have been taken. Either pass --increment yourself — "
        + "the run records that the number was yours — or ask the account owner to record the "
        + "instrument in venues.json in TradeAgent's own folder. 'trade venue list' shows what is "
        + "recorded today and which rows have been verified.");

    /// <summary>
    /// A decimal with its trailing zeros gone, invariant, for a sentence a person reads.
    /// <c>StrategyParser.Number</c> does the same job for the CANONICAL form and is internal to Core;
    /// this is prose and is deliberately not the same function, because the canonical form must never
    /// start depending on how a refusal happens to print a number.
    /// </summary>
    static string Plain(decimal value) =>
        value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);

    void Record(BacktestResult result, StrategyProgram program, string role, string? attempt,
        CampaignRow? campaign, string kind, string? incrementSource)
    {
        var at = _now();
        var metrics = result.Metrics;

        // ONE TRANSACTION FOR THE VERSION, THE RUN AND ITS TRADES. `Database.Write` joins the
        // transaction already open, so a crash between the three leaves none of them rather than a run
        // whose version nobody can look up.
        db.Write(_ =>
        {
            _strategies.RecordVersion(new StrategyVersionRow(
                program.StrategyId, program.Source, program.Canonical, program.Manifest,
                // THE ROLE AND THE ATTEMPT ARE THE CALLER'S OWN, off the launch grant, and never the
                // folder's: a file's location is something an agent can arrange, and a grant is not.
                StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, at, role, attempt));

            _strategies.RecordRun(new StrategyRunRow(
                result.RunId, result.VersionId, result.Request.DatasetId, result.Request.DatasetSha256,
                result.Request.From, result.Request.To, result.Request.Model.Canonical,
                result.Outcome.ToString(), result.FaultReason,
                metrics.Bars, metrics.Trades, metrics.Wins, metrics.Signals, metrics.Fills,
                metrics.ExposureBars, metrics.MissingMinutes, metrics.Faults,
                metrics.GrossPnl, metrics.Fees, metrics.NetPnl, metrics.MaxDrawdown,
                result.Trace.Sha256, at, role, attempt) { IncrementSource = incrementSource },
                [.. result.Trades.Select(t => new StrategyTradeRow(
                    result.RunId, t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice,
                    t.Quantity, t.Reason.ToString(), t.Fees, t.Pnl))]);

            // THE TRIAL, IN THE SAME TRANSACTION AS THE RUN IT IS THE COST OF, AND IT IS THE GATE.
            //
            // Either both land or neither does: a run recorded without its trial is a peek nobody was
            // charged for, and a trial without its run is a charge for nothing. Keyed by the campaign,
            // the version and the run — never by the role or the attempt, which are on the run row.
            //
            // THE REFUSAL IS WHAT CLOSES THE BY-ONE RACE. `TrialRefusal` above is a LOOK: it reads the
            // count in its own transaction before a run that takes minutes, so two roles asking for the
            // last trial of a campaign both pass it. `RegisterTrial` reads the count and writes the row
            // in ONE transaction, so exactly one of them takes it — and the loser's run is rolled back
            // with everything else in this write and its figures are never served. The compute is spent
            // either way; what must not happen is a run over the owner's data standing in the ledger
            // with no trial against it, which is the peek the count exists to bound recorded as free.
            if (campaign is { } open)
            {
                var registered = _campaigns.RegisterTrial(open.Id, result.VersionId, result.RunId, kind, at);
                if (!registered.Ok)
                    throw new GatewayDeniedException(ErrorCode.CAMPAIGN_BUDGET_REACHED, registered.Why);
            }

            return 0;
        });
    }
}

/// <summary>One completed run, with what it was a run of. What the pipe turns into its answer.</summary>
public sealed record BacktestRan(
    BacktestResult Result,
    StrategyProgram Program,
    string Role,
    DatasetRecord Dataset,
    /// <summary>
    /// Where the run's quantity increment came from, in words — the caller's own declaration or the
    /// venue catalogue row it was read out of. It is in the ANSWER as well as on the run row because
    /// the four numbers in <c>execution_model</c> say what was used and not who said so, and an agent
    /// reading a size it did not choose has to be able to see what chose it.
    /// </summary>
    string? IncrementSource);
