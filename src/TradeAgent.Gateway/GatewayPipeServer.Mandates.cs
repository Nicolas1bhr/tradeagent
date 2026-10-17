using System.Globalization;
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
    /// it is what the caller is, not a passing condition of the host.
    /// </summary>
    string OrgVerbCaller(AgentContext ctx, Capability capability)
    {
        var minimum = CapabilityPolicy.Minimum(capability);
        var position = Gateway.ActivePositionOf(ctx.Role)
            ?? throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "an organisation verb is exercised by the active position whose launch asks it, and this connection proved none — "
                + "no launch grant, an app principal, or a position that is not active. Nothing was written.");

        var attempt = ctx.AttemptId is { } id ? Gateway.Attempt(id) : null;
        if (attempt is null || attempt.Role != position)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{ctx.AttemptId}' is no launch the ledger recorded for {position}, so there is no seat to weigh this verb against. "
                + "Nothing was written.");

        var recorded = AssuranceNamed(attempt.Assurance);
        if (recorded is not { } assurance || assurance < minimum)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"the organisation's verbs need a seat of at least {SeatClasses.Name(minimum)} assurance, and this launch of "
                + $"{position} was admitted on a {attempt.SeatClass ?? "unrecorded"} seat recorded "
                + $"{attempt.Assurance ?? "with no assurance"} (ORCHESTRATION § 2.3). Heading a unit grants no verb by itself. "
                + "Nothing was written.");
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
            req.Str("shape"), NamedFlag(req, "exact", whenAbsent: false), Due(req), allocation, retries), DateTimeOffset.UtcNow);
        return MandateReply(row);
    }

    /// <summary>CANCELS AN OPEN MANDATE THE CALLER DECIDES, with a reason (<see cref="OrgStore.Cancel"/>).</summary>
    object MandateCancel(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, MandateArgsWhy);
        var caller = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var id = Require(req, "mandate");
        Gateway.Org.Cancel(id, caller, ctx.AttemptId, Require(req, "why"), DateTimeOffset.UtcNow);
        return MandateReply(Gateway.Org.Mandate(id)!);
    }

    /// <summary>What an organisation verb's refusal of an argument it does not take says.</summary>
    const string MandateArgsWhy = "A mandate's record is the app's: its id, its events, its seal and its checks are written by "
                                  + "TradeAgent and by nothing a frame carries. Nothing was written.";

    /// <summary>The mandate as the app recorded it, and its status — its latest event.</summary>
    object MandateReply(MandateRow m) => new
    {
        mandate = m.Id,
        status = Gateway.Org.MandateStatus(m.Id),
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
