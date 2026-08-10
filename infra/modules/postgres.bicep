// Azure Database for PostgreSQL Flexible Server — Marten's event + document store, Wolverine's durable-messaging
// substrate, and the durable admission queue, all in one Postgres (see ARCHITECTURE.md A.2).
//
// Staging sizing is the POC floor: Burstable B1ms, 32 GiB, PG16, NO high availability, NO zone redundancy, NO
// geo-redundant backup. Access is PUBLIC-endpoint + firewalled to Azure services (no VNet in staging) — the cheap
// path that still validates the same ingress/connection contract. VNet integration + private access are prod-only
// deltas, deferred (ARCHITECTURE.md B.2/B.3).

@description('Server name (CAF: psql-crawldad-<env>-<uniq>; globally unique, lowercase).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Administrator login.')
param administratorLogin string

@secure()
@description('Administrator password (generated at bootstrap, never committed).')
param administratorPassword string

@description('Application database name (Marten creates its "crawldad" schema inside it).')
param databaseName string

@description('PostgreSQL major version.')
param version string = '16'

@description('Compute SKU name. Burstable B1ms is the POC floor.')
param skuName string = 'Standard_B1ms'

@description('Compute SKU tier.')
@allowed([ 'Burstable', 'GeneralPurpose', 'MemoryOptimized' ])
param skuTier string = 'Burstable'

@description('Provisioned storage in GiB.')
param storageSizeGB int = 32

@description('Backup retention window in days.')
param backupRetentionDays int = 7

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: skuName, tier: skuTier }
  properties: {
    version: version
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: { storageSizeGB: storageSizeGB, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: backupRetentionDays, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    authConfig: { activeDirectoryAuth: 'Disabled', passwordAuth: 'Enabled' }
    // Public endpoint, firewalled to Azure services (below). No delegatedSubnet/privateDnsZone ⇒ no VNet, keeping
    // staging cheap. TLS is required by the server (SSL Mode=VerifyFull in the composed connection string).
    network: { publicNetworkAccess: 'Enabled' }
  }
}

resource db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: pg
  name: databaseName
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

// The special "allow all Azure services" sentinel (start=end=0.0.0.0) — NOT a public 0.0.0.0/0 range. It lets the
// in-Azure Container Apps app + db-apply job reach the server; the GitHub runner (not an Azure IP) deliberately
// cannot, which is why schema is applied by the in-Azure db-apply job rather than from the runner.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: pg
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

output serverName string = pg.name
output fqdn string = pg.properties.fullyQualifiedDomainName
output databaseName string = db.name
