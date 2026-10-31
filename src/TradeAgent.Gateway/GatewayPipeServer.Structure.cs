using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// THE STRUCTURAL VERBS ON THE PIPE (<c>U-org-verbs-c</c>, O6 part c; ORCHESTRATION § 2.9): <c>org-charter</c> and <c>org-hire</c>;
/// and the restructuring verbs (<c>U-org-verbs-d</c>): <c>org-retire</c>, <c>org-close</c>, <c>org-merge</c>.
/// Each proves its caller with O5a's ONE helper (<see cref="OrgVerbCaller"/>: the active position its launch grant proved, on a seat
/// whose RECORDED assurance carries the organisation's verbs — never headship) before anything is read or written, and hands the act
/// to <see cref="OrgStore"/>, whose writers read the rights matrix, the bounds, the throttle and the earning test. Neither places an
/// order, activates a position, seats one, or reaches the mode, the kill switch, an approval, live activation, an update or a
/// credential; neither reaches <c>operations</c> or its division, which the matrix answers a two-seat board act.
/// </summary>
public sealed partial class GatewayPipeServer
{
    /// <summary>What a structural verb's refusal of an argument it does not take says.</summary>
    const string StructureArgsWhy = "A unit's or a position's record is the app's: its id, its parent, its head, its home, its status and its "
                                    + "seat are written by TradeAgent from the chart and by nothing a frame carries. Nothing was written.";

