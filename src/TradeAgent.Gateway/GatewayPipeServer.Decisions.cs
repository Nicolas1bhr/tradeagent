using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.Gateway;

/// <summary>
/// THE ORGANISATION'S DECISION RECORDS, FORECASTS, ESCALATIONS AND LATERAL NOTES ON THE PIPE (<c>U-org-decisions</c>, O5b;
/// ORCHESTRATION §§ 2.3, 2.9–2.11; ORGANISATION §§ 4, 5). Every handler proves its caller with O5a's ONE helper
/// (<see cref="OrgVerbCaller"/>: the active position its launch grant proved, on a seat whose RECORDED assurance carries the
/// organisation's verbs — never headship) before anything is read or written, and hands the rest to <see cref="OrgStore"/>, whose
/// writers check the chart and the records again. None places an order, and none reaches the mode, the kill switch, an approval,
/// live activation, an update or a credential.
/// </summary>
public sealed partial class GatewayPipeServer
{
    /// <summary>The most rows one <c>decision-list</c>, <c>escalation-list</c> or <c>lateral-list</c> answers.</summary>
    public const int OrgListMax = 50;

    /// <summary>What an organisation record's refusal of an argument it does not take says.</summary>
    const string OrgRecordArgsWhy = "A decision's, a forecast's, an escalation's or a note's record is the app's: its id, its sender, "
                                    + "its recipient, its scoring and its resolution are written by TradeAgent and by nothing a frame "
                                    + "carries. Nothing was written.";

