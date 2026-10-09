using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-decision-port item 3 — A PERCEPTION CALL IS THE APP'S OWN SPENDING, NEVER A COUNCIL ROLE'S TURN.
///
/// <para>The decision port charges every call to <c>ai_attempt</c> under <see cref="AppPrincipals.Perception"/>,
/// so its rows sit in the same ledger as the council's turns. Every reader of that ledger was written while
/// the only roles in it were Operations and Research, and three of them folded ANY role they did not know
/// into the chair's: the relay's fence would have published a file naming a perception call as the
/// Operations Director's work, the share refusal named the Operations Director's share, and the report's
/// recovery list called a lost perception call an Operations attempt. The role-less counts behind the card,
/// <c>ai_turns_today</c>, the Situation and the report counted a perception call as an AI turn.</para>
///
/// <para>What holds now: the fence compares a row's role EXACTLY (a NULL role is still the chair's history),
/// the refusal and the recovery list name perception in its own words, and the role-less counts skip the
/// app's own principals while the day's spent and reserved money keeps them — perception spends inside the
/// owner's one daily cap.</para>
/// </summary>
public class PerceptionReadersTests(ITestOutputHelper log)
{
    /// <summary>What one perception call reserves: 64,000 input tokens at 0.042 USD per million.</summary>
    const decimal Reserve = 0.002688m;

    /// <summary>What the answered call cost: 300 input tokens at the same price.</summary>
    const decimal Answered = 0.0000126m;

    /// <summary>What the chair's one turn cost.</summary>
    const decimal ChairTurn = 0.05m;

    sealed record Seeded(string AnsweredId, string UnreportedId, string LostId, string OpenId, string ChairId);

    static string NewId(string what) => $"decision-{what}-{Guid.NewGuid():n}"[..44];

    static AiAttempt Perception(string id, DateTimeOffset at) => new()
    {
        Id = id,
        StartedAt = at,
        Runtime = "typesafe-direct",
        RequestedModel = "jev-1.13.0",
        PricingBasis = "list price, read 2026-10-09",
        ReservedCost = Reserve,
        Role = AppPrincipals.Perception
    };

    /// <summary>
    /// TODAY'S LEDGER, as the port and the chair leave it: one perception call answered and settled, one
    /// that ended with no answer and kept its reservation, one a restart declared LOST, one still flying —
    /// and one ordinary turn of the chair's, so the counts the council DOES own are seen to survive.
    /// </summary>
    static Seeded Seed(Database db)
    {
        var at = TestEnv.LocalNoon();
        var store = new AiAttemptStore(db);
        var ids = new Seeded(NewId("answered"), NewId("unreported"), NewId("lost"), NewId("open"),
            $"turn-chair-{Guid.NewGuid():n}");

        store.Begin(Perception(ids.AnsweredId, at));
        Assert.True(store.End(ids.AnsweredId, 0, at.AddSeconds(1), 300, null, null, 20, null, "jev-1.13.0",
            Answered, null, null, "list price, read 2026-10-09"));

        // NO ANSWER: the reservation stands as the cost, and the row says its usage never arrived.
        store.Begin(Perception(ids.UnreportedId, at.AddMinutes(1)));
        Assert.True(store.End(ids.UnreportedId, 2, at.AddMinutes(1).AddSeconds(30), null, null, null, null, null,
            null, null, null, null));

        // A RESTART FOUND IT OPEN: LOST, and the reservation is its cost for good.
        store.Begin(Perception(ids.LostId, at.AddMinutes(2)));
        store.LoseOpen(at.AddMinutes(3));

        // STILL FLYING: the port holds its id in the live register until the call settles.
        store.Begin(Perception(ids.OpenId, at.AddMinutes(4)));

        store.Begin(new AiAttempt
        {
            Id = ids.ChairId,
            StartedAt = at.AddMinutes(5),
            Runtime = "codex",
            RequestedModel = "gpt-5.6-luna",
            ReservedCost = 0.5m,
            Role = CouncilRoles.Operations
        });
        Assert.True(store.End(ids.ChairId, 0, at.AddMinutes(6), 1000, 0, 0, 100, 0, "gpt-5.6-luna", ChairTurn,
            null, null));

        return ids;
    }

