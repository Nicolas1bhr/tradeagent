using System.Diagnostics;
using Avalonia.Controls;
using SkiaSharp;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE GLASS, DRAWN (<c>U-ui-world</c>): everything the window lays over the floor is a pane the floor's own painter draws — the floor
/// behind it out of focus, bent at the rim — so it is the same picture on every platform. These rows paint real frames of the seeded
/// working organisation with Skia on the processor alone (no graphics card: the slowest way a frame is ever painted) and hold what
/// the glass promises: it changes only where a pane lies, it leaves the floor it lies on as it was, a machine that cannot afford the
/// floor twice gets a pane that does not draw it twice, and what a frame costs is said.
/// </summary>
public class GlassRenderTests(ITestOutputHelper output)
{
    const int Width = 1280, Height = 800;

    static readonly FloorFrame.Pane Sheet = new(549, 66, 717, 660, 26, 1);
    static readonly FloorFrame.Pane Dock = new(120, 738, 1040, 48, 24, 1);
    static readonly FloorFrame.Pane Status = new(380, 12, 886, 40, 20, 1);

    /// <summary>A frame of the working organisation with two turns open, as the floor's own page makes it.</summary>
    static FloorFrame Frame(Database db, AppHost host)
    {
        FloorFrame? frame = null;
        HeadlessUi.Run(() =>
        {
            var page = new FloorPage(FloorSources.For(host), () => Task.CompletedTask, _ => { }) { Manual = true };
            var win = new Window { Width = Width, Height = Height, Content = page.Root };
            win.Show();
            win.UpdateLayout();
            page.Shown(true);
            page.ReadNow();
            win.UpdateLayout();
            for (var t = 0.0; t < 3; t += 0.05) page.Stage.Step(0.05f);
            frame = page.Stage.Last;
            win.Close();
        });
        return frame ?? throw new InvalidOperationException("the floor made no frame");
    }

    static FloorFrame With(FloorFrame f, FloorMotion motion, params FloorFrame.Pane[] panes) => new()
    {
        Width = f.Width, Height = f.Height, Time = f.Time, Unit = f.Unit, Asleep = f.Asleep, Camera = f.Camera, Motion = motion,
        Slabs = f.Slabs, Names = f.Names, Cells = f.Cells, Lines = f.Lines, Flights = f.Flights, Rings = f.Rings, Sparks = f.Sparks,
        Tape = f.Tape, Owner = f.Owner, OwnerAt = f.OwnerAt, OwnerLight = f.OwnerLight, OwnerWord = f.OwnerWord, Glass = panes
    };

    static SKBitmap Paint(FloorFrame frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        FloorPaint.Paint(canvas, frame);
        return bitmap;
    }

