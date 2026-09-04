using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeAgent.Core;

namespace TradeAgent.AtasBridge;

/// <summary>
/// One claim, written down before it could be true: THIS PRODUCT IS ABOUT TO SUBMIT THIS
/// IDENTIFIER. Plus, later, the one half of the record this product did not choose — the broker
/// order id ATAS assigned to it.
///
/// Everything on it is a fact about the SUBMISSION, not about the order. That is deliberate: the
/// order is what the experiment goes looking for in ATAS's collection, and a record that described
/// the order would be describing the thing it is supposed to be independent evidence about.
/// </summary>
public sealed record CoidWitnessRecord
{
    /// <summary>The identifier that rode out on <c>Order.Comment</c>.</summary>
    [JsonPropertyName("client_order_id")] public string ClientOrderId { get; init; } = "";

    /// <summary>
    /// Which run of the bridge submitted it. A GUID minted at construction and never chosen, seen
    /// or influenced by anything outside the process — see <see cref="CoidWitness.SessionId"/> for
    /// why that matters more than it looks.
    /// </summary>
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = "";

    /// <summary>When the claim was made — which is BEFORE the order was submitted, always.</summary>
    [JsonPropertyName("written_at")] public DateTimeOffset WrittenAt { get; init; }

    [JsonPropertyName("account_id")] public string? AccountId { get; init; }
    [JsonPropertyName("symbol")] public string? Symbol { get; init; }
    [JsonPropertyName("side")] public string? Side { get; init; }
    [JsonPropertyName("quantity")] public decimal Quantity { get; init; }
    [JsonPropertyName("price")] public decimal? Price { get; init; }

    /// <summary>
    /// THE HALF WE DID NOT WRITE. Read off <c>Order.Id</c> once ATAS assigned one, by the session
    /// that submitted the order, and written into this record only by that session (see
    /// <see cref="CoidWitness.Identified"/>). Null until then, and a record with it null is not
    /// evidence of anything — see <see cref="CoidWitness.PriorSession"/>.
    /// </summary>
    [JsonPropertyName("broker_order_id")] public string? BrokerOrderId { get; init; }

    /// <summary>When the broker order id arrived. Null while <see cref="BrokerOrderId"/> is.</summary>
    [JsonPropertyName("identified_at")] public DateTimeOffset? IdentifiedAt { get; init; }
}

/// <summary>
/// THE DURABLE, WRITE-AHEAD RECORD OF WHICH CLIENT ORDER IDS THIS PRODUCT SUBMITTED — the piece
/// that makes rule 1 answerable at all.
///
/// WHY IT HAS TO EXIST. <c>ProveClientOrderId</c> refuses any identifier that is not in the
/// adapter's in-memory <c>_submitted</c> map. That refusal is a 2026-08-27 safety fix and it is
/// right: without it, any order sitting in ATAS's book carrying any comment would set the
/// capability latch, and TradeAgent would report SupportsClientOrderId = true on evidence it never
/// produced. But <c>_submitted</c> dies with the process, and the one experiment that can settle
/// rule 1 — place a resting order, RESTART ATAS, read the book — is precisely the experiment in
/// which the process that submitted the order is gone. So the evidence has to outlive it.
///
/// THREE PROPERTIES DO THE WORK, and none of them is decoration:
///
///   1. WRITE-AHEAD. <see cref="Submitting"/> is called with no broker id BEFORE the order is
///      handed to ATAS. The claim "we submitted this identifier" is therefore made before the
///      order exists, by a process that is dead by the time anybody reads it. It cannot be a story
///      composed afterwards to fit an order somebody found in the book — which is the single
///      failure mode that would turn this whole mechanism back into an automatic true.
///
///   2. A SESSION IDENTITY WE DID NOT CHOOSE. <see cref="SessionId"/> is a fresh GUID minted when
///      the bridge initialises. A record counts as prior-session only when its session differs
///      from the running one, so an identifier THIS process submitted is still governed by
///      <c>_submitted</c> exactly as it was before this file existed. Nothing here relaxes the
///      in-session guard; it adds a second, strictly separate route that requires the reader and
///      the writer to be different processes.
///
///   3. THE HALF WE DID NOT WRITE. <see cref="Identified"/> stores <c>Order.Id</c> as ATAS assigned
///      it, and refuses to write it into any record that does not belong to the current session.
///      Without that refusal a stray order carrying a prior session's comment could write its own
///      id into the record and then match itself — evidence manufactured out of the thing it was
///      supposed to be evidence about.
///
/// WHY IT IS A JSON FILE AND NOT THE DATABASE THE GATEWAY ALREADY HAS. Trap 34 in
/// docs/RESUME-HERE.md: <c>AtasInstallation.InstallBridge</c> deploys into ATAS's Strategies folder
/// by filename prefix — <c>Directory.GetFiles(dir, "TradeAgent.*")</c> — so every first-party
/// assembly is copied and nothing else is. The moment the bridge's dependency chain acquires a
/// third-party assembly (a NuGet package, or the native <c>e_sqlite3</c> that Microsoft.Data.Sqlite
/// loads) that file is silently not deployed, the build is green, the install reports success, and
/// the failure appears inside ATAS as a type load with no message anywhere. This file therefore
/// uses <c>System.Text.Json</c> and <c>System.IO</c> and nothing else. DO NOT reach for the
/// gateway's SQLite store here, and do not add a package reference to this project.
///
/// ALWAYS COMPILED. Unlike <c>AtasStrategyAdapter.cs</c>, this file is not <c>&lt;Compile Remove&gt;</c>d
/// off Windows, so every machine and CI run tests it — the same reason
/// <see cref="ClientOrderIdProofs"/> was moved out of the adapter.
///
/// EVERY PUBLIC METHOD SWALLOWS IO FAILURE. <see cref="Submitting"/> runs inside <c>Place</c>, and
/// an exception escaping from a diagnostic into <c>Place</c> is read by the gateway as an ambiguous
/// placement — rule 3 broken by bookkeeping. A witness that cannot write is a witness that cannot
/// prove anything later, which is the direction to fail in; a witness that throws is an order whose
/// outcome is unknown.
///
/// SWALLOWED IS NOT THE SAME AS LOST, and the difference is the point of <see cref="Save"/>.
/// <see cref="Submitting"/> RETURNS whether the claim reached the disk, so the caller can refuse to
/// place an order whose identifier could not be recorded; a rewrite that will not land keeps its
/// state in memory, leaves the newer content in the temp for <see cref="UncommittedRewrite"/> to
/// find after a restart, and writes an engineering event to <see cref="ErrorLogName"/>. What must
/// never happen is the write disappearing with nobody able to tell.
/// </summary>
public sealed class CoidWitness : IDisposable
{
    /// <summary>
    /// How many records the file keeps. Small on purpose: it is read into memory whole, and the
    /// only records worth keeping are ones a future session might still find resting in ATAS.
    /// </summary>
    public const int DefaultCap = 512;

    /// <summary>The file's own name, so a person looking for it can find it.</summary>
    public const string FileName = "coid-witness.json";

    /// <summary>
    /// WHERE A REWRITE THAT NEVER LANDED IS WRITTEN DOWN, and why it is a plain file beside the
    /// witness rather than a log call.
    ///
    /// This assembly has no logger and may not acquire one. It is deployed into ATAS's Strategies
    /// folder by filename prefix (trap 34), so a package reference or the gateway's SQLite store
    /// would silently not be copied and the bridge would fail to load with no message anywhere; and
    /// nothing in this project writes a log today. A failed rewrite therefore says so here, next to
    /// the file it is about, where <c>tools/probe</c> and a person both already look.
    ///
    /// Bounded twice, and asymmetrically: warnings and markers at <see cref="MaxLoggedFailures"/>
    /// per session, safety events never; the file rotates one generation back past
    /// <see cref="MaxErrorLogBytes"/>. Every part of it is best-effort inside its own catch — a
    /// witness that cannot write must never become a witness that throws.
    /// </summary>
    public const string ErrorLogName = "coid-witness.errors.log";

    /// <summary>
    /// HOW HARD THE WHOLE-FILE REPLACE IS TRIED, AND THESE NUMBERS ARE A JUDGMENT.
    ///
    /// Five attempts with a backoff of 20, 40, 60, 80 ms — 200 ms of patience. On Windows the
    /// replace is refused while the destination is open without <c>FileShare.Delete</c>: a scanner,
    /// the indexer, another process's reader, or another process replacing the same file. All of
    /// those are sub-100 ms in the ordinary case. The constraint pulling the other way is that
    /// <see cref="Submitting"/> runs on the pipe thread inside <c>Place</c>, BEFORE the order is
    /// handed to ATAS, so every millisecond spent here is slippage on a live order. 200 ms is long
    /// enough to ride out a scanner and short enough that a genuinely locked file refuses the order
    /// promptly rather than hanging it.
    ///
    /// NOT MEASURED ON WINDOWS. The one data point there is a GitHub CI failure on
    /// `test (windows-latest)` which says only that the previous budget — three retries, 60 ms — was
    /// not enough at least once. There is no distribution behind these numbers and there will not be
    /// one until a Windows run produces it.
    ///
    /// WHAT IS MEASURED IS THE HEADROOM, and it is what bounds the choice. A refusal that runs the
    /// whole budget costs 205 ms (U14 round-2 verifier) and 229 ms (builder, same day, same machine)
    /// of wall clock — the 200 ms of sleeps plus scheduling — and the worst path a single pipe call
    /// can take, every save in one operation running its full budget, was measured by the verifier
    /// at 8.42 s against the 10 s RPC deadline the gateway allows.
    ///
    /// RE-MEASURED AFTER THE COMPARE-AND-SWAP AND THE LOCK FILE WENT IN, because both sit on this
    /// path and a budget that fits is not a budget that stays fitting: 218 ms for one fully-refused
    /// rewrite and 216 ms each over ten back to back (builder, macOS). Neither addition is
    /// measurable in the uncontended case, which is the design — a compare-and-swap miss costs one
    /// read and short-circuits before the retry budget, and an uncontended lock file is one open. So this
    /// budget fits, with about 1.5 s of margin, and it is the largest one that does: doubling the
    /// attempts would put a wholly contended order past the deadline, where the gateway stops
    /// waiting and records the order UNKNOWN — turning a disk problem into a reconciliation.
    /// </summary>
    const int ReplaceAttempts = 5;

    /// <summary>Multiplied by the attempt number, so the waits lengthen: 20, 40, 60, 80 ms.</summary>
    const int ReplaceBackoffMs = 20;

    /// <summary>50 ms of waiting for the lock file, then refuse the write. See <see cref="Own"/>.</summary>
    const int LockAttempts = 5;
    const int LockBackoffMs = 10;

    /// <summary>
    /// How old a rejected candidate must be before it is moved aside. An in-flight rewrite from
    /// another process is milliseconds old and must not be touched; a leftover from a dead session
    /// is minutes or days old. Two seconds separates them with room to spare.
    /// </summary>
    const int QuarantineGraceSeconds = 2;

    /// <summary>What a resolved sidecar's last line says. See <see cref="_degraded"/>.</summary>
    const string ResolvedMarker = "RESOLVED coid-witness committed cleanly after the failures above.";

    /// <summary>
    /// WHAT MAKES A SIDECAR LINE A SAFETY EVENT ON DISK, and why the state has to be read off this
    /// rather than off the file merely existing.
    ///
    /// Two very different things are written here: a DURABILITY GAP — a claim or an acknowledgement
    /// that did not reach the disk — and a DIAGNOSTIC — a foreign leftover moved aside, two rivals
    /// declined, a rewrite recovered. The first means an order may have gone out with no record
    /// behind it. The second means the file was tidied. They were indistinguishable downstream,
    /// because every line set the degraded state, and the degraded state is what puts DEGRADED on
    /// the ATAS bridge row and drops <c>SupportsClientOrderId</c> to false. A machine with one old
    /// temp beside the witness therefore reported that orders were being refused while every order
    /// went through — the row crying wolf, which is the thing that makes it unreadable the day it is
    /// right.
    ///
    /// Safety lines already carried this prefix and diagnostics already lacked it, so the format
    /// does not change; what changes is that the distinction is now load-bearing and named.
    /// </summary>
    const string SafetyPrefix = "ERROR ";

    /// <summary>
    /// HOW HARD A SIDECAR LINE IS TRIED, AND WHY IT HAS TO BE TRIED AT ALL.
    ///
    /// The append opens the file for writing with <c>FileShare.Read</c> — one writer at a time —
    /// and every failure on this path is swallowed on purpose, because a witness that cannot write
    /// must never become one that throws. Those two together silently DISCARDED any line that lost
    /// the race, and the writers producing these lines are precisely the ones the lease REFUSED, so
    /// there is nothing serialising them: that is what being refused means. Measured on this branch
    /// at 4, 10 and 26 lines lost out of 160 over three runs of four concurrent writers.
    ///
    /// NEITHER WAITING NOR APPENDING FIXES IT, WHICH IS WHY THE FILE IS SPLIT INSTEAD. Measured on
    /// this branch, four concurrent writers × 40 claims: `File.AppendAllText` as it stood lost 4, 10
    /// and 26 lines of 160; retrying the exclusive open lost 7–17; opening `FileMode.Append` with
    /// `FileShare.ReadWrite` and writing each line in a single call lost 6–36. The last is the
    /// telling one — .NET's `FileStream` writes at a position IT tracks rather than through the
    /// kernel's append, so two writers place two lines at the same offset and one of them is simply
    /// not there afterwards. There is no share mode or retry budget that makes a shared file safe
    /// here.
    ///
    /// So each writer gets its own file (see <see cref="SidecarPath"/>) and the race stops existing.
    /// The retries below cover only the rotation racing a reader, not the append.
    /// </summary>
    const int SidecarAttempts = 10;
    const int SidecarBackoffMs = 5;

    const int MaxLoggedFailures = 32;
    const int MaxNoteChars = 400;
    const long MaxErrorLogBytes = 64 * 1024;

    /// <summary>The generation one back, and the name the current log is moved aside under while a
    /// rotation is in flight. Both are SCANNED — see <see cref="SidecarGenerations"/>.</summary>
    const string RolledSuffix = ".1";
    const string SecondSuffix = ".2";

    /// <summary>
    /// WHERE THE NEXT CURRENT LOG IS BUILT BEFORE ANY GENERATION MOVES. <see cref="Rotate"/> writes
    /// the carried-forward line into this name and flushes it to the disk, and only then renames the
    /// generations underneath it. The name is inside the reader's glob on purpose: every state a
    /// crash can leave behind is then a SUBSET of the files a reader already reads.
    /// </summary>
    const string PendingSuffix = ".new";

    /// <summary>
    /// A GENERATION NO BUILD WRITES ANY MORE, AND THAT IS WHY IT IS STILL READ. Rounds 8 and 9
    /// rotated by moving the current log aside under this name; a machine that died inside one of
    /// those rotations, and was then upgraded to this build, has the only copy of its unresolved
    /// line sitting here. Nothing creates it now and nothing deletes it; it is read like any other
    /// generation so that the upgrade does not lose the gap.
    /// </summary>
    const string StagingSuffix = ".rotating";

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// ITS OWN LOCK, NOT THE ADAPTER'S. <c>Submitting</c> is called from <c>Place</c> on the
    /// bridge's pipe thread; <c>Identified</c> is called from the order-event fan on ATAS's thread.
    /// They are genuinely concurrent. It is deliberately NOT the adapter's <c>_gate</c>: this class
    /// performs file IO under its lock, and holding the adapter's gate across a disk write would
    /// put every read of every side table in the adapter behind a spinning disk.
    /// </summary>
    readonly Lock _gate = new();

    readonly List<CoidWitnessRecord> _records = new();
    readonly int _cap;
    readonly string? _path;

    /// <summary>
    /// THE ONE STEP THAT FAILS ON WINDOWS AND CANNOT BE MADE TO FAIL ANYWHERE ELSE.
    ///
    /// <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c> refuses with a sharing violation when the
    /// destination is open without <c>FileShare.Delete</c> — a scanner, the indexer, another
    /// process's reader. On macOS and Linux <c>rename(2)</c> does not consult open handles at all,
    /// so the failure this class has to survive is not reproducible on the machine it is written on.
    /// It is therefore a seam: production passes nothing and gets <see cref="DefaultReplace"/>, and
    /// a test passes a delegate that throws the way Windows would.
    /// </summary>
    readonly Action<string, string> _replace;

    /// <summary>
    /// THE OTHER STEP THAT FAILS ON WINDOWS AND CANNOT BE MADE TO FAIL ANYWHERE ELSE — opening a file
    /// that exists and refusing to hand it over.
    ///
    /// On Unix a test can chmod a candidate to 000 and get exactly that. On Windows there is no
    /// portable way to do it from inside a test: it takes an ACL or a second process holding the file
    /// without <c>FileShare.Read</c>. So the branch that matters most — a candidate that is THERE and
    /// unreadable, which must count as a failed read or a witness with nothing committed beside it
    /// reports a confident zero — was asserted on macOS and Linux and skipped on the one platform
    /// this product runs on.
    ///
    /// Same remedy as <see cref="_replace"/>: production passes nothing and opens the file the usual
    /// way; a test passes an opener that refuses. The Unix chmod test is KEPT beside the seam test,
    /// so a seam that stopped resembling the real refusal would show up.
    /// </summary>
    readonly Func<string, Stream> _open;

