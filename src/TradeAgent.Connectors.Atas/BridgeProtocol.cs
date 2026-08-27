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

    /// <summary>
    /// How many orders carrying a client order id this bridge has submitted to ATAS this session,
    /// and how many times it has actually gone looking for one of them in ATAS's own order
    /// collection.
    ///
    /// These exist because <see cref="SupportsClientOrderId"/> is ONE boolean while false is three
    /// different facts: nothing was ever attempted; something was attempted but never came back to
    /// be checked; something was attempted, checked, and the read-back genuinely failed. Only the
    /// last is evidence against ATAS. Without these, a harness has to infer which one it is from
    /// the live order book, and an inference is not a report.
    ///
    /// Null — not zero — when the bridge does not report them. A bridge that says nothing must not
    /// read as one that attempted nothing; that conflation is the same mistake the boolean already
    /// makes, and duplicating it here would defeat the point of the field.
    ///
    /// DIAGNOSTIC ONLY. Nothing derives a capability from these, and nothing may: a counter is not
    /// a round trip, and rule 1 is satisfied by the read-back or not at all.
    /// </summary>
    [JsonPropertyName("client_order_id_attempts")] public int? ClientOrderIdAttempts { get; set; }

    /// <inheritdoc cref="ClientOrderIdAttempts"/>
    [JsonPropertyName("client_order_id_checks")] public int? ClientOrderIdChecks { get; set; }
    [JsonPropertyName("supports_order_history")] public bool SupportsOrderHistory { get; set; }
    [JsonPropertyName("supports_modify")] public bool SupportsModify { get; set; }
    [JsonPropertyName("supports_close_position")] public bool SupportsClosePosition { get; set; }
}

/// <summary>
/// What a bridge speaking the wrong protocol version said about itself. DISPLAY ONLY.
///
/// A mismatched hello is refused: it never becomes <see cref="AtasConnector.Bridge"/>, because
/// <c>Capabilities</c> derives from that and an incompatible bridge's claims must not reach the
/// gateway — a bridge whose protocol this build does not speak cannot be allowed to say what it
/// supports. But refusing the frame outright also threw away the version numbers, and left the user
/// looking at "FAILED" with nothing to act on. Repairing a version mismatch begins with knowing
/// which version is loaded.
///
/// So the identity is kept and the claims are dropped. Nothing here is trusted for any decision.
/// The strings arrive from a peer this build has already declined to speak to, so they are clipped
/// and stripped of anything that is not printable before they are stored — a version string is the
/// one place a hostile or simply broken bridge gets to put text in front of the user.
/// </summary>
public sealed record IncompatibleBridge(int ReportedProtocolVersion, int ExpectedProtocolVersion,
                                        string BridgeVersion, string AtasVersion)
{
    /// <summary>Untrusted text on its way to a label: one line, printable, and short.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var kept = new string(raw.Where(c => !char.IsControl(c)).Take(40).ToArray()).Trim();
        return kept.Length == 0 ? "unknown" : kept;
    }

    public override string ToString() =>
        $"bridge {BridgeVersion} speaks protocol {ReportedProtocolVersion}, this build speaks " +
        $"{ExpectedProtocolVersion} — reinstall the add-on from TradeAgent";
}
