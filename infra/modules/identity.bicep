// The app's runtime user-assigned managed identity. It pulls the image from ACR (AcrPull, granted in registry.bicep)
// and resolves Key Vault secret-refs (Key Vault Secrets User, granted in keyvault.bicep) — no long-lived credentials
// anywhere. Blob storage is reached with the account-key connection string (a KV secret), so this identity needs no
// Storage role.

@description('Identity name (CAF: id-crawldad-<env>-app).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
