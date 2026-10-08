using TradeAgent.Core.Db;

namespace TradeAgent.Core.Data;

/// <summary>
/// ONE DATASET'S HOLDOUT, ON THE TAPE'S CLOCK (<c>U-tape-holdout</c>): every tape row whose SOURCE time is at or after
/// <see cref="From"/> and before <see cref="Until"/> is private evaluation evidence, whatever its source, series or
/// subject.
///
/// <para><b>The window is the market time the holdout's bars cover:</b> from the dataset's <c>holdout_from</c>
/// (inclusive, as <see cref="Holdout"/> reads it) to the close of its last bar — <see cref="DatasetRecord.LastBar"/> plus
/// one bar of its <see cref="DatasetRecord.Interval"/>, exclusive. A dataset whose last bar is not recorded, or whose bar
/// length this build cannot read, is held with NO end (<see cref="Until"/> null): refused rather than guessed.</para>
/// </summary>
public sealed record TapeHoldoutWindow(
    long DatasetId, string Pair, string Interval, string Version, DateTimeOffset From, DateTimeOffset? Until)
{
    /// <summary>Whether a row stamped <paramref name="sourceTime"/> is inside.</summary>
    public bool Holds(DateTimeOffset sourceTime) => sourceTime >= From && (Until is not { } end || sourceTime < end);

    /// <summary>
    /// Whether a read of source times from <paramref name="from"/> to <paramref name="to"/>, both inclusive and either
    /// null for no bound, reaches this window. An absent bound reaches every window on its side.
    /// </summary>
    public bool ReachedBy(DateTimeOffset? from, DateTimeOffset? to) =>
        (to is not { } hi || hi >= From) && (from is not { } lo || Until is not { } end || lo < end);

    /// <summary>
    /// Whether market time from <paramref name="from"/> (inclusive) to <paramref name="until"/> (exclusive), either null
    /// for no bound, overlaps this window — the test a run of BARS takes (<c>U-bar-holdout</c>): a bar is the span from
    /// its open to its close, so one that opens before the window and closes inside it reaches it.
    /// </summary>
    public bool Overlaps(DateTimeOffset? from, DateTimeOffset? until) =>
        (until is not { } hi || hi > From) && (from is not { } lo || Until is not { } end || lo < end);

    /// <summary>The window in the words a refusal uses: the dataset, its cutoff and the tape's window.</summary>
    public string Words => WordsOf("the tape is");

    /// <summary>
    /// The same window in the words a refusal of BARS uses (<c>U-bar-holdout</c>): the dataset, its cutoff, and every
    /// pair's bars held back over the same market time.
    /// </summary>
    public string BarWords => WordsOf("every pair's bars, from every dataset and the forward bars, are");

    string WordsOf(string held) =>
        $"dataset {DatasetId} ({Pair} {Interval} {Version}) holds out every bar from {From:u} onwards, so {held} held "
        + (Until is { } end
            ? $"back over the same market time, from {From:u} up to the close of its last bar at {end:u}"
            : $"back from {From:u} with no end, because the dataset does not record where its bars end");
}

