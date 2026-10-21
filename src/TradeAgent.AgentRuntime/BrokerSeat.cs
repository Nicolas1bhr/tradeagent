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
/// <para><b>Why a pinned model.</b> The class holds only on a model with classic tools: a code-mode-only model (gpt-6-astra) keeps its
/// code-mode <c>exec</c> even with the code-mode host disabled, so model-written code would run inside the process that holds the
/// sign-in (Law 2, § 2.8). <b>Why a verified surface.</b> codex lists no offered tools itself, and a runtime update or the account's
/// default model changes them, so before every launch the app reads them from a dry run against its own loopback mock
/// (<see cref="CodexSurfaceProbe"/>), cached by binary, argument template and model, and refuses anything but the expected set.</para>
///
/// <para><b>What it is not.</b> MEDIATED, not hard: no model-reachable tool crosses, but the codex process itself holds the login and
/// could. The dry run sees the binary's BUNDLED model metadata; whether the real provider's metadata equals it is UNKNOWN until the first
/// observed run, on the owner's word. A per-request inspecting proxy is the later hardening.</para>
/// </summary>
public static class BrokerSeat
{
    /// <summary>The name the app's door is configured under in a broker seat's deny set, and the one tool server it may list.</summary>
    public const string DoorServer = "tradeagent";

    /// <summary>
    /// THE CLASSIC-TOOLS MODEL A BROKER SEAT RUNS ON. RUN 2026-10-17 (codex-cli 0.160.1, the loopback mock, a throwaway home): offered
    /// exactly <see cref="Expected"/>'s set; gpt-6-astra under the same argv was offered <c>exec</c> and refused.
    /// </summary>
    public const string Model = "gpt-5.5";

    /// <summary>The model <paramref name="seatClass"/> is pinned to, or null for a class that pins none.</summary>
    public static string? ModelOf(string seatClass) => seatClass == SeatPlan.BrokerClass ? Model : null;

