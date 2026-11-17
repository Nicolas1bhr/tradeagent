using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// THE FLOOR, REPLAYED (<c>U-ui-alive</c>). The organisation works at the pace of its turns — a few an hour, some hours none — so what it
/// did overnight is not something anybody can sit and watch. A replay shows it: the same rows the floor shows as they are written, read
/// back for a stretch of time and played through in about a minute, the stretches where nothing was open skipped.
///
/// <para><b>It is the records, and it says so.</b> While it runs the page says it is a replay and of what moment, every sentence beside it
/// carries the time it was recorded, and the figures about now are put away; the header above the page goes on saying what is true now.
/// Nothing is written and nothing is started: it is a read of a window of the ledgers, played back.</para>
///
/// <para>The chart is drawn from the seats that stand today, each appearing at the moment it was founded. A seat that has since been ended
/// is not drawn; what it did is still said in the sentences.</para>
/// </summary>
sealed partial class FloorPage
{
    /// <summary>About how long a replay runs, whatever stretch it covers.</summary>
    static readonly TimeSpan ReplayRuns = TimeSpan.FromSeconds(70);

    internal sealed class Replaying
    {
        public required DateTimeOffset From, To;
        public required IReadOnlyList<FloorTurn> Turns;
        public required IReadOnlyList<FloorEvent> Events;
        public required IReadOnlyDictionary<string, DateTimeOffset> Founded;
        public required IReadOnlyList<OrgUnitRow> Units;
        public required IReadOnlyList<OrgPositionRow> Positions;
        public required IReadOnlyList<BoardSeat> Board;
        public required OrgNames Names;
        public DateTimeOffset At;
        public DateTimeOffset? NextFounding;
        public int Next;
        public double Speed;
        public bool Ended;
        public double EndedFor;
    }

    Replaying? _replay;
    DispatcherTimer? _replayTimer;
    long _replayLast;
    List<FloorLine>? _liveLines;
    string _liveSignature = "";

    readonly TextBlock _replayNote = Ui.Micro(Labels.FloorReplayNote);
    Control? _replayChoices;
    Button? _replayBack;

    /// <summary>The replay's own controls, under the title: which stretch to play, and — while one plays — the way back to now.</summary>
    Control BuildReplay()
    {
        Button Choice(string label, TimeSpan? span) => Ui.With(Ui.Ghost(label, () => StartReplay(span)), b =>
        {
            b.FontSize = Theme.Small;
            b.Padding = new Thickness(Theme.S2, 5);
        });
        var word = Ui.With(Ui.Row(Theme.S1, Icons.Of(Icons.Replay, Theme.TextFaint, 14),
                new TextBlock { Text = Labels.FloorReplay, FontSize = Theme.Small, Foreground = Theme.TextFaint, VerticalAlignment = VerticalAlignment.Center }),
            r => { r.VerticalAlignment = VerticalAlignment.Center; r.Margin = new Thickness(Theme.S2, 0, Theme.S1, 0); });
        _replayChoices = Ui.Row(0, word, Choice(Labels.FloorReplayHour, TimeSpan.FromHours(1)), Choice(Labels.FloorReplayDay, TimeSpan.FromHours(24)),
            Choice(Labels.FloorReplayAll, null));
        _replayBack = Ui.With(Ui.Secondary(Labels.FloorReplayStop, StopReplay), b =>
        {
            b.FontSize = Theme.Small;
            b.Padding = new Thickness(Theme.S3, 6);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(Theme.S2, Theme.S1, 0, 0);
            b.IsVisible = false;
        });
        _replayNote.IsVisible = false;
        _replayNote.Margin = new Thickness(Theme.S2, 0, 0, 0);
        _replayNote.MaxWidth = 420;
        return Ui.Col(Theme.S1, _replayChoices, _replayNote, _replayBack);
    }

