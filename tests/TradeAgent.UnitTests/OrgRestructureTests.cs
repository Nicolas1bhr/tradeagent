using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A RESTRUCTURING IS ONE TRANSACTION (<c>U-org-verbs-d</c>, O6 part d; ORCHESTRATION § 2.9): it disposes every open mandate held or
/// decided inside what ends, fences the positions it touches and moves no past charge. Store-level, over the chart
/// <see cref="OrgStructureTests"/> builds with raw SQL. No model.
/// </summary>
public partial class OrgStructureTests
{
    /// <summary>A member of <see cref="Sub"/>, under <see cref="SubHead"/>.</summary>
    const string SubMember = "pos-d1c0ffee0004";

    static MandateRow Mandate(OrgStore org, string decider, string holder) =>
        org.Assign(new MandateIssue(decider, null, holder, MandateKind.Task, "find out whether the funding skew predicts the next hour",
            null, MandateShape.Report, false, Now.AddDays(7), 5m, 2), Now);

    /// <summary>Every launch a unit's chain funded, with its reservation and cost, as the ledger holds them.</summary>
    static string[] ChargedTo(Database db, string unit) => db.Read(_ =>
    {
        using var c = db.Cmd("""
            SELECT a.id || ' ' || a.reserved_cost || ' ' || COALESCE(a.cost, '-') || ' ' || f.depth
              FROM ai_attempt_funding f JOIN ai_attempt a ON a.id = f.attempt_id WHERE f.unit_id = $u ORDER BY a.id
            """, ("$u", unit));
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows.ToArray();
    });

    static long Untaken(Database db, string mandate) =>
        Count(db, "mission_event", $"kind = 'mandate' AND payload = '{mandate}' AND consumed_at IS NULL");

