using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// THE SECURITY BOARD'S VERBS ON THE PIPE (<c>U-board-b1</c>; R24 § 4.2, ORCHESTRATION § 2.9). Every handler proves its caller with ONE
/// helper (<see cref="BoardVerbCaller"/>) before anything is read or written, and hands the rest to the gateway's board seam, whose store
/// checks the seat again inside the act's own write. None of them reaches the owner's authority — the veto, the board's composition and
/// budget are in-process seams no op maps to (AUTH-001, CHANGE-001) — and none places, alters, times or sizes an order (ORDER-001).
/// </summary>
public sealed partial class GatewayPipeServer
{
    /// <summary>
    /// THE OPS A BOARD SEAT MAY CALL BESIDE ITS OWN VERBS: reads of the account, the market, the ledgers and the records — each of them
    /// writes nothing. Everything else is refused to a board seat before it is read (<see cref="Handle"/>): an allowlist, so an op
    /// added later is refused to the board until it is named here.
    /// </summary>
    internal static readonly string[] BoardSeatReads =
    [
        Core.Ops.Status, Core.Ops.Schema, Core.Ops.Connectors, Core.Ops.Accounts, Core.Ops.Account, Core.Ops.Instruments, Core.Ops.Quote,
        Core.Ops.Positions, Core.Ops.Position, Core.Ops.Orders, Core.Ops.Order, Core.Ops.Executions, Core.Ops.Pnl, Core.Ops.MaterialList,
        Core.Ops.DataList, Core.Ops.DataBars, Core.Ops.DataTape, Core.Ops.VenueList, Core.Ops.DeploymentList, Core.Ops.LedgerList,
        Core.Ops.LedgerShow, Core.Ops.LensList, Core.Ops.LensShow
    ];

    /// <summary>What every board verb says of an argument it does not take.</summary>
    const string BoardArgsWhy =
        "A board act's record is TradeAgent's: its id, its seat, its hash, its events and the version it writes are written by TradeAgent "
        + "from what it checked, never from an argument. Nothing was written.";

