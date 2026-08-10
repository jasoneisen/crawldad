using './main.bicep'

// Staging parameters. Non-secret values are literals here; the two secrets are read from the environment at compile
// time (the deploy workflow exports them from GitHub secrets). The readEnvironmentVariable defaults are placeholders
// used ONLY for local `az bicep build-params` validation — a real deploy always sets the env vars, and they are never
// committed values.

param location = 'eastus2'
param regionCode = 'eus2'
param envToken = 'stg'
param aspNetCoreEnvironment = 'Staging'

// The deploy workflow sets SERVE_IMAGE to the freshly-built digest; unset (first-run infra bootstrap) falls back to
// the public placeholder so the ACR can be created before the real image exists.
param serveImage = readEnvironmentVariable('SERVE_IMAGE', 'mcr.microsoft.com/k8se/quickstart:latest')

// Postgres (Burstable B1ms, 32 GiB, PG16 — the POC floor).
param pgAdminLogin = 'crawldadadmin'
param pgDatabaseName = 'crawldad'
param pgMaxPoolSize = 10
param pgAdminPassword = readEnvironmentVariable('PG_ADMIN_PASSWORD', 'PLACEHOLDER-local-build-only-not-a-secret')

// Placeholder tenant so staging is callable beyond /health (the app boots with zero tenants, but nothing could
// authenticate). The API key is generated at bootstrap (>=16 chars) and stored as a KV secret.
param tenantId = 'staging'
param tenantActor = 'staging-operator'
param tenantApiKey = readEnvironmentVariable('STAGING_TENANT_API_KEY', 'PLACEHOLDER-local-build-only-not-a-secret')

param storageContainer = 'crawldad-blobs'

// Scale-to-zero for cost (ARCHITECTURE.md B.3 trade-off documented in the PR). 0.5 vCPU / 1 GiB is a valid
// Consumption combo and stays inside the free grant.
param minReplicas = 0
param maxReplicas = 1
param appCpu = '0.5'
param appMemory = '1Gi'
