using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;

namespace TradeAgent.AgentRuntime;

/// <summary>
/// WHAT ONE CALL OF A CLI SEAT'S COMMAND TOOL DID IN THE DRY RUN (<c>U-cli-code-mode</c>): the tools its first request offered, as
/// captured; the tool the mock called — code mode's <c>exec</c>, else <c>exec_command</c>, else none; what the second request carried
/// back as that call's output, or null for none; and the mark the output must hold, which only a call that RAN can produce.
/// A measurement: <see cref="SeatProfile"/> judges it.
/// </summary>
public sealed record ToolRun(IReadOnlyList<string> Offered, string? Tried, string? Output, string Expected);

/// <summary>
/// THE CLI CLASS'S ONE-CALL DRY RUN (<c>U-cli-code-mode</c>; <c>fleet/tmp/M4-regression/report.md</c>): a DRY <c>codex exec</c> of the CLI
/// class's own argument template and model against a Responses endpoint the app hosts on loopback, under a throwaway home — never the
/// seat home, which holds the sign-in, and never the owner's own. The mock answers the first request with one call of the command tool
/// it offers — code mode's <c>exec</c> running <c>text("probe-" + nonce)</c>, else <c>exec_command</c> running <c>echo probe-nonce</c> — and
/// reads what the second request carries back. No real model is asked anything, nothing is spent, and the throwaway home is deleted.
///
/// <para>Why: attempt 4's seats were offered <c>exec</c> and every call failed closed ("code-mode host is disabled") while each turn
/// exited 0. A seat whose tools cannot run is refused in words before admission, never passed off as turns. RUN 2026-10-23 on the dev
/// Mac against the pinned codex-cli 0.160.1 under a throwaway home with no sign-in (the same row runs on CI's ubuntu job on the linux pin):
/// the CLI class on gpt-6-luna was offered <c>exec</c> and its JavaScript ran; the same argv with <c>--disable code_mode_host</c> answered
/// "code-mode host is disabled" (<c>CliCodeModeHostTests</c>). ~1 s a run, then kept by binary, template and model.</para>
/// </summary>
public sealed class CodexToolProbe
{
    public static readonly CodexToolProbe Instance = new();

