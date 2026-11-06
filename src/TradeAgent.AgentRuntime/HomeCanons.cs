using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT AN INSTALL OR REMOVAL OF THE OWNER'S KIT REACHES, AND WHEN (<c>U-kit-socket-2</c>). A start writes every home's canon with the kit
/// as it may be granted then (<see cref="WorkspaceBuilder.Build"/>); until this unit a kit changed between starts reached the canons only
/// at the next "Start the AI". Now every home a start built in this process is rewritten at once — its canon files' kit part only, from
/// the same composition the next start would use (<see cref="Canon.WithKit"/>), each file replaced whole (written beside it, then moved
/// over its name, so no reader ever meets half a canon or none) and kept as the app's (<see cref="AppFileManifest.Keep"/>).
///
/// <para><b>A turn in flight keeps the canon it started with.</b> A session holds its home for the length of a turn (<see cref="Turn"/>:
/// a CLI turn from before its canon is put back and read until its process tree is ended, a harness turn from before its system text is
/// read until its last request). A home held when the kit changes is not touched: the change waits, and is applied — file, record and
/// kept text together — when the last turn holding it ends, before any next turn can start in it. A home no turn holds is rewritten at
/// once.</para>
///
/// <para>Nothing here grants anything: what a cell is GIVEN of the kit is read at each launch (<see cref="Kit.Granted"/>); this is what
/// every seat is TOLD of it.</para>
/// </summary>
public static class HomeCanons
{
    sealed class Home(AppFileManifest files, string folder, string homeDir)
    {
        public readonly object Gate = new();
        public AppFileManifest Files { get; } = files;
        public string Folder { get; } = folder;
        public string HomeDir { get; } = homeDir;

        /// <summary>Each canon file the start wrote here, by its path in the home: its text without the kit, and its runtime class.</summary>
        public readonly Dictionary<string, (string Plain, RuntimeClass Runtime)> Canons = new(StringComparer.Ordinal);

        public int Turns;
        public bool Pending;
        public Kit.View? PendingKit;
    }

    static readonly ConditionalWeakTable<AppFileManifest, ConcurrentDictionary<string, Home>> Homes = new();

    static ConcurrentDictionary<string, Home> Of(AppFileManifest files) => Homes.GetValue(files, _ => new(StringComparer.Ordinal));

    static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>
    /// A start wrote <paramref name="relPath"/> into the home at <paramref name="folder"/>: <paramref name="plain"/> is that canon without the
    /// kit's part, for <paramref name="runtime"/>. A change still waiting for this home is dropped: the start wrote the kit as it is now.
    /// </summary>
    internal static void Wrote(AppFileManifest files, string folder, string homeDir, string relPath, string plain, RuntimeClass runtime)
    {
        var home = Of(files).GetOrAdd(Key(folder), k => new Home(files, k, homeDir));
        lock (home.Gate)
        {
            home.Canons[relPath] = (plain, runtime);
            home.Pending = false;
            home.PendingKit = null;
        }
    }

    /// <summary>What a kit change reached: the homes rewritten now, those that wait for the turn they are in, and any that failed, in words.</summary>
    public sealed record Reach(int Now, int AfterTheirTurn, IReadOnlyList<string> Failed)
    {
        /// <summary>One sentence for the activity log.</summary>
        public string Words =>
            $"{Now} seat folder{(Now == 1 ? "" : "s")} told now" +
            (AfterTheirTurn > 0 ? $", {AfterTheirTurn} as soon as the turn {(AfterTheirTurn == 1 ? "it is" : "they are")} in ends" : "") +
            (Failed.Count > 0 ? $"; not told, said here: {string.Join("; ", Failed)}" : "");
    }

