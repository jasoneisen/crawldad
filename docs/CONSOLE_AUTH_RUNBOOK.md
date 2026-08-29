# Console auth — one-time operator runbook (issue #119 PR2)

Stand up the **API App Registration** exposing a **`Console.Access` AppRole**, assign that role to the **portal's
user-assigned managed identity (UAMI)**, and hand the resulting non-secret IDs to the deploy. Do this **once per
environment** (staging, then prod). Re-running is safe — every step is idempotent.

## Why this is a runbook and not part of the deploy

App Registrations and AppRole assignments are **Microsoft Graph** objects, not ARM resources, so Bicep cannot create
them and the deploy's GitHub-OIDC identity (`id-crawldad-<env>-github`) is scoped to **ARM** and has **no Graph
application-write permission**. Creating an app registration + assigning an app role therefore runs **out of band**, under
a **privileged human operator**, exactly once. The deploy only *consumes* the results (a directory GUID + a public App ID
URI) through `readEnvironmentVariable` params, so **the deploy stays green whether or not this runbook has been run** —
until both values are set, the API's `ConsolePrincipal` scheme simply isn't registered (`ApiKey` stays the only scheme).

> Do **not** run these commands from CI or from an unattended machine. They mutate the directory.

## Prerequisites

- Azure CLI signed in as an operator with, in the target directory, **Application Administrator** (or **Cloud
  Application Administrator**) — enough to create an app registration, add an AppRole, and write app-role assignments.
- The infra deploy has already run at least once for the environment (so the portal UAMI exists).
- `jq` and `uuidgen` available.

## Inputs

```bash
# --- set these for the environment you are provisioning ---
ENV=stg                                   # stg | prod
RG="rg-crawldad-${ENV}-cus"               # resource group (CAF: rg-crawldad-<env>-<regioncode>)
APP_NAME="crawldad-api-${ENV}"            # the API App Registration display name
ROLE_VALUE="Console.Access"               # the AppRole value the portal UAMI must carry

TENANT_ID="$(az account show --query tenantId -o tsv)"

# Default tenant policy requires identifier URIs to embed a verified domain, the tenant ID, or the app ID
# (bare api://<name> is rejected on newer tenants) — the tenant-ID form satisfies it and stays stable.
APP_URI="api://${TENANT_ID}/crawldad-api-${ENV}"   # the App ID URI == the ConsolePrincipal token audience

# The portal UAMI's service-principal (object) id — the principal that receives the AppRole. Read it straight from the
# managed identity (or from the deploy outputs: properties.outputs.portalIdentityPrincipalId.value).
PORTAL_SP_OID="$(az identity show -g "$RG" -n "id-crawldad-${ENV}-portal" --query principalId -o tsv)"

echo "tenant=$TENANT_ID portalSp=$PORTAL_SP_OID app=$APP_NAME uri=$APP_URI"
```

## Step 1 — Create (or update) the API App Registration with the `Console.Access` AppRole

