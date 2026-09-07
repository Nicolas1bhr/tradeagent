using System.IO.Compression;
using System.Text;
using TradeAgent.Core.Data;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — the normaliser. Twelve zips become one timeline, and every reading it could get wrong
/// is a reading somebody would trade on.
///
/// The timestamp UNIT is the sharp one. Binance changed these files from milliseconds to
/// microseconds in January 2025 and did not change the column, so a twelve-month collection that
/// straddles that boundary contains both — and a build that decides the unit from the month's DATE
/// is a build whose answer depends on a fact about the vendor's release history rather than on the
/// bytes in front of it. The magnitude is in the number itself; the date is a guess about it.
/// </summary>
public class KlineNormaliserTests
{
    static string Scratch()
    {
        var d = Path.Combine(TestEnv.Home, "norm-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// <paramref name="count"/> consecutive 1-minute rows in the archive's own twelve-column shape,
    /// with the timestamps written in <paramref name="unit"/> and the minutes in
    /// <paramref name="skip"/> left out.
    /// </summary>
    static string Rows(DateTimeOffset start, int count, KlineTimeUnit unit, params int[] skip)
    {
        var b = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (skip.Contains(i)) continue;

            var open = start.AddMinutes(i);
            var openT = Stamp(open, unit);
            var closeT = Stamp(open.AddMinutes(1), unit) - 1;
            var price = 100 + i;
            b.Append(openT).Append(',')
             .Append($"{price}.00000000,{price + 1}.00000000,{price - 1}.00000000,{price}.50000000,")
             .Append("1.00000000,").Append(closeT)
             .Append(",1000.00000000,10,0.50000000,500.00000000,0\n");
        }
        return b.ToString();
    }

    static long Stamp(DateTimeOffset at, KlineTimeUnit unit) =>
        unit == KlineTimeUnit.Milliseconds ? at.ToUnixTimeMilliseconds() : at.ToUnixTimeMilliseconds() * 1000L;

    /// <summary>Writes a month zip exactly as the archive ships one: one CSV, no header.</summary>
    static RawArchiveFile Zip(string dir, string month, string csv)
    {
        var path = Path.Combine(dir, $"BTCUSDT-1m-{month}.zip");
        using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry($"BTCUSDT-1m-{month}.csv").Open();
            var bytes = Encoding.UTF8.GetBytes(csv);
            stream.Write(bytes, 0, bytes.Length);
        }
        return new RawArchiveFile(month, path);
    }

    static readonly DateTimeOffset Dec2024 = new(2024, 12, 31, 23, 50, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Jan2025 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Later = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_months_written_in_different_units_become_one_continuous_timeline()
    {
        var dir = Scratch();
        var raws = new[]
        {
            Zip(dir, "2024-12", Rows(Dec2024, 10, KlineTimeUnit.Milliseconds)),
            Zip(dir, "2025-01", Rows(Jan2025, 10, KlineTimeUnit.Microseconds))
        };

        var set = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "out.csv"));

        Assert.Equal(20, set.Bars);
        Assert.Equal(Dec2024, set.FirstBar);
        Assert.Equal(Jan2025.AddMinutes(9), set.LastBar);
        Assert.Equal(0, set.Gaps);
        Assert.Equal(KlineTimeUnit.Milliseconds, set.Months.Single(m => m.Month == "2024-12").Unit);
        Assert.Equal(KlineTimeUnit.Microseconds, set.Months.Single(m => m.Month == "2025-01").Unit);
    }

    [Fact]
    public void The_unit_is_read_off_the_number_and_not_off_the_month_it_came_from()
    {
        // A month AFTER the vendor's change, still written in milliseconds. Nothing but the
        // magnitude can tell the truth about this file.
        var dir = Scratch();
        var raws = new[] { Zip(dir, "2026-08", Rows(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 5, KlineTimeUnit.Milliseconds)) };

        var set = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "out.csv"));

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), set.FirstBar);
        Assert.Equal(KlineTimeUnit.Milliseconds, set.Months.Single().Unit);
    }

    [Fact]
    public void Three_missing_minutes_are_reported_as_three_and_nothing_is_filled_in()
    {
        var dir = Scratch();
        var raws = new[] { Zip(dir, "2026-08", Rows(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 10, KlineTimeUnit.Microseconds, 4, 5, 8)) };

        var set = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "out.csv"));

        Assert.Equal(3, set.Gaps);
        Assert.Equal(7, set.Bars);
        Assert.Equal(2, set.GapRuns.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 4, 0, TimeSpan.Zero), set.GapRuns[0].From);
        Assert.Equal(2, set.GapRuns[0].Minutes);
        Assert.Equal(1, set.GapRuns[1].Minutes);
        Assert.Equal(7, File.ReadAllLines(set.Path).Length - 1);
    }

    [Fact]
    public void A_duplicate_open_time_is_dropped_and_counted()
    {
        var dir = Scratch();
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = Rows(start, 5, KlineTimeUnit.Microseconds);
        var raws = new[] { Zip(dir, "2026-08", rows + rows) };

        var set = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "out.csv"));

        Assert.Equal(5, set.Bars);
        Assert.Equal(5, set.Duplicates);
        Assert.Equal(0, set.Gaps);
    }

    [Fact]
    public void A_bar_that_had_not_closed_when_it_was_downloaded_is_excluded_and_counted()
    {
        var dir = Scratch();
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var raws = new[] { Zip(dir, "2026-08", Rows(start, 10, KlineTimeUnit.Microseconds)) };

        // Downloaded while the eighth bar was still forming: bars 8 and 9 have not closed.
        var set = KlineNormaliser.Normalise(raws, start.AddMinutes(8).AddSeconds(30), Path.Combine(dir, "out.csv"));

        Assert.Equal(8, set.Bars);
        Assert.Equal(2, set.Incomplete);
        Assert.Equal(start.AddMinutes(7), set.LastBar);
    }

    [Fact]
    public void The_same_raw_files_normalise_to_the_same_hash()
    {
        var dir = Scratch();
        var raws = new[]
        {
            Zip(dir, "2024-12", Rows(Dec2024, 10, KlineTimeUnit.Milliseconds)),
            Zip(dir, "2025-01", Rows(Jan2025, 10, KlineTimeUnit.Microseconds))
        };

        var first = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "v1.csv"));
        var second = KlineNormaliser.Normalise(raws, Later, Path.Combine(dir, "v2.csv"));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(File.ReadAllText(first.Path), File.ReadAllText(second.Path));
    }

    [Fact]
    public void A_magnitude_that_is_neither_milliseconds_nor_microseconds_is_refused()
    {
        Assert.Equal(KlineTimeUnit.Milliseconds, KlineNormaliser.UnitOf(1_785_542_400_000L));
        Assert.Equal(KlineTimeUnit.Microseconds, KlineNormaliser.UnitOf(1_785_542_400_000_000L));
        Assert.Null(KlineNormaliser.TryUnitOf(1_785_542_400L));            // seconds
        Assert.Null(KlineNormaliser.TryUnitOf(1_785_542_400_000_000_000L)); // nanoseconds
    }
}
