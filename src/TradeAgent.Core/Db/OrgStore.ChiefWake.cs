using System.Globalization;

namespace TradeAgent.Core.Db;

/// <summary>
/// WHAT THE APP KNOWS ABOUT ITSELF THAT THE WAKE-ONCE PRESS IS REFUSED ON (<c>U-chief-wake-once</c>), read by the owner's own app at each
/// press — never by an agent, never from a file: whether the AI is started (an agent prepared), whether it works on its own (the loop is
/// running, not paused), whether this host records a current proven executor cell, and the chief's ceiling as the board's policy states
/// it now (what wakes it next, said on the card).
/// </summary>
public sealed record ChiefWakeFacts(bool AgentStarted, bool WorksOnItsOwn, bool ProvenCell, TimeSpan Ceiling);

/// <summary>
/// WHAT THE FIRST PRESS OF "Wake the chief once" SHOWED: the chief, its seat, and the sentence the second press carries out — or why it
/// cannot be pressed (<see cref="Refusal"/>, in words). The second press hands this back, and is refused as moved when the press would now
/// do anything else (<see cref="WhatMoved"/>).
/// </summary>
public sealed record ChiefWakePreview(string? Chief, string? Seat, string Words, string? Refusal)
{
    /// <summary>What wakes the chief after the press's one turn, and how he stops it (<see cref="OrgStore.ChiefWakeAfter"/>) — said on the card.</summary>
    public string After { get; init; } = "";

    /// <summary>
    /// NULL WHEN <paramref name="now"/> IS THE PRESS THIS PREVIEW SHOWED — the same chief, the same seat, the same sentence — or the words
    /// of what moved. A preview that was itself a refusal never completes a press.
    /// </summary>
    public string? WhatMoved(ChiefWakePreview now)
    {
        if (Refusal is not null) return "the press you armed showed a refusal, so it wakes nothing";
        if (now.Chief != Chief)
            return $"{now.Chief ?? "no chief"} is the chief now, not {Chief ?? "no chief"}: the press was armed for a chief who no longer sits";
        if (now.Seat != Seat) return $"the chief's seat is {now.Seat ?? "none"} now, not {Seat ?? "none"} as your first press showed";
        if (now.Words != Words) return "what the press would do changed after your first press";
        return null;
    }
}

/// <summary>What the wake-once press wrote: the chief, the one wake raised for it, and the <c>woken-once</c> event's id.</summary>
public sealed record ChiefWoken(string Chief, string WakeId, long EventId);

/// <summary>
/// THE OWNER WAKES AN ACTIVE CHIEF ONCE (<c>U-chief-wake-once</c>): one press — two, the first only showing what the second does, as every
/// paid start in this app — gives the chief exactly ONE wake now, so its first turn runs while he watches instead of at its ceiling a day
/// later. IN-PROCESS ONLY (AUTH-001): no pipe op, door verb, <c>trade</c> verb, worker tool or board act reaches it (a guard reads their
/// sources); the owner's word on paid spend is his whether or not a board seat is active. It changes neither the ceiling interval nor any
/// value the board holds: it writes one <c>woken-once</c> event decided by the owner and raises one <see cref="MissionEventKind.OwnerWake"/>,
/// and every launch it buys still passes the activation gate and admission.
/// </summary>
public sealed partial class OrgStore
{
    /// <summary>
    /// PRESS ONE: what "Wake the chief once" would do now, or why it cannot — refused unless a chief sits and is ACTIVE, this host records a
    /// proven executor cell, the AI is started and works on its own, and no wake he pressed before is still waiting. Reads only.
    /// </summary>
    public ChiefWakePreview PreviewChiefWake(ChiefWakeFacts facts) => db.Read(_ => ChiefWakeNow(facts));

