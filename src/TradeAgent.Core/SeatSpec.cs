using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// WHETHER A SEAT'S TURN IS HELD TO ITS ALLOWANCE (<c>U-org-seats</c>; ORCHESTRATION § 2.4): "incurred cost is bounded only where a
/// total-turn bound … is demonstrated for that runtime; elsewhere the reservation is the bound, and an overrun is recorded and charged,
/// never hidden".
/// </summary>
public enum TurnBound
{
    /// <summary>The app's own harness composes every request and sends none that could pass the allowance: the turn is held to it.</summary>
    Enforced,

    /// <summary>A vendor's program runs the turn and nothing here can stop it mid-turn: the reservation is the only bound.</summary>
    Reservation
}

/// <summary>
/// ONE SEAT, RESOLVED ONCE FOR A LAUNCH (<c>U-org-seats</c>; ORCHESTRATION § 2.1; ORGANISATION § 10 "a runtime, a model class, … a
/// per-turn allowance"): the runtime the launch runs on, the model it names, the allowance it is reserved at, and whether that allowance
/// is enforced. The meter's reservation, the ledger row, the conversation's model and the harness's bound all read THIS — never a second
/// resolution of the same question, which is how a turn came to be priced at one model and launched on another.
/// </summary>
public sealed record SeatSpec(string? Runtime, string? Model, TurnAllowance Allowance, TurnBound Bound)
{
    /// <summary>The ledger's spelling of <see cref="TurnBound.Enforced"/> (<c>ai_attempt.turn_bound</c>).</summary>
    public const string EnforcedName = "enforced";

    /// <summary>The ledger's spelling of <see cref="TurnBound.Reservation"/>.</summary>
    public const string ReservationName = "reservation";

    /// <summary>A bound's spelling on the ledger row.</summary>
    public static string Name(TurnBound bound) => bound == TurnBound.Enforced ? EnforcedName : ReservationName;

    /// <summary>
    /// WHAT THE RESERVATION IS PRICED AT (R15 § 3c: "a smaller allowance is only honest where it is enforced"): the seat's own allowance
    /// where its turn is held to it; elsewhere never below the shipped bound, component by component — a CLI seat set to 300 k would
    /// otherwise reserve 300 k for a turn that can read millions (M0 attempt 3: about 6.2 M a turn). A larger allowance raises it.
    /// </summary>
    public TurnAllowance Reserved => Bound == TurnBound.Enforced
        ? Allowance
        : new TurnAllowance(Math.Max(Allowance.InputTokens, TurnAllowance.Default.InputTokens),
            Math.Max(Allowance.OutputTokens, TurnAllowance.Default.OutputTokens));
}

/// <summary>
/// A SEAT THAT CANNOT BE READ, OR CANNOT BE SERVED AS WRITTEN — refused in words naming the position and the column, never half-read
/// (refuse, never guess): the launch writes no row, reserves nothing and keeps its wakes.
/// </summary>
public sealed class SeatRefusedException(string message) : Exception(message);

/// <summary>
/// A POSITION'S OWN SEAT, as its <c>org_position</c> columns hold it (rung 28): all four set, or none. NULL in all four is "today's
/// sources" — the Safety page's runtime and model and the app-wide allowance (<see cref="Of"/> answers null).
/// </summary>
public sealed record SeatColumns(string Runtime, string Model, TurnAllowance Allowance)
{
    /// <summary>The column names, spelled once for the words that name them.</summary>
    public const string RuntimeColumn = "seat_runtime", ModelColumn = "seat_model", InColumn = "seat_allow_in", OutColumn = "seat_allow_out";

    /// <summary>
    /// THE POSITION'S SEAT, or null where none of its four columns is set. Anything else — a column set without the others, a runtime
    /// <paramref name="knownRuntime"/> does not know, an empty model, a token figure that is not a positive whole number — throws
    /// <see cref="SeatRefusedException"/> naming the position and the column. Never a part of a seat filled from elsewhere.
    /// </summary>
    public static SeatColumns? Of(OrgPositionRow row, Func<string, bool> knownRuntime)
    {
        if (row.SeatRuntime is null && row.SeatModel is null && row.SeatAllowIn is null && row.SeatAllowOut is null) return null;

        if (row.SeatRuntime is not { Length: > 0 } runtime) throw Refused(row.Id, RuntimeColumn, "is empty while the rest of the seat is set");
        if (!knownRuntime(runtime)) throw Refused(row.Id, RuntimeColumn, $"names “{runtime}”, which is not a runtime this build knows");
        if (row.SeatModel is not { Length: > 0 } model) throw Refused(row.Id, ModelColumn, "is empty while the rest of the seat is set");
        var input = Tokens(row.Id, InColumn, row.SeatAllowIn);
        var output = Tokens(row.Id, OutColumn, row.SeatAllowOut);
        return new SeatColumns(runtime, model, new TurnAllowance(input, output));
    }

    static long Tokens(string position, string column, string? text) =>
        text is { Length: > 0 }
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw Refused(position, column, text is null ? "is empty while the rest of the seat is set"
                : $"reads “{text}”, which is not a positive whole number of tokens");

    /// <summary>The refusal, in the owner's words: which position, which column, what is wrong with it.</summary>
    public static SeatRefusedException Refused(string position, string column, string why) =>
        new(Labels.SeatUnreadable(position, column, why));
}
