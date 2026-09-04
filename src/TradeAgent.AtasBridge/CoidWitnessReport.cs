namespace TradeAgent.AtasBridge;

/// <summary>One sidecar file beside the witness, and the lines a run read out of it.</summary>
public sealed record SidecarFile(string Path, IReadOnlyList<string> Lines);

/// <summary>
/// THE SIDECAR SET AS ONE VALUE: every file with the lines that were captured from it, or the
/// reason there was no reading. See <see cref="CoidWitness.Sidecars"/>.
/// </summary>
public sealed record SidecarText(IReadOnlyList<SidecarFile> Files, string? Unreadable)
{
    /// <summary>A witness with nowhere to live: no files, and nothing wrong with that.</summary>
    public static readonly SidecarText Nothing = new([], null);
}

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
/// WHY THIS MACHINE IS <see cref="WitnessStanding.Noted"/> — THREE CAUSES, AND THEY ARE NOT THE SAME
/// NEWS.
///
/// `Noted` means "something was refused or written down", and it was worded as if there were one way
/// to reach it. There are three, and one of them is a SUCCESS: a rewrite that never reached the
/// witness was read back and its acknowledgements recovered, which is the recovery working, and it
/// was being reported as something having been "refused". A reader acts differently on each.
///
/// Flags rather than an enum because a machine can be in more than one of them at once; when it is,
/// the report names none of them rather than picking one, and the sidecar files it lists say which.
/// </summary>
[Flags]
public enum WitnessNotes
{
    /// <summary>Nothing this run could attribute. The files listed under the headline say what.</summary>
    None = 0,

    /// <summary>A second writer was refused the witness and wrote its own account beside it.</summary>
    RefusedWriter = 1,

    /// <summary>A file beside the witness was not a rewrite of it and was not adopted.</summary>
    RejectedCandidate = 2,

    /// <summary>A rewrite that never reached the witness was read back and its claims recovered.</summary>
    RecoveredRewrite = 4,

    /// <summary>
    /// THE SIDECAR SET COULD NOT BE READ AT ALL, so none of the three above was observed and none may
    /// be named. Codex F37: a machine whose directory this run could not look in was told that
    /// something "was refused, declined or recovered" — three events, no evidence for any of them,
    /// and the one thing an operator could have acted on left out. It rides beside
    /// <see cref="WitnessStanding.Unresolved"/>, because a set nobody could read is a gap nobody can
    /// rule out.
    /// </summary>
    UnreadableSidecar = 8
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
    public static string Headline(WitnessStanding standing, string sidecarPath,
                                  WitnessNotes notes = WitnessNotes.None) => standing switch
    {
        // AN UNREADABLE SET IS NOT AN UNRESOLVED FAILURE, and it is not a clean machine either. It
        // stands where UNRESOLVED stands — everything below it is provisional — and it says which of
        // the two it is, because the repair is completely different: one is a durability gap to
        // investigate, the other is a permission or a lock to clear.
        WitnessStanding.Unresolved when notes.HasFlag(WitnessNotes.UnreadableSidecar) =>
            $"{sidecarPath} — COULD NOT BE READ. This run cannot tell whether a gap is open.",
        WitnessStanding.Unresolved => $"{sidecarPath} — UNRESOLVED. THIS FILE SHOULD NOT EXIST.",
        WitnessStanding.Historical => $"{sidecarPath} — historical.",
        // THREE CAUSES, THREE SENTENCES, AND ONE OF THEM IS A SUCCESS. It was written when the only
        // way to reach this state was a temp beside the witness being declined; the split per writer
        // added a second bridge that the lease turned away, and the recovery adds a third — a rewrite
        // that never landed, read back and adopted. Describing that one as "refused" tells an
        // operator that something went wrong at the moment the mechanism worked. Where more than one
        // cause is live, or none can be attributed, none is named and the files listed below say
        // which.
        WitnessStanding.Noted => notes switch
        {
            WitnessNotes.RefusedWriter =>
                "no durability gap — but a second writer was refused this witness.",
            WitnessNotes.RejectedCandidate =>
                "no durability gap — but a file beside the witness was declined.",
            WitnessNotes.RecoveredRewrite =>
                "no durability gap — but a rewrite that never landed was recovered.",
            _ => "no durability gap — but something beside the witness was refused, declined or recovered."
        },
        _ => "none recorded"
    };

    /// <summary>What the line above means, in the words the operator gets.</summary>
    public static string[] Explanation(WitnessStanding standing,
                                       WitnessNotes notes = WitnessNotes.None) => standing switch
    {
        WitnessStanding.Unresolved when notes.HasFlag(WitnessNotes.UnreadableSidecar) =>
        [
            "The files beside the witness could not be read — a permission, a lock, a directory",
            "that would not list, or a set being rewritten while it was read. Nothing here says a",
            "failure happened; it says this run could not find out. Treat every verdict below as",
            "provisional until the files can be read, then look again."
        ],
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
        WitnessStanding.Noted => notes switch
        {
            WitnessNotes.RefusedWriter =>
            [
                "A second writer asked for this witness, was refused it, and wrote its own account",
                "of what it could not record — the files listed above include it. Nothing was lost:",
                "the refusal is what stopped that order being sent. But it does mean a count of zero",
                "below is not the same as 'nothing was ever recorded here'."
            ],
            WitnessNotes.RejectedCandidate =>
            [
                "A file beside the witness was not a rewrite of it — wrong lineage, no records, or",
                "unreadable — so it was not adopted, and it was moved aside rather than deleted.",
                "Nothing was lost: it displaced nothing. But it does mean a count of zero below is",
                "not the same as 'nothing was ever recorded here'."
            ],
            WitnessNotes.RecoveredRewrite =>
            [
                "A rewrite of the witness that never reached the disk was found beside it, checked",
                "against the committed file, and its acknowledgements taken back into the record.",
                "That is the recovery working, and nothing was refused. It is noted because the",
                "count below now rests on a file that had to be repaired to produce it."
            ],
            _ =>
            [
                "Something beside the witness was refused, declined or recovered — a second writer",
                "turned away, a file that was not a rewrite of this one, or a rewrite that never",
                "landed and was read back. The files listed above say which. Nothing was lost in any",
                "of the three, but a count of zero below is not the same as 'nothing was ever",
                "recorded here'."
            ]
        },
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
