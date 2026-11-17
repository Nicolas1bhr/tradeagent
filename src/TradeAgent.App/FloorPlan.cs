using System.Numerics;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>What a cell on the floor is: the chief's, a unit head's, a member's, a security-board seat's — or the empty place where a unit's head would stand.</summary>
enum FloorCellKind { Chief, Head, Member, Board, Vacant }

/// <summary>One unit of the chart as the floor draws it.</summary>
sealed record FloorChartUnit(string Id, string? Parent, string Name, bool Quiet, string? Head);

/// <summary>
/// One seat as the floor draws it: a position, or a security-board seat. <see cref="Short"/> is its name without the unit it stands
/// in — the unit's name is written on the slab under it, so a caption beside the cell need not say it again.
/// </summary>
sealed record FloorChartSeat(string Id, string Unit, string Name, FloorCellKind Kind, bool Active)
{
    public string Short { get; init; } = Name;
}

/// <summary>
/// THE CHART AS THE FLOOR NEEDS IT (<c>U-ui-alive</c>): the open units, the positions that have not ended and the board's seats, under the
/// names every other page calls them by (<see cref="OrgNames"/>). Composed from the chart's own rows and nothing else.
/// </summary>
sealed record FloorChart(IReadOnlyList<FloorChartUnit> Units, IReadOnlyList<FloorChartSeat> Seats, IReadOnlyList<FloorChartSeat> Board)
{
    public static readonly FloorChart Empty = new([], [], []);

    /// <summary>The unit a security-board seat is drawn in. Not a row of the chart: the board stands outside it.</summary>
    public const string BoardUnit = "board";

    /// <summary>Everything that decides where anything stands or how it is named: a new chart with the same signature needs no new plan.</summary>
    public string Signature { get; } =
        string.Join('|', Units.Select(u => $"{u.Id}:{u.Parent}:{u.Head}:{u.Quiet}:{u.Name}")) + "#"
        + string.Join('|', Seats.Select(s => $"{s.Id}:{s.Unit}:{s.Kind}:{s.Active}:{s.Name}")) + "#"
        + string.Join('|', Board.Select(s => $"{s.Id}:{s.Active}"));

    public static FloorChart From(IReadOnlyList<OrgUnitRow> units, IReadOnlyList<OrgPositionRow> positions, OrgNames names,
        IReadOnlyList<BoardSeat> board)
    {
        var open = units.Where(u => u.Status != OrgUnitStatus.Closed && u.ClosedAt is null).ToList();
        var standing = positions.Where(p => p.Status != OrgPositionStatus.Ended && p.EndedAt is null).ToList();
        var seated = standing.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var root = open.FirstOrDefault(u => u.IsRoot);

        var chartUnits = open.Select(u => new FloorChartUnit(u.Id, u.ParentId, names.Unit(u.Id), u.Status != OrgUnitStatus.Active,
            u.HeadPositionId is { } head && seated.Contains(head) ? head : null)).ToList();
        // A position is the head of the first unit that names it: a chart in which two units name one head is damaged, and is still drawn.
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in chartUnits)
            if (unit.Head is { } head) heads.TryAdd(head, unit.Id);
        var known = chartUnits.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);

        var seats = standing.Where(p => known.Contains(p.UnitId)).Select(p =>
        {
            var name = names.Position(p.Id);
            var unit = names.Unit(p.UnitId);
            // "Executor 2 · Research team 1" is "Executor 2" on Research team 1's slab, and "Research team 1 head" is its "Head".
            var cut = name.IndexOf(" · ", StringComparison.Ordinal);
            var brief = cut > 0 && name[(cut + 3)..] == unit ? name[..cut] : name == unit + " head" ? "Head" : name;
            return new FloorChartSeat(p.Id, p.UnitId, name,
                heads.TryGetValue(p.Id, out var headed) ? headed == root?.Id ? FloorCellKind.Chief : FloorCellKind.Head : FloorCellKind.Member,
                p.Status == OrgPositionStatus.Active) { Short = brief };
        }).ToList();

        var boardSeats = board.Where(s => s.Status != BoardSeatStatus.Retired)
            .Select(s => new FloorChartSeat(s.Id, BoardUnit, Labels.FloorBoardSeat(s.Id), FloorCellKind.Board, s.Status == BoardSeatStatus.Active))
            .ToList();
        return new FloorChart(chartUnits, seats, boardSeats);
    }
}

