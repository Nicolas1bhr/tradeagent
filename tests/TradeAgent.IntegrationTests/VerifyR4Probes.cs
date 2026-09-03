using System.Text.Json;
using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// ADVERSARIAL VERIFY round 4, leg [2]. These are REFUTATION probes: each one PASSES if the defect
/// it names exists. They are not product tests and are not proposed for the branch.
/// </summary>
public class VerifyR4Probes(ITestOutputHelper o)
{
    static string NewPipe() => "ta-vr4-" + Guid.NewGuid().ToString("n")[..12];

    static IpcRequest Buy(string? requestId, string symbol, string? frameId = null)
    {
        var r = new IpcRequest
        {
            Op = Ops.Buy,
            RequestId = requestId,
            Args = new()
            {
                ["symbol"] = JsonSerializer.SerializeToElement(symbol),
                ["quantity"] = JsonSerializer.SerializeToElement("1"),
                ["limit"] = JsonSerializer.SerializeToElement("1")
            }
        };
        if (frameId is not null) r.Id = frameId;
        return r;
    }

    /// <summary>
    /// PROBE 1 (target 3). The 61-char budget and the [A-Za-z0-9-] charset are enforced on
    /// `request_id` ONLY. GatewayPipeServer.Handle does `var rid = req.RequestId ?? req.Id;` — so an
    /// agent that OMITS request_id has its FRAME id carried onto the broker order as
    /// TA-{id}, unchecked in length and in charset.
    ///
    /// This probe PASSES if the defect exists.
    /// </summary>
    [Fact]
    public async Task PROBE1_frame_id_bypasses_the_client_order_id_budget_and_charset()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        // No request_id at all. The frame id is 200 characters and carries '#', '/', ' ' and '_' —
        // every shape SweepRequestIdTests refuses when it arrives in the request_id field.
        var evil = "x#y/z w_" + new string('q', 192);
        var reply = await client.SendAsync(Buy(null, "ES", frameId: evil)).WaitAsync(TimeSpan.FromSeconds(10));

        o.WriteLine($"reply.Ok            = {reply.Ok}");
        o.WriteLine($"frame id length     = {evil.Length}");
        o.WriteLine($"broker orders       = {conn.Broker.Orders.Count}");
        foreach (var ord in conn.Broker.Orders)
            o.WriteLine($"broker ClientOrderId= [{ord.ClientOrderId}] len={ord.ClientOrderId?.Length}");

        Assert.True(reply.Ok, "the frame id was refused after all — no bypass");
        var coid = conn.Broker.Orders.Single().ClientOrderId!;
        Assert.True(coid.Length > 64, $"client order id was {coid.Length} chars, the 64 budget held");
        Assert.Contains("#", coid);
    }

    /// <summary>
    /// PROBE 2 (target 3, positive control + both directions). 61 accepted, 62 refused, charset
    /// refused — but stated on the CLIENT ORDER ID, and with the CLI's own minted op- ids as the
    /// positive control. This probe FAILS if the guard holds (it is written as the product test
    /// would be); it is here to measure, not to refute.
    /// </summary>
    [Fact]
    public async Task PROBE2_request_id_caps_measured()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        foreach (var n in new[] { 60, 61, 62, 63, 64 })
        {
            var id = new string('a', n);
            var r = await client.SendAsync(Buy(id, "ES")).WaitAsync(TimeSpan.FromSeconds(10));
            o.WriteLine($"len={n,3}  ok={r.Ok,-5}  coid_len={TradingGateway.ClientOrderIdFor(id).Length}  err={r.Error?.Code}");
        }

        // The CLI's own minted ids, as the positive control.
        var sweep = (JsonElement)(await client.SendAsync(new IpcRequest { Op = Ops.CancelAll, RequestId = "vr4-sweep" })
            .WaitAsync(TimeSpan.FromSeconds(20))).Data!;
        foreach (var r in sweep.GetProperty("requests").EnumerateArray())
        {
            var id = r.GetProperty("request_id").GetString()!;
            o.WriteLine($"minted [{id}] len={id.Length} coid_len={TradingGateway.ClientOrderIdFor(id).Length} " +
                        $"charset_ok={System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9-]+$")}");
        }
        o.WriteLine($"broker orders now: {conn.Broker.Orders.Count}");
        foreach (var ord in conn.Broker.Orders)
            o.WriteLine($"  [{ord.ClientOrderId}] len={ord.ClientOrderId?.Length}");
    }

    /// <summary>
    /// PROBE 3 (target 3, same root cause). The reserved `op-` prefix is what makes a minted sweep
    /// id uncollidable "by construction" (GatewayPipeServer.cs:436 comment). It is checked on
    /// req.RequestId only, so the FRAME id can carry it. PASSES if the defect exists.
    /// </summary>
    [Fact]
    public async Task PROBE3_frame_id_can_take_the_reserved_minted_prefix()
    {
        var (gw, conn, db) = await TestEnv.Ready(faults: new FaultProfile { Fill = FillBehaviour.LeaveWorking });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var reserved = "op-deadbeef-cancelall-0";
        var reply = await client.SendAsync(Buy(null, "ES", frameId: reserved)).WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"ok={reply.Ok} err={reply.Error?.Code}");
        foreach (var ord in conn.Broker.Orders) o.WriteLine($"  broker coid=[{ord.ClientOrderId}]");

        // And it really is in the idempotency store under that id: replay returns the PLACE record.
        var replay = await client.SendAsync(Buy(null, "NQ", frameId: reserved)).WaitAsync(TimeSpan.FromSeconds(10));
        o.WriteLine($"replay ok={replay.Ok} broker order count={conn.Broker.Orders.Count} (1 => the id is a live idempotency key)");

        Assert.True(reply.Ok, "the reserved prefix was refused in the frame id — no bypass");
        Assert.Equal("TA-" + reserved, conn.Broker.Orders.Single().ClientOrderId);
    }

    /// <summary>
    /// PROBE 4 (target 4, round-1 exploit re-run on the REBASED tip). session:"operator" in seven
    /// spellings with the kill switch pressed. PASSES if any spelling escapes INVALID_REQUEST or
    /// reaches the broker.
    /// </summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("Operator")]
    [InlineData("OPERATOR")]
    [InlineData("oPeRaToR")]
    [InlineData(" operator")]
    [InlineData("operator ")]
    [InlineData("\toperator")]
    public async Task PROBE4_forged_operator_session_with_stop_pressed(string spelling)
    {
        var (gw, conn, db) = await TestEnv.Ready(settings: st =>
        {
            st.Mode = TradingMode.LIVE_CONFIRM;
            st.LiveActivated = true;
            st.AiTradingStopped = true;        // STOP pressed
        });
        using var _1 = db;
        var pipe = NewPipe();
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        await using var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var req = Buy("vr4-op-" + Math.Abs(spelling.GetHashCode()), "ES");
        req.Session = spelling;
        var reply = await client.SendAsync(req).WaitAsync(TimeSpan.FromSeconds(10));

        o.WriteLine($"spelling=[{spelling}] ok={reply.Ok} code={reply.Error?.Code} msg={reply.Error?.Message}");
        o.WriteLine($"broker orders={conn.Broker.Orders.Count}");

        // The probe passes if the exploit still works.
        Assert.True(reply.Ok || conn.Broker.Orders.Count > 0,
            $"refused as {reply.Error?.Code} with {conn.Broker.Orders.Count} broker order(s) — the guard held");
    }
}
