using SeoLoodoi.Application.Billing;

namespace SeoLoodoi.Infrastructure.Billing;

public sealed class LoodoiIdentityOptions
{
    /// <summary>
    /// "Unconfigured" (default): no central identity service is wired, so checkout is
    /// refused with 503 instead of inventing an identity.
    /// "Development": DEVELOPMENT ADAPTER. It derives a deterministic id from the
    /// local user id and is refused by startup validation outside Development.
    /// </summary>
    public string Mode { get; set; } = UnconfiguredMode;
    public const string UnconfiguredMode = "Unconfigured";
    public const string DevelopmentMode = "Development";
}

/// <summary>Production default until the real Loodoi identity client exists: resolves nothing.</summary>
public sealed class UnconfiguredLoodoiIdentityProvider : ILoodoiIdentityProvider
{
    public bool IsDevelopmentAdapter => false;
    public Task<string?> ResolveAccountIdAsync(Guid authenticatedUserId, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// DEVELOPMENT ADAPTER — NOT A REAL IDENTITY SERVICE. It returns <c>dev_acc_{userId}</c>,
/// which is stable for the local user id and satisfies the contract shape so that
/// checkout and webhook flows can be exercised locally and in tests.
/// </summary>
public sealed class DevelopmentLoodoiIdentityProvider : ILoodoiIdentityProvider
{
    public bool IsDevelopmentAdapter => true;
    public Task<string?> ResolveAccountIdAsync(Guid authenticatedUserId, CancellationToken ct) =>
        Task.FromResult<string?>(authenticatedUserId == Guid.Empty ? null : $"dev_acc_{authenticatedUserId:N}");
}