/// <summary>
/// WHERE EVERYTHING STANDS (<c>U-ui-alive</c>). The chart laid out as a place: each unit a low slab standing on its parent's, each seat a
/// cell on its unit's slab — the head at the back, its members in front of it, the units under it in a row in front of those. So the
/// organisation reads the way a chart does, the chief furthest and the executors nearest, and a unit holds what it holds by standing under it.
///
/// <para><b>It is decided by the chart alone and by nothing that changes while it runs</b>: the same rows give the same floor, a seat keeps
/// its place while its unit keeps its members, and a new one takes the next place rather than moving the others — a floor that reshuffled
/// itself every time somebody was hired would be a floor nobody could learn.</para>
///
/// <para>Distances are in cells: one is the width a member's cell stands in.</para>
/// </summary>
sealed class FloorPlan
{
    public const float Pitch = 1.26f, Pad = 0.42f, Gap = 0.42f, RowGap = 0.34f, LabelStrip = 0.66f, Step = 0.12f;

    /// <summary>How much wider than deep a unit that holds others tries to be: past it, the units under it start a second row.</summary>
    const float Spread = 2.4f;

    /// <summary>A unit's slab: its footprint (Y1 the back edge), the heights it stands between, and where its name is written.</summary>
    public sealed record Slab(string Unit, string Name, int Level, float X0, float Y0, float X1, float Y1, float Z0, float Z1, bool Quiet,
        bool Board, Vector3 NameAt, float NameRoom);

    /// <summary>A cell: whose it is, the middle of its foot, how wide it stands and how tall.</summary>
    public sealed record Cell(string Id, string Name, FloorCellKind Kind, Vector3 Foot, float Size, float Height, bool Active, string Unit)
    {
        /// <summary>Its name without its unit's, for a caption beside it.</summary>
        public string Short { get; init; } = Name;

        public Vector3 Middle => Foot + new Vector3(0, 0, Height / 2);
        public Vector3 Top => Foot + new Vector3(0, 0, Height);
    }

    /// <summary>A line laid in the floor from one cell to the one it answers to.</summary>
    public sealed record Channel(string From, string To, Vector3[] Path);

    public IReadOnlyList<Slab> Slabs { get; }
    public IReadOnlyList<Cell> Cells { get; }
    public IReadOnlyList<Channel> Channels { get; }
    public IReadOnlyDictionary<string, Cell> ById { get; }

    /// <summary>Where the owner stands: behind the chief, off the organisation's own slab.</summary>
    public Vector3 Owner { get; }

    /// <summary>The cell at the head of the organisation — the chief's, or the empty place where a chief would stand — or null on an empty floor.</summary>
    public string? Chief { get; }

    /// <summary>The line from the head of the organisation back to the owner's place; empty on an empty floor.</summary>
    public Vector3[] OwnerLine { get; } = [];

    /// <summary>The line from the security board's slab forward to the organisation's back edge; empty where there is no board.</summary>
    public Vector3[] BoardLine { get; } = [];

    /// <summary>The organisation's own slab — the root's — or null on an empty floor: what the tape is laid in front of.</summary>
    public Slab? Ground { get; }

