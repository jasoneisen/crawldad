// RG-scoped module for bootstrap.bicep: the CI-deploy user-assigned identity, its GitHub OIDC federated credential,
// and the three RG-scoped roles the deploy needs beyond subscription Contributor. A UAMI (not an Entra app
// registration) is used deliberately: its federatedIdentityCredentials are ARM resources, so the identity + its
// GitHub trust are fully bicep-able in one deploy — no `az ad app federated-credential` CLI step.

@description('Location for the identity.')
param location string

@description('Name of the CI-deploy user-assigned identity.')
param identityName string

@description('OIDC subject prefix GitHub presents for this repo — the repo\'s `sub_claim_prefix` (now the immutable-ID form `repo:<owner>@<ownerId>/<repo>@<repoId>`), fetched live by bootstrap.sh.')
param githubSubjectPrefix string

@description('GitHub Environment whose deployments are trusted. The subject must match the workflow job\'s environment exactly.')
param githubEnvironment string

@description('Resource tags.')
param tags object

// Built-in roles.
var uaaRoleId = '18d7d88d-d35e-4fb5-a5c3-7773c20a72d9' // User Access Administrator
var kvSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7' // Key Vault Secrets Officer
var kvCryptoOfficerRoleId = '14b46e9e-c2b7-41b4-b07b-48a6ebf60603' // Key Vault Crypto Officer

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// GitHub Actions OIDC trust. The subject binds to a GitHub ENVIRONMENT (not a branch), so a workflow_dispatch from
// any branch that runs the `<env>` environment job gets a matching token — the human gate is the environment +
// workflow_dispatch. The audience is the fixed Entra token-exchange audience azure/login requests.
resource federatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: identity
  name: 'github-${githubEnvironment}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: '${githubSubjectPrefix}:environment:${githubEnvironment}'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

// User Access Administrator, scoped to THIS resource group (not the subscription): lets the CI deploy create the app
// identity's AcrPull + Key Vault Secrets User role assignments during EVERY main.bicep deploy, idempotently — so there
// is no "first provision must be run by an Owner" ceremony (the example's assignManagedIdentityRoles=false toggle).
// Blast radius = this one RG. principalType 'ServicePrincipal' avoids the just-created-identity replication race.
resource uaa 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, uaaRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', uaaRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Key Vault Secrets Officer, scoped to this RG: an RBAC-authorization vault requires a DATA-plane role to SET secret
// VALUES via ARM (which the keyvault module does — the marten/blob connection strings + tenant key). Subscription
// Contributor is control-plane only and gets Forbidden on the secret write. Granted here at bootstrap — before the
// vault exists (an RG-scoped assignment applies to resources created later) — so it has propagated by deploy time.
resource kvSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, kvSecretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsOfficerRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Key Vault Crypto Officer, scoped to this RG: creating a KEY via ARM on an RBAC vault is a data-plane write (like the
// secret writes above), so subscription Contributor alone gets Forbidden. Needed so the keyvault module can create the
// Data Protection wrapping key (issue #65). Granted at bootstrap — re-run bootstrap.sh once before deploying that change.
resource kvCryptoOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, identity.id, kvCryptoOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCryptoOfficerRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
output identityId string = identity.id
