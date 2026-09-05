using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using ATAS.DataFeedsCore.Database;
using ATAS.Indicators;
using ATAS.DataFeedsCore.Statistics;

namespace TradeAgent.AtasGate;

/// <summary>
/// A trading manager that records what it was asked to do and does none of it. The whole point of
/// the gate is that <see cref="ClosePosition"/> must NOT be called, so the only thing that has to be
/// real is the counter — and the two properties the adapter reads on its way to the call.
///
/// IT ALSO PRODUCES THE ORDERS A CLOSE HAS TO BE TOLD APART FROM. <see cref="OnClose"/> is run
/// inside <see cref="ClosePosition"/>, so whatever it adds to <see cref="Book"/> appears in exactly
/// the window the adapter diffs — which is the only way to ask "would an unrelated order that
/// arrived in the window be labelled as ours".
/// </summary>
public sealed class StubTrading : ITradingManager
{
    public int ClosePositionCalls;
    public int OpenOrderCalls;
    public Security? SecurityValue;
    public Position? PositionValue;
    public Portfolio? PortfolioValue;

    /// <summary>ATAS's own order collection, as this stub lets the gate write it.</summary>
    public readonly List<Order> Book = [];

    /// <summary>ATAS's own fill collection. On the real box a market close is Done before the
    /// adapter can look and is in NONE of the order collections — but its fill is here, carrying the
    /// order object (measured 2026-09-06). So the gate has to be able to answer a close that arrives
    /// only as a fill.</summary>
    public readonly List<MyTrade> Fills = [];

    /// <summary>Run inside ClosePosition, i.e. inside the before/after window the adapter diffs.</summary>
    public Action? OnClose;

    /// <summary>When set, OpenOrder blocks on it — the stalled synchronous SDK call of finding 10.
    /// Bounded at 60 s so a gate run can never wedge the machine it is run on.</summary>
    public ManualResetEventSlim? OpenOrderBlock;

    public bool ClosePosition(Position position, bool askConfirmation, bool checkOrderStates)
    {
        ClosePositionCalls++;
        OnClose?.Invoke();
        return true;
    }

    public Security Security => SecurityValue!;
    public Position Position => PositionValue!;
    public Portfolio Portfolio => PortfolioValue!;

    public bool IsStopLossModeActivated => false;
    public bool IsTakeProfitModeActivated => false;
    public IEnumerable<MyTrade> MyTrades => Fills;
    public IEnumerable<Order> Orders => Book;
    public TPlusLimits? TPlusLimit => null;
    public ITradingVolumeInfo TradingVolumeInfo => null!;

#pragma warning disable CS0067
    public event Action<MyTrade>? NewMyTrade;
    public event Action<Order>? NewOrder;
    public event Action<Order, string>? OrderCancelFailed;
    public event Action<Order>? OrderChanged;
    public event Action<Order, Order, string>? OrderModifyFailed;
    public event Action<Order, string>? OrderRegisterFailed;
    public event Action<Portfolio>? PortfolioChanged;
    public event Action<Portfolio>? PortfolioSelected;
    public event Action<Position>? PositionChanged;
    public event Action<Security>? SecuritySelected;
#pragma warning restore CS0067

    public void CancelOrder(Order order, bool a, bool c) => throw new NotSupportedException();
    public Task CancelOrderAsync(Order order, bool a, bool c) => throw new NotSupportedException();
    public Task ClosePositionAsync(Position p, bool a, bool c) => throw new NotSupportedException();
    public ISecurityTradingOptions GetSecurityTradingOptions() => null!;
    public bool IsStopLossOrder(Order order) => false;
    public bool IsTakeProfitOrder(Order order) => false;
    public void ModifyOrder(Order o, Order n, bool a, bool c) => throw new NotSupportedException();
    public Task ModifyOrderAsync(Order o, Order n, bool a, bool c) => throw new NotSupportedException();

    /// <summary>The stalled money call of finding 10. Counts, then waits if the gate asked it to.</summary>
    public void OpenOrder(Order o, bool d, bool a, bool c)
    {
        OpenOrderCalls++;
        OpenOrderBlock?.Wait(TimeSpan.FromSeconds(60));
    }

