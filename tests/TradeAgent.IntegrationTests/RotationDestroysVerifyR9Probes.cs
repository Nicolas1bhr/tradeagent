using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-9 ADVERSARIAL VERIFY — the rotation's OTHER conflation.
///
/// Round 9 closed "unreadable is not empty" on every READ probe (F31): a sidecar read that fails is
/// UNREADABLE, an enumeration that fails is UNREADABLE, and only the two exceptions that mean
/// "there is nothing at this name" are absence. The class-closure argument then declares the four
/// remaining `File.Exists` calls harmless because they are "all on the WRITE path" and "a wrong
/// answer costs at worst a rotation or a quarantine attempt".
///
/// `Rotate` is on the write path, and what it costs is not a rotation. It decides what to DESTROY
/// from `LastDecidingLine()`, which returns null both when there is nothing unresolved anywhere and
/// when every generation that could have answered THREW. In the second case the `carry is null`
/// branch runs and deletes the generation whose contents this run could not read — which, for a
/// staging file left by a crashed rotation, is the only copy of the gap there is.
///
/// The measurement below is the CONTENT of the sidecar set, not the file names: the plain path
/// re-creates `.1` by moving the current log onto it, so "the file exists" is true and empty of the
/// thing that mattered.
///
/// No seam is used: the denial is a real `chmod 000` on a real file, the same instrument round 6's
/// F17 variants use. Nothing here is a proposed patch.
/// </summary>
public class RotationDestroysVerifyR9Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "witness-r9-" + Guid.NewGuid().ToString("n")[..8]);

    public RotationDestroysVerifyR9Probes() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
            try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    CoidWitness Session() => new(File_);

    static string Gap(int minutesAgo) =>
        $"{DateTimeOffset.UtcNow.AddMinutes(-minutesAgo):O} ERROR coid-witness rewrite did not land. claim=TA-GAP"
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
            try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
            try { text.Add(Path.GetFileName(f) + ": " + File.ReadAllText(f)); } catch (Exception) { }
        }
        return string.Join("\n", text);
    }

    static void Deny(string path) => File.SetUnixFileMode(path, UnixFileMode.None);

    /// <summary>
    /// THE CONTROL, and it is the builder's own state: a staging file left by a crashed rotation,
    /// READABLE, holding the only unresolved line. Round 9 carries it into the current log before
    /// deleting it, which is exactly what commit 62779f0 added.
    /// </summary>
    [Fact]
    public void CONTROL_a_readable_leftover_staging_file_is_carried_across_the_next_rotation()
    {
        Seed();
        File.WriteAllText(Sidecar + ".rotating", Gap(9));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// AND THE SAME FILE, ONE PERMISSION BIT DIFFERENT. `LastDecidingLine()` cannot read it, answers
    /// null, and null is what `Rotate` reads as "there is nothing unresolved to protect" — so the
    /// plain path deletes it. The denial is transient (a scanner, a backup tool, an ACL a later run
    /// clears); the deletion is not.
    /// </summary>
    [Fact]
    public void A_rotation_destroys_a_staging_file_it_could_not_read()
    {
        Seed();
        var staging = Sidecar + ".rotating";
        File.WriteAllText(staging, Gap(9));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(staging);
        Assert.NotNull(Session().Trouble);   // this run knows perfectly well it cannot read it

        RotateNow();

        Assert.Contains("TA-GAP", Everything());
    }

    /// <summary>
    /// THE SECOND INSTANCE OF THE SAME ROOT CAUSE — the rolled generation, deleted by the same
    /// branch for the same reason. Both `File.Delete` calls in it destroy a file this run never read,
    /// and the `File.Move` that follows puts the current log at the deleted name, so the set looks
    /// intact.
    /// </summary>
    [Fact]
    public void A_rotation_destroys_a_rolled_generation_it_could_not_read()
    {
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
    /// AND THE CARRY PATH DOES IT TOO, which is what makes this the branch's rule rather than the
    /// plain path's accident: with a READABLE `.1` holding a gap and an UNREADABLE staging file
    /// holding a different one, the carry restates only the line it could read, then deletes the one
    /// it could not.
    /// </summary>
    [Fact]
    public void A_carrying_rotation_destroys_the_staging_file_it_could_not_read()
    {
        Seed();
        var staging = Sidecar + ".rotating";
        File.WriteAllText(staging, Gap(9).Replace("TA-GAP", "TA-UNREADABLE-GAP"));
        File.WriteAllText(Sidecar + ".1", Gap(8));
        File.WriteAllText(Sidecar, new string('x', 70 * 1024) + Environment.NewLine);
        Deny(staging);
        Assert.NotNull(Session().Trouble);

        RotateNow();

        Assert.Contains("TA-UNREADABLE-GAP", Everything());
    }
}
