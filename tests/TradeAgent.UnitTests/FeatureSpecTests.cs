using System.Reflection;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Features;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A FEATURE SPEC, PARSED (<c>U-features</c> item 1): a total parse of a JSON spec into one typed meaning, one canonical
/// text and one id — or a refusal in words. Nothing here reads the tape: a spec is data, and whether it parses is a
/// fact about this build alone.
/// </summary>
public class FeatureSpecTests(ITestOutputHelper log)
{
    /// <summary>The brief's own example: the live funding rate of BTCUSDT, five seconds after arrival, at most three minutes old.</summary>
    const string Example =
        """{"kind":"latest","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":180}""";

    static FeatureSpec Parsed(string json)
    {
        var parse = FeatureSpec.Parse(json);
        Assert.True(parse.Ok, $"refused: {parse.Why}\n{json}");
        Assert.Equal("", parse.Why);
        return parse.Spec!;
    }

    static string Refused(string? json)
    {
        var parse = FeatureSpec.Parse(json);
        Assert.False(parse.Ok, $"parsed, and was meant to be refused:\n{json}");
        Assert.Null(parse.Spec);
        Assert.False(string.IsNullOrWhiteSpace(parse.Why));
        return parse.Why;
    }

    /// <summary>A spec of any kind, every key stated; each argument replaces its key's JSON value as written.</summary>
    static string Spec(string kind = "latest", string? mode = null, string source = "binance-um-premium",
        string series = "premium-index", string subject = "BTCUSDT", string field = "lastFundingRate",
        string latency = "5", string maxAge = "180", string? lookback = null, string? window = null, string? minRows = null)
    {
        var extra = kind switch
        {
            "change" => $$""","mode":"{{mode ?? "diff"}}","lookback_s":{{lookback ?? "3600"}}""",
            "latest" => "",
            _ => $$""","window_s":{{window ?? "3600"}},"min_rows":{{minRows ?? "30"}}"""
        };
        return $$"""{"kind":"{{kind}}","input":{"source":"{{source}}","series":"{{series}}","subject":"{{subject}}","field":"{{field}}"},"latency_s":{{latency}},"max_age_s":{{maxAge}}{{extra}}}""";
    }

    /// <summary>
    /// THE EXAMPLE PARSES TO THE TEXT AND THE ID THE CONTRACT STATES: <c>feature/1</c>, one fact a line in a fixed order,
    /// durations in whole seconds — and an id that is the SHA-256 of that text, a line break and <c>features=1</c>.
    /// </summary>
    [Fact]
    public void The_example_parses_to_one_canonical_text_and_its_id()
    {
        var spec = Parsed(Example);
        log.WriteLine(spec.Canonical + spec.Id);

        Assert.Equal(
            "feature/1\n"
            + "kind latest\n"
            + "input source=binance-um-premium series=premium-index subject=BTCUSDT field=lastFundingRate\n"
            + "latency 5s\n"
            + "maxage 180s\n",
            spec.Canonical);
        Assert.Equal("features=1", spec.Manifest);
        Assert.Equal("features=1", FeatureVersions.Manifest);
        Assert.Equal(Sha256Hex.Of(spec.Canonical + "\n" + "features=1"), spec.Id);
        Assert.Equal(64, spec.Id.Length);

        Assert.Equal(FeatureKinds.Latest, spec.Kind);
        Assert.Null(spec.Mode);
        Assert.Equal(new FeatureInput("binance-um-premium", "premium-index", "BTCUSDT", "lastFundingRate"), spec.Input);
        Assert.Equal([spec.Input], spec.Inputs);
        Assert.Equal(TimeSpan.FromSeconds(5), spec.Latency);
        Assert.Equal(TimeSpan.FromSeconds(180), spec.MaxAge);
        Assert.Null(spec.Lookback);
        Assert.Null(spec.Window);
        Assert.Null(spec.MinRows);

        // EVERY KIND HAS ITS OWN LINES, AND ONLY ITS OWN.
        Assert.Equal(
            "feature/1\nkind change\nmode ratio\n"
            + "input source=binance-um-premium series=premium-index subject=BTCUSDT field=lastFundingRate\n"
            + "latency 5s\nmaxage 180s\nlookback 3600s\n",
            Parsed(Spec("change", mode: "ratio")).Canonical);
        foreach (var kind in new[] { "mean", "min", "max", "pct-rank" })
            Assert.Equal(
                $"feature/1\nkind {kind}\n"
                + "input source=binance-um-premium series=premium-index subject=BTCUSDT field=lastFundingRate\n"
                + "latency 5s\nmaxage 180s\nwindow 3600s\nminrows 30\n",
                Parsed(Spec(kind)).Canonical);
    }