    /// <summary>
    /// (1) THE TRANSACTION CANCELS, FENCES AND MOVES NO PAST CHARGE: closing <see cref="Sub"/> cancels the mandate HELD inside it
    /// (decided from outside) and the one DECIDED inside it, each with the app's reason naming the act, their holders' untaken wakes
    /// withdrawn; a mandate wholly outside it stays open; the position touched is fenced, so its running launch is; and every launch
    /// the unit's chain funded reads exactly as before — its rows, reservations, costs and depths (BUDGET-001). RED at base: no
    /// <c>OrgStore.Restructure</c>. The watched mutant: skipping the mandates HELD in the closed unit leaves one open.
    /// </summary>
    [Fact]
    public void A_restructure_cancels_fences_and_moves_no_past_charge()
    {
        using var db = Chart();
        Seat(db, SubMember, Sub);
        var org = new OrgStore(db);
        var heldInside = Mandate(org, Head, SubHead);
        var decidedInside = Mandate(org, SubHead, SubMember);
        var outside = Mandate(org, Head, Member);
        var running = Funded(db, SubHead, Sub, Div, OrgIds.Root);
        var spent = Funded(db, SubMember, Sub, Div, OrgIds.Root);
        Assert.True(new AiAttemptStore(db).End(spent, 0, Now, 10, 0, 5, 5, 0, null, 0.04m, null, null));
        var charged = ChargedTo(db, Sub);
        Assert.Equal(2, charged.Length);
        Assert.Equal(1, Untaken(db, heldInside.Id));

        var disposed = org.Restructure(new Restructuring($"closing {Sub}", Head, [Sub], [], [SubHead, SubMember]), Now.AddMinutes(1));

        foreach (var m in new[] { heldInside, decidedInside })
        {
            Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(m.Id));
            var why = org.MandateEvents(m.Id)[^1];
            Assert.Equal(OrgStore.AppActor, why.Actor);
            Assert.Contains($"closing {Sub}", why.Reason, StringComparison.Ordinal);
            Assert.Equal(0, Untaken(db, m.Id));
        }
        Assert.True(org.IsOpen(outside.Id), "a mandate wholly outside the restructuring was disposed of");
        Assert.Equal(new[] { heldInside.Id, decidedInside.Id }.Order(StringComparer.Ordinal), disposed.Cancelled.Order(StringComparer.Ordinal));
        Assert.Equal(2, disposed.Fences.Count);
        Assert.Equal(OrgEventKind.Fenced, org.FencedSince(running)?.Cause);
        Assert.Equal(charged, ChargedTo(db, Sub));
    }

    // ---- item 2: org-retire -------------------------------------------------------------------------------------------

    static RetireRequest RetireAsk(string position, string caller = Head) =>
        new(caller, null, position, "the member's line of work is answered", ["retire", "keep"], [], [Admissible()]);

    /// <summary>One refusal of a retirement: the target, the caller, and the words it is refused in.</summary>
    public static TheoryData<string> RetireRefusals => new()
    {
        "operations", "research", "a division head", "the chief", "the caller itself", "not the parent's head", "an ended position", "no forecast"
    };

    /// <summary>
    /// (2a) EACH REFUSAL OF A RETIREMENT, IN WORDS, WITH NOTHING WRITTEN: <c>operations</c> (a two-seat board act, R24 § 4.1), the legacy
    /// <c>research</c> (never an agent's: an app trigger or a board act), a division head (the same), the chief (a board act), the
    /// caller itself, a caller that does not head the position's parent unit, a position already ended, and no forecast. RED at base: no
    /// <c>OrgStore.Retire</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RetireRefusals))]
    public void Each_refusal_of_a_retirement_is_in_words_and_writes_nothing(string refusal)
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var (ask, words) = refusal switch
        {
            "operations" => (RetireAsk(CouncilRoles.Operations), "a two-seat board act"),
            "research" => (RetireAsk(CouncilRoles.Research), "app trigger or a board act"),
            "a division head" => (RetireAsk(Head, caller: SubHead), "app trigger or a board act"),
            "the chief" => (RetireAsk(Chief), "a two-seat board act"),
            "the caller itself" => (RetireAsk(Head), "cannot retire itself"),
            "not the parent's head" => (RetireAsk(Member, caller: SubHead), "only the parent unit's head"),
            "an ended position" => (RetireAsk(SubHead), "has ended already"),
            "no forecast" => (RetireAsk(SubHead) with { Forecasts = [] }, "1 to"),
            _ => throw new InvalidOperationException(refusal)
        };
        if (refusal == "the chief")
        {
            Seat(db, Chief, OrgIds.Root);
            Exec(db, "UPDATE org_unit SET head_position_id=$c WHERE id='org'", ("$c", Chief));
        }
        if (refusal == "an ended position") Exec(db, "UPDATE org_position SET status='ended' WHERE id=$p", ("$p", SubHead));

        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(() => org.Retire(ask, Now));
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
    }

    /// <summary>
    /// (2b) A RETIREMENT ENDS THE POSITION, LEAVES ITS UNIT VACANT AND DISPOSES OF ITS WORK, IN ONE WRITE: the division's head retires
    /// the head of the subdivision under it — the position <c>ended</c> with its time, the subdivision's head vacant, the <c>retire</c>
    /// decision with its rationale in the ledger, the mandate it held cancelled with the app's reason naming the retirement, the
    /// position fenced, and one <c>retired</c> event decided by the head. Not a structural change the weekly throttle counts.
    /// </summary>
    [Fact]
    public void A_retirement_ends_the_position_vacates_its_head_and_disposes_of_its_work()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        var held = Mandate(org, Head, SubHead);
        var running = Funded(db, SubHead, Sub, Div, OrgIds.Root);

        var retired = org.Retire(RetireAsk(SubHead), Now);

        Assert.Equal(OrgPositionStatus.Ended, retired.Position.Status);
        Assert.Equal(Now, retired.Position.EndedAt);
        Assert.Null(org.Units().Single(u => u.Id == Sub).HeadPositionId);
        Assert.Equal((Head, OrgDecisionKind.Retire), (retired.Decision.Position, retired.Decision.Kind));
        Assert.Equal([held.Id], retired.Cancelled);
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(held.Id));
        Assert.Contains($"{SubHead} was retired by {Head}", org.MandateEvents(held.Id)[^1].Reason, StringComparison.Ordinal);
        Assert.NotNull(org.FencedSince(running));
        Assert.Equal(1, Count(db, "org_event", $"kind = 'retired' AND position_id = '{SubHead}' AND decider = '{Head}' AND id = {retired.EventId}"));
        Assert.Equal(0, Count(db, "org_event", "kind IN ('chartered', 'closed', 'merged')"));
    }

    // ---- item 3: org-close and org-merge ------------------------------------------------------------------------------

    /// <summary>A unit under <paramref name="parent"/> created <paramref name="days"/> ago, headed by <paramref name="head"/> when one is named.</summary>
    static void Aged(Database db, string unit, string parent, string kind, double days, string? head = null)
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,$k,NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$k", kind), ("$at", Now.AddDays(-days).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        if (head is null) return;
        Seat(db, head, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", head), ("$t", unit));
    }

    static CloseRequest CloseAsk(string unit, string caller = Head) =>
        new(caller, null, unit, "the team's question is answered", ["close", "keep"], [], [Admissible()]);

    static MergeRequest MergeAsk(string unit, string into, string caller = Head) =>
        new(caller, null, unit, into, "two teams chase one question", ["merge", "keep both"], [], [Admissible()]);

    const string Old = "team-0ld0c0ffee", Young = "team-y0un9c0ffee", OldHead = "pos-01dc0ffee001", OldMember = "pos-01dc0ffee002";

    /// <summary>One refusal of a close or a merge: the act, and the words it is refused in.</summary>
    public static TheoryData<string> CloseRefusals => new()
    {
        "close the organisation", "close div-operations", "close div-research", "close a unit with open children", "close a 10-day-old unit",
        "close a third change in seven days", "close a closed unit", "close by a member", "merge into itself", "merge non-siblings",
        "merge different kinds", "merge a 10-day-old unit", "merge into div-research", "merge past five members", "merge a third change in seven days"
    };

    /// <summary>
    /// (3a) EACH REFUSAL OF A CLOSE OR A MERGE, IN WORDS, WITH NOTHING WRITTEN: the organisation itself and <c>div-operations</c> (two-seat
    /// board acts), <c>div-research</c> (an app trigger or a board act), open child units, a unit younger than fourteen days whose charter
    /// no forecast resolved, a third structural change in seven days, a unit closed already, a caller that does not head the parent; a
    /// merge into itself, of units that are not siblings or not of one kind, of a young unit, into a reserved unit, past five members.
    /// RED at base: no <c>OrgStore.Close</c> or <c>OrgStore.Merge</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CloseRefusals))]
    public void Each_refusal_of_a_close_or_a_merge_is_in_words_and_writes_nothing(string refusal)
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        Aged(db, Young, Div, OrgUnitKind.Team, 10);
        Func<object> act = () => org.Close(CloseAsk(Old), Now);
        string words;
        switch (refusal)
        {
            case "close the organisation": act = () => org.Close(CloseAsk(OrgIds.Root), Now); words = "a two-seat board act"; break;
            case "close div-operations": act = () => org.Close(CloseAsk(OrgIds.Operations), Now); words = "a two-seat board act"; break;
            case "close div-research": act = () => org.Close(CloseAsk(OrgIds.Research), Now); words = "app trigger or a board act"; break;
            case "close a unit with open children":
                Aged(db, "team-undersub0", Sub, OrgUnitKind.Team, 20);
                Exec(db, "UPDATE org_unit SET created_at=$at WHERE id=$s", ("$at", Now.AddDays(-20).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$s", Sub));
                act = () => org.Close(CloseAsk(Sub), Now); words = "has open units under it (team-undersub0)"; break;
            case "close a 10-day-old unit": act = () => org.Close(CloseAsk(Young), Now); words = "a unit lives 14 days"; break;
            case "close a third change in seven days":
            case "merge a third change in seven days":
                foreach (var decider in new[] { Head, SubHead })
                    Exec(db, "INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES($at, $u, 'closed', $d, 'x')",
                        ("$at", Now.AddDays(-2).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$u", Div), ("$d", decider));
                if (refusal.StartsWith("merge", StringComparison.Ordinal))
                {
                    Aged(db, "team-twin0c0ffee", Div, OrgUnitKind.Team, 20);
                    act = () => org.Merge(MergeAsk(Old, "team-twin0c0ffee"), Now);
                }
                words = "2 structural changes in the last seven days"; break;
            case "close a closed unit":
                Exec(db, "UPDATE org_unit SET status='closed' WHERE id=$t", ("$t", Old)); words = "is closed already"; break;
            case "close by a member": act = () => org.Close(CloseAsk(Old, caller: Member), Now); words = "only the parent unit's head"; break;
            case "merge into itself": act = () => org.Merge(MergeAsk(Old, Old), Now); words = "never into itself"; break;
            case "merge non-siblings":
                Aged(db, "team-elsewhere0", Sub, OrgUnitKind.Team, 20);
                act = () => org.Merge(MergeAsk(Old, "team-elsewhere0"), Now); words = "a merge joins siblings of one parent"; break;
            case "merge different kinds":
                Exec(db, "UPDATE org_unit SET created_at=$at WHERE id=$s", ("$at", Now.AddDays(-20).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ("$s", Sub));
                act = () => org.Merge(MergeAsk(Sub, Old), Now); words = "a merge joins units of one kind"; break;
            case "merge a 10-day-old unit": act = () => org.Merge(MergeAsk(Young, Old), Now); words = "a unit lives 14 days"; break;
            case "merge into div-research":
                Seat(db, Chief, OrgIds.Root);
                Exec(db, "UPDATE org_unit SET head_position_id=$c WHERE id='org'", ("$c", Chief));
                Aged(db, "div-0th3rc0ffee", OrgIds.Root, OrgUnitKind.Division, 20);
                act = () => org.Merge(MergeAsk("div-0th3rc0ffee", OrgIds.Research, caller: Chief), Now); words = "app trigger or a board act"; break;
            case "merge past five members":
                Aged(db, "team-full0c0ffee", Div, OrgUnitKind.Team, 20);
                for (var i = 0; i < 5; i++) Seat(db, $"pos-f011c0ffee0{i}", "team-full0c0ffee");
                Seat(db, OldMember, Old);
                act = () => org.Merge(MergeAsk(Old, "team-full0c0ffee"), Now); words = "would have 7 members"; break;
            default:
                throw new InvalidOperationException(refusal);
        }

        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(act);
        Assert.Contains(words, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
    }

    /// <summary>
    /// (3b) A CLOSE ENDS THE UNIT AND ITS POSITIONS AND DELETES NOTHING: the division's head closes a team chartered fifteen days ago —
    /// the team <c>closed</c> with its time, its head and member <c>ended</c>, the mandate held in it cancelled, both fenced, the
    /// <c>close</c> decision, one <c>closed</c> event naming what it ended; every launch the team's chain funded reads as before. A
    /// fourteen-day-young unit closes once a forecast on its charter resolved (the kill criterion).
    /// </summary>
    [Fact]
    public void A_close_ends_the_unit_and_its_positions_and_moves_no_past_charge()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        Seat(db, OldMember, Old);
        var held = Mandate(org, OldHead, OldMember);
        var running = Funded(db, OldMember, Old, Div, OrgIds.Root);
        var charged = ChargedTo(db, Old);

        var closed = org.Close(CloseAsk(Old), Now);

        Assert.Equal((OrgUnitStatus.Closed, (DateTimeOffset?)Now), (closed.Unit.Status, closed.Unit.ClosedAt));
        Assert.Equal(new[] { OldHead, OldMember }.Order(StringComparer.Ordinal), closed.Ended.Order(StringComparer.Ordinal));
        Assert.All(closed.Ended, p => Assert.Equal(OrgPositionStatus.Ended, org.Position(p)!.Status));
        Assert.Equal([held.Id], closed.Cancelled);
        Assert.Equal(OrgDecisionKind.Close, closed.Decision.Kind);
        Assert.NotNull(org.FencedSince(running));
        Assert.Equal(charged, ChargedTo(db, Old));
        Assert.Equal(1, Count(db, "org_event", $"kind = 'closed' AND unit_id = '{Old}' AND decider = '{Head}'"));

        // A YOUNG UNIT WHOSE CHARTER'S FORECAST RESOLVED: the kill criterion lets it close before fourteen days (a chart of its own, so
        // the charter and this close are its week's two structural changes).
        using var young = Chart();
        var youngOrg = new OrgStore(young);
        var made = youngOrg.Charter(Ask(refs: [CurrentRun(young)]), Metered, Now.AddDays(-5));
        Assert.Contains("a unit lives 14 days", Assert.Throws<TradeAgentException>(() => youngOrg.Close(CloseAsk(made.Unit.Id), Now)).Message,
            StringComparison.Ordinal);
        var forecast = youngOrg.ForecastsOn(made.Decision.Id).Single();
        Exec(young, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES($f, 'no', 'verdict', 'v-1', 'x', $at)",
            ("$f", forecast.Id), ("$at", Now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        Assert.Equal(OrgUnitStatus.Closed, youngOrg.Close(CloseAsk(made.Unit.Id), Now).Unit.Status);
    }

    /// <summary>
    /// (3c) A MERGE MOVES POSITIONS AND CHILDREN AND KEEPS A'S HISTORY: the division's head merges subdivision A into its sibling B —
    /// A <c>closed</c> naming B, its head vacant; A's head and member now in B, A's team now under B; the mandate issued in A cancelled;
    /// A's and B's positions fenced; the <c>merge</c> decision; A's past launch still charged to A's chain exactly as before, and the
    /// member's NEXT launch funded through B. RED at base: no <c>OrgStore.Merge</c>.
    /// </summary>
    [Fact]
    public void A_merge_moves_positions_and_children_and_keeps_As_history()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        const string a = "sub-aaaa0c0ffee", b = "sub-bbbb0c0ffee", aHead = "pos-aaaac0ffee01", aMember = "pos-aaaac0ffee02",
            bHead = "pos-bbbbc0ffee01", aTeam = "team-aaaa0c0ffee";
        Aged(db, a, Div, OrgUnitKind.Subdivision, 20, aHead);
        Seat(db, aMember, a);
        Aged(db, b, Div, OrgUnitKind.Subdivision, 20, bHead);
        Aged(db, aTeam, a, OrgUnitKind.Team, 20);
        var held = Mandate(org, aHead, aMember);
        var past = Funded(db, aMember, a, Div, OrgIds.Root);
        var aHistory = Count(db, "org_event", $"unit_id = '{a}'");
        var charged = ChargedTo(db, a);

        var merged = org.Merge(MergeAsk(a, b), Now);

        Assert.Equal((OrgUnitStatus.Closed, (string?)null), (merged.Closed.Status, merged.Closed.HeadPositionId));
        Assert.Equal(new[] { aHead, aMember }.Order(StringComparer.Ordinal), merged.MovedPositions.Order(StringComparer.Ordinal));
        Assert.All(merged.MovedPositions, p => Assert.Equal((b, OrgPositionStatus.Active), (org.Position(p)!.UnitId, org.Position(p)!.Status)));
        Assert.Equal([aTeam], merged.MovedUnits);
        Assert.Equal(b, org.Units().Single(u => u.Id == aTeam).ParentId);
        Assert.Equal([held.Id], merged.Cancelled);
        Assert.Equal(OrgDecisionKind.Merge, merged.Decision.Kind);
        Assert.NotNull(org.FencedSince(past));
        Assert.Equal(3, Count(db, "org_event", $"kind = 'fenced' AND position_id IN ('{aHead}', '{aMember}', '{bHead}')"));
        Assert.Contains($"into={b}", db.Read(_ =>
        {
            using var c = db.Cmd("SELECT detail FROM org_event WHERE kind = 'merged' AND unit_id = $a", ("$a", a));
            return (string)c.ExecuteScalar()!;
        }), StringComparison.Ordinal);

        // A'S HISTORY STAYS A'S: its events (the merge's own and the fences of its positions added), and every past charge.
        Assert.Equal(aHistory + 3, Count(db, "org_event", $"unit_id = '{a}'"));
        Assert.Equal(charged, ChargedTo(db, a));
        Assert.Equal([b, Div, OrgIds.Root], org.FundingLine(aMember).Select(u => u.Id));
    }

    // ---- item 4: the owner's veto and activation, in-process -----------------------------------------------------------

    /// <summary>Every position declared on a puppet seat: no model, so the unconfined-seat ceiling never counts it.</summary>
    static string? Puppets(string _) => SeatClasses.Puppet;

    /// <summary>
    /// (4a) THE VETO RESTORES STRUCTURE, NEVER WORK: the division's head closes a team (its head and member ended, the mandate held in
    /// it cancelled); the owner's veto restores the team DORMANT with both positions DORMANT, decided by the owner of record, and fences
    /// them — while the cancelled mandate stays cancelled, the close's decision stays recorded and every launch the team's chain funded
    /// reads as before. Vetoing it twice is refused. A charter's veto closes the unit; a hire's ends the position; a retirement's
    /// restores it DORMANT as its unit's head; a merge's veto, the app's own events and the owner's are refused in words. No veto is a
    /// structural change the weekly throttle counts. RED at base: no <c>OrgStore.Veto</c>.
    /// </summary>
    [Fact]
    public void The_veto_restores_structure_never_work()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        Seat(db, OldMember, Old);
        var held = Mandate(org, OldHead, OldMember);
        var past = Funded(db, OldMember, Old, Div, OrgIds.Root);
        var closed = org.Close(CloseAsk(Old), Now);
        var charged = ChargedTo(db, Old);
        var ledger = Count(db, "ledger_entry");

        var vetoed = org.Veto(closed.EventId, Now.AddMinutes(1));

        Assert.Equal(OrgEventKind.Closed, vetoed.Kind);
        Assert.Equal(new[] { Old, OldHead, OldMember }.Order(StringComparer.Ordinal), vetoed.Restored.Order(StringComparer.Ordinal));
        var unit = org.Units().Single(u => u.Id == Old);
        Assert.Equal((OrgUnitStatus.Dormant, (DateTimeOffset?)null, (string?)OldHead), (unit.Status, unit.ClosedAt, unit.HeadPositionId));
        Assert.All(new[] { OldHead, OldMember }, p => Assert.Equal((OrgPositionStatus.Dormant, (DateTimeOffset?)null),
            (org.Position(p)!.Status, org.Position(p)!.EndedAt)));
        Assert.Equal(1, Count(db, "org_event", $"id = {vetoed.VetoEventId} AND kind = 'vetoed' AND decider = '{OrgDecider.Owner}'"));
        Assert.Equal(2, Count(db, "org_event", $"kind = 'fenced' AND decider = '{OrgDecider.Owner}'"));
        // ... NEVER WORK: the mandate stays cancelled, the decision stays, the launches stay.
        Assert.Equal(MandateEventKind.Cancelled, org.MandateStatus(held.Id));
        Assert.NotNull(org.Decision(closed.Decision.Id));
        Assert.Equal(ledger, Count(db, "ledger_entry"));
        Assert.Equal(charged, ChargedTo(db, Old));
        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(db).Get(past)!.State);
        Assert.Contains("vetoed already", Assert.Throws<TradeAgentException>(() => org.Veto(closed.EventId, Now)).Message, StringComparison.Ordinal);

        // A CHARTER'S VETO CLOSES THE UNIT; A HIRE'S ENDS THE POSITION; A RETIREMENT'S RESTORES IT AS ITS UNIT'S HEAD.
        var made = org.Charter(Ask(refs: [CurrentRun(db)]), Metered, Now);
        Assert.Equal([made.Unit.Id], org.Veto(made.EventId, Now).Ended);
        Assert.Equal(OrgUnitStatus.Closed, org.Units().Single(u => u.Id == made.Unit.Id).Status);
        var hired = org.Hire(new HireRequest(Head, null, Old, false, "one more pair of hands", ["hire"], [CurrentRun(db)], [Admissible()]), Now);
        Assert.Equal([hired.Position.Id], org.Veto(hired.EventId, Now).Ended);
        Assert.Equal(OrgPositionStatus.Ended, org.Position(hired.Position.Id)!.Status);
        var retired = org.Retire(RetireAsk(SubHead), Now);
        Assert.Equal([SubHead], org.Veto(retired.EventId, Now).Restored);
        Assert.Equal((OrgPositionStatus.Dormant, (string?)SubHead), (org.Position(SubHead)!.Status, org.Units().Single(u => u.Id == Sub).HeadPositionId));

        // REFUSED IN WORDS: the app's own seed, a merge, an event that is not there.
        var seeded = db.Read(_ => { using var c = db.Cmd("SELECT id FROM org_event WHERE kind = 'seeded' LIMIT 1"); return (long)c.ExecuteScalar()!; });
        Assert.Contains("never the app's or your own act", Assert.Throws<TradeAgentException>(() => org.Veto(seeded, Now)).Message, StringComparison.Ordinal);
        Aged(db, "team-twin0c0ffee", Div, OrgUnitKind.Team, 20);
        Exec(db, "DELETE FROM org_event WHERE kind IN ('chartered', 'closed') AND decider = $h", ("$h", Head));
        var merged = org.Merge(MergeAsk(Old, "team-twin0c0ffee"), Now);
        Assert.Contains("a merge is not undone", Assert.Throws<TradeAgentException>(() => org.Veto(merged.EventId, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("is not on the chart's record", Assert.Throws<TradeAgentException>(() => org.Veto(999_999, Now)).Message, StringComparison.Ordinal);
        Assert.Equal(0, Count(db, "org_event", $"kind IN ('chartered', 'closed', 'merged') AND decider = '{OrgDecider.Owner}'"));
        Assert.DoesNotContain(OrgEventKind.Vetoed, OrgEventKind.Structural);
    }

    /// <summary>
    /// (4b) THE OWNER'S ACTIVATION IS HIS ONLY WHILE NO BOARD SEAT IS ACTIVE: a dormant hire turns active, one <c>activated</c> event
    /// decided by the owner; an active, ended or unknown position is refused; and once a board seat is active the activation is refused
    /// in words naming the board, nothing written (R24; the orchestrator's generalised ruling).
    /// </summary>
    [Fact]
    public void The_owners_activation_is_his_only_while_no_board_seat_is_active()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        var hired = org.Hire(new HireRequest(Head, null, Old, false, "a member for the team", ["hire"], [CurrentRun(db)], [Admissible()]), Now);
        var dormant = org.Hire(new HireRequest(Head, null, Old, false, "a second member", ["hire"], [CurrentRun(db)],
            [Admissible()]), Now);

        Assert.Equal(OrgPositionStatus.Active, org.Activate(hired.Position.Id, Puppets, 3, Now).Status);
        Assert.Equal(1, Count(db, "org_event", $"kind = 'activated' AND position_id = '{hired.Position.Id}' AND decider = '{OrgDecider.Owner}'"));
        Assert.Contains("only a dormant position", Assert.Throws<TradeAgentException>(() => org.Activate(hired.Position.Id, Puppets, 3, Now)).Message, StringComparison.Ordinal);
        Assert.Contains("no position", Assert.Throws<TradeAgentException>(() => org.Activate("pos-000000000000", Puppets, 3, Now)).Message, StringComparison.Ordinal);

        // A BOARD SEAT ACTIVE (U-board-b's rung admits it; the test rebuilds the table to store one).
        db.Exec("DROP TRIGGER board_seat_sealed_u");
        db.Exec("ALTER TABLE board_seat RENAME TO board_seat_49");
        db.Exec("CREATE TABLE board_seat(id TEXT PRIMARY KEY, seat_class TEXT, model_class TEXT, vendor_family TEXT, status TEXT, founded_at TEXT) WITHOUT ROWID");
        Exec(db, "INSERT INTO board_seat VALUES('board-1', 'harness', 'strong', 'anthropic', 'active', $at)", ("$at", Stamp));
        var before = Written(db);
        var refused = Assert.Throws<TradeAgentException>(() => org.Activate(dormant.Position.Id, Puppets, 3, Now));
        Assert.Contains("the security board's now that a board seat is active", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        Assert.Equal(OrgPositionStatus.Dormant, org.Position(dormant.Position.Id)!.Status);
    }

    /// <summary>
    /// (4c) GUARD — NO PIPE OP, DOOR VERB OR <c>trade</c> VERB REACHES THE VETO OR THE ACTIVATION (AUTH-001): no op the gateway serves —
    /// and so no door verb, which serves every organisation op — and no CLI form names either, and no source file outside the store and
    /// the app's own process calls them. RED with an op added (an <c>org-veto</c> or <c>org-activate</c> in <c>Ops</c>).
    /// </summary>
    [Fact]
    public void No_pipe_op_or_door_verb_reaches_veto_or_activation()
    {
        string[] words = ["veto", "activat"];
        var constants = typeof(Ops).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(constants);
        foreach (var op in constants.Concat(Ops.Organisation))
            foreach (var word in words)
                Assert.DoesNotContain(word, op, StringComparison.OrdinalIgnoreCase);
        foreach (var op in TradeAgent.Gateway.GatewaySchema.Ops())
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
                Assert.All(op.Args, a => Assert.DoesNotContain(word, a.Name, StringComparison.OrdinalIgnoreCase));
            }

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
        var callers = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.StartsWith("src/TradeAgent.App/", StringComparison.Ordinal)
                        && !f.StartsWith("src/TradeAgent.Core/Db/OrgStore", StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(root, f)), @"\.(Veto|Activate)\s*\("))
            .ToList();
        Assert.Empty(callers);
    }

    // ---- item 5: the owner's unconfined-seat ceiling -------------------------------------------------------------------

    /// <summary>
    /// (5) A FOURTH UNCONFINED SEAT IS REFUSED WHATEVER THE SETTING SAYS (ORGANISATION § 10; CRED-001): with the legacy pair and one
    /// more position active on seats that run a model below a proven cell, a fourth on a harness seat is refused at the gate — about the
    /// host, both counts named — under a setting of 3 and under a setting of 10 (a setting never raises the code's three); a setting
    /// of 2 refuses the third; a puppet seat and a cell are exempt; and the owner's own activation of a dormant fourth is refused in the
    /// same words, nothing written, while a third activates. RED with the ceiling's line removed from <c>PositionActivation.Decide</c>.
    /// </summary>
    [Fact]
    public void A_fourth_unconfined_seat_is_refused_whatever_the_setting_says()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        static string? Harness(string _) => SeatClasses.Harness;
        var active = org.UnconfinedSeatsActive(Harness);
        Assert.Contains(CouncilRoles.Operations, active);
        Assert.Contains(CouncilRoles.Research, active);

        SeatFacts Facts(int setting, string seat, params string[] others) => new()
        {
            Standing = org.Standing(Member), DeclaredClass = seat, ProvenCell = true, UnconfinedActive = others, UnconfinedSeatSetting = setting
        };
        string[] three = [CouncilRoles.Operations, CouncilRoles.Research, Head];
        foreach (var setting in new[] { 3, 10, int.MaxValue })
        {
            var refused = PositionActivation.Decide(Member, org.Standing(Member), Facts(setting, SeatClasses.Harness, three));
            Assert.NotNull(refused);
            Assert.Equal(RefusalKind.HostOrLaunch, refused!.Kind);
            Assert.Contains("3 such positions are active", refused.Words, StringComparison.Ordinal);
            Assert.Contains("unconfined-seat ceiling is 3", refused.Words, StringComparison.Ordinal);
        }
        Assert.Contains("ceiling is 2", PositionActivation.Decide(Member, org.Standing(Member),
            Facts(2, SeatClasses.Harness, CouncilRoles.Operations, CouncilRoles.Research))!.Words, StringComparison.Ordinal);
        Assert.Null(PositionActivation.Decide(Member, org.Standing(Member), Facts(3, SeatClasses.Harness, CouncilRoles.Operations, CouncilRoles.Research)));
        Assert.Null(PositionActivation.Decide(Member, org.Standing(Member), Facts(3, SeatClasses.Puppet, three)));
        Assert.Null(PositionActivation.Decide(Member, org.Standing(Member), Facts(3, SeatClasses.Cell, three)));

        // THE OWNER'S ACTIVATION: the chart's own count — the legacy pair and every active position here on a harness seat — is past
        // three already, so a dormant hire is refused whatever the setting; with only the pair counted, a third activates.
        Aged(db, Old, Div, OrgUnitKind.Team, 15, OldHead);
        var hired = org.Hire(new HireRequest(Head, null, Old, false, "a member for the team", ["hire"], [CurrentRun(db)], [Admissible()]), Now);
        var before = Written(db);
        var words = Assert.Throws<TradeAgentException>(() => org.Activate(hired.Position.Id, Harness, 99, Now)).Message;
        Assert.Contains("unconfined-seat ceiling is 3 (his setting 99", words, StringComparison.Ordinal);
        Assert.Equal(before, Written(db));
        string? OnlyThePairUnconfined(string id) => CouncilRoles.IsKnown(id) || id == hired.Position.Id ? SeatClasses.Harness : SeatClasses.Puppet;
        Assert.Equal(OrgPositionStatus.Active, org.Activate(hired.Position.Id, OnlyThePairUnconfined, 3, Now).Status);
    }

    // ---- the fix pass (the Codex review of U-org-verbs-d, triaged by seat B) -----------------------------------------------

    const string SubA = "sub-aaaa0c0ffee", SubB = "sub-bbbb0c0ffee", AHead = "pos-aaaac0ffee01", AMember = "pos-aaaac0ffee02",
        BHead = "pos-bbbbc0ffee01", ATeam = "team-aaaa0c0ffee", ATeamHead = "pos-aaaac0ffee03", BTeam = "team-bbbb0c0ffee";

    /// <summary>Two sibling subdivisions under <see cref="Div"/>, twenty days old: A with a head, a member and a headed team; B with a head.</summary>
    static void Siblings(Database db)
    {
        Aged(db, SubA, Div, OrgUnitKind.Subdivision, 20, AHead);
        Seat(db, AMember, SubA);
        Aged(db, SubB, Div, OrgUnitKind.Subdivision, 20, BHead);
        Aged(db, ATeam, SubA, OrgUnitKind.Team, 20, ATeamHead);
    }

    /// <summary>
    /// (F1) A MERGE FENCES EVERY POSITION IN THE CHILDREN IT MOVES: the head of A's team is mid-turn when A merges into B; its unit's
    /// parent — its funding chain and the authority above it — changed, so its running launch is fenced. RED before the fix: only A's
    /// and B's own positions were fenced.
    /// </summary>
    [Fact]
    public void A_merge_fences_every_position_in_the_children_it_moves()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Siblings(db);
        var running = Funded(db, ATeamHead, ATeam, SubA, Div, OrgIds.Root);

        org.Merge(MergeAsk(SubA, SubB), Now);

        Assert.Equal(OrgEventKind.Fenced, org.FencedSince(running)?.Cause);
    }

    /// <summary>
    /// (F2) A MERGE CARRIES A'S RESERVATIONS, DEBTS AND HISTORY TO B (ORCHESTRATION § 2.9): after A merges into B, B's day — the
    /// totals admission compares B's envelope against (<see cref="AiAttemptStore.UnitTotalsBetween"/>, the same query
    /// <see cref="AiAttemptStore.Begin"/> runs) — holds A's outstanding reservation and its spend, read through the recorded chain and
    /// the merge's successor link; A's own day and every funding row read as before (never rewritten). RED before the fix: B's day
    /// was empty.
    /// </summary>
    [Fact]
    public void A_merge_carries_As_reservations_and_spend_to_Bs_day()
    {
        using var db = Chart();
        var org = new OrgStore(db);
        Siblings(db);
        Funded(db, AMember, SubA, Div, OrgIds.Root);
        var spent = Funded(db, AHead, SubA, Div, OrgIds.Root);
        Assert.True(new AiAttemptStore(db).End(spent, 0, Now, 10, 0, 5, 5, 0, null, 0.04m, null, null));
        var ledger = new AiAttemptStore(db);
        AiAttemptTotals Day(string unit) => ledger.UnitTotalsBetween(Now.AddDays(-1), Now.AddDays(1), unit);
        var aDay = Day(SubA);
        var charged = ChargedTo(db, SubA);
        Assert.Equal((0m, 0m), (Day(SubB).Spent, Day(SubB).Reserved));

        org.Merge(MergeAsk(SubA, SubB), Now);

        Assert.Equal((0.04m, 0.10m), (Day(SubB).Spent, Day(SubB).Reserved));
        Assert.Equal((aDay.Spent, aDay.Reserved), (Day(SubA).Spent, Day(SubA).Reserved));
        Assert.Equal(charged, ChargedTo(db, SubA));
        Assert.Empty(ChargedTo(db, SubB));
    }

}
