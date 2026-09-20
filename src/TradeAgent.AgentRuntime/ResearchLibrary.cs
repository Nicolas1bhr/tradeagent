using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT THE APP PUTS IN A ROLE'S HOME SO THAT THE ROLE CAN WRITE A STRATEGY: the language reference
/// and the three worked programs.
///
/// <para><b>Why this exists at all.</b> There is no terminal in this product and no browser tab of
/// documentation. What the app writes into a home is the ONLY way a model learns anything about the
/// language — so before this class, a Research turn that wanted to write a program had exactly two
/// sources: one line of <c>AGENTS.md</c> naming <c>trade backtest --strategy …</c>, and the parser's
/// refusals. Reconstructing a grammar from refusals costs a turn per mistake, and the owner pays for
/// every one of them.</para>
///
/// <para><b>One source, copied and not restated.</b> <c>docs/STRATEGY-LANGUAGE.md</c> is content of
/// this assembly: the build copies that file, and this class writes the same bytes under one added
/// line. There is deliberately no second, hand-maintained description of the grammar in code — a
/// second copy is a second language the moment either one is edited, and only one of them has
/// tests.</para>
///
/// <para><b>App-owned, like <c>AGENTS.md</c>.</b> Every file below is rewritten on every start and a
/// role's edit of it is overwritten. A reference an agent can edit is a reference an agent can make
/// say that the language has a feature it does not have, and the next session — which remembers
/// nothing else — would read that as the app's own word. The first line of the reference says this
/// out loud, in the role's own words, so the overwrite is never a surprise.</para>
/// </summary>
public static class ResearchLibrary
{
    /// <summary>The reference's name, in the repository, in the build output and in the home alike.</summary>
    public const string ReferenceFile = "STRATEGY-LANGUAGE.md";

    /// <summary>Where the reference lands in a home, relative to it. Slashes: this is printed to agents.</summary>
    public const string ReferencePath = "research/" + ReferenceFile;

    /// <summary>Where the worked programs land in a home, relative to it.</summary>
    public const string ExamplesDir = "strategies/examples";

    /// <summary>
    /// THE THREE PROGRAMS <c>docs/COUNCIL.md</c> REQUIRES THE LANGUAGE TO EXPRESS ON DAY ONE, in the
    /// order <c>docs/STRATEGY-LANGUAGE.md</c> prints them. A list rather than a directory scan: what
    /// ships is decided here and not by whatever happens to be sitting in the output directory.
    /// </summary>
    public static readonly string[] Programs =
        ["ma-crossover.strategy", "opening-range-breakout.strategy", "rsi-mean-reversion.strategy"];

    /// <summary>The one a role is told to run first, as <c>AGENTS.md</c> and <c>trade schema</c> print it.</summary>
    public static string FirstExample => $"{ExamplesDir}/{Programs[0]}";

    /// <summary>
    /// THE LINE THAT SAYS WHO OWNS THE FILE, first, before the grammar. It names the examples too,
    /// because those are shipped byte for byte — a program with a banner comment in it would hash to
    /// something other than the id the app pinned for it, so the ownership of that folder is stated
    /// here and in <c>AGENTS.md</c> rather than inside the programs themselves.
    /// </summary>
    public const string Banner =
        "> **TradeAgent writes this file into your folder on every start, exactly as it writes `AGENTS.md`."
        + " Anything you change here — or in `strategies/examples/` beside it — is overwritten the next time"
        + " you wake, so copy it somewhere else in your folder if you want to annotate it.**";

    /// <summary>The build output directory this assembly's content files were copied into.</summary>
    static string Shipped => AppContext.BaseDirectory;

    /// <summary>The reference as a home receives it, or null when this build shipped no content file.</summary>
    public static string? Reference()
    {
        var file = Path.Combine(Shipped, ReferenceFile);
        if (!File.Exists(file)) return null;

        try
        {
            return Banner + "\n\n" + File.ReadAllText(file).ReplaceLineEndings("\n");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Writes the reference and the examples into <paramref name="home"/>, overwriting whatever is
    /// there. Called by <see cref="WorkspaceBuilder.Build"/> on every start.
    ///
    /// <para>A content file this build did not ship is SKIPPED rather than thrown over: the app
    /// starting without its reference is a bad day, and the app not starting is a worse one. That it
    /// is always shipped is a claim the test suite makes on every build, not one made here.</para>
    ///
    /// <para>The programs are copied BYTE FOR BYTE — no line-ending normalisation, no banner. Each is
    /// pinned to a <c>StrategyId</c> computed over its text, and an example whose id is not the pinned
    /// one is an example whose recorded runs belong to no version anybody can name.</para>
    ///
    /// <para><b>Every file written here is recorded in <paramref name="appFiles"/> as it is written</b>,
    /// under <paramref name="homeDir"/> — the home's name under the workspace, which is the form the
    /// material ledger records paths in. That record is the whole of what makes these files measurable
    /// as <see cref="MaterialOrigin.App"/> rather than as the role's own work; a write that did not
    /// land is not recorded, and measures as the role's.</para>
    /// </summary>
    public static void Write(string home, string homeDir, AppFileManifest appFiles)
    {
        if (Reference() is { } reference)
            WriteFile(Path.Combine(home, "research", ReferenceFile), text: reference,
                record: () => appFiles.Record(homeDir, ReferencePath, reference));

        var examples = Path.Combine(home, "strategies", "examples");
        Directory.CreateDirectory(examples);

        foreach (var name in Programs)
        {
            var from = Path.Combine(Shipped, "Strategies", name);
            if (!File.Exists(from)) continue;
            var bytes = ReadAll(from);
            WriteFile(Path.Combine(examples, name), bytes: bytes,
                record: () => appFiles.Record(homeDir, $"{ExamplesDir}/{name}", bytes!));
        }
    }

    static byte[]? ReadAll(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// An IO failure here leaves the previous copy in place and is not fatal: the same reading
    /// <c>WorkspaceBuilder.MoveOlderLayout</c> takes of a file somebody has open. The next start
    /// writes it again.
    /// </summary>
    static void WriteFile(string path, string? text = null, byte[]? bytes = null, Action? record = null)
    {
        if (text is null && bytes is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (text is not null) File.WriteAllText(path, text);
            else File.WriteAllBytes(path, bytes!);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        // AFTER the bytes are on disk, never before: the manifest says what the app wrote, and a
        // write that threw wrote nothing. Claiming it here would hand the next pass an App origin
        // for whatever is at that path — including what the role left there.
        record?.Invoke();
    }
}
