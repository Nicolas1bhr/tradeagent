using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
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

    static TypeSafeWire Wire(DecisionInstrument instrument, HarnessKey key) =>
        new(instrument, key, requestTimeout: TimeSpan.FromSeconds(10));

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
        using var host = new FakeProvider();
        host.Answer(FakeProvider.SystemOne("jev-1.13.0", TriageAnswers));
        var holder = host.Holding(_pasted);
        var instrument = PointedAt(host);
        using var wire = Wire(instrument, holder);

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
            ("an alias", Wire(instrument with { RequestModel = "jev-latest" }, holder), Ask()),
            ("OpenRouter's alias", Wire(instrument with { RequestModel = "~typesafe/jev-latest" }, holder), Ask())
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
        Assert.True(holder.Held, "a refused call asked for the key");

        // THE CONTROL: the same wire and host, a call within every limit, reaches the host — once.
        var answered = await wire.DecideAsync(Ask());
        Assert.Equal(DecisionStatus.ANSWERED, answered.Status);
        Assert.Single(host.Requests);
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

        var holder = pastedFor.Holding(_pasted);
        using (var wrong = Wire(PointedAt(elsewhere), holder))
        {
            var refused = await wrong.DecideAsync(Ask());
            log.WriteLine(refused.Refusal);
            Assert.Equal(DecisionStatus.REFUSED, refused.Status);
            Assert.Equal(Labels.DecisionKeyPastedForAnotherOrigin(UrlOrigin.Of(pastedFor.BaseUrl)!, UrlOrigin.Of(elsewhere.BaseUrl)),
                refused.Refusal);
        }

        Assert.Empty(elsewhere.Requests);
        Assert.False(holder.Held, "a key something tried to send elsewhere is forgotten");

        var again = pastedFor.Holding(_pasted);
        using var wire = Wire(PointedAt(pastedFor), again);
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
        Assert.Throws<ArgumentException>(() => new TypeSafeWire(PointedAt(pastedFor), HarnessKey.Shared));
    }
}
