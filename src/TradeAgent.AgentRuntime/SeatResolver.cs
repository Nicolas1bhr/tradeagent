using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE RULES ONE SEAT IS RESOLVED BY (<c>U-org-seats</c>), kept out of the composition root so they can be checked without starting
/// the app: which bound a runtime gives a role's turn, and what a position's own seat columns say.
/// </summary>
public static class SeatResolver
{
    /// <summary>
    /// <see cref="TurnBound.Enforced"/> only where the turn runs on the app's own harness — a runtime the harness serves for this role,
    /// whose every request this process composes and withholds once it could pass the allowance (<see cref="ApiConversation"/>).
    /// Every vendor program, and a role the harness does not serve (the chair), is <see cref="TurnBound.Reservation"/>. A launch with
    /// no role is the chair's.
    /// </summary>
    public static TurnBound BoundOf(string? role, string? runtime) =>
        ApiAgentRuntime.Serves(role ?? CouncilRoles.Operations) && RuntimeCatalog.IsHarness(runtime)
            ? TurnBound.Enforced
            : TurnBound.Reservation;

    /// <summary>A seat from its three parts, its bound decided by <see cref="BoundOf"/> — never by the caller.</summary>
    public static SeatSpec Of(string? role, string? runtime, string? model, TurnAllowance allowance) =>
        new(runtime, model, allowance, BoundOf(role, runtime));

    /// <summary>
    /// A POSITION'S OWN SEAT, or null where it has none and today's sources stand — no database, a launch with no role, a name the
    /// chart holds no position for, or a position whose four seat columns are all NULL. The legacy pair are seated on the Owner page
    /// (the orchestrator's Q5): a seat column set on either of them is damage no writer here makes, and is refused rather than obeyed or
    /// ignored. Throws <see cref="SeatRefusedException"/> for a seat that cannot be read; a chart that cannot be read throws as it is.
    /// </summary>
    public static SeatColumns? Seated(Database? db, string? role)
    {
        if (db is null || role is null) return null;
        if (new OrgStore(db).Position(role) is not { } row) return null;
        if (CouncilRoles.IsKnown(role))
            return row.SeatRuntime is null && row.SeatModel is null && row.SeatAllowIn is null && row.SeatAllowOut is null
                ? null
                : throw SeatColumns.Refused(role, SeatColumns.RuntimeColumn,
                    "is set, but the Operations and Research Directors are seated on the Owner page");
        return SeatColumns.Of(row, RuntimeCatalog.Knows);
    }
}
