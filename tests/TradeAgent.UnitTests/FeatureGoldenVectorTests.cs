using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// (g) THE FEATURE GOLDEN VECTORS — EVERY KIND'S ID AND OUTPUT OVER FIXED ROWS, PINNED TOGETHER WITH THE NUMBER THAT
/// DECLARES WHAT A FEATURE MEANS (<c>U-features</c>; <see cref="FeatureVersions.Manifest"/>).
///
/// <para><b>Why.</b> A program that binds a feature (v2a) binds its id, and the id is the spec's canonical text plus
/// <c>features=N</c>. That makes N a DECLARATION: what the evaluator computes from a spec, and how the spec is written
/// down, may change only together with it. These vectors check the declaration — a changed canonical text, gate,
/// absence or arithmetic with N unmoved fails here and says to bump N; a bump re-identifies every feature, which is
/// the point.</para>
///
/// <para><b>What is pinned, per vector:</b> its id, and the SHA-256 of its whole output over the fixture — every
/// instant's value or absence in words, class, rows read and their digest. <b>The fixture is built here and pinned
/// by its own hash</b>, so a moved input says so with a message of its own rather than as a semantics change.</para>
/// </summary>
public class FeatureGoldenVectorTests(ITestOutputHelper log)
{
    /// <summary>The semantics every pin below was computed under.</summary>
    const string PinnedManifest = "features=1";

    /// <summary>The SHA-256 of the fixture rows as <see cref="FixtureText"/> spells them.</summary>
    const string FixtureSha256 = "42f53354f921aa668434b33685512a6830dac40381bc42d573b35f12da94fd4e";

    static readonly DateTimeOffset Start = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One vector: a name and a spec, every key stated.</summary>
    sealed record Vector(string Name, string Spec);

    /// <summary>What one vector produced.</summary>
    sealed record Pin(string Name, string Id, string Output)
    {
        public string AsCode => $"        new(\"{Name}\", \"{Id}\",\n            \"{Output}\"),";
    }

    static string Json(string kind, string field, string rest) =>
        $$"""{"kind":"{{kind}}","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"{{field}}"},{{rest}}}""";

    static readonly Vector[] Vectors =
    [
        new("latest-funding", Json("latest", "lastFundingRate", "\"latency_s\":5,\"max_age_s\":180")),
        new("change-diff-mark", Json("change", "markPrice", "\"mode\":\"diff\",\"lookback_s\":600,\"latency_s\":5,\"max_age_s\":180")),
        new("change-ratio-funding", Json("change", "lastFundingRate", "\"mode\":\"ratio\",\"lookback_s\":300,\"latency_s\":5,\"max_age_s\":180")),
        new("mean-mark", Json("mean", "markPrice", "\"window_s\":600,\"min_rows\":5,\"latency_s\":5,\"max_age_s\":180")),
        new("min-funding", Json("min", "lastFundingRate", "\"window_s\":900,\"min_rows\":10,\"latency_s\":10,\"max_age_s\":300")),
        new("max-mark", Json("max", "markPrice", "\"window_s\":300,\"min_rows\":3,\"latency_s\":0,\"max_age_s\":120")),
        new("pct-rank-mark", Json("pct-rank", "markPrice", "\"window_s\":1800,\"min_rows\":20,\"latency_s\":5,\"max_age_s\":180")),
    ];

    /// <summary>THE PINS — computed under <see cref="PinnedManifest"/>. A failure prints this block as it now computes.</summary>
    static readonly Pin[] Pins =
    [
        new("latest-funding", "d9f3e14e5dd8ecc13196b4d48faa14bc818073be2626f6ff2eda24e6d6091179",
            "a79d26e098ae936caac3d286b7068b425b0138031d5044c324ce9383b07f0800"),
        new("change-diff-mark", "7b2132f7d3f828b0809d80bcbbc34ab67ea08dbd92232890d138fc8d577f5899",
            "fc0d8660cc9145253edefe88e56e15bff1c48475e7c3b694965175a3fdb1e10f"),
        new("change-ratio-funding", "dbf64eac18e5796721cf45538a33896b32f1814c0d7c43136fb06214c86cba74",
            "f468cc917d086e8a760e55deea301553a538b54a459cc27d58687deb06887046"),
        new("mean-mark", "936b905f9fe777a1f66c19c5f11c81110bc2bb74da7ce75a128554e103ea944f",
            "cc748556697baf8120709d8a8cdff9390ac327611e890ebd8100d952f00242c4"),
        new("min-funding", "010a86b0f493007c14b575806e590baa8d95219ee49407df60048ce7fbfaeb50",
            "43687bcc762b908da488112f97573edb1818756b1e624b6cb16e0b9cf3264f6e"),
        new("max-mark", "191a5851cee9ade570d69e5aa6400a0cd001fc65e98951ada83db4d16bc77998",
            "7bd53eb199afbdf2f2013d29d955c991a374e09904e79626cf033db4478cb400"),
        new("pct-rank-mark", "9737f1eae6b8aaa8e6915da6b27c4294e5d3848a4204ccabc0e5a5ad3dffdab3",
            "d7bff28b54c0004646a36f1011d5599cf1488a36bf546281120f3fef24d392ec"),
    ];

