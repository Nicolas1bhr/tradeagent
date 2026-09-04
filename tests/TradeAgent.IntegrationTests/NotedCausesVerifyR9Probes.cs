using TradeAgent.AtasBridge;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ROUND-9 ADVERSARIAL VERIFY — target 4 (PRIOR 29: three states, three sentences) and the class
/// behind it.
///
/// The wording split is easy to check and holds. What the class-closure argument does NOT check is
/// the new WRITE it introduced: the adoption now sets `_noted` (`CoidWitness.cs:1376`), and the
/// adoption runs under `EnsureRecovered()`. `Noted` (`:959`) runs `EnsureLoaded()` and NOT
/// `EnsureRecovered()`, so for a machine whose only cause is a recovered rewrite the answer depends
/// on what was asked first.
///
/// `Trouble`'s own doc names this hazard in as many words — "the only production caller was safe by
/// ordering rather than by rule" — and fixes it by running the recovery. `Noted` and `GapClosed` do
/// not, and round 9 gave `Noted` a cause that only the recovery can discover.
/// </summary>
public class NotedCausesVerifyR9Probes : IDisposable
{
    readonly string _dir = Path.Combine(TestEnv.Home, "notes-r9-" + Guid.NewGuid().ToString("n")[..8]);

    public NotedCausesVerifyR9Probes() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    string File_ => Path.Combine(_dir, "coid-witness.json");
    string Sidecar => Path.Combine(_dir, CoidWitness.ErrorLogName);
    CoidWitness Session() => new(File_);

    static string Fingerprint(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }

