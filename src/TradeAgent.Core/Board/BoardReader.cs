using TradeAgent.Core.Db;

namespace TradeAgent.Core.Board;

/// <summary>One field of the policy in force as the Board page shows it: its path, its value as text, and the version that set it.</summary>
public sealed record BoardPolicyField(string Name, string Value, int SetByVersion);

/// <summary>
/// THE POLICY IN FORCE, AS READ (<c>U-board-a1</c>): the highest version, who decided it and on what basis, when, its body's hash,
/// the typed policy and every field with the version that set it. <see cref="CouldNotBeRead"/> with <see cref="Why"/> when the record
/// did not verify — then <see cref="Policy"/> is <see cref="BoardPolicy.Unreadable"/>, <see cref="Version"/> the highest version stored
/// (0 for none) and the decider, basis and hash empty: nothing here may be read as decided.
/// </summary>
public sealed record BoardPolicyInForce(int Version, string Decider, string Basis, DateTimeOffset WrittenAt, string BodySha256,
    BoardPolicy Policy, IReadOnlyList<BoardPolicyField> Fields)
{
    public bool CouldNotBeRead { get; init; }
    public string? Why { get; init; }
}

/// <summary>
/// ONE VERSION OF THE POLICY IN ITS HISTORY: who decided it, on what basis, when, its hash — and the rationale, the deciding seats,
/// the evidence class and the score of a board act, each null for the founding record, which no seat decided and nothing scored.
/// </summary>
public sealed record BoardPolicyVersion(int Version, string Decider, string Basis, DateTimeOffset WrittenAt, string BodySha256,
    string? Rationale, IReadOnlyList<string> Deciders, string? EvidenceClass, decimal? Score);

/// <summary>
/// THE ONE READ OF THE BOARD THE APP'S PAGES TAKE (<c>U-board-a1</c>; the seam with <c>U-settings-shed</c>): the policy in force, its
/// history and the seats — reads only, over <see cref="BoardStore"/>. Every read is taken fresh from the store.
/// </summary>
public sealed class BoardReader(Database db)
{
    readonly BoardStore _store = new(db);

    /// <summary>The policy in force: the highest version, read strictly, or the unreadable shape with the reason.</summary>
    public BoardPolicyInForce InForce() => _store.InForce();

    /// <summary>Every version, oldest first.</summary>
    public IReadOnlyList<BoardPolicyVersion> History() => _store.History();

    /// <summary>The board's seats, in founding order.</summary>
    public IReadOnlyList<BoardSeat> Seats() => _store.Seats();

    /// <summary>True only while a board seat is ACTIVE — its latest state row says so (<c>board_seat_state</c>). No product path writes one in this build.</summary>
    public bool AnyActive => _store.AnyActive();

    /// <summary>
    /// EVERY ACT OF THE BOARD, NEWEST FIRST, WHOLE (<c>U-board-b1</c>; the seam with <c>U-org-surface-b</c>): each act with its status,
    /// its events and every seat's rationale — shown to the owner always, never to a seat (the blind read is the pipe's) — its forecasts
    /// and their resolutions.
    /// </summary>
    public IReadOnlyList<BoardActRecord> Acts() => _store.Acts();

    /// <summary>One act whole, or null.</summary>
    public BoardActRecord? Act(string id) => _store.Act(id);
}
