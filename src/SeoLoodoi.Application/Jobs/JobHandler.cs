using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Jobs;

public interface ISeoJobHandler
{
    SeoJobType Type { get; }
    Task HandleAsync(SeoBackgroundJob job, CancellationToken ct);
}
