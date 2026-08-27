using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace TradeAgent.WinAgent;

/// <summary>
/// A long-lived agent that lives inside the Windows interactive desktop session and does the GUI
/// work that SSH cannot.
///
/// Why this exists at all: a program started over SSH runs in a session with no desktop, so
/// screenshots come back black and clicks go nowhere (trap 2). The previous approach registered a
/// scheduled task per action — seconds of latency each, no way to read the screen except pixels, and
/// it lived only on the machine rather than in this repository. Driving something as large as ATAS
/// that way is blind clicking between screenshots.
///
/// Two decisions worth keeping:
///
///   * **UI Automation, not pixels.** ATAS is a WPF application, so its controls have names, types
///     and invoke patterns. Asking the tree for "the button called Start" survives a window moving,
///     a theme changing and a different screen resolution; a coordinate does not. Coordinates remain
///     available for the cases UIA cannot reach (custom-drawn chart surfaces).
///   * **Files, not sockets.** The transport is a directory of request and response files. A local
///     socket would be faster and would also make Windows Defender Firewall put a prompt on screen —
///     in front of a user this product promised would click Yes exactly once, and in front of
///     automation that has nobody to click it. Polling a directory costs milliseconds and prompts
///     nothing.
///
/// The agent holds element references between calls (e1, e2, ...), which is the whole reason it is a
/// resident process rather than a script: `tree` then `invoke --ref e12` is two SSH round trips
/// against one live UI, not two separate searches that may disagree.
/// </summary>
static class Program
{
    static readonly string Root = Environment.GetEnvironmentVariable("TA_AGENT_ROOT") ?? @"C:\ta\agent";
    static string InDir => Path.Combine(Root, "in");
    static string OutDir => Path.Combine(Root, "out");
    static string AliveFile => Path.Combine(Root, "alive.json");