    /// <summary>
    /// REWRITES EVERY HOME'S CANON FOR <paramref name="kit"/> (null: no kit — its part goes). Every home a start built in this process on
    /// <paramref name="files"/> (default the install's own record): rewritten at once when no turn holds it, else when its turn ends.
    /// Never throws: a home that could not be rewritten keeps the canon it had, and is named in <see cref="Reach.Failed"/>.
    /// </summary>
    public static Reach Refresh(Kit.View? kit, AppFileManifest? files = null)
    {
        int now = 0, later = 0;
        var failed = new List<string>();
        foreach (var home in Of(files ?? AppFileManifest.Shared).Values)
            lock (home.Gate)
            {
                if (home.Canons.Count == 0) continue;
                if (home.Turns > 0)
                {
                    home.Pending = true;
                    home.PendingKit = kit;
                    later++;
                    continue;
                }
                try
                {
                    Apply(home, kit);
                    home.Pending = false;
                    home.PendingKit = null;
                    now++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{home.HomeDir} ({ex.Message})");
                }
            }
        return new Reach(now, later, failed);
    }

    /// <summary>
    /// A START WROTE ITS HOMES WITH THE KIT IT READ BEFORE ITS AWAITS (Codex review): <paramref name="built"/>, read before the runtime was
    /// detected. An install or removal in between told the homes that existed then, and the start then wrote its older reading over them
    /// or into homes nobody had told. The start calls this straight after its homes are built, with the kit as it is <paramref name="now"/>:
    /// the same kit, nothing to do (null); another, every home is told as <see cref="Refresh"/> tells it. A change after this call finds
    /// every home built, so <see cref="Refresh"/> alone reaches them.
    /// </summary>
    public static Reach? Settle(Kit.View? built, Kit.View? now, AppFileManifest? files = null) =>
        built?.Hash == now?.Hash ? null : Refresh(now, files);

    /// <summary>
    /// HOLDS THE HOME AT <paramref name="folder"/> FOR ONE TURN: no kit change touches its canon until the returned handle is disposed, and
    /// a change that waited is applied then. A home no start registered (a test's own folder) is held by nothing. Disposing never throws:
    /// a change that could not be applied then waits again, for the next turn's end.
    /// </summary>
    public static IDisposable Turn(AppFileManifest files, string folder)
    {
        if (!Of(files).TryGetValue(Key(folder), out var home)) return Nothing.Instance;
        lock (home.Gate)
        {
            // A CHANGE THAT COULD NOT BE APPLIED WHEN ITS LAST TURN ENDED is tried again before the next one reads anything.
            if (home.Turns == 0 && home.Pending) TryPending(home);
            home.Turns++;
        }
        return new Held(home);
    }

    static void TryPending(Home home)
    {
        try
        {
            Apply(home, home.PendingKit);
            home.Pending = false;
            home.PendingKit = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* waits for the next turn's end */ }
    }

    /// <summary>Each canon file of the home with <paramref name="kit"/>'s part: replaced whole, then kept as the app's. Under the home's gate.</summary>
    static void Apply(Home home, Kit.View? kit)
    {
        foreach (var (relPath, (plain, runtime)) in home.Canons)
        {
            var text = Canon.WithKit(plain, kit, runtime);
            Replace(home.Folder, relPath, Encoding.UTF8.GetBytes(text));
            home.Files.Keep(home.HomeDir, relPath, text, home.Folder);
        }
    }

    /// <summary>The folder a replacement is written in first: the home's app folder, which the material scanner never walks (a dot folder).</summary>
    const string Beside = ".tradeagent";

    /// <summary>
    /// <paramref name="relPath"/> REPLACED WHOLE: the bytes written to a new file in the home's <c>.tradeagent/</c> (never through a link —
    /// <see cref="HomeFiles"/>), then moved over the name — the name itself replaced, a link there included, never followed.
    /// </summary>
    static void Replace(string folder, string relPath, byte[] bytes)
    {
        HomeFiles.CreateDirectory(folder, Beside, replaceLinks: true);
        var temp = $"{Beside}/canon-{Guid.NewGuid():n}.tmp";
        if (!HomeFiles.TryCreate(folder, temp, bytes, flush: true))
            throw new IOException($"'{temp}' was already there, so the canon was not replaced.");
        try { HomeFiles.Move(folder, temp, relPath); }
        catch
        {
            try { HomeFiles.Remove(folder, temp); } catch (Exception) { /* a stray file in .tradeagent, read by nothing */ }
            throw;
        }
    }

    sealed class Held(Home home) : IDisposable
    {
        int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (home.Gate)
            {
                home.Turns--;
                if (home.Turns == 0 && home.Pending) TryPending(home);
            }
        }
    }

    sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
