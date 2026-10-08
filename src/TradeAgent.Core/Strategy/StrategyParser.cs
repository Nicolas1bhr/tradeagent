using System.Globalization;
using System.Text;
using TradeAgent.Core.Features;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// TEXT IN, A TYPED PROGRAM OR A REFUSAL OUT. NOTHING ELSE, EVER.
///
/// <para>The text is written by an agent — a cheap model asked for a rule set, writing into
/// `workspace/strategies/` — or by the account owner, dropped into `workspace/inbox/`. `CLAUDE.md`:
/// material in the inbox is DATA. A parser that threw on a malformed program would make a file the
/// agent writes into a way to crash the app that supervises it, so this one is total: every input
/// produces a <see cref="StrategyParse"/>, and the refusal names the line.</para>
///
/// <para><b>Three passes over the lines, not one.</b> Constants first, then indicators and features,
/// then everything else. That makes declaration ORDER irrelevant — a program whose `const` lines are at
/// the bottom means what a program whose `const` lines are at the top means — which matters twice:
/// the writer is a model that does not reliably order its output, and two orderings of one program
/// must reach the same <see cref="StrategyProgram.StrategyId"/>. Rules are the one exception: their
/// order is their meaning, and an entry declared above an exit is refused rather than reordered.</para>
///
/// <para><b>The grammar is in `docs/STRATEGY-LANGUAGE.md`</b>, with the indicator semantics and the
/// limits. This file is that document's implementation; the document is what a model is shown.</para>
/// </summary>
public static class StrategyParser
{
    /// <summary>
    /// Reads one program. Returns a <see cref="StrategyProgram"/> or a <see cref="StrategyRefusal"/>,
    /// and never throws, for any input.
    /// </summary>
    public static StrategyParse Parse(string? source)
    {
        try
        {
            return ParseCore(source ?? "");
        }
        catch (Exception ex)
        {
            // INSURANCE, NOT CONTROL FLOW. Nothing below raises, and the depth limit is what keeps
            // recursive descent off the stack's edge — but "nothing below raises" is a claim about
            // every line of this file forever, and the cost of it being wrong once is the supervisor
            // crashing on a text file the agent wrote. So an unexpected exception becomes a refusal
            // that says it is a parser defect, which is both true and actionable.
            return StrategyParse.No(0,
                $"this text could not be read as a program: the parser itself failed with {ex.GetType().Name}. " +
                "That is a defect in the parser rather than in the program — nothing was parsed.");
        }
    }

    // ---- the vocabulary, in one place each -------------------------------------------------------

    const string Keywords =
        "instrument, timezone, bars, timeframe, data_freshness, max_decision_age, const, indicator, feature, size, " +
        "stop, target, max_hold_bars, weekdays, entry_window, opening_range, session_exit, exit, entry";

    /// <summary>
    /// `bars 1h` — A DECLARATION, AND DELIBERATELY NOT A KEYWORD.
    ///
    /// <para>It is recognised in exactly one place: as the first word of a line (<see cref="ReadLines"/>).
    /// It is not in <see cref="IsKeyword"/> and therefore not in <see cref="IsReserved"/>, because the
    /// language had no `bars` before and a stored program may well say `const bars = 20` — reserving the
    /// word now would turn a program that parsed, and that results are recorded against, into a refusal.
    /// A name and a declaration never meet: a declaration is always the line's first word, a name never
    /// is (`const bars = 20` starts with `const`).</para>
    /// </summary>
    const string BarsDeclaration = "bars";

    /// <summary>
    /// `feature funding = {...}` — A DECLARATION, AND NOT A KEYWORD, for the reason <see cref="BarsDeclaration"/> is
    /// not one (<c>U-language-v2a</c>): the language had no `feature` before, a stored program may well say
    /// `const feature = 2`, and reserving the word now would turn a recorded version into a refusal. It is
    /// recognised as a line's first word and nowhere else.
    ///
    /// <para><b>The author writes the spec; the app states its id.</b> Everything after the first `=` is one JSON
    /// object handed whole to <see cref="FeatureSpec.Parse"/>, whose refusal is quoted on the line. A `#` starts a
    /// comment here as on every line, so a spec holding one is cut there and refused as the JSON it then is not —
    /// never read as another spec.</para>
    /// </summary>
    const string FeatureDeclaration = "feature";

    const string IndicatorList =
        "sma, ema, rsi, atr, highest, lowest, opening_range_high, opening_range_low";

    const string SeriesList = "open, high, low, close, volume";

    /// <summary>
    /// THE INDICATOR TABLE. The one place a name becomes a kind, so that an unknown name has exactly
    /// one place to be refused and cannot fall through to something generic.
    /// </summary>
    static bool TryIndicator(string name, out IndicatorKind kind)
    {
        switch (name)
        {
            case "sma": kind = IndicatorKind.Sma; return true;
            case "ema": kind = IndicatorKind.Ema; return true;
            case "rsi": kind = IndicatorKind.Rsi; return true;
            case "atr": kind = IndicatorKind.Atr; return true;
            case "highest": kind = IndicatorKind.Highest; return true;
            case "lowest": kind = IndicatorKind.Lowest; return true;
            case "opening_range_high": kind = IndicatorKind.OpeningRangeHigh; return true;
            case "opening_range_low": kind = IndicatorKind.OpeningRangeLow; return true;
            default: kind = default; return false;
        }
    }

    static bool TrySeries(string name, out BarSeries series)
    {
        switch (name)
        {
            case "open": series = BarSeries.Open; return true;
            case "high": series = BarSeries.High; return true;
            case "low": series = BarSeries.Low; return true;
            case "close": series = BarSeries.Close; return true;
            case "volume": series = BarSeries.Volume; return true;
            default: series = default; return false;
        }
    }

    static bool IsKeyword(string word) => word switch
    {
        "instrument" or "timezone" or "timeframe" or "data_freshness" or "max_decision_age"
            or "const" or "indicator" or "size" or "stop" or "target"
            or "max_hold_bars" or "weekdays" or "entry_window" or "opening_range" or "session_exit"
            or "exit" or "entry" => true,
        _ => false
    };

    /// <summary>Names a program may not take for a constant or an indicator, because they already mean something.</summary>
    static bool IsReserved(string name) =>
        IsKeyword(name) || TrySeries(name, out _) || TryIndicator(name, out _)
        || name is "when" or "and" or "or" or "not" or "true" or "false"
            or "crosses_above" or "crosses_below" or "fixed" or "percent";

    // ---- the lines ------------------------------------------------------------------------------

    readonly record struct Decl(int No, string Keyword, string Rest);

    sealed class Refused(int line, string reason) : Exception(reason)
    {
        public int Line { get; } = line;
        public string Text { get; } = reason;
    }

    static StrategyParse ParseCore(string text)
    {
        try
        {
            return Build(text);
        }
        catch (Refused r)
        {
            // The ONE use of an exception in here, and it never leaves: a refusal found eight frames
            // down in an expression is carried out to here rather than threaded through every
            // signature as a nullable. `Parse` above turns anything else into a refusal too.
            return StrategyParse.No(r.Line, r.Text);
        }
    }

