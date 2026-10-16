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

    /// <summary>
    /// A token this app issued whose launch it has not yet bound to the container it made for it (<c>U-grant-tree</c>):
    /// minted before the process exists, and served to nobody until the app has proved it holds that process.
    /// </summary>
    Pending,

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

    internal AgentGrant(AgentGrants owner, string token, string role, string attemptId, DateTimeOffset expires,
        string? ordersWithheld = null)
    {
        _owner = owner;
        Token = token;
        Role = role;
        AttemptId = attemptId;
        Expires = expires;
        OrdersWithheld = ordersWithheld;
    }

    /// <summary>
    /// WHETHER THIS LAUNCH MAY PLACE, CHANGE OR CANCEL ORDERS (ORDER-001, <c>U-grant-tree</c>): its role may — only
    /// <c>operations</c> — AND no other launch's process grant was live when it was issued. Decided once, atomically, in
    /// <see cref="AgentGrants.Issue"/>, and never changed: a launch that started beside an unconfined neighbour stays one.
    /// </summary>
    public bool MayPlaceOrders => CouncilRoles.MayPlaceOrders(Role) && OrdersWithheld is null;

    /// <summary>
    /// Why this launch holds no order permission although its role has it, in words the refusal and the turn's own message
    /// carry — or null when it holds it, or its role never had it.
    /// </summary>
    public string? OrdersWithheld { get; }

    /// <summary>
    /// Set when the turn ends: a grant in its grace is no longer a running neighbour (<see cref="AgentGrants.Issue"/>). Read by
    /// the broker door (<c>U-thinking-seats-b</c>), whose only client is the launch's own process, which has no call left to
    /// finish once its turn is over — so it serves no request in the grace the pipe keeps for a <c>trade</c> started late.
    /// </summary>
    public bool TurnOver { get; internal set; }

    /// <summary>
    /// Set once <see cref="AgentGrants.Ended"/> has been raised for this grant's expiry while its turn still ran: the grant
    /// is refused for service from then on and stays in the register as a neighbour until the turn ends.
    /// </summary>
    internal bool ExpiryTold { get; set; }

    public string Token { get; }
    public string Role { get; }
    public string AttemptId { get; internal set; }
    public DateTimeOffset Expires { get; internal set; }

    /// <summary>
    /// THE CONTAINER THE APP PROVED IT HOLDS FOR THIS ATTEMPT, or null while the grant is PENDING (<c>U-grant-tree</c>).
    /// Set once, by <see cref="AgentGrants.Bind"/>, and never changed: a grant is worth one launch of one container.
    /// </summary>
    public AttemptContainer? Container { get; internal set; }

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

    /// <summary>
    /// How long a grant outlives its turn, and WHAT THAT BUYS — which is narrower than "the grant
    /// still works for a minute".
    ///
    /// <para>It covers a call ALREADY IN FLIGHT: a frame the gateway is inside right now is answered
    /// rather than cut off, because hanging up mid-frame would look to the agent like a failed order
    /// when the order may already be at the broker. And it covers a <c>trade</c> the agent STARTED a
    /// moment before its turn ended, which is still that turn's work and may still say hello.</para>
    ///
    /// <para>It does NOT cover a further frame arriving on a connection whose turn is over: the
    /// gateway re-verifies the grant on every frame and <see cref="Ended"/> closes the connections
    /// that grant authenticated, so the caller cannot even send one (REVIEW 2026-09-16 finding 5).
    /// A grace that let a socket keep trading for a minute after the turn ended would be a minute of
    /// unattributable orders, which is the opposite of what a launch grant is for.</para>
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A GRANT HAS STOPPED BEING LIVE, and the token it was. Raised for every way a grant can end —
    /// revoked, its turn ended, or simply expired and pruned — because a register the rest of the
    /// program has to POLL is a register whose endings arrive whenever someone happens to ask.
    ///
    /// <para>It exists so that an ending reaches the SOCKET. Revocation that changes a list while the
    /// revoked caller keeps trading down a connection it opened earlier is not revocation, and that
    /// is what the pipe server subscribes to this for. A subscriber gets the token and nothing else:
    /// this type knows about processes and turns, not about transports.</para>
    ///
    /// <para>Raised OUTSIDE <see cref="_gate"/>, deliberately: a subscriber closing a pipe is doing
    /// I/O, and doing it under the lock that every <see cref="Verify"/> on every frame contends for
    /// would put the register's throughput at the mercy of a peer that stopped reading. It may be
    /// raised more than once for one token — a turn that ends is one ending and the expiry that
    /// follows it a minute later is another — so a subscriber must be idempotent.</para>
    /// </summary>
    public event Action<string>? Ended;

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
    public int Live
    {
        get
        {
            List<string>? gone;
            int count;
            lock (_gate) { gone = Prune(); count = _live.Count; }
            RaiseEnded(gone);
            return count;
        }
    }

    /// <summary>
    /// Mints one launch's grant.
    ///
    /// <paramref name="attemptId"/> is the attempt the meter opened for THIS launch, so that the
    /// question "which attempt placed this order" has an answer that does not depend on an agent
    /// telling the truth about itself.
    /// </summary>
    ///
    /// <para><b>ORDER PERMISSION IS DECIDED HERE, ATOMICALLY, AND THIS IS THE HARD LINE OF ORDER-001's OVERLAP RULE</b>
    /// (<c>U-grant-tree</c>, R23 P-01). A launch that may trade must not stand beside another attempt's process that
    /// nothing confines: a research turn running as the same user can read the chair's environment, and only a cell stops
    /// that (§ 2.8). So, under the register's lock: while a live, order-capable grant's turn runs, no other launch is
    /// issued at all — <see cref="ErrorCode.LAUNCH_HELD"/> is thrown before any process exists; and an <c>operations</c>
    /// launch issued while any other launch's grant is live — pending, bound, whatever its role — gets a grant WITHOUT order
    /// permission and the reason in <see cref="AgentGrant.OrdersWithheld"/>. A grant whose turn has ended is no neighbour:
    /// its tree was ended before the turn returned. The mission loop asks <see cref="HeldBy"/> first and defers rather than
    /// meeting this line; this line is what holds when two launches race past that look — and the loop and the owner's
    /// chat call it BEFORE the ledger is asked (fix pass 1), so a launch it refuses has written no row and consumed no wake.</para>
    /// </summary>
    public AgentGrant Issue(string role, string attemptId, TimeSpan? life = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var span = life is { } l && l > TimeSpan.Zero && l < MaxTurn ? l : MaxTurn;
        AgentGrant? grant = null;
        string? held;
        List<string>? gone;
        lock (_gate)
        {
            gone = Prune();
            held = Held(role);
            if (held is null)
            {
                grant = new AgentGrant(this, token, role, attemptId ?? "", _now() + span, Withheld(role));
                _live.Add(grant);
            }
        }
        RaiseEnded(gone);
        if (grant?.OrdersWithheld is not null && Withholding is { } told)
            try { told(grant); } catch (Exception) { /* a line about a launch is never worth the launch */ }
        return grant ?? throw new TradeAgentException(ErrorCode.LAUNCH_HELD, held);
    }

    /// <summary>
    /// THE LOOK BEFORE THE LAUNCH: the sentence <see cref="Issue"/> would refuse a launch of <paramref name="role"/> with
    /// right now, or null when it would issue one. The mission loop asks this and defers — no admission row, wakes kept —
    /// so the hard line in <see cref="Issue"/> is met only by a race.
    /// </summary>
    public string? HeldBy(string role)
    {
        List<string>? gone;
        string? held;
        lock (_gate) { gone = Prune(); held = Held(role); }
        RaiseEnded(gone);
        return held;
    }

    /// <summary>
    /// STAMPS THE ATTEMPT ON A GRANT ISSUED BEFORE ITS ADMISSION (<c>U-grant-tree</c> fix pass 1): a launch's place is taken
    /// before the ledger opens its attempt, so the attempt is written onto the grant once it exists — while the grant is
    /// still pending and serves nobody, never after it is bound.
    /// </summary>
    public void Attribute(AgentGrant grant, string attemptId)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
            if (grant.Container is null) grant.AttemptId = attemptId ?? "";
    }

    /// <summary>
    /// A LAUNCH WAS ISSUED WITHOUT THE ORDER PERMISSION ITS ROLE HAS (ORDER-001), raised outside the lock with the grant,
    /// so the app can write the activity line. A subscriber that throws is ignored.
    /// </summary>
    public event Action<AgentGrant>? Withholding;

    /// <summary>Under the lock: why a launch of <paramref name="role"/> may not start now, or null.</summary>
    string? Held(string role)
    {
        foreach (var g in _live)
            if (!g.TurnOver && g.MayPlaceOrders)
                return $"the {CouncilRoles.Title(g.Role)}'s launch (attempt {Shown(g.AttemptId)}) holds permission to " +
                       $"trade, and no other AI launch starts while it runs, so this launch of the {CouncilRoles.Title(role)} " +
                       "waits until that turn ends";
        return null;
    }

    /// <summary>Under the lock: why a launch of <paramref name="role"/> would hold no order permission now, or null.</summary>
    string? Withheld(string role)
    {
        if (!CouncilRoles.MayPlaceOrders(role)) return null;
        foreach (var g in _live)
            if (!g.TurnOver)
                return $"this launch of the {CouncilRoles.Title(role)} started while the {CouncilRoles.Title(g.Role)}'s " +
                       $"launch (attempt {Shown(g.AttemptId)}) was running beside it, so it holds no permission to trade " +
                       "this turn: it may read and answer, and may not place, change or cancel an order";
        return null;
    }

    /// <summary>
    /// BINDS A GRANT TO THE CONTAINER THE APP PROVED IT HOLDS FOR ITS ATTEMPT (<c>U-grant-tree</c>). False when the
    /// register holds no live grant by that token, or the grant is already bound — once, and never to a second container.
    /// </summary>
    public bool Bind(string? token, AttemptContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (token is null) return false;
        lock (_gate)
        {
            foreach (var g in _live)
            {
                if (!Same(g.Token, token)) continue;
                if (g.Container is not null || g.Expires <= _now()) return false;
                g.Container = container;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Takes a grant out of the register outright. The turn is over and nothing is in flight.
    ///
    /// <para><see cref="Ended"/> is raised for the token whether or not this register held it, so that
    /// an owner revoking twice, or revoking a token another register issued, still reaches every
    /// connection holding it. A revocation is the one operation that must not depend on the revoker
    /// having guessed the right register.</para>
    /// </summary>
    public void Revoke(string? token)
    {
        if (token is null) return;
        lock (_gate) _live.RemoveAll(g => g.Token == token);
        RaiseEnded([token]);
    }

    internal void EndTurn(AgentGrant grant)
    {
        List<string>? gone;
        lock (_gate)
        {
            var until = _now() + Grace;
            if (grant.Expires > until) grant.Expires = until;
            grant.TurnOver = true;
            gone = Prune();
        }

        // THE TURN IS OVER NOW, not when the grace runs out. The grant stays in the register for the
        // grace so that a `trade` started a moment ago can still say hello (see Grace) — but the
        // connections this launch is ALREADY holding have had their turn, and an ending that does not
        // reach them leaves the socket trading on a turn that is finished.
        RaiseEnded([grant.Token]);
        RaiseEnded(gone);
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

        // PENDING IS A REFUSAL (U-grant-tree). The grant is minted before its process exists and put in that process's
        // environment; until the app has bound it to the container it proved it holds, there is nothing a peer could be
        // inside, so nothing may present it.
        if (found.Container is null)
            return new GrantVerdict(GrantState.Pending, null,
                $"the launch grant presented (attempt {Shown(found.AttemptId)}) is not yet bound to the container " +
                "TradeAgent made for its attempt, so nothing may present it");

        return new GrantVerdict(GrantState.Valid, found, "");
    }

    /// <summary>
    /// Are these the same grant token? Exposed because the pipe server has to answer it too — which
    /// connection is holding the grant that just ended — and a credential compared by <c>==</c> is
    /// the habit this codebase does not want anywhere near a credential, in its own assembly or in
    /// anyone else's.
    /// </summary>
    public static bool SameToken(string? a, string? b) =>
        a is not null && b is not null && Same(a, b);

    static string Shown(string attemptId) => attemptId.Length > 0 ? attemptId : "none";

    static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// Drops every grant whose expiry has passed and whose turn is over, and RETURNS the tokens that
    /// stopped being served, so the caller can announce those endings once it is out of the lock.
    /// Null rather than an empty list because pruning nothing is the ordinary case and it should
    /// allocate nothing.
    ///
    /// <para><b>AN EXPIRED GRANT WHOSE TURN STILL RUNS STAYS, AS A NEIGHBOUR</b> (ORDER-001, <c>U-grant-tree</c> fix
    /// pass 1). Expiry refuses the token for service — <see cref="Verify"/> answers Expired and the ending is announced
    /// once, so its connections close — but it says nothing about the process: a turn hung past
    /// <see cref="MaxTurn"/> is still running, unconfined, and dropping it here would let an Operations launch issued
    /// beside it hold order permission. It leaves the register only once its turn has returned, which is after its tree
    /// was ended (a tree the teardown could not end is held by the meter's survivors).</para>
    /// </summary>
    List<string>? Prune()
    {
        var cutoff = _now();
        List<string>? gone = null;
        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var g = _live[i];
            if (g.Expires > cutoff) continue;
            if (!g.TurnOver)
            {
                if (g.ExpiryTold) continue;
                g.ExpiryTold = true;
                (gone ??= []).Add(g.Token);
                continue;
            }
            if (!g.ExpiryTold) (gone ??= []).Add(g.Token);
            _live.RemoveAt(i);
        }
        return gone;
    }

    void RaiseEnded(IReadOnlyList<string>? tokens)
    {
        if (tokens is null || Ended is not { } handlers) return;
        foreach (var token in tokens)
        {
            // ONE SUBSCRIBER THAT THROWS DOES NOT KEEP THE ENDING FROM THE OTHERS, and does not
            // fault the turn that ended. There is no useful answer to "closing a socket threw"
            // beyond not letting it stop the next one.
            foreach (var handler in handlers.GetInvocationList())
                try { ((Action<string>)handler)(token); } catch (Exception) { }
        }
    }
}
