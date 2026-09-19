using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FORWARD LEDGER'S FOUR PROMISES, one test each: a bar is stored only when it has CLOSED, every
/// attempt is a row and every bar names the attempt it came in, a hole is written down and never
/// filled, and the FIRST READING STANDS.
///
/// <para>No socket here at all — these are about the store's arithmetic and its keys, and the bars
/// are handed in as the parser would produce them. The collector's own tests talk to a loopback
/// listener; nothing in this suite ever talks to the vendor.</para>
/// </summary>
public class ForwardBarStoreTests(ITestOutputHelper log)
{
    const string Symbol = "BTCUSDT";

    /// <summary>Noon UTC on a fixed day. Every instant below is an offset from this one.</summary>
    static readonly DateTimeOffset Noon = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One minute of the fixed series. <paramref name="minute"/> is minutes past noon.</summary>
    static ForwardBars.Kline Bar(int minute, decimal close = 100m) =>
        new(Noon.AddMinutes(minute), 100m, 101m, 99m, close, 1.5m,
            Noon.AddMinutes(minute + 1).AddMilliseconds(-1));

    static ForwardFetchAttempt Fetch(DateTimeOffset receivedAt, string? note = null, int? status = 200) => new()
    {
        Source = ForwardBars.Source,
        Symbol = Symbol,
        Url = $"http://127.0.0.1:0/api/v3/klines?symbol={Symbol}&interval=1m",
        RequestedAt = receivedAt.AddSeconds(-1),
        ReceivedAt = receivedAt,
        HttpStatus = status,
        BodySha256 = status == 200 ? new string('b', 64) : null,
        Note = note
    };

    /// <summary>
    /// THE OPEN MINUTE IS NOT A BAR. The vendor's last row is the candle still forming — its high,
    /// low, close and volume are all still moving — and a store that kept it would hold a row that
    /// changes after it was written.
    /// </summary>
    [Fact]
    public void Only_closed_bars_are_stored_and_the_open_one_is_not()
    {
        using var db = TestEnv.NewDb();
        var store = new ForwardBarStore(db);

        // Read thirty seconds into minute 2: minutes 0 and 1 have closed, minute 2 has not.
        var at = Noon.AddMinutes(2).AddSeconds(30);
        var result = store.Append(Fetch(at), [Bar(0), Bar(1), Bar(2)]);

        log.WriteLine($"stored={result.Stored} notClosedYet={result.NotClosedYet}");

        Assert.Equal(2, result.Stored);
        Assert.Equal(1, result.NotClosedYet);

        var held = store.Since(Symbol);
        Assert.Equal(2, held.Count);
        Assert.Equal(Noon, held[0].OpenTime);
        Assert.Equal(Noon.AddMinutes(1), held[1].OpenTime);
        Assert.DoesNotContain(held, b => b.OpenTime == Noon.AddMinutes(2));

        // AND EVERY STORED ROW CAN BE CHECKED BY A READER: its close time precedes the instant it
        // was received, which is the closure claim itself rather than a promise that it was made.
        Assert.All(held, b => Assert.True(b.CloseTime < b.ReceivedAt,
            $"{b.OpenTime:O} closes at {b.CloseTime:O}, which is not before it was received at {b.ReceivedAt:O}"));
    }

    /// <summary>
    /// THE ATTEMPT IS THE PROVENANCE, so it is written whether or not anything arrived — and a bar
    /// that cannot name the attempt it came in is a bar with no provenance at all.
    /// </summary>
    [Fact]
    public void Every_fetch_is_recorded_succeeded_or_failed_and_a_bar_names_its_fetch()
    {
        using var db = TestEnv.NewDb();
        var store = new ForwardBarStore(db);

        var good = store.Append(Fetch(Noon.AddMinutes(2)), [Bar(0), Bar(1)]);
        var bad = store.Append(Fetch(Noon.AddMinutes(3), note: "the host answered 503", status: 503));

        var attempts = store.Attempts(Symbol);
        log.WriteLine(string.Join("\n", attempts.Select(a => $"{a.Id} {a.Status} bars={a.Bars} note={a.Note}")));

        // BOTH attempts are rows. The failure is not silence.
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, a => a.Id == good.FetchId && a.Status == 200 && a.Bars == 2 && a.Note is null);
        Assert.Contains(attempts, a => a.Id == bad.FetchId && a.Status == 503 && a.Bars == 0
                                       && a.Note!.Contains("503", StringComparison.Ordinal));
        Assert.All(attempts, a => Assert.Contains("/api/v3/klines", a.Url, StringComparison.Ordinal));

        // AND EVERY STORED BAR NAMES THE ATTEMPT IT ARRIVED IN.
        Assert.All(store.Since(Symbol), b => Assert.Equal(good.FetchId, b.FetchId));

