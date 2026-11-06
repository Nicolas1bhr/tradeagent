using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION'S DIGEST (<c>U-org-digest</c>): <c>tools/linux-host.sh org-digest</c> sends <c>tools/org-digest.py</c> to the owner's
/// Linux host and reads the app's two state databases, read-only, for the 72-hour unattended run. No host is needed here: the
/// script is run on a FIXTURE home whose rows the app's own stores wrote (<see cref="AiAttemptStore"/>, <see cref="MissionEventStore"/>,
/// <see cref="ToolCallStore"/>, <see cref="FillStore"/>, <see cref="CellProofStore"/>, <see cref="TapeStore"/>, the schema and the board's
/// founding of <see cref="Database"/>); only the rows whose stores need a whole campaign or chart behind them (verdicts, deployments,
/// positions, org events, the pipe's refusal log) are inserted into the app-made schema with its foreign keys off, columns and
/// vocabularies as the app writes them.
///
/// Every number below is worked out in the fixture's comments, not read back from the script. Time is fixed (<see cref="Now"/>) and the
/// script runs in UTC, so "today" is 2026-10-31 and T0 is 2026-10-30T12:00Z.
/// </summary>
public class OrgDigestTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    const string TradeHash = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
    static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }

    sealed record Fixture(string Root, string State, string MachineId, string BootId, string OsRelease)
    {
        public string[] Identity() => ["--machine-id-file", MachineId, "--boot-id-file", BootId, "--osrelease-file", OsRelease];
    }

    // ---- THE FIXTURE ---------------------------------------------------------------------------------------------------------
    static void Attempt(AiAttemptStore store, string id, string? role, string started, string billing, string? model, decimal reserved, decimal? cost,
        int? exit, string? vendorSaid = null, string? tools = null, bool end = true)
    {
        var at = At(started);
        store.Begin(new AiAttempt { Id = id, StartedAt = at, Runtime = "codex", RequestedModel = model, ReservedCost = reserved, Role = role,
            BillingClass = billing, OfferedTools = tools });
        if (end) store.End(id, exit ?? 0, at.AddMinutes(5), 100, 0, 0, 50, 0, model, cost, cost is null ? "unpriced in the fixture" : null, null, null, vendorSaid);
    }

    static Fixture Build()
    {
        var root = Path.Combine(TestEnv.Home, "orgdigest-" + Guid.NewGuid().ToString("n")[..10]);
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);

        // ---- tradeagent.db: the schema, the seeded chart and the board's founding are the app's own.
        using (var db = new Database(Path.Combine(state, "tradeagent.db")))
        {
            var attempts = new AiAttemptStore(db);
            // Operations (the legacy chair, role NULL), plan class unless said. Today = 2026-10-31 (UTC).
            Attempt(attempts, "a1", null, "2026-10-31T08:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 1.5m, 0, tools: "read\nwrite\nsearch");
            Attempt(attempts, "a2", "pos-aaaaaa", "2026-10-31T09:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 2.25m, 0);
            // The same vendor words twice: one error group of 2, exit 1, both key-billed.
            Attempt(attempts, "a3", "pos-aaaaaa", "2026-10-31T10:00:00Z", BillingClasses.Key, "gpt-5.6-sol", 1m, 0.5m, 1, "ERROR 429: the model refused this request", "read\nwrite");
            Attempt(attempts, "a4", "pos-aaaaaa", "2026-10-31T11:00:00Z", BillingClasses.Key, "gpt-5.6-sol", 1m, 0.25m, 1, "ERROR 429: the model refused this request", "read\nwrite");
            Attempt(attempts, "a5", "board-1", "2026-10-31T12:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 0.4m, 0);
            // Yesterday (after T0): counts since T0, not today. And one BEFORE T0: counts for neither.
            Attempt(attempts, "a6", null, "2026-10-30T13:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 3.0m, 0);
            Attempt(attempts, "a7", null, "2026-10-29T10:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 9.0m, 0);
            // One still open (counts its 0.7 reservation) and one declared LOST (counts its 0.1 reservation).
            Attempt(attempts, "a9", null, "2026-10-31T13:40:00Z", BillingClasses.Plan, "gpt-5.6-sol", 0.1m, null, null, end: false);
            Attempt(attempts, "a8", null, "2026-10-31T13:30:00Z", BillingClasses.Plan, "gpt-5.6-sol", 0.7m, null, null, end: false);
            Assert.Equal(1, attempts.LoseOpen(At("2026-10-31T13:50:00Z"), ["a8"]));

            // THE WAKES: operations has two untaken (next in 1h, kind order), pos-aaaaaa's was due an hour ago (overdue), research none.
            var wakes = new MissionEventStore(db);
            Assert.True(wakes.RaiseDue("w1", MissionEventKind.Order, At("2026-10-31T12:00:00Z"), At("2026-10-31T15:00:00Z")));
            Assert.True(wakes.RaiseDue("w2", MissionEventKind.Self, At("2026-10-31T12:00:00Z"), At("2026-10-31T16:00:00Z")));
            Assert.True(wakes.RaiseDue("w3", MissionEventKind.Self, At("2026-10-31T12:00:00Z"), At("2026-10-31T13:00:00Z"), role: "pos-aaaaaa"));

            // REFUSALS the tool surface wrote: 3 identical, 1 of another tool, 2 served.
            var tools = new ToolCallStore(db);
            for (var i = 0; i < 3; i++)
                tools.Record(new ToolCallRow { At = At("2026-10-31T09:10:00Z").AddMinutes(i), Attempt = "a2", Role = "pos-aaaaaa", Tool = "place_order", Served = false, Refusal = "no order verb is offered to this seat" });
            tools.Record(new ToolCallRow { At = At("2026-10-31T09:20:00Z"), Attempt = "a2", Role = "pos-aaaaaa", Tool = "wallet", Served = false, Refusal = "credentials are the owner's" });
            tools.Record(new ToolCallRow { At = At("2026-10-31T09:30:00Z"), Attempt = "a2", Role = "pos-aaaaaa", Tool = "read", Served = true });
            tools.Record(new ToolCallRow { At = At("2026-10-31T09:31:00Z"), Attempt = "a2", Role = "pos-aaaaaa", Tool = "read", Served = true });

            // THE FILLS (paper account PAPER-1, BTCUSDT) as the fill ledger writes them; the deployment's ops below name their requests.
            var fills = new FillStore(db);
            Fill F(string id, string at, string side, decimal q, decimal p, decimal? fee, string? requestId, string? clientOrderId) =>
                new("PAPER-1", id, At(at), "BTCUSDT", side, q, p, null, clientOrderId, requestId, null, FillSource.Event, fee, At(at)) { ConnectorId = "paper" };
            Assert.True(fills.Record(F("x1", "2026-10-30T14:00:00Z", "Buy", 1m, 100m, 0.1m, "r1", "TA-r1")));
            Assert.True(fills.Record(F("x2", "2026-10-30T15:00:00Z", "Sell", 1m, 110m, 0.1m, "r2", "TA-r2")));
            // Attributed by its client order id alone, and its fee unknown.
            Assert.True(fills.Record(F("x3", "2026-10-31T08:00:00Z", "Buy", 2m, 100m, null, null, "TA-r4")));

            // THE CELL PROOF, written by the store as the benchmark writes it, bound to a host identity and a build: the digest recomputes both.
            var host = new HostIdentity(Sha("tradeagent-cell-proof-machine:machine-one"), "6.8.0-fixture") { Boot = Sha("tradeagent-cell-proof-boot:boot-one") };
            var proofs = new CellProofStore(db, () => host, () => "0.0.0+trade-sha256:" + TradeHash);
            var kept = proofs.Record("codex", "profile-1", ["none"], [new ProofRow("netns", false, "denied"), new ProofRow("rlimit", true, "not measured")]);
            var dropped = proofs.Record("codex", "profile-0", ["none"]);
            proofs.Revoke(dropped, "the fixture revokes one");
            Assert.NotEqual(kept, dropped);

            db.Exec("PRAGMA foreign_keys=OFF;");
            db.Exec("""
                INSERT INTO org_position(id, unit_id, home_dir, status, seat_runtime, seat_model, created_at, ended_at) VALUES
                  ('pos-aaaaaa', 'div-operations', 'h1', 'active', 'codex', 'gpt-5.6-sol', '2026-10-30T10:00:00.0000000Z', NULL),
                  ('pos-bbbbbb', 'div-operations', 'h2', 'dormant', NULL, NULL, '2026-10-30T10:00:00.0000000Z', NULL),
                  ('pos-cccccc', 'div-research', 'h3', 'ended', 'codex', 'gpt-5.6-sol', '2026-10-29T10:00:00.0000000Z', '2026-10-30T09:00:00.0000000Z');
                INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES
                  ('2026-10-30T10:00:00.0000000Z', 'div-operations', 'pos-aaaaaa', 'hired', 'chief', 'a'),
                  ('2026-10-30T10:01:00.0000000Z', 'div-operations', 'pos-bbbbbb', 'hired', 'chief', 'b'),
                  ('2026-10-31T10:00:00.0000000Z', 'div-operations', NULL, 'tilted', 'app', 'c'),
                  ('2026-10-29T10:00:00.0000000Z', 'div-operations', NULL, 'tilted', 'app', 'before T0');
                INSERT INTO strategy_verdict(campaign_id, version_id, requested_at, holdout_from) VALUES
                  (1, 'v1', '2026-10-31T01:00:00.0000000Z', '2026-01-01'),
                  (1, 'v2', '2026-10-31T02:00:00.0000000Z', '2026-01-01'),
                  (1, 'v3', '2026-10-31T03:00:00.0000000Z', '2026-01-01'),
                  (1, 'v0', '2026-10-01T03:00:00.0000000Z', '2026-01-01');
                INSERT INTO strategy_promotion(id, version_id, campaign_id, scoring_policy_sha256, interpreter_build, holdout_dataset_id, holdout_dataset_sha256,
                    execution_model, evaluator_version, holdout_run_id, verdict, reason, at) VALUES
                  ('p1', 'v1', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', 'refused', 'no-trade-on-the-holdout', '2026-10-31T01:30:00.0000000Z'),
                  ('p2', 'v2', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', 'paper-eligible', 'meets-the-paper-policy-on-historical-evidence', '2026-10-31T02:30:00.0000000Z'),
                  ('p0', 'v0', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', 'refused', 'not-profitable-after-costs', '2026-10-01T03:30:00.0000000Z');
                INSERT INTO referee_holdout(campaign_id, version_id, method, research_row, holdout_days, z_status, alpha_j, wealth_before, wealth_after, addis_status, remembered, at) VALUES
                  (1, 'v1', 1, 1, 30, 'inconclusive', '0.01', '1', '1', 'inconclusive', 0, '2026-10-31T01:20:00.0000000Z'),
                  (1, 'v2', 1, 1, 30, 'enforceable', '0.01', '1', '1', 'tested', 0, '2026-10-31T02:20:00.0000000Z');
                INSERT INTO strategy_allocation(id, version_id, promotion_id, policy_version, max_quantity, currency, effective_from, effective_to, reason, at) VALUES
                  ('al1', 'v2', 'p2', 'pol', '1', 'USD', '2026-10-31T02:40:00.0000000Z', NULL, 'paper', '2026-10-31T02:40:00.0000000Z');
                INSERT INTO strategy_deployment(id, version_id, allocation_id, envelope_id, connector_id, account_id, symbol, mode, state, started_at, ended_at, end_reason) VALUES
                  ('dep-paper-active-0001', 'v2', 'al1', 'env', 'paper', 'PAPER-1', 'BTCUSDT', 'paper', 'active', '2026-10-30T13:00:00.0000000Z', NULL, NULL),
                  ('dep-paper-ended-00002', 'v2', 'al1', 'env', 'paper', 'PAPER-1', 'ETHUSDT', 'paper', 'ended', '2026-10-20T13:00:00.0000000Z', '2026-10-21T13:00:00.0000000Z', 'term over'),
                  ('dep-live-suspended-03', 'v2', 'al1', 'env', 'atas', 'LIVE-1', 'ES', 'live', 'suspended', '2026-10-30T13:00:00.0000000Z', NULL, NULL);
                INSERT INTO deployment_op(request_id, deployment_id, bar_open_time, kind, intent, state, answer, created_at, resolved_at) VALUES
                  ('r1', 'dep-paper-active-0001', 'b1', 'entry', '{}', 'resolved', NULL, '2026-10-30T14:00:00.0000000Z', '2026-10-30T14:00:01.0000000Z'),
                  ('r2', 'dep-paper-active-0001', 'b2', 'exit', '{}', 'resolved', NULL, '2026-10-30T15:00:00.0000000Z', '2026-10-30T15:00:01.0000000Z'),
                  ('r3', 'dep-paper-active-0001', 'b3', 'entry', '{}', 'refused', 'the envelope allows 1, the plan asked for 3', '2026-10-31T09:00:00.0000000Z', NULL),
                  ('r4', 'dep-paper-active-0001', 'b4', 'entry', '{}', 'resolved', NULL, '2026-10-31T08:00:00.0000000Z', '2026-10-31T08:00:01.0000000Z');
                INSERT INTO engineering_log(at, component, event, severity, metadata) VALUES
                  ('2026-10-31T09:40:00.0000000Z', 'Ipc', 'op_refused', 'warn', '{"op":"hire","code":"INVALID_REQUEST","role":"pos-aaaaaa"}'),
                  ('2026-10-31T09:41:00.0000000Z', 'Ipc', 'op_refused', 'warn', '{"op":"hire","code":"INVALID_REQUEST","role":"pos-aaaaaa"}'),
                  ('2026-10-31T09:42:00.0000000Z', 'Ipc', 'auth_rejected', 'warn', NULL),
                  ('2026-10-31T09:43:00.0000000Z', 'Other', 'op_refused', 'warn', '{"op":"x"}');
                """);
            db.Exec("PRAGMA foreign_keys=ON;");
        }

        // ---- tape.db: the tape's own store appends the fetches.
        using (var tape = new TapeStore(Path.Combine(state, "tape.db")))
        {
            TapeFetch Fetch(string source, string received, int? status, string? note) => new()
            {
                Source = source, Series = "s", Url = "https://example.invalid/" + source, RequestedAt = At(received).AddSeconds(-1), ReceivedAt = At(received),
                HttpStatus = status, Note = note
            };
            tape.Append(Fetch("alpha", "2026-10-31T13:00:00Z", 200, null));
            tape.Append(Fetch("alpha", "2026-10-31T13:50:00Z", 200, null));       // alpha's newest clean delivery: 10 minutes before Now
            tape.Append(Fetch("beta", "2026-10-31T10:00:00Z", 200, null));        // beta's: 4 hours before Now
            tape.Append(Fetch("beta", "2026-10-31T13:55:00Z", 500, null));        // a non-200
            tape.Append(Fetch("gamma", "2026-10-31T13:30:00Z", 200, "parse: not json"));   // a 200 whose parse failed: no delivery
        }

        // ---- the host the cell proof was measured on, and the paper book's mark.
        var fixture = new Fixture(root, state, Path.Combine(root, "machine-id"), Path.Combine(root, "boot-id"), Path.Combine(root, "osrelease"));
        File.WriteAllText(fixture.MachineId, "machine-one\n");
        File.WriteAllText(fixture.BootId, "boot-one\n");
        File.WriteAllText(fixture.OsRelease, "6.8.0-fixture\n");
        File.WriteAllText(Path.Combine(state, "trade.sha256"), TradeHash + "\n");
        using (var book = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(state, "paper-PAPER-1.db")};Pooling=False"))
        {
            book.Open();
            using var c = book.CreateCommand();
            c.CommandText = "CREATE TABLE paper_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO paper_meta VALUES('close:BTCUSDT','105');";
            c.ExecuteNonQuery();
        }
        return fixture;
    }

    // ---- RUNNING IT ----------------------------------------------------------------------------------------------------------
    static (int Exit, string Out, string Err) Run(string file, IEnumerable<string> args, string? stdin = null, IDictionary<string, string>? env = null, string tool = "python3")
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        if (tool == "python3") psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(file);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TZ"] = "UTC";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        var @out = p.StandardOutput.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "the script did not finish in a minute");
        return (p.ExitCode, @out.Result, err.Result);
    }

    static string[] DigestArgs(Fixture f, params string[] more) =>
        [.. new[] { "--state", f.State, "--now", Now.ToString("o"), "--since", T0.ToString("o"), "--no-systemd" }, .. f.Identity(), .. more];

    static JsonElement Json(Fixture f, string? boot = null)
    {
        if (boot is not null) File.WriteAllText(f.BootId, boot);
        var (exit, o, e) = Run(Path.Combine(RepoRoot(), "tools", "org-digest.py"), DigestArgs(f, "--json"));
        Assert.True(exit == 0, $"exit {exit}: {e}");
        return JsonDocument.Parse(o).RootElement.Clone();
    }

    static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    // ---- THE SECTIONS --------------------------------------------------------------------------------------------------------
    [UnixFact]
    public void Every_section_reads_what_the_apps_stores_wrote()
    {
        var f = Build();
        var before = (Hash(Path.Combine(f.State, "tradeagent.db")), Hash(Path.Combine(f.State, "tape.db")));
        var d = Json(f);
        log.WriteLine(d.GetRawText());

        Assert.True(d.GetProperty("read_only").GetBoolean());

        // 1. POSITIONS by status and model: the two seeded legacy positions, plus active/dormant/ended added above.
        var pos = d.GetProperty("positions");
        var groups = pos.GetProperty("by_status_model").EnumerateArray().ToDictionary(g => g.GetProperty("status").GetString() + "/" + g.GetProperty("model").GetString(), g => g.GetProperty("count").GetInt32());
        Assert.Equal(1, groups["active/gpt-5.6-sol"]);
        Assert.Equal(1, groups["dormant/(owner's setting)"]);
        Assert.Equal(1, groups["ended/gpt-5.6-sol"]);
        Assert.Equal(5, pos.GetProperty("total").GetInt32());
        // the model every launch since T0 ran on: 8 of them
        Assert.Equal(8, pos.GetProperty("models_run_since").EnumerateArray().Single(m => m.GetProperty("model").GetString() == "gpt-5.6-sol").GetProperty("turns").GetInt32());

        // 2. TURNS AND COST. operations today: a1 1.5 + a8 open 0.7 + a9 lost 0.1 = 3 turns 2.3; since T0 adds a6 3.0 = 4 turns 5.3.
        //    pos-aaaaaa: a2 2.25 + a3 0.5 + a4 0.25 = 3 turns 3.0. board-1: 0.4. Nothing before T0 (a7's 9.0).
        var turns = d.GetProperty("turns");
        var byPos = turns.GetProperty("positions").EnumerateArray().ToDictionary(p => p.GetProperty("position").GetString()!);
        Assert.Equal((3, "2.3000", 4, "5.3000"), Cells(byPos["operations"]));
        Assert.Equal((3, "3.0000", 3, "3.0000"), Cells(byPos["pos-aaaaaa"]));
        Assert.Equal((1, "0.4000", 1, "0.4000"), Cells(byPos["board-1"]));
        Assert.True(byPos["board-1"].GetProperty("board_seat").GetBoolean());
        Assert.Equal((7, "5.7000", 8, "8.7000"), Cells(turns.GetProperty("total")));

        // 3. ORG EVENTS since T0: 2 hired (chief), 1 tilted (app) — the one before T0 is not counted.
        var events = d.GetProperty("org_events").GetProperty("events").EnumerateArray().ToDictionary(e => e.GetProperty("kind").GetString()!);
        Assert.Equal(2, events["hired"].GetProperty("count").GetInt32());
        Assert.Equal(1, events["tilted"].GetProperty("count").GetInt32());
        Assert.Equal(2, events["hired"].GetProperty("deciders").GetProperty("chief").GetInt32());

        // 4. VERDICTS: asked 3 since T0 (v0 is older); waiting = asked and never given = v3 and... v0 has p0 -> only v3; given 2 since T0.
        var v = d.GetProperty("verdicts");
        Assert.Equal(3, v.GetProperty("asked_since").GetInt32());
        Assert.Equal(1, v.GetProperty("waiting_now").GetInt32());
        Assert.Equal(2, v.GetProperty("given_total").GetInt32());
        Assert.Equal(1, v.GetProperty("no_trade_refusals_since").GetInt32());
        Assert.Equal(1, v.GetProperty("referee_inconclusive_since").GetInt32());
        Assert.Contains(v.GetProperty("given_since").EnumerateArray(), g => g.GetProperty("verdict").GetString() == "paper-eligible");

        // 5. DEPLOYMENTS (a simulation): 1 paper/active, 1 paper/ended, 1 live/suspended; the ended one is older than T0 and is not listed.
        //    dep-paper-active: buy 1@100, sell 1@110 => realised 10; buy 2@100 open, mark 105 => unrealised 10; fees 0.1+0.1 and one unknown.
        var dep = d.GetProperty("deployments");
        Assert.Contains("SIMULATION", dep.GetProperty("label").GetString());
        Assert.Equal(1, dep.GetProperty("allocations").GetProperty("open").GetInt32());
        Assert.Equal(3, dep.GetProperty("by_mode_state").GetArrayLength());
        var listed = dep.GetProperty("deployments").EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);
        var paper = listed.Single(x => x.GetProperty("mode").GetString() == "paper");
        Assert.Equal(3, paper.GetProperty("fills").GetInt32());
        Assert.Equal("10.0000", paper.GetProperty("realised").GetString());
        Assert.Equal("0.2000", paper.GetProperty("fees").GetString());
        Assert.Equal(1, paper.GetProperty("fees_unknown_fills").GetInt32());
        Assert.Equal("2", paper.GetProperty("open_quantity").GetString());
        Assert.Equal("10.0000", paper.GetProperty("unrealised").GetString());
        Assert.Equal(JsonValueKind.Null, paper.GetProperty("net_if_known").ValueKind);   // an unknown fee is not a zero

        // 6. REFUSALS with their words and counts: the tool refusal x3, the other tool x1, the pipe's two kinds, the deployment gate's words.
        var refusals = d.GetProperty("refusals");
        Assert.Equal(8, refusals.GetProperty("total").GetInt32());
        var g0 = refusals.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(5, g0.Count);
        Assert.Equal(3, g0[0].GetProperty("count").GetInt32());
        Assert.Equal("no order verb is offered to this seat", g0[0].GetProperty("words").GetString());
        Assert.Contains(g0, g => g.GetProperty("source").GetString() == "pipe" && g.GetProperty("count").GetInt32() == 2 && g.GetProperty("words").GetString() == "op hire / INVALID_REQUEST");
        Assert.Contains(g0, g => g.GetProperty("source").GetString() == "deployment-op" && g.GetProperty("words").GetString()!.StartsWith("the envelope allows 1", StringComparison.Ordinal));

        // 7. AI ERRORS: a3 and a4 share their vendor words (one group of 2, exit 1, offered 2 tools); a9 is LOST. 3 of 8 launches.
        var errors = d.GetProperty("errors");
        Assert.Equal(8, errors.GetProperty("attempts_since").GetInt32());
        Assert.Equal(3, errors.GetProperty("failed_since").GetInt32());
        Assert.Equal(2, errors.GetProperty("exit_codes").GetProperty("1").GetInt32());
        var said = errors.GetProperty("groups").EnumerateArray().First();
        Assert.Equal(2, said.GetProperty("count").GetInt32());
        Assert.Equal("ERROR 429: the model refused this request", said.GetProperty("vendor_said").GetString());
        Assert.Equal(2, said.GetProperty("offered_tools").GetInt32());
        Assert.Contains(errors.GetProperty("groups").EnumerateArray(), g => g.GetProperty("state").GetString() == "LOST");

        // 8. THE AI DAY-TOTAL vs THE BOARD'S: today plan = a1 1.5 + a2 2.25 + a5 0.4 + a8 0.7 + a9 0.1 = 4.95; key = 0.75. The board's
        //    plan day-total and share are read from the rows the app founded, not asserted from a constant here.
        var day = d.GetProperty("day_total");
        Assert.Equal("4.9500", day.GetProperty("used").GetProperty("plan").GetString());
        Assert.Equal("0.7500", day.GetProperty("used").GetProperty("key").GetString());
        Assert.Equal("0.4000", day.GetProperty("board_seats_spent").GetString());
        Assert.Equal(1, day.GetProperty("board_policy").GetProperty("version").GetInt32());
        Assert.Equal("0.10", day.GetProperty("board_share").GetString());
        Assert.Equal("2026-10-31T00:00:00.0000000Z", day.GetProperty("day").GetProperty("from").GetString());

        // 9. THE NEXT WAKE per active position: operations in 1h (2 pending, kind order), pos-aaaaaa overdue by 1h, research nothing.
        var wakes = d.GetProperty("wakes");
        var byWake = wakes.GetProperty("active_positions").EnumerateArray().ToDictionary(w => w.GetProperty("position").GetString()!);
        Assert.Equal(3600, byWake["operations"].GetProperty("in_seconds").GetInt32());
        Assert.Equal(2, byWake["operations"].GetProperty("pending").GetInt32());
        Assert.Equal("order", byWake["operations"].GetProperty("kind").GetString());
        Assert.Equal(-3600, byWake["pos-aaaaaa"].GetProperty("in_seconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, byWake["research"].GetProperty("next_due").ValueKind);
        Assert.Equal("pos-aaaaaa", Assert.Single(wakes.GetProperty("overdue").EnumerateArray()).GetString());

        // 10. THE CELL PROOF is current on this identity: one unrevoked, one revoked, one GAP row on the newest.
        var cell = d.GetProperty("cell_proof");
        Assert.True(cell.GetProperty("current").GetBoolean());
        Assert.Equal(1, cell.GetProperty("unrevoked_proofs").GetInt32());
        Assert.Equal(1, cell.GetProperty("revoked").GetInt32());
        Assert.Equal(1, cell.GetProperty("newest_gap_rows").GetInt32());

        // 11. THE TAPE: alpha 10 min, beta 4 h (the stalest), gamma never delivered clean; 1 non-200, 1 parse failure; 5 fetches since T0.
        var tape = d.GetProperty("tape_health");
        Assert.Equal(3, tape.GetProperty("source_count").GetInt32());
        Assert.Equal(("alpha", 600), (tape.GetProperty("newest").GetProperty("source").GetString(), tape.GetProperty("newest").GetProperty("age_s").GetInt32()));
        Assert.Equal(("beta", 14400), (tape.GetProperty("stalest").GetProperty("source").GetString(), tape.GetProperty("stalest").GetProperty("age_s").GetInt32()));
        Assert.Equal(5, tape.GetProperty("fetches_since").GetInt32());
        Assert.Equal(1, tape.GetProperty("non_200_since").GetInt32());
        Assert.Equal(1, tape.GetProperty("parse_failed_since").GetInt32());
        Assert.Equal("parse: not json", tape.GetProperty("last_parse_note").GetString());

        // ATTENTION: the live deployment, the stale tape, the overdue wake, the lost launch and the failures are all raised.
        var attention = string.Join("\n", d.GetProperty("attention").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("mode 'live'", attention);
        Assert.Contains("overdue wake", attention);
        Assert.Contains("LOST", attention);
        Assert.Contains("beta last delivered 4h ago", attention);
        Assert.Contains("parse failure", attention);

        // READ-ONLY, ON DISK: not one byte of either database moved.
        Assert.Equal(before, (Hash(Path.Combine(f.State, "tradeagent.db")), Hash(Path.Combine(f.State, "tape.db"))));
    }

    static (int, string, int, string) Cells(JsonElement p) =>
        (p.GetProperty("turns_today").GetInt32(), p.GetProperty("cost_today").GetString()!, p.GetProperty("turns_since").GetInt32(), p.GetProperty("cost_since").GetString()!);

    [UnixFact]
    public void The_cell_proof_is_not_current_on_another_boot_or_another_build()
    {
        var f = Build();
        Assert.True(Json(f).GetProperty("cell_proof").GetProperty("current").GetBoolean());

        var other = Json(f, boot: "boot-two\n").GetProperty("cell_proof");
        Assert.False(other.GetProperty("current").GetBoolean());
        Assert.Equal("boot", Assert.Single(other.GetProperty("differs").EnumerateArray()).GetString());
        Assert.Contains("cell proof is NOT current", string.Join("\n", Json(f).GetProperty("attention").EnumerateArray().Select(a => a.GetString())));

        File.WriteAllText(f.BootId, "boot-one\n");
        File.WriteAllText(Path.Combine(f.State, "trade.sha256"), new string('c', 64) + "\n");
        Assert.Equal("build", Assert.Single(Json(f).GetProperty("cell_proof").GetProperty("differs").EnumerateArray()).GetString());

        // No deployed hash at all binds no build: not current, and it says so rather than guessing.
        File.Delete(Path.Combine(f.State, "trade.sha256"));
        Assert.False(Json(f).GetProperty("cell_proof").GetProperty("current").GetBoolean());
    }

    [UnixFact]
    public void The_human_digest_is_sixty_lines_or_fewer_and_names_every_section()
    {
        var f = Build();
        // A crowd: many positions, kinds, refusals and errors, so the cap is what holds the line, not a small fixture.
        using (var db = new Database(Path.Combine(f.State, "tradeagent.db")))
        {
            db.Exec("PRAGMA foreign_keys=OFF;");
            var sb = new StringBuilder();
            for (var i = 0; i < 40; i++)
                sb.Append($"INSERT INTO org_position(id, unit_id, home_dir, status, seat_model, created_at) VALUES('pos-x{i:00}', 'div-research', 'h', 'active', 'm{i % 7}', '2026-10-30T10:00:00.0000000Z');")
                  .Append($"INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES('2026-10-31T10:00:00.0000000Z', 'div-research', 'kind-{i}', 'chief', 'd');")
                  .Append($"INSERT INTO tool_call(at, attempt, role, tool, served, refusal) VALUES('2026-10-31T10:00:00.0000000Z', 'a1', 'pos-x{i:00}', 'tool-{i}', 0, 'refused number {i}');")
                  .Append($"INSERT INTO ai_attempt(id, started_at, state, exit_code, vendor_said, role, reserved_cost) VALUES('e{i}', '2026-10-31T10:00:00.0000000Z', 'ENDED', 1, 'vendor error {i}', 'pos-x{i:00}', '0.01');");
            db.Exec(sb.ToString());
            db.Exec("PRAGMA foreign_keys=ON;");
        }
        var (exit, text, err) = Run(Path.Combine(RepoRoot(), "tools", "org-digest.py"), DigestArgs(f));
        Assert.True(exit == 0, err);
        log.WriteLine(text);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.InRange(lines.Length, 20, 60);
        foreach (var word in new[] { "positions:", "turns/cost", "org events since T0", "verdicts:", "deployments (SIMULATION", "refusals since T0", "AI launches since T0",
                                     "AI day-total", "next wake per active position", "cell proof:", "tape:" })
            Assert.Contains(lines, l => l.StartsWith(word, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Length > 400);
    }

    // ---- READ-ONLY, IN THE OPEN ----------------------------------------------------------------------------------------------
    [UnixFact]
    public void The_scripts_connection_cannot_write()
    {
        var f = Build();
        var probe = Path.Combine(f.Root, "probe.py");
        File.WriteAllText(probe, $$"""
            import importlib.util, sqlite3, sys
            spec = importlib.util.spec_from_file_location("od", {{PyString(Path.Combine(RepoRoot(), "tools", "org-digest.py"))}})
            od = importlib.util.module_from_spec(spec); spec.loader.exec_module(od)
            conn = od.open_ro({{PyString(Path.Combine(f.State, "tradeagent.db"))}})
            for sql in ("INSERT INTO meta(key, value) VALUES('x','y')", "UPDATE ai_attempt SET cost = '0'", "DELETE FROM org_event",
                        "CREATE TABLE t(a)", "PRAGMA query_only=OFF"):
                try:
                    conn.execute(sql); conn.commit()
                    if sql.startswith("PRAGMA"):
                        conn.execute("INSERT INTO meta(key, value) VALUES('x','y')"); conn.commit()
                except sqlite3.Error as ex:
                    print("refused:", sql.split()[0])
                    continue
                print("WROTE:", sql); sys.exit(1)
            """);
        var before = Hash(Path.Combine(f.State, "tradeagent.db"));
        var (exit, o, e) = Run(probe, []);
        Assert.True(exit == 0, o + e);
        Assert.Equal(5, o.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("refused:", StringComparison.Ordinal)));
        Assert.Equal(before, Hash(Path.Combine(f.State, "tradeagent.db")));
    }

    static string PyString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    [UnixFact]
    public void A_missing_database_is_said_and_a_bad_T0_is_refused()
    {
        var empty = Path.Combine(TestEnv.Home, "orgdigest-empty-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(empty);
        var script = Path.Combine(RepoRoot(), "tools", "org-digest.py");
        var (exit, o, _) = Run(script, ["--state", empty, "--json", "--no-systemd"]);
        Assert.Equal(1, exit);
        Assert.Equal("absent", JsonDocument.Parse(o).RootElement.GetProperty("app_db").GetString());
        Assert.Equal(2, Run(script, ["--state", empty, "--since", "yesterday-ish"]).Exit);
        Assert.Equal(0, Run(script, ["--state", Build().State, "--since", "72h", "--no-systemd"]).Exit);
    }

    // ---- THE VERB ------------------------------------------------------------------------------------------------------------
    [UnixFact]
    public void The_verb_refuses_a_bad_call_before_it_reaches_for_the_env_file_and_for_a_host()
    {
        var script = Path.Combine(RepoRoot(), "tools", "linux-host.sh");
        var home = Path.Combine(TestEnv.Home, "orgdigest-home-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(home);
        var env = new Dictionary<string, string> { ["HOME"] = home };

        // Arguments that are not the verb's, a T0 that is not a plain T0 (it goes onto a remote command line): usage, exit 2.
        foreach (var bad in new[] { new[] { "org-digest", "--bogus" }, new[] { "org-digest", "--since" }, new[] { "org-digest", "--since", "1; touch /tmp/x" },
                                    new[] { "org-digest", "--since", "$(id)" }, new[] { "org-digest", "extra" } })
        {
            var (exit, _, err) = Run(script, bad, env: env, tool: "bash");
            Assert.True(exit == 2, $"{string.Join(' ', bad)} exited {exit}");
            Assert.DoesNotContain("does not exist", err, StringComparison.Ordinal);
        }

        // A good call with no env file stops at the env file: no host is named by the repo and none is guessed.
        var (code, _, refusal) = Run(script, ["org-digest", "--since", "72h", "--json"], env: env, tool: "bash");
        Assert.Equal(2, code);
        Assert.Contains("linux.env does not exist", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verb_sends_the_script_over_stdin_as_the_apps_account_and_writes_nothing_on_the_host()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "linux-host.sh"));
        var digest = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "org-digest.py"));
        var verb = host[host.IndexOf("org_digest()", StringComparison.Ordinal)..];
        verb = verb[..verb.IndexOf("\n}\n", StringComparison.Ordinal)];

        Assert.Contains("sudo -n -u tradeagent python3 -I -", verb, StringComparison.Ordinal);
        Assert.Contains("< \"$here/org-digest.py\"", verb, StringComparison.Ordinal);
        foreach (var write in new[] { "scp", "mktemp", "tee ", "> /", ">> ", "rm ", "mv ", "cp ", "install ", "chmod", "systemctl start", "systemctl stop", "systemctl restart" })
            Assert.DoesNotContain(write, verb, StringComparison.Ordinal);

        // The script holds no write path to the databases: only mode=ro opens, and never a statement beyond SELECT/PRAGMA query_only.
        Assert.Contains("?mode=ro", digest, StringComparison.Ordinal);
        Assert.Contains("PRAGMA query_only=ON", digest, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA query_only=OFF", digest, StringComparison.Ordinal);
        foreach (var write in new[] { ".commit()", "executescript", "executemany", "ATTACH ", "VACUUM", "journal_mode" })
            Assert.DoesNotContain(write, digest, StringComparison.Ordinal);
        foreach (var secret in new[] { "auth.json", ".codex", "id_rsa", "password", "TA_LINUX_KEY", "TA_LINUX_HOST" })
            Assert.DoesNotContain(secret, digest, StringComparison.OrdinalIgnoreCase);
    }
}
