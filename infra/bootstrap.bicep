// Crawldad — Azure staging landing-zone bootstrap (subscription-scoped, run ONCE by an Owner via bootstrap.sh).
//
// The CI-deploy chicken-and-egg: the identity that main.bicep runs under does not exist yet, so a human Owner runs
// this one deployment to create it. It provisions exactly what must exist BEFORE any main.bicep deploy can run under
// OIDC:
//
//   Subscription (sub-crawldad-staging)
//    ├─ RG  rg-crawldad-stg-<region>                    (holds every app resource main.bicep makes)
//    ├─ UAMI id-crawldad-stg-github  (in the RG)         + a GitHub OIDC federated credential
//    │        ├─ Contributor  (subscription scope)       (create the RG + all resources; NOT role assignments)
//    │        ├─ User Access Administrator  (RG scope)   (create the app identity's AcrPull + KV Secrets User)
//    │        └─ Key Vault Secrets Officer  (RG scope)   (write secret VALUES to the RBAC vault at deploy time)
//    └─ Consumption budget  bdgt-crawldad-stg            (the ~$25/mo cost-guard on the Sponsorship credits)
//
// The CI identity is NOT Owner. Owner grants Microsoft.Authorization/roleAssignments/write at subscription scope
// (self-escalation to anything). Instead it gets subscription Contributor plus the two TIGHT, RG-scoped roles above —
// least privilege for exactly what the deploy does, blast radius one RG (bar Contributor, which the task mandates at
// sub scope; the sub is a dedicated staging sub).

targetScope = 'subscription'

@description('Resource location for the RG and its resources.')
param location string = 'eastus2'

@description('Short region code used in the resource-group name (must match main.bicep). eastus2 → eus2.')
param regionCode string = 'eus2'

@description('Environment token (staging is the live target; prod is bootstrapped separately when it lands).')
@allowed([ 'stg', 'prod' ])
param envToken string = 'stg'

@description('OIDC subject prefix the federated credential trusts — the repo\'s `sub_claim_prefix` (immutable-ID form), fetched by bootstrap.sh via the GitHub OIDC customization API.')
param githubSubjectPrefix string

@description('GitHub Environment whose deployments are trusted (must match the deploy workflow job\'s environment).')
param githubEnvironment string = 'staging'

@description('Monthly staging budget in USD (the cost-guard on the Sponsorship credits). Alerts at 50/80/100% actual + 100% forecast.')
param budgetAmount int = 30

@description('Email the budget cost alerts notify.')
param budgetContactEmail string = 'jason@eisenmenger.io'

@description('First-of-month the budget tracks, UTC (budgets require a first-of-month start). Set to the current month on first run; kept as a param so re-runs after the start month do not fail "start date in the past".')
param budgetStartDate string = '2026-08-01'

var rgName = 'rg-crawldad-${envToken}-${regionCode}'
var cicdIdentityName = 'id-crawldad-${envToken}-github'
var budgetName = 'bdgt-crawldad-${envToken}'

// Built-in role: Contributor (all resources, but NO Microsoft.Authorization/roleAssignments/write — no self-escalation).
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

var tags = {
  workload: 'crawldad'
  env: envToken
  managedBy: 'bicep'
}

// ── Resource group ────────────────────────────────────────────────────────────────────
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

// ── CI-deploy identity + GitHub OIDC + RG-scoped roles (RG-scoped module) ────────────────
module cicd 'bootstrap-cicd.bicep' = {
  scope: rg
  name: 'cicd-identity'
  params: {
    location: location
    identityName: cicdIdentityName
    githubSubjectPrefix: githubSubjectPrefix
    githubEnvironment: githubEnvironment
    tags: tags
  }
}

// ── Contributor at SUBSCRIPTION scope ────────────────────────────────────────────────────
// main.bicep is subscription-scoped (it creates the RG), so the CI deploy needs Contributor on the sub. Role
// assignments + KV secret writes are covered by the tighter RG-scoped roles in the cicd module above.
resource contributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // The role-assignment NAME must be computable at the start of the deployment, so it is seeded from the identity
  // NAME (a compile-time var), not the module's runtime principalId output; the principalId flows into properties.
  name: guid(subscription().id, cicdIdentityName, contributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: cicd.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Subscription budget (the cost-guard) ─────────────────────────────────────────────────
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: budgetName
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      Actual_50: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [ budgetContactEmail ]
      }
      Actual_80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [ budgetContactEmail ]
      }
      Actual_100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [ budgetContactEmail ]
      }
      Forecast_100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [ budgetContactEmail ]
      }
    }
  }
}

// Outputs feed the GitHub OIDC repo variables (see bootstrap.sh).
output clientId string = cicd.outputs.clientId
output principalId string = cicd.outputs.principalId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
output resourceGroupName string = rg.name
