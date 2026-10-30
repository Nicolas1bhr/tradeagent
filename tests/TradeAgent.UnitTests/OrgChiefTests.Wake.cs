using Avalonia.Controls;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// "WAKE THE CHIEF ONCE" (<c>U-chief-wake-once</c>; AUTH-001): the owner's in-process press that gives an ACTIVE chief exactly one wake now,
/// two presses bound to what the first showed. Every chief here is a puppet; nothing reaches a model, and the app's facts are the test's.
/// </summary>
public partial class OrgChiefTests
{
    static ChiefWakeFacts Facts(bool started = true, bool own = true, bool proven = true) =>
        new(started, own, proven, TimeSpan.FromHours(24));

    /// <summary>An active puppet chief, appointed and activated through the owner's own route.</summary>
    static string ActiveChief(OrgStore org)
    {
        var chief = org.AppointChief(Puppet, Known, Now).Chief!;
        org.Activate(chief, _ => SeatClasses.Puppet, 3, Now, () => false);
        return chief;
    }

    static long WakeRows(Database db) => Count(db, "mission_event", $"kind='{MissionEventKind.OwnerWake}'");

    /// <summary>Every row a press could write, as one comparable value.</summary>
    static string State(Database db) => $"{string.Join(",", Written(db))}|{WakeRows(db)}";

    static string Refusal(Func<object> press)
    {
        try { press(); }
        catch (TradeAgentException ex) { return ex.Message; }
        throw new Xunit.Sdk.XunitException("the press was not refused");
    }

    /// <summary>
    /// (w1) THE PRESS IS REFUSED IN WORDS UNLESS EVERY CONDITION HOLDS — a chief sits, it is ACTIVE, this host records a proven cell, the AI
    /// is started and works on its own, and no wake he pressed is still untaken — each refusal a sentence naming what to do, and press two
    /// over a refusing state writes nothing: no org event, no wake. RED at base: no such press (the store had no <c>WakeChiefOnce</c>).
    /// </summary>
    [Fact]
    public void Waking_the_chief_once_is_refused_in_words_unless_every_condition_holds()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var at = DateTimeOffset.UtcNow;

        Assert.Contains("No chief is appointed", org.PreviewChiefWake(Facts()).Refusal, StringComparison.Ordinal);
        var chief = org.AppointChief(Puppet, Known, Now).Chief!;
        Assert.Contains($"The chief {chief} is dormant", org.PreviewChiefWake(Facts()).Refusal, StringComparison.Ordinal);
        org.Activate(chief, _ => SeatClasses.Puppet, 3, Now, () => false);

        (ChiefWakeFacts Facts, string Words)[] refused =
        [
            (Facts(proven: false), "no proven executor cell recorded"),
            (Facts(started: false, own: false), "Press \"Start the AI\" on the Dashboard first"),
            (Facts(own: false), "Press \"Let the AI work on its own\" on the Dashboard first"),
        ];
        var shown = org.PreviewChiefWake(Facts());
        Assert.Null(shown.Refusal);
        foreach (var (facts, words) in refused)
        {
            var preview = org.PreviewChiefWake(facts);
            Assert.Contains(words, preview.Refusal, StringComparison.Ordinal);
            var before = State(db);
            // PRESS TWO OVER A STATE THAT NOW REFUSES, armed while it allowed: refused with the same words, nothing written.
            Assert.Contains(words, Refusal(() => org.WakeChiefOnce(shown, facts, at)), StringComparison.Ordinal);
            Assert.Equal(before, State(db));
        }

