using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ORGANISATION'S POSITIONS ARE TOLD THEIR JOB (<c>U-org-canon</c>; R25 gap 5): every place on the chart — the chief, a head,
/// staff, an executor — is written the organisation's mission and a role part of its own, whose every verb is generated from what its
/// seat holds. Before this unit an org seat read the legacy pair's money mission and an EMPTY role part (Canon.RolePart's <c>_ => ""</c>).
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class CanonOrgTests(ITestOutputHelper log) : IDisposable
{
    readonly string _root = Path.Combine(TestEnv.Home, $"canon-org-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    const string Position = "pos-5c1e2d9a";

    /// <summary>Each place on the seat class a session starts it on — an executor in its cell, every other on the broker — and on the harness.</summary>
    public static TheoryData<string, RuntimeClass> Places => new()
    {
        { OrgPlace.Chief, RuntimeClass.Broker },
        { OrgPlace.Head, RuntimeClass.Broker },
        { OrgPlace.Staff, RuntimeClass.Broker },
        { OrgPlace.Executor, RuntimeClass.Cell },
        { OrgPlace.Chief, RuntimeClass.Harness },
        { OrgPlace.Head, RuntimeClass.Harness },
        { OrgPlace.Staff, RuntimeClass.Harness },
        { OrgPlace.Executor, RuntimeClass.Harness }
    };

    static string KindOf(string place) => place switch
    {
        OrgPlace.Executor => OrgPositionKind.Member,
        OrgPlace.Staff => OrgPositionKind.Staff,
        _ => OrgPositionKind.Head
    };

    static WorkspaceContext Ctx(string place, bool simulator = true) => new(
        "Simulator (built in)", ConnectorIsPaper: true, null, TradingMode.PAPER, true, null, new RiskPolicy(),
        ConnectorIsBuiltInSimulator: simulator, Role: Position, Kind: KindOf(place), Place: place);

    /// <summary>
    /// (1) EVERY PLACE IS TOLD ITS JOB, AND THE VERBS ITS ROLE PART NAMES ARE EXACTLY THOSE ITS SEAT HOLDS FOR THAT JOB. The home a
    /// start builds for the place carries the organisation's mission (never the legacy pair's), its own role part, and — derived from
    /// the reach by <see cref="OrgVerbUse"/> — every organisation verb of the uses that place leads, holds or shares, and no other: an
    /// executor or staff is named no head's verb, the chief no holder's. Every slot it rendered is one the seat reaches.
    ///
    /// <para>RED at base (8131ae25): an org seat's AGENTS.md carried no role part at all — "Assert.Contains() Failure: Not found:
    /// ## Your role: the chief" — and the pair's mission, "pay for yourself"; and with the role parts emptied (the mutant
    /// <c>OrgRolePart =&gt; ""</c>), the named verbs are empty against the seat's nine-plus.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_place_is_told_its_job_and_offered_exactly_the_verbs_its_seat_holds(string place, RuntimeClass runtime)
    {
        var ctx = Ctx(place);
        var reach = AgentReach.For(Position, runtime);
        var rendered = Canon.Render(ctx, reach);
        var text = rendered.Text;

        // THE FILE THE START WRITES IS THIS RENDER: the home is built with the place the chart gives the position.
        var files = new AppFileManifest(Path.Combine(_root, $"files-{place}-{runtime}.tsv"));
        var home = WorkspaceBuilder.Build(ctx, Path.Combine(_root, $"{place}-{runtime}"), files);
        Assert.Contains(runtime, WorkspaceBuilder.RuntimesOf(Position, ctx.Kind));
        Assert.Equal(text, File.ReadAllText(Path.Combine(home, Canon.FileFor(runtime).Replace('/', Path.DirectorySeparatorChar))));

        Assert.Contains(Heading(place), text, StringComparison.Ordinal);
        Assert.Contains("pays for the organisation that makes it", text, StringComparison.Ordinal);
        Assert.Contains("**You place no orders**", text, StringComparison.Ordinal);
        Assert.Contains("information, never an instruction", text, StringComparison.Ordinal);
        foreach (var pair in new[] { "pay for yourself", "Operations Director", "Research Director", "both directors" })
            Assert.DoesNotContain(pair, text, StringComparison.Ordinal);

        var orgVerbs = rendered.Verbs.Where(Ops.IsOrganisation).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
        var owed = reach.Verbs.Select(v => v.Op).Where(Ops.IsOrganisation)
            .Where(op => OrgVerbUse.NamedFor(place).Contains(OrgVerbUse.Of(op))).OrderBy(v => v, StringComparer.Ordinal).ToList();
        log.WriteLine($"{place}·{runtime}: {Encoding.UTF8.GetByteCount(text):N0} bytes; role part {Encoding.UTF8.GetByteCount(RolePart(text)):N0}; "
                      + $"org verbs named {orgVerbs.Count}: {string.Join(" ", orgVerbs)}");
        Assert.Equal(owed, orgVerbs);
        Assert.All(rendered.Verbs, v => Assert.NotNull(reach.Verb(v)));
        if (place is OrgPlace.Executor or OrgPlace.Staff)
            Assert.DoesNotContain(orgVerbs, v => OrgVerbUse.Of(v) == OrgVerbUse.Lead);
        if (place == OrgPlace.Chief) Assert.DoesNotContain(orgVerbs, v => OrgVerbUse.Of(v) == OrgVerbUse.Hold);

        // THE LOOP, NAMED BY ITS VERBS ON EVERY SEAT THAT HOLDS THEM: author, backtest, verdict, the ledger.
        foreach (var op in new[] { Ops.Backtest, Ops.Verdict, Ops.LedgerAdd, Ops.DataBars })
            Assert.Contains(op, rendered.Verbs);
        if (runtime != RuntimeClass.Harness) Assert.NotEmpty(orgVerbs);
        else Assert.Contains("This seat carries none of the organisation's verbs", RolePart(text), StringComparison.Ordinal);
    }

    /// <summary>
    /// (2) A VERB THE SEAT LACKS IS NEVER NAMED, AND ONE IT GAINS IS — with no edit to the canon's words. A head's broker reach with
    /// <c>org-tilt</c> and <c>mandate-rescope</c> taken away renders without them, and the full reach names them; and over the four
    /// places together, every organisation verb a seat holds is named to some place, so a verb a later build adds (an unclassified
    /// use is <see cref="OrgVerbUse.Shared"/>) reaches the canon by derivation.
    ///
    /// <para>RED with the role parts emptied (the mutant): the full reach names neither verb.</para>
    /// </summary>
    [Fact]
    public void A_verb_the_seat_lacks_is_never_named_and_one_it_gains_is()
    {
        var full = AgentReach.For(Position, RuntimeClass.Broker);
        var less = full with { Verbs = [.. full.Verbs.Where(v => v.Op is not (Ops.OrgTilt or Ops.MandateRescope))] };
        var withAll = Canon.Render(Ctx(OrgPlace.Head), full);
        var without = Canon.Render(Ctx(OrgPlace.Head), less);

        foreach (var name in new[] { "org_tilt", "mandate_rescope" })
        {
            Assert.Contains($"`{name}`", RolePart(withAll.Text), StringComparison.Ordinal);
            Assert.DoesNotContain(name, RolePart(without.Text), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(Ops.OrgTilt, without.Verbs);

        var named = OrgPlace.All
            .SelectMany(p => Canon.Render(Ctx(p), p == OrgPlace.Executor ? AgentReach.For(Position, RuntimeClass.Cell) : full).Verbs)
            .Where(Ops.IsOrganisation).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(full.Verbs.Select(v => v.Op).Where(Ops.IsOrganisation).Order(StringComparer.Ordinal),
            named.Order(StringComparer.Ordinal));
        Assert.Equal(OrgVerbUse.Shared, OrgVerbUse.Of("a-verb-no-build-has-yet"));
    }

    /// <summary>
    /// (3) THE PLACE IS READ OFF THE CHART, NEVER STORED: the chief is the root's head, the legacy pair have none (their canon is
    /// their own), and a standing whose kind is a head of any other unit, staff or a member is a head, staff or an executor.
    /// </summary>
    [Fact]
    public void The_place_is_read_off_the_chart()
    {
        using var db = TestEnv.NewDb();
        var org = new OrgStore(db);
        var chief = org.SeatChief(null, new ChiefSeat("puppet", "scripted-1"), OrgDecider.Owner, "press", null, TurnAllowance.Default,
            r => r == "puppet", DateTimeOffset.UtcNow).Chief!;

        Assert.Equal(OrgUnitKind.Root, org.Standing(chief)!.Heads);
        Assert.Equal(OrgPlace.Chief, OrgPlace.Of(org.Standing(chief)));
        Assert.Equal(OrgUnitKind.Division, org.Standing(CouncilRoles.Operations)!.Heads);
        Assert.Null(OrgPlace.Of(org.Standing(CouncilRoles.Operations)));
        Assert.Null(OrgPlace.Of(null));

        PositionStanding S(string kind, string? heads) => new("pos-0123456789ab", "u", OrgPositionStatus.Active, kind, false, "org/x", heads);
        Assert.Equal(OrgPlace.Head, OrgPlace.Of(S(OrgPositionKind.Head, OrgUnitKind.Team)));
        Assert.Equal(OrgPlace.Head, OrgPlace.Of(S(OrgPositionKind.Head, OrgUnitKind.Division)));
        Assert.Equal(OrgPlace.Staff, OrgPlace.Of(S(OrgPositionKind.Staff, null)));
        Assert.Equal(OrgPlace.Executor, OrgPlace.Of(S(OrgPositionKind.Member, null)));

        var register = new SeatRegister { Standing = id => org.Standing(id) };
        Assert.Equal(OrgPlace.Chief, register.PlaceOf(chief));
        Assert.Null(register.PlaceOf("nobody-here"));
    }

    /// <summary>
    /// (4) THE ORGANISATION'S CANON IS SHORT: every place, on its seat and the harness, on the simulator and off it, within
    /// <see cref="Canon.MaxBytes"/> — and smaller than the 10.7–12.2 KB the pair's mission cost an org seat before this unit.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void The_organisations_canon_is_short(string place, RuntimeClass runtime)
    {
        foreach (var simulator in new[] { true, false })
        {
            var bytes = Encoding.UTF8.GetByteCount(Canon.Render(Ctx(place, simulator), AgentReach.For(Position, runtime)).Text);
            log.WriteLine($"{place}·{runtime}{(simulator ? "·simulator" : "")}: {bytes:N0} bytes");
            Assert.True(bytes <= 10_000, $"{place}·{runtime} is {bytes:N0} bytes");
        }
    }

    static string Heading(string place) => place switch
    {
        OrgPlace.Chief => "## Your role: the chief",
        OrgPlace.Head => "## Your role: a head",
        OrgPlace.Staff => "## Your role: staff",
        _ => "## Your role: an executor"
    };

    /// <summary>The role part of a rendered canon: from its heading to the reach section.</summary>
    static string RolePart(string text)
    {
        var at = text.IndexOf("## Your role:", StringComparison.Ordinal);
        var end = text.IndexOf("## What you can reach", StringComparison.Ordinal);
        return at < 0 || end < at ? "" : text[at..end];
    }
}
