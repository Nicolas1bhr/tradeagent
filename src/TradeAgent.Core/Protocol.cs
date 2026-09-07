using System.Globalization;
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

    /// <summary>
    /// What the trading actually made or lost, out of the fill ledger. A READ — it changes nothing —
    /// and the only op that answers in money rather than in orders.
    /// </summary>
    public const string Pnl = "pnl";
    public const string Buy = "buy", Sell = "sell", Modify = "modify", Cancel = "cancel", CancelAll = "cancel-all";
    public const string Close = "close", CloseAll = "close-all";
    public const string Schema = "schema";

    /// <summary>
    /// The material ledger. <c>MaterialList</c> reads what TradeAgent observed on disk;
    /// <c>MaterialNote</c> lets the agent record what it did with a file. A note is a CLAIM and is
    /// stored as one — it can never alter an observation, so nothing an agent writes here can make
    /// the record say a file was something it was not.
    /// </summary>
    public const string MaterialList = "material-list", MaterialNote = "material-note";

    /// <summary>
    /// THE MARKET DATA LEDGER, AND IT IS READ-ONLY ON THIS CHANNEL. <c>DataList</c> serves what this
    /// installation collected and where every byte of it came from; <c>DataBars</c> serves the bars
    /// themselves. Neither writes anything, and there is no op on this pipe that collects, normalises,
    /// rejects or deletes a dataset — the account owner presses that in TradeAgent's own window.
    ///
    /// The asymmetry is deliberate and is the same one <c>material</c> makes: an agent that could
    /// edit the provenance of the data its strategies are judged on could report a clean twelve
    /// months over three with the holes filled in.
    /// </summary>
    public const string DataList = "data-list", DataBars = "data-bars";

    public static readonly string[] Mutating = [Buy, Sell, Modify, Cancel, CancelAll, Close, CloseAll];
    public static bool IsMutating(string op) => Mutating.Contains(op);
}

public sealed class IpcRequest
{
    /// <summary>
    /// THE VERSION IS A THING THE PEER SAYS, NOT A THING THIS BUILD ASSUMES ON ITS BEHALF (Codex F13).
    ///
    /// The initializer is the OUTGOING default — everything this product constructs and sends stamps
    /// the version it was built against, which is why no client needed changing. <see cref="JsonRequired"/>
    /// is the INCOMING rule, and it is the whole fix: without it a frame that omitted <c>v</c>
    /// deserialized straight onto this default, so the gateway's <c>req.V != ProtocolVersion</c> check
    /// compared the current version against itself and a versionless peer walked through the one
    /// check that must never be optional. Measured over the real pipe before this changed: a hello
    /// carrying no <c>v</c> at all was answered
    /// <c>{"ok":true,"data":{"protocol_version":1,...,"compatible":true}}</c>.
    ///
    /// It is enforced on the TYPE rather than in the hello handler, deliberately. Every other field
    /// on this frame is read on the strength of both ends agreeing what the frame IS, so a frame that
    /// never said is unreadable wherever it arrives — not merely at the handshake.
    /// </summary>
    [JsonRequired]
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

    /// <summary>
    /// A NUMBER THIS FRAME CARRIES. Absent is null; PRESENT AND UNREADABLE IS A REFUSAL, never a
    /// null that the caller's default then fills in (Codex F6).
    ///
    /// It was <c>decimal.TryParse(...) ? d : null</c>, which cannot tell "no price was sent" from
    /// "a price was sent and this build could not read it" — and those are two different orders.
    /// <c>limit: "bad"</c> became null, the order type is derived from which prices are PRESENT, so
    /// the frame that asked to rest at a price bought at the market instead. Measured over the real
    /// pipe before this changed: <c>limit='bad' -> ok=True · connector saw: Market limit=none</c>.
    ///
    /// STRICT, AND INVARIANT, which is the second half of the same defect. The framework default is
    /// <see cref="NumberStyles.Number"/> — thousands separators and surrounding whitespace included —
    /// so <c>limit: "1,5"</c> was accepted as <b>15</b>, measured over the pipe on this machine.
    ///
    /// The culture is named rather than inherited, and that is a smaller claim than it looks:
    /// <c>Directory.Build.props</c> sets <c>InvariantGlobalization=true</c> for every project, so the
    /// ambient culture already IS the invariant one wherever this ships — a line written for startup
    /// cost, holding up the reading of a price. Naming it here moves that guarantee to the call site,
    /// where the next person to change a build property cannot silently take it away, and
    /// <c>A_price_is_read_the_same_way_whatever_the_threads_culture_says</c> is the run that holds it
    /// to it under a culture whose decimal separator is a comma.
    ///
    /// Names of the styles rather than a bare TryParse, for the same reason <c>NamedValue</c> matches
    /// names rather than parsing: a value with a stray character in it and a value the caller meant
    /// are not distinguishable from here.
    ///
    /// JSON <c>null</c> is a refusal too, not an absence. The frame named the field; a field named
    /// with no value in it is the caller saying something this end cannot act on, and the whole rule
    /// is that such a frame is answered rather than half-read.
    /// </summary>
    public decimal? Dec(string k)
    {
        if (Args is null || !Args.TryGetValue(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetDecimal(out var n) ? n : throw Unreadable(k, v.GetRawText());
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s
            && decimal.TryParse(s, Numeric, CultureInfo.InvariantCulture, out var d)) return d;
        throw Unreadable(k, v.GetRawText());
    }

    /// <summary>A sign, digits and at most one <c>.</c> — no separators, no exponent, no whitespace.</summary>
    const NumberStyles Numeric = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    static TradeAgentException Unreadable(string k, string raw) => new(ErrorCode.INVALID_REQUEST,
        $"'{k}' is not a number this build can read: {raw}. Send digits with an optional leading sign " +
        "and at most one '.' — no thousands separators, no exponent and no surrounding spaces. TradeAgent " +
        "does not read an unreadable price as an absent one: that would place a different order from the " +
        "one you asked for.");
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
