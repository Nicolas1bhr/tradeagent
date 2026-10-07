using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FEATURE EVALUATOR, PURE (<c>U-features</c> item 2): rows and an instant in, a decimal or a stated absence out.
/// Every row here is built by the test — the evaluator reads no tape — so each case states exactly when a reading was
/// stamped, when it arrived, which revision it is and what it counts as.
///
/// <para><b>The guard under test is the gate</b>, <see cref="FeatureEvaluator.Counts"/>: a row counts at <c>t</c> only
/// once FIRST SEEN by <c>t − latency</c>. Its mutant — the source time standing in for the first-seen time — turns (a),
/// (b) and (c) red.</para>
/// </summary>
public class FeatureEvaluatorTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Rows of one premium-index series, ids in the order they are added — the order a tape would write them.</summary>
    sealed class Rows
    {
        long _id;
        public readonly List<TapeObservation> All = [];

        public TapeObservation Add(DateTimeOffset stamped, DateTimeOffset arrived, string? value, string cls = TapeClass.Live,
            int revision = 1, bool withheld = false, string field = "lastFundingRate", string subject = "BTCUSDT",
            string? raw = null)
        {
            var ms = stamped.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var payload = TapeJson.Canonical(raw ?? $$"""{"{{field}}":"{{value}}","symbol":"{{subject}}","time":{{ms}}}""");
            var row = new TapeObservation(++_id, TapeSourceCatalog.Premium, "premium-index", subject, stamped, arrived,
                FetchId: _id, TapeStore.NaturalKey(subject, stamped), revision, TapeJson.Sha256(payload),
                withheld ? null : payload, cls, withheld ? new TapeQuarantine(TapeScreen.InstructionOverride, 2) : null);
            All.Add(row);
            return row;
        }
    }

    static FeatureSpec Spec(string kind = "latest", int latency = 5, int maxAge = 180, string field = "lastFundingRate",
        string mode = "diff", int lookback = 600, int window = 600, int minRows = 1)
    {
        var extra = kind switch
        {
            "latest" => "",
            "change" => $$""","mode":"{{mode}}","lookback_s":{{lookback}}""",
            _ => $$""","window_s":{{window}},"min_rows":{{minRows}}"""
        };
        var parse = FeatureSpec.Parse(
            $$"""{"kind":"{{kind}}","input":{"source":"binance-um-premium","series":"premium-index","subject":"BTCUSDT","field":"{{field}}"},"latency_s":{{latency}},"max_age_s":{{maxAge}}{{extra}}}""");
        Assert.True(parse.Ok, parse.Why);
        return parse.Spec!;
    }

    static decimal? Value(FeatureSpec spec, IEnumerable<TapeObservation> rows, DateTimeOffset t) =>
        FeatureEvaluator.At(spec, rows, t).Value;

    static string Show(FeatureValue v) =>
        $"{v.At:HH:mm:ss.fff} {(v.Value is { } d ? d.ToString(CultureInfo.InvariantCulture) : "absent: " + v.Absent)} [{v.Class ?? "-"}] rows={v.Rows}";

    /// <summary>
    /// (a) A FEATURE READS ONLY WHAT HAD ARRIVED BY ITS AS-OF TIME MINUS ITS LATENCY. Three readings a minute apart, each
    /// arriving a few seconds after its stamp; at each instant the latest and a window see exactly the readings that had
    /// arrived five seconds earlier — the reading stamped 12:01:00 and arrived 12:01:03 is not there at 12:01:07 (its gate
    /// is 12:01:02) and is at 12:01:08.
    /// </summary>
    [Fact]
    public void A_feature_reads_only_what_had_arrived_by_its_as_of_time_minus_its_latency()
    {
        var rows = new Rows();
        rows.Add(Noon.AddMinutes(-1), Noon.AddMinutes(-1).AddSeconds(2), "1");
        rows.Add(Noon, Noon.AddSeconds(2), "2");
        rows.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(3), "3");

        var latest = Spec(latency: 5, maxAge: 300);
        var mean = Spec("mean", latency: 5, maxAge: 300, window: 120);

        foreach (var t in new[] { Noon.AddSeconds(6), Noon.AddSeconds(7), Noon.AddMinutes(1).AddSeconds(7), Noon.AddMinutes(1).AddSeconds(8) })
            log.WriteLine($"latest {Show(FeatureEvaluator.At(latest, rows.All, t))} | mean {Show(FeatureEvaluator.At(mean, rows.All, t))}");

        Assert.Equal(1m, Value(latest, rows.All, Noon.AddSeconds(6)));
        Assert.Equal(2m, Value(latest, rows.All, Noon.AddSeconds(7)));
        Assert.Equal(2m, Value(latest, rows.All, Noon.AddMinutes(1).AddSeconds(7)));
        Assert.Equal(3m, Value(latest, rows.All, Noon.AddMinutes(1).AddSeconds(8)));

        // THE WINDOW BY THE SAME GATE: (12:01:02 − 120 s, 12:01:07] holds only the noon reading at 12:01:07, both at 12:01:08.
        Assert.Equal(2m, Value(mean, rows.All, Noon.AddMinutes(1).AddSeconds(7)));
        Assert.Equal(2.5m, Value(mean, rows.All, Noon.AddMinutes(1).AddSeconds(8)));
    }

    /// <summary>
    /// (b) ROWS THAT ARRIVED AFTER <c>t</c> CHANGE NO VALUE, EVEN WHEN HANDED IN. For every kind, the value at <c>t</c>
    /// from the rows known by then equals the value from those rows plus: new readings stamped well before <c>t</c> that
    /// arrived after it, a revision of a known reading that arrived after it, a reading stamped after <c>t</c>, and a
    /// reading that arrived inside the latency. Same value, same absence, same class, same rows, same digest.
    /// </summary>
    [Fact]
    public void Rows_that_arrived_after_t_change_no_value_even_when_handed_in()
    {
        var t = Noon.AddMinutes(30);
        var known = new Rows();
        for (var i = 0; i < 25; i++)
            known.Add(Noon.AddMinutes(i), Noon.AddMinutes(i).AddSeconds(2), (100 + i * 3 % 7).ToString(CultureInfo.InvariantCulture));

        // THE SAME ROWS, AND FOUR KINDS OF ROW THAT HAD NOT ARRIVED BY t − latency.
        var handed = new Rows();
        for (var i = 0; i < 25; i++)
            handed.Add(Noon.AddMinutes(i), Noon.AddMinutes(i).AddSeconds(2), (100 + i * 3 % 7).ToString(CultureInfo.InvariantCulture));
        for (var i = 25; i < 30; i++)
            handed.Add(Noon.AddMinutes(i), t.AddMinutes(1 + i), "900", TapeClass.Arch);                  // stamped before t, arrived after
        handed.Add(Noon.AddMinutes(24), t.AddSeconds(1), "555", TapeClass.Arch, revision: 2);           // a revision, after t
        handed.Add(t.AddMinutes(1), t.AddSeconds(2), "777");                                             // stamped after t
        handed.Add(Noon.AddMinutes(29).AddSeconds(30), t.AddSeconds(-2), "888");                         // inside the latency

        var specs = new[]
        {
            Spec("latest", maxAge: 900), Spec("change", maxAge: 900, mode: "diff", lookback: 300),
            Spec("change", maxAge: 900, mode: "ratio", lookback: 300), Spec("mean", maxAge: 900, window: 900, minRows: 5),
            Spec("min", maxAge: 900, window: 900, minRows: 5), Spec("max", maxAge: 900, window: 900, minRows: 5),
            Spec("pct-rank", maxAge: 900, window: 900, minRows: 5)
        };

        foreach (var spec in specs)
        {
            var alone = FeatureEvaluator.At(spec, known.All, t);
            var withLate = FeatureEvaluator.At(spec, handed.All, t);
            log.WriteLine($"{spec.Kind,-8} {spec.Mode,-5} {Show(alone)} | {Show(withLate)}");

            Assert.True(alone.Present, $"{spec.Kind}: {alone.Absent}");
            Assert.Equal(alone, withLate);
        }
    }

    /// <summary>
    /// (c) THE SOURCE TIME ALONE NEVER ADMITS A ROW. A reading stamped half an hour ago that arrived a second ago did not
    /// exist here half an hour ago, nor five seconds ago: there is no value until it is five seconds old. A reading
    /// stamped a minute AHEAD of the instant — a vendor clock running fast — never counts before that minute, however
    /// early it arrived.
    /// </summary>
    [Fact]
    public void Source_time_alone_never_admits_a_row()
    {
        var spec = Spec(latency: 5, maxAge: 3600);
        var rows = new Rows();
        rows.Add(Noon.AddMinutes(-30), Noon.AddSeconds(-1), "1");

        var before = FeatureEvaluator.At(spec, rows.All, Noon);
        log.WriteLine(Show(before));
        Assert.False(before.Present);
        Assert.Equal(0, before.Rows);
        Assert.Null(before.Class);
        Assert.False(FeatureEvaluator.Counts(rows.All[0], Noon, spec.Latency));
        Assert.False(FeatureEvaluator.Counts(rows.All[0], Noon.AddSeconds(3), spec.Latency));
        Assert.Equal(1m, Value(spec, rows.All, Noon.AddSeconds(4)));

        var ahead = new Rows();
        ahead.Add(Noon.AddMinutes(1), Noon.AddMinutes(-2), "9");
        Assert.False(FeatureEvaluator.Counts(ahead.All[0], Noon, TimeSpan.Zero));
        Assert.Null(Value(Spec(latency: 0), ahead.All, Noon.AddSeconds(59)));
        Assert.Equal(9m, Value(Spec(latency: 0), ahead.All, Noon.AddMinutes(1)));
    }

    /// <summary>
    /// (d) AN O-PIT ROW COUNTS FROM ITS LABEL, AND A LATE REVISION FROM ITS ARRIVAL. An archive reading labelled noon and
    /// recorded three days later — its vendor's checksum verified and its storage dated before the label — is what
    /// TradeAgent can show was knowable at noon, so a replay of noon may read it; its rebuilt revision, recorded a day
    /// after that, was not knowable until it arrived. An archive row that is NOT O-PIT counts from its arrival.
    /// </summary>
    [Fact]
    public void An_O_PIT_row_counts_from_its_label_and_a_late_revision_from_its_arrival()
    {
        var spec = Spec(latency: 5, maxAge: 864_000);
        var rows = new Rows();
        var pit = rows.Add(Noon, Noon.AddDays(3), "10", TapeClass.Pit);
        var rebuilt = rows.Add(Noon, Noon.AddDays(4), "11", TapeClass.Pit, revision: 2);
        var arch = rows.Add(Noon.AddMinutes(15), Noon.AddDays(3), "20", TapeClass.Arch);

        Assert.Equal(Noon, FeatureEvaluator.FirstSeen(pit));
        Assert.Equal(Noon.AddDays(4), FeatureEvaluator.FirstSeen(rebuilt));
        Assert.Equal(Noon.AddDays(3), FeatureEvaluator.FirstSeen(arch));

        var atLabel = FeatureEvaluator.At(spec, rows.All, Noon.AddSeconds(5));
        log.WriteLine(Show(atLabel));
        Assert.Equal(10m, atLabel.Value);
        Assert.Equal(TapeClass.Pit, atLabel.Class);
        Assert.Null(Value(spec, rows.All, Noon.AddSeconds(4)));

        // THE ARCHIVE ROW STAMPED 12:15 IS NOT THERE AT 12:15 OR ON THE NEXT DAY: the noon reading still is.
        Assert.Equal(10m, Value(spec, rows.All, Noon.AddMinutes(15).AddSeconds(5)));
        Assert.Equal(10m, Value(spec, rows.All, Noon.AddDays(1)));

        // FROM ITS ARRIVAL, THREE DAYS LATER, IT IS; THE REBUILT NOON READING IS NOT, UNTIL ITS OWN ARRIVAL.
        Assert.Equal(20m, Value(spec, rows.All, Noon.AddDays(3).AddSeconds(5)));
        var noonOnly = rows.All.Where(r => r.SourceTime == Noon).ToList();
        Assert.Equal(10m, Value(spec, noonOnly, Noon.AddDays(4).AddSeconds(4)));
        Assert.Equal(11m, Value(spec, noonOnly, Noon.AddDays(4).AddSeconds(5)));
    }

    /// <summary>
    /// (h) A VALUE CARRIES THE WORST CLASS OF ITS ROWS — and the digest of exactly those rows. A window of live readings is
    /// O-LIVE; one archive reading in it makes it O-ARCH; a checked print makes it O-PIT. An absence carries the class of
    /// what it read, and a value that read nothing carries none.
    /// </summary>
    [Fact]
    public void A_value_carries_the_worst_class_of_its_rows()
    {
        var mean = Spec("mean", maxAge: 600, window: 600, minRows: 2);
        var t = Noon.AddMinutes(3);

        string? ClassOf(params string[] classes)
        {
            var rows = new Rows();
            for (var i = 0; i < classes.Length; i++) rows.Add(Noon.AddMinutes(i), Noon.AddMinutes(i).AddSeconds(1), "1", classes[i]);
            var v = FeatureEvaluator.At(mean, rows.All, t);
            log.WriteLine($"{string.Join(",", classes)} -> {Show(v)}");
            return v.Class;
        }

        Assert.Equal(TapeClass.Live, ClassOf(TapeClass.Live, TapeClass.Live, TapeClass.Live));
        Assert.Equal(TapeClass.Arch, ClassOf(TapeClass.Live, TapeClass.Arch, TapeClass.Live));
        Assert.Equal(TapeClass.Pit, ClassOf(TapeClass.Live, TapeClass.Pit, TapeClass.Live));
        Assert.Equal(TapeClass.Arch, ClassOf(TapeClass.Pit, TapeClass.Arch));
        Assert.Equal(TapeClass.Hind, ClassOf(TapeClass.Live, TapeClass.Hind));

        // AN ABSENCE CARRIES WHAT IT READ: one archive reading, under min_rows.
        var one = new Rows();
        one.Add(Noon, Noon.AddSeconds(1), "1", TapeClass.Arch);
        var thin = FeatureEvaluator.At(mean, one.All, t);
        Assert.False(thin.Present);
        Assert.Contains("fewer than min_rows 2", thin.Absent, StringComparison.Ordinal);
        Assert.Equal((TapeClass.Arch, 1), (thin.Class, thin.Rows));

        // A CHANGE READS TWO ROWS: the class is the worse of the two, and the digest names both, in id order.
        var change = Spec("change", maxAge: 120, lookback: 180);
        var pair = new Rows();
        var earlier = pair.Add(Noon, Noon.AddSeconds(1), "4", TapeClass.Arch);
        var later = pair.Add(Noon.AddMinutes(3), Noon.AddMinutes(3).AddSeconds(1), "10");
        var moved = FeatureEvaluator.At(change, pair.All, Noon.AddMinutes(3).AddSeconds(10));
        log.WriteLine(Show(moved));
        Assert.Equal(6m, moved.Value);
        Assert.Equal((TapeClass.Arch, 2), (moved.Class, moved.Rows));
        Assert.Equal(Sha256Hex.Of($"{earlier.Id}:1:{earlier.PayloadSha256}\n{later.Id}:1:{later.PayloadSha256}\n"), moved.RowsSha256);

        // NOTHING READ: no class, and the digest of no rows.
        var none = FeatureEvaluator.At(Spec(), [], Noon);
        Assert.Equal((null, 0, Sha256Hex.Of("")), (none.Class, none.Rows, none.RowsSha256));
    }

    /// <summary>
    /// (k) A WITHHELD PAYLOAD MAKES THE VALUE ABSENT, NEVER SKIPPED. A reading the tape's screen quarantined reaches the
    /// evaluator without its payload; a window holding it has no mean — not the mean of the readings around it — and a
    /// latest that IS it has no value, not the reading before it. The absence names the screen's rule and still carries
    /// the withheld row in its class and digest.
    /// </summary>
    [Fact]
    public void A_withheld_payload_makes_the_value_absent_never_skipped()
    {
        var rows = new Rows();
        rows.Add(Noon, Noon.AddSeconds(1), "1");
        var withheld = rows.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(1), "500", withheld: true);
        rows.Add(Noon.AddMinutes(2), Noon.AddMinutes(2).AddSeconds(1), "3");

        var mean = FeatureEvaluator.At(Spec("mean", maxAge: 600, window: 600, minRows: 2), rows.All, Noon.AddMinutes(3));
        log.WriteLine(Show(mean));
        Assert.Null(mean.Value);
        Assert.Contains($"the payload of row {withheld.Id} is withheld", mean.Absent, StringComparison.Ordinal);
        Assert.Contains(TapeScreen.InstructionOverride, mean.Absent, StringComparison.Ordinal);
        Assert.Equal(3, mean.Rows);

        var latest = FeatureEvaluator.At(Spec(maxAge: 600), rows.All.Take(2), Noon.AddMinutes(1).AddSeconds(10));
        log.WriteLine(Show(latest));
        Assert.Null(latest.Value);
        Assert.Contains("withheld", latest.Absent, StringComparison.Ordinal);

        // AND WITHOUT THE WITHHELD READING THE SAME WINDOW HAS A MEAN — so it was the withholding, not the window.
        Assert.Equal(2m, Value(Spec("mean", maxAge: 600, window: 600, minRows: 2), rows.All.Where(r => r.Id != withheld.Id), Noon.AddMinutes(3)));
    }

    /// <summary>
    /// (l) A STALE LATEST IS ABSENT, NOT CARRIED FORWARD. When the readings stop, the latest stands for max_age_s after
    /// its stamp — the bound inclusive — and then there is no value, not the last one repeated. A window whose newest
    /// reading is older than max_age_s is absent too, and so is a change whose latest is.
    /// </summary>
    [Fact]
    public void A_stale_latest_is_absent_not_carried_forward()
    {
        var rows = new Rows();
        rows.Add(Noon, Noon.AddSeconds(2), "1");
        rows.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(2), "2");

        var latest = Spec(latency: 5, maxAge: 180);
        Assert.Equal(2m, Value(latest, rows.All, Noon.AddMinutes(4)));
        var stale = FeatureEvaluator.At(latest, rows.All, Noon.AddMinutes(4).AddMilliseconds(1));
        log.WriteLine(Show(stale));
        Assert.Null(stale.Value);
        Assert.Contains("older than max_age_s (180 s)", stale.Absent, StringComparison.Ordinal);
        Assert.Null(Value(latest, rows.All, Noon.AddHours(5)));

        var window = FeatureEvaluator.At(Spec("mean", maxAge: 180, window: 3600, minRows: 1), rows.All, Noon.AddMinutes(10));
        log.WriteLine(Show(window));
        Assert.Null(window.Value);
        Assert.Contains("more than max_age_s (180 s)", window.Absent, StringComparison.Ordinal);
        Assert.Equal(2, window.Rows);

        var change = FeatureEvaluator.At(Spec("change", maxAge: 180, lookback: 60), rows.All, Noon.AddMinutes(10));
        Assert.Null(change.Value);
        Assert.Contains("older than max_age_s", change.Absent, StringComparison.Ordinal);
    }

    /// <summary>
    /// EVERY KIND, EXACT IN DECIMAL, FROM THE VENDOR'S TEXT. Funding rates as Binance serves them — eight decimals, one
    /// negative — read exactly; a mean, a minimum, a maximum, a rank, a difference and a ratio computed in decimal; the
    /// latest read at its highest revision that had arrived.
    /// </summary>
    [Fact]
    public void Every_kind_computes_exactly_in_decimal_from_the_vendors_text()
    {
        var rows = new Rows();
        rows.Add(Noon, Noon.AddSeconds(2), "0.00010000");
        rows.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(2), "-0.00001858");
        rows.Add(Noon.AddMinutes(2), Noon.AddMinutes(2).AddSeconds(2), "0.00020000");
        rows.Add(Noon.AddMinutes(3), Noon.AddMinutes(3).AddSeconds(2), "0.00005000");
        rows.Add(Noon.AddMinutes(3), Noon.AddMinutes(4).AddSeconds(2), "0.00030000", TapeClass.Arch, revision: 2);

        var t = Noon.AddMinutes(5);
        decimal? At(string kind, string mode = "diff") =>
            Value(Spec(kind, maxAge: 600, mode: mode, lookback: 180, window: 600, minRows: 4), rows.All, t);

        Assert.Equal(0.00030000m, At("latest"));
        Assert.Equal(0.000145355m, At("mean"));            // (0.0001 − 0.00001858 + 0.0002 + 0.0003) / 4
        Assert.Equal(-0.00001858m, At("min"));
        Assert.Equal(0.00030000m, At("max"));
        Assert.Equal(1m, At("pct-rank"));                    // the newest, 0.0003, is the greatest: 4 of 4
        Assert.Equal(0.00031858m, At("change", "diff"));     // 0.0003 − (−0.00001858), the latest at 12:02:00 being 12:01's
        Assert.Equal(0.0003m / -0.00001858m, At("change", "ratio"));

        // BEFORE THE REVISION ARRIVED (12:04:02 + 5 s), THE FIRST READING OF 12:03 STOOD.
        Assert.Equal(0.00005000m, Value(Spec(maxAge: 600), rows.All, Noon.AddMinutes(4).AddSeconds(6)));
        Assert.Equal(0.00030000m, Value(Spec(maxAge: 600), rows.All, Noon.AddMinutes(4).AddSeconds(7)));
        Assert.Equal(0.5m, Value(Spec("pct-rank", maxAge: 600, window: 600, minRows: 4), rows.All, Noon.AddMinutes(4).AddSeconds(6)));
    }

    /// <summary>
    /// A FIELD IS A PLAIN DECIMAL OR THE VALUE IS ABSENT. A JSON number reads as its own text; a string with an exponent,
    /// a plus sign, white space, a comma, more than 28 decimals, or no digits does not, nor does a boolean, a null, an
    /// object or a missing field — and a ratio over a base of 0 has no value.
    /// </summary>
    [Fact]
    public void A_field_that_is_not_a_plain_decimal_makes_the_value_absent()
    {
        var latest = Spec(maxAge: 600);
        decimal? Read(string payload)
        {
            var rows = new Rows();
            rows.Add(Noon, Noon.AddSeconds(1), null, raw: payload);
            var v = FeatureEvaluator.At(latest, rows.All, Noon.AddMinutes(1));
            if (!v.Present) log.WriteLine($"{payload,-60} -> {v.Absent}");
            return v.Value;
        }

        Assert.Equal(0.00001937m, Read("""{"lastFundingRate":0.00001937}"""));
        Assert.Equal(-5m, Read("""{"lastFundingRate":"-5"}"""));
        Assert.Equal(0m, Read("""{"lastFundingRate":"-0.000"}"""));
        Assert.Equal(1234567890123456789012345678m, Read("""{"lastFundingRate":"1234567890123456789012345678"}"""));

        foreach (var bad in new[]
                 {
                     """{"lastFundingRate":"1e-5"}""", """{"lastFundingRate":1E-5}""", """{"lastFundingRate":"+1"}""",
                     """{"lastFundingRate":" 1"}""", """{"lastFundingRate":"1,5"}""", """{"lastFundingRate":"1."}""",
                     """{"lastFundingRate":".5"}""", """{"lastFundingRate":""}""", """{"lastFundingRate":"n/a"}""",
                     """{"lastFundingRate":"0.00000000000000000000000000001"}""",
                     """{"lastFundingRate":"12345678901234567890123456789"}""",
                     """{"lastFundingRate":true}""", """{"lastFundingRate":null}""", """{"lastFundingRate":{"v":"1"}}""",
                     """{"markPrice":"1"}""", """["1"]"""
                 })
            Assert.Null(Read(bad));

        var zero = new Rows();
        zero.Add(Noon, Noon.AddSeconds(1), "0.00000000");
        zero.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(1), "0.00001");
        var ratio = FeatureEvaluator.At(Spec("change", maxAge: 120, mode: "ratio", lookback: 60), zero.All, Noon.AddMinutes(1).AddSeconds(10));
        log.WriteLine(Show(ratio));
        Assert.Null(ratio.Value);
        Assert.Contains("the ratio's base", ratio.Absent, StringComparison.Ordinal);
        Assert.Contains("is 0", ratio.Absent, StringComparison.Ordinal);
        Assert.Equal(0.00001m, Value(Spec("change", maxAge: 120, mode: "diff", lookback: 60), zero.All, Noon.AddMinutes(1).AddSeconds(10)));
    }

    /// <summary>
    /// ROWS OF ANOTHER SERIES OR SUBJECT NEVER COUNT, whoever hands them in: the ETHUSDT reading beside BTCUSDT's, and a
    /// row of another source with the same subject and stamp, leave BTCUSDT's value as it was.
    /// </summary>
    [Fact]
    public void Rows_of_another_subject_or_series_never_count()
    {
        var rows = new Rows();
        rows.Add(Noon, Noon.AddSeconds(1), "1");
        rows.Add(Noon.AddMinutes(1), Noon.AddMinutes(1).AddSeconds(1), "999", subject: "ETHUSDT");
        var other = rows.All[0] with { Id = 77, Source = TapeSourceCatalog.OpenInterest, Series = "open-interest", SourceTime = Noon.AddMinutes(1) };

        var value = FeatureEvaluator.At(Spec(maxAge: 600), [.. rows.All, other], Noon.AddMinutes(2));
        Assert.Equal((1m, 1), (value.Value, value.Rows));
    }
}
