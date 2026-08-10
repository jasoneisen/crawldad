// OffMarket — Azure prod stack (resource-group-scoped), deployed into rg-offmarket-prod-cus.
//
// Shape: one always-on Blazor-Server + Marten/Wolverine web app on Azure Container Apps, fronted by
// a PRIVATE Postgres flexible server (Marten's event + doc store) and Azure Blob storage (images +
// data-protection key ring), with Key Vault holding every secret, pulled passwordless via a
// user-assigned identity.
//
//   ACR ──image──► ACA environment
//                   └─ App  ca-offmarket-prod-cus   (HTTP ingress 8080, min-replicas 1)
//   Azure Database for PostgreSQL Flexible Server (PRIVATE access, VNet-integrated)
//   Storage account + blob containers (dataprotection / images / originals / contracts)
//   Key Vault + user-assigned identity  →  ACR pull + KV secret refs + blob data (passwordless)
//   VNet (snet-app delegated to ACA, snet-pg delegated to Postgres) + private DNS zone
//   Log Analytics + Application Insights
//
// Copies the LeadGen idiom: KV secret refs wired to the UAMI (keyVaultUrl + identity), the
// empty-secret coalesce (empty(x)?'unset':x — ACA cannot fetch an EMPTY KV secret), role-assignment
// guid() names, take()-capped names within their limits.
//
// AUTHOR-ONLY at this migration phase — do NOT deploy. Real secret VALUES are seeded in Phase 4.2
// (never here); this template hardcodes NO secret. Validate with:
//   az bicep build -f infra/main.bicep
//
// The CPU/memory below are a STARTING ESTIMATE (2 vCPU / 4 GiB) for Magick.NET — to be empirically
// validated by the image-upload load test, runbook item 4.4, before locking prod.

targetScope = 'resourceGroup'

@description('Resource location. Defaults to the resource group location (centralus).')
param location string = resourceGroup().location

@description('Deployment environment, surfaced as the env tag / ASPNETCORE_ENVIRONMENT.')
@allowed([
  'Production'
  'Development'
  'Test'
  'Stage'
])
param environmentName string = 'Production'

// ── Postgres credentials (secure params — never hardcoded) ──────────────────────
@description('PostgreSQL administrator login.')
param pgAdminLogin string = 'offmarketadmin'

@description('PostgreSQL administrator password. Composed into the Marten connection string stored in Key Vault; never echoed.')
@secure()
param pgAdminPassword string

// ── Secrets (secure params) ──────────────────────────────────────────────────────
// REQUIRED secrets default to '' only so `az bicep build` and a plan-only what-if type-check; the
// deploy workflow's pre-flight mandates each non-empty, and their KV values are stored RAW (no
// 'unset' coalesce), so an empty value fails the deploy (fail-closed) instead of writing a placeholder.
@description('Postmark server API token. Seeded in Phase 4.2. Stored RAW in Key Vault — empty fails the deploy (the workflow pre-flight mandates it non-empty).')
@secure()
param postmarkApiKey string = ''
@description('Cloudflare Turnstile secret key. Seeded in Phase 4.2. Stored RAW in Key Vault — empty fails the deploy (workflow pre-flight mandates it non-empty).')
@secure()
param turnstileSecretKey string = ''
@description('n8n basic-auth password. Seeded in Phase 4.2. Stored RAW in Key Vault — empty fails the deploy (workflow pre-flight mandates it non-empty).')
@secure()
param n8nPassword string = ''
@description('Login-code HMAC key (Security:LoginCodeKey). Seeded in Phase 4.2. Stored RAW in Key Vault — empty fails the deploy; production also fails to boot on a blank value (ProductionSettingsGuards), so the placeholder must never be injected.')
@secure()
param loginCodeKey string = ''
@description('Sentry DSN (Sentry:Dsn). PLAIN env, NOT a Key Vault secret-ref: a DSN is a client-side key (semi-public, safe in env/config), and ACA cannot surface an EMPTY KV secret. Mirrors the LeadGen idiom. Defaults empty — Sentry treats an empty DSN as its disable sentinel, so the SDK self-disables (sends nothing) until a real DSN is set.')
param sentryDsn string = ''
@description('Application Insights connection string (APPLICATIONINSIGHTS_CONNECTION_STRING). Auto-derived from the App Insights resource below; the param is an override seam (defaults empty → use the created resource).')
@secure()
param appInsightsConnectionString string = ''

