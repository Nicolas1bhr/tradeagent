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
            TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts"), collapse), FundingOf(caller, ctx.AttemptId), DateTimeOffset.UtcNow);
        return new
        {
            unit = made.Unit.Id,
            kind = made.Unit.Kind,
            parent = made.Unit.ParentId,
            head = "vacant",
            status = made.Unit.Status,
            ai_share = made.Unit.EnvelopeShare,
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
            Require(req, "rationale"), TextList(req, "options"), OrgRefs(req), ForecastClaims(req, "forecasts")), DateTimeOffset.UtcNow);
        return new
        {
            position = hired.Position.Id,
            unit = hired.Position.UnitId,
            status = hired.Position.Status,
            home = hired.Position.HomeDir,
            seat = "none",
            decision = DecisionReply(hired.Decision),
            note = "Hired DORMANT: the position's id and home are TradeAgent's, it has no seat, and it runs nothing — no wake reaches "
                   + "it and no launch starts for it until it is seated and activated, neither of which is an organisation verb's. "
                   + "The hire is your 'hire' decision, the rationale your own ledger entry, marked claim."
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
            var chain = days.Where(d => d.Ceiling == ceiling).ToList();
            var start = chain.FindIndex(d => d.UnitId == unit.Id);
            if (start < 0) return null;
            if (chain.Skip(start).FirstOrDefault(d => d.Envelope is not null)?.Envelope is not { } envelope) return null;
            parent.Add(new CeilingEnvelope(ceiling, envelope));
        }
        return parent.Count == 0 ? null
            : new CharterFunding(caller, attempt.ReservedCost, parent, LegacyShares([.. parent.Select(p => p.Ceiling)]));
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
    /// <paramref name="role"/>'s chain in one <paramref name="ceiling"/>: the host's reading for that ceiling, or — from a host that wires
    /// only the class reading — that reading's rows in the ceiling, which are the same figures. Null where neither says.
    /// </summary>
    IReadOnlyList<UnitDay>? EnvelopesIn(string role, string ceiling) =>
        Gateway.AiEnvelopesIn is { } named
            ? named(role, ceiling)
            : Gateway.AiEnvelopes?.Invoke(role) is { } days ? [.. days.Where(d => d.Ceiling == ceiling)] : null;
}
