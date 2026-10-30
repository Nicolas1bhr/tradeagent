using Avalonia.Controls;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S PAGE (<c>U-settings-shed</c>; R24 §§ 4.3, 4.7): it holds exactly the presses that stay his — the emergency family, his
/// connections and keys, the real money with perception's budget, the evidence settings and the narrowing presses over what is running —
/// and no press that sets a mode, a limit, an allowance, a lane, an allocation, an envelope grant or real money — the seat choices and
/// the paper review release stay his only until a board seat is active (the orchestrator's rulings of 2026-10-23).
/// </summary>
public class OwnerPageTests
{
    /// <summary>The owner's page as the window builds it.</summary>
    internal static OwnerPage Build(AppHost host) => new(host, new SettingsPage(host));

    static async Task OnThePage(Func<OwnerPage, AppHost, Task> body)
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try { await body(Build(host), host); }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>Every press in the built tree, by the words it wears at rest.</summary>
    static List<string> Presses(OwnerPage page) =>
        [.. page.Root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string ?? $"<{b.Content?.GetType().Name}>")];

    /// <summary>
    /// THE OWNER'S PAGE HOLDS EXACTLY THE OWNER'S PRESSES (R24 § 4.3; AUTH-001): a label allowlist over the tree the window builds — the
    /// emergency family, the key's two presses (his and perception's), the real-money cap and perception's budget, the AI's price, the two
    /// narrowings over what is running and the review release while no board seat is active — and none of the presses that left with
    /// the security board's decisions: no mode, no real-money switch, no limit, plan, allowance, review, lane, share,
    /// allocation or envelope grant.
    /// </summary>
    [Fact]
    public Task The_owners_page_holds_exactly_the_owners_presses() => OnThePage((page, host) =>
    {
        var presses = Presses(page);
        var card = (PerceptionCard)typeof(OwnerPage).GetField("_perception", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        var choices = card.Choices.Select(b => (string)b.Content!).ToList();

        string[] owners =
        [
            Labels.StopAiTrading, Labels.CancelAllWorkingOrders, Labels.CloseAllPositions,
            Labels.SaveHarnessKey, Labels.ForgetHarnessKey,
            Labels.SaveDailyCap, Labels.SaveAiPrice,
            Labels.WithdrawEnvelope, Labels.StopDeployment, Labels.ReopenAfterReview,
            // THE SEAT CHOICES, his until a board seat is active (the orchestrator's ruling): what the Research Director runs on, and the
            // two model rows — the AI's and the Research Director's — each "TradeAgent's choice" and one press per priced model.
            Labels.RuntimeIsTheCli, Labels.RuntimeIsTheHarness, Labels.AiModelDefault, Labels.AiModelDefault,
            .. host.ModelChoices.Select(m => OwnerPage.ModelLabel(m, host.SpendToday.Currency)),
            .. host.ModelChoices.Select(m => OwnerPage.ModelLabel(m, host.SpendToday.Currency)),
            // (AN EARLIER ARMED HOME's one press is the window's strip since fix pass 1 — above every page, not on this one.)
            // PERCEPTION'S CARD: its own key's two presses, its budget inside the cap, its Test — and the instrument it is shown on.
            (string)card.SaveKey.Content!, (string)card.ForgetKey.Content!, (string)card.SaveBudget.Content!, (string)card.TestButton.Content!,
            // CONNECTIONS (item 3): the platform's three, the account list's look-again, and the bridge's repair.
            "Use the practice simulator", "Use TradeAgent paper", "Use ATAS", "Look again", Labels.ReinstallBridge,
            // EVIDENCE (item 3): what a paper fill pays, and the holdout's two.
            "Use my numbers", "Clear my numbers", Labels.SetHoldout, Labels.SetHoldoutFixture,
            // UPDATES (item 3).
            "Check for updates", "What's new", "Install update", "Turn off automatic checks",
            // THE BOARD'S COMPOSITION AND BUDGET (U-org-surface-b item 5; R24 § 4.3, CHANGE-001): his, each two presses — the board only
            // proposes them. Three presses: the share, founding a seat, retiring the seat chosen in its list.
            Labels.SetBoardBudgetSharePress, Labels.FoundBoardSeatPress, Labels.RetireBoardSeatPress
        ];
        // THE ACCOUNT LIST'S ROWS arrive when the platform answers; each is the account's own press and nothing else.
        Assert.Equal(string.Join(" | ", owners.Concat(choices).Order(StringComparer.Ordinal)),
            string.Join(" | ", presses.Where(p => p != "Use this account").Order(StringComparer.Ordinal)));

        // NONE OF THE BOARD'S, by name — so a re-added one fails here with its own words, not as a count.
        string[] board =
        [
            "Switch real-money trading ON", "Switch real-money trading OFF", Labels.SaveLimits, Labels.SavePlanCapacity,
            Labels.SaveTurnAllowance, Labels.SaveReviewEvery, Labels.SaveLanes, Labels.SaveResearchShare, Labels.Allocate,
            Labels.GrantEnvelope,
            .. Enum.GetValues<TradingMode>().Select(Ui.ModeLabel)
        ];
        foreach (var label in board) Assert.DoesNotContain(label, presses);

        // AND THE SETTINGS PAGE KEEPS THE MARKET DATA ALONE: the owner's sections are on his page, in one tree only.
        var settings = new SettingsPage(host);
        var left = settings.Root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        foreach (var label in owners) Assert.DoesNotContain(label, left);
        Assert.Contains("Download 12 months", left);
        return Task.CompletedTask;
    });

    /// <summary>
    /// (U-org-surface-b 5) THE BOARD'S COMPOSITION AND BUDGET TAKE TWO PRESSES AND REFUSE IN WORDS (R24 § 4.3, Q6, Q7; CHANGE-001): on the
    /// owner's page the board's share, founding a seat and retiring one each write nothing on the first press and act on the second; a
    /// choice moved after the first press disarms it; a box that reads as nothing refuses both presses; and the store's refusal — a seat
    /// with no vendor family, a retirement that would leave one model class — is shown verbatim, nothing written. The closed lists are
    /// the board's own (<c>BoardSeats.SeatClasses</c>, <c>BoardModelClass.All</c>) on a box no scroll changes. RED at base: no such presses.
    /// </summary>
    [Fact]
    public Task The_boards_composition_and_budget_take_two_presses_and_refuse_in_words() => OnThePage((page, host) =>
    {
        static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var c = (BoardComposition)typeof(OwnerPage).GetField("_boardComposition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        var board = host.Board;
        Assert.Equal(TradeAgent.Core.Board.BoardSeats.SeatClasses, c.SeatClass.ItemsSource);
        Assert.Equal(TradeAgent.Core.Board.BoardModelClass.All, c.ModelClass.ItemsSource);
        Assert.IsType<ChoiceBox>(c.SeatClass);
        Assert.NotNull(typeof(ChoiceBox).GetMethod("OnPointerWheelChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));

        // THE SHARE: the box opens on the share in force (U-ui-pages: an empty box drew a refusal before anything was typed); an emptied
        // box refuses either press; a figure takes two.
        var was = board.BudgetShare();
        Assert.Equal(was, c.Share.Value);
        c.Share.Box.Text = "";
        Press(c.SetShare);
        Assert.False(Ui.IsArmed(c.SetShare));
        Assert.Equal(was, board.BudgetShare());
        c.Share.Box.Text = "0.3";
        Press(c.SetShare);
        Assert.True(Ui.IsArmed(c.SetShare));
        Assert.Equal(was, board.BudgetShare());
        Press(c.SetShare);
        Assert.Equal(0.3m, board.BudgetShare());

        // FOUNDING: two presses; a choice moved after the first disarms it; no vendor family is the store's refusal, verbatim.
        var seats = board.Seats().Count;
        c.SeatClass.SelectedItem = TradeAgent.Core.Db.SeatClasses.Cell;
        c.Vendor.Text = "anthropic";
        Press(c.Found);
        Assert.Equal(seats, board.Seats().Count);
        c.ModelClass.SelectedItem = TradeAgent.Core.Board.BoardModelClass.Standard;
        Assert.False(Ui.IsArmed(c.Found));
        Press(c.Found);
        Press(c.Found);
        var founded = board.Seats().Last();
        Assert.Equal(seats + 1, board.Seats().Count);
        Assert.Equal((TradeAgent.Core.Db.SeatClasses.Cell, TradeAgent.Core.Board.BoardModelClass.Standard, "anthropic", TradeAgent.Core.Board.BoardSeatStatus.Dormant),
            (founded.SeatClass, founded.ModelClass, founded.VendorFamily, founded.Status));
        c.Vendor.Text = "";
        Press(c.Found);
        Press(c.Found);
        Assert.Equal(seats + 1, board.Seats().Count);
        Assert.Equal(Assert.Throws<TradeAgentException>(() => new TradeAgent.Core.Board.BoardStore(host.Db).FoundBoardSeat(
            TradeAgent.Core.Db.SeatClasses.Cell, TradeAgent.Core.Board.BoardModelClass.Standard, "", DateTimeOffset.UtcNow)).Message, c.Note.Text);

        // RETIRING: two presses; one that would leave the board on one model class is the store's refusal, verbatim, nothing written.
        Assert.Contains(founded.Id, (IEnumerable<string>)c.RetireSeat.ItemsSource!);
        c.RetireSeat.SelectedItem = founded.Id;
        Press(c.Retire);
        Assert.Equal(TradeAgent.Core.Board.BoardSeatStatus.Dormant, board.Seats().Single(s => s.Id == founded.Id).Status);
        Press(c.Retire);
        Assert.Equal(TradeAgent.Core.Board.BoardSeatStatus.Retired, board.Seats().Single(s => s.Id == founded.Id).Status);
        Assert.DoesNotContain(founded.Id, (IEnumerable<string>)c.RetireSeat.ItemsSource!);
        var standard = board.Seats().Single(s => s.ModelClass == TradeAgent.Core.Board.BoardModelClass.Standard && s.Status != TradeAgent.Core.Board.BoardSeatStatus.Retired).Id;
        c.RetireSeat.SelectedItem = standard;
        Press(c.Retire);
        Press(c.Retire);
        Assert.NotEqual(TradeAgent.Core.Board.BoardSeatStatus.Retired, board.Seats().Single(s => s.Id == standard).Status);
        Assert.Contains("would leave the board on fewer than two model classes", c.Note.Text, StringComparison.Ordinal);
        Assert.Equal(Theme.Caution, c.Note.Foreground);
        return Task.CompletedTask;
    });

    /// <summary>
    /// NO EVIDENCE SETTING IS A POLICY KEY (EVID-001; R24 § 4.3): the AI's price, what a paper fill pays, the holdout, a campaign's
    /// budgets and the judge's capital stay the owner's — measurements, never the board's to set — so no field of the board's policy
    /// names one, and the owner's page shows the campaign figures the settings row holds, read-only, with no box for them.
    /// </summary>
    [Fact]
    public Task No_evidence_setting_is_a_policy_key() => OnThePage((page, host) =>
    {
        string[] evidence = ["price", "friction", "fee", "slippage", "holdout", "campaign", "trial", "verdict", "exploration", "judge", "capital"];
        var paths = TradeAgent.Core.Board.BoardPolicyBody.Flatten(TradeAgent.Core.Board.BoardPolicyBody.Canonical(TradeAgent.Core.Board.BoardFounding.V1))
            .Select(f => f.Path).ToList();
        var properties = typeof(TradeAgent.Core.Board.BoardPolicy).GetProperties().Select(p => p.Name).ToList();
        foreach (var word in evidence)
        {
            Assert.DoesNotContain(paths, p => p.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(properties, p => p.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(Labels.BoardFields, f => f.Path.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        // THE CAMPAIGN FIGURES, AS THE ROW HOLDS THEM, READ-ONLY.
        host.Gateway.Update(s => { s.CampaignTrialBudget = 120; s.CampaignExplorationBudget = 500; s.CampaignVerdictBudget = 2; s.JudgeCapital = 0m; });
        page.Update(host.Gateway.StatusAsync().GetAwaiter().GetResult());
        TextBlock Of(string name) => (TextBlock)typeof(OwnerPage).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;
        Assert.Equal("120", Of("_trialBudget").Text);
        Assert.Equal("120", Of("_explorationBudget").Text);           // clamped to the trials, as a campaign reads it
        Assert.Equal("2", Of("_verdictBudget").Text);
        Assert.StartsWith("10000", Of("_judgeCapital").Text, StringComparison.Ordinal);   // zero reads as the judge's ten thousand
        Assert.Contains(Labels.CampaignBudgetsAreEvidence, page.Root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        return Task.CompletedTask;
    });

    /// <summary>
    /// THE REVIEW RELEASE IS OFFERED ONLY WHILE NO BOARD SEAT IS ACTIVE (the orchestrator's ruling of 2026-10-23): something held and no
    /// active seat — offered, with the sentence that says it is his only until the board is active; an active seat — never, whatever is held.
    /// </summary>
    [Fact]
    public void The_review_release_is_the_owners_only_until_a_board_seat_is_active()
    {
        Assert.True(OwnerPage.ReleaseOffered(held: 1, boardActive: false));
        Assert.False(OwnerPage.ReleaseOffered(held: 0, boardActive: false));
        Assert.False(OwnerPage.ReleaseOffered(held: 1, boardActive: true));
        Assert.Contains("yours until the security board is active, then the board's", Labels.ReopenAfterReviewIsYoursUntilTheBoard,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// SEAT CHOICES STAY THE OWNER'S UNTIL A BOARD SEAT IS ACTIVE (the orchestrator's ruling for the board pair, 2026-10-23: "seat choices
    /// (model/runtime per role) stay settings until a board seat is ACTIVE"). With every board seat dormant his page offers the model the AI
    /// runs on, what the Research Director runs on and its model — labelled "yours until the security board is active", the same words as
    /// the review release — and each press writes the settings row it always wrote.
    /// </summary>
    [Fact]
    public Task Seat_choices_stay_the_owners_until_a_board_seat_is_active() => OnThePage((page, host) =>
    {
        Assert.False(host.Board.AnyActive);
        page.Update(host.Gateway.StatusAsync().GetAwaiter().GetResult());
        var presses = Presses(page);
        Assert.Contains(Labels.RuntimeIsTheCli, presses);
        Assert.Contains(Labels.RuntimeIsTheHarness, presses);
        Assert.Equal(2, presses.Count(p => p == Labels.AiModelDefault));

        // THE LABEL, ON BOTH PRESSES THAT ARE HIS ONLY UNTIL THE BOARD IS ACTIVE: the seat choices and the review release.
        var texts = page.Root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(Labels.SeatChoicesAreYours, texts);
        Assert.Contains(Labels.ReopenAfterReviewIsYoursUntilTheBoard, texts);
        Assert.Contains(Labels.YoursUntilTheBoardIsActive, Labels.SeatChoicesAreYours, StringComparison.Ordinal);
        Assert.Contains(Labels.YoursUntilTheBoardIsActive, Labels.ReopenAfterReviewIsYoursUntilTheBoard, StringComparison.Ordinal);

        // OFFERED, AND EACH PRESS WRITES THE SETTINGS ROW IT ALWAYS WROTE — one press, never armed first.
        var card = Field<OwnerPage.SeatChoices>(page, "_seatChoices");
        Assert.True(card.Root.IsVisible);
        host.Gateway.Update(s => s.SelectedRuntimeId = "codex");          // the AI tool chosen during setup
        card.Update();                                                    // its models: both rows follow the runtime
        Assert.Equal(host.ModelChoices.Count + 1, card.ModelRow.Children.Count);
        Assert.Equal(host.ModelChoices.Count + 1, card.ResearchModelRow.Children.Count);
        Press((Button)card.ResearchRuntimeRow.Children[1]);
        Assert.Equal(TradeAgent.AgentRuntime.ApiAgentRuntime.RuntimeId, host.Gateway.Settings.RoleRuntime[CouncilRoles.Research]);
        Press((Button)card.ResearchRuntimeRow.Children[0]);
        Assert.Equal("codex", host.RuntimeForRole(CouncilRoles.Research));
        if (host.ModelChoices.Count > 0)
        {
            Press((Button)card.ModelRow.Children[1]);
            Assert.Equal(host.ModelChoices[0].Model, host.Gateway.Settings.SelectedModelId);
            Press((Button)card.ResearchModelRow.Children[1]);
            Assert.Equal(host.ModelChoices[0].Model, host.Gateway.Settings.RoleModel[CouncilRoles.Research]);
        }
        Press((Button)card.ModelRow.Children[0]);
        Assert.Null(host.Gateway.Settings.SelectedModelId);
        Assert.False(card.Note.IsVisible);
        return Task.CompletedTask;
    });

    /// <summary>
    /// ONCE A BOARD SEAT IS ACTIVE THE SEAT CHOICES LEAVE HIS SURFACE (the twin): the card is hidden, and a press that arrives anyway —
    /// a half-read page, a stale tick — writes nothing and says the choice is the board's. A board that cannot be read counts as active.
    /// (No build stores an active seat before <c>U-board-b</c>, and <c>board_seat</c> is sealed, so the board's answer is handed in.)
    /// </summary>
    [Fact]
    public Task Seat_choices_are_not_offered_once_a_board_seat_is_active() => OnThePage((page, host) =>
    {
        var before = Json.Write(host.Gateway.Settings);
        var changed = 0;
        foreach (Func<bool> active in new Func<bool>[] { () => true, () => throw new InvalidOperationException("the board could not be read") })
        {
            var card = new OwnerPage.SeatChoices(host, () => { try { return active(); } catch (Exception) { return true; } }, () => changed++);
            card.Update();
            Assert.False(card.Root.IsVisible);

            Press((Button)card.ResearchRuntimeRow.Children[1]);
            Press((Button)card.ModelRow.Children[^1]);
            Press((Button)card.ResearchModelRow.Children[^1]);
            Assert.Equal(before, Json.Write(host.Gateway.Settings));
            Assert.Equal(0, changed);
            Assert.Equal(Labels.SeatChoicesAreTheBoards, card.Note.Text);
        }

        // AND THE PAGE ITSELF READS THE BOARD ONCE, FAILING CLOSED: the same reader the release uses.
        var owner = File.ReadAllText(Path.Combine(RepoRoot(), "src", "TradeAgent.App", "OwnerView.cs"));
        Assert.Contains("_seatChoices = new SeatChoices(_host, BoardActive, Refresh);", owner, StringComparison.Ordinal);
        Assert.Contains("catch (Exception) { return true; }", owner[owner.IndexOf("bool BoardActive()", StringComparison.Ordinal)..], StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    static T Field<T>(object page, string name) =>
        (T)page.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;

    static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>One standing closure held for review on <paramref name="account"/>, written as the gateway would have, in <paramref name="mode"/>.</summary>
    static TradeAgent.Gateway.LossBreachRecord SeedHold(TradeAgent.Core.Db.Database db, string connector, string account, TradingMode mode,
        string? symbol = null)
    {
        var at = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);
        var breach = new TradeAgent.Gateway.LossBreachRecord
        {
            Account = account, Connector = connector, Mode = mode, Day = "2026-10-20", Symbol = symbol,
            FirstSeenAt = at, ConfirmedAt = at, Loss = 1_200m, DayBudget = 1_000m, Currency = "USD"
        };
        db.SetKv(TradeAgent.Gateway.LossBreach.KeyFor(breach), Json.Write(breach));
        db.SetKv(TradeAgent.Gateway.LossHold.HoldKey(connector, breach), Json.Write(new TradeAgent.Gateway.LossHoldRecord
        {
            Account = account, Connector = connector, Mode = mode, Day = breach.Day, Symbol = symbol,
            BreachKey = TradeAgent.Gateway.LossBreach.KeyFor(breach), Episodes = ["2026-10-17 14:00 UTC", "2026-10-20 14:00 UTC"],
            StrikeWindowDays = 7, WindowFrom = "2026-10-14", At = at,
            Why = $"{symbol ?? "Your account"} reached its loss budget twice inside seven days, so it is held for you to look at."
        }));
        return breach;
    }

    /// <summary>
    /// THE OWNER'S RELEASE IS FOR PAPER SCOPES ONLY (Codex P1 on <c>U-settings-shed</c>; the orchestrator's ruling Q4: before an active
    /// board only paper can be held, and "every widening of a live limit or of real money stays the board's"). A hold on a live-mode scope —
    /// retained from before the board, the board's seats dormant — is not offered, and pressing the release twice anyway is refused in
    /// words and writes no release row: the gateway's <c>ReleaseHold</c> lifts every hold on the account at once, so the page never calls
    /// it while one of them is not paper. A paper hold beside it changes nothing; a paper hold alone is released.
    /// </summary>
    [Fact]
    public async Task The_owners_release_never_reaches_a_scope_that_is_not_paper()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host.Gateway;
            gw.Update(s => s.SelectedAccountId = "ACC-1");
            var live = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.LIVE_AUTONOMOUS);
            Assert.False(host.Board.AnyActive);
            Assert.NotEmpty(host.Board.Seats());
            Assert.All(host.Board.Seats(), s => Assert.Equal(TradeAgent.Core.Board.BoardSeatStatus.Dormant, s.Status));
            Assert.Single(gw.HoldsForReview());

            var page = Build(host);
            page.Update(await gw.StatusAsync());
            var release = Field<Button>(page, "_release");
            Field<TextBox>(page, "_releaseNote").Text = "looked at the live account";
            Press(release);
            Press(release);

            // NO RELEASE ROW: the live hold stands, and the page said why.
            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, live)));
            Assert.Single(gw.HoldsForReview());
            Assert.Equal(Labels.ReleaseRefusedNotPaper(["your account"]), Field<TextBlock>(page, "_releaseNoteResult").Text);
            Assert.False(Field<Avalonia.Controls.Border>(page, "_releaseCard").IsVisible);

            // A PAPER HOLD BESIDE IT: offered (it is paper), and the press still refuses — the release cannot take one without the other.
            var paper = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.PAPER, symbol: "BTCUSDT");
            page.Update(await gw.StatusAsync());
            Assert.True(Field<Avalonia.Controls.Border>(page, "_releaseCard").IsVisible);
            Field<TextBox>(page, "_releaseNote").Text = "looked at both";
            Press(release);
            Press(release);
            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, live)));
            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, paper)));
            Assert.Equal(Labels.ReleaseRefusedNotPaper(["your account"]), Field<TextBlock>(page, "_releaseNoteResult").Text);
        }
        finally { await host.Gateway.DisposeAsync(); }

        // A PAPER HOLD ALONE IS RELEASED, through the gateway's own release.
        using var db2 = TestEnv.NewDb();
        var host2 = AppHost.Composed(db2, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host2.Gateway;
            gw.Update(s => s.SelectedAccountId = "ACC-1");
            var paper = SeedHold(db2, gw.Connector.Id, "ACC-1", TradingMode.PAPER, symbol: "BTCUSDT");
            var page = Build(host2);
            page.Update(await gw.StatusAsync());
            Assert.True(Field<Avalonia.Controls.Border>(page, "_releaseCard").IsVisible);
            Field<TextBox>(page, "_releaseNote").Text = "looked at the paper run";
            var release = Field<Button>(page, "_release");
            Press(release);
            Press(release);
            Assert.NotNull(db2.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, paper)));
            Assert.Empty(gw.HoldsForReview());
        }
        finally { await host2.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// A LIVE HOLD WHOSE ROW CANNOT BE READ IS NOT PAPER (Codex re-review of fix pass 1, P1): the gateway answers an unreadable hold as
    /// HELD and stamps it with the mode in force now — Practice — so the hold's own mode cannot be trusted alone. Paper is judged by the
    /// BREACH it holds as well, and a live breach under a malformed hold is refused in words and writes no release row.
    /// </summary>
    [Fact]
    public async Task An_unreadable_hold_over_a_live_breach_is_never_released_by_the_owner()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host.Gateway;
            gw.Update(s => s.SelectedAccountId = "ACC-1");
            var live = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.LIVE_AUTONOMOUS);
            db.SetKv(TradeAgent.Gateway.LossHold.HoldKey(gw.Connector.Id, live), "{ this is not a hold");
            Assert.False(host.Board.AnyActive);
            var held = Assert.Single(gw.HoldsForReview());
            Assert.Equal(TradingMode.PAPER, held.Mode);            // the gateway's stamp: the mode in force, not the breach's

            var page = Build(host);
            page.Update(await gw.StatusAsync());
            Assert.False(Field<Avalonia.Controls.Border>(page, "_releaseCard").IsVisible);
            Field<TextBox>(page, "_releaseNote").Text = "looked at it";
            var release = Field<Button>(page, "_release");
            Press(release);
            Press(release);

            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, live)));
            Assert.Equal(Labels.ReleaseRefusedNotPaper(["your account"]), Field<TextBlock>(page, "_releaseNoteResult").Text);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }
    /// <summary>
    /// A PAPER HOLD FILED UNDER A LIVE BREACH IS NEVER RELEASED (Codex re-review 2 of <c>U-settings-shed</c>, P1): the gateway releases
    /// the breach a hold is FILED under, so the page judges that breach — its mode, and whether the hold names exactly it — never the
    /// breach the hold's own text claims. A paper hold's row copied under a live breach's key is a mismatch, refused in words, and no
    /// release row is written for either scope.
    /// </summary>
    [Fact]
    public async Task A_paper_hold_filed_under_a_live_breach_is_never_released_by_the_owner()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host.Gateway;
            gw.Update(s => s.SelectedAccountId = "ACC-1");
            var paper = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.PAPER, symbol: "BTCUSDT");
            var live = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.LIVE_AUTONOMOUS);
            db.SetKv(TradeAgent.Gateway.LossHold.HoldKey(gw.Connector.Id, live),
                db.GetKv(TradeAgent.Gateway.LossHold.HoldKey(gw.Connector.Id, paper))!);
            Assert.False(host.Board.AnyActive);

            var page = Build(host);
            page.Update(await gw.StatusAsync());
            Field<TextBox>(page, "_releaseNote").Text = "looked at it";
            var release = Field<Button>(page, "_release");
            Press(release);
            Press(release);

            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, live)));
            Assert.Null(db.GetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, paper)));
            Assert.Equal(Labels.ReleaseRefusedMismatch(["your account"]), Field<TextBlock>(page, "_releaseNoteResult").Text);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE RESEARCH DIRECTOR'S CLI PRESS SELECTS THE CLI EVEN WITH A WORKER KEY HELD (Codex re-review of fix pass 1, P2): clearing the
    /// override would fall back to the harness the moment a key is held, so the press writes the app-wide runtime as the role's own.
    /// </summary>
    [Fact]
    public Task The_research_cli_press_selects_the_cli_while_a_worker_key_is_held() => OnThePage((page, host) =>
    {
        // THE RULE, NOT THE SHARED KEY: HarnessKey.Shared is process-wide, so a test that held it would leak into every other class.
        // What the press writes is fed to AppHost's own static rule with a key held.
        host.Gateway.Update(s => s.SelectedRuntimeId = "codex");
        var card = Field<OwnerPage.SeatChoices>(page, "_seatChoices");
        string? Resolved() => AppHost.RuntimeForRole(CouncilRoles.Research,
            host.Gateway.Settings.RuntimeForRole(CouncilRoles.Research), host.Gateway.Settings.SelectedRuntimeId, keyHeld: true);

        Press((Button)card.ResearchRuntimeRow.Children[1]);
        Assert.True(TradeAgent.AgentRuntime.RuntimeCatalog.IsHarness(Resolved()));
        Press((Button)card.ResearchRuntimeRow.Children[0]);
        Assert.Equal("codex", Resolved());
        // WHAT THE PRESS STORES IS THE FACT, NOT A COPY OF THE CLI'S ID (U-role-follows-setup).
        Assert.Equal(TradeAgentSettings.SetupRuntime, host.Gateway.Settings.RuntimeForRole(CouncilRoles.Research));
        return Task.CompletedTask;
    });

    /// <summary>
    /// A RELEASE ROW THAT CANNOT BE READ IS NO RELEASE, ON THE CARD AS IN THE GATEWAY (Codex re-review of fix pass 2, P2): the gateway
    /// answers a malformed release row as no release and keeps the hold, so the owner's card keeps showing that held paper scope too.
    /// </summary>
    [Fact]
    public async Task A_paper_hold_beside_an_unreadable_release_row_stays_on_the_card()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host.Gateway;
            gw.Update(s => s.SelectedAccountId = "ACC-1");
            var paper = SeedHold(db, gw.Connector.Id, "ACC-1", TradingMode.PAPER, symbol: "BTCUSDT");
            db.SetKv(TradeAgent.Gateway.LossHold.ReleaseKey(gw.Connector.Id, paper), "{ this is not a release");
            Assert.Single(gw.HoldsForReview());

            var page = Build(host);
            page.Update(await gw.StatusAsync());
            Assert.True(Field<Avalonia.Controls.Border>(page, "_releaseCard").IsVisible);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE RESEARCH DIRECTOR'S CLI CHOICE FOLLOWS THE SETUP'S CLI (Codex re-review 2 of <c>U-settings-shed</c>, P2): chosen under Codex,
    /// then the setup switched to another CLI by a plain write of the setting, Research resolves to the newly prepared CLI — the one its turns
    /// run on — never the old one (U-role-follows-setup: the row stores the fact, so no writer keeps rows true).
    /// </summary>
    [Fact]
    public Task The_research_cli_choice_follows_the_setups_cli_when_it_is_switched() => OnThePage((page, host) =>
    {
        host.Gateway.Update(s => s.SelectedRuntimeId = "codex");
        var card = Field<OwnerPage.SeatChoices>(page, "_seatChoices");
        Press((Button)card.ResearchRuntimeRow.Children[0]);

        host.Gateway.Update(s => s.SelectedRuntimeId = "opencode");     // ANY writer of the setup's CLI: no helper, no row rewritten

        Assert.Equal("opencode", AppHost.RuntimeForRole(CouncilRoles.Research,
            host.Gateway.Settings.RuntimeForRole(CouncilRoles.Research), host.Gateway.Settings.SelectedRuntimeId, keyHeld: true));
        Assert.Equal("opencode", host.RuntimeForRole(CouncilRoles.Research));
        return Task.CompletedTask;
    });

    /// <summary>
    /// THE PAGE READS THE SENTINEL AS THE SETUP'S CLI (<c>U-role-follows-setup</c>): after the CLI press — and for a literal row naming the
    /// setup's CLI alike — the CLI button is the emphasised one and the Activity line names the resolved CLI, never the word the row stores.
    /// </summary>
    [Fact]
    public async Task The_research_card_emphasises_the_cli_and_names_it_for_the_sentinel_and_a_literal_id_alike()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            host.Gateway.Update(s => s.SelectedRuntimeId = "codex");
            var card = Field<OwnerPage.SeatChoices>(Build(host), "_seatChoices");
            bool CliOn() => ((Button)card.ResearchRuntimeRow.Children[0]).Classes.Contains("on");
            bool OwnOn() => ((Button)card.ResearchRuntimeRow.Children[1]).Classes.Contains("on");
            List<string> Activity()
            {
                using var c = db.Cmd("SELECT text FROM activity ORDER BY id");
                using var r = c.ExecuteReader();
                var l = new List<string>();
                while (r.Read()) l.Add(r.GetString(0));
                return l;
            }

            Press((Button)card.ResearchRuntimeRow.Children[1]);
            Assert.True(OwnOn() && !CliOn());

            Press((Button)card.ResearchRuntimeRow.Children[0]);
            Assert.Equal(TradeAgentSettings.SetupRuntime, host.Gateway.Settings.RuntimeForRole(CouncilRoles.Research));
            Assert.True(CliOn() && !OwnOn());
            Assert.Contains("The Research Director now runs on codex", Activity());
            Assert.DoesNotContain(Activity(), a => a.Contains(TradeAgentSettings.SetupRuntime));

            // A LITERAL ROW NAMING THE SETUP'S CLI READS THE SAME: the page asks the resolver, not the row.
            host.Gateway.Update(s => s.RoleRuntime[CouncilRoles.Research] = "codex");
            card.Update();
            Assert.True(CliOn() && !OwnOn());
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// TODAY'S LINE PROMISES MIDNIGHT ONLY WHEN A FRESH DAY CAN FUND ONE TURN (<c>U-billing-classes</c>' review, P2): a reached day whose
    /// next turn fits a whole day waits for midnight; one whose single turn sets aside more than the whole day — or whose limit is 0 —
    /// says midnight will not bring a turn back, and names what would: his own limit for real money, the board's day-total for the plan.
    /// </summary>
    [Fact]
    public void Todays_line_promises_midnight_only_when_a_fresh_day_can_fund_one_turn()
    {
        AiSpendToday Day(decimal cap, decimal spent, decimal turn, string cls) => new()
        {
            Metered = true, Cap = cap, Spent = spent, NextTurnReservation = turn, Currency = "USD", Class = cls
        };

        var (money, _) = OwnerPage.TodayLines(Day(10m, 10m, 2m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.EndsWith(" — reached; the next turn of this kind waits for midnight.", money, StringComparison.Ordinal);

        (money, _) = OwnerPage.TodayLines(Day(1m, 0m, 2m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.DoesNotContain("waits for midnight", money, StringComparison.Ordinal);
        Assert.Contains("midnight will not bring a turn back", money, StringComparison.Ordinal);
        Assert.EndsWith(Labels.TodayMoneyRepair, money, StringComparison.Ordinal);

        (money, _) = OwnerPage.TodayLines(Day(0m, 0m, 0m, TradeAgent.Core.Db.BillingClasses.Key));
        Assert.Contains("a limit of 0 admits no turn of this kind on any day", money, StringComparison.Ordinal);

        var plan = Day(40m, 0m, 50m, TradeAgent.Core.Db.BillingClasses.Plan);
        var (_, planLine) = OwnerPage.TodayLines(Day(10m, 0m, 2m, TradeAgent.Core.Db.BillingClasses.Key) with { PlanReading = plan });
        Assert.Contains("midnight will not bring a turn back", planLine, StringComparison.Ordinal);
        Assert.EndsWith(Labels.TodayPlanRepair, planLine, StringComparison.Ordinal);
        Assert.Contains("security board", planLine, StringComparison.Ordinal);
    }
}

/// <summary>
/// AN EARLIER ARMED HOME, IN THE APP (<c>U-settings-shed</c> item 6; R24 § 4.5; CLAUDE.md "refuse, never guess" and "the first change that
/// refuses older homes ships that in-app path with it"): a home whose settings an earlier build left armed for real money is refused in
/// the gateway's own sentence — both versions and the repair — on the owner's page, and ONE press sets the earlier setting aside (a
/// narrowing, like stop). Nothing is converted and nothing is touched until he presses; no terminal anywhere.
/// </summary>
public class EarlierArmedHomeTests
{
    static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    static readonly TradeAgent.Core.Board.EarlierLiveSetting Armed = new("LIVE_AUTONOMOUS", "true", 46,
        Labels.HomeArmedBeforeTheBoard("a live mode and real money switched on", 46, 47));

    [Fact]
    public void An_earlier_armed_home_is_refused_in_the_app_and_one_press_sets_its_live_setting_aside()
    {
        TradeAgent.Core.Board.EarlierLiveSetting? earlier = Armed;
        var pressed = 0;
        var card = new OwnerPage.EarlierArmedHome(() => earlier, () => { pressed++; earlier = null; });
        card.Update();

        Assert.True(card.Card.IsVisible);
        Assert.Equal(Armed.Sentence, card.Sentence.Text);
        Assert.Contains("schema 46", card.Sentence.Text, StringComparison.Ordinal);
        Assert.Contains("schema 47", card.Sentence.Text, StringComparison.Ordinal);
        Assert.Equal(Labels.SetAsideEarlierLiveSetting, card.Press.Content);

        // ONE PRESS: a narrowing, never armed first.
        Press(card.Press);
        Assert.Equal(1, pressed);
        Assert.False(Ui.IsArmed(card.Press));
        Assert.False(card.Card.IsVisible);
        Assert.Equal(Labels.EarlierLiveSettingSetAside, card.Note.Text);
        Assert.True(card.Note.IsVisible);

        // AND THE WINDOW WIRES THE GATEWAY'S OWN READING AND ITS ONE PRESS (since fix pass 1: a strip above every page), nowhere else.
        var root = Repo();
        var window = File.ReadAllText(Path.Combine(root, "src", "TradeAgent.App", "MainWindow.cs"));
        Assert.Contains("new(() => host.Gateway.EarlierLiveSetting, () => host.Gateway.SetAsideEarlierLiveSetting())",
            window, StringComparison.Ordinal);
        Assert.Equal(1, Directory.GetFiles(Path.Combine(root, "src", "TradeAgent.App"), "*.cs")
            .Sum(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"SetAsideEarlierLiveSetting\(\)").Count));
    }

    [Fact]
    public void An_earlier_armed_home_is_left_untouched_until_the_press()
    {
        TradeAgent.Core.Board.EarlierLiveSetting? earlier = Armed;
        var pressed = 0;
        var card = new OwnerPage.EarlierArmedHome(() => earlier, () => pressed++);

        for (var i = 0; i < 5; i++) card.Update();           // the five-second pass, five times over
        Assert.Equal(0, pressed);
        Assert.True(card.Card.IsVisible);
        Assert.Equal(Armed, earlier);

        // A HOME NOTHING ARMED SHOWS NO CARD AND OFFERS NO PRESS.
        var none = new OwnerPage.EarlierArmedHome(() => null, () => pressed++);
        none.Update();
        Assert.False(none.Card.IsVisible);
        Assert.Equal(0, pressed);

        // A PRESS THE GATEWAY REFUSES IS SAID, AND THE CARD STAYS.
        var refused = new OwnerPage.EarlierArmedHome(() => Armed, () => throw new InvalidOperationException("the row could not be kept"));
        refused.Update();
        Press(refused.Press);
        Assert.True(refused.Card.IsVisible);
        Assert.Contains("the row could not be kept", refused.Note.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN EARLIER ARMED HOME IS REFUSED WHERE THE APP OPENS (Codex P2 on <c>U-settings-shed</c>): the app lands on Chat, so a refusal that
    /// lives on one page is a refusal nobody sees. The gateway's sentence and its one press are a strip of the window's own, above every
    /// page and updated before anything else on the tick; and the header's AI line names the refusal even while AI trading is stopped —
    /// "AI trading stopped" must not stand in for "every order is refused until you set the earlier setting aside".
    /// </summary>
    [Fact]
    public async Task An_earlier_armed_home_is_refused_where_the_app_opens()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("settings", "{\"mode\":\"LIVE_AUTONOMOUS\",\"live_activated\":true,\"ai_trading_stopped\":true}");
        new TradeAgent.Core.Db.OnboardingStore(db).Complete(OnboardingStep.SETUP_COMPLETE);
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            var gw = host.Gateway;
            Assert.True(host.Onboarding.IsComplete());
            Assert.True(gw.Settings.AiTradingStopped);
            var earlier = Assert.IsType<TradeAgent.Core.Board.EarlierLiveSetting>(gw.EarlierLiveSetting);

            // THE WINDOW'S OWN STRIP, outside the page host, built with the shell and updated first on every tick.
            var window = File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "MainWindow.cs"));
            var shell = window[window.IndexOf("void EnsureShell()", StringComparison.Ordinal)..window.IndexOf("Control BuildHeader()", StringComparison.Ordinal)];
            Assert.Contains("_shellSurface.Children.Add(Ui.With(BuildEarlierArmedStrip(), c => c[Grid.RowProperty] = 1));", shell, StringComparison.Ordinal);
            Assert.True(shell.IndexOf("BuildEarlierArmedStrip()", StringComparison.Ordinal) < shell.IndexOf("Select(Page.Chat);", StringComparison.Ordinal));
            var update = window[window.IndexOf("void UpdateShell(GatewayStatus status)", StringComparison.Ordinal)..];
            Assert.Contains("_earlierArmed?.Update();", update[..update.IndexOf("_modePillHost", StringComparison.Ordinal)], StringComparison.Ordinal);
            // AND ONE STRIP ONLY: the owner's page no longer builds a card of its own.
            Assert.DoesNotContain("new EarlierArmedHome(", File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "OwnerView.cs")),
                StringComparison.Ordinal);

            // THE STRIP OVER THIS HOME: the gateway's own sentence and the one press, visible at once.
            var strip = MainWindow.EarlierArmedStrip(host);
            strip.Update();
            Assert.True(strip.Card.IsVisible);
            Assert.Equal(earlier.Sentence, strip.Sentence.Text);
            Assert.Equal(Labels.SetAsideEarlierLiveSetting, strip.Press.Content);
            Assert.True(strip.Press.IsEnabled);

            // THE HEADER NEVER HIDES IT BEHIND "STOPPED".
            var status = await gw.StatusAsync();
            Assert.True(status.AiTradingStopped);
            var line = MainWindow.AiLine(status, gw.EarlierLiveSetting);
            Assert.Equal(Labels.HeaderEarlierLiveSetting, line.Text);
            Assert.Equal(MainWindow.AiTone.Refused, line.Tone);
            Assert.Equal("AI trading stopped", MainWindow.AiLine(status, null).Text);

            // ONE PRESS, AND IT IS GONE FROM EVERY PAGE.
            strip.Press.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null(gw.EarlierLiveSetting);
            Assert.False(strip.Card.IsVisible);
            Assert.Equal("AI trading stopped", MainWindow.AiLine(await gw.StatusAsync(), gw.EarlierLiveSetting).Text);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
