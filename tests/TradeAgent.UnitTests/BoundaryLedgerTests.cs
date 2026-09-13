using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CONSEQUENTIAL BOUNDARY — <c>docs/COUNCIL.md</c>:59-65, the one paragraph of the doctrine that
/// spends the strongest model and had no product code at all before this unit.
///
/// <para><b>What was wrong before this.</b> <c>grep -rn "assessment\|challenge" src</c> was empty,
/// <c>mission_event</c> had no boundary kind, and nothing anywhere deduplicated a proposal. Every one of
/// the paragraph's five clauses — two sealed assessments, one bounded challenge, one disposition, a
/// deadline with a predetermined default, and deduplication by entity and revision — was a sentence in a
/// document.</para>
///
/// <para><b>The mutants this class exists to catch.</b> The boundary's key taken from the attempt, so a
/// restart manufactures senior spend. The seal released when ONE assessment exists, which is the whole
/// protocol gone. The challenge's cap dropped. And the deadline comparison inverted, which disposes every
/// boundary the instant it opens and makes the two assessments the owner paid for arrive after the
/// answer.</para>
/// </summary>
public class BoundaryLedgerTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A day, which is what <see cref="CouncilBoundaries.DefaultWindow"/> is.</summary>
    static readonly TimeSpan Window = CouncilBoundaries.DefaultWindow;

    const string Version = "5d221b931c28aa";

    static CouncilBoundaries Boundaries(Database db, TimeSpan? window = null) =>
        new(db, () => window ?? Window);

    /// <summary>One proposal, raised the way the referee raises it: the version, under one campaign.</summary>
    static BoundaryOpened Raise(CouncilBoundaries b, long campaign = 1, DateTimeOffset? at = null,
        string entity = Version, string standing = BoundaryDisposition.Deploy) =>
        b.Open(BoundaryKind.Promotion, entity, campaign, standing,
            $"holdout run 9f3c under campaign {campaign}", at ?? At);

    static List<MissionEvent> Wakes(Database db) =>
        [.. new MissionEventStore(db).OfKind(MissionEventKind.Boundary)];

    // ---- item 1: deduplicated by entity and revision -------------------------------------------------

    /// <summary>
    /// RED FIRST: two raises of ONE proposal buy two turns each.
    ///
    /// <para><c>docs/COUNCIL.md</c>:64 — "Deduplicate boundary events by entity and revision, so repeated
    /// proposals cannot manufacture senior spend." A boundary costs two turns of the strongest model the
    /// moment it opens, so a proposal that can be re-raised is a budget anybody who can re-submit can
    /// empty.</para>
    ///
    /// <para>The count asserted is the WAKES, not the rows: a second boundary row nobody was woken for
    /// costs nothing, and a second wake with no row is the spend without the fact. Two, one per director,
    /// however many times the proposal is made.</para>
    /// </summary>
    [Fact]
    public void Two_raises_of_one_proposal_open_one_boundary_and_buy_one_turn_each()
    {
        using var db = TestEnv.NewDb();
        var boundaries = Boundaries(db);

        var first = Raise(boundaries);
        var again = Raise(boundaries, at: At.AddMinutes(5));

        Assert.True(first.Fresh);
        Assert.False(again.Fresh);
        Assert.Equal(first.Row.Id, again.Row.Id);
        Assert.Equal(BoundaryIds.Of(BoundaryKind.Promotion, Version, 1), first.Row.Id);

        Assert.Single(boundaries.All());

        // ONE PAID TURN EACH, AND EXACTLY ONE. The ids are the deduplication, so the second raise
        // collides with rows the table already holds.
        var wakes = Wakes(db);
        Assert.Equal(2, wakes.Count);
        Assert.Equal(CouncilRoles.All.Order(), wakes.Select(w => w.For).Order());

        // The opening instant is the FIRST raise's, not the second's: a re-proposal must not be able to
        // push the deadline out by asking again.
        Assert.Equal(At, again.Row.OpenedAt);
        Assert.Equal(At + Window, again.Row.DeadlineAt);
    }

    /// <summary>
    /// A RESTART RAISES THE SAME PROPOSAL AND BUYS NOTHING. The same boundary, over the same bytes, from
    /// a genuinely new <see cref="Database"/> — which is what a key minted from the attempt or the clock
    /// cannot survive, and is the mutant this pair is built against.
    /// </summary>
    [Fact]
    public void A_restart_raising_the_same_proposal_buys_no_second_turn()
    {
        var file = Path.Combine(TestEnv.Home, $"boundary-{Guid.NewGuid():n}.db");

        using (var first = new Database(file)) Raise(Boundaries(first));

        using var restarted = new Database(file);
        var again = Raise(Boundaries(restarted), at: At.AddHours(3));

        // THE SPEND FIRST, because the spend is the property: two turns of the strongest model, once,
        // however many processes have raised the proposal.
        Assert.Equal(2, Wakes(restarted).Count);
        Assert.Single(Boundaries(restarted).All());
        Assert.False(again.Fresh);
    }

    /// <summary>
    /// A DIFFERENT REVISION IS A DIFFERENT BOUNDARY, and that is the other half of :64. The same version
    /// judged under a RENEWED campaign is a new question about new evidence and is worth the spend;
    /// deduplicating on the entity alone would mean a version could be judged once, ever.
    /// </summary>
    [Fact]
    public void The_same_version_under_a_different_campaign_is_a_second_boundary()
    {
        using var db = TestEnv.NewDb();
        var boundaries = Boundaries(db);

        Assert.True(Raise(boundaries, campaign: 1).Fresh);
        Assert.True(Raise(boundaries, campaign: 2).Fresh);

        Assert.Equal(2, boundaries.All().Count);
        Assert.Equal(4, Wakes(db).Count);
    }

    /// <summary>
    /// THE DEFAULT IS WRITTEN AT OPEN AND THE ROW IS OPEN WITH NOBODY'S NAME ON IT.
    ///
    /// "A deadline with a predetermined default" is precommitment (<c>docs/COUNCIL.md</c>:212): a default
    /// worked out when the clock expires is a default chosen once the outcome is known. Nothing here has
    /// a disposition, a disposed_at or a disposed_by until code applies one.
    /// </summary>
    [Fact]
    public void A_boundary_opens_with_its_default_already_written_and_nobody_as_its_author()
    {
        using var db = TestEnv.NewDb();
        var row = Raise(Boundaries(db), standing: BoundaryDisposition.Hold).Row;

        Assert.Equal(BoundaryDisposition.Hold, row.DefaultDisposition);
        Assert.True(row.IsOpen);
        Assert.Null(row.Disposition);
        Assert.Null(row.DisposedAt);
        Assert.Null(row.DisposedBy);
        Assert.Equal(BoundaryKind.Promotion, row.Kind);
    }

    /// <summary>
    /// THE TWO TABLES ARE SCHEMA 16, AND THEIR COLUMNS ARE ASSERTED BECAUSE THE KEYS ARE THE DESIGN.
    ///
    /// <para>A FLOOR plus "the stamped number is this build's", so an additive rung above this one needs
    /// no edit here — the shape `U-referee-2` settled on, and the reason `U-api-worker`'s pinned equality
    /// had to be rewritten when the next unit landed.</para>
    ///
    /// <para><c>boundary_event</c> has no <c>attempt</c>, no <c>session</c> and no <c>opened_by</c>: its
    /// identity is the fact and the columns say so. <c>boundary_submission</c>'s primary key is
    /// (boundary, role, kind), which is the second-assessment refusal in SQL.</para>
    /// </summary>
    [Fact]
    public void The_boundary_tables_are_schema_sixteen_and_carry_no_column_naming_an_attempt()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= 16,
            "the consequential boundary needs schema 16 or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            db.Read(_ =>
            {
                using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return c.ExecuteScalar() as string;
            }));

        Assert.Equal([
            "id", "kind", "entity", "revision", "opened_at", "deadline_at", "default_disposition",
            "evidence", "disposition", "disposed_at", "disposed_by"
        ], Columns(db, "boundary_event"));

        Assert.Equal(["boundary_id", "role", "kind", "publication_id", "at"],
            Columns(db, "boundary_submission"));
    }

    // ---- item 4: the deadline's default is applied by CODE ------------------------------------------

    /// <summary>
    /// RED FIRST: the deadline passes and the boundary stays open for ever.
    ///
    /// <para><c>docs/COUNCIL.md</c>:62 — "a deadline with a predetermined default, and code applies the
    /// promotion and allocation policy so neither director can veto an eligible deployment forever." A
    /// veto is not only a director saying no. A director that never answers holds an eligible deployment
    /// exactly as effectively, and it is the cheaper way to do it: saying nothing costs nothing.</para>
    ///
    /// <para>What is written is the default recorded AT OPEN, and the author is <c>policy</c>. There is
    /// no method on <see cref="CouncilBoundaries"/> that takes a disposition from a caller, so no
    /// director — and no agent anywhere — can write any other value here.</para>
    /// </summary>
    [Fact]
    public void At_the_deadline_the_app_writes_the_policys_default_and_nobody_else_can()
    {
        using var db = TestEnv.NewDb();
        var boundaries = Boundaries(db);
        var row = Raise(boundaries).Row;

        // A MINUTE BEFORE: nothing is settled, and nothing was going to be.
        Assert.Empty(boundaries.ApplyDue(row.DeadlineAt.AddMinutes(-1)));
        Assert.True(boundaries.ById(row.Id)!.IsOpen);

        var settled = Assert.Single(boundaries.ApplyDue(row.DeadlineAt));
        Assert.Equal(row.Id, settled.Id);
        Assert.Equal(BoundaryDisposition.Deploy, settled.Disposition);
        Assert.Equal(row.DefaultDisposition, settled.Disposition);
        Assert.Equal(BoundaryAuthor.Policy, settled.DisposedBy);
        Assert.Equal(row.DeadlineAt, settled.DisposedAt);
        Assert.False(settled.IsOpen);

        // IDEMPOTENT. A second sweep settles nothing, so the instant and the author on the row are the
        // ones the first sweep wrote.
        Assert.Empty(boundaries.ApplyDue(row.DeadlineAt.AddDays(1)));
        Assert.Equal(row.DeadlineAt, boundaries.ById(row.Id)!.DisposedAt);

        // AND NOTHING BUT THE POLICY CAN WRITE ONE. The surface is held by name: a method that took a
        // disposition from a caller is the one thing that would turn this into a veto.
        Assert.Empty(typeof(CouncilBoundaries).GetMethods()
            .Where(m => m.GetParameters().Any(x => x.Name is "disposition" or "disposedBy"))
            .Select(m => m.Name)
            .Except([nameof(CouncilBoundaries.Open)]));
    }

    /// <summary>
    /// THE CHALLENGE WINDOW CLOSING IS THE OTHER TRIGGER, and it settles the boundary early.
    ///
    /// <para>Once both assessments are delivered and the one bounded challenge has been written, the
    /// protocol has had everything it is ever going to get and waiting out the clock buys nothing. It is
    /// still CODE that answers: the disposition written is the same frozen default, and a director that
    /// wrote the challenge has no more say in the outcome than one that wrote nothing.</para>
    /// </summary>
    [Fact]
    public void A_boundary_whose_challenge_window_has_closed_is_disposed_before_its_deadline()
    {
        using var db = TestEnv.NewDb();
        var boundaries = Boundaries(db);
        var row = Raise(boundaries, standing: BoundaryDisposition.Hold).Row;

        foreach (var role in CouncilRoles.All)
            Assert.True(boundaries.Assess(Assessment(role, $"{role} read it."), At).Ok);

        // Still open: the assessments are sealed, so there is nothing to challenge yet.
        Assert.Empty(boundaries.ApplyDue(At.AddMinutes(1)));

        boundaries.Release(At.AddMinutes(2));
        Assert.True(boundaries.AssessmentsDelivered(row.Id));
        Assert.Empty(boundaries.ApplyDue(At.AddMinutes(3)));

        Assert.True(boundaries.Challenge(
            Assessment(CouncilRoles.Operations, "the sample is one campaign wide.",
                PublicationKind.Challenge), At.AddMinutes(4)).Ok);

        var settled = Assert.Single(boundaries.ApplyDue(At.AddMinutes(5)));
        Assert.Equal(BoundaryDisposition.Hold, settled.Disposition);
        Assert.Equal(BoundaryAuthor.Policy, settled.DisposedBy);
        Assert.True(At.AddMinutes(5) < row.DeadlineAt, "settled before the clock, not by it");
    }

    /// <summary>
    /// THE EARLIEST DEADLINE STILL TO COME, which is what the loop sleeps until so that a boundary is
    /// settled on the owner's clock rather than whenever an agent next happens to be woken.
    /// </summary>
    [Fact]
    public void The_next_deadline_is_the_earliest_open_one_and_nothing_once_all_are_settled()
    {
        using var db = TestEnv.NewDb();
        var boundaries = Boundaries(db);

        Assert.Null(boundaries.NextDeadline());

        var first = Raise(boundaries, campaign: 1, at: At).Row;
        Raise(boundaries, campaign: 2, at: At.AddHours(1));
        Assert.Equal(first.DeadlineAt, boundaries.NextDeadline());

        boundaries.ApplyDue(first.DeadlineAt);
        Assert.Equal(At.AddHours(1) + Window, boundaries.NextDeadline());

        boundaries.ApplyDue(At.AddDays(2));
        Assert.Null(boundaries.NextDeadline());
    }

    /// <summary>One director's submission, as the relay builds it: content-addressed, to the peer.</summary>
    static Publication Assessment(string role, string text, string kind = PublicationKind.Assessment) => new()
    {
        Id = Publication.IdOf(role, kind, text),
        Role = role,
        Kind = kind,
        Recipients = string.Join(",", CouncilRelay.RecipientsOf(role)),
        CreatedAt = At,
        Content = text
    };

    static List<string> Columns(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT name FROM pragma_table_info('{table}')");
        var names = new List<string>();
        using var r = c.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    });
}
