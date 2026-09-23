using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using System.Threading.RateLimiting;
using SeoLoodoi.Api.Middleware;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.AI;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Application.Reports;
using SeoLoodoi.Application.SearchConsole;
using SeoLoodoi.Application.Serp;
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
// P0 production hardening: outside Development, refuse to start with dev
// secrets, the dev billing adapter, or account flows that cannot send mail.
if (!builder.Environment.IsDevelopment() && !EF.IsDesignTime)
{
    var configErrors = DeploymentConfigurationValidator.Validate(builder.Configuration);
    if (configErrors.Count > 0)
        throw new InvalidOperationException("Unsafe deployment configuration:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", configErrors));
}
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // B6: dedicated limiter so webhook bursts from the billing authority neither starve
    // nor are starved by the interactive login limiter.
    o.AddPolicy("billing-webhook", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<IUrlNormalizer, UrlNormalizer>();
builder.Services.AddScoped<CrawlFrontierPlanner>();
builder.Services.AddSingleton<IScoringEngine, ScoringEngine>();
builder.Services.AddSingleton<IContentSimilarityEngine, ContentSimilarityEngine>();
builder.Services.AddSingleton<IInternalLinkGraph, InternalLinkGraph>();
builder.Services.AddHealthChecks();

if (!builder.Environment.IsDevelopment())
{
    // Machine-parseable logs for the container log pipeline; developers keep
    // the default readable console format.
    builder.Logging.AddJsonConsole();
}
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
app.UseMiddleware<RequestLoggingMiddleware>();
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
        PreferredLanguage = SupportedLanguages.Normalize(request.PreferredLanguage),
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
        await guard.ValidateAsync(url, ct);
        // Atomic: count + insert under the owner's quota lock (race-safe, rolls back on failure).
        var project = await quota.WithTenantLockAsync(UserId(user), async (lease, token) =>
        {
            await lease.EnsureAvailableAsync(QuotaDimension.Projects, 1, token);
            var created = new SeoProject(UserId(user), request.Name, normalizer.Normalize(url));
            await repo.AddAsync(created, token); await repo.SaveChangesAsync(token);
            return created;
        }, ct);
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
api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/analysis-status", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IAnalysisStatusService analysis, CancellationToken ct) =>
    await analysis.GetStatusAsync(projectId, UserId(user), crawlId, ct) is { } status ? Results.Ok(status) : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/analysis/retry", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IAnalysisStatusService analysis, CancellationToken ct) =>
{
    try { return await analysis.RetryAsync(projectId, UserId(user), crawlId, ct) is { } status ? Results.Accepted($"/api/seo/projects/{projectId}/crawls/{crawlId}/analysis-status", status) : Results.NotFound(); }
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
    if (!SupportedLanguages.IsSupported(request.PreferredLanguage)) errors["preferredLanguage"] = ["زبان انتخاب‌شده پشتیبانی نمی‌شود."];
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
        project.UpdateSettings(new CrawlSettings(request.MaxPages ?? current.MaxPages, request.MaxDepth ?? current.MaxDepth, request.Concurrency ?? current.Concurrency, request.DelayMilliseconds ?? current.DelayMilliseconds, request.TimeoutSeconds ?? current.TimeoutSeconds, request.RetryCount ?? current.RetryCount, request.ObeyRobots ?? current.ObeyRobots, request.FollowRedirects ?? current.FollowRedirects, request.IncludeSubdomains ?? current.IncludeSubdomains, request.MaxResponseBytes ?? current.MaxResponseBytes, request.UserAgent ?? current.UserAgent, request.Schedule ?? current.Schedule, request.ScheduleHourUtc ?? current.ScheduleHourUtc,
            request.RenderMode ?? current.RenderMode, request.DiscoveryMode ?? current.DiscoveryMode, request.Viewport ?? current.Viewport, request.MaxRendersPerCrawl ?? current.MaxRendersPerCrawl, request.UrlList ?? current.UrlList));
        await repo.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, UserId(user), "CRAWL_SETTINGS_UPDATED", "SeoProject", projectId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { project.Settings.Schedule, project.Settings.MaxPages, project.Settings.MaxDepth, project.Settings.RenderMode, project.Settings.DiscoveryMode, project.Settings.Viewport }), http.Connection.RemoteIpAddress?.ToString(), ct);
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
        .Select(x => new { x.Id, x.Url, x.StatusCode, x.ContentType, x.Depth, x.ResponseTimeMs, x.IsIndexable, x.WordCount, x.ContentHash, x.RedirectChainJson, Snapshot = db.PageSnapshots.Where(s => s.CrawledUrlId == x.Id).Select(s => new { s.Title, s.MetaDescription, s.H1, s.Canonical, s.RobotsMeta, s.Language, s.ImageCount, s.MissingAltCount, s.InternalLinkCount, s.ExternalLinkCount, s.HreflangJson, s.OpenGraphJson, s.TwitterCardsJson, s.XRobotsTag, s.AssetsJson }).FirstOrDefault() }).ToListAsync(ct);
    var total = await db.CrawledUrls.CountAsync(x => x.ProjectId == projectId && x.CrawlId == selectedCrawl, ct);
    return Results.Ok(new { crawlId = selectedCrawl, page = number, pageSize = size, total, pages = rows });
});

