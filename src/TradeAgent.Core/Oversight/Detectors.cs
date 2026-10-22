using System.Globalization;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Oversight;

/// <summary>
/// THE LINE'S THRESHOLDS, NAMED POLICY CONSTANTS until the owner sets them (his page, part b, shows them). Each cites where its figure comes
/// from, or says it is this unit's choice.
/// </summary>
public static class OversightPolicy
{
    /// <summary><c>spend-no-accept</c>'s window: the seven whole UTC days before the sweep's day. This unit's choice.</summary>
    public static readonly TimeSpan SpendWindow = TimeSpan.FromDays(7);

    /// <summary><c>spend-no-accept</c>'s floor: a unit's spend in the window at or above this, with nothing accepted, is a finding. This unit's choice.</summary>
    public const decimal SpendFloor = 0.50m;

    /// <summary><c>stall</c>'s N: an active unit with no accepted delivery for this long (ORGANISATION § 9's "for N days"). This unit's choice.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromDays(3);

    /// <summary><c>owner-words-late</c>'s deadline — the default of <see cref="TradeAgentSettings.OwnerReplyDeadlineHours"/>.</summary>
    public static readonly TimeSpan OwnerWordsDeadline = TimeSpan.FromHours(24);

    /// <summary><c>material-unwoken</c>'s bound — ORGANISATION § 10's "maximum interval (default 24 h)" for a level.</summary>
    public static readonly TimeSpan MaterialMaxInterval = TimeSpan.FromHours(24);

    /// <summary><c>claim-without-evidence</c>'s grace for a ledger claim to gain its link at its revision. This unit's choice.</summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromHours(1);

    /// <summary>The most records one finding cites; the count is measured whole.</summary>
    public const int MaxCites = 20;
}

/// <summary>The sweep's instant and the windows the detectors derive from it.</summary>
public sealed record OversightWindow(DateTimeOffset Now)
{
    /// <summary>The start of the sweep's UTC day: whole-day windows end here, so every sweep of one day sees the same window.</summary>
    public DateTimeOffset DayStart => new(Now.UtcDateTime.Date, TimeSpan.Zero);
}

/// <summary>
/// ONE CODE DETECTOR (ORGANISATION § 9 layer 1: "code detectors that cannot be argued with"). <see cref="Detect"/> returns only findings in
/// its scope's subtree; each carries the detector's fixed <see cref="Blind"/> sentence — what it cannot see on CLI seats.
/// </summary>
public abstract class OversightDetector
{
    /// <summary>The detector's name, part of every finding's id.</summary>
    public abstract string Name { get; }

    /// <summary>What this detector cannot see (§ 9: file reads, shared-file side channels, shell-started runs unmetered on CLI seats).</summary>
    public abstract string Blind { get; }

    /// <summary>Its severity, unless a candidate says otherwise.</summary>
    protected virtual string Severity => OversightSeverity.Info;

    /// <summary>A fact before it is scoped: the unit it is in, its subject and window, what it cites and measured.</summary>
    protected sealed record Candidate(string Unit, string Subject, string Window, IReadOnlyList<OversightCite> Cites,
        IReadOnlyDictionary<string, string> Measured, string? Severity = null);

