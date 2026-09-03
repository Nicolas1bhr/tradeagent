using System.IO;
using System.Linq;
using System.Reflection;
using ATAS.DataFeedsCore;
using TradeAgent.AtasBridge;
using TradeAgent.AtasGate;
using TradeAgent.Core;

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
        var security = new Security { Code = "ES", SecurityId = "ES" };
        var position = new Position { SecurityId = "ES", Volume = 2m };
        var trading = new StubTrading { SecurityValue = security, PositionValue = position };

        AtasStrategyAdapter NewAdapter()
        {
            var adapter = new AtasStrategyAdapter();
            adapter.DataProvider = new StubProvider(trading);
            return adapter;
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
        trading.ClosePositionCalls = 0;
        var allowed = NewAdapter();
        try { allowed.ClosePosition("", "ES", "TA-CLOSE-ALLOWED"); }
        catch (AtasRejectedException e)
        {
            Check("a writable witness does not produce a write-ahead refusal", false, e.Message);
        }
        catch (Exception) { /* "the resulting order could not be identified" — expected against a stub */ }

        Check("ITradingManager.ClosePosition WAS called once the witness could be written",
              trading.ClosePositionCalls == 1, $"calls = {trading.ClosePositionCalls}");

        var witness = new CoidWitness(Path.Combine(Paths.BridgeDir, CoidWitness.FileName));
        var ids = witness.All().Select(r => r.ClientOrderId).ToArray();
        Check("the refused close left no write-ahead record", !ids.Contains("TA-CLOSE-REFUSED"),
              $"records = [{string.Join(", ", ids)}]");
        Check("the permitted close left one", ids.Contains("TA-CLOSE-ALLOWED"),
              $"records = [{string.Join(", ", ids)}]");

        Console.WriteLine(failures == 0 ? "GATE PASSED" : $"GATE FAILED — {failures} check(s)");
        return failures == 0 ? 0 : 1;
    }
}