api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/redirects", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var rows = await db.CrawledUrls.AsNoTracking()
        .Where(x => x.ProjectId == projectId && x.CrawlId == crawlId && x.RedirectChainJson != "[]")
        .Select(x => new { x.Id, x.Url, x.StatusCode, x.ResponseTimeMs, x.RedirectChainJson })
        .ToListAsync(ct);
    return Results.Ok(rows);
});

api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/rendering", async (Guid projectId, Guid crawlId, bool? mismatchesOnly, int? page, int? pageSize, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    // Crawler v2 D3: deterministic raw-vs-rendered evidence for one crawl (tenant-scoped).
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    if (!await db.Crawls.AnyAsync(x => x.Id == crawlId && x.ProjectId == projectId, ct)) return Results.NotFound();
    var size = Math.Clamp(pageSize ?? 50, 1, 200); var index = Math.Max(1, page ?? 1);
    var query = db.PageRenderEvidences.AsNoTracking().Where(x => x.CrawlId == crawlId && x.ProjectId == projectId);
    var summary = new
    {
        Total = await query.CountAsync(ct),
        Rendered = await query.CountAsync(x => x.Status == SeoLoodoi.Domain.Seo.RenderEvidenceStatus.Rendered, ct),
        Failed = await query.CountAsync(x => x.Status == SeoLoodoi.Domain.Seo.RenderEvidenceStatus.Failed, ct),
        QuotaExceeded = await query.CountAsync(x => x.Status == SeoLoodoi.Domain.Seo.RenderEvidenceStatus.QuotaExceeded, ct),
        CapacityRejected = await query.CountAsync(x => x.Status == SeoLoodoi.Domain.Seo.RenderEvidenceStatus.CapacityRejected, ct),
        Disabled = await query.CountAsync(x => x.Status == SeoLoodoi.Domain.Seo.RenderEvidenceStatus.Disabled, ct),
        WithCriticalDifferences = await query.CountAsync(x => x.CriticalDifferences > 0, ct)
    };
    if (mismatchesOnly == true) query = query.Where(x => x.CriticalDifferences > 0);
    var items = await query.OrderByDescending(x => x.CriticalDifferences).ThenBy(x => x.NormalizedUrl).Skip((index - 1) * size).Take(size)
        .Select(x => new { x.Id, x.CrawledUrlId, Url = x.NormalizedUrl, x.RenderMode, x.Viewport, x.Status, x.Reason, x.TriggerSignalsJson, x.DiffJson, x.RenderedFinalUrl, x.RawWordCount, x.RenderedWordCount, x.CriticalDifferences, x.SubresourceRequests, x.BlockedRequests, x.JsErrors, x.DurationMs, x.CreatedAt })
        .ToListAsync(ct);
    return Results.Ok(new { Summary = summary, Page = index, PageSize = size, Items = items });
});
api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/assets", async (Guid projectId, Guid crawlId, string? type, bool? mixedContentOnly, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var snapshots = await db.PageSnapshots.AsNoTracking()
        .Where(s => s.CrawlId == crawlId && s.AssetsJson != "[]")
        .Select(s => new { s.CrawledUrlId, s.AssetsJson })
        .ToListAsync(ct);
    return Results.Ok(snapshots);
});

