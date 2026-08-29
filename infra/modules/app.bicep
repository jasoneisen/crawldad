// The Container Apps environment, the Crawldad app, and the db-apply migration job.
//
// Consumption-only environment (no vnetConfiguration, no workloadProfiles) ⇒ scale-to-zero capable and cheap. The app
// gets exactly the env it needs to BOOT and be usable (derived from the host's boot guards): the Marten connection
// (secret), the Azure Blob storage provider (StorageOptionsValidator requires a non-empty connection string +
// container under the `azure` provider), and one placeholder tenant so the API is callable beyond the anonymous
// /health. All three secrets resolve passwordless via the app identity's Key Vault Secrets User grant.
//
// The db-apply job runs the SAME image as `dotnet Crawldad.Api.dll db-apply` to apply the Marten + Wolverine schema
// out-of-band (a normal server start never applies schema in non-Development). It needs only the Marten connection;
// Marten's conjoined multi-tenancy is one shared schema, so a single db-apply covers every tenant.

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Container Apps environment name (CAF: cae-crawldad-<env>).')
param environmentName string

@description('App name (CAF: ca-crawldad-<env>).')
param appName string

@description('db-apply job name (CAF: caj-crawldad-<env>-dbapply).')
param jobName string

@description('Portal app name (CAF: ca-crawldad-<env>-portal).')
param portalAppName string

@description('Log Analytics workspace name (referenced existing for its shared key, kept out of deploy outputs).')
param logAnalyticsName string

@description('App identity resource id (already granted AcrPull + Key Vault Secrets User).')
param appIdentityId string

@description('App identity client id — set as AZURE_CLIENT_ID so DefaultAzureCredential picks THIS user-assigned identity.')
param appIdentityClientId string

@description('Portal identity resource id (issue #119 PR2) — the portal container app runs under THIS least-privilege identity, not the shared app identity. Already granted AcrPull + secret-scoped Secrets User + portal-key Crypto User + portal-container Blob Contributor.')
param portalIdentityId string

@description('Portal identity client id (issue #119 PR2) — set as the portal container AZURE_CLIENT_ID so DefaultAzureCredential picks the portal-owned user-assigned identity for its Data-Protection blob + Key Vault access.')
param portalIdentityClientId string

@description('Container image for the app + job (pinned by digest by the deploy workflow).')
param image string

@description('Container image for the portal (pinned by digest by the deploy workflow; independent of the API image).')
param portalImage string

@description('ACR login server (the app pulls from here with its identity).')
param acrLoginServer string

@description('Key Vault URI (base for the secret-ref URLs).')
param keyVaultUri string

@description('Marten connection-string secret name.')
param martenSecretName string

@description('Blob connection-string secret name.')
param blobSecretName string

@description('Placeholder-tenant API-key secret name.')
param tenantApiKeySecretName string

@description('Beta-tenant id; empty ⇒ no beta tenant is wired.')
param betaTenantId string = ''

@description('Beta-tenant actor identity.')
param betaTenantActor string = ''

@description('Beta-tenant API-key secret name; empty ⇒ no beta tenant is wired.')
param betaTenantApiKeySecretName string = ''

@description('ASPNETCORE_ENVIRONMENT value (anything but Development ⇒ the prod exception/HSTS branch + no boot-time schema apply).')
param aspNetCoreEnvironment string

@description('Blob container name (plain app config).')
param storageContainer string

@description('Absolute URI of the blob the API Data Protection key ring is persisted to (issue #65).')
param keyRingBlobUri string

@description('Key Vault key id that wraps the API Data Protection key ring (versionless).')
param dataProtectionKeyId string

@description('Absolute URI of the blob the PORTAL Data Protection key ring is persisted to (issue #119) — its own container, never the API ring.')
param portalKeyRingBlobUri string

@description('Key Vault key id that wraps the PORTAL Data Protection key ring (versionless).')
param portalDataProtectionKeyId string

@description('Postmark server-token KV secret name for the portal OTP mailer (issue #119); empty ⇒ no email provider is wired on the portal and it stays fail-closed (deploy still green).')
param portalPostmarkTokenSecretName string = ''

@description('Verified From address for portal OTP mail (issue #119). Wired only when portalPostmarkTokenSecretName is set.')
param portalEmailFromAddress string = ''

@description('Postmark message stream for portal OTP mail (issue #119). Wired only when portalPostmarkTokenSecretName is set.')
param portalEmailMessageStream string = ''

@description('Entra directory (tenant) GUID for the API ConsolePrincipal scheme (issue #119 PR2). Empty ⇒ the scheme stays inert and no Crawldad__ConsoleAuth__* env is set.')
param consoleAuthTenantId string = ''

