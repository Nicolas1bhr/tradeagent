using System.Collections.Concurrent;
using TradeAgent.Core;
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
/// <para><b>The program is read from inside a role's home and nowhere else.</b> An absolute path
/// outside every role home is REFUSED, and so is a relative one that climbs out with <c>..</c> —
/// <see cref="Resolve"/> normalises first and compares afterwards, which is the only order that works.
/// Without that check this op would be a general file-read primitive for any file the app's own
/// process can open, the database and the IPC token included, and it would hand the contents back in
/// a parse refusal that echoes the line it failed on.</para>
///
/// <para><b>One run at a time per role.</b> A run is unbounded work on the caller's own connection, so
/// a second request from the same role is refused rather than queued: queueing would hold a pipe
/// handler open behind a run nobody is waiting for, and the honest answer to "you are already running
/// one" is to say so. The ROLE is the role whose home the program was read from — a measurement, not a
/// claim — because the pipe does not yet carry an authenticated role.</para>
///
/// <para><b>The app records the version and the run; the agent records nothing.</b> Ids are content
/// hashes computed here (`Backtest`), the metrics are computed from the app's own trace, and the tables
/// have no write path on the pipe at all.</para>
/// </summary>
public sealed class Backtests(TradingGateway gateway, Database db, Func<DateTimeOffset>? now = null)
{
    readonly StrategyStore _strategies = new(db);
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
    public BacktestRan Run(BacktestAsk ask, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var (path, role) = Resolve(ask.Strategy);

        var declared = ExecutionModel.Declare(ask.Fees, ask.Slippage, ask.Increment, ask.Capital);
        if (declared.Model is not { } model)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"that execution model cannot be run: {declared.Why}.");

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

        if (!_running.TryAdd(role, 0))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"a backtest for {CouncilRoles.Title(role)} is already running. One at a time per role: "
                + "wait for that one to answer, then ask for this one.");

        try
        {
            var run = Backtest.Over(
                gateway.Datasets, ask.Dataset, program, model, ask.From, ask.To, stop: stop);

            if (run.Result is not { } result)
                throw new GatewayDeniedException(ErrorCode.MARKET_DATA_UNAVAILABLE, run.Why + ".");

            // A RUN THE APP ITSELF STOPPED IS NOT RECORDED. The fault is this process shutting down,
            // not the program's, and a FAULTED row blaming the strategy for it would be a record of
            // something that did not happen.
            if (!stop.IsCancellationRequested) Record(result, program, role);

            return new BacktestRan(result, program, role, gateway.Datasets.ById(ask.Dataset)!);
        }
        finally
        {
            _running.TryRemove(role, out _);
        }
    }

    /// <summary>
    /// THE FULL PATH THIS PROGRAM IS AT, AND THE ROLE WHOSE HOME IT IS IN.
    ///
    /// <para>A relative path is resolved against each role's home in turn and the first one that HAS
    /// the file wins, because the agent typed it from inside its own home and that is the only place
    /// it means anything. An absolute path is taken as given. Either way the answer is then required
    /// to be inside a role home — after normalisation, so <c>../../state/tradeagent.db</c> is refused
    /// rather than followed.</para>
    /// </summary>
    static (string Path, string Role) Resolve(string strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'strategy' is required: the path of a program file inside your own role folder, for "
                + "example strategies/ma-crossover.strategy.");

        var homes = CouncilRoles.All.Select(role => (Role: role, Home: Paths.RoleHome(role))).ToList();

        var candidates = new List<string>();
        if (Path.IsPathRooted(strategy)) candidates.Add(Full(strategy));
        else foreach (var home in homes) candidates.Add(Full(Path.Combine(home.Home, strategy)));

        var anyInsideARoleHome = false;

        foreach (var candidate in candidates)
        {
            var owner = homes.FirstOrDefault(h => Inside(h.Home, candidate)).Role;
            if (owner is null) continue;                       // outside every role home: not this one

            anyInsideARoleHome = true;
            if (File.Exists(candidate)) return (candidate, owner);
        }

        // THE TWO REFUSALS ARE DIFFERENT AND ARE WORDED DIFFERENTLY. A path outside the role homes is a
        // request this op will never serve; a path inside one with no file there is a typo.
        if (!anyInsideARoleHome)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{strategy}' is outside your role folder, and a backtest reads a program from inside it "
                + "and from nowhere else. Put the program in your own folder — strategies/ is what it is "
                + "for — and name it from there.");

        throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
            $"there is no file at '{strategy}' in your role folder. Write the program there first; "
            + "'trade material list' shows what TradeAgent can see.");
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

    void Record(BacktestResult result, StrategyProgram program, string role)
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
                StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, at, role,
                // THE ATTEMPT IS NOT KNOWN HERE. The agent-facing pipe carries a session string and no
                // attested attempt, and an attempt guessed from a session an agent chose would be
                // provenance nobody could stand behind. Null says so.
                null));

            _strategies.RecordRun(new StrategyRunRow(
                result.RunId, result.VersionId, result.Request.DatasetId, result.Request.DatasetSha256,
                result.Request.From, result.Request.To, result.Request.Model.Canonical,
                result.Outcome.ToString(), result.FaultReason,
                metrics.Bars, metrics.Trades, metrics.Wins, metrics.Signals, metrics.Fills,
                metrics.ExposureBars, metrics.MissingMinutes, metrics.Faults,
                metrics.GrossPnl, metrics.Fees, metrics.NetPnl, metrics.MaxDrawdown,
                result.Trace.Sha256, at, role, null),
                [.. result.Trades.Select(t => new StrategyTradeRow(
                    result.RunId, t.Ordinal, t.EntryBar, t.EntryPrice, t.ExitBar, t.ExitPrice,
                    t.Quantity, t.Reason.ToString(), t.Fees, t.Pnl))]);

            return 0;
        });
    }
}

/// <summary>One completed run, with what it was a run of. What the pipe turns into its answer.</summary>
public sealed record BacktestRan(
    BacktestResult Result,
    StrategyProgram Program,
    string Role,
    DatasetRecord Dataset);