    /// <summary>
    /// THE TOOLS EACH PINNED MODEL IS OFFERED UNDER THE BROKER CLASS, compiled: the app's door's tools are deferred behind
    /// <c>tool_search</c>, and the rest are codex's own MCP-resource and question tools, none of which reaches the computer. A set that
    /// differs refuses — a release, a key or a model that changed what is offered is a new set this build has not seen.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Model] = ["list_mcp_resource_templates", "list_mcp_resources", "read_mcp_resource", "request_user_input", "tool_search"]
    };

    /// <summary>
    /// TOOLS NO BROKER SEAT IS EVER OFFERED, named in the refusal: what runs a command, writes a file, reads an image off the disk, starts
    /// a sub-agent or runs model-written code — on this computer or inside the process that holds the sign-in.
    /// </summary>
    public static readonly string[] HostTools =
    [
        "exec", "exec_command", "write_stdin", "shell", "shell_command", "local_shell", "unified_exec", "apply_patch", "view_image",
        "js_repl", "spawn_agent", "send_input", "image_generation", "web_search"
    ];

    /// <summary>Whether this build has a broker class for the built-in runtime <paramref name="runtimeId"/> — code, read once.</summary>
    public static bool Offers(string runtimeId) => WithBrokerClass.Value.Contains(runtimeId);

    static readonly Lazy<HashSet<string>> WithBrokerClass = new(() =>
        RuntimeCatalog.BuiltIn().Where(m => m.Seat?.Args.ContainsKey(SeatPlan.BrokerClass) == true).Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal));

    static RuntimeManifest? BuiltIn(string runtimeId) => RuntimeCatalog.BuiltIn().FirstOrDefault(m => m.Id == runtimeId);

    // ---- the class's security fields are this build's ------------------------------------------------------------------

    /// <summary>
    /// NULL WHEN <paramref name="manifest"/> CARRIES THE BROKER CLASS EXACTLY AS THIS BUILD COMPILED IT, or the refusal naming the field
    /// and both versions — the file's and this build's. <c>runtimes.json</c> replaces a manifest whole and stays the vendor-commands
    /// data file (<c>CLAUDE.md</c>); a seat class's pin and deny set are never read from it (ORCHESTRATION § 2.7). Refuse, never guess.
    /// </summary>
    public static string? OverrideRefusal(RuntimeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var built = BuiltIn(manifest.Id);
        if (built?.Seat is not { } own || !own.Args.ContainsKey(SeatPlan.BrokerClass))
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: this build ({Versions.App}) has no broker class " +
                   $"for '{manifest.Id}'. Nothing was started.";
        if (manifest.Seat is not { } seat)
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: its entry in runtimes.json has no seat profile, " +
                   $"and this build ({Versions.App}) compiles one. Remove the '{manifest.Id}' entry from runtimes.json to continue. Nothing was started.";

        foreach (var (field, file, build) in Fields(manifest, seat, PinOf(manifest)).Zip(Fields(built, own, BuildPin(built, manifest)),
                     (a, b) => (a.Field, a.Value, b.Value)))
        {
            if (file == build) continue;
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: runtimes.json gives its {field} as " +
                   $"{Fingerprint(file)}, and this build ({Versions.App}) compiles it as {Fingerprint(build)}. A broker seat's security " +
                   $"fields are this build's own and never read from a file. Remove the '{manifest.Id}' entry from runtimes.json, or put " +
                   "this build's value back, to continue. Nothing was started.";
        }
        return null;
    }

    /// <summary>
    /// The broker class's security fields, each as one comparable string, in a fixed order — the program's pin first: a broker seat's
    /// program is this build's pinned codex, and an override that swaps the program and its digest together would otherwise pass the
    /// pin check and receive the seat's environment before anything else refused it (fix pass 1, R1).
    /// </summary>
    static IEnumerable<(string Field, string Value)> Fields(RuntimeManifest m, SeatPlan seat, string pin)
    {
        static string Join(IEnumerable<string>? values) => string.Join('\u001f', values ?? []);
        static string Pairs(IReadOnlyDictionary<string, string>? kv) =>
            Join(kv?.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        yield return ("program pin", pin);
        yield return ("deny set", Join(seat.ArgsFor(SeatPlan.BrokerClass)));
        yield return ("class environment", Pairs(seat.ClassEnvironment.GetValueOrDefault(SeatPlan.BrokerClass)));
        yield return ("seat environment", Pairs(seat.Environment));
        yield return ("feature allowlist", Join(seat.FeaturesFor(SeatPlan.BrokerClass)));
        yield return ("tool-server allowlist", Join(seat.ServersFor(SeatPlan.BrokerClass)));
        yield return ("feature report command", Join(seat.FeaturesArgs));
        yield return ("tool-server report command", Join(seat.McpArgs));
        yield return ("model flag", Join(m.ModelArgs));
        yield return ("unattended flags", Join(m.UnattendedArgs));
        yield return ("turn command", Join(m.ExecArgs) + "\u001e" + Join(m.ResumeArgs));
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
    /// written (its door slot unfilled), the model flag and the class's environment — what decides which tools a binary offers a model.
    /// </summary>
    public static string TemplateHash(RuntimeManifest manifest, string seatClass)
    {
        var seat = manifest.Seat;
        var parts = new[]
        {
            manifest.JsonFlag ?? "", string.Join('\0', manifest.UnattendedArgs), string.Join('\0', manifest.ExecArgs),
            string.Join('\0', seat?.ArgsFor(seatClass) ?? []), string.Join('\0', manifest.ModelArgs),
            string.Join('\0', (seat?.ClassEnvironment.GetValueOrDefault(seatClass) ?? new Dictionary<string, string>())
                .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
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
        return (SurfaceRefusal(ask.Model, offered, ask.Manifest.DisplayName), offered);
    }

    /// <summary>Forgets every captured surface: a test's way to make the next ask reach its probe.</summary>
    internal static void ForgetSurfaces() => Surfaces.Clear();

    /// <summary>
    /// NULL WHEN <paramref name="offered"/> IS EXACTLY THE SET <see cref="Expected"/> HOLDS FOR <paramref name="model"/>, otherwise the
    /// refusal in words: a host tool or <c>exec</c> named first, then what is offered beyond the set and what is missing from it.
    /// </summary>
    public static string? SurfaceRefusal(string model, IReadOnlyList<string> offered, string who)
    {
        var names = offered.Select(ToolName).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var host = names.Where(n => HostTools.Contains(n, StringComparer.Ordinal)).ToList();
        if (host.Count > 0)
            return $"TradeAgent will not start {who} as a broker-only seat: on {model} it would offer {Words(host)}, which runs on this " +
                   "computer or runs model-written code inside the program that holds the AI's sign-in. A broker-only seat is offered " +
                   "TradeAgent's tools and nothing that reaches the computer. Nothing was started.";
        if (!Expected.TryGetValue(model, out var expected))
            return $"TradeAgent will not start {who} as a broker-only seat on {model}: this build knows no offered surface for that model. " +
                   "Nothing was started.";
        var extra = names.Except(expected, StringComparer.Ordinal).ToList();
        var missing = expected.Except(names, StringComparer.Ordinal).ToList();
        if (extra.Count == 0 && missing.Count == 0) return null;
        return $"TradeAgent will not start {who} as a broker-only seat: on {model} it would be offered " +
               (extra.Count > 0 ? $"{Words(extra)} beyond " : "") + "the tools this build expects" +
               (missing.Count > 0 ? $"{(extra.Count > 0 ? ", and not" : " less")} {Words(missing)}" : "") +
               ". A release or a model that changes what a seat is offered is refused until a build knows it. Nothing was started.";
    }

    /// <summary>
    /// A captured tool's bare name: the namespace before its last dot, a <c>[kind]</c> after it and an <c>(additional_tools)</c> mark
    /// dropped — <c>(additional_tools) functions.exec[custom]</c> is <c>exec</c>, <c>tool_search[tool_search]</c> is <c>tool_search</c>.
    /// </summary>
    public static string ToolName(string captured)
    {
        var name = captured.Trim();
        if (name.StartsWith('(') && name.IndexOf(')') is var close and > 0) name = name[(close + 1)..].Trim();
        if (name.IndexOf('[') is var bracket and >= 0) name = name[..bracket];
        if (name.LastIndexOf('.') is var dot and >= 0) name = name[(dot + 1)..];
        return name.Trim();
    }

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
