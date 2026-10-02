using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-cost-model — THE REFEREE JUDGES UNDER THE VENUE COST MODEL ITS CAMPAIGN PINNED, AND NEVER UNDER
/// ONE THE SUBMITTER CHOSE.
///
/// <para><b>What was wrong before this.</b> <c>trade verdict</c> passed no model, so every judgement ran
/// under <c>ExecutionModel.Frictionless</c> — no fee, no slippage, a quantity step of ONE and ten
/// thousand of capital. On a pair priced near 85,000 every size rounds down to nothing at a step of a
/// whole Bitcoin, so every verdict came back <c>no-trade</c> and still spent one of the campaign's three
/// judgements. The fixtures priced 96 to 105 that every other referee test uses could not show it.</para>
///
/// <para><b>What this class holds.</b> The owner's press pins one app-owned model in the transaction
/// that opens the campaign, from the DATASET's recorded venue; a renewal carries it; a settings change
/// does not move it; a dataset that records no venue keeps the frictionless judge and says why; a
/// campaign opened before the model existed keeps the judge its charged verdicts were taken under, or
/// is pinned at its first verdict when it has none; and an instrument whose step nobody has confirmed
/// refuses the press in words.</para>
///
/// <para><b>The mutant this class exists to catch.</b> <c>model ?? ExecutionModel.Frictionless</c>
/// restored in <c>Referee.Verdict</c>: the BTC-priced program goes back to taking no trade at all.</para>
/// </summary>
public class CostModelPinTests(ITestOutputHelper log)
{
    internal static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    internal static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture at which the owner's holdout begins.</summary>
    internal const int HoldoutAtBar = 60;

    internal static DateTimeOffset Cutoff => Bar0.AddMinutes(HoldoutAtBar);

    /// <summary>
    /// BUYS THE DIP AND SELLS THE RIP AROUND 85,000, WITH HALF THE CAPITAL. The bars below cycle
    /// 84,000 → 86,250 in steps of 250, so each ten-bar cycle enters at the open after a close under
    /// 84,500 and leaves at the open after a close over 85,750 — a round trip that clears 0.1% a fill
    /// and two basis points of slippage comfortably. Half of 10,000 at 84,000 is 0.0595 of a Bitcoin:
    /// a real size at a step of 0.00001 and NOTHING at a step of 1.
    ///
    /// <para>It declares the three execution bounds because the referee refuses to judge a version that
    /// declares none (<c>PromotionBoundsTests</c>); none of the three changes the backtest.</para>
    /// </summary>
    internal const string BtcProgram =
        "instrument BTCUSDT\nsize capital_fraction 0.5\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 85750\nentry when close < 84500\n";

    /// <summary>The same idea with a different entry, so it is a different program and a different id.</summary>
    internal const string OtherBtcProgram =
        "instrument BTCUSDT\nsize capital_fraction 0.5\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 85750\nentry when close < 84300\n";

    /// <summary>The fixture every other referee test uses: 96 → 105, one whole unit, frictionless-friendly.</summary>
    const string ClassicProgram =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>
    /// THE VENUE MODEL THE REFEREE IS EXPECTED TO JUDGE UNDER, written out by hand rather than computed
    /// by the code under test: Binance spot's published 0.100% standard taker fee, the two basis points
    /// of slippage TradeAgent assumes, the confirmed step of 0.00001 and the shipped judge capital.
    /// </summary>
    internal const string VenueModel = "fees=0.001;slippage=0.0002;increment=0.00001;capital=10000";

