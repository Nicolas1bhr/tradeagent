using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

// Inside a control subclass the bare name `Theme` is StyledElement's property, never the design-token class.
using Tokens = TradeAgent.App.Theme;

namespace TradeAgent.App;

/// <summary>
/// HOW FAR THE SETUP HAS COME (<c>U-ui-world</c>): one thin line across the column, the steps done lit, a short tick for each step so the
/// line still says "there are this many, you are here" — the old rail's point, without seventeen dots and their bars. Drawn once per build.
/// </summary>
sealed class OnboardingProgress : Control
{
    readonly int _here, _total;

    public OnboardingProgress(int here, int total)
    {
        _here = here;
        _total = Math.Max(1, total);
        Height = Tokens.S2;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var mid = Bounds.Height / 2;
        var h = Tokens.SetupProgress;
        context.DrawRectangle(Tokens.Line, null, new Rect(0, mid - h / 2, w, h), h / 2, h / 2);
        // The step on screen counts as begun: the lit part runs to its tick.
        var done = w * (_here + 1) / _total;
        context.DrawRectangle(Tokens.Text, null, new Rect(0, mid - h / 2, done, h), h / 2, h / 2);
        var faint = new Pen(Tokens.LineStrong, 1);
        for (var i = 1; i < _total; i++)
        {
            var x = Math.Round(w * i / _total) + 0.5;
            if (x > done) context.DrawLine(faint, new Point(x, mid - 3), new Point(x, mid + 3));
        }
        context.DrawEllipse(Tokens.Text, null, new Point(Math.Clamp(done, 3, w - 3), mid), 3, 3);
    }
}
