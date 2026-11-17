using System.Numerics;

namespace TradeAgent.App;

/// <summary>How much the floor moves. <see cref="Full"/>: everything. <see cref="Calm"/>: no drift and a slow frame, for a machine that draws without a graphics card. <see cref="Still"/>: redrawn only when something changes.</summary>
enum FloorMotion { Full, Calm, Still }

/// <summary>What a light on the floor is saying, which decides its colour: the AI at work, something to look at, or something that failed.</summary>
enum FloorTone { Work, Caution, Failure }

/// <summary>
/// THE CAMERA (<c>U-ui-alive</c>): where the floor is looked at from. It stands in front of the organisation and above it, turned a
/// little to one side, so the chief is furthest and the executors nearest — and it is a real point of view, not a fixed drawing: it can
/// turn a few degrees, and everything on the floor turns with it.
/// </summary>
readonly record struct FloorCamera(float Yaw, float Pitch, float Zoom, Vector2 Screen, Vector3 Target, float Perspective)
{
    /// <summary>Where a point of the floor falls on the screen, and how far from the camera it is (greater is further).</summary>
    public Vector3 Project(Vector3 p)
    {
        var d = p - Target;
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw), cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
        var across = d.X * cy + d.Y * sy;
        var away = -d.X * sy + d.Y * cy;
        var up = d.Z * cp + away * sp;
        var depth = away * cp - d.Z * sp;
        var scale = Zoom / (1 + depth * Perspective);
        return new Vector3(Screen.X + across * scale, Screen.Y - up * scale, depth);
    }

    public Vector2 Point(Vector3 p)
    {
        var q = Project(p);
        return new Vector2(q.X, q.Y);
    }

    /// <summary>How many pixels one cell's width is at <paramref name="p"/>.</summary>
    public float ScaleAt(Vector3 p) => Zoom / (1 + Project(p).Z * Perspective);
}

/// <summary>
/// THE TAPE AS THE FLOOR SHOWS IT: whose prices, each minute's close as a height from 0 (the least of the stretch) to 1 (the most),
/// oldest first, and the words beside the newest. <see cref="Stale"/>: the newest is older than a tape being recorded would be.
/// </summary>
sealed record FloorTape(string Symbol, float[] Heights, string Caption, bool Stale);

/// <summary>
/// ONE FRAME OF THE FLOOR, ready to paint (<c>U-ui-alive</c>): everything already placed on the screen by the camera, so the painter only
/// paints. Made on the UI thread, handed to the render thread and never changed after — which is what lets one thread move the floor
/// while another draws it.
/// </summary>
sealed class FloorFrame
{
    public float Width, Height, Time, Unit, Asleep;
    public FloorCamera Camera;
    public FloorMotion Motion;

    /// <summary>A slab: its top and the two sides that face the camera, back to front.</summary>
    public readonly record struct Slab(Vector2[] Top, Vector2[] Front, Vector2[] Side, int Level, bool Quiet, bool Board, float Born, float Rim,
        Vector2[] Seams);

    /// <summary>A unit's name, written on its slab: the words, where they start, the screen direction they run in and the one they stand up in.</summary>
    public readonly record struct Name(string Text, Vector2 At, Vector2 Along, Vector2 Up, float Size, float Room, float Strength);

    /// <summary>A cell: its eight corners (foot then top, counter-clockwise from the near-left), its middle, and how lit it is.</summary>
    public readonly record struct Cell(string Id, FloorCellKind Kind, Vector2[] Foot, Vector2[] Top, Vector2 Middle, Vector2 Ground, float Size,
        float Light, float Flash, float Born, bool Active, bool Marked, float Mark, FloorTone Tone, float Flag, float Depth);

    /// <summary>A line in the floor, and how warm something that just ran along it left it.</summary>
    public readonly record struct Line(Vector2[] Points, float Warmth);

    /// <summary>Something travelling: where its head is, the points behind it, and how much of it is left.</summary>
    public readonly record struct Flight(Vector2[] Trail, float Strength, FloorTone Tone, float Size, bool Air);

    /// <summary>A ring opening on the floor round a cell.</summary>
    public readonly record struct Ring(Vector2[] Points, float Strength, FloorTone Tone);

    /// <summary>A spark leaving the top of a cell.</summary>
    public readonly record struct Spark(Vector2 From, Vector2 To, float Strength, FloorTone Tone);

    public Slab[] Slabs = [];
    public Name[] Names = [];
    public Cell[] Cells = [];
    public Line[] Lines = [];
    public Flight[] Flights = [];
    public Ring[] Rings = [];
    public Spark[] Sparks = [];

    /// <summary>
    /// The tape: its strip, the foot and the top of the ribbon standing on it (oldest first), the words beside its newest end, and how
    /// far it has risen. Null where there is no tape to show.
    /// </summary>
    public sealed record TapeDraw(Slab Strip, Vector2[] Foot, Vector2[] Top, string Caption, bool Stale, float Born, float Fresh);

    public TapeDraw? Tape;

