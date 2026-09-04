using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND 10 — THE SIDECAR IS READ ONCE, INTO A SNAPSHOT, AND EVERY ANSWER COMES OUT OF IT.
///
/// Rounds 6–9 closed "unreadable is not empty" seven times, at seven call sites, and every round a
/// reviewer found the neighbouring site: F17, F28, F31, PRIOR 28, F33, F36, F37 on the read paths,
/// R9-1/F34 inside <c>Rotate</c>, R9-5 on the candidate glob. §9.10 says a class with that many
/// instances is fixed structurally, so the structure changed: <c>ReadSidecarSet()</c> is the ONLY
/// code in <see cref="CoidWitness"/> that reads the sidecar filesystem, it has ONE try/catch, and
/// every consumer is handed the snapshot it returns. A consumer cannot conflate "I could not read
/// it" with "there is nothing there" because it never asks the filesystem anything.
///
/// The harnesses below are the round-9 verifier's own (`u14-verify-r9-probes`), lifted into the
/// shipped suite so the mutants they caught go RED here rather than only in a verifier's worktree.
/// The denial instrument is a real <c>chmod 000</c> or a real execute-only directory — no seam
/// decides whether a file is readable.
/// </summary>
public class WitnessSnapshotTests : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "witness-r10-" + Guid.NewGuid().ToString("n")[..8]);

    public WitnessSnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Allow(_dir);
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []) Allow(f);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The denial instrument, and the reason every test using it returns early on Windows: a real
    /// permission bit, not a seam. The Windows-reachable equivalent is an ACL denying
    /// <c>FILE_READ_DATA</c>, which there is no portable way to set from here — the seam-driven tests
    /// beside these cover the same classification on that platform.
    /// </summary>
    static void Deny(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    static void Allow(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception) { }
    }

    static void DenyEnumerationOnly(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(dir, UnixFileMode.UserExecute | UnixFileMode.UserWrite);
    }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    CoidWitness Session() => new(File_);

    static string Gap(int minutesAgo, string claim = "TA-GAP") =>
        $"{DateTimeOffset.UtcNow.AddMinutes(-minutesAgo):O} ERROR coid-witness rewrite did not land. claim={claim}"
        + Environment.NewLine;

    /// <summary>A stale foreign temp: one quarantine WARNING, which is the append that tips the log over.</summary>
    void WriteForeignLeftover(int n)
    {
        var p = File_ + $".tmp-dead-{n:D3}";
        File.WriteAllText(p,
            $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{"client_order_id":"TA-X{{n}}","session_id":"dead","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-5));
    }

    void Seed()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();
    }

    /// <summary>Drives the append that rotates the oversized current log.</summary>
    void RotateNow()
    {
        WriteForeignLeftover(1);
        var w = new CoidWitness(File_);
        Assert.True(w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        w.Dispose();
    }

    /// <summary>Everything the sidecar set holds, whatever the file names are now.</summary>
    string Everything()
    {
        var text = new List<string>();
        foreach (var f in Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*"))
        {
            Allow(f);
            try { text.Add(Path.GetFileName(f) + ": " + File.ReadAllText(f)); } catch (Exception) { }
        }
        return string.Join("\n", text);
    }

    // ================================================================= directive 3, via directive 1

    /// <summary>
    /// THE CONTROL, and it is the state round 9 built: a leftover from a crashed rotation, READABLE,
    /// holding the only unresolved line. It is carried across.
    /// </summary>
    [Fact]
    public void CONTROL_a_readable_leftover_generation_is_carried_across_the_next_rotation()
    {
        Seed();
        File.WriteAllText(Sidecar + ".rotating", Gap(9));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// R9-1 / Codex F34. THE SAME FILE, ONE PERMISSION BIT DIFFERENT. A rotation that cannot read
    /// what it rotates does not rotate: the snapshot is <c>Unreadable</c>, so there is no carry to
    /// compute and nothing is renamed or replaced. The denial is transient — a scanner, a backup
    /// tool, an ACL a later run clears — and the deletion was not.
    /// </summary>
    [Fact]
    public void A_rotation_does_not_destroy_a_generation_it_could_not_read()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        var staging = Sidecar + ".rotating";
        File.WriteAllText(staging, Gap(9));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(staging);
        Assert.NotNull(Session().Trouble);   // this run knows perfectly well it cannot read it

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>The same root cause at the rolled generation, which the other delete destroyed.</summary>
    [Fact]
    public void A_rotation_does_not_destroy_a_rolled_generation_it_could_not_read()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        var rolled = Sidecar + ".1";
        File.WriteAllText(rolled, Gap(9));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(rolled);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// AND THE CARRY PATH DID IT TOO, which is what made it the branch's rule rather than the plain
    /// path's accident: a READABLE generation holding one gap and an UNREADABLE one holding another
    /// restated only the line it could read and then deleted the one it could not.
    /// </summary>
    [Fact]
    public void A_rotation_with_a_carry_does_not_destroy_the_file_it_could_not_read()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        var staging = Sidecar + ".rotating";
        File.WriteAllText(staging, Gap(9, "TA-UNREADABLE-GAP"));
        File.WriteAllText(Sidecar + ".1", Gap(8));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(staging);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-UNREADABLE-GAP", Everything());
    }

    /// <summary>
    /// R9-4. THE CARRY COMES FROM THE FILE SET BEING ROTATED. A writer the lease refused writes its
    /// own sidecar beside the canonical one; when ITS file passes the cap, the rotation must decide
    /// from its own generations. Deciding from the canonical machine's line restated somebody else's
    /// gap into this file and deleted this file's own history to make room for it.
    /// </summary>
    [Fact]
    public void A_refused_writers_rotation_carries_its_own_unresolved_line_not_the_canonical_one()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-OWNED", "SIM", "ES", "Buy", 1m, null));

        // The canonical machine has an open gap of its own, and it is not this writer's.
        File.AppendAllText(Sidecar, Gap(9, "TA-CANONICAL-GAP"));

        var refused = new CoidWitness(File_);
        Assert.False(refused.Submitting("TA-REFUSED", "SIM", "ES", "Buy", 1m, null));
        var mine = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "-*").Single();

        // This writer's own file holds its own unresolved line, and is oversized.
        File.AppendAllText(mine, Gap(7, "TA-MY-OWN-GAP"));
        File.AppendAllText(mine, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.False(refused.Submitting("TA-REFUSED-2", "SIM", "ES", "Buy", 1m, null));

        var carried = Everything();
        Assert.Contains("TA-MY-OWN-GAP", carried);
        Assert.DoesNotContain("carried an unresolved failure across a sidecar rotation: ERROR coid-witness rewrite did not land. claim=TA-CANONICAL-GAP", carried);
        owner.Dispose();
        refused.Dispose();
    }

    /// <summary>
    /// THE CRASH POINTS, BUILT ON DISK AND READ. Rotation is four acts with nothing between them —
    /// write <c>log.new</c>, <c>log.1</c>→<c>log.2</c>, <c>log</c>→<c>log.1</c>,
    /// <c>log.new</c>→<c>log</c> — so there are five instants a machine can die at, counting the one
    /// before it starts. The claim is one sentence: every one of those states is a SUBSET of the
    /// files a reader reads, and the carried line is on the disk before the first act that removes
    /// anything. Each state is constructed here with the real filenames and read by a real witness.
    ///
    /// The states are built rather than raced because the three renames have no observation point
    /// between them in this process; a real <c>SIGKILL</c> landing in them at random is
    /// <c>scratchpad/rotkill10</c>, out of process, and is recorded in the round's build record.
    /// </summary>
    [Theory]
    [InlineData(0, "entry — nothing has run yet")]
    [InlineData(1, "the carry is written, before any generation moves")]
    [InlineData(2, "the oldest generation has been renamed out")]
    [InlineData(3, "the current log has become the rolled generation")]
    [InlineData(4, "the rotation has completed")]
    public void A_gap_is_readable_at_every_instant_of_the_rotation(int crashPoint, string _)
    {
        Seed();
        var restatement =
            $"{DateTimeOffset.UtcNow:O} ERROR coid-witness carried an unresolved failure across a " +
            "sidecar rotation: ERROR coid-witness rewrite did not land. claim=TA-GAP" + Environment.NewLine;

        // The ordinary state: the unresolved line is in the CURRENT log, which is where safety
        // events land and where nothing but a rotation moves them from.
        File.WriteAllText(Sidecar, Gap(9));
        File.WriteAllText(Sidecar + ".1", $"{DateTimeOffset.UtcNow.AddMinutes(-20):O} WARN older" + Environment.NewLine);
        File.WriteAllText(Sidecar + ".2", $"{DateTimeOffset.UtcNow.AddMinutes(-30):O} WARN oldest" + Environment.NewLine);

        if (crashPoint >= 1) File.WriteAllText(Sidecar + ".new", restatement);
        if (crashPoint >= 2) File.Move(Sidecar + ".1", Sidecar + ".2", overwrite: true);
        if (crashPoint >= 3) File.Move(Sidecar, Sidecar + ".1");
        if (crashPoint >= 4) File.Move(Sidecar + ".new", Sidecar);

        var reader = Session();
        Assert.NotNull(reader.Trouble);
        Assert.Contains("io:degraded", reader.Token());
        Assert.Equal(WitnessStanding.Unresolved, CoidWitnessReport.Standing(reader));
        Assert.Contains("TA-GAP", Everything());
        reader.Dispose();
    }

    /// <summary>
    /// AND THE SAME FIVE INSTANTS FOR A GAP THAT WAS ALREADY A GENERATION BACK — round 8's
    /// arrangement, which is the one its own tests built and the one that hid the ordinary case.
    /// The oldest generation is the one the rotation removes, so this is where the carry has to be
    /// right or the line is gone.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_gap_in_the_oldest_generation_is_readable_at_every_instant_of_the_rotation(int crashPoint)
    {
        Seed();
        var restatement =
            $"{DateTimeOffset.UtcNow:O} ERROR coid-witness carried an unresolved failure across a " +
            "sidecar rotation: ERROR coid-witness rewrite did not land. claim=TA-GAP" + Environment.NewLine;

        File.WriteAllText(Sidecar, $"{DateTimeOffset.UtcNow:O} WARN current" + Environment.NewLine);
        File.WriteAllText(Sidecar + ".1", $"{DateTimeOffset.UtcNow.AddMinutes(-20):O} WARN rolled" + Environment.NewLine);
        File.WriteAllText(Sidecar + ".2", Gap(30));

        if (crashPoint >= 1) File.WriteAllText(Sidecar + ".new", restatement);
        if (crashPoint >= 2) File.Move(Sidecar + ".1", Sidecar + ".2", overwrite: true);
        if (crashPoint >= 3) File.Move(Sidecar, Sidecar + ".1");
        if (crashPoint >= 4) File.Move(Sidecar + ".new", Sidecar);

        var reader = Session();
        Assert.NotNull(reader.Trouble);
        Assert.Contains("io:degraded", reader.Token());
        Assert.Contains("TA-GAP", Everything());
        reader.Dispose();
    }

    /// <summary>
    /// AND A GAP THAT WAS CLOSED BEFORE THE ROTATION STAYS CLOSED THROUGH ALL FIVE — the other
    /// direction, without which "always degraded" would satisfy every assertion above.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_closed_gap_stays_closed_at_every_instant_of_the_rotation(int crashPoint)
    {
        Seed();
        File.WriteAllText(Sidecar,
            $"{DateTimeOffset.UtcNow:O} RESOLVED coid-witness committed cleanly after the failures above."
            + Environment.NewLine);
        File.WriteAllText(Sidecar + ".1", Gap(20));

        if (crashPoint >= 1) File.WriteAllText(Sidecar + ".new", "");
        if (crashPoint >= 2) File.Move(Sidecar + ".1", Sidecar + ".2", overwrite: true);
        if (crashPoint >= 3) File.Move(Sidecar, Sidecar + ".1");
        if (crashPoint >= 4) File.Move(Sidecar + ".new", Sidecar);

        var reader = Session();
        Assert.Null(reader.Trouble);
        Assert.True(reader.GapClosed);
        Assert.Equal(WitnessStanding.Historical, CoidWitnessReport.Standing(reader));
        reader.Dispose();
    }

    // ================================================================= directive 1, every consumer

    /// <summary>
    /// Codex F33. A READ THAT FAILS AT ANY POINT LEAVES THE MACHINE DEGRADED, HOWEVER MANY TIMES THE
    /// FILE WAS READ BEFORE IT. Round 9 read the canonical sidecar once per <c>HasNotes</c> pass and
    /// again for the deciding line, and the last read's failure was swallowed by a per-generation
    /// catch — so a canonical file holding an unresolved ERROR came out non-degraded, with
    /// <c>Trouble</c> null and the token saying <c>io:noted</c>, because the probe that saw the ERROR
    /// and the probe that decided the state were different reads of the same file.
    /// </summary>
    [Fact]
    public void An_unresolved_line_degrades_the_machine_however_many_reads_it_takes_to_see_it()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));

        // Denied on the SECOND read of the canonical file and never on the first, so the state under
        // test is precisely "a probe saw the ERROR and the probe that decided the state did not".
        var reads = 0;
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            readSidecar: p => string.Equals(p, Sidecar, StringComparison.Ordinal) && ++reads > 1
                ? throw new UnauthorizedAccessException("denied on re-read")
                : File.ReadAllLines(p));

        Assert.NotNull(witness.Trouble);
        Assert.Contains("degraded", witness.Token());
        witness.Dispose();
    }

    /// <summary>
    /// A SIDECAR FILE IS READ EXACTLY ONCE PER SNAPSHOT, which is what makes the test above a rule
    /// rather than a coincidence: there is no second read for a denial to land in.
    /// </summary>
    [Fact]
    public void Every_sidecar_file_is_read_once_per_snapshot()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));

        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            readSidecar: p => { reads[p] = reads.TryGetValue(p, out var n) ? n + 1 : 1; return File.ReadAllLines(p); });

        _ = witness.Trouble;
        _ = witness.Noted;
        _ = witness.GapClosed;
        _ = witness.SidecarPaths;
        _ = witness.Notes;
        _ = witness.Token();

        Assert.Equal(1, reads[Sidecar]);
        witness.Dispose();
    }

    /// <summary>
    /// Codex F36. <c>SidecarPaths</c> CANNOT DISAGREE WITH THE REST OF THE REPORT, because it is the
    /// same snapshot. An enumeration this run could not perform used to reach the operator as an
    /// empty file list beside a clean standing — the report saying "none recorded" about a directory
    /// it had not managed to look in.
    /// </summary>
    [Fact]
    public void An_enumeration_that_fails_is_reported_rather_than_listed_as_nothing()
    {
        Seed();
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            listSidecars: (_, _) => throw new UnauthorizedAccessException("readdir denied"));

        Assert.NotNull(witness.Trouble);
        Assert.Contains("could not", witness.Trouble!);
        Assert.Empty(witness.SidecarPaths);
        Assert.True(witness.Noted);
        Assert.Contains("degraded", witness.Token());
        Assert.Equal(WitnessStanding.Unresolved, CoidWitnessReport.Standing(witness));
        witness.Dispose();
    }

    /// <summary>
    /// Codex F37. AND THE REPORT SAYS SO IN THE WORDS THE OPERATOR READS. A machine whose sidecar
    /// could not be read was told something "was refused, declined or recovered" — three things,
    /// none of which was observed, about a directory this run never managed to look in.
    /// </summary>
    [Fact]
    public void The_report_for_an_unreadable_sidecar_says_it_could_not_be_read()
    {
        Seed();
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            listSidecars: (_, _) => throw new UnauthorizedAccessException("readdir denied"));

        var standing = CoidWitnessReport.Standing(witness);
        var headline = CoidWitnessReport.Headline(standing, witness.ErrorLogPath!, witness.Notes);
        var explanation = string.Join(" ", CoidWitnessReport.Explanation(standing, witness.Notes));

        Assert.Contains("COULD NOT BE READ", headline);
        Assert.DoesNotContain("refused, declined or recovered", explanation);
        Assert.Contains("could not", explanation);
        Assert.True(CoidWitnessReport.ZeroIsProvisional(standing));
        witness.Dispose();
    }

    /// <summary>
    /// R9-5. THE RECOVERY GLOB COMES OUT OF THE SAME SNAPSHOT. <c>Candidates()</c> answered with an
    /// empty list for an enumeration it was refused — "I could not list the directory" reaching the
    /// recovery path as "there is no stranded rewrite to recover".
    ///
    /// The instrument is real and needs no seam: a directory with the EXECUTE bit and not the READ
    /// bit serves every open by name and refuses <c>readdir</c>, so the committed file still reads
    /// while both globs are denied.
    /// </summary>
    [Fact]
    public void A_refused_candidate_enumeration_does_not_read_as_no_stranded_rewrite()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        DenyEnumerationOnly(_dir);
        try
        {
            var witness = Session();
            // PREMISE: opens by name still work, so this is a denial of the enumeration alone.
            Assert.NotEmpty(File.ReadAllText(File_));
            Assert.NotNull(witness.Trouble);
            Assert.True(witness.Noted);
            witness.Dispose();
        }
        finally
        {
            Allow(_dir);
        }
    }

    /// <summary>Both directions: a genuinely absent sidecar is still clean-empty, and not flagged.</summary>
    [Fact]
    public void A_genuinely_absent_sidecar_is_clean_empty()
    {
        Seed();
        var witness = Session();
        Assert.Null(witness.Trouble);
        Assert.False(witness.Noted);
        Assert.False(witness.GapClosed);
        Assert.Contains("io:ok", witness.Token());
        Assert.Equal(WitnessStanding.Clean, CoidWitnessReport.Standing(witness));
        Assert.False(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(witness)));
        witness.Dispose();
    }

    // ================================================================= R9-2, order independence

    /// <summary>
    /// R9-2. EVERY PUBLIC READING IS THE SAME WHATEVER IS ASKED FIRST. Round 9 gave <c>Noted</c> a
    /// cause that only the recovery discovers and left <c>Noted</c> running the load and not the
    /// recovery, so a fresh instance answered <c>Noted=false</c> while another answered
    /// <c>io:noted</c>, and <c>Standing</c> was right only because C# evaluates arguments left to
    /// right and the argument that runs the recovery happened to come first.
    /// </summary>
    [Fact]
    public void Every_reading_is_the_same_whichever_is_asked_first()
    {
        StrandARecoverableRewrite();

        // Each fresh instance is asked ONE question first; the readings must agree across them.
        Assert.True(new CoidWitness(File_).Noted, "Noted asked first");
        Assert.Contains("io:noted", new CoidWitness(File_).Token());
        Assert.Equal(WitnessNotes.RecoveredRewrite, new CoidWitness(File_).Notes);
        Assert.Equal(WitnessStanding.Noted, CoidWitnessReport.Standing(new CoidWitness(File_)));

        var byParts = new CoidWitness(File_);
        var noted = byParts.Noted;                 // deliberately read BEFORE Trouble
        var gapClosed = byParts.GapClosed;
        var troubled = byParts.Trouble is not null;
        Assert.Equal(WitnessStanding.Noted, CoidWitnessReport.Standing(gapClosed, troubled, noted));
        byParts.Dispose();
    }

    /// <summary>A committed claim plus an uncommitted rewrite of it that carries the acknowledgement.</summary>
    void StrandARecoverableRewrite()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-STRAND", "SIM", "ES", "Buy", 1m, null));
        var session = owner.SessionId;
        var committed = File.ReadAllText(File_);
        owner.Dispose();

        var generation = System.Text.Json.JsonDocument.Parse(committed)
                             .RootElement.GetProperty("generation").GetInt64();
        var temp = File_ + ".tmp-stranded";
        File.WriteAllText(temp,
            $$"""{"version":1,"generation":{{generation + 1}},"predecessor":"{{CoidWitness.Fingerprint(committed)}}","records":[{"client_order_id":"TA-STRAND","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK-STRANDED","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(-5));
    }

    // ================================================================= directive 2, concurrent change

    /// <summary>
    /// A SET THAT IS CHANGING UNDER THE READ IS NOT A SET THAT WAS READ. The listing is taken before
    /// and after; a difference is retried once and a second difference refuses the snapshot. That is
    /// what closes PRIOR 27 — "a marker moved into an already-scanned file during rotation" — without
    /// putting a lock on readers.
    /// </summary>
    [Fact]
    public void A_sidecar_set_that_keeps_changing_under_the_read_is_refused_not_believed()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));

        // Every read grows the file, so the listing after can never match the listing before.
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            readSidecar: p =>
            {
                var lines = File.ReadAllLines(p);
                File.AppendAllText(p, $"{DateTimeOffset.UtcNow:O} WARN moved under the reader" + Environment.NewLine);
                return lines;
            });

        Assert.NotNull(witness.Trouble);
        Assert.Contains("changing", witness.Trouble!);
        Assert.Contains("degraded", witness.Token());
        witness.Dispose();
    }

    /// <summary>Both directions: a set that is NOT changing is read, and answers normally.</summary>
    [Fact]
    public void A_sidecar_set_that_is_not_changing_is_read_normally()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));

        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            readSidecar: File.ReadAllLines);

        Assert.NotNull(witness.Trouble);
        Assert.DoesNotContain("changing", witness.Trouble!);
        Assert.Contains("degraded", witness.Token());
        witness.Dispose();
    }

    /// <summary>
    /// AND THE REAL SHAPE PRIOR 27 NAMED, WITH NO SEAM: a writer appending safety lines and rotating
    /// its sidecar while readers read it. No reader may ever report a clean, unflagged machine while
    /// an unresolved line is on disk — the answer may be "unresolved" or "I could not read it", and
    /// both are honest; "nothing here" is the one that is not.
    /// </summary>
    [Fact]
    public async Task No_reader_reports_a_clean_machine_while_a_writer_is_rotating_under_it()
    {
        Seed();
        var stop = false;
        using var started = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            var w = new CoidWitness(File_);
            for (var i = 0; i < 60 && !stop; i++)
            {
                File.AppendAllText(Sidecar, Gap(0, "TA-LIVE-GAP"));
                File.AppendAllText(Sidecar, new string('x', 40 * 1024) + Environment.NewLine);
                started.Set();
                WriteForeignLeftover(i);
                w.Submitting($"TA-W{i}", "SIM", "ES", "Buy", 1m, null);
            }
            w.Dispose();
        });

        // Not before the first unresolved line is on disk: until then a clean reading is the truth.
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));

        var clean = 0;
        for (var i = 0; i < 400 && !writer.IsCompleted; i++)
        {
            var reader = new CoidWitness(File_);
            var standing = CoidWitnessReport.Standing(reader);
            if (standing == WitnessStanding.Clean) clean++;
            reader.Dispose();
        }
        stop = true;
        await writer.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, clean);
    }
}
