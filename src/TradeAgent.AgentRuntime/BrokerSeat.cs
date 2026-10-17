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

        foreach (var (field, file, build) in Fields(manifest, seat).Zip(Fields(built, own), (a, b) => (a.Field, a.Value, b.Value)))
        {
            if (file == build) continue;
            return $"TradeAgent will not start {manifest.DisplayName} as a broker-only seat: runtimes.json gives its {field} as " +
                   $"{Fingerprint(file)}, and this build ({Versions.App}) compiles it as {Fingerprint(build)}. A broker seat's security " +
                   $"fields are this build's own and never read from a file. Remove the '{manifest.Id}' entry from runtimes.json, or put " +
                   "this build's value back, to continue. Nothing was started.";
        }
        return null;
    }

    /// <summary>The broker class's security fields, each as one comparable string, in a fixed order.</summary>
    static IEnumerable<(string Field, string Value)> Fields(RuntimeManifest m, SeatPlan seat)
    {
        static string Join(IEnumerable<string>? values) => string.Join('\u001f', values ?? []);
        static string Pairs(IReadOnlyDictionary<string, string>? kv) =>
            Join(kv?.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

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
}