    /// <summary>
    /// (e) RED AT BASE ON A SEEDED ROW, in every reader at once: the fence, the refusal, the recovery list,
    /// the report's basis, the role-less counts, the card's label and line, and the status's
    /// <c>ai_turns_today</c>. Each reading is collected rather than asserted one at a time, so a reader that
    /// goes wrong again is named beside every other one that did.
    ///
    /// <para>Put <c>Fence</c> back on <c>CouncilRoles.Or</c> and the first two lines of the list come back: the
    /// chair's pass publishes the lost call's agenda and leaves the flying one's in place.</para>
    /// </summary>
    [Fact]
    public async Task A_perception_attempt_is_never_read_as_an_operations_turn()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var _gw = gw;
        var ids = Seed(db);
        var (from, to) = DailyReports.LocalDay(TestEnv.LocalNoon());
        var live = new LiveAttempts();
        live.Enter(ids.OpenId);

        var wrong = new List<string>();
        void Expect(bool held, string reading)
        {
            if (!held) wrong.Add(reading);
        }

        // ---- the fence: a file naming a perception call is quarantined in EVERY pass ------------------
        var root = Path.Combine(TestEnv.Home, $"perception-relay-{Guid.NewGuid():n}");
        string Home(string role) => WorkspaceBuilder.HomeOf(root, role);
        string Out(string role) => Path.Combine(Home(role), WorkspaceBuilder.OutDir);
        foreach (var role in CouncilRoles.All)
        {
            Directory.CreateDirectory(Path.Combine(Home(role), WorkspaceBuilder.InDir));
            Directory.CreateDirectory(Out(role));
        }

        string Named(string role, string attempt) => CouncilRelay.Pattern(CouncilRelay.KindFor(role)).Replace("*", attempt);
        var lostAgenda = Named(CouncilRoles.Operations, ids.LostId);
        var flyingAgenda = Named(CouncilRoles.Operations, ids.OpenId);
        var answeredReport = Named(CouncilRoles.Research, ids.AnsweredId);
        File.WriteAllText(Path.Combine(Out(CouncilRoles.Operations), lostAgenda), "line 1: a plan nobody wrote\n");
        File.WriteAllText(Path.Combine(Out(CouncilRoles.Operations), flyingAgenda), "line 1: a plan nobody wrote\n");
        File.WriteAllText(Path.Combine(Out(CouncilRoles.Research), answeredReport), "line 1: a report nobody wrote\n");

        var relay = new CouncilRelay(db, Home, live: live);
        relay.Run(CouncilRoles.Operations, "turn-of-this-pass");
        relay.Run(CouncilRoles.Research, "turn-of-this-pass");

