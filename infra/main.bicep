// Crawldad — Azure staging stack (subscription-scoped).
//
// Shape: one .NET 10 Wolverine.HTTP + Marten API on Azure Container Apps (Consumption, scale-to-zero), backed by a
// PUBLIC-endpoint Postgres Flexible Server (Burstable B1ms) firewalled to Azure services, an LRS blob account, and a
// Key Vault holding every secret pulled passwordless via a user-assigned identity. It is the POC floor from
// ARCHITECTURE.md B.3: the prod-only deltas (Front Door, NAT Gateway, VNet/private access, zone redundancy, HA,
// min-replicas>=1, a Dedicated workload profile) are deliberately absent and deferred.
//
//   ACR ──image (managed-identity pull)──► ACA environment
//                                           ├─ App  ca-crawldad-stg   (HTTP ingress 8080, scale-to-zero)
//                                           └─ Job  caj-crawldad-stg-dbapply  (runs `db-apply` on demand)
//   Postgres Flexible Server (public + firewalled to Azure services) + database
//   Storage account + blob container (downloads / screenshots, tenant-partitioned)
//   Key Vault + user-assigned identity → ACR pull + KV secret refs (passwordless)
//   Log Analytics
//
// This template hardcodes NO secret. The two secure params (pgAdminPassword, tenantApiKey) are generated at bootstrap
// and supplied at deploy time from GitHub secrets (the .bicepparam reads them via readEnvironmentVariable). The
// connection-string secrets are composed inside the keyvault module so no key/password is ever a deployment output.
//
// Validate locally:  az bicep build -f infra/main.bicep
// Deploy:            az deployment sub create -l <loc> -f infra/main.bicep -p infra/main.staging.bicepparam -p serveImage=<digest>

targetScope = 'subscription'

@description('Azure region for all resources. B1ms Postgres + Consumption Container Apps are available in centralus.')
param location string = 'centralus'

@description('Short region code used in the resource-group name (CAF). centralus → cus.')
param regionCode string = 'cus'

@description('Environment token in resource names + tags. Staging is the only live target today; prod is parameterized but inert.')
@allowed([ 'stg', 'prod' ])
param envToken string = 'stg'

@description('ASPNETCORE_ENVIRONMENT for the app (anything but Development ⇒ the prod exception/HSTS branch + no boot-time schema apply).')
param aspNetCoreEnvironment string = 'Staging'

@description('Container image for the app + db-apply job. Defaults to a public placeholder so the FIRST deploy can create the ACR; the workflow then builds the real image and redeploys pinned by digest.')
param serveImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

// ── Postgres ──────────────────────────────────────────────────────────────────────
@description('PostgreSQL administrator login.')
param pgAdminLogin string = 'crawldadadmin'

@secure()
@description('PostgreSQL administrator password (generated at bootstrap, supplied from a GitHub secret — never committed).')
param pgAdminPassword string

@description('Application database name (Marten creates the "crawldad" schema inside it).')
param pgDatabaseName string = 'crawldad'

@description('Npgsql Maximum Pool Size — capped well under the B1ms ~35-connection ceiling.')
param pgMaxPoolSize int = 10

// ── Placeholder tenant ──────────────────────────────────────────────────────────────
@description('Placeholder staging tenant id (the billing/partition subject; must not contain ":").')
param tenantId string = 'staging'

@description('Placeholder staging tenant actor identity (stamped on mutation events).')
param tenantActor string = 'staging-operator'

@secure()
@description('Placeholder staging tenant API key (generated at bootstrap >=16 chars, supplied from a GitHub secret — never committed).')
param tenantApiKey string

// ── Storage ─────────────────────────────────────────────────────────────────────────
@description('Blob container all tenants share (partitioned by a {tenant}/ prefix).')
param storageContainer string = 'crawldad-blobs'

// ── Scale / sizing ────────────────────────────────────────────────────────────────────
@description('Min replicas. Staging defaults to 0 (scale-to-zero) for cost; see the scale-to-zero trade-off in ARCHITECTURE.md B.3.')
@minValue(0)
param minReplicas int = 0

@description('Max replicas (staging stays small — 1–2 concurrent runs per the POC floor).')
@minValue(1)
param maxReplicas int = 1