// ── Non-secret app settings ─────────────────────────────────────────────────────
@description('Postmark weekly-digest template id (Postmark:WeeklyTemplateId).')
param postmarkWeeklyTemplateId int = 40988906

@description('Client IPs/CIDRs allowed to call the Postmark delivery webhook (Postmark:AllowedWebhookIpAddresses). Fail-closed: empty denies every caller. Seed the Postmark/Cloudflare egress IPs in Phase 4.2.')
param postmarkAllowedWebhookIps array = []

@description('Mailbox the contact form delivers to (Contact:Recipient).')
param contactRecipient string = 'deals@offmarket.deals'

@description('Mailbox new-deal submission notices deliver to (Contact:DealSubmissionRecipient).')
param dealSubmissionRecipient string = 'deals@offmarket.deals'

@description('n8n basic-auth username (N8n:Username).')
param n8nUsername string = 'offmarket.deals'

@description('n8n deal-submitted webhook URL (N8n:DealSubmitted). Blank disables the POST; the real …/webhook/474612cc-… URL is set in Phase 4.2.')
param n8nDealSubmitted string = ''

@description('Cloudflare Turnstile site key (Turnstile:SiteKey — public, not a secret). Set the real value in Phase 4.2.')
param turnstileSiteKey string = ''

@description('Cookie domain for the auth/antiforgery cookies (Security:CookieDomain).')
param securityCookieDomain string = 'offmarket.deals'

@description('Matomo site id (Matomo:SiteId — public analytics id, not a secret). Defaults to the prod idSite 1. The MatomoTracker component renders ONLY when BOTH Matomo:SiteId and Matomo:Url are non-empty (a DSN-style config gate), so the base appsettings.json leaves it blank and dev/test/E2E emit no tracker (stops the Playwright suite firing pageviews + conversion goals against prod idSite 1); prod carries it here.')
param matomoSiteId string = '1'

@description('Matomo tracker base URL (Matomo:Url — public, not a secret). Defaults to the prod Matomo Cloud instance. The tracking endpoint is <url>matomo.php and the JS loads from cdn.matomo.cloud/<host>/matomo.js (host parsed from this URL). Blank (with SiteId) disables the tracker; see matomoSiteId.')
param matomoUrl string = 'https://offmarketdeals.matomo.cloud/'

// ── Container image + sizing ────────────────────────────────────────────────────
@description('Container image for the app. Defaults to the public MCR quickstart placeholder so resources create cleanly on the FIRST deploy; the deploy workflow then builds to ACR and rolls the real image in by digest.')
param serveImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

// STARTING ESTIMATE for Magick.NET: ~2 vCPU / 4 GiB — VALIDATED by the image-upload load test
// (runbook item 4.4) before prod is locked. Do not under-size or Magick.NET OOMs on large images
// (peak RAM is driven by image pixel dimensions, per single conversion). Kept as params so 4.4 can
// tune them without a code change.
@description('vCPU for the app container. Starting estimate 2 — validated by runbook item 4.4 load test.')
param appCpu string = '2'
@description('Memory for the app container. Starting estimate 4Gi — validated by runbook item 4.4 load test.')
param appMemory string = '4Gi'

@description('Min replicas for the always-on app. Prod keeps 1 (webhook availability + Wolverine durability + the single-node daemon assumption until SCALE-OUT blockers are fixed).')
@minValue(1)
@maxValue(1)
param minReplicas int = 1

@description('Whether to (re)assign the app identity its AcrPull / Key Vault Secrets User / Storage Blob Data Contributor roles. DEFAULTS true. The FIRST provision MUST be run by a privileged (Owner) principal with -p assignManagedIdentityRoles=true so these role assignments are created (roleAssignments/write). Every STEADY-STATE CI redeploy passes assignManagedIdentityRoles=false: the CI UAMI holds only Contributor (no roleAssignments/write), the roles already exist and persist, and skipping them keeps the deploy least-privilege.')
param assignManagedIdentityRoles bool = true

@description('Apex custom domain bound to the app ingress. MUST be declared in this template: ARM incremental deploys are resource-level, not property-level, so a template without customDomains STRIPS a hand-added binding — that took prod down behind Cloudflare (525 on every request) on 2026-07-11, and the deploy health gate cannot catch it because it probes the raw ACA FQDN. Empty string = no custom domain (fresh-environment bring-up, before the managed certificate exists).')
param customDomainName string = 'offmarket.deals'

