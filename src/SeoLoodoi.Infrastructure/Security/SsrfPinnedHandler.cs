using System.Net;
using System.Net.Sockets;

namespace SeoLoodoi.Infrastructure.Security;

/// <summary>
/// HTTP handler whose connect callback resolves the destination itself, applies
/// the SSRF policy to the resolved addresses, and then connects to the very
/// same address object. This closes the DNS-rebinding window left by validating
/// a name and letting HttpClient resolve it again at connect time.
/// </summary>
public static class SsrfPinnedHandler
{
    public static SocketsHttpHandler Create(Action<SocketsHttpHandler>? configure = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectThroughGuardAsync,
        };
        configure?.Invoke(handler);
        return handler;
    }

    private static async ValueTask<Stream> ConnectThroughGuardAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("Destination host could not be resolved.", ex);
        }
        var address = OutboundUrlGuard.SelectSafeAddress(addresses);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }
}
