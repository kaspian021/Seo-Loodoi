# Production release runbook

Verified implementation commits: `fddbbf7` (container artifacts, compose contract, CI image gate), `865b976` + `77afbdf` (fail-fast production configuration and durable Data Protection configuration), `20e32cf` + `d096927` (duplicate-host 409 contract). CI run https://github.com/kaspian021/Seo-Loodoi/actions/runs/35599916806 is green: **493 backend passed, 0 failed**, migration/model checks, 47 frontend tests, production builds for both containers and four browser E2E journeys.

This runbook defines the deployment contract shipped by the production-release closure work. A green build proves that the artifacts can be built; it does **not** replace load testing, a backup/restore drill, live provider acceptance, or operational approval.

## Artifacts

- `src/SeoLoodoi.Api/Dockerfile`: .NET 10 API, published in Release with warnings as errors, non-root runtime and database-aware readiness check.
- `src/SeoLoodoi.Web/Dockerfile`: immutable Vite build served by unprivileged nginx.
- `deploy/nginx.conf`: same-origin UI/API routing. `/api/*` and `/health/*` are proxied to the private API; all other unknown paths use the SPA fallback.
- `compose.production.yml`: PostgreSQL 16, API and web reference topology with persistent database and Data Protection key volumes.

The compose file is a single-host reference deployment, not an HA architecture. In a managed environment, use managed PostgreSQL, external secret storage, durable shared Data Protection storage, TLS at the ingress/load balancer, centralized logs and independently scaled workers as appropriate.

## Required configuration

Copy `.env.example` to a secret-managed deployment environment and replace every example value. At minimum:

- `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`
- `PUBLIC_API_URL` and `PUBLIC_WEB_URL` (public HTTPS origins; no trailing slash)
- production image tags, when images are pulled rather than built locally

Provider credentials remain optional. If email confirmation is enabled, SMTP must be configured and verified first. Never commit `.env`, database passwords, provider keys, SMTP credentials or exported Data Protection keys.

## Database migration policy

`APPLY_MIGRATIONS` defaults to `false`. Generate and review the idempotent migration script in CI, back up the database, then run migration as an explicit release step. For a controlled single-instance rollout it may be set to `true` for one API start and returned to `false` after success. Do not let multiple replicas race migrations.

## Build and start

```bash
cp .env.example .env
# edit .env using a secret source; do not commit it
docker compose --env-file .env -f compose.production.yml config
docker compose --env-file .env -f compose.production.yml build
docker compose --env-file .env -f compose.production.yml up -d
```

The public ingress should terminate HTTPS and route to the web container. PostgreSQL is on an internal network and is not published to the host.

## Release verification

Run these checks after deployment, from outside the cluster where applicable:

1. `GET /healthz` returns 200 from the web artifact.
2. `GET /health/ready` returns 200 through the proxy and confirms database connectivity.
3. Register/login (or login with a release-test account), create a project on a unique public host, start a crawl, wait for deterministic analysis, and verify issues/score.
4. Restart the API container and confirm existing sessions remain usable (persistent Data Protection keys).
5. Confirm structured logs reach the production log sink without secrets or bearer tokens.
6. Exercise alert email/webhook delivery only when those providers are configured.

## Rollback and recovery

- Keep the previous immutable API and web image tags; rollback both together.
- Application rollback does not automatically reverse schema migrations. Use backward-compatible migrations and a reviewed database recovery procedure.
- Back up PostgreSQL and the Data Protection key volume before first production traffic and before schema changes.
- A release is not approved until a restore into an isolated database has been executed and documented by the operator.

## Known release blockers not closed by containerization

- No measured production-scale load/stress acceptance.
- No documented successful backup/restore drill in a target environment.
- Live Google, backlink, SERP and AI-provider credentials have not been accepted in CI.
- JavaScript rendering and measured Core Web Vitals are not implemented.
- Nine fallback UI locales still need human translation review.
Duplicate project hosts are no longer a blocker: the API pre-checks the owner/normalized-host contract and also translates a racing database unique violation to HTTP 409. The PostgreSQL unique index remains authoritative.

Do not label a deployment generally available until the applicable blockers are accepted or removed from scope by the release owner.
