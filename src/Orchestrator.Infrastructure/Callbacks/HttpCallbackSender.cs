using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Orchestrator.Application.Callbacks;
using Orchestrator.Application.Providers;

namespace Orchestrator.Infrastructure.Callbacks;

/// <summary>
/// Outbound connections that cannot be tricked into reaching blocked networks: the host is resolved once, every
/// resolved address is validated against the <see cref="SsrfGuard"/> and the socket connects to a validated IP
/// (so a DNS answer that changes between validation and connection - DNS rebinding - has no effect).
/// </summary>
public static class GuardedConnect
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(SsrfGuard guard) =>
        async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

            var blocked = addresses.FirstOrDefault(guard.IsBlocked);
            if (blocked is not null)
                throw new SsrfBlockedException($"Destination {host} resolves to a blocked address");

            Exception? last = null;
            foreach (var ip in addresses)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    last = ex;
                }
            }
            throw last ?? new SocketException((int)SocketError.HostUnreachable);
        };

    public static SocketsHttpHandler CreateHandler(SsrfGuard guard, int maxRedirects = 0) => new()
    {
        AllowAutoRedirect = maxRedirects > 0,
        MaxAutomaticRedirections = Math.Max(1, maxRedirects),
        ConnectCallback = Create(guard),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1), // re-resolve DNS periodically
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
}

public sealed class HttpCallbackSender(HttpClient http, HostRateLimiter limiter, IOptions<CallbackOptions> options) : ICallbackSender
{
    public async Task<int> SendAsync(CallbackRequest request, CancellationToken ct)
    {
        var uri = new Uri(request.Url);
        if (!limiter.TryAcquire(uri.Host))
            throw new CallbackDeliveryException($"Rate limit exceeded for destination {uri.Host}", transient: true);

        using var msg = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, "application/json")
        };
        msg.Headers.UserAgent.ParseAdd("SignatureOrchestrator/1.0");
        foreach (var (k, v) in request.Headers) msg.Headers.TryAddWithoutValidation(k, v);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));
        try
        {
            using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)resp.StatusCode;
            if (code is >= 200 and <= 299) return code; // the response body is never read
            var transient = code is 408 or 429 or >= 500;
            throw new CallbackDeliveryException($"Destination responded HTTP {code}", transient, code);
        }
        catch (CallbackDeliveryException) { throw; }
        catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException blocked)
        {
            throw new CallbackDeliveryException(blocked.Message, transient: false);
        }
        catch (HttpRequestException ex)
        {
            throw new CallbackDeliveryException($"Destination unreachable: {ex.Message}", transient: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CallbackDeliveryException("Destination timed out", transient: true);
        }
    }
}
