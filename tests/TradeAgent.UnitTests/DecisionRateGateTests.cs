using System.Globalization;
using System.Net;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Decisions;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-decision-card item 1 — THE HOSTS' RATE LIMITS, ENFORCED BEFORE THE PORT IS EVER CALLED. A call over the instrument's
/// requests or tokens a second, over TradeAgent's own bound where no rate is documented, or inside the hold a host's 429,
/// 529 or 402 put on it, is REFUSED in words — the limit, whose it is, until when — before its key is read or anything is
/// reserved, and nothing waits.
///
/// <para>Every test here was RED first, written against the wire as it stood: the 81st call in one second was sent and
/// reserved; two calls in flight at once were both sent; the call after a 429 asking for thirty seconds was sent ten
/// seconds later; ten calls in one second through OpenRouter were all sent. They were then moved to the gate's signature
/// — a gate of their own, as <c>live</c> is — with their names kept.</para>
///
/// <para>Every host is <see cref="FakeProvider"/> on loopback and every instant is the test's own: a fixed "second" the
/// clock is moved through by hand, so a rolling second is exactly what the test says it is on any machine.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class DecisionRateGateTests(ITestOutputHelper log) : IDisposable
{
    /// <summary>A pretend key nobody else could have produced.</summary>
    readonly string _pasted = $"not-a-real-decision-key-{Guid.NewGuid():n}";

    public void Dispose()
    {
        if (File.Exists(DecisionInstruments.OverridePath)) File.Delete(DecisionInstruments.OverridePath);
    }

    /// <summary>The fake second every test starts in.</summary>
    static readonly DateTimeOffset Second = new(2026, 10, 10, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A wire on the test's own gate and clock, with room in the budget for every call these tests make.</summary>
    static TypeSafeWire Wire(DecisionInstrument instrument, HarnessKey key, DecisionPortTests.Rig rig, DecisionRateGate gate,
        Func<DateTimeOffset> now) =>
        new(instrument, key, rig.Db, () => rig.Tape, () => 50m, () => 10m, rig.Live, () => "USD",
            requestTimeout: TimeSpan.FromSeconds(10), now: now, gate: gate);

    /// <summary>Every launch-ledger row the rig holds: a refused call writes none.</summary>
    static int Rows(DecisionPortTests.Rig rig) => new AiAttemptStore(rig.Db).Between(Second.AddDays(-1), Second.AddDays(1)).Count;

    static readonly DecisionInstrument TypeSafe = DecisionInstruments.BuiltIn().Single(i => i.Id == DecisionInstruments.TypeSafeDirect);

    static DecisionRequest Ask(string instrument = DecisionInstruments.TypeSafeDirect) => DecisionPortTests.Ask(instrument);

    // ---- (a) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A CALL OVER A DOCUMENTED RATE IS REFUSED BEFORE ITS KEY IS READ OR ANYTHING RESERVED. Eighty calls in one second
    /// go — TypeSafe's "80 requests per second" — and the eighty-first is refused in words naming the limit, whose it is
    /// (the host's, read on a named day from a named page) and when the next can go. It is asked through a holder whose key
    /// was pasted for ANOTHER origin, which a read of the key would forget: the key is still held, so nobody asked for it.
    /// Nothing more reached the host and no row was written; a second later the next call goes.
    ///
    /// <para>Move the gate after <c>Reserve</c> in <c>TypeSafeWire.DecideAsync</c> and the 81st is reserved — and its
    /// key forgotten — before it is refused.</para>
    /// </summary>
    [Fact]
    public async Task A_call_over_a_documented_rate_is_refused_before_its_key_is_read_or_anything_reserved()
    {
        using var rig = new DecisionPortTests.Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", DecisionPortTests.TriageAnswers, 300, 20));
        var gate = new DecisionRateGate();
        var at = Second;
        var instrument = DecisionPortTests.PointedAt(host);
        Assert.Equal(80, instrument.Limits.RequestsPerSecond);
        using var wire = Wire(instrument, host.Holding(_pasted), rig, gate, () => at);

        for (var i = 0; i < 80; i++)
            Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);

        var elsewhere = new HarnessKey();
        elsewhere.Set(_pasted, "http://127.0.0.1:9");
        using var second = Wire(instrument, elsewhere, rig, gate, () => at);
        var over = await second.DecideAsync(Ask());
        log.WriteLine($"the 81st: {over.Status} — {over.Refusal}; requests {host.Requests.Count}, rows {Rows(rig)}");

        Assert.Equal(DecisionStatus.REFUSED, over.Status);
        Assert.Null(over.AttemptId);
        Assert.Equal($"Jev 1.13 at TypeSafe takes at most 80 requests a second (the host's documented limit, read "
                     + $"{TypeSafe.RatesReadOn} from {TypeSafe.RatesSource}), and 80 went in the last second, so this call was "
                     + $"not sent; nothing was reserved or charged. The next can go at {DecisionRateGate.When(Second.AddSeconds(1), Second)}.",
            over.Refusal);
        Assert.True(elsewhere.Held, "the 81st call read its key: a key pasted for another origin was forgotten");
        Assert.Equal(80, host.Requests.Count);
        Assert.Equal(80, Rows(rig));
        Assert.Equal(DecisionStatus.REFUSED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(80, host.Requests.Count);

        // A SECOND LATER, THE NEXT ONE GOES — the window rolls, it is not a ban.
        at = Second.AddSeconds(1);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(81, host.Requests.Count);
        Assert.Equal(81, Rows(rig));
    }

    // ---- (b) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (b) A CALL IS CHARGED AT ITS BOUND UNTIL ITS ANSWER SAYS OTHERWISE. TradeAgent cannot count the host's tokens, so a
    /// call in flight counts the most it could use — 65,536 tokens, what its money is reserved at — and a second call while
    /// it flies would pass TypeSafe's 100,000 a second: refused, in words, with nothing sent. Once the first is answered it
    /// counts the 320 tokens it used and the next goes in the same second. A call that came back with no usage keeps its
    /// bound for its second — unknown is never zero — and the second after, the window is clear.
    /// </summary>
    [Fact]
    public async Task A_call_is_charged_at_its_bound_until_its_answer_says_otherwise()
    {
        using var rig = new DecisionPortTests.Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", DecisionPortTests.TriageAnswers, 300, 20));
        using var hold = new ManualResetEventSlim();
        host.Arriving = () => hold.Wait(TimeSpan.FromSeconds(30));
        var gate = new DecisionRateGate();
        var at = Second;
        var instrument = DecisionPortTests.PointedAt(host);
        Assert.Equal(65_536L, instrument.ReservedTokens);
        using var wire = Wire(instrument, host.Holding(_pasted), rig, gate, () => at);

        var first = Task.Run(() => wire.DecideAsync(Ask()));
        for (var i = 0; i < 500 && host.Requests.Count < 1; i++) await Task.Delay(20);
        var second = Task.Run(() => wire.DecideAsync(Ask()));
        for (var i = 0; i < 150 && !second.IsCompleted && host.Requests.Count < 2; i++) await Task.Delay(20);
        hold.Set();
        var answers = await Task.WhenAll(first, second);
        log.WriteLine($"first {answers[0].Status}; second {answers[1].Status} — {answers[1].Refusal}");

        Assert.Equal(DecisionStatus.ANSWERED, answers[0].Status);
        Assert.Equal(DecisionStatus.REFUSED, answers[1].Status);
        Assert.Equal("Jev 1.13 at TypeSafe takes at most 100,000 tokens a second (the host's documented limit, read "
                     + $"{TypeSafe.RatesReadOn} from {TypeSafe.RatesSource}). TradeAgent counts a call in flight at the most it "
                     + "could use — 65,536 tokens — until its answer says what it used, and 65,536 are counted in the last "
                     + "second, so this call was not sent; nothing was reserved or charged. The next can go once the call in "
                     + "flight is answered.", answers[1].Refusal);
        Assert.Single(host.Requests);
        Assert.Equal(1, Rows(rig));

        // ANSWERED, IT COUNTS WHAT IT USED: 320 + 65,536 fits under 100,000, in the same second.
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(2, host.Requests.Count);

        // NO USAGE, NO MEASUREMENT: a 500 keeps its bound for its second, so the next call in it is refused.
        using var failing = new FakeProvider { AlwaysAnswer = HttpStatusCode.InternalServerError };
        using var failingWire = Wire(DecisionPortTests.PointedAt(failing), failing.Holding(_pasted), rig, gate, () => at);
        Assert.Equal(DecisionStatus.FAILED, (await failingWire.DecideAsync(Ask())).Status);
        var after = await wire.DecideAsync(Ask());
        log.WriteLine($"after a call with no usage: {after.Status} — {after.Refusal}");
        Assert.Equal(DecisionStatus.REFUSED, after.Status);
        Assert.Contains("66,176 are counted in the last second", after.Refusal, StringComparison.Ordinal);
        Assert.EndsWith($"The next can go at {DecisionRateGate.When(Second.AddSeconds(1), Second)}.", after.Refusal, StringComparison.Ordinal);
        Assert.Equal(2, host.Requests.Count);

        at = Second.AddSeconds(1);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(3, host.Requests.Count);
    }

    // ---- (c) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) A HOST'S RETRY-AFTER HOLDS THE NEXT CALL, IN WORDS. A 429 asking for thirty seconds is FAILED and keeps its
    /// reservation as its cost — whether it is billed is not documented — and every call in those thirty seconds is refused
    /// before anything is reserved, in words saying what the host answered, that the time is the host's, and until when; at
    /// thirty seconds the next one goes. A date is read as a date; an unreadable value counts as absent, and with none the
    /// app's own back-off applies — one second, doubling with each such answer in a row, to sixty — named as the app's, the
    /// run ended by an answer. A 529 and a 402 hold the same way; a 500 does not.
    ///
    /// <para>Make the wire ignore <c>Retry-After</c> and the call ten seconds after the 429 is held for one second only —
    /// and sent.</para>
    /// </summary>
    [Fact]
    public async Task A_hosts_retry_after_holds_the_next_call_in_words()
    {
        using var rig = new DecisionPortTests.Rig();
        using var host = new FakeProvider { AlwaysAnswer = HttpStatusCode.TooManyRequests, RetryAfter = "30" };
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", DecisionPortTests.TriageAnswers, 300, 20));
        var gate = new DecisionRateGate();
        var at = Second;
        using var wire = Wire(DecisionPortTests.PointedAt(host), host.Holding(_pasted), rig, gate, () => at);

        var refused = await wire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.FAILED, refused.Status);
        Assert.Equal("http-429", refused.ErrorClass);
        Assert.Equal(TypeSafe.Reservation, new AiAttemptStore(rig.Db).Get(refused.AttemptId!)!.Cost);

        at = Second.AddSeconds(10);
        var held = await wire.DecideAsync(Ask());
        log.WriteLine($"ten seconds on: {held.Status} — {held.Refusal}");
        Assert.Equal(DecisionStatus.REFUSED, held.Status);
        Assert.Equal($"Jev 1.13 at TypeSafe answered 429 (too many requests) at {DecisionRateGate.When(Second, Second)} and asked "
                     + $"to be left until {DecisionRateGate.When(Second.AddSeconds(30), Second)}. Nothing goes to Jev 1.13 at "
                     + $"TypeSafe before {DecisionRateGate.When(Second.AddSeconds(30), at)}; this call was not sent, and nothing "
                     + "was reserved or charged.", held.Refusal);
        Assert.Single(host.Requests);
        Assert.Equal(1, Rows(rig));

        at = Second.AddSeconds(30);
        Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(2, host.Requests.Count);

        // A DATE IS A DATE: held until the instant it names.
        host.RetryAfter = Second.AddSeconds(75).ToString("R", CultureInfo.InvariantCulture);
        at = Second.AddSeconds(60);
        Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        at = Second.AddSeconds(74);
        Assert.Contains($"asked to be left until {DecisionRateGate.When(Second.AddSeconds(75), Second.AddSeconds(60))}",
            (await wire.DecideAsync(Ask())).Refusal, StringComparison.Ordinal);
        at = Second.AddSeconds(75);
        host.RetryAfter = "soon";

        // UNREADABLE IS ABSENT: TradeAgent's own back-off, named as its own — and this is the fourth 429 in a row, so 8 s.
        Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(4, host.Requests.Count);
        at = Second.AddSeconds(82);
        var backedOff = await wire.DecideAsync(Ask());
        log.WriteLine($"after an unreadable Retry-After: {backedOff.Refusal}");
        Assert.Contains("without saying how long to wait, so TradeAgent waits 8 s — its own back-off, doubling with each such "
                        + "answer in a row up to 60 s, not the host's figure.", backedOff.Refusal, StringComparison.Ordinal);
        Assert.Equal(4, host.Requests.Count);

        // AN ANSWER ENDS THE RUN: the next 429 with no time waits one second, not sixteen.
        host.AlwaysAnswer = null;
        at = Second.AddSeconds(83);
        Assert.Equal(DecisionStatus.ANSWERED, (await wire.DecideAsync(Ask())).Status);
        host.AlwaysAnswer = HttpStatusCode.TooManyRequests;
        host.RetryAfter = null;
        at = Second.AddSeconds(84);
        Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        at = Second.AddSeconds(84.5);
        Assert.Contains("waits 1 s", (await wire.DecideAsync(Ask())).Refusal, StringComparison.Ordinal);
        at = Second.AddSeconds(85);
        Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        Assert.Equal(7, host.Requests.Count);

        // NEVER MORE THAN SIXTY SECONDS OF THE APP'S OWN: six more in a row would double past it.
        var t = 85.0;
        for (var i = 0; i < 6; i++)
        {
            t += 61;
            at = Second.AddSeconds(t);
            Assert.Equal(DecisionStatus.FAILED, (await wire.DecideAsync(Ask())).Status);
        }
        at = Second.AddSeconds(t + 59);
        Assert.Contains("waits 60 s", (await wire.DecideAsync(Ask())).Refusal, StringComparison.Ordinal);

        // A 529 AND A 402 HOLD AS A 429 DOES; A 500 DOES NOT.
        foreach (var (status, meaning) in new[] { ((HttpStatusCode)529, "overloaded"), (HttpStatusCode.PaymentRequired, "a credit limit") })
        {
            using var other = new FakeProvider { AlwaysAnswer = status, RetryAfter = "5" };
            using var otherWire = Wire(DecisionPortTests.PointedAt(other), other.Holding(_pasted), rig, new DecisionRateGate(), () => Second);
            Assert.Equal($"http-{(int)status}", (await otherWire.DecideAsync(Ask())).ErrorClass);
            var words = (await otherWire.DecideAsync(Ask())).Refusal;
            Assert.Contains($"answered {(int)status} (", words, StringComparison.Ordinal);
            Assert.Contains(meaning, words, StringComparison.Ordinal);
            Assert.Single(other.Requests);
        }

        using var broken = new FakeProvider { AlwaysAnswer = HttpStatusCode.InternalServerError, RetryAfter = "30" };
        using var brokenWire = Wire(DecisionPortTests.PointedAt(broken), broken.Holding(_pasted), rig, new DecisionRateGate(), () => at);
        Assert.Equal(DecisionStatus.FAILED, (await brokenWire.DecideAsync(Ask())).Status);
        at = at.AddSeconds(1);
        Assert.Equal(DecisionStatus.FAILED, (await brokenWire.DecideAsync(Ask())).Status);
        Assert.Equal(2, broken.Requests.Count);
    }

    // ---- (c2) -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c2) THE HOLD SURVIVES A BODY THE WIRE CANNOT READ. A 429 asking for thirty seconds whose page is larger than the
    /// wire ever reads (<see cref="TypeSafeWire.MaxAnswerBytes"/>) is still FAILED <c>http-429</c> — the status and its
    /// <c>Retry-After</c> are taken from the headers before any body, and a non-2xx's body is never read — so the call a
    /// second later is refused in words with no second request. A 2xx whose answer is over that bound is UNANSWERED with its
    /// status kept, and nothing of it is served.
    ///
    /// <para>Send buffered — the whole body read before the status is looked at — and the 429 becomes a lost answer with
    /// no status and no hold: the next call a second later is sent.</para>
    /// </summary>
    [Fact]
    public async Task A_hosts_hold_survives_a_body_the_wire_cannot_read()
    {
        using var rig = new DecisionPortTests.Rig();
        using var host = new FakeProvider
        {
            AlwaysAnswer = HttpStatusCode.TooManyRequests, RetryAfter = "30", ErrorBody = new byte[TypeSafeWire.MaxAnswerBytes + 1]
        };
        var gate = new DecisionRateGate();
        var at = Second;
        using var wire = Wire(DecisionPortTests.PointedAt(host), host.Holding(_pasted), rig, gate, () => at);

        var failed = await wire.DecideAsync(Ask());
        log.WriteLine($"a 429 with a page over the bound: {failed.Status} {failed.ErrorClass} {failed.HttpStatus}");
        foreach (var mark in host.Marks) log.WriteLine(mark);
        Assert.Equal(DecisionStatus.FAILED, failed.Status);
        Assert.Equal("http-429", failed.ErrorClass);
        Assert.Equal(429, failed.HttpStatus);
        Assert.Equal(TypeSafe.Reservation, new AiAttemptStore(rig.Db).Get(failed.AttemptId!)!.Cost);

        at = Second.AddSeconds(1);
        var held = await wire.DecideAsync(Ask());
        log.WriteLine($"a second on: {held.Status} — {held.Refusal}");
        Assert.Equal(DecisionStatus.REFUSED, held.Status);
        Assert.Equal($"Jev 1.13 at TypeSafe answered 429 (too many requests) at {DecisionRateGate.When(Second, Second)} and asked "
                     + $"to be left until {DecisionRateGate.When(Second.AddSeconds(30), Second)}. Nothing goes to Jev 1.13 at "
                     + $"TypeSafe before {DecisionRateGate.When(Second.AddSeconds(30), at)}; this call was not sent, and nothing "
                     + "was reserved or charged.", held.Refusal);
        Assert.Single(host.Requests);
        Assert.Equal(1, Rows(rig));

        // A 2xx WHOSE ANSWER IS OVER THE BOUND: unanswered, its status kept, nothing of it served.
        using var big = new FakeProvider();
        big.Answer(new string(' ', TypeSafeWire.MaxAnswerBytes)
                   + FakeProvider.SystemOne("jev-1.13.0", DecisionPortTests.TriageAnswers, 300, 20));
        using var bigWire = Wire(DecisionPortTests.PointedAt(big), big.Holding(_pasted), rig, new DecisionRateGate(), () => Second);
        var lost = await bigWire.DecideAsync(Ask());
        log.WriteLine($"a 200 over the bound: {lost.Status} {lost.ErrorClass} {lost.HttpStatus}");
        Assert.Equal(DecisionStatus.UNANSWERED, lost.Status);
        Assert.Equal(200, lost.HttpStatus);
        Assert.Empty(lost.Answers);
        Assert.Null(lost.InputTokens);
        Assert.Single(big.Requests);
    }

    // ---- (d) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) AN UNDOCUMENTED RATE IS THE APP'S OWN BOUND, NEVER NONE. OpenRouter's route documents no rate, so zero is not "no
    /// limit": TradeAgent applies its own, named as its own — one call in flight, one a second — and ten calls in one second
    /// are one answer and nine refusals, nothing sent for them. A call while another flies is refused too. A
    /// <c>decision-models.json</c> row that zeroes a rate TypeSafe documents is refused, naming the rate — a file cannot make
    /// a documented rate undocumented — and so is one that sets a rate OpenRouter does not document. The bound applies
    /// WHOLE: an instrument with either rate undocumented is held to one call in flight and one a second, whatever figure
    /// it carries for the other.
    ///
    /// <para>Give the bound back per rate — the app's one a second only where requests are undocumented — and an
    /// instrument with no tokens figure beside 80 requests a second answers ten calls in one second.</para>
    /// </summary>
    [Fact]
    public async Task An_undocumented_rate_is_the_apps_own_bound_never_none()
    {
        using var rig = new DecisionPortTests.Rig();
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("typesafe/jev-1.13-20260917", DecisionPortTests.TriageAnswers, 300, 20, id: "gen-1", cost: 0.0000126m));
        var gate = new DecisionRateGate();
        var at = Second;
        var openRouter = DecisionPortTests.PointedAt(host, DecisionInstruments.OpenRouterJev);
        Assert.Equal(0, openRouter.Limits.RequestsPerSecond);
        Assert.Equal(0, openRouter.Limits.TokensPerSecond);
        using var wire = Wire(openRouter, host.Holding(_pasted), rig, gate, () => at);

        var answers = new List<DecisionAnswer>();
        for (var i = 0; i < 10; i++) answers.Add(await wire.DecideAsync(Ask(DecisionInstruments.OpenRouterJev)));
        log.WriteLine(answers[1].Refusal);

        Assert.Equal(1, answers.Count(a => a.Status == DecisionStatus.ANSWERED));
        Assert.Equal(9, answers.Count(a => a.Status == DecisionStatus.REFUSED));
        Assert.Equal("No requests-a-second rate is documented for Jev 1.13 through OpenRouter, so TradeAgent applies its own "
                     + "bound — not a vendor's figure: 1 call in flight at a time and 1 a second. One went in the last second, "
                     + "so this call was not sent; nothing was reserved or charged. The next can go at "
                     + $"{DecisionRateGate.When(Second.AddSeconds(1), Second)}.", answers[1].Refusal);
        Assert.Single(host.Requests);
        Assert.Equal(1, Rows(rig));

        // ONE IN FLIGHT: a call while another is still being answered is refused, a second or more later.
        using var hold = new ManualResetEventSlim();
        host.Arriving = () => hold.Wait(TimeSpan.FromSeconds(30));
        at = Second.AddSeconds(2);
        var flying = Task.Run(() => wire.DecideAsync(Ask(DecisionInstruments.OpenRouterJev)));
        for (var i = 0; i < 500 && host.Requests.Count < 2; i++) await Task.Delay(20);
        at = Second.AddSeconds(4);
        var meanwhile = await wire.DecideAsync(Ask(DecisionInstruments.OpenRouterJev));
        hold.Set();
        Assert.Equal(DecisionStatus.ANSWERED, (await flying).Status);
        log.WriteLine(meanwhile.Refusal);
        Assert.Equal(DecisionStatus.REFUSED, meanwhile.Status);
        Assert.EndsWith("A call is in flight, so this one was not sent; nothing was reserved or charged. The next can go once it "
                        + "is answered.", meanwhile.Refusal, StringComparison.Ordinal);
        Assert.Equal(2, host.Requests.Count);

        // A FILE CANNOT MAKE A DOCUMENTED RATE UNDOCUMENTED: a row zeroing either of TypeSafe's rates is refused in words
        // naming the rate, and that instrument stopped — zero means "not documented", and TypeSafe documents both.
        foreach (var (field, rate, figure) in new[] { ("tokens_per_second", "tokens-a-second", 100_000), ("requests_per_second", "requests-a-second", 80) })
        {
            File.WriteAllText(DecisionInstruments.OverridePath, $$"""[{"id":"typesafe-direct","{{field}}":0}]""");
            var zeroing = DecisionInstruments.Read();
            foreach (var r in zeroing.Refused) log.WriteLine(r);
            Assert.Null(DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect));
            Assert.Contains(zeroing.Refused, r => r.StartsWith($"'typesafe-direct' in decision-models.json sets the {rate} rate its "
                                                               + $"host documents ({figure:N0}) to zero", StringComparison.Ordinal));
            Assert.NotNull(DecisionInstruments.Find(DecisionInstruments.OpenRouterJev));
        }
        File.Delete(DecisionInstruments.OverridePath);

        // THE APP'S BOUND APPLIES WHOLE: an instrument with ANY rate undocumented — here its tokens, beside a documented 80
        // requests a second — is held to one call in flight AND one a second, never to eighty a second.
        using var typeSafeHost = new FakeProvider();
        typeSafeHost.Answer(FakeProvider.SystemOne("jev-1.13.0", DecisionPortTests.TriageAnswers, 300, 20));
        var pointed = DecisionPortTests.PointedAt(typeSafeHost);
        var half = pointed with { Limits = pointed.Limits with { TokensPerSecond = 0 } };
        Assert.Equal(80, half.Limits.RequestsPerSecond);
        using var halfWire = Wire(half, typeSafeHost.Holding(_pasted), rig, new DecisionRateGate(), () => Second);
        var halfAnswers = new List<DecisionAnswer>();
        for (var i = 0; i < 10; i++) halfAnswers.Add(await halfWire.DecideAsync(Ask()));
        log.WriteLine($"the second call in that second: {halfAnswers[1].Status} — {halfAnswers[1].Refusal}");
        Assert.Equal(1, halfAnswers.Count(a => a.Status == DecisionStatus.ANSWERED));
        Assert.Single(typeSafeHost.Requests);
        Assert.Equal("Jev 1.13 at TypeSafe carries no tokens-a-second rate, so TradeAgent applies its own bound — not a vendor's "
                     + "figure: 1 call in flight at a time and 1 a second. One went in the last second, so this call was not "
                     + $"sent; nothing was reserved or charged. The next can go at {DecisionRateGate.When(Second.AddSeconds(1), Second)}.",
            halfAnswers[1].Refusal);

        // AND A FILE CANNOT LIFT IT WITH A FIGURE: a rate OpenRouter does not document is refused, and the instrument stopped.
        File.WriteAllText(DecisionInstruments.OverridePath, """
            [{"id":"openrouter-jev","requests_per_second":1000,"tokens_per_second":10000000}]
            """);
        var read = DecisionInstruments.Read();
        foreach (var r in read.Refused) log.WriteLine(r);
        Assert.Null(DecisionInstruments.Find(DecisionInstruments.OpenRouterJev));
        Assert.Contains(read.Refused, r => r.StartsWith("'openrouter-jev' in decision-models.json sets a rate its host does not "
                                                        + "document; TradeAgent applies its own bound there", StringComparison.Ordinal));
        Assert.NotNull(DecisionInstruments.Find(DecisionInstruments.TypeSafeDirect));
    }
}
