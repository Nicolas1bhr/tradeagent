using System.Buffers;
using System.Text;
using System.Text.Json;
using TradeAgent.Core.Data;
using TradeAgent.Core.Decisions;

namespace TradeAgent.Core.Perception;

/// <summary>One payload field a lens's state reads, and the most of it the state carries, in characters.</summary>
public sealed record LensField(string Path, int MaxChars);

/// <summary>
/// ONE TEXT ROW OF THIS BUILD'S TAPE CATALOGUE A LENS MAY LOOK AT, with the fields its items' payloads carry — the
/// only things a lens can be about (<c>U-lenses</c>; R10 #14: a lens over the genome bank, the ledger or an agent's
/// traces cannot be expressed, because no such row is here).
/// </summary>
public sealed record LensTarget(string Source, string Series, IReadOnlyList<string> Fields);

/// <summary>A parsed lens, or the refusal in words. Never both, never neither — <see cref="Features.FeatureParse"/>'s shape.</summary>
public sealed class LensParse
{
    LensParse(Lens? lens, string why)
    {
        Lens = lens;
        Why = why;
    }

    /// <summary>The lens, or null when the text was refused.</summary>
    public Lens? Lens { get; }

    /// <summary>The refusal, in words, or the empty string when the text parsed.</summary>
    public string Why { get; }

    public bool Ok => Lens is not null;

    internal static LensParse Yes(Lens lens) => new(lens, "");

    internal static LensParse No(string why) => new(null, why);
}

/// <summary>
/// A LENS: A VERSIONED, HASHED QUESTION SCHEMA OVER THE TAPE'S OWN TEXT (<c>U-lenses</c>; <c>docs/EDGE-FACTORY.md</c>
/// § 4.2; R02 § 6.7) — what to ask, about which item of which text row, and which of the item's fields the asking may
/// carry. Data, not code: an agent proposes one as text, <see cref="Parse"/> validates it, and the tape's store
/// registers it (<see cref="Db.TapeStore.RegisterLens"/>), numbering its version and hashing it itself.
///
/// <para><b>The parse is TOTAL and refuses in words.</b> An unknown key, a missing one, a target that is not a text row
/// of this build's catalogue, a field the target's items do not carry, a Choice with no escape option, a property
/// naming an instrument, a host, a pin, a key, a budget or a model, the owner's Test press's own id, a text over
/// <see cref="MaxDefinitionBytes"/> — and any question the port would refuse, in the port's own words
/// (<see cref="DecisionRequests.QuestionsRefusal"/>), checked against the STRICTEST built-in instrument with the
/// lens's declared state cap included (<see cref="DecisionRequests.BytesRefusal"/>). A lens that parsed is one every
/// built-in instrument takes.</para>
///
/// <para><b>It grants nothing.</b> It carries no instrument, budget, pin, key or schedule, so registering one runs
/// nothing and spends nothing; which lenses run is <c>U-annotator</c>'s, inside the owner's perception budget.</para>
///
/// <para><b>One meaning, one hash.</b> <see cref="Canonical"/> is the whole definition in one spelling — keys in
/// ordinal order, fields by path — and <see cref="LensSha"/> its SHA-256, so two spellings of one lens are one lens and
/// a change to ANY part of it, the state's fields included, is a different one. <see cref="SchemaSha"/> is the port's
/// <see cref="SchemaRef.Sha"/>, the questions' hash alone: both are recorded, because a call's record carries the
/// second and only the first says what the state was built from.</para>
/// </summary>
public sealed class Lens
{
    /// <summary>
    /// THE VERSION OF THESE RULES AND OF THE STATE <see cref="LensState"/> BUILDS FROM A LENS — written beside every
    /// registered lens, so a row says which validator admitted it and which builder its states came from. Raised by a
    /// change to either.
    /// </summary>
    public const string ValidatorVersion = "lens-v1";

    /// <summary>The most a definition's text may hold, in UTF-8 bytes.</summary>
    public const int MaxDefinitionBytes = 16 * 1024;

    /// <summary>The most fields one lens's state may read.</summary>
    public const int MaxFields = 16;

    /// <summary>The most characters one field may carry into a state.</summary>
    public const int MaxFieldChars = 4_000;

    /// <summary>
    /// THE OWNER'S TEST PRESS'S SCHEMA ID (<c>App/PerceptionCard.TestSchema</c>): its record says it was the owner's
    /// test, never a lens's evidence, so no lens may take it.
    /// </summary>
    public const string OwnerTestId = "owner-test";

