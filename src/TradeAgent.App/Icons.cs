using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace TradeAgent.App;

/// <summary>
/// THE APP'S OWN SMALL DRAWINGS (<c>U-ui-alive</c>): one line drawing per page of the rail, and the app's mark. Drawn here on a 24-unit
/// grid as plain path data — no icon font and nobody else's set — so a build carries nothing it has to credit and a page added to the rail
/// gets a drawing in the same hand.
///
/// <para>They are all lines, never fills: a rail of fourteen filled shapes is fourteen blobs at this size, and a line keeps its meaning at
/// seventeen pixels. A drawing takes its colour from whatever holds it (<see cref="For"/> binds the line to the holder's own foreground), so
/// the rail's hover, selected and resting states colour the drawing and the word together and no page paints an icon by hand.</para>
/// </summary>
static class Icons
{
    /// <summary>The floor: one cell, the thing the floor is made of.</summary>
    public const string Floor = "M12 3 L20 7.4 V16.6 L12 21 L4 16.6 V7.4 Z M4 7.4 L12 11.8 L20 7.4 M12 11.8 V21";

    public const string Chat = "M5.5 4 H18.5 A2.5 2.5 0 0 1 21 6.5 V13.5 A2.5 2.5 0 0 1 18.5 16 H11.5 L7 20 V16 H5.5 A2.5 2.5 0 0 1 3 13.5 V6.5 A2.5 2.5 0 0 1 5.5 4 Z";

    /// <summary>The organisation: one seat over two.</summary>
    public const string Organisation = "M9 3.5 H15 V8 H9 Z M3.5 16 H9.5 V20.5 H3.5 Z M14.5 16 H20.5 V20.5 H14.5 Z M12 8 V12 M6.5 16 V12 H17.5 V16";

    /// <summary>Work: three lines, each ticked.</summary>
    public const string Work = "M3.5 6 L5.2 7.7 L8 4.8 M11.5 6.3 H20.5 M3.5 12 L5.2 13.7 L8 10.8 M11.5 12.3 H20.5 M3.5 18 L5.2 19.7 L8 16.8 M11.5 18.3 H20.5";

    /// <summary>Costs: a stack of coins.</summary>
    public const string Costs = "M4 7 A8 3 0 1 0 20 7 A8 3 0 1 0 4 7 Z M4 7 V12 A8 3 0 0 0 20 12 V7 M4 12 V17 A8 3 0 0 0 20 17 V12";

    /// <summary>The dashboard: a dial and its needle.</summary>
    public const string Dashboard = "M4.2 17 A9 9 0 1 1 19.8 17 M12 13 L16.2 8.2 M3.5 20.5 H20.5";

    /// <summary>The board: a shield with its decision in it.</summary>
    public const string Board = "M12 3 L19.5 6 V11.5 C19.5 16 16.5 19.6 12 21.2 C7.5 19.6 4.5 16 4.5 11.5 V6 Z M8.8 12 L11.2 14.4 L15.4 9.8";

    public const string Report = "M7 3 H14 L19 8 V19 A2 2 0 0 1 17 21 H7 A2 2 0 0 1 5 19 V5 A2 2 0 0 1 7 3 Z M14 3 V8 H19 M8.5 13 H15.5 M8.5 17 H13.5";

    /// <summary>The AI's inbox: a tray.</summary>
    public const string Inbox = "M3 13 L6 5 H18 L21 13 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 Z M3 13 H8 L9.5 16 H14.5 L16 13 H21";

    /// <summary>What stays the owner's: a key.</summary>
    public const string Owner = "M3.5 15.5 A4.5 4.5 0 1 0 12.5 15.5 A4.5 4.5 0 1 0 3.5 15.5 Z M11.3 12.3 L20.5 3.2 M16.2 7.4 L19 10.2 M13.6 10 L15.6 12";

    /// <summary>What reached the owner: a bell.</summary>
    public const string ForYou = "M6 16 V11 A6 6 0 0 1 18 11 V16 L20 18.5 H4 Z M10 21.5 H14";

