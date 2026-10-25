using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S "FOR YOU" PAGE AND ITS READS (<c>U-org-surface-b</c>; R24 §§ 4.3, 4.7; ORCHESTRATION §§ 2.5, 2.9): what reached the
/// root, his words still owed and why, the board's power proposals, and his presses — a veto that only takes away, and an activation
/// that is his only until a board seat is active. Store-level reads here; the chart-level checks are <see cref="OrgStructureTests"/>'.
/// </summary>
public class OwnerInboxTests
{
    static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddMinutes(-30);

    /// <summary>
    /// (1a) THE OWED WORDS ACROSS ROLES CARRY THEIR BLOCK REASON: the owner's words to the chair (the chair's own row), words to the
    /// Research Director blocked by a refused launch, and a third set already taken by a launch that ENDED (no turn can close it) — the
    /// one read answers the first two, oldest first, each with its addressee, the time received and its words, the blocked one with
    /// the block's reason as the app wrote it, and exactly what <see cref="MissionEventStore.OwnerWordsOpen"/> answers per role.
    /// RED at base: no <c>MissionEventStore.OwnerWordsOwed</c>.
    /// </summary>
    [Fact]
    public void Owed_words_across_roles_carry_their_block_reason()
    {
        using var db = TestEnv.NewDb();
        var events = new MissionEventStore(db);
        Assert.True(events.RecordOwnerMessage("what is the chair doing today", At));
        Assert.True(events.Raise("owner:research-1", MissionEventKind.Owner, At.AddMinutes(1),
            Json.Write(new MissionOwnerMessage("research, look at the funding skew", At.AddMinutes(1))), CouncilRoles.Research));
        const string why = "the Research Director's launch was refused: its folder could not be rebuilt.";
        Assert.Equal(1, events.BlockOwnerWords(CouncilRoles.Research, DateTimeOffset.UtcNow, why));

        // A THIRD, taken by a launch that has ENDED: no turn can close it, so it is not owed (the loop settles and re-raises it).
        Assert.True(events.RecordOwnerMessage("an older question", At.AddMinutes(2)));
        var taken = events.OfKind(MissionEventKind.Owner).Single(e => e.Payload!.Contains("an older question", StringComparison.Ordinal));
        var attempt = $"turn-{Guid.NewGuid():n}"[..17];
        Assert.True(new AiAttemptStore(db).Begin(new AiAttempt
        {
            Id = attempt, StartedAt = At.AddMinutes(3), ReservedCost = 0.10m, Role = CouncilRoles.Operations
        }, [taken.Id]).Admitted);
        Assert.True(new AiAttemptStore(db).End(attempt, 0, At.AddMinutes(4), null, null, null, null, null, null, null, null, null));

        var owed = events.OwnerWordsOwed();
        Assert.Equal(new[] { CouncilRoles.Operations, CouncilRoles.Research }, owed.Select(w => w.Addressee));
        Assert.Equal(new[] { "what is the chair doing today", "research, look at the funding skew" }, owed.Select(w => w.Text));
        Assert.Equal(new[] { At, At.AddMinutes(1) }, owed.Select(w => w.ReceivedAt));
        Assert.Equal(new string?[] { null, why }, owed.Select(w => w.BlockedWhy));
        Assert.Equal(events.OwnerWordsOpen(CouncilRoles.Operations).Concat(events.OwnerWordsOpen(CouncilRoles.Research)).Select(e => e.Id)
            .Order(StringComparer.Ordinal), owed.Select(w => w.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(owed, w => w.Id == taken.Id);
    }
}
