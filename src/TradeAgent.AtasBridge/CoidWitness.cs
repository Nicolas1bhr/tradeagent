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
public sealed class CoidWitness
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

    const int MaxLoggedFailures = 32;
    const int MaxNoteChars = 400;
    const long MaxErrorLogBytes = 64 * 1024;

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

    bool _loaded;
    bool _readFailed;
    bool _candidateUnreadable;

    /// <summary>
    /// THERE IS SOMETHING IN THE SIDECAR — from a previous run of this product or from this one.
    /// Reported through <see cref="Token"/> as <c>io:degraded</c>, because a durability gap that
    /// ended when the process did is exactly the gap nobody would otherwise ever see: the next
    /// session starts with a clean <see cref="LastWriteFailure"/> and a witness that looks perfect.
    /// Cleared by deleting the file, which takes effect at the next start — checked once at load
    /// rather than on every heartbeat, because <see cref="Token"/> runs on the heartbeat and has no
    /// business stat-ing a file five times a minute forever.
    /// </summary>
    bool _degraded;

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

    /// <summary>Why this instance does not own the witness, or null while it does. See <see cref="Own"/>.</summary>
    string? _notOwned;

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
    bool _recovered;
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
                       Action<string, string>? replace = null)
    {
        _path = path;
        _cap = cap < 1 ? 1 : cap;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("n") : sessionId;
        _replace = replace ?? DefaultReplace;
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
                // NO LOCK, NO WRITE. See Own: one owner per witness, and a writer that cannot take
                // the lock refuses the order rather than racing a party whose semantics are unknown.
                using var owned = Own();
                if (owned is null) { NotOurs(_notOwned ?? "this witness is not ours to write"); return false; }

                EnsureLoaded();
                ApplyRecovery();

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
                using var owned = Own();
                if (owned is null) { NotOurs(_notOwned ?? "this witness is not ours to write"); return; }

                EnsureLoaded();
                ApplyRecovery();
                var i = _records.FindIndex(r => string.Equals(r.ClientOrderId, clientOrderId, StringComparison.Ordinal));
                if (i < 0) return;

                var record = _records[i];
                // Not ours to write on. Nothing is recorded, and nothing is reported: this is the
                // ordinary case every time a prior session's order shows up in the book.
                if (!string.Equals(record.SessionId, SessionId, StringComparison.Ordinal)) return;
                if (!string.IsNullOrEmpty(record.BrokerOrderId)) return;

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
                EnsureLoaded();
                EnsureRecovered();
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
                EnsureLoaded();
                EnsureRecovered();
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
            try { lock (_gate) { EnsureLoaded(); return _readFailed; } }
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
                    EnsureLoaded();
                    if (_notOwned is { } contended) return contended;
                    if (LastWriteFailure is { } now) return now;
                    return _degraded
                        ? $"an earlier run could not write the write-ahead record; the account of it " +
                          $"is in {ErrorLogPath}"
                        : null;
                }
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Every record on file, newest last. For the probe and for tests; not a proof path.</summary>
    public IReadOnlyList<CoidWitnessRecord> All()
    {
        try { lock (_gate) { EnsureLoaded(); EnsureRecovered(); return _records.ToArray(); } }
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
                EnsureLoaded();
                EnsureRecovered();
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
                var io = _writeFailed ? "failed" : _degraded ? "degraded" : "ok";
                return $"session:{session},records:{_records.Count},prior:{prior},io:{io}";
            }
        }
        catch (Exception) { return $"session:{session},records:err,prior:err,io:failed"; }
    }

    // ---------------------------------------------------------------- the file

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
        _loaded = true;
        if (_path is null) return;

        // UNRESOLVED, not merely present. Asked before anything below can write one.
        try
        {
            if (ErrorLogPath is { } log && File.Exists(log))
                _degraded = !string.Equals(LastSidecarLine(), ResolvedMarker, StringComparison.Ordinal);
        }
        catch (Exception) { }

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
        ScanCandidates(committedText, committed);

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

        if (committed is not null) Take(committed);
    }

    /// <summary>An envelope, or null when the text is not one. Caller holds <see cref="_gate"/>.</summary>
    static Envelope? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Envelope>(json, Opts); }
        catch (JsonException) { return null; }
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
    void ScanCandidates(string? committedText, Envelope? committed)
    {
        foreach (var candidate in Candidates())
        {
            var text = ReadTolerantly(candidate, out var unreadable);
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
            // commits. See KeepsCommittedRecords for what "no legitimate rewrite loses a record"
            // means once Trim is in the picture.
            if (committed is not null && !KeepsCommittedRecords(envelope, committed))
            {
                _rejected.Add((candidate,
                    $"it does not carry the committed records forward ({envelope.Records.Count} " +
                    $"records against {committed.Records.Count}), so adopting it would drop claims"));
                continue;
            }

            _viable.Add((candidate, envelope));
        }
    }

    /// <summary>
    /// ACTING ON THE SCAN, ONCE, AND ONLY AS THE OWNER. Caller holds <see cref="_gate"/> AND the
    /// file lock — that is what makes it safe to move files and write the sidecar, because the
    /// writer whose rewrite these candidates might be cannot be running at the same time.
    ///
    /// TWO RIVALS MEAN NEITHER IS TRUSTED. Every viable candidate descends from the same commit and
    /// therefore carries the same generation, so nothing in the files distinguishes them. One writer
    /// cannot produce this — it keeps at most one uncommitted rewrite — so it means a copied file or
    /// a writer that is not this build, and guessing is how a claim gets dropped without anybody
    /// being told.
    /// </summary>
    void ApplyRecovery()
    {
        if (_recovered) return;
        _recovered = true;

        foreach (var (path, why) in _rejected)
        {
            var moved = Quarantine(path);
            Note(moved is null
                ? $"ignored {path}: {why}"
                : $"ignored {path}: {why} — moved to {System.IO.Path.GetFileName(moved)}");
        }
        _rejected.Clear();

        if (_viable.Count > 1)
        {
            Note($"WARN coid-witness found {_viable.Count} rival uncommitted rewrites of generation " +
                 $"{_viable[0].Envelope.Generation} and adopted none of them: " +
                 string.Join(", ", _viable.Select(v => v.Path)));
            _viable.Clear();
            return;
        }

        if (_viable.Count == 1)
        {
            _records.Clear();
            Take(_viable[0].Envelope);
            _adopted = _viable[0].Path;
            Note($"coid-witness recovered an uncommitted rewrite (generation {_viable[0].Envelope.Generation}, " +
                 $"{_viable[0].Envelope.Records.Count} records) from {_viable[0].Path}");
            _viable.Clear();
        }
    }

    /// <summary>
    /// Recovery for a caller that does not already hold the lock — the read paths. A process that
    /// cannot take the lock does not own the witness, so it recovers nothing and writes nothing: it
    /// reads the committed file and answers from that. Caller holds <see cref="_gate"/>.
    /// </summary>
    void EnsureRecovered()
    {
        if (_recovered || _path is null) return;
        using var owned = Own();
        if (owned is null) return;
        ApplyRecovery();
    }

    /// <summary>
    /// WHETHER A CANDIDATE CARRIES THE COMMITTED RECORDS FORWARD, and why this is membership rather
    /// than a count.
    ///
    /// A count catches a rewrite that lost records outright and misses the one that swapped them: a
    /// candidate holding three records where the committed file held three, but a DIFFERENT three,
    /// passes a count check and quietly drops a claim. Membership is the property that matters —
    /// every identifier that was committed is still there.
    ///
    /// EXCEPT THE ONES TRIM TOOK, which is the whole subtlety. At the cap, the legitimate next
    /// rewrite drops the OLDEST record to make room, so demanding every committed id would refuse
    /// every recovery on a full file. Trim only ever removes from the front, so the ids a real
    /// rewrite may be missing are a PREFIX of the committed order — and anything missing after the
    /// first one that is present is a record that went missing some other way.
    /// </summary>
    static bool KeepsCommittedRecords(Envelope candidate, Envelope committed)
    {
        if (candidate.Records.Count < committed.Records.Count) return false;

        var ids = new HashSet<string>(candidate.Records.Select(r => r.ClientOrderId), StringComparer.Ordinal);
        var i = 0;
        while (i < committed.Records.Count && !ids.Contains(committed.Records[i].ClientOrderId)) i++;
        for (; i < committed.Records.Count; i++)
            if (!ids.Contains(committed.Records[i].ClientOrderId)) return false;
        return true;
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
    /// The middle case is the awkward one: the committed file EXISTS but does not parse. Its
    /// generation is then unknowable, so the fingerprint is the whole of the test — which is sound,
    /// because the fingerprint is over the exact bytes and is strictly the stronger of the two
    /// checks. The generation comparison adds confirmation, never permission.
    /// </summary>
    static bool DescendsFrom(Envelope temp, string? committedText, Envelope? committed)
    {
        if (committedText is null) return false;
        if (!string.Equals(temp.Predecessor, Fingerprint(committedText), StringComparison.Ordinal)) return false;
        if (committed is null) return true;
        return temp.Generation == committed.Generation + 1;
    }

    /// <summary>
    /// Temps that might be an uncommitted rewrite of this file, in whatever order the directory
    /// gives them. <see cref="DescendsFrom"/> decides which one is real, and
    /// <see cref="AdoptUncommittedRewrite"/> declines when more than one qualifies.
    /// </summary>
    IEnumerable<string> Candidates()
    {
        if (_path is null) return [];
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return [];
            // UNORDERED, AND DELIBERATELY SO. It used to be newest-first, because mtime picked the
            // winner among several. It no longer picks anything: a candidate qualifies on lineage
            // alone, and two that both qualify are declined rather than ranked. Sorting by a
            // property that decides nothing is untested code that looks load-bearing.
            return Directory.GetFiles(dir, System.IO.Path.GetFileName(_path) + ".tmp*");
        }
        catch (Exception) { return []; }
    }

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
    /// <summary>The sidecar's last non-blank line with its timestamp stripped, or null.</summary>
    string? LastSidecarLine()
    {
        try
        {
            if (ErrorLogPath is not { } log || !File.Exists(log)) return null;
            var last = Array.FindLast(File.ReadAllLines(log), l => !string.IsNullOrWhiteSpace(l));
            if (last is null) return null;
            var space = last.IndexOf(' ');
            return space < 0 ? last : last[(space + 1)..];
        }
        catch (Exception) { return null; }
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
    static string? ReadTolerantly(string path, out bool failed)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                failed = false;
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { failed = false; return null; }
            catch (DirectoryNotFoundException) { failed = false; return null; }
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
        var current = ReadTolerantly(_path, out _);
        var currentHash = current is null ? null : Fingerprint(current);
        if (!string.Equals(currentHash, _committedHash, StringComparison.Ordinal))
        {
            NotOurs($"the witness file changed underneath this writer, so something else is writing " +
                    $"it. file={_path} claim={claim}");
            return false;
        }

        return Attempt(claim);
    }

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
    /// ONE OWNER PER WITNESS, AND THE LOCK IS HOW THAT IS DECIDED.
    ///
    /// Round 3 treated this as a contention reducer that a writer could proceed without, on the
    /// argument that the compare-and-swap made the result correct anyway. That was wrong in the
    /// direction that matters. Two writers were never a supported configuration — trap 35 calls a
    /// second bridge a misconfiguration — and every interleaving that a lock-optional design has to
    /// survive is an interleaving of a scenario the product does not support. Hardening a path
    /// nobody is meant to take costs correctness arguments forever; refusing it costs one branch.
    ///
    /// So: no lock, no write. A writer that cannot take the lock inside the budget reports the
    /// reason through <see cref="Trouble"/> and <see cref="Submitting"/> returns false, which makes
    /// <c>Place</c> refuse the order — the same refusal as any other unwritable witness, and the
    /// safe direction. A read-only directory or a denied ACL lands here too, which is correct: a
    /// witness that cannot take its own lock cannot be written either.
    ///
    /// The handle is released by process death like any other, so a crash cannot wedge it.
    /// </summary>
    IDisposable? Own()
    {
        if (_path is null) return null;
        var lockPath = _path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _notOwned = null;
                return held;
            }
            catch (Exception e)
            {
                if (attempt >= LockAttempts)
                {
                    _notOwned = $"another writer owns this witness ({lockPath}): {e.GetType().Name}";
                    return null;
                }
                Thread.Sleep(LockBackoffMs);
            }
        }
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
    ///   * SOMEBODY ELSE'S. Our claim is not on disk. The lineage is re-synced to what actually IS
    ///     committed so the next rewrite descends from THAT rather than from a file nobody has, the
    ///     overwrite is reported, and false goes back to the caller — which for <c>Place</c> means
    ///     refusing the order, which is right: the write-ahead record is not there.
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
        if (_degraded)
        {
            _degraded = false;
            // RE-READ RATHER THAN TRUST THE FLAG. _degraded was decided when this instance loaded,
            // and the file has been open to anything since. Appending a second RESOLVED under a
            // first says a gap was closed twice; worse, appending one at all when the tail already
            // says RESOLVED means this instance is reporting on a gap that was not its own.
            if (!string.Equals(LastSidecarLine(), ResolvedMarker, StringComparison.Ordinal))
                AppendToErrorLog($"{DateTimeOffset.UtcNow:O} {ResolvedMarker}", safety: false);
        }
    }

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
        var line = $"ERROR coid-witness rewrite did not land. file={_path} " +
                   (tempHoldsTheClaim ? $"temp_holding_newer_state={tmp} " : $"temp_not_written={tmp} ") +
                   $"claim={(string.IsNullOrEmpty(claim) ? "<none>" : claim)} " +
                   $"records_in_memory={_records.Count} {e.GetType().Name}: {e.Message}";
        LastWriteFailure = line;
        Note(line, safety: true);
    }

    /// <summary>
    /// One line into the sidecar, and it is ONE line however hard the input tries. Caller holds
    /// <see cref="_gate"/>.
    /// </summary>
    void Note(string line, bool safety = false)
    {
        _degraded = true;
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

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return;
            var log = System.IO.Path.Combine(dir, ErrorLogName);

            // ROTATED, NOT DELETED. Deleting was fine while the quota capped what could be lost; with
            // failures unrationed it is not, because the file being thrown away is now the one
            // holding them. One generation back is kept, which bounds the disk at twice the cap.
            if (File.Exists(log) && new FileInfo(log).Length > MaxErrorLogBytes)
            {
                var rolled = log + ".1";
                try { File.Delete(rolled); } catch (Exception) { }
                File.Move(log, rolled);
            }

            File.AppendAllText(log, line + Environment.NewLine);
        }
        catch (Exception) { /* a witness that cannot write must not become one that throws */ }
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
