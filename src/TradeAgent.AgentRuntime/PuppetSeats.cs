namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHERE A PUPPET SEAT MAY RUN: in a test host, and nowhere else (<c>U-puppet-runtime</c>; <c>docs/ORCHESTRATION.md</c> § 7).
///
/// <para>A puppet (<see cref="RuntimeManifest.Puppet"/>) is a scripted program started by the real <see cref="AgentSession"/>
/// with the launch's real grant — the chair's may place orders (ORDER-001) — so a puppet in the product would be a way to put a
/// program nobody chose behind a seat. <see cref="TestHost"/> is internal and set only by the test assembly's module initializer
/// (<c>InternalsVisibleTo</c>); nothing in the product sets it, and no file, setting or pipe op reaches it (AUTH-001).</para>
/// </summary>
internal static class PuppetSeats
{
    /// <summary>Whether this process is a test host. Set by the test assembly's module initializer; false in the product.</summary>
    internal static bool TestHost { get; set; }

    /// <summary>
    /// Whether <paramref name="runtime"/> is the puppet's reserved id in a test host — the one runtime a test host seats a position on that
    /// no catalogue carries (<c>U-loop-e2e</c>: a chief appointed by the owner's own press, on the puppet the host runs). False in the product.
    /// </summary>
    internal static bool Seats(string runtime) => TestHost && runtime == RuntimeManifest.PuppetId;

    /// <summary>
    /// The sentence refusing <paramref name="manifest"/>, naming it, when it is a puppet and this process is not a test host; null
    /// otherwise — and always null for a manifest that is not a puppet, whatever the flag says.
    /// </summary>
    internal static string? Refusal(RuntimeManifest manifest) =>
        manifest.Puppet && !TestHost
            ? $"TradeAgent did not start '{manifest.DisplayName}' ({manifest.Id}): it is a puppet seat, a scripted stand-in that " +
              "runs only inside TradeAgent's own tests, and this is not a test host."
            : null;
}
