using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.FloorDemo;

// THE FLOOR DEMO (U-ui-alive) — FOR DEVELOPERS ONLY. It writes SYNTHETIC rows into a scratch home so the app's floor can be watched
// without spending a model turn: no row it writes is a real model's work, a real order or a real result. It runs only in a home named
// explicitly whose folder name starts with "demo-", never in the default home, so it can never be pointed at a real home by mistake.
//
//   dotnet run --project tests/TradeAgent.FloorDemo -- seed <home-dir>
//       a fresh home: the database as the app makes it, onboarding complete, a working organisation and ~36 hours of history to now.
//   dotnet run --project tests/TradeAgent.FloorDemo -- play <home-dir> [--minutes N] [--pace S]
//       appends rows in real time (defaults 30 minutes, 2.5 s a step), next to a running app on the same home; every turn it opens
//       it ends before it exits, Ctrl-C included.

const string Usage = "usage: floor-demo seed <home-dir> | play <home-dir> [--minutes N] [--pace S]   (the home's folder name starts with demo-)";

if (args.Length < 2 || args[0] is not ("seed" or "play"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var home = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
if (!Path.GetFileName(home).StartsWith("demo-", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"refused: {home} is not a demo home — its folder name must start with \"demo-\". Nothing was written.");
    return 2;
}

// THE HOME IS THIS PROCESS'S BEFORE ANYTHING READS PATHS: every store that looks a path up finds the demo home, never the real one.
Environment.SetEnvironmentVariable("TRADEAGENT_HOME", home);
if (string.Equals(Path.GetFullPath(Paths.DefaultHome()).TrimEnd(Path.DirectorySeparatorChar), home, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"refused: {home} is the default home. Nothing was written.");
    return 2;
}

var file = Paths.DatabaseFile;
if (args[0] == "seed")
{
    if (File.Exists(file))
    {
        Console.Error.WriteLine($"refused: {file} exists. seed writes a fresh home; delete the folder first. Nothing was written.");
        return 2;
    }
    using var db = new Database(file);
    Seed.Write(new Ledger(db, new Random(17)), DateTimeOffset.UtcNow);
    Console.WriteLine($"seeded {file}");
    return 0;
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"refused: {file} does not exist; run seed first.");
    return 2;
}
var minutes = 30.0;
var pace = 2.5;
for (var i = 2; i + 1 < args.Length; i += 2)
{
    var ok = args[i] switch
    {
        "--minutes" => double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out minutes),
        "--pace" => double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out pace),
        _ => false
    };
    if (!ok || minutes <= 0 || pace <= 0)
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
using (var db = new Database(file))
    await Play.Run(new Ledger(db, new Random()), TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(pace));
return 0;

