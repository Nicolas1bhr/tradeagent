using System.Globalization;

namespace TradeAgent.Core.Features;

/// <summary>
/// THE SEMANTIC VERSION A FEATURE'S IDENTITY IS HASHED OVER (<c>U-features</c>; <c>docs/EDGE-FACTORY.md</c> § 4.3, § 6.2).
///
/// <para>A feature's id is <c>Sha256Hex.Of(canonical + "\n" + Manifest)</c>. The canonical text says what the spec
/// ASKS FOR; this says what the app COMPUTES from it — which rows count at an instant, when a value is absent, how a
/// field is read as a decimal, what a mean or a rank is. The same spec text under two meanings is two features, so a
/// change to any of that moves <see cref="SemanticsVersion"/> and re-identifies every feature, which is the point: a
/// result bound to an id must not survive a change to what the id computes.</para>
///
/// <para><b>Its own manifest, and <c>StrategyVersions.Manifest</c> does not move for it.</b> That manifest is inside
/// every program's id; adding features to the app re-identifies no program. A program that binds a feature (v2a)
/// binds this id, and this version is inside it.</para>
///
/// <para><c>FeatureGoldenVectorTests</c> pins every kind's id and output over fixed rows under this number: a change
/// to either with the number unmoved fails there and says to bump it.</para>
/// </summary>
public static class FeatureVersions
{
    /// <summary>
    /// What a feature means: the evaluator's gate, its absences, its arithmetic and the canonical layout. 1 is the
    /// meaning <c>docs/CONTRACTS.md</c> "Features" states.
    /// </summary>
    public const int SemanticsVersion = 1;

    /// <summary>
    /// The manifest text, exactly as it is hashed: one line, no trailing newline. Its spelling is part of every
    /// feature id, so it is a contract and not a formatting choice.
    /// </summary>
    public static string Manifest => string.Create(CultureInfo.InvariantCulture, $"features={SemanticsVersion}");
}
