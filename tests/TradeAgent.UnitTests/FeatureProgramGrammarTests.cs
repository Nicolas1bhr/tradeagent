using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Features;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A PROGRAM READS A FEATURE IT DECLARES, BOUND INTO ITS ID BY THE FEATURE'S HASH (<c>U-language-v2a</c> item 1):
/// <c>feature &lt;name&gt; = &lt;spec JSON&gt;</c>, a declaration on a line's first word and never a reserved name, read in a
/// rule as an indicator is read — and every v1 text, canonical form and id exactly what it was.
/// </summary>
public class FeatureProgramGrammarTests(ITestOutputHelper log)
{
    /// <summary>The brief's own spec: the newest funding rate that had arrived five seconds before the close, at most three minutes old.</summary>
    public const string FundingSpec =
        """{"kind":"latest","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":180}""";

    /// <summary>The brief's observable program: it buys when funding is deeply negative and sells when it turns positive.</summary>
    public static string FundingProgram(string spec = FundingSpec, string name = "funding") => $"""
        instrument BTCUSDT
        bars 1h
        timeframe 1h
        data_freshness 2h
        max_decision_age 10m
        feature {name} = {spec}
        size fixed 1
        stop percent 2
        exit when {name} > 0
        entry when {name} < -0.0003
        """;

