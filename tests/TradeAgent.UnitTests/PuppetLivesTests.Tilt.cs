using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class PuppetLivesTests
{
    /// <summary>
    /// (h) O6 PART E ON THE REAL KERNEL, WITH A PUPPET SEAT (<c>U-org-verbs-e</c>): a division head on a puppet seat tilts its team
    /// through <c>trade org tilt</c> — the AI share by 0.15 (0.2 → 0.35), a trial share and a verdict share set from none — and a second
    /// AI tilt that week is refused (twenty points a week), as is a tilt of the organisation itself (a board act); <c>trade org show</c>
    /// is served. The chart, the three <c>tilted</c> events and the <c>envelope-tilt</c> decisions read back, and every refusal wrote
    /// nothing. No model.
    /// </summary>
    [Fact]
    public async Task A_puppet_division_head_tilts_its_teams_three_shares()
    {
        const string head = "pos-7117dd000001", division = "div-7117dd00", team = "team-7117dd01";
        await using var life = await PuppetLife.Begin("org-tilt", seed: db =>
        {
            SeedDivision(division, head)(db);
            db.Write(_ =>
            {
                using var c = db.Cmd("""
                    INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, envelope_share, created_at)
                    VALUES($t, $d, 'team', NULL, 'active', '0.2', '2026-10-23T12:00:00.0000000Z')
                    """, ("$t", team), ("$d", division));
                return c.ExecuteNonQuery();
            });
        });
        var org = new OrgStore(life.Host.Db);
        var forecasts = Word(new[] { new { predicate = ForecastVocabulary.UnitAdmissible, target = ForecastVocabulary.Admitted,
            by = DateTimeOffset.UtcNow.AddDays(45).ToString("O"), probability = 0.4m } });
        life.Script(head,
        [
            $"trade org tilt {team} ai 0.35 \"the skew team earns more of the division's room\" --forecasts {forecasts}",
            $"trade org tilt {team} trials 0.2 \"a fifth of a campaign's trials\" --forecasts {forecasts}",
            $"trade org tilt {team} verdicts 0.2 \"the division delegates a fifth of the judgements\" --forecasts {forecasts}",
            $"trade org tilt {team} ai 0.5 \"more again this week\" --forecasts {forecasts}",
            $"trade org tilt {OrgIds.Root} trials 0.5 \"the organisation itself\" --forecasts {forecasts}",
            "trade org show", "signal tilted", "usage 100 0 10", "final Tilted."
        ]);
        life.Wake(head);
        await life.RunUntil(() => life.Reached("tilted") && life.AttemptsOf(head).Any(a => a.State == AiAttemptState.ENDED),
            "the head's puppet tilted");

        var steps = TradeSteps(life, head, 1);
        Assert.Equal(6, steps.Count);
        for (var i = 0; i < 3; i++) Assert.True((int?)steps[i]["exit"] == 0, $"tilt {i} was refused: {Said(steps[i])}");
        Assert.True((int?)steps[3]["exit"] != 0, $"a second AI tilt that week was served: {Said(steps[3])}");
        Assert.Contains("in the last seven days", Said(steps[3]), StringComparison.Ordinal);
        Assert.True((int?)steps[4]["exit"] != 0, $"a tilt of the organisation was served: {Said(steps[4])}");
        Assert.Contains("a two-seat board act", Said(steps[4]), StringComparison.Ordinal);
        Assert.True((int?)steps[5]["exit"] == 0, $"org show was refused: {Said(steps[5])}");

        var unit = org.Units().Single(u => u.Id == team);
        Assert.Equal(("0.35", "0.2", "0.2"), (unit.EnvelopeShare, unit.TrialShare, unit.VerdictShare));
        Assert.Equal(3, org.DecisionsOf(head).Count(d => d.Kind == OrgDecisionKind.EnvelopeTilt));
        var tilted = life.Host.Db.Read(_ =>
        {
            using var c = life.Host.Db.Cmd("SELECT COUNT(*) FROM org_event WHERE kind = $k AND decider = $h", ("$k", OrgEventKind.Tilted), ("$h", head));
            return Convert.ToInt32(c.ExecuteScalar());
        });
        Assert.Equal(3, tilted);
        output.WriteLine($"life (org-tilt): {life.Counts()}");
    }
}
