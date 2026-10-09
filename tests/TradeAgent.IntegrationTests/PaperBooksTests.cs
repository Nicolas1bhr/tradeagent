using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;
using static TradeAgent.Tests.Integration.ForwardRunnerTests;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// EACH PAPER RUN HOLDS, CLOSES AND IS CHARGED ITS OWN BOOK (<c>U-paper-books</c>): two versions on one symbol never
/// net each other, a run's END sells only what it bought, and the envelope's ceilings bound the runs' sum.
///
/// <para>The paper account is one position per symbol (<c>PaperBook</c>), so two runs on BTCUSDT hold one position at
/// the venue — their SUM — and so does a run beside the owner's own holding. Everything a run's orders passed through
/// read that sum: its stop, its exit and its maximum hold were refused <c>POSITION_MOVED</c> while anyone else held,
/// its END sold everybody's holding as its own, and its opener was charged everybody's exposure. A run's own book is
/// its own <c>deployment_op</c> rows joined to the <c>fill</c> rows under their request ids.</para>
///
/// <para>The harness is <see cref="ForwardRunnerTests"/>'s: the paper connector over the forward ledger through the
/// shipped adapter, the runner, and every order through <c>PlaceAsync</c>. Two runs at once need a grant of two, which
/// the owner's card never writes (<c>PaperDeploymentsPerEnvelope</c> is 1): it is written to the ledger here, as
/// <c>PaperDeploymentExitHygieneTests</c> writes one. What a run holds is worked out HERE, off the fill ledger — its
/// own request ids, buys less sells — never by the code under test. No venue is reached.</para>
/// </summary>
public class PaperBooksTests(ITestOutputHelper log)
{
    const string Head =
        "instrument BTCUSDT\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\n";

    /// <summary>A one-minute BTCUSDT program: its size, any declarations, and one exit and one entry rule.</summary>
    static string Program(string size, string declarations, string exit, string entry) =>
        Head + $"size fixed {size}\n" + declarations + $"exit when close < {exit}\nentry when close > {entry}\n";

    // ---------------------------------------------------------------- the harness

    /// <summary>
    /// THE OWNER'S GRANT OF TWO RUNS AT A TIME ON BTCUSDT, written to the ledger: 5 at a time and 5,000,000, unless a
    /// test is about the numbers.
    /// </summary>
    static PaperEnvelopeRow GrantOfTwo(Rig rig, decimal quantity = 5m, decimal? notional = 5_000_000m)
    {
        var granted = rig.Gw.Envelopes.Grant(new PaperEnvelopeRow(
            "", rig.Gw.Connector.Id, PaperConnector.TheAccount, "BTCUSDT", "USDT", quantity, notional, 2,
            rig.Origin, rig.Origin.AddDays(30), "test: two runs at a time on one symbol", null));
        Assert.True(granted.Ok, granted.Why);
        return granted.Envelope!;
    }

    /// <summary>
    /// TWO RUNS ON BTCUSDT: the grant of two, both versions judged, and the app's own sweeps allocating and starting
    /// both. Answers the rig and the two runs, A and B.
    /// </summary>
    static async Task<(Rig Rig, StrategyDeploymentRow A, StrategyDeploymentRow B)> TwoRunsAsync(string a, string b)
    {
        var rig = await ReadyAsync(a, seed: false);
        GrantOfTwo(rig);

        var versionA = Judged(rig.Db, a, rig.Origin);
        var versionB = Judged(rig.Db, b, rig.Origin);
        Assert.Equal(2, rig.Gw.AllocatePaperDue(rig.Origin));
        Assert.Equal(2, rig.Gw.StartPaperDeploymentsDue(rig.Origin));

        var open = rig.Gw.Deployments.Open();
        return (rig, Assert.Single(open, d => d.VersionId == versionA), Assert.Single(open, d => d.VersionId == versionB));
    }

    /// <summary>
    /// ONE CLOSED MINUTE, THE CONNECTOR ASKED ABOUT IT — which settles its book on that minute — AND TWO PASSES OF THE
    /// RUNNER OVER EVERY RUN, the second of them after a position read: what the app does when a minute closes.
    /// </summary>
    static async Task TickAsync(Rig rig, int minute, decimal open, decimal high, decimal low, decimal close)
    {
        rig.Bar(minute, open, high, low, close);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await Position(rig);
        await rig.Runner.AdvanceAsync();
    }

    /// <summary>One run's fills off the ledger: every fill under a request id one of its operations was written under.</summary>
    static List<Fill> FillsOf(Rig rig, StrategyDeploymentRow run)
    {
        var ids = rig.Gw.Deployments.OpsOf(run.Id).Select(o => o.RequestId).ToHashSet(StringComparer.Ordinal);
        return [.. rig.Gw.Fills.Since(null).Where(f => f.RequestId is { } r && ids.Contains(r))];
    }

