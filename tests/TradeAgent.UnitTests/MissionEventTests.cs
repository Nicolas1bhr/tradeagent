using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE WAKE QUEUE, AND THE ONE PROPERTY IT EXISTS FOR: RAISING THE SAME FACT TWICE COSTS NOTHING.
///
/// Every id is a function of the fact rather than of the moment it was noticed — a fill is its
/// execution id, an order transition is its request id and state, a scan is the instant it ran. That
/// is what makes a replay free, and a replay is not hypothetical: the fill ledger deliberately has
/// two sources serving the same executions (<c>TradingGateway</c>), a reconnect re-pulls a whole
/// day, and a restart re-reads a scan. With a minted id every one of those would have been another
/// paid turn.
///
/// The table is written by the app and by nothing else — no verb, no pipe op — which is the rule
/// <c>material</c> and <c>ai_attempt</c> already keep, and is checked by
/// <see cref="MissionEventReachTests"/> below.
/// </summary>
public class MissionEventTests
{
    static readonly DateTimeOffset At = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The wake queue arrived at schema 7, this build is at or past it, and the version on disk is
    /// the one this build writes. It used to read <c>Assert.Equal(7, ...)</c> — the same shape the
    /// launch ledger's test was already cured of: an ADDITIVE migration that touched nothing about
    /// <c>mission_event</c> failed here anyway. <c>U-data-binance</c> added <c>dataset</c> at 8 and
    /// the council's role columns and relay tables came at 9; the exact number moves with whichever
    /// migration last claimed it, now
    /// <see cref="CouncilRoleTests.The_role_columns_arrive_at_schema_nine_and_an_unnamed_row_is_the_chairs"/>.
    /// 7 is this class's floor, because below it there is no wake queue at all.
    /// </summary>
    [Fact]
    public void The_schema_carries_the_table_at_version_seven_or_later()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= 7,
            $"the wake queue needs schema 7 or later; this build says {Versions.DatabaseSchemaVersion}");
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return c.ExecuteScalar() as string;
        }));
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM mission_event");
            return Convert.ToInt64(c.ExecuteScalar());
        }));

        // The table itself, which is what the name is about: present, readable, and empty.
        Assert.Null(new MissionEventStore(db).NextDueAt());
        Assert.Empty(new MissionEventStore(db).Due(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// RED FIRST. The same execution reported by the connector's event stream and again by the
    /// five-minute pull is ONE reason to wake, and the second raise writes nothing at all — not a
    /// second row, and not an exception either, because the caller raising it is a code path that
    /// must carry on.
    /// </summary>
    [Fact]
    public void A_second_raise_of_the_same_id_is_a_no_op()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var id = MissionEventIds.Fill("EXEC-9001");

        Assert.True(events.Raise(id, MissionEventKind.Fill, At, """{"symbol":"ES"}"""));
        Assert.False(events.Raise(id, MissionEventKind.Fill, At.AddMinutes(5), """{"symbol":"NQ"}"""));

        var rows = events.OfKind(MissionEventKind.Fill);
        Assert.Single(rows);
        // The FIRST sighting is what stands. A repeat that overwrote the payload would let a later,
        // worse reading of the same fact replace the one that was actually recorded.
        Assert.Equal("""{"symbol":"ES"}""", rows[0].Payload);
        Assert.Equal(At, rows[0].CreatedAt);
    }

    /// <summary>Every id is the fact, spelled the same way twice from the same inputs.</summary>
    [Fact]
    public void Every_id_is_a_function_of_the_fact_and_not_of_the_clock()
    {
        var day = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("owner:7", MissionEventIds.Owner(7));
        Assert.Equal("fill:EXEC-1", MissionEventIds.Fill("EXEC-1"));
        Assert.Equal("order:req-1:FILLED", MissionEventIds.Order("req-1", "FILLED"));
        Assert.Equal("self:turn-42", MissionEventIds.Self("turn-42"));
        Assert.Equal(MissionEventIds.Inbox(At), MissionEventIds.Inbox(At));
        Assert.NotEqual(MissionEventIds.Inbox(At), MissionEventIds.Inbox(At.AddSeconds(1)));
        Assert.Equal($"renewal:{day.LocalDateTime:yyyy-MM-dd}", MissionEventIds.Renewal(day));
        // To the minute: two review ticks scheduled inside one minute are one review.
        Assert.Equal(MissionEventIds.Review(At), MissionEventIds.Review(At.AddSeconds(30)));
    }

    /// <summary>Nothing is due before its time, and what is due comes back oldest first.</summary>
    [Fact]
    public void Only_what_is_due_comes_back_and_the_earliest_is_what_the_loop_sleeps_until()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);

        events.RaiseDue("self:a", MissionEventKind.Self, At, At.AddMinutes(10));
        events.RaiseDue("review:b", MissionEventKind.Review, At, At.AddMinutes(30));
        events.Raise("owner:1", MissionEventKind.Owner, At);

        Assert.Equal(["owner:1"], events.Due(At).Select(e => e.Id));
        Assert.Equal(["owner:1", "self:a"], events.Due(At.AddMinutes(10)).Select(e => e.Id));
        Assert.Equal(At, events.NextDueAt());
        Assert.Equal(MissionEventKind.Owner, events.NextKind());
    }

    /// <summary>
    /// A CONSUMED EVENT IS NEVER SERVED AGAIN, and it is never re-assigned either: a wake handed to
    /// one launch stays that launch's, so a second attempt cannot be charged for the same work.
    /// </summary>
    [Fact]
    public void Consumption_is_once_and_names_the_attempt_that_took_it()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        events.Raise("owner:1", MissionEventKind.Owner, At);

        Assert.Equal(1, events.Consume(["owner:1"], "turn-a", At));
        Assert.Equal(0, events.Consume(["owner:1"], "turn-b", At.AddMinutes(1)));

        var row = events.Get("owner:1")!;
        Assert.True(row.Consumed);
        Assert.Equal("turn-a", row.ConsumedBy);
        Assert.Empty(events.Due(At.AddHours(1)));
        Assert.Null(events.NextDueAt());
    }

    /// <summary>
    /// THE LAUNCH RECORD AND THE CONSUMPTION ARE ONE COMMIT. Two would leave a window in which the
    /// turn is recorded and its wake is not — and a kill inside that window hands the same wake to
    /// the next launch, which the owner pays for twice.
    /// </summary>
    [Fact]
    public void The_attempt_row_and_the_consumption_are_written_together()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        var attempts = new AiAttemptStore(db);
        events.Raise("owner:1", MissionEventKind.Owner, At);
        events.Raise("inbox:x", MissionEventKind.Inbox, At);

        attempts.Begin(new AiAttempt { Id = "turn-1", StartedAt = At, ReservedCost = 0.5m },
            consuming: ["owner:1", "inbox:x"]);

        Assert.Equal(AiAttemptState.LAUNCHED, attempts.Get("turn-1")!.State);
        Assert.Equal("turn-1", events.Get("owner:1")!.ConsumedBy);
        Assert.Equal("turn-1", events.Get("inbox:x")!.ConsumedBy);
        Assert.Empty(events.Due(At));
    }

    /// <summary>
    /// A disposition is a verdict on a turn that happened, so it is refused on an event nobody has
    /// taken. Otherwise "answered" could be written over a message that was never read.
    /// </summary>
    [Fact]
    public void A_disposition_needs_a_turn_to_have_taken_the_event()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        events.Raise("owner:1", MissionEventKind.Owner, At);

        Assert.False(events.Settle("owner:1", MissionEventDisposition.Answered));
        events.Consume(["owner:1"], "turn-1", At);
        Assert.True(events.Settle("owner:1", MissionEventDisposition.Answered));
        Assert.Equal(MissionEventDisposition.Answered, events.Get("owner:1")!.Disposition);
    }

    /// <summary>The sequence comes from the ids already written, so a restart cannot reuse one.</summary>
    [Fact]
    public void The_owner_sequence_continues_across_a_fresh_store_over_the_same_database()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);

        Assert.Equal(1, events.NextOwnerSequence());
        events.Raise(MissionEventIds.Owner(1), MissionEventKind.Owner, At);
        events.Raise(MissionEventIds.Owner(2), MissionEventKind.Owner, At);
        events.Consume([MissionEventIds.Owner(1)], "turn-1", At);

        Assert.Equal(3, new MissionEventStore(db).NextOwnerSequence());
    }
}

/// <summary>
/// THE TABLE IS THE APP'S AND THE AGENT CANNOT REACH IT — the rule <c>CLAUDE.md</c> states for
/// <c>material</c>, for the same reason: an agent that could insert here would buy itself turns, and
/// one that could delete here would erase what it was asked to do.
///
/// Checked over the shipped verb list and the pipe's operation list rather than by reading the code,
/// because the failure this guards against is a later unit adding a convenient op.
/// </summary>
public class MissionEventReachTests
{
    [Fact]
    public void No_verb_and_no_pipe_op_names_the_wake_queue()
    {
        var ops = string.Join(" ", typeof(Ops).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!));

        Assert.DoesNotContain("event", ops, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wake", ops, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mission", ops, StringComparison.OrdinalIgnoreCase);
    }
}
