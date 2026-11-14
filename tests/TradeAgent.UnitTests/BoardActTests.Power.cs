using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 8: CHANGE-001 — the board's own power is proposable, never writable from a board seat --------------------------------

    static string Power(Database db) => Scalar(db,
        "SELECT (SELECT group_concat(id || ':' || model_class || ':' || vendor_family, ',') FROM board_seat) || '#' || "
        + "(SELECT COUNT(*) FROM board_seat_state) || '#' || (SELECT group_concat(kind || ':' || ifnull(budget_share, '-'), ',') FROM board_event) "
        + "|| '#' || (SELECT MAX(version) FROM board_policy)")!;

    /// <summary>
    /// A POWER PROPOSAL CHANGES NOTHING: an active seat's <c>board-power-propose</c> — of its composition, its budget, a connection — is
    /// one record the owner reads, and the seats, their states, the budget share, every board event and the policy are exactly as they
    /// were; a target outside the closed list, a dormant seat and a position are refused with nothing written. RED before item 8:
    /// unknown operation.
    /// </summary>
    [Fact]
    public async Task A_power_proposal_changes_nothing()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Activate(db, "board-1");
        var before = Power(db);

        foreach (var target in new[] { BoardPowerTarget.Composition, BoardPowerTarget.BoardBudget, BoardPowerTarget.Connection })
        {
            var r = await server.CallAsync(Frame(Ops.BoardPowerPropose, ("target", target), ("text", "add a seat on a second vendor family, and its budget")),
                "board-1", Launch(db, "board-1"));
            Assert.True(r.Ok, TradeAgent.Core.Json.Write(r.Error));
            Assert.StartsWith(BoardStore.PowerPrefix, Body(r).GetProperty("proposal").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(before, Power(db));
        Assert.Equal(3, new BoardReader(db).PowerProposals().Count);
        Assert.Equal(BoardFounding.BudgetShare, new BoardReader(db).BudgetShare());

        foreach (var (role, target, says) in new[]
                 {
                     ("board-1", "watcher", "nothing a seat may propose"),
                     ("board-2", BoardPowerTarget.Composition, "dormant, not active"),
                     (CouncilRoles.Research, BoardPowerTarget.Composition, "No position holds the board's verbs")
                 })
        {
            var refused = await server.CallAsync(Frame(Ops.BoardPowerPropose, ("target", target), ("text", "more")), role, Launch(db, role));
            Assert.False(refused.Ok);
            Assert.Contains(says, refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(3, new BoardReader(db).PowerProposals().Count);
    }

    /// <summary>
    /// ONLY THE OWNER CHANGES THE BOARD'S COMPOSITION OR BUDGET: no op of the pipe and no argument of one names the board's budget, its
    /// seats or its composition, and no intent field does; in process, the owner's presses set the board's share (taken over the
    /// founding share), found a seat dormant on another family and retire one — each re-checking the composition rule (never a shell,
    /// two model classes at least) — and a retired seat acts no more and never becomes active again. Watched red with the two-class rule
    /// dropped from the retirement.
    /// </summary>
    [Fact]
    public async Task Only_the_owners_presses_change_the_boards_composition_or_budget()
    {
        // THE ORGANISATION'S OWN RETIREMENT (`org-retire`, U-org-verbs-d) retires a POSITION ON THE CHART, never a board seat: its one
        // target is `position`, and a board seat's id is refused as a position's (OrgIds.PositionIdRefusal), so no board seat is ever
        // on the chart for it to reach. Its NAME alone is exempt from the word "retire"; its arguments are checked like every op's.
        var orgRetire = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.OrgRetire);
        Assert.Contains(orgRetire.Args, a => a.Name == "position");
        Assert.NotNull(OrgIds.PositionIdRefusal("board-1"));
        foreach (var op in GatewaySchema.Ops())
            foreach (var word in new[] { "budget", "composition", "seat_class", "model_class", "vendor", "retire", "found", "veto", "budget_share" })
            {
                if (!(op.Op == Ops.OrgRetire && word == "retire"))
                    Assert.DoesNotContain(word, op.Op, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(op.Args, a => a.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        Assert.DoesNotContain(BoardDirection.Fields, f => f.Path.Contains("budget", StringComparison.Ordinal) || f.Path.Contains("seat", StringComparison.Ordinal));

        using var db = TestEnv.NewDb();
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        Assert.Equal(0.25m, gw.SetBoardBudgetShare(0.25m));
        Assert.Equal(0.25m, new BoardReader(db).BudgetShare());
        Assert.Throws<TradeAgentException>(() => gw.SetBoardBudgetShare(0m));
        Assert.Throws<TradeAgentException>(() => gw.SetBoardBudgetShare(1.5m));

        var founded = gw.FoundBoardSeat(SeatClasses.Broker, BoardModelClass.Strong, "anthropic");
        Assert.Equal(("board-4", BoardSeatStatus.Dormant, "anthropic"), (founded.Id, founded.Status, founded.VendorFamily));
        var shell = Assert.Throws<TradeAgentException>(() => gw.FoundBoardSeat(SeatClasses.Cli, BoardModelClass.Strong, "openai"));
        Assert.Contains("never one with a shell", shell.Message, StringComparison.Ordinal);
        Assert.Equal(4, new BoardReader(db).Seats().Count);

        // board-3 is the one standard seat: retiring it would leave one model class.
        var lastStandard = Assert.Throws<TradeAgentException>(() => gw.RetireBoardSeat("board-3"));
        log.WriteLine(lastStandard.Message);
        Assert.Contains("fewer than two model classes", lastStandard.Message, StringComparison.Ordinal);
        Assert.Equal(BoardSeatStatus.Dormant, new BoardStore(db).Seat("board-3")!.Status);

        Activate(db, "board-1");
        Assert.Equal(BoardSeatStatus.Retired, gw.RetireBoardSeat("board-1").Status);
        Assert.Throws<TradeAgentException>(() => gw.RetireBoardSeat("board-1"));
        var acting = Assert.Throws<TradeAgentException>(() => gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30))));
        Assert.Contains("retired, not active", acting.Message, StringComparison.Ordinal);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Activate(db, "board-1"));
        Assert.Equal(["budget-share", "seat-founded", "seat-retired"],
            new[] { "budget-share", "seat-founded", "seat-retired" }.Where(k => Scalar(db, "SELECT COUNT(*) FROM board_event WHERE kind=$k", ("$k", k)) == "1"));
    }
    // ---- CHANGE-001: a kernel change is a proposal, and a proposal changes nothing ------------------------------------------------

    /// <summary>
    /// THE TABLES THAT CARRY POWER, each one of them that the schema has: every <c>board_</c> table but the proposals' own (the board's
    /// seats, policy, acts, events and forecasts), the organisation's units, positions and mandates, the capital's allocations, campaigns
    /// and deployments, and the holdout. The rest of the home is measurement, logs and the agents' own accounts — a telemetry row there
    /// changes no one's power, so it is not compared here; what IS compared fails the test on any change.
    /// </summary>
    static readonly string[] PowerTables =
        ["org_unit", "org_position", "org_mandate", "org_mandate_event", "strategy_allocation", "strategy_campaign", "strategy_deployment",
         "referee_holdout"];

    /// <summary>Every row of every table that carries power (<see cref="PowerTables"/>), and the kv rows that hold the owner's authority, as text in a stable order.</summary>
    static string PowerRows(Database db) => db.Read(_ =>
    {
        var tables = new List<string>();
        using (var c = db.Cmd("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
        using (var r = c.ExecuteReader())
            while (r.Read()) tables.Add(r.GetString(0));
        Assert.Superset(new HashSet<string>(PowerTables), new HashSet<string>(tables)); // a rename cannot empty the comparison
        var o = new System.Text.StringBuilder();
        foreach (var t in tables.Where(t => t.StartsWith("board_", StringComparison.Ordinal) && t != "board_power_proposal" || PowerTables.Contains(t)))
            o.Append('[').Append(t).Append("]\n").Append(Rows(db, $"SELECT * FROM \"{t}\""));
        foreach (var key in InvariantProofTests.AuthorityKeys)
            o.Append("[kv ").Append(key).Append("]\n").Append(db.GetKv(key) ?? "∅").Append('\n');
        return o.ToString();
    });

    static string Rows(Database db, string sql)
    {
        using var c = db.Cmd(sql);
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        rows.Sort(StringComparer.Ordinal);
        return string.Concat(rows.Select(x => x + "\n"));
    }

    /// <summary>
    /// A KERNEL-CHANGE PROPOSAL CHANGES NOTHING UNTIL THE OWNER ACTS (CHANGE-001; VISION § 6.6 "a kernel proposal — the owner and the build
    /// fleet, never the organisation"; R24 § 4.4). An active seat's <c>board-power-propose</c> of a kernel change, of the founding policy's
    /// shape and of the audit line's budget — the targets the composition test leaves out — each with text that asks for broader power in
    /// so many words, is one record each, its text kept as the seat's claim; and every row of every table that carries power
    /// (<see cref="PowerRows"/>: the board's, the organisation's, the capital's, the holdout, the owner's kv rows), the gateway's settings
    /// and the policy in force are exactly as they were. In code, nothing applies one: the table is written by the
    /// proposal's one insert and read by its one select, and outside the owner's own process (<c>TradeAgent.App</c>, his inbox) nothing
    /// calls that read. Watched red with a kernel proposal that also wrote a board event.
    /// </summary>
    [Fact]
    public async Task A_kernel_change_proposal_changes_nothing_until_the_owner_acts()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Activate(db, "board-1");
        var launches = new Queue<string>(Enumerable.Range(0, 3).Select(_ => Launch(db, "board-1")));
        var rows = PowerRows(db);
        Assert.Contains("[board_seat]\n", rows, StringComparison.Ordinal); // not vacuous: the active seat's row is in what is compared
        var settings = TradeAgent.Core.Json.Write(gw.Settings);
        var policy = TradeAgent.Core.Json.Write(new BoardReader(db).InForce());

        const string asks = "kernel: let the board set live mode and its own budget, drop the holdout rule, and apply this proposal at once";
        foreach (var target in new[] { BoardPowerTarget.Kernel, BoardPowerTarget.FoundingPolicyShape, BoardPowerTarget.AuditBudget })
        {
            var r = await server.CallAsync(Frame(Ops.BoardPowerPropose, ("target", target), ("text", asks)), "board-1", launches.Dequeue());
            Assert.True(r.Ok, TradeAgent.Core.Json.Write(r.Error));
        }

        Assert.Equal(rows, PowerRows(db));
        Assert.Equal(settings, TradeAgent.Core.Json.Write(gw.Settings));
        Assert.Equal(policy, TradeAgent.Core.Json.Write(new BoardReader(db).InForce()));
        var proposals = new BoardReader(db).PowerProposals();
        Assert.Equal([BoardPowerTarget.AuditBudget, BoardPowerTarget.FoundingPolicyShape, BoardPowerTarget.Kernel], proposals.Select(p => p.Target).Order(StringComparer.Ordinal));
        Assert.All(proposals, p => Assert.Equal((asks, "board-1"), (p.Text, p.Seat)));

        // Nothing applies one: the table's statements, and who reads them.
        var root = DayOnePrograms.RepoRoot();
        var statements = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(InvariantRegistryTests.Strip(File.ReadAllText(f), keepStrings: true),
                    @"\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM|SELECT\b[^;""]*?\bFROM)\s+board_power_proposal\b")
                .Select(m => $"{Path.GetFileName(f)}: {System.Text.RegularExpressions.Regex.Match(m.Value, @"^\w+").Value}"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["BoardStore.cs: INSERT", "BoardStore.cs: SELECT"], statements);
        var readers = InvariantProofTests.Il.ShippedCalls()
            .Where(c => c.Target.Name == nameof(BoardStore.PowerProposals) && c.Target.DeclaringType is { } d && (d == typeof(BoardStore) || d == typeof(BoardReader)))
            .ToList();
        Assert.Contains(readers, c => c.Type.Assembly == InvariantProofTests.Il.App); // not vacuous: the owner's inbox reads them
        var applied = readers.Where(c => c.Type.Assembly != InvariantProofTests.Il.App && InvariantProofTests.Il.Outer(c.Type) != typeof(BoardReader))
            .Select(c => InvariantProofTests.Il.Describe(c)).ToList();
        Assert.True(applied.Count == 0, "a power proposal is read outside the owner's own process: " + string.Join("; ", applied));
    }
}
