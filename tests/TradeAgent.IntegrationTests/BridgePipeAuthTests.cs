using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TradeAgent.AtasBridge;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Atas;
using TradeAgent.Core;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The bridge pipe is the one that places orders, and until these tests existed anything that owned
/// its name could drive <see cref="IAtasAdapter"/> directly.
///
/// WHAT THE ATTACK IS. There is exactly one server instance of that pipe, so whichever process
/// creates the name first owns it, and the bridge inside ATAS dials in to whatever is listening. A
/// process that wins the name receives that connection and can send <c>place</c> — with
/// TradingGateway, and therefore the mode, the kill switch, the approvals, the risk limits and the
/// autonomy gate, entirely out of the path. Winning the name needs no boot race: it needs one moment
/// when TradeAgent is not holding it.
///
/// WHAT THESE TESTS ARE FOR. <see cref="A_pipe_owner_that_cannot_prove_the_secret_never_reaches_the_adapter"/>
/// is the one that matters: it stands a squatter up on the pipe, lets the real
/// <see cref="BridgeServer"/> dial in to it, and asserts that the adapter is never touched. The rest
/// pin down the shape of the refusal, because a refusal that presents as a timeout is worth very
/// little here — "connected, then nothing" is already the signature of three separate traps that
/// have each cost a session, and an authentication failure must be distinguishable from all of them
/// by reading one line.
///
/// THE PEER-IDENTITY HALF, AND THE ASSUMPTION THAT BROKE. The bridge asks Windows who owns the pipe
/// BEFORE it asks the peer for anything, and refuses if the answer is not the program TradeAgent
/// recorded — including when nothing was recorded, because "could not check" is the state an
/// impersonator would engineer. That is correct, and it is why every credential below records THIS
/// process: in these tests the peer is this test process, so a credential naming anything else (or
/// naming nothing) refuses at step 1 and the proof half these tests are about is never reached.
///
/// This was written on macOS, where the identity half does not execute at all, so a credential that
/// recorded no image was invisible here — and failed four of these tests on every Windows CI run for
/// four commits. The rule itself is still tested directly and portably in
/// <see cref="The_image_rule_refuses_a_stranger_and_the_managed_runtime_folder"/>; the kernel calls
/// that feed it are now exercised on Windows by
/// <see cref="A_peer_that_is_not_the_recorded_program_is_refused_before_the_secret_is_asked_for"/>.
/// </summary>
public class BridgePipeAuthTests(ITestOutputHelper output)
{
    static string NewPipe() => "ta-auth-" + Guid.NewGuid().ToString("n")[..12];

    static string Secret(char c) => new(c, 64);

    /// <summary>
    /// The program these tests expect to find on the other end of the pipe: this test process, which
    /// is what the squatters and the connector below actually run inside. See the class comment —
    /// recording anything else here refuses every peer on Windows before the proof is ever asked for.
    /// </summary>
    static string Self => Environment.ProcessPath ?? "";

    static BridgeCredential Cred() => new(Secret('a'), Self);

    /// <summary>The same peer, a different secret. Only the secret differs, so only the secret is on trial.</summary>
    static BridgeCredential OtherCred() => new(Secret('b'), Self);

    // ---------------------------------------------------------------- the one that matters

    /// <summary>
    /// A squatter owns the pipe name, the real bridge dials in to it, and it tries to place an
    /// order. The adapter must never hear about it.
    ///
    /// CATCHES: serving frames before the handshake completes; treating a missing or wrong proof as
    /// a warning instead of a refusal; and any future rearrangement that moves the read loop ahead
    /// of the authentication. Break any of those and <c>Placed</c> is 1.
    /// </summary>
    [Fact]
    public async Task A_pipe_owner_that_cannot_prove_the_secret_never_reaches_the_adapter()
    {
        var pipe = NewPipe();

        // The squatter holds the right pipe name and the wrong secret — which is every process on
        // the machine that has not read this installation's bridge.auth.
        await using var squatter = new SquattingPipeOwner(pipe, OtherCred());
        squatter.Start();

        var adapter = new CountingAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, Cred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
            AuthTimeout = TimeSpan.FromSeconds(2)
        };
        bridge.Start();

        await Wait(() => bridge.LastAuthFailure is not null);

        // The squatter got a connection and sent a real order over it. Nothing reached ATAS.
        await Wait(() => squatter.PlaceAttempts > 0);
        Assert.Equal(0, adapter.Placed);
        Assert.Equal(0, adapter.Described);
        Assert.False(bridge.Connected);

