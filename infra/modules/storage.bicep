// Storage account (LRS) + the single blob container the app partitions per tenant ({tenant}/downloads|screenshots/…),
// plus two dedicated containers holding the persisted Data Protection key rings: the API host's (issue #65) and the
// portal host's (issue #119), kept separate so neither app's ring is ever the other's blob.
// Holds downloaded attachments + failure/explicit screenshots; the retention janitor sweeps them host-side by TTL.
//
// The app's AzureBlobStore authenticates with the account-key connection string (a KV secret), so shared-key access
// stays enabled; blobs are never publicly readable (allowBlobPublicAccess=false, container publicAccess=None). LRS +
// public endpoint is the staging floor; ZRS/GRS and a private endpoint are prod-only deltas, deferred.
//
// Each Data Protection key ring is read/written passwordless via a managed identity (DefaultAzureCredential), so the
// identity is granted Storage Blob Data Contributor scoped to JUST each key-ring container (least privilege; the
// tenant-data container is untouched by these grants — the app already reaches it via the account-key secret). Since
// issue #119 PR2 the two rings bind DIFFERENT identities: the API key-ring container to the API identity, and the portal
// key-ring container to the portal's OWN least-privilege identity (the shared-identity grant on the portal container is
// retained so a cutover rollback needs no re-granting).

@description('Storage account name (CAF: stcrawldad<env><uniq>; globally unique, lowercase alphanumeric, <=24).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Blob container all tenants share (partitioned by a {tenant}/ blob-name prefix).')
param containerName string

@description('Container holding the API Data Protection key ring (issue #65).')
param dataProtectionContainer string

@description('Container holding the PORTAL Data Protection key ring (issue #119) — its OWN container, never the API ring.')
param portalDataProtectionContainer string

@description('Principal id of the app identity (granted Storage Blob Data Contributor on each key-ring container).')
param appIdentityPrincipalId string

@description('Principal id of the portal identity (granted Storage Blob Data Contributor on ONLY the portal key-ring container). Issue #119 PR2.')
param portalIdentityPrincipalId string

// Built-in role: Storage Blob Data Contributor (read/write/create blobs — the key-ring repo only uploads/downloads one blob).
var blobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // The app reads Crawldad:Storage:Azure:ConnectionString (account key), so shared-key access is required.
    allowSharedKeyAccess: true
    // No VNet/private endpoint in staging; reached over the public blob endpoint.
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: { publicAccess: 'None' }
}

// The Data Protection key-ring container (one blob holds the whole ring). Reached only by the app's managed identity.
resource dpContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: dataProtectionContainer
  properties: { publicAccess: 'None' }
}

// Least-privilege data-plane grant: the app identity can read/write blobs in ONLY the API key-ring container.
resource dpBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dpContainer.id, appIdentityPrincipalId, blobDataContributorRoleId)
  scope: dpContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalId: appIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The PORTAL's Data Protection key-ring container (issue #119) — its own container so the portal ring is never the API's
// blob. Reached only by the app's managed identity (the portal shares the API's identity — no new identity is created).
resource portalDpContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: portalDataProtectionContainer
  properties: { publicAccess: 'None' }
}

// The SAME least-privilege pattern for the portal container. The shared app identity keeps this grant (retained so a
// cutover rollback that repoints the portal app back to the shared identity still works with no re-granting — issue #119
// PR2). The portal's OWN identity gets its own grant below.
resource portalDpBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(portalDpContainer.id, appIdentityPrincipalId, blobDataContributorRoleId)
  scope: portalDpContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalId: appIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The portal identity's own least-privilege grant (issue #119 PR2, the review's finding #6): the portal container app now
// runs under its OWN identity and must keep reading/writing its Data-Protection key ring — the ring that encrypts the
// stored tenant API keys AND the auth/OTP cookies. Scoped to ONLY the portal key-ring container; the API's key-ring
// container and the tenant-data container are untouched by this grant.
resource portalDpBlobContributorPortalIdentity 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(portalDpContainer.id, portalIdentityPrincipalId, blobDataContributorRoleId)
  scope: portalDpContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributorRoleId)
    principalId: portalIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
