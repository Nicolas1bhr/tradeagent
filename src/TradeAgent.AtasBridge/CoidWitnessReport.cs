namespace TradeAgent.AtasBridge;

/// <summary>Where a witness's sidecar stands, as one value. See <see cref="CoidWitnessReport"/>.</summary>
public enum WitnessStanding
{
    /// <summary>Nothing has been written down and nothing was refused.</summary>
    Clean,

    /// <summary>Something was refused or noted, and no durability gap is open.</summary>
    Noted,

    /// <summary>A gap happened and a later clean commit closed it. History, not a live problem.</summary>
    Historical,

    /// <summary>A durability gap is open. Every verdict below it is provisional.</summary>
    Unresolved
}

/// <summary>
/// WHAT A PERSON IS TOLD ABOUT THE WITNESS, AS A PURE FUNCTION — and the reason it is not written
/// inline in <c>tools/probe</c>, which is where it used to live.
///
/// That block sits behind a live bridge-pipe connection, so it cannot execute anywhere but a machine
/// running ATAS: measured off Windows it never printed at all, no test project references the probe,
/// and a mutant that made every sidecar read as UNRESOLVED left the whole suite green. The wording an
/// operator actually reads was the least-verified thing in the unit.
///
/// The decision is three inputs and no IO, so it belongs here — in an assembly that is compiled and
/// tested on every machine — and the probe becomes the thing it should be: a caller that renders what
/// this returns.
/// </summary>
public static class CoidWitnessReport
{
    /// <summary>
    /// The standing, from what the witness reports rather than from the sidecar existing.
    ///
    /// <paramref name="troubled"/> is <see cref="CoidWitness.Trouble"/> being non-null — an
    /// UNRESOLVED safety gap, and the only state that makes the readings below it provisional on
    /// account of a durability problem. <paramref name="noted"/> is <see cref="CoidWitness.Noted"/>:
    /// something was refused or recorded, which need not be a gap at all — a foreign leftover moved
    /// aside is the ordinary case — but which does mean a zero is not a confident zero.
    ///
    /// <paramref name="gapClosed"/> is <see cref="CoidWitness.GapClosed"/>, and it used to be "does
    /// the sidecar exist". That is not the same question. A file holding nothing but quarantine
    /// notes exists and has never had a gap to close, so it was labelled HISTORICAL — whose
    /// explanation tells the reader that a clean commit resolved earlier failures, which never
    /// happened — and that label made a zero below it non-provisional. "Historical" means a RESOLVED
    /// marker stands after the last safety line, never "no safety lines in this file".
    /// </summary>
    public static WitnessStanding Standing(bool gapClosed, bool troubled, bool noted) =>
        troubled ? WitnessStanding.Unresolved
        : gapClosed ? WitnessStanding.Historical
        : noted ? WitnessStanding.Noted
        : WitnessStanding.Clean;

    /// <summary>
    /// The same decision off a live witness, so the probe and its tests cannot drift on what they
    /// feed it — which is the mistake that produced the finding above.
    /// </summary>
    public static WitnessStanding Standing(CoidWitness witness) =>
        Standing(witness.GapClosed, witness.Trouble is not null, witness.Noted);

    /// <summary>The one line beside "WITNESS FAILURES".</summary>
    public static string Headline(WitnessStanding standing, string sidecarPath) => standing switch
    {
        WitnessStanding.Unresolved => $"{sidecarPath} — UNRESOLVED. THIS FILE SHOULD NOT EXIST.",
        WitnessStanding.Historical => $"{sidecarPath} — historical.",
        // NOT "a candidate", BECAUSE Noted NOW HAS TWO CAUSES. It was written when the only way to
        // reach this state was a temp beside the witness being declined; since the sidecar was split
        // per writer, a second bridge that the lease turned away reaches it too. Naming the wrong one
        // sends the reader looking for a recovery that never happened. The files are listed below, so
        // which it was is a line away rather than a guess.
        WitnessStanding.Noted => "no durability gap — but something beside the witness was refused.",
        _ => "none recorded"
    };

    /// <summary>What the line above means, in the words the operator gets.</summary>
    public static string[] Explanation(WitnessStanding standing) => standing switch
    {
        WitnessStanding.Unresolved =>
        [
            "Each line is a rewrite of the witness that did not reach the disk. Treat every",
            "verdict below as provisional until this is understood."
        ],
        WitnessStanding.Historical =>
        [
            "The last entry says the witness committed cleanly after those failures, so the",
            "gap is closed and nothing below is provisional on account of this file. It is",
            "kept as history; delete it once it has been read."
        ],
        WitnessStanding.Noted =>
        [
            "Either a file beside the witness was not a rewrite of it and was not adopted, or a",
            "second writer was refused the witness and wrote its own account of that — the files",
            "listed above say which. Nothing was lost either way: a refused writer's order was not",
            "sent, and a declined candidate displaced nothing. But it does mean a count of zero",
            "below is not the same as 'nothing was ever recorded here'."
        ],
        _ => []
    };

    /// <summary>
    /// WHETHER A ZERO BELOW IS PROVISIONAL, which is the whole reason the standing is computed
    /// before the records are counted. "No records" and "this product never submitted that
    /// identifier" are the same sentence to a reader, and they are only the same fact when nothing
    /// was refused on the way to counting them.
    /// </summary>
    public static bool ZeroIsProvisional(WitnessStanding standing) =>
        standing is WitnessStanding.Unresolved or WitnessStanding.Noted;
}
