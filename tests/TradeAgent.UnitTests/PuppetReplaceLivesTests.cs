using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUPPET LIFE OF A REPLACED WORKER (<c>U-worker-replace</c>; VISION § 1 test 1, "replace every occupant"), on the real kernel with
/// puppet seats: occupant A takes a mandate's turn and keeps a plan — and delivers nothing; the owner's press retires it; occupant B, woken
/// once, starts in a FRESH session (never A's) from a packet built from the position's memory alone, and resumes the mandate from it: its
/// script finds the mandate's id and the plan's words in its own prompt — nothing else tells it either — and delivers. No model.
/// </summary>
public class PuppetReplaceLivesTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
    }

    static Action<Database> SeedTeam(string team, params string[] positions) => db => db.Write(_ =>
    {
        const string at = "2026-10-22T12:00:00.0000000Z";
        var sql = new List<string> { $"INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES('{team}', '{OrgIds.Research}', 'team', NULL, 'active', '{at}')" };
        sql.AddRange(positions.Select(p => $"INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES('{p}', '{team}', 'org/{p}', 'active', '{at}')"));
        sql.Add($"UPDATE org_unit SET head_position_id='{positions[0]}' WHERE id='{team}'");
        foreach (var line in sql)
        {
            using var c = db.Cmd(line);
            c.ExecuteNonQuery();
        }
        return 0;
    });

    static (string? Session, string? Why) SessionOf(AiAttempt a)
    {
        using var context = JsonDocument.Parse(a.Context!);
        var root = context.RootElement;
        return (root.TryGetProperty("session", out var s) ? s.GetString() : null,
            root.TryGetProperty("session_why", out var w) ? w.GetString() : null);
    }

    /// <summary>
    /// A WORKS, IS RETIRED, B RESUMES THE MANDATE FROM MEMORY ALONE. Arrows: mandate issued → A's turn (fresh, first; writes
    /// <c>trading/PLAN.md</c>, delivers nothing) → the plan's revision recorded → the owner's two-press replacement (event, fence, one
    /// <c>occupant</c> wake) → B's turn: fresh, why <c>replaced</c>, its packet opening "You are this position's occupant 2" with the plan
    /// quoted and the mandate it holds → B's <c>trade mandate deliver</c> with the mandate id and the plan's line read from its own prompt →
    /// the delivery is sealed under B's launch, the mandate is still the same one, and the spend of A's launch still counts.
    /// </summary>
    [Fact]
    public async Task A_worker_retired_mid_mandate_is_resumed_by_a_fresh_occupant_from_the_positions_memory_alone()
    {
        const string head = "pos-5e55cc000001", member = "pos-5e55cc000002", team = "team-5e55cc00";
        await using var life = await PuppetLife.Begin("replace", seed: SeedTeam(team, head, member));
        life.Script(member,
            ["write trading/PLAN.md Deliver this: the second venue leads the funding skew by an hour.", "usage 100 0 10", "final Planned, not delivered."],
            ["trade mandate deliver {prompt:Mandate `([^`]+)` you hold} \"{prompt:    > (Deliver this: [^\\r\\n]*)}\"", "usage 100 0 10", "final Delivered."]);

        var org = new OrgStore(life.Host.Db);
        var m = org.Assign(new MandateIssue(head, null, member, MandateKind.Task, "find out whether the funding skew predicts the next hour", null,
            MandateShape.Report, false, DateTimeOffset.UtcNow.AddDays(7), 5m, 2), DateTimeOffset.UtcNow);

        await life.RunUntil(() => life.AttemptsOf(member).Count(a => a.State == AiAttemptState.ENDED) == 1, "occupant A's turn ended");
        var plan = new PublicationStore(life.Host.Db).Latest(member, PublicationKind.Plan);
        Assert.NotNull(plan);
        Assert.Contains("the second venue leads", plan!.Content, StringComparison.Ordinal);
        Assert.Empty(org.Deliveries(m.Id));
        var spentByA = new AiAttemptStore(life.Host.Db).TotalsBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, member);

        var replaced = org.ReplaceOccupant(org.PreviewReplaceOccupant(member), DateTimeOffset.UtcNow);
        Assert.Equal(2, replaced.Seated);

        await life.RunUntil(() => life.AttemptsOf(member).Count(a => a.State == AiAttemptState.ENDED) == 2, "occupant B's turn ended");
        var turns = life.AttemptsOf(member).OrderBy(a => a.StartedAt).ToList();
        Assert.Equal([("fresh", SessionWhy.First), ("fresh", SessionWhy.Replaced)], turns.Select(SessionOf));

        var prompt = life.Prompt(member, 2)!;
        var block = prompt[prompt.IndexOf("- You are this position's occupant 2", StringComparison.Ordinal)..];
        Assert.Contains("Occupant 1 was retired by your owner's press. You do not have its conversation", block, StringComparison.Ordinal);
        Assert.Contains($"  - Mandate `{m.Id}` you hold, open", block, StringComparison.Ordinal);
        Assert.Contains("    > Deliver this: the second venue leads the funding skew by an hour.", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Planned, not delivered.", prompt, StringComparison.Ordinal);    // A's reply is no record and never reaches B

        var deliver = Assert.Single(life.Log(member, 2), s => (string?)s["step"] == "trade");
        Assert.True((int?)deliver["exit"] == 0, $"occupant B's delivery was refused: {deliver.ToJsonString()}");
        var delivery = Assert.Single(org.Deliveries(m.Id));
        Assert.Equal(turns[1].Id, delivery.Attempt);
        Assert.Equal("Deliver this: the second venue leads the funding skew by an hour.", delivery.Report);
        var now = new AiAttemptStore(life.Host.Db).TotalsBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, member);
        Assert.True(now.Turns == spentByA.Turns + 1 && now.Spent >= spentByA.Spent, $"A's spend was not carried: {spentByA} then {now}");

        output.WriteLine($"life (replace) A {turns[0].Id} {SessionOf(turns[0])} → retired by event {replaced.EventId} → B {turns[1].Id} "
                         + $"{SessionOf(turns[1])} → delivery {delivery.Id} on {m.Id}");
        foreach (var line in block.Split('\n').Take(8)) output.WriteLine($"life (replace) B's takeover: {line.TrimEnd()}");
    }
}
