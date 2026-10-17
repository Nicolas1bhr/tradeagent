using System.Globalization;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// DECISIONS, FORECASTS, ESCALATIONS AND LATERAL NOTES (<c>U-org-decisions</c>, O5b; ORCHESTRATION §§ 2.3, 2.9–2.11; ORGANISATION
/// §§ 4, 5). NOTHING HERE REACHES A REAL MODEL (R1): every caller is an in-process <see cref="GatewayPipeServer"/> call under a
/// launch id the TEST seeded in the ledger with the seat assurance it names — the <c>OrgRightsTests</c> / <c>OrgAssignmentTests</c>
/// pattern — and every position beyond the legacy pair is written by the test with raw SQL, as no writer of the chart exists yet.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class OrgDecisionTests
{
    /// <summary>
    /// The chart: <see cref="Chief"/> heads <see cref="Sub"/> under Research's division; <see cref="Head"/> heads <see cref="Team"/>
    /// under it with two members; <see cref="Outsider"/> heads a team directly under <c>div-research</c>.
    /// </summary>
    const string Chief = "pos-c3c0ffee0001", Head = "pos-a1c0ffee0001", Member = "pos-a1c0ffee0002", Other = "pos-a1c0ffee0003",
        Outsider = "pos-b2c0ffee0001";

    const string Sub = "sub-c3c0ffee", Team = "team-a1c0ffee", Elsewhere = "team-b2c0ffee";

    const string Stamp = "2026-10-11T12:00:00.0000000Z";

    static readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddDays(7);

    // ---- the chart, as only a test may write it --------------------------------------------------------------------

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    static void HeadAUnit(Database db, string position, string unit, string parent, string kind = "team")
    {
        Exec(db, "INSERT INTO org_unit(id, parent_id, kind, head_position_id, status, created_at) VALUES($t,$p,$k,NULL,'active',$at)",
            ("$t", unit), ("$p", parent), ("$k", kind), ("$at", Stamp));
        Seat(db, position, unit);
        Exec(db, "UPDATE org_unit SET head_position_id=$id WHERE id=$t", ("$id", position), ("$t", unit));
    }

    static void Seat(Database db, string id, string unit, string status = OrgPositionStatus.Active) =>
        Exec(db, "INSERT INTO org_position(id, unit_id, home_dir, status, created_at) VALUES($id,$u,$h,$s,$at)",
            ("$id", id), ("$u", unit), ("$h", $"org/{id}"), ("$s", status), ("$at", Stamp));

    static void Chart(Database db)
    {
        HeadAUnit(db, Chief, Sub, OrgIds.Research, "subdivision");
        HeadAUnit(db, Head, Team, Sub);
        Seat(db, Member, Team);
        Seat(db, Other, Team);
        HeadAUnit(db, Outsider, Elsewhere, OrgIds.Research);
    }

    /// <summary>A launch the test recorded, as the ledger would: LAUNCHED, on a seat of <paramref name="seatClass"/> — never a real model.</summary>
    static string Launch(Database db, string role, string seatClass = SeatClasses.Harness)
    {
        var id = $"turn-{Guid.NewGuid():n}"[..17];
        var admission = new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, ReservedCost = 0.10m, SeatClass = seatClass,
            Assurance = SeatClasses.AssuranceOf(seatClass) is { } a ? SeatClasses.Name(a) : null
        });
        Assert.True(admission.Admitted, admission.Refusal);
        return id;
    }

    static IpcRequest Frame(string op, params (string Key, object Value)[] args) => new()
    {
        Op = op,
        Session = "decisions",
        RequestId = "decisions-" + Guid.NewGuid().ToString("n")[..12],
        Args = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value))
    };

    static JsonElement Body(IpcResponse r) => JsonDocument.Parse(Json.Write(r.Data)).RootElement;

    static long Count(Database db, string table, string where = "1") => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table} WHERE {where}");
        return (long)c.ExecuteScalar()!;
    });

    static async Task<(TradingGateway Gw, Database Db, GatewayPipeServer Server)> Pipe()
    {
        var (gw, _, db) = await TestEnv.Ready();
        Chart(db);
        return (gw, db, new GatewayPipeServer(gw, IpcToken.Ensure()));
    }

    // ---- item 1: the records ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE FIVE RECORDS ARE APPEND-ONLY AND THEIR SHAPES ARE THE SCHEMA'S (rung 41): no UPDATE or DELETE reaches a decision, a
    /// forecast, a resolution, an escalation or a note; a forecast is on exactly one of a decision and a mandate, its probability a
    /// decimal text from 0 to 1, and an unscored one says why — each refused by a CHECK, so no writer on the app's connection can
    /// store a row that says otherwise.
    /// </summary>
    [Fact]
    public void The_decision_records_are_append_only_and_their_shapes_are_the_schemas()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            db.Read(_ => { using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));

        var entry = new ResearchLedger(db).Add(Head, null, LedgerKind.Finding, "a rationale", LedgerMark.Claim, null, null, null).Id;
        Exec(db, """
            INSERT INTO org_decision(id, position, kind, options, choice, refs, ledger_entry_id, at)
            VALUES('dec-000000000001', $p, 'routine', '["a","b"]', 'a', '[]', $e, $at)
            """, ("$p", Head), ("$e", entry), ("$at", Stamp));
        const string forecast = """
            INSERT INTO org_forecast(id, decision_id, mandate_id, forecaster, vocabulary, predicate, subject, target, resolve_by,
                                     probability, scored, unscored_why, at)
            VALUES($id, $d, $m, $p, 1, 'verdict-class', 'v', 'promoted', $at, $prob, $scored, $why, $at)
            """;
        Exec(db, forecast, ("$id", "fc-000000000001"), ("$d", "dec-000000000001"), ("$m", null), ("$p", Head), ("$at", Stamp),
            ("$prob", "0.7"), ("$scored", 1), ("$why", null));
        Exec(db, "INSERT INTO org_forecast_resolution(forecast_id, outcome, record_kind, record_id, why, at) VALUES('fc-000000000001','void','version','v','none',$at)",
            ("$at", Stamp));
        Exec(db, "INSERT INTO org_escalation(id, sender, unit_id, reason, skipped, day, at) VALUES('esc-000000000001',$p,$u,'r','[]','2026-10-11',$at)",
            ("$p", Head), ("$u", Team), ("$at", Stamp));
        Exec(db, "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('lat-000000000001',$p,$q,'t','[]',$at)",
            ("$p", Head), ("$q", Member), ("$at", Stamp));

        foreach (var table in new[] { "org_decision", "org_forecast", "org_forecast_resolution", "org_escalation", "org_lateral" })
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, $"UPDATE {table} SET at='x'"));
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, $"DELETE FROM {table}"));
            Assert.Equal(1L, Count(db, table));
        }

        // THE SHAPES: both or neither of decision and mandate; a probability outside 0..1 or not a decimal text; scored with a reason,
        // or unscored without one; an escalation with a recipient and no wake; a note to oneself; an id the app would not mint.
        foreach (var (d, m, prob, scored, why) in new (string?, string?, object, int, string?)[]
                 {
                     ("dec-000000000001", "man-x", "0.5", 1, null), (null, null, "0.5", 1, null), ("dec-000000000001", null, "1.5", 1, null),
                     ("dec-000000000001", null, "-0.1", 1, null),
                     ("dec-000000000001", null, "0.5", 1, "why"), ("dec-000000000001", null, "0.5", 0, null)
                 })
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db, forecast, ("$id", "fc-000000000002"), ("$d", d), ("$m", m),
                ("$p", Head), ("$at", Stamp), ("$prob", prob), ("$scored", scored), ("$why", why)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_escalation(id, sender, unit_id, to_position, reason, skipped, day, at) VALUES('esc-000000000002',$p,$u,$q,'r','[]','d',$at)",
            ("$p", Head), ("$u", Team), ("$q", Chief), ("$at", Stamp)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('lat-000000000002',$p,$p,'t','[]',$at)",
            ("$p", Head), ("$at", Stamp)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(db,
            "INSERT INTO org_lateral(id, sender, to_position, text, refs, at) VALUES('chosen-by-a-caller',$p,$q,'t','[]',$at)",
            ("$p", Head), ("$q", Member), ("$at", Stamp)));
        Assert.Equal(1L, Count(db, "org_forecast"));
    }

    // ---- item 2: decisions and the ledger ------------------------------------------------------------------------------

    static readonly string[] Options = ["test the funding skew on a second venue", "drop the family"];

    static IpcRequest RecordFrame(object? refs = null, object? forecasts = null, string? kind = null, string choice = "test the funding skew on a second venue") =>
        Frame(Ops.DecisionRecord, [
            ("options", Options), ("choice", choice), ("rationale", "one venue's skew could be its own microstructure"),
            .. refs is null ? Array.Empty<(string, object)>() : [("refs", refs)],
            .. forecasts is null ? Array.Empty<(string, object)>() : [("forecasts", forecasts)],
            .. kind is null ? Array.Empty<(string, object)>() : [("kind", kind)]
        ]);

    /// <summary>A strategy version the ledger recorded, as only a test may write it.</summary>
    static void Version(Database db, string id) =>
        Exec(db, """
            INSERT INTO strategy_version(id, source, canonical, manifest, interpreter_build, parse_verdict, warm_up_bars, created_at, role, attempt)
            VALUES($id, 'x', 'x', '{}', 'test', 'ok', 0, $at, $role, NULL)
            """, ("$id", id), ("$at", Stamp), ("$role", Member));

    /// <summary>
    /// (a) A BROKERED POSITION RECORDS A DECISION, ITS LEDGER ENTRY AND ITS FORECASTS IN ONE WRITE: the app mints <c>dec-…</c>,
    /// writes the decision row and the rationale as the position's own <c>decision</c> entry in the research ledger — marked claim,
    /// linked by the row — in one write; a write killed after the entry leaves NOTHING (the entry rolls back with it). RED at base:
    /// no <c>decision-record</c> op existed (unknown operation).
    /// </summary>
    [Fact]
    public async Task A_brokered_position_records_a_decision_its_ledger_entry_and_its_forecasts_in_one_write()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Version(db, "ver-a1");

        var reply = await server.CallAsync(RecordFrame(refs: new[] { new { kind = "version", id = "ver-a1" } },
            forecasts: new object[] { Fc("ver-a1", "promoted", 0.3m), Fc("ver-a1", "refused", "0.6") }), Member, Launch(db, Member));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var id = Body(reply).GetProperty("decision").GetString()!;
        Assert.StartsWith(OrgStore.DecisionPrefix, id, StringComparison.Ordinal);

        var d = gw.Org.Decision(id)!;
        Assert.Equal((Member, OrgDecisionKind.Routine, "test the funding skew on a second venue"), (d.Position, d.Kind, d.Choice));
        Assert.Equal(Options, d.Options);
        Assert.Equal([new OrgRef("version", "ver-a1")], d.Refs);
        var entry = gw.Ledger.Entry(d.LedgerEntryId)!;
        Assert.Equal((LedgerKind.Decision, Member), (entry.Kind, entry.Author));
        var said = gw.Ledger.Latest(entry.Id)!;
        Assert.Equal(("one venue's skew could be its own microstructure", LedgerMark.Claim), (said.Text, said.Mark));
        Assert.Null(said.Confidence);
        var forecasts = gw.Org.ForecastsOn(id);
        Assert.Equal([(0.3m, "promoted"), (0.6m, "refused")], forecasts.Select(f => (f.Probability, f.Target)));
        Assert.All(forecasts, f => Assert.Equal((id, (string?)null, Member, ForecastVocabulary.Version, true), (f.DecisionId!, f.MandateId, f.Forecaster, f.Vocabulary, f.Scored)));
        Assert.StartsWith(OrgStore.ForecastPrefix, forecasts[0].Id, StringComparison.Ordinal);

        // A REFUSED FORECAST REFUSES THE WHOLE DECISION: nothing of it is written.
        var before = (Count(db, "org_decision"), Count(db, "ledger_entry"), Count(db, "org_forecast"));
        Assert.False((await server.CallAsync(RecordFrame(forecasts: new object[] { Fc("ver-a1", "promoted", 0.3m), Fc("ver-a1", "famous", 0.3m) }),
            Member, Launch(db, Member))).Ok);
        Assert.Equal(before, (Count(db, "org_decision"), Count(db, "ledger_entry"), Count(db, "org_forecast")));

        // A CONFINED SEAT records one too.
        Assert.True((await server.CallAsync(RecordFrame(), Head, Launch(db, Head, SeatClasses.Cell))).Ok);
        Assert.Equal(2L, Count(db, "org_decision"));
        Assert.Equal(2L, Count(db, "org_forecast"));

        // KILL THE WRITE AFTER THE ENTRY: the row's insert fails, and the entry written before it in the same write is gone too.
        var entries = Count(db, "ledger_entry");
        Exec(db, "CREATE TRIGGER kill_decision BEFORE INSERT ON org_decision BEGIN SELECT RAISE(ABORT, 'killed after the entry'); END");
        Assert.False((await server.CallAsync(RecordFrame(), Member, Launch(db, Member))).Ok);
        Assert.Equal(entries, Count(db, "ledger_entry"));
        Assert.Equal(2L, Count(db, "org_decision"));
    }

    /// <summary>
    /// (a3) A DECISION CITING A RECORD THAT IS NOT THERE, OR OF A RESERVED KIND, IS REFUSED — in words, nothing written: a ref of a
    /// kind outside the closed list, a ref to no record, a mandatory kind ("written by the verb that performs it"), an unknown kind,
    /// no options, and an argument the verb does not take. RED at base: no op existed.
    /// </summary>
    [Fact]
    public async Task A_decision_citing_a_record_that_is_not_there_or_of_a_reserved_kind_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        foreach (var (frame, words) in new[]
                 {
                     (RecordFrame(refs: new[] { new { kind = "version", id = "ver-nowhere" } }), "is not in the app's ledgers"),
                     (RecordFrame(refs: new[] { new { kind = "ledger", id = "999999" } }), "is not in the app's ledgers"),
                     (RecordFrame(refs: new[] { new { kind = "file", id = "notes.md" } }), "no kind of app record"),
                     (RecordFrame(kind: "charter"), "written by the verb that performs it"),
                     (RecordFrame(kind: "paper-nomination"), "written by the verb that performs it"),
                     (RecordFrame(kind: "whim"), "is no kind of decision"),
                     (Frame(Ops.DecisionRecord, ("options", Array.Empty<string>()), ("choice", "c"), ("rationale", "r")), "options it considered"),
                     (Frame(Ops.DecisionRecord, ("options", Options), ("choice", "c"), ("rationale", "r"), ("id", "dec-chosen")), "'id' is not an argument")
                 })
        {
            var refused = await server.CallAsync(frame, Member, Launch(db, Member));
            Assert.False(refused.Ok, $"a decision was recorded: {words}");
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0L, Count(db, "org_decision"));
        Assert.Equal(0L, Count(db, "ledger_entry"));
    }

    /// <summary>
    /// (b) LEDGER-ADD AND LEDGER-REVISE REFUSE THE DECISION KIND (GUARD; RULING Q8): a <c>decision</c> entry is written only by
    /// <c>decision-record</c>, in its record's own write — else any active position, the legacy pair included, could write one with
    /// no record behind it — and a decision's entry is never revised. Green at base only because <c>decision</c> was no kind;
    /// watched red with <see cref="LedgerKind.Decision"/> accepted by the public <see cref="ResearchLedger.Add"/>.
    /// </summary>
    [Fact]
    public async Task Ledger_add_and_revise_refuse_the_decision_kind()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        foreach (var (role, seat) in new[] { (CouncilRoles.Research, SeatClasses.Cli), (CouncilRoles.Operations, SeatClasses.Cli), (Member, SeatClasses.Harness) })
        {
            var refused = await server.CallAsync(Frame(Ops.LedgerAdd, ("kind", LedgerKind.Decision), ("text", "we decided"), ("mark", "claim")),
                role, Launch(db, role, seat));
            Assert.False(refused.Ok, $"{role} wrote a decision entry with ledger-add");
            Assert.Contains("only 'decision-record' writes one", refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Contains("only 'decision-record' writes one", Assert.Throws<TradeAgentException>(() =>
            gw.Ledger.Add(Member, null, LedgerKind.Decision, "we decided", LedgerMark.Claim, null, null, null)).Message, StringComparison.Ordinal);
        Assert.Equal(0L, Count(db, "ledger_entry"));

        // AND A DECISION'S ENTRY IS NEVER REVISED, by its own author either.
        Assert.True((await server.CallAsync(RecordFrame(), Member, Launch(db, Member))).Ok);
        var entry = gw.Org.DecisionsOf(Member).Single().LedgerEntryId;
        var revise = await server.CallAsync(Frame(Ops.LedgerRevise, ("entry", entry), ("text", "on second thoughts"), ("mark", "claim"),
            ("why", "changed my mind")), Member, Launch(db, Member));
        Assert.False(revise.Ok, "a decision's ledger entry was revised");
        Assert.Contains("never revised", revise.Error!.Message, StringComparison.Ordinal);
        Assert.Single(gw.Ledger.Revisions(entry));
    }

    // ---- item 3: forecasts -----------------------------------------------------------------------------------------------

    /// <summary>A forecast as a frame states it.</summary>
    static object Fc(string subject, string target, object probability, string predicate = ForecastVocabulary.VerdictClass, DateTimeOffset? by = null) =>
        new { predicate, subject, target, by = (by ?? Due).ToString("O"), probability };

    /// <summary>A verdict the referee recorded for <paramref name="version"/>, as only a test may write it (its references off).</summary>
    static void Verdict(Database db, string version, string verdict, DateTimeOffset at)
    {
        db.Exec("PRAGMA foreign_keys=OFF");
        Exec(db, """
            INSERT INTO strategy_promotion(id, version_id, campaign_id, scoring_policy_sha256, interpreter_build, holdout_dataset_id,
                                           holdout_dataset_sha256, execution_model, evaluator_version, holdout_run_id, verdict, reason, at)
            VALUES($id, $v, 1, 'p', 'b', 1, 's', 'm', 'e', 'run-h', $verdict, 'r', $at)
            """, ("$id", $"prom-{Guid.NewGuid():n}"), ("$v", version), ("$verdict", verdict), ("$at", Sql(at)));
        db.Exec("PRAGMA foreign_keys=ON");
    }

    static string Sql(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    static MandateIssue Issue(string holder = Member) =>
        new(Head, null, holder, MandateKind.Exploration, "find out whether the funding skew predicts the next hour", null, null, false,
            Due, 5m, MandateLimits.DefaultRetryLimit);

    static IpcRequest ForecastFrame(string on, string subject, string target, object probability, string predicate = ForecastVocabulary.MandateState,
        DateTimeOffset? by = null) =>
        Frame(Ops.ForecastAdd, ("on", on), ("predicate", predicate), ("subject", subject), ("target", target),
            ("by", (by ?? Due).ToString("O")), ("probability", probability));

    /// <summary>
    /// (c) A FORECAST ON AN OUTCOME ALREADY MEASURED IS REFUSED (precommitment, ORGANISATION § 5): a verdict-class forecast on a
    /// version that already has a verdict, and a mandate-state forecast on a closed mandate or on a state it has already reached —
    /// in words, nothing written. RED at base: no op existed.
    /// </summary>
    [Fact]
    public async Task A_forecast_on_an_outcome_already_measured_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Version(db, "ver-judged");
        Verdict(db, "ver-judged", PromotionVerdict.Refused, DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True((await server.CallAsync(RecordFrame(), Head, Launch(db, Head))).Ok);
        var decision = gw.Org.DecisionsOf(Head).Single().Id;

        var judged = await server.CallAsync(ForecastFrame(decision, "ver-judged", PromotionVerdict.Promoted, 0.2m, ForecastVocabulary.VerdictClass),
            Head, Launch(db, Head));
        Assert.False(judged.Ok, "a forecast on a judged version was recorded");
        Assert.Contains("already has a verdict", judged.Error!.Message, StringComparison.Ordinal);

        var closed = gw.Org.Assign(Issue(), DateTimeOffset.UtcNow);
        gw.Org.Cancel(closed.Id, Head, null, "answered elsewhere", DateTimeOffset.UtcNow);
        var onClosed = await server.CallAsync(ForecastFrame(decision, closed.Id, MandateEventKind.Accepted, 0.5m), Head, Launch(db, Head));
        Assert.Contains("already measured", onClosed.Error!.Message, StringComparison.Ordinal);

        // A STATE ALREADY REACHED: a rejection recorded before the forecast.
        var open = gw.Org.Assign(Issue(), DateTimeOffset.UtcNow);
        var delivered = gw.Org.Deliver(open.Id, Member, null, "found nothing", [], [], DateTimeOffset.UtcNow);
        gw.Org.Reject(delivered.Delivery.Id, Head, null, null, "the report says nothing", DateTimeOffset.UtcNow);
        var reached = await server.CallAsync(ForecastFrame(decision, open.Id, MandateEventKind.Rejected, 0.9m), Head, Launch(db, Head));
        Assert.Contains("has already been rejected", reached.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(0L, Count(db, "org_forecast"));

        // AND THE SAME RULE ON A DECISION'S OWN FORECASTS: the whole record is refused.
        Assert.False((await server.CallAsync(RecordFrame(forecasts: new object[] { Fc("ver-judged", "promoted", 0.5m) }), Head, Launch(db, Head))).Ok);
        Assert.Single(gw.Org.DecisionsOf(Head));
    }

    /// <summary>
    /// (c2) THE ISSUER'S FORECAST ON ITS OWN MANDATE IS RECORDED UNSCORED, WITH THE REASON (ORGANISATION § 5, R17 #5): a
    /// mandate-state forecast by the mandate's decider — on <c>mandate-assign</c>, in the assign's own write, or with
    /// <c>forecast-add</c> — is recorded with <c>scored</c> false and "the forecaster decides this mandate"; one by a head whose
    /// subtree holds the decider is unscored too; one by a position outside it is scored. RED at base: no forecast existed. Watched
    /// red with <c>scored</c> left true for the decider.
    /// </summary>
    [Fact]
    public async Task The_issuers_forecast_on_its_own_mandate_is_recorded_unscored_with_the_reason()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;

        var assign = Frame(Ops.MandateAssign, ("holder", Member), ("goal", "find out whether the skew predicts the hour"), ("kind", "exploration"),
            ("due", Due.ToString("O")), ("allocation", 5m),
            ("forecasts", new object[] { new { predicate = ForecastVocabulary.MandateState, target = "accepted", by = Due.ToString("O"), probability = 0.8m } }));
        var reply = await server.CallAsync(assign, Head, Launch(db, Head));
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var mandate = Body(reply).GetProperty("mandate").GetString()!;
        var own = Assert.Single(gw.Org.ForecastsOn(mandate));
        Assert.Equal((mandate, (string?)null, mandate, Head, false, "the forecaster decides this mandate"),
            (own.Subject, own.DecisionId, own.MandateId!, own.Forecaster, own.Scored, own.UnscoredWhy));

        var added = await server.CallAsync(ForecastFrame(mandate, mandate, MandateEventKind.Expired, 0.1m), Head, Launch(db, Head));
        Assert.True(added.Ok, Json.Write(added.Error));
        Assert.False(Body(added).GetProperty("forecast").GetProperty("scored").GetBoolean());
        Assert.Equal("the forecaster decides this mandate", Body(added).GetProperty("forecast").GetProperty("unscored_why").GetString());

        // THE HEAD ABOVE THE DECIDER controls it through its subtree; a position outside it does not.
        Assert.True((await server.CallAsync(RecordFrame(), Chief, Launch(db, Chief))).Ok);
        var chiefs = await server.CallAsync(ForecastFrame(gw.Org.DecisionsOf(Chief).Single().Id, mandate, "accepted", 0.5m), Chief, Launch(db, Chief));
        Assert.True(chiefs.Ok, Json.Write(chiefs.Error));
        Assert.Equal("this mandate's decider is in the forecaster's subtree",
            Body(chiefs).GetProperty("forecast").GetProperty("unscored_why").GetString());
        Assert.True((await server.CallAsync(RecordFrame(), Outsider, Launch(db, Outsider))).Ok);
        var outside = await server.CallAsync(ForecastFrame(gw.Org.DecisionsOf(Outsider).Single().Id, mandate, "accepted", 0.5m), Outsider, Launch(db, Outsider));
        Assert.True(outside.Ok, Json.Write(outside.Error));
        Assert.True(Body(outside).GetProperty("forecast").GetProperty("scored").GetBoolean());
        Assert.False(Body(outside).GetProperty("forecast").TryGetProperty("unscored_why", out var why) && why.ValueKind != JsonValueKind.Null);

        // ONLY THE DECIDER attaches one to a mandate, and only an author to its own decision.
        Assert.Contains("only its decider", (await server.CallAsync(ForecastFrame(mandate, mandate, "accepted", 0.5m), Member, Launch(db, Member))).Error!.Message,
            StringComparison.Ordinal);
        Assert.Contains("not yours", (await server.CallAsync(ForecastFrame(gw.Org.DecisionsOf(Chief).Single().Id, mandate, "accepted", 0.5m), Outsider,
            Launch(db, Outsider))).Error!.Message, StringComparison.Ordinal);
        Assert.Equal(4L, Count(db, "org_forecast"));
    }

    /// <summary>
    /// (c3) A FORECAST OUTSIDE THE VOCABULARY OR WITH A PAST DATE IS REFUSED (RULING Q3): a predicate v1 does not hold, a target its
    /// predicate does not name, a subject that is not there, a date in the past or beyond the horizon, a probability outside 0..1 —
    /// in words, nothing written. RED at base: no op existed.
    /// </summary>
    [Fact]
    public async Task A_forecast_outside_the_vocabulary_or_with_a_past_date_is_refused()
    {
        var (gw, db, server) = await Pipe();
        await using var _gw = gw;
        using var _db = db;
        await using var _server = server;
        Version(db, "ver-c3");
        Assert.True((await server.CallAsync(RecordFrame(), Head, Launch(db, Head))).Ok);
        var on = gw.Org.DecisionsOf(Head).Single().Id;

        foreach (var (frame, words) in new[]
                 {
                     (ForecastFrame(on, "ver-c3", "promoted", 0.5m, "sharpe-above"), "no predicate of the forecast vocabulary v1"),
                     (ForecastFrame(on, "ver-c3", "accepted", 0.5m, ForecastVocabulary.VerdictClass), "no target of verdict-class"),
                     (ForecastFrame(on, "ver-nowhere", "promoted", 0.5m, ForecastVocabulary.VerdictClass), "no strategy version"),
                     (ForecastFrame(on, "man-nowhere", "accepted", 0.5m), "no mandate"),
                     (ForecastFrame(on, "ver-c3", "promoted", 0.5m, ForecastVocabulary.VerdictClass, DateTimeOffset.UtcNow.AddMinutes(-1)), "not in the future"),
                     (ForecastFrame(on, "ver-c3", "promoted", 0.5m, ForecastVocabulary.VerdictClass, DateTimeOffset.UtcNow.AddDays(400)), "horizon"),
                     (ForecastFrame(on, "ver-c3", "promoted", 1.2m, ForecastVocabulary.VerdictClass), "no probability"),
                     (ForecastFrame(on, "ver-c3", "promoted", "likely", ForecastVocabulary.VerdictClass), "is a number from 0 to 1"),
                     (ForecastFrame("ver-c3", "ver-c3", "promoted", 0.5m, ForecastVocabulary.VerdictClass), "neither a decision")
                 })
        {
            var refused = await server.CallAsync(frame, Head, Launch(db, Head));
            Assert.False(refused.Ok, $"a forecast was recorded: {words}");
            Assert.Contains(words, refused.Error!.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0L, Count(db, "org_forecast"));
    }

    // ---- item 4: the resolver ---------------------------------------------------------------------------------------------

    /// <summary>A decision of <see cref="Head"/>'s carrying <paramref name="forecasts"/>, by the store — the pipe is tested above.</summary>
    static DecisionRow Decided(Database db, DateTimeOffset at, params ForecastClaim[] forecasts) =>
        new OrgStore(db).Decide(Head, null, OrgDecisionKind.Routine, ["a", "b"], "a", "because", [], forecasts, at);

    /// <summary>
    /// (d) A VERDICT RESOLVES A VERDICT-CLASS FORECAST YES OR NO, AND NONE BY THE DATE RESOLVES VOID (RULINGS Q3, Q4): the first
    /// verdict by the date settles it — of the class, yes; of another, no — naming the promotion it rests on; a version with no
    /// verdict stays open until its date and then resolves void, naming the version; a verdict after the date resolves nothing.
    /// RED at base: no resolver existed.
    /// </summary>
    [Fact]
    public void A_verdict_resolves_a_verdict_class_forecast_yes_or_no_and_none_by_the_date_resolves_void()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        foreach (var v in new[] { "ver-yes", "ver-no", "ver-void", "ver-late" }) Version(db, v);
        var t0 = DateTimeOffset.UtcNow;
        var by = t0.AddDays(2);
        var d = Decided(db, t0, new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-yes", PromotionVerdict.Promoted, by, 0.7m),
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-no", PromotionVerdict.Promoted, by, 0.7m),
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-void", PromotionVerdict.PaperEligible, by, 0.4m),
            new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-late", PromotionVerdict.Refused, by, 0.9m));
        var org = new OrgStore(db);
        var f = org.ForecastsOn(d.Id).ToDictionary(x => x.Subject);

        Verdict(db, "ver-yes", PromotionVerdict.Promoted, t0.AddHours(1));
        Verdict(db, "ver-no", PromotionVerdict.Refused, t0.AddHours(1));
        Assert.Equal(2, new ForecastResolver(db).Sweep(t0.AddHours(2)).Resolved);
        Assert.Equal((ForecastOutcome.Yes, OrgRefKind.Promotion), (org.Resolution(f["ver-yes"].Id)!.Outcome, org.Resolution(f["ver-yes"].Id)!.RecordKind));
        Assert.Equal(ForecastOutcome.No, org.Resolution(f["ver-no"].Id)!.Outcome);
        Assert.Contains("'refused'", org.Resolution(f["ver-no"].Id)!.Why, StringComparison.Ordinal);
        Assert.Null(org.Resolution(f["ver-void"].Id));

        // THE DATE PASSES with no verdict for two of them; one arrives after it, and is read as no verdict by the date.
        Verdict(db, "ver-late", PromotionVerdict.Refused, by.AddHours(1));
        Assert.Equal(2, new ForecastResolver(db).Sweep(by.AddHours(2)).Resolved);
        foreach (var v in new[] { "ver-void", "ver-late" })
        {
            var r = org.Resolution(f[v].Id)!;
            Assert.Equal((ForecastOutcome.Void, OrgRefKind.Version, v), (r.Outcome, r.RecordKind, r.RecordId));
            Assert.Contains("no verdict was recorded", r.Why, StringComparison.Ordinal);
        }
        // AND IT WOKE NOBODY.
        Assert.Equal(0L, Count(db, "mission_event"));
    }

    /// <summary>
    /// (d2) A MANDATE EVENT RESOLVES A MANDATE-STATE FORECAST: the target state by the date is yes, naming the event; another
    /// closing state first is no; the date passing with neither is no. RED at base: no resolver existed.
    /// </summary>
    [Fact]
    public void A_mandate_event_resolves_a_mandate_state_forecast()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        var org = new OrgStore(db);
        var t0 = DateTimeOffset.UtcNow;
        var accepted = org.Assign(Issue(), t0);
        var cancelled = org.Assign(Issue(), t0);
        var idle = org.Assign(Issue(), t0);
        var by = t0.AddDays(2);
        var d = Decided(db, t0, new ForecastClaim(ForecastVocabulary.MandateState, accepted.Id, MandateEventKind.Accepted, by, 0.6m),
            new ForecastClaim(ForecastVocabulary.MandateState, cancelled.Id, MandateEventKind.Accepted, by, 0.6m),
            new ForecastClaim(ForecastVocabulary.MandateState, idle.Id, MandateEventKind.Rejected, by, 0.2m));
        var f = org.ForecastsOn(d.Id).ToDictionary(x => x.Subject);

        var delivery = org.Deliver(accepted.Id, Member, null, "the skew leads by four minutes", [], [], t0.AddHours(1));
        org.Accept(delivery.Delivery.Id, Head, null, t0.AddHours(2));
        org.Cancel(cancelled.Id, Head, null, "answered elsewhere", t0.AddHours(2));
        Assert.Equal(2, new ForecastResolver(db).Sweep(t0.AddHours(3)).Resolved);

        var yes = org.Resolution(f[accepted.Id].Id)!;
        Assert.Equal((ForecastOutcome.Yes, "mandate-event"), (yes.Outcome, yes.RecordKind));
        Assert.Equal(org.MandateEvents(accepted.Id).Single(e => e.Kind == MandateEventKind.Accepted).Id.ToString(CultureInfo.InvariantCulture), yes.RecordId);
        Assert.Equal(ForecastOutcome.No, org.Resolution(f[cancelled.Id].Id)!.Outcome);
        Assert.Contains("was cancelled", org.Resolution(f[cancelled.Id].Id)!.Why, StringComparison.Ordinal);
        Assert.Null(org.Resolution(f[idle.Id].Id));

        new ForecastResolver(db).Sweep(by.AddMinutes(1));
        var no = org.Resolution(f[idle.Id].Id)!;
        Assert.Equal((ForecastOutcome.No, OrgRefKind.Mandate, idle.Id), (no.Outcome, no.RecordKind, no.RecordId));
    }

    /// <summary>
    /// (d3) THE RESOLVER READS ONLY THE VERDICT CLASS AND MANDATE EVENTS (GUARD; EVID-001, § 2.11): its two measurement reads name
    /// <c>strategy_promotion</c> and <c>org_mandate_event</c> alone, and its source names no run, holdout, stream or ledger table.
    /// Watched red with the verdict read joining <c>strategy_run</c> for a figure.
    /// </summary>
    [Fact]
    public void The_resolver_reads_only_the_verdict_class_and_mandate_events()
    {
        var tables = new System.Text.RegularExpressions.Regex(@"\b(FROM|JOIN)\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Equal(["org_mandate_event", "strategy_promotion"],
            ForecastResolver.MeasurementSql.SelectMany(q => tables.Matches(q).Select(m => m.Groups[2].Value)).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["id", "verdict", "at"], Columns(ForecastResolver.VerdictSql));
        Assert.Equal(["id", "kind", "at"], Columns(ForecastResolver.MandateEventSql));

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradeAgent.Core", "Db", "ForecastResolver.cs"));
        foreach (var banned in new[] { "strategy_run", "referee_holdout", "referee_research", "strategy_stream", "ledger_", "strategy_trade", "forward_" })
            Assert.DoesNotContain(banned, source, StringComparison.OrdinalIgnoreCase);

        static string[] Columns(string sql) =>
            [.. sql["SELECT ".Length..sql.IndexOf(" FROM ", StringComparison.Ordinal)].Split(',').Select(c => c.Trim())];
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    /// <summary>
    /// (d4) RESOLVING TWICE WRITES ONE RESOLUTION (restart-safe): a second pass, and a restart's pass, find the forecast resolved and
    /// write nothing; the first row stands as written, never replaced; a forecast in a vocabulary this build does not know is refused
    /// in words and left unresolved. RED at base: no resolver existed. Watched red with the resolution written ON CONFLICT REPLACE.
    /// </summary>
    [Fact]
    public void Resolving_twice_writes_one_resolution()
    {
        using var db = TestEnv.NewDb();
        Chart(db);
        Version(db, "ver-twice");
        var t0 = DateTimeOffset.UtcNow;
        var d = Decided(db, t0, new ForecastClaim(ForecastVocabulary.VerdictClass, "ver-twice", PromotionVerdict.Promoted, t0.AddDays(1), 0.5m));
        var org = new OrgStore(db);
        var f = Assert.Single(org.ForecastsOn(d.Id));
        Verdict(db, "ver-twice", PromotionVerdict.Promoted, t0.AddHours(1));

        Assert.Equal(1, new ForecastResolver(db).Sweep(t0.AddHours(2)).Resolved);
        var first = org.Resolution(f.Id)!;
        Assert.Equal(0, new ForecastResolver(db).Sweep(t0.AddHours(3)).Resolved);

        // A RESTART: the same home, opened again, and its first pass writes nothing.
        var file = db.Read(c => c.DataSource);
        using (var reopened = new Database(file))
            Assert.Equal(0, new ForecastResolver(reopened).Sweep(t0.AddDays(3)).Resolved);

        // TWO PASSES THAT BOTH READ IT OPEN — the store's writer asked again for the same forecast — write ONE row, never replace it.
        Assert.False(org.RecordResolution(f.Id, ForecastOutcome.No, OrgRefKind.Version, "ver-twice", "a second pass", t0.AddDays(4)));
        Assert.Equal(1L, Count(db, "org_forecast_resolution"));
        Assert.Equal(first, org.Resolution(f.Id));

        // A VOCABULARY THIS BUILD DOES NOT KNOW: refused in words, left unresolved, never guessed.
        Exec(db, """
            INSERT INTO org_forecast(id, decision_id, forecaster, vocabulary, predicate, subject, target, resolve_by, probability, scored, at)
            VALUES('fc-0000000000v2', $d, $p, 2, 'verdict-class', 'ver-twice', 'promoted', $by, '0.5', 1, $at)
            """, ("$d", d.Id), ("$p", Head), ("$by", Sql(t0.AddDays(1))), ("$at", Sql(t0)));
        var sweep = new ForecastResolver(db).Sweep(t0.AddDays(5));
        Assert.Contains(sweep.Refused, w => w.Contains("fc-0000000000v2", StringComparison.Ordinal) && w.Contains("v1 only", StringComparison.Ordinal));
        Assert.Null(org.Resolution("fc-0000000000v2"));
    }

}
