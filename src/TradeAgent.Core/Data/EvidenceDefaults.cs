using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// THE DEFAULTS THE OWNER ACCEPTS IN ONE PRESS DURING SETUP (<c>U-evidence-campaign</c> item 1; R25 D7): which months of the
/// first download are held back as the private test the AI is judged on. The Settings page's two presses — Download and
/// "Hold these bars back" — stay where they are; this is what the setup step presses for him, with the date chosen here
/// rather than typed.
///
/// <para><b>The last <see cref="HoldoutMonths"/> complete calendar months of the download</b>, from the first instant of
/// the earliest of them, UTC. Whole months because the archive is published in whole months and a boundary that fell
/// inside one would hold back a part of a file nobody can point at; the most recent ones because a holdout is the
/// months nearest the present a strategy will trade in. <b>Twelve of thirty-six</b> (<c>U-evidence-holdout-length</c>; R25 D12,
/// decided by the orchestrator on the owner's behalf, his to overrule): twelve whole months are 365 or 366 days, which is what the
/// referee asks of a holdout before it can be conclusive — <c>Inconclusive.ShortHoldout</c> calls a loss over less inconclusive, and
/// the α-test (<c>AlphaWealth.MinHoldoutDays</c>) is run only over 365 known days or more. Three of twelve, the default before it,
/// made every verdict inconclusive (R27). Nothing about the scorer is lowered: the bar stays and the data meets it. The margin is
/// none in a year without a 29 February — 365 days exactly — so a holdout missing a whole UTC day of bars, or judged on daily bars
/// that open off midnight UTC, counts 364 known days and its α-test stays inconclusive.</para>
///
/// <para><b>It never chooses a date for a dataset that already has one.</b> A cutoff the owner set is his, and moving it
/// later while no campaign holds it would RELEASE months he held back; the setup step opens the campaign at the cutoff
/// that is there (<c>EvidenceCampaigns.SetUpAsync</c>).</para>
/// </summary>
public static class EvidenceDefaults
{
    /// <summary>How many of the download's most recent complete months are held back: twelve, a year the referee can be conclusive over.</summary>
    public const int HoldoutMonths = 12;

    /// <summary>
    /// THE PAIRS SETUP OFFERS, EACH HELD BACK UNDER ITS OWN CAMPAIGN (<c>U-evidence-breadth</c> item 1): Binance spot's five most
    /// liquid USDT pairs — BTC, ETH, SOL, BNB and XRP. Chosen, and stated: each is published month by month in Binance's public
    /// archive (the data source, no account and no key), each is in the tape's universe (<see cref="TapeSourceCatalog.Universe"/>,
    /// whose sixth, DOGE, is left out as the least liquid), and the paper venue trades any Binance spot pair whose step the app's
    /// instrument check has read from the venue's own definition (<c>PaperConnector</c>, <c>U-venue-verify</c>), which setup asks
    /// for each pair before holding it back. Five campaigns of three judgements are fifteen looks over fresh months instead of
    /// three; the owner unticks any he does not want, and the pair on his Settings page is always offered first.
    /// </summary>
    public static readonly IReadOnlyList<string> Pairs = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT"];

    /// <summary>What setup offers, in order: the owner's Settings pair first when it is a pair, then <see cref="Pairs"/>, each once.</summary>
    public static IReadOnlyList<string> Offered(string? settingsPair) =>
        [.. (BinanceArchive.IsPair(settingsPair) ? [settingsPair!] : Array.Empty<string>()).Concat(Pairs).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// THE CUTOFF THE SETUP STEP HOLDS BACK FROM, or null with nothing to hold: no bar recorded, or a download too short to
    /// keep at least one research month before the held months. Midnight UTC on the first day of the earliest held month.
    /// </summary>
    public static DateTimeOffset? Cutoff(DatasetRecord set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.FirstBar is not { } first || set.LastBar is not { } last) return null;

        var newest = last.UtcDateTime;
        var cutoff = new DateTimeOffset(newest.Year, newest.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(HoldoutMonths - 1));

        // AT LEAST ONE WHOLE RESEARCH MONTH BEFORE IT: a holdout with nothing before it is a campaign nobody can run a trial
        // under, so no verdict could ever be asked of it.
        return first.ToUniversalTime() <= cutoff.AddMonths(-1) ? cutoff : null;
    }
}
