using System.Net;
using System.Net.Sockets;

namespace TradeAgent.Tests;

/// <summary>
/// THE ONE PLACE A TEST GETS A LOOPBACK HTTP SERVER (<c>U-loopback-listener-mac</c>). Every listener
/// fixture in the suite is built from <see cref="Start"/>, and
/// <c>SuiteReachesNoVendorTests.No_test_builds_its_own_loopback_listener</c> is the scan that keeps
/// that true of the SUITE rather than of the fixtures that happen to exist today.
///
/// <para><b>Why a fixture cannot do this for itself.</b> Each one used to pick a random port out of a
/// band of two thousand and retry <c>Start()</c> up to twenty times on a collision — on the SAME
/// <see cref="HttpListener"/> instance. Measured on .NET 10.0.11 / macOS 26.5.1: a <c>Start()</c>
/// that fails to bind leaves the instance CLOSED, not merely stopped. <c>IsListening</c> is false and
/// the very next touch of <c>Prefixes</c> throws <c>ObjectDisposedException: Cannot access a disposed
/// object. Object name: 'System.Net.HttpListener'.</c>, so the retry threw out of the constructor at
/// <c>Prefixes.Clear()</c> without ever reaching <c>Start()</c> again: the twenty attempts were one
/// attempt, and the first collision was fatal. Both wordings of the bind failure end there — the
/// in-process one, <c>Failed to listen on prefix 'http://127.0.0.1:18777/' because it conflicts with
/// an existing registration on the machine</c>, and the cross-process one, <c>Address already in
/// use</c>. That is CI run 34967255830 (macos-latest, a docs-only sha, the test dead in 8 ms having
/// served no request) and the <c>UpdateTrustTests</c> sighting at <c>BUILD-STATUS.md:3738</c>.
/// Only the owner of the instance can retry, which is why this hands back a started listener rather
/// than a number.</para>
///
/// <para><b>What it does instead.</b> No random band. The OS names a port that nothing on the machine
/// is on — no other class in this process and no other process, which matters because two suites
/// share the dev Mac — by binding a <see cref="TcpListener"/> at port 0 and handing the number
/// straight over, <see cref="HttpListener"/> not taking port 0 itself. Borrow and bind are one
/// critical section, so two fixtures here cannot be inside that window at once; an outside process
/// that takes the port inside it is what the retry is for, and the retry starts from a fresh
/// listener.</para>
/// </summary>
static class Loopback
{
    /// <summary>Holds the borrow-then-bind window against the other fixtures in this process.</summary>
    static readonly object Gate = new();

    /// <summary>
    /// A started <see cref="HttpListener"/> on <c>http://127.0.0.1:{port}/</c>, and the port it was
    /// given. The caller owns it and disposes it.
    /// </summary>
    public static HttpListener Start(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            var http = new HttpListener();
            try
            {
                lock (Gate)
                {
                    var probe = new TcpListener(IPAddress.Loopback, 0);
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    probe.Stop();

                    http.Prefixes.Add($"http://127.0.0.1:{port}/");
                    http.Start();
                }

                return http;
            }
            catch (Exception e) when (e is HttpListenerException or SocketException && attempt < 20)
            {
                // The failed Start() has already closed this one. Close() on a closed listener is a
                // no-op, and the next attempt builds another.
                try { http.Close(); } catch (Exception) { }
            }
        }
    }
}
