using System.Collections.Concurrent;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE SEATS WHOSE TOOLS COULD NOT RUN (<c>U-cli-code-mode</c>): a turn whose stream carried the vendor's sentence for it
/// (<see cref="RuntimeManifest.ToolsUnavailablePattern"/>) marks its seat — the runtime, the profile it was verified under and the model
/// — and every later launch of that same seat is refused in the same words before admission, so the loop stops launching it and says
/// why, rather than spending turns that look like work and do none (attempt 4). Another program, settings file, deny set or model is
/// another seat and is tried; so is the same one after the app restarts, when its dry run runs again.
/// </summary>
public static class DeadSeats
{
    static readonly ConcurrentDictionary<(string Runtime, string Profile, string Model), string> Marked = new();

    /// <summary>
    /// Marks the seat and returns the app's sentence for it, quoting <paramref name="vendorSaid"/>. The first mark of a seat stands.
    /// </summary>
    public static string Mark(RuntimeManifest manifest, string? profile, string? model, string vendorSaid)
    {
        var on = model is { Length: > 0 } m ? $" on {m}" : "";
        var words = $"{manifest.DisplayName}{on} could not run any command this turn — it said \"{vendorSaid.Trim()}\" — so the turn did no " +
                    "work, and TradeAgent will not start this AI seat again until its program, its settings or its model change, or " +
                    "TradeAgent restarts. Nothing it was woken for was answered; it stays owed.";
        return Marked.GetOrAdd(Key(manifest, profile, model), words);
    }

    /// <summary>The sentence a launch of this seat is refused in, or null for a seat nothing marked.</summary>
    public static string? Refusal(RuntimeManifest manifest, string? profile, string? model) =>
        Marked.TryGetValue(Key(manifest, profile, model), out var words) ? words : null;

    /// <summary>Forgets every mark: a test's way to start clean.</summary>
    internal static void Forget() => Marked.Clear();

    /// <summary>The seat: the runtime, its verified profile — or, for a runtime with no seat profile, the program it names — and the model.</summary>
    static (string, string, string) Key(RuntimeManifest manifest, string? profile, string? model) =>
        (manifest.Id, profile ?? "program:" + manifest.Executable, model ?? "");
}
