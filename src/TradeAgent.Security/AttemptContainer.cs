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

    /// <summary>
    /// The peer's <c>/proc/&lt;pid&gt;/cgroup</c> as it read between the two looks at the pidfd that prove the pid still names the
    /// connected process (<c>U-cell-linux-b</c>, Linux with <c>SO_PEERPIDFD</c> only) — or null when it was not read.
    /// </summary>
    public string? Cgroup { get; init; }

    /// <summary>
    /// How many pids the kernel lists for the peer across pid namespaces (the pidfd's <c>NSpid</c> fields): 1 means the pid this
    /// process sees is the peer's own, more means a translation this check does not make, 0 means it was not read.
    /// </summary>
    public int NsPids { get; init; }
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

/// <summary>
/// THE ATTEMPT'S OWN CGROUP AS A GRANT'S CONTAINER (<c>U-cell-linux-b</c>, Linux): a peer is inside when the cgroup the kernel names
/// for it — read through the pidfd fixed at connect, so a pid handed on is never read — is the attempt's directory or beneath it, by
/// whole path components. Unknown, and so a refusal, whenever that cannot be proved: a peer named without a pidfd, a cgroup file
/// that is not exactly one <c>0::</c> line, a pid seen through another pid namespace, an attempt already ended or its cgroup
/// removed. Never alone: it is ANDed onto the session proof (<see cref="AllOf"/>, RULING 1), never a replacement for it.
/// </summary>
public sealed class CgroupContainer(string attemptDir, Func<bool>? over = null, string mount = Core.AttemptCgroups.Mount) : AttemptContainer
{
    public override string Name => "the cgroup TradeAgent made for this attempt";

    public override MembershipVerdict Contains(PeerFacts peer)
    {
        if (over?.Invoke() == true) return MembershipVerdict.Unknown("the attempt's processes have already been ended");
        if (peer.Source != "SO_PEERPIDFD")
            return MembershipVerdict.Unknown($"the kernel named process {peer.Pid} without a pidfd ({(peer.Source.Length > 0 ? peer.Source : "no source")}), " +
                                             "so which cgroup it connected from cannot be proved");
        if (peer.NsPids != 1)
            return MembershipVerdict.Unknown(peer.NsPids == 0
                ? $"the kernel would not say which pid namespace process {peer.Pid} is in"
                : $"process {peer.Pid} is seen through {peer.NsPids} pid namespaces, a translation TradeAgent does not make");
        if (peer.Cgroup is not { } text)
            return MembershipVerdict.Unknown($"the kernel would not say which cgroup process {peer.Pid} is in");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || !lines[0].StartsWith("0::/", StringComparison.Ordinal))
            return MembershipVerdict.Unknown($"process {peer.Pid}'s cgroup is not one cgroup v2 line");
        var rel = lines[0][3..];
        if (rel.Split('/').Any(s => s == ".."))
            return MembershipVerdict.Unknown($"process {peer.Pid}'s cgroup {rel} lies outside this app's view of the hierarchy");
        if (!Directory.Exists(attemptDir))
            return MembershipVerdict.Unknown("the attempt's cgroup has already been removed");
        var path = rel == "/" ? mount : mount + rel;
        return path == attemptDir || path.StartsWith(attemptDir + "/", StringComparison.Ordinal)
            ? MembershipVerdict.Member
            : MembershipVerdict.Outside($"process {peer.Pid} is in the cgroup {rel}, not the one TradeAgent made for this attempt");
    }
}

/// <summary>
/// EVERY ONE OF SEVERAL PROOFS (<c>U-cell-linux-b</c>, RULING 1): a peer is inside only when each part says Member; the first part that
/// does not answers for all of them. On Linux under a delegated cgroup the grant's container is the session AND the cgroup.
/// </summary>
public sealed class AllOf(params AttemptContainer[] parts) : AttemptContainer
{
    public override string Name => parts.Length == 0 ? "no container" : string.Join(" and ", parts.Select(p => p.Name));

    public override MembershipVerdict Contains(PeerFacts peer)
    {
        if (parts.Length == 0) return MembershipVerdict.Unknown("no container was named");
        foreach (var part in parts)
        {
            var verdict = part.Contains(peer);
            if (verdict.Is != Membership.Member) return verdict;
        }
        return MembershipVerdict.Member;
    }
}
