using System.Net;
using System.Net.Sockets;

namespace SeoLoodoi.Infrastructure.Security;

public interface IOutboundUrlGuard { Task ValidateAsync(Uri uri, CancellationToken ct); }

public sealed class OutboundUrlGuard : IOutboundUrlGuard
{
    public async Task ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Only HTTP(S) destinations are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidOperationException("URLs containing credentials are blocked.");
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Local destinations are blocked.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
        catch (SocketException ex) { throw new InvalidOperationException("Destination host could not be resolved.", ex); }
        if (addresses.Length == 0 || addresses.Any(IsForbidden)) throw new InvalidOperationException("Destination resolves to a protected network.");
    }

    internal static bool IsForbidden(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (ULA), fe80::/10 (link-local), and mapped IPv4 addresses
            // must never be reachable through the crawler.
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            else
            {
                var v6 = ip.GetAddressBytes();
                return v6.Length == 16 && ((v6[0] & 0xfe) == 0xfc || // fc00::/7 ULA
                    (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8) || // 2001:db8::/32 documentation
                    (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x02) || // 2001:2::/48 benchmarking
                    (v6[0] == 0x01 && v6[1] == 0x00 && v6[2] == 0 && v6[3] == 0 && v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0)); // 100::/64 discard
            }
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224 ||
               (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127) ||
               (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
               (b[0] == 192 && b[1] == 0 && b[2] == 2) ||
               (b[0] == 192 && b[1] == 88 && b[2] == 99) ||
               (b[0] == 198 && (b[1] is 18 or 19)) || (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
               (b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
