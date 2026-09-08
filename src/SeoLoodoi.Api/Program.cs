using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Infrastructure.Security;
using SeoLoodoi.Infrastructure.Identity;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure;
using SeoLoodoi.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<IUrlNormalizer, UrlNormalizer>();
builder.Services.AddScoped<CrawlFrontierPlanner>();
builder.Services.AddSingleton<IScoringEngine, ScoringEngine>();
builder.Services.AddSingleton<IContentSimilarityEngine, ContentSimilarityEngine>();
builder.Services.AddSingleton<IInternalLinkGraph, InternalLinkGraph>();
builder.Services.AddHealthChecks();

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (migrationDb.Database.IsRelational()) await migrationDb.Database.MigrateAsync();
}
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) => (IResult)(await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)));
var identity = app.MapGroup("/api/auth").RequireRateLimiting("auth");
identity.MapIdentityApi<ApplicationUser>().AddEndpointFilter(async (context, next) =>
    context.HttpContext.Request.Path.Value?.EndsWith("/register", StringComparison.OrdinalIgnoreCase) == true ? Results.NotFound() : await next(context));

app.MapPost("/api/account/register", async (RegisterAccountRequest request, UserManager<ApplicationUser> users, IEmailSender<ApplicationUser> emailSender, IOptions<EmailOptions> emailOptions, IConfiguration configuration, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    var errors = ValidateRegistration(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors, title: "لطفاً اطلاعات ثبت‌نام را اصلاح کنید.");
    if (configuration.GetValue<bool>("Identity:RequireConfirmedEmail") && !emailOptions.Value.Enabled) return Results.Problem("تأیید ایمیل فعال است اما سرویس ایمیل پیکربندی نشده است.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var email = request.Email.Trim();
    var user = new ApplicationUser
    {
        UserName = email,
        Email = email,
        DisplayName = request.FullName.Trim(),
        CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? null : request.CompanyName.Trim(),
        PreferredLanguage = request.PreferredLanguage is "en" ? "en" : "fa",
        RegisteredAt = DateTimeOffset.UtcNow,
        TermsAcceptedAt = DateTimeOffset.UtcNow
    };
    var result = await users.CreateAsync(user, request.Password);
    if (!result.Succeeded)
    {
        var identityErrors = result.Errors.GroupBy(x => IdentityField(x.Code)).ToDictionary(g => g.Key, g => g.Select(x => PersianIdentityError(x.Code)).Distinct().ToArray());
        return Results.ValidationProblem(identityErrors, title: "ساخت حساب کاربری انجام نشد.");
    }
    if (configuration.GetValue<bool>("Identity:RequireConfirmedEmail"))
    {
        try
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var publicBaseUrl = configuration["Application:PublicBaseUrl"]?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}";
            var confirmationLink = $"{publicBaseUrl}/api/auth/confirmEmail?userId={Uri.EscapeDataString(user.Id.ToString())}&code={Uri.EscapeDataString(encodedToken)}";
            await emailSender.SendConfirmationLinkAsync(user, user.Email!, confirmationLink);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Mail.SmtpException or ArgumentException)
        {
            await users.DeleteAsync(user);
            return Results.Problem("ارسال ایمیل تأیید انجام نشد؛ حساب ساخته نشد.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    await audit.RecordAsync(null, user.Id, "ACCOUNT_REGISTERED", "ApplicationUser", user.Id.ToString(), "{}", http.Connection.RemoteIpAddress?.ToString(), ct);
    return Results.Created("/api/account/me", new { user.Id, user.Email, user.DisplayName, user.CompanyName, user.PreferredLanguage, requiresEmailConfirmation = configuration.GetValue<bool>("Identity:RequireConfirmedEmail") });
}).RequireRateLimiting("auth");

app.MapGet("/api/account/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    return user is null ? Results.NotFound() : Results.Ok(new { user.Id, user.Email, user.DisplayName, user.CompanyName, user.PreferredLanguage, user.RegisteredAt, user.TermsAcceptedAt });
}).RequireAuthorization().RequireRateLimiting("api");

var api = app.MapGroup("/api/seo").RequireAuthorization().RequireRateLimiting("api");
api.MapGet("/projects", async (ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) => Results.Ok(await repo.ListForOwnerAsync(UserId(user), ct)));
api.MapPost("/projects", async (CreateProjectRequest request, ClaimsPrincipal user, ISeoProjectRepository repo, IQuotaService quota, IOutboundUrlGuard guard, IUrlNormalizer normalizer, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length is < 2 or > 160) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["نام پروژه باید بین ۲ تا ۱۶۰ کاراکتر باشد."] });
    if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = ["یک آدرس مطلق HTTP یا HTTPS معتبر وارد کنید."] });
    try
    {
        await guard.ValidateAsync(url, ct); await quota.EnsureCanCreateProjectAsync(UserId(user), ct);
        var project = new SeoProject(UserId(user), request.Name, normalizer.Normalize(url));
        await repo.AddAsync(project, ct); await repo.SaveChangesAsync(ct);
        await audit.RecordAsync(project.Id, UserId(user), "PROJECT_CREATED", "SeoProject", project.Id.ToString(), $"{{\"baseUrl\":{System.Text.Json.JsonSerializer.Serialize(project.BaseUrl)}}}", http.Connection.RemoteIpAddress?.ToString(), ct);
        return Results.Created($"/api/seo/projects/{project.Id}", project);
    }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ex.Message] }); }
});
api.MapGet("/projects/{id:guid}", async (Guid id, ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) => (await repo.FindOwnedAsync(id, UserId(user), ct)) is { } project ? Results.Ok(project) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/crawls", async (Guid projectId, ClaimsPrincipal user, ICrawlQueryService queries, CancellationToken ct) => Results.Ok(await queries.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/crawls", async (Guid projectId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) =>
{
    try { return await commands.StartAsync(projectId, UserId(user), ct) is { } crawl ? Results.Accepted($"/api/seo/projects/{projectId}/crawls/{crawl.Id}", crawl) : Results.NotFound(); }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/pause", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) =>
{
    try { return await commands.PauseAsync(projectId, crawlId, UserId(user), ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/resume", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) =>
{
    try { return await commands.ResumeAsync(projectId, crawlId, UserId(user), ct) ? Results.Accepted() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/cancel", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) =>
{
    try { return await commands.CancelAsync(projectId, crawlId, UserId(user), ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapGet("/projects/{projectId:guid}/issues", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => Results.Ok(await queries.ListIssuesAsync(projectId, UserId(user), crawlId, ct)));
api.MapGet("/projects/{projectId:guid}/scores/latest", async (Guid projectId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => await queries.LatestScoreAsync(projectId, UserId(user), ct) is { } score ? Results.Ok(score) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/scores/history", async (Guid projectId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => Results.Ok(await queries.ScoreHistoryAsync(projectId, UserId(user), ct)));
api.MapGet("/projects/{projectId:guid}/audit-logs", async (Guid projectId, ClaimsPrincipal user, IAuditLogService audit, CancellationToken ct) => Results.Ok(await audit.ListAsync(projectId, UserId(user), ct)));

// Product capabilities ------------------------------------------------------
app.MapPut("/api/account/me", async (UpdateProfileRequest request, ClaimsPrincipal principal, UserManager<ApplicationUser> users, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120) errors["displayName"] = ["نام کامل باید بین ۲ تا ۱۲۰ کاراکتر باشد."];
    if (request.CompanyName?.Trim().Length > 160) errors["companyName"] = ["نام شرکت نمی‌تواند بیشتر از ۱۶۰ کاراکتر باشد."];
    if (request.PreferredLanguage is not ("fa" or "en")) errors["preferredLanguage"] = ["زبان انتخاب‌شده پشتیبانی نمی‌شود."];
    if (errors.Count > 0) return Results.ValidationProblem(errors, title: "اطلاعات پروفایل معتبر نیست.");
    user.DisplayName = request.DisplayName.Trim(); user.CompanyName = string.IsNullOrWhiteSpace(request.CompanyName) ? null : request.CompanyName.Trim(); user.PreferredLanguage = request.PreferredLanguage;
    var result = await users.UpdateAsync(user); if (!result.Succeeded) return Results.Problem("ذخیره پروفایل انجام نشد.", statusCode: 500);
    await audit.RecordAsync(null, user.Id, "PROFILE_UPDATED", "ApplicationUser", user.Id.ToString(), "{}", http.Connection.RemoteIpAddress?.ToString(), ct);
    return Results.Ok(new { user.Id, user.Email, user.DisplayName, user.CompanyName, user.PreferredLanguage, user.RegisteredAt, user.TermsAcceptedAt });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapGet("/api/account/security/2fa", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var key = await users.GetAuthenticatorKeyAsync(user);
    if (string.IsNullOrWhiteSpace(key))
    {
        var reset = await users.ResetAuthenticatorKeyAsync(user);
        if (!reset.Succeeded) return Results.Problem("ساخت کلید احراز هویت دومرحله‌ای انجام نشد.", statusCode: StatusCodes.Status500InternalServerError);
        key = await users.GetAuthenticatorKeyAsync(user);
    }
    return Results.Ok(new { enabled = user.TwoFactorEnabled, sharedKey = user.TwoFactorEnabled ? null : key });
}).RequireAuthorization().RequireRateLimiting("api");

app.MapPost("/api/account/security/2fa", async (TwoFactorUpdateRequest request, ClaimsPrincipal principal, UserManager<ApplicationUser> users, CancellationToken ct) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    if (request.ResetAuthenticatorKey && user.TwoFactorEnabled) return Results.Conflict(new { error = "برای تعویض کلید، ابتدا احراز هویت دومرحله‌ای را غیرفعال کنید." });
    if (request.ResetAuthenticatorKey)
    {
        var reset = await users.ResetAuthenticatorKeyAsync(user);
        if (!reset.Succeeded) return Results.Problem("بازنشانی کلید احراز هویت انجام نشد.", statusCode: StatusCodes.Status500InternalServerError);
    }
    var key = await users.GetAuthenticatorKeyAsync(user);
    if (request.Enable is true)
    {
        if (!user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(key)) return Results.Conflict(new { error = "ابتدا کلید احراز هویت را دریافت کنید." });
            if (string.IsNullOrWhiteSpace(request.Code) || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.Code.Trim())) return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["کد برنامه احراز هویت معتبر نیست."] });
            var enabled = await users.SetTwoFactorEnabledAsync(user, true);
            if (!enabled.Succeeded) return Results.Problem("فعال‌سازی احراز هویت دومرحله‌ای انجام نشد.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
    else if (request.Enable is false)
    {
        var disabled = await users.SetTwoFactorEnabledAsync(user, false);
        if (!disabled.Succeeded) return Results.Problem("غیرفعال‌سازی احراز هویت دومرحله‌ای انجام نشد.", statusCode: StatusCodes.Status500InternalServerError);
    }
    string[]? recoveryCodes = null;
    if (request.ResetRecoveryCodes)
    {
        if (!user.TwoFactorEnabled && request.Enable is not true) return Results.Conflict(new { error = "احراز هویت دومرحله‌ای فعال نیست." });
        var generated = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        if (generated is null) return Results.Problem("ساخت کدهای بازیابی انجام نشد.", statusCode: StatusCodes.Status500InternalServerError);
        recoveryCodes = generated.ToArray();
    }
    return Results.Ok(new { enabled = user.TwoFactorEnabled, sharedKey = user.TwoFactorEnabled ? null : await users.GetAuthenticatorKeyAsync(user), recoveryCodes });
}).RequireAuthorization().RequireRateLimiting("api");

api.MapGet("/usage", async (ClaimsPrincipal user, IQuotaService quota, CancellationToken ct) => Results.Ok(await quota.GetAsync(UserId(user), ct)));
api.MapGet("/projects/{projectId:guid}/dashboard", async (Guid projectId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => await queries.DashboardAsync(projectId, UserId(user), ct) is { } dashboard ? Results.Ok(dashboard) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/settings", async (Guid projectId, ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) => await repo.FindOwnedAsync(projectId, UserId(user), ct) is { } project ? Results.Ok(project.Settings) : Results.NotFound());
api.MapPut("/projects/{projectId:guid}/settings", async (Guid projectId, UpdateCrawlSettingsRequest request, ClaimsPrincipal user, ISeoProjectRepository repo, IProjectAccessService access, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (!await access.CanEditAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var project = await repo.FindOwnedAsync(projectId, UserId(user), ct); if (project is null) return Results.NotFound();
    try
    {
        var current = project.Settings;
        project.UpdateSettings(new CrawlSettings(request.MaxPages ?? current.MaxPages, request.MaxDepth ?? current.MaxDepth, request.Concurrency ?? current.Concurrency, request.DelayMilliseconds ?? current.DelayMilliseconds, request.TimeoutSeconds ?? current.TimeoutSeconds, request.RetryCount ?? current.RetryCount, request.ObeyRobots ?? current.ObeyRobots, request.FollowRedirects ?? current.FollowRedirects, request.IncludeSubdomains ?? current.IncludeSubdomains, request.MaxResponseBytes ?? current.MaxResponseBytes, request.UserAgent ?? current.UserAgent, request.Schedule ?? current.Schedule, request.ScheduleHourUtc ?? current.ScheduleHourUtc));
        await repo.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, UserId(user), "CRAWL_SETTINGS_UPDATED", "SeoProject", projectId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { project.Settings.Schedule, project.Settings.MaxPages, project.Settings.MaxDepth }), http.Connection.RemoteIpAddress?.ToString(), ct);
        return Results.Ok(project.Settings);
    }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [ex.Message] }); }
});

api.MapGet("/projects/{projectId:guid}/pages", async (Guid projectId, Guid? crawlId, int? page, int? pageSize, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var selectedCrawl = crawlId ?? await db.Crawls.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync(ct);
    var size = Math.Clamp(pageSize ?? 50, 1, 200); var number = Math.Max(1, page ?? 1);
    var rows = await db.CrawledUrls.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == selectedCrawl).OrderBy(x => x.Url).Skip((number - 1) * size).Take(size)
        .Select(x => new { x.Id, x.Url, x.StatusCode, x.ContentType, x.Depth, x.ResponseTimeMs, x.IsIndexable, x.WordCount, x.ContentHash, x.RedirectChainJson, Snapshot = db.PageSnapshots.Where(s => s.CrawledUrlId == x.Id).Select(s => new { s.Title, s.MetaDescription, s.H1, s.Canonical, s.RobotsMeta, s.Language, s.ImageCount, s.MissingAltCount, s.InternalLinkCount, s.ExternalLinkCount, s.HreflangJson, s.OpenGraphJson, s.TwitterCardsJson, s.XRobotsTag }).FirstOrDefault() }).ToListAsync(ct);
    var total = await db.CrawledUrls.CountAsync(x => x.ProjectId == projectId && x.CrawlId == selectedCrawl, ct);
    return Results.Ok(new { crawlId = selectedCrawl, page = number, pageSize = size, total, pages = rows });
});

api.MapGet("/projects/{projectId:guid}/recommendations", async (Guid projectId, RecommendationStatus? status, ClaimsPrincipal user, IRecommendationQueryService recommendations, CancellationToken ct) =>
{
    if (status is not null && !Enum.IsDefined(status.Value)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["وضعیت پیشنهاد معتبر نیست."] });
    return Results.Ok(await recommendations.ListAsync(projectId, UserId(user), status, ct));
});
api.MapPatch("/projects/{projectId:guid}/recommendations/{recommendationId:guid}", async (Guid projectId, Guid recommendationId, UpdateRecommendationStatusRequest request, ClaimsPrincipal user, IRecommendationQueryService recommendations, CancellationToken ct) =>
{
    if (!Enum.IsDefined(request.Status)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["وضعیت پیشنهاد معتبر نیست."] });
    return await recommendations.UpdateStatusAsync(projectId, recommendationId, UserId(user), request.Status, ct) ? Results.NoContent() : Results.NotFound();
});
api.MapPatch("/projects/{projectId:guid}/issues/{issueId:guid}", async (Guid projectId, Guid issueId, UpdateIssueStatusRequest request, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (!await access.CanEditAsync(projectId, UserId(user), ct)) return Results.NotFound();
    if (!Enum.IsDefined(request.Status)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["وضعیت مشکل معتبر نیست."] });
    var issue = await db.SeoIssues.SingleOrDefaultAsync(x => x.Id == issueId && x.ProjectId == projectId, ct); if (issue is null) return Results.NotFound();
    issue.ChangeStatus(request.Status); await db.SaveChangesAsync(ct); await audit.RecordAsync(projectId, UserId(user), "ISSUE_STATUS_CHANGED", "SeoIssue", issueId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { status = request.Status.ToString() }), http.Connection.RemoteIpAddress?.ToString(), ct); return Results.NoContent();
});

api.MapGet("/projects/{projectId:guid}/keywords", async (Guid projectId, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) => Results.Ok(await keywords.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/keywords", async (Guid projectId, CreateKeywordRequest request, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) =>
{
    try { return await keywords.CreateAsync(projectId, UserId(user), request, ct) is { } keyword ? Results.Created($"/api/seo/projects/{projectId}/keywords/{keyword.Id}", keyword) : Results.NotFound(); }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["phrase"] = [ex.Message] }); }
});
api.MapDelete("/projects/{projectId:guid}/keywords/{keywordId:guid}", async (Guid projectId, Guid keywordId, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) => await keywords.DeleteAsync(projectId, keywordId, UserId(user), ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/keywords/{keywordId:guid}/metrics", async (Guid projectId, Guid keywordId, ImportKeywordMetricRequest request, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) =>
{
    try { return await keywords.AddMetricAsync(projectId, keywordId, UserId(user), request, ct) ? Results.NoContent() : Results.Conflict(new { error = "A metric for this keyword and segment already exists, or the resource is not accessible." }); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["metric"] = [ex.Message] }); }
});
api.MapGet("/projects/{projectId:guid}/keywords/opportunities", async (Guid projectId, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) => Results.Ok(await keywords.OpportunitiesAsync(projectId, UserId(user), ct)));

