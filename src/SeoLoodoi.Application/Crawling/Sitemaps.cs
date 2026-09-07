using System.Xml;
using System.Xml.Linq;

namespace SeoLoodoi.Application.Crawling;

public enum SitemapKind { UrlSet, Index }
public sealed record SitemapEntry(Uri Location, DateTimeOffset? LastModified);
public sealed record ParsedSitemap(SitemapKind Kind, IReadOnlyList<SitemapEntry> Entries);
public interface ISitemapParser { ParsedSitemap Parse(Stream xml, Uri source); }

public sealed class SitemapParser : ISitemapParser
{
    private static readonly XmlReaderSettings SafeSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50_000_000, IgnoreComments = true, IgnoreWhitespace = true };
    public ParsedSitemap Parse(Stream xml, Uri source)
    {
        using var reader = XmlReader.Create(xml, SafeSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException($"Invalid sitemap: {source}");
        var kind = root.Name.LocalName switch { "urlset" => SitemapKind.UrlSet, "sitemapindex" => SitemapKind.Index, _ => throw new InvalidDataException("Unsupported sitemap root element.") };
        var itemName = kind == SitemapKind.UrlSet ? "url" : "sitemap";
        var entries = new List<SitemapEntry>();
        foreach (var item in root.Elements().Where(x => x.Name.LocalName == itemName))
        {
            var location = item.Elements().FirstOrDefault(x => x.Name.LocalName == "loc")?.Value.Trim();
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var rawLastModified = item.Elements().FirstOrDefault(x => x.Name.LocalName == "lastmod")?.Value.Trim();
            DateTimeOffset? lastModified = DateTimeOffset.TryParse(rawLastModified, out var parsed) ? parsed : null;
            entries.Add(new(uri, lastModified));
        }
        return new(kind, entries);
    }
}
