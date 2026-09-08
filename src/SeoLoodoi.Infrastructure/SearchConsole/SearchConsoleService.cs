using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.SearchConsole;

public sealed class SearchConsoleOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string ApiEndpoint { get; set; } = "https://www.googleapis.com/webmasters/v3/sites";
}

public sealed class SearchConsoleService(AppDbContext db, IProjectAccessService access, IQuotaService quota, HttpClient client, IOptions<SearchConsoleOptions> options, IDataProtectionProvider protection, IAuditLogService audit) : ISearchConsoleService
{
    private readonly SearchConsoleOptions _options = options.Value;
    private readonly IDataProtector _state = protection.CreateProtector("seo-loodoi/search-console/state/v1");
    private readonly IDataProtector _tokens = protection.CreateProtector("seo-loodoi/search-console/tokens/v1");

    public async Task<string?> GetAuthorizationUrlAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct) || string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.RedirectUri)) return null;
        var state = _state.Protect($"{projectId:N}|{userId:N}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        return $"{_options.AuthorizationEndpoint}?client_id={Uri.EscapeDataString(_options.ClientId)}&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}&response_type=code&access_type=offline&prompt=consent&scope={Uri.EscapeDataString("https://www.googleapis.com/auth/webmasters.readonly")}&state={Uri.EscapeDataString(state)}";
    }

    public async Task<bool> CompleteAuthorizationAsync(string state, string code, CancellationToken ct)
    {
        Guid projectId; Guid userId; try
        {
            var values = _state.Unprotect(state).Split('|');
            if (values.Length != 3 || !Guid.TryParse(values[0], out projectId) || !Guid.TryParse(values[1], out userId) || !long.TryParse(values[2], out var issued) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued) > 600 || !await access.CanManageAsync(projectId, userId, ct)) return false;
        }
        catch (Exception) { return false; }
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint) { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["code"] = code, ["client_id"] = _options.ClientId, ["client_secret"] = _options.ClientSecret, ["redirect_uri"] = _options.RedirectUri, ["grant_type"] = "authorization_code" }) };
        using var response = await client.SendAsync(request, ct); if (!response.IsSuccessStatusCode) return false;
        var token = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(ct), new JsonSerializerOptions(JsonSerializerDefaults.Web)); if (token?.AccessToken is null) return false;
        var connection = await db.ExternalConnections.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Provider == ExternalProvider.GoogleSearchConsole, ct);
        if (connection is null) db.ExternalConnections.Add(new ExternalConnection(projectId, ExternalProvider.GoogleSearchConsole, _tokens.Protect(token.AccessToken), token.RefreshToken is null ? null : _tokens.Protect(token.RefreshToken), DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn ?? 3600)));
        else connection.UpdateTokens(_tokens.Protect(token.AccessToken), token.RefreshToken is null ? null : _tokens.Protect(token.RefreshToken), DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn ?? 3600));
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "SEARCH_CONSOLE_CONNECTED", "ExternalConnection", projectId.ToString(), "{\"provider\":\"GoogleSearchConsole\"}", null, ct);
        return true;
    }

    public async Task<SearchConsoleConnectionStatus?> StatusAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var connection = await db.ExternalConnections.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Provider == ExternalProvider.GoogleSearchConsole, ct);
        return connection is null ? new SearchConsoleConnectionStatus(false, "GoogleSearchConsole", null, "Disconnected") : new SearchConsoleConnectionStatus(connection.Status == "Connected", "GoogleSearchConsole", connection.ExpiresAt, connection.Status);
    }

    public async Task<SearchConsoleSyncResult?> SyncAsync(Guid projectId, Guid userId, SearchConsoleSyncRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        if (request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber > 89) throw new ArgumentException("بازه Search Console باید بین ۱ تا ۹۰ روز باشد.");
        var project = await db.SeoProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct); if (project is null) return null;
        var connection = await db.ExternalConnections.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Provider == ExternalProvider.GoogleSearchConsole, ct); if (connection is null || connection.Status != "Connected") throw new InvalidOperationException("Search Console به این پروژه متصل نیست.");
        var accessToken = _tokens.Unprotect(connection.EncryptedAccessToken);
        if (connection.ExpiresAt is null || connection.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            if (string.IsNullOrWhiteSpace(connection.EncryptedRefreshToken)) throw new InvalidOperationException("Refresh token برای اتصال Search Console موجود نیست.");
            var refresh = await RefreshAsync(_tokens.Unprotect(connection.EncryptedRefreshToken), ct); accessToken = refresh.AccessToken ?? throw new InvalidOperationException("توکن دسترسی Search Console نامعتبر است."); var refreshedToken = refresh.RefreshToken is null ? connection.EncryptedRefreshToken : _tokens.Protect(refresh.RefreshToken); connection.UpdateTokens(_tokens.Protect(accessToken), refreshedToken, DateTimeOffset.UtcNow.AddSeconds(refresh.ExpiresIn ?? 3600)); await db.SaveChangesAsync(ct);
        }
        var site = Uri.EscapeDataString(project.BaseUrl + "/"); var url = $"{_options.ApiEndpoint.TrimEnd('/')}/{site}/searchAnalytics/query"; var rowsReceived = 0; var added = 0; var startRow = 0; var partial = false;
        while (true)
        {
            var body = new Dictionary<string,object> { ["startDate"] = request.StartDate.ToString("yyyy-MM-dd"), ["endDate"] = request.EndDate.ToString("yyyy-MM-dd"), ["dimensions"] = new[] { "query", "page", "date" }, ["rowLimit"] = 25000, ["startRow"] = startRow };
            if (request.Country != "ALL" || request.Device != "ALL")
            {
                var filters = new List<object>();
                if (request.Country != "ALL") filters.Add(new { dimension = "country", expression = request.Country.ToLowerInvariant(), operatorType = "equals" });
                if (request.Device != "ALL") filters.Add(new { dimension = "device", expression = request.Device.ToLowerInvariant(), operatorType = "equals" });
                body["dimensionFilterGroups"] = new[] { new { groupType = "and", filters } };
            }
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url); httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken); httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(httpRequest, ct); response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("rows", out var rows) || rows.GetArrayLength() == 0) break;
            foreach (var row in rows.EnumerateArray())
            {
                var keys = row.GetProperty("keys"); var phrase = keys.GetArrayLength() > 0 ? keys[0].GetString() : null; var page = keys.GetArrayLength() > 1 ? keys[1].GetString() : null; var dateText = keys.GetArrayLength() > 2 ? keys[2].GetString() : null; if (string.IsNullOrWhiteSpace(phrase) || !DateOnly.TryParse(dateText, out var metricDate)) continue;
                var keyword = await db.Keywords.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.NormalizedPhrase == Keyword.Normalize(phrase) && x.Country == "ALL", ct);
                if (keyword is null) { await quota.EnsureCanAddKeywordAsync(projectId, userId, ct); keyword = new Keyword(projectId, phrase, "fa", "ALL"); db.Keywords.Add(keyword); await db.SaveChangesAsync(ct); }
                var metricExists = await db.KeywordMetrics.AnyAsync(x => x.KeywordId == keyword.Id && x.Date == metricDate && x.PageUrl == page && x.Country == request.Country && x.Device == request.Device, ct); if (metricExists) continue;
                var clicks = ReadDecimal(row, "clicks"); var impressions = ReadDecimal(row, "impressions"); var ctr = ReadDecimal(row, "ctr"); var position = ReadDecimal(row, "position");
                db.KeywordMetrics.Add(new KeywordMetric(projectId, keyword.Id, metricDate, (int)Math.Round(clicks), (int)Math.Round(impressions), ctr, position, "search-console", page, request.Country, request.Device)); keyword.TouchMetrics(DateTimeOffset.UtcNow); added++;
            }
            rowsReceived += rows.GetArrayLength(); startRow += rows.GetArrayLength(); if (rows.GetArrayLength() < 25000 || startRow >= 100_000) { partial = rows.GetArrayLength() == 25000 && startRow >= 100_000; break; }
            await db.SaveChangesAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "SEARCH_CONSOLE_SYNCED", "KeywordMetric", null, JsonSerializer.Serialize(new { rowsReceived, added, request.StartDate, request.EndDate, partial }), null, ct);
        return new SearchConsoleSyncResult(rowsReceived, added, request.StartDate, request.EndDate, partial, partial ? "Search Console pagination was capped at 100,000 rows." : null);
    }

    private async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint) { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["client_id"] = _options.ClientId, ["client_secret"] = _options.ClientSecret, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }) };
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(ct), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Invalid token response.");
    }
    private static decimal ReadDecimal(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0;
    private sealed record TokenResponse(string? AccessToken, string? RefreshToken, int? ExpiresIn);
}
