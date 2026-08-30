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

    bool _loaded;
    bool _readFailed;
    bool _writeFailed;

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
    /// The live bridge's witness: a fresh session id, and the file under
    /// <see cref="Paths.BridgeDir"/>, which <see cref="Paths"/> has already created.
    /// </summary>
    public CoidWitness() : this(DefaultPath(), null, DefaultCap) { }

    /// <summary>
    /// The testable shape. <paramref name="sessionId"/> is null in every production use — a caller
    /// choosing the session id is exactly what property 2 above rules out — and tests pass null too:
    /// two instances over one path get two different sessions for free, which is the whole scenario.
    /// </summary>
    public CoidWitness(string? path, string? sessionId = null, int cap = DefaultCap)
    {
        _path = path;
        _cap = cap < 1 ? 1 : cap;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("n") : sessionId;
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
    /// </summary>
    public void Submitting(string clientOrderId, string? accountId, string? symbol, string? side,
                           decimal quantity, decimal? price)
    {
        if (string.IsNullOrEmpty(clientOrderId) || _path is null) return;
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
                Save();
            }
        }
        catch (Exception) { MarkWriteFailed(); }
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
                return $"session:{session},records:{_records.Count},prior:{prior}," +
                       $"io:{(_writeFailed ? "failed" : "ok")}";
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

        var json = ReadTolerantly(_path, out var failed);
        _readFailed = failed;
        if (json is null) return;

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(json, Opts);
            if (envelope?.Records is { } list)
                foreach (var r in list)
                    if (!string.IsNullOrEmpty(r.ClientOrderId)) _records.Add(r);
        }
        catch (JsonException)
        {
            // A truncated or hand-edited file is not a crash and is not evidence either. Treat it
            // as unreadable — the token says so — and let this session write a clean one. The
            // records lost were claims about orders from runs that have already ended.
            _readFailed = true;
            _records.Clear();
        }
    }

    /// <summary>
    /// A read that survives the file being REPLACED under it.
    ///
    /// <see cref="Save"/> replaces the whole file with <c>File.Move(..., overwrite: true)</c>, and
    /// a second bridge instance — or a probe — can be doing that at the moment this opens it. The
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
    /// Caller holds <see cref="_gate"/>. Failure is swallowed by the public methods above: an
    /// unwritable witness proves nothing later, which is the direction to fail in.
    /// </summary>
    void Save()
    {
        if (_path is null) return;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Envelope { Records = _records }, Opts));

        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, _path, overwrite: true); return; }
            catch (IOException) when (attempt < 3) { Thread.Sleep(20); }
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
        [JsonPropertyName("records")] public List<CoidWitnessRecord> Records { get; set; } = new();
    }
}