    /// <summary>
    /// THE BOARD SEAT A BOARD VERB IS EXERCISED UNDER, OR A REFUSAL — the one gate every <c>board-*</c> op passes first. The caller is the
    /// role its launch grant proved, and that role is a seat of the security board (<see cref="BoardSeats.IsBoardId"/>) whose state NOW
    /// is <c>active</c>; the launch is one the ledger recorded under that seat; and the seat's RECORDED assurance is at least
    /// <see cref="CapabilityPolicy.Minimum"/> of the decision verbs. A position — the chief included — is refused here, as a board id is
    /// refused at <see cref="OrgVerbCaller"/>. Every refusal is MARKED (<see cref="RefusalMark"/>).
    /// </summary>
    string BoardVerbCaller(AgentContext ctx)
    {
        var seat = ctx.Role;
        if (!BoardSeats.IsBoardId(seat))
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "a board verb is exercised by an active seat of the security board whose launch asks it, and this connection proved "
                + "none — a position, an app principal, or no launch grant. No position holds the board's verbs. Nothing was written."));
        var state = Gateway.BoardSeatState(seat!);
        if (state != BoardSeatStatus.Active)
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"board seat {seat} is {state ?? "no seat this board founded"}, not active: only an active seat acts (ORCHESTRATION § 2.3). "
                + "Nothing was written."));

        var attempt = ctx.AttemptId is { } id ? Gateway.Attempt(id) : null;
        if (attempt is null || attempt.Role != seat)
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{ctx.AttemptId}' is no launch the ledger recorded for {seat}, so there is no seat to weigh this verb against. "
                + "Nothing was written."));

        var minimum = CapabilityPolicy.Minimum(Capability.DecisionVerbs);
        if (AssuranceNamed(attempt.Assurance) is not { } assurance || assurance < minimum)
            throw RefusalMark.Mark(new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"the board's verbs need a seat of at least {SeatClasses.Name(minimum)} assurance, and this launch of {seat} was admitted "
                + $"on a {attempt.SeatClass ?? "unrecorded"} seat recorded {attempt.Assurance ?? "with no assurance"} (R24 § 4.1: broker-only "
                + "or confined, never unconfined). Nothing was written."));
        return seat!;
    }

    /// <summary>The intent a frame carries: a JSON list of changes, or a string holding one — passed on as text for the store's strict parse.</summary>
    static string IntentOf(IpcRequest req)
    {
        if (req.Args is null || !req.Args.TryGetValue("intent", out var raw) || raw.ValueKind == JsonValueKind.Null)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                "'intent' is required: a JSON list of changes, each {\"field\", \"from\", \"to\"}. Nothing was written.");
        return raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.GetRawText();
    }

    BoardActRequest ActRequest(AgentContext ctx, IpcRequest req, string seat) =>
        new(seat, ctx.AttemptId, IntentOf(req), Require(req, "rationale"), OrgRefs(req), ForecastClaims(req, "forecasts"));

    static object ActReply(BoardActResult r) => new
    {
        act = r.Act.Id,
        kind = r.Act.Kind,
        status = r.Status,
        version = r.PolicyVersion,
        intent_sha256 = r.Act.IntentSha256,
        expires_at = r.Act.ExpiresAt,
        words = r.Words,
        note = "Your rationale is your claim, sealed with the act; the hash, the checks, the version and every event are TradeAgent's."
    };

    /// <summary>ONE ACTIVE SEAT NARROWS, AT ONCE (<see cref="TradingGateway.BoardNarrow"/>).</summary>
    object BoardNarrow(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        var seat = BoardVerbCaller(ctx);
        return ActReply(Gateway.BoardNarrow(ActRequest(ctx, req, seat)));
    }

    /// <summary>ONE ACTIVE SEAT PROPOSES A WIDENING (<see cref="TradingGateway.BoardPropose"/>): a record; nothing changes.</summary>
    object BoardPropose(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        var seat = BoardVerbCaller(ctx);
        return ActReply(Gateway.BoardPropose(ActRequest(ctx, req, seat)));
    }

    /// <summary>ANOTHER ACTIVE SEAT CONFIRMS OR REFUSES A PROPOSAL, naming its intent's hash (<see cref="TradingGateway.BoardDecide"/>).</summary>
    object BoardDecide(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        var seat = BoardVerbCaller(ctx);
        var confirm = Require(req, "answer") switch
        {
            "confirm" => true,
            "refuse" => false,
            var other => throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{other}' is no answer to a proposal: confirm | refuse. Nothing was written.")
        };
        return ActReply(Gateway.BoardDecide(Require(req, "act"), seat, ctx.AttemptId, confirm, Require(req, "intent_sha256"),
            Require(req, "rationale")));
    }

    /// <summary>THE BOARD'S ACTS, NEWEST FIRST — never a rationale or a forecast (<see cref="TradingGateway.BoardList"/>).</summary>
    object BoardList(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        BoardVerbCaller(ctx);
        var limit = req.Args?.ContainsKey("limit") == true ? WholeNumber(req, "limit") : OrgListMax;
        return new
        {
            acts = Gateway.BoardList(limit),
            note = "Rationales and forecasts are not listed: 'board-show' gives an act's, and only once your own word on it is sealed."
        };
    }

    /// <summary>
    /// A SEAT PROPOSES A CHANGE OF THE BOARD'S OWN POWER (<see cref="BoardStore.ProposePower"/>; CHANGE-001): one record, shown to the
    /// owner, applied by nothing. The board's composition, its budget and the audit line's are the owner's alone, in process.
    /// </summary>
    object BoardPowerPropose(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        var seat = BoardVerbCaller(ctx);
        var row = Gateway.BoardProposePower(seat, ctx.AttemptId, Require(req, "target"), Require(req, "text"));
        return new
        {
            proposal = row.Id,
            target = row.Target,
            note = "Recorded and shown to the owner. It changes nothing: the board's own power — its seats, its budget, the audit line's "
                   + "budget, a connection, the founding policy's shape, the kernel — is changed by the owner in TradeAgent, or by no one."
        };
    }

    /// <summary>
    /// ONE ACT, BLIND UNTIL THE CALLER'S OWN WORD ON IT IS SEALED (<see cref="TradingGateway.BoardShow"/>): before, the intent, its hash and
    /// direction, the refs, TradeAgent's figures in force and the expiry; after, everything, every seat's rationale included.
    /// </summary>
    object BoardShow(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, BoardArgsWhy);
        var seat = BoardVerbCaller(ctx);
        var (blind, whole) = Gateway.BoardShow(seat, Require(req, "act"));
        return blind is not null
            ? new
            {
                act = (object)blind,
                @sealed = false,
                note = "You have not sealed your own word on this act, so you are shown what it would do and TradeAgent's figures — never "
                       + "another seat's reasons or forecasts. Decide on the evidence; after your decision, all of it is yours to read."
            }
            : new { act = (object)whole!, @sealed = true, note = "Every seat's rationale here is its claim, never a measurement." };
    }
}
