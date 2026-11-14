using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// ONE FOLDER OF THE OWNER'S COMPUTER THAT KEEPS EVERY AI SEAT FROM STARTING (<c>U-windows-cards-2</c>): a name of
/// <see cref="SeatPlan.AccountForbidden"/> under the account's profile, or of <see cref="SeatPlan.MachineForbidden"/> under the machine's
/// program-data folder, that is not absent or a plain, empty folder (<see cref="SeatProfile.FirstHeld"/>).
/// </summary>
/// <param name="Path">The forbidden path itself, as the refusal names it.</param>
/// <param name="Holds">What is there, in the refusal's words, naming nothing in it.</param>
/// <param name="Move">What a move aside renames: the first link at or below the root on the way to <paramref name="Path"/>, never followed,
/// or <paramref name="Path"/> itself.</param>
/// <param name="Items">How many entries the folder <paramref name="Move"/> holds (1 for a link or a file); null where it could not be counted.</param>
/// <param name="OnMachine">True for the machine's folder, false for the account's.</param>
/// <param name="Root">The folder it was looked for under: the account's profile, or the machine's program-data folder.</param>
/// <param name="Identity">What Windows knows <paramref name="Move"/> by — its volume and file index, whatever it is named
/// (<c>U-windows-cards-3</c>) — so the second press moves the very folder the first one showed or nothing; null off Windows, or where it
/// could not be read (and then nothing moves).</param>
public sealed record HeldFolder(string Path, string Holds, string Move, int? Items, bool OnMachine, string Root, string? Identity = null)
{
    /// <summary>"3 items", "1 item", or "what is there" where nothing could be counted.</summary>
    public string Count => Items switch { null => "what is there", 1 => "1 item", var n => $"{n} items" };
}

/// <summary>One rename the owner's second press carries out: what moves, and the folder beside it that it becomes.</summary>
public sealed record AsideStep(HeldFolder Held, string To);

/// <summary>
/// WHAT THE SECOND PRESS WILL DO, fixed at the first (<c>U-windows-cards-2</c>): the steps and the sentence that arms the press. The press
/// carries out exactly these steps or refuses each in words; it never works out new ones between the presses.
/// </summary>
public sealed record AsidePlan(IReadOnlyList<AsideStep> Steps)
{
    /// <summary>The armed sentence: "Yes — move 3 items to …; nothing is deleted".</summary>
    public string Sentence =>
        "Yes — move " + string.Join(" and ", Steps.Select(s => $"{s.Held.Count} to {s.To}")) + "; nothing is deleted";
}

/// <summary>What the second press did: whether nothing holds the AI any more, the words for the owner, and each rename made.</summary>
public sealed record SetAside(bool Cleared, string Words, IReadOnlyList<AsideStep> Moved);

/// <summary>
/// THE OWNER'S WAY OUT OF A HELD AI, WITH NO TERMINAL (<c>U-windows-cards-2</c>). On Windows codex reads the account's own
/// <c>.agents\skills</c> and the machine's <c>%ProgramData%\OpenAI\Codex</c> whatever TradeAgent sets, so while either holds anything no
/// AI seat starts (<see cref="SeatProfile.AccountRefusal"/>, <see cref="SeatProfile.MachineRefusal"/>). An owner who keeps his own skills
/// there would be held for good, and the no-terminal rule is the product: the Dashboard's two presses move each such folder ASIDE —
/// renamed, whole, to a TradeAgent-named folder beside it, which codex never reads.
///
/// <list type="bullet">
/// <item><b>Never deleted, never opened.</b> One rename per folder; what is in it is never read, copied or named. The count of its
/// entries is the only thing looked at below it.</item>
/// <item><b>Never through a link.</b> A link on the way is itself what is renamed, never followed (<see cref="SeatProfile.FirstHeld"/>'s
/// rule), so a move can never carry anything out of the folder it was in. On Windows the rename itself is by handle, each name opened
/// from the one above it and never through a reparse point, so a link made in the instant after the last look moves nothing either
/// (<c>U-windows-cards-3</c>).</item>
/// <item><b>The very folder the sentence named.</b> Windows' own id for it is read at the first press and checked on the handle that
/// renames it; a folder put in its place, even with the same name and count, is left as it is.</item>
/// <item><b>Never over anything.</b> A destination that exists in any form refuses that step in words.</item>
/// <item><b>Re-checked.</b> After the renames the same look runs again, and the words say whether the AI can start.</item>
/// <item><b>In-process only.</b> The Dashboard card is the one caller; no pipe op, <c>trade</c> verb or file reaches it.</item>
/// </list>
/// </summary>
public static class HeldFolders
{
    /// <summary>The words a moved folder's new name carries beside its old one.</summary>
    public const string AsideMark = "moved aside by TradeAgent";

