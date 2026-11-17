using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

// Inside a control subclass the bare name `Theme` is StyledElement's property, never the design-token class.
using Tokens = TradeAgent.App.Theme;

namespace TradeAgent.App;

/// <summary>
/// ONE ENTRY'S PLACE ON THE ACTIVITY TIMELINE (<c>U-ui-world</c>): the line the day runs down, and a dot where this entry stands on it,
/// amber for a warning and the pane's light for the rest. Drawn, not laid out: one line and one dot, nothing at rest but the picture.
/// </summary>
sealed class TradingTimelineMark : Control
{
    readonly IBrush _dot;
    readonly bool _first;

    /// <param name="dot">The dot's tone.</param>
    /// <param name="first">The day's first entry: the line starts at its dot, not above it.</param>
    public TradingTimelineMark(IBrush dot, bool first)
    {
        _dot = dot;
        _first = first;
        Width = Tokens.TimelineMark;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Height <= 0) return;
        var x = Math.Round(size.Width / 2) + 0.5;
        var dotY = Tokens.TimelineDotTop + Tokens.TimelineDot / 2;
        var pen = new Pen(Tokens.Line, 1);
        context.DrawLine(pen, new Point(x, _first ? dotY : 0), new Point(x, size.Height));
        context.DrawEllipse(_dot, null, new Point(x, dotY), Tokens.TimelineDot / 2, Tokens.TimelineDot / 2);
    }
}

/// <summary>
/// THE COMPUTER'S PARTS AT ONE GLANCE (<c>U-ui-world</c>): a dot per part in its state's colour, in the order the rows below name them —
/// green ready, amber degraded or paused, red failed, the pane's grey for what nobody has read. It says nothing the rows do not; it is how
/// "is anything wrong" is answered before a row is read. Redrawn only when it is told a different set of states.
/// </summary>
sealed class TradingHealthStrip : Control
{
    IReadOnlyList<IBrush> _tones = [];

    public TradingHealthStrip()
    {
        Height = Tokens.StripDot;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }

    public void Set(IReadOnlyList<IBrush> tones)
    {
        if (tones.SequenceEqual(_tones, ReferenceEqualityComparer.Instance)) return;
        _tones = tones;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_tones.Count == 0 ? 0 : _tones.Count * Tokens.StripDot + (_tones.Count - 1) * Tokens.StripGap, Tokens.StripDot);

    public override void Render(DrawingContext context)
    {
        var r = Tokens.StripDot / 2;
        for (var i = 0; i < _tones.Count; i++)
            context.DrawEllipse(_tones[i], null, new Point(r + i * (Tokens.StripDot + Tokens.StripGap), r), r, r);
    }
}

/// <summary>
/// WHERE FILES ARE HANDED TO THE AI (<c>U-ui-world</c>): a large place on the pane edged by a dashed hairline — the one shape a desktop
/// already reads as "put it here". While a file is held over it, it lights: the pane's light under it, the dash at full strength. It is
/// still the one control that takes a drop; only how it is drawn changed.
/// </summary>
sealed class TradingDropTarget : Decorator, Avalonia.Rendering.ICustomHitTest
{
    bool _lit;

    public TradingDropTarget()
    {
        MinHeight = Tokens.DropMinHeight;
        Padding = new Thickness(Tokens.S6);
    }

    /// <summary>Hit everywhere inside the dash, not only on the words: a drop lands where the hand lets go.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Whether a file is being held over it.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            if (_lit == value) return;
            _lit = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size).Deflate(0.5);
        var radius = Tokens.RadiusLg.TopLeft;
        if (_lit) context.DrawRectangle(Tokens.BgHover, null, rect, radius, radius);
        var pen = new Pen(_lit ? Tokens.FocusLine : Tokens.LineStrong, 1, new DashStyle(Tokens.DropDash, 0));
        context.DrawRectangle(null, pen, rect, radius, radius);
    }
}
