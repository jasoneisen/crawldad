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

@description('Absolute URI of the blob the Data Protection key ring is persisted to (issue #65).')
param keyRingBlobUri string

@description('Key Vault key id that wraps the Data Protection key ring (versionless).')
param dataProtectionKeyId string

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
// The customer-facing Blazor SSR portal, sharing this environment, identity, and registry with the API. It is a thin
// auth+shell host with its OWN Marten document store on the SAME Postgres, isolated in the code-configured "portal"
// schema — so it needs only the Marten connection to boot (the identical KV secret the API uses; same server, the
// portal picks its schema in code). It carries NO storage/tenant/DataProtection config: those are API concerns.
//
// Schema: the portal has no out-of-band db-apply command, and a normal start applies schema only in Development, so in
// Staging/Production it relies on Marten's default runtime auto-create (AutoCreate.CreateOrUpdate) on first document
// use. The anonymous marketing "/" route touches no database, so the app boots and serves before any schema exists —
// which is what the ingress probes + the deploy smoke-test target.
//
// KNOWN LIMITATION (issue #119): outside Development the portal's email sender is fail-closed (UnconfiguredEmailSender),
// so requesting a sign-in OTP surfaces a 500 until a real email provider is wired. Deploying the shell now is
// deliberate; sign-in is not expected to work end-to-end yet.
resource portal 'Microsoft.App/containerApps@2024-03-01' = {
  name: portalAppName
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
      // The portal reuses the API's marten-connection-string KV secret verbatim (same Postgres; its "portal" schema is
      // selected in code, not in the connection string), resolved passwordless via the app identity.
      secrets: [
        { name: 'marten-connection-string', keyVaultUrl: '${keyVaultUri}secrets/${martenSecretName}', identity: appIdentityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'portal'
          image: portalImage
          resources: { cpu: json(cpu), memory: memory }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: aspNetCoreEnvironment }
            { name: 'ConnectionStrings__marten', secretRef: 'marten-connection-string' }
          ]
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
