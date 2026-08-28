// A runtime user-assigned managed identity. Instantiated twice (issue #119 PR2): the API/db-apply identity
// (id-crawldad-<env>-app) and the portal's own least-privilege identity (id-crawldad-<env>-portal). Each pulls its image
// from ACR (AcrPull, granted in registry.bicep) and resolves Key Vault secret-refs (Key Vault Secrets User, granted in
// keyvault.bicep) — no long-lived credentials anywhere. The API reaches blob storage with the account-key connection
// string (a KV secret), so that role is granted only where a Data-Protection ring needs it (storage.bicep/keyvault.bicep).

@description('Identity name (CAF: id-crawldad-<env>-app or id-crawldad-<env>-portal).')
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