    static readonly Dictionary<string, AutomationElement> Refs = new(StringComparer.Ordinal);
    static int _refSeq;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    static int Main(string[] args)
    {
        // FIRST, before anything reads a coordinate. This machine runs a scaled display, and a
        // DPI-unaware process is handed virtualised coordinates: GetWindowRect reported 2208x1533
        // for a window DWM described as 1530x914, and a capture of it came back scaled. Mixing the
        // two spaces puts every synthesised click somewhere near the control instead of on it —
        // which fails intermittently, looks like a flaky UI, and is very expensive to chase.
        // Per-monitor-v2 makes UIA rectangles, GetWindowRect, SetCursorPos and CopyFromScreen all
        // speak physical pixels.
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (EntryPointNotFoundException) { try { SetProcessDPIAware(); } catch (Exception) { } }

        Directory.CreateDirectory(InDir);
        Directory.CreateDirectory(OutDir);

        // One-shot mode, for diagnosing the agent without the queue.
        //
        //   winagent.exe --file C:\ta\req.json      (preferred)
        //   echo {"op":"ping"} | winagent.exe -
        //
        // JSON is deliberately NOT accepted as a bare argument. Between zsh here, ssh, cmd.exe and
        // PowerShell's native-argument parsing, the double quotes do not survive: the agent receives
        // {op:ping} and reports a parse error that looks like a bug in the agent rather than in the
        // four layers of quoting above it. A file or stdin crosses all four untouched.
        if (args.Length > 0)
        {
            var payload = args[0] switch
            {
                "-" => Console.In.ReadToEnd(),
                "--file" when args.Length > 1 => File.ReadAllText(args[1]),
                _ when args[0].StartsWith('{') => args[0],
                _ => throw new ArgumentException("usage: winagent [--file <path> | -]  (no argument runs the queue)")
            };
            Console.WriteLine(Handle(payload));
            return 0;
        }

        // Keep the display awake. This is a request, not a settings change: nothing about the
        // machine's configuration is altered and it lapses when the process exits. Changing the
        // lock or screensaver policy would be a security setting, and this tool does not touch those.
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

        var beat = new Thread(Heartbeat) { IsBackground = true };
        beat.Start();

        Log($"winagent up. root={Root} session={Process.GetCurrentProcess().SessionId} " +
            $"interactive={Environment.UserInteractive} desktop={DesktopName()}");

        while (true)
        {
            try
            {
                var pending = Directory.GetFiles(InDir, "*.json").OrderBy(f => f).ToArray();
                if (pending.Length == 0) { Thread.Sleep(80); continue; }

                foreach (var file in pending)
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    string body;
                    try { body = File.ReadAllText(file); }
                    catch (IOException) { continue; }   // still being written; next pass
                    File.Delete(file);
                    Write(id, Handle(body));
                }
            }
            catch (Exception ex) { Log("loop: " + ex.Message); Thread.Sleep(250); }
        }
    }

    static void Write(string id, string json)
    {
        var final = Path.Combine(OutDir, id + ".json");
        var tmp = final + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, final, overwrite: true);   // readers never see a half-written response
    }

    static void Heartbeat()
    {
        while (true)
        {
            try
            {
                File.WriteAllText(AliveFile, JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now.ToString("O"),
                    pid = Environment.ProcessId,
                    session = Process.GetCurrentProcess().SessionId,
                    interactive = Environment.UserInteractive,
                    desktop = DesktopName(),
                    screen = ScreenSize(),
                    // Carried on the heartbeat so tools/win-state.sh can tell the truth about this
                    // machine by reading one file, without a round trip through the request queue.
                    can_automate = CanDriveUi(),
                    can_capture = CanCapture()
                }, Json));
            }
            catch (Exception) { /* a failed heartbeat must not take the agent down */ }
            Thread.Sleep(2000);
        }
    }

    // ------------------------------------------------------------------ dispatch

    static string Handle(string body)
    {
        try
        {
            var req = JsonNode.Parse(body)?.AsObject() ?? throw new AgentException("request was not a JSON object");
            var op = Str(req, "op") ?? throw new AgentException("no 'op' in request");

            if (op == "batch")
            {
                var items = req["items"]?.AsArray() ?? throw new AgentException("batch needs 'items'");
                var results = new JsonArray();
                foreach (var item in items)
                {
                    var one = Handle(item!.ToJsonString());
                    var node = JsonNode.Parse(one)!;
                    results.Add(node);
                    // Stop on the first failure. A batch that keeps going after a click missed is a
                    // batch whose later steps acted on a screen nobody has seen.
                    if (node["ok"]?.GetValue<bool>() != true) break;
                }
                return Ok(new JsonObject { ["results"] = results });
            }

            return op switch
            {
                "ping" => OpPing(),
                "windows" => OpWindows(),
                "shot" => OpShot(req),
                "tree" => OpTree(req),
                "find" => OpFind(req),
                "invoke" => OpInvoke(req),
                "click" => OpClick(req),
                "type" => OpType(req),
                "key" => OpKey(req),
                "setvalue" => OpSetValue(req),
                "select" => OpSelect(req),
                "launch" => OpLaunch(req),
                "front" => OpFront(req),
                "wait" => OpWait(req),
                "sleep" => OpSleep(req),
                _ => throw new AgentException($"unknown op '{op}'")
            };
        }
        catch (AgentException ex) { return Err(ex.Message); }
        catch (Exception ex) { return Err($"{ex.GetType().Name}: {ex.Message}"); }
    }

    static string Ok(JsonNode? data = null) =>
        new JsonObject { ["ok"] = true, ["data"] = data }.ToJsonString(Json);

    static string Err(string message) =>
        new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString(Json);

    sealed class AgentException(string m) : Exception(m);

    // ------------------------------------------------------------------ ops

    static string OpPing()
    {
        var p = Process.GetCurrentProcess();
        return Ok(new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["session"] = p.SessionId,
            ["interactive"] = Environment.UserInteractive,
            ["desktop"] = DesktopName(),
            ["screen"] = ScreenSize(),
            ["user"] = Environment.UserName,
            // The honest answer to "can you actually drive a GUI right now". A session with no
            // desktop reports a blank station and every capture comes back black; saying so here is
            // the difference between a diagnosable failure and a mysterious one.
            // Two separate answers, because they come apart. A disconnected RDP session keeps a
            // working UI Automation tree and a live agent, and loses only the ability to render:
            // capture fails with "The handle is invalid". Reporting one number for both said
            // can_drive_ui:true on a session that could not photograph anything.
            ["can_automate"] = CanDriveUi(),
            ["can_capture"] = CanCapture(),
            ["dpi_aware"] = true
        });
    }

    static string OpWindows()
    {
        var arr = new JsonArray();
        foreach (var p in Process.GetProcesses())
        {
            IntPtr h;
            string title;
            try
            {
                h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                title = p.MainWindowTitle;
            }
            catch (Exception) { continue; }

            GetWindowRect(h, out var r);
            arr.Add(new JsonObject
            {
                ["process"] = p.ProcessName,
                ["pid"] = p.Id,
                ["title"] = title,
                ["hwnd"] = h.ToInt64(),
                ["visible"] = IsWindowVisible(h),
                ["rect"] = $"{r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}"
            });
        }
        return Ok(arr);
    }

    static string OpShot(JsonObject req)
    {
        var path = Str(req, "path") ?? Path.Combine(@"C:\ta\shots", $"shot-{DateTime.Now:HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var full = Bool(req, "full");
        var window = Str(req, "window");

        int x, y, w, h;
        string what;

        if (!full && window is not null)
        {
            var proc = FindWindowProcess(window) ?? throw new AgentException($"no visible window matching '{window}'");
            var hwnd = proc.MainWindowHandle;
            Front(hwnd);
            Thread.Sleep(500);
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);
            x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
            what = $"[{proc.ProcessName}] {proc.MainWindowTitle}";
        }
        else
        {
            var b = VirtualScreen();
            x = b.Left; y = b.Top; w = b.Right - b.Left; h = b.Bottom - b.Top;
            what = "virtual screen";
        }

        if (w <= 0 || h <= 0) throw new AgentException($"bad capture rectangle {w}x{h}");

        using var bmp = new Bitmap(w, h);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, bmp.Size);
        }
        catch (Exception ex)
        {
            // Almost always a disconnected RDP session: interactive, automatable, and with nothing
            // rendering. Say that, rather than passing up "The handle is invalid".
            throw new AgentException(
                $"the screen could not be captured ({ex.Message}). This session has no rendering " +
                "surface — usually a disconnected RDP session. UI Automation still works; only " +
                "pictures do not. Reconnect, or use the console session.");
        }
        bmp.Save(path, ImageFormat.Png);

        return Ok(new JsonObject
        {
            ["path"] = path,
            ["what"] = what,
            ["size"] = $"{w}x{h}",
            ["bytes"] = new FileInfo(path).Length,
            // A capture from a session with no desktop is uniformly black or white, and looks exactly
            // like a broken application. Say which it is rather than handing back a mystery image.
            ["uniform"] = UniformColour(bmp)
        });
    }

    static string OpTree(JsonObject req)
    {
        var root = Resolve(req, required: false) ?? RootFor(Str(req, "window"));
        var depth = Int(req, "depth") ?? 8;
        var all = Bool(req, "all");
        var sb = new StringBuilder();
        var count = 0;
        Walk(root, 0, depth, all, sb, ref count);
        return Ok(new JsonObject { ["elements"] = count, ["tree"] = sb.ToString() });
    }

    static void Walk(AutomationElement el, int level, int maxDepth, bool all, StringBuilder sb, ref int count)
    {
        if (level > maxDepth || count > 4000) return;
        AutomationElement.AutomationElementInformation info;
        try { info = el.Current; } catch (Exception) { return; }

        var interactive = IsInteractive(el, info);
        if (all || interactive || level == 0)
        {
            count++;
            var r = Ref(el);
            var name = Clip(info.Name);
            var id = string.IsNullOrEmpty(info.AutomationId) ? "" : $" #{Clip(info.AutomationId)}";
            var off = info.IsOffscreen ? " (offscreen)" : "";
            var en = info.IsEnabled ? "" : " (disabled)";
            sb.Append(' ', level * 2)
              .Append($"[{r}] {info.ControlType.ProgrammaticName.Replace("ControlType.", "")}")
              .Append(name.Length > 0 ? $" \"{name}\"" : "")
              .Append(id).Append(off).Append(en).Append('\n');
        }

        AutomationElementCollection kids;
        try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch (Exception) { return; }
        foreach (AutomationElement k in kids) Walk(k, level + 1, maxDepth, all, sb, ref count);
    }

    static string OpFind(JsonObject req)
    {
        var root = Resolve(req, required: false) ?? RootFor(Str(req, "window"));
        var query = Str(req, "query") ?? throw new AgentException("find needs 'query'");
        var wantType = Str(req, "type");
        var hits = new JsonArray();

        foreach (var el in Descendants(root, 4000))
        {
            AutomationElement.AutomationElementInformation info;
            try { info = el.Current; } catch (Exception) { continue; }

            var name = info.Name ?? "";
            var autoId = info.AutomationId ?? "";
            var type = info.ControlType.ProgrammaticName.Replace("ControlType.", "");

            if (wantType is not null && !type.Equals(wantType, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                autoId.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

            hits.Add(new JsonObject
            {
                ["ref"] = Ref(el),
                ["type"] = type,
                ["name"] = Clip(name),
                ["automationId"] = Clip(autoId),
                ["enabled"] = info.IsEnabled,
                ["offscreen"] = info.IsOffscreen,
                ["patterns"] = string.Join(",", Patterns(el))
            });
            if (hits.Count >= 60) break;
        }
        return Ok(new JsonObject { ["matches"] = hits.Count, ["hits"] = hits });
    }

    static string OpInvoke(JsonObject req)
    {
        var el = Resolve(req, required: true)!;
        // Invoke, then Toggle, then ExpandCollapse, then SelectionItem: the order runs from "press
        // this" to "choose this", which is the order a person would try them in.
        if (TryPattern<InvokePattern>(el, InvokePattern.Pattern, p => p.Invoke())) return Ok(Note("invoked"));
        if (TryPattern<TogglePattern>(el, TogglePattern.Pattern, p => p.Toggle())) return Ok(Note("toggled"));
        if (TryPattern<ExpandCollapsePattern>(el, ExpandCollapsePattern.Pattern, p =>
            { if (p.Current.ExpandCollapseState == ExpandCollapseState.Expanded) p.Collapse(); else p.Expand(); }))
            return Ok(Note("expanded/collapsed"));
        if (TryPattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern, p => p.Select())) return Ok(Note("selected"));

        // Nothing programmatic is available, so fall back to a real click at its centre. Said out
        // loud in the result, because a synthesised click can land on whatever is on top.
        ClickPoint(CentreOf(el));
        return Ok(Note("no invokable pattern; clicked its centre instead"));
    }

    static string OpClick(JsonObject req)
    {
        Point pt;
        if (req["ref"] is not null) pt = CentreOf(Resolve(req, required: true)!);
        else if (req["x"] is not null && req["y"] is not null) pt = new Point(Int(req, "x")!.Value, Int(req, "y")!.Value);
        else throw new AgentException("click needs either 'ref' or 'x' and 'y'");

        var right = string.Equals(Str(req, "button"), "right", StringComparison.OrdinalIgnoreCase);
        ClickPoint(pt, right, Bool(req, "double"));
        return Ok(Note($"clicked {(right ? "right" : "left")} at {pt.X},{pt.Y}{(Bool(req, "double") ? " (double)" : "")}"));
    }

    static string OpType(JsonObject req)
    {
        var text = Str(req, "text") ?? throw new AgentException("type needs 'text'");
        foreach (var ch in text) TypeChar(ch);
        return Ok(Note($"typed {text.Length} character(s)"));
    }

    static string OpKey(JsonObject req)
    {
        var keys = Str(req, "keys") ?? throw new AgentException("key needs 'keys'");
        SendChord(keys);
        return Ok(Note($"sent {keys}"));
    }

    static string OpSetValue(JsonObject req)
    {
        var el = Resolve(req, required: true)!;
        var value = Str(req, "value") ?? "";
        if (TryPattern<ValuePattern>(el, ValuePattern.Pattern, p => p.SetValue(value))) return Ok(Note("set via ValuePattern"));

        // No ValuePattern: focus it, select everything, and type. Slower and more fragile, which is
        // why it is the fallback and why it says which path it took.
        try { el.SetFocus(); } catch (Exception) { ClickPoint(CentreOf(el)); }
        Thread.Sleep(120);
        SendChord("CTRL+a");
        SendChord("DELETE");
        foreach (var ch in value) TypeChar(ch);
        return Ok(Note("no ValuePattern; focused and typed instead"));
    }

    static string OpSelect(JsonObject req)
    {
        var el = Resolve(req, required: true)!;
        if (TryPattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern, p => p.Select())) return Ok(Note("selected"));
        throw new AgentException("element does not support SelectionItem");
    }

    static string OpLaunch(JsonObject req)
    {
        var path = Str(req, "path") ?? throw new AgentException("launch needs 'path'");
        var psi = new ProcessStartInfo(path)
        {
            Arguments = Str(req, "args") ?? "",
            WorkingDirectory = Str(req, "cwd") ?? Path.GetDirectoryName(path) ?? @"C:\",
            UseShellExecute = true
        };
        var p = Process.Start(psi) ?? throw new AgentException("process did not start");
        return Ok(new JsonObject { ["pid"] = p.Id, ["note"] = $"launched {Path.GetFileName(path)}" });
    }

    static string OpFront(JsonObject req)
    {
        var window = Str(req, "window") ?? throw new AgentException("front needs 'window'");
        var proc = FindWindowProcess(window) ?? throw new AgentException($"no visible window matching '{window}'");
        Front(proc.MainWindowHandle);
        return Ok(Note($"fronted [{proc.ProcessName}] {proc.MainWindowTitle}"));
    }

    static string OpWait(JsonObject req)
    {
        var window = Str(req, "window") ?? throw new AgentException("wait needs 'window'");
        var timeout = Int(req, "timeoutMs") ?? 30_000;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var p = FindWindowProcess(window);
            if (p is not null)
                return Ok(new JsonObject { ["process"] = p.ProcessName, ["title"] = p.MainWindowTitle, ["pid"] = p.Id });
            Thread.Sleep(300);
        }
        throw new AgentException($"no window matching '{window}' appeared within {timeout} ms");
    }

    static string OpSleep(JsonObject req)
    {
        var ms = Int(req, "ms") ?? 500;
        Thread.Sleep(Math.Clamp(ms, 0, 120_000));
        return Ok(Note($"slept {ms} ms"));
    }

    // ------------------------------------------------------------------ UIA helpers

    static JsonObject Note(string s) => new() { ["note"] = s };

    static AutomationElement RootFor(string? window)
    {
        if (window is null) return AutomationElement.RootElement;
        var proc = FindWindowProcess(window) ?? throw new AgentException($"no visible window matching '{window}'");
        var el = AutomationElement.FromHandle(proc.MainWindowHandle)
                 ?? throw new AgentException("that window has no automation element");
        return el;
    }

    static AutomationElement? Resolve(JsonObject req, bool required)
    {
        var r = Str(req, "ref");
        if (r is null)
        {
            if (required) throw new AgentException("this op needs 'ref'");
            return null;
        }
        if (!Refs.TryGetValue(r, out var el))
            throw new AgentException($"unknown ref '{r}' — run tree or find again (the agent may have restarted)");
        try { _ = el.Current.ControlType; }
        catch (ElementNotAvailableException) { throw new AgentException($"ref '{r}' is gone — that element no longer exists"); }
        return el;
    }

    static string Ref(AutomationElement el)
    {
        var key = "e" + (++_refSeq);
        Refs[key] = el;
        if (Refs.Count > 20_000) Refs.Clear();   // a long-lived agent must not grow without bound
        return key;
    }

    static IEnumerable<AutomationElement> Descendants(AutomationElement root, int cap)
    {
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var seen = 0;
        while (queue.Count > 0 && seen < cap)
        {
            var el = queue.Dequeue();
            seen++;
            yield return el;
            AutomationElementCollection kids;
            try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
            catch (Exception) { continue; }
            foreach (AutomationElement k in kids) queue.Enqueue(k);
        }
    }

    static bool IsInteractive(AutomationElement el, AutomationElement.AutomationElementInformation info)
    {
        var t = info.ControlType;
        if (t == ControlType.Button || t == ControlType.MenuItem || t == ControlType.CheckBox ||
            t == ControlType.RadioButton || t == ControlType.Edit || t == ControlType.ComboBox ||
            t == ControlType.List || t == ControlType.ListItem || t == ControlType.Tab ||
            t == ControlType.TabItem || t == ControlType.TreeItem || t == ControlType.Hyperlink ||
            t == ControlType.Window || t == ControlType.Table || t == ControlType.DataItem)
            return true;
        // A named Text element is how a WPF dialog usually says what it is asking, so it is worth
        // showing even though nothing can be done to it.
        return t == ControlType.Text && !string.IsNullOrWhiteSpace(info.Name);
    }

    static List<string> Patterns(AutomationElement el)
    {
        var found = new List<string>();
        foreach (var (p, n) in new (AutomationPattern, string)[]
                 {
                     (InvokePattern.Pattern, "Invoke"), (TogglePattern.Pattern, "Toggle"),
                     (ValuePattern.Pattern, "Value"), (SelectionItemPattern.Pattern, "SelectionItem"),
                     (ExpandCollapsePattern.Pattern, "ExpandCollapse"), (ScrollPattern.Pattern, "Scroll")
                 })
        {
            try { if (el.TryGetCurrentPattern(p, out _)) found.Add(n); }
            catch (Exception) { /* an element that vanished mid-probe simply has no patterns */ }
        }
        return found;
    }

    static bool TryPattern<T>(AutomationElement el, AutomationPattern pattern, Action<T> act) where T : BasePattern
    {
        try
        {
            if (!el.TryGetCurrentPattern(pattern, out var raw) || raw is not T typed) return false;
            act(typed);
            return true;
        }
        // ElementNotEnabledException derives from InvalidOperationException, so it has to be caught
        // first or a disabled control reads as "this pattern is not supported" — which would send the
        // caller looking for the wrong control entirely.
        catch (ElementNotEnabledException) { throw new AgentException("that element is disabled"); }
        catch (InvalidOperationException) { return false; }
    }

    static Point CentreOf(AutomationElement el)
    {
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty || double.IsInfinity(r.X) || r.Width <= 0 || r.Height <= 0)
            throw new AgentException("that element has no on-screen rectangle (it may be offscreen)");
        return new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
    }

    // ------------------------------------------------------------------ input

    static void ClickPoint(Point p, bool right = false, bool dbl = false)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(60);
        var down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        if (dbl)
        {
            Thread.Sleep(60);
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
        Thread.Sleep(120);
    }

    /// <summary>
    /// Types one character by its unicode value rather than its scan code, so it does not depend on
    /// the machine's keyboard layout. The test machine reports Belgian formatting; a scan-code path
    /// would put the wrong character on screen and the failure would look like a stuck key.
    /// </summary>
    static void TypeChar(char ch)
    {
        if (ch == '\n') { SendChord("ENTER"); return; }
        if (ch == '\t') { SendChord("TAB"); return; }
        var inputs = new[]
        {
            KeyInput(0, ch, KEYEVENTF_UNICODE),
            KeyInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(12);
    }

    static void SendChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = new List<ushort>();
        ushort main = 0;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods.Add(VK_CONTROL); break;
                case "ALT": mods.Add(VK_MENU); break;
                case "SHIFT": mods.Add(VK_SHIFT); break;
                case "WIN": mods.Add(VK_LWIN); break;
                default: main = VkFor(part); break;
            }
        }
        if (main == 0) throw new AgentException($"could not understand key '{chord}'");

        var seq = new List<INPUT>();
        foreach (var m in mods) seq.Add(KeyInput(m, '\0', 0));
        seq.Add(KeyInput(main, '\0', 0));
        seq.Add(KeyInput(main, '\0', KEYEVENTF_KEYUP));
        for (var i = mods.Count - 1; i >= 0; i--) seq.Add(KeyInput(mods[i], '\0', KEYEVENTF_KEYUP));
        var arr = seq.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
    }

    static ushort VkFor(string name) => name.ToUpperInvariant() switch
    {
        "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
        "SPACE" => 0x20, "BACKSPACE" or "BACK" => 0x08, "DELETE" or "DEL" => 0x2E,
        "UP" => 0x26, "DOWN" => 0x28, "LEFT" => 0x25, "RIGHT" => 0x27,
        "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
        "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74, "F6" => 0x75,
        "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        _ when name.Length == 1 && char.IsLetterOrDigit(name[0]) => (ushort)char.ToUpperInvariant(name[0]),
        _ => 0
    };

    static void Front(IntPtr h)
    {
        ShowWindow(h, SW_RESTORE);
        BringWindowToTop(h);
        // Tapping ALT releases Windows' foreground lock, which otherwise silently refuses the change
        // and leaves the capture showing whatever was in front before.
        keybd_event((byte)VK_MENU, 0, 0, IntPtr.Zero);
        keybd_event((byte)VK_MENU, 0, KEYEVENTF_KEYUP_LEGACY, IntPtr.Zero);
        SetForegroundWindow(h);
        Thread.Sleep(250);
    }

    static Process? FindWindowProcess(string match) =>
        Process.GetProcesses()
            .Where(p =>
            {
                try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0; }
                catch (Exception) { return false; }
            })
            .FirstOrDefault(p =>
                p.ProcessName.Contains(match, StringComparison.OrdinalIgnoreCase) ||
                p.MainWindowTitle.Contains(match, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ environment

    static bool CanDriveUi()
    {
        try
        {
            var b = VirtualScreen();
            return Environment.UserInteractive && b.Right - b.Left > 0 && DesktopName() is "Default" or "default";
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Attempts a one-pixel screen grab rather than reasoning about whether one would work.
    /// A disconnected RDP session has an interactive desktop and no rendering surface, and the only
    /// honest way to report that is to try it: BitBlt throws "The handle is invalid" there.
    /// </summary>
    static bool CanCapture()
    {
        try
        {
            using var probe = new Bitmap(1, 1);
            using var g = Graphics.FromImage(probe);
            g.CopyFromScreen(0, 0, 0, 0, probe.Size);
            return true;
        }
        catch (Exception) { return false; }
    }

    static string DesktopName()
    {
        try
        {
            var h = GetThreadDesktop(GetCurrentThreadId());
            var sb = new StringBuilder(128);
            return GetUserObjectInformation(h, UOI_NAME, sb, sb.Capacity, out _) ? sb.ToString() : "<unknown>";
        }
        catch (Exception) { return "<unknown>"; }
    }

    static string ScreenSize()
    {
        var b = VirtualScreen();
        return $"{b.Right - b.Left}x{b.Bottom - b.Top}";
    }

    static RECT VirtualScreen() => new()
    {
        Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
        Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
        Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
        Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)
    };

    /// <summary>Reports "black", "white" or null. A capture from a session with no desktop is
    /// uniform, and reads as a broken application unless something says otherwise.</summary>
    static string? UniformColour(Bitmap bmp)
    {
        var first = bmp.GetPixel(0, 0);
        for (var i = 0; i < 400; i++)
        {
            var x = i * 7919 % bmp.Width;
            var y = i * 6053 % bmp.Height;
            var c = bmp.GetPixel(x, y);
            if (c.R != first.R || c.G != first.G || c.B != first.B) return null;
        }
        return first.R < 24 && first.G < 24 && first.B < 24 ? "black"
             : first.R > 232 && first.G > 232 && first.B > 232 ? "white"
             : $"#{first.R:x2}{first.G:x2}{first.B:x2}";
    }

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        Console.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Root, "agent.log"), line + Environment.NewLine); }
        catch (Exception) { }
    }

    static string Clip(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= 70 ? s.ReplaceLineEndings(" ") : s.ReplaceLineEndings(" ")[..70] + "…";

    static string? Str(JsonObject o, string k) => o[k]?.GetValue<string>();
    static int? Int(JsonObject o, string k) => o[k] is { } n ? n.GetValue<int>() : null;
    static bool Bool(JsonObject o, string k) => o[k]?.GetValue<bool>() ?? false;

    // ------------------------------------------------------------------ interop

    const int SW_RESTORE = 9;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int UOI_NAME = 2;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    const uint KEYEVENTF_KEYUP_LEGACY = 0x0002;
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    static INPUT KeyInput(ushort vk, char ch, uint flags) => new()
    {
        type = 1,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = ch, dwFlags = flags } }
    };

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int len, out uint needed);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}
