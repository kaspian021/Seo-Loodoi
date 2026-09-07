using Microsoft.AspNetCore.Identity;

namespace SeoLoodoi.Infrastructure.Persistence;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string PreferredLanguage { get; set; } = "fa";
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TermsAcceptedAt { get; set; }
}
