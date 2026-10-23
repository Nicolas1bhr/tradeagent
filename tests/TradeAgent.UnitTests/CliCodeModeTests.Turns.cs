using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class CliCodeModeTests
{
    // ---- (C) the turn reader --------------------------------------------------------------------------------------------

    /// <summary>
    /// (C) CODEX'S <c>error</c> ITEM IS NOT A COMMAND. Red at base: <c>TurnMeter.IsToolItem</c> counted any item that was not a message
    /// or reasoning, so attempt 4's every turn read <c>command_items 1, tool_output_bytes 0</c> — a warning counted as work.
    /// </summary>
    [Fact]
    public void The_turn_reader_counts_codexs_error_item_as_no_command()
    {
        var read = TurnContext.Read(DeadTurn, promptChars: 10, usage: null);
        Assert.Equal(0, read.CommandItems);
        Assert.Equal(0, read.ToolOutputBytes);
    }

    /// <summary>
    /// (C) A TURN WHOSE STREAM SAYS ITS TOOLS CANNOT RUN IS REFUSED IN WORDS: the vendor's sentence is shown in the conversation —
    /// never swallowed as a tool line nobody reads — the turn ends carrying it and the warning, and the same seat's next launch is
    /// refused in the same words before admission. Red at base: nothing was shown and the next launch was admitted.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_stream_says_its_tools_cannot_run_shows_the_vendors_words()
    {
        var session = AgentRuntimeProbe.SessionOverStream(DeadTurn, manifest: VendorLimitTests.Codex,
            name: nameof(A_turn_whose_stream_says_its_tools_cannot_run_shows_the_vendors_words));
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        await session.SendAsync("continue");
        Assert.Contains(session.History, t => t.Role == ChatRole.System && t.Text.Contains(Warning, StringComparison.Ordinal));

        Assert.NotNull(ended);
        Assert.Equal([Warning], ended!.VendorWarnings);
        Assert.Contains(Warning, ended.ToolsUnavailable, StringComparison.Ordinal);
        Assert.Equal(ended.ToolsUnavailable, session.History.Last().Text);
        Assert.DoesNotContain(session.History, t => t.Role == ChatRole.Tool);

        var next = session.CheckSeat();
        Assert.True(next.Refused, "the seat whose tools could not run was admitted again unchanged");
        Assert.Equal(ended.ToolsUnavailable, next.Refusal);
    }

    /// <summary>
    /// (C) A TURN WHOSE TOOLS COULD NOT RUN GIVES BACK THE WAKES IT TOOK, as the gate's refusal does: the owner's words stay owed, shown
    /// with the app's sentence. At base the turn ended clean and settled them as answered.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_tools_could_not_run_gives_back_its_wakes()
    {
        var name = nameof(A_turn_whose_tools_could_not_run_gives_back_its_wakes);
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var session = AgentRuntimeProbe.SessionOverStream(DeadTurn, manifest: VendorLimitTests.Codex, name: name);
        var now = TestEnv.LocalNoon();
        var home = Path.Combine(TestEnv.Home, "cli-code-mode", name);
        Directory.CreateDirectory(Path.Combine(home, ".tradeagent"));
        var host = new VendorLimitTests.Host(db, events, session, home) { Clock = () => now };
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);

        Assert.True(events.RecordOwnerMessage("how is the book?", now));
        await loop.TurnAsync();
        Assert.Single(host.Opened);
        var owed = Assert.Single(events.Due(now.AddHours(1)), e => e.Kind == MissionEventKind.Owner);
        Assert.Contains("how is the book?", owed.Payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SESSION OVER <see cref="DeadTurn"/> WHOSE PROCESS IS STILL RUNNING AFTER IT PRINTED THE WARNING: the child prints the stream,
    /// then waits until the returned file exists, then exits. A test may cancel the turn while it waits, or change the model.
    /// </summary>
    static (AgentSession Session, string Release) HeldTurn(string name, Func<string?>? model = null)
    {
        var session = AgentRuntimeProbe.SessionOverStream(DeadTurn, manifest: VendorLimitTests.Codex, model: model, name: name);
        var dir = Path.Combine(TestEnv.Home, "meter", name);
        var payload = Path.Combine(dir, "stream.txt");
        var release = Path.Combine(dir, "release");
        if (OperatingSystem.IsWindows())
            File.WriteAllText(Path.Combine(dir, "runtime.cmd"),
                $"@echo off\r\ntype \"{payload}\"\r\n:wait\r\nif exist \"{release}\" goto done\r\nping -n 2 127.0.0.1 > nul\r\ngoto wait\r\n:done\r\n");
        else
            File.WriteAllText(Path.Combine(dir, "runtime.sh"),
                $"#!/bin/sh\ncat \"{payload}\"\nwhile [ ! -f \"{release}\" ]; do sleep 0.1; done\n");
        return (session, release);
    }

    /// <summary>Waits until the turn in flight has read the model's words that follow the warning in <see cref="DeadTurn"/>.</summary>
    static async Task WarningRead(AgentSession session, Task turn)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!session.History.Any(t => t.Text.Contains("couldn't access the workspace", StringComparison.Ordinal)))
        {
            Assert.False(turn.IsCompleted, "the turn ended before its stream was read");
            Assert.True(DateTime.UtcNow < deadline, "the turn's stream was not read within a minute");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// (C) A WARNING ALREADY READ SURVIVES THE TURN'S CANCELLATION (fix pass 1, review finding 2): a turn cancelled after its stream
    /// said the seat's tools cannot run, before its process exited, still ends carrying the sentence and the warning, gives back the
    /// wakes it took, and the same seat's next admission is refused. At the unit's code the mark was saved only after a normal end of
    /// the stream, so a cancelled turn lost all three.
    /// </summary>
    [Fact]
    public async Task A_warning_already_read_survives_the_turns_cancellation()
    {
        var name = nameof(A_warning_already_read_survives_the_turns_cancellation);
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var (session, _) = HeldTurn(name);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;
        var now = TestEnv.LocalNoon();
        var home = Path.Combine(TestEnv.Home, "cli-code-mode", name);
        Directory.CreateDirectory(Path.Combine(home, ".tradeagent"));
        var host = new VendorLimitTests.Host(db, events, session, home) { Clock = () => now };
        var loop = new MissionLoop(host, new MissionOptions { ReviewEvery = TimeSpan.Zero }, now: () => now);
        Assert.True(events.RecordOwnerMessage("how is the book?", now));

        using var cancel = new CancellationTokenSource();
        var turn = loop.TurnAsync(cancel.Token);
        await WarningRead(session, turn);
        cancel.Cancel();
        try { await turn; }
        catch (OperationCanceledException) { /* the loop's own way out of a cancelled pass */ }

        Assert.NotNull(ended);
        Assert.Equal([Warning], ended!.VendorWarnings);
        Assert.Contains(Warning, ended.ToolsUnavailable, StringComparison.Ordinal);
        var owed = Assert.Single(events.Due(now.AddHours(1)), e => e.Kind == MissionEventKind.Owner);
        Assert.Contains("how is the book?", owed.Payload, StringComparison.Ordinal);
        var next = session.CheckSeat();
        Assert.True(next.Refused, "the seat whose tools could not run was admitted again after its turn was cancelled");
        Assert.Equal(ended.ToolsUnavailable, next.Refusal);
    }

    /// <summary>
    /// (C) THE MODEL MARKED IS THE MODEL THE TURN LAUNCHED WITH (fix pass 1, review finding 3): a turn launched on model A whose stream
    /// says its tools cannot run, with model B chosen before its process exits, marks A — A's next admission is refused, B's is not. At
    /// the unit's code the mark re-read the settings at the turn's end and marked B.
    /// </summary>
    [Fact]
    public async Task The_model_marked_dead_is_the_model_the_turn_launched_with()
    {
        var model = "model-a";
        var (session, release) = HeldTurn(nameof(The_model_marked_dead_is_the_model_the_turn_launched_with), () => model);
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;

        var turn = session.SendAsync("continue");
        await WarningRead(session, turn);
        model = "model-b";
        File.WriteAllText(release, "");
        await turn;
        Assert.Contains(Warning, ended?.ToolsUnavailable, StringComparison.Ordinal);

        var onB = session.CheckSeat();
        Assert.False(onB.Refused, $"model-b, chosen after the turn launched on model-a, was marked dead: {onB.Refusal}");
        model = "model-a";
        var onA = session.CheckSeat();
        Assert.True(onA.Refused, "model-a, whose turn's tools could not run, was admitted again");
        Assert.Contains("model-a", onA.Refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// (C) THE VENDOR'S WARNING GOES TO THE ENGINEERING LOG AND AN ACTIVITY LINE, through the meter every conversation is metered by.
    /// </summary>
    [Fact]
    public void The_vendors_warning_goes_to_the_engineering_log_and_an_activity_line()
    {
        using var db = TestEnv.NewDb();
        var at = TestEnv.LocalNoon();
        var meter = new TurnMeter(db, () => 50m, runtimeId: () => "codex", now: () => at,
            recordPath: Path.Combine(TestEnv.Home, $"turns-{Guid.NewGuid():n}.jsonl"), model: () => "gpt-6-luna", share: _ => 1m);
        const string Sentence = "OpenAI Codex CLI on gpt-6-luna could not run any command this turn.";
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(3), DeadTurn, at) { VendorWarnings = [Warning], ToolsUnavailable = Sentence },
            CouncilRoles.Research);

        Assert.Contains(new LogStore(db).RecentActivity(), a => a.Level == "warn" && a.Text == $"Research Director: {Sentence}");
        var logged = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT event, metadata FROM engineering_log WHERE event = 'vendor_warning'");
            using var r = c.ExecuteReader();
            var rows = new List<string>();
            while (r.Read()) rows.Add(r.GetString(1));
            return rows;
        });
        Assert.Contains("Code Mode is unavailable because", Assert.Single(logged), StringComparison.Ordinal);
    }
}
