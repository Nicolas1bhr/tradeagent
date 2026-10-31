using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using TradeAgent.App;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BOARD PAGE (<c>U-settings-shed</c> item 1; R24 § 4.7): every decision of the security board in force, in words, with the version
/// that set it — a founding value labelled the app's, never the owner's — read through <see cref="BoardReader"/> and nothing that writes,
/// built once and updated in place.
/// </summary>
public class BoardPageTests
{
    static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>A policy whose standing paper envelope is set with an end, so its seven terms are flattened too.</summary>
    static BoardPolicy WithEnvelope() => BoardFounding.V1 with
    {
        PaperEnvelopes = new Dictionary<string, PaperEnvelopeTerms>(StringComparer.Ordinal)
        {
            ["BTCUSDT"] = new("paper", "acct", "BTCUSDT", 1m, 100m, 2, 30)
        }
    };

    static IEnumerable<string> PathsOf(BoardPolicy policy) =>
        BoardPolicyBody.Flatten(BoardPolicyBody.Canonical(policy)).Select(f => f.Path);

    /// <summary>
    /// EVERY POLICY KEY HAS A PLAIN LABEL: each field the founding policy holds — and each of the envelope's terms, which appear only
    /// while one stands — has words on the page, and no label is a path in disguise. And the other way: no label waits for a key the
    /// policy cannot hold. A key with no words is refused on the page, never shown as a decision (the page test below).
    /// </summary>
    [Fact]
    public void Every_policy_key_has_a_plain_label()
    {
        var paths = PathsOf(BoardFounding.V1).Concat(PathsOf(WithEnvelope())).Concat(PathsOf(BoardFounding.V1 with { PaperEnvelopes = new Dictionary<string, PaperEnvelopeTerms>() }))
            .Distinct().ToList();
        Assert.Contains("mode", paths);
        Assert.Contains("risk.max_order_quantity", paths);
        Assert.Contains("paper_envelopes", paths);
        foreach (var (pair, _) in BoardFounding.PaperPairs) Assert.Contains($"paper_envelopes.{pair}.max_notional", paths);

        foreach (var path in paths)
        {
            var words = Labels.BoardField(path);
            Assert.True(words is not null, $"the policy key “{path}” has no words on the Board page");
            Assert.DoesNotContain("_", words!.Label);
            Assert.DoesNotContain(".", words.Label);
            Assert.False(string.IsNullOrWhiteSpace(words.Group));
        }
        foreach (var words in Labels.BoardFields)
            Assert.True(paths.Contains(words.Path), $"the label “{words.Label}” waits for “{words.Path}”, which no policy holds");

        // THE PAGE REFUSES A KEY WITH NO WORDS: named in a caution line, and shown nowhere as a value.
        Assert.Null(Labels.BoardField("a_key_no_build_has"));
        Assert.Contains("“a_key_no_build_has”", Labels.BoardFieldUnnamed(["a_key_no_build_has"]), StringComparison.Ordinal);
        Assert.Contains("not shown here as a decision", Labels.BoardFieldUnnamed(["x"]), StringComparison.Ordinal);
    }

    /// <summary>
    /// A FOUNDING VALUE IS LABELLED THE APP'S, NEVER THE OWNER'S (R24 § 4.5; honesty): on a fresh home every row the page shows reads
    /// "founding (the app's)", the decider is the app, and nothing on the page says the owner set it.
    /// </summary>
    [Fact]
    public void A_founding_value_is_labelled_the_apps_never_the_owners()
    {
        using var db = TestEnv.NewDb();
        var page = new BoardPage(new BoardReader(db));
        var inForce = new BoardReader(db).InForce();
        Assert.False(inForce.CouldNotBeRead);

        var rows = Rows(page);
        Assert.Equal(inForce.Fields.Count, rows.Count(r => r.Root.IsVisible));
        foreach (var field in inForce.Fields)
        {
            var row = rows.Single(r => r.Path == field.Name);
            Assert.True(row.Root.IsVisible, field.Name);
            Assert.StartsWith("founding (the app's)", row.Source.Text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Text), field.Name);
        }

