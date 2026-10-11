namespace TradeAgent.Security;

/// <summary>
/// THE PROCESS ON THE OTHER END OF ONE CONNECTION, as the kernel named it (<c>U-grant-tree</c>): its pid, its start in
/// the kernel's own unit (<see cref="Core.ProcessClock"/>) — with the pid, its identity, since a pid handed on comes back
/// with a different start — and when it was read. <see cref="Source"/> says which question the kernel answered, so a
/// refusal and a test can say whether Linux's pidfd or its older credential carried it.
/// </summary>
public sealed record PeerFacts(int Pid, ulong Start, DateTimeOffset ReadAt)
{
    public string Source { get; init; } = "";
}

/// <summary>What a container answered about one peer.</summary>
public enum Membership
{
    /// <summary>Proved inside the container the app made for the attempt.</summary>
    Member,

    /// <summary>Proved NOT inside it.</summary>
    Outside,

    /// <summary>Could not be proved either way — a lookup failed, the container is not held yet, or it has ended. A refusal.</summary>
    Unknown
}

/// <summary>A container's answer and, when it is not <see cref="Membership.Member"/>, why — in words a refusal can carry.</summary>
public readonly record struct MembershipVerdict(Membership Is, string Why)
{
    public static MembershipVerdict Member { get; } = new(Membership.Member, "");

    public static MembershipVerdict Outside(string why) => new(Membership.Outside, why);

    public static MembershipVerdict Unknown(string why) => new(Membership.Unknown, why);
}

/// <summary>
/// THE CONTAINER THE APP MADE FOR ONE ATTEMPT, asked whether a peer is inside it (<c>U-grant-tree</c>,
/// <c>docs/ORCHESTRATION.md</c> § 2.3).
///
/// <para>A launch grant is a token, and a token is only as private as the environment it travels in: a process running
/// as the same user can read another's environment, and the trade command is a program anyone on the machine can start.
/// So the grant is bound to the CONTAINER the app proved it holds — on Windows the job the launch was assigned to, on
/// macOS and Linux the session the resident launcher made and the parent links below it — and a hello presenting the
/// token is served only to a peer inside it. A container answers <see cref="Membership.Unknown"/> whenever it cannot
/// prove the answer, and every caller reads Unknown as a refusal: the kernel's silence is never a pass.</para>
///
/// <para>What it is NOT, said where the type is: a process of the same user that ptraces a member, is handed a member's
/// connection, or duplicates the job handle out of the app is inside by every test this type can make. Only a cell stops
/// those (§ 2.8); until then the ORDER-001 overlap rule in <see cref="AgentGrants.Issue"/> is what keeps order permission
/// from standing beside another unconfined attempt.</para>
/// </summary>
public abstract class AttemptContainer
{
    /// <summary>What this container is, in the owner's words: "the Windows job TradeAgent made for this attempt".</summary>
    public abstract string Name { get; }

    /// <summary>Whether <paramref name="peer"/> is inside. Never throws: a lookup that fails is <see cref="Membership.Unknown"/>.</summary>
    public abstract MembershipVerdict Contains(PeerFacts peer);
}
