namespace SeoLoodoi.Application.Crawling;

public sealed record FetchResult(Uri RequestedUri, Uri FinalUri, int StatusCode, string? ContentType, IReadOnlyDictionary<string, string[]> Headers, byte[] Content, TimeSpan Duration, IReadOnlyList<Uri> RedirectChain);
public interface IPageFetcher { Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, CancellationToken ct); }
public interface IConfigurablePageFetcher
{
    Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, string userAgent, bool followRedirects, int timeoutSeconds, CancellationToken ct);
}

public sealed record ExtractedLink(Uri Target, string AnchorText, string? Rel, bool IsInternal);
public sealed record ExtractedHeading(int Level, string Text);
public sealed record ExtractedHreflang(string Language, Uri Target);
public sealed record ExtractedPage(string? Title, string? MetaDescription, IReadOnlyList<ExtractedHeading> Headings, string? Canonical, string? Robots, string? Language, string Text, int WordCount, int ImageCount, int MissingAltCount, IReadOnlyList<ExtractedLink> Links, IReadOnlyList<string> JsonLd, IReadOnlyDictionary<string,string> OpenGraph, IReadOnlyDictionary<string,string> TwitterCards, IReadOnlyList<ExtractedHreflang>? Hreflang = null);
public interface IHtmlExtractor { Task<ExtractedPage> ExtractAsync(string html, Uri pageUri, CancellationToken ct); }
