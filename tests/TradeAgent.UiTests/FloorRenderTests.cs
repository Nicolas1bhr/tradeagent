using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FLOOR, RENDERED (<c>U-ui-alive</c>). The landing page built the way <c>MainWindow</c> builds it — over a host composed on a database
/// this test seeded through the app's own stores — read, stepped, and drawn by Skia in the frame the window gives it: edge to edge beside
/// the rail and under the header, at the two window sizes the owner meets. Set <c>TA_UI_SHOTS</c> to a folder to keep the frames.
///
/// <para>What the rows hold: the page draws every seat the chart has; a cell with a turn open is brighter on the screen than one with
/// none — measured in pixels, not in the scene's own numbers; the words over the stage are the ledgers' and stay inside the page; a replay
/// plays the records in order and gives the floor back as it was; and the stage draws while something moves and stops when nothing does.</para>
/// </summary>
public class FloorRenderTests(ITestOutputHelper output)
{
    static readonly string? Shots = Environment.GetEnvironmentVariable("TA_UI_SHOTS") is { Length: > 0 } dir ? dir : null;

    /// <summary>A database a developer seeded with <c>tests/TradeAgent.FloorDemo</c>, to look at the floor over a fuller organisation; unset, those rows do nothing.</summary>
    static readonly string? DemoDb = Environment.GetEnvironmentVariable("TA_FLOOR_DEMO_DB") is { Length: > 0 } path ? path : null;

    /// <summary>A folder to keep a replay's frames in, one PNG a frame, for a developer to make a film of; unset, that row does nothing.</summary>
    static readonly string? Film = Environment.GetEnvironmentVariable("TA_FLOOR_FILM") is { Length: > 0 } film ? film : null;

    static readonly double RailEdge = Theme.RailWidth + 1;

