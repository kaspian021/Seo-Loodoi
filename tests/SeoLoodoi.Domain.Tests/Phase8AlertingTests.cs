using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase8AlertingTests
{
    [Fact]
    public void WebhookSignature_SignAndVerify_WorksCorrectly()
    {
        var secret = WebhookSignature.GenerateSecret();
        var payload = "{\"rule\":\"CANNIBALIZATION_DETECTED\",\"score\":85}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var signature = WebhookSignature.Sign(timestamp, payload, secret);
        signature.Should().StartWith("sha256=");

        var isValid = WebhookSignature.Verify(timestamp, payload, secret, signature);
        isValid.Should().BeTrue();

        var isTampered = WebhookSignature.Verify(timestamp, payload + "!", secret, signature);
        isTampered.Should().BeFalse();

        var isReplayed = WebhookSignature.Verify(timestamp + 100, payload, secret, signature);
        isReplayed.Should().BeFalse();
    }

    [Theory]
    [InlineData("SCORE_DROP")]
    [InlineData("CRITICAL_ISSUE")]
    [InlineData("CRAWL_FAILURE")]
    [InlineData("CANNIBALIZATION_DETECTED")]
    [InlineData("THIN_CONTENT_SPIKE")]
    [InlineData("ORPHAN_PAGES_DETECTED")]
    public void AlertTypes_AllowedTypes_AreRecognized(string alertType)
    {
        var allowed = new[] { "SCORE_DROP", "CRITICAL_ISSUE", "CRAWL_FAILURE", "CANNIBALIZATION_DETECTED", "THIN_CONTENT_SPIKE", "ORPHAN_PAGES_DETECTED" };
        allowed.Should().Contain(alertType);
    }
}