    /// <summary>
    /// THE STEP THAT DECIDES WHETHER THE ROTATION ORDER IS LOAD-BEARING — writing the restatement
    /// into the new log, in <see cref="Rotate"/>.
    ///
    /// It is a seam for the same reason the two above are: the failure it has to survive cannot be
    /// provoked from a test without one. <c>Rotate</c> restates the unresolved line into the new log
    /// BEFORE it deletes the generation that holds it, and the whole value of that ordering is what
    /// remains on disk when the restatement does not land — a full disk, a directory a backup tool
    /// made read-only, a scanner holding the name. <c>Rotate</c> runs inside
    /// <see cref="AppendToErrorLog"/>'s catch, so such a failure does not stop the process: it simply
    /// leaves the sidecar in whatever state the ordering produced, which is exactly the state the
    /// next start reads.
    ///
    /// Without this the order is untestable in-process — the two syscalls it separates have no
    /// observation point between them — and a later edit could reverse it in silence. That was
    /// recorded as a surviving mutant (MF27b) before this existed. Production passes nothing.
    /// </summary>
    readonly Action<string, string> _writeSidecar;

    /// <summary>
    /// THE FOUR RENAMES <see cref="Rotate"/> AND <see cref="Resume"/> MAKE, AS ONE SEAM, AND THE
    /// REASON IS THE SAME AS <see cref="_replace"/>'S: the rename that has to be survived is the one
    /// Windows refuses, and it cannot be provoked on the machine this is written on.
    ///
    /// What made it necessary rather than merely symmetrical is that ROTATION IS RESUMABLE NOW. The
    /// state that has to be recovered from — <c>log</c> gone, <c>log.new</c> holding everything — is
    /// produced by the LAST of the four acts failing, and there is no observation point between the
    /// acts. Without a seam the resume path is unreachable from a test, and the thing it recovers
    /// from cannot be built except by a crash.
    ///
    /// Production passes nothing and gets <see cref="DefaultMoveSidecar"/>.
    /// </summary>
    readonly Action<string, string, bool> _moveSidecar;

    /// <summary>
    /// THE TWO CALLS <see cref="ReadSidecarSet"/> MAKES, AS SEAMS, AND FOR THE SAME REASON
    /// <see cref="_open"/> IS ONE: some of the failures the snapshot has to classify — an ACL that
    /// denies a file's attributes on Windows, a set that keeps changing under the read — cannot be
    /// provoked on the machine the code is written on. Null in every production use.
    ///
    /// They are reachable from <see cref="ReadSidecarSet"/> and from nowhere else, which is what
    /// makes the class-closure argument a sentence: a consumer cannot conflate "I could not read it"
    /// with "there is nothing there" because a consumer never asks the filesystem anything.
    /// </summary>
    readonly Func<string, string[]> _readSidecar;
    readonly Func<string, string, string[]> _listSidecars;

    /// <summary>
    /// THE ONE SNAPSHOT EVERY READING COMES OUT OF, or null when one has not been taken since the
    /// last write. See <see cref="ReadSidecarSet"/> and <see cref="Derive"/>.
    /// </summary>
    SidecarSnapshot? _snapshot;

    /// <summary>
    /// WHY THERE IS NO SNAPSHOT, or null when there is one. Set by <see cref="Derive"/> and by
    /// nothing else. Non-null means, at every consumer and with no exceptions: standing UNRESOLVED,
    /// a provisional zero, a non-null <see cref="Trouble"/> and a report that says the sidecar could
    /// not be read.
    /// </summary>
    string? _snapshotRefusal;

    /// <summary>
    /// WHAT THE SNAPSHOT SAID, COMPUTED ONCE, READ BY EVERY PROPERTY IN ANY ORDER. R9-2: round 9
    /// gave <see cref="Noted"/> a cause that only the recovery discovers and left <c>Noted</c>
    /// running the load and not the recovery, so a fresh instance answered <c>false</c> while
    /// another answered <c>io:noted</c>, and the operator's sentence was right only because C#
    /// evaluates arguments left to right. These five are written in <see cref="Derive"/> and nowhere
    /// else; the instance and session latches beside them are separate fields, and every public
    /// member runs <see cref="Ready"/> so that all of them are complete before any is read.
    /// </summary>
    bool _diskNoted;
    bool _diskDegraded;
    bool _diskGapClosed;
    WitnessNotes _diskNotes;
    string[] _sidecars = [];

    /// <summary>
    /// THE SIDECAR SET AS A RENDERER SEES IT — the same snapshot as the five above, with the lines
    /// carried rather than the names. Written in <see cref="Derive"/> and nowhere else. See
    /// <see cref="Sidecars"/> for what handing out names cost.
    /// </summary>
    SidecarText _sidecarText = SidecarText.Nothing;

    /// <summary>
    /// This writer's own account of how big its sidecar is: the length the snapshot recorded, plus
    /// everything appended since. There is exactly ONE writer per sidecar file (see
    /// <see cref="SidecarPath"/>), so the rotation trigger needs no filesystem probe — and the probe
    /// it replaces was a <c>File.Exists</c> plus an attribute read that answered "no" for a denial
    /// as readily as for an absence, which is the conflation this whole round exists to end.
    /// Negative until the first snapshot supplies it.
    /// </summary>
    long _sidecarBytes = -1;

    /// <summary>
    /// HOW MANY ROTATIONS THIS INSTANCE HAS ATTEMPTED, and therefore the last part of the name act 1
    /// writes. It is what makes that name unique per ATTEMPT rather than per file: an attempt that
    /// died after opening its temp leaves that name occupied, and a retry reusing it would have to
    /// truncate — which is the whole of the finding. See <see cref="Rotate"/>.
    /// </summary>
    int _rotations;

    /// <summary>The session id, clipped, for the names this instance owns.</summary>
    readonly string _session8;

    /// <summary>
    /// WHY THIS RUN WILL NOT APPEND TO ITS SIDECAR, or null. A rotation that stopped after its
    /// current log was rolled aside leaves <c>log.new</c> holding everything and no current log at
    /// all; <see cref="Resume"/> finishes it before any append, and when it CANNOT the append is
    /// refused rather than performed into a file that was about to be overwritten by the completion.
    ///
    /// It is reported through <see cref="Trouble"/> and it degrades the machine, because the
    /// alternative — an engineering log that silently stops recording while orders keep being
    /// refused — is the exact shape of every finding this class has had.
    /// </summary>
    string? _appendRefused;

    bool _loaded;
    bool _readFailed;

    /// <summary>
    /// THIS BUILD DOES NOT HAVE THE COMMITTED CONTENT, and it is not because the file is absent.
    /// Two ways in and they are one predicate on purpose (<see cref="EnsureLoaded"/>): the read was
    /// REFUSED, or the bytes arrived and are not an envelope. Absent — <see cref="FileNotFoundException"/>
    /// on this path — is neither, and is the only outcome that lets a write proceed against nothing.
    /// Refuses every write while it stands and reports through <see cref="Trouble"/>, which puts the
    /// reason on the ATAS bridge row and drops <c>SupportsClientOrderId</c>.
    /// </summary>
    bool _committedUnreadable;
    bool _candidateUnreadable;

    /// <summary>
    /// THERE IS SOMETHING IN THE SIDECAR — from a previous run of this product or from this one.
    /// Reported through <see cref="Token"/> as <c>io:degraded</c>, because a durability gap that
    /// ended when the process did is exactly the gap nobody would otherwise ever see: the next
    /// session starts with a clean <see cref="LastWriteFailure"/> and a witness that looks perfect.
    /// Cleared by deleting the file, which takes effect at the next start — checked once at load
    /// rather than on every heartbeat, because <see cref="Token"/> runs on the heartbeat and has no
    /// business stat-ing a file five times a minute forever.
    ///
    /// THIS FIELD IS THIS SESSION'S OWN LATCH ONLY. What the FILES say is <see cref="_diskDegraded"/>,
    /// computed in <see cref="Derive"/> from one snapshot; the reading an operator gets is the two of
    /// them together (<see cref="Degraded"/>). They are separate because a safety line this session
    /// wrote must degrade the machine even when the append that would have recorded it failed — the
    /// disk cannot be the only source — while the disk must be the only source for everything this
    /// session did not write, so that no property depends on another having run first.
    /// </summary>
    bool _degraded;

    /// <summary>
    /// THERE IS SOMETHING IN THE SIDECAR, whether or not it is a durability gap. Reported through
    /// <see cref="Token"/> as <c>io:noted</c> and nowhere else — it is deliberately NOT a
    /// <see cref="Trouble"/> input, because a quarantined leftover is not a reason to tell an
    /// operator that orders are being refused. What it is for is the reading a zero needs: a reader
    /// that sees <c>records:0</c> beside <c>io:noted</c> knows something was refused, and does not
    /// take the zero for "this product never submitted that identifier".
    ///
    /// THIS FIELD IS THE IN-PROCESS CAUSES ONLY — a candidate this instance declined, a rewrite it
    /// recovered, a line it wrote. What the FILES say is <see cref="_diskNoted"/>. See
    /// <see cref="_degraded"/> for why the two are apart, and <see cref="Noted"/> for the reading.
    /// </summary>
    bool _noted;

    /// <summary>
    /// WHICH OF THE THREE THINGS PUT THIS MACHINE IN <see cref="WitnessStanding.Noted"/>, as far as
    /// this run can attribute it. Accumulated as each is discovered rather than decided once, because
    /// a machine can be in more than one at a time; when it is, the report names none of them and
    /// lists the files instead. See <see cref="WitnessNotes"/>.
    /// </summary>
    WitnessNotes _notes;

    /// <summary>
    /// THE LINEAGE OF WHAT IS COMMITTED, as far as this instance knows: the generation the committed
    /// file carries, and the fingerprint of its exact bytes (null when nothing is committed). Every
    /// rewrite this instance writes names them, and every rewrite this instance ADOPTS has to name
    /// them. Updated only by a replace that actually landed.
    /// </summary>
    long _generation;
    string? _committedHash;

    /// <summary>
    /// A TEMP NAME NO OTHER WRITER USES, and why one shared name was a defect rather than a detail.
    ///
    /// Trap 35: a second bridge can be running. With one <c>coid-witness.json.tmp</c> for everybody,
    /// two writers interleave inside a rewrite — B's <c>WriteAllText</c> lands between A's write and
    /// A's rename, so A renames B's content onto the file and reports its own claim durable when
    /// what got committed was somebody else's; and a temp consumed by the other writer's rename
    /// makes this one's replace fail with FileNotFound and burn the entire retry budget waiting for
    /// a file that is never coming back.
    ///
    /// The prefix carries the process id AND the session, because two witnesses over one path inside
    /// one process is the ordinary case in tests and a real one whenever two strategies are started.
    /// The sequence makes each rewrite of this instance distinct, so a rewrite that failed is still
    /// on disk under its own name when the next one is written.
    /// </summary>
    readonly string _tempPrefix = "";
    int _tempSeq;

    /// <summary>
    /// Temps this instance wrote that never got committed. Deleted after the next successful commit:
    /// that commit carries the same records plus whatever came after, so nothing is thrown away —
    /// and anything it does NOT carry was removed by <see cref="Trim"/>, which is the cap doing its
    /// job. Leaving them lying around is how a trimmed identifier comes back to life.
    /// </summary>
    readonly List<string> _stranded = new();

    /// <summary>The candidate this instance adopted at load, or null. Deleted once it is committed.</summary>
    string? _adopted;

    /// <summary>Why this instance does not own the witness, or null while it does. See <see cref="Lease"/>.</summary>
    string? _notOwned;

    /// <summary>
    /// THE LEASE: an exclusive handle on the lock file, taken at this instance's FIRST WRITE and held
    /// until <see cref="Dispose"/> or process death. Null while this instance has never written —
    /// which is every reader, for ever. See <see cref="Lease"/> for why it is a lifetime and not a
    /// call.
    /// </summary>
    FileStream? _lease;

    /// <summary>
    /// WHAT THE SCAN FOUND, HELD UNTIL SOMEBODY WHO OWNS THE FILE CAN ACT ON IT.
    ///
    /// The scan is pure — it reads and classifies and changes nothing — because it runs on every
    /// read path, including <see cref="PriorSession"/> on ATAS's event thread and <c>tools/probe</c>
    /// in another process entirely. A reader that adopted, quarantined and wrote the sidecar could
    /// do all three in the middle of the owning writer's rewrite: the candidate it "recovers" is the
    /// rewrite in flight, the sidecar line it leaves says a gap happened, and the rewrite then
    /// commits perfectly cleanly, leaving an unresolved failure recorded about nothing.
    ///
    /// So the findings wait here, and <see cref="ApplyRecovery"/> acts on them only under the lock.
    /// </summary>
    readonly List<(string Path, Envelope Envelope)> _viable = new();
    readonly List<(string Path, string Why)> _rejected = new();
    bool _adoptedAlready;

    /// <summary>
    /// How much the adopted candidate actually gave. Zero is the awkward case: the candidate is a
    /// legal transition, so it is not rejected, and it contributed nothing, so it is not adopted —
    /// which left it reported as "recovered" and lying in the glob for every later session to find,
    /// declare recovered and write another line about. See <see cref="ReportAndQuarantine"/>.
    /// </summary>
    int _recovered;
    bool _reported;
    bool _writeFailed;
    int _loggedFailures;

    /// <summary>
    /// The last rewrite that did not reach the disk, in one line naming the destination, the temp
    /// that holds the newer state, the newest claim at risk and the exception. Null until one
    /// happens, and it stays set afterwards — a session that ever failed to make a claim durable has
    /// had a gap, and clearing this on the next success would hide it. Read by tests and available
    /// to the probe; the same line is appended to <see cref="ErrorLogName"/>.
    /// </summary>
    public string? LastWriteFailure { get; private set; }

    /// <summary>
    /// THE SESSION IDENTITY, AND WHY IT IS A GUID RATHER THAN SOMETHING MEANINGFUL.
    ///
    /// A process id would be reused; a start timestamp collides at second resolution and is
    /// guessable; a machine-stable name would be identical across restarts, which is the one thing
    /// it must never be. A GUID minted here is different on every run of the bridge by
    /// construction, and it is not chosen, derived from, or visible to anything outside this
    /// process before it is written down — so "this record was written by a different run" is a
    /// fact about the world rather than about anybody's intent.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Where the record lives, or null when it could not be placed anywhere at all.
    ///
    /// A null path makes this whole object INERT rather than memory-only: nothing is recorded and
    /// nothing is answered. A witness that held claims in memory would report counts through
    /// <see cref="Token"/> and hand records to <see cref="All"/> that no future session will ever
    /// see, and durability is the entire value of the thing. Better to say "disabled" once than to
    /// look like a working witness that quietly forgets everything at shutdown.
    /// </summary>
    public string? Path => _path;

    /// <summary>
    /// Where a rewrite that never landed is written down, or null when this witness has no home.
    /// Printed by <c>tools/probe</c> and collected into the support package, because a file nobody
    /// is told about is not a report.
    /// </summary>
    /// <summary>
    /// WHERE THIS INSTANCE WRITES, AND WHY IT IS NOT ALWAYS <see cref="ErrorLogPath"/>.
    ///
    /// The OWNER writes the canonical file: it is the one whose last deciding line says whether this
    /// machine has an open durability gap, and the only instance that can ever close one, because
    /// closing one means committing. Anything else — an instance the lease refused — writes a file
    /// of its own beside it, named for the process and session, so that there is exactly one writer
    /// per file and nothing to race.
    ///
    /// The name keeps <c>.errors.log</c> inside it so the probe's glob and the support package's
    /// (`*.errors.log*`) collect it without being told, and <see cref="Noted"/> counts it: a refused
    /// writer's account of what it could not record is exactly the sort of thing a support package
    /// exists to carry. It does NOT decide the degraded state — a second bridge being turned away is
    /// a misconfiguration that cost no order, since the refusal is what stops the order being sent,
    /// and letting it mark the machine degraded for ever is the row crying wolf again.
    /// </summary>
    string? SidecarPath
    {
        get
        {
            if (ErrorLogPath is not { } canonical) return null;
            if (_lease is not null) return canonical;
            var session = SessionId.Length >= 8 ? SessionId[..8] : SessionId;
            return $"{canonical}-{Environment.ProcessId}-{session}";
        }
    }