    /// <summary>What one run holds, worked out here and not by the code under test: its own buys less its own sells.</summary>
    static decimal Holds(Rig rig, StrategyDeploymentRow run) =>
        FillsOf(rig, run).Sum(f => string.Equals(f.Side, nameof(OrderSide.Buy), StringComparison.OrdinalIgnoreCase)
            ? f.Quantity : -f.Quantity);

    static string LineOf(Rig rig, StrategyDeploymentRow run) =>
        rig.Gw.DeploymentReadings().Single(d => d.Id == run.Id).Line;

    static List<DeploymentOpRow> OpsOf(Rig rig, StrategyDeploymentRow run, string kind) =>
        [.. rig.Gw.Deployments.OpsOf(run.Id).Where(o => o.Kind == kind)];

    /// <summary>The order at the wire one operation became, by the client order id it carries.</summary>
    static async Task<OrderInfo> OrderOf(Rig rig, DeploymentOpRow op) =>
        Assert.Single(await Wire(rig), o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(op.RequestId));

    void Show(Rig rig, params StrategyDeploymentRow[] runs)
    {
        foreach (var run in runs)
        {
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id))
                log.WriteLine($"{StrategyDeploymentRow.Short(run.Id)} {op.RequestId} {op.Kind} {op.State} "
                              + $"({rig.Gw.Requests.Get(op.RequestId)?.State.ToString() ?? "no row"}) — {op.Answer}");
            log.WriteLine($"{StrategyDeploymentRow.Short(run.Id)} holds {Holds(rig, run)} — {LineOf(rig, run)}");
        }
    }

    // ---------------------------------------------------------------- (a)

    /// <summary>
    /// (a) TWO RUNS ON ONE SYMBOL EACH REST THEIR PROTECTION AND EXIT THEIR OWN, AND THE ACCOUNT HOLDS THEIR SUM.
    ///
    /// <para>A and B enter on the same minute and both fill at 100: the account holds 2. Each run's stop goes to the
    /// venue sized from its own book, 1; A's exit then takes its stop off and sells its own 1 at 97, and B goes on
    /// holding, its stop still resting.</para>
    ///
    /// <para><b>RED on the base</b>: both stops are refused <c>POSITION_MOVED</c> — the close of 1 read against the
    /// account's 2 — so neither run rests any protection, and A's exit is refused the same way while B holds.
    /// <b>Mutant</b> — the run's branch of the stale-close read reading the account's position — goes red the same
    /// way.</para>
    /// </summary>
    [Fact]
    public async Task Two_runs_on_one_symbol_each_rest_protection_and_exit_their_own()
    {
        var (rig, a, b) = await TwoRunsAsync(
            Program("1", "stop percent 5\n", exit: "98", entry: "99"),
            Program("1", "stop percent 10\n", exit: "50", entry: "99"));
        await using var _ = rig;

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // both signal from a close of 100
        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // both fill at this open, 100, and protection goes on
        var bothIn = await Position(rig);
        Show(rig, a, b);

        var stopA = Assert.Single(OpsOf(rig, a, DeploymentOpKind.Stop));
        var stopB = Assert.Single(OpsOf(rig, b, DeploymentOpKind.Stop));
        Assert.Equal(2m, bothIn);
        Assert.Equal(DeploymentOpState.Resolved, stopA.State);
        Assert.Equal(DeploymentOpState.Resolved, stopB.State);

        await TickAsync(rig, 4, 100m, 100.2m, 96.5m, 97m);      // A: exit when close < 98 — its stop off, its exit out
        await TickAsync(rig, 5, 97m, 97.5m, 96.5m, 97m);        // the minute already in progress
        await TickAsync(rig, 6, 97m, 97.5m, 96.5m, 97m);        // A's exit fills at this open, 97
        Show(rig, a, b);

        // EACH RUN'S STOP RESTED AT THE VENUE AT ITS OWN SIZE: A's taken off by its exit, B's still working.
        var restedA = await OrderOf(rig, stopA);
        var restedB = await OrderOf(rig, stopB);
        Assert.Equal((OrderType.Stop, 1m, (decimal?)95m, ExecutionState.CANCELLED),
            (restedA.Type, restedA.Quantity, restedA.StopPrice, restedA.State));
        Assert.Equal((OrderType.Stop, 1m, (decimal?)90m, ExecutionState.WORKING),
            (restedB.Type, restedB.Quantity, restedB.StopPrice, restedB.State));

        // A'S EXIT SOLD ITS OWN 1, AND IS ITS OWN ROUND TRIP IN THE LEDGER.
        var exit = Assert.Single(OpsOf(rig, a, DeploymentOpKind.Exit));
        Assert.Equal(DeploymentOpState.Resolved, exit.State);
        var sold = await OrderOf(rig, exit);
        Assert.Equal((OrderSide.Sell, 1m, ExecutionState.FILLED), (sold.Side, sold.Quantity, sold.State));
        Assert.Equal(new[] { (nameof(OrderSide.Buy), 100m), (nameof(OrderSide.Sell), 97m) },
            FillsOf(rig, a).Select(f => (f.Side, f.Price)));

        // AND THE ACCOUNT HOLDS THE SUM OF THE TWO BOOKS.
        Assert.Equal(0m, Holds(rig, a));
        Assert.Equal(1m, Holds(rig, b));
        Assert.Equal(1m, await Position(rig));

        // EACH RUN'S LINE SAYS WHAT IT HOLDS, AT WHAT AVERAGE, AND WHAT IT HAS REALISED AFTER COSTS.
        Assert.Contains("Its own book: holds nothing, realised -3 after 0 in costs, over 2 fills.", LineOf(rig, a),
            StringComparison.Ordinal);
        Assert.Contains("Its own book: holds 1 at an average of 100, realised 0 after 0 in costs, over 1 fill.",
            LineOf(rig, b), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- (b)

    /// <summary>
    /// (b) AN END SELLS ONLY ITS OWN RUN'S HOLDING.
    ///
    /// <para>A and B both hold 1; the owner stops A. Its END closes exactly A's book — a market sell of 1 — and once that
    /// fills the account holds B's 1, B's book says so, and B is still running.</para>
    ///
    /// <para><b>RED on the base</b>: A's END goes through <c>CloseAsync</c>, which sizes the ACCOUNT's position, and
    /// sells 2 under A's request id — B's holding sold as A's, B's book left long with no exit. <b>Mutant</b> — the END
    /// through <c>CloseAsync</c> again — goes red here: the run's stale-close read refuses that close of 2 against A's
    /// book of 1, and A's END closes nothing.</para>
    /// </summary>
    [Fact]
    public async Task An_end_sells_only_its_own_runs_holding()
    {
        var (rig, a, b) = await TwoRunsAsync(
            Program("1", "", exit: "50", entry: "99"),
            Program("1", "", exit: "40", entry: "99"));
        await using var _ = rig;

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // both signal from a close of 100
        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // both fill at this open, 100
        Assert.Equal(2m, await Position(rig));

        await rig.Gw.EndPaperDeploymentAsync(a.Id, "test: the owner stopped run A");
        Show(rig, a, b);
        var flatten = Assert.Single(OpsOf(rig, a, DeploymentOpKind.Flatten));
        var close = await OrderOf(rig, flatten);
        log.WriteLine($"A's END sent: {close.Side} {close.Type} {close.Quantity} {close.State}");

        // THE END'S CLOSE IS A'S OWN BOOK, AND NOTHING OF B'S.
        Assert.Equal((OrderSide.Sell, OrderType.Market, 1m), (close.Side, close.Type, close.Quantity));

        await TickAsync(rig, 4, 100.2m, 100.4m, 100m, 100.3m);  // the minute already in progress
        await TickAsync(rig, 5, 100.2m, 100.4m, 100m, 100.3m);  // A's close fills at this open, 100.2
        Show(rig, a, b);

        Assert.Equal(ExecutionState.FILLED, (await OrderOf(rig, flatten)).State);
        Assert.Equal(0m, Holds(rig, a));
        Assert.Equal(1m, Holds(rig, b));
        Assert.Equal(1m, await Position(rig));
        Assert.True(rig.Gw.Deployments.ById(b.Id)!.IsActive);
        Assert.Contains("ENDED", LineOf(rig, a), StringComparison.Ordinal);
        Assert.Contains("Its own book: holds nothing, realised 0.2 after 0 in costs, over 2 fills.", LineOf(rig, a),
            StringComparison.Ordinal);
        Assert.Contains("Its own book: holds 1 at an average of 100", LineOf(rig, b), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- (c)

    /// <summary>
    /// (c) A RUN BESIDE A HOLDING IT DID NOT OPEN RESTS ITS PROTECTION, EXITS AND ENDS WITH ITS OWN.
    ///
    /// <para>One run, as shipped, and the owner's own long of 1 bought by hand beside it on the same account and symbol:
    /// the account holds 2 while the run holds 1. The run's stop rests at 1, its exit sells its 1 and leaves the owner's;
    /// it enters again, and its END sells its own 1 and leaves the owner's.</para>
    ///
    /// <para><b>RED on the base</b>: the run's stop is refused <c>POSITION_MOVED</c> (1 against the account's 2), its exit
    /// the same, and its END sells the owner's 1 with its own. <b>Mutants</b>: the run's branch reading the account
    /// refuses the stop; the END through <c>CloseAsync</c> sizes 2 and is refused against the run's book.</para>
    /// </summary>
    [Fact]
    public async Task A_run_beside_a_holding_it_did_not_open_exits_and_ends_with_its_own()
    {
        await using var rig = await ReadyAsync(Program("1", "stop percent 5\n", exit: "98", entry: "99"));
        var run = rig.Deployment;

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // the run signals from a close of 100

        // AND THE OWNER BUYS ONE OF THEIR OWN, BY HAND, IN PROCESS: a holding the run did not open.
        var owners = await rig.Gw.PlaceAsync(AgentContext.Operator, "owner-own-1", new PlaceIntent(
            "BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, "the owner's own"));
        log.WriteLine($"the owner's buy: {owners.State}");

        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // both fill at this open, 100: the account holds 2
        Show(rig, run);

        var stop = Assert.Single(OpsOf(rig, run, DeploymentOpKind.Stop));
        Assert.Equal(2m, await Position(rig));
        Assert.Equal(DeploymentOpState.Resolved, stop.State);
        var rested = await OrderOf(rig, stop);
        Assert.Equal((1m, ExecutionState.WORKING), (rested.Quantity, rested.State));

        await TickAsync(rig, 4, 100m, 100.2m, 96.5m, 97m);      // exit when close < 98: its stop off, its exit out
        await TickAsync(rig, 5, 97m, 97.5m, 96.5m, 97m);        // the minute already in progress
        await TickAsync(rig, 6, 97m, 97.5m, 96.5m, 97m);        // the exit fills at this open, 97
        Show(rig, run);

        var exit = Assert.Single(OpsOf(rig, run, DeploymentOpKind.Exit));
        var sold = await OrderOf(rig, exit);
        Assert.Equal((1m, ExecutionState.FILLED), (sold.Quantity, sold.State));
        Assert.Equal(0m, Holds(rig, run));
        Assert.Equal(1m, await Position(rig));                  // the owner's, untouched

        await TickAsync(rig, 7, 99m, 100m, 98.5m, 100m);        // a second entry, from a close of 100
        await TickAsync(rig, 8, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 9, 100m, 100.6m, 99.6m, 100.2m);   // it fills at this open: the account holds 2 again
        Assert.Equal(2m, await Position(rig));

        await rig.Gw.EndPaperDeploymentAsync(run.Id, "test: the owner stopped it");
        var flatten = Assert.Single(OpsOf(rig, run, DeploymentOpKind.Flatten));
        var close = await OrderOf(rig, flatten);
        Assert.Equal((OrderSide.Sell, 1m), (close.Side, close.Quantity));

        await TickAsync(rig, 10, 100.2m, 100.4m, 100m, 100.3m); // the minute already in progress
        await TickAsync(rig, 11, 100.2m, 100.4m, 100m, 100.3m); // the END's close fills at this open
        Show(rig, run);

        Assert.Equal(ExecutionState.FILLED, (await OrderOf(rig, flatten)).State);
        Assert.Equal(0m, Holds(rig, run));
        Assert.Equal(1m, await Position(rig));                  // the owner's, still untouched
    }

    // ---------------------------------------------------------------- (d)

    /// <summary>
    /// (d) A CLOSE OUTSIDE THE RUN RESOLVES ITS END, AND IS NEVER ITS FILL.
    ///
    /// <para>The run holds 1, and the owner presses Close all: the account is flat by a close the run did not send.
    /// The run's own book still holds its 1 — the press's fill is the press's, under the press's own request id — and
    /// its END finds the account flat under a complete book with no order of its own open: it sends nothing and is
    /// RESOLVED, "closed outside the run", in those words on the operation and on the run's line. Nothing is owed and
    /// nothing is attributed to the run that it did not do.</para>
    ///
    /// <para><b>RED on the base</b>: the END reads the account, finds it flat and says only "there was nothing to close",
    /// so the words this test pins are not there.</para>
    /// </summary>
    [Fact]
    public async Task A_close_outside_the_run_resolves_its_end_and_is_never_its_fill()
    {
        await using var rig = await ReadyAsync(Program("1", "", exit: "90", entry: "100"));
        var run = rig.Deployment;

        await TickAsync(rig, 1, 99m, 101m, 98m, 101m);          // the entry signals
        await TickAsync(rig, 2, 102m, 103m, 101m, 102m);        // the minute already in progress
        await TickAsync(rig, 3, 104m, 105m, 103m, 104m);        // it fills at this open, 104
        Assert.Equal(1m, await Position(rig));

        // THE OWNER PRESSES CLOSE ALL, ITS CLOSE FILLS AT THE NEXT OPEN, AND THE OWNER CONFIRMS THE PRESS.
        var press = await rig.Gw.OperatorCloseAllAsync();
        log.WriteLine($"the press: {press.Summary}");
        await TickAsync(rig, 4, 104m, 104.5m, 103.5m, 104m);    // the minute already in progress
        await TickAsync(rig, 5, 104m, 104.5m, 103.5m, 104m);    // the press's close fills at this open
        foreach (var row in rig.Gw.Unreconciled().Where(r => r.RequestId.StartsWith(TradingGateway.ClosePress, StringComparison.Ordinal)))
            rig.Gw.ForceResolve(row.RequestId, row.State, "the owner checked: it filled");
        await rig.Gw.RefreshHealthAsync();
        Assert.Equal(0m, await Position(rig));

        await rig.Gw.EndPaperDeploymentAsync(run.Id, "test: the owner stopped it");
        Show(rig, run);

        var flatten = Assert.Single(OpsOf(rig, run, DeploymentOpKind.Flatten));
        var reading = rig.Gw.DeploymentReadings().Single(d => d.Id == run.Id);

        // RESOLVED, IN WORDS, AND NOTHING SENT.
        Assert.Equal(DeploymentOpState.Resolved, flatten.State);
        Assert.StartsWith("closed outside the run", flatten.Answer, StringComparison.Ordinal);
        Assert.Null(rig.Gw.Requests.Get(flatten.RequestId));
        Assert.Null(reading.CloseOwed);
        Assert.Contains("closed outside the run", reading.Line, StringComparison.Ordinal);

        // AND THE PRESS'S FILL IS NOT THE RUN'S: its one fill is its entry.
        var own = Assert.Single(FillsOf(rig, run));
        Assert.Equal((nameof(OrderSide.Buy), 1m), (own.Side, own.Quantity));
        var pressFill = Assert.Single(rig.Gw.Fills.Since(null), f => f.Side == nameof(OrderSide.Sell));
        Assert.StartsWith(TradingGateway.ClosePress, pressFill.RequestId, StringComparison.Ordinal);
        Assert.Equal(0m, await Position(rig));
    }

    // ---------------------------------------------------------------- (e)

    /// <summary>
    /// (e) AN INCOMPLETE BOOK SIZES NOTHING, AND THE END STAYS OWED UNTIL IT IS WHOLE.
    ///
    /// <para>The run's entry fills at the venue and its order record says FILLED 1, but the fill never reaches the
    /// ledger — the platform's report of it is lost on the way, and the five-minute pull does not see it either. The
    /// book is then not the whole of it, and nothing is sized from it: the program's next entry, decided over a book
    /// that reads flat, is refused before the wire, and the END's close is refused before the wire and OWED, in words.
    /// Once the ledger has the fill, the next minute's owed close sells the run's 1.</para>
    ///
    /// <para><b>RED on the base</b>: the second entry goes out, charged the account's 1 and the order's 1 against a
    /// ceiling of 5, and the run holds 2 at the venue over a book that knows of none.</para>
    /// </summary>
    [Fact]
    public async Task An_incomplete_book_sizes_nothing_and_the_end_stays_owed()
    {
        LedgerBlind? blind = null;
        await using var rig = await ReadyAsync(Program("1", "", exit: "90", entry: "100"),
            through: paper => blind = new LedgerBlind(paper));
        var run = rig.Deployment;

        await TickAsync(rig, 1, 99m, 101m, 98m, 101m);          // the entry signals
        await TickAsync(rig, 2, 102m, 103m, 101m, 102m);        // the minute already in progress
        blind!.Hide();
        await TickAsync(rig, 3, 104m, 105m, 103m, 104m);        // it fills at this open — and the ledger is never told
        Show(rig, run);

        var entries = OpsOf(rig, run, DeploymentOpKind.Entry);
        foreach (var e in entries) log.WriteLine($"entry {e.RequestId} {e.State} — {e.Answer}");

        // THE PREMISE: the venue holds the entry's 1 and its order record says FILLED 1; the ledger has no fill of it.
        Assert.Equal(1m, await Position(rig));
        Assert.Equal((ExecutionState.FILLED, 1m),
            (rig.Gw.Requests.Get(entries[0].RequestId)!.State, rig.Gw.Requests.Get(entries[0].RequestId)!.FilledQuantity));
        Assert.Empty(FillsOf(rig, run));

        // AN OPENER OVER IT SIZES NOTHING: refused before the wire, and the wire holds the one entry.
        Assert.Equal(2, entries.Count);
        Assert.Equal(DeploymentOpState.Refused, entries[1].State);
        Assert.Contains(ErrorCode.RISK_CHECK_UNAVAILABLE.ToString(), entries[1].Answer, StringComparison.Ordinal);
        Assert.Single(await Wire(rig));

        // AND THE END'S CLOSE IS REFUSED BEFORE THE WIRE AND OWED, IN WORDS.
        await rig.Gw.EndPaperDeploymentAsync(run.Id, "test: the owner stopped it");
        var refused = Assert.Single(OpsOf(rig, run, DeploymentOpKind.Flatten));
        var reading = rig.Gw.DeploymentReadings().Single(d => d.Id == run.Id);
        log.WriteLine($"the END's close: {refused.State} — {refused.Answer}");
        log.WriteLine($"line: {reading.Line}");

        Assert.Equal(DeploymentOpState.Refused, refused.State);
        Assert.Null(rig.Gw.Requests.Get(refused.RequestId));
        Assert.Contains(ErrorCode.RISK_CHECK_UNAVAILABLE.ToString(), refused.Answer, StringComparison.Ordinal);
        Assert.NotNull(reading.CloseOwed);
        Assert.Contains("NOT closed", reading.Line, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", reading.Line, StringComparison.Ordinal);
        Assert.Single(await Wire(rig));
        Assert.Equal(1m, await Position(rig));

        // THE LEDGER CATCHES UP, AND THE NEXT MINUTE'S PASS SENDS THE OWED CLOSE, SIZED FROM THE BOOK NOW WHOLE.
        blind.Show();
        var pulled = await rig.Gw.PullFillsAsync();
        log.WriteLine($"the pull: {pulled.Seen} seen, {pulled.Added} added");
        Assert.Single(FillsOf(rig, run));

        rig.Clock.At = rig.Origin.AddMinutes(5).AddSeconds(5);
        await rig.Gw.ReconcilePaperDeploymentsAsync(rig.Clock.At);
        var flattens = OpsOf(rig, run, DeploymentOpKind.Flatten);
        Assert.Equal(2, flattens.Count);
        var owed = await OrderOf(rig, flattens[1]);
        Assert.Equal((OrderSide.Sell, 1m), (owed.Side, owed.Quantity));

        await TickAsync(rig, 5, 104m, 104.5m, 103.5m, 104m);
        await TickAsync(rig, 6, 104m, 104.5m, 103.5m, 104m);    // the owed close fills at this open
        Show(rig, run);

        Assert.Equal(ExecutionState.FILLED, (await OrderOf(rig, flattens[1])).State);
        Assert.Equal(0m, await Position(rig));
        Assert.Null(rig.Gw.DeploymentReadings().Single(d => d.Id == run.Id).CloseOwed);
    }

    // ---------------------------------------------------------------- (f)

    /// <summary>
    /// (f) THE ALLOCATIONS IN ONE ENVELOPE NEVER SUM PAST ITS CEILING.
    ///
    /// <para>The grant is the TOTAL the owner agreed to. With two runs at a time, the app's own policy writes each
    /// version the grant's ceilings divided by two, and the ledger refuses an allocation that would take the grant's
    /// standing paper allocations past either ceiling — here A again, at the whole grant, from a later instant, beside
    /// B's half.</para>
    ///
    /// <para><b>RED on the base</b>: both versions are written at the whole grant, 5 and 5,000,000 each — twice what
    /// the owner granted — and the ledger takes A at the whole grant again.</para>
    /// </summary>
    [Fact]
    public async Task Allocations_in_one_envelope_never_sum_past_its_ceiling()
    {
        var (rig, a, _) = await TwoRunsAsync(
            Program("1", "", exit: "50", entry: "99"),
            Program("1", "", exit: "40", entry: "99"));
        await using var _rig = rig;

        var envelope = rig.Gw.Envelopes.ById(a.EnvelopeId)!;
        var standing = rig.Gw.Allocations.InEnvelope(envelope.Id, rig.Origin);
        foreach (var x in standing)
            log.WriteLine($"allocation {StrategyDeploymentRow.Short(x.VersionId)}: {x.MaxQuantity} and {x.MaxNotional}");

        Assert.Equal(2, standing.Count);
        Assert.All(standing, x => Assert.Equal((2.5m, (decimal?)2_500_000m), (x.MaxQuantity, x.MaxNotional)));
        Assert.True(standing.Sum(x => x.MaxQuantity) <= envelope.MaxQuantity);
        Assert.True(standing.Sum(x => x.MaxNotional ?? 0m) <= envelope.MaxNotional);

        var later = rig.Origin.AddMinutes(1);
        var whole = rig.Gw.Allocations.RecordPaper(PaperAllocation(rig, a.VersionId, envelope,
            envelope.MaxQuantity, envelope.MaxNotional, later), later);
        log.WriteLine($"A again at the whole grant: {whole.Ok} — {whole.Why}");

        Assert.False(whole.Ok);
        Assert.Contains("in all", whole.Why, StringComparison.Ordinal);
        Assert.Equal(2, rig.Gw.Allocations.InEnvelope(envelope.Id, later).Count);
    }

    // ---------------------------------------------------------------- (g)

    /// <summary>
    /// (g) A RUN'S OPENER IS CHARGED ITS OWN HOLDING, NOT ANOTHER RUN'S.
    ///
    /// <para>A grant of 4, two runs at a time, and each version allocated 2 — written to the ledger. A buys 2 and holds
    /// them; B then buys 2. B is charged what B holds, nothing, plus its order, 2: inside its own 2. The account holds 4,
    /// which is the grant.</para>
    ///
    /// <para><b>RED on the base</b>: B is charged the ACCOUNT's 2 — A's — plus its order, 4 against its 2, and refused
    /// <c>ALLOCATION_EXCEEDED</c> for a position it does not hold.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_opener_is_charged_its_own_holding_not_another_runs()
    {
        var textA = Program("2", "", exit: "50", entry: "99");
        var textB = Program("2", "", exit: "50", entry: "102");
        await using var rig = await ReadyAsync(textA, seed: false);
        var envelope = GrantOfTwo(rig, quantity: 4m);

        var versionA = Judged(rig.Db, textA, rig.Origin);
        var versionB = Judged(rig.Db, textB, rig.Origin);
        foreach (var version in new[] { versionA, versionB })
        {
            var written = rig.Gw.Allocations.RecordPaper(
                PaperAllocation(rig, version, envelope, 2m, 2_000_000m, rig.Origin), rig.Origin);
            Assert.True(written.Ok, written.Why);
        }
        Assert.Equal(2, rig.Gw.StartPaperDeploymentsDue(rig.Origin));
        var open = rig.Gw.Deployments.Open();
        var a = Assert.Single(open, d => d.VersionId == versionA);
        var b = Assert.Single(open, d => d.VersionId == versionB);

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // A signals from a close of 100; B does not
        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // A's 2 fill at this open
        Assert.Equal(2m, await Position(rig));

        await TickAsync(rig, 4, 100.5m, 103.5m, 100.4m, 103m);  // B signals from a close of 103
        await TickAsync(rig, 5, 103m, 103.5m, 102.5m, 103m);    // the minute already in progress
        await TickAsync(rig, 6, 103m, 103.5m, 102.5m, 103m);    // B's 2 fill at this open
        Show(rig, a, b);

        var entries = OpsOf(rig, b, DeploymentOpKind.Entry);
        Assert.DoesNotContain(entries, e => e.State == DeploymentOpState.Refused);
        var entry = Assert.Single(entries);
        Assert.Equal(DeploymentOpState.Resolved, entry.State);
        var bought = await OrderOf(rig, entry);
        Assert.Equal((2m, ExecutionState.FILLED), (bought.Quantity, bought.State));
        Assert.Equal(2m, Holds(rig, a));
        Assert.Equal(2m, Holds(rig, b));
        Assert.Equal(4m, await Position(rig));
    }

    // ---------------------------------------------------------------- the line (item 1)

    /// <summary>
    /// A RUN'S LINE STATES ITS OWN BOOK — what it holds, at what average, and what it has realised after costs — on the
    /// Dashboard, in section 4 of the owner's report and in <c>deployment-list</c>, which all print the one line.
    ///
    /// <para>No fill yet; then the entry's 1 at 100; then flat after the exit at 97, 3 lost and no cost (the paper
    /// account's declared friction is none). The runner's own reading of the same bars says the same thing: the two
    /// walk one book.</para>
    ///
    /// <para><b>RED on the base</b>: the line said nothing of what the run holds.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_line_states_what_it_holds_its_average_and_its_realised_after_costs()
    {
        await using var rig = await ReadyAsync(Program("1", "", exit: "98", entry: "99"));
        var run = rig.Deployment;
        var before = LineOf(rig, run);

        await TickAsync(rig, 1, 99m, 100m, 98m, 100m);          // the entry signals from a close of 100
        await TickAsync(rig, 2, 100m, 100.5m, 99.5m, 100m);     // the minute already in progress
        await TickAsync(rig, 3, 100m, 100.6m, 99.6m, 100.2m);   // it fills at this open, 100
        var held = LineOf(rig, run);
        var reading = Assert.Single(await rig.Runner.AdvanceAsync()).Account!;

        await TickAsync(rig, 4, 100m, 100.2m, 96.5m, 97m);      // exit when close < 98
        await TickAsync(rig, 5, 97m, 97.5m, 96.5m, 97m);        // the minute already in progress
        await TickAsync(rig, 6, 97m, 97.5m, 96.5m, 97m);        // it fills at this open, 97
        var flat = LineOf(rig, run);

        log.WriteLine($"before : {before}");
        log.WriteLine($"held   : {held}");
        log.WriteLine($"runner : {reading}");
        log.WriteLine($"flat   : {flat}");

        Assert.Contains("Its own book: no fill yet.", before, StringComparison.Ordinal);
        Assert.Contains("Its own book: holds 1 at an average of 100, realised 0 after 0 in costs, over 1 fill.", held,
            StringComparison.Ordinal);
        Assert.Equal((PositionSide.Long, 1m, 100m), (reading.Position, reading.Quantity, reading.AverageFillPrice));
        Assert.Contains("Its own book: holds nothing, realised -3 after 0 in costs, over 2 fills.", flat,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A paper allocation of one version into one grant, at the ceilings given, written to the ledger.</summary>
    static AllocationRow PaperAllocation(Rig rig, string version, PaperEnvelopeRow envelope, decimal quantity,
        decimal? notional, DateTimeOffset at) =>
        new("", version, rig.Gw.Promotions.Standing(version).Promotion!.Id, AllocationPolicy.V1,
            quantity, notional, envelope.Currency, at, null, "test: written to the ledger", at)
        {
            Scope = AllocationScope.Paper,
            ConnectorId = rig.Gw.Connector.Id,
            Mode = TradingMode.PAPER.ToString(),
            AccountId = envelope.AccountId,
            EnvelopeId = envelope.Id
        };

    /// <summary>
    /// THE PAPER CONNECTOR WITH ITS EXECUTIONS WITHHELD FROM THE GATEWAY WHILE <see cref="Hide"/> STANDS — the event
    /// AND the read the fill pull makes — so an order's record can say it filled while the ledger holds no fill of it.
    /// <see cref="Show"/> serves them again. Everything else passes straight through; the rig disposes the paper
    /// connector itself.
    /// </summary>
    sealed class LedgerBlind : ITradingConnector, IConnectorStatusDetail
    {
        readonly PaperConnector _inner;
        readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        Action<ExecutionInfo>? _executions;
        volatile bool _hiding;

        public LedgerBlind(PaperConnector inner)
        {
            _inner = inner;
            inner.ExecutionReceived += x =>
            {
                if (_hiding)
                {
                    lock (_hidden) _hidden.Add(x.ExecutionId);
                    return;
                }
                _executions?.Invoke(x);
            };
        }

        internal void Hide() => _hiding = true;

        internal void Show()
        {
            _hiding = false;
            lock (_hidden) _hidden.Clear();
        }

        public string Id => _inner.Id;
        public string DisplayName => _inner.DisplayName;
        public ConnectorCapabilities Capabilities => _inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => _inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => _inner.EmergencyBudget;
        public string? StatusDetail => _inner.StatusDetail;

        public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => _inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => _inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => _inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string accountId, CancellationToken ct = default) => _inner.GetAccountAsync(accountId, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => _inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string symbol, CancellationToken ct = default) => _inner.GetQuoteAsync(symbol, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string accountId, CancellationToken ct = default) =>
            _inner.GetPositionsAsync(accountId, ct);
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string accountId, bool includeInactive, DateTimeOffset? since,
            CancellationToken ct = default) => _inner.GetOrdersAsync(accountId, includeInactive, since, ct);

        public async Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string accountId, DateTimeOffset? since,
            CancellationToken ct = default)
        {
            var all = await _inner.GetExecutionsAsync(accountId, since, ct);
            lock (_hidden) return [.. all.Where(x => !_hidden.Contains(x.ExecutionId))];
        }

        public Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default) => _inner.PlaceOrderAsync(cmd, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand cmd, CancellationToken ct = default) => _inner.ModifyOrderAsync(cmd, ct);
        public Task CancelOrderAsync(string connectorOrderId, CancellationToken ct = default) => _inner.CancelOrderAsync(connectorOrderId, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string accountId, CancellationToken ct = default) =>
            _inner.CancelAllOrdersAsync(accountId, ct);
        public Task<OrderInfo?> ClosePositionAsync(string accountId, string symbol, string clientOrderId,
            CancellationToken ct = default) => _inner.ClosePositionAsync(accountId, symbol, clientOrderId, ct);

        public event Action<HealthState>? ConnectionChanged { add => _inner.ConnectionChanged += value; remove => _inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => _inner.QuoteChanged += value; remove => _inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => _inner.OrderChanged += value; remove => _inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => _executions += value; remove => _executions -= value; }
        public event Action<PositionInfo>? PositionChanged { add => _inner.PositionChanged += value; remove => _inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => _inner.AccountChanged += value; remove => _inner.AccountChanged -= value; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
