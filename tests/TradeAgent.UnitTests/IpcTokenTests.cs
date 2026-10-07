using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PIPE'S SHARED SECRET IS WRITTEN ONCE, WHOEVER ASKS FIRST AND HOWEVER MANY ASK AT ONCE
/// (<c>U-test-hygiene-2</c> item 1).
///
/// <para><b>What was wrong.</b> <see cref="IpcToken.Ensure()"/> was an unlocked read-then-write: every
/// caller that found no token minted one of its own and wrote it with <c>File.WriteAllBytes</c>, which
/// truncates the file before it writes it, and the file's mode was made owner-only only AFTER the
/// bytes were in it. So two first callers each walked away holding a token — one of them a token the
/// file no longer had, which is a gateway refusing every agent that reads the file — a reader that
/// came between the truncation and the write read nothing and took it for "absent", and on Windows the
/// second writer met the first one's handle and threw. That last one is how it was seen: windows-latest
/// run 37443989301, <c>VenueOpsTests.The_catalogue_read_is_in_the_deadline_table_at_zero</c>, "being used
/// by another process", three test classes asking one process's fresh home for its token at once.</para>
///
/// <para><b>How it is driven.</b> Each round is a fresh home, and every caller in it is a dedicated
/// thread held at one gate and released together, so the first calls really are concurrent rather than
/// queued on the thread pool. Readers poll the file for the whole of the race, because "a reader never
/// sees half a file" is a claim about the instants in between, and only something reading during them
/// can check it.</para>
/// </summary>
public class IpcTokenTests
{
    const int Rounds = 20;
    const int Writers = 16;
    const int Readers = 2;

    [Fact]
    public void Many_first_calls_on_a_fresh_home_leave_one_token_and_every_caller_holds_it()
    {
        for (var round = 0; round < Rounds; round++)
        {
            using var home = TestEnv.NewScratch("ipc-token");
            var path = Path.Combine(home.Dir, "state", "ipc.token");

            var race = Race(path);
            var distinct = race.Tokens.Distinct().ToList();
            var held = SecretStore.Read(path);

            // ONE SENTENCE FOR THE WHOLE ROUND, carried by every assertion below, so whichever fails first
            // still says what the other three found.
            var said = $"round {round}: {Writers} first callers hold {distinct.Count} different token(s), the file " +
                       $"{(held is not null && distinct.Contains(held) ? "holds one of them" : "holds none of them")}; " +
                       $"{race.Failures.Count} call(s) threw{(race.Failures.Count > 0 ? " — " + string.Join(" | ", race.Failures.Distinct()) : "")}; " +
                       $"readers saw a part-written file {race.Torn.Count} time(s)" +
                       (race.Torn.Count > 0 ? $" (lengths {string.Join(", ", race.Torn.Select(t => t.Length).Distinct())}, where a token is 64)" : "");

            Assert.True(race.Failures.Count == 0, said);
            Assert.True(distinct.Count == 1, said);
            Assert.True(held == distinct[0], said);
            Assert.True(race.Torn.Count == 0, said);

            // NOTHING LEFT BESIDE IT: the token, and no temp a crash-free run should have cleaned up.
            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));

            // OWNER-ONLY. Windows has no mode bits to read — the bytes are DPAPI-sealed to the account and the
            // directory's inherited ACL is the owner's — so this half of the claim is the Unix one.
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    sealed record Outcome(List<string> Tokens, List<string> Failures, List<string> Torn);

    static Outcome Race(string path)
    {
        var tokens = new List<string>();
        var failures = new List<string>();
        var torn = new List<string>();
        using var go = new ManualResetEventSlim(false);
        var writing = Writers;

        var writers = Enumerable.Range(0, Writers).Select(_ => new Thread(() =>
        {
            go.Wait();
            try
            {
                var token = IpcToken.Ensure(path);
                lock (tokens) tokens.Add(token);
            }
            catch (Exception ex) { lock (failures) failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            finally { Interlocked.Decrement(ref writing); }
        })).ToList();

        var readers = Enumerable.Range(0, Readers).Select(_ => new Thread(() =>
        {
            go.Wait();
            while (Volatile.Read(ref writing) > 0)
            {
                try
                {
                    if (SecretStore.Read(path) is { } seen && !IsWhole(seen)) lock (torn) torn.Add(seen);
                }
                catch (Exception ex) { lock (failures) failures.Add($"reader {ex.GetType().Name}: {ex.Message}"); }
            }
        })).ToList();

        foreach (var t in writers.Concat(readers)) t.Start();
        go.Set();
        foreach (var t in writers.Concat(readers)) t.Join();
        return new Outcome(tokens, failures, torn);
    }

    /// <summary>What <see cref="IpcToken.Ensure()"/> mints: 32 random bytes as 64 lower-case hex digits.</summary>
    static bool IsWhole(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigitLower);
}
