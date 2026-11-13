using System.Runtime.CompilerServices;
using TradeAgent.AgentRuntime;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// NO SEAT TEST READS THE REAL ACCOUNT'S PROFILE (<c>U-windows-cards</c>): on Windows every seat start looks for skills in the account's own
/// profile, and a box whose account keeps some would refuse each seat a test starts. This assembly's seats look in an empty folder of
/// its own instead; the tests of that rule set their own folder.
/// </summary>
static class AccountProfileTestHost
{
    [ModuleInitializer]
    internal static void Init()
    {
        var empty = Path.Combine(Path.GetTempPath(), "tradeagent-tests-account-profile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        SeatProfile.AccountProfileForEveryTest = empty;
        // AND NONE READS THE REAL MACHINE'S PROGRAM-DATA FOLDER (U-windows-cards-2): its codex system layer refuses every seat too.
        var machine = Path.Combine(Path.GetTempPath(), "tradeagent-tests-machine-folder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(machine);
        SeatProfile.MachineFolderForEveryTest = machine;
    }
}
