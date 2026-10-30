using System.Collections.Concurrent;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE BROKER-ONLY CODEX SEAT, AS CODE (<c>U-thinking-seats-c</c>; <c>docs/ORCHESTRATION.md</c> § 2.7, § 5; <c>R-exec-server</c>
/// DECISION 2): its pinned model, the tools that model is expected to be offered, the tools no broker seat may ever be offered, and
/// the check that a <c>runtimes.json</c> left the class's security fields as this build compiled them.
///
/// <para><b>Why a pinned model.</b> The class holds only on a model with classic tools. A code-mode-only model (gpt-6-astra) is still
/// OFFERED code mode's <c>exec</c> with the code-mode host disabled; at codex 0.160.1 every such call fails closed ("code-mode host is
/// disabled" — attempt 4's transcript, <c>fleet/tmp/M4-regression/report.md</c>), and with the host on, the model-written JavaScript
/// runs in a <c>codex-code-mode-host</c> child of the codex that holds the sign-in (Law 2, § 2.8). So the class keeps the host disabled,
/// pins a model it runs with classic tools — gpt-6-luna on the app's catalog without its <c>code_mode_only</c> (<c>U-cell-model-catalog</c>) —
/// and refuses any surface offering <c>exec</c> (<see cref="NeverOffered"/>); the CLI class runs the host (<c>U-cli-code-mode</c>).
/// <b>Why a verified surface.</b> codex lists no offered tools itself, and a runtime update or the account's
/// default model changes them, so before every launch the app reads them from a dry run against its own loopback mock
/// (<see cref="CodexSurfaceProbe"/>), cached by binary, argument template and model, and refuses anything but the expected set.</para>
///
/// <para><b>What it is not.</b> MEDIATED, not hard: no model-reachable tool crosses, but the codex process itself holds the login and
/// could. The model's metadata is PINNED by the app's catalog (<c>U-cell-model-catalog</c>, <see cref="SeatPlan.ModelCatalog"/>): the dry
/// run and a live turn read the same bytes, so they agree on the tools offered; whether the owner's plan serves gpt-6-luna on that
/// edited metadata stays UNKNOWN until one real turn, on his word. A per-request inspecting proxy is the later hardening.</para>
/// </summary>
public static class BrokerSeat
{
    /// <summary>The name the app's door is configured under in a broker seat's deny set, and the one tool server it may list.</summary>
    public const string DoorServer = "tradeagent";

    /// <summary>
    /// THE MODEL A BROKER OR CELL SEAT RUNS ON, WITH CLASSIC TOOLS (<c>U-cell-model-catalog</c>, option C of 2026-10-25): gpt-5.5 retired
    /// from Codex with ChatGPT sign-in on 2026-10-14 and every model on the owner's plan is <c>code_mode_only</c>, so the seat runs
    /// gpt-6-luna on the app's own catalog — the vendor's bundled entry without <c>tool_mode</c> (<see cref="SeatPlan.ModelCatalog"/>).
    /// Offered exactly <see cref="Expected"/>'s set on the loopback mock; under the bundled metadata the same argv is offered code
    /// mode's <c>exec</c> and refused.
    /// </summary>
    public const string Model = "gpt-6-luna";

    /// <summary>The model <paramref name="seatClass"/> is pinned to, or null for a class that pins none.</summary>
    public static string? ModelOf(string seatClass) => IsVerified(seatClass) ? Model : null;

    /// <summary>
    /// THE CLASSES WHOSE SEAT IS VERIFIED BEFORE EVERY LAUNCH (<c>U-cell-linux-c</c> item 4): the broker and route (a)'s cell — a pinned
    /// model, the door, this build's own security fields and the offered surface held to the class's expected set.
    /// </summary>
    public static bool IsVerified(string seatClass) => seatClass is SeatPlan.BrokerClass or SeatPlan.CellClass;

    /// <summary>The words a refusal names a verified class by.</summary>
    public static string Kind(string seatClass) => seatClass == SeatPlan.CellClass ? "a route (a) cell seat" : "a broker-only seat";

    /// <summary>
    /// THE TOOLS EACH PINNED MODEL IS OFFERED UNDER THE BROKER CLASS, compiled: the app's door's tools are deferred behind
    /// <c>tool_search</c>, and the rest are codex's own clock and MCP-resource tools, none of which reaches the computer. A set that
    /// differs refuses — a release, a key or a model that changed what is offered is a new set this build has not seen.
    ///
    /// <para><b>Why each stays</b> (<c>U-broker-seat-fixes</c> item 2): <c>tool_search</c> is how the model reaches the door's tools at all
    /// (they are deferred behind it); <c>clock.curr_time</c> reads the time, which a seat that paces its own wakes needs, and reaches nothing;
    /// <c>list_mcp_resources</c>, <c>list_mcp_resource_templates</c> and <c>read_mcp_resource</c> are registered by codex whenever any tool
    /// server is configured (vendor <c>core/src/tools/spec_plan.rs:1132-1145</c>) and read only from that server — the app's door, which
    /// serves no resources. <b>Gone:</b> <c>request_user_input</c> and <c>request_user_input_async</c> — no user answers a confined seat's
    /// exec turn; the class's deny set and the app's catalog switch them off, and a surface still offering one is refused here.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Model] = ["clock.curr_time", "list_mcp_resource_templates", "list_mcp_resources", "read_mcp_resource", "tool_search"]
    };

    /// <summary>
    /// ROUTE (a)'S CELL CLASS'S EXPECTED SURFACE (<c>U-cell-linux-c</c> L2), RUN on codex-cli 0.160.1 and the pinned model on the app's
    /// catalog (classic tools: no <c>exec</c>; gpt-5.5 on 2026-10-22, gpt-6-luna under <c>U-cell-model-catalog</c>): the shell, patch and image tools — which run in the cell — and the door's. Code mode's
    /// <c>exec</c>, sub-agents and anything else would run in the harness beside the login and are refused before admission.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ExpectedInCell = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // NO QUESTION NOBODY ANSWERS (U-broker-seat-fixes item 2): request_user_input and its async form are off for the cell as for the broker.
        [Model] = ["apply_patch", "clock.curr_time", "exec_command", "list_mcp_resource_templates", "list_mcp_resources", "read_mcp_resource",
                   "tool_search", "view_image", "write_stdin"]
    };

    /// <summary>The expected surface of <paramref name="seatClass"/> on <paramref name="model"/>, or null for none this build knows.</summary>
    public static string[]? ExpectedFor(string seatClass, string model) =>
        (seatClass == SeatPlan.CellClass ? ExpectedInCell : Expected).TryGetValue(model, out var e) ? e : null;

    /// <summary>
    /// TOOLS NO BROKER SEAT IS EVER OFFERED, named in the refusal: what runs a command, writes a file, reads an image off the disk, starts
    /// a sub-agent or runs model-written code — on this computer or beside the process that holds the sign-in (code mode's host is its child).
    /// </summary>
    public static readonly string[] HostTools =
    [
        "exec", "exec_command", "write_stdin", "shell", "shell_command", "local_shell", "unified_exec", "apply_patch", "view_image",
        "js_repl", "spawn_agent", "send_input", "image_generation", "web_search"
    ];

    /// <summary>
    /// TOOLS NO VERIFIED CLASS IS EVER OFFERED, whatever its expected set says (<c>U-cell-model-catalog</c>, option C): code mode's
    /// <c>exec</c>. The broker and cell seats run gpt-6-luna on a catalog without its <c>tool_mode</c> and with code mode's host off; an
    /// <c>exec</c> offered means that edit stopped holding, and the seat is refused before admission.
    /// </summary>
    public static readonly string[] NeverOffered = ["exec"];

    /// <summary>Whether this build has a broker class for the built-in runtime <paramref name="runtimeId"/> — code, read once.</summary>
    public static bool Offers(string runtimeId) => Offers(runtimeId, SeatPlan.BrokerClass);

    /// <summary>
    /// Whether this build has <paramref name="seatClass"/> for the built-in runtime <paramref name="runtimeId"/> — the broker class or
    /// route (a)'s cell class (<c>U-execution-environment</c>) — code, read once.
    /// </summary>
    public static bool Offers(string runtimeId, string seatClass) =>
        WithClass.Value.Contains((runtimeId, seatClass));

    static readonly Lazy<HashSet<(string, string)>> WithClass = new(() =>
        RuntimeCatalog.BuiltIn().SelectMany(m => m.Seat?.Args.Keys.Select(c => (m.Id, c)) ?? []).ToHashSet());

    static RuntimeManifest? BuiltIn(string runtimeId) => RuntimeCatalog.BuiltIn().FirstOrDefault(m => m.Id == runtimeId);

    // ---- the class's security fields are this build's ------------------------------------------------------------------

    /// <summary>
    /// NULL WHEN <paramref name="manifest"/> CARRIES THE BROKER CLASS EXACTLY AS THIS BUILD COMPILED IT, or the refusal naming the field
    /// and both versions — the file's and this build's. <c>runtimes.json</c> replaces a manifest whole and stays the vendor-commands
    /// data file (<c>CLAUDE.md</c>); a seat class's pin and deny set are never read from it (ORCHESTRATION § 2.7). Refuse, never guess.
    /// </summary>
    public static string? OverrideRefusal(RuntimeManifest manifest) => OverrideRefusal(manifest, SeatPlan.BrokerClass);

    /// <summary>The same check for <paramref name="seatClass"/>, one of the verified classes: its security fields are this build's own.</summary>
    public static string? OverrideRefusal(RuntimeManifest manifest, string seatClass)
    {
        var kind = Kind(seatClass);
        ArgumentNullException.ThrowIfNull(manifest);
        var built = BuiltIn(manifest.Id);
        if (built?.Seat is not { } own || !own.Args.ContainsKey(seatClass))
            return $"TradeAgent will not start {manifest.DisplayName} as {kind}: this build ({Versions.App}) has no {seatClass} class " +
                   $"for '{manifest.Id}'. Nothing was started.";
        if (manifest.Seat is not { } seat)
            return $"TradeAgent will not start {manifest.DisplayName} as {kind}: its entry in runtimes.json has no seat profile, " +
                   $"and this build ({Versions.App}) compiles one. Remove the '{manifest.Id}' entry from runtimes.json to continue. Nothing was started.";

        foreach (var (field, file, build) in Fields(manifest, seat, PinOf(manifest), seatClass).Zip(Fields(built, own, BuildPin(built, manifest), seatClass),
                     (a, b) => (a.Field, a.Value, b.Value)))
        {
            if (file == build) continue;
            return $"TradeAgent will not start {manifest.DisplayName} as {kind}: runtimes.json gives its {field} as " +
                   $"{Fingerprint(file)}, and this build ({Versions.App}) compiles it as {Fingerprint(build)}. A verified seat's security " +
                   $"fields are this build's own and never read from a file. Remove the '{manifest.Id}' entry from runtimes.json, or put " +
                   "this build's value back, to continue. Nothing was started.";
        }
        return null;
    }

    /// <summary>
    /// The broker class's security fields, each as one comparable string, in a fixed order — the program's pin first: a broker seat's
    /// program is this build's pinned codex, and an override that swaps the program and its digest together would otherwise pass the
    /// pin check and receive the seat's environment before anything else refused it (fix pass 1, R1).
    ///
    /// <para>EACH VALUE IS AN UNAMBIGUOUS ENCODING (fix pass 2): a list as its count and each element length-prefixed, a dictionary as
    /// its ordinal-ordered keys and values the same way — never a separator-joined string, where one element holding the separator
    /// (<c>"codex/hooks.json\u001fcodex/environments.toml"</c>) compared equal to two and dropped a check. Two values are equal exactly
    /// when the sequences are; the string form remains only for the refusal's fingerprint.</para>
    /// </summary>
    static IEnumerable<(string Field, string Value)> Fields(RuntimeManifest m, SeatPlan seat, string pin, string seatClass)
    {
        static string Seq(IEnumerable<string>? values)
        {
            var list = (values ?? []).ToList();
            var sb = new StringBuilder().Append(list.Count).Append('[');
            foreach (var v in list) sb.Append(v.Length).Append(':').Append(v);
            return sb.Append(']').ToString();
        }
        static string Pairs(IReadOnlyDictionary<string, string>? kv) =>
            Seq(kv?.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => new[] { p.Key, p.Value }));

        yield return ("program pin", pin);
        // WHAT MUST NOT EXIST IN THE SEAT'S HOME AND WORKSPACE, AND WHICH FILES HOLD ITS CREDENTIAL (fix pass 1, R2): codex's
        // environments.toml in the home replaces CODEX_EXEC_SERVER_URL=none, and the surface probe's throwaway home has none, so its
        // accepted surface proves nothing about a launched seat whose override dropped the entry (CRED-001, Law 2).
        yield return ("home's forbidden paths", Seq(seat.Forbidden));
        yield return ("workspace's forbidden names", Seq(seat.WorkspaceForbidden));
        yield return ("credential files", Seq(seat.PrivateFiles));
        yield return ("deny set", Seq(seat.ArgsFor(seatClass)));
        yield return ("class environment", Pairs(seat.ClassEnvironment.GetValueOrDefault(seatClass)));
        yield return ("seat environment", Pairs(seat.Environment));
        yield return ("feature allowlist", Seq(seat.FeaturesFor(seatClass)));
        // THE PINNED MODEL'S CATALOG (U-cell-model-catalog): where the seat home keeps it, the shipped file and its pin.
        yield return ("model catalog", Seq([seat.ModelCatalog, seat.ModelCatalogSource, seat.ModelCatalogSha256]));
        // THE SENTENCE THAT REFUSES A SEAT ON TOOLS NO CHECK HAS SEEN (U-cell-model-catalog, fix pass): codex's fallback metadata and
        // code mode's unavailability, read by the dry run and by every turn. Data for the CLI class; for a verified class it is this
        // build's own, whole — an override that clears it or drops one alternative would let such a seat be admitted.
        yield return ("tools-unavailable pattern", Seq([m.ToolsUnavailablePattern ?? ""]));
        yield return ("tool-server allowlist", Seq(seat.ServersFor(seatClass)));
        yield return ("feature report command", Seq(seat.FeaturesArgs));
        yield return ("tool-server report command", Seq(seat.McpArgs));
        yield return ("model flag", Seq(m.ModelArgs));
        yield return ("unattended flags", Seq(m.UnattendedArgs));
        yield return ("turn command", Seq(m.ExecArgs) + Seq(m.ResumeArgs));
    }

    /// <summary>A manifest's program pin as compared: trimmed and lower-case, as <see cref="SeatProfile.IsPin"/> reads it; empty for none.</summary>
    static string PinOf(RuntimeManifest m) => m.Install.ExecutableSha256?.Trim().ToLowerInvariant() ?? "";

    /// <summary>
    /// THIS BUILD'S PIN for <paramref name="manifest"/>'s runtime: the built-in manifest's — or, in a test host only, the manifest's own
    /// when the test rig registered it (<see cref="PinForTest"/>), because a test's codex is a script whose digest no build compiles.
    /// </summary>
    static string BuildPin(RuntimeManifest built, RuntimeManifest manifest) =>
        PuppetSeats.TestHost && TestPins.ContainsKey(PinOf(manifest)) ? PinOf(manifest) : PinOf(built);

    /// <summary>
    /// THE SCRIPTS A TEST RIG STANDS IN FOR THIS BUILD'S PROGRAM, by digest. Internal and written only by the test assembly
    /// (<c>InternalsVisibleTo</c>); read only while <see cref="PuppetSeats.TestHost"/> is set, which nothing in the product sets — no
    /// file, setting or pipe op reaches either (AUTH-001). A digest no test registered is compared against the build's own pin.
    /// </summary>
    static readonly ConcurrentDictionary<string, byte> TestPins = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="sha256"/> as a test's stand-in for this build's pin (<see cref="TestPins"/>).</summary>
    internal static void PinForTest(string sha256) => TestPins[sha256.Trim().ToLowerInvariant()] = 0;

    /// <summary>A value as the refusal names it: its SHA-256's first twelve hex digits and its length.</summary>
    static string Fingerprint(string value) =>
        $"SHA-256 {Sha256Hex.Of(value)[..12]} ({Encoding.UTF8.GetByteCount(value)} bytes)";

    // ---- the pinned model --------------------------------------------------------------------------------------------

    /// <summary>
    /// NULL WHEN A <paramref name="seatClass"/> SEAT MAY RUN ON ITS PINNED MODEL, or the refusal: a class that pins no model, a runtime
    /// that cannot name one on its command line, or an owner's choice for the position that names another model.
    /// <paramref name="chosen"/> is the owner's own choice for the position, null or empty for none — the pin then stands.
    /// </summary>
    public static string? ModelRefusal(string seatClass, string? chosen, RuntimeManifest manifest)
    {
        if (ModelOf(seatClass) is not { } pin)
            return $"TradeAgent will not start {manifest.DisplayName} as a {seatClass} seat: the class pins no model, and a broker-only seat runs " +
                   "only on the model whose offered tools this build knows. Nothing was started.";
        if (!manifest.ModelArgs.Any(a => a.Contains("{model}", StringComparison.Ordinal)))
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: its command line cannot name the model, so it " +
                   $"would run on whatever its account defaults to rather than {pin}. Nothing was started.";
        if (chosen is { Length: > 0 } other && !string.Equals(other, pin, StringComparison.Ordinal))
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat on {other}: a broker-only seat runs only on {pin}, " +
                   $"the model whose offered tools this build knows. Choose {pin} for this position, or no model, to continue. Nothing was started.";
        return null;
    }

    // ---- the offered surface -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE ARGUMENT TEMPLATE A SURFACE IS KEYED BY: the stream flag, the unattended flags, the turn command, the class's deny set as
    /// written (its door slot unfilled), the model flag, the class's environment and its model catalog's pin — what decides which tools a
    /// binary offers a model.
    /// </summary>
    public static string TemplateHash(RuntimeManifest manifest, string seatClass)
    {
        var seat = manifest.Seat;
        var parts = new[]
        {
            manifest.JsonFlag ?? "", string.Join('\0', manifest.UnattendedArgs), string.Join('\0', manifest.ExecArgs),
            string.Join('\0', seat?.ArgsFor(seatClass) ?? []), string.Join('\0', manifest.ModelArgs),
            string.Join('\0', (seat?.ClassEnvironment.GetValueOrDefault(seatClass) ?? new Dictionary<string, string>())
                .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)),
            // THE MODEL'S METADATA, by the catalog's pin (U-cell-model-catalog): another catalog is another surface.
            seat is null ? "" : SeatProfile.CatalogPin(seat, seatClass)
        };
        return Sha256Hex.Of(string.Join('\u001e', parts));
    }

    /// <summary>What was captured per (binary digest, argument template, model), for this process's life.</summary>
    static readonly ConcurrentDictionary<(string Binary, string Template, string Model), IReadOnlyList<string>> Surfaces = new();

    /// <summary>
    /// THE SURFACE <paramref name="ask"/>'s SEAT WOULD BE OFFERED, VERIFIED: read from the cache or, on a key not yet seen, from
    /// <paramref name="probe"/> — then held to <see cref="SurfaceRefusal"/> on every call. A probe that fails is a refusal, never cached.
    /// </summary>
    public static async Task<(string? Refusal, IReadOnlyList<string>? Offered)> VerifySurfaceAsync(SurfaceAsk ask, ISurfaceProbe probe,
        CancellationToken ct)
    {
        var key = (ask.ExeSha256, TemplateHash(ask.Manifest, ask.SeatClass), ask.Model);
        if (!Surfaces.TryGetValue(key, out var offered))
        {
            try { offered = await probe.OfferedAsync(ask, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ($"TradeAgent will not start the AI: it could not read which tools {ask.Manifest.DisplayName} would offer {ask.Model} " +
                        $"as a broker-only seat ({ex.Message}). Nothing was started.", null);
            }
            Surfaces[key] = offered;
        }
        return (SurfaceRefusal(ask.Model, offered, ask.Manifest.DisplayName, ask.SeatClass), offered);
    }

    /// <summary>Forgets every captured surface: a test's way to make the next ask reach its probe.</summary>
    internal static void ForgetSurfaces() => Surfaces.Clear();

    /// <summary>
    /// NULL WHEN <paramref name="offered"/> IS EXACTLY THE SET <see cref="Expected"/> HOLDS FOR <paramref name="model"/>, otherwise the
    /// refusal in words: a host tool or <c>exec</c> named first, then what is offered beyond the set and what is missing from it.
    /// </summary>
    public static string? SurfaceRefusal(string model, IReadOnlyList<string> offered, string who) =>
        SurfaceRefusal(model, offered, who, SeatPlan.BrokerClass);

    /// <summary>
    /// The same check for <paramref name="seatClass"/>: for route (a)'s cell class the shell, patch and image tools are EXPECTED — they run
    /// in the cell — and any other host tool (code mode's <c>exec</c> above all: model-written code in the harness, beside the login) refuses.
    /// </summary>
    public static string? SurfaceRefusal(string model, IReadOnlyList<string> offered, string who, string seatClass) =>
        SurfaceRefusal(model, offered, who, seatClass, ExpectedFor(seatClass, model));

    /// <summary>The same check against <paramref name="expectedSet"/> — the class's compiled set, or a test's.</summary>
    internal static string? SurfaceRefusal(string model, IReadOnlyList<string> offered, string who, string seatClass, string[]? expectedSet)
    {
        var names = offered.Select(ToolName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var inCell = seatClass == SeatPlan.CellClass ? expectedSet ?? [] : [];
        // CODE MODE'S EXEC IS NEVER EXEMPTED (U-cell-model-catalog, option C): its JavaScript runs in the process that holds the sign-in,
        // so no class's expected set — compiled or a test's — lets it through.
        var host = names.Where(n => HostTools.Contains(BareName(n), StringComparer.Ordinal)
                                    && (NeverOffered.Contains(BareName(n), StringComparer.Ordinal) || !inCell.Contains(n, StringComparer.Ordinal))).ToList();
        var kind = Kind(seatClass);
        if (host.Count > 0)
            return $"TradeAgent will not start {who} as {kind}: on {model} it would offer {Words(host)}, which runs on this " +
                   "computer outside its cell or runs model-written code inside the program that holds the AI's sign-in. " +
                   (seatClass == SeatPlan.CellClass
                       ? "A route (a) cell seat is offered TradeAgent's tools and the tools that run in its cell, nothing else. "
                       : "A broker-only seat is offered TradeAgent's tools and nothing that reaches the computer. ") + "Nothing was started.";
        if (expectedSet is not { } expected)
            return $"TradeAgent will not start {who} as {kind} on {model}: this build knows no offered surface for that model. " +
                   "Nothing was started.";
        var extra = names.Except(expected, StringComparer.Ordinal).ToList();
        var missing = expected.Except(names, StringComparer.Ordinal).ToList();
        if (extra.Count == 0 && missing.Count == 0) return null;
        return $"TradeAgent will not start {who} as {kind}: on {model} it would be offered " +
               (extra.Count > 0 ? $"{Words(extra)} beyond " : "") + "the tools this build expects" +
               (missing.Count > 0 ? $"{(extra.Count > 0 ? ", and not" : " less")} {Words(missing)}" : "") +
               ". A release or a model that changes what a seat is offered is refused until a build knows it. Nothing was started.";
    }

    /// <summary>
    /// A captured tool's name: a <c>[kind]</c> after it, an <c>(additional_tools)</c> mark and codex's own <c>functions.</c> namespace
    /// dropped — <c>(additional_tools) functions.exec[custom]</c> is <c>exec</c>, <c>tool_search[tool_search]</c> is <c>tool_search</c> —
    /// and any other namespace KEPT: <c>clock.curr_time</c> stays itself, so a tool of that name in another namespace is a new tool.
    /// </summary>
    public static string ToolName(string captured)
    {
        var name = captured.Trim();
        if (name.StartsWith('(') && name.IndexOf(')') is var close and > 0) name = name[(close + 1)..].Trim();
        if (name.IndexOf('[') is var bracket and >= 0) name = name[..bracket];
        name = name.Trim();
        return name.StartsWith("functions.", StringComparison.Ordinal) ? name["functions.".Length..] : name;
    }

    /// <summary>A tool name's last segment, whatever its namespace: what <see cref="HostTools"/> is matched against.</summary>
    static string BareName(string name) => name.LastIndexOf('.') is var dot and >= 0 ? name[(dot + 1)..] : name;

    static string Words(IReadOnlyList<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
}

/// <summary>
/// ONE ASK OF A SURFACE PROBE: the runtime, its program and that program's SHA-256, the seat class and the pinned model. The probe
/// starts the class's own argument template (<see cref="BrokerSeat.TemplateHash"/>) with a stub door in the door's slot.
/// </summary>
public sealed record SurfaceAsk(RuntimeManifest Manifest, string Exe, string ExeSha256, string SeatClass, string Model);

/// <summary>
/// WHICH TOOLS A SEAT WOULD BE OFFERED, read without a real model, behind an interface because CI has no codex: a test hands a fake,
/// the product the real dry run (<see cref="CodexSurfaceProbe"/>).
/// </summary>
public interface ISurfaceProbe
{
    /// <summary>The tool names the first model request carried, as captured (namespaces dotted, kinds bracketed).</summary>
    Task<IReadOnlyList<string>> OfferedAsync(SurfaceAsk ask, CancellationToken ct);
}