    /// <summary>The words one of which every Choice must offer, so a model is never forced to pick a wrong option.</summary>
    public static readonly IReadOnlyList<string> EscapeOptions = ["other", "cannot_tell", "insufficient_information", "none_of_these"];

    /// <summary>
    /// What no property of a lens may name — the things a lens cannot carry because they are the owner's: an instrument,
    /// a host, a pin, a key, a budget or a model. Matched against every '_'- or '-'-separated word of every name.
    /// </summary>
    public static readonly IReadOnlyList<string> Forbidden = ["instrument", "host", "pin", "key", "budget", "model"];

    /// <summary>
    /// OKX's announcement item, as measured (<see cref="TapeSourceCatalog.Announcements"/>' <c>Measured</c>): the
    /// whole of what such an item's payload carries.
    /// </summary>
    public static readonly IReadOnlyList<string> OkxAnnouncementFields = ["annType", "title", "url", "pTime", "businessPTime"];

    Lens(string id, LensTarget target, IReadOnlyList<LensField> fields, IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        Id = id;
        Target = target;
        Fields = fields;
        Questions = questions;
        Canonical = CanonicalOf(id, target, fields, questions);
        LensSha = Sha256Hex.Of(Canonical);
        SchemaSha = TapeJson.Sha256(DecisionRequests.QuestionsJson(questions));
    }

    public string Id { get; }
    public LensTarget Target { get; }

    /// <summary>The state's fields, by path.</summary>
    public IReadOnlyList<LensField> Fields { get; }

    public IReadOnlyDictionary<string, DecisionQuestion> Questions { get; }

    /// <summary>The whole definition in one spelling: what is stored and what <see cref="LensSha"/> is the hash of.</summary>
    public string Canonical { get; }

    /// <summary>The SHA-256 of <see cref="Canonical"/>, lower-case hex: the lens's identity.</summary>
    public string LensSha { get; }

    /// <summary>The questions' hash as the port computes it (<see cref="SchemaRef.Of"/>): what a call's record carries.</summary>
    public string SchemaSha { get; }

    /// <summary>The port's reference for this lens at <paramref name="version"/> — hashed from the questions, never taken.</summary>
    public SchemaRef Schema(int version) => SchemaRef.Of(Id, version, Questions);

    /// <summary>
    /// THE TEXT ROWS A LENS MAY TARGET: every announcement row and GDELT's kept items in THIS BUILD's shipped catalogue,
    /// with the fields their payloads carry. Rows a <c>tape-sources.json</c> adds are not here, market rows are not
    /// here, and nothing that is not the tape is here.
    /// </summary>
    public static IReadOnlyList<LensTarget> Targets()
    {
        var targets = new List<LensTarget>();
        foreach (var row in TapeSourceCatalog.Shipped())
        {
            if (row.Parser == TapeSourceCatalog.AnnouncementParser && row.Id == TapeSourceCatalog.OkxEeaAnnouncements)
                targets.AddRange(row.Series.Select(s => new LensTarget(row.Id, s.Id, OkxAnnouncementFields)));
            else if (row.Parser == TapeSourceCatalog.GkgParser && row.Id == GdeltGkg.Source)
                targets.Add(new LensTarget(row.Id, GdeltGkg.ItemsSeries, GdeltGkg.Fields));
        }
        return targets;
    }

    /// <summary>The target a source and series name, or null because it is no text row of this build's catalogue.</summary>
    public static LensTarget? TargetOf(string? source, string? series) =>
        Targets().FirstOrDefault(t => t.Source == source && t.Series == series);