    /// <summary>Reads the stretch — off the UI thread — and begins playing it. Null: everything recorded.</summary>
    void StartReplay(TimeSpan? span)
    {
        if (_replay is not null || _reading) return;
        _reading = true;
        _replayNote.Text = Labels.FloorReplayLoading;
        _replayNote.IsVisible = true;
        _ = Task.Run(() =>
        {
            Replaying? read = null;
            string? problem = null;
            try { read = ReadReplay(span); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                problem = ex is TradeAgentException t ? t.Info.UserMessage : ex.Message;
            }
            Dispatcher.UIThread.Post(() =>
            {
                _reading = false;
                if (read is null || read.Events.Count == 0)
                {
                    _replayNote.Text = problem is null ? Labels.FloorReplayNothing : Labels.FloorNotRead + " " + problem;
                    return;
                }
                Safely(() => BeginReplay(read));
                if (_replay is null) return;
                _replayLast = Stopwatch.GetTimestamp();
                _replayTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    var dt = Stopwatch.GetElapsedTime(_replayLast, now).TotalSeconds;
                    _replayLast = now;
                    Safely(() => ReplayStep(Math.Min(dt, 0.25)));
                });
                _replayTimer.Start();
            });
        });
    }

    internal Replaying? ReadReplay(TimeSpan? span)
    {
        var org = _sources.Org();
        var floor = _sources.Floor();
        if (org is null || floor is null) return null;
        var now = _sources.Now();
        var from = span is { } stretch ? now - stretch : (floor.Span().First ?? now) - TimeSpan.FromSeconds(1);
        var units = org.Units();
        var positions = org.Positions();
        // Tool calls only over a short stretch: over a day there are thousands, and at that speed each would be one frame of noise.
        return new Replaying
        {
            From = from, To = now, Turns = floor.Turns(from, now), Events = floor.Events(from, now, 6000, tools: now - from <= TimeSpan.FromHours(3)),
            Founded = floor.Founded(), Units = units, Positions = positions, Board = _sources.Board(), Names = OrgNames.Of(units, positions)
        };
    }

    /// <summary>The replay begins: the floor goes dark, the sentences are put aside, and the clock starts at the stretch's first record.</summary>
    internal void BeginReplay(Replaying replay)
    {
        _replay = replay;
        replay.Speed = Math.Max(4, (replay.To - replay.From).TotalSeconds / ReplayRuns.TotalSeconds);
        var first = replay.Events.Count > 0 ? replay.Events[0].At : replay.From;
        replay.At = first - TimeSpan.FromSeconds(replay.Speed * 0.8) is var lead && lead > replay.From ? lead : replay.From;

        _look.Stop();
        _liveLines = [.. _lines];
        _liveSignature = _chartSignature;
        _lines.Clear();
        _lastTool.Clear();
        _openSince.Clear();
        _names = replay.Names;
        _chosen = null;
        _replayChoices!.IsVisible = false;
        _replayBack!.IsVisible = true;
        _replayNote.Text = Labels.FloorReplayNote;
        _replayNote.IsVisible = true;
        _start.IsVisible = false;
        _next.IsVisible = false;
        _figures.Opacity = 0;
        _state.Foreground = Theme.Accent;
        ChartAt(replay);
        ReplayShow(replay);
    }

    /// <summary>The replay's clock moves on by <paramref name="seconds"/> of the owner's own time: everything recorded up to where it now stands is shown.</summary>
    internal void ReplayStep(double seconds)
    {
        if (_replay is not { } replay) return;
        if (replay.Ended)
        {
            // Left standing for a moment at its end, then the floor is the present's again.
            replay.EndedFor += seconds;
            if (replay.EndedFor > 3 && !Manual) StopReplay();
            return;
        }

        replay.At += TimeSpan.FromSeconds(seconds * replay.Speed);
        var open = replay.Turns.Any(t => t.StartedAt <= replay.At && (t.EndedAt ?? replay.To) > replay.At);
        // NOTHING OPEN AND NOTHING SOON: the stretch with nothing in it is skipped, to just before the next thing recorded.
        if (!open && replay.Next < replay.Events.Count)
        {
            var next = replay.Events[replay.Next].At;
            if (next - replay.At > TimeSpan.FromSeconds(replay.Speed * 1.2)) replay.At = next - TimeSpan.FromSeconds(replay.Speed * 0.6);
        }
        else if (!open && replay.Next >= replay.Events.Count) replay.At = replay.To;
        if (replay.At > replay.To) replay.At = replay.To;

        if (replay.NextFounding is { } founding && replay.At >= founding) ChartAt(replay);
        while (replay.Next < replay.Events.Count && replay.Events[replay.Next].At <= replay.At)
            Happened(replay.Events[replay.Next++], moving: true);
        if (replay.At >= replay.To) replay.Ended = true;
        ReplayShow(replay);
    }

    /// <summary>The chart as it stood at the replay's clock: every unit and seat founded by then.</summary>
    void ChartAt(Replaying replay)
    {
        bool Stood(string id) => !replay.Founded.TryGetValue(id, out var at) || at <= replay.At;
        var units = replay.Units.Where(u => u.IsRoot || Stood(u.Id)).ToList();
        var positions = replay.Positions.Where(p => Stood(p.Id)).ToList();
        var chart = FloorChart.From(units, positions, replay.Names, replay.Board);
        replay.NextFounding = replay.Founded.Values.Where(at => at > replay.At).OrderBy(at => at).Cast<DateTimeOffset?>().FirstOrDefault();
        if (chart.Signature == _chartSignature) return;
        _chartSignature = chart.Signature;
        _stage.Scene.Lay(new FloorPlan(chart), atOnce: !_laid);
        _laid = true;
    }

    void ReplayShow(Replaying replay)
    {
        _openSince.Clear();
        foreach (var turn in replay.Turns.Where(t => t.StartedAt <= replay.At && (t.EndedAt ?? replay.To) > replay.At))
            _openSince[turn.Role] = turn.StartedAt;
        foreach (var gone in _lastTool.Keys.Where(k => !_openSince.ContainsKey(k)).ToList()) _lastTool.Remove(gone);
        _stage.Scene.Lit(_openSince.Keys.ToHashSet(StringComparer.Ordinal), running: true);
        _stage.Scene.Flag(new HashSet<string>());

        _state.Text = replay.Ended ? Labels.FloorReplayEnded : Labels.FloorReplaying(replay.At);
        _working.Text = Labels.FloorWorking([.. _openSince.OrderBy(o => o.Value).Select(o => FloorWords.Who(o.Key, _names))]);
        _working.IsVisible = true;
        Feed();
        _stage.Wake();
    }

    /// <summary>Back to now: the floor is the present's again, with the sentences it had.</summary>
    internal void StopReplay()
    {
        if (_replay is null) return;
        _replay = null;
        _replayTimer?.Stop();
        _lines.Clear();
        if (_liveLines is not null) _lines.AddRange(_liveLines);
        _liveLines = null;
        _lastTool.Clear();
        _openSince.Clear();
        _stage.Scene.Lit(new HashSet<string>(), running: !_asleep);
        // The chart is laid again from the next read, whatever the replay left standing.
        _chartSignature = "\0";
        _replayChoices!.IsVisible = true;
        _replayBack!.IsVisible = false;
        _replayNote.IsVisible = false;
        _figures.Opacity = 1;
        _state.Foreground = Theme.TextMuted;
        _state.Text = "";
        Feed();
        if (Manual) return;
        if (_shown) _look.Start();
        Read();
    }

    /// <summary>The clock a caption runs by: the replay's own while one plays, so a working cell's time is the time it had been open then.</summary>
    DateTimeOffset Clock() => _replay?.At ?? _sources.Now();
}