    static int Differing(SKBitmap a, SKBitmap b, SKRectI area)
    {
        var n = 0;
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    static double Mean(SKBitmap b, SKRectI area)
    {
        long sum = 0;
        var n = 0;
        for (var y = area.Top; y < area.Bottom; y += 3)
            for (var x = area.Left; x < area.Right; x += 3)
            {
                var c = b.GetPixel(x, y);
                sum += c.Red + c.Green + c.Blue;
                n++;
            }
        return n == 0 ? 0 : sum / (3.0 * n);
    }

    [Fact]
    public async Task A_pane_of_glass_changes_the_frame_where_it_lies_and_leaves_the_floor_beside_it_as_it_was()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"glass-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var frame = Frame(db, host);
            using var bare = Paint(With(frame, FloorMotion.Full));
            using var glazed = Paint(With(frame, FloorMotion.Full, Sheet));

            // WHERE THE PANE LIES the frame is another picture: the floor out of focus under a veil, and a rim.
            var inside = SKRectI.Create((int)Sheet.X + 4, (int)Sheet.Y + 4, (int)Sheet.Width - 8, (int)Sheet.Height - 8);
            Assert.True(Differing(bare, glazed, inside) > 2000, "a pane of glass drew nothing");
            // ITS RIM CATCHES THE LIGHT: along the top edge the pane is lighter than its own middle.
            var rim = Mean(glazed, SKRectI.Create((int)Sheet.X + 60, (int)Sheet.Y, 500, 2));
            var body = Mean(glazed, SKRectI.Create((int)Sheet.X + 60, (int)Sheet.Y + 300, 500, 60));
            Assert.True(rim > body + 12, $"the pane has no rim: {rim:0} along its top edge, {body:0} in its middle");

            // BESIDE IT — clear of the soft shadow it lays — the floor is drawn exactly as it is drawn with no pane at all.
            var beside = SKRectI.Create(0, 0, (int)Sheet.X - 60, Height);
            Assert.Equal(0, Differing(bare, glazed, beside));
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    [Fact]
    public async Task Without_a_graphics_card_a_pane_does_not_draw_the_floor_a_second_time_and_is_still_a_pane()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"glass-calm-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var frame = Frame(db, host);
            // A PANE OVER THE WHOLE FRAME, on a calm floor: what is under it is the window's own dark, nearly whole — the lit cells do
            // not come through, because the floor was not drawn again to come through.
            var over = new FloorFrame.Pane(0, 0, Width, Height, 0, 1);
            using var calm = Paint(With(frame, FloorMotion.Calm, over));
            using var bare = Paint(With(frame, FloorMotion.Calm));
            // Measured as how much the picture varies from place to place, well inside the pane: the floor is slabs, cells and a pool of
            // light; the calm pane over it is one dark, top to bottom.
            double Spread(SKBitmap b)
            {
                double least = double.MaxValue, most = double.MinValue;
                for (var y = 120; y < Height - 160; y += 40)
                    for (var x = 80; x < Width - 80; x += 40)
                    {
                        var here = Mean(b, SKRectI.Create(x, y, 40, 40));
                        var above = Mean(b, SKRectI.Create(x, y - 40, 40, 40));
                        least = Math.Min(least, here - above);
                        most = Math.Max(most, here - above);
                    }
                return most - least;
            }
            Assert.True(Spread(bare) > 8, $"the seeded floor has nothing on it to hide: {Spread(bare):0.0}");
            Assert.True(Spread(calm) < 6 && Spread(calm) < Spread(bare) / 5, $"a calm pane let the floor through: {Spread(calm):0.0} (the floor alone: {Spread(bare):0.0})");
            // AND IT STILL HAS ITS RIM: a pane is told from the floor on every machine.
            using var sheet = Paint(With(frame, FloorMotion.Calm, Sheet));
            var rim = Mean(sheet, SKRectI.Create((int)Sheet.X + 60, (int)Sheet.Y, 500, 2));
            var body = Mean(sheet, SKRectI.Create((int)Sheet.X + 60, (int)Sheet.Y + 300, 500, 60));
            Assert.True(rim > body + 12, $"a calm pane has no rim: {rim:0} against {body:0}");
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// WHAT A FRAME COSTS, SAID: painted on the processor alone, the floor by itself and under the window's three panes, in full
    /// motion (the floor drawn again behind each pane) and calm (not). No figure is asserted — a build machine's speed is nobody's
    /// promise — but the two things that must stay true are: the calm panes cost next to nothing over the floor itself, and the
    /// figures are written where a reader of the run finds them.
    /// </summary>
    [Fact]
    public async Task What_a_frame_of_glass_costs_on_the_processor_alone_is_measured_and_the_calm_glass_is_nearly_free()
    {
        using var db = OwnerPagesSeed.Working(Path.Combine(UiTestHome.Home, $"glass-cost-{Guid.NewGuid():n}.db"));
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var frame = Frame(db, host);
            double Ms(FloorFrame f)
            {
                using (Paint(f)) { }                                    // once to warm the faces and shaders
                const int n = 24;
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < n; i++)
                    using (Paint(f)) { }
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds / n;
            }
            double floorFull = Ms(With(frame, FloorMotion.Full)), glassFull = Ms(With(frame, FloorMotion.Full, Sheet, Dock, Status));
            double floorCalm = Ms(With(frame, FloorMotion.Calm)), glassCalm = Ms(With(frame, FloorMotion.Calm, Sheet, Dock, Status));
            output.WriteLine($"{Width}x{Height}, processor only, mean of 24 frames: floor {floorFull:0.0} ms, floor under three panes {glassFull:0.0} ms; "
                             + $"calm floor {floorCalm:0.0} ms, calm floor under three panes {glassCalm:0.0} ms");
            Assert.True(glassCalm < floorCalm * 1.6 + 2, $"calm glass is not nearly free: {glassCalm:0.0} ms against {floorCalm:0.0} ms for the floor alone");
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
}
