using System.Net.Mail;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Urls;
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
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
var identity = app.MapGroup("/api/auth").RequireRateLimiting("auth");
identity.MapIdentityApi<ApplicationUser>().AddEndpointFilter(async (context, next) =>
    context.HttpContext.Request.Path.Value?.EndsWith("/register", StringComparison.OrdinalIgnoreCase) == true ? Results.NotFound() : await next(context));

app.MapPost("/api/account/register", async (RegisterAccountRequest request, UserManager<ApplicationUser> users) =>
{
    var errors = ValidateRegistration(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors, title: "لطفاً اطلاعات ثبت‌نام را اصلاح کنید.");
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
    return Results.Created("/api/account/me", new { user.Id, user.Email, user.DisplayName, user.CompanyName, user.PreferredLanguage });
}).RequireRateLimiting("auth");

app.MapGet("/api/account/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    return user is null ? Results.NotFound() : Results.Ok(new { user.Id, user.Email, user.DisplayName, user.CompanyName, user.PreferredLanguage, user.RegisteredAt });
}).RequireAuthorization().RequireRateLimiting("api");

var api = app.MapGroup("/api/seo").RequireAuthorization().RequireRateLimiting("api");
api.MapGet("/projects", async (ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) => Results.Ok(await repo.ListForOwnerAsync(UserId(user), ct)));
api.MapPost("/projects", async (CreateProjectRequest request, ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) =>
{
    if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = ["A valid absolute HTTP(S) URL is required."] });
    var project = new SeoProject(UserId(user), request.Name, url);
    await repo.AddAsync(project, ct); await repo.SaveChangesAsync(ct);
    return Results.Created($"/api/seo/projects/{project.Id}", project);
});
api.MapGet("/projects/{id:guid}", async (Guid id, ClaimsPrincipal user, ISeoProjectRepository repo, CancellationToken ct) => (await repo.FindOwnedAsync(id, UserId(user), ct)) is { } project ? Results.Ok(project) : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/crawls", async (Guid projectId, ClaimsPrincipal user, ICrawlQueryService queries, CancellationToken ct) => Results.Ok(await queries.ListAsync(projectId, UserId(user), ct)));
api.MapPost("/projects/{projectId:guid}/crawls", async (Guid projectId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) =>
{
    try { return await commands.StartAsync(projectId, UserId(user), ct) is { } crawl ? Results.Accepted($"/api/seo/projects/{projectId}/crawls/{crawl.Id}", crawl) : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/pause", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) => await commands.PauseAsync(projectId, crawlId, UserId(user), ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/resume", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) => await commands.ResumeAsync(projectId, crawlId, UserId(user), ct) ? Results.Accepted() : Results.NotFound());
api.MapPost("/projects/{projectId:guid}/crawls/{crawlId:guid}/cancel", async (Guid projectId, Guid crawlId, ClaimsPrincipal user, ICrawlCommandService commands, CancellationToken ct) => await commands.CancelAsync(projectId, crawlId, UserId(user), ct) ? Results.NoContent() : Results.NotFound());
api.MapGet("/projects/{projectId:guid}/issues", async (Guid projectId, Guid? crawlId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => Results.Ok(await queries.ListIssuesAsync(projectId, UserId(user), crawlId, ct)));
api.MapGet("/projects/{projectId:guid}/scores/latest", async (Guid projectId, ClaimsPrincipal user, IAuditQueryService queries, CancellationToken ct) => await queries.LatestScoreAsync(projectId, UserId(user), ct) is { } score ? Results.Ok(score) : Results.NotFound());

app.Run();
static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
static Dictionary<string, string[]> ValidateRegistration(RegisterAccountRequest request)
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120) errors["fullName"] = ["نام کامل باید بین ۲ تا ۱۲۰ کاراکتر باشد."];
    if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out var parsed) || !string.Equals(parsed.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase)) errors["email"] = ["یک ایمیل معتبر وارد کنید."];
    if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 10) errors["password"] = ["رمز عبور باید حداقل ۱۰ کاراکتر باشد."];
    else
    {
        var passwordErrors = new List<string>();
        if (!request.Password.Any(char.IsUpper)) passwordErrors.Add("حداقل یک حرف بزرگ انگلیسی لازم است.");
        if (!request.Password.Any(char.IsLower)) passwordErrors.Add("حداقل یک حرف کوچک انگلیسی لازم است.");
        if (!request.Password.Any(char.IsDigit)) passwordErrors.Add("حداقل یک عدد لازم است.");
        if (request.Password.All(char.IsLetterOrDigit)) passwordErrors.Add("حداقل یک نماد مانند ! یا @ لازم است.");
        if (passwordErrors.Count > 0) errors["password"] = passwordErrors.ToArray();
    }
    if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal)) errors["confirmPassword"] = ["تکرار رمز عبور با رمز اصلی یکسان نیست."];
    if (!request.AcceptTerms) errors["acceptTerms"] = ["پذیرش قوانین استفاده و حریم خصوصی الزامی است."];
    if (request.CompanyName?.Length > 160) errors["companyName"] = ["نام شرکت نمی‌تواند بیشتر از ۱۶۰ کاراکتر باشد."];
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
public partial class Program;