/// <summary>
/// WHO IS READING THE TAPE OR THE BARS, AND THE HOLDOUT WINDOWS THEY MAY NOT READ — one argument, required by every reader
/// of tape rows that serves a payload (<c>U-tape-holdout</c>; the orchestrator's ruling of 2026-10-07) and by every reader
/// of bars that may serve one on the agent-facing pipe (<c>U-bar-holdout</c>; seat A's decision of 2026-10-08).
///
/// <para><b>The bars hold the same windows, over every subject.</b> A cutoff on one dataset withholds its own months, and
/// <see cref="Holdout"/> still decides those, unchanged; but the same months reach a caller through another version of the
/// same pair (every Download press records one), through a correlated pair that carries the held period's regime, and
/// through the forward bars when a Download is taken after the collector ran. So a read of ANY dataset, or of the forward
/// bars, whose market span — the open of the first bar it could serve to the close of the last — reaches ANOTHER dataset's
/// window is REFUSED in <see cref="TapeHoldoutWindow.BarWords"/>, never clipped: <see cref="Refusal(DatasetRecord,
/// DateTimeOffset?, DateTimeOffset?)"/> and <see cref="ForwardRefusal"/>. One rule for the tape and the bars, one sentence
/// to the owner.</para>
///
/// <para><b>Why the tape has a holdout at all.</b> The bars' holdout (<see cref="Holdout"/>) withholds a dataset's
/// months from the research process; the tape records the same market's context over the same time — a premium-index
/// row carries the mark price — so once a holdout overlaps what the tape recorded, a tape read would hand over prices
/// from inside the window. Every dataset holding a cutoff therefore holds a tape window (<see cref="TapeHoldoutWindow"/>),
/// open campaign or not, because the bars rule reads the dataset and so does this one.</para>
///
/// <para><b>The rule, and it refuses by default.</b> For an audience that may not read the holdout, a read whose asked
/// SOURCE-time window reaches any window is REFUSED in words naming the dataset, its cutoff and the window — never
/// clipped — whatever the source, series or subject: a feature over any series is evaluation evidence inside the window,
/// GDELT's news items included. An absent end or start reaches every window beyond it. An as-of read is refused when
/// the row it would serve has a source time inside a window. Arrival time is not the test: a late revision of a reading
/// stamped before the window says nothing about the window's market.</para>
///
/// <para><b>Read from the ledger at each read.</b> <see cref="Pipe"/> holds the dataset ledger, not a copy of it, and
/// every <see cref="Refusal(DateTimeOffset?, DateTimeOffset?)"/> reads <see cref="DatasetStore.All"/> afresh — a cutoff
/// the owner set a second ago counts, however long a caller keeps this object.</para>
///
/// <para><b>The audience travels inside, and the one that may read the windows is <c>internal</c>.</b>
/// <see cref="Pipe"/> is every caller on the agent-facing channel, and it is the only public door: it cannot be made
/// without the ledger, so a pipe audience never travels without its windows. <see cref="Referee"/> passes everything and,
/// like <see cref="BarAudience.Referee"/>, no assembly outside <c>TradeAgent.Core</c> can reach it.</para>
/// </summary>
public sealed class TapeHoldout
{
    readonly DatasetStore? _ledger;

    TapeHoldout(BarAudience audience, DatasetStore? ledger)
    {
        Audience = audience;
        _ledger = ledger;
    }

    /// <summary>
    /// The audience — <c>internal</c>, because a public getter returning a <see cref="BarAudience"/> would be a second
    /// door onto one (<c>HoldoutLedgerTests</c> holds that list to <c>BarAudience.Pipe</c>).
    /// </summary>
    internal BarAudience Audience { get; }

    /// <summary>Whether this reader may read inside the windows: the referee's alone.</summary>
    public bool MayReadHoldout => Audience.MayReadHoldout;

    /// <summary>Who this is, in the words a refusal uses.</summary>
    public string Who => Audience.Who;

    /// <summary>
    /// A CALLER ON THE AGENT-FACING PIPE, with the dataset ledger its windows are read from at each read. Never reads
    /// inside a window, whatever role it proved; a null role is a caller that proved nothing, refused the same.
    /// </summary>
    public static TapeHoldout Pipe(string? role, DatasetStore datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        return new TapeHoldout(BarAudience.Pipe(role), datasets);
    }

    /// <summary>
    /// THE REFEREE'S PASS-THROUGH: reads the tape inside every window, in process. Internal, like
    /// <see cref="BarAudience.Referee"/>; no pipe op and no CLI verb is behind it.
    /// </summary>
    internal static TapeHoldout Referee { get; } = new(BarAudience.Referee, null);

    /// <summary>
    /// THE HOLDOUT FOR A RUN WHOSE BARS ARE READ UNDER <paramref name="audience"/> (<c>U-language-v2a</c>, <c>U-bar-holdout</c>):
    /// that same audience, with the dataset ledger its windows are read from at each read. <c>Backtest.Over</c> builds ONE and
    /// hands it to the bars (<see cref="BarFeed.Open"/>) and to the features, so a holdout decided for one is decided for the
    /// other. Internal: <see cref="Pipe"/> stays the only public door, and the referee's audience reaches here only from a
    /// charged verdict — through <c>Backtest.Over</c> and <c>Referee.HoldoutFeed</c>.
    /// </summary>
    internal static TapeHoldout Of(BarAudience audience, DatasetStore datasets)
    {
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(datasets);
        return new TapeHoldout(audience, audience.MayReadHoldout ? null : datasets);
    }

    /// <summary>The windows this reader may not read, as the ledger holds them NOW — none for the referee's.</summary>
    public IReadOnlyList<TapeHoldoutWindow> Windows() =>
        Audience.MayReadHoldout || _ledger is null ? [] : WindowsOf(_ledger.All());

