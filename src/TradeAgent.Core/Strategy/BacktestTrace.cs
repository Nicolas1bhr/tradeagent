using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeAgent.Core.Strategy;

/// <summary>What one line of a run's trace is a record of.</summary>
public enum BacktestEventKind
{
    /// <summary>
    /// ONE CLOSED BAR: what the event produced, and what the account was worth at its close. Every
    /// bar of the window has exactly one of these, warming-up bars included — a run that recorded only
    /// the interesting bars would have no equity curve to measure a drawdown on.
    /// </summary>
    Bar,

    /// <summary>A gap in front of that bar: minutes the dataset has no bar for. Nothing was filled in.</summary>
    Gap,

    /// <summary>The evaluator emitted an intent at that bar's close.</summary>
    Signal,

    /// <summary>An entry filled, at the next bar's open plus slippage.</summary>
    Fill,

    /// <summary>A position closed, with the reason it closed. Its gross result and both fills' fees.</summary>
    Exit,

    /// <summary>
    /// A signal that could not be acted on, and WHY. A quantity that rounded down to nothing, a fill
    /// the declared capital could not pay for, a signal the window ended before. It is a line in the
    /// trace rather than a silence, because a strategy that never traded and a strategy whose trades
    /// were all refused are different findings with the same net result.
    /// </summary>
    NoTrade,

    /// <summary>The run halted: a defined fault, with its reason. Nothing after this line exists.</summary>
    Fault
}

/// <summary>
/// ONE LINE OF THE RECORD A RUN'S FIGURES ARE COMPUTED FROM.
///
/// <para>Every field is nullable and named, and each kind fills in the ones that mean something for
/// it. That is deliberately a wide record rather than a hierarchy: the trace has to render to text
/// BYTE FOR BYTE the same way on two machines and in a year (`docs/briefs/U-runner-3.md` item 4), and
/// one fixed field order is the shortest way to be sure of it.</para>
///
/// <para><see cref="Exposed"/> is true when the position was open at any point during that bar,
/// including a bar the position opened on at the open and a bar the stop closed it on. It is recorded
/// rather than derived from <see cref="Position"/> — which is the reading at the CLOSE — because a bar
/// whose whole range the position was in and which ended flat was still a bar of exposure.</para>
/// </summary>
public sealed record BacktestEvent
{
    /// <summary>The bar's place in the run, 1-based. Two events of one bar share it.</summary>
    public required long Ordinal { get; init; }

    /// <summary>The bar's open time, in UTC. The only timestamp a dataset carries.</summary>
    public required DateTimeOffset Bar { get; init; }

    public required BacktestEventKind Kind { get; init; }

    /// <summary>For <see cref="BacktestEventKind.Bar"/>: the <see cref="EvaluationStatus"/>'s name.</summary>
    public string? Status { get; init; }

    /// <summary>A fill's price, or the reference price a signal was computed at.</summary>
    public decimal? Price { get; init; }

    public decimal? Quantity { get; init; }

    /// <summary>A fill's fee; on an exit, the fees of BOTH of the trade's fills.</summary>
    public decimal? Fee { get; init; }

    /// <summary>On an exit: the trade's GROSS result, before the fees beside it.</summary>
    public decimal? Pnl { get; init; }

    /// <summary>
    /// On a bar: the account at that close — cash plus the open position marked at it. THE OPEN TRADE
    /// IS IN HERE, which is what makes the drawdown computed from this series a drawdown on the
    /// account rather than on the closed trades only.
    /// </summary>
    public decimal? Equity { get; init; }

    /// <summary>On a bar or a fill: what cash stood at. On an exit: what it stood at after the sale.</summary>
    public decimal? Cash { get; init; }

    /// <summary>On a bar: the position held at that close. Zero is flat.</summary>
    public decimal? Position { get; init; }

    /// <summary>On a gap: the minutes with no bar. Nothing stands in for them.</summary>
    public long? Minutes { get; init; }

    /// <summary>Whether the position was open at any point during this bar. See the type's summary.</summary>
    public bool Exposed { get; init; }

    /// <summary>An exit's reason, a signal's cause and kind, a no-trade's or a fault's words.</summary>
    public string? Reason { get; init; }

    internal static BacktestEvent Closed(
        long ordinal, DateTimeOffset bar, string status, decimal equity, decimal position, bool exposed) =>
        new()
        {
            Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Bar, Status = status,
            Equity = equity, Position = position, Exposed = exposed
        };

