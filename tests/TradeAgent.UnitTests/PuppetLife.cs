using System.Reflection;
using System.Runtime.InteropServices;
using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE PUPPET PROGRAM THIS BUILD MADE, and the manifest that starts it (<c>U-puppet-runtime</c>; <c>tests/TradeAgent.Puppet</c>).
/// The manifest is handed to the host in-process: its id is reserved, so no <c>runtimes.json</c> can carry it, and its
/// <see cref="RuntimeManifest.Puppet"/> flag is internal, so only this assembly can set it.
/// </summary>
static class Puppet
{
    static string Built(string name) =>
        typeof(Puppet).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == name).Value
        ?? throw new InvalidOperationException($"this assembly was built without its {name}");

    /// <summary>The puppet's assembly, as this configuration built it.</summary>
    public static string Dll
    {
        get
        {
            var dll = Path.GetFullPath(Built("PuppetDll"));
            Assert.True(File.Exists(dll), $"the puppet seat was not built: no {dll}");
            return dll;
        }
    }

    /// <summary>The folder this configuration built the trade command into, which the app deploys from.</summary>
    public static string TradeCliDir => Path.GetFullPath(Built("TradeCliDir"));

    /// <summary>Where this test host's .NET is: the folder above <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c>.</summary>
    public static string DotnetRoot =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    /// <summary>The <c>dotnet</c> program that runs the puppet's assembly.</summary>
    public static string DotnetHost
    {
        get
        {
            var host = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(host)) return host;
            var fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            Assert.True(fromSdk is { Length: > 0 } && File.Exists(fromSdk), $"no dotnet program beside this test host's runtime ({host})");
            return fromSdk!;
        }
    }

    /// <summary>
    /// A puppet manifest: the real codex event stream (<c>--json</c>), the prompt on stdin, the scripts in
    /// <paramref name="scripts"/>, and the seat class its test declares — <see cref="SeatPlan.PuppetClass"/> unless named.
    /// </summary>
    public static RuntimeManifest Manifest(string scripts, string? seatClass = null)
    {
        string[] turn = [Dll, "--scripts", scripts, "{prompt}"];
        return new RuntimeManifest
        {
            Id = RuntimeManifest.PuppetId,
            DisplayName = "Puppet seat",
            Description = "A scripted seat for TradeAgent's own tests. Runs no model.",
            Executable = DotnetHost,
            Install = new InstallPlan { Kind = InstallKind.None },
            VersionArgs = [Dll, "--version"],
            HealthArgs = [Dll, "--version"],
            ExecArgs = turn,
            ResumeArgs = turn,
            JsonFlag = "--json",
            PromptOnStdin = true,
            StdinMarker = "",
            Puppet = true,
            PuppetSeatClass = seatClass ?? SeatPlan.PuppetClass
        };
    }

    /// <summary>A fresh folder for one test's scripts, under this process's test home.</summary>
    public static string NewScripts(string name)
    {
        var dir = Path.Combine(TestEnv.Home, $"puppet-{name}-{Guid.NewGuid():n}"[..Math.Min(60, name.Length + 40)]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