@description('Name of the EXISTING managed certificate on the environment for customDomainName. Created out-of-band by `az containerapp hostname bind` (managed certs require live domain-control validation at issue time, so this template references rather than creates it; the name is Azure-generated). Ignored when customDomainName is empty.')
param managedCertificateName string = 'mc-cae-offmarket--offmarket-deals-5459'

// ── Naming (CAF; region abbrev cus) ─────────────────────────────────────────────
var suffix = uniqueString(resourceGroup().id)
var shortId = substring(suffix, 0, 6)
// KV name must be ≤24 chars — the shortId suffix keeps it unique and within cap.
var kvName = take('kv-offmarket-prod-${shortId}', 24)
var acrName = 'croffmarketprodcus'
var storageName = 'stoffmarketprodcus'
var pgName = 'psql-offmarket-prod-cus'
var vnetName = 'vnet-offmarket-prod-cus'
var lawName = 'log-offmarket-prod-cus'
var appInsightsName = 'appi-offmarket-prod-cus'
var caeName = 'cae-offmarket-prod-cus'
var appName = 'ca-offmarket-prod-cus'
var appIdentityName = 'id-offmarket-prod-cus'

var tags = {
  property: 'offmarket'
  env: 'prod'
}

// LOAD-BEARING VALUE — this is the database production actually serves from. Every deploy rewrites
// the marten-connection-string KV secret from this var, so a stale name here silently repoints the
// app at the wrong database on the NEXT deploy (2026-07-11 incident: the final cutover loaded prod
// data into offmarket_prod out-of-band — the migrate job's "DROP+CREATE fresh db offmarket_prod" —
// and repointed the app manually; the next routine deploy reverted the secret to offmarket_marten
// and served an EMPTY feed with a green health gate). offmarket_marten was the dry-run/bring-up db
// and is retired; it stays on the server unmanaged until someone deliberately drops it.
var pgDatabaseName = 'offmarket_prod'
var privateDnsZoneName = 'privatelink.postgres.database.azure.com'
// Private-link DNS zones for the KV + Storage(blob) private endpoints (finding #7): the app resolves
// each service's public FQDN to a VNet-private IP via these zones, so KV/blob stay reachable with
// public network access DISABLED.
var kvPrivateDnsZoneName = 'privatelink.vaultcore.azure.net'
var blobPrivateDnsZoneName = 'privatelink.blob.${az.environment().suffixes.storage}'

var blobContainers = [
  'dataprotection'
  'images'
  'originals'
  'contracts'
]

// Built-in role ids.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// Marten connection string over the PRIVATE FQDN. Azure Postgres requires TLS; VerifyFull does REAL
// CA + hostname validation (no MITM) — the correct mode over the private network, where the server
// presents a cert for its <name>.postgres.database.azure.com FQDN (which the private DNS zone resolves
// to). Pool capped (Burstable max_connections is small).
var martenConnString = 'Host=${pg.properties.fullyQualifiedDomainName};Port=5432;Database=${pgDatabaseName};Username=${pgAdminLogin};Password=${pgAdminPassword};SSL Mode=VerifyFull;Maximum Pool Size=20;Minimum Pool Size=1;Connection Idle Lifetime=60'

// Blob connection string (used by the image store + data-protection key ring). Passwordless is the
// long-term goal (the UAMI holds Storage Blob Data Contributor), but the app reads AzureBlob:ConnectionString,
// so compose it from the account key here; migrate to DefaultAzureCredential + a URI later.
var blobConnString = 'DefaultEndpointsProtocol=https;AccountName=${storageName};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${az.environment().suffixes.storage}'

// App Insights connection string: the override param when set, else the created resource's own.
var effectiveAppInsightsConn = empty(appInsightsConnectionString) ? appInsights.properties.ConnectionString : appInsightsConnectionString

