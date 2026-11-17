using System.Globalization;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.FloorDemo;

/// <summary>
/// THE DEMO IN REAL TIME: SYNTHETIC rows appended with <c>at</c> = now, one step every pace, following the organisation's loop — a turn opens,
/// its calls land, a head issues a mandate, the holder's turn delivers, the head's next turn accepts or rejects; now and then a decision,
/// an escalation, a hire into the vacant unit and its activation. At most three turns open at once, and every turn it opened is ended
/// before it returns, a Ctrl-C or a SIGTERM included (a SIGKILL cannot be caught: the app's next start declares those turns lost).
/// </summary>
static class Play
{
    const int MaxOpen = 3;

    enum Work { Plain, Issue, Deliver, Judge }

    sealed record Open(string Id, string Role, string Unit, Work Work, string? Mandate, string? Holder)
    {
        public int CallsLeft { get; set; }
    }

    public static async Task Run(Ledger l, TimeSpan length, TimeSpan pace)
    {
        using var stop = new CancellationTokenSource();
        // CTRL-C, AND A PLAIN `kill`: either one stops the loop, and the finally below ends every turn this run opened.
        void Stop(System.Runtime.InteropServices.PosixSignalContext c) { c.Cancel = true; stop.Cancel(); }
        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, Stop);
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Stop);
        var open = new List<Open>();
        var deadline = DateTimeOffset.UtcNow + length;
        (string Position, string Unit, string Decider, int At)? hire = null;
        var step = 0;
        try
        {
            while (!stop.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                step++;
                Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss}  {Step(l, open, ref hire, step)}");
                try { await Task.Delay(pace, stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            foreach (var turn in open.ToList()) Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss}  {Close(l, turn, open)} (closing)");
        }
    }

    static string Step(Ledger l, List<Open> open, ref (string Position, string Unit, string Decider, int At)? hire, int step)
    {
        var now = DateTimeOffset.UtcNow;

        // THE VACANT UNIT, once a run: a head hired into it by the director above it, activated a few steps later.
        if (hire is { } h && step >= h.At)
        {
            hire = (h.Position, h.Unit, h.Decider, int.MaxValue);
            l.Activate(h.Position, h.Unit, h.Decider, now);
            return $"{h.Decider} activates {h.Position}, the new head of {h.Unit}";
        }
        if (hire is null && step >= 12 && l.Next() < 0.05 && Vacant(l) is { } vacant)
        {
            var position = l.Hire(vacant.Id, vacant.Decider, head: true, now);
            hire = (position, vacant.Id, vacant.Decider, step + 6);
            return $"{vacant.Decider} hires {position} to head {vacant.Id}";
        }

        if (open.Count < MaxOpen && (open.Count == 0 || l.Next() < 0.3) && Choose(l, open) is { } next)
        {
            var id = l.Begin(next.Role, now, next.Work == Work.Deliver ? next.Mandate : null);
            var turn = next with { Id = id };
            turn.CallsLeft = l.Next(3, 9);
            open.Add(turn);
            return $"{turn.Role} opens a turn ({Words(turn)})";
        }
        if (open.Count == 0) return "nobody is working";

        var t = l.Pick(open);
        if (t.CallsLeft-- > 0)
        {
            var (tool, argument, served) = l.Tool(t.Id, t.Role, now);
            return $"{t.Role} calls {tool} \"{argument}\"{(served ? "" : " - refused")}";
        }
        return Close(l, t, open);
    }

    /// <summary>The turn's act, then its end with a cost (one in twelve unpriced, outside a mandate).</summary>
    static string Close(Ledger l, Open t, List<Open> open)
    {
        var now = DateTimeOffset.UtcNow;
        var did = "";
        switch (t.Work)
        {
            case Work.Issue:
                l.Tool(t.Id, t.Role, now, ("mandate_assign", "mandate-assign"));
                did = $"issues {l.Mandate(t.Unit, t.Role, t.Holder!, t.Id, now)} to {t.Holder}";
                break;
            case Work.Deliver:
                var passes = l.Next() > 0.15;
                l.Deliver(t.Mandate!, t.Role, t.Id, now, passes);
                did = $"delivers {t.Mandate}{(passes ? "" : " (a check fails, the app retries)")}";
                break;
            case Work.Judge:
                var accept = l.Next() < 0.75;
                l.Tool(t.Id, t.Role, now, accept ? ("mandate_accept", "mandate-accept") : ("mandate_reject", "mandate-reject"));
                l.MandateEvent(t.Mandate!, now, accept ? MandateEventKind.Accepted : MandateEventKind.Rejected, t.Role, t.Id,
                    accept ? null : l.Pick(Ledger.Rejections));
                did = $"{(accept ? "accepts" : "rejects")} {t.Mandate}";
                break;
            default:
                var roll = l.Next();
                if (roll < 0.25 && !CouncilRoles.IsKnown(t.Role))
                {
                    l.Tool(t.Id, t.Role, now, ("decision_record", "decision-record"));
                    l.Decide(t.Role, t.Id, now);
                    did = "records a decision";
                }
                else if (roll < 0.35 && HeadAbove(l, t.Role) is { } above)
                {
                    l.Escalate(t.Role, t.Unit, above == FloorEvent.Owner ? null : above, t.Id, null,
                        "The work queued for this unit needs more allowance than it has left this week.", now);
                    did = $"escalates to {above}";
                }
                break;
        }
        var cost = t.Work == Work.Plain && l.Next() < 1.0 / 12 ? (decimal?)null : l.Cost();
        var ended = l.End(t.Id, now.AddMilliseconds(1), cost, "claude-sonnet");
        open.Remove(t);
        var price = cost is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "unknown";
        return $"{t.Role} {(did.Length > 0 ? did + ", and " : "")}ends its turn"
               + (ended ? $" (cost {price})" : " (already ended elsewhere: a restart declared it lost)");
    }

    /// <summary>The next turn worth opening for a position not already in one: a delivery owed, a delivery to judge, a mandate to issue, or its own.</summary>
    static Open? Choose(Ledger l, List<Open> open)
    {
        var org = l.Org;
        var units = org.Units();
        var busy = open.Select(o => o.Role).ToHashSet(StringComparer.Ordinal);
        var free = org.Positions().Where(p => p.Status == OrgPositionStatus.Active && !busy.Contains(p.Id)).ToList();
        var owed = open.Where(o => o.Mandate is not null).Select(o => o.Mandate!).ToHashSet(StringComparer.Ordinal);
        var options = new List<Open>();
        foreach (var p in free)
        {
            foreach (var m in org.MandatesOwedBy(p.Id).Where(m => !owed.Contains(m)))
                options.Add(new Open("", p.Id, p.UnitId, Work.Deliver, m, null));
            foreach (var m in org.DeliveriesToJudge(p.Id).Where(m => !owed.Contains(m)))
                options.Add(new Open("", p.Id, p.UnitId, Work.Judge, m, null));
            var headed = units.FirstOrDefault(u => u.HeadPositionId == p.Id && u.Kind == OrgUnitKind.Team);
            if (headed is not null)
                foreach (var member in org.Positions().Where(x => x.UnitId == headed.Id && x.Id != p.Id && x.Status == OrgPositionStatus.Active))
                    if (org.MandatesOwedBy(member.Id).Count == 0 && !open.Any(o => o.Holder == member.Id))
                        options.Add(new Open("", p.Id, headed.Id, Work.Issue, null, member.Id));
        }
        if (options.Count == 0 || l.Next() < 0.25)
            options.AddRange(free.Select(p => new Open("", p.Id, p.UnitId, Work.Plain, null, null)));
        return options.Count == 0 ? null : l.Pick(options);
    }

    /// <summary>The head of the unit above a team head, or the owner for the chief's line; null for anyone else.</summary>
    static string? HeadAbove(Ledger l, string role)
    {
        var units = l.Org.Units();
        var unit = units.FirstOrDefault(u => u.HeadPositionId == role);
        if (unit is null) return null;
        if (unit.ParentId is null || unit.Kind != OrgUnitKind.Team) return FloorEvent.Owner;
        return units.FirstOrDefault(u => u.Id == unit.ParentId)?.HeadPositionId ?? FloorEvent.Owner;
    }

    /// <summary>A team with no head, open, under a unit that has one (who hires into it).</summary>
    static (string Id, string Decider)? Vacant(Ledger l)
    {
        var units = l.Org.Units();
        foreach (var u in units.Where(u => u.Kind == OrgUnitKind.Team && u.HeadPositionId is null && u.Status != OrgUnitStatus.Closed))
            if (units.FirstOrDefault(p => p.Id == u.ParentId)?.HeadPositionId is { } decider)
                return (u.Id, decider);
        return null;
    }

    static string Words(Open t) => t.Work switch
    {
        Work.Issue => $"to give {t.Holder} a mandate",
        Work.Deliver => $"on {t.Mandate}",
        Work.Judge => $"to judge the delivery on {t.Mandate}",
        _ => "its own work"
    };
}
