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
        // AND IT DID NOT ROTATE AT ALL. The renames themselves destroy nothing, so "the marker
        // survived" is satisfied by a rotation that ran over a set it had not read; the rule is
        // stronger than that, and this is what says so. The log is still the oversized one and no
        // generation was created beside it.
        Assert.False(File.Exists(Sidecar + ".1"), "it rotated over a set it could not read");
        Assert.False(File.Exists(Sidecar + ".new"), "it left a rotation in flight over a set it could not read");
        Assert.True(new FileInfo(Sidecar).Length > 64 * 1024, "the oversized log was rotated anyway");
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
    /// gap into this file — and, before round 10, deleted this file's own history to make room.
    ///
    /// Both writers are given an explicit session id so that the two refused instances share one
    /// sidecar name: that is what lets the second one start from a snapshot of the oversized file
    /// the first one left, which is the ordinary case across two runs of a refused bridge.
    /// </summary>
    [Fact]
    public void A_refused_writers_rotation_carries_its_own_unresolved_line_not_the_canonical_one()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-OWNED", "SIM", "ES", "Buy", 1m, null));

        // The canonical machine has an open gap of its own, and it is not this writer's.
        File.AppendAllText(Sidecar, Gap(9, "TA-CANONICAL-GAP"));

        const string session = "beadfeed0000";
        var refused = new CoidWitness(File_, session);
        Assert.False(refused.Submitting("TA-REFUSED", "SIM", "ES", "Buy", 1m, null));
        refused.Dispose();
        var mine = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "-*").Single();

        // This writer's own file holds its own unresolved line, and is over the cap.
        File.AppendAllText(mine, Gap(7, "TA-MY-OWN-GAP"));
        File.AppendAllText(mine, new string('x', 70 * 1024) + Environment.NewLine);

        // The next run of the same refused bridge: same sidecar name, and its first refusal rotates.
        var again = new CoidWitness(File_, session);
        Assert.False(again.Submitting("TA-REFUSED-2", "SIM", "ES", "Buy", 1m, null));
        again.Dispose();

        var carried = Everything();
        Assert.Contains("rotation: ERROR coid-witness rewrite did not land. claim=TA-MY-OWN-GAP", carried);
        Assert.DoesNotContain("rotation: ERROR coid-witness rewrite did not land. claim=TA-CANONICAL-GAP", carried);
        owner.Dispose();
    }

    /// <summary>
    /// AND A ROTATION THAT CANNOT READ ITS SET DOES NOT RUN AT ALL — measured where refusing makes a
    /// difference. The renames themselves destroy nothing (a generation moves to the next name), so
    /// the one act that removes anything is the oldest generation being replaced. Put the ONLY
    /// unresolved marker there, deny it, and a rotation that proceeds over a set it could not read
    /// destroys the last copy of the gap while the file names still look intact.
    /// </summary>
    [Fact]
    public void A_rotation_over_an_unreadable_set_does_not_destroy_the_oldest_generation()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        var oldest = Sidecar + ".2";
        File.WriteAllText(oldest, Gap(30));
        File.WriteAllText(Sidecar + ".1", $"{DateTimeOffset.UtcNow.AddMinutes(-20):O} WARN rolled" + Environment.NewLine);
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(oldest);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
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
        // There ARE files here. The point is that this run could not establish that, so the list it
        // prints must be empty and the sentence beside it must say why — the two cannot disagree,
        // which they can only fail to do if they come from different reads.
        File.WriteAllText(Sidecar, Gap(9));
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
    // ============================================================ U14a item 1: the deciding line

    /// <summary>
    /// U14a ITEM 1. "I COULD NOT READ IT" IS NOT "THERE IS NO RESOLVED LINE YET".
    ///
    /// <c>LastDecidingLine()</c> collapsed an <c>Unreadable</c> snapshot to null — the same answer a
    /// clean set gives — so the clean-commit path in <c>Settled()</c> read "no marker there yet" and
    /// appended the line that CLOSES a durability gap, over a set nobody had managed to read. The
    /// line is durable and it outranks everything under it, so the next run that CAN read the file
    /// is told the gap was closed: <c>GapClosed</c> comes back true and the standing goes Historical
    /// over a claim that is known not to have reached the disk. The gap below is real and is this
    /// session's own — the rewrite failed — which is what makes the marker a false statement rather
    /// than a redundant one.
    /// </summary>
    [Fact]
    public void A_commit_over_an_unreadable_sidecar_does_not_say_the_gap_was_closed()
    {
        Seed();

        var refuseReplace = true;
        var denyRead = false;
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            replace: (tmp, dest) =>
            {
                if (refuseReplace) throw new IOException("the destination is open");
                File.Move(tmp, dest, overwrite: true);
            },
            readSidecar: p => denyRead
                ? throw new UnauthorizedAccessException("the sidecar could not be read")
                : File.ReadAllLines(p));

        // The gap, and it is real: the claim did not reach the disk and the sidecar says so.
        Assert.False(witness.Submitting("TA-GAP", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("ERROR", Everything());

        // Now nothing can read the set, and a later order commits cleanly on top of it.
        denyRead = true;
        refuseReplace = false;
        Assert.True(witness.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));

        // The standing stays degraded while this run cannot look.
        Assert.NotNull(witness.Trouble);
        Assert.False(witness.GapClosed);
        witness.Dispose();

        // Nothing was written over a set nobody could read...
        Assert.DoesNotContain("RESOLVED", Everything());

        // ...so the run that CAN read it still finds the gap open.
        var next = Session();
        Assert.False(next.GapClosed);
        Assert.NotNull(next.Trouble);
        Assert.Equal(WitnessStanding.Unresolved, CoidWitnessReport.Standing(next));
        next.Dispose();
    }

    /// <summary>
    /// THE CONTROL, and it is what keeps the test above from being satisfied by never writing the
    /// marker at all. Same sequence, same real gap, one difference: the set can be read. A clean
    /// commit then DOES close the gap, and the next run reads it closed.
    /// </summary>
    [Fact]
    public void CONTROL_a_commit_over_a_readable_sidecar_does_say_the_gap_was_closed()
    {
        Seed();

        var refuseReplace = true;
        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            replace: (tmp, dest) =>
            {
                if (refuseReplace) throw new IOException("the destination is open");
                File.Move(tmp, dest, overwrite: true);
            });

        Assert.False(witness.Submitting("TA-GAP", "SIM", "ES", "Buy", 1m, null));
        Assert.Contains("ERROR", Everything());

        refuseReplace = false;
        Assert.True(witness.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        witness.Dispose();

        Assert.Contains("RESOLVED", Everything());

        var next = Session();
        Assert.True(next.GapClosed);
        Assert.Null(next.Trouble);
        next.Dispose();
    }
    // ========================================================== U14a item 2: the renderers' input

    /// <summary>
    /// U14a ITEM 2. THE SNAPSHOT HANDS OVER LINES, NOT NAMES.
    ///
    /// <see cref="CoidWitness.SidecarPaths"/> hands out names, and a name is an invitation to open
    /// the file again: <c>tools/probe</c> reopened each one under its own catch, and the support
    /// package enumerated the directory itself and copied what it found under a catch that
    /// swallowed <c>IOException</c> and <c>UnauthorizedAccessException</c>. So the report an
    /// operator reads and the zip an engineer opens came out of a SECOND look — one that can
    /// disagree with the standing printed beside it, and one whose failure is invisible.
    ///
    /// <c>Sidecars</c> is the snapshot: the lines as they were captured, and a refusal when there
    /// was no capture. This asserts both halves of the value, because a renderer that is handed it
    /// can render nothing the snapshot did not say.
    /// </summary>
    [Fact]
    public void The_snapshot_hands_over_the_lines_it_captured_and_not_the_names_to_reopen()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));
        File.WriteAllText(Sidecar + ".1", Gap(20, "TA-OLDER"));

        var witness = Session();
        var sidecars = witness.Sidecars;

        Assert.Null(sidecars.Unreadable);
        Assert.Equal(2, sidecars.Files.Count);
        Assert.Contains(sidecars.Files, f => f.Path == Sidecar && f.Lines.Any(l => l.Contains("TA-GAP")));
        Assert.Contains(sidecars.Files, f => f.Path == Sidecar + ".1" && f.Lines.Any(l => l.Contains("TA-OLDER")));

        // The names it lists and the lines it hands over are the same set — one reading, not two.
        Assert.Equal(witness.SidecarPaths.OrderBy(x => x, StringComparer.Ordinal),
                     sidecars.Files.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal));
        witness.Dispose();
    }

    /// <summary>
    /// AND A SET THAT COULD NOT BE READ IS A VALUE, NOT AN EMPTY LIST. This is the state that
    /// reached a renderer as "there are no sidecar files" — which reads, to whoever is holding the
    /// machine, as "this bridge has never had a durability failure".
    /// </summary>
    [Fact]
    public void An_unreadable_set_is_handed_over_as_a_refusal_rather_than_as_no_files()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));

        var witness = new CoidWitness(File_, null, CoidWitness.DefaultCap,
            readSidecar: _ => throw new UnauthorizedAccessException("the sidecar could not be read"));
        var sidecars = witness.Sidecars;

        Assert.NotNull(sidecars.Unreadable);
        Assert.Empty(sidecars.Files);
        witness.Dispose();
    }

    // ============================================== U14b item 1, the rotation resumes where it stopped

    /// <summary>The line a rotation carries forward, in the wording <c>Restatement</c> writes.</summary>
    static string Carried(string claim = "TA-GAP") =>
        $"{DateTimeOffset.UtcNow:O} ERROR coid-witness carried an unresolved failure across a " +
        $"sidecar rotation: ERROR coid-witness rewrite did not land. claim={claim}" + Environment.NewLine;

    /// <summary>Crash point 3: the current log rolled aside, everything in <c>.new</c>, no current log.</summary>
    void StoppedAtTheLastAct()
    {
        Seed();
        if (File.Exists(Sidecar)) File.Delete(Sidecar);
        File.WriteAllText(Sidecar + ".1", Gap(9));
        File.WriteAllText(Sidecar + ".new", Carried());
    }

    /// <summary>
    /// U14b ITEM 1. A ROTATION IS NOT AN ALL-OR-NOTHING ACT, AND THE RETRY USED TO PRETEND IT WAS.
    ///
    /// The four acts end with <c>log.new → log</c>. On Windows that last act is a rename onto a name
    /// a scanner or the indexer may be holding — the one failure <c>_replace</c> exists to describe —
    /// and when it is refused the set is left with the current log GONE and <c>log.new</c> holding
    /// the carried unresolved line. Every subsequent append then started from a missing current: it
    /// recreated <c>log</c> as a fresh empty file BESIDE the orphan, and the next rotation moved the
    /// generations along underneath both. The set never came back together, and nothing said so.
    ///
    /// So the completion is finished first, by whoever appends next — including a process that was
    /// not the one that started it, which is what makes this a restart story rather than a retry one.
    /// </summary>
    [Fact]
    public void A_rotation_that_stopped_at_its_last_act_is_completed_before_the_next_append()
    {
        StoppedAtTheLastAct();

        // A fresh instance — a RESTART, not a retry — appends one line.
        WriteForeignLeftover(1);
        var next = new CoidWitness(File_);
        Assert.True(next.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));
        next.Dispose();

        Assert.False(File.Exists(Sidecar + ".new"), "the half-finished rotation was left half finished");
        Assert.True(File.Exists(Sidecar), "there is still no current log");

        var current = File.ReadAllText(Sidecar);
        Assert.Contains("TA-GAP", current);          // the carry came back as the current log
        Assert.True(File.ReadAllLines(Sidecar).Length >= 2, "the new line did not land after it");
        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// THE SAME STATE, PRODUCED BY THE ACT ITSELF RATHER THAN BUILT — the seam refuses the last
    /// rename exactly as Windows would, and then an ordinary append has to put the set back together.
    /// Building the state proves the recovery; producing it proves that the recovery is reachable
    /// from the code path that creates it.
    /// </summary>
    [Fact]
    public void A_last_act_that_is_refused_leaves_a_set_the_next_append_puts_back_together()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9) + new string('x', 70 * 1024) + Environment.NewLine);

        WriteForeignLeftover(1);
        var stopped = new CoidWitness(File_, moveSidecar: (src, dst, overwrite) =>
        {
            if (src.EndsWith(".new", StringComparison.Ordinal))
                throw new IOException("the destination is open in another process");
            File.Move(src, dst, overwrite);
        });
        Assert.True(stopped.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        stopped.Dispose();

        Assert.False(File.Exists(Sidecar), "the last act did not actually fail");
        Assert.True(File.Exists(Sidecar + ".new"));

        WriteForeignLeftover(2);
        var next = new CoidWitness(File_);
        Assert.True(next.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));

        Assert.False(File.Exists(Sidecar + ".new"), "the half-finished rotation was left half finished");
        Assert.True(File.Exists(Sidecar), "there is still no current log");
        Assert.Contains("TA-GAP", Everything());
        next.Dispose();
    }

    /// <summary>
    /// AND WHEN IT CANNOT BE COMPLETED, THE APPEND IS REFUSED AND SAYS WHY — never silently.
    ///
    /// Appending to a current log that does not exist creates one that the completion is going to
    /// overwrite, so the line would be destroyed by the very act that repairs the set. That is the
    /// worst of the three outcomes: the record is gone AND nothing reports it. A refusal that
    /// degrades the machine is the direction to fail in.
    /// </summary>
    [Fact]
    public void A_completion_that_cannot_be_done_refuses_the_append_and_degrades_with_the_reason()
    {
        StoppedAtTheLastAct();

        WriteForeignLeftover(1);
        var stuck = new CoidWitness(File_, moveSidecar: (_, _, _) =>
            throw new IOException("the destination is open in another process"));
        Assert.True(stuck.Submitting("TA-AFTER", "SIM", "ES", "Buy", 1m, null));

        Assert.False(File.Exists(Sidecar),
            "it appended into a current log the completion was going to overwrite");
        Assert.Contains("cannot be moved back", stuck.Trouble!);
        Assert.Contains("io:degraded", stuck.Token());
        Assert.False(stuck.GapClosed);
        stuck.Dispose();
    }

    // ================================================ U14b item 2, the fifth crash point inside act 1

    /// <summary>
    /// U14b ITEM 2. THE CARRY WRITE USED TO EMPTY THE FILE HOLDING THE ONLY COPY OF THE MARKER.
    ///
    /// Act 1 opened <c>log.new</c> with <c>FileMode.Create</c>. <c>log.new</c> is exactly the file a
    /// rotation that stopped at crash point 1 leaves the unresolved line in, so the FIRST thing the
    /// next rotation did was truncate it — and a transient IO error between that open and the write
    /// destroyed the only copy. Worse than losing it: the retry then recomputed the carry from the
    /// emptied file, found nothing unresolved, and rotated a set that reads as healthy. A durability
    /// gap disappears by way of a write that failed.
    ///
    /// The seam here is production's own failure mode — open the destination, truncate it, then die
    /// — and the assertion is taken INSIDE that failure as well as after the retry, because "the
    /// marker is present in every state" is the claim and the failed state is one of them.
    /// </summary>
    [Fact]
    public void A_carry_write_that_fails_after_it_opens_does_not_empty_the_only_copy_of_the_marker()
    {
        Seed();
        // Crash point 1's leftover: `.new` holds the only unresolved line there is, and the current
        // log is over the cap, so the next append rotates.
        File.WriteAllText(Sidecar + ".new", Carried());
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        var attempts = 0;
        var insideTheFailure = "";
        var witness = new CoidWitness(File_, writeSidecar: (path, text) =>
        {
            if (++attempts == 1)
            {
                using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
                insideTheFailure = Everything();
                throw new IOException("the device is full");
            }
            File.WriteAllText(path, text);
        });

        WriteForeignLeftover(1);
        Assert.True(witness.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        witness.Dispose();

        Assert.Equal(2, attempts);                                   // the transient error was retried
        Assert.Contains("TA-GAP", insideTheFailure);                 // and the marker survived it
        Assert.Contains("TA-GAP", Everything());                     // the retry did not restate an emptied file
        // Nothing is asserted about Trouble afterwards: this witness went on to commit cleanly, and a
        // clean commit legitimately writes the RESOLVED marker that closes the gap. What must not
        // happen is the gap disappearing because the file holding it was emptied, which is what the
        // two lines above measure.
    }

    /// <summary>
    /// THE SAME RULE WITHOUT A SEAM ANYWHERE NEAR IT: production's own writer refuses a name that is
    /// occupied rather than truncating it. The name act 1 will choose is squatted here by a file
    /// holding a marker; <c>FileMode.CreateNew</c> refuses it, the rotation is retried under the next
    /// name, and the marker is still on the disk afterwards. With <c>FileMode.Create</c> the squatter
    /// is emptied, filled with an empty carry and renamed away, taking the marker with it.
    /// </summary>
    [Fact]
    public void The_carry_write_refuses_a_name_that_is_already_occupied_rather_than_emptying_it()
    {
        Seed();
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);

        var witness = new CoidWitness(File_);
        var occupied = $"{Sidecar}.new-{Environment.ProcessId}-{witness.SessionId[..8]}-1";
        File.WriteAllText(occupied, Gap(9));

        WriteForeignLeftover(1);
        Assert.True(witness.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        witness.Dispose();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// AND THE TEMP IS THIS WRITER'S OWN FILE, NOT A STRANGER'S. Anything in the set that is not one
    /// of the five generations is a writer the lease turned away — that is how <c>RefusedWriter</c>
    /// is decided — so a rotation temp left behind by an attempt that died must not make this
    /// machine report a second bridge that was never there.
    /// </summary>
    [Fact]
    public void A_rotation_temp_left_behind_is_not_reported_as_a_refused_writer()
    {
        Seed();
        File.WriteAllText(Sidecar, Gap(9));
        var witness = new CoidWitness(File_);
        File.WriteAllText($"{Sidecar}.new-{Environment.ProcessId}-{witness.SessionId[..8]}-7", Carried());

        Assert.False(witness.Notes.HasFlag(WitnessNotes.RefusedWriter));
        witness.Dispose();

        // The control: a genuine refused writer's file, which IS reported.
        File.WriteAllText($"{Sidecar}-9999-deadbeef", Gap(9));
        var reader = Session();
        Assert.True(reader.Notes.HasFlag(WitnessNotes.RefusedWriter));
        reader.Dispose();
    }

    // ============================================== U14b item 5, the cap is in bytes and so is the count

    /// <summary>
    /// U14b ITEM 5. THE SIZE BOUND WAS COUNTED IN UTF-16 CODE UNITS AND COMPARED AGAINST A BYTE CAP.
    ///
    /// <c>MaxErrorLogBytes</c> is 64 KiB of DISK, the file is written as UTF-8, and the running total
    /// was <c>string.Length</c> — one per UTF-16 code unit. Every accented character in an OS error
    /// message weighs two bytes and counted as one; every CJK character in a path weighs three and
    /// counted as one. On a machine whose error strings are not ASCII the log grew to two or three
    /// times its cap before anything rotated it, and the bound is not decorative: it is what keeps an
    /// unrationed stream of safety events finite.
    ///
    /// IT HAS TO BITE PAST THE FIRST APPEND. The count is SEEDED once, from the snapshot, and the
    /// seed is a property of the file rather than of the arithmetic — so one append over a
    /// pre-loaded file measures nothing. What is wrong is the accumulation, and only a run of
    /// appends shows it.
    ///
    /// The wide characters get in through the CLAIM, which is the caller's own string and is the
    /// first thing the failure line says.
    /// </summary>
    [Fact]
    public void The_size_bound_counts_the_bytes_the_sidecar_is_written_in()
    {
        var wide = new string('あ', 300);        // three bytes each in UTF-8, one unit each in Length

        // Every rewrite is refused, so every call writes one unrationed SAFETY line naming the claim.
        var witness = new CoidWitness(File_, replace: (_, _) =>
            throw new InvalidOperationException("the rewrite is refused"));

        for (var i = 0; i < 110; i++)
            Assert.False(witness.Submitting(wide + i, "SIM", "ES", "Buy", 1m, null));
        witness.Dispose();

        // 110 lines of about 1 kB each is ~113 kB against a 64 kB cap. Counted as UTF-16 units the
        // same 110 lines come to ~48 kB and nothing rotates at all.
        var current = new FileInfo(Sidecar).Length;
        Assert.True(File.Exists(Sidecar + ".1"),
            $"the log passed the cap without rotating: {current} bytes in the current log");
        Assert.True(current <= 64 * 1024, $"the current log is {current} bytes, past the 65536 byte cap");
    }
}
