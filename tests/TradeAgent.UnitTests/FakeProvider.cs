using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using TradeAgent.Core;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A LOOPBACK STAND-IN FOR THE PROVIDER'S CHAT-COMPLETIONS ENDPOINT. Every harness test talks to this
/// and never to a vendor: the brief forbids a real provider call, and
/// <see cref="SuiteReachesNoVendorTests"/> is the scan that keeps that a property of the SUITE rather
/// than of the tests that happen to exist today.
///
/// <para><b>It answers from a queue.</b> One canned response per request, in order, so a test can say
/// "tool call, tool call, then a message" and get exactly that — which is how a MULTI-REQUEST turn is
/// exercised at all. Running off the end of the queue is deliberate and is not a 500: it repeats the
/// last response, so a test whose guard fails to stop the loop keeps going rather than passing for
/// the wrong reason.</para>
///
/// <para><b>It keeps a mark and the body per request</b> (<see cref="Requests"/>), which is the
/// <c>U-archive-win</c> lesson from <see cref="FakeArchive"/>: a harness that stops answering is
/// indistinguishable from a vendor with nothing to say unless the harness says what it did. The
/// bodies are also the evidence for what the app SENT — the model, the granted tools, the max-output
/// parameter — which no assertion on the app's own state could establish.</para>
///
/// <para>Every response is closed in a <c>finally</c>, for the reason that file gives at length.</para>
/// </summary>
public sealed class FakeProvider : IDisposable
{
    readonly HttpListener _http = new();
    readonly ConcurrentQueue<string> _responses = new();
    readonly ConcurrentQueue<string> _bodies = new();
    readonly ConcurrentQueue<string> _marks = new();
    readonly ConcurrentQueue<string> _keys = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    string _last = "";

    /// <param name="answers">
    /// False accepts every request and answers none of them — the case a harness must fail in seconds
    /// on rather than sit in, because a turn that never returns looks exactly like one that is
    /// thinking.
    /// </param>
    public FakeProvider(bool answers = true)
    {
        Answers = answers;

        for (var attempt = 0; ; attempt++)
        {
            Port = 21000 + Random.Shared.Next(3000);
            _http.Prefixes.Clear();
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try { _http.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { }
        }

        Serving = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch (Exception) { return; }

                var path = ctx.Request.Url!.AbsolutePath;
                Mark($"got {ctx.Request.HttpMethod} {path}");

                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    _bodies.Enqueue(await reader.ReadToEndAsync());
                }
                catch (Exception ex) { Mark($"could not read the body: {ex.GetType().Name}"); }

                _keys.Enqueue(ctx.Request.Headers["Authorization"] ?? "");

                if (!Answers) { Mark("answering nothing, on purpose"); continue; }

                try
                {
                    if (AlwaysAnswer is { } always)
                    {
                        ctx.Response.StatusCode = (int)always;
                        Mark($"answering {(int)always} to everything, on purpose");
                        continue;
                    }

                    if (_responses.TryDequeue(out var next)) _last = next;
                    var body = Encoding.UTF8.GetBytes(_last.Length > 0 ? _last : Message("(nothing canned)"));

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = body.Length;
                    Mark($"answering 200, {body.Length} bytes");
                    await ctx.Response.OutputStream.WriteAsync(body);
                    Mark("write returned");
                }
                catch (Exception ex) { Mark($"THREW {ex.GetType().Name}: {One(ex.Message)}"); }
                finally
                {
                    // THE CLOSE IS THE ANSWER, so it is not optional and it is not inside the try.
                    try { ctx.Response.Close(); Mark("closed"); }
                    catch (Exception ex) { Mark($"close THREW {ex.GetType().Name}: {One(ex.Message)}"); }
                }
            }
        });
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
    public Task Serving { get; }

    /// <inheritdoc cref="FakeProvider(bool)"/>
    public bool Answers { get; }

    /// <summary>When set, every request is answered with this status and no body.</summary>
    public HttpStatusCode? AlwaysAnswer { get; set; }

    /// <summary>Every request body this server received, oldest first.</summary>
    public IReadOnlyList<string> Requests => [.. _bodies];

    /// <summary>Every Authorization header received. Asserted on; never a real key.</summary>
    public IReadOnlyList<string> Keys => [.. _keys];

    /// <summary>What this server received and what it did about it, oldest first.</summary>
    public IReadOnlyList<string> Marks => [.. _marks];

    /// <summary>Queues one canned response, as the raw JSON body.</summary>
    public FakeProvider Answer(string json)
    {
        _responses.Enqueue(json);
        return this;
    }

    void Mark(string what) => _marks.Enqueue($"{_clock.ElapsedMilliseconds,7} ms  prov {what}");

    static string One(string s) => s.ReplaceLineEndings(" ");

    // ---- the canned shapes ------------------------------------------------------------------------
    //
    // Composed rather than pasted, so a test reads as "a tool call, then a message" and the wire shape
    // lives in one place. The usage numbers are always explicit: a response with no usage is its own
    // case (see `WithoutUsage`) and must never be produced by accident.

    /// <summary>A response that ends the turn with text, reporting what it used.</summary>
    public static string Message(string text, long input = 100, long output = 10, long cached = 0,
        string model = "gpt-5.6-luna") =>
        Json.Write(new
        {
            model,
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = text } } },
            usage = Usage(input, output, cached)
        });

    /// <summary>A response that asks for one tool call, reporting what it used.</summary>
    public static string ToolCall(string name, object arguments, string id = "call-1",
        long input = 100, long output = 10, long cached = 0, string model = "gpt-5.6-luna") =>
        Json.Write(new
        {
            model,
            choices = new[]
            {
                new
                {
                    finish_reason = "tool_calls",
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new { id, type = "function", function = new { name, arguments = Json.Write(arguments) } }
                        }
                    }
                }
            },
            usage = Usage(input, output, cached)
        });

    /// <summary>
    /// A response that reports NO usage at all — the case rule 3 calls "usage that never arrives",
    /// which must stay unresolved rather than become a zero.
    /// </summary>
    public static string WithoutUsage(string text) =>
        Json.Write(new
        {
            model = "gpt-5.6-luna",
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = text } } }
        });

    /// <summary>
    /// The usage object, with the cached count NESTED where the OpenAI-compatible body puts it
    /// (<c>usage.prompt_tokens_details.cached_tokens</c>) rather than flat beside the totals. That
    /// nesting is the reason <c>TurnUsage.Nested</c> exists, and a fake that flattened it would test
    /// a shape the provider does not send.
    /// </summary>
    static object Usage(long input, long output, long cached) => new
    {
        prompt_tokens = input,
        completion_tokens = output,
        total_tokens = input + output,
        prompt_tokens_details = new { cached_tokens = cached }
    };

    public void Dispose()
    {
        try { _http.Stop(); _http.Close(); } catch (Exception) { }
    }
}