@description('API App-ID-URI audience for the ConsolePrincipal scheme (issue #119 PR2). Empty ⇒ the scheme stays inert.')
param consoleAuthAudience string = ''

@description('AppRole the portal UAMI must carry for the ConsolePrincipal scheme (issue #119 PR2). Wired only when consoleAuthTenantId + consoleAuthAudience are both set.')
param consoleAuthRequiredRole string = 'Console.Access'

@description('Placeholder-tenant id (partition/billing subject; must not contain ":").')
param tenantId string

@description('Placeholder-tenant actor identity.')
param tenantActor string

@description('Min replicas (0 ⇒ scale-to-zero; see ARCHITECTURE.md B.3 for the trade-off).')
param minReplicas int

@description('Max replicas.')
param maxReplicas int

@description('vCPU per replica (Consumption combo, e.g. 0.5).')
param cpu string

@description('Memory per replica (matched Consumption combo, e.g. 1Gi).')
param memory string

// Referenced existing so the workspace shared key is read at deploy time without ever becoming a module output.
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsName
}

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
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
    // No vnetConfiguration + no workloadProfiles ⇒ a Consumption-only environment that scales to zero. VNet + NAT
    // Gateway + a Dedicated workload profile are prod-only deltas (ARCHITECTURE.md B.2/B.3), deferred to keep
    // staging within the ~$25/mo floor.
  }
}

// KV-backed secret refs, resolved passwordless via the app identity (Key Vault Secrets User).
var hasBetaTenant = !empty(betaTenantApiKeySecretName)

// Console auth (issue #119 PR2): both non-empty ⇒ wire the API's Crawldad__ConsoleAuth__* env so the (still inert)
// ConsolePrincipal scheme is registered. An all-or-nothing gate mirroring the host's own boot validator (a half-set
// pair fails boot), so the container never boots half-configured. Empty ⇒ no env, no scheme, deploy stays green.
var hasConsoleAuth = !empty(consoleAuthTenantId) && !empty(consoleAuthAudience)
var appSecrets = concat(
  [
    { name: 'marten-connection-string', keyVaultUrl: '${keyVaultUri}secrets/${martenSecretName}', identity: appIdentityId }
    { name: 'blob-connectionstring', keyVaultUrl: '${keyVaultUri}secrets/${blobSecretName}', identity: appIdentityId }
    { name: 'tenant-apikey', keyVaultUrl: '${keyVaultUri}secrets/${tenantApiKeySecretName}', identity: appIdentityId }
  ],
  hasBetaTenant
    ? [{ name: 'beta-tenant-apikey', keyVaultUrl: '${keyVaultUri}secrets/${betaTenantApiKeySecretName}', identity: appIdentityId }]
    : []
)

var registries = [
  { server: acrLoginServer, identity: appIdentityId }
]

// The minimal valid boot env (derived from HostConfiguration + the ValidateOnStart guards): Marten connection,
// the azure storage provider (+ its connection string and container), and one placeholder tenant (id/actor plain,
// api key by reference). Nothing here selects a browser backend — those are chosen per-run by payload data and do
// not gate boot. The DataProtection pair persists the key ring so registered credentials survive redeploys (issue #65);
// AZURE_CLIENT_ID points DefaultAzureCredential at the app's user-assigned identity for the blob + Key Vault access.
var appEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetCoreEnvironment }
    { name: 'AZURE_CLIENT_ID', value: appIdentityClientId }
    { name: 'ConnectionStrings__marten', secretRef: 'marten-connection-string' }
    { name: 'Crawldad__Storage__Provider', value: 'azure' }
    { name: 'Crawldad__Storage__Azure__ConnectionString', secretRef: 'blob-connectionstring' }
    { name: 'Crawldad__Storage__Azure__Container', value: storageContainer }
    { name: 'Crawldad__DataProtection__KeyRingBlobUri', value: keyRingBlobUri }
    { name: 'Crawldad__DataProtection__KeyVaultKeyId', value: dataProtectionKeyId }
    { name: 'Crawldad__Tenants__0__Id', value: tenantId }
    { name: 'Crawldad__Tenants__0__Actor', value: tenantActor }
    { name: 'Crawldad__Tenants__0__ApiKey', secretRef: 'tenant-apikey' }
  ],
  hasBetaTenant
    ? [
        { name: 'Crawldad__Tenants__1__Id', value: betaTenantId }
        { name: 'Crawldad__Tenants__1__Actor', value: betaTenantActor }
        { name: 'Crawldad__Tenants__1__ApiKey', secretRef: 'beta-tenant-apikey' }
      ]
    : [],
  hasConsoleAuth
    ? [
        // The inert ConsolePrincipal scheme's audience/issuer (issue #119 PR2). Non-secret (a directory GUID + a public
        // App ID URI), so both are plain values — no KV secret ref. Signing keys come from Entra metadata at runtime.
        { name: 'Crawldad__ConsoleAuth__TenantId', value: consoleAuthTenantId }
        { name: 'Crawldad__ConsoleAuth__Audience', value: consoleAuthAudience }
        { name: 'Crawldad__ConsoleAuth__RequiredRole', value: consoleAuthRequiredRole }
      ]
    : []
)

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${appIdentityId}': {} } }
  properties: {
    managedEnvironmentId: cae.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        traffic: [ { weight: 100, latestRevision: true } ]
      }
      registries: registries
      secrets: appSecrets
    }
    template: {
      containers: [
        {
          name: 'crawldad'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          env: appEnv
          // Probe the anonymous /health (a 200 proves Marten + Wolverine + the pipeline composed and started).
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 30, failureThreshold: 3 }
            { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10, failureThreshold: 6 }
          ]
        }
      ]
      scale: { minReplicas: minReplicas, maxReplicas: maxReplicas }
    }
  }
}