    /// <summary>
    /// CHARTERS A UNIT UNDER THE CALLER'S OWN (<see cref="OrgStore.Charter"/>): the kind, the AI share, the rationale and options,
    /// the cited work, the forecasts and — for a bounded experiment — the collapse date. The share is sized against the parent's
    /// envelope as the host that meters launches resolves it, and one turn of the caller's own recorded seat; a host that meters
    /// nothing refuses it.
    /// </summary>
    object OrgCharter(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var share = req.Dec("ai_share")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'ai_share' is required: the fraction of your unit's AI envelope the new unit is given, above 0 and at most 1. Nothing was written.");
        var collapse = req.Str("collapse_by") is { } raw ? When(raw, "collapse_by") : (DateTimeOffset?)null;
        var made = Gateway.Org.Charter(new CharterRequest(caller, ctx.AttemptId, Require(req, "kind"), share, Require(req, "rationale"),
            TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts"), collapse, req.Dec("trial_share"), req.Dec("verdict_share")),
            FundingOf(caller, ctx.AttemptId), DateTimeOffset.UtcNow);
        return new
        {
            unit = made.Unit.Id,
            kind = made.Unit.Kind,
            parent = made.Unit.ParentId,
            head = "vacant",
            status = made.Unit.Status,
            ai_share = made.Unit.EnvelopeShare,
            trial_share = made.Unit.TrialShare,
            verdict_share = made.Unit.VerdictShare,
            decision = DecisionReply(made.Decision),
            note = "Chartered: the unit's id is TradeAgent's, its head is vacant until you hire into it with 'org-hire' and its head flag, "
                   + "and its AI share is a fraction of your unit's envelope. The charter is your 'charter' decision — the rationale your "
                   + "own ledger entry, marked claim — and its unit-admissible forecast resolves at its date from the referee's verdicts "
                   + "on versions the unit's launches funded. It counts toward the organisation's two structural changes a week."
        };
    }

    /// <summary>
    /// HIRES A POSITION INTO A UNIT WHOSE PARENT THE CALLER HEADS, or appoints one to its VACANT head (<see cref="OrgStore.Hire"/>).
    /// The position is DORMANT: it runs nothing until it is activated, and activation is no organisation verb's.
    /// </summary>
    object OrgHire(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var hired = Gateway.Org.Hire(new HireRequest(caller, ctx.AttemptId, Require(req, "unit"), NamedFlag(req, "head", whenAbsent: false),
            Require(req, "rationale"), TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow,
            SelfActivationRefusal);
        var active = hired.Dormant is null;
        return new
        {
            position = hired.Position.Id,
            unit = hired.Position.UnitId,
            status = hired.Position.Status,
            home = hired.Position.HomeDir,
            dormant_because = hired.Dormant,
            decision = DecisionReply(hired.Decision),
            note = (active
                       ? "Hired and ACTIVE: the activation gate admits it on its seat and one turn of it fits its unit's envelope and the "
                         + "day-total, so it activated itself in the same write, and TradeAgent wakes it and you once each. "
                       : "Hired DORMANT: it runs nothing until it is activated — 'dormant_because' says why it did not activate itself. ")
                   + "The position's id and home are TradeAgent's. The hire is your 'hire' decision, the rationale your own ledger entry, "
                   + "marked claim; the owner can veto it, which ends it."
        };
    }

    /// <summary>
    /// RETIRES A POSITION IN A UNIT WHOSE PARENT THE CALLER HEADS (<see cref="OrgStore.Retire"/>; <c>U-org-verbs-d</c>): the position
    /// ends, its open mandates are cancelled with the app's reason, and its running turn is fenced — so the app ends it now.
    /// </summary>
    object OrgRetire(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var retired = Gateway.Org.Retire(new RetireRequest(caller, ctx.AttemptId, Require(req, "position"), Require(req, "rationale"),
            TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow);
        // THE RETIREMENT IS A FENCE (U-org-verbs-d), committed above: the app ends the retired position's running turn now.
        Gateway.Fenced();
        return new
        {
            position = retired.Position.Id,
            status = retired.Position.Status,
            cancelled = retired.Cancelled,
            decision = DecisionReply(retired.Decision),
            note = "Retired: the position has ended, every open mandate it held or decided is cancelled with TradeAgent's reason, and its "
                   + "running turn is fenced. Its launches, spend and records stay as they ran. The retirement is your 'retire' decision, the "
                   + "rationale your own ledger entry, marked claim; the owner can veto it."
        };
    }

    /// <summary>
    /// CLOSES A UNIT WHOSE PARENT THE CALLER HEADS (<see cref="OrgStore.Close"/>; <c>U-org-verbs-d</c>): the unit and its positions end,
    /// its open mandates are cancelled, its running turns fenced — so the app ends them now.
    /// </summary>
    object OrgClose(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var closed = Gateway.Org.Close(new CloseRequest(caller, ctx.AttemptId, Require(req, "unit"), Require(req, "rationale"),
            TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow);
        Gateway.Fenced();
        return new
        {
            unit = closed.Unit.Id,
            status = closed.Unit.Status,
            ended = closed.Ended,
            cancelled = closed.Cancelled,
            decision = DecisionReply(closed.Decision),
            note = "Closed: the unit and every position in it have ended, every open mandate held, decided or issued in it is cancelled "
                   + "with TradeAgent's reason, and their running turns are fenced. Nothing is deleted: its records and the spend its chain "
                   + "funded stay as they ran. It counts toward the organisation's two structural changes a week; the owner can veto it."
        };
    }

    /// <summary>
    /// MERGES A UNIT INTO ITS SIBLING, UNDER A PARENT THE CALLER HEADS (<see cref="OrgStore.Merge"/>; <c>U-org-verbs-d</c>): the first
    /// closes naming the second, which takes its positions and open child units; both units' running turns are fenced.
    /// </summary>
    object OrgMerge(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var merged = Gateway.Org.Merge(new MergeRequest(caller, ctx.AttemptId, Require(req, "unit"), Require(req, "into"),
            Require(req, "rationale"), TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow);
        Gateway.Fenced();
        return new
        {
            unit = merged.Closed.Id,
            into = merged.Successor.Id,
            moved = merged.MovedPositions,
            children = merged.MovedUnits,
            cancelled = merged.Cancelled,
            decision = DecisionReply(merged.Decision),
            note = "Merged: the first unit is closed naming the second, which now holds its positions and open child units; every open "
                   + "mandate held, decided or issued in the first is cancelled with TradeAgent's reason, and both units' running turns are "
                   + "fenced. Past spend stays on the chain that funded it; launches from now are funded through the second. It counts "
                   + "toward the organisation's two structural changes a week."
        };
    }

    /// <summary>
    /// RE-SPLITS ONE CURRENCY OF A CHILD UNIT'S SHARE (<see cref="OrgStore.Tilt"/>; <c>U-org-verbs-e</c>): the unit, the currency, the new
    /// share, the rationale and options, the refs and the forecasts. An AI share is sized as a charter's is — against the caller's own
    /// unit's envelope as the host that meters launches resolves it, and one turn of the caller's own recorded seat; a trial or verdict
    /// share against the open campaigns' budgets, which are read and never written.
    /// </summary>
    object OrgTilt(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var share = req.Dec("share")
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'share' is required: the unit's new share in that currency, a fraction at least 0 and at most 1. Nothing was written.");
        var currency = Require(req, "currency");
        var made = Gateway.Org.Tilt(new TiltRequest(caller, ctx.AttemptId, Require(req, "unit"), currency, share, Require(req, "rationale"),
            TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")),
            currency == OrgCurrency.Ai ? FundingOf(caller, ctx.AttemptId) : null, DateTimeOffset.UtcNow, SeatTurns(caller));
        return new
        {
            unit = made.Unit.Id,
            currency = made.Currency,
            from = made.From ?? "none",
            to = made.To,
            decision = DecisionReply(made.Decision),
            note = "Tilted: the share is a fraction — of your unit's AI envelope, or of a campaign's own trial or verdict budget — and the "
                   + "tilt is your 'envelope-tilt' decision, the rationale your own ledger entry, marked claim. A share moves at most 0.20 "
                   + "in seven days. Each trial and verdict is charged to the chain the launch that spent it was admitted under."
        };
    }

    /// <summary>
    /// ANSWERS A CUT RULE'S REVIEW WITH A RECORD (<see cref="OrgStore.AnswerReview"/>; <c>U-org-scorecards-b</c>): the review event its
    /// wake carried, the choice, the rationale and options, the refs and the forecasts. Only the reviewer the app woke answers it; the
    /// answer is a record and changes no share, status or seat.
    /// </summary>
    object OrgReviewAnswer(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var raw = Require(req, "event");
        if (!long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var eventId))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{raw}' is no event number: 'event' is the review's number, as the line of the 'cut-review' wake that woke you names it. Nothing was written.");
        var made = Gateway.Org.AnswerReview(new ReviewAnswerRequest(caller, ctx.AttemptId, eventId, Require(req, "choice"),
            Require(req, "rationale"), TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow);
        return new
        {
            @event = made.EventId,
            decision = DecisionReply(made.Decision),
            wakes_closed = made.WakesClosed,
            note = "Answered: your 'review-answer' decision cites the review first, the rationale your own ledger entry, marked claim, and "
                   + "its forecasts resolve from the app's records. The review changed nothing and neither does the answer: a change to the "
                   + "unit's share or chart is its own verb — 'org-tilt', 'org-close', 'org-merge' — or the security board's."
        };
    }

    /// <summary>
    /// WHY A HIRE MAY NOT ACTIVATE ITSELF NOW, or null where it may (<c>U-org-autonomy</c> item 2; R25 D3): the activation gate over the
    /// host's facts for it (<see cref="PositionActivation.SelfActivation"/> — a broker or cell seat, the proven cell, the seat's assurance,
    /// the unconfined seats), then one turn of its seat (<see cref="TradingGateway.AiReservation"/>) against its funding chain today — its
    /// unit's envelopes up to the plan's day-total (<see cref="NoRoomFor"/>). A host that reads none of it keeps the hire dormant.
    /// </summary>
    string? SelfActivationRefusal(string position)
    {
        SeatFacts? facts;
        try { facts = Gateway.SeatFactsOf?.Invoke(position); }
        catch (Exception) { facts = null; }
        if (PositionActivation.SelfActivation(position, facts) is { } gate) return gate;
        decimal? turn;
        try { turn = Gateway.AiReservation?.Invoke(position); }
        catch (Exception) { turn = null; }
        // NONE, OR NOTHING (Codex review): a seat that runs a model always reserves something, and the host reads an unpriced seat as zero —
        // either is a turn nobody can show fits, so the hire stays dormant.
        if (turn is not { } reserves || reserves <= 0m)
            return $"TradeAgent could not price one turn of {position}'s seat, so it does not activate a seat nobody can show fits its day.";
        return NoRoomFor(position, reserves, $"{position}'s seat");
    }

    /// <summary>
    /// A POSITION ASKS FOR ITS NEXT WAKE (<see cref="OrgStore.RequestWake"/>; <c>U-org-autonomy</c> item 1, R25 D5): one <c>self</c> wake,
    /// bounded by the security board's policy IN FORCE — the floor and the ceiling of the caller's level, read from
    /// <see cref="TradingGateway.Policy"/> at this call, never a constant — one pending at a time, and refused when one more turn of
    /// the caller's seat does not fit its funding chain today (<see cref="NoRoomForATurn"/>).
    /// </summary>
    object OrgWake(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, StructureArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        // A CELL ASKS WITH ITS WAKE FILE (Codex review): it writes '.tradeagent/next.json', and a request here beside it would be a second
        // pending wake — one request per position, so a cell seat is refused this verb in words.
        if ((ctx.AttemptId is { } launch ? Gateway.Attempt(launch) : null)?.SeatClass == SeatClasses.Cell)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"{caller} runs in a cell, which asks for its next wake by writing '.tradeagent/next.json' in its own home — one way to ask, so "
                + "one request at a time. Nothing was written.");
        if (req.Dec("after_minutes") is null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'after_minutes' is required: how long from now to be woken, in whole minutes above zero. Nothing was written.");
        var minutes = WholeNumber(req, "after_minutes");
        var asked = Gateway.Org.RequestWake(new WakeRequest(caller, ctx.AttemptId, TimeSpan.FromMinutes(minutes)),
            RewakeFloors.Of(Gateway.Policy), NoRoomForATurn(caller, ctx.AttemptId), DateTimeOffset.UtcNow);
        return new
        {
            wake = asked.EventId,
            due_at = asked.DueAt,
            level = asked.Level,
            floor_minutes = (int)asked.Floor.TotalMinutes,
            ceiling_minutes = (int)asked.Ceiling.TotalMinutes,
            note = "Asked: TradeAgent wakes you once at the time above, unless work addressed to you wakes you first — the request stays "
                   + "until a turn takes it. The turn is metered like any other: a day with no room left when it is due waits for room."
        };
    }

    /// <summary>
    /// WHY ONE MORE TURN OF <paramref name="caller"/>'S SEAT DOES NOT FIT ITS FUNDING CHAIN TODAY, or null where it fits: for every unit of the
    /// chain the host's meter reads (<see cref="TradingGateway.AiEnvelopes"/>, nearest first and ending at the root — the plan's day-total),
    /// today's spend, what is reserved and the unmeasured charge, plus one turn of the caller's own recorded launch, against its envelope.
    /// A host that meters nothing, a reading that fails or a launch the ledger does not hold is a reason too — refused, never guessed.
    /// </summary>
    string? NoRoomForATurn(string caller, string? attemptId)
    {
        if ((attemptId is { } id ? Gateway.Attempt(id) : null) is not { } attempt)
            return $"'{attemptId}' is no launch the ledger recorded, so there is no seat to price one more turn of.";
        return NoRoomFor(caller, attempt.ReservedCost, "your seat") is { } why
            ? why + " Your ceiling, or work addressed to you, wakes you."
            : null;
    }

    /// <summary>
    /// WHY ONE TURN RESERVING <paramref name="reserves"/> DOES NOT FIT <paramref name="position"/>'S FUNDING CHAIN TODAY, or null where it fits:
    /// every unit the host's meter reads for it (<see cref="TradingGateway.AiEnvelopes"/>, nearest first, ending at the root — the plan's
    /// day-total), today's spend, reservations and unmeasured charge plus that turn, against its envelope. No reading is a reason.
    /// </summary>
    string? NoRoomFor(string position, decimal reserves, string seat)
    {
        IReadOnlyList<UnitDay>? days;
        try { days = Gateway.AiEnvelopes?.Invoke(position); }
        catch (Exception) { days = null; }
        if (days is null || days.Count == 0)
            return $"TradeAgent could not read the AI envelope of {position}'s unit today, so it does not commit a turn nobody can show fits.";
        foreach (var d in days)
        {
            if (d.Envelope is not { } envelope) continue;
            var committed = d.Spent + d.Reserved + d.UnmeasuredCharge;
            if (committed + reserves > envelope)
                return $"{OrgEnvelopes.NameOf(d.UnitId)} has {committed.ToString(CultureInfo.InvariantCulture)} of its "
                       + $"{envelope.ToString(CultureInfo.InvariantCulture)} ({d.Ceiling}) committed today, and one turn of {seat} reserves "
                       + $"{reserves.ToString(CultureInfo.InvariantCulture)}.";
        }
        return null;
    }

    /// <summary>
    /// WHAT A CHARTER'S SHARE IS SIZED AGAINST, as the host that meters launches reads it (<see cref="TradingGateway.AiEnvelopes"/>):
    /// for each ceiling, the nearest envelope at or above the caller's unit — the parent-to-be — and what one turn of the caller's own
    /// recorded launch reserves. Null when nothing meters, the reading fails, or it holds no reading of that unit: refused, never guessed.
    /// </summary>
    CharterFunding? FundingOf(string caller, string? attemptId)
    {
        IReadOnlyList<UnitDay>? days;
        try { days = Gateway.AiEnvelopes?.Invoke(caller); }
        catch (Exception) { days = null; }
        var unit = Gateway.Org.UnitOf(caller);
        if (days is null || unit is null || !days.Any(d => d.UnitId == unit.Id)) return null;
        if ((attemptId is { } id ? Gateway.Attempt(id) : null) is not { } attempt) return null;

        var parent = new List<CeilingEnvelope>();
        foreach (var ceiling in days.Select(d => d.Ceiling).Distinct(StringComparer.Ordinal))
        {
            // THE CHAIN IS NEAREST FIRST (the caller's unit, then each above it): the first envelope in this ceiling binds.
            if (Nearest([.. days.Where(d => d.Ceiling == ceiling)], unit.Id) is not { } envelope) return null;
            parent.Add(new CeilingEnvelope(ceiling, envelope));
        }
        return parent.Count == 0 ? null
            : new CharterFunding(caller, attempt.ReservedCost, parent, LegacyShares([.. parent.Select(p => p.Ceiling)]));
    }

    /// <summary>THE FIRST ENVELOPE AT OR ABOVE <paramref name="unitId"/> in a chain read nearest first, or null where the chain holds no reading of it.</summary>
    static decimal? Nearest(IReadOnlyList<UnitDay> chain, string unitId)
    {
        var start = chain.ToList().FindIndex(d => d.UnitId == unitId);
        return start < 0 ? null : chain.Skip(start).FirstOrDefault(d => d.Envelope is not null)?.Envelope;
    }

    /// <summary>
    /// ONE TURN OF EACH SEATED POSITION AN AI TILT REACHES, AS THE HOST READS IT (<c>U-org-verbs-e</c>, fix pass 2): what it reserves
    /// (<see cref="TradingGateway.AiReservation"/>), and the caller's unit's envelope — the one the tilted share is a fraction of — in
    /// EACH ceiling that position's own seat is held to (<see cref="TradingGateway.AiClassOf"/>, <see cref="BillingClasses.CeilingsOf"/>),
    /// read in that named ceiling (<see cref="EnvelopesIn"/>), nearest envelope at or above the caller's unit. Never the caller's own
    /// ceilings. Null — no price, no class or no reading of a ceiling it needs — is refused by the tilt, never guessed.
    /// </summary>
    Func<string, SeatTurn?>? SeatTurns(string caller)
    {
        if (Gateway.AiReservation is not { } priced || Gateway.AiClassOf is not { } classOf) return null;
        var unit = Gateway.Org.UnitOf(caller);
        return position =>
        {
            if (unit is null || priced(position) is not { } reserves || classOf(position) is not { } billing) return null;
            var parent = new List<CeilingEnvelope>();
            foreach (var ceiling in BillingClasses.CeilingsOf(billing))
            {
                IReadOnlyList<UnitDay>? chain;
                try { chain = EnvelopesIn(caller, ceiling); }
                catch (Exception) { chain = null; }
                if (chain is null || Nearest(chain, unit.Id) is not { } envelope) return null;
                parent.Add(new CeilingEnvelope(ceiling, envelope));
            }
            return new SeatTurn(reserves, parent);
        };
    }

    /// <summary>
    /// EACH LEGACY DIVISION'S SHARE OF ITS PARENT, as admission resolves its NULL <c>envelope_share</c> (the split on the Safety page):
    /// its envelope over the nearest envelope above it, read IN EACH CEILING THE CHARTER DRAWS ON (<paramref name="ceilings"/> — the
    /// re-review of <c>U-org-verbs-c</c> fix 1, folded into <c>U-org-verbs-e</c> as item 7), whatever class the legacy seat itself bills.
    /// A ceiling the charter does not draw on is not read, so a zero cap there drops nothing; a division one of the charter's own
    /// ceilings cannot size — a zero cap in it, or no reading — is left out, and a charter it is a sibling of is refused
    /// (<see cref="OrgStore.Charter"/>) — never counted as nothing. The split is one fraction in every ceiling; should two readings
    /// differ, the larger is taken, the one that leaves the charter less room.
    /// </summary>
    Dictionary<string, decimal> LegacyShares(IReadOnlyList<string> ceilings)
    {
        var shares = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var unit in new[] { OrgIds.Operations, OrgIds.Research })
        {
            // A PAIR THE GATE KEEPS ASLEEP HOLDS NONE OF IT (U-loop-e2e; R25 D4): while a position beyond the pair is active, the gate
            // refuses the pair's unconfined seats every launch, so its split is spent by nobody — and on the founding policy's equal
            // split the two would hold the whole root, leaving an active chief nothing to charter. A share is a slice of the ceiling,
            // never an addition (BoardPolicy.ShareForRole): the pair woken again later still spends inside the day-total. A division with any
            // position active under its director still spends its split, and counts it (the Codex review).
            if (Asleep(unit))
            {
                shares[unit] = 0m;
                continue;
            }
            decimal? found = null;
            foreach (var ceiling in ceilings.Distinct(StringComparer.Ordinal))
            {
                IReadOnlyList<UnitDay>? chain;
                try { chain = EnvelopesIn(OrgEnvelopes.LegacyRoleOf(unit)!, ceiling); }
                catch (Exception) { chain = null; }
                var at = chain?.ToList().FindIndex(d => d.UnitId == unit) ?? -1;
                if (at < 0 || chain![at].Envelope is not { } own
                    || chain.Skip(at + 1).FirstOrDefault(d => d.Envelope is not null)?.Envelope is not { } above || above <= 0m)
                {
                    found = null;
                    break;
                }
                found = Math.Max(found ?? 0m, own / above);
            }
            if (found is { } share) shares[unit] = share;
        }
        return shares;
    }

    /// <summary>
    /// Whether legacy division <paramref name="unit"/> spends nothing now: the activation gate refuses its director because a position beyond
    /// the pair is active — its own rule over the facts the host reads for every launch (<see cref="TradingGateway.SeatFactsOf"/>) — and no
    /// position is active anywhere under it. Facts nobody could read are no proof of sleep.
    /// </summary>
    bool Asleep(string unit)
    {
        try
        {
            var role = OrgEnvelopes.LegacyRoleOf(unit)!;
            var org = Gateway.Org;
            return Gateway.SeatFactsOf?.Invoke(role) is { Standing: { IsLegacy: true } standing } facts
                   && facts.ActiveBeyondLegacy.Count > 0
                   && PositionActivation.Decide(role, standing, facts) is not null
                   && !org.ActivePositionsBeyondLegacy().Any(p => org.UnitOf(p) is { } u
                                                                  && (u.Id == unit || org.Ancestors(u.Id).Any(a => a.Id == unit)));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// <paramref name="role"/>'s chain in one <paramref name="ceiling"/>: the host's reading for that ceiling, or — from a host that wires
    /// only the class reading — that reading's rows in the ceiling, which are the same figures. Null where neither says.
    /// </summary>
    IReadOnlyList<UnitDay>? EnvelopesIn(string role, string ceiling) =>
        Gateway.AiEnvelopesIn is { } named
            ? named(role, ceiling)
            : Gateway.AiEnvelopes?.Invoke(role) is { } days ? [.. days.Where(d => d.Ceiling == ceiling)] : null;
}
