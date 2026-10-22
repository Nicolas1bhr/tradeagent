namespace TradeAgent.Core.Db;

/// <summary>
/// WHO PAYS FOR A LAUNCH, AND SO WHICH CEILING IT IS COUNTED AGAINST (<c>U-billing-classes</c>; <c>docs/ORCHESTRATION.md</c> § 2.4,
/// ORGANISATION § 10, <c>docs/COUNCIL.md</c> rule 4 — "subscription charges, API charges and list-price equivalents are three figures,
/// never one"). Three words, the values of <c>ai_attempt.billing_class</c>:
/// <list type="bullet">
/// <item><see cref="Plan"/> — the owner's AI plan (a ChatGPT sign-in): its turns are capacity the subscription includes, counted at
/// list price against <c>AiPlanDailyCapacity</c>, a list-price EQUIVALENT and never money.</item>
/// <item><see cref="Key"/> — an API key (the app's harness, perception's decision model, a CLI signed in with a pasted key): real money,
/// counted against <see cref="TradeAgentSettings.AiDailyCostCap"/>.</item>
/// <item><see cref="Unknown"/> — nobody established which: a seat with no seat profile, a sign-in text that matches neither pattern, a row
/// written before the rung. Counted against BOTH ceilings, and a launch of it must fit both — unknown is never zero.</item>
/// </list>
/// <para><b>Measurement, never claim.</b> The class is composed by the app from the seat verdict it read before the row existed and
/// written once in the row's insert; no agent's word reaches it, and an UPDATE of it is refused by trigger.</para>
/// </summary>
public static class BillingClasses
{
    public const string Plan = "plan";
    public const string Key = "key";
    public const string Unknown = "unknown";

    public static readonly string[] All = [Plan, Key, Unknown];

    /// <summary>The class a value names, or <see cref="Unknown"/> for anything else — an unrecognised class is never guessed.</summary>
    public static string Of(string? value) => value is Plan or Key ? value : Unknown;

    /// <summary>
    /// THE CEILINGS A LAUNCH OF <paramref name="billingClass"/> MUST FIT, each named by the class whose own ceiling it is: a plan launch the
    /// plan's capacity, a key launch the money cap, an unknown one both — the money cap first.
    /// </summary>
    public static IReadOnlyList<string> CeilingsOf(string? billingClass) => Of(billingClass) switch
    {
        Plan => [Plan],
        Key => [Key],
        _ => [Key, Plan]
    };

    /// <summary>The ceiling a class is counted in, in the owner's words, for a sentence: "real money" or "plan capacity".</summary>
    public static string Words(string? billingClass) => Of(billingClass) switch
    {
        Plan => "your ChatGPT plan's capacity",
        Key => "real money (an API key)",
        _ => "not known, so counted against both limits"
    };
}
