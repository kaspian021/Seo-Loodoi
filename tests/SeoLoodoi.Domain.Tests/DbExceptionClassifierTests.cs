using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public class DbExceptionClassifierTests
{
    [Theory]
    [InlineData("duplicate key value violates unique constraint (23505)")]
    [InlineData("UNIQUE constraint failed: CrawledUrls.CrawlId, CrawledUrls.Url")]
    [InlineData("Duplicate entry 'x' for key 'IX_CrawlAnalyses_CrawlId'")]
    public void Detects_unique_violations_through_inner_messages(string message)
    {
        var ex = new DbUpdateException("Save failed", new InvalidOperationException(message));
        DbExceptionClassifier.IsUniqueViolation(ex).Should().BeTrue();
    }
    [Theory]
    [InlineData("40001 could not serialize access due to concurrent update")]
    [InlineData("53300 too many connections")]
    [InlineData("Connection timeout expired")]
    public void Detects_transient_failures(string message)
    {
        var ex = new DbUpdateException("Save failed", new InvalidOperationException(message));
        DbExceptionClassifier.IsTransient(ex).Should().BeTrue();
        DbExceptionClassifier.IsUniqueViolation(ex).Should().BeFalse();
    }
    [Theory]
    [InlineData("40001 could not serialize access due to read/write dependencies among transactions")]
    [InlineData("could not serialize access due to concurrent update")]
    public void Detects_serialization_failures(string message)
    {
        var ex = new DbUpdateException("Save failed", new InvalidOperationException(message));
        DbExceptionClassifier.IsSerializationFailure(ex).Should().BeTrue();
    }
    [Theory]
    [InlineData("23505 duplicate key value violates unique constraint", false)]
    [InlineData("40P01 deadlock detected", false)]
    [InlineData("could not serialize access due to read/write dependencies among transactions", true)]
    public void Serialization_failure_detection_is_precise(string message, bool expected)
    {
        var ex = new DbUpdateException("Save failed", new InvalidOperationException(message));
        DbExceptionClassifier.IsSerializationFailure(ex).Should().Be(expected);
    }
    [Fact]
    public void Unknown_errors_are_neither_unique_nor_transient()
    {
        var ex = new DbUpdateException("Some other persistence problem");
        DbExceptionClassifier.IsUniqueViolation(ex).Should().BeFalse();
        DbExceptionClassifier.IsTransient(ex).Should().BeFalse();
        DbExceptionClassifier.IsSerializationFailure(ex).Should().BeFalse();
    }
}
