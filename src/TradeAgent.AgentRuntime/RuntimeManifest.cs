using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

public enum InstallKind { None, Download, Npm, Winget, Manual }

public sealed class InstallPlan
{
    public InstallKind Kind { get; set; } = InstallKind.Manual;
    /// <summary>Download URL for the Windows x64 build. May contain {version}.</summary>
    public string? Url { get; set; }
    public string? ArchiveEntry { get; set; }
    public string? NpmPackage { get; set; }
    public string? WingetId { get; set; }
    public string? ManualUrl { get; set; }
}

/// <summary>
/// How to drive one agent CLI, expressed as DATA rather than code.
///
/// This is deliberate. OpenCode and Codex change their install and login commands on their own
/// schedule, and a build that hard-codes today's flags becomes wrong silently. Shipping the commands
/// as an overridable manifest means a wrong command is a one-line data fix — in
/// <c>%LOCALAPPDATA%\TradeAgent\runtimes.json</c>, no rebuild — instead of a broken product.
///
/// <see cref="Verified"/> is the honest bit: false means nobody has yet confirmed these commands
/// against the official docs on a real Windows machine. The Doctor surfaces that.
/// </summary>
public sealed class RuntimeManifest
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SignInDescription { get; set; } = "";

    public InstallPlan Install { get; set; } = new();

    /// <summary>Executable name. Resolved inside the managed tools directory first, then PATH.</summary>
    public string Executable { get; set; } = "";
    public string[] VersionArgs { get; set; } = ["--version"];
    public string[] AuthArgs { get; set; } = [];
    public string[] AuthStateArgs { get; set; } = [];
    public string? AuthStateSuccessPattern { get; set; }
    public string[] HealthArgs { get; set; } = ["--version"];

    /// <summary>Argument template for one-shot execution. "{prompt}" is replaced with the task text.</summary>
    public string[] TaskArgs { get; set; } = ["run", "{prompt}"];

    /// <summary>Argument template for the interactive session the user actually works in.</summary>
    public string[] InteractiveArgs { get; set; } = [];

    public bool RequiresNode { get; set; }
    public bool SelfContained { get; set; }

    /// <summary>False until the commands above have been checked against current official documentation.</summary>
    public bool Verified { get; set; }

    public string? DocsUrl { get; set; }
}

/// <summary>
/// The built-in manifests, overridable from disk. Treat the built-ins as a starting point that must
/// be checked against official documentation before release — see docs/RESEARCH-REQUIRED.md.
/// </summary>
public static class RuntimeCatalog
{
    public static string OverridePath => Path.Combine(Paths.Home, "runtimes.json");

    public static List<RuntimeManifest> BuiltIn() =>
    [
        new RuntimeManifest
        {
            Id = "opencode",
            DisplayName = "OpenCode",
            Description = "An open-source coding agent that runs on your machine. Works with several AI providers.",
            SignInDescription = "A browser window will open so you can sign in to your AI provider.",
            Executable = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode",
            Install = new InstallPlan { Kind = InstallKind.Manual, ManualUrl = "https://opencode.ai/docs/" },
            VersionArgs = ["--version"],
            AuthArgs = ["auth", "login"],
            AuthStateArgs = ["auth", "list"],
            TaskArgs = ["run", "{prompt}"],
            InteractiveArgs = [],
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://opencode.ai/docs/"
        },
        new RuntimeManifest
        {
            Id = "codex",
            DisplayName = "OpenAI Codex CLI",
            Description = "OpenAI's coding agent. Signs in with your ChatGPT account.",
            SignInDescription = "A browser window will open so you can sign in with your ChatGPT account.",
            Executable = OperatingSystem.IsWindows() ? "codex.exe" : "codex",
            Install = new InstallPlan { Kind = InstallKind.Manual, ManualUrl = "https://developers.openai.com/codex/cli/" },
            VersionArgs = ["--version"],
            AuthArgs = ["login"],
            AuthStateArgs = ["login", "status"],
            TaskArgs = ["exec", "{prompt}"],
            InteractiveArgs = [],
            SelfContained = true,
            Verified = false,
            DocsUrl = "https://developers.openai.com/codex/cli/"
        },
        new RuntimeManifest
        {
            Id = "custom",
            DisplayName = "Other AI assistant",
            Description = "Any command-line AI tool, described by a manifest. Developer-facing: the end user never edits this.",
            Executable = "",
            Install = new InstallPlan { Kind = InstallKind.None },
            Verified = false
        }
    ];

    public static List<RuntimeManifest> Load()
    {
        var builtIn = BuiltIn();
        if (!File.Exists(OverridePath)) return builtIn;
        try
        {
            var overrides = Json.Read<List<RuntimeManifest>>(File.ReadAllText(OverridePath)) ?? [];
            foreach (var o in overrides)
            {
                var i = builtIn.FindIndex(b => b.Id == o.Id);
                if (i >= 0) builtIn[i] = o; else builtIn.Add(o);
            }
        }
        catch (Exception) { /* a broken override file must not stop the app from starting */ }
        return builtIn;
    }

    public static void SaveOverrides(IEnumerable<RuntimeManifest> manifests) =>
        File.WriteAllText(OverridePath, Json.Write(manifests.ToList(), pretty: true));

    public static RuntimeManifest? Find(string id) => Load().FirstOrDefault(m => m.Id == id);
}