/// <summary>THE SEED: a working organisation and about 36 hours of plausible, synthetic history ending at <c>now</c>.</summary>
static class Seed
{
    public static void Write(Ledger l, DateTimeOffset now)
    {
        var org = l.Org;
        var onboarding = new OnboardingStore(l.Db);
        foreach (var step in OnboardingSteps.Order) onboarding.Complete(step, "floor demo");

        var t0 = now.AddHours(-36);

        // THE HOME IS AS OLD AS ITS HISTORY. The database was made a moment ago, so the rows it seeds itself with — the organisation, its
        // two divisions and their directors — are dated now, a day and a half after the work this writes under them. A replay shows a seat
        // from the moment it was founded, so those rows are dated back to where the history starts.
        l.Exec("UPDATE org_unit SET created_at=$at", ("$at", Ledger.T(t0)));
        l.Exec("UPDATE org_position SET created_at=$at", ("$at", Ledger.T(t0)));
        l.Exec("UPDATE org_event SET at=$at", ("$at", Ledger.T(t0)));

        // THE CHART: the chief (appointed by the owner, then activated), the two seeded directors as the app seeds them, two Research teams
        // and one Operations team, and one Research unit chartered with its head still vacant.
        var chief = org.AppointChief(new ChiefSeat("claude", "claude-opus"), _ => true, t0.AddMinutes(2)).Chief!;
        org.Activate(chief, _ => SeatClasses.Harness, 10, t0.AddMinutes(4), () => true);

        var at = t0.AddMinutes(10);
        DateTimeOffset Later() => at = at.AddMinutes(l.Next(3, 9));
        (string Unit, string Head, List<string> Executors) Team(string parent, string director, int active, int dormant)
        {
            var unit = l.Unit(parent, director, Later());
            var head = l.Hire(unit, director, head: true, Later());
            l.Activate(head, unit, director, Later());
            var executors = new List<string>();
            for (var i = 0; i < active + dormant; i++)
            {
                var e = l.Hire(unit, head, head: false, Later());
                if (i < active) { l.Activate(e, unit, head, Later()); executors.Add(e); }
            }
            return (unit, head, executors);
        }
        var teams = new[]
        {
            Team(OrgIds.Research, CouncilRoles.Research, 3, 0),
            Team(OrgIds.Research, CouncilRoles.Research, 1, 1),
            Team(OrgIds.Operations, CouncilRoles.Operations, 1, 0),
        };
        l.Unit(OrgIds.Research, CouncilRoles.Research, t0.AddHours(2));

        // THE HISTORY, as jobs run in the order they started: a turn is written whole (launch, calls, acts, end), and a later job never
        // starts before an earlier one's acts, so every mandate's events land in time order.
        var jobs = new List<(DateTimeOffset Start, Action Run)>();
        var stop = now.AddMinutes(-14);
        void Job(DateTimeOffset start, Action run) { if (start < stop) jobs.Add((start, run)); }

        var escalatedToHead = false;
        var deliverLast = false;
        foreach (var (unit, head, executors) in teams)
            foreach (var executor in executors)
            {
                var t = t0.AddHours(2.5 + l.Next() * 3);
                var last = t0;
                while (t < stop)
                {
                    var issue = t;
                    string? mandate = null;
                    Job(issue, () => mandate = Turn(l, head, issue, null, (turn, end) =>
                    {
                        l.Tool(turn, head, end.AddSeconds(-5), ("mandate_assign", "mandate-assign"));
                        return l.Mandate(unit, head, executor, turn, end);
                    }));
                    var work = issue.AddMinutes(l.Next(35, 70));
                    var passes = l.Next() > 0.15;
                    var escalate = !escalatedToHead && unit == teams[1].Unit && work > t0.AddHours(8);
                    escalatedToHead |= escalate;
                    Job(work, () => Turn(l, executor, work, mandate, (turn, end) =>
                    {
                        if (escalate)
                            l.Escalate(executor, unit, head, turn, mandate, "The dataset the mandate names stops a month short of the window it asks for.", end.AddMinutes(-1));
                        l.Deliver(mandate!, executor, turn, end, passes);
                    }));
                    var judge = work.AddMinutes(l.Next(40, 80));
                    if (!passes)
                    {
                        var again = work.AddMinutes(l.Next(20, 30));
                        Job(again, () => Turn(l, executor, again, mandate, (turn, end) => l.Deliver(mandate!, executor, turn, end)));
                        judge = again.AddMinutes(l.Next(40, 80));
                    }
                    var reject = l.Next() < 0.25;
                    Job(judge, () => Turn(l, head, judge, null, (turn, end) =>
                    {
                        l.Tool(turn, head, end.AddSeconds(-5), reject ? ("mandate_reject", "mandate-reject") : ("mandate_accept", "mandate-accept"));
                        l.MandateEvent(mandate!, end, reject ? MandateEventKind.Rejected : MandateEventKind.Accepted, head, turn,
                            reject ? l.Pick(Ledger.Rejections) : null);
                    }));
                    if (reject)
                    {
                        var redo = judge.AddMinutes(l.Next(30, 50));
                        Job(redo, () => Turn(l, executor, redo, mandate, (turn, end) => l.Deliver(mandate!, executor, turn, end)));
                        var final = redo.AddMinutes(l.Next(40, 70));
                        Job(final, () => Turn(l, head, final, null, (turn, end) =>
                        {
                            l.Tool(turn, head, end.AddSeconds(-5), ("mandate_accept", "mandate-accept"));
                            l.MandateEvent(mandate!, end, MandateEventKind.Accepted, head, turn, null);
                        }));
                        judge = final;
                    }
                    t = judge.AddHours(2 + l.Next() * 3);
                    last = judge;
                }

                // WORK STILL OPEN AT NOW: a last mandate per executor, issued within the hour — every other one already delivered and
                // waiting for its head's judgement.
                var closing = now.AddMinutes(-l.Next(45, 70));
                if (closing <= last.AddMinutes(15)) continue;
                string? open = null;
                Job(closing, () => open = Turn(l, head, closing, null, (turn, end) =>
                {
                    l.Tool(turn, head, end.AddSeconds(-5), ("mandate_assign", "mandate-assign"));
                    return l.Mandate(unit, head, executor, turn, end);
                }));
                if (deliverLast = !deliverLast)
                {
                    var work = closing.AddMinutes(l.Next(18, 28));
                    Job(work, () => Turn(l, executor, work, open, (turn, end) => l.Deliver(open!, executor, turn, end)));
                }
            }

        // TURNS OF THEIR OWN for the chief, the directors and the heads: a decision now and then, a word to a peer, one escalation to the
        // owner; two whose cost nothing priced, and one lost to a restart.
        List<string> plain = [chief, CouncilRoles.Operations, CouncilRoles.Research, .. teams.Select(t => t.Head)];
        var unpriced = 2;
        var lost = 1;
        var escalatedToOwner = false;
        foreach (var role in plain)
            for (var t = t0.AddHours(1 + l.Next() * 3); t < stop; t = t.AddHours(3 + l.Next() * 3))
            {
                var start = t;
                var cost = unpriced > 0 && start > t0.AddHours(12) && role != chief ? (decimal?)null : l.Cost();
                if (cost is null) unpriced--;
                var lose = lost > 0 && role == CouncilRoles.Research && start > t0.AddHours(20);
                if (lose) lost--;
                var toOwner = !escalatedToOwner && role == teams[0].Head && start > t0.AddHours(18);
                escalatedToOwner |= toOwner;
                Job(start, () => Turn(l, role, start, null, (turn, end) =>
                {
                    if (toOwner)
                        l.Escalate(role, teams[0].Unit, null, turn, null,
                            "The team's allowance for the week will run out on Thursday at this pace; raising it is your decision.", end);
                    else if (role != CouncilRoles.Operations && role != CouncilRoles.Research && l.Next() < 0.3)
                    {
                        l.Tool(turn, role, end.AddSeconds(-3), ("decision_record", "decision-record"));
                        l.Decide(role, turn, end);
                    }
                    else if (role == teams[0].Head && l.Next() < 0.3)
                        l.Lateral(role, teams[1].Head, turn, "We hold a second source for the hourly bars if your team needs one.", end);
                }, cost, lose));
            }

        foreach (var (_, run) in jobs.OrderBy(j => j.Start)) run();

        new MissionEventStore(l.Db).RecordOwnerMessage("How is the team doing this week, and what has it cost so far?", now.AddMinutes(-40));
    }