    public string? ErrorLogPath
    {
        get
        {
            if (_path is null) return null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, ErrorLogName);
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// The live bridge's witness: a fresh session id, and the file under
    /// <see cref="Paths.BridgeDir"/>, which <see cref="Paths"/> has already created.
    /// </summary>
    public CoidWitness() : this(DefaultPath(), null, DefaultCap) { }

    /// <summary>
    /// The testable shape. <paramref name="sessionId"/> is null in every production use — a caller
    /// choosing the session id is exactly what property 2 above rules out — and tests pass null too:
    /// two instances over one path get two different sessions for free, which is the whole scenario.
    ///
    /// <paramref name="replace"/> is null in every production use as well. It exists because the
    /// failure this class must survive — a rename refused because the destination is open — happens
    /// on Windows and cannot be provoked on the machine the code is written on. See
    /// <see cref="_replace"/>.
    /// </summary>
    public CoidWitness(string? path, string? sessionId = null, int cap = DefaultCap,
                       Action<string, string>? replace = null, Func<string, Stream>? open = null,
                       Action<string, string>? writeSidecar = null,
                       Func<string, string[]>? readSidecar = null,
                       Func<string, string, string[]>? listSidecars = null,
                       Action<string, string, bool>? moveSidecar = null)
    {
        _path = path;
        _cap = cap < 1 ? 1 : cap;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("n") : sessionId;
        _session8 = SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        _replace = replace ?? DefaultReplace;
        _open = open ?? DefaultOpen;
        _writeSidecar = writeSidecar ?? DefaultWriteSidecar;
        _moveSidecar = moveSidecar ?? DefaultMoveSidecar;
        _readSidecar = readSidecar ?? File.ReadAllLines;
        // ENTRIES, NOT FILES, and the reason is on Listing: `Directory.GetFiles` does not return a
        // DIRECTORY sitting at a sidecar's name, so that name would never be read and would answer
        // "absent" — the round-9 finding one turn further out.
        _listSidecars = listSidecars ?? Directory.GetFileSystemEntries;
        if (_path is not null)
            _tempPrefix = $"{_path}.tmp-{Environment.ProcessId}-{(SessionId.Length >= 8 ? SessionId[..8] : SessionId)}-";
    }

    /// <summary>
    /// The real thing: one operation, so a reader sees the old file or the new one and no third
    /// thing. See <see cref="Save"/> for why never a delete followed by a move.
    /// </summary>
    static void DefaultReplace(string tmp, string destination) =>
        File.Move(tmp, destination, overwrite: true);

    /// <summary>
    /// The real open, with the share flags a file that is being REPLACED under the reader needs: a
    /// concurrent writer and a concurrent delete are both admitted. See <see cref="ReadTolerantly"/>.
    /// </summary>
    static Stream DefaultOpen(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// The real write: create or replace, whole file, one call — and FLUSHED TO THE DISK before it
    /// returns. See <see cref="_writeSidecar"/>.
    ///
    /// The caller is <see cref="Rotate"/>, whose very next act destroys the generation this text
    /// replaces. `File.WriteAllText` returns once the bytes are in the operating system's cache, so
    /// the delete could reach the platter first and a power cut between them would leave neither
    /// copy. <c>Flush(flushToDisk: true)</c> is what makes "written before destroyed" a fact about
    /// the disk rather than about this process's view of it.
    /// </summary>
    /// <summary>
    /// CREATE-NEW, NOT CREATE, AND THAT IS THE FIFTH CRASH POINT RATHER THAN A PREFERENCE.
    ///
    /// Its only caller is <see cref="Rotate"/>'s act 1, which now writes a name unique to the
    /// attempt. <c>FileMode.Create</c> would TRUNCATE whatever it found there, so a name left
    /// occupied by an earlier attempt — or by an earlier build's <c>log.new</c>, which is what this
    /// used to be handed — is emptied before a single byte is written, and one transient IO error
    /// after that open destroys the only copy of an unresolved marker. Refusing to open an occupied
    /// name turns that into a failed rotation, which the retry above simply performs again under the
    /// next name.
    /// </summary>
    static void DefaultWriteSidecar(string path, string text) => WriteDurably(path, text, FileMode.CreateNew);

    /// <summary>The real rename. One operation, so a reader sees one name or the other.</summary>
    static void DefaultMoveSidecar(string source, string destination, bool overwrite) =>
        File.Move(source, destination, overwrite);

    /// <summary>
    /// WHICH SIDECAR WRITES ARE FLUSHED TO THE PLATTER, AND WHICH ARE NOT, IN ONE PLACE.
    ///
    /// FLUSHED: the rotation's carry (<see cref="DefaultWriteSidecar"/>, and only that). Its very
    /// next act renames a generation away, so "written before destroyed" has to be a fact about the
    /// disk rather than about this process's view of it — a power cut between a cached write and a
    /// completed rename would leave neither copy.
    ///
    /// NOT FLUSHED: the ordinary append in <see cref="AppendToErrorLog"/>, which is
    /// <c>File.AppendAllText</c>. Nothing is destroyed after it, so the only thing an <c>fsync</c>
    /// per engineering event would buy is durability against a power cut in the milliseconds after
    /// it — and it would be paid on a path that runs while an order is being refused. A crash there
    /// loses the newest line of a log whose whole job is to outlive the process; losing the line
    /// about the crash that just happened is a bounded loss, and the write-ahead record itself —
    /// <see cref="Save"/> — is what is actually protected.
    ///
    /// This is NOT verified to reach the platter either way: no in-process observation on a
    /// developer machine distinguishes a flushed write from an unflushed one, and a <c>SIGKILL</c>
    /// does not, because the page cache survives it. What IS measured is the ORDER — see
    /// <see cref="Rotate"/>.
    /// </summary>
    /// <summary>
    /// WHAT THE DISK WILL WEIGH, NOT HOW MANY <c>char</c>s THE STRING HOLDS. The sidecar is written
    /// as UTF-8 and bounded in BYTES, and a <c>string.Length</c> counts UTF-16 code units: every
    /// accented character in an OS error message weighs two bytes and counted as one, every CJK
    /// character in a path weighs three and counted as one. The bound is not decorative — it is what
    /// keeps an unrationed stream of safety events finite — so a log on a machine whose error
    /// strings are not ASCII grew to two or three times its cap before anything rotated it.
    /// </summary>
    static long ByteCount(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    static void WriteDurably(string path, string text, FileMode mode)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Resolving the default path touches <see cref="Paths"/>, which creates directories. Guarded
    /// because a witness that cannot find a home must degrade to "records nothing" rather than
    /// throw out of a field initialiser inside an ATAS-constructed strategy.
    /// </summary>
    static string? DefaultPath()
    {
        try { return System.IO.Path.Combine(Paths.BridgeDir, FileName); }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// PROPERTY 1, THE WRITE-AHEAD CLAIM. Call this BEFORE the order is handed to ATAS, never after.
    ///
    /// The record is written with no broker order id, because at this moment there is no order to
    /// have one. That ordering is the entire evidential value of this file: a claim made before its
    /// subject exists cannot have been shaped to fit it. Calling this after the submission would
    /// leave a file that says exactly the same thing and proves nothing at all, and nothing in the
    /// data would show the difference — which is why it is said here rather than left to a caller
    /// to remember.
    ///
    /// A repeat of an identifier already on file REPLACES it. Two records under one identifier
    /// would make <see cref="PriorSession"/> ambiguous, and the accurate reading of a resubmission
    /// is that THIS session is submitting it now: any earlier record's broker id belongs to a
    /// different order that happened to share the identifier.
    ///
    /// IT RETURNS WHETHER THE CLAIM IS ON DISK, AND THE CALLER IS MEANT TO ACT ON IT. True means the
    /// record reached <see cref="Path"/>. False means it did not, for one of three reasons: no
    /// identifier was supplied, this witness has nowhere to live (<see cref="Path"/> is null), or
    /// the rewrite did not land inside <see cref="ReplaceAttempts"/> attempts. An order whose
    /// identifier could not be recorded must not be sent — the whole point of a write-ahead record
    /// is that it exists BEFORE the order does, and a claim that only ever lived in this process's
    /// memory is not one. The caller is <c>AtasStrategyAdapter.Place</c>, which runs this before
    /// handing the order to ATAS and can therefore still refuse it.
    ///
    /// IT RETURNS RATHER THAN THROWS, and that is not squeamishness. An exception out of here lands
    /// inside <c>Place</c> after <c>_submitted</c> has been written and before ATAS has been asked,
    /// where the gateway reads it as an AMBIGUOUS placement and starts reconciling an order that was
    /// never submitted — rule 3 broken by bookkeeping. A caller that wants to refuse can raise its
    /// own definite rejection, which is honest because nothing has been handed to ATAS yet.
    /// </summary>
    public bool Submitting(string clientOrderId, string? accountId, string? symbol, string? side,
                           decimal quantity, decimal? price)
    {
        if (string.IsNullOrEmpty(clientOrderId) || _path is null) return false;
        try
        {
            lock (_gate)
            {
                // NO LEASE, NO WRITE. See Lease: one owner per witness, and a writer that is not
                // the owner refuses the order rather than racing a party whose semantics are unknown.
                if (!Lease()) { NotOurs(NotOursDetail(clientOrderId)); return false; }

                EnsureLoaded();
                if (_committedUnreadable) { NotOurs(UnreadableDetail()); return false; }
                AdoptInMemory();
                ReportAndQuarantine();

                // EVERYTHING BEFORE THE ATTEMPT, KEPT, so a refusal can be undone exactly. See the
                // rollback below for why a refused claim may not be left lying in memory.
                var before = _records.ToArray();

                _records.RemoveAll(r => string.Equals(r.ClientOrderId, clientOrderId, StringComparison.Ordinal));
                _records.Add(new CoidWitnessRecord
                {
                    ClientOrderId = clientOrderId,
                    SessionId = SessionId,
                    WrittenAt = DateTimeOffset.UtcNow,
                    AccountId = accountId,
                    Symbol = symbol,
                    Side = side,
                    Quantity = quantity,
                    Price = price
                });
                Trim();
                if (Save(clientOrderId)) return true;

                // THE REFUSED CLAIM IS TAKEN BACK OUT, and this is a rule-1 correction rather than
                // tidiness. Returning false means <c>Place</c> refuses the order, so no order
                // carrying this identifier is ever sent. But the record would stay in memory, and
                // the order-event fan calls Identified for EVERY order it sees carrying a comment —
                // so an unrelated order in ATAS's book bearing this identifier would complete the
                // abandoned claim with its own broker id, and that record is then full
                // prior-session evidence: a write-ahead claim, acknowledged, for an order this
                // product never submitted. Exactly the manufactured proof the whole file exists to
                // make impossible.
                //
                // The snapshot is restored rather than the new record removed, because Trim may
                // have dropped others to make room for it.
                _records.Clear();
                _records.AddRange(before);
                return false;
            }
        }
        catch (Exception) { MarkWriteFailed(); return false; }
    }

    /// <summary>
    /// PROPERTY 3, THE HALF WE DID NOT WRITE. Records the broker order id ATAS assigned, and does
    /// so ONLY into a record belonging to the RUNNING session.
    ///
    /// That restriction is the load-bearing part and it is not defensiveness. Without it, an order
    /// found in ATAS's book carrying a prior session's comment — placed by hand, restored from a
    /// workspace, or belonging to something else entirely — would have its own id written into that
    /// prior record by this session, and would then match the record perfectly on the very next
    /// read-back. The proof would be manufactured out of the thing it was supposed to be evidence
    /// about. So a prior-session record is untouchable: this method is a no-op on it, permanently.
    ///
    /// FIRST NON-EMPTY ID WINS. A broker order id does not change once assigned, and refusing later
    /// writes means no later event can quietly rewrite the half of the record this process did not
    /// choose.
    /// </summary>
    public void Identified(string clientOrderId, string? brokerOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId) || string.IsNullOrEmpty(brokerOrderId) || _path is null) return;
        try
        {
            lock (_gate)
            {
                // LOOK BEFORE LEASING. The order-event fan calls this for EVERY order in ATAS's book
                // carrying a comment, so leasing first meant a witness belonging to a strategy that
                // had already been stopped reacquired the file on the next event about somebody
                // else's identifier — and held it for the life of the ATAS process, refusing every
                // order the live bridge then tried to record. Reading the record needs no lease, and
                // if there is nothing of ours under that identifier there is nothing to write.
                EnsureLoaded();
                AdoptInMemory();
                var i = _records.FindIndex(r => string.Equals(r.ClientOrderId, clientOrderId, StringComparison.Ordinal));
                if (i < 0) return;

                var record = _records[i];
                // Not ours to write on. Nothing is recorded, and nothing is reported: this is the
                // ordinary case every time a prior session's order shows up in the book.
                if (!string.Equals(record.SessionId, SessionId, StringComparison.Ordinal)) return;
                if (!string.IsNullOrEmpty(record.BrokerOrderId)) return;

                // Now there IS something to write, so now the lease matters.
                if (!Lease()) { NotOurs(NotOursDetail(clientOrderId)); return; }
                if (_committedUnreadable) { NotOurs(UnreadableDetail()); return; }
                ReportAndQuarantine();

                // NO ROLLBACK HERE, AND THE ASYMMETRY WITH Submitting IS THE POINT. A failed
                // Submitting means the order will not be sent, so its claim describes nothing and
                // must not survive. A failed Identified is the opposite: the order IS live and the
                // broker id IS real, so the half we did not write has to be kept in memory for the
                // next save to carry, and left in the temp for a later session to recover.
                _records[i] = record with { BrokerOrderId = brokerOrderId, IdentifiedAt = DateTimeOffset.UtcNow };
                Save(clientOrderId);
            }
        }
        catch (Exception) { MarkWriteFailed(); }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// THE QUESTION THE RESTART EXPERIMENT ASKS: did a PREVIOUS run of this product submit this
    /// identifier, and did that run see ATAS assign a broker order id to it?
    ///
    /// Returns the record only when BOTH hold. Everything else is null:
    ///
    ///   * no record at all — this identifier is not one this product ever submitted, and the
    ///     read-back must go on refusing it exactly as it did before this file existed;
    ///   * a record from the RUNNING session — then <c>_submitted</c> is the authority on it and
    ///     the in-session readings (SameRef / Distinct) are the ones that apply. Answering here
    ///     would let a fresh process reach the cross-session reading for an order it placed itself
    ///     thirty seconds ago, which is the automatic true this whole mechanism exists to avoid;
    ///   * a record with no broker order id — the submitting session never saw ATAS assign one, so
    ///     there is no half-we-did-not-write to check an order against, and a match on the comment
    ///     alone is satisfiable by any order carrying that comment.
    ///
    /// WHAT THE CALLER MUST STILL DO, because this method cannot: require that the order in front
    /// of it carries the SAME <see cref="CoidWitnessRecord.BrokerOrderId"/> this record does. This
    /// answers "we submitted that identifier"; only that comparison answers "and this is the order
    /// we submitted it on".
    ///
    /// THE ONE HOLE, STATED RATHER THAN CLAIMED AWAY. "A different session" is not the same
    /// statement as "a different process". Two TradeAgent Bridge strategies started on two charts
    /// in ONE ATAS process are two sessions, and if the bridge pipe moved from one to the other
    /// mid-run — the gateway restarting is enough — the second would read the first's records as
    /// prior-session ones while the first is still loaded in the same process. The order objects
    /// would then be ones this PROCESS constructed, and the cross-session reading would be as
    /// vacuous as SameRef. It needs both bridges started and the pipe to have changed hands, and
    /// trap 24 already names two bridges as a misconfiguration; but it is a real path and it is
    /// written here rather than anywhere else, because this is the method that would be wrong.
    /// Closing it wants a process identity on the record, which is a change to the file format.
    /// </summary>
    public CoidWitnessRecord? PriorSession(string clientOrderId)
    {
        if (string.IsNullOrEmpty(clientOrderId)) return null;
        try
        {
            lock (_gate)
            {
                Ready();
                foreach (var r in _records)
                {
                    if (!string.Equals(r.ClientOrderId, clientOrderId, StringComparison.Ordinal)) continue;
                    if (string.Equals(r.SessionId, SessionId, StringComparison.Ordinal)) return null;
                    return string.IsNullOrEmpty(r.BrokerOrderId) ? null : r;
                }
                return null;
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// THE PULL PATH'S INPUT: the identifiers a previous run submitted and saw acknowledged,
    /// newest first, at most <paramref name="max"/> of them.
    ///
    /// It exists because <c>OnOrderPayload</c> is a PUSH and nothing guarantees ATAS raises an
    /// order event for an order that merely sits there after a restart. The read-back would then
    /// never be asked about the one order the experiment is about. <c>Describe()</c> runs on the
    /// handshake and on every heartbeat, so it can ask instead of waiting to be told.
    ///
    /// Newest first and bounded because that sweep runs every few seconds: the experiment is always
    /// about the most recent order, and an unbounded sweep would rescan ATAS's whole order book
    /// once per record in a file that holds <see cref="DefaultCap"/> of them.
    /// </summary>
    public IReadOnlyList<string> PriorSessionIds(int max)
    {
        if (max < 1) return [];
        try
        {
            lock (_gate)
            {
                Ready();
                var ids = new List<string>();
                for (var i = _records.Count - 1; i >= 0 && ids.Count < max; i--)
                {
                    var r = _records[i];
                    if (string.Equals(r.SessionId, SessionId, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(r.BrokerOrderId)) continue;
                    if (r.ClientOrderId.Length > 0) ids.Add(r.ClientOrderId);
                }
                return ids;
            }
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// WHETHER A ZERO HERE IS A FACT OR A FAILURE. True when something is at the path and this build
    /// could not read it, so <see cref="All"/> returning nothing means "unreadable", not "nothing
    /// was ever recorded". Those are opposite answers for this file — the second one says this
    /// product never submitted the identifier being asked about — and a reader that cannot tell them
    /// apart will report the wrong one. <c>tools/probe</c> asks this before it says "no experiment
    /// has been set up".
    /// </summary>
    public bool Unreadable
    {
        get
        {
            if (_path is null) return false;
            try { lock (_gate) { Ready(); return _readFailed; } }
            catch (Exception) { return true; }
        }
    }

    /// <summary>
    /// THE WITNESS'S TROUBLE IN ONE LINE, or null when there is none — the value that rides the
    /// hello into the ATAS bridge health row.
    ///
    /// Three states, and the middle one is the one that was invisible. A failure in THIS session is
    /// <see cref="LastWriteFailure"/>. A failure in an EARLIER one leaves nothing in memory at all:
    /// the process that saw it is gone, this one starts with a clean slate and a witness that looks
    /// perfect, and the only thing that still knows is the sidecar. Reporting only the first meant
    /// the app said READY over a witness with an unresolved durability gap. And a witness with
    /// nowhere to live at all reports so here rather than silently refusing every order, which is
    /// what it would otherwise do.
    ///
    /// It is a REPORT, not a gate on ordering: <c>Place</c> refuses per order on the write actually
    /// failing, which is the precise test. This is what makes the reason visible and what downgrades
    /// the capability — a run that cannot vouch for its own history cannot claim rule 1 is proven.
    /// </summary>
    public string? Trouble
    {
        get
        {
            if (_path is null)
                return "the write-ahead record has nowhere to live on this machine, so no client " +
                       "order id can be recorded and no order can be placed";
            try
            {
                lock (_gate)
                {
                    // ONE PREPARATION FOR EVERY READING, whichever is asked first. See Ready.
                    Ready();
                    if (_committedUnreadable) return UnreadableDetail();
                    if (_notOwned is { } contended) return contended;
                    if (LastWriteFailure is { } now) return now;
                    // A HALF-FINISHED ROTATION THAT CANNOT BE FINISHED. Reported ahead of the disk's
                    // own reading because it is the reason the disk's reading has stopped moving.
                    if (_appendRefused is { } stuck) return stuck;
                    if (_snapshotRefusal is { } why)
                        return $"the account of earlier write failures beside {ErrorLogPath} could not " +
                               $"be read ({why}), so this run cannot tell whether a durability gap is open";
                    return Degraded
                        ? $"an earlier run could not write the write-ahead record; the account of it " +
                          $"is in {ErrorLogPath}"
                        : null;
                }
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// SOMETHING WAS REFUSED OR WRITTEN DOWN, whether or not a durability gap is open. Distinct from
    /// <see cref="Trouble"/> on purpose: a quarantined leftover is not a reason to tell an operator
    /// that orders are being refused, but it IS a reason not to read a zero from this file as "this
    /// product never submitted that identifier". A reader that declined a candidate sets this
    /// without writing anything, which is what lets <c>tools/probe</c> mark its own zero provisional.
    /// </summary>
    /// <summary>
    /// A DURABILITY GAP HAPPENED AND A CLEAN COMMIT CLOSED IT — the last line that decides anything
    /// is the RESOLVED marker. Distinct from "the sidecar exists", which is what the probe used to
    /// ask: a file holding nothing but quarantine notes has never had a gap to close, and calling
    /// that "historical" tells a reader that earlier failures were resolved when there were none.
    /// </summary>
    public bool GapClosed
    {
        get
        {
            if (_path is null) return false;
            // A SAFETY EVENT IN THIS SESSION REOPENS IT whatever the files said when they were
            // read: the line that closes a gap is written when the commit lands, and until then this
            // session's own failure is the newest word there is.
            try { lock (_gate) { Ready(); return _diskGapClosed && !_degraded; } }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// EVERY SIDECAR BESIDE THE WITNESS THAT EXISTS — the canonical file, its rotated generation, and
    /// each refused writer's own. Public because a reader that prints only the canonical one reports
    /// a rejected candidate for a state that was in fact a second bridge being turned away, and the
    /// two read very differently to whoever is holding the machine.
    /// </summary>
    public IReadOnlyList<string> SidecarPaths
    {
        get
        {
            // F36: THE SAME SNAPSHOT AS EVERY OTHER READING, so this list cannot disagree with the
            // standing printed beside it. It used to run its own enumeration under its own catch, so
            // a directory this run could not look in reached the operator as an empty file list
            // under a clean headline — the report saying "none recorded" about a set it never saw.
            try { lock (_gate) { Ready(); return _sidecars; } }
            catch (Exception) { return []; }
        }
    }

    /// <summary>
    /// EVERY SIDECAR LINE THIS RUN READ, AS IT READ THEM — or the reason there was no reading.
    ///
    /// <see cref="SidecarPaths"/> hands out NAMES, and a name is an invitation to open the file
    /// again. Both renderers took it: <c>tools/probe</c> reopened each one under its own catch, and
    /// the support package enumerated the directory itself and copied what it found, swallowing
    /// <c>IOException</c> and <c>UnauthorizedAccessException</c>. So the report an operator reads
    /// and the zip an engineer opens were derived from a SECOND look — one that can disagree with
    /// the standing printed beside it, and one whose failure is INVISIBLE: a file that could not be
    /// copied is simply not in the archive, and an archive with no sidecar in it is
    /// indistinguishable from a machine that never had a durability failure.
    ///
    /// This is the snapshot itself. A renderer handed this cannot look again, and cannot fail to
    /// mention that the look failed — the refusal is a field it has to render past.
    /// </summary>
    public SidecarText Sidecars
    {
        get
        {
            if (_path is null) return SidecarText.Nothing;
            // FAIL-CLOSED, unlike SidecarPaths beside it: an exception on the way out of here must
            // not arrive at a renderer wearing the shape of an empty set, which is the very reading
            // this property exists to make impossible.
            try { lock (_gate) { Ready(); return _sidecarText; } }
            catch (Exception e) { return new SidecarText([], e.GetType().Name); }
        }
    }

    public bool Noted
    {
        get
        {
            if (_path is null) return false;
            try { lock (_gate) { Ready(); return NotedNow; } }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Why <see cref="Noted"/> is true, where this run can say. <see cref="EnsureRecovered"/> is run
    /// as well as <see cref="EnsureLoaded"/> because the recovery is the cause a READER discovers:
    /// a stranded rewrite is adopted in memory on any read path, and that adoption is the fact.
    /// </summary>
    public WitnessNotes Notes
    {
        get
        {
            if (_path is null) return WitnessNotes.None;
            try { lock (_gate) { Ready(); return _notes | _diskNotes; } }
            catch (Exception) { return WitnessNotes.None; }
        }
    }

    /// <summary>Every record on file, newest last. For the probe and for tests; not a proof path.</summary>
    public IReadOnlyList<CoidWitnessRecord> All()
    {
        try { lock (_gate) { Ready(); return _records.ToArray(); } }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The witness in one token for <c>BridgeHello.TradingSurface</c>.
    ///
    /// NO SPACE ANYWHERE IN IT. That report is a space-joined line and tools/probe splits it on
    /// spaces, so a value containing one would silently become two fields and the token after it
    /// would be unreadable.
    ///
    /// The session prefix is what lets a reader tell "the bridge has restarted since that record
    /// was written" from "you are looking at the same run that wrote it" — which is the difference
    /// between an experiment that has been performed and one that has not.
    /// </summary>
    public string Token()
    {
        var session = SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        if (_path is null) return $"session:{session},io:disabled";
        try
        {
            lock (_gate)
            {
                Ready();
                if (_readFailed) return $"session:{session},records:err,prior:err,io:failed";
                var prior = 0;
                foreach (var r in _records)
                    if (!string.Equals(r.SessionId, SessionId, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(r.BrokerOrderId)) prior++;
                // THREE STATES, NOT TWO, and the middle one is the whole point of it. "failed"
                // is this session unable to write. "degraded" is a durability gap recorded in the
                // sidecar — most usefully one from a session that has already ended, which is
                // otherwise invisible: the next run starts with a clean LastWriteFailure and a
                // witness that looks perfect. The field name and the shape of the token are
                // unchanged, so a probe splitting on spaces reads it exactly as before.
                // FOUR STATES. "failed" is this session unable to write. "degraded" is an
                // UNRESOLVED SAFETY line — a claim or an acknowledgement that did not reach the
                // disk, most usefully one from a session that has already ended. "noted" is a
                // sidecar with diagnostics in it and no open gap, which is what makes a zero here a
                // flagged zero rather than a confident one. Only "degraded" reaches Trouble.
                var io = _writeFailed ? "failed" : Degraded ? "degraded" : NotedNow ? "noted" : "ok";
                return $"session:{session},records:{_records.Count},prior:{prior},io:{io}";
            }
        }
        catch (Exception) { return $"session:{session},records:err,prior:err,io:failed"; }
    }

    // ---------------------------------------------------------------- the file

    /// <summary>
    /// EVERY PUBLIC MEMBER RUNS THIS, AND THAT IS THE WHOLE OF R9-2. The load reads the committed
    /// file and takes the snapshot; the recovery adopts a stranded rewrite in memory and is a cause
    /// of <see cref="Noted"/> in its own right. Round 9 had properties running one, the other, or
    /// both, so a fresh instance answered <c>Noted=false</c> while another answered <c>io:noted</c>
    /// about the same machine, and the operator's sentence came out right only because C# evaluates
    /// arguments left to right. No reading may depend on another having run first, so they all run
    /// the same two steps in the same order.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void Ready()
    {
        EnsureLoaded();
        EnsureRecovered();
        // AND A SNAPSHOT THAT IS STILL CURRENT. The load runs once; the files do not stop moving
        // when it has. Anything this session appends invalidates the snapshot, and this is where the
        // next one is taken — so the reading after a write is of the file as it is now and not of
        // the set as it was at construction.
        Snapshot();
    }

    /// <summary>
    /// A DURABILITY GAP IS OPEN — what the files say, together with what this session has done. The
    /// two halves are separate fields for the reason on <see cref="_degraded"/>: a safety line this
    /// session wrote counts even when the append that would have recorded it failed, and everything
    /// else comes from one snapshot so that no two readings can disagree. Caller holds the gate and
    /// has run <see cref="Ready"/>.
    /// </summary>
    bool Degraded => _diskDegraded || _degraded;

    /// <summary>Something is written down, from any of the three kinds of cause.</summary>
    bool NotedNow => _diskNoted || _noted;

    /// <summary>
    /// Loaded once, then held. The records a PREVIOUS session wrote cannot change — the process
    /// that would change them is gone — and this session's own records are written through here,
    /// so memory is authoritative for everything this instance can legitimately answer about.
    ///
    /// It also keeps the read-back off the disk. <c>ProveClientOrderId</c> consults
    /// <see cref="PriorSession"/> for every order event naming an id this session did not submit;
    /// re-reading a file on each of those would put disk IO on ATAS's event thread.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void EnsureLoaded()
    {
        if (_loaded) return;
        if (_path is null) { _loaded = true; return; }

        // ONE SNAPSHOT, TAKEN HERE, DERIVED FROM ONCE. Everything the sidecar files say — whether
        // anything is written down, whether a durability gap is open, whether it was closed, which
        // files are there, who wrote them, and whether any of that could be read at all — is
        // computed in Derive from this one value. There is no second read to disagree with it and
        // no probe left for a later edit to add.
        var snapshot = Snapshot();

        // AND AN ENUMERATION THIS RUN COULD NOT PERFORM IS NOT AN EMPTY DIRECTORY, on the recovery
        // path either: with no listing there is no way to know whether a rewrite is stranded beside
        // the witness, and a zero read out of that is the one answer that must never be produced by
        // accident.
        if (snapshot.Refusal is not null) _candidateUnreadable = true;

        // ONE PREDICATE FOR "THIS BUILD DOES NOT HAVE THE COMMITTED CONTENT", and it covers both
        // ways of not having it. ABSENT is exactly FileNotFound on this path — the file has never
        // been written — and that is the only outcome that lets a write proceed against nothing.
        // Every other outcome is UNREADABLE: the read was refused (a scanner, an ACL, a disk error)
        // or the bytes came back and are not an envelope. Those were two predicates and the I/O one
        // was missing, so a denied read looked like an empty directory: lineage reset to generation
        // 0 with no predecessor, the compare-and-swap compared null against null and passed, and the
        // rewrite replaced a file of acknowledged claims with the new claim alone.
        var committedText = ReadTolerantly(_path, out var unreadable);
        var committed = committedText is null ? null : Parse(committedText);
        if (committedText is not null && committed is null) unreadable = true;

        // The lineage of what is committed, fixed before anything is adopted: every rewrite this
        // instance writes will name these, and every rewrite it adopts has to name them already.
        _committedHash = committedText is null ? null : Fingerprint(committedText);
        _generation = committed?.Generation ?? 0;

        // THE REWRITE THAT NEVER LANDED, PREFERRED WHEN IT DESCENDS FROM WHAT IS COMMITTED. Without
        // this the claim in an uncommitted temp is invisible to every reader forever, which is the
        // failure this file was rewritten to close: on a contended Windows machine a replace is
        // refused, the newer state stays in the temp, the process ends, and the durable answer to
        // "did this product submit this identifier" becomes NO for an identifier that was handed to
        // ATAS microseconds later. See AdoptUncommittedRewrite for the rule.
        // CLASSIFIED, NOT ACTED ON. See _viable / _rejected: this runs on read paths, and a reader
        // that wrote the sidecar or moved a file could do it in the middle of the owner's rewrite.
        ScanCandidates(committedText, committed, snapshot);

        // A truncated or hand-edited file is not a crash and is not evidence either. Treat it as
        // unreadable — the token says so — and let this session write a clean one. The records lost
        // were claims about orders from runs that have already ended.
        //
        // THE READ FAILED WHEN NOTHING READABLE WAS FOUND AND SOMETHING ON DISK COULD NOT BE READ,
        // and both halves of that are load-bearing. A truncated TEMP beside an intact committed file
        // is not a failed read — the records are all there, and saying records:err would report a
        // healthy witness as broken. But a truncated temp with NO committed file beside it used to
        // report records:0, io:ok — a confident zero, which for this file means "this product never
        // submitted that identifier". That is the one answer that must never be produced by
        // accident, and it was being produced by a file that plainly had something in it.
        _readFailed = (unreadable || _candidateUnreadable) && committed is null;

        // BYTES THIS BUILD COULD NOT READ ARE BYTES IT MUST NOT OVERWRITE. Something is at the path
        // and it is not an envelope: a truncated write, a hand edit, another product's file. Writing
        // over it destroys whatever it holds, and this run cannot say what that was — so every write
        // is refused while it stands and Trouble says why. It is a state and not a latch: repair or
        // remove the file and the next start works.
        _committedUnreadable = unreadable;

        if (committed is not null) Take(committed);

        // LOADED ONLY ONCE THE RECORDS ARE ACTUALLY IN MEMORY. Setting this first meant that an
        // exception anywhere above left the instance believing it had loaded — with an empty list,
        // no read failure, and a Submitting that would go on to replace the file it never read.
        _loaded = true;
    }

    /// <summary>
    /// AN ENVELOPE, OR NULL WHEN THE TEXT IS NOT ONE — AND "IS NOT ONE" IS A QUESTION ABOUT MEANING,
    /// NOT ABOUT SYNTAX.
    ///
    /// This used to ask only whether the JSON deserialised. <c>records:[null, A]</c> does. Iterating
    /// it then throws on the null before it reaches A, the public reader swallows the exception, and
    /// the instance is left LOADED with an empty list and no read failure recorded — a confident
    /// zero, which for this file means "this product never submitted that identifier". Worse, the
    /// next <see cref="Submitting"/> skips loading (it is loaded) and replaces the anchor with a file
    /// holding one claim: A's record, which was in the bytes the whole time, is gone.
    ///
    /// What is checked is what the rest of this class relies on: a version it can read, a
    /// non-negative generation, a record list that exists, no null elements, no empty identifiers,
    /// and no identifier twice — <see cref="PriorSession"/> answers with the first record it meets,
    /// so a duplicate makes the answer a property of the file's byte order rather than of this
    /// machine's history. Nothing this build writes can fail any of these.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    static Envelope? Parse(string json)
    {
        Envelope? envelope;
        // ANY exception, not just JsonException. Deserialize also throws NotSupportedException for
        // shapes the converter cannot handle, and one escaping here reaches EnsureLoaded, whose
        // caller swallows it — leaving an instance that believes it loaded a file it never read.
        try { envelope = JsonSerializer.Deserialize<Envelope>(json, Opts); }
        catch (Exception) { return null; }
        if (envelope is null || envelope.Version < 1 || envelope.Generation < 0) return null;

        var records = envelope.Records;
        if (records is null) return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r is null || string.IsNullOrEmpty(r.ClientOrderId)) return null;
            if (!ids.Add(r.ClientOrderId)) return null;
        }
        return envelope;
    }

    /// <summary>Copies an envelope's records in. Caller holds <see cref="_gate"/>.</summary>
    void Take(Envelope envelope)
    {
        foreach (var r in envelope.Records)
            if (!string.IsNullOrEmpty(r.ClientOrderId)) _records.Add(r);
    }

    /// <summary>
    /// THE RECOVERY RULE, AND IT IS LINEAGE RATHER THAN TIME.
    ///
    /// An uncommitted temp is adopted ONLY when it is provably the rewrite that this exact committed
    /// file was about to become. Three conditions, all of them required:
    ///
    ///   1. IT HAS RECORDS. An envelope deserialises with <c>Records</c> defaulting to an empty
    ///      list, so a file containing <c>{}</c> — or a legitimately empty rewrite — parses
    ///      perfectly and says nothing. Adopting one would shadow a good committed file with a void
    ///      and the next <see cref="Save"/> would COMMIT that void: permanent loss of every claim,
    ///      caused by the recovery meant to prevent loss. Zero records is never adopted.
    ///
    ///   2. ITS PREDECESSOR IS THE COMMITTED CONTENT. <see cref="Envelope.Predecessor"/> must equal
    ///      the fingerprint of the committed file's exact bytes. This is what makes it descent
    ///      rather than resemblance.
    ///
    ///   3. ITS GENERATION IS THE NEXT ONE. Exactly <c>committed.Generation + 1</c> — or exactly 1
    ///      with no predecessor, when nothing is committed at all.
    ///
    /// WHY TIME IS NOT IN THAT LIST ANY MORE, and this is the correction that matters. A rule that
    /// adopted "the newest temp" was wrong in both directions. An OLDER envelope preserved with a
    /// later mtime — a backup tool, a copy, a hand-restored file — resurrects identifiers that
    /// <see cref="Trim"/> removed, and those go straight into <see cref="PriorSessionIds"/>, then
    /// into the cross-session reading, and set SupportsClientOrderId TRUE from state that is not in
    /// the committed file at all. And equal timestamps or a clock that went backwards would refuse a
    /// perfectly good recovery. Under lineage neither is possible: a preserved older envelope does
    /// not descend from the current commit whatever its mtime says, and a genuine failed rewrite
    /// does whatever its mtime says. Time is now used ONLY to order candidates, never to qualify
    /// one.
    ///
    /// A REJECTED CANDIDATE IS REPORTED, not silently skipped — a temp lying beside the witness that
    /// does not descend from it is a fact somebody needs. It goes in the sidecar with the reason.
    ///
    /// It writes nothing else: the adopted content is not committed here. A reader may be
    /// <c>tools/probe</c> or <see cref="PriorSession"/> on ATAS's event thread, and committing from
    /// a read path would race the writer that owns the temp. Convergence happens on this session's
    /// next <see cref="Save"/>, which serialises the adopted records back out under the next
    /// generation and renames over the top.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void ScanCandidates(string? committedText, Envelope? committed, SidecarSnapshot snapshot)
    {
        foreach (var candidate in Candidates(snapshot))
        {
            // OUT OF THE SNAPSHOT, NOT OFF THE DISK. The name and the bytes come from the same
            // reading, inside the window that refuses a set which will not hold still — so the
            // content this decision rests on is the content the change detection covered.
            var (text, unreadable) = snapshot.Candidate(candidate);
            if (text is null)
            {
                if (unreadable) { _candidateUnreadable = true; _rejected.Add((candidate, "it could not be read")); }
                continue;
            }

            var envelope = Parse(text);
            if (envelope is null)
            {
                _candidateUnreadable = true;
                _rejected.Add((candidate, "it is not a witness envelope"));
                continue;
            }
            if (envelope.Records.Count == 0) { _rejected.Add((candidate, "it contains no records")); continue; }
            if (!DescendsFrom(envelope, committedText, committed))
            {
                _rejected.Add((candidate, committedText is null
                    ? "there is no committed witness file for it to be a rewrite OF, so nothing " +
                      "anchors it to this machine's own history"
                    : $"it does not descend from the committed file " +
                      $"(temp generation={envelope.Generation} predecessor={envelope.Predecessor ?? "<none>"}; " +
                      $"committed generation={(committed is null ? "<unreadable>" : committed.Generation.ToString())} " +
                      $"fingerprint={Fingerprint(committedText)})"));
                continue;
            }

            // LINEAGE AUTHENTICATES THE PARENT, NOT THE CONTENT, and that gap is real. A rewrite
            // that descends perfectly well from the committed file can still be missing records it
            // had — and adopting one drops committed claims, up to and including resurrecting an
            // identifier the cap had trimmed, since the adopted set becomes what the next save
            // commits. See IllegalTransition for what one rewrite of this file can legally do
            // means once Trim is in the picture.
            if (committed is not null && IllegalTransition(envelope, committed) is { } illegal)
            {
                _rejected.Add((candidate,
                    $"it is not one rewrite of the committed file ({illegal}), so adopting it " +
                    $"would drop or invent claims"));
                continue;
            }

            _viable.Add((candidate, envelope));
        }

        // A REFUSAL IS FLAGGED EVEN BY A READER THAT WRITES NOTHING, and this is what keeps a zero
        // from reading as a confident one. The owner also puts the reason in the sidecar; a reader
        // must not, but it still KNOWS it refused something, and `records:0,io:ok` from a directory
        // holding a candidate this build declined is the one answer that must never be produced by
        // accident — for this file it means "this product never submitted that identifier".
        //
        // A clean single recovery is not flagged: nothing was refused.
        if (_rejected.Count > 0 || _viable.Count > 1)
        {
            _noted = true;
            _notes |= WitnessNotes.RejectedCandidate;
        }
    }

    /// <summary>
    /// ACTING ON THE SCAN IN MEMORY ONLY — SAFE FOR ANY READER, IN ANY PROCESS, AT ANY TIME, because
    /// it changes no file. Caller holds <see cref="_gate"/>.
    ///
    /// TWO RIVALS MEAN NEITHER IS TRUSTED. Every viable candidate descends from the same commit and
    /// therefore carries the same generation, so nothing in the files distinguishes them. One writer
    /// cannot produce this — it keeps at most one uncommitted rewrite — so it means a copied file or
    /// a writer that is not this build, and guessing is how a claim gets dropped without anybody
    /// being told.
    /// </summary>
    void AdoptInMemory()
    {
        if (_adoptedAlready) return;
        _adoptedAlready = true;

        // TWO RIVALS MEAN NEITHER IS TRUSTED — nothing in the files distinguishes them.
        if (_viable.Count != 1) return;

        // A MERGE, NOT A REPLACEMENT. The candidate may only fill in the half this product did not
        // write, on a claim the committed file already carries. It cannot add an identifier (that
        // would be a write-ahead record for an order Place refused to send), it cannot remove one,
        // and it cannot revise a broker id that is already recorded — a broker id does not change
        // once assigned, so a second value means the file is being written by something else.
        //
        // AND IT CANNOT COMPLETE ANOTHER SESSION'S CLAIM. Identified refuses to write into a record
        // belonging to a different session, because an order found in ATAS's book carrying a prior
        // session's comment would otherwise write its own id into that record and match itself.
        // Recovery must not be the way around that refusal, so the sessions have to agree.
        var recovered = 0;
        foreach (var candidate in _viable[0].Envelope.Records)
        {
            if (string.IsNullOrEmpty(candidate.BrokerOrderId)) continue;

            var i = _records.FindIndex(r => string.Equals(r.ClientOrderId, candidate.ClientOrderId,
                                                          StringComparison.Ordinal));
            if (i < 0) continue;
            if (!string.IsNullOrEmpty(_records[i].BrokerOrderId)) continue;
            if (!string.Equals(_records[i].SessionId, candidate.SessionId, StringComparison.Ordinal)) continue;

            _records[i] = _records[i] with
            {
                BrokerOrderId = candidate.BrokerOrderId,
                IdentifiedAt = candidate.IdentifiedAt
            };
            recovered++;
        }

        // Only a candidate that actually gave something is worth committing over and deleting — and
        // one that gave nothing is SPENT rather than pending, which ReportAndQuarantine acts on.
        _recovered = recovered;
        if (recovered > 0)
        {
            _adopted = _viable[0].Path;
            _notes |= WitnessNotes.RecoveredRewrite;
            // AND A READER SAYS SO TOO. A WRITER reaches Noted for this state through the sidecar
            // line ReportAndQuarantine writes; a reader writes nothing, so it reported a machine
            // whose record had just been repaired as Clean — two readings of one machine
            // disagreeing, which is the shape this unit keeps finding. The count below now rests on
            // a file that had to be repaired to produce it, and that is what Noted is for.
            _noted = true;
        }
    }

    /// <summary>
    /// THE HALF THAT TOUCHES THE DISK, AND IT BELONGS TO THE OWNER ALONE. Caller holds
    /// <see cref="_gate"/> AND the lease — that is what makes it safe to move files and write the
    /// sidecar, because the writer whose rewrite these candidates might be cannot be running.
    ///
    /// It is separate from <see cref="AdoptInMemory"/> because a READER has to answer correctly
    /// about a stranded rewrite — that is the whole point of the recovery — while changing nothing.
    /// Adoption into this instance's own list is invisible outside the process; quarantining a file
    /// and writing a sidecar line are not, and a reader that did them was making a diagnostic run
    /// (`tools/probe`, on a machine where the bridge is not running) into a witness-modifying event.
    /// </summary>
    void ReportAndQuarantine()
    {
        if (_reported) return;
        _reported = true;

        foreach (var (path, why) in _rejected)
        {
            var moved = Quarantine(path);
            Note(moved is null
                ? $"ignored {path}: {why}"
                : $"ignored {path}: {why} — moved to {System.IO.Path.GetFileName(moved)}");
        }
        _rejected.Clear();

        if (_viable.Count > 1)
            Note($"WARN coid-witness found {_viable.Count} rival uncommitted rewrites of generation " +
                 $"{_viable[0].Envelope.Generation} and adopted none of them: " +
                 string.Join(", ", _viable.Select(v => v.Path)));
        else if (_viable.Count == 1 && _recovered > 0)
            Note($"coid-witness recovered an uncommitted rewrite (generation {_viable[0].Envelope.Generation}, " +
                 $"{_recovered} acknowledgement(s)) from {_viable[0].Path}");
        else if (_viable.Count == 1)
        {
            // NOTHING WAS TAKEN FROM IT, SO IT IS SPENT. A legal transition that restates a broker id
            // already on file, or belongs to another session, or carries no acknowledgement at all,
            // is neither adopted nor rejected — and was therefore left where it was and re-declared
            // "recovered" by every session that followed. It is moved out of the candidate glob like
            // any other leftover: kept, not deleted, and looked at once.
            var spent = _viable[0].Path;
            var moved = Quarantine(spent);
            Note(moved is null
                ? $"ignored {spent}: it carries nothing this witness does not already have"
                : $"ignored {spent}: it carries nothing this witness does not already have — " +
                  $"moved to {System.IO.Path.GetFileName(moved)}");
        }

        _viable.Clear();
    }

    /// <summary>
    /// RECOVERY FOR A READ PATH, AND IT NEVER TAKES THE LEASE. It used to take the lock
    /// opportunistically and treat getting it as being the owner, so a reader over a witness nobody
    /// happened to own quarantined temps, created the lock file and wrote the sidecar —
    /// <c>tools/probe</c> being precisely a thing an operator runs while the bridge is NOT running.
    ///
    /// A reader still has to ANSWER correctly about a stranded rewrite, so it adopts in memory,
    /// which nothing outside this process can observe. Caller holds <see cref="_gate"/>.
    /// </summary>
    void EnsureRecovered()
    {
        if (_path is null) return;
        AdoptInMemory();
    }

    /// <summary>
    /// WHETHER A CANDIDATE IS A LEGAL TRANSITION FROM THE COMMITTED STATE — or the reason it is not.
    /// Null means it is one rewrite of this file and may be merged.
    ///
    /// A TEMP IS NEVER A NEW CLAIM, and that single rule is what this collapsed to. Since round 2
    /// <c>Place</c> refuses the order when <see cref="Submitting"/> returns false, so a claim that is
    /// in a temp and not in the committed file is, by that contract, a submission THAT DID NOT
    /// HAPPEN — no order carrying that identifier was ever handed to ATAS. Recovering it writes a
    /// write-ahead record for an order this product never submitted, and nothing afterwards can tell
    /// that record from a real one; at the cap it also evicts a genuine committed claim to make room
    /// for itself. Recovery cannot distinguish a failed SUBMISSION temp from a failed
    /// ACKNOWLEDGEMENT temp by inspection — both are "the rewrite that did not land" — so the rule
    /// is stated rather than inferred.
    ///
    /// What is left for a legal candidate is therefore exactly the committed identifiers, no more
    /// and no fewer, differing only in the half this product did not write. Which is why the
    /// arithmetic that used to live here — never shrinks, adds at most one, drops at most the oldest
    /// at the cap — is gone with the case it existed for. That also removes this rule's dependence on
    /// <see cref="_cap"/>, and with it the cross-cap upgrade asymmetry the round-4 record described
    /// backwards: no candidate that trims is adoptable by anybody's cap any more.
    ///
    /// Uniqueness is <see cref="Parse"/>'s job, on both sides of this comparison, so identifier
    /// counts here are set sizes.
    /// </summary>
    static string? IllegalTransition(Envelope candidate, Envelope committed)
    {
        if (candidate.Records.Count != committed.Records.Count)
            return $"it holds {candidate.Records.Count} records against the committed file's " +
                   $"{committed.Records.Count}, and a rewrite that did not land can only carry the " +
                   $"committed claims";

        var ids = new HashSet<string>(candidate.Records.Select(r => r.ClientOrderId), StringComparer.Ordinal);
        foreach (var r in committed.Records)
            if (!ids.Contains(r.ClientOrderId))
                return $"{r.ClientOrderId} is committed and not in it";

        return null;
    }

    /// <summary>
    /// Whether one envelope is the rewrite THIS committed content was about to become. See
    /// <see cref="AdoptUncommittedRewrite"/> for the argument; this is only the arithmetic.
    ///
    /// NO ANCHOR, NO ADOPTION, and this is the correction round 3 forced. With no committed file
    /// there is nothing to be descended FROM, so "generation 1 and no predecessor" was not a lineage
    /// test at all — it was a shape test, and any fragment of any other witness's history satisfies
    /// it. That fragment's records are acknowledged, so they walk into <see cref="PriorSessionIds"/>,
    /// the cross-session reading, and SupportsClientOrderId: a capability set true out of a file
    /// this product never wrote. Round 2 narrowed that branch and round 3 removes it, because it has
    /// no legitimate case left: since <c>Place</c> refuses any order whose write-ahead record did
    /// not land, "the first write failed and an order went out anyway" cannot happen. A first
    /// rewrite that never landed protects nothing, so nothing is lost by declining to trust it.
    ///
    /// AND THE ANCHOR HAS TO PARSE. The awkward middle case is a committed file that EXISTS but is
    /// not an envelope. Its generation is then unknowable, and this used to fall back to the
    /// fingerprint by itself, on the argument that the fingerprint is over exact bytes and is the
    /// stronger of the two checks. It is — over bytes that mean something. Corrupt bytes are not a
    /// history this file has, they are a file that has to be replaced, and a temp claiming descent
    /// from them was adopted whatever generation it named: its acknowledged identifiers reached
    /// <see cref="PriorSession"/> while the witness was reporting, at the same moment, that it could
    /// not be read. Both halves of the lineage or no adoption.
    /// </summary>
    static bool DescendsFrom(Envelope temp, string? committedText, Envelope? committed)
    {
        if (committedText is null || committed is null) return false;
        if (!string.Equals(temp.Predecessor, Fingerprint(committedText), StringComparison.Ordinal)) return false;
        return temp.Generation == committed.Generation + 1;
    }

    /// <summary>
    /// Temps that might be an uncommitted rewrite of this file, in whatever order the directory
    /// gives them. <see cref="DescendsFrom"/> decides which one is real, and
    /// <see cref="AdoptUncommittedRewrite"/> declines when more than one qualifies.
    ///
    /// R9-5: OUT OF THE SNAPSHOT, LIKE EVERYTHING ELSE. This used to run its own
    /// <c>Directory.GetFiles</c> under a catch that returned an empty list, so a refused enumeration
    /// reached the RECOVERY path as "there is no stranded rewrite here" — the same conflation the
    /// read paths were fixed for, one glob over, in the same directory. It is now the same
    /// enumeration as the sidecar set's, so it cannot answer where that one refused.
    ///
    /// UNORDERED, AND DELIBERATELY SO. It used to be newest-first, because mtime picked the winner
    /// among several. It no longer picks anything: a candidate qualifies on lineage alone, and two
    /// that both qualify are declined rather than ranked.
    /// </summary>
    IReadOnlyList<string> Candidates(SidecarSnapshot snapshot) => snapshot.Candidates;

    /// <summary>
    /// A REJECTED CANDIDATE IS MOVED, NOT JUST LOGGED, AND SAID ONCE.
    ///
    /// One crash mid-rewrite used to degrade the witness permanently: the rejected candidate stayed
    /// where it was, every later session rejected it again, every rejection wrote another sidecar
    /// line, and the sidecar's mere existence was what made the witness look degraded. Renaming it
    /// out of the candidate glob ends that after exactly one report, and keeps the file for whoever
    /// wants to look at it rather than deleting evidence.
    ///
    /// AND WHY A YOUNG ONE IS LEFT ALONE. A candidate written seconds ago may be a rewrite in
    /// flight, between its write and its rename. Renaming that would make the replace fail. The lock
    /// makes this nearly unreachable now — recovery only runs while nothing else can be writing —
    /// but the grace costs one comparison and covers a writer that is not this build.
    /// </summary>
    /// <summary>
    /// ROTATION IS ATOMIC RENAMES OVER A SNAPSHOT, AND THE CARRIED LINE IS WRITTEN FIRST.
    ///
    /// THREE THINGS IT WILL NOT DO, and each of them was a finding.
    ///
    /// It will not rotate what it cannot read (R9-1 / F34). <c>LastDecidingLine</c> used to answer
    /// null both for "nothing unresolved anywhere" and for "every generation that could have
    /// answered threw", and the second reading ran two <c>File.Delete</c> calls over files this run
    /// had never read — measured with a real <c>chmod 000</c> and a real <c>SIGKILL</c>: the marker
    /// gone from every file, <c>Trouble</c> null, and the gateway still trading fully automatically.
    /// Now the snapshot is either complete or <c>Unreadable</c>, and an unreadable one does not
    /// rotate at all. The log then grows past its cap, which is the direction to fail in: a bounded
    /// file is a convenience and a safety event is not.
    ///
    /// It will not decide from somebody else's file (R9-4). The carry comes from the generations OF
    /// THE FILE BEING ROTATED, so a writer the lease refused rotates on its own unresolved line
    /// rather than restating the canonical machine's gap into its own file and deleting its own
    /// history to make room for it.
    ///
    /// And it will not destroy anything before the replacement is on the disk. The new current log
    /// is built under <see cref="PendingSuffix"/> — a name inside the reader's glob — with the
    /// carried line FIRST and <c>Flush(flushToDisk: true)</c>, and only then do the generations
    /// move, oldest first. There is no staging file and no branch: one path, four acts, and every
    /// state a crash can leave behind is a SUBSET of the files a reader already reads. That is the
    /// whole class-closure argument, and it is a sentence rather than a table of interleavings:
    ///
    ///   1. write <c>log.new</c> — carry line first, flushed;
    ///   2. <c>log.1</c> → <c>log.2</c>, which is the one act that removes a generation, and by then
    ///      that generation's deciding line, if it was the last one, is already in <c>log.new</c>;
    ///   3. <c>log</c> → <c>log.1</c>;
    ///   4. <c>log.new</c> → <c>log</c>.
    ///
    /// Caller is inside <see cref="AppendToErrorLog"/>'s try, so a failure here is reported the same
    /// way any other sidecar failure is: not at all, because a witness that cannot write must never
    /// become one that throws. A rotation that stops half way leaves <c>log.new</c> holding the
    /// carried line, which the next reader reads and the next rotation consumes.
    /// </summary>
    /// <summary>
    /// THE ROTATION IS RESUMABLE, AND WITHOUT THIS IT WAS NOT.
    ///
    /// The four acts end with <c>log.new → log</c>, so the state left by the LAST one failing is
    /// the current log GONE and <c>log.new</c> holding everything — the carried unresolved line and
    /// nothing else. Every retry then started from a missing current: the append recreated <c>log</c>
    /// as a fresh empty file beside an orphaned <c>log.new</c>, and the next rotation moved the
    /// generations along underneath both. On Windows that is not an exotic state — the last act is a
    /// rename onto a name a scanner or an indexer may be holding, which is the one failure
    /// <see cref="_replace"/> exists to describe — and the retry loop above turned it into four.
    ///
    /// So the completion is finished before anything else is written, and it is IDEMPOTENT: the
    /// condition is a property of the disk, not of a flag, so a fresh process that starts on a
    /// half-rotated set finishes the previous process's rotation on its first append.
    ///
    /// AND WHEN IT CANNOT BE FINISHED THE APPEND IS REFUSED, LOUDLY. Appending to a current log that
    /// does not exist would create one that the completion is going to overwrite, so the line would
    /// be lost by the very act that repairs the set — silently, on the path whose entire job is to
    /// leave a record. Refusing and degrading says so instead: see <see cref="_appendRefused"/>.
    ///
    /// Returns whether the append may proceed. Caller holds <see cref="_gate"/>.
    /// </summary>
    bool Resume(string log)
    {
        var snapshot = Snapshot();

        // NO SNAPSHOT, NO CLAIM ABOUT THE SET. An unreadable set is already degraded and already
        // stops the rotation (below); inventing a completion over files this run has not read is
        // the R9-1 shape, and refusing every append over a transient denial is worse than a log
        // that grows.
        if (snapshot.Refusal is not null) return true;

        // THE REFUSAL IS CLEARED BY THE SET BEING WHOLE, whoever made it whole. It is a sentence in
        // the present tense — "no further event can be recorded there" — so leaving it standing over
        // a set that has since come back together would be a false one. What does NOT clear is
        // _degraded: events WERE lost, and that is a fact about this session that stays.
        var pending = log + PendingSuffix;
        if (!snapshot.Sidecars.Contains(pending, StringComparer.Ordinal)) { _appendRefused = null; return true; }
        if (snapshot.Sidecars.Contains(log, StringComparer.Ordinal)) { _appendRefused = null; return true; }

        try
        {
            _moveSidecar(pending, log, false);
            _appendRefused = null;
            Invalidate();
            return true;
        }
        catch (Exception e)
        {
            _appendRefused =
                $"a sidecar rotation beside {log} stopped after the current log was rolled aside: " +
                $"{pending} holds it and cannot be moved back ({e.GetType().Name}: {e.Message}), so " +
                $"no further engineering event can be recorded there";
            _degraded = true;
            Invalidate();
            return false;
        }
    }

    /// <summary>
    /// Returns whether the append that asked for this rotation may proceed. False only when the set
    /// is half rotated and cannot be put back together — see <see cref="Resume"/>: appending into a
    /// current log that does not exist writes the line into a file the completion will overwrite.
    /// </summary>
    bool Rotate(string log)
    {
        var snapshot = Snapshot();

        // A ROTATION THAT CANNOT READ WHAT IT ROTATES DOES NOT ROTATE. The count is reset so the
        // attempt is made again after another cap's worth rather than on every single append: a
        // denial is usually transient, and re-reading the whole set per line would turn one refused
        // read into a permanent cost.
        if (snapshot.Refusal is not null) { _sidecarBytes = 0; return true; }

        // A ROTATION STARTS FROM A WHOLE SET. If the previous one stopped at its last act this
        // finishes it, and the snapshot below is retaken over the completed set; if it cannot be
        // finished there is nothing to rotate and the caller has already been refused.
        if (!Resume(log)) return false;
        snapshot = Snapshot();
        if (snapshot.Refusal is not null) { _sidecarBytes = 0; return true; }

        var deciding = DecidingIn(snapshot, Generations(log));
        var carry = deciding.IsUnresolved ? Restatement(deciding.Line!) : "";

        var pending = log + PendingSuffix;
        var rolled = log + RolledSuffix;
        var second = log + SecondSuffix;

        // ACT 1 — THE CARRY, INTO A NAME NOTHING ELSE HAS, AND THEN ONTO `.new` IN ONE OPERATION.
        //
        // THE FIFTH CRASH POINT. This used to open `log.new` itself with `FileMode.Create`, so the
        // first thing act 1 did was EMPTY it — and `log.new` is exactly the file a rotation that
        // stopped at crash point 1 left the only copy of the unresolved marker in. One transient IO
        // error between the open and the write and that copy was gone; the retry then recomputed the
        // carry from the emptied file, found nothing unresolved, and rotated a clean-looking set. A
        // durability gap became invisible by way of a failed write.
        //
        // So nothing that is already there is ever truncated. An existing `log.new` is either
        // COMPLETED (Resume, above) or READ as part of the snapshot the carry is computed from, and
        // the replacement is built under a name unique to this attempt — unique to the ATTEMPT, so a
        // retry does not reuse a name its predecessor may have left occupied — and moved onto
        // `log.new` in one operation. Every failure before that move leaves the set exactly as the
        // snapshot found it, which is why the retry's own snapshot agrees with this one.
        //
        // The temp is inside the reader's glob, so an attempt that dies between the write and the
        // move leaves a file a reader reads rather than one nobody sees. It is not one of the five
        // GENERATIONS: its content is a restatement of a line that is still in the set, so nothing
        // is lost by not deciding from it, and a stale one cannot resurrect a gap that was closed.
        var temp = $"{pending}-{Environment.ProcessId}-{_session8}-{++_rotations}";
        _writeSidecar(temp, carry);
        _moveSidecar(temp, pending, true);

        // FROM HERE THE SET CAN BE HALF MOVED, so this run stops believing its own byte count. A
        // negative count is what sends the next append through Resume with a fresh snapshot — which
        // is how the four acts become resumable rather than merely ordered.
        _sidecarBytes = -1;

        // THE OLDEST GENERATION LEAVES IN ONE ACT, and it leaves after the carry is on the disk. As
        // an atomic rename rather than a delete followed by a move, so there is no instant at which
        // neither `.1` nor `.2` exists — the reader's set is a superset of the previous one at every
        // step, which is what makes the crash argument a subset argument.
        if (snapshot.Sidecars.Contains(rolled, StringComparer.Ordinal))
            _moveSidecar(rolled, second, true);

        _moveSidecar(log, rolled, false);
        _moveSidecar(pending, log, false);

        _sidecarBytes = ByteCount(carry);
        Invalidate();
        return true;
    }

    /// <summary>The one wording for a carried-forward failure, so both places that write it agree.</summary>
    string Restatement(string carry) =>
        $"{DateTimeOffset.UtcNow:O} {SafetyPrefix}coid-witness carried an unresolved failure across a " +
        $"sidecar rotation: {OneLine(carry)}" + Environment.NewLine;

    // ================================================================ THE ONE READER

    /// <summary>
    /// THE ONLY CODE IN THIS CLASS THAT READS THE SIDECAR FILESYSTEM. Everything else — the notes,
    /// the deciding line, the file list, the degraded state, the report, the probe, the support
    /// package, the recovery glob and ROTATION — is handed what this returns.
    ///
    /// WHY IT IS ONE FUNCTION (§9.10). Rounds 6 to 9 closed "a file I could not read is not a file
    /// with nothing in it" seven times, at seven different call sites: F17 and PRIOR 17 on the
    /// committed read, F28 on the sidecar read, F31 and PRIOR 28 on the three <c>File.Exists</c>
    /// probes in front of it, F33 on the second read of the same file, F36 on
    /// <see cref="SidecarPaths"/>, F37 on the report's wording, R9-1 and F34 inside
    /// <see cref="Rotate"/> — where the wrong answer is a <c>File.Delete</c> rather than a wrong
    /// sentence — and R9-5 on the recovery glob. Each fix was right where it stood and the next
    /// reviewer found the site beside it, because the conflation was reachable from anywhere that
    /// could call the filesystem. So the filesystem is callable from exactly one place, that place
    /// returns a VALUE the caller has to handle, and the class-closure argument stops being an
    /// enumeration of call sites: there is one.
    ///
    /// ONE <c>try</c>, AROUND EVERYTHING. Enumerating, stat-ing, opening, reading, a file that
    /// vanishes mid-read, a directory at a sidecar's name, a denied <c>readdir</c>, a
    /// <see cref="DirectoryNotFoundException"/> for a bridge folder that is gone — every exception
    /// of every type at every step is the same answer, <c>Unreadable</c>, because they are the same
    /// news: this run does not know what is beside the witness. There is no exception filter to
    /// forget to widen and no second catch to disagree with the first.
    ///
    /// ABSENCE IS STILL ABSENCE, and it is the enumeration that says so rather than an exception: a
    /// name the listing does not contain is a name with nothing at it, and a directory that listed
    /// cleanly and held nothing is a clean-empty sidecar set. Both directions matter — a machine
    /// that has never had a failure must not report one.
    ///
    /// AND A SET THAT IS CHANGING UNDER THE READ WAS NEVER READ (PRIOR 27). The listing — names,
    /// lengths and modification times — is taken before and after; if it moved, the whole snapshot
    /// is taken again, and if it moved twice the answer is <c>Unreadable("changing")</c>. That is
    /// what closes "a marker moved into an already-scanned file while a rotation was running"
    /// without putting a lock on readers: <c>tools/probe</c> runs against a live bridge and must
    /// never be able to block it.
    /// </summary>
    SidecarSnapshot ReadSidecarSet()
    {
        if (_path is null || ErrorLogPath is not { } log) return SidecarSnapshot.Nothing;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(log);
            if (string.IsNullOrEmpty(dir)) return SidecarSnapshot.Nothing;

            var sidecarGlob = ErrorLogName + "*";
            var candidateGlob = System.IO.Path.GetFileName(_path) + ".tmp*";

            for (var attempt = 1; ; attempt++)
            {
                var before = Listing(dir, sidecarGlob);
                var beforeTemps = Listing(dir, candidateGlob);

                var lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var (path, _, _) in before) lines[path] = _readSidecar(path);

                // AND THE CANDIDATES' CONTENTS, INSIDE THE SAME WINDOW.
                //
                // They used to be ENUMERATED here and READ later, in ScanCandidates. So the change
                // detection watched the names and the adoption decision rested on bytes it had never
                // covered — and adoption is the one decision in this class that changes what the
                // machine believes it submitted. A rewrite its owner finished between the listing
                // and the read was adopted on content nobody established was stable. Read here, a
                // temp that moves makes the second listing disagree with the first, and the whole
                // snapshot is taken again exactly as a moving sidecar does.
                //
                // ReadTolerantly does not throw: absence and denial are its return value, so a
                // candidate that cannot be read stays the per-candidate fact it was — it does not
                // become a refusal of the whole set. Only CHANGE escalates to Unreadable.
                var temps = new Dictionary<string, CandidateRead>(StringComparer.Ordinal);
                foreach (var (path, _, _) in beforeTemps)
                {
                    var text = ReadTolerantly(path, out var failed);
                    temps[path] = new CandidateRead(text, failed);
                }

                var after = Listing(dir, sidecarGlob);
                var afterTemps = Listing(dir, candidateGlob);

                if (Same(before, after) && Same(beforeTemps, afterTemps))
                    return new SidecarSnapshot(before.Select(e => e.Path).ToArray(),
                                               beforeTemps.Select(e => e.Path).ToArray(), lines, temps);
                if (attempt == 2) return SidecarSnapshot.Unreadable("the set is changing under this reader");
            }
        }
        catch (Exception e) { return SidecarSnapshot.Unreadable(e.GetType().Name); }
    }

    /// <summary>
    /// Every entry the sidecar glob matches, with the two facts that say whether it moved. Sorted so
    /// two listings of an unchanged directory compare equal whatever order the filesystem gave them.
    ///
    /// ENTRIES AND NOT FILES. <c>Directory.GetFiles</c> does not return a DIRECTORY sitting at a
    /// sidecar's name, so such a name would never be read at all and would answer "absent" — which
    /// is the round-9 finding one turn further out. A directory here is listed, then read, and the
    /// read fails, which is the correct answer.
    ///
    /// Caller is inside <see cref="ReadSidecarSet"/>'s try, which is the only place this is called.
    /// </summary>
    List<(string Path, long Length, DateTime Modified)> Listing(string dir, string glob)
    {
        var names = _listSidecars(dir, glob);
        var listing = new List<(string, long, DateTime)>(names.Length);
        foreach (var name in names)
        {
            var info = new FileInfo(name);
            listing.Add((name, info.Exists ? info.Length : -1, info.LastWriteTimeUtc));
        }
        listing.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return listing;
    }

    static bool Same(List<(string Path, long Length, DateTime Modified)> before,
                     List<(string Path, long Length, DateTime Modified)> after)
    {
        if (before.Count != after.Count) return false;
        for (var i = 0; i < before.Count; i++)
            if (!string.Equals(before[i].Path, after[i].Path, StringComparison.Ordinal)
                || before[i].Length != after[i].Length
                || before[i].Modified != after[i].Modified) return false;
        return true;
    }

    /// <summary>
    /// The snapshot every reading comes out of, taken once and held until something writes. Caller
    /// holds <see cref="_gate"/>.
    /// </summary>
    SidecarSnapshot Snapshot()
    {
        if (_snapshot is not null) return _snapshot;
        _snapshot = ReadSidecarSet();
        Derive(_snapshot);
        return _snapshot;
    }

    /// <summary>
    /// A WRITE MAKES THE SNAPSHOT STALE, so the next reading takes a fresh one. Called from the one
    /// place that appends to a sidecar and from <see cref="Rotate"/>.
    /// </summary>
    void Invalidate() => _snapshot = null;

    /// <summary>
    /// EVERYTHING THE FILES SAY, COMPUTED ONCE FROM ONE SNAPSHOT — the only writer of the five
    /// <c>_disk…</c> fields. Every public member runs <see cref="Ready"/> first, so no reading can
    /// depend on another having run: that is R9-2, and it is a property of this method being the
    /// only one that derives.
    ///
    /// Unreadable is not a middle state. It is the fail-closed end of both scales at once: written
    /// down (so the zero is flagged) AND a gap this run cannot rule out (so the machine is degraded
    /// and <c>SupportsClientOrderId</c> drops), with its own flag so the report says which of the
    /// two problems it is rather than naming a refusal nobody observed.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void Derive(SidecarSnapshot snapshot)
    {
        _snapshotRefusal = snapshot.Refusal;
        _diskNotes = WitnessNotes.None;

        if (snapshot.Refusal is not null)
        {
            _sidecars = [];
            _sidecarText = new SidecarText([], snapshot.Refusal);
            _diskNoted = true;
            _diskDegraded = true;
            _diskGapClosed = false;
            _diskNotes = WitnessNotes.UnreadableSidecar;
            return;
        }

        _sidecars = snapshot.Sidecars.ToArray();
        _sidecarText = new SidecarText(
            _sidecars.Select(p => new SidecarFile(p, snapshot.Lines(p))).ToArray(), null);
        _diskNoted = _sidecars.Any(snapshot.HasNotes);

        if (ErrorLogPath is not { } canonical)
        {
            _diskDegraded = false;
            _diskGapClosed = false;
            return;
        }

        // A REFUSED WRITER IS THE ONE CAUSE THAT IS VISIBLE IN THE NAMES. Its sidecar is
        // `<canonical>-<pid>-<session>` and its own generations hang off that, so anything in the
        // set that is not one of the canonical generations is somebody the lease turned away. The
        // other two causes are discovered by the candidate scan and by the recovery.
        var family = new HashSet<string>(Generations(canonical), StringComparer.Ordinal);
        if (_sidecars.Any(f => !family.Contains(f) && !IsRotationTemp(canonical, f)))
            _diskNotes |= WitnessNotes.RefusedWriter;

        // THE DEGRADED STATE IS THE CANONICAL FILE'S QUESTION, and that is not an oversight being
        // preserved. A second bridge turned away cost no order — the refusal is what stops the order
        // being sent — so its lines must not mark this machine degraded for ever, which would drop
        // SupportsClientOrderId over somebody else's misconfiguration. It must only stop a zero
        // being read as a fact about what was submitted. The F25 boundary, kept: what crosses it is
        // UNREADABILITY, which is this run's own problem whoever the file belonged to.
        var deciding = DecidingIn(snapshot, Generations(canonical));
        _diskDegraded = deciding.IsUnresolved;
        _diskGapClosed = deciding.Says(ResolvedMarker);
    }

    /// <summary>
    /// THE LINE THAT DECIDES THE STATE: a safety event, or the marker that closes one. Warnings are
    /// skipped, because they say nothing about whether a durability gap is open — that is the whole
    /// of the class fix (see <see cref="SafetyPrefix"/>).
    /// </summary>
    static bool Deciding(string line) =>
        line.StartsWith(SafetyPrefix, StringComparison.Ordinal)
        || string.Equals(line, ResolvedMarker, StringComparison.Ordinal);

    /// <summary>
    /// THE SIDECAR IS A SET OF GENERATIONS, NOT A FILE, and the state is read off the set.
    ///
    /// <see cref="AppendToErrorLog"/> bounds the file by rotating it, and the line that tips it over
    /// can be a quarantine WARNING from a session that commits nothing — so reading only the current
    /// log would leave every safety event in an older generation and report an open durability gap
    /// as perfect health.
    ///
    /// NEWEST FIRST, AND BY NAME RATHER THAN BY TIMESTAMP. The last deciding line wins wherever it
    /// is, so a gap closed before a rotation stays closed and one left open stays open. The two
    /// in-flight names can only ever hold a RESTATEMENT of a line that is also in an older
    /// generation, so ordering them after the current log is what makes a rotation that is followed
    /// by a clean commit read as resolved: the newest word is always the current log's.
    /// </summary>
    /// <summary>
    /// The name act 1 builds the next current log under. In the canonical file's own family — it is
    /// this writer's, not a refused writer's — but NOT one of the five generations: see
    /// <see cref="Rotate"/> for why nothing decides from it.
    /// </summary>
    static bool IsRotationTemp(string log, string file) =>
        file.StartsWith(log + PendingSuffix + "-", StringComparison.Ordinal);

    static IEnumerable<string> Generations(string log)
    {
        yield return log;
        yield return log + PendingSuffix;
        yield return log + StagingSuffix;
        yield return log + RolledSuffix;
        yield return log + SecondSuffix;
    }

    /// <summary>
    /// EVERY SIDECAR BESIDE THE WITNESS, READ ONCE, HELD IN MEMORY — or the reason there is no
    /// reading. It is a VALUE: a consumer that wants to know whether anything is written down has to
    /// take an answer that may be "I could not look", and cannot get "there is nothing there"
    /// instead, because it has no way to ask.
    /// </summary>
    /// <summary>
    /// WHAT A CANDIDATE HELD WHEN IT WAS READ, in the same three shapes <see cref="ReadTolerantly"/>
    /// answers in: text, absent (<c>null</c> with <see cref="Unreadable"/> false), or a read this
    /// build could not perform. Carried in the snapshot so an adoption decision rests on bytes the
    /// change detection covered rather than on a second look nobody watched.
    /// </summary>
    readonly record struct CandidateRead(string? Text, bool Unreadable);

    sealed class SidecarSnapshot
    {
        readonly Dictionary<string, string[]> _lines;
        readonly Dictionary<string, CandidateRead> _candidates;

        SidecarSnapshot(string refusal)
        {
            Refusal = refusal;
            _lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
            _candidates = new Dictionary<string, CandidateRead>(StringComparer.Ordinal);
            Sidecars = [];
            Candidates = [];
        }

        internal SidecarSnapshot(IReadOnlyList<string> sidecars, IReadOnlyList<string> candidates,
                                 Dictionary<string, string[]> lines,
                                 Dictionary<string, CandidateRead> candidateText)
        {
            Sidecars = sidecars;
            Candidates = candidates;
            _lines = lines;
            _candidates = candidateText;
        }

        /// <summary>
        /// What this candidate held when it was read. A name the snapshot does not carry was never
        /// listed, which is absence — the shape <see cref="ReadTolerantly"/> gives a file that is
        /// not there.
        /// </summary>
        public CandidateRead Candidate(string path) =>
            _candidates.TryGetValue(path, out var read) ? read : new CandidateRead(null, false);

        /// <summary>Why there is no snapshot, or null when this one is complete.</summary>
        public string? Refusal { get; }

        /// <summary>Every name the sidecar glob matched, in ordinal order. Empty when refused.</summary>
        public IReadOnlyList<string> Sidecars { get; }

        /// <summary>Every name the uncommitted-rewrite glob matched. Empty when refused.</summary>
        public IReadOnlyList<string> Candidates { get; }

        /// <summary>A witness with no home: nothing to read, and nothing wrong with that.</summary>
        public static readonly SidecarSnapshot Nothing =
            new([], [], new Dictionary<string, string[]>(StringComparer.Ordinal),
                new Dictionary<string, CandidateRead>(StringComparer.Ordinal));

        public static SidecarSnapshot Unreadable(string reason) => new(reason);

        /// <summary>Whether a name held anything. A name not in the snapshot held nothing.</summary>
        public bool HasNotes(string path) =>
            _lines.TryGetValue(path, out var lines) && lines.Any(l => !string.IsNullOrWhiteSpace(l));

        /// <summary>
        /// What this file held when it was read. Empty for a name the snapshot does not carry, which
        /// is a name that was never listed — see <see cref="Refusal"/> for the other zero.
        /// </summary>
        public IReadOnlyList<string> Lines(string path) =>
            _lines.TryGetValue(path, out var lines) ? lines : [];

        /// <summary>
        /// What this file weighs IN BYTES. Zero for a name that is not there.
        ///
        /// It is the seed for the writer's own byte count, and it is compared against a byte cap, so
        /// it is measured the way the file is written: UTF-8. Summing <c>string.Length</c> counted
        /// UTF-16 code units, which is the same number only for ASCII — and the lines that land here
        /// are exception messages and filesystem paths, which on most machines are not.
        /// </summary>
        public long Length(string path) =>
            _lines.TryGetValue(path, out var lines)
                ? lines.Sum(l => (long)System.Text.Encoding.UTF8.GetByteCount(l) + Environment.NewLine.Length)
                : 0;

        /// <summary>
        /// The last line matching <paramref name="want"/> across <paramref name="generations"/> in
        /// the order given, timestamp stripped. No IO and no catch: there is nothing left to fail.
        /// </summary>
        public string? LastLineWhere(IEnumerable<string> generations, Func<string, bool> want)
        {
            foreach (var log in generations)
            {
                if (!_lines.TryGetValue(log, out var lines)) continue;
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var space = lines[i].IndexOf(' ');
                    var text = space < 0 ? lines[i] : lines[i][(space + 1)..];
                    if (want(text)) return text;
                }
            }
            return null;
        }
    }

    /// <summary>Renames a rejected candidate out of the <c>.tmp*</c> glob. Null when it was left.</summary>
    string? Quarantine(string candidate)
    {
        try
        {
            if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(candidate) < TimeSpan.FromSeconds(QuarantineGraceSeconds))
                return null;
            for (var n = 1; n <= 64; n++)
            {
                var target = $"{_path}.rejected-{n}";
                if (File.Exists(target)) continue;
                File.Move(candidate, target);
                return target;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>
    /// A NON-CRYPTOGRAPHIC FINGERPRINT, AND THAT IS THE RIGHT CHOICE HERE — FNV-1a, 64 bit, over the
    /// UTF-8 bytes.
    ///
    /// What it has to do is tell "this rewrite was derived from that exact committed content" from
    /// "this file merely looks like a rewrite". Accidental collision is around one in 1.8e19, which
    /// is far below every other risk on this path.
    ///
    /// What it deliberately does NOT do is resist a forger, and nothing is lost by that: anyone who
    /// can write <c>coid-witness.json.tmp</c> in this directory can write <c>coid-witness.json</c>
    /// itself, so a cryptographic digest would move no boundary — it would only be recomputable by
    /// the same attacker. Against that, this file promises to use System.Text.Json and System.IO and
    /// nothing else, because it is deployed into ATAS's Strategies folder by filename prefix (trap
    /// 34) and a dependency that is silently not copied fails inside ATAS with no message anywhere.
    /// Eight lines of arithmetic keeps that promise literally.
    ///
    /// PUBLIC BECAUSE IT IS PART OF THE FILE FORMAT. The value is written into every rewrite as
    /// <see cref="Envelope.Predecessor"/>, so anything that reads or writes one of these files needs
    /// it — and it is testable in its own right, which matters more than it looks: the whole
    /// discrimination comes from the multiply, and a build with the prime wrong collapses this to an
    /// XOR fold into the low byte while every lineage test in the suite goes on passing.
    /// </summary>
    public static string Fingerprint(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }

    /// <summary>
    /// A read that survives the file being REPLACED under it.
    ///
    /// <see cref="Save"/> replaces the whole file with <see cref="DefaultReplace"/>, and a second
    /// bridge instance — or a probe — can be doing that at the moment this opens it. The
    /// share flags admit a concurrent writer and a concurrent delete, and the retry covers the
    /// instant during a replace when the name resolves to neither the old file nor the new one. A
    /// missing file is NOT that case and returns immediately: it means nothing has ever been
    /// written, which is a clean answer rather than a failed read.
    /// </summary>
    string? ReadTolerantly(string path, out bool failed)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = _open(path);
                using var reader = new StreamReader(stream);
                failed = false;
                return reader.ReadToEnd();
            }
            // ABSENCE IS THIS ONE EXCEPTION AND NO OTHER. The file has never been written here, which
            // is the only outcome a write may proceed against.
            catch (FileNotFoundException) { failed = false; return null; }

            // AND A MISSING DIRECTORY IS THE OPPOSITE KIND OF FACT, however similar the exception
            // looks. It says the folder this machine's whole history lives in is gone — unmounted,
            // removed, renamed by a cleanup — not that nothing was ever written. Classified as
            // absence it invites the build to write a fresh file over a history it could not see,
            // and it told an operator the witness "changed underneath this writer", which sends them
            // hunting a second bridge instead of the folder that is missing. The ratified rule for
            // this case is already on the record: a machine with no bridge directory refuses every
            // order rather than trade without a write-ahead record.
            catch (DirectoryNotFoundException) { failed = true; return null; }
            catch (Exception) when (attempt < 3) { Thread.Sleep(20); }
            catch (Exception) { failed = true; return null; }
        }
    }

    /// <summary>
    /// ATOMIC WHOLE-FILE REPLACE, AND NEVER DELETE-THEN-MOVE.
    ///
    /// A delete followed by a move has a window in which the name resolves to nothing at all, and
    /// anything reading in that window sees "no record was ever written" — which for this file is
    /// the same as "this product never submitted that identifier". Writing a temporary file and
    /// replacing the real one in a single operation means a reader sees the old file or the new
    /// one, and no third thing.
    ///
    /// AND WHEN THE REPLACE IS REFUSED ANYWAY. It is retried — see <see cref="ReplaceAttempts"/> for
    /// the numbers and for the fact that they are a judgment — and if it still will not land, the
    /// write is NOT quietly dropped. Three things happen instead, and each of them closes a
    /// different half of the hole:
    ///
    ///   1. THE IN-MEMORY STATE IS KEPT. The claim stays in <see cref="_records"/>, so the next
    ///      successful save carries it and the durable file catches up on its own.
    ///   2. THE TEMP IS LEFT WHERE IT IS, holding the newer state, and
    ///      <see cref="UncommittedRewrite"/> makes a later session read it. That is what stops a
    ///      failure at the END of a session from losing the claim outright, which is the case the
    ///      first point cannot help with — there is no next save.
    ///   3. IT IS WRITTEN DOWN. <see cref="LastWriteFailure"/> and the sidecar
    ///      <see cref="ErrorLogName"/>, naming the file, the temp and the newest claim at risk.
    ///
    /// RETURNS whether the records reached <see cref="_path"/>. <see cref="Submitting"/> hands that
    /// answer to its caller, which can still refuse to place the order.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    bool Save(string claim)
    {
        if (_path is null) return false;

        // COMPARE AND SWAP, AND A MISS IS A REFUSAL. This writer's rewrite says it descends from a
        // particular committed content. It holds the lock, so nothing that plays by the rules can
        // have changed the file — if it changed anyway, something is writing this witness that is
        // not this build: an older bridge, a hand edit, a restored backup. There is no safe merge
        // with a party whose semantics are unknown, and this product does not support two writers
        // (trap 35: a second bridge is a misconfiguration). Refuse, and let Place refuse the order.
        // AND THE COMPARE-AND-SWAP READ HAS THE SAME RULE. It used to discard the failure flag, so
        // a refused read here produced null, null matched a null _committedHash, and the swap went
        // ahead against a file this instance had never managed to see. A read that failed says
        // nothing about what is on disk, and "nothing" is not a lineage.
        var current = ReadTolerantly(_path, out var unreadable);
        if (unreadable) { NotOurs(UnreadableDetail()); return false; }

        var currentHash = current is null ? null : Fingerprint(current);
        if (!string.Equals(currentHash, _committedHash, StringComparison.Ordinal))
        {
            NotOurs($"the witness file changed underneath this writer, so something else is writing " +
                    $"it. file={_path} claim={claim}");
            return false;
        }

        return Attempt(claim);
    }

    /// <summary>
    /// THE REFUSAL NAMES THE CLAIM IT REFUSED. Without it every line a refused writer left said only
    /// that somebody else owned the witness — true, and useless to anyone asking which order went
    /// unrecorded. `ReportWriteFailure` has always named the claim; this is the same obligation on
    /// the other refusal path.
    /// </summary>
    string NotOursDetail(string claim) =>
        $"claim={(string.IsNullOrEmpty(claim) ? "<none>" : claim)} " +
        (_notOwned ?? "this witness is not ours to write");

    /// <summary>The one sentence for "there is something at the path and it is not an envelope".</summary>
    string UnreadableDetail() =>
        $"the write-ahead record at {_path} could not be read, so this run cannot say what it has " +
        $"submitted and must not write over it; if the file is locked wait, if it is damaged repair " +
        $"or remove it";

    /// <summary>The one refusal shape for "this witness is not ours to write". Caller holds the gate.</summary>
    void NotOurs(string detail)
    {
        _writeFailed = true;
        LastWriteFailure = "ERROR " + detail;
        Note(LastWriteFailure, safety: true);
    }

    /// <summary>
    /// One rewrite, from the lineage this instance currently believes. Caller holds
    /// <see cref="_gate"/> and the file lock.
    /// </summary>
    bool Attempt(string claim)
    {
        if (_path is not { } destination) return false;
        var tmp = _tempPrefix + (++_tempSeq);

        // THE LINEAGE GOES IN THE FILE, not in the timestamps. It names the generation after the
        // committed one and the fingerprint of the committed content it was derived from, which is
        // what lets a later reader tell this rewrite from any other file lying beside the witness.
        var envelope = new Envelope
        {
            Generation = _generation + 1,
            Predecessor = _committedHash,
            Records = _records
        };
        var text = JsonSerializer.Serialize(envelope, Opts);

        // Not retried: a temp that cannot be written at all is a directory problem, not contention,
        // and the retry budget belongs to the replace. It is reported on the same path as a refused
        // replace, because a claim that never reached even the temp is just as lost.
        // NOT "the temp holds the newer state" on this branch, because it does not: the write is
        // what failed. Saying otherwise sends whoever reads the sidecar to a file that is absent or
        // half-written, looking for a claim that is not in it.
        try { File.WriteAllText(tmp, text); }
        catch (Exception e) { ReportWriteFailure(e, tmp, claim, tempHoldsTheClaim: false); return false; }

        // THE NEW REWRITE SUPERSEDES THIS INSTANCE'S EARLIER FAILED ONE, and the ordering here is
        // the whole of the safety: the new temp is already on disk before the old one is removed, so
        // there is never an instant with no temp holding the claim. Two temps of the same lineage
        // from one writer would also be genuinely ambiguous to the recovery scan — they are written
        // milliseconds apart and mtime cannot order them — and letting them pile up is how a
        // trimmed identifier finds its way back onto disk.
        SweepStranded();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _replace(tmp, destination);
                return Committed(text, claim);
            }
            catch (Exception e) when (Transient(e) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceBackoffMs * attempt);
            }
            catch (Exception e) { _stranded.Add(tmp); ReportWriteFailure(e, tmp, claim, tempHoldsTheClaim: true); return false; }
        }
    }

    /// <summary>
    /// ONE OWNER PER WITNESS, AND THE LEASE IS HOW THAT IS DECIDED — FOR A LIFETIME, NOT A CALL.
    ///
    /// Round 3 treated this as a contention reducer that a writer could proceed without, on the
    /// argument that the compare-and-swap made the result correct anyway. That was wrong in the
    /// direction that matters. Two writers were never a supported configuration — trap 35 calls a
    /// second bridge a misconfiguration — and every interleaving that a lock-optional design has to
    /// survive is an interleaving of a scenario the product does not support. Hardening a path
    /// nobody is meant to take costs correctness arguments forever; refusing it costs one branch.
    ///
    /// So: no lease, no write. A writer that cannot take it inside the budget reports the reason
    /// through <see cref="Trouble"/> and <see cref="Submitting"/> returns false, which makes
    /// <c>Place</c> refuse the order — the same refusal as any other unwritable witness, and the
    /// safe direction. A read-only directory or a denied ACL lands here too, which is correct: a
    /// witness that cannot take its own lock cannot be written either.
    ///
    /// AND IT IS HELD, NOT RETAKEN. Taking and releasing it per call left two live instances taking
    /// turns: A writes, releases, B writes, releases, and each is perfectly polite while the file
    /// has two authors. The exclusion has to last as long as the writer does, so the handle is taken
    /// at the first write and held until <see cref="Dispose"/> or process death — the OS releases it
    /// either way, so a crashed bridge strands nothing and needs no timeout to recover from.
    ///
    /// READERS NEVER COME HERE. Ownership is a property of writing; a reader answers from the
    /// committed file and its own memory. See <see cref="EnsureRecovered"/>.
    /// </summary>
    bool Lease()
    {
        if (_path is null) return false;
        if (_lease is not null) return true;          // already the owner, for the life of this instance

        var lockPath = _path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _notOwned = null;
                return true;
            }
            catch (Exception e)
            {
                if (attempt >= LockAttempts)
                {
                    _notOwned = $"another writer owns this witness ({lockPath}): {e.GetType().Name}";
                    return false;
                }
                Thread.Sleep(LockBackoffMs);
            }
        }
    }

    /// <summary>
    /// Releases the lease. The instance stays usable — a later write takes the lease again — because
    /// that is exactly the ATAS strategy stop/start cycle inside one process: the adapter is taken
    /// down, must not go on owning the witness, and may be started again against the same object.
    ///
    /// Not the only release. The OS closes the handle when the process dies, which is what makes a
    /// crashed bridge harmless: there is no lease file to clean up and no stale owner to time out.
    /// </summary>
    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                _lease?.Dispose();
                _lease = null;
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// AFTER THE RENAME LANDED: IS WHAT IS ON DISK OURS? The rename returning success says this
    /// process replaced the file; it does NOT say the file still holds this process's content a
    /// moment later, and with a second bridge running it may not. <see cref="Submitting"/> promises
    /// its caller that THIS claim is durable, so the promise is checked rather than assumed.
    ///
    /// Three outcomes, and the middle one is the point:
    ///
    ///   * OURS. The lineage moves forward, this instance's stranded temps are swept, true.
    ///   * SOMEBODY ELSE'S. Our claim is not on disk. This does NOT re-sync the lineage — round 3
    ///     did, and round 4 removed it with the rebase. Nothing that plays by the rules can have
    ///     written this file while we held the lease, so the party that did is one whose semantics
    ///     this build does not know, and adopting its content as our parent would be negotiating
    ///     with it. `_committedHash` is left as it was, which means every later save by this
    ///     instance misses the compare-and-swap too and the whole run is refused rather than one
    ///     order — measured at 80/80 refusals per rival in the round-4 three-process race. That is
    ///     the fail-closed direction and it is deliberate.
    ///   * UNREADABLE. Not evidence of an overwriter. The rename is the durability event and it
    ///     succeeded, so this reports true and records that the confirmation could not be taken.
    ///     Refusing an order because a re-read hiccuped would be inventing a failure.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    bool Committed(string text, string claim)
    {
        var actual = _path is null ? null : ReadTolerantly(_path, out _);

        // ABSENT OR UNREADABLE IS NOT DURABLE. The rename returning success is not evidence that a
        // record exists to be found later, and rule 1 is a question about evidence: if this cannot
        // read back what it just wrote, it does not know the claim is there, and the honest answer
        // to "is the write-ahead record durable" is no. The caller refuses the order.
        //
        // AND NOTHING BUT OUR OWN BYTES COUNTS. Round 3 accepted an overtaker's file when it
        // happened to contain a matching record, which meant trusting a writer whose semantics this
        // build knows nothing about to have carried the claim faithfully. With one owner per witness
        // there is no legitimate overtaker: anything other than exactly what was just written means
        // something else is writing this file, and that is a refusal, not a negotiation.
        if (actual is null || !string.Equals(Fingerprint(actual), Fingerprint(text), StringComparison.Ordinal))
        {
            NotOurs(actual is null
                ? $"the rewrite landed but the file could not be read back, so the claim is not " +
                  $"known to be durable. file={_path} claim={claim}"
                : $"the rewrite landed and the file already holds something else, so something is " +
                  $"writing this witness that is not this build. file={_path} claim={claim}");
            return false;
        }

        _generation++;
        _committedHash = Fingerprint(text);
        Settled();
        return true;
    }

    /// <summary>
    /// A CLEAN COMMIT, AND WHAT IT PUTS RIGHT.
    ///
    /// <see cref="LastWriteFailure"/> and <see cref="_writeFailed"/> used to be permanent for the
    /// life of the process. That is wrong once anything reads them: a contended replace that
    /// succeeded on the next order left the ATAS bridge health row saying "orders are being refused"
    /// while every order was going through, and a row that cries wolf is a row nobody reads the day
    /// it is right. A failure that has been superseded by a commit carrying the same records is a
    /// failure that no longer describes anything.
    ///
    /// What does NOT clear here is the sidecar: the history stays on disk. Whether it still counts
    /// as a live problem is <see cref="_degraded"/>'s question, resolved separately.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void Settled()
    {
        _writeFailed = false;
        LastWriteFailure = null;
        SweepStranded();

        // The rewrite this session recovered is now committed, records and all, so the file it came
        // from is a duplicate rather than a safety net. Left in place it is re-examined and
        // re-rejected by every later session — the permanent-degradation loop again, one step
        // further along.
        if (_adopted is not null)
        {
            try { File.Delete(_adopted); } catch (Exception) { }
            _adopted = null;
        }

        // AND THE GAP IS MARKED RESOLVED RATHER THAN ERASED. _degraded asks whether there is an
        // UNRESOLVED failure, not whether anything ever went wrong: a witness that has since
        // committed cleanly is working, and reporting it degraded forever would make the state
        // useless the moment it mattered. The history stays in the file; the last line says the
        // problem ended.
        if (Degraded)
        {
            _degraded = false;
            // RE-READ RATHER THAN TRUST THE FLAG. _degraded was decided when this instance loaded,
            // and the file has been open to anything since. Appending a second RESOLVED under a
            // first says a gap was closed twice; worse, appending one at all when the tail already
            // says RESOLVED means this instance is reporting on a gap that was not its own.
            //
            // AND IT IS WRITTEN AS A SAFETY EVENT, because it is the line that ENDS one. Written as
            // a warning it was rationed by the same 32-line quota as the quarantine notes, so a
            // session that had tidied 32 leftovers could not say the gap was closed: the flag was
            // cleared in memory while the file's last word was still an open gap, and the next
            // process read DEGRADED over a witness that had just committed cleanly. It is a state
            // transition, at most once per session, guarded against duplication by the re-read
            // above — it cannot flood the file the quota exists to bound.
            //
            // AND THE READING THAT FOLLOWS COMES BACK OFF THE FILES. It used to latch a closed gap
            // in memory here, which said the gap was closed even when the append that closes it had
            // silently failed — the one direction this file must never fail in. The append
            // invalidates the snapshot, so the next reading re-derives from what is on disk:
            // RESOLVED if the line landed, still degraded if it did not.
            //
            // AND "I COULD NOT READ IT" IS NOT "THERE IS NO MARKER YET". That is the third state,
            // and losing it cost the one thing this file exists to protect: the re-read answered
            // null for a set nobody could look in — the same answer a set with no marker gives — so
            // this appended RESOLVED over it. The marker is durable and it outranks every line under
            // it, so the next run that CAN read the file is told a gap was closed that this run
            // never saw. Nothing is written, the session's own latch is left standing, and the
            // machine goes on reading degraded until somebody can read the set again.
            var deciding = LastDecidingLine();
            if (!deciding.CouldNotRead)
            {
                _degraded = false;
                if (!deciding.Says(ResolvedMarker))
                    AppendToErrorLog($"{DateTimeOffset.UtcNow:O} {ResolvedMarker}", safety: true);
            }
        }
    }

    /// <summary>
    /// THE ONE PLACE THE DECIDING LINE IS ASKED FOR, so no caller can invent a fourth answer.
    /// <see cref="Derive"/>, <see cref="Rotate"/> and <see cref="Settled"/> all come through here.
    /// </summary>
    static DecidingLine DecidingIn(SidecarSnapshot snapshot, IEnumerable<string> generations) =>
        snapshot.Refusal is not null
            ? DecidingLine.Unread
            : snapshot.LastLineWhere(generations, Deciding) is { } line
                ? DecidingLine.Found(line)
                : DecidingLine.None;

    /// <summary>
    /// THE DECIDING LINE IS A TRI-STATE — a line, no line, or no reading — and the third state is
    /// the one that kept being lost.
    ///
    /// The first two are facts about the sidecar. The third is a fact about THIS RUN, and it is not
    /// representable as null, because null already means the second. Collapsing them is what let a
    /// clean commit append the RESOLVED marker over a set nobody had managed to read: a durable
    /// claim that a durability gap was closed, made by a run that could not see whether one was
    /// open. Every caller has to take the third state; none of them can be handed it as an absence.
    /// </summary>
    readonly record struct DecidingLine
    {
        DecidingLine(string? line, bool couldNotRead)
        {
            Line = line;
            CouldNotRead = couldNotRead;
        }

        /// <summary>
        /// The line, timestamp stripped, or null when there is none — and also null when there was
        /// no reading, which is why <see cref="CouldNotRead"/> is asked first and not second.
        /// </summary>
        public string? Line { get; }

        /// <summary>The set could not be read, so there is NO ANSWER. Not "there is no line".</summary>
        public bool CouldNotRead { get; }

        /// <summary>The set was read and holds nothing that decides anything.</summary>
        public static readonly DecidingLine None = new(null, false);

        /// <summary>There was no reading of the set at all.</summary>
        public static readonly DecidingLine Unread = new(null, true);

        public static DecidingLine Found(string line) => new(line, false);

        /// <summary>True only when the set WAS read and its last deciding line is this one.</summary>
        public bool Says(string marker) =>
            !CouldNotRead && string.Equals(Line, marker, StringComparison.Ordinal);

        /// <summary>True only when the set WAS read and its last deciding line leaves a gap open.</summary>
        public bool IsUnresolved => !CouldNotRead && Line is not null && !Says(ResolvedMarker);
    }

    /// <summary>
    /// The canonical machine's last deciding line, out of the snapshot — a fresh one whenever
    /// something has written since the last was taken. Caller holds <see cref="_gate"/>.
    ///
    /// A witness with nowhere to live answers <see cref="DecidingLine.None"/> and not
    /// <see cref="DecidingLine.Unread"/>: there is no set, so there was no read to fail. What that
    /// answer reaches is <see cref="AppendToErrorLog"/>, which has no file to write to either.
    /// </summary>
    DecidingLine LastDecidingLine() =>
        ErrorLogPath is { } log ? DecidingIn(Snapshot(), Generations(log)) : DecidingLine.None;

    /// <summary>
    /// Deletes this instance's uncommitted leftovers. Called after the superseding rewrite is
    /// already on disk, never before. See <see cref="_stranded"/>.
    /// </summary>
    void SweepStranded()
    {
        foreach (var path in _stranded)
            try { File.Delete(path); } catch (Exception) { }
        _stranded.Clear();
    }

    /// <summary>
    /// The two ways Windows refuses to replace an open file, and both of them pass.
    ///
    /// A sharing violation — the destination is open without <c>FileShare.Delete</c>, which is a
    /// scanner, the indexer or another reader — arrives as <see cref="IOException"/>. A handle the
    /// process may not displace, or a read-only attribute set by a backup tool, arrives as
    /// <see cref="UnauthorizedAccessException"/> instead, and that one used not to be retried at
    /// all: the first refusal ended the write. Both are transient and both are retried.
    ///
    /// A VANISHED TEMP IS NOT CONTENTION, and both of its exceptions derive from
    /// <see cref="IOException"/>, so they are excluded by name. Waiting 200 ms in 20 ms steps for a
    /// file that has been deleted is 200 ms of an order's life spent on a certainty.
    /// </summary>
    static bool Transient(Exception e) =>
        e is IOException or UnauthorizedAccessException
        && e is not FileNotFoundException and not DirectoryNotFoundException;

    /// <summary>
    /// The engineering event. Caller holds <see cref="_gate"/>.
    ///
    /// <see cref="_writeFailed"/> is set and never cleared: a session that once failed to make a
    /// claim durable has had a gap, and reporting <c>io:ok</c> again after the next success would
    /// hide it.
    /// </summary>
    void ReportWriteFailure(Exception e, string tmp, string claim, bool tempHoldsTheClaim)
    {
        _writeFailed = true;
        // THE CLAIM AT RISK IS THE ONE BEING WRITTEN, not the newest record on the list. For
        // Submitting those are the same; for Identified they are not — it updates a record wherever
        // it sits — so reading the last entry named an unrelated identifier and sent whoever was
        // holding the sidecar looking for the wrong order.
        // THE CLAIM COMES FIRST, AND THAT IS NOT COSMETIC. OneLine clips the whole event at
        // MaxNoteChars, and two absolute paths — the witness and a per-writer temp name — can spend
        // 350 characters between them before anything else is said. So the field the file exists to
        // record, the identifier that may be live at the broker, was the field the clip removed.
        var line = $"ERROR coid-witness rewrite did not land. " +
                   $"claim={(string.IsNullOrEmpty(claim) ? "<none>" : claim)} " +
                   $"records_in_memory={_records.Count} file={_path} " +
                   (tempHoldsTheClaim ? $"temp_holding_newer_state={tmp} " : $"temp_not_written={tmp} ") +
                   $"{e.GetType().Name}: {e.Message}";
        LastWriteFailure = line;
        Note(line, safety: true);
    }

    /// <summary>
    /// One line into the sidecar, and it is ONE line however hard the input tries. Caller holds
    /// <see cref="_gate"/>.
    /// </summary>
    void Note(string line, bool safety = false)
    {
        _noted = true;
        // ONLY A SAFETY EVENT OPENS A DURABILITY GAP. This used to set _degraded for every line, so
        // a quarantined leftover and a lost claim were the same state downstream — see
        // <see cref="SafetyPrefix"/> for what that cost.
        // The closed-gap reading follows from this one (see GapClosed) rather than being latched
        // beside it, so no path can set one and leave the other disagreeing.
        if (safety) _degraded = true;
        AppendToErrorLog($"{DateTimeOffset.UtcNow:O} {OneLine(line)}", safety);
    }

    /// <summary>
    /// A LINE-ORIENTED FILE GETS ONE LINE. Most of what lands here is an exception message and a
    /// path, and neither is under this product's control: an OS error string can carry a newline, a
    /// filename can carry anything the filesystem allows. A newline in the middle turns one event
    /// into two half-events and lets whatever follows it pose as a fresh, timestamp-free record —
    /// so control characters become spaces and the whole thing is clipped.
    /// </summary>
    static string OneLine(string raw)
    {
        var kept = new char[Math.Min(raw.Length, MaxNoteChars)];
        for (var i = 0; i < kept.Length; i++) kept[i] = char.IsControl(raw[i]) ? ' ' : raw[i];
        return new string(kept) + (raw.Length > MaxNoteChars ? "…" : "");
    }

    /// <summary>
    /// Appends one line to <see cref="ErrorLogName"/> beside the witness. Warnings and markers are
    /// bounded at <see cref="MaxLoggedFailures"/> per session so a permanently unwritable
    /// destination cannot turn every order into a log line; SAFETY events — a write-ahead or
    /// acknowledgement failure — are never rationed. The file is rotated one generation back past
    /// <see cref="MaxErrorLogBytes"/>, which is what keeps that finite.
    ///
    /// Every failure here is discarded. The operation that just failed was a rename onto a file
    /// something else has open, which says nothing about whether an append to a different name
    /// works — but if it does not, the answer is silence, not an exception out of <c>Place</c>.
    /// </summary>
    void AppendToErrorLog(string line, bool safety)
    {
        // A SAFETY EVENT IS NEVER DROPPED, AND THE QUOTA IS NOT A SAFETY MECHANISM. It exists so a
        // permanently unwritable destination cannot turn every order into a log line — a fair worry
        // about NOISE. Applied to failures it silenced the thing the file exists for: the 33rd event
        // in a session might be an Identified that never reached the disk for an order that is live
        // at the broker, and this file is the only cross-process record that the gap happened.
        // Warnings and markers are what get rationed; failures always go in, and the size bound
        // below is what keeps that finite.
        if (!safety)
        {
            if (_loggedFailures >= MaxLoggedFailures) return;
            _loggedFailures++;
        }

        if (SidecarPath is not { } log) return;

        var text = line + Environment.NewLine;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // ROTATED, NOT DELETED, AND DECIDED WITHOUT A PROBE. Deleting was fine while the
                // quota capped what could be lost; with failures unrationed it is not, because the
                // file being thrown away is now the one holding them.
                //
                // PRIOR 31: the trigger used to be `File.Exists` plus an attribute read — two calls
                // that answer "no" for a denial exactly as readily as for an absence, on the one path
                // where a wrong answer costs a rename rather than a sentence. There is exactly one
                // writer per sidecar file, so this writer knows its own file's size: the snapshot
                // supplies the length it started at and every append since is counted. A wrong count
                // costs a rotation that is late, which costs a bigger file and nothing else.
                // A ROTATION THAT STOPPED AT ITS LAST ACT IS FINISHED BEFORE ANYTHING IS WRITTEN.
                // The count is negative exactly when this run has not yet looked at the set — the
                // first append of a session, and every append after a rotation that threw, because
                // Rotate clears it before its first destructive act. So this is every instant at
                // which the four acts may be half done, and Resume runs at all of them.
                if (_sidecarBytes < 0)
                {
                    if (!Resume(log)) return;
                    _sidecarBytes = Snapshot().Length(log);
                }
                // AND THE ROTATION'S OWN RESUME CAN REFUSE TOO. It is the same refusal for the same
                // reason — appending into a current log that is not there writes the line into a file
                // the completion is going to overwrite — and it is checked here rather than trusted
                // to be unreachable.
                if (_sidecarBytes + ByteCount(text) > MaxErrorLogBytes && !Rotate(log)) return;

                // ONE WRITER PER FILE, so this append has nobody to race. See SidecarPath.
                File.AppendAllText(log, text);
                _sidecarBytes += ByteCount(text);
                return;
            }
            catch (Exception) when (attempt < SidecarAttempts) { Thread.Sleep(SidecarBackoffMs * attempt); }
            catch (Exception) { return; /* a witness that cannot write must not become one that throws */ }
            finally
            {
                // THE FILES HAVE MOVED UNDER THE SNAPSHOT, whether or not the append landed: a
                // rotation may have run, and an append that threw may still have written. The next
                // reading takes a fresh one rather than answering out of a stale set.
                Invalidate();
            }
        }
    }

    /// <summary>
    /// OLDEST FIRST, AND WHAT IT COSTS. The list is in submission order, so dropping from the front
    /// drops the claims least likely to be about anything still resting in ATAS.
    ///
    /// A trimmed-away record makes its identifier permanently unprovable — <see cref="PriorSession"/>
    /// answers null and the read-back refuses it. That REFUSES a proof rather than inventing one,
    /// which is the same direction <c>AdapterTouchedOrders.Trim</c> and the adapter's own
    /// <c>_submitted</c> fail in, and the same bound: a very old identifier stops being provable.
    ///
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    void Trim()
    {
        if (_records.Count <= _cap) return;
        _records.RemoveRange(0, _records.Count - _cap);
    }

    void MarkWriteFailed() { try { lock (_gate) _writeFailed = true; } catch (Exception) { } }

    /// <summary>
    /// A version on the file rather than a bare array. The records are read by a build that may be
    /// older or newer than the one that wrote them — the bridge inside ATAS and the probe beside it
    /// are separately deployed — so a format change needs somewhere to announce itself.
    /// </summary>
    sealed class Envelope
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;

        /// <summary>
        /// WHICH REWRITE THIS IS, counted from the file rather than from the process. Loaded from
        /// whatever is committed and incremented by one on every successful replace, so it survives
        /// a restart and is a property of the FILE's history, not of any run's memory.
        /// </summary>
        [JsonPropertyName("generation")] public long Generation { get; set; }

        /// <summary>
        /// THE FINGERPRINT OF THE COMMITTED CONTENT THIS REWRITE WAS DERIVED FROM, or null when it
        /// was derived from no committed file at all. This is the whole of the lineage test — see
        /// <see cref="DescendsFrom"/>.
        /// </summary>
        [JsonPropertyName("predecessor")] public string? Predecessor { get; set; }

        [JsonPropertyName("records")] public List<CoidWitnessRecord> Records { get; set; } = new();
    }
}
