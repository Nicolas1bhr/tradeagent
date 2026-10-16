using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.AgentRuntime;
using TradeAgent.App;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Diagnostics;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-SEAT-SIGNIN: THE OWNER SIGNS THE APP'S SEAT HOME IN FROM THE DASHBOARD, ANY TIME, WITH NO TERMINAL — the browser, a one-time
/// code or a pasted key, each run under the seat home with no window; a two-press sign-out; a Doctor row (CRED-001, the no-terminal
/// rule; <c>docs/ORCHESTRATION.md</c> § 2.7).
///
/// <para>CI has no codex, so the program is a script that answers like codex-cli 0.160.1 did on 2026-10-11 (`login status` exit 0/1,
/// `login --with-api-key` reading stdin, `logout`, the device prompt of 2026-10-09) and writes its whole environment, per start, to a
/// file — so where each start ran is read, not assumed. Presses are made on the card the dashboard builds, on one thread; work the
/// card runs off it comes back through a queue this rig drains, as <see cref="PerceptionCardTests"/> does.</para>
///
/// <para>In the vendor-file collection: one test sets the process-wide <c>CODEX_HOME</c>, which a neighbour must not read meanwhile.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class SeatSignInTests
{
    const string FakeKey = "sk-FAKE-0000-seat-signin";

    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>
    /// (a) AN OWNER WHO FINISHED ONBOARDING AND WHOSE SEAT HOME HOLDS NO CREDENTIAL SEES THE SIGN-IN CARD ON THE DASHBOARD — and it
    /// goes when status turns 0. Red at base: only onboarding asked the status; the dashboard had no card and the owner met
    /// U-seat-home's "Sign in again." with no button behind it.
    /// </summary>
    [Fact]
    public async Task An_onboarded_owner_with_a_signed_out_seat_home_sees_the_sign_in_card()
    {
        using var rig = new Rig(nameof(An_onboarded_owner_with_a_signed_out_seat_home_sees_the_sign_in_card));
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            foreach (var step in Enum.GetValues<OnboardingStep>()) host.Onboarding.Complete(step);
            Assert.True(host.Onboarding.IsComplete());
            host.SeatSignIn = rig.Service;

            // ONE THREAD FOR THE PAGE, which owns its controls: every wait below is made on it.
            var page = new DashboardPage(host, () => Task.CompletedTask);
            Rig.OnThisThread(rig.Service.RefreshAsync());
            page.Update(Rig.OnThisThread(host.Gateway.StatusAsync()), []);

            var card = page.SignIn;
            Assert.Contains(card.Root, page.Root.GetLogicalDescendants());
            Assert.True(card.Root.IsVisible, "a signed-out seat home after onboarding must show the sign-in card");
            var words = Rig.Words(card.Root);
            Assert.Contains("TradeAgent now keeps its own AI sign-in, separate from your own Codex. Sign in again.", words);
            Assert.Contains(SeatSignInCard.Browser, words);
            Assert.Contains(SeatSignInCard.Code, words);
            Assert.Contains(SeatSignInCard.Key, words);

            // SIGNED IN — here by the script's own marker, as a sign-in finished elsewhere would leave it — the card goes.
            File.WriteAllText(rig.Marker, "");
            Rig.OnThisThread(rig.Service.RefreshAsync());
            page.Update(Rig.OnThisThread(host.Gateway.StatusAsync()), []);
            Assert.False(card.Root.IsVisible);
            Assert.True(card.SignedIn.IsVisible);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// (b) EVERY SIGN-IN PATH RUNS UNDER THE SEAT HOME, NEVER THE OWNER'S — the status, the browser, the code, the key and the sign-out,
    /// each pressed on the card with the owner's own <c>CODEX_HOME</c> set in the app's environment. Red at base: no card and no code,
    /// key-from-the-dashboard or sign-out path existed. THE WATCHED MUTANT drops the seat-home <c>Apply</c> on the code path ⇒ red here.
    /// </summary>
    [Fact]
    public void Every_sign_in_path_runs_under_the_seat_home_never_the_owners_home()
    {
        using var rig = new Rig(nameof(Every_sign_in_path_runs_under_the_seat_home_never_the_owners_home));
        rig.Device(hold: true);
        var owners = Path.Combine(TestEnv.Home, $"the-owners-codex-home-{Guid.NewGuid():n}");
        var inherited = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", owners);
        try
        {
            rig.Refresh();
            Assert.True(rig.Card.Root.IsVisible);

            rig.Press(rig.Button(SeatSignInCard.Browser));
            Assert.Equal(["https://auth.example.test/oauth/authorize?x=1"], rig.Opened);

            rig.Press(rig.Button(SeatSignInCard.Code));
            Assert.Contains("ABC1-DE23F", Rig.Words(rig.Card.Root));

            rig.KeyField.Text = FakeKey;
            rig.Press(rig.Button(SeatSignInCard.Key));
            Assert.True(rig.Card.SignedIn.IsVisible, string.Join(" | ", Rig.Words(rig.Card.Root)));

            rig.Press(rig.Card.SignOutButton);
            rig.Press(rig.Card.SignOutButton);
            Assert.True(rig.Card.Root.IsVisible);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", inherited);
            rig.Service.Dispose();
        }

        var starts = rig.Starts();
        foreach (var path in new[] { "login status", "login", "login --device-auth", "login --with-api-key", "logout" })
            Assert.Contains(starts, s => s.Args == path);
        var seat = rig.Codex.Seat!.Root;
        foreach (var (args, env) in starts.Where(s => s.Args != "--version"))
        {
            Assert.True(Path.Combine(seat, "codex") == env.GetValueOrDefault("CODEX_HOME"),
                $"`{args}` ran with CODEX_HOME={env.GetValueOrDefault("CODEX_HOME")}, not the seat home");
            Assert.True(Path.Combine(seat, "home") == env.GetValueOrDefault("HOME"),
                $"`{args}` ran with HOME={env.GetValueOrDefault("HOME")}, not the seat home");
            Assert.DoesNotContain(env.Values, v => v.Contains(owners, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// (c) A PASTED KEY REACHES THE PROGRAM'S STDIN AND IS NEVER LOGGED, SHOWN BACK OR KEPT: not in its argv or environment, not in
    /// the activity log or any word on the card, not in a field of the card or the service; the box is empty after the press — and
    /// a program that refuses by echoing the key has it replaced in the words shown. Red at base: no card took a key.
    /// </summary>
    [Fact]
    public void A_pasted_key_reaches_stdin_and_is_never_logged_or_kept()
    {
        using var rig = new Rig(nameof(A_pasted_key_reaches_stdin_and_is_never_logged_or_kept));
        rig.Refresh();

        // REFUSED FIRST, by a program that prints the key back.
        File.WriteAllText(rig.Refuse, "");
        rig.KeyField.Text = FakeKey;
        rig.Press(rig.Button(SeatSignInCard.Key));
        Assert.Equal("", rig.KeyField.Text ?? "");
        Assert.Contains(Rig.Words(rig.Card.Root), w => w.Contains("(the key)", StringComparison.Ordinal));
        Assert.True(rig.Card.Root.IsVisible);

        File.Delete(rig.Refuse);
        rig.KeyField.Text = "  " + FakeKey + "  ";
        rig.Press(rig.Button(SeatSignInCard.Key));
        Assert.Equal("", rig.KeyField.Text ?? "");
        Assert.True(rig.Card.SignedIn.IsVisible);

        Assert.Equal(FakeKey, File.ReadAllText(rig.Stdin).Trim());
        Assert.DoesNotContain(FakeKey, File.ReadAllText(rig.ArgvFile), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, File.ReadAllText(rig.EnvFile), StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Activity, a => a.Contains(FakeKey, StringComparison.Ordinal));
        Assert.Contains(rig.Activity, a => a.Contains("pasted key", StringComparison.Ordinal));
        Assert.DoesNotContain(Rig.Words(rig.Card.Root).Concat(Rig.Words(rig.Card.SignedIn)),
            w => w.Contains(FakeKey, StringComparison.Ordinal));
        foreach (var holder in new object[] { rig.Card, rig.Service })
            foreach (var f in holder.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Assert.False(f.GetValue(holder) is string s && s.Contains(FakeKey, StringComparison.Ordinal),
                    $"{holder.GetType().Name}.{f.Name} kept the key");
    }

    /// <summary>
    /// FIX 1 (CRED-001; the review's P1): NO PIECE OF A PASTED KEY COMES BACK IN ANY WORD — a key copied over two lines, or echoed by
    /// the program only in part. A key with a space or a line break inside is refused in words before anything runs (a key has none);
    /// the program's words are cleared of the key BEFORE they are cut to one line, and of every run of eight characters of it. Read in
    /// the refusal itself, every word on the card and every activity line. Red at base: the words were cut to their first line and
    /// only then cleared of the exact key, so the first line of a wrapped key, or the key less its first four characters, came back.
    /// </summary>
    [Fact]
    public void A_pasted_key_wrapped_over_lines_or_echoed_in_part_leaves_no_piece_of_itself_in_any_word()
    {
        using var rig = new Rig(nameof(A_pasted_key_wrapped_over_lines_or_echoed_in_part_leaves_no_piece_of_itself_in_any_word));
        rig.Refresh();
        const string wrapped = "sk-FAKE-1111-first-half\r\nsecond-half-2222-wrapped";

        // A KEY COPIED OVER TWO LINES, into a program that prints back what it read.
        File.WriteAllText(rig.Refuse, "");
        rig.KeyField.Text = wrapped;
        rig.Press(rig.Button(SeatSignInCard.Key));
        Assert.Equal("", rig.KeyField.Text ?? "");
        NoPieceOf(wrapped, Rig.Words(rig.Card.Root), "the card");
        foreach (var broken in new[] { wrapped, "sk-FAKE-1111-first-half second-half-2222-wrapped" })
            NoPieceOf(broken, [Refusal(rig, broken)], "the refusal");
        Assert.Contains(Rig.Words(rig.Card.Root), w => w.Contains("line break", StringComparison.Ordinal));
        Assert.DoesNotContain("login --with-api-key", rig.Argv());

        // A PROGRAM THAT ECHOES THE KEY ONLY IN PART: all but its first four characters.
        File.Delete(rig.Refuse);
        File.WriteAllText(rig.RefusePart, "");
        rig.KeyField.Text = FakeKey;
        rig.Press(rig.Button(SeatSignInCard.Key));
        NoPieceOf(FakeKey, Rig.Words(rig.Card.Root), "the card");
        NoPieceOf(FakeKey, [Refusal(rig, FakeKey)], "the refusal");
        Assert.Contains(Rig.Words(rig.Card.Root), w => w.Contains("(the key)", StringComparison.Ordinal));
        Assert.Contains("login --with-api-key", rig.Argv());

        NoPieceOf(wrapped + " " + FakeKey, rig.Activity, "the activity log");
        Assert.True(rig.Card.Root.IsVisible);
    }

    /// <summary>The words a key sign-in was refused in, called as the card calls it.</summary>
    static string Refusal(Rig rig, string key) =>
        Assert.IsType<TradeAgentException>(Assert.Throws<AggregateException>(() => rig.Service.SignInWithKeyAsync(key).Wait()).InnerException).Message;

    /// <summary>No run of eight characters of any whitespace-separated piece of what was pasted, in any of the words.</summary>
    static void NoPieceOf(string pasted, IEnumerable<string> words, string where)
    {
        var pieces = pasted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
            foreach (var p in pieces)
                for (var i = 0; i + 8 <= p.Length; i++)
                    Assert.False(w.Contains(p.Substring(i, 8), StringComparison.Ordinal), $"{where} carries \"{p.Substring(i, 8)}\" of the key: {w}");
    }

    /// <summary>
    /// FIX 2 (the review's P2): ONE SIGN-IN AT A TIME — a key sign-in stops a browser sign-in still waiting, so the login left running
    /// cannot later overwrite the credential the owner just chose. Red at base: the key path never stopped the pending login, which
    /// kept waiting (and SigningIn kept answering true) after the key was in.
    /// </summary>
    [Fact]
    public void A_key_sign_in_stops_a_browser_sign_in_still_waiting()
    {
        using var rig = new Rig(nameof(A_key_sign_in_stops_a_browser_sign_in_still_waiting));
        rig.Refresh();
        rig.Press(rig.Button(SeatSignInCard.Browser));
        Assert.True(rig.Service.SigningIn, "the browser sign-in should be waiting for its callback");
        int? pid = OperatingSystem.IsWindows() ? null : int.Parse(File.ReadAllText(rig.BrowserPid).Trim());

        rig.KeyField.Text = FakeKey;
        rig.Press(rig.Button(SeatSignInCard.Key));
        Assert.True(rig.Card.SignedIn.IsVisible, string.Join(" | ", Rig.Words(rig.Card.Root)));
        Assert.False(rig.Service.SigningIn, "the browser sign-in is still waiting after the key was in");
        if (pid is { } id) Assert.True(Gone(id), $"the browser sign-in's process {id} is still running after the key was in");
    }

    /// <summary>Whether a process has ended, given ten seconds.</summary>
    static bool Gone(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); if (p.HasExited) return true; }
            catch (ArgumentException) { return true; }
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// (d) THE CODE IS SHOWN WITH ITS EXPIRY AND A RETRY ONCE IT LAPSES, AND OUTPUT THE MANIFEST'S PATTERNS DO NOT MATCH IS REFUSED
    /// NAMING THE VERSION — no code guessed onto the screen. Red at base: no code path on the card, no expiry read, and no refusal.
    /// </summary>
    [Fact]
    public void The_device_code_shows_its_expiry_and_retry_and_an_unmatched_output_refuses_naming_the_version()
    {
        using var rig = new Rig(nameof(The_device_code_shows_its_expiry_and_retry_and_an_unmatched_output_refuses_naming_the_version));
        rig.Device(hold: true);
        rig.Refresh();

        rig.Press(rig.Button(SeatSignInCard.Code));
        var words = Rig.Words(rig.Card.Root);
        Assert.Contains("https://auth.example.test/codex/device", words);
        Assert.Contains("ABC1-DE23F", words);
        var expires = (rig.Clock + TimeSpan.FromMinutes(15)).ToLocalTime().ToString("HH:mm");
        Assert.Contains($"This code expires at {expires}.", words);
        Assert.False(rig.Button(SeatSignInCard.Retry).IsVisible, "a Retry while the code is live would end the sign-in being typed");

        // LAPSED: the card says so and offers a new code; the new one replaces the old.
        rig.Clock += TimeSpan.FromMinutes(16);
        rig.Card.Update();
        Assert.Contains($"This code expired at {expires}. Get a new one to try again.", Rig.Words(rig.Card.Root));
        Assert.True(rig.Button(SeatSignInCard.Retry).IsVisible);
        rig.Device(hold: true, code: "XYZ9-QR77S");
        rig.Press(rig.Button(SeatSignInCard.Retry));
        Assert.Contains("XYZ9-QR77S", Rig.Words(rig.Card.Root));
        Assert.DoesNotContain("ABC1-DE23F", Rig.Words(rig.Card.Root));

        // A SHAPE THIS BUILD DOES NOT KNOW: a code of three and two, and no expiry. Refused, naming the version; nothing shown.
        File.WriteAllText(rig.DeviceFile, "1. Open https://auth.example.test/codex/device\n2. Enter this one-time code\n   abc-12\n");
        File.Delete(rig.Hold);
        rig.Clock += TimeSpan.FromMinutes(16);
        rig.Card.Update();
        rig.Press(rig.Button(SeatSignInCard.Retry));
        words = Rig.Words(rig.Card.Root);
        Assert.Contains(words, w => w.Contains("OpenAI Codex CLI 0.160.1", StringComparison.Ordinal) && w.Contains("will not guess", StringComparison.Ordinal));
        Assert.DoesNotContain("abc-12", words);
        Assert.DoesNotContain("XYZ9-QR77S", words);
        var refused = Assert.Throws<AggregateException>(() => rig.Service.BeginDeviceCodeAsync().Wait()).InnerException as TradeAgentException;
        Assert.NotNull(refused);
        Assert.Contains("0.160.1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("how long the code lasts", refused.Message, StringComparison.Ordinal);
        rig.Service.Dispose();
    }

    /// <summary>
    /// (e) SIGNING OUT NEEDS TWO PRESSES (CLAUDE.md: what removes permission asks twice): the first only arms it and runs nothing; the
    /// second runs <c>logout</c> under the seat home and the card comes back. Red at base: no sign-out existed.
    /// </summary>
    [Fact]
    public void Sign_out_needs_two_presses()
    {
        using var rig = new Rig(nameof(Sign_out_needs_two_presses));
        File.WriteAllText(rig.Marker, "");
        rig.Refresh();
        Assert.True(rig.Card.SignedIn.IsVisible);
        Assert.False(rig.Card.Root.IsVisible);

        var pressing = rig.Card.Pressing;
        Press(rig.Card.SignOutButton);
        Assert.Same(pressing, rig.Card.Pressing);
        Assert.True(Ui.IsArmed(rig.Card.SignOutButton));
        Assert.Equal(SeatSignInCard.SignOutArmed, rig.Card.SignOutButton.Content);
        Assert.DoesNotContain("logout", rig.Argv());
        Assert.True(File.Exists(rig.Marker));

        rig.Press(rig.Card.SignOutButton);
        Assert.Equal("logout", rig.Argv().Last(a => a != "login status"));
        Assert.False(File.Exists(rig.Marker));
        Assert.True(rig.Card.Root.IsVisible);
        Assert.False(rig.Card.SignedIn.IsVisible);
        Assert.Contains(rig.Activity, a => a.Contains("two presses", StringComparison.Ordinal));
    }

    /// <summary>
    /// (f) THE CARD IS UPDATED IN PLACE, NOT REBUILT ON REFRESH (CLAUDE.md): through signed out, signed in, unknown and back, every
    /// control is the one built first; a half-typed key and a half-pressed sign-out survive the tick. Red at base: no card.
    /// </summary>
    [Fact]
    public void The_card_is_updated_in_place_not_rebuilt_on_refresh()
    {
        using var rig = new Rig(nameof(The_card_is_updated_in_place_not_rebuilt_on_refresh));
        rig.Refresh();
        var built = rig.Card.Root.GetLogicalDescendants().Concat(rig.Card.SignedIn.GetLogicalDescendants()).ToList();
        var child = rig.Card.Root.Child;

        rig.KeyField.Text = "half-typed";
        for (var i = 0; i < 3; i++) rig.Card.Update();
        Assert.Equal("half-typed", rig.KeyField.Text);

        File.WriteAllText(rig.Marker, "");
        rig.Refresh();
        Assert.True(rig.Card.SignedIn.IsVisible);
        Press(rig.Card.SignOutButton);
        for (var i = 0; i < 3; i++) rig.Card.Update();
        Assert.True(Ui.IsArmed(rig.Card.SignOutButton), "the five-second tick disarmed a half-pressed sign-out");

        // UNKNOWN: the program is no longer the pin, so status is not run and the card says it cannot tell.
        rig.Codex.Install.ExecutableSha256 = new string('0', 64);
        rig.Refresh();
        Assert.True(rig.Card.Root.IsVisible);
        Assert.Contains(Rig.Words(rig.Card.Root), w => w.StartsWith("TradeAgent could not tell whether its AI is signed in", StringComparison.Ordinal));

        rig.Codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(rig.Exe);
        File.Delete(rig.Marker);
        rig.Refresh();

        Assert.Same(child, rig.Card.Root.Child);
        Assert.Equal(built, rig.Card.Root.GetLogicalDescendants().Concat(rig.Card.SignedIn.GetLogicalDescendants()).ToList());
        Assert.Equal("half-typed", rig.KeyField.Text);
    }

    /// <summary>
    /// ITEM 1: THE STATE IS THE STATUS COMMAND UNDER THE SEAT HOME — signed out (exit 1), signed in (exit 0) — and UNKNOWN, with the
    /// reason, when status could not be run: a program that is not the pin (never started), or none at all.
    /// </summary>
    [Fact]
    public async Task The_sign_in_state_is_read_by_status_under_the_seat_home_and_unknown_when_status_cannot_run()
    {
        using var rig = new Rig(nameof(The_sign_in_state_is_read_by_status_under_the_seat_home_and_unknown_when_status_cannot_run));
        Assert.Equal(SeatSignInState.NotApplicable, rig.Service.State().State);

        Assert.Equal(SeatSignInState.SignedOut, (await rig.Service.RefreshAsync()).State);
        File.WriteAllText(rig.Marker, "");
        Assert.Equal(SeatSignInState.SignedIn, (await rig.Service.RefreshAsync()).State);
        Assert.Equal(SeatSignInState.SignedIn, rig.Service.State().State);
        Assert.All(rig.Starts().Where(s => s.Args == "login status"),
            s => Assert.Equal(Path.Combine(rig.Codex.Seat!.Root, "codex"), s.Env.GetValueOrDefault("CODEX_HOME")));

        var before = rig.Argv().Length;
        rig.Codex.Install.ExecutableSha256 = new string('0', 64);
        var notThePin = await rig.Service.RefreshAsync();
        Assert.Equal(SeatSignInState.Unknown, notThePin.State);
        Assert.Contains(new string('0', 64), notThePin.Reason, StringComparison.Ordinal);
        Assert.Equal(before, rig.Argv().Length);

        rig.Codex.Executable = Path.Combine(rig.Dir, "no-such-codex");
        rig.Codex.Install.ExecutableSha256 = null;
        var none = await rig.Service.RefreshAsync();
        Assert.Equal(SeatSignInState.Unknown, none.State);
        Assert.Contains("not on this computer", none.Reason, StringComparison.Ordinal);

        // A RUNTIME WITH NO SEAT HOME has nothing for the card to say.
        Assert.Equal(SeatSignInState.NotApplicable,
            (await new SeatSignIn(() => RuntimeCatalog.BuiltIn().Single(m => m.Id == "opencode")).RefreshAsync()).State);
    }

    /// <summary>
    /// ITEM 4: THE DOCTOR'S "AI seat home" ROW — the path, signed in or not, the pinned version and the runtime-profile hash a launch
    /// records — and nothing of the credential: the status command's own words, which name the key's prefix, are not on it.
    /// </summary>
    [Fact]
    public async Task The_doctor_row_names_the_seat_home_its_sign_in_pin_and_profile_hash_and_no_credential()
    {
        using var rig = new Rig(nameof(The_doctor_row_names_the_seat_home_its_sign_in_pin_and_profile_hash_and_no_credential));
        var probe = new SeatHomeTests.FakeProbe();
        var row = await Doctor.SeatHomeCheck(rig.Codex, rig.Exe, probe);
        var hash = SeatProfile.ProfileHash(rig.Codex, rig.Exe, SeatProfile.ConfigBytes(rig.Codex.Seat!), probe.Reading);
        Assert.Equal(Doctor.SeatHomeRow, row.Name);
        Assert.Equal(HealthState.READY, row.State);
        Assert.Equal($"{rig.Codex.Seat!.Root} · signed in · OpenAI Codex CLI 0.160.1 pinned · profile {hash}", row.Detail);
        Assert.DoesNotContain("sk-", row.Detail, StringComparison.Ordinal);

        var signedOut = await Doctor.SeatHomeCheck(rig.Codex, rig.Exe, new SeatHomeTests.FakeProbe(signedIn: false));
        Assert.Equal(HealthState.DEGRADED, signedOut.State);
        Assert.Contains(" · not signed in · ", signedOut.Detail, StringComparison.Ordinal);
        Assert.Contains("AI sign-in card", signedOut.UserAction, StringComparison.Ordinal);

        rig.Codex.Install.ExecutableSha256 = new string('0', 64);
        var notThePin = await Doctor.SeatHomeCheck(rig.Codex, rig.Exe, probe);
        Assert.Equal(HealthState.DEGRADED, notThePin.State);
        Assert.Contains("not checked", notThePin.Detail, StringComparison.Ordinal);
        Assert.Equal(1, probe.Reads);
    }

    // ---- the rig ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE SHIPPED CODEX MANIFEST over a script and a seat home of this test's own — the environment, the deny set and the sign-in
    /// commands are the build's own data; only the program, its pin and the home's root change, and the browser sign-in is the
    /// desktop's on every platform so the card offers all three ways in wherever CI runs it. The card is built over the service as
    /// the dashboard builds it, with a queue for the dispatcher and a clock the test moves.
    /// </summary>
    sealed class Rig : IDisposable
    {
        readonly ConcurrentQueue<Action> _posted = new();

        public Rig(string name)
        {
            Dir = Path.Combine(TestEnv.Home, "signin", $"{name}-{Guid.NewGuid():n}"[..40]);
            Directory.CreateDirectory(Dir);
            Exe = WriteScript();
            Codex = RuntimeCatalog.BuiltIn().Single(m => m.Id == "codex");
            Codex.Executable = Exe;
            Codex.Install.ExecutableSha256 = SeatProfile.Sha256Of(Exe);
            Codex.Seat!.Home = Path.Combine(Dir, "seat-home");
            Codex.AuthArgs = ["login"];
            Codex.AuthCodePattern = null;
            Service = new SeatSignIn(() => Codex, new AgentPresence());
            Card = new SeatSignInCard(new SeatSignInSources
            {
                Service = Service,
                Open = Opened.Add,
                Activity = Activity.Add,
                Ui = _posted.Enqueue,
                Now = () => Clock
            });
        }

        public string Dir { get; }
        public string Exe { get; }
        public RuntimeManifest Codex { get; }
        public SeatSignIn Service { get; }
        public SeatSignInCard Card { get; }
        public List<string> Opened { get; } = [];
        public List<string> Activity { get; } = [];
        public DateTimeOffset Clock { get; set; } = new(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);

        public string Marker => Path.Combine(Dir, "signed-in");
        public string Refuse => Path.Combine(Dir, "refuse");
        public string RefusePart => Path.Combine(Dir, "refuse-part");
        public string BrowserPid => Path.Combine(Dir, "browser.pid");
        public string Stdin => Path.Combine(Dir, "stdin.txt");
        public string DeviceFile => Path.Combine(Dir, "device.txt");
        public string Hold => Path.Combine(Dir, "hold");
        public string ArgvFile => Path.Combine(Dir, "argv.txt");
        public string EnvFile => Path.Combine(Dir, "env.txt");

        public TextBox KeyField => Card.Root.GetLogicalDescendants().OfType<TextBox>().Single();

        public Button Button(string label) =>
            Card.Root.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == label);

        /// <summary>The device prompt as codex-cli 0.160.1 printed it on 2026-10-09, with its colour codes; the code is a stand-in.</summary>
        public void Device(bool hold, string code = "ABC1-DE23F")
        {
            File.WriteAllText(DeviceFile,
                "Welcome to Codex [v\u001b[90m0.160.1\u001b[0m]\n\n1. Open this link in your browser and sign in to your account\n" +
                "   \u001b[94mhttps://auth.example.test/codex/device\u001b[0m\n\n" +
                "2. Enter this one-time code \u001b[90m(expires in 15 minutes)\u001b[0m\n" +
                $"   \u001b[94m{code}\u001b[0m\n");
            if (hold) File.WriteAllText(Hold, ""); else File.Delete(Hold);
        }

        /// <summary>A press, its work waited for, and what it handed back run on this thread.</summary>
        public void Press(Button b)
        {
            var before = Card.Pressing;
            SeatSignInTests.Press(b);
            if (!ReferenceEquals(before, Card.Pressing)) Settle();
        }

        public void Settle()
        {
            Assert.True(Card.Pressing?.Wait(TimeSpan.FromSeconds(60)) ?? true, "the press never finished");
            while (_posted.TryDequeue(out var work)) work();
        }

        /// <summary>A task waited for ON THIS THREAD, which owns the controls the next line touches.</summary>
        public static T OnThisThread<T>(Task<T> task) => task.GetAwaiter().GetResult();

        /// <summary>A status read, waited for ON THIS THREAD — the card belongs to it — and the card's pass.</summary>
        public void Refresh()
        {
            Service.RefreshAsync().GetAwaiter().GetResult();
            Card.Update();
        }

        public string[] Argv() =>
            File.Exists(ArgvFile) ? [.. File.ReadAllLines(ArgvFile).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];

        /// <summary>Every start the script saw: its arguments and its whole environment.</summary>
        public List<(string Args, Dictionary<string, string> Env)> Starts()
        {
            var starts = new List<(string, Dictionary<string, string>)>();
            if (!File.Exists(EnvFile)) return starts;
            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            string? args = null;
            Dictionary<string, string> env = new(cmp);
            foreach (var line in File.ReadAllLines(EnvFile))
            {
                if (line.StartsWith("== ", StringComparison.Ordinal) || line == "==")
                {
                    if (args is not null) starts.Add((args, env));
                    args = line.Length > 3 ? line[3..].Trim() : "";
                    env = new(cmp);
                    continue;
                }
                var eq = line.IndexOf('=');
                if (eq > 0) env[line[..eq]] = line[(eq + 1)..];
            }
            if (args is not null) starts.Add((args, env));
            return starts;
        }

        /// <summary>Every word on a control: each text, button label, placeholder and typed text.</summary>
        public static List<string> Words(Control root)
        {
            var words = new List<string>();
            foreach (var node in root.GetLogicalDescendants().Prepend(root))
            {
                if (node is Control { IsVisible: false }) continue;
                if (node is Control c && c.GetLogicalAncestors().OfType<Control>().Any(a => !a.IsVisible)) continue;
                switch (node)
                {
                    case TextBlock t when t.Text is { Length: > 0 } text: words.Add(text); break;
                    case Button b when b.Content is string label: words.Add(label); break;
                    case TextBox box:
                        if (box.PlaceholderText is { Length: > 0 } placeholder) words.Add(placeholder);
                        if (box.Text is { Length: > 0 } typed) words.Add(typed);
                        break;
                }
            }
            return words;
        }

        /// <summary>
        /// A PROGRAM THAT ANSWERS LIKE codex-cli 0.160.1, on either platform: status by a marker file, a key read from stdin (and
        /// refused, printing it back, while a "refuse" file exists — or all but its first four characters, while a "refuse-part" one does), the device prompt from a file (held open while a "hold" file
        /// exists), the browser sign-in's address on stderr then a wait (its process id written first, off Windows), and logout. Every start appends "== args" and its environment.
        /// </summary>
        string WriteScript()
        {
            if (OperatingSystem.IsWindows())
            {
                var bat = Path.Combine(Dir, "codex.cmd");
                File.WriteAllText(bat, string.Join("\r\n",
                    "@echo off",
                    $"(echo == %*& set) >> \"{EnvFile}\"",
                    $"echo %*>> \"{ArgvFile}\"",
                    "if \"%1\"==\"--version\" goto version",
                    "if \"%1\"==\"logout\" goto logout",
                    "if not \"%1\"==\"login\" exit /b 2",
                    "if \"%2\"==\"status\" goto status",
                    "if \"%2\"==\"--with-api-key\" goto key",
                    "if \"%2\"==\"--device-auth\" goto device",
                    "goto browser",
                    ":version",
                    "echo codex-cli 0.160.1",
                    "exit /b 0",
                    ":status",
                    $"if exist \"{Marker}\" (echo Logged in using an API key - *** 1>&2& exit /b 0)",
                    "echo Not logged in 1>&2",
                    "exit /b 1",
                    ":key",
                    "set /p KEY=",
                    $"> \"{Stdin}\" echo %KEY%",
                    $"if exist \"{Refuse}\" (echo refused: %KEY% 1>&2& exit /b 1)",
                    $"if exist \"{RefusePart}\" (echo the key ending %KEY:~4% is not valid 1>&2& exit /b 1)",
                    $"type nul > \"{Marker}\"",
                    "echo Successfully logged in 1>&2",
                    "exit /b 0",
                    ":device",
                    $"type \"{DeviceFile}\"",
                    $"if exist \"{Hold}\" ping -n 31 127.0.0.1 >nul",
                    "exit /b 0",
                    ":browser",
                    "echo https://auth.example.test/oauth/authorize?x=1 1>&2",
                    "ping -n 31 127.0.0.1 >nul",
                    "exit /b 0",
                    ":logout",
                    $"del /q \"{Marker}\" 2>nul",
                    "echo Successfully logged out 1>&2",
                    "exit /b 0",
                    ""));
                return bat;
            }
            var sh = Path.Combine(Dir, "codex");
            File.WriteAllText(sh, string.Join("\n",
                "#!/bin/sh",
                $"{{ echo \"== $*\"; env; }} >> \"{EnvFile}\"",
                $"echo \"$*\" >> \"{ArgvFile}\"",
                "case \"$*\" in",
                "  --version) echo 'codex-cli 0.160.1' ;;",
                $"  'login status') if [ -f \"{Marker}\" ]; then echo 'Logged in using an API key - ***' >&2; exit 0; fi; echo 'Not logged in' >&2; exit 1 ;;",
                $"  'login --with-api-key') read -r KEY; printf '%s\\n' \"$KEY\" > \"{Stdin}\"; if [ -f \"{Refuse}\" ]; then echo \"refused: $KEY\" >&2; exit 1; fi; if [ -f \"{RefusePart}\" ]; then echo \"the key ending ${{KEY#????}} is not valid\" >&2; exit 1; fi; : > \"{Marker}\"; echo 'Successfully logged in' >&2 ;;",
                $"  'login --device-auth') cat \"{DeviceFile}\"; if [ -f \"{Hold}\" ]; then sleep 30; fi ;;",
                $"  login) echo $$ > \"{BrowserPid}\"; echo 'https://auth.example.test/oauth/authorize?x=1' >&2; exec sleep 30 ;;",
                $"  logout) rm -f \"{Marker}\"; echo 'Successfully logged out' >&2 ;;",
                "  *) exit 2 ;;",
                "esac",
                ""));
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return sh;
        }

        public void Dispose()
        {
            Service.Dispose();
            TestEnv.DeleteQuietly(Dir);
        }
    }
}
