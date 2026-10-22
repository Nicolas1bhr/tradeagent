using System.Globalization;

namespace TradeAgent.Core.Db;

public sealed partial class OrgStore
{
    /// <summary>
    /// SEATS A POSITION (<c>U-org-seats</c>; ORCHESTRATION § 2.1 "a seat moves only by an app rule or the owner"): its runtime, model and
    /// per-turn allowance, all four columns in one write with one <c>org_event</c> <see cref="OrgEventKind.Seated"/> whose decider is
    /// <see cref="OrgDecider.Owner"/> and whose detail the app composes from the validated values.
    ///
    /// <para><b>In-process only (AUTH-001).</b> No pipe op, no door verb and no <c>trade</c> verb reaches it: an agent that wants a
    /// stronger or a cheaper seat has nowhere to ask. Its control on the Org page arrives with O7 (<c>U-org-surface</c>); until then
    /// every position without a seat runs on today's sources.</para>
    ///
    /// <para><b>Validated, each refused by name, nothing written:</b> a position the chart holds and has not ended; never the legacy pair,
    /// whose seat is the Safety page (the orchestrator's Q5); a runtime <paramref name="knownRuntime"/> knows (the catalogue, which this
    /// assembly cannot see); a model named; positive token figures. A smaller allowance than the shipped bound lowers what is set aside
    /// only on the app's harness, which holds the turn to it — on a vendor's program the reservation never drops below the shipped bound
    /// (<see cref="SeatSpec.Reserved"/>), so seating a position raises no ceiling and removes no permission.</para>
    /// </summary>
    public void SetSeat(string position, string runtime, string model, long inputTokens, long outputTokens,
        Func<string, bool> knownRuntime, DateTimeOffset at) => db.Write(_ =>
    {
        if (Position(position) is not { } row) throw Refused($"the chart holds no position '{position}' to seat.");
        if (row.EndedAt is not null || row.Status == OrgPositionStatus.Ended) throw Refused($"position '{position}' has ended; an ended position is not seated.");
        if (CouncilRoles.IsKnown(position))
            throw Refused($"'{position}' is seated on the Safety page, where its runtime and model are chosen; its seat columns stay empty.");
        if (runtime is not { Length: > 0 } || !knownRuntime(runtime)) throw Refused($"'{runtime}' is not a runtime this build knows.");
        if (model is not { Length: > 0 and <= 128 } || !model.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/'))
            throw Refused("a seat names its model in at most 128 letters, digits and . - _ : / — nothing else.");
        if (inputTokens <= 0 || outputTokens <= 0) throw Refused("a seat's allowance is a positive number of input and of output tokens.");

        var input = inputTokens.ToString(CultureInfo.InvariantCulture);
        var output = outputTokens.ToString(CultureInfo.InvariantCulture);
        using (var c = db.Cmd("""
            UPDATE org_position SET seat_runtime=$rt, seat_model=$model, seat_allow_in=$in, seat_allow_out=$out WHERE id=$id
            """, ("$rt", runtime), ("$model", model), ("$in", input), ("$out", output), ("$id", position)))
            c.ExecuteNonQuery();
        using (var e = db.Cmd("""
            INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES($at, $unit, $id, $kind, $decider, $detail)
            """, ("$at", Sql.T(at)), ("$unit", row.UnitId), ("$id", position), ("$kind", OrgEventKind.Seated),
            ("$decider", OrgDecider.Owner), ("$detail", $"position={position} runtime={runtime} model={model} allow_in={input} allow_out={output}")))
            e.ExecuteNonQuery();
        return 0;
    });
}
