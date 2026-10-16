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
