using System.Globalization;
using System.Text;

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
/// <para><b>Three passes over the lines, not one.</b> Constants first, then indicators, then
/// everything else. That makes declaration ORDER irrelevant — a program whose `const` lines are at
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
        "instrument, timezone, const, indicator, size, stop, target, max_hold_bars, weekdays, " +
        "entry_window, opening_range, session_exit, exit, entry";

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
        "instrument" or "timezone" or "const" or "indicator" or "size" or "stop" or "target"
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
        var settings = ReadSettings(decls, constants);
        var rules = ReadRules(decls, constants, indicators);

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

        var time = new TimeFilters(
            settings.TimeZone ?? "UTC",
            settings.Days ?? Weekdays.All,
            settings.EntryWindows,
            settings.OpeningRange,
            settings.SessionExit);

        return StrategyParse.Yes(new StrategyProgram(
            text,
            instrument,
            [.. constants.Values.OrderBy(c => c.Name, StringComparer.Ordinal)],
            [.. indicators.Values.OrderBy(i => i.Name, StringComparer.Ordinal)],
            rules,
            sizing,
            settings.Stop,
            settings.Target,
            settings.MaxHoldBars,
            time));
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

            if (raw.Length > StrategyLimits.MaxLineLength)
                throw new Refused(no,
                    $"a line may be at most {StrategyLimits.MaxLineLength} characters and this one is {raw.Length}");

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

            if (!IsKeyword(keyword))
                throw new Refused(no,
                    $"`{Clip(keyword)}` is not a declaration this language has. The declarations are: {Keywords}");

            decls.Add(new Decl(no, keyword, rest));
        }

        return decls;
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

    static Sizing ReadSizing(Decl d, Dictionary<string, StrategyConstant> constants)
    {
        var words = Words(d.Rest);
        if (words.Length != 2)
            throw new Refused(d.No,
                "size reads `size fixed <quantity>`, `size capital_fraction <fraction>` or `size risk_fraction <fraction>`");

        var value = Value(d.No, words[1], constants);
        switch (words[0].ToLowerInvariant())
        {
            case "fixed":
                if (value <= 0m || value > StrategyLimits.MaxFixedQuantity)
                    throw new Refused(d.No,
                        $"a fixed quantity must be above 0 and at most {StrategyLimits.MaxFixedQuantity}, and this one is {Number(value)}");
                return new Sizing(SizingKind.FixedQuantity, value);

            case "capital_fraction":
            case "risk_fraction":
                if (value <= 0m || value > StrategyLimits.MaxSizingFraction)
                    throw new Refused(d.No,
                        $"a fraction must be above 0 and at most {Number(StrategyLimits.MaxSizingFraction)} — " +
                        $"{Number(value)} is more than everything there is, which is leverage, and leverage is not " +
                        "something this language can say");
                return new Sizing(
                    words[0].ToLowerInvariant() == "capital_fraction" ? SizingKind.CapitalFraction : SizingKind.EquityRiskFraction,
                    value);

            default:
                throw new Refused(d.No,
                    $"`{Clip(words[0])}` is not a way to size. The three are: fixed, capital_fraction, risk_fraction");
        }
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
        Dictionary<string, IndicatorDecl> indicators)
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

            var condition = ParseExpression(d.No, d.Rest[words[0].Length..].Trim(), constants, indicators);

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
        Dictionary<string, IndicatorDecl> indicators)
    {
        if (text.Length == 0) throw new Refused(line, "this rule has no condition after `when`");

        var tokens = Tokenise(line, text);
        var parser = new Expressions(line, tokens, constants, indicators);
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
        Dictionary<string, IndicatorDecl> indicators)
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
                        "a constant, an indicator or a bracketed condition");
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

            throw new Refused(line,
                $"`{Clip(name)}` is not a bar series, a declared constant or a declared indicator. " +
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
