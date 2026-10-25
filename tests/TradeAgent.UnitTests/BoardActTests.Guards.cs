using System.Text.RegularExpressions;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 10: the guards ---------------------------------------------------------------------------------------------------

    /// <summary>The owner's in-process seams over the board (AUTH-001, CHANGE-001), and the test host's one writer of an active seat.</summary>
    static readonly string[] OwnersSeams =
        ["VetoBoardAct", "PreviewBoardVeto", "SetBoardBudgetShare", "FoundBoardSeat", "RetireBoardSeat", "SetSeatStateUnderTest"];

    /// <summary>Every source of the agent-facing side: the pipe server's partials, the broker door, the worker's tools and the CLI.</summary>
    static IEnumerable<string> AgentFacingSources()
    {
        var src = Path.Combine(RepoRoot(), "src");
        bool Built(string f) => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        return Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Gateway"), "GatewayPipeServer*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "BrokerDoor*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "WorkerTools*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.TradeCli"), "*.cs", SearchOption.AllDirectories))
            .Concat([Path.Combine(src, "TradeAgent.Gateway", "GatewaySchema.cs")])
            .Where(f => !Built(f));
    }

    /// <summary>
    /// NO OP, NO VERB AND NO BOARD ACT REACHES THE OWNER'S VETO, THE BOARD'S COMPOSITION OR ITS BUDGET (AUTH-001, CHANGE-001): no source of
    /// the pipe server, the broker door, the worker's tools, the schema or the CLI names the owner's seams — comments included — and
    /// no op of the pipe is named for them; the seams themselves are the gateway's public, in-process members. RED with a pipe source
    /// naming <c>VetoBoardAct</c>.
    /// </summary>
    [Fact]
    public void No_op_verb_or_board_act_reaches_the_owners_veto_composition_or_budget()
    {
        var sources = AgentFacingSources().ToList();
        Assert.Contains(sources, f => f.EndsWith("GatewayPipeServer.Board.cs", StringComparison.Ordinal));
        Assert.Contains(sources, f => f.EndsWith("BrokerDoor.cs", StringComparison.Ordinal));
        var hits = sources.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => OwnersSeams.Any(s => l.line.Contains(s, StringComparison.Ordinal)))
            .Select(l => $"{Path.GetFileName(l.f)}:{l.i + 1}: {l.line.Trim()}")
            .ToList();
        foreach (var h in hits) log.WriteLine(h);
        Assert.Empty(hits);

        foreach (var op in GatewaySchema.Ops())
            Assert.DoesNotContain(OwnersSeams, s => Regex.IsMatch(op.Op.Replace("-", ""), Regex.Escape(s.Replace("Board", "")), RegexOptions.IgnoreCase));
        foreach (var seam in OwnersSeams.Take(5))
            Assert.True(typeof(TradingGateway).GetMethod(seam) is { IsPublic: true }, $"{seam} is not the gateway's own in-process seam");
        Assert.True(typeof(BoardStore).GetMethod("SetSeatStateUnderTest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            is { IsAssembly: true });
    }

    /// <summary>
    /// A BOARD SEAT NEVER PLACES AN ORDER (ORDER-001, SIGNAL-001): no board id — the founding seats or one the owner founds — holds order
    /// permission, as a role or as a caller's context, and an active board seat's order op is refused at the pipe; no field an intent
    /// may change names an order, its price, its time or its size. Watched red with order permission given to board ids.
    /// </summary>
    [Fact]
    public void A_board_seat_never_places_an_order()
    {
        foreach (var id in BoardSeats.Founding.Concat(["board-4", "board-99"]))
        {
            Assert.False(CouncilRoles.MayPlaceOrders(id), $"{id} may place orders");
            Assert.False(AgentContext.ForAgent("board", id, "turn-board", null).MayPlaceOrders, $"{id}'s context may place orders");
        }
        Assert.True(CouncilRoles.MayPlaceOrders(CouncilRoles.Operations));
        Assert.All(Ops.Board, op => Assert.False(Ops.IsMutating(op)));
        string[] order = ["side", "price", "stop", "fill", "tif", "symbol", "schedule", "when", "deadline", "buy", "sell"];
        foreach (var f in BoardDirection.Fields)
            Assert.DoesNotContain(f.Path.Split('.', '_'), w => order.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE BOARD'S FIELDS ARE THE POLICY'S — AND THE CHART'S CHIEF (<c>U-org-chief</c>) — AND NO EVIDENCE SETTING IS ONE (EVID-001): every
    /// field an act may change is a field of the policy's body or <c>org.chief</c> — which holds no friction, holdout, price, campaign, judge or verdict (<c>BoardTests.The_policy_holds_no_evidence_setting</c>)
    /// — and none names one of those itself. RED with an evidence field added to the direction table.
    /// </summary>
    [Fact]
    public void The_boards_fields_are_the_policys_and_none_is_evidence()
    {
        var policy = BoardPolicyBody.Flatten(BoardPolicyBody.Canonical(BoardFounding.V1)).Select(f => f.Path)
            .SelectMany(p => Enumerable.Range(1, p.Split('.').Length).Select(n => string.Join('.', p.Split('.').Take(n))))
            .ToHashSet(StringComparer.Ordinal);
        string[] evidence = ["fee", "slippage", "friction", "holdout", "price", "campaign", "judge", "verdict", "trial", "exploration", "cost_cap",
            "perception", "watcher", "audit", "composition", "budget"];
        foreach (var f in BoardDirection.Fields)
        {
            Assert.Contains(f.Path, policy);
            Assert.DoesNotContain(evidence, e => f.Path.Contains(e, StringComparison.Ordinal));
        }

        // THE ONE NAME BESIDE THE POLICY'S (U-org-chief): the chart's chief, which is no evidence setting — and an intent naming any other
        // field outside the policy, an evidence word among them, is refused at the parse. The fields an act names ⊆ the policy's ∪ {org.chief}.
        Assert.DoesNotContain(BoardDirection.Chief, policy);
        Assert.DoesNotContain(evidence, e => BoardDirection.Chief.Contains(e, StringComparison.Ordinal));
        Assert.NotNull(BoardIntent.Parse("""[{"field":"org.chief","from":null,"to":{"runtime":"codex","model":"gpt-6"}}]""").Intent);
        foreach (var e in evidence.Append("org.unit").Append("org.chief.runtime"))
            Assert.Null(BoardIntent.Parse($$"""[{"field":"{{e}}","from":1,"to":0}]""").Intent);
    }
}
