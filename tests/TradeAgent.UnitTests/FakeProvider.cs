using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TradeAgent.Core;
using TradeAgent.Security;

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
    /// <summary>The one method that carries no entity body. See the comment at the write below.</summary>
    const string Head = "HEAD";

    readonly HttpListener _http;
    readonly ConcurrentQueue<string> _responses = new();
    readonly ConcurrentQueue<string> _bodies = new();
    readonly ConcurrentQueue<string> _marks = new();
    readonly ConcurrentQueue<string> _keys = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly CancellationTokenSource _release = new();
    string _last = "";

    /// <param name="answers">
    /// False accepts every request and answers none of them — the case a harness must fail in seconds
    /// on rather than sit in, because a turn that never returns looks exactly like one that is
    /// thinking.
    /// </param>
    public FakeProvider(bool answers = true)
    {
        Answers = answers;

        _http = Loopback.Start(out var port);
        Port = port;

        Serving = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch (Exception) { return; }

                var path = ctx.Request.Url!.AbsolutePath;
                var method = ctx.Request.HttpMethod;
                Mark($"got {method} {path}");

                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    _bodies.Enqueue(await reader.ReadToEndAsync());
                }
                catch (Exception ex) { Mark($"could not read the body: {ex.GetType().Name}"); }

                _keys.Enqueue(ctx.Request.Headers["Authorization"] ?? "");

                // THE HOOK, RUN AS THE REQUEST ARRIVES and before anything is answered: what the app had already
                // written by the moment its request reached the host is a fact only the host's side can witness.
                try { Arriving?.Invoke(); }
                catch (Exception ex) { Mark($"the arrival hook THREW {ex.GetType().Name}: {One(ex.Message)}"); }

                if (!Answers) { Mark("answering nothing, on purpose"); continue; }

                try
                {
                    // THE ONE RESPONSE HEADER A TEST MAY ASK FOR (U-decision-card): the host's own word on when to come
                    // back, written exactly as the test spelled it — a number of seconds, a date, or something unreadable.
                    if (RetryAfter is { } wait)
                    {
                        ctx.Response.AddHeader("Retry-After", wait);
                        Mark($"Retry-After: {wait}");
                    }

                    if (AlwaysAnswer is { } always)
                    {
                        ctx.Response.StatusCode = (int)always;
                        if (ErrorBody is { } page)
                        {
                            ctx.Response.ContentLength64 = page.Length;
                            Mark($"answering {(int)always} with {page.Length} bytes, on purpose");
                            await ctx.Response.OutputStream.WriteAsync(page);
                            Mark("write returned");
                        }
                        else Mark($"answering {(int)always} to everything, on purpose");
                        continue;
                    }

                    if (_responses.TryDequeue(out var next)) _last = next;
                    var body = Encoding.UTF8.GetBytes(_last.Length > 0 ? _last : Message("(nothing canned)"));

                    if (StallsBody)
                    {
                        // A 200 WHOSE BODY NEVER FINISHES (U-wire-body-deadline): the status, the headers and the first
                        // part of a valid body are written and flushed, then the rest is held until this host is disposed.
                        // Chunked, so no length is declared that the close would then have to break.
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.SendChunked = true;
                        var first = body.AsMemory(0, Math.Max(1, body.Length / 2));
                        Mark($"answering 200, {first.Length} of {body.Length} bytes, then holding the rest, on purpose");
                        await ctx.Response.OutputStream.WriteAsync(first);
                        await ctx.Response.OutputStream.FlushAsync();
                        Mark("first part flushed");
                        try { await Task.Delay(Timeout.Infinite, _release.Token); }
                        catch (OperationCanceledException) { Mark("released"); }
                        continue;
                    }

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = body.Length;

                    // A HEAD IS ANSWERED WITH THE HEADERS AND NOTHING ELSE — the FakeArchive lesson,
                    // kept here although the harness only ever POSTs: http.sys allows no body on a HEAD,
                    // so writing one throws, the throw skips the close, and the client sits on its own
                    // timeout reading the silence as a provider that had nothing to say. That cost
                    // windows-latest thirty minutes once already (see FakeArchive), and a harness that
                    // could do it again is worse than one line of method check.
                    if (string.Equals(method, Head, StringComparison.OrdinalIgnoreCase))
                    {
                        Mark($"answering 200, {body.Length} bytes declared, no body because this is a HEAD");
                    }
                    else
                    {
                        Mark($"answering 200, {body.Length} bytes");
                        await ctx.Response.OutputStream.WriteAsync(body);
                        Mark("write returned");
                    }
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

    /// <summary>When set, every request is answered with this status, and with <see cref="ErrorBody"/> or no body.</summary>
    public HttpStatusCode? AlwaysAnswer { get; set; }

    /// <summary>
    /// When true, a request is answered with status 200, a JSON content type, the headers flushed and the first part of
    /// a valid body — and the rest is held until this host is disposed (<c>U-wire-body-deadline</c>): a host whose
    /// headers arrive at once and whose body then stalls. Nothing in it outlives <see cref="Dispose"/>.
    /// </summary>
    public bool StallsBody { get; set; }

    /// <summary>
    /// The body served with <see cref="AlwaysAnswer"/>'s status, or none — a host's error page can be anything, larger than
    /// a caller reads included (<c>U-decision-card</c>). Written whole with its length declared; a caller that stops
    /// reading cuts it, and the write's failure is only marked.
    /// </summary>
    public byte[]? ErrorBody { get; set; }

    /// <summary>
    /// When set, every response carries this as its <c>Retry-After</c> header, verbatim — the host asking the caller to
    /// wait, in seconds or until a date (<c>U-decision-card</c>). A value no reader can parse is how a test sends an
    /// unreadable one.
    /// </summary>
    public string? RetryAfter { get; set; }

    /// <summary>
    /// RUN AS EACH REQUEST ARRIVES, after its body and headers are kept and before it is answered (<c>U-decision-port</c>):
    /// a test reads the app's ledger here to see what was written BEFORE the request left — which no assertion made
    /// after the call returns could tell apart from what was written after.
    /// </summary>
    public Action? Arriving { get; set; }

    /// <summary>Every request body this server received, oldest first.</summary>
    public IReadOnlyList<string> Requests => [.. _bodies];

    /// <summary>Every Authorization header received. Asserted on; never a real key.</summary>
    public IReadOnlyList<string> Keys => [.. _keys];

    /// <summary>What this server received and what it did about it, oldest first.</summary>
    public IReadOnlyList<string> Marks => [.. _marks];

    /// <summary>
    /// A KEY HOLDER WITH <paramref name="key"/> PASTED FOR THIS SERVER'S ORIGIN — what the Safety page
    /// does when the harness points here — or holding nothing when the key is null. The harness releases
    /// a key only for the origin it was pasted for (<c>U-key-host-pin</c>), so a test that wants its
    /// requests to carry one pastes it for the address they go to, exactly as the owner would.
    /// </summary>
    public HarnessKey Holding(string? key)
    {
        var holder = new HarnessKey();
        holder.Set(key, BaseUrl);
        return holder;
    }

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
    /// A SYSTEM ONE ANSWER in TypeSafe's published shape (<c>docs.typesafe.ai/api</c>, read 2026-10-09):
    /// <c>{model, answers, usage{input_tokens, output_tokens}}</c> — plus, where given, what OpenRouter adds to the same
    /// shape: <c>id</c>, <c>provider</c> and <c>usage.cost</c>. <paramref name="answers"/> is raw JSON written as given, so
    /// a test states the exact numbers the host served, spelled the way it served them.
    /// </summary>
    public static string SystemOne(string model, string answers, long input = 300, long output = 20,
        string? id = null, string? provider = null, decimal? cost = null)
    {
        var body = new JsonObject();
        if (id is not null) body["id"] = id;
        body["model"] = model;
        if (provider is not null) body["provider"] = provider;
        body["answers"] = JsonNode.Parse(answers);
        var usage = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output };
        if (cost is { } billed) usage["cost"] = JsonValue.Create(billed);
        body["usage"] = usage;
        return body.ToJsonString();
    }

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
        // RELEASE A HELD BODY FIRST, so the serving loop ends with the listener rather than after it.
        try { _release.Cancel(); } catch (Exception) { }
        try { _http.Stop(); _http.Close(); } catch (Exception) { }
    }
}
