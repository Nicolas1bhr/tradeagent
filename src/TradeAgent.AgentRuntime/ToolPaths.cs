namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHERE A WORKER'S FILE TOOL MAY POINT, AND THE FOUR WAYS IT MAY NOT.
///
/// <para>This is the whole of the harness's path containment, in one place that can be exercised
/// without a provider, a database or a turn — because a path rule that can only be reached through an
/// HTTP conversation is a rule nobody is testing at the boundary that matters.</para>
///
/// <para><b>It is not a sandbox and does not claim to be.</b> The worker runs inside TradeAgent's own
/// process, so nothing here stops the PROCESS from reading the disk; what it stops is the model
/// choosing what the app reads on its behalf, which is the one thing a vendor CLI's "sandbox restricts
/// writes, not what enters its context" could never do (<c>docs/COUNCIL.md</c>). The remaining gap is
/// the same one <c>U-contain-2</c> owns and <c>Containment.Sandbox()</c> reports as
/// <c>NONE</c>.</para>
///
/// <para>The four refusals, each because it is a real way out of a directory:</para>
/// <list type="bullet">
/// <item><b>An absolute path</b>, which ignores the root entirely.</item>
/// <item><b>A <c>..</c> segment</b>, which is the shortest climb out — and it is refused as a SEGMENT
/// rather than by comparing full paths afterwards, so <c>a/../../b</c> is refused for what it says and
/// not only for where it happens to land.</item>
/// <item><b>Anything that resolves outside the root</b>, checked after normalisation, because a
/// segment check alone is an allowlist of the tricks somebody thought of.</item>
/// <item><b>A symbolic link inside the root</b>, which is a path that obeys every rule above and
/// still reads somewhere else. Only components BELOW the root are checked: the root itself may
/// legitimately sit under one (on macOS <c>/var</c> is a link to <c>/private/var</c>, so a walk that
/// went further up would refuse every path on the machine).</item>
/// </list>
/// </summary>
public static class ToolPaths
{
    /// <summary>
    /// The full path this relative path names inside <paramref name="root"/>, or the refusal in words.
    /// Exactly one of the two is non-null.
    /// </summary>
    /// <param name="allowed">
    /// Top-level directories inside the root this path must be under, or empty for "anywhere inside
    /// the root". It is what separates a READ grant (the role's whole home, its <c>in/</c> included)
    /// from a WRITE grant (<c>out/</c> and <c>trading/</c> only).
    /// </param>
    public static (string? Full, string? Refusal) Resolve(string root, string? path,
        IReadOnlyList<string>? allowed = null)
    {
        if (path is not { Length: > 0 })
            return (null, "no path was given.");

        if (Path.IsPathRooted(path) || path.StartsWith('~'))
            return (null, $"'{path}' is an absolute path. Paths are relative to your own folder.");

        var segments = path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            return (null, $"'{path}' climbs out of your own folder with '..', which is never allowed. "
                          + "You can only read and write inside the folder TradeAgent gave you.");

        string full, rooted;
        try
        {
            rooted = Path.GetFullPath(root);
            full = Path.GetFullPath(Path.Combine(rooted, Path.Combine(segments)));
        }
        catch (Exception ex) { return (null, $"'{path}' is not a path this build can read: {ex.Message}"); }

        var inside = full.Length > rooted.Length
                     && full.StartsWith(rooted, StringComparison.Ordinal)
                     && (full[rooted.Length] == Path.DirectorySeparatorChar
                         || rooted.EndsWith(Path.DirectorySeparatorChar));
        if (!inside)
            return (null, $"'{path}' is outside your own folder, so it was refused.");

        if (allowed is { Count: > 0 })
        {
            var first = segments[0];
            if (!allowed.Any(a => string.Equals(a, first, StringComparison.Ordinal)))
                return (null, $"'{path}' is not somewhere you may write. Write into "
                              + $"{string.Join(" or ", allowed.Select(a => $"'{a}/'"))} only — TradeAgent "
                              + "validates and publishes what you leave there.");
        }

        if (LinkInside(rooted, full) is { } link)
            return (null, $"'{path}' goes through '{link}', which is a link to somewhere else. "
                          + "Links are refused because a path that obeys every other rule can still "
                          + "read outside your folder.");

        return (full, null);
    }

    /// <summary>
    /// The first component BELOW the root that is a symbolic link, or null when none is. Walks down
    /// from the root so the root's own ancestry — which the app chose and the worker did not — is never
    /// what refuses a path.
    /// </summary>
    static string? LinkInside(string root, string full)
    {
        var relative = full[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var at = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            at = Path.Combine(at, segment);
            try
            {
                // Directory first: a link to a directory answers on both APIs on some platforms, and a
                // link this app cannot classify must still be refused rather than followed.
                if (Directory.Exists(at) && Directory.ResolveLinkTarget(at, returnFinalTarget: false) is not null)
                    return segment;
                if (File.Exists(at) && File.ResolveLinkTarget(at, returnFinalTarget: false) is not null)
                    return segment;
            }
            // A component this build cannot inspect is refused, not followed: the safe direction here
            // is the one that answers "no" about a path nobody could confirm.
            catch (IOException) { return segment; }
            catch (UnauthorizedAccessException) { return segment; }
        }
        return null;
    }
}