    static StrategyParse Build(string text)
    {
        var byteLength = Encoding.UTF8.GetByteCount(text);
        if (byteLength > StrategyLimits.MaxSourceBytes)
            throw new Refused(0,
                $"a program may be at most {StrategyLimits.MaxSourceBytes} bytes of text and this one is {byteLength}");

        var decls = ReadLines(text);
        if (decls.Count == 0)
            throw new Refused(0, "there is nothing here to read: no declarations at all");

        var constants = ReadConstants(decls);
        var indicators = ReadIndicators(decls, constants);
        var features = ReadFeatures(decls, constants, indicators);
        var settings = ReadSettings(decls, constants);
        var rules = ReadRules(decls, constants, indicators, features);

        // ---- what a program must have in order to be one ----------------------------------------

        var instrument = settings.Instrument
            ?? throw new Refused(0, "no instrument: a program names the one spot instrument it trades, `instrument BTCUSDT`");

        var sizing = settings.Sizing
            ?? throw new Refused(0,
                "no size: a program says how much to buy, as `size fixed <quantity>`, " +
                "`size capital_fraction <fraction>` or `size risk_fraction <fraction>`");

        // RISK SIZING WITH NOTHING TO MEASURE RISK AGAINST. `risk_fraction` is a fraction of equity
        // over the STOP DISTANCE; with no stop there is no distance, and the quantity is a division
        // by nothing. The refusal names the size line, because that is the line that has to change.
        if (sizing.Kind == SizingKind.EquityRiskFraction && settings.Stop.Kind == StopKind.None)
            throw new Refused(settings.SizingLine,
                "`size risk_fraction` sizes a position so that the distance to the STOP is that fraction of equity, " +
                "and this program declares no stop. Add a `stop`, or size with `fixed` or `capital_fraction`");

        if (!rules.Any(r => r.Kind == RuleKind.Entry))
            throw new Refused(0, "no entry rule: a program that can never enter is not a strategy");

        var canLeave = rules.Any(r => r.Kind == RuleKind.Exit)
            || settings.Stop.Kind != StopKind.None
            || settings.Target.Kind != TargetKind.None
            || settings.MaxHoldBars is not null
            || settings.SessionExit is not null;
        if (!canLeave)
            throw new Refused(0,
                "nothing here can ever close the position: declare an `exit when ...` rule, a `stop`, " +
                "a `target`, a `max_hold_bars` or a `session_exit`");

        if (indicators.Values.Any(i => i.Kind is IndicatorKind.OpeningRangeHigh or IndicatorKind.OpeningRangeLow)
            && settings.OpeningRange is null)
            throw new Refused(0,
                "an opening-range indicator is declared but no `opening_range <from>-<to>` interval is: " +
                "the accumulator has no window to accumulate over");

        // ALL THREE EXECUTION BOUNDS, OR NONE OF THEM — and the refusal names the ones that are
        // missing rather than the one that is there.
        //
        // `docs/COUNCIL.md`:96-97 states them as one declaration: a promoted strategy declares its
        // timeframe, its required data freshness AND its maximum decision age. Two of the three is
        // the shape that would do damage: a program declaring a timeframe and a freshness reads as
        // though it had been given an execution gate, and the one bound that actually refuses a late
        // order — the decision age — is the one nobody wrote. So it is refused while it is still
        // text, which is also what makes `IntentDecision` able to be all-or-nothing downstream.
        var declared = new[]
        {
            ("timeframe", settings.Timeframe.HasValue),
            ("data_freshness", settings.DataFreshness.HasValue),
            ("max_decision_age", settings.MaxDecisionAge.HasValue)
        };
        var missing = declared.Where(x => !x.Item2).Select(x => x.Item1).ToArray();
        if (missing.Length is > 0 and < 3)
            throw new Refused(0,
                $"this program declares {string.Join(" and ", declared.Where(x => x.Item2).Select(x => $"`{x.Item1}`"))} "
                + $"and not {string.Join(" or ", missing.Select(m => $"`{m}`"))}. The three are one declaration: a "
                + "timeframe and a freshness with no `max_decision_age` beside them read like an execution gate and "
                + "are not one, because nothing there refuses a late order. Declare all three, or none of them");

        var freshness = settings is { Timeframe: { } tf, DataFreshness: { } df, MaxDecisionAge: { } mda }
            ? new FreshnessBounds(tf, df, mda)
            : null;

        var time = new TimeFilters(
            settings.TimeZone ?? "UTC",
            settings.Days ?? Weekdays.All,
            settings.EntryWindows,
            settings.OpeningRange,
            settings.SessionExit);

        var program = new StrategyProgram(
            text,
            instrument,
            [.. constants.Values.OrderBy(c => c.Name, StringComparer.Ordinal)],
            [.. indicators.Values.OrderBy(i => i.Name, StringComparer.Ordinal)],
            [.. features.Values.OrderBy(f => f.Name, StringComparer.Ordinal)],
            rules,
            sizing,
            settings.Stop,
            settings.Target,
            settings.MaxHoldBars,
            time,
            freshness,
            settings.Bars ?? StrategyBars.OneMinute);

        // A WARM-UP THAT CANNOT BE REACHED IS A REFUSAL, not a program that waits for ever. One limit
        // covers the period and the warm-up, so a period at the limit inside a crossing — which reads
        // one bar more — is refused here rather than accepted and never acted on.
        if (program.WarmUpBars > StrategyLimits.MaxLookbackBars)
            throw new Refused(0,
                $"this program would need {program.WarmUpBars} closed bars before it could act, and the most a " +
                $"program may need is {StrategyLimits.MaxLookbackBars}. Shorten the deepest period or history reference");

        return StrategyParse.Yes(program);
    }

