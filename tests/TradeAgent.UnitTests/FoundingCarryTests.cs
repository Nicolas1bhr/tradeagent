using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HOME FOUNDED BEFORE PER-PAIR ENVELOPES (<c>U-founding-carry</c>; CLAUDE.md "No backward compatibility while we build": refuse,
/// never guess; never destroy without the owner's choice). A home whose board's founding policy still states the older single
/// <c>paper_envelope</c> — as every home founded before <c>U-evidence-breadth-paper</c> does — is refused by the open in words naming
/// both shapes, left exactly as it was found; one on which the board also decided is refused saying so. The owner's two-press Start
/// fresh moves it aside whole, deletes nothing, and CARRIES the market-context tape into the new home when its own ladder version is
/// this build's, byte for byte, so its rows stay true about what was observed. NOTHING HERE REACHES A MODEL, A VENUE OR A BROKER.
/// </summary>
public class FoundingCarryTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The founding body an earlier build wrote (main before 744d8150): today's founding policy with the per-pair map replaced by the
    /// ONE envelope of its time — BTCUSDT, 0.05, 5,000 USDT, one deployment — and its two open positions; canonical, so it verifies.
    /// </summary>
    static string OldFoundingBody(decimal planDayTotal = 40m)
    {
        var body = JsonNode.Parse(BoardPolicyBody.Canonical(BoardFounding.V1))!.AsObject();
        body.Remove("paper_envelopes");
        body["paper_envelope"] = new JsonObject
        {
            ["connector"] = "paper", ["account"] = "PAPER-1", ["instrument"] = "BTCUSDT", ["max_quantity"] = 0.05m,
            ["max_notional"] = 5000, ["deployments"] = 1, ["days"] = null
        };
        body["risk"]!["max_open_positions"] = 2;
        body["plan_day_total"] = planDayTotal;
        return TapeJson.Canonical(body.ToJsonString());
    }

    /// <summary>
    /// THE FOUNDING BODY THE SERVER'S KIT REALLY WROTE: read out of a home founded by that kit's own code (commit 9836e38b, schema 52), not
    /// built from today's. The server's home holds exactly this row, so the refusal is proved on it and not only on a likeness of it.
    /// </summary>
    const string ServerKitBody = """
        {"head_ceiling_hours":24,"lanes":1,"lanes_per_cell_class":1,"lanes_per_pool":1,"live_activated":false,"mode":"PAPER","paper_envelope":{"account":"PAPER-1","connector":"paper","days":null,"deployments":1,"instrument":"BTCUSDT","max_notional":5000,"max_quantity":0.05},"plan_day_total":40,"review_minutes":30,"rewake_floor_minutes":{"department":240,"executor":15,"team":60},"risk":{"instrument_allowlist":["BTCUSDT"],"loss_min_closure_hours":24,"loss_strike_window_days":7,"max_daily_loss":0,"max_loss_per_trade":0,"max_notional_per_order":0,"max_open_positions":2,"max_order_quantity":1,"max_orders_per_minute":6,"valuation_loss_exit_minutes":15},"role_shares":{},"turn_allowance_input":1200000,"turn_allowance_output":20000}
        """;

    const string ServerKitBodySha = "ab6ca90507fccd95f3dd320306f3f33f2fed376fc19da57b4c6783ffca0c6c09";

    static void Raw(string file, string sql, params (string, object?)[] ps)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A tape of this build's layout with three fetches and their observations, its handle closed — or still open, for the -wal.</summary>
    static TapeStore Tape(string file)
    {
        var tape = new TapeStore(file);
        for (var i = 0; i < 3; i++)
        {
            var at = Noon.AddMinutes(i);
            tape.Append(new TapeFetch
            {
                Source = TapeSourceCatalog.OkxEeaAnnouncements, Series = "announcements", Url = "http://127.0.0.1:9/okx",
                RequestedAt = at.AddMilliseconds(-200), ReceivedAt = at, HttpStatus = 200
            }, [new TapeItem("BTCUSDT", at.AddSeconds(-30), $$"""{"id":"a-{{i}}","title":"listing {{i}}"}""")]);
        }
        return tape;
    }

    /// <summary>
    /// A HOME AS THE OWNER'S SERVER HOLDS ONE (founded 2026-10-30): a database this build's ladder made, its founding policy rewritten
    /// raw to the older shape (its hash its own, so it verifies), a workspace file of his, and the tape. With <paramref name="acts"/>,
    /// a board act and the version it wrote, on the older shape too.
    /// </summary>
    static string OldShapeHome(string root, bool acts = false, bool tape = true)
    {
        var home = Path.Combine(root, "TradeAgent");
        var file = Path.Combine(home, "state", "tradeagent.db");
        using (new Database(file)) { }
        Directory.CreateDirectory(Path.Combine(home, "workspace"));
        File.WriteAllText(Path.Combine(home, "workspace", "notes.txt"), "the owner's own file");

        var body = OldFoundingBody();
        Raw(file, "DROP TRIGGER IF EXISTS board_policy_sealed_u; UPDATE board_policy SET body=$b, body_sha256=$s WHERE version=1;",
            ("$b", body), ("$s", Sha256Hex.Of(body)));
        if (acts)
        {
            var later = OldFoundingBody(30m);
            Raw(file, "INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                      + "VALUES('bact-old', 'narrow', 'board-1', NULL, '[]', $sha, 1, 'a claim', '[]', NULL, NULL, $at)",
                ("$sha", Sha256Hex.Of("[]")), ("$at", Noon.UtcDateTime.ToString("O")));
            Raw(file, "INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                      + "VALUES(2, 'board', 'act', 'bact-old', $at, 'PAPER', 0, $b, $s, 1)",
                ("$at", Noon.UtcDateTime.ToString("O")), ("$b", later), ("$s", Sha256Hex.Of(later)));
        }
        if (tape) Tape(Path.Combine(home, "state", "tape.db")).Dispose();
        SqliteConnection.ClearAllPools();
        return home;
    }

    /// <summary>Every file and its SHA-256, SQLite's own -wal/-shm sidecars aside (OlderHomeTests' fingerprint).</summary>
    static Dictionary<string, string> Fingerprint(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Hash(f));

    static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>EVERY ROW OF EVERY TABLE OF A TAPE, every column as SQLite stores it, in key order: what "its rows stay true" is held to.</summary>
    static string Rows(string tapeFile)
    {
        // IMMUTABLE WHEN CLOSED (HomeMark.Read's way): a read-only open of a WAL file with no -wal would leave one beside it.
        var closed = !File.Exists(tapeFile + "-wal");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = closed ? $"file:{Path.GetFullPath(tapeFile).Replace('\\', '/')}?immutable=1" : tapeFile,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        c.Open();
        var tables = new List<string>();
        using (var t = c.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = t.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        var dump = new StringBuilder();
        foreach (var table in tables)
        {
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1, 2";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                dump.Append(table);
                for (var i = 0; i < r.FieldCount; i++) dump.Append('|').Append(r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                dump.Append('\n');
            }
        }
        SqliteConnection.ClearAllPools();
        return dump.ToString();
    }

    /// <summary>
    /// THE OLDER SHAPE IS REFUSED IN WORDS NAMING BOTH, UNTOUCHED: the open throws the older-home refusal naming
    /// <c>paper_envelope</c> and <c>paper_envelopes</c>, that nothing was changed, and the Start fresh that carries the tape; the
    /// app's start reads the same refusal read-only before its lock; every file is as it was. RED before <c>U-founding-carry</c> (the
    /// Database open check removed): the home opened, its rungs and journal written, its policy read as unreadable — OBSERVE and
    /// zero limits — with no words and no way on.
    /// </summary>
    [Fact]
    public void A_home_founded_in_the_older_policy_shape_is_refused_untouched_naming_both_shapes()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var before = Fingerprint(home);

        var refused = Assert.Throws<OlderHomeException>(() => new Database(Path.Combine(home, "state", "tradeagent.db")));
        log.WriteLine(refused.Message);

        Assert.NotNull(refused.Policy);
        Assert.Equal(["paper_envelope"], refused.Policy!.Theirs);
        Assert.Equal(["paper_envelopes"], refused.Policy.Ours);
        Assert.False(refused.Policy.HasDecisions);
        Assert.Equal(Versions.DatabaseSchemaVersion, refused.HomeSchema);
        Assert.Equal(Path.GetFullPath(home), refused.Home);
        Assert.Contains("'paper_envelope'", refused.Message);
        Assert.Contains("'paper_envelopes'", refused.Message);
        Assert.Contains("None of your records or files in that folder has been changed", refused.Message);
        Assert.Contains("start fresh", refused.Message);
        Assert.Contains("tape", refused.Message);
        Assert.DoesNotContain("decided on that older policy", refused.Message);
        Assert.Equal(before, Fingerprint(home));

        var probed = TradeAgent.App.AppHost.ProbeOlderHome(home);
        Assert.NotNull(probed?.Policy);
        Assert.Equal(refused.Message, probed!.Message);
        Assert.Equal(before, Fingerprint(home));
    }

    /// <summary>
    /// A HOME WHOSE BOARD DECIDED ON THE OLDER SHAPE is refused in words too, naming the decisions and that they are not carried —
    /// never read on a guess. RED with the decisions' words off (<see cref="OlderPolicyShape.HasDecisions"/> false): no words about them.
    /// </summary>
    [Fact]
    public void A_home_whose_board_decided_on_the_older_shape_is_refused_saying_so()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir, acts: true);
        var before = Fingerprint(home);

        var refused = Assert.Throws<OlderHomeException>(() => new Database(Path.Combine(home, "state", "tradeagent.db")));
        log.WriteLine(refused.Message);

        Assert.Equal((1, 1, true), (refused.Policy!.LaterVersions, refused.Policy.Acts, refused.Policy.HasDecisions));
        Assert.Contains("The board also decided on that older policy (1 later policy version, 1 board act)", refused.Message);
        Assert.Contains("they stay, whole, in the old folder", refused.Message);
        Assert.Equal(before, Fingerprint(home));
    }

    /// <summary>
    /// START FRESH OVER AN OLDER-SHAPE HOME KEEPS IT WHOLE AND CARRIES THE TAPE BYTE FOR BYTE: the old home moves aside by one rename
    /// with every file as it was, the new home holds the tape — the same bytes, the same rows — and nothing else of the old one, and
    /// the new database opens fresh beside it with this build's founding policy. RED with the carry off: the new home has no tape.
    /// </summary>
    [Fact]
    public void Start_fresh_over_an_older_shape_home_keeps_it_whole_and_carries_the_tape_byte_for_byte()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var before = Fingerprint(home);
        var oldTape = Path.Combine(home, "state", "tape.db");
        var tapeBytes = Hash(oldTape);
        var tapeRows = Rows(oldTape);
        Assert.Contains("listing 2", tapeRows);

        var aside = OlderHome.SetAside(home, Noon, out var words);
        log.WriteLine(words);

        Assert.Equal(Path.GetFullPath(home) + "-from-older-policy-20261030-120000", aside);
        Assert.Equal(before, Fingerprint(aside));
        Assert.Contains("copied into the new folder byte for byte", words);
        var carried = Path.Combine(home, "state", "tape.db");
        Assert.Equal(["state/tape.db"], Directory.GetFiles(home, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(home, f).Replace('\\', '/')));
        Assert.Equal(tapeBytes, Hash(carried));
        Assert.Equal(tapeRows, Rows(carried));

        using (var db = new Database(Path.Combine(home, "state", "tradeagent.db")))
        {
            Assert.True(db.CreatedHere);
            Assert.Equal(BoardFounding.V1, new BoardReader(db).InForce().Policy);
        }
        using (var tape = new TapeStore(carried)) Assert.Equal(TapeStore.Schema, tape.Version);
        Assert.Equal(tapeRows, Rows(carried));
    }

    /// <summary>
    /// A TAPE LEFT WITH ROWS IN ITS -WAL (the app stopped hard) IS CARRIED WITH THEM: the copy takes the -wal too, so every row the
    /// tape committed is in the new home — the bytes of both files and every row the same. RED with the -wal left behind: the rows
    /// still in it are missing from the carried tape.
    /// </summary>
    [Fact]
    public void A_tape_with_rows_still_in_its_wal_is_carried_with_every_row()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir, tape: false);
        var staged = Path.Combine(scratch.Dir, "live", "tape.db");
        string rows;
        using (Tape(staged))
        {
            Assert.True(File.Exists(staged + "-wal") && new FileInfo(staged + "-wal").Length > 0);
            File.Copy(staged, Path.Combine(home, "state", "tape.db"));
            File.Copy(staged + "-wal", Path.Combine(home, "state", "tape.db-wal"));
            rows = Rows(staged);
        }
        Assert.Contains("listing 2", rows);
        var walBytes = Hash(Path.Combine(home, "state", "tape.db-wal"));
        var dbBytes = Hash(Path.Combine(home, "state", "tape.db"));
        // THE FIXTURE IS WHAT IT SAYS: the main file alone, without its -wal, does not hold the rows.
        var alone = Path.Combine(scratch.Dir, "alone", "tape.db");
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        File.Copy(Path.Combine(home, "state", "tape.db"), alone);
        Assert.DoesNotContain("listing 2", Rows(alone));

        OlderHome.SetAside(home, Noon, out var words);
        log.WriteLine(words);

        var carried = Path.Combine(home, "state", "tape.db");
        Assert.Equal((dbBytes, walBytes), (Hash(carried), Hash(carried + "-wal")));
        Assert.Equal(rows, Rows(carried));
        using (var tape = new TapeStore(carried)) Assert.Equal(TapeStore.Schema, tape.Version);
        Assert.Equal(rows, Rows(carried));
    }

    /// <summary>
    /// ONLY A TAPE AT THIS BUILD'S OWN LADDER VERSION IS CARRIED: one at another layout version is left, whole, in the old home, the
    /// words say which version and where it is kept, and the new home holds no tape and no half-made copy. RED with the version check
    /// off: the older-layout tape was carried.
    /// </summary>
    [Fact]
    public void A_tape_at_another_layout_version_is_not_carried_and_stays_whole_in_the_old_home()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        Raw(Path.Combine(home, "state", "tape.db"), "UPDATE tape_meta SET value='2' WHERE key='schema'");
        SqliteConnection.ClearAllPools();
        var before = Fingerprint(home);

        var aside = OlderHome.SetAside(home, Noon, out var words);
        log.WriteLine(words);

        Assert.Contains("was not carried", words);
        Assert.Contains("its layout is version 2 and this version reads 3", words);
        Assert.Contains(aside, words);
        Assert.Equal(before, Fingerprint(aside));
        Assert.Equal([], Directory.GetFiles(home, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(home, f)));
    }

    /// <summary>
    /// A PROVING GROUND'S TAPE IS NEVER CARRIED INTO A REAL HOME: one marked <c>proving</c> is left, whole, in the old home, and the new
    /// home holds no tape. RED with the mark check off: the synthetic tape was carried into the owner's new home.
    /// </summary>
    [Fact]
    public void A_proving_grounds_tape_is_never_carried_into_a_real_home()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        Raw(Path.Combine(home, "state", "tape.db"), "UPDATE tape_meta SET value='proving' WHERE key='home_kind'");
        SqliteConnection.ClearAllPools();
        var before = Fingerprint(home);

        var aside = OlderHome.SetAside(home, Noon, out var words);
        log.WriteLine(words);

        Assert.Contains("it is marked 'proving', not a real home's", words);
        Assert.Equal(before, Fingerprint(aside));
        Assert.Equal([], Directory.GetFiles(home, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(home, f)));
    }

    /// <summary>
    /// THE BODY THE SERVER'S KIT WROTE VERIFIES AND READS AS THE OLDER SHAPE: its own hash, its own canonical spelling, one field apart from
    /// this build's. If this stops holding, the server's home would not get the refusal and its way on.
    /// </summary>
    [Fact]
    public void The_body_the_servers_kit_really_wrote_is_read_as_the_older_shape()
    {
        var shape = BoardPolicyBody.OlderShape(ServerKitBody, ServerKitBodySha);
        Assert.NotNull(shape);
        Assert.Equal("paper_envelope", Assert.Single(shape.Value.Theirs));
        Assert.Equal("paper_envelopes", Assert.Single(shape.Value.Ours));
    }

    /// <summary>
    /// A HOME ANOTHER TRADEAGENT STILL HOLDS IS NOT MOVED, AND NOTHING OF IT IS COPIED: the refusal is read before the instance lock, so an
    /// older app still running on the folder can reach this press. Linux would rename the folder under it, and a tape and its -wal copied
    /// while it writes can disagree. RED before the hold: the home was moved with the lock held.
    /// </summary>
    [Fact]
    public void A_home_another_TradeAgent_still_holds_is_not_moved_and_nothing_of_it_is_copied()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var before = Fingerprint(home);
        var refused = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            using var running = TradeAgent.Security.SingleInstanceLock.TryAcquire(Path.Combine(home, "state", "gateway.lock"));
            Assert.NotNull(running);
            OlderHome.SetAside(home, Noon, out _);
        });
        log.WriteLine(refused.Message);

        Assert.Contains("still running", refused.Message);
        Assert.True(File.Exists(Path.Combine(home, "state", "tradeagent.db")));
        Assert.Equal(before.Keys.Order(), Fingerprint(home).Keys.Where(k => !k.EndsWith("gateway.lock", StringComparison.Ordinal)).Order());
        Assert.Equal([Path.GetFileName(home)], Directory.GetFileSystemEntries(scratch.Dir).Select(Path.GetFileName));
    }

    /// <summary>
    /// A TAPE THAT CANNOT BE COPIED MOVES NOTHING: the copy is made BEFORE the home moves, so a full disk leaves the home refused, whole and
    /// where it was, and the press can simply be made again. RED before: the home moved first, and a failed copy left it set aside with
    /// the new home running without its tape.
    /// </summary>
    [Fact]
    public void A_tape_that_cannot_be_copied_moves_nothing_and_the_press_can_be_made_again()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var before = Fingerprint(home);
        var blocked = $"{Path.GetFullPath(home)}-tape-carrying-20261030-120000";
        File.WriteAllText(blocked, "a file where the copy would go");

        var refused = Assert.Throws<InvalidOperationException>(() => OlderHome.SetAside(home, Noon, out _));
        log.WriteLine(refused.Message);

        Assert.Contains("was not moved", refused.Message);
        Assert.Equal(before, Fingerprint(home));
        File.Delete(blocked);

        var aside = OlderHome.SetAside(home, Noon, out var words);
        Assert.Contains("copied into the new folder byte for byte", words);
        Assert.Equal(before, Fingerprint(aside));
        Assert.Equal(Hash(Path.Combine(aside, "state", "tape.db")), Hash(Path.Combine(home, "state", "tape.db")));
    }

    /// <summary>
    /// A FOLDER BESIDE THE HOME IS NEVER REMOVED FOR ITS NAME: one the owner keeps, named like a staging folder, is there after the press with
    /// every file as it was. Only a folder this press made is ours to clear. RED before: a press removed every sibling matching the pattern.
    /// </summary>
    [Fact]
    public void A_folder_beside_the_home_is_never_removed_for_its_name()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var his = $"{Path.GetFullPath(home)}-tape-carrying-my-backup";
        Directory.CreateDirectory(his);
        File.WriteAllText(Path.Combine(his, "keep.txt"), "the owner's own file");

        OlderHome.SetAside(home, Noon, out var words);

        Assert.Contains("copied into the new folder byte for byte", words);
        Assert.Equal("the owner's own file", File.ReadAllText(Path.Combine(his, "keep.txt")));
    }

    /// <summary>
    /// THE COPY LEAVES NOTHING BEHIND: no staging folder stays beside the two homes once the tape has taken its name. RED if it did.
    /// </summary>
    [Fact]
    public void A_carried_tape_leaves_no_staging_folder_beside_the_homes()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var aside = OlderHome.SetAside(home, Noon, out _);
        Assert.Equal(new[] { Path.GetFileName(home), Path.GetFileName(aside) }.Order(StringComparer.Ordinal),
            Directory.GetFileSystemEntries(scratch.Dir).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A TAPE THAT FAILS SQLITE'S OWN CHECK IS NOT CARRIED: its layout row reads fine, a page of its records does not. It stays, whole, in the
    /// old home, and the words say so. RED with the layout row the only check: the damaged copy took the tape's name in the new home.
    /// </summary>
    [Fact]
    public void A_tape_that_fails_its_own_check_is_not_carried()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = OldShapeHome(scratch.Dir);
        var tape = Path.Combine(home, "state", "tape.db");
        var bytes = File.ReadAllBytes(tape);
        var page = (bytes[16] << 8) | bytes[17];
        Array.Fill(bytes, (byte)0xFF, bytes.Length - page, page);
        File.WriteAllBytes(tape, bytes);
        var before = Fingerprint(home);

        var aside = OlderHome.SetAside(home, Noon, out var words);
        log.WriteLine(words);

        Assert.Contains("was not carried", words);
        Assert.Equal(before, Fingerprint(aside));
        Assert.False(File.Exists(Path.Combine(home, "state", "tape.db")));
    }

    /// <summary>
    /// A HOME THIS BUILD FOUNDED is not refused, and Start fresh never moves it: the probe reads no older shape, the press refuses.
    /// </summary>
    [Fact]
    public void A_home_this_build_founded_is_never_refused_or_moved()
    {
        using var scratch = TestEnv.NewScratch("founding-carry");
        var home = Path.Combine(scratch.Dir, "TradeAgent");
        using (new Database(Path.Combine(home, "state", "tradeagent.db"))) { }
        Assert.Null(OlderHome.RefusedPolicy(home));
        Assert.Null(TradeAgent.App.AppHost.ProbeOlderHome(home));
        Assert.Null(BoardPolicyBody.OlderShape(BoardPolicyBody.Canonical(BoardFounding.V1), Sha256Hex.Of(BoardPolicyBody.Canonical(BoardFounding.V1))));
        Assert.Throws<InvalidOperationException>(() => OlderHome.SetAside(home, Noon, out _));
        using (new Database(Path.Combine(home, "state", "tradeagent.db"))) { }
    }
}
