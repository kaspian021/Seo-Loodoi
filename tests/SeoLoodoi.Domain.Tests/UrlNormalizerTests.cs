using AwesomeAssertions;
using SeoLoodoi.Application.Urls;

namespace SeoLoodoi.Domain.Tests;

public class UrlNormalizerTests
{
    private readonly UrlNormalizer _sut = new();
    [Theory]
    [InlineData("HTTPS://EXAMPLE.COM:443/path/#section", "https://example.com/path")]
    [InlineData("https://example.com/a/?utm_source=x&b=2&a=1", "https://example.com/a?a=1&b=2")]
    [InlineData("http://example.com:80/", "http://example.com/")]
    public void Normalize_produces_deterministic_url(string input, string expected) => _sut.Normalize(new Uri(input)).AbsoluteUri.Should().Be(expected);
    [Theory]
    [InlineData("https://example.com/search?*", "https://example.com/search")]
    [InlineData("https://example.com/search?", "https://example.com/search")]
    public void Normalize_drops_meaningless_wildcard_query(string input, string expected) => _sut.Normalize(new Uri(input)).AbsoluteUri.Should().Be(expected);
}