    static StrategyProgram Parsed(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    string Refused(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.False(parse.Ok, $"this text parsed, and it should have been refused:\n{text}");
        log.WriteLine(parse.Why);
        return parse.Why;
    }

    /// <summary>The ids `DayOneStrategyTests` pins for the three shipped programs, restated as the contract.</summary>
    static readonly Dictionary<string, string> ShippedIds = new(StringComparer.Ordinal)
    {
        ["ma-crossover.strategy"] = "3b3364734ea97e715479476ffd9992ade4074bfd52bc1a9220ef4b7605ffbd42",
        ["opening-range-breakout.strategy"] = "70ec1a6e45dc45096995564fc11d76f24f13c5ae156beab05aa9d1639ee7d26d",
        ["rsi-mean-reversion.strategy"] = "16d6192f798908637d3ae246056f4e2d65e0231531604202204783e62760b624"
    };

    /// <summary>
    /// (a) A V1 PROGRAM KEEPS ITS TEXT AND ITS ID. The three shipped programs parse to the ids pinned for them before
    /// this unit, declare no feature, carry no <c>feature</c> line, and hash under the manifest that did not move; the
    /// header and the language version are what they were; and then the golden vectors' own check runs — every golden
    /// program backtested by this build and compared with its pins — so this cannot hold while what a v1 program does
    /// has moved. Calling that check rather than copying its pins keeps one set of pins.
    /// </summary>
    [Fact]
    public void A_v1_program_keeps_its_text_and_id()
    {
        Assert.Equal("language=1;indicators=1;calendar=1", StrategyVersions.Manifest);
        Assert.Equal(1, StrategyVersions.LanguageVersion);
        Assert.Equal("program/1", StrategyCanonical.Header);

        foreach (var name in DayOnePrograms.Names)
        {
            var text = DayOnePrograms.Text(name);
            var program = Parsed(text);

            log.WriteLine($"{name,-34} {program.StrategyId}");
            Assert.Equal(ShippedIds[name], program.StrategyId);
            Assert.Empty(program.Features);
            Assert.DoesNotContain("\nfeature ", program.Canonical, StringComparison.Ordinal);
            Assert.DoesNotContain("$", program.Canonical, StringComparison.Ordinal);
            Assert.Equal(text, program.Source);
            Assert.Equal(
                Sha256Hex.Of($"{program.Canonical}\n{program.Parameters}\n{StrategyVersions.Manifest}"),
                program.StrategyId);
        }

        Assert.Equal(ShippedIds.Keys.Order(StringComparer.Ordinal), DayOnePrograms.Names.Order(StringComparer.Ordinal));

        new EvaluationGoldenVectorTests(log).Changed_evaluation_output_without_a_version_bump_fails_the_golden_vectors();
    }

    /// <summary>
    /// (b) A STORED PROGRAM WITH A CONSTANT NAMED <c>feature</c> STILL PARSES, TO THE TEXT IT ALWAYS HAD. The language had
    /// no <c>feature</c> before this unit, so a stored program may use the word as a name; reserving it would turn a
    /// recorded version into a refusal. The canonical text is pinned whole, so a change that kept the parse but moved the
    /// id is caught too. An indicator named <c>feature</c> parses, and so does a feature named <c>feature</c>.
    /// </summary>
    [Fact]
    public void A_stored_program_with_a_const_named_feature_still_parses()
    {
        var stored = Parsed("""
            instrument BTCUSDT
            const feature = 20
            indicator avg = sma(close, feature)
            size fixed 1
            stop percent 2
            exit when close < avg
            entry when close > avg and close[1] < feature * 10
            """);

        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            timeframe none
            freshness none
            decisionage none
            days all
            ind avg=sma(close,20)
            size fixed:1
            stop percent:2
            target none
            hold none
            warmup 20
            exit (< close @avg)
            entry (and (> close @avg) (< close[1] (* 20 10)))

            """.ReplaceLineEndings("\n"), stored.Canonical);
        Assert.Equal("feature=number:20", stored.Parameters);
        Assert.Empty(stored.Features);

        var indicator = Parsed("""
            instrument BTCUSDT
            indicator feature = sma(close, 5)
            size fixed 1
            exit when close < feature
            entry when close > feature
            """);
        Assert.Equal("feature", Assert.Single(indicator.Indicators).Name);

        var named = Parsed(FundingProgram(name: "feature"));
        Assert.Equal("feature", Assert.Single(named.Features).Name);
        Assert.Contains("entry (< $feature (neg 0.0003))", named.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) TWO SPELLINGS OF ONE SPEC SHARE AN ID, AND EVERY FACT THE SPEC STATES MOVES IT. Key order, spacing and an
    /// escaped letter are not part of a feature, so they are not part of the program that reads it: one canonical form,
    /// one id, two sources kept byte for byte. The canonical form names the feature by its spec's own hash — and every
    /// field of the spec, changed alone, is another feature and therefore another program.
    /// </summary>
    [Fact]
    public void Spellings_share_an_id_and_each_spec_field_moves_it()
    {
        var program = Parsed(FundingProgram());
        var spec = FeatureSpec.Parse(FundingSpec).Spec!;

        Assert.Equal("funding", Assert.Single(program.Features).Name);
        Assert.Equal(spec.Id, program.Features[0].Spec.Id);
        Assert.Contains($"\nfeature funding={spec.Id}\nsize fixed:1\n", program.Canonical, StringComparison.Ordinal);
        Assert.Contains("\nexit (> $funding 0)\nentry (< $funding (neg 0.0003))\n", program.Canonical, StringComparison.Ordinal);
        Assert.Equal(Sha256Hex.Of($"{program.Canonical}\n{program.Parameters}\n{StrategyVersions.Manifest}"), program.StrategyId);
        Assert.Equal(StrategyVersions.Manifest, program.Manifest);
        log.WriteLine(program.Canonical);

        // ANOTHER SPELLING: keys reordered, spaced, a letter escaped, the declaration's own case and spacing changed.
        var respelled = Parsed(FundingProgram(
            """{ "max_age_s" : 180, "latency_s": 5, "input": { "field": "lastFundingRate", "subject": "BTCUSDT", "series": "premium-index", "source": "binance-um-premium" }, "kind": "latest" }""")
            .Replace("feature funding =", "FEATURE   funding=", StringComparison.Ordinal));
        Assert.NotEqual(program.Source, respelled.Source);
        Assert.Equal(program.Canonical, respelled.Canonical);
        Assert.Equal(program.StrategyId, respelled.StrategyId);

        // EVERY FACT, MOVED ALONE: a different feature id, a different program id.
        var moved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source and series"] = FundingSpec.Replace("binance-um-premium", "binance-um-funding").Replace("premium-index", "funding-rate").Replace("lastFundingRate", "fundingRate"),
            ["subject"] = FundingSpec.Replace("BTCUSDT", "ETHUSDT"),
            ["field"] = FundingSpec.Replace("lastFundingRate", "markPrice"),
            ["latency_s"] = FundingSpec.Replace("\"latency_s\":5", "\"latency_s\":6"),
            ["max_age_s"] = FundingSpec.Replace("\"max_age_s\":180", "\"max_age_s\":181"),
            ["kind"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"mean\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"window_s\":3600,\"min_rows\":30"),
            ["window_s"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"mean\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"window_s\":3601,\"min_rows\":30"),
            ["min_rows"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"mean\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"window_s\":3600,\"min_rows\":31"),
            ["change, diff"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"change\",\"mode\":\"diff\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"lookback_s\":3600"),
            ["change, ratio"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"change\",\"mode\":\"ratio\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"lookback_s\":3600"),
            ["lookback_s"] = FundingSpec.Replace("\"kind\":\"latest\"", "\"kind\":\"change\",\"mode\":\"diff\"").Replace("\"max_age_s\":180", "\"max_age_s\":180,\"lookback_s\":3601"),
        };

        var featureIds = new HashSet<string>(StringComparer.Ordinal) { spec.Id };
        var programIds = new HashSet<string>(StringComparer.Ordinal) { program.StrategyId };
        foreach (var (fact, text) in moved)
        {
            var other = Parsed(FundingProgram(text));
            var otherSpec = Assert.Single(other.Features).Spec;
            log.WriteLine($"{fact,-18} feature {otherSpec.Id[..12]} program {other.StrategyId[..12]}");
            Assert.True(featureIds.Add(otherSpec.Id), $"moving the {fact} did not move the feature's id");
            Assert.True(programIds.Add(other.StrategyId), $"moving the {fact} did not move the program's id");
            Assert.Contains($"\nfeature funding={otherSpec.Id}\n", other.Canonical, StringComparison.Ordinal);
        }

        // AND THE NAME A RULE READS IT BY IS THE PROGRAM'S, as an indicator's is: another name, another text.
        Assert.NotEqual(program.StrategyId, Parsed(FundingProgram(name: "rate")).StrategyId);
    }

    /// <summary>
    /// (d) A PROGRAM THAT NAMES A FEATURE IT CANNOT RESOLVE IS REFUSED. A rule reading a name no line declares — a
    /// feature written nowhere, the canonical form's <c>$funding</c> typed as source, a feature declared below a
    /// constant of the same name — and a spec naming an input this build does not record: each is a refusal at its
    /// line, and nothing parses to a program whose identity would name an input with no hash.
    /// </summary>
    [Fact]
    public void A_program_that_names_a_feature_it_cannot_resolve_is_refused()
    {
        var undeclared = Refused(FundingProgram().Replace("feature funding = " + FundingSpec + "\n", "", StringComparison.Ordinal));
        Assert.StartsWith("line 8:", undeclared, StringComparison.Ordinal);
        Assert.Contains("`funding` is not a bar series, a declared constant or a declared indicator, nor a declared feature", undeclared, StringComparison.Ordinal);

        var dollar = Refused(FundingProgram().Replace("entry when funding <", "entry when $funding <", StringComparison.Ordinal));
        Assert.StartsWith("line 10:", dollar, StringComparison.Ordinal);
        Assert.Contains("the character `$` has no meaning in a condition", dollar, StringComparison.Ordinal);

        var shadow = Refused(FundingProgram().Replace("size fixed 1", "size fixed 1\nconst funding = 1", StringComparison.Ordinal));
        Assert.StartsWith("line 6:", shadow, StringComparison.Ordinal);
        Assert.Contains("`funding` is already a constant, so this feature would shadow it", shadow, StringComparison.Ordinal);

        var indicator = Refused(FundingProgram().Replace("size fixed 1", "size fixed 1\nindicator funding = sma(close, 3)", StringComparison.Ordinal));
        Assert.StartsWith("line 6:", indicator, StringComparison.Ordinal);
        Assert.Contains("`funding` is already an indicator, so this feature would shadow it", indicator, StringComparison.Ordinal);

        var twice = Refused(FundingProgram().Replace("size fixed 1", $"feature funding = {FundingSpec}\nsize fixed 1", StringComparison.Ordinal));
        Assert.StartsWith("line 7:", twice, StringComparison.Ordinal);
        Assert.Contains("`funding` is declared twice", twice, StringComparison.Ordinal);

        // AND BELOW THE PARSER, THE CANONICAL FORM FAILS CLOSED: a rule node reading a feature its program declares no
        // line for has no input hash to name, so the frozen program cannot be built at all — it used to be written `?`.
        // Reached through the internal constructor, because no text the parser accepts can produce one; `Parse` turns
        // whatever that constructor throws into a refusal naming the parser's defect.
        var ctor = typeof(StrategyProgram).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        var ghost = Assert.Throws<System.Reflection.TargetInvocationException>(() => ctor.Invoke(
        [
            "instrument BTCUSDT", "BTCUSDT", Array.Empty<StrategyConstant>(), Array.Empty<IndicatorDecl>(), Array.Empty<FeatureDecl>(),
            new[] { new StrategyRule(RuleKind.Entry, new BinaryExpr(BinaryOp.Less, new FeatureRef("ghost", 0), new NumberLiteral(0m))) },
            new Sizing(SizingKind.FixedQuantity, 1m), StopRule.None, TargetRule.None, (int?)1, TimeFilters.Default,
            null, StrategyBars.OneMinute
        ]));
        var inner = Assert.IsType<InvalidOperationException>(ghost.InnerException);
        Assert.Contains("a rule reads a feature `ghost` that this program declares no `feature` line for", inner.Message, StringComparison.Ordinal);

        foreach (var (input, says) in new[]
                 {
                     (FundingSpec.Replace("binance-um-premium", "binance-um-spot"), "'binance-um-spot' is not a source the tape records"),
                     (FundingSpec.Replace("premium-index", "mark-price"), "'mark-price' is not a series of binance-um-premium"),
                     (FundingSpec.Replace("BTCUSDT", "PEPEUSDT"), "'PEPEUSDT' is not one of them"),
                 })
        {
            var why = Refused(FundingProgram(input));
            Assert.StartsWith("line 6: feature `funding` is not a spec TradeAgent computes: ", why, StringComparison.Ordinal);
            Assert.Contains(says, why, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// (e) A BAD FEATURE LINE IS REFUSED IN WORDS: a spec over a TEXT source (OKX's announcements are words, not
    /// market numbers), a negative <c>latency_s</c> (look-ahead), a <c>#</c> inside the spec (a comment starts there,
    /// so what is left is not JSON, and it is never read as another spec), a ninth feature, a line of 513 characters,
    /// a negative history <c>funding[-1]</c> and a line with no <c>=</c> — each refused naming its line. A line of
    /// exactly 512 characters parses, and every other line is still held to 240.
    /// </summary>
    [Fact]
    public void A_bad_feature_line_is_refused_in_words()
    {
        var text = Refused(FundingProgram(FundingSpec
            .Replace("binance-um-premium", TapeSourceCatalog.OkxEeaAnnouncements)));
        Assert.StartsWith("line 6: feature `funding` is not a spec TradeAgent computes: ", text, StringComparison.Ordinal);
        Assert.Contains("its rows are text, not market numbers", text, StringComparison.Ordinal);

        var ahead = Refused(FundingProgram(FundingSpec.Replace("\"latency_s\":5", "\"latency_s\":-60")));
        Assert.StartsWith("line 6:", ahead, StringComparison.Ordinal);
        Assert.Contains("latency_s is -60: a negative latency would count a reading before it had arrived, which is look-ahead", ahead, StringComparison.Ordinal);

        var hash = Refused(FundingProgram(FundingSpec.Replace("lastFundingRate", "last#FundingRate")));
        Assert.StartsWith("line 6: feature `funding` is not a spec TradeAgent computes: ", hash, StringComparison.Ordinal);
        Assert.Contains("is not JSON", hash, StringComparison.Ordinal);

        var seven = string.Join("\n", Enumerable.Range(1, 7).Select(i => $"feature f{i} = {FundingSpec}"));
        Assert.Equal(8, Parsed(FundingProgram().Replace("size fixed 1", seven + "\nsize fixed 1", StringComparison.Ordinal)).Features.Count);
        var nine = Refused(FundingProgram().Replace("size fixed 1", string.Join("\n", Enumerable.Range(1, 8).Select(i => $"feature f{i} = {FundingSpec}")) + "\nsize fixed 1", StringComparison.Ordinal)
            .Replace("entry when funding", "entry when f1 > 1 or funding", StringComparison.Ordinal));
        Assert.StartsWith("line 14:", nine, StringComparison.Ordinal);
        Assert.Contains("a program may declare at most 8 features, and `f8` is one more", nine, StringComparison.Ordinal);

        // 512 CHARACTERS PARSE AND 513 DO NOT: the spec padded with spaces, which JSON ignores.
        var head = "feature funding = ";
        var at512 = head + FundingSpec.Replace("{\"kind\"", "{" + new string(' ', 512 - head.Length - FundingSpec.Length) + "\"kind\"", StringComparison.Ordinal);
        Assert.Equal(512, at512.Length);
        Assert.Equal(Parsed(FundingProgram()).StrategyId,
            Parsed(FundingProgram().Replace(head + FundingSpec, at512, StringComparison.Ordinal)).StrategyId);
        var long513 = Refused(FundingProgram().Replace(head + FundingSpec, at512.Replace("{ ", "{  ", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Equal("line 6: a `feature` line may be at most 512 characters and this one is 513", long513);

        var other = Refused(FundingProgram().Replace("stop percent 2", "stop percent 2" + new string(' ', 239), StringComparison.Ordinal));
        Assert.Equal("line 8: a line may be at most 240 characters and this one is 253", other);
        var comment = Refused(FundingProgram().Replace("size fixed 1", "# feature " + new string('x', 300) + "\nsize fixed 1", StringComparison.Ordinal));
        Assert.Equal("line 7: a line may be at most 240 characters and this one is 310", comment);

        var back = Refused(FundingProgram().Replace("entry when funding <", "entry when funding[-1] <", StringComparison.Ordinal));
        Assert.StartsWith("line 10:", back, StringComparison.Ordinal);
        Assert.Contains("`funding[...]` takes a plain whole number of bars back", back, StringComparison.Ordinal);

        var deep = Refused(FundingProgram().Replace("entry when funding <", "entry when funding[21] <", StringComparison.Ordinal));
        Assert.Contains("`funding[21]` reaches further back than the 20 bars", deep, StringComparison.Ordinal);

        var noEquals = Refused(FundingProgram().Replace("feature funding = ", "feature funding ", StringComparison.Ordinal));
        Assert.Equal("line 6: a feature reads `feature <name> = <spec>`, the spec one JSON object, and this line has no `=`", noEquals);

        // A HISTORY REFERENCE IS A NUMBER AND COSTS ITS BARS: `funding[3]` needs four.
        Assert.Equal(4, Parsed(FundingProgram().Replace("entry when funding <", "entry when funding[3] <", StringComparison.Ordinal)
            .Replace("stop percent 2", "stop percent 2", StringComparison.Ordinal)).WarmUpBars);
    }
}
