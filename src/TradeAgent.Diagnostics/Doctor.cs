using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.Diagnostics;

public sealed record CheckResult(string Name, HealthState State, string Detail, string UserAction,
    bool AutoRepairable, ErrorCode? Code = null)
{
    public static CheckResult Ok(string name, string detail = "") => new(name, HealthState.READY, detail, "", false);
    public static CheckResult Warn(string name, string detail, string action, ErrorCode? code = null, bool repairable = false)
        => new(name, HealthState.DEGRADED, detail, action, repairable, code);
    public static CheckResult Bad(string name, string detail, string action, ErrorCode? code = null, bool repairable = false)
        => new(name, HealthState.FAILED, detail, action, repairable, code);
}

public sealed record DoctorReport(DateTimeOffset At, IReadOnlyList<CheckResult> Checks)
{
    public bool AllHealthy => Checks.All(c => c.State is HealthState.READY);
    public IEnumerable<CheckResult> Problems => Checks.Where(c => c.State is not HealthState.READY);
}

/// <summary>
/// "Check everything". Every result carries a plain-language action, because a raw exception is not
/// guidance for someone who cannot read a stack trace.
/// </summary>
public sealed class Doctor(TradingGateway? gateway = null, bool allowNetwork = true)
{
    public async Task<DoctorReport> RunAsync(CancellationToken ct = default)
    {
        var r = new List<CheckResult>();

        // ---- machine
        r.Add(OperatingSystem.IsWindows()
            ? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? CheckResult.Ok("Windows version", Environment.OSVersion.VersionString)
                : CheckResult.Warn("Windows version", Environment.OSVersion.VersionString,
                    "TradeAgent targets Windows 11. It may work here but is not tested.")
            : CheckResult.Warn("Operating system", RuntimeInformation.OSDescription,
                "TradeAgent's trading features target Windows 11, because ATAS is a Windows program."));

        r.Add(RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64
            ? CheckResult.Ok("Processor", RuntimeInformation.OSArchitecture.ToString())
            : CheckResult.Bad("Processor", RuntimeInformation.OSArchitecture.ToString(), "A 64-bit computer is required."));

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Paths.Home)!);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            r.Add(freeGb >= 2
                ? CheckResult.Ok("Free disk space", $"{freeGb:N1} GB")
                : CheckResult.Bad("Free disk space", $"{freeGb:N1} GB",
                    "Free up at least 2 GB. TradeAgent needs room for the AI tools and its records."));
        }
        catch (Exception ex) { r.Add(CheckResult.Warn("Free disk space", ex.Message, "Could not read the disk.")); }

        r.Add(CanWrite(Paths.Home, out var writeErr)
            ? CheckResult.Ok("Folder permissions", Paths.Home)
            : CheckResult.Bad("Folder permissions", writeErr, "TradeAgent cannot write to its own folder.",
                ErrorCode.WORKSPACE_CORRUPT, repairable: true));

        // ---- network
        if (allowNetwork)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var resp = await http.GetAsync("https://api.nuget.org/v3/index.json",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                r.Add(resp.IsSuccessStatusCode
                    ? CheckResult.Ok("Internet and secure connections", $"HTTP {(int)resp.StatusCode}")
                    : CheckResult.Warn("Internet and secure connections", $"HTTP {(int)resp.StatusCode}",
                        "The internet reachable but responding oddly. Check any firewall or VPN."));
            }
            catch (Exception ex)
            {
                r.Add(CheckResult.Bad("Internet and secure connections", ex.Message,
                    "TradeAgent could not reach the internet. Check your connection, firewall or VPN."));
            }
        }

        // ---- workspace and tools
        r.Add(Directory.Exists(Paths.Workspace)
            ? CheckResult.Ok("AI workspace", Paths.Workspace)
            : CheckResult.Bad("AI workspace", "missing", "Press Repair workspace.", ErrorCode.WORKSPACE_CORRUPT, true));

        var tradeExe = Path.Combine(Paths.Bin, OperatingSystem.IsWindows() ? "trade.exe" : "trade");
        r.Add(File.Exists(tradeExe)
            ? CheckResult.Ok("trade command", tradeExe)
            : CheckResult.Bad("trade command", "not installed", "Press Repair. The AI cannot trade without it.",
                ErrorCode.IPC_UNAVAILABLE, true));

        foreach (var manifest in RuntimeCatalog.Load().Where(m => m.Id != "custom"))
        {
            var runtime = new CliAgentRuntime(manifest);
            var d = await runtime.DetectAsync(ct);
            if (!d.Installed)
            {
                r.Add(CheckResult.Warn($"{manifest.DisplayName}", "not installed",
                    $"Only needed if you chose {manifest.DisplayName}.", ErrorCode.AI_RUNTIME_NOT_FOUND, true));
                continue;
            }
            r.Add(CheckResult.Ok($"{manifest.DisplayName}", $"{d.Version} at {d.Path}"));
            if (!manifest.Verified)
                r.Add(CheckResult.Warn($"{manifest.DisplayName} commands",
                    "the install and sign-in commands in this build have not been confirmed against the vendor's current documentation",
                    "If sign-in misbehaves, the commands can be corrected in runtimes.json without reinstalling."));
        }

        // ---- IPC
        r.Add(await PipeReachable(ct)
            ? CheckResult.Ok("Trading service connection", Paths.PipeName)
            : CheckResult.Bad("Trading service connection", "the local trading service is not answering",
                "Restart TradeAgent.", ErrorCode.IPC_UNAVAILABLE, true));

        // ---- ATAS
        var atas = AtasInstallation.Detect();
        r.Add(atas.Installed
            ? CheckResult.Ok("ATAS installation", $"{atas.InstallDir} {atas.Version}")
            : CheckResult.Bad("ATAS installation", "not found",
                "Install ATAS, then press Retry.", ErrorCode.ATAS_NOT_FOUND));
        if (!atas.LayoutVerified)
            r.Add(CheckResult.Warn("ATAS folder layout",
                "the folders TradeAgent looks in have not been confirmed against a real ATAS install",
                "If ATAS is installed but not found, its folders can be corrected in atas.json."));
        r.Add(atas.Running
            ? CheckResult.Ok("ATAS running", "yes")
            : CheckResult.Warn("ATAS running", "no", "Press Open ATAS.", ErrorCode.ATAS_NOT_RUNNING, true));
        r.Add(atas.BridgeInstalled
            ? CheckResult.Ok("ATAS bridge files", atas.StrategyDir!)
            : CheckResult.Warn("ATAS bridge files", "not installed", "Press Install bridge.", ErrorCode.ATAS_BRIDGE_MISSING, true));

        // ---- live trading chain
        if (gateway is not null)
        {
            await gateway.RefreshHealthAsync(ct);
            foreach (var h in gateway.Health.Snapshot())
                r.Add(new CheckResult(h.Component, h.State, h.Detail,
                    h.State is HealthState.READY ? "" : "See the activity history for what happened.", false));

            var unreconciled = gateway.Requests.NeedingReconciliation();
            r.Add(unreconciled.Count == 0
                ? CheckResult.Ok("Order confirmation", "nothing outstanding")
                : CheckResult.Warn("Order confirmation", $"{unreconciled.Count} order(s) not yet confirmed",
                    "Trading stays paused until these are confirmed. This is deliberate.",
                    ErrorCode.TRADING_PAUSED_UNRECONCILED, true));
        }

        return new DoctorReport(DateTimeOffset.UtcNow, r);
    }

    static bool CanWrite(string dir, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static async Task<bool> PipeReachable(CancellationToken ct)
    {
        try
        {
            await using var c = new NamedPipeClientStream(".", Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(1500, ct);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A zip a nontechnical person can send for help. Logs only, and the secret files are never in it.
    /// </summary>
    public static string CreateSupportPackage(Database db, string? outputPath = null)
    {
        var target = outputPath ?? Path.Combine(Paths.Home, $"TradeAgent-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"ta-support-{Guid.NewGuid():n}");
        Directory.CreateDirectory(staging);
        try
        {
            var log = new LogStore(db);
            File.WriteAllText(Path.Combine(staging, "activity.txt"),
                string.Join('\n', log.RecentActivity(2000).Select(a => $"{a.At:u} [{a.Level}] {a.Text}")));

            File.WriteAllText(Path.Combine(staging, "environment.json"), Json.Write(new
            {
                app = Versions.App,
                protocol = Versions.ProtocolVersion,
                bridge_protocol = Versions.BridgeProtocolVersion,
                db_schema = Versions.DatabaseSchemaVersion,
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture.ToString(),
                dotnet = RuntimeInformation.FrameworkDescription,
                home = Paths.Home
            }, pretty: true));

            // Engineering log, minus anything that could carry a secret.
            var lines = db.Read(_ =>
            {
                using var cmd = db.Cmd("SELECT at,component,event,severity,request_id,metadata,exception FROM engineering_log ORDER BY id DESC LIMIT 5000");
                using var rd = cmd.ExecuteReader();
                var rows = new List<string>();
                while (rd.Read())
                    rows.Add($"{rd.GetValue(0)} {rd.GetValue(1)} {rd.GetValue(2)} {rd.GetValue(3)} req={rd.GetValue(4)} {rd.GetValue(5)} {rd.GetValue(6)}");
                return rows;
            });
            File.WriteAllText(Path.Combine(staging, "engineering.log"), string.Join('\n', lines));

            foreach (var f in Directory.GetFiles(Paths.Logs))
            {
                var name = Path.GetFileName(f);
                if (name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("secret", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(staging, name), true);
            }

            if (File.Exists(target)) File.Delete(target);
            ZipFile.CreateFromDirectory(staging, target);
            return target;
        }
        finally { try { Directory.Delete(staging, true); } catch (Exception) { } }
    }
}
