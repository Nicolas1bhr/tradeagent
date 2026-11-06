using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Oversight;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
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
public partial class OrgDigestTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    const string TradeHash = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    /// <summary>What the app writes for a paper mode (<c>AllocationStore.RecordPaper</c> refuses anything else). Never typed by hand.</summary>
    static readonly string Paper = TradingMode.PAPER.ToString();

    /// <summary>D19's stop row: <c>AllocationPolicy.StopV1</c> once U-paper-closure lands, read by name so this file compiles either side of it.</summary>
    const string StopPolicy = "allocation-policy-stop-v1";

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
            // A door call is spelled with underscores: the charter arrow is refused under its door name.
            tools.Record(new ToolCallRow { At = At("2026-10-30T12:30:00Z"), Attempt = "a6", Role = "operations", Tool = BrokerDoor.VerbName(Ops.OrgCharter), Served = false, Refusal = "two structural changes this week" });
            // A backtest goes through the `trade` tool, so its row names the tool and carries the op in its summary: this row is the tool's own.
            var worker = new GrantedWorkerTools("pos-aaaaaa", () => root, () => "a2", gateway: null, ledger: tools, now: () => At("2026-10-31T09:32:00Z"));
            Assert.False(worker.InvokeAsync(new ToolRequest("t1", GrantedWorkerTools.Trade, "{\"op\":\"backtest\"}")).GetAwaiter().GetResult().Served);
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
            db.Exec($$"""
                INSERT INTO org_position(id, unit_id, home_dir, status, seat_runtime, seat_model, created_at, ended_at) VALUES
                  ('pos-aaaaaa', 'div-operations', 'h1', '{{OrgPositionStatus.Active}}', 'codex', 'gpt-5.6-sol', '2026-10-30T10:00:00.0000000Z', NULL),
                  ('pos-bbbbbb', 'div-operations', 'h2', '{{OrgPositionStatus.Dormant}}', NULL, NULL, '2026-10-30T10:00:00.0000000Z', NULL),
                  ('pos-cccccc', 'div-research', 'h3', '{{OrgPositionStatus.Ended}}', 'codex', 'gpt-5.6-sol', '2026-10-29T10:00:00.0000000Z', '2026-10-30T09:00:00.0000000Z');
                INSERT INTO org_event(at, unit_id, position_id, kind, decider, detail) VALUES
                  ('2026-10-30T13:00:00.0000000Z', 'div-operations', 'pos-aaaaaa', '{{OrgEventKind.Hired}}', 'chief', 'a'),
                  ('2026-10-30T13:01:00.0000000Z', 'div-operations', 'pos-bbbbbb', '{{OrgEventKind.Hired}}', 'chief', 'b'),
                  ('2026-10-30T13:02:00.0000000Z', 'div-operations', 'pos-aaaaaa', '{{OrgEventKind.Activated}}', 'chief', 'it activated itself'),
                  ('2026-10-30T13:02:30.0000000Z', 'div-operations', 'pos-dddddd', '{{OrgEventKind.Activated}}', '{{OrgDecider.Owner}}', 'the owner pressed: not the arrow'),
                  ('2026-10-30T13:03:00.0000000Z', 'div-operations', NULL, '{{OrgEventKind.Chartered}}', '{{CouncilRoles.Operations}}', 'inside a6: the turn acted'),
                  ('2026-10-31T10:00:00.0000000Z', 'div-operations', NULL, '{{OrgEventKind.Tilted}}', '{{OrgDecider.App}}', 'c'),
                  ('2026-10-29T10:00:00.0000000Z', 'div-operations', NULL, '{{OrgEventKind.Tilted}}', '{{OrgDecider.App}}', 'before T0');
                INSERT INTO strategy_verdict(campaign_id, version_id, requested_at, holdout_from) VALUES
                  (1, 'v1', '2026-10-31T01:00:00.0000000Z', '2026-01-01'),
                  (1, 'v2', '2026-10-31T02:00:00.0000000Z', '2026-01-01'),
                  (1, 'v3', '2026-10-31T03:00:00.0000000Z', '2026-01-01'),
                  (1, 'v0', '2026-10-01T03:00:00.0000000Z', '2026-01-01'),
                  (3, 'v7', '2026-10-01T04:00:00.0000000Z', '2026-01-01'),
                  (2, 'v9', '2026-10-01T05:00:00.0000000Z', '2026-01-01');
                INSERT INTO strategy_promotion(id, version_id, campaign_id, scoring_policy_sha256, interpreter_build, holdout_dataset_id, holdout_dataset_sha256,
                    execution_model, evaluator_version, holdout_run_id, verdict, reason, at) VALUES
                  ('p1', 'v1', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', '{{PromotionVerdict.Refused}}', '{{PromotionReason.NoTrade}}', '2026-10-31T01:30:00.0000000Z'),
                  ('p2', 'v2', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', '{{PromotionVerdict.PaperEligible}}', 'meets-the-paper-policy-on-historical-evidence', '2026-10-31T02:30:00.0000000Z'),
                  ('p0', 'v0', 1, 's', 'b', 1, 'd', 'e', 'ev', 'r', '{{PromotionVerdict.Refused}}', 'not-profitable-after-costs', '2026-10-01T03:30:00.0000000Z'),
                  ('p7', 'v7', 3, 's', 'b', 1, 'd', 'e', 'ev', 'r', '{{PromotionVerdict.Refused}}', 'not-profitable-after-costs', '2026-10-01T04:30:00.0000000Z'),
                  ('p9', 'v9', 2, 's', 'b', 1, 'd', 'e', 'ev', 'r', '{{PromotionVerdict.Refused}}', 'not-profitable-after-costs', '2026-10-01T05:30:00.0000000Z');
                INSERT INTO referee_holdout(campaign_id, version_id, method, research_row, holdout_days, z_status, alpha_j, wealth_before, wealth_after, addis_status, remembered, at) VALUES
                  (1, 'v1', 1, 1, 30, 'inconclusive', '0.01', '1', '1', '{{AlphaWealth.Inconclusive}}', 0, '2026-10-31T01:20:00.0000000Z'),
                  (1, 'v2', 1, 1, 30, 'enforceable', '0.01', '1', '1', 'tested', 0, '2026-10-31T02:20:00.0000000Z');
                INSERT INTO strategy_allocation(id, version_id, promotion_id, policy_version, max_quantity, currency, effective_from, effective_to, reason, at, mode) VALUES
                  ('al1', 'v2', 'p2', 'pol', '1', 'USD', '2026-10-31T02:40:00.0000000Z', NULL, 'paper', '2026-10-31T02:40:00.0000000Z', '{{Paper}}'),
                  -- A stop is written as a row with an empty window (D19): it is no allocation. An inconclusive slot costs its campaign a trial.
                  ('al2', 'v2', 'p2', '{{StopPolicy}}', '0', 'USD', '2026-10-31T03:00:00.0000000Z', '2026-10-31T03:00:00.0000000Z', 'stopped', '2026-10-31T03:00:00.0000000Z', '{{Paper}}'),
                  ('al3', 'v1', 'p1', '{{AllocationPolicy.InconclusiveV1}}', '1', 'USD', '2026-10-01T03:00:00.0000000Z', '2026-10-02T03:00:00.0000000Z', 'quota', '2026-10-01T03:00:00.0000000Z', '{{Paper}}');
                INSERT INTO paper_envelope(id, connector_id, account_id, symbol, currency, max_quantity, max_notional, max_deployments, granted_at, expires_at, reason, withdrawn_at) VALUES
                  ('env', 'paper', 'PAPER-1', 'BTCUSDT', 'USDT', '1', '5000', 3, '2026-10-01T00:00:00.0000000Z', '2027-10-01T00:00:00.0000000Z', 'founding', NULL),
                  ('env-eth', 'paper', 'PAPER-1', 'ETHUSDT', 'USDT', '1', '5000', 3, '2026-10-01T00:00:00.0000000Z', '2027-10-01T00:00:00.0000000Z', 'founding', NULL),
                  ('env-gone', 'paper', 'PAPER-1', 'XRPUSDT', 'USDT', '1', '5000', 3, '2026-10-01T00:00:00.0000000Z', '2027-10-01T00:00:00.0000000Z', 'founding', '2026-10-02T00:00:00.0000000Z');
                INSERT INTO publication(id, role, attempt, revision, kind, recipients, classification, created_at, content) VALUES
                  ('pub-old', '{{TradingGateway.PaperAllocatorRole}}', NULL, 1, '{{PublicationKind.Note}}', 'research', 'council', '2026-10-01T00:00:00.0000000Z', 'before T0'),
                  ('pub-1', '{{TradingGateway.PaperAllocatorRole}}', NULL, 2, '{{PublicationKind.Note}}', 'research,pos-aaaaaa', 'council', '2026-10-31T09:00:00.0000000Z', 'Forward paper run dep-1: 3 simulated fills.');
                INSERT INTO org_mandate_event(mandate_id, at, kind, actor, attempt_id, reason, evidence) VALUES
                  ('m1', '2026-10-31T09:05:00.0000000Z', '{{MandateEventKind.Issued}}', 'operations', 'a2', 'x', 'x'),
                  ('m1', '2026-10-31T09:06:00.0000000Z', '{{MandateEventKind.Delivered}}', 'pos-aaaaaa', 'a2', 'x', 'x'),
                  ('m1', '2026-10-31T09:07:00.0000000Z', '{{MandateEventKind.Rejected}}', 'operations', 'a2', 'x', 'x'),
                  ('m0', '2026-10-29T09:00:00.0000000Z', '{{MandateEventKind.Accepted}}', 'operations', 'a7', 'before T0', 'x');
                INSERT INTO strategy_run(id, version_id, dataset_id, dataset_sha256, execution_model, outcome, bars, trades, wins, intents, fills, exposure_bars, missing_minutes, faults, trace_sha256, created_at, role, attempt) VALUES
                  ('bt1', 'v2', 1, 'd', 'e', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', '2026-10-31T00:30:00.0000000Z', 'pos-aaaaaa', 'a2'),
                  ('bt2', 'v2', 1, 'd', 'e', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', '2026-10-31T00:40:00.0000000Z', 'pos-aaaaaa', 'a2'),
                  ('r', 'v2', 1, 'd', 'e', 'completed', 1, 0, 0, 0, 0, 0, 0, 0, 't', '2026-10-31T01:00:00.0000000Z', 'pos-aaaaaa', 'a2');
                INSERT INTO ledger_entry(id, kind, author, created_at, attempt) VALUES
                  (1, '{{LedgerKind.Finding}}', 'operations', '2026-10-31T08:01:00.0000000Z', 'a1'),
                  (2, '{{LedgerKind.Decision}}', 'pos-aaaaaa', '2026-10-31T09:08:00.0000000Z', 'a2'),
                  (3, '{{LedgerKind.Kill}}', 'pos-aaaaaa', '2026-10-31T09:09:00.0000000Z', 'a2'),
                  (4, '{{LedgerKind.Finding}}', 'operations', '2026-10-01T08:01:00.0000000Z', 'a7');
                INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt) VALUES
                  -- pos-aaaaaa took the run's note at 09:10: the version after it is an iteration, the one before it is not, and nobody else's is.
                  ('vb', 's', 'c', 'm', 'b', 'ok', 0, '2026-10-31T09:09:00.0000000Z', 'pos-aaaaaa', 'a2'),
                  ('vi', 's', 'c', 'm', 'b', 'ok', 0, '2026-10-31T09:11:00.0000000Z', 'pos-aaaaaa', 'a2'),
                  ('vo', 's', 'c', 'm', 'b', 'ok', 0, '2026-10-31T09:12:00.0000000Z', 'operations', 'a1');
                -- FORECASTS. pos-aaaaaa since T0: 3 written (2 scored), 1 resolved and scored, 1 void. One from before T0 resolved before T0: total 2.
                INSERT INTO org_forecast(id, mandate_id, forecaster, attempt_id, vocabulary, predicate, subject, target, resolve_by, probability, scored, unscored_why, at) VALUES
                  ('fc-1', 'm1', 'pos-aaaaaa', 'a2', 1, 'p', 's', 't', '2026-12-01', '0.6', 1, NULL, '2026-10-31T09:00:00.0000000Z'),
                  ('fc-2', 'm1', 'pos-aaaaaa', 'a2', 1, 'p', 's', 't', '2026-12-01', '0.6', 1, NULL, '2026-10-31T09:01:00.0000000Z'),
                  ('fc-3', 'm1', 'pos-aaaaaa', 'a2', 1, 'p', 's', 't', '2026-12-01', '0.6', 0, 'a vocabulary this build does not read', '2026-10-31T09:02:00.0000000Z'),
                  ('fc-0', 'm0', 'pos-aaaaaa', 'a7', 1, 'p', 's', 't', '2026-12-01', '0.6', 1, NULL, '2026-10-01T09:00:00.0000000Z');
                INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES
                  ('fc-1', '{{ForecastOutcome.Yes}}', 'verdict', 'p2', 'w', '2026-10-31T10:00:00.0000000Z'),
                  ('fc-2', '{{ForecastOutcome.Void}}', 'verdict', 'p2', 'w', '2026-10-31T10:01:00.0000000Z'),
                  ('fc-0', '{{ForecastOutcome.No}}', 'verdict', 'p0', 'w', '2026-10-02T10:00:00.0000000Z');
                -- CAMPAIGNS. #1 and #3 hold BTCUSDT over overlapping months; #2 holds ETHUSDT over the same ones; #4 is #1's older self, closed before T0.
                INSERT INTO dataset(id, source, pair, interval, version, months_attempted, months_present, months_not_published, normalised_path, normalised_sha256, bars, first_bar, last_bar, gaps, gap_runs, gap_runs_truncated, duplicates, incomplete, unreadable, accepted_at, state) VALUES
                  (1, 's', 'BTCUSDT', '1h', 'v', 1, 1, '', 'p', 'h', 1, '2025-01-01T00:00:00.0000000Z', '2026-06-01T00:00:00.0000000Z', 0, '', 0, 0, 0, 0, '2026-10-01', 'accepted'),
                  (2, 's', 'ETHUSDT', '1h', 'v', 1, 1, '', 'p', 'h', 1, '2025-01-01T00:00:00.0000000Z', '2026-06-01T00:00:00.0000000Z', 0, '', 0, 0, 0, 0, '2026-10-01', 'accepted'),
                  (3, 's', 'BTCUSDT', '1h', 'v', 1, 1, '', 'p', 'h', 1, '2025-01-01T00:00:00.0000000Z', '2026-06-01T00:00:00.0000000Z', 0, '', 0, 0, 0, 0, '2026-10-01', 'accepted'),
                  (4, 's', 'BTCUSDT', '1h', 'v', 1, 1, '', 'p', 'h', 1, '2024-01-01T00:00:00.0000000Z', '2025-01-01T00:00:00.0000000Z', 0, '', 0, 0, 0, 0, '2026-10-01', 'accepted');
                INSERT INTO strategy_campaign(id, name, scoring_policy, scoring_policy_sha256, trial_budget, verdict_budget, holdout_dataset_id, holdout_from, opened_at, renewed_from, closed_at, exploration_budget) VALUES
                  (4, 'old', 'p', 'h', 200, 3, 4, '2024-06-01T00:00:00.0000000Z', '2026-01-01', NULL, '2026-02-01T00:00:00.0000000Z', 50),
                  (1, 'btc', 'p', 'h', 200, 3, 1, '2026-01-01T00:00:00.0000000Z', '2026-02-01', 4, NULL, 50),
                  (2, 'eth', 'p', 'h', 200, 3, 2, '2026-01-01T00:00:00.0000000Z', '2026-02-01', NULL, NULL, 50),
                  (3, 'btc again', 'p', 'h', 200, 3, 3, '2026-01-01T00:00:00.0000000Z', '2026-02-01', NULL, NULL, 50);
                INSERT INTO strategy_trial(campaign_id, version_id, run_id, kind, charged, registered_at, charged_to, exploration) VALUES
                  (1, 'v1', 'run-1', 'research', 1, '2026-10-31T00:00:00.0000000Z', NULL, 0),
                  (1, 'v2', 'run-1', 'research', 1, '2026-10-31T00:00:00.0000000Z', NULL, 0),
                  (1, 'v1', 'run-2', 'research', 1, '2026-10-31T00:00:00.0000000Z', NULL, 1),
                  (1, 'v1', 'run-3', 'fixture', 0, '2026-10-31T00:00:00.0000000Z', NULL, 0),
                  (2, 'v9', 'run-4', 'research', 1, '2026-10-31T00:00:00.0000000Z', 1, 0);
                -- THE WAKES OF THE TURNS. Failed and lost are told apart by the launch's state, and a word nobody has heard of stands as it is.
                INSERT INTO mission_event(id, kind, created_at, due_at, consumed_at, consumed_by, disposition, disposition_detail, role) VALUES
                  ('{{MissionEventIds.ForRole(MissionEventIds.PaperRun("dep-1", "end"), "pos-aaaaaa")}}', '{{PublicationKind.Note}}', '2026-10-31T09:00:00.0000000Z', '2026-10-31T09:00:00.0000000Z', '2026-10-31T09:10:00.0000000Z', 'a2', '{{MissionEventDisposition.Answered}}', NULL, 'pos-aaaaaa'),
                  ('wf', 'self', '2026-10-31T09:58:00.0000000Z', '2026-10-31T09:58:00.0000000Z', '2026-10-31T10:00:00.0000000Z', 'a3', '{{MissionEventDisposition.Failed}}', 'the turn failed', 'pos-aaaaaa'),
                  ('wl', 'owner', '2026-10-31T13:29:00.0000000Z', '2026-10-31T13:29:00.0000000Z', '2026-10-31T13:30:00.0000000Z', 'a9', '{{MissionEventDisposition.Failed}}', 'the turn was lost', NULL),
                  ('wl2', 'note', '2026-10-31T13:29:30.0000000Z', '2026-10-31T13:29:30.0000000Z', NULL, NULL, '{{MissionEventDisposition.Blocked}}', 'the seat is out of its envelope', 'pos-bbbbbb'),
                  ('{{"wl" + MissionLoop.AfterLost}}', 'owner', '2026-10-31T13:51:00.0000000Z', '2026-10-31T13:51:00.0000000Z', '2026-10-31T13:51:00.0000000Z', NULL, '{{MissionEventDisposition.Superseded}}', 'x', 'pos-cccccc'),
                  ('wr', 'self', '2026-10-31T07:00:00.0000000Z', '2026-10-31T07:00:00.0000000Z', '2026-10-31T08:00:00.0000000Z', 'a1', 'released', 'a word of a later build', NULL);
                INSERT INTO strategy_deployment(id, version_id, allocation_id, envelope_id, connector_id, account_id, symbol, mode, state, started_at, ended_at, end_reason) VALUES
                  ('dep-paper-active-0001', 'v2', 'al1', 'env', 'paper', 'PAPER-1', 'BTCUSDT', '{{Paper}}', '{{DeploymentState.Active}}', '2026-10-30T13:00:00.0000000Z', NULL, NULL),
                  ('dep-paper-ended-00002', 'v2', 'al1', 'env', 'paper', 'PAPER-1', 'ETHUSDT', '{{Paper}}', '{{DeploymentState.Ended}}', '2026-10-20T13:00:00.0000000Z', '2026-10-21T13:00:00.0000000Z', 'term over'),
                  ('dep-live-suspended-03', 'v2', 'al1', 'env', 'atas', 'LIVE-1', 'ES', '{{TradingMode.LIVE_CONFIRM}}', '{{DeploymentState.Suspended}}', '2026-10-30T13:00:00.0000000Z', NULL, NULL);
                INSERT INTO deployment_op(request_id, deployment_id, bar_open_time, kind, intent, state, answer, created_at, resolved_at) VALUES
                  ('r1', 'dep-paper-active-0001', 'b1', 'entry', '{}', '{{DeploymentOpState.Resolved}}', NULL, '2026-10-30T14:00:00.0000000Z', '2026-10-30T14:00:01.0000000Z'),
                  ('r2', 'dep-paper-active-0001', 'b2', 'exit', '{}', '{{DeploymentOpState.Resolved}}', NULL, '2026-10-30T15:00:00.0000000Z', '2026-10-30T15:00:01.0000000Z'),
                  ('r3', 'dep-paper-active-0001', 'b3', 'entry', '{}', '{{DeploymentOpState.Refused}}', 'the envelope allows 1, the plan asked for 3', '2026-10-31T09:00:00.0000000Z', NULL),
                  ('r4', 'dep-paper-active-0001', 'b4', 'entry', '{}', '{{DeploymentOpState.Resolved}}', NULL, '2026-10-31T08:00:00.0000000Z', '2026-10-31T08:00:01.0000000Z');
                INSERT INTO engineering_log(at, component, event, severity, metadata) VALUES
                  ('2026-10-31T09:40:00.0000000Z', 'Ipc', '{{RefusalMark.Event}}', 'warn', '{"op":"{{Ops.OrgHire}}","code":"INVALID_REQUEST","role":"pos-aaaaaa"}'),
                  ('2026-10-31T09:41:00.0000000Z', 'Ipc', '{{RefusalMark.Event}}', 'warn', '{"op":"{{Ops.OrgHire}}","code":"INVALID_REQUEST","role":"pos-aaaaaa"}'),
                  ('2026-10-31T09:42:00.0000000Z', 'Ipc', 'auth_rejected', 'warn', NULL),
                  ('2026-10-31T09:43:00.0000000Z', 'Other', '{{RefusalMark.Event}}', 'warn', '{"op":"x"}');
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
        var paper = listed.Single(x => x.GetProperty("mode").GetString() == Paper);
        Assert.Equal(3, paper.GetProperty("fills").GetInt32());
        Assert.Equal("10.0000", paper.GetProperty("realised").GetString());
        Assert.Equal("0.2000", paper.GetProperty("fees").GetString());
        Assert.Equal(1, paper.GetProperty("fees_unknown_fills").GetInt32());
        Assert.Equal("2", paper.GetProperty("open_quantity").GetString());
        Assert.Equal("10.0000", paper.GetProperty("unrealised").GetString());
        Assert.Equal(JsonValueKind.Null, paper.GetProperty("net_if_known").ValueKind);   // an unknown fee is not a zero

        // 6. REFUSALS with their words and counts: the tool refusal x3, the other tool x1, the pipe's two kinds, the deployment gate's words.
        var refusals = d.GetProperty("refusals");
        Assert.Equal(10, refusals.GetProperty("total").GetInt32());
        var g0 = refusals.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(7, g0.Count);
        Assert.Equal(3, g0[0].GetProperty("count").GetInt32());
        Assert.Equal("no order verb is offered to this seat", g0[0].GetProperty("words").GetString());
        Assert.Contains(g0, g => g.GetProperty("source").GetString() == "pipe" && g.GetProperty("count").GetInt32() == 2 && g.GetProperty("words").GetString() == "op org-hire / INVALID_REQUEST");
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
        Assert.Contains($"mode '{TradingMode.LIVE_CONFIRM}'", attention);
        Assert.DoesNotContain($"mode '{Paper}'", attention);                              // a paper run is never called "NOT paper"
        Assert.Contains("overdue wake", attention);
        Assert.Contains("LOST", attention);
        Assert.Contains("beta last delivered 4h ago", attention);
        Assert.Contains("parse failure", attention);

        // ON MAIN'S SCHEMA NOTHING THIS FILE NAMES IS MISSING: `absent` is for a home behind or ahead of main, and a misnamed table would hide behind it.
        Assert.DoesNotContain("\"absent\"", d.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(d.GetProperty("pace").GetProperty("act_tables_absent").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("app").GetProperty("schema").GetString()));

        // READ-ONLY, ON DISK: not one byte of either database moved.
        Assert.Equal(before, (Hash(Path.Combine(f.State, "tradeagent.db")), Hash(Path.Combine(f.State, "tape.db"))));
    }

    [UnixFact]
    public void Each_arrow_says_when_it_was_first_reached_how_often_and_its_first_refusal_in_words()
    {
        var d = Json(Build()).GetProperty("arrows");
        var arrows = d.GetProperty("arrows").EnumerateArray().ToDictionary(a => a.GetProperty("arrow").GetString()!);
        (string? First, int Count) Reached(string name) => (arrows[name].GetProperty("first_at").GetString(), arrows[name].GetProperty("count").GetInt32());

        Assert.Equal(("2026-10-30T13:03:00.0000000Z", 1), Reached("charter"));
        Assert.Equal(("2026-10-30T13:00:00.0000000Z", 2), Reached("hire"));
        Assert.Equal(("2026-10-30T13:02:00.0000000Z", 1), Reached("self-activation"));   // the owner's press is not this arrow
        Assert.Equal(("2026-10-31T09:05:00.0000000Z", 1), Reached("mandate"));
        Assert.Equal(("2026-10-31T09:06:00.0000000Z", 1), Reached("delivery"));
        Assert.Equal((null, 0), Reached("accepted"));                                     // m0's is before T0
        Assert.Equal(("2026-10-31T09:07:00.0000000Z", 1), Reached("rejected"));
        Assert.Equal(("2026-10-31T00:30:00.0000000Z", 2), Reached("backtest"));           // the verdict's own holdout run 'r' is no backtest
        Assert.Equal(("2026-10-31T01:00:00.0000000Z", 3), Reached("verdict asked"));
        Assert.Equal(("2026-10-31T01:30:00.0000000Z", 2), Reached("verdict given"));
        Assert.Equal(("2026-10-31T02:40:00.0000000Z", 1), Reached("paper allocation"));   // the stop row and the older slot are not
        Assert.Equal(("2026-10-30T13:00:00.0000000Z", 1), Reached("paper run"));          // a live row and an older paper one are not
        Assert.Equal(("2026-10-30T14:00:00.0000000Z", 3), Reached("paper fill"));
        Assert.Equal(("2026-10-31T09:00:00.0000000Z", 1), Reached("note raised"));
        Assert.Equal(("2026-10-31T09:10:00.0000000Z", 1), Reached("note taken"));
        Assert.Equal(("2026-10-31T09:09:00.0000000Z", 1), Reached("kill"));               // the author's ledger entry
        Assert.Equal(("2026-10-31T09:11:00.0000000Z", 1), Reached("iterate"));            // vi, after the note was taken; not vb, before it, nor vo, another's

        Assert.Equal(16, d.GetProperty("reached").GetInt32());
        Assert.Equal(17, d.GetProperty("of").GetInt32());
        Assert.Equal(["accepted"], d.GetProperty("not_reached").EnumerateArray().Select(a => a.GetString()!).ToArray());

        // The door's org_charter is the op's org-charter; the first refusal of the run is that one, in its own words and hour.
        Assert.Equal(1, arrows["charter"].GetProperty("refused").GetInt32());
        Assert.Equal("two structural changes this week", arrows["charter"].GetProperty("first_refusal").GetString());
        Assert.Equal("2026-10-30T12:30:00.0000000Z", d.GetProperty("first_refusal").GetProperty("at").GetString());
        // A backtest refused through the `trade` tool is the backtest arrow's: the tool's own row, its op read from its summary.
        Assert.Equal(1, arrows["backtest"].GetProperty("refused").GetInt32());
        // And a pipe refusal is its op's, by the op's own name.
        Assert.Equal(2, arrows["hire"].GetProperty("refused").GetInt32());
        Assert.Contains("keep", d.GetProperty("cannot_be_read").GetString(), StringComparison.Ordinal);
    }

    [UnixFact]
    public void The_pace_is_turns_per_day_cost_per_turn_the_idle_share_and_the_wait_behind_the_lane()
    {
        var p = Json(Build()).GetProperty("pace");

        // T0 to Now is 26 hours, 1.0833 days. 8 launches, 8.7 charged.
        Assert.Equal(1.08, p.GetProperty("days").GetDouble());
        Assert.Equal(8, p.GetProperty("turns").GetInt32());
        Assert.Equal(7.38, p.GetProperty("turns_per_day").GetDouble());
        Assert.Equal("1.0875", p.GetProperty("cost_per_turn").GetString());

        // Clean endings: a1, a2, a5, a6. a1 wrote a ledger entry, a2 a mandate event, a6's span holds its chart act (a charter names no
        // launch). a5 left nothing: one idle of four. a3 and a4 failed, a9 was lost and a8 is open: none of them is a clean turn.
        Assert.Equal(4, p.GetProperty("clean_turns").GetInt32());
        Assert.Equal(1, p.GetProperty("idle_turns").GetInt32());
        Assert.Equal("25.0", p.GetProperty("idle_share_pct").GetString());

        // The waits, due to taken: 600 and 120 on pos-aaaaaa, 60 and 3600 on operations. Median of the four 360, max 3600.
        Assert.Equal(360, p.GetProperty("wait_median_s").GetInt32());
        Assert.Equal(3600, p.GetProperty("wait_max_s").GetInt32());
        var seats = p.GetProperty("positions").EnumerateArray().ToDictionary(x => x.GetProperty("position").GetString()!);
        Assert.Equal(360, seats["pos-aaaaaa"].GetProperty("wait_median_s").GetInt32());
        Assert.Equal(600, seats["pos-aaaaaa"].GetProperty("wait_max_s").GetInt32());
        Assert.Equal(1830, seats["operations"].GetProperty("wait_median_s").GetInt32());
        Assert.Equal("1.3250", seats["operations"].GetProperty("cost_per_turn").GetString());   // 5.3 over 4 turns
        Assert.Equal(3.69, seats["operations"].GetProperty("turns_per_day").GetDouble());
        Assert.Equal("gpt-5.6-sol", Assert.Single(seats["pos-aaaaaa"].GetProperty("models").EnumerateArray()).GetString());
        Assert.Equal(1, seats["board-1"].GetProperty("idle_turns").GetInt32());

        // The vendor's own words: a3 and a4 are 429s.
        Assert.Equal(2, p.GetProperty("vendor_limit_refusals").GetInt32());
        Assert.Equal("pos-aaaaaa", p.GetProperty("first_vendor_limit").GetProperty("position").GetString());
    }

    [UnixFact]
    public void The_signal_is_forecasts_to_fifty_and_budgets_spent_as_the_apps_own_gates_count_them()
    {
        var f = Build();
        var s = Json(f).GetProperty("signal");

        // pos-aaaaaa: written 3 since T0 (one unscored); resolved scored non-void 1 since T0, 2 in all. 48 left at 1 in 1.0833 days = 52.0 days.
        var m = Assert.Single(s.GetProperty("forecasts").GetProperty("managers").EnumerateArray());
        Assert.Equal("pos-aaaaaa", m.GetProperty("position").GetString());
        Assert.Equal(3, m.GetProperty("written_since").GetInt32());
        Assert.Equal(2, m.GetProperty("scored_since").GetInt32());
        Assert.Equal(1, m.GetProperty("resolved_since").GetInt32());
        Assert.Equal(2, m.GetProperty("resolved_total").GetInt32());
        Assert.Equal(52.0, m.GetProperty("days_to_50").GetDouble());

        var byId = s.GetProperty("campaigns").GetProperty("campaigns").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
        Assert.False(byId.ContainsKey(4));                                                 // closed before T0
        var btc = byId[1];
        Assert.Equal("BTCUSDT", btc.GetProperty("pair").GetString());
        Assert.Equal(1, btc.GetProperty("exploration").GetInt32());
        Assert.Equal(50, btc.GetProperty("exploration_budget").GetInt32());

        // AND THE FIGURES ARE THE APP'S OWN: CampaignStore's gates read the same rows and must agree, whatever the home holds.
        using var db = new Database(Path.Combine(f.State, "tradeagent.db"));
        var store = new CampaignStore(db);
        foreach (var id in new long[] { 1, 2, 3 })
        {
            Assert.Equal(store.TrialsCharged(id), byId[(int)id].GetProperty("trials").GetInt32());
            Assert.Equal(store.JudgementsSpent(id), byId[(int)id].GetProperty("verdicts").GetInt32());
        }
        // run-1 twice is one trial, run-3 was not charged, run-4 was charged here from #2, and the inconclusive slot is one: 4.
        Assert.Equal(4, btc.GetProperty("trials").GetInt32());
        // Worked by hand too: #1's four, and #3's over the same months of the same pair; never #2's, another pair's.
        Assert.Equal(5, btc.GetProperty("verdicts").GetInt32());
        Assert.Equal(5, byId[3].GetProperty("verdicts").GetInt32());
        Assert.Equal(1, byId[2].GetProperty("verdicts").GetInt32());

        Assert.Equal(1, s.GetProperty("candidates").GetProperty("admissible_since").GetInt32());   // p2, paper-eligible
        Assert.Equal(1, s.GetProperty("ledger").GetProperty("finding").GetInt32());
        Assert.Contains("found a finding again", s.GetProperty("cannot_be_read").GetString(), StringComparison.Ordinal);
    }

    [UnixFact]
    public void A_failed_turns_wakes_and_a_lost_turns_are_told_apart_and_a_new_word_stands_as_it_is()
    {
        var f = Build();
        var d = Json(f);
        var w = d.GetProperty("wakes");
        Assert.Equal(1, w.GetProperty("failed_turn_wakes_since").GetInt32());              // wf, on a3
        Assert.Equal(1, w.GetProperty("lost_turn_wakes_since").GetInt32());                // wl reads `failed`, but a9 was LOST
        Assert.Equal(1, w.GetProperty("raised_again_after_lost_since").GetInt32());
        Assert.Equal(1, w.GetProperty("consumed_since_by_disposition").GetProperty("released").GetInt32());
        var blocked = Assert.Single(w.GetProperty("blocked_now").EnumerateArray());
        Assert.Equal("the seat is out of its envelope", blocked.GetProperty("words").GetString());
        Assert.Equal("pos-bbbbbb", Assert.Single(blocked.GetProperty("roles").EnumerateArray()).GetString());

        // A launch's state this file has not heard of is an error in its own word, never folded into ENDED.
        using (var db = new Database(Path.Combine(f.State, "tradeagent.db")))
            db.Exec("INSERT INTO ai_attempt(id, started_at, runtime, state, reserved_cost, role) VALUES('a-new', '2026-10-31T12:30:00.0000000Z', 'codex', 'TIMED-OUT', '0.1', 'pos-aaaaaa');");
        var errors = Json(f).GetProperty("errors");
        Assert.Contains(errors.GetProperty("groups").EnumerateArray(), g => g.GetProperty("state").GetString() == "TIMED-OUT");
    }

    [UnixFact]
    public void Evidence_per_pair_and_the_paper_notes_are_read()
    {
        var dep = Json(Build()).GetProperty("deployments");
        var envelopes = dep.GetProperty("envelopes").EnumerateArray().ToDictionary(e => e.GetProperty("symbol").GetString()!);
        Assert.Equal(2, envelopes.Count);                                                  // XRPUSDT's was withdrawn
        Assert.Equal(2, envelopes["BTCUSDT"].GetProperty("open").GetInt32());              // the active run and the suspended one; an ended run holds no slot
        Assert.Equal(3, envelopes["BTCUSDT"].GetProperty("max_deployments").GetInt32());
        Assert.Equal(1, dep.GetProperty("allocations").GetProperty("stopped_since").GetInt32());
        Assert.Equal(1, dep.GetProperty("notes").GetProperty("since").GetInt32());
        Assert.StartsWith("Forward paper run", dep.GetProperty("notes").GetProperty("last").GetString(), StringComparison.Ordinal);
    }

    [UnixFact]
    public void A_home_behind_main_names_what_it_lacks_and_every_other_section_still_reads()
    {
        var f = Build();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(f.State, "tradeagent.db")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=OFF; DROP TABLE org_forecast_resolution; DROP TABLE org_forecast; DROP TABLE org_decision; DROP TABLE org_mandate_event; "
                              + "DROP TABLE paper_envelope; ALTER TABLE strategy_trial DROP COLUMN exploration;";
            cmd.ExecuteNonQuery();
        }
        var d = Json(f);
        Assert.Equal("org_forecast", d.GetProperty("signal").GetProperty("forecasts").GetProperty("absent").GetString());
        var accepted = d.GetProperty("arrows").GetProperty("arrows").EnumerateArray().Single(a => a.GetProperty("arrow").GetString() == "accepted");
        Assert.Equal("org_mandate_event", accepted.GetProperty("absent").GetString());
        // A home with no decisions table still reads the arrows that have another record: the ledger's kill entry.
        var killed = d.GetProperty("arrows").GetProperty("arrows").EnumerateArray().Single(a => a.GetProperty("arrow").GetString() == "kill");
        Assert.Equal(1, killed.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("deployments").GetProperty("envelopes").ValueKind);
        Assert.Contains("org_decision", d.GetProperty("pace").GetProperty("act_tables_absent").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(JsonValueKind.Null, d.GetProperty("signal").GetProperty("campaigns").GetProperty("campaigns")[0].GetProperty("exploration").ValueKind);
        Assert.DoesNotContain(d.GetProperty("attention").EnumerateArray(), a => a.GetString()!.Contains("unreadable", StringComparison.Ordinal));
        var (exit, text, err) = Run(Path.Combine(RepoRoot(), "tools", "org-digest.py"), DigestArgs(f));
        Assert.True(exit == 0, err);
        Assert.Contains("forecasts: org_forecast is not on this home", text, StringComparison.Ordinal);
    }

    [UnixFact]
    public void A_stopped_apps_home_is_read_as_it_stands_and_a_running_apps_unsaved_rows_are_seen()
    {
        // The deliberate restart: the app is down, so its WAL databases have no -wal beside them, and nothing appears beside them after.
        var f = Build();
        var before = Directory.GetFiles(f.State).Order().ToArray();
        Assert.Equal("present", Json(f).GetProperty("app_db").GetString());
        Assert.Equal(before, Directory.GetFiles(f.State).Order().ToArray());

        // The app running: a writer holds the database open with a row only in its -wal, and the digest reads that row, not a stale file.
        using var running = new Database(Path.Combine(f.State, "tradeagent.db"));
        running.Exec("INSERT INTO org_event(at, unit_id, kind, decider, detail) VALUES('2026-10-31T13:59:00.0000000Z', 'div-operations', 'only-in-the-wal', 'app', 'd');");
        Assert.True(File.Exists(Path.Combine(f.State, "tradeagent.db-wal")));
        var kinds = Json(f).GetProperty("org_events").GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString());
        Assert.Contains("only-in-the-wal", kinds);
    }

    [UnixFact]
    public void The_monitors_act_on_list_is_raised_at_the_top()
    {
        var f = Build();
        using (var db = new Database(Path.Combine(f.State, "tradeagent.db")))
        {
            var attempts = new AiAttemptStore(db);
            Attempt(attempts, "over", null, "2026-10-31T13:00:00Z", BillingClasses.Plan, "gpt-5.6-sol", 1m, 1000m, 0);   // past any day-total
            var tools = new ToolCallStore(db);
            for (var i = 0; i < 5; i++)
                tools.Record(new ToolCallRow { At = At("2026-10-31T12:00:00Z").AddMinutes(i), Attempt = "a2", Role = "pos-aaaaaa", Tool = "backtest", Served = false, Refusal = "the same words, five times" });
            db.SetKv("settings", TradeAgent.Core.Json.Write(new TradeAgentSettings { AiWorksOnItsOwn = false }));
        }
        var d = Json(f);
        var attention = string.Join("\n", d.GetProperty("attention").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains("NOT set to work on its own", attention, StringComparison.Ordinal);    // proves the app's own spelling of the setting
        Assert.Contains("AI day-total PASSED", attention, StringComparison.Ordinal);
        Assert.Contains("refused 5x: backtest: the same words, five times", attention, StringComparison.Ordinal);
        Assert.Contains("on failed turns", attention, StringComparison.Ordinal);
    }

    // ---- THE WORDS ------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// EVERY NAME IN THE DIGEST'S <c>class W</c>, TO WHAT THE APP WRITES. A constant here is the app's own, so a word renamed in the app and not in
    /// the digest goes red, and a word this table does not list goes red too: a new comparison is traced before it lands.
    /// </summary>
    static Dictionary<string, object> WordsTheAppWrites() => new()
    {
        ["MODE_OBSERVE"] = TradingMode.OBSERVE.ToString(), ["MODE_PAPER"] = Paper,
        ["DEPLOYMENT_ENDED"] = DeploymentState.Ended, ["DEPLOYMENT_OP_REFUSED"] = DeploymentOpState.Refused, ["POSITION_ACTIVE"] = OrgPositionStatus.Active,
        ["VERDICT_PROMOTED"] = PromotionVerdict.Promoted, ["VERDICT_PAPER_ELIGIBLE"] = PromotionVerdict.PaperEligible, ["REASON_NO_TRADE"] = PromotionReason.NoTrade,
        ["ADDIS_INCONCLUSIVE"] = AlphaWealth.Inconclusive,
        ["ORG_CHARTERED"] = OrgEventKind.Chartered, ["ORG_HIRED"] = OrgEventKind.Hired, ["ORG_ACTIVATED"] = OrgEventKind.Activated,
        ["DECIDER_OWNER"] = OrgDecider.Owner, ["DECIDER_APP"] = OrgDecider.App, ["DECIDER_BOARD"] = OrgDecider.Board,
        ["MANDATE_ISSUED"] = MandateEventKind.Issued, ["MANDATE_DELIVERED"] = MandateEventKind.Delivered,
        ["MANDATE_ACCEPTED"] = MandateEventKind.Accepted, ["MANDATE_REJECTED"] = MandateEventKind.Rejected,
        ["DISPOSITION_BLOCKED"] = MissionEventDisposition.Blocked, ["DISPOSITION_FAILED"] = MissionEventDisposition.Failed,
        ["WAKE_PAPER_RUN"] = MissionEventIds.PaperRun("", "")[..^1], ["WAKE_AFTER_LOST"] = MissionLoop.AfterLost,
        ["PUBLICATION_ALLOCATOR"] = TradingGateway.PaperAllocatorRole, ["PUBLICATION_NOTE"] = PublicationKind.Note,
        ["LEDGER_KILL"] = LedgerKind.Kill, ["OUTCOME_VOID"] = ForecastOutcome.Void, ["LOG_OP_REFUSED"] = RefusalMark.Event,
        ["PIPE_REFUSALS"] = OrgSnapshot.PipeRefusalEvents, ["POLICY_INCONCLUSIVE"] = AllocationPolicy.InconclusiveV1,
        ["OP_ORG_CHARTER"] = Ops.OrgCharter, ["OP_ORG_HIRE"] = Ops.OrgHire, ["OP_MANDATE_ASSIGN"] = Ops.MandateAssign, ["OP_MANDATE_DELIVER"] = Ops.MandateDeliver,
        ["OP_MANDATE_ACCEPT"] = Ops.MandateAccept, ["OP_MANDATE_REJECT"] = Ops.MandateReject, ["OP_BACKTEST"] = Ops.Backtest, ["OP_VERDICT"] = Ops.Verdict,
        ["TOOLS_CARRYING_AN_OP"] = new[] { GrantedWorkerTools.Trade, GrantedWorkerTools.Data, GrantedWorkerTools.Report },
        ["ROLE_DEFAULT"] = CouncilRoles.Default, ["BOARD_PREFIX"] = BoardSeats.Prefix, ["CLIENT_ORDER_PREFIX"] = TradingGateway.ClientOrderIdFor(""),
        ["SIDE_SELL"] = TradeAgent.ConnectorSdk.OrderSide.Sell.ToString(),
        ["BILLING_PLAN"] = BillingClasses.Plan, ["BILLING_KEY"] = BillingClasses.Key, ["BILLING_UNKNOWN"] = BillingClasses.Unknown,
    };

    /// <summary>
    /// THE WORDS THE APP HAS NO CONSTANT FOR: it spells them in one writer's text, so the digest's word is looked for there. A word moved or
    /// respelled on either side goes red.
    /// </summary>
    static readonly (string Word, string File, string Writer)[] WordsInAWriter =
    [
        ("ATTEMPT_LAUNCHED", "src/TradeAgent.Core/Db/AiAttemptStore.cs", "WHERE id=$id AND state='{0}'"),
        ("ATTEMPT_ENDED", "src/TradeAgent.Core/Db/AiAttemptStore.cs", "state='{0}', ended_at=$end"),
        ("ATTEMPT_LOST", "src/TradeAgent.Core/Db/AiAttemptStore.cs", "SET state='{0}', ended_at=$at"),
        ("PROOF_GAP", "src/TradeAgent.Core/Db/CellProofStore.cs", "row.Gap ? \"{0}\" : "),
        ("BOARD_FOUNDED", "src/TradeAgent.Core/Board/BoardStore.cs", "VALUES($at, '{0}', 1, $schema, $share, $detail)"),
        ("BOARD_BUDGET_SHARE", "src/TradeAgent.Core/Board/BoardStore.cs", "VALUES($at, '{0}', NULL, NULL, $share, NULL, $detail)"),
        ("LOG_IPC", "src/TradeAgent.Gateway/GatewayPipeServer.cs", "Log.Engineering(\"{0}\", \"protocol_rejected\""),
        ("META_SCHEMA_VERSION", "src/TradeAgent.Core/Db/Database.cs", "SELECT value FROM meta WHERE key='{0}'"),
        ("KV_SETTINGS", "src/TradeAgent.Gateway/TradingGateway.cs", "_db.SetKv(\"{0}\", Json.Write(Settings))"),
    ];

    [Fact]
    public void Every_word_the_digest_compares_is_the_one_the_app_writes()
    {
        var script = Path.Combine(RepoRoot(), "tools", "org-digest.py");
        if (OperatingSystem.IsWindows()) return;    // the script runs on the Linux host; the table it is held to compiles here regardless
        var (exit, o, e) = Run(script, ["--vocabulary"]);
        Assert.True(exit == 0, e);
        var digest = JsonDocument.Parse(o).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        string Word(string name) => digest[name].GetString()!;

        var theApp = WordsTheAppWrites();
        foreach (var (name, want) in theApp)
        {
            Assert.True(digest.ContainsKey(name), $"the digest no longer has {name}: take it out of this table with the comparison");
            if (want is string[] many) Assert.Equal(many, digest[name].EnumerateArray().Select(w => w.GetString()!).ToArray());
            else Assert.True(Word(name) == (string)want, $"{name} is '{Word(name)}' in the digest and '{want}' in the app");
        }
        foreach (var (name, file, writer) in WordsInAWriter)
            Assert.True(File.ReadAllText(Path.Combine(RepoRoot(), file)).Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, writer, Word(name)), StringComparison.Ordinal),
                $"{name} '{Word(name)}' is not what {file} writes");

        // The two words of a decision the app reserves and no verb writes on main: held to its list, and said in the digest's words.
        foreach (var name in new[] { "DECISION_FAMILY_KILL", "DECISION_FAMILY_BRANCH" }) Assert.Contains(Word(name), OrgDecisionKind.Mandatory);

        // The one word of a unit still landing: held to the app's constant from the moment it exists.
        if (typeof(AllocationPolicy).GetField("StopV1") is { } stop) Assert.Equal((string)stop.GetValue(null)!, Word("POLICY_STOP"));
        Assert.Equal(StopPolicy, Word("POLICY_STOP"));

        // The setting's key, as the app serialises it, read from the app's own writer.
        Assert.Contains($"\"{Word("SETTING_WORKS_ON_ITS_OWN")}\":", TradeAgent.Core.Json.Write(new TradeAgentSettings()), StringComparison.Ordinal);

        // AND NOTHING IS LEFT UNTRACED: every word in the digest is in one of the tables above.
        var traced = theApp.Keys.Concat(WordsInAWriter.Select(w => w.Word)).Concat(["DECISION_FAMILY_KILL", "DECISION_FAMILY_BRANCH", "POLICY_STOP", "SETTING_WORKS_ON_ITS_OWN"]);
        Assert.Empty(digest.Keys.Except(traced));
    }

    /// <summary>What a comparison against a column looks like when its word is typed in place, in SQL or in Python, instead of coming from <c>class W</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("""(?:=|<>|!=|\bIN\s*\(|\bLIKE)\s*'[^'{?]|\[["']\w+["']\]\)?(?:\.\w+\(\))?\s*(?:==|!=|\bnot in\b|\bin\b)\s*\(?(?:None,\s*)?["']""")]
    private static partial System.Text.RegularExpressions.Regex TypedInPlace();

    [Fact]
    public void No_word_is_typed_into_a_comparison_outside_the_digests_vocabulary()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "org-digest.py"));
        var (from, to) = (Array.FindIndex(lines, l => l.StartsWith("class W:", StringComparison.Ordinal)), Array.FindIndex(lines, l => l.StartsWith("ADMISSIBLE =", StringComparison.Ordinal)));
        Assert.True(from > 0 && to > from, "the digest's vocabulary is not where this test looks");
        var typed = lines.Select((l, i) => (Line: l, At: i)).Where(x => x.At < from || x.At > to)
            .Where(x => !x.Line.TrimStart().StartsWith('#') && !x.Line.Contains("[\"app_db\"]", StringComparison.Ordinal))    // the digest's own field, not a column
            .Where(x => TypedInPlace().IsMatch(x.Line)).Select(x => $"line {x.At + 1}: {x.Line.Trim()}").ToList();
        Assert.True(typed.Count == 0, "a word typed into a comparison, not taken from class W:\n" + string.Join("\n", typed));
    }

    // ---- THE REAL LOOP -------------------------------------------------------------------------------------------------------
    /// <summary>
    /// THE DIGEST OVER A HOME EVERY WRITER OF THE APP WROTE: <see cref="OrganisationLoopTests"/> calls this at the end of the whole loop, so no
    /// row here was typed by a test. It is how a word the fixture repeats wrongly would be seen: the loop reached these arrows, so the digest must say so.
    /// </summary>
    internal static JsonElement OnALiveHome(Database db)
    {
        var state = Path.Combine(TestEnv.Home, "orgdigest-live-" + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(state);
        var image = Path.Combine(state, "tradeagent.db");
        db.Read(_ => { db.Exec($"VACUUM INTO '{image.Replace("'", "''", StringComparison.Ordinal)}'"); return 0; });
        var (exit, o, e) = Run(Path.Combine(RepoRoot(), "tools", "org-digest.py"), ["--state", state, "--since", "1d", "--no-systemd", "--json"]);
        Assert.True(exit == 0, $"exit {exit}: {e}");
        return JsonDocument.Parse(o).RootElement.Clone();
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
        foreach (var word in new[] { "positions:", "turns/cost", "org events since T0", "arrows reached since T0", "verdicts:", "deployments (SIMULATION", "refusals since T0",
                                     "AI launches since T0", "pace over", "forecasts:", "campaigns:", "admissible candidates:", "AI day-total", "next wake per active position",
                                     "cell proof:", "tape:" })
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
