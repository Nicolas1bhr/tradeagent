using System.Text.Json.Serialization;
using TradeAgent.Core;

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

    /// <summary>
    /// MEASUREMENT ONLY, and it places a real order to take the measurement.
    ///
    /// Identical to <see cref="Place"/> in every respect a broker can see — same command, same
    /// pre-flight refusals, same acknowledgement wait, same write-ahead record — except that the
    /// bridge submits it through <c>ITradingManager.OpenOrderAsync</c> instead of the obsolete
    /// synchronous <c>OpenOrder</c>. It exists to answer one question that cannot be answered by
    /// reading anything: does that task complete on SUBMISSION or on broker ACKNOWLEDGEMENT? The
    /// four obsolete synchronous call sites cannot be given a deadline, so a block inside one wedges
    /// the bridge's frame loop; flipping them to the Async overloads is what lets
    /// <c>AtasCall.Block</c> reach them, and whether that is safe turns entirely on this answer.
    ///
    /// WHY IT IS A SEPARATE OP RATHER THAN A FLAG ON <see cref="Place"/>. A flag would put a second
    /// way to submit an order inside the one method the gateway calls, reachable from the wire, on
    /// the money path — which is exactly where a rule-3 misclassification hides. As a separate op it
    /// is reachable only by a caller that names it, and the adapter's public <c>Place(cmd)</c> can go
    /// on being auditable in a single line.
    ///
    /// NOTHING IN THE PRODUCT SENDS THIS. <see cref="AtasConnector.PlaceOrderAsync"/> — the only
    /// placement <c>ITradingConnector</c> exposes, and therefore the only one TradingGateway can
    /// reach — sends <see cref="Place"/>. This op is sent by <c>tools/probe</c> and by nothing else.
    /// </summary>
    public const string PlaceViaAsyncOverload = "place-via-async-overload";
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
    /// <summary>
    /// Which ATAS surface the adapter actually bound to at runtime, in plain words, plus what it
    /// found there. Free text, diagnostic only, and nothing derives a capability from it.
    ///
    /// It exists because of the defect that cost a whole live run: ChartStrategy.Connector EXISTS,
    /// has the right type, compiles, and is null — so every read failed with "this chart has no
    /// trading connection attached yet" on a chart that was demonstrably attached to a portfolio.
    /// A capability boolean cannot say "I looked at the wrong object"; this can. When a read fails,
    /// this is the field that says whether the adapter had anything to read from in the first place.
    ///
    /// Null — not empty — when the bridge does not report it, so an older bridge does not read as
    /// one that bound to nothing.
    /// </summary>
    [JsonPropertyName("trading_surface")] public string? TradingSurface { get; set; }

    /// <summary>
    /// THE WRITE-AHEAD RECORD COULD NOT BE WRITTEN, in one line naming the file. Null — not empty —
    /// when the bridge has no such trouble or does not report the field at all, so an older bridge
    /// reads as "nothing to say" rather than as a failure.
    ///
    /// It exists because the refusal it describes is otherwise silent. <c>Place</c> now declines an
    /// order whose client order id could not be recorded, which is right — rule 1 rests on that
    /// record — but a permanent local failure at the witness path refuses EVERY order forever, and
    /// without a route to the screen the owner sees orders failing and no reason anywhere. This is
    /// that route: it lands in the ATAS bridge health row, which is where somebody is already
    /// looking when trading stops.
    ///
    /// DIAGNOSTIC ONLY. No capability derives from it, and it is free text from the bridge, so it is
    /// cleaned before it reaches a label.
    /// </summary>
    [JsonPropertyName("witness_failure")] public string? WitnessFailure { get; set; }

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
    /// <summary>
    /// Untrusted text on its way to a label: one line, printable, and short.
    ///
    /// <paramref name="max"/> defaults to the version-string length this was written for. A caller
    /// that needs a sentence — the witness-failure row has to name a file path — asks for more,
    /// which changes how much is kept and nothing about what is stripped.
    /// </summary>
    public static string Clean(string? raw, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var kept = new string(raw.Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim();
        return kept.Length == 0 ? "unknown" : kept;
    }

    /// <summary>
    /// The sentence the owner reads when a bridge is refused, and the one that has to be actionable:
    /// a protocol bump refuses EVERY bridge deployed before it, so this line is what the whole
    /// installed base sees on the morning after an update.
    ///
    /// It used to end "reinstall the add-on from TradeAgent" — the right diagnosis pointed at no
    /// control. "Add-on" is not what anything else in the product calls this, and reinstalling was
    /// possible only inside the setup wizard, which an owner past setup can never open again. Both
    /// halves are now the same repair the row and the Checks page name, with its on-screen label.
    /// </summary>
    public override string ToString() =>
        $"bridge {BridgeVersion} speaks protocol {ReportedProtocolVersion}, this build speaks " +
        $"{ExpectedProtocolVersion} — press {Labels.ReinstallBridge} on the Checks page";
}
