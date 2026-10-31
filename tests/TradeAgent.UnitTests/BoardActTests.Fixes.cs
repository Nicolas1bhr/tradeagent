using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>THE FIX PASS (<c>U-board-b1</c>, the Codex review's five findings): each finding's settling check, red first.</summary>
public partial class BoardActTests
{
    /// <summary>
    /// A SAME-FAMILY REFUSAL RECORDS THE OTHER-FAMILY SEAT THAT IS ACTIVE (review P2, BoardStore.cs:376): the app's words sealed in a
    /// refusal from the proposer's own family are true — while a seat of another family is active they name it, and never say that no
    /// such seat could decide. RED before the fix: the refusal sealed "no active seat of another family could decide it". Watched red
    /// with the words chosen by the decision instead of by the seats.
    /// </summary>
    [Fact]
    public void A_same_family_refusal_records_the_other_family_seat_that_is_active()
    {
        using var db = TestEnv.NewDb();
        FoundOnAnotherFamily(db);
        Activate(db, "board-1", "board-2", "board-4");
        var store = new BoardStore(db);
        var now = DateTimeOffset.UtcNow;
        var act = store.Propose(Act("board-1", Raise()), now).Act;
        store.Decide(act.Id, "board-2", null, false, act.IntentSha256, "not on this evidence", now);
        var refused = store.Events(act.Id)[^1];
        log.WriteLine(refused.Detail);
        Assert.Equal((BoardActEventKind.Refused, (bool?)true), (refused.Kind, refused.SameFamily));
        Assert.DoesNotContain("no active seat of another family", refused.Detail ?? "", StringComparison.Ordinal);
        Assert.Contains("board-4", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("anthropic", refused.Detail, StringComparison.Ordinal);
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// AN OLDER READ OF THE POLICY NEVER REPLACES A NEWER ONE (review P1, TradingGateway.Board.cs:19): a refresh that read version 2 and
    /// was held there while another act wrote and published version 3 must not publish version 2 when it resumes — that would leave
    /// the gateway enforcing an older, wider limit after a narrowing completed. RED before the fix: the gateway held version 2's day of
    /// 30 after version 3's 20 was in force. Watched red with the version comparison removed from the publish.
    /// </summary>
    [Fact]
    public async Task An_older_read_of_the_policy_never_replaces_a_newer_one()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1", "board-2");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var armed = 1;
        gw.PolicyReadForRefresh = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1) return;
            paused.Set();
            resume.Wait(TimeSpan.FromSeconds(10));
        };

        var first = Task.Run(() => gw.BoardNarrow(Act("board-1", Change("plan_day_total", 40, 30))));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(10)), "the first refresh never read the store");
        var second = gw.BoardNarrow(Act("board-2", Change("plan_day_total", 30, 20)));
        Assert.Equal(3, second.PolicyVersion);
        Assert.Equal(20m, gw.Policy.PlanDayTotal);
        resume.Set();
        Assert.Equal(2, (await first).PolicyVersion);

        Assert.Equal(20m, gw.Policy.PlanDayTotal);
        Assert.Equal(3, new BoardReader(db).InForce().Version);
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// THE ENVELOPE'S TERMS ARE READ AGAIN INSIDE THE GRANT'S WRITE (review P1, TradingGateway.cs:490): a health pass that read version
    /// 2's narrower terms, and was held there while another act removed the envelope altogether (version 3, none), grants NOTHING when
    /// it resumes — it never grants terms the policy in force no longer states. RED before the fix: version 2's envelope was granted
    /// after version 3 had removed it. Watched red with the terms taken from the read made before the write.
    /// </summary>
    [Fact]
    public async Task A_grant_never_stands_on_terms_an_act_removed_meanwhile()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1");
        await using var gw = await PaperGateway(db);
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(Envelope), Json(EnvelopeWith("max_quantity", "0.02")))));
        Assert.Empty(Standing(gw));

        var armed = 1;
        gw.PolicyEnvelopeTermsRead = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
                gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(EnvelopeWith("max_quantity", "0.02")), Json("null"))));
        };
        await gw.RefreshHealthAsync();

        Assert.Equal(0, armed);
        Assert.Null(gw.Policy.PaperEnvelopeFor("BTCUSDT"));
        foreach (var e in gw.Envelopes.All()) log.WriteLine($"{e.MaxQuantity} until {e.ExpiresAt:O} withdrawn {e.WithdrawnAt:O} — {e.Reason}");
        Assert.Empty(Standing(gw));
        await gw.RefreshHealthAsync();
        Assert.Empty(Standing(gw));
    }
}