    static Window Framed(Control page, int width, int height)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{RailEdge.ToString(System.Globalization.CultureInfo.InvariantCulture)},*"),
            RowDefinitions = new RowDefinitions($"{Theme.HeaderHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)},*")
        };
        grid.Children.Add(new Border { Background = Theme.BgRail, [Grid.RowSpanProperty] = 2 });
        grid.Children.Add(new Border { Background = Theme.BgRail, [Grid.ColumnProperty] = 1 });
        page.IsVisible = true;
        // The floor runs edge to edge: the window's page frame has no padding round it (MainWindow.Select).
        grid.Children.Add(new Border { Child = page, [Grid.ColumnProperty] = 1, [Grid.RowProperty] = 1 });
        return new Window { Width = width, Height = height, Background = Theme.Bg, Content = grid };
    }

    static WriteableBitmap Capture(Window win, string? name = null)
    {
        var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window rendered no frame");
        if (Shots is not null && name is not null)
        {
            Directory.CreateDirectory(Shots);
            frame.Save(Path.Combine(Shots, name), new PngBitmapEncoderOptions());
        }
        return frame;
    }

    /// <summary>How bright the frame is round a point of the page (page coordinates): the mean of blue, green and red over a small square.</summary>
    static double Brightness(WriteableBitmap frame, System.Numerics.Vector2 at)
    {
        using var fb = frame.Lock();
        int cx = (int)(at.X + RailEdge), cy = (int)(at.Y + Theme.HeaderHeight), n = 0;
        long sum = 0;
        for (var y = Math.Max(0, cy - 2); y <= Math.Min(fb.Size.Height - 1, cy + 2); y++)
            for (var x = Math.Max(0, cx - 2); x <= Math.Min(fb.Size.Width - 1, cx + 2); x++)
            {
                var px = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
                sum += (px & 0xFF) + ((px >> 8) & 0xFF) + ((px >> 16) & 0xFF);
                n++;
            }
        return n == 0 ? 0 : sum / (3.0 * n);
    }

    static List<string> Texts(Control page) =>
        [.. page.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").Where(t => t.Length > 0)];

    /// <summary>A turn opened for <paramref name="role"/> at <paramref name="at"/> and left open, as a launch leaves it.</summary>
    static string Open(Database db, string role, DateTimeOffset at)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admitted = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = at, Role = role, ReservedCost = 0.40m, SeatClass = SeatClasses.Harness, BillingClass = BillingClasses.Key,
            Runtime = "claude", RequestedModel = "claude-sonnet",
            Assurance = SeatClasses.AssuranceOf(SeatClasses.Harness) is { } a ? SeatClasses.Name(a) : null,
            Funding = [OrgIds.Research, OrgIds.Root]
        });
        Assert.True(admitted.Admitted, admitted.Refusal);
        return id;
    }

    static void Step(FloorPage page, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 0.05) page.Stage.Step(0.05f);
    }

    static FloorPage Page(AppHost host) => new(FloorSources.For(host), () => Task.CompletedTask, _ => { }) { Manual = true };

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    public async Task The_floor_draws_the_working_organisation_and_lights_the_cells_whose_turns_are_open(int width, int height)
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"floor-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var page = Page(host);
                var win = Framed(page.Root, width, height);
                win.Show();
                win.UpdateLayout();
                page.Shown(true);
                page.ReadNow();
                win.UpdateLayout();
                Step(page, 2);
                Capture(win, $"floor-rest-{width}x{height}.png");

                // EVERY SEAT THE CHART HAS IS ON THE FLOOR: the positions that have not ended, the board's seats, and nothing else that takes a turn.
                var org = new OrgStore(db);
                var seats = org.Positions().Where(p => p.Status != OrgPositionStatus.Ended).Select(p => p.Id).ToList();
                var cells = page.Stage.Last!.Cells;
                Assert.All(seats, seat => Assert.Contains(cells, c => c.Id == seat));
                Assert.All(host.Board.Seats(), seat => Assert.Contains(cells, c => c.Id == seat.Id));
                Assert.Equal(seats.Count + host.Board.Seats().Count, cells.Count(c => c.Kind != FloorCellKind.Vacant));

                // NOTHING IS OPEN, AND THE PAGE SAYS SO in the launch ledger's own terms; the page's start is the Chat page's.
                var resting = Texts(page.Root);
                Assert.Contains(Labels.NavFloor, resting);
                Assert.Contains(Labels.FloorNobodyWorking, resting);
                Assert.Contains(Labels.FloorStart, page.Root.GetSelfAndVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string));

                // TWO TURNS OPEN, as two launches leave them.
                Open(db, OwnerPagesSeed.Executor1, DateTimeOffset.UtcNow.AddMinutes(-3));
                Open(db, OwnerPagesSeed.Head, DateTimeOffset.UtcNow.AddSeconds(-40));
                page.ReadNow();
                win.UpdateLayout();
                Step(page, 3);
                win.UpdateLayout();
                var frame = Capture(win, $"floor-working-{width}x{height}.png");

                // LIT ON THE SCREEN, measured in pixels: each working cell's middle is far brighter than a dormant cell's and an idle one's.
                var drawn = page.Stage.Last!.Cells.ToDictionary(c => c.Id);
                double working = Brightness(frame, drawn[OwnerPagesSeed.Executor1].Middle), head = Brightness(frame, drawn[OwnerPagesSeed.Head].Middle);
                double dormant = Brightness(frame, drawn[OwnerPagesSeed.Dormant].Middle), idle = Brightness(frame, drawn[OwnerPagesSeed.Executor2].Middle);
                output.WriteLine($"{width}x{height}: working {working:0}, head {head:0}, idle {idle:0}, dormant {dormant:0}");
                Assert.True(working > 150 && head > 150, $"a working cell is not lit: {working:0} and {head:0}");
                // The idle one stands beside the two that are working, in their light — brighter than it would be alone, and still plainly not lit.
                Assert.True(working - idle > 70 && head - idle > 70, $"a working cell cannot be told from an idle one: {working:0}, {head:0} and {idle:0}");
                Assert.True(dormant < 90 && dormant < idle, $"a dormant cell is lit: {dormant:0} (idle {idle:0})");

                // THE WORDS: who is working, by the chart's names, and each working cell named beside it by its short name with its clock.
                var names = OrgNames.Of(org.Units(), org.Positions());
                var texts = Texts(page.Root);
                Assert.Contains(Labels.FloorWorking([names.Position(OwnerPagesSeed.Executor1), names.Position(OwnerPagesSeed.Head)]), texts);
                Assert.Contains("Executor 1", texts);
                Assert.Contains("Head", texts);
                Assert.Contains(texts, t => t.StartsWith("3:0", StringComparison.Ordinal));   // open three minutes and a few seconds

                // WHAT HAPPENED, as the ledgers have it: the day's launches in the sentences, newest first, in the page's own words.
                Assert.Contains(texts, t => t == Labels.FloorTurnStarted(names.Position(OwnerPagesSeed.Head)));
                Assert.Contains(texts, t => t.StartsWith(names.Position(OwnerPagesSeed.Executor1) + " finished a turn after", StringComparison.Ordinal));

                // NOTHING THE PAGE WRITES RUNS OFF IT: every text ends inside the page.
                var edge = page.Root.Bounds.Width;
                foreach (var t in page.Root.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0))
                {
                    var right = (t.TranslatePoint(new Point(t.Bounds.Width, 0), page.Root) ?? default).X;
                    Assert.True(right <= edge + 1, $"“{t.Text}” ends at {right:0}, past the page's edge at {edge:0}");
                }

                // SOMETHING CROSSING THE FLOOR, for the frame a review looks at.
                var now = DateTimeOffset.UtcNow;
                page.Happened(new FloorEvent(now, FloorEventKind.Mandate, OwnerPagesSeed.Head)
                    { Target = OwnerPagesSeed.Executor2, Ref = OwnerPagesSeed.Executor2, Detail = MandateEventKind.Issued, Text = "Backtest the opening-range breakout on the last six months and report the costs." }, moving: true);
                page.Happened(new FloorEvent(now, FloorEventKind.Escalation, OwnerPagesSeed.Executor1) { Target = FloorEvent.Owner, Text = "The second source disagrees with the first for a whole week." }, moving: true);
                page.Happened(new FloorEvent(now, FloorEventKind.Tool, OwnerPagesSeed.Executor1) { Detail = "data", Text = "data bars BTCUSDT 1h", Ok = true }, moving: true);
                Step(page, 0.9);
                win.UpdateLayout();
                Capture(win, $"floor-moving-{width}x{height}.png");
                Assert.Contains("data bars BTCUSDT 1h", Texts(page.Root));
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    [Fact]
    public async Task A_replay_plays_the_records_in_order_says_it_is_one_and_gives_the_floor_back()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"replay-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var page = Page(host);
                var win = Framed(page.Root, 1280, 800);
                win.Show();
                win.UpdateLayout();
                page.Shown(true);
                page.ReadNow();
                win.UpdateLayout();
                var before = Texts(page.Root);
                Assert.DoesNotContain(before, t => t.StartsWith("Replay of the records", StringComparison.Ordinal));

                var replay = page.ReadReplay(null) ?? throw new InvalidOperationException("nothing to replay");
                var said = replay.Events.Where(e => e.Kind != FloorEventKind.Tool).ToList();
                Assert.True(said.Count >= 8, "the seeded home has too little recorded to replay");
                Assert.Equal(said.OrderBy(e => e.At).Select(e => e.At), said.Select(e => e.At));

                page.BeginReplay(replay);
                var seen = new List<string>();
                var first = true;
                for (var i = 0; i < 4000 && !replay.Ended; i++)
                {
                    page.ReplayStep(1 / 30.0);
                    page.Stage.Step(1 / 30f);
                    if (i % 15 != 0) continue;
                    win.UpdateLayout();
                    var texts = Texts(page.Root);
                    // WHILE IT PLAYS THE PAGE SAYS IT IS A REPLAY, and of when; the figures about now are not shown as if they were of then.
                    Assert.Contains(texts, t => t.StartsWith("Replay of the records", StringComparison.Ordinal) || t == Labels.FloorReplayEnded);
                    Assert.Contains(Labels.FloorReplayNote, texts);
                    Assert.Equal(0, page.Root.GetSelfAndVisualDescendants().OfType<Control>().Single(c => c.Name == "floor-figures").Opacity);
                    if (first && Shots is not null && texts.Any(t => t.EndsWith("are working.", StringComparison.Ordinal) || t.EndsWith("is working.", StringComparison.Ordinal)))
                    {
                        Capture(win, "floor-replay-1280x800.png");
                        first = false;
                    }
                    seen.AddRange(texts.Where(t => !seen.Contains(t)));
                }
                Assert.True(replay.Ended, "the replay did not reach its end");
                Assert.Equal(replay.Events.Count, replay.Next);                           // every record of the stretch was played, none twice
                // What it showed is what was recorded: a launch, a delivery and the decision the seed wrote.
                Assert.Contains(seen, t => t.Contains("started a turn.", StringComparison.Ordinal));
                Assert.Contains(seen, t => t.Contains("delivered to", StringComparison.Ordinal));
                Assert.Contains(seen, t => t.Contains("decided: “keep testing the skew”", StringComparison.Ordinal));

                // BACK TO NOW: the floor is the present's again, with the sentences and the figures it had.
                page.StopReplay();
                page.ReadNow();
                win.UpdateLayout();
                var after = Texts(page.Root);
                Assert.DoesNotContain(after, t => t.StartsWith("Replay of the records", StringComparison.Ordinal));
                Assert.DoesNotContain(Labels.FloorReplayNote, after);
                Assert.Equal(1, page.Root.GetSelfAndVisualDescendants().OfType<Control>().Single(c => c.Name == "floor-figures").Opacity);
                Assert.Equal(before.Where(t => t.Contains(" turn", StringComparison.Ordinal)), after.Where(t => t.Contains(" turn", StringComparison.Ordinal)));
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE STAGE DRAWS WHILE SOMETHING MOVES AND STOPS WHEN NOTHING DOES. The page as the window runs it — its own timers, its reads off the
    /// UI thread — over the headless platform's frame clock: the first read lays the floor and a frame is drawn; with nothing moving and no
    /// drift, tick after tick draws nothing more; and one thing sent across the floor draws frames again until it has arrived.
    /// </summary>
    [Fact]
    public async Task The_stage_draws_while_something_moves_and_draws_nothing_when_nothing_does()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"pump-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var page = new FloorPage(FloorSources.For(host), () => Task.CompletedTask, _ => { });
                page.Stage.Scene.Motion = FloorMotion.Calm;
                var win = Framed(page.Root, 1280, 800);
                win.Show();
                win.UpdateLayout();
                page.Shown(true);

                void Ticks(int n, Func<bool>? until = null)
                {
                    for (var i = 0; i < n && until?.Invoke() != true; i++)
                    {
                        Thread.Sleep(8);
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                    }
                }

                // THE FIRST READ comes back from its own thread, lays the floor, and the stage draws it.
                Ticks(400, () => page.Stage.Last is { Cells.Length: > 0 });
                Assert.True(page.Stage.Last is { Cells.Length: > 0 }, "the floor was never read and drawn");
                // It settles: lights reach their rest and nothing is left moving.
                Ticks(400, () => !page.Stage.Scene.Busy && !page.Stage.Scene.Breathing);
                Assert.False(page.Stage.Scene.Busy);
                Ticks(4);

                // NOTHING MOVES: tick after tick, no frame.
                var still = page.Stage.Frames;
                page.Stage.Scene.Motion = FloorMotion.Calm;
                Ticks(40);
                Assert.True(page.Stage.Frames - still <= 2, $"{page.Stage.Frames - still} frames were drawn of a floor on which nothing moved");

                // SOMETHING CROSSES THE FLOOR: it is drawn all the way, then the stage is still again.
                var arrived = false;
                page.Stage.Scene.Send(OwnerPagesSeed.Head, OwnerPagesSeed.Executor1, FloorTone.Work, 1, () => arrived = true);
                page.Stage.Wake();
                var moving = page.Stage.Frames;
                Ticks(600, () => arrived && !page.Stage.Scene.Busy);
                Assert.True(arrived, "what was sent never arrived");
                Assert.True(page.Stage.Frames - moving >= 10, $"only {page.Stage.Frames - moving} frames were drawn of something crossing the floor");

                page.Shown(false);
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    // ---- a developer's look at a fuller floor ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(1920, 1080)]
    public async Task The_floor_draws_a_developers_demo_home(int width, int height)
    {
        if (DemoDb is null) return;
        var copy = Path.Combine(UiTestHome.Home, $"demo-{Guid.NewGuid():n}.db");
        File.Copy(DemoDb, copy);
        using var db = new Database(copy);
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            HeadlessUi.Run(() =>
            {
                var page = Page(host);
                var win = Framed(page.Root, width, height);
                win.Show();
                win.UpdateLayout();
                page.Shown(true);
                page.ReadNow();
                win.UpdateLayout();
                Step(page, 2);
                Capture(win, $"demo-rest-{width}x{height}.png");

                var seats = new OrgStore(db).Positions().Where(p => p.Status == OrgPositionStatus.Active).Select(p => p.Id).ToList();
                var now = DateTimeOffset.UtcNow;
                foreach (var seat in seats.Skip(1).Take(3)) Open(db, seat, now.AddSeconds(-25 - 40 * seats.IndexOf(seat)));
                page.ReadNow();
                win.UpdateLayout();
                Step(page, 2.5);
                if (seats.Count > 6)
                {
                    page.Happened(new FloorEvent(now, FloorEventKind.Mandate, seats[2]) { Target = seats[5], Ref = seats[5], Detail = MandateEventKind.Issued, Text = "Collect a second source for the liquidation data and compare the two." }, moving: true);
                    page.Happened(new FloorEvent(now, FloorEventKind.Mandate, seats[6]) { Target = seats[3], Ref = seats[6], Detail = MandateEventKind.Delivered, Text = "Both sources agree within 2% on 97% of the hours." }, moving: true);
                    page.Happened(new FloorEvent(now, FloorEventKind.Tool, seats[1]) { Detail = "data", Text = "data bars BTCUSDT 1h", Ok = true }, moving: true);
                    page.Happened(new FloorEvent(now, FloorEventKind.Decision, seats[2]) { Text = "keep testing the skew" }, moving: true);
                }
                Step(page, 0.8);
                win.UpdateLayout();
                Capture(win, $"demo-moving-{width}x{height}.png");
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// A REPLAY, FRAME BY FRAME, over a developer's demo home: the last hours of its records played the way the page plays them, each
    /// thirtieth of a second drawn and kept. Nothing is asserted but that it draws; it exists so the motion can be looked at.
    /// </summary>
    [Fact]
    public async Task A_replay_of_a_developers_demo_home_is_drawn_frame_by_frame()
    {
        if (DemoDb is null || Film is null) return;
        var copy = Path.Combine(UiTestHome.Home, $"film-{Guid.NewGuid():n}.db");
        File.Copy(DemoDb, copy);
        using var db = new Database(copy);
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var hours = double.TryParse(Environment.GetEnvironmentVariable("TA_FLOOR_FILM_HOURS"), inv, out var h) ? h : 3;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("TA_FLOOR_FILM_SECONDS"), inv, out var sec) ? sec : 24;
        try
        {
            HeadlessUi.Run(() =>
            {
                Directory.CreateDirectory(Film);
                var page = Page(host);
                var win = Framed(page.Root, 1600, 900);
                win.Show();
                win.UpdateLayout();
                page.Shown(true);
                page.ReadNow();
                win.UpdateLayout();
                Step(page, 1);
                var replay = page.ReadReplay(TimeSpan.FromHours(hours)) ?? throw new InvalidOperationException("nothing to replay");
                page.BeginReplay(replay);
                const double frame = 1 / 30.0;
                for (var i = 0; i * frame < seconds; i++)
                {
                    page.ReplayStep(frame);
                    page.Stage.Step((float)frame);
                    win.UpdateLayout();
                    var shot = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
                    shot.Save(Path.Combine(Film, $"f{i:0000}.png"), new PngBitmapEncoderOptions());
                }
                win.Close();
            });
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}
