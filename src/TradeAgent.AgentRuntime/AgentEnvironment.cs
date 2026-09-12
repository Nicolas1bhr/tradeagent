using System.Diagnostics;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT THE AGENT PROCESS IS ALLOWED TO INHERIT, AS A LIST OF NAMES.
///
/// <c>ProcessStartInfo.Environment</c> starts life as a COPY OF THIS PROCESS'S OWN ENVIRONMENT, and
/// every launcher in this assembly used to add TradeAgent's few variables on top of that copy. So
/// everything the app was started with reached the AI: whatever the owner exports in their shell
/// profile, whatever an installer left behind, whatever a parent process was holding. On a machine
/// where somebody had once exported a broker or provider credential, the credential was in the
/// agent's environment for every turn, and nothing in the product said so.
///
/// The rule is a whitelist and it is stated as one. Not a blacklist of names that look like secrets:
/// a blacklist is a guess about what the next variable will be called, and it is wrong the first
/// time somebody names one something ordinary.
///
/// What passes: the platform's own machinery (where the home directory is, where temp is, what the
/// locale is, and on Windows the handful of variables without which ordinary programs misbehave),
/// TradeAgent's own <c>TRADEAGENT_*</c>, and the variables the vendor's own CLI reads, which come
/// from that runtime's manifest because vendor commands are data.
///
/// It is NOT a sandbox and it protects nothing on disk: the agent runs as the same user and can read
/// the same files. What it removes is one specific way for a secret to arrive somewhere nobody
/// decided to put it.
/// </summary>
public static class AgentEnvironment
{
    /// <summary>
    /// Names common to both platforms.
    ///
    /// <c>TRADEAGENT_HOME</c> and <c>TRADEAGENT_PIPE</c> are here rather than among the additions
    /// because they are how a NON-DEFAULT install says where it is — a portable build on a stick, or
    /// a test with its own scratch directory — and dropping them would point the agent's <c>trade</c>
    /// at a different installation from the one that launched it.
    /// </summary>
    static readonly string[] Common =
    [
        "PATH",
        "TRADEAGENT_HOME", "TRADEAGENT_PIPE",
        "LANG", "LC_ALL", "LC_CTYPE", "TZ"
    ];

    static readonly string[] WindowsOnly =
    [
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT",
        "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "PUBLIC",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ALLUSERSPROFILE",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "OS",
        "USERNAME", "USERDOMAIN", "COMPUTERNAME"
    ];

    static readonly string[] UnixOnly =
    [
        "HOME", "TMPDIR", "USER", "LOGNAME", "SHELL",
        "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"
    ];

    /// <summary>Every name that may cross, on this platform, before the vendor's own are added.</summary>
    public static IReadOnlyList<string> PassThrough =>
        [.. Common, .. OperatingSystem.IsWindows() ? WindowsOnly : UnixOnly];

    /// <summary>
    /// Replaces the inherited environment with the whitelist plus what TradeAgent hands over.
    ///
    /// <c>Clear()</c> is the load-bearing line and it is first: without it every name below is an
    /// addition to the inherited copy rather than a replacement of it, which is the exact defect
    /// this method exists for.
    /// </summary>
    /// <param name="additions">
    /// TradeAgent's own variables for this launch — see <see cref="WorkspaceBuilder.EnvironmentFor"/>.
    /// Applied last, so a name the app sets wins over the same name inherited.
    /// </param>
    /// <param name="vendor">
    /// Names this runtime's own CLI reads (<see cref="RuntimeManifest.KeepEnvironment"/>). Data, not
    /// code: a vendor that starts reading a new variable is a one-line change to runtimes.json.
    /// </param>
    public static void Apply(ProcessStartInfo psi, IReadOnlyDictionary<string, string> additions,
        IEnumerable<string>? vendor = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentNullException.ThrowIfNull(additions);

        psi.Environment.Clear();

        foreach (var name in PassThrough.Concat(vendor ?? []))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) psi.Environment[name] = value;
        }

        foreach (var (k, v) in additions) psi.Environment[k] = v;
    }
}
