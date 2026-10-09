using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-decision-port — BOUNDED DECISION MODELS BEHIND ONE PORT: every call refused before sending or reserved before it
/// is sent, recorded before it settles, priced, and pinned; the key only to its origin; nothing of it on the pipe.
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

    const string Headline = "A venue lists a new perpetual";
    const string State = $$"""{"headline":"{{Headline}}","source":"tape","tier":2}""";

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

    /// <summary>A main database and a tape of the test's own, on files it names, and a live register as a restart has.</summary>
    sealed class Rig : IDisposable
    {
        bool _closed;

        public Rig()
        {
            DbFile = Path.Combine(TestEnv.Home, $"decision-db-{Guid.NewGuid():n}.db");
            TapeFile = Path.Combine(TestEnv.Home, $"decision-tape-{Guid.NewGuid():n}.db");
            Db = new Database(DbFile);
            Tape = new TapeStore(TapeFile);
        }

        public string DbFile { get; }
        public string TapeFile { get; }
        public Database Db { get; }
        public TapeStore Tape { get; }
        public LiveAttempts Live { get; } = new();

        /// <summary>
        /// Whether any byte of either file — or of a journal beside it — holds <paramref name="text"/>. Read after both are
        /// closed, so every page is in the file, and shared for reading and writing, which is how SQLite opens them.
        /// </summary>
        public bool Holds(string text)
        {
            Dispose();
            var needle = Encoding.UTF8.GetBytes(text);
            foreach (var file in new[] { DbFile, TapeFile }.SelectMany(f => new[] { f, f + "-wal", f + "-shm", f + "-journal" }))
            {
                if (!File.Exists(file)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (copy.ToArray().AsSpan().IndexOf(needle) >= 0) return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            Tape.Dispose();
            Db.Dispose();
        }
    }

    /// <summary>
    /// A wire over the rig's ledger and tape, with the owner's cap and perception's budget as given — delegates, as the
    /// app hands them — and the rig's live register.
    /// </summary>
    static TypeSafeWire Wire(DecisionInstrument instrument, HarnessKey key, Rig rig, decimal cap = 5m, decimal budget = 1m,
        TimeSpan? timeout = null, Func<TapeStore?>? tape = null) =>
        new(instrument, key, rig.Db, tape ?? (() => rig.Tape), () => cap, () => budget, rig.Live, () => "USD",
            requestTimeout: timeout ?? TimeSpan.FromSeconds(10));

    static List<AiAttempt> Today(Database db)
    {
        var (from, to) = OwnerDay.Window(DateTimeOffset.Now);
        return new AiAttemptStore(db).Between(from, to);
    }

    /// <summary>What one call reserves: TypeSafe's "64k tokens" a request, read as 65,536, at 0.042 USD a million.</summary>
    const decimal Reservation = 65_536m * 0.042m / 1_000_000m;

    // ---- (a) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) AN ANSWER FROM A MODEL OTHER THAN THE HOST'S PIN IS RECORDED UNPINNED — per instrument, because the two hosts
    /// name their answers differently: TypeSafe by its versioned id, OpenRouter by a DATED id it was never asked for, so
    /// OpenRouter's pin is that dated id and an answer naming its undated request id is not it. The pinned answer and the
    /// stray one are both recorded whole — who was asked and who answered, hashes and sources but never the state's text,
    /// the instants, the tokens, the list-price estimate and the host's billed figure apart, the answers unrounded — and
    /// only the verdict differs, reached by the tape's store from this build's own pin.
    ///
    /// <para>Take the pin comparison out of <c>TapeStore.RecordDecision</c> and the stray answer is recorded as evidence.</para>
    /// </summary>
    [Theory]
    [InlineData(DecisionInstruments.TypeSafeDirect, "jev-1.13.0", "jev-1.14.0")]
    [InlineData(DecisionInstruments.OpenRouterJev, "typesafe/jev-1.13-20260917", "typesafe/jev-1.13")]
    public async Task An_answer_from_a_model_other_than_the_hosts_pin_is_recorded_unpinned(string instrument, string pin, string other)
    {
        using var rig = new Rig();
        using var host = new FakeProvider();
        var billing = instrument == DecisionInstruments.OpenRouterJev;
        host.Answer(FakeProvider.SystemOne(pin, TriageAnswers, 300, 20,
                id: billing ? "gen-dec-pinned" : null, provider: billing ? "TypeSafe" : null, cost: billing ? 0.0000126m : null))
            .Answer(FakeProvider.SystemOne(other, TriageAnswers, 310, 20,
                id: billing ? "gen-dec-stray" : null, provider: billing ? "TypeSafe" : null, cost: billing ? 0.0000131m : null));

        var shipped = DecisionInstruments.BuiltIn().Single(i => i.Id == instrument);
        Assert.Equal(pin, shipped.Pin);
        Assert.False(DecisionInstruments.IsAlias(shipped.RequestModel));
        using var wire = Wire(PointedAt(host, instrument), host.Holding(_pasted), rig);

        var pinned = await wire.DecideAsync(Ask(instrument));
        var stray = await wire.DecideAsync(Ask(instrument));
        log.WriteLine($"{instrument}: {pinned.AnsweredModel} unpinned={pinned.Unpinned}; {stray.AnsweredModel} unpinned={stray.Unpinned}");

        Assert.Equal(DecisionStatus.ANSWERED, pinned.Status);
        Assert.Equal(DecisionStatus.ANSWERED, stray.Status);
        Assert.False(pinned.Unpinned);
        Assert.True(stray.Unpinned);

        var first = rig.Tape.RecordedCall(pinned.AttemptId!)!;
        var second = rig.Tape.RecordedCall(stray.AttemptId!)!;
        Assert.False(first.Unpinned);
        Assert.True(second.Unpinned);
        Assert.Equal(pin, second.Pin);

        var call = second.Call;
        Assert.Equal(instrument, call.Instrument);
        Assert.Equal(shipped.RequestModel, call.RequestedModel);
        Assert.Equal(other, call.AnsweredModel);
        Assert.Equal(UrlOrigin.Of(host.BaseUrl), second.Origin);
        Assert.Equal(DecisionStatus.ANSWERED, call.Status);
        Assert.Equal(SchemaRef.Of("item-triage", 1, Triage), call.Schema);
        Assert.Equal(TapeJson.Sha256(TapeJson.Canonical(State)), call.StateSha256);
        Assert.Equal(["tape_obs:41", "tape_fetch:7"], call.Sources);
        Assert.Equal(310L, call.InputTokens);
        Assert.Equal(20L, call.OutputTokens);
        Assert.Equal(310m * 0.042m / 1_000_000m, call.EstimatedCost);
        Assert.Equal(200, call.HttpStatus);
        Assert.True(call.LatencyMs >= 0 && call.EndedAt >= call.RequestedAt);
        Assert.Equal(TapeJson.Canonical(TriageAnswers), call.Answers);
        Assert.Contains("0.0390625", call.Answers, StringComparison.Ordinal);
        Assert.Equal(0.0390625, stray.Answers["names_btc"].Noul);
        Assert.Equal(0.65625, stray.Answers["urgency"].Probabilities["1"]);

        var settled = new AiAttemptStore(rig.Db).Get(stray.AttemptId!)!;
        Assert.Equal(other, settled.EffectiveModel);
        if (billing)
        {
            Assert.Equal("gen-dec-stray", call.HostResponseId);
            Assert.Equal(0.0000131m, call.BilledCost);
            Assert.Equal(TypeSafeWire.BilledBasis, call.PriceBasis);
            Assert.Equal(0.0000131m, settled.Cost);
            Assert.Equal(TypeSafeWire.BilledBasis, settled.PricingBasis);
        }
        else
        {
            Assert.Null(call.HostResponseId);
            Assert.Null(call.BilledCost);
            Assert.Equal(shipped.PriceBasis, call.PriceBasis);
            Assert.Equal(310m * 0.042m / 1_000_000m, settled.Cost);
        }

        // THE STATE IS A HASH AND ITS SOURCES: its text is on the wire and nowhere on this machine.
        Assert.Contains(Headline, host.Requests[1], StringComparison.Ordinal);
        Assert.False(rig.Holds(Headline), "the state's text was written to the ledger or the tape");
    }

    // ---- (b) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (b) EVERY STRUCTURAL LIMIT IS REFUSED BEFORE SENDING — the documented ones the host's own schema does not enforce
    /// (a Choice of at most 255 options, a Score of 2 to 10 levels), the budgets in bytes, an alias, a schema reference
    /// its questions do not hash to, a state that is not one JSON string, object or array — and a call with no tape open
    /// to record it on. Each answers REFUSED in words; the host sees nothing, nothing is reserved or recorded, and the key
    /// is never even asked for.
    /// </summary>
    [Fact]
    public async Task Limits_are_refused_before_sending()
    {
        using var rig = new Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));
        var holder = host.Holding(_pasted);
        var instrument = PointedAt(host);
        using var wire = Wire(instrument, holder, rig);
        using var alias = Wire(instrument with { RequestModel = "jev-latest" }, holder, rig);
        using var openRouterAlias = Wire(instrument with { RequestModel = "~typesafe/jev-latest" }, holder, rig);
        using var noTape = Wire(instrument, holder, rig, tape: () => null);

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
            ("OpenRouter's alias", openRouterAlias, Ask()),
            ("no tape open", noTape, Ask())
        };

        foreach (var (what, through, request) in cases)
        {
            var answer = await through.DecideAsync(request);
            log.WriteLine($"{what}: {answer.Status} — {answer.Refusal}");
            Assert.True(answer.Status == DecisionStatus.REFUSED, $"{what} was not refused: {answer.Status}");
            Assert.False(string.IsNullOrWhiteSpace(answer.Refusal));
            Assert.Null(answer.AttemptId);
        }

        Assert.Equal(TypeSafeWire.NoTape, (await noTape.DecideAsync(Ask())).Refusal);
        Assert.Empty(host.Requests);
        Assert.Empty(Today(rig.Db));
        Assert.Empty(rig.Tape.RecordedCalls());
        Assert.True(holder.Held, "a refused call asked for the key");

        // THE CONTROL: the same wire and host, a call within every limit, reaches the host — once.
        var answered = await wire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.ANSWERED, answered.Status);
        Assert.Single(host.Requests);
        Assert.Single(rig.Tape.RecordedCalls());
    }

    // ---- (c) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) A CALL IS RESERVED IN THE MAIN DATABASE BEFORE IT IS SENT, AND A LOST ANSWER KEEPS IT. The host's own hook
    /// reads the launch ledger as the request arrives: the row is already there, LAUNCHED, perception's, carrying the
    /// whole reservation and held as flying. Answered, it settles on the answered id at the dated price; never answered,
    /// it keeps the reservation as its cost; refused with a 429 it is asked once and keeps it too; and an answer that came
    /// while the tape was closing is not served and keeps it as well.
    ///
    /// <para>Move the reservation after the send in <c>TypeSafeWire.DecideAsync</c> and the hook finds no row.</para>
    /// </summary>
    [Fact]
    public async Task A_call_is_reserved_in_the_main_database_before_it_is_sent_and_a_lost_answer_keeps_it()
    {
        using var rig = new Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers, input: 300, output: 20));

        var atArrival = new List<(AiAttempt Row, bool Flying)>();
        host.Arriving = () => atArrival.AddRange(Today(rig.Db).Select(r => (r, rig.Live.Holds(r.Id))));

        var instrument = PointedAt(host);
        Assert.Equal(Reservation, instrument.Reservation);
        using var wire = Wire(instrument, host.Holding(_pasted), rig);
        var answered = await wire.DecideAsync(Ask());
        log.WriteLine($"answered: {answered.Status} on {answered.AttemptId}; at arrival {atArrival.Count} row(s)");

        var (row, flying) = Assert.Single(atArrival);
        Assert.Equal(answered.AttemptId, row.Id);
        Assert.Equal(AiAttemptState.LAUNCHED, row.State);
        Assert.Equal(AppPrincipals.Perception, row.Role);
        Assert.Equal(DecisionInstruments.TypeSafeDirect, row.Runtime);
        Assert.Equal("jev-1.13.0", row.RequestedModel);
        Assert.Equal(Reservation, row.ReservedCost);
        Assert.Equal(Sha256Hex.Of(host.Requests[0]), row.InputHash);
        Assert.True(flying, "the call was not held as flying while it was sent");

        var settled = new AiAttemptStore(rig.Db).Get(answered.AttemptId!)!;
        Assert.Equal(AiAttemptState.ENDED, settled.State);
        Assert.Equal("jev-1.13.0", settled.EffectiveModel);
        Assert.Equal(300L, settled.InputTokens);
        Assert.Equal(300m * 0.042m / 1_000_000m, settled.Cost);
        Assert.Equal(instrument.PriceBasis, settled.PricingBasis);
        Assert.False(rig.Live.Holds(answered.AttemptId));
        Assert.NotNull(rig.Tape.RecordedCall(answered.AttemptId!));

        // NEVER ANSWERED: the request went, the host kept it, the call timed out. The reservation is the cost.
        using var silent = new FakeProvider(answers: false);
        using var lostWire = Wire(PointedAt(silent), silent.Holding(_pasted), rig, timeout: TimeSpan.FromSeconds(2));
        var lost = await lostWire.DecideAsync(Ask());
        log.WriteLine($"lost: {lost.Status} {lost.ErrorClass}");
        Assert.Equal(DecisionStatus.UNANSWERED, lost.Status);
        Assert.Equal("timeout", lost.ErrorClass);
        Assert.Single(silent.Requests);
        var kept = new AiAttemptStore(rig.Db).Get(lost.AttemptId!)!;
        Assert.Equal(AiAttemptState.ENDED, kept.State);
        Assert.Equal(Reservation, kept.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, kept.UnpricedReason);
        Assert.Null(kept.InputTokens);
        Assert.False(rig.Live.Holds(lost.AttemptId));
        var lostRecord = rig.Tape.RecordedCall(lost.AttemptId!)!;
        Assert.Equal(DecisionStatus.UNANSWERED, lostRecord.Call.Status);
        Assert.True(lostRecord.Unpinned);
        Assert.Null(lostRecord.Call.Answers);

        // A HOST THAT SAYS "TOO MANY": asked once on the reservation, never twice, and the reservation stands.
        using var busy = new FakeProvider { AlwaysAnswer = HttpStatusCode.TooManyRequests };
        using var busyWire = Wire(PointedAt(busy), busy.Holding(_pasted), rig);
        var failed = await busyWire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.FAILED, failed.Status);
        Assert.Equal("http-429", failed.ErrorClass);
        Assert.Single(busy.Requests);
        Assert.Equal(Reservation, new AiAttemptStore(rig.Db).Get(failed.AttemptId!)!.Cost);
        Assert.Equal(429, rig.Tape.RecordedCall(failed.AttemptId!)!.Call.HttpStatus);

        // AN ANSWER THE TAPE COULD NOT TAKE — it closed while the call was in flight — is not served, and the call keeps
        // its reservation as though no answer had come.
        using var closing = new FakeProvider();
        closing.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers, input: 300, output: 20));
        TapeStore? open = rig.Tape;
        closing.Arriving = () => open = null;
        using var closingWire = Wire(PointedAt(closing), closing.Holding(_pasted), rig, tape: () => open);
        var unrecorded = await closingWire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.UNANSWERED, unrecorded.Status);
        Assert.Equal("not-recorded", unrecorded.ErrorClass);
        Assert.Empty(unrecorded.Answers);
        Assert.Null(rig.Tape.RecordedCall(unrecorded.AttemptId!));
        Assert.Equal(Reservation, new AiAttemptStore(rig.Db).Get(unrecorded.AttemptId!)!.Cost);

        var (from, to) = OwnerDay.Window(DateTimeOffset.Now);
        Assert.Equal(300m * 0.042m / 1_000_000m + 3 * Reservation,
            new AiAttemptStore(rig.Db).TotalsBetween(from, to, AppPrincipals.Perception).Spent);
    }

    // ---- (d) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) PERCEPTION'S BUDGET AND THE OWNER'S DAILY CAP BIND IN ONE TRANSACTION, and no council share moves. The budget
    /// refuses a call it has no room for and the day refuses one IT has no room for, each in its own words with nothing
    /// sent; six calls racing for the room of one admit exactly one; and the chair's share and spending read the same
    /// with perception's money in the day as without it — perception narrows the day the council has left and nothing
    /// else.
    /// </summary>
    [Fact]
    public async Task The_perception_budget_and_the_daily_cap_bind_in_one_transaction()
    {
        using var rig = new Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers, input: 300, output: 20));
        var key = host.Holding(_pasted);
        var instrument = PointedAt(host);

        // A council turn already spent today.
        var store = new AiAttemptStore(rig.Db);
        var now = DateTimeOffset.Now;
        var chair = $"turn-chair-{Guid.NewGuid():n}";
        store.Begin(new AiAttempt { Id = chair, StartedAt = now, ReservedCost = 0.5m, Role = CouncilRoles.Operations });
        Assert.True(store.End(chair, 0, now, 1000, 0, 0, 100, 0, "gpt-5.6-luna", 0.25m, null, null));

        // THE BUDGET: room for less than one reservation.
        using (var tight = Wire(instrument, key, rig, cap: 5m, budget: Reservation / 2m))
        {
            var refused = await tight.DecideAsync(Ask());
            log.WriteLine($"budget: {refused.Refusal}");
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.PerceptionBudgetReached, refused.Refusal);
            Assert.Empty(host.Requests);
        }

        // THE DAY: the council has spent all but less than one reservation of the owner's cap.
        using (var spent = Wire(instrument, key, rig, cap: 0.25m + Reservation / 2m, budget: 1m))
        {
            var refused = await spent.DecideAsync(Ask());
            log.WriteLine($"day: {refused.Refusal}");
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.DailySpendingLimitReached, refused.Refusal);
            Assert.Empty(host.Requests);
        }

        Assert.Single(Today(rig.Db));

        // SIX CALLS RACING FOR ROOM FOR ONE. The host holds the one it receives until every other call has been answered,
        // so the first call's reservation is open the whole time the others are deciding: exactly one goes.
        using var gate = new ManualResetEventSlim();
        host.Arriving = () => gate.Wait(TimeSpan.FromSeconds(60));
        using var race = Wire(instrument, key, rig, cap: 5m, budget: Reservation * 1.5m);
        var calls = Enumerable.Range(0, 6).Select(_ => Task.Run(() => race.DecideAsync(Ask()))).ToList();
        for (var i = 0; i < 600 && calls.Count(c => c.IsCompleted) < 5; i++) await Task.Delay(50);
        gate.Set();
        var answers = await Task.WhenAll(calls);

        foreach (var a in answers) log.WriteLine($"race: {a.Status} {a.Refusal}");
        Assert.Equal(1, answers.Count(a => a.Status == DecisionStatus.ANSWERED));
        Assert.Equal(5, answers.Count(a => a.Status == DecisionStatus.REFUSED));
        Assert.All(answers.Where(a => a.Status == DecisionStatus.REFUSED), a => Assert.Equal(Labels.PerceptionBudgetReached, a.Refusal));
        Assert.Single(host.Requests);

        // AND NO COUNCIL SHARE MOVED: the chair's own reading is its turn and its slice, perception's money is in the day.
        var meter = new TurnMeter(rig.Db, cap: () => 5m, live: rig.Live,
            recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"));
        var chairToday = meter.TodayFor(CouncilRoles.Operations);
        Assert.Equal(0.25m, chairToday.RoleSpent);
        Assert.Equal(0m, chairToday.RoleReserved);
        Assert.Equal(5m * 0.5m, chairToday.RoleCap);
        Assert.Equal(0.25m + 300m * 0.042m / 1_000_000m, meter.Today.Spent);
    }

    // ---- (f) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (f) THE KEY GOES ONLY TO THE ORIGIN IT WAS PASTED FOR, AND IS NEVER WRITTEN. A key pasted for one listener, a call
    /// through an instrument pointed at another — the same host, another port — sends nothing at all, reserves nothing
    /// and forgets the key; pasted for the listener the call goes to, it goes, once per call, and a match does not clear
    /// it. The ledger, the tape and their journals hold no byte of it, and a file that tries to move an instrument's
    /// address stops that instrument instead.
    /// </summary>
    [Fact]
    public async Task The_key_goes_only_to_its_pasted_origin_and_is_never_written()
    {
        using var rig = new Rig();
        using var pastedFor = new FakeProvider();
        using var elsewhere = new FakeProvider();
        pastedFor.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));
        elsewhere.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));

        var holder = pastedFor.Holding(_pasted);
        using (var wrong = Wire(PointedAt(elsewhere), holder, rig))
        {
            var refused = await wrong.DecideAsync(Ask());
            log.WriteLine(refused.Refusal);
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.DecisionKeyPastedForAnotherOrigin(UrlOrigin.Of(pastedFor.BaseUrl)!, UrlOrigin.Of(elsewhere.BaseUrl)),
                refused.Refusal);
        }

        Assert.Empty(elsewhere.Requests);
        Assert.Empty(Today(rig.Db));
        Assert.False(holder.Held, "a key something tried to send elsewhere is forgotten");

        var again = pastedFor.Holding(_pasted);
        using var wire = Wire(PointedAt(pastedFor), again, rig);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal([$"Bearer {_pasted}", $"Bearer {_pasted}"], pastedFor.Keys);
        Assert.True(again.Held);

        // WHAT WENT: the versioned id, the questions in the host's shape, the state canonical. Never an alias.
        using var sent = JsonDocument.Parse(pastedFor.Requests[0]);
        Assert.Equal("jev-1.13.0", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("choice", sent.RootElement.GetProperty("questions").GetProperty("event_type").GetProperty("type").GetString());
        Assert.Equal(3, sent.RootElement.GetProperty("questions").GetProperty("urgency").GetProperty("criteria").GetArrayLength());
        Assert.Equal(Headline, sent.RootElement.GetProperty("state").GetProperty("headline").GetString());

        // AND NO DECISION MODEL CAN BE GIVEN THE WORKER'S HOLDER.
        Assert.Throws<ArgumentException>(() => new TypeSafeWire(PointedAt(pastedFor), HarnessKey.Shared, rig.Db, () => rig.Tape,
            () => 5m, () => 1m));

        // NEVER WRITTEN: not a byte of it in the ledger, the tape, or a journal beside either.
        Assert.Equal(2, rig.Tape.RecordedCalls().Count);
        Assert.False(rig.Holds(_pasted), "the key was written to the ledger or the tape");

        // AND A FILE CANNOT MOVE WHERE A KEY GOES: a row naming an address stops its instrument rather than moving it.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","endpoint":"http://127.0.0.1:9/v1/systemone"}]
            """);
        var read = DecisionInstruments.Read();
        foreach (var r in read.Refused) log.WriteLine(r);
        Assert.Null(DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect));
        Assert.Contains(read.Refused, r => r.Contains("'endpoint'", StringComparison.Ordinal));
        Assert.Equal(DecisionInstruments.BuiltIn().Single(i => i.Id == DecisionInstruments.OpenRouterJev),
            DecisionInstruments.Find(DecisionInstruments.OpenRouterJev));
    }

    // ---- (g) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) NO PIPE OP REACHES THE DECISION PORT — NOR ITS BUDGET, ITS PIN OR ITS KEY. A GUARD in the shape of
    /// <c>OrgLedgerTests.No_pipe_op_writes_an_org_table</c>: no op and no <c>trade</c> verb names it and no op takes an
    /// argument that would address it; the pipe server's assembly does not reference the one the port's only
    /// implementation lives in and holds nothing of its kind; no gateway source names the budget, the key holder or the
    /// catalogue; and a file an agent can write cannot move a pin — the row is refused and the instrument stopped — while
    /// it may change a price, dated and sourced, and nothing else.
    /// </summary>
    [Fact]
    public void No_pipe_op_reaches_the_decision_port()
    {
        string[] words = ["decision", "perception", "jev", "typesafe", "systemone", "openrouter", "annotat"];
        foreach (var op in GatewaySchema.Ops())
        {
            foreach (var word in words)
            {
                Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, op.Cli, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var arg in op.Args)
                foreach (var word in words.Concat(["budget", "pin", "instrument", "key", "model", "question"]))
                    Assert.DoesNotContain(word, arg.Name, StringComparison.OrdinalIgnoreCase);
        }

        var pipe = typeof(GatewayPipeServer).Assembly;
        var references = pipe.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        log.WriteLine(string.Join(", ", references.Where(n => n.StartsWith("TradeAgent.", StringComparison.Ordinal))));
        Assert.DoesNotContain("TradeAgent.AgentRuntime", references);

        Type[] port = [typeof(IDecisionModel), typeof(DecisionInstrument), typeof(HarnessKey), typeof(DecisionRequest)];
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = pipe.GetTypes()
            .SelectMany(t => t.GetFields(all).Where(f => port.Contains(f.FieldType)).Select(f => $"{t.Name}.{f.Name}")
                .Concat(t.GetProperties(all).Where(p => port.Contains(p.PropertyType)).Select(p => $"{t.Name}.{p.Name}"))
                .Concat(t.GetMethods(all).Where(m => port.Contains(m.ReturnType) || m.GetParameters().Any(p => port.Contains(p.ParameterType)))
                    .Select(m => $"{t.Name}.{m.Name}()")))
            .ToList();
        Assert.True(holders.Count == 0, "the gateway's assembly can hold the decision port: " + string.Join(", ", holders));

        var root = RepoRoot();
        var named = Directory.GetFiles(Path.Combine(root, "src", "TradeAgent.Gateway"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains(nameof(TradeAgentSettings.PerceptionDailyBudget), StringComparison.Ordinal)
                            || text.Contains("PerceptionKey", StringComparison.Ordinal)
                            || text.Contains(nameof(DecisionInstruments), StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(named.Count == 0, "a gateway source names the port's budget, key or catalogue: " + string.Join(", ", named));

        // THE PIN IS THIS BUILD'S: a row that names one is refused, and the instrument it names is not called at all.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","pin":"jev-9.9.9","input_per_million":0.05,"priced_on":"2026-10-10","price_source":"https://prices.example/jev"}]
            """);
        var pinned = DecisionInstruments.Read();
        Assert.Contains(pinned.Refused, r => r.Contains("'pin'", StringComparison.Ordinal));
        Assert.Null(DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect));
        Assert.Equal("jev-1.13.0", DecisionInstruments.BuiltInPin(DecisionInstruments.TypeSafeDirect));

        // A PRICE MAY MOVE, DATED AND SOURCED — and only the price moved. Zero is no price, and is refused.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","input_per_million":0.05,"priced_on":"2026-10-10","price_source":"https://prices.example/jev"}]
            """);
        var moved = DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect)!;
        var shipped = DecisionInstruments.BuiltIn().Single(i => i.Id == DecisionInstruments.TypeSafeDirect);
        Assert.Equal(0.05m, moved.InputPerMillion);
        Assert.Equal(shipped with { InputPerMillion = 0.05m, PricedOn = "2026-10-10", PriceSource = "https://prices.example/jev" }, moved);

        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"typesafe-direct","input_per_million":0,"priced_on":"2026-10-10","price_source":"https://prices.example/jev"}]
            """);
        Assert.Null(DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect));

        // AND AN UNREADABLE FILE CALLS NOTHING.
        File.WriteAllText(DecisionInstruments.OverridePath, "[{");
        var unreadable = DecisionInstruments.Read();
        Assert.Empty(unreadable.Instruments);
        Assert.NotNull(unreadable.Unreadable);
    }

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "tests"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }
}
