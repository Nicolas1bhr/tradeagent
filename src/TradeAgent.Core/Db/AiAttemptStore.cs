using Microsoft.Data.Sqlite;

namespace TradeAgent.Core.Db;

/// <summary>Where one launch of the agent CLI has got to. See <see cref="AiAttemptStore"/>.</summary>
public enum AiAttemptState
{
    /// <summary>The process was started and nothing has reported how it ended. Its reservation stands.</summary>
    LAUNCHED,

    /// <summary>It ended and the app saw it end. <c>Cost</c> is what the usage came to, or null.</summary>
    ENDED,

    /// <summary>
    /// A meter opened this database while the row was still LAUNCHED, so the process that owned it
    /// is gone and nobody will ever report its usage. The reservation becomes the cost.
    /// </summary>
    LOST
}

/// <summary>
/// ONE LAUNCH OF THE AGENT CLI, as the app recorded it before and after.
///
/// <see cref="ReservedCost"/> is committed BEFORE the process starts and <see cref="Cost"/> is what
/// it turned out to be. They are two different numbers on purpose and neither replaces the other: a
/// row whose cost is still null is an allowance that has been spent as far as the ceiling is
/// concerned, because the vendor has already been asked to do the work.
/// </summary>
public sealed record AiAttempt
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The runtime id the turn ran on, e.g. <c>codex</c>. Null when nothing named one.</summary>
    public string? Runtime { get; init; }

    /// <summary>The model TradeAgent PUT ON THE COMMAND LINE, or null where it named none.</summary>
    public string? RequestedModel { get; init; }

    /// <summary>
    /// Where the rate came from: <c>owner</c> for the two numbers on the Safety page, or the day a
    /// list price was read and the page it was read from. A figure whose basis is not recorded is a
    /// figure nobody can check later, and these go stale on the vendor's schedule.
    /// </summary>
    public string? PricingBasis { get; init; }

    public decimal ReservedCost { get; init; }
    public AiAttemptState State { get; init; } = AiAttemptState.LAUNCHED;

    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }

    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }

    /// <summary>The model the RUNTIME'S OWN STREAM named, or null when it named none.</summary>
    public string? EffectiveModel { get; init; }

    public decimal? Cost { get; init; }

    /// <summary>Why <see cref="Cost"/> is absent. Present exactly when it is.</summary>
    public string? UnpricedReason { get; init; }

    /// <summary>What the app could observe about the turn's context, as JSON. See <c>TurnContext</c>.</summary>
    public string? Context { get; init; }

    /// <summary>The grant-policy revision in force at launch. See <see cref="Versions.GrantPolicyVersion"/>.</summary>
    public string? PolicyVersion { get; init; }

    /// <summary>SHA-256, lower-case hex, of the prompt the app wrote. Never the prompt itself.</summary>
    public string? InputHash { get; init; }

    /// <summary>
    /// WHICH COUNCIL ROLE WAS CHARGED FOR THIS LAUNCH. Null on every row written before the council
    /// existed, and read as the chair's — the single agent it replaces was Operations, and a launch
    /// attributed to nobody is a bill nobody can allocate.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    /// WHAT THIS ROW COSTS THE DAY IT STARTED ON. A row still LAUNCHED costs its reservation,
    /// because the work has been asked for; a LOST one costs it for good.
    /// </summary>
    public decimal? Charge => State == AiAttemptState.LAUNCHED ? ReservedCost : Cost;
}

/// <summary>
/// Today's totals for the AI's spending, and the daily ceiling's ruler.
///
/// <paramref name="Reserved"/> is separate from <paramref name="Spent"/> so the loop can add the
/// turn it is about to take: the admission rule is <c>spent + reserved + this turn ≤ cap</c>, which
/// is a cap, where <c>spent ≥ cap</c> alone is only a check on money already gone.
/// </summary>
/// <param name="Unreported">
/// Resolved turns charged their RESERVATION because nothing ever said what they used — a turn the
/// app saw end with no usage event, and a turn a restart declared LOST. Their money is in
/// <paramref name="Spent"/>, and this is what stops that total reading as a bill.
/// </param>
public sealed record AiAttemptTotals(
    decimal Spent, decimal Reserved, int Turns, int Unpriced, int Estimated, int Open,
    int Unreported = 0);

