using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE TOOLS ONE ROLE'S LAUNCH WAS GRANTED, AND NOTHING ELSE.
///
/// <para><b>Default deny is the shape, not a policy applied afterwards.</b> Six tools are named here;
/// a call by any other name is refused with the tool's own name in the sentence. There is no route by
/// which a string the model produced reaches a process, an HTTP client, a package manager or a path
/// outside the role's folder, because no such tool exists to reach them through — which is the
/// difference <c>docs/COUNCIL.md</c> asks for: "no shell, no arbitrary HTTP, no package installs, no
/// model-selected executable".</para>
///
/// <para><b>The role check is not here.</b> <c>trade</c> hands the operation to
/// <see cref="IGatewayCalls"/>, which is the gateway's OWN handler — the same one the agent pipe uses —
/// under the role and attempt the app assigned. So a Research worker's <c>trade buy</c> is refused by
/// the line that refuses a Research launch on the pipe, with the same code and the same sentence, and a
/// rule changed there cannot be true on one route and false on the other. Writing a second role check
/// in this file is the mistake this design exists to make impossible.</para>
///
/// <para><b>Every call is recorded whether or not it was served.</b> One <c>tool_call</c> row per call,
/// written by the app, which the worker cannot edit — round 4's "observed deliveries". A refusal is
/// recorded as carefully as a success: what a worker reached for and was denied is the half of the
/// record that says whether the grants are doing anything.</para>
/// </summary>
/// <param name="role">Which council role's launch this surface belongs to. The app's answer.</param>
/// <param name="home">The role's own folder, read per call: a prepare may have rebuilt it.</param>
/// <param name="attempt">The attempt the meter opened for the launch, for the ledger row.</param>
/// <param name="gateway">
/// The trading surface, read per call rather than captured: switching the trading platform replaces the
/// gateway, and a worker holding the old one would be asking a gateway nobody trades through. Null — or
/// a null answer — refuses <c>trade</c>, <c>data</c> and <c>report</c> in words rather than leaving them
/// silently absent, because a worker told a tool exists and then given nothing reports a broken platform.
/// </param>
public sealed class GrantedWorkerTools(
    string role,
    Func<string> home,
    Func<string?> attempt,
    Func<IGatewayCalls?>? gateway = null,
    ToolCallStore? ledger = null,
    Func<DateTimeOffset>? now = null,
    Func<string>? session = null) : IWorkerTools
{
    public const string ReadFile = "read_file";
    public const string ListFiles = "list_files";
    public const string WriteFile = "write_file";
    public const string Trade = "trade";
    public const string Data = "data";
    public const string Report = "report";

    /// <summary>The most one <c>read_file</c> or <c>list_files</c> may return. Bounded per CALL.</summary>
    public const int MaxBytesPerCall = ApiConversation.MaxRetrievalBytesPerCall;

    /// <summary>The most one <c>write_file</c> may stage. A worker writes reports, not datasets.</summary>
    public const int MaxWriteBytes = 256 * 1024;

    /// <summary>The most entries one <c>list_files</c> names before it counts the rest.</summary>
    public const int MaxListed = 200;

    /// <summary>
    /// WHERE A WORKER MAY WRITE: its own <c>out/</c> and its own <c>trading/</c>, and nowhere else.
    ///
    /// <c>out/</c> is staged output — the relay and <c>U-turn-commit</c> validate and publish it, and
    /// nothing a worker does publishes anything. <c>trading/</c> is the role's own plan and journal,
    /// which <c>WorkspaceRevisions</c> versions at the end of a turn. Every other directory in the
    /// role's home is readable and not writable, because a worker that could rewrite its own mission
    /// file would be a worker editing its own instructions.
    /// </summary>
    public static readonly string[] Writable = [CouncilRoles.OutDir, "trading"];

    /// <summary>
    /// THE TRADING OPS <c>trade</c> WILL CARRY, and a closed list rather than "whatever the gateway
    /// knows". A new op on the pipe is a deliberate decision to expose it to a worker, not an
    /// inheritance — and <c>hello</c> is a handshake that would mean nothing here.
    ///
    /// Mutating ops are on the list for EVERY role. That is not a hole: the list says which operations
    /// exist, and the gateway's own role check says who may use them. Keeping a per-role list here
    /// instead would be the second role check this class refuses to have.
    /// </summary>
    public static readonly string[] TradeOps =
    [
        Ops.Status, Ops.Connectors, Ops.Accounts, Ops.Account, Ops.Instruments, Ops.Quote,
        Ops.Positions, Ops.Position, Ops.Orders, Ops.Order, Ops.Executions, Ops.Pnl,
        Ops.MaterialList, Ops.MaterialNote, Ops.Schema,
        Ops.Buy, Ops.Sell, Ops.Modify, Ops.Cancel, Ops.CancelAll, Ops.Close, Ops.CloseAll
    ];

    /// <summary>The two ops <c>data</c> will carry. Both reads; there is no op that writes a dataset.</summary>
    public static readonly string[] DataOps = [Ops.DataList, Ops.DataBars];

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public IReadOnlyList<ToolSpec> Offered { get; } = Specs();

    /// <inheritdoc />
    public async Task<ToolAnswer> InvokeAsync(ToolRequest call, CancellationToken ct = default)
    {
        var (args, badJson) = Parse(call.Arguments);
        var answer = badJson is not null
            ? ToolAnswer.Refused(badJson, call.Name)
            : call.Name switch
            {
                ReadFile => Read(args),
                ListFiles => List(args),
                WriteFile => Write(args),
                Trade => await Call(Trade, TradeOps, args, ct),
                Data => await Call(Data, DataOps, args, ct),
                Report => await Call(Report, [Ops.Report], args, ct),
                // DEFAULT DENY, and it is the switch's own default rather than a check somewhere above.
                _ => ToolAnswer.Refused(WorkerTools.NotGranted(call.Name), call.Name)
            };

        Record(call, answer);
        return answer;
    }

    // ---- the file tools ---------------------------------------------------------------------------

    ToolAnswer Read(JsonElement args)
    {
        var path = Str(args, "path");
        // READS SEE THE WHOLE OF THE ROLE'S OWN HOME, `in/` included: what the app delivered to this
        // role is half of what the role was given, and a worker that could not read its own brief
        // would have to be told it in the prompt instead, where nothing bounds it.
        var (full, refusal) = ToolPaths.Resolve(home(), path);
        if (refusal is not null) return ToolAnswer.Refused(refusal, Summary(ReadFile, path));

        try
        {
            if (!File.Exists(full))
                return ToolAnswer.Refused($"'{path}' does not exist.", Summary(ReadFile, path));

            var bytes = new FileInfo(full!).Length;
            using var stream = File.OpenRead(full!);
            var buffer = new byte[Math.Min(MaxBytesPerCall, bytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            return ToolAnswer.Ok(
                bytes > MaxBytesPerCall
                    ? text + $"\n[cut: '{path}' is {bytes:N0} bytes and one read returns at most {MaxBytesPerCall:N0}]"
                    : text,
                Summary(ReadFile, path));
        }
        catch (Exception ex)
        {
            return ToolAnswer.Refused($"'{path}' could not be read: {ex.Message}", Summary(ReadFile, path));
        }
    }

    ToolAnswer List(JsonElement args)
    {
        var path = Str(args, "path") ?? ".";
        var (full, refusal) = ToolPaths.Resolve(home(), path == "." ? "." : path);
        // "." resolves to the root itself, which Resolve refuses as not INSIDE it — correct for a file
        // and wrong for a listing, so the root is named here rather than by loosening that rule.
        var at = path == "." ? Path.GetFullPath(home()) : full;
        if (path != "." && refusal is not null) return ToolAnswer.Refused(refusal, Summary(ListFiles, path));

        try
        {
            if (!Directory.Exists(at))
                return ToolAnswer.Refused($"'{path}' is not a folder here.", Summary(ListFiles, path));

            var entries = new List<string>();
            foreach (var dir in Directory.GetDirectories(at!).OrderBy(d => d, StringComparer.Ordinal))
                entries.Add(Path.GetFileName(dir) + "/");
            foreach (var file in Directory.GetFiles(at!).OrderBy(f => f, StringComparer.Ordinal))
                entries.Add($"{Path.GetFileName(file)}  {new FileInfo(file).Length:N0} bytes");

            var shown = entries.Take(MaxListed).ToList();
            var text = string.Join("\n", shown);
            if (entries.Count > shown.Count)
                text += $"\n[{entries.Count - shown.Count:N0} more not listed]";

            return ToolAnswer.Ok(text.Length == 0 ? "(empty)" : text, Summary(ListFiles, path));
        }
        catch (Exception ex)
        {
            return ToolAnswer.Refused($"'{path}' could not be listed: {ex.Message}", Summary(ListFiles, path));
        }
    }

    ToolAnswer Write(JsonElement args)
    {
        var path = Str(args, "path");
        var content = Str(args, "content") ?? "";
        var (full, refusal) = ToolPaths.Resolve(home(), path, Writable);
        if (refusal is not null) return ToolAnswer.Refused(refusal, Summary(WriteFile, path));

        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxWriteBytes)
            return ToolAnswer.Refused(
                $"that is {bytes:N0} bytes and one file may be {MaxWriteBytes:N0}. Write less, or write "
                + "several files.", Summary(WriteFile, path, bytes));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full!)!);
            File.WriteAllText(full!, content);
            // STAGED, NOT PUBLISHED, and the answer says so: nothing a worker writes is delivered by
            // writing it. The relay validates what is in `out/` and commits the delivery.
            return ToolAnswer.Ok($"wrote {bytes:N0} bytes to {path}. It is staged: TradeAgent validates "
                                 + "and publishes what you leave there, and nothing you do publishes it.",
                Summary(WriteFile, path, bytes));
        }
        catch (Exception ex)
        {
            return ToolAnswer.Refused($"'{path}' could not be written: {ex.Message}",
                Summary(WriteFile, path, bytes));
        }
    }

    // ---- the gateway tools ------------------------------------------------------------------------

    /// <summary>
    /// One operation through the gateway's own handler, under the identity the APP assigned. The op
    /// must be on the tool's own closed list; everything else about who may do what is the gateway's.
    /// </summary>
    async Task<ToolAnswer> Call(string tool, IReadOnlyList<string> allowed, JsonElement args,
        CancellationToken ct)
    {
        var op = Str(args, "op") ?? (allowed.Count == 1 ? allowed[0] : null);
        if (op is null || !allowed.Contains(op))
            return ToolAnswer.Refused(
                $"'{op ?? "(none)"}' is not an operation '{tool}' carries. It carries: "
                + string.Join(", ", allowed) + ".", Summary(tool, op));

        if (gateway?.Invoke() is not { } surface)
            return ToolAnswer.Refused($"'{tool}' is not available in this build's configuration.",
                Summary(tool, op));

        var request = new IpcRequest
        {
            Op = op,
            Session = Session(),
            RequestId = RequestId(args),
            Args = Fields(args)
        };

        IpcResponse response;
        try { response = await surface.CallAsync(request, role, attempt(), ct); }
        catch (OperationCanceledException) { throw; }
        // A THROW OUT OF THE GATEWAY IS NOT A REFUSAL OF THE REQUEST and must not be recorded as one
        // being denied; it is an answer nobody could give. The worker is told, and the row says so.
        catch (Exception ex)
        {
            return ToolAnswer.Refused($"'{op}' could not be answered: {ex.Message}", Summary(tool, op));
        }

        if (response.Ok)
            return ToolAnswer.Ok(Json.Write(response.Data), Summary(tool, op));

        // THE GATEWAY'S OWN WORDS, both of them. `user_message` is what the owner would read and
        // `message` is the technical detail; a worker refused an order needs the second to decide what
        // to do next, and the first is what it should repeat if it reports the refusal upward.
        var error = response.Error;
        return ToolAnswer.Refused(
            $"{error?.Code}: {error?.UserMessage} ({error?.Message})", Summary(tool, op));
    }

    /// <summary>
    /// The session name this surface's calls carry. The prepared agent's own where there is one, and a
    /// name that says which role otherwise — read per call because a prepare mints a new session id, and
    /// a captured one would attribute a later turn's fills to an earlier session.
    ///
    /// It is a NAME and never an authority: <c>AgentContext.ForAgent</c> cannot return an operator
    /// whatever this says, and the reserved word is refused by the handler as a tripwire.
    /// </summary>
    string Session() =>
        session?.Invoke() is { Length: > 0 } named ? named : $"harness-{role}";

    /// <summary>
    /// The idempotency key a mutating op is sent under: the worker's own where it gave a usable one,
    /// and a minted one otherwise.
    ///
    /// <para>A worker's own id is worth passing through — it is what makes a retry safe rather than a
    /// second order — and a MINTED one is what stops an op arriving with none. The gateway checks the
    /// shape either way and refuses a reserved prefix, so nothing here has to be trusted.</para>
    /// </summary>
    string RequestId(JsonElement args) =>
        Str(args, "request_id") is { Length: > 0 } given ? given : Minted();

    string Minted() =>
        $"w-{role}-{_now().UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-"
        + Guid.NewGuid().ToString("n")[..8];

    /// <summary>
    /// The model's arguments, minus the two this class consumed. Handed to the gateway as the op's own
    /// arguments so the pipe's own parsers — the strict number reading, the named flags, the enum
    /// matching — are what read them, rather than a second reading written here.
    /// </summary>
    static Dictionary<string, JsonElement>? Fields(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in args.EnumerateObject())
        {
            if (p.NameEquals("op") || p.NameEquals("request_id")) continue;
            fields[p.Name] = p.Value.Clone();
        }
        return fields;
    }

    // ---- the record -------------------------------------------------------------------------------

    void Record(ToolRequest call, ToolAnswer answer) =>
        ledger?.Record(new ToolCallRow
        {
            At = _now(),
            Attempt = attempt(),
            Role = role,
            Tool = call.Name,
            Argument = answer.Summary,
            Bytes = Encoding.UTF8.GetByteCount(answer.Content),
            Served = answer.Served,
            Refusal = answer.Served ? null : answer.Content
        });

    static string Summary(string tool, string? what, int? bytes = null) =>
        bytes is null ? $"{tool} {what}" : $"{tool} {what} ({bytes:N0} bytes)";

    // ---- the arguments ----------------------------------------------------------------------------

    /// <summary>
    /// The model's argument string, parsed. A body that is not an object is a REFUSAL rather than an
    /// empty one: a tool run with arguments nobody could read is a tool run on a guess.
    /// </summary>
    static (JsonElement Args, string? Refusal) Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (default, null);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? (doc.RootElement.Clone(), null)
                : (default, "the arguments were not a JSON object, so nothing was run.");
        }
        catch (JsonException ex)
        {
            return (default, $"the arguments were not JSON this build could read ({ex.Message}), so "
                             + "nothing was run.");
        }
    }

    static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.GetRawText()
            }
            : null;

    // ---- what the model is told exists ------------------------------------------------------------

    static IReadOnlyList<ToolSpec> Specs() =>
    [
        new(ReadFile,
            "Read one text file inside your own folder, including anything TradeAgent delivered into "
            + $"in/. Paths are relative to your folder; '..', absolute paths and links are refused. At "
            + $"most {MaxBytesPerCall:N0} bytes per read, and a longer file is cut with a note saying so.",
            Schema(("path", "string", "Relative path inside your folder, e.g. in/brief.md"))),

        new(ListFiles,
            "List one folder inside your own folder. Same path rules as read_file. Omit the path for "
            + "your folder's root.",
            Schema(("path", "string", "Relative folder, e.g. trading. Omit for the root."))),

        new(WriteFile,
            $"Write one text file into {string.Join(" or ", Writable.Select(w => w + "/"))}. What you "
            + "leave in out/ is STAGED: TradeAgent validates it and publishes it, and nothing you do "
            + "publishes anything. Nowhere else is writable — not your mission file, not another "
            + $"role's folder. At most {MaxWriteBytes:N0} bytes.",
            Schema(("path", "string", "Relative path under out/ or trading/, e.g. out/report.md"),
                   ("content", "string", "The whole file. It replaces what is there."))),

        new(Trade,
            "One operation on TradeAgent's trading surface, under the identity TradeAgent assigned your "
            + "launch. Reads answer for every role. Anything that places, changes or cancels an order is "
            + "refused unless your role may move money, and no argument here changes that: there is no "
            + "operation that grants permission, changes the mode, lifts the kill switch or approves "
            + "anything. Run 'schema' for the full description of every operation and its arguments.",
            Schema(("op", "string", "One of: " + string.Join(", ", TradeOps)),
                   ("request_id", "string", "Idempotency key for a mutating op. Reuse it to retry safely."))),

        new(Data,
            "Historical market data TradeAgent holds: 'data-list' for what there is and where every "
            + "byte came from, 'data-bars' for the bars. Bars are hypothesis evidence — they establish "
            + "no fill, no queue position and no intrabar ordering. Nothing here collects or changes "
            + "data; the account owner does that in TradeAgent.",
            Schema(("op", "string", "data-list or data-bars"),
                   ("pair", "string", "For data-bars, e.g. BTCUSDT"),
                   ("from", "string", "ISO-8601 date or instant, inclusive"),
                   ("to", "string", "ISO-8601 date or instant, inclusive"))),

        new(Report,
            "The account owner's daily report for a local day, exactly as they read it. It is a READ: "
            + "there is no operation that writes, rewrites or deletes one, because it is the record "
            + "your work is judged by.",
            Schema(("day", "string", "A local calendar day, yyyy-MM-dd. Omit for today.")))
    ];

    /// <summary>
    /// A JSON-schema object for the provider's tool contract. Nothing is REQUIRED: a missing argument
    /// is answered in words by the tool that needed it, which is a better failure than a model being
    /// told its call was malformed by a validator it cannot see.
    /// </summary>
    static object Schema(params (string Name, string Type, string Description)[] fields) => new
    {
        type = "object",
        properties = fields.ToDictionary(f => f.Name, f => (object)new { type = f.Type, description = f.Description }),
        additionalProperties = false
    };
}
