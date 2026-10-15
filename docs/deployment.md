# Deployment and operations reference

[Back to README](../README.md) | [Step-by-step installation](deployment-step-by-step.md) |
[Architecture](architecture.md) | [Development](development.md)

Use this reference for infrastructure contracts, protected configuration,
updates, credential rotation and troubleshooting. For a first deployment,
follow the [installation guide](deployment-step-by-step.md).

> [!WARNING]
> This is an independent PoC, not affiliated with, endorsed or supported by
> GitHub or Microsoft. It is not a production-readiness certification.
> The adopting organization must review, test and approve deployment, App
> permissions, administrator-managed installations and real budget changes.
> The [project disclaimer](../README.md#disclaimer-and-adoption-responsibility)
> applies.

## Deployment sequence

The Entra application is an administrator-owned prerequisite. The deployment does not call Microsoft Graph or create applications, service principals, credentials, or redirect URIs.

The operator sets `PORTAL_DEPLOYMENT_CONFIG` and runs `azd up`. Hooks load the JSON and credentials automatically. When not already configured, azd prompts for tenant, subscription, and location. The administrator registers the provisioned callback URLs before sign-in. Organization and identity discovery occurs at runtime.

Local Bicep compilation and hook tests validate implementation contracts,
not your subscription policies, runtime availability, permissions or live
integration. Successful provisioning or a healthy startup endpoint is not
deployment approval.

## Prerequisites

Install:

- .NET 10 SDK for building `src/BudgetPortal/BudgetPortal.csproj`.
- Current Azure Developer CLI (`azd`) and Azure CLI (`az`).
- Python 3.10+ (`python3` on Linux/macOS; `python` on Windows).
- Windows only: PowerShell 7 (`pwsh`).

The Azure operator needs subscription-level permission to create a dedicated resource group and the included resources, including virtual networks, NSGs, private endpoints and private DNS zones, plus role-assignment permission for the app's Application Insights role (for example Owner, or Contributor plus Role Based Access Control Administrator). Key Vault access policies are used for secret references; subscriptions enforcing RBAC-only vaults require adapting that policy before deployment. Resource providers Microsoft.Web, Microsoft.KeyVault, Microsoft.Network, Microsoft.Insights and Microsoft.OperationalInsights must be available. The selected Azure subscription and Entra application must belong to the same tenant.

The default Linux App Service runtime is **`DOTNETCORE|10.0`**, using the platform's standard ASP.NET Core startup/port handling, not a custom container or fixed port. Confirm availability in your Azure cloud with `az webapp list-runtimes --os linux`; a cloud that lacks .NET 10 needs a reviewed container variant, not a silent downgrade.

## Administrator-owned GitHub prerequisites

Register **one GitHub App** against the correct GitHub Enterprise Cloud host (including the dedicated API host for a data-resident `ghe.com` enterprise). Generate/download its private key yourself. This setup does **not** automate GitHub App registration or installation approval.

1. Create one enterprise-owned GitHub App with enterprise **Billing: read and write**, **Enterprise organization installations: read**, and **Enterprise SCIM: read**.
2. Give the same App organization **Members: read** and **GitHub Copilot Business / Copilot seat management: read** permissions. The permission UI may label the latter **GitHub Copilot Business**. Repository permissions and Copilot license-assignment writes are not required.
3. Install the App at enterprise level and in the organizations to include in the portal. At least one installed organization must belong to that enterprise. Other enterprise organizations are excluded from portal access, but remain in the complete login inventory for center isolation. The portal discovers existing installations without an installation-ID configuration and never installs or updates the App.
4. Verify that Entra's EMU provisioning maps the immutable Entra object ID to the GitHub SCIM `externalId`. The portal resolves `oid` through SCIM and uses the returned `profileUrl` to obtain the canonical EMU login on the configured GitHub host. SCIM `userName` may differ from that login (for example an IdP UPN); it is used only when `profileUrl` is absent and the API returns the same canonical login. The portal never correlates accounts by email or display name.
5. Choose one global `MaxBudgetAmount` for each created or updated per-user budget, not an aggregate cap. Cost-center defaults require nonempty user-only centers with unambiguous attribution to eligible direct licensed users; shared organization membership alone does not exclude a user-only center.

After changing the App's permission definition, approve the new permissions
on existing enterprise and organization installations. A definition change
alone is insufficient. Discovery and token caches may need to expire, or the
application can be restarted after approval.

The enterprise token still cannot read organization membership directly. Administrator-managed organization installations provide separate organization-scoped tokens for owner checks, member inventory, and Copilot seats. An active Owner can view, create, update and delete individual budgets for members with a Copilot seat in an owned installed organization, including members of other organizations. Other memberships or cost-center assignments do not revoke this individual authorization. The budget remains enterprise-global, so different Owners may manage the same user's total consumption limit. Membership, seats and ownership are rechecked on every operation. Cost centers retain fail-closed checks for unauthorized resources and direct foreign/ambiguous assignments, including organizations without an App installation. A missing enterprise installation, no in-enterprise organization installation, suspended in-scope installations and incomplete discovery remain explicit failures. The supported API hosts are `api.github.com` and `api.<subdomain>.ghe.com`; arbitrary GitHub Enterprise Server API endpoints are not supported.

GitHub App private keys, enterprise billing, SCIM, and organization-installation permissions have enterprise-wide power. Protect the administrator-managed key and never grant organization administrators access to it.

## Prepare local configuration

Copy `scripts/deployment.example.json` to **`scripts/deployment.local.json`** and replace every example value. Place the App key at a local private path. `PrivateKeyPemFile` is relative to the configuration file (or may be absolute); the hook reads the PEM into the Key Vault configuration secret. Use an unencrypted RSA PEM downloaded from the GitHub App.

The sample deliberately contains no real credentials. It will not work until an administrator supplies the Entra tenant ID, Entra application Client ID, Entra client secret value, enterprise slug, GitHub App ID, amount ceiling, and GitHub key. API origins must be HTTPS on `api.github.com` or `api.<single-subdomain>.ghe.com`, with no path beyond `/`, credentials, query, fragment or nondefault port (explicit 443 is accepted). `MaxBudgetAmount` is a nonnegative signed 32-bit integer in the billing API's currency units, not cents; zero permits only zero-amount budgets. The entire serialized Portal configuration must be under 25,000 UTF-8 bytes (Key Vault secret limit).

Enterprise slugs are limited to 100 characters. `GitHub.ApiVersion` must be a real `YYYY-MM-DD` date; it defaults to `2026-03-10`, matching the backend. The application additionally parses the supplied RSA private key and requires at least 2048 bits; the dependency-free provisioning validator checks PEM private-key delimiters, not its cryptographic contents.

```sh
chmod 600 scripts/deployment.local.json /private/path/github-app.pem
python3 scripts/deploy.py validate --config scripts/deployment.local.json
az login --tenant YOUR_TENANT_ID
azd auth login
azd env new budget-portal-poc
azd env set PORTAL_DEPLOYMENT_CONFIG scripts/deployment.local.json
azd up
# Register the two printed callback URLs manually before sign-in.
```

On Windows use `python` instead of `python3` and restrict file access through Windows ACLs instead of `chmod`. The Azure CLI and azd sign-ins are separate; both must be authorized. The `preup` and `preprovision` hooks load the configured JSON; no manual `prepare` command is required. If `PORTAL_DEPLOYMENT_CONFIG` is absent, the interactive hook asks for its path; noninteractive deployment fails with actionable guidance instead of inventing values.

Select a subscription in the configured Entra tenant and an approved region in the native azd prompts. Existing environments reuse their saved subscription and location. Preparation checks tenant consistency when a subscription is already known; the postprovision hook checks the selected subscription again before deployment. On a new environment, a mismatch detected at that stage stops deployment but does not remove resources already provisioned.

### Administrator-owned Entra application

Before deployment, an Entra administrator must:

- Create a single-tenant web application (`AzureADMyOrg`) and service principal.
- Create a client secret with an organization-approved expiry.
- Provide the tenant ID, Application (client) ID, and the secret **value** in the protected deployment JSON.
- Register the two callback URLs printed by the postprovision hook as Web redirect URIs before sign-in. `azd up` can publish the app before callback registration; `/healthz` does not validate the Entra redirect configuration.

The `Entra` configuration is mandatory:

```json
{
  "TenantId": "YOUR-TENANT-UUID",
  "ClientId": "YOUR-APPLICATION-CLIENT-UUID",
  "ClientSecret": "<CLIENT-SECRET-VALUE>"
}
```

`ClientSecret` contains the secret value, not its identifier. The local JSON stores it in plaintext: restrict file access and never share it. Git exclusion does not protect local file access. The application uses authorization-code flow; implicit access-token and ID-token issuance must remain disabled. The deployment operator does not need Entra application-management permissions.

Configure user assignment and Conditional Access according to tenant policy. At runtime the portal also requires an active EMU resolved through SCIM and a live Organization Owner role.

## Infrastructure contract

`infra/main.bicep` creates `rg-budget-portal-<deterministic-token>` rather than reusing a generic resource group from another project. The token incorporates subscription, environment name, location and the portal name. Keep these inputs stable; changing location or environment changes resource names. `azd` discovers the service through the `azd-service-name: web` tag and the **`AZURE_WEB_NAME`** output.

Resources:

- Linux App Service B1 plan, initially **one instance**, HTTPS-only, FTP disabled and minimum TLS 1.2 for app and SCM.
- System-assigned managed identity with **get-only** Key Vault secret access.
- Soft-delete/purge-protected Key Vault with public access disabled and an approved private endpoint. No GitHub key, OIDC secret, or portal configuration is emitted as a deployment output.
- Dedicated VNet (`10.72.0.0/16`), separate App Service integration (`10.72.0.0/24`) and private endpoint (`10.72.1.0/24`) subnets, NSGs, and a linked `privatelink.vaultcore.azure.net` private DNS zone.
- Log Analytics (30-day retention) and workspace-based Application Insights, with Entra-authenticated ingestion and a scoped Monitoring Metrics Publisher role for the web identity. App Service's .NET monitoring agent is enabled; verify telemetry after deploying in the target cloud.
- Console/application diagnostics to Log Analytics. Raw HTTP access-log export is intentionally omitted to reduce authentication-query-string exposure. Application logs must never include tokens, keys or configuration payloads.

App settings:

| Setting | Source |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `AllowedHosts` | Provisioned App Service hostname |
| `WEBSITES_ENABLE_APP_SERVICE_STORAGE` | `true` |
| `Entra__TenantId`, `Entra__ClientId`, `Entra__ClientSecret` | Versionless Key Vault references |
| `PORTAL_CONFIGURATION_JSON` | Versionless reference to the `portal-configuration` Key Vault secret |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Provisioned Application Insights |
| `APPLICATIONINSIGHTS_AUTHENTICATION_STRING` | `Authorization=AAD` |

`PORTAL_CONFIGURATION_JSON` is the **Portal section's value**, not an object wrapped in `Portal`. Its root keys are `GitHub` and `MaxBudgetAmount`; the application loads this value into its `Portal` configuration section before binding. The hook replaces `GitHub.PrivateKeyPemFile` with the actual `GitHub.PrivateKeyPem`. `Entra` is not part of that JSON.

The hook JSON-escapes the client secret and serialized Portal configuration for substitution into `infra/main.parameters.json`. Parsing the resolved parameters restores the original strings, including PEM newlines, before they reach Key Vault. Supply ordinary values in the deployment JSON; do not escape them a second time or set these two azd variables manually.

The application persists data-protection keys under **`$HOME/data/BudgetPortal/keys`** (`/home/data/BudgetPortal/keys` in App Service) so auth cookies and antiforgery state survive restarts/redeployments. App Service persistent storage is enabled. `AllowedHosts` restricts requests to the provisioned hostname, and the application's authentication redirects use the Azure-provided `WEBSITE_HOSTNAME` as their fixed public HTTPS origin. Cookies are always Secure. Do not scale out or introduce slots without reviewing a shared encrypted key store and application affinity/data consistency. App Service handles standard .NET hosting; no development port, authentication bypass or demo-mode flag is deployed.

### Private network

App Service uses regional VNet integration and routes outbound traffic through the VNet, as required for Linux Key Vault references to private endpoints. The integration subnet allows HTTPS to the vault subnet and public HTTPS for GitHub, Entra and monitoring; other outbound traffic is denied. Azure platform DNS remains available by default outside NSG filtering. Do not add an `Allow` rule targeting `AzurePlatformDNS`: Azure permits only `Deny` rules for that platform service tag. The endpoint subnet accepts HTTPS only from the integration subnet. App settings are applied after the private endpoint DNS configuration and VNet link exist. Key Vault authorization remains managed-identity, get-only access; private networking does not replace authorization.

The VNet is dedicated and has no peering. Review its address ranges before connecting it to an existing network. B1 supports regional VNet integration; private endpoints and private DNS incur additional charges. The deployment remains a single `azd up` command with no new JSON fields. The portal and SCM endpoints remain public; this template does not provide WAF, private-only SCM, or a custom domain, and is not a complete production security baseline.

References: [App Service VNet integration](https://learn.microsoft.com/azure/app-service/overview-vnet-integration), [network-restricted Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references#access-network-restricted-vaults).

## Secrets, updates and rotation

Bicep marks all identity/configuration inputs as `@secure()`; ARM deployment outputs contain only resource names and the portal URL. Secrets are stored in Key Vault and read through App Service's managed identity.

**Local `.azure/` state contains plaintext secrets** because azd must provide the secure ARM parameters. The hook restricts this state to the current POSIX user; Windows requires appropriate ACLs. Do not upload `.azure`, administrator JSON, PEMs, secret files, shell transcripts, or `azd env get-values` output. `.azureignore` excludes deployment-only files from app packaging; it does not replace `.gitignore`. Keep all credential files outside source control, use the documented `.local.json` suffix, and verify ignores before adding files.

The hook does not print command output or credentials, but `azd env set` necessarily passes values to a child process. Run deployment from a trusted single-user workstation/runner: other privileged local processes may inspect command arguments or state. Never enable CLI debug/verbose request logging with production secrets.

To change configuration or secrets, edit the administrator-managed files and run `azd up` (or `azd provision` if application binaries are unchanged). Hooks reload the JSON automatically. Run `azd deploy` for code-only updates. Successful installation discovery is cached for ten minutes per process; identity and Owner roles are reloaded for budget-data requests and mutations. Membership, seats, centers and budgets are read live only where needed by the operation and stored scope, as described in [operation-specific reads](architecture.md#operation-specific-reads). Installation tokens have their own expiry/refresh cache. Provisioning updates Key Vault secrets and refreshes App Service references; reference propagation can still take time. Azure App Service normally refreshes versionless references within 24 hours; retry `python3 scripts/deploy.py redirects` to request a refresh after verifying permissions.

Rotate the Entra client secret before its configured expiry. An administrator creates the replacement credential and updates `Entra.ClientSecret` in the local JSON; the operator reloads and reprovisions it. Verify sign-in before removing the old credential. Rotate GitHub App keys similarly; keep the old key until deployment and live checks succeed.

## Cleanup

`azd down` deletes the Azure portal resource group, including monitoring and
stored secrets; review data retention and obtain approval first.
It does not delete independently registered Entra applications, service
principals, credentials, GitHub Apps or their installations.
Remove those separately only when they are no longer needed.

Vault purge protection retains a deleted vault for 90 days and can prevent
immediate recreation under the same name. Recover the vault or use a new
environment name rather than attempting to bypass protection.

## Verification and troubleshooting

The signed-in **API reference** lists only the 15 remaining runtime GitHub
endpoints, with an invocation map and expandable technical details in
English/Italian. It is documentation, not a live request log or permission
audit, and makes no GitHub calls. Installation discovery needs read-only
access; installation creation/update is an administrator responsibility.

The UI defaults to English. The top-bar language dropdown switches between
English and Italian and persists the preference in a one-year, host-only
Secure/HttpOnly/SameSite=Lax culture cookie. It is available before sign-in,
uses an antiforgery-protected POST, and redirects only to local pages.
Without a valid preference, English is used regardless of browser language.
ASP.NET Core `RequestLocalization`, `IStringLocalizer` and DataAnnotations
localization load the standard `.resx` resources under
`src/BudgetPortal/Resources`. Adding translations requires matching resource
keys; adding another language also requires updating the supported cultures
and selector allowlist. Dates and numeric display follow the selected
culture; budget currency remains USD and API identifiers/payloads are unchanged.

Cost-center resource types `Org` and `User` are normalized before authorization checks. Only nonempty user-only centers are budget destinations: every assigned user must have current membership and a Copilot seat in at least one owned organization, including users who are also members of unowned organizations. All active center compositions remain necessary for attribution and isolation. Organization membership alone does not revoke individual or user-only center eligibility. Direct user assignments to unauthorized/ambiguous centers and unknown resource attribution remain subject to isolation checks. Name uniqueness and live checks are required. Organization-containing centers, empty centers, teams, repositories, unknown resource types and unauthorized users remain excluded as default destinations. The portal manages only cost-center per-user defaults and individual overrides; both cover included and additional consumption and require usage stopping.

GitHub requires a per-user cost-center budget to contain at least one user or team and only users or teams, not organizations or repositories. The portal currently supports the user-only subset because enterprise team membership is not yet verified. See [GitHub's cost-center per-user budget announcement](https://github.blog/changelog/2026-07-07-per-user-budgets-for-cost-centers-in-the-billing-ui/) and [budgets for usage-based billing](https://docs.github.com/en/enterprise-cloud@latest/copilot/concepts/billing-and-usage/organizations-and-enterprises/budgets). The UI explains this on scope selection and on failed per-user submissions; incompatible organization centers are excluded from per-user destinations and rejected before a write.

For `multi_user_cost_center` default writes, `budget_entity_name` carries the immutable cost-center ID, not its display name. GitHub may return the display name when reading budgets; the portal resolves it against the live inventory before authorization and duplicate checks. Sending the display name on creation can return HTTP 404 even when cost-center discovery succeeds.

If the login button stays on "Working..." and the browser reports a `form-action` CSP violation, deploy the current application binaries with `azd deploy`. The CSP permits same-origin forms and redirects to `https://login.microsoftonline.com` for Entra sign-in/sign-out; it does not allow arbitrary external form destinations. Entra callback registration is still required separately.

If Key Vault references report `AccessToKeyVaultDenied`, check the private endpoint connection is `Approved`, the app is integrated with the provisioned VNet, and the vault hostname resolves to its private IP from the app. A subscription policy may enforce disabled vault public access; do not enable public access or bypass that policy. Reapply this template with `azd up`, then verify reference resolution and `/healthz`. A successful ARM deployment alone does not prove private DNS, managed identity propagation, or application startup.

Offline checks (no Azure login needed):

```sh
python3 -m unittest discover -s scripts/tests -v
bicep build infra/main.bicep --stdout > /dev/null
```

After an authorized deployment:

The `postdeploy` hook automatically polls the provisioned HTTPS `/healthz` endpoint for up to five minutes (at most 60 attempts, five-second pauses and ten-second request timeouts) to accommodate startup and Key Vault propagation. Only HTTP 200 with the health-check body `Healthy` succeeds; redirects, sign-in/placeholder HTML and unhealthy responses do not. Persistent failure makes `azd deploy` fail explicitly with startup/configuration troubleshooting guidance, without printing HTTP response bodies. After correcting a failure, rerun `python3 scripts/deploy.py verify` or `azd deploy`. This health check verifies startup/readiness, not live GitHub billing authorization; complete the checks below.

1. Confirm App Service runs `DOTNETCORE|10.0`, HTTPS-only and one worker; resolve all Key Vault references before testing the portal.
2. Verify Entra callbacks, sign-in, sign-out, SCIM identity resolution, and denial for suspended or unknown EMU accounts. Confirm restarts preserve cookies through the `/home` key ring.
3. Confirm discovery selects only administrator-installed organizations belonging to the configured enterprise, using read permissions only. Verify excluded organizations are not queried for ownership/members/seats and cannot authorize centers. Missing enterprise/no in-enterprise organization installations or suspended in-scope installations must produce explicit errors. Organization tokens must read membership and Copilot seats.
4. Verify non-owner organizations, ambiguous cost centers, users without membership and a seat in an owned organization, and over-limit amounts are denied server-side. Verify shared licensed members appear as individual destinations and their budgets can be managed, with the global-impact warning visible.
5. Check Application Insights and Log Analytics, without exposing credentials or configuration in logs.

A failure in registration, Azure resource policy, quota, Key Vault reference resolution or runtime availability is not silently bypassed. The application should fail closed on missing/unresolved secret references. For partial provisioning, correct the prerequisite and rerun the same environment; do not delete a working Entra app or erase existing redirects as a workaround.

## Approval and operational limits

Use an approved evaluation environment and authorized test resources for
live checks. Budget saves/deletions are real mutations, not a dry run.

The initial topology has one App Service instance, public portal/SCM
endpoints, no WAF and a filesystem data-protection key ring. Before adopting
it beyond evaluation, review network access, encrypted/shared key storage,
credential access, API limits, monitoring, incident response, costs and
retention with the responsible teams.

The App's enterprise write permissions remain broad. The service implements
delegated authorization, but that is not an independent GitHub-issued
per-user permission boundary. A compromised backend or App key has wider
impact. Do not grant portal users access to deployment state, Key Vault,
process diagnostics or privileged logs just because they are GitHub Owners.

Access checks and mutations are separate API calls. Fingerprints are
optimistic checks, not atomic GitHub transactions; interruption can leave
the result uncertain. Reload before retrying. Consumption can lag, and the
per-budget ceiling is not an aggregate cap or a guarantee against overspend.

Read the [architecture limitations](architecture.md#caching-audit-and-limitations)
and record the adopting organization's approval outside the portal.
