using TradeAgent.App;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE UNCONFIRMED CARD'S ANSWERS SAY SOMETHING TRUE OF THE ROW THEY ARE ON (<c>U-inflight-owner</c>, <c>U-press-row-answer</c>).
///
/// <para>The card is the only route into <c>ForceResolve</c>, and its buttons are assertions the owner signs with the note he
/// types. Since an order the platform answered and no longer lists reaches the card, a row there can carry the platform's own
/// reference — the order existed — and "No order exists" would ask him to assert something false of it. Such a row asks
/// whether it filled; a row with no reference keeps the old words. Both buttons record the same two states as before.</para>
///
/// <para>And a press's own close the platform answered and has not finished — <c>ACKNOWLEDGED</c>, <c>WORKING</c>,
/// <c>PARTIALLY_FILLED</c>, <c>CANCEL_PENDING</c> — is offered "It is no longer working at your platform" and "It was
/// filled" beside "Our record is right": nothing but the owner settles a press's row, and a close whose platform update was
/// lost had no answer that could end it. Not "It did not fill": a partly filled close did. Every other row keeps the answers
/// it had. What is read here is the one function the card builds its buttons from and relabels them with on every
/// tick.</para>
/// </summary>
public class UnconfirmedCardTests
{
    [Fact]
    public void An_order_the_platform_answered_is_confirmed_as_not_filled_rather_than_not_existing()
    {
        Assert.Equal(("It did not fill", "Confirm: I checked in ATAS and this order did not fill"),
            DashboardPage.CancelledAnswer("FB-7"));
        Assert.Equal(("It did not fill", "Confirm: I checked in ATAS and this order did not fill"),
            DashboardPage.CancelledAnswer("ext:none/TA-u-inflight-owner"));
    }

    [Fact]
    public void An_order_with_no_reference_keeps_asking_whether_it_exists_at_all()
    {
        Assert.Equal(("No order exists", "Confirm: I checked in ATAS and no such order exists"),
            DashboardPage.CancelledAnswer(null));
        Assert.Equal(("No order exists", "Confirm: I checked in ATAS and no such order exists"),
            DashboardPage.CancelledAnswer(""));
    }

    /// <summary>
    /// (d) A PRESS'S OWN ROW THE PLATFORM STILL HOLDS LIVE IS OFFERED "NO LONGER WORKING" AND "FILLED" BESIDE ITS OWN ANSWER,
    /// in each of the four states; any other row in that state keeps its one answer.
    ///
    /// <para><b>RED on the base</b>: one answer, "Our record is right", for the press's row too.</para>
    /// </summary>
    [Theory]
    [InlineData(ExecutionState.ACKNOWLEDGED, "acknowledged")]
    [InlineData(ExecutionState.WORKING, "working")]
    [InlineData(ExecutionState.PARTIALLY_FILLED, "partially filled")]
    [InlineData(ExecutionState.CANCEL_PENDING, "cancel pending")]
    public void A_press_row_still_live_at_the_platform_is_offered_no_longer_working_and_filled_beside_its_own(
        ExecutionState state, string word)
    {
        foreach (var reference in new[] { "FB-7", null })
        {
            var press = DashboardPage.Answers(state, isPress: true, reference);
            Assert.Equal([state, ExecutionState.CANCELLED, ExecutionState.FILLED], press.Select(a => a.Outcome));
            Assert.Equal(($"Our record is right — it is {word}", $"Confirm: I checked in ATAS and this order is {word}"),
                (press[0].Label, press[0].Armed));
            Assert.Equal(("It is no longer working at your platform",
                    "Confirm: I checked in ATAS and this order is no longer working there"),
                (press[1].Label, press[1].Armed));
            Assert.Equal(("It was filled", "Confirm: I checked in ATAS and this order was filled"),
                (press[2].Label, press[2].Armed));

            // ONE ANSWER OTHERWISE: any other row the platform holds live is agreed with, and nothing else.
            var other = Assert.Single(DashboardPage.Answers(state, isPress: false, reference));
            Assert.Equal(($"Our record is right — it is {word}", state), (other.Label, other.Outcome));
        }
    }

    /// <summary>
    /// THE GUARD, GREEN BEFORE AND AFTER: every row that is not a press's own live close keeps exactly the answers it had — a
    /// state only the platform's answer produced, its one; anything nobody answered, "It was filled" and the CANCELLED answer
    /// in the row's own words — press row or not.
    /// </summary>
    [Fact]
    public void Every_other_row_keeps_the_answers_it_had()
    {
        foreach (var isPress in new[] { false, true })
        {
            foreach (var (state, word) in new[]
                     {
                         (ExecutionState.FILLED, "filled"), (ExecutionState.CANCELLED, "cancelled"),
                         (ExecutionState.REJECTED, "refused by the broker")
                     })
            {
                var one = Assert.Single(DashboardPage.Answers(state, isPress, "FB-7"));
                Assert.Equal(($"Our record is right — it was {word}", $"Confirm: I checked in ATAS and this order was {word}", state),
                    (one.Label, one.Armed, one.Outcome));
            }

            foreach (var state in new[]
                     {
                         ExecutionState.UNKNOWN, ExecutionState.RECONCILING, ExecutionState.DISPATCHING, ExecutionState.CREATED
                     })
            {
                foreach (var reference in new[] { null, "FB-7" })
                {
                    var two = DashboardPage.Answers(state, isPress, reference);
                    Assert.Equal([ExecutionState.FILLED, ExecutionState.CANCELLED], two.Select(a => a.Outcome));
                    Assert.Equal(("It was filled", "Confirm: I checked in ATAS and this order was filled"),
                        (two[0].Label, two[0].Armed));
                    Assert.Equal(DashboardPage.CancelledAnswer(reference), (two[1].Label, two[1].Armed));
                }
            }
        }
    }
}
