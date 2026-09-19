namespace SeoLoodoi.Infrastructure.Billing;

public sealed class LoodoiBillingOptions
{
    public string ServiceUrl { get; set; } = "https://billing.loodoi.com";
    public string SecretKey { get; set; } = "dev_billing_signing_key_secret_for_hmac_32_chars_minimum!";
    public string WebhookSecret { get; set; } = "dev_billing_webhook_secret_for_hmac_32_chars_minimum!";
    public string CheckoutEndpoint { get; set; } = "https://billing.loodoi.com/checkout";
    public bool EnableDevMock { get; set; } = true;
}
