using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Security;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-key-host-pin — THE OWNER'S PASTED KEY GOES ONLY TO THE ORIGIN THEY SAW WHEN THEY PASTED IT.
///
/// <para><b>The route this closes.</b> Where the harness sends a request is manifest data, and
/// <c>runtimes.json</c> overrides the built-in row WHOLE, <c>BaseUrl</c> included. The vendor CLI runs
/// unconfined as the same user and can write that file. The key is memory-only, so after any restart —
/// a self-update is one — the owner pastes it again, and until this unit the next turn sent it, in the
/// Authorization header, to whatever origin the file named.</para>
///
/// <para><b>What holds now.</b> <see cref="HarnessKey"/> keeps the key WITH the origin it was pasted
/// for (scheme, host, port — <see cref="KeyOrigin"/>), and releases it only through
/// <see cref="HarnessKey.ReadFor"/>, for a request to that same origin. Anything else is refused and
/// the key is forgotten. Presence — the health row, the auth state, both <c>StartAsync</c>s — reads
/// <see cref="HarnessKey.Held"/>, which never clears anything.</para>
///
/// <para>Every listener is <see cref="FakeProvider"/> on <c>http://127.0.0.1:&lt;port&gt;</c>, which is
/// exactly the case the comparison has to get right: two listeners are ONE host on TWO ports, so a check
/// on the host alone would hand the key to the second one. No key here is real and no provider is
/// reached.</para>
/// </summary>
[Collection(VendorOverrideFiles.Name)]
public class HarnessKeyOriginTests : IDisposable
{
    readonly string _home = Path.Combine(TestEnv.Home, $"key-origin-{Guid.NewGuid():n}");

    /// <summary>A pretend key nobody else could have produced, so finding it anywhere is evidence.</summary>
    readonly string _pasted = $"not-a-real-credential-{Guid.NewGuid():n}";

    public HarnessKeyOriginTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        if (File.Exists(RuntimeCatalog.OverridePath)) File.Delete(RuntimeCatalog.OverridePath);
        if (File.Exists(CostCatalog.OverridePath)) File.Delete(CostCatalog.OverridePath);
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    /// <summary>What a request carrying <paramref name="key"/> carries in its Authorization header.</summary>
    static string Sent(string key) => $"Bearer {key}";

    /// <summary>The shipped harness row, pointed at a loopback listener. Everything else is as it ships.</summary>
    static RuntimeManifest PointedAt(FakeProvider provider)
    {
        var manifest = RuntimeCatalog.Require(ApiAgentRuntime.RuntimeId);
        manifest.BaseUrl = provider.BaseUrl;
        return manifest;
    }

    static ApiAgentRuntime Runtime(RuntimeManifest manifest, HarnessKey holder) =>
        new(manifest, holder, requestTimeout: TimeSpan.FromSeconds(10));

    IAgentConversation Research(ApiAgentRuntime runtime) =>
        runtime.OpenConversation(CouncilRoles.Research, () => _home,
            () => new Dictionary<string, string>(), () => null);

    // ---- the guard -------------------------------------------------------------------------------

    /// <summary>
    /// THE TEST THAT WAS RED FIRST. Written against the API as it stood — <c>Set(key)</c> and a reader
    /// that handed the key to any request — it failed behaviourally: "the listener on port 51085 received
    /// 1 request(s), 1 of them carrying the key pasted for the listener on port 51084". Moved to the new
    /// signature with its name kept.
    ///
    /// <para>The key goes to the listener it was pasted for, and then a runtime pointed at a second
    /// listener — the same host, another port — runs a turn with the same holder. That second listener
    /// must see nothing at all, not merely no key: a request is the owner's Situation leaving the
    /// machine. Take the origin comparison out of <c>HarnessKey.ReadFor</c>, or compare
    /// <see cref="Uri.Host"/> instead of the origin, and this goes red on exactly that line.</para>
    /// </summary>
    [Fact]
    public async Task A_key_pasted_for_one_origin_is_never_sent_to_another()
    {
        using var pastedFor = new FakeProvider();
        using var elsewhere = new FakeProvider();
        pastedFor.Answer(FakeProvider.Message("from the address the owner saw"));
        elsewhere.Answer(FakeProvider.Message("from an address nobody showed the owner"));

        var holder = new HarnessKey();
        holder.Set(_pasted, pastedFor.BaseUrl);

        using (var first = Runtime(PointedAt(pastedFor), holder))
            await Research(first).SendMissionAsync("## Situation");
        Assert.Equal(1, pastedFor.Keys.Count(k => k == Sent(_pasted)));

        using (var second = Runtime(PointedAt(elsewhere), holder))
            await Research(second).SendMissionAsync("## Situation");

        var carried = elsewhere.Keys.Count(k => k == Sent(_pasted));
        Assert.True(carried == 0 && elsewhere.Requests.Count == 0,
            $"the listener on port {elsewhere.Port} received {elsewhere.Requests.Count} request(s), {carried} of "
            + $"them carrying the key pasted for the listener on port {pastedFor.Port} (same host, 127.0.0.1)");
    }

