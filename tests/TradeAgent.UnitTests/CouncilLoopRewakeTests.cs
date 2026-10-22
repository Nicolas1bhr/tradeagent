using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RE-WAKE FLOOR THROUGH THE LOOP (<c>U-rewake-floor</c>; ORCHESTRATION § 0.4, turns are capital; § 2.5, a floor and never a
/// heartbeat): a member holding a mandate it never delivers, on this file's SEAM HOST — the real loop, the real launch ledger, the
/// real organisation's sweep, a conversation that is a test double, the test's own clock. No real model, no real seat.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>No look and no ceiling: every turn below is the mandate's, or the member's own request.</summary>
    static readonly MissionOptions MandateOnly = new()
    {
        ReviewEvery = TimeSpan.Zero, Ceilings = OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.Zero, StringComparer.Ordinal)
    };

    /// <summary>
    /// AN HOUR OF THE LOOP'S CLOCK, ONE TICK A MINUTE, for a member that never delivers its one mandate under
    /// <paramref name="floors"/>: the minute each of its launches started, the mandate it served, and the wakes it was given — printed
    /// as the unit's evidence. <paramref name="asks"/>, when set, is the member's <c>next.json</c> after its first turn.
    /// </summary>
    async Task<(List<int> Minutes, List<string?> Served, MandateRow Mandate, Database Db)> Hour(RewakeFloors floors, string label,
        double? asks = null)
    {
        var (db, root) = Workspace();
        Unit(db, SessionTeam, OrgIds.Research, null);
        Seat(db, TeamHead, SessionTeam, heads: true);
        Seat(db, Executor, SessionTeam);
        var m = Give(db, "find out whether the funding skew predicts the next hour");
        var start = DateTimeOffset.UtcNow.AddSeconds(1);
        var now = start;
        var host = new CouncilHost(db, root, new Concurrency()) { Clock = () => now, Floors = floors, EndsTurns = true }
            .Add(TeamHead).Add(Executor);
        var turns = 0;
        host.Conversations[Executor].OnTurn = () =>
        {
            if (++turns == 1 && asks is { } seconds)
                File.WriteAllText(Path.Combine(host.HomeFor(Executor), ".tradeagent", "next.json"), $$"""{"after_seconds": {{seconds}}}""");
        };
        var loop = new MissionLoop(host, MandateOnly, now: () => now);

        for (var minute = 0; minute < 60; minute++)
        {
            now = start.AddMinutes(minute);
            await loop.TurnAsync();
        }

        var attempts = new AiAttemptStore(db).Between(start.AddDays(-1), start.AddDays(1)).Where(a => a.Role == Executor)
            .OrderBy(a => a.StartedAt).ToList();
        var minutes = attempts.Select(a => (int)Math.Round((a.StartedAt - start).TotalMinutes)).ToList();
        log.WriteLine($"{label}: {attempts.Count} paid turns of the member in an hour, at minutes [{string.Join(", ", minutes)}]");
        foreach (var a in attempts.Take(6))
            log.WriteLine($"{label}:   attempt {a.Id} started {a.StartedAt:HH:mm:ss} mandate={a.MandateId}");
        foreach (var e in host.Events!.OfKind(MissionEventKind.Mandate).Take(6))
            log.WriteLine($"{label}:   wake {e.Id} created {e.CreatedAt:HH:mm:ss} due {e.DueAt:HH:mm:ss} consumed_by={e.ConsumedBy}");
        return (minutes, [.. attempts.Select(a => a.MandateId)], m, db);
    }

    /// <summary>
    /// (h) A MEMBER THAT NEVER DELIVERS SPENDS THREE TURNS WHERE IT SPENT EVERY TICK. Over an hour of the loop's clock, ticking each
    /// minute: under the shipped floors the member turns at T, T + 15 and T + 45 — its issued wake, then the sweep's re-wake a floor
    /// and then two floors after each undelivered turn — every one charged to its mandate; with every floor zero, as before this
    /// unit, it turns on every tick. And a member that asks, in its own <c>next.json</c>, to be woken in 60 s is woken then and that
    /// turn serves the mandate: the re-wake rides with its own request.
    ///
    /// <para>RED at base: the shipped run turned on every tick too — sixty paid turns.</para>
    /// </summary>
    [Fact]
    public async Task A_member_that_never_delivers_spends_three_turns_where_it_spent_every_tick()
    {
        var shipped = RewakeFloors.From(RewakeFloors.DefaultMinutes, OrgLevel.All.ToDictionary(l => l, _ => TimeSpan.FromHours(24)));

        var floored = await Hour(shipped, "floors default");
        using (floored.Db)
        {
            Assert.Equal([0, 15, 45], floored.Minutes);
            Assert.All(floored.Served, s => Assert.Equal(floored.Mandate.Id, s));
        }

        var zero = await Hour(RewakeFloors.None, "floors zero");
        using (zero.Db)
        {
            Assert.Equal(Enumerable.Range(0, 60), zero.Minutes);
            Assert.All(zero.Served, s => Assert.Equal(zero.Mandate.Id, s));
        }

        var asked = await Hour(shipped, "next.json 60 s", asks: 60);
        using (asked.Db)
        {
            Assert.Equal([0, 1], asked.Minutes.Take(2));
            Assert.Equal(asked.Mandate.Id, asked.Served[1]);
            var second = new AiAttemptStore(asked.Db).Between(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))
                .Where(a => a.Role == Executor).OrderBy(a => a.StartedAt).ElementAt(1);
            var took = new MissionEventStore(asked.Db).OfKind(MissionEventKind.Mandate).Concat(new MissionEventStore(asked.Db).OfKind(MissionEventKind.Self))
                .Where(e => e.ConsumedBy == second.Id).Select(e => e.Kind).Order(StringComparer.Ordinal).ToList();
            Assert.Equal([MissionEventKind.Mandate, MissionEventKind.Self], took);
        }
    }
}
