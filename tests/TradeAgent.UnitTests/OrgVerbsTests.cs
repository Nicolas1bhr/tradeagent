using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HEAD RE-SCOPES, AN EXECUTOR PROPOSES, EVERY CAPABLE POSITION READS ITS OWN PLACE (<c>U-org-verbs-a</c>, O6 part a;
/// ORCHESTRATION §§ 2.2, 2.5, 2.9; ORGANISATION § 4). In-process <see cref="GatewayPipeServer"/> calls under launch ids the test
/// seeded in the ledger, as <see cref="OrgAssignmentTests"/>' other parts do. No real model.
/// </summary>
public partial class OrgAssignmentTests
{
    const string RescopeWhy = "two clean failures: the question was too wide for one report";
    const string NarrowerGoal = "summarise in ten lines whether the funding skew leads the next hour";

    static IpcRequest RescopeFrame(string mandate, decimal allocation = 3m, params (string Key, object Value)[] more) =>
        Frame(Ops.MandateRescope, [
            ("mandate", mandate), ("why", RescopeWhy), ("goal", NarrowerGoal), ("kind", MandateKind.Task),
            ("shape", MandateShape.Report), ("due", Due.ToString("O")), ("allocation", allocation), .. more
        ]);

    /// <summary>A field the wire left out, or wrote as null: the writer omits a null.</summary>
    static bool Absent(JsonElement e, string name) => !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    /// <summary>A mandate whose holder failed twice, cleanly: code retried once and then escalated it to its decider.</summary>
    static async Task<string> FailedTwice(TradingGateway gw, GatewayPipeServer server, Database db)
    {
        var m = await Assigned(server, db, retries: 2);
        for (var i = 0; i < 2; i++)
            Assert.True((await server.CallAsync(DeliverFrame(m, TooLong), Member, Launch(db, Member, mandate: m))).Ok);
        Assert.Equal(MandateEventKind.Escalated, gw.Org.MandateStatus(m));
        return m;
    }

    // ---- item 1: mandate-rescope -----------------------------------------------------------------------------------

    /// <summary>
    /// (1a) A RE-SCOPE CLOSES THE OLD MANDATE AND ISSUES A LINKED SUCCESSOR IN ONE WRITE (ORCHESTRATION § 2.2: rows never update,
    /// failures are remembered): a mandate whose holder failed twice is re-scoped by its decider — the old reads <c>rescoped</c>
    /// with the new id, the new is <c>issued</c> with the old id, the holder is woken ONCE for the new, and the old mandate's spend
    /// and failures stay its own; <c>mandate-show</c> names the predecessor and its measured cost, and the old one its successor.
    /// When the successor's wake cannot be written, nothing of the re-scope is. RED at base: no <c>mandate-rescope</c> op.
    /// </summary>
    [Fact]
    public async Task Rescope_closes_the_old_and_issues_a_linked_successor_in_one_write()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var old = await FailedTwice(gw, server, db);
        var spent = gw.Org.SpendOf(old);
        var deliveries = gw.Org.Deliveries(old).Count;

        // ONE WRITE: the successor's wake cannot be written, so neither is anything else of the re-scope.
        Exec(db, """
            CREATE TRIGGER abort_successor BEFORE INSERT ON mission_event WHEN NEW.kind = 'mandate'
            BEGIN SELECT RAISE(ABORT, 'the successor''s wake could not be written'); END
            """);
        Assert.False((await server.CallAsync(RescopeFrame(old), Head, Launch(db, Head))).Ok);
        Assert.Equal(1L, Count(db, "org_mandate"));
        Assert.Equal(MandateEventKind.Escalated, gw.Org.MandateStatus(old));
        Exec(db, "DROP TRIGGER abort_successor");

        var reply = await server.CallAsync(RescopeFrame(old), Head, Launch(db, Head));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var successor = Body(reply).GetProperty("mandate").GetString()!;
        Assert.NotEqual(old, successor);
        Assert.Equal(MandateEventKind.Issued, Body(reply).GetProperty("status").GetString());
        Assert.Equal(old, Body(reply).GetProperty("rescoped_from").GetString());

        var closed = gw.Org.MandateEvents(old)[^1];
        Assert.Equal((MandateEventKind.Rescoped, Head, RescopeWhy), (closed.Kind, closed.Actor, closed.Reason));
        Assert.Equal([successor], closed.Evidence);
        Assert.False(gw.Org.IsOpen(old));
        var issued = Assert.Single(gw.Org.MandateEvents(successor));
        Assert.Equal((MandateEventKind.Issued, Head), (issued.Kind, issued.Actor));
        Assert.Equal([old], issued.Evidence);
        var m = gw.Org.Mandate(successor)!;
        Assert.Equal((Head, Member, Team, 3m, NarrowerGoal), (m.Decider, m.Holder, m.UnitId, m.Allocation, m.Objective));

        // THE HOLDER IS WOKEN ONCE, FOR THE SUCCESSOR; nothing of the old one is left untaken.
        var wakes = Wakes(db, MissionEventKind.Mandate, Member);
        Assert.Equal(MissionEventIds.Mandate(successor, issued.Id), Assert.Single(wakes, w => w.Payload == successor).Id);
        Assert.DoesNotContain(wakes, w => w.Payload == old && w.ConsumedAt is null);

        // THE OLD MANDATE'S SPEND AND FAILURES STAY ITS OWN (BUDGET-001): nothing moves to the successor, which starts fresh.
        Assert.Equal(spent, gw.Org.SpendOf(old));
        Assert.Equal(deliveries, gw.Org.Deliveries(old).Count);
        Assert.Equal(0, gw.Org.SpendOf(successor).Launches);
        Assert.Empty(gw.Org.Deliveries(successor));

        var shown = Body(await server.CallAsync(Frame(Ops.MandateShow, ("mandate", successor)), Head, Launch(db, Head)));
        Assert.Equal(old, shown.GetProperty("predecessor").GetProperty("mandate").GetString());
        Assert.Equal(MandateEventKind.Rescoped, shown.GetProperty("predecessor").GetProperty("status").GetString());
        Assert.Equal(spent.Launches, shown.GetProperty("predecessor").GetProperty("measured_spend").GetProperty("launches").GetInt32());
        Assert.True(Absent(shown, "successor"), "a mandate with no successor named one");
        var before = Body(await server.CallAsync(Frame(Ops.MandateShow, ("mandate", old)), Member, Launch(db, Member)));
        Assert.Equal(successor, before.GetProperty("successor").GetString());
    }

    /// <summary>
    /// (1b) A LAUNCH UNDER A RE-SCOPED MANDATE IS REFUSED (BUDGET-001), and the re-scope withdrew the old mandate's untaken wakes —
    /// the sweep's FLOORED re-raise included (<c>U-rewake-floor</c>): a launch of the holder's ended undelivered, the sweep wrote its
    /// re-raise due a floor later, the decider re-scoped; that re-raise is withdrawn in the re-scope's write, a launch naming the
    /// old mandate is refused with no row and no reservation, and a launch answering the successor's wake is charged to it. RED at
    /// base: no <c>mandate-rescope</c> op.
    /// </summary>
    [Fact]
    public async Task A_launch_under_a_rescoped_mandate_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var old = gw.Org.Mandate(await Assigned(server, db))!;
        var t = DateTimeOffset.UtcNow;
        var first = TurnOn(db, Member, old, t);
        Assert.Equal(1, gw.Org.Sweep(t, Shipped));
        var reraise = Reraise(db, gw.Org, old, first)!;
        Assert.True(reraise.DueAt > t && reraise.ConsumedAt is null, "the floored re-raise was not pending");

        var reply = await server.CallAsync(RescopeFrame(old.Id), Head, Launch(db, Head));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var successor = Body(reply).GetProperty("mandate").GetString()!;
        var withdrawn = new MissionEventStore(db).Get(reraise.Id)!;
        Assert.Equal(MissionEventDisposition.Withdrawn, withdrawn.Disposition);
        Assert.Contains(successor, withdrawn.DispositionDetail, StringComparison.Ordinal);

        var rows = Count(db, "ai_attempt");
        var refused = Begin(db, Member, 0.10m, mandate: old.Id);
        Assert.False(refused.Admitted, "a launch was admitted under a re-scoped mandate");
        Assert.Equal(AiAdmission.Mandate, refused.Ceiling);
        Assert.Contains(MandateEventKind.Rescoped, refused.Refusal, StringComparison.Ordinal);
        Assert.Equal(rows, Count(db, "ai_attempt"));

        var wake = Assert.Single(Wakes(db, MissionEventKind.Mandate, Member), w => w.Payload == successor);
        var admitted = Begin(db, Member, 0.10m, consuming: [wake.Id]);
        Assert.True(admitted.Admitted, admitted.Refusal);
        Assert.Equal(1, gw.Org.SpendOf(successor).Launches);
        Assert.Equal(0, gw.Org.Sweep(DateTimeOffset.UtcNow.AddHours(1), Shipped));
        Assert.DoesNotContain(Wakes(db, MissionEventKind.Mandate, Member), w => w.Payload == old.Id && w.ConsumedAt is null);
    }

    /// <summary>
    /// (1c) ONLY ITS DECIDER RE-SCOPES A MANDATE (ORG-001: the row decides): the holder, another member and a head elsewhere are
    /// refused in words with nothing written; a frame naming a holder is refused (a different holder is a cancellation and a new
    /// assignment); the new terms are held to every rule an assignment is; a closed mandate is refused. RED at base: no op. WATCHED
    /// MUTANT: <c>Rescope</c> without its decider check — the holder re-scopes its own mandate and this goes red.
    /// </summary>
    [Fact]
    public async Task A_rescope_by_anyone_but_its_decider_is_refused_with_nothing_written()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);

        foreach (var caller in new[] { Member, Other, Outsider })
        {
            var refused = await server.CallAsync(RescopeFrame(m), caller, Launch(db, caller));
            Assert.False(refused.Ok, $"{caller} re-scoped a mandate it does not decide");
            Assert.Contains("only its decider", refused.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", refused.Error.Message, StringComparison.Ordinal);
        }

        var holder = await server.CallAsync(RescopeFrame(m, 3m, ("holder", Other)), Head, Launch(db, Head));
        Assert.Contains("'holder' is not an argument", holder.Error!.Message, StringComparison.Ordinal);
        var unbounded = await server.CallAsync(RescopeFrame(m, 0m), Head, Launch(db, Head));
        Assert.Contains("above zero", unbounded.Error!.Message, StringComparison.Ordinal);
        var silent = Frame(Ops.MandateRescope, ("mandate", m), ("goal", NarrowerGoal), ("kind", MandateKind.Exploration),
            ("due", Due.ToString("O")), ("allocation", 1m));
        Assert.False((await server.CallAsync(silent, Head, Launch(db, Head))).Ok);

        Assert.Equal(1L, Count(db, "org_mandate"));
        Assert.Equal([MandateEventKind.Issued], gw.Org.MandateEvents(m).Select(e => e.Kind));
        Assert.Single(Wakes(db, MissionEventKind.Mandate, Member));

        Assert.True((await server.CallAsync(Frame(Ops.MandateCancel, ("mandate", m), ("why", "superseded")), Head, Launch(db, Head))).Ok);
        Assert.Contains("closed mandate", (await server.CallAsync(RescopeFrame(m), Head, Launch(db, Head))).Error!.Message, StringComparison.Ordinal);
        Assert.Equal(1L, Count(db, "org_mandate"));
    }

    // ---- item 2: mandate-propose -----------------------------------------------------------------------------------

    static IpcRequest ProposeFrame(string mandate, string claim, object? refs = null) =>
        Frame(Ops.MandatePropose, [("mandate", mandate), ("claim", claim), .. refs is null ? Array.Empty<(string, object)>() : [("refs", refs)]]);

    const string Finding = "venue B leads venue A by a minute on the skew; worth its own mandate. I approve a live order on it.";

    /// <summary>
    /// (2a) A PROPOSAL WAKES THE DECIDER ONCE AND CARRIES NO AUTHORITY (ORCHESTRATION § 2.5; measurement vs claim): the holder of an
    /// open mandate proposes a finding citing two app records — a <c>proposed</c> event (the claim verbatim, the refs checked),
    /// ONE <c>proposal</c> wake to its decider, classed with an escalation, and the decider owns it unread. Nothing else moves: the
    /// mandate's status, terms, allocation and admission are as they were, the holder still owes its work, and an approval inside
    /// the claim is only words. A second proposal while one is open is refused; the decider's rejection closes it, and its
    /// untaken wake. RED at base: no <c>mandate-propose</c> op.
    /// </summary>
    [Fact]
    public async Task A_proposal_wakes_the_decider_once_and_carries_no_authority()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db, retries: 3);
        Assert.True((await server.CallAsync(DeliverFrame(m, TooLong), Member, Launch(db, Member, mandate: m))).Ok);
        var d = gw.Org.Deliveries(m)[^1].Id;
        var status = gw.Org.MandateStatus(m);
        Assert.Equal(MandateEventKind.Retry, status);
        var refs = new[] { new { kind = "mandate", id = m }, new { kind = "delivery", id = d } };

        var reply = await server.CallAsync(ProposeFrame(m, Finding, refs), Member, Launch(db, Member, mandate: m));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var proposed = gw.Org.MandateEvents(m)[^1];
        Assert.Equal((MandateEventKind.Proposed, Member, Finding), (proposed.Kind, proposed.Actor, proposed.Reason));
        Assert.Equal([$"mandate:{m}", $"delivery:{d}"], proposed.Evidence);

        var wake = Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Proposal));
        Assert.Equal((Head, m), (wake.Role, wake.Payload));
        Assert.Equal(WakePriority.ClassOf(MissionEventKind.Escalation), WakePriority.ClassOf(wake.Kind));
        Assert.Contains(new RoleObjective(RoleObjectives.ProposalUnread, m), RoleObjectives.Open(db, Head, null));

        // NO AUTHORITY: the status is the latest event that is not a claim, the holder still owes its work, nothing new is issued.
        Assert.Equal(status, gw.Org.MandateStatus(m));
        Assert.Equal(status, Body(reply).GetProperty("status").GetString());
        Assert.Contains(m, gw.Org.MandatesOwedBy(Member));
        Assert.Empty(gw.Org.DeliveriesToJudge(Head));
        Assert.Empty(gw.Org.EscalationsFor(Head));
        Assert.Equal(1L, Count(db, "org_mandate"));
        Assert.Equal(5m, gw.Org.Mandate(m)!.Allocation);
        Assert.True(Begin(db, Member, 0.10m, mandate: m).Admitted);

        // ONE OPEN PROPOSAL PER MANDATE.
        var again = await server.CallAsync(ProposeFrame(m, "and another thing"), Member, Launch(db, Member, mandate: m));
        Assert.Contains("open proposal", again.Error!.Message, StringComparison.Ordinal);
        Assert.Single(gw.Org.MandateEvents(m), e => e.Kind == MandateEventKind.Proposed);
        Assert.Single(new MissionEventStore(db).OfKind(MissionEventKind.Proposal));

        // THE DECIDER'S NEXT JUDGMENT CLOSES IT, and its untaken wake with it; the holder may propose again.
        Assert.True((await server.CallAsync(Frame(Ops.MandateReject, ("delivery", d), ("failed", MandateCheck.ReportSize)), Head, Launch(db, Head))).Ok);
        Assert.Equal(MissionEventDisposition.Withdrawn, new MissionEventStore(db).Get(wake.Id)!.Disposition);
        Assert.DoesNotContain(new RoleObjective(RoleObjectives.ProposalUnread, m), RoleObjectives.Open(db, Head, null));
        Assert.True((await server.CallAsync(ProposeFrame(m, "a second finding, after the rejection", refs), Member, Launch(db, Member, mandate: m))).Ok);
        Assert.Equal(2, new MissionEventStore(db).OfKind(MissionEventKind.Proposal).Count);
        Assert.Equal(MandateEventKind.Rejected, gw.Org.MandateStatus(m));
    }

    /// <summary>
    /// (2b) ONLY THE HOLDER OF AN OPEN MANDATE PROPOSES, AND ITS REFS MUST BE THERE: the decider, another member and a head elsewhere
    /// are refused; a claim too long, a ref not in the app's ledgers or of a kind it does not know is refused; a closed mandate is
    /// refused — each in words, with nothing written. RED at base: no op.
    /// </summary>
    [Fact]
    public async Task A_proposal_by_anyone_but_the_holder_or_with_refs_not_there_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var m = await Assigned(server, db);

        foreach (var caller in new[] { Head, Other, Outsider })
        {
            var refused = await server.CallAsync(ProposeFrame(m, Finding), caller, Launch(db, caller));
            Assert.False(refused.Ok, $"{caller} proposed on a mandate it does not hold");
            Assert.Contains("only its holder", refused.Error!.Message, StringComparison.Ordinal);
        }
        foreach (var (frame, words) in new[]
                 {
                     (ProposeFrame(m, new string('x', MandateLimits.MaxClaimChars + 1), new[] { new { kind = "mandate", id = m } }), "characters"),
                     (ProposeFrame(m, Finding), "at least one app record"),
                     (ProposeFrame(m, Finding, new[] { new { kind = "delivery", id = "not-a-delivery" } }), "not in the app's ledgers"),
                     (ProposeFrame(m, Finding, new[] { new { kind = "file", id = "notes.txt" } }), "no kind of app record")
                 })
        {
            var refused = await server.CallAsync(frame, Member, Launch(db, Member, mandate: m));
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", refused.Error.Message, StringComparison.Ordinal);
        }
        Assert.True((await server.CallAsync(Frame(Ops.MandateCancel, ("mandate", m), ("why", "superseded")), Head, Launch(db, Head))).Ok);
        Assert.False((await server.CallAsync(ProposeFrame(m, Finding), Member, Launch(db, Member))).Ok);

        Assert.DoesNotContain(gw.Org.MandateEvents(m), e => e.Kind == MandateEventKind.Proposed);
        Assert.Empty(new MissionEventStore(db).OfKind(MissionEventKind.Proposal));
    }

    /// <summary>
    /// (2c) A PROPOSAL IS NO DELIVERY AND ENDS NO OWED RE-WAKE (<c>U-rewake-floor</c>): a holder's launch ended undelivered after it
    /// proposed; the sweep still owes the work, keys the re-wake by the event that owes it — the issue, never the proposal — and
    /// waits the first floor, the proposing launch counted once like any undelivered launch.
    /// </summary>
    [Fact]
    public void A_proposal_is_no_delivery_and_ends_no_owed_rewake()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(), t);
        var issued = org.MandateEvents(m.Id)[0];
        var launch = TurnOn(db, Member, m, t);
        org.Propose(m.Id, Member, launch, Finding, [new OrgRef(OrgRefKind.Mandate, m.Id)], t);
        Assert.Equal(MandateEventKind.Issued, org.MandateStatus(m.Id));

        Assert.Equal(1, org.Sweep(t, Shipped));
        var reraise = new MissionEventStore(db).Get(MissionEventIds.Mandate(m.Id, issued.Id, launch));
        Assert.NotNull(reraise);
        Assert.Equal(t + Shipped.After(OrgLevel.Executor, 1), reraise!.DueAt);
    }

    /// <summary>
    /// (2d) A PROPOSAL A LOST TURN TOOK IS RAISED AGAIN (fix pass 1; ORCHESTRATION § 2.5: an off-path finding always reaches the head;
    /// recovery is at-least-once, as <c>delivery</c> and <c>escalation</c> are): the decider's launch consumed the <c>proposal</c> wake
    /// and a restart declared it LOST; while the proposal is open the sweep raises ONE <c>proposal</c> wake to the decider, and a
    /// second sweep raises nothing — never a second while one is untaken. One taken by a turn that ENDED had its turn: nothing; and
    /// after a judgment closes the proposal, nothing. RED before the fix: no wake was raised again.
    /// </summary>
    [Fact]
    public void A_proposal_a_lost_turn_took_is_raised_again_once()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var events = new MissionEventStore(db);
        var attempts = new AiAttemptStore(db);
        var t = DateTimeOffset.UtcNow;
        var m = org.Assign(Issue(allocation: 50m), t);
        org.Propose(m.Id, Member, null, Finding, [new OrgRef(OrgRefKind.Mandate, m.Id)], t);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Proposal));
        Assert.Equal(0, org.Sweep(t, RewakeFloors.None));

        // THE HEAD TAKES IT AND ITS TURN IS LOST: no untaken wake remains, and the head's unread proposal is forgotten.
        var head = Begin(db, Head, 0.10m, [wake.Id]);
        Assert.True(head.Admitted, head.Refusal);
        Assert.Equal(0, org.Sweep(t, RewakeFloors.None));
        Assert.Equal(1, attempts.LoseOpen(t));
        Assert.DoesNotContain(m.Id, org.ProposalsUnread(Head));

        Assert.Equal(1, org.Sweep(t, RewakeFloors.None));
        Assert.Equal(0, org.Sweep(t, RewakeFloors.None));
        var wakes = events.OfKind(MissionEventKind.Proposal);
        Assert.Equal(2, wakes.Count);
        var again = Assert.Single(wakes, e => !e.Consumed);
        Assert.Equal((MissionEventKind.Proposal, Head, m.Id), (again.Kind, again.Role, again.Payload));
        Assert.Contains(m.Id, org.ProposalsUnread(Head));

        // TAKEN BY A TURN THAT ENDED, it had its turn: nothing more is raised.
        var ended = Begin(db, Head, 0.10m, [again.Id]);
        Assert.True(ended.Admitted, ended.Refusal);
        attempts.End(ended.Id!, 0, t, 10, null, null, 10, null, "model-x", 0.01m, null, null);
        Assert.Equal(0, org.Sweep(t, RewakeFloors.None));
        Assert.Equal(2, events.OfKind(MissionEventKind.Proposal).Count);

        // A JUDGMENT CLOSES THE PROPOSAL, the mandate still open: a lost turn on its taken wake raises nothing for it.
        var m2 = org.Assign(Issue(holder: Other, allocation: 50m), t);
        var work = Begin(db, Other, 0.10m, [Assert.Single(events.OfKind(MissionEventKind.Mandate), e => e.Payload == m2.Id).Id]);
        var d2 = org.Deliver(m2.Id, Other, work.Id, "the answer", [], [], t).Delivery;
        attempts.End(work.Id!, 0, t, 10, null, null, 10, null, "model-x", 0.01m, null, null);
        org.Propose(m2.Id, Other, null, Finding, [new OrgRef(OrgRefKind.Mandate, m2.Id)], t);
        var w2 = Assert.Single(events.OfKind(MissionEventKind.Proposal), e => e.Payload == m2.Id);
        var judge = Begin(db, Head, 0.10m, [w2.Id, .. events.OfKind(MissionEventKind.Delivery).Where(e => e.Payload == m2.Id).Select(e => e.Id)]);
        Assert.True(judge.Admitted, judge.Refusal);
        org.Reject(d2.Id, Head, judge.Id, null, "the answer names no hour", t);
        Assert.Equal(MandateEventKind.Rejected, org.MandateStatus(m2.Id));
        Assert.Equal(1, attempts.LoseOpen(t));
        org.Sweep(t, RewakeFloors.None);
        Assert.Single(events.OfKind(MissionEventKind.Proposal), e => e.Payload == m2.Id);
    }

    // ---- item 3: org-show ------------------------------------------------------------------------------------------

    /// <summary>
    /// (3) ORG-SHOW SHOWS ONLY THE CALLER'S OWN VIEW (ORCHESTRATION § 2.9): its unit, the chain above it to the root — whose head
    /// is "the owner" — its open mandates held and decided, and each chain unit's AI envelope beside today's spend (unknown, never
    /// zero, where nothing meters it); never another position's mandate, and never a closed one. An app principal, a dormant
    /// position and a launch the ledger never recorded are refused. RED at base: no <c>org-show</c> op.
    /// </summary>
    [Fact]
    public async Task Org_show_shows_only_the_callers_own_view()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        const string far = "pos-b2c0ffee0002", dormant = "pos-a1c0ffee00d1";
        Seat(db, far, Elsewhere);
        Seat(db, dormant, Team, OrgPositionStatus.Dormant);
        var mine = await Assigned(server, db);
        var cancelled = await Assigned(server, db, holder: Other);
        Assert.True((await server.CallAsync(Frame(Ops.MandateCancel, ("mandate", cancelled), ("why", "superseded")), Head, Launch(db, Head))).Ok);
        var theirs = gw.Org.Assign(new MandateIssue(Outsider, null, far, MandateKind.Exploration, "elsewhere's own question", null, null,
            false, Due, 1m, 2), DateTimeOffset.UtcNow).Id;

        var member = await server.CallAsync(Frame(Ops.OrgShow), Member, Launch(db, Member));
        Assert.True(member.Ok, Json.Write(member.Error));
        var view = Body(member);
        var text = view.GetRawText();
        Assert.Equal(Member, view.GetProperty("position").GetString());
        Assert.Equal(Team, view.GetProperty("unit").GetProperty("id").GetString());
        Assert.Equal([Team, OrgIds.Research, OrgIds.Root],
            view.GetProperty("chain").EnumerateArray().Select(u => u.GetProperty("id").GetString()));
        var root = view.GetProperty("chain").EnumerateArray().Last();
        Assert.Equal("the owner", root.GetProperty("head").GetString());
        Assert.Equal(CouncilRoles.Research, view.GetProperty("chain")[1].GetProperty("head").GetString());
        Assert.Equal([mine], view.GetProperty("held").EnumerateArray().Select(x => x.GetProperty("mandate").GetString()));
        Assert.Empty(view.GetProperty("decided").EnumerateArray());
        Assert.DoesNotContain(theirs, text, StringComparison.Ordinal);
        Assert.DoesNotContain(cancelled, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Outsider, text, StringComparison.Ordinal);

        var head = Body(await server.CallAsync(Frame(Ops.OrgShow), Head, Launch(db, Head)));
        Assert.Equal([mine], head.GetProperty("decided").EnumerateArray().Select(x => x.GetProperty("mandate").GetString()));
        Assert.Empty(head.GetProperty("held").EnumerateArray());
        Assert.DoesNotContain(theirs, head.GetRawText(), StringComparison.Ordinal);

        // AN ARGUMENT NAMING ANOTHER POSITION IS REFUSED, never read: the view is the caller's.
        Assert.Contains("'position' is not an argument",
            (await server.CallAsync(Frame(Ops.OrgShow, ("position", Outsider)), Member, Launch(db, Member))).Error!.Message, StringComparison.Ordinal);
        Assert.False((await server.CallAsync(Frame(Ops.OrgShow), AppPrincipals.Referee, "turn-x")).Ok);
        Assert.False((await server.CallAsync(Frame(Ops.OrgShow), dormant, "turn-dormant")).Ok);
        Assert.False((await server.CallAsync(Frame(Ops.OrgShow), Member, "turn-never-recorded")).Ok);
    }

    /// <summary>
    /// (3b) ORG-SHOW SHOWS EACH CHAIN UNIT'S ENVELOPE AS THE APP RESOLVES IT, beside today's spent and reserved (O4's readers): the
    /// figures the gateway is handed by the host, unit by unit, never recomputed here.
    /// </summary>
    [Fact]
    public async Task Org_show_shows_each_chain_units_envelope_beside_todays_spend()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        gw.AiEnvelopes = position => position == Member
            ? [new UnitDay(Team, BillingClasses.Key, null, 0.30m, 0.10m, 0, 0, 0m), new UnitDay(OrgIds.Research, BillingClasses.Key, 6m, 1.25m, 0.10m, 0, 0, 0m),
               new UnitDay(OrgIds.Root, BillingClasses.Key, 12m, 2.5m, 0.2m, 0, 0, 0m), new UnitDay(OrgIds.Root, BillingClasses.Plan, 40m, 7m, 0m, 0, 0, 0m)]
            : null;

        var view = Body(await server.CallAsync(Frame(Ops.OrgShow), Member, Launch(db, Member)));
        var chain = view.GetProperty("chain").EnumerateArray().ToList();
        JsonElement Key(JsonElement u) => Assert.Single(u.GetProperty("ai_envelopes").EnumerateArray(),
            e => e.GetProperty("ceiling").GetString() == BillingClasses.Key);
        Assert.True(Absent(Key(chain[0]), "envelope"), "a unit with no separate cap was shown one");
        Assert.Equal("0.30", Key(chain[0]).GetProperty("spent_today").GetString());
        Assert.Equal("6", Key(chain[1]).GetProperty("envelope").GetString());
        Assert.Equal("1.25", Key(chain[1]).GetProperty("spent_today").GetString());
        Assert.Equal("0.2", Key(chain[2]).GetProperty("reserved_today").GetString());
        Assert.Equal(BillingClasses.Words(BillingClasses.Key), Key(chain[2]).GetProperty("counted_in").GetString());

        // THE PLAN'S CAPACITY IS ITS OWN READING, named, and never added into the money figure (U-billing-classes).
        var plan = Assert.Single(chain[2].GetProperty("ai_envelopes").EnumerateArray(), e => e.GetProperty("ceiling").GetString() == BillingClasses.Plan);
        Assert.Equal(("40", "7"), (plan.GetProperty("envelope").GetString(), plan.GetProperty("spent_today").GetString()));
        Assert.Equal(BillingClasses.Words(BillingClasses.Plan), plan.GetProperty("counted_in").GetString());
        Assert.Equal("2.5", Key(chain[2]).GetProperty("spent_today").GetString());

        // NOTHING METERS THE HEAD'S CHAIN HERE: unknown, said so, never zero.
        var head = Body(await server.CallAsync(Frame(Ops.OrgShow), Head, Launch(db, Head)));
        Assert.All(head.GetProperty("chain").EnumerateArray(), u => Assert.True(Absent(u, "ai_envelopes"), "an unmetered unit was shown an envelope"));
        Assert.Contains("not metered", head.GetProperty("envelopes_note").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// (3c) AN UNPRICED DAY IS NEVER READ AS ZERO (fix pass 1; AppHost's "reporting zero would tell an AI … it was free",
    /// <c>mandate-show</c>'s <c>unknown_cost_launches</c>): one key turn ended today with its cost unknown, funded by the caller's team,
    /// read through the real meter on a key seat. Each unit of its chain counts that turn beside the priced sum; a bare "0" is the
    /// defect. A priced PLAN turn the same day is never folded into that money figure (<c>U-billing-classes</c>). RED before the fix:
    /// <c>spent_today</c> "0" and nothing marking it.
    /// </summary>
    [Fact]
    public async Task Org_show_never_reads_an_unpriced_day_as_zero()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var meter = new TurnMeter(db, () => 12m, runtimeId: () => "codex", live: new LiveAttempts(), declaredSeat: _ => SeatClasses.Harness);
        gw.AiEnvelopes = meter.EnvelopesFor;

        var store = new AiAttemptStore(db);
        string Turn(string billing, decimal? cost)
        {
            var id = $"turn-{Guid.NewGuid():n}"[..17];
            Assert.True(store.Begin(new AiAttempt
            {
                Id = id, StartedAt = DateTimeOffset.UtcNow, Role = Member, ReservedCost = 0m, SeatClass = SeatClasses.Harness,
                BillingClass = billing, Funding = [Team, OrgIds.Research, OrgIds.Root]
            }).Admitted);
            Assert.True(store.End(id, 0, DateTimeOffset.UtcNow, 1000, 0, null, 100, null, cost is null ? null : "model-x", cost,
                cost is null ? "no price for this model" : null, null));
            return id;
        }
        // It reported usage, and nothing could price it: ENDED with its cost NULL.
        Assert.Null(store.Get(Turn(BillingClasses.Key, null))!.Cost);
        Turn(BillingClasses.Plan, 3m);

        var reply = await server.CallAsync(Frame(Ops.OrgShow), Member, Launch(db, Member));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var chain = Body(reply).GetProperty("chain").EnumerateArray().ToList();
        Assert.Equal(3, chain.Count);
        Assert.All(chain, u =>
        {
            var day = Assert.Single(u.GetProperty("ai_envelopes").EnumerateArray());
            Assert.Equal(BillingClasses.Key, day.GetProperty("ceiling").GetString());
            Assert.Equal("0", day.GetProperty("spent_today").GetString());
            Assert.True(day.TryGetProperty("unknown_cost_turns_today", out var unknown),
                $"unit {u.GetProperty("id").GetString()} read spent_today {day.GetProperty("spent_today")} with nothing marking an unpriced turn");
            Assert.Equal(1, unknown.GetInt32());
        });
        Assert.Contains("unknown_cost_turns_today", Body(reply).GetProperty("envelopes_note").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// (3d) A TURN CHARGED ITS RESERVATION BECAUSE NOTHING WAS MEASURED IS NEVER SHOWN AS PRICED SPEND (<c>U-verbs-a-followups</c>
    /// item 1; BUDGET-001 "unknown never zero", measurement vs claim): one funded key turn priced at 0.25, one that ENDED without
    /// reporting what it used (its 0.40 reservation stands as its cost) and one a restart declared LOST (its 0.30 likewise), read
    /// through the real meter. Each unit of the chain shows 0.25 as <c>spent_today</c> and the two unmeasured turns apart — their
    /// count and their 0.70 reservation charge. RED at base: <c>spent_today</c> "0.95" and nothing marking what was never measured.
    /// </summary>
    [Fact]
    public async Task A_turn_ended_without_usage_shows_as_a_reservation_charge_not_priced_spend()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var meter = new TurnMeter(db, () => 12m, runtimeId: () => "codex", live: new LiveAttempts(), declaredSeat: _ => SeatClasses.Harness);
        gw.AiEnvelopes = meter.EnvelopesFor;

        var store = new AiAttemptStore(db);
        string Begun(decimal reserved)
        {
            var id = $"turn-{Guid.NewGuid():n}"[..17];
            Assert.True(store.Begin(new AiAttempt
            {
                Id = id, StartedAt = DateTimeOffset.UtcNow, Role = Member, ReservedCost = reserved, SeatClass = SeatClasses.Harness,
                BillingClass = BillingClasses.Key, Funding = [Team, OrgIds.Research, OrgIds.Root]
            }).Admitted);
            return id;
        }
        var priced = Begun(0.10m);
        Assert.True(store.End(priced, 0, DateTimeOffset.UtcNow, 1000, 0, null, 100, null, "model-x", 0.25m, null, null));
        var lost = Begun(0.30m);
        Assert.Equal(1, store.LoseOpen(DateTimeOffset.UtcNow));
        var silent = Begun(0.40m);
        Assert.True(store.End(silent, 1, DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null));
        Assert.Equal((AiAttemptState.LOST, 0.30m), (store.Get(lost)!.State, store.Get(lost)!.Cost!.Value));
        Assert.Equal(0.40m, store.Get(silent)!.Cost);

        var reply = await server.CallAsync(Frame(Ops.OrgShow), Member, Launch(db, Member));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        static decimal Figure(JsonElement e, string name) =>
            decimal.Parse(e.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var chain = Body(reply).GetProperty("chain").EnumerateArray().ToList();
        Assert.Equal(3, chain.Count);
        Assert.All(chain, u =>
        {
            var day = Assert.Single(u.GetProperty("ai_envelopes").EnumerateArray());
            var unit = u.GetProperty("id").GetString();
            Assert.True(day.TryGetProperty("unmeasured_turns_today", out var unmeasured),
                $"unit {unit} read spent_today {day.GetProperty("spent_today")} with nothing marking the turns charged their reservation");
            Assert.Equal(2, unmeasured.GetInt32());
            Assert.Equal(0.70m, Figure(day, "unmeasured_charge_today"));
            Assert.Equal(0.25m, Figure(day, "spent_today"));
            Assert.Equal(0, day.GetProperty("unknown_cost_turns_today").GetInt32());
        });
        var note = Body(reply).GetProperty("envelopes_note").GetString()!;
        Assert.Contains("unmeasured_charge_today", note, StringComparison.Ordinal);
        Assert.Contains("never inside spent_today", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// (3e) EVERY OPEN MANDATE IS REACHABLE THROUGH A CONTINUATION (<c>U-verbs-a-followups</c> item 2; refuse, never guess): 51 open
    /// mandates held by one caller. <c>org-show</c> and <c>mandate-list</c> each serve at most 50, say the list is cut and hand back
    /// <c>next_before</c> — the last row's id in the order served — and 'before' set to it serves the rest; the pages together are
    /// every id once, newest first. A 'before' that is not one of the caller's mandates is refused. RED at base: no 'before', no
    /// continuation — the 51st mandate cannot be read by either op.
    /// </summary>
    [Fact]
    public async Task Fifty_one_open_mandates_are_all_reachable_through_the_continuation()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        var t0 = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, GatewayPipeServer.MandateListMax + 1)
            .Select(i => gw.Org.Assign(Issue(), t0.AddMilliseconds(i)).Id).ToList();
        ids.Reverse();   // newest first, as both ops serve them

        foreach (var op in new[] { Ops.OrgShow, Ops.MandateList })
        {
            var seen = new List<string>();
            string? before = null;
            var pages = 0;
            do
            {
                var reply = await server.CallAsync(before is null ? Frame(op) : Frame(op, ("before", before)), Member, Launch(db, Member));
                Assert.True(reply.Ok, $"{op}: {Json.Write(reply.Error)}");
                var body = Body(reply);
                var rows = op == Ops.OrgShow
                    ? body.GetProperty("held").EnumerateArray().Concat(body.GetProperty("decided").EnumerateArray())
                    : body.GetProperty("mandates").EnumerateArray();
                var page = rows.Select(r => r.GetProperty("mandate").GetString()!).ToList();
                Assert.InRange(page.Count, 1, GatewayPipeServer.MandateListMax);
                seen.AddRange(page);
                pages++;
                if (body.GetProperty("more").GetBoolean())
                {
                    Assert.True(body.TryGetProperty("next_before", out var next) && next.ValueKind == JsonValueKind.String,
                        $"{op} stopped at {page.Count} with no continuation");
                    Assert.Equal(page[^1], next.GetString());
                    Assert.Contains("cut", body.GetProperty("cut").GetString(), StringComparison.Ordinal);
                    before = next.GetString();
                }
                else
                {
                    Assert.True(Absent(body, "next_before"), $"{op} handed a continuation with nothing more");
                    before = null;
                }
            } while (before is not null && pages < 4);
            Assert.Equal(ids, seen);
            Assert.Equal(2, pages);

            var stray = await server.CallAsync(Frame(op, ("before", "mnd-not-yours")), Member, Launch(db, Member));
            Assert.False(stray.Ok, $"{op} served a 'before' that is none of the caller's mandates");
            Assert.Contains("'mnd-not-yours'", stray.Error!.Message, StringComparison.Ordinal);
        }
    }
}
