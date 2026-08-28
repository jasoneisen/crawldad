// Azure Container Registry (Basic) + the app identity's AcrPull role assignment.
//
// ACR Basic with managed-identity pull is preferred over GHCR: the app pulls with its user-assigned identity
// (AcrPull), so there are NO long-lived registry credentials to store, rotate, or leak — the passwordless posture the
// threat model favours. adminUser is disabled for the same reason. Basic tier (~$5/mo) is ample for one small image.

@description('Registry name (CAF: crcrawldad<env><uniq>, alphanumeric).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Principal id of the app identity that pulls images (granted AcrPull on this registry).')
param appIdentityPrincipalId string

@description('Principal id of the portal identity that pulls the portal image (granted its own AcrPull on this registry). Issue #119 PR2.')
param portalIdentityPrincipalId string

// Built-in role: AcrPull.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, appIdentityPrincipalId, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: appIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The portal's own AcrPull (issue #119 PR2): the portal container app pulls its image under its OWN identity, so that
// identity needs its own pull grant — the API identity's grant above does not extend to it.
resource portalAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, portalIdentityPrincipalId, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: portalIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = acr.name
output loginServer string = acr.properties.loginServer