    /// <summary>
    /// PRESS TWO, handed what press one showed (<paramref name="asShown"/>, required): in ONE write, the press is decided again — a refusal
    /// now, or a press that would now do anything other than <paramref name="asShown"/> said, is refused in words with nothing written —
    /// then one <c>woken-once</c> event (decider <see cref="OrgDecider.Owner"/>; the position, its seat and the wake in app-composed detail)
    /// and one <see cref="MissionEventKind.OwnerWake"/> for the chief, due <paramref name="now"/>, its payload the event's id.
    /// </summary>
    /// <exception cref="TradeAgentException">Refused; nothing was written.</exception>
    public ChiefWoken WakeChiefOnce(ChiefWakePreview asShown, ChiefWakeFacts facts, DateTimeOffset now) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(asShown);
        var press = ChiefWakeNow(facts);
        if (press.Refusal is { } refused) throw Refused(refused);
        if (asShown.WhatMoved(press) is { } moved) throw Refused($"Nothing was woken: {moved}.");
        var chief = press.Chief!;
        var row = Position(chief)!;
        var wake = MissionEventIds.OwnerWake(chief, now);
        var eventId = Event(now, row.UnitId, chief, OrgEventKind.WokenOnce, OrgDecider.Owner,
            $"position={chief} seat={press.Seat} wake={wake}");
        if (!new MissionEventStore(db).Raise(wake, MissionEventKind.OwnerWake, now,
                payload: eventId.ToString(CultureInfo.InvariantCulture), role: chief))
            throw Refused($"a wake of {chief} at this instant is already on the queue, so no second one was raised.");
        return new ChiefWoken(chief, wake, eventId);
    });

    /// <summary>The press as it stands now, read inside the caller's read or write.</summary>
    ChiefWakePreview ChiefWakeNow(ChiefWakeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var after = ChiefWakeAfter(facts.Ceiling);
        ChiefWakePreview Refuse(string? who, string? on, string why) => new(who, on, why, why) { After = after };
        if (Chief() is not { } chief || Position(chief) is not { } row)
            return Refuse(null, null, "No chief is appointed, so there is none to wake. Appoint a chief and activate it first.");
        var seat = row.SeatRuntime is { } runtime ? $"{runtime} · {row.SeatModel ?? "its runtime's default model"}" : "no seat";
        if (row.Status != OrgPositionStatus.Active)
            return Refuse(chief, seat, $"The chief {chief} is {row.Status}: only an active chief is woken. Activate it first.");
        if (!facts.ProvenCell)
            return Refuse(chief, seat, $"This host has no proven executor cell recorded, so the chief {chief} is not woken: no position "
                                       + "beyond the legacy pair runs on a real model before one is (ORCHESTRATION § 2.3).");
        if (!facts.AgentStarted)
            return Refuse(chief, seat, $"The AI is not started, so the chief {chief} is not woken. Press \"Start the AI\" on the Dashboard first.");
        if (!facts.WorksOnItsOwn)
            return Refuse(chief, seat, $"The AI is paused, so the chief {chief} is not woken: its turn is taken only while the AI works on its "
                                       + "own. Press \"Let the AI work on its own\" on the Dashboard first.");
        if (new MissionEventStore(db).HasUnconsumed(MissionEventKind.OwnerWake, chief))
            return Refuse(chief, seat, $"The wake you pressed for the chief {chief} has not been taken yet: one press is one wake, so nothing "
                                       + "more is raised until its turn has run.");
        return new ChiefWakePreview(chief, seat,
            $"Wake the chief {chief} once, now: one turn on {seat}, which spends your AI plan's capacity. {after}",
            null) { After = after };
    }

    /// <summary>
    /// WHAT WAKES THE CHIEF AFTER ITS ONE TURN, AND HOW HE STOPS IT — said on the card beside the press and in its armed sentence:
    /// nothing more is started by the press; its own request in <c>next.json</c>, or its ceiling as the board's policy states it now.
    /// </summary>
    public static string ChiefWakeAfter(TimeSpan ceiling) =>
        "Nothing else is started by this press. After the turn the chief wakes again only when it asks to in its next.json, or "
        + (ceiling > TimeSpan.Zero
            ? $"at its ceiling, {ceiling.TotalHours.ToString("0.##", CultureInfo.InvariantCulture)} h after its last turn"
            : "never by the clock: the security board's policy holds no ceiling")
        + ". To stop it: \"Pause the AI\" on the Dashboard, or \"Remove the chief\" here, which gives Operations — and your chat — back.";
}