    // ---- the fixture ---------------------------------------------------------------------------------------

    /// <summary>
    /// AN HOUR OF ONE SERIES, MINUTE BY MINUTE, with what a recording really holds: readings two seconds after their
    /// stamp (O-LIVE); a gap of five minutes; a reading that arrived ten minutes late (O-ARCH); a datum revised five
    /// minutes later (its revision O-ARCH); a payload the screen withheld; a field that is not a decimal; funding rates
    /// that are exactly zero. Ids in arrival order, as the tape writes them.
    /// </summary>
    static List<TapeObservation> Fixture()
    {
        var drafts = new List<(DateTimeOffset Stamped, DateTimeOffset Arrived, string Funding, string Mark, string Class, int Revision, bool Withheld)>();
        for (var minute = 0; minute < 60; minute++)
        {
            if (minute is >= 20 and < 25) continue;
            var stamped = Start.AddMinutes(minute);
            var funding = minute == 55 ? "n/a" : (((minute * 7) % 11 - 5) * 0.00001m).ToString("0.00000000", CultureInfo.InvariantCulture);
            var mark = (85_000m + minute * 37 % 101 + minute % 4 * 0.25m).ToString("0.00000000", CultureInfo.InvariantCulture);
            var late = minute == 30;
            drafts.Add((stamped, stamped.AddSeconds(late ? 600 : 2), funding, mark, late ? TapeClass.Arch : TapeClass.Live, 1, minute == 50));
            if (minute == 40)
                drafts.Add((stamped, stamped.AddMinutes(5), funding, "85100.00000000", TapeClass.Arch, 2, false));
        }

        var rows = new List<TapeObservation>();
        foreach (var d in drafts.OrderBy(d => d.Arrived).ThenBy(d => d.Stamped))
        {
            var ms = d.Stamped.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var payload = TapeJson.Canonical(
                $$"""{"lastFundingRate":"{{d.Funding}}","markPrice":"{{d.Mark}}","symbol":"BTCUSDT","time":{{ms}}}""");
            var id = rows.Count + 1L;
            rows.Add(new TapeObservation(id, TapeSourceCatalog.Premium, "premium-index", "BTCUSDT", d.Stamped, d.Arrived, id,
                TapeStore.NaturalKey("BTCUSDT", d.Stamped), d.Revision, TapeJson.Sha256(payload), d.Withheld ? null : payload,
                d.Class, d.Withheld ? new TapeQuarantine(TapeScreen.InstructionOverride, 2) : null));
        }
        return rows;
    }

    static string FixtureText(IEnumerable<TapeObservation> rows) => string.Concat(rows.Select(r =>
        $"{r.Id}|{FeatureEvaluatorStamp(r.SourceTime)}|{FeatureEvaluatorStamp(r.ReceivedAt)}|{r.Revision}|{r.EvidenceClass}|{r.PayloadSha256}|{r.Payload ?? "-"}\n"));

    static string FeatureEvaluatorStamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The instants every vector is asked at: the hour and ten minutes past it, every thirty seconds.</summary>
    static IEnumerable<DateTimeOffset> Instants() => Enumerable.Range(0, 141).Select(i => Start.AddSeconds(30 * i));

    static string Line(FeatureValue v) =>
        $"{FeatureEvaluatorStamp(v.At)} "
        + (v.Value is { } d ? d.ToString("0.############################", CultureInfo.InvariantCulture) : "absent: " + v.Absent)
        + $" [{v.Class ?? "-"}] rows={v.Rows} {v.RowsSha256}";

    sealed record Run(Vector Vector, FeatureSpec Spec, IReadOnlyList<FeatureValue> Values, Pin Pin);

    static List<Run> RunAll()
    {
        var rows = Fixture();
        return Vectors.Select(v =>
        {
            var parse = FeatureSpec.Parse(v.Spec);
            Assert.True(parse.Ok, $"golden vector {v.Name} no longer parses: {parse.Why}");
            var values = Instants().Select(t => FeatureEvaluator.At(parse.Spec!, rows, t)).ToList();
            var output = Sha256Hex.Of(string.Join("\n", values.Select(Line)));
            return new Run(v, parse.Spec!, values, new Pin(v.Name, parse.Spec!.Id, output));
        }).ToList();
    }

