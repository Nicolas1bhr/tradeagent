using System.Reflection;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RENDER ROWS RUN ONE AT A TIME (<c>U-ci-precheck</c>; main push run 43558900671, macOS:
/// <c>OwnerPagesRenderTests.System_health_says_the_platform_in_force_on_each_platform</c> failed in
/// <c>HeadlessUnitTestSession.EnsureIsolatedApplication</c> → <c>AvaloniaHeadlessPlatform.Initialize</c> → <c>DefaultRenderLoop.Add</c> with
/// "The calling thread cannot access this object because a different thread owns it", 2.8 s into the assembly). The session makes a new
/// application and UI dispatcher at every <see cref="HeadlessUi.Run"/>, on its own thread, while the work a row does off that thread — composing
/// a host, the host's events, a press's work — reads the UI dispatcher; a row of another class doing that while the session set up for this one
/// owned the new dispatcher first. The assembly runs its classes in turn, so no other class's work runs beside a set-up.
/// </summary>
public class HeadlessSessionTests
{
    [Fact]
    public void The_render_rows_assembly_runs_its_tests_one_at_a_time()
    {
        var behaviour = typeof(HeadlessUi).Assembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        Assert.True(behaviour is { DisableTestParallelization: true },
            "the render rows' assembly runs its classes in parallel, so a row's work off the UI thread can meet another row's session set-up");
    }
}