    internal static BacktestEvent Gap(long ordinal, DateTimeOffset bar, long minutes) =>
        new() { Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Gap, Minutes = minutes };

    internal static BacktestEvent Signal(
        long ordinal, DateTimeOffset bar, string kind, decimal quantity, decimal reference, string cause) =>
        new()
        {
            Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Signal, Status = kind,
            Quantity = quantity, Price = reference, Reason = cause
        };

    internal static BacktestEvent Fill(
        long ordinal, DateTimeOffset bar, decimal quantity, decimal price, decimal fee, decimal cash) =>
        new()
        {
            Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Fill, Quantity = quantity,
            Price = price, Fee = fee, Cash = cash, Exposed = true
        };

    internal static BacktestEvent Exit(
        long ordinal, DateTimeOffset bar, decimal quantity, decimal price, decimal fees, decimal pnl,
        decimal cash, string reason) =>
        new()
        {
            Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Exit, Quantity = quantity,
            Price = price, Fee = fees, Pnl = pnl, Cash = cash, Reason = reason, Exposed = true
        };

    internal static BacktestEvent NoTrade(
        long ordinal, DateTimeOffset bar, decimal quantity, decimal price, string why) =>
        new()
        {
            Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.NoTrade, Quantity = quantity,
            Price = price, Reason = why
        };

    internal static BacktestEvent Fault(long ordinal, DateTimeOffset bar, string why) =>
        new() { Ordinal = ordinal, Bar = bar, Kind = BacktestEventKind.Fault, Reason = why };

    /// <summary>
    /// THE LINE THIS EVENT IS HASHED AND COMPARED AS. Pipe separated, fixed field order, an empty
    /// field for every absent value, decimals with their trailing zeros gone
    /// (<see cref="StrategyParser.Number"/>) so <c>100.50</c> and <c>100.5</c> are one price.
    /// </summary>
    public string Line =>
        string.Join('|',
            Ordinal.ToString(CultureInfo.InvariantCulture),
            Bar.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            Kind.ToString(),
            Status ?? "",
            Num(Price), Num(Quantity), Num(Fee), Num(Pnl), Num(Equity), Num(Cash), Num(Position),
            Minutes?.ToString(CultureInfo.InvariantCulture) ?? "",
            Exposed ? "1" : "0",
            Reason ?? "");

    static string Num(decimal? value) => value is { } d ? StrategyParser.Number(d) : "";
}

/// <summary>
/// THE WHOLE RECORD OF ONE RUN, IN ORDER, AND THE HASH OF IT.
///
/// <para><b>Why the text exists at all.</b> "The same version, dataset sha, window and model produce a
/// byte-identical trace" is the unit's determinism claim, and a claim about bytes needs bytes. Two
/// runs are compared by <see cref="Sha256"/> — 64 characters recorded on the run row — so a later
/// build that quietly changed a fill rule cannot re-run an old request and reach the same recorded
/// figures through a different path.</para>
///
/// <para><b>It is not persisted.</b> The row keeps the HASH and the closed trades; the trace itself is
/// megabytes for a twelve-month run and is reproducible from the same inputs by construction, which is
/// the whole point of the hash being in the row.</para>
/// </summary>
public sealed class BacktestTrace(IReadOnlyList<BacktestEvent> events)
{
    /// <summary>Every event, in the order the run produced them.</summary>
    public IReadOnlyList<BacktestEvent> Events { get; } = events;

    /// <summary>One line per event, newline separated, with a trailing newline. What is hashed.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();
            foreach (var e in Events) text.Append(e.Line).Append('\n');
            return text.ToString();
        }
    }

    /// <summary>
    /// The SHA-256 of <see cref="Text"/>, which is what two runs are compared by — computed line by
    /// line rather than over one string.
    ///
    /// <para>The string form of a long run is tens of megabytes, and building it only to hash it would
    /// double that in transient memory on a machine this product is meant to run on. The bytes hashed
    /// are exactly <see cref="Text"/>'s: each line's UTF-8, each followed by one newline.</para>
    /// </summary>
    public string Sha256
    {
        get
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var e in Events)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(e.Line));
                hash.AppendData(Newline);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    static readonly byte[] Newline = "\n"u8.ToArray();

    /// <summary>Every event of one kind, in order. For a reader and for the metrics.</summary>
    public IEnumerable<BacktestEvent> Of(BacktestEventKind kind) => Events.Where(e => e.Kind == kind);
}