api.MapGet("/projects/{projectId:guid}/competitors", async (Guid projectId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => Results.Ok(await competitors.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/competitors", async (Guid projectId, CreateCompetitorRequest request, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) =>
{
    try { return await competitors.CreateAsync(projectId, UserId(user), request, ct) is { } competitor ? Results.Created($"/api/seo/projects/{projectId}/competitors/{competitor.Id}", competitor) : Results.NotFound(); }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ex.Message] }); }
});
api.MapPatch("/projects/{projectId:guid}/competitors/{competitorId:guid}", async (Guid projectId, Guid competitorId, UpdateCompetitorRequest request, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => await competitors.UpdateAsync(projectId, competitorId, UserId(user), request, ct) ? Results.NoContent() : Results.NotFound());
api.MapDelete("/projects/{projectId:guid}/competitors/{competitorId:guid}", async (Guid projectId, Guid competitorId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => await competitors.DeleteAsync(projectId, competitorId, UserId(user), ct) ? Results.NoContent() : Results.NotFound());

api.MapPost("/projects/{projectId:guid}/ai/analyze", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, IAiAnalysisService ai, CancellationToken ct) => await ai.AnalyzeProjectAsync(projectId, UserId(user), crawlId, ct) is { } analysis ? Results.Ok(analysis) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/search-console/connect", async (Guid projectId, ClaimsPrincipal user, ISearchConsoleService searchConsole, CancellationToken ct) => await searchConsole.GetAuthorizationUrlAsync(projectId, UserId(user), ct) is { } url ? Results.Ok(new { authorizationUrl = url }) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/search-console/status", async (Guid projectId, ClaimsPrincipal user, ISearchConsoleService searchConsole, CancellationToken ct) => await searchConsole.StatusAsync(projectId, UserId(user), ct) is { } status ? Results.Ok(status) : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/search-console/sync", async (Guid projectId, SearchConsoleSyncRequest request, ClaimsPrincipal user, ISearchConsoleService searchConsole, CancellationToken ct) =>
{
    try { return await searchConsole.SyncAsync(projectId, UserId(user), request, ct) is { } result ? Results.Ok(result) : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = [ex.Message] }); }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/integrations/google/search-console/callback", async (string? state, string? code, ISearchConsoleService searchConsole, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code) || !await searchConsole.CompleteAuthorizationAsync(state, code, ct)) return Results.BadRequest(new { error = "OAuth callback could not be completed." });
    return Results.Ok(new { connected = true, message = "Search Console connected. You may close this window." });
}).RequireRateLimiting("auth");
api.MapGet("/projects/{projectId:guid}/reports",  async (Guid projectId, ClaimsPrincipal user, IReportService reports, CancellationToken ct) => Results.Ok(await reports.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/reports", async (Guid projectId, CreateReportRequest request, ClaimsPrincipal user, IReportService reports, CancellationToken ct) =>
{
    try { return await reports.CreateAsync(projectId, UserId(user), request, ct) is { } report ? Results.Created($"/api/seo/projects/{projectId}/reports/{report.Id}", report) : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [ex.Message] }); }
});
api.MapGet("/projects/{projectId:guid}/reports/{reportId:guid}/download", async (Guid projectId, Guid reportId, ClaimsPrincipal user, IReportService reports, CancellationToken ct) => await reports.DownloadAsync(projectId, reportId, UserId(user), ct) is { } file ? Results.File(file.Bytes, file.ContentType, file.FileName) : Results.NotFound());