// ── KV-backed secret refs (mirror LeadGen: keyVaultUrl + identity) ───────────────
var appSecrets = [
  { name: 'marten-connection-string', keyVaultUrl: '${kv.properties.vaultUri}secrets/marten-connection-string', identity: appIdentity.id }
  { name: 'blob-connectionstring', keyVaultUrl: '${kv.properties.vaultUri}secrets/blob-connectionstring', identity: appIdentity.id }
  { name: 'postmark-apikey', keyVaultUrl: '${kv.properties.vaultUri}secrets/postmark-apikey', identity: appIdentity.id }
  { name: 'turnstile-secretkey', keyVaultUrl: '${kv.properties.vaultUri}secrets/turnstile-secretkey', identity: appIdentity.id }
  { name: 'n8n-password', keyVaultUrl: '${kv.properties.vaultUri}secrets/n8n-password', identity: appIdentity.id }
  { name: 'login-code-key', keyVaultUrl: '${kv.properties.vaultUri}secrets/login-code-key', identity: appIdentity.id }
  { name: 'appinsights-connectionstring', keyVaultUrl: '${kv.properties.vaultUri}secrets/appinsights-connectionstring', identity: appIdentity.id }
]

// ── Postgres maintenance window — ONE definition, two consumers ──────────────────
// Azure patches the server by restarting it, roughly monthly ("normally, the interval between
// successful scheduled maintenance events for a server is at least 30 days"). With highAvailability
// Disabled (see the pg resource below) there is no standby to fail over to, so every patch is a hard
// ~90s outage: Postgres emits 57P03 ("the database system is shutting down"), then refuses connections
// until it is back.
//
// Leaving customWindow Disabled lets Azure pick the moment — on 2026-07-28 it chose 10:10 UTC and
// produced 277 Sentry events across 29 issue groups. Pinning the window does not prevent the outage,
// it makes it PREDICTABLE, which is the precondition for suppressing the resulting noise. A pinned
// (custom) schedule is also patched at least seven days after the system-managed servers in the same
// region, so the rollout reaching us has had a week of exposure elsewhere first.
//
// These three values feed BOTH the server's maintenanceWindow AND the app's Maintenance__* env vars
// (MaintenanceWindowSentryFilter), so a schedule change made HERE moves Azure's restart schedule and
// the app's suppression window together. That holds for changes made through bicep; a one-off
// `az postgres flexible-server update --maintenance-window …` moves the server alone and leaves the
// app suppressing the old hours. See docs/OPERATIONS.md.
//
// NOTE: changing these does not move the NEXT restart. Azure applies updated maintenance settings
// "upon successful completion of the next scheduled maintenance" and does not reprogram a rollout
// that is already scheduled.
//
// startHour is UTC. 07:00 UTC = 03:00 America/New_York (EDT) / 02:00 (EST), which sits inside the
// quiet 00:00-04:00 ET band: 9-24 visits an hour across 30 days of Matomo data, against 421 at the
// 05:00 ET peak. It is NOT the single quietest hour on either metric (01:00 ET had 9 visits to
// 03:00's 12, and 17:00 ET had 41 actions to its 574) — inside that band the choice is arbitrary, and
// 03:00 buys the most margin either side of it. dayOfWeek 0 = Sunday. Azure begins maintenance within
// the hour that follows; the 2h suppression window absorbs a late start plus the restart itself.
var pgMaintenanceDayOfWeekUtc = 0
var pgMaintenanceStartHourUtc = 7
var pgMaintenanceDurationHours = 2

// Non-secret settings are plain env; secret settings reference the KV-backed secrets above. The
// array-valued Postmark:AllowedWebhookIpAddresses is expanded to indexed env vars (…__0, __1, …).
var ipEnv = [for (ip, i) in postmarkAllowedWebhookIps: { name: 'Postmark__AllowedWebhookIpAddresses__${i}', value: ip }]
var appEnv = concat([
  { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName }
  { name: 'ConnectionStrings__Marten', secretRef: 'marten-connection-string' }
  { name: 'AzureBlob__ConnectionString', secretRef: 'blob-connectionstring' }
  { name: 'Postmark__ApiKey', secretRef: 'postmark-apikey' }
  { name: 'Postmark__WeeklyTemplateId', value: string(postmarkWeeklyTemplateId) }
  { name: 'Turnstile__SecretKey', secretRef: 'turnstile-secretkey' }
  { name: 'Turnstile__SiteKey', value: turnstileSiteKey }
  { name: 'N8n__Username', value: n8nUsername }
  { name: 'N8n__Password', secretRef: 'n8n-password' }
  { name: 'N8n__DealSubmitted', value: n8nDealSubmitted }
  { name: 'Contact__Recipient', value: contactRecipient }
  { name: 'Contact__DealSubmissionRecipient', value: dealSubmissionRecipient }
  { name: 'Security__LoginCodeKey', secretRef: 'login-code-key' }
  { name: 'Security__RequireSecureCookie', value: 'true' }
  { name: 'Security__CookieDomain', value: securityCookieDomain }
  // Sentry: plain env (semi-public client key). Empty DSN = SDK self-disables; ACA cannot surface
  // an EMPTY KV secret, so this must NOT be a secretRef. Mirrors the LeadGen idiom.
  { name: 'Sentry__Dsn', value: sentryDsn }
  // Matomo: plain env (public analytics ids). Populated here so the tracker renders in prod; the base
  // appsettings.json leaves these blank so dev/test/E2E render no tracker (the config gate that stops
  // the Playwright suite polluting prod idSite 1). Same plain-env-not-secretRef rationale as Sentry.
  { name: 'Matomo__SiteId', value: matomoSiteId }
  { name: 'Matomo__Url', value: matomoUrl }
  // Maintenance window, shared with the pg resource's maintenanceWindow below. Present ONLY here, so
  // the base appsettings.json leaves it absent and dev/test/E2E suppress nothing (the filter fails
  // closed on missing config). The app logs the window it resolved on every boot, so a value that
  // failed to reach the container is visible in the log stream. See MaintenanceWindowSentryFilter.
  { name: 'Maintenance__DayOfWeekUtc', value: string(pgMaintenanceDayOfWeekUtc) }
  { name: 'Maintenance__StartHourUtc', value: string(pgMaintenanceStartHourUtc) }
  { name: 'Maintenance__DurationHours', value: string(pgMaintenanceDurationHours) }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connectionstring' }
], ipEnv)