    /// <summary>The owner's place: the ring behind the chief, its word, and how lit it is.</summary>
    public Vector2[] Owner = [];
    public Vector2 OwnerAt;
    public float OwnerLight;
    public string OwnerWord = "";
}

/// <summary>
/// THE FLOOR, MOVING (<c>U-ui-alive</c>). Holds where everything stands (<see cref="FloorPlan"/>), how lit each cell is and what is
/// travelling, and each frame moves them all a step and places them on the screen (<see cref="Frame"/>).
///
/// <para><b>Nothing here decides what is true.</b> A cell is lit because the page said a turn is open in it; something travels because the
/// page handed over a row that says it happened. This class only decides how that looks: how fast a light comes up, how long something
/// takes to cross the floor. Take every animation away and the same facts are still drawn.</para>
///
/// <para>One thread only: the UI's. A frame, once made, belongs to whoever paints it.</para>
/// </summary>
sealed class FloorScene
{
    /// <summary>How lit a cell is at rest: an active seat while the AI runs, the same seat while it does not, and a dormant one.</summary>
    public const float Idle = 0.17f, IdleAsleep = 0.07f, Dark = 0f;

    const float BaseYaw = 0.40f, BasePitch = 0.64f, Perspective = 0.014f;

    sealed class CellState
    {
        public float Light, Flash, Born, Mark, Flag, Wait;
        public bool Working, Flagged, Seen;
        public FloorTone Tone;
    }

    sealed class SlabState
    {
        public float Born, Rim, Wait;
        public bool Seen;
    }

    sealed class Travel
    {
        public required Vector3[] Path;
        public required float[] Along;
        public float Length, Age, Lasts, Size = 1;
        public FloorTone Tone;
        public bool Air;
        public string? Warms;
        public Action? Arrived;
    }

    sealed class Ripple
    {
        public Vector3 At;
        public float Age, Lasts, Reach;
        public FloorTone Tone;
    }

    sealed class Fleck
    {
        public Vector3 At;
        public float Age, Lasts, Rise;
        public FloorTone Tone;
    }

