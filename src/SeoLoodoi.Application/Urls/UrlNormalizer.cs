using System.Globalization;
using System.Web;

namespace SeoLoodoi.Application.Urls;

public sealed record UrlNormalizationOptions(bool RemoveTrackingParameters = true, bool RemoveTrailingSlash = true);

public interface IUrlNormalizer { Uri Normalize(Uri input, UrlNormalizationOptions? options = null); }

public sealed class UrlNormalizer : IUrlNormalizer
{
    private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase) { "gclid", "fbclid", "msclkid", "dclid" };
    public Uri Normalize(Uri input, UrlNormalizationOptions? options = null)
    {
        options ??= new();
        if (!input.IsAbsoluteUri || input.Scheme is not ("http" or "https")) throw new ArgumentException("Absolute HTTP(S) URL required.", nameof(input));
        var builder = new UriBuilder(input) { Fragment = "", Host = new IdnMapping().GetAscii(input.IdnHost).ToLowerInvariant() };
        if ((builder.Scheme == "http" && builder.Port == 80) || (builder.Scheme == "https" && builder.Port == 443)) builder.Port = -1;
        var query = HttpUtility.ParseQueryString(builder.Query);
        var pairs = query.AllKeys.Where(k => k is not null)
            .Where(k => !options.RemoveTrackingParameters || !(k!.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || Tracking.Contains(k)))
            .SelectMany(k => query.GetValues(k!)!.Distinct().Select(v => (Key: k!, Value: v)))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value ?? "")}");
        builder.Query = string.Join("&", pairs);
        if (options.RemoveTrailingSlash && builder.Path.Length > 1) builder.Path = builder.Path.TrimEnd('/');
        return builder.Uri;
    }
}
