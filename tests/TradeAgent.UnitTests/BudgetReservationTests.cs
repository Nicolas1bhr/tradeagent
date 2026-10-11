using Avalonia.Controls;
using Avalonia.Interactivity;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// TWO ADMITTED TURNS CANNOT BOTH SPEND THE SAME ALLOWANCE — the property <c>docs/COUNCIL.md</c>
/// rule 3 states as "budgets are reserved, not checked", and Astra's round-1 wording for this unit.
///
/// The defect it closes is not a rounding error and is not the crash path <c>U-model</c> closed.
/// Admission read the day's totals in the loop and the row went in sixty lines later, under a
/// different lock: two launches could both be told "there is room for one" and both then take it.
/// The owner's own chat turn made that ordinary rather than exotic — it is a second launch, on a
/// second conversation, that no gate ever saw.
///
/// The race below is run with TWO REAL THREADS against ONE <see cref="Database"/>, because "both
/// were admitted" is a fact about two callers and a shared table and a stand-in for either would
/// exercise neither.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class BudgetReservationTests : IDisposable
{
    readonly Database _db = TestEnv.NewDb();
    readonly string _records = Path.Combine(TestEnv.Home, $"reserve-{Guid.NewGuid():n}.jsonl");

    public void Dispose()
    {
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        _db.Dispose();
    }

    /// <summary>The owner's own rate, so the reservation has one right answer: 1.20 M in at 1.00 and 20 k out at 4.00.</summary>
    static readonly OwnerPrice Rate = new(1m, 4m);

    const decimal Reservation = (1_200_000m * 1m + 20_000m * 4m) / 1_000_000m;   // 1.28

    /// <summary>
    /// A meter whose role shares are the WHOLE day unless a test says otherwise, so a test about the
    /// owner's ceiling is not silently answered by a role's half of it.
    /// </summary>
    TurnMeter Meter(decimal cap, Func<DateTimeOffset> now, Func<string, decimal>? share = null) =>
        new(_db, () => cap, runtimeId: () => "codex", now: now, recordPath: _records,
            owner: () => Rate, model: () => "gpt-5.6-sol", share: share ?? (_ => 1m));

    int Rows(string state)
    {
        using var c = _db.Cmd("SELECT COUNT(*) FROM ai_attempt WHERE state=$s", ("$s", state));
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>
    /// THE GUARD. A ceiling with room for exactly one reservation, two threads that have BOTH been
    /// told there is room, and one reservation row at the end of it.
    ///
    /// The barrier is what makes this a race rather than a sequence: neither thread reaches
    /// <see cref="TurnMeter.Begin"/> until both have taken the loop's cheap first look and been
    /// answered yes. With the comparison outside the transaction that is enough for both to commit.
    /// </summary>
    [Fact]
    public void Two_admissions_racing_on_one_database_write_exactly_one_reservation()
    {
        var now = DateTimeOffset.Now;
        // 1.28 fits under 2.00 and 2.56 does not, so the ledger has room for exactly one.
        var meter = Meter(2m, () => now);

        var looked = new bool[2];
        var admissions = new AiAdmission[2];
        using var read = new Barrier(2);

        void Admit(int i)
        {
            looked[i] = meter.Today.AdmitsAnotherTurn;   // the loop's pre-check, on an empty ledger
            read.SignalAndWait();                        // neither commits until both have looked
            admissions[i] = meter.Begin("## Situation", role: CouncilRoles.Operations);
        }

        var a = new Thread(() => Admit(0));
        var b = new Thread(() => Admit(1));
        a.Start();
        b.Start();
        Assert.True(a.Join(TimeSpan.FromSeconds(30)), "the first admission never finished");
        Assert.True(b.Join(TimeSpan.FromSeconds(30)), "the second admission never finished");

        // Both were told there was room. That is the race, not a defect: it is what a cheap first
        // look can honestly say, and it is why the look cannot be the gate.
        Assert.Equal([true, true], looked);

        Assert.Equal(1, Rows(nameof(AiAttemptState.LAUNCHED)));
        Assert.Equal(Reservation, meter.Today.Reserved);

        var admitted = Assert.Single(admissions, x => x.Admitted);
        Assert.NotNull(admitted.Id);

        var refused = Assert.Single(admissions, x => !x.Admitted);
        Assert.Null(refused.Id);
        Assert.Equal(AiAdmission.Day, refused.Ceiling);
        Assert.Equal(Labels.DailySpendingLimitReached, refused.Refusal);
        // 0 spent + 1.28 committed + 1.28 asked for, against 2.00.
        Assert.Equal(0.56m, refused.Over);
    }

    /// <summary>
    /// The gate is the transaction's OWN reading and not the caller's: a look taken while the ledger
    /// was empty does not admit a turn once the room it saw has gone.
    /// </summary>
    [Fact]
    public void A_look_taken_before_the_room_went_does_not_admit_the_turn_it_said_yes_to()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(2m, () => now);

        var stale = meter.Today;
        Assert.True(stale.AdmitsAnotherTurn);

        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);

        var refused = meter.Begin("## Situation", role: CouncilRoles.Operations);
        Assert.False(refused.Admitted);
        Assert.Null(refused.Id);
        Assert.Equal(1, Rows(nameof(AiAttemptState.LAUNCHED)));
    }

    /// <summary>
    /// A ROLE'S SHARE REFUSES ON ITS OWN, and says so as its own ceiling: the day's money is not
    /// gone, one role's slice of it is, and the two have different repairs on the Safety page.
    /// </summary>
    [Fact]
    public void A_roles_share_refuses_the_launch_while_the_day_still_has_room()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(10m, () => now, share: _ => 0.1m);   // 1.00 of a 10.00 day

        var refused = meter.Begin("## Situation", role: CouncilRoles.Research);

        Assert.False(refused.Admitted);
        Assert.Equal(CouncilRoles.Research, refused.Ceiling);
        Assert.Equal(Labels.RoleShareReached(CouncilRoles.Title(CouncilRoles.Research)), refused.Refusal);
        Assert.Equal(0.28m, refused.Over);          // 1.28 asked for against a 1.00 share
        Assert.Equal(0, Rows(nameof(AiAttemptState.LAUNCHED)));
        Assert.True(meter.Today.AdmitsAnotherTurn); // the DAY is nowhere near its ceiling
    }

    /// <summary>
    /// A REFUSED LAUNCH CONSUMES NOTHING. The reason to take a turn is still owed one: spending the
    /// wake on a turn that never ran would lose it at midnight along with the money that refused it.
    /// </summary>
    [Fact]
    public void A_refused_launch_leaves_its_wakes_unconsumed()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("owner:1", MissionEventKind.Owner, now);

        var meter = Meter(2m, () => now);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);

        Assert.False(meter.Begin("## Situation", ["owner:1"], CouncilRoles.Operations).Admitted);
        Assert.Null(events.Get("owner:1")!.ConsumedBy);
    }

    /// <summary>
    /// THE OWNER'S OWN TURN IS RESERVED TOO, AND CLOSES ITS OWN ROW.
    ///
    /// It ran beside a Research turn — the chair's session is not busy while another role's process
    /// is — and the meter held ONE open attempt, so the chat's usage resolved the Research
    /// Director's reservation and the Research turn then opened a second row with nothing reserved
    /// on it. Two launches, one commitment, and a day's ceiling short by a whole turn.
    /// </summary>
    [Fact]
    public async Task The_owners_chat_turn_beside_a_research_turn_reserves_and_closes_its_own_row()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(50m, () => now);                      // room for several turns
        var store = new AiAttemptStore(_db);

        // The Research Director is mid-turn: its row is open and its allowance committed.
        var research = meter.Begin("## Situation", role: CouncilRoles.Research);
        Assert.True(research.Admitted);

        // The owner types into the chair's conversation while that is still running.
        var chat = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);
        await chat.SendAsync("what are you doing?");

        // The Research turn then ends, with its own usage.
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(4), "", now)
        {
            Usage = new TurnUsage(1_000, 0, 0, 10, 0, null)
        }, CouncilRoles.Research);
        meter.CommitStaged(CouncilRoles.Research);   // the mission loop's own commit, where this role's close lands

        var rows = store.Between(now.AddDays(-1), now.AddDays(1));
        Assert.Equal(2, rows.Count);

        // NEITHER of them is a turn nobody committed for.
        Assert.All(rows, r => Assert.Equal(Reservation, r.ReservedCost));
        Assert.All(rows, r => Assert.Equal(AiAttemptState.ENDED, r.State));

        var mission = Assert.Single(rows, r => r.Id == research.Id);
        Assert.Equal(CouncilRoles.Research, mission.Role);
        Assert.Equal((1_000m * 1m + 10m * 4m) / 1_000_000m, mission.Cost);

        var typed = Assert.Single(rows, r => r.Id != research.Id);
        Assert.Equal(CouncilRoles.Operations, typed.Role);
        Assert.Equal((17_232m * 1m + 6m * 4m) / 1_000_000m, typed.Cost);
    }

    /// <summary>
    /// ITEM 2, RED FIRST: TWO ROLES' TURNS OVERLAP, AND EACH CLOSE LANDS IN ITS OWN ROLE'S
    /// TRANSITION.
    ///
    /// <para>The close of a mission turn is measured when the runtime reports its usage and HELD
    /// until the loop's own committed transition (<c>docs/COUNCIL.md</c> rule 6). That hold was ONE
    /// slot. With two roles turning, the second role to end overwrote the first one's held close, so
    /// the first role's commit wrote the OTHER role's close — inside the first role's transaction,
    /// against the first role's published work — and the first role's row stayed LAUNCHED until a
    /// restart declared it LOST and charged it its reservation. One turn resolved twice and one turn
    /// never resolved at all, out of two turns that both ran.</para>
    ///
    /// <para>It is keyed by role and carries the attempt the close belongs to, so a commit can only
    /// ever write the row its own turn opened.</para>
    /// </summary>
    [Fact]
    public void Two_roles_ending_before_either_commits_each_close_their_own_launch()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(50m, () => now);                      // room for both turns
        var store = new AiAttemptStore(_db);

        var chair = meter.Begin("## Situation", role: CouncilRoles.Operations).Id!;
        var research = meter.Begin("## Situation", role: CouncilRoles.Research).Id!;

        // The chair's process ends first. Its close is metered and held for the chair's commit.
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(4), "", now)
        {
            Usage = new TurnUsage(1_000, 0, 0, 10, 0, null)
        }, CouncilRoles.Operations);

        // The Research Director's ends a moment later — before the chair's transition has run.
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(6), "", now)
        {
            Usage = new TurnUsage(2_000, 0, 0, 20, 0, null)
        }, CouncilRoles.Research);

        // THE CHAIR'S OWN COMMITTED TRANSITION. It closes the chair's launch and nothing else.
        Assert.True(meter.CommitStaged(CouncilRoles.Operations));
        Assert.Equal(AiAttemptState.ENDED, store.Get(chair)!.State);
        Assert.Equal(AiAttemptState.LAUNCHED, store.Get(research)!.State);

        // AND THEN THE RESEARCH DIRECTOR'S, with its own usage on its own row.
        Assert.True(meter.CommitStaged(CouncilRoles.Research));
        Assert.Equal(AiAttemptState.ENDED, store.Get(research)!.State);

        Assert.Equal((1_000m * 1m + 10m * 4m) / 1_000_000m, store.Get(chair)!.Cost);
        Assert.Equal((2_000m * 1m + 20m * 4m) / 1_000_000m, store.Get(research)!.Cost);
    }

    /// <summary>
    /// A CHAT TURN THE CEILING REFUSES STARTS NO PROCESS AND SAYS SO. The marker file is the
    /// evidence: the child touches it before it prints anything, so its absence is the operating
    /// system's word rather than this software's.
    /// </summary>
    [Fact]
    public async Task A_chat_turn_the_ceiling_refuses_starts_no_process_and_says_so_in_the_chat()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(2m, () => now);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Research).Admitted);

        var marker = Path.Combine(TestEnv.Home, $"ran-{Guid.NewGuid():n}.txt");
        var chat = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, marker: marker);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);

        await chat.SendAsync("place a trade for me");

        Assert.False(File.Exists(marker), "the AI tool was started for a turn the ceiling refused");
        Assert.Null(chat.ThreadId);
        Assert.Equal(1, Rows(nameof(AiAttemptState.LAUNCHED)));
        Assert.Equal(0, Rows(nameof(AiAttemptState.ENDED)));

        // Their words are still theirs, and the last thing said is which ceiling stopped it.
        var history = chat.History;
        Assert.Contains(history, t => t is { Role: ChatRole.You, Text: "place a trade for me" });
        var said = history[^1];
        Assert.Equal(ChatRole.System, said.Role);
        Assert.Equal(Labels.DailySpendingLimitReached, said.Text);
    }

    /// <summary>
    /// A TURN THAT ENDED WITHOUT REPORTING WHAT IT USED KEEPS ITS RESERVATION AS ITS COST.
    ///
    /// The non-crash half of rule 3. <c>Reserved</c> sums only LAUNCHED rows, so an ENDED row with a
    /// null cost released the whole commitment and put nothing in its place: the vendor had been
    /// asked to do the work, and the day's total never moved. A turn that fails to start, or is
    /// cancelled, or prints nothing this parser recognises, is exactly that turn.
    /// </summary>
    [Fact]
    public void A_turn_that_ends_without_reporting_its_usage_is_charged_what_it_reserved()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(50m, () => now);

        var id = meter.Begin("## Situation", role: CouncilRoles.Operations).Id!;

        // The process ended and said nothing about what it used — no usage event at all.
        meter.Record(new AgentTurnEnded(1, TimeSpan.FromSeconds(2), "the AI tool could not be reached", now),
            CouncilRoles.Operations);
        meter.CommitStaged();               // the mission loop's own commit, which is where its close lands

        var row = new AiAttemptStore(_db).Get(id)!;
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(Reservation, row.ReservedCost);
        Assert.Equal(Reservation, row.Cost);
        Assert.Equal(AiAttemptStore.UnreportedReason, row.UnpricedReason);

        var spend = meter.Today;
        Assert.Equal(Reservation, spend.Spent);
        Assert.Equal(0m, spend.Reserved);          // resolved, and resolved AT the reservation
        Assert.Equal(1, spend.Turns);
        Assert.Equal(1, spend.UnreportedTurns);    // so the total is not read as a bill
        Assert.Equal(0, spend.UnpricedTurns);
    }

    /// <summary>
    /// AND EVERY SURFACE THAT SHOWS THE DAY SAYS SO. The item is not finished when the ledger is
    /// right: `Spent` now contains money that is a WORST CASE, and a total the owner, the report and
    /// the AI itself all read as a bill is the reading rule 4 forbids. Three surfaces, one fact.
    ///
    /// <para>The report says it twice on purpose: once as a count beside the day's turns, and once
    /// in the unresolved list beside the LAUNCHED and LOST rows — because "reserved and unresolved"
    /// above it sums only the rows still open, and a turn charged its reservation is exactly as
    /// unresolved as those are.</para>
    /// </summary>
    [Fact]
    public async Task An_unreported_turn_is_named_on_the_card_the_report_and_the_line_the_AI_reads()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        var at = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));

        var meter = new TurnMeter(db, () => 50m, runtimeId: () => "codex", now: () => at,
            recordPath: _records, owner: () => Rate, model: () => "gpt-5.6-sol", share: _ => 1m);

        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);
        meter.Record(new AgentTurnEnded(1, TimeSpan.FromSeconds(2), "", at), CouncilRoles.Operations);
        meter.CommitStaged();

        var spend = meter.Today;
        Assert.Equal(1, spend.UnreportedTurns);

        // THE CARD, in the owner's words.
        Assert.Contains("never reported what they used and are charged what they reserved",
            DashboardPage.MissionCost(spend));

        // THE LINE THE AI READS, in the same direction and not folded into the unpriced one.
        var told = MissionSituation.SpendLine(spend)!;
        Assert.Contains("never reported what they used and are charged what they reserved", told);
        Assert.Contains("which is the most they could have cost", told);
        Assert.DoesNotContain("could not be priced", told);

        // THE REPORT, as a count beside the turns and as an unresolved commitment of its own.
        // The snapshot the app hands the report, which is the meter's own reading of the day.
        var report = gw.Reports.Compose(at, new DailyReportInputs { Spending = [spend] });
        var text = DailyReportText.Render(report);
        Assert.Equal(1, report.Spending.UnreportedTurns);
        Assert.Contains("charged at their reservation because no usage was ever reported", text);
        Assert.Contains("its reservation stands as its cost", text);
    }

    /// <summary>
    /// AND AN INSTALLATION THAT CANNOT PRICE A TURN AT ALL IS NOT FILLED WITH ZEROES. A reservation
    /// of nothing is not a charge: the row stays unpriced, which is the reading whose sentence says
    /// the total is a floor.
    /// </summary>
    [Fact]
    public void A_turn_with_nothing_reserved_stays_unpriced_rather_than_being_charged_zero()
    {
        var now = DateTimeOffset.Now;
        // No owner rate and a runtime nothing ships a price for: the reservation is unpriceable.
        var meter = new TurnMeter(_db, () => 50m, runtimeId: () => "nothing-prices-this",
            now: () => now, recordPath: _records, share: _ => 1m);

        var id = meter.Begin("## Situation", role: CouncilRoles.Operations).Id!;
        Assert.Equal(0m, new AiAttemptStore(_db).Get(id)!.ReservedCost);

        meter.Record(new AgentTurnEnded(1, TimeSpan.FromSeconds(2), "", now), CouncilRoles.Operations);
        meter.CommitStaged();               // the mission loop's own commit, which is where its close lands

        var row = new AiAttemptStore(_db).Get(id)!;
        Assert.Null(row.Cost);
        Assert.NotEqual(AiAttemptStore.UnreportedReason, row.UnpricedReason);
        Assert.Equal(1, meter.Today.UnpricedTurns);
        Assert.Equal(0, meter.Today.UnreportedTurns);
    }

    /// <summary>
    /// AND THE LOOP OBEYS IT: a turn the reservation refused is never sent to the AI tool, even when
    /// the host's own reading says there is room. That reading is the stale one this unit exists for.
    /// </summary>
    [Fact]
    public async Task A_turn_the_reservation_refused_is_never_sent_to_the_AI_tool()
    {
        var now = DateTimeOffset.Now;
        var cap = 2m;
        var meter = new TurnMeter(_db, () => cap, runtimeId: () => "codex", now: () => now,
            recordPath: _records, owner: () => Rate, model: () => "gpt-5.6-sol", share: _ => 1m);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);

        var host = new StaleHost(meter);
        host.Conv.Typed.Add("what are you doing?");
        // On the meter's instant: the wait is the meter's midnight minus the LOOP's clock, and two
        // clocks either side of midnight make it negative. See TestEnv.LocalNoon.
        var loop = new MissionLoop(host, now: () => now);

        var wait = await loop.TurnAsync();
        Assert.Empty(host.Conv.Sent);
        Assert.Equal(1, Rows(nameof(AiAttemptState.LAUNCHED)));
        Assert.True(wait > TimeSpan.Zero);

        // AND THEIR QUESTION IS NOT LOST WITH THE TURN. It was taken off the conversation's list to
        // build a Situation nobody ever sent, so the next turn is the one that carries it.
        cap = 50m;                          // the owner raises the limit on the Safety page
        await loop.TurnAsync();
        Assert.Contains("what are you doing?", Assert.Single(host.Conv.Sent));
    }

    /// <summary>
    /// AND THE REFUSAL IS RECORDED AGAINST THE MESSAGE IT COULD NOT ANSWER. The owner's message is
    /// still owed a turn — a refused launch consumes nothing — so the queue has to carry the standing
    /// reason it has not had one, in the same words and by the same helper the loop's own pre-check
    /// uses. Without it the only record of the refusal is a wait the owner cannot account for.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_recorded_against_the_owners_message_it_could_not_answer()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("owner:1", MissionEventKind.Owner, now);

        var meter = Meter(2m, () => now);
        Assert.True(meter.Begin("## Situation", role: CouncilRoles.Operations).Admitted);   // the room goes

        var host = new StaleHost(meter) { Wakes = events };
        Assert.True(await new MissionLoop(host, now: () => now).TurnAsync() > TimeSpan.Zero);   // the meter's instant, as above
        Assert.Empty(host.Conv.Sent);

        var row = events.Get("owner:1")!;
        Assert.Null(row.ConsumedBy);                       // still owed a turn, at midnight or sooner
        Assert.Equal(MissionEventDisposition.Blocked, row.Disposition);
        Assert.Equal(Labels.DailySpendingLimitReached, row.DispositionDetail);
    }

    /// <summary>
    /// THE LEDGER WILL NOT TAKE A ROW, HONESTLY: the INSERT itself throws, inside the transaction that would have
    /// reserved the turn and consumed its wakes, exactly as a full disk or a locked file does. Reads still work,
    /// so what is under test is the write and nothing else. <see cref="LedgerTakesRows"/> lifts it.
    /// </summary>
    void LedgerRefusesRows() => _db.Write(_ =>
    {
        using var c = _db.Cmd("""
            CREATE TRIGGER refuse_ai_attempt BEFORE INSERT ON ai_attempt
            BEGIN SELECT RAISE(ABORT, 'the disk would not take the row'); END
            """);
        return c.ExecuteNonQuery();
    });

    /// <summary>The ledger's sentence, carrying what the ledger said; the driver wraps the trigger's words in its own.</summary>
    static void AssertSaysTheLedger(string? said)
    {
        var sentence = Labels.LaunchLedgerUnwritable("\0");
        Assert.NotNull(said);
        Assert.StartsWith(sentence[..sentence.IndexOf('\0')], said);
        Assert.EndsWith(sentence[(sentence.IndexOf('\0') + 1)..], said);
        Assert.Contains("the disk would not take the row", said);
    }

    void LedgerTakesRows() => _db.Write(_ =>
    {
        using var c = _db.Cmd("DROP TRIGGER refuse_ai_attempt");
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// A LAUNCH WHOSE ADMISSION CANNOT BE WRITTEN IS REFUSED (<c>U-admission-fail-closed</c>). It used to be
    /// "unrecorded" and admitted: the turn ran, its cost went into no row, and the day's cap never saw it. Refused,
    /// it names the ledger — not the day's limit, not a role's share — and leaves its wake owed.
    /// </summary>
    [Fact]
    public void A_launch_whose_admission_cannot_be_written_is_refused_and_consumes_no_wake()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("owner:1", MissionEventKind.Owner, now);
        var meter = Meter(50m, () => now);
        LedgerRefusesRows();

        var admission = meter.Begin("## Situation", ["owner:1"], CouncilRoles.Operations);

        Assert.False(admission.Admitted, "a launch the ledger would not record was admitted");
        Assert.Null(admission.Id);
        Assert.Equal(AiAdmission.Ledger, admission.Ceiling);
        AssertSaysTheLedger(admission.Refusal);
        Assert.Null(events.Get("owner:1")!.ConsumedBy);
        Assert.Equal(0, Rows(nameof(AiAttemptState.LAUNCHED)));
    }

    /// <summary>
    /// AND THE LOOP STARTS NO PROCESS FOR IT: nothing is sent, the wake stays owed, the wait is the loop's refusal
    /// retry and not a busy loop — and once the ledger takes rows again the very next look launches the turn.
    /// </summary>
    [Fact]
    public async Task The_mission_loop_starts_no_process_when_the_ledger_will_not_take_the_row()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("owner:1", MissionEventKind.Owner, now);
        var meter = Meter(50m, () => now);
        var host = new StaleHost(meter) { Wakes = events };
        var options = new MissionOptions();
        var loop = new MissionLoop(host, options, now: () => now);
        LedgerRefusesRows();

        var wait = await loop.TurnAsync();

        Assert.Empty(host.Conv.Sent);
        Assert.Equal(0, Rows(nameof(AiAttemptState.LAUNCHED)));
        Assert.Null(events.Get("owner:1")!.ConsumedBy);
        Assert.True(wait >= options.BusyRetry, $"the loop came back after {wait}, faster than its refusal retry");

        LedgerTakesRows();
        await loop.TurnAsync();

        Assert.Single(host.Conv.Sent);
        Assert.NotNull(events.Get("owner:1")!.ConsumedBy);
        Assert.NotEqual(MissionLoop.Unrecorded, events.Get("owner:1")!.ConsumedBy);
    }

    /// <summary>
    /// A LEDGER REFUSAL REACHES THE OWNER WHATEVER WOKE THE LOOP (<c>U-admission-fail-closed</c>, fix pass 1, review P1).
    /// It used to be written only onto due Owner messages, so a turn a fill, a review or the inbox asked for was refused in
    /// silence: the owner saw an unexplained wait. Now the host is told, with the ledger's own words and the cause, and the
    /// app writes that into the activity log; the wake stays owed and the loop backs off as from any failed turn.
    /// </summary>
    [Fact]
    public async Task A_ledger_refusal_reaches_the_owner_on_a_wake_that_is_not_theirs()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("fill:1", MissionEventKind.Fill, now);
        var meter = Meter(50m, () => now);
        var host = new StaleHost(meter) { Wakes = events };
        var options = new MissionOptions();
        var loop = new MissionLoop(host, options, now: () => now);
        LedgerRefusesRows();

        var wait = await loop.TurnAsync();

        Assert.Empty(host.Conv.Sent);
        Assert.Null(events.Get("fill:1")!.ConsumedBy);
        var told = Assert.Single(host.LedgerTold);
        AssertSaysTheLedger(told.Refusal);
        Assert.Equal(options.FirstBackoff, wait);
    }

    /// <summary>
    /// AND A LEDGER THAT STAYS BROKEN WRITES ONE LINE, NOT ONE PER LOOK. Look after look is refused for the same cause:
    /// the owner is told once, the card says what the loop is waiting for and why, and each wait is longer than the one
    /// before — the failure backoff, not the five-second busy retry. Once the ledger takes rows again the next look
    /// launches the turn the wake was owed, and the run of failures is over.
    /// </summary>
    [Fact]
    public async Task A_ledger_that_stays_broken_is_told_once_and_backs_off_until_it_takes_rows_again()
    {
        var now = DateTimeOffset.Now;
        var events = new MissionEventStore(_db);
        events.Raise("review:1", MissionEventKind.Review, now);
        var meter = Meter(50m, () => now);
        var host = new StaleHost(meter) { Wakes = events };
        var options = new MissionOptions();
        var waits = new List<TimeSpan>();
        var loop = new MissionLoop(host, options, now: () => now, delay: (d, ct) =>
        {
            lock (waits) waits.Add(d);
            return Task.Delay(1, ct);
        });
        LedgerRefusesRows();

        loop.Start();
        string? card = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            card ??= loop.Status.WaitingFor;
            lock (waits) if (waits.Count >= 4 && card is not null) break;
            await Task.Delay(1);
        }
        TimeSpan[] refused;
        lock (waits) refused = [.. waits.Take(4)];

        LedgerTakesRows();
        deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while ((host.Conv.Sent.Count == 0 || loop.Status.State == MissionState.Working)
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(1);
        var after = loop.Status;
        await loop.PauseAsync();

        Assert.Equal(4, refused.Length);
        Assert.Equal(options.FirstBackoff, refused[0]);
        for (var i = 1; i < refused.Length; i++)
            Assert.True(refused[i] > refused[i - 1], $"look {i + 1} waited {refused[i]} after {refused[i - 1]}");
        Assert.NotNull(card);
        Assert.Contains("launch ledger", card);
        Assert.Contains("the disk would not take the row", card);
        AssertSaysTheLedger(Assert.Single(host.LedgerTold).Refusal);

        Assert.Single(host.Conv.Sent);
        Assert.NotNull(events.Get("review:1")!.ConsumedBy);
        Assert.Equal(0, after.ConsecutiveErrors);
    }

    /// <summary>
    /// THE APP WRITES THE LEDGER'S REFUSAL WHERE THE OWNER LOOKS for what the software did: one activity line, naming the
    /// role and carrying the ledger's sentence with its cause.
    /// </summary>
    [Fact]
    public async Task The_apps_mission_host_writes_a_ledger_refusal_into_the_activity_log()
    {
        using var db = TestEnv.NewDb();
        var app = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            IMissionHost mission = new AppHost.MissionHost(app);
            mission.LaunchLedgerRefused(CouncilRoles.Research,
                AiAdmission.LedgerRefused("the disk would not take the row").Refusal!);

            var line = Assert.Single(app.Gateway.Log.RecentActivity(), r => r.Text.Contains("launch ledger"));
            Assert.Contains(CouncilRoles.Title(CouncilRoles.Research), line.Text);
            Assert.Contains("the disk would not take the row", line.Text);
        }
        finally { await app.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE OWNER'S TYPED TURN IS HELD WHEN ITS ADMISSION CANNOT BE WRITTEN, exactly as a capped one is: no process,
    /// their words kept for the next turn, and the last line says it was the ledger — not the day's limit.
    /// </summary>
    [Fact]
    public async Task The_owners_typed_turn_is_held_when_its_admission_cannot_be_written()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(50m, () => now);
        var marker = Path.Combine(TestEnv.Home, $"ran-{Guid.NewGuid():n}.txt");
        var chat = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, marker: marker);
        using var metering = meter.Attach(chat, CouncilRoles.Operations);
        LedgerRefusesRows();

        await chat.SendAsync("what are you doing?");

        Assert.False(File.Exists(marker), "the AI tool was started for a turn the ledger never recorded");
        Assert.Null(chat.ThreadId);
        Assert.Equal(0, Rows(nameof(AiAttemptState.ENDED)));
        Assert.Contains("what are you doing?", chat.TakeTyped());
        var said = chat.History[^1];
        Assert.Equal(ChatRole.System, said.Role);
        AssertSaysTheLedger(said.Text);
    }

    /// <summary>
    /// A CONVERSATION NO METER IS ATTACHED TO REFUSES THE OWNER'S TURN. In the shipped app that is a conversation whose
    /// runtime was replaced — the meter detaches and clears its admission — still bound to the Chat page; a null
    /// admission used to be "unrecorded" and admitted, so the typed turn ran with no row and no ceiling.
    /// </summary>
    [Fact]
    public async Task A_conversation_no_meter_is_attached_to_refuses_the_owners_turn()
    {
        var now = DateTimeOffset.Now;
        var meter = Meter(50m, () => now);
        var marker = Path.Combine(TestEnv.Home, $"ran-{Guid.NewGuid():n}.txt");
        var chat = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, marker: marker);
        meter.Attach(chat, CouncilRoles.Operations).Dispose();   // the meter detaches, as on a runtime change

        await chat.SendAsync("what are you doing?");

        Assert.False(File.Exists(marker), "the AI tool was started for a turn no meter reserved");
        Assert.Contains("what are you doing?", chat.TakeTyped());
        var said = chat.History[^1];
        Assert.Equal(ChatRole.System, said.Role);
        Assert.Equal(AiAdmission.NoLedgerOpen.Refusal, said.Text);
        Assert.Equal(0, Rows(nameof(AiAttemptState.LAUNCHED)) + Rows(nameof(AiAttemptState.ENDED)));
    }

    /// <summary>
    /// THE APP'S MISSION HOST WITH NO METER REFUSES THE LAUNCH. A freshly built app has no meter until its start
    /// composes one; a launch asked of it then is one no ledger could reserve, and it is refused on the ledger.
    /// </summary>
    [Fact]
    public void The_apps_mission_host_with_no_meter_refuses_the_launch()
    {
        var mission = new AppHost.MissionHost(new AppHost());

        foreach (var admission in new[]
                 {
                     mission.BeginTurn("## Situation", []),
                     mission.BeginTurn("## Situation", [], CouncilRoles.Research)
                 })
        {
            Assert.False(admission.Admitted, "a launch with no meter to reserve it was admitted");
            Assert.Equal(AiAdmission.Ledger, admission.Ceiling);
            Assert.Equal(AiAdmission.NoLedgerOpen.Refusal, admission.Refusal);
        }
    }

    /// <summary>
    /// THE GUARD: A HOST BUILT WITHOUT A LEDGER STILL RUNS ITS TURNS, through the explicit value and nothing else —
    /// the loop's no-ledger default sends the turn, and a conversation that says it has no ledger starts its process.
    /// </summary>
    [Fact]
    public async Task A_host_built_without_a_ledger_still_runs_its_turns_through_the_explicit_value()
    {
        var host = new NoLedgerHost();
        await new MissionLoop(host).TurnAsync();
        Assert.Single(host.Conv.Sent);

        var marker = Path.Combine(TestEnv.Home, $"ran-{Guid.NewGuid():n}.txt");
        var chat = AgentRuntimeProbe.SessionOverStream(TurnMeterTests.CodexStream, marker: marker);
        Assert.Equal(AiAdmission.NoLedger, chat.Admit!("say hi"));
        await chat.SendAsync("say hi");
        Assert.True(File.Exists(marker), "a conversation built without a ledger did not run its turn");
    }

    /// <summary>
    /// THE RESERVATION CHARGES INPUT AT THE DEARER OF THE TWO RATES THE SAME TOKENS CAN BE BILLED AT.
    ///
    /// <c>gpt-5.6-sol</c>, on this build's own shipped list, costs 4.00 per million input and 5.00
    /// per million for CACHE WRITES. Reserved at the plain input rate, a turn that wrote its whole
    /// input to cache would be billed above its own reservation, and the ceiling would be walked
    /// past by exactly that difference — which is a bound that is not one.
    /// </summary>
    [Fact]
    public void The_reservation_charges_input_at_the_dearer_of_the_plain_and_cache_write_rates()
    {
        var reserved = CostCatalog.Reserve(TurnAllowance.Default, "codex", requestedModel: "gpt-5.6-sol");

        Assert.Equal((1_200_000m * 5.00m + 20_000m * 20.00m) / 1_000_000m, reserved.Cost);
        Assert.Null(reserved.Unpriced);

        // AND IT REALLY IS THE WORST CASE: a turn whose whole input was a cache write costs exactly
        // the reservation, so nothing this catalogue can bill exceeds what was set aside.
        var worst = CostCatalog.Price(new TurnUsage(0, 0, 1_200_000, 20_000, 0, null), "codex",
            requestedModel: "gpt-5.6-sol");
        Assert.Equal(reserved.Cost, worst.Cost);

        // A bound, not a bill: the ordinary turn is cheaper than what was committed for it.
        var ordinary = CostCatalog.Price(new TurnUsage(1_200_000, 0, 0, 20_000, 0, null), "codex",
            requestedModel: "gpt-5.6-sol");
        Assert.True(ordinary.Cost < reserved.Cost);
    }

    /// <summary>And that is the number the ledger commits, not one the price list was asked for separately.</summary>
    [Fact]
    public void The_row_commits_what_the_formula_says_a_turn_may_cost()
    {
        var now = DateTimeOffset.Now;
        var meter = new TurnMeter(_db, () => 50m, runtimeId: () => "codex", now: () => now,
            recordPath: _records, model: () => "gpt-5.6-sol", share: _ => 1m);

        const decimal bound = (1_200_000m * 5.00m + 20_000m * 20.00m) / 1_000_000m;   // 6.40
        Assert.Equal(bound, meter.Today.NextTurnReservation);

        var id = meter.Begin("## Situation", role: CouncilRoles.Operations).Id!;
        Assert.Equal(bound, new AiAttemptStore(_db).Get(id)!.ReservedCost);
    }

    /// <summary>
    /// The two boxes that set the allowance save in ONE press — they take no permission and give
    /// none — and a zero in either reads as the shipped default rather than as an allowance of
    /// nothing, which would be a reservation of nothing and a ceiling holding nothing back.
    /// </summary>
    [Fact]
    public void The_turn_allowance_saves_in_one_press_and_a_zero_box_reads_as_the_default()
    {
        var saved = 0;
        var b = SafetyPage.BuildSaveTurnAllowance(() => saved++);
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, saved);
        Assert.Equal(Labels.SaveTurnAllowance, b.Content);

        Assert.Equal(TurnAllowance.Default,
            SafetyPage.PendingAllowance(0m, 0m, TurnAllowance.Default));
        Assert.Equal(new TurnAllowance(400_000, 8_000),
            SafetyPage.PendingAllowance(400_000m, 8_000m, TurnAllowance.Default));
        // An empty box is "leave it as it is", not "nothing".
        Assert.Equal(new TurnAllowance(400_000, TurnAllowance.Default.OutputTokens),
            SafetyPage.PendingAllowance(400_000m, null, TurnAllowance.Default));
    }

    /// <summary>
    /// A host whose own reading of the day ALWAYS admits — an unmetered one, which is the permissive
    /// default — so the only thing that can stop the turn below is the reservation's transaction.
    /// </summary>
    sealed class StaleHost(TurnMeter meter) : IMissionHost
    {
        public Recording Conv { get; } = new();

        public IAgentConversation? Conversation => Conv;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"reserve-home-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });

        public AiAdmission BeginTurn(string prompt, IReadOnlyList<string> wakes) =>
            meter.Begin(prompt, wakes, CouncilRoles.Operations);

        /// <summary>The wake queue, where a test needs one. Null is the host with no queue at all.</summary>
        public MissionEventStore? Wakes { get; init; }

        public MissionEventStore? Events => Wakes;

        /// <summary>What the loop told the owner about a launch ledger that refused, as the app's activity log gets it.</summary>
        public List<(string Role, string Refusal)> LedgerTold { get; } = [];

        public void LaunchLedgerRefused(string role, string refusal)
        {
            lock (LedgerTold) LedgerTold.Add((role, refusal));
        }
    }

    /// <summary>A host built without a launch ledger: it keeps the interface's own no-ledger admission.</summary>
    sealed class NoLedgerHost : IMissionHost
    {
        public Recording Conv { get; } = new();

        public IAgentConversation? Conversation => Conv;
        public string AgentHome { get; } = Path.Combine(TestEnv.Home, $"no-ledger-home-{Guid.NewGuid():n}");
        public bool InboxChangedSinceLastPass => false;
        public Task ScanAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<MissionSituation> SituationAsync(CancellationToken ct) =>
            Task.FromResult(new MissionSituation { LocalTime = DateTimeOffset.Now, Mode = "PAPER" });
    }

    /// <summary>A conversation that records what it was sent, and answers every turn the same way.</summary>
    sealed class Recording : IAgentConversation
    {
        readonly List<ChatTurn> _history = [];
        public List<string> Sent { get; } = [];

        public bool Busy => false;
        public IReadOnlyList<ChatTurn> History => _history.ToArray();

        public event Action<ChatTurn>? TurnAdded;
        public event Action<string>? Delta;
        public event Action? StateChanged;
        public event Action<AgentTurnEnded>? TurnEnded;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string message, CancellationToken ct = default) => Run(message);
        public Task SendMissionAsync(string message, CancellationToken ct = default) => Run(message);

        Task Run(string message)
        {
            Sent.Add(message);
            StateChanged?.Invoke();
            Delta?.Invoke("");
            var turn = new ChatTurn(ChatRole.Ai, "Did some work.", DateTimeOffset.UtcNow);
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
            TurnEnded?.Invoke(new AgentTurnEnded(0, TimeSpan.FromSeconds(1), "", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        /// <summary>What the owner typed while the AI was working, as the real session keeps it.</summary>
        public List<string> Typed { get; } = [];

        public IReadOnlyList<string> TakeTyped()
        {
            var taken = Typed.ToArray();
            Typed.Clear();
            return taken;
        }

        public void Queue(string message) => Typed.Add(message);
        public Task CancelAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }
}
