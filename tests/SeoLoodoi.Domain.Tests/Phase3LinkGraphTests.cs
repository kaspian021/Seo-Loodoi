using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase3LinkGraphTests
{
    [Fact]
    public void InternalLinkGraph_Detects_DeadEnd_Pages_And_Top_Anchors()
    {
        var home = new GraphPage(Guid.NewGuid(), "/", true, true);
        var blog = new GraphPage(Guid.NewGuid(), "/blog", false, true);
        var post = new GraphPage(Guid.NewGuid(), "/blog/post-1", false, true);

        // home -> blog -> post. But post has no outgoing links!
        var links = new[]
        {
            new GraphLink(home.Id, blog.Id, "Our Blog"),
            new GraphLink(blog.Id, post.Id, "Read More"),
            new GraphLink(home.Id, post.Id, "اینجا کلیک کنید")
        };

        var graph = new InternalLinkGraph();
        var summary = graph.AnalyzeGraph([home, blog, post], links);

        summary.TotalInternalLinks.Should().Be(3);
        summary.DeadEndPageCount.Should().Be(1);

        var postMetrics = summary.Pages.Single(x => x.PageId == post.Id);
        postMetrics.IsDeadEnd.Should().BeTrue();
        postMetrics.InDegree.Should().Be(2);
        postMetrics.OutDegree.Should().Be(0);

        summary.TopAnchors.Should().HaveCount(3);
        summary.TopAnchors.Should().Contain(x => x.Text == "Read More" && x.IsGeneric);
        summary.TopAnchors.Should().Contain(x => x.Text == "اینجا کلیک کنید" && x.IsGeneric);
        summary.TopAnchors.Should().Contain(x => x.Text == "Our Blog" && !x.IsGeneric);
    }

    [Fact]
    public void DeepClickDepthRule_Triggers_When_Depth_Exceeds_Three()
    {
        var context = new PageAnalysisContext(
            "https://example.com/a/b/c/d/e", "Title", "Meta", ["H1"], "https://example.com/a/b/c/d/e",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", "{}", Depth: 4);

        var rule = new DeepClickDepthRule(maxDepth: 3);
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.Medium);
        result.Category.Should().Be(IssueCategory.Technical);
        result.Evidence?.Actual.Should().Be("4");
    }

    [Fact]
    public void DeepClickDepthRule_DoesNotTrigger_When_Depth_Is_Three_Or_Less()
    {
        var context = new PageAnalysisContext(
            "https://example.com/a/b/c", "Title", "Meta", ["H1"], "https://example.com/a/b/c",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", "{}", Depth: 2);

        var rule = new DeepClickDepthRule(maxDepth: 3);
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeFalse();
    }
}