        // The failed attempt is the LAST one, so it is what the series reports as its error.
        var series = store.Series(Symbol);
        Assert.Equal("the host answered 503", series.LastError);
        Assert.Equal(Noon.AddMinutes(3), series.LastErrorAt);
        Assert.Equal(2, series.Bars);
    }

    /// <summary>
    /// A MINUTE THAT IS MISSING IS MISSING. It is counted where it was seen and nothing invents a
    /// price for it: the bar count over the window is the bars there really are.
    /// </summary>
    [Fact]
    public void A_gap_is_recorded_and_never_filled()
    {
        using var db = TestEnv.NewDb();
        var store = new ForwardBarStore(db);

        store.Append(Fetch(Noon.AddMinutes(2)), [Bar(0), Bar(1)]);
        // Minutes 2, 3 and 4 never arrive: the next answer starts at 5.
        var second = store.Append(Fetch(Noon.AddMinutes(7)), [Bar(5), Bar(6)]);

        var gaps = store.Gaps(Symbol);
        log.WriteLine(string.Join("\n", gaps.Select(g => $"{g.FromOpen:O}..{g.ToOpen:O} missing={g.BarsMissing}")));

        var gap = Assert.Single(gaps);
        Assert.Equal(Noon.AddMinutes(2), gap.FromOpen);
        Assert.Equal(Noon.AddMinutes(4), gap.ToOpen);
        Assert.Equal(3, gap.BarsMissing);
        Assert.Equal(Noon.AddMinutes(7), gap.SeenAt);
        Assert.Single(second.Gaps);

        // NOTHING WAS FILLED IN: four bars are held, not seven, and the three minutes in the hole
        // have no row of any kind.
        var held = store.Since(Symbol);
        Assert.Equal(4, held.Count);
        Assert.DoesNotContain(held, b => b.OpenTime > Noon.AddMinutes(1) && b.OpenTime < Noon.AddMinutes(5));

        var series = store.Series(Symbol);
        Assert.Equal(4, series.Bars);
        Assert.Equal(1, series.Gaps);
        Assert.Equal(3, series.BarsMissing);

        // AND THE SAME HOLE SEEN AGAIN IS ONE HOLE. A re-fetch of the window writes no second row,
        // so the absence cannot be counted twice in any figure taken over this table.
        store.Append(Fetch(Noon.AddMinutes(9)), [Bar(1), Bar(5), Bar(6)]);
        Assert.Single(store.Gaps(Symbol));
        Assert.Equal(3, store.Series(Symbol).BarsMissing);
    }

    /// <summary>
    /// THE FIRST READING STANDS. A vendor that serves a different candle for a minute already held
    /// does not get to rewrite it: the disagreement is counted on the later attempt's own note, and
    /// the row a research run may already have been given is untouched.
    /// </summary>
    [Fact]
    public void A_differing_refetch_does_not_overwrite_the_first_reading()
    {
        using var db = TestEnv.NewDb();
        var store = new ForwardBarStore(db);

        store.Append(Fetch(Noon.AddMinutes(2)), [Bar(0, close: 100m), Bar(1, close: 100m)]);

        // The same two minutes again, one of them with a different close.
        var again = store.Append(Fetch(Noon.AddMinutes(4)), [Bar(0, close: 100m), Bar(1, close: 777m)]);

        log.WriteLine($"stored={again.Stored} alreadyHeld={again.AlreadyHeld} disagreed={again.Disagreed}");

        Assert.Equal(0, again.Stored);
        Assert.Equal(2, again.AlreadyHeld);
        Assert.Equal(1, again.Disagreed);

        // THE STORED BAR IS THE FIRST READING, unchanged, and it still names the FIRST attempt.
        var kept = store.Bar(ForwardBars.Source, Symbol, Noon.AddMinutes(1))!;
        Assert.Equal(100m, kept.Close);
        Assert.NotEqual(again.FetchId, kept.FetchId);
        Assert.Equal(2, store.Since(Symbol).Count);

        // AND THE CONTRADICTION IS WRITTEN DOWN, on the attempt that made it.
        var note = store.Attempts(Symbol).Single(a => a.Id == again.FetchId).Note;
        log.WriteLine(note ?? "<no note>");
        Assert.Contains("DIFFER", note!, StringComparison.Ordinal);
        Assert.Contains("first reading stands", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Since</c> IS EXCLUSIVE AND BOUNDED, and <c>Freshness</c> answers off the rows rather than
    /// off "when the collector last ran" — a collector running happily against a vendor publishing
    /// nothing is exactly the case a liveness check must not report as fresh.
    /// </summary>
    [Fact]
    public void Since_is_exclusive_and_bounded_and_freshness_is_the_last_closed_bars_age()
    {
        using var db = TestEnv.NewDb();
        var store = new ForwardBarStore(db);

        Assert.Null(store.Freshness(Symbol, Noon));
        Assert.Empty(store.Since(Symbol));

        store.Append(Fetch(Noon.AddMinutes(5)), [Bar(0), Bar(1), Bar(2), Bar(3)]);

        var after = store.Since(Symbol, Noon.AddMinutes(1));
        Assert.Equal(2, after.Count);
        Assert.Equal(Noon.AddMinutes(2), after[0].OpenTime);

        Assert.Single(store.Since(Symbol, Noon.AddMinutes(1), limit: 1));

        // The newest bar opens at minute 3 and therefore closed at minute 4; at minute 10 it is six
        // minutes old.
        Assert.Equal(TimeSpan.FromMinutes(6), store.Freshness(Symbol, Noon.AddMinutes(10)));
    }
}
