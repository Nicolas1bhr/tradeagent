using System.Text.Json.Serialization;

namespace TradeAgent.Connectors.Atas;

/// <summary>
/// The wire contract between TradeAgent and the component loaded inside ATAS.
///
/// Direction: TradeAgent hosts the pipe, the bridge dials in. That way the bridge's presence is an
/// observable fact (a connection plus a heartbeat) rather than something the user has to confirm —
/// which is what lets the setup wizard continue by itself once the strategy is actually started.
///
/// This file is compiled into BOTH sides so the shapes cannot drift apart.
/// </summary>
public static class BridgeOps
{
    public const string Hello = "hello", Heartbeat = "heartbeat";
    public const string Accounts = "accounts", Instruments = "instruments", Quote = "quote";
    public const string Positions = "positions", Orders = "orders", Executions = "executions";
    public const string Place = "place", Modify = "modify", Cancel = "cancel", CancelAll = "cancel-all", Close = "close";
}

public static class BridgeEvents
{
    public const string Connection = "connection", Quote = "quote", Order = "order";
    public const string Execution = "execution", Position = "position", Account = "account";
}

public sealed class BridgeFrame
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    /// <summary>True when the bridge is certain the broker refused. Anything else is indefinite.</summary>
    [JsonPropertyName("rejected")] public bool Rejected { get; set; }
    [JsonPropertyName("data")] public System.Text.Json.JsonElement? Data { get; set; }
}

public sealed class BridgeHello
{
    [JsonPropertyName("bridge_protocol_version")] public int BridgeProtocolVersion { get; set; }
    [JsonPropertyName("bridge_version")] public string BridgeVersion { get; set; } = "";
    [JsonPropertyName("atas_version")] public string AtasVersion { get; set; } = "";
    [JsonPropertyName("account_id")] public string? AccountId { get; set; }
    [JsonPropertyName("is_simulated")] public bool IsSimulated { get; set; }
    [JsonPropertyName("supports_client_order_id")] public bool SupportsClientOrderId { get; set; }
    [JsonPropertyName("supports_order_history")] public bool SupportsOrderHistory { get; set; }
    [JsonPropertyName("supports_modify")] public bool SupportsModify { get; set; }
    [JsonPropertyName("supports_close_position")] public bool SupportsClosePosition { get; set; }
}
