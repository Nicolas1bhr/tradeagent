using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// Machine-readable description of the trading surface, served over IPC and by <c>trade schema --json</c>.
/// It exists so an agent can discover what it may do at runtime instead of relying on a prompt that
/// silently drifts out of date as this software changes.
/// </summary>
public static class GatewaySchema
{
    public sealed record ArgSpec(string Name, string Type, bool Required, string Description);
    public sealed record OpSpec(string Op, string Cli, bool Mutating, string Description, ArgSpec[] Args);

    public static object Describe(GatewayStatus? status = null) => new
    {
        protocol_version = Versions.ProtocolVersion,
        app_version = Versions.App,
        transport = "newline-delimited JSON over a local named pipe; first frame must be 'hello' with the token",
        idempotency = "every mutating call takes request_id; repeating a request_id returns the original outcome and never places a second order",
        execution_states = Enum.GetNames<ExecutionState>(),
        unknown_state_meaning = "UNKNOWN means TradeAgent cannot yet confirm the order. It never means the order failed. Trading pauses until it is reconciled.",
        trading_modes = Enum.GetNames<TradingMode>(),
        current = status,
        operations = Ops(),
    };

    public static OpSpec[] Ops() =>
    [
        new(Core.Ops.Status,      "trade status",              false, "Everything at a glance: mode, health, whether execution is allowed.", []),
        new(Core.Ops.Connectors,  "trade connectors",          false, "Trading backends TradeAgent knows about.", []),
        new(Core.Ops.Accounts,    "trade accounts",            false, "Accounts visible on the connected platform.", []),
        new(Core.Ops.Account,     "trade account",             false, "The selected account, with balance and equity.", []),
        new(Core.Ops.Instruments, "trade instruments",         false, "Tradable instruments, with tick size and contract size.", []),
        new(Core.Ops.Quote,       "trade quote <symbol>",      false, "Current bid/ask/last. Check the timestamp before you size anything.",
            [new("symbol", "string", true, "Instrument symbol, e.g. ES")]),
        new(Core.Ops.Positions,   "trade positions",           false, "Open positions.", []),
        new(Core.Ops.Position,    "trade position <symbol>",   false, "One position by symbol.",
            [new("symbol", "string", true, "Instrument symbol")]),
        new(Core.Ops.Orders,      "trade orders",              false, "Working orders. Pass --all to include finished ones.",
            [new("all", "bool", false, "Include inactive/finished orders")]),
        new(Core.Ops.Order,       "trade order <id>",          false, "One order, by request id or by broker order id.",
            [new("id", "string", true, "Request id or connector order id")]),
        new(Core.Ops.Executions,  "trade executions",          false, "Fills on the account.", []),

        new(Core.Ops.Buy,  "trade buy <symbol> <qty>",  true, "Buy. Market unless you pass --limit or --stop.",
        [
            new("symbol", "string", true, "Instrument symbol"),
            new("quantity", "number", true, "Contracts or shares"),
            new("limit", "number", false, "Limit price"),
            new("stop", "number", false, "Stop price"),
            new("tif", "string", false, "Day | GoodTillCancel | ImmediateOrCancel | FillOrKill"),
            new("request_id", "string", false, "Idempotency key. Reuse it to retry safely; a new one places a new order."),
            new("comment", "string", false, "Free text stored with the request")
        ]),
        new(Core.Ops.Sell, "trade sell <symbol> <qty>", true, "Sell. Same arguments as buy.", []),
        new(Core.Ops.Modify, "trade modify <id>", true, "Change quantity or price on a working order.",
        [
            new("id", "string", true, "Request id or connector order id"),
            new("quantity", "number", false, "New quantity"),
            new("limit", "number", false, "New limit price"),
            new("stop", "number", false, "New stop price")
        ]),
        new(Core.Ops.Cancel,    "trade cancel <id>", true, "Cancel one working order.",
            [new("id", "string", true, "Request id or connector order id")]),
        new(Core.Ops.CancelAll, "trade cancel-all",  true, "Cancel every working order on the account.", []),
        new(Core.Ops.Close,     "trade close <symbol>", true, "Flatten one position with a market order.",
            [new("symbol", "string", true, "Instrument symbol")]),
        new(Core.Ops.CloseAll,  "trade close-all", true, "Flatten every position.", []),
        new(Core.Ops.Schema,    "trade schema", false, "This description.", []),
    ];
}
