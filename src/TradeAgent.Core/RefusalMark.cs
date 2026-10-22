using System.Reflection;

namespace TradeAgent.Core;

/// <summary>
/// A REFUSAL OF RIGHTS OR AUTHORITY, MARKED IN PROCESS (<c>U-org-watcher-b</c>, seat A's ruling (b)). Every organisation-verb refusal answers
/// <see cref="ErrorCode.INVALID_REQUEST"/> — a malformed frame's code — so the code cannot tell the two apart and no new code is minted for it:
/// the throw site that refuses on rights or authority marks its exception here, and the pipe's one handler reads the mark. The mark never
/// reaches the wire: every answer stays byte-for-byte what it was. A malformed frame is never marked.
/// </summary>
public static class RefusalMark
{
    /// <summary>The engineering-log event the pipe writes for a marked refusal (component <c>Ipc</c>).</summary>
    public const string Event = "op_refused";

    const string Key = "TradeAgent.RefusalMark";

    static readonly HashSet<string> KnownOps = [.. typeof(Ops).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!)];

    /// <summary>Marks <paramref name="ex"/> as a refusal of rights or authority, and returns it for the throw.</summary>
    public static T Mark<T>(T ex) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(ex);
        ex.Data[Key] = true;
        return ex;
    }

    /// <summary>
    /// <paramref name="to"/>, marked exactly when <paramref name="from"/> was — for a site that re-throws a refusal as another exception
    /// type, so the mark its throw site set is never dropped on the way to the pipe (fix pass 1, R2).
    /// </summary>
    public static T Carry<T>(Exception from, T to) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(from);
        return IsMarked(from) ? Mark(to) : to;
    }

    /// <summary>Whether a throw site marked <paramref name="ex"/> a refusal.</summary>
    public static bool IsMarked(Exception ex) => ex.Data[Key] is true;

    /// <summary>An op as a refusal row names it: one of <see cref="Ops"/>' own names, or <c>unknown</c> — never the request's text.</summary>
    public static string OpName(string? op) => op is not null && KnownOps.Contains(op) ? op : "unknown";
}
