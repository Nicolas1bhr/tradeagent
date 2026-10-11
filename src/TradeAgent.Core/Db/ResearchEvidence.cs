using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;

namespace TradeAgent.Core.Db;

/// <summary>
/// ONE TRIAL A RESEARCH-EVIDENCE MEASUREMENT READ: its place in the lineage (<see cref="Ordinal"/>, by registration then
/// run id), its run, the cluster it fell in, and how it was read — <see cref="ResearchEvidence.Streamed"/>,
/// <see cref="ResearchEvidence.NoStream"/> (no run row, a run measured under other evaluation semantics, no 1× stream, a
/// missing one, or one with no known day) or
/// <see cref="ResearchEvidence.HeldBack"/> (a holdout window reaches the run now, so nothing of it was read).
/// </summary>
public sealed record ResearchTrialRow(int Ordinal, string RunId, int Cluster, string Reading);

/// <summary>
/// WHY A RECORDED MEASUREMENT IS NOT SHOWN NOW: <see cref="RunId"/>, a run it read, is held back from research today
/// (<see cref="Why"/>, in <c>StrategyStore</c>'s words) — a holdout window set after the verdict reaches it, or its dataset
/// is gone so whether one does cannot be told.
/// </summary>
public sealed record ResearchWithheld(string RunId, string Why);

/// <summary>
/// WHAT ONE VERDICT MEASURED OF A VERSION'S RESEARCH EVIDENCE (<c>U-referee-v2a</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5, E2
/// and E3) — SHOWN, NEVER A CLAUSE.
///
/// <para>The candidate stream it read (<see cref="CandidateRunId"/>, with that 1× stream's <see cref="FrictionSha256"/> and
/// <see cref="TraceSha256"/>; all null when the lineage had none), its <see cref="Days"/> known days, E2's blocks
/// (<see cref="BlocksKnown"/> holding a known day, <see cref="BlocksPositive"/> of them positive), E3's
/// <see cref="TrialsM"/> trials of which <see cref="TrialsUnstreamed"/> had no readable stream, <see cref="NEff"/>
/// clusters, the candidate's per-day <see cref="Sr"/>, <see cref="Skew"/> and raw <see cref="Kurtosis"/>,
/// <see cref="Sr0"/> (per day), <see cref="Dsr"/> and the annualised <see cref="Ceiling"/>; and each gate's power and
/// status. A figure that is not a number is null — never 0.</para>
/// </summary>
public sealed record ResearchEvidenceRow(
    long CampaignId,
    string VersionId,
    int Method,
    string? CandidateRunId,
    string? FrictionSha256,
    string? TraceSha256,
    int Days,
    int BlocksKnown,
    int BlocksPositive,
    int TrialsM,
    int TrialsUnstreamed,
    int NEff,
    double? Sr,
    double? Skew,
    double? Kurtosis,
    double? Sr0,
    double? Dsr,
    double? Ceiling,
    double BlocksPower,
    double DsrPower,
    string BlocksStatus,
    string DsrStatus,
    DateTimeOffset At,
    IReadOnlyList<ResearchTrialRow> Trials)
{
    /// <summary>The family gate (CSCV-PBO over a family's trials): there are no families yet (<c>U-experiments-op</c>), so always this.</summary>
    public string FamilyStatus => GateStatus.Inconclusive;

    /// <summary>
    /// HOW MANY OF THE <see cref="TrialsUnstreamed"/> WERE RUNS MEASURED UNDER OTHER EVALUATION SEMANTICS
    /// (<c>U-evidence-evaluator-label</c>, rung 36): a trial whose run's <c>evaluator_version</c> is not this build's
    /// <see cref="Strategy.Referee.EvaluatorVersion"/>, or is unknown, counted in M and none of its returns used — or null
    /// for a measurement recorded before the rung, which applied no such check. Init-only with a default, so no
    /// construction site moved.
    /// </summary>
    public int? TrialsOtherSemantics { get; init; }

    /// <summary>
    /// THE EVALUATOR THE MEASUREMENT WAS TAKEN UNDER (rung 36, the measurement's identity with its campaign, version and
    /// method): <see cref="Strategy.Referee.EvaluatorVersion"/> of the build that recorded it, or null for one from before
    /// the rung — unknown. Read back only: <see cref="ResearchEvidence.Record"/> writes this build's constant and never this
    /// value, and <see cref="ResearchEvidence.Of"/> serves only this build's.
    /// </summary>
    public string? EvaluatorVersion { get; internal init; }

    /// <summary>The order the paper queue will read (<c>U-referee-v2c</c>): the DSR, an unknown one last.</summary>
    public double Rank => Dsr ?? double.NegativeInfinity;
}