api.MapGet("/projects/{projectId:guid}/alerts/rules", async (Guid projectId, ClaimsPrincipal user, IAlertService alerts, CancellationToken ct) => Results.Ok(await alerts.ListRulesAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/alerts/rules", async (Guid projectId, CreateAlertRuleRequest request, ClaimsPrincipal user, IAlertService alerts, CancellationToken ct) =>
{
    try { return await alerts.CreateRuleAsync(projectId, UserId(user), request, ct) is { } rule ? Results.Created($"/api/seo/projects/{projectId}/alerts/rules/{rule.Id}", rule) : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["alert"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["destination"] = [ex.Message] }); }
});
api.MapPatch("/projects/{projectId:guid}/alerts/rules/{ruleId:guid}", async (Guid projectId, Guid ruleId, UpdateAlertRuleRequest request, ClaimsPrincipal user, IAlertService alerts, CancellationToken ct) =>
{
    try { return await alerts.UpdateRuleAsync(projectId, ruleId, UserId(user), request, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["alert"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["destination"] = [ex.Message] }); }
});
api.MapPost("/projects/{projectId:guid}/alerts/check", async (Guid projectId, ClaimsPrincipal user, IAlertService alerts, CancellationToken ct) => Results.Ok(new { created = await alerts.CheckAsync(projectId, UserId(user), ct) }));
api.MapGet("/projects/{projectId:guid}/alerts/events", async (Guid projectId, ClaimsPrincipal user, IAlertService alerts, CancellationToken ct) => Results.Ok(await alerts.ListEventsAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/alerts/events/{eventId:guid}/read", async (Guid projectId, Guid eventId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var alertEvent = await db.AlertEvents.SingleOrDefaultAsync(x => x.Id == eventId && x.ProjectId == projectId, ct);
    if (alertEvent is null) return Results.NotFound();
    alertEvent.MarkRead(); await db.SaveChangesAsync(ct); return Results.NoContent();
});
api.MapDelete("/projects/{projectId:guid}/alerts/rules/{ruleId:guid}", async (Guid projectId, Guid ruleId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanManageAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == ruleId && x.ProjectId == projectId, ct);
    if (rule is null) return Results.NotFound();
    db.AlertRules.Remove(rule); await db.SaveChangesAsync(ct); return Results.NoContent();
});