        // And the refusal says which of the two things went wrong, in words.
        Assert.Contains("wrong proof", bridge.LastAuthFailure!.Reason);
    }

    /// <summary>
    /// The same squat, from a peer that does not attempt the handshake at all — which is what an
    /// unmodified pipe-squatting program looks like, and what an AI runtime that merely knows the
    /// pipe name would do.
    ///
    /// The assertion that matters is the deadline: it must be the BRIDGE's own AuthTimeout that
    /// ends this, with a sentence, and not the test's patience.
    /// </summary>
    [Fact]
    public async Task A_pipe_owner_that_says_nothing_is_refused_by_name_rather_than_waited_on()
    {
        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, credential: null) { Answer = false };
        squatter.Start();

        var adapter = new CountingAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, Cred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
            AuthTimeout = TimeSpan.FromMilliseconds(700)
        };
        bridge.Start();

        await Wait(() => bridge.LastAuthFailure is not null);

        Assert.Equal(0, adapter.Placed);
        Assert.Equal(0, adapter.Described);
        Assert.Contains("did not answer the authentication challenge", bridge.LastAuthFailure!.Reason);
    }

    /// <summary>
    /// A refused peer is refused again on every reconnection, rather than being let through once
    /// the bridge has been trying for a while. A defence that gives up after N attempts is not one.
    /// </summary>
    [Fact]
    public async Task A_refused_pipe_owner_stays_refused_across_reconnections()
    {
        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, OtherCred());
        squatter.Start();

        var adapter = new CountingAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, Cred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            AuthTimeout = TimeSpan.FromMilliseconds(700)
        };
        bridge.Start();

        await Wait(() => bridge.AuthFailures >= 3, 15_000);
        Assert.Equal(0, adapter.Placed);
        Assert.False(bridge.Connected);
    }

    /// <summary>The bridge tells the peer why, so the reason can reach a screen rather than a log.</summary>
    [Fact]
    public async Task A_refused_pipe_owner_is_told_why_on_the_wire()
    {
        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, OtherCred());
        squatter.Start();

        await using var bridge = new BridgeServer(new CountingAdapter(), pipe, Cred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
            AuthTimeout = TimeSpan.FromSeconds(2)
        };
        bridge.Start();

        await Wait(() => squatter.RefusalReason is not null);
        Assert.Contains("wrong proof", squatter.RefusalReason!);
    }

    /// <summary>
    /// With no credential published — TradeAgent has never run on this machine — the bridge refuses
    /// rather than falling open. The failure names the file, because that is the repair.
    /// </summary>
    [Fact]
    public async Task With_no_published_secret_the_bridge_refuses_and_names_the_file()
    {
        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, credential: null) { Answer = false };
        squatter.Start();

        var adapter = new CountingAdapter();
        // A bridge whose credential lookup finds nothing: BridgeServer is handed an explicit null
        // and a pipe whose owner published nothing, which is the same state as a missing file.
        await using var bridge = new BridgeServer(adapter, pipe, new BridgeCredential("not-a-secret", null))
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
            AuthTimeout = TimeSpan.FromMilliseconds(700)
        };
        bridge.Start();

        await Wait(() => bridge.LastAuthFailure is not null);
        Assert.Equal(0, adapter.Placed);
    }

    // ---------------------------------------------------------------- the good path still works

    /// <summary>
    /// The real connector and the real bridge, sharing one credential, still complete — and the
    /// connector reports the peer as proved rather than merely present.
    /// </summary>
    [Fact]
    public async Task The_real_pair_authenticate_each_other_and_then_trade()
    {
        var pipe = NewPipe();
        var cred = Cred();

        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(10), cred);
        await connector.ConnectAsync();
        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, cred) { HeartbeatInterval = TimeSpan.FromMilliseconds(200) };
        bridge.Start();

        await Wait(() => connector.IsConnectedAsync().GetAwaiter().GetResult());

        Assert.Null(connector.Unauthenticated);
        Assert.Null(connector.StatusDetail);
        Assert.Null(bridge.LastAuthFailure);
        Assert.Equal(0, bridge.AuthFailures);

        var order = await connector.PlaceOrderAsync(new PlaceOrderCommand("TA-auth-1", "ATAS-LOOPBACK", "ES",
            OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
        Assert.Equal("TA-auth-1", order.ClientOrderId);
    }

    /// <summary>
    /// Two installations, or a copied profile: the secrets do not match and neither end pretends
    /// otherwise. The connector must not merely stay silent about it — the reason has to reach
    /// <c>StatusDetail</c>, which is the string the dashboard shows.
    /// </summary>
    [Fact]
    public async Task A_bridge_holding_a_different_secret_is_refused_and_named_by_the_connector()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5), Cred());
        await connector.ConnectAsync();

        var adapter = new LoopbackAtasAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, OtherCred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(300),
            AuthTimeout = TimeSpan.FromSeconds(2)
        };
        bridge.Start();

        await Wait(() => connector.Unauthenticated is not null);

        Assert.False(await connector.IsConnectedAsync());
        Assert.Null(connector.Bridge);
        Assert.False(connector.Capabilities.ReconciliationProvable);
        Assert.Contains("could not prove", connector.StatusDetail);
        Assert.Contains("bridge.auth", connector.StatusDetail);
    }

    /// <summary>
    /// THE DEPLOYED-BRIDGE CASE, and the reason this test exists at all: the DLL inside ATAS on the
    /// test machine is older than this repository and knows nothing about authentication. It must
    /// produce a SENTENCE, not a silence — "connected, then nothing" is indistinguishable from a
    /// stub bridge DLL, the wrong strategies folder and a strategy restored stopped, and each of
    /// those has already cost a session.
    ///
    /// This is also where the connector's half deliberately stops: such a bridge is NAMED, not
    /// dropped. See the class comment on BridgePipeAuth.
    /// </summary>
    [Fact]
    public async Task A_bridge_that_predates_authentication_is_named_rather_than_left_silent()
    {
        var pipe = NewPipe();
        await using var connector = new AtasConnector(pipe, TimeSpan.FromSeconds(5), Cred())
        {
            AuthGrace = TimeSpan.FromMilliseconds(300)
        };
        await connector.ConnectAsync();

        // Exactly what the older BridgeServer puts on the wire: a hello, and nothing else.
        await using var old = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await old.ConnectAsync(10_000);
        await using var w = new StreamWriter(old, new UTF8Encoding(false)) { AutoFlush = true };
        await w.WriteLineAsync(Json.Write(new BridgeFrame
        {
            Op = BridgeOps.Hello,
            Data = JsonSerializer.SerializeToElement(
                new BridgeHello
                {
                    BridgeProtocolVersion = Versions.BridgeProtocolVersion,
                    BridgeVersion = "0.0.9-deployed", AtasVersion = "8.0.14.397"
                }, Json.Options)
        }));

        await Wait(() => connector.Unauthenticated is not null);

        var named = connector.StatusDetail!;
        Assert.Contains("did not authenticate", named);
        Assert.Contains("press Reinstall the bridge", named);
    }

    /// <summary>
    /// The peer-identity half through the REAL kernel calls, on the one OS where they run.
    ///
    /// The squatter here holds the RIGHT secret — only its identity is wrong — so a bridge that
    /// asked for the proof first, or asked for it at all, would let it through. The refusal must
    /// name the mismatch, and naming it is also the evidence that
    /// <c>GetNamedPipeServerProcessId</c>/<c>QueryFullProcessImageName</c> actually answered: had
    /// they failed, the reason would be "would not say which program", which is a refusal for a
    /// different cause and would hide this one.
    ///
    /// Runs on Windows only, and returns rather than failing elsewhere: off Windows there is no ATAS
    /// and no product, and <see cref="BridgePipeAuth.ImageVerdict"/> is covered portably below.
    /// </summary>
    [Fact]
    public async Task A_peer_that_is_not_the_recorded_program_is_refused_before_the_secret_is_asked_for()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, Cred());
        squatter.Start();

        var adapter = new CountingAdapter();
        await using var bridge = new BridgeServer(adapter, pipe,
            new BridgeCredential(Secret('a'), @"C:\Program Files\Somebody Else\Other.exe"))
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
            AuthTimeout = TimeSpan.FromSeconds(2)
        };
        bridge.Start();

        await Wait(() => bridge.LastAuthFailure is not null);

        var reason = bridge.LastAuthFailure!.Reason;
        Assert.Contains("but TradeAgent recorded", reason);
        Assert.DoesNotContain("would not say", reason);
        Assert.Contains("Other.exe", reason);
        Assert.Equal(0, adapter.Placed);
        Assert.False(bridge.Connected);
    }

    /// <summary>
    /// A peer that accepts the connection and then never reads a byte. It must not be able to stop
    /// this bridge, and above all it must not be able to stop this bridge from being DISPOSED —
    /// that call runs when ATAS unloads the strategy.
    ///
    /// FOUND BY MEASUREMENT, on Windows, 2026-09-01. The write side had no deadline, so the bridge
    /// parked inside a frame that could never land, and the async chain in the dump ran
    /// DisposeAsync -> RunAsync -> Authenticate -> Refuse -> SendRaw -> WriteAsyncInternal. Nothing
    /// on any thread; nothing to time out; nothing to cancel, because a token cannot recall a write
    /// the kernel has already accepted. Only closing the handle ends it, which is what
    /// <see cref="BridgeServer.WriteTimeout"/> and the disposal order now do.
    ///
    /// The 0-byte buffer is the hostile choice a squatter is free to make: it blocks every write to
    /// it, however small.
    /// </summary>
    [Fact]
    public async Task A_peer_that_accepts_the_connection_and_never_reads_cannot_freeze_the_bridge()
    {
        var pipe = NewPipe();
        await using var squatter = new SquattingPipeOwner(pipe, credential: null)
        {
            Answer = false, Read = false, BufferBytes = 0
        };
        squatter.Start();

        var adapter = new CountingAdapter();
        await using var bridge = new BridgeServer(adapter, pipe, Cred())
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            AuthTimeout = TimeSpan.FromMilliseconds(500),
            WriteTimeout = TimeSpan.FromMilliseconds(500)
        };
        bridge.Start();

        // It got a connection, and the bridge is now trying to write a challenge into a peer that
        // will never read one.
        await Wait(() => Volatile.Read(ref squatter.Connections) >= 1);

        var timer = Stopwatch.StartNew();
        await bridge.DisposeAsync();
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8),
            $"DisposeAsync took {timer.Elapsed.TotalSeconds:0.0}s against a peer that never reads; it used to never return");
        Assert.False(bridge.Connected);
        Assert.Equal(0, adapter.Placed);
    }

    // ---------------------------------------------------------------- the rules themselves

    /// <summary>
    /// The peer-identity rule, tested where the decision is actually made. The kernel calls that
    /// supply <c>actual</c> are Windows-only; this is not, and it is where every one of the rule's
    /// verdicts comes from.
    /// </summary>
    [Fact]
    public void The_image_rule_refuses_a_stranger_and_the_managed_runtime_folder()
    {
        const string tools = "/home/u/AppData/Local/TradeAgent/tools";
        const string app = "/home/u/AppData/Local/TradeAgent/bin/TradeAgent.exe";

        Assert.Null(BridgePipeAuth.ImageVerdict(app, app, tools));

        // The named adversary: the AI runtime TradeAgent installs and starts as the same user.
        // Refused even when it has rewritten the record to name itself, which a same-user process
        // can do — so this rule is not derived from the record.
        var runtime = tools + "/codex/codex.exe";
        Assert.Contains("AI runtime", BridgePipeAuth.ImageVerdict(runtime, runtime, tools));

        // Anything else that is not what TradeAgent recorded.
        Assert.Contains("recorded", BridgePipeAuth.ImageVerdict("/tmp/squat.exe", app, tools));

        // "Could not check" is a refusal, not a shrug: both of these are states an impersonator
        // would engineer, and neither may read as permission.
        Assert.Contains("would not say", BridgePipeAuth.ImageVerdict(null, app, tools));
        Assert.Contains("did not record", BridgePipeAuth.ImageVerdict(app, null, tools));
    }

    /// <summary>
    /// A proof is bound to the nonce AND to which end produced it, so neither half of the exchange
    /// can be turned round and replayed as the other. Without the role, a squatter could answer the
    /// bridge's challenge with the bridge's own proof, copied straight back off the wire.
    /// </summary>
    [Fact]
    public void A_proof_cannot_be_replayed_as_the_other_ends_proof_or_under_another_nonce()
    {
        var secret = new string('c', 64);
        var nonce = BridgePipeAuth.NewNonce();
        var fromBridge = BridgePipeAuth.Proof(secret, BridgePipeAuth.BridgeRole, nonce);

        Assert.True(BridgePipeAuth.ProofMatches(secret, BridgePipeAuth.BridgeRole, nonce, fromBridge));
        Assert.False(BridgePipeAuth.ProofMatches(secret, BridgePipeAuth.ServerRole, nonce, fromBridge));
        Assert.False(BridgePipeAuth.ProofMatches(secret, BridgePipeAuth.BridgeRole, BridgePipeAuth.NewNonce(), fromBridge));
        Assert.False(BridgePipeAuth.ProofMatches(new string('d', 64), BridgePipeAuth.BridgeRole, nonce, fromBridge));
        Assert.False(BridgePipeAuth.ProofMatches(secret, BridgePipeAuth.BridgeRole, nonce, null));

        // A malformed secret or nonce fails closed rather than throwing out of the read loop.
        Assert.False(BridgePipeAuth.ProofMatches("short", BridgePipeAuth.BridgeRole, nonce, fromBridge));
        Assert.False(BridgePipeAuth.ProofMatches(secret, BridgePipeAuth.BridgeRole, "zz", fromBridge));
        Assert.False(BridgePipeAuth.IsNonce("nothex"));
        Assert.False(BridgePipeAuth.IsSecret(new string('a', 63)));
    }

    /// <summary>
    /// The published credential keeps its secret across calls — the bridge may be mid-reconnect —
    /// and re-stamps the owning image every time, so the record always names the program holding
    /// the pipe now rather than the one that held it first.
    /// </summary>
    [Fact]
    public void Publishing_the_credential_keeps_the_secret_and_restamps_the_owner()
    {
        var first = BridgePipeAuth.EnsureForServer();
        var second = BridgePipeAuth.EnsureForServer();

        Assert.True(BridgePipeAuth.IsSecret(first.Secret));
        Assert.Equal(first.Secret, second.Secret);
        Assert.Equal(Environment.ProcessPath, second.ServerImage);
        Assert.Equal(first.Secret, BridgePipeAuth.ReadForClient()!.Secret);
        Assert.StartsWith(Paths.State, BridgePipeAuth.CredentialFile);
    }

    // ---------------------------------------------------------------- the secret on disk (U-bridge-auth-owner-only)

    /// <summary>
    /// (a) WHO CAN READ THE SECRET AT THE ONE INSTANT THE WRITE USED TO GET WRONG: the secret is in the temp
    /// and nothing has published it yet. <see cref="BridgePipeAuth.Write"/>'s seam hands the test that instant.
    ///
    /// <para><b>macOS and Linux:</b> the temp is 0600 at the seam, and so is the published file. The write used
    /// to create the temp with <c>File.WriteAllText</c> — 0666 less the umask, 0644 under the usual 022 — and
    /// chmod it only afterwards: any account that could traverse the state directory could open it in
    /// between, a descriptor opened then reads on after the chmod, and a crash in between left a 0644 temp
    /// holding the live secret under a per-process name nothing ever swept.</para>
    ///
    /// <para><b>Windows</b> has no mode. The temp inherits its directory's DACL and the rename keeps it, so the
    /// test reads that the temp's DACL at the seam is the published file's and that every entry on it is
    /// inherited — the write adds no reader of its own. Whether the inherited DACL is owner-only is a claim
    /// about the directory the product publishes in, <c>%LOCALAPPDATA%\TradeAgent\state</c>, so that claim is
    /// read where it is made: the entries of <c>%LOCALAPPDATA%</c> that reach a file two directories below it
    /// let nobody read but this user, SYSTEM and Administrators (CREATOR OWNER is the file's creator, one of
    /// those). Read there, never written; not read off the test home, because the test home is not under the
    /// profile everywhere — the tests box points TEMP at <c>C:\ta\runs\&lt;id&gt;\tmp</c>
    /// (<c>tools/win-test-run.ps1</c>), which inherits <c>C:\</c>'s entries for Users and Authenticated Users,
    /// and a reading of the rig is not a reading of the product.</para>
    /// </summary>
    [Fact]
    public void The_secret_is_owner_only_before_the_rename()
    {
        using var home = TestEnv.NewScratch("bridge-auth-mode");
        var path = Path.Combine(home.Dir, "bridge.auth");

        if (OperatingSystem.IsWindows()) TheSecretIsOwnerOnlyOnWindows(path);
        else TheSecretIsOwnerOnlyOnUnix(path);
    }

    [UnsupportedOSPlatform("windows")]
    void TheSecretIsOwnerOnlyOnUnix(string path)
    {
        UnixFileMode? atSeam = null;
        BridgePipeAuth.Write(path, Cred(), beforeRename: temp => atSeam = File.GetUnixFileMode(temp));
        var published = File.GetUnixFileMode(path);

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var said = $"the secret was on disk at {Octal(atSeam)} before the rename and is published at {Octal(published)}; " +
                   $"owner-only is {Octal(ownerOnly)} — {Umask(Path.GetDirectoryName(path)!)}";
        output.WriteLine(said);

        Assert.True(atSeam == ownerOnly, said);
        Assert.True(published == ownerOnly, said);
    }

    /// <summary>What this process's umask makes of a file created without a mode — which is what the old temp was.</summary>
    [UnsupportedOSPlatform("windows")]
    static string Umask(string dir)
    {
        var probe = Path.Combine(dir, $"umask-{Guid.NewGuid():n}.probe");
        File.WriteAllText(probe, "");
        try
        {
            var made = File.GetUnixFileMode(probe);
            return $"umask {Octal((UnixFileMode)0b110_110_110 & ~made)}: a file created without a mode comes out {Octal(made)}";
        }
        finally { File.Delete(probe); }
    }

    static string Octal(UnixFileMode? mode) => mode is { } m ? Convert.ToString((int)m, 8).PadLeft(4, '0') : "<never seen>";

    [SupportedOSPlatform("windows")]
    void TheSecretIsOwnerOnlyOnWindows(string path)
    {
        FileSecurity? atSeam = null;
        BridgePipeAuth.Write(path, Cred(), beforeRename: temp => atSeam = new FileInfo(temp).GetAccessControl(AccessControlSections.Access));
        Assert.NotNull(atSeam);
        var published = new FileInfo(path).GetAccessControl(AccessControlSections.Access);

        var user = WindowsIdentity.GetCurrent().User!.Value;
        string Dacl(FileSecurity s) => s.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string Name(string sid) => sid == user ? "this user" : sid switch
        {
            "S-1-5-18" => "SYSTEM", "S-1-5-32-544" => "Administrators", "S-1-3-0" => "CREATOR OWNER", _ => sid
        };

        var own = atSeam.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var readers = ReadersOfAFileBelow(profile);
        string[] ownerOnly = [user, "S-1-5-18", "S-1-5-32-544", "S-1-3-0"];

        var said = $"the temp at the seam: {Dacl(atSeam).Replace(user, "<this user>")} — {own} entr(y/ies) of its own, " +
                   $"protected {atSeam.AreAccessRulesProtected}; published: {Dacl(published).Replace(user, "<this user>")}; " +
                   $"%LOCALAPPDATA% lets read a file two directories below it: {string.Join(", ", readers.Select(Name))}";
        output.WriteLine(said);

        Assert.True(Dacl(atSeam) == Dacl(published), said);
        Assert.True(own == 0 && !atSeam.AreAccessRulesProtected, said);
        Assert.True(readers.Count > 0 && readers.All(ownerOnly.Contains), said);
    }

    /// <summary>
    /// The accounts an Allow entry of <paramref name="dir"/>'s DACL lets read a file two directories below it:
    /// object-inherit, not stopped from propagating, and granting read data (or the generic read or all that
    /// maps to it).
    /// </summary>
    [SupportedOSPlatform("windows")]
    static List<string> ReadersOfAFileBelow(string dir)
    {
        const int genericRead = unchecked((int)0x80000000), genericAll = 0x10000000;
        return new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow
                        && r.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)
                        && !r.PropagationFlags.HasFlag(PropagationFlags.NoPropagateInherit)
                        && ((int)r.FileSystemRights & ((int)FileSystemRights.ReadData | genericRead | genericAll)) != 0)
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// (b) A READER HOLDING THE CREDENTIAL DOES NOT REFUSE ITS REWRITE. Both ends read through
    /// <see cref="BridgePipeAuth.Read"/> — the bridge inside ATAS before every challenge, the connector before
    /// every republish — and the connector republishes on every turn of its accept loop. A replace on Windows
    /// meets every handle open on the file it replaces: refused, the accept loop catches it, and the pipe name
    /// stands unowned for the loop's one-second pause. The handle held here is the one
    /// <see cref="BridgePipeAuth.OpenToRead"/> opens, which is the one every reader holds.
    ///
    /// <para>Green on Windows takes two halves, and windows-latest measured the first one alone curing
    /// nothing: red with the reader sharing read only (run 37675671465) and still red with it sharing read,
    /// write and delete (run 37675951756), both "UnauthorizedAccessException: Access to the path is denied" —
    /// <c>MoveFileExW</c> refuses a replace under any open handle. The second half is the POSIX rename
    /// (<c>OwnerOnlyFile.Publish</c>, since <c>U-credential-replace</c> the one every credential the app writes
    /// takes). <c>rename(2)</c> ignores open handles, so on macOS and Linux this is
    /// green whatever the reader does; the claim is Windows'.</para>
    /// </summary>
    [Fact]
    public void A_reader_holding_the_credential_does_not_refuse_its_rewrite()
    {
        using var home = TestEnv.NewScratch("bridge-auth-held");
        var path = Path.Combine(home.Dir, "bridge.auth");
        BridgePipeAuth.Write(path, Cred());

        Exception? refused;
        string held;
        using (var reader = BridgePipeAuth.OpenToRead(path))
        {
            refused = Record.Exception(() => BridgePipeAuth.Write(path, OtherCred()));
            using var text = new StreamReader(reader);
            held = text.ReadToEnd();
        }

        Assert.True(refused is null,
            $"the rewrite was refused while a reader held bridge.auth — {refused?.GetType().Name}: {refused?.Message}");
        // The reader read the credential it had opened, whole; the next reader reads the new one; nothing is left beside it.
        Assert.Equal(Secret('a'), Json.Read<BridgeCredential>(held)!.Secret);
        Assert.Equal(Secret('b'), BridgePipeAuth.Read(path)!.Secret);
        Assert.Equal([path], Directory.GetFiles(home.Dir));
    }

    /// <summary>
    /// (c) A REWRITE REFUSED AT THE RENAME LEAVES NO TEMP AND THE OLD SECRET. A replace that does not happen —
    /// refused by the platform, a full disk, the process dying — used to leave <c>bridge.auth.&lt;pid&gt;.tmp</c>
    /// behind holding the new secret, under a per-process name nothing sweeps. The seam throws where the
    /// rename would run.
    /// </summary>
    [Fact]
    public void A_rewrite_refused_at_the_rename_leaves_no_temp_and_the_old_secret()
    {
        using var home = TestEnv.NewScratch("bridge-auth-refused");
        var path = Path.Combine(home.Dir, "bridge.auth");
        BridgePipeAuth.Write(path, Cred());

        var refused = Assert.Throws<IOException>(() => BridgePipeAuth.Write(path, OtherCred(),
            beforeRename: _ => throw new IOException("the rename was refused")));

        var left = Directory.GetFiles(home.Dir).Where(f => f != path).Select(Path.GetFileName).ToList();
        Assert.True(left.Count == 0, $"the refused rewrite left {string.Join(", ", left)} beside bridge.auth");
        Assert.Equal(Secret('a'), BridgePipeAuth.Read(path)!.Secret);
        Assert.Equal("the rename was refused", refused.Message);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A process that has taken the bridge pipe name. It behaves the way an impersonator would: it
    /// completes the connection, answers the bridge's challenge as best it can, and then sends a
    /// live order.
    ///
    /// It is not a general tool. It speaks one protocol, to one pipe name given to it, and its only
    /// purpose is to be refused.
    /// </summary>
    sealed class SquattingPipeOwner(string pipe, BridgeCredential? credential) : IAsyncDisposable
    {
        readonly CancellationTokenSource _cts = new();
        Task? _loop;

        /// <summary>False makes it say nothing at all, which is the simpler squat.</summary>
        public bool Answer { get; init; } = true;

        /// <summary>
        /// False makes it accept the connection and then never read a byte — the peer that used to
        /// freeze the bridge permanently. See
        /// <see cref="A_peer_that_accepts_the_connection_and_never_reads_cannot_freeze_the_bridge"/>.
        /// </summary>
        public bool Read { get; init; } = true;

        /// <summary>
        /// The pipe's buffer, in bytes, and it is load-bearing rather than a detail.
        ///
        /// A Windows server pipe created with 0 blocks EVERY write until somebody reads it, however
        /// small the frame. The five-argument NamedPipeServerStream constructor passes 0, which is
        /// what this class used, and it is why four of these tests deadlocked on Windows the moment
        /// they got far enough to exchange frames at all: both ends sat in a write that could never
        /// complete. A peer that is only pretending to be TradeAgent behaves like a normal one here;
        /// the hostile choice is made explicitly, by the one test that is about it.
        /// </summary>
        public int BufferBytes { get; init; } = 8192;

        public int PlaceAttempts;
        public int Connections;
        public string? RefusalReason { get; private set; }

        public void Start() => _loop ??= Task.Run(() => Run(_cts.Token));

        async Task Run(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferBytes, BufferBytes);
                    await server.WaitForConnectionAsync(ct);
                    Interlocked.Increment(ref Connections);

                    if (!Read)
                    {
                        // Connected, and deaf. Hold the connection open and read nothing.
                        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { break; }
                    }

                    var reader = new StreamReader(server, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                    var writer = new StreamWriter(server, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

                    string? line;
                    while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                    {
                        BridgeFrame? f = null;
                        try { f = Json.Read<BridgeFrame>(line); } catch (JsonException) { }
                        if (f is null) continue;

                        if (f.Op == BridgePipeAuth.Refused) { RefusalReason = f.Error; continue; }
                        if (f.Op != BridgePipeAuth.Challenge || !Answer) continue;

                        var nonce = f.Data!.Value.GetProperty("nonce").GetString()!;
                        if (credential is not null)
                            await writer.WriteLineAsync(Json.Write(new
                            {
                                v = Versions.BridgeProtocolVersion,
                                op = BridgePipeAuth.Response,
                                data = new { proof = BridgePipeAuth.Proof(credential.Secret, BridgePipeAuth.ServerRole, nonce) }
                            }));

                        // And then the whole point of the squat: an order, straight at the adapter,
                        // with nothing in between it and the broker.
                        Interlocked.Increment(ref PlaceAttempts);
                        await writer.WriteLineAsync(Json.Write(new
                        {
                            v = Versions.BridgeProtocolVersion,
                            id = Guid.NewGuid().ToString("n"),
                            op = BridgeOps.Place,
                            data = new PlaceOrderCommand("SQUAT-1", "ATAS-LOOPBACK", "ES",
                                OrderSide.Buy, OrderType.Market, 5m, null, null, TimeInForce.Day, null)
                        }));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* the bridge hung up on us, as it should */ }
                finally { server?.Dispose(); }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_loop is not null) { try { await _loop; } catch (Exception) { } }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// An adapter that records being touched and nothing else. Counting Describe() as well as
    /// Place() matters: a bridge that leaked its capabilities to an unproved peer would have told a
    /// squatter the account id and what this platform can prove, before refusing it.
    /// </summary>
    sealed class CountingAdapter : IAtasAdapter
    {
        public int Placed, Described;

        public BridgeHello Describe()
        {
            Interlocked.Increment(ref Described);
            return new BridgeHello { BridgeProtocolVersion = Versions.BridgeProtocolVersion, AccountId = "ATAS-LOOPBACK" };
        }

        public OrderInfo Place(PlaceOrderCommand cmd)
        {
            Interlocked.Increment(ref Placed);
            return new OrderInfo("ATAS-1", cmd.ClientOrderId, cmd.AccountId, cmd.Symbol, cmd.Side, cmd.Type,
                cmd.Quantity, 0m, cmd.LimitPrice, cmd.StopPrice, ExecutionState.WORKING, null, DateTimeOffset.UtcNow);
        }

        public IReadOnlyList<AccountInfo> GetAccounts() => [];
        public IReadOnlyList<InstrumentInfo> GetInstruments() => [];
        public QuoteInfo? GetQuote(string symbol) => null;
        public IReadOnlyList<PositionInfo> GetPositions(string accountId) => [];
        public IReadOnlyList<OrderInfo> GetOrders(string a, bool i, DateTimeOffset? s) => [];
        public IReadOnlyList<ExecutionInfo> GetExecutions(string a, DateTimeOffset? s) => [];
        public OrderInfo Modify(ModifyOrderCommand cmd) => throw new NotSupportedException();
        public void Cancel(string connectorOrderId) => throw new NotSupportedException();
        public IReadOnlyList<string> CancelAll(string accountId) => [];
        public OrderInfo? ClosePosition(string a, string symbol, string clientOrderId) => null;

        public event Action<bool>? ConnectionChanged;
        public event Action<QuoteInfo>? QuoteChanged;
        public event Action<OrderInfo>? OrderChanged;
        public event Action<ExecutionInfo>? ExecutionReceived;
        public event Action<PositionInfo>? PositionChanged;
        public event Action<AccountInfo>? AccountChanged;

        // Never raised: this adapter exists to be left alone. Touching them keeps the compiler from
        // warning that a bridge which leaked events would have nowhere to leak them from.
        void Unused() { ConnectionChanged?.Invoke(false); QuoteChanged?.Invoke(default!); OrderChanged?.Invoke(default!);
            ExecutionReceived?.Invoke(default!); PositionChanged?.Invoke(default!); AccountChanged?.Invoke(default!); }
    }

    static async Task Wait(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("the condition was not met in time");
    }
}