    /// <summary>The corners of everything, for a camera to fit: the least and the most of each axis.</summary>
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    readonly Dictionary<string, string> _answersTo = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string), Vector3[]> _paths = new();

    public static readonly FloorPlan Empty = new(FloorChart.Empty);

    sealed class Box
    {
        public required FloorChartUnit Unit;
        public FloorChartSeat? Head;
        public List<FloorChartSeat> Members = [];
        public List<Box> Children = [];
        public int Cols, Rows;
        public float W, D, HeadDepth, MembersDepth, ChildrenWidth, ChildrenDepth;
        /// <summary>The units under this one, row by row from the back, each row with its own width and depth.</summary>
        public List<(List<Box> Boxes, float Width, float Depth)> Lines = [];
    }

    public FloorPlan(FloorChart chart)
    {
        var slabs = new List<Slab>();
        var cells = new List<Cell>();
        var channels = new List<Channel>();

        var root = chart.Units.FirstOrDefault(u => u.Parent is null);
        float rootX0 = 0, rootX1 = 0, rootY0 = 0;
        Vector3? chiefFoot = null;
        if (root is not null)
        {
            var under = chart.Units.Where(u => u.Parent is not null).GroupBy(u => u.Parent!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var seats = chart.Seats.GroupBy(s => s.Unit, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var box = Measure(root, under, seats, []);
            Place(box, -box.W / 2, 0, 0, null, slabs, cells, channels);
            rootX0 = -box.W / 2;
            rootX1 = box.W / 2;
            rootY0 = -box.D;
            // The root's own head is the first cell placed: the chief's, or the empty place where one would stand.
            Chief = cells[0].Id;
            chiefFoot = cells[0].Foot;
        }

        // THE SECURITY BOARD STANDS BEHIND THE ORGANISATION, beside the owner's place and never on the organisation's own slab: it is
        // outside the chart, and what it decides binds the whole of it. A line runs from it to the organisation's edge.
        var chiefX = chiefFoot?.X ?? 0;
        if (chart.Board.Count > 0)
        {
            var n = chart.Board.Count;
            float w = n * Pitch + 2 * Pad, d = Pad + Pitch + LabelStrip + Pad * 0.35f;
            float x0 = chiefX + 1.7f, y0 = 0.62f, y1 = y0 + d;
            slabs.Add(new Slab(FloorChart.BoardUnit, Labels.FloorBoard, 0, x0, y0, x0 + w, y1, 0, Step, false, true,
                new Vector3(x0 + Pad * 0.8f, y0 + Pad * 0.5f, Step), w - Pad * 1.6f));
            for (var i = 0; i < n; i++)
            {
                var s = chart.Board[i];
                cells.Add(new Cell(s.Id, s.Name, FloorCellKind.Board, new Vector3(x0 + Pad + Pitch * (i + 0.5f), y1 - Pad - Pitch / 2, Step),
                    0.72f, 0.7f, s.Active, FloorChart.BoardUnit));
            }
            BoardLine = [new Vector3(x0 + w / 2, y0, Step), new Vector3(x0 + w / 2, y0, 0), new Vector3(x0 + w / 2, 0, 0)];
        }

        Ground = root is null ? null : slabs[0];
        Slabs = slabs;
        Cells = cells;
        Channels = channels;
        ById = cells.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var c in channels) _answersTo[c.From] = c.To;

        Owner = chiefFoot is { } chief ? new Vector3(chief.X - 0.2f, 1.5f, 0) : new Vector3(0, 1.5f, 0);
        if (chiefFoot is { } foot)
            OwnerLine = [foot, new Vector3(foot.X, 0, foot.Z), new Vector3(foot.X, 0, 0), Owner];

        if (slabs.Count == 0)
        {
            Min = new Vector3(-4, -4, 0);
            Max = new Vector3(4, 2, 1);
        }
        else
        {
            Min = new Vector3(slabs.Min(s => s.X0), Math.Min(slabs.Min(s => s.Y0), rootY0), 0);
            Max = new Vector3(slabs.Max(s => s.X1), Math.Max(Owner.Y + 0.4f, slabs.Max(s => s.Y1)), cells.Count == 0 ? 1 : cells.Max(c => c.Foot.Z + c.Height));
        }
    }

    static Box Measure(FloorChartUnit unit, Dictionary<string, List<FloorChartUnit>> under, Dictionary<string, List<FloorChartSeat>> seats,
        HashSet<string> seen)
    {
        var box = new Box { Unit = unit };
        // A chart that is not one tree is drawn as far as it is one: a unit already on the way down is not drawn under itself.
        if (!seen.Add(unit.Id)) return box;
        var here = seats.TryGetValue(unit.Id, out var s) ? s : [];
        box.Head = here.FirstOrDefault(p => p.Id == unit.Head);
        box.Members = here.Where(p => p.Id != unit.Head).ToList();
        if (under.TryGetValue(unit.Id, out var kids))
            foreach (var kid in kids) box.Children.Add(Measure(kid, under, seats, seen));

        var m = box.Members.Count;
        box.Cols = m <= 4 ? m : (int)Math.Ceiling(Math.Sqrt(m * 1.8));
        box.Rows = m == 0 ? 0 : (int)Math.Ceiling(m / (double)box.Cols);
        box.HeadDepth = Pitch * (unit.Parent is null ? 1.28f : 1.1f);
        box.MembersDepth = box.Rows * Pitch;
        // THE UNITS UNDER IT, IN ROWS: side by side until the row is as wide as the whole would be deep, then a new row in front. A
        // chart is wider than it is deep; a floor that followed it would be a strip, and a strip fits no window.
        var area = box.Children.Sum(c => c.W * c.D);
        var widest = box.Children.Count == 0 ? 0 : box.Children.Max(c => c.W);
        var aim = Math.Max(widest, MathF.Sqrt(area * Spread));
        foreach (var child in box.Children)
        {
            if (box.Lines.Count == 0 || box.Lines[^1].Width + Gap + child.W > aim + 0.01f)
                box.Lines.Add(([child], child.W, child.D));
            else
            {
                var line = box.Lines[^1];
                line.Boxes.Add(child);
                box.Lines[^1] = (line.Boxes, line.Width + Gap + child.W, Math.Max(line.Depth, child.D));
            }
        }
        box.ChildrenWidth = box.Lines.Count == 0 ? 0 : box.Lines.Max(l => l.Width);
        box.ChildrenDepth = box.Lines.Sum(l => l.Depth) + Gap * Math.Max(0, box.Lines.Count - 1);

        // A unit that holds others is at least wide enough for its name beside its head; a leaf carries its name along its front.
        var inner = Math.Max(Math.Max(Pitch * 1.25f, box.Cols * Pitch), box.ChildrenWidth);
        if (box.Children.Count > 0) inner = Math.Max(inner, Pitch * 4.2f);
        box.W = inner + 2 * Pad;
        box.D = box.Children.Count > 0
            ? Pad + box.HeadDepth + box.MembersDepth + RowGap + box.ChildrenDepth + Pad
            : Pad + box.HeadDepth + box.MembersDepth + LabelStrip + Pad * 0.35f;
        return box;
    }

    /// <summary>Places a measured unit with its back-left corner at (<paramref name="x0"/>, <paramref name="y1"/>); returns its head's cell id.</summary>
    static string Place(Box box, float x0, float y1, int level, string? answersTo, List<Slab> slabs, List<Cell> cells, List<Channel> channels)
    {
        float z0 = level * Step, z1 = z0 + Step;
        float x1 = x0 + box.W, y0 = y1 - box.D, mid = x0 + box.W / 2;
        var headY = y1 - Pad - box.HeadDepth / 2;
        var holds = box.Children.Count > 0;

        slabs.Add(new Slab(box.Unit.Id, box.Unit.Name, level, x0, y0, x1, y1, z0, z1, box.Unit.Quiet, false,
            holds ? new Vector3(x0 + Pad * 0.8f, headY - 0.2f, z1) : new Vector3(x0 + Pad * 0.8f, y0 + Pad * 0.5f, z1),
            holds ? box.W / 2 - Pitch * 0.62f - Pad * 0.8f : box.W - Pad * 1.6f));

        var root = box.Unit.Parent is null;
        var headId = box.Head?.Id ?? "vacant:" + box.Unit.Id;
        var headFoot = new Vector3(mid, headY, z1);
        cells.Add(box.Head is { } head
            ? new Cell(head.Id, head.Name, root ? FloorCellKind.Chief : FloorCellKind.Head, headFoot, root ? 1.06f : 0.92f, root ? 1.36f : 1.08f,
                head.Active, box.Unit.Id) { Short = head.Short }
            : new Cell(headId, Labels.FloorVacant(box.Unit.Name), FloorCellKind.Vacant, headFoot, root ? 1.06f : 0.92f, 0, false, box.Unit.Id));

        // A head answers to the head of the unit it stands in: the line runs back off its own slab and along the row to that head.
        if (answersTo is not null && cells.FirstOrDefault(c => c.Id == answersTo) is { } above)
        {
            var back = y1 + RowGap / 2;
            channels.Add(new Channel(headId, answersTo,
            [
                headFoot, new Vector3(mid, y1, z1), new Vector3(mid, y1, z0), new Vector3(mid, back, z0), new Vector3(above.Foot.X, back, z0), above.Foot
            ]));
        }

        var membersTop = y1 - Pad - box.HeadDepth;
        var join = membersTop + Pitch * 0.04f;
        for (var i = 0; i < box.Members.Count; i++)
        {
            int row = i / box.Cols, col = i % box.Cols;
            var inRow = Math.Min(box.Cols, box.Members.Count - row * box.Cols);
            var foot = new Vector3(mid + (col - (inRow - 1) / 2f) * Pitch, membersTop - (row + 0.5f) * Pitch, z1);
            var member = box.Members[i];
            cells.Add(new Cell(member.Id, member.Name, FloorCellKind.Member, foot, 0.8f, 0.86f, member.Active, box.Unit.Id) { Short = member.Short });
            // Between the columns, never under the cells in front of it: a member in a second row runs up the aisle to its side.
            var aisle = row == 0 ? foot.X : foot.X + Pitch / 2 * (col < inRow / 2f ? -1 : 1);
            channels.Add(new Channel(member.Id, headId, row == 0
                ? [foot, new Vector3(foot.X, join, z1), new Vector3(mid, join, z1), headFoot]
                : [foot, new Vector3(aisle, foot.Y, z1), new Vector3(aisle, join, z1), new Vector3(mid, join, z1), headFoot]));
        }

        var lineBack = membersTop - box.MembersDepth - RowGap;
        foreach (var (boxes, width, depth) in box.Lines)
        {
            var x = x0 + (box.W - width) / 2;
            foreach (var child in boxes)
            {
                Place(child, x, lineBack, level + 1, headId, slabs, cells, channels);
                x += child.W + Gap;
            }
            lineBack -= depth + Gap;
        }
        return headId;
    }

    /// <summary>
    /// THE WAY FROM ONE CELL TO ANOTHER ALONG THE LINES IN THE FLOOR — up from each to the first cell both answer to, by way of that — or
    /// null where no line joins them (a board seat, the owner, a cell that is not on the floor).
    /// </summary>
    public Vector3[]? Route(string from, string to)
    {
        if (from == to || !ById.ContainsKey(from) || !ById.ContainsKey(to)) return null;
        if (_paths.TryGetValue((from, to), out var known)) return known;

        var up = Chain(from);
        var down = Chain(to);
        var meet = up.FirstOrDefault(down.Contains);
        if (meet is null) return null;

        var path = new List<Vector3>();
        foreach (var hop in Hops(up, meet)) path.AddRange(hop.Path);
        foreach (var hop in Hops(down, meet).Reverse()) path.AddRange(hop.Path.Reverse());
        var route = Dedupe(path);
        _paths[(from, to)] = route;
        return route;

        List<string> Chain(string id)
        {
            var chain = new List<string> { id };
            while (_answersTo.TryGetValue(chain[^1], out var next) && !chain.Contains(next)) chain.Add(next);
            return chain;
        }

        IEnumerable<Channel> Hops(List<string> chain, string until)
        {
            for (var i = 0; i < chain.Count - 1 && chain[i] != until; i++)
                yield return Channels.First(c => c.From == chain[i] && c.To == chain[i + 1]);
        }
    }

    static Vector3[] Dedupe(List<Vector3> path)
    {
        var kept = new List<Vector3>(path.Count);
        foreach (var p in path)
            if (kept.Count == 0 || Vector3.DistanceSquared(kept[^1], p) > 1e-6f) kept.Add(p);
        return [.. kept];
    }
}
