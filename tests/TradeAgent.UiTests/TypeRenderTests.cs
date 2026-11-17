using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using TradeAgent.App;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RENDER ROWS DRAW THE APP'S OWN TYPE, AND A FIGURE HAS ONE WIDTH ON EVERY PLATFORM (<c>U-ui-world</c>).
///
/// <para><b>What was wrong.</b> The test host builds a new application for every render, each with a font manager of its own, and the app
/// registered its embedded Inter once per process: the first render of a process drew the pages in Inter and every later one in the
/// machine's fallback (Helvetica Neue on a Mac, DejaVu Sans on Linux). So the rows that measure whether a page fits measured another
/// face than the window draws. And the face for figures named no family a Linux machine has, so there a figure was set in the default
/// proportional face — a column of prices that jitters, and a device code whose weight depended on which text had been drawn before it
/// (CI's Linux run of <see cref="DeviceCodeWellTests"/> at 486df768: "30px of ink in the well, 28px when set free").</para>
/// </summary>
public class TypeRenderTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_render_of_the_test_host_sets_the_pages_in_the_apps_own_face()
    {
        // THREE renders: the first of a process always had the face, so one render proves nothing.
        for (var render = 1; render <= 3; render++)
        {
            string? family = null;
            HeadlessUi.Run(() => family = FontManager.Current.TryGetGlyphTypeface(new Typeface(Theme.Sans), out var face) ? face.FamilyName : null);
            Assert.True(family == "Inter", $"render {render} of this process sets the pages in {family ?? "no face"}, not in Inter");
        }
    }

    [Fact]
    public void A_figure_is_set_in_a_face_of_one_width_on_this_machine()
    {
        string? family = null;
        double narrow = 0, wide = 0;
        HeadlessUi.Run(() =>
        {
            var mono = new Typeface(Theme.Mono);
            family = FontManager.Current.TryGetGlyphTypeface(mono, out var face) ? face.FamilyName : null;
            double Width(string text) => new TextLayout(text, mono, Theme.Small, Theme.Text).WidthIncludingTrailingWhitespace;
            narrow = Width("1111111111.,");
            wide = Width("0000000000WM");
        });
        output.WriteLine($"figures are set in {family}: twelve narrow characters {narrow:0.##} wide, twelve wide ones {wide:0.##}");
        Assert.NotNull(family);
        Assert.True(Math.Abs(narrow - wide) < 0.5,
            $"figures are set in {family}, which is not of one width: twelve narrow characters are {narrow:0.##} wide and twelve wide ones {wide:0.##}");
    }
}