// Schema-apply job: the same image + identity + registry, running `dotnet Crawldad.Api.dll db-apply` once on demand.
// It reaches Postgres over the "allow Azure services" firewall rule (it runs inside Azure). The deploy workflow starts
// it and waits for Succeeded BEFORE the health gate, so the schema exists before the app's first (scale-from-zero) boot.
resource dbApplyJob 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${appIdentityId}': {} } }
  properties: {
    environmentId: cae.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: registries
      secrets: [
        { name: 'marten-connection-string', keyVaultUrl: '${keyVaultUri}secrets/${martenSecretName}', identity: appIdentityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'dbapply'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          // args append to the image ENTRYPOINT ["dotnet","Crawldad.Api.dll"] ⇒ `dotnet Crawldad.Api.dll db-apply`.
          args: [ 'db-apply' ]
          // No storage config ⇒ the host defaults to the filesystem provider (valid with its default Root), so the
          // job boots on just the Marten connection; db-apply only applies schema and needs no tenants/blob.
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetCoreEnvironment }
            { name: 'ConnectionStrings__marten', secretRef: 'marten-connection-string' }
          ]
        }
      ]
    }
  }
}

// ── Portal ────────────────────────────────────────────────────────────────────────────────────────────────
// The customer-facing Blazor SSR portal, sharing this environment with the API but running under its OWN least-privilege
// identity + registry pull (issue #119 PR2). It is a thin auth+shell host with its OWN Marten document store on the SAME
// Postgres, isolated in the code-configured "portal" schema — so it needs only the Marten connection to boot (the
// identical KV secret the API uses; same server, the portal picks its schema in code). It carries NO storage/tenant
// config (those are API concerns), but it DOES persist its OWN Data Protection key ring (issue #119): its own blob
// container + its own Key Vault key, reached passwordless via the PORTAL identity (AZURE_CLIENT_ID → DefaultAzureCredential)
// which holds a Crypto User grant on that key + Blob Contributor on that container. Without it, every restart/replace
// would rotate the ring — signing users out AND orphaning the Data-Protected tenant API keys it stores (the "relink
// needed" path). The ring is isolated from the API's by a distinct application discriminator, purpose, blob, and key.
//
// Schema: the portal has no out-of-band db-apply command, and a normal start applies schema only in Development, so in
// Staging/Production it relies on Marten's default runtime auto-create (AutoCreate.CreateOrUpdate) on first document
// use. The anonymous marketing "/" route touches no database, so the app boots and serves before any schema exists —
// which is what the ingress probes + the deploy smoke-test target.
//
// Portal email (issue #119): the OTP mailer (Postmark) is wired ONLY when a server-token secret name is passed — all
// three Crawldad__Portal__Email__* env vars appear together (ServerToken by KV reference, FromAddress + MessageStream
// plain) so the portal host sees a fully-configured provider and selects PostmarkEmailSender. Empty ⇒ none are wired
// and the portal stays fail-closed (UnconfiguredEmailSender): requesting an OTP surfaces a 500, but the deploy is still
// green (the anonymous "/" probe never touches email). This all-or-nothing gate matches the host's own validator (a
// half-set pair fails boot), so the container never boots half-configured.
var hasPortalEmail = !empty(portalPostmarkTokenSecretName)

