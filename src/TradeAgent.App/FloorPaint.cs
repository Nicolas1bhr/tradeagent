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
        var awake = 1 - f.Asleep;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, f.Width, f.Height));
        canvas.Clear(Bg);

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
        canvas.Restore();
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
            using var shade = new SKPaint { IsAntialias = true, Color = A(SKColors.Black, 0.5f * alpha), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) };
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
    static void Name(SKCanvas canvas, FloorFrame.Name name)
    {
        if (name.Strength <= 0.02f || name.Text.Length == 0) return;
        using var font = new SKFont(_medium ?? SKTypeface.Default, 100) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
        var wide = font.MeasureText(name.Text);
        if (wide <= 0) return;
        // As large as its place allows and no larger than a name on this floor is; a name that would be too small to read is left off,
        // and the seat's own callout says where it is instead.
        var size = Math.Min(name.Size, name.Room * 100 / wide);
        var across = name.Along.Length();
        if (size * across < 7.5f) return;
        var k = size / 100f;
        var matrix = new SKMatrix
        {
            ScaleX = name.Along.X * k, SkewX = -name.Up.X * k, TransX = name.At.X,
            SkewY = name.Along.Y * k, ScaleY = -name.Up.Y * k, TransY = name.At.Y,
            Persp2 = 1
        };
        using var paint = new SKPaint { IsAntialias = true, Color = A(Faint, 0.92f * name.Strength) };
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
