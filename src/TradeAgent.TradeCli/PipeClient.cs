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

    /// <summary>
    /// One attempt, reporting what is KNOWN about where the frame got to rather than throwing.
    ///
    /// Every exit path returns a <see cref="TransportResult"/>, and that is the point of it. The
    /// failures that matter most here — a half-written request, a half-read reply, a service that
    /// hangs up mid-frame — arrive as <c>IOException</c>, <c>ObjectDisposedException</c> and
    /// <c>JsonException</c>, none of which were wrapped, while <c>Program.cs</c> caught only
    /// <c>TradeAgentException</c>. So the most common lost-reply cases killed the process with no
    /// structured output and no recovery guidance (Codex F7), which is the one situation the whole
    /// replay contract exists for.
    ///
    /// <see cref="TransportOutcome.NothingWritten"/> is claimed only when it can be SHOWN — no pipe,
    /// or a pipe already disconnected before the write was attempted. Everything else is
    /// <see cref="TransportOutcome.PossiblyWritten"/>, because a write that threw may still have put
    /// bytes on the wire and the safe reading of "I do not know" is that the order may exist.
    /// </summary>
    public async Task<TransportResult> TrySendAsync(IpcRequest req, CancellationToken ct = default)
    {
        if (_w is null || _r is null || _pipe is null)
            return TransportResult.Nothing(new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "not connected"));

        // Provable, and the only place it is: a pipe that is already down cannot have taken bytes.
        if (!_pipe.IsConnected)
            return TransportResult.Nothing(new TradeAgentException(
                ErrorCode.IPC_UNAVAILABLE, "the trading service closed the connection before the request was sent"));

        try { await _w.WriteLineAsync(Json.Write(req).AsMemory(), ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return TransportResult.Possibly(new TradeAgentException(
                ErrorCode.IPC_UNAVAILABLE, "the connection failed while the request was being sent", ex));
        }

        // From here the frame is out of this process. Nothing below may report NothingWritten.
        string? line;
        try { line = await _r.ReadLineAsync(ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return TransportResult.Possibly(new TradeAgentException(
                ErrorCode.IPC_UNAVAILABLE, "the connection failed while the reply was being read", ex));
        }

        if (line is null)
            return TransportResult.Possibly(new TradeAgentException(
                ErrorCode.IPC_UNAVAILABLE, "the trading service closed the connection"));

        try
        {
            return Json.Read<IpcResponse>(line) is { } reply
                ? TransportResult.Answered(reply)
                : TransportResult.Possibly(new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "unreadable reply"));
        }
        catch (Exception ex)
        {
            // A truncated reply parses as garbage, and a truncated reply is a reply we did not get.
            return TransportResult.Possibly(new TradeAgentException(
                ErrorCode.IPC_UNAVAILABLE, "the reply from the trading service was incomplete", ex));
        }
    }

    /// <summary>
    /// Sends one frame EXACTLY AS WRITTEN and reads one reply.
    ///
    /// For tests that need a frame the serializer would not produce — a field explicitly null, a
    /// field the model omits — which is the only way to exercise what the server does with one.
    /// </summary>
    public async Task<IpcResponse> SendRawAsync(string frame, CancellationToken ct = default)
    {
        if (_w is null || _r is null) throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "not connected");
        await _w.WriteLineAsync(frame.AsMemory(), ct);
        var line = await _r.ReadLineAsync(ct)
            ?? throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "the trading service closed the connection");
        return Json.Read<IpcResponse>(line) ?? throw new TradeAgentException(ErrorCode.IPC_UNAVAILABLE, "unreadable reply");
    }

    /// <summary>
    /// The throwing form, for callers that have nothing to reconcile — the handshake, and the tests
    /// that drive the gateway over the pipe. A mutating call must use <see cref="TrySendAsync"/>.
    /// </summary>
    public async Task<IpcResponse> SendAsync(IpcRequest req, CancellationToken ct = default)
    {
        var result = await TrySendAsync(req, ct);
        return result.Reply ?? throw result.Failure!;
    }

    /// <summary>
    /// Closing down never throws, and that is not tidiness — it is the difference between a
    /// structured failure and a crash.
    ///
    /// `Program.cs` holds this in an `await using`, so disposal runs AFTER its try/catch has already
    /// chosen an exit code and printed the replay JSON. Disposing a StreamWriter flushes it, and
    /// flushing into a pipe whose far end has gone throws IOException — from outside every handler,
    /// on the exact path where the agent most needs the structured output. Measured while adding the
    /// F7 tests: against a service that answered the handshake and closed, `trade buy` exited 134
    /// (SIGABRT, unhandled) with the recovery JSON already on stdout and no way for the caller to
    /// see the exit code it deserved.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { if (_w is not null) await _w.DisposeAsync(); } catch (Exception) { /* the far end is gone */ }
        try { _r?.Dispose(); } catch (Exception) { /* the far end is gone */ }
        try { if (_pipe is not null) await _pipe.DisposeAsync(); } catch (Exception) { /* the far end is gone */ }
    }
}
