using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.Security;

/// <summary>What <see cref="AgentGrants.Verify"/> concluded about a token a peer presented.</summary>
public enum GrantState
{
    /// <summary>No token was presented. The caller is authenticated but is nobody in particular.</summary>
    Absent,

    /// <summary>A token was presented and this app never issued it, or has forgotten it.</summary>
    Unknown,

    /// <summary>A token this app issued, whose turn is over.</summary>
    Expired,

    /// <summary>A live grant. <see cref="GrantVerdict.Grant"/> says whose.</summary>
    Valid
}

public sealed record GrantVerdict(GrantState State, AgentGrant? Grant, string Reason);

/// <summary>
/// ONE LAUNCH'S IDENTITY ON THE PIPE: which council role is calling, under which attempt, and until
/// when.
///
/// The token is a secret the app minted for exactly one process, handed over in that process's
/// environment and nowhere else. It is not an authorisation — the gateway's modes, limits, approvals
/// and kill switch are unchanged and unreachable — it is an ANSWER to "who is this", which the pipe
/// previously could not give: every caller presented the same machine token, so Research and the
/// chair were the same caller to every rule downstream.
///
/// Disposing it is the turn ending: the expiry is pulled in to now plus a short grace rather than
/// being deleted outright, because a <c>trade</c> the agent started a moment before its turn ended is
/// still that turn's work and hanging up on it mid-frame would look to the agent like a failed order.
/// </summary>
public sealed class AgentGrant : IDisposable
{
    readonly AgentGrants _owner;

    internal AgentGrant(AgentGrants owner, string token, string role, string attemptId, DateTimeOffset expires)
    {
        _owner = owner;
        Token = token;
        Role = role;
        AttemptId = attemptId;
        Expires = expires;
    }

    public string Token { get; }
    public string Role { get; }
    public string AttemptId { get; }
    public DateTimeOffset Expires { get; internal set; }

    /// <summary>Ends the turn: the grant lives only for the grace after this.</summary>
    public void Dispose() => _owner.EndTurn(this);
}

/// <summary>
/// The grants this process has issued, in memory and nowhere else.
///
/// In memory deliberately: a grant is worth exactly one launch of one process this app started, and
/// a restart has no launches in flight. Writing them down would create a file whose value is a
/// credential, beside the one credential TradeAgent already keeps, for no gain at all.
/// </summary>
public sealed class AgentGrants
{
    /// <summary>The environment variable the token travels in, and the only way it travels.</summary>
    public const string Variable = "TRADEAGENT_GRANT";

    /// <summary>
    /// The longest a grant can live if nobody ever says the turn ended. Not a turn budget — the
    /// budget is <c>TurnMeter</c>'s — just a bound on a token whose owner crashed.
    /// </summary>
    public static readonly TimeSpan MaxTurn = TimeSpan.FromHours(6);

    /// <summary>How long a grant outlives its turn, so a call already in flight is still answered.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The process-wide register, for the same reason <see cref="Core.AgentPresence.Shared"/> is one:
    /// the half that issues grants and the half that checks them are different objects in different
    /// assemblies, and a check against a register nobody issued into refuses every real caller.
    /// </summary>
    public static AgentGrants Shared { get; } = new();

    readonly Lock _gate = new();
    readonly List<AgentGrant> _live = [];
    readonly Func<DateTimeOffset> _now;

    public AgentGrants(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>How many grants are live right now. Diagnostics only.</summary>
    public int Live { get { lock (_gate) { Prune(); return _live.Count; } } }

    /// <summary>
    /// Mints one launch's grant.
    ///
    /// <paramref name="attemptId"/> is the attempt the meter opened for THIS launch, so that the
    /// question "which attempt placed this order" has an answer that does not depend on an agent
    /// telling the truth about itself.
    /// </summary>
    public AgentGrant Issue(string role, string attemptId, TimeSpan? life = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var span = life is { } l && l > TimeSpan.Zero && l < MaxTurn ? l : MaxTurn;
        var grant = new AgentGrant(this, token, role, attemptId ?? "", _now() + span);
        lock (_gate)
        {
            Prune();
            _live.Add(grant);
        }
        return grant;
    }

    /// <summary>Takes a grant out of the register outright. The turn is over and nothing is in flight.</summary>
    public void Revoke(string? token)
    {
        if (token is null) return;
        lock (_gate) _live.RemoveAll(g => g.Token == token);
    }

    internal void EndTurn(AgentGrant grant)
    {
        lock (_gate)
        {
            var until = _now() + Grace;
            if (grant.Expires > until) grant.Expires = until;
            Prune();
        }
    }

    /// <summary>
    /// Who is calling, from the token they presented.
    ///
    /// The comparison is constant-time against every live grant rather than a dictionary lookup:
    /// the list is two entries long in this product, and a credential compared by <c>==</c> is the
    /// habit this codebase does not want anywhere near a credential.
    /// </summary>
    public GrantVerdict Verify(string? token, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new GrantVerdict(GrantState.Absent, null, "no launch grant was presented");

        var at = now ?? _now();
        AgentGrant? found = null;
        lock (_gate)
        {
            foreach (var g in _live)
                if (Same(g.Token, token!)) { found = g; break; }
        }

        if (found is null)
            return new GrantVerdict(GrantState.Unknown, null,
                "the launch grant presented was not issued by this TradeAgent, or its turn is long over");

        if (found.Expires <= at)
            return new GrantVerdict(GrantState.Expired, null,
                $"the launch grant presented expired at {found.Expires:u}");

        return new GrantVerdict(GrantState.Valid, found, "");
    }

    static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    void Prune()
    {
        var cutoff = _now();
        _live.RemoveAll(g => g.Expires <= cutoff);
    }
}
