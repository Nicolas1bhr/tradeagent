using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// NOTHING AN AGENT WRITES CAN MARK THE OWNER'S WORDS HANDLED (<c>U-owner-words-delegated</c>). The <c>delegated</c>
/// link is the app's measurement of a FINISHED turn: the launch the brief's file name carries ENDED, is the brief's
/// role's, consumed the message, and the message had no outcome yet. A brief naming a lost turn, an unknown launch or
/// another role's leaves the words where they are; and words a finished turn left without any outcome are handed on
/// once, as a lost turn's are.
/// </summary>
public partial class CouncilLoopTests
{
    const string Said = "close the ES position before the weekend";
    const string BriefText = "Research: look into the opening range on ES before Monday.";

    static string OutOf(string root, string role)
    {
        var dir = Path.Combine(WorkspaceBuilder.HomeOf(root, role), WorkspaceBuilder.OutDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The agenda an Operations turn stages, named after the launch it claims, exactly as the Situation asks.</summary>
    static void StageAgenda(string root, string attempt) =>
        File.WriteAllText(Path.Combine(OutOf(root, CouncilRoles.Operations),
            CouncilRelay.Pattern(PublicationKind.Brief).Replace("*", attempt)), BriefText);

    /// <summary>The owner's message, consumed by a launch of <paramref name="role"/> in the launch's own write.</summary>
    static (MissionEvent Owner, string Attempt) TakenBy(Database db, string role, DateTimeOffset at, string said = Said)
    {
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage(said, at));
        var owner = events.OfKind(MissionEventKind.Owner).Last();
        var attempt = $"turn-{Guid.NewGuid():n}";
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = attempt, StartedAt = at, ReservedCost = 0.10m, Role = role
        }, [owner.Id]).Admitted);
        return (owner, attempt);
    }

    static void Ended(Database db, string attempt, DateTimeOffset at) =>
        Assert.True(new AiAttemptStore(db).End(attempt, 0, at, null, null, null, null, null, null, null, null, null));

    /// <summary>The Operations Director's brief, committed as the relay commits one.</summary>
    static Publication Brief(Database db, DateTimeOffset at, string attempt)
    {
        var p = new Publication
        {
            Id = Publication.IdOf(CouncilRoles.Operations, PublicationKind.Brief, BriefText),
            Role = CouncilRoles.Operations,
            Attempt = attempt,
            Kind = PublicationKind.Brief,
            Recipients = CouncilRoles.Research,
            Classification = PublicationClass.Council,
            CreatedAt = at,
            Content = BriefText
        };
        new PublicationStore(db).Commit(p, at);
        return p;
    }

    static List<MissionEvent> RetriesOf(MissionEventStore events, string id) =>
        [.. events.OfKind(MissionEventKind.Owner).Where(e => Json.Read<MissionOwnerMessage>(e.Payload ?? "")?.RetryOf == id)];

    /// <summary>
    /// (a) A BRIEF NAMING A LOST TURN LEAVES ITS OWNER MESSAGE OPEN, AND THE WORDS ARE RE-RAISED — IN THE APP'S OWN
    /// START-UP ORDER. The chair's turn took the owner's words, staged an agenda naming itself, and the process died; the
    /// restart's meter declares the launch LOST. <c>AppHost</c> runs <see cref="CouncilRelay.Reconcile"/> BEFORE the loop's
    /// first pass, so the agenda is published under that lost launch first — the work is real — and then the loop's
    /// recovery settles the message <c>failed</c> and re-raises the words for the chair's next turn, first.
    ///
    /// <para>RED at base c4a6f2d8: the relay marked the lost turn's message <c>delegated</c>, so recovery skipped it
    /// and the words were never re-raised.</para>
    /// </summary>
    [Fact]
    public async Task A_brief_naming_a_lost_turn_leaves_its_owner_message_open_and_re_raised()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        var (owner, lost) = TakenBy(db, CouncilRoles.Operations, now);
        StageAgenda(root, lost);

        // THE CRASH, AND THE RESTART: the app's meter opens the database and loses the launch nobody will report.
        Assert.NotNull(new TurnMeter(db, () => 100m, now: () => now.AddMinutes(1), live: new LiveAttempts(),
            recordPath: Path.Combine(root, "turns.jsonl")));
        Assert.Equal(AiAttemptState.LOST, new AiAttemptStore(db).Get(lost)!.State);

        // THE START, IN AppHost's ORDER: the relay's reconcile pass, then the loop.
        new CouncilRelay(db, r => WorkspaceBuilder.HomeOf(root, r)).Reconcile();
        Assert.Contains(new PublicationStore(db).By(CouncilRoles.Operations),
            p => p.Kind == PublicationKind.Brief && p.Attempt == lost);

        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        var opened = await Quiet(host, HalfHourLooks, now.AddMinutes(2), TimeSpan.FromHours(4));

        var settled = events.Get(owner.Id)!;
        Assert.Equal(MissionEventDisposition.Failed, settled.Disposition);
        Assert.Single(RetriesOf(events, owner.Id));

        var turn = Assert.Single(opened, o => o.Role == CouncilRoles.Operations);
        Assert.True(turn.Prompt.IndexOf(Said, StringComparison.Ordinal) is >= 0 and var at
                    && at < turn.Prompt.IndexOf("Why you are awake", StringComparison.Ordinal),
            "the chair's next turn does not carry the owner's words first");
    }

    /// <summary>
    /// (b) A BRIEF NEVER OVERWRITES AN OUTCOME. A finished turn's brief, published again by the start-up pass after the
    /// message was answered — and another whose message a turn settled <c>failed</c> and re-raised — leave both outcomes
    /// as they were: <c>delegated</c> is written only where nothing has settled the message yet.
    ///
    /// <para>RED at base c4a6f2d8: <c>Delegated</c> overwrote <c>answered</c> and <c>failed</c> alike.</para>
    /// </summary>
    [Fact]
    public void A_brief_never_overwrites_an_answered_or_failed_owner_message()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();

        var (answered, a) = TakenBy(db, CouncilRoles.Operations, now);
        Ended(db, a, now.AddMinutes(1));
        Assert.True(events.Settle(answered.Id, MissionEventDisposition.Answered));

        var (failed, b) = TakenBy(db, CouncilRoles.Operations, now.AddMinutes(2), "and the NQ one too");
        Ended(db, b, now.AddMinutes(3));
        Assert.True(events.Settle(failed.Id, MissionEventDisposition.Failed, "the runtime fell over"));

        StageAgenda(root, a);
        new CouncilRelay(db, r => WorkspaceBuilder.HomeOf(root, r)).Reconcile();
        var brief = Brief(db, now, b);

        Assert.Equal(0, events.Delegated(a, brief.Id));
        Assert.Equal(0, events.Delegated(b, brief.Id));
        Assert.Equal(MissionEventDisposition.Answered, events.Get(answered.Id)!.Disposition);
        Assert.Equal(MissionEventDisposition.Failed, events.Get(failed.Id)!.Disposition);
        Assert.Equal("the runtime fell over", events.Get(failed.Id)!.DispositionDetail);
    }

    /// <summary>
    /// (c) A BRIEF NAMING ANOTHER ROLE'S TURN, OR A LAUNCH THE LEDGER DOES NOT HOLD, DELEGATES NOTHING. The link is a
    /// measurement of the brief's own role's finished launch; a message consumed by a launch with no meter row, or by a
    /// Research launch, is no brief's to settle — whatever id a caller hands the store.
    ///
    /// <para>RED at base c4a6f2d8: both were marked <c>delegated</c>.</para>
    /// </summary>
    [Fact]
    public void A_brief_naming_another_roles_or_an_unknown_turn_delegates_nothing()
    {
        var (db, _) = Workspace();
        using var __ = db;
        var events = new MissionEventStore(db);
        var now = Noon();

        Assert.True(events.RecordOwnerMessage(Said, now));
        var unknown = Assert.Single(events.OfKind(MissionEventKind.Owner));
        Assert.Equal(1, events.Consume([unknown.Id], MissionLoop.Unrecorded, now));

        var (theirs, research) = TakenBy(db, CouncilRoles.Research, now.AddMinutes(1), "and the NQ one too");
        Ended(db, research, now.AddMinutes(2));

        var brief = Brief(db, now.AddMinutes(3), research);
        Assert.Equal(0, events.Delegated(MissionLoop.Unrecorded, brief.Id));
        Assert.Equal(0, events.Delegated(research, brief.Id));
        Assert.Equal(0, events.Delegated("turn-nobody-launched", brief.Id));
        Assert.Null(events.Get(unknown.Id)!.Disposition);
        Assert.Null(events.Get(theirs.Id)!.Disposition);
    }

    /// <summary>
    /// (d) A TURN THAT ENDED WITHOUT AN OUTCOME HANDS THE OWNER'S WORDS ON, ONCE. A launch that ENDED and left the
    /// message it took with no disposition, and one with no meter row behind it, are settled <c>failed</c> with the
    /// app's own sentence and their words re-raised once, in one commit — so the chair's next turn is shown them first,
    /// and four quiet hours buy that one turn and no look after it.
    ///
    /// <para>RED at base c4a6f2d8: neither message was an objective nor re-raised, so no chair turn ever quoted them.</para>
    /// </summary>
    [Fact]
    public async Task A_turn_that_ended_without_an_outcome_hands_the_owners_words_on_once()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();

        var (ended, a) = TakenBy(db, CouncilRoles.Operations, now);
        Ended(db, a, now.AddMinutes(1));

        const string unmetered = "and the NQ one too";
        Assert.True(events.RecordOwnerMessage(unmetered, now.AddMinutes(2)));
        var unrecorded = events.OfKind(MissionEventKind.Owner).Last();
        Assert.Equal(1, events.Consume([unrecorded.Id], MissionLoop.Unrecorded, now.AddMinutes(2)));

        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        var opened = await Quiet(host, HalfHourLooks, now.AddMinutes(3), TimeSpan.FromHours(4));

        var turn = Assert.Single(opened, o => o.Role == CouncilRoles.Operations);
        foreach (var (e, said) in new[] { (ended, Said), (unrecorded, unmetered) })
        {
            var settled = events.Get(e.Id)!;
            Assert.Equal(MissionEventDisposition.Failed, settled.Disposition);
            Assert.Contains("ended without an outcome", settled.DispositionDetail ?? "", StringComparison.Ordinal);
            Assert.Single(RetriesOf(events, e.Id));
            Assert.True(turn.Prompt.IndexOf(said, StringComparison.Ordinal) is >= 0 and var at
                        && at < turn.Prompt.IndexOf("Why you are awake", StringComparison.Ordinal),
                $"the chair's next turn does not carry \"{said}\" first");
        }
        Assert.Empty(RoleObjectives.Open(db, CouncilRoles.Operations, []));
    }

    /// <summary>
    /// (e) A RETRY THAT ENDS WITHOUT AN OUTCOME IS NOT RE-RAISED AGAIN. The once rule holds on this path as on a failed
    /// or lost turn's: the retry is settled <c>failed</c>, and no third row carries the words — the owner sees both and
    /// asks again in their own words.
    ///
    /// <para>RED at base c4a6f2d8: the retry stayed unsettled for ever, no outcome and no objective.</para>
    /// </summary>
    [Fact]
    public async Task A_retry_that_ends_without_an_outcome_is_not_re_raised_again()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();

        Assert.True(events.RecordOwnerMessage(Said, now));
        var original = Assert.Single(events.OfKind(MissionEventKind.Owner));
        Assert.Equal(1, events.Consume([original.Id], $"turn-{Guid.NewGuid():n}", now));
        Assert.True(events.Settle(original.Id, MissionEventDisposition.Failed, "the runtime fell over"));
        var said = Json.Read<MissionOwnerMessage>(original.Payload!)!;
        var retryId = MissionEventIds.Owner(events.NextOwnerSequence());
        Assert.True(events.Raise(retryId, MissionEventKind.Owner, now.AddMinutes(1),
            Json.Write(said with { RetryOf = original.Id, Failure = "the runtime fell over" }), CouncilRoles.Operations));

        var attempt = $"turn-{Guid.NewGuid():n}";
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = attempt, StartedAt = now.AddMinutes(2), ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [retryId]).Admitted);
        Ended(db, attempt, now.AddMinutes(3));

        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = [] };
        await Quiet(host, HalfHourLooks, now.AddMinutes(4), TimeSpan.FromHours(4));

        var retry = events.Get(retryId)!;
        Assert.Equal(MissionEventDisposition.Failed, retry.Disposition);
        Assert.Contains("ended without an outcome", retry.DispositionDetail ?? "", StringComparison.Ordinal);
        Assert.Empty(RetriesOf(events, retryId));
        Assert.Equal(2, events.OfKind(MissionEventKind.Owner).Count);
    }

    /// <summary>
    /// (f) A FINISHED TURN'S BRIEF STILL DELEGATES THE WORDS IT TOOK — the legitimate path kept, through the relay's own
    /// committed transition: the launch closed ENDED, its agenda published, and the loop's <c>answered</c> after it does
    /// not erase which brief the message became.
    ///
    /// <para>Green at base c4a6f2d8 and kept green: it is the guard against a fix that delegates nothing at all.</para>
    /// </summary>
    [Fact]
    public void A_finished_turns_brief_still_delegates_the_words_it_took()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var events = new MissionEventStore(db);
        var now = Noon();
        var (owner, a) = TakenBy(db, CouncilRoles.Operations, now);
        StageAgenda(root, a);

        new CouncilRelay(db, r => WorkspaceBuilder.HomeOf(root, r)).CommitTurn(CouncilRoles.Operations, a,
            endAttempt: () => Ended(db, a, now.AddMinutes(1)),
            dispositions: () => events.Settle(owner.Id, MissionEventDisposition.Answered));

        var brief = Assert.Single(new PublicationStore(db).By(CouncilRoles.Operations),
            p => p.Kind == PublicationKind.Brief);
        var settled = events.Get(owner.Id)!;
        Assert.Equal(MissionEventDisposition.Delegated, settled.Disposition);
        Assert.Equal(brief.Id, settled.DispositionDetail);
    }
}
