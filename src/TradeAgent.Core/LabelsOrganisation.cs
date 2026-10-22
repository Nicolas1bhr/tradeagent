using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// THE WORDS OF THE ORGANISATION-FIRST PAGES (<c>U-org-surface-a</c>; R24 § 4.7): the rail's three entries and each page's title, subtitle,
/// section names and empty lines, and the plain words for a seat's boundary and a billing class. Spelled once, so a page, a test and a
/// sentence cannot drift apart. These pages only read: nothing here names a control, because none of them has one.
/// </summary>
public static partial class Labels
{
    // ---- the rail --------------------------------------------------------------------------------

    public const string NavOrganisation = "Organisation";
    public const string NavWork = "Work";
    public const string NavCosts = "Costs";

    // ---- shared ----------------------------------------------------------------------------------

    /// <summary>The line under every one of these pages' titles: whose records they are and that they cannot be changed here.</summary>
    public const string SurfaceReadOnly = "These are TradeAgent's own records, read as they stand. Nothing on this page can be changed from it.";

    /// <summary>What a page says before its first read of the records.</summary>
    public const string SurfaceNotRead = "The organisation's records have not been read yet.";

    /// <summary>What a page says when the records could not be read: the reason, in the app's words, and that nothing below is current.</summary>
    public static string SurfaceUnreadable(string why) => $"The organisation's records could not be read, so nothing below is current: {why}";

    /// <summary>How a claim is introduced: who wrote it, then their words in quotation marks. A claim is never printed as the app's.</summary>
    public static string ClaimBy(string who) => $"{who} wrote:";

    // ---- Organisation ----------------------------------------------------------------------------

    public const string OrganisationSubtitle =
        "The organisation as it stands now: its units, the positions in them, and what each position last ran on.";

    public const string OrganisationNoUnits = "The chart holds no units.";

    public const string CellProofHeld = "A cell proof is recorded for this machine and this build.";

    public const string CellProofNone =
        "No cell proof is recorded for this machine and this build, so no seat can run in a confined cell here.";

    /// <summary>A position's seat boundary, in words — from the seat class the app recorded for its latest launch, never from a position's own word.</summary>
    public static string SeatAssuranceWords(SeatAssurance assurance) => assurance switch
    {
        SeatAssurance.Unconfined => "unconfined: a command-line seat started as you, with a shell, so it can run anything you can",
        SeatAssurance.Mediated => "mediated: a broker-only seat that reaches only what the app serves, though its process itself could reach more",
        SeatAssurance.Brokered => "brokered: the app's own harness, no outside process, every tool the app's own code",
        SeatAssurance.Confined => "confined: started in a cell whose confinement was proved on this machine",
        _ => "not a boundary this build knows"
    };

    // ---- Work ------------------------------------------------------------------------------------

    public const string WorkSubtitle =
        "What the organisation is doing: the mandates open now, the deliveries waiting for a decision, and the decisions taken lately.";

    public const string WorkMandatesHeading = "Open mandates";
    public const string WorkNoMandates = "No mandate is open.";
    public const string WorkDeliveriesHeading = "Deliveries waiting to be judged";
    public const string WorkNoDeliveries = "No delivery is waiting.";
    public const string WorkDecisionsHeading = "Decisions in the last 7 days";
    public const string WorkNoDecisions = "No decision was recorded in the last 7 days.";

    /// <summary>The line above a decision's forecasts: they are the forecaster's claim; the resolution is the app's.</summary>
    public const string WorkForecastNote = "A forecast is its author's claim; whether it came true is the app's own record.";

    // ---- Costs -----------------------------------------------------------------------------------

    public const string CostsSubtitle =
        "What the organisation's AI turns cost today, unit by unit, and who paid for them. A unit's figure includes every unit below it.";

    public const string CostsNoUnits = "The chart holds no units.";
    public const string CostsNoTurns = "No AI turn has been funded here today.";

    public const string CostsPlanHeading = "Your ChatGPT plan";
    public const string CostsKeyHeading = "An API key";
    public const string CostsUnknownHeading = "Not established";

    /// <summary>One billing class's heading line, with what its figure is: list price for the plan, real money for a key.</summary>
    public static string CostsClassWords(string billingClass) => BillingClasses.Of(billingClass) switch
    {
        BillingClasses.Plan => $"{CostsPlanHeading} (counted at list price against the plan's capacity; not money charged)",
        BillingClasses.Key => $"{CostsKeyHeading} (real money)",
        _ => $"{CostsUnknownHeading} (nobody established who pays, so each turn is counted against both limits)"
    };
}
