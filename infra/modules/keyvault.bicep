// Key Vault (RBAC) holding the app's three secrets + the two Data Protection key-ring wrapping keys (the API host's,
// issue #65, and the portal host's, issue #119), plus the app identity's read (Secrets User) and wrap/unwrap
// (Crypto User) grants. The Crypto User grant is vault-scoped, so it covers both wrapping keys with no second grant.
//
// The two connection strings are COMPOSED here (not passed in) so the Postgres password and the storage account key
// never become deployment outputs: the marten string is built from the secure password + the Postgres FQDN, and the
// blob string is built from the account key of the already-created storage account (referenced `existing`). The third
// secret is the generated placeholder-tenant API key. The app resolves all three by reference via its identity
// (Key Vault Secrets User, granted below); the deploy principal writes the values via Key Vault Secrets Officer
// (granted at bootstrap — RBAC vaults require a data-plane role to set secret values).

@description('Vault name (CAF: kv-crawldad-<env>-<uniq>; <=24).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Principal id of the app identity (granted Key Vault Secrets User).')
param appIdentityPrincipalId string

@description('Postgres fully-qualified domain name (public endpoint).')
param pgFqdn string

@description('Postgres administrator login.')
param pgAdminLogin string

@secure()
@description('Postgres administrator password.')
param pgAdminPassword string

@description('Application database name.')
param pgDatabaseName string

@description('Npgsql Maximum Pool Size (capped under the B1ms connection ceiling).')
param pgMaxPoolSize int

@description('Storage account name (referenced existing to compose the account-key connection string in-module).')
param storageAccountName string

@secure()
@description('Generated placeholder-tenant API key (>=16 chars; never committed).')
param tenantApiKey string

@secure()
@description('Generated beta-tenant API key; empty ⇒ the beta-tenant secret is not created.')
param betaTenantApiKey string = ''

@secure()
@description('Postmark server token for the portal OTP mailer (issue #119); empty ⇒ the secret is not created and the portal stays fail-closed (no email provider). Supplied from a GitHub secret, never committed.')
param postmarkServerToken string = ''

@description('Purge protection. Off in staging so a torn-down vault name can be purged + reused; irreversible once on.')
param enablePurgeProtection bool = false

@description('Soft-delete retention window in days.')
param softDeleteRetentionInDays int = 7

// Built-in role: Key Vault Secrets User (data-plane read).
var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// Built-in role: Key Vault Crypto User (data-plane wrap/unwrap) — the app wraps the Data Protection keys with the key below.
var kvCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'

// Azure Postgres requires TLS; VerifyFull does real CA + hostname validation over the public FQDN (the aspnet base
// image trusts Azure's DigiCert roots). Pool capped under the B1ms ~35-connection ceiling, leaving room for the
// Marten async daemon + Wolverine direct connections; min pool 0 is scale-to-zero friendly.
var martenConnString = 'Host=${pgFqdn};Port=5432;Database=${pgDatabaseName};Username=${pgAdminLogin};Password=${pgAdminPassword};SSL Mode=VerifyFull;Maximum Pool Size=${pgMaxPoolSize};Minimum Pool Size=0'

// Compose the account-key connection string in-module so the key never leaves as a deployment output. This is exactly
// what AzureBlobStore reads (Crawldad:Storage:Azure:ConnectionString → new BlobServiceClient(connectionString)).
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}
var blobConnString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // Set true only when enabling (it is irreversible); null leaves it off for staging.
    enablePurgeProtection: enablePurgeProtection ? true : null
    publicNetworkAccess: 'Enabled'
  }
}

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

resource tenantKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'primary-tenant-apikey'
  properties: { value: tenantApiKey }
}

resource betaTenantKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(betaTenantApiKey)) {
  parent: kv
  name: 'beta-tenant-apikey'
  properties: { value: betaTenantApiKey }
}

// The portal OTP mailer's Postmark server token (issue #119). Created ONLY when the token is supplied — empty ⇒ no
// secret, and the portal env below wires no email provider, so the portal stays fail-closed (deploy still green). The
// value is the token itself; the app resolves it by reference via the SAME app identity (Key Vault Secrets User) that
// reads the marten/blob/tenant secrets. Rotating: set the GitHub secret + redeploy (canonical), or update this secret's
// value in the vault and restart the portal revision (picked up on the next secret refresh).
resource postmarkTokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(postmarkServerToken)) {
  parent: kv
  name: 'portal-postmark-server-token'
  properties: { value: postmarkServerToken }
}

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, appIdentityPrincipalId, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: appIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The key wrapping the API host's persisted Data Protection key ring (issue #65). RSA 2048; only wrap/unwrap are needed.
// The app references it by its VERSIONLESS id so key rotation keeps decrypting existing keys (encrypt uses the latest version).
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: kv
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [ 'wrapKey', 'unwrapKey' ]
  }
}

// The key wrapping the PORTAL host's persisted Data Protection key ring (issue #119) — its own wrapping key, symmetric
// with the API's above. Referenced versionless for the same rotation reason. The app identity already holds vault-wide
// Crypto User (the grant below is vault-scoped), so wrapping with THIS key needs no additional role assignment.
resource dataProtectionPortalKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: kv
  name: 'dataprotection-portal'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [ 'wrapKey', 'unwrapKey' ]
  }
}

// Crypto User (wrap/unwrap), vault-scoped like the Secrets User grant above — it covers every key in the vault (the
// API's dataprotection key AND the portal's dataprotection-portal key), so a second key needs no second grant.
resource kvCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, appIdentityPrincipalId, kvCryptoUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCryptoUserRoleId)
    principalId: appIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = kv.name
output vaultUri string = kv.properties.vaultUri
output martenSecretName string = martenSecret.name
output blobSecretName string = blobSecret.name
output tenantKeySecretName string = tenantKeySecret.name
output dataProtectionKeyId string = dataProtectionKey.properties.keyUri
output dataProtectionPortalKeyId string = dataProtectionPortalKey.properties.keyUri