    /// <summary>Every fact this detector measures in the snapshot, organisation-wide.</summary>
    protected abstract IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w);

    /// <summary>The findings of <paramref name="scope"/>: this detector's facts in its subtree only.</summary>
    public IReadOnlyList<OversightFinding> Detect(OrgSnapshot snapshot, OversightScope scope, OversightWindow window) =>
        [.. Find(snapshot, window)
            .Where(c => snapshot.InSubtree(c.Unit, scope.UnitId))
            .Select(c => new OversightFinding(OversightFinding.IdOf(Name, scope.Id, c.Subject, c.Window), Name, c.Severity ?? Severity,
                scope.Id, c.Unit, snapshot.PathOf(c.Unit), c.Subject, c.Window, [.. c.Cites.Take(OversightPolicy.MaxCites)], c.Measured, Blind,
                window.Now))];

    /// <summary>Every detector of layer 1, in a fixed order.</summary>
    public static IReadOnlyList<OversightDetector> All { get; } =
    [
        new SpendNoAcceptDetector(), new StallDetector(), new OwnerWordsLateDetector(), new MaterialUnwokenDetector(),
        new RefusalsDetector(), new ClaimWithoutEvidenceDetector(), new AcceptedFailedCheckDetector(), new DeadVersionRerunDetector(),
    ];

    protected const string CliBlind =
        "on the legacy pair's CLI seats it cannot see file reads, shared-file side channels or runs a shell started, which no app row meters";

    protected static string T(DateTimeOffset t) => Sql.T(t);
    protected static string N(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    protected static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The one publication kind each legacy head hands off — the only publication that counts as its division's accepted work.</summary>
    protected static readonly IReadOnlyDictionary<string, string> LegacyHandoff = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CouncilRoles.Research] = PublicationKind.Report,
        [CouncilRoles.Operations] = PublicationKind.Brief,
    };

    /// <summary>The accepted events of mandates, each with its mandate and the delivery its evidence names first.</summary>
    protected static IEnumerable<(SnapMandateEvent Event, SnapMandate Mandate, SnapDelivery? Delivery)> Accepted(OrgSnapshot s)
    {
        var mandates = s.Mandates.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var deliveries = s.Deliveries.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var e in s.MandateEvents.Where(e => e.Kind == MandateEventKind.Accepted))
            if (mandates.TryGetValue(e.MandateId, out var m))
                yield return (e, m, e.Evidence.Select(id => deliveries.GetValueOrDefault(id)).FirstOrDefault(d => d is not null));
    }

    /// <summary>
    /// When <paramref name="unit"/>'s subtree last had work accepted at or before <paramref name="until"/>: an accepted mandate in it, or —
    /// for a legacy division, which runs on publications rather than mandates — its head's HANDOFF, the one kind it delivers up or down
    /// (<see cref="LegacyHandoff"/>: Research's report to the chair, Operations' brief to Research). Null when never.
    ///
    /// <para><b>Never a private plan, journal or note:</b> a head's own memory is not work anyone accepted, and counting it let a division
    /// that spent and handed nothing off quiet <c>stall</c> and <c>spend-no-accept</c> by writing to itself (fix pass 1, R2).</para>
    /// </summary>
    protected static DateTimeOffset? LastAccepted(OrgSnapshot s, string unit, DateTimeOffset until)
    {
        var accepted = Accepted(s).Where(a => a.Event.At <= until && s.InSubtree(a.Mandate.UnitId, unit)).Select(a => (DateTimeOffset?)a.Event.At);
        var legacy = OrgEnvelopes.LegacyRoleOf(unit) is { } role && LegacyHandoff.TryGetValue(role, out var kind)
            ? s.Publications.Where(p => p.Role == role && p.Kind == kind && p.At <= until).Select(p => (DateTimeOffset?)p.At)
            : [];
        return accepted.Concat(legacy).Max();
    }
}

/// <summary>
/// <c>spend-no-accept</c>: a unit's AI spend over the window with nothing accepted in its subtree (§ 9 "spend without accepted milestones").
/// Spend is the app's own figure per attempt — its bill, or its reservation while it runs — charged to the attempt's recorded funding unit.
/// </summary>
public sealed class SpendNoAcceptDetector : OversightDetector
{
    public override string Name => "spend-no-accept";

    public override string Blind =>
        "It counts only the spend the app metered on ai_attempt; " + CliBlind + ", and work that was done but never delivered and accepted counts as nothing.";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        var to = w.DayStart;
        var from = to - OversightPolicy.SpendWindow;
        var attempts = s.Attempts.Where(a => a.StartedAt >= from && a.StartedAt < to && a.UnitId is not null
                                             && Array.IndexOf(AppPrincipals.All, a.Role) < 0).ToList();
        foreach (var unit in s.Units.Where(u => u.ParentId is not null))
        {
            var mine = attempts.Where(a => s.InSubtree(a.UnitId!, unit.Id)).ToList();
            var spent = mine.Sum(a => a.Spend);
            if (spent < OversightPolicy.SpendFloor) continue;
            if (LastAccepted(s, unit.Id, to) is { } last && last >= from) continue;
            yield return new Candidate(unit.Id, unit.Id, $"{T(from)}..{T(to)}",
                [.. mine.Select(a => new OversightCite("attempt", a.Id))],
                new Dictionary<string, string>
                {
                    ["spent"] = N(spent), ["attempts"] = N(mine.Count), ["from"] = T(from), ["to"] = T(to),
                });
        }
    }
}

