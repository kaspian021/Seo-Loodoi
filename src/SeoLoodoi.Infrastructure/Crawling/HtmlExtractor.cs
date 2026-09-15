using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class HtmlExtractor : IHtmlExtractor
{
    private readonly HtmlParser _parser = new(new() { IsKeepingSourceReferences = false });
    public async Task<ExtractedPage> ExtractAsync(string html, Uri pageUri, CancellationToken ct)
    {
        var doc = await _parser.ParseDocumentAsync(html, ct);
        var title = Clean(doc.Title);
        var description = Clean(doc.QuerySelector("meta[name='description' i]")?.GetAttribute("content"));
        var headings = doc.QuerySelectorAll("h1,h2,h3,h4,h5,h6")
            .Select(x => new ExtractedHeading(int.Parse(x.LocalName[1..]), Clean(x.TextContent) ?? string.Empty))
            .Where(x => x.Text.Length > 0).ToArray();
        var canonical = doc.QuerySelector("link[rel~='canonical' i]")?.GetAttribute("href");
        if (canonical is not null && Uri.TryCreate(pageUri, canonical, out var canonicalUri)) canonical = canonicalUri.AbsoluteUri;
        var robots = Clean(doc.QuerySelector("meta[name='robots' i]")?.GetAttribute("content"));
        var text = Clean(doc.Body?.TextContent) ?? string.Empty;
        var links = doc.QuerySelectorAll("a[href]").Select(a =>
        {
            var href = a.GetAttribute("href")!;
            if (!Uri.TryCreate(pageUri, href, out var target) || target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo)) return null;
            return new ExtractedLink(target, Clean(a.TextContent) ?? string.Empty, a.GetAttribute("rel"), string.Equals(target.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase));
        }).Where(x => x is not null).Cast<ExtractedLink>().ToArray();
        var images = doc.Images;
        var jsonLd = doc.QuerySelectorAll("script[type='application/ld+json' i]").Select(x => x.TextContent.Trim()).Where(x => x.Length > 0).ToArray();
        var hreflang = doc.QuerySelectorAll("link[rel~='alternate' i][hreflang][href]").Select(link =>
        {
            var language = link.GetAttribute("hreflang")?.Trim();
            var href = link.GetAttribute("href");
            return language is not null && Uri.TryCreate(pageUri, href, out var target) && target is not null && (target.Scheme is "http" or "https") && string.IsNullOrEmpty(target.UserInfo) ? new ExtractedHreflang(language, target) : null;
        }).Where(x => x is not null).Cast<ExtractedHreflang>().DistinctBy(x => (x.Language, x.Target.AbsoluteUri)).ToArray();

        var assets = new List<ExtractedAsset>();
        foreach (var img in doc.Images)
        {
            var src = img.GetAttribute("src")?.Trim();
            if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(pageUri, src, out var assetUri) || assetUri.Scheme is not ("http" or "https")) continue;
            var isExt = !string.Equals(assetUri.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase);
            var isMixed = pageUri.Scheme == "https" && assetUri.Scheme == "http";
            var alt = Clean(img.GetAttribute("alt"));
            var loading = img.GetAttribute("loading");
            var width = img.GetAttribute("width");
            var height = img.GetAttribute("height");
            var extra = (loading != null || width != null || height != null)
                ? System.Text.Json.JsonSerializer.Serialize(new { loading, width, height })
                : null;
            assets.Add(new ExtractedAsset(AssetType.Image, assetUri.AbsoluteUri, alt, "image/*", isExt, isMixed, extra));
        }
        foreach (var link in doc.QuerySelectorAll("link[rel~='stylesheet' i][href]"))
        {
            var href = link.GetAttribute("href")?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(pageUri, href, out var assetUri) || assetUri.Scheme is not ("http" or "https")) continue;
            var isExt = !string.Equals(assetUri.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase);
            var isMixed = pageUri.Scheme == "https" && assetUri.Scheme == "http";
            var media = link.GetAttribute("media");
            var extra = media != null ? System.Text.Json.JsonSerializer.Serialize(new { media }) : null;
            assets.Add(new ExtractedAsset(AssetType.Stylesheet, assetUri.AbsoluteUri, null, "text/css", isExt, isMixed, extra));
        }
        foreach (var script in doc.QuerySelectorAll("script[src]"))
        {
            var src = script.GetAttribute("src")?.Trim();
            if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(pageUri, src, out var assetUri) || assetUri.Scheme is not ("http" or "https")) continue;
            var isExt = !string.Equals(assetUri.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase);
            var isMixed = pageUri.Scheme == "https" && assetUri.Scheme == "http";
            var isAsync = script.HasAttribute("async");
            var isDefer = script.HasAttribute("defer");
            var type = script.GetAttribute("type");
            var extra = System.Text.Json.JsonSerializer.Serialize(new { isAsync, isDefer, type });
            assets.Add(new ExtractedAsset(AssetType.Script, assetUri.AbsoluteUri, null, "application/javascript", isExt, isMixed, extra));
        }
        foreach (var font in doc.QuerySelectorAll("link[rel~='preload' i][as='font' i][href]"))
        {
            var href = font.GetAttribute("href")?.Trim();
            if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(pageUri, href, out var assetUri) || assetUri.Scheme is not ("http" or "https")) continue;
            var isExt = !string.Equals(assetUri.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase);
            var isMixed = pageUri.Scheme == "https" && assetUri.Scheme == "http";
            assets.Add(new ExtractedAsset(AssetType.Font, assetUri.AbsoluteUri, null, "font/*", isExt, isMixed, null));
        }
        foreach (var iframe in doc.QuerySelectorAll("iframe[src]"))
        {
            var src = iframe.GetAttribute("src")?.Trim();
            if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(pageUri, src, out var assetUri) || assetUri.Scheme is not ("http" or "https")) continue;
            var isExt = !string.Equals(assetUri.IdnHost, pageUri.IdnHost, StringComparison.OrdinalIgnoreCase);
            var isMixed = pageUri.Scheme == "https" && assetUri.Scheme == "http";
            var titleAttr = iframe.GetAttribute("title");
            var extra = titleAttr != null ? System.Text.Json.JsonSerializer.Serialize(new { title = titleAttr }) : null;
            assets.Add(new ExtractedAsset(AssetType.Iframe, assetUri.AbsoluteUri, titleAttr, "text/html", isExt, isMixed, extra));
        }

        return new(title, description, headings, canonical, robots, doc.DocumentElement?.GetAttribute("lang"), text,
            text.Length == 0 ? 0 : Regex.Matches(text, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant).Count,
            images.Length, images.Count(x => string.IsNullOrWhiteSpace(x.GetAttribute("alt"))), links, jsonLd,
            Meta(doc, "property", "og:"), Meta(doc, "name", "twitter:"), hreflang, assets);
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, @"\s+", " ").Trim();
    private static IReadOnlyDictionary<string,string> Meta(AngleSharp.Dom.IDocument doc, string attribute, string prefix) => doc.QuerySelectorAll($"meta[{attribute}^='{prefix}' i]").Select(x => (Key:x.GetAttribute(attribute), Value:x.GetAttribute("content"))).Where(x => x.Key is not null && x.Value is not null).GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Value!, StringComparer.OrdinalIgnoreCase);
}
