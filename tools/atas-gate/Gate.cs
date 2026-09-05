using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using TradeAgent.AtasBridge;
using TradeAgent.AtasGate;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
// ATAS.DataFeedsCore.TimeInForce and TradeAgent.ConnectorSdk.TimeInForce collide, the same clash the
// adapter aliases its way out of. This gate builds a TradeAgent command, so it names that one.
using Tif = TradeAgent.ConnectorSdk.TimeInForce;

namespace TradeAgent.AtasGate;

/// <summary>The gate's body, in its own method so the resolver below is installed before the JIT
/// has to find a single ATAS type.</summary>
public static class Gate
{
    public static int Run()
    {
        var home = Path.Combine(Path.GetTempPath(), "ta-atas-gate-" + Guid.NewGuid().ToString("n")[..8]);
        Environment.SetEnvironmentVariable("TRADEAGENT_HOME", home);
        Directory.CreateDirectory(Paths.BridgeDir);
        Console.WriteLine($"TRADEAGENT_HOME = {home}");
        Console.WriteLine($"bridge dir      = {Paths.BridgeDir}");

        var failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what} — {detail}");
            if (!ok) failures++;
        }

        // A chart with one instrument and one open position in it.
        const string Account = "SIM-1";
        var security = new Security { Code = "ES", SecurityId = "ES" };
        var portfolio = new Portfolio { AccountID = Account };
        var position = new Position { SecurityId = "ES", Volume = 2m, AccountID = Account };
        var trading = new StubTrading
        {
            SecurityValue = security, PositionValue = position, PortfolioValue = portfolio
        };

        // ONE ADAPTER FOR EVERY CHECK PAST THE FIRST, AND THE WITNESS LEASE IS WHY.
        //
        // CoidWitness takes its lock at the first write and HOLDS it for the life of the writer
        // (that is the "one owner per witness" rule, and it is deliberate). Paths.BridgeDir is
        // resolved once per process, so a second adapter in this process asks for the same file and
        // is refused "another writer owns this witness" — which is check 1's condition, arriving in
        // every check after it and passing several of them for the wrong reason. Section 1 gets its
        // own instance because it must be refused; everything below shares this one.
        var provider = new StubProvider(trading);
        AtasStrategyAdapter NewAdapter()
        {
            var made = new AtasStrategyAdapter { DataProvider = provider };
            return made;
        }

        // ---------------------------------------------------------------- 1. witness unavailable
        //
        // Somebody else owns the witness, which is the state a second bridge — or any process holding that
        // file — produces. Submitting cannot take the lease, so it returns false.
        var lockPath = Path.Combine(Paths.BridgeDir, CoidWitness.FileName + ".lock");
        using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            trading.ClosePositionCalls = 0;
            var refused = NewAdapter();
            string? refusal = null;
            try
            {
                refused.ClosePosition("", "ES", "TA-CLOSE-REFUSED");
                Check("close-all is refused when the witness cannot be written", false, "it returned instead of throwing");
            }
            catch (AtasRejectedException e) { refusal = e.Message; }
            catch (Exception e)
            {
                Check("the refusal is definite (AtasRejectedException)", false, $"{e.GetType().Name}: {e.Message}");
            }

            Check("ITradingManager.ClosePosition was never called", trading.ClosePositionCalls == 0,
                  $"calls = {trading.ClosePositionCalls}");
            Check("the refusal says nothing was submitted", refusal?.Contains("nothing was submitted") == true,
                  refusal ?? "<no AtasRejectedException>");
            Check("the refusal names the witness file", refusal?.Contains(CoidWitness.FileName) == true,
                  refusal ?? "<none>");
        }

        // ---------------------------------------------------------------- 2. witness writable
        //
        // The other direction: with the witness available the close IS put to ATAS. The adapter then throws
        // because this stub creates no order to identify — which is its own rule 3 behaviour and not what is
        // under test here; what is under test is that the call happened.
        //
        // AND HOW LONG IT TOOK, which is finding 11's other half. `close` is an emergency: the whole
        // caller budget is AtasConnector.EmergencyDeadline (docs/CONTRACTS.md, "Bridge deadlines"),
        // covering the send gate, the write, this handler and the reply. A handler that outlasts it
        // settles the operator's Close All as UNKNOWN however fast the close actually filled —
        // measured on the box on 2026-09-05: a close that FILLED in 341 ms was recorded
        // "'close' is NOT confirmed … The bridge is busy" because the caller gave up at 2.0 s.
        trading.ClosePositionCalls = 0;
        trading.Book.Clear();
        trading.OnClose = null;
        var allowed = NewAdapter();
        var adapter = allowed;   // the lease holder from here on
        var unidentified = Stopwatch.StartNew();
        try { allowed.ClosePosition("", "ES", "TA-CLOSE-ALLOWED"); }
        catch (AtasRejectedException e)
        {
            Check("a writable witness does not produce a write-ahead refusal", false, e.Message);
        }
        catch (Exception) { /* "the resulting order could not be identified" — expected against a stub */ }
        unidentified.Stop();

        Check("ITradingManager.ClosePosition WAS called once the witness could be written",
              trading.ClosePositionCalls == 1, $"calls = {trading.ClosePositionCalls}");
        Check("an unidentifiable close answers inside the emergency budget",
              unidentified.Elapsed < BridgeBudgets.Emergency,
              $"{unidentified.ElapsedMilliseconds} ms against a {BridgeBudgets.Emergency.TotalMilliseconds:0} ms budget");

        var witness = new CoidWitness(Path.Combine(Paths.BridgeDir, CoidWitness.FileName));
        var ids = witness.All().Select(r => r.ClientOrderId).ToArray();
        Check("the refused close left no write-ahead record", !ids.Contains("TA-CLOSE-REFUSED"),
              $"records = [{string.Join(", ", ids)}]");
        Check("the permitted close left one", ids.Contains("TA-CLOSE-ALLOWED"),
              $"records = [{string.Join(", ", ids)}]");

        // ---------------------------------------------------------------- 3. WHICH order the close was
        //
        // Finding 11. ClosePosition cannot hand ATAS our identifier — ATAS builds the closing order
        // itself and writes its own "Close position" into the comment (measured on the box,
        // 2026-09-05) — so the order it caused is found afterwards by diffing ATAS's collection. A
        // BARE same-symbol match over that diff labels whatever arrived in the window, which on a
        // busy account is somebody else's order being reported to the gateway as the close.
        Order Unrelated() => new()
        {
            AccountID = "SOMEBODY-ELSE", SecurityId = "ES", Security = security,
            Direction = OrderDirections.Buy, QuantityToFill = 7m, Time = DateTime.Now
        };
        Order TheClose() => new()
        {
            AccountID = Account, SecurityId = "ES", Security = security,
            Direction = OrderDirections.Sell, QuantityToFill = 2m, Time = DateTime.Now
        };

        // 3a — ONLY an unrelated order arrives. There is no close to find, so the honest answer is
        // the reconcile exception, and the unrelated order must be left alone.
        trading.Book.Clear();
        var stranger = Unrelated();
        trading.OnClose = () => trading.Book.Add(stranger);
        var mislabel = adapter;
        object? answered = null;
        string? complaint = null;
        try { answered = mislabel.ClosePosition(Account, "ES", "TA-CLOSE-STRANGER"); }
        catch (Exception e) { complaint = $"{e.GetType().Name}: {e.Message}"; }

        Check("an unrelated same-symbol order is NOT labelled with the close's id",
              string.IsNullOrEmpty(stranger.Comment),
              $"its comment is \"{stranger.Comment}\"");
        Check("an unrelated same-symbol order is NOT returned as the close",
              answered is null, complaint ?? $"it returned {answered}");

        // 3b — the real close arrives, alone. It must be found and labelled, or this fix has broken
        // the operator's Close All rather than corrected it.
        trading.Book.Clear();
        var real = TheClose();
        trading.OnClose = () => trading.Book.Add(real);
        var identified = adapter;
        OrderInfo? found = null;
        string? failed = null;
        var clock = Stopwatch.StartNew();
        try { found = identified.ClosePosition(Account, "ES", "TA-CLOSE-REAL"); }
        catch (Exception e) { failed = $"{e.GetType().Name}: {e.Message}"; }
        clock.Stop();

        Check("the order ATAS built for the close IS identified", found is not null, failed ?? "ok");
        Check("and it carries the close's client order id", real.Comment == "TA-CLOSE-REAL",
              $"comment = \"{real.Comment}\"");
        Check("an identified close answers inside the emergency budget",
              clock.Elapsed < BridgeBudgets.Emergency,
              $"{clock.ElapsedMilliseconds} ms against a {BridgeBudgets.Emergency.TotalMilliseconds:0} ms budget");

        // 3c — both arrive in the same window. The close is ours; the stranger is not.
        trading.Book.Clear();
        var both = TheClose();
        var alsoStranger = Unrelated();
        trading.OnClose = () => { trading.Book.Add(alsoStranger); trading.Book.Add(both); };
        var crowded = adapter;
        OrderInfo? picked = null;
        try { picked = crowded.ClosePosition(Account, "ES", "TA-CLOSE-CROWD"); }
        catch (Exception) { /* reported by the checks below */ }

        Check("with a stranger in the window the CLOSE is the one picked",
              picked is not null && both.Comment == "TA-CLOSE-CROWD",
              $"close comment = \"{both.Comment}\", returned = {(picked is null ? "<nothing>" : "an order")}");
        Check("with a stranger in the window the STRANGER is left alone",
              string.IsNullOrEmpty(alsoStranger.Comment), $"its comment is \"{alsoStranger.Comment}\"");

        // 3d — THE CLOSE ARRIVES ONLY AS A FILL, which is what the real box does. Measured
        // 2026-09-06 on ATAS 8.0.14.397 (simulated CRYPTO5EB41): the operator's Close All filled at
        // 79720.2 and the diff over the three order collections saw ZERO new orders — a market close
        // is Done before this method looks, and a Done order is in none of them. The fill is in
        // MyTrades inside the window and carries the order object itself.
        trading.Book.Clear();
        trading.Fills.Clear();
        var onlyAFill = TheClose();
        trading.OnClose = () => trading.Fills.Add(new MyTrade { Order = onlyAFill });
        OrderInfo? viaFill = null;
        string? fillFailed = null;
        var fillClock = Stopwatch.StartNew();
        try { viaFill = adapter.ClosePosition(Account, "ES", "TA-CLOSE-VIA-FILL"); }
        catch (Exception e) { fillFailed = $"{e.GetType().Name}: {e.Message}"; }
        fillClock.Stop();

        Check("a close that reaches ATAS's collections ONLY as a fill is still identified",
              viaFill is not null && onlyAFill.Comment == "TA-CLOSE-VIA-FILL",
              fillFailed ?? $"comment = \"{onlyAFill.Comment}\", {fillClock.ElapsedMilliseconds} ms");

        // 3e — and the terms still decide. An unrelated fill in the same window is not the close.
        trading.Book.Clear();
        trading.Fills.Clear();
        var strangerFill = Unrelated();
        trading.OnClose = () => trading.Fills.Add(new MyTrade { Order = strangerFill });
        OrderInfo? viaStrangerFill = null;
        string? fillRefusal = null;
        try { viaStrangerFill = adapter.ClosePosition(Account, "ES", "TA-CLOSE-STRANGER-FILL"); }
        catch (Exception e) { fillRefusal = $"{e.GetType().Name}: {e.Message}"; }

        Check("an unrelated fill in the window is NOT returned as the close",
              viaStrangerFill is null && string.IsNullOrEmpty(strangerFill.Comment),
              fillRefusal ?? $"it returned an order; stranger comment = \"{strangerFill.Comment}\"");

        trading.OnClose = null;
        trading.Book.Clear();
        trading.Fills.Clear();

        // ---------------------------------------------------------------- 4. order history coverage
        //
        // Rule 2, and finding 8 / UNVERIFIED 1. SupportsOrderHistory is answered from cache PRESENCE,
        // and the coverage watermark in GetOrders is skipped entirely when the platform states no
        // retention period — so a cache that knows nothing about the window being asked for answers
        // every `since` as covered. A partial history makes "this order does not exist" look provable.
        var cold = adapter;
        provider.Services[typeof(ATAS.DataFeedsCore.Database.ICache)] =
            CacheProxy.New(TimeSpan.Zero, portfolio);
        var coldHello = cold.Describe();
        string? coldRefusal = null;
        int coldCount = -1;
        try { coldCount = cold.GetOrders(Account, includeInactive: true, DateTimeOffset.UtcNow.AddDays(-7)).Count; }
        catch (Exception e) { coldRefusal = $"{e.GetType().Name}: {e.Message}"; }

        Check("a cache that states no retention period does not claim order history",
              !coldHello.SupportsOrderHistory || coldRefusal is not null,
              $"SupportsOrderHistory={coldHello.SupportsOrderHistory}, GetOrders → " +
              (coldRefusal ?? $"{coldCount} order(s)") + $"; surface: {coldHello.TradingSurface}");

        var warm = adapter;
        provider.Services[typeof(ATAS.DataFeedsCore.Database.ICache)] =
            CacheProxy.New(TimeSpan.FromDays(30), portfolio);
        var warmHello = warm.Describe();
        string? warmRefusal = null;
        try { warm.GetOrders(Account, includeInactive: true, DateTimeOffset.UtcNow.AddDays(-7)); }
        catch (Exception e) { warmRefusal = $"{e.GetType().Name}: {e.Message}"; }

        Check("a cache that states a retention period DOES claim order history",
              warmHello.SupportsOrderHistory, $"SupportsOrderHistory={warmHello.SupportsOrderHistory}; " +
              $"surface: {warmHello.TradingSurface}");
        Check("and answers a window inside it", warmRefusal is null, warmRefusal ?? "no refusal");

        string? outside = null;
        try { warm.GetOrders(Account, includeInactive: true, DateTimeOffset.UtcNow.AddDays(-60)); }
        catch (Exception e) { outside = e.Message; }
        Check("and refuses a window older than it keeps", outside is not null,
              outside ?? "it answered a 60-day window against a 30-day cache");

        // ---------------------------------------------------------------- 5. a stalled money call
        //
        // Finding 10 / UNVERIFIED 3. The four obsolete synchronous ATAS calls have no deadline and
        // run on BridgeServer's serial frame loop, so one that never returns takes the operator's
        // cancel-all and close-all with it while the heartbeat goes on saying READY.
        using var stall = new ManualResetEventSlim(false);
        trading.OpenOrderBlock = stall;
        trading.OpenOrderCalls = 0;
        var wedged = adapter;
        var order = new PlaceOrderCommand("TA-STALL-1", Account, "ES", OrderSide.Buy, OrderType.Market,
                                          1m, null, null, Tif.Day, null);
        var placeClock = Stopwatch.StartNew();
        string? placeOutcome = null;
        var place = Task.Run(() =>
        {
            try { wedged.Place(order); placeOutcome = "it RETURNED from a call that never came back"; }
            catch (Exception e) { placeOutcome = $"{e.GetType().Name}: {e.Message}"; }
        });
        // Generous: the gate must never be the thing that hangs. Twelve seconds is more than twice
        // the adapter's own CallTimeout and a third of the stub's 60 s ceiling.
        var bounded = place.Wait(TimeSpan.FromSeconds(12));
        placeClock.Stop();

        Check("a stalled synchronous ATAS call is bounded, not waited on forever",
              bounded && placeOutcome?.Contains(nameof(AtasCallTimeoutException)) == true,
              bounded ? $"{placeClock.ElapsedMilliseconds} ms → {placeOutcome}"
                      : $"still running after {placeClock.ElapsedMilliseconds} ms — OpenOrder is still inside ATAS");

        // Degraded health: a bridge that has abandoned a money call must stop reporting a clean
        // surface, because the heartbeat's READY is otherwise the only thing anybody sees.
        var stalledSurface = "";
        try { stalledSurface = wedged.Describe().TradingSurface ?? ""; } catch (Exception e) { stalledSurface = e.Message; }
        Check("and the bridge says so in its own surface report",
              stalledSurface.Contains("stalled"), stalledSurface);

        // Emergency progress: the close-all behind the stalled placement still gets through, inside
        // its budget, while OpenOrder is STILL sitting in ATAS.
        trading.ClosePositionCalls = 0;
        var emergencyClock = Stopwatch.StartNew();
        try { wedged.ClosePosition(Account, "ES", "TA-CLOSE-BEHIND-STALL"); }
        catch (Exception) { /* no order is created, so it reports it cannot identify one */ }
        emergencyClock.Stop();
        Check("an emergency behind the stalled call still reaches ATAS, inside its budget",
              trading.ClosePositionCalls == 1 && emergencyClock.Elapsed < BridgeBudgets.Emergency,
              $"ClosePosition calls = {trading.ClosePositionCalls} after {emergencyClock.ElapsedMilliseconds} ms " +
              $"(budget {BridgeBudgets.Emergency.TotalMilliseconds:0} ms); OpenOrder calls = {trading.OpenOrderCalls}");

        stall.Set();
        place.Wait(TimeSpan.FromSeconds(5));
        trading.OpenOrderBlock = null;

        Console.WriteLine(failures == 0 ? "GATE PASSED" : $"GATE FAILED — {failures} check(s)");
        return failures == 0 ? 0 : 1;
    }
}
