using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-9 ADVERSARIAL VERIFY — target 2. Every probe on the sidecar path, DENIED FOR REAL.
///
/// The builder added `_readSidecar` / `_listSidecars` as seams "for the identical stated reason" the
/// `_open` seam exists: that an ACL denying attributes or refusing an enumeration "cannot be provoked
/// on this machine without also breaking the committed read in the same directory". On a POSIX
/// filesystem it can: a directory with the EXECUTE bit and not the READ bit lets every known name be
/// opened and refuses `readdir`. So the enumeration denial is driven here against the real
/// `Directory.GetFiles`, with the committed read still working, and the seam is not used at all.
///
/// Both directions throughout: a genuinely ABSENT sidecar must still read clean-empty.
/// </summary>
public class ProbeDenialsVerifyR9Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "deny-r9-" + Guid.NewGuid().ToString("n")[..8]);

    public ProbeDenialsVerifyR9Probes() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception) { }
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
            try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    CoidWitness Session() => new(File_);

    void Seed()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();
    }

    static void Deny(string path) => File.SetUnixFileMode(path, UnixFileMode.None);
    static void TraverseOnly(string dir) => File.SetUnixFileMode(dir, UnixFileMode.UserExecute);
    static void Restore(string dir) =>
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>
    /// THE PREMISE OF THE STATE, ASSERTED RATHER THAN ASSUMED: an execute-only directory refuses the
    /// enumeration and serves every open. If a future macOS stops behaving this way this test says so
    /// rather than letting the two below pass vacuously.
    /// </summary>
    [Fact]
    public void PREMISE_an_execute_only_directory_refuses_readdir_and_still_serves_opens()
    {
        Seed();
        File.WriteAllText(Sidecar, "hello" + Environment.NewLine);
        TraverseOnly(_dir);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(_dir, "*"));
            Assert.Equal(["hello"], File.ReadAllLines(Sidecar));
            Assert.True(File.Exists(Sidecar));
        }
        finally { Restore(_dir); }
    }

    /// <summary>
    /// ENUMERATE DENIED, for real. `SidecarSet` cannot list the per-writer files, so the zero it would
    /// report is a zero this run cannot stand behind: `Noted` true and the zero flagged provisional.
    /// The F25 boundary says it must NOT drop the machine to degraded — somebody else's directory
    /// permissions are not this machine's durability problem — so that is asserted too.
    /// </summary>
    [Fact]
    public void An_enumeration_this_run_cannot_perform_flags_the_zero_and_does_not_read_as_empty()
    {
        Seed();
        TraverseOnly(_dir);
        try
        {
            var w = Session();
            Assert.True(w.Noted, "a directory that would not list its contents reported nothing written down");
            Assert.True(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(w)), "the zero was reported as a fact");
            Assert.Contains("io:noted", w.Token());
        }
        finally { Restore(_dir); }
    }

    /// <summary>READ DENIED, for real: the canonical sidecar exists and cannot be opened.</summary>
    [Fact]
    public void A_canonical_generation_that_cannot_be_read_is_not_one_with_nothing_in_it()
    {
        Seed();
        File.WriteAllText(Sidecar, $"{DateTimeOffset.UtcNow:O} WARNING something" + Environment.NewLine);
        Deny(Sidecar);
        try
        {
            var w = Session();
            Assert.True(w.Noted);
            Assert.NotNull(w.Trouble);
            Assert.Contains("io:degraded", w.Token());
            Assert.False(w.GapClosed);
        }
        finally { File.SetUnixFileMode(Sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// <summary>
    /// AND A GENERATION WHOSE EXISTENCE THIS RUN CANNOT ESTABLISH — the `.1` name, denied. Round 9's
    /// stated behaviour change: `Noted` by NAME rather than by whether the enumeration listed it.
    /// </summary>
    [Fact]
    public void A_rolled_generation_that_cannot_be_read_is_noted_by_name()
    {
        Seed();
        File.WriteAllText(Sidecar + ".1", "x" + Environment.NewLine);
        Deny(Sidecar + ".1");
        try
        {
            var w = Session();
            Assert.True(w.Noted);
            Assert.NotNull(w.Trouble);
            Assert.Contains("io:degraded", w.Token());
        }
        finally { File.SetUnixFileMode(Sidecar + ".1", UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// <summary>
    /// THE STAGING NAME IS A GENERATION NOW, so the same rule has to hold for it — this is the name
    /// the round-9 rotation work added to the scanned set.
    /// </summary>
    [Fact]
    public void A_staging_generation_that_cannot_be_read_is_noted_by_name()
    {
        Seed();
        File.WriteAllText(Sidecar + ".rotating", "x" + Environment.NewLine);
        Deny(Sidecar + ".rotating");
        try
        {
            var w = Session();
            Assert.True(w.Noted);
            Assert.NotNull(w.Trouble);
            Assert.Contains("io:degraded", w.Token());
        }
        finally { File.SetUnixFileMode(Sidecar + ".rotating", UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// <summary>
    /// A DIRECTORY AT THE SIDECAR'S NAME — `File.Exists` answers false about it while it is manifestly
    /// not nothing. The builder's own F31 state, re-driven here independently.
    /// </summary>
    [Fact]
    public void A_directory_at_the_sidecars_name_is_unreadable_rather_than_absent()
    {
        Seed();
        Directory.CreateDirectory(Sidecar);
        var w = Session();
        Assert.True(w.Noted);
        Assert.NotNull(w.Trouble);
        Assert.Contains("io:degraded", w.Token());
    }

    /// <summary>
    /// THE OTHER DIRECTION, and it is the half that a fail-closed reading breaks: a machine with no
    /// sidecar at all is CLEAN, not degraded. Every denial above has to be distinguishable from this.
    /// </summary>
    [Fact]
    public void A_genuinely_absent_sidecar_reads_as_clean_empty()
    {
        Seed();
        Assert.False(File.Exists(Sidecar));
        var w = Session();
        Assert.Null(w.Trouble);
        Assert.False(w.Noted);
        Assert.False(CoidWitnessReport.ZeroIsProvisional(CoidWitnessReport.Standing(w)));
        Assert.Contains("io:ok", w.Token());
        Assert.Equal(WitnessStanding.Clean, CoidWitnessReport.Standing(w));
    }

    /// <summary>
    /// AND THE PROBE THE ENUMERATION DOES NOT COVER: the CANDIDATE glob (`CoidWitness.cs:1519-1533`)
    /// is `Directory.GetFiles(dir, "<witness>.tmp*")` under a catch that returns an EMPTY LIST for
    /// every exception — the same conflation `SidecarSet` was fixed for, one glob over, on the
    /// recovery path. It is not on the builder's enumeration ("every filesystem call on the sidecar
    /// path"), which is true as far as it goes: it is on the candidate path, in the same directory,
    /// and a denial there means "there is no stranded rewrite to recover".
    ///
    /// This test records what actually happens rather than asserting a defect: because both globs run
    /// against the SAME directory, an execute-only directory denies both, and `SidecarSet`'s flag is
    /// what saves the reading. The finding is therefore that the candidate glob is UNCOVERED, not
    /// that it is currently observable — see R9-3.
    /// </summary>
    [Fact]
    public void A_denied_candidate_enumeration_is_covered_only_by_its_sibling_globs_flag()
    {
        Seed();
        // A stranded rewrite that a working candidate scan would adopt and report.
        File.WriteAllText(File_ + ".tmp-live", File.ReadAllText(File_));
        TraverseOnly(_dir);
        try
        {
            var w = Session();
            // Noted comes from the SIDECAR enumeration's flag, not from the candidate scan.
            Assert.True(w.Noted, "neither glob flagged a directory that would not list its contents");
        }
        finally { Restore(_dir); }
    }
}