/// <summary><c>stall</c>: an active unit with no accepted delivery for <see cref="OversightPolicy.StallAfter"/> (legacy divisions: no handoff by their head — a report or brief, never a private plan or journal).</summary>
public sealed class StallDetector : OversightDetector
{
    public override string Name => "stall";

    public override string Blind =>
        "It sees only deliveries the app sealed and accepted and the legacy heads' handoffs (reports, briefs); " + CliBlind + ", so work in progress is invisible to it.";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var unit in s.Units.Where(u => u.ParentId is not null && u.Status == OrgUnitStatus.Active))
        {
            var last = LastAccepted(s, unit.Id, w.Now);
            var anchor = last is { } l && l > unit.CreatedAt ? l : unit.CreatedAt;
            if (w.Now - anchor < OversightPolicy.StallAfter) continue;
            var lastMandate = Accepted(s).Where(a => s.InSubtree(a.Mandate.UnitId, unit.Id) && a.Event.At == last).Select(a => a.Mandate).FirstOrDefault();
            yield return new Candidate(unit.Id, unit.Id, "since " + T(anchor),
                lastMandate is null ? [] : [new OversightCite("mandate", lastMandate.Id)],
                new Dictionary<string, string>
                {
                    ["since"] = T(anchor), ["days"] = N((long)(w.Now - anchor).TotalDays),
                    ["last_accepted"] = last is { } a ? T(a) : "never",
                });
        }
    }
}

/// <summary><c>owner-words-late</c>: the owner's words (typed or dropped in his inbox) not dispositioned within the deadline.</summary>
public sealed class OwnerWordsLateDetector : OversightDetector
{
    public override string Name => "owner-words-late";

    public override string Blind =>
        "It reads only whether the app recorded a disposition for the wake, never whether the answer was any good; the app keeps no "
        + "disposition time, so a dispositioned wake is timed by its consumption and a late disposition is seen only by a sweep that ran before it; "
        + CliBlind + ".";

    /// <summary>
    /// <b>The disposition, not the consumption, settles the owner's words</b> (fix pass 1, R3): consumption is launch admission — the turn
    /// may still fail to say anything — so a wake with no disposition, or only <see cref="MissionEventDisposition.Blocked"/>'s standing
    /// reason, is late from its raising to now, consumed or not. A dispositioned wake is timed by its consumption, the last instant the app
    /// records on the row (it keeps no disposition time — named in <see cref="Blind"/>); one dispositioned without ever being consumed has no
    /// instant to time and is not measured.
    /// </summary>
    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var e in s.Wakes.Where(e => e.Kind is MissionEventKind.Owner or MissionEventKind.Inbox))
        {
            var dispositioned = e.Disposition is { } d && d != MissionEventDisposition.Blocked;
            if (dispositioned && e.ConsumedAt is null) continue;
            var took = (dispositioned ? e.ConsumedAt!.Value : w.Now) - e.CreatedAt;
            if (took <= OversightPolicy.OwnerWordsDeadline) continue;
            yield return new Candidate(s.UnitOfRole(e.Role), e.Id, "-", [new OversightCite("event", e.Id)],
                new Dictionary<string, string>
                {
                    ["kind"] = e.Kind, ["raised"] = T(e.CreatedAt),
                    ["dispositioned"] = dispositioned ? $"{e.Disposition}, consumed at {T(e.ConsumedAt!.Value)}"
                        : e.Disposition is { } blocked ? $"not yet, {blocked}" : "not yet",
                    ["hours"] = N((long)took.TotalHours),
                });
        }
    }
}

/// <summary>
/// <c>material-unwoken</c>: a material wake — a class at or above Unblocking (<see cref="WakePriority"/>) — due and unconsumed past a level's
/// maximum interval: its position did not wake on it (§ 9 "the chief not waking on material events", generic per position).
/// </summary>
public sealed class MaterialUnwokenDetector : OversightDetector
{
    public override string Name => "material-unwoken";

