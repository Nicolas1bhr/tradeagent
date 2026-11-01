using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Db;

namespace TradeAgent.Core.Proving;

/// <summary>
/// ONE CONFIGURATION'S SCORE (<c>U-proving-ground-c</c> item 4; <c>docs/VISION.md</c> § 6.4: "planted edges found, false
/// discoveries on noise, AI dollars …"). SIMULATED in every row and sentence: a proving market measures process, never market
/// truth, and no evidence path reads it (SIM-001).
/// </summary>
/// <param name="Configuration">The configuration's name.</param>
/// <param name="Kind">'floor' or 'seats'.</param>
/// <param name="HomeSha256">SHA-256 over the copied stores the configuration started from — equal across a run's configurations.</param>
/// <param name="TrialBudget">The trial budget each market's campaign enforced.</param>
/// <param name="VerdictBudget">The verdict budget each market's campaign enforced.</param>
/// <param name="AiEnvelope">The root unit's AI envelope admission enforced.</param>
/// <param name="DeadlineSeconds">The deadline the configuration worked under.</param>
/// <param name="PlantedFound">Markets with a planted edge the floor's grammar can express on which a version PASSED.</param>
/// <param name="PlantedExpressible">Markets with a planted edge on the bare signal: the denominator.</param>
/// <param name="FalseDiscoveries">Versions that PASSED on a noise market.</param>
/// <param name="TrapsFallenFor">Versions that PASSED on a trap's market.</param>
/// <param name="TrapVerdicts">Verdicts spent on a trap's market, PASS or not.</param>
/// <param name="TrialsSpent">Trials the campaigns charged.</param>
/// <param name="VerdictsSpent">Verdicts the campaigns charged.</param>
/// <param name="AiDollarsMetered">What the meter priced the configuration's turns at, or null where one was not priced — never a zero standing for unknown.</param>
/// <param name="EnvelopeRefusals">Positions whose launch the envelope refused: wakes due and never consumed when the configuration went
/// quiet, while the meter admitted no further turn for them.</param>
/// <param name="FirstPassSeconds">Seconds from the configuration's start to its first PASS, or null for none.</param>
/// <param name="Sources">The rows each figure was read from, by table and id.</param>
/// <param name="OffPathMarkets">Markets with a planted edge the brief did not ask about (<c>U-proving-ground-d</c> item 3; VISION § 1 test 9).</param>
/// <param name="OffPathFound">Off-path markets on which a version PASSED.</param>
/// <param name="OffPathReported">Off-path markets whose PASSING version a record names to another position — a proposal to a mandate's
/// decider or a lateral note — so the finding outlives the finder's context.</param>
/// <param name="Calibration">The configuration's forecasts as the app scored them against its own resolutions (<c>U-proving-ground-d</c> item 2).</param>
public sealed record ProvingScore(
    string Configuration, string Kind, string HomeSha256,
    int TrialBudget, int VerdictBudget, decimal AiEnvelope, int DeadlineSeconds,
    int PlantedFound, int PlantedExpressible, int FalseDiscoveries, int TrapsFallenFor, int TrapVerdicts,
    int TrialsSpent, int VerdictsSpent, decimal? AiDollarsMetered, int EnvelopeRefusals, double? FirstPassSeconds,
    string Sources, int OffPathMarkets, int OffPathFound, int OffPathReported, ProvingCalibration Calibration)
{
    /// <summary>The label every row and sentence carries.</summary>
    public const string Label = "SIMULATED";

    /// <summary>Real dollars a proving configuration can spend: none — the proving host composes no connector that reaches money.</summary>
    public const decimal RealDollars = 0m;

    /// <summary>The score in one sentence, labelled.</summary>
    public string Sentence => string.Create(CultureInfo.InvariantCulture,
        $"{Label} — {Configuration} ({Kind}): planted found {PlantedFound}/{PlantedExpressible}, false discoveries on noise {FalseDiscoveries}, "
        + $"traps fallen for {TrapsFallenFor} ({TrapVerdicts} trap verdict(s)), trials {TrialsSpent} ({TrialBudget} per market), verdicts "
        + $"{VerdictsSpent} ({VerdictBudget} per market), AI dollars metered {(AiDollarsMetered is { } d ? d.ToString(CultureInfo.InvariantCulture) : "UNKNOWN")} "
        + $"of an envelope of {AiEnvelope}, real dollars {RealDollars}, envelope refusals {EnvelopeRefusals}, first PASS "
        + $"{(FirstPassSeconds is { } s ? $"{s:0.0} s" : "none")}; off-path found {OffPathFound}/{OffPathMarkets}, reported {OffPathReported}; "
        + $"calibration {Calibration.Words}; home {HomeSha256[..Math.Min(12, HomeSha256.Length)]}");
}

