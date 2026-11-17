using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

// Inside a control subclass the bare name `Theme` is StyledElement's property, never the design-token class.
using Tokens = TradeAgent.App.Theme;

namespace TradeAgent.App;

/// <summary>
/// ONE ROW OF THE CHART, SET IN BY ITS DEPTH (<c>U-ui-world</c>): the hierarchy is drawn as indentation and a thin guide line for each level
/// above it, with an elbow into the row's own name — no box round anything. The row draws only when its depth or size changes.
/// </summary>
sealed class OrgTreeRow : Decorator
{
    int _depth = -1;

    /// <summary>Where, from the row's top, the elbow meets the row's name.</summary>
    public double ElbowAt { get; init; } = Tokens.S4 + 10;

    public OrgTreeRow(Control child)
    {
        Child = child;
        Depth = 0;
    }

    public int Depth
    {
        get => _depth;
        set
        {
            if (value == _depth) return;
            _depth = Math.Max(0, value);
            Padding = new Thickness(_depth * Tokens.TreeIndent, 0, 0, 0);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_depth == 0) return;
        var h = Bounds.Height;
        var pen = new Pen(Tokens.LineStrong, 1);
        for (var k = 1; k <= _depth; k++)
        {
            var x = Math.Round((k - 1) * Tokens.TreeIndent + Tokens.TreeIndent / 2) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, h));
            if (k == _depth)
                context.DrawLine(pen, new Point(x, ElbowAt), new Point(_depth * Tokens.TreeIndent - Tokens.S2, ElbowAt));
        }
    }
}

/// <summary>
/// A STATE AS A DOT (<c>U-ui-world</c>): lit and filled for a thing at work (active, open), a faint ring for one at rest (dormant, ended),
/// amber only where the app's caution rule says so. White at some strength otherwise: green means profit on this product, never "active".
/// </summary>
sealed class OrgStateDot : Control
{
    IBrush _tone = Tokens.Text;
    bool _filled = true;

    public OrgStateDot()
    {
        Width = Height = Tokens.StateDot;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(bool filled, IBrush tone)
    {
        if (filled == _filled && ReferenceEquals(tone, _tone)) return;
        _filled = filled;
        _tone = tone;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var r = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (_filled) context.DrawEllipse(_tone, null, c, r, r);
        else context.DrawEllipse(null, new Pen(_tone, 1.2), c, r - 0.6, r - 0.6);
    }
}

/// <summary>
/// A MANDATE'S TIME, DRAWN (<c>U-ui-world</c>): a thin line from when it was issued to its deadline, the time gone lit, "now" marked on it.
/// Past its deadline the line runs on to now in the caution colour, with the deadline ticked where it fell — the page's one amber, the app's
/// own rule for a late mandate. Nothing is drawn until it is told both ends.
/// </summary>
sealed class OrgTimeLine : Control
{
    DateTimeOffset _issued, _deadline, _now;
    bool _set;

    public OrgTimeLine()
    {
        Height = Tokens.TimeLineHeight;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MinWidth = 60;
    }

    public void Set(DateTimeOffset issued, DateTimeOffset deadline, DateTimeOffset now)
    {
        if (_set && issued == _issued && deadline == _deadline && now == _now) return;
        (_issued, _deadline, _now, _set) = (issued, deadline, now, true);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_set) return;
        var w = Bounds.Width;
        var mid = Math.Round(Bounds.Height / 2) + 0.5;
        if (w <= 0) return;
        var end = _now > _deadline ? _now : _deadline;
        var span = (end - _issued).TotalSeconds;
        double X(DateTimeOffset t) => span <= 0 ? w : Math.Clamp((t - _issued).TotalSeconds / span, 0, 1) * w;

        var late = _now > _deadline;
        var deadlineX = X(_deadline);
        var nowX = X(_now);
        context.DrawLine(new Pen(Tokens.LineStrong, 2), new Point(0, mid), new Point(deadlineX, mid));
        context.DrawLine(new Pen(Tokens.TextMuted, 2), new Point(0, mid), new Point(Math.Min(nowX, deadlineX), mid));
        if (late)
        {
            context.DrawLine(new Pen(Tokens.Caution, 2), new Point(deadlineX, mid), new Point(nowX, mid));
            context.DrawLine(new Pen(Tokens.Caution, 1), new Point(deadlineX, 1), new Point(deadlineX, Bounds.Height - 1));
        }
        else context.DrawLine(new Pen(Tokens.TextMuted, 1), new Point(deadlineX - 0.5, 2), new Point(deadlineX - 0.5, Bounds.Height - 2));
        var r = Tokens.TimeLineNow / 2;
        var x = Math.Clamp(nowX, r, w - r);
        context.DrawEllipse(late ? Tokens.Caution : Tokens.Text, null, new Point(x, mid), r, r);
    }
}

/// <summary>
/// THE APP'S CHECKS ON A DELIVERY, AS MARKS (<c>U-ui-world</c>): one per check, in the order the sentence under them names them — a tick for
/// passed, a cross in the caution colour for failed. The marks say at a glance what the sentence says in words.
/// </summary>
sealed class OrgCheckMarks : Control
{
    IReadOnlyList<bool> _passed = [];

    public OrgCheckMarks()
    {
        Height = Tokens.CheckMark;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(IReadOnlyList<bool> passed)
    {
        if (passed.SequenceEqual(_passed)) return;
        _passed = [.. passed];
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_passed.Count == 0 ? 0 : _passed.Count * Tokens.CheckMark + (_passed.Count - 1) * Tokens.S2, Tokens.CheckMark);

    public override void Render(DrawingContext context)
    {
        var s = Tokens.CheckMark;
        for (var i = 0; i < _passed.Count; i++)
        {
            var x = i * (s + Tokens.S2);
            if (_passed[i])
            {
                var pen = new Pen(Tokens.Text, 1.6, lineCap: PenLineCap.Round);
                context.DrawLine(pen, new Point(x + 1.5, s * 0.55), new Point(x + s * 0.4, s - 2));
                context.DrawLine(pen, new Point(x + s * 0.4, s - 2), new Point(x + s - 1.5, 2));
            }
            else
            {
                var pen = new Pen(Tokens.Caution, 1.6, lineCap: PenLineCap.Round);
                context.DrawLine(pen, new Point(x + 2, 2), new Point(x + s - 2, s - 2));
                context.DrawLine(pen, new Point(x + s - 2, 2), new Point(x + 2, s - 2));
            }
        }
    }
}
