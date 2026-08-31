using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// The material ledger as the agent actually reaches it: over the pipe, through the gateway, with a
/// real handshake. The unit tests cover the store; these cover the half an agent can touch — and in
/// particular the two things that would quietly hollow the record out.
/// </summary>
public class MaterialOverPipeTests
{
    static string NewPipe() => "ta-mat-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, Database Db, PipeClient Client, IAsyncDisposable Server, string Root)> Connected()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();

        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);

        var root = Path.Combine(TestEnv.Home, $"mws-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        return (gw, db, client, server, root);
    }

    static void Drop(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task An_agent_can_see_what_the_owner_handed_it_and_record_what_it_did()
    {
        var (gw, db, client, server, root) = await Connected();
        using var _ = db;
        await using var _2 = server;
        await using var _3 = client;

        Drop(root, "inbox/backtest.py", "print('hi')");
        new MaterialScanner(db, root).Scan();

        var list = await client.SendAsync(new IpcRequest { Op = Ops.MaterialList });
        Assert.True(list.Ok, Json.Write(list.Error));
        Assert.Contains("inbox/backtest.py", Json.Write(list.Data));

        var sha = gw.Materials.Present().Single().Sha256![..12];
        var note = await client.SendAsync(new IpcRequest
        {
            Op = Ops.MaterialNote,
            Session = "agent-7",
            Args = GatewayThroughPipeTests.Args(("kind", "ran"), ("sha", sha), ("text", "ran it on the sample"))
        });
        Assert.True(note.Ok, Json.Write(note.Error));

        var recorded = Assert.Single(gw.Materials.NotesFor(gw.Materials.Present().Single().Sha256!));
        Assert.Equal(MaterialNoteKind.Ran, recorded.Kind);
        Assert.Equal("agent-7", recorded.Session);
        Assert.Equal("agent", recorded.Author);
    }

    [Fact]
    public async Task A_note_about_a_file_that_is_not_in_the_ledger_is_refused()
    {
        // A note pointing at nothing looks like a record and is not one. Storing it would let the
        // history fill with claims nobody can follow back to a file, which is the failure the ledger
        // exists to prevent, wearing the ledger's own clothes.
        var (gw, db, client, server, _) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var note = await client.SendAsync(new IpcRequest
        {
            Op = Ops.MaterialNote,
            Args = GatewayThroughPipeTests.Args(("kind", "ran"), ("sha", "deadbeefdead"), ("text", "trust me"))
        });

        Assert.False(note.Ok);
        Assert.Empty(gw.Materials.RecentNotes(10));
    }

    [Fact]
    public async Task A_derivation_without_a_source_is_refused()
    {
        var (gw, db, client, server, root) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        Drop(root, "inbox/raw.csv", "1,2,3");
        new MaterialScanner(db, root).Scan();
        var sha = gw.Materials.Present().Single().Sha256![..12];

        var note = await client.SendAsync(new IpcRequest
        {
            Op = Ops.MaterialNote,
            Args = GatewayThroughPipeTests.Args(("kind", "derived"), ("sha", sha), ("text", "cleaned it up"))
        });

        Assert.False(note.Ok);
        Assert.Contains("derived from", note.Error!.Message);
        Assert.Empty(gw.Materials.RecentNotes(10));
    }

    [Fact]
    public async Task The_agent_channel_still_offers_no_way_to_change_what_was_observed()
    {
        // The ledger adds two operations to the agent's surface. Neither may become a way to edit
        // measurement — that would hand the agent the ability to rewrite the record of itself, which
        // is the same class of hole as operator authority leaking onto this pipe.
        var (gw, db, client, server, root) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        Drop(root, "inbox/tool.exe", "payload");
        new MaterialScanner(db, root).Scan();
        var before = gw.Materials.Present().Single();

        foreach (var kind in new[] { "ran", "used", "note" })
            await client.SendAsync(new IpcRequest
            {
                Op = Ops.MaterialNote,
                Args = GatewayThroughPipeTests.Args(("kind", kind), ("sha", before.Sha256![..12]), ("text", "x"))
            });

        var after = gw.Materials.Present().Single();
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.RelPath, after.RelPath);
        Assert.Equal(before.FirstSeenAt, after.FirstSeenAt);
        Assert.Null(after.RemovedAt);
    }

    [Fact]
    public async Task The_schema_tells_an_agent_the_material_commands_exist()
    {
        // AGENTS.md says `trade schema --json` is authoritative. If the ledger is missing from it, an
        // agent that trusts the schema over the prose will never record anything.
        var (_, db, client, server, _2) = await Connected();
        using var _1 = db;
        await using var _3 = server;
        await using var _4 = client;

        var schema = await client.SendAsync(new IpcRequest { Op = Ops.Schema });
        Assert.True(schema.Ok);
        var text = Json.Write(schema.Data);
        Assert.Contains(Ops.MaterialList, text);
        Assert.Contains(Ops.MaterialNote, text);
    }
}
