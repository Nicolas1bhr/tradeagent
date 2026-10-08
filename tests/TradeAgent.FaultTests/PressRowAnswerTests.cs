using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A PRESS'S OWN CLOSE THE OWNER ANSWERED "STILL WORKING", WHOSE PLATFORM UPDATE IS THEN LOST, CAN BE ANSWERED AGAIN — AND
/// ONE THE PLATFORM STILL LISTS LIVE IS NEVER ANSWERED AWAY (<c>U-press-row-answer</c>).
///
/// <para><c>U-press-close-once</c> made the owner's Close all wait on another press's own close in flight: it never asks
/// about it, cancels it or closes beside it. Its residual (c): the loss flatten's close rests <c>WORKING</c>, the owner
/// answers it on the card "Our record is right — it is working", which clears its flag, and the platform then cancels it
/// without the update ever arriving. Nothing settles a press's row — the reconciler, the in-flight sweep and the press's
/// own settle all leave it to the owner — and the card offered only that one answer for it, and only while it was flagged:
/// every Close all waited on it for good, and it was on no card.</para>
///
/// <para>Now the owner's Close all puts such a row back on the card once every leg has written its rows, and the card
/// offers a press's own live row "It is no longer working at your platform" and "It was filled" beside its own answer —
/// under the platform's live-order veto wherever its order history can be asked, so a close it still lists live is never
/// answered away, and a Close all never sends a close beside it.</para>
///
/// <para>Over <see cref="InFlightSettleTests"/>' harness and <see cref="PressCloseOnceTests"/>' flatten fixture —
/// <see cref="RecordingConnector"/> and the built-in simulator, on a gateway clock the test moves — with the press budget
/// sized by its legs: every verdict is the book and the records. Every answer goes through the card's one route,
/// <see cref="TradingGateway.AnswerFromTheCardAsync"/>. No venue is reached and no real money is involved.</para>
/// </summary>
public class PressRowAnswerTests(ITestOutputHelper log)
{
    static readonly AgentContext Ai = new("ai");

