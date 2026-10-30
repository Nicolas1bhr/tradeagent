using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.Core;

/// <summary>
/// THE WORDS OF THE ORGANISATION-FIRST PAGES (<c>U-org-surface-a</c>; R24 § 4.7): the rail's three entries and each page's title, subtitle,
/// section names and empty lines, and the plain words for a seat's boundary and a billing class. Spelled once, so a page, a test and a
/// sentence cannot drift apart. These pages only read: the controls named here are the owner's repair of a refused position's folder (<c>U-repair-position-home</c>) and his presses over the chief (<c>U-org-chief</c>).
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

    /// <summary>What a page says instead of drawing a chart that is not one tree: it names the damage and draws nothing, never a hierarchy it is not.</summary>
    public static string ChartDamaged(string what) => $"Nothing is drawn, because the chart is damaged: {what}.";

    /// <summary>How a claim is introduced: who wrote it, then their words in quotation marks. A claim is never printed as the app's.</summary>
    public static string ClaimBy(string who) => $"{who} wrote:";

    // ---- Organisation ----------------------------------------------------------------------------

    public const string OrganisationSubtitle =
        "The organisation as it stands now: its units, the positions in them, and what each position last ran on.";

    public const string OrganisationNoUnits = "The chart holds no units.";

    /// <summary>
    /// The Organisation page's line under its title (<c>U-repair-position-home</c>): the chart is read as it stands, and its one press is the owner's
    /// repair of a position's folder the last start could not rebuild.
    /// </summary>
    public const string OrganisationRecordsAndRepair =
        "These are TradeAgent's own records, read as they stand. What you can do here is repair the folder of a position the last start "
        + "could not rebuild, and appoint, replace, activate or remove the chief on the organisation's card; nothing else on this page can "
        + "be changed from it.";

    /// <summary>The repair press at rest, on the row of a position whose folder the last start could not rebuild.</summary>
    public const string RepairHome = "Repair its folder";

    /// <summary>The repair press armed: what the second press does, in full — nothing is deleted.</summary>
    public const string RepairHomeConfirm = "Press again: move the folder aside, kept whole, and build it afresh";

    // ---- the chief, on the organisation's card (U-org-chief item 7) ------------------------------------------------------------------

    public const string ChiefTitle = "The chief";

    /// <summary>Under the chief's presses while no board seat is active: whose they are, and until when (the orchestrator's Q1).</summary>
    public const string ChiefYoursUntilBoard =
        "Yours until the security board is active: you appoint, replace and activate the chief here, two presses each. Once a board seat is "
        + "active, the board does that by a two-seat act, and you can still remove the chief.";

    /// <summary>In their place once a board seat is active.</summary>
    public const string ChiefIsTheBoards =
        "The security board appoints and replaces the chief now, by a two-seat act under your veto. You can still remove the chief.";

    public const string ChiefNone = "No chief is appointed: the organisation's head is vacant, so it is yours.";

    /// <summary>The chief sitting, in the app's words: its id, whether it is active, and the seat its row names.</summary>
    public static string ChiefSitting(string id, string status, string? runtime, string? model) =>
        $"The chief is {id}, {status}, set to run on {runtime ?? "no runtime"}{(model is null ? "" : $" with model {model}")}.";

    public const string ChiefSeatPrompt = "The seat a new chief runs on:";
    public const string ChiefNoSeats = "No seat can be offered: TradeAgent knows no runtime and model to seat a chief on yet.";
    public const string ChiefChooseSeat = "Choose the seat the chief runs on first. Nothing was written.";
    public static string ChiefSeatChoice(ChiefSeat seat) => $"{seat.Runtime} · {seat.Model}";

    public const string ChiefAppoint = "Appoint a chief";
    public const string ChiefAppointConfirm = "Press again: appoint a chief on the chosen seat, dormant until you activate it";
    public const string ChiefReplace = "Replace the chief";
    public const string ChiefReplaceConfirm = "Press again: end the chief, cancel its open mandates and appoint a new one on the chosen seat";
    public const string ChiefActivate = "Activate";
    public const string ChiefActivateConfirm = "Press again: activate the chief — a real-model seat needs a recorded proven cell";
    public const string ChiefRemove = "Remove the chief";
    public const string ChiefRemoveConfirm = "Press again: end the chief and cancel its open mandates; no one takes its place";

    /// <summary>THE OWNER'S WAKE-ONCE PRESS on the chief's card (<c>U-chief-wake-once</c>): two presses, the second carrying out what the first showed.</summary>
    public const string ChiefWakeOnce = "Wake the chief once";

    /// <summary>The armed sentence: the preview's own words, after "Press again".</summary>
    public static string ChiefWakeOnceConfirm(string words) => $"Press again: {words}";

    /// <summary>A second press that arrives with no first press shown — nothing is woken.</summary>
    public const string ChiefWakeOnceUnarmed = "The wake press could not show what it would do, so nothing was woken.";

    /// <summary>The preview could not be read: nothing is armed and nothing is woken.</summary>
    public static string ChiefWakeOnceUnreadable(string why) => $"The wake press could not read what it would do ({why}), so nothing was woken.";

    /// <summary>What the press does, beside it while it is drawn: one turn now, and what wakes the chief after it and how he stops it.</summary>
    public const string ChiefWakeOnceWhat =
        "\"Wake the chief once\" gives an active chief one turn now, on your plan, so you can watch its first turn instead of waiting for its ceiling.";

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

    /// <summary>
    /// ONE POSITION'S CALIBRATION IN WORDS (<c>U-org-scorecards-a</c> a5): the app's own score of its resolved, scored forecasts — how many,
    /// their mean Brier score against the base rate's over the same forecasts, and the skill between them — summed over every manager
    /// version, with the forecasts kept apart (open, void, not scored) and those the app could not score. A measurement, never a claim.
    /// </summary>
    public static string CalibrationWords(PositionScorecard card)
    {
        var t = card.Total;
        var apart = string.Join(", ", new[] { (t.Open, "open"), (t.Void, "void"), (t.Unscored, "not scored") }
            .Where(x => x.Item1 > 0).Select(x => $"{x.Item1.ToString(CultureInfo.InvariantCulture)} {x.Item2}"));
        var refused = card.Refused.Count == 0 ? ""
            : $" {card.Refused.Count.ToString(CultureInfo.InvariantCulture)} of its forecasts could not be scored.";
        if (t.Scored == 0)
            return $"Calibration: no scored forecast of its has resolved yet{(apart.Length > 0 ? $" ({apart})" : "")}.{refused}";
        var versions = card.Versions.Count(v => v.Scored > 0);
        return $"Calibration: {t.Scored.ToString(CultureInfo.InvariantCulture)} scored {(t.Scored == 1 ? "forecast" : "forecasts")} resolved"
               + $" — Brier {Three(t.Brier!.Value)} against the base rate's {Three(t.BaseBrier!.Value)}, skill {Signed(t.Skill!.Value)}"
               + (versions > 1 ? $", over {versions.ToString(CultureInfo.InvariantCulture)} manager versions" : "")
               + (apart.Length > 0 ? $"; {apart}" : "") + "." + refused;

        static string Three(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);
        static string Signed(double d) => Math.Round(d, 3) == 0 ? "0.000" : d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture);
    }

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

    /// <summary>A unit with no turn today in any billing class: one line, never three "none today" lines.</summary>
    public const string CostsNothingToday = "Nothing spent today: no AI turn of this unit or any unit below it has run.";
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
