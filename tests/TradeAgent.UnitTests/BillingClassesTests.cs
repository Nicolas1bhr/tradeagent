using System.Globalization;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// <c>U-billing-classes</c> — EVERY LAUNCH IS COUNTED AGAINST THE CEILING OF ITS OWN BILLING CLASS (ORCHESTRATION § 2.4, § 8 BUDGET-001;
/// ORGANISATION § 10; COUNCIL rule 4): the owner's plan capacity — a list-price equivalent, not money — or the real-money cap, decided
/// from the seat the app verified and recorded with the row; an unknown class counts against both; the envelopes nest in each.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BillingClassesTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"billing-{Guid.NewGuid():n}.jsonl");
    readonly DateTimeOffset _at = TestEnv.LocalNoon();

    /// <summary>The owner's own two numbers per million tokens, so every reservation has one right answer.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    /// <summary>One turn's reservation at <see cref="Rate"/> over the shipped allowance: 1.2 M in at 1, 20 k out at 4.</summary>
    static readonly decimal Turn = TurnAllowance.Default.InputTokens * 1m / 1_000_000m + TurnAllowance.Default.OutputTokens * 4m / 1_000_000m;

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    TurnMeter Meter(decimal money, decimal plan, Database? db = null) =>
        new(db ?? _db, () => money, now: () => _at, recordPath: _records, owner: () => Rate, share: _ => 1m,
            seat: role => SeatResolver.Of(role, "codex", "gpt-5.6-sol", TurnAllowance.Default), live: new LiveAttempts());

    List<AiAttempt> Rows() => new AiAttemptStore(_db).Between(_at.AddDays(-1), _at.AddDays(1));

    static void Exec(Database db, string sql, params (string, object?)[] args) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, args);
        c.ExecuteNonQuery();
        return 0;
    });

    // ---- (d) the sign-in names the class -------------------------------------------------------------------------------

    /// <summary>
    /// (d) THE SIGN-IN NAMES THE CLASS, AND AN UNRECOGNISED ONE IS UNKNOWN: the shipped codex seat's two patterns read the two measured
    /// texts as <c>plan</c> and <c>key</c>; anything else — a reworded status, both at once, nothing — is <c>unknown</c>. The CLI's own
    /// probe reads it off <c>login status</c> under the seat home; the harness is <c>key</c>; a seat with no seat profile <c>unknown</c>.
    /// </summary>
    [Fact]
    public async Task The_sign_in_names_the_class_and_an_unrecognised_one_is_unknown()
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        Assert.Equal(BillingClasses.Plan, codex.Seat!.BillingClassOf("Logged in using ChatGPT"));
        Assert.Equal(BillingClasses.Key, codex.Seat.BillingClassOf("Logged in using an API key - sk-FAKE-***-0000"));
        foreach (var other in new[] { "Logged in using a ChatGPT workspace token", "", "Signed in.",
                     "Logged in using ChatGPT\nLogged in using an API key - sk-FAKE" })
            Assert.Equal(BillingClasses.Unknown, codex.Seat.BillingClassOf(other));

        // THE PROBE ITSELF, over a script that answers `login status` as each text: the class comes off the status the CLI printed.
        foreach (var (said, expected) in new[]
                 {
                     ("Logged in using ChatGPT", BillingClasses.Plan),
                     ("Logged in using an API key - sk-FAKE-***-0000", BillingClasses.Key),
                     ("Logged in some new way", BillingClasses.Unknown)
                 })
        {
            var (manifest, exe, cwd) = SignInScript(said);
            var reading = await CliSeatProbe.Instance.ReadAsync(manifest, exe, cwd, CancellationToken.None);
            Assert.True(reading.SignedIn);
            Assert.Equal(expected, reading.BillingClass);
        }

        // THE VERDICT: no seat profile is unknown; the harness is an API key.
        Assert.Equal(BillingClasses.Unknown, SeatVerdict.None.BillingClass);
        Assert.Equal(BillingClasses.Key, ((ISeatLaunch)HarnessConversation()).CheckSeat().BillingClass);
    }

    /// <summary>The shipped codex manifest pointed at a script that prints <paramref name="said"/> for <c>login status</c>.</summary>
    static (RuntimeManifest Manifest, string Exe, string Cwd) SignInScript(string said)
    {
        var codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
        var dir = Path.Combine(TestEnv.Home, "billing-seat", Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(dir);
        string exe;
        if (OperatingSystem.IsWindows())
        {
            exe = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(exe, "@echo off\r\nif \"%1\"==\"login\" echo " + said + " 1>&2\r\nexit /b 0\r\n");
        }
        else
        {
            exe = Path.Combine(dir, "codex");
            File.WriteAllText(exe, "#!/bin/sh\n[ \"$1\" = \"login\" ] && echo '" + said + "' >&2\nexit 0\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        codex.Executable = exe;
        codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(exe);
        codex.Seat!.Home = Path.Combine(dir, "seat-home");
        var cwd = Path.Combine(dir, "work");
        Directory.CreateDirectory(cwd);
        return (codex, exe, cwd);
    }

    static IAgentConversation HarnessConversation()
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        var home = Path.Combine(TestEnv.Home, $"billing-harness-{Guid.NewGuid():n}");
        return new ApiAgentRuntime(manifest, new TradeAgent.Security.HarnessKey()).OpenConversation(CouncilRoles.Research, () => home,
            () => new Dictionary<string, string>(), () => null);
    }

    // ---- (h) the class is recorded with the row ------------------------------------------------------------------------

    /// <summary>
    /// (h) THE CLASS IS RECORDED WITH THE ROW, FROM THE VERDICT ADMITTED: a seat read as the plan writes <c>plan</c>, one read as a key
    /// <c>key</c>, a seat with no seat profile <c>unknown</c> — and nothing changes it afterwards: an UPDATE is refused by trigger, a
    /// word outside the three by CHECK, and a row inserted without one is <c>unknown</c>.
    /// </summary>
    [Fact]
    public void The_class_is_recorded_with_the_row_from_the_verdict_admitted()
    {
        var meter = Meter(money: 100m, plan: 100m);
        using (meter.Attach(new Billed(BillingClasses.Plan), CouncilRoles.Operations))
        using (meter.Attach(new Billed(BillingClasses.Key), CouncilRoles.Research))
        {
            Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
            Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);
        }
        var rows = Rows().ToDictionary(a => a.Role!);
        Assert.Equal(BillingClasses.Plan, rows[CouncilRoles.Operations].BillingClass);
        Assert.Equal(BillingClasses.Key, rows[CouncilRoles.Research].BillingClass);

        // A CONVERSATION THAT DECLARED NO SEAT: nobody established who pays, so unknown.
        var bare = Meter(money: 100m, plan: 100m, db: _db);
        Assert.True(bare.Begin("## Situation").Admitted);
        Assert.Equal(BillingClasses.Unknown, Rows().Single(a => a.Role is null).BillingClass);

        var id = rows[CouncilRoles.Operations].Id;
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db, "UPDATE ai_attempt SET billing_class='key' WHERE id=$id", ("$id", id)));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(_db,
            "INSERT INTO ai_attempt(id, started_at, reserved_cost, state, billing_class) VALUES('a-word', $at, 0, 'LAUNCHED', 'credits')",
            ("$at", Sql.T(_at))));
        Exec(_db, "INSERT INTO ai_attempt(id, started_at, reserved_cost, state) VALUES('a-bare', $at, 0, 'LAUNCHED')", ("$at", Sql.T(_at)));
        Assert.Equal(BillingClasses.Unknown, new AiAttemptStore(_db).Get("a-bare")!.BillingClass);
        Exec(_db, "UPDATE ai_attempt SET exit_code=0 WHERE id=$id", ("$id", id));
        Assert.Equal(BillingClasses.Plan, new AiAttemptStore(_db).Get(id)!.BillingClass);
    }

    /// <summary>The rung's number, spelled once here as in the schema.</summary>
    const int Rung = 46;

    /// <summary>
    /// THE RUNG REOPENS AFTER A CRASH BETWEEN ITS STATEMENTS AND ITS STAMP: with the trigger not yet made, and with everything made but the
    /// stamp, the next open runs the rung again to the same schema — the trigger present, a row's class as it was, an update refused.
    /// A row from before the rung reads <c>unknown</c>.
    /// </summary>
    [Fact]
    public void The_billing_rung_arrives_at_its_number_and_a_crash_before_its_stamp_runs_it_again()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= Rung, $"the column arrives at rung {Rung}; this build says {Versions.DatabaseSchemaVersion}");
        var file = Path.Combine(TestEnv.Home, $"billing-rung-{Guid.NewGuid():n}.db");
        using (var db = new Database(file))
            new AiAttemptStore(db).Begin(new AiAttempt { Id = "a-rung", StartedAt = _at, ReservedCost = 1m, BillingClass = BillingClasses.Plan });

        foreach (var dropped in new[] { true, false })
        {
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                raw.Open();
                using var c = raw.CreateCommand();
                c.CommandText = (dropped ? "DROP TRIGGER ai_attempt_billing_fixed; " : "")
                                + $"UPDATE meta SET value='{Rung - 1}' WHERE key='schema_version';";
                c.ExecuteNonQuery();
            }
            using var rerun = new Database(file);
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
                rerun.Read(_ => { using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
            Assert.Equal(1L, rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='ai_attempt_billing_fixed'");
                return (long)c.ExecuteScalar()!;
            }));
            Assert.Equal(BillingClasses.Plan, new AiAttemptStore(rerun).Get("a-rung")!.BillingClass);
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Exec(rerun, "UPDATE ai_attempt SET billing_class='key' WHERE id='a-rung'"));
        }
    }

    /// <summary>A conversation whose seat the app read as billed to <paramref name="billing"/>; it never runs a turn here.</summary>
    internal sealed class Billed(string billing, string seatClass = SeatClasses.Cli) : IAgentConversation, ISeatLaunch
    {
        public SeatVerdict CheckSeat() => SeatVerdict.None with { SeatClass = seatClass, BillingClass = billing };

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => [];
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<string>? Delta { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<AgentTurnEnded>? TurnEnded { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public void Queue(string message) { }
        public IReadOnlyList<string> TakeTyped() => [];
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}
