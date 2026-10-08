using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// A HELD WINDOW NEVER SHRINKS WHILE A CAMPAIGN JUDGES ON IT (<c>U-holdout-later</c>; seat A's decision of 2026-10-08,
/// ruled urgent by the orchestrator): the owner's press may not move a cutoff LATER while its dataset has an open
/// campaign, as it may not move it earlier, and a refused press writes nothing.
///
/// <para><b>What was wrong before this.</b> <c>DatasetStore.SetHoldout</c> refused only an EARLIER cutoff and
/// <c>TradingGateway.SetHoldout</c> left the open campaign as it was. The survey's probe (<c>s-holdout-campaign</c>, fact
/// 3, RED at <c>ea88e72b</c>) moved A's cutoff from 01:00 to 01:30: the press answered Ok, <c>data-bars</c> served the
/// Research Director the thirty minutes in between, and the referee still judged from 01:00 — every later judgement
/// reading minutes research had been served, its <c>strategy_verdict</c> row recording them as private. (a) is that
/// probe's legs, asserting the protection.</para>
///
/// <para>The ledger every test reads: A is BTCUSDT 1m v1, 120 minutes from 2026-08-01 00:00Z, cut at 01:00 by the owner's
/// own press (<c>TradingGateway.SetHoldout</c>, which opens its campaign), so its window is [01:00, 02:00).</para>
/// </summary>
public class HoldoutLaterOverPipeTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A's cutoff, minute 60, pressed first.</summary>
    static readonly DateTimeOffset Cutoff = Start.AddMinutes(60);

    /// <summary>The later date the owner's second press asks for, minute 90: [01:00, 01:30) is what it would release.</summary>
    static readonly DateTimeOffset Later = Start.AddMinutes(90);

    /// <summary>
    /// A PROFITABLE PROGRAM THAT DECLARES THE THREE EXECUTION BOUNDS — the one <c>BarHoldoutOverPipeTests</c>' verdict test
    /// judges, over the same bars cycling 96 → 105.
    /// </summary>
    const string JudgeableText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>The wire by hand, as <c>BarHoldoutOverPipeTests</c> speaks it: the machine's key on every frame, the grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    /// <summary>A gateway serving the pipe, A, and the campaign the owner's first press opened over it.</summary>
    sealed record Rig(TradingGateway Gw, Database Db, GatewayPipeServer Server, AgentGrants Grants, string Pipe,
        DatasetRecord A, CampaignRow Campaign) : IAsyncDisposable
    {
        public async Task<Raw> Dial(string? role)
        {
            var client = await Raw.ConnectAsync(Pipe);
            var grant = role is null ? null : Grants.Issue(role, "attempt-holdout-later").Token;
            var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
            Assert.True(hello.Ok, Json.Write(hello.Error));
            return client;
        }

        /// <summary>The same frame through the other door into the same handler: the in-process worker surface.</summary>
        public Task<IpcResponse> CallAsync(string? role, IpcRequest req) => Server.CallAsync(req, role, "attempt-holdout-later");

        /// <summary>
        /// Everything a refused press must leave as it was: A's cutoff and class, the open campaign, the count of
        /// campaigns, and every held window — the bars' and the tape's, which are one list (<c>TapeHoldout.WindowsOf</c>).
        /// </summary>
        public Snapshot Ledger()
        {
            var a = Gw.Datasets.ById(A.Id)!;
            return new(a.HoldoutFrom, a.EvaluationClass, Gw.Campaigns.OpenForDataset(A.Id), Gw.Campaigns.All().Count,
                TapeHoldout.WindowsOf(Gw.Datasets.All()));
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>What <see cref="Rig.Ledger"/> read, compared field by field: a list inside a record compares by reference.</summary>
    sealed record Snapshot(DateTimeOffset? Cutoff, string Class, CampaignRow? Open, int Campaigns,
        IReadOnlyList<TapeHoldoutWindow> Windows)
    {
        public void Unchanged(Snapshot after)
        {
            Assert.Equal(Cutoff, after.Cutoff);
            Assert.Equal(Class, after.Class);
            Assert.Equal(Open, after.Open);
            Assert.Equal(Campaigns, after.Campaigns);
            Assert.Equal(Windows, after.Windows);
        }
    }

    static async Task<Rig> Ready()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = "ta-hl-" + Guid.NewGuid().ToString("n")[..12];
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        // A: THE OWNER'S FIRST PRESS — the cutoff and its campaign in one transaction.
        var a = Recorded(db, "BTCUSDT", "v1", 120);
        var (done, campaign) = gw.SetHoldout(a.Id, Cutoff, EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
        Assert.NotNull(campaign);
        Assert.Equal(Cutoff, campaign!.HoldoutFrom);

        // The campaign AS THE LEDGER READS IT, so every comparison below is row against row.
        return new Rig(gw, db, server, grants, pipe, gw.Datasets.ById(a.Id)!, gw.Campaigns.ById(campaign.Id)!);
    }

    /// <summary>A dataset the ledger recorded, hashes and all, with no cutoff: <paramref name="bars"/> 1m bars from <see cref="Start"/>, closes cycling 96 → 105.</summary>
    static DatasetRecord Recorded(Database db, string pair, string version, int bars)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, version, 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));
        return store.ById(id)!;
    }

    /// <summary>
    /// The judgeable program in the Research Director's own folder under a name of this test's own, its version frozen
    /// at <see cref="Start"/> — before the window begins, so the scoring policy's forward-evidence clause can be met.
    /// </summary>
    static (string File, string Version) Judgeable(Database db)
    {
        var parsed = StrategyParser.Parse(JudgeableText).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Start, CouncilRoles.Research, "attempt-holdout-later"));

        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        var name = $"holdout-later-{Guid.NewGuid():n}.strategy";
        File.WriteAllText(Path.Combine(dir, name), JudgeableText);
        return ("strategies/" + name, parsed.StrategyId);
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static IpcRequest Bars(DateTimeOffset from, DateTimeOffset to) => new()
    {
        Op = Ops.DataBars, Session = "holdout-later",
        Args = Args(("pair", "BTCUSDT"), ("from", Iso(from)), ("to", Iso(to)))
    };

    static string Who(string? role) => role is null ? "a caller that proved no role" : $"the {CouncilRoles.Title(role)}";

    /// <summary>What a reply served, for the log: "REFUSED code: message…" or "SERVED n bars …".</summary>
    static string Said(IpcResponse r)
    {
        if (!r.Ok)
        {
            var message = r.Error?.Message ?? "";
            return $"REFUSED {r.Error?.Code}: {(message.Length > 220 ? message[..220] + "…" : message)}";
        }

        var d = Data(r);
        if (d.TryGetProperty("bars", out var bars) && bars.ValueKind == JsonValueKind.Array)
            return $"SERVED {bars.GetArrayLength()} bars"
                + (bars.GetArrayLength() > 0
                    ? $", first open_time {bars[0].GetProperty("open_time")}, last open_time {bars[bars.GetArrayLength() - 1].GetProperty("open_time")}"
                    : "");
        return "SERVED " + d;
    }

    /// <summary>The words of the later refusal that test (c) holds the press to, campaign and dates included.</summary>
    static void SaysWhoJudgesAndWhatToDo(string why, long campaign, DateTimeOffset asked)
    {
        Assert.StartsWith("Nothing was changed.", why, StringComparison.Ordinal);
        Assert.Contains($"Campaign {campaign} still judges strategies on every bar from 2026-08-01 01:00 UTC on", why, StringComparison.Ordinal);
        Assert.Contains($"moving the date to {asked.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC "
            + "would show the AI bars those judgements use", why, StringComparison.Ordinal);
        Assert.Contains("To hold back a different period, download a fresh copy of the history and hold months back on that.",
            why, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a) A LATER CUTOFF IS REFUSED WHILE A CAMPAIGN JUDGES FROM THE FIRST — the probe's L legs, asserting the protection.
    /// The owner presses 01:30 over A's 01:00 with campaign 1 open: the press answers Ok=False in his words and writes
    /// NOTHING (A's cutoff and class, the campaign, the count of campaigns and every held window are as they were);
    /// <c>data-bars</c> over [01:00, 01:30) stays <c>HOLDOUT_WITHHELD</c> for the Research Director and for a caller that
    /// proved no role, over the pipe and in process; and a judgement taken afterwards reads from 01:00 — the referee's
    /// run, its verdict row and A's own column all name the one cutoff, so no minute it judges was served. RED on the
    /// base, where the press answered Ok and moved A's column to 01:30.
    /// </summary>
    [Fact]
    public async Task A_later_cutoff_is_refused_while_a_campaign_judges_from_the_first()
    {
        await using var rig = await Ready();
        var before = rig.Ledger();

        // L.1 — THE OWNER'S SECOND PRESS, LATER.
        var (moved, campaign) = rig.Gw.SetHoldout(rig.A.Id, Later, EvaluationClass.Research);
        var after = rig.Ledger();
        log.WriteLine($"L.1 the owner moves A's cutoff from {Cutoff:u} to {Later:u}: Ok={moved.Ok} Why=\"{moved.Why}\" "
            + $"campaign={campaign?.Id} campaign.HoldoutFrom={rig.Gw.Campaigns.ById(rig.Campaign.Id)!.HoldoutFrom:u} "
            + $"dataset.holdout_from={after.Cutoff:u}");

        // L.2 — THE MINUTES THE MOVE WOULD RELEASE, asked for by the Research Director and by a caller with none.
        var leaks = new List<string>();
        foreach (var role in new[] { CouncilRoles.Research, null })
        {
            await using var client = await rig.Dial(role);
            foreach (var (door, reply) in new[]
                     {
                         ("over the pipe", await client.SendAsync(Bars(Cutoff, Later.AddMinutes(-1)))),
                         ("in process", await rig.CallAsync(role, Bars(Cutoff, Later.AddMinutes(-1))))
                     })
            {
                var line = $"L.2 data-bars BTCUSDT [{Iso(Cutoff)}, {Iso(Later.AddMinutes(-1))}] as {Who(role)} {door} after the press: {Said(reply)}";
                log.WriteLine(line);
                if (reply.Ok || reply.Error?.Code != nameof(ErrorCode.HOLDOUT_WITHHELD)
                             || !reply.Error.Message.Contains($"holds out every bar from {Cutoff:u}", StringComparison.Ordinal)
                             || Json.Write(reply).Contains("open_time", StringComparison.Ordinal))
                    leaks.Add(line);
            }
        }

        // L.3-L.5 — A JUDGEMENT AFTER THE PRESS: research over A's development minutes, then the verdict.
        var (file, version) = Judgeable(rig.Db);
        await using (var research = await rig.Dial(CouncilRoles.Research))
        {
            var ran = await research.SendAsync(new IpcRequest
            {
                Op = Ops.Backtest, Session = "holdout-later", RequestId = "holdout-later-bt-" + Guid.NewGuid().ToString("n")[..12],
                Args = Args(("strategy", file), ("dataset", rig.A.Id.ToString(CultureInfo.InvariantCulture)),
                    ("from", Iso(Start)), ("to", Iso(Cutoff.AddMinutes(-1))), ("increment", "1"))
            });
            log.WriteLine($"L.3 backtest over A [{Iso(Start)}, {Iso(Cutoff.AddMinutes(-1))}]: {Said(ran)}");
            Assert.True(ran.Ok, Json.Write(ran.Error));

            var verdict = await research.SendAsync(new IpcRequest
            {
                Op = Ops.Verdict, Session = "holdout-later", RequestId = "holdout-later-verdict",
                Args = Args(("version", version), ("dataset", rig.A.Id.ToString(CultureInfo.InvariantCulture)))
            });
            log.WriteLine($"L.4 verdict over A: {(verdict.Ok ? Data(verdict).ToString() : Said(verdict))}");
            Assert.True(verdict.Ok, Json.Write(verdict.Error));
        }

        var judged = rig.Gw.Strategies.RunsOfDataset(rig.A.Id).Single(r => r.Role == Referee.RunRole);
        var charged = Assert.Single(rig.Gw.Campaigns.Verdicts(rig.Campaign.Id));
        var column = rig.Gw.Datasets.ById(rig.A.Id)!.HoldoutFrom;
        log.WriteLine($"L.5 the referee's run over A: window_from={judged.WindowFrom:u} bars={judged.Bars}; its verdict row's "
            + $"holdout_from={charged.HoldoutFrom:u}; A's cutoff now {column:u}");

        // REFUSED IN THE OWNER'S WORDS, AND NOTHING WRITTEN.
        Assert.False(moved.Ok, $"a later cutoff was written while campaign {rig.Campaign.Id} judges from {Cutoff:u}: "
            + $"A's cutoff is now {after.Cutoff:u}");
        Assert.Null(moved.Cutoff);
        Assert.Null(campaign);
        SaysWhoJudgesAndWhatToDo(moved.Why, rig.Campaign.Id, Later);
        before.Unchanged(after);
        Assert.Equal(rig.Campaign, after.Open);
        Assert.Equal(1, after.Campaigns);

        // THE RELEASED MINUTES STAYED HELD, FOR EVERY CALLER AND THROUGH BOTH DOORS.
        Assert.True(leaks.Count == 0, "minutes the refused press would have released were served: " + string.Join(" | ", leaks));

        // AND THE JUDGEMENT READ FROM THE ONE CUTOFF: the referee's window, the row that records what was private, and the
        // column every reader serves by.
        Assert.Equal(Cutoff, judged.WindowFrom);
        Assert.Equal(Cutoff, charged.HoldoutFrom);
        Assert.Equal(Cutoff, column);
    }

    /// <summary>
    /// (b) THE SAME CUTOFF AGAIN IS STILL A NO-OP: Ok, the campaign already open handed back rather than a second one,
    /// and nothing written — the press the owner makes when he is not sure the first one landed.
    /// </summary>
    [Fact]
    public async Task The_same_cutoff_again_is_still_a_no_op()
    {
        await using var rig = await Ready();
        var before = rig.Ledger();

        var (again, campaign) = rig.Gw.SetHoldout(rig.A.Id, Cutoff, EvaluationClass.Research);
        log.WriteLine($"the same cutoff again, {Cutoff:u}: Ok={again.Ok} Why=\"{again.Why}\" campaign={campaign?.Id}");

        Assert.True(again.Ok, again.Why);
        Assert.Equal(Cutoff, again.Cutoff);
        Assert.Equal(rig.Campaign, campaign);
        var after = rig.Ledger();
        before.Unchanged(after);
        Assert.Equal(1, after.Campaigns);
    }

    /// <summary>
    /// (b) AN EARLIER CUTOFF IS REFUSED AS BEFORE, in the same words — and they no longer offer a later date as the
    /// harmless direction, which is what they said until this unit. Nothing is written.
    /// </summary>
    [Fact]
    public async Task An_earlier_cutoff_is_refused_as_before_and_no_longer_offers_a_later_one()
    {
        await using var rig = await Ready();
        var before = rig.Ledger();

        var (back, campaign) = rig.Gw.SetHoldout(rig.A.Id, Cutoff.AddMinutes(-30), EvaluationClass.Research);
        log.WriteLine($"an earlier cutoff, {Cutoff.AddMinutes(-30):u}: Ok={back.Ok} Why=\"{back.Why}\"");

        Assert.False(back.Ok, "a cutoff was moved back, and the bars in between had already been served");
        Assert.Null(campaign);
        Assert.Contains($"dataset {rig.A.Id} already holds out every bar from {Cutoff:u}, and {Cutoff.AddMinutes(-30):u} is EARLIER",
            back.Why, StringComparison.Ordinal);
        Assert.Contains("already been served to the research process", back.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("later is allowed", back.Why, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing has read", back.Why, StringComparison.Ordinal);
        var after = rig.Ledger();
        before.Unchanged(after);
        Assert.Equal(1, after.Campaigns);
    }

    /// <summary>
    /// (c) THE REFUSAL NAMES THE CAMPAIGN, ITS FIRST CUTOFF AND THE FRESH DOWNLOAD — and it is the campaign judging NOW: once
    /// campaign 1 is renewed, its child carries the same cutoff (<c>CampaignStore.Renew</c>) and the refusal names the
    /// child, so a renewal is not a way to move the date either. A press of the fixture button is refused the same way:
    /// the class rides on the same press and is not written.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_the_campaign_its_first_cutoff_and_the_fresh_download()
    {
        await using var rig = await Ready();

        var (first, _) = rig.Gw.SetHoldout(rig.A.Id, Later, EvaluationClass.Research);
        log.WriteLine($"campaign {rig.Campaign.Id} open: \"{first.Why}\"");
        Assert.False(first.Ok);
        SaysWhoJudgesAndWhatToDo(first.Why, rig.Campaign.Id, Later);

        var renewed = rig.Gw.Campaigns.Renew(rig.Campaign.Id, 5, 5, Start.AddDays(70));
        Assert.True(renewed.Ok, renewed.Why);
        var child = renewed.Campaign!;
        Assert.Equal(Cutoff, child.HoldoutFrom);

        var muchLater = Start.AddMinutes(110);
        var (second, campaign) = rig.Gw.SetHoldout(rig.A.Id, muchLater, EvaluationClass.Fixture);
        log.WriteLine($"campaign {child.Id} (renewed from {rig.Campaign.Id}) open: \"{second.Why}\"");
        Assert.False(second.Ok);
        Assert.Null(campaign);
        SaysWhoJudgesAndWhatToDo(second.Why, child.Id, muchLater);
        Assert.DoesNotContain($"Campaign {rig.Campaign.Id} ", second.Why, StringComparison.Ordinal);

        var a = rig.Gw.Datasets.ById(rig.A.Id)!;
        Assert.Equal(Cutoff, a.HoldoutFrom);
        Assert.Equal(EvaluationClass.Research, a.EvaluationClass);
        Assert.Equal(child, rig.Gw.Campaigns.OpenForDataset(rig.A.Id));
    }

    /// <summary>
    /// (d) <c>data-list</c>'S NOTE STATES THE EVERY-DATASET RULE AND NO LONGER EXEMPTS THE FORWARD BARS — U-bar-holdout's
    /// leftover. The note every pipe caller reads beside the cutoff said "No holdout applies to them either … every forward
    /// bar post-dates every freeze", and that 'data-bars' and 'backtest' refuse a window reaching the dataset's OWN cutoff,
    /// while since U-bar-holdout a cutoff holds its window on every pair's bars, the forward bars and the tape; and it
    /// offered the owner's later move as the one he may still make. RED on the base, for the Research Director and a
    /// caller that proved no role alike.
    /// </summary>
    [Fact]
    public async Task Data_list_states_the_every_dataset_rule_and_no_longer_exempts_the_forward_bars()
    {
        await using var rig = await Ready();

        foreach (var role in new[] { CouncilRoles.Research, null })
        {
            await using var client = await rig.Dial(role);
            var reply = await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "holdout-later" });
            Assert.True(reply.Ok, Json.Write(reply.Error));
            var note = Data(reply).GetProperty("note").GetString()!;
            log.WriteLine($"data-list's note as {Who(role)}: {note}");

            // GONE: the exemption, its premise, and the move offered as harmless.
            Assert.DoesNotContain("No holdout applies", note, StringComparison.Ordinal);
            Assert.DoesNotContain("post-dates every freeze", note, StringComparison.Ordinal);
            Assert.DoesNotContain("moving it earlier is refused", note, StringComparison.Ordinal);

            // STATED: the window, every pair's bars and the tape, the forward bars, and the cutoff that does not move.
            Assert.Contains("HOLDOUT WINDOW runs from it to the close of its last bar", note, StringComparison.Ordinal);
            Assert.Contains("THE SAME MONTHS ARE HELD BACK ON EVERY PAIR'S BARS AND ON THE TAPE", note, StringComparison.Ordinal);
            Assert.Contains("reach ANY dataset's holdout window — through that dataset, a newer version of the same pair, "
                + "another pair or the forward bars alike", note, StringComparison.Ordinal);
            Assert.Contains("held back over every dataset's holdout window exactly as the archive's bars are", note, StringComparison.Ordinal);
            Assert.Contains("never earlier, and never later while a campaign judges strategies from it", note, StringComparison.Ordinal);

            // AND WHAT IT ALWAYS SAID STILL STANDS: the field is named, and forward bars are a different kind of thing.
            Assert.Contains("'holdout_from'", note, StringComparison.Ordinal);
            Assert.Contains("DIFFERENT KIND OF THING", note, StringComparison.Ordinal);
        }
    }
}
