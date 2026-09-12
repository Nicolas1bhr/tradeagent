namespace TradeAgent.Core.Strategy;

/// <summary>
/// THE SEMANTIC VERSIONS A STRATEGY'S IDENTITY IS HASHED OVER.
///
/// <para>`docs/COUNCIL.md`: the identity of a strategy is "SHA-256 over the canonical typed program,
/// parameters and semantic-version manifest". The manifest is the half a reader cannot see in the
/// program text: the same twelve lines mean one thing under an EMA seeded with a simple mean and
/// another under an EMA seeded with its first value, and a backtest result recorded against the
/// first is not evidence about the second.</para>
///
/// <para>So the numbers are here, they are hashed into every <see cref="StrategyProgram.StrategyId"/>,
/// and moving one re-identifies every program in the installation — which is the point. A result is
/// attached to an id; an id that survived a semantics change would attach yesterday's result to
/// today's meaning.</para>
/// </summary>
public static class StrategyVersions
{
    /// <summary>
    /// The concrete syntax and the typed AST: which declarations exist, what an expression may be,
    /// what is refused. 1 is the language `docs/STRATEGY-LANGUAGE.md` specifies.
    /// </summary>
    public const int LanguageVersion = 1;

    /// <summary>
    /// The numeric meaning and the initialisation of every indicator — the table in
    /// `docs/STRATEGY-LANGUAGE.md`. Separate from the language version because a fix to Wilder's
    /// seeding changes what a program MEANS without changing what parses.
    /// </summary>
    public const int IndicatorSemanticsVersion = 1;

    /// <summary>
    /// The session calendar the time filters are read against: which zones may be named, what a
    /// weekday is, how a session with no bars is treated. Separate again, because a holiday table is
    /// data that changes without either of the two above moving.
    /// </summary>
    public const int CalendarVersion = 1;

    /// <summary>
    /// The manifest text, exactly as it is hashed. One line, no trailing newline, stable field order:
    /// it is an input to a hash that is compared against figures written by earlier builds, so its
    /// spelling is part of the contract and not a formatting choice.
    /// </summary>
    public static string Manifest =>
        $"language={LanguageVersion};indicators={IndicatorSemanticsVersion};calendar={CalendarVersion}";
}
