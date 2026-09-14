using SeoLoodoi.Infrastructure.Security;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

public sealed class WebhookSignatureTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Sign_ProducesStablePrefixedHexDigest()
    {
        var signature = WebhookSignature.Sign(1_700_000_000, "{\"a\":1}", Secret);

        signature.Should().StartWith("sha256=").And.MatchRegex("^sha256=[0-9a-f]{64}$");
        WebhookSignature.Sign(1_700_000_000, "{\"a\":1}", Secret).Should().Be(signature);
    }

    [Fact]
    public void Sign_IncludesTheTimestampInTheSignedContent()
    {
        WebhookSignature.Sign(1_700_000_000, "{}", Secret)
            .Should().NotBe(WebhookSignature.Sign(1_700_000_001, "{}", Secret));
    }

    [Fact]
    public void Verify_AcceptsAValidSignature_AndRejectsTampering()
    {
        var payload = "{\"rule\":\"CRAWL_FAILURE\"}";
        var signature = WebhookSignature.Sign(42, payload, Secret);

        WebhookSignature.Verify(42, payload, Secret, signature).Should().BeTrue();
        WebhookSignature.Verify(42, payload + " ", Secret, signature).Should().BeFalse();
        WebhookSignature.Verify(43, payload, Secret, signature).Should().BeFalse();
        WebhookSignature.Verify(42, payload, "f" + Secret[1..], signature).Should().BeFalse();
        WebhookSignature.Verify(42, payload, Secret, null).Should().BeFalse();
        WebhookSignature.Verify(42, payload, Secret, "").Should().BeFalse();
    }

    [Fact]
    public void GenerateSecret_IsA256BitHexValue()
    {
        var first = WebhookSignature.GenerateSecret();
        var second = WebhookSignature.GenerateSecret();

        first.Should().MatchRegex("^[0-9a-f]{64}$");
        second.Should().NotBe(first);
    }
}
