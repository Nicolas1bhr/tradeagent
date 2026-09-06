namespace TradeAgent.Core;

/// <summary>Who put the file there. The distinction the ledger exists to preserve.</summary>
public enum MaterialOrigin
{
    /// <summary>
    /// The account owner handed it over, through the inbox, and the scanner can attest it: no agent
    /// process was alive at any point between the previous pass and this sighting. This is the only
    /// origin that states something the filesystem alone cannot show, and it is never assumed.
    /// </summary>
    Inbox,

    /// <summary>
    /// It is in the inbox, and nobody can prove who put it there — an agent process was alive
    /// somewhere in the window between the previous pass and this sighting
    /// (REVIEW 2026-09-05b finding 5).
    ///
    /// It is a real sighting with a real hash; what is missing is the attribution. Kept as a
    /// separate word rather than folded into <see cref="Inbox"/> because a measurement table that
    /// says "the owner gave me this" when it means "it was in their folder" is exactly the record
    /// the observed party can author.
    /// </summary>
    InboxUnattested,

    /// <summary>It appeared under the agent's own working directories, so the agent produced it.</summary>
    Agent
}

/// <summary>
/// One file version TradeAgent saw on disk. An observation, never a claim — every field here was
/// read from the filesystem, not reported by the agent.
/// </summary>
public sealed record Material(
    long Id,
    string RelPath,
    MaterialOrigin Origin,
    string? Sha256,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RemovedAt,
    bool Runnable)
{
    public bool Present => RemovedAt is null;
    public string Name => RelPath.Split('/')[^1];

    /// <summary>Short form for the UI and the CLI. Enough to match a row against a note by eye.</summary>
    public string ShortSha => Sha256 is null ? "(not hashed yet)" : Sha256[..12];
}

public enum MaterialNoteKind
{
    /// <summary>Read it, referred to it, worked from it.</summary>
    Used,
    /// <summary>Executed it. The one worth being able to search for later.</summary>
    Ran,
    /// <summary>Produced one file from another. <c>ParentSha</c> says from what.</summary>
    Derived,
    /// <summary>Anything else worth writing down.</summary>
    Note
}

/// <summary>
/// Something somebody said about a file. A <b>claim</b>, and stored apart from
/// <see cref="Material"/> for that reason: the agent writes these, and an agent's account of what
/// it did is evidence, not fact. Read them next to the observations, never merged into them.
/// </summary>
public sealed record MaterialNote(
    long Id,
    DateTimeOffset At,
    string Author,
    string? Session,
    MaterialNoteKind Kind,
    string? SubjectSha,
    string? ParentSha,
    string Text);

/// <summary>What one scan pass did. Small enough to log on every pass without becoming noise.</summary>
public sealed record ScanResult(int Seen, int Added, int Hashed, int Removed, int Skipped, bool HashBudgetSpent)
{
    public bool Changed => Added > 0 || Removed > 0 || Hashed > 0;
    public override string ToString() =>
        $"seen={Seen} added={Added} hashed={Hashed} removed={Removed} skipped={Skipped}" +
        (HashBudgetSpent ? " (hash budget spent — more next pass)" : "");
}
