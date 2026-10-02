using System.Globalization;
using TradeAgent.Core.Db;

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

    /// <summary>
    /// The <c>kv</c> key family <see cref="RecordInForce"/> writes: this prefix and then
    /// <see cref="Of"/>'s spelling of one pair.
    /// </summary>
    public const string SinceKeyPrefix = "evaluation_semantics_since:";

    /// <summary>
    /// WHEN THIS INSTALLATION FIRST EVALUATED UNDER THIS BUILD'S SEMANTICS — written once per pair, the
    /// first time a build that has them opens the ledger, and never moved after.
    ///
    /// <para><b>Why it is recorded at all.</b> A promotion's standing is computed at read time and nothing
    /// is written when it is withdrawn, which is right: a withdrawal that depended on a sweep having run
    /// would be the sweep that did not run. But it leaves "withdrawn WHEN" with no answer, and the only
    /// true one is the moment the first build with other semantics began running here. This is that
    /// moment, measured by the app at its own start — a fact about the installation, kept in the same
    /// write-once way as every other one (<see cref="Database.AddKvOnce"/>), and read by
    /// <see cref="WithdrawnAt"/> and by nothing that decides a standing.</para>
    ///
    /// <para>Answers whether this call is the one that wrote it.</para>
    /// </summary>
    public static bool RecordInForce(Database db, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.AddKvOnce(SinceKeyPrefix + Current, Sql.T(now));
    }

    /// <summary>
    /// WHEN A VERDICT TAKEN UNDER THESE SEMANTICS WAS WITHDRAWN BY OTHER ONES: the earliest instant
    /// <see cref="RecordInForce"/> recorded for a pair that is NOT the one it was judged under and that
    /// came after the judgement — or null, because no such instant was recorded (a ledger opened only by
    /// builds older than this record, or a judgement written after every recorded change).
    ///
    /// <para>The earliest and not the newest: across two bumps, a verdict was withdrawn by the first one,
    /// and the second changed nothing more about it.</para>
    /// </summary>
    public static DateTimeOffset? WithdrawnAt(
        Database db, string evaluatorVersion, string manifest, DateTimeOffset judgedAt)
    {
        ArgumentNullException.ThrowIfNull(db);
        var judgedUnder = Of(evaluatorVersion, manifest);
        DateTimeOffset? first = null;

        foreach (var (key, value) in db.KvStartingWith(SinceKeyPrefix))
        {
            if (string.Equals(key[SinceKeyPrefix.Length..], judgedUnder, StringComparison.Ordinal)) continue;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since)) continue;
            if (since <= judgedAt) continue;
            if (first is null || since < first) first = since;
        }

        return first;
    }
}
