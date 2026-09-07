using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE APP CAN SEE OF A TURN'S CONTEXT, AND WHAT IT HONESTLY CANNOT.
///
/// <c>docs/COUNCIL.md</c>'s <c>U-model</c> line asks for the observable components of a turn's
/// context measured from the event stream WITH THE REMAINDER LABELLED UNATTRIBUTED, because the
/// meter beside it sees aggregate usage only. The second half is the part worth guarding: the
/// components the stream reports are characters and bytes, the bill is in tokens, and any bridge
/// between the two would be an invented number sitting beside measured ones.
///
/// Both streams below are recordings, not fixtures. The first is the four lines codex-cli 0.153.4
/// printed on this Mac on 2026-09-06 (<see cref="TurnMeterTests.CodexStream"/>, which is the
/// <c>GREEN on TurnMeterTests.cs:80-86</c> the brief asks for). The second is the six lines it
/// printed on 2026-09-07 for a turn that was told to run <c>ls</c>, in a scratch directory, on
/// <c>gpt-5.6-sol</c> — the measurement that settled what a command item's fields are called.
/// </summary>
public class TurnContextTests
{
    /// <summary>
    /// THE MEASURED TOOL TURN, verbatim. Command:
    /// <c>codex exec --json --skip-git-repo-check -s read-only -m gpt-5.6-sol "Run ls in this
    /// directory. Reply with only the number of entries."</c>, exit 0.
    ///
    /// Three things it settles. A command is an item of type <c>command_execution</c>; its output is
    /// on <c>aggregated_output</c> and its shell line on <c>command</c>; and the SAME item id
    /// arrives twice — <c>item.started</c> with an empty output, <c>item.completed</c> with the
    /// output — so a parser counting lines would report two commands for one.
    ///
    /// It also settles the thing item 5 turns on: nothing in this stream names a model, even though
    /// <c>-m gpt-5.6-sol</c> was on the command line.
    /// </summary>
    public const string LsStream =
        """
        {"type":"thread.started","thread_id":"01a07972-332d-7182-b12d-d30676ade554"}
        {"type":"turn.started"}
        {"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"/bin/zsh -lc 'ls -1 | wc -l'","aggregated_output":"","exit_code":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"/bin/zsh -lc 'ls -1 | wc -l'","aggregated_output":"       5\n","exit_code":0,"status":"completed"}}
        {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"5"}}
        {"type":"turn.completed","usage":{"input_tokens":32913,"cached_input_tokens":22656,"cache_write_input_tokens":0,"output_tokens":217,"reasoning_output_tokens":103}}
        """;

    static readonly TurnUsage LsUsage = new(32913, 22656, 0, 217, 103, null);

    /// <summary>
    /// THE GUARD, on the measured tool turn. One command, its output counted once, and the input the
    /// stream does not account for named as unattributed rather than divided up.
    /// </summary>
    [Fact]
    public void The_components_of_a_real_tool_turn_are_counted_and_the_rest_is_named_unattributed()
    {
        var context = TurnContext.Read(LsStream, promptChars: 1234, LsUsage);

        // One command, not two: item_0 arrives started and completed.
        Assert.Equal(1, context.CommandItems);
        // "       5\n" — seven spaces, a 5 and a newline.
        Assert.Equal(9, context.ToolOutputBytes);
        Assert.Equal(1234, context.PromptChars);
        Assert.True(context.StreamBytes > 0);

        Assert.Equal(32913, context.InputTokens);
        Assert.Equal(22656, context.CachedInputTokens);
        Assert.Equal(10257, context.UncachedInputTokens);

        // THE POINT. 10,257 input tokens arrived that nothing in the stream accounts for, and they
        // are labelled rather than attributed to the prompt, the tools or anything else.
        Assert.Equal(10257, context.UnattributedInputTokens);
        Assert.Equal(TurnContext.Unmeasured, context.Note);
    }

    /// <summary>
    /// GREEN on the recording at <c>TurnMeterTests.cs:80-86</c>: a turn that ran no command at all.
    /// Zero commands and zero tool bytes are MEASURED zeroes here — the stream showed none — which is
    /// a different fact from a stream nobody could read, and the numbers beside them prove it was read.
    /// </summary>
    [Fact]
    public void The_recorded_turn_that_ran_no_commands_reports_none_and_still_reports_its_tokens()
    {
        var context = TurnContext.Read(TurnMeterTests.CodexStream, promptChars: 42,
            new TurnUsage(17232, 12928, 0, 6, 0, null));

        Assert.Equal(0, context.CommandItems);
        Assert.Equal(0, context.ToolOutputBytes);
        Assert.Equal(42, context.PromptChars);
        Assert.Equal(17232, context.InputTokens);
        Assert.Equal(12928, context.CachedInputTokens);
        Assert.Equal(4304, context.UncachedInputTokens);
        Assert.Equal(4304, context.UnattributedInputTokens);
        Assert.Equal(6, context.OutputTokens);
    }

    /// <summary>
    /// NOTHING IS ESTIMATED FROM WHAT THE STREAM DOES NOT SHOW. A turn whose runtime reported no
    /// usage at all still reports the components the app did see, and reports zero tokens rather
    /// than inferring them from the characters and bytes it has in front of it.
    /// </summary>
    [Fact]
    public void A_turn_with_no_usage_reported_infers_no_tokens_from_the_bytes_it_can_see()
    {
        var context = TurnContext.Read(LsStream, promptChars: 1234, usage: null);

        Assert.Equal(1, context.CommandItems);
        Assert.Equal(9, context.ToolOutputBytes);
        Assert.Equal(0, context.InputTokens);
        Assert.Equal(0, context.UnattributedInputTokens);
    }

    /// <summary>Junk in the stream contributes nothing rather than throwing or guessing.</summary>
    [Fact]
    public void A_stream_that_is_not_json_contributes_nothing_and_does_not_throw()
    {
        var context = TurnContext.Read("I did the thing.\nnot json {{{\n", promptChars: null, usage: null);
        Assert.Equal(0, context.CommandItems);
        Assert.Null(context.PromptChars);
        Assert.Equal(TurnContext.Unmeasured, context.Note);
    }

    /// <summary>
    /// And it reaches the row: the launch ledger's <c>context</c> column carries this record as JSON,
    /// so what a turn's context was made of is answerable months later without the stream.
    /// </summary>
    [Fact]
    public void The_context_lands_on_the_attempt_row_as_json()
    {
        using var db = TestEnv.NewDb();
        var records = Path.Combine(TestEnv.Home, $"ctx-{Guid.NewGuid():n}.jsonl");
        var meter = new TurnMeter(db, () => 500m, runtimeId: () => "codex", recordPath: records,
            owner: () => new OwnerPrice(4m, 20m), model: () => "gpt-5.6-sol");

        var id = meter.Begin("## Situation\n\nContinue your mission.")!;
        meter.Record(new AgentTurnEnded(0, TimeSpan.FromSeconds(8), LsStream, DateTimeOffset.Now)
        {
            Usage = LsUsage
        });

        var row = new AiAttemptStore(db).Get(id)!;
        var context = Json.Read<TurnContext>(row.Context!)!;

        Assert.Equal(1, context.CommandItems);
        Assert.Equal(9, context.ToolOutputBytes);
        Assert.Equal("## Situation\n\nContinue your mission.".Length, context.PromptChars);
        Assert.Equal(10257, context.UnattributedInputTokens);

        // The jsonl mirror stays, and carries the same reading.
        var mirrored = Json.Read<TurnRecord>(File.ReadAllLines(records)[0])!;
        Assert.Equal(1, mirrored.Context!.CommandItems);
    }
}
