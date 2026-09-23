using Microsoft.Extensions.Configuration;
using SeoLoodoi.Infrastructure.Billing;

namespace SeoLoodoi.Infrastructure.Security;

/// <summary>
/// Fail-fast validation of security-critical configuration for non-Development
/// environments. The application refuses to start rather than silently running
/// with development secrets, the development billing adapter, or account
/// flows (email verification / password reset) that cannot deliver mail.
/// Returned messages never contain secret values.
/// </summary>
public static class DeploymentConfigurationValidator
{
    public const int MinimumSecretLength = 32;

    public static IReadOnlyList<string> Validate(IConfiguration config)
    {
        var errors = new List<string>();

        // ---- Billing (Loodoi is the billing authority) ----
        var billing = config.GetSection("Billing").Get<LoodoiBillingOptions>() ?? new LoodoiBillingOptions();
        if (billing.EnableDevMock)
            errors.Add("Billing:EnableDevMock must be false outside Development (it grants plans without payment).");
        CheckSecret(errors, "Billing:SecretKey", billing.SecretKey, LoodoiBillingOptions.DevSigningKey);
        CheckSecret(errors, "Billing:WebhookSecret", billing.WebhookSecret, LoodoiBillingOptions.DevWebhookSecret);
        if (!string.IsNullOrEmpty(billing.SecretKey) && billing.SecretKey == billing.WebhookSecret)
            errors.Add("Billing:SecretKey and Billing:WebhookSecret must be different secrets.");
        if (!IsHttpsUrl(billing.CheckoutEndpoint))
            errors.Add("Billing:CheckoutEndpoint must be an absolute https URL.");

        // ---- Email / account flows ----
        var emailEnabled = config.GetValue<bool>("Email:Enabled");
        var requireConfirmed = config.GetValue<bool>("Identity:RequireConfirmedEmail");
        var allowUnverified = config.GetValue<bool>("Identity:AllowUnverifiedAccountsInProduction");
        if (emailEnabled)
        {
            if (string.IsNullOrWhiteSpace(config["Email:Host"])) errors.Add("Email:Host is required when Email:Enabled=true.");
            if (string.IsNullOrWhiteSpace(config["Email:FromAddress"]) || !System.Net.Mail.MailAddress.TryCreate(config["Email:FromAddress"], out _))
                errors.Add("Email:FromAddress must be a valid address when Email:Enabled=true.");
            var port = config.GetValue("Email:Port", 587);
            if (port is <= 0 or > 65535) errors.Add("Email:Port is out of range.");
            if (!config.GetValue("Email:EnableSsl", true)) errors.Add("Email:EnableSsl must be true outside Development.");
        }
        if (requireConfirmed && !emailEnabled)
            errors.Add("Identity:RequireConfirmedEmail=true requires Email:Enabled=true with a configured SMTP server.");
        if (!requireConfirmed && !allowUnverified)
            errors.Add("Identity:RequireConfirmedEmail must be true outside Development (or explicitly opt out with Identity:AllowUnverifiedAccountsInProduction=true).");
        if (!emailEnabled && !allowUnverified)
            errors.Add("Email:Enabled must be true outside Development so password reset and verification mail can be delivered (or explicitly opt out with Identity:AllowUnverifiedAccountsInProduction=true).");

        // ---- Public URLs used in emails / redirects ----
        if (!IsHttpsUrl(config["Application:PublicBaseUrl"])) errors.Add("Application:PublicBaseUrl must be an absolute https URL.");
        if (!IsHttpsUrl(config["Application:WebBaseUrl"])) errors.Add("Application:WebBaseUrl must be an absolute https URL.");

        // ---- Database ----
        var provider = config["DatabaseProvider"] ?? "Postgres";
        if (string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            errors.Add("DatabaseProvider=InMemory is not allowed outside Development.");
        var connection = config.GetConnectionString("Postgres") ?? string.Empty;
        if (connection.Contains("CHANGE_ME", StringComparison.Ordinal))
            errors.Add("ConnectionStrings:Postgres still contains the CHANGE_ME placeholder password.");

        return errors;
    }

    private static void CheckSecret(List<string> errors, string key, string? value, string devDefault)
    {
        if (string.IsNullOrWhiteSpace(value) || value == devDefault)
            errors.Add($"{key} must be set to a unique production secret (the development default is not allowed).");
        else if (value.Length < MinimumSecretLength)
            errors.Add($"{key} must be at least {MinimumSecretLength} characters.");
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
