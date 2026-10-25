namespace TradeAgent.Core;

/// <summary>
/// THE NAME A WIN32 CALL OF THE APP'S OWN IS HANDED (<c>U-catalog-windows-path</c>).
///
/// <para>.NET's own file calls reach a path of <see cref="MaxPath"/> characters or more by adding the <c>\\?\</c> prefix
/// themselves; a <c>CreateFileW</c> or a <c>FILE_RENAME_INFO</c> the app calls through P/Invoke gets the string as it is, and on a
/// Windows whose long paths are off — the default, and the lent tests laptop's — Windows answers "the path specified cannot be
/// found" for it. That refused every credential, config and catalog <see cref="OwnerOnlyFile.Write"/> published once the
/// temp's name — the file's own plus 37 characters — reached 260: a seat home under a deep app home could not start, while
/// windows-latest, whose long paths are on, never saw it.</para>
/// </summary>
public static class Win32Path
{
    /// <summary>Win32's MAX_PATH: a name this long or longer reaches a classic Win32 call only verbatim.</summary>
    public const int MaxPath = 260;

    /// <summary>
    /// <paramref name="fullPath"/> as a Win32 call takes it at any length: unchanged below <see cref="MaxPath"/> or when it is
    /// already a <c>\\?\</c> or <c>\\.\</c> name; otherwise verbatim — <c>\\?\C:\…</c>, and <c>\\?\UNC\server\share\…</c> for
    /// <c>\\server\share\…</c>. A verbatim name is never normalised again, so the caller hands a full, normalised path
    /// (<see cref="Path.GetFullPath(string)"/>'s).
    /// </summary>
    public static string Verbatim(string fullPath)
    {
        if (fullPath.Length < MaxPath
            || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }
}
