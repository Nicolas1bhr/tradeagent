using TradeAgent.App;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE UNCONFIRMED CARD'S CANCELLED ANSWER SAYS SOMETHING TRUE OF THE ROW IT IS ON (<c>U-inflight-owner</c>).
///
/// <para>The card is the only route into <c>ForceResolve</c>, and its two buttons are assertions the owner signs with
/// the note he types. Since an order the platform answered and no longer lists reaches the card, a row there can carry
/// the platform's own reference — the order existed — and "No order exists" would ask him to assert something false
/// of it. Such a row asks whether it filled; a row with no reference keeps the old words. Both buttons record the same
/// two states as before. What is read here is the one helper the card builds the button from and relabels it with on
/// every tick.</para>
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
}
