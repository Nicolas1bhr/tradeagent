namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE DECLARATION KINDS A PROGRAM CAN USE, WHICH OF THEM A PROGRAM REQUIRES, AND WHICH OF THEM A READER IMPLEMENTS
/// (<c>U-language-v2a</c> item 2; <c>docs/EDGE-FACTORY.md</c> § 4.4; R05 row 10).
///
/// <para><b>Every declaration a program uses is REQUIRED</b>, because every one of them constrains its orders, its risk
/// or how it is evaluated: an instrument and a size say what is bought; a stop, a target, a holding limit and a session
/// exit say when a position must end; the bounds say how stale a decision may be; a bar, a zone, a window, a weekday, an
/// indicator, a constant, a feature and a rule say what is decided. R05 row 10 is the failure this prevents: "a silently
/// dropped stop is an unprotected live strategy". So a reader that does not implement one of them REFUSES the program in
/// words — it never runs the rest of it — and the refusal names the declarations it does not implement.</para>
///
/// <para><b>Comments are the one optional part.</b> They are kept byte for byte in <see cref="StrategyProgram.Source"/>
/// and are outside the id; no reader needs to understand them, and none refuses them.</para>
///
/// <para><b>What a program requires is read off the TYPED program, never off its text</b>
/// (<see cref="StrategyProgram.Requires"/>): two spellings of one program — <c>timezone UTC</c> written or left out,
/// <c>bars 1m</c> or nothing — are one id and require the same. A declaration that states the default requires
/// nothing, because every reader already runs the default.</para>
/// </summary>
public static class StrategyDeclarations
{
    public const string Instrument = "instrument";
    public const string TimeZone = "timezone";
    public const string Bars = "bars";
    public const string Timeframe = "timeframe";
    public const string DataFreshness = "data_freshness";
    public const string MaxDecisionAge = "max_decision_age";
    public const string Const = "const";
    public const string Indicator = "indicator";
    public const string Feature = "feature";
    public const string Size = "size";
    public const string Stop = "stop";
    public const string Target = "target";
    public const string MaxHoldBars = "max_hold_bars";
    public const string Weekdays = "weekdays";
    public const string EntryWindow = "entry_window";
    public const string OpeningRange = "opening_range";
    public const string SessionExit = "session_exit";
    public const string Exit = "exit";
    public const string Entry = "entry";

    /// <summary>Every declaration kind this build's parser reads, in the order its refusals name them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Instrument, TimeZone, Bars, Timeframe, DataFreshness, MaxDecisionAge, Const, Indicator, Feature, Size,
        Stop, Target, MaxHoldBars, Weekdays, EntryWindow, OpeningRange, SessionExit, Exit, Entry
    ];

    /// <summary>
    /// THE KINDS <paramref name="p"/> REQUIRES, in <see cref="All"/>'s order: every kind whose meaning the typed program
    /// carries. The instrument, the size and an entry always; the rest where the program states something other than
    /// the default.
    /// </summary>
    internal static IReadOnlyList<string> Of(StrategyProgram p)
    {
        var uses = new HashSet<string>(StringComparer.Ordinal) { Instrument, Size, Entry };

        if (!string.Equals(p.Time.TimeZone, TimeFilters.Default.TimeZone, StringComparison.Ordinal)) uses.Add(TimeZone);
        if (p.Bars != StrategyBars.OneMinute) uses.Add(Bars);
        if (p.Freshness is not null) uses.UnionWith([Timeframe, DataFreshness, MaxDecisionAge]);
        if (p.Constants.Count > 0) uses.Add(Const);
        if (p.Indicators.Count > 0) uses.Add(Indicator);
        if (p.Features.Count > 0) uses.Add(Feature);
        if (p.Stop.Kind != StopKind.None) uses.Add(Stop);
        if (p.Target.Kind != TargetKind.None) uses.Add(Target);
        if (p.MaxHoldBars is not null) uses.Add(MaxHoldBars);
        if (p.Time.Days != global::TradeAgent.Core.Strategy.Weekdays.All) uses.Add(Weekdays);
        if (p.Time.EntryWindows.Count > 0) uses.Add(EntryWindow);
        if (p.Time.OpeningRange is not null) uses.Add(OpeningRange);
        if (p.Time.SessionExit is not null) uses.Add(SessionExit);
        if (p.Rules.Any(r => r.Kind == RuleKind.Exit)) uses.Add(Exit);

        return [.. All.Where(uses.Contains)];
    }

    /// <summary>
    /// WHY <paramref name="reader"/>, WHICH IMPLEMENTS <paramref name="implements"/>, WILL NOT RUN <paramref name="program"/>
    /// — the required declarations it does not implement, named — or null, because it implements every one.
    /// </summary>
    public static string? Refusal(StrategyProgram program, IReadOnlyCollection<string> implements, string reader)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(implements);

        var missing = program.Requires.Where(k => !implements.Contains(k)).ToList();
        if (missing.Count == 0) return null;

        return $"this program requires {string.Join(" and ", missing.Select(k => $"`{k}`"))}, which {reader} does not "
               + "implement. Every declaration a program uses constrains its orders, its risk or how it is evaluated, so "
               + "a reader that left one out would run a program nobody judged — it is refused rather than run without it";
    }
}