/// <summary>
/// A CONFIGURATION'S CALIBRATION (<c>U-proving-ground-d</c> item 2; VISION § 6.4: "forecast calibration"): its positions' forecasts the app
/// resolved yes or no and scored (<see cref="Scorecards"/>: the forecaster's probability against the app's outcome, and the base rate — what
/// the records already said before each forecast — against the same outcome), as SUMS, so every mean is over <see cref="Scored"/>; the
/// outcomes that came out yes; and, apart and never in a score, the forecasts still open, resolved void or written unscored.
/// </summary>
public sealed record ProvingCalibration(int Scored, int Hits, double BrierSum, double BaseBrierSum, double LogSum, double BaseLogSum, int Unscored)
{
    /// <summary>No forecast scored: every mean null, never a zero.</summary>
    public static readonly ProvingCalibration None = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>The mean Brier score, or null with nothing scored.</summary>
    public double? Brier => Scored == 0 ? null : BrierSum / Scored;

    /// <summary>The base rate's mean Brier score over the same forecasts, or null.</summary>
    public double? BaseBrier => Scored == 0 ? null : BaseBrierSum / Scored;

    /// <summary>The mean log score, or null.</summary>
    public double? Log => Scored == 0 ? null : LogSum / Scored;

    /// <summary>The share of scored forecasts whose outcome was yes — the rate a forecaster would have to beat — or null.</summary>
    public double? Rate => Scored == 0 ? null : (double)Hits / Scored;

    /// <summary>The calibration in words, the base rate beside it.</summary>
    public string Words => Scored == 0
        ? string.Create(CultureInfo.InvariantCulture, $"none scored ({Unscored} open, void or unscored)")
        : string.Create(CultureInfo.InvariantCulture,
            $"Brier {Brier:0.0000} against the base rate's {BaseBrier:0.0000} over {Scored} scored ({Hits} yes, rate {Rate:0.00}), log {Log:0.0000}; {Unscored} open, void or unscored");
}

/// <summary>
/// THE PROVING GROUND'S OWN STORE (<c>U-proving-ground-c</c> item 4): <c>proving.db</c>, the proving home's own file with its own
/// ladder — the <see cref="TapeStore"/> pattern — and never a rung of the real schema. <c>proving_run</c> is one run of a
/// ladder over one seeded home; <c>proving_score</c> one configuration's score in it. Every row says SIMULATED, by a CHECK the
/// file enforces, and the store is marked a proving home's and refuses any other mark.
///
/// <para><b>Read by no evidence path</b> (SIM-001): this type is in <c>TradeAgent.Core.Proving</c>, which the source scan
/// (<c>ProvingHomeTests.No_evidence_path_names_the_proving_namespace</c>) forbids every file that judges, records or serves
/// evidence to name; and its tables live in no database an evidence path opens.</para>
/// </summary>
public sealed class ProvingStore : IDisposable
{
    /// <summary>The layout version this build writes and reads.</summary>
    public const int Schema = 2;

    /// <summary>The file's name in a proving home.</summary>
    public const string FileName = "proving.db";

    readonly SqliteConnection _conn;

    /// <summary>The file.</summary>
    public string File { get; }

