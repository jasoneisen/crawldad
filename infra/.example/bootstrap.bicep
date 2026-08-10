// OffMarket — Azure landing-zone bootstrap (subscription-scoped, run ONCE by an Owner).
//
// The bootstrap chicken-and-egg: the CI-deploy identity that main.bicep will run under does not
// exist yet, so a human Owner runs this one deployment to create it. It provisions exactly the
// pieces that must exist BEFORE any RG-scoped app deploy can run under OIDC:
//
//   Subscription
//    ├─ RG  rg-offmarket-prod-cus                      (holds every app resource main.bicep makes)
//    ├─ UAMI id-offmarket-cicd-prod-cus  (in the RG)   + a GitHub OIDC federated credential
//    │        └─ Contributor on the RG                  (least-privilege: deploys app resources but
//    │                                                   canNOT assign roles — see note below)
//    └─ Consumption budget  bdgt-offmarket-prod        (the billing-boundary control, ~$150/mo)
//
// The CI identity gets CONTRIBUTOR, not Owner. Owner would let it grant itself any role
// (self-escalation via Microsoft.Authorization/roleAssignments/write). The app UAMI's own role
// assignments (AcrPull / KV Secrets User / Storage Blob Data Contributor) are created ONLY on the
// FIRST main.bicep provision, run by a privileged (Owner) principal with -p
// assignManagedIdentityRoles=true. Every steady-state CI redeploy passes
// assignManagedIdentityRoles=false, so Contributor (no roleAssignments/write) is sufficient.
//
// A UAMI (not an Entra app registration) is used deliberately: its federatedIdentityCredentials are
// ARM resources, so the identity + its GitHub OIDC trust are fully bicep-able in one deploy — no
// `az ad app federated-credential` CLI step.
//
// Run once as Owner (context must be the OffMarket sub):
//   az deployment sub create -l centralus -f infra/bootstrap.bicep \
//     --subscription ab2368d5-d7ce-4003-a49e-554da3a88188
//
// Then feed the outputs into GitHub OIDC secrets (see the migration runbook, Phase 2.3):
//   AZURE_CLIENT_ID = clientId · AZURE_TENANT_ID = tenantId · AZURE_SUBSCRIPTION_ID = subscriptionId

targetScope = 'subscription'

@description('Resource location for the RG and its resources.')
param location string = 'centralus'

@description('GitHub org/repo the OIDC federated credential trusts (the workflow that will deploy).')
param githubRepo string = 'propertypartner-io/offmarket.deals'

@description('GitHub Environment whose deployments are trusted by the federated credential. The subject must exactly match the workflow that runs azure/login (repo:<org>/<repo>:environment:<name>).')
param githubEnvironment string = 'Production'

@description('Monthly subscription budget in USD (the OffMarket billing-boundary control). Alerts fire at 50/80/100% (actual) and 80/100% (forecast) to the notification address.')
param budgetAmount int = 150

@description('Email address the budget cost alerts notify.')
param budgetContactEmail string = 'jason@propertypartner.io'

@description('First month the budget tracks (budgets require a start date on the first of a month, UTC). Kept as a param so re-deploys after the start month do not fail the "start date in the past" validation — set it to the current month on first run.')
param budgetStartDate string = '2026-07-01'

// CAF names — region abbrev `cus` (centralus), workload `offmarket`, env `prod`.
var rgName = 'rg-offmarket-prod-cus'
var cicdIdentityName = 'id-offmarket-cicd-prod-cus'
var budgetName = 'bdgt-offmarket-prod'

var tags = {
  property: 'offmarket'
  env: 'prod'
}

// Built-in role: Contributor (create/manage all resources, but NO Microsoft.Authorization/
// roleAssignments/write — so the CI identity cannot self-escalate). The app UAMI's role assignments
// are made on the first provision by an Owner principal (assignManagedIdentityRoles=true); see header.
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

// ── Resource group ────────────────────────────────────────────────────────────
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

// ── CI-deploy identity + GitHub OIDC (RG-scoped module) ─────────────────────────
// A UAMI lives in a resource group, so the identity, its federated credential, and its RG-scoped
// Contributor role assignment are created in a module targeted at the just-created RG.
module cicd 'bootstrap-cicd.bicep' = {
  name: 'cicd-identity'
  scope: rg
  params: {
    location: location
    identityName: cicdIdentityName
    githubRepo: githubRepo
    githubEnvironment: githubEnvironment
    contributorRoleId: contributorRoleId
    tags: tags
  }
}

// ── Subscription budget (the billing-boundary control) ──────────────────────────
// Consumption budgets are extension resources on the subscription (targetScope = subscription).
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
      Forecast_80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Forecasted'
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

// Outputs feed the GitHub OIDC secrets (runbook Phase 2.3).
output clientId string = cicd.outputs.clientId
output principalId string = cicd.outputs.principalId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
output rgName string = rg.name
