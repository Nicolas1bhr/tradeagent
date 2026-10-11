using System.Globalization;
using System.Reflection;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT A ROLE OWNS WAKES IT, AND NOTHING AN AGENT WRITES DOES (<c>U-reconcile-packet</c>, item 1): Research is woken
/// once for a new tape item it owns — never inside a held-back month, never with the item's text — and a director is
/// reminded once before a boundary it has not assessed reaches its deadline.
/// </summary>
public partial class CouncilLoopTests
{
    static readonly DateTimeOffset TapeNow = new(2026, 10, 4, 12, 0, 2, TimeSpan.Zero);

    static TapeSourceEntry Okx() =>
        TapeSourceCatalog.Announcements().Single(r => r.Id == TapeSourceCatalog.OkxEeaAnnouncements);

    static string AnnouncementUrl(string slug) => $"https://example.invalid/en-eu/help/{slug}";

    /// <summary>One announcement as OKX serves it (<c>TapeAnnouncementTests</c>' shape).</summary>
    static string Announcement(string slug, DateTimeOffset published, string title)
    {
        var ms = published.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var early = published.AddMinutes(-5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return $$"""{"annType":"announcements-new-listings","title":"{{title}}","url":"{{AnnouncementUrl(slug)}}","pTime":"{{ms}}","businessPTime":"{{early}}"}""";
    }

    static string AnnouncementPage(params string[] items) =>
        $$"""{"code":"0","data":[{"details":[{{string.Join(",", items)}}],"totalPage":"92"}],"msg":""}""";

    /// <summary>A collector pointed at the listener, telling <see cref="TapeWakes"/> what it stored — as the app wires it.</summary>
    static TapeCollector ToldCollector(TapeStore store, FakeArchive host, TapeSourceEntry row, Database db) =>
        new(store, baseUrl: host.BaseUrl, requestTimeout: TimeSpan.FromSeconds(5), now: () => TapeNow,
            catalog: new TapeSourceCatalogRead([row], null, []),
            stored: arrival => TapeWakes.Raise(db, arrival, TapeNow));

    static DatasetRecord HeldDataset(DatasetStore store, DateTimeOffset first, DateTimeOffset? last, DateTimeOffset cutoff)
    {
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, $"v{store.All().Count + 1}", 2, 2, [],
            Path.Combine(Paths.Data, $"not-read-here-{Guid.NewGuid():n}.csv"), "aa11", 1000,
            first, last, 0, [], false, 0, 0, 0, TapeNow, DatasetState.ACCEPTED, null, []));
        Assert.True(store.SetHoldout(id, cutoff, EvaluationClass.Research).Ok);
        return store.ById(id)!;
    }

    /// <summary>
    /// (a) A NEW ANNOUNCEMENT WAKES RESEARCH ONCE AND NAMES NO TEXT. The collector stores a new item and the app raises
    /// one <c>tape</c> wake for Research whose payload is source, series, subject and count; a second new item while it
    /// is untaken raises nothing more (coalesced); the turn it buys is told what changed without the item's title or
    /// address; and once that turn has taken it the next new item raises its own.
    ///
    /// <para>RED at base 43c894a4's behaviour, on the new seam (the collector told nothing, so no wake): "Assert.Single()
    /// Failure: The collection was empty". Mutant, the raise not coalesced (<c>Raise</c> for <c>RaiseCoalesced</c>):
    /// red, two untaken rows after the second item.</para>
    /// </summary>
    [Fact]
    public async Task A_new_announcement_wakes_research_once_and_names_no_text()
    {
        var (db, root) = Workspace();
        using var _ = db;
        using var archive = new FakeArchive();
        var okx = Okx();
        var path = TapeCollector.Url(okx.Series[0], "", null);
        const string title = "OKX will launch ZZZ for spot trading";

        archive.PublishAt(path, AnnouncementPage(Announcement("okx-will-launch-zzz", TapeNow.AddHours(-1), title)));
        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));
        await using var collector = ToldCollector(store, archive, okx, db);

        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);

        var events = new MissionEventStore(db);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.Equal(CouncilRoles.Research, wake.Role);
        var payload = Json.Read<TapeWakePayload>(wake.Payload!)!;
        Assert.Equal(new TapeWakePayload(okx.Id, okx.Series[0].Id,
            TapeParse.ItemSubject(AnnouncementUrl("okx-will-launch-zzz")), 1), payload);
        foreach (var text in new[] { title, "zzz", "example.invalid", "launch" })
            Assert.DoesNotContain(text, wake.Payload!, StringComparison.OrdinalIgnoreCase);

        // COALESCED: a second new item while the first wake is untaken buys nothing more.
        archive.PublishAt(path, AnnouncementPage(
            Announcement("okx-will-launch-zzz", TapeNow.AddHours(-1), title),
            Announcement("okx-will-delist-yyy", TapeNow.AddMinutes(-30), "OKX will delist YYY")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Single(events.OfKind(MissionEventKind.Tape));

        // THE TURN IT BUYS is Research's, told what changed and never the item.
        var host = new CouncilHost(db, root, new Concurrency());
        var now = TapeNow;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        await loop.TurnAsync();
        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Research, role);
        Assert.Contains("TradeAgent stored something new on the tape that your work reads", prompt, StringComparison.Ordinal);
        foreach (var text in new[] { title, "ZZZ", "YYY", "example.invalid" })
            Assert.DoesNotContain(text, prompt, StringComparison.Ordinal);

        // AND ONCE TAKEN, the next new item raises its own.
        archive.PublishAt(path, AnnouncementPage(Announcement("okx-will-list-xxx", TapeNow.AddMinutes(-10), "OKX will list XXX")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(2, events.OfKind(MissionEventKind.Tape).Count);
        Assert.Single(events.OfKind(MissionEventKind.Tape), e => !e.Consumed);
    }

    /// <summary>
    /// (a2) A NOTICE THE LISTENER FAILS IS TOLD AGAIN ON THE NEXT POLL, AND IT IS STILL ONE WAKE. The tape committed the
    /// item and the listener threw; the identical page polled again stores nothing new, and the collector tells the
    /// pending notice anyway — once it succeeds it is not told again. A listener that raised and then threw is retried
    /// too, and the coalesced raise keeps it one untaken wake.
    ///
    /// <para>RED at 27cb7bcf (a failed notice was dropped): "Assert.Single() Failure: The collection was empty".</para>
    /// </summary>
    [Fact]
    public async Task A_notice_the_listener_fails_is_told_again_on_the_next_poll_and_is_still_one_wake()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        using var archive = new FakeArchive();
        var okx = Okx();
        var path = TapeCollector.Url(okx.Series[0], "", null);
        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));

        var calls = 0;
        var throwAt = new HashSet<int> { 1 };
        var raiseFirst = false;
        await using var collector = new TapeCollector(store, baseUrl: archive.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5), now: () => TapeNow,
            catalog: new TapeSourceCatalogRead([okx], null, []),
            stored: arrival =>
            {
                calls++;
                if (raiseFirst) TapeWakes.Raise(db, arrival, TapeNow);
                if (throwAt.Contains(calls)) throw new IOException("the listener fell over");
                if (!raiseFirst) TapeWakes.Raise(db, arrival, TapeNow);
            });

        // THE LISTENER THROWS BEFORE IT RAISES: nothing yet, and the identical page polled again tells it.
        archive.PublishAt(path, AnnouncementPage(Announcement("okx-will-launch-zzz", TapeNow.AddHours(-1), "OKX will launch ZZZ")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Empty(events.OfKind(MissionEventKind.Tape));
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.Equal(CouncilRoles.Research, wake.Role);
        Assert.Equal(1, Json.Read<TapeWakePayload>(wake.Payload!)!.Count);

        // TOLD ONCE IT SUCCEEDED, AND NOT AGAIN.
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(2, calls);

        // THE LISTENER RAISES AND THEN THROWS: retried on the next poll, and still one untaken wake.
        events.Consume([wake.Id], "turn-1", TapeNow);
        raiseFirst = true;
        throwAt.Add(3);
        archive.PublishAt(path, AnnouncementPage(
            Announcement("okx-will-launch-zzz", TapeNow.AddHours(-1), "OKX will launch ZZZ"),
            Announcement("okx-will-list-xxx", TapeNow.AddMinutes(-10), "OKX will list XXX")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(4, calls);
        Assert.Equal(2, events.OfKind(MissionEventKind.Tape).Count);
        Assert.Single(events.OfKind(MissionEventKind.Tape), e => !e.Consumed);
    }

    /// <summary>
    /// (a3) NO UNHELD NOTICE IS EVER DROPPED BY THE BOUND ON WHAT IS PENDING (review 2). The listener falls over for 257
    /// storing fetches of one series; only the first stored an item outside every holdout window, the 256 after it are
    /// all inside August, which is held. Pending notices coalesce per series into one, so nothing has to be evicted, and
    /// once the listener recovers an unchanged page polled again still raises Research's wake — counting and naming only
    /// the item outside.
    ///
    /// <para>RED at fa8f86c4 (≤ 256 notices, the oldest evicted): "Assert.Single() Failure: The collection was empty".</para>
    /// </summary>
    [Fact]
    public async Task A_full_backlog_of_held_notices_never_evicts_the_one_outside_every_window()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        using var archive = new FakeArchive();
        var okx = Okx();
        var path = TapeCollector.Url(okx.Series[0], "", null);
        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));
        HeldDataset(new DatasetStore(db), new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var down = true;
        await using var collector = new TapeCollector(store, baseUrl: archive.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5), now: () => TapeNow,
            catalog: new TapeSourceCatalogRead([okx], null, []),
            stored: arrival =>
            {
                if (down) throw new IOException("the listener fell over");
                TapeWakes.Raise(db, arrival, TapeNow);
            });

        // THE FIRST STORING FETCH IS OUTSIDE EVERY WINDOW; THE 256 AFTER IT ARE INSIDE AUGUST.
        archive.PublishAt(path, AnnouncementPage(Announcement("okx-will-list-outside", TapeNow.AddHours(-1), "OKX will list OUT")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        var held = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 256; i++)
        {
            archive.PublishAt(path, AnnouncementPage(Announcement($"okx-will-list-held-{i}", held.AddMinutes(i), $"OKX will list H{i}")));
            Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        }
        Assert.Empty(events.OfKind(MissionEventKind.Tape));

        // THE LISTENER RECOVERS, AND THE UNCHANGED PAGE POLLED AGAIN STORES NOTHING: the item outside still wakes Research.
        down = false;
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.Equal(CouncilRoles.Research, wake.Role);
        var payload = Json.Read<TapeWakePayload>(wake.Payload!)!;
        Assert.Equal(1, payload.Count);
        Assert.Equal(TapeParse.ItemSubject(AnnouncementUrl("okx-will-list-outside")), payload.Subject);
        Assert.DoesNotContain("held", wake.Payload!, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a4) A RETRY NEVER RAISES A SECOND WAKE FOR WHAT A TAKEN WAKE ALREADY CARRIED, AND A HELD FETCH NEVER BUYS ONE
    /// (review 3). The listener raises Research's wake for an item outside every window and then falls over; Research's
    /// turn takes that wake. While the listener keeps falling over, a fetch of the same series stores an item inside
    /// August, which is held. Once it recovers, an unchanged page polled again raises nothing: the failed notice is retried
    /// as it was offered, under the id its taken wake already holds, and the held fetch is a notice of its own that owns
    /// nothing outside a window.
    ///
    /// <para>RED at 0afd7deb (the held fetch extended the failed notice, so its retry carried the old item under a new
    /// id): "Assert.Single() Failure: The collection contained 2 items". Mutant, the failed notice not frozen
    /// (<c>Offered</c> a no-op): red, the same line.</para>
    /// </summary>
    [Fact]
    public async Task A_retry_never_raises_a_second_wake_for_a_taken_one_and_a_held_fetch_never_buys_one()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        using var archive = new FakeArchive();
        var okx = Okx();
        var path = TapeCollector.Url(okx.Series[0], "", null);
        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));
        HeldDataset(new DatasetStore(db), new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var down = true;
        await using var collector = new TapeCollector(store, baseUrl: archive.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5), now: () => TapeNow,
            catalog: new TapeSourceCatalogRead([okx], null, []),
            stored: arrival =>
            {
                TapeWakes.Raise(db, arrival, TapeNow);
                if (down) throw new IOException("the listener fell over after it raised");
            });

        // THE LISTENER RAISES FOR THE ITEM OUTSIDE AND FALLS OVER; RESEARCH'S TURN TAKES THE WAKE.
        var outside = Announcement("okx-will-list-outside", TapeNow.AddHours(-1), "OKX will list OUT");
        archive.PublishAt(path, AnnouncementPage(outside));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        events.Consume([wake.Id], "turn-1", TapeNow);

        // STILL FALLING OVER, A HELD ITEM OF THE SAME SERIES ARRIVES.
        archive.PublishAt(path, AnnouncementPage(outside,
            Announcement("okx-will-list-held", new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), "OKX will list H")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);

        // RECOVERED, THE UNCHANGED PAGE POLLED AGAIN: every notice is told, and still the one wake, taken.
        down = false;
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.All(events.OfKind(MissionEventKind.Tape), e => Assert.True(e.Consumed));

        // AND A NEW ITEM OUTSIDE EVERY WINDOW STILL RAISES ITS OWN: nothing new is coalesced away under the taken wake.
        archive.PublishAt(path, AnnouncementPage(outside,
            Announcement("okx-will-list-next", TapeNow.AddMinutes(-5), "OKX will list NEXT")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(2, events.OfKind(MissionEventKind.Tape).Count);
        Assert.Single(events.OfKind(MissionEventKind.Tape), e => !e.Consumed);
    }

    /// <summary>
    /// (a5) A HELD ARRIVAL NEVER BRINGS A RETRY FORWARD (review 3). The listener falls over before it raises on an item
    /// outside every window, and again when the next look retries it; in that look a fetch stores an item inside August.
    /// The wake is not raised on the held fetch's timing — that would say something arrived in a held month — but at the
    /// next look, which the timer starts.
    ///
    /// <para>RED at 0afd7deb (every storing fetch told every pending notice): "Assert.Equal() Failure: Values differ
    /// Expected: 2 Actual: 3" — the held fetch's arrival retried the failed notice.</para>
    /// </summary>
    [Fact]
    public async Task A_held_arrival_never_brings_a_failed_notice_s_retry_forward()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        using var archive = new FakeArchive();
        var okx = Okx();
        var path = TapeCollector.Url(okx.Series[0], "", null);
        using var store = new TapeStore(Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db"));
        HeldDataset(new DatasetStore(db), new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        var calls = 0;
        await using var collector = new TapeCollector(store, baseUrl: archive.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5), now: () => TapeNow,
            catalog: new TapeSourceCatalogRead([okx], null, []),
            stored: arrival =>
            {
                if (++calls <= 2) throw new IOException("the listener fell over");
                TapeWakes.Raise(db, arrival, TapeNow);
            });

        // LOOK 1: the item outside is stored and its notice fails.
        var outside = Announcement("okx-will-list-outside", TapeNow.AddHours(-1), "OKX will list OUT");
        archive.PublishAt(path, AnnouncementPage(outside));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);

        // LOOK 2: its retry fails at the look's start; the listener would answer now, but a held item is what just arrived.
        archive.PublishAt(path, AnnouncementPage(outside,
            Announcement("okx-will-list-held", new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), "OKX will list H")));
        Assert.Equal(1, (await collector.CollectOnceAsync(okx)).Stored);
        Assert.Equal(2, calls);
        Assert.Empty(events.OfKind(MissionEventKind.Tape));

        // LOOK 3: the timer's look tells it, one wake counting only the item outside.
        Assert.Equal(0, (await collector.CollectOnceAsync(okx)).Stored);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        var payload = Json.Read<TapeWakePayload>(wake.Payload!)!;
        Assert.Equal(1, payload.Count);
        Assert.Equal(TapeParse.ItemSubject(AnnouncementUrl("okx-will-list-outside")), payload.Subject);
    }

    /// <summary>
    /// (b) A TAPE ITEM INSIDE A HOLDOUT WINDOW WAKES NOBODY — a window that ends, and one with no end because its dataset
    /// does not record where its bars end. An item outside every window still wakes Research, and the held one beside it
    /// is neither counted nor named.
    ///
    /// <para>RED at base 43c894a4's behaviour on the new seam (no holdout check): a wake for the held item. Mutant, the
    /// window check skipped: red, "Assert.False() Failure" on the item inside August.</para>
    /// </summary>
    [Fact]
    public void A_tape_item_inside_a_holdout_window_wakes_nobody()
    {
        using var db = TestEnv.NewDb();
        var datasets = new DatasetStore(db);
        var events = new MissionEventStore(db);
        var okx = Okx();
        TapeArrival Arrival(long fetch, params (string Subject, DateTimeOffset At)[] items) =>
            new(okx.Id, okx.Series[0].Id, okx.Parser, fetch, [.. items.Select(i => new TapeArrived(i.Subject, i.At))]);

        var august = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        HeldDataset(datasets, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero), august);

        // INSIDE A WINDOW THAT ENDS: nothing.
        Assert.False(TapeWakes.Raise(db, Arrival(1, ("aaaa", new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero))), TapeNow));
        Assert.Empty(events.OfKind(MissionEventKind.Tape));

        // INSIDE A WINDOW WITH NO END: a dataset whose last bar is not recorded holds from its cutoff on, for good.
        HeldDataset(datasets, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), null,
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains(TapeHoldout.Pipe(CouncilRoles.Research, datasets).Windows(), w => w.Until is null);
        Assert.False(TapeWakes.Raise(db, Arrival(2, ("bbbb", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))), TapeNow));
        Assert.Empty(events.OfKind(MissionEventKind.Tape));

        // OUTSIDE EVERY WINDOW: one wake, counting and naming only the item outside.
        Assert.True(TapeWakes.Raise(db, Arrival(3,
            ("cccc", new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)),
            ("dddd", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero))), TapeNow));
        var wake = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.Equal(new TapeWakePayload(okx.Id, okx.Series[0].Id, "cccc", 1), Json.Read<TapeWakePayload>(wake.Payload!));
        Assert.DoesNotContain("dddd", wake.Payload!, StringComparison.Ordinal);
    }

    /// <summary>
    /// (b2) A TAPE WAKE WHOSE ITEM A WINDOW DECLARED AFTER IT WAS QUEUED NOW HOLDS BUYS NOTHING AND NAMES NOTHING — the
    /// holdout is asked again when the wake is admitted and when the packet is rendered, never only when it was raised.
    /// A wake whose every item is now held is closed without a turn; one with an item still outside names only that
    /// one; and a window declared while the turn is being composed (here an open-ended one) takes the wake out of the
    /// packet, and the turn takes only what else woke it.
    ///
    /// <para>RED at 27cb7bcf (the payload cached at the raise, no check after it): "Assert.Empty() Failure: Collection was not empty" — Research launched on the held item.
    /// Mutant, the admission and render check removed (<c>Unheld</c> keeps every wake): red, the same line.</para>
    /// </summary>
    [Fact]
    public async Task A_tape_wake_a_later_holdout_covers_buys_no_turn_and_names_nothing()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var datasets = new DatasetStore(db);
        var events = new MissionEventStore(db);
        var okx = Okx();
        TapeArrival Arrival(long fetch, params (string Subject, DateTimeOffset At)[] items) =>
            new(okx.Id, okx.Series[0].Id, okx.Parser, fetch, [.. items.Select(i => new TapeArrived(i.Subject, i.At))]);
        static DateTimeOffset On(int month, int day) => new(2026, month, day, 12, 0, 0, TimeSpan.Zero);

        var host = new CouncilHost(db, root, new Concurrency());
        var now = TapeNow;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        // QUEUED OUTSIDE EVERY WINDOW, THEN HELD: May is declared held after the wake went in.
        Assert.True(TapeWakes.Raise(db, Arrival(1, ("subject-held-in-may", On(5, 15))), TapeNow));
        HeldDataset(datasets, On(5, 1), On(5, 31), On(5, 10));
        await loop.TurnAsync();
        Assert.Empty(host.Opened);
        var closed = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.NotNull(closed.ConsumedAt);
        Assert.Equal(MissionEventDisposition.Withdrawn, closed.Disposition);
        Assert.DoesNotContain("subject-held-in-may", closed.DispositionDetail ?? "", StringComparison.Ordinal);

        // ONE ITEM HELD LATER, ONE STILL OUTSIDE: the turn is bought by and names only the one outside.
        Assert.True(TapeWakes.Raise(db, Arrival(2,
            ("subject-outside-in-july", On(7, 15)), ("subject-held-in-june", On(6, 15))), TapeNow));
        HeldDataset(datasets, On(6, 1), On(6, 30), On(6, 10));
        await loop.TurnAsync();
        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Research, role);
        Assert.Contains("1 new item of", prompt, StringComparison.Ordinal);
        Assert.Contains("subject-outside-in-july", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-held-in-june", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("2 new items", prompt, StringComparison.Ordinal);

        // HELD WHILE THE TURN IS COMPOSED, BY A WINDOW WITH NO END: the turn runs for the look that also woke it, and
        // its packet says nothing of the tape.
        now = TapeNow.AddMinutes(1);
        Assert.True(TapeWakes.Raise(db, Arrival(3, ("subject-held-from-august", On(8, 15))), now));
        events.RaiseDue(MissionEventIds.ForRole(MissionEventIds.Review(now), CouncilRoles.Research),
            MissionEventKind.Review, now, now, role: CouncilRoles.Research);
        host.BeforeLaunch = _ => HeldDataset(datasets, On(8, 1), null, On(8, 10));
        await loop.TurnAsync();
        Assert.Equal(2, host.Opened.Count);
        var (late, packet) = host.Opened[1];
        Assert.Equal(CouncilRoles.Research, late);
        foreach (var text in new[] { "subject-held-from-august", "new item", "stored something new on the tape" })
            Assert.DoesNotContain(text, packet, StringComparison.Ordinal);
        var withdrawn = Assert.Single(events.OfKind(MissionEventKind.Tape), e => e.Id.StartsWith(MissionEventIds.Tape(3) + "#", StringComparison.Ordinal));
        Assert.Equal(MissionEventDisposition.Withdrawn, withdrawn.Disposition);
        Assert.Null(withdrawn.ConsumedBy);
    }

    /// <summary>
    /// (b3) A TAPE WAKE WHOSE WINDOWS CANNOT BE READ FOR A WHILE IS DEFERRED, NEVER LOST (review 2). Fail closed is "no
    /// launch and no name NOW": while the holdout windows throw the wake buys no turn, is named in no packet and is
    /// neither taken nor withdrawn — and it does not stand in front of another role's wake. Once the windows read again
    /// it is decided again, and it buys Research its turn.
    ///
    /// <para>RED at fa8f86c4 (a null read withdrew every tape wake): "Assert.Null() Failure: Value is not null" — the
    /// wake was withdrawn on the first look. Mutant, the withdraw on an unreadable read restored: red, the same line.</para>
    /// </summary>
    [Fact]
    public async Task A_tape_wake_whose_holdouts_cannot_be_read_is_deferred_and_decided_again_at_the_next_look()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var okx = Okx();
        var host = new CouncilHost(db, root, new Concurrency());
        var now = TapeNow;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        Assert.True(TapeWakes.Raise(db, new TapeArrival(okx.Id, okx.Series[0].Id, okx.Parser, 1,
            [new TapeArrived("subject-outside-every-window", TapeNow.AddHours(-1))]), TapeNow));
        now = TapeNow.AddMinutes(1);
        events.RaiseDue(MissionEventIds.ForRole(MissionEventIds.Review(now), CouncilRoles.Operations),
            MissionEventKind.Review, now, now, role: CouncilRoles.Operations);

        // THE WINDOWS THROW FOR A WHOLE LOOK: Operations, waiting behind the tape wake, gets its turn; Research does not,
        // no packet names the item, and the wake is still untaken and undisposed.
        host.HoldoutsUnreadable = true;
        await loop.TurnAsync();
        var tape = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.Null(tape.Disposition);
        Assert.Null(tape.ConsumedAt);
        Assert.Equal([CouncilRoles.Operations], host.Opened.Select(o => o.Role));
        Assert.DoesNotContain("subject-outside-every-window", host.Opened[0].Prompt, StringComparison.Ordinal);

        // THE NEXT LOOK READS THEM: the same wake buys Research its turn and is taken by it.
        host.HoldoutsUnreadable = false;
        await loop.TurnAsync();
        Assert.Equal([CouncilRoles.Operations, CouncilRoles.Research], host.Opened.Select(o => o.Role));
        Assert.Contains("subject-outside-every-window", host.Opened[1].Prompt, StringComparison.Ordinal);
        tape = Assert.Single(events.OfKind(MissionEventKind.Tape));
        Assert.NotNull(tape.ConsumedBy);
    }

    /// <summary>
    /// A MARKET READING WAKES RESEARCH ONLY WHILE AN ACTIVE DEPLOYMENT OF ITS VERSION READS IT. The input is the one the
    /// version's program declares, read back from the version the deployment runs; no deployment, or one that is not
    /// active, and the same reading wakes nobody — it arrives every poll.
    /// </summary>
    [Fact]
    public void A_reading_wakes_research_only_while_an_active_deployment_of_its_version_reads_it()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var program = StrategyParser.Parse(FeatureProgramGrammarTests.FundingProgram()).Program!;
        var input = program.Features[0].Spec.Inputs[0];
        var at = new DateTimeOffset(2026, 10, 4, 11, 0, 0, TimeSpan.Zero);
        TapeArrival Reading(long fetch) =>
            new(input.Source, input.Series, TapeSourceCatalog.JsonParser, fetch, [new TapeArrived(input.Subject, at)]);

        Assert.False(TapeWakes.Raise(db, Reading(1), TapeNow), "a reading nothing deployed reads woke Research");

        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, at.AddDays(-1), CouncilRoles.Research, null));
        var run = new Deployments(db).Start(new StrategyDeploymentRow(
            "", program.StrategyId, "allocation-1", "envelope-1", "paper", "account-1", "BTCUSDT",
            TradingMode.PAPER.ToString(), DeploymentState.Active, null, at, null, null, null));
        Assert.True(run.Ok, run.Why);

        Assert.True(TapeWakes.Raise(db, Reading(2), TapeNow));
        Assert.Equal(new TapeWakePayload(input.Source, input.Series, input.Subject, 1),
            Json.Read<TapeWakePayload>(Assert.Single(events.OfKind(MissionEventKind.Tape)).Payload!));

        // ANOTHER SUBJECT of the same series is not what the deployment reads.
        events.Consume([Assert.Single(events.OfKind(MissionEventKind.Tape)).Id], "turn-1", TapeNow);
        Assert.False(TapeWakes.Raise(db,
            new TapeArrival(input.Source, input.Series, TapeSourceCatalog.JsonParser, 3, [new TapeArrived("ETHUSDT", at)]),
            TapeNow));
    }

    static Publication AssessmentBy(string role, DateTimeOffset at)
    {
        var content = $"{BoundaryDeclaration.RecommendationPrefix} {BoundaryDisposition.Hold}\n"
                      + $"{BoundaryDeclaration.BaselinePrefix} {PromotionState.Unjudged}\n{role} read it.";
        return new Publication
        {
            Id = Publication.IdOf(role, PublicationKind.Assessment, content),
            Role = role,
            Kind = PublicationKind.Assessment,
            Recipients = string.Join(",", CouncilRelay.RecipientsOf(role)),
            CreatedAt = at,
            Content = content
        };
    }

    /// <summary>
    /// (c) AN UNASSESSED BOUNDARY WAKES ITS DIRECTOR BEFORE ITS DEADLINE, AND AN ASSESSED ONE DOES NOT. Both directors
    /// take the boundary's opening wake; Research assesses and Operations does not; a quarter of the window before the
    /// deadline Operations is woken once, keyed <c>deadline:&lt;boundary&gt;:operations</c>, and Research's reminder is
    /// closed without a turn.
    ///
    /// <para>RED at base 43c894a4: no deadline wake at all — "Assert.Single() Failure: The collection was empty".</para>
    /// </summary>
    [Fact]
    public async Task An_unassessed_boundary_wakes_its_director_before_its_deadline_and_an_assessed_one_does_not()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var t0 = Noon();
        var now = t0;
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        var events = host.Events!;

        var boundary = host.Boundaries!.Open(BoundaryKind.Promotion, "version-a", 1, BoundaryDisposition.Hold,
            "holdout run 1", t0).Row;
        var due = boundary.DeadlineAt - TimeSpan.FromTicks((boundary.DeadlineAt - boundary.OpenedAt).Ticks / 4);

        // THE OPENING WAKE, the first call, taken by both directors.
        await loop.TurnAsync();
        await loop.TurnAsync();
        Assert.Equal(2, host.Opened.Count);
        foreach (var role in CouncilRoles.All)
        {
            var reminder = events.Get(MissionEventIds.Deadline(boundary.Id, role));
            Assert.NotNull(reminder);
            Assert.Equal((due, role, false), (reminder!.DueAt, reminder.Role, reminder.Consumed));
        }

        // RESEARCH ASSESSES; Operations does not.
        Assert.True(host.Boundaries.Assess(AssessmentBy(CouncilRoles.Research, t0.AddSeconds(5)), t0.AddSeconds(5)).Ok);

        now = due;
        await loop.TurnAsync();
        var (woken, prompt) = host.Opened[^1];
        Assert.Equal(3, host.Opened.Count);
        Assert.Equal(CouncilRoles.Operations, woken);
        Assert.Contains("a consequential boundary you have not assessed is near its deadline", prompt, StringComparison.Ordinal);

        var research = events.Get(MissionEventIds.Deadline(boundary.Id, CouncilRoles.Research))!;
        Assert.True(research.Consumed);
        Assert.Null(research.ConsumedBy);
        Assert.Equal(MissionEventDisposition.Withdrawn, research.Disposition);
        Assert.NotNull(events.Get(MissionEventIds.Deadline(boundary.Id, CouncilRoles.Operations))!.ConsumedBy);

        // ONCE PER BOUNDARY AND DIRECTOR: the passes after it raise nothing more, and nobody else is woken.
        now = due.AddSeconds(5);
        await loop.TurnAsync();
        Assert.Equal(3, host.Opened.Count);
        Assert.Equal(2, events.OfKind(MissionEventKind.Deadline).Count);
    }

    /// <summary>
    /// (e) NOTHING AN AGENT WRITES BECOMES A SUBSCRIPTION. No op and no op argument names a watch, a subscription or a
    /// deadline; <c>next.json</c> keeps its one field, and a <c>watch</c> written beside it raises nothing; the tape and
    /// deadline wakes are raised in exactly two places in the source — the app's tape listener and the loop's own
    /// schedule — and <see cref="TapeWakes"/> takes the database, the collector's own record of a fetch and the time.
    ///
    /// <para>Pins an absence, so it has no red at base; watched red with a planted op named <c>tape-watch</c>.</para>
    /// </summary>
    [Fact]
    public async Task Nothing_an_agent_writes_becomes_a_subscription()
    {
        string[] banned = ["watch", "subscri", "deadline"];
        var ops = typeof(Ops).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).ToArray();
        var args = GatewaySchema.Ops().SelectMany(o => o.Args.Select(a => $"{o.Op}.{a.Name}")).ToArray();
        foreach (var word in banned)
        {
            Assert.DoesNotContain(ops, o => o.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(args, a => a.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(["AfterSeconds"], typeof(MissionNext).GetProperties().Select(p => p.Name).ToArray());

        // A WATCH WRITTEN BESIDE THE DELAY raises nothing but the delay.
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);
        host.Events!.Raise("owner:1", MissionEventKind.Owner, now,
            Json.Write(new MissionOwnerMessage("look at the tape", now)), CouncilRoles.Operations);
        foreach (var role in CouncilRoles.All)
            File.WriteAllText(Path.Combine(host.HomeFor(role), MissionLoop.WakeFile),
                """{"after_seconds": 600, "watch": {"source": "okx-eea-announcements", "series": "announcements"}}""");
        await loop.TurnAsync();
        Assert.Single(host.Opened);
        Assert.Empty(host.Events.OfKind(MissionEventKind.Tape));
        Assert.Empty(host.Events.OfKind(MissionEventKind.Deadline));

        // THE TWO RAISERS, by source, and the listener's inputs.
        var src = Path.Combine(DayOnePrograms.RepoRoot(), "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories);
        string[] Using(string text) => [.. files.Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).Order()];
        Assert.Equal(["TradeAgent.AgentRuntime/TapeWakes.cs", "TradeAgent.Core/Db/MissionEventStore.cs"], Using("RaiseCoalesced("));
        Assert.Equal(["TradeAgent.AgentRuntime/MissionLoop.cs"], Using("MissionEventIds.Deadline("));
        Assert.Equal(["TradeAgent.AgentRuntime/MissionLoop.cs", "TradeAgent.Core/Db/MissionEventStore.cs"], Using("WithdrawDeadline("));
        Assert.Equal(["TradeAgent.App/AppHost.cs"], Using("TapeWakes.Raise("));

        var raise = typeof(TapeWakes).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(["Raise"], raise.Select(m => m.Name).ToArray());
        Assert.Equal([typeof(Database), typeof(TapeArrival), typeof(DateTimeOffset)],
            raise[0].GetParameters().Select(p => p.ParameterType).ToArray());
    }
}
