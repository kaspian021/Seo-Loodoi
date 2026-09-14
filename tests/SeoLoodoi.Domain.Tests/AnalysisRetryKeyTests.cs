using SeoLoodoi.Infrastructure.Analysis;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

public sealed class AnalysisRetryKeyTests
{
    [Fact]
    public void ConsecutiveRetryKeys_AreUnique()
    {
        var crawlId = Guid.NewGuid();

        var keys = Enumerable.Range(0, 1000).Select(_ => AnalysisStatusService.RetryKey(crawlId)).ToArray();

        keys.Distinct().Should().HaveCount(1000);
    }

    [Fact]
    public void RetryKey_KeepsTheAnalysisPrefixUsedByJobLookups()
    {
        var crawlId = Guid.NewGuid();

        AnalysisStatusService.RetryKey(crawlId).Should().StartWith($"analyze-crawl:{crawlId}:retry:");
    }
}