    /// <summary>
    /// The folders that hold the AI on this computer now: the account's profile and the machine's folder as the seat check reads them,
    /// against this build's own seat plans — the ones an override file is compared to (<see cref="BrokerSeat"/>).
    /// </summary>
    public static IReadOnlyList<HeldFolder> Here() =>
        Find(SeatProfile.AccountProfile(), SeatProfile.MachineFolder(),
            RuntimeCatalog.BuiltIn().Select(m => m.Seat).OfType<SeatPlan>());

    /// <summary>The folders under <paramref name="profile"/> and <paramref name="machine"/> that hold the AI, account first; empty for none.</summary>
    public static IReadOnlyList<HeldFolder> Find(string? profile, string? machine, IEnumerable<SeatPlan> seats)
    {
        var plans = seats.ToList();
        var held = new List<HeldFolder>();
        Add(held, profile, plans.SelectMany(s => s.AccountForbidden), onMachine: false);
        Add(held, machine, plans.SelectMany(s => s.MachineForbidden), onMachine: true);
        return held;
    }

    static void Add(List<HeldFolder> held, string? root, IEnumerable<string> names, bool onMachine)
    {
        if (root is null) return;
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var look = onMachine ? SeatProfile.FirstHeldOnMachine(root, [name]) : SeatProfile.FirstHeld(root, [name]);
            if (look is not { } found) continue;
            if (held.Any(h => string.Equals(h.Path, found.Path, StringComparison.Ordinal))) continue;
            var move = FirstLinkOrSelf(root, found.Path);
            held.Add(new HeldFolder(found.Path, found.Holds, move, ItemsIn(move), onMachine, root,
                OperatingSystem.IsWindows() ? WindowsNoFollow.IdentityAt(move) : null));
        }
    }

    /// <summary>
    /// The first name from <paramref name="root"/> down to <paramref name="path"/> that is a link, a junction or any other reparse point —
    /// looked at itself, never followed — or <paramref name="path"/> itself where there is none, or where a name on the way is absent.
    /// </summary>
    static string FirstLinkOrSelf(string root, string path)
    {
        var at = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            if (AttributesOf(at) is not { } attributes) return path;
            if ((attributes & FileAttributes.ReparsePoint) != 0) return at;
        }
        return path;
    }

    /// <summary>A name's own attributes, never its target's; null where nothing is there, or where it cannot be looked at.</summary>
    static FileAttributes? AttributesOf(string at)
    {
        try { return File.GetAttributes(at); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The number of entries in the plain folder <paramref name="move"/>, 1 for a link or a file, null where it cannot be counted.</summary>
    static int? ItemsIn(string move)
    {
        if (AttributesOf(move) is not { } attributes) return null;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) return 1;
        try { return Directory.EnumerateFileSystemEntries(move).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// THE PLAN THE FIRST PRESS ARMS, or null where nothing holds the AI: each folder renamed to "&lt;name&gt; (moved aside by
    /// TradeAgent)" beside it, numbered on where that name is taken already.
    /// </summary>
    public static AsidePlan? Plan(IReadOnlyList<HeldFolder> held)
    {
        if (held.Count == 0) return null;
        return new AsidePlan([.. held.Select(h => new AsideStep(h, FreeNameBeside(h.Move)))]);
    }

    static string FreeNameBeside(string move)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(move))!;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(move));
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(parent, n == 1 ? $"{name} ({AsideMark})" : $"{name} ({AsideMark} {n})");
            if (AttributesOf(candidate) is null && !Exists(candidate)) return candidate;
        }
    }

    /// <summary>Anything at all at <paramref name="at"/>, a link to nothing included.</summary>
    static bool Exists(string at) => File.Exists(at) || Directory.Exists(at) || new FileInfo(at).LinkTarget is not null;

    /// <summary>
    /// THE SECOND PRESS: each step of <paramref name="plan"/> carried out where it still stands — the folder still held, still the same
    /// name to move, its destination still free — and refused in words where it does not; then <paramref name="again"/> looks once more.
    /// Nothing is deleted, opened or followed.
    /// </summary>
    public static SetAside Carry(AsidePlan plan, Func<IReadOnlyList<HeldFolder>> again)
    {
        var moved = new List<AsideStep>();
        var said = new List<string>();
        var now = again();
        foreach (var step in plan.Steps)
        {
            var from = step.Held.Move;
            // THE SAME FOLDER AS THE SENTENCE SAID (opus review): its path, what moves, what it holds and how many — or nothing moves.
            if (!now.Any(h => h == step.Held))
            {
                said.Add($"{step.Held.Path} was not moved: it changed after you pressed, so TradeAgent left it as it is now.");
                continue;
            }
            if (AttributesOf(step.To) is not null || Exists(step.To))
            {
                said.Add($"{from} was not moved: {step.To} exists already, and TradeAgent never moves anything over something. " +
                         "Press again to move it to a new name.");
                continue;
            }
            try
            {
                // LOOKED AT AGAIN THE MOMENT BEFORE THE RENAME (opus review): a link made on the way since the look above is refused.
                if (LinkAbove(step.Held.Root, from) is { } link)
                {
                    said.Add(LinkNow(from, link));
                    continue;
                }
                RaceUnderTest.Value?.Invoke(from);
                // AND ON WINDOWS THE RENAME ITSELF IS BY HANDLE (U-windows-cards-3; U-windows-cards-2 card (1)): a link made in the instant
                // after that look is found by the walk that the rename then acts through, so nothing ever moves within a link's target.
                if (OperatingSystem.IsWindows())
                {
                    if (RenameByHandle(step, from) is { } refused) { said.Add(refused); continue; }
                }
                else Rename(from, step.To);
                moved.Add(step);
                said.Add($"TradeAgent moved {step.Held.Count} from {from} to {step.To}. Nothing was deleted, and nothing in it was opened. " +
                         $"To put {(step.Held.Items == 1 ? "it" : "them")} back, rename that folder to \"{Path.GetFileName(from)}\" again — " +
                         "the AI will not start while it holds anything.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                said.Add($"{from} was not moved: Windows did not let TradeAgent rename it ({ex.Message}). Nothing was changed. " +
                         (step.Held.OnMachine
                             ? "That folder belongs to the whole computer, and an administrator of it may need to move it."
                             : "Close any program that has a file in it open, then press again."));
            }
        }

        var still = again();
        said.Add(still.Count == 0
            ? "Nothing TradeAgent checks holds the AI any more: it can start."
            : $"The AI still cannot start: {string.Join("; ", still.Select(h => $"{h.Path} {h.Holds}"))}.");
        return new SetAside(still.Count == 0, string.Join(" ", said), moved);
    }

    /// <summary>
    /// A TEST'S SEAM (<c>U-windows-cards-3</c>): called with the name about to move in the instant after the last look by path and before
    /// the rename, so a test can make the link the look could not see. Nothing in the product sets it, and no file, setting or pipe op
    /// reaches it.
    /// </summary>
    internal static readonly AsyncLocal<Action<string>?> RaceUnderTest = new();

    /// <summary>The first folder below <paramref name="root"/> and above <paramref name="move"/> that is a link now; null where none is.</summary>
    static string? LinkAbove(string root, string move)
    {
        var at = root;
        var parts = Path.GetRelativePath(root, move).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts.SkipLast(1))
        {
            at = Path.Combine(at, part);
            if (AttributesOf(at) is { } a && (a & FileAttributes.ReparsePoint) != 0) return at;
        }
        return null;
    }

    static string LinkNow(string from, string link) =>
        $"{from} was not moved: {link} is a link now, and TradeAgent never moves anything through a link. Nothing was changed.";

    /// <summary>
    /// THE RENAME BY HANDLE, ON WINDOWS (<c>U-windows-cards-3</c>): from the root the look was made under, each name down to the one that
    /// moves is opened from the folder above it by that folder's handle, never through a reparse point (<see cref="WindowsNoFollow"/>); a
    /// link on the way refuses; what moves must be the very object the first press showed (<see cref="HeldFolder.Identity"/>); and it is
    /// renamed inside the folder whose handle is held, never over anything. While its handle is open no folder above it can be renamed, so
    /// nothing on the way can be swapped for a link before the rename. Null where it moved, the owner's words where it did not; a failure
    /// Windows reports throws, as the rename by path did.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? RenameByHandle(AsideStep step, string from)
    {
        var changed = $"{step.Held.Path} was not moved: it changed after you pressed, so TradeAgent left it as it is now.";
        var parts = Path.GetRelativePath(step.Held.Root, from).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Contains("..")) return changed;
        var parent = WindowsNoFollow.OpenRoot(step.Held.Root);
        try
        {
            var at = step.Held.Root;
            foreach (var part in parts.SkipLast(1))
            {
                at = Path.Combine(at, part);
                var status = WindowsNoFollow.TryOpen(parent, part, WindowsNoFollow.ReadAttributes, WindowsNoFollow.ShareAll,
                    WindowsNoFollow.Open, 0, out var next);
                if (status is WindowsNoFollow.NameNotFound or WindowsNoFollow.PathNotFound) return changed;
                if (status != WindowsNoFollow.Success) throw WindowsNoFollow.Failure(status, at, "opened");
                parent.Dispose();
                parent = next!;
                var attributes = WindowsNoFollow.AttributesOf(parent);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return LinkNow(from, at);
                if ((attributes & FileAttributes.Directory) == 0) return changed;
            }

            var opened = WindowsNoFollow.TryOpen(parent, parts[^1], WindowsNoFollow.Delete | WindowsNoFollow.ReadAttributes,
                WindowsNoFollow.ShareRead | WindowsNoFollow.ShareWrite, WindowsNoFollow.Open, 0, out var item);
            if (opened is WindowsNoFollow.NameNotFound or WindowsNoFollow.PathNotFound) return changed;
            if (opened != WindowsNoFollow.Success) throw WindowsNoFollow.Failure(opened, from, "opened");
            using (item)
            {
                if (step.Held.Identity is null || WindowsNoFollow.IdentityOf(item!) != step.Held.Identity) return changed;
                var renamed = WindowsNoFollow.RenameIn(item!, parent, Path.GetFileName(step.To));
                if (renamed == WindowsNoFollow.NameCollision)
                    return $"{from} was not moved: {step.To} exists already, and TradeAgent never moves anything over something. " +
                           "Press again to move it to a new name.";
                if (renamed != WindowsNoFollow.Success) throw WindowsNoFollow.Failure(renamed, from, "renamed");
                return null;
            }
        }
        finally { parent.Dispose(); }
    }

    /// <summary>
    /// One rename of the name itself by its path — a folder whole, or a link or a file as itself, never its target. Off Windows only, where
    /// no account profile or machine folder is read (<see cref="Here"/> finds nothing), so the product never reaches it; the tests' own
    /// folders do.
    /// </summary>
    static void Rename(string from, string to)
    {
        var attributes = File.GetAttributes(from);
        if ((attributes & FileAttributes.Directory) != 0) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