/// <summary>
/// THE CEILING A LAUNCH IS ADMITTED AGAINST, handed to <see cref="AiAttemptStore.Begin"/> so that
/// the comparison is made INSIDE the transaction that writes the reservation.
///
/// It carries everything the arithmetic needs EXCEPT the day's totals, and that omission is the
/// whole design: the totals are the one part that another launch can change between a caller's look
/// and this launch's commit, so they are read where nothing can move them. A rule that carried a
/// reading would be the defect this unit closes, wearing a different name.
///
/// <see cref="Metered"/> false is a host with no price list at all: every launch is admitted, which
/// is what <see cref="AiSpendToday.AdmitsGlobally"/> has always answered for an unmetered build.
/// </summary>
public sealed record AiAdmissionRule
{
    /// <summary>The owner's local day, as the half-open window a row's <c>started_at</c> is in.</summary>
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    /// <summary>The role being charged, or null for a launch attributed to nobody.</summary>
    public string? Role { get; init; }

    /// <summary>The owner's ceiling for the whole day, across every role.</summary>
    public decimal Cap { get; init; }

    /// <summary>That role's slice of it. Meaningless, and unread, when <see cref="Role"/> is null.</summary>
    public decimal RoleCap { get; init; }

    /// <summary>What THIS launch commits — the number about to be written as its reservation.</summary>
    public decimal Reservation { get; init; }

    /// <summary>The local midnight the day's totals expire at, for the refusal's own sentence.</summary>
    public DateTimeOffset ResumesAt { get; init; }

    public bool Metered { get; init; } = true;

    /// <summary>
    /// The reading the admission is decided on: the day's own rows, the role's own rows, and this
    /// launch's reservation. Built here rather than in the store so that ONE record answers the
    /// question — <see cref="AiSpendToday.AdmitsAnotherTurn"/> — for the loop's cheap first look and
    /// for the transaction alike. Two comparisons that could drift apart would be two caps.
    /// </summary>
    internal AiSpendToday Reading(AiAttemptTotals day, AiAttemptTotals mine) => new()
    {
        Metered = Metered,
        Role = Role,
        Spent = day.Spent,
        Reserved = day.Reserved,
        RoleSpent = mine.Spent,
        RoleReserved = mine.Reserved,
        NextTurnReservation = Reservation,
        Cap = Cap,
        RoleCap = RoleCap,
        ResumesAt = ResumesAt
    };
}

/// <summary>
/// WHETHER A LAUNCH MAY HAPPEN, AND WHAT IT COMMITTED — decided and written in one transaction.
///
/// <see cref="Admitted"/> is keyed on the REFUSAL and not on the id, because the two absences mean
/// opposite things. A refusal is "do not start the process": some ceiling has no room. A null id
/// with no refusal is "nothing could be recorded" — a host that keeps no ledger, or a database that
/// would not take the row — and the turn still runs, because a launch the app failed to write down
/// is not a launch the app may silently decline to make.
/// </summary>
public sealed record AiAdmission
{
    /// <summary>Nothing was written and nothing refused it. The caller carries on.</summary>
    public static readonly AiAdmission Unrecorded = new();

    /// <summary>The attempt id to complete at the end of the turn, or null when none was written.</summary>
    public string? Id { get; init; }

    public bool Admitted => Refusal is null;

    /// <summary>Why the launch was refused, in the owner's words. Null when it was admitted.</summary>
    public string? Refusal { get; init; }

    /// <summary>
    /// WHICH CEILING REFUSED IT: <see cref="Day"/> for the owner's whole-day limit, or the role id
    /// whose share ran out. The two have different repairs — the day's limit is raised on the Safety
    /// page and a role's share is a reallocation — so a caller that said one when it meant the other
    /// would send the owner to the wrong screen.
    /// </summary>
    public string? Ceiling { get; init; }

