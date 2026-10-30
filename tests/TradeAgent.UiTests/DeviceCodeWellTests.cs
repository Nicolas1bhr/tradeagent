using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.VisualTree;
using TradeAgent.App;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>The headless Avalonia app these rows render in: the product's own theme, Skia's real text layout and a real frame.</summary>
public static class HeadlessUi
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TradeAgentApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessUi)));

    /// <summary>Runs <paramref name="body"/> on the headless UI thread; one session for the process (an Avalonia app is one per process).</summary>
    public static void Run(Action body) => Session.Value.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// THE DEVICE CODE SHOWS WHOLE (<c>U-device-code-clipped</c>). On the owner's Linux server (Xvnc 1280x800) the onboarding's code well showed
/// only the TOP HALF of the code's glyphs, so a sign-in could not be finished. Cause: the base TextBlock style gives every text a 20 px line
/// box, the code is 30 px (<see cref="Theme.Display"/>) and set no line height of its own, so its glyphs overflowed the line box and the
/// well's rounded clip cut what overflowed. (The Mac renders the same overflow; the headless render here reproduces the cut.)
///
/// <para>The measurement is of PIXELS, not of metrics that could agree with a wrong box: the well is rendered by Skia, the code's ink is
/// measured inside the well, and it is compared to the same text in the same font set free in a box with no line height set — its
/// whole glyph height whatever font this host has. RED at base: the well's ink was about half the free text's height.</para>
/// </summary>
public class DeviceCodeWellTests(ITestOutputHelper output)
{
    const string Code = "WDJB-MJHT";

    record Ink(int Left, int Top, int Right, int Bottom)
    {
        public int Height => Bottom - Top + 1;
        public int Width => Right - Left + 1;
    }

    /// <summary>The bounding box of the pixels in <paramref name="area"/> (frame coordinates) that are brighter than the sunken well's own.</summary>
    static Ink? InkIn(WriteableBitmap frame, PixelRect area)
    {
        using var fb = frame.Lock();
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = Math.Max(0, area.Y); y < Math.Min(fb.Size.Height, area.Bottom); y++)
            for (var x = Math.Max(0, area.X); x < Math.Min(fb.Size.Width, area.Right); x++)
            {
                var px = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
                var sum = (px & 0xFF) + ((px >> 8) & 0xFF) + ((px >> 16) & 0xFF); // B + G + R; the well's own is ~30, the text's ~700
                if (sum < 300) continue;
                l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
            }
        return r < 0 ? null : new Ink(l, t, r, b);
    }

    static PixelRect Pixels(Visual v, Visual root)
    {
        var at = v.TranslatePoint(new Point(0, 0), root) ?? default;
        return new PixelRect((int)Math.Round(at.X), (int)Math.Round(at.Y), (int)Math.Ceiling(v.Bounds.Width), (int)Math.Ceiling(v.Bounds.Height));
    }

    /// <summary>Renders <paramref name="well"/> in a window, returns the code's ink inside its box and the box.</summary>
    static (Ink? Ink, PixelRect Box) Measure(Func<Control> build, Func<Control, Control> findBox)
    {
        Ink? ink = null; PixelRect box = default;
        HeadlessUi.Run(() =>
        {
            var well = build();
            var win = new Window { Width = 800, Height = 300, Content = new Border { Padding = new Thickness(20), Child = well } };
            win.Show();
            win.UpdateLayout();
            var boxControl = findBox(well);
            box = Pixels(boxControl, win);
            var frame = win.CaptureRenderedFrame() ?? throw new InvalidOperationException("the headless window rendered no frame");
            ink = InkIn(frame, box);
            win.Close();
        });
        return (ink, box);
    }

    [Fact]
    public void The_onboarding_device_code_shows_its_whole_glyph_height_inside_its_well()
    {
        // THE REFERENCE: the same text, font, size and spacing free of any line box — what "whole" is on this host's fonts.
        Control Free() => new Border
        {
            Background = Theme.BgSunken, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Padding = new Thickness(Theme.S5, Theme.S4),
            Child = new TextBlock
            {
                Text = Code, FontFamily = Theme.Mono, FontSize = Theme.Display, FontWeight = FontWeight.SemiBold,
                Foreground = Theme.Text, LetterSpacing = 3, LineHeight = double.NaN
            }
        };
        var (whole, _) = Measure(Free, c => c);
        Assert.True(whole is { Height: > 15 }, $"the reference text drew no ink: this host has no usable font ({whole})");

        var (ink, box) = Measure(() => OnboardingView.CodeWell(Code), c => c.GetVisualDescendants().OfType<Border>().First(b => b.Child is TextBlock { Text: Code }));
        output.WriteLine($"box {box.Width}x{box.Height} at y={box.Y}; ink in it {ink?.Width}x{ink?.Height} (top {ink?.Top - box.Y} below the box's top, bottom {box.Bottom - 1 - ink?.Bottom} above its bottom); whole glyph height on this host {whole!.Height}, width {whole.Width}");

        Assert.NotNull(ink);
        Assert.True(Math.Abs(ink!.Height - whole.Height) <= 1,
            $"the code's glyphs are cut: {ink.Height}px of ink in the well, {whole.Height}px when set free");
        Assert.True(Math.Abs(ink.Width - whole.Width) <= 1, $"the code's width differs: {ink.Width}px in the well, {whole.Width}px free");
        Assert.True(ink.Top >= box.Y && ink.Bottom <= box.Bottom - 1, $"the ink {ink} leaves the well {box}");
    }

    [Fact]
    public void The_sign_in_cards_code_text_is_set_with_a_line_box_that_holds_its_size()
    {
        // The seat sign-in card builds its code from the same factory (Ui.Code); its line box must hold the display size.
        double lineHeight = 0;
        HeadlessUi.Run(() => lineHeight = Ui.Code(Code).LineHeight); // an Avalonia object is built on the UI thread, never the test's
        Assert.True(lineHeight >= Theme.Display, $"a {Theme.Display}px code in a {lineHeight}px line box overflows it");
        Assert.Equal(Theme.DisplayLine, lineHeight);
    }
}