/// <summary>
/// THE REFEREE'S RESEARCH EVIDENCE — E2'S BLOCKS AND E3'S DEFLATION, MEASURED AT EVERY VERDICT AND RECORDED WITH IT
/// (<c>U-referee-v2a</c>; <c>docs/EDGE-FACTORY.md</c> § 4.5 and :211-212; R04 move 3, "compute N_eff and DSR at verdict
/// request"). Method <see cref="Version"/>.
///
/// <para><b>What is read.</b> The trials: every distinct run charged to the verdict's campaign lineage
/// (<c>strategy_trial.charged</c>, the campaign as peek or as cost — <c>CampaignStore.TrialsCharged</c>'s reading), ordered by
/// first registration then run id. Each is read through its 1× stream (<c>U-trial-returns</c>) — unless a holdout window
/// reaches its run NOW (<see cref="StrategyStore.HeldBack"/>, under <c>BarAudience.ResearchEvidence</c>, which never reads
/// the holdout), when nothing of it is read; or unless its run was measured under other evaluation semantics than this
/// build's (<see cref="OtherSemantics"/>, <c>U-evidence-evaluator-label</c>), when it is <see cref="NoStream"/>: still a
/// trial in M — the reading that can only lower the deflated figures — and none of its returns used, because returns one
/// evaluator measured are not deflated as another's. The candidate: of the VERSION'S streamed trials, the one with the most known
/// days (tie: the earliest recorded run, then run id); none, and every statistic is <see cref="GateStatus.NoStream"/>.
/// The referee's own holdout run is no trial and keeps no stream, so nothing here reads a held-back bar, before the charge
/// or after it.</para>
///
/// <para><b>What is computed</b> is <see cref="BlockTest"/>, <see cref="Deflation"/> and <see cref="GatePower"/>, all
/// pure, over what was read: app-computed from the app's own streams, and nothing an op, a verb, an argument or an answer
/// carries reaches it.</para>
///
/// <para><b>What is written</b>, by <see cref="Record"/> only, inside the verdict's own write: one row per (campaign,
/// version, method, evaluator — this build's <see cref="Referee.EvaluatorVersion"/>, rung 36), first writer wins, and its
/// trials; every read serves this build's evaluator's row only, so a verdict asked again under another evaluator is
/// measured under its own and never served an earlier one's. Nothing updates or deletes either, and no clause of any policy
/// reads them: <c>ScoringPolicyV1</c>, <c>PaperPolicyV1</c>, the promotion and <c>RefereeFeedback.Text</c> are what they
/// were. A changed method is a new <see cref="Version"/> on new rows.</para>
/// </summary>
public sealed class ResearchEvidence(Database db)
{
    /// <summary>
    /// The measurement's method: <see cref="Deflation.Version"/> 1, <see cref="BlockTest.Version"/> 1 and
    /// <see cref="GatePower.Version"/> 1, over <see cref="DailyReturns.Version"/> 1 streams.
    /// </summary>
    public const int Version = 1;

    /// <summary>A trial read through its 1× stream.</summary>
    public const string Streamed = "streamed";

    /// <summary>A trial with no readable 1× stream: counted in M, and clustered alone.</summary>
    public const string NoStream = "no-stream";

    /// <summary>A trial a holdout window reaches now: nothing of it is read; counted in M, and clustered alone.</summary>
    public const string HeldBack = "held-back";

    /// <summary>
    /// WHY A TRIAL WITH A STREAM IS READ AS <see cref="NoStream"/> ALL THE SAME (<c>U-evidence-evaluator-label</c>): its run's
    /// label (<c>strategy_run.evaluator_version</c>) is not this build's <see cref="Strategy.Referee.EvaluatorVersion"/>, or a
    /// run recorded before rung 36 has none. Counted in <see cref="ResearchEvidenceRow.TrialsOtherSemantics"/>.
    /// </summary>
    public const string OtherSemantics = "measured under other evaluation semantics";

    readonly StrategyStore _strategies = new(db);
    readonly DatasetStore _datasets = new(db);

