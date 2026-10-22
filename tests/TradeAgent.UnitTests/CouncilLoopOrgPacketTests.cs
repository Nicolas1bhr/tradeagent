using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE LOOP'S HALF OF A POSITION'S PACKET (<c>U-org-packets-b</c>; ORCHESTRATION § 2.6: "A position's cursor advances only with the
/// attempt's committed outcome"; § 2.10: "a lateral note is app-relayed into the recipient's next packet … it wakes nobody"). Every
/// position here runs on the SEAM HOST <see cref="CouncilHost"/>: conversations are test doubles, the ledgers are real. No real model.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>The lineage rows of <paramref name="attempt"/>, as kind:id.</summary>
    static List<string> InputsOf(Database db, string attempt) =>
        [.. new AiAttemptStore(db).InputsOf(attempt).Select(i => $"{i.Kind}:{i.RefId}")];

    /// <summary>Each launch of <paramref name="position"/>, in launch order.</summary>
    static List<string> LaunchesOf(Database db, string position)
    {
        using var c = db.Cmd("SELECT id FROM ai_attempt WHERE role=$r ORDER BY rowid", ("$r", position));
        using var r = c.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>
    /// (a) A NOTE RIDES ITS RECIPIENT'S NEXT PACKET ONCE — AND AGAIN AFTER A LOST OR GATE-REFUSED LAUNCH. The head sends the executor a
    /// note, which wakes nobody; the executor's own self-wakes then launch it four times: the gate refuses the first at its last check
    /// (its packet carried the note, nothing recorded), the second's transition never commits (a launch the next start loses; nothing
    /// recorded), the third commits and records the note as its input, and the fourth's packet no longer carries it.
    ///
    /// <para>RED at base: no packet carried a note (the first send held no <c>lat-</c> id). MUTANT, the lineage written whatever the
    /// launch's ending (the gate-refusal condition removed): the second launch's packet no longer carried the note.</para>
    /// </summary>
    [Fact]
    public async Task A_note_rides_the_recipients_next_packet_once_and_again_after_a_lost_or_gate_refused_launch()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor,
            "The funding skew looks venue-specific.\nCheck the second venue before you trust it.", [], DateTimeOffset.UtcNow);
        Assert.Empty(host.Events!.Due(DateTimeOffset.UtcNow.AddMinutes(1)).Where(e => e.Role == Executor));   // it woke nobody

        var executor = host.Conversations[Executor];
        executor.GateRefused = new LateRefusal(new TurnRefusal(RefusalKind.HostOrLaunch, "the host's cell proof changed"), true);
        SelfWake(host, Executor, "packet-1");
        await TurnsUntil(loop, host, Executor, 1);

        executor.GateRefused = null;
        host.CommitThrows = true;
        SelfWake(host, Executor, "packet-2");
        await TurnsUntil(loop, host, Executor, 2);

        host.CommitThrows = false;
        SelfWake(host, Executor, "packet-3");
        await TurnsUntil(loop, host, Executor, 3);

        SelfWake(host, Executor, "packet-4");
        await TurnsUntil(loop, host, Executor, 4);

        var sent = executor.Sent;
        Assert.All(sent.Take(3), prompt => Assert.Contains($"Note `{note.Id}` from `{TeamHead}`", prompt, StringComparison.Ordinal));
        Assert.DoesNotContain(note.Id, sent[3], StringComparison.Ordinal);

        var launches = LaunchesOf(db, Executor);
        Assert.Equal(4, launches.Count);
        Assert.Empty(InputsOf(db, launches[0]));   // the gate refused it at its last check
        Assert.Empty(InputsOf(db, launches[1]));   // its transition never committed
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, launches[2]));
        Assert.Empty(InputsOf(db, launches[3]));
        Assert.Empty(host.InputsOutsideCommit);
    }

    /// <summary>
    /// (h) INPUT LINEAGE IS APPEND-ONLY AND WRITTEN ONLY WITH THE COMMITTED OUTCOME: the loop hands the host what a packet showed only
    /// from inside the turn's committed transition; the rows refuse UPDATE and DELETE and a kind outside the closed list; and a
    /// second commit of the same attempt writes nothing twice. RED at base: no <c>ai_attempt_input</c> table existed.
    /// </summary>
    [Fact]
    public async Task Input_lineage_is_append_only_and_written_only_with_the_committed_outcome()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = SessionChart(db, root);
        var loop = new MissionLoop(host, NoHeartbeat);
        var note = new OrgStore(db).SendLateral(TeamHead, null, Executor, "a note", [], DateTimeOffset.UtcNow);
        SelfWake(host, Executor, "lineage-1");
        await TurnsUntil(loop, host, Executor, 1);

        var attempt = Assert.Single(LaunchesOf(db, Executor));
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));
        Assert.Empty(host.InputsOutsideCommit);

        new AiAttemptStore(db).RecordInputs(attempt, [new PacketInput(PacketInputKind.Lateral, note.Id)]);
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));

        void Exec(string sql) => db.Write(_ => { using var c = db.Cmd(sql); return c.ExecuteNonQuery(); });
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec("UPDATE ai_attempt_input SET ref_id='lat-000000000000'"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec("DELETE FROM ai_attempt_input"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            new AiAttemptStore(db).RecordInputs(attempt, [new PacketInput("approval", "owner")]));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            new AiAttemptStore(db).RecordInputs("turn-nobody-launched", [new PacketInput(PacketInputKind.Lateral, note.Id)]));
        Assert.Equal([$"lateral:{note.Id}"], InputsOf(db, attempt));
    }
}