    /// <summary>
    /// (f) ONE SPEC SPELLED TWO WAYS HAS ONE ID: keys in another order, the input's keys in another order, white space
    /// and line breaks, and a letter written as a JSON escape are not part of a feature.
    /// </summary>
    [Fact]
    public void One_spec_spelled_two_ways_has_one_id()
    {
        var plain = Parsed(Example);
        var respelled = Parsed("""
            {
              "max_age_s" : 180,
              "latency_s" : 5,
              "input" : { "field" : "lastFundingRate", "subject" : "BTC\u0055SDT",
                          "series" : "premium-index", "source" : "binance-um-premium" },
              "kind" : "lat\u0065st"
            }
            """);

        log.WriteLine($"{plain.Id}\n{respelled.Id}");
        Assert.Equal(plain.Canonical, respelled.Canonical);
        Assert.Equal(plain.Id, respelled.Id);

        foreach (var kind in FeatureKinds.All)
        {
            var once = Parsed(Spec(kind));
            var twice = Parsed(Spec(kind).Replace(",", " ,\n ", StringComparison.Ordinal));
            Assert.Equal(once.Id, twice.Id);
        }
    }

    /// <summary>
    /// (f) EVERY FIELD MOVES THE ID: each fact a spec can state, changed alone, is another feature — the kind, a
    /// change's mode, the source with its series, the series, the subject, the field, the latency, the max age, the
    /// lookback, the window and the fewest readings. Checked to cover every key the parse accepts, so a key added later
    /// without a line in the canonical text fails here.
    /// </summary>
    [Fact]
    public void Every_field_moves_the_id()
    {
        var variants = new List<(string Key, string Json)>
        {
            ("kind", Spec("latest")),
            ("kind", Spec("mean")),
            ("kind", Spec("min")),
            ("kind", Spec("max")),
            ("kind", Spec("pct-rank")),
            ("mode", Spec("change", mode: "diff")),
            ("mode", Spec("change", mode: "ratio")),
            ("source", Spec(source: "binance-um-oi", series: "open-interest", field: "openInterest")),
            ("series", Spec(source: "binance-um-ratios-5m", series: "long-short-account-5m", field: "longShortRatio")),
            ("series", Spec(source: "binance-um-ratios-5m", series: "taker-long-short-5m", field: "longShortRatio")),
            ("subject", Spec(subject: "ETHUSDT")),
            ("field", Spec(field: "markPrice")),
            ("latency_s", Spec(latency: "6")),
            ("latency_s", Spec(latency: "0")),
            ("max_age_s", Spec(maxAge: "181")),
            ("lookback_s", Spec("change", lookback: "3601")),
            ("window_s", Spec("mean", window: "3601")),
            ("min_rows", Spec("mean", minRows: "31")),
        };

        var specs = variants.Select(v => (v.Key, Spec: Parsed(v.Json))).ToList();
        foreach (var (key, spec) in specs) log.WriteLine($"{key,-11} {spec.Id[..16]} {spec.Canonical.Replace("\n", " | ", StringComparison.Ordinal)}");

        // ALL DISTINCT: the base spec (latest, change diff, mean) is among them, so each variant differs from its base.
        Assert.Equal(specs.Count, specs.Select(s => s.Spec.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(specs.Count, specs.Select(s => s.Spec.Canonical).Distinct(StringComparer.Ordinal).Count());

        // AND EVERY KEY OF EVERY KIND WAS VARIED.
        string[] keys = ["kind", "mode", "source", "series", "subject", "field", "latency_s", "max_age_s", "lookback_s", "window_s", "min_rows"];
        Assert.Equal(keys.Order(StringComparer.Ordinal), variants.Select(v => v.Key).Distinct().Order(StringComparer.Ordinal));

        // A SPEC CAN STATE NOTHING ELSE: its type's public facts are these, and every one is in the canonical text.
        string[] facts = ["Canonical", "Id", "Input", "Inputs", "Kind", "Latency", "Manifest", "MaxAge", "MinRows", "Mode", "Lookback", "Window"];
        Assert.Equal(
            facts.Order(StringComparer.Ordinal),
            typeof(FeatureSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// (e) A LOOK-AHEAD SPEC CANNOT BE WRITTEN. A negative latency — the one number that could move the gate past
    /// arrival — is refused as look-ahead; every key that might read ahead (a lead, an offset, an "as of", a shift) is
    /// not a key of the language and is refused; a negative lookback or window is refused. A latency of zero is not
    /// look-ahead: a row still counts only once it had arrived (<c>FeatureEvaluatorTests</c>).
    /// </summary>
    [Fact]
    public void A_look_ahead_spec_cannot_be_written()
    {
        var negative = Refused(Spec(latency: "-5"));
        log.WriteLine(negative);
        Assert.Contains("look-ahead", negative, StringComparison.Ordinal);

        foreach (var key in new[] { "lead_s", "offset_s", "as_of", "shift_s", "future_s", "horizon_s", "align" })
        {
            var why = Refused(Spec().TrimEnd('}') + $",\"{key}\":60}}");
            log.WriteLine(why);
            Assert.Contains($"'{key}' is not a key of a latest feature", why, StringComparison.Ordinal);
            Assert.Contains("No key reads ahead", why, StringComparison.Ordinal);
        }

        Assert.Contains("never negative", Refused(Spec("change", lookback: "-3600")), StringComparison.Ordinal);
        Assert.Contains("never negative", Refused(Spec("mean", window: "-3600")), StringComparison.Ordinal);
        Assert.Contains("never negative", Refused(Spec(maxAge: "-1")), StringComparison.Ordinal);

        Assert.Equal(TimeSpan.Zero, Parsed(Spec(latency: "0")).Latency);
    }

    /// <summary>
    /// (m) A TEXT SOURCE IS REFUSED: OKX's announcements and GDELT's news items are text, read by parsers other than
    /// <c>binance-um-json</c>, and a feature reads numbers from market rows only. A source the tape does not record at
    /// all — a row <c>tape-sources.json</c> might add included — is refused as unknown: whether a spec parses is a fact
    /// about this build, never about a file an agent can write.
    /// </summary>
    [Fact]
    public void A_text_source_is_refused()
    {
        var okx = Refused(Spec(source: TapeSourceCatalog.OkxEeaAnnouncements, series: "announcements", field: "title"));
        var gdelt = Refused(Spec(source: GdeltGkg.Source, series: GdeltGkg.ItemsSeries, field: "V2Tone"));
        log.WriteLine(okx);
        log.WriteLine(gdelt);

        Assert.Contains($"parser '{TapeSourceCatalog.AnnouncementParser}'", okx, StringComparison.Ordinal);
        Assert.Contains($"parser '{TapeSourceCatalog.GkgParser}'", gdelt, StringComparison.Ordinal);
        foreach (var why in new[] { okx, gdelt })
        {
            Assert.Contains("its rows are text", why, StringComparison.Ordinal);
            Assert.Contains(TapeSourceCatalog.JsonParser, why, StringComparison.Ordinal);
        }

        var added = Refused(Spec(source: "my-premium"));
        Assert.Contains("'my-premium' is not a source the tape records", added, StringComparison.Ordinal);

        // EVERY MARKET ROW THIS BUILD SHIPS IS A SOURCE A FEATURE MAY READ, AND NO OTHER.
        var markets = TapeSourceCatalog.Shipped().Where(r => r.Parser == TapeSourceCatalog.JsonParser).ToList();
        Assert.Equal(5, markets.Count);
        foreach (var row in markets)
        {
            var series = row.Series[0];
            var field = series.Id == "funding-rate" ? "fundingRate" : "someValue";
            Assert.True(FeatureSpec.Parse(Spec(source: row.Id, series: series.Id, field: field)).Ok, row.Id);
            Assert.Contains(row.Id, added, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE PARSE IS TOTAL, AND EVERY REFUSAL IS IN WORDS: an unknown kind, an unknown key here or in the input, a missing
    /// key, an unknown series, a symbol the tape does not record, a field that is a row's time or symbol, a duration that
    /// is not whole seconds or is out of range, a key named twice, text that is not one JSON object, and text past the
    /// size bound. Nothing is defaulted or repaired.
    /// </summary>
    [Fact]
    public void A_malformed_spec_is_refused_in_words_never_defaulted()
    {
        (string? Json, string Says)[] cases =
        [
            (null, "this text is empty"),
            ("   ", "this text is empty"),
            ("[1,2]", "this text is a list"),
            ("{\"kind\":", "is not JSON"),
            ("""{"kind":"latest",}""", "is not JSON"),
            ("""{"kind":"latest" /* comment */}""", "is not JSON"),
            ("""{"input":{}}""", "names its kind as text"),
            ("""{"kind":7}""", "names its kind as text"),
            (Spec().Replace("\"latest\"", "\"Latest\"", StringComparison.Ordinal), "'Latest' is not a kind of feature"),
            (Spec().Replace("\"latest\"", "\"median\"", StringComparison.Ordinal), "'median' is not a kind of feature"),
            (Spec().Replace(",\"max_age_s\":180", "", StringComparison.Ordinal), "a latest feature names its max_age_s"),
            (Spec().Replace(",\"latency_s\":5", "", StringComparison.Ordinal), "a latest feature names its latency_s"),
            (Spec("mean").Replace(",\"min_rows\":30", "", StringComparison.Ordinal), "a mean feature names its min_rows"),
            (Spec("change").Replace(",\"mode\":\"diff\"", "", StringComparison.Ordinal), "a change feature names its mode"),
            (Spec().TrimEnd('}') + ",\"window_s\":60}", "'window_s' is not a key of a latest feature"),
            (Spec().TrimEnd('}') + ",\"kind\":\"mean\"}", "names 'kind' twice"),
            (Spec().Replace("\"field\":", "\"note\":\"x\",\"field\":", StringComparison.Ordinal), "'note' is not a key of a feature's input"),
            (Spec().Replace("\"subject\":\"BTCUSDT\",", "", StringComparison.Ordinal), "a feature's input names its subject"),
            (Spec().Replace("\"BTCUSDT\"", "7", StringComparison.Ordinal), "names its subject as text"),
            (Spec(series: "funding-rate"), "'funding-rate' is not a series of binance-um-premium"),
            (Spec(subject: "LTCUSDT"), "'LTCUSDT' is not one of them"),
            (Spec(subject: "btcusdt"), "'btcusdt' is not one of them"),
            (Spec(field: "time"), "'time' is the time of premium-index"),
            (Spec(field: "symbol"), "'symbol' is the symbol of premium-index"),
            (Spec(field: "last.FundingRate"), "is not a field name"),
            (Spec(latency: "5.0"), "latency_s is a whole number of seconds, written in digits, and 5.0 is not"),
            (Spec(latency: "5e0"), "and 5e0 is not"),
            (Spec(latency: "\"5\""), "latency_s is a whole number of seconds, written in digits, and this one is text"),
            (Spec(latency: "86401"), "latency_s is 86401; it is 0 to 86400 seconds"),
            (Spec(maxAge: "0"), "max_age_s is 0; it is 1 to 31622400 seconds"),
            (Spec("mean", minRows: "0"), "min_rows is 0; it is 1 to 50000 readings"),
            (Spec("mean", minRows: "50001"), "min_rows is 50001; it is 1 to 50000 readings"),
            (Spec("change", mode: "pct"), "a change's mode is 'diff'"),
            ("{\"kind\":\"latest\",\"x\":\"" + new string('a', FeatureSpec.MaxSpecBytes) + "\"}", "a feature spec is at most 4096 bytes"),
        ];

        foreach (var (json, says) in cases)
        {
            var why = Refused(json);
            log.WriteLine($"{says,-60} <- {why}");
            Assert.Contains(says, why, StringComparison.Ordinal);
        }

        // A REFUSAL ECHOES AT MOST FORTY CHARACTERS OF WHAT IT WAS GIVEN, AND NO LINE BREAK.
        var echoed = Refused(Spec().Replace("\"latest\"", "\"" + new string('k', 500) + "\\nmore\"", StringComparison.Ordinal));
        Assert.DoesNotContain(new string('k', 41), echoed, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', echoed);
    }
}
