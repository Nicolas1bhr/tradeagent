using System.Net;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-decision-port — BOUNDED DECISION MODELS BEHIND ONE PORT: refused before sending, the key only to its origin.
///
/// <para>Every host here is <see cref="FakeProvider"/> on <c>http://127.0.0.1:&lt;port&gt;</c> answering canned System One
/// JSON in TypeSafe's published shape. No request in this file reaches TypeSafe or OpenRouter, no key in it is real,
/// and the canned states, questions and numbers are this suite's own — never the vendor's examples.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class DecisionPortTests(ITestOutputHelper log) : IDisposable
{
    /// <summary>A pretend key nobody else could have produced, so finding it anywhere is evidence.</summary>
    readonly string _pasted = $"not-a-real-decision-key-{Guid.NewGuid():n}";

    public void Dispose()
    {
        if (File.Exists(DecisionInstruments.OverridePath)) File.Delete(DecisionInstruments.OverridePath);
    }

    static readonly IReadOnlyDictionary<string, DecisionQuestion> Triage = new Dictionary<string, DecisionQuestion>
    {
        ["event_type"] = DecisionQuestion.Choice("What kind of event does this item report?",
            new DecisionOption("listing", "A venue starts or stops trading an asset"),
            new DecisionOption("exploit", "Funds were taken through a flaw"),
            new DecisionOption("other")),
        ["names_btc"] = DecisionQuestion.Noul("Is the item specifically about bitcoin?", "It names bitcoin", "It does not"),
        ["urgency"] = DecisionQuestion.Score("How soon could this move a price?", "Not within a day", "Within hours", "Within minutes")
    };

    /// <summary>The canned answers to <see cref="Triage"/>, numbers spelled the way a host serves them.</summary>
    const string TriageAnswers = """
        {"event_type":{"type":"choice","choice":"listing","probabilities":{"listing":0.8125,"exploit":0.0625,"other":0.125},"confidence":0.71875},
         "names_btc":{"type":"noul","noul":0.0390625},
         "urgency":{"type":"score","score":1.15625,"legend":{"0":"Not within a day","1":"Within hours","2":"Within minutes"},
                    "probabilities":{"0":0.09375,"1":0.65625,"2":0.25},"confidence":0.6}}
        """;

    const string State = """{"headline":"A venue lists a new perpetual","source":"tape","tier":2}""";

    static DecisionRequest Ask(string instrument = DecisionInstruments.TypeSafeDirect,
        IReadOnlyDictionary<string, DecisionQuestion>? questions = null, string state = State) => new()
    {
        Instrument = instrument,
        Schema = SchemaRef.Of("item-triage", 1, questions ?? Triage),
        State = state,
        Questions = questions ?? Triage,
        Sources = ["tape_obs:41", "tape_fetch:7"]
    };

    /// <summary>A built-in instrument, every field as it ships but its address: a loopback listener.</summary>
    static DecisionInstrument PointedAt(FakeProvider host, string instrument = DecisionInstruments.TypeSafeDirect)
    {
        var shipped = DecisionInstruments.BuiltIn().Single(i => i.Id == instrument);
        return shipped with
        {
            Endpoint = instrument == DecisionInstruments.OpenRouterJev
                ? $"http://127.0.0.1:{host.Port}/api/v1/systemone"
                : $"{host.BaseUrl}/systemone"
        };
    }

    /// <summary>
    /// A wire over its own ledger, with the owner's cap and perception's budget as given — the same delegates the app
    /// hands it — and a live register of its own, as a restart would have.
    /// </summary>
    static TypeSafeWire Wire(DecisionInstrument instrument, HarnessKey key, Database db, decimal cap = 5m,
        decimal budget = 1m, LiveAttempts? live = null, TimeSpan? timeout = null) =>
        new(instrument, key, db, () => cap, () => budget, live ?? new LiveAttempts(), () => "USD",
            requestTimeout: timeout ?? TimeSpan.FromSeconds(10));

    static List<AiAttempt> Today(Database db)
    {
        var (from, to) = OwnerDay.Window(DateTimeOffset.Now);
        return new AiAttemptStore(db).Between(from, to);
    }

    /// <summary>What one call reserves: TypeSafe's "64k tokens" a request, read as 65,536, at 0.042 USD a million.</summary>
    const decimal Reservation = 65_536m * 0.042m / 1_000_000m;

    // ---- (b) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (b) EVERY STRUCTURAL LIMIT IS REFUSED BEFORE SENDING — the documented ones the host's own schema does not enforce
    /// (a Choice of at most 255 options, a Score of 2 to 10 levels), the budgets in bytes, an alias, a schema reference
    /// its questions do not hash to, a state that is not one JSON string, object or array. Each answers REFUSED in words,
    /// the host sees nothing, and the key is never even asked for.
    /// </summary>
    [Fact]
    public async Task Limits_are_refused_before_sending()
    {
        using var db = TestEnv.NewDb();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));
        var holder = host.Holding(_pasted);
        var instrument = PointedAt(host);
        using var wire = Wire(instrument, holder, db);
        using var alias = Wire(instrument with { RequestModel = "jev-latest" }, holder, db);
        using var openRouterAlias = Wire(instrument with { RequestModel = "~typesafe/jev-latest" }, holder, db);

        Dictionary<string, DecisionQuestion> One(DecisionQuestion q) => new() { ["only"] = q };
        var options = Enumerable.Range(0, 256).Select(i => new DecisionOption($"o{i}")).ToArray();

        var cases = new (string What, TypeSafeWire Through, DecisionRequest Request)[]
        {
            ("a Choice of one option", wire, Ask(questions: One(DecisionQuestion.Choice("Which?", new DecisionOption("a"))))),
            ("a Choice of 256 options", wire, Ask(questions: One(DecisionQuestion.Choice("Which?", options)))),
            ("a Score of one level", wire, Ask(questions: One(DecisionQuestion.Score("How much?", "low")))),
            ("a Score of eleven levels", wire, Ask(questions: One(DecisionQuestion.Score("How much?", [.. Enumerable.Range(0, 11).Select(i => $"level {i}")])))),
            ("no question", wire, Ask(questions: new Dictionary<string, DecisionQuestion>())),
            ("a question name with a capital", wire, Ask(questions: new Dictionary<string, DecisionQuestion> { ["Urgency"] = Triage["urgency"] })),
            ("a question with no instructions", wire, Ask(questions: One(DecisionQuestion.Noul(" ")))),
            ("a schema reference its questions do not hash to", wire, Ask() with { Schema = SchemaRef.Of("item-triage", 1, One(Triage["urgency"])) }),
            ("a state that is not JSON", wire, Ask(state: "a venue lists a new perpetual")),
            ("a state that is a bare number", wire, Ask(state: "42")),
            ("a state larger in bytes than the request budget", wire, Ask(state: JsonSerializer.Serialize(new string('x', 64_001)))),
            ("a state and question larger in bytes than their budget", wire, Ask(state: JsonSerializer.Serialize(new string('x', 32_001)))),
            ("too many source references", wire, Ask() with { Sources = [.. Enumerable.Range(0, 65).Select(i => $"tape_obs:{i}")] }),
            ("a call for another instrument", wire, Ask(DecisionInstruments.OpenRouterJev)),
            ("an alias", alias, Ask()),
            ("OpenRouter's alias", openRouterAlias, Ask())
        };

        foreach (var (what, through, request) in cases)
        {
            var answer = await through.DecideAsync(request);
            log.WriteLine($"{what}: {answer.Status} — {answer.Refusal}");
            Assert.True(answer.Status == DecisionStatus.REFUSED, $"{what} was not refused: {answer.Status}");
            Assert.False(string.IsNullOrWhiteSpace(answer.Refusal));
            Assert.Null(answer.AttemptId);
        }

        Assert.Empty(host.Requests);
        Assert.Empty(Today(db));
        Assert.True(holder.Held, "a refused call asked for the key");

        // THE CONTROL: the same wire and host, a call within every limit, reaches the host — once.
        var answered = await wire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.ANSWERED, answered.Status);
        Assert.Single(host.Requests);
    }

    // ---- (c) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) A CALL IS RESERVED IN THE MAIN DATABASE BEFORE IT IS SENT, AND A LOST ANSWER KEEPS IT. The host's own hook
    /// reads the launch ledger as the request arrives: the row is already there, LAUNCHED, perception's, carrying the
    /// whole reservation and held as flying. Answered, it settles on the answered id at the dated price; never answered,
    /// it keeps the reservation as its cost; refused with a 429, it is asked once and keeps it too.
    ///
    /// <para>Move the reservation after the send in <c>TypeSafeWire.DecideAsync</c> and the hook finds no row.</para>
    /// </summary>
    [Fact]
    public async Task A_call_is_reserved_in_the_main_database_before_it_is_sent_and_a_lost_answer_keeps_it()
    {
        using var db = TestEnv.NewDb();
        var live = new LiveAttempts();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers, input: 300, output: 20));

        var atArrival = new List<(AiAttempt Row, bool Flying)>();
        host.Arriving = () => atArrival.AddRange(Today(db).Select(r => (r, live.Holds(r.Id))));

        var instrument = PointedAt(host);
        Assert.Equal(Reservation, instrument.Reservation);
        using var wire = Wire(instrument, host.Holding(_pasted), db, live: live);
        var answered = await wire.DecideAsync(Ask());
        log.WriteLine($"answered: {answered.Status} on {answered.AttemptId}");

        var (row, flying) = Assert.Single(atArrival);
        Assert.Equal(answered.AttemptId, row.Id);
        Assert.Equal(AiAttemptState.LAUNCHED, row.State);
        Assert.Equal(AppPrincipals.Perception, row.Role);
        Assert.Equal(DecisionInstruments.TypeSafeDirect, row.Runtime);
        Assert.Equal("jev-1.13.0", row.RequestedModel);
        Assert.Equal(Reservation, row.ReservedCost);
        Assert.Equal(Sha256Hex.Of(host.Requests[0]), row.InputHash);
        Assert.True(flying, "the call was not held as flying while it was sent");

        var settled = new AiAttemptStore(db).Get(answered.AttemptId!)!;
        Assert.Equal(AiAttemptState.ENDED, settled.State);
        Assert.Equal("jev-1.13.0", settled.EffectiveModel);
        Assert.Equal(300L, settled.InputTokens);
        Assert.Equal(300m * 0.042m / 1_000_000m, settled.Cost);
        Assert.Equal(instrument.PriceBasis, settled.PricingBasis);
        Assert.False(live.Holds(answered.AttemptId));

        // NEVER ANSWERED: the request went, the host kept it, the call timed out. The reservation is the cost.
        using var silent = new FakeProvider(answers: false);
        using var lostWire = Wire(PointedAt(silent), silent.Holding(_pasted), db, live: live, timeout: TimeSpan.FromSeconds(2));
        var lost = await lostWire.DecideAsync(Ask());
        log.WriteLine($"lost: {lost.Status} {lost.ErrorClass}");
        Assert.Equal(DecisionStatus.UNANSWERED, lost.Status);
        Assert.Equal("timeout", lost.ErrorClass);
        Assert.Single(silent.Requests);
        var kept = new AiAttemptStore(db).Get(lost.AttemptId!)!;
        Assert.Equal(AiAttemptState.ENDED, kept.State);
        Assert.Equal(Reservation, kept.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, kept.UnpricedReason);
        Assert.Null(kept.InputTokens);
        Assert.False(live.Holds(lost.AttemptId));

        // A HOST THAT SAYS "TOO MANY": asked once on the reservation, never twice, and the reservation stands.
        using var busy = new FakeProvider { AlwaysAnswer = HttpStatusCode.TooManyRequests };
        using var busyWire = Wire(PointedAt(busy), busy.Holding(_pasted), db, live: live);
        var failed = await busyWire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.FAILED, failed.Status);
        Assert.Equal("http-429", failed.ErrorClass);
        Assert.Single(busy.Requests);
        Assert.Equal(Reservation, new AiAttemptStore(db).Get(failed.AttemptId!)!.Cost);

        var (from, to) = OwnerDay.Window(DateTimeOffset.Now);
        Assert.Equal(300m * 0.042m / 1_000_000m + Reservation + Reservation,
            new AiAttemptStore(db).TotalsBetween(from, to, AppPrincipals.Perception).Spent);
    }

    // ---- (d) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) PERCEPTION'S BUDGET AND THE OWNER'S DAILY CAP BIND IN ONE TRANSACTION, and no council share moves. The budget
    /// refuses a call it has no room for and the day refuses one IT has no room for, each naming its own ceiling with
    /// nothing sent; six calls racing for the room of one admit exactly one; and the chair's share and spending read the
    /// same with perception's money in the day as without it — perception narrows the day the council has left and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task The_perception_budget_and_the_daily_cap_bind_in_one_transaction()
    {
        using var db = TestEnv.NewDb();
        var live = new LiveAttempts();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers, input: 300, output: 20));
        var key = host.Holding(_pasted);
        var instrument = PointedAt(host);

        // A council turn already spent today.
        var store = new AiAttemptStore(db);
        var now = DateTimeOffset.Now;
        store.Begin(new AiAttempt { Id = $"turn-chair-{Guid.NewGuid():n}"[..44], StartedAt = now, ReservedCost = 0.5m, Role = CouncilRoles.Operations });
        var chair = Today(db).Single().Id;
        Assert.True(store.End(chair, 0, now, 1000, 0, 0, 100, 0, "gpt-5.6-luna", 0.25m, null, null));

        // THE BUDGET: room for less than one reservation.
        using (var tight = Wire(instrument, key, db, cap: 5m, budget: Reservation / 2m, live: live))
        {
            var refused = await tight.DecideAsync(Ask());
            log.WriteLine($"budget: {refused.Refusal}");
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Empty(host.Requests);
        }

        // THE DAY: the council has spent all but less than one reservation of the owner's cap.
        using (var spent = Wire(instrument, key, db, cap: 0.25m + Reservation / 2m, budget: 1m, live: live))
        {
            var refused = await spent.DecideAsync(Ask());
            log.WriteLine($"day: {refused.Refusal}");
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.DailySpendingLimitReached, refused.Refusal);
            Assert.Empty(host.Requests);
        }

        Assert.Single(Today(db));

        // SIX CALLS RACING FOR ROOM FOR ONE. The host holds the one it receives until every other call has been answered,
        // so the first call's reservation is open the whole time the others are deciding: exactly one goes.
        using var gate = new ManualResetEventSlim();
        host.Arriving = () => gate.Wait(TimeSpan.FromSeconds(60));
        using var race = Wire(instrument, key, db, cap: 5m, budget: Reservation * 1.5m, live: live);
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(() => race.DecideAsync(Ask()))).ToList();
        for (var i = 0; i < 600 && calls.Count(c => c.IsCompleted) < 5; i++) await Task.Delay(50);
        gate.Set();
        var answers = await Task.WhenAll(calls);

        foreach (var a in answers) log.WriteLine($"race: {a.Status} {a.Refusal}");
        Assert.Equal(1, answers.Count(a => a.Status == DecisionStatus.ANSWERED));
        Assert.Equal(5, answers.Count(a => a.Status == DecisionStatus.REFUSED));
        Assert.Single(host.Requests);

        // AND NO COUNCIL SHARE MOVED: the chair's own reading is its turn and its slice, perception's money is in the day.
        var meter = new TurnMeter(db, cap: () => 5m, live: live, recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"));
        var chairToday = meter.TodayFor(CouncilRoles.Operations);
        Assert.Equal(0.25m, chairToday.RoleSpent);
        Assert.Equal(0m, chairToday.RoleReserved);
        Assert.Equal(5m * 0.5m, chairToday.RoleCap);
        Assert.Equal(0.25m + 300m * 0.042m / 1_000_000m, meter.Today.Spent);
    }

    // ---- (f) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) THE KEY GOES ONLY TO THE ORIGIN IT WAS PASTED FOR. A key pasted for one listener, a call through an
    /// instrument pointed at another — the same host, another port — sends nothing at all and forgets the key; pasted
    /// for the listener the call goes to, it goes, once per call, and a match does not clear it.
    /// </summary>
    [Fact]
    public async Task The_key_goes_only_to_its_pasted_origin_and_is_never_written()
    {
        using var pastedFor = new FakeProvider();
        using var elsewhere = new FakeProvider();
        pastedFor.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));
        elsewhere.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));

        using var db = TestEnv.NewDb();
        var holder = pastedFor.Holding(_pasted);
        using (var wrong = Wire(PointedAt(elsewhere), holder, db))
        {
            var refused = await wrong.DecideAsync(Ask());
            log.WriteLine(refused.Refusal);
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.DecisionKeyPastedForAnotherOrigin(UrlOrigin.Of(pastedFor.BaseUrl)!, UrlOrigin.Of(elsewhere.BaseUrl)),
                refused.Refusal);
        }

        Assert.Empty(elsewhere.Requests);
        Assert.Empty(Today(db));
        Assert.False(holder.Held, "a key something tried to send elsewhere is forgotten");

        var again = pastedFor.Holding(_pasted);
        using var wire = Wire(PointedAt(pastedFor), again, db);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal([$"Bearer {_pasted}", $"Bearer {_pasted}"], pastedFor.Keys);
        Assert.True(again.Held);

        // WHAT WENT: the versioned id, the questions in the host's shape, the state canonical. Never an alias.
        using var sent = JsonDocument.Parse(pastedFor.Requests[0]);
        Assert.Equal("jev-1.13.0", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("choice", sent.RootElement.GetProperty("questions").GetProperty("event_type").GetProperty("type").GetString());
        Assert.Equal(3, sent.RootElement.GetProperty("questions").GetProperty("urgency").GetProperty("criteria").GetArrayLength());
        Assert.Equal("A venue lists a new perpetual", sent.RootElement.GetProperty("state").GetProperty("headline").GetString());

        // AND NO DECISION MODEL CAN BE GIVEN THE WORKER'S HOLDER.
        Assert.Throws<ArgumentException>(() => new TypeSafeWire(PointedAt(pastedFor), HarnessKey.Shared, db, () => 5m, () => 1m));
    }
}
