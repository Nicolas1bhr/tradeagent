using System.Runtime.CompilerServices;
using TradeAgent.Core.Db;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THIS ASSEMBLY IS A TEST HOST FOR THE ACTIVATION GATE (review of <c>U-thinking-seats-a</c>, fix pass 2, item 5): a register nothing
/// set leaves the legacy pair's unconfined launch as it was before the gate here, and nowhere else — in the product it refuses.
/// The flag is internal to <c>TradeAgent.Core</c> and visible to this assembly alone; nothing in the product sets it. A test that
/// is ABOUT the unset register asks the gate as a process that is not a test host, by argument, and never flips this flag.
/// </summary>
static class GateTestHost
{
    [ModuleInitializer]
    internal static void Init() => PositionActivation.TestHost = true;
}

/// <summary>
/// THIS ASSEMBLY'S BUILD, as a cell proof binds it (<c>U-cell-linux-c</c> fix pass 1, R3): a fixed version-and-hash, so the proofs its rows
/// write do not follow the rows that rewrite or delete <c>state/trade.sha256</c> mid-run. The flag is internal to <c>TradeAgent.Core</c>;
/// nothing in the product sets it. The rows ABOUT a missing hash ask <see cref="CellProofStore.BuildOf"/> and a store given no build.
/// </summary>
static class ProofTestHost
{
    [ModuleInitializer]
    internal static void Init() => CellProofStore.TestHostBuild = CellProofStore.BuildOf(new string('7', 64));
}