    /// <summary>
    /// THE LOSS FLATTEN'S CLOSE RESTS, AND THE OWNER ANSWERS IT STILL WORKING: a long 2 ES, a closure recorded for today and
    /// the health pass that flattens it, its close left <c>WORKING</c> at the platform — and then every row of the flatten
    /// answered on the card with its own state, as "Our record is right — it is working" does. The close is then unflagged,
    /// still <c>WORKING</c>, and on no card.
    /// </summary>
    async Task<(InFlightSettleTests.Harness H, ExecutionRequest Flatten)> AFlattenCloseAnsweredStillWorking(string prefix,
        bool hideHistory = false)
    {
        var h = await PressCloseOnceTests.Ready(legs: 1);
        h.C.Faults.HideOrderHistory = hideHistory;
        await h.Gw.PlaceAsync(Ai, $"{prefix}-open", TestEnv.Buy("ES", 2m));

        h.C.Faults.Fill = FillBehaviour.LeaveWorking;
        await PressCloseOnceTests.AClosureIsRecordedAndFlattened(h);
        h.C.Faults.Fill = FillBehaviour.FillImmediately;
        var flatten = Assert.Single(h.Gw.Requests.Query("request_id LIKE $p AND intent = 'PLACE'",
            ("$p", $"{TradingGateway.BudgetClosePress}-%")));
        Assert.Equal(ExecutionState.WORKING, flatten.State);

        foreach (var kind in new[] { TradingGateway.BudgetClosePress, TradingGateway.BudgetCancelPress })
            foreach (var r in h.Gw.Requests.Query("request_id LIKE $p AND needs_reconciliation = 1", ("$p", $"{kind}-%")))
                await h.Gw.AnswerFromTheCardAsync(r.RequestId, r.State, "checked in ATAS: still working (test)");

        var answered = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"the flatten's close  : {answered.RequestId} {answered.State}, flagged {answered.NeedsReconciliation}, "
                      + $"at the platform {AtThePlatform(h, answered)}");
        Assert.Equal(ExecutionState.WORKING, answered.State);
        Assert.False(answered.NeedsReconciliation);
        Assert.DoesNotContain(h.Gw.Unreconciled(), r => r.RequestId == answered.RequestId);
        return (h, answered);
    }

    static ExecutionState AtThePlatform(InFlightSettleTests.Harness h, ExecutionRequest row) =>
        h.C.Broker.Orders.Single(o => o.ClientOrderId == row.ClientOrderId).State;

    static bool OnTheCard(InFlightSettleTests.Harness h, ExecutionRequest row) =>
        h.Gw.Unreconciled().Any(r => r.RequestId == row.RequestId);

    // ------------------------------------------------------------------------------------- (a) dropped by the platform

    /// <summary>
    /// (a) A PRESS'S CLOSE THE PLATFORM DROPPED REACHES THE CARD, AND THE NEXT CLOSE ALL CLOSES ONCE.
    ///
    /// <para>The flatten's close rests and the owner answers it still working; the platform then cancels it and its update
    /// never arrives. Close all 1 waits on it — sends nothing beside it — and puts it back on the card. He answers it there,
    /// "It is no longer working at your platform", and the platform's history agrees: <c>CANCELLED</c>, his words on it.
    /// Close all 2 sends the one close, and the account is flat with one sell filled.</para>
    ///
    /// <para><b>RED on the base</b>: after Close all 1 the close is on no card — nothing anywhere offers the owner an answer
    /// for it, and every Close all waits on it for good.</para>
    /// </summary>
    [Fact]
    public async Task A_press_close_the_platform_dropped_reaches_the_card_and_the_next_close_all_closes_once()
    {
        var (h, flatten) = await AFlattenCloseAnsweredStillWorking("pra");
        using var dbh = h.Db;

        // THE PLATFORM CANCELS IT, AND ITS UPDATE NEVER ARRIVES.
        Assert.True(h.C.Broker.Cancel(flatten.ConnectorOrderId!));

        var closes = h.C.Closes;
        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        var after1 = h.Gw.GetRequest(flatten.RequestId)!;
        var onTheCard = OnTheCard(h, after1);
        log.WriteLine($"press 1              : {PressCloseOnceTests.Said(p1)}");
        log.WriteLine($"closes at the wire   : {closes} before, {closesAfter1} after");
        log.WriteLine($"the flatten's close  : {after1.State}, flagged {after1.NeedsReconciliation}, at the platform "
                      + $"{AtThePlatform(h, after1)}, {(onTheCard ? "on the card" : "not on the card")} — {after1.LastError ?? "-"}");

        Assert.Equal(closes, closesAfter1);
        Assert.Empty(p1.Targets);
        Assert.Contains(flatten.RequestId, p1.Summary, StringComparison.Ordinal);
        Assert.True(onTheCard, $"after Close all 1 the flatten's close {flatten.RequestId} is not on the card");
        Assert.True(after1.NeedsReconciliation);
        Assert.Equal(ExecutionState.WORKING, after1.State);
        Assert.Contains("Close all positions", after1.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("It is no longer working at your platform", after1.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("back on the Dashboard", p1.Summary, StringComparison.Ordinal);

        // HE ANSWERS IT ON THE CARD: IT IS NO LONGER WORKING AT HIS PLATFORM. Then he presses again.
        await h.Gw.AnswerFromTheCardAsync(flatten.RequestId, ExecutionState.CANCELLED, "checked in ATAS: it is gone (test)");
        var answered = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"his answer           : {answered.State}, flagged {answered.NeedsReconciliation} — {answered.LastError ?? "-"}");

        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        PressCloseOnceTests.PriceArrives(h.C);
        log.WriteLine($"press 2              : {PressCloseOnceTests.Said(p2)}");
        log.WriteLine($"closes at the wire   : {closesAfter1} before, {closesAfter2} after");
        log.WriteLine($"the book             : {PressCloseOnceTests.Book(h.C)}");
        log.WriteLine($"position at the end  : {PressCloseOnceTests.Pos(h.C)}");

        Assert.Equal(ExecutionState.CANCELLED, answered.State);
        Assert.False(answered.NeedsReconciliation);
        Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, answered.LastError, StringComparison.Ordinal);
        Assert.Contains("checked in ATAS: it is gone (test)", answered.LastError, StringComparison.Ordinal);
        Assert.Equal(1, closesAfter2 - closesAfter1);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(p2.Targets).State);
        Assert.Equal(1, PressCloseOnceTests.SellsFilled(h.C));
        Assert.Equal(0m, PressCloseOnceTests.Held(h.C));
        await h.Gw.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------- (b) still live at the platform

    /// <summary>
    /// (b) A PRESS'S CLOSE THE PLATFORM STILL LISTS WORKING IS NEVER ANSWERED AWAY.
    ///
    /// <para>The flatten's close rests and the owner answers it still working; it is STILL working at the platform. Close all 1
    /// waits on it. He then answers it on the card "It is no longer working at your platform" — wrongly. The platform's order
    /// history can be asked, and either holds it <c>WORKING</c> or cannot be read: his answer is refused, naming the order and
    /// why, and nothing is written. Close all 2 still sends nothing beside it. Price arrives: the flatten's own close fills, and
    /// the account is flat with one sell filled.</para>
    ///
    /// <para><b>RED on the base</b>: his answer is written <c>CANCELLED</c> over the live close, Close all 2 sends a second
    /// close beside it, both fill — two sells, ES −2. <b>The mutant</b> — the route's veto dropped — is the same.</para>
    /// </summary>
    [Theory]
    [InlineData("lists it working")]
    [InlineData("cannot be read")]
    public async Task A_press_close_the_platform_still_lists_working_is_never_answered_away(string history)
    {
        var (h, flatten) = await AFlattenCloseAnsweredStillWorking(history == "lists it working" ? "prb-live" : "prb-unread");
        using var dbh = h.Db;

        var closes = h.C.Closes;
        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        log.WriteLine($"[{history}] press 1      : {PressCloseOnceTests.Said(p1)}");
        log.WriteLine($"[{history}] closes       : {closes} before, {closesAfter1} after");

        // HE ANSWERS IT "NO LONGER WORKING" — and the platform still holds it WORKING, or its history does not answer.
        if (history == "cannot be read")
            h.C.HistoryThrows = new ConnectorTransportException("the order history did not answer (test)");
        GatewayDeniedException? refused = null;
        try
        {
            await h.Gw.AnswerFromTheCardAsync(flatten.RequestId, ExecutionState.CANCELLED,
                "checked in ATAS: it is gone (test)");
        }
        catch (GatewayDeniedException ex) { refused = ex; }
        h.C.HistoryThrows = null;
        var after = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"[{history}] his answer   : {(refused is null ? "WRITTEN" : $"refused — {refused.Message}")}");
        log.WriteLine($"[{history}] the close    : {after.State}, flagged {after.NeedsReconciliation}, at the platform "
                      + $"{AtThePlatform(h, after)} — {after.LastError ?? "-"}");

        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        var sellsAtTheWire = h.C.Broker.Orders.Count(o => o.Side == OrderSide.Sell);
        PressCloseOnceTests.PriceArrives(h.C);
        log.WriteLine($"[{history}] press 2      : {PressCloseOnceTests.Said(p2)}");
        log.WriteLine($"[{history}] sells        : {sellsAtTheWire} at the wire; closes {closesAfter1} before press 2, {closesAfter2} after");
        log.WriteLine($"[{history}] the book     : {PressCloseOnceTests.Book(h.C)}");
        log.WriteLine($"[{history}] position     : {PressCloseOnceTests.Pos(h.C)}");

        Assert.Equal(0m, PressCloseOnceTests.Held(h.C));
        Assert.Equal(1, PressCloseOnceTests.SellsFilled(h.C));
        Assert.Equal(closes, closesAfter2);
        Assert.Empty(p2.Targets);
        Assert.Contains(flatten.RequestId, p2.Summary, StringComparison.Ordinal);

        Assert.NotNull(refused);
        Assert.Contains(flatten.RequestId, refused!.Message, StringComparison.Ordinal);
        Assert.Contains(history == "lists it working" ? "holds it as WORKING" : "could not be read", refused.Message,
            StringComparison.Ordinal);
        Assert.Contains("Nothing was recorded", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutionState.WORKING, after.State);
        Assert.False(after.LastError?.StartsWith(TradingGateway.ResolvedByOwnerPrefix, StringComparison.Ordinal) ?? false);
        await h.Gw.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------ (c) where no history is asked

    /// <summary>
    /// (c) GUARD, GREEN BEFORE AND AFTER: WHERE NO HISTORY CAN BE ASKED, HIS ANSWER STANDS.
    ///
    /// <para>On a platform that cannot show its order history (<see cref="FaultProfile.HideOrderHistory"/>, ATAS's shape) the
    /// flatten's close rests, he answers it still working, and the platform cancels it without the update arriving. Close all
    /// 1 waits on it. He answers it "It is no longer working at your platform": there is nothing the platform could say that
    /// outranks him, so it is written as he said — without a single read of the history, which would only fail — exactly as
    /// his answer about an UNKNOWN order is. Close all 2 closes once, and the account is flat with one sell filled.</para>
    /// </summary>
    [Fact]
    public async Task Where_no_history_can_be_asked_his_answer_stands()
    {
        var (h, flatten) = await AFlattenCloseAnsweredStillWorking("prc", hideHistory: true);
        using var dbh = h.Db;
        Assert.False(h.C.Capabilities.ReconciliationProvable);
        Assert.True(h.C.Broker.Cancel(flatten.ConnectorOrderId!));

        var closes = h.C.Closes;
        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        log.WriteLine($"press 1              : {PressCloseOnceTests.Said(p1)}");

        var historyReads = h.C.HistoryReads;
        await h.Gw.AnswerFromTheCardAsync(flatten.RequestId, ExecutionState.CANCELLED, "checked in ATAS: it is gone (test)");
        var readForTheAnswer = h.C.HistoryReads - historyReads;
        var answered = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"his answer           : {answered.State}, flagged {answered.NeedsReconciliation}, history reads "
                      + $"{readForTheAnswer} — {answered.LastError ?? "-"}");

        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        PressCloseOnceTests.PriceArrives(h.C);
        log.WriteLine($"press 2              : {PressCloseOnceTests.Said(p2)}");
        log.WriteLine($"closes at the wire   : {closes} before press 1, {closesAfter1} after it, {closesAfter2} after press 2");
        log.WriteLine($"position at the end  : {PressCloseOnceTests.Pos(h.C)}");

        Assert.Equal(closes, closesAfter1);
        Assert.Equal(ExecutionState.CANCELLED, answered.State);
        Assert.False(answered.NeedsReconciliation);
        Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, answered.LastError, StringComparison.Ordinal);
        Assert.Equal(0, readForTheAnswer);
        Assert.Equal(1, closesAfter2 - closesAfter1);
        Assert.Equal(ExecutionState.FILLED, Assert.Single(p2.Targets).State);
        Assert.Equal(1, PressCloseOnceTests.SellsFilled(h.C));
        Assert.Equal(0m, PressCloseOnceTests.Held(h.C));
        await h.Gw.DisposeAsync();
    }

    // ------------------------------------------------------------- (d) a final listing his answer contradicts

    /// <summary>What the card's route told the engineering log about each answer a final listing contradicted.</summary>
    static List<string> Contradicted(InFlightSettleTests.Harness h)
    {
        using var c = h.Db.Cmd("SELECT COALESCE(metadata,'') FROM engineering_log WHERE event='card_answer_contradicted' ORDER BY id");
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    /// <summary>
    /// (d) AN ANSWER A PROVABLE FINAL LISTING CONTRADICTS IS REFUSED, NEVER WRITTEN.
    ///
    /// <para>The flatten's close rests and the owner answers it still working; Close all 1 waits on it and puts it back on
    /// the card. The platform then finishes it and its update never arrives — it FILLS (<see cref="FakeBroker.FillWorking"/>)
    /// or it is CANCELLED (<see cref="FakeBroker.Cancel"/>) — and he answers it on the card the other way: "It is no longer
    /// working at your platform" over a close the platform's history lists <c>FILLED</c>, "It was filled" over one it lists
    /// <c>CANCELLED</c>. The history can be asked and its answer is final, so his answer is refused, naming the order, the
    /// state listed and the card's answer that matches it, and nothing is written: the row keeps its state, its flag and
    /// its words, still on the card. He answers the one that matches, and it is written. Close all 2 sends nothing where
    /// the close filled and one close where it was cancelled; the account is flat with one sell filled.</para>
    ///
    /// <para><b>RED on the base</b>: his contradicted answer is written — a close recorded <c>CANCELLED</c> that the
    /// platform holds <c>FILLED</c>, or <c>FILLED</c> that never filled — with the platform's state beside it in his note:
    /// a record false about what ran, written with the proof of that in hand. <b>The mutant</b> — the new refusal dropped
    /// — is the same.</para>
    /// </summary>
    [Theory]
    [InlineData("fills it")]
    [InlineData("cancels it")]
    public async Task An_answer_a_final_listing_contradicts_is_refused_and_never_written(string platform)
    {
        var fills = platform == "fills it";
        var (h, flatten) = await AFlattenCloseAnsweredStillWorking(fills ? "prd-filled" : "prd-cancelled");
        using var dbh = h.Db;

        var closes = h.C.Closes;
        var p1 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter1 = h.C.Closes;
        log.WriteLine($"[{platform}] press 1      : {PressCloseOnceTests.Said(p1)}");
        Assert.Equal(closes, closesAfter1);
        Assert.True(OnTheCard(h, flatten), $"after Close all 1 the flatten's close {flatten.RequestId} is not on the card");

        // THE PLATFORM FINISHES IT, AND ITS UPDATE NEVER ARRIVES.
        if (fills) Assert.Equal(ExecutionState.FILLED, h.C.Broker.FillWorking(flatten.ConnectorOrderId!)?.State);
        else Assert.True(h.C.Broker.Cancel(flatten.ConnectorOrderId!));
        var (listed, wrong, right, matching) = fills
            ? (ExecutionState.FILLED, ExecutionState.CANCELLED, ExecutionState.FILLED, "It was filled")
            : (ExecutionState.CANCELLED, ExecutionState.FILLED, ExecutionState.CANCELLED,
                "It is no longer working at your platform");
        var before = h.Gw.GetRequest(flatten.RequestId)!;
        Assert.Equal(listed, AtThePlatform(h, before));
        log.WriteLine($"[{platform}] the close    : {before.State}, flagged {before.NeedsReconciliation}, at the platform "
                      + $"{AtThePlatform(h, before)} — {before.LastError ?? "-"}");

        // HE ANSWERS IT THE OTHER WAY.
        GatewayDeniedException? refused = null;
        try
        {
            await h.Gw.AnswerFromTheCardAsync(flatten.RequestId, wrong, "checked in ATAS: the other way (test)");
        }
        catch (GatewayDeniedException ex) { refused = ex; }
        var after = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"[{platform}] his {wrong,-9}: {(refused is null ? "WRITTEN" : $"refused — {refused.Message}")}");
        log.WriteLine($"[{platform}] the close    : {after.State}, flagged {after.NeedsReconciliation}, "
                      + $"{(OnTheCard(h, after) ? "on the card" : "not on the card")} — {after.LastError ?? "-"}");

        Assert.True(refused is not null, $"his answer {wrong} over a close the platform's history lists {listed} was WRITTEN: "
                                         + $"{after.State}, flagged {after.NeedsReconciliation} — {after.LastError}");
        Assert.Contains(flatten.RequestId, refused!.Message, StringComparison.Ordinal);
        Assert.Contains($"holds it as {listed}", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"\"{matching}\"", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was recorded.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutionState.WORKING, after.State);
        Assert.True(after.NeedsReconciliation);
        Assert.Equal(before.LastError, after.LastError);
        Assert.True(OnTheCard(h, after));
        var engineering = Assert.Single(Contradicted(h));
        Assert.Contains($"\"listed\":\"{listed}\"", engineering, StringComparison.Ordinal);
        Assert.Contains($"\"answered\":\"{wrong}\"", engineering, StringComparison.Ordinal);

        // HE ANSWERS THE ONE THAT MATCHES, AND IT IS WRITTEN. Then he presses again.
        await h.Gw.AnswerFromTheCardAsync(flatten.RequestId, right, "checked in ATAS again (test)");
        var answered = h.Gw.GetRequest(flatten.RequestId)!;
        log.WriteLine($"[{platform}] his {right,-9}: {answered.State}, flagged {answered.NeedsReconciliation} — {answered.LastError ?? "-"}");

        var p2 = await h.Gw.OperatorCloseAllAsync();
        var closesAfter2 = h.C.Closes;
        PressCloseOnceTests.PriceArrives(h.C);
        log.WriteLine($"[{platform}] press 2      : {PressCloseOnceTests.Said(p2)}");
        log.WriteLine($"[{platform}] closes       : {closesAfter1} before press 2, {closesAfter2} after");
        log.WriteLine($"[{platform}] the book     : {PressCloseOnceTests.Book(h.C)}");
        log.WriteLine($"[{platform}] position     : {PressCloseOnceTests.Pos(h.C)}");

        Assert.Equal(right, answered.State);
        Assert.False(answered.NeedsReconciliation);
        Assert.StartsWith(TradingGateway.ResolvedByOwnerPrefix, answered.LastError, StringComparison.Ordinal);
        Assert.Contains("checked in ATAS again (test)", answered.LastError, StringComparison.Ordinal);
        Assert.Single(Contradicted(h));
        if (fills)
        {
            Assert.Equal(closesAfter1, closesAfter2);
            Assert.Empty(p2.Targets);
        }
        else
        {
            Assert.Equal(1, closesAfter2 - closesAfter1);
            Assert.Equal(ExecutionState.FILLED, Assert.Single(p2.Targets).State);
        }
        Assert.Equal(1, PressCloseOnceTests.SellsFilled(h.C));
        Assert.Equal(0m, PressCloseOnceTests.Held(h.C));
        await h.Gw.DisposeAsync();
    }
}
