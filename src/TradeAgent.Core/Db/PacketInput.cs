namespace TradeAgent.Core.Db;

/// <summary>
/// THE KINDS OF RECORD A POSITION'S PACKET SHOWS (<c>U-org-packets-b</c>; ORCHESTRATION § 2.6, "input lineage"), a closed list the
/// <c>ai_attempt_input</c> CHECK repeats word for word.
/// </summary>
public static class PacketInputKind
{
    public const string Lateral = "lateral", Forecast = "forecast", Resolution = "resolution", Escalation = "escalation",
        Delivery = "delivery", Decision = "decision", Ledger = "ledger", MandateEvent = "mandate-event";

    public static readonly string[] All = [Lateral, Forecast, Resolution, Escalation, Delivery, Decision, Ledger, MandateEvent];

    public static bool IsKnown(string? kind) => kind is not null && Array.IndexOf(All, kind) >= 0;
}

/// <summary>One record a packet showed, by reference: its kind (<see cref="PacketInputKind"/>) and its id — never a copy (§ 8).</summary>
public sealed record PacketInput(string Kind, string RefId);
