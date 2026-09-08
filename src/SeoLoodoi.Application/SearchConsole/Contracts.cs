namespace SeoLoodoi.Application.SearchConsole;

public sealed record SearchConsoleConnectionStatus(bool Connected, string Provider, DateTimeOffset? ExpiresAt, string Status);
public sealed record SearchConsoleSyncRequest(DateOnly StartDate, DateOnly EndDate, string Country = "ALL", string Device = "ALL");
public sealed record SearchConsoleSyncResult(int RowsReceived, int KeywordsUpdated, DateOnly StartDate, DateOnly EndDate, bool Partial, string? Warning);
public interface ISearchConsoleService
{
    Task<string?> GetAuthorizationUrlAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<bool> CompleteAuthorizationAsync(string state, string code, CancellationToken ct);
    Task<SearchConsoleConnectionStatus?> StatusAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<SearchConsoleSyncResult?> SyncAsync(Guid projectId, Guid userId, SearchConsoleSyncRequest request, CancellationToken ct);
}
