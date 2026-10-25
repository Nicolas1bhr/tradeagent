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
    /// THE CHIEF A BOARD ACT SEATED (<c>U-org-chief</c> item 4): the position its <c>chief-appointed</c> or <c>chief-replaced</c> event names,
    /// read from the app-composed detail the chart's writer wrote, or null — the act seated no one. The owner's veto reads it.
    /// </summary>
    internal string? ChiefSeatedBy(string actId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT detail FROM org_event WHERE kind IN ($a, $r) AND decider = $board ORDER BY id",
            ("$a", OrgEventKind.ChiefAppointed), ("$r", OrgEventKind.ChiefReplaced), ("$board", OrgDecider.Board));
        using var r = c.ExecuteReader();
        while (r.Read())
            if (DetailValue(r.GetString(0), "act") == actId) return DetailValue(r.GetString(0), "chief");
        return null;
    });

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

    // ---- the owner's route, in-process (item 2; AUTH-001) ------------------------------------------------------------------------------

    /// <summary>
    /// THE OWNER APPOINTS A CHIEF OVER A VACANT ROOT (<c>U-org-chief</c> item 2; the orchestrator's Q1) — IN-PROCESS ONLY: no pipe op, door
    /// verb, <c>trade</c> verb or board act reaches it (AUTH-001; a guard reads their sources), and only while no board seat is ACTIVE (read
    /// through <see cref="Board.BoardReader.AnyActive"/>, the board's one reader): "yours until the security board is active". The chief is
    /// hired DORMANT on <paramref name="seat"/> at the policy's turn allowance; activating it is a press of its own through
    /// <see cref="Activate"/>'s gate. Refused in words, nothing written: a board seat active; a chief already sitting; the policy unreadable;
    /// a seat the checks refuse.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ChiefChanged AppointChief(ChiefSeat seat, Func<string, bool> knownRuntime, DateTimeOffset now) => db.Write(_ =>
    {
        OwnersWhileNoBoard("appointing a chief");
        if (Chief() is { } sitting)
            throw Refused($"{sitting} is the chief: a chief is appointed over a vacant root only — 'Replace the chief' replaces one.");
        return SeatChief(null, seat, OrgDecider.Owner, "press", null, PolicyAllowance(), knownRuntime, now);
    });

    /// <summary>
    /// THE OWNER REPLACES THE SITTING CHIEF (<c>U-org-chief</c> item 2) — in-process only, and only while no board seat is ACTIVE: the sitting
    /// chief ended through the restructuring's transaction and a new one hired DORMANT, in one write (<see cref="SeatChief"/>).
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ChiefChanged ReplaceChief(ChiefSeat seat, Func<string, bool> knownRuntime, DateTimeOffset now) => db.Write(_ =>
    {
        OwnersWhileNoBoard("replacing the chief");
        var sitting = Chief() ?? throw Refused("the organisation has no chief to replace: 'Appoint a chief' appoints one.");
        return SeatChief(sitting, seat, OrgDecider.Owner, "press", null, PolicyAllowance(), knownRuntime, now);
    });

    /// <summary>
    /// THE OWNER REMOVES THE CHIEF, AT ANY TIME (<c>U-org-chief</c> item 2; R24 Q12: "his narrowing veto removes a chief") — in-process only,
    /// whether or not a board seat is active: the sitting chief ended through the restructuring's transaction, the root left VACANT, and NO
    /// predecessor restored — a removal only takes away. Refused only when no chief sits.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ChiefChanged VacateChief(DateTimeOffset now) => db.Write(_ =>
        SeatChief(Chief(), null, OrgDecider.Owner, "press", null, null, null, now));

    /// <summary>The owner's appointment and replacement are his only while no board seat is active; after that, a two-seat board act.</summary>
    void OwnersWhileNoBoard(string doing)
    {
        if (new Board.BoardReader(db).AnyActive)
            throw Refused($"{doing} is the security board's now that a board seat is active (R24 § 4.1): a two-seat act — one active seat "
                          + "proposes it, another confirms it — under your narrowing veto. You can still remove the chief.");
    }

    /// <summary>The turn allowance a chief is seated at: the policy in force's, read strictly — an unreadable policy refuses, never a default.</summary>
    TurnAllowance PolicyAllowance()
    {
        var inForce = new Board.BoardReader(db).InForce();
        if (inForce.CouldNotBeRead)
            throw Refused($"the board's policy could not be read ({inForce.Why}), so the turn allowance a chief is seated at is not known.");
        return TurnAllowance.From(inForce.Policy.TurnAllowanceInput, inForce.Policy.TurnAllowanceOutput);
    }
}

