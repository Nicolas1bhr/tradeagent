using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeAgent.AgentRuntime;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE BROKER'S SHELL-FREE DOOR (<c>U-thinking-seats-b</c>, <c>docs/ORCHESTRATION.md</c> § 2.7): the app's own MCP server on
/// loopback, spoken to here by a real HTTP client on every runner.
/// </summary>
[Collection("containment")]
public class BrokerDoorTests
{
    /// <summary>
    /// ITEM 1, GUARD: THE DOOR IS ON LOOPBACK, ON A PORT CHOSEN AT START, ANSWERS MCP's HANDSHAKE AS JSON AND OPENS NO STREAM.
    /// </summary>
    [Fact]
    public async Task The_door_listens_on_loopback_answers_the_handshake_and_opens_no_stream()
    {
        await using var door = new BrokerDoor();
        door.Start();

        Assert.NotNull(door.Url);
        Assert.Equal("127.0.0.1", door.Url!.Host);
        Assert.NotEqual(0, door.Url.Port);
        Assert.Equal(BrokerDoor.Route, door.Url.AbsolutePath);

        using var http = Client();
        var init = await Post(http, door.Url, Rpc(1, "initialize", new { protocolVersion = "2025-06-18" }));
        Assert.Equal(HttpStatusCode.OK, init.Status);
        Assert.Equal("2025-06-18", init.Json.GetProperty("result").GetProperty("protocolVersion").GetString());

        var note = await Post(http, door.Url, JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        Assert.Equal(HttpStatusCode.Accepted, note.Status);

        var stream = await http.GetAsync(door.Url);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, stream.StatusCode);
    }

    // ---- the client --------------------------------------------------------------------------------------------------------

    internal static HttpClient Client(string? bearer = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (bearer is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return http;
    }

    internal static string Rpc(int id, string method, object? parameters = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { }
        });

    internal sealed record Reply(HttpStatusCode Status, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    internal static async Task<Reply> Post(HttpClient http, Uri url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
