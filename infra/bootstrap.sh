#!/usr/bin/env bash
#
# Crawldad staging bootstrap — the exact ordered runbook the ORCHESTRATOR runs ONCE (as an Azure Owner on the staging
# subscription, with `gh` authenticated as a repo admin). It is idempotent and safe to re-run: it never rotates an
# existing secret, and every az/gh call converges to the same state.
#
# What it does:
#   1. Deploy infra/bootstrap.bicep (RG + CI-deploy identity + GitHub OIDC federated credential + sub Contributor +
#      RG-scoped User Access Administrator + Key Vault Secrets Officer + the staging budget).
#   2. Ensure the GitHub `staging` environment exists.
#   3. Set the repo variables the deploy workflow reads (AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_STAGING_SUBSCRIPTION_ID).
#   4. Generate + set the two deploy secrets (PG_ADMIN_PASSWORD / STAGING_TENANT_API_KEY) on the `staging` environment —
#      ONLY if they do not already exist, so a re-run never rotates them.
#   5. Kick off the first deploy: `gh workflow run deploy-staging.yml -f environment=staging`.
#
# Prerequisites: az CLI logged in (an Owner on the staging sub), gh CLI logged in (repo admin on jasoneisen/crawldad).
#
# NOTE: this script is AUTHORED here for the orchestrator to run; it is not run by CI. Every step is a deliberate,
# state-changing action on Azure + GitHub.

set -euo pipefail

# ── Config (staging) ─────────────────────────────────────────────────────────────────────
SUBSCRIPTION_ID="f930a541-0b8b-4634-9c0e-f083820eb31d"   # sub-crawldad-staging
REPO="jasoneisen/crawldad"
GH_ENV="staging"
LOCATION="centralus"
REGION_CODE="cus"
ENV_TOKEN="stg"
BUDGET_EMAIL="jason@eisenmenger.io"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_NAME="crawldad-${ENV_TOKEN}-bootstrap"
# Budgets require a first-of-month start date; use the current month so a first run never trips "start date in the past".
BUDGET_START="$(date -u +%Y-%m-01)"

echo "==> Using subscription $SUBSCRIPTION_ID"
az account set --subscription "$SUBSCRIPTION_ID"

# GitHub now embeds immutable account/repo IDs in the OIDC subject (repo:<owner>@<id>/<repo>@<id>:...). Fetch the
# repo's actual sub prefix so the federated credential trusts exactly what GitHub presents.
SUBJECT_PREFIX="$(gh api "repos/$REPO/actions/oidc/customization/sub" --jq '.sub_claim_prefix // empty')"
[ -n "$SUBJECT_PREFIX" ] || SUBJECT_PREFIX="repo:$REPO"
echo "==> OIDC subject prefix: $SUBJECT_PREFIX"

# ── 1. Bootstrap deploy (idempotent) ──────────────────────────────────────────────────────
echo "==> Deploying infra/bootstrap.bicep"
az deployment sub create \
  --location "$LOCATION" \
  --name "$DEPLOY_NAME" \
  --template-file "$SCRIPT_DIR/bootstrap.bicep" \
  --parameters \
      location="$LOCATION" \
      regionCode="$REGION_CODE" \
      envToken="$ENV_TOKEN" \
      githubSubjectPrefix="$SUBJECT_PREFIX" \
      githubEnvironment="$GH_ENV" \
      budgetContactEmail="$BUDGET_EMAIL" \
      budgetStartDate="$BUDGET_START" \
  --output none

echo "==> Capturing bootstrap outputs"
CLIENT_ID="$(az deployment sub show -n "$DEPLOY_NAME" --query "properties.outputs.clientId.value" -o tsv)"
TENANT_ID="$(az deployment sub show -n "$DEPLOY_NAME" --query "properties.outputs.tenantId.value" -o tsv)"
SUB_ID="$(az deployment sub show -n "$DEPLOY_NAME" --query "properties.outputs.subscriptionId.value" -o tsv)"
RG_NAME="$(az deployment sub show -n "$DEPLOY_NAME" --query "properties.outputs.resourceGroupName.value" -o tsv)"
echo "    clientId=$CLIENT_ID  tenantId=$TENANT_ID  rg=$RG_NAME"

# ── 2. Ensure the GitHub environment exists ────────────────────────────────────────────────
# The deploy workflow's job runs under `environment: staging`, whose OIDC subject the federated credential trusts.
echo "==> Ensuring GitHub environment '$GH_ENV' exists"
gh api --method PUT "repos/$REPO/environments/$GH_ENV" >/dev/null

# ── 3. Repo variables the workflow reads ───────────────────────────────────────────────────
echo "==> Setting repo variables (AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_STAGING_SUBSCRIPTION_ID)"
gh variable set AZURE_CLIENT_ID              --repo "$REPO" --body "$CLIENT_ID"
gh variable set AZURE_TENANT_ID              --repo "$REPO" --body "$TENANT_ID"
gh variable set AZURE_STAGING_SUBSCRIPTION_ID --repo "$REPO" --body "$SUB_ID"

# ── 4. Deploy secrets (generate once; never rotate on re-run) ──────────────────────────────
# PG password meets Azure Postgres complexity (>=3 of upper/lower/digit/non-alnum) and is fully alphanumeric so it is
# safe inside the marten connection string. The tenant key is >=16 chars (the app's TenantRegistry floor).
ensure_env_secret() {
  local name="$1" value="$2"
  if gh secret list --repo "$REPO" --env "$GH_ENV" --json name --jq '.[].name' 2>/dev/null | grep -qx "$name"; then
    echo "    secret $name already set on env $GH_ENV — leaving it (no rotation)"
  else
    printf '%s' "$value" | gh secret set "$name" --repo "$REPO" --env "$GH_ENV"
    echo "    set secret $name on env $GH_ENV"
  fi
}

echo "==> Ensuring deploy secrets on env '$GH_ENV'"
ensure_env_secret "PG_ADMIN_PASSWORD"      "$(openssl rand -hex 24)Aa9"
ensure_env_secret "STAGING_TENANT_API_KEY" "$(openssl rand -hex 24)"

# ── 5. First deploy ────────────────────────────────────────────────────────────────────────
echo "==> Triggering the first staging deploy"
gh workflow run deploy-staging.yml --repo "$REPO" -f environment="$GH_ENV"

cat <<EOF

Bootstrap complete.
  Resource group : $RG_NAME
  CI identity    : id-crawldad-${ENV_TOKEN}-github  (clientId $CLIENT_ID)
  First deploy   : triggered — watch it with:  gh run watch --repo $REPO

The deploy provisions the app stack, builds + pushes the image, applies schema (db-apply job), and smoke-tests
GET /health. Re-running this script is safe: infra converges, variables re-set, secrets are left as-is.
EOF
