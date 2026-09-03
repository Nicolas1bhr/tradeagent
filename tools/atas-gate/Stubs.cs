using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.DataFeedsCore.Statistics;

namespace TradeAgent.AtasGate;

/// <summary>
/// A trading manager that records what it was asked to do and does none of it. The whole point of
/// the gate is that <see cref="ClosePosition"/> must NOT be called, so the only thing that has to be
/// real is the counter — and the two properties the adapter reads on its way to the call.
/// </summary>
public sealed class StubTrading : ITradingManager
{
    public int ClosePositionCalls;
    public Security? SecurityValue;
    public Position? PositionValue;

    public bool ClosePosition(Position position, bool askConfirmation, bool checkOrderStates)
    {
        ClosePositionCalls++;
        return true;
    }

    public Security Security => SecurityValue!;
    public Position Position => PositionValue!;
    public Portfolio Portfolio => null!;

    public bool IsStopLossModeActivated => false;
    public bool IsTakeProfitModeActivated => false;
    public IEnumerable<MyTrade> MyTrades => [];
    public IEnumerable<Order> Orders => [];
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
    public void OpenOrder(Order o, bool d, bool a, bool c) => throw new NotSupportedException();
    public Task OpenOrderAsync(Order o, bool d, bool a, bool c) => throw new NotSupportedException();
    public Task SetBreakeven() => throw new NotSupportedException();
    public Task SetStopLoss(PriceUnit value) => throw new NotSupportedException();
    public Task SetTakeProfit(PriceUnit value) => throw new NotSupportedException();
}

/// <summary>The chart's data provider, carrying nothing but the trading manager the adapter binds.</summary>
public sealed class StubProvider(ITradingManager trading) : IIndicatorDataProvider
{
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
    public T GetService<T>() => default!;
    public bool IsNewMonth(DateTime a, DateTime b) => false;
    public bool IsNewSession(DateTime a, DateTime b) => false;
    public bool IsNewWeek(DateTime a, DateTime b) => false;
}
