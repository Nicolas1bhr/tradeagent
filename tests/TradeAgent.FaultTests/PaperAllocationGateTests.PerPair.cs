using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// ONE ENVELOPE PER PAIR (<c>U-evidence-breadth-paper</c>; R25 D11): each pair under evidence has its own standing paper envelope, a
/// favourable verdict's version is admitted only inside the envelope of the pair it trades, and each pair carries at most its own
/// deployments — a run on one pair never takes, or frees, another pair's room.
/// </summary>
public partial class PaperAllocationGateTests
{
    /// <summary>The allowlist this class's gateway needs for a second pair, beside the BTCUSDT it already allows.</summary>
    static void BothPairs(RiskPolicy r) => r.InstrumentAllowlist = [.. TestEnv.Instruments, "BTCUSDT", "ETHUSDT"];

    /// <summary>
    /// A VERSION IS PUT ONLY IN ITS OWN PAIR'S ENVELOPE: with BTCUSDT's and ETHUSDT's envelopes both standing on the account, the one
    /// writer refuses a version trading ETHUSDT in BTCUSDT's envelope — in words naming both pairs, nothing written — whoever asked;
    /// and the app's sweep puts it in ETHUSDT's, at that envelope's own ceiling. RED before the unit: the writer re-asked the verdict,
    /// the grant and the ceiling and never the instrument, so the ETHUSDT version was written into BTCUSDT's envelope.
    /// </summary>
    [Fact]
    public async Task A_version_is_put_only_in_its_own_pairs_envelope()
    {
        var (gw, _, db) = await Ready(risk: BothPairs);
        using var _1 = db;

        var btc = await Envelope(gw, quantity: 0.1m, notional: 5_000m, pair: "BTCUSDT");
        var eth = await Envelope(gw, quantity: 2.5m, notional: 5_000m, pair: "ETHUSDT");
        var version = Judged(db, PromotionVerdict.PaperEligible, pair: "ETHUSDT");

        var crossed = gw.Allocations.RecordPaper(PaperRowFor(gw, version, btc), At);
        log.WriteLine($"into BTCUSDT's envelope: {crossed.Ok} — {crossed.Why}");
        Assert.False(crossed.Ok, "a version trading ETHUSDT was written into BTCUSDT's envelope");
        Assert.Contains("trades ETHUSDT", crossed.Why, StringComparison.Ordinal);
        Assert.Contains("granted for BTCUSDT", crossed.Why, StringComparison.Ordinal);
        Assert.Empty(gw.Allocations.For(version));

        Assert.Equal(1, gw.AllocatePaperDue(At));
        var row = Assert.Single(gw.Allocations.For(version));
        log.WriteLine($"swept: envelope {row.EnvelopeId![..12]} (ETHUSDT's is {eth.Id[..12]}), up to {row.MaxQuantity} and {row.MaxNotional}");
        Assert.Equal((eth.Id, 2.5m, (decimal?)5_000m), (row.EnvelopeId, row.MaxQuantity, row.MaxNotional));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// ONE DEPLOYMENT PER PAIR, NEVER A SECOND: a BTCUSDT run started under one grant still holds BTCUSDT's slot after that grant is
    /// withdrawn, so a version allocated under the pair's next grant waits — while ETHUSDT's version, in its own envelope, is started.
    /// Each pair counts its own runs; no pair's room is another's. RED before the unit: the account's one standing envelope was read,
    /// so only one pair ever deployed. Watched red with the slot counted by envelope instead of by pair: a second BTCUSDT run started.
    /// </summary>
    [Fact]
    public async Task A_pair_carries_one_deployment_and_never_a_second()
    {
        var (gw, _, db) = await Ready(risk: BothPairs);
        using var _1 = db;

        var first = await Envelope(gw, quantity: 0.1m, notional: 5_000m, pair: "BTCUSDT");
        var older = Judged(db, PromotionVerdict.PaperEligible, threshold: 103, pair: "BTCUSDT");
        Assert.Equal(1, gw.AllocatePaperDue(At));
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
        var running = Assert.Single(gw.Deployments.All(), d => d.VersionId == older);

        // THE GRANT GOES; ITS RUN DOES NOT — not yet ended, it holds BTCUSDT's slot. The pair's next grant, and ETHUSDT's.
        Assert.True(gw.WithdrawPaperEnvelope(first.Id, At).Ok);
        var later = At.AddSeconds(1);
        var second = (await gw.GrantPaperEnvelopeAsync("BTCUSDT", 0.1m, 5_000m, At.AddDays(30), later)).Envelope!;
        var eth = (await gw.GrantPaperEnvelopeAsync("ETHUSDT", 2.5m, 5_000m, At.AddDays(30), later)).Envelope!;
        var newer = Judged(db, PromotionVerdict.PaperEligible, threshold: 104, pair: "BTCUSDT");
        var ether = Judged(db, PromotionVerdict.PaperEligible, threshold: 105, pair: "ETHUSDT");

        // ONE BTCUSDT VERSION IN THE PAIR'S NEXT GRANT (its one run's room), ETHUSDT'S IN ITS OWN.
        Assert.Equal(2, gw.AllocatePaperDue(later));
        Assert.Single(new[] { older, newer }.SelectMany(v => gw.Allocations.For(v)), a => a.EnvelopeId == second.Id);
        Assert.Equal(eth.Id, Assert.Single(gw.Allocations.For(ether)).EnvelopeId);

        var started = gw.StartPaperDeploymentsDue(later);
        var runs = gw.Deployments.All().Where(d => !d.IsEnded).ToList();
        foreach (var d in runs) log.WriteLine($"run {d.Id[..12]}: {d.Symbol} version {d.VersionId[..12]} envelope {d.EnvelopeId[..12]}");
        Assert.Equal(1, started);
        Assert.Equal(1, runs.Count(d => d.Symbol == "BTCUSDT"));
        Assert.Equal(running.Id, Assert.Single(runs, d => d.Symbol == "BTCUSDT").Id);
        Assert.Equal(ether, Assert.Single(runs, d => d.Symbol == "ETHUSDT").VersionId);
        Assert.Equal(0, gw.StartPaperDeploymentsDue(later.AddMinutes(1)));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN INCONCLUSIVE VERSION OF A PAIR IS OFFERED A PAPER ROW IN ITS FOUNDING ENVELOPE (R25 D11 as amended: three deployments per pair):
    /// on the app's own paper venue the founding policy grants each pair its envelope of three runs, so the inconclusive path's third of
    /// the slots is one — and a no-trade refusal of an ETHUSDT version is put on paper under <c>InconclusiveV1</c>, inside ETHUSDT's
    /// founding envelope, at a third of its ceilings, for exactly the term. RED with one deployment per pair (watched: the founding
    /// deployments set back to 1): a third of one slot is none, and no inconclusive version could ever reach paper.
    /// </summary>
    [Fact]
    public async Task An_inconclusive_version_of_a_pair_is_offered_a_paper_row_in_its_founding_envelope()
    {
        var (gw, _, db) = await Ready(connectorId: TradeAgent.Core.Board.BoardFounding.PaperVenue,
            account: TradeAgent.Core.Board.BoardFounding.PaperAccount, risk: BothPairs);
        using var _1 = db;

        var founding = gw.Envelopes.StandingOn(gw.Connector.Id, TradeAgent.Core.Board.BoardFounding.PaperAccount, At);
        foreach (var e in founding) log.WriteLine($"founding: {e.Symbol} up to {e.MaxQuantity} and {e.MaxNotional}, {e.MaxDeployments} runs — {e.Reason}");
        var eth = Assert.Single(founding, e => e.Symbol == "ETHUSDT");
        Assert.Equal((3, 2.5m, (decimal?)5_000m), (eth.MaxDeployments, eth.MaxQuantity, eth.MaxNotional));

        var version = Judged(db, PromotionVerdict.Refused, 110, PromotionReason.NoTrade, pair: "ETHUSDT");
        Assert.Equal(1, gw.AllocatePaperDue(At));
        var row = Assert.Single(gw.Allocations.For(version));
        log.WriteLine($"inconclusive row: {row.PolicyVersion} in {row.EnvelopeId![..12]} up to {row.MaxQuantity} and {row.MaxNotional} until {row.EffectiveTo:O} — {row.Reason}");
        Assert.Equal((AllocationPolicy.InconclusiveV1, eth.Id, eth.ShareOf(2.5m), (decimal?)eth.ShareOf(5_000m), (DateTimeOffset?)(At + Inconclusive.Term)),
            (row.PolicyVersion, row.EnvelopeId, row.MaxQuantity, row.MaxNotional, row.EffectiveTo));
        Assert.Equal(1_666.66666666m, row.MaxNotional);
        await gw.DisposeAsync();
    }
}