    /// <summary>
    /// OPENS — OR CREATES — THE STORE AT <paramref name="file"/>. Refused, with nothing written, for a file of a newer layout or
    /// one marked anything but a proving home's.
    /// </summary>
    public ProvingStore(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        File = Path.GetFullPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = File, Pooling = false }.ToString());
        _conn.Open();
        try
        {
            using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            Exec("CREATE TABLE IF NOT EXISTS proving_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)", tx);
            var mark = Scalar($"SELECT value FROM proving_meta WHERE key='{HomeMark.Key}'", tx) as string;
            var version = Scalar("SELECT value FROM proving_meta WHERE key='schema'", tx) as string;
            // EXACTLY THIS BUILD'S LAYOUT, OR NONE YET: any other stamp — newer, older, negative, not a number — is refused, never guessed.
            var have = version is null ? 0
                : version == Schema.ToString(CultureInfo.InvariantCulture) ? Schema
                : throw new InvalidOperationException($"the proving store at {File} is stamped layout '{version}', and this build reads version {Schema} only. Nothing was read or written.");
            if (mark is not null && mark != HomeMark.Word(HomeKind.Proving))
                throw new InvalidOperationException($"the proving store at {File} is marked '{mark}', and a proving store is a proving home's only. Nothing was read or written.");
            if (mark is null)
                Exec($"INSERT INTO proving_meta(key, value) VALUES('{HomeMark.Key}', '{HomeMark.Word(HomeKind.Proving)}')", tx);
            if (have < Schema)
            {
                Exec($"""
                    CREATE TABLE proving_run(
                      id          INTEGER PRIMARY KEY AUTOINCREMENT,
                      started_at  TEXT NOT NULL,
                      seed_home   TEXT NOT NULL,
                      markets     TEXT NOT NULL,
                      label       TEXT NOT NULL CHECK (label = '{ProvingScore.Label}'));
                    CREATE TABLE proving_score(
                      run_id              INTEGER NOT NULL REFERENCES proving_run(id),
                      ordinal             INTEGER NOT NULL,
                      configuration       TEXT NOT NULL,
                      kind                TEXT NOT NULL CHECK (kind IN ('floor','seats')),
                      home_sha256         TEXT NOT NULL,
                      trial_budget        INTEGER NOT NULL,
                      verdict_budget      INTEGER NOT NULL,
                      ai_envelope         TEXT NOT NULL,
                      deadline_seconds    INTEGER NOT NULL,
                      planted_found       INTEGER NOT NULL,
                      planted_expressible INTEGER NOT NULL,
                      false_discoveries   INTEGER NOT NULL,
                      traps_fallen_for    INTEGER NOT NULL,
                      trap_verdicts       INTEGER NOT NULL,
                      trials_spent        INTEGER NOT NULL,
                      verdicts_spent      INTEGER NOT NULL,
                      ai_dollars_metered  TEXT NULL,
                      real_dollars        TEXT NOT NULL CHECK (real_dollars = '0'),
                      envelope_refusals   INTEGER NOT NULL,
                      first_pass_seconds  REAL NULL,
                      off_path_markets    INTEGER NOT NULL,
                      off_path_found      INTEGER NOT NULL,
                      off_path_reported   INTEGER NOT NULL,
                      forecasts_scored    INTEGER NOT NULL,
                      forecast_hits       INTEGER NOT NULL,
                      brier_sum           REAL NOT NULL,
                      base_brier_sum      REAL NOT NULL,
                      log_sum             REAL NOT NULL,
                      base_log_sum        REAL NOT NULL,
                      forecasts_unscored  INTEGER NOT NULL,
                      sources             TEXT NOT NULL,
                      sentence            TEXT NOT NULL,
                      label               TEXT NOT NULL CHECK (label = '{ProvingScore.Label}'),
                      PRIMARY KEY (run_id, ordinal));
                    INSERT INTO proving_meta(key, value) VALUES('schema', '2') ON CONFLICT(key) DO UPDATE SET value='2';
                    """, tx);
            }
            tx.Commit();
        }
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>Records a run over the seeded home at <paramref name="seedHome"/>, its markets described in <paramref name="markets"/>.</summary>
    public long BeginRun(DateTimeOffset startedAt, string seedHome, string markets)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "INSERT INTO proving_run(started_at, seed_home, markets, label) VALUES($a, $h, $m, $l) RETURNING id";
        c.Parameters.AddWithValue("$a", startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        c.Parameters.AddWithValue("$h", seedHome);
        c.Parameters.AddWithValue("$m", markets);
        c.Parameters.AddWithValue("$l", ProvingScore.Label);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Records <paramref name="score"/> as the <paramref name="ordinal"/>-th configuration of run <paramref name="runId"/>.</summary>
    public void Record(long runId, int ordinal, ProvingScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO proving_score(run_id, ordinal, configuration, kind, home_sha256, trial_budget, verdict_budget, ai_envelope,
              deadline_seconds, planted_found, planted_expressible, false_discoveries, traps_fallen_for, trap_verdicts, trials_spent,
              verdicts_spent, ai_dollars_metered, real_dollars, envelope_refusals, first_pass_seconds, off_path_markets, off_path_found,
              off_path_reported, forecasts_scored, forecast_hits, brier_sum, base_brier_sum, log_sum, base_log_sum, forecasts_unscored, sources,
              sentence, label)
            VALUES($r, $o, $c, $k, $h, $tb, $vb, $e, $d, $pf, $pe, $fd, $tf, $tv, $ts, $vs, $ai, '0', $er, $fp, $opm, $opf, $opr, $cs, $ch, $cb,
              $cbb, $cl, $cbl, $cu, $src, $s, $l)
            """;
        object? N<T>(T? v) where T : struct => v is { } x ? x : DBNull.Value;
        c.Parameters.AddWithValue("$r", runId);
        c.Parameters.AddWithValue("$o", ordinal);
        c.Parameters.AddWithValue("$c", score.Configuration);
        c.Parameters.AddWithValue("$k", score.Kind);
        c.Parameters.AddWithValue("$h", score.HomeSha256);
        c.Parameters.AddWithValue("$tb", score.TrialBudget);
        c.Parameters.AddWithValue("$vb", score.VerdictBudget);
        c.Parameters.AddWithValue("$e", score.AiEnvelope.ToString(CultureInfo.InvariantCulture));
        c.Parameters.AddWithValue("$d", score.DeadlineSeconds);
        c.Parameters.AddWithValue("$pf", score.PlantedFound);
        c.Parameters.AddWithValue("$pe", score.PlantedExpressible);
        c.Parameters.AddWithValue("$fd", score.FalseDiscoveries);
        c.Parameters.AddWithValue("$tf", score.TrapsFallenFor);
        c.Parameters.AddWithValue("$tv", score.TrapVerdicts);
        c.Parameters.AddWithValue("$ts", score.TrialsSpent);
        c.Parameters.AddWithValue("$vs", score.VerdictsSpent);
        c.Parameters.AddWithValue("$ai", score.AiDollarsMetered is { } ai ? ai.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        c.Parameters.AddWithValue("$er", score.EnvelopeRefusals);
        c.Parameters.AddWithValue("$fp", N(score.FirstPassSeconds));
        c.Parameters.AddWithValue("$opm", score.OffPathMarkets);
        c.Parameters.AddWithValue("$opf", score.OffPathFound);
        c.Parameters.AddWithValue("$opr", score.OffPathReported);
        c.Parameters.AddWithValue("$cs", score.Calibration.Scored);
        c.Parameters.AddWithValue("$ch", score.Calibration.Hits);
        c.Parameters.AddWithValue("$cb", score.Calibration.BrierSum);
        c.Parameters.AddWithValue("$cbb", score.Calibration.BaseBrierSum);
        c.Parameters.AddWithValue("$cl", score.Calibration.LogSum);
        c.Parameters.AddWithValue("$cbl", score.Calibration.BaseLogSum);
        c.Parameters.AddWithValue("$cu", score.Calibration.Unscored);
        c.Parameters.AddWithValue("$src", score.Sources);
        c.Parameters.AddWithValue("$s", score.Sentence);
        c.Parameters.AddWithValue("$l", ProvingScore.Label);
        c.ExecuteNonQuery();
    }

    /// <summary>Every score of run <paramref name="runId"/>, in its configurations' order.</summary>
    public IReadOnlyList<ProvingScore> Scores(long runId)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            SELECT configuration, kind, home_sha256, trial_budget, verdict_budget, ai_envelope, deadline_seconds, planted_found,
                   planted_expressible, false_discoveries, traps_fallen_for, trap_verdicts, trials_spent, verdicts_spent,
                   ai_dollars_metered, envelope_refusals, first_pass_seconds, sources, off_path_markets, off_path_found, off_path_reported,
                   forecasts_scored, forecast_hits, brier_sum, base_brier_sum, log_sum, base_log_sum, forecasts_unscored
            FROM proving_score WHERE run_id=$r ORDER BY ordinal
            """;
        c.Parameters.AddWithValue("$r", runId);
        using var r = c.ExecuteReader();
        var list = new List<ProvingScore>();
        while (r.Read())
            list.Add(new ProvingScore(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                decimal.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9),
                r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
                r.IsDBNull(14) ? null : decimal.Parse(r.GetString(14), CultureInfo.InvariantCulture), r.GetInt32(15),
                r.IsDBNull(16) ? null : r.GetDouble(16), r.GetString(17), r.GetInt32(18), r.GetInt32(19), r.GetInt32(20),
                new ProvingCalibration(r.GetInt32(21), r.GetInt32(22), r.GetDouble(23), r.GetDouble(24), r.GetDouble(25), r.GetDouble(26), r.GetInt32(27))));
        return list;
    }

    /// <summary>The newest run's id, or null for none.</summary>
    public long? LastRun()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT MAX(id) FROM proving_run";
        return c.ExecuteScalar() is long id ? id : null;
    }

    void Exec(string sql, SqliteTransaction tx)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    object? Scalar(string sql, SqliteTransaction tx)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        return c.ExecuteScalar();
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>
/// WHAT ONE MARKET'S TRUTH SAYS, as its manifest states it — the score's key, read only by <see cref="ProvingScorer"/> and only
/// from the seeded home's truth folder, never from a configuration's home (where no seat can read it).
/// </summary>
public sealed record MarketTruth(string Instrument, string NormalisedSha256, int Planted, int PlantedOnTheSignal, int Traps, string File)
{
    /// <summary>
    /// Whether the run placed this market OFF THE BRIEF's PATH (<c>U-proving-ground-d</c> item 3): a planted edge beside, not inside, what the
    /// configurations were asked to work — scored as the off-path exam, never among the planted edges the brief named.
    /// </summary>
    public bool OffPath { get; init; }

