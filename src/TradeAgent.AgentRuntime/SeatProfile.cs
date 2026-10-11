using System.Diagnostics;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// THE SEAT HOME, MADE BY THE APP AND VERIFIED BY IT (<c>U-seat-home</c>; AUTH-001, ORDER-005, CRED-001).
///
/// <para>Every codex process TradeAgent starts runs with <c>CODEX_HOME</c> and <c>HOME</c> inside <see cref="SeatPlan.Root"/>,
/// never the owner's own: his <c>~/.codex</c> carries his plugins, MCP servers, notify hook, skills and sign-in, and none of them
/// is something a seat was configured to have. The app creates the folders; nothing is copied from his.</para>
/// </summary>
public static class SeatProfile
{
    /// <summary>
    /// MAKES THE SEAT HOME READY FOR A START: its folders exist. Called by <see cref="AgentEnvironment.Apply(ProcessStartInfo,
    /// IReadOnlyDictionary{string, string}, RuntimeManifest)"/>, the one seam every process of a runtime passes.
    /// </summary>
    public static void Prepare(SeatPlan seat)
    {
        Directory.CreateDirectory(seat.Root);
        foreach (var (name, value) in seat.ResolvedEnvironment())
            if (seat.Environment[name].Contains("{seatHome}", StringComparison.Ordinal)) Directory.CreateDirectory(value);

        // THE CLI'S OWN CONFIG FILE, WRITTEN BY THE APP BEFORE EVERY START. A turn ignores it (--ignore-user-config), but `login`,
        // `features` and `mcp` take no such flag, and a seat can write this file: a seat-written chatgpt_base_url would steer the
        // next sign-in. Published whole through a rename, so a reader sees the old bytes or the new ones and never half of either.
        if (ConfigPath(seat) is { } path)
        {
            var bytes = ConfigBytes(seat);
            if (!SameBytes(path, bytes)) OwnerOnlyFile.Write(path, bytes);
        }
    }

    /// <summary>Where <see cref="SeatPlan.ConfigFile"/> is, or null for a plan with none.</summary>
    public static string? ConfigPath(SeatPlan seat) =>
        seat.ConfigFile is { Length: > 0 } rel
            ? Path.GetFullPath(Path.Combine(seat.Root, rel.Replace('/', Path.DirectorySeparatorChar)))
            : null;

    /// <summary>
    /// THE CONFIG FILE'S BYTES: the <see cref="SeatPlan.CliClass"/> deny set's keys as TOML, one per line — <c>-c key=value</c> as
    /// <c>key = value</c>, <c>--disable name</c> as <c>features.name = false</c>, <c>--enable name</c> as <c>= true</c>. The flags
    /// with no key (<c>--ignore-user-config</c> and its kind) have no line. UTF-8, no byte-order mark, LF.
    /// </summary>
    public static byte[] ConfigBytes(SeatPlan seat)
    {
        var lines = new List<string> { "# Written by TradeAgent before every start of this AI seat. Any change here is replaced." };
        var args = seat.ArgsFor(SeatPlan.CliClass);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disable" when i + 1 < args.Length: lines.Add($"features.{args[++i]} = false"); break;
                case "--enable" when i + 1 < args.Length: lines.Add($"features.{args[++i]} = true"); break;
                case "-c" or "--config" when i + 1 < args.Length:
                    var pair = args[++i];
                    var eq = pair.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0) lines.Add($"{pair[..eq].Trim()} = {pair[(eq + 1)..].Trim()}");
                    break;
            }
        }
        return new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");
    }

    static bool SameBytes(string path, byte[] bytes)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
