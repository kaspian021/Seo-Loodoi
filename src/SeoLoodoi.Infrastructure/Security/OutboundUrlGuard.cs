using System.Net;
using System.Net.Sockets;

namespace SeoLoodoi.Infrastructure.Security;

public interface IOutboundUrlGuard { Task ValidateAsync(Uri uri, CancellationToken ct); }

public sealed class OutboundUrlGuard : IOutboundUrlGuard
{
    public async Task ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Only HTTP(S) destinations are allowed.");
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Local destinations are blocked.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        if (addresses.Length == 0 || addresses.Any(IsForbidden)) throw new InvalidOperationException("Destination resolves to a protected network.");
    }

    internal static bool IsForbidden(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224 ||
               (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127) ||
               (b[0] == 198 && b[1] is 18 or 19);
    }
}
