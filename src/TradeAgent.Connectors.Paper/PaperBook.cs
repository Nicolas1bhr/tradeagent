using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.ConnectorSdk;
using TradeAgent.Core;

namespace TradeAgent.Connectors.Paper;

/// <summary>One simulated fill as the book holds it, including the friction it was simulated under.</summary>
public sealed record PaperFill(
    string ExecutionId, string ClientOrderId, string ConnectorOrderId, string Symbol, OrderSide Side,
    decimal Quantity, decimal Price, decimal Fee, DateTimeOffset BarOpenTime, DateTimeOffset At, string Friction);

/// <summary>One average-cost position, as the book holds it. No mark: see <see cref="PaperBook.LastClose"/>.</summary>
public sealed record PaperPosition(string Symbol, decimal Quantity, decimal AveragePrice);

/// <summary>
/// THE PAPER CONNECTOR'S OWN BOOK, IN ITS OWN SQLITE FILE.
///
/// <para><b>It is not a rung of the app's schema and it must not become one.</b> The app's database
/// records what TradeAgent measured and what a broker did; this records a simulation this
/// application performed against itself. Putting it in <c>tradeagent.db</c> would put simulated fills
/// one join away from real ones, and would make every paper experiment a migration. It lives at
/// <c>state/paper-&lt;account&gt;.db</c> and carries its own version INSIDE the file, so a build that
/// finds a newer one says so rather than reading columns it does not understand.</para>
///
/// <para><b>What makes re-processing a bar safe is here, not in the caller.</b>
/// <c>UNIQUE (client_order_id, bar_open_time)</c> on the fills is the whole of the idempotency: the
/// settlement loop replays whatever bars the source hands it, attempts the fill, and takes the
/// constraint's answer as the decision. A caller that filtered bars by a watermark instead would be
/// safe only for as long as the watermark was right, and the one situation this has to survive — a
/// restart, a ledger re-serving from behind — is exactly the situation where it is not.</para>
///
/// <para>Decimals are stored as invariant TEXT. A price, a size and a fee are compared and summed on
/// a money path; SQLite's REAL is a double and would quietly re-round every one of them.</para>
/// </summary>
public sealed class PaperBook : IDisposable
{
    /// <summary>The version of the layout below, written into the file it describes.</summary>
    public const int Schema = 1;

    readonly Lock _gate = new();
    readonly SqliteConnection _conn;

    public string File { get; }
    public string AccountId { get; }
    public string Currency { get; }
    public decimal StartingEquity { get; }

    /// <summary>
    /// Opens (or creates) the book. <paramref name="currency"/> and <paramref name="startingEquity"/>
    /// are DECLARED at creation and then belong to the file: a second instance over the same file
    /// answers the numbers the file already holds, because an account whose starting equity changed
    /// when a different caller constructed it would make its own equity unreadable.
    /// </summary>
    public PaperBook(string file, string accountId, string currency, decimal startingEquity)
    {
        File = file;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        Migrate();

        var found = Meta("schema");
        if (found is { } v && int.Parse(v, CultureInfo.InvariantCulture) > Schema)
            throw new InvalidOperationException(
                $"the paper book at {file} was written by a newer TradeAgent (its layout is version {v}, "
                + $"this build understands {Schema}). Nothing has been read from it.");

        SetMetaIfAbsent("schema", Schema.ToString(CultureInfo.InvariantCulture));
        SetMetaIfAbsent("account", accountId);
        SetMetaIfAbsent("currency", currency);
        SetMetaIfAbsent("starting_equity", D(startingEquity));

        AccountId = Meta("account")!;
        Currency = Meta("currency")!;
        StartingEquity = Dec(Meta("starting_equity"));
    }

    void Migrate() => Exec("""
        CREATE TABLE IF NOT EXISTS paper_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS paper_order (
            client_order_id      TEXT PRIMARY KEY,
            connector_order_id   TEXT NOT NULL UNIQUE,
            symbol               TEXT NOT NULL,
            side                 TEXT NOT NULL,
            type                 TEXT NOT NULL,
            quantity             TEXT NOT NULL,
            filled_quantity      TEXT NOT NULL,
            limit_price          TEXT NULL,
            stop_price           TEXT NULL,
            tif                  TEXT NOT NULL,
            state                TEXT NOT NULL,
            reject_reason        TEXT NULL,
            placed_at            TEXT NOT NULL,
            filled_bar_open_time TEXT NULL);

        CREATE TABLE IF NOT EXISTS paper_fill (
            execution_id       TEXT PRIMARY KEY,
            client_order_id    TEXT NOT NULL,
            connector_order_id TEXT NOT NULL,
            bar_open_time      TEXT NOT NULL,
            symbol             TEXT NOT NULL,
            side               TEXT NOT NULL,
            quantity           TEXT NOT NULL,
            price              TEXT NOT NULL,
            fee                TEXT NOT NULL,
            at                 TEXT NOT NULL,
            friction           TEXT NOT NULL,
            UNIQUE (client_order_id, bar_open_time));

        CREATE TABLE IF NOT EXISTS paper_position (
            symbol        TEXT PRIMARY KEY,
            quantity      TEXT NOT NULL,
            average_price TEXT NOT NULL);
        """);

