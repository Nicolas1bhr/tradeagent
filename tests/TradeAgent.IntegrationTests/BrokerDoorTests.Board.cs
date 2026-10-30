using System.Text.Json;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Integration;

public partial class BrokerDoorTests
{
    /// <summary>
    /// A BOARD SEAT'S DOOR CARRIES EXACTLY THE BOARD'S VERBS, AND A POSITION'S NONE OF THEM (<c>U-board-alive</c> item 1; R24 § 4.2, AUTH-002,
    /// ORCHESTRATION § 2.9). Over a real gateway and pipe, the door composed as the app composes it — the organisation's verbs and the
    /// board's: board-1, ACTIVE on a broker launch the ledger recorded, is offered exactly <c>board_narrow</c>, <c>board_propose</c>,
    /// <c>board_decide</c>, <c>board_list</c>, <c>board_show</c> and <c>board_power_propose</c> — no harness tool and no organisation verb — and
    /// its <c>board_propose</c> reaches the board's store through the pipe under its grant (an act, applying nothing); its <c>trade</c> and
    /// <c>mandate_list</c> are refused in the door's words and recorded. The chief-like position beside it is offered every organisation verb
    /// and no board verb, and its <c>board_propose</c> is refused — no act made. RED at base: <c>CS0117</c>, no <c>BrokerDoor.BoardVerbs</c>.
    /// Watched red with <c>VerbsFor</c> serving every verb to every grant.
    /// </summary>
    [Fact]
    public async Task A_board_seats_door_carries_exactly_the_boards_verbs_and_a_positions_none()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _db = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-door-" + Guid.NewGuid().ToString("n")[..12]);
        new BoardStore(db).SetSeatStateUnderTest("board-1", BoardSeatStatus.Active, DateTimeOffset.UtcNow);
        var attempts = new AiAttemptStore(db);
        foreach (var (id, role, seat) in new[] { ("attempt-b-board", "board-1", SeatClasses.Broker), ("attempt-b-pos", CouncilRoles.Research, SeatClasses.Puppet) })
            Assert.True(attempts.Begin(new AiAttempt
            {
                Id = id, StartedAt = DateTimeOffset.UtcNow, Role = role, SeatClass = seat,
                Assurance = SeatClasses.Name(SeatClasses.AssuranceOf(seat)!.Value)
            }).Admitted);

        var verbs = new List<BrokerDoor.DoorVerb>([.. BrokerDoor.OrganisationVerbs(() => server), .. BrokerDoor.BoardVerbs(() => server)]);
        string[] boardVerbs = [.. Ops.Board.Select(BrokerDoor.VerbName)];
        Assert.Equal(["board_narrow", "board_propose", "board_decide", "board_list", "board_show", "board_power_propose"], boardVerbs);
        var intent = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["field"] = "plan_day_total", ["from"] = 40, ["to"] = 50 } });

        foreach (var (attempt, role) in new[] { ("attempt-b-board", "board-1"), ("attempt-b-pos", CouncilRoles.Research) })
        {
            var grants = new AgentGrants();
            using var grant = grants.IssueHere(role, attempt);
            var folder = Path.Combine(TestEnv.Home, "door-home-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(folder);
            var ledger = new ToolCallStore(db);
            await using var door = new BrokerDoor
            {
                Grants = grants, Image = SelfImage, Verbs = verbs, Ledger = ledger, Pipe = () => server,
                VerbGate = BrokerDoor.RecordedAssurance(attempts.Get),
                Tools = g => BrokerDoor.ToolsFor(g, _ => folder, () => server, ledger)
            };
            door.Start();
            using var http = Client(grant.Token);
            var listed = (await Post(http, door.Url!, Rpc(1, "tools/list"))).Json.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
            var propose = (await Post(http, door.Url!, Rpc(2, "tools/call",
                new { name = "board_propose", arguments = new { intent, rationale = "the research load needs a larger day" } }))).Json.GetProperty("result");

            if (role == "board-1")
            {
                Assert.Equal(boardVerbs.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
                Assert.False(propose.GetProperty("isError").GetBoolean(), $"board-1's proposal was refused at the door: {Text(propose)}");
                var act = Assert.Single(new BoardStore(db).Acts()).Act;
                Assert.Equal(("board-1", BoardActKind.Propose, attempt), (act.Seat, act.Kind, act.Attempt));
                Assert.Equal(1, new BoardStore(db).InForce().Version);
                foreach (var (name, arguments) in new (string, object)[] { ("trade", new { op = Ops.Status }), ("mandate_list", new { }), ("read_file", new { path = "AGENTS.md" }) })
                {
                    var refused = (await Post(http, door.Url!, Rpc(3, "tools/call", new { name, arguments }))).Json.GetProperty("result");
                    Assert.True(refused.GetProperty("isError").GetBoolean(), $"board-1 reached {name}: {Text(refused)}");
                    Assert.Contains("its door carries the board's verbs alone", Text(refused));
                }
                Assert.Equal(4, ledger.ForAttempt(attempt).Count);
            }
            else
            {
                Assert.Empty(listed.Intersect(boardVerbs));
                Assert.Contains("mandate_list", listed);
                Assert.Contains("trade", listed);
                Assert.True(propose.GetProperty("isError").GetBoolean(), $"a position's board_propose was served: {Text(propose)}");
                Assert.Single(new BoardStore(db).Acts());
                Assert.Single(ledger.ForAttempt(attempt), r => r.Tool == "board_propose" && !r.Served);
            }
        }
    }
}
