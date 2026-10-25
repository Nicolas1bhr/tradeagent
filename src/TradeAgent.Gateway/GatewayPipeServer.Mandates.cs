using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// THE ORGANISATION'S VERBS ON THE PIPE (<c>U-org-assignments-a</c>; ORCHESTRATION §§ 2.2, 2.3, 2.9): the mandate's life,
/// through the broker. Every handler proves its caller with ONE helper (<see cref="OrgVerbCaller"/>) before anything is read
/// or written, and hands the rest to <see cref="OrgStore"/>, whose writers check the chart and the mandate row again.
/// </summary>
public sealed partial class GatewayPipeServer
{
    /// <summary>
    /// THE POSITION AN ORGANISATION VERB IS EXERCISED UNDER, OR A REFUSAL — the one gate every <c>mandate-*</c> op passes first.
    /// The caller is the ACTIVE POSITION its launch grant proved (<see cref="TradingGateway.ActivePositionOf"/>), and the launch
    /// is the one the ledger recorded under that position, on a seat whose RECORDED assurance (<c>ai_attempt.assurance</c>,
    /// written at admission from the seat the app verified) is at least <see cref="CapabilityPolicy.Minimum"/> of
    /// <paramref name="capability"/>. HEADSHIP ALONE GRANTS NOTHING: <c>research</c> heads <c>div-research</c> from an unconfined
    /// seat and is refused here, as <c>operations</c> is (§ 2.3). Refused with the <c>RefusalKind.Position</c> reading —
    /// it is what the caller is, not a passing condition of the host — and every refusal here is MARKED (<see cref="RefusalMark"/>),
    /// so the pipe records it as one, apart from a malformed frame that shares its code.
    /// </summary>
    string OrgVerbCaller(AgentContext ctx, Capability capability)
    {
        var minimum = CapabilityPolicy.Minimum(capability);
        var position = Gateway.ActivePositionOf(ctx.Role)
            ?? throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "an organisation verb is exercised by the active position whose launch asks it, and this connection proved none — "
                + "no launch grant, an app principal, or a position that is not active. Nothing was written."));

        var attempt = ctx.AttemptId is { } id ? Gateway.Attempt(id) : null;
        if (attempt is null || attempt.Role != position)
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{ctx.AttemptId}' is no launch the ledger recorded for {position}, so there is no seat to weigh this verb against. "
                + "Nothing was written."));

        var recorded = AssuranceNamed(attempt.Assurance);
        if (recorded is not { } assurance || assurance < minimum)
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"the organisation's verbs need a seat of at least {SeatClasses.Name(minimum)} assurance, and this launch of "
                + $"{position} was admitted on a {attempt.SeatClass ?? "unrecorded"} seat recorded "
                + $"{attempt.Assurance ?? "with no assurance"} (ORCHESTRATION § 2.3). Heading a unit grants no verb by itself. "
                + "Nothing was written."));
        return position;
    }

    /// <summary>The assurance a ledger row names, or null for a word this build does not know — refused, never guessed.</summary>
    static SeatAssurance? AssuranceNamed(string? name)
    {
        foreach (var a in Enum.GetValues<SeatAssurance>())
            if (SeatClasses.Name(a) == name) return a;
        return null;
    }

    /// <summary>
    /// ISSUES A MANDATE INTO THE CALLER'S OWN SUBTREE: the app mints the id and writes the row, its <c>issued</c> event and ONE
    /// <c>mandate</c> wake to the holder in one write — or refuses in words with nothing written (<see cref="OrgStore.Assign"/>).
    /// </summary>
    object MandateAssign(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var decider = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var retries = req.Args?.ContainsKey("retries") == true ? WholeNumber(req, "retries") : MandateLimits.DefaultRetryLimit;
        var allocation = req.Dec("allocation")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'allocation' is required: the most AI spend, over the mandate's whole life, its holder's launches may commit.");

        var row = Gateway.Org.Assign(new MandateIssue(
            decider, ctx.AttemptId, Require(req, "holder"), Require(req, "kind"), Require(req, "goal"), req.Str("scope"),
            req.Str("shape"), NamedFlag(req, "exact", whenAbsent: false), Due(req), allocation, retries), DateTimeOffset.UtcNow,
            ForecastClaims(req, "forecasts"));
        return MandateReply(row);
    }

    /// <summary>CANCELS AN OPEN MANDATE THE CALLER DECIDES, with a reason (<see cref="OrgStore.Cancel"/>).</summary>
    object MandateCancel(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var id = Require(req, "mandate");
        Gateway.Org.Cancel(id, caller, ctx.AttemptId, Require(req, "why"), DateTimeOffset.UtcNow);
        // THE CANCELLATION IS THE FENCE (U-org-verbs-b): committed above, so the app ends the holder's running turn now.
        Gateway.Fenced();
        return MandateReply(Gateway.Org.Mandate(id)!);
    }

    /// <summary>
    /// RE-SCOPES AN OPEN MANDATE THE CALLER DECIDES INTO A LINKED SUCCESSOR, saying why (<see cref="OrgStore.Rescope"/>): the new terms
    /// are held to every rule an assignment is; who decides and who holds it are the old row's, never the frame's. Answers the
    /// successor, naming the mandate it was re-scoped from.
    /// </summary>
    object MandateRescope(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var decider = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var retries = req.Args?.ContainsKey("retries") == true ? WholeNumber(req, "retries") : MandateLimits.DefaultRetryLimit;
        var allocation = req.Dec("allocation")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'allocation' is required: the most AI spend, over the successor's whole life, its holder's launches may commit. Nothing was written.");
        var (_, successor) = Gateway.Org.Rescope(Require(req, "mandate"), decider, ctx.AttemptId, Require(req, "why"),
            new MandateTerms(Require(req, "kind"), Require(req, "goal"), req.Str("scope"), req.Str("shape"),
                NamedFlag(req, "exact", whenAbsent: false), Due(req), allocation, retries), DateTimeOffset.UtcNow);
        // THE RE-SCOPE IS THE FENCE on the old mandate's running launch (U-org-verbs-b), committed above.
        Gateway.Fenced();
        return MandateReply(successor);
    }

    /// <summary>
    /// THE HOLDER HANDS ITS WORK BACK, SEALED (<see cref="OrgStore.Deliver"/>): the report, the deviations and the refs are its
    /// claims, stored verbatim; the seal, the checks and what code makes of them are TradeAgent's. A position's work reaches its
    /// issuer through this op and nothing else (RULING Q1): a file staged in <c>out/</c> stays held.
    /// </summary>
    object MandateDeliver(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var deviations = Claims(req, "deviations", ["what", "found", "cost"],
            o => new MandateDeviation(o["what"], o["found"], o["cost"]));
        var refs = Claims(req, "refs", ["kind", "id"], o => new MandateRef(o["kind"], o["id"]));
        var result = Gateway.Org.Deliver(Require(req, "mandate"), caller, ctx.AttemptId, Require(req, "report"), deviations, refs,
            DateTimeOffset.UtcNow, LensOrigin);
        return new
        {
            delivery = result.Delivery.Id,
            mandate = result.Delivery.MandateId,
            status = result.Status,
            checks = result.Checks.Select(c => new { check = c.Check, passed = c.Passed, reason = c.Reason }),
            note = "Sealed: the id is the SHA-256 of what you handed back, and nothing edits it — a later delivery is a new one. "
                   + "The checks are TradeAgent's; your report, deviations and refs are your claims, stored as written."
        };
    }

    /// <summary>
    /// THE HOLDER PROPOSES A FINDING BEYOND ITS MANDATE TO ITS DECIDER (<see cref="OrgStore.Propose"/>): its claim, stored verbatim,
    /// the app records it cites, each checked, and ONE wake to the decider. It grants nothing: the mandate's status, terms and
    /// allocation are as they were, and the answer says so.
    /// </summary>
    object MandatePropose(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var id = Require(req, "mandate");
        var proposed = Gateway.Org.Propose(id, caller, ctx.AttemptId, Require(req, "claim"), OrgRefs(req), DateTimeOffset.UtcNow);
        var m = Gateway.Org.Mandate(id)!;
        return new
        {
            mandate = m.Id,
            status = Gateway.Org.MandateStatus(m.Id),
            proposal = proposed.Id,
            woken = m.Decider,
            note = "Recorded as YOUR claim, with the records you cited checked to be there, and your decider is woken once to read it. "
                   + "It changes nothing about this mandate — its status, terms and allocation are as they were, and you still owe its "
                   + "work. One proposal is open at a time, until your decider's next judgment."
        };
    }

    /// <summary>THE HOLDER DECLINES AN OPEN MANDATE, infeasible or ambiguous, saying why (<see cref="OrgStore.Decline"/>).</summary>
    object MandateDecline(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var id = Require(req, "mandate");
        Gateway.Org.Decline(id, caller, ctx.AttemptId, Require(req, "reason"), Require(req, "why"), DateTimeOffset.UtcNow);
        return MandateReply(Gateway.Org.Mandate(id)!);
    }

    /// <summary>THE DECIDER ACCEPTS THE EXACT DELIVERY IT WAS SHOWN — the latest sealed one, by id (<see cref="OrgStore.Accept"/>).</summary>
    object MandateAccept(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var delivery = Require(req, "delivery");
        Gateway.Org.Accept(delivery, caller, ctx.AttemptId, DateTimeOffset.UtcNow);
        return MandateReply(Gateway.Org.Mandate(Gateway.Org.Delivery(delivery)!.MandateId)!);
    }

    /// <summary>
    /// THE DECIDER REJECTS THE LATEST DELIVERY, citing a check of it that failed or a defect it states (<see cref="OrgStore.Reject"/>);
    /// the holder is woken to work on it again.
    /// </summary>
    object MandateReject(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var delivery = Require(req, "delivery");
        Gateway.Org.Reject(delivery, caller, ctx.AttemptId, req.Str("failed"), req.Str("defect"), DateTimeOffset.UtcNow);
        return MandateReply(Gateway.Org.Mandate(Gateway.Org.Delivery(delivery)!.MandateId)!);
    }

    /// <summary>The most mandates one <c>mandate-list</c> answers.</summary>
    public const int MandateListMax = 50;

    /// <summary>
    /// THE CALLER'S OWN MANDATES, NEWEST FIRST — those it holds and those it decides, never another position's: each with its status
    /// (its latest event), the issuer's objective as written, and the app's measured spend. Bounded twice and never silently: at
    /// most <see cref="MandateListMax"/> and <see cref="ResearchLedger.MaxReadBytes"/> as the wire writes them; 'more' says it
    /// stopped.
    /// </summary>
    object MandateList(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.Reads);
        var served = new List<object>();
        // THE FRAME FIRST, at its widest, and a comma between two rows: the WHOLE answer is bounded, not its rows alone.
        long spent = ReplyBytes(ListAnswer([], "bytes"));
        string? cappedBy = null;
        foreach (var m in Gateway.Org.MandatesOf(caller))
        {
            if (served.Count == MandateListMax) { cappedBy = "count"; break; }
            var row = new
            {
                mandate = m.Id,
                status = Gateway.Org.MandateStatus(m.Id),
                you_are = m.Holder == caller ? "holder" : "decider",
                holder = m.Holder,
                decider = m.Decider,
                kind = m.Kind,
                goal = m.Objective,
                deadline = m.Deadline,
                allocation = m.Allocation.ToString(CultureInfo.InvariantCulture),
                measured_spend = Measured(Gateway.Org.SpendOf(m.Id))
            };
            var cost = ReplyBytes(row) + (served.Count > 0 ? 1 : 0);
            if (served.Count > 0 && spent + cost > ResearchLedger.MaxReadBytes) { cappedBy = "bytes"; break; }
            spent += cost;
            served.Add(row);
        }
        return ListAnswer(served, cappedBy);
    }

    static object ListAnswer(List<object> served, string? cappedBy) => new
    {
        mandates = served,
        more = cappedBy is not null,
        capped_by = cappedBy,
        note = "Each goal is its issuer's words; the status, the allocation and the measured spend are TradeAgent's records."
    };

    /// <summary>
    /// THE CALLER'S OWN PLACE ON THE CHART (<c>org-show</c>, <c>U-org-verbs-a</c>; ORCHESTRATION § 2.9) — and nobody else's: its unit,
    /// the chain of units above it to the root, whose head is the owner and is named so (the chain is the caller's own line, so no
    /// watcher or audit line is in it), each unit's head, and each unit's AI envelope beside today's spent and reserved as the
    /// host that meters launches resolves them (<see cref="TradingGateway.AiEnvelopes"/>; not metered is said, never zero); and
    /// its OPEN mandates, held and decided, newest first, each with its status and measured spend — read from
    /// <see cref="OrgStore"/>'s own reads (<see cref="OrgStore.UnitOf"/>, <see cref="OrgStore.Ancestors"/>,
    /// <see cref="OrgStore.MandatesOf"/>, <see cref="OrgStore.SpendOf"/>), never a packet. Bounded by
    /// <see cref="MandateListMax"/> mandates and <see cref="ResearchLedger.MaxReadBytes"/> as the wire writes it; 'more' says
    /// it stopped, and <c>mandate-list</c> reads them all.
    /// </summary>
    object OrgShow(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, "org-show reads YOUR place on the chart and nothing else: it takes no argument, and names no other "
                          + "position's. Nothing was read.");
        var caller = OrgVerbCaller(ctx, Capability.Reads);
        var org = Gateway.Org;
        var unit = org.UnitOf(caller)
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{caller}' belongs to no unit the chart holds, so it has no place to show.");
        IReadOnlyList<OrgUnitRow> chain = [unit, .. org.Ancestors(unit.Id)];

        IReadOnlyList<UnitDay>? days;
        try { days = Gateway.AiEnvelopes?.Invoke(caller); }
        catch (Exception) { days = null; }
        // ONE READING PER CEILING THE CALLER'S SEAT IS HELD TO (U-billing-classes): key is real money, plan is the plan's capacity and
        // never money, and a seat whose class is not known is held to both — so each reading names its ceiling and none is summed.
        var byUnit = days?.ToLookup(d => d.UnitId, StringComparer.Ordinal);
        var units = chain.Select(u => (object)new
        {
            id = u.Id,
            kind = u.Kind,
            name = OrgEnvelopes.NameOf(u.Id),
            head = u.IsRoot ? "the owner" : u.HeadPositionId ?? "vacant",
            // ALL THREE DIMENSIONS OF ITS ENVELOPE (U-org-verbs-e), as the row holds them: absent is no own cap.
            shares = new { ai = u.EnvelopeShare, trials = u.TrialShare, verdicts = u.VerdictShare },
            ai_envelopes = byUnit is not null && byUnit.Contains(u.Id)
                ? byUnit[u.Id].Select(d => new
                {
                    ceiling = d.Ceiling,
                    counted_in = BillingClasses.Words(d.Ceiling),
                    envelope = d.Envelope?.ToString(CultureInfo.InvariantCulture),
                    spent_today = d.Spent.ToString(CultureInfo.InvariantCulture),
                    // A TURN WHOSE COST IS UNKNOWN IS NOT FREE: spent_today adds it as nothing, so it is counted beside the
                    // priced sum, as mandate-show's unknown_cost_launches is — never a bare zero (AppHost's rule).
                    unknown_cost_turns_today = d.Unpriced,
                    // A TURN NOTHING MEASURED IS CHARGED ITS RESERVATION (U-verbs-a-followups; BUDGET-001): a turn that ended
                    // without reporting what it used, or that a restart declared LOST — its count and its charge, apart from
                    // spent_today and never inside it, so a reservation never reads as a priced bill.
                    unmeasured_turns_today = d.Unmeasured,
                    unmeasured_charge_today = d.UnmeasuredCharge.ToString(CultureInfo.InvariantCulture),
                    reserved_today = d.Reserved.ToString(CultureInfo.InvariantCulture)
                }).ToList()
                : null
        }).ToList();
        var note = byUnit is null
            ? "'shares' are each unit's own fractions, as the chart holds them (absent: no own cap): 'ai' of its parent's AI envelope, "
              + "'trials' and 'verdicts' of a campaign's own research-trial and final-judgement budgets. Envelopes are not metered here: "
              + "nothing gave a reading of this chain's envelopes, so each is unknown — never zero."
            : "'shares' are each unit's own fractions, as the chart holds them (absent: no own cap): 'ai' of its parent's AI envelope, "
              + "'trials' and 'verdicts' of a campaign's own research-trial and final-judgement budgets, charged to the chain each launch "
              + "was admitted under. Each envelope is TradeAgent's, resolved as admission resolves it (none: no separate cap — the units above bind); spent "
              + "and reserved are today's, charged to the unit's funding chain. Each reading is counted in ONE ceiling, named: 'key' is "
              + "real money, 'plan' is your ChatGPT plan's capacity and never money; a seat whose class is not known is shown against "
              + "both, as admission holds it to both. The two are never added. spent_today is the PRICED sum of that ceiling's "
              + "turns: a turn whose cost is unknown adds nothing to it and is counted in unknown_cost_turns_today, so a day with "
              + "any is unknown, never zero. A turn that ended without reporting what it used, or that a restart declared lost, was "
              + "charged its reservation because nothing measured it: those turns are counted in unmeasured_turns_today and their "
              + "charge is unmeasured_charge_today — a charge, not a price, and never inside spent_today. What admission counts "
              + "against the envelope is spent_today, unmeasured_charge_today and reserved_today together.";

        var held = new List<object>();
        var decided = new List<object>();
        var open = org.MandatesOf(caller).Where(m => org.IsOpen(m.Id)).ToList();
        long spent = ReplyBytes(ShowOrg(caller, units, held, decided, true, note)) + 64;
        var more = false;
        foreach (var m in open)
        {
            if (held.Count + decided.Count == MandateListMax) { more = true; break; }
            var mine = m.Holder == caller;
            var row = new
            {
                mandate = m.Id,
                status = org.MandateStatus(m.Id),
                holder = m.Holder,
                decider = m.Decider,
                kind = m.Kind,
                deadline = m.Deadline,
                allocation = m.Allocation.ToString(CultureInfo.InvariantCulture),
                measured_spend = Measured(org.SpendOf(m.Id))
            };
            var cost = ReplyBytes(row) + 1;
            if (spent + cost > ResearchLedger.MaxReadBytes) { more = true; break; }
            spent += cost;
            (mine ? held : decided).Add(row);
        }
        return ShowOrg(caller, units, held, decided, more, note);
    }

    static object ShowOrg(string caller, List<object> chain, List<object> held, List<object> decided, bool more, string envelopes) => new
    {
        position = caller,
        unit = chain[0],
        chain,
        held,
        decided,
        more,
        envelopes_note = envelopes,
        note = "Your own place on the chart: your unit, the line above it to the owner, and your open mandates. The chart, the "
               + "statuses, the envelopes and the spend are TradeAgent's records; a goal is its issuer's words — 'mandate-show' reads one."
    };

    /// <summary>What <c>mandate-show</c>'s <c>part</c> names: its deliveries (the default), its events or its forecasts.</summary>
    public const string ShowDeliveries = "deliveries", ShowEvents = "events", ShowForecasts = "forecasts";

    /// <summary>
    /// ONE MANDATE THE CALLER HOLDS OR DECIDES, IN FULL: as issued, its sealed deliveries newest first — each verbatim, each check
    /// TradeAgent ran on it, and each deviation's STATED cost (the holder's claim) shown BESIDE the mandate's MEASURED spend (the
    /// app's), never instead of it — its events newest first, and its forecasts newest first, each probability (the decider's claim)
    /// shown BESIDE the app's resolution and the record it rests on, never instead (<c>U-org-decisions</c> fix pass 1, item 3; only
    /// to a caller that may already read the mandate). THE WHOLE ANSWER IS BOUNDED by
    /// <see cref="ResearchLedger.MaxReadBytes"/> as the wire writes it, escaping included (fix pass 1, item 3): the frame at its
    /// longest, then whole rows — a delivery or an event is never cut — and what it left out is said, never silent: each list's
    /// total, whether more is left and the cursor that continues it, and <c>left_out</c> in words. With no <c>part</c> it serves
    /// the newest deliveries, then as many of the newest events, then of the newest forecasts, as still fit; <c>part</c> =
    /// <c>deliveries</c>, <c>events</c> or <c>forecasts</c> pages ONE list from <c>before</c>. The first row of the list asked for is always served and always fits: a
    /// delivery is at most <see cref="MandateLimits.MaxDeliveryBytes"/> at its seal, and the frame at most its fields' caps
    /// (<c>OrgAssignmentTests.A_mandate_show_is_bounded_whole_and_says_what_it_left_out</c> measures both at their widest).
    /// </summary>
    object MandateShow(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.Reads);
        var id = Require(req, "mandate");
        var m = Gateway.Org.Mandate(id);
        if (m is null || (m.Holder != caller && m.Decider != caller))
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{id}' is no mandate you hold or decide: a position reads its own mandates and nobody else's."));
        var part = req.Str("part");
        if (part is not null and not ShowDeliveries and not ShowEvents and not ShowForecasts)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'part' is '{ShowDeliveries}', '{ShowEvents}' or '{ShowForecasts}' — '{part}' is none of them. Omit it to read the newest of each.");
        var before = req.Str("before");
        if (before is not null && part is null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'before' continues one list: name it with 'part' ('{ShowDeliveries}', '{ShowEvents}' or '{ShowForecasts}'), as an answer's 'left_out' says.");

        var measured = Measured(Gateway.Org.SpendOf(m.Id));
        // THE LINEAGE (U-org-verbs-a): the mandate this one was re-scoped from, with ITS measured spend — the predecessor's cost
        // stays its own and is shown beside this one, never folded in — and the one this was re-scoped into.
        var predecessor = Gateway.Org.PredecessorOf(m.Id) is { } p && Gateway.Org.Mandate(p) is { } pm
            ? new { mandate = pm.Id, status = Gateway.Org.MandateStatus(pm.Id), allocation = pm.Allocation.ToString(CultureInfo.InvariantCulture),
                    measured_spend = Measured(Gateway.Org.SpendOf(pm.Id)) }
            : null;
        var successor = Gateway.Org.SuccessorOf(m.Id);
        // THE LAUNCHES ITS CANCELLATION OR RE-SCOPE FENCED (U-org-verbs-b): refused from that instant; each turn 'ended' only once
        // its row is closed, and 'ending' until then — the fence's end is asynchronous, and a claim of it before the ledger has it is
        // not a measurement (fix pass 1, Codex P2).
        var fenced = Gateway.Org.FencedBy(m.Id).Select(f => (object)new { attempt = f.Attempt, turn = f.Ended ? "ended" : "ending" }).ToList();
        var deliveries = Gateway.Org.Deliveries(m.Id).Reverse().ToList();
        var events = Gateway.Org.MandateEvents(m.Id).Reverse().ToList();
        var forecasts = Gateway.Org.ForecastsOn(m.Id).Reverse().ToList();
        var dFrom = 0;
        var eFrom = 0;
        var fFrom = 0;
        if (before is not null && part == ShowDeliveries)
        {
            var at = deliveries.FindIndex(d => d.Id == before);
            if (at < 0)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{before}' is no delivery of {m.Id}: pass a 'deliveries_next_before' an answer gave.");
            dFrom = at + 1;
        }
        if (before is not null && part == ShowEvents)
        {
            var at = long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
                ? events.FindIndex(e => e.Id == eventId) : -1;
            if (at < 0)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{before}' is no event of {m.Id}: pass an 'events_next_before' an answer gave.");
            eFrom = at + 1;
        }
        if (before is not null && part == ShowForecasts)
        {
            var at = forecasts.FindIndex(f => f.Id == before);
            if (at < 0)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{before}' is no forecast on {m.Id}: pass a 'forecasts_next_before' an answer gave.");
            fFrom = at + 1;
        }

        // THE FRAME FIRST, AT THE MOST ITS OWN FIELDS CAN COST once the lists are in: every count and cursor at its widest.
        var widest = new string('f', 64);
        long spent = ReplyBytes(Show(m, measured, predecessor, successor, fenced, [], [], [], int.MaxValue, int.MaxValue, int.MaxValue, true, widest, true, long.MaxValue,
            true, widest, LeftOut(m.Id, int.MaxValue, widest, int.MaxValue, long.MaxValue, int.MaxValue, widest)));

        var servedD = new List<object>();
        string? lastD = null;
        if (part is null or ShowDeliveries)
            foreach (var d in deliveries.Skip(dFrom))
            {
                var row = DeliveryShown(d, measured);
                var cost = ReplyBytes(row) + (servedD.Count > 0 ? 1 : 0);
                // NEVER MORE THAN THE BOUND, the first row included: the seal's cap is what makes the first always fit.
                if (spent + cost > ResearchLedger.MaxReadBytes) break;
                spent += cost;
                servedD.Add(row);
                lastD = d.Id;
            }

        var servedE = new List<object>();
        long? lastE = null;
        if (part is null or ShowEvents)
            foreach (var e in events.Skip(eFrom))
            {
                var row = new { id = e.Id, at = e.At, kind = e.Kind, actor = e.Actor, attempt = e.Attempt, reason = e.Reason, evidence = e.Evidence };
                var cost = ReplyBytes(row) + (servedE.Count > 0 ? 1 : 0);
                if (spent + cost > ResearchLedger.MaxReadBytes) break;
                spent += cost;
                servedE.Add(row);
                lastE = e.Id;
            }

        var servedF = new List<object>();
        string? lastF = null;
        if (part is null or ShowForecasts)
            foreach (var f in forecasts.Skip(fFrom))
            {
                var row = ForecastShown(f);
                var cost = ReplyBytes(row) + (servedF.Count > 0 ? 1 : 0);
                if (spent + cost > ResearchLedger.MaxReadBytes) break;
                spent += cost;
                servedF.Add(row);
                lastF = f.Id;
            }

        // WHAT IS LEFT, PER LIST: a list this call did not page is left whole, from its newest.
        var dLeft = part is ShowEvents or ShowForecasts ? deliveries.Count : deliveries.Count - dFrom - servedD.Count;
        var eLeft = part is ShowDeliveries or ShowForecasts ? events.Count : events.Count - eFrom - servedE.Count;
        var fLeft = part is ShowDeliveries or ShowEvents ? forecasts.Count : forecasts.Count - fFrom - servedF.Count;
        var dNext = part is ShowEvents or ShowForecasts || dLeft == 0 ? null : lastD;
        var eNext = part is ShowDeliveries or ShowForecasts || eLeft == 0 ? null : lastE;
        var fNext = part is ShowDeliveries or ShowEvents || fLeft == 0 ? null : lastF;
        return Show(m, measured, predecessor, successor, fenced, servedD, servedE, servedF, deliveries.Count, events.Count, forecasts.Count, dLeft > 0, dNext, eLeft > 0, eNext,
            fLeft > 0, fNext, dLeft == 0 && eLeft == 0 && fLeft == 0 ? null : LeftOut(m.Id, dLeft, dNext, eLeft, eNext, fLeft, fNext));
    }

    /// <summary>The answer of <c>mandate-show</c>, in one place, so the frame measured is the frame sent.</summary>
    object Show(MandateRow m, object measured, object? predecessor, string? successor, IReadOnlyList<object> fenced, List<object> deliveries, List<object> events, List<object> forecasts, int deliveriesTotal,
        int eventsTotal, int forecastsTotal, bool deliveriesMore, string? deliveriesNext, bool eventsMore, long? eventsNext,
        bool forecastsMore, string? forecastsNext, string? leftOut) => new
    {
        mandate = MandateReply(m),
        goal = m.Objective,
        scope = m.Scope,
        measured_spend = measured,
        predecessor,
        successor,
        fenced,
        deliveries,
        deliveries_total = deliveriesTotal,
        deliveries_more = deliveriesMore,
        deliveries_next_before = deliveriesNext,
        events,
        events_total = eventsTotal,
        events_more = eventsMore,
        events_next_before = eventsNext?.ToString(CultureInfo.InvariantCulture),
        forecasts,
        forecasts_total = forecastsTotal,
        forecasts_more = forecastsMore,
        forecasts_next_before = forecastsNext,
        left_out = leftOut,
        note = "A delivery's report, deviations and refs are its holder's CLAIMS, stored as written; a deviation's "
               + "'stated_cost' is the holder's word, and 'measured_spend' beside it is TradeAgent's, from the launches charged "
               + "to this mandate. The checks, the events and the seal are TradeAgent's records. A forecast's probability is its "
               + "forecaster's claim; whether it is scored, and its resolution beside it, are TradeAgent's. A 'predecessor' is the "
               + "mandate this one was re-scoped from, with its own measured spend, which stays its own. 'fenced' names the launches its "
               + "cancellation or re-scope took authority from mid-turn, each refused from then on; its 'turn' is 'ended' once the launch "
               + "is closed in TradeAgent's ledger, and 'ending' while it is still open, its end not yet recorded."
    };

    /// <summary>What a bounded answer left out, in words naming each list's rest and the call that reads it.</summary>
    static string LeftOut(string mandate, int deliveries, string? deliveriesNext, int events, long? eventsNext, int forecasts, string? forecastsNext)
    {
        var parts = new List<string>();
        if (deliveries > 0)
            parts.Add($"{deliveries.ToString(CultureInfo.InvariantCulture)} more deliver{(deliveries == 1 ? "y" : "ies")} — read with "
                      + $"part '{ShowDeliveries}'{(deliveriesNext is null ? "" : $" and before '{deliveriesNext}'")}");
        if (events > 0)
            parts.Add($"{events.ToString(CultureInfo.InvariantCulture)} more event{(events == 1 ? "" : "s")} — read with part '{ShowEvents}'"
                      + (eventsNext is null ? "" : $" and before '{eventsNext.Value.ToString(CultureInfo.InvariantCulture)}'"));
        if (forecasts > 0)
            parts.Add($"{forecasts.ToString(CultureInfo.InvariantCulture)} more forecast{(forecasts == 1 ? "" : "s")} — read with part '{ShowForecasts}'"
                      + (forecastsNext is null ? "" : $" and before '{forecastsNext}'"));
        return $"This answer is bounded at {ResearchLedger.MaxReadBytes:N0} bytes and left out of {mandate}: {string.Join("; ", parts)}.";
    }

    /// <summary>One sealed delivery as <c>mandate-show</c> serves it: verbatim, its checks, each stated cost beside the measured spend.</summary>
    object DeliveryShown(DeliveryRow d, object measured) => new
    {
        delivery = d.Id,
        at = d.At,
        attempt = d.Attempt,
        report = d.Report,
        deviations = d.Deviations.Select(v => new
        {
            what = v.What,
            found = v.Found,
            stated_cost = v.Cost,
            measured_spend = measured
        }),
        refs = d.Refs.Select(r => new { kind = r.Kind, id = r.Id }),
        checks = Gateway.Org.Checks(d.Id).Select(c => new { check = c.Check, passed = c.Passed, reason = c.Reason })
    };

    /// <summary>A mandate's measured spend as a read says it: resolved cost, open reservations, and how many launches' cost is unknown.</summary>
    static object Measured(MandateSpend s) => new
    {
        spent = s.Spent.ToString(CultureInfo.InvariantCulture),
        reserved = s.Reserved.ToString(CultureInfo.InvariantCulture),
        unknown_cost_launches = s.Unknown,
        launches = s.Launches
    };

    /// <summary>The launch that registered lens <paramref name="id"/> at <paramref name="version"/>, read from the tape's registry, or null.</summary>
    LensOrigin? LensOrigin(string id, int version) =>
        Gateway.Tape?.Lens(id, version) is { } lens && lens.Version == version ? new LensOrigin(lens.Attempt) : null;

    /// <summary>
    /// A LIST OF CLAIMS THE FRAME CARRIES, AS JSON OBJECTS WITH EXACTLY <paramref name="keys"/>, each a string: absent is none; a
    /// JSON array or a string holding one is read; anything else — another shape, a key it does not take, a missing one — is
    /// refused, never cut or filled in.
    /// </summary>
    static IReadOnlyList<T> Claims<T>(IpcRequest req, string arg, string[] keys, Func<IReadOnlyDictionary<string, string>, T> make)
    {
        if (req.Args is null || !req.Args.TryGetValue(arg, out var raw) || raw.ValueKind == System.Text.Json.JsonValueKind.Null) return [];
        System.Text.Json.JsonElement list;
        try
        {
            list = raw.ValueKind == System.Text.Json.JsonValueKind.String
                ? System.Text.Json.JsonDocument.Parse(raw.GetString() ?? "[]").RootElement.Clone()
                : raw;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{arg}' is not JSON. {Shape()}");
        }
        if (list.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{arg}' is not a list. {Shape()}");

        var items = new List<T>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"an item of '{arg}' is not an object. {Shape()}");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in item.EnumerateObject())
            {
                if (Array.IndexOf(keys, p.Name) < 0 || p.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{p.Name}' is not a text field of an item of '{arg}'. {Shape()}");
                fields[p.Name] = p.Value.GetString()!;
            }
            if (keys.FirstOrDefault(k => !fields.ContainsKey(k)) is { } missing)
                throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"an item of '{arg}' has no '{missing}'. {Shape()}");
            items.Add(make(fields));
        }
        return items;

        string Shape() => $"'{arg}' is a JSON list of objects with exactly {string.Join(", ", keys.Select(k => $"'{k}'"))}, each text. Nothing was written.";
    }

    /// <summary>What an organisation verb's refusal of an argument it does not take says.</summary>
    const string MandateArgsWhy = "A mandate's record is the app's: its id, its events, its seal and its checks are written by "
                                  + "TradeAgent and by nothing a frame carries. Nothing was written.";

    /// <summary>The mandate as the app recorded it, and its status — its latest event.</summary>
    object MandateReply(MandateRow m) => new
    {
        mandate = m.Id,
        status = Gateway.Org.MandateStatus(m.Id),
        rescoped_from = Gateway.Org.PredecessorOf(m.Id),
        rescoped_into = Gateway.Org.SuccessorOf(m.Id),
        unit = m.UnitId,
        decider = m.Decider,
        holder = m.Holder,
        kind = m.Kind,
        shape = m.OutputShape,
        latitude = m.Latitude,
        checks = m.Checks,
        deadline = m.Deadline,
        allocation = m.Allocation.ToString(CultureInfo.InvariantCulture),
        retry_limit = m.RetryLimit,
        note = "The objective and scope are the issuer's words, stored as written; the id, the status, the checks and the "
               + "spend are TradeAgent's records."
    };

    /// <summary>The deadline as the frame names it: an ISO-8601 time, read as UTC when it names no offset; unreadable is refused.</summary>
    static DateTimeOffset Due(IpcRequest req)
    {
        var raw = Require(req, "due");
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var due))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'due' is the mandate's deadline as an ISO-8601 time, such as 2026-10-20T18:00:00Z — '{raw}' is not one. Nothing was written.");
        return due;
    }

    /// <summary>A whole number the frame names, or a refusal.</summary>
    static int WholeNumber(IpcRequest req, string key)
    {
        var raw = req.Dec(key)!.Value;
        if (raw != decimal.Truncate(raw) || raw < int.MinValue || raw > int.MaxValue)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{key}' is a whole number — '{raw.ToString(CultureInfo.InvariantCulture)}' is not one. Nothing was written.");
        return (int)raw;
    }
}