var registries = [
  { server: acr.properties.loginServer, identity: appIdentity.id }
]

// ── Networking: VNet + delegated subnets + private DNS zone ─────────────────────
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [ '10.20.0.0/16' ] }
    subnets: [
      {
        // Container Apps environment needs a dedicated /23 delegated to Microsoft.App/environments.
        name: 'snet-app'
        properties: {
          addressPrefix: '10.20.0.0/23'
          delegations: [
            { name: 'aca', properties: { serviceName: 'Microsoft.App/environments' } }
          ]
        }
      }
      {
        // Postgres flexible server (private) requires a subnet delegated to it.
        name: 'snet-pg'
        properties: {
          addressPrefix: '10.20.2.0/24'
          delegations: [
            { name: 'pg', properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' } }
          ]
        }
      }
      {
        // Private endpoints for KV + Storage(blob) (finding #7). A PE subnet must be NON-delegated
        // (unlike snet-app/snet-pg), so it is its own subnet.
        name: 'snet-pe'
        properties: {
          addressPrefix: '10.20.3.0/24'
        }
      }
    ]
  }
}

resource appSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-app'
}
resource pgSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-pg'
}
resource peSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: vnet
  name: 'snet-pe'
}

resource privateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: privateDnsZoneName
  location: 'global'
  tags: tags
}

resource privateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: privateDnsZone
  name: 'link-${vnetName}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}

// KV + Storage(blob) private DNS zones, linked to the VNet (finding #7). The private endpoints below
// register their A records here, so the app resolves the KV/blob FQDN to a VNet-private IP.
resource kvPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: kvPrivateDnsZoneName
  location: 'global'
  tags: tags
}
resource kvPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: kvPrivateDnsZone
  name: 'link-${vnetName}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}
resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: blobPrivateDnsZoneName
  location: 'global'
  tags: tags
}
resource blobPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: blobPrivateDnsZone
  name: 'link-${vnetName}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}

// ── PostgreSQL (Marten store), private access ────────────────────────────────────
resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: pgName
  location: location
  tags: tags
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '16'
    administratorLogin: pgAdminLogin
    administratorLoginPassword: pgAdminPassword
    storage: { storageSizeGB: 32, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    // Pins WHEN Azure may restart this server for patching. Rationale, timezone maths and the shared
    // app-side suppression window all live with the pgMaintenance* vars near appEnv above — change the
    // schedule there, not here, so the server and the app stay in step.
    //
    // startMinute is the one part with no var, and must stay 0: the app-side window has no minute
    // component (Maintenance__StartHourUtc only), so a non-zero start minute is the one value that
    // could not be mirrored into the container app. Adding a fourth var would mean adding a minute to
    // the filter's arithmetic to keep the invariant honest, for a knob nothing needs.
    maintenanceWindow: {
      customWindow: 'Enabled'
      dayOfWeek: pgMaintenanceDayOfWeekUtc
      startHour: pgMaintenanceStartHourUtc
      startMinute: 0
    }
    authConfig: { activeDirectoryAuth: 'Disabled', passwordAuth: 'Enabled' }
    // Private access: no public endpoint; reached only inside the VNet via the private DNS zone.
    network: {
      delegatedSubnetResourceId: pgSubnet.id
      privateDnsZoneArmResourceId: privateDnsZone.id
    }
  }
  dependsOn: [ privateDnsLink ]
}