    /// <summary>What <see cref="Ceiling"/> says for the owner's whole-day limit.</summary>
    public const string Day = "day";

    /// <summary>By how much this launch would have passed that ceiling. Never negative.</summary>
    public decimal Over { get; init; }

    /// <summary>The local midnight the refusing day's totals expire at.</summary>
    public DateTimeOffset ResumesAt { get; init; }
}

/// <summary>
/// THE LAUNCH LEDGER: one row per run of the agent CLI, written before the process starts.
///
/// <para><b>Why it exists.</b> The meter it replaces wrote its totals when a turn FINISHED, so a
/// turn killed mid-flight cost nothing at all — the vendor had done the work and billed for it, and
/// the app's day total never moved. An agent that is killed and restarted got its allowance back
/// every time, and across midnight it got a fresh one; the ceiling that is the only bound on an AI
/// working non-stop was therefore a report on completed spending. Measured on 2026-09-07 the loop
/// spent 5.07 USD against a 5 USD cap.</para>
///
/// <para><b>The mechanism is <see cref="AiAttemptState"/> and nothing else.</b> The row goes in as
/// LAUNCHED with a reservation; it becomes ENDED when the app sees the usage; and it becomes LOST —
/// keeping the reservation as its cost — the next time a meter opens this database with it still
/// open, because at that moment the process that owned it is provably gone and its usage will never
/// arrive. Nothing here releases a reservation.</para>
///
/// <para><b>Written by the app only.</b> There is no verb, no pipe op and no path from the agent to
/// this table, which is the same rule <c>material</c> keeps: a bill the billed party can edit is not
/// a bill. <c>state/agent-turns.jsonl</c> remains beside it as the per-turn diagnostic mirror.</para>
/// </summary>
public sealed class AiAttemptStore(Database db)
{
    const string Cols = """
        id, started_at, runtime, requested_model, pricing_basis, reserved_cost, state, ended_at,
        exit_code, input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens,
        reasoning_output_tokens, effective_model, cost, unpriced_reason, context, policy_version,
        input_hash, role
        """;

    /// <summary>
    /// ADMITS THE LAUNCH AND WRITES ITS RESERVATION, OR WRITES NOTHING — one transaction, one answer.
    ///
    /// <para><b>Why the ceiling is compared here.</b> The caller's own look at the day is a cheap
    /// first filter and cannot be the gate: it reads the totals and the row goes in some lines
    /// later, so two launches can both read "there is room for one" and both then take it. That is
    /// not a hypothetical — it is what <c>docs/COUNCIL.md</c> rule 3 requires reserved rather than
    /// checked, and the ledger's own arithmetic cannot fix a comparison made against a number that
    /// was already stale when it was read. Inside <see cref="Database.Write"/> the totals below are
    /// read under the same lock and the same transaction as the INSERT, so the second of two racing
    /// launches sees the first one's reservation and is refused.</para>
    ///
    /// <paramref name="admit"/> null is an ungated write: the owner's own recorded turn before any
    /// ceiling existed, and a test that is asserting about rows rather than about admission.
    ///
    /// <paramref name="consuming"/> is the wake queue's part of the same commit: the
    /// <c>mission_event</c> rows this launch is answering are marked <c>consumed_by</c> this attempt
    /// IN THIS TRANSACTION. Two transactions would leave a window in which the launch is recorded
    /// and the events are not — a kill there hands the same wake to the next launch and the owner
    /// pays for both — and a window the other way round in which events are spent on a launch that
    /// never happened. One commit has neither. A REFUSED launch consumes nothing, because it never
    /// happened and the reason to take it is still owed a turn.
    /// </summary>
    public AiAdmission Begin(AiAttempt a, IReadOnlyList<string>? consuming = null,
        AiAdmissionRule? admit = null) => db.Write(_ =>
    {
        // READ HERE, NOT BY THE CALLER. This is the whole of the guard: same transaction, same lock,
        // same connection as the INSERT below.
        if (admit is { } rule)
        {
            var day = Totals(rule.From, rule.To, null);
            var mine = rule.Role is null ? day : Totals(rule.From, rule.To, rule.Role);
            var reading = rule.Reading(day, mine);
            if (!reading.AdmitsAnotherTurn) return Refuse(rule, reading);
        }

        using var c = db.Cmd($"""
            INSERT INTO ai_attempt({Cols})
            VALUES($id,$started,$rt,$req,$basis,$res,$state,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                   NULL,NULL,NULL,$ctx,$policy,$hash,$role)
            """,
            ("$id", a.Id), ("$started", Sql.T(a.StartedAt)), ("$rt", a.Runtime),
            ("$req", a.RequestedModel), ("$basis", a.PricingBasis), ("$res", Sql.D(a.ReservedCost)),
            ("$state", nameof(AiAttemptState.LAUNCHED)), ("$ctx", a.Context),
            ("$policy", a.PolicyVersion), ("$hash", a.InputHash), ("$role", a.Role));
        c.ExecuteNonQuery();
        if (consuming is { Count: > 0 }) MissionEventStore.MarkConsumed(db, consuming, a.Id, a.StartedAt);
        return new AiAdmission { Id = a.Id, ResumesAt = admit?.ResumesAt ?? default };
    });