public partial class BoardActTests
{
    /// <summary>A clock the test moves.</summary>
    sealed class Settable(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A NARROWING NEVER LENGTHENS AN ENVELOPE'S LIFE (review P1, TradingGateway.cs:499): an envelope granted for ten days, narrowed by one
    /// seat nine days in — a lower quantity, the same ten days — is replaced by one that ends when the first would have, never ten days
    /// from the narrowing: one seat cannot widen its life. A two-seat widening of the days still gives its full new life. RED before
    /// the fix: the replacement ran to day nineteen. Watched red with the cap applied to no envelope.
    /// </summary>
    [Fact]
    public async Task A_narrowing_never_lengthens_an_envelopes_life()
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelopes.BTCUSDT", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT";
        var first = Assert.Single(gw.Envelopes.All(), Stands);
        Assert.Equal(clock.Now.AddDays(10), first.ExpiresAt);

        clock.Now = clock.Now.AddDays(9);
        var ten = EnvelopeWith("days", "10");
        var lower = System.Text.Json.Nodes.JsonNode.Parse(ten)!.AsObject();
        lower["max_quantity"] = 0.02m;
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(ten), Json(lower.ToJsonString()))));
        await gw.RefreshHealthAsync();
        var replaced = Assert.Single(gw.Envelopes.All(), Stands);
        log.WriteLine($"first until {first.ExpiresAt:O}; replacement {replaced.MaxQuantity} until {replaced.ExpiresAt:O}");
        Assert.Equal(0.02m, replaced.MaxQuantity);
        Assert.True(replaced.ExpiresAt <= first.ExpiresAt, $"the narrowing ran the envelope to {replaced.ExpiresAt:O}, past {first.ExpiresAt:O}");

        // A TWO-SEAT WIDENING OF THE DAYS gives its full new life.
        var twenty = System.Text.Json.Nodes.JsonNode.Parse(lower.ToJsonString())!.AsObject();
        twenty["days"] = 20;
        var proposed = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(lower.ToJsonString()), Json(twenty.ToJsonString()))));
        gw.BoardDecide(proposed.Act.Id, "board-2", null, true, proposed.Act.IntentSha256, "a longer run is earned");
        await gw.RefreshHealthAsync();
        Assert.Equal(clock.Now.AddDays(20), Assert.Single(gw.Envelopes.All(), Stands).ExpiresAt);
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// THE LOOP'S REVIEW INTERVAL AND CEILINGS FOLLOW AN ACT AT ONCE (review P2, AppHost.cs:1284): the options the app gives its loop ask the
    /// gateway's policy at every scheduling, so one seat raising <c>review_minutes</c> and <c>head_ceiling_hours</c> (both narrowings) is
    /// what the loop's next look and next ceiling wake are measured by, with no restart. RED before the fix: 30 minutes and 24 hours,
    /// copied at start. Watched red with the options composed without their live sources.
    /// </summary>
    [Fact]
    public async Task The_loops_review_and_ceilings_follow_an_act_at_once()
    {
        using var db = TestEnv.NewDb();
        Activate(db, "board-1");
        var (gw, _) = await Gateway(db);
        await using var _gw = gw;
        var options = TradeAgent.App.AppHost.LoopOptions(() => gw.Policy, 1);
        Assert.Equal((TimeSpan.FromMinutes(30), TimeSpan.FromHours(24)), (options.ReviewInterval(), options.CeilingFor(OrgLevel.Department)));

        gw.BoardNarrow(Act("board-1", "[{\"field\":\"review_minutes\",\"from\":30,\"to\":90},{\"field\":\"head_ceiling_hours\",\"from\":24,\"to\":72}]"));
        Assert.Equal((TimeSpan.FromMinutes(90), TimeSpan.FromHours(72)), (options.ReviewInterval(), options.CeilingFor(OrgLevel.Department)));
        Assert.Equal(TimeSpan.FromHours(72), options.CeilingFor(OrgLevel.Team));
    }
}

public partial class BoardActTests
{
    /// <summary>
    /// A TWO-SEAT WIDENING AFTER A NARROWING GETS ITS AUTHORISED LIFE (<c>U-board-b1</c> fix pass 2, the re-review's P2): an envelope of
    /// 0.1 for ten days; on day nine one seat narrows it to 0.02, and two seats then widen it to 0.04 — with no health pass between —
    /// and the one pass after grants the 0.04 envelope its full ten days from now (day nineteen): whether a life may be lengthened is
    /// read from the act that SET the terms, never from the last envelope granted. RED before the fix: day ten. Watched red with the
    /// terms before that act read from the act's own version.
    /// </summary>
    [Fact]
    public async Task A_two_seat_widening_after_a_narrowing_gets_its_authorised_life()
    {
        using var db = TestEnv.NewDb();
        SeedField(db, "paper_envelopes.BTCUSDT", EnvelopeWith("days", "10"));
        Activate(db, "board-1", "board-2");
        var clock = new Settable(DateTimeOffset.UtcNow);
        var inner = new TradeAgent.Connectors.Fake.FakeConnector(new TradeAgent.Connectors.Fake.FakeBroker
            { AccountId = BoardFounding.PaperAccount, IsSimulated = true }) { QuoteClock = clock };
        var conn = new RecordingConnector(inner, BoardFounding.PaperVenue);
        await using var gw = new TradeAgent.Gateway.TradingGateway(db, conn, new HealthRegistry(), new TradeAgent.Gateway.GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        bool Stands(PaperEnvelopeRow e) => e.StandsAt(clock.Now) && e.Symbol == "BTCUSDT";
        var first = Assert.Single(gw.Envelopes.All(), Stands);
        Assert.Equal(clock.Now.AddDays(10), first.ExpiresAt);

        clock.Now = clock.Now.AddDays(9);
        string With(decimal quantity)
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(EnvelopeWith("days", "10"))!.AsObject();
            o["max_quantity"] = quantity;
            return o.ToJsonString();
        }
        gw.BoardNarrow(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.1m)), Json(With(0.02m)))));
        var widened = gw.BoardPropose(Act("board-1", Change("paper_envelopes.BTCUSDT", Json(With(0.02m)), Json(With(0.04m)))));
        gw.BoardDecide(widened.Act.Id, "board-2", null, true, widened.Act.IntentSha256, "the book has room again");
        await gw.RefreshHealthAsync();

        var granted = Assert.Single(gw.Envelopes.All(), Stands);
        log.WriteLine($"0.04 envelope until {granted.ExpiresAt:O}; first until {first.ExpiresAt:O}");
        Assert.Equal((0.04m, clock.Now.AddDays(10)), (granted.MaxQuantity, granted.ExpiresAt));
    }
}