resource pgDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: pg
  name: pgDatabaseName
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

// ── Storage + blob containers ────────────────────────────────────────────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // Public network access DISABLED (finding #7): blob is reachable ONLY via the private endpoint
    // below (resolved through the blob private DNS zone from inside the VNet).
    publicNetworkAccess: 'Disabled'
  }
}

// Blob private endpoint + DNS zone group (finding #7): gives the storage account a VNet-private IP and
// registers its A record in the blob private DNS zone, so the VNet-integrated app reaches blob with
// public access disabled.
resource blobPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${storageName}-blob'
  location: location
  tags: tags
  properties: {
    subnet: { id: peSubnet.id }
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: [ 'blob' ]
        }
      }
    ]
  }
}
resource blobPrivateDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: blobPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      { name: 'blob', properties: { privateDnsZoneId: blobPrivateDnsZone.id } }
    ]
  }
  dependsOn: [ blobPrivateDnsLink ]
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for name in blobContainers: {
  parent: blobService
  name: name
  properties: { publicAccess: 'None' }
}]

// ── Container registry (Basic, dedicated — preserves the billing boundary) ────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

// ── Key Vault (RBAC) ──────────────────────────────────────────────────────────────
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Purge protection: once on, a soft-deleted vault/secret CANNOT be permanently purged before its
    // retention window elapses — defends the prod secret store against accidental or malicious purge.
    // Irreversible (cannot be turned back off), which is the intended one-way ratchet for prod.
    enablePurgeProtection: true
    // Network lockdown (finding #7): firewall the vault — deny all public network callers EXCEPT
    // trusted Azure services (bypass). RBAC still governs identity. Two access paths remain open:
    //   1. the app's UAMI reaches it PRIVATELY via the kv private endpoint + DNS zone below (private-
    //      endpoint traffic is exempt from networkAcls), and
    //   2. this deployment's secret-VALUE writes (a Key Vault DATA-plane op) reach it via bypass:
    //      AzureServices — which is why publicNetworkAccess is left at its default (Enabled) rather
    //      than 'Disabled'. Setting the hard 'Disabled' master switch would deny the ARM secret-
    //      resource writes IN THIS SAME deploy (they run from the deployment worker's public IP, not
    //      over the PE) and brick the apply. RESIDUAL: the vault keeps a firewalled public endpoint
    //      (deny-all-but-trusted) instead of the stricter publicNetworkAccess:Disabled. Tighten to
    //      'Disabled' only once secret seeding is decoupled from this template (e.g. seeded over the
    //      PE / from inside the VNet), so the deploy no longer writes secret values from a public IP.
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

// KV private endpoint + DNS zone group (finding #7): gives the vault a VNet-private IP and registers
// its A record in the KV private DNS zone, so the VNet-integrated app resolves secret-refs privately
// with public access disabled.
resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${kvName}'
  location: location
  tags: tags
  properties: {
    subnet: { id: peSubnet.id }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: kv.id
          groupIds: [ 'vault' ]
        }
      }
    ]
  }
}
resource kvPrivateDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: kvPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      { name: 'vault', properties: { privateDnsZoneId: kvPrivateDnsZone.id } }
    ]
  }
  dependsOn: [ kvPrivateDnsLink ]
}

// Secrets. Each value comes from a secure param seeded in Phase 4.2. Two regimes:
//   REQUIRED secrets (marten conn / blob conn / postmark / turnstile / n8n / login-code) are stored
//   RAW — no empty(x)?'unset':x coalesce. The deploy workflow's pre-flight already mandates their
//   source values non-empty, and the app fail-fasts on a bogus value (e.g. ProductionSettingsGuards
//   on Security:LoginCodeKey), so injecting the literal 'unset' would only DEFEAT those guards. An
//   empty value must fail the deploy (fail-closed), not silently write a placeholder.
//   OPTIONAL settings (appinsights connection string) keep the coalesce so their secret-ref stays
//   resolvable when genuinely unset (ACA cannot fetch an EMPTY KV secret).
resource martenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'marten-connection-string'
  properties: { value: martenConnString }
}
resource blobSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'blob-connectionstring'
  properties: { value: blobConnString }
}
resource postmarkSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'postmark-apikey'
  properties: { value: postmarkApiKey }
}
resource turnstileSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'turnstile-secretkey'
  properties: { value: turnstileSecretKey }
}
resource n8nSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'n8n-password'
  properties: { value: n8nPassword }
}
resource loginCodeSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'login-code-key'
  properties: { value: loginCodeKey }
}
resource appInsightsSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'appinsights-connectionstring'
  properties: { value: empty(effectiveAppInsightsConn) ? 'unset' : effectiveAppInsightsConn }
}

