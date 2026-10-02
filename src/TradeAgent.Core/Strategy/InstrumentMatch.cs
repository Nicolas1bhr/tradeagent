using TradeAgent.Core.Db;

namespace TradeAgent.Core.Strategy;

/// <summary>
/// A PROGRAM IS MEASURED ON BARS OF THE INSTRUMENT IT NAMES, OR NOT ON THAT DATASET AT ALL.
///
/// <para><b>Why it is a rule and not a convenience.</b> A program names one instrument on its first
/// line, and that line is something the agent types. The quantity step a run rounds to — and, since
/// <see cref="VenueCostModel"/>, the venue costs a verdict is scored under — are looked up by the
/// DATASET's instrument, the one the collector recorded beside the bytes, which is right: a size that
/// came from a word the agent chose would be a size it could arrange. But it left a program saying
/// <c>instrument ES</c> free to be measured over BTCUSDT bars at BTCUSDT's step, recorded as a trial and
/// judged — evidence about one instrument filed under a program about another, and a strategy that
/// could not trade what it was promoted to trade.</para>
///
/// <para><b>One check, two callers, both BEFORE anything is charged</b>: <c>Backtests.Run</c> before
/// the trial is looked at and the run is made, and <c>Referee.RequestVerdict</c> before the verdict
/// budget is touched. The words name both instruments, because the repair is one of two and only the
/// caller knows which: rename the program's instrument, or run it over a dataset of the one it
/// names.</para>
///
/// <para><b>A dataset that records no instrument is not refused here</b>; there is nothing on it to
/// disagree with. Such bars have no step to look up and no venue to price, and the callers that need
/// either already say so in their own words.</para>
/// </summary>
public static class InstrumentMatch
{
    /// <summary>
    /// Why this program may not be measured on this dataset, in words naming both instruments, or null
    /// because it may. The comparison is ORDINAL: the dataset's symbol is the exact key its step is
    /// looked up by, and the parser writes a program's symbol in upper case already.
    /// </summary>
    public static string? Refusal(StrategyProgram program, DatasetRecord dataset)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.InstrumentSymbol is not { Length: > 0 } recorded) return null;
        if (string.Equals(program.Instrument, recorded, StringComparison.Ordinal)) return null;

        var venue = dataset.VenueId is { Length: > 0 } v ? $" on {v}" : "";
        return $"this program says `instrument {program.Instrument}` and dataset {dataset.Id} holds bars of "
            + $"{recorded}{venue}, as the collector recorded them. A result is evidence about the instrument it "
            + "was measured on, and the dataset's instrument is also the key its quantity step and its venue "
            + "costs are looked up by, so TradeAgent will not measure a program about one instrument on "
            + $"another's bars. Write `instrument {recorded}` in the program, or use a dataset of "
            + $"{program.Instrument}.";
    }
}
