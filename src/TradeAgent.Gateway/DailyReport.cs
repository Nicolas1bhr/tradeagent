using System.Globalization;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.Gateway;

/// <summary>
/// ONE FIGURE THE REPORT COULD NOT MEASURE, NAMED WHERE IT WOULD HAVE STOOD.
///
/// <para>The report never omits a number it could not work out and never prints a zero in its place:
/// it prints a dash and this sentence. <c>docs/COUNCIL.md</c> rule 10 — "every missing trading or
/// operating cost named, so an incomplete net figure can never read as a complete profitability
/// claim" — is the whole reason this type exists rather than a nullable field on its own.</para>
/// </summary>
public sealed record ReportGap(string Field, string Why);

/// <summary>Who this report is about, and exactly when it was taken. Nothing here is a range.</summary>
public sealed record ReportIdentity
{
    /// <summary>The owner's local calendar day, <c>yyyy-MM-dd</c>. What the file is named after.</summary>
    public required string Day { get; init; }

    /// <summary>What the day is bounded by — the two instants, on the offsets in force at each.</summary>
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>The zone the day was cut on, by name, so a reader in another one can place it.</summary>
    public required string Timezone { get; init; }

    /// <summary>
    /// THE ONE INSTANT EVERY FIGURE BELOW WAS READ AT. A report assembled from three reads a minute
    /// apart is a report whose sections can contradict each other; this says which moment it is.
    /// </summary>
    public required DateTimeOffset SnapshotAt { get; init; }

    public string? AppVersion { get; init; }
    public int? SchemaVersion { get; init; }
}

/// <summary>One role's state on the day. Absent figures are absent, never zero.</summary>
public sealed record ReportRole(string Role, string? Model, int? Turns, decimal? Spent,
    decimal? Reserved, decimal? Cap, string? Estimated);

/// <summary>Working, healthy idle, blocked or paused — with the coded reason and the next wake.</summary>
public sealed record ReportMission
{
    /// <summary>The loop's own word for what it is doing, or null where nothing reported one.</summary>
    public string? State { get; init; }

    /// <summary>The CODED reason, as the app names it — never a sentence composed here.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset? NextEligibleWake { get; init; }
    public IReadOnlyList<ReportRole> Roles { get; init; } = [];
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>Whether an order could be placed at all, and what stands in the way if not.</summary>
public sealed record ReportReadiness
{
    public string? Connector { get; init; }
    public bool? ConnectorIsPaper { get; init; }
    public string? Account { get; init; }
    public string? Mode { get; init; }
    public bool? ExecutionAvailable { get; init; }
    public string? ExecutionBlockedReason { get; init; }

    /// <summary>How old the newest price this installation has seen is, at the snapshot.</summary>
    public TimeSpan? DataAge { get; init; }

    public IReadOnlyList<string> MissingCapabilities { get; init; } = [];
    public IReadOnlyList<string> ActivationBlockers { get; init; } = [];
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// WHAT THE TRADING MADE OR LOST, AND WHAT THE FIGURE DOES NOT KNOW.
///
/// <para><see cref="Net"/> is null exactly when <see cref="PnlReport.Net"/> is — a fill whose fee the
/// platform never reported is not a fill that cost nothing — and <see cref="Missing"/> then names the
/// fee. The two travel together on purpose: a dash with no reason beside it is read as a rounding
/// artefact, and a net printed as though an unreported cost were zero is wrong in the owner's favour
/// every single time.</para>
/// </summary>
public sealed record ReportPerformance
{
    public decimal? Exposure { get; init; }
    public decimal? Realized { get; init; }
    public decimal? Unrealized { get; init; }

    /// <summary>When the open positions were valued. Null when nothing valued them.</summary>
    public DateTimeOffset? ValuedAt { get; init; }

