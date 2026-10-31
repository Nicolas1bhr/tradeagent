using System.Globalization;

namespace TradeAgent.Core.Proving;

/// <summary>One program of the floor's grammar: its place in the fixed order, its words, and its text.</summary>
public sealed record FloorProgram(int Ordinal, string Kind, string Side, int Percentile, decimal Threshold, int Hold, string Text);

/// <summary>
/// THE FLOOR'S CLOSED GRAMMAR (<c>U-proving-ground-c</c> item 3; <c>docs/ORCHESTRATION.md</c> § 2.14: "a deterministic search with no
/// model as the floor"). Version-2a programs over one instrument's bare proving signal, hourly bars: the feature is the latest
/// reading or its change over one hour; the entry is the feature above (or below) a fixed percentile of the readings the floor may
/// see — those before the holdout cutoff — and the exit its opposite; each with one of two holds. Every program is enumerated in
/// one fixed order — kind, side, percentile, hold — and nothing about the order reads a market's truth or a result.
///
/// <para><b>Honest scope:</b> the grammar expresses a planted edge on the bare signal over an hourly horizon, in either sign; it
/// cannot express one on another series, a regime, or a lag beyond an hour. A score names what it could express.</para>
/// </summary>
public static class FloorGrammar
{
    /// <summary>The two feature kinds, in the order searched.</summary>
    public static readonly IReadOnlyList<string> Kinds = [Features.FeatureKinds.Latest, Features.FeatureKinds.Change];

    /// <summary>The percentiles an entry above a threshold is tried at, in the order searched; an entry below mirrors them.</summary>
    public static readonly IReadOnlyList<int> Percentiles = [80, 50, 20];

    /// <summary>The two holds, in bars, in the order searched.</summary>
    public static readonly IReadOnlyList<int> Holds = [1, 4];

    /// <summary>The series the grammar reads: the bare signal only.</summary>
    public const string Series = ProvingSources.Signal;

    /// <summary>
    /// EVERY PROGRAM OF THE GRAMMAR FOR <paramref name="instrument"/>, in the fixed order, thresholds drawn from
    /// <paramref name="readings"/> — the signal's values before the cutoff, in source-time order. Fewer than two readings is no
    /// grammar: a percentile of nothing is a guess.
    /// </summary>
    public static IReadOnlyList<FloorProgram> Programs(string instrument, IReadOnlyList<decimal> readings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count < 2) throw new ArgumentException($"the floor needs at least two readings of {instrument}'s signal before the cutoff, and has {readings.Count}.", nameof(readings));

        var changes = readings.Skip(1).Select((v, i) => v - readings[i]).ToList();
        var programs = new List<FloorProgram>();
        foreach (var kind in Kinds)
        {
            var values = kind == Features.FeatureKinds.Latest ? readings : changes;
            foreach (var side in new[] { "above", "below" })
                foreach (var p in Percentiles)
                {
                    var percentile = side == "above" ? p : 100 - p;
                    var threshold = Percentile(values, percentile);
                    foreach (var hold in Holds)
                        programs.Add(new FloorProgram(programs.Count + 1, kind, side, percentile, threshold, hold,
                            Text(instrument, kind, side, threshold, hold)));
                }
        }
        return programs;
    }

    /// <summary>The <paramref name="percentile"/>-th value of <paramref name="values"/>: the nearest rank below, never interpolated.</summary>
    public static decimal Percentile(IReadOnlyList<decimal> values, int percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Floor(percentile / 100m * (sorted.Length - 1))];
    }

    static string Text(string instrument, string kind, string side, decimal threshold, int hold)
    {
        var t = threshold.ToString("0.0000", CultureInfo.InvariantCulture);
        var feature = kind == Features.FeatureKinds.Latest
            ? $$"""{"kind":"latest","input":{"source":"{{ProvingSources.Synthetic}}","series":"{{Series}}","subject":"{{instrument}}","field":"{{ProvingSources.ValueField}}"},"latency_s":5,"max_age_s":7200}"""
            : $$"""{"kind":"change","mode":"diff","input":{"source":"{{ProvingSources.Synthetic}}","series":"{{Series}}","subject":"{{instrument}}","field":"{{ProvingSources.ValueField}}"},"latency_s":5,"max_age_s":7200,"lookback_s":3600}""";
        var (entry, exit) = side == "above" ? ($"x > {t}", $"x <= {t}") : ($"x < {t}", $"x >= {t}");
        return $"""
            instrument {instrument}
            bars 1h
            timeframe 1h
            data_freshness 2h
            max_decision_age 10m
            feature x = {feature}
            size fixed 1
            stop percent 50
            max_hold_bars {hold}
            exit when {exit}
            entry when {entry}

            """;
    }
}
