using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT ON THE TAPE A ROLE OWNS, AND THE ONE WAKE IT BUYS (<c>U-reconcile-packet</c>).
///
/// <para><b>Ownership is the app's record, never a declaration.</b> Research owns two things on the tape: a new
/// exchange announcement — an event, not a periodic reading — and a reading of a series that an ACTIVE deployment of
/// one of its versions declares as a feature input, read back from the version the deployment runs. Nothing an agent
/// writes subscribes it to anything: no op, verb, argument, setting or file names a watch, and a market reading no
/// deployment reads wakes nobody — it arrives every poll, so a wake on it would be a timer by another name.</para>
///
/// <para><b>Never inside a held-back month.</b> An item whose source time is inside any window
/// <see cref="TapeHoldout.Pipe"/> refuses the role's own reads — a dataset with no recorded end held with NO end — is
/// not counted, not named and raises nothing: a wake that said "something happened in August" would be a read of the
/// held month by another door.</para>
///
/// <para><b>Coalesced and textless.</b> At most one untaken tape wake per role
/// (<see cref="MissionEventStore.RaiseCoalesced"/>), keyed by the fetch; its payload is source, series, subject and
/// count (<see cref="TapeWakePayload"/>) — never an item's text, its address or a figure.</para>
/// </summary>
public static class TapeWakes
{
    /// <summary>The role the tape's owned observations wake.</summary>
    public const string Owner = CouncilRoles.Research;

    /// <summary>
    /// RAISES RESEARCH'S TAPE WAKE FOR WHAT <paramref name="arrival"/> STORED, when it owns any of it outside every
    /// holdout window, and answers whether a row went in. False when it owns none, when every owned item is held back,
    /// and when a tape wake is already untaken. Throws when a ledger cannot be read; the caller's notice is then lost
    /// and the next new item raises its own.
    /// </summary>
    public static bool Raise(Database db, TapeArrival arrival, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(arrival);

        var owned = Owned(db, arrival);
        if (owned.Count == 0) return false;

        // THE ROLE'S OWN WINDOWS, read from the dataset ledger now: the reads it would be refused are the reads this
        // wake may not point at.
        var windows = TapeHoldout.Pipe(Owner, new DatasetStore(db)).Windows();
        var outside = owned.Where(i => !windows.Any(w => w.Holds(i.SourceTime))).ToList();
        if (outside.Count == 0) return false;

        var subjects = outside.Select(i => i.Subject).Distinct(StringComparer.Ordinal).ToList();
        var payload = new TapeWakePayload(arrival.Source, arrival.Series, subjects.Count == 1 ? subjects[0] : null,
            outside.Count);

        return new MissionEventStore(db).RaiseCoalesced(new MissionEvent
        {
            Id = MissionEventIds.ForRole(MissionEventIds.Tape(arrival.FetchId), Owner),
            Kind = MissionEventKind.Tape,
            CreatedAt = at,
            DueAt = at,
            Payload = Json.Write(payload),
            Role = Owner
        });
    }

    /// <summary>
    /// THE ITEMS OF <paramref name="arrival"/> RESEARCH OWNS: every one of an announcement row, and of any other row
    /// those whose source, series and subject an active deployment of a Research version declares as a feature input.
    /// </summary>
    static List<TapeArrived> Owned(Database db, TapeArrival arrival)
    {
        if (arrival.Parser == TapeSourceCatalog.AnnouncementParser) return [.. arrival.Items];

        var declared = DeclaredInputs(db);
        return [.. arrival.Items.Where(i => declared.Contains((arrival.Source, arrival.Series, i.Subject)))];
    }

    /// <summary>
    /// EVERY TAPE INPUT AN ACTIVE DEPLOYMENT OF A RESEARCH VERSION DECLARES, read back from the version it runs. A
    /// version that does not parse in this build declares nothing here: the runner ends such a run, and a guess at what
    /// it reads would be a subscription nobody recorded.
    /// </summary>
    static HashSet<(string Source, string Series, string Subject)> DeclaredInputs(Database db)
    {
        var inputs = new HashSet<(string, string, string)>();
        var strategies = new StrategyStore(db);
        foreach (var d in new Deployments(db).Open())
        {
            if (!d.IsActive) continue;
            if (strategies.VersionById(d.VersionId) is not { Role: Owner } version) continue;
            if (StrategyParser.Parse(version.Source).Program is not { } program) continue;
            foreach (var feature in program.Features)
                foreach (var input in feature.Spec.Inputs)
                    inputs.Add((input.Source, input.Series, input.Subject));
        }
        return inputs;
    }
}
