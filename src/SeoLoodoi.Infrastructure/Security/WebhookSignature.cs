using System.Security.Cryptography;
using System.Text;

namespace SeoLoodoi.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 payload signatures for alert webhooks, Stripe-style:
/// the signed string is "{unix-timestamp}.{payload}" so a captured body cannot
/// be replayed without its timestamp, and receivers can bound how old a
/// delivery they accept. Comparison is constant-time.
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "X-Loodoi-Timestamp";
    public const string SignatureHeader = "X-Loodoi-Signature";

    public static string GenerateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string Sign(long unixTimestampSeconds, string payload, string secret)
    {
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixTimestampSeconds}.{payload}"));
        return "sha256=" + Convert.ToHexString(signature).ToLowerInvariant();
    }

    public static bool Verify(long unixTimestampSeconds, string payload, string secret, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var expected = Encoding.UTF8.GetBytes(Sign(unixTimestampSeconds, payload, secret));
        var actual = Encoding.UTF8.GetBytes(signatureHeader);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
