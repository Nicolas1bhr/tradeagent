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
public sealed record ProvingScore(
    string Configuration, string Kind, string HomeSha256,
    int TrialBudget, int VerdictBudget, decimal AiEnvelope, int DeadlineSeconds,
    int PlantedFound, int PlantedExpressible, int FalseDiscoveries, int TrapsFallenFor, int TrapVerdicts,
    int TrialsSpent, int VerdictsSpent, decimal? AiDollarsMetered, int EnvelopeRefusals, double? FirstPassSeconds,
    string Sources)
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
        + $"{(FirstPassSeconds is { } s ? $"{s:0.0} s" : "none")}; home {HomeSha256[..Math.Min(12, HomeSha256.Length)]}");
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
    public const int Schema = 1;

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
            if (have < 1)
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
                      sources             TEXT NOT NULL,
                      sentence            TEXT NOT NULL,
                      label               TEXT NOT NULL CHECK (label = '{ProvingScore.Label}'),
                      PRIMARY KEY (run_id, ordinal));
                    INSERT INTO proving_meta(key, value) VALUES('schema', '1') ON CONFLICT(key) DO UPDATE SET value='1';
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
              verdicts_spent, ai_dollars_metered, real_dollars, envelope_refusals, first_pass_seconds, sources, sentence, label)
            VALUES($r, $o, $c, $k, $h, $tb, $vb, $e, $d, $pf, $pe, $fd, $tf, $tv, $ts, $vs, $ai, '0', $er, $fp, $src, $s, $l)
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
                   ai_dollars_metered, envelope_refusals, first_pass_seconds, sources
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
                r.IsDBNull(16) ? null : r.GetDouble(16), r.GetString(17)));
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

        var found = byCampaign.Where(kv => kv.Value.PlantedOnTheSignal > 0 && passes.Any(p => p.Campaign == kv.Key)).Select(kv => kv.Value.Instrument).Distinct().Count();
        var expressible = truths.Count(t => t.PlantedOnTheSignal > 0);
        var falseDiscoveries = passes.Count(p => byCampaign[p.Campaign].Noise);
        var trapsFallen = passes.Count(p => byCampaign[p.Campaign].Traps > 0);

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
            real_dollars = "the proving host composes FakeConnector only: no path reaches money"
        });

        return new ProvingScore(configuration.Name, configuration.Kind == ProvingConfigurationKind.Floor ? "floor" : "seats", homeSha256,
            configuration.TrialBudget, configuration.VerdictBudget, configuration.AiEnvelope, configuration.DeadlineSeconds,
            found, expressible, falseDiscoveries, trapsFallen, trapVerdicts, trials, verdicts.Values.Sum(), metered, refused.Count,
            firstPass, sources);
    }
}