    public override string Blind =>
        "It sees only the app's wake rows, so a position that read the matter some other way and never consumed the wake still counts as unwoken; " + CliBlind + ".";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var e in s.Wakes.Where(e => e.ConsumedAt is null && WakePriority.ClassOf(e.Kind) is { } c && c <= WakeClass.Unblocking))
        {
            if (w.Now - e.DueAt <= OversightPolicy.MaterialMaxInterval) continue;
            yield return new Candidate(s.UnitOfRole(e.Role), e.Id, "-", [new OversightCite("event", e.Id)],
                new Dictionary<string, string>
                {
                    ["kind"] = e.Kind, ["class"] = WakePriority.ClassOf(e.Kind)!.Value.ToString(), ["due"] = T(e.DueAt),
                    ["hours"] = N((long)(w.Now - e.DueAt).TotalHours), ["role"] = e.Role ?? CouncilRoles.Default,
                });
        }
    }
}

/// <summary>
/// <c>refusals</c>: refusals at the gateway (a tool call the app did not serve) or at the pipe (the engineering log's Ipc refusals). The pipe's
/// <c>protocol_rejected</c> and <c>operator_session_refused</c> are SEVERE; an <c>op_refused</c> — a refusal of rights or authority at the
/// pipe's one handler (<c>U-org-watcher-b</c>) — is info, in its attempt's unit. A refusal is ONE finding: a call through a worker's or the
/// door's tools is its <c>tool_call</c> row alone, and the pipe writes <c>op_refused</c> only for a frame from its read loop (fix pass 1, R4).
/// The log is pruned, so it is read by cursor and the facts copied in.
/// </summary>
public sealed class RefusalsDetector : OversightDetector
{
    public override string Name => "refusals";

    /// <summary>The pipe refusals this line holds SEVERE.</summary>
    public static readonly string[] SevereEvents = ["protocol_rejected", "operator_session_refused"];

    public override string Blind =>
        "It sees only what the app refused and recorded: a tool call the app did not serve, whatever the reason; and on the pipe, a launch "
        + "grant it refused (grant_rejected), a frame without the app's machine token (auth_rejected), a frame with no protocol version or "
        + "another one — a client that is not this app's (protocol_rejected, severe), a frame naming the owner's reserved session "
        + "(operator_session_refused, severe), and a request its handler refused on rights or authority (op_refused). A request the handler "
        + "refused only because what it said was malformed is not counted; " + CliBlind + ".";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        var units = s.Attempts.Where(a => a.UnitId is not null).ToDictionary(a => a.Id, a => a.UnitId!, StringComparer.Ordinal);
        foreach (var group in s.ToolRefusals.GroupBy(t => t.Attempt is { } a ? "attempt:" + a : "role:" + (t.Role ?? CouncilRoles.Default)))
        {
            var first = group.First();
            var unit = first.Attempt is { } at && units.TryGetValue(at, out var u) ? u : s.UnitOfRole(first.Role);
            IEnumerable<OversightCite> cites = group.Select(t => new OversightCite("tool_call", N(t.Id)));
            if (first.Attempt is { } attempt) cites = cites.Prepend(new OversightCite("attempt", attempt));
            yield return new Candidate(unit, group.Key, "tool_call:" + N(group.Min(t => t.Id)), [.. cites],
                new Dictionary<string, string>
                {
                    ["refused"] = N(group.Count()), ["tools"] = string.Join(",", group.Select(t => t.Tool).Distinct().Order(StringComparer.Ordinal)),
                    ["role"] = first.Role ?? CouncilRoles.Default,
                });
        }
        foreach (var p in s.PipeRefusals.Where(p => p.Event == RefusalMark.Event))
        {
            // A REFUSAL OF RIGHTS OR AUTHORITY AT THE PIPE (U-org-watcher-b): in its attempt's unit, as a tool refusal is; a caller with no
            // grant is nobody's, so the root's.
            var unit = p.Attempt is { } a && units.TryGetValue(a, out var u) ? u : p.Role is null ? OrgIds.Root : s.UnitOfRole(p.Role);
            IEnumerable<OversightCite> cites = [new OversightCite("log", N(p.Id))];
            if (p.Attempt is { } attempt && s.Attempts.Any(x => x.Id == attempt)) cites = cites.Append(new OversightCite("attempt", attempt));
            yield return new Candidate(unit, "log:" + N(p.Id), "-", [.. cites],
                new Dictionary<string, string>
                {
                    ["op"] = p.Op ?? "unknown", ["code"] = p.Code ?? "unknown", ["role"] = p.Role ?? "none", ["at"] = T(p.At),
                });
        }
        foreach (var p in s.PipeRefusals.Where(p => p.Event != RefusalMark.Event))
            yield return new Candidate(OrgIds.Root, "log:" + N(p.Id), "-", [new OversightCite("log", N(p.Id))],
                new Dictionary<string, string> { ["event"] = p.Event, ["at"] = T(p.At), ["session"] = p.Session ?? "none" },
                Array.IndexOf(SevereEvents, p.Event) >= 0 ? OversightSeverity.Severe : null);
    }
}

