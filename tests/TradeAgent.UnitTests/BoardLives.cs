using System.Text.Json;
using System.Text.Json.Nodes;
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
        }, seatClasses: FoundingClasses);

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