    /// <summary>The call id the mock's one call carries; the second request's output for it is the answer.</summary>
    public const string CallId = "tradeagent_tool_probe";

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    public async Task<ToolRun> RunAsync(SurfaceAsk ask, CancellationToken ct)
    {
        var manifest = ask.Manifest;
        var seat = manifest.Seat ?? throw new InvalidOperationException($"{manifest.DisplayName} has no seat profile");
        var mark = "probe-" + Guid.NewGuid().ToString("n")[..10];
        var dialogue = new Dialogue(mark);
        using var mock = new LoopbackHttp(dialogue.Answer);
        mock.Start();

        var root = Path.Combine(Path.GetTempPath(), "tradeagent-tools-" + Guid.NewGuid().ToString("n")[..12]);
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(root, "codex"));
        Directory.CreateDirectory(Path.Combine(root, "home"));
        Directory.CreateDirectory(work);
        try
        {
            // THE CLASS'S OWN ARGV, as a turn builds it, and the mock pinned as the provider.
            string[] provider =
            [
                "-c", $"model_provider=\"{CodexSurfaceProbe.ProviderId}\"",
                "-c", $"model_providers.{CodexSurfaceProbe.ProviderId}.name=\"TradeAgent tool probe\"",
                "-c", $"model_providers.{CodexSurfaceProbe.ProviderId}.base_url=\"{mock.Url}v1\"",
                "-c", $"model_providers.{CodexSurfaceProbe.ProviderId}.wire_api=\"responses\""
            ];
            string[] model = ask.Model is { Length: > 0 } m
                ? [.. manifest.ModelArgs.Select(a => a.Replace("{model}", m, StringComparison.Ordinal))]
                : [];
            var args = AgentArgs.Build(manifest.ExecArgs, "probe", manifest.JsonFlag, manifest.UnattendedArgs, model,
                [.. seat.ArgsFor(ask.SeatClass), .. provider], manifest.PromptOnStdin, manifest.StdinMarker, out var promptInArgv);

            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            CliAgentRuntime.SetCommand(psi, ask.Exe, args);
            // THE WHITELIST, THEN A THROWAWAY HOME OVER EVERY NAME THE SEAT HOME SETS, THEN THE CLASS'S OWN.
            AgentEnvironment.Apply(psi, new Dictionary<string, string>(), manifest.KeepEnvironment);
            foreach (var name in seat.Environment.Keys) psi.Environment[name] = Path.Combine(root, "home");
            psi.Environment["CODEX_HOME"] = Path.Combine(root, "codex");
            psi.Environment["HOME"] = Path.Combine(root, "home");
            if (seat.ClassEnvironment.TryGetValue(ask.SeatClass, out var own))
                foreach (var (k, v) in own) psi.Environment[k] = v;

            using var held = ProcessContainment.Start(psi);
            var p = held.Process;
            try
            {
                if (!promptInArgv) await p.StandardInput.WriteAsync("probe");
                p.StandardInput.Close();
            }
            catch (Exception) { /* it may have exited already; what it sent, or did not, is the answer */ }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(Deadline);
            var stdout = p.StandardOutput.ReadToEndAsync(timer.Token);
            var stderr = p.StandardError.ReadToEndAsync(timer.Token);
            try { await Task.WhenAny(dialogue.Done, p.WaitForExitAsync(timer.Token)).WaitAsync(timer.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                held.Kill();
                throw new InvalidOperationException($"{Path.GetFileName(ask.Exe)} did not finish its tool call within {Deadline.TotalSeconds:0} seconds");
            }

            // AN EXIT CAN RACE THE LAST REQUEST'S HANDLER: a moment for it, then the answer is what arrived.
            if (!dialogue.Done.IsCompleted) await Task.WhenAny(dialogue.Done, Task.Delay(TimeSpan.FromSeconds(2), ct));
            held.Kill();
            if (!dialogue.Done.IsCompleted)
            {
                if (dialogue.Offered is { } offered) return new ToolRun(offered, dialogue.Tried, null, mark);
                var why = Ansi.Strip(await Quietly(stderr) + "\n" + await Quietly(stdout)).Split('\n')
                    .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "no output";
                throw new InvalidOperationException(
                    $"{Path.GetFileName(ask.Exe)} exited {(p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "?")} " +
                    $"without asking the model: {why}");
            }
            return await dialogue.Done;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* the OS temp cleaner takes it */ }
        }
    }

    static async Task<string> Quietly(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// THE MOCK'S SIDE OF THE DRY RUN: request 1 — the tools offered, answered with one call of the command tool; the request carrying
    /// that call's output — the answer, and a closing message. Anything else is answered with a message.
    /// </summary>
    sealed class Dialogue(string mark)
    {
        readonly TaskCompletionSource<ToolRun> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly object _gate = new();

        public Task<ToolRun> Done => _done.Task;
        public IReadOnlyList<string>? Offered { get; private set; }
        public string? Tried { get; private set; }

        public (int Status, string Type, byte[] Body) Answer(LoopbackHttp.Request req, LoopbackHttp self)
        {
            if (req.Method != "POST" || !req.Path.EndsWith("/responses", StringComparison.Ordinal)) return (404, "text/plain", []);
            object item;
            lock (_gate)
            {
                if (Offered is null)
                {
                    Offered = CodexSurfaceProbe.Tools(req.Body);
                    var names = Offered.Select(BrokerSeat.ToolName).ToHashSet(StringComparer.Ordinal);
                    // CODE MODE'S EXEC FIRST: on a code-mode-only model it is the one command tool, and the JavaScript proves itself by
                    // joining the mark, which the call's own text does not hold.
                    if (names.Contains("exec"))
                    {
                        Tried = "exec";
                        item = new { type = "custom_tool_call", call_id = CallId, name = "exec", input = $"text(\"probe-\" + \"{mark["probe-".Length..]}\")" };
                    }
                    else if (names.Contains("exec_command"))
                    {
                        Tried = "exec_command";
                        item = new { type = "function_call", call_id = CallId, name = "exec_command", arguments = JsonSerializer.Serialize(new { cmd = $"echo {mark}" }) };
                    }
                    else
                    {
                        _done.TrySetResult(new ToolRun(Offered, null, null, mark));
                        item = Message("no command tool");
                    }
                }
                else
                {
                    if (OutputOf(req.Body) is { } output) _done.TrySetResult(new ToolRun(Offered, Tried, output, mark));
                    item = Message("done");
                }
            }
            return (200, "text/event-stream", Sse(item));
        }

        static object Message(string text) => new
        {
            type = "message", role = "assistant", id = "msg_" + Guid.NewGuid().ToString("n")[..8],
            content = new[] { new { type = "output_text", text } }
        };

        /// <summary>The output the request carries back for the probe's call, its text parts joined, or null for none.</summary>
        static string? OutputOf(string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var t)
                    || t.GetString() is not ("function_call_output" or "custom_tool_call_output")
                    || !item.TryGetProperty("call_id", out var id) || id.GetString() != CallId
                    || !item.TryGetProperty("output", out var output)) continue;
                return output.ValueKind switch
                {
                    JsonValueKind.String => output.GetString() ?? "",
                    JsonValueKind.Array => string.Join("\n", output.EnumerateArray()
                        .Select(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String
                            ? x.GetString() : part.ToString())),
                    _ => output.ToString()
                };
            }
            return null;
        }

        static byte[] Sse(object item)
        {
            var id = "resp_" + Guid.NewGuid().ToString("n")[..8];
            var events = new object[]
            {
                new { type = "response.created", response = new { id } },
                new { type = "response.output_item.done", item },
                new { type = "response.completed", response = new { id, usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } } }
            };
            var sse = new StringBuilder();
            foreach (var e in events)
            {
                var json = JsonSerializer.Serialize(e);
                using var d = JsonDocument.Parse(json);
                sse.Append("event: ").Append(d.RootElement.GetProperty("type").GetString()).Append('\n').Append("data: ").Append(json).Append("\n\n");
            }
            return Encoding.UTF8.GetBytes(sse.ToString());
        }
    }
}
