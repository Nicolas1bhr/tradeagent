namespace TradeAgent.Core.Strategy;

/// <summary>
/// WHY A TEXT IS NOT A PROGRAM, AND WHERE.
///
/// <para><see cref="Line"/> is 1-based and names the line at fault. It is 0 only when the fault is
/// the ABSENCE of something — no instrument, no sizing, no entry rule — where there is no honest line
/// to point at and inventing one ("line 14", the last line, which is innocent) would send the reader
/// to the wrong place. <see cref="Text"/> spells the difference out rather than leaving a reader to
/// read 0 as line zero.</para>
/// </summary>
public sealed record StrategyRefusal(int Line, string Reason)
{
    /// <summary>The refusal as it is shown: `line 7: ...`, or `program: ...` when nothing is missing from a line.</summary>
    public string Text => Line > 0 ? $"line {Line}: {Reason}" : $"program: {Reason}";

    public override string ToString() => Text;
}

/// <summary>
/// WHAT PARSING A TEXT PRODUCES: a program, or a refusal. Never both, never neither, and never an
/// exception.
///
/// <para>The text comes from an agent — a cheap model asked for a rule set, writing into
/// `workspace/strategies/` — so a parser that threw would be a crash the agent can cause with a file,
/// and `CLAUDE.md` says material the agent produces is data. Totality is what makes that true here:
/// <see cref="StrategyParser.Parse"/> answers with one of these two for every input, including the
/// empty string, 8 KiB of brackets and a binary file that was dropped in the wrong folder.</para>
/// </summary>
public sealed record StrategyParse
{
    StrategyParse(StrategyProgram? program, StrategyRefusal? refusal)
    {
        Program = program;
        Refusal = refusal;
    }

    /// <summary>The program, or null when the text was refused.</summary>
    public StrategyProgram? Program { get; }

    /// <summary>The refusal, or null when a program came out.</summary>
    public StrategyRefusal? Refusal { get; }

    public bool Ok => Program is not null;

    /// <summary>The refusal's text, or the empty string when the text parsed. For logs and for tests.</summary>
    public string Why => Refusal?.Text ?? "";

    internal static StrategyParse Yes(StrategyProgram program) => new(program, null);

    internal static StrategyParse No(int line, string reason) => new(null, new StrategyRefusal(line, reason));
}
