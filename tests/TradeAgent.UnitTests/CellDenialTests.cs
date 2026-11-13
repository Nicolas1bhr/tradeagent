using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A DENIAL IS ONLY A DENIAL WHEN NOTHING ELSE COULD HAVE FAILED IT (<c>U-cell-windows-egress</c> item 3), on every OS: the verdict of a Windows denial row from what its
/// three runs of one line printed. The real runs are <c>CellWindowsEgressTests</c>' and the benchmark's. The outputs here are what cells really printed.
/// </summary>
public class CellDenialTests
{
    const string Syntax = "La syntaxe du nom de fichier, de répertoire ou de volume est incorrecte.\r\n";

    /// <summary>
    /// THE OUTPUT THAT PASSED FOR A DENIAL ON THE BOX (u-kit-socket-2's box run, main's row at 92204b30): a line cmd could not even parse printed DENIED and the proof counted it.
    /// That line fails outside a cell too, and a row reading only its own DENIED had no way to know. Now it does not hold, and it says why.
    /// </summary>
    [Fact]
    public void A_line_that_cannot_run_is_not_a_denial_though_it_printed_denied()
    {
        var cell = "CTL-READ-DENIED\r\nREAD-DENIED\r\n" + Syntax + Syntax;
        var (held, seen) = CellDenial.Judge(["READ"], cell, cell);
        Assert.False(held, seen);
        Assert.Contains("the same line fails outside a cell too", seen, StringComparison.Ordinal);
        Assert.Contains("does not run in a cell even on the workspace", seen, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the control failed in the cell", "CTL-READ-DENIED\r\nREAD-DENIED\r\n", "READ-OPEN\r\n", "does not run in a cell")]
    [InlineData("the line fails outside too", "CTL-READ-OPEN\r\nREAD-DENIED\r\n", "READ-DENIED\r\n", "fails outside a cell too")]
    [InlineData("no outside word at all", "CTL-READ-OPEN\r\nREAD-DENIED\r\n", "", "fails outside a cell too")]
    [InlineData("the cell opened it", "CTL-READ-OPEN\r\nREAD-OPEN\r\n", "READ-OPEN\r\n", "not denied")]
    [InlineData("nothing ran in the cell", "", "READ-OPEN\r\n", "does not run in a cell")]
    public void Every_way_the_three_runs_can_fall_short_fails_the_row(string what, string cell, string outside, string says)
    {
        var (held, seen) = CellDenial.Judge(["READ"], cell, outside);
        Assert.False(held, what);
        Assert.Contains(says, seen, StringComparison.Ordinal);
    }

    [Fact]
    public void The_control_word_is_never_read_as_the_row_opening_or_as_the_outside_opening()
    {
        // "CTL-READ-OPEN" holds "READ-OPEN" as a substring: a row reading by substring would call the control the forbidden line.
        var (held, seen) = CellDenial.Judge(["READ"], "CTL-READ-OPEN\r\nREAD-DENIED\r\n", "CTL-READ-OPEN\r\nREAD-DENIED\r\n");
        Assert.False(held, seen);
        var (real, said) = CellDenial.Judge(["READ"], "CTL-READ-OPEN\r\nREAD-DENIED\r\n", "CTL-READ-OPEN\r\nREAD-OPEN\r\n");
        Assert.True(real, said);
    }

    [Fact]
    public void Every_word_of_a_row_must_hold_not_one_of_them()
    {
        var (held, seen) = CellDenial.Judge(["READ", "LIST"], "CTL-READ-OPEN\r\nREAD-DENIED\r\nCTL-LIST-OPEN\r\nLIST-OPEN\r\n", "READ-OPEN\r\nLIST-OPEN\r\n");
        Assert.False(held, seen);
        Assert.Contains("LIST", seen, StringComparison.Ordinal);
    }

    /// <summary>The body runs the control and the forbidden target through one line; the outside run swaps in the twin where one is named, and only there.</summary>
    [Fact]
    public void One_line_runs_on_the_control_on_the_forbidden_target_and_on_the_outside_target()
    {
        var (body, cell, outside) = CellDenial.Lines([new DenialProbe("READ", "type {path} > {sink}", @"C:\state\ledger", @"C:\ws\control"), new DenialProbe("INBOX", "echo x> {path}", @"C:\inbox\x", @"C:\ws\y", @"C:\twin\x")]);
        Assert.Equal(
            "type {0} > ctl0.out && (echo CTL-READ-OPEN) || (echo CTL-READ-DENIED)\ntype {1} > try0.out && (echo READ-OPEN) || (echo READ-DENIED)\n" +
            "echo x> {2} && (echo CTL-INBOX-OPEN) || (echo CTL-INBOX-DENIED)\necho x> {3} && (echo INBOX-OPEN) || (echo INBOX-DENIED)", body);
        Assert.Equal([@"C:\ws\control", @"C:\state\ledger", @"C:\ws\y", @"C:\inbox\x"], cell);
        Assert.Equal([@"C:\ws\control", @"C:\state\ledger", @"C:\ws\y", @"C:\twin\x"], outside);
    }

    /// <summary>
    /// A PATH IS ONE ARGUMENT, AND NOTHING IN IT IS CMD'S (Codex): a path with a space is left for the quoting of the command line; one without, whose characters cmd would
    /// read as operators, is escaped with carets. A quote or a trailing backslash cannot be carried at all, and is refused.
    /// </summary>
    [Fact]
    public void A_path_reaches_cmd_as_one_argument()
    {
        Assert.Equal(["type", @"C:\Users\A^&B\x"], CellDenial.CmdTokens("type {0}", @"C:\Users\A&B\x"));
        Assert.Equal(["type", @"C:\Users\A & B\x"], CellDenial.CmdTokens("type {0}", @"C:\Users\A & B\x"));
        Assert.Equal(["a", "&", "b"], CellDenial.CmdTokens("a\nb"));
        Assert.Throws<ArgumentException>(() => CellDenial.CmdTokens("type {0}", "C:\\a\"b"));
        Assert.Throws<ArgumentException>(() => CellDenial.CmdTokens("type {0}", "C:\\a\\"));
    }

    /// <summary>
    /// THE PROBE'S CALLS (<c>U-cell-probe</c>, in place of the two PowerShell scripts): the listing names its workspace control first, then the forbidden folder,
    /// each one argument whole (a quote in a path is no longer a script's quoting problem); the kill names its target alone, its own process being the probe's.
    /// Their verdicts are <c>CellProbeTests</c>'.
    /// </summary>
    [Fact]
    public void The_probe_calls_name_the_control_first_and_carry_each_path_whole()
    {
        Assert.Equal(["list", @"C:\Users\O'Neil\ws", @"C:\state"], CellDenial.ListProbe(@"C:\Users\O'Neil\ws", @"C:\state"));
        Assert.Equal(["kill", "4242"], CellDenial.KillProbe(4242));
        Assert.Equal("cell-probe.exe", CellDenial.ProbeExe);
    }
}
