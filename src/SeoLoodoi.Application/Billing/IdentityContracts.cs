namespace SeoLoodoi.Application.Billing;

/// <summary>
/// Adapter to the central Loodoi identity service, which is the authority for
/// the stable Loodoi account id of an authenticated SEO Loodoi user.
/// <para>Contract (docs/LOODOI-IDENTITY-CONTRACT.md):</para>
/// <list type="bullet">
/// <item>Input is the server-side authenticated user id only, never an e-mail
/// address or any value supplied by the browser.</item>
/// <item>Output is an opaque, immutable account id (3–128 chars, <c>[A-Za-z0-9_\-:.]</c>)
/// that stays the same across plan changes, renewals, cancellations, expirations,
/// failed payments, reactivations, and profile or e-mail changes.</item>
/// <item>Returns <c>null</c> when the identity cannot be resolved (for example the
/// service is not configured or is unreachable). Callers must then refuse the
/// operation. They must never fall back to inventing an id.</item>
/// </list>
/// The central service does not exist in this repository. Only
/// <c>UnconfiguredLoodoiIdentityProvider</c> (production default: always null) and
/// the clearly labelled <c>DevelopmentLoodoiIdentityProvider</c> ship here.
/// </summary>
public interface ILoodoiIdentityProvider
{
    /// <summary>True only for the development adapter; surfaced for diagnostics.</summary>
    bool IsDevelopmentAdapter { get; }
    Task<string?> ResolveAccountIdAsync(Guid authenticatedUserId, CancellationToken ct);
}

/// <summary>Raised when the Loodoi identity service cannot provide a stable account id.</summary>
public sealed class LoodoiIdentityUnavailableException(string message) : InvalidOperationException(message);
