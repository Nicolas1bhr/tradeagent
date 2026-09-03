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
    /// Bounded twice: <see cref="MaxLoggedFailures"/> lines per session, and restarted past
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
    /// NOT MEASURED. The one data point is a GitHub CI failure on `test (windows-latest)` which says
    /// only that the previous budget — three retries, 60 ms — was not enough at least once. There is
    /// no distribution behind these numbers and there will not be one until a Windows run produces
    /// it.
    /// </summary>
    const int ReplaceAttempts = 5;

    /// <summary>Multiplied by the attempt number, so the waits lengthen: 20, 40, 60, 80 ms.</summary>
    const int ReplaceBackoffMs = 20;

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
                EnsureLoaded();
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
                return Save();
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
                EnsureLoaded();
                var i = _records.FindIndex(r => string.Equals(r.ClientOrderId, clientOrderId, StringComparison.Ordinal));
                if (i < 0) return;

                var record = _records[i];
                // Not ours to write on. Nothing is recorded, and nothing is reported: this is the
                // ordinary case every time a prior session's order shows up in the book.
                if (!string.Equals(record.SessionId, SessionId, StringComparison.Ordinal)) return;
                if (!string.IsNullOrEmpty(record.BrokerOrderId)) return;

                _records[i] = record with { BrokerOrderId = brokerOrderId, IdentifiedAt = DateTimeOffset.UtcNow };
                Save();
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

    /// <summary>Every record on file, newest last. For the probe and for tests; not a proof path.</summary>
    public IReadOnlyList<CoidWitnessRecord> All()
    {
        try { lock (_gate) { EnsureLoaded(); return _records.ToArray(); } }
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

        // Asked before anything below can write one.
        try { _degraded = ErrorLogPath is { } log && File.Exists(log); } catch (Exception) { }

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
        if (AdoptUncommittedRewrite(committedText, committed)) return;

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
    bool AdoptUncommittedRewrite(string? committedText, Envelope? committed)
    {
        foreach (var candidate in Candidates())
        {
            var text = ReadTolerantly(candidate, out var unreadable);
            if (text is null)
            {
                if (unreadable) { _candidateUnreadable = true; RejectCandidate(candidate, "it could not be read"); }
                continue;
            }

            var envelope = Parse(text);
            if (envelope is null)
            {
                _candidateUnreadable = true;
                RejectCandidate(candidate, "it is not a witness envelope");
                continue;
            }
            if (envelope.Records.Count == 0) { RejectCandidate(candidate, "it contains no records"); continue; }
            if (!DescendsFrom(envelope, committedText, committed))
            {
                RejectCandidate(candidate,
                    $"it does not descend from the committed file " +
                    $"(temp generation={envelope.Generation} predecessor={envelope.Predecessor ?? "<none>"}; " +
                    $"committed generation={(committed is null ? "<unreadable>" : committed.Generation.ToString())} " +
                    $"fingerprint={(committedText is null ? "<absent>" : Fingerprint(committedText))})");
                continue;
            }

            Take(envelope);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether one envelope is the rewrite THIS committed content was about to become. See
    /// <see cref="AdoptUncommittedRewrite"/> for the argument; this is only the arithmetic.
    ///
    /// The middle case is the awkward one: the committed file EXISTS but does not parse. Its
    /// generation is then unknowable, so the fingerprint is the whole of the test — which is sound,
    /// because the fingerprint is over the exact bytes and is strictly the stronger of the two
    /// checks. The generation comparison adds confirmation, never permission.
    /// </summary>
    static bool DescendsFrom(Envelope temp, string? committedText, Envelope? committed)
    {
        if (committedText is null) return temp.Predecessor is null && temp.Generation == 1;
        if (!string.Equals(temp.Predecessor, Fingerprint(committedText), StringComparison.Ordinal)) return false;
        if (committed is null) return true;
        return temp.Generation == committed.Generation + 1;
    }

    /// <summary>
    /// Temps that might be an uncommitted rewrite of this file, newest first. Time orders them and
    /// nothing more: <see cref="DescendsFrom"/> decides which one is real.
    /// </summary>
    IEnumerable<string> Candidates()
    {
        if (_path is null) return [];
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return [];
            return Directory.GetFiles(dir, System.IO.Path.GetFileName(_path) + ".tmp*")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .ToArray();
        }
        catch (Exception) { return []; }
    }

    /// <summary>A temp beside the witness that is not a rewrite of it is a fact, not noise.</summary>
    void RejectCandidate(string candidate, string why) =>
        Note($"ignored {candidate}: {why}");

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
    /// </summary>
    static string Fingerprint(string text)
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
    bool Save()
    {
        if (_path is null) return false;
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
        try { File.WriteAllText(tmp, text); }
        catch (Exception e) { ReportWriteFailure(e, tmp); return false; }

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
                _replace(tmp, _path);
                return Committed(text);
            }
            catch (Exception e) when (Transient(e) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceBackoffMs * attempt);
            }
            catch (Exception e) { _stranded.Add(tmp); ReportWriteFailure(e, tmp); return false; }
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
    bool Committed(string text)
    {
        var ours = Fingerprint(text);
        var actual = _path is null ? null : ReadTolerantly(_path, out _);

        if (actual is null)
        {
            _generation++;
            _committedHash = ours;
            SweepStranded();
            Note($"WARN coid-witness rewrite landed but could not be read back to confirm. file={_path}");
            return true;
        }

        if (string.Equals(Fingerprint(actual), ours, StringComparison.Ordinal))
        {
            _generation++;
            _committedHash = ours;
            SweepStranded();
            return true;
        }

        _committedHash = Fingerprint(actual);
        _generation = Parse(actual)?.Generation ?? 0;
        _writeFailed = true;
        var newest = _records.Count > 0 ? _records[^1].ClientOrderId : "<none>";
        LastWriteFailure = $"ERROR coid-witness rewrite was overwritten by another writer before it " +
                           $"could be confirmed. file={_path} newest_claim={newest}";
        Note(LastWriteFailure);
        return false;
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
    void ReportWriteFailure(Exception e, string tmp)
    {
        _writeFailed = true;
        var newest = _records.Count > 0 ? _records[^1].ClientOrderId : "<none>";
        var line = $"ERROR coid-witness rewrite did not land. file={_path} " +
                   $"temp_holding_newer_state={tmp} newest_claim={newest} records_in_memory={_records.Count} " +
                   $"{e.GetType().Name}: {e.Message}";
        LastWriteFailure = line;
        Note(line);
    }

    /// <summary>
    /// One line into the sidecar, and it is ONE line however hard the input tries. Caller holds
    /// <see cref="_gate"/>.
    /// </summary>
    void Note(string line)
    {
        _degraded = true;
        AppendToErrorLog($"{DateTimeOffset.UtcNow:O} {OneLine(line)}");
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
    /// Appends one line to <see cref="ErrorLogName"/> beside the witness. Bounded at
    /// <see cref="MaxLoggedFailures"/> lines per session so a permanently unwritable destination
    /// cannot turn every order into a log line, and the file is restarted past
    /// <see cref="MaxErrorLogBytes"/> so it cannot grow without limit across sessions.
    ///
    /// Every failure here is discarded. The operation that just failed was a rename onto a file
    /// something else has open, which says nothing about whether an append to a different name
    /// works — but if it does not, the answer is silence, not an exception out of <c>Place</c>.
    /// </summary>
    void AppendToErrorLog(string line)
    {
        if (_loggedFailures >= MaxLoggedFailures) return;
        _loggedFailures++;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return;
            var log = System.IO.Path.Combine(dir, ErrorLogName);
            if (File.Exists(log) && new FileInfo(log).Length > MaxErrorLogBytes) File.Delete(log);
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
