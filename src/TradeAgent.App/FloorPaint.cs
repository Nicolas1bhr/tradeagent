using System.Numerics;
using Avalonia.Platform;
using SkiaSharp;

namespace TradeAgent.App;

/// <summary>
/// THE FLOOR, PAINTED (<c>U-ui-alive</c>). One frame in, one picture out: slabs, the lines laid in them, their names written on them, and
/// on them the cells — glass, each with a light in it that is as bright as its seat is busy.
///
/// <para><b>The light is the only colour.</b> Everything that stands is the dark of the app's own surfaces; what is lit is lit in the
/// accent, which in this app means the AI and nothing else. Amber and red appear only where the theme lets them — something to look at,
/// something that failed — and green not at all, because nothing on this floor is a profit.</para>
///
/// <para>How it is lit: light adds. A cell's glow is painted onto whatever is already there with addition, the way light falls on a
/// surface, so two lit cells side by side are brighter between them and a dark one stands in its neighbour's light. That, and one
/// consistent direction for what little shading there is, is the whole of the trick.</para>
///
/// <para>Runs on the render thread, over a frame nobody changes. It keeps nothing between frames but its typefaces.</para>
/// </summary>
static class FloorPaint
{
    // The app's surfaces (Theme), as the painter's own colours: the floor is made of the same dark the pages are.
    static readonly SKColor Bg = new(0x0B, 0x0D, 0x12);
    static readonly SKColor Faint = new(0x6F, 0x79, 0x8B), Muted = new(0x9B, 0xA4, 0xB3);

    // The light, by what it is saying: its core, its body and the colour it fades out through.
    static readonly SKColor[] Work = [new(0xFF, 0xFF, 0xFF), new(0xC6, 0xCE, 0xFF), new(0x7C, 0x8C, 0xF8)];
    static readonly SKColor[] Caution = [new(0xFF, 0xF4, 0xE2), new(0xF2, 0xC2, 0x85), new(0xE0, 0xA4, 0x58)];
    static readonly SKColor[] Failure = [new(0xFF, 0xEC, 0xEC), new(0xF2, 0x8C, 0x90), new(0xE5, 0x48, 0x4D)];

    static SKTypeface? _medium, _semi;

    /// <summary>Loads the app's type for the floor's own words. Called once, on the UI thread, before the first frame is painted.</summary>
    public static void Warm()
    {
        _medium ??= Face("Inter-Medium.ttf");
        _semi ??= Face("Inter-SemiBold.ttf");
    }

