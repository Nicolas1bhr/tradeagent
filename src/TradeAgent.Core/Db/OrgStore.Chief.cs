using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>The seat a chief is appointed on: the runtime and the model — the board act's <c>to</c>, or the owner's press.</summary>
public sealed record ChiefSeat(string Runtime, string Model);

/// <summary>
/// What a change of the chief wrote: its event's kind (<see cref="OrgEventKind.ChiefAppointed"/>, <see cref="OrgEventKind.ChiefReplaced"/>
/// or <see cref="OrgEventKind.ChiefVacated"/>), the chief now (null: vacant), the chief it ended (null: none sat), the mandates the
/// restructuring cancelled, and the event's id.
/// </summary>
public sealed record ChiefChanged(string Kind, string? Chief, string? Was, IReadOnlyList<string> Cancelled, long EventId);

/// <summary>
/// THE CHIEF — THE ROOT'S HEAD (<c>U-org-chief</c>; R24 §§ 4.1, 4.3, Q12; ORGANISATION §§ 2, 3; ORCHESTRATION §§ 2.1, 2.3, 2.9). One chart
/// writer, <see cref="SeatChief"/>, internal, called by the owner's in-process route and the board's confirmed act alone (a guard reads
/// their sources). A chief is a POSITION under the root, minted by the app and hired DORMANT: heading the root grants no board seat, no
/// activation, no envelope raise and no root tilt (ORG-001), and never order permission (ORDER-001, <see cref="CouncilRoles.MayPlaceOrders"/>).
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>The chief sitting now — the root's head — or null: the root is vacant.</summary>
    public string? Chief() => Units().FirstOrDefault(u => u.IsRoot)?.HeadPositionId;

    /// <summary>
    /// THE ONE CHART WRITER OF THE CHIEF (<c>U-org-chief</c> item 1). Inside ONE write: <paramref name="from"/> must be the chief sitting now
    /// (null: the root vacant), or the change is refused as moved; a sitting chief is ENDED through the restructuring's transaction
    /// (<see cref="Restructure"/>: every open mandate it holds or decides cancelled, the position fenced so its running turn ends, no past
    /// charge moved) and the root left vacant; a new chief (<paramref name="to"/>) is minted under the root, DORMANT, seated by
    /// <see cref="SetSeat"/>'s checks on <paramref name="to"/>'s runtime and model at <paramref name="allowance"/>, and made the root's head;
    /// and ONE <c>chief-appointed</c>, <c>chief-replaced</c> or <c>chief-vacated</c> event decided by <paramref name="decider"/> —
    /// <see cref="OrgDecider.Owner"/> or <see cref="OrgDecider.Board"/> — on <paramref name="basis"/> (<c>press</c>, <c>act</c>, <c>veto</c>)
    /// with <paramref name="actId"/> in its app-composed detail. Nothing activates here. Refused in words, nothing written: another decider;
    /// a moved chief; nothing to remove; a seat the checks refuse; a full chart.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    internal ChiefChanged SeatChief(string? from, ChiefSeat? to, string decider, string basis, string? actId, TurnAllowance? allowance,
        Func<string, bool>? knownRuntime, DateTimeOffset now) => db.Write(_ =>
    {
        if (decider is not (OrgDecider.Owner or OrgDecider.Board))
            throw Refused($"'{decider}' does not change the chief: the owner, in-process, or a two-seat act of the security board does.");
        if (basis is not ("press" or "act" or "veto")) throw Refused($"'{basis}' is no basis a change of the chief stands on.");
        if (actId is not null && !actId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
            throw Refused($"'{actId}' is no act id.");
        var root = Units().FirstOrDefault(u => u.IsRoot) ?? throw Damaged("it has no root");
        var sitting = root.HeadPositionId;
        if (!string.Equals(from, sitting, StringComparison.Ordinal))
            throw Refused($"{(sitting is null ? "no chief sits" : $"{sitting} is the chief now")}, not {from ?? "no chief"}: a change of the "
                          + "chief names the chief it changes, and this one has moved.");
        if (sitting is null && to is null) throw Refused("the organisation has no chief to remove.");
        if (to is not null)
        {
            var a = allowance ?? throw Refused("a chief is seated at an allowance, and none was given.");
            if (SeatRefusal(to.Runtime, to.Model, a.InputTokens, a.OutputTokens, knownRuntime ?? (_ => false)) is { } bad) throw Refused(bad);
            var open = Positions().Count(p => p.Status != OrgPositionStatus.Ended && p.Id != sitting);
            if (open >= OrgBounds.MaxPositions)
                throw Refused($"the organisation holds {open.ToString(CultureInfo.InvariantCulture)} positions that have not ended, the most it "
                              + $"carries ({OrgBounds.MaxPositions.ToString(CultureInfo.InvariantCulture)}), so no chief can be added.");
        }

        IReadOnlyList<string> cancelled = [];
        if (sitting is not null)
        {
            EndPositions([sitting], now);
            VacateHead(root.Id, sitting);
            var why = to is null ? "removed" : "replaced";
            cancelled = Restructure(new Restructuring($"the chief {sitting} was {why} by {decider}", decider, [], [sitting], [sitting]), now).Cancelled;
        }

        string? chief = null;
        if (to is not null)
        {
            chief = OrgIds.MintPosition();
            if (OrgIds.PositionIdRefusal(chief) is { } badId) throw Refused(badId);
            using (var c = db.Cmd("""
                INSERT INTO org_position(id, unit_id, home_dir, status, created_at, ended_at) VALUES($id, $unit, $home, $status, $at, NULL)
                """, ("$id", chief), ("$unit", root.Id), ("$home", CouncilRoles.HomeDir(chief)), ("$status", OrgPositionStatus.Dormant),
                ("$at", Sql.T(now))))
                c.ExecuteNonQuery();
            using (var h = db.Cmd("UPDATE org_unit SET head_position_id = $id WHERE id = $root AND head_position_id IS NULL",
                       ("$id", chief), ("$root", root.Id)))
                if (h.ExecuteNonQuery() != 1) throw Refused("the root's head was filled while this change was written.");
            WriteSeat(root.Id, chief, to.Runtime, to.Model, allowance!.InputTokens, allowance.OutputTokens, decider, now, recorded: false);
        }

        var kind = sitting is null ? OrgEventKind.ChiefAppointed : chief is null ? OrgEventKind.ChiefVacated : OrgEventKind.ChiefReplaced;
        var seat = to is null ? ""
            : $" runtime={to.Runtime} model={to.Model} allow_in={allowance!.InputTokens.ToString(CultureInfo.InvariantCulture)} "
              + $"allow_out={allowance.OutputTokens.ToString(CultureInfo.InvariantCulture)} status={OrgPositionStatus.Dormant}";
        var eventId = Event(now, root.Id, chief ?? sitting, kind, decider,
            $"chief={chief ?? "none"} was={sitting ?? "none"}{seat} basis={basis} act={actId ?? "none"} cancelled={Ids(cancelled)}");
        return new ChiefChanged(kind, chief, sitting, cancelled, eventId);
    });
}
