using System.Runtime.CompilerServices;
using TradeAgent.AgentRuntime;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THIS ASSEMBLY IS A TEST HOST (<c>U-puppet-runtime</c>): its puppet seats may run here, and nowhere else. The flag is internal
/// to <c>TradeAgent.AgentRuntime</c> and visible to this assembly alone; nothing in the product sets it.
/// </summary>
static class PuppetTestHost
{
    [ModuleInitializer]
    internal static void Init() => PuppetSeats.TestHost = true;
}
