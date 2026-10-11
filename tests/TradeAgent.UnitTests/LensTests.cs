using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
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

    // ---- the registry --------------------------------------------------------------------------------------------

    static string NewTape() => Path.Combine(TestEnv.Home, $"lens-tape-{Guid.NewGuid():n}.db");

    static string FileSha(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static string? Scalar(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static void Exec(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- (c) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) THE SAME BYTES REGISTER ONCE AND NEW BYTES ARE THE NEXT VERSION. A lens proposed twice — the second time spelled
    /// differently — is one row, the first registration standing; a change to its state alone, then to its questions, is
    /// version 2 and then 3 of the same id, each with its own hash and the port's hash of its questions; and every number
    /// and hash is the store's. A role's ninth registration of the owner's day is refused, another role's is not, and no
    /// proposal takes a built-in's id or registers as the app.
    /// </summary>
    [Fact]
    public void The_same_bytes_register_once_and_new_bytes_are_the_next_version()
    {
        using var tape = new TapeStore(NewTape());
        var at = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

        var first = tape.RegisterLens(Valid, CouncilRoles.Research, "attempt-1", at);
        log.WriteLine($"v{first.Lens?.Version} {first.Lens?.LensSha} new={first.New} {first.Refusal}");
        Assert.True(first.New, first.Refusal);
        var lens = Lens.Parse(Valid).Lens!;
        Assert.Equal(("suite-headline", 1, lens.LensSha, lens.SchemaSha, lens.Canonical), (first.Lens!.Id, first.Lens.Version,
            first.Lens.LensSha, first.Lens.SchemaSha, first.Lens.Definition));
        Assert.Equal((CouncilRoles.Research, "attempt-1", Lens.ValidatorVersion, "okx-eea-announcements", "announcements"),
            (first.Lens.Proposer, first.Lens.Attempt, first.Lens.ValidatorVersion, first.Lens.TargetSource, first.Lens.TargetSeries));

        var respelled = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Valid));
        var again = tape.RegisterLens(respelled, CouncilRoles.Operations, "attempt-2", at);
        Assert.False(again.New);
        Assert.Equal(first.Lens, again.Lens);

        var state = tape.RegisterLens(With(l => l["state"]!["fields"]![0]!["max_chars"] = 301), CouncilRoles.Research, "attempt-3", at);
        Assert.True(state.New, state.Refusal);
        Assert.Equal(2, state.Lens!.Version);
        Assert.NotEqual(first.Lens.LensSha, state.Lens.LensSha);
        Assert.Equal(first.Lens.SchemaSha, state.Lens.SchemaSha);

        var asked = tape.RegisterLens(With(l => Q(l, "urgent")["instructions"] = "Does it take effect within an hour?"),
            CouncilRoles.Research, "attempt-4", at);
        Assert.Equal(3, asked.Lens!.Version);
        Assert.NotEqual(first.Lens.SchemaSha, asked.Lens.SchemaSha);

        // THE PROPOSER'S DAY: eight registrations, and the ninth refused in words, nothing written.
        for (var i = 4; i <= 8; i++)
            Assert.True(tape.RegisterLens(With(l => l["id"] = $"suite-{i}"), CouncilRoles.Research, null, at).New);
        var ninth = tape.RegisterLens(With(l => l["id"] = "suite-9"), CouncilRoles.Research, null, at);
        log.WriteLine("ninth: " + ninth.Refusal);
        Assert.Null(ninth.Lens);
        Assert.Contains($"at most {TapeStore.MaxLensProposalsPerDay} in one owner day", ninth.Refusal, StringComparison.Ordinal);
        Assert.True(tape.RegisterLens(With(l => l["id"] = "suite-9"), CouncilRoles.Operations, null, at).New);
        Assert.True(tape.RegisterLens(With(l => l["id"] = "suite-10"), CouncilRoles.Research, null, at.AddDays(1)).New);

        // NO PROPOSAL TAKES A BUILT-IN'S ID, NONE REGISTERS AS THE APP, AND NONE IS NOBODY'S.
        var builtIn = tape.RegisterLens(With(l => l["id"] = BuiltInLenses.OkxAnnouncement), CouncilRoles.Operations, null, at);
        Assert.Contains("the id of a lens this build ships", builtIn.Refusal, StringComparison.Ordinal);
        Assert.Contains("registers this build's own lenses", tape.RegisterLens(Valid, BuiltInLenses.Proposer, null, at).Refusal, StringComparison.Ordinal);
        Assert.Contains("names none", tape.RegisterLens(Valid, " ", null, at).Refusal, StringComparison.Ordinal);
        Assert.Contains("offers no way out", tape.RegisterLens(With(l => Q(l, "kind")["criteria"]!.AsObject().Remove("other")),
            CouncilRoles.Operations, null, at).Refusal, StringComparison.Ordinal);

        Assert.Equal(1 + 3 + 5 + 2, tape.Lenses().Count);
        Assert.Equal(first.Lens, tape.LensOf("suite-headline", 1));
    }

    // ---- (d) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) A BUILT-IN CHANGED UNDER ITS VERSION IS REFUSED AND THE TAPE UNTOUCHED. This build's built-in registers at open
    /// as the app's; a build whose built-in of the same id and version has other bytes is refused the tape in words
    /// naming both hashes and the version that would open it, the file byte for byte as it was; and the changed lens
    /// shipped as the next version opens it, beside the first.
    /// </summary>
    [Fact]
    public void A_built_in_changed_under_its_version_is_refused_and_the_tape_untouched()
    {
        var file = NewTape();
        using (var shipped = new TapeStore(file))
        {
            var row = Assert.Single(shipped.Lenses());
            log.WriteLine($"built-in {row.Id} v{row.Version} lens_sha {row.LensSha} schema_sha {row.SchemaSha} ({row.ValidatorVersion})");
            Assert.Equal((BuiltInLenses.OkxAnnouncement, 1, BuiltInLenses.Proposer), (row.Id, row.Version, row.Proposer));
            Assert.Equal(Lens.Parse(BuiltInLenses.OkxAnnouncementV1).Lens!.LensSha, row.LensSha);
            Assert.Equal(["addresses_automated_reader", "direction_for_holders", "event_type", "time_sensitivity"],
                row.Parsed!.Questions.Keys.Order(StringComparer.Ordinal).ToList());
        }

        // REOPENED BY THE SAME BUILD: nothing new.
        using (var again = new TapeStore(file)) Assert.Single(again.Lenses());
        var before = FileSha(file);

        var changed = BuiltInLenses.OkxAnnouncementV1.Replace("\"max_chars\":500", "\"max_chars\":400", StringComparison.Ordinal);
        Assert.NotEqual(BuiltInLenses.OkxAnnouncementV1, changed);
        var refused = Assert.Throws<InvalidOperationException>(() => new TapeStore(file, [new BuiltInLens(changed, 1)]));
        log.WriteLine(refused.Message);
        Assert.Contains("'okx-announcement' version 1", refused.Message, StringComparison.Ordinal);
        Assert.Contains(Lens.Parse(changed).Lens!.LensSha, refused.Message, StringComparison.Ordinal);
        Assert.Contains("never changes under its version", refused.Message, StringComparison.Ordinal);
        Assert.Contains("as version 2 opens it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, FileSha(file));
        Assert.Equal("1", Scalar(file, "SELECT COUNT(*) FROM lens"));

        // SHIPPED AS THE NEXT VERSION, IT OPENS — the first beside it, unchanged.
        using (var next = new TapeStore(file, [new BuiltInLens(BuiltInLenses.OkxAnnouncementV1, 1), new BuiltInLens(changed, 2)]))
        {
            Assert.Equal([1, 2], next.Lenses().Select(l => l.Version).Order().ToList());
            Assert.Equal(Lens.Parse(changed).Lens!.LensSha, next.LensOf(BuiltInLenses.OkxAnnouncement, 2)!.LensSha);
        }
    }

    // ---- (i) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (i) THE RUNG RERUNS CLEANLY AFTER A CRASH BEFORE ITS STAMP. A tape whose <c>lens</c> table and built-in row were
    /// written but whose version row still says 2 — and a tape at rung 2 that never had the table — each open at
    /// rung 3 with exactly one built-in row; the version row says 3.
    /// </summary>
    [Fact]
    public void The_rung_reruns_cleanly_after_a_crash_before_its_stamp()
    {
        var crashed = NewTape();
        using (var t = new TapeStore(crashed)) Assert.Equal(TapeStore.Schema, t.Version);
        Exec(crashed, "UPDATE tape_meta SET value='2' WHERE key='schema'");

        var rungTwo = NewTape();
        using (var t = new TapeStore(rungTwo)) Assert.Equal(3, t.Version);
        Exec(rungTwo, "DROP TABLE lens; UPDATE tape_meta SET value='2' WHERE key='schema'");

        foreach (var file in new[] { crashed, rungTwo })
        {
            using var reopened = new TapeStore(file);
            Assert.Equal(3, reopened.Version);
            var row = Assert.Single(reopened.Lenses());
            Assert.Equal(BuiltInLenses.OkxAnnouncement, row.Id);
            Assert.Equal("3", Scalar(file, "SELECT value FROM tape_meta WHERE key='schema'"));
        }
    }
}
