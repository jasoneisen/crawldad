// RG-scoped module for bootstrap.bicep: the CI-deploy user-assigned identity, its GitHub OIDC
// federated credential, and its Contributor role assignment on this RG. A UAMI is a resource-group
// resource, so it (and its federatedIdentityCredentials child) must be created at RG scope.

@description('Location for the identity.')
param location string

@description('Name of the CI-deploy user-assigned identity.')
param identityName string

@description('GitHub org/repo the federated credential trusts.')
param githubRepo string

@description('GitHub Environment the federated credential trusts.')
param githubEnvironment string

@description('Built-in Contributor role definition GUID (granted to the identity on this RG).')
param contributorRoleId string

@description('Resource tags.')
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// GitHub Actions OIDC trust. The subject must match the workflow's environment exactly, and the
// audience is the fixed Entra token-exchange audience azure/login requests.
resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-${githubEnvironment}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepo}:environment:${githubEnvironment}'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

// Contributor (NOT Owner) on this RG for the CI identity. Owner is over-broad: it grants
// Microsoft.Authorization/roleAssignments/write, which is self-escalation (the identity could grant
// itself any role). Contributor is the least-privilege steady-state fit: it can create/update every
// app resource main.bicep declares, and the app UAMI's role assignments (AcrPull / KV Secrets User /
// Storage Blob Data Contributor) are made ONLY on the FIRST provision — run by a privileged (Owner)
// principal with -p assignManagedIdentityRoles=true. Steady-state redeploys pass
// assignManagedIdentityRoles=false, so Contributor (which lacks roleAssignments/write) suffices.
// Blast radius = this one app's RG.
resource contributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, contributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
output identityId string = identity.id
