using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PACKET (<c>U-reconcile-packet</c>, item 2): every turn's Situation says what changed, what the role believes and
/// what is wanted — under the owner's words, above the attempt line and the state lines, which it leaves as they were.
/// </summary>
public partial class CouncilLoopTests(ITestOutputHelper log)
{
    /// <summary>The lines of one section: from its header to the next top-level line.</summary>
    static List<string> Section(string prompt, string header)
    {
        var lines = prompt.Split(Environment.NewLine);
        var at = Array.FindIndex(lines, l => l.StartsWith(header, StringComparison.Ordinal));
        Assert.True(at >= 0, $"no line starts with \"{header}\"");
        return [.. lines.Skip(at + 1).TakeWhile(l => l.StartsWith("  - ", StringComparison.Ordinal))];
    }

    /// <summary>
    /// (d) THE PACKET SAYS WHAT CHANGED, WHAT THE ROLE BELIEVES AND WHAT IS WANTED, UNDER THE OWNER'S WORDS. The chair is
    /// woken by its owner's words, a fill and a boundary; Research by the boundary. Each turn's message keeps the owner's
    /// words first; "Why you are awake" heads what changed, one line per observation with its entity; what the role
    /// believes is its OWN open entries, each marked as its claim — never another role's, never a dropped one, never
    /// inside what changed; what is wanted is its open objectives, the boundary and the guidance; the attempt line and
    /// the state lines follow, and the message ends with the new Continue. The belief lines are bounded: ten entries,
    /// 2 KiB, a long text cut in words, the rest counted.
    ///
    /// <para>RED at base 43c894a4's behaviour on the new fields (the loop handing the Situation none of them): no line
    /// starts with "- What changed". Mutant, the claim mark left off a belief line: red, every belief line no longer
    /// starts with "  - your claim".</para>
    /// </summary>
    [Fact]
    public async Task The_packet_says_what_changed_what_the_role_believes_and_what_is_wanted_under_the_owners_words()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        var events = host.Events!;
        var ledger = new ResearchLedger(db, () => now);
        const string research = CouncilRoles.Research, chair = CouncilRoles.Operations;

        // THE OBSERVED WORLD: the owner's words, a fill, a boundary.
        Assert.True(events.RecordOwnerMessage("What is the plan for today?", now));
        events.Raise(MissionEventIds.Fill("EX-1"), MissionEventKind.Fill, now,
            Json.Write(new { symbol = "ES", side = "Buy", quantity = 1m }), chair);
        var boundary = host.Boundaries!.Open(BoundaryKind.Promotion, "version-a", 1, BoundaryDisposition.Hold,
            "holdout run 1", now).Row;

        // WHAT EACH ROLE SAID IT BELIEVES: two of Research's open, one it dropped, and one of the chair's.
        var hypothesis = ledger.Add(research, null, LedgerKind.Hypothesis, "Funding below -0.05% mean-reverts within 8h.",
            LedgerMark.Hypothesis, 0.6m, null, null);
        var finding = ledger.Add(research, null, LedgerKind.Finding, "The 1h close breakout lost money over 2025.",
            LedgerMark.Claim, null, null, "run abc");
        var dropped = ledger.Add(research, null, LedgerKind.Lesson, "DROPPED-ONE", LedgerMark.Assumption, null, null, null);
        ledger.Revise(research, null, dropped.Id, "DROPPED-ONE", LedgerMark.Assumption, "no longer held", null,
            LedgerStatus.Dropped);
        var chairs = ledger.Add(chair, null, LedgerKind.Lesson, "CHAIR-ONE", LedgerMark.Claim, null, null, null);

        await loop.TurnAsync();
        await loop.TurnAsync();
        var ops = host.Opened.Single(o => o.Role == chair).Prompt;
        var res = host.Opened.Single(o => o.Role == research).Prompt;
        log.WriteLine($"Situation bytes: operations {Encoding.UTF8.GetByteCount(ops):N0}, research {Encoding.UTF8.GetByteCount(res):N0}");
        log.WriteLine(ops);
        log.WriteLine(res);

        // THE ORDER: the owner first, then the cause and what changed, what you believe, what is wanted, the attempt,
        // the state.
        string[] order =
        [
            "> What is the plan for today?", "- Why you are awake: ", "- What changed, ", "- What you believe",
            "- What is wanted", "- You are the ", "- This turn is attempt `",
            "- Local time: ", "- Trading mode: ", "- execution_available: ", "- Account: ", "- Open orders: ",
            "- Positions: "
        ];
        var at = order.Select(o => ops.IndexOf(o, StringComparison.Ordinal)).ToArray();
        Assert.All(at.Zip(order), p => Assert.True(p.First >= 0, $"the chair's message has no \"{p.Second}\""));
        Assert.Equal(at.Order(), at);
        Assert.EndsWith(MissionSituation.Continue + Environment.NewLine, ops);

