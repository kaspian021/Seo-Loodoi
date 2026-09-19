using System.Text;
using Microsoft.Extensions.Caching.Memory;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class RobotsService(IPageFetcher fetcher, IRobotsParser parser, IMemoryCache cache) : IRobotsService
{
    private static readonly RobotsDocument AllowAll = new([], []);
    public async Task<RobotsPolicy> GetPolicyAsync(Uri siteUri, CancellationToken ct)
    {
        var origin = new Uri(siteUri.GetLeftPart(UriPartial.Authority));
        var robotsUri = new Uri(origin, "/robots.txt");
        var key = $"robots:{robotsUri.AbsoluteUri}";
        if (cache.TryGetValue<RobotsPolicy>(key, out var cached)) return cached!;
        RobotsPolicy policy;
        try
        {
            var response = await fetcher.FetchAsync(robotsUri, 512_000, ct);
            policy = response.StatusCode switch
            {
                >= 200 and < 300 => Build(response, origin),
                401 or 403 => new(parser.Parse("User-agent: *\nDisallow: /", origin), response.StatusCode, DateTimeOffset.UtcNow, false),
                429 or >= 500 => new(AllowAll, response.StatusCode, DateTimeOffset.UtcNow, true),
                _ => new(AllowAll, response.StatusCode, DateTimeOffset.UtcNow, false)
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            policy = new(AllowAll, null, DateTimeOffset.UtcNow, true);
        }
        catch (HttpRequestException)
        {
            policy = new(AllowAll, null, DateTimeOffset.UtcNow, true);
        }
        cache.Set(key, policy, policy.TemporarilyUnavailable ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(6));
        return policy;
    }

    /// <summary>
    /// Keeps the raw robots.txt text on the policy. AEO analysis needs it: the
    /// parsed document answers "may this crawler fetch", but only the raw text can
    /// be re-evaluated later against crawler definitions that change over time.
    /// </summary>
    private RobotsPolicy Build(FetchResult response, Uri origin)
    {
        var raw = Encoding.UTF8.GetString(response.Content);
        return new RobotsPolicy(parser.Parse(raw, origin), response.StatusCode, DateTimeOffset.UtcNow, false, raw);
    }
}