    /// <summary>The stranded rewrite: same session, same claim, carrying the acknowledgement.</summary>
    string StrandARewrite()
    {
        var owner = Session();
        Assert.True(owner.Submitting("TA-LIVE", "SIM", "ES", "Buy", 1m, null));
        var session = owner.SessionId;
        var committed = File.ReadAllText(File_);
        owner.Dispose();

        var gen = System.Text.Json.JsonDocument.Parse(committed).RootElement
                      .GetProperty("generation").GetInt64();
        var tmp = File_ + ".tmp";
        File.WriteAllText(tmp,
            $$"""{"version":1,"generation":{{gen + 1}},"predecessor":"{{Fingerprint(committed)}}","records":[{"client_order_id":"TA-LIVE","session_id":"{{session}}","written_at":"2026-01-01T00:00:00+00:00","quantity":1,"broker_order_id":"BRK-STRANDED","identified_at":"2026-01-01T00:00:01+00:00"}]}""");
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddMinutes(-5));
        return session;
    }

    /// <summary>
    /// CONTROL — the builder's own reading order. `PriorSession` runs the recovery first, so
    /// everything after it agrees.
    /// </summary>
    [Fact]
    public void CONTROL_the_recovered_rewrite_is_noted_when_the_recovery_has_been_asked_for()
    {
        StrandARewrite();
        var reader = Session();
        Assert.Equal("BRK-STRANDED", reader.PriorSession("TA-LIVE")!.BrokerOrderId);
        Assert.True(reader.Noted);
        Assert.Equal(WitnessNotes.RecoveredRewrite, reader.Notes);
        Assert.Equal(WitnessStanding.Noted, CoidWitnessReport.Standing(reader));
    }

    /// <summary>
    /// AND THE SAME MACHINE, ASKED THE OTHER WAY ROUND. `Noted` is a public property; a caller that
    /// asks it on a fresh instance gets the answer the recovery has not been run to produce.
    /// </summary>
    [Fact]
    public void The_recovered_rewrite_is_noted_however_the_reading_is_ordered()
    {
        StrandARewrite();

        var noted = new CoidWitness(File_).Noted;              // the FIRST thing asked
        var token = new CoidWitness(File_).Token();            // the first thing asked, other instance

        Assert.True(noted,
            $"Noted answered false on a fresh instance while Token answered '{token}' on another; " +
            "the two readings of one machine disagree unless the recovery is asked for first");
    }

    /// <summary>
    /// THE SAME DISAGREEMENT AS ONE SENTENCE AN OPERATOR READS. `Headline` is fed `Standing` and
    /// `Notes`; `Standing(bool,bool,bool)` is the overload `tools/probe`-shaped callers use when they
    /// already hold the three values, and nothing makes them read them in the safe order.
    /// </summary>
    [Fact]
    public void The_headline_says_the_same_thing_whichever_order_its_inputs_were_read_in()
    {
        StrandARewrite();

        var w = new CoidWitness(File_);
        var noted = w.Noted;                                    // read BEFORE Trouble
        var gapClosed = w.GapClosed;
        var troubled = w.Trouble is not null;
        var mine = CoidWitnessReport.Standing(gapClosed, troubled, noted);

        var theirs = CoidWitnessReport.Standing(new CoidWitness(File_));

        Assert.Equal(theirs, mine);
    }

    // ------------------------------------------------------------------ the wording itself

    /// <summary>PRIOR 29, checked independently: three states, three sentences, none of them "refused".</summary>
    [Fact]
    public void The_three_noted_sentences_are_distinct_and_name_their_own_cause()
    {
        var refused = CoidWitnessReport.Headline(WitnessStanding.Noted, Sidecar, WitnessNotes.RefusedWriter);
        var declined = CoidWitnessReport.Headline(WitnessStanding.Noted, Sidecar, WitnessNotes.RejectedCandidate);
        var recovered = CoidWitnessReport.Headline(WitnessStanding.Noted, Sidecar, WitnessNotes.RecoveredRewrite);
        var unattributed = CoidWitnessReport.Headline(WitnessStanding.Noted, Sidecar);

        Assert.Equal(4, new[] { refused, declined, recovered, unattributed }.Distinct().Count());
        Assert.Contains("second writer was refused", refused);
        Assert.Contains("declined", declined);
        Assert.Contains("recovered", recovered);
        Assert.DoesNotContain("refused", recovered);
        Assert.DoesNotContain("declined", recovered);

        // And more than one live cause names none of them.
        Assert.Equal(unattributed,
            CoidWitnessReport.Headline(WitnessStanding.Noted, Sidecar,
                WitnessNotes.RefusedWriter | WitnessNotes.RecoveredRewrite));
    }

    /// <summary>
    /// AND THE ATTRIBUTION IS NOT FOOLED BY THIS ROUND'S OWN NEW FILE NAME: the staging file is a
    /// canonical generation, not a second writer's own sidecar.
    /// </summary>
    [Fact]
    public void A_staging_generation_is_not_attributed_to_a_refused_writer()
    {
        var seed = Session();
        Assert.True(seed.Submitting("TA-SEED", "SIM", "ES", "Buy", 1m, null));
        seed.Dispose();

        File.WriteAllText(Sidecar, $"{DateTimeOffset.UtcNow:O} WARNING tidy" + Environment.NewLine);
        File.WriteAllText(Sidecar + ".1", $"{DateTimeOffset.UtcNow:O} WARNING older" + Environment.NewLine);
        File.WriteAllText(Sidecar + ".rotating", $"{DateTimeOffset.UtcNow:O} WARNING staged" + Environment.NewLine);

        var reader = Session();
        Assert.True(reader.Noted);
        Assert.False(reader.Notes.HasFlag(WitnessNotes.RefusedWriter),
                     "a canonical generation was counted as a second writer's own sidecar");

        // And a real refused writer's file IS attributed.
        File.WriteAllText(Sidecar + "-99999-deadbeef",
            $"{DateTimeOffset.UtcNow:O} ERROR claim=TA-X another writer owns this witness" + Environment.NewLine);
        Assert.True(Session().Notes.HasFlag(WitnessNotes.RefusedWriter));
    }
}
