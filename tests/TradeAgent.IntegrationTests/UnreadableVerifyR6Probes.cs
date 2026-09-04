using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// U14 round-6 probes, target 1: "unreadable is not absent" beyond the injected opener. Absent is
/// exactly FileNotFound; every other way of not having the bytes must refuse every write and preserve
/// what is on disk. These use REAL failures where one can be made, and the seam only where it cannot.
/// </summary>
public class UnreadableVerifyR6Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "wit-r7-" + Guid.NewGuid().ToString("n")[..8]);
    public UnreadableVerifyR6Probes() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir)) try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception) { }
    }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string[] CommittedIds() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_)).RootElement.GetProperty("records")
            .EnumerateArray().Select(r => r.GetProperty("client_order_id").GetString()!).ToArray();

    /// <summary>Commits one acknowledged claim and returns the exact bytes on disk.</summary>
    string Seed()
    {
        using var w = new CoidWitness(File_);
        Assert.True(w.Submitting("TA-A", "SIM", "ES", "Buy", 1m, null));
        w.Identified("TA-A", "BRK-A");
        return File.ReadAllText(File_);
    }

    /// <summary>
    /// A REAL UnauthorizedAccessException on the committed path — chmod 000, no injection at all.
    /// The bytes are there and this build may not have them, which is the F17 class.
    /// </summary>
    [Fact]
    public void A_committed_file_this_build_may_not_open_is_unreadable_and_is_not_written_over()
    {
        var bytes = Seed();
        File.SetUnixFileMode(File_, UnixFileMode.None);
        // If the platform lets the owner read it anyway there is nothing to test; say so rather than pass.
        try { using var probe = File.OpenRead(File_); Assert.Fail("chmod 000 did not deny the owner; this probe cannot run here"); }
        catch (UnauthorizedAccessException) { }

        using var w = new CoidWitness(File_);
        Assert.True(w.Unreadable);
        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.Contains("could not be read", w.Trouble);

        File.SetUnixFileMode(File_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(bytes, File.ReadAllText(File_));            // byte-identical
        Assert.Equal(["TA-A"], CommittedIds());
    }

    /// <summary>A DIRECTORY at the witness path. Not absent, not an envelope, and not overwritable.</summary>
    [Fact]
    public void A_directory_at_the_witness_path_is_unreadable_and_no_write_replaces_it()
    {
        Directory.CreateDirectory(File_);
        Directory.CreateDirectory(Path.Combine(File_, "marker"));

        using var w = new CoidWitness(File_);
        Assert.True(w.Unreadable);
        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.True(Directory.Exists(File_));
        Assert.True(Directory.Exists(Path.Combine(File_, "marker")));
    }

    /// <summary>
    /// A PARTIAL READ that never throws: the stream ends early, so the text is short but arrives
    /// cleanly. The I/O layer reports success; only the parse can catch it — which is the seam
    /// between the two halves of the one predicate, driven from the half that looks healthy.
    /// </summary>
    [Fact]
    public void A_committed_file_that_reads_short_without_throwing_is_unreadable_and_is_not_written_over()
    {
        var bytes = Seed();
        var half = bytes.Length / 2;

        using var w = new CoidWitness(File_, null, CoidWitness.DefaultCap, null,
            path => string.Equals(path, File_, StringComparison.Ordinal)
                ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bytes[..half]))
                : File.OpenRead(path));

        Assert.True(w.Unreadable);
        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.Equal(bytes, File.ReadAllText(File_));
        Assert.Equal(["TA-A"], CommittedIds());
    }

    /// <summary>
    /// A stream that throws PART WAY THROUGH the read, every time — the I/O half, with the bytes
    /// partly delivered so a build that trusted what it already had would proceed.
    /// </summary>
    [Fact]
    public void A_committed_file_whose_read_fails_part_way_is_unreadable_and_is_not_written_over()
    {
        var bytes = Seed();

        using var w = new CoidWitness(File_, null, CoidWitness.DefaultCap, null,
            path => string.Equals(path, File_, StringComparison.Ordinal)
                ? new ThrowsPartWay(System.Text.Encoding.UTF8.GetBytes(bytes), bytes.Length / 3)
                : File.OpenRead(path));

        Assert.True(w.Unreadable);
        Assert.False(w.Submitting("TA-B", "SIM", "ES", "Buy", 1m, null));
        Assert.NotNull(w.Trouble);
        Assert.Equal(bytes, File.ReadAllText(File_));
        Assert.Equal(["TA-A"], CommittedIds());
    }

    /// <summary>A stream that hands over <paramref name="ok"/> bytes and then fails like a bad disk.</summary>
    sealed class ThrowsPartWay(byte[] data, int ok) : Stream
    {
        long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= ok) throw new IOException("the device is not ready");
            var n = (int)Math.Min(count, ok - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

/// <summary>
/// U14 round-6 probes: targets 3 (rotation both directions), 4 (what an operator is shown when only
/// per-writer sidecars exist) and 5 (the lease on terminal paths).
/// </summary>
public class SidecarAndLeaseVerifyR6Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "wit-r7b-" + Guid.NewGuid().ToString("n")[..8]);
    public SidecarAndLeaseVerifyR6Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    string[] CommittedIds() =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_)).RootElement.GetProperty("records")
            .EnumerateArray().Select(r => r.GetProperty("client_order_id").GetString()!).ToArray();

    /// <summary>
    /// TARGET 4, THE HALF THE SUPPORT PACKAGE COVERS AND THE PROBE DOES NOT. With the owner healthy
    /// and only REFUSED writers' per-writer sidecars beside the witness, what does the operator's
    /// report say? `Noted` must be true so the zero is provisional — and the report must lead them to
    /// a file that actually exists.
    /// </summary>
    [Fact]
    public void A_zero_is_flagged_when_the_only_account_of_a_refusal_is_a_per_writer_sidecar()
    {
        // An owner holds the lease and has committed NOTHING yet — a bridge that has just started.
        using var owner = new CoidWitness(File_);
        Assert.True(owner.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        File.Delete(File_);                       // the committed file is removed; records really is zero

        // A second bridge — trap 35's misconfiguration — is refused, repeatedly.
        using (var refused = new CoidWitness(File_))
            for (var i = 0; i < 5; i++)
                Assert.False(refused.Submitting($"TA-REFUSED-{i}", "SIM", "ES", "Buy", 1m, null));

        var perWriter = Directory.GetFiles(_dir, CoidWitness.ErrorLogName + "-*");
        Assert.NotEmpty(perWriter);
        Assert.False(File.Exists(Sidecar));       // the canonical sidecar was never written

        var w = new CoidWitness(File_);
        var standing = CoidWitnessReport.Standing(w);

        // THE INVARIANT: a zero from a directory where something WAS refused is never a confident
        // zero. For this file a confident zero means "this product never submitted that identifier".
        Assert.True(w.Noted && standing != WitnessStanding.Clean && CoidWitnessReport.ZeroIsProvisional(standing),
            $"records={w.All().Count} but the report calls the directory clean: Noted={w.Noted} "
            + $"Standing={standing} ZeroIsProvisional={CoidWitnessReport.ZeroIsProvisional(standing)} "
            + $"perWriterFiles=[{string.Join(", ", perWriter.Select(Path.GetFileName))}] "
            + $"linesInThem={perWriter.Sum(f => File.ReadAllLines(f).Count(l => l.Trim().Length > 0))}");
    }

    /// <summary>TARGET 5: Dispose releases; a disposed instance does not silently reacquire.</summary>
    [Fact]
    public void Dispose_hands_the_witness_over_and_a_stopped_instance_takes_no_lease_to_read()
    {
        var first = new CoidWitness(File_);
        Assert.True(first.Submitting("TA-1", "SIM", "ES", "Buy", 1m, null));

        var second = new CoidWitness(File_);
        Assert.False(second.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null));

        first.Dispose();
        Assert.True(second.Submitting("TA-2", "SIM", "ES", "Buy", 1m, null));

        // The stopped instance's order handler still fires for foreign identifiers. It must not take
        // the lease back — that is F21, and it is what bricked the live bridge until ATAS restarted.
        first.Identified("TA-SOMEBODY-ELSES", "BRK-X");
        Assert.True(second.Submitting("TA-3", "SIM", "ES", "Buy", 1m, null));
        Assert.Equal(["TA-1", "TA-2", "TA-3"], CommittedIds());
        second.Dispose();
    }
}