api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/links/graph", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IProjectAccessService access, IInternalLinkGraph linkGraph, IUrlNormalizer normalizer, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var pages = await db.CrawledUrls.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == crawlId).ToListAsync(ct);
    if (pages.Count == 0) return Results.Ok(new { summary = new { totalInternalLinks = 0, orphanPages = 0, deadEndPages = 0, weaklyLinkedPages = 0 }, topAnchors = Array.Empty<object>(), pages = Array.Empty<object>() });

    string? NormalizeOrNull(string value) { try { return normalizer.Normalize(new Uri(value)).AbsoluteUri; } catch { return null; } }
    var normalizedToId = pages.Select(x => (Id: x.Id, Url: NormalizeOrNull(x.Url))).Where(x => x.Url is not null).GroupBy(x => x.Url!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);
    var seedUrls = (await db.CrawlFrontierItems.AsNoTracking().Where(x => x.CrawlId == crawlId && x.DiscoveredFromId == null).Select(x => x.NormalizedUrl).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var graphPages = pages.Select(x => { var normalized = NormalizeOrNull(x.Url); var root = Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/"; return new GraphPage(x.Id, x.Url, root, normalized is not null && seedUrls.Contains(normalized)); }).ToArray();
    var graphLinks = (await db.PageLinks.AsNoTracking().Where(x => x.CrawlId == crawlId && x.IsInternal).Select(x => new { x.SourceUrlId, x.NormalizedTarget, x.AnchorText }).ToListAsync(ct))
        .Where(x => normalizedToId.ContainsKey(x.NormalizedTarget)).Select(x => new GraphLink(x.SourceUrlId, normalizedToId[x.NormalizedTarget], x.AnchorText)).ToArray();

    var summary = linkGraph.AnalyzeGraph(graphPages, graphLinks);
    var pageUrlMap = pages.ToDictionary(x => x.Id, x => x.Url);
    var pageResponses = summary.Pages.Select(p => new
    {
        pageId = p.PageId,
        url = pageUrlMap.GetValueOrDefault(p.PageId, ""),
        inDegree = p.InDegree,
        outDegree = p.OutDegree,
        internalAuthority = p.InternalAuthority,
        isOrphan = p.IsOrphan,
        isWeaklyLinked = p.IsWeaklyLinked,
        isDeadEnd = p.IsDeadEnd
    }).OrderByDescending(x => x.internalAuthority).ToArray();

    return Results.Ok(new
    {
        summary = new
        {
            totalInternalLinks = summary.TotalInternalLinks,
            orphanPages = summary.OrphanPageCount,
            deadEndPages = summary.DeadEndPageCount,
            weaklyLinkedPages = summary.WeaklyLinkedCount
        },
        topAnchors = summary.TopAnchors,
        pages = pageResponses
    });
});

api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/content/analysis", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IProjectAccessService access, IContentQualityEngine qualityEngine, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var snapshots = await db.PageSnapshots.AsNoTracking()
        .Where(s => s.CrawlId == crawlId && !string.IsNullOrWhiteSpace(s.TextContent))
        .Join(db.CrawledUrls.AsNoTracking().Where(u => u.ProjectId == projectId && u.CrawlId == crawlId),
            s => s.CrawledUrlId, u => u.Id,
            // WordCount lives on CrawledUrl (page-level crawl result), not on PageSnapshot.
            (s, u) => new { u.Id, u.Url, s.TextContent, s.Title, u.WordCount })
        .Take(50)
        .ToListAsync(ct);

    var analyses = snapshots.Select(s => qualityEngine.Analyze(s.TextContent, s.Url)).ToArray();
    var totalWords = analyses.Sum(a => a.Readability.WordCount);
    // No analyzed pages means no readability measurement, not a score of zero.
    decimal? avgScore = analyses.Length > 0 ? decimal.Round(analyses.Average(a => a.Readability.ReadabilityScore), 1) : null;
    var thinCount = analyses.Count(a => a.IsThinContent);
    var stuffingCount = analyses.Count(a => a.HasKeywordStuffing);

    return Results.Ok(new
    {
        summary = new
        {
            pagesAnalyzed = analyses.Length,
            totalWords,
            averageReadabilityScore = avgScore,
            thinContentPages = thinCount,
            keywordStuffingPages = stuffingCount
        },
        pages = analyses
    });
});