    public decimal? Fees { get; init; }
    public int? FeesUnknownFills { get; init; }
    public decimal? Net { get; init; }
    public decimal? Drawdown { get; init; }
    public int? Fills { get; init; }
    public decimal? LossAllowanceRemaining { get; init; }
    public decimal? LossBudgetDay { get; init; }
    public string Currency { get; init; } = "";
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>One operation the app cannot yet say the outcome of, and how long that has been true.</summary>
public sealed record ReportUnknown(string RequestId, string Instrument, DateTimeOffset Since, TimeSpan Age);

public sealed record ReportExecution
{
    public int? OpenRequests { get; init; }
    public int? Unreconciled { get; init; }
    public int? OrdersToday { get; init; }
    public int? FillsToday { get; init; }
    public int? RejectionsToday { get; init; }
    public IReadOnlyList<ReportUnknown> Unknown { get; init; } = [];
    public DateTimeOffset? LastFillPullAt { get; init; }
    public string? LastFillPullError { get; init; }
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// WHAT THE AI COST, WITH THE THREE FIGURES <c>docs/COUNCIL.md</c> RULE 4 KEEPS APART.
///
/// <para>"subscription charges, API charges and list-price equivalents are three figures, never one".
/// This build has only the third: every number here is a list-price equivalent computed by TradeAgent
/// from token counts, which is a calculation and not an invoice. <see cref="Basis"/> says so, in the
/// owner's words, on every report — so an advisory number can never be read as a cap that was
/// enforced by somebody's billing.</para>
/// </summary>
public sealed record ReportSpending
{
    public IReadOnlyList<ReportRole> Roles { get; init; } = [];
    public decimal? Spent { get; init; }
    public decimal? Reserved { get; init; }
    public decimal? Cap { get; init; }
    public int? Turns { get; init; }
    public int? UnpricedTurns { get; init; }
    public string Currency { get; init; } = "";
    public string? Runtime { get; init; }

    /// <summary>Where these figures come from, and what they are not. Never null in a real report.</summary>
    public string? Basis { get; init; }

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>Everything it costs to run that is NOT the AI. Today the app measures none of it.</summary>
public sealed record ReportOtherCosts
{
    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// The evidence, with the app's own measurements kept apart from what an agent said about them —
/// round 4's "app-owned measurements and separately attributed judgments".
/// </summary>
public sealed record ReportResearch
{
    /// <summary>Facts the app computed from its own tables. Nothing an agent wrote is in here.</summary>
    public IReadOnlyList<string> AppMetrics { get; init; } = [];

    /// <summary>Publications, by role and revision. What was claimed, attributed, never merged above.</summary>
    public IReadOnlyList<string> AgentClaims { get; init; } = [];

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// ONE MESSAGE THE OWNER TYPED, AND WHAT BECAME OF IT.
///
/// <para>Round 4: "Owner text enters Operations' agenda first, with receipt, disposition and
/// deadline." <see cref="Disposition"/> is null while nothing has become of it yet, which is a
/// different fact from any of the recorded outcomes and is printed as one.</para>
/// </summary>
public sealed record ReportOwnerMessage(string Text, DateTimeOffset ReceivedAt, string? Disposition);

public sealed record ReportDecisions
{
    public IReadOnlyList<ReportOwnerMessage> OwnerMessages { get; init; } = [];

    /// <summary>Limit changes and cap events, as the activity log recorded them on the day.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

public sealed record ReportRecovery
{
    /// <summary>Launches that were never closed — a killed turn nobody will report usage for.</summary>
    public IReadOnlyList<string> Interrupted { get; init; } = [];

    /// <summary>Publications committed whose copy into the recipient's folder has not been made.</summary>
    public IReadOnlyList<string> PendingHandoffs { get; init; } = [];

    public IReadOnlyList<string> Blockers { get; init; } = [];
    public DateTimeOffset? NextReviewAt { get; init; }

    /// <summary>Set when a review is pending because it was not funded, never because it was skipped.</summary>
    public string? ReviewNotFunded { get; init; }

