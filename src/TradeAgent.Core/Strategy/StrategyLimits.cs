namespace TradeAgent.Core.Strategy;

/// <summary>
/// EVERY BOUND THE STRATEGY LANGUAGE ENFORCES, IN ONE PLACE.
///
/// <para>A limit that is spelled at the site that enforces it is a limit nobody can read off as a
/// set, and the runner's promise (`docs/COUNCIL.md`, "The runner and the referee") is precisely a
/// SET: program size, parse work, lookback, state and per-event computation are all bounded, and a
/// program that exceeds any of them is refused rather than run slowly. So the numbers live here,
/// they are named in the refusals that cite them, and `docs/STRATEGY-LANGUAGE.md` prints this list
/// for whoever writes a program.</para>
///
/// <para>The numbers are deliberately small. The writer of a program is a cheap model asked for a
/// rule set, not an engineer tuning a kernel; a program that needs 300 nodes is not a rule set that
/// went one node over, it is a different kind of artifact and it should be refused loudly while it
/// is still text.</para>
/// </summary>
public static class StrategyLimits
{
    /// <summary>The most source text one program may be, in UTF-8 bytes.</summary>
    public const int MaxSourceBytes = 8 * 1024;

    /// <summary>The most lines one program may have, comments and blank lines included.</summary>
    public const int MaxLines = 200;

    /// <summary>The most characters one line may be.</summary>
    public const int MaxLineLength = 240;

    /// <summary>The most characters a constant or indicator name may be.</summary>
    public const int MaxIdentifierLength = 32;

    /// <summary>The most characters an instrument symbol may be.</summary>
    public const int MaxInstrumentLength = 20;

    /// <summary>The most constants one program may declare.</summary>
    public const int MaxConstants = 32;

    /// <summary>The most indicators one program may declare.</summary>
    public const int MaxIndicators = 16;

    /// <summary>The most rules one program may declare, entries and exits together.</summary>
    public const int MaxRules = 20;

    /// <summary>
    /// The most expression nodes one program may contain, summed over every rule condition. This is
    /// the per-event computation bound: the interpreter walks these nodes once per closed bar.
    /// </summary>
    public const int MaxNodes = 200;

    /// <summary>
    /// The deepest a condition may nest BRACKETS, NEGATIONS AND CROSSINGS.
    ///
    /// <para>This is what makes the parser TOTAL rather than merely careful: recursive descent on
    /// 10,000 open brackets is a stack overflow, and a stack overflow cannot be caught. The depth is
    /// checked on the way DOWN, before the recursion happens.</para>
    ///
    /// <para>It counts nesting rather than grammar levels — the precedence chain under each nesting
    /// construct is a fixed handful of frames — so eight here is about seventy frames of stack and a
    /// condition deeper than any rule set a person would want to read.</para>
    /// </summary>
    public const int MaxExpressionDepth = 8;

    /// <summary>The largest bar offset a history reference may ask for: `close[20]`, never `close[21]`.</summary>
    public const int MaxHistoryDepth = 20;

    /// <summary>
    /// The largest indicator period, and the largest warm-up a program may end up needing. One
    /// number for both, because the second is a function of the first and two numbers would let a
    /// program declare a period it can never be warm enough to use.
    /// </summary>
    public const int MaxLookbackBars = 500;

    /// <summary>The most entry windows one program may declare.</summary>
    public const int MaxEntryWindows = 4;

    /// <summary>The largest holding time a program may state, in bars.</summary>
    public const int MaxHoldBars = 10_000;

    /// <summary>
    /// The largest fixed quantity a program may ask for. The gateway's own risk policy is what
    /// actually bounds an order (`RiskPolicy.MaxOrderQuantity`); this bound exists so that an
    /// unbounded number cannot be FROZEN INTO a promoted program and then meet the policy later.
    /// </summary>
    public const decimal MaxFixedQuantity = 1_000_000m;

    /// <summary>
    /// The largest sizing fraction, for both `capital_fraction` and `risk_fraction`: one whole. A
    /// fraction above one IS leverage, and leverage is on COUNCIL's "Never:" line, so the language
    /// has no way to spell it — not a flag that defaults to off, a number that is refused.
    /// </summary>
    public const decimal MaxSizingFraction = 1.0m;

    /// <summary>The largest percentage a stop or target may be stated as.</summary>
    public const decimal MaxPercent = 100m;

    /// <summary>The largest ATR multiple a stop may be stated as.</summary>
    public const decimal MaxAtrMultiple = 100m;

    /// <summary>
    /// THE PER-EVENT OPERATION BUDGET — the runner's promise that one closed bar costs a bounded
    /// amount of work, enforced while the bar is being evaluated rather than argued about afterwards.
    ///
    /// <para>An operation is one indicator step (a rolling extreme costs its period on the bar the
    /// extreme expires from its window) or one expression node visited. The number is set ABOVE what
    /// any program the parser accepts can reach, and `EvaluatorLimitTests` pins that arithmetic:
    /// <see cref="MaxIndicators"/> extremes of <see cref="MaxLookbackBars"/> is 8,000, a crossing
    /// doubles its operands so <see cref="MaxNodes"/> is at most 400 visits — a crossing cannot nest
    /// inside a crossing, because a crossing is a true-or-false value and a crossing's sides are
    /// numbers — and the stop's own ATR is one more. 8,401 against 16,384.</para>
    ///
    /// <para>That relationship is the point. A budget a valid program could trip would refuse a
    /// program the parser accepted, which is a limit disagreeing with a limit; a budget no invalid
    /// program can trip would be a promise with nothing behind it. This one bounds the interpreter
    /// while leaving every parseable program runnable, and a run may be given a TIGHTER budget but
    /// never a wider one.</para>
    /// </summary>
    public const int MaxOperationsPerEvent = 16_384;

    /// <summary>
    /// THE STATE-SIZE LIMIT, in bytes of the decimals a run holds between bars.
    ///
    /// <para>`docs/COUNCIL.md` bounds "lookback, state, per-event computation and output". The state
    /// is the indicator windows, the values kept for history references, and the recent bars:
    /// <see cref="MaxIndicators"/> windows of <see cref="MaxLookbackBars"/> plus their kept values,
    /// plus the bar ring, is about 138 KB at the parser's own limits — so this is set at 256 KB and
    /// the same test pins the arithmetic. A program's state does not grow while it runs; the limit
    /// exists so that a WIDER limit elsewhere cannot quietly make a run unbounded.</para>
    /// </summary>
    public const int MaxStateBytes = 256 * 1024;

    /// <summary>
    /// THE ZONES A PROGRAM MAY NAME, as data rather than as an OS lookup.
    ///
    /// <para>`TimeZoneInfo.FindSystemTimeZoneById` answers differently on Windows and on Unix, and
    /// this build sets `InvariantGlobalization`, so a program whose meaning depended on that lookup
    /// would parse on one machine and be refused on another — and a promoted program's identity
    /// would depend on the machine that hashed it. A short allowlist is deterministic everywhere,
    /// and adding a zone is a one-line data change with a version bump beside it.</para>
    ///
    /// <para>Resolving one of these to wall-clock instants is the evaluator's job, and the evaluator
    /// records the zone data it used. The parser only records WHICH zone was asked for.</para>
    /// </summary>
    public static readonly string[] TimeZones =
        ["UTC", "America/New_York", "America/Chicago", "Europe/London", "Europe/Berlin", "Asia/Tokyo"];
}
