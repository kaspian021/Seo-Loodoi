using System.Net;
using System.Text.Json;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class Phase11AeoTests
{
    private static readonly Uri Origin = new("https://example.com/");

    private readonly IRobotsParser parser = new RobotsParser();
    private readonly Guid projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid crawlId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private AeoAnalyzer Analyzer() => new(parser);

    private static AiCrawlerProfileDto Profile(string key, string token, AiCrawlerPurpose purpose = AiCrawlerPurpose.AnswerEngine, decimal weight = 1m) =>
        new(Guid.NewGuid(), key, key, token, purpose, weight, true, null);

    private static AeoPageInput Page(
        string? text = "محتوا",
        string? schema = null,
        string? headings = null,
        string? canonical = null,
        int wordCount = 300) =>
        new("https://example.com/a", text, schema, headings, canonical, wordCount);

    // ------------------------------------------------------------- profiles

    [Fact]
    public void Profile_ClampsWeightIntoUnitRange()
    {
        var high = new AiCrawlerProfile("high", "High", "Bot", AiCrawlerPurpose.AiSearch, 5m);
        var low = new AiCrawlerProfile("low", "Low", "Bot", AiCrawlerPurpose.AiSearch, -5m);
        Assert.Equal(1m, high.Weight);
        Assert.Equal(0m, low.Weight);
    }

    [Fact]
    public void Profile_TrimsAndLowercasesKey()
    {
        var p = new AiCrawlerProfile("  GPTBot ", "OpenAI GPTBot", "GPTBot", AiCrawlerPurpose.Training, 0.5m);
        Assert.Equal("gptbot", p.Key);
    }

    [Fact]
    public void Profile_PurposeDefaultsToAnswerEngine()
    {
        var p = new AiCrawlerProfile("x", "X", "XBot", AiCrawlerPurpose.AnswerEngine, 1m);
        Assert.Equal(AiCrawlerPurpose.AnswerEngine, p.Purpose);
        Assert.True(p.IsEnabled);
    }

    // ---------------------------------------------------------- crawlability

    [Fact]
    public void Analyze_ExplicitDisallow_IsBlocked()
    {
        var robots = """
            User-agent: GPTBot
            Disallow: /

            User-agent: *
            Allow: /
            """;
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [Page()]);

        var gpt = Assert.Single(report.CrawlerAccess);
        Assert.Equal("Blocked", gpt.Access);
        Assert.Equal(0m, report.AiCrawlabilityScore);
        Assert.Contains(report.Findings, f => f.Contains("GPTBot"));
    }

    [Fact]
    public void Analyze_ExplicitAllow_IsAllowed()
    {
        var robots = """
            User-agent: GPTBot
            Allow: /
            """;
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [Page()]);

        Assert.Equal("Allowed", Assert.Single(report.CrawlerAccess).Access);
        Assert.Equal(100m, report.AiCrawlabilityScore);
    }

    [Fact]
    public void Analyze_UnmentionedCrawler_IsUnspecifiedNotAllowed()
    {
        var robots = """
            User-agent: *
            Allow: /
            """;
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [Page()]);

        // Falling back to the wildcard is not an explicit decision by the site,
        // so it must not be reported as "allowed".
        Assert.Equal("Unspecified", Assert.Single(report.CrawlerAccess).Access);
    }

    [Fact]
    public void Analyze_EmptyRobots_IsUnspecified()
    {
        var report = Analyzer().Analyze(projectId, crawlId, string.Empty, Origin, [Profile("gptbot", "GPTBot")], [Page()]);
        Assert.Equal("Unspecified", Assert.Single(report.CrawlerAccess).Access);
    }

    [Fact]
    public void Analyze_WildcardDisallow_BlocksUnmentionedCrawler()
    {
        var robots = """
            User-agent: *
            Disallow: /
            """;
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [Page()]);
        Assert.Equal("Blocked", Assert.Single(report.CrawlerAccess).Access);
    }

    [Fact]
    public void Analyze_SpecificGroupOverridesWildcard()
    {
        var robots = """
            User-agent: *
            Disallow: /

            User-agent: GPTBot
            Allow: /
            """;
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [Page()]);
        Assert.Equal("Allowed", Assert.Single(report.CrawlerAccess).Access);
    }

    // -------------------------------------------------- unknown stays unknown

    [Fact]
    public void Analyze_MissingRobots_LeavesCrawlabilityScoreNull()
    {
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [Profile("gptbot", "GPTBot")], [Page()]);

        // A missing robots.txt is unknown, not a zero score.
        Assert.Null(report.AiCrawlabilityScore);
        Assert.Null(report.AiVisibilityScore);
        Assert.False(report.RobotsAvailable);
        Assert.Contains(report.Findings, f => f.Contains("نامشخص"));
    }

    [Fact]
    public void Analyze_NoPages_LeavesAllContentScoresNull()
    {
        var report = Analyzer().Analyze(projectId, crawlId, "User-agent: *\nAllow: /", Origin, [Profile("gptbot", "GPTBot")], [Page(text: null)]);

        Assert.Equal(0, report.Signals.PagesAnalyzed);
        Assert.Null(report.AnswerReadinessScore);
        Assert.Null(report.CitationReadinessScore);
        Assert.Null(report.AiVisibilityScore);
        Assert.NotNull(report.AiCrawlabilityScore);
    }

    [Fact]
    public void Analyze_DisabledProfilesAreIgnored()
    {
        var profile = Profile("gptbot", "GPTBot") with { IsEnabled = false };
        var report = Analyzer().Analyze(projectId, crawlId, "User-agent: GPTBot\nDisallow: /", Origin, [profile], [Page()]);
        Assert.Empty(report.CrawlerAccess);
    }

    // --------------------------------------------------------------- signals

    [Fact]
    public void Analyze_DetectsQuestionHeadings()
    {
        var headings = JsonSerializer.Serialize(new[] { new { level = 2, text = "سئو چیست؟" } });
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(headings: headings)]);
        Assert.Equal(1, report.Signals.PagesWithQuestionHeadings);
    }

    [Fact]
    public void Analyze_DetectsEnglishQuestionHeadings()
    {
        var headings = JsonSerializer.Serialize(new[] { new { level = 2, text = "How does crawling work" } });
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(headings: headings)]);
        Assert.Equal(1, report.Signals.PagesWithQuestionHeadings);
    }

    [Fact]
    public void Analyze_DetectsFaqAndEntitySchema()
    {
        // A raw literal, not an anonymous object: serializing new { @type = ... }
        // emits "type" without the JSON-LD '@' prefix.
        var schema = """{"@type":"FAQPage","mainEntity":[]}""";
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(schema: schema)]);
        Assert.Equal(1, report.Signals.PagesWithFaqSchema);
        Assert.Equal(1, report.Signals.PagesWithAnySchema);
        Assert.Equal(0, report.Signals.PagesWithEntitySchema);
    }

    [Fact]
    public void Analyze_HandlesGraphAndArraySchema()
    {
        var schema = """{"@graph":[{"@type":["Organization","LocalBusiness"],"name":"Acme"}]}""";
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(schema: schema)]);
        Assert.Equal(1, report.Signals.PagesWithEntitySchema);
    }

    [Fact]
    public void Analyze_InvalidSchemaJsonIsIgnored()
    {
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(schema: "not json")]);
        Assert.Equal(0, report.Signals.PagesWithAnySchema);
    }

    [Fact]
    public void Analyze_DetectsAuthorAndCanonical()
    {
        var schema = """{"@type":"Article","author":{"@type":"Person","name":"A"},"datePublished":"2026-01-01"}""";
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(schema: schema, canonical: "https://example.com/a")]);
        Assert.Equal(1, report.Signals.PagesWithAuthorOrDate);
        Assert.Equal(1, report.Signals.PagesWithCanonical);
    }

    [Fact]
    public void Analyze_ConciseAnswerUsesWordCountWindow()
    {
        var report = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(wordCount: 400), Page(wordCount: 5000)]);
        Assert.Equal(2, report.Signals.PagesAnalyzed);
        Assert.Equal(1, report.Signals.PagesWithConciseAnswer);
    }

    // -------------------------------------------------------- combined score

    [Fact]
    public void Analyze_VisibilityScoreIsWeightedAndBounded()
    {
        var headings = JsonSerializer.Serialize(new[]
        {
            new { level = 2, text = "چرا سئو مهم است؟" },
            new { level = 2, text = "خلاصه" },
            new { level = 3, text = "جزئیات" }
        });
        var schema = """{"@type":"FAQPage"}""";
        var robots = "User-agent: GPTBot\nAllow: /";

        var page = Page(schema: schema, headings: headings, canonical: "https://example.com/a", wordCount: 300);
        var report = Analyzer().Analyze(projectId, crawlId, robots, Origin, [Profile("gptbot", "GPTBot")], [page]);

        Assert.NotNull(report.AiVisibilityScore);
        Assert.InRange(report.AiVisibilityScore!.Value, 0m, 100m);
        Assert.Equal(100m, report.AiCrawlabilityScore);
        Assert.True(report.AnswerReadinessScore > 0);
        Assert.True(report.CitationReadinessScore > 0);
    }

    [Fact]
    public void Analyze_HigherWeightCrawlerDominatesScore()
    {
        var robots = """
            User-agent: GPTBot
            Allow: /

            User-agent: Bytespider
            Disallow: /
            """;
        var lowWeightBlocked = new[]
        {
            Profile("gptbot", "GPTBot", weight: 1m),
            Profile("bytespider", "Bytespider", AiCrawlerPurpose.Training, 0.1m)
        };
        var highWeightBlocked = new[]
        {
            Profile("gptbot", "GPTBot", weight: 0.1m),
            Profile("bytespider", "Bytespider", AiCrawlerPurpose.Training, 1m)
        };

        var good = Analyzer().Analyze(projectId, crawlId, robots, Origin, lowWeightBlocked, [Page()]);
        var bad = Analyzer().Analyze(projectId, crawlId, robots, Origin, highWeightBlocked, [Page()]);

        Assert.True(good.AiCrawlabilityScore > bad.AiCrawlabilityScore);
    }

    [Fact]
    public void Analyze_ZeroWeightProfilesLeaveCrawlabilityUnknown()
    {
        var report = Analyzer().Analyze(projectId, crawlId, "User-agent: GPTBot\nAllow: /", Origin, [Profile("gptbot", "GPTBot", weight: 0m)], [Page()]);
        Assert.Null(report.AiCrawlabilityScore);
    }

    // ------------------------------------------------------------- snapshot

    [Fact]
    public void Snapshot_ApplyPersistsScoresAndCounters()
    {
        var snapshot = new AiVisibilitySnapshot(projectId, crawlId);
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        snapshot.Apply(80m, 60m, 40m, 68m, 3, 1, 2, 12, "{}", now);

        Assert.Equal(80m, snapshot.AiCrawlabilityScore);
        Assert.Equal(60m, snapshot.AnswerReadinessScore);
        Assert.Equal(40m, snapshot.CitationReadinessScore);
        Assert.Equal(68m, snapshot.AiVisibilityScore);
        Assert.Equal(3, snapshot.CrawlersAllowed);
        Assert.Equal(1, snapshot.CrawlersBlocked);
        Assert.Equal(2, snapshot.CrawlersUnspecified);
        Assert.Equal(12, snapshot.PagesAnalyzed);
        Assert.Equal(now, snapshot.ComputedAt);
    }

    [Fact]
    public void Snapshot_ApplyIsIdempotentForTheSameCrawl()
    {
        var snapshot = new AiVisibilitySnapshot(projectId, crawlId);
        var first = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(1);

        snapshot.Apply(80m, 60m, 40m, 68m, 3, 1, 2, 12, "{}", first);
        snapshot.Apply(10m, 10m, 10m, 10m, 0, 0, 0, 1, "{\"a\":1}", second);

        Assert.Equal(10m, snapshot.AiCrawlabilityScore);
        Assert.Equal(second, snapshot.ComputedAt);
        Assert.Equal("{\"a\":1}", snapshot.EvidenceJson);
    }

    [Fact]
    public void Snapshot_NullScoresArePreservedAsNull()
    {
        var snapshot = new AiVisibilitySnapshot(projectId, crawlId);
        snapshot.Apply(null, null, null, null, 0, 0, 4, 0, "{}", DateTimeOffset.UtcNow);

        Assert.Null(snapshot.AiCrawlabilityScore);
        Assert.Null(snapshot.AiVisibilityScore);
        Assert.Equal(4, snapshot.CrawlersUnspecified);
    }

    // ------------------------------------------------------------- report

    [Fact]
    public void Report_EvidenceRoundTripsThroughJson()
    {
        var report = Analyzer().Analyze(projectId, crawlId, "User-agent: GPTBot\nAllow: /", Origin, [Profile("gptbot", "GPTBot")], [Page()]);
        var json = JsonSerializer.Serialize(report);
        var back = JsonSerializer.Deserialize<AiVisibilityReportDto>(json)!;

        Assert.Equal(report.AiCrawlabilityScore, back.AiCrawlabilityScore);
        Assert.Equal(report.CrawlerAccess.Count, back.CrawlerAccess.Count);
        Assert.Equal(projectId, back.ProjectId);
        Assert.Equal(crawlId, back.CrawlId);
    }

    [Fact]
    public void RobotsParser_ExposesGroupsUsedByAeo()
    {
        var document = parser.Parse("User-agent: GPTBot\nDisallow: /private", Origin);
        var group = Assert.Single(document.Groups);
        Assert.Contains("GPTBot", group.UserAgents);
        Assert.Equal("/private", Assert.Single(group.Rules).Pattern);
    }

    [Fact]
    public void RobotsPolicy_KeepsRawTextForReEvaluation()
    {
        var raw = "User-agent: *\nAllow: /";
        var policy = new RobotsPolicy(parser.Parse(raw, Origin), (int)HttpStatusCode.OK, DateTimeOffset.UtcNow, false, raw);
        Assert.Equal(raw, policy.RawText);
    }
    [Theory]
    [InlineData("[\"not an object\"]")]
    [InlineData("[null,42]")]
    [InlineData("[{\"text\":42,\"level\":\"one\"}]")]
    public void Analyze_InvalidHeadingShapes_DoNotCrashOrInventQuestionHeadings(string headings)
    {
        var result = Analyzer().Analyze(projectId, crawlId, null, Origin, [], [Page(headings: headings)]);
        Assert.Equal(0, result.Signals.PagesWithQuestionHeadings);
        Assert.Equal(0, result.Signals.PagesWithHeadingStructure);
    }
}