// ── App-runtime identity + role assignments ───────────────────────────────────────
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: appIdentityName
  location: location
  tags: tags
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignManagedIdentityRoles) {
  name: guid(acr.id, appIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignManagedIdentityRoles) {
  name: guid(kv.id, appIdentity.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignManagedIdentityRoles) {
  name: guid(storage.id, appIdentity.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Log Analytics + Application Insights ──────────────────────────────────────────
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: lawName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

// ── Container Apps environment (VNet-integrated, wired to Log Analytics) ───────────
resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: caeName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: appSubnet.id
      internal: false
    }
    // A workload-profile (v2) environment. snet-app is DELEGATED to Microsoft.App/environments; a v1
    // (Consumption-only, no workloadProfiles) environment requires a NON-delegated subnet and would
    // fail with ManagedEnvironmentSubnetIsDelegated. Declaring the Consumption profile makes this a v2
    // env, for which the delegated subnet is correct. The app then pins workloadProfileName below.
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
  }
}

// The apex's managed certificate — referenced (never created) so the app's customDomains binding
// below survives every redeploy. See the customDomainName param for the outage this prevents.
resource apexCert 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' existing = if (!empty(customDomainName)) {
  parent: cae
  name: managedCertificateName
}

// ── The app: always-on Blazor Server + Marten/Wolverine web host ──────────────────
// Multiple revisions mode for health-gated blue/green traffic shifting (the deploy workflow brings a
// new revision up at 0%, health-gates it, then shifts 100%). External ingress on 8080 (the app's
// Kestrel port), sits behind Cloudflare in prod (Phase 8). minReplicas 1 (single-node until the
// SCALE-OUT / HotCold daemon blocker, runbook item 3.0, is cleared).
resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${appIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: cae.id
    // Pin the environment's Consumption workload profile (the v2 env declared above). Required for a
    // workload-profile environment; without it the app requests the (absent) default profile.
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        traffic: [ { weight: 100, latestRevision: true } ]
        // Resource-level incremental deploys: omitting this strips the binding (see customDomainName).
        customDomains: empty(customDomainName) ? null : [
          {
            name: customDomainName
            bindingType: 'SniEnabled'
            certificateId: apexCert.id
          }
        ]
      }
      registries: registries
      secrets: appSecrets
    }
    template: {
      containers: [
        {
          name: 'offmarket'
          image: serveImage
          // CPU/memory are the runbook item 4.4 starting estimate — validated by the load test.
          resources: { cpu: json(appCpu), memory: appMemory }
          env: appEnv
          probes: [
            { type: 'Liveness', httpGet: { path: '/api/health', port: 8080 }, initialDelaySeconds: 20, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/api/health', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 15 }
          ]
        }
      ]
      // Pinned single replica (single-writer durability + single-node daemon). maxReplicas 1 until
      // the HotCold daemon blocker (runbook item 3.0) is cleared and SCALE-OUT is safe.
      scale: { minReplicas: minReplicas, maxReplicas: 1 }
    }
  }
  // KV/blob private endpoints + their DNS zone groups are dependencies too: the app must not start
  // resolving secret-refs / blob until those private paths exist (public access is disabled).
  dependsOn: [ acrPull, kvSecretsUser, storageBlobContributor, martenSecret, blobSecret, postmarkSecret, turnstileSecret, n8nSecret, loginCodeSecret, appInsightsSecret, pgDb, kvPrivateDnsGroup, blobPrivateDnsGroup ]
}

output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output keyVaultName string = kv.name
output appFqdn string = app.properties.configuration.ingress.fqdn
output appName string = app.name
output postgresFqdn string = pg.properties.fullyQualifiedDomainName
output appIdentityClientId string = appIdentity.properties.clientId
