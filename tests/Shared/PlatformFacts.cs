using Xunit;

namespace TradeAgent.Tests;

/// <summary>
/// A TEST ONLY SOME PLATFORMS CAN RUN SAYS SO IN THE RESULTS (R29 F-E2, F-E3). A plain <c>[Fact]</c> that opens with a platform check and returns
/// reads Passed where it never ran; this reads Skipped, with the reason, so no result claims a run that did not happen.
/// <paramref name="platforms"/> are <see cref="OperatingSystem.IsOSPlatform"/>'s names: "linux", "macos", "windows".
/// </summary>
public sealed class OnlyOnFactAttribute : FactAttribute
{
    public OnlyOnFactAttribute(string why, params string[] platforms)
    {
        if (!platforms.Any(OperatingSystem.IsOSPlatform)) Skip = $"{string.Join(" and ", platforms)} only: {why}";
    }
}

/// <summary>A theory only some platforms can run: <see cref="OnlyOnFactAttribute"/>, every row.</summary>
public sealed class OnlyOnTheoryAttribute : TheoryAttribute
{
    public OnlyOnTheoryAttribute(string why, params string[] platforms)
    {
        if (!platforms.Any(OperatingSystem.IsOSPlatform)) Skip = $"{string.Join(" and ", platforms)} only: {why}";
    }
}

/// <summary>A fact that cannot hold under root off Windows: skipped there with the reason, never passed unrun.</summary>
public sealed class NotAsRootFactAttribute : FactAttribute
{
    public NotAsRootFactAttribute(string why)
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) Skip = $"not as root: {why}";
    }
}