        var text = AllText(page);
        Assert.Contains(Labels.BoardDecider(BoardDecider.App), text);
        foreach (var claim in new[] { "set by you", "you set", "your choice", "you decided", "chosen by you" })
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);

        // THE VALUES ARE THE POLICY'S OWN: the mode in its words, the plan's figure as written and never called money.
        Assert.Equal(Ui.ModeLabel(TradingMode.PAPER), rows.Single(r => r.Path == "mode").Value.Text);
        Assert.Equal("off — no real money", rows.Single(r => r.Path == "live_activated").Value.Text);
        var plan = inForce.Fields.Single(f => f.Name == "plan_day_total").Value;
        Assert.Equal($"{plan} a day, counted at list price — not money", rows.Single(r => r.Path == "plan_day_total").Value.Text);
    }

    /// <summary>
    /// THE BOARD PAGE HOLDS NO PRESS THAT WRITES (AUTH-001): it is built from a <see cref="BoardReader"/> alone — no host, no gateway, no
    /// store, no database it could write through — and the only press in its tree opens the version history, which reads.
    /// </summary>
    [Fact]
    public void The_board_page_holds_no_press_that_writes()
    {
        var ctor = Assert.Single(typeof(BoardPage).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Equal([typeof(BoardReader)], ctor.GetParameters().Select(p => p.ParameterType));

        Type[] writers = [typeof(AppHost), typeof(TradeAgent.Gateway.TradingGateway), typeof(BoardStore), typeof(Database)];
        foreach (var f in typeof(BoardPage).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            Assert.DoesNotContain(f.FieldType, writers);

        using var db = TestEnv.NewDb();
        var page = new BoardPage(new BoardReader(db));
        var buttons = page.Root.GetLogicalDescendants().OfType<Button>().ToList();
        var only = Assert.Single(buttons);
        Assert.Equal(Labels.ShowBoardHistory, only.Content);

        Press(only);
        Assert.Equal(Labels.HideBoardHistory, only.Content);
        Assert.Contains(Labels.BoardHistoryFoundingOnly, AllText(page));
        Assert.Contains(Labels.BoardRationale(null), AllText(page));
        Assert.Contains(Labels.BoardScore(null), AllText(page));
        Assert.Single(page.Root.GetLogicalDescendants().OfType<Button>());
    }

    /// <summary>
    /// THE BOARD PAGE IS BUILT ONCE AND UPDATED IN PLACE (CLAUDE.md): the five-second pass writes text into the controls built at
    /// construction and adds or removes none — the rows, the seats and an opened history are the same objects after three passes.
    /// </summary>
    [Fact]
    public void The_board_page_is_built_once_and_updated_in_place()
    {
        using var db = TestEnv.NewDb();
        var page = new BoardPage(new BoardReader(db));
        Press(page.Root.GetLogicalDescendants().OfType<Button>().Single());

        var before = page.Root.GetLogicalDescendants().ToList();
        var texts = before.OfType<TextBlock>().Select(t => t.Text).ToList();
        for (var i = 0; i < 3; i++) page.Update();
        var after = page.Root.GetLogicalDescendants().ToList();

        Assert.Equal(before.Count, after.Count);
        Assert.True(before.Zip(after).All(p => ReferenceEquals(p.First, p.Second)), "a pass replaced a control");
        Assert.Equal(texts, after.OfType<TextBlock>().Select(t => t.Text).ToList());

        // THE SEATS: one line each, the founding composition, dormant in words.
        var text = AllText(page);
        foreach (var seat in BoardFounding.Seats) Assert.Contains(seat.Id, text);
        Assert.Contains(Labels.BoardSeatStatus(BoardSeatStatus.Dormant), text);
        Assert.Contains(Labels.BoardSeatsDormant, text);
    }

    sealed record RowView(string Path, Control Root, TextBlock Value, TextBlock Source);

    static List<RowView> Rows(BoardPage page)
    {
        var rows = (System.Collections.IDictionary)typeof(BoardPage).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        var list = new List<RowView>();
        foreach (System.Collections.DictionaryEntry e in rows)
        {
            var t = e.Value!.GetType();
            list.Add(new RowView((string)e.Key, (Control)t.GetProperty("Root")!.GetValue(e.Value)!,
                (TextBlock)t.GetProperty("Value")!.GetValue(e.Value)!, (TextBlock)t.GetProperty("Source")!.GetValue(e.Value)!));
        }
        return list;
    }

    static string AllText(BoardPage page) =>
        string.Join("\n", page.Root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    static string Change(string field, object from, object to) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    /// <summary>
    /// (U-org-surface-b 3) EVERY BOARD ACT SHOWS ITS HISTORY, RATIONALE, DECIDERS, CLASS AND SCORE (R24 § 4.7): a raise proposed by one
    /// seat with a forecast, confirmed by another, its forecast resolved by the app's sweep — the Board page's card for it says its
    /// status, the intent it is bound to, each event with its seat (proposer and confirmer), their vendor families and that they are
    /// one, the version it wrote; both rationales as their seats' quoted words; "Evidence class: none yet"; the forecast as the
    /// proposer's claim with the app's resolution; and an audit score not yet given. The page still holds only its history press, and
    /// a tick keeps the card. RED at base: the page had no acts.
    /// </summary>
    [Fact]
    public void Every_board_act_shows_history_rationale_deciders_class_and_score()
    {
        using var db = TestEnv.NewDb();
        var board = new BoardStore(db);
        foreach (var seat in new[] { "board-1", "board-2" }) board.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var forecast = new ForecastClaim(ForecastVocabulary.UnitAdmissible, OrgIds.Research, ForecastVocabulary.NoneAdmitted, now.AddDays(1), 0.8m);
        var act = board.Propose(new BoardActRequest("board-1", null, Change("plan_day_total", 40, 50), "the plan runs short by noon", [], [forecast]), now);
        var confirmed = board.Decide(act.Act.Id, "board-2", null, true, act.Act.IntentSha256, "agreed: the noon stop costs a review", now);
        Assert.Equal(1, new ForecastResolver(db).Sweep(now.AddDays(2)).Resolved);

        var page = new BoardPage(new BoardReader(db));
        var card = page.Acts.CardOf(act.Act.Id)!;
        var text = string.Join("\n", card.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

        Assert.Contains(Labels.BoardActStatus("confirmed"), text, StringComparison.Ordinal);
        Assert.Contains(act.Act.Intent, text, StringComparison.Ordinal);
        Assert.Contains("Proposed by board-1", text, StringComparison.Ordinal);
        Assert.Contains("Confirmed by board-2", text, StringComparison.Ordinal);
        Assert.Contains("proposer's vendor family openai, decider's openai — the same family", text, StringComparison.Ordinal);
        Assert.Contains($"it wrote the policy's version {confirmed.PolicyVersion}", text, StringComparison.Ordinal);
        Assert.Contains("board-1 wrote: “the plan runs short by noon”", text, StringComparison.Ordinal);
        Assert.Contains("board-2 wrote: “agreed: the noon stop costs a review”", text, StringComparison.Ordinal);
        Assert.Contains(Labels.BoardActEvidenceClass(null), text, StringComparison.Ordinal);
        Assert.Equal("Evidence class: none yet", Labels.BoardActEvidenceClass(null));
        Assert.Contains($"board-1 wrote: “unit-admissible of {OrgIds.Research}: none by", text, StringComparison.Ordinal);
        Assert.Contains("Resolved yes", text, StringComparison.Ordinal);
        Assert.Contains(Labels.BoardActAuditScore, text, StringComparison.Ordinal);

        page.Update();
        Assert.Same(card, page.Acts.CardOf(act.Act.Id));
        Assert.Single(page.Root.GetLogicalDescendants().OfType<Button>());
    }

    /// <summary>
    /// (U-org-surface-b fix pass 1; Codex P2) A CHIEF ACT NEVER CLAIMS TO HAVE WRITTEN A POLICY VERSION: the board appoints a chief by two
    /// seats — a proposal and a confirmation of <c>org.chief</c> — which writes no version of the policy, and whose confirmed event names
    /// the version in force it was DECIDED UNDER (U-org-chief, the orchestrator's ruling). Its history on the Board page says "decided
    /// under version N; no policy version written" and never "wrote the policy's version"; a policy act's line still says it wrote one
    /// (<see cref="Every_board_act_shows_history_rationale_deciders_class_and_score"/>). RED at base: every non-null event version was
    /// worded as written. THE WATCHED MUTANT: the label's chief branch removed turns this red.
    /// </summary>
    [Fact]
    public void A_chief_acts_history_says_the_version_it_was_decided_under_and_writes_none()
    {
        using var db = TestEnv.NewDb();
        var board = new BoardStore(db) { KnownRuntime = runtime => runtime == "puppet" };
        foreach (var seat in new[] { "board-1", "board-2" }) board.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var intent = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object?>
            {
                ["field"] = BoardDirection.Chief, ["from"] = null,
                ["to"] = new Dictionary<string, string> { ["runtime"] = "puppet", ["model"] = "scripted-1" }
            }
        });
        var underVersion = new BoardReader(db).InForce().Version;
        var act = board.Propose(new BoardActRequest("board-1", null, intent, "the organisation needs a head", [], []), now);
        var confirmed = board.Decide(act.Act.Id, "board-2", null, true, act.Act.IntentSha256, "agreed: a dormant chief costs nothing", now);
        Assert.Equal((BoardActEventKind.Confirmed, (int?)null), (confirmed.Status, confirmed.PolicyVersion));
        Assert.Equal(underVersion, new BoardReader(db).InForce().Version);
        Assert.Equal(underVersion, board.Events(act.Act.Id).Single(e => e.Kind == BoardActEventKind.Confirmed).PolicyVersion);

        var page = new BoardPage(new BoardReader(db));
        var text = string.Join("\n", page.Acts.CardOf(act.Act.Id)!.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        var line = text.Split('\n').Single(l => l.StartsWith("Confirmed by board-2", StringComparison.Ordinal));
        Assert.Contains($"; decided under version {underVersion}; no policy version written", line, StringComparison.Ordinal);
        Assert.DoesNotContain("wrote the policy's version", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// (U-org-surface-b 6) VENTURES SHOW A NAMED EMPTY STATE (R24 § 4.6): no venture exists in this build, so the page says so in words
    /// — "No venture yet — the board approves them once they exist" — and draws no empty frame. On the Board page: the Organisation
    /// page's file is U-org-chief's under the seat's file seam. RED at base: no ventures section.
    /// </summary>
    [Fact]
    public void Ventures_show_a_named_empty_state()
    {
        using var db = TestEnv.NewDb();
        var page = new BoardPage(new BoardReader(db));
        Assert.Equal("No venture yet — the board approves them once they exist", Labels.VenturesNone);
        Assert.Contains(Labels.VenturesNone, AllText(page));
        Assert.Contains(Labels.Ventures.ToUpperInvariant(), AllText(page));
    }
}