    internal sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set);

    /// <summary>
    /// A GATEWAY, A CATALOGUE AND A DATASET OF REAL BARS ON DISK. <paramref name="confirmed"/> writes
    /// the venue rows the account owner would have confirmed — the shipped BTCUSDT row is unverified,
    /// and an unverified row is not a step anything may be sized to.
    /// </summary>
    internal static async Task<World> Given(
        string? venue = VenueCatalog.BinanceSpot, string? symbol = "BTCUSDT", bool confirmed = true,
        decimal basePrice = 84_000m, decimal step = 250m, Action<TradeAgentSettings>? settings = null)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 5;
            s.CampaignVerdictBudget = 3;
            settings?.Invoke(s);
        });

        // AFTER the gateway, which syncs the SHIPPED catalogue when it is built.
        if (confirmed) Confirm(db);

        return new World(gw, db, Dataset(gw, venue, symbol, basePrice, step));
    }

    /// <summary>
    /// THE ACCOUNT OWNER'S CONFIRMED ROW, written as the <c>venues.json</c> they would write — to a file
    /// of this test's own naming and synced into this test's own database, because the assembly shares
    /// one <c>TRADEAGENT_HOME</c> and a <c>venues.json</c> in it would change every other class's catalogue.
    /// </summary>
    internal static void Confirm(Database db, decimal increment = 0.00001m)
    {
        var path = Path.Combine(TestEnv.Home, $"venues-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, $$"""
            [
              { "id": "binance-spot", "display_name": "Binance spot", "calendar_kind": "continuous",
                "source": "the account owner read Binance's own exchangeInfo",
                "recorded_at": "2026-09-12T00:00:00Z", "verified": true,
                "instruments": [
                  { "symbol": "BTCUSDT", "tick_size": 0.01,
                    "quantity_increment": {{increment.ToString(CultureInfo.InvariantCulture)}},
                    "source": "the account owner read Binance's own exchangeInfo",
                    "recorded_at": "2026-09-12T00:00:00Z", "verified": true }
                ] }
            ]
            """);
        new VenueStore(db).Sync(VenueCatalog.Read(path));
    }

    /// <summary>120 one-minute bars on disk, cycling from <paramref name="basePrice"/> in ten steps.</summary>
    internal static DatasetRecord Dataset(TradingGateway gw, string? venue, string? symbol, decimal basePrice,
        decimal step, int bars = 120)
    {
        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = basePrice + step * (i % 10);
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + step / 5},{close - step / 5},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)])
        {
            VenueId = venue,
            InstrumentSymbol = symbol
        };

        return gw.Datasets.ById(gw.Datasets.Record(record))!;
    }

    /// <summary>A version recorded by the app's own writer, frozen at an instant this test chooses.</summary>
    internal static string Version(Database db, string text, DateTimeOffset frozenAt)
    {
        var parsed = StrategyParser.Parse(text).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            frozenAt, CouncilRoles.Research, "attempt-1"));
        return parsed.StrategyId;
    }

    /// <summary>
    /// A CAMPAIGN AS A BUILD BEFORE SCHEMA 27 WROTE IT — the column list <c>CampaignStore.Insert</c> had
    /// then, and nothing else. That is exactly the row the rung leaves behind: it adds the cost-model
    /// columns and backfills nothing, so every campaign that predates it reads as having pinned none.
    /// The cutoff goes on first through <c>DatasetStore.SetHoldout</c>, which is what the old press did.
    /// </summary>
    internal static long LegacyCampaign(World w, int verdicts = 3)
    {
        Assert.True(w.Gw.Datasets.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research).Ok);

        return w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("""
                INSERT INTO strategy_campaign(name, scoring_policy, scoring_policy_sha256, trial_budget,
                                              verdict_budget, holdout_dataset_id, holdout_from, opened_at,
                                              renewed_from, closed_at, exploration_budget,
                                              paper_policy, paper_policy_sha256)
                VALUES('BTCUSDT 1m v1', $policy, $sha, 5, $verdicts, $ds, $cut, $at, NULL, NULL, 5,
                       $paper, $papersha);
                SELECT last_insert_rowid();
                """,
                ("$policy", CampaignPolicy.V1), ("$sha", CampaignPolicy.Sha256Of(CampaignPolicy.V1)),
                ("$verdicts", verdicts), ("$ds", w.Set.Id),
                ("$cut", Cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$at", At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$paper", CampaignPolicy.PaperV1),
                ("$papersha", CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1)));
            return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        });
    }

    internal static Referee RefereeOf(World w) => new(w.Db, () => At);

    void Describe(RefereeVerdict verdict, StrategyRunRow? run)
    {
        log.WriteLine($"ok={verdict.Ok} why={verdict.Why}");
        log.WriteLine($"verdict={verdict.Promotion?.Verdict} reason={verdict.Promotion?.Reason} model={verdict.Promotion?.ExecutionModel}");
        if (run is not null)
            log.WriteLine($"trades={run.Trades} gross={run.GrossPnl} fees={run.Fees} net={run.NetPnl}");
    }

    // ---- (a) the observable result ------------------------------------------------------------------

    /// <summary>
    /// A BTC-PRICED PROGRAM SIZED AT HALF THE CAPITAL TRADES ON THE HOLDOUT, NET OF THE VENUE'S FEE.
    ///
    /// <para>Red first: under the frictionless judge the size is 0.0595 rounded down to a step of 1,
    /// which is nothing, so the verdict was <c>no-trade</c> — and it still spent a judgement. The caller
    /// passes no model here, exactly as <c>trade verdict</c> passes none: the friction is the campaign's,
    /// fixed when the owner pressed, and the submitter never chooses it.</para>
    /// </summary>
    [Fact]
    public async Task A_btc_priced_program_trades_on_the_holdout_under_the_pinned_cost_model()
    {
        var w = await Given();
        using var _1 = w.Db;

        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        var version = Version(w.Db, BtcProgram, Bar0);
        var verdict = RefereeOf(w).Verdict(version, campaign.Id);
        var run = verdict.Promotion is { } p ? new StrategyStore(w.Db).RunById(p.HoldoutRunId) : null;
        Describe(verdict, run);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.NotNull(run);
        Assert.True(run.Trades > 0, $"the holdout run took no trade: {verdict.Promotion!.Reason}");
        Assert.Equal(VenueModel, verdict.Promotion!.ExecutionModel);

        // NET OF THE VENUE'S FEE: every fill paid 0.1% of its notional, and the net the verdict was
        // scored on is the gross less exactly those fees.
        Assert.True(run.Fees > 0m, "a venue model with a published fee charged nothing");
        Assert.Equal(run.GrossPnl - run.Fees, run.NetPnl);
        foreach (var t in new StrategyStore(w.Db).TradesOf(run.Id))
            Assert.Equal(Math.Round((t.EntryPrice + t.ExitPrice) * t.Quantity * 0.001m, 10),
                Math.Round(t.Fees, 10));

        // AND IT CLEARED THEM: a version frozen before the cutoff, profitable after costs, is promoted.
        Assert.True(verdict.Promoted, verdict.Promotion.Reason);
    }

    // ---- (b) pinned at the press, and only there ---------------------------------------------------

    /// <summary>
    /// THE PIN IS FIXED WHEN THE OWNER PRESSES, AND NEITHER A SETTINGS CHANGE NOR A RENEWAL MOVES IT.
    ///
    /// <para>Red first by failing to compile: before this unit a campaign had no cost model to pin and
    /// <c>TradeAgentSettings</c> had no judge capital. The model is the DATASET's venue's, with the
    /// owner's capital AS IT STOOD at the press; changing the setting or the catalogue afterwards moves
    /// no campaign already open, and a renewal carries the parent's pin rather than today's numbers —
    /// a renewal buys attempts and never cheaper friction.</para>
    /// </summary>
    [Fact]
    public async Task The_pin_is_fixed_at_campaign_open_and_survives_a_settings_change_and_a_renewal()
    {
        const string AtTwenty = "fees=0.001;slippage=0.0002;increment=0.00001;capital=20000";
        var w = await Given(settings: s => s.JudgeCapital = 20_000m);
        using var _1 = w.Db;

        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);
        log.WriteLine(campaign.CostModelCanonical);

        // PINNED IN THE OPENING TRANSACTION, with its own hash beside it.
        var pinned = VenueCostModel.Read(campaign.CostModelCanonical, campaign.CostModelSha256);
        Assert.NotNull(pinned);
        Assert.Equal(AtTwenty, pinned.Model.Canonical);
        Assert.Equal(Sha256Hex.Of(campaign.CostModelCanonical!), campaign.CostModelSha256);
        Assert.Equal((campaign.CostModelCanonical, campaign.CostModelSha256), PinOf(w.Db, campaign.Id));

        // THE OWNER CHANGES THE SETTING, AND THE CATALOGUE'S STEP MOVES: neither reaches the open campaign.
        w.Gw.Update(s => s.JudgeCapital = 50_000m);
        Confirm(w.Db, increment: 0.001m);
        var after = w.Gw.Campaigns.ById(campaign.Id)!;
        Assert.Equal(campaign.CostModelCanonical, after.CostModelCanonical);
        Assert.Equal(campaign.CostModelSha256, after.CostModelSha256);

        // A RENEWAL CARRIES THE PARENT'S PIN, not today's setting and not today's step.
        var child = w.Gw.Campaigns.Renew(campaign.Id, 5, 3, At);
        Assert.True(child.Ok, child.Why);
        Assert.Equal(campaign.CostModelCanonical, child.Campaign!.CostModelCanonical);
        Assert.Equal(campaign.CostModelSha256, child.Campaign.CostModelSha256);

        // AND THE GATEWAY'S OWN REFEREE — the one `trade verdict` reaches — scores the child's verdict
        // under the pin, although the setting now says 50,000 and the catalogue 0.001.
        var verdict = w.Gw.Referee.Verdict(Version(w.Db, BtcProgram, Bar0), child.Campaign.Id);
        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(AtTwenty, verdict.Promotion!.ExecutionModel);
    }

    /// <summary>
    /// EVERY PIN SAYS WHICH JUDGE IT IS, IN ITS OWN TEXT AND ON THE HOLDOUT CARD — the venue's fee, "no
    /// venue recorded", or "legacy frictionless judge" — and a campaign that has not been pinned yet
    /// says so rather than guessing which it will get.
    /// </summary>
    [Fact]
    public async Task Every_pin_names_its_judge_in_words_on_its_row_and_on_the_holdout_card()
    {
        // THE VENUE'S.
        var venue = await Given();
        using var _1 = venue.Db;
        var (_, pinned) = venue.Gw.SetHoldout(venue.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.Contains($"judge: {VenueCostModel.VenueJudge}", pinned!.CostModelCanonical, StringComparison.Ordinal);
        var line = App.SettingsPage.JudgeLine(pinned);
        log.WriteLine(line);
        Assert.StartsWith("binance-spot BTCUSDT: fee 0.1% a fill, the venue's published standard taker rate; "
            + "slippage 0.02% a fill, TradeAgent's assumption; step 0.00001; capital 10000", line, StringComparison.Ordinal);

        // NO VENUE RECORDED.
        var bare = await Given(venue: null, symbol: null, basePrice: 96m, step: 1m);
        using var _2 = bare.Db;
        var (_, frictionless) = bare.Gw.SetHoldout(bare.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.Contains("judge: no venue recorded", frictionless!.CostModelCanonical, StringComparison.Ordinal);
        Assert.Contains("no venue recorded", App.SettingsPage.JudgeLine(frictionless), StringComparison.Ordinal);

        // LEGACY: unpinned until its next verdict, then pinned frictionless because one was already charged.
        var old = await Given();
        using var _3 = old.Db;
        var legacy = LegacyCampaign(old);
        Assert.StartsWith("fixed at this campaign's next judgement",
            App.SettingsPage.JudgeLine(old.Gw.Campaigns.ById(legacy)), StringComparison.Ordinal);

        Assert.True(old.Gw.Campaigns.ChargeVerdict(legacy, Version(old.Db, BtcProgram, Bar0), At).Ok);
        Assert.True(RefereeOf(old).Verdict(Version(old.Db, OtherBtcProgram, Bar0), legacy).Ok);

        var row = old.Gw.Campaigns.ById(legacy)!;
        Assert.Contains("judge: legacy frictionless judge", row.CostModelCanonical, StringComparison.Ordinal);
        Assert.Equal(VenueCostModel.LegacyFrictionless.Sha256, row.CostModelSha256);
        Assert.Contains("legacy frictionless judge", App.SettingsPage.JudgeLine(row), StringComparison.Ordinal);

        Assert.Equal("—", App.SettingsPage.JudgeLine(null));
    }

    // ---- (c) and (d): campaigns opened before the model existed ------------------------------------

    /// <summary>
    /// A LEGACY CAMPAIGN WITH A CHARGED VERDICT KEEPS THE FRICTIONLESS JUDGE. A guard: it is true before
    /// this unit and must stay true after it.
    ///
    /// <para>Every verdict this installation charged before schema 27 was judged frictionless, because
    /// nothing passed a model. A campaign that has already spent one of its judgements under that judge
    /// keeps it: re-judging the next version under a different one would make two verdicts of one
    /// campaign answers to two different questions, and asking again about the version already paid
    /// for must not quietly re-score it.</para>
    /// </summary>
    [Fact]
    public async Task A_legacy_campaign_with_a_charged_verdict_keeps_the_frictionless_judge()
    {
        var w = await Given();
        using var _1 = w.Db;
        var campaignId = LegacyCampaign(w);
        var first = Version(w.Db, BtcProgram, Bar0);
        var second = Version(w.Db, OtherBtcProgram, Bar0);

        // THE VERDICT ALREADY CHARGED BEFORE THE UPGRADE — the row the old referee's RequestVerdict wrote.
        Assert.True(w.Gw.Campaigns.ChargeVerdict(campaignId, first, At).Ok);

        var verdict = RefereeOf(w).Verdict(second, campaignId);
        Describe(verdict, null);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(ExecutionModel.Frictionless.Canonical, verdict.Promotion!.ExecutionModel);
        // A whole-unit step on a pair priced at 85,000 buys nothing: the old judge, exactly as it was.
        Assert.Equal(PromotionReason.NoTrade, verdict.Promotion.Reason);

        // AND THE VERSION ALREADY PAID FOR IS JUDGED BY THE JUDGE IT WAS PAID FOR UNDER.
        var again = RefereeOf(w).Verdict(first, campaignId);
        Assert.True(again.Ok, again.Why);
        Assert.Equal(ExecutionModel.Frictionless.Canonical, again.Promotion!.ExecutionModel);
        Assert.Equal(2, w.Gw.Campaigns.VerdictsInLineage(campaignId));
    }

    /// <summary>
    /// A LEGACY CAMPAIGN THAT NEVER CHARGED A VERDICT IS PINNED AT ITS FIRST ONE, from its dataset's
    /// venue, in the transaction that charges it.
    ///
    /// <para>Red first: it was judged frictionless and took no trade. Nothing about such a campaign was
    /// ever judged, so there is no earlier judge to stay consistent with — and the first verdict is the
    /// moment the question is first asked.</para>
    /// </summary>
    [Fact]
    public async Task A_legacy_campaign_never_charged_is_pinned_at_its_first_verdict()
    {
        var w = await Given();
        using var _1 = w.Db;
        var campaignId = LegacyCampaign(w);
        var version = Version(w.Db, BtcProgram, Bar0);

        var verdict = RefereeOf(w).Verdict(version, campaignId);
        var run = verdict.Promotion is { } p ? new StrategyStore(w.Db).RunById(p.HoldoutRunId) : null;
        Describe(verdict, run);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(VenueModel, verdict.Promotion!.ExecutionModel);
        Assert.True(run!.Trades > 0, verdict.Promotion.Reason);

        // AND THE PIN IS ON THE ROW NOW, with the sha of its own text beside it.
        var (canonical, sha) = PinOf(w.Db, campaignId);
        Assert.NotNull(canonical);
        Assert.Contains(VenueModel, canonical, StringComparison.Ordinal);
        Assert.Equal(CampaignPolicy.Sha256Of(canonical), sha);
    }

    /// <summary>
    /// AND ONE WHOSE INSTRUMENT NOBODY CONFIRMED IS REFUSED BEFORE THAT FIRST VERDICT IS CHARGED. The
    /// budget is the scarcest thing in the product, and a judgement that could only have guessed the
    /// step would have spent it on a strategy nobody submitted.
    /// </summary>
    [Fact]
    public async Task A_legacy_campaign_over_an_unconfirmed_instrument_is_refused_before_its_first_verdict_is_charged()
    {
        var w = await Given(confirmed: false);
        using var _1 = w.Db;
        var campaignId = LegacyCampaign(w);
        var version = Version(w.Db, BtcProgram, Bar0);

        var verdict = RefereeOf(w).Verdict(version, campaignId);
        Describe(verdict, null);

        Assert.False(verdict.Ok, "a verdict was taken under a step nobody confirmed");
        Assert.Null(verdict.Promotion);
        Assert.Contains("BTCUSDT's quantity step is not confirmed against the venue's own definition", verdict.Why,
            StringComparison.Ordinal);
        Assert.Equal(0, w.Gw.Campaigns.VerdictsInLineage(campaignId));
        Assert.Empty(new StrategyStore(w.Db).Runs(50));
        Assert.Null(PinOf(w.Db, campaignId).Canonical);
    }

    // ---- (f) the press ------------------------------------------------------------------------------

    /// <summary>
    /// AN INSTRUMENT WHOSE STEP NOBODY CONFIRMED REFUSES THE OWNER'S PRESS, IN WORDS, AND WRITES NOTHING.
    ///
    /// <para>Red first: the press opened a campaign that could only ever be judged frictionless. Now the
    /// judge's model is settled before the cutoff is written, so the press that cannot pin one moves no
    /// cutoff and opens no campaign: the two land together or neither does. The words name what is not
    /// known, and no file for the owner to edit — this is the state BTCUSDT ships in.</para>
    /// </summary>
    [Fact]
    public async Task An_unverified_instrument_refuses_the_campaign_press_in_words()
    {
        var w = await Given(confirmed: false);
        using var _1 = w.Db;

        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research);
        log.WriteLine(held.Why);

        Assert.False(held.Ok, "the press opened a campaign over a step nobody confirmed");
        Assert.Null(campaign);
        Assert.Contains("BTCUSDT's quantity step is not confirmed against the venue's own definition; a judge "
            + "that guessed it would judge a different strategy", held.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("venues.json", held.Why, StringComparison.Ordinal);

        // NOTHING WAS WRITTEN: no cutoff, no campaign.
        Assert.Null(w.Gw.Datasets.ById(w.Set.Id)!.HoldoutFrom);
        Assert.Null(w.Gw.Campaigns.OpenForDataset(w.Set.Id));
        Assert.Empty(w.Gw.Campaigns.All());
    }

    // ---- (g) no venue -------------------------------------------------------------------------------

    /// <summary>
    /// A DATASET THAT RECORDS NO VENUE KEEPS THE FRICTIONLESS JUDGE. A guard, and the reason every
    /// fixture-backed referee test in this suite stands: there is no venue to read a fee or a step from,
    /// and inventing one would put a number nobody measured into a verdict.
    /// </summary>
    [Fact]
    public async Task A_dataset_without_a_venue_keeps_the_labelled_frictionless_judge()
    {
        var w = await Given(venue: null, symbol: null, basePrice: 96m, step: 1m);
        using var _1 = w.Db;

        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        var verdict = RefereeOf(w).Verdict(Version(w.Db, ClassicProgram, Bar0), campaign.Id);
        Describe(verdict, null);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(ExecutionModel.Frictionless.Canonical, verdict.Promotion!.ExecutionModel);
        Assert.True(verdict.Promoted, verdict.Promotion.Reason);
    }

    /// <summary>The pin as the row holds it, read past the store.</summary>
    internal static (string? Canonical, string? Sha) PinOf(Database db, long campaignId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT cost_model_canonical, cost_model_sha FROM strategy_campaign WHERE id=$id",
            ("$id", campaignId));
        using var r = c.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    });
}
