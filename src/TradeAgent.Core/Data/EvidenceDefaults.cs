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

    /// <summary>
    /// WHAT THE DEFAULT DOWNLOAD WEIGHS, PER PAIR, IN MEGABYTES — MEASURED, NOT ESTIMATED, and dated: the sizes Binance's own archive
    /// listing gave on 2026-10-31 for the thirty-six monthly files 2023-10 to 2026-09 of each default pair. A claim with an expiry
    /// date — the months move on and a month weighs what its trading made it — so it is stated with <see cref="MeasuredOn"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, decimal> MeasuredMegabytes = new Dictionary<string, decimal>(StringComparer.Ordinal)
    {
        ["BTCUSDT"] = 76.4m, ["ETHUSDT"] = 73.4m, ["SOLUSDT"] = 66.2m, ["BNBUSDT"] = 62.6m, ["XRPUSDT"] = 62.0m
    };

    /// <summary>The day <see cref="MeasuredMegabytes"/>, <see cref="MeasuredMegabytesPerSecond"/> and <see cref="StoredPerDownloadedByte"/> were measured.</summary>
    public static readonly DateOnly MeasuredOn = new(2026, 10, 31);

    /// <summary>
    /// HOW FAST THE ARCHIVE ARRIVED, measured on <see cref="MeasuredOn"/>: BTCUSDT's 36 files (76.4 MB), fetched one after another with
    /// their checksums as the app fetches them, took 213 seconds of transfer — about 0.36 MB a second — on the connection it was measured
    /// on (one more file stalled and was left out of the figure). One connection's figure; the owner's may be faster or slower.
    /// </summary>
    public const decimal MeasuredMegabytesPerSecond = 0.36m;

    /// <summary>
    /// WHAT IS KEPT ON DISK PER DOWNLOADED BYTE: the vendor's zip, kept as it arrived, plus the normalised file — measured on BTCUSDT's
    /// 2026-09 (a 2.05 MB zip, 43,200 minutes normalised to 3.99 MB), so about 2.95 bytes kept for every byte downloaded.
    /// </summary>
    public const decimal StoredPerDownloadedByte = 2.95m;

    /// <summary>
    /// THE SETUP STEP'S SENTENCE ABOUT THE DOWNLOAD (<c>U-evidence-holdout-length</c> item 1): how much, how long and how much disk, for
    /// the pairs offered — a pair not measured counted at the measured pairs' average — and what a download that stops part way costs.
    /// </summary>
    public static string DownloadWords(IReadOnlyList<string> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var average = MeasuredMegabytes.Values.Average();
        var total = pairs.Sum(p => MeasuredMegabytes.TryGetValue(p, out var mb) ? mb : average);
        var minutes = Math.Max(1, (int)Math.Round(total / MeasuredMegabytesPerSecond / 60m, MidpointRounding.AwayFromZero));
        var disk = total * StoredPerDownloadedByte / 1000m;
        var them = pairs.Count == 1 ? "the pair" : $"the {pairs.Count} pairs";
        return FormattableString.Invariant(
                $"About {total:N0} MB for {them} offered here (about {average:N0} MB a pair), and about {disk:N1} GB on disk once unpacked. ")
            + FormattableString.Invariant(
                $"Measured on {MeasuredOn:yyyy-MM-dd}, the archive arrived at about {MeasuredMegabytesPerSecond} MB a second, so this takes ")
            + FormattableString.Invariant($"about {minutes} minutes there — longer on a slower connection. If it stops part way, press again: ")
            + "the months already checked are kept and only the rest are fetched; nothing is held back until every month has arrived.";
    }

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
