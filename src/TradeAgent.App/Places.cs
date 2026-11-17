using TradeAgent.Core;
using Page = TradeAgent.App.MainWindow.Page;

namespace TradeAgent.App;

/// <summary>Where in the window's one picture a page is looked at from.</summary>
enum Place { Floor, Talk, Desk, Board, Yours, Machine }

/// <summary>
/// WHERE EVERYTHING IS (<c>U-ui-world</c>). The window is one picture — the organisation standing on its floor, the market in front of
/// it, the security board beside it, the owner's place behind its chief, and the machine it all stands on — and every page of the app
/// is found at the thing it is about. There is no list of pages to search: there are six places, and a page is a leaf of one of them.
///
/// <para><b>Held as data</b> so a test reads what the window builds: every page is a leaf of exactly one place, no page is lost and none
/// appears twice. A page keeps the name the app's own sentences call it by — "the Settings page", "the Board page", "the Checks page" —
/// because a refusal that names a page nobody can find is a refusal nobody can act on.</para>
/// </summary>
static class Places
{
    public sealed record Leaf(Page Page, string Word);

    public sealed record Info(Place Place, string Name, string Icon, IReadOnlyList<Leaf> Leaves, string Says);

    /// <summary>
    /// The six places, in the dock's order, each with its leaves. A place's first leaf is where the place opens.
    /// <list type="bullet">
    /// <item><b>The floor</b>: the organisation itself — and the three ways of reading it, its structure, the work crossing it and what it costs.</item>
    /// <item><b>Talk</b>: the owner's conversation with the AI.</item>
    /// <item><b>The desk</b>: where the organisation meets the market — what is running now, and each day's report.</item>
    /// <item><b>The board</b>: the security board's decisions in force.</item>
    /// <item><b>Yours</b>: what only the owner decides, what reached him, what he hands the AI, and his private line.</item>
    /// <item><b>The machine</b>: what the floor stands on — this computer's parts, what the app did, and the market data.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<Info> All =
    [
        new(Place.Floor, Labels.NavFloor, Icons.Floor,
            [new(Page.Floor, Labels.PlaceOverview), new(Page.Organisation, Labels.NavOrganisation), new(Page.Work, Labels.NavWork), new(Page.Costs, Labels.NavCosts)],
            Labels.PlaceFloorSays),
        new(Place.Talk, Labels.PlaceTalk, Icons.Chat, [new(Page.Chat, "Chat")], Labels.PlaceTalkSays),
        new(Place.Desk, Labels.PlaceDesk, Icons.Dashboard, [new(Page.Dashboard, "Dashboard"), new(Page.Report, "Daily report")], Labels.PlaceDeskSays),
        new(Place.Board, Labels.PlaceBoard, Icons.Board, [new(Page.Board, Labels.BoardPage)], Labels.PlaceBoardSays),
        new(Place.Yours, Labels.PlaceYours, Icons.Owner,
            [new(Page.Owner, Labels.OwnerPage), new(Page.ForYou, Labels.NavForYou), new(Page.Inbox, "Inbox"), new(Page.Oversight, "Oversight")],
            Labels.PlaceYoursSays),
        new(Place.Machine, Labels.PlaceMachine, Icons.Settings,
            [new(Page.Checks, "Checks"), new(Page.Activity, "Activity"), new(Page.Settings, "Settings")], Labels.PlaceMachineSays)
    ];

    /// <summary>Every page with the word it goes by, in the dock's order: place by place, leaf by leaf.</summary>
    public static IEnumerable<(Page Page, string Word)> Pages => All.SelectMany(p => p.Leaves).Select(l => (l.Page, l.Word));

    /// <summary>The place a page is a leaf of.</summary>
    public static Info Of(Page page) => All.First(p => p.Leaves.Any(l => l.Page == page));

    /// <summary>The word a page goes by.</summary>
    public static string Word(Page page) => Of(page).Leaves.First(l => l.Page == page).Word;

    /// <summary>Whether a page opens a sheet over the floor. The floor's own overview does not: it is the floor.</summary>
    public static bool HasSheet(Page page) => page != Page.Floor;
}
