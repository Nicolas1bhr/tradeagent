using TradeAgent.AgentRuntime;
using TradeAgent.App;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TASKBAR SAYS WHEN THE AI NEEDS HIM (<c>U-window-door</c> item 8): on Windows, while the seat home is signed out, the window's
/// title says so, so the taskbar and Alt-Tab show it while the app runs unattended behind other windows. Red at aa126ada: the title was
/// the constant "TradeAgent".
/// </summary>
public class WindowTitleTests
{
    [Fact]
    public void On_Windows_a_signed_out_seat_home_puts_sign_in_needed_in_the_title_and_nothing_else_does()
    {
        Assert.Equal("TradeAgent — sign in needed", MainWindow.WindowTitle(windows: true, SeatSignInState.SignedOut));
        Assert.Equal("TradeAgent", MainWindow.WindowTitle(windows: true, SeatSignInState.SignedIn));
        Assert.Equal("TradeAgent", MainWindow.WindowTitle(windows: true, SeatSignInState.Unknown));
        foreach (var state in Enum.GetValues<SeatSignInState>())
            Assert.Equal("TradeAgent", MainWindow.WindowTitle(windows: false, state));
    }
}
