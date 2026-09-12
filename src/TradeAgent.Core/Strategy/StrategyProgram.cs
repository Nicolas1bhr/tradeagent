namespace TradeAgent.Core.Strategy;

/// <summary>
/// ONE FROZEN STRATEGY: the typed program, and nothing that is not part of its meaning.
///
/// <para>It can only be built by <see cref="StrategyParser"/> — the constructor is internal — and
/// every property is get-only, because `docs/COUNCIL.md` makes immutability the thing a promoted
/// version IS: "a promoted strategy is an immutable version whose frozen semantics and event
/// interface are the same in backtest, paper and live". A program that could be edited after it was
/// identified would make every result recorded against its id a claim about a different program.</para>
///
/// <para>What it deliberately does NOT have: an instrument list (one spot instrument), a side (long
/// or flat), a leverage factor, a pyramiding count, a data source, a clock, a seed. Those are absent
/// from the type, not defaulted in it.</para>
/// </summary>
public sealed class StrategyProgram
{
    internal StrategyProgram(
        string source,
        string instrument,
        IReadOnlyList<StrategyConstant> constants,
        IReadOnlyList<IndicatorDecl> indicators,
        IReadOnlyList<StrategyRule> rules,
        Sizing sizing,
        StopRule stop,
        TargetRule target,
        int? maxHoldBars,
        TimeFilters time)
    {
        Source = source;
        Instrument = instrument;
        Constants = constants;
        Indicators = indicators;
        Rules = rules;
        Sizing = sizing;
        Stop = stop;
        Target = target;
        MaxHoldBars = maxHoldBars;
        Time = time;

        // Computed once, here, because a frozen program's identity must not depend on when it is
        // asked for — and because every caller that compares two programs compares these.
        Canonical = StrategyCanonical.Of(this);
        Parameters = StrategyCanonical.Parameters(this);
        Manifest = StrategyVersions.Manifest;
        StrategyId = Sha256Hex.Of($"{Canonical}\n{Parameters}\n{Manifest}");
    }

    /// <summary>
    /// THE SOURCE TEXT, VERBATIM, KEPT BESIDE THE CANONICAL FORM.
    ///
    /// Comments, spacing and declaration order are not part of the program's identity, and they are
    /// most of what a reader needs in order to understand why a rule is there. Both are kept: the
    /// canonical form is what is hashed, the source is what is read.
    /// </summary>
    public string Source { get; }

    /// <summary>The one spot instrument. A program trades this and cannot name a second.</summary>
    public string Instrument { get; }

    /// <summary>The declared constants, ordered by name. Their values are also inlined into the expressions that used them.</summary>
    public IReadOnlyList<StrategyConstant> Constants { get; }

    /// <summary>The declared indicators, ordered by name.</summary>
    public IReadOnlyList<IndicatorDecl> Indicators { get; }

    /// <summary>The rules in declared order, every exit before every entry.</summary>
    public IReadOnlyList<StrategyRule> Rules { get; }

    public Sizing Sizing { get; }

    /// <summary><see cref="StopKind.None"/> when the program declared none.</summary>
    public StopRule Stop { get; }

    /// <summary><see cref="TargetKind.None"/> when the program declared none.</summary>
    public TargetRule Target { get; }

    /// <summary>Null when the program declared no holding limit.</summary>
    public int? MaxHoldBars { get; }

    public TimeFilters Time { get; }

    /// <summary>Every exit rule, in declared order. Exits are evaluated before entries.</summary>
    public IEnumerable<StrategyRule> ExitRules => Rules.Where(r => r.Kind == RuleKind.Exit);

    /// <summary>Every entry rule, in declared order.</summary>
    public IEnumerable<StrategyRule> EntryRules => Rules.Where(r => r.Kind == RuleKind.Entry);

    /// <summary>
    /// The expression nodes in every rule condition, which is what one bar costs to evaluate and
    /// what <see cref="StrategyLimits.MaxNodes"/> bounds.
    /// </summary>
    public int NodeCount => Rules.Sum(r => r.Condition.NodeCount);

    /// <summary>
    /// THE TYPED, ORDERED FORM THIS PROGRAM IS HASHED IN — `StrategyCanonical`. Free of comments, of
    /// the source's spacing and letter case, and of the order the declarations happened to be written
    /// in; the rules keep their order, because their order is their meaning.
    /// </summary>
    public string Canonical { get; }

    /// <summary>The declared constants, sorted, as `name=type:value` lines. The other half of the identity.</summary>
    public string Parameters { get; }

    /// <summary>The semantic versions in force — `StrategyVersions.Manifest` — hashed in beside the program.</summary>
    public string Manifest { get; }

    /// <summary>
    /// THE IDENTITY: `Sha256Hex.Of(Canonical + "\n" + Parameters + "\n" + Manifest)`.
    ///
    /// <para>`docs/COUNCIL.md`: "SHA-256 over the canonical typed program, parameters and
    /// semantic-version manifest, source retained". Everything the referee does hangs off this figure
    /// — lineage, the trial budget a submission is charged against, which results may be pooled — so
    /// the three parts are joined in that order, with a newline between them, and each of the three is
    /// spelled in exactly one place.</para>
    ///
    /// <para>Promotion binds MORE than this: the interpreter build, the dataset, the execution model
    /// and the evaluation policy. That is a different record, and it names this id.</para>
    /// </summary>
    public string StrategyId { get; }
}