    /// <summary>
    /// One whole turn of <paramref name="role"/> from <paramref name="start"/>: its launch, 3–12 calls spread through it, its act at the
    /// end, and its end — priced at <paramref name="cost"/> (null: unknown), or lost when <paramref name="lose"/>. Returns what the act made.
    /// </summary>
    static string? Turn(Ledger l, string role, DateTimeOffset start, string? mandate, Func<string, DateTimeOffset, string?>? act,
        decimal? cost = 0m, bool lose = false)
    {
        var turn = l.Begin(role, start, mandate);
        var end = start.AddMinutes(l.Next(2, 13)).AddSeconds(l.Next(0, 60));
        var calls = l.Next(3, 13);
        for (var i = 1; i <= calls; i++) l.Tool(turn, role, start + (end - start) * i / (calls + 1));
        var made = act?.Invoke(turn, end);
        if (lose) l.LoseOpen(end);
        else l.End(turn, end, cost == 0m ? l.Cost() : cost, "claude-sonnet");
        return made;
    }

    static string? Turn(Ledger l, string role, DateTimeOffset start, string? mandate, Action<string, DateTimeOffset> act,
        decimal? cost = 0m, bool lose = false) =>
        Turn(l, role, start, mandate, (turn, end) => { act(turn, end); return null; }, cost, lose);
}