        // WHAT CHANGED: one line per observation, each with its entity.
        Assert.Equal(
        [
            "  - your owner typed something — `owner:1`",
            "  - an order filled — Buy 1 ES, execution `fill:EX-1`",
            $"  - a consequential boundary opened and it is waiting on your assessment — boundary `{boundary.Id}` "
            + $"(promotion of version-a), decided by TradeAgent at {boundary.DeadlineAt.LocalDateTime:yyyy-MM-dd HH:mm}"
        ], Section(ops, "- What changed, "));

        // WHAT IS WANTED: the chair's open objectives — the owner's message and the boundary — with their entities.
        var wanted = Section(ops, "- What is wanted");
        Assert.Contains("  - an answer to your owner's message `owner:1`", wanted);
        Assert.Contains($"  - your assessment of boundary `{boundary.Id}` — its line follows", wanted);

        // WHAT YOU BELIEVE: each role its OWN open entries, newest first, every line its claim, none in what changed.
        Assert.Equal([$"  - {MissionSituation.ClaimMark}, entry {chairs.Id} (lesson, held as claim, confidence unknown): CHAIR-ONE"],
            Section(ops, "- What you believe"));
        var beliefs = Section(res, "- What you believe");
        Assert.Equal(
        [
            $"  - {MissionSituation.ClaimMark}, entry {finding.Id} (finding, held as claim, confidence unknown): "
            + "The 1h close breakout lost money over 2025.",
            $"  - {MissionSituation.ClaimMark}, entry {hypothesis.Id} (hypothesis, held as hypothesis, confidence 0.6): "
            + "Funding below -0.05% mean-reverts within 8h."
        ], beliefs);
        Assert.All(beliefs, l => Assert.StartsWith($"  - {MissionSituation.ClaimMark}", l));
        Assert.DoesNotContain("DROPPED-ONE", res);
        Assert.DoesNotContain("CHAIR-ONE", res);
        Assert.DoesNotContain(Section(res, "- What changed, "), l => l.Contains("Funding", StringComparison.Ordinal));
        Assert.Contains("  - your research mandate, standing: hypotheses, data and backtests", Section(res, "- What is wanted"));
        Assert.True(res.IndexOf("- What you believe", StringComparison.Ordinal) > res.IndexOf("- What changed, ", StringComparison.Ordinal));

        // BOUNDED: twelve long entries are at most ten lines in at most 2 KiB, a text cut in words, the rest counted.
        var long12 = Enumerable.Range(1, 12).Select(i => new LedgerListed(
            new LedgerEntryRow(100 - i, LedgerKind.Hypothesis, research, null, null, now, null),
            new LedgerRevisionRow(100 - i, 1, now, null, new string('x', 1_500) + "é", LedgerMark.Hypothesis, null,
                LedgerStatus.Open, null), 1, 0)).ToList();
        var bounded = Section(new MissionSituation { Believed = long12 }.Text(), "- What you believe");
        var claims = bounded.Where(l => l.StartsWith($"  - {MissionSituation.ClaimMark}", StringComparison.Ordinal)).ToList();
        log.WriteLine($"bounded: {claims.Count} claim lines, {claims.Sum(l => Encoding.UTF8.GetByteCount(l + Environment.NewLine)):N0} bytes");
        Assert.InRange(claims.Count, 1, MissionSituation.BeliefLimit);
        Assert.True(claims.Sum(l => Encoding.UTF8.GetByteCount(l + Environment.NewLine)) <= MissionSituation.BeliefBytes);
        Assert.EndsWith(new string('x', 1_500) + "é", claims[0]);
        Assert.EndsWith("x … (cut here; the ledger holds the whole entry)", claims[^1]);
        Assert.StartsWith("  - and more open entries of yours than are shown here", bounded[^1]);

        // THE BOUNDARY LINES AND THE GUIDANCE ARE WHAT IS WANTED TOO, under the objectives and above the attempt.
        var composed = new MissionSituation
        {
            Role = chair, Attempt = "turn-x", Wanted = [new RoleObjective(RoleObjectives.BoundaryAssessed, boundary.Id)],
            Boundaries = [$"Boundary `{boundary.Id}` (promotion of version-a): write your assessment this turn."],
            Guidance = "stay in ES"
        }.Text();
        string[] wantedOrder =
        [
            "- What is wanted", $"  - your assessment of boundary `{boundary.Id}`", $"- Boundary `{boundary.Id}`",
            "- The owner's guidance: stay in ES", "- You are the ", "- This turn is attempt `turn-x`", "- Local time: "
        ];
        var placed = wantedOrder.Select(o => composed.IndexOf(o, StringComparison.Ordinal)).ToArray();
        Assert.All(placed.Zip(wantedOrder), p => Assert.True(p.First >= 0, $"no \"{p.Second}\""));
        Assert.Equal(placed.Order(), placed);

        var none = new MissionSituation { Believed = [] }.Text();
        Assert.Contains("- What you believe: you hold no open entry in the research ledger.", none);
    }
}
