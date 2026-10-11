using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core.Data;
using TradeAgent.Core.Decisions;
using TradeAgent.Core.Perception;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-lenses — A LENS IS A VERSIONED, HASHED QUESTION SCHEMA OVER THE TAPE'S OWN TEXT: validated in words, registered by
/// the app on <c>tape.db</c> rung 3, built into a state by code, and bound to every answer recorded under it.
///
/// <para>No request in this file reaches a vendor: every host is <see cref="FakeProvider"/> on a loopback port, no key
/// is real, and every definition is this suite's own or the build's R02-derived built-in.</para>
/// </summary>
public class LensTests(ITestOutputHelper log)
{
    /// <summary>A lens over OKX's announcements that every rule admits: this suite's own words.</summary>
    internal const string Valid = """
        {"id":"suite-headline",
         "target":{"source":"okx-eea-announcements","series":"announcements"},
         "state":{"fields":[{"path":"title","max_chars":300},{"path":"annType","max_chars":60}]},
         "questions":{
           "kind":{"type":"choice","instructions":"Which kind of notice is this?",
                   "criteria":{"listing":"A trading pair is added or removed","maintenance":null,"other":null}},
           "urgent":{"type":"noul","instructions":"Does the notice take effect within a day?",
                     "criteria":{"true":"Within a day","false":"Later, or no date"}},
           "reach":{"type":"score","instructions":"How many users does it touch?",
                    "criteria":["Almost none","Some","Most","All"]}}}
        """;

    /// <summary><see cref="Valid"/> with one change made to its parsed tree.</summary>
    internal static string With(Action<JsonObject> change, string from = Valid)
    {
        var node = JsonNode.Parse(from)!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    static JsonObject Q(JsonObject lens, string name) => lens["questions"]![name]!.AsObject();

    // ---- (b) ------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> Rules() => new()
    {
        { "a target that is a market row", With(l => l["target"]!["source"] = TapeSourceCatalog.Premium), "is not a text row of this build's tape" },
        { "a target series the row does not record", With(l => l["target"]!["series"] = "news"), "is not a text row of this build's tape" },
        { "a field not in the target's items", With(l => l["state"]!["fields"]![0]!["path"] = "body"), "is not a field of a 'okx-eea-announcements/announcements' item" },
        { "a field named twice", With(l => l["state"]!["fields"]![1]!["path"] = "title"), "names the field 'title' twice" },
        { "a field cap of zero", With(l => l["state"]!["fields"]![0]!["max_chars"] = 0), "'max_chars' is a whole number from 1" },
        { "a Choice with no escape option", With(l => Q(l, "kind")["criteria"]!.AsObject().Remove("other")), "offers no way out" },
        { "an instrument", With(l => l["instrument"] = "typesafe-direct"), "names what a lens cannot carry" },
        { "a host", With(l => l["target"]!["host"] = "127.0.0.1"), "names what a lens cannot carry" },
        { "a pin", With(l => l["pin"] = "jev-1.13.0"), "names what a lens cannot carry" },
        { "a key", With(l => l["api_key"] = "x"), "names what a lens cannot carry" },
        { "a budget", With(l => l["state"]!["budget"] = 1), "names what a lens cannot carry" },
        { "a model", With(l => Q(l, "urgent")["model"] = "jev-latest"), "names what a lens cannot carry" },
        { "the Test press's id", With(l => l["id"] = Lens.OwnerTestId), "is the account owner's Test press" },
        { "an id with a capital", With(l => l["id"] = "Suite"), "'id' is lower-case letters" },
        { "an unknown key", With(l => l["schedule"] = "hourly"), "'schedule' is not a key of a lens" },
        { "a missing key", With(l => l.Remove("state")), "a lens has no 'state'" },
        { "over 16 KiB", With(l => Q(l, "urgent")["instructions"] = new string('w', Lens.MaxDefinitionBytes)), "a lens holds at most 16,384 bytes" },
        { "a Choice of one option the port refuses", With(l => Q(l, "kind")["criteria"] = new JsonObject { ["other"] = null }), "has 1 options; a Choice has between 2 and 255" },
        { "a Score of eleven levels the port refuses", With(l => Q(l, "reach")["criteria"] = new JsonArray([.. Enumerable.Range(0, 11).Select(i => (JsonNode)$"level {i}")])), "has 11 levels; a Score has between 2 and 10" },
        { "a question name the port refuses", With(l => { var q = Q(l, "urgent").DeepClone(); l["questions"]!.AsObject().Remove("urgent"); l["questions"]!["Urgent"] = q; }), "'Urgent' is not a question name" },
        { "no question", With(l => l["questions"] = new JsonObject()), "a call asks at least one question" },
        { "a declared state over the strictest budget", With(l => { l["state"]!["fields"]![0]!["max_chars"] = 4000; l["state"]!["fields"]![1]!["max_chars"] = 4000; }), "at its declared caps, the state and" },
        { "not JSON", "{\"id\":", "does not read as one" }
    };

    /// <summary>
    /// (b) A LENS IS REFUSED IN WORDS FOR EACH RULE, and the valid one beside them parses — each refusal the reason, and
    /// never a lens with it. The port's own refusals come in the port's words, checked against the strictest built-in
    /// instrument, declared state cap included.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rules))]
    public void A_lens_is_refused_in_words_for_each_rule(string what, string definition, string words)
    {
        Assert.True(Lens.Parse(Valid).Ok, Lens.Parse(Valid).Why);

        var parsed = Lens.Parse(definition);
        log.WriteLine($"{what}: {parsed.Why}");
        Assert.False(parsed.Ok, $"{what} was admitted");
        Assert.Null(parsed.Lens);
        Assert.Contains(words, parsed.Why, StringComparison.Ordinal);
    }

    // ---- (g) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) NO LENS TARGETS ANYTHING BUT THE TAPE'S TEXT ROWS. The genome bank, the research ledger, an agent's traces, a
    /// market row and GDELT's file records are each refused; the targets are exactly this build's OKX announcements and
    /// GDELT's kept items, with the fields their payloads carry — so a lens over the moat cannot be written at all.
    /// </summary>
    [Fact]
    public void No_lens_targets_anything_but_the_tapes_text_rows()
    {
        var targets = Lens.Targets();
        log.WriteLine(string.Join(", ", targets.Select(t => $"{t.Source}/{t.Series} ({t.Fields.Count} fields)")));
        Assert.Equal(
            [$"{TapeSourceCatalog.OkxEeaAnnouncements}/announcements", $"{GdeltGkg.Source}/{GdeltGkg.ItemsSeries}"],
            targets.Select(t => $"{t.Source}/{t.Series}").Order(StringComparer.Ordinal).Reverse().ToList());
        Assert.Equal(Lens.OkxAnnouncementFields, targets.Single(t => t.Source == TapeSourceCatalog.OkxEeaAnnouncements).Fields);
        Assert.Equal(GdeltGkg.Fields, targets.Single(t => t.Source == GdeltGkg.Source).Fields);

        (string Source, string Series)[] elsewhere =
        [
            ("genome-bank", "versions"), ("research-ledger", "entries"), ("agent-traces", "turns"),
            (TapeSourceCatalog.Premium, "premium-index"), (TapeSourceCatalog.HyperliquidAssetCtxs, "asset-ctxs"),
            (GdeltGkg.Source, GdeltGkg.BatchSeries), ("workspace", "inbox")
        ];
        foreach (var (source, series) in elsewhere)
        {
            var parsed = Lens.Parse(With(l =>
            {
                l["target"] = new JsonObject { ["source"] = source, ["series"] = series };
                l["state"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["path"] = "text", ["max_chars"] = 100 }) };
            }));
            log.WriteLine($"{source}/{series}: {parsed.Why}");
            Assert.False(parsed.Ok, $"a lens over {source}/{series} was admitted");
            Assert.Contains("is not a text row of this build's tape", parsed.Why, StringComparison.Ordinal);
        }

