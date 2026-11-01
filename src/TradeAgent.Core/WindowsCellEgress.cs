using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TradeAgent.Core;

/// <summary>One outbound block rule the egress helper places: its name, the slot container it names, its protocol and the remote ports it blocks.</summary>
public sealed record EgressRule(string Name, int Slot, string Sid, int Protocol, string RemotePorts);

/// <summary>What the owner's one press came to: the rule placed, Windows' prompt declined, or a failure in words.</summary>
public enum EgressAsk { Placed, Declined, Failed }

/// <summary>
/// THE ONE FIXED, MINIMAL ELEVATED HELPER (<c>U-cell-windows-egress</c> item 1; R25 D2, the owner's "Web (443) for every executor"). A cell of
/// class <c>web</c> holds <c>internetClient</c>, which opens EVERY outbound port; port 443 alone is this helper's work: for each of the
/// <see cref="WindowsCellSlots.Count"/> slot containers of the app's home, one Windows Firewall rule blocking every outbound TCP port but 443
/// and one blocking every UDP datagram (names still resolve: the system's DNS client asks on the cell's behalf, outside it — measured).
///
/// <para><b>Fixed behaviour, no input.</b> The app re-launches ITS OWN program elevated with the one argument <see cref="Verb"/>, through
/// Windows' "runas" — the owner's press, then Windows' own prompt, his "Yes". The elevated process reads nothing an agent can reach: no other
/// argument (anything else is refused before a rule is touched), no environment variable (the home is the account's default one,
/// <see cref="AccountHome.Default"/>, computed — nothing is created, <see cref="Paths"/> is never touched), no file. It places exactly the rule set <see cref="RulesFor"/> names —
/// block rules only, which can only take reach away — reads them back, and exits. Idempotent: a rule already exactly right is left alone;
/// a rule of that name that is not is replaced. It never removes a rule it did not name and never adds an allow rule.</para>
///
/// <para><b>Declined or failed</b>, nothing is placed and the cells run at egress <c>none</c> — said on the Organisation page. Whether the rule
/// HOLDS is never this helper's word: the benchmark measures every slot under it and only then records <c>web</c>.</para>
/// </summary>
public static class WindowsCellEgress
{
    /// <summary>The one argument the elevated helper answers to. The app's own program, started with exactly this and nothing else.</summary>
    public const string Verb = "--tradeagent-cell-web-rule";

    /// <summary>The firewall group every rule carries, as Windows' firewall console shows it.</summary>
    public const string Group = "TradeAgent cells";

    /// <summary>Every outbound TCP port but 443.</summary>
    public const string BlockedTcp = "1-442,444-65535";

    /// <summary>Exit codes of the elevated helper: placed and read back · refused (an argument it does not answer to) · not elevated · failed.</summary>
    public const int Done = 0, Refused = 2, NotElevated = 3, FailedCode = 4;

    const int Tcp = 6, Udp = 17;