    /// <summary>
    /// RECORDS A ROUTINE DECISION OF THE CALLER'S (<see cref="OrgStore.Decide"/>): its options, its choice and its rationale — the
    /// position's claims, the rationale written as its own <c>decision</c> entry in the research ledger — and the app records it
    /// cites, in one write; or refused in words with nothing written.
    /// </summary>
    object DecisionRecord(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, OrgRecordArgsWhy);
        var position = OrgVerbCaller(ctx, Capability.DecisionVerbs);
        var row = Gateway.Org.Decide(position, ctx.AttemptId, req.Str("kind") ?? OrgDecisionKind.Routine, TextList(req, "options"),
            Require(req, "choice"), Require(req, "rationale"), OrgRefs(req), DateTimeOffset.UtcNow);
        return DecisionReply(row);
    }

    /// <summary>
    /// THE CALLER'S OWN DECISIONS, NEWEST FIRST — never another position's. Bounded twice and never silently: at most
    /// <see cref="OrgListMax"/> and <see cref="ResearchLedger.MaxReadBytes"/> as the wire writes them; 'more' says it stopped and
    /// 'next_before' continues it.
    /// </summary>
    object DecisionList(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, OrgRecordArgsWhy);
        var position = OrgVerbCaller(ctx, Capability.Reads);
        var all = Gateway.Org.DecisionsOf(position).ToList();
        var from = From(all.FindIndex(d => d.Id == req.Str("before")), req.Str("before"), "decision");
        return Bounded(all.Skip(from), d => d.Id, d => new
        {
            decision = d.Id, kind = d.Kind, choice = d.Choice, at = d.At, ledger_entry = d.LedgerEntryId,
            forecasts = Gateway.Org.ForecastsOn(d.Id).Count
        }, "decisions", "Each choice is its author's words; the id, the time and the counts are TradeAgent's records.");
    }

    /// <summary>
    /// ONE DECISION OF THE CALLER'S, IN FULL: its options, its choice and its rationale (the ledger entry, a claim), the refs it
    /// cites, and each forecast with its probability — the forecaster's claim — shown BESIDE the app's measured resolution and the
    /// record that resolution rests on, never instead of it. Always whole: a decision is bounded at its write.
    /// </summary>
    object DecisionShow(AgentContext ctx, IpcRequest req)
    {
        OnlyDeclared(req, OrgRecordArgsWhy);
        var position = OrgVerbCaller(ctx, Capability.Reads);
        var id = Require(req, "id");
        var d = Gateway.Org.Decision(id);
        if (d is null || d.Position != position)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{id}' is no decision of yours: a position reads its own decision records and nobody else's.");
        var rationale = Gateway.Ledger.Latest(d.LedgerEntryId);
        return new
        {
            decision = d.Id,
            kind = d.Kind,
            at = d.At,
            attempt = d.Attempt,
            options = d.Options,
            choice = d.Choice,
            rationale = rationale?.Text,
            rationale_mark = rationale?.Mark,
            ledger_entry = d.LedgerEntryId,
            refs = d.Refs.Select(r => new { kind = r.Kind, id = r.Id }),
            forecasts = Gateway.Org.ForecastsOn(d.Id).Select(ForecastShown),
            note = "The options, the choice, the rationale and each forecast's probability are your claims, stored as written. "
                   + "Whether a forecast is scored, and its resolution and the record it rests on, are TradeAgent's — shown beside "
                   + "the probability, never instead of it."
        };
    }

    /// <summary>A forecast as a read shows it: the claim, the app's scoring, and the app's resolution beside it, or null while open.</summary>
    object ForecastShown(ForecastRow f) => new
    {
        forecast = f.Id,
        predicate = f.Predicate,
        vocabulary = f.Vocabulary,
        subject = f.Subject,
        target = f.Target,
        by = f.ResolveBy,
        probability = f.Probability.ToString(CultureInfo.InvariantCulture),
        scored = f.Scored,
        unscored_why = f.UnscoredWhy,
        resolution = Gateway.Org.Resolution(f.Id) is { } r
            ? new { outcome = r.Outcome, record_kind = r.RecordKind, record_id = r.RecordId, why = r.Why, at = r.At }
            : null
    };

    /// <summary>A decision as the app recorded it.</summary>
    object DecisionReply(DecisionRow d) => new
    {
        decision = d.Id,
        kind = d.Kind,
        ledger_entry = d.LedgerEntryId,
        at = d.At,
        forecasts = Gateway.Org.ForecastsOn(d.Id).Select(ForecastShown),
        note = "Recorded: the id is TradeAgent's, the rationale is your own 'decision' entry in the research ledger (marked claim), "
               + "and nothing edits either — a changed mind is a new record. Each forecast resolves from TradeAgent's own records."
    };

    // ---- the parts the handlers share --------------------------------------------------------------------------------

    /// <summary>Where a list continues: the row after <paramref name="before"/>, or the first — or refused, never guessed.</summary>
    static int From(int at, string? before, string what)
    {
        if (before is null) return 0;
        if (at < 0)
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST,
                $"'{before}' is no {what} of yours: pass the 'next_before' an answer gave.");
        return at + 1;
    }

    /// <summary>
    /// A LIST BOUNDED TWICE AND NEVER SILENTLY: at most <see cref="OrgListMax"/> rows and <see cref="ResearchLedger.MaxReadBytes"/>
    /// as the wire writes the WHOLE answer, its frame at its widest first; whole rows only, the first always served (every record is
    /// bounded at its write); 'more', 'capped_by' and 'next_before' say where it stopped.
    /// </summary>
    object Bounded<T>(IEnumerable<T> rows, Func<T, string> id, Func<T, object> shown, string name, string note)
    {
        var widest = new string('f', 64);
        long spent = ReplyBytes(ListFrame(name, [], "bytes", widest, note));
        var served = new List<object>();
        string? last = null, cappedBy = null;
        foreach (var row in rows)
        {
            if (served.Count == OrgListMax) { cappedBy = "count"; break; }
            var shownRow = shown(row);
            var cost = ReplyBytes(shownRow) + (served.Count > 0 ? 1 : 0);
            if (served.Count > 0 && spent + cost > ResearchLedger.MaxReadBytes) { cappedBy = "bytes"; break; }
            spent += cost;
            served.Add(shownRow);
            last = id(row);
        }
        return ListFrame(name, served, cappedBy, cappedBy is null ? null : last, note);
    }

    static Dictionary<string, object?> ListFrame(string name, List<object> served, string? cappedBy, string? next, string note) => new()
    {
        [name] = served,
        ["more"] = cappedBy is not null,
        ["capped_by"] = cappedBy,
        ["next_before"] = next,
        ["note"] = note
    };

    /// <summary>The app records a frame cites, as a JSON list of {"kind", "id"} — absent is none; any other shape is refused.</summary>
    static IReadOnlyList<OrgRef> OrgRefs(IpcRequest req) => Claims(req, "refs", ["kind", "id"], o => new OrgRef(o["kind"], o["id"]));

    /// <summary>
    /// A LIST OF TEXTS THE FRAME CARRIES: a JSON array of strings, or a string holding one; absent is none; anything else is
    /// refused, never cut or filled in.
    /// </summary>
    static IReadOnlyList<string> TextList(IpcRequest req, string arg)
    {
        if (req.Args is null || !req.Args.TryGetValue(arg, out var raw) || raw.ValueKind == JsonValueKind.Null) return [];
        JsonElement list;
        try
        {
            list = raw.ValueKind == JsonValueKind.String ? JsonDocument.Parse(raw.GetString() ?? "[]").RootElement.Clone() : raw;
        }
        catch (JsonException)
        {
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{arg}' is a JSON list of texts, and this is not JSON. Nothing was written.");
        }
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            throw new GatewayDeniedException(ErrorCode.INVALID_REQUEST, $"'{arg}' is a JSON list of texts. Nothing was written.");
        return [.. list.EnumerateArray().Select(e => e.GetString()!)];
    }
}