    static string Repin(IEnumerable<Run> runs, string fixture) =>
        $"    const string PinnedManifest = \"{FeatureVersions.Manifest}\";\n"
        + $"    const string FixtureSha256 = \"{fixture}\";\n"
        + string.Join("\n", runs.Select(r => r.Pin.AsCode));

    /// <summary>
    /// (g) A CHANGED FEATURE ID OR OUTPUT WITHOUT A VERSION BUMP FAILS THE GOLDEN VECTORS. Three checks, each failure
    /// saying one thing: the fixture is the one pinned; the pins were computed under the manifest this build declares;
    /// and every vector's id and output are what was pinned — with <c>features=</c> unmoved, a difference is a meaning
    /// that changed without a bump.
    /// </summary>
    [Fact]
    public void Changed_feature_output_without_a_version_bump_fails_the_golden_vectors()
    {
        var fixture = Sha256Hex.Of(FixtureText(Fixture()));
        var runs = RunAll();
        foreach (var run in runs)
            foreach (var v in run.Values.Where((_, i) => i % 20 == 0))
                log.WriteLine($"{run.Vector.Name,-21} {Line(v)}");

        Assert.True(fixture == FixtureSha256,
            $"the golden feature fixture hashes {fixture} and was pinned at {FixtureSha256}. A golden vector is only a "
            + "tripwire if its input cannot move: put the rows back, or — if the change is meant — say why in the commit "
            + "and re-pin every vector:\n" + Repin(runs, fixture));

        Assert.True(FeatureVersions.Manifest == PinnedManifest,
            $"the feature semantics moved from {PinnedManifest} to {FeatureVersions.Manifest}, and the golden vectors are "
            + "still pinned under the old one. Set PinnedManifest to the new number and re-pin in the same commit:\n"
            + Repin(runs, fixture));

        var pins = Pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var changed = runs.Where(r => !pins.TryGetValue(r.Vector.Name, out var pinned) || pinned != r.Pin)
            .Select(r => r.Vector.Name)
            .ToList();

        Assert.True(changed.Count == 0,
            $"{changed.Count} golden feature vector(s) produced an id or an output other than what was pinned under "
            + $"{PinnedManifest} — {string.Join(", ", changed)} — while it did not move. What a spec is written as, or what "
            + "the app computes from it, has changed: bump `FeatureVersions.SemanticsVersion` (and `FeatureCanonical.Header` "
            + "if the text's layout moved) and re-pin in the same commit. The bump is what re-identifies every feature, so "
            + "no result bound to an old id is read as evidence about the new meaning. The pins as this build computes "
            + "them:\n" + Repin(runs, fixture));
    }

    /// <summary>
    /// THE VECTORS COVER WHAT THEY CLAIM TO, read off the specs and the outputs rather than off the names: every kind and
    /// both changes; each vector both present and absent somewhere; every absence the evaluator states — stale, too few
    /// readings, a withheld payload, an unreadable field, a ratio's base of 0, a window whose newest is stale — and values
    /// of more than one class.
    /// </summary>
    [Fact]
    public void The_golden_vectors_cover_every_kind_change_absence_and_class()
    {
        var runs = RunAll();
        Assert.Equal(Vectors.Length, Vectors.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var kind in FeatureKinds.All)
            Assert.True(runs.Any(r => r.Spec.Kind == kind), $"no vector is a {kind}");
        foreach (var mode in new[] { FeatureKinds.Diff, FeatureKinds.Ratio })
            Assert.True(runs.Any(r => r.Spec.Mode == mode), $"no vector is a {mode} change");

        foreach (var run in runs)
        {
            Assert.True(run.Values.Any(v => v.Present), $"{run.Vector.Name} is never present");
            Assert.True(run.Values.Any(v => !v.Present), $"{run.Vector.Name} is never absent");
        }

        var absences = runs.SelectMany(r => r.Values).Where(v => !v.Present).Select(v => v.Absent!).ToList();
        foreach (var words in new[]
                 {
                     "older than max_age_s", "fewer than min_rows", "is withheld", "is not a plain decimal", "the ratio's base",
                     "more than max_age_s"
                 })
            Assert.True(absences.Any(a => a.Contains(words, StringComparison.Ordinal)), $"no vector is absent because '{words}'");

        var classes = runs.SelectMany(r => r.Values).Where(v => v.Present).Select(v => v.Class).Distinct().ToList();
        Assert.Contains(TapeClass.Live, classes);
        Assert.Contains(TapeClass.Arch, classes);
    }
}