    public IReadOnlyList<ReportGap> Missing { get; init; } = [];
}

/// <summary>
/// THE OWNER'S DAILY REPORT, WRITTEN BY THE APP FROM WHAT IT MEASURED (<c>docs/COUNCIL.md</c> rule 10
/// and "The owner's report, every day, from the app, even when no agent ran").
///
/// <para><b>Nothing here is inferred and no paid turn produces it.</b> Every field is composed from a
/// table this app wrote or a read it took itself, at ONE instant
/// (<see cref="ReportIdentity.SnapshotAt"/>). There is no model call anywhere on this path: the
/// Operations Director's note is a separate, paid publication and appears beside the report rather
/// than inside it.</para>
///
/// <para><b>Every field is nullable and labelled, never defaulted.</b> A figure the app could not
/// work out is null and is named in that section's <c>Missing</c>. That is not a style: the report is
/// the document an owner decides on, and a zero standing in for an unknown is a claim this software
/// is not entitled to make — most sharply for <see cref="ReportPerformance.Net"/>, which is withheld
/// whenever a single fill's fee was never reported.</para>
/// </summary>
public sealed record DailyReport
{
    public required ReportIdentity Identity { get; init; }
    public ReportMission Mission { get; init; } = new();
    public ReportReadiness Readiness { get; init; } = new();
    public ReportPerformance Performance { get; init; } = new();
    public ReportExecution Execution { get; init; } = new();
    public ReportSpending Spending { get; init; } = new();
    public ReportOtherCosts OtherCosts { get; init; } = new();
    public ReportResearch Research { get; init; } = new();
    public ReportDecisions Decisions { get; init; } = new();
    public ReportRecovery Recovery { get; init; } = new();

    /// <summary>Every gap in the whole report, section by section, for a reader that wants the list.</summary>
    public IReadOnlyList<ReportGap> AllGaps =>
    [
        .. Mission.Missing, .. Readiness.Missing, .. Performance.Missing, .. Execution.Missing,
        .. Spending.Missing, .. OtherCosts.Missing, .. Research.Missing, .. Decisions.Missing,
        .. Recovery.Missing
    ];
}

/// <summary>
/// A RENDERED REPORT AND WHETHER THE APP ACCEPTED ITS OWN DRAFT.
///
/// <see cref="Rejected"/> is non-null when the draft ran past <see cref="DailyReportText.MaxLines"/>.
/// The text is then the refusal itself, so the day's file still exists and still says what happened —
/// <c>docs/COUNCIL.md</c>'s "an invalid publication is rejected and the last valid plan stands",
/// applied to the app's own output rather than only to an agent's.
/// </summary>
public sealed record DailyReportDraft(string Text, int Lines, string? Rejected);

/// <summary>
/// THE REPORT AS WORDS. Separated from everything that gathers it so the SENTENCES can be tested
/// without a gateway, a connector or a screen — the same reason <c>PerformanceCard.Words</c> is a
/// static function, and for the same kind of defect: "we do not know" is the part of this document
/// that matters and it is not something a screenshot proves.
/// </summary>
public static class DailyReportText
{
    /// <summary>
    /// The most lines the app will write for one day. A report nobody finishes reading is not a
    /// report, and the limit is enforced rather than requested — see <see cref="DailyReportDraft"/>.
    /// </summary>
    public const int MaxLines = 120;

    /// <summary>What stands where a figure could not be worked out. Never a zero, never blank.</summary>
    public const string Unknown = "—";

    /// <summary>The sentence at the top of every report, which is also the whole of its authority.</summary>
    public const string Preamble =
        "Written by TradeAgent from what it measured. Nothing in it is inferred, no figure is a "
        + "guess, and no AI turn was spent producing it. A dash is a figure TradeAgent could not "
        + "work out; the reason is named beside it.";

    public static DailyReportDraft Draft(DailyReport r)
    {
        var text = Render(r);
        var lines = Count(text);
        if (lines <= MaxLines) return new DailyReportDraft(text, lines, null);

        var why = $"TradeAgent's own draft of the report for {r.Identity.Day} came to {lines} lines, "
                  + $"which is past the {MaxLines} this report may be, so it was not kept. Nothing has "
                  + "been shortened or left out silently.";
        var refusal = string.Join('\n',
        [
            $"# TradeAgent — {r.Identity.Day}",
            "",
            why,
            ""
        ]);
        return new DailyReportDraft(refusal, Count(refusal), why);
    }

    static int Count(string text) => text.Replace("\r\n", "\n").Split('\n').Length;