        // ONE WAKE AT A TIME: once a pressed wake waits untaken, a further press is refused in words.
        org.WakeChiefOnce(shown, Facts(), at);
        Assert.Contains("has not been taken yet", org.PreviewChiefWake(Facts()).Refusal, StringComparison.Ordinal);
        Assert.Contains("has not been taken yet", Refusal(() => org.WakeChiefOnce(shown, Facts(), at.AddSeconds(1))), StringComparison.Ordinal);
        Assert.Equal(1, WakeRows(db));
    }

    /// <summary>
    /// (w2) ONE PRESS PAIR IS ONE WAKE AND ONE OWNER EVENT, AND MOVES NOTHING THE BOARD HOLDS: the armed preview names the chief, its seat,
    /// that it spends his plan, what wakes it next (its next.json or its ceiling) and how to stop it; press two writes one
    /// <c>woken-once</c> event decided by the owner naming the position, and one <c>owner-wake</c> for the chief, due now, carrying that event's
    /// id — and no board policy, no ceiling wake and no other row of the chart.
    /// </summary>
    [Fact]
    public void One_press_pair_is_one_wake_and_one_owner_event_and_moves_nothing_the_board_holds()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var chief = ActiveChief(org);
        var at = DateTimeOffset.UtcNow;
        var shown = org.PreviewChiefWake(Facts());
        foreach (var said in new[] { chief, "puppet · scripted-1", "plan or your API key", "next.json", "ceiling, 24 h", "Pause the AI", "Remove the chief" })
            Assert.Contains(said, shown.Words, StringComparison.Ordinal);

        var policies = Count(db, "board_policy");
        var units = Count(db, "org_unit");
        var positions = Count(db, "org_position");
        var woken = org.WakeChiefOnce(shown, Facts(), at);

        Assert.Equal(1, Count(db, "org_event",
            $"kind='{OrgEventKind.WokenOnce}' AND decider='{OrgDecider.Owner}' AND position_id='{chief}' AND detail LIKE 'position={chief} %'"));
        var wake = new MissionEventStore(db).Get(woken.WakeId)!;
        Assert.Equal((MissionEventKind.OwnerWake, chief, woken.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            (wake.Kind, wake.Role, wake.Payload, wake.Consumed));
        Assert.True(wake.DueAt <= at);
        Assert.Equal(1, WakeRows(db));
        Assert.Equal(0, Count(db, "mission_event", $"kind='{MissionEventKind.Ceiling}'"));
        Assert.Equal((policies, units, positions), (Count(db, "board_policy"), Count(db, "org_unit"), Count(db, "org_position")));
    }

    /// <summary>
    /// (w3) PRESS TWO IS ABOUT WHAT PRESS ONE SHOWED: armed for chief A, the chief replaced by B (active) before press two — the press is
    /// refused as moved, naming both, nothing written; armed with a refusal, it wakes nothing either. WATCHED MUTANT: the store's
    /// <c>WhatMoved</c> check removed → B is woken on a press armed for A.
    /// </summary>
    [Fact]
    public void Press_two_is_refused_as_moved_when_the_chief_changed_after_press_one()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var a = ActiveChief(org);
        var shown = org.PreviewChiefWake(Facts());
        var b = org.ReplaceChief(a, Puppet, Known, Now).Chief!;
        org.Activate(b, _ => SeatClasses.Puppet, 3, Now, () => false);

        var before = State(db);
        Assert.Contains($"{b} is the chief now, not {a}", Refusal(() => org.WakeChiefOnce(shown, Facts(), DateTimeOffset.UtcNow)),
            StringComparison.Ordinal);
        Assert.Equal(before, State(db));

        // WHO THE TURN BILLS IS PART OF WHAT PRESS ONE SHOWED (the Codex review): armed on the plan, a seat that bills a key by press two
        // is a press that moved — the armed sentence promised plan capacity, never real money.
        var onPlan = org.PreviewChiefWake(Facts() with { Billing = BillingClasses.Plan });
        Assert.Contains("your AI plan's capacity", onPlan.Words, StringComparison.Ordinal);
        Assert.Contains("real money", org.PreviewChiefWake(Facts() with { Billing = BillingClasses.Key }).Words, StringComparison.Ordinal);
        Assert.Contains("what the press would do changed",
            Refusal(() => org.WakeChiefOnce(onPlan, Facts() with { Billing = BillingClasses.Key }, DateTimeOffset.UtcNow)), StringComparison.Ordinal);
        Assert.Equal(before, State(db));

        var refusing = org.PreviewChiefWake(Facts(own: false));
        Assert.Contains("showed a refusal", Refusal(() => org.WakeChiefOnce(refusing, Facts(), DateTimeOffset.UtcNow)), StringComparison.Ordinal);
        Assert.Equal(before, State(db));
    }

    /// <summary>
    /// (w4) ON THE CHIEF'S CARD: "Wake the chief once" is drawn only while an ACTIVE chief sits, beside the words of what it does, what wakes
    /// the chief next and how to stop it; a press while the AI is paused arms nothing and says why ON THE CARD; with the AI working, press one
    /// arms with the preview's words and writes nothing, press two raises the one wake and says so.
    /// </summary>
    [Fact]
    public void The_wake_press_on_the_chiefs_card_takes_two_presses_and_says_its_refusals()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var facts = Facts(own: false);
        var basic = Sources(db);
        var page = new OrganisationPage(basic with
        {
            Chief = basic.Chief! with
            {
                PreviewWake = () => new OrgStore(db).PreviewChiefWake(facts),
                WakeOnce = shown => Said(() => $"woke {new OrgStore(db).WakeChiefOnce(shown, facts, DateTimeOffset.UtcNow).Chief}")
            }
        });
        var card = Card(page);
        Assert.False(card.WakeOnce.IsVisible);

        var chief = org.AppointChief(Puppet, Known, Now).Chief!;
        page.Update();
        Assert.False(card.WakeOnce.IsVisible);
        org.Activate(chief, _ => SeatClasses.Puppet, 3, Now, () => false);
        page.Update();
        Assert.True(card.WakeOnce.IsVisible && card.WakeWhat.IsVisible);
        foreach (var said in new[] { "next.json", "ceiling", "Pause the AI", "Remove the chief" })
            Assert.Contains(said, card.WakeWhat.Text, StringComparison.Ordinal);

        // PAUSED: either press arms nothing, and the refusal is said on the card.
        var before = State(db);
        Press(card.WakeOnce);
        Assert.False(Ui.IsArmed(card.WakeOnce));
        Assert.Contains("Let the AI work on its own", card.Done.Text, StringComparison.Ordinal);
        Assert.Equal(before, State(db));

        // WORKING: press one arms with the preview's words and writes nothing; press two wakes once.
        facts = Facts();
        Press(card.WakeOnce);
        Assert.True(Ui.IsArmed(card.WakeOnce));
        Assert.Contains($"Wake the chief {chief} once", card.WakeOnce.Content as string, StringComparison.Ordinal);
        Assert.Equal(before, State(db));
        Press(card.WakeOnce);
        Assert.Equal($"woke {chief}", card.Done.Text);
        Assert.Equal(1, WakeRows(db));

        // ONE WAKE AT A TIME: while the pressed wake waits untaken, a further press arms nothing and says why on the card.
        Press(card.WakeOnce);
        Assert.False(Ui.IsArmed(card.WakeOnce));
        Assert.Contains("has not been taken yet", card.Done.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// (w5) NO OP OR VERB REACHES THE WAKE-ONCE PRESS (AUTH-001): no source of the pipe server, the broker door, the worker's tools, the
    /// schema or the CLI names the store's press, the host's route or the kind it raises, and no op is named for it; the store's press is
    /// named by the host's route alone (<c>AppHost.cs</c>), and the kind is raised by that store file alone — the wake table and the loop's
    /// phrase name it, and nothing else does.
    /// </summary>
    [Fact]
    public void No_op_or_verb_reaches_the_wake_once_press()
    {
        string[] seams = ["WakeChiefOnce", "PreviewChiefWake", "OwnerWake", "owner-wake", "woken-once", "WokenOnce"];
        var src = Src();
        var agentFacing = Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Gateway"), "GatewayPipeServer*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "BrokerDoor*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.AgentRuntime"), "WorkerTools*.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.TradeCli"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, "TradeAgent.Core", "Board"), "*.cs"))
            .Concat([Path.Combine(src, "TradeAgent.Gateway", "GatewaySchema.cs")])
            .Where(f => !Built(f)).ToList();
        Assert.Contains(agentFacing, f => f.EndsWith("BrokerDoor.cs", StringComparison.Ordinal));
        var hits = agentFacing.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(l => seams.Any(s => l.line.Contains(s, StringComparison.Ordinal)))
            .Select(l => $"{Path.GetFileName(l.f)}:{l.i + 1}: {l.line.Trim()}")
            .ToList();
        foreach (var h in hits) log.WriteLine(h);
        Assert.Empty(hits);
        // NO OP IS NAMED FOR IT: `org-wake` (U-org-autonomy) is a position asking for ITS OWN next wake, a `self` row behind its verb caller —
        // never the owner's wake of the chief, which the source scan above holds no agent-facing file names.
        Assert.DoesNotContain(GatewaySchema.Ops(), op => op.Op.Contains("chief", StringComparison.OrdinalIgnoreCase)
                                                          || op.Op.Contains("once", StringComparison.OrdinalIgnoreCase)
                                                          || op.Op.Contains("owner", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["TradeAgent.App/AppHost.cs", "TradeAgent.Core/Db/OrgStore.ChiefWake.cs"],
            Naming(".WakeChiefOnce").Concat(Naming(" WakeChiefOnce")).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["TradeAgent.AgentRuntime/MissionLoop.cs", "TradeAgent.Core/Db/MissionEventStore.cs", "TradeAgent.Core/Db/OrgStore.ChiefWake.cs",
                      "TradeAgent.Core/Db/OrgStore.cs"],
            [.. Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !Built(f))
                .Where(f => File.ReadAllText(f).Contains("MissionEventKind.OwnerWake", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/')).Order(StringComparer.Ordinal)]);
    }
}