    /// <summary>A definition, parsed and validated: a lens, or the refusal in words.</summary>
    public static LensParse Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LensParse.No("a lens is a JSON object: {id, target, state, questions}, and this is empty");
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxDefinitionBytes)
            return LensParse.No($"a lens holds at most {MaxDefinitionBytes:N0} bytes, and this one is {bytes:N0}");

        try
        {
            using var doc = JsonDocument.Parse(text);
            return ParseRoot(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return LensParse.No("a lens is one JSON object, and this does not read as one: " + ex.Message);
        }
    }

    static LensParse ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return LensParse.No("a lens is one JSON object: {id, target, state, questions}");
        if (ForbiddenName(root) is { } named)
            return LensParse.No($"'{named}' names what a lens cannot carry: no instrument, host, pin, key, budget or model — "
                                + "which model answers, at what cost and with whose key is the account owner's, never a lens's");

        if (Keys(root, "a lens", ["id", "target", "state", "questions"]) is { } keys) return LensParse.No(keys);

        var id = root.GetProperty("id");
        if (id.ValueKind != JsonValueKind.String || !IsId(id.GetString()))
            return LensParse.No("'id' is lower-case letters, digits and '-', starting with a letter, at most 64");
        var name = id.GetString()!;
        if (name == OwnerTestId)
            return LensParse.No($"'{OwnerTestId}' is the account owner's Test press, whose record says it was a test and never a "
                                + "lens's evidence; a lens takes another id");

        var t = root.GetProperty("target");
        if (t.ValueKind != JsonValueKind.Object) return LensParse.No("'target' is an object: {source, series}");
        if (Keys(t, "'target'", ["source", "series"]) is { } tk) return LensParse.No(tk);
        var source = t.GetProperty("source").ValueKind == JsonValueKind.String ? t.GetProperty("source").GetString() : null;
        var series = t.GetProperty("series").ValueKind == JsonValueKind.String ? t.GetProperty("series").GetString() : null;
        if (TargetOf(source, series) is not { } target)
            return LensParse.No($"'{source}/{series}' is not a text row of this build's tape. A lens looks at "
                                + string.Join(" or ", Targets().Select(x => $"'{x.Source}/{x.Series}'"))
                                + " and at nothing else — no market row, and nothing that is not the tape");

        var st = root.GetProperty("state");
        if (st.ValueKind != JsonValueKind.Object) return LensParse.No("'state' is an object: {fields: [{path, max_chars}]}");
        if (Keys(st, "'state'", ["fields"]) is { } sk) return LensParse.No(sk);
        var fl = st.GetProperty("fields");
        if (fl.ValueKind != JsonValueKind.Array || fl.GetArrayLength() is < 1 or > MaxFields)
            return LensParse.No($"'state.fields' is a list of 1 to {MaxFields} fields of the target's items");
        var fields = new List<LensField>();
        foreach (var f in fl.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) return LensParse.No("a state field is an object: {path, max_chars}");
            if (Keys(f, "a state field", ["path", "max_chars"]) is { } fk) return LensParse.No(fk);
            var path = f.GetProperty("path").ValueKind == JsonValueKind.String ? f.GetProperty("path").GetString() : null;
            if (path is null || !target.Fields.Contains(path, StringComparer.Ordinal))
                return LensParse.No($"'{path}' is not a field of a '{target.Source}/{target.Series}' item, which carries "
                                    + string.Join(", ", target.Fields));
            if (fields.Any(x => x.Path == path)) return LensParse.No($"the state names the field '{path}' twice");
            var max = f.GetProperty("max_chars");
            if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var chars) || chars is < 1 or > MaxFieldChars
                || max.GetRawText().Any(c => c is '.' or 'e' or 'E'))
                return LensParse.No($"the field '{path}''s 'max_chars' is a whole number from 1 to {MaxFieldChars:N0}");
            fields.Add(new LensField(path, chars));
        }
        fields.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var qs = root.GetProperty("questions");
        if (qs.ValueKind != JsonValueKind.Object) return LensParse.No("'questions' is an object: {name: question}");
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);
        foreach (var p in qs.EnumerateObject())
        {
            if (questions.ContainsKey(p.Name)) return LensParse.No($"the question '{p.Name}' is named twice");
            if (Question(p.Name, p.Value, out var q) is { } why) return LensParse.No(why);
            questions[p.Name] = q!;
        }

        // THE PORT'S OWN WORDS, against the strictest instrument this build ships.
        var limits = DecisionInstruments.StrictestBuiltInLimits;
        if (DecisionRequests.QuestionsRefusal(questions, limits) is { } refused) return LensParse.No(refused);

        foreach (var (qn, q) in questions)
            if (q.Kind == DecisionKind.Choice && !q.Options.Any(o => EscapeOptions.Contains(o.Name, StringComparer.Ordinal)))
                return LensParse.No($"the Choice '{qn}' offers no way out: every Choice carries one of "
                                    + string.Join(", ", EscapeOptions) + ", so an item that fits no option is never forced into one");

        var lens = new Lens(name, new LensTarget(target.Source, target.Series, target.Fields), fields, questions);
        if (DecisionRequests.BytesRefusal(LensState.CapBytes(lens), questions, limits, "the strictest built-in instrument") is { } big)
            return LensParse.No("at its declared caps, " + big);

        return LensParse.Yes(lens);
    }

    /// <summary>One question in the port's wire shape — <c>{type, instructions, criteria}</c> — or the reason it is not one.</summary>
    static string? Question(string name, JsonElement e, out DecisionQuestion? q)
    {
        q = null;
        if (e.ValueKind != JsonValueKind.Object) return $"the question '{name}' is an object: {{type, instructions, criteria}}";
        var allowed = new[] { "type", "instructions", "criteria" };
        foreach (var p in e.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                return $"'{p.Name}' is not a key of a question: a question has type, instructions and criteria";
        if (!e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return $"the question '{name}' names no type: choice, noul or score";
        if (!e.TryGetProperty("instructions", out var ins) || ins.ValueKind != JsonValueKind.String)
            return $"the question '{name}' has no instructions";
        var instructions = ins.GetString()!;
        e.TryGetProperty("criteria", out var criteria);

        switch (type.GetString())
        {
            case "choice":
                if (criteria.ValueKind != JsonValueKind.Object) return $"the Choice '{name}''s criteria are an object: {{option: description or null}}";
                var options = new List<DecisionOption>();
                foreach (var o in criteria.EnumerateObject())
                {
                    if (options.Any(x => x.Name == o.Name)) return $"the Choice '{name}' names one option twice";
                    if (o.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        return $"the Choice '{name}''s option '{o.Name}' is described by text or null";
                    options.Add(new DecisionOption(o.Name, o.Value.ValueKind == JsonValueKind.String ? o.Value.GetString() : null));
                }
                q = DecisionQuestion.Choice(instructions, [.. options]);
                return null;

            case "score":
                if (criteria.ValueKind != JsonValueKind.Array) return $"the Score '{name}''s criteria are a list of its levels, lowest first";
                var levels = new List<string>();
                foreach (var l in criteria.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.String) return $"the Score '{name}' has a level that is not text";
                    levels.Add(l.GetString()!);
                }
                q = DecisionQuestion.Score(instructions, [.. levels]);
                return null;

            case "noul":
                if (criteria.ValueKind == JsonValueKind.Undefined) { q = DecisionQuestion.Noul(instructions); return null; }
                if (criteria.ValueKind != JsonValueKind.Object) return $"the Noul '{name}''s criteria are an object: {{true, false}}";
                string? yes = null, no = null;
                foreach (var c in criteria.EnumerateObject())
                {
                    if (c.Name is not ("true" or "false")) return $"the Noul '{name}''s criteria name only 'true' and 'false'";
                    if (c.Value.ValueKind != JsonValueKind.String) return $"the Noul '{name}''s '{c.Name}' is text";
                    if (c.Name == "true") yes = c.Value.GetString(); else no = c.Value.GetString();
                }
                q = DecisionQuestion.Noul(instructions, yes, no);
                return null;

            default:
                return $"'{type.GetString()}' is not a kind of question: choice, noul or score";
        }
    }

    /// <summary>The words naming what is wrong with <paramref name="e"/>'s keys, or null because it has exactly these.</summary>
    static string? Keys(JsonElement e, string what, string[] keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) return $"{what} names '{p.Name}' twice";
            if (!keys.Contains(p.Name, StringComparer.Ordinal))
                return $"'{p.Name}' is not a key of {what}, which has exactly {string.Join(", ", keys)}";
        }
        foreach (var k in keys)
            if (!seen.Contains(k)) return $"{what} has no '{k}'; it has exactly {string.Join(", ", keys)}";
        return null;
    }

    /// <summary>The first property name anywhere in <paramref name="e"/> that names a forbidden thing, or null.</summary>
    static string? ForbiddenName(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    var words = p.Name.ToLowerInvariant().Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (words.Any(w => Forbidden.Contains(w, StringComparer.Ordinal))) return p.Name;
                    if (ForbiddenName(p.Value) is { } inner) return inner;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (ForbiddenName(item) is { } inner) return inner;
                return null;
            default:
                return null;
        }
    }

    static bool IsId(string? s) =>
        s is { Length: >= 1 and <= 64 } && s[0] is >= 'a' and <= 'z'
        && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    static string CanonicalOf(string id, LensTarget target, IReadOnlyList<LensField> fields,
        IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteStartObject("target");
            w.WriteString("source", target.Source);
            w.WriteString("series", target.Series);
            w.WriteEndObject();
            w.WriteStartObject("state");
            w.WriteStartArray("fields");
            foreach (var f in fields.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteNumber("max_chars", f.MaxChars);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WritePropertyName("questions");
            w.WriteRawValue(DecisionRequests.QuestionsJson(questions));
            w.WriteEndObject();
        }
        return TapeJson.Canonical(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
