using './main.bicep'

// PROD PARAMETERIZATION ONLY — this path is present but INERT. The deploy workflow defaults to staging and guards the
// prod path off (no prod subscription / federated credential is bootstrapped yet). This file exists so the prod shape
// is reviewable and ready; it is NOT deployed by the current pipeline.
//
// Prod-only hardening from ARCHITECTURE.md B.2 is DEFERRED and intentionally NOT expressed here yet: Front Door +
// WAF, NAT Gateway (stable egress IP), VNet + private Postgres/KV/Blob endpoints, zone redundancy + Postgres HA, a
// Dedicated workload profile, and the D-series SKUs. Those land when prod is bootstrapped (a follow-up issue). What
// this file changes today vs staging: the env token/name, the Production ASP.NET environment, and a min-1 replica so
// prod runs the production durability shape (no scale-to-zero trap — see B.3) rather than the cost floor.

param location = 'centralus'
param regionCode = 'cus'
param envToken = 'prod'
param aspNetCoreEnvironment = 'Production'

param serveImage = readEnvironmentVariable('SERVE_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')
param portalImage = readEnvironmentVariable('SERVE_PORTAL_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')

param pgAdminLogin = 'crawldadadmin'
param pgDatabaseName = 'crawldad'
param pgMaxPoolSize = 10
param pgAdminPassword = readEnvironmentVariable('PROD_PG_ADMIN_PASSWORD', 'PLACEHOLDER-local-build-only-not-a-secret')

param tenantId = 'crawldad'
param tenantActor = 'crawldad-operator'
param tenantApiKey = readEnvironmentVariable('PROD_TENANT_API_KEY', 'PLACEHOLDER-local-build-only-not-a-secret')

// Portal sign-in email (issue #119): Postmark is wired only when the PROD_PORTAL_POSTMARK_SERVER_TOKEN environment
// secret is set (empty ⇒ no provider, fail-closed, deploy still green). The from-address defaults to the docs domain —
// CHANGE it once the production sending domain is finalized and verified in Postmark. Stream keeps 'outbound' (main.bicep).
param postmarkServerToken = readEnvironmentVariable('PROD_PORTAL_POSTMARK_SERVER_TOKEN', '')
param portalEmailFromAddress = 'noreply@crawldad.dev'

// Console auth (issue #119 PR2): the API's ConsolePrincipal scheme audience/issuer, set ONCE by the operator after
// running docs/CONSOLE_AUTH_RUNBOOK.md (App Registration + Console.Access AppRole — Microsoft Graph, not ARM). Empty
// (default) ⇒ inert, deploy stays green. Non-secret; read from the environment so this file needs no edit.
param consoleAuthTenantId = readEnvironmentVariable('PROD_CONSOLE_AUTH_TENANT_ID', '')
param consoleAuthAudience = readEnvironmentVariable('PROD_CONSOLE_AUTH_AUDIENCE', '')

param storageContainer = 'crawldad-blobs'

// Production durability shape: keep at least one replica warm (Wolverine queue polling, RunDeadline scheduling,
// startup recovery, SSE all require a running replica — see B.3). The prod-scale SKU/HA/networking deltas above are
// the follow-up; this keeps prod correct-by-default even at this minimal parameterization.
param minReplicas = 1
param maxReplicas = 3
param appCpu = '1'
param appMemory = '2Gi'