The AppRole needs a **stable GUID**. Generate it **once** and keep it (store it next to the environment's other IDs);
re-runs must reuse the same GUID so the role identity is stable.

```bash
ROLE_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"   # GENERATE ONCE per environment (uuidgen also fine if installed)
# Record it durably for re-runs — this repo's convention: gh variable set CONSOLE_AUTH_ROLE_ID --body "$ROLE_ID"
# (staging's role id lives in the CONSOLE_AUTH_ROLE_ID repository variable.)

cat > /tmp/console-approle.json <<JSON
[
  {
    "allowedMemberTypes": ["Application"],
    "description": "Portal console access to the Crawldad API",
    "displayName": "Console Access",
    "id": "${ROLE_ID}",
    "isEnabled": true,
    "value": "${ROLE_VALUE}"
  }
]
JSON

APP_ID="$(az ad app list --display-name "$APP_NAME" --query '[0].appId' -o tsv)"
if [ -z "$APP_ID" ]; then
  APP_ID="$(az ad app create \
    --display-name "$APP_NAME" \
    --identifier-uris "$APP_URI" \
    --app-roles @/tmp/console-approle.json \
    --sign-in-audience AzureADMyOrg \
    --query appId -o tsv)"
  echo "created app registration $APP_ID"
else
  # Idempotent update: ensure the identifier URI and the AppRole are present with the SAME role GUID as before.
  az ad app update --id "$APP_ID" --identifier-uris "$APP_URI"
  az ad app update --id "$APP_ID" --app-roles @/tmp/console-approle.json
  echo "updated existing app registration $APP_ID"
fi
```

`allowedMemberTypes: ["Application"]` means only applications/managed identities — never users — can hold the role, so
`roles: ["Console.Access"]` in a token is proof the caller is the portal UAMI. `api://…` identifier URIs need no domain
verification (unlike `https://` URIs), so `api://crawldad-api-<env>` is accepted as-is.

## Step 2 — Ensure the API's resource service principal exists, and require role assignment

AppRole assignments target the **enterprise application** (the service principal) behind the app registration.

```bash
API_SP_OID="$(az ad sp list --filter "appId eq '${APP_ID}'" --query '[0].id' -o tsv)"
if [ -z "$API_SP_OID" ]; then
  API_SP_OID="$(az ad sp create --id "$APP_ID" --query id -o tsv)"
  echo "created resource service principal $API_SP_OID"
fi

# Require an explicit assignment to receive a token for this API (defence in depth; the role claim already gates access).
az rest --method PATCH \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}" \
  --headers "Content-Type=application/json" \
  --body '{"appRoleAssignmentRequired": true}'
```

## Step 3 — Assign `Console.Access` to the portal UAMI (idempotent)

```bash
EXISTING="$(az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}/appRoleAssignedTo" \
  --query "value[?principalId=='${PORTAL_SP_OID}' && appRoleId=='${ROLE_ID}'] | [0].id" -o tsv)"

if [ -z "$EXISTING" ]; then
  az rest --method POST \
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}/appRoleAssignedTo" \
    --headers "Content-Type=application/json" \
    --body "{\"principalId\":\"${PORTAL_SP_OID}\",\"resourceId\":\"${API_SP_OID}\",\"appRoleId\":\"${ROLE_ID}\"}"
  echo "assigned ${ROLE_VALUE} to portal UAMI ${PORTAL_SP_OID}"
else
  echo "assignment already present ($EXISTING) — nothing to do"
fi
```

## Step 4 — Hand the non-secret IDs to the deploy

```bash
echo "CONSOLE_AUTH_TENANT_ID=${TENANT_ID}"
echo "CONSOLE_AUTH_AUDIENCE=${APP_URI}"
```

Set these two as GitHub **environment variables** (repository → Settings → Environments → *staging* → Variables — they are
**not** secrets: a directory GUID and a public App ID URI):

- `CONSOLE_AUTH_TENANT_ID` = the tenant GUID above
- `CONSOLE_AUTH_AUDIENCE` = the `APP_URI` above (`api://<tenantId>/crawldad-api-<env>`)

The deploy workflow exports them (`.github/workflows/deploy-staging.yml`), `infra/main.<env>.bicepparam` reads them with
`readEnvironmentVariable`, and `app.bicep` wires the API container's `Crawldad__ConsoleAuth__*` env — at which point the
API registers the `ConsolePrincipal` scheme. Re-run the deploy to apply. (Leaving them unset keeps the scheme inert; the
deploy is green either way.)

> Note: **prod** uses the `PROD_CONSOLE_AUTH_TENANT_ID` / `PROD_CONSOLE_AUTH_AUDIENCE` variable names
> (`infra/main.prod.bicepparam`); wire them the same way when prod is bootstrapped.

## What stays inert after this runbook

Registering the scheme does **not** change request handling: **no endpoint opts into `ConsolePrincipal` yet** (the
`ConsoleOrKey` policy is PR5), so nothing accepts a console token and `ApiKey` remains the only scheme any route requires.
This runbook only makes the scheme *validate a token when one is presented to it* — which the CI suite exercises with
test-issued tokens. Mapping a console token to a tenant (the membership store) is PR4.

## Verify

```bash
# The role is assigned:
az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}/appRoleAssignedTo" \
  --query "value[?principalId=='${PORTAL_SP_OID}'].appRoleId" -o tsv    # ⇒ prints $ROLE_ID

# The tenant's v1.0 metadata (whose issuer the API trusts) is reachable:
curl -s "https://login.microsoftonline.com/${TENANT_ID}/.well-known/openid-configuration" | jq -r .issuer
#   ⇒ https://sts.windows.net/<tenant>/
```

## Rollback

```bash
# Remove just the assignment (leaves the app registration in place):
ASSIGN_ID="$(az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}/appRoleAssignedTo" \
  --query "value[?principalId=='${PORTAL_SP_OID}' && appRoleId=='${ROLE_ID}'] | [0].id" -o tsv)"
az rest --method DELETE \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/${API_SP_OID}/appRoleAssignedTo/${ASSIGN_ID}"

# Or clear the two GitHub variables and redeploy — the scheme goes inert again with no directory change.
```
