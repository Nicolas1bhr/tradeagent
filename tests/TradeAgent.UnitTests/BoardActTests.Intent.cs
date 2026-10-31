using TradeAgent.Core;
using TradeAgent.Core.Board;
using Xunit;

namespace TradeAgent.Tests.Unit;

public partial class BoardActTests
{
    // ---- item 2: the typed intent and its direction ---------------------------------------------------------------------------

    const string Envelope = "{\"account\":\"PAPER-1\",\"connector\":\"paper\",\"days\":null,\"deployments\":3,\"instrument\":\"BTCUSDT\",\"max_notional\":5000,\"max_quantity\":0.1}";

    static string EnvelopeWith(string key, string value)
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse(Envelope)!.AsObject();
        o[key] = System.Text.Json.Nodes.JsonNode.Parse(value);
        return TradeAgent.Core.Data.TapeJson.Canonical(o.ToJsonString());
    }

    /// <summary>
    /// EVERY FIELD'S PAIRS, (narrower, wider) — each read the way the code that enforces the field reads it, zero included: a raised
    /// cap, a cap switched off, a shorter closure or strike window, a longer or switched-off valuation exit, a review or head ceiling
    /// switched ON from "none", a share that rises because it was removed, a floor that falls to its default, an envelope granted,
    /// enlarged or made to last for ever.
    /// </summary>
    static readonly Dictionary<string, (string Narrow, string Wide)[]> Pairs = new Dictionary<string, (string Narrow, string Wide)[]>
    {
        ["mode"] = [("\"OBSERVE\"", "\"PAPER\"")],
        ["risk.max_order_quantity"] = [("0.5", "1"), ("0", "1")],
        ["risk.max_notional_per_order"] = [("1000", "2000"), ("1000", "0")],
        ["risk.max_open_positions"] = [("1", "2"), ("0", "2")],
        ["risk.max_orders_per_minute"] = [("3", "6")],
        ["risk.instrument_allowlist"] = [("[\"BTCUSDT\"]", "[\"BTCUSDT\",\"ETHUSDT\"]"), ("[]", "[\"BTCUSDT\"]")],
        ["risk.max_loss_per_trade"] = [("50", "100"), ("50", "0")],
        ["risk.max_daily_loss"] = [("200", "400"), ("200", "0")],
        ["risk.loss_min_closure_hours"] = [("48", "24"), ("24", "0")],
        ["risk.loss_strike_window_days"] = [("14", "7"), ("7", "0")],
        ["risk.valuation_loss_exit_minutes"] = [("5", "15"), ("15", "0")],
        ["plan_day_total"] = [("20", "40"), ("0", "40")],
        ["role_shares"] = [("{\"research\":0.25}", "{\"research\":0.4}"), ("{\"research\":0.25}", "{}")],
        ["turn_allowance_input"] = [("1000", "2000"), ("1000", "0")],
        ["turn_allowance_output"] = [("500", "1000")],
        ["lanes"] = [("1", "2")],
        ["lanes_per_pool"] = [("1", "2")],
        ["lanes_per_cell_class"] = [("1", "3")],
        ["review_minutes"] = [("60", "30"), ("0", "30")],
        ["head_ceiling_hours"] = [("48", "24"), ("0", "24")],
        ["rewake_floor_minutes"] = [("{\"team\":120}", "{\"team\":60}"), ("{\"team\":120}", "{}"), ("{\"executor\":30}", "{\"executor\":0}")],
        // EACH PAIR'S ENVELOPE ITS OWN FIELD (U-evidence-breadth-paper): the same pairs, written for each founding pair.
    }.Concat(BoardFounding.PaperPairs.Select(p => KeyValuePair.Create($"paper_envelopes.{p.Instrument}", new[]
    {
        ("null", For(p.Instrument, Envelope)), (For(p.Instrument, EnvelopeWith("max_quantity", "0.01")), For(p.Instrument, Envelope)),
        (For(p.Instrument, EnvelopeWith("max_notional", "1000")), For(p.Instrument, Envelope)),
        (For(p.Instrument, EnvelopeWith("deployments", "1")), For(p.Instrument, EnvelopeWith("deployments", "2"))),
        (For(p.Instrument, EnvelopeWith("days", "7")), For(p.Instrument, Envelope))
    }))).ToDictionary(StringComparer.Ordinal);

    /// <summary><paramref name="terms"/> made <paramref name="pair"/>'s: the same terms with its instrument.</summary>
    static string For(string pair, string terms)
    {
        if (terms == "null") return terms;
        var o = System.Text.Json.Nodes.JsonNode.Parse(terms)!.AsObject();
        o["instrument"] = pair;
        return TradeAgent.Core.Data.TapeJson.Canonical(o.ToJsonString());
    }

    /// <summary>
    /// A RAISE IS NEVER A NARROWING, ON EVERY FIELD, BOTH WAYS: for every field the board's acts change — the table's own list, so a
    /// field added without pairs fails here — moving to the wider value WIDENS and back NARROWS; and a change whose two values each
    /// allow what the other does not widens both ways. RED before <c>BoardDirection</c>: no table.
    /// </summary>
    [Fact]
    public void A_raise_is_never_a_narrowing()
    {
        Assert.Equal(BoardDirection.Fields.Select(f => f.Path).Order(StringComparer.Ordinal), Pairs.Keys.Order(StringComparer.Ordinal));
        foreach (var (field, pairs) in Pairs)
            foreach (var (narrow, wide) in pairs)
            {
                log.WriteLine($"{field}: {narrow} -> {wide}");
                Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of(field, narrow, wide));
                Assert.Equal(BoardDirectionKind.Narrows, BoardDirection.Of(field, wide, narrow));
                Assert.Equal(BoardDirectionKind.Unchanged, BoardDirection.Of(field, wide, wide));
            }

        foreach (var (field, a, b) in new[]
                 {
                     ("risk.instrument_allowlist", "[\"BTCUSDT\"]", "[\"ETHUSDT\"]"),
                     ("role_shares", "{\"research\":0.25,\"operations\":0.5}", "{\"research\":0.5,\"operations\":0.25}"),
                     ("rewake_floor_minutes", "{\"team\":120,\"executor\":15}", "{\"team\":60,\"executor\":30}"),
                     ("paper_envelopes.BTCUSDT", Envelope, EnvelopeWith("account", "\"PAPER-2\"")),
                     ("paper_envelopes.BTCUSDT", Envelope.Replace("\"deployments\":3", "\"deployments\":4").Replace("0.1", "0.01"), Envelope)
                 })
        {
            Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of(field, a, b));
            Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of(field, b, a));
        }

        // AN INTENT NARROWS ONLY IF EVERY CHANGE DOES: one widening change makes the whole a widening.
        var (mixed, why) = BoardIntent.Parse("[{\"field\":\"plan_day_total\",\"from\":40,\"to\":20},{\"field\":\"lanes\",\"from\":1,\"to\":2}]");
        Assert.True(mixed is not null, why);
        Assert.Equal(BoardDirectionKind.Widens, mixed.Direction);
        var (lowered, _) = BoardIntent.Parse("[{\"field\":\"plan_day_total\",\"from\":40,\"to\":20},{\"field\":\"review_minutes\",\"from\":30,\"to\":0}]");
        Assert.True(lowered!.Narrows);
    }

    /// <summary>
    /// AN UNLISTED FIELD WIDENS, AND NO INTENT MAY NAME ONE: a field outside the table — real money's switch, a part of the envelope,
    /// a field this build has never heard of — is a widening whichever way it moves, and the parse refuses it, a live mode, a value
    /// outside the field's type, a change that changes nothing, a field named twice and an intent of no or too many changes, in words.
    /// The intent is written one way — sorted, normalised — so its hash is its meaning's. Watched red with an unlisted field read as
    /// narrowing.
    /// </summary>
    [Fact]
    public void An_unlisted_field_widens()
    {
        foreach (var (field, a, b) in new[]
                 {
                     ("live_activated", "true", "false"), ("paper_envelopes.BTCUSDT.max_quantity", "0.1", "0.01"),
                     ("paper_envelope", Envelope, "null"), ("paper_envelopes", "{}", "{}"),
                     ("risk.max_slippage", "1", "0"), ("holdout_cutoff", "\"2026-01-01\"", "\"2025-01-01\"")
                 })
        {
            Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of(field, a, b));
            Assert.Equal(BoardDirectionKind.Widens, BoardDirection.Of(field, b, a));
        }

        foreach (var (json, says) in new[]
                 {
                     ("[{\"field\":\"live_activated\",\"from\":false,\"to\":true}]", "real money"),
                     ("[{\"field\":\"paper_envelopes.BTCUSDT.max_quantity\",\"from\":0.1,\"to\":0.01}]", "changes whole"),
                     ("[{\"field\":\"paper_envelopes\",\"from\":{},\"to\":{}}]", "the pair it changes"),
                     ("[{\"field\":\"paper_envelope\",\"from\":null,\"to\":null}]", "no field"),
                     ("[{\"field\":\"risk.max_slippage\",\"from\":1,\"to\":0}]", "no field"),
                     ("[{\"field\":\"mode\",\"from\":\"PAPER\",\"to\":\"LIVE_CONFIRM\"}]", "real-money mode"),
                     ("[{\"field\":\"mode\",\"from\":\"PAPER\",\"to\":\"LIVE_AUTONOMOUS\"}]", "real-money mode"),
                     ("[{\"field\":\"risk.max_open_positions\",\"from\":2,\"to\":1.5}]", "whole number"),
                     ("[{\"field\":\"risk.max_order_quantity\",\"from\":1,\"to\":\"0.5\"}]", "never text"),
                     ("[{\"field\":\"risk.max_order_quantity\",\"from\":1,\"to\":-1}]", "or more"),
                     ("[{\"field\":\"lanes\",\"from\":1,\"to\":0}]", "or more"),
                     ("[{\"field\":\"role_shares\",\"from\":{},\"to\":{\"research\":1.5}}]", "fraction"),
                     ("[{\"field\":\"rewake_floor_minutes\",\"from\":{},\"to\":{\"chief\":5}}]", "no level"),
                     ("[{\"field\":\"paper_envelopes.BTCUSDT\",\"from\":null,\"to\":{\"connector\":\"paper\"}}]", "exactly"),
                     ("[{\"field\":\"plan_day_total\",\"from\":40,\"to\":40.0}]", "changes nothing"),
                     ("[{\"field\":\"lanes\",\"from\":2,\"to\":1},{\"field\":\"lanes\",\"from\":2,\"to\":1}]", "twice"),
                     ("[{\"field\":\"lanes\",\"from\":2,\"to\":1,\"why\":\"x\"}]", "nothing else"),
                     ("[]", "1 to 10"),
                     ("{\"field\":\"lanes\",\"from\":2,\"to\":1}", "list"),
                     ("not json", "not JSON")
                 })
        {
            var (intent, why) = BoardIntent.Parse(json);
            log.WriteLine($"{json} -> {why}");
            Assert.Null(intent);
            Assert.Contains(says, why, StringComparison.Ordinal);
        }
        var eleven = "[" + string.Join(",", Enumerable.Range(0, 11).Select(i => $"{{\"field\":\"lanes\",\"from\":{i + 2},\"to\":1}}")) + "]";
        Assert.Contains("1 to 10", BoardIntent.Parse(eleven).Why, StringComparison.Ordinal);

        // ONE SPELLING: the same changes in another order, with another spacing and 1e1 for 10, are one text and one hash.
        var (x, _) = BoardIntent.Parse("[{\"field\":\"plan_day_total\",\"from\":40,\"to\":1e1},{\"field\":\"lanes\",\"from\":2,\"to\":1}]");
        var (y, _) = BoardIntent.Parse("[ {\"to\":1,\"from\":2,\"field\":\"lanes\"}, {\"field\":\"plan_day_total\", \"to\":10, \"from\":40} ]");
        Assert.Equal(x!.Canonical, y!.Canonical);
        Assert.Equal(x.Sha256, y.Sha256);
        Assert.Equal("[{\"field\":\"lanes\",\"from\":2,\"to\":1},{\"field\":\"plan_day_total\",\"from\":40,\"to\":10}]", x.Canonical);
    }
}