    /// <summary>
    /// The ten sections, in the order <c>docs/COUNCIL.md</c> fixes them. LF endings on every
    /// platform: the file is read by a person, by a later build and by the AI, and a line ending that
    /// depends on which machine wrote it is a diff nobody asked for.
    /// </summary>
    public static string Render(DailyReport r)
    {
        var b = new StringBuilder();
        var id = r.Identity;

        Head(b, $"TradeAgent — {id.Day}");
        b.Append(Preamble).Append('\n').Append('\n');

        Section(b, "1. Identity");
        Kv(b, "day", $"{id.Day} ({id.Timezone})");
        Kv(b, "covers", $"{Instant(id.From)} to {Instant(id.To)}");
        Kv(b, "snapshot taken", Instant(id.SnapshotAt));
        Kv(b, "app version", id.AppVersion ?? Unknown);
        Kv(b, "database schema", id.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? Unknown);

        Section(b, "2. Mission state");
        Kv(b, "state", r.Mission.State ?? Unknown);
        Kv(b, "reason", r.Mission.Reason ?? Unknown);
        Kv(b, "next eligible wake", Instant(r.Mission.NextEligibleWake));
        foreach (var role in r.Mission.Roles)
            Kv(b, role.Role, $"model {role.Model ?? Unknown}, "
                             + $"{role.Turns?.ToString(CultureInfo.InvariantCulture) ?? Unknown} turns");
        Gaps(b, r.Mission.Missing);

        Section(b, "3. Trading readiness");
        Kv(b, "platform", r.Readiness.Connector is { } c
            ? c + (r.Readiness.ConnectorIsPaper is { } p ? p ? " (practice)" : " (real money)" : "")
            : Unknown);
        Kv(b, "account", r.Readiness.Account ?? Unknown);
        Kv(b, "mode", r.Readiness.Mode ?? Unknown);
        Kv(b, "orders allowed", r.Readiness.ExecutionAvailable switch
        {
            true => "yes",
            false => "no — " + (r.Readiness.ExecutionBlockedReason ?? Unknown),
            null => Unknown
        });
        Kv(b, "newest price", r.Readiness.DataAge is { } age ? Age(age) : Unknown);
        List(b, "missing capabilities", r.Readiness.MissingCapabilities);
        List(b, "activation blockers", r.Readiness.ActivationBlockers);
        Gaps(b, r.Readiness.Missing);

        Section(b, "4. Capital and performance");
        var money = r.Performance.Currency;
        Kv(b, "exposure", Money(r.Performance.Exposure, money));
        Kv(b, "realised", Money(r.Performance.Realized, money));
        Kv(b, "unrealised", Money(r.Performance.Unrealized, money)
                            + (r.Performance.ValuedAt is { } v ? $" (valued {Instant(v)})" : ""));
        Kv(b, "fees reported", Money(r.Performance.Fees, money)
                               + (r.Performance.FeesUnknownFills is int n and > 0
                                   ? $" — {n} fill(s) carry no fee at all" : ""));
        // THE ONE FIGURE THIS DOCUMENT EXISTS TO GET RIGHT. Withheld whenever a cost is unknown, and
        // the reason for withholding it is printed by the Gaps line below, never omitted.
        Kv(b, "net after costs", Money(r.Performance.Net, money));
        Kv(b, "worst drop", Money(r.Performance.Drawdown, money));
        Kv(b, "fills", r.Performance.Fills?.ToString(CultureInfo.InvariantCulture) ?? Unknown);
        Kv(b, "loss allowance left", Money(r.Performance.LossAllowanceRemaining, money)
                                     + (r.Performance.LossBudgetDay is { } d
                                         ? $" of {Labels.Money(d, money)}" : " — no daily loss budget is set"));
        Gaps(b, r.Performance.Missing);

        Section(b, "5. Execution health");
        Kv(b, "orders placed today", Whole(r.Execution.OrdersToday));
        Kv(b, "fills today", Whole(r.Execution.FillsToday));
        Kv(b, "rejections today", Whole(r.Execution.RejectionsToday));
        Kv(b, "open requests", Whole(r.Execution.OpenRequests));
        Kv(b, "awaiting reconciliation", Whole(r.Execution.Unreconciled));
        foreach (var u in r.Execution.Unknown)
            Kv(b, "UNKNOWN", $"{u.RequestId} on {u.Instrument}, {Age(u.Age)} since {Instant(u.Since)}");
        Kv(b, "last read of the platform's fills", r.Execution.LastFillPullAt is { } at
            ? Instant(at) + (r.Execution.LastFillPullError is { } e ? $" — FAILED: {e}" : "")
            : Unknown);
        Gaps(b, r.Execution.Missing);

        Section(b, "6. AI spending");
        Kv(b, "spent", Money(r.Spending.Spent, r.Spending.Currency));
        Kv(b, "reserved and unresolved", Money(r.Spending.Reserved, r.Spending.Currency));
        Kv(b, "daily ceiling", Money(r.Spending.Cap, r.Spending.Currency));
        Kv(b, "turns", Whole(r.Spending.Turns)
                       + (r.Spending.UnpricedTurns is int unpriced and > 0 ? $", {unpriced} of them unpriced" : ""));
        Kv(b, "runtime", r.Spending.Runtime ?? Unknown);
        foreach (var role in r.Spending.Roles)
            Kv(b, role.Role, $"{Money(role.Spent, r.Spending.Currency)} spent, "
                             + $"{Money(role.Reserved, r.Spending.Currency)} committed, "
                             + $"of {Money(role.Cap, r.Spending.Currency)} on {role.Model ?? Unknown}"
                             + (role.Estimated is { } est ? $" — {est}" : ""));
        Kv(b, "basis", r.Spending.Basis ?? Unknown);
        Gaps(b, r.Spending.Missing);

        Section(b, "7. Other operating costs");
        Gaps(b, r.OtherCosts.Missing);

        Section(b, "8. Research evidence");
        List(b, "measured by TradeAgent", r.Research.AppMetrics);
        List(b, "claimed by an agent", r.Research.AgentClaims);
        Gaps(b, r.Research.Missing);

        Section(b, "9. Decisions and changes");
        foreach (var m in r.Decisions.OwnerMessages)
            Kv(b, "you said", $"\"{OneLine(m.Text)}\" at {Instant(m.ReceivedAt)} — "
                              + $"{m.Disposition ?? "nothing yet"}");
        List(b, "changes", r.Decisions.Changes);
        Gaps(b, r.Decisions.Missing);

        Section(b, "10. Recovery and next work");
        List(b, "interrupted", r.Recovery.Interrupted);
        List(b, "handoffs not yet delivered", r.Recovery.PendingHandoffs);
        List(b, "blockers", r.Recovery.Blockers);
        Kv(b, "next review", Instant(r.Recovery.NextReviewAt)
                             + (r.Recovery.ReviewNotFunded is { } why ? $" — {why}" : ""));
        Gaps(b, r.Recovery.Missing);

        return b.ToString();
    }