    /// <summary>Whether the market is pure noise: nothing planted and no trap.</summary>
    public bool Noise => Planted == 0 && Traps == 0;

    /// <summary>Reads the manifest at <paramref name="file"/>.</summary>
    public static MarketTruth Read(string file)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var root = doc.RootElement;
        var planted = root.GetProperty("planted").EnumerateArray().ToList();
        return new MarketTruth(
            root.GetProperty("instrument").GetString()!,
            root.GetProperty("normalised_sha256").GetString()!,
            planted.Count,
            planted.Count(p => p.GetProperty("series").GetString() == FloorGrammar.Series),
            root.GetProperty("traps").GetArrayLength(),
            file);
    }
}

/// <summary>
/// AN ENVELOPE REFUSAL THE HOST SAW: a position left with wakes due and never consumed when its configuration went quiet, while the
/// meter admitted no further turn for it — the launch its envelope refused — with those wakes and the meter's reading.
/// </summary>
public sealed record EnvelopeRefusal(string Role, IReadOnlyList<string> WakeIds, decimal Spent, decimal Cap);

/// <summary>
/// SCORES ONE CONFIGURATION (<c>U-proving-ground-c</c> item 4) from the stores of the home it worked in, after its process has
/// exited, against the truths of the markets it worked on. Each figure is read from the configuration home's own rows — promotions,
/// campaign trials and verdicts, launch rows, wakes — and the rows are named in <see cref="ProvingScore.Sources"/>.
/// </summary>
public static class ProvingScorer
{
    /// <summary>The score of <paramref name="configuration"/>, which worked in <paramref name="db"/> from <paramref name="startedAt"/>.</summary>
    public static ProvingScore Score(Database db, ProvingConfiguration configuration, string homeSha256, IReadOnlyList<MarketTruth> truths,
        DateTimeOffset startedAt, IReadOnlyList<EnvelopeRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(truths);
        ArgumentNullException.ThrowIfNull(refusals);
        if (db.Kind != HomeKind.Proving) throw new InvalidOperationException("a proving score is read only from a proving home's database (SIM-001).");

        var datasets = new DatasetStore(db);
        var campaigns = new CampaignStore(db);
        var byCampaign = new Dictionary<long, MarketTruth>();
        foreach (var campaign in campaigns.All())
        {
            var set = datasets.ById(campaign.HoldoutDatasetId)
                      ?? throw new InvalidOperationException($"campaign {campaign.Id} holds out dataset {campaign.HoldoutDatasetId}, which this home does not hold.");
            var truth = truths.SingleOrDefault(t => string.Equals(t.NormalisedSha256, set.NormalisedSha256, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"dataset {set.Id} ({set.Pair}) matches no market truth of this run.");
            byCampaign[campaign.Id] = truth;
        }

        var promotions = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT id, campaign_id, verdict, at FROM strategy_promotion ORDER BY at, id");
            using var r = c.ExecuteReader();
            var list = new List<(string Id, long Campaign, string Verdict, DateTimeOffset At)>();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt64(1), r.GetString(2),
                    DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
            return list;
        });
        static bool Pass(string verdict) => verdict is PromotionVerdict.Promoted or PromotionVerdict.PaperEligible;
        var passes = promotions.Where(p => Pass(p.Verdict) && byCampaign.ContainsKey(p.Campaign)).ToList();