    static SKTypeface? Face(string file)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/" + file));
            return SKTypeface.FromStream(stream);
        }
        // Without the face the floor's words fall back to the system's: the floor is still drawn.
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }

    static SKColor[] Light(FloorTone tone) => tone switch { FloorTone.Caution => Caution, FloorTone.Failure => Failure, _ => Work };

    static SKColor A(SKColor c, float alpha) => c.WithAlpha((byte)Math.Clamp(alpha * 255f, 0, 255));

    static SKColor Mix(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
    }

    static SKPoint P(Vector2 v) => new(v.X, v.Y);

    static SKPath Poly(ReadOnlySpan<Vector2> points, bool close = true)
    {
        var path = new SKPath();
        if (points.Length == 0) return path;
        path.MoveTo(points[0].X, points[0].Y);
        for (var i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
        if (close) path.Close();
        return path;
    }

    public static void Paint(SKCanvas canvas, FloorFrame f)
    {
        var full = f.Motion == FloorMotion.Full;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, f.Width, f.Height));
        canvas.Clear(Bg);
        // Without a graphics card no pane shows the floor a second time, so the floor is simply drawn, once, with nothing kept of it.
        if (f.Glass.Length == 0 || !full)
        {
            Scene(canvas, f, full);
            foreach (var pane in f.Glass) Glass(canvas, f, null, pane);
        }
        else
        {
            // THE FLOOR IS WORKED OUT ONCE AND SHOWN TWICE: as it is, and — small, a sixth of its size, and out of focus — as what
            // every pane of glass shows of it. One small picture for all the panes: a pane costs the drawing of an image, never the
            // floor again, so the glass is as cheap on a frame with four panes as on a frame with one.
            using var recorder = new SKPictureRecorder();
            Scene(recorder.BeginRecording(new SKRect(0, 0, f.Width, f.Height)), f, full);
            using var floor = recorder.EndRecording();
            canvas.DrawPicture(floor);
            using var behind = Behind(floor, f.Width, f.Height);
            foreach (var pane in f.Glass) Glass(canvas, f, behind, pane);
        }
        canvas.Restore();
    }

    /// <summary>How much smaller than the floor the picture behind the glass is kept. Out of focus, nothing finer than this is seen.</summary>
    const float BehindScale = 1 / 6f;

    // The small surface the floor is drawn on for the glass, kept between frames: the painter's own, one per thread that paints.
    [ThreadStatic] static SKSurface? _behind;
    [ThreadStatic] static int _behindWide, _behindHigh;

    /// <summary>The floor, a sixth of its size and out of focus: what is seen through every pane of this frame.</summary>
    static SKImage Behind(SKPicture floor, float width, float height)
    {
        int wide = Math.Max(8, (int)MathF.Ceiling(width * BehindScale)), high = Math.Max(8, (int)MathF.Ceiling(height * BehindScale));
        if (_behind is null || wide != _behindWide || high != _behindHigh)
        {
            _behind?.Dispose();
            _behind = SKSurface.Create(new SKImageInfo(wide, high, SKColorType.Rgba8888, SKAlphaType.Premul));
            _behindWide = wide;
            _behindHigh = high;
        }
        var small = _behind.Canvas;
        small.Clear(Bg);
        // Out of focus, and its colour deepened here, where it is small: what is lit behind glass glows through it.
        using var blur = SKImageFilter.CreateBlur(GlassBlur * BehindScale, GlassBlur * BehindScale);
        using var deepen = SKColorFilter.CreateColorMatrix(Saturate(1.75f, 1.18f));
        using var soft = new SKPaint { ImageFilter = blur, ColorFilter = deepen };
        small.SaveLayer(soft);
        small.Scale(BehindScale);
        small.DrawPicture(floor);
        small.Restore();
        return _behind.Snapshot();
    }

    static void Scene(SKCanvas canvas, FloorFrame f, bool full)
    {
        var awake = 1 - f.Asleep;
        Pool(canvas, f, awake);
        foreach (var slab in f.Slabs) Slab(canvas, slab, full);
        Lines(canvas, f);
        foreach (var name in f.Names) Name(canvas, name);
        Owner(canvas, f);
        if (f.Tape is { } tape) Tape(canvas, tape, full);

        // Light on the floor first, so every cell stands in it.
        foreach (var cell in f.Cells) Glow(canvas, cell, f);
        foreach (var ring in f.Rings) Ring(canvas, ring);
        foreach (var flight in f.Flights)
            if (!flight.Air) Flight(canvas, flight);
        foreach (var cell in f.Cells) Cell(canvas, cell, f, full);
        foreach (var cell in f.Cells) Bloom(canvas, cell, f, full);
        foreach (var flight in f.Flights)
            if (flight.Air) Flight(canvas, flight);
        foreach (var spark in f.Sparks) Spark(canvas, spark);
        foreach (var cell in f.Cells) Marks(canvas, cell);
        foreach (var post in f.Posts) Post(canvas, post);
        foreach (var name in f.PostNames) Name(canvas, name, Muted, 5.5f);
        foreach (var column in f.Columns) Column(canvas, column);
        foreach (var figure in f.Figures) Name(canvas, figure, SKColors.White, 6);
        foreach (var note in f.FigureNotes) Name(canvas, note, Muted, 5.5f);
        foreach (var thread in f.Threads) Thread(canvas, thread);
    }

    /// <summary>
    /// A PILLAR UNDER THE FLOOR (the machine lens): one part of what the floor stands on — a face of faint glass, lighter at its head,
    /// and at its head a light in the colour of how the part stands: green ready, amber asking to be looked at, red not working, the
    /// AI's indigo while it starts, grey where nobody has looked yet. The same five colours every page uses for the same five things.
    /// </summary>
    static void Post(SKCanvas canvas, FloorFrame.Post post)
    {
        if (post.Strength <= 0.01f) return;
        using var face = Poly(post.Face);
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(P((post.Face[2] + post.Face[3]) / 2), P((post.Face[0] + post.Face[1]) / 2),
            [A(SKColors.White, 0.13f * post.Strength), A(SKColors.White, 0.015f * post.Strength)], null, SKShaderTileMode.Clamp);
        canvas.DrawPath(face, paint);
        paint.Shader.Dispose();
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = A(SKColors.White, 0.2f * post.Strength);
        canvas.DrawPath(face, paint);

        var tone = post.State switch
        {
            FloorPartState.Ready => new SKColor(0x3F, 0xB6, 0x8B),
            FloorPartState.Attention => new SKColor(0xE0, 0xA4, 0x58),
            FloorPartState.Failed => new SKColor(0xE5, 0x48, 0x4D),
            FloorPartState.Starting => new SKColor(0x7C, 0x8C, 0xF8),
            _ => new SKColor(0x6A, 0x73, 0x82)
        };
        paint.Style = SKPaintStyle.Fill;
        // A part that asks to be looked at is the brighter light: ready is calm, and nobody having looked is nearly dark.
        var loud = post.State is FloorPartState.Attention or FloorPartState.Failed ? 1f : post.State == FloorPartState.Unknown ? 0.45f : 0.75f;
        paint.Color = A(tone, 0.3f * loud * post.Strength);
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5);
        canvas.DrawCircle(P(post.Light), 8, paint);
        paint.MaskFilter.Dispose();
        paint.MaskFilter = null;
        paint.Color = A(tone, Math.Min(1, 0.6f + 0.4f * loud) * post.Strength);
        canvas.DrawCircle(P(post.Light), 3.2f, paint);
    }

    /// <summary>
    /// A UNIT'S COLUMN (the money lens): a box of glass standing on the unit's slab, as tall as what the unit spent today — its top
    /// and the two sides that face the camera barely there, its edges a line of light. Where what the unit's turns cost is not known,
    /// the column is an outline in dashes and nothing else: never a column of no height, which would say "nothing".
    /// </summary>
    static void Column(SKCanvas canvas, FloorFrame.Column c)
    {
        if (c.Strength <= 0.01f) return;
        using var top = Poly(c.Top);
        using var front = Poly([c.Foot[0], c.Foot[1], c.Top[1], c.Top[0]]);
        using var side = Poly([c.Foot[1], c.Foot[2], c.Top[2], c.Top[1]]);
        using var paint = new SKPaint { IsAntialias = true };
        if (!c.Unknown)
        {
            // Each face is lightest along its top edge and all but gone at its foot: glass seen edge on.
            void Pane(SKPath face, Vector2 high, Vector2 low, float strength)
            {
                paint.Shader = SKShader.CreateLinearGradient(P(high), P(low), [A(SKColors.White, strength * c.Strength), A(SKColors.White, strength * 0.12f * c.Strength)], null,
                    SKShaderTileMode.Clamp);
                canvas.DrawPath(face, paint);
                paint.Shader.Dispose();
                paint.Shader = null;
            }
            Pane(front, (c.Top[0] + c.Top[1]) / 2, (c.Foot[0] + c.Foot[1]) / 2, 0.12f);
            Pane(side, (c.Top[1] + c.Top[2]) / 2, (c.Foot[1] + c.Foot[2]) / 2, 0.075f);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = A(SKColors.White, 0.06f * c.Strength);
            canvas.DrawPath(top, paint);
        }
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        using var dash = c.Unknown ? SKPathEffect.CreateDash([5, 5], 0) : null;
        paint.PathEffect = dash;
        paint.Color = A(SKColors.White, (c.Unknown ? 0.34f : 0.5f) * c.Strength);
        canvas.DrawPath(top, paint);
        paint.Color = A(SKColors.White, (c.Unknown ? 0.2f : 0.26f) * c.Strength);
        for (var i = 0; i < 3; i++) canvas.DrawLine(P(c.Foot[i]), P(c.Top[i]), paint);
    }

    /// <summary>
    /// AN OPEN TASK (the work lens): a thread over the floor from the seat that gave it to the seat that holds it, and on it a bead as
    /// far along as the task's time is. A task waiting on its giver — delivered, not judged — wears a ring round its bead.
    /// </summary>
    static void Thread(SKCanvas canvas, in FloorFrame.Thread t)
    {
        if (t.Strength <= 0.01f || t.Points.Length < 2) return;
        var light = Light(t.Tone);
        using var path = Poly(t.Points, close: false);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        paint.StrokeWidth = 3.4f;
        paint.Color = A(light[2], 0.16f * t.Strength);
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
        canvas.DrawPath(path, paint);
        paint.MaskFilter?.Dispose();
        paint.MaskFilter = null;
        paint.StrokeWidth = 1.1f;
        paint.Color = A(light[1], 0.62f * t.Strength);
        canvas.DrawPath(path, paint);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = A(light[2], 0.3f * t.Strength);
        canvas.DrawCircle(P(t.Bead), 7, paint);
        paint.Color = A(light[0], t.Strength);
        canvas.DrawCircle(P(t.Bead), 3, paint);
        if (!t.Waiting) return;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.2f;
        paint.Color = A(light[0], 0.8f * t.Strength);
        canvas.DrawCircle(P(t.Bead), 9, paint);
    }

    /// <summary>
    /// A PANE OF GLASS OVER THE FLOOR (<c>U-ui-world</c>): everything the window lays over the floor — a sheet, the dock, the status —
    /// is glass, and the glass is drawn here, by the floor's own painter, because glass is made of what is behind it.
    ///
    /// <para>Through the body of a pane the floor is seen out of focus with its colour deepened, so a cell that is lit behind the glass
    /// glows through it and the words on the pane are still read on something calm. Along the rim the glass is thicker, and bends what
    /// is behind it: the floor is drawn there a little larger than it is, as an edge of real glass shows it. The rim itself catches the
    /// light — brightest where it faces up and to the left — and the pane lays a soft shadow on the floor under it. Where the pointer
    /// is over a pane, the pane is a little lighter round it.</para>
    ///
    /// <para>On a machine that draws without a graphics card the floor is not drawn again: the pane is the window's own dark, nearly
    /// whole, with its rim.</para>
    /// </summary>
    static void Glass(SKCanvas canvas, FloorFrame f, SKImage? behind, in FloorFrame.Pane pane)
    {
        var full = behind is not null;
        var strength = Math.Clamp(pane.Strength, 0, 1);
        if (strength <= 0.01f || pane.Width < 2 || pane.Height < 2) return;
        var rect = new SKRect(pane.X, pane.Y, pane.X + pane.Width, pane.Y + pane.Height);
        var radius = Math.Min(pane.Radius, Math.Min(pane.Width, pane.Height) / 2);
        using var round = new SKRoundRect(rect, radius, radius);

        // ITS SHADOW ON THE FLOOR: under it and a little below, soft.
        if (full)
        {
            using var shade = new SKPaint { IsAntialias = true, Color = A(SKColors.Black, 0.42f * strength), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
            canvas.Save();
            canvas.ClipRoundRect(round, SKClipOperation.Difference, true);
            using var cast = new SKRoundRect(SKRect.Create(rect.Left + 2, rect.Top + 10, rect.Width - 4, rect.Height), radius, radius);
            canvas.DrawRoundRect(cast, shade);
            canvas.Restore();
        }

        canvas.Save();
        canvas.ClipRoundRect(round, SKClipOperation.Intersect, true);
        if (full)
        {
            // THE BODY: the floor out of focus, its colour deepened.
            var whole = new SKRect(0, 0, f.Width, f.Height);
            using var body = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * strength)) };
            canvas.DrawImage(behind, whole, Smooth, body);

            // THE RIM, WHERE THE GLASS IS THICK: what is behind it again, larger than it is, in a band along the edge — so a light
            // behind the pane breaks at the rim the way it does at the edge of real glass.
            var band = Math.Min(GlassRim, Math.Min(pane.Width, pane.Height) / 2 - 1);
            if (band > 3)
            {
                var inner = rect;
                inner.Inflate(-band, -band);
                using var innerRound = new SKRoundRect(inner, Math.Max(0, radius - band), Math.Max(0, radius - band));
                canvas.Save();
                canvas.ClipRoundRect(innerRound, SKClipOperation.Difference, true);
                canvas.Scale(GlassLens, GlassLens, rect.MidX, rect.MidY);
                canvas.DrawImage(behind, whole, Smooth, body);
                canvas.Restore();
            }
        }

        // THE GLASS'S OWN BODY: the window's dark, thin enough that light behind it comes through, and a breath of light from above.
        using var veil = new SKPaint { IsAntialias = true };
        var dark = full ? 0.46f : 0.96f;
        veil.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Bottom),
            [A(new SKColor(0x1A, 0x1E, 0x2C), dark * strength), A(new SKColor(0x0B, 0x0D, 0x12), Math.Min(1, dark + 0.16f) * strength)], [0, 1],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(rect, veil);
        veil.Shader.Dispose();
        veil.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Top + Math.Min(rect.Height, 220)),
            [A(SKColors.White, 0.075f * strength), A(SKColors.White, 0.018f * strength)], [0, 1], SKShaderTileMode.Clamp);
        canvas.DrawRect(rect, veil);
        veil.Shader.Dispose();
        veil.Shader = null;

        // WHERE THE POINTER IS, the glass is lit a little: light on a surface, moving as the hand moves.
        if (full && f.Pointer is { } at && rect.Contains(at.X, at.Y))
        {
            veil.Shader = SKShader.CreateRadialGradient(new SKPoint(at.X, at.Y), GlassPointer,
                [A(SKColors.White, 0.07f * strength), A(SKColors.White, 0)], [0, 1], SKShaderTileMode.Clamp);
            canvas.DrawRect(rect, veil);
            veil.Shader.Dispose();
            veil.Shader = null;
        }

        // INSIDE THE RIM: a soft line of light just under the top edge, the way a thick edge holds it.
        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.2f) };
        glow.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Top + Math.Min(rect.Height, 90)),
            [A(SKColors.White, 0.26f * strength), A(SKColors.White, 0)], [0, 1], SKShaderTileMode.Clamp);
        var within = rect;
        within.Inflate(-1.6f, -1.6f);
        using var withinRound = new SKRoundRect(within, Math.Max(0, radius - 1.6f), Math.Max(0, radius - 1.6f));
        canvas.DrawRoundRect(withinRound, glow);
        glow.Shader.Dispose();
        canvas.Restore();

        // THE RIM ITSELF: a hairline that is bright where it faces the light — up and to the left — and faint where it turns away.
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        edge.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
            [A(SKColors.White, 0.46f * strength), A(SKColors.White, 0.10f * strength), A(SKColors.White, 0.05f * strength), A(SKColors.White, 0.20f * strength)],
            [0, 0.3f, 0.7f, 1], SKShaderTileMode.Clamp);
        var inset = rect;
        inset.Inflate(-0.5f, -0.5f);
        using var line = new SKRoundRect(inset, Math.Max(0, radius - 0.5f), Math.Max(0, radius - 0.5f));
        canvas.DrawRoundRect(line, edge);
        edge.Shader.Dispose();
    }

    /// <summary>How the small picture behind the glass is drawn at the floor's size: smoothly, so what was out of focus small is out of focus large.</summary>
    static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear);

    /// <summary>How far out of focus the floor is behind a pane, how wide the band along a pane's rim is, how much larger the floor is drawn in it, and how far the light round the pointer reaches.</summary>
    const float GlassBlur = 24, GlassRim = 13, GlassLens = 1.09f, GlassPointer = 260;

    /// <summary>A colour matrix that deepens colour by <paramref name="s"/> and lifts it by <paramref name="gain"/>: what is lit behind glass glows through it.</summary>
    static float[] Saturate(float s, float gain)
    {
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        float a = (1 - s) * r, c = (1 - s) * g, d = (1 - s) * b;
        return
        [
            (a + s) * gain, c * gain, d * gain, 0, 0,
            a * gain, (c + s) * gain, d * gain, 0, 0,
            a * gain, c * gain, (d + s) * gain, 0, 0,
            0, 0, 0, 1, 0
        ];
    }

    /// <summary>The floor itself: nothing but a pool of faint light under the organisation, so it stands on something and not in a void.</summary>
    static void Pool(SKCanvas canvas, FloorFrame f, float awake)
    {
        if (f.Slabs.Length == 0) return;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var slab in f.Slabs)
            foreach (var p in slab.Top)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        var centre = new SKPoint((minX + maxX) / 2, (minY + maxY) / 2 + (maxY - minY) * 0.12f);
        var radius = Math.Max(maxX - minX, (maxY - minY) * 1.8f) * 0.72f;
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateRadialGradient(centre, radius,
            [A(new SKColor(0x1B, 0x21, 0x3A), 0.5f + 0.25f * awake), A(new SKColor(0x12, 0x16, 0x26), 0.32f), A(Bg, 0)], [0, 0.5f, 1],
            SKShaderTileMode.Clamp, SKMatrix.CreateScale(1, 0.62f, centre.X, centre.Y));
        canvas.DrawRect(0, 0, f.Width, f.Height, paint);
        paint.Shader?.Dispose();
    }

    static void Slab(SKCanvas canvas, FloorFrame.Slab slab, bool full)
    {
        if (slab.Born <= 0.01f) return;
        var level = Math.Min(slab.Level, 3);
        var lift = level * 0.035f;
        // The board's slab is the colder of the two metals: it is not part of the organisation, and should not look like a piece of it.
        var top = slab.Board ? new SKColor(0x17, 0x18, 0x21) : Mix(new SKColor(0x12, 0x15, 0x1E), new SKColor(0x27, 0x2E, 0x42), lift * 4.2f);
        var front = Mix(new SKColor(0x09, 0x0B, 0x10), new SKColor(0x14, 0x18, 0x23), lift * 4.2f);
        var side = Mix(new SKColor(0x06, 0x08, 0x0C), new SKColor(0x0E, 0x11, 0x19), lift * 4.2f);
        var quiet = slab.Quiet ? 0.6f : 1f;
        var alpha = slab.Born;

        using var paint = new SKPaint { IsAntialias = true };
        using var topPath = Poly(slab.Top);
        using var frontPath = Poly(slab.Front);
        using var sidePath = Poly(slab.Side);                    // empty for a strip seen end on

        if (full && slab.Level == 0)
        {
            // What it stands on darkens round it: the one soft shadow on the floor, and only under the two things that stand on the floor itself.
            using var soft = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16);
            using var shade = new SKPaint { IsAntialias = true, Color = A(SKColors.Black, 0.5f * alpha), MaskFilter = soft };
            canvas.Save();
            canvas.Translate(0, 10);
            canvas.DrawPath(frontPath, shade);
            canvas.DrawPath(sidePath, shade);
            canvas.Restore();
        }

        paint.Color = A(front, alpha);
        canvas.DrawPath(frontPath, paint);
        paint.Color = A(side, alpha);
        canvas.DrawPath(sidePath, paint);

        // The top catches a little more light toward the back, where the camera looks along it.
        paint.Shader = SKShader.CreateLinearGradient(P((slab.Top[0] + slab.Top[1]) / 2), P((slab.Top[2] + slab.Top[3]) / 2),
            [A(Mix(top, Bg, 1 - quiet), alpha), A(Mix(Mix(top, new SKColor(0x34, 0x3C, 0x55), 0.22f), Bg, 1 - quiet), alpha)], null, SKShaderTileMode.Clamp);
        canvas.DrawPath(topPath, paint);
        paint.Shader.Dispose();
        paint.Shader = null;

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        // The joints between its panels: barely there, and enough to give the surface a size.
        paint.Color = A(new SKColor(0x2C, 0x33, 0x47), 0.34f * alpha);
        for (var i = 0; i + 1 < slab.Seams.Length; i += 2) canvas.DrawLine(P(slab.Seams[i]), P(slab.Seams[i + 1]), paint);

        // Its edge, where the light catches: brighter along the two edges that face the camera.
        paint.Color = A(new SKColor(0x2A, 0x31, 0x42), 0.7f * alpha * quiet);
        canvas.DrawPath(topPath, paint);
        paint.Color = A(new SKColor(0x4A, 0x55, 0x70), 0.85f * alpha * quiet);
        canvas.DrawLine(P(slab.Top[0]), P(slab.Top[1]), paint);
        canvas.DrawLine(P(slab.Top[1]), P(slab.Top[2]), paint);

        if (slab.Rim > 0.01f)
        {
            paint.BlendMode = SKBlendMode.Plus;
            paint.StrokeWidth = 5;
            paint.Color = A(Work[2], 0.22f * slab.Rim);
            canvas.DrawPath(topPath, paint);
            paint.StrokeWidth = 1.4f;
            paint.Color = A(Work[1], 0.9f * slab.Rim);
            canvas.DrawPath(topPath, paint);
        }
    }

    static void Lines(SKCanvas canvas, FloorFrame f)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        foreach (var line in f.Lines)
        {
            using var path = Poly(line.Points, close: false);
            paint.BlendMode = SKBlendMode.SrcOver;
            paint.StrokeWidth = 1.15f;
            paint.Color = A(new SKColor(0x4A, 0x55, 0x72), 0.82f);
            canvas.DrawPath(path, paint);
            if (line.Warmth <= 0.01f) continue;
            // Something ran along it a moment ago and it has not yet cooled.
            paint.BlendMode = SKBlendMode.Plus;
            paint.StrokeWidth = 5;
            paint.Color = A(Work[2], 0.14f * line.Warmth);
            canvas.DrawPath(path, paint);
            paint.StrokeWidth = 1.3f;
            paint.Color = A(Work[1], 0.55f * line.Warmth);
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>A unit's name, lying on its slab: set in the floor's own two directions, so it turns and foreshortens with the floor.</summary>
    static void Name(SKCanvas canvas, FloorFrame.Name name) => Name(canvas, name, Faint, 7.5f);

    /// <summary>Words lying on the floor or on something standing on it, in <paramref name="ink"/>; left off where they would be smaller than <paramref name="least"/> pixels a letter.</summary>
    static void Name(SKCanvas canvas, FloorFrame.Name name, SKColor ink, float least)
    {
        if (name.Strength <= 0.02f || name.Text.Length == 0) return;
        using var font = new SKFont(_medium ?? SKTypeface.Default, 100) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
        var wide = font.MeasureText(name.Text);
        if (wide <= 0) return;
        // As large as its place allows and no larger than a name on this floor is; a name that would be too small to read is left off,
        // and the seat's own callout says where it is instead.
        var size = Math.Min(name.Size, name.Room * 100 / wide);
        var across = name.Along.Length();
        if (size * across < least) return;
        var k = size / 100f;
        var matrix = new SKMatrix
        {
            ScaleX = name.Along.X * k, SkewX = -name.Up.X * k, TransX = name.At.X,
            SkewY = name.Along.Y * k, ScaleY = -name.Up.Y * k, TransY = name.At.Y,
            Persp2 = 1
        };
        using var paint = new SKPaint { IsAntialias = true, Color = A(ink, 0.92f * name.Strength) };
        canvas.Save();
        canvas.Concat(in matrix);
        canvas.DrawText(name.Text, 0, 0, SKTextAlign.Left, font, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The tape: the minutes TradeAgent recorded, each one's close standing on a strip in front of the organisation — a ribbon whose top
    /// edge is the price. Drawn in the grey of the app's own words, never in the light's colour and never in green or red: it is the
    /// market, not the AI, and it is neither a profit nor a loss.
    /// </summary>
    static void Tape(SKCanvas canvas, FloorFrame.TapeDraw tape, bool full)
    {
        Slab(canvas, tape.Strip, false);
        var n = tape.Top.Length;
        if (n < 2) return;
        using var ribbon = new SKPath();
        ribbon.MoveTo(P(tape.Foot[0]));
        for (var i = 1; i < n; i++) ribbon.LineTo(P(tape.Foot[i]));
        for (var i = n - 1; i >= 0; i--) ribbon.LineTo(P(tape.Top[i]));
        ribbon.Close();
        float high = float.MaxValue, low = float.MinValue;
        foreach (var p in tape.Top) high = Math.Min(high, p.Y);
        foreach (var p in tape.Foot) low = Math.Max(low, p.Y);
        var line = tape.Stale ? Faint : new SKColor(0xD8, 0xDD, 0xE8);

        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, high), new SKPoint(0, low), [A(line, 0.26f * tape.Born), A(line, 0.02f * tape.Born)], null,
            SKShaderTileMode.Clamp);
        canvas.DrawPath(ribbon, paint);
        paint.Shader.Dispose();
        paint.Shader = null;

        using var edge = Poly(tape.Top, close: false);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeJoin = SKStrokeJoin.Round;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeWidth = 1.5f;
        paint.Color = A(line, 0.92f * tape.Born);
        canvas.DrawPath(edge, paint);

        // Its newest end: where the tape is being written. A moment brighter each time a minute lands.
        var newest = P(tape.Top[^1]);
        paint.Style = SKPaintStyle.Fill;
        if (!tape.Stale)
        {
            var reach = 7 + 9 * tape.Fresh;
            paint.BlendMode = SKBlendMode.Plus;
            paint.Shader = SKShader.CreateRadialGradient(newest, reach, [A(SKColors.White, (0.5f + 0.5f * tape.Fresh) * tape.Born), A(SKColors.White, 0)], null,
                SKShaderTileMode.Clamp);
            canvas.DrawCircle(newest, reach, paint);
            paint.Shader.Dispose();
            paint.Shader = null;
            paint.BlendMode = SKBlendMode.SrcOver;
        }
        paint.Color = A(line, tape.Born);
        canvas.DrawCircle(newest, 2.2f, paint);

        if (tape.Caption.Length == 0) return;
        using var font = new SKFont(_medium ?? SKTypeface.Default, 11.5f) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var words = new SKPaint { IsAntialias = true, Color = A(tape.Stale ? Caution[2] : Muted, tape.Born) };
        // Beside the newest end; where the stage's edge is too near for that, above it and ending where it does.
        var wide = font.MeasureText(tape.Caption);
        var clip = canvas.LocalClipBounds;
        if (newest.X + 10 + wide <= clip.Right - 6) canvas.DrawText(tape.Caption, newest.X + 10, newest.Y + 4, SKTextAlign.Left, font, words);
        else canvas.DrawText(tape.Caption, newest.X, high - 9, SKTextAlign.Right, font, words);
    }

    static void Owner(SKCanvas canvas, FloorFrame f)
    {
        if (f.Owner.Length == 0) return;
        using var path = Poly(f.Owner);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = A(new SKColor(0x4A, 0x55, 0x70), 0.9f) };
        canvas.DrawPath(path, paint);
        if (f.OwnerLight > 0.01f)
        {
            paint.BlendMode = SKBlendMode.Plus;
            paint.StrokeWidth = 6;
            paint.Color = A(Work[2], 0.25f * f.OwnerLight);
            canvas.DrawPath(path, paint);
            paint.StrokeWidth = 1.6f;
            paint.Color = A(Work[0], 0.9f * f.OwnerLight);
            canvas.DrawPath(path, paint);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = A(Work[2], 0.16f * f.OwnerLight);
            canvas.DrawPath(path, paint);
        }
        if (f.OwnerWord.Length == 0) return;
        using var font = new SKFont(_medium ?? SKTypeface.Default, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var word = new SKPaint { IsAntialias = true, Color = Mix(Faint, SKColors.White, f.OwnerLight * 0.8f) };
        float right = float.MinValue, y = f.OwnerAt.Y;
        foreach (var p in f.Owner) right = Math.Max(right, p.X);
        canvas.DrawText(f.OwnerWord, right + 9, y + 4, SKTextAlign.Left, font, word);
    }

    /// <summary>How lit a cell is this instant: its light, breathing a little while it works, and whatever just flashed through it.</summary>
    static float Lit(in FloorFrame.Cell cell, FloorFrame f)
    {
        var light = cell.Light;
        if (light > 0.5f)
        {
            // Never two cells in step: each breathes from its own place on the floor.
            var phase = cell.Ground.X * 0.021f + cell.Ground.Y * 0.013f;
            light *= 0.86f + 0.14f * MathF.Sin(f.Time * 2.1f + phase) + 0.03f * MathF.Sin(f.Time * 7.3f + phase * 3);
        }
        return Math.Clamp(light + cell.Flash * 0.75f, 0, 1.4f) * cell.Born;
    }

    static void Glow(SKCanvas canvas, in FloorFrame.Cell cell, FloorFrame f)
    {
        var lit = Lit(cell, f);
        if (lit < 0.03f || cell.Kind == FloorCellKind.Vacant) return;
        var colours = Light(cell.Tone);
        var radius = cell.Size * (1.25f + 2.3f * Math.Min(lit, 1.2f));
        var centre = P(cell.Ground);
        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        paint.Shader = SKShader.CreateRadialGradient(centre, radius,
            [A(colours[1], 0.34f * Math.Min(lit, 1.2f)), A(colours[2], 0.16f * lit), A(colours[2], 0)], [0, 0.4f, 1],
            SKShaderTileMode.Clamp, SKMatrix.CreateScale(1, MathF.Sin(f.Camera.Pitch) * 0.95f, centre.X, centre.Y));
        canvas.DrawCircle(centre, radius, paint);
        paint.Shader.Dispose();
    }

    static void Cell(SKCanvas canvas, in FloorFrame.Cell cell, FloorFrame f, bool full)
    {
        if (cell.Born <= 0.01f) return;
        using var paint = new SKPaint { IsAntialias = true };

        if (cell.Kind == FloorCellKind.Vacant)
        {
            // An empty place: the foot of a cell that is not there, drawn in dashes. A unit with nobody at its head says so by standing empty.
            using var foot = Poly(cell.Foot);
            using var dash = SKPathEffect.CreateDash([4, 4], 0);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1.2f;
            paint.PathEffect = dash;
            paint.Color = A(new SKColor(0x5A, 0x64, 0x7C), 0.9f * cell.Born);
            canvas.DrawPath(foot, paint);
            return;
        }

        var lit = Lit(cell, f);
        var colours = Light(cell.Tone);
        var glass = cell.Active ? 1f : 0.42f;
        var a = cell.Born;

        // What it stands on darkens under it.
        paint.Shader = SKShader.CreateRadialGradient(P(cell.Ground), cell.Size * 0.95f, [A(SKColors.Black, 0.5f * a), A(SKColors.Black, 0)], null,
            SKShaderTileMode.Clamp, SKMatrix.CreateScale(1, 0.55f, cell.Ground.X, cell.Ground.Y));
        canvas.DrawCircle(P(cell.Ground), cell.Size * 0.95f, paint);
        paint.Shader.Dispose();
        paint.Shader = null;

        Vector2[] frontFace = [cell.Foot[0], cell.Foot[1], cell.Top[1], cell.Top[0]];
        Vector2[] sideFace = [cell.Foot[1], cell.Foot[2], cell.Top[2], cell.Top[1]];
        using var front = Poly(frontFace);
        using var side = Poly(sideFace);
        using var top = Poly(cell.Top);

        if (full && lit > 0.08f)
        {
            // The floor is polished enough to give a lit cell back: a short wash of its light below its near edge.
            Vector2 down = new(0, cell.Size * (0.35f + 0.5f * Math.Min(lit, 1)));
            using var mirror = Poly([cell.Foot[0], cell.Foot[1], cell.Foot[2], cell.Foot[2] + down, cell.Foot[1] + down, cell.Foot[0] + down]);
            paint.BlendMode = SKBlendMode.Plus;
            paint.Shader = SKShader.CreateLinearGradient(P(cell.Foot[1]), P(cell.Foot[1] + down), [A(colours[2], 0.2f * Math.Min(lit, 1)), A(colours[2], 0)], null,
                SKShaderTileMode.Clamp);
            canvas.DrawPath(mirror, paint);
            paint.Shader.Dispose();
            paint.Shader = null;
            paint.BlendMode = SKBlendMode.SrcOver;
        }

        // The glass, dark: three faces, each a little different, the top the lightest.
        Face(canvas, paint, front, frontFace, new SKColor(0x25, 0x2C, 0x40), new SKColor(0x16, 0x1A, 0x27), 0.86f * a * glass);
        Face(canvas, paint, side, sideFace, new SKColor(0x1A, 0x20, 0x30), new SKColor(0x0F, 0x12, 0x1B), 0.9f * a * glass);
        paint.Color = A(new SKColor(0x2E, 0x36, 0x4C), 0.9f * a * glass);
        canvas.DrawPath(top, paint);

        if (lit > 0.015f)
        {
            // THE LIGHT INSIDE, seen through each face: brightest where the face is nearest the middle of the cell, falling away to its
            // edges. Painted face by face and clipped to each, which is what makes it sit inside the glass and not on it.
            var reach = cell.Size * (0.5f + 0.42f * Math.Min(lit, 1.2f));
            var centre = P(cell.Middle);
            using var glow = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
            // Taller than it is wide: a light standing in its cell, not a lamp hung in a box.
            glow.Shader = SKShader.CreateRadialGradient(centre, reach,
                [A(colours[0], Math.Min(1, 0.12f + 0.95f * lit)), A(colours[1], Math.Min(1, 0.58f * lit)), A(colours[2], 0.22f * lit), A(colours[2], 0)],
                [0, 0.24f, 0.66f, 1], SKShaderTileMode.Clamp, SKMatrix.CreateScale(0.72f, 1.12f, centre.X, centre.Y));
            foreach (var face in (SKPath[])[front, side, top])
            {
                canvas.Save();
                canvas.ClipPath(face, SKClipOperation.Intersect, true);
                canvas.DrawCircle(centre, reach, glow);
                canvas.Restore();
            }
            glow.Shader.Dispose();

            // And the glass itself takes the colour of what is lit behind it.
            paint.Color = A(colours[2], 0.16f * Math.Min(lit, 1) * a);
            canvas.DrawPath(front, paint);
            canvas.DrawPath(side, paint);
            paint.Color = A(colours[1], 0.2f * Math.Min(lit, 1) * a);
            canvas.DrawPath(top, paint);
        }

        // Its edges: the top's four bright, the three uprights quieter, all warmer as it lights.
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.StrokeJoin = SKStrokeJoin.Round;
        paint.Color = A(Mix(new SKColor(0x38, 0x41, 0x57), colours[1], lit * 0.55f), 0.9f * a * (0.55f + 0.45f * glass));
        for (var i = 0; i < 3; i++) canvas.DrawLine(P(cell.Foot[i]), P(cell.Top[i]), paint);
        canvas.DrawLine(P(cell.Foot[0]), P(cell.Foot[1]), paint);
        canvas.DrawLine(P(cell.Foot[1]), P(cell.Foot[2]), paint);
        paint.Color = A(Mix(new SKColor(0x5A, 0x65, 0x82), colours[0], lit * 0.7f), 0.95f * a * (0.6f + 0.4f * glass));
        canvas.DrawPath(top, paint);

        if (cell.Kind == FloorCellKind.Board)
        {
            // A board seat's cell wears a band: the same glass, sealed twice.
            Vector2 At(Vector2 foot, Vector2 up) => Vector2.Lerp(foot, up, 0.72f);
            paint.Color = A(Mix(new SKColor(0x6A, 0x75, 0x92), colours[0], lit * 0.5f), 0.9f * a);
            canvas.DrawLine(P(At(cell.Foot[0], cell.Top[0])), P(At(cell.Foot[1], cell.Top[1])), paint);
            canvas.DrawLine(P(At(cell.Foot[1], cell.Top[1])), P(At(cell.Foot[2], cell.Top[2])), paint);
        }

        if (cell.Flag > 0.02f)
        {
            // Something here wants looking at: a small amber light standing over the cell.
            var at = (cell.Top[0] + cell.Top[1] + cell.Top[2] + cell.Top[3]) / 4 - new Vector2(0, cell.Size * 0.42f + 5);
            using var flag = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
            flag.Shader = SKShader.CreateRadialGradient(P(at), 11, [A(Caution[2], 0.55f * cell.Flag), A(Caution[2], 0)], null, SKShaderTileMode.Clamp);
            canvas.DrawCircle(P(at), 11, flag);
            flag.Shader.Dispose();
            flag.Shader = null;
            flag.BlendMode = SKBlendMode.SrcOver;
            flag.Color = A(Caution[2], cell.Flag);
            canvas.DrawCircle(P(at), 2.6f, flag);
        }
    }

    static void Face(SKCanvas canvas, SKPaint paint, SKPath path, Vector2[] quad, SKColor high, SKColor low, float alpha)
    {
        paint.Shader = SKShader.CreateLinearGradient(P((quad[2] + quad[3]) / 2), P((quad[0] + quad[1]) / 2), [A(high, alpha), A(low, alpha)], null,
            SKShaderTileMode.Clamp);
        canvas.DrawPath(path, paint);
        paint.Shader.Dispose();
        paint.Shader = null;
    }

    /// <summary>The light a lit cell throws past its own glass, over whatever is near it.</summary>
    static void Bloom(SKCanvas canvas, in FloorFrame.Cell cell, FloorFrame f, bool full)
    {
        var lit = Lit(cell, f);
        if (lit < 0.3f || cell.Kind == FloorCellKind.Vacant) return;
        var colours = Light(cell.Tone);
        var radius = cell.Size * (full ? 1.1f + 2.2f * Math.Min(lit, 1.2f) : 1.5f);
        var strength = (lit - 0.3f) / 0.7f;
        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        paint.Shader = SKShader.CreateRadialGradient(P(cell.Middle), radius,
            [A(colours[1], 0.3f * strength), A(colours[2], 0.1f * strength), A(colours[2], 0)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
        canvas.DrawCircle(P(cell.Middle), radius, paint);
        paint.Shader.Dispose();
    }

    static void Ring(SKCanvas canvas, in FloorFrame.Ring ring)
    {
        if (ring.Strength <= 0.01f) return;
        var colours = Light(ring.Tone);
        using var path = Poly(ring.Points);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, BlendMode = SKBlendMode.Plus };
        paint.StrokeWidth = 6;
        paint.Color = A(colours[2], 0.16f * ring.Strength);
        canvas.DrawPath(path, paint);
        paint.StrokeWidth = 1.5f;
        paint.Color = A(colours[1], 0.85f * ring.Strength);
        canvas.DrawPath(path, paint);
    }

    static void Flight(SKCanvas canvas, in FloorFrame.Flight flight)
    {
        if (flight.Strength <= 0.01f || flight.Trail.Length < 2) return;
        var colours = Light(flight.Tone);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, BlendMode = SKBlendMode.Plus };
        var n = flight.Trail.Length;
        for (var i = n - 2; i >= 0; i--)
        {
            // Bright and thick at the head, thinning to nothing behind it.
            var t = 1 - i / (float)(n - 1);
            paint.StrokeWidth = (0.8f + 2.4f * t * t) * flight.Size;
            paint.Color = A(Mix(colours[2], colours[0], t * t), flight.Strength * t * t * 0.9f);
            canvas.DrawLine(P(flight.Trail[i + 1]), P(flight.Trail[i]), paint);
        }
        var head = P(flight.Trail[0]);
        paint.Style = SKPaintStyle.Fill;
        var radius = 13 * flight.Size;
        paint.Shader = SKShader.CreateRadialGradient(head, radius, [A(colours[0], 0.95f * flight.Strength), A(colours[1], 0.4f * flight.Strength), A(colours[2], 0)],
            [0, 0.25f, 1], SKShaderTileMode.Clamp);
        canvas.DrawCircle(head, radius, paint);
        paint.Shader.Dispose();
    }

    static void Spark(SKCanvas canvas, in FloorFrame.Spark spark)
    {
        if (spark.Strength <= 0.01f) return;
        var colours = Light(spark.Tone);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, BlendMode = SKBlendMode.Plus };
        paint.StrokeWidth = 1.6f;
        paint.Color = A(colours[1], 0.9f * spark.Strength);
        canvas.DrawLine(P(spark.From), P(spark.To), paint);
        paint.Style = SKPaintStyle.Fill;
        paint.Shader = SKShader.CreateRadialGradient(P(spark.To), 7, [A(colours[0], 0.8f * spark.Strength), A(colours[2], 0)], null, SKShaderTileMode.Clamp);
        canvas.DrawCircle(P(spark.To), 7, paint);
        paint.Shader.Dispose();
    }

    /// <summary>
    /// THE FOUR CORNERS round the cell that is working, or the one being pointed at: nothing but four small angles, drawn tight to it, that
    /// say "this one" — so the words beside the floor and the thing on it are plainly about each other.
    /// </summary>
    static void Marks(SKCanvas canvas, in FloorFrame.Cell cell)
    {
        if (cell.Mark <= 0.02f || cell.Kind == FloorCellKind.Vacant) return;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in cell.Foot.Concat(cell.Top))
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        // They close in on the cell as they arrive.
        var open = 5 + (1 - cell.Mark) * 12;
        float l = minX - open, r = maxX + open, t = minY - open, b = maxY + open;
        var arm = Math.Max(5, cell.Size * 0.2f);
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, StrokeCap = SKStrokeCap.Square,
            Color = A(SKColors.White, 0.82f * cell.Mark)
        };
        using var path = new SKPath();
        path.MoveTo(l, t + arm); path.LineTo(l, t); path.LineTo(l + arm, t);
        path.MoveTo(r - arm, t); path.LineTo(r, t); path.LineTo(r, t + arm);
        path.MoveTo(r, b - arm); path.LineTo(r, b); path.LineTo(r - arm, b);
        path.MoveTo(l + arm, b); path.LineTo(l, b); path.LineTo(l, b - arm);
        canvas.DrawPath(path, paint);
    }
}
