namespace TradeAgent.Core.Db;

public sealed partial class OrgStore
{
    /// <summary>
    /// THE <c>tool-offer</c> DECISION, INSIDE THE LADDER'S WRITE (<see cref="ToolLadder.Offer"/>; <c>U-admission-ladder</c>): a mandatory kind,
    /// written only by the act that offers the tool, so no record of an offer stands without the step behind it. Validated as every decision
    /// is; the rationale is the decider's own ledger entry, marked claim.
    /// </summary>
    internal DecisionRow WriteToolDecision(string decider, string? attempt, IReadOnlyList<string> options, string choice, string rationale,
        DateTimeOffset now) =>
        WriteDecision(decider, attempt, OrgDecisionKind.ToolOffer, options, choice, rationale, [], now);
}