api.MapGet("/projects/{projectId:guid}/members", async (Guid projectId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var members = await db.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == projectId).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new ProjectMemberDto(m.Id, m.UserId, u.Email ?? "", u.DisplayName, m.Role.ToString(), m.CreatedAt)).ToListAsync(ct);
    return Results.Ok(members);
});
api.MapPost("/projects/{projectId:guid}/members", async (Guid projectId, AddProjectMemberRequest request, ClaimsPrincipal user, IProjectAccessService access, UserManager<ApplicationUser> users, AppDbContext db, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (!await access.CanManageAsync(projectId, UserId(user), ct)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Email)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["ایمیل همکار الزامی است."] });
    if (!Enum.IsDefined(request.Role)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["نقش انتخاب‌شده معتبر نیست."] });
    var memberUser = await users.FindByEmailAsync(request.Email.Trim()); if (memberUser is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["حسابی با این ایمیل پیدا نشد."] });
    if (memberUser.Id == UserId(user) || await db.SeoProjects.AnyAsync(x => x.Id == projectId && x.OwnerId == memberUser.Id, ct)) return Results.Conflict(new { error = "مالک پروژه نمی‌تواند به‌عنوان عضو اضافه شود." });
    if (await db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.UserId == memberUser.Id, ct)) return Results.Conflict(new { error = "این کاربر قبلاً عضو پروژه است." });
    var member = new ProjectMember(projectId, memberUser.Id, request.Role); db.ProjectMembers.Add(member); await db.SaveChangesAsync(ct);
    await audit.RecordAsync(projectId, UserId(user), "PROJECT_MEMBER_ADDED", "ProjectMember", member.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { member.UserId, role = member.Role.ToString() }), http.Connection.RemoteIpAddress?.ToString(), ct);
    return Results.Created($"/api/seo/projects/{projectId}/members/{member.Id}", new ProjectMemberDto(member.Id, member.UserId, memberUser.Email ?? "", memberUser.DisplayName, member.Role.ToString(), member.CreatedAt));
});
api.MapPatch("/projects/{projectId:guid}/members/{memberId:guid}", async (Guid projectId, Guid memberId, ChangeProjectMemberRoleRequest request, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (!await access.CanManageAsync(projectId, UserId(user), ct)) return Results.NotFound();
    if (!Enum.IsDefined(request.Role)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["نقش انتخاب‌شده معتبر نیست."] });
    var member = await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == projectId, ct); if (member is null) return Results.NotFound(); member.ChangeRole(request.Role); await db.SaveChangesAsync(ct); await audit.RecordAsync(projectId, UserId(user), "PROJECT_MEMBER_ROLE_CHANGED", "ProjectMember", member.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { role = member.Role.ToString() }), http.Connection.RemoteIpAddress?.ToString(), ct); return Results.NoContent();
});
api.MapDelete("/projects/{projectId:guid}/members/{memberId:guid}", async (Guid projectId, Guid memberId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, IAuditLogService audit, HttpContext http, CancellationToken ct) =>
{
    if (!await access.CanManageAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var member = await db.ProjectMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.ProjectId == projectId, ct); if (member is null) return Results.NotFound(); db.ProjectMembers.Remove(member); await db.SaveChangesAsync(ct); await audit.RecordAsync(projectId, UserId(user), "PROJECT_MEMBER_REMOVED", "ProjectMember", memberId.ToString(), "{}", http.Connection.RemoteIpAddress?.ToString(), ct); return Results.NoContent();
});