    // ---- meta ---------------------------------------------------------------------------------

    public string? Meta(string key)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT value FROM paper_meta WHERE key=$k";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT INTO paper_meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    void SetMetaIfAbsent(string key, string value)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO paper_meta(key,value) VALUES($k,$v)";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>The open time of the last bar settled for this symbol, or <see cref="DateTimeOffset.MinValue"/>.</summary>
    public DateTimeOffset SettledThrough(string symbol) =>
        Meta($"settled:{symbol.ToUpperInvariant()}") is { } t ? Time(t) : DateTimeOffset.MinValue;

    /// <summary>The close of the last bar settled for this symbol, or null because none has been.</summary>
    public decimal? LastClose(string symbol) =>
        Meta($"close:{symbol.ToUpperInvariant()}") is { } c ? Dec(c) : null;

    /// <summary>The spacing between the last two settled bars, or null with fewer than two.</summary>
    public TimeSpan? Interval(string symbol) =>
        Meta($"interval:{symbol.ToUpperInvariant()}") is { } s
            ? TimeSpan.FromSeconds(double.Parse(s, CultureInfo.InvariantCulture))
            : null;

    /// <summary>
    /// Records that a bar has been settled — its open time, its close and, once two have been seen,
    /// the spacing between them. The watermark is a HINT handed to the bar source and never the
    /// idempotency: see the type summary.
    /// </summary>
    public void MarkSettled(string symbol, DateTimeOffset openTime, decimal close)
    {
        var key = symbol.ToUpperInvariant();
        var previous = SettledThrough(key);
        if (previous > DateTimeOffset.MinValue && openTime > previous)
            SetMeta($"interval:{key}", (openTime - previous).TotalSeconds.ToString(CultureInfo.InvariantCulture));
        if (openTime >= previous)
        {
            SetMeta($"settled:{key}", T(openTime));
            SetMeta($"close:{key}", D(close));
        }
    }