    /// <summary>
    /// The refusal, in the owner's words, naming the ceiling that had no room and by how much. It
    /// carries no money formatting: this layer does not know the currency, and a sentence that
    /// guessed one would put a wrong symbol on the owner's screen.
    /// </summary>
    static AiAdmission Refuse(AiAdmissionRule rule, AiSpendToday reading)
    {
        var global = !reading.AdmitsGlobally;
        var over = global
            ? reading.Spent + reading.Reserved + reading.NextTurnReservation - reading.Cap
            : reading.RoleSpent + reading.RoleReserved + reading.NextTurnReservation - reading.RoleCap;

        return new AiAdmission
        {
            Refusal = global
                ? Labels.DailySpendingLimitReached
                : Labels.RoleShareReached(CouncilRoles.Title(CouncilRoles.Or(rule.Role))),
            Ceiling = global ? AiAdmission.Day : rule.Role,
            Over = over > 0m ? over : 0m,
            ResumesAt = rule.ResumesAt
        };
    }

    /// <summary>
    /// Completes one attempt. ONLY a row still LAUNCHED is written: if a restart already declared it
    /// LOST, that verdict stands and the reservation stays charged, because a process that came back
    /// to claim a cheaper number for a turn nobody was watching is exactly what this table refuses.
    /// Returns false when nothing was updated.
    /// </summary>
    public bool End(string id, int exitCode, DateTimeOffset endedAt, long? input, long? cached,
        long? cacheWrite, long? output, long? reasoning, string? effectiveModel, decimal? cost,
        string? unpricedReason, string? context, string? pricingBasis = null) => db.Write(_ =>
    {
        // A TURN THAT ENDED WITHOUT SAYING WHAT IT USED KEEPS ITS RESERVATION AS ITS COST.
        //
        // This is the non-crash half of the same rule LoseOpen keeps. A turn whose process exited
        // with no usage event — it failed to start, it was cancelled, the runtime printed nothing
        // this parser recognised — became ENDED with a null cost, and `Reserved` sums only LAUNCHED
        // rows, so the whole commitment was released and NOTHING replaced it. The vendor had still
        // been asked to do the work. Kill a turn early enough and the allowance came back, which is
        // exactly what docs/COUNCIL.md rule 3 forbids on every path, not only on the crash path.
        //
        // A reservation of zero is not a charge, and such a row stays unpriced: an installation that
        // cannot price a turn at all must not have its ceiling filled with zeroes that look measured.
        using var c = db.Cmd("""
            UPDATE ai_attempt SET
              state='ENDED', ended_at=$end, exit_code=$exit,
              input_tokens=$in, cached_input_tokens=$cached, cache_write_input_tokens=$write,
              output_tokens=$out, reasoning_output_tokens=$reason,
              effective_model=$model,
              cost=COALESCE($cost, CASE WHEN $in IS NULL AND $out IS NULL
                                             AND CAST(reserved_cost AS REAL) > 0
                                        THEN reserved_cost END),
              unpriced_reason=CASE WHEN $cost IS NULL AND $in IS NULL AND $out IS NULL
                                        AND CAST(reserved_cost AS REAL) > 0
                                   THEN $unreported ELSE $why END,
              context=COALESCE($ctx, context),
              pricing_basis=COALESCE($basis, pricing_basis)
            WHERE id=$id AND state='LAUNCHED'
            """,
            ("$end", Sql.T(endedAt)), ("$exit", exitCode), ("$in", input), ("$cached", cached),
            ("$write", cacheWrite), ("$out", output), ("$reason", reasoning),
            ("$model", effectiveModel), ("$cost", cost is null ? null : Sql.D(cost.Value)),
            ("$why", unpricedReason), ("$unreported", UnreportedReason),
            ("$ctx", context), ("$basis", pricingBasis), ("$id", id));
        return c.ExecuteNonQuery() == 1;
    });

