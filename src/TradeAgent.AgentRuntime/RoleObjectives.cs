using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// ONE OPEN PIECE OF WORK A ROLE OWNS: which objective it is an instance of, and the entity it is about —
/// an owner message's event id, a request id, a boundary id, a position, a deployment.
/// </summary>
public sealed record RoleObjective(string Kind, string Entity);

/// <summary>
/// WHAT EACH ROLE IS FOR, AS THE APP RECORDS IT — the desired world of <c>docs/VISION.md</c> § 6.7, as app data
/// (<c>U-reconcile-wakes</c>).
///
/// <para><b>Kinds in code, instances derived at read.</b> The kinds are this class's constants, per role, and
/// versioned with the build as the canon is: <see cref="Version"/> rides every attempt's context, so two builds are
/// compared by what their attempts say, with no rung and no table. An instance is never stored: it is read from the
/// rows that open it — the wake queue, the request ledger, the boundary ledger, the deployments — so nothing is copied,
/// a restart loses nothing, and the only way to open one is for the app to have recorded the fact it is about.</para>
///
/// <para><b>Nothing an agent writes opens one.</b> No op, verb, argument, setting or file names an objective; this
/// class has no writer at all, and the one thing it is handed that is not a row — the chair's book — is the host's own
/// last read of the platform, never anything a role said about it. An objective says WHAT is wanted and never how
/// (<c>docs/PRINCIPLES.md</c>, "Autonomy is the default inside authority"): it decides only whether the app's own look
/// is worth paying for.</para>
///
/// <para><b>What it decides.</b> <c>MissionLoop.Schedule</c> raises a role's scheduled look only while it holds an
/// open instance, and closes a pending one when it holds none. Every real event still wakes the role it is for —
/// this is about the clock, not about the queue.</para>
/// </summary>
public static class RoleObjectives
{
    /// <summary>
    /// THE OBJECTIVES' VERSION, written into every attempt's context (<c>objectives_version</c>). Raised whenever a
    /// kind is added, removed or derived differently — the attempt that ran under the old meaning stays true about
    /// what it ran under.
    /// </summary>
    public const int Version = 3;

    /// <summary>Operations: every message the owner typed reaches a turn, and that turn's outcome is written.</summary>
    public const string OwnerAnswered = "owner-message-answered";

    /// <summary>
    /// Operations: every request in its book reaches a final state. A paper deployment's requests are the app's own
    /// run of a Research version and are not the chair's.
    /// </summary>
    public const string RequestFinal = "request-final";

    /// <summary>Operations: every position in its book is watched — and an unread book counts as one.</summary>
    public const string PositionWatched = "position-watched";

    /// <summary>Both: every consequential boundary open to the director is assessed by it.</summary>
    public const string BoundaryAssessed = "boundary-assessed";

    /// <summary>Research: every verdict the referee delivered on its versions is taken by a turn.</summary>
    public const string VerdictRead = "verdict-read";

    /// <summary>Research: every forward run of its versions is followed while it runs.</summary>
    public const string ForwardRunFollowed = "forward-run-followed";

    /// <summary>
    /// Research: its research mandate — hypotheses, data, backtests. STANDING, so always open: research is the work
    /// that has no event behind it, and its look keeps the decayed pace <c>U-quiet-review</c> gave it.
    /// </summary>
    public const string ResearchMandate = "research-mandate";

    /// <summary>
    /// A MANDATE THIS POSITION HOLDS AND OWES WORK ON (<c>U-org-assignments-a</c>): issued to it, retried by code or sent back by a
    /// rejection. Closed by its delivery, its decline, or its decider's cancellation; the entity is the mandate's id.
    /// </summary>
    public const string MandateOwed = "mandate-owed";

    /// <summary>A SEALED DELIVERY ON A MANDATE THIS POSITION DECIDES, waiting on its judgment (<c>mandate-accept</c> / <c>mandate-reject</c>).</summary>
    public const string DeliveryToJudge = "delivery-to-judge";

    /// <summary>A MANDATE THIS POSITION DECIDES THAT CODE ESCALATED: retries stopped, and the decision is its own.</summary>
    public const string EscalationOpen = "escalation-open";

    /// <summary>A MANDATE THIS POSITION DECIDES THAT PASSED ITS DEADLINE, until a turn of its own has been woken for it.</summary>
    public const string MandateExpired = "mandate-expired";

    /// <summary>The entity of <see cref="PositionWatched"/> when the host has no reading of the book to give.</summary>
    public const string UnreadBook = "the book has not been read";

    /// <summary>The request ledger's rows not in a final state, every non-terminal state of the machine named.</summary>
    static readonly string NotFinal = "execution_state IN ("
        + string.Join(",", Enum.GetValues<ExecutionState>().Where(s => !OrderStateMachine.IsTerminal(s)).Select(s => $"'{s}'"))
        + ")";