    static void Head(StringBuilder b, string text) => b.Append("# ").Append(text).Append('\n').Append('\n');

    static void Section(StringBuilder b, string title) =>
        b.Append("## ").Append(title).Append('\n');

    static void Kv(StringBuilder b, string key, string value) =>
        b.Append("- ").Append(key).Append(": ").Append(value).Append('\n');

    static void List(StringBuilder b, string key, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { Kv(b, key, "none"); return; }
        foreach (var item in items) Kv(b, key, OneLine(item));
    }

    /// <summary>
    /// THE GAPS, AND THEY ARE NOT OPTIONAL. Removing this line is what turns a withheld net into a
    /// dash nobody can account for, which reads as a complete figure that happened to be empty.
    /// </summary>
    static void Gaps(StringBuilder b, IReadOnlyList<ReportGap> gaps)
    {
        foreach (var g in gaps)
            b.Append("- missing (").Append(g.Field).Append("): ").Append(OneLine(g.Why)).Append('\n');
        b.Append('\n');
    }

    static string Whole(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? Unknown;

    /// <summary>An amount with its currency, or the dash. Never a zero standing in for an unknown.</summary>
    static string Money(decimal? v, string currency) => v is { } m ? Labels.Money(m, currency) : Unknown;

    static string Instant(DateTimeOffset? at) =>
        at is { } t ? t.ToString("yyyy-MM-dd HH:mm:ssK", CultureInfo.InvariantCulture) : Unknown;

    static string Age(TimeSpan age) =>
        age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds}s old"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m old"
        : $"{(int)age.TotalHours}h old";

    /// <summary>
    /// One line, because a line count is the limit and a message with newlines in it would let one
    /// entry silently spend the whole budget. The owner's own words are never truncated away — they
    /// are folded, and the fold is visible.
    /// </summary>
    static string OneLine(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
}
