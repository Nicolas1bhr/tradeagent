using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.App;

/// <summary>
/// A HOME WHOSE SAVED PLATFORM IS ATAS, OPENED BY A BUILD WITHOUT THE ATAS ADAPTER (<c>U-atas-home-no-atas-build</c>) — every CI build,
/// so the installer the release workflow cuts (<c>TradeAgent-Setup-x64-no-atas.exe</c>, <see cref="BuildContents"/>).
///
/// <para><b>Refused at the start, before any connector, gateway, reconciler, collector or AI exists.</b> The start reads the saved
/// platform here and nowhere else: a build that cannot reach ATAS does not start the ATAS connector, does not stand another venue in for
/// it on a guess, and does not ask any venue about an order it sent to ATAS. The window says so in words and offers one way on, the
/// owner's two presses: run this build on TradeAgent's own paper venue.</para>
///
/// <para><b>Nothing of his is destroyed.</b> The press writes one key, <see cref="StandInKey"/>; the saved platform stays <c>atas</c>, so
/// a build WITH the adapter starts ATAS again and never reads the key. Every order TradeAgent sent to ATAS stays exactly as recorded —
/// UNKNOWN stays UNKNOWN: the reconciler refuses evidence from a platform other than the one a record names
/// (<c>TradingGateway.ReconcileAsync</c>), and trading stays paused over an unconfirmed one until a TradeAgent with ATAS settles it or the
/// owner answers its card on the Dashboard.</para>
/// </summary>
public static class AtasHome
{
    /// <summary>The key the owner's press writes: <c>paper</c> once he chose TradeAgent's paper venue for a build without ATAS.</summary>
    public const string StandInKey = "connector.no_atas_stand_in";

    /// <summary>The refusal's first sentence, wherever it is said.</summary>
    public const string Refused = "This build has no ATAS; your setup chose ATAS.";

    /// <summary>What the second press says it will do, in full.</summary>
    public const string ConfirmPaper =
        "Yes — run this TradeAgent on its own paper venue (no money; your ATAS choice is kept for a TradeAgent with ATAS)";

    /// <summary>
    /// The platform this start builds, from the home's saved choice, or null with <paramref name="refused"/> set: a home whose saved
    /// platform is ATAS, in a build without the adapter, before the owner chose the paper venue for it. Writes nothing.
    /// </summary>
    public static string? PlatformAtStart(Database db, bool atasInThisBuild, out AtasHomeRefusal? refused)
    {
        refused = null;
        var saved = db.GetKv("connector") ?? Platforms.Connectors.Simulator;
        if (saved != Platforms.Connectors.Atas || atasInThisBuild) return saved;
        if (db.GetKv(StandInKey) == Platforms.Connectors.Paper) return Platforms.Connectors.Paper;
        refused = new AtasHomeRefusal(UnsettledOnAtas(db));
        return null;
    }

    /// <summary>The owner's second press: TradeAgent's paper venue for this build. Writes <see cref="StandInKey"/> and nothing else.</summary>
    public static void ChoosePaper(Database db) => db.SetKv(StandInKey, Platforms.Connectors.Paper);

    /// <summary>Every order TradeAgent sent to ATAS that is not settled: not final, or flagged for reconciliation. Read-only.</summary>
    public static IReadOnlyList<ExecutionRequest> UnsettledOnAtas(Database db) =>
        [.. new ExecutionRequestStore(db).Query("connector_id = $c", ("$c", Platforms.Connectors.Atas))
            .Where(r => r.NeedsReconciliation || !OrderStateMachine.IsTerminal(r.State))];
}

/// <summary>
/// THE START'S REFUSAL OF AN ATAS HOME BY A BUILD WITHOUT THE ADAPTER, and what it found: the orders sent to ATAS that are not settled.
/// </summary>
public sealed class AtasHomeRefusal(IReadOnlyList<ExecutionRequest> unsettled)
{
    /// <summary>Every order sent to ATAS that is not settled, as recorded.</summary>
    public IReadOnlyList<ExecutionRequest> Unsettled { get; } = unsettled;

    /// <summary>Those of them whose outcome is not confirmed: UNKNOWN, mid-question, or flagged. Trading stays paused over these.</summary>
    public int Unconfirmed => Unsettled.Count(r => r.NeedsReconciliation
        || r.State is ExecutionState.UNKNOWN or ExecutionState.RECONCILING or ExecutionState.DISPATCHING);

    /// <summary>What the owner reads.</summary>
    public string Message
    {
        get
        {
            var words = AtasHome.Refused + " Nothing was started: this TradeAgent did not connect to anything and did not touch your orders.";
            if (Unsettled.Count == 0) return words;
            words += Unsettled.Count == 1
                ? " One order TradeAgent sent to ATAS is not settled"
                : $" {Unsettled.Count} orders TradeAgent sent to ATAS are not settled";
            words += Unconfirmed == 0 ? "." : Unconfirmed == Unsettled.Count
                ? (Unsettled.Count == 1 ? ", and its outcome is not confirmed." : ", and their outcome is not confirmed.")
                : $", and {Unconfirmed} of them are not confirmed.";
            words += " This build cannot reach ATAS, so they stay exactly as recorded until a TradeAgent with ATAS reconciles them.";
            if (Unconfirmed > 0)
                words += " While an outcome is unconfirmed, trading stays paused — paper too — unless you answer it on the Dashboard.";
            return words;
        }
    }
}