    /// <summary>
    /// THE GUARD THE OTHER WAY: THE OWNER'S OWN CASE STILL WORKS. A pin that refused everything would
    /// pass the test above and leave the worker dead.
    ///
    /// <para>First the SHIPPED row, untouched and never contacted: a key pasted for its base address is
    /// released for its full endpoint — the path is not part of an origin — and for the same endpoint
    /// with the default port written out, which is the same place. Then the wire: the same row pointed
    /// at a listener, the key pasted for that listener, two whole turns, the key on both — a match does
    /// not clear it.</para>
    /// </summary>
    [Fact]
    public async Task The_built_in_origin_still_receives_the_key()
    {
        var shipped = RuntimeCatalog.BuiltIn().Single(m => m.Id == ApiAgentRuntime.RuntimeId);
        var builtIn = new HarnessKey();
        builtIn.Set(_pasted, shipped.BaseUrl);

        Assert.Equal(KeyOrigin.Of(shipped.Endpoint), builtIn.Origin);
        Assert.StartsWith("https://", builtIn.Origin);
        Assert.Equal(_pasted, builtIn.ReadFor(KeyOrigin.Of(shipped.Endpoint)).Key);
        var endpoint = new Uri(shipped.Endpoint);
        Assert.Equal(_pasted, builtIn.ReadFor($"https://{endpoint.Host}:443{endpoint.AbsolutePath}").Key);
        Assert.True(builtIn.Held);

        using var provider = new FakeProvider();
        provider.Answer(FakeProvider.Message("read it", input: 10, output: 2));
        var holder = provider.Holding(_pasted);
        using var runtime = Runtime(PointedAt(provider), holder);
        var conversation = Research(runtime);
        var ended = new List<AgentTurnEnded>();
        conversation.TurnEnded += ended.Add;

        await conversation.SendMissionAsync("## Situation");
        await conversation.SendMissionAsync("## Situation");

        Assert.Equal([ApiConversation.Completed, ApiConversation.Completed], ended.Select(e => e.ExitCode));
        Assert.Equal(2, provider.Keys.Count(k => k == Sent(_pasted)));
        Assert.True(holder.Held);
    }

    // ---- the refusal -----------------------------------------------------------------------------

