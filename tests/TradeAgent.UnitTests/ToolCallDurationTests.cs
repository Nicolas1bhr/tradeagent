using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-DOOR-BOUNDS ITEM 4: EVERY CALL THROUGH THE HARNESS'S SURFACE RECORDS ITS OWN WALL TIME (<c>tool_call.duration_ms</c>, rung
/// <see cref="Versions.ToolCallDurationRung"/>) — served, refused and stopped alike — a home climbing from the rung before keeps every row, and
/// the Work page names a slow call in words. The door's verbs are timed the same way (<c>BrokerDoorTests</c>).
/// </summary>
public class ToolCallDurationTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    /// <summary>The timer's own grain (about 15.6 ms on Windows) and the row's whole milliseconds: a delay may end that much early by the stopwatch.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    /// <summary>The gateway's refusal of a verdict it stopped, as the pipe answers one: unavailable, in its words.</summary>
    const string Stopped = "TradeAgent stopped this verdict before it answered";

    /// <summary>
    /// A gateway that answers after <see cref="Delay"/>: served for <c>status</c>, a verdict STOPPED as the gateway refuses one it stopped
    /// (<c>IPC_UNAVAILABLE</c>), refused for every other op — and honours the caller's own cancellation.
    /// </summary>
    sealed class Slow : IGatewayCalls
    {
        public async Task<IpcResponse> CallAsync(IpcRequest req, string? role, string? attemptId, CancellationToken ct = default)
        {
            await Task.Delay(Delay, ct);
            return req.Op == Ops.Status ? IpcResponse.Success(req.RequestId ?? "", new { ok = true })
                : req.Op == Ops.Verdict ? IpcResponse.Fail(req.RequestId ?? "", ErrorCode.IPC_UNAVAILABLE, Stopped)
                : IpcResponse.Fail(req.RequestId ?? "", ErrorCode.INVALID_REQUEST, "not this one");
        }
    }

    /// <summary>
    /// A SERVED, A REFUSED AND A STOPPED CALL EACH RECORD HOW LONG THEY RAN: at least the gateway's delay, measured by the surface itself. The
    /// stopped one is a verdict the gateway stopped, refused in its own words. A call the caller itself cancels still writes no row, as before:
    /// the oversight reads every unserved row as a refusal. RED at base: no row carried a duration.
    /// </summary>
    [Fact]
    public async Task A_served_a_refused_and_a_stopped_call_each_record_their_duration()
    {
        using var db = TestEnv.NewDb();
        var ledger = new ToolCallStore(db);
        var tools = new GrantedWorkerTools(CouncilRoles.Research, () => TestEnv.Home, () => "attempt-durations", gateway: () => new Slow(),
            ledger: ledger, now: () => Now);

        Assert.True((await tools.InvokeAsync(new ToolRequest("1", GrantedWorkerTools.Trade, $"{{\"op\":\"{Ops.Status}\"}}"))).Served);
        Assert.False((await tools.InvokeAsync(new ToolRequest("2", GrantedWorkerTools.Trade, $"{{\"op\":\"{Ops.Positions}\"}}"))).Served);
        Assert.False((await tools.InvokeAsync(new ToolRequest("3", GrantedWorkerTools.Trade, $"{{\"op\":\"{Ops.Verdict}\"}}"))).Served);
        using (var stop = new CancellationTokenSource(Delay / 3))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tools.InvokeAsync(new ToolRequest("4", GrantedWorkerTools.Trade, $"{{\"op\":\"{Ops.Status}\"}}"), stop.Token));

        var rows = ledger.ForAttempt("attempt-durations");
        Assert.Equal(3, rows.Count);
        var (served, refused, stopped) = (rows[0], rows[1], rows[2]);
        Assert.True(served.Served);
        Assert.False(refused.Served);
        Assert.False(stopped.Served);
        Assert.Contains(Stopped, stopped.Refusal);
        Assert.Contains(Ops.Verdict, stopped.Argument);
        foreach (var (what, row) in new[] { ("served", served), ("refused", refused), ("stopped", stopped) })
            Assert.True(row.Duration >= Delay - Tick && row.Duration < Delay * 40, $"the {what} call recorded {row.Duration}");

        // A CALL THE SURFACE ANSWERS ITSELF IS TIMED TOO: a tool this launch was not granted.
        Assert.False((await tools.InvokeAsync(new ToolRequest("5", "shell", "{}"))).Served);
        Assert.NotNull(ledger.ForAttempt("attempt-durations")[3].Duration);
    }

    /// <summary>
    /// A HOME CLIMBING FROM THE RUNG BEFORE KEEPS EVERY ROW: a home stamped one rung below, its <c>tool_call</c> without the column, reopened by
    /// this build — every table holds the rows it held, the old call keeps no duration (never backfilled), and a new call records one.
    /// </summary>
    [Fact]
    public void A_home_climbing_from_the_rung_before_keeps_every_row()
    {
        var path = Path.Combine(TestEnv.Home, $"db-{Guid.NewGuid():n}.db");
        Dictionary<string, long> before;
        using (var db = new Database(path))
        {
            new ToolCallStore(db).Record(new ToolCallRow
            {
                At = Now, Attempt = "attempt-old", Role = CouncilRoles.Research, Tool = "trade", Argument = "trade status", Bytes = 12, Served = true
            });
            Exec(db, "ALTER TABLE tool_call DROP COLUMN duration_ms");
            Exec(db, $"UPDATE meta SET value='{(Versions.ToolCallDurationRung - 1).ToString(CultureInfo.InvariantCulture)}' WHERE key='schema_version'");
            before = Counts(db);
        }

        using var reopened = new Database(path);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            reopened.Read(_ => { using var c = reopened.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
        var after = Counts(reopened);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (table, rows) in before) Assert.True(after[table] == rows, $"{table} held {rows} rows and holds {after[table]}");
        var old = Assert.Single(new ToolCallStore(reopened).ForAttempt("attempt-old"));
        Assert.Null(old.Duration);
        Assert.Equal(("trade status", 12L, true), (old.Argument, old.Bytes, old.Served));

        new ToolCallStore(reopened).Record(new ToolCallRow
        {
            At = Now, Attempt = "attempt-new", Role = CouncilRoles.Research, Tool = "trade", Served = true, Duration = TimeSpan.FromMilliseconds(1234)
        });
        Assert.Equal(TimeSpan.FromMilliseconds(1234), Assert.Single(new ToolCallStore(reopened).ForAttempt("attempt-new")).Duration);
    }

    /// <summary>
    /// THE WORK PAGE NAMES A SLOW CALL IN WORDS: the calls of the last day that took a quarter or more of the AI's per-call wait, slowest first —
    /// who, which tool, how long, how it ended — and what the wait is; one past it is said to have been told it failed. A quick call, one older
    /// than a day, and one with no recorded duration are not named; with none slow the line is empty and hidden.
    /// </summary>
    [Fact]
    public void The_work_page_names_a_slow_call_in_words()
    {
        using var db = TestEnv.NewDb();
        var ledger = new ToolCallStore(db);
        var limit = BrokerSeat.DoorToolTimeout;
        void Call(string role, string tool, TimeSpan? took, bool served, DateTimeOffset at, string? refusal = null) => ledger.Record(new ToolCallRow
        {
            At = at, Attempt = "a-" + Guid.NewGuid().ToString("n")[..6], Role = role, Tool = tool, Served = served, Duration = took,
            Refusal = served ? null : refusal ?? "refused"
        });

        Assert.Equal("", WorkWords.Of(WorkRead.From(new OrgStore(db), Now, "USD"), null).Slow);
        Call(CouncilRoles.Research, "trade", TimeSpan.FromSeconds(2), true, Now.AddMinutes(-5));
        Call(CouncilRoles.Research, "trade", null, true, Now.AddMinutes(-5));
        Call(CouncilRoles.Research, "trade", limit, true, Now.AddDays(-2));
        Assert.Equal("", WorkWords.Of(WorkRead.From(new OrgStore(db), Now, "USD"), null).Slow);

        Call(CouncilRoles.Research, "trade", limit / 4, true, Now.AddHours(-3));
        Call(CouncilRoles.Operations, "data", limit + TimeSpan.FromSeconds(20), false, Now.AddHours(-1), "IPC_UNAVAILABLE: stopped");
        var slow = WorkWords.Of(WorkRead.From(new OrgStore(db), Now, "USD"), null).Slow;

        var research = CouncilRoles.Title(CouncilRoles.Research);
        var operations = CouncilRoles.Title(CouncilRoles.Operations);
        Assert.Equal(
            $"Slow tool calls in the last day: {operations}'s 'data' call took 5 min 20 s and was refused; {research}'s 'trade' call took 1 min 15 s "
            + "and was answered. An AI working through TradeAgent's door waits 5 min for one call. One ran past that: an AI working through "
            + "TradeAgent's door is told such a call failed, although TradeAgent finished it.", slow);

        var page = new WorkPage(new OrgSurfaceSources { Org = () => new OrgStore(db), Attempts = () => new AiAttemptStore(db), Currency = () => "USD", Now = () => Now });
        Assert.False(page.SlowText.IsVisible);
        page.Update();
        Assert.True(page.SlowText.IsVisible);
        Assert.Equal(slow, page.SlowText.Text);
        Assert.Contains(page.Root.GetLogicalDescendants().OfType<TextBlock>(), t => ReferenceEquals(t, page.SlowText));
    }

    static Dictionary<string, long> Counts(Database db) => db.Read(_ =>
    {
        var tables = new List<string>();
        using (var c = db.Cmd("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
        using (var r = c.ExecuteReader())
            while (r.Read()) tables.Add(r.GetString(0));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            using var c = db.Cmd($"SELECT COUNT(*) FROM \"{t}\"");
            counts[t] = (long)c.ExecuteScalar()!;
        }
        return counts;
    });

    static void Exec(Database db, string sql) => db.Write(_ =>
    {
        using var c = db.Cmd(sql);
        return c.ExecuteNonQuery();
    });
}