app.Run();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
static Dictionary<string, string[]> ValidateRegistration(RegisterAccountRequest request)
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120) errors["fullName"] = ["نام کامل باید بین ۲ تا ۱۲۰ کاراکتر باشد."];
    if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out var parsed) || parsed is null || !string.Equals(parsed.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase)) errors["email"] = ["یک ایمیل معتبر وارد کنید."];
    if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 10) errors["password"] = ["رمز عبور باید حداقل ۱۰ کاراکتر باشد."];
    else
    {
        var passwordErrors = new List<string>();
        if (!request.Password.Any(c => c is >= 'A' and <= 'Z')) passwordErrors.Add("حداقل یک حرف بزرگ انگلیسی لازم است.");
        if (!request.Password.Any(c => c is >= 'a' and <= 'z')) passwordErrors.Add("حداقل یک حرف کوچک انگلیسی لازم است.");
        if (!request.Password.Any(c => c is >= '0' and <= '9')) passwordErrors.Add("حداقل یک عدد لازم است.");
        if (request.Password.All(char.IsLetterOrDigit)) passwordErrors.Add("حداقل یک نماد مانند ! یا @ لازم است.");
        if (passwordErrors.Count > 0) errors["password"] = passwordErrors.ToArray();
    }
    if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal)) errors["confirmPassword"] = ["تکرار رمز عبور با رمز اصلی یکسان نیست."];
    if (!request.AcceptTerms) errors["acceptTerms"] = ["پذیرش قوانین استفاده و حریم خصوصی الزامی است."];
    if (request.CompanyName?.Trim().Length > 160) errors["companyName"] = ["نام شرکت نمی‌تواند بیشتر از ۱۶۰ کاراکتر باشد."];
    if (request.PreferredLanguage is not ("fa" or "en")) errors["preferredLanguage"] = ["زبان انتخاب‌شده پشتیبانی نمی‌شود."];
    return errors;
}
static string IdentityField(string code) => code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email";
static string PersianIdentityError(string code) => code switch
{
    "DuplicateEmail" or "DuplicateUserName" => "قبلاً حسابی با این ایمیل ساخته شده است.",
    "InvalidEmail" or "InvalidUserName" => "فرمت ایمیل معتبر نیست.",
    "PasswordTooShort" => "رمز عبور کوتاه‌تر از حد مجاز است.",
    "PasswordRequiresUpper" => "رمز عبور باید حرف بزرگ انگلیسی داشته باشد.",
    "PasswordRequiresLower" => "رمز عبور باید حرف کوچک انگلیسی داشته باشد.",
    "PasswordRequiresDigit" => "رمز عبور باید عدد داشته باشد.",
    "PasswordRequiresNonAlphanumeric" => "رمز عبور باید حداقل یک نماد داشته باشد.",
    _ => "اطلاعات واردشده قابل قبول نیست."
};
public sealed record RegisterAccountRequest(string FullName, string Email, string? CompanyName, string Password, string ConfirmPassword, bool AcceptTerms, string PreferredLanguage = "fa");
public sealed record UpdateProfileRequest(string DisplayName, string? CompanyName, string PreferredLanguage = "fa");
public sealed record TwoFactorUpdateRequest(bool? Enable = null, string? Code = null, bool ResetAuthenticatorKey = false, bool ResetRecoveryCodes = false);
public sealed record UpdateCrawlSettingsRequest(int? MaxPages = null, int? MaxDepth = null, int? Concurrency = null, int? DelayMilliseconds = null, int? TimeoutSeconds = null, int? RetryCount = null, bool? ObeyRobots = null, bool? FollowRedirects = null, bool? IncludeSubdomains = null, int? MaxResponseBytes = null, string? UserAgent = null, string? Schedule = null, int? ScheduleHourUtc = null);
public sealed record UpdateIssueStatusRequest(IssueStatus Status);
public partial class Program;