    const string Cols = "campaign_id, version_id, method, evaluator_version, candidate_run_id, friction_sha256, trace_sha256, days, blocks_known, "
        + "blocks_positive, trials_m, trials_unstreamed, n_eff, sr, skew, kurtosis, sr0, dsr, ceiling, blocks_power, dsr_power, "
        + "blocks_status, dsr_status, at, trials_other_semantics";

    /// <summary>
    /// MEASURES the version's research evidence over <paramref name="lineage"/> (the verdict campaign and every campaign it
    /// was renewed from), in one read. Writes nothing. <see cref="ResearchEvidenceRow.At"/> is <paramref name="at"/>.
    /// </summary>
    internal ResearchEvidenceRow Measure(long campaignId, string versionId, IReadOnlyList<long> lineage, DateTimeOffset at) =>
        db.Read(_ =>
        {
            ArgumentNullException.ThrowIfNull(versionId);
            ArgumentNullException.ThrowIfNull(lineage);
            var holdout = TapeHoldout.Of(BarAudience.ResearchEvidence, _datasets);

            var read = new List<(string RunId, string Reading, StrategyRunRow? Run, StrategyStreamRow? Stream)>();
            var other = 0;
            foreach (var runId in TrialRuns(lineage))
            {
                var run = _strategies.RunById(runId);
                if (run is null) { read.Add((runId, NoStream, null, null)); continue; }
                if (_strategies.HeldBack(run, holdout) is not null) { read.Add((runId, HeldBack, run, null)); continue; }

                // MEASURED UNDER OTHER EVALUATION SEMANTICS — another evaluator's label, or none from before rung 36: a trial,
                // and none of its returns, which this build's evaluator did not measure.
                if (!string.Equals(run.EvaluatorVersion, Referee.EvaluatorVersion, StringComparison.Ordinal))
                {
                    read.Add((runId, NoStream, run, null));
                    other++;
                    continue;
                }

                var stream = _strategies.StreamsOf(runId).FirstOrDefault(s => s.Multiple == 1);
                read.Add(stream is { Missing: null } && stream.Days.Any(d => d.NetReturn is not null)
                    ? (runId, Streamed, run, stream)
                    : (runId, NoStream, run, null));
            }

            var trials = read.Select(t => new TrialStream(t.RunId, t.Stream?.Days)).ToList();
            var clusters = Deflation.Cluster(trials);
            var trialRows = read.Select((t, i) => new ResearchTrialRow(i, t.RunId, clusters.ClusterOf[i], t.Reading)).ToList();

            var candidate = read
                .Where(t => t.Stream is not null && string.Equals(t.Run!.VersionId, versionId, StringComparison.Ordinal))
                .OrderByDescending(t => t.Stream!.Days.Count(d => d.NetReturn is not null))
                .ThenBy(t => t.Run!.CreatedAt)
                .ThenBy(t => t.RunId, StringComparer.Ordinal)
                .Select(t => t.Stream)
                .FirstOrDefault();

            var unstreamed = read.Count(t => t.Stream is null);
            if (candidate is null)
                return new ResearchEvidenceRow(campaignId, versionId, Version, null, null, null, 0, 0, 0, read.Count, unstreamed,
                    clusters.NEff, null, null, null, null, null, null, GatePower.Blocks(0), GatePower.Dsr(0, clusters.NEff),
                    GateStatus.NoStream, GateStatus.NoStream, at, trialRows)
                { TrialsOtherSemantics = other, EvaluatorVersion = Referee.EvaluatorVersion };

            var deflated = Deflation.Of(candidate.Days, trials);
            var blocks = BlockTest.Of(candidate.Days);
            var blocksPower = GatePower.Blocks(blocks.Days);
            var dsrPower = GatePower.Dsr(deflated.Days, deflated.NEff);
            return new ResearchEvidenceRow(campaignId, versionId, Version, candidate.RunId, candidate.FrictionSha256,
                candidate.TraceSha256, deflated.Days, blocks.Known, blocks.Positive, deflated.M, deflated.Unstreamed,
                deflated.NEff, deflated.Candidate?.Sr, deflated.Candidate?.Skew, deflated.Candidate?.Kurtosis, deflated.Sr0,
                deflated.Dsr, deflated.Ceiling, blocksPower, dsrPower,
                GatePower.Status(blocksPower, blocks.Known == BlockTest.Count),
                GatePower.Status(dsrPower, deflated.Dsr is not null), at, trialRows)
            { TrialsOtherSemantics = other, EvaluatorVersion = Referee.EvaluatorVersion };
        });