    /// <summary>
    /// The objective kinds a role holds, in this build (<c>U-org-assignments-a</c>, version 3): the legacy pair's, each by its
    /// exact id, unchanged; a HEAD after them (<paramref name="kind"/> as the chart derives it) owns the owner's words addressed to
    /// it (<c>U-org-wakes</c>), the mandates it holds, and — as a decider — its deliveries to judge, its escalations and its
    /// mandates past their deadline; a member or the staff owns the mandates it holds and nothing else — an executor's objective is
    /// its open mandate, never the app's clock (ORCHESTRATION § 2.5); a position whose kind is not given holds NONE.
    /// </summary>
    public static IReadOnlyList<string> KindsOf(string role, string? kind = null) => role switch
    {
        CouncilRoles.Operations => [OwnerAnswered, RequestFinal, PositionWatched, BoundaryAssessed],
        CouncilRoles.Research => [ResearchMandate, VerdictRead, ForwardRunFollowed, BoundaryAssessed],
        _ when kind == OrgPositionKind.Head => [OwnerAnswered, MandateOwed, DeliveryToJudge, EscalationOpen, MandateExpired],
        _ when kind is not null => [MandateOwed],
        _ => []
    };

    /// <summary>
    /// EVERY INSTANCE OPEN FOR <paramref name="role"/> NOW, read from the rows that open them.
    ///
    /// <para><paramref name="book"/> is the chair's positions as the host last read them from the platform — one
    /// line each — or null where it holds no reading (none taken yet, or the last one failed): an unknown book is not
    /// an empty one, so null opens <see cref="PositionWatched"/> rather than closing it. It is a read the host already
    /// made: this never calls the platform.</para>
    ///
    /// <para>It throws when a ledger cannot be read; the loop then treats the role as one the host cannot speak for
    /// and keeps its look, the direction that costs a look rather than work.</para>
    /// </summary>
    public static IReadOnlyList<RoleObjective> Open(Database db, string role, IReadOnlyList<string>? book)
    {
        var open = new List<RoleObjective>();
        var events = new MissionEventStore(db);

        switch (role)
        {
            case CouncilRoles.Operations:
                // OPEN WHILE A TURN CAN STILL CLOSE IT, not only until a launch took it (review 1, finding 2): a
                // message no turn has taken, and one a launch took that has no outcome yet while that launch is in
                // flight or was lost — the loop re-raises a lost turn's words to the chair's next turn, and that
                // turn's outcome closes both. Nothing is open that no turn can close.
                foreach (var e in events.OwnerWordsOpen(role))
                    open.Add(new RoleObjective(OwnerAnswered, e.Id));

                // EVERY STATE THE STATE MACHINE CAN STILL LEAVE, from the machine itself: a request written and
                // not yet dispatched, or waiting for the owner's approval, has not reached a final state either.
                // `ExecutionRequestStore.Open()` is what is at the broker, and leaves both out.
                var deployments = new Deployments(db);
                foreach (var r in new ExecutionRequestStore(db).Query(NotFinal))
                    if (deployments.OpById(r.RequestId) is null)
                        open.Add(new RoleObjective(RequestFinal, r.RequestId));

                if (book is null) open.Add(new RoleObjective(PositionWatched, UnreadBook));
                else foreach (var p in book) open.Add(new RoleObjective(PositionWatched, p));
                break;

            case CouncilRoles.Research:
                open.Add(new RoleObjective(ResearchMandate, role));
                foreach (var e in events.Pending(MissionEventKind.Verdict, role))
                    open.Add(new RoleObjective(VerdictRead, e.Id));
                foreach (var d in new Deployments(db).Open())
                    open.Add(new RoleObjective(ForwardRunFollowed, d.Id));
                break;

            default:
                // A POSITION AFTER THE LEGACY PAIR, by the kind the chart derives for it now (U-org-wakes): a head owns the
                // owner's words addressed to it, read as the chair's are; anyone else owns nothing here.
                var org = new OrgStore(db);
                var kinds = KindsOf(role, org.Standing(role)?.Kind);
                if (kinds.Contains(OwnerAnswered))
                    foreach (var e in events.OwnerWordsOpen(role))
                        open.Add(new RoleObjective(OwnerAnswered, e.Id));

                // AND THE MANDATE'S LIFE (U-org-assignments-a, v3): a member owns its open mandates; a head also its deliveries
                // to judge, its escalations and its mandates past their deadline — each read from the mandate's latest event.
                if (kinds.Contains(MandateOwed))
                    foreach (var m in org.MandatesOwedBy(role)) open.Add(new RoleObjective(MandateOwed, m));
                if (kinds.Contains(DeliveryToJudge))
                    foreach (var m in org.DeliveriesToJudge(role)) open.Add(new RoleObjective(DeliveryToJudge, m));
                if (kinds.Contains(EscalationOpen))
                    foreach (var m in org.EscalationsFor(role)) open.Add(new RoleObjective(EscalationOpen, m));
                if (kinds.Contains(MandateExpired))
                    foreach (var m in org.ExpiredUnread(role)) open.Add(new RoleObjective(MandateExpired, m));
                return open;
        }

        foreach (var b in new CouncilBoundaries(db).OpenFor(role))
            if (!b.Assessed) open.Add(new RoleObjective(BoundaryAssessed, b.Row.Id));

        return open;
    }
}
