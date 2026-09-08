using System.Diagnostics;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class SafePageFetcher(HttpClient client, IOutboundUrlGuard guard, IHostRequestCoordinator coordinator) : IPageFetcher, IConfigurablePageFetcher
{
    private const int MaxRedirects = 10;

    public Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, CancellationToken ct) =>
        FetchAsync(uri, maxResponseBytes, "SEO-LoodoiBot/1.0 (+https://loodoi.example/bot)", true, 30, ct);

    public async Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, string userAgent, bool followRedirects, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 120)));
        var requestCt = timeout.Token;
        try
        {
            var requested = uri;
            var current = uri;
            var redirects = new List<Uri>();
            var timer = Stopwatch.StartNew();
            for (var hop = 0; hop <= (followRedirects ? MaxRedirects : 0); hop++)
            {
                await guard.ValidateAsync(current, requestCt);
                await using var hostLease = await coordinator.AcquireAsync(current, requestCt);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "SEO-LoodoiBot/1.0" : userAgent);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt);
                if (followRedirects && (int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
                {
                    if (hop == MaxRedirects) throw new HttpRequestException("Maximum redirect count exceeded.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (current.Scheme is not ("http" or "https")) throw new HttpRequestException("Redirected to a blocked scheme.");
                    redirects.Add(current);
                    continue;
                }
                var declared = response.Content.Headers.ContentLength;
                if (declared > maxResponseBytes) throw new HttpRequestException("Response exceeds configured size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(requestCt);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, requestCt);
                    if (read == 0) break;
                    if (output.Length + read > maxResponseBytes) throw new HttpRequestException("Response exceeds configured size limit.");
                    output.Write(buffer, 0, read);
                }
                timer.Stop();
                var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
                return new(requested, current, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, headers, output.ToArray(), timer.Elapsed, redirects);
            }
            throw new UnreachableException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("Page request timed out.");
        }
    }
}
