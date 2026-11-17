using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

namespace TradeAgent.App;

/// <summary>
/// THE STAGE (<c>U-ui-alive</c>): the control the floor is drawn in. It owns the <see cref="FloorScene"/>, moves it on once a frame while
/// anything on it is moving, and hands each frame to the painter.
///
/// <para><b>It draws only while there is something to draw.</b> While a light is on its way or something is travelling, every frame;
/// while the floor is only breathing — a turn open, the camera drifting — a slow one; and when nothing moves, none at all, until the page
/// wakes it. This app runs for days on a machine somebody else is using and on a server nobody is looking at; a picture that redrew itself
/// sixty times a second to show nothing changing would be paying for an audience that is not there.</para>
///
/// <para><b>And it measures itself.</b> If a frame takes too long to paint — a machine drawing without a graphics card, a remote
/// screen — it stops the camera's drift and the costliest light and falls back to a slow frame (<see cref="FloorMotion.Calm"/>). What the
/// floor says does not change; only how much it moves while saying it.</para>
/// </summary>
sealed class FloorStage : Control
{
    public FloorScene Scene { get; } = new();

    /// <summary>The part of the stage not under the words laid over it, for a stage of this size: left, top, right, bottom.</summary>
    public Func<Size, (float L, float T, float R, float B)> Clear { get; set; } =
        size => (32, 32, (float)size.Width - 32, (float)size.Height - 32);

    /// <summary>The panes of glass the window lays over the stage this instant (<see cref="FloorFrame.Pane"/>): asked for every frame.</summary>
    public Func<FloorFrame.Pane[]>? Glass { get; set; }

    Vector2? _pointerOnGlass;

    /// <summary>
    /// The pointer moved over the window's glass (stage coordinates), or left it (null): the pane under it is lit a little round it.
    /// Only where the frames are free — on a machine that draws without a graphics card the glass is not lit, and nothing is redrawn.
    /// </summary>
    public void PointerOnGlass(Vector2? at)
    {
        if (Scene.Motion != FloorMotion.Full) at = null;
        if (at == _pointerOnGlass) return;
        _pointerOnGlass = at;
        Wake();
    }

    /// <summary>The word beside the owner's place.</summary>
    public string OwnerWord { get; set; } = "";

    /// <summary>A frame was made: where everything is on the stage this instant. The page places its own words by it.</summary>
    public event Action<FloorFrame>? Framed;

    /// <summary>The pointer came onto a cell or left one (null).</summary>
    public event Action<string?>? Pointed;

    /// <summary>A cell was pressed, or the floor beside every cell (null).</summary>
    public event Action<string?>? Chosen;

    /// <summary>
    /// THE FLOOR COULD NOT BE DRAWN, and why. Raised once, on the UI thread; from then on the stage draws nothing. A picture that will
    /// not paint must never be what ends the program: the pages beside it are the owner's controls, and they go on working.
    /// </summary>
    public event Action<string>? Failed;

    int _failed;