    /// <summary>
    /// A MISMATCH IS REFUSED BEFORE ANY REQUEST, CLEARS THE KEY AND CHARGES NOTHING — on the metered
    /// path, admitted first, so the reservation is really committed before the refusal.
    ///
    /// <para>Nothing reached either listener: not the key and not the request, because a request is the
    /// owner's Situation leaving the machine. The key is gone even for the origin it WAS pasted for —
    /// something tried to send it elsewhere, and the owner looks again before it goes anywhere. And the
    /// launch row is ENDED at ZERO with its reservation released: no provider was asked for anything, so
    /// none can have billed. Take the withheld-key branch out of <c>TurnMeter.Charge</c> and the row is
    /// charged its reservation (1.28) for a turn that never left the app.</para>
    /// </summary>
    [Fact]
    public async Task An_origin_mismatch_refuses_before_any_request_clears_the_key_and_charges_nothing()
    {
        using var db = TestEnv.NewDb();
        var allowance = TurnAllowance.From(1_200_000, 20_000);
        // 1.20 M at 1.00 plus 20 k at 4.00 per million: a reservation of 1.28 a turn, well under the ceiling.
        var meter = new TurnMeter(db, () => 50m, runtimeId: () => ApiAgentRuntime.RuntimeId,
            recordPath: Path.Combine(_home, "turns.jsonl"), owner: () => new OwnerPrice(1m, 4m),
            model: () => "gpt-5.6-luna", allowance: () => allowance, share: _ => 1m);

        using var pastedFor = new FakeProvider();
        using var elsewhere = new FakeProvider();
        pastedFor.Answer(FakeProvider.Message("from the address the owner saw"));
        elsewhere.Answer(FakeProvider.Message("from an address nobody showed the owner"));
        var holder = pastedFor.Holding(_pasted);

        using var runtime = new ApiAgentRuntime(PointedAt(elsewhere), holder, allowance: () => allowance,
            requestTimeout: TimeSpan.FromSeconds(10));
        var conversation = Research(runtime);
        using var metering = meter.Attach(conversation, CouncilRoles.Research);
        AgentTurnEnded? ended = null;
        conversation.TurnEnded += e => ended = e;

        await conversation.SendAsync("what is the plan?");

        Assert.Empty(elsewhere.Requests);
        Assert.Empty(pastedFor.Requests);
        Assert.False(holder.Held);
        Assert.Null(holder.ReadFor(pastedFor.BaseUrl).Key);

        var row = Assert.Single(Attempts(db));
        Assert.Equal(AiAttemptState.ENDED, row.State);
        Assert.Equal(1.28m, row.ReservedCost);
        Assert.Equal(0m, row.Cost);
        var today = meter.TodayFor(CouncilRoles.Research);
        Assert.Equal(0m, today.Spent);
        Assert.Equal(0m, today.Reserved);
        Assert.Equal(0, today.UnreportedTurns);

        Assert.NotNull(ended);
        Assert.Null(ended!.Usage);
        Assert.Equal(ApiConversation.NotStarted, ended.ExitCode);
        Assert.Equal(ApiConversation.EndedKeyRefused, ended.Outcome);

        var said = Assert.Single(conversation.History,
            t => t.Role == ChatRole.System && t.Text.Contains(KeyOrigin.Of(pastedFor.BaseUrl)!, StringComparison.Ordinal));
        Assert.Equal(
            Labels.HarnessKeyPastedForAnotherOrigin(KeyOrigin.Of(pastedFor.BaseUrl)!, KeyOrigin.Of(elsewhere.BaseUrl)),
            said.Text);
        Assert.Contains(KeyOrigin.Of(elsewhere.BaseUrl)!, said.Text, StringComparison.Ordinal);
        Assert.Contains("paste it again on the Safety page if you meant that", said.Text, StringComparison.Ordinal);
        Assert.Equal(said.Text, ended.KeyWithheld);

        // AND THE ROW SAYS WHY IT COST NOTHING, in the same words: a zero with no reason is a zero nobody
        // can check afterwards.
        using var context = System.Text.Json.JsonDocument.Parse(row.Context!);
        Assert.Equal(ApiConversation.EndedKeyRefused, context.RootElement.GetProperty("ended").GetString());
        Assert.Equal(said.Text, context.RootElement.GetProperty("refused").GetString());
    }

    static IReadOnlyList<AiAttempt> Attempts(Database db)
    {
        var ids = new List<string>();
        using (var c = db.Cmd("SELECT id FROM ai_attempt ORDER BY started_at, id"))
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));

        var store = new AiAttemptStore(db);
        return [.. ids.Select(id => store.Get(id)!)];
    }

    // ---- presence is not a send ------------------------------------------------------------------

    /// <summary>
    /// EVERY PRESENCE CHECK THE PRODUCT MAKES, TWICE, ON A RUNTIME THAT POINTS SOMEWHERE ELSE — and the
    /// key is still held afterwards, still bound to the origin it was pasted for, and nothing was sent.
    ///
    /// <para>The health row polls; the auth state, the runtime's own <c>StartAsync</c> and the
    /// conversation's status line all ask too. If any of them asked through
    /// <see cref="HarnessKey.ReadFor"/>, the mismatch would clear the key on the first poll and the
    /// owner would be told nothing — which is why presence is <see cref="HarnessKey.Held"/> and only a
    /// request path may read the key.</para>
    /// </summary>
    [Fact]
    public async Task A_presence_check_never_clears_the_key()
    {
        using var pastedFor = new FakeProvider();
        using var elsewhere = new FakeProvider();
        var holder = pastedFor.Holding(_pasted);

        using var runtime = Runtime(PointedAt(elsewhere), holder);
        var conversation = Research(runtime);

        for (var i = 0; i < 2; i++)
        {
            Assert.True(holder.Held);
            Assert.True(runtime.KeyHeld);
            Assert.Equal(AuthState.Authenticated, await runtime.GetAuthenticationStateAsync());
            Assert.Equal(HealthState.READY, await runtime.GetHealthAsync());
            await runtime.StartAsync();
            await runtime.RestartAsync();
            await conversation.StartAsync();
        }

        Assert.True(holder.Held);
        Assert.Equal(KeyOrigin.Of(pastedFor.BaseUrl), holder.Origin);
        Assert.Empty(elsewhere.Requests);
        Assert.Empty(pastedFor.Requests);
        Assert.Equal(2, conversation.History.Count(t => t.Role == ChatRole.System && t.Text.EndsWith(" is ready.")));
        Assert.Equal(_pasted, holder.ReadFor(pastedFor.BaseUrl).Key);
    }
}