    FloorPlan _plan = FloorPlan.Empty;
    readonly Dictionary<string, CellState> _cells = new(StringComparer.Ordinal);
    readonly Dictionary<string, SlabState> _slabs = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string), float> _warmth = new();
    readonly List<Travel> _travels = [];
    readonly List<Ripple> _ripples = [];
    readonly List<Fleck> _flecks = [];

    float _time, _asleep = 1, _asleepWanted = 1, _ownerLight;
    FloorTape? _tape;
    float _tapeBorn, _tapeFresh;
    float _zoom, _zoomWanted;
    Vector3 _target;
    Vector2 _lean, _leanWanted;
    bool _fitted;
    string? _marked;

    public FloorPlan Plan => _plan;
    public FloorMotion Motion { get; set; } = FloorMotion.Full;

    /// <summary>Whether anything is on its way somewhere: a light coming up, something travelling. While it is, every frame is drawn.</summary>
    public bool Busy { get; private set; } = true;

    /// <summary>Whether the floor is only breathing: a turn is open, or the camera is drifting. A slow frame is enough for that.</summary>
    public bool Breathing { get; private set; }

    /// <summary>The cell the pointer is on or the owner chose, drawn with its corners marked.</summary>
    public string? Marked
    {
        get => _marked;
        set => _marked = value;
    }

    /// <summary>
    /// The chart changed: everything takes its new place. A cell or slab that was not there before grows in where it stands.
    /// <paramref name="assembling"/>: the floor is being shown for the first time, and builds itself — the organisation's slab first, then
    /// each unit on the one that holds it, then the cells on them, a moment apart — instead of simply being there.
    /// </summary>
    public void Lay(FloorPlan plan, bool atOnce, bool assembling = false)
    {
        _plan = plan;
        var levels = plan.Slabs.ToDictionary(s => s.Unit, s => s.Level, StringComparer.Ordinal);
        foreach (var s in _cells.Values) s.Seen = false;
        var nth = 0;
        foreach (var cell in plan.Cells)
        {
            if (!_cells.TryGetValue(cell.Id, out var state))
            {
                _cells[cell.Id] = state = new CellState { Born = atOnce ? 1 : 0 };
                if (assembling) state.Wait = 0.35f + levels.GetValueOrDefault(cell.Unit) * 0.22f + nth * 0.045f;
            }
            state.Seen = true;
            nth++;
        }
        foreach (var gone in _cells.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) _cells.Remove(gone);

        foreach (var s in _slabs.Values) s.Seen = false;
        nth = 0;
        foreach (var slab in plan.Slabs)
        {
            if (!_slabs.TryGetValue(slab.Unit, out var state))
            {
                _slabs[slab.Unit] = state = new SlabState { Born = atOnce ? 1 : 0 };
                if (assembling) state.Wait = slab.Level * 0.22f + nth * 0.03f;
            }
            state.Seen = true;
            nth++;
        }
        foreach (var gone in _slabs.Where(kv => !kv.Value.Seen).Select(kv => kv.Key).ToList()) _slabs.Remove(gone);
        _fitted = false;
        _arriving = assembling;
        Attend();
    }

    /// <summary>The floor is being shown for the first time: the camera starts a step back and comes in as it builds.</summary>
    bool _arriving;

    /// <summary>Which cells have a turn open now, and whether the AI is running at all: decides every cell's resting light.</summary>
    public void Lit(IReadOnlySet<string> working, bool running)
    {
        var changed = _asleepWanted != (running ? 0 : 1);
        foreach (var (id, state) in _cells)
        {
            var now = working.Contains(id);
            changed |= now != state.Working;
            state.Working = now;
        }
        _asleepWanted = running ? 0 : 1;
        // Told the same thing again, it has nothing new to show: the page says who is working every time it looks, changed or not.
        if (changed) Attend();
    }

    /// <summary>A cell is marked for the owner to look at (a mandate past its deadline) until the page says otherwise.</summary>
    public void Flag(IReadOnlySet<string> flagged)
    {
        foreach (var (id, state) in _cells)
        {
            var now = flagged.Contains(id);
            if (now != state.Flagged) Busy = true;
            state.Flagged = now;
        }
    }

    /// <summary>The pointer leans the camera a little, as far across and down the stage as it is (each from -1 to 1).</summary>
    public void Lean(float across, float down)
    {
        // Only where the frames are free: on a machine that draws without a graphics card, a camera that followed the pointer would
        // redraw the whole stage for every inch the pointer moved.
        if (Motion != FloorMotion.Full)
        {
            _leanWanted = Vector2.Zero;
            return;
        }
        _leanWanted = new Vector2(Math.Clamp(across, -1, 1), Math.Clamp(down, -1, 1));
        Attend();
    }

    /// <summary>
    /// HOW LONG THE FLOOR GOES ON MOVING OF ITSELF after the last thing that happened on it or the last move of the pointer over it.
    /// While somebody may be looking, the camera drifts; left alone this long, it comes to rest and the floor asks for no frame at all.
    /// This app runs for days with nobody at the screen, and on a server seen through a remote one: a picture that turned for ever
    /// would redraw the whole stage twenty-five times a second for an audience that is not there.
    /// </summary>
    public const float AttentionLasts = 25;

    float _attention;

    /// <summary>Something happened, or somebody moved: the floor is worth looking at for a while.</summary>
    void Attend()
    {
        _attention = AttentionLasts;
        Busy = true;
    }

    /// <summary>How far the camera's own drift is let out: all the way while the floor has attention, eased to nothing as it runs out.</summary>
    float Drift => Motion != FloorMotion.Full ? 0 : Ease(Math.Clamp(_attention / 6f, 0, 1));

    /// <summary>
    /// Everything on its way is dropped where it is — what is travelling, the rings, the sparks, what a line was warmed by, a flash half
    /// faded. For when what the floor was showing stops being what it shows: a replay ending is not followed by its last moments
    /// crossing the present's floor.
    /// </summary>
    public void Clear()
    {
        _travels.Clear();
        _ripples.Clear();
        _flecks.Clear();
        _warmth.Clear();
        foreach (var state in _cells.Values) state.Flash = 0;
        foreach (var state in _slabs.Values) state.Rim = 0;
        _ownerLight = 0;
        Busy = true;
    }

    // ---- what the page tells it happened ----------------------------------------------------------------------------------------

    /// <summary>A cell brightens for a moment: its turn opened, or something reached it.</summary>
    public void Flash(string id, FloorTone tone = FloorTone.Work, float strength = 1)
    {
        if (!_cells.TryGetValue(id, out var state)) return;
        state.Flash = Math.Max(state.Flash, strength);
        state.Tone = tone;
        Attend();
    }

    /// <summary>A ring opens on the floor round a cell: a decision, a judgement, a turn that ended badly.</summary>
    public void Ring(string id, FloorTone tone = FloorTone.Work, float reach = 1.5f)
    {
        if (!_plan.ById.TryGetValue(id, out var cell) || _ripples.Count > 40) return;
        _ripples.Add(new Ripple { At = cell.Foot, Lasts = 1.5f, Reach = reach, Tone = tone });
        Attend();
    }

    /// <summary>A spark leaves the top of a cell: one tool call answered.</summary>
    public void Spark(string id, FloorTone tone = FloorTone.Work)
    {
        // A seat at work calls a tool every few seconds. On a machine that draws without a graphics card that would be a floor never
        // still while any turn is open, so there the call is shown where it costs nothing: in the words under the cell's name.
        if (Motion != FloorMotion.Full) return;
        if (!_plan.ById.TryGetValue(id, out var cell) || _flecks.Count > 80) return;
        // Never two in the same place: each leaves a little to one side of the last, by the count of them.
        var turn = _flecks.Count * 2.4f;
        var off = new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0) * cell.Size * 0.22f;
        _flecks.Add(new Fleck { At = cell.Top + off, Lasts = 1.1f, Rise = 0.9f, Tone = tone });
        if (_cells.TryGetValue(id, out var state)) state.Flash = Math.Max(state.Flash, 0.35f);
        Attend();
    }

    /// <summary>
    /// Something goes from one place to another: along the lines in the floor where they join the two, through the air where they do not.
    /// <see cref="FloorPlan.Owner"/>'s place is reached with <see cref="OwnerId"/>.
    /// </summary>
    public void Send(string from, string to, FloorTone tone = FloorTone.Work, float size = 1, Action? arrived = null)
    {
        if (_travels.Count > 60) { arrived?.Invoke(); return; }
        var a = Place(from);
        var b = Place(to);
        if (a is null || b is null) { arrived?.Invoke(); return; }

        Vector3[]? path = null;
        string? warms = null;
        if (from != OwnerId && to != OwnerId) path = _plan.Route(from, to);
        else if (_plan.Chief is { } chief && _plan.OwnerLine.Length > 0)
        {
            // To and from the owner: the floor's own lines as far as the head of the organisation, then the line behind it.
            var seat = from == OwnerId ? to : from;
            var inner = seat == chief ? [] : _plan.Route(seat, chief);
            if (inner is not null)
            {
                path = [.. inner, .. _plan.OwnerLine];
                if (from == OwnerId) Array.Reverse(path);
            }
        }
        var air = path is null;
        path ??= Arc(a.Value, b.Value);
        if (!air) warms = from + ">" + to;

        var along = new float[path.Length];
        for (var i = 1; i < path.Length; i++) along[i] = along[i - 1] + Vector3.Distance(path[i - 1], path[i]);
        var length = along[^1];
        if (length < 0.01f) { arrived?.Invoke(); return; }
        _travels.Add(new Travel
        {
            Path = path, Along = along, Length = length, Lasts = Math.Clamp(length / 5.2f, 0.9f, 2.8f), Tone = tone, Size = size, Air = air,
            Warms = warms, Arrived = arrived
        });
        if (!air) Warm(path, 1);
        Attend();
    }

    /// <summary>The owner's place as a destination of <see cref="Send"/>.</summary>
    public const string OwnerId = "owner";

    Vector3? Place(string id) =>
        id == OwnerId ? _plan.Owner : _plan.ById.TryGetValue(id, out var cell) ? cell.Top : null;

    /// <summary>The way through the air: up off one cell, over, and down onto the other — higher the further apart they stand.</summary>
    static Vector3[] Arc(Vector3 a, Vector3 b)
    {
        const int steps = 26;
        var height = 0.7f + Vector3.Distance(a, b) * 0.22f;
        var path = new Vector3[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            path[i] = Vector3.Lerp(a, b, t) + new Vector3(0, 0, height * 4 * t * (1 - t));
        }
        return path;
    }

    void Warm(Vector3[] path, float by)
    {
        foreach (var channel in _plan.Channels)
        {
            // A line is warmed when the way runs over it: its two ends are both on the path.
            if (!OnPath(path, channel.Path[0]) || !OnPath(path, channel.Path[^1])) continue;
            var key = (channel.From, channel.To);
            _warmth[key] = Math.Max(_warmth.GetValueOrDefault(key), by);
        }
    }

    static bool OnPath(Vector3[] path, Vector3 p)
    {
        foreach (var q in path)
            if (Vector3.DistanceSquared(p, q) < 1e-4f) return true;
        return false;
    }

    // ---- one step ---------------------------------------------------------------------------------------------------------------

    /// <summary>Moves everything on by <paramref name="dt"/> seconds.</summary>
    public void Advance(float dt)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        _time += dt;
        bool busy = false, breathing = false;

        _asleep = Toward(_asleep, _asleepWanted, dt, 1.6f, ref busy);
        foreach (var cell in _plan.Cells)
        {
            if (!_cells.TryGetValue(cell.Id, out var s)) continue;
            var rest = !cell.Active ? Dark : Lerp(Idle, IdleAsleep, _asleep);
            var wanted = s.Working ? 1f : rest;
            // A light comes up faster than it goes down: a turn opening is an event, a turn closing is a settling.
            s.Light = Toward(s.Light, wanted, dt, wanted > s.Light ? 3.2f : 1.1f, ref busy);
            s.Flash = Toward(s.Flash, 0, dt, 1.9f, ref busy);
            if (s.Wait > 0) { s.Wait -= dt; busy = true; }
            else s.Born = Toward(s.Born, 1, dt, 2.6f, ref busy);
            s.Mark = Toward(s.Mark, cell.Id == _marked || s.Working ? 1 : 0, dt, 5f, ref busy);
            s.Flag = Toward(s.Flag, s.Flagged ? 1 : 0, dt, 2.5f, ref busy);
            // A lit cell breathes on a machine that can afford the frames; on one that cannot it is simply lit, and costs none.
            if (s.Working && Motion == FloorMotion.Full) breathing = true;
        }
        foreach (var s in _slabs.Values)
        {
            if (s.Wait > 0) { s.Wait -= dt; busy = true; }
            else s.Born = Toward(s.Born, 1, dt, 2.4f, ref busy);
            s.Rim = Toward(s.Rim, 0, dt, 1.5f, ref busy);
        }
        _ownerLight = Toward(_ownerLight, 0, dt, 1.4f, ref busy);
        _tapeBorn = Toward(_tapeBorn, _tape is null ? 0 : 1, dt, 1.6f, ref busy);
        // A minute landing lights the tape's end for a moment, on a machine that can afford the frames; on one that cannot, the new
        // minute is simply drawn. It lands every minute for as long as the app runs: it must never cost more than a moment.
        _tapeFresh = Motion == FloorMotion.Full ? Toward(_tapeFresh, 0, dt, 3f, ref busy) : 0;

        foreach (var key in _warmth.Keys.ToList())
        {
            var w = _warmth[key] - dt * 0.42f;
            if (w <= 0) _warmth.Remove(key);
            else { _warmth[key] = w; busy = true; }
        }

        for (var i = _travels.Count - 1; i >= 0; i--)
        {
            var t = _travels[i];
            t.Age += dt;
            busy = true;
            if (t.Age < t.Lasts + 0.35f) continue;
            _travels.RemoveAt(i);
        }
        foreach (var t in _travels)
        {
            if (t.Arrived is null || t.Age < t.Lasts) continue;
            var arrived = t.Arrived;
            t.Arrived = null;
            arrived();
        }
        for (var i = _ripples.Count - 1; i >= 0; i--)
        {
            _ripples[i].Age += dt;
            busy = true;
            if (_ripples[i].Age >= _ripples[i].Lasts) _ripples.RemoveAt(i);
        }
        for (var i = _flecks.Count - 1; i >= 0; i--)
        {
            _flecks[i].Age += dt;
            busy = true;
            if (_flecks[i].Age >= _flecks[i].Lasts) _flecks.RemoveAt(i);
        }

        var before = _lean;
        _lean += (_leanWanted - _lean) * Math.Min(1, dt * 3.5f);
        if (Vector2.DistanceSquared(before, _lean) > 1e-7f) busy = true;
        if (MathF.Abs(_zoom - _zoomWanted) > 0.01f || Vector2.DistanceSquared(_offset, _offsetWanted) > 0.05f) busy = true;
        // Coming in at the start is slow enough to be seen; following a window being resized is quick enough not to be.
        var follow = _arriving ? 1.3f : 4f;
        if (_arriving && MathF.Abs(_zoom - _zoomWanted) < 0.05f) _arriving = false;
        _zoom += (_zoomWanted - _zoom) * Math.Min(1, dt * follow);
        _offset += (_offsetWanted - _offset) * Math.Min(1, dt * 4f);

        if (_attention > 0)
        {
            _attention = Math.Max(0, _attention - dt);
            if (Motion == FloorMotion.Full) breathing = true;                     // the camera is still drifting, or coming to rest
        }
        Busy = busy;
        Breathing = breathing;
    }

    /// <summary>
    /// The tape to show, or none. A new last minute — the same stretch moved on by one — lights the ribbon's newest end for a moment.
    /// </summary>
    public void Tape(FloorTape? tape)
    {
        if (tape is null != _tape is null) _fitted = false;                       // the floor is fitted with its tape or without it
        var same = tape is not null && _tape is not null && tape.Caption == _tape.Caption && tape.Stale == _tape.Stale
                   && tape.Heights.AsSpan().SequenceEqual(_tape.Heights);
        if (same) return;
        // Another minute landed: its end lights for a moment. That is the tape's own business and not a reason for the camera to stir.
        if (tape is not null && _tape is not null) _tapeFresh = 1;
        _tape = tape;
        Busy = true;
    }

    /// <summary>The owner's place lights for a moment: something reached him, or he spoke.</summary>
    public void OwnerLit() { _ownerLight = 1; Attend(); }

    /// <summary>A unit's slab shows its edge for a moment: it was chartered, or the board's act reached it.</summary>
    public void Rim(string unit)
    {
        if (_slabs.TryGetValue(unit, out var s)) { s.Rim = 1; Attend(); }
    }

    static float Toward(float value, float wanted, float dt, float rate, ref bool moving)
    {
        var d = wanted - value;
        // Near enough is there: the last hundredth of a light's way is nothing an eye sees, and would be seconds more of frames.
        if (MathF.Abs(d) < 0.008f) return wanted;
        moving = true;
        return value + d * Math.Min(1, dt * rate);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ---- one frame --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The camera for a stage whose clear part — not under the words laid over it — is <paramref name="clear"/> (left, top, right,
    /// bottom): turned a little by time and by the pointer, and as near as it can stand with the whole floor in the clear part.
    /// </summary>
    FloorCamera Camera((float L, float T, float R, float B) clear)
    {
        var drift = MathF.Sin(_time * 0.11f) * 0.04f * Drift;
        var yaw = BaseYaw + drift + _lean.X * 0.06f;
        var pitch = BasePitch + MathF.Sin(_time * 0.07f + 1.3f) * 0.012f * Drift + _lean.Y * 0.03f;

        if (!_fitted || clear != _fitKey)
        {
            _fitKey = clear;
            (_zoomWanted, _offsetWanted) = Fit(clear);
            if (!_fitted || _zoom <= 0)
            {
                _zoom = _arriving ? _zoomWanted * 0.9f : _zoomWanted;
                _offset = _offsetWanted;
            }
            _fitted = true;
        }
        // Where the floor's middle falls follows the zoom it is drawn at, so coming in is a move toward the floor and not a slide across it.
        return new FloorCamera(yaw, pitch, _zoom, _offset + _centre * (_zoomWanted - _zoom), _target, Perspective);
    }

    (float, float, float, float) _fitKey;
    Vector2 _offset, _offsetWanted, _centre;

    /// <summary>
    /// How near the camera may stand, and where on the stage the floor's middle then falls: the whole floor — every slab's corners and
    /// the top of every cell — inside the clear part, in the middle of it. Fitted at the resting angle, so the floor does not breathe in
    /// and out as the camera turns.
    /// </summary>
    (float Zoom, Vector2 Offset) Fit((float L, float T, float R, float B) clear)
    {
        _target = ((_plan.Min + _plan.Max) / 2) with { Z = 0 };
        // With the camera's own perspective: what is nearer is drawn larger, and it is the drawn floor that has to fit.
        var rest = new FloorCamera(BaseYaw, BasePitch, 1, Vector2.Zero, _target, Perspective);
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        void Hold(Vector3 world)
        {
            var p = rest.Point(world);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        foreach (var slab in _plan.Slabs)
        {
            Hold(new Vector3(slab.X0, slab.Y0, slab.Z0)); Hold(new Vector3(slab.X1, slab.Y0, slab.Z0));
            Hold(new Vector3(slab.X1, slab.Y1, slab.Z1)); Hold(new Vector3(slab.X0, slab.Y1, slab.Z1));
        }
        foreach (var cell in _plan.Cells)
        {
            var half = cell.Size / 2;
            for (var i = 0; i < 4; i++)
                Hold(cell.Foot + new Vector3((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, cell.Height));
        }
        var middle = new Vector2((clear.L + clear.R) / 2, (clear.T + clear.B) / 2);
        if (_plan.Slabs.Count == 0) return (40, middle);
        Hold(_plan.Owner + new Vector3(0.5f, 0.5f, 0));
        if (_tape is not null && TapeStrip() is { } strip)
            foreach (var corner in strip.Corners) Hold(corner);

        var zoom = Math.Min((clear.R - clear.L) / Math.Max(0.1f, maxX - minX), (clear.B - clear.T) / Math.Max(0.1f, maxY - minY));
        zoom = Math.Clamp(zoom, 12, 120);
        _centre = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
        return (zoom, middle - _centre * zoom);
    }

    static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (var i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
    }

    /// <summary>Everything, placed on a stage of this size.</summary>
    public FloorFrame Frame(float width, float height, (float L, float T, float R, float B) clear, string ownerWord)
    {
        var cam = Camera(clear);
        var frame = new FloorFrame
        {
            Width = width, Height = height, Time = _time, Camera = cam, Motion = Motion, Asleep = _asleep,
            Unit = cam.ScaleAt(cam.Target)
        };

        // SLABS, the lowest first, and on one level the furthest first: each stands on the one before it.
        var slabs = new List<(float Depth, FloorFrame.Slab Slab)>();
        foreach (var slab in _plan.Slabs)
        {
            var s = _slabs.GetValueOrDefault(slab.Unit);
            var born = Ease(s?.Born ?? 1);
            float z0 = slab.Z0, z1 = slab.Z0 + (slab.Z1 - slab.Z0) * born;
            Vector3 a = new(slab.X0, slab.Y0, 0), b = new(slab.X1, slab.Y0, 0), c = new(slab.X1, slab.Y1, 0), d = new(slab.X0, slab.Y1, 0);
            Vector2 P(Vector3 p, float z) => cam.Point(p with { Z = z });
            slabs.Add((slab.Level * 1000 - cam.Project(new Vector3((slab.X0 + slab.X1) / 2, (slab.Y0 + slab.Y1) / 2, z1)).Z, new FloorFrame.Slab(
                [P(a, z1), P(b, z1), P(c, z1), P(d, z1)],
                [P(a, z0), P(b, z0), P(b, z1), P(a, z1)],
                [P(b, z0), P(c, z0), P(c, z1), P(b, z1)],
                slab.Level, slab.Quiet, slab.Board, born, s?.Rim ?? 0, slab.Level == 0 && !slab.Board ? Seams(slab, cam) : [])));
        }
        frame.Slabs = [.. slabs.OrderBy(s => s.Depth).Select(s => s.Slab)];

        // NAMES, written on the slabs: along the floor's own across and standing up in its own away, so they lie on it.
        var names = new List<FloorFrame.Name>();
        foreach (var slab in _plan.Slabs)
        {
            var at = cam.Point(slab.NameAt);
            var along = cam.Point(slab.NameAt + new Vector3(1, 0, 0)) - at;
            var up = cam.Point(slab.NameAt + new Vector3(0, 1, 0)) - at;
            names.Add(new FloorFrame.Name(slab.Name, at, along, up, 0.34f, slab.NameRoom, Ease(_slabs.GetValueOrDefault(slab.Unit)?.Born ?? 1)
                * (slab.Quiet ? 0.55f : 1)));
        }
        frame.Names = [.. names];

        // LINES in the floor.
        var lines = new List<FloorFrame.Line>();
        foreach (var channel in _plan.Channels)
            lines.Add(new FloorFrame.Line([.. channel.Path.Select(cam.Point)], _warmth.GetValueOrDefault((channel.From, channel.To))));
        if (_plan.OwnerLine.Length > 0) lines.Add(new FloorFrame.Line([.. _plan.OwnerLine.Select(cam.Point)], _ownerLight));
        if (_plan.BoardLine.Length > 0)
            lines.Add(new FloorFrame.Line([.. _plan.BoardLine.Select(cam.Point)], _slabs.GetValueOrDefault(FloorChart.BoardUnit)?.Rim ?? 0));
        frame.Lines = [.. lines];

        // CELLS, the furthest first.
        var cells = new List<FloorFrame.Cell>();
        foreach (var cell in _plan.Cells)
        {
            var s = _cells.GetValueOrDefault(cell.Id);
            var born = Ease(s?.Born ?? 1);
            var half = cell.Size / 2;
            var h = cell.Height * born;
            var f = cell.Foot;
            Vector3[] foot =
            [
                new(f.X - half, f.Y - half, f.Z), new(f.X + half, f.Y - half, f.Z), new(f.X + half, f.Y + half, f.Z), new(f.X - half, f.Y + half, f.Z)
            ];
            var middle = cam.Project(f + new Vector3(0, 0, h / 2));
            cells.Add(new FloorFrame.Cell(cell.Id, cell.Kind,
                [.. foot.Select(cam.Point)], [.. foot.Select(p => cam.Point(p + new Vector3(0, 0, h)))],
                new Vector2(middle.X, middle.Y), cam.Point(f), cam.ScaleAt(f) * cell.Size,
                s?.Light ?? 0, s?.Flash ?? 0, born, cell.Active, (s?.Mark ?? 0) > 0.01f, s?.Mark ?? 0, s?.Tone ?? FloorTone.Work, s?.Flag ?? 0, middle.Z));
        }
        frame.Cells = [.. cells.OrderByDescending(c => c.Depth)];

        // THE OWNER'S PLACE: a ring on the floor behind the chief.
        if (_plan.Slabs.Count > 0)
        {
            const int n = 40;
            var ring = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var turn = i / (float)n * MathF.Tau;
                ring[i] = cam.Point(_plan.Owner + new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0) * 0.44f);
            }
            frame.Owner = ring;
            frame.OwnerAt = cam.Point(_plan.Owner);
            frame.OwnerLight = _ownerLight;
            frame.OwnerWord = ownerWord;
        }

        // THE TAPE: a strip in front of the organisation, and on it each minute's close standing as high as it was.
        if (_tape is { Heights.Length: > 1 } tape && TapeStrip() is { } lies && _tapeBorn > 0.01f)
        {
            var born = Ease(_tapeBorn);
            var z1 = FloorPlan.Step * born;
            Vector2 P(Vector3 p, float z) => cam.Point(p with { Z = z });
            var c = lies.Corners;
            // It faces the camera squarely, so its two ends are seen edge on: it has a top and a front, and no side to draw.
            var strip = new FloorFrame.Slab([P(c[0], z1), P(c[1], z1), P(c[2], z1), P(c[3], z1)], [P(c[0], 0), P(c[1], 0), P(c[1], z1), P(c[0], z1)], [],
                0, false, true, born, 0, []);
            var n = tape.Heights.Length;
            Vector3 from = Vector3.Lerp(Vector3.Lerp(c[0], c[3], 0.42f), Vector3.Lerp(c[1], c[2], 0.42f), 0.03f),
                to = Vector3.Lerp(Vector3.Lerp(c[0], c[3], 0.42f), Vector3.Lerp(c[1], c[2], 0.42f), 0.97f);
            Vector2[] foot = new Vector2[n], top = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var at = Vector3.Lerp(from, to, i / (float)(n - 1));
                foot[i] = P(at, z1);
                top[i] = P(at, z1 + (0.1f + 0.86f * Math.Clamp(tape.Heights[i], 0, 1)) * born);
            }
            frame.Tape = new FloorFrame.TapeDraw(strip, foot, top, tape.Caption, tape.Stale, born, _tapeFresh);
        }

        // WHAT IS TRAVELLING.
        var flights = new List<FloorFrame.Flight>();
        foreach (var t in _travels)
        {
            var progress = Math.Clamp(t.Age / t.Lasts, 0, 1);
            var head = EaseInOut(progress) * t.Length;
            var tail = Math.Min(t.Length * 0.45f, 2.6f);
            var fade = t.Age > t.Lasts ? 1 - (t.Age - t.Lasts) / 0.35f : Math.Min(1, t.Age / 0.15f);
            const int samples = 14;
            var trail = new Vector2[samples];
            for (var i = 0; i < samples; i++)
                trail[i] = cam.Point(At(t, Math.Max(0, head - tail * i / (samples - 1))));
            flights.Add(new FloorFrame.Flight(trail, Math.Clamp(fade, 0, 1), t.Tone, t.Size, t.Air));
        }
        frame.Flights = [.. flights];

        var rings = new List<FloorFrame.Ring>();
        foreach (var r in _ripples)
        {
            var p = r.Age / r.Lasts;
            var radius = 0.3f + EaseOut(p) * r.Reach;
            const int n = 36;
            var points = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var turn = i / (float)n * MathF.Tau;
                points[i] = cam.Point(r.At + new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0) * radius);
            }
            rings.Add(new FloorFrame.Ring(points, (1 - p) * (1 - p), r.Tone));
        }
        frame.Rings = [.. rings];

        var sparks = new List<FloorFrame.Spark>();
        foreach (var fleck in _flecks)
        {
            var p = fleck.Age / fleck.Lasts;
            var rise = EaseOut(p) * fleck.Rise;
            sparks.Add(new FloorFrame.Spark(cam.Point(fleck.At + new Vector3(0, 0, Math.Max(0, rise - 0.28f))), cam.Point(fleck.At + new Vector3(0, 0, rise)),
                1 - p, fleck.Tone));
        }
        frame.Sparks = [.. sparks];
        return frame;
    }

    /// <summary>
    /// The joints of the organisation's own slab: it is laid in panels two cells wide, and the joints between them — pairs of points,
    /// end to end — are what gives a wide dark surface a size the eye can read.
    /// </summary>
    static Vector2[] Seams(FloorPlan.Slab slab, FloorCamera cam)
    {
        const float panel = 2 * FloorPlan.Pitch;
        var points = new List<Vector2>();
        for (var x = slab.X0 + panel; x < slab.X1 - 0.2f; x += panel)
        {
            points.Add(cam.Point(new Vector3(x, slab.Y0, slab.Z1)));
            points.Add(cam.Point(new Vector3(x, slab.Y1, slab.Z1)));
        }
        for (var y = slab.Y1 - panel; y > slab.Y0 + 0.2f; y -= panel)
        {
            points.Add(cam.Point(new Vector3(slab.X0, y, slab.Z1)));
            points.Add(cam.Point(new Vector3(slab.X1, y, slab.Z1)));
        }
        return [.. points];
    }

    /// <summary>
    /// WHERE THE TAPE LIES: a strip in front of the organisation's slab, off it — the market is no part of the organisation — and
    /// squared to where the floor is looked at from, not to the floor's own lines. So its length runs level across the screen, and a
    /// price that rose is drawn rising: on a strip laid along the organisation's slanted edge, a rising market would be drawn as a line
    /// going down. Its corners (near-left, near-right, far-right, far-left) and its two directions; null on an empty floor.
    /// </summary>
    (Vector3[] Corners, Vector3 Along, Vector3 Away)? TapeStrip()
    {
        if (_plan.Ground is not { } ground) return null;
        float cy = MathF.Cos(BaseYaw), sy = MathF.Sin(BaseYaw);
        Vector3 along = new(cy, sy, 0), away = new(-sy, cy, 0);
        float left = float.MaxValue, right = float.MinValue, nearest = float.MaxValue;
        foreach (var corner in (Vector3[])[new(ground.X0, ground.Y0, 0), new(ground.X1, ground.Y0, 0), new(ground.X1, ground.Y1, 0), new(ground.X0, ground.Y1, 0)])
        {
            left = Math.Min(left, Vector3.Dot(corner, along));
            right = Math.Max(right, Vector3.Dot(corner, along));
            nearest = Math.Min(nearest, Vector3.Dot(corner, away));
        }
        const float deep = 0.62f, gap = 0.22f;
        var inset = (right - left) * 0.13f;
        float a0 = left + inset, a1 = right - inset, far = nearest - gap, near = far - deep;
        return ([along * a0 + away * near, along * a1 + away * near, along * a1 + away * far, along * a0 + away * far], along, away);
    }

    static Vector3 At(Travel t, float along)
    {
        var path = t.Path;
        for (var i = 1; i < path.Length; i++)
        {
            if (along > t.Along[i]) continue;
            var span = t.Along[i] - t.Along[i - 1];
            return span <= 0 ? path[i] : Vector3.Lerp(path[i - 1], path[i], (along - t.Along[i - 1]) / span);
        }
        return path[^1];
    }

    static float Ease(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    static float EaseOut(float t) => 1 - (1 - Math.Clamp(t, 0, 1)) * (1 - Math.Clamp(t, 0, 1));
    static float EaseInOut(float t) => t < 0.5f ? 2 * t * t : 1 - MathF.Pow(-2 * t + 2, 2) / 2;
}
