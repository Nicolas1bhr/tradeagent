using System.Globalization;

namespace TradeAgent.Tests;

/// <summary>
/// THE ONE KNOB FOR "THE MACHINE RUNNING THIS SUITE IS SLOWER THAN THE ONE ITS FIXTURES WERE TUNED ON".
///
/// A timing fixture in this repository is a number chosen on a developer machine: how long a test
/// will wait for a handler to write down what it knows, how long it polls before it calls a state
/// unreachable, how much room it leaves a peer before something shipped expires. Those numbers were
/// right here and wrong on `windows-latest` three fix units in a row — U2a-fix (three tests),
/// U-win-flakes (two) and the two instances this class was written for — and each time the remedy
/// was to raise one number by hand for one test. This is the same remedy applied once, to all of
/// them, with the factor measured instead of guessed
/// (<c>RunnerSpeedProbeTests</c> prints it on every platform, every run).
///
/// WHAT MAY BE SCALED AND WHAT MAY NOT. These methods scale a FIXTURE'S PATIENCE: a wait, a poll
/// bound, a margin the test leaves around something else. They must never be applied to a product
/// deadline, a grace, a budget or a bound that the assertion is ABOUT — scaling one of those does
/// not make a slow machine pass, it makes the test stop testing. Two consequences, both deliberate:
///
///   * <see cref="Scale"/> is never below 1. A scale under one shortens a fixture's patience, which
///     is the failure this exists to end. An out-of-range or unparseable value is refused loudly
///     rather than ignored, because a scale that silently did not apply turns a green run into a
///     claim nobody can read.
///   * Scaling a fixture's patience can only ever turn a failure into a pass by giving the code
///     under test more time to do what the test says it does. It cannot make a wrong answer right:
///     every assertion on WHAT happened is untouched.
/// </summary>
public static class TestTime
{
    /// <summary>
    /// How much slower this machine is than the reference machine, from <c>TA_TEST_TIME_SCALE</c>.
    /// 1 (unscaled) everywhere it is unset, which is every developer machine and, until the workflow
    /// says otherwise, every CI runner.
    /// </summary>
    public static double Scale { get; } = Read();

    const double Max = 20.0;

    static double Read()
    {
        var raw = Environment.GetEnvironmentVariable("TA_TEST_TIME_SCALE");
        if (string.IsNullOrWhiteSpace(raw)) return 1.0;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new InvalidOperationException(
                $"TA_TEST_TIME_SCALE is '{raw}', which is not a number. It is the factor by which this " +
                "machine is slower than the reference machine; unset it or give it a value between 1 and 20.");
        if (v < 1.0 || v > Max)
            throw new InvalidOperationException(
                $"TA_TEST_TIME_SCALE is {v.ToString(CultureInfo.InvariantCulture)}. Below 1 it would SHORTEN " +
                $"fixture margins, which is the failure it exists to prevent; above {Max:0} it is a typo. " +
                "Measure the factor with RunnerSpeedProbeTests and use that.");
        return v;
    }

    /// <summary>
    /// A FIXTURE'S PATIENCE, scaled — how long this test is prepared to wait, or how much room it
    /// leaves around something else. Never a product deadline and never the quantity under test.
    /// </summary>
    public static TimeSpan Margin(TimeSpan fixtureWait) =>
        Scale == 1.0 ? fixtureWait : TimeSpan.FromTicks((long)(fixtureWait.Ticks * Scale));

    /// <inheritdoc cref="Margin(TimeSpan)"/>
    public static TimeSpan MarginMs(double ms) => Margin(TimeSpan.FromMilliseconds(ms));

    /// <inheritdoc cref="Margin(TimeSpan)"/>
    public static TimeSpan MarginSeconds(double seconds) => Margin(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// The same quantity where a fixture wants milliseconds as a number — a poll-loop bound, a
    /// <c>WaitAsync</c> in milliseconds. Rounded up, so a scale can never shave a millisecond off.
    /// </summary>
    public static int MarginMillis(int ms) => (int)Math.Ceiling(ms * Scale);
}