// The portal reuses the API's marten-connection-string KV secret verbatim (same Postgres; "portal" schema selected in
// code), plus — when email is configured — the Postmark token secret. Since issue #119 PR2 these resolve passwordless via
// the portal's OWN identity (secret-scoped Secrets User on exactly these two secrets), not the shared app identity.
var portalSecrets = concat(
  [
    { name: 'marten-connection-string', keyVaultUrl: '${keyVaultUri}secrets/${martenSecretName}', identity: portalIdentityId }
  ],
  hasPortalEmail
    ? [{ name: 'portal-postmark-server-token', keyVaultUrl: '${keyVaultUri}secrets/${portalPostmarkTokenSecretName}', identity: portalIdentityId }]
    : []
)

// The portal pulls its image under its OWN identity (issue #119 PR2), so its registry entry names the portal identity
// rather than the shared app identity's `registries` above.
var portalRegistries = [
  { server: acrLoginServer, identity: portalIdentityId }
]

var portalEnv = concat(
  [
    { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetCoreEnvironment }
    // Point DefaultAzureCredential at the portal's OWN least-privilege user-assigned identity (issue #119 PR2) for the
    // portal's Data Protection blob + Key Vault access — no longer the shared API identity.
    { name: 'AZURE_CLIENT_ID', value: portalIdentityClientId }
    { name: 'ConnectionStrings__marten', secretRef: 'marten-connection-string' }
    // The portal's OWN Data Protection key ring (issue #119): persisted to its own blob, wrapped by its own KV
    // key. BOTH set ⇒ durable + wrapped; the portal host fails fast if only one is set (its boot-time validator).
    { name: 'Crawldad__Portal__DataProtection__KeyRingBlobUri', value: portalKeyRingBlobUri }
    { name: 'Crawldad__Portal__DataProtection__KeyVaultKeyId', value: portalDataProtectionKeyId }
    // The API base URL the portal's tenant-scoped Crawldad.Client calls (issue #119). Without this the container
    // falls back to appsettings' localhost dev default and every workspace-link/dashboard read fails with
    // "couldn't reach the Crawldad API". Same-environment hairpin through the API's public ingress FQDN.
    { name: 'Crawldad__Api__BaseUrl', value: 'https://${app.properties.configuration.ingress.fqdn}/' }
  ],
  hasPortalEmail
    ? [
        // The Postmark OTP mailer (issue #119): token by KV reference, from-address + stream plain. All three present
        // together ⇒ the host selects PostmarkEmailSender; the double-underscore keys bind Crawldad:Portal:Email.
        { name: 'Crawldad__Portal__Email__ServerToken', secretRef: 'portal-postmark-server-token' }
        { name: 'Crawldad__Portal__Email__FromAddress', value: portalEmailFromAddress }
        { name: 'Crawldad__Portal__Email__MessageStream', value: portalEmailMessageStream }
      ]
    : [],
  hasConsoleAuth
    ? [
        // Portal console-mode (issue #119 PR4): the SAME Crawldad:ConsoleAuth section the API's scheme binds —
        // the portal acquires its Entra token (ManagedIdentityCredential via AZURE_CLIENT_ID above) for this
        // audience and switches dashboard traffic to the console path. Without these the portal stays in
        // stored-key mode even when the API scheme is registered.
        { name: 'Crawldad__ConsoleAuth__TenantId', value: consoleAuthTenantId }
        { name: 'Crawldad__ConsoleAuth__Audience', value: consoleAuthAudience }
      ]
    : []
)

resource portal 'Microsoft.App/containerApps@2024-03-01' = {
  name: portalAppName
  location: location
  tags: tags
  // The portal's OWN least-privilege identity (issue #119 PR2), not the shared app identity the API + db-apply job use.
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${portalIdentityId}': {} } }
  properties: {
    managedEnvironmentId: cae.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        traffic: [ { weight: 100, latestRevision: true } ]
      }
      registries: portalRegistries
      secrets: portalSecrets
    }
    template: {
      containers: [
        {
          name: 'portal'
          image: portalImage
          resources: { cpu: json(cpu), memory: memory }
          env: portalEnv
          // The portal has no /health endpoint; probe the anonymous marketing "/" (a 200 proves Kestrel + the Blazor
          // SSR pipeline composed and started — it renders without any database access).
          probes: [
            { type: 'Liveness', httpGet: { path: '/', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 30, failureThreshold: 3 }
            { type: 'Readiness', httpGet: { path: '/', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10, failureThreshold: 6 }
          ]
        }
      ]
      scale: { minReplicas: minReplicas, maxReplicas: maxReplicas }
    }
  }
}

output environmentName string = cae.name
output appName string = app.name
output appFqdn string = app.properties.configuration.ingress.fqdn
output dbApplyJobName string = dbApplyJob.name
output portalAppName string = portal.name
output portalAppFqdn string = portal.properties.configuration.ingress.fqdn
