using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 1 — THE OWNER GRANTS BOUNDED PAPER EXPERIMENTATION ONCE, AND THE GRANT IS A ROW.
///
/// <para>The arrow this closes is <c>manager-prompt.md</c> § 5, "Paper authority": a verdict that says
/// paper-eligible has to be able to become a paper allocation without the owner pressing anything per
/// version, while <c>docs/PRINCIPLES.md</c> § boundary keeps "new live authority and live capital
/// allocations" on their deliberate confirmation. The envelope is where the one press goes: it names a
/// platform, an account, an instrument, a ceiling and a date, and everything the app does afterwards is
/// bounded by it.</para>
///
/// <para><b>The account must be provably simulated by BOTH sources.</b> The dispatch gate's own paper
/// check (<c>TradingGateway</c>, the <c>MODE_ACCOUNT_MISMATCH</c> refusal) asks
/// <c>account.IsSimulated || Capabilities.IsPaper</c>, which is right there: it is refusing an order,
/// and either witness is enough to prove the account is not real money. THIS is a standing grant that
/// will be spent without the owner in the room, over and over, for as long as it lasts — so it asks
/// for both, and an installation where the two disagree is one where nobody can say which of them is
/// wrong.</para>
/// </summary>
public class PaperEnvelopeTests
{
    static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        bool platformIsPaper = true, bool accountIsSimulated = true, TradingMode mode = TradingMode.PAPER)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()))
        {
            PlatformIsPaper = platformIsPaper,
            AccountIsSimulated = accountIsSimulated
        };
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = mode;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// THE GRANT IS REFUSED UNLESS BOTH WITNESSES SAY SIMULATION AND THE MODE IS PAPER AT THE PRESS.
    ///
    /// <para>Three refusals and one grant, and the two refusals in the middle are the whole point: an
    /// <c>||</c> here would take either witness on its own, and a standing authority to trade an
    /// account nobody can prove is simulated is the one thing this table must not be able to hold.</para>
    /// </summary>
    [Theory]
    [InlineData(true, true, TradingMode.PAPER, true)]
    [InlineData(false, true, TradingMode.PAPER, false)]
    [InlineData(true, false, TradingMode.PAPER, false)]
    [InlineData(true, true, TradingMode.LIVE_CONFIRM, false)]
    [InlineData(true, true, TradingMode.OBSERVE, false)]
    public async Task An_envelope_is_granted_only_on_an_account_both_sources_call_simulated_in_paper_mode(
        bool platformIsPaper, bool accountIsSimulated, TradingMode mode, bool ok)
    {
        var (gw, _, db) = await Ready(platformIsPaper, accountIsSimulated, mode);
        using var _1 = db;

        var result = await gw.GrantPaperEnvelopeAsync("ES", 2m, null, At.AddDays(30), At);

        Assert.Equal(ok, result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Why));
        Assert.Equal(ok ? 1 : 0, gw.Envelopes.All().Count);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN ENVELOPE IS ONE IMMUTABLE ROW WHOSE ID IS THE HASH OF ITS FACTS. Granting the same thing
    /// twice is granting it once — the <c>PromotionRow</c> and <c>AllocationRow</c> shape, for the same
    /// reason: an id minted from the clock would let one press write two standing grants and leave a
    /// later reader unable to say which of them the app was spending.
    /// </summary>
    [Fact]
    public async Task An_envelope_is_immutable_and_its_id_is_the_hash_of_its_facts()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var first = await gw.GrantPaperEnvelopeAsync("ES", 2m, null, At.AddDays(30), At);
        var again = await gw.GrantPaperEnvelopeAsync("ES", 2m, null, At.AddDays(30), At);

        Assert.True(first.Ok, first.Why);
        Assert.True(again.Ok, again.Why);
        Assert.Equal(first.Envelope!.Id, again.Envelope!.Id);
        Assert.Equal(first.Envelope.ComputedId, first.Envelope.Id);
        Assert.Single(gw.Envelopes.All());
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EXPIRED ENVELOPE STANDS NO LONGER, AND A WITHDRAWN ONE STOPS STANDING THE MOMENT IT IS
    /// WITHDRAWN. <c>withdrawn_at</c> is written once and by nothing else: a second withdrawal leaves
    /// the first instant where it is, because when permission was taken away is a fact about the
    /// owner's press and not about how many times the card was looked at.
    /// </summary>
    [Fact]
    public async Task An_expired_or_withdrawn_envelope_no_longer_stands()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var granted = await gw.GrantPaperEnvelopeAsync("ES", 2m, null, At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);

        Assert.NotNull(gw.Envelopes.Standing(conn.Id, conn.Broker.AccountId, At.AddDays(1)));
        Assert.Null(gw.Envelopes.Standing(conn.Id, conn.Broker.AccountId, At.AddDays(31)));
        Assert.Null(gw.Envelopes.Standing(conn.Id, "another-account", At.AddDays(1)));
        Assert.Null(gw.Envelopes.Standing("another-platform", conn.Broker.AccountId, At.AddDays(1)));

        var withdrawn = gw.WithdrawPaperEnvelope(granted.Envelope!.Id, At.AddDays(2));
        Assert.True(withdrawn.Ok, withdrawn.Why);
        Assert.Null(gw.Envelopes.Standing(conn.Id, conn.Broker.AccountId, At.AddDays(3)));

        gw.WithdrawPaperEnvelope(granted.Envelope.Id, At.AddDays(9));
        Assert.Equal(At.AddDays(2), gw.Envelopes.ById(granted.Envelope.Id)!.WithdrawnAt);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE CARD: TWO PRESSES TO GRANT, ONE PRESS PLUS A CONFIRM TO WITHDRAW.
    ///
    /// <para>Granting is the owner handing a standing authority to software that will spend it while
    /// they are asleep, so it is the allocate card's rule and says in full what the second press does.
    /// Withdrawing only ever takes authority away — the kill switch's rule — so it asks once, and the
    /// one question it does ask is the one the Safety page asks of every confirm: this is the control,
    /// pressed, and not a reconstruction of it.</para>
    /// </summary>
    [Fact]
    public void Granting_an_envelope_is_two_presses_and_withdrawing_is_one_press_plus_confirm()
    {
        (string Symbol, decimal Quantity, decimal? Notional, DateTimeOffset Until) typed =
            ("ES", 2m, null, At.AddDays(30));
        (string Symbol, decimal Quantity, decimal? Notional, DateTimeOffset Until)? granted = null;
        string? withdrew = null;

        var grant = SafetyPage.BuildEnvelopeConfirm(() => typed, () => "USD",
            (s, q, n, u) => granted = (s, q, n, u));

        Press(grant);
        Assert.Null(granted);
        Assert.Equal(Labels.EnvelopeArmed("ES", "2", null, At.AddDays(30)), grant.Content);

        Press(grant);
        Assert.Equal(typed, granted);

        var withdraw = SafetyPage.BuildEnvelopeWithdrawConfirm(() => "an-envelope", id => withdrew = id);
        Press(withdraw);
        Assert.Null(withdrew);
        Press(withdraw);
        Assert.Equal("an-envelope", withdrew);
    }

    /// <summary>
    /// THE TABLE IS AT SCHEMA 25. A FLOOR rather than an equality, so an additive migration above this
    /// rung does not have to edit a test about it — <c>AllocationLedgerTests</c>' reading.
    /// </summary>
    [Fact]
    public async Task The_envelope_table_is_schema_twenty_five()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        Assert.True(Versions.DatabaseSchemaVersion >= 25,
            "the paper envelope needs schema 25 or later; this build says "
            + Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(
            Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            db.Read(_ =>
            {
                using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return (string)c.ExecuteScalar()!;
            }));
        await gw.DisposeAsync();
    }
}
