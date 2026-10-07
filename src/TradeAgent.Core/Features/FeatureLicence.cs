using TradeAgent.Core.Data;

namespace TradeAgent.Core.Features;

/// <summary>
/// WHETHER CAPITAL MAY EVER STAND ON A FEATURE: the terms its inputs were recorded under (<c>U-features</c> item 3;
/// R07 § 5.3; <c>docs/CONTRACTS.md</c> "Data licences").
///
/// <para><b>Research-only confers nothing, and today every feature is research-only.</b> A tape source's licence reading
/// is a <c>data_licence</c> row like a dataset's, written by a schema rung on the day its venue's terms were read — and no
/// tape source has one: the readings fall to the conferring-dataset unit, the one that opens a conferring path for tape
/// evidence, never to a unit that only computes. So this answers a sentence for every feature this build can parse, and
/// it says that research goes on.</para>
///
/// <para><b>It only ever refuses.</b> Like <see cref="DataLicence.LiveRefusal"/> it is pure and read at a gate, never
/// hashed into a feature's id: a feature is what it computes, and a reading appended later — narrower or wider — changes
/// what may stand on it without making it another feature.</para>
/// </summary>
public static class FeatureLicence
{
    /// <summary>
    /// WHY NO CAPITAL MAY STAND ON <paramref name="spec"/>, as one sentence — or null ONLY when every input's source has a
    /// newest reading, for that source, whose class confers (<see cref="DataLicence.Confers"/>). A source with no reading,
    /// a reading of another source, and every class but <c>first-party</c> and <c>commercial-ok</c> confer nothing.
    /// </summary>
    /// <param name="newest">The newest licence reading of a source, or null where none was recorded — <c>DataLicences.Newest</c>.</param>
    public static string? LiveRefusal(FeatureSpec spec, Func<string, DataLicenceReading?> newest)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(newest);

        var refusing = new List<string>();
        foreach (var source in spec.Inputs.Select(i => i.Source).Distinct(StringComparer.Ordinal))
        {
            var reading = newest(source);
            if (reading is not null && !string.Equals(reading.Source, source, StringComparison.Ordinal)) reading = null;
            if (reading is not null && DataLicence.Confers(reading.Class)) continue;

            refusing.Add(reading is null
                ? $"{source}, whose terms TradeAgent holds no licence reading of"
                : $"{source}, whose newest licence reading is {DataLicence.Words(reading.Licence)}");
        }

        if (refusing.Count == 0) return null;
        return $"feature {spec.Id[..12]} reads {string.Join("; and ", refusing)}; only {DataLicence.FirstParty} or "
               + $"{DataLicence.CommercialOk} data confers live eligibility, so no capital may stand on it — it is research "
               + "context, and backtests and paper go on.";
    }
}
