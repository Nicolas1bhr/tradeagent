using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeAgent.Core;

/// <summary>
/// Agent-facing IPC. One JSON object per line over a named pipe (Unix domain socket on macOS/Linux).
/// The first frame must be <c>hello</c> carrying the shared token; anything else is refused.
/// Operator authority (mode changes, kill switch, approvals) is deliberately NOT on this channel.
/// </summary>
public static class Ops
{
    public const string Hello = "hello";
    public const string Status = "status", Connectors = "connectors", Accounts = "accounts", Account = "account";
    public const string Instruments = "instruments", Quote = "quote";
    public const string Positions = "positions", Position = "position";
    public const string Orders = "orders", Order = "order", Executions = "executions";
    public const string Buy = "buy", Sell = "sell", Modify = "modify", Cancel = "cancel", CancelAll = "cancel-all";
    public const string Close = "close", CloseAll = "close-all";
    public const string Schema = "schema";

    public static readonly string[] Mutating = [Buy, Sell, Modify, Cancel, CancelAll, Close, CloseAll];
    public static bool IsMutating(string op) => Mutating.Contains(op);
}

public sealed class IpcRequest
{
    [JsonPropertyName("v")] public int V { get; set; } = Versions.ProtocolVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("n");
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("args")] public Dictionary<string, JsonElement>? Args { get; set; }

    public string? Str(string k) =>
        Args is not null && Args.TryGetValue(k, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    public decimal? Dec(string k)
    {
        if (Args is null || !Args.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
        return decimal.TryParse(v.GetString(), out var d) ? d : null;
    }
}

public sealed class IpcError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("user_message")] public string UserMessage { get; set; } = "";
    [JsonPropertyName("repair")] public string Repair { get; set; } = "";
    [JsonPropertyName("auto_repairable")] public bool AutoRepairable { get; set; }

    public static IpcError From(ErrorInfo i) => new()
    {
        Code = i.Code.ToString(), Message = i.Technical, UserMessage = i.UserMessage,
        Repair = i.Repair, AutoRepairable = i.AutoRepairable
    };
}

public sealed class IpcResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = Versions.ProtocolVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("data")] public object? Data { get; set; }
    [JsonPropertyName("error")] public IpcError? Error { get; set; }

    public static IpcResponse Success(string id, object? data) => new() { Id = id, Ok = true, Data = data };
    public static IpcResponse Fail(string id, ErrorInfo i) => new() { Id = id, Ok = false, Error = IpcError.From(i) };
    public static IpcResponse Fail(string id, ErrorCode c, string? tech = null) => Fail(id, Errors.Get(c, tech));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    public static string Write(object? o, bool pretty = false) => JsonSerializer.Serialize(o, pretty ? Pretty : Options);
    public static T? Read<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
}
