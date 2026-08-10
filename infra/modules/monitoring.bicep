// Log Analytics workspace — the Container Apps environment ships its app logs + platform diagnostics here.
// PerGB2018 with the 5 GB/month free grant; a small daily cap keeps a runaway log source from ever leaving the
// free tier in low-traffic staging.

@description('Workspace name (CAF: log-crawldad-<env>).')
param name string

@description('Resource location.')
param location string

@description('Resource tags.')
param tags object

@description('Daily ingestion cap in GB (staging stays well under the 5 GB PerGB2018 free grant). 0 disables the cap.')
param dailyQuotaGb int = 1

@description('Log retention in days (30 is the free floor for PerGB2018).')
param retentionInDays int = 30

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionInDays
    workspaceCapping: dailyQuotaGb > 0 ? { dailyQuotaGb: dailyQuotaGb } : null
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

output id string = law.id
output name string = law.name
output customerId string = law.properties.customerId
