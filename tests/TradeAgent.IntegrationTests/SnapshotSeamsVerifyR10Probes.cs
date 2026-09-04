using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-10 VERIFIER, leg [2]. Target 1 (one site, one failure mode), target 2 (concurrent change),
/// target 3 (the crash points), target 8 (the reversed theory). Probes, not fixes.
/// </summary>
public class SnapshotSeamsVerifyR10Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "r10v-" + Guid.NewGuid().ToString("n")[..8]);

    public SnapshotSeamsVerifyR10Probes() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Allow(_dir);
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []) Allow(f);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    CoidWitness Session() => new(File_);

    static string GapLine(string claim = "TA-GAP") =>
        $"{DateTimeOffset.UtcNow.AddMinutes(-5):O} ERROR coid-witness rewrite did not land. claim={claim}"
        + Environment.NewLine;

    void Seed()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();
    }

    void WriteForeignLeftover(int n)
    {
        var p = File_ + $".tmp-dead-{n:D3}";
        File.WriteAllText(p,
            $$"""{"version":1,"generation":99,"predecessor":"deadbeefdeadbeef","records":[{"client_order_id":"TA-X{{n}}","session_id":"dead","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-5));
    }

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

    // ============================================================ TARGET 1 — every step of the one read

    /// <summary>
    /// THE STAT STEP, which the builder's theory does not drive. `Listing` calls `new FileInfo(name)`
    /// on every name the enumeration returned; that constructor throws on a name the OS will not
    /// accept. A listing that returns such a name is what a filesystem with a name this build cannot
    /// represent looks like, and the answer must be Unreadable, not "there is nothing there".
    /// </summary>
    [Fact]
    public void A_name_this_build_cannot_stat_is_unreadable_rather_than_absent()
    {
        Seed();
        File.WriteAllText(Sidecar, GapLine());

        var reader = new CoidWitness(File_, listSidecars: (dir, glob) =>
            glob.StartsWith(CoidWitness.ErrorLogName, StringComparison.Ordinal)
                ? [Path.Combine(dir, "coid-witness.errors.log\0bad")]
                : Directory.GetFileSystemEntries(dir, glob));

        Assert.NotNull(reader.Trouble);
        Assert.Contains("could not be read", reader.Trouble);
        Assert.Contains("io:degraded", reader.Token());
        Assert.True(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(reader)));
    }

    /// <summary>
    /// THE VANISH STEP. A name the listing returned and the read cannot open — a file deleted between
    /// the two — must be Unreadable. It is the direction that matters: a set that moved, not a set
    /// that was empty.
    /// </summary>
    [Fact]
    public void A_name_that_vanished_between_the_listing_and_the_read_is_unreadable()
    {
        Seed();
        File.WriteAllText(Sidecar, GapLine());

        var reader = new CoidWitness(File_, listSidecars: (dir, glob) =>
            glob.StartsWith(CoidWitness.ErrorLogName, StringComparison.Ordinal)
                ? [.. Directory.GetFileSystemEntries(dir, glob), Path.Combine(dir, CoidWitness.ErrorLogName + ".ghost")]
                : Directory.GetFileSystemEntries(dir, glob));

        Assert.NotNull(reader.Trouble);
        Assert.Contains("could not be read", reader.Trouble);
        Assert.Contains("io:degraded", reader.Token());
    }

    /// <summary>
    /// THE CANDIDATE ENUMERATION, which runs AFTER the stability check and is the second call to the
    /// listing seam. A denial there must be Unreadable too (R9-5's class).
    /// </summary>
    [Fact]
    public void A_denied_candidate_glob_is_unreadable_rather_than_no_stranded_rewrite()
    {
        Seed();
        File.WriteAllText(Sidecar, GapLine());

        var reader = new CoidWitness(File_, listSidecars: (dir, glob) =>
            glob.Contains(".tmp", StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("denied")
                : Directory.GetFileSystemEntries(dir, glob));

        Assert.NotNull(reader.Trouble);
        Assert.Contains("could not be read", reader.Trouble);
        Assert.Contains("io:degraded", reader.Token());
    }

    /// <summary>
    /// BOTH DIRECTIONS. A clean-empty directory reads clean-empty ONLY after a successful
    /// enumeration — and it does read clean-empty, which is the half a fail-closed change breaks.
    /// </summary>
    [Fact]
    public void CONTROL_a_directory_that_enumerated_cleanly_and_held_nothing_is_clean()
    {
        Seed();
        Assert.False(File.Exists(Sidecar));

        var reader = Session();
        Assert.Null(reader.Trouble);
        Assert.False(reader.Noted);
        Assert.Contains("io:ok", reader.Token());
        Assert.Equal(WitnessStanding.Clean, CoidWitnessReport.Standing(reader));
        Assert.False(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(reader)));
    }

    // ============================================================ TARGET 8 — the reversed theory

    /// <summary>
    /// THE F25 BOUNDARY, THE HALF THAT MUST STILL HOLD: a refused writer's CONTENT still only notes.
    /// The reversal is about UNREADABILITY, and if it had leaked into content this is what would say
    /// so.
    /// </summary>
    [Fact]
    public void A_refused_writers_content_still_notes_without_degrading_this_machine()
    {
        Seed();
        File.WriteAllText(Path.Combine(_dir, CoidWitness.ErrorLogName + "-99999-deadbeef"), GapLine("TA-THEIRS"));

        var reader = Session();
        Assert.True(reader.Noted);
        Assert.Null(reader.Trouble);
        Assert.DoesNotContain("io:degraded", reader.Token());
        Assert.True(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(reader)));
    }

    /// <summary>
    /// AND THE HALF THE REVERSAL MOVED, MEASURED RATHER THAN ARGUED: a file this run cannot read
    /// that belongs to SOMEBODY ELSE now degrades THIS machine and drops SupportsClientOrderId.
    /// Whether that is the right direction is a judgement; that it is reachable by any process that
    /// can write in the bridge directory is a fact, and this is the fact.
    /// </summary>
    [Fact]
    public void A_second_writers_unreadable_file_degrades_this_machine()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        var theirs = Path.Combine(_dir, CoidWitness.ErrorLogName + "-99999-deadbeef");
        File.WriteAllText(theirs, GapLine("TA-THEIRS"));
        File.SetUnixFileMode(theirs, UnixFileMode.None);

        var reader = Session();
        Assert.NotNull(reader.Trouble);                 // SupportsClientOrderId := false on the adapter
        Assert.Contains("io:degraded", reader.Token());
    }

    // ============================================================ TARGET 2 — the listing that cannot tell

    /// <summary>
    /// THE STABILITY CHECK IS NAMES + LENGTHS + MTIMES, so a change that preserves all three is
    /// invisible to it. Constructed with a writer that restores the modification time — what a
    /// backup agent, an `rsync --times` or any restore does. Reported as what it is: this product's
    /// own writer never does it, and the direction it fails in here is closed, not open.
    /// </summary>
    [Fact]
    public void A_same_length_rewrite_that_restores_the_mtime_is_invisible_to_the_stability_check()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();

        // Two lines of EXACTLY equal length that say opposite things: one opens a durability gap,
        // the other closes it. The timestamps are the same width by construction (`O`).
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var resolvedLine = $"{stamp:O} RESOLVED coid-witness committed cleanly after the failures above.";
        var head = $"{stamp:O} ERROR coid-witness rewrite did not land. claim=TA-";
        var errorLine = head + new string('A', resolvedLine.Length - head.Length);
        Assert.Equal(resolvedLine.Length, errorLine.Length);

        File.WriteAllText(Sidecar, errorLine + Environment.NewLine);
        var mtime = File.GetLastWriteTimeUtc(Sidecar);

        var swapped = false;
        var reader = new CoidWitness(File_, readSidecar: path =>
        {
            var lines = File.ReadAllLines(path);
            if (!swapped && string.Equals(path, Sidecar, StringComparison.Ordinal))
            {
                swapped = true;
                File.WriteAllText(Sidecar, resolvedLine + Environment.NewLine);
                File.SetLastWriteTimeUtc(Sidecar, mtime);
            }
            return lines;
        });

        var whatItSaw = reader.Trouble;
        Assert.True(swapped);
        Assert.Equal(new FileInfo(Sidecar).Length, errorLine.Length + Environment.NewLine.Length);

        // The snapshot was ACCEPTED — no "changing", no "could not be read": the two listings matched
        // although the file did not.
        Assert.NotNull(whatItSaw);
        Assert.DoesNotContain("could not be read", whatItSaw);

        // And it disagrees with the disk, which a second reader reports correctly.
        Assert.Null(new CoidWitness(File_).Trouble);
    }

    /// <summary>
    /// AND THE MEASUREMENT THE ONE ABOVE RESTS ON: what modification-time resolution this filesystem
    /// actually reports. "Within the clock's granularity" is only a hole if the granularity is
    /// coarse; this says which it is on the machine the claim was made on.
    /// </summary>
    [Fact]
    public void MEASURE_the_mtime_resolution_this_filesystem_reports()
    {
        var p = Path.Combine(_dir, "granularity.txt");
        var distinct = new HashSet<long>();
        for (var i = 0; i < 200; i++)
        {
            File.WriteAllText(p, new string('x', 10));
            distinct.Add(File.GetLastWriteTimeUtc(p).Ticks);
        }
        Assert.True(distinct.Count == 200,
            $"MTIME RESOLUTION: {distinct.Count} distinct stamps over 200 same-length rewrites "
            + $"(200 = the stamp moves on every write)");
    }

    // ============================================================ TARGET 3 — the carry write truncates first

    /// <summary>
    /// THE ROTATION'S FIRST ACT DESTROYS BEFORE IT REPLACES, and the shipped test cannot see it
    /// because its seam never touches the filesystem.
    ///
    /// Production's carry write is `WriteDurably(path, text, FileMode.Create)` — the open TRUNCATES
    /// an existing `log.new` and only then are the bytes written. When `log.new` is the only file
    /// holding the unresolved line — which is exactly the state the builder's own crash-point row 3
    /// leaves behind — a write that truncates and then fails leaves the marker in no file at all.
    /// The shipped `A_restatement_that_never_lands_leaves_the_gap_where_a_reader_still_finds_it`
    /// asserts the file set at that instant is `[coid-witness.errors.log]`, which is true of its seam
    /// and not of `FileMode.Create`.
    /// </summary>
    [Fact]
    public void A_carry_write_that_truncates_and_then_fails_loses_the_only_copy()
    {
        Seed();
        // The state crash-point 3 leaves: the only unresolved line is in the pending generation.
        File.WriteAllText(Sidecar + ".new", GapLine());
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);
        Assert.Contains("TA-GAP", Everything());

        WriteForeignLeftover(1);
        var w = new CoidWitness(File_, writeSidecar: (path, _) =>
        {
            using (var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
            throw new IOException("no space left on device");
        });
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        // THE HARM, not just the missing string: a fresh reader now calls the machine healthy, and
        // `AtasStrategyAdapter.cs:655` reads `Trouble is null` as "rule 1 is proven".
        var next = new CoidWitness(File_);
        var everything = Everything();
        Assert.True(next.Trouble is not null && everything.Contains("TA-GAP"),
            $"Trouble={next.Trouble ?? "<null>"} Token={next.Token()} "
            + $"Standing={CoidWitnessReport.Standing(next)} files=["
            + string.Join(", ", Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName))
            + $"] TA-GAP-on-disk={everything.Contains("TA-GAP")}");
    }

    /// <summary>
    /// AND THE SAME LOSS FROM ONE TRANSIENT ERROR, WITH NO SECOND CRASH AT ALL. `AppendToErrorLog`
    /// retries, and the retry takes a FRESH snapshot — in which `log.new` is now the empty file the
    /// first attempt left. The carry is recomputed as "nothing to carry", the rotation completes
    /// normally, and the machine reads healthy.
    /// </summary>
    [Fact]
    public void One_failed_write_during_a_rotation_loses_the_marker_and_the_retry_completes_over_it()
    {
        Seed();
        File.WriteAllText(Sidecar + ".new", GapLine());
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        WriteForeignLeftover(1);
        var attempts = 0;
        var w = new CoidWitness(File_, writeSidecar: (path, text) =>
        {
            using (var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                if (++attempts == 1) throw new IOException("no space left on device");
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
                s.Flush(flushToDisk: true);
            }
        });
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        var next = new CoidWitness(File_);
        var everything = Everything();
        Assert.True(next.Trouble is not null && everything.Contains("TA-GAP"),
            $"attempts={attempts} Trouble={next.Trouble ?? "<null>"} Token={next.Token()} "
            + $"Standing={CoidWitnessReport.Standing(next)} files=["
            + string.Join(", ", Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName))
            + $"] TA-GAP-on-disk={everything.Contains("TA-GAP")}");
    }

    /// <summary>THE CONTROL: the shipped test's own seam, which never opens the file. The line survives.</summary>
    [Fact]
    public void CONTROL_a_carry_write_that_never_opens_the_file_keeps_the_only_copy()
    {
        Seed();
        File.WriteAllText(Sidecar + ".new", GapLine());
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        WriteForeignLeftover(1);
        var w = new CoidWitness(File_, writeSidecar: (_, _) => throw new IOException("no space left on device"));
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// AND THE PREMISE, MEASURED RATHER THAN READ: `FileMode.Create` empties an existing file at the
    /// open, before any byte of the replacement is written.
    /// </summary>
    [Fact]
    public void PREMISE_FileMode_Create_empties_the_file_at_the_open()
    {
        var p = Path.Combine(_dir, "premise.txt");
        File.WriteAllText(p, "the only copy" + Environment.NewLine);
        using (var s = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read))
            Assert.Equal(0, new FileInfo(p).Length);
        Assert.Equal("", File.ReadAllText(p));
    }

    /// <summary>
    /// THE CONTROL FOR THE TWO ABOVE: with a REAL carry write and no seam at all, the crash-point-3
    /// state — the only unresolved line in the pending generation — is carried across the next
    /// rotation. So the loss above is the failed write and nothing else.
    /// </summary>
    [Fact]
    public void CONTROL_a_real_rotation_carries_the_only_copy_out_of_the_pending_generation()
    {
        Seed();
        File.WriteAllText(Sidecar + ".new", GapLine());
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        WriteForeignLeftover(1);
        var w = new CoidWitness(File_);
        Assert.True(w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null));
        w.Dispose();

        // The rewrite SUCCEEDS here, so the gap is legitimately closed by the RESOLVED marker that
        // follows — which is why this asserts the LINE survived the rotation and not the standing.
        Assert.Contains("TA-GAP", Everything());
        Assert.Contains("coid-witness carried an unresolved failure across a sidecar rotation",
                        Everything());
    }

    /// <summary>
    /// TARGET 3 — A ROTATION THAT CANNOT READ ITS SET REFUSES TO ROTATE AND THE APPEND STILL LANDS.
    /// Both halves: nothing is renamed, and the safety line is not lost to the refusal.
    /// </summary>
    [Fact]
    public void A_rotation_that_cannot_read_refuses_and_the_append_still_lands()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed();
        File.WriteAllText(Sidecar, GapLine() + new string('x', 70 * 1024) + Environment.NewLine);
        var before = new FileInfo(Sidecar).Length;

        WriteForeignLeftover(1);
        var w = new CoidWitness(File_, listSidecars: (dir, glob) =>
            glob.StartsWith(CoidWitness.ErrorLogName, StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("denied")
                : Directory.GetFileSystemEntries(dir, glob));
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        var files = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["coid-witness.errors.log"], files);            // nothing was renamed
        Assert.True(new FileInfo(Sidecar).Length > before,           // the append landed anyway
            $"the append was lost to the refusal: {before} -> {new FileInfo(Sidecar).Length}");
        Assert.Contains("TA-GAP", File.ReadAllText(Sidecar));
    }

    /// <summary>
    /// TARGET 3 — NO STAGING FILE IS EVER CREATED, and the pending name IS inside the reader's glob.
    /// Driven by a real rotation, asserted on the names that exist afterwards.
    /// </summary>
    [Fact]
    public void A_real_rotation_creates_no_staging_file_and_its_temp_is_inside_the_readers_glob()
    {
        Seed();
        File.WriteAllText(Sidecar, GapLine() + new string('x', 70 * 1024) + Environment.NewLine);
        WriteForeignLeftover(1);

        string[] duringRotation = [];
        var w = new CoidWitness(File_, writeSidecar: (path, text) =>
        {
            using var st = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            st.Write(bytes, 0, bytes.Length);
            st.Flush(flushToDisk: true);
            duringRotation = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "*")
                                      .Select(Path.GetFileName).Order().ToArray()!;
        });
        w.Submitting("TA-NEXT", "SIM", "ES", "Buy", 1m, null);
        w.Dispose();

        Assert.Contains("coid-witness.errors.log.new", duringRotation);
        Assert.DoesNotContain("coid-witness.errors.log.rotating", duringRotation);
        Assert.Empty(Directory.GetFiles(_dir, CoidWitness.ErrorLogName + ".rotating*"));
    }
}
