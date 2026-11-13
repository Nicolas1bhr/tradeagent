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
    Agent,

    /// <summary>
    /// TradeAgent itself put it there — the strategy language reference and the worked programs it
    /// writes into every role's home, and every report or brief the relay delivers into <c>in/</c>.
    ///
    /// It is a MEASUREMENT and not a label: the scanner says this only when the path AND the sha256
    /// both match what the app wrote down in <see cref="AppFileManifest"/> at the moment it wrote
    /// the file. A role's edit of one changes the bytes, so the next sighting is
    /// <see cref="Agent"/> — which is the point. Nothing the agent does can move a file INTO this
    /// word: it cannot write the manifest, and a path the manifest never named is what it always
    /// was.
    ///
    /// Last in this enum deliberately: the values are stored as their own names, and a word inserted
    /// above another one would renumber it for everything that reads the order rather than the text.
    /// </summary>
    App
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
    /// <summary>
    /// <see cref="Added"/>, BY THE WORD EACH NEW ROW WAS WRITTEN WITH — counted by the scanner as it
    /// answered the store, so a row and its count cannot carry different origins. An origin that added
    /// nothing in this pass is absent.
    /// </summary>
    public IReadOnlyDictionary<MaterialOrigin, int> AddedBy { get; init; } = new Dictionary<MaterialOrigin, int>();

    /// <summary>
    /// WHAT ARRIVED: the additions no role authored — the owner's <c>inbox/</c>, attested
    /// (<see cref="MaterialOrigin.Inbox"/>) or not (<see cref="MaterialOrigin.InboxUnattested"/>, the
    /// word a drop gets when an agent was alive anywhere in its window — a file dropped while a turn
    /// is running, for one). The one count the inbox wake reads.
    ///
    /// <para><b>Never <see cref="MaterialOrigin.Agent"/>.</b> A role's own write is something that role
    /// already knows: waking it for its rewritten plan and journal told the next turn "new material
    /// arrived in <c>../inbox</c>" when nothing had, eight turns in eight minutes in the observed run of
    /// 2026-10-01. <b>Never <see cref="MaterialOrigin.App"/>.</b> The app's own files are not news, and a
    /// relay delivery wakes its recipient through its own <c>task:</c> event, written in the same
    /// transaction as the publication. Both are still recorded, under the word they always had: this
    /// narrows what buys a turn, not what the ledger sees.</para>
    /// </summary>
    public int Arrived =>
        AddedBy.GetValueOrDefault(MaterialOrigin.Inbox) + AddedBy.GetValueOrDefault(MaterialOrigin.InboxUnattested);

    /// <summary>
    /// HOW MANY FOLDERS THIS PASS COULD NOT READ IN FULL (U-inbox-unreadable): a tracked folder the disk
    /// would not say is there, a folder it would not list, or one holding a listed file it would not
    /// describe. Never a folder skipped on purpose — that is <see cref="Skipped"/>.
    ///
    /// <para><b>Any at all and the pass is not complete:</b> it moved neither window key, marked nothing
    /// missing in the group the folder is in, and recorded what it did see with the words its older,
    /// wider window allows. "I could not look" is neither "it is gone" nor "I looked".</para>
    /// </summary>
    public int Unreadable { get; init; }

    /// <summary>
    /// Those folders, relative to the workspace — the owner's drop folder's first — at most
    /// <see cref="MaterialScanner.UnreadableNamed"/> of them; <see cref="Unreadable"/> counts them all.
    /// Names only, never anything inside a file.
    /// </summary>
    public IReadOnlyList<string> UnreadableFolders { get; init; } = [];

    /// <summary>
    /// HOW MANY FILES THIS PASS LEFT UNHASHED BECAUSE ITS BYTES OR ITS WALL TIME FOR HASHING WAS SPENT (<c>U-cards-2</c>): each is
    /// tried again next pass, and each is a row that stays present with no hash — never a file dropped from the ledger. Not
    /// <see cref="HashBudgetSpent"/>, which is the walk's file budget despite its name.
    /// </summary>
    public int HashDeferred { get; init; }

    /// <summary>The deferrals in words — the file, its size, why — at most <see cref="MaterialScanner.DeferralsNamed"/> of them, then one line for the rows its batch did not reach.</summary>
    public IReadOnlyList<string> HashDeferrals { get; init; } = [];

    /// <summary>
    /// A HASH READ OF THIS WORKSPACE DID NOT COME BACK FROM THE DISK (<c>U-perf-scan</c>): this pass's or an earlier one's, still waiting.
    /// Its deferrals are a disk that stopped answering, not files too large for a pass, and the owner is told that reason.
    /// </summary>
    public bool HashStalled { get; init; }

    /// <summary>
    /// A READ OF THE WALK DID NOT COME BACK FROM THE DISK (<c>U-material-pass-2</c>) — a listing, the stat of a file or the app-origin
    /// measure, this pass's or one an earlier pass left waiting. The pass looked nowhere past it: it recorded nothing of that place, marked
    /// nothing missing there and moved no window, and let go of its exclusion so the AI's work goes on while the disk does not answer.
    /// </summary>
    public bool WalkStalled { get; init; }

    /// <summary>
    /// WHERE THE FILE BUDGET RAN OUT (<c>U-material-pass-2</c>; U-perf-scan's card 2), when it did: the tracked folder, relative to the
    /// workspace, the walk was in. Null on a pass that did not run out. A pass cut there records no removals anywhere, and the owner is
    /// told once (AppHost's cut line).
    /// </summary>
    public string? CutIn { get; init; }

    /// <summary>The file budget this pass ran under (<see cref="MaterialScanner.FileLimit"/>), for the owner's line about a cut pass.</summary>
    public int FileLimit { get; init; }

    /// <summary>
    /// The pass looked everywhere: it did not run out of budget (<see cref="HashBudgetSpent"/>, which is
    /// the file budget's flag despite its name) and read every place it walked. Only a complete pass
    /// moves the window, and only a complete pass can say that a folder it once could not read is
    /// readable again — one that stopped early may never have reached it.
    /// </summary>
    public bool Complete => !HashBudgetSpent && Unreadable == 0 && !WalkStalled;

    public bool Changed => Added > 0 || Removed > 0 || Hashed > 0;
    public override string ToString() =>
        $"seen={Seen} added={Added} hashed={Hashed} removed={Removed} skipped={Skipped}" +
        (Unreadable > 0 ? $" unreadable={Unreadable}" : "") +
        (HashDeferred > 0 ? $" hash-deferred={HashDeferred}" : "") +
        (WalkStalled ? " walk-stalled" : "") +
        (HashBudgetSpent ? " (hash budget spent — more next pass)" : "");
}
