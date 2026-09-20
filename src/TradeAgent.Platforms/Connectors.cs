using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core.Db;

namespace TradeAgent.Platforms;

/// <summary>
/// What a connector needs that only the host can supply — the simulator's account name, and the
/// paper connector's prices and declared friction. Everything optional, and every default is the
/// safe one: no bars, and no friction modelled.
/// </summary>
public sealed record ConnectorChoice
{
    /// <summary>The practice simulator's account id, for a host that names one (<c>--account</c>).</summary>
    public string? SimulatorAccountId { get; init; }

    /// <summary>
    /// Where the paper connector's prices come from, when the host has a source of its own. Left
    /// null, <see cref="ForwardBars"/> is used; with neither, the connector quotes nothing, fills
    /// nothing, and says so in its status line rather than inventing a price.
    /// </summary>
    public IPaperBarSource? PaperBars { get; init; }

    /// <summary>
    /// THE FORWARD-BAR LEDGER THE PAPER CONNECTOR SETTLES FROM — the minutes this installation
    /// collected while it was running.
    ///
    /// <para>Handed over as a store rather than as a built <see cref="ForwardBarSource"/> so that
    /// <see cref="Connectors.Create"/> stays the one place that decides what a platform id gets: a
    /// host that built the adapter itself would be a second answer to that question, which is the
    /// defect this class was made to end.</para>
    /// </summary>
    public ForwardBarStore? ForwardBars { get; init; }

    /// <summary>
    /// HOW THE HOST WIRES A COLLECTOR'S "a minute closed" TO THE SOURCE THIS CALL BUILDS.
    ///
    /// <para>Called with <see cref="ForwardBarSource.Announce"/> while the connector is being built.
    /// It is a subscription and not a collector, because the collector does not exist yet at that
    /// moment in either host — the app creates it after the gateway, and the gateway host has none
    /// at all, where polling <see cref="IPaperBarSource.SinceAsync"/> is the whole of the answer.</para>
    /// </summary>
    public Action<Action<string, DateTimeOffset>>? SubscribeToBarClosed { get; init; }

    /// <summary>
    /// The paper connector's fee and slippage fractions, read AT THE MOMENT OF THE FILL — a function
    /// because the owner can change them between one fill and the next, and because the host builds
    /// the connector before it has loaded the settings to read them from.
    /// </summary>
    public Func<PaperFriction>? PaperFrictionNow { get; init; }
}

/// <summary>
/// THE ONE PLACE THAT KNOWS WHICH TRADING PLATFORMS EXIST.
///
/// <para>It replaces the two ternaries that used to decide it — one in <c>AppHost</c> and one in the
/// gateway host — which agreed on two platforms by coincidence and would have disagreed about a
/// third: a host that did not know the id fell through to the simulator, so an installation whose
/// saved platform was <c>paper</c> would have been silently put back on the practice simulator by
/// one entry point and not the other.</para>
///
/// <para>An id nothing here recognises still falls through to the practice simulator, and that stays
/// the right direction: the simulator is the one platform where being wrong costs nothing.</para>
/// </summary>
public static class Connectors
{
    /// <summary>The built-in practice simulator. Nothing here is real and nothing can be lost.</summary>
    public const string Simulator = FakeConnector.ConnectorId;

    /// <summary>TradeAgent's own paper connector: real prices in, simulated fills out.</summary>
    public const string Paper = PaperConnector.ConnectorId;

    /// <summary>
    /// ATAS. Spelled here rather than read off <c>AtasConnector.Id</c> because that is an instance
    /// property and this has to be usable to DECIDE which instance to build.
    /// </summary>
    public const string Atas = "atas";

    /// <summary>Every platform this build can be switched to, in the order the Settings page offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Simulator, Paper, Atas];

    public static ITradingConnector Create(string? id, ConnectorChoice? choice = null) => id switch
    {
        Atas => new AtasConnector(),
        Paper => new PaperConnector(new PaperConnectorOptions
        {
            Source = PaperBarsFor(choice),
            Friction = choice?.PaperFrictionNow ?? (() => PaperFriction.None)
        }),
        _ => new FakeConnector(new FakeBroker { AccountId = choice?.SimulatorAccountId ?? "SIM-001" })
    };

    /// <summary>
    /// THE PAPER CONNECTOR'S PRICES: the host's own source, else the FORWARD-BAR LEDGER, else
    /// nothing at all.
    ///
    /// <para>The middle arm is what <c>U-runner</c> added. Until it existed this method handed the
    /// connector an empty <see cref="MemoryBarSource"/> whatever the collector had stored, so a
    /// paper account quoted nothing and filled nothing on an installation that had been collecting
    /// minutes for a week. An empty source stays the honest answer when there is no ledger to read —
    /// it is a connector that says it has no prices, which is a different thing from one that
    /// invents them.</para>
    /// </summary>
    static IPaperBarSource PaperBarsFor(ConnectorChoice? choice)
    {
        if (choice?.PaperBars is { } supplied) return supplied;
        if (choice?.ForwardBars is not { } ledger) return new MemoryBarSource();

        var source = new ForwardBarSource(ledger);
        choice.SubscribeToBarClosed?.Invoke(source.Announce);
        return source;
    }
}
