using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE CGROUP HALF OF A GRANT'S CONTAINER, AS LOGIC (<c>U-cell-linux-b</c> item 4): what the kernel said about a peer, judged against
/// the attempt's directory under a stand-in mount — every OS. The kernel's own answers are CellLinuxTests' rows on Linux.
/// </summary>
public class CgroupContainerTests : IDisposable
{
    /// <summary>Forward slashes on every OS, as the kernel writes a cgroup path; Windows opens such a path too.</summary>
    readonly string _mount = Path.Combine(Path.GetTempPath(), "ta-cgroup-" + Guid.NewGuid().ToString("n")[..8]).Replace('\\', '/');

    string Attempt(string name)
    {
        var d = _mount + "/svc/" + name;
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        try { Directory.Delete(_mount, recursive: true); } catch (IOException) { }
    }

    static PeerFacts Peer(string? cgroup, string source = "SO_PEERPIDFD", int nspids = 1) =>
        new(4242, 7, DateTimeOffset.UtcNow) { Source = source, Cgroup = cgroup, NsPids = nspids };

    [Fact]
    public void A_peer_in_the_attempts_cgroup_or_beneath_it_is_a_member_and_nowhere_else()
    {
        var a = Attempt("attempt-a");
        Attempt("attempt-ab");
        var inside = new CgroupContainer(a, mount: _mount);

        Assert.Equal(Membership.Member, inside.Contains(Peer("0::/svc/attempt-a\n")).Is);
        Assert.Equal(Membership.Member, inside.Contains(Peer("0::/svc/attempt-a/sub/leaf\n")).Is);
        foreach (var other in new[] { "0::/svc/attempt-ab\n", "0::/svc\n", "0::/svc/attempt-b\n", "0::/\n", "0::/svc/supervisor\n" })
        {
            var v = inside.Contains(Peer(other));
            Assert.True(v.Is == Membership.Outside, $"{other.Trim()} was read as {v.Is}");
            Assert.Contains("not the one TradeAgent made for this attempt", v.Why);
        }
    }

    [Fact]
    public void What_cannot_be_proved_is_unknown_and_never_a_member()
    {
        var a = Attempt("attempt-a");
        var ended = false;
        var inside = new CgroupContainer(a, () => ended, _mount);
        foreach (var (peer, words) in new (PeerFacts, string)[]
        {
            (Peer("0::/svc/attempt-a\n", source: "SO_PEERCRED"), "without a pidfd"),
            (Peer("0::/svc/attempt-a\n", nspids: 2), "2 pid namespaces"),
            (Peer("0::/svc/attempt-a\n", nspids: 0), "pid namespace"),
            (Peer(null), "would not say which cgroup"),
            (Peer("0::/svc/attempt-a\n1:name=systemd:/svc/attempt-a\n"), "not one cgroup v2 line"),
            (Peer("12:pids:/svc/attempt-a\n"), "not one cgroup v2 line"),
            (Peer("0::/svc/attempt-a/../attempt-a\n"), "outside this app's view"),
        })
        {
            var v = inside.Contains(peer);
            Assert.True(v.Is == Membership.Unknown, $"{words}: read as {v.Is}");
            Assert.Contains(words, v.Why);
        }

        ended = true;
        Assert.Equal(Membership.Unknown, inside.Contains(Peer("0::/svc/attempt-a\n")).Is);
        ended = false;
        Directory.Delete(a);
        Assert.Contains("already been removed", inside.Contains(Peer("0::/svc/attempt-a\n")).Why);
    }

    [Fact]
    public void All_of_is_a_member_only_when_every_part_is_and_the_first_refusal_answers()
    {
        var yes = new Fixed(MembershipVerdict.Member);
        var outside = new Fixed(MembershipVerdict.Outside("left the session"));
        var unknown = new Fixed(MembershipVerdict.Unknown("cgroup unreadable"));
        var peer = Peer("0::/x\n");

        Assert.Equal(Membership.Member, new AllOf(yes, yes).Contains(peer).Is);
        Assert.Equal("left the session", new AllOf(outside, unknown).Contains(peer).Why);
        Assert.Equal("cgroup unreadable", new AllOf(yes, unknown).Contains(peer).Why);
        Assert.Equal(Membership.Outside, new AllOf(yes, outside).Contains(peer).Is);
        Assert.Equal(Membership.Unknown, new AllOf().Contains(peer).Is);
    }

    sealed class Fixed(MembershipVerdict verdict) : AttemptContainer
    {
        public override string Name => "a fixed answer";
        public override MembershipVerdict Contains(PeerFacts peer) => verdict;
    }
}