api.MapGet("/projects/{projectId:guid}/recommendations", async (Guid projectId, RecommendationStatus? status, Guid? crawlId, ClaimsPrincipal user, IRecommendationQueryService recommendations, CancellationToken ct) =>
{
    if (status is not null && !Enum.IsDefined(status.Value)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["وضعیت پیشنهاد معتبر نیست."] });
    return Results.Ok(await recommendations.ListAsync(projectId, UserId(user), status, ct, crawlId));
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
api.MapGet("/projects/{projectId:guid}/keywords/summary", async (Guid projectId, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) => Results.Ok(await keywords.SummaryAsync(projectId, UserId(user), ct)));
api.MapGet("/projects/{projectId:guid}/keywords/cannibalization", async (Guid projectId, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) => Results.Ok(await keywords.CannibalizationAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/keywords/batch", async (Guid projectId, BatchCreateKeywordsRequest request, ClaimsPrincipal user, IKeywordService keywords, CancellationToken ct) =>
{
    try { return Results.Ok(await keywords.BatchCreateAsync(projectId, UserId(user), request, ct)); }
    catch (QuotaExceededException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
});

// PHASE 10 — SERP intelligence. Every response states whether results were
// actually observed by a provider; positions are never inferred or estimated.
api.MapGet("/projects/{projectId:guid}/serp/status", async (Guid projectId, ClaimsPrincipal user, ISerpService serp, IProjectAccessService access, CancellationToken ct) => !await access.CanViewAsync(projectId, UserId(user), ct) ? Results.NotFound() : Results.Ok(await serp.ProviderStatusAsync(ct)));
api.MapGet("/projects/{projectId:guid}/keywords/{keywordId:guid}/serp", async (Guid projectId, Guid keywordId, ClaimsPrincipal user, ISerpService serp, CancellationToken ct) => await serp.LatestAsync(projectId, keywordId, UserId(user), ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/keywords/{keywordId:guid}/serp/history", async (Guid projectId, Guid keywordId, int? limit, ClaimsPrincipal user, ISerpService serp, CancellationToken ct) => Results.Ok(await serp.HistoryAsync(projectId, keywordId, UserId(user), limit ?? 12, ct)));
api.MapPost("/projects/{projectId:guid}/keywords/{keywordId:guid}/serp/refresh", async (Guid projectId, Guid keywordId, SerpDevice? device, SerpSurface? surface, ClaimsPrincipal user, ISerpService serp, CancellationToken ct) =>
{
    try { return await serp.RequestRefreshAsync(projectId, keywordId, UserId(user), device, surface, ct) is { } snapshot ? Results.Accepted($"/api/seo/projects/{projectId}/keywords/{keywordId}/serp", snapshot) : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status402PaymentRequired); }
});
api.MapGet("/projects/{projectId:guid}/keywords/{keywordId:guid}/serp/{snapshotId:guid}/results", async (Guid projectId, Guid keywordId, Guid snapshotId, int? take, ClaimsPrincipal user, ISerpService serp, CancellationToken ct) => await serp.ResultsAsync(projectId, snapshotId, UserId(user), take ?? 50, ct) is { } page ? Results.Ok(page) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/keywords/{keywordId:guid}/serp/compare", async (Guid projectId, Guid keywordId, Guid from, Guid to, ClaimsPrincipal user, ISerpService serp, CancellationToken ct) =>
    from == Guid.Empty || to == Guid.Empty
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["هر دو شناسه snapshots الزامی است."] })
        : await serp.CompareAsync(projectId, UserId(user), from, to, ct) is { } comparison ? Results.Ok(comparison) : Results.NotFound());

// PHASE 11 — AEO / GEO. Reading the assessment only needs view access; running a
// new one needs edit access because it fetches robots.txt and persists a snapshot.
api.MapGet("/aeo/crawlers", async (ClaimsPrincipal user, IAeoService aeo, CancellationToken ct) => Results.Ok(await aeo.ListProfilesAsync(ct)));
api.MapGet("/projects/{projectId:guid}/crawls/{crawlId:guid}/aeo", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IAeoService aeo, CancellationToken ct) =>
    await aeo.LatestAsync(projectId, crawlId, UserId(user), ct) is { } report ? Results.Ok(report) : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/aeo/analyze", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, IAeoService aeo, CancellationToken ct) =>
    await aeo.AnalyzeAsync(projectId, crawlId, UserId(user), ct) is { } report ? Results.Ok(report) : Results.NotFound());

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
api.MapPost("/projects/{projectId:guid}/competitors/{competitorId:guid}/crawl", async (Guid projectId, Guid competitorId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) =>
{
    try { return await competitors.StartCrawlAsync(projectId, competitorId, UserId(user), ct) is { } crawl ? Results.Accepted($"/api/seo/projects/{projectId}/competitors/{competitorId}/crawls/latest", crawl) : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapGet("/projects/{projectId:guid}/competitors/{competitorId:guid}/crawls", async (Guid projectId, Guid competitorId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => Results.Ok(await competitors.ListCrawlsAsync(projectId, competitorId, UserId(user), ct)));
api.MapGet("/projects/{projectId:guid}/competitors/{competitorId:guid}/crawls/latest", async (Guid projectId, Guid competitorId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => await competitors.LatestCrawlAsync(projectId, competitorId, UserId(user), ct) is { } crawl ? Results.Ok(crawl) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/competitors/compare", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => await competitors.CompareAsync(projectId, UserId(user), crawlId, ct) is { } comparison ? Results.Ok(comparison) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/competitors/gaps", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, ICompetitorService competitors, CancellationToken ct) => Results.Ok(await competitors.GapAnalysisAsync(projectId, UserId(user), crawlId, ct)));

// PHASE 9 — backlinks. Every response states whether the data was actually
// observed by a provider (Available/Partial) or not (NotConfigured/Unavailable).
// The platform never substitutes estimated or crawled data for provider data.
api.MapGet("/projects/{projectId:guid}/backlinks/status", async (Guid projectId, ClaimsPrincipal user, IBacklinkService backlinks, IProjectAccessService access, CancellationToken ct) => !await access.CanViewAsync(projectId, UserId(user), ct) ? Results.NotFound() : Results.Ok(await backlinks.ProviderStatusAsync(ct)));
api.MapGet("/projects/{projectId:guid}/backlinks", async (Guid projectId, ClaimsPrincipal user, IBacklinkService backlinks, CancellationToken ct) => await backlinks.LatestAsync(projectId, UserId(user), ct) is { } latest ? Results.Ok(latest) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/backlinks/history", async (Guid projectId, int? limit, ClaimsPrincipal user, IBacklinkService backlinks, CancellationToken ct) => Results.Ok(await backlinks.HistoryAsync(projectId, UserId(user), limit ?? 12, ct)));
api.MapPost("/projects/{projectId:guid}/backlinks/refresh", async (Guid projectId, ClaimsPrincipal user, IBacklinkService backlinks, CancellationToken ct) =>
{
    try { return await backlinks.RequestRefreshAsync(projectId, UserId(user), ct) is { } snapshot ? Results.Accepted($"/api/seo/projects/{projectId}/backlinks", snapshot) : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status402PaymentRequired); }
});
api.MapGet("/projects/{projectId:guid}/backlinks/{snapshotId:guid}/links", async (Guid projectId, Guid snapshotId, int? take, ClaimsPrincipal user, IBacklinkService backlinks, CancellationToken ct) => await backlinks.ObservationsAsync(projectId, snapshotId, UserId(user), take ?? 100, ct) is { } page ? Results.Ok(page) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/backlinks/compare", async (Guid projectId, Guid from, Guid to, ClaimsPrincipal user, IBacklinkService backlinks, CancellationToken ct) =>
    from == Guid.Empty || to == Guid.Empty
        ? Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["هر دو شناسه snapshots الزامی است."] })
        : await backlinks.CompareAsync(projectId, UserId(user), from, to, ct) is { } diff ? Results.Ok(diff) : Results.NotFound());

api.MapGet("/billing/entitlements", async (ClaimsPrincipal user, IEntitlementService billing, CancellationToken ct) => Results.Ok(await billing.GetEntitlementsAsync(UserId(user), ct)));
api.MapGet("/billing/plans", async (IEntitlementService billing, CancellationToken ct) => Results.Ok(await billing.GetAvailablePlansAsync(ct)));
api.MapPost("/billing/checkout", async (CheckoutSessionRequest request, ClaimsPrincipal user, IEntitlementService billing, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.TargetPlan) || !PlanCatalog.IsValidPlan(request.TargetPlan)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetPlan"] = ["پلن انتخابی معتبر نیست."] });
    try { return Results.Ok(await billing.CreateCheckoutSessionAsync(UserId(user), request, ct)); }
    catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["returnUrl"] = ["آدرس بازگشت مجاز نیست."] }); }
    catch (LoodoiIdentityUnavailableException) { return Results.Problem("سرویس هویت Loodoi در دسترس نیست؛ امکان شروع پرداخت وجود ندارد.", statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (LoodoiIdentityConflictException) { return Results.Conflict(new { error = "این حساب Loodoi به فضای کاری دیگری متصل است." }); }
    catch (InvalidOperationException) { return Results.Problem("فقط مالک فضای کاری می‌تواند اشتراک را مدیریت کند.", statusCode: StatusCodes.Status403Forbidden); }
});
api.MapPost("/billing/checkout/return", async (CheckoutReturnRequest request, ClaimsPrincipal user, IEntitlementService billing, CancellationToken ct) =>
{
    try { return Results.Ok(await billing.ProcessCheckoutReturnAsync(UserId(user), request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

api.MapPost("/projects/{projectId:guid}/ai/analyze", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, IAiAnalysisService ai, IEntitlementService entitlements, CancellationToken ct) =>
{
    if (!await entitlements.ConsumeAiCreditsAsync(UserId(user), 1, ct)) return Results.Problem("اعتبار تحلیل هوش مصنوعی شما برای دوره جاری به پایان رسیده است. لطفاً پلن خود را ارتقا دهید.", statusCode: StatusCodes.Status429TooManyRequests);
    return await ai.AnalyzeProjectAsync(projectId, UserId(user), crawlId, ct) is { } analysis ? Results.Ok(analysis) : Results.NotFound();
});
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
app.MapPost("/api/billing/webhook", async (HttpContext http, IEntitlementService billing, CancellationToken ct) =>
{
    if (http.Request.ContentLength is > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
    var buffer = new char[64 * 1024 + 1];
    var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
    if (read > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    var payloadJson = new string(buffer, 0, read);
    var signature = http.Request.Headers["X-Loodoi-Signature"].ToString();
    var timestamp = http.Request.Headers["X-Loodoi-Timestamp"].ToString();
    var success = await billing.ProcessWebhookAsync(payloadJson, signature, timestamp, ct);
    return success ? Results.Ok(new { received = true }) : Results.Unauthorized();
}).RequireRateLimiting("billing-webhook");
api.MapGet("/projects/{projectId:guid}/reports",  async (Guid projectId, ClaimsPrincipal user, IReportService reports, CancellationToken ct) => Results.Ok(await reports.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/reports", async (Guid projectId, CreateReportRequest request, ClaimsPrincipal user, IReportService reports, CancellationToken ct) =>
{
    try { return await reports.CreateAsync(projectId, UserId(user), request, ct) is { } report ? Results.Created($"/api/seo/projects/{projectId}/reports/{report.Id}", report) : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
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

static IResult TeamFailure(TeamOperationError error, string? message) => error switch
{
    TeamOperationError.NotFound => Results.NotFound(),
    TeamOperationError.Forbidden => Results.Problem(message, statusCode: StatusCodes.Status403Forbidden),
    TeamOperationError.Conflict => Results.Conflict(new { error = message }),
    TeamOperationError.QuotaExceeded => Results.Problem(message, statusCode: StatusCodes.Status429TooManyRequests),
    _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [message ?? "Invalid request."] })
};

api.MapGet("/projects/{projectId:guid}/members", async (Guid projectId, ClaimsPrincipal user, IProjectAccessService access, AppDbContext db, CancellationToken ct) =>
{
    if (!await access.CanViewAsync(projectId, UserId(user), ct)) return Results.NotFound();
    var members = await db.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == projectId).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new ProjectMemberDto(m.Id, m.UserId, u.Email ?? "", u.DisplayName, m.Role.ToString(), m.CreatedAt, m.Status.ToString())).ToListAsync(ct);
    return Results.Ok(members);
});
api.MapGet("/projects/{projectId:guid}/members/seats", async (Guid projectId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
    await team.SeatsAsync(projectId, UserId(user), ct) is { } seats ? Results.Ok(seats) : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/members", async (Guid projectId, AddProjectMemberRequest request, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.AddExistingUserAsync(projectId, UserId(user), request, ct);
    if (!result.Ok) return TeamFailure(result.Error, result.Message);
    var m = result.Value!;
    return Results.Created($"/api/seo/projects/{projectId}/members/{m.Id}", new ProjectMemberDto(m.Id, m.UserId, m.Email, m.DisplayName, m.Role, m.CreatedAt, m.Status));
});
api.MapPatch("/projects/{projectId:guid}/members/{memberId:guid}", async (Guid projectId, Guid memberId, ChangeProjectMemberRoleRequest request, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.ChangeRoleAsync(projectId, memberId, UserId(user), request.Role, ct);
    return result.Ok ? Results.NoContent() : TeamFailure(result.Error, result.Message);
});
api.MapPost("/projects/{projectId:guid}/members/{memberId:guid}/suspend", async (Guid projectId, Guid memberId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.SetSuspendedAsync(projectId, memberId, UserId(user), true, ct);
    return result.Ok ? Results.NoContent() : TeamFailure(result.Error, result.Message);
});
api.MapPost("/projects/{projectId:guid}/members/{memberId:guid}/reactivate", async (Guid projectId, Guid memberId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.SetSuspendedAsync(projectId, memberId, UserId(user), false, ct);
    return result.Ok ? Results.NoContent() : TeamFailure(result.Error, result.Message);
});
api.MapDelete("/projects/{projectId:guid}/members/{memberId:guid}", async (Guid projectId, Guid memberId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.RemoveAsync(projectId, memberId, UserId(user), ct);
    return result.Ok ? Results.NoContent() : TeamFailure(result.Error, result.Message);
});
api.MapGet("/projects/{projectId:guid}/invitations", async (Guid projectId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
    await team.ListInvitationsAsync(projectId, UserId(user), ct) is { } list ? Results.Ok(list) : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/invitations", async (Guid projectId, InviteProjectMemberRequest request, ClaimsPrincipal user, ITeamService team, IEmailDelivery email, IConfiguration config, ILogger<Program> logger, CancellationToken ct) =>
{
    var result = await team.InviteAsync(projectId, UserId(user), request, ct);
    if (!result.Ok) return TeamFailure(result.Error, result.Message);
    var created = result.Value!;
    // The token is sent only to the invited address. It is never returned to the client or logged.
    var link = $"{(config["Application:WebBaseUrl"] ?? "").TrimEnd('/')}/?invite={Uri.EscapeDataString(created.Token)}";
    var delivered = true;
    try { await email.SendAsync(created.Invitation.Email, "دعوت به پروژه در SEO Loodoi", $"<p>شما به یک پروژه در SEO Loodoi دعوت شده‌اید.</p><p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">پذیرش دعوت</a></p><p>این لینک ۷ روز اعتبار دارد.</p>", ct); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        delivered = false;
        logger.LogWarning("Invitation {InvitationId} e-mail could not be delivered ({Error})", created.Invitation.Id, ex.GetType().Name);
    }
    return Results.Created($"/api/seo/projects/{projectId}/invitations/{created.Invitation.Id}", new { invitation = created.Invitation, emailDelivered = delivered });
});
api.MapDelete("/projects/{projectId:guid}/invitations/{invitationId:guid}", async (Guid projectId, Guid invitationId, ClaimsPrincipal user, ITeamService team, CancellationToken ct) =>
{
    var result = await team.CancelInvitationAsync(projectId, invitationId, UserId(user), ct);
    return result.Ok ? Results.NoContent() : TeamFailure(result.Error, result.Message);
});
api.MapPost("/invitations/accept", async (AcceptInvitationRequest request, ClaimsPrincipal user, ITeamService team, UserManager<ApplicationUser> users, CancellationToken ct) =>
{
    var account = await users.FindByIdAsync(UserId(user).ToString());
    if (account is null) return Results.NotFound();
    var result = await team.AcceptInvitationAsync(account.Id, account.Email ?? "", request.Token ?? "", ct);
    return result.Ok ? Results.Ok(result.Value) : TeamFailure(result.Error, result.Message);
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
    if (!SupportedLanguages.IsSupported(request.PreferredLanguage)) errors["preferredLanguage"] = ["زبان انتخاب‌شده پشتیبانی نمی‌شود."];
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

/// <summary>
/// The product's supported UI languages. This is the single source of truth for
/// language validation; the frontend i18n module mirrors this exact list.
/// </summary>
public static class SupportedLanguages
{
    public static readonly string[] Codes = ["fa", "en", "ar", "zh", "es", "fr", "de", "ru", "pt", "tr", "hi"];
    public static bool IsSupported(string? code) => code is not null && Array.Exists(Codes, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
    /// <summary>Keeps any supported language as-is; falls back to the default (fa).</summary>
    public static string Normalize(string? code) => IsSupported(code) ? code!.ToLowerInvariant() : "fa";
}
public sealed record TwoFactorUpdateRequest(bool? Enable = null, string? Code = null, bool ResetAuthenticatorKey = false, bool ResetRecoveryCodes = false);
public sealed record UpdateCrawlSettingsRequest(int? MaxPages = null, int? MaxDepth = null, int? Concurrency = null, int? DelayMilliseconds = null, int? TimeoutSeconds = null, int? RetryCount = null, bool? ObeyRobots = null, bool? FollowRedirects = null, bool? IncludeSubdomains = null, int? MaxResponseBytes = null, string? UserAgent = null, string? Schedule = null, int? ScheduleHourUtc = null,
    string? RenderMode = null, string? DiscoveryMode = null, string? Viewport = null, int? MaxRendersPerCrawl = null, string? UrlList = null);
public sealed record UpdateIssueStatusRequest(IssueStatus Status);
public partial class Program;
