using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;

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
    /// Where the paper connector's prices come from. Null until the forward-bars ledger implements
    /// <see cref="IPaperBarSource"/>; the connector then quotes nothing, fills nothing, and says so
    /// in its status line rather than inventing a price.
    /// </summary>
    public IPaperBarSource? PaperBars { get; init; }

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
            Source = choice?.PaperBars ?? new MemoryBarSource(),
            Friction = choice?.PaperFrictionNow ?? (() => PaperFriction.None)
        }),
        _ => new FakeConnector(new FakeBroker { AccountId = choice?.SimulatorAccountId ?? "SIM-001" })
    };
}