    static List<Decl> ReadLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > StrategyLimits.MaxLines)
            throw new Refused(StrategyLimits.MaxLines + 1,
                $"a program may be at most {StrategyLimits.MaxLines} lines and this one has {lines.Length}");

        var decls = new List<Decl>();
        for (var i = 0; i < lines.Length; i++)
        {
            var no = i + 1;
            var raw = lines[i];
            if (i == 0) raw = raw.TrimStart('﻿');

            // A `feature` LINE MAY BE LONGER, AND ONLY IT: its spec is one JSON object (`MaxFeatureLineLength`). The
            // first word decides which limit, read here the way the keyword is read below, so a `# feature` comment
            // is held to the ordinary one.
            if (raw.Length > StrategyLimits.MaxLineLength)
            {
                if (!IsFeatureLine(raw))
                    throw new Refused(no,
                        $"a line may be at most {StrategyLimits.MaxLineLength} characters and this one is {raw.Length}");
                if (raw.Length > StrategyLimits.MaxFeatureLineLength)
                    throw new Refused(no,
                        $"a `feature` line may be at most {StrategyLimits.MaxFeatureLineLength} characters and this one is {raw.Length}");
            }

            foreach (var c in raw)
                if (char.IsControl(c) && c != '\t')
                    throw new Refused(no,
                        $"this line holds a control character (U+{(int)c:X4}), so it is not text somebody wrote as a program");

            var hash = raw.IndexOf('#');
            var body = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (body.Length == 0) continue;

            var space = body.IndexOfAny([' ', '\t']);
            var keyword = (space < 0 ? body : body[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : body[(space + 1)..].Trim();

            if (!IsKeyword(keyword) && keyword != BarsDeclaration && keyword != FeatureDeclaration)
                throw new Refused(no,
                    $"`{Clip(keyword)}` is not a declaration this language has. The declarations are: {Keywords}");

            decls.Add(new Decl(no, keyword, rest));
        }

        return decls;
    }

    /// <summary>Whether a raw line's first word is `feature`, read as <see cref="ReadLines"/> reads a keyword.</summary>
    static bool IsFeatureLine(string raw)
    {
        var hash = raw.IndexOf('#');
        var body = (hash >= 0 ? raw[..hash] : raw).Trim();
        var space = body.IndexOfAny([' ', '\t']);
        var word = space < 0 ? body : body[..space];
        return string.Equals(word, FeatureDeclaration, StringComparison.OrdinalIgnoreCase);
    }

    // ---- pass one: constants --------------------------------------------------------------------

    static Dictionary<string, StrategyConstant> ReadConstants(List<Decl> decls)
    {
        var constants = new Dictionary<string, StrategyConstant>(StringComparer.Ordinal);

        foreach (var d in decls)
        {
            if (d.Keyword != "const") continue;

            var eq = d.Rest.IndexOf('=');
            if (eq < 0)
                throw new Refused(d.No, "a constant reads `const <name> = <number>`, and this line has no `=`");

            var name = Name(d.No, d.Rest[..eq].Trim(), "constant");
            var value = d.Rest[(eq + 1)..].Trim();
            if (value.StartsWith('=') || value.Length == 0)
                throw new Refused(d.No, "a constant reads `const <name> = <number>`");

            if (constants.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is declared twice, and the second declaration would silently win");

            var lower = value.ToLowerInvariant();
            if (lower is "true" or "false")
                constants[name] = new StrategyConstant(name, ValueKind.Boolean, 0m, lower == "true");
            else if (TryNumber(value, out var number))
                constants[name] = new StrategyConstant(name, ValueKind.Number, number, false);
            else
                throw new Refused(d.No,
                    $"`{Clip(value)}` is not a number and is not `true` or `false`. A constant holds one of those two " +
                    "and nothing else — there are no expressions on a `const` line");
        }

        if (constants.Count > StrategyLimits.MaxConstants)
            throw new Refused(0,
                $"a program may declare at most {StrategyLimits.MaxConstants} constants and this one declares {constants.Count}");

        return constants;
    }

    // ---- pass two: indicators -------------------------------------------------------------------

    static Dictionary<string, IndicatorDecl> ReadIndicators(
        List<Decl> decls, Dictionary<string, StrategyConstant> constants)
    {
        var indicators = new Dictionary<string, IndicatorDecl>(StringComparer.Ordinal);

        foreach (var d in decls)
        {
            if (d.Keyword != "indicator") continue;

            var eq = d.Rest.IndexOf('=');
            if (eq < 0)
                throw new Refused(d.No, $"an indicator reads `indicator <name> = <indicator>(...)`, and this line has no `=`");

            var name = Name(d.No, d.Rest[..eq].Trim(), "indicator");
            if (constants.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is already a constant, so this indicator would shadow it");
            if (indicators.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is declared twice, and the second declaration would silently win");

            indicators[name] = ReadIndicatorCall(d.No, name, d.Rest[(eq + 1)..].Trim(), constants);
        }

        if (indicators.Count > StrategyLimits.MaxIndicators)
            throw new Refused(0,
                $"a program may declare at most {StrategyLimits.MaxIndicators} indicators and this one declares {indicators.Count}");

        return indicators;
    }

    static IndicatorDecl ReadIndicatorCall(
        int line, string name, string call, Dictionary<string, StrategyConstant> constants)
    {
        var open = call.IndexOf('(');
        if (open < 0 || !call.EndsWith(')'))
            throw new Refused(line,
                $"`{Clip(call)}` is not an indicator. Write one of: {IndicatorList} — with its arguments in brackets");

        var head = call[..open].Trim().ToLowerInvariant();
        if (!TryIndicator(head, out var kind))
            throw new Refused(line,
                $"`{Clip(head)}` is not an indicator this language has. The indicators are: {IndicatorList}");

        var inner = call[(open + 1)..^1].Trim();
        string[] args = inner.Length == 0 ? [] : [.. inner.Split(',').Select(a => a.Trim())];

        switch (kind)
        {
            case IndicatorKind.OpeningRangeHigh or IndicatorKind.OpeningRangeLow:
                if (args.Length != 0)
                    throw new Refused(line,
                        $"`{head}` takes no arguments: its window is the program's `opening_range` interval");
                return new IndicatorDecl(name, kind, BarSeries.Close, 0);

            case IndicatorKind.Atr:
                if (args.Length != 1)
                    throw new Refused(line,
                        $"`atr` takes one argument, the period: a true range is over high, low and the previous close, " +
                        "so there is no series to choose");
                return new IndicatorDecl(name, kind, BarSeries.Close, Period(line, head, args[0], constants));

            default:
                if (args.Length != 2)
                    throw new Refused(line, $"`{head}` takes two arguments: a bar series and a period");
                if (!TrySeries(args[0].ToLowerInvariant(), out var series))
                    throw new Refused(line,
                        $"`{Clip(args[0])}` is not a bar series. An indicator reads one of: {SeriesList} — " +
                        "an indicator of an indicator is not part of this language");
                return new IndicatorDecl(name, kind, series, Period(line, head, args[1], constants));
        }
    }

    // ---- pass two and a half: features ------------------------------------------------------------

    /// <summary>
    /// EVERY `feature <name> = <spec>` LINE (<c>U-language-v2a</c>): a name by <see cref="Name"/>, unique across
    /// constants, indicators and features, and a spec <see cref="FeatureSpec.Parse"/> accepts — its refusal quoted on
    /// the line. At most <see cref="StrategyLimits.MaxFeatures"/>, the ninth refused where it is written.
    /// </summary>
    static Dictionary<string, FeatureDecl> ReadFeatures(
        List<Decl> decls, Dictionary<string, StrategyConstant> constants, Dictionary<string, IndicatorDecl> indicators)
    {
        var features = new Dictionary<string, FeatureDecl>(StringComparer.Ordinal);

        foreach (var d in decls)
        {
            if (d.Keyword != FeatureDeclaration) continue;

            var eq = d.Rest.IndexOf('=');
            if (eq < 0)
                throw new Refused(d.No,
                    "a feature reads `feature <name> = <spec>`, the spec one JSON object, and this line has no `=`");

            var name = Name(d.No, d.Rest[..eq].Trim(), "feature");
            if (constants.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is already a constant, so this feature would shadow it");
            if (indicators.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is already an indicator, so this feature would shadow it");
            if (features.ContainsKey(name))
                throw new Refused(d.No, $"`{name}` is declared twice, and the second declaration would silently win");
            if (features.Count == StrategyLimits.MaxFeatures)
                throw new Refused(d.No,
                    $"a program may declare at most {StrategyLimits.MaxFeatures} features, and `{name}` is one more");

            var parse = FeatureSpec.Parse(d.Rest[(eq + 1)..].Trim());
            if (parse.Spec is not { } spec)
                throw new Refused(d.No, $"feature `{name}` is not a spec TradeAgent computes: {parse.Why}");

            features[name] = new FeatureDecl(name, spec);
        }

        return features;
    }

    // ---- pass three: everything that is not a constant, an indicator or a rule -------------------

    sealed class Settings
    {
        public string? Instrument;
        public string? TimeZone;
        public Sizing? Sizing;
        public int SizingLine;
        public StopRule Stop = StopRule.None;
        public TargetRule Target = TargetRule.None;
        public int? MaxHoldBars;
        public Weekdays? Days;
        public List<TimeWindow> EntryWindows = [];
        public TimeWindow? OpeningRange;
        public TimeOfDay? SessionExit;
        public TimeSpan? Timeframe;
        public TimeSpan? DataFreshness;
        public TimeSpan? MaxDecisionAge;
        public TimeSpan? Bars;
    }

    static Settings ReadSettings(List<Decl> decls, Dictionary<string, StrategyConstant> constants)
    {
        var s = new Settings();

        foreach (var d in decls)
        {
            switch (d.Keyword)
            {
                case "instrument":
                    Once(d, s.Instrument is not null);
                    s.Instrument = Symbol(d);
                    break;

                case "timezone":
                    Once(d, s.TimeZone is not null);
                    s.TimeZone = StrategyLimits.TimeZones.FirstOrDefault(
                            z => string.Equals(z, d.Rest, StringComparison.OrdinalIgnoreCase))
                        ?? throw new Refused(d.No,
                            $"`{Clip(d.Rest)}` is not a zone this language admits. The zones are: " +
                            $"{string.Join(", ", StrategyLimits.TimeZones)}");
                    break;

                case "size":
                    Once(d, s.Sizing is not null);
                    s.Sizing = ReadSizing(d, constants);
                    s.SizingLine = d.No;
                    break;

                case "stop":
                    Once(d, s.Stop.Kind != StopKind.None);
                    s.Stop = ReadStop(d, constants);
                    break;

                case "target":
                    Once(d, s.Target.Kind != TargetKind.None);
                    s.Target = ReadTarget(d, constants);
                    break;

                case "max_hold_bars":
                    Once(d, s.MaxHoldBars is not null);
                    s.MaxHoldBars = Count(d, d.Rest, constants, "max_hold_bars", StrategyLimits.MaxHoldBars);
                    if (s.MaxHoldBars <= 0)
                        throw new Refused(d.No,
                            $"max_hold_bars is a number of bars above 0, and this one is {s.MaxHoldBars}. " +
                            "A holding time of zero bars would close a position on the bar that opened it");
                    break;

                case "weekdays":
                    Once(d, s.Days is not null);
                    s.Days = ReadWeekdays(d);
                    break;

                case "entry_window":
                    s.EntryWindows.Add(ReadWindow(d, d.Rest));
                    if (s.EntryWindows.Count > StrategyLimits.MaxEntryWindows)
                        throw new Refused(d.No,
                            $"a program may declare at most {StrategyLimits.MaxEntryWindows} entry windows");
                    break;

                case "opening_range":
                    Once(d, s.OpeningRange is not null);
                    s.OpeningRange = ReadWindow(d, d.Rest);
                    break;

                case "session_exit":
                    Once(d, s.SessionExit is not null);
                    s.SessionExit = Clock(d.No, d.Rest);
                    break;

                case "timeframe":
                    Once(d, s.Timeframe is not null);
                    s.Timeframe = Duration(d);
                    break;

                case "data_freshness":
                    Once(d, s.DataFreshness is not null);
                    s.DataFreshness = Duration(d);
                    break;

                case "max_decision_age":
                    Once(d, s.MaxDecisionAge is not null);
                    s.MaxDecisionAge = Duration(d);
                    break;

                case BarsDeclaration:
                    Once(d, s.Bars is not null);
                    s.Bars = Bars(d);
                    break;
            }
        }

        return s;
    }

    static void Once(Decl d, bool already)
    {
        if (already)
            throw new Refused(d.No,
                $"`{d.Keyword}` is declared twice. One of the two would silently win, and which one is not " +
                "something a program should leave to the order of its lines");
    }

    static string Symbol(Decl d)
    {
        var symbol = d.Rest.ToUpperInvariant();
        if (symbol.Length is 0 or > StrategyLimits.MaxInstrumentLength
            || !symbol.All(c => char.IsAsciiLetterOrDigit(c)))
            throw new Refused(d.No,
                $"`{Clip(d.Rest)}` is not one instrument symbol. A program trades ONE spot instrument, written as " +
                $"letters and digits ({StrategyLimits.MaxInstrumentLength} characters at most) — a list, a path or a " +
                "second symbol is not something this language can say");
        return symbol;
    }

    /// <summary>
    /// `max_capital_fraction` — THE CAP ON A RISK-SIZED ENTRY (<c>U-size-cap</c>), A CLAUSE OF THE SIZE LINE AND NOTHING
    /// ELSE: <c>size risk_fraction 0.01 max_capital_fraction 0.95</c>. Recognised only as the size line's third word,
    /// never as a line's first word and never as a reserved name, for the reason <see cref="BarsDeclaration"/> is not one:
    /// the language had no such word before, a stored program may well say <c>const max_capital_fraction = 1</c>, and
    /// reserving it now would turn a recorded version into a refusal.
    /// </summary>
    const string CapClause = StrategyDeclarations.MaxCapitalFraction;

    const string SizeGrammar =
        "size reads `size fixed <quantity>`, `size capital_fraction <fraction>` or `size risk_fraction <fraction>`, " +
        "and a risk fraction may add the most of its capital an entry may spend: " +
        "`size risk_fraction <fraction> max_capital_fraction <fraction>`";

    static Sizing ReadSizing(Decl d, Dictionary<string, StrategyConstant> constants)
    {
        var words = Words(d.Rest);

        // THE CAP, read before the base form so that a cap where it means nothing is refused in its own words rather
        // than as a line one word too long. Only as the third word: anywhere else it is a word this line does not take.
        if (words.Length >= 3 && string.Equals(words[2], CapClause, StringComparison.OrdinalIgnoreCase))
            return ReadCappedSizing(d, words, constants);

        if (words.Length != 2)
            throw new Refused(d.No, SizeGrammar);

        var value = Value(d.No, words[1], constants);
        switch (words[0].ToLowerInvariant())
        {
            case "fixed":
                if (value <= 0m || value > StrategyLimits.MaxFixedQuantity)
                    throw new Refused(d.No,
                        $"a fixed quantity must be above 0 and at most {Number(StrategyLimits.MaxFixedQuantity)}, and this one is {Number(value)}");
                return new Sizing(SizingKind.FixedQuantity, value);

            case "capital_fraction":
            case "risk_fraction":
                return new Sizing(
                    words[0].ToLowerInvariant() == "capital_fraction" ? SizingKind.CapitalFraction : SizingKind.EquityRiskFraction,
                    Fraction(d, value));

            default:
                throw new Refused(d.No,
                    $"`{Clip(words[0])}` is not a way to size. The three are: fixed, capital_fraction, risk_fraction");
        }
    }

    /// <summary>
    /// <c>size risk_fraction &lt;f&gt; max_capital_fraction &lt;c&gt;</c> (<c>U-size-cap</c>): the risk fraction under the
    /// base form's rule, and a cap above 0 and at most <see cref="StrategyLimits.MaxSizingFraction"/>, each a NUMBER or a
    /// declared number constant. Every other shape with the clause in it is refused on the size line, in words: a cap on a
    /// size that cannot grow, a cap that is no fraction of capital, and anything after it.
    /// </summary>
    static Sizing ReadCappedSizing(Decl d, string[] words, Dictionary<string, StrategyConstant> constants)
    {
        switch (words[0].ToLowerInvariant())
        {
            case "fixed":
                throw new Refused(d.No,
                    $"`{CapClause}` caps a RISK-sized entry, whose size grows as its stop nears the price, and " +
                    "`size fixed` is a constant quantity with nothing to cap. Write `size fixed <quantity>` alone");

            case "capital_fraction":
                throw new Refused(d.No,
                    $"`{CapClause}` caps a RISK-sized entry, and `size capital_fraction` already is a fraction of " +
                    "capital — it is its own cap. Write `size capital_fraction <fraction>` alone");

            case "risk_fraction":
                break;

            default:
                throw new Refused(d.No,
                    $"`{Clip(words[0])}` is not a way to size. The three are: fixed, capital_fraction, risk_fraction");
        }

        if (words.Length != 4)
            throw new Refused(d.No,
                $"a capped risk size reads `size risk_fraction <fraction> {CapClause} <fraction>`: the fraction of " +
                "equity to risk, then the most of its capital the entry may spend, and nothing after it");

        var risk = Fraction(d, Value(d.No, words[1], constants));
        var cap = Value(d.No, words[3], constants);

        if (cap <= 0m)
            throw new Refused(d.No,
                $"`{CapClause}` must be above 0, and this one is {Number(cap)}: a cap of nothing would size every " +
                "entry to nothing, which is a program that can never trade");
        if (cap > StrategyLimits.MaxSizingFraction)
            throw new Refused(d.No,
                $"`{CapClause}` must be at most {Number(StrategyLimits.MaxSizingFraction)} — {Number(cap)} is more " +
                "than everything there is, which is leverage, and leverage is not something this language can say");

        return new Sizing(SizingKind.EquityRiskFraction, risk, cap);
    }

    /// <summary>A capital or risk fraction: above 0 and at most <see cref="StrategyLimits.MaxSizingFraction"/>, or refused on its line.</summary>
    static decimal Fraction(Decl d, decimal value)
    {
        if (value <= 0m || value > StrategyLimits.MaxSizingFraction)
            throw new Refused(d.No,
                $"a fraction must be above 0 and at most {Number(StrategyLimits.MaxSizingFraction)} — " +
                $"{Number(value)} is more than everything there is, which is leverage, and leverage is not " +
                "something this language can say");
        return value;
    }

    static StopRule ReadStop(Decl d, Dictionary<string, StrategyConstant> constants)
    {
        var words = Words(d.Rest);
        var kindWord = words.Length == 0 ? "" : words[0].ToLowerInvariant();

        switch (kindWord)
        {
            case "fixed" when words.Length == 2:
                return new StopRule(StopKind.FixedPrice, Positive(d.No, "a fixed stop price", Value(d.No, words[1], constants)), 0);

            case "percent" when words.Length == 2:
                var pct = Value(d.No, words[1], constants);
                if (pct <= 0m || pct > StrategyLimits.MaxPercent)
                    throw new Refused(d.No, $"a percentage stop must be above 0 and at most {Number(StrategyLimits.MaxPercent)}");
                return new StopRule(StopKind.Percent, pct, 0);

            case "atr" when words.Length == 3:
                var multiple = Value(d.No, words[1], constants);
                if (multiple <= 0m || multiple > StrategyLimits.MaxAtrMultiple)
                    throw new Refused(d.No, $"an ATR multiple must be above 0 and at most {Number(StrategyLimits.MaxAtrMultiple)}");
                return new StopRule(StopKind.EntryAtr, multiple, Period(d.No, "stop atr", words[2], constants));

            default:
                throw new Refused(d.No,
                    "a stop reads `stop fixed <price>`, `stop percent <percent>` or `stop atr <multiple> <period>`");
        }
    }

    static TargetRule ReadTarget(Decl d, Dictionary<string, StrategyConstant> constants)
    {
        var words = Words(d.Rest);
        var kindWord = words.Length == 0 ? "" : words[0].ToLowerInvariant();

        switch (kindWord)
        {
            case "fixed" when words.Length == 2:
                return new TargetRule(TargetKind.FixedPrice, Positive(d.No, "a fixed target price", Value(d.No, words[1], constants)));

            case "percent" when words.Length == 2:
                var pct = Value(d.No, words[1], constants);
                if (pct <= 0m || pct > StrategyLimits.MaxPercent)
                    throw new Refused(d.No, $"a percentage target must be above 0 and at most {Number(StrategyLimits.MaxPercent)}");
                return new TargetRule(TargetKind.Percent, pct);

            default:
                throw new Refused(d.No, "a target reads `target fixed <price>` or `target percent <percent>`");
        }
    }

    static Weekdays ReadWeekdays(Decl d)
    {
        var days = Weekdays.None;
        foreach (var word in d.Rest.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            days |= word.ToLowerInvariant() switch
            {
                "mon" => Weekdays.Mon,
                "tue" => Weekdays.Tue,
                "wed" => Weekdays.Wed,
                "thu" => Weekdays.Thu,
                "fri" => Weekdays.Fri,
                "sat" => Weekdays.Sat,
                "sun" => Weekdays.Sun,
                _ => throw new Refused(d.No,
                    $"`{Clip(word)}` is not a weekday. Write them as mon,tue,wed,thu,fri,sat,sun")
            };

        if (days == Weekdays.None)
            throw new Refused(d.No, "a weekday list with no days in it would refuse every bar there is");

        return days;
    }

    static TimeWindow ReadWindow(Decl d, string text)
    {
        var parts = text.Split('-');
        if (parts.Length != 2)
            throw new Refused(d.No, $"`{d.Keyword}` reads an interval as `<from>-<to>`, as in 09:30-10:00");

        var from = Clock(d.No, parts[0].Trim());
        var to = Clock(d.No, parts[1].Trim());
        if (from.MinuteOfDay >= to.MinuteOfDay)
            throw new Refused(d.No,
                $"{from} is not before {to}. An interval that wraps past midnight is not something this language can say");

        return new TimeWindow(from, to);
    }

    /// <summary>
    /// A DURATION: a whole number and a unit, written together — `30s`, `5m`, `2h`, `1d`.
    ///
    /// <para>Its own spelling rather than the language's NUMBER, because the three bounds are the one
    /// place a number's UNIT decides whether an order is sent: `max_decision_age 30` is thirty of
    /// something, and a reader who assumed minutes where the parser assumed seconds would be reading
    /// a gate sixty times looser than the one that runs. There is no default unit to get wrong.</para>
    ///
    /// <para>No fractions and no compound forms (`1m30s`): a bound is compared against a wall clock
    /// on the money path, and the two ways to spell one duration are already one duration in the
    /// canonical form, where it is written in seconds.</para>
    /// </summary>
    static TimeSpan Duration(Decl d)
    {
        var text = d.Rest.Trim().ToLowerInvariant();
        var unit = text.Length == 0 ? '\0' : text[^1];

        var seconds = unit switch
        {
            's' => 1L,
            'm' => 60L,
            'h' => 3600L,
            'd' => 86400L,
            _ => 0L
        };

        if (seconds == 0
            || !long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            throw new Refused(d.No,
                $"`{Clip(d.Rest)}` is not a duration. Write a whole number and one of s, m, h or d — " +
                "`30s`, `5m`, `2h`, `1d` — because a bound with no unit on it is a gate whose reader " +
                "has to guess which one, and guessing wrong by a factor of sixty is not visible anywhere");

        var total = count * seconds;
        if (total < 1 || total > StrategyLimits.MaxBoundSeconds)
            throw new Refused(d.No,
                $"`{d.Keyword}` must be at least 1 second and at most {StrategyLimits.MaxBoundSeconds} " +
                $"(one week), and this one is {total} seconds. A bound of nothing refuses every order " +
                "rather than gating one, and a bound of months admits every order there will ever be");

        return TimeSpan.FromSeconds(total);
    }

    /// <summary>
    /// THE BAR THIS PROGRAM IS EVALUATED ON — `bars 1h` — one of <see cref="StrategyBars.List"/>.
    ///
    /// <para>Written like a duration (a whole number and one of s, m, h or d) and then held to the closed
    /// list, so `bars 60m` is `bars 1h` — one meaning, one text — while `bars 2h` is refused with the seven
    /// named. Every one of them is a grid the evaluator cuts closed minutes on (`BarGrid`), and a duration
    /// outside the list would be a grid nobody else's program shares.</para>
    ///
    /// <para><b>It is not a fourth execution bound</b> and it changes nothing about the other three: a
    /// `timeframe` is a statement checked at execution, this is the bar the rules are asked on.
    /// `docs/STRATEGY-LANGUAGE.md` says how the two relate.</para>
    /// </summary>
    static TimeSpan Bars(Decl d)
    {
        var text = d.Rest.Trim().ToLowerInvariant();
        var unit = text.Length == 0 ? '\0' : text[^1];

        var seconds = unit switch
        {
            's' => 1L,
            'm' => 60L,
            'h' => 3600L,
            'd' => 86400L,
            _ => 0L
        };

        // The count is bounded BEFORE it is multiplied, so a twenty-digit number is a refusal and never
        // an overflow: nothing on the list is longer than a day.
        if (seconds != 0
            && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count is > 0 and <= 86400
            && StrategyBars.IsAllowed(TimeSpan.FromSeconds(count * seconds)))
            return TimeSpan.FromSeconds(count * seconds);

        throw new Refused(d.No,
            $"`{Clip(d.Rest)}` is not a bar this language evaluates a program on. Write one of {StrategyBars.List} — " +
            "a closed list, so that two programs on hourly bars cut them at the same instants. A program that " +
            "declares no `bars` is evaluated on every closed minute");
    }

    static TimeOfDay Clock(int line, string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || hour > 23 || minute > 59)
            throw new Refused(line,
                $"`{Clip(text)}` is not a time of day. Write it as HH:MM on a 24-hour clock, in the program's declared zone");

        return new TimeOfDay(hour, minute);
    }

    // ---- pass four: the rules -------------------------------------------------------------------

    static List<StrategyRule> ReadRules(
        List<Decl> decls,
        Dictionary<string, StrategyConstant> constants,
        Dictionary<string, IndicatorDecl> indicators,
        Dictionary<string, FeatureDecl> features)
    {
        var rules = new List<StrategyRule>();
        var sawEntry = false;
        var nodes = 0;

        foreach (var d in decls)
        {
            if (d.Keyword is not ("exit" or "entry")) continue;

            var kind = d.Keyword == "exit" ? RuleKind.Exit : RuleKind.Entry;
            var words = Words(d.Rest);
            if (words.Length == 0 || !string.Equals(words[0], "when", StringComparison.OrdinalIgnoreCase))
                throw new Refused(d.No,
                    $"a rule reads `{d.Keyword} when <condition>`. " +
                    (words.Length == 0
                        ? "This one says nothing after it"
                        : $"`{Clip(words[0])}` is not `when` — and a rule takes no side, because a program is long or flat"));

            var condition = ParseExpression(d.No, d.Rest[words[0].Length..].Trim(), constants, indicators, features);

            // A CONDITION IS A QUESTION, and the type system is what says so while there is still a
            // line to name. `entry when close` asks whether a price is true.
            var type = StrategyTypes.TypeOf(condition, out var mismatch);
            if (type is null)
                throw new Refused(d.No, mismatch ?? "this condition does not type-check");
            if (type != ValueKind.Boolean)
                throw new Refused(d.No,
                    "a rule condition is a true-or-false question, and this one is a number. " +
                    "A comparison makes it a question: `close > fastma`, `rsi14 < 30`");

            nodes += condition.NodeCount;
            if (nodes > StrategyLimits.MaxNodes)
                throw new Refused(d.No,
                    $"the conditions in this program come to more than the {StrategyLimits.MaxNodes} expression nodes " +
                    "one program may hold. That count is what the interpreter walks on every closed bar");

            if (kind == RuleKind.Entry) sawEntry = true;
            else if (sawEntry)
                throw new Refused(d.No,
                    "an exit rule cannot be declared below an entry rule. Exits are evaluated before entries on " +
                    "every bar, so they are written first — a program whose order says otherwise means something " +
                    "its author did not write");

            rules.Add(new StrategyRule(kind, condition));

            if (rules.Count > StrategyLimits.MaxRules)
                throw new Refused(d.No,
                    $"a program may declare at most {StrategyLimits.MaxRules} rules, exits and entries together");
        }

        return rules;
    }

    // ---- expressions ----------------------------------------------------------------------------

    enum Tok { Number, Name, Op, LParen, RParen, LBracket, RBracket, Comma }

    readonly record struct Token(Tok Kind, string Text, decimal Number);

    static Expr ParseExpression(
        int line, string text,
        Dictionary<string, StrategyConstant> constants,
        Dictionary<string, IndicatorDecl> indicators,
        Dictionary<string, FeatureDecl> features)
    {
        if (text.Length == 0) throw new Refused(line, "this rule has no condition after `when`");

        var tokens = Tokenise(line, text);
        var parser = new Expressions(line, tokens, constants, indicators, features);
        var expr = parser.Parse();
        return expr;
    }

    static List<Token> Tokenise(int line, string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is ' ' or '\t') { i++; continue; }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                var dots = 0;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    if (text[i] == '.' && ++dots > 1)
                        throw new Refused(line, $"`{Clip(text[start..(i + 1)])}` is not a number");
                    i++;
                }
                var word = text[start..i];
                if (!TryNumber(word, out var value))
                    throw new Refused(line, $"`{Clip(word)}` is not a number this language can read");
                tokens.Add(new Token(Tok.Number, word, value));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text[start..i];
                if (word.Length > StrategyLimits.MaxIdentifierLength)
                    throw new Refused(line,
                        $"`{Clip(word)}` is longer than the {StrategyLimits.MaxIdentifierLength} characters a name may be");
                tokens.Add(new Token(Tok.Name, word.ToLowerInvariant(), 0m));
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(Tok.LParen, "(", 0m)); i++; continue;
                case ')': tokens.Add(new Token(Tok.RParen, ")", 0m)); i++; continue;
                case '[': tokens.Add(new Token(Tok.LBracket, "[", 0m)); i++; continue;
                case ']': tokens.Add(new Token(Tok.RBracket, "]", 0m)); i++; continue;
                case ',': tokens.Add(new Token(Tok.Comma, ",", 0m)); i++; continue;
                case '+' or '-' or '*' or '/':
                    tokens.Add(new Token(Tok.Op, c.ToString(), 0m)); i++; continue;
                case '<' or '>' or '=' or '!':
                    var two = i + 1 < text.Length && text[i + 1] == '=';
                    var op = two ? text.Substring(i, 2) : c.ToString();
                    if (op is "=" or "!")
                        throw new Refused(line,
                            op == "=" ? "`=` compares nothing: write `==` to ask whether two numbers are equal"
                                      : "`!` on its own means nothing: write `!=` for \"is not equal\", or `not` for a negation");
                    tokens.Add(new Token(Tok.Op, op, 0m));
                    i += two ? 2 : 1;
                    continue;
                default:
                    throw new Refused(line,
                        $"the character `{c}` has no meaning in a condition. Conditions are numbers, names, " +
                        "`[` history `]`, arithmetic, comparisons, `and`, `or`, `not` and the two crossings");
            }
        }

        if (tokens.Count == 0) throw new Refused(line, "this rule has no condition after `when`");
        return tokens;
    }

    /// <summary>
    /// Recursive descent, bounded by <see cref="StrategyLimits.MaxExpressionDepth"/> ON THE WAY DOWN.
    /// The bound is not a limit on programs so much as what makes the parser total: a stack overflow
    /// cannot be caught, so 5,000 open brackets have to be refused before the recursion happens
    /// rather than after.
    /// </summary>
    sealed class Expressions(
        int line,
        List<Token> tokens,
        Dictionary<string, StrategyConstant> constants,
        Dictionary<string, IndicatorDecl> indicators,
        Dictionary<string, FeatureDecl> features)
    {
        int _pos;

        public Expr Parse()
        {
            var expr = Or(0);
            if (_pos < tokens.Count)
                throw new Refused(line,
                    $"`{Clip(tokens[_pos].Text)}` is left over after the condition ends, so this line says two things");
            return expr;
        }

        bool Next(out Token token)
        {
            token = _pos < tokens.Count ? tokens[_pos] : default;
            return _pos < tokens.Count;
        }

        bool NextIs(Tok kind, string text) =>
            Next(out var t) && t.Kind == kind && string.Equals(t.Text, text, StringComparison.Ordinal);

        Token Take() => tokens[_pos++];

        void Expect(Tok kind, string text, string what)
        {
            if (!NextIs(kind, text))
                throw new Refused(line,
                    $"expected `{text}` {what}, and found " + (Next(out var t) ? $"`{Clip(t.Text)}`" : "the end of the line"));
            _pos++;
        }

        Expr Or(int depth)
        {
            var left = And(depth);
            while (NextIs(Tok.Name, "or")) { _pos++; left = new BinaryExpr(BinaryOp.Or, left, And(depth)); }
            return left;
        }

        Expr And(int depth)
        {
            var left = Not(depth);
            while (NextIs(Tok.Name, "and")) { _pos++; left = new BinaryExpr(BinaryOp.And, left, Not(depth)); }
            return left;
        }

        Expr Not(int depth)
        {
            if (NextIs(Tok.Name, "not")) { _pos++; return new UnaryExpr(UnaryOp.Not, Not(Deeper(depth))); }
            return Comparison(depth);
        }

        Expr Comparison(int depth)
        {
            var left = Additive(depth);
            if (Next(out var t) && t.Kind == Tok.Op && Compare(t.Text) is { } op)
            {
                _pos++;
                return new BinaryExpr(op, left, Additive(depth));
            }
            return left;
        }

        static BinaryOp? Compare(string op) => op switch
        {
            "<" => BinaryOp.Less,
            "<=" => BinaryOp.LessOrEqual,
            ">" => BinaryOp.Greater,
            ">=" => BinaryOp.GreaterOrEqual,
            "==" => BinaryOp.Equal,
            "!=" => BinaryOp.NotEqual,
            _ => null
        };

        Expr Additive(int depth)
        {
            var left = Multiplicative(depth);
            while (Next(out var t) && t.Kind == Tok.Op && t.Text is "+" or "-")
            {
                _pos++;
                var op = t.Text == "+" ? BinaryOp.Add : BinaryOp.Subtract;
                left = new BinaryExpr(op, left, Multiplicative(depth));
            }
            return left;
        }

        Expr Multiplicative(int depth)
        {
            var left = Unary(depth);
            while (Next(out var t) && t.Kind == Tok.Op && t.Text is "*" or "/")
            {
                _pos++;
                var op = t.Text == "*" ? BinaryOp.Multiply : BinaryOp.Divide;
                left = new BinaryExpr(op, left, Unary(depth));
            }
            return left;
        }

        Expr Unary(int depth)
        {
            if (Next(out var t) && t.Kind == Tok.Op && t.Text == "-")
            {
                _pos++;
                return new UnaryExpr(UnaryOp.Negate, Unary(Deeper(depth)));
            }
            return Primary(depth);
        }

        Expr Primary(int depth)
        {
            if (!Next(out var t))
                throw new Refused(line, "this condition ends where a number, a name or a bracket should be");

            switch (t.Kind)
            {
                case Tok.Number:
                    _pos++;
                    return new NumberLiteral(t.Number);

                case Tok.LParen:
                    _pos++;
                    var inner = Or(Deeper(depth));
                    Expect(Tok.RParen, ")", "to close a bracket");
                    return inner;

                case Tok.Name:
                    return NamedValue(depth);

                default:
                    throw new Refused(line,
                        $"`{Clip(t.Text)}` is not where a condition can start. A value is a number, a bar series, " +
                        "a constant, an indicator, a feature or a bracketed condition");
            }
        }

        Expr NamedValue(int depth)
        {
            var name = Take().Text;

            if (name is "true" or "false") return new BoolLiteral(name == "true");

            if (name is "crosses_above" or "crosses_below")
            {
                Expect(Tok.LParen, "(", $"after `{name}`");
                var left = Or(Deeper(depth));
                Expect(Tok.Comma, ",", $"between the two sides of `{name}`");
                var right = Or(Deeper(depth));
                Expect(Tok.RParen, ")", $"to close `{name}`");
                return new CrossExpr(name == "crosses_above" ? CrossDirection.Above : CrossDirection.Below, left, right);
            }

            if (constants.TryGetValue(name, out var constant))
            {
                if (NextIs(Tok.LBracket, "["))
                    throw new Refused(line,
                        $"`{name}` is a constant, and a constant has no history: it is the same number on every bar");
                return constant.Type == ValueKind.Number
                    ? new NumberLiteral(constant.Number)
                    : new BoolLiteral(constant.Boolean);
            }

            var back = History(name);

            if (TrySeries(name, out var series)) return new SeriesRef(series, back);
            if (indicators.ContainsKey(name)) return new IndicatorRef(name, back);
            if (features.ContainsKey(name)) return new FeatureRef(name, back);

            throw new Refused(line,
                $"`{Clip(name)}` is not a bar series, a declared constant or a declared indicator, nor a declared feature. " +
                $"The bar series are: {SeriesList}");
        }

        /// <summary>`close[2]` — the bar offset, or 0 when there is no bracket. Only a literal count, never an expression.</summary>
        int History(string name)
        {
            if (!NextIs(Tok.LBracket, "[")) return 0;
            _pos++;

            if (!Next(out var t) || t.Kind != Tok.Number)
                throw new Refused(line,
                    $"`{name}[...]` takes a plain whole number of bars back, and nothing else: a history depth that " +
                    "was itself computed would be a lookback nobody can bound before the program runs");
            _pos++;

            Expect(Tok.RBracket, "]", $"to close `{name}[`");

            if (t.Number != decimal.Truncate(t.Number) || t.Number < 0m)
                throw new Refused(line, $"`{name}[{t.Text}]` is not a whole number of bars back");
            if (t.Number > StrategyLimits.MaxHistoryDepth)
                throw new Refused(line,
                    $"`{name}[{t.Text}]` reaches further back than the {StrategyLimits.MaxHistoryDepth} bars a history " +
                    "reference may reach. A lookback has to be bounded before the program runs, not after");

            return (int)t.Number;
        }

        /// <summary>
        /// One level further in. Only the NESTING constructs count — a bracket, a negation, a
        /// crossing's arguments — because the precedence chain below each of them is a fixed number
        /// of frames and counting it would make the limit a fact about this parser's shape rather
        /// than about the program.
        /// </summary>
        int Deeper(int depth)
        {
            if (depth >= StrategyLimits.MaxExpressionDepth)
                throw new Refused(line,
                    $"this condition nests brackets, negations and crossings deeper than the " +
                    $"{StrategyLimits.MaxExpressionDepth} levels one condition may nest");
            return depth + 1;
        }
    }

    // ---- the small shared readers ---------------------------------------------------------------

    static string[] Words(string text) =>
        text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string Name(int line, string name, string what)
    {
        var lower = name.ToLowerInvariant();

        if (lower.Length == 0)
            throw new Refused(line, $"this {what} has no name");
        if (lower.Length > StrategyLimits.MaxIdentifierLength)
            throw new Refused(line,
                $"`{Clip(lower)}` is longer than the {StrategyLimits.MaxIdentifierLength} characters a name may be");
        if (!char.IsAsciiLetter(lower[0]) || !lower.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new Refused(line,
                $"`{Clip(name)}` is not a name: a name starts with a letter and holds letters, digits and underscores");
        if (IsReserved(lower))
            throw new Refused(line, $"`{lower}` already means something in this language, so it cannot name a {what}");

        return lower;
    }

    /// <summary>A number, or the value of a declared constant. The one place a numeric argument is read.</summary>
    static decimal Value(int line, string word, Dictionary<string, StrategyConstant> constants)
    {
        if (TryNumber(word, out var number)) return number;

        var lower = word.ToLowerInvariant();
        if (constants.TryGetValue(lower, out var constant))
        {
            if (constant.Type != ValueKind.Number)
                throw new Refused(line, $"`{lower}` is a true/false constant, so it is not a number");
            return constant.Number;
        }

        throw new Refused(line,
            $"`{Clip(word)}` is not a number and is not a declared constant");
    }

    /// <summary>
    /// A PERIOD, WHICH IS ABOVE ZERO. The mean of no bars is not a number: an indicator that answered
    /// one anyway would make a program's backtest the backtest of a different program, and the author
    /// — a model that wrote `sma(close, 0)` by arithmetic slip — would never see it.
    /// </summary>
    static int Period(int line, string what, string word, Dictionary<string, StrategyConstant> constants)
    {
        var period = Count(line, what + " period", Value(line, word, constants), StrategyLimits.MaxLookbackBars);
        if (period <= 0)
            throw new Refused(line, $"{what} needs a period above 0, and this one asks for {period}");
        return period;
    }

    static int Count(Decl d, string word, Dictionary<string, StrategyConstant> constants, string what, int most) =>
        Count(d.No, what, Value(d.No, word, constants), most);

    static int Count(int line, string what, decimal value, int most)
    {
        if (value != decimal.Truncate(value))
            throw new Refused(line, $"{what} is a whole number of bars, and {Number(value)} is not");
        if (value > most)
            throw new Refused(line, $"{what} may be at most {most}, and this one is {Number(value)}");
        return (int)value;
    }

    static decimal Positive(int line, string what, decimal value)
    {
        if (value <= 0m) throw new Refused(line, $"{what} must be above 0, and {Number(value)} is not");
        return value;
    }

    static bool TryNumber(string word, out decimal value) =>
        decimal.TryParse(word, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);

    /// <summary>Trailing zeros removed, so that `1.50` and `1.5` are the same number in every message and every hash.</summary>
    internal static string Number(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>What a refusal is allowed to echo back. A refusal is read by a model; 8 KiB of it is not a message.</summary>
    static string Clip(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
