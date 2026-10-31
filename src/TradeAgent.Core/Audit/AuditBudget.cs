using TradeAgent.Core.Db;

namespace TradeAgent.Core.Audit;

/// <summary>
/// THE AUDIT LINE'S BUDGET, CARVED OFF THE TOP (<c>U-org-audit-b</c>; ORCHESTRATION § 2.13 "its own owner-set budget beside the
/// organisation's"; VISION § 5.3 "its budget never the board's to set"; the owner's Q1 of 2026-10-22: off the top of the daily cap, default 0,
/// two presses to raise). One figure per billing class — real money off the money cap, plan capacity off the plan's day-total — because a
/// list-price equivalent is never added to money (COUNCIL rule 4). What the organisation (every position, the security board's seats and the
/// app's perception alike) is admitted against is that class's ceiling LESS the line's figure, never below zero; the line's figure itself is
/// set by the owner's press alone and read from the line's own file, which nothing the organisation or the board reaches can write.
/// </summary>
public static class AuditBudget
{
    /// <summary>
    /// What the organisation may be admitted against in one class: <paramref name="ceiling"/> less the line's <paramref name="budget"/>,
    /// never below zero. A budget nobody could read (<c>null</c> — the line's file refused) admits nothing: the auditor's share is never
    /// guessed to be zero (BUDGET-001, unknown never zero).
    /// </summary>
    public static decimal Off(decimal ceiling, decimal? budget) =>
        budget is not { } b ? 0m : b <= 0m ? ceiling : ceiling > b ? ceiling - b : 0m;

    /// <summary>The class a budget row is kept under: <c>plan</c> for plan capacity, <c>key</c> for real money. Anything else is refused.</summary>
    public static string ClassOf(string billingClass) => billingClass switch
    {
        BillingClasses.Key => BillingClasses.Key,
        BillingClasses.Plan => BillingClasses.Plan,
        _ => throw new ArgumentException($"the audit line keeps a budget in '{BillingClasses.Key}' or '{BillingClasses.Plan}', not '{billingClass}'",
            nameof(billingClass)),
    };
}