        string[] Moved(string role)
        {
            var dir = Path.Combine(Out(role), CouncilRelay.QuarantineDir);
            return Directory.Exists(dir)
                ? [.. Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
                : [];
        }

        var published = new PublicationStore(db).By(CouncilRoles.Operations).Select(p => p.Attempt).ToList();
        Expect(published.Count == 0, $"fence: the chair's pass PUBLISHED {string.Join(", ", published)} as the Operations Director's work");
        Expect(Moved(CouncilRoles.Operations).Contains(flyingAgenda),
            "fence: the chair's pass LEFT a file naming a flying perception call where it was");
        Expect(Moved(CouncilRoles.Operations).Contains(lostAgenda), "fence: a file naming a lost perception call was not quarantined");
        Expect(Moved(CouncilRoles.Research).SequenceEqual([answeredReport]), "fence: Research's pass did not quarantine its file");
        Expect(new PublicationStore(db).By(CouncilRoles.Research).Count == 0, "fence: Research's pass published a perception call");

        // ---- the refusal names perception's own budget ---------------------------------------------------
        var refused = new AiAttemptStore(db).Begin(Perception(NewId("refused"), TestEnv.LocalNoon().AddMinutes(7)),
            admit: new AiAdmissionRule
            {
                From = from,
                To = to,
                Role = AppPrincipals.Perception,
                Cap = 5m,
                RoleCap = 0.001m,
                Reservation = Reserve,
                ResumesAt = to
            });
        Assert.False(refused.Admitted);
        Assert.Equal(AppPrincipals.Perception, refused.Ceiling);
        Expect(!refused.Refusal!.Contains("Operations", StringComparison.Ordinal)
               && refused.Refusal.Contains("perception", StringComparison.OrdinalIgnoreCase),
            $"refusal: \"{refused.Refusal}\"");

        // ---- the report's recovery list calls it a perception call, and its basis names a billed cost ----
        var report = gw.Reports.Compose(TestEnv.LocalNoon().AddHours(1));
        var perceptionLines = report.Recovery.Interrupted.Where(l => l.Contains("decision-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, perceptionLines.Count);
        foreach (var line in perceptionLines)
            Expect(line.StartsWith("Perception call ", StringComparison.Ordinal) && !line.Contains("Operations", StringComparison.Ordinal),
                $"recovery: \"{line}\"");
        Expect((report.Spending.Basis ?? "").Contains("perception", StringComparison.OrdinalIgnoreCase),
            $"report basis names no perception charge: \"{report.Spending.Basis}\"");

        // ---- the role-less counts skip it; the day's money keeps it ---------------------------------------
        var day = new AiAttemptStore(db).TotalsBetween(from, to);
        Expect(day is { Turns: 1, Unpriced: 0, Estimated: 0, Open: 0, Unreported: 0 }, $"day counts: {day}");
        Assert.Equal(ChairTurn + Answered + Reserve + Reserve, day.Spent);
        Assert.Equal(Reserve, day.Reserved);

        // Perception's own reading is still whole: it is what its budget is compared against. And the chair's is its own.
        Assert.Equal(new AiAttemptTotals(Answered + Reserve + Reserve, Reserve, 3, 0, 2, 1, 2),
            new AiAttemptStore(db).TotalsBetween(from, to, AppPrincipals.Perception));
        Assert.Equal(1, new AiAttemptStore(db).TotalsBetween(from, to, CouncilRoles.Operations).Turns);

        // ---- the card's label and line, read off the meter ------------------------------------------------
        var meter = new TurnMeter(db, cap: () => 5m, owner: () => new OwnerPrice(1m, 2m), live: live,
            recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"));
        var today = meter.Today;
        var card = DashboardPage.MissionCost(today);
        Expect(today is { Turns: 1, UnreportedTurns: 0, EstimatedTurns: 0, Estimated: null },
            $"meter: {today.Turns} turns, {today.UnreportedTurns} unreported, {today.EstimatedTurns} estimated, label \"{today.Estimated}\"");
        Expect(!card.Contains("never reported", StringComparison.Ordinal), $"card: \"{card}\"");
        Assert.Equal(AiAttemptState.LAUNCHED, new AiAttemptStore(db).Get(ids.OpenId)!.State);

        // ---- and the status the agent reads, through the app's own composition ----------------------------
        using var appDb = TestEnv.NewDb();
        Seed(appDb);
        var connector = new FakeConnector(new FakeBroker());
        await connector.ConnectAsync();
        var host = AppHost.Composed(appDb, connector, new AgentPresence());
        try
        {
            var status = await host.Gateway.StatusAsync();
            Expect(status.AiTurnsToday == 1, $"status: ai_turns_today {status.AiTurnsToday}");
        }
        finally
        {
            await host.Gateway.DisposeAsync();
        }

        foreach (var reading in wrong) log.WriteLine("WRONG " + reading);
        Assert.True(wrong.Count == 0, $"{wrong.Count} reader(s) read a perception call as the council's:\n  " + string.Join("\n  ", wrong));

        // AND THE REFUSAL IS PERCEPTION'S OWN SENTENCE, word for word.
        Assert.Equal(Labels.PerceptionBudgetReached, refused.Refusal);
    }
}
