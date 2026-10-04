namespace TradeAgent.Core.Data;

/// <summary>
/// THE TERMS A DATASET'S BARS CAME UNDER, AND WHICH OF THEM MAY CONFER LIVE ELIGIBILITY
/// (<c>U-data-licence</c>; <c>docs/CONTRACTS.md</c>, "Data licences").
///
/// <para><b>Why this exists.</b> Binance's public archive is now under the Binance Vision Dataset Terms
/// v1.0 (CC BY-NC-SA 4.0): § 4.1 allows backtesting for personal non-production research and § 4.2 forbids
/// live proprietary trading execution. The orchestrator's COMPLY decision of 2026-10-04 and
/// <c>docs/research/2026-10-04/R19-historical-data-licences.md</c> follow from it: archive datasets serve
/// research — backtests, paper, M0 — and no live eligibility may rest on them.</para>
///
/// <para><b>The class is TEXT and never a CHECK.</b> A class a newer build writes reads as itself rather
/// than failing a constraint (the reading <c>MissionEventKind</c> gives) — and it confers NOTHING unless it
/// is one of the two words below. Unknown is never zero: NULL, <see cref="ResearchOnly"/>,
/// <see cref="Unverified"/> and a misspelling all read research-only (<c>docs/EDGE-FACTORY.md</c> § 6.7).</para>
///
/// <para><b>App-written only.</b> The <c>data_licence</c> table is written by schema rungs and by nothing
/// else — reclassifying a source is a new row in a later rung, never gate code — and a dataset row's four
/// columns are written by the collector from that table (<c>MarketDataService</c>) and by rung 30's one
/// backfill. There is no op, no verb and no file an agent writes that sets or inherits a class.</para>
/// </summary>
public static class DataLicence
{
    /// <summary>
    /// The vendor's terms allow research and forbid live use — the Binance archive under the Binance Vision
    /// Dataset Terms v1.0 (§ 4.1, § 4.2). Backtests and paper go on; no capital stands on it.
    /// </summary>
    public const string ResearchOnly = "research-only";

    /// <summary>
    /// The terms were read and do not settle the live-gating use. Confers nothing, exactly as
    /// <see cref="ResearchOnly"/> does: it is the forward ledger's reading while R19 § 6 Q2 is open.
    /// </summary>
    public const string Unverified = "unverified";

    /// <summary>
    /// Recorded by TradeAgent itself from a venue whose own terms reach the owner's own-account use.
    /// One of the two classes that confer.
    /// </summary>
    public const string FirstParty = "first-party";

    /// <summary>A written licence covering this use. One of the two classes that confer.</summary>
    public const string CommercialOk = "commercial-ok";

    /// <summary>
    /// WHETHER A CLASS MAY CONFER LIVE ELIGIBILITY: two words, and every other value — NULL included — no.
    /// The list is what confers rather than what refuses, so a word nobody wrote here cannot open anything.
    /// </summary>
    public static bool Confers(string? licenceClass) => licenceClass is CommercialOk or FirstParty;

    // ---- the two readings rung 30 seeds (R19 § 8, S1 and S2; both read 2026-10-04) ----------------------

    /// <summary>The Binance Vision Dataset Terms, R19 S1 — the archive's reading.</summary>
    public const string ArchiveTermsUrl =
        "https://github.com/binance/binance-public-data/blob/master/TERMS_AND_CONDITIONS.md";

    /// <summary>The version as printed on the terms.</summary>
    public const string ArchiveTermsVersion = "v1.0, updated 2026-08-26";

    /// <summary>The day R19 read them.</summary>
    public const string ArchiveTermsReadOn = "2026-10-04";

    /// <summary>What the archive's reading rests on, in words.</summary>
    public const string ArchiveNote =
        "Binance Vision Dataset Terms v1.0 (CC BY-NC-SA 4.0): § 4.1 allows backtesting for personal "
        + "non-production research and § 4.2 forbids live proprietary trading execution; the orchestrator's "
        + "COMPLY decision of 2026-10-04 (R19)";

    /// <summary>Binance's Spot API "Terms of Use" page, R19 S2 — the forward ledger's reading.</summary>
    public const string ForwardTermsUrl =
        "https://developers.binance.com/docs/binance-spot-api-docs/PROD-TERMS-OF-USE";

    /// <summary>The page's own date, as R19 read it.</summary>
    public const string ForwardTermsVersion = "modified 2026-10-02";

    /// <summary>The day R19 read it.</summary>
    public const string ForwardTermsReadOn = "2026-10-04";

    /// <summary>Why the forward ledger reads <see cref="Unverified"/> — the orchestrator's order of 2026-10-04.</summary>
    public const string ForwardNote = "R19 § 6 Q2 open: Binance API terms not read for live-gating use";
}
