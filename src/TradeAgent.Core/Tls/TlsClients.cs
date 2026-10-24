using System.Net.Http;
using System.Net.Security;

namespace TradeAgent.Core.Tls;

/// <summary>
/// U-tls-denylist — THE ONE PLACE A NETWORK CLIENT IS BUILT. Every handler carries <see cref="TlsPolicy"/>'s callback over the shipped
/// <see cref="TlsDenylist"/>; the guard (<c>TlsClientGuardTests</c>) holds that nothing in <c>src/</c> builds an <see cref="HttpClient"/>,
/// a <see cref="SocketsHttpHandler"/>, an <c>HttpClientHandler</c> or an <see cref="SslStream"/> anywhere else, and that no other
/// certificate validator exists.
///
/// <para>The list is read once, at the first secure connection, never at construction: a client held in a static field is built at class
/// load, and a missing list must refuse connections in words, not fail a type initializer. A list that cannot be read stays refused for
/// the life of the process — the repair is a reinstall, which restarts it.</para>
/// </summary>
public static class TlsClients
{
    static readonly Lazy<TlsDenylist> Shipped = new(() => TlsDenylist.Load(TlsDenylist.ShippedPath), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The policy over the shipped list: the callback every handler built here carries.</summary>
    public static RemoteCertificateValidationCallback ShippedCallback { get; } = TlsPolicy.Callback(() => Shipped.Value);

    /// <summary>
    /// A handler with the app's TLS policy installed. <paramref name="configure"/> sets everything else (redirects, decompression); a
    /// configuration that installs a validator of its own is refused — there is one validation.
    /// </summary>
    public static SocketsHttpHandler Handler(Action<SocketsHttpHandler>? configure = null)
    {
        var handler = new SocketsHttpHandler();
        configure?.Invoke(handler);
        if (handler.SslOptions.RemoteCertificateValidationCallback is not null)
        {
            handler.Dispose();
            throw new InvalidOperationException("A secure connection has one validation, the app's TLS policy; a handler may not install its own.");
        }
        handler.SslOptions.RemoteCertificateValidationCallback = ShippedCallback;
        return handler;
    }

    /// <summary>A client over <see cref="Handler"/>, which it disposes.</summary>
    public static HttpClient Client(Action<SocketsHttpHandler>? configure = null, TimeSpan? timeout = null) =>
        Client(Handler(configure), timeout);

    /// <summary>
    /// A client over a handler chain the caller composed — a <see cref="DelegatingHandler"/> over <see cref="Handler"/>, or a test's
    /// loopback transport. The handler is the caller's to have built here; this client disposes it.
    /// </summary>
    public static HttpClient Client(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var client = new HttpClient(handler, disposeHandler: true);
        if (timeout is { } t) client.Timeout = t;
        return client;
    }
}
