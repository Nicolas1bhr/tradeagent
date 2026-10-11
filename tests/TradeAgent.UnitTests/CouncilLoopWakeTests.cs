using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// EACH ROLE IS WOKEN FOR WHAT IT OWNS (<c>U-reconcile-wakes</c>): every wake names its role and says
/// truthfully what it is, a role with no open work is not looked at, and a renewal is bought only for a
/// role its allowance stopped.
/// </summary>
public partial class CouncilLoopTests
{
    /// <summary>
    /// (d) A NOTE WAKE IS NAMED AND QUOTED. The paper allocation and the forward-run notes are TradeAgent's
    /// own words to Research about its versions; a turn that read "Why you are awake: note" and nothing
    /// else had to spend a tool call opening the file the app already held.
    ///
    /// <para>RED at base 604a3133: the reason line read "note" and the note's text was not in the prompt.</para>
    /// </summary>
    [Fact]
    public async Task A_note_wake_is_named_and_quoted()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency());
        var now = Noon();
        var loop = new MissionLoop(host, NoHeartbeat, now: () => now);

        const string content = "Forward run of version v-1 on BTCUSDT, day 2026-10-10: 3 trades, the run continues.";
        new PublicationStore(db).Commit(new Publication
        {
            Id = Publication.IdOf("allocator", PublicationKind.Note, content),
            Role = "allocator",
            Kind = PublicationKind.Note,
            Recipients = CouncilRoles.Research,
            Classification = PublicationClass.Council,
            CreatedAt = now,
            Content = content
        }, now, MissionEventIds.PaperRun("deployment-1", "2026-10-10"));

        await loop.TurnAsync();

        var (role, prompt) = Assert.Single(host.Opened);
        Assert.Equal(CouncilRoles.Research, role);
        Assert.Contains("- Why you are awake: TradeAgent's forward-run note arrived in `in/`" + Environment.NewLine,
            prompt, StringComparison.Ordinal);
        Assert.Contains("> " + content, prompt, StringComparison.Ordinal);
        Assert.Contains("A note TradeAgent wrote itself", prompt, StringComparison.Ordinal);
    }

    // ---- item 2: a role is looked at only while it owns open work ---------------------------------

    /// <summary>
    /// Sleeps the loop through a quiet span as the app does — to the earliest wake, every turn due there taken —
    /// and answers the launches it made, as (role, prompt).
    /// </summary>
    static async Task<List<(string Role, string Prompt)>> Quiet(CouncilHost host, MissionOptions options,
        DateTimeOffset from, TimeSpan span)
    {
        var events = host.Events!;
        var now = from;
        var loop = new MissionLoop(host, options, now: () => now);
        var before = host.Opened.Count;

        await loop.TurnAsync();      // the first pass schedules; nothing is due yet
        var end = from + span;
        for (var step = 0; step < 400 && events.NextDueAt() is { } next && next <= end; step++)
        {
            now = next;
            for (var i = 0; i < 10 && events.RolesDue(now).Count > 0; i++) await loop.TurnAsync();
        }
        return host.Opened.Skip(before).ToList();
    }

    static readonly MissionOptions HalfHourLooks = new() { ReviewEvery = TimeSpan.FromMinutes(30) };

    static DateTimeOffset LocalMidnight() => new(DateTime.Today, DateTimeOffset.Now.Offset);

    /// <summary>
    /// (a) AN IDLE DAY COSTS ONLY THE LOOKS AN OPEN OBJECTIVE ASKS FOR. Twenty-four hours in which nothing happens,
    /// from local midnight to the next: the chair owns nothing open — no message, no request, an empty book, no
    /// boundary — and is not looked at; Research's standing mandate keeps its look at the pace <c>U-quiet-review</c>
    /// gave it; and neither role was stopped by its allowance, so no renewal is bought.
    ///
    /// <para>RED at base 604a3133's schedule (a look and a renewal per role whatever it owns), with this host saying
    /// what each role owns: "18 paid turns in 24 quiet hours — Operations 9, Research 9, renewals 2".</para>
    /// </summary>
    [Fact]
    public async Task An_idle_day_costs_only_the_looks_an_open_objective_asks_for()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };

        var opened = await Quiet(host, HalfHourLooks, LocalMidnight(), TimeSpan.FromHours(24));

        var operations = opened.Count(o => o.Role == CouncilRoles.Operations);
        var research = opened.Count(o => o.Role == CouncilRoles.Research);
        var renewals = opened.Count(o => o.Prompt.Contains("the day turned over", StringComparison.Ordinal));
        var said = $"{opened.Count} paid turns in 24 quiet hours — Operations {operations}, Research {research}, "
                   + $"renewals {renewals}";

        Assert.True(operations == 0, said);
        Assert.True(research is >= 1 and <= 8, said);
        Assert.True(renewals == 0, said);
        Assert.Empty(host.Events!.OfKind(MissionEventKind.Renewal));
        Assert.All(opened, o => Assert.Contains(
            "- Why you are awake: a scheduled look; nothing else has happened" + Environment.NewLine, o.Prompt));
    }

    /// <summary>
    /// (b) A RENEWAL WAKES ONLY A ROLE ITS ALLOWANCE STOPPED. The renewal exists to tell a role that stopped at its
    /// share of the day that it may work again; a role nothing stopped is not paid a turn at midnight to be told so.
    ///
    /// <para>RED at base 604a3133: both roles had a renewal raised, the one with room to work included.</para>
    /// </summary>
    [Fact]
    public async Task A_renewal_wakes_only_a_role_its_allowance_stopped()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var host = new CouncilHost(db, root, new Concurrency()) { Owns = true };
        // RESEARCH HAS SPENT ITS HALF OF A 2.00 DAY; the chair has room.
        host.Spending[CouncilRoles.Research] = Reading(CouncilRoles.Research, spent: 1m, cap: 2m, share: 0.5m);
        Assert.False(host.SpendFor(CouncilRoles.Research).AdmitsAnotherTurn);
        Assert.True(host.SpendFor(CouncilRoles.Operations).AdmitsAnotherTurn);

        var now = Noon();
        await new MissionLoop(host, NoHeartbeat, now: () => now).TurnAsync();

        var renewal = Assert.Single(host.Events!.OfKind(MissionEventKind.Renewal));
        Assert.Equal(CouncilRoles.Research, renewal.For);
        Assert.True(renewal.DueAt > now, "the renewal was raised already due");
        Assert.Empty(host.Opened);
    }

    /// <summary>
    /// (e) AN OPEN POSITION KEEPS OPERATIONS' LOOK, AND NO BROKER IS CALLED TO KNOW IT. The book is the chair's to
    /// watch; the host hands over the positions its last Situation already read, and the loop decides from that.
    /// The same four quiet hours with an empty book look at the chair not once.
    ///
    /// <para>RED with the open-position instance dropped (the mutant): the chair holding a position was never
    /// looked at.</para>
    /// </summary>
    [Fact]
    public async Task An_open_position_keeps_operations_look_without_a_broker_call()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var holding = new CouncilHost(db, root, new Concurrency()) { Owns = true, Book = ["ES +1"] };
        var held = await Quiet(holding, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.True(held.Count(o => o.Role == CouncilRoles.Operations) >= 1,
            $"the chair holding ES +1 was looked at {held.Count(o => o.Role == CouncilRoles.Operations)} times in four quiet hours");

        var (db2, root2) = Workspace();
        using var _2 = db2;
        var flat = new CouncilHost(db2, root2, new Concurrency()) { Owns = true, Book = [] };
        var none = await Quiet(flat, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.DoesNotContain(none, o => o.Role == CouncilRoles.Operations);

        // AN UNREAD BOOK IS NOT AN EMPTY ONE: a host with no reading keeps the chair's look.
        var (db3, root3) = Workspace();
        using var _3 = db3;
        var unread = new CouncilHost(db3, root3, new Concurrency()) { Owns = true, Book = null };
        var blind = await Quiet(unread, HalfHourLooks, Noon(), TimeSpan.FromHours(4));
        Assert.Contains(blind, o => o.Role == CouncilRoles.Operations);

        // AND THE APP'S HOST ANSWERS FROM ITS LAST READ, never by asking the platform: the member is one
        // synchronous line over the ledgers and the book the Situation stored.
        var source = File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "AppHost.cs"));
        var member = source[source.IndexOf("public IReadOnlyList<RoleObjective>? Objectives(string role)", StringComparison.Ordinal)..];
        member = member[..member.IndexOf(';')];
        Assert.Contains("RoleObjectives.Open(db, role, Volatile.Read(ref host._lastBook))", member, StringComparison.Ordinal);
        Assert.DoesNotContain("Async", member, StringComparison.Ordinal);
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
