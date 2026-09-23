namespace SeoLoodoi.Infrastructure.Billing;

/// <summary>
/// Configuration for the central Loodoi Billing authority. SEO Loodoi never takes
/// payments itself; it only redirects to Loodoi checkout and applies entitlements
/// delivered by signed server-to-server webhooks.
/// </summary>
public sealed class LoodoiBillingOptions
{
    /// <summary>Well-known development-only secrets. Rejected at startup outside Development.</summary>
    public const string DevSigningKey = "dev_billing_signing_key_secret_for_hmac_32_chars_minimum!";
    public const string DevWebhookSecret = "dev_billing_webhook_secret_for_hmac_32_chars_minimum!";

    public string ServiceUrl { get; set; } = "https://billing.loodoi.com";
    public string SecretKey { get; set; } = DevSigningKey;
    public string WebhookSecret { get; set; } = DevWebhookSecret;
    public string CheckoutEndpoint { get; set; } = "https://billing.loodoi.com/checkout";

    /// <summary>
    /// DEVELOPMENT ADAPTER. When true, returning from "checkout" applies the plan
    /// locally without any payment. Off by default; enabled only in
    /// appsettings.Development.json; startup validation refuses it elsewhere.
    /// </summary>
    public bool EnableDevMock { get; set; }

    /// <summary>Plan given to a tenant that has never received a billing event.</summary>
    public string DefaultPlan { get; set; } = "Free";

    /// <summary>Checkout sessions older than this are refused on return.</summary>
    public int CheckoutSessionMinutes { get; set; } = 30;

    /// <summary>
    /// Absolute origins a checkout may return to (open-redirect protection).
    /// Application:WebBaseUrl and AllowedOrigins are added automatically.
    /// </summary>
    public List<string> AllowedReturnOrigins { get; set; } = [];

    /// <summary>Maximum accepted clock skew / age of a webhook timestamp.</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;
}