        // THE BRIEF'S MARKETS AND THE OFF-PATH ONES APART: an off-path market is the exam of § 1 test 9, never one of the planted edges asked for.
        var found = byCampaign.Where(kv => !kv.Value.OffPath && kv.Value.PlantedOnTheSignal > 0 && passes.Any(p => p.Campaign == kv.Key)).Select(kv => kv.Value.Instrument).Distinct().Count();
        var expressible = truths.Count(t => !t.OffPath && t.PlantedOnTheSignal > 0);
        var falseDiscoveries = passes.Count(p => !byCampaign[p.Campaign].OffPath && byCampaign[p.Campaign].Noise);
        var trapsFallen = passes.Count(p => !byCampaign[p.Campaign].OffPath && byCampaign[p.Campaign].Traps > 0);

        // THE OFF-PATH EXAM: a PASS on an off-path market, and a record that names its passing version to another position — a proposal
        // to a mandate's decider, or a lateral note — read from this home's own org rows, which the finder wrote through its verbs.
        var offPathPasses = passes.Where(p => byCampaign[p.Campaign].OffPath).ToList();
        var offPathFound = offPathPasses.Select(p => byCampaign[p.Campaign].Instrument).Distinct().Count();
        var offPathMarkets = truths.Count(t => t.OffPath);
        var reports = new List<string>();
        var offPathReported = offPathPasses.GroupBy(p => byCampaign[p.Campaign].Instrument).Count(g => g.Any(p =>
        {
            var version = PromotedVersion(db, p.Id);
            var named = version is null ? null : Reported(db, version);
            if (named is not null) reports.Add(named);
            return named is not null;
        }));

