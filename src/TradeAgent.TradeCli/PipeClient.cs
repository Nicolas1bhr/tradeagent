using System.IO.Pipes;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Security;

namespace TradeAgent.TradeCli;

/// <summary>Thin client: authenticate, send one frame, read one reply.</summary>
public sealed class PipeClient : IAsyncDisposable
{
    NamedPipeClientStream? _pipe;
    StreamReader? _r;
    StreamWriter? _w;

    public async Task ConnectAsync(int timeoutMs = 5000, string? pipeName = null, CancellationToken ct = default)
    {
        var token = IpcToken.Peek()
            ?? throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "no access token found; is TradeAgent installed and running?");

        _pipe = new NamedPipeClientStream(".", pipeName ?? Paths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await _pipe.ConnectAsync(timeoutMs, ct); }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "the TradeAgent trading service is not running", ex);
        }

        _r = new StreamReader(_pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        _w = new StreamWriter(_pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };

        var hello = await SendAsync(new IpcRequest { Op = Ops.Hello, Token = token }, ct);
        if (!hello.Ok)
            throw new TradeAgentException(ErrorCode.IPC_UNAUTHENTICATED, hello.Error?.Message ?? "handshake refused");
    }

    public async Task<IpcResponse> SendAsync(IpcRequest req, CancellationToken ct = default)
    {
        if (_w is null || _r is null) throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "not connected");
        await _w.WriteLineAsync(Json.Write(req));
        var line = await _r.ReadLineAsync(ct)
            ?? throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "the trading service closed the connection");
        return Json.Read<IpcResponse>(line) ?? throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "unreadable reply");
    }

    public async ValueTask DisposeAsync()
    {
        if (_w is not null) await _w.DisposeAsync();
        _r?.Dispose();
        if (_pipe is not null) await _pipe.DisposeAsync();
    }
}
