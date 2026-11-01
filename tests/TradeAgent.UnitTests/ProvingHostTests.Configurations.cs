using System.Text.Json;
using System.Text.Json.Nodes;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE REST OF THE FIRST LADDER, AS PUPPET CONFIGURATIONS (<c>U-proving-ground-d</c> item 1; ORCHESTRATION § 2.14: "a deterministic search
/// with no model as the floor, one agent, an agent with a critic, a headed team — at equal budgets on the same markets"). Each richer chart
/// is chartered and hired through the kernel's own writers in its configuration's home; every seat is a puppet whose turns are its
/// script. What a score says is what the script did, through the real kernel — SIMULATED, plumbing, never intelligence.
/// </summary>
public partial class ProvingHostTests
{
    /// <summary>The AI envelope every configuration of the ladder works under.</summary>
    internal const decimal Envelope = 100m;

    /// <summary>The verdict class a PASS on a proving market gets, as the referee writes it there.</summary>
    internal const string Pass = PromotionVerdict.PaperEligible;

    /// <summary>A value as one word of a puppet step: JSON with its quotes kept.</summary>
    internal static string Word(object value) => JsonSerializer.Serialize(value).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A date every forecast of a run resolves by: well after any verdict, well inside the vocabulary's horizon.</summary>
    static string By() => DateTimeOffset.UtcNow.AddDays(30).ToString("O");

    /// <summary>
    /// A ROUTINE DECISION WITH A FORECAST PER PROGRAM (<c>trade decision record … --forecasts</c>): that each program's version gets the PASS
    /// class, at the stated probability — written before its verdict is asked, so the app's resolver settles it from the referee's record.
    /// </summary>
    internal static string Judging(string choice, params (string Program, decimal P)[] forecasts) =>
        $"trade decision record \"{choice}\" \"the backtests before the cutoff are what I can judge by\" --options \"{Word(new[] { choice, "spend no verdict" })}\" --forecasts "
        + Word(forecasts.Select(f => new { predicate = ForecastVocabulary.VerdictClass, subject = VersionOf(f.Program), target = Pass, by = By(), probability = f.P }));

    /// <summary>A report-shaped task mandate to <paramref name="holder"/> (a template token), due in a week, with a small allocation.</summary>
    static string Assign(string holder, string goal) =>
        $"trade mandate assign {{position:{holder}}} \"{goal}\" --kind {MandateKind.Task} --shape {MandateShape.Report} --due {DateTimeOffset.UtcNow.AddDays(7):O} --allocation 5";

    /// <summary>The mandate this turn's wake names, as the seat was told it.</summary>
    const string MyMandate = "{prompt:(man-[0-9a-f]+)}";

    static string[] Ack(string what) => ["usage 2000 0 200", $"final {what}"];

    static JsonObject Seat(string id, string unit, string[][] script, bool head = false)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["unit"] = unit,
            ["script"] = new JsonArray([.. script.Select(t => (JsonNode?)new JsonArray([.. t.Select(x => (JsonNode?)x)]))])
        };
        if (head) o["head"] = true;
        return o;
    }

    static JsonObject Chart(string name, JsonArray charters, params JsonObject[] positions) => new()
    {
        ["version"] = ProvingConfiguration.Version, ["name"] = name, ["kind"] = "seats",
        ["chart"] = new JsonObject
        {
            ["units"] = new JsonArray(OrgIds.Root, OrgIds.Research, OrgIds.Operations),
            ["charters"] = charters,
            ["positions"] = new JsonArray([.. positions])
        },
        ["ai_envelope"] = Envelope, ["trial_budget"] = 10, ["verdict_budget"] = 2, ["deadline_seconds"] = 900
    };

    static JsonObject Team(string id, decimal? verdictShare = null)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = OrgUnitKind.Team, ["parent"] = OrgIds.Research, ["ai_share"] = OrgBounds.ExperimentMaxShare };
        if (verdictShare is { } v) o["verdict_share"] = v;
        return o;
    }

    /// <summary>
    /// AN AGENT WITH A CRITIC. The research position tests the same two candidates the one agent does, then — before spending a verdict —
    /// gives its critic, a position in a review team it chartered, a mandate to review them. The critic re-runs the trap's candidate and
    /// delivers that it does not clear the venue's cost; the agent, woken by the delivery, forecasts and asks a verdict on the edge's
    /// candidate alone.
    /// </summary>
    internal static JsonObject CriticConfiguration()
    {
        var edge = VersionOf(Program("SYNA"));
        var trap = VersionOf(Program("SYNC"));
        string[][] agent =
        [
            Ack("I read my critic's activation."),
            [
                "write strategies/edge.strategy " + Program("SYNA"),
                $"trade backtest --strategy strategies/edge.strategy --dataset {EdgeDataset} --from {ResearchFrom(0)} --to {ResearchTo(0)}",
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                Assign("critic", $"review my two candidates before I spend a verdict: {edge} on dataset {EdgeDataset} and {trap} on dataset {TrapDataset}"),
                "usage 100000 0 20000",
                "final I measured one hypothesis on two instruments and asked my critic to review both."
            ],
            [
                Judging("ask a verdict on the SYNA candidate only", (Program("SYNA"), 0.7m)),
                $"trade verdict --version {edge} --dataset {EdgeDataset}",
                "usage 10000 0 1000",
                "final My critic found the SYNC candidate's edge does not clear costs; I asked one verdict."
            ],
            ["usage 10000 0 1000", "final I read the verdict."]
        ];
        string[][] critic =
        [
            Ack("I read my activation."),
            [
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                $"trade mandate deliver {MyMandate} \"the SYNA candidate holds; the SYNC candidate's net is gone at the venue's cost, so it deserves no verdict\"",
                "usage 20000 0 4000",
                "final I reviewed both and delivered."
            ],
            Ack("Nothing more to review.")
        ];
        return Chart("critic", [Team("review")],
            Seat(CouncilRoles.Research, OrgIds.Research, agent),
            Seat("critic", "review", critic),
            Seat(CouncilRoles.Operations, OrgIds.Operations, ChairScript));
    }

    /// <summary>
    /// A HEADED TEAM: a head and two executors in a team the research position chartered. The brief goes to the head, which gives each
    /// executor a mandate — the edge's market to one, the trap's to the other. Each tests its candidate, forecasts its verdict, asks it
    /// and delivers. The first executor also tests a market no mandate named (the off-path exam), forecasts and asks its verdict, and
    /// PROPOSES the finding to its head citing the version — so the finding outlives its context.
    /// </summary>
    internal static JsonObject HeadedConfiguration()
    {
        var edge = VersionOf(Program("SYNA"));
        var trap = VersionOf(Program("SYNC"));
        var off = VersionOf(Program("SYND"));
        string[][] head =
        [
            Ack("I read my activation."),
            [
                Assign("exec-1", $"test whether the bare signal leads SYNA's next hour on dataset {EdgeDataset}, and ask its verdict if it does"),
                Assign("exec-2", $"test whether the bare signal leads SYNC's next hour on dataset {TrapDataset}, and ask its verdict if it does"),
                "usage 50000 0 10000",
                "final I split the brief between my two executors."
            ],
            ["usage 10000 0 1000", "final I read what my team sent."]
        ];
        string[][] first =
        [
            Ack("I read my activation."),
            [
                "write strategies/edge.strategy " + Program("SYNA"),
                $"trade backtest --strategy strategies/edge.strategy --dataset {EdgeDataset} --from {ResearchFrom(0)} --to {ResearchTo(0)}",
                Judging("ask the SYNA candidate's verdict", (Program("SYNA"), 0.7m)),
                $"trade verdict --version {edge} --dataset {EdgeDataset}",
                "write strategies/off.strategy " + Program("SYND"),
                $"trade backtest --strategy strategies/off.strategy --dataset {OffPathDataset} --from {ResearchFrom(OffPathDay)} --to {ResearchTo(OffPathDay)}",
                Judging("ask the SYND candidate's verdict, beside my mandate", (Program("SYND"), 0.6m)),
                $"trade verdict --version {off} --dataset {OffPathDataset}",
                $"trade mandate propose {MyMandate} \"beside my mandate: the bare signal leads SYND's next hour too, and I asked its verdict\" --refs "
                    + Word(new[] { new { kind = OrgRefKind.Version, id = off } }),
                $"trade mandate deliver {MyMandate} \"the SYNA candidate clears the venue's cost before the cutoff; I asked its verdict\"",
                "usage 60000 0 12000",
                "final I tested my market, and one beside it."
            ],
            ["usage 10000 0 1000", "final I read my verdicts."]
        ];
        string[][] second =
        [
            Ack("I read my activation."),
            [
                "write strategies/trap.strategy " + Program("SYNC"),
                $"trade backtest --strategy strategies/trap.strategy --dataset {TrapDataset} --from {ResearchFrom(TrapDay)} --to {ResearchTo(TrapDay)}",
                Judging("ask the SYNC candidate's verdict", (Program("SYNC"), 0.3m)),
                $"trade verdict --version {trap} --dataset {TrapDataset}",
                $"trade mandate deliver {MyMandate} \"the SYNC candidate barely clears its cost before the cutoff; I asked its verdict\"",
                "usage 40000 0 8000",
                "final I tested my market."
            ],
            ["usage 10000 0 1000", "final I read my verdict."]
        ];
        return Chart("headed", [Team("desk", verdictShare: 0.5m)],
            Seat("head", "desk", head, head: true),
            Seat("exec-1", "desk", first),
            Seat("exec-2", "desk", second),
            Seat(CouncilRoles.Research, OrgIds.Research, [Ack("I read my team's activations."), ["usage 2000 0 200", "final Nothing for me."]]),
            Seat(CouncilRoles.Operations, OrgIds.Operations, ChairScript));
    }
}
