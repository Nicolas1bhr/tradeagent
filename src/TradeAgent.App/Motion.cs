using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace TradeAgent.App;

/// <summary>
/// HOW THINGS MOVE (<c>U-ui-world</c>), in one place, the way <see cref="Theme"/> is how they look.
///
/// <para><b>Motion here says something or it is not here.</b> A sheet arrives from where the thing it is about stands; a figure rolls
/// to its new value because it changed; a line of words rises because it is new. Nothing loops for decoration, and nothing the owner
/// presses ever waits for an animation: a control is the control, pressable, from its first frame — only how it is painted moves.</para>
///
/// <para><b>And nothing here may end the program.</b> Every frame's work is caught: how a thing is painted for a third of a second is
/// never worth the owner's controls.</para>
/// </summary>
static class Motion
{
    /// <summary>The app's one curve: away at once, a long soft arrival. Everything that comes in comes in on it.</summary>
    public static readonly Easing Arrive = new SplineEasing(0.2, 0.7, 0.2, 1);

    /// <summary>How long a sheet takes to arrive, a line of words to rise, and how far apart the lines of one arrival start.</summary>
    public static readonly TimeSpan SheetIn = TimeSpan.FromMilliseconds(520), SheetOut = TimeSpan.FromMilliseconds(260),
        RiseIn = TimeSpan.FromMilliseconds(460), Stagger = TimeSpan.FromMilliseconds(38);

    /// <summary>
    /// Set by a host that draws frames itself and waits for none (the render rows): every motion is then at its end the moment it
    /// starts, so a frame taken at any instant is the settled picture.
    /// </summary>
    public static bool Still { get; set; }

    /// <summary>One motion in flight; <see cref="Stop"/> leaves it where it is.</summary>
    public sealed class Run
    {
        internal bool Stopped;
        public void Stop() => Stopped = true;
    }

    /// <summary>
    /// Calls <paramref name="frame"/> with 0…1 — already eased by <paramref name="easing"/> — once a frame for <paramref name="lasts"/>,
    /// then once with exactly 1, then <paramref name="done"/>. On a control that is not on screen, or while <see cref="Still"/>, it is
    /// called once with 1 at once: a thing nobody is looking at is simply where it ends.
    /// </summary>
    public static Run Play(Control on, TimeSpan lasts, Action<double> frame, Action? done = null, Easing? easing = null)
    {
        var run = new Run();
        easing ??= Arrive;
        if (Still || lasts <= TimeSpan.Zero || TopLevel.GetTopLevel(on) is not { } top)
        {
            Safely(() => frame(1));
            Safely(done);
            return run;
        }
        var started = Stopwatch.GetTimestamp();
        void Tick(TimeSpan _)
        {
            if (run.Stopped) return;
            var t = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / lasts.TotalMilliseconds, 0, 1);
            Safely(() => frame(t >= 1 ? 1 : easing.Ease(t)));
            if (t >= 1) { Safely(done); return; }
            top.RequestAnimationFrame(Tick);
        }
        Safely(() => frame(0));
        top.RequestAnimationFrame(Tick);
        return run;
    }

    static void Safely(Action? work)
    {
        try { work?.Invoke(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
    }

    /// <summary>
    /// A control comes up into its place as it clears: <paramref name="from"/> pixels below it, after <paramref name="delay"/>. It is
    /// laid out and pressable where it belongs the whole time; only its painting moves.
    /// </summary>
    public static void Rise(Control c, double from = 14, TimeSpan delay = default, TimeSpan? lasts = null)
    {
        if (Still) { c.Opacity = 1; c.RenderTransform = null; return; }
        var settled = c.Opacity <= 0 ? 1 : c.Opacity;
        c.Transitions = null;
        c.Opacity = 0;
        c.RenderTransform = TransformOperations.Parse($"translateY({from.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
        var time = lasts ?? RiseIn;
        DispatcherTimer.RunOnce(() =>
        {
            Safely(() =>
            {
                c.Transitions =
                [
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = time, Easing = Arrive },
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = time, Easing = Arrive }
                ];
                c.Opacity = settled;
                c.RenderTransform = TransformOperations.Parse("translateY(0px)");
            });
        }, delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, DispatcherPriority.Render);
    }

    /// <summary>The children of a column rise one after another, the first at once: a page of words arrives the way it is read, from the top.</summary>
    public static void RiseEach(IEnumerable<Control> children, double from = 14, int most = 14)
    {
        var nth = 0;
        foreach (var child in children)
        {
            if (!child.IsVisible) continue;
            // Past the first screenful nothing is seen arriving: the rest is simply there.
            if (nth >= most) { child.Opacity = 1; child.RenderTransform = null; continue; }
            Rise(child, from, Stagger * nth);
            nth++;
        }
    }

    /// <summary>A property that eases to whatever it is set to, on the app's curve.</summary>
    public static void Eased(Control c, AvaloniaProperty<double> property, TimeSpan lasts) =>
        (c.Transitions ??= []).Add(new DoubleTransition { Property = property, Duration = lasts, Easing = Arrive });
}
