using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Infrastructure.AI;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase7AiExpertEngineTests
{
    [Fact]
    public async Task AiSeoExpert_Template_SynthesizesMultiDimensionalEvidence()
    {
        var options = Options.Create(new AiOptions { Enabled = false, PromptVersion = "2.0.0" });
        using var client = new HttpClient();
        var expert = new AiSeoExpert(client, options);

        var packet = new AiEvidencePacket(
            Guid.NewGuid(),
            "https://example.com/guide",
            200,
            "سئو سایت",
            "راهنمای جامع سئو",
            1200,
            [
                new("ORPHAN_PAGE", "High", "InternalLinks", "{}"),
                new("THIN_CONTENT", "Medium", "Content", "{}"),
                new("DUPLICATE_TITLE_TAG", "High", "OnPage", "{}")
            ],
            new AiCrawlOverview(50, 48, 2, 240),
            new AiLinkGraphOverview(3, 2, 180),
            new AiContentOverview(4, 1, 82.5m),
            new AiKeywordOverview(25, 8, 2),
            new AiCompetitorOverview(2, 5));

        var response = await expert.AnalyzeAsync(packet, CancellationToken.None);

        response.Should().NotBeNull();
        response.Provider.Should().Be("deterministic-expert-engine");
        response.PromptVersion.Should().Be("2.0.0");
        response.Confidence.Should().Be(0.95m);

        response.Summary.Should().Contain("بررسی داده‌های خزش");
        response.Observations.Should().Contain(x => x.Contains("50 صفحه بررسی‌شده"));
        response.Observations.Should().Contain(x => x.Contains("صفحه یتیم"));
        response.Observations.Should().Contain(x => x.Contains("محتوای بسیار ضعیف"));
        response.Observations.Should().Contain(x => x.Contains("همنوع‌خواری"));

        response.Recommendations.Should().NotBeEmpty();
        response.Actions.Should().NotBeEmpty();
    }
}
