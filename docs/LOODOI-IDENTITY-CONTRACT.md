# Loodoi identity contract (P0 A3)

**Status: ADAPTER ONLY.** The central Loodoi identity service is **not** part of this
repository and was not available during development. SEO Loodoi codes against the
interface below. The real client still has to be written once the central API exists.

## Why
Billing webhooks and checkout must refer to a tenant by an identifier that the billing
authority also knows and that never changes. E-mail addresses change, and so do
frontend state and locally generated ids. None of them is acceptable.

## Interface
`SeoLoodoi.Application.Billing.ILoodoiIdentityProvider`

```csharp
Task<string?> ResolveAccountIdAsync(Guid authenticatedUserId, CancellationToken ct);
```

| Rule | Requirement |
|---|---|
| Input | The server-side authenticated SEO Loodoi user id (from the validated bearer token). Never e-mail, never a browser-supplied value. |
| Output | An opaque account id, 3–128 characters from `[A-Za-z0-9_\-:.]`. |
| Stability | The same value across plan changes, renewals, cancellations, expirations, failed payments, reactivations, and profile or e-mail changes. |
| Failure | Return `null` (or throw) when the id cannot be resolved. SEO Loodoi then refuses checkout (HTTP 503). It never invents an id. |
| Uniqueness | One Loodoi account ↔ one SEO Loodoi workspace (enforced by a unique filtered index on `TenantEntitlements.LoodoiAccountId`). |

## Lifecycle inside SEO Loodoi
1. A new tenant has `LoodoiAccountId = null`.
2. `POST /api/seo/billing/checkout` (owner only) calls the provider and binds the returned
   id with `TenantEntitlement.LinkLoodoiAccount`. Binding is idempotent for the same id.
   A different id → 409 (`LoodoiIdentityConflictException`). If the id is already bound to
   another workspace → 409. The id is passed to Loodoi checkout as `account=`.
3. `POST /api/billing/webhook` resolves the tenant **only** by an already-bound
   `LoodoiAccountId`. Unknown account → rejected (401). The webhook never creates tenants and
   never binds or rebinds an identity. A payload `userId`, if present, must match the bound tenant.

## Implementations shipped
| Class | Use | Selected by |
|---|---|---|
| `UnconfiguredLoodoiIdentityProvider` | Production default. Resolves nothing, so checkout returns 503. | default |
| `DevelopmentLoodoiIdentityProvider` | **DEVELOPMENT ADAPTER.** Returns `dev_acc_{userId}`. | `LoodoiIdentity:Mode=Development` (refused at startup outside Development) |

## What the real client must provide (open item)
- An authenticated server-to-server call (mTLS or signed service token) that maps the local user id to the central account id.
- Timeouts and retries, and it must not log the account id together with personal data.
- A migration path for existing tenants. Synthetic `loodoi_acc_{userId}` ids created by earlier builds are cleared by migration `20260923150000`, so they re-bind at next checkout.