    /// <summary>Whether <paramref name="args"/> name the helper at all — the app's entry then runs <see cref="RunHelper"/> and never its window.</summary>
    public static bool Named(IReadOnlyList<string> args) => args.Any(a => a.Trim().Equals(Verb, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when <paramref name="args"/> is exactly <see cref="Verb"/> alone, else why the helper refuses them. Nothing else is accepted.</summary>
    public static string? Refusal(IReadOnlyList<string> args) =>
        args.Count == 1 && string.Equals(args[0], Verb, StringComparison.Ordinal)
            ? null
            : $"the cell web helper takes exactly one argument, {Verb}, and was given {args.Count}: it does nothing else";

    /// <summary>
    /// THE ELEVATED HELPER'S WHOLE BODY: refuse anything but <see cref="Verb"/> alone, refuse when not elevated, place the rule set for the
    /// account's default home and read it back. Returns an exit code (<see cref="Done"/> only when every rule reads back exactly).
    /// </summary>
    public static int RunHelper(IReadOnlyList<string> args)
    {
        if (Refusal(args) is not null) return Refused;
        if (!OperatingSystem.IsWindows()) return Refused;
        if (!Elevated()) return NotElevated;
        try
        {
            var rules = RulesFor(AccountHome.Default());
            Place(rules);
            return Missing(rules).Count == 0 ? Done : FailedCode;
        }
        catch (Exception ex) when (ex is TradeAgentException or COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return FailedCode;
        }
    }

    /// <summary>Whether this process runs elevated (a member of Administrators in its token).</summary>
    [SupportedOSPlatform("windows")]
    public static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// The rule set for the app home <paramref name="home"/>: for each slot, a TCP rule blocking <see cref="BlockedTcp"/> and a UDP rule
    /// blocking every port, both outbound, both naming that slot's container SID. Named by the home's profile prefix, so two homes never
    /// replace each other's rules.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<EgressRule> RulesFor(string home) =>
    [
        .. Enumerable.Range(1, WindowsCellSlots.Count).SelectMany(slot =>
        {
            var profile = WindowsCellStage.ProfileNameFor(home, slot);
            var sid = AppContainerCell.SidOf(profile);
            return new[]
            {
                new EgressRule($"{profile} web: TCP 443 only", slot, sid, Tcp, BlockedTcp),
                new EgressRule($"{profile} web: no UDP", slot, sid, Udp, "*")
            };
        })
    ];

    /// <summary>
    /// PLACES <paramref name="rules"/> (elevated): each one already exactly right is left alone; any rule of its name that is not is removed
    /// and the rule added. Throws when Windows refuses.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Place(IReadOnlyList<EgressRule> rules)
    {
        var policy = Policy();
        var set = policy.Rules;
        foreach (var rule in rules)
        {
            if (Read(set, rule.Name) is { } present && Matches(present, rule)) continue;
            RemoveNamed(set, rule.Name);
            dynamic r = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            r.Name = rule.Name;
            r.Description = "TradeAgent: an AI cell reaches the web on TCP 443 only. Placed once, with your permission.";
            r.Grouping = Group;
            r.Direction = DirectionOut;
            r.Action = ActionBlock;
            r.Protocol = rule.Protocol;
            if (rule.RemotePorts != "*") r.RemotePorts = rule.RemotePorts;
            r.LocalAppPackageId = rule.Sid;
            r.Profiles = AllProfiles;
            r.InterfaceTypes = "All";
            r.Enabled = true;
            set.Add(r);
        }
    }

    /// <summary>The names of the rules of <paramref name="rules"/> that are absent or not exactly as placed. Readable without elevation.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Missing(IReadOnlyList<EgressRule> rules)
    {
        var set = Policy().Rules;
        return [.. rules.Where(r => Read(set, r.Name) is not { } present || !Matches(present, r)).Select(r => r.Name)];
    }

    /// <summary>Removes every rule named in <paramref name="rules"/> (elevated) — a test's cleanup on CI's disposable runner; the product never removes.</summary>
    [SupportedOSPlatform("windows")]
    internal static void RemoveForTests(IReadOnlyList<EgressRule> rules)
    {
        var set = Policy().Rules;
        foreach (var r in rules) RemoveNamed(set, r.Name);
    }

    /// <summary>
    /// THE OWNER'S PRESS, AFTER IT (in the app, not elevated): starts <paramref name="program"/> — the app's own program — elevated with
    /// <see cref="Verb"/>, which shows Windows' permission prompt, and waits for it. Declined at the prompt → <see cref="EgressAsk.Declined"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static (EgressAsk Outcome, string Why) Request(string? program)
    {
        if (program is null || !string.Equals(Path.GetFileName(program), "TradeAgent.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(program))
            return (EgressAsk.Failed, "this copy of TradeAgent was not started from its own program, so it cannot ask Windows for the rule");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, Verb) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is null) return (EgressAsk.Failed, "Windows started nothing");
            if (!p.WaitForExit(120_000)) return (EgressAsk.Failed, "the helper did not finish within two minutes");
            return p.ExitCode switch
            {
                Done => (EgressAsk.Placed, ""),
                NotElevated => (EgressAsk.Failed, "the helper did not receive Windows' permission"),
                Refused => (EgressAsk.Failed, "the helper refused how it was started"),
                var code => (EgressAsk.Failed, $"Windows refused to set the rule (the helper ended with {code})")
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return (EgressAsk.Declined, ""); }
        catch (Win32Exception ex) { return (EgressAsk.Failed, ex.Message); }
    }

    const int ErrorCancelled = 1223, DirectionOut = 2, ActionBlock = 0, AllProfiles = 0x7FFFFFFF;

    [SupportedOSPlatform("windows")]
    static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    static dynamic? Read(dynamic set, string name)
    {
        try { return set.Item(name); }
        catch (COMException) { return null; }
    }

    static void RemoveNamed(dynamic set, string name)
    {
        for (var i = 0; i < 64 && Read(set, name) is not null; i++) set.Remove(name);
    }

    /// <summary>
    /// Whether a present rule is exactly the one placed — and narrows nothing: outbound block, the protocol, the remote ports, the container,
    /// every profile, every interface, enabled, and no address, local port, program or service condition that would make it block less.
    /// </summary>
    static bool Matches(dynamic present, EgressRule rule)
    {
        static string S(object? o) => (o as string ?? "").Replace(" ", "", StringComparison.Ordinal);
        return (int)present.Direction == DirectionOut && (int)present.Action == ActionBlock && (int)present.Protocol == rule.Protocol
            && (bool)present.Enabled && ((int)present.Profiles & AllProfiles) == AllProfiles
            && string.Equals(S(present.LocalAppPackageId), rule.Sid, StringComparison.OrdinalIgnoreCase)
            && S(present.RemotePorts) == rule.RemotePorts
            && S(present.RemoteAddresses) == "*" && S(present.LocalAddresses) == "*" && S(present.LocalPorts) == "*"
            && string.Equals(S(present.InterfaceTypes), "All", StringComparison.OrdinalIgnoreCase)
            && S(present.ApplicationName).Length == 0 && S(present.ServiceName).Length == 0;
    }
}