    public Task OpenOrderAsync(Order o, bool d, bool a, bool c) => throw new NotSupportedException();
    public Task SetBreakeven() => throw new NotSupportedException();
    public Task SetStopLoss(PriceUnit value) => throw new NotSupportedException();
    public Task SetTakeProfit(PriceUnit value) => throw new NotSupportedException();
}

/// <summary>The chart's data provider, carrying nothing but the trading manager the adapter binds.
///
/// <see cref="GetService{T}"/> is the ONE route the adapter has to ATAS's order-history cache on a
/// chart strategy (Connector is null there, trap 13), so the gate's cache is handed out from here.
/// </summary>
public sealed class StubProvider(ITradingManager trading) : IIndicatorDataProvider
{
    /// <summary>What the service locator answers with, by requested type. Empty means "knows
    /// nothing", which is what the real box reports.</summary>
    public readonly Dictionary<Type, object> Services = [];

    public ITradingManager TradingManager { get; } = trading;
    public IOnlineDataProvider OnlineDataProvider => null!;
    public ObservableCollection<CandlePartSeries> CandlesDataSeries => [];
    public IChart ChartInfo => null!;
    public IPlatformSettings GlobalPlatformSettings => null!;
    public IInstrumentInfo InstrumentInfo => null!;
    public MarketDepthInfoProvider MarketDepthInfoProvider => null!;
    public string Name => "stub";
    public ObservableCollection<string> Panels => [];
    public ITradingStatisticsProvider TradingStatisticsProvider => null!;

    public void AddAlert(string s, string i, string m, System.Windows.Media.Color b, System.Windows.Media.Color f, DateTime? t) { }
    public void DoActionInGuiThread(Action action) => action();
    public DateTime GetCustomStartTime(DateTime time, TimeSpan timeFrame) => time;
    public T GetService<T>() => Services.TryGetValue(typeof(T), out var v) ? (T)v : default!;
    public bool IsNewMonth(DateTime a, DateTime b) => false;
    public bool IsNewSession(DateTime a, DateTime b) => false;
    public bool IsNewWeek(DateTime a, DateTime b) => false;
}

/// <summary>
/// ATAS's order-history cache, as a runtime proxy rather than seventy hand-written members.
///
/// <c>ICache</c> declares about seventy methods across itself, <c>IEntityFactory</c> and
/// <c>ILoggerSource</c>, and this gate is compiled ONLY on a Windows box with ATAS installed — so a
/// hand-written stub's every signature mismatch costs a whole push-and-build cycle to discover.
/// <see cref="DispatchProxy"/> generates the implementation at runtime from the interface itself, so
/// the four members the adapter actually reads are the only ones written down and the rest answer
/// with their type's default. It cannot drift from the SDK because it is built out of the SDK.
/// </summary>
// NOT sealed: DispatchProxy.Create generates a type that DERIVES from this one.
public class CacheProxy : DispatchProxy
{
    /// <summary>What the platform says it keeps. <c>TimeSpan.Zero</c> is the case of UNVERIFIED 1:
    /// a platform that states no retention period at all.</summary>
    public TimeSpan Retention;

    /// <summary>Non-null makes the cache CONFIRM against the account (AtasStrategyAdapter.Confirm).</summary>
    public Portfolio? Known;

    /// <summary>What GetOrders(accountId) answers. Empty is a COLD cache: it holds none of the
    /// orders the window being asked for actually contains.</summary>
    public List<Order> Orders = [];

    public static ICache New(TimeSpan retention, Portfolio? known, params Order[] orders)
    {
        var proxy = DispatchProxy.Create<ICache, CacheProxy>();
        var self = (CacheProxy)(object)proxy;
        self.Retention = retention;
        self.Known = known;
        self.Orders = [.. orders];
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) return null;
        switch (method.Name)
        {
            case "get_IsInitialized": return true;
            case "get_ClearCachePeriod": return Retention;
            case "set_ClearCachePeriod": Retention = (TimeSpan)args![0]!; return null;
            case "GetPortfolio": return Known;
            case "TryGetPortfolio": return Known;
            // Two overloads; only the (String accountId) one is the order-history query the adapter
            // reads, and the other must not answer with a list that looks like it.
            case "GetOrders" when args is { Length: 1 }: return Orders;
        }
        var t = method.ReturnType;
        return t.IsValueType && t != typeof(void) ? Activator.CreateInstance(t) : null;
    }
}