/// <summary>
/// <c>claim-without-evidence</c>: a result claimed with no evidence id — a delivery ACCEPTED with no refs, or a research-ledger revision marked
/// <c>claim</c> with no ledger link at that revision.
/// </summary>
public sealed class ClaimWithoutEvidenceDetector : OversightDetector
{
    public override string Name => "claim-without-evidence";

    public override string Blind =>
        "It reads whether a claim names evidence, never whether the evidence named supports it; " + CliBlind + ".";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var (e, m, d) in Accepted(s))
            if (d is not null && d.Refs.Trim() == "[]")
                yield return new Candidate(m.UnitId, "delivery:" + d.Id, "-",
                    [new OversightCite("mandate", m.Id), new OversightCite("delivery", d.Id)],
                    new Dictionary<string, string> { ["accepted"] = T(e.At), ["by"] = e.Actor, ["refs"] = "0" });
        foreach (var c in s.BareClaims.Where(c => w.Now - c.At >= OversightPolicy.ClaimGrace))
        {
            IEnumerable<OversightCite> cites = [new OversightCite("ledger", N(c.EntryId))];
            if (c.Attempt is { } a) cites = cites.Append(new OversightCite("attempt", a));
            yield return new Candidate(s.UnitOfRole(c.Author), $"ledger:{N(c.EntryId)}r{N(c.Revision)}", "-", [.. cites],
                new Dictionary<string, string> { ["revision"] = N(c.Revision), ["at"] = T(c.At), ["author"] = c.Author, ["links"] = "0" });
        }
    }
}

/// <summary>
/// <c>accepted-failed-check</c> (SEVERE): the decidable "report contradicting a measurement" — the decider accepted a delivery a check the
/// APP ran on it failed.
/// </summary>
public sealed class AcceptedFailedCheckDetector : OversightDetector
{
    public override string Name => "accepted-failed-check";

    protected override string Severity => OversightSeverity.Severe;

    public override string Blind =>
        "It sees only the checks the app itself ran on a delivery, not a contradiction the checks do not test; " + CliBlind + ".";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var (e, m, d) in Accepted(s))
        {
            if (d is null) continue;
            var failed = s.Checks.Where(c => c.DeliveryId == d.Id && !c.Passed).ToList();
            if (failed.Count == 0) continue;
            yield return new Candidate(m.UnitId, "delivery:" + d.Id, "-",
                [new OversightCite("mandate", m.Id), new OversightCite("delivery", d.Id), .. failed.Select(c => new OversightCite("check", $"{d.Id}/{c.Name}"))],
                new Dictionary<string, string>
                {
                    ["accepted"] = T(e.At), ["by"] = e.Actor, ["failed"] = string.Join(",", failed.Select(c => c.Name)),
                });
        }
    }
}

/// <summary><c>dead-version-rerun</c>: the exact version a promotion REFUSED, run again after the refusal (near-duplicates wait for a genome bank).</summary>
public sealed class DeadVersionRerunDetector : OversightDetector
{
    public override string Name => "dead-version-rerun";

    public override string Blind =>
        "It matches only the exact version id, so a refused idea re-submitted with any change is invisible to it; " + CliBlind + ".";

    protected override IEnumerable<Candidate> Find(OrgSnapshot s, OversightWindow w)
    {
        foreach (var p in s.RefusedPromotions)
            foreach (var r in s.Runs.Where(r => r.VersionId == p.VersionId && r.CreatedAt > p.At))
                yield return new Candidate(s.UnitOfRole(r.Role), "run:" + r.Id, "-",
                    [new OversightCite("promotion", p.Id), new OversightCite("run", r.Id)],
                    new Dictionary<string, string> { ["version"] = r.VersionId, ["refused"] = T(p.At), ["ran"] = T(r.CreatedAt), ["role"] = r.Role ?? CouncilRoles.Default });
    }
}
