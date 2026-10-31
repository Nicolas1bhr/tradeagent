using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;

namespace TradeAgent.Tests.Unit;

/// <summary>The board's lives' own helpers: a life with seats active on their founding classes, and the seats' commands.</summary>
static class BoardLives
{
    /// <summary>A rationale whose marker no packet may carry to another seat.</summary>
    public const string Marker = "PROPOSER-RATIONALE-7f3a";

    public const string Rationale = Marker + " the day is too small for the research load";

    /// <summary>The class each founding seat plays: the one it was founded on (<c>BoardFounding.Seats</c>).</summary>
    public static IReadOnlyDictionary<string, string> FoundingClasses { get; } =
        BoardFounding.Seats.ToDictionary(s => s.Id, s => s.SeatClass, StringComparer.Ordinal);

    /// <summary>A life whose <paramref name="active"/> seats are ACTIVE through the test host's seam, each on its founding class.</summary>
    public static Task<PuppetLife> Begin(string name, params string[] active) =>
        Begin(name, null, active);

    public static Task<PuppetLife> Begin(string name, Action<Database>? seed, params string[] active) =>
        PuppetLife.Begin(name, seed: db =>
        {
            var store = new BoardStore(db);
            foreach (var seat in active) store.SetSeatStateUnderTest(seat, BoardSeatStatus.Active, DateTimeOffset.UtcNow);
            seed?.Invoke(db);
        }, seatClasses: FoundingClasses, settings: s => s.SelectedAccountId = "SIM-001");

    /// <summary>
    /// A POLICY VERSION THE TEST WRITES BEFORE THE LIFE STARTS, as only a test may (b1's <c>SeedVersion</c>): <paramref name="change"/>
    /// over the version in force, decided by <c>board</c> under a seeded act. Returns the new version.
    /// </summary>
    public static int Seed(Database db, Func<BoardPolicy, BoardPolicy> change)
    {
        var inForce = new BoardReader(db).InForce();
        var body = BoardPolicyBody.Canonical(change(inForce.Policy));
        var act = $"bact-seed{inForce.Version + 1}";
        const string at = "2026-10-24T12:00:00.0000000Z";
        db.Write(_ =>
        {
            using (var c = db.Cmd("INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, "
                                  + "evidence_class, expires_at, at) VALUES($id, 'narrow', 'board-3', NULL, '[]', $sha, $v, 'the test seeds this "
                                  + "version', '[]', NULL, NULL, $at)",
                       ("$id", act), ("$sha", Sha256Hex.Of("[]")), ("$v", inForce.Version), ("$at", at)))
                c.ExecuteNonQuery();
            using (var c = db.Cmd("INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, "
                                  + "supersedes) VALUES($next, 'board', 'act', $act, $at, 'PAPER', 0, $body, $sha, $v)",
                       ("$next", inForce.Version + 1), ("$act", act), ("$at", at), ("$body", body), ("$sha", Sha256Hex.Of(body)),
                       ("$v", inForce.Version)))
                c.ExecuteNonQuery();
            return 0;
        });
        return inForce.Version + 1;
    }

    /// <summary>
    /// A LIFE'S TRADABLE POLICY: the simulator's instrument allowed at up to ten.
    /// </summary>
    public static BoardPolicy Tradable(BoardPolicy p)
    {
        var risk = p.Risk;
        risk.MaxOrderQuantity = 10m;
        risk.InstrumentAllowlist = ["ES", "BTCUSDT"];
        return p with { Risk = risk };
    }

    /// <summary>The policy's standing paper envelope on the life's own simulated venue and account (<c>fake</c>, <c>SIM-001</c>).</summary>
    public static BoardPolicy EnvelopeHere(BoardPolicy p) =>
        p with
        {
            PaperEnvelopes = new Dictionary<string, PaperEnvelopeTerms>(StringComparer.Ordinal)
            {
                ["BTCUSDT"] = new(Connectors.Fake.FakeConnector.ConnectorId, "SIM-001", "BTCUSDT", 0.1m, 5_000m, 1, null)
            }
        };

    /// <summary>
    /// NO REAL MODEL RAN IN <paramref name="life"/> (<c>U-board-b2</c> item 7): its manifest is a puppet, and every launch the ledger recorded
    /// ran on the puppet runtime, names no model it asked for or was answered by, and was priced at the test's own rate — never a vendor's
    /// bill. Answers how many launches were board seats'.
    /// </summary>
    public static int AssertNoModel(PuppetLife life)
    {
        Xunit.Assert.True(life.Manifest.Puppet, "a board life ran on a manifest that is not a puppet");
        var attempts = life.Attempts();
        foreach (var a in attempts)
        {
            Xunit.Assert.Equal(AgentRuntime.RuntimeManifest.PuppetId, a.Runtime);
            Xunit.Assert.True(a.RequestedModel is null && a.EffectiveModel is null,
                $"{a.Id} ({a.Role}) names a model: requested {a.RequestedModel}, effective {a.EffectiveModel}");
            Xunit.Assert.True(a.PricingBasis is null or "owner", $"{a.Id} ({a.Role}) was priced as a vendor's bill: {a.PricingBasis}");
        }
        return attempts.Count(a => BoardSeats.IsBoardId(a.Role));
    }

    public static string Change(string field, object from, object to) =>
        JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = field, ["from"] = from, ["to"] = to } });

    /// <summary>A JSON argument as a puppet step's one word: its quotes kept as themselves.</summary>
    static string Arg(string json) => "\"" + json.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string Propose(string intent, string why) => $"trade board propose {Arg(intent)} \"{why}\"";

    public static string Narrow(string intent, string why) => $"trade board narrow {Arg(intent)} \"{why}\"";

    public static string Decide(string act, string answer, string sha, string why) => $"trade board decide {act} {answer} {sha} \"{why}\"";

    public static List<JsonObject> TradeSteps(PuppetLife life, string role, int turn) =>
        [.. life.Log(role, turn).Where(s => (string?)s["step"] == "trade")];

    public static string Said(JsonObject step) => $"{(string?)step["stdout"]}{(string?)step["stderr"]}";
}