    /// <summary>Settings: two sliders.</summary>
    public const string Settings = "M3.5 7.5 H9.5 M14.5 7.5 H20.5 M9.5 7.5 A2.5 2.5 0 1 0 14.5 7.5 A2.5 2.5 0 1 0 9.5 7.5 Z M3.5 16.5 H7 M12 16.5 H20.5 M7 16.5 A2.5 2.5 0 1 0 12 16.5 A2.5 2.5 0 1 0 7 16.5 Z";

    public const string Activity = "M2.5 12 H7 L10 5 L14 19 L17 12 H21.5";

    public const string Checks = "M3 12 A9 9 0 1 0 21 12 A9 9 0 1 0 3 12 Z M8 12.2 L10.9 15.1 L16.2 9.3";

    /// <summary>The owner's private line: an eye.</summary>
    public const string Oversight = "M2.5 12 C5 7.4 8.4 5.2 12 5.2 C15.6 5.2 19 7.4 21.5 12 C19 16.6 15.6 18.8 12 18.8 C8.4 18.8 5 16.6 2.5 12 Z M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12 Z";

    /// <summary>A small arrow to the right, for a link that goes to another page.</summary>
    public const string Go = "M5 12 H19 M13 6 L19 12 L13 18";

    /// <summary>Replay: an arrow running back round a clock face.</summary>
    public const string Replay = "M4 12 A8 8 0 1 0 6.4 6.3 M4 4 V7.5 H7.5 M12 8 V12 L15 14";

    public const string Play = "M8 5 L19 12 L8 19 Z";

    public const string Pause = "M8 5 V19 M16 5 V19";

    public const string Close = "M6 6 L18 18 M18 6 L6 18";

    /// <summary>
    /// One drawing at the rail's size, its line the colour of <paramref name="holder"/>'s own foreground and following it as that changes.
    /// </summary>
    public static Control For(string data, TemplatedControl holder, double size = Theme.IconSize)
    {
        var path = Line(data, size);
        path[!Shape.StrokeProperty] = holder[!TemplatedControl.ForegroundProperty];
        return Boxed(path, size);
    }

    /// <summary>One drawing in a fixed colour.</summary>
    public static Control Of(string data, IBrush brush, double size = Theme.IconSize)
    {
        var path = Line(data, size);
        path.Stroke = brush;
        return Boxed(path, size);
    }

    /// <summary>The drawing's own path, for a caller that paints its line itself (the rail does, for the page it is on).</summary>
    public static Path Line(string data, double size = Theme.IconSize) => new()
    {
        Data = Geometry.Parse(data),
        Width = 24, Height = 24,
        StrokeThickness = Theme.IconStroke * 24 / size,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round
    };

    /// <summary>A 24-unit drawing scaled into a square of <paramref name="size"/>; the line's weight is set for that size, so it stays one weight.</summary>
    public static Control Boxed(Path path, double size) => new Viewbox
    {
        Width = size, Height = size, Child = path,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    /// <summary>
    /// THE APP'S MARK: one cell, its top lit. The same object the floor is made of, so the mark in the corner and the thing on the landing
    /// page are one idea — a sealed place with a light on in it.
    /// </summary>
    public static Control Mark(double size = 22)
    {
        var top = new Path
        {
            Data = Geometry.Parse("M12 2.5 L20.5 7 L12 11.5 L3.5 7 Z"),
            Fill = Theme.MarkLight, Width = 24, Height = 24
        };
        var left = new Path
        {
            Data = Geometry.Parse("M3.5 7 L12 11.5 V21.5 L3.5 17 Z"),
            Fill = Theme.MarkSideDark, Width = 24, Height = 24
        };
        var right = new Path
        {
            Data = Geometry.Parse("M20.5 7 L12 11.5 V21.5 L20.5 17 Z"),
            Fill = Theme.MarkSide, Width = 24, Height = 24
        };
        return new Viewbox
        {
            Width = size, Height = size,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = new Panel { Width = 24, Height = 24, Children = { left, right, top } }
        };
    }
}
