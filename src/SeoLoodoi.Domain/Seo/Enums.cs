namespace SeoLoodoi.Domain.Seo;

public enum ProjectStatus { Active, Paused, Archived }
public enum CrawlStatus { Queued, Running, Paused, Completed, Failed, Cancelled }
public enum CrawlTrigger { Manual, Scheduled, Initial, Retry }
public enum IssueSeverity { Notice, Low, Medium, High, Critical }
public enum IssueCategory { Technical, Indexability, OnPage, Content, InternalLinks, StructuredData, Performance, International, Security }
public enum IssueStatus { Open, Ignored, Resolved }
public enum RecommendationStatus { Proposed, Accepted, InProgress, Completed, Dismissed }
public enum ExternalProvider { GoogleSearchConsole, GoogleAnalytics, BingWebmaster, PageSpeed, OpenAiCompatible, LocalAi }
public enum FrontierStatus { Pending, Leased, Completed, Failed, Skipped }
public enum SeoJobStatus { Queued, Running, Succeeded, Failed, Cancelled }
public enum SeoJobType { InitialCrawl, ContinueCrawl, AnalyzeCrawl, CalculateScores, GenerateRecommendations, GenerateReport, MonitoringCheck, Cleanup, CompetitorCrawl }