        var calibration = Calibration(db);

        var trials = byCampaign.Keys.Sum(campaigns.TrialsCharged);
        var verdicts = byCampaign.Keys.ToDictionary(id => id, id => campaigns.Verdicts(id).Count);
        var trapVerdicts = verdicts.Where(kv => byCampaign[kv.Key].Traps > 0).Sum(kv => kv.Value);

        var attempts = db.Read(_ =>
        {
            using var c = db.Cmd("SELECT id, cost FROM ai_attempt ORDER BY started_at, id");
            using var r = c.ExecuteReader();
            var list = new List<(string Id, decimal? Cost)>();
            while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture)));
            return list;
        });
        decimal? metered = attempts.All(a => a.Cost is not null) ? attempts.Sum(a => a.Cost!.Value) : null;

        // A REFUSAL COUNTS ONLY WHERE THE HOME'S OWN ROWS AGREE: every wake it names is there, and nothing consumed it.
        var refused = refusals.Where(f => f.WakeIds.Count > 0 && f.WakeIds.All(id => db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM mission_event WHERE id=$i AND consumed_by IS NULL", ("$i", id));
            return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }))).ToList();

        double? firstPass = passes.Count == 0 ? null : (passes.Min(p => p.At) - startedAt).TotalSeconds;
        var sources = JsonSerializer.Serialize(new
        {
            label = ProvingScore.Label,
            promotions = passes.Select(p => p.Id).ToList(),
            all_promotions = "strategy_promotion: " + promotions.Count + " row(s)",
            campaigns = byCampaign.Select(kv => new { campaign = kv.Key, market = kv.Value.Instrument, truth = Path.GetFileName(kv.Value.File), verdicts = verdicts[kv.Key] }).ToList(),
            trials = "strategy_trial where charged, per campaign (CampaignStore.TrialsCharged)",
            verdicts = "strategy_verdict per campaign (CampaignStore.Verdicts)",
            ai_attempts = attempts.Select(a => a.Id).ToList(),
            refused = refused.Select(f => new { role = f.Role, wakes = f.WakeIds }).ToList(),
            off_path = new { passes = offPathPasses.Select(p => p.Id).ToList(), reports },
            calibration = "org_forecast scored against org_forecast_resolution with each forecast's base rate (Scorecards.Of, every position on the chart)",
            real_dollars = "the proving host composes FakeConnector only: no path reaches money"
        });

        return new ProvingScore(configuration.Name, configuration.Kind == ProvingConfigurationKind.Floor ? "floor" : "seats", homeSha256,
            configuration.TrialBudget, configuration.VerdictBudget, configuration.AiEnvelope, configuration.DeadlineSeconds,
            found, expressible, falseDiscoveries, trapsFallen, trapVerdicts, trials, verdicts.Values.Sum(), metered, refused.Count,
            firstPass, sources, offPathMarkets, offPathFound, offPathReported, calibration);
    }

    /// <summary>The version a promotion row judged, or null.</summary>
    static string? PromotedVersion(Database db, string promotionId) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT version_id FROM strategy_promotion WHERE id=$p", ("$p", promotionId));
        return c.ExecuteScalar() as string;
    });

    /// <summary>
    /// THE RECORD THAT TELLS ANOTHER POSITION OF <paramref name="version"/>, or null: a <c>proposed</c> mandate event whose evidence cites it
    /// (the decider is woken for it) or a lateral note whose refs cite it — each a row the app stamped with its sender.
    /// </summary>
    static string? Reported(Database db, string version) => db.Read(_ =>
    {
        var cited = "%version:" + version + "%";
        using (var c = db.Cmd("SELECT id FROM org_mandate_event WHERE kind=$k AND evidence LIKE $v ORDER BY id LIMIT 1",
                   ("$k", MandateEventKind.Proposed), ("$v", cited)))
            if (c.ExecuteScalar() is long id) return "org_mandate_event:" + id.ToString(CultureInfo.InvariantCulture);
        using (var c = db.Cmd("SELECT id FROM org_lateral WHERE refs LIKE $v OR refs LIKE $j ORDER BY at, id LIMIT 1",
                   ("$v", cited), ("$j", "%\"" + version + "\"%")))
            if (c.ExecuteScalar() is string lat) return "org_lateral:" + lat;
        return null;
    });

    /// <summary>
    /// THE CONFIGURATION'S CALIBRATION: every position on this home's chart, its scorecard as the app computes it at read (<see cref="Scorecards.Of"/>),
    /// summed — never a mean of means.
    /// </summary>
    static ProvingCalibration Calibration(Database db)
    {
        var scorecards = new Scorecards(db);
        var total = ProvingCalibration.None;
        foreach (var position in new OrgStore(db).Positions())
        {
            var card = scorecards.Of(position.Id);
            var t = card.Total;
            total = new ProvingCalibration(total.Scored + t.Scored, total.Hits + card.Scores.Count(s => s.Outcome), total.BrierSum + t.BrierSum,
                total.BaseBrierSum + t.BaseBrierSum, total.LogSum + t.LogSum, total.BaseLogSum + t.BaseLogSum,
                total.Unscored + t.Void + t.Unscored + t.Open + card.Refused.Count);
        }
        return total;
    }
}