    /// <summary>Every symbol this book has ever settled a bar for, ordered for a stable settlement pass.</summary>
    public IReadOnlyList<string> SymbolsSeen()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT substr(key,9) FROM paper_meta WHERE key LIKE 'settled:%' ORDER BY key";
            var found = new List<string>();
            using var r = c.ExecuteReader();
            while (r.Read()) found.Add(r.GetString(0));
            return found;
        }
    }

    // ---- orders -------------------------------------------------------------------------------

    /// <summary>The next connector order id, taken inside the book so two callers cannot be given one id.</summary>
    public string NextOrderId()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = """
                INSERT INTO paper_meta(key,value) VALUES('seq','1')
                  ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
                RETURNING value
                """;
            return "PB-" + (string)c.ExecuteScalar()!;
        }
    }

    /// <summary>Writes a new order. False when this client order id is already in the book.</summary>
    public bool TryInsert(OrderInfo o, TimeInForce tif)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = """
                INSERT OR IGNORE INTO paper_order
                  (client_order_id, connector_order_id, symbol, side, type, quantity, filled_quantity,
                   limit_price, stop_price, tif, state, reject_reason, placed_at, filled_bar_open_time)
                VALUES ($coid,$oid,$sym,$side,$type,$qty,$filled,$lim,$stop,$tif,$state,NULL,$at,NULL)
                """;
            c.Parameters.AddWithValue("$coid", o.ClientOrderId!);
            c.Parameters.AddWithValue("$oid", o.ConnectorOrderId);
            c.Parameters.AddWithValue("$sym", o.Symbol);
            c.Parameters.AddWithValue("$side", o.Side.ToString());
            c.Parameters.AddWithValue("$type", o.Type.ToString());
            c.Parameters.AddWithValue("$qty", D(o.Quantity));
            c.Parameters.AddWithValue("$filled", D(o.FilledQuantity));
            c.Parameters.AddWithValue("$lim", (object?)(o.LimitPrice is { } l ? D(l) : null) ?? DBNull.Value);
            c.Parameters.AddWithValue("$stop", (object?)(o.StopPrice is { } s ? D(s) : null) ?? DBNull.Value);
            c.Parameters.AddWithValue("$tif", tif.ToString());
            c.Parameters.AddWithValue("$state", o.State.ToString());
            c.Parameters.AddWithValue("$at", T(o.At));
            return c.ExecuteNonQuery() == 1;
        }
    }

    public void SetState(string clientOrderId, ExecutionState state) => Set(clientOrderId, "state", state.ToString());

    public void SetPrices(string clientOrderId, decimal? limit, decimal? stop, decimal quantity)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE paper_order SET limit_price=$l, stop_price=$s, quantity=$q WHERE client_order_id=$k";
            c.Parameters.AddWithValue("$l", (object?)(limit is { } l ? D(l) : null) ?? DBNull.Value);
            c.Parameters.AddWithValue("$s", (object?)(stop is { } s ? D(s) : null) ?? DBNull.Value);
            c.Parameters.AddWithValue("$q", D(quantity));
            c.Parameters.AddWithValue("$k", clientOrderId);
            c.ExecuteNonQuery();
        }
    }

    void Set(string clientOrderId, string column, string value)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"UPDATE paper_order SET {column}=$v WHERE client_order_id=$k";
            c.Parameters.AddWithValue("$v", value);
            c.Parameters.AddWithValue("$k", clientOrderId);
            c.ExecuteNonQuery();
        }
    }

    const string OrderCols = """
        client_order_id, connector_order_id, symbol, side, type, quantity, filled_quantity,
        limit_price, stop_price, tif, state, reject_reason, placed_at
        """;

    /// <summary>
    /// Every order this file holds, back to <paramref name="since"/> — and REALLY back to it. The
    /// file holds every order ever placed on this account and nothing prunes it, which is what
    /// <c>SupportsOrderHistory = true</c> is entitled to mean and nothing less.
    /// </summary>
    public IReadOnlyList<OrderInfo> Orders(bool includeInactive, DateTimeOffset? since)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"SELECT {OrderCols} FROM paper_order ORDER BY placed_at, connector_order_id";
            var all = new List<OrderInfo>();
            using var r = c.ExecuteReader();
            while (r.Read()) all.Add(ReadOrder(r));
            return [.. all
                .Where(o => includeInactive || o.State is not (ExecutionState.FILLED or ExecutionState.CANCELLED or ExecutionState.REJECTED))
                .Where(o => since is null || o.At >= since)];
        }
    }

    public OrderInfo? ByClientOrderId(string clientOrderId) => One("client_order_id", clientOrderId);
    public OrderInfo? ByConnectorOrderId(string connectorOrderId) => One("connector_order_id", connectorOrderId);

    OrderInfo? One(string column, string value)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = $"SELECT {OrderCols} FROM paper_order WHERE {column}=$v";
            c.Parameters.AddWithValue("$v", value);
            using var r = c.ExecuteReader();
            return r.Read() ? ReadOrder(r) : null;
        }
    }

    OrderInfo ReadOrder(SqliteDataReader r) => new(
        r.GetString(1), r.GetString(0), AccountId, r.GetString(2),
        Enum.Parse<OrderSide>(r.GetString(3)), Enum.Parse<OrderType>(r.GetString(4)),
        Dec(r.GetString(5)), Dec(r.GetString(6)),
        r.IsDBNull(7) ? null : Dec(r.GetString(7)),
        r.IsDBNull(8) ? null : Dec(r.GetString(8)),
        Enum.Parse<ExecutionState>(r.GetString(10)),
        r.IsDBNull(11) ? null : r.GetString(11),
        Time(r.GetString(12)));

    /// <summary>The time-in-force the order was placed with. Read apart because <see cref="OrderInfo"/> carries none.</summary>
    public TimeInForce Tif(string clientOrderId)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT tif FROM paper_order WHERE client_order_id=$k";
            c.Parameters.AddWithValue("$k", clientOrderId);
            var v = c.ExecuteScalar() as string;
            return v is not null && Enum.TryParse<TimeInForce>(v, out var t) ? t : TimeInForce.Day;
        }
    }

    // ---- fills and positions ------------------------------------------------------------------

    /// <summary>
    /// True when this order already filled on a bar EARLIER than the one being settled. That is the
    /// only order-level guard the settlement loop has, and it is deliberately not "has it filled at
    /// all": whether it filled on THIS bar is what the fills' unique key answers, and answering it
    /// here instead would make a replay silently safe for a reason no constraint enforces.
    /// </summary>
    public bool FilledBefore(string clientOrderId, DateTimeOffset barOpenTime)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT 1 FROM paper_fill WHERE client_order_id=$k AND bar_open_time < $t LIMIT 1";
            c.Parameters.AddWithValue("$k", clientOrderId);
            c.Parameters.AddWithValue("$t", T(barOpenTime));
            return c.ExecuteScalar() is not null;
        }
    }

    /// <summary>
    /// Records a fill and everything that follows from it — the order's state, the average-cost
    /// position, the realised amount and the fee — in ONE transaction, and answers NULL when this
    /// (order, bar) pair is already in the file. The fill that comes back carries the execution id
    /// the book gave it.
    ///
    /// <para>The refusal is the <c>UNIQUE (client_order_id, bar_open_time)</c> constraint's, taken
    /// from <c>INSERT OR IGNORE</c>'s row count rather than decided beforehand: a check followed by
    /// an insert is two statements a restart can land between, and this has to be safe against
    /// exactly that.</para>
    ///
    /// <para><b>The execution id is a sequence and is deliberately NOT derived from the order and the
    /// bar.</b> An id spelled <c>PX-&lt;order&gt;-&lt;bar&gt;</c> reads well and makes the PRIMARY KEY
    /// a second constraint enforcing the same fact — which means dropping the named one changes
    /// nothing, and the guarantee stops being the one anybody reading this file would check. The id
    /// names the ROW; the unique key names the FACT.</para>
    /// </summary>
    public PaperFill? TryFill(PaperFill fill)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            fill = fill with { ExecutionId = "PX-" + Next(tx, "xseq") };

            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO paper_fill
                      (execution_id, client_order_id, connector_order_id, bar_open_time, symbol, side,
                       quantity, price, fee, at, friction)
                    VALUES ($x,$coid,$oid,$bar,$sym,$side,$qty,$px,$fee,$at,$fr)
                    """;
                ins.Parameters.AddWithValue("$x", fill.ExecutionId);
                ins.Parameters.AddWithValue("$coid", fill.ClientOrderId);
                ins.Parameters.AddWithValue("$oid", fill.ConnectorOrderId);
                ins.Parameters.AddWithValue("$bar", T(fill.BarOpenTime));
                ins.Parameters.AddWithValue("$sym", fill.Symbol);
                ins.Parameters.AddWithValue("$side", fill.Side.ToString());
                ins.Parameters.AddWithValue("$qty", D(fill.Quantity));
                ins.Parameters.AddWithValue("$px", D(fill.Price));
                ins.Parameters.AddWithValue("$fee", D(fill.Fee));
                ins.Parameters.AddWithValue("$at", T(fill.At));
                ins.Parameters.AddWithValue("$fr", fill.Friction);
                if (ins.ExecuteNonQuery() == 0) { tx.Rollback(); return null; }
            }

            using (var ord = _conn.CreateCommand())
            {
                ord.Transaction = tx;
                ord.CommandText = """
                    UPDATE paper_order
                       SET filled_quantity=$q, state='FILLED', filled_bar_open_time=$bar
                     WHERE client_order_id=$coid
                    """;
                ord.Parameters.AddWithValue("$q", D(fill.Quantity));
                ord.Parameters.AddWithValue("$bar", T(fill.BarOpenTime));
                ord.Parameters.AddWithValue("$coid", fill.ClientOrderId);
                ord.ExecuteNonQuery();
            }

            ApplyToPosition(tx, fill);
            if (fill.Fee != 0m) AddTo(tx, "fees", fill.Fee);

            tx.Commit();
            return fill;
        }
    }

    /// <summary>The next number of a counter, inside the caller's transaction so a rollback takes it back.</summary>
    static string Next(SqliteTransaction tx, string key)
    {
        using var c = tx.Connection!.CreateCommand();
        c.Transaction = tx;
        c.CommandText = """
            INSERT INTO paper_meta(key,value) VALUES($k,'1')
              ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            RETURNING value
            """;
        c.Parameters.AddWithValue("$k", key);
        return (string)c.ExecuteScalar()!;
    }

    /// <summary>
    /// Adds to a running decimal total inside the transaction that produced it. The sum is taken in
    /// C# and written back as TEXT: an <c>UPDATE ... CAST(value AS REAL)</c> would add every fee and
    /// every realised amount as a DOUBLE, which is the one thing storing them as text was for.
    /// </summary>
    void AddTo(SqliteTransaction tx, string key, decimal amount)
    {
        decimal now;
        using (var read = _conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT value FROM paper_meta WHERE key=$k";
            read.Parameters.AddWithValue("$k", key);
            now = Dec(read.ExecuteScalar() as string);
        }
        using var write = _conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = "INSERT INTO paper_meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        write.Parameters.AddWithValue("$k", key);
        write.Parameters.AddWithValue("$v", D(now + amount));
        write.ExecuteNonQuery();
    }

    /// <summary>
    /// The average-cost book, and the realised amount a reduction produces. Adding to a position
    /// moves the average; reducing one realises against it; a reversal realises the whole of what was
    /// held and starts the new side at the fill price.
    /// </summary>
    void ApplyToPosition(SqliteTransaction tx, PaperFill fill)
    {
        var signed = fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;
        var (held, average) = PositionOf(tx, fill.Symbol);
        decimal realised = 0m;
        decimal nowHeld, nowAverage;

        if (held == 0m) { nowHeld = signed; nowAverage = fill.Price; }
        else if (Math.Sign(held) == Math.Sign(signed))
        {
            nowHeld = held + signed;
            nowAverage = (average * Math.Abs(held) + fill.Price * fill.Quantity) / Math.Abs(nowHeld);
        }
        else
        {
            var closed = Math.Min(Math.Abs(held), fill.Quantity);
            realised = (fill.Price - average) * closed * Math.Sign(held);
            nowHeld = held + signed;
            nowAverage = nowHeld == 0m ? 0m : Math.Sign(nowHeld) == Math.Sign(held) ? average : fill.Price;
        }

        using (var p = _conn.CreateCommand())
        {
            p.Transaction = tx;
            if (nowHeld == 0m)
            {
                p.CommandText = "DELETE FROM paper_position WHERE symbol=$s";
                p.Parameters.AddWithValue("$s", fill.Symbol);
            }
            else
            {
                p.CommandText = """
                    INSERT INTO paper_position(symbol,quantity,average_price) VALUES($s,$q,$a)
                      ON CONFLICT(symbol) DO UPDATE SET quantity=$q, average_price=$a
                    """;
                p.Parameters.AddWithValue("$s", fill.Symbol);
                p.Parameters.AddWithValue("$q", D(nowHeld));
                p.Parameters.AddWithValue("$a", D(nowAverage));
            }
            p.ExecuteNonQuery();
        }

        if (realised != 0m) AddTo(tx, "realised", realised);
    }

    (decimal Held, decimal Average) PositionOf(SqliteTransaction tx, string symbol)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT quantity, average_price FROM paper_position WHERE symbol=$s";
        c.Parameters.AddWithValue("$s", symbol);
        using var r = c.ExecuteReader();
        return r.Read() ? (Dec(r.GetString(0)), Dec(r.GetString(1))) : (0m, 0m);
    }

    public IReadOnlyList<PaperPosition> Positions()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT symbol, quantity, average_price FROM paper_position ORDER BY symbol";
            var found = new List<PaperPosition>();
            using var r = c.ExecuteReader();
            while (r.Read()) found.Add(new PaperPosition(r.GetString(0), Dec(r.GetString(1)), Dec(r.GetString(2))));
            return found;
        }
    }

    /// <summary>
    /// Every fill back to <paramref name="since"/>, and really back to it: nothing prunes this table,
    /// which is what <c>SupportsOrderHistory = true</c> has to be able to promise about executions as
    /// well as orders.
    /// </summary>
    public IReadOnlyList<PaperFill> Fills(DateTimeOffset? since)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = """
                SELECT execution_id, client_order_id, connector_order_id, symbol, side, quantity,
                       price, fee, bar_open_time, at, friction
                  FROM paper_fill ORDER BY bar_open_time, execution_id
                """;
            var found = new List<PaperFill>();
            using var r = c.ExecuteReader();
            while (r.Read())
                found.Add(new PaperFill(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    Enum.Parse<OrderSide>(r.GetString(4)), Dec(r.GetString(5)), Dec(r.GetString(6)),
                    Dec(r.GetString(7)), Time(r.GetString(8)), Time(r.GetString(9)), r.GetString(10)));
            return [.. found.Where(f => since is null || f.At >= since)];
        }
    }

    /// <summary>What closed trades have realised, in the account's currency. Zero until one closes.</summary>
    public decimal Realised => Meta("realised") is { } v ? Dec(v) : 0m;

    /// <summary>What the declared fee fraction has charged in total. Zero on a frictionless book.</summary>
    public decimal Fees => Meta("fees") is { } v ? Dec(v) : 0m;

    // ---- plumbing -----------------------------------------------------------------------------

    void Exec(string sql)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    internal static string D(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    internal static decimal Dec(string? s) => string.IsNullOrEmpty(s) ? 0m : decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static string T(DateTimeOffset d) => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset Time(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public void Dispose()
    {
        lock (_gate)
        {
            _conn.Close();
            _conn.Dispose();
        }
    }
}
