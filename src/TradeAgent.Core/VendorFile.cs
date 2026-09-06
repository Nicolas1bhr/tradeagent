using System.Text.Json;

namespace TradeAgent.Core;

/// <summary>
/// What an optional override file said, or why it could not be read. Never both, and never neither.
///
/// <see cref="Value"/> null with <see cref="Unreadable"/> null is the ordinary case: the file is not
/// there, so the shipped configuration stands. <see cref="Unreadable"/> set means the file EXISTS
/// and could not be turned into a value — which is a different thing entirely, and the distinction
/// this type exists to keep.
/// </summary>
public sealed record VendorFileRead<T>(T? Value, string? Unreadable) where T : class;

/// <summary>
/// READING THE TWO FILES THAT SAY WHAT TRADEAGENT RUNS AND WHERE IT LOOKS.
///
/// <c>runtimes.json</c> holds the agent CLIs' install, sign-in and sandbox commands;
/// <c>atas.json</c> holds ATAS's folders, process names and executables. Both are DATA on purpose
/// (see <c>CLAUDE.md</c>): a vendor that moves a folder or renames a flag should cost a one-line
/// edit, not a rebuild.
///
/// Both were read under a bare <c>catch (Exception)</c> that returned the BUILT-INS, so one bad byte
/// in a file the owner wrote to make TradeAgent more restrictive silently put the shipped command
/// back — a `codex` manifest with the sandbox-bypass flags removed reverting to one that carries
/// them, with nothing on any screen to say so (milestone review 2026-09-05b, Codex F16 and the
/// reviewer's UNVERIFIED 6).
///
/// The rule is `U-settings-closed`'s, applied to the files instead of the settings row: AN
/// UNREADABLE OVERRIDE IS THE MOST RESTRICTIVE OVERRIDE. Nothing built in stands in for it, the
/// caller refuses whatever it was about to do, and the reason reaches the owner in their own words.
/// An ABSENT file is not that: it is the shipped configuration and stays it.
/// </summary>
public static class VendorFile
{
    public static VendorFileRead<T> Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return new(null, null);

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return new(null, Why(ex)); }

        try
        {
            var value = Json.Read<T>(text);
            // A file holding nothing, or the literal null, is the shape a half-finished write leaves
            // behind. It is not an absent file and must not be treated as one — the empty settings
            // row that read as a fresh install is the same defect one layer down.
            return value is null ? new(null, "there is nothing in it") : new(value, null);
        }
        catch (Exception ex) { return new(null, Why(ex)); }
    }

    /// <summary>
    /// The reason, in words an owner can act on. No exception type, no stack, no file path, no
    /// command to run: the person reading this has a text editor and the file's name, and that is
    /// the whole repair.
    /// </summary>
    static string Why(Exception ex) => ex switch
    {
        JsonException => "the text in it is not valid JSON",
        UnauthorizedAccessException => "TradeAgent is not allowed to open it",
        _ => "TradeAgent could not open it"
    };
}