        // GDELT's kept items are a target, and a lens over them reads only their 27 fields.
        var gdelt = Lens.Parse(With(l =>
        {
            l["target"] = new JsonObject { ["source"] = GdeltGkg.Source, ["series"] = GdeltGkg.ItemsSeries };
            l["state"] = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["path"] = "V2ENHANCEDTHEMES", ["max_chars"] = 800 }) };
        }));
        Assert.True(gdelt.Ok, gdelt.Why);
    }

    /// <summary>A lens's two hashes: the whole definition's, one spelling per meaning, and the port's over its questions.</summary>
    [Fact]
    public void A_lens_hashes_its_whole_definition_and_its_questions_apart()
    {
        var lens = Lens.Parse(Valid).Lens!;
        var respelled = Lens.Parse(JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(With(l =>
        {
            var fields = l["state"]!["fields"]!.AsArray();
            var first = fields[0]!.DeepClone();
            fields.RemoveAt(0);
            fields.Add(first);
        })))).Lens!;
        Assert.Equal(lens.LensSha, respelled.LensSha);
        Assert.Equal(lens.Canonical, respelled.Canonical);

        var capped = Lens.Parse(With(l => l["state"]!["fields"]![0]!["max_chars"] = 301)).Lens!;
        Assert.NotEqual(lens.LensSha, capped.LensSha);
        Assert.Equal(lens.SchemaSha, capped.SchemaSha);
        Assert.Equal(SchemaRef.Of(lens.Id, 1, lens.Questions).Sha, lens.SchemaSha);
    }
}