    void Fail(Exception ex)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        var why = ex.GetBaseException().Message;
        Dispatcher.UIThread.Post(() => Failed?.Invoke(why));
    }

    FloorFrame? _frame;
    bool _shown, _pumping, _dirty = true;

    /// <summary>The frame last made, and how many have been: for the rows that hold the stage to drawing only while something moves.</summary>
    internal FloorFrame? Last => _frame;
    internal int Frames { get; private set; }
    long _lastTick, _lastFrame;
    string? _over;

    // What painting costs, as the render thread reports it: a running mean in ticks, and how many frames it has seen.
    long _paintTicks;
    int _painted;
    bool _measured;

    /// <summary>
    /// The floor is behind a sheet: something is being read in front of it. It moves at half its pace and breathes slowly — it is
    /// still alive, and it is not what the frames are for.
    /// </summary>
    public bool Behind { get; set; }

    /// <summary>The least time between two frames: a sixtieth of a second, or a thirtieth behind a sheet.</summary>
    TimeSpan Pace => Behind ? TimeSpan.FromMilliseconds(32) : TimeSpan.FromMilliseconds(15.5);

    static readonly TimeSpan BehindBreath = TimeSpan.FromMilliseconds(100);

    static readonly TimeSpan Breath = TimeSpan.FromMilliseconds(40), CalmBreath = TimeSpan.FromMilliseconds(125), CalmStep = TimeSpan.FromMilliseconds(66);

    /// <summary>A frame that takes longer than this to paint is one this machine cannot afford sixty of.</summary>
    const double TooSlowMs = 8;

    /// <summary>Set by the painter the first time it finds it is drawing without a graphics card.</summary>
    int _software;

    public FloorStage()
    {
        ClipToBounds = true;
        FloorPaint.Warm();
    }

    /// <summary>The page is on screen or it is not: off screen the stage does nothing at all.</summary>
    public void Show(bool shown)
    {
        _shown = shown;
        if (shown) Wake();
    }

    /// <summary>Something changed that a frame should show: one is drawn, whatever else is or is not moving.</summary>
    public void Wake()
    {
        _dirty = true;
        Pump();
    }

    /// <summary>
    /// The scene may have been told something new: the stage looks, and draws only if it was. For the page's every look at the
    /// ledgers — most of which find nothing changed, and must then cost no frame.
    /// </summary>
    public void Nudge() => Pump();

    /// <summary>
    /// Moves the floor on by <paramref name="seconds"/> and draws it, now, whatever the clock says. For a host that keeps no frame
    /// clock of its own — the render rows, which step the floor and look at each step.
    /// </summary>
    internal void Step(float seconds)
    {
        Scene.Advance(seconds);
        Draw(Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Set by a host that steps the floor itself (<see cref="Step"/>): the stage then keeps no frame clock of its own, so the floor
    /// moves exactly as far as that host steps it and no further.
    /// </summary>
    internal bool Stepped { get; set; }

    void Pump()
    {
        if (_pumping || !_shown || !IsEffectivelyVisible || Stepped) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _pumping = true;
        top.RequestAnimationFrame(Tick);
    }

    void Tick(TimeSpan _)
    {
        _pumping = false;
        if (!_shown || !IsEffectivelyVisible || Volatile.Read(ref _failed) != 0) { _lastTick = 0; return; }
        try { Beat(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { Fail(ex); }
    }

    void Beat()
    {

        var now = Stopwatch.GetTimestamp();
        var dt = _lastTick == 0 ? 1 / 60f : (float)Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;

        Settle();
        Scene.Advance(dt);

        var breath = Scene.Motion == FloorMotion.Full ? Breath : CalmBreath;
        // NEVER FASTER THAN THE EYE NEEDS: a screen that refreshes 120 times a second asks for 120 frames, and the floor's motion is
        // slow — sixty is all of it, and behind a sheet, where it is the background to something being read, thirty.
        var since = Stopwatch.GetElapsedTime(_lastFrame, now);
        var due = (_dirty && since >= Pace) || (Scene.Busy && Scene.Motion != FloorMotion.Still && since >= Pace)
                  || (Scene.Breathing && Scene.Motion != FloorMotion.Still && since >= (Behind ? BehindBreath : breath));
        if (due) Draw(now);

        // Every frame while something is on its way; a slow frame while the floor only breathes; nothing while nothing moves. A calm
        // floor — a machine that draws without a graphics card — moves what is on its way at a pace it can afford, never every frame.
        // A frame asked for sooner than the pace allows is drawn at the next beat, never dropped.
        if (_dirty) { Pump(); return; }
        if (Scene.Motion == FloorMotion.Still) { _lastTick = 0; return; }
        if (Scene.Busy && Scene.Motion == FloorMotion.Full) Pump();
        else if (Scene.Busy) DispatcherTimer.RunOnce(Pump, CalmStep, DispatcherPriority.Background);
        else if (Scene.Breathing) DispatcherTimer.RunOnce(Pump, breath, DispatcherPriority.Background);
        else _lastTick = 0;
    }

    void Draw(long now)
    {
        _dirty = false;
        _lastFrame = now;
        var size = Bounds.Size;
        if (size.Width < 4 || size.Height < 4) return;
        _frame = Scene.Frame((float)size.Width, (float)size.Height, Clear(size), OwnerWord);
        if (Glass is { } glass) _frame.Glass = glass();
        _frame.Pointer = _pointerOnGlass;
        Frames++;
        Framed?.Invoke(_frame);
        InvalidateVisual();
    }

    /// <summary>Reads what painting has cost and, the first time it proves too slow, calms the floor. Never the other way: a machine that was too slow a minute ago is not asked again.</summary>
    void Settle()
    {
        // WITHOUT A GRAPHICS CARD EVERY PIXEL OF EVERY FRAME IS THIS PROCESSOR'S: calm at once, without waiting to measure how slow.
        // Measuring the painter alone misses most of it anyway — the frame is also composed and carried to the screen.
        if (Volatile.Read(ref _software) != 0 && Scene.Motion == FloorMotion.Full) Scene.Motion = FloorMotion.Calm;
        if (_measured || Volatile.Read(ref _painted) < 45) return;
        _measured = true;
        var ms = Stopwatch.GetElapsedTime(0, Volatile.Read(ref _paintTicks)).TotalMilliseconds;
        if (ms > TooSlowMs && Scene.Motion == FloorMotion.Full) Scene.Motion = FloorMotion.Calm;
    }

    void Painted(long ticks)
    {
        // A running mean, weighted to the recent: the first frames after a start are never the typical ones.
        var mean = Volatile.Read(ref _paintTicks);
        Volatile.Write(ref _paintTicks, mean == 0 ? ticks : (mean * 7 + ticks) / 8);
        Interlocked.Increment(ref _painted);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wake();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == IsVisibleProperty) Wake();
    }

    public override void Render(DrawingContext context)
    {
        // Something to hit, so the pointer reaches the stage wherever on it it is.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_frame is { } frame && Volatile.Read(ref _failed) == 0) context.Custom(new Paint(new Rect(Bounds.Size), frame, this));
    }

    // ---- the pointer ------------------------------------------------------------------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        var size = Bounds.Size;
        if (size.Width > 0 && size.Height > 0)
            Scene.Lean((float)(at.X / size.Width * 2 - 1), (float)(at.Y / size.Height * 2 - 1));
        var over = Under(at);
        if (over != _over)
        {
            _over = over;
            Cursor = over is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            Pointed?.Invoke(over);
        }
        Pump();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Scene.Lean(0, 0);
        if (_over is not null)
        {
            _over = null;
            Pointed?.Invoke(null);
        }
        Pump();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Chosen?.Invoke(Under(e.GetPosition(this)));
    }

    /// <summary>The cell under a point of the stage: the nearest one whose drawn shape holds it.</summary>
    string? Under(Point at)
    {
        if (_frame is not { } frame) return null;
        var p = new Vector2((float)at.X, (float)at.Y);
        // Cells are kept furthest first, so the nearest is the last that holds the point.
        for (var i = frame.Cells.Length - 1; i >= 0; i--)
        {
            var cell = frame.Cells[i];
            if (cell.Kind == FloorCellKind.Vacant ? Inside(cell.Foot, p) : Inside(Hull(cell), p)) return cell.Id;
        }
        return null;
    }

    /// <summary>A cell's outline on the screen: round its foot at the front and its top at the back.</summary>
    static Vector2[] Hull(in FloorFrame.Cell cell) =>
        [cell.Foot[0], cell.Foot[1], cell.Foot[2], cell.Top[2], cell.Top[3], cell.Top[0]];

    static bool Inside(Vector2[] polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>One frame, painted on the render thread through Skia. Where there is no Skia under the window (a host that draws nothing), nothing is drawn.</summary>
    sealed class Paint(Rect bounds, FloorFrame frame, FloorStage stage) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => other is Paint that && ReferenceEquals(that.Frame, frame);

        FloorFrame Frame => frame;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia) return;
            using var lease = skia.Lease();
            if (lease.GrContext is null) Volatile.Write(ref stage._software, 1);
            var canvas = lease.SkCanvas;
            var started = Stopwatch.GetTimestamp();
            // Whatever the painter had saved is put back even if it throws half way: the canvas is the whole window's, not the floor's.
            var saved = canvas.Save();
            try { FloorPaint.Paint(canvas, frame); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { stage.Fail(ex); }
            finally { canvas.RestoreToCount(saved); }
            stage.Painted(Stopwatch.GetTimestamp() - started);
        }
    }
}