    /// <summary>
    /// What <c>unpriced_reason</c> says on a turn charged its reservation because its usage never
    /// arrived. A constant because three surfaces read the row and one of them is the owner's daily
    /// report, where "its reservation stands as its cost" is the whole of what they need to know.
    /// </summary>
    public const string UnreportedReason =
        "the turn ended without reporting what it used; the reservation stands as its cost";

    /// <summary>
    /// What <c>pricing_basis</c> says when the owner's own two numbers priced the turn. A row with
    /// this basis is never counted as an estimate, however little the runtime said about itself:
    /// the rate is what the person paying the bill says they are charged, not a guess over a
    /// catalogue. It is a constant here because the query below is what reads it.
    /// </summary>
    public const string OwnerBasis = "owner";

    /// <summary>
    /// EVERY ATTEMPT STILL LAUNCHED BECOMES LOST, KEEPING ITS RESERVATION AS ITS COST.
    ///
    /// Called when a meter opens the database. At that moment any row still LAUNCHED belongs to a
    /// process that is gone — the app is starting, or a second meter is being built over the same
    /// file — so its usage will never be reported by anyone. Releasing the reservation there is the
    /// defect this whole table exists for: kill the turn before its usage arrives and the allowance
    /// comes back. It does not come back. Returns how many were lost.
    /// </summary>
    public int LoseOpen(DateTimeOffset at) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            UPDATE ai_attempt
               SET state='LOST', ended_at=$at, cost=reserved_cost,
                   unpriced_reason=COALESCE(unpriced_reason, $why)
             WHERE state='LAUNCHED'
            """,
            ("$at", Sql.T(at)),
            ("$why", "the turn was launched and never reported its usage; the reservation stands"));
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// The day's totals, over attempts that STARTED inside the local day <paramref name="from"/> to
    /// <paramref name="to"/>. Local because the owner's midnight is the one on their wall.
    ///
    /// A turn is counted once it is resolved, so a launch in flight is in <c>Reserved</c> and not in
    /// <c>Turns</c>. "Estimated" is not a column: a row priced while the runtime named no model IS
    /// the estimate, and deriving it here keeps one fact in one place — except where the rate was
    /// the owner's own (<see cref="OwnerBasis"/>), which is a figure they are responsible for rather
    /// than one this build inferred.
    /// </summary>
    public AiAttemptTotals TotalsBetween(DateTimeOffset from, DateTimeOffset to, string? role = null) =>
        db.Read(_ => Totals(from, to, role));

    /// <summary>
    /// The same query with NO lock of its own, so <see cref="Begin"/> can run it inside the
    /// transaction it is about to insert into. Every caller is already holding the database's gate —
    /// either through <see cref="Database.Read"/> above or through <see cref="Database.Write"/> —
    /// and that is the point: an admission decided outside that gate is decided on a number another
    /// launch is free to change before the row lands.
    /// </summary>
    AiAttemptTotals Totals(DateTimeOffset from, DateTimeOffset to, string? role)
    {
        using var c = db.Cmd("""
            SELECT
              COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN 0 ELSE CAST(cost AS REAL) END), 0),
              COALESCE(SUM(CASE WHEN state='LAUNCHED' THEN CAST(reserved_cost AS REAL) ELSE 0 END), 0),
              SUM(CASE WHEN state='LAUNCHED' THEN 0 ELSE 1 END),
              SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NULL THEN 1 ELSE 0 END),
              SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL AND effective_model IS NULL
                            AND COALESCE(pricing_basis,'') <> 'owner' THEN 1 ELSE 0 END),
              SUM(CASE WHEN state='LAUNCHED' THEN 1 ELSE 0 END),
              -- Charged a RESERVATION rather than a bill: a cost with a reason it could not be
              -- priced is exactly that, and it is what keeps `Spent` from reading as an invoice.
              SUM(CASE WHEN state<>'LAUNCHED' AND cost IS NOT NULL AND unpriced_reason IS NOT NULL
                       THEN 1 ELSE 0 END)
            FROM ai_attempt WHERE started_at >= $from AND started_at < $to
              AND ($role IS NULL OR COALESCE(role,$chair) = $role)
            """, ("$from", Sql.T(from)), ("$to", Sql.T(to)), ("$role", role),
            ("$chair", CouncilRoles.Default));
        using var r = c.ExecuteReader();
        if (!r.Read()) return new AiAttemptTotals(0m, 0m, 0, 0, 0, 0);

        // Summed as REAL and returned as decimal. SQLite has no decimal, and these are text columns
        // precisely so a written value comes back as it went in; a total of a few dozen four-decimal
        // figures is exact to far more places than a currency needs, and the per-row numbers the
        // owner reads are never routed through this.
        return new AiAttemptTotals(
            (decimal)r.GetDouble(0), (decimal)r.GetDouble(1),
            Int(r, 2), Int(r, 3), Int(r, 4), Int(r, 5), Int(r, 6));
    }

    static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));

    /// <summary>One attempt by id, or null. For the screens and for a test that has to read a row back.</summary>
    public AiAttempt? Get(string id) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM ai_attempt WHERE id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    /// <summary>Attempts that started in the window, oldest first.</summary>
    public List<AiAttempt> Between(DateTimeOffset from, DateTimeOffset to) => db.Read(_ =>
    {
        using var c = db.Cmd(
            $"SELECT {Cols} FROM ai_attempt WHERE started_at >= $from AND started_at < $to ORDER BY started_at, rowid",
            ("$from", Sql.T(from)), ("$to", Sql.T(to)));
        using var r = c.ExecuteReader();
        var list = new List<AiAttempt>();
        while (r.Read()) list.Add(Read(r));
        return list;
    });

    static AiAttempt Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        StartedAt = Sql.Time(r.GetValue(1)),
        Runtime = Sql.S(r.GetValue(2)),
        RequestedModel = Sql.S(r.GetValue(3)),
        PricingBasis = Sql.S(r.GetValue(4)),
        ReservedCost = Sql.Dec(r.GetValue(5)),
        State = Enum.TryParse<AiAttemptState>(r.GetString(6), out var s) ? s : AiAttemptState.LOST,
        EndedAt = Sql.TimeN(r.GetValue(7)),
        ExitCode = r.IsDBNull(8) ? null : r.GetInt32(8),
        InputTokens = Long(r, 9),
        CachedInputTokens = Long(r, 10),
        CacheWriteInputTokens = Long(r, 11),
        OutputTokens = Long(r, 12),
        ReasoningOutputTokens = Long(r, 13),
        EffectiveModel = Sql.S(r.GetValue(14)),
        Cost = Sql.DecN(r.GetValue(15)),
        UnpricedReason = Sql.S(r.GetValue(16)),
        Context = Sql.S(r.GetValue(17)),
        PolicyVersion = Sql.S(r.GetValue(18)),
        InputHash = Sql.S(r.GetValue(19)),
        Role = Sql.S(r.GetValue(20))
    };

    static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