@description('vCPU per replica (a valid Consumption combo with memory).')
param appCpu string = '0.5'

@description('Memory per replica (a valid Consumption combo with cpu).')
param appMemory string = '1Gi'

// ── Naming (CAF) — single source of truth ────────────────────────────────────────────
var rgName = 'rg-crawldad-${envToken}-${regionCode}'
// Deterministic (subscription + rg name) so names stay STABLE across redeploys; 6 chars keeps the globally-unique
// names within their length caps.
var uniq = substring(uniqueString(subscription().subscriptionId, rgName), 0, 6)

var names = {
  logAnalytics: 'log-crawldad-${envToken}'
  acr: 'crcrawldad${envToken}${uniq}'
  appIdentity: 'id-crawldad-${envToken}-app'
  postgres: 'psql-crawldad-${envToken}-${uniq}'
  storage: 'stcrawldad${envToken}${uniq}'
  keyVault: take('kv-crawldad-${envToken}-${uniq}', 24)
  containerEnv: 'cae-crawldad-${envToken}'
  app: 'ca-crawldad-${envToken}'
  dbApplyJob: 'caj-crawldad-${envToken}-dbapply'
}

var tags = {
  workload: 'crawldad'
  env: envToken
  managedBy: 'bicep'
}

// ── Resource group ────────────────────────────────────────────────────────────────────
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

// ── Modules ───────────────────────────────────────────────────────────────────────────
module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: {
    name: names.logAnalytics
    location: location
    tags: tags
  }
}

module identity 'modules/identity.bicep' = {
  scope: rg
  name: 'app-identity'
  params: {
    name: names.appIdentity
    location: location
    tags: tags
  }
}

module registry 'modules/registry.bicep' = {
  scope: rg
  name: 'registry'
  params: {
    name: names.acr
    location: location
    tags: tags
    appIdentityPrincipalId: identity.outputs.principalId
  }
}

module postgres 'modules/postgres.bicep' = {
  scope: rg
  name: 'postgres'
  params: {
    name: names.postgres
    location: location
    tags: tags
    administratorLogin: pgAdminLogin
    administratorPassword: pgAdminPassword
    databaseName: pgDatabaseName
  }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  params: {
    name: names.storage
    location: location
    tags: tags
    containerName: storageContainer
  }
}

module keyvault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyvault'
  params: {
    name: names.keyVault
    location: location
    tags: tags
    appIdentityPrincipalId: identity.outputs.principalId
    pgFqdn: postgres.outputs.fqdn
    pgAdminLogin: pgAdminLogin
    pgAdminPassword: pgAdminPassword
    pgDatabaseName: pgDatabaseName
    pgMaxPoolSize: pgMaxPoolSize
    storageAccountName: storage.outputs.name
    tenantApiKey: tenantApiKey
  }
}

module app 'modules/app.bicep' = {
  scope: rg
  name: 'app'
  params: {
    location: location
    tags: tags
    environmentName: names.containerEnv
    appName: names.app
    jobName: names.dbApplyJob
    logAnalyticsName: monitoring.outputs.name
    appIdentityId: identity.outputs.id
    image: serveImage
    acrLoginServer: registry.outputs.loginServer
    keyVaultUri: keyvault.outputs.vaultUri
    martenSecretName: keyvault.outputs.martenSecretName
    blobSecretName: keyvault.outputs.blobSecretName
    tenantApiKeySecretName: keyvault.outputs.tenantKeySecretName
    aspNetCoreEnvironment: aspNetCoreEnvironment
    storageContainer: storageContainer
    tenantId: tenantId
    tenantActor: tenantActor
    minReplicas: minReplicas
    maxReplicas: maxReplicas
    cpu: appCpu
    memory: appMemory
  }
}

// ── Outputs (non-secret; consumed by the deploy workflow) ───────────────────────────────
output resourceGroupName string = rg.name
output acrName string = registry.outputs.name
output acrLoginServer string = registry.outputs.loginServer
output keyVaultName string = keyvault.outputs.name
output appName string = app.outputs.appName
output appFqdn string = app.outputs.appFqdn
output dbApplyJobName string = app.outputs.dbApplyJobName
output postgresFqdn string = postgres.outputs.fqdn
output appIdentityClientId string = identity.outputs.clientId