    /// <summary>
    /// Every distinct run charged to the lineage, by its first registration and then its id — <c>CampaignStore.TrialsCharged</c>'s
    /// reading (the campaign as peek or as cost), over the whole chain.
    /// </summary>
    List<string> TrialRuns(IReadOnlyList<long> lineage)
    {
        var runs = new List<string>();
        if (lineage.Count == 0) return runs;
        var names = lineage.Select((_, i) => $"$c{i}").ToList();
        var inList = string.Join(",", names);
        using var c = db.Cmd(
            $"SELECT run_id, MIN(registered_at) AS first FROM strategy_trial WHERE charged=1 "
            + $"AND (campaign_id IN ({inList}) OR charged_to IN ({inList})) GROUP BY run_id ORDER BY first, run_id",
            [.. lineage.Select((id, i) => (names[i], (object?)id))]);
        using var r = c.ExecuteReader();
        while (r.Read()) runs.Add(r.GetString(0));
        return runs;
    }

    /// <summary>
    /// RECORDS one measurement and its trials — called inside the verdict's own write (<c>Referee.Verdict</c>), so it lands
    /// with the promotion or not at all. First writer wins: a measurement already recorded for the (campaign, version,
    /// method) under this build's evaluator stands, and this writes nothing. The evaluator written is this build's
    /// <see cref="Referee.EvaluatorVersion"/>, never <see cref="ResearchEvidenceRow.EvaluatorVersion"/>. True when this call recorded it — what the verdict's write asks before it
    /// seals a holdout record beside it (<c>U-referee-v2c</c>).
    /// </summary>
    internal bool Record(ResearchEvidenceRow row) => db.Write(_ =>
    {
        ArgumentNullException.ThrowIfNull(row);
        using var insert = db.Cmd($"""
            INSERT INTO referee_research({Cols})
            VALUES($camp,$ver,$method,$eval,$run,$friction,$trace,$days,$bk,$bp,$m,$un,$neff,$sr,$skew,$kurt,$sr0,$dsr,$ceil,
                   $bpow,$dpow,$bstat,$dstat,$at,$other)
            ON CONFLICT(campaign_id, version_id, method, evaluator_version) DO NOTHING
            """,
            ("$camp", row.CampaignId), ("$ver", row.VersionId), ("$method", row.Method), ("$eval", Referee.EvaluatorVersion),
            ("$run", row.CandidateRunId),
            ("$friction", row.FrictionSha256), ("$trace", row.TraceSha256), ("$days", row.Days),
            ("$bk", row.BlocksKnown), ("$bp", row.BlocksPositive), ("$m", row.TrialsM), ("$un", row.TrialsUnstreamed),
            ("$neff", row.NEff), ("$sr", Text(row.Sr)), ("$skew", Text(row.Skew)), ("$kurt", Text(row.Kurtosis)),
            ("$sr0", Text(row.Sr0)), ("$dsr", Text(row.Dsr)), ("$ceil", Text(row.Ceiling)),
            ("$bpow", Text(row.BlocksPower)), ("$dpow", Text(row.DsrPower)),
            ("$bstat", row.BlocksStatus), ("$dstat", row.DsrStatus), ("$at", Sql.T(row.At)),
            ("$other", row.TrialsOtherSemantics));
        if (insert.ExecuteNonQuery() == 0) return false;

        foreach (var trial in row.Trials)
        {
            using var c = db.Cmd("""
                INSERT INTO referee_research_trial(campaign_id, version_id, method, evaluator_version, ordinal, run_id, cluster, reading)
                VALUES($camp,$ver,$method,$eval,$ord,$run,$cluster,$reading)
                """,
                ("$camp", row.CampaignId), ("$ver", row.VersionId), ("$method", row.Method),
                ("$eval", Referee.EvaluatorVersion), ("$ord", trial.Ordinal),
                ("$run", trial.RunId), ("$cluster", trial.Cluster), ("$reading", trial.Reading));
            c.ExecuteNonQuery();
        }
        return true;
    });

    /// <summary>
    /// The measurement recorded for one version under one campaign at this build's method and under this build's
    /// evaluator, or null — another evaluator's, or one from before rung 36 (unknown), is never served for this build's.
    /// </summary>
    public ResearchEvidenceRow? Of(long campaignId, string versionId) => db.Read(_ =>
    {
        using var c = db.Cmd($"""
            SELECT {Cols} FROM referee_research
             WHERE campaign_id=$camp AND version_id=$ver AND method=$m AND evaluator_version=$eval
            """, ("$camp", campaignId), ("$ver", versionId), ("$m", Version), ("$eval", Referee.EvaluatorVersion));
        return ReadRows(c).FirstOrDefault();
    });

