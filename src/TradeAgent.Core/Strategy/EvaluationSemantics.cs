namespace TradeAgent.Core.Strategy;

/// <summary>
/// WHAT A VERDICT'S EVIDENCE MEANS — the evaluator's version and the language manifest, which are the
/// two facts a promotion's standing is withdrawn on.
///
/// <para><b>Why these two and not the app's release number.</b> <c>docs/COUNCIL.md</c> rule 9 binds
/// promotion to "code, parameters, dependencies, data, evaluator, execution-and-cost model and
/// scoring-policy versions". The program's code and parameters, read under the semantic-version
/// manifest (<see cref="StrategyVersions.Manifest"/>), are its id; the evaluator is
/// <see cref="Referee.EvaluatorVersion"/> — the backtest that produced the trace, the metrics computed
/// from it and the scoring applied to them. Between them they decide everything a program DOES over a
/// given set of bars and what that is judged to be worth. The release number decides none of it: it
/// moves with every update whether or not anything a program means has moved, and binding standing to
/// it withdrew every verdict, and ended every paper run, on every release.</para>
///
/// <para><b>What holds a build to the two numbers</b> is <c>EvaluationGoldenVectorTests</c>: output over
/// fixed bars that changes while neither number moves fails the build and says to bump one.</para>
/// </summary>
public static class EvaluationSemantics
{
    /// <summary>The semantics this build evaluates under, spelled as <see cref="Of"/> spells them.</summary>
    public static string Current => Of(Referee.EvaluatorVersion, StrategyVersions.Manifest);

    /// <summary>
    /// One pair as a person reads it — <c>evaluator backtest=1;metrics=1;scoring=1 with manifest
    /// language=1;indicators=1;calendar=1</c> — and the spelling every withdrawal names them in.
    /// </summary>
    public static string Of(string evaluatorVersion, string manifest) =>
        $"evaluator {evaluatorVersion} with manifest {manifest}";
}
