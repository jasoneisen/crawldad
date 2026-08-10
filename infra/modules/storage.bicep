// Storage account (LRS) + the single blob container the app partitions per tenant ({tenant}/downloads|screenshots/…).
// Holds downloaded attachments + failure/explicit screenshots; the retention janitor sweeps them host-side by TTL.
//
// The app's AzureBlobStore authenticates with the account-key connection string (a KV secret), so shared-key access
// stays enabled; blobs are never publicly readable (allowBlobPublicAccess=false, container publicAccess=None). LRS +
// public endpoint is the staging floor; ZRS/GRS and a private endpoint are prod-only deltas, deferred.

@description('Storage account name (CAF: stcrawldad<env><uniq>; globally unique, lowercase alphanumeric, <=24).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Blob container all tenants share (partitioned by a {tenant}/ blob-name prefix).')
param containerName string

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

output name string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