    /// <summary>
    /// WHETHER <paramref name="row"/>'S FIGURES MAY BE SHOWN TODAY, asked at each read: null when they may, else the first
    /// run it read — its candidate, then every trial it read through a stream, in order — that a holdout window reaches NOW
    /// (<see cref="StrategyStore.HeldBack"/>, under <c>BarAudience.ResearchEvidence</c>, the check <see cref="Measure"/>
    /// applied), with why. A run the ledger no longer holds is withheld too: whether a window reaches it cannot be told, and
    /// a figure is not shown on a guess. The recorded rows are not touched — the measurement stays true about what ran; it
    /// is only no longer served. A trial read as <see cref="NoStream"/> or <see cref="HeldBack"/> gave no figure of its
    /// bars, so it is not asked.
    /// </summary>
    public ResearchWithheld? Withheld(ResearchEvidenceRow row) => db.Read(_ =>
    {
        ArgumentNullException.ThrowIfNull(row);
        var holdout = TapeHoldout.Of(BarAudience.ResearchEvidence, _datasets);
        var read = row.Trials.Where(t => t.Reading == Streamed).Select(t => t.RunId);
        if (row.CandidateRunId is { } candidate) read = read.Prepend(candidate);
        foreach (var runId in read.Distinct(StringComparer.Ordinal))
        {
            if (_strategies.RunById(runId) is not { } run)
                return new ResearchWithheld(runId, $"run {runId} is no longer in this installation's strategy ledger, so "
                    + "whether a holdout window reaches it cannot be read now — and a figure is not shown on a guess.");
            if (_strategies.HeldBack(run, holdout) is { } why) return new ResearchWithheld(runId, why);
        }
        return null;
    });

    /// <summary>Every measurement recorded, any method and any evaluator (<see cref="ResearchEvidenceRow.EvaluatorVersion"/> says which), oldest first.</summary>
    public IReadOnlyList<ResearchEvidenceRow> All() => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT {Cols} FROM referee_research ORDER BY at, campaign_id, version_id, method, evaluator_version");
        return (IReadOnlyList<ResearchEvidenceRow>)ReadRows(c);
    });

    List<ResearchEvidenceRow> ReadRows(SqliteCommand c)
    {
        var heads = new List<ResearchEvidenceRow>();
        using (var r = c.ExecuteReader())
            while (r.Read())
                heads.Add(new ResearchEvidenceRow(
                    r.GetInt64(0), r.GetString(1), r.GetInt32(2), Sql.S(r.GetValue(4)), Sql.S(r.GetValue(5)),
                    Sql.S(r.GetValue(6)), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11),
                    r.GetInt32(12), Number(r.GetValue(13)), Number(r.GetValue(14)), Number(r.GetValue(15)),
                    Number(r.GetValue(16)), Number(r.GetValue(17)), Number(r.GetValue(18)), Number(r.GetValue(19))!.Value,
                    Number(r.GetValue(20))!.Value, r.GetString(21), r.GetString(22), Sql.Time(r.GetString(23)), [])
                {
                    EvaluatorVersion = Sql.S(r.GetValue(3)),
                    TrialsOtherSemantics = r.IsDBNull(24) ? null : r.GetInt32(24)
                });

        var rows = new List<ResearchEvidenceRow>(heads.Count);
        foreach (var head in heads)
        {
            using var t = db.Cmd("""
                SELECT ordinal, run_id, cluster, reading FROM referee_research_trial
                 WHERE campaign_id=$camp AND version_id=$ver AND method=$m AND evaluator_version IS $eval ORDER BY ordinal
                """, ("$camp", head.CampaignId), ("$ver", head.VersionId), ("$m", head.Method),
                ("$eval", head.EvaluatorVersion));
            var trials = new List<ResearchTrialRow>();
            using var r = t.ExecuteReader();
            while (r.Read()) trials.Add(new ResearchTrialRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3)));
            rows.Add(head with { Trials = trials });
        }
        return rows;
    }

    /// <summary>A figure as the row holds it: invariant round-trip text, or NULL for one that is not a number.</summary>
    static string? Text(double? x) => x is { } v && double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : null;

    static double? Number(object? o) => o is null or DBNull ? null : double.Parse((string)o, CultureInfo.InvariantCulture);
}
