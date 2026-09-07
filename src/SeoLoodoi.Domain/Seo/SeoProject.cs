using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

public sealed class SeoProject : Entity
{
    private SeoProject() { }
    public SeoProject(Guid ownerId, string name, Uri baseUri, CrawlSettings? settings = null)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (baseUri.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP(S) websites are supported.", nameof(baseUri));
        OwnerId = ownerId;
        Name = name.Trim();
        BaseUrl = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        NormalizedHost = baseUri.IdnHost.ToLowerInvariant();
        Settings = (settings ?? new CrawlSettings()).Validate();
    }

    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string NormalizedHost { get; private set; } = string.Empty;
    public ProjectStatus Status { get; private set; } = ProjectStatus.Active;
    public CrawlSettings Settings { get; private set; } = new();
    public DateTimeOffset? LastCrawlAt { get; private set; }
    public DateTimeOffset? NextCrawlAt { get; private set; }
    public void Archive() { Status = ProjectStatus.Archived; UpdatedAt = DateTimeOffset.UtcNow; }
}
