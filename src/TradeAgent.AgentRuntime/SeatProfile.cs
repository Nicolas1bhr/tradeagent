using System.Diagnostics;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE SEAT HOME, MADE BY THE APP AND VERIFIED BY IT (<c>U-seat-home</c>; AUTH-001, ORDER-005, CRED-001).
///
/// <para>Every codex process TradeAgent starts runs with <c>CODEX_HOME</c> and <c>HOME</c> inside <see cref="SeatPlan.Root"/>,
/// never the owner's own: his <c>~/.codex</c> carries his plugins, MCP servers, notify hook, skills and sign-in, and none of them
/// is something a seat was configured to have. The app creates the folders; nothing is copied from his.</para>
/// </summary>
public static class SeatProfile
{
    /// <summary>
    /// MAKES THE SEAT HOME READY FOR A START: its folders exist. Called by <see cref="AgentEnvironment.Apply(ProcessStartInfo,
    /// IReadOnlyDictionary{string, string}, RuntimeManifest)"/>, the one seam every process of a runtime passes.
    /// </summary>
    public static void Prepare(SeatPlan seat)
    {
        Directory.CreateDirectory(seat.Root);
        foreach (var (name, value) in seat.ResolvedEnvironment())
            if (seat.Environment[name].Contains("{seatHome}", StringComparison.Ordinal)) Directory.CreateDirectory(value);
    }
}
