using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A BOARD FORECAST NAMES THE ATTEMPT THAT MADE IT (<c>U-cards-2</c> item 1, U-lineage-check's card): the board's forecasts are the
/// board's own ledger, outside the chart, but lineage still needs an attempt — the writer refuses an act that carries a forecast and
/// names none, in words and with nothing written, and rung 55's trigger refuses a later writer that skips the check. A row from
/// before the rung stays as it was. NOTHING HERE REACHES A REAL MODEL.
/// </summary>
public class BoardForecastLineageTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static string Change(string field, object from, object to) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    static ForecastClaim AForecast() =>
        new(ForecastVocabulary.UnitAdmissible, OrgIds.Research, ForecastVocabulary.NoneAdmitted, Now.AddDays(1), 0.8m);

    static BoardStore ActiveBoard(Database db)
    {
        var board = new BoardStore(db);
        foreach (var seat in new[] { "board-1", "board-2", "board-3" }) board.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, Now);
        return board;
    }

    static long Count(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
        return (long)c.ExecuteScalar()!;
    });

    static void Exec(Database db, string sql, params (string, object?)[] ps) => db.Write(_ =>
    {
        using var c = db.Cmd(sql, ps);
        return c.ExecuteNonQuery();
    });

    const string InsertNoAttempt = """
        INSERT INTO board_forecast(id, act_id, forecaster, attempt_id, vocabulary, predicate, subject, target, resolve_by, probability, scored, unscored_why, at)
        VALUES($id, $act, 'board-1', $attempt, 1, 'p', 's', 't', $at, '0.5', 1, NULL, $at)
        """;

    [Fact]
    public void An_act_with_a_forecast_and_no_attempt_is_refused_in_words_and_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        var board = ActiveBoard(db);
        foreach (var attempt in new string?[] { null, "", "  \t" })
        {
            foreach (var propose in new[] { false, true })
            {
                var request = new BoardActRequest("board-1", attempt, Change("plan_day_total", 40, propose ? 50 : 30), "a reason", [], [AForecast()]);
                var e = Assert.Throws<TradeAgentException>(() => propose ? board.Propose(request, Now) : board.Narrow(request, Now));
                Assert.Contains("board forecast records the attempt", e.Message, StringComparison.Ordinal);
                Assert.Contains("Nothing was written", e.Message, StringComparison.Ordinal);
            }
        }
        Assert.Equal((0L, 0L, 0L), (Count(db, "board_act"), Count(db, "board_act_event"), Count(db, "board_forecast")));

        // An act with NO forecast needs none from this rule (the act's own attempt is a separate, older question); one that names its
        // attempt writes the forecast with it.
        board.Propose(new BoardActRequest("board-1", "turn-test", Change("plan_day_total", 40, 50), "a reason", [], [AForecast()]), Now);
        Assert.Equal(1L, Count(db, "board_forecast"));
        Assert.Equal("turn-test", db.Read(_ =>
        {
            using var c = db.Cmd("SELECT attempt_id FROM board_forecast");
            return (string)c.ExecuteScalar()!;
        }));
    }

    [Fact]
    public void The_database_refuses_a_board_forecast_row_with_no_attempt_whichever_writer_tried()
    {
        using var db = TestEnv.NewDb();
        var board = ActiveBoard(db);
        var act = board.Propose(new BoardActRequest("board-1", "turn-test", Change("plan_day_total", 40, 50), "a reason", [], []), Now);
        foreach (var attempt in new string?[] { null, "", " \n " })
        {
            var e = Assert.Throws<SqliteException>(() => Exec(db, InsertNoAttempt,
                ("$id", "bfc-" + Guid.NewGuid().ToString("n")[..8]), ("$act", act.Act.Id), ("$attempt", attempt), ("$at", "2026-11-01T12:00:00.0000000Z")));
            Assert.Contains("lineage: a board forecast names the attempt", e.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0L, Count(db, "board_forecast"));
    }

    [Fact]
    public void A_board_forecast_written_before_the_rung_keeps_its_null_and_the_trigger_is_made_on_reopening()
    {
        var path = Path.Combine(TestEnv.Home, $"db-{Guid.NewGuid():n}.db");
        string id = "bfc-oldrow0001";
        using (var db = new Database(path))
        {
            var act = ActiveBoard(db).Propose(new BoardActRequest("board-1", "turn-test", Change("plan_day_total", 40, 50), "a reason", [], []), Now);
            Exec(db, $"DROP TRIGGER {ArtifactLineage.BoardForecastTrigger}");
            Exec(db, InsertNoAttempt, ("$id", id), ("$act", act.Act.Id), ("$attempt", null), ("$at", "2026-11-01T12:00:00.0000000Z"));
            Exec(db, $"UPDATE meta SET value='{Versions.BoardForecastLineageRung - 1}' WHERE key='schema_version'");
        }

        using var reopened = new Database(path);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            reopened.Read(_ => { using var c = reopened.Cmd("SELECT value FROM meta WHERE key='schema_version'"); return c.ExecuteScalar() as string; }));
        Assert.Equal(1L, Count(reopened, "board_forecast"));
        Assert.True(reopened.Read(_ =>
        {
            using var c = reopened.Cmd("SELECT attempt_id IS NULL FROM board_forecast WHERE id=$id", ("$id", id));
            return (long)c.ExecuteScalar()! == 1;
        }), "no backfill: the row from before the rung keeps its NULL");
        Assert.Equal(1L, reopened.Read(_ =>
        {
            using var c = reopened.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name=$n AND tbl_name='board_forecast'",
                ("$n", ArtifactLineage.BoardForecastTrigger));
            return (long)c.ExecuteScalar()!;
        }));
    }
}
