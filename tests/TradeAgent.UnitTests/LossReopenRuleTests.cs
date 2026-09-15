using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE ARITHMETIC A REOPEN RESTS ON, HELD ON ITS OWN.
///
/// <para><c>LossReopen.EligibleAt</c> is a pure function of the immutable breach record and one
/// duration, which is what lets the tick that acts on it and the surfaces that show it be the same
/// number. It is the LATER of two terms and each one is there for a failure that actually happened
/// in the design: midnight alone gives a 23:58Z breach two minutes of closure, and a duration alone
/// would let a closure end inside the very UTC day whose ledger figure closed it.</para>
/// </summary>
public class LossReopenRuleTests(ITestOutputHelper log)
{
    static readonly TimeSpan Day = TimeSpan.FromHours(24);

    /// <summary>
    /// THE 23:58Z BREACH — the case the day-keyed rule got wrong, and the reason this unit exists.
    /// Two minutes after a breach the account has just been flattened out of is not a pause; it is
    /// the same market, the same book and the same agent, with a clean key.
    /// </summary>
    [Fact]
    public void A_breach_two_minutes_before_midnight_is_not_eligible_at_midnight()
    {
        var confirmed = new DateTimeOffset(2026, 3, 10, 23, 58, 0, TimeSpan.Zero);
        var eligible = LossReopen.EligibleAt(confirmed, Day);
        log.WriteLine($"confirmed {confirmed:yyyy-MM-dd HH:mm}Z -> eligible {eligible:yyyy-MM-dd HH:mm}Z");

        Assert.Equal(new DateTimeOffset(2026, 3, 11, 23, 58, 0, TimeSpan.Zero), eligible);
        Assert.True(eligible > new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(Day, eligible - confirmed);
    }

    /// <summary>
    /// A BREACH EARLY IN THE DAY WAITS THE WHOLE DURATION TOO, so a closure is the same length
    /// whenever it happens. At the 24 h this build fixes, the duration is always the binding term.
    /// </summary>
    [Fact]
    public void A_breach_early_in_the_day_waits_the_same_full_closure()
    {
        var confirmed = new DateTimeOffset(2026, 3, 10, 0, 1, 0, TimeSpan.Zero);
        var eligible = LossReopen.EligibleAt(confirmed, Day);
        log.WriteLine($"confirmed {confirmed:yyyy-MM-dd HH:mm}Z -> eligible {eligible:yyyy-MM-dd HH:mm}Z");

        Assert.Equal(new DateTimeOffset(2026, 3, 11, 0, 1, 0, TimeSpan.Zero), eligible);
        Assert.Equal(Day, eligible - confirmed);
    }

    /// <summary>
    /// THE MIDNIGHT TERM IS WHAT A SHORTER CLOSURE RUNS INTO. It does nothing at 24 h and is kept
    /// because the duration is the number that changes (<c>U-reopen-2</c>): whatever the owner sets,
    /// a closure may not end inside the UTC day whose ledger figure closed it, or the scope is
    /// handed straight back to the same "today" that refused it.
    /// </summary>
    [Fact]
    public void A_shorter_closure_still_cannot_end_inside_the_utc_day_that_recorded_it()
    {
        var confirmed = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        var eligible = LossReopen.EligibleAt(confirmed, TimeSpan.FromHours(2));
        log.WriteLine($"confirmed {confirmed:yyyy-MM-dd HH:mm}Z, 2h closure -> eligible {eligible:yyyy-MM-dd HH:mm}Z");

        Assert.Equal(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero), eligible);
        Assert.True(eligible > confirmed + TimeSpan.FromHours(2));
    }

    /// <summary>
    /// A BREACH AT MIDNIGHT EXACTLY IS THE ONE INSTANT THE TWO TERMS AGREE ON, and the answer is a
    /// whole day later rather than the same instant — an off-by-one here would reopen a scope the
    /// moment it was closed.
    /// </summary>
    [Fact]
    public void A_breach_at_midnight_exactly_waits_a_whole_day()
    {
        var confirmed = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero);
        var eligible = LossReopen.EligibleAt(confirmed, Day);
        log.WriteLine($"confirmed {confirmed:yyyy-MM-dd HH:mm}Z -> eligible {eligible:yyyy-MM-dd HH:mm}Z");

        Assert.Equal(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero), eligible);
        Assert.True(eligible > confirmed);
    }
}
