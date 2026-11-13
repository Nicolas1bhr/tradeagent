using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE FREE-SPACE GUARD (<c>U-disk-guard</c>; R30 row 10). Real agents run unattended on the owner's server beside his other services, and code in
/// a cell can write as fast as the disk takes it. The floors and the watch are read here with no disk; the pause is read on a real puppet turn
/// through the host's own path, because "paused" means the running tree ended, the launch closed and the wake went back.
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class DiskGuardTests
{
    const string Chair = CouncilRoles.Operations;

    const long GiB = 1L << 30;

    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // Enough on a 100 GiB disk, and too little.
    static readonly DiskSpace Plenty = new(60 * GiB, 100 * GiB), Low = new(5 * GiB, 100 * GiB);

    // ---- the floor ---------------------------------------------------------------------------------------------------------

    /// <summary>A small laptop's floor is its 10 %; a large server's stops at 10 GiB; nothing goes under the 2 GiB the Doctor asks for.</summary>
    [Theory]
    [InlineData(2000, 10, 15)]  // the server: 10 % would be 200 GiB of nothing wrong
    [InlineData(256, 10, 15)]   // a laptop whose 10 % has passed the most
    [InlineData(64, 6.4, 9.6)]  // a small laptop: its share
    [InlineData(16, 2, 3)]      // the hard minimum
    public void The_floor_suits_a_large_server_and_a_small_laptop(long totalGiB, double floorGiB, double resumeGiB)
    {
        Assert.Equal(floorGiB, DiskFloor.For(totalGiB * GiB) / (double)GiB, 1);
        Assert.Equal(resumeGiB, DiskFloor.ToResume(totalGiB * GiB) / (double)GiB, 1);
    }

    /// <summary>The reading is of this machine's real filesystem: both figures are there on every platform, and a path nothing holds is no reading.</summary>
    [Fact]
    public void A_read_gives_this_filesystems_figures_and_a_read_that_cannot_be_made_gives_none()
    {
        var here = DiskSpace.Read(Paths.Home);
        Assert.NotNull(here);
        Assert.InRange(here.Free, 0, here.Total);
        Assert.Null(DiskSpace.Read(OperatingSystem.IsWindows() ? "?:\\nothing" : "/nothing/here/" + Guid.NewGuid().ToString("n")));
        Assert.Null(DiskSpace.Read(""));

        // AND THE PRODUCT'S HOST READS THE REAL ONE: only a test's composed host stands a disk in.
        Assert.Equal(here.Total, new AppHost().ReadDisk()?.Total);
    }

    // ---- the watch ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_low_read_trips_at_once_with_the_figures_it_saw_and_only_while_there_is_an_ai_to_pause()
    {
        var pause = new DiskWatch().Look(Low, Now, aiOn: true).Trip;
        Assert.Equal(new DiskPause { At = Now, Free = Low.Free, Total = Low.Total, Floor = 10 * GiB }, pause);

        var stopped = new DiskWatch().Look(Low, Now, aiOn: false);
        Assert.Equal(DiskVerdict.Low, stopped.Verdict);
        Assert.Null(stopped.Trip);
        Assert.Null(new DiskWatch().Look(Plenty, Now, aiOn: true).Trip);
    }

    /// <summary>One bad read pauses nothing. A guard that cannot see for half a minute stops the AI rather than leave it unwatched, and a good read starts the count again.</summary>
    [Fact]
    public void One_bad_read_pauses_nothing_and_a_blind_guard_fails_safe()
    {
        var watch = new DiskWatch();
        for (var i = 1; i < DiskWatch.BlindReads; i++) Assert.Equal(new DiskLook(DiskVerdict.Missed, null), watch.Look(null, Now, aiOn: true));
        Assert.Equal(DiskVerdict.Fine, watch.Look(Plenty, Now, aiOn: true).Verdict);
        for (var i = 1; i < DiskWatch.BlindReads; i++) Assert.Null(watch.Look(null, Now, aiOn: true).Trip);

        var blind = watch.Look(null, Now, aiOn: true);
        Assert.Equal(DiskVerdict.Blind, blind.Verdict);
        Assert.True(blind.Trip?.Unreadable);
    }

    /// <summary>Half as much again as the floor after a trip, so a press cannot flap; the floor alone with nothing tripped; and a blind guard is not pressed past.</summary>
    [Fact]
    public void A_press_needs_real_room_after_a_trip()
    {
        var trip = new DiskPause { At = Now, Free = Low.Free, Total = Low.Total, Floor = 10 * GiB };
        Assert.False(DiskWatch.MayResume(trip, new DiskSpace(15 * GiB - 1, 100 * GiB)));
        Assert.True(DiskWatch.MayResume(trip, new DiskSpace(15 * GiB, 100 * GiB)));
        Assert.False(DiskWatch.MayResume(trip, null));

        Assert.True(DiskWatch.MayResume(null, new DiskSpace(10 * GiB, 100 * GiB)));
        Assert.False(DiskWatch.MayResume(null, new DiskSpace(10 * GiB - 1, 100 * GiB)));
        Assert.True(DiskWatch.MayResume(null, null));
    }

    // ---- the files a filesystem can still hold (U-disk-guard-2; A4-2) --------------------------------------------------------

    // A 100 GiB disk with plenty of bytes and 6.5 M inodes (ext4's default of one per 16 KiB), and the same disk with only 400 000 of them left.
    static readonly DiskSpace Roomy = new(60 * GiB, 100 * GiB, 6_000_000, 6_553_600), FewFiles = new(60 * GiB, 100 * GiB, 400_000, 6_553_600);

    /// <summary>
    /// THE FILES FLOOR: a tenth of the filesystem's files, never under 500 000, and never over a quarter of them, so a small filesystem is not held at all of its
    /// files; none at all where the filesystem has no fixed count (macOS, Windows, btrfs read 0). A resume needs half as much again, as for bytes.
    /// </summary>
    [Theory]
    [InlineData(122_093_568, 12_209_356, 18_314_034)]   // a 2 TB ext4: its tenth
    [InlineData(30_523_392, 3_052_339, 4_578_507)]      // a 500 GB ext4, the owner's server's size: its tenth
    [InlineData(4_194_304, 500_000, 750_000)]           // a 64 GB ext4: the least
    [InlineData(1_000_000, 250_000, 375_000)]           // a 16 GB ext4: a quarter, not half of it
    [InlineData(0, 0, 0)]                               // no fixed count: no files floor
    public void The_files_floor_suits_every_filesystem_with_a_fixed_count(long files, long floor, long resume)
    {
        Assert.Equal(floor, DiskFloor.FilesFor(files));
        Assert.Equal(resume, DiskFloor.FilesToResume(files));
    }

    /// <summary>
    /// A DISK WITH ROOM FOR BYTES AND NONE FOR FILES TRIPS: empty files take no byte the guard reads, and ext4 keeps no inode back for root, so every account's
    /// next file fails there. The pause carries the figures it saw, and the same disk with no fixed count is plenty.
    /// </summary>
    [Fact]
    public void A_disk_out_of_files_trips_with_plenty_of_bytes_free()
    {
        var look = new DiskWatch().Look(FewFiles, Now, aiOn: true);
        Assert.Equal(DiskVerdict.Low, look.Verdict);
        Assert.Equal(new DiskPause { At = Now, Free = 60 * GiB, Total = 100 * GiB, Floor = 10 * GiB, FilesFree = 400_000, Files = 6_553_600, FilesFloor = 655_360 }, look.Trip);
        Assert.Equal(DiskVerdict.Fine, new DiskWatch().Look(Roomy, Now, aiOn: true).Verdict);
        Assert.Equal(DiskVerdict.Fine, new DiskWatch().Look(new DiskSpace(60 * GiB, 100 * GiB, 0, 0), Now, aiOn: true).Verdict);

        // AND A PRESS NEEDS REAL ROOM FOR FILES TOO: half as much again as the files floor after a trip, the floor alone with nothing tripped.
        Assert.False(DiskWatch.MayResume(look.Trip, new DiskSpace(60 * GiB, 100 * GiB, 983_039, 6_553_600)));
        Assert.True(DiskWatch.MayResume(look.Trip, new DiskSpace(60 * GiB, 100 * GiB, 983_040, 6_553_600)));
        Assert.False(DiskWatch.MayResume(null, FewFiles));
        Assert.True(DiskWatch.MayResume(null, new DiskSpace(60 * GiB, 100 * GiB, 655_360, 6_553_600)));
    }

    /// <summary>
    /// A BYTES TRIP WHOSE PRESS FINDS THE BYTES BACK AND THE FILES SHORT IS REFUSED IN WORDS THAT NAME THE FILES: the press needs both, so the words say both
    /// whenever the half that did not trip is short too, and only then.
    /// </summary>
    [Fact]
    public void The_refusal_names_what_the_read_lacks_whichever_half_tripped()
    {
        var bytesTrip = DiskWatch.PauseFor(new DiskSpace(5 * GiB, 100 * GiB, 6_000_000, 6_553_600), Now);
        var filesShort = new DiskSpace(60 * GiB, 100 * GiB, 900_000, 6_553_600);
        Assert.False(DiskWatch.MayResume(bytesTrip, filesShort));
        var said = DiskWords.Refused(bytesTrip, filesShort);
        foreach (var word in new[] { "15 GB", "60 GB is free now", "983,040 new files", "900,000 can be made now" })
            Assert.Contains(word, said, StringComparison.Ordinal);
        Assert.DoesNotContain("new files", DiskWords.Refused(bytesTrip, new DiskSpace(12 * GiB, 100 * GiB, 6_000_000, 6_553_600)), StringComparison.Ordinal);
    }

    /// <summary>
    /// FIGURES THAT ARE NO FIXED COUNT DO NOT BLIND THE GUARD: more free than in all, or a count past a signed word (read negative), as some network and FUSE
    /// filesystems report, are no files floor, as btrfs's 0 is; the bytes still count. A real count is kept as read.
    /// </summary>
    [Theory]
    [InlineData(6_553_600, 400_000, 400_000, 6_553_600)]
    [InlineData(6_553_600, 6_553_600, 6_553_600, 6_553_600)]
    [InlineData(6_553_600, 0, 0, 6_553_600)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 200, 0, 0)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(6_553_600, -1, 0, 0)]
    public void Figures_that_are_no_fixed_count_read_as_none(long files, long favail, long filesFree, long kept) =>
        Assert.Equal((filesFree, kept), DiskSpace.FixedCount(files, favail));

    /// <summary>On Linux the read has the filesystem's file counts beside its bytes; elsewhere none, and the files floor is not applied.</summary>
    [Fact]
    public void A_read_on_linux_has_the_filesystems_file_counts()
    {
        var here = DiskSpace.Read(Paths.Home)!;
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            Assert.True(here.Files > 0, $"no file count read on Linux: {here}");
            Assert.InRange(here.FilesFree, 0, here.Files);
        }
        else Assert.Equal((0L, 0L), (here.FilesFree, here.Files));
    }

    /// <summary>
    /// THE FILES TRIP IS THE SAME PAUSE: a puppet mid-turn on a disk out of files, one pass, and the turn's tree is gone and the owner's flag off. The page says
    /// what ran out and what to do in his words, and a press with bytes but still too few files is refused.
    /// </summary>
    [Fact]
    public async Task A_trip_on_files_ends_the_turn_and_says_what_ran_out()
    {
        await using var life = await PuppetLife.Begin("disk-files", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."]);
        var host = life.Host;
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning") && life.Presence.Live > 0, "the chair's puppet is mid-turn");

        host.ReadDisk = () => FewFiles;
        await host.DiskGuardPassAsync();

        Assert.False(host.Mission.Running);
        Assert.Equal(0, life.Presence.Live);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.Equal(400_000, host.Gateway.Settings.DiskPause?.FilesFree);
        var said = DashboardPage.MissionSentence(host.Mission.Status, host.AiNotStarted, host.DiskPauseWords);
        foreach (var word in new[] { "disk", "new files", "400,000", "655,360", "983,040", DashboardPage.LetTheAiWork, "by itself" })
            Assert.Contains(word, said, StringComparison.Ordinal);
        Assert.DoesNotContain("inode", said, StringComparison.OrdinalIgnoreCase);

        host.ReadDisk = () => new DiskSpace(60 * GiB, 100 * GiB, 900_000, 6_553_600);
        Assert.Contains("983,040", host.LetTheAiWorkOnItsOwn(), StringComparison.Ordinal);
        Assert.False(host.Mission.Running);
        // A PRESS WITH THE FILES BACK AND THE BYTES SHORT IS REFUSED SAYING SO: the words name what the read actually lacks.
        host.ReadDisk = () => new DiskSpace(12 * GiB, 100 * GiB, 6_000_000, 6_553_600);
        var bytesShort = host.LetTheAiWorkOnItsOwn();
        Assert.Contains("15 GB", bytesShort, StringComparison.Ordinal);
        Assert.Contains("12 GB is free now", bytesShort, StringComparison.Ordinal);
        Assert.False(host.Mission.Running);
        host.ReadDisk = () => Roomy;
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        Assert.True(host.Mission.Running);
        Assert.Null(host.Gateway.Settings.DiskPause);
        await host.PauseTheAiAsync();
    }

    // ---- the pause, on a real turn -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE TRIP IS THE OWNER'S OWN PAUSE. A puppet mid-turn, the disk runs low, and one pass of the guard: the turn's whole tree is gone, its launch
    /// closed and nothing left reserved, the wake is not spent, and the owner's flag is off. It stays paused over any number of good reads, says why
    /// on the page, and only the press puts it back, which needs real room.
    /// </summary>
    [Fact]
    public async Task A_trip_ends_the_turn_honestly_and_only_the_owners_press_with_real_room_resumes()
    {
        await using var life = await PuppetLife.Begin("disk-trip", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."], ["usage 10 0 5", "final Back at work."]);
        var host = life.Host;
        var wake = life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning") && life.Presence.Live > 0, "the chair's puppet is mid-turn");
        var spentBefore = host.Wakes!.Get(wake)!;

        host.ReadDisk = () => Low;
        await host.DiskGuardPassAsync();

        // PAUSED, BY THE OWNER'S OWN PATH: the loop stopped, his flag off, the record beside it.
        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.Equal(Low.Free, host.Gateway.Settings.DiskPause?.Free);
        // THE TURN ENDED, AND THE TREE WITH IT.
        Assert.Equal(0, life.Presence.Live);
        var attempt = Assert.Single(life.AttemptsOf(Chair));
        Assert.NotEqual(AiAttemptState.LAUNCHED, attempt.State);
        Assert.Null(host.Meter!.OpenAttemptIdFor(Chair));
        // AND THE WAKE WENT BACK: not settled by a turn that never finished.
        Assert.Null(host.Wakes.Get(wake)!.Disposition);
        Assert.False(spentBefore.Disposition is not null);

        // THE APP'S OWN CLOCK GOES ON: reconciling and the frozen paper runs are its pass, not a turn.
        await host.TradingPassAsync();

        // IT NEVER RESUMES ITSELF, on any number of good reads.
        host.ReadDisk = () => Plenty;
        for (var i = 0; i < 3; i++) await host.DiskGuardPassAsync();
        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.NotNull(host.Gateway.Settings.DiskPause);

        // THE PAGE SAYS WHY AND WHAT TO DO, in the owner's words.
        var said = DashboardPage.MissionSentence(host.Mission.Status, host.AiNotStarted, host.DiskPauseWords);
        foreach (var word in new[] { "disk", "5 GB", "10 GB", "15 GB", DashboardPage.LetTheAiWork, "by itself" })
            Assert.Contains(word, said, StringComparison.Ordinal);
        Assert.DoesNotContain("/", said, StringComparison.Ordinal);

        // A PRESS WITHOUT REAL ROOM IS REFUSED IN WORDS: above the floor, under what a resume needs.
        host.ReadDisk = () => new DiskSpace(12 * GiB, 100 * GiB);
        var refused = host.LetTheAiWorkOnItsOwn();
        Assert.Contains("15 GB", refused, StringComparison.Ordinal);
        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.NotNull(host.Gateway.Settings.DiskPause);

        // THE PRESS WITH ROOM PUTS IT BACK, and the record goes with it.
        host.ReadDisk = () => Plenty;
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        Assert.True(host.Mission.Running);
        Assert.True(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.Null(host.Gateway.Settings.DiskPause);
        Assert.Null(host.DiskPauseWords);
        life.Release("never");
        await life.RunUntil(() => host.Wakes.Get(wake)!.Disposition is not null, "the wake that went back is taken by the next turn");
    }

    /// <summary>One bad read beside a running turn leaves it running, says so on the health row, and pauses nothing. Half a minute of them does, in words.</summary>
    [Fact]
    public async Task A_bad_read_leaves_a_healthy_run_alone_and_a_blind_guard_pauses_it_in_words()
    {
        await using var life = await PuppetLife.Begin("disk-blind", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."]);
        var host = life.Host;
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning") && life.Presence.Live > 0, "the chair's puppet is mid-turn");

        host.ReadDisk = () => null;
        await host.DiskGuardPassAsync();
        Assert.True(host.Mission.Running);
        Assert.True(life.Presence.Live > 0);
        Assert.Equal(HealthState.DEGRADED, host.Health.Get(Components.DiskSpace).State);
        Assert.Contains("could not", host.Health.Get(Components.DiskSpace).Detail, StringComparison.Ordinal);

        for (var i = 1; i < DiskWatch.BlindReads; i++) await host.DiskGuardPassAsync();
        Assert.False(host.Mission.Running);
        Assert.Equal(0, life.Presence.Live);
        Assert.True(host.Gateway.Settings.DiskPause?.Unreadable);
        Assert.Contains("could not read", host.DiskPauseWords, StringComparison.Ordinal);
    }

    /// <summary>
    /// A RESTART DOES NOT WALK THE AI BACK ONTO A FULL DISK. The app comes up with the owner's flag on and too little room: the guard looks before the
    /// resume, and the AI stays stopped saying why, as it would have if the app had been up when the disk filled.
    /// </summary>
    [Fact]
    public async Task A_restart_on_a_low_disk_stays_paused_and_says_why()
    {
        await using var life = await PuppetLife.Begin("disk-restart", settings: s => { s.AiWorksOnItsOwn = true; s.ResumeAiOnStart = true; });
        var host = life.Host;
        host.ReadDisk = () => Low;

        await host.ResumeOnStartAsync();

        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.NotNull(host.Gateway.Settings.DiskPause);
    }

    /// <summary>A trip that stands stops every launch, the Chat page's included: the owner's typed turn is a process with the same disk under it.</summary>
    [Fact]
    public async Task While_tripped_no_launch_starts_and_the_words_are_the_reason()
    {
        await using var life = await PuppetLife.Begin("disk-chat", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["usage 10 0 5", "final Should not run."]);
        var host = life.Host;
        host.ReadDisk = () => Low;
        await host.DiskGuardPassAsync();

        await host.Conversation!.SendAsync("hello");
        await life.RunUntil(() => host.Conversation.History.Any(t => t.Text.Contains("disk", StringComparison.Ordinal)), "the Chat page says why");
        await host.Mission.PauseAsync();
        Assert.Equal(0, life.Turns(Chair));
    }

    /// <summary>
    /// A SAVE BESIDE THE PAUSE CANNOT WRITE IT AWAY. Any other save serialised the settings before it took the database's lock, so one caught before the pause could commit
    /// after it: the row then read "working, no pause", the guard took it as saved, and a restart resumed the AI with nobody pressing anything.
    /// </summary>
    [Fact]
    public async Task A_settings_save_beside_the_pause_cannot_write_it_away()
    {
        await using var life = await PuppetLife.Begin("disk-save");
        var host = life.Host;
        Task saver = Task.CompletedTask;

        // THE ORDER, MADE: the other save starts while the database's lock is held here, so it gets as far as it can before the lock. The pause commits first, then it goes on.
        await host.Db.Write(_ =>
        {
            saver = Task.Run(() => host.Gateway.Update(_ => { }));
            Thread.Sleep(300);
            host.Gateway.Update(s => s.DiskPause = new DiskPause { At = Now, Free = Low.Free });
            return Task.CompletedTask;
        });
        await saver;

        Assert.Contains("disk_pause", host.Db.GetKv("settings"), StringComparison.Ordinal);
    }

    /// <summary>The press measured the disk, so the count of failed reads starts again: a blind trip cannot leave one bad read a step from pausing the resumed run.</summary>
    [Fact]
    public async Task After_a_blind_trip_and_a_press_with_room_one_bad_read_pauses_nothing()
    {
        await using var life = await PuppetLife.Begin("disk-sighted", settings: s => s.AiWorksOnItsOwn = true);
        var host = life.Host;
        host.Mission.Start();
        host.ReadDisk = () => null;
        for (var i = 0; i < DiskWatch.BlindReads; i++) await host.DiskGuardPassAsync();
        Assert.True(host.Gateway.Settings.DiskPause?.Unreadable);

        host.ReadDisk = () => Plenty;
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        host.ReadDisk = () => null;
        await host.DiskGuardPassAsync();
        Assert.True(host.Mission.Running);
        Assert.Null(host.Gateway.Settings.DiskPause);
    }

    /// <summary>A press that lands while a trip's turns are still ending starts nothing, room or no room: the trip is the later word, and the press has to come after it.</summary>
    [Fact]
    public async Task A_press_beside_a_trip_under_way_does_not_undo_it()
    {
        await using var life = await PuppetLife.Begin("disk-press", settings: s => s.AiWorksOnItsOwn = true);
        var host = life.Host;
        host.Mission.Start();
        host.ReadDisk = () => Low;
        string? refused = null;
        host.DuringDiskTripUnderTest = () =>
        {
            host.ReadDisk = () => Plenty;
            refused = host.LetTheAiWorkOnItsOwn();
        };

        await host.DiskGuardPassAsync();

        Assert.NotNull(refused);
        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.NotNull(host.Gateway.Settings.DiskPause);

        // AND THE NEXT PRESS, WITH THE TRIP OVER, IS THE OWNER'S WORD.
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        Assert.True(host.Mission.Running);
    }

    /// <summary>A turn typed on the Chat page with the loop paused is an AI process on the same disk: the owner's flag is off, and the guard ends it all the same.</summary>
    [Fact]
    public async Task A_chat_turn_with_the_loop_paused_is_on_as_far_as_the_guard_is_concerned()
    {
        await using var life = await PuppetLife.Begin("disk-chat-turn");
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."]);
        var host = life.Host;
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        var typed = host.Conversation!.SendAsync("hello");
        await Until(() => life.Reached("turning") && life.Presence.Live > 0, "the typed turn is under way");
        Assert.False(host.Mission.Running);

        host.ReadDisk = () => Low;
        await host.DiskGuardPassAsync();
        Assert.NotNull(host.Gateway.Settings.DiskPause);

        await typed.WaitAsync(PuppetLife.Patience);
        await Until(() => life.Presence.Live == 0, "the typed turn's process left the register");
    }

    /// <summary>
    /// THE GUARD'S OWN LOOP (<see cref="AppHost.DiskGuardLoopAsync"/>): it reads on its own clock, nothing else has to run for it to trip, and a read
    /// that throws does not end it. It has to live beside the app's background pass rather than inside it, because a full disk makes that pass throw.
    /// </summary>
    [Fact]
    public async Task The_guards_own_loop_trips_on_its_own_and_outlives_a_read_that_throws()
    {
        await using var life = await PuppetLife.Begin("disk-loop", settings: s => s.AiWorksOnItsOwn = true);
        var host = life.Host;
        host.Mission.Start();
        var reads = 0;
        host.ReadDisk = () =>
        {
            if (Interlocked.Increment(ref reads) == 1) throw new IOException("a read that does what a read must never do");
            return Low;
        };

        using var stop = new CancellationTokenSource();
        var loop = host.DiskGuardLoopAsync(stop.Token, TimeSpan.FromMilliseconds(20));
        await Until(() => !host.Mission.Running && host.Gateway.Settings.DiskPause is not null, "the guard's loop paused the AI");
        await stop.CancelAsync();
        await loop;
        Assert.True(reads >= 2);
    }

    /// <summary>
    /// A DISK SO FULL THE RECORD CANNOT BE WRITTEN STILL ENDS THE TURN. The guard's write is the first thing a full disk refuses, and the owner's press
    /// through the same path had the write first, so the pause never happened.
    /// </summary>
    [Fact]
    public async Task A_settings_write_the_disk_refuses_still_pauses_the_ai_and_the_owners_press_too()
    {
        await using var life = await PuppetLife.Begin("disk-full", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."], ["signal turning2", "await never", "usage 10 0 5", "final Never reached."]);
        var host = life.Host;
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning") && life.Presence.Live > 0, "the chair's puppet is mid-turn");

        using (Full(host.Db))
        {
            host.ReadDisk = () => Low;
            await host.DiskGuardPassAsync();
        }
        Assert.False(host.Mission.Running);
        Assert.Equal(0, life.Presence.Live);
        Assert.NotNull(host.Gateway.Settings.DiskPause);

        // THE RECORD THE DISK REFUSED IS WRITTEN WHEN IT CAN BE: read back from the database, not from memory.
        host.ReadDisk = () => Plenty;
        await host.DiskGuardPassAsync();
        Assert.Contains("disk_pause", host.Db.GetKv("settings"), StringComparison.Ordinal);

        // THE OWNER'S OWN PAUSE ON THE SAME DISK: the write throws, and the AI is paused all the same.
        Assert.Null(host.LetTheAiWorkOnItsOwn());
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning2") && life.Presence.Live > 0, "the chair's next turn is under way");
        using (Full(host.Db)) await Assert.ThrowsAnyAsync<Exception>(host.PauseTheAiAsync);
        Assert.False(host.Mission.Running);
        Assert.Equal(0, life.Presence.Live);
    }

    /// <summary>
    /// THE KILL DOES NOT WAIT BEHIND THE DATABASE (<c>U-disk-guard-2</c>; A4-3). Every read and write of the app's database goes through one lock, and the
    /// pause's record is a write: with the record first, a trip while another thread held the lock (a turn's commit, a long read) left the writer running
    /// until it let go. The tree ends while the lock is still held; the record follows once it is free.
    /// </summary>
    [Fact]
    public async Task A_trip_ends_the_turn_while_another_thread_holds_the_database()
    {
        await using var life = await PuppetLife.Begin("disk-locked", settings: s => s.AiWorksOnItsOwn = true);
        life.Script(Chair, ["signal turning", "await never", "usage 10 0 5", "final Never reached."]);
        var host = life.Host;
        life.Wake(Chair);
        await life.RunUntil(() => life.Reached("turning") && life.Presence.Live > 0, "the chair's puppet is mid-turn");

        using var held = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var holder = Task.Run(() => host.Db.Read(_ => { held.Set(); letGo.Wait(); return 0; }));
        held.Wait();
        host.ReadDisk = () => Low;
        var trip = Task.Run(host.DiskGuardPassAsync);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (life.Presence.Live > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(life.Presence.Live == 0, "the turn's tree was still alive 20 s into a trip while another thread held the database");
            Assert.False(holder.IsCompleted);
        }
        finally { letGo.Set(); }
        await holder;
        await trip;
        Assert.False(host.Mission.Running);
        Assert.False(host.Gateway.Settings.AiWorksOnItsOwn);
        Assert.Equal(Low.Free, host.Gateway.Settings.DiskPause?.Free);
    }

    /// <summary>
    /// A FULL DISK DOES NOT END THE APP'S OWN CLOCK. That loop is what manages positions while the AI is paused. Its failure line went through a
    /// write that throws on a full disk, out of the catch, and the whole loop ended with nothing said; room coming back brought nothing back.
    /// </summary>
    [Fact]
    public async Task A_full_disk_does_not_end_the_apps_background_loop()
    {
        await using var life = await PuppetLife.Begin("disk-background");
        var host = life.Host;
        var passes = 0;
        host.BeforeBackgroundPassUnderTest = () =>
        {
            Interlocked.Increment(ref passes);
            host.Gateway.Log.Activity("a pass's own write, as any pass makes");
        };

        using var stop = new CancellationTokenSource();
        var loop = host.BackgroundAsync(stop.Token);
        await Until(() => Volatile.Read(ref passes) >= 1, "the loop made its first pass");
        using (Full(host.Db)) await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(loop.IsCompleted, "the app's background loop ended on a full disk: " + loop.Exception?.InnerException?.Message);

        var before = Volatile.Read(ref passes);
        await Until(() => Volatile.Read(ref passes) > before, "room came back and the loop made another pass");
        await stop.CancelAsync();
        await loop;
    }

    static async Task Until(Func<bool> condition, string waitingFor)
    {
        var deadline = DateTimeOffset.UtcNow + PuppetLife.Patience;
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, waitingFor);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// EVERY WRITE TO THE RECORDS THE GUARD AND THE LOOP KEEP FAILS IN SQLITE'S OWN WORDS FOR A FULL DISK until this is disposed. Triggers on this connection
    /// alone, because a real ceiling on the file's pages lets any write through that fits a page already there.
    /// </summary>
    static IDisposable Full(Database db)
    {
        db.Write(_ =>
        {
            foreach (var table in FullTables)
                foreach (var verb in new[] { "INSERT", "UPDATE" })
                    db.Exec($"CREATE TEMP TRIGGER full_{table}_{verb} BEFORE {verb} ON main.{table} BEGIN SELECT RAISE(FAIL, 'database or disk is full'); END");
            return 0;
        });
        return new Room(db);
    }

    static readonly string[] FullTables = ["kv", "activity", "engineering_log"];

    sealed class Room(Database db) : IDisposable
    {
        public void Dispose() => db.Write(_ =>
        {
            foreach (var table in FullTables)
                foreach (var verb in new[] { "INSERT", "UPDATE" })
                    db.Exec($"DROP TRIGGER temp.full_{table}_{verb}");
            return 0;
        });
    }
}
