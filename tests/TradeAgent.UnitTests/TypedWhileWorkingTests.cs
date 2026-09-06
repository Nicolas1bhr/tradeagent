using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE OWNER TYPES WHILE THE AI IS WORKING IS NOT THROWN AWAY.
///
/// Typing while a turn was in flight used to raise INVALID_REQUEST, which was defensible when every
/// turn was one somebody had asked for: they pressed send twice and the second press was a mistake.
/// The mission loop makes it indefensible. Turns run back to back, so <c>Busy</c> is true almost all
/// the time, and the one person the AI works for would have had nowhere to put a question.
///
/// So the message is kept, shown as theirs, and taken by the loop for the top of the next turn's
/// Situation — where <c>MissionLoopTests</c> proves it lands above the state block.
/// </summary>
public class TypedWhileWorkingTests
{
    /// <summary>
    /// A conversation with no runtime behind it. <c>SendAsync</c>'s queue branch is decided before
    /// anything is executed, so nothing here needs a process — which is the point: the branch is
    /// about the owner's message, not about what the AI is doing with it.
    ///
    /// OFF THE DISK, deliberately. This used to be <c>RuntimeCatalog.Require("codex")</c>, which
    /// reads <c>runtimes.json</c> under the assembly's single test home — the same one file
    /// <see cref="VendorOverrideFileTests"/> corrupts on purpose. Nothing about the owner's typed
    /// message depends on where the manifest came from, so this class reads the built-in list, which
    /// is a literal in <c>RuntimeManifest.cs</c> and touches no file: it cannot lose that race, and
    /// it does not have to serialise itself behind every class that writes the file in order to
    /// avoid it. <see cref="TypedWhileWorkingSurvivesACorruptRuntimesFileTests"/> holds it to that.
    /// </summary>
    internal static AgentSession Session() => new(
        RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex"),
        resolveExecutable: () => null,
        workspace: () => TestEnv.Home,
        environment: () => new Dictionary<string, string>());

    /// <summary>
    /// A turn that could not even start still ends. The loop's backoff and `U-meter`'s prices are
    /// both counting turns, and a turn that vanished silently is one neither can account for — so
    /// the missing runtime is exit code -1 with the reason in the raw stream, not nothing at all.
    /// </summary>
    [Fact]
    public async Task A_turn_that_never_started_still_reports_that_it_ended()
    {
        var session = Session();
        AgentTurnEnded? ended = null;
        session.TurnEnded += e => ended = e;

        await session.SendMissionAsync("## Situation\n\nContinue your mission.");

        Assert.NotNull(ended);
        Assert.Equal(-1, ended.ExitCode);
        Assert.True(ended.Failed);
        Assert.Contains("not installed", ended.Raw);
        Assert.False(session.Busy);
    }

    /// <summary>
    /// The queue itself: taken once, in order, and emptied. Taking it twice would have the AI
    /// answering the same question every turn for the rest of the day.
    /// </summary>
    [Fact]
    public void The_queue_is_taken_in_order_and_emptied()
    {
        var session = Session();
        Assert.Empty(session.TakeTyped());

        session.Queue("stop buying NQ");
        session.Queue("and check the journal");

        Assert.Equal(["stop buying NQ", "and check the journal"], session.TakeTyped());
        Assert.Empty(session.TakeTyped());
    }

    /// <summary>
    /// A queued message is SHOWN, and the owner is told where it went. A message that disappeared
    /// into a queue with no acknowledgement reads exactly like a message that was dropped.
    /// </summary>
    [Fact]
    public void A_queued_message_appears_in_the_conversation_with_a_line_saying_where_it_went()
    {
        var session = Session();
        session.Queue("stop buying NQ");

        var history = session.History;

        Assert.Equal(ChatRole.You, history[^2].Role);
        Assert.Equal("stop buying NQ", history[^2].Text);
        Assert.Equal(ChatRole.System, history[^1].Role);
        Assert.Contains("next turn", history[^1].Text);
    }

    /// <summary>
    /// THE CHAT PAGE CAN STILL REACH IT. The composer's button is the Stop control while the AI
    /// works, so Enter is the gesture that has to keep sending — and it used to return early on
    /// exactly the condition that is now true nearly all the time.
    ///
    /// A source assertion, because the composer's key handler needs a running app to press. It
    /// catches the return of the early exit, which is the whole of what would break this.
    /// </summary>
    [Fact]
    public void The_composer_still_sends_on_enter_while_the_ai_is_working()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir.FullName, "src", "TradeAgent.App", "ChatView.cs"));

        Assert.DoesNotContain("if (_bound is null || _bound.Busy) return;", text);
        Assert.Contains("if (_bound is null) return;\n        _ = SubmitAsync();", text.Replace("\r\n", "\n"));
    }
}

/// <summary>
/// THE REPRODUCTION. This class is what <see cref="TypedWhileWorkingTests"/> went red on for real,
/// performed on purpose instead of by ordering: the corruption test's write, then the session.
///
/// CI run 34040176577 (ubuntu-latest) failed all three of that class's session tests in about a
/// millisecond each with `runtimes.json could not be read, so TradeAgent will not start an AI
/// assistant …`, thrown out of <c>RuntimeCatalog.Require</c>. Nothing was wrong with the product or
/// with the class: <see cref="VendorOverrideFileTests"/> deliberately corrupts the one
/// <c>runtimes.json</c> under the assembly's single test home, and a class that reads that file
/// while not sharing its collection is running a coin toss against xUnit's class-parallel schedule
/// — a toss this Mac and the Windows runner happened to win.
///
/// So the fix is that the conversation under test never reads the file, and this is the test that
/// says so in the only way that cannot rot: the file is corrupt on disk while the session is built
/// and used. It carries the collection attribute the class it guards no longer needs, because IT
/// writes the file and must not become the next unexplained failure somewhere else.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class TypedWhileWorkingSurvivesACorruptRuntimesFileTests
{
    [Fact]
    public void The_queue_works_while_runtimes_json_on_disk_is_corrupt()
    {
        try
        {
            File.WriteAllText(RuntimeCatalog.OverridePath, "{ \"id\": \"codex\", ");
            // The catalogue really is unreadable — otherwise this test proves nothing at all.
            Assert.NotNull(RuntimeCatalog.Read().Unreadable);

            var session = TypedWhileWorkingTests.Session();

            session.Queue("stop buying NQ");
            Assert.Equal(["stop buying NQ"], session.TakeTyped());
        }
        finally
        {
            if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        }
    }
}