    /// <summary>
    /// THE TAPE WINDOW OF EVERY DATASET HOLDING A CUTOFF, earliest first — whatever its state, its class or its
    /// campaign. A dataset that holds no bar holds no market time; a window that ends where it starts holds nothing.
    /// </summary>
    public static IReadOnlyList<TapeHoldoutWindow> WindowsOf(IEnumerable<DatasetRecord> datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);

        var windows = new List<TapeHoldoutWindow>();
        foreach (var set in datasets)
        {
            if (set.HoldoutFrom is not { } cutoff) continue;
            if (set.Bars == 0 && set.LastBar is null) continue;

            var from = cutoff.ToUniversalTime();
            var until = set.LastBar is { } last && BarLength(set.Interval) is { } length ? last.ToUniversalTime() + length : (DateTimeOffset?)null;
            if (until is { } end && end <= from) continue;

            windows.Add(new TapeHoldoutWindow(set.Id, set.Pair, set.Interval, set.Version, from, until));
        }

        return [.. windows.OrderBy(w => w.From).ThenBy(w => w.DatasetId)];
    }

    /// <summary>One bar of <paramref name="interval"/>, or null for one this build cannot read — which holds the window open.</summary>
    static TimeSpan? BarLength(string interval)
    {
        try { return KlineNormaliser.BarLength(interval); }
        catch (TradeAgentException) { return null; }
    }

    /// <summary>
    /// WHY A READ OF SOURCE TIMES FROM <paramref name="from"/> TO <paramref name="to"/> (both inclusive, either null for
    /// no bound) IS NOT SERVED, in the words the caller reads, or null when it is. Null only for the referee's reader, a
    /// ledger holding no window, or a window that reaches none.
    /// </summary>
    public string? Refusal(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (Audience.MayReadHoldout) return null;

        var reached = Windows().Where(w => w.ReachedBy(from, to)).ToList();
        if (reached.Count == 0) return null;

        var asked = (from, to) switch
        {
            ({ } lo, { } hi) => $"the window asked for runs {lo:u} to {hi:u} in source time",
            ({ } lo, null) => $"the window asked for starts at {lo:u} and names no end",
            (null, { } hi) => $"the window asked for ends at {hi:u} and names no start",
            _ => "the window asked for names neither a start nor an end, so it is the whole tape"
        };

        return string.Join("; ", reached.Select(w => w.Words)) + $" — and {asked}, which reaches "
            + (reached.Count == 1 ? "it. " : "them. ") + Why() + Repair(reached);
    }

    /// <summary>
    /// WHY AN AS-OF READ AT <paramref name="asOf"/> MAY NOT SERVE <paramref name="row"/>, or null when it may: refused
    /// when the row's SOURCE time is inside a window. When it arrived does not matter.
    /// </summary>
    public string? Refusal(TapeObservation row, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Audience.MayReadHoldout) return null;

        var inside = Windows().Where(w => w.Holds(row.SourceTime)).ToList();
        if (inside.Count == 0) return null;

        return string.Join("; ", inside.Select(w => w.Words))
            + $" — and the newest reading of {row.Source} {row.Series} {row.Subject} that had arrived by {asOf:u} is stamped "
            + "inside it. " + Why() + "Ask as of an earlier instant, or read a window of source time outside it.";
    }

    /// <summary>
    /// WHY BARS OF <paramref name="set"/> OPENING FROM <paramref name="from"/> TO <paramref name="to"/> (both inclusive,
    /// either null for no bound) ARE NOT SERVED BECAUSE ANOTHER DATASET HOLDS THEIR MARKET TIME (<c>U-bar-holdout</c>), in
    /// the words the caller reads, or null when they are.
    ///
    /// <para>The set's OWN cutoff is not asked here: <see cref="Holdout.Refusal"/> decides it, first and unchanged. Every
    /// OTHER dataset's window, of any pair, is asked over the read's market span — from <paramref name="from"/> to the close
    /// of the bar opening at <paramref name="to"/>, one bar of the set's interval later. An absent bound reaches every
    /// window on its side, and a set whose bar length this build cannot read spans with no end. Null only for the
    /// referee's reader, a ledger holding no other window, or a span that reaches none.</para>
    /// </summary>
    public string? Refusal(DatasetRecord set, DateTimeOffset? from, DateTimeOffset? to)
    {
        ArgumentNullException.ThrowIfNull(set);
        return BarRefusal($"dataset {set.Id} ({set.Pair} {set.Interval} {set.Version})", set.Id, BarLength(set.Interval), from, to);
    }

    /// <summary>
    /// THE SAME RULE FOR THE FORWARD BARS OF <paramref name="symbol"/> — the minutes TradeAgent collected itself, which
    /// belong to no dataset, so EVERY window is asked. A Download taken after the collector ran holds months the collector
    /// already has: a forward bar does not post-date every freeze.
    /// </summary>
    public string? ForwardRefusal(string symbol, DateTimeOffset? from, DateTimeOffset? to) =>
        BarRefusal($"the forward bars of {symbol}", null, ForwardBars.BarLength, from, to);

    string? BarRefusal(string read, long? own, TimeSpan? length, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (Audience.MayReadHoldout) return null;

        // THE READ'S MARKET SPAN: from the open of the first bar it could serve to the close of the last, one bar after
        // 'to'. A bar length this build cannot read, or a close past the last instant there is, is a span with no end.
        DateTimeOffset? until = to is { } hi && length is { } bar && hi <= DateTimeOffset.MaxValue - bar ? hi + bar : null;

        var reached = Windows().Where(w => w.DatasetId != own && w.Overlaps(from, until)).ToList();
        if (reached.Count == 0) return null;

        var asked = (from, to) switch
        {
            ({ } lo, { } last) => $"the bars asked for, {read} opening from {lo:u} to {last:u}, span the market "
                + (until is { } end ? $"to the close of the last at {end:u}" : "with no end this build can measure"),
            ({ } lo, null) => $"the bars asked for, {read} opening from {lo:u}, name no end",
            (null, { } last) => $"the bars asked for, {read} opening up to {last:u}, name no start",
            _ => $"the bars asked for, {read}, name neither a start nor an end, so they are every bar there is"
        };

        return string.Join("; ", reached.Select(w => w.BarWords)) + $" — and {asked}, which reaches "
            + (reached.Count == 1 ? "it. " : "them. ") + BarsWhy() + BarRepair(reached, length);
    }

    string BarsWhy() =>
        "Bars over a holdout's months are the private evaluation evidence the research process never sees, whichever "
        + "dataset, pair or source serves them: another version of the same months, a correlated pair and the minutes "
        + "TradeAgent collected itself all say what those months did. TradeAgent serves them to no caller on this channel — "
        + $"not to {Who}, not to either director, not to a connection that proved no role at all. This is REFUSED rather "
        + "than quietly cut short at the window, because an answer silently clipped is a different window from the one you "
        + "asked for, and a bar once served can never become holdout again. ";

    static string BarRepair(IReadOnlyList<TapeHoldoutWindow> reached, TimeSpan? length)
    {
        if (reached is not [var one])
            return "Ask for bars wholly outside each of them — the last closing by its start, or the first opening at or "
                   + "after its end, as named above; 'trade data list' names every dataset's 'holdout_from' and last bar.";

        var before = length is { } bar && one.From >= DateTimeOffset.MinValue + bar
            ? $"a 'to' at or before {one.From - bar:u}, so the last bar closes by the cutoff"
            : null;
        var after = one.Until is { } end ? $"a 'from' at or after {end:u}" : null;

        return (before, after) switch
        {
            ({ } b, { } a) => $"Ask for bars wholly outside it: {b}, or {a}.",
            ({ } b, null) => $"Ask for bars that END before it: {b}.",
            (null, { } a) => $"Ask for bars that START after it: {a}.",
            _ => "Ask for bars of a pair and a window 'trade data list' shows outside every dataset's 'holdout_from' and last bar."
        };
    }

    string Why() =>
        "Rows recorded over a holdout are the private evaluation evidence the research process never sees: whatever "
        + "their source, series or subject — a price, a rate, a news item — a row stamped inside the window says what "
        + "those months did. TradeAgent serves them to no caller on this channel — not to "
        + $"{Who}, not to either director, not to a connection that proved no role at all. This is REFUSED rather "
        + "than quietly cut short at the window, because an answer silently clipped is a different window from the one "
        + "you asked for, and a row once served can never become holdout again. ";

    static string Repair(IReadOnlyList<TapeHoldoutWindow> reached) => reached is [var one]
        ? one.Until is { } end
            ? $"Ask for a window wholly outside it: 'to' earlier than {one.From:u}, or 'from' at or after {end:u}."
            : $"Ask for a window that ENDS before it: 'to' earlier than {one.From:u}."
        : "Ask for a window wholly outside each of them — ending before its start or starting at or after its end, as "
          + "named above; 'trade data list' names every dataset's 'holdout_from' and last bar.";
}
