# Step-by-step installation

[Back to README](../README.md) | [Architecture](architecture.md) |
[Operations reference](deployment.md)

Deploy the Copilot Budget Administration PoC to Azure App Service, with:

- One enterprise-owned GitHub App.
- Administrator-managed enterprise and organization installations.
- Entra sign-in and SCIM-based EMU identity resolution.
- No manually maintained user mapping or installation-ID list.

> [!WARNING]
> This is an independent PoC, not affiliated with or supported by GitHub or
> Microsoft. Obtain your organization's approval before deployment and use.
> Installation permissions and budget writes have real enterprise effects.
> Start in an approved evaluation environment and read the
> [disclaimer and adoption responsibilities](../README.md#disclaimer-and-adoption-responsibility).

## Before you start

You need GitHub Enterprise Cloud with EMU, working Entra provisioning and
access to the required enterprise GitHub App, SCIM and billing APIs.
Both GitHub.com and a data-resident `ghe.com` enterprise are supported;
GitHub Enterprise Server is not.

Prepare:

| Input | What to supply |
| --- | --- |
| Entra tenant ID | Your tenant GUID. |
| Entra application client ID | The application registration GUID, not its service-principal object ID. |
| Entra client secret | The secret **value**, not its ID. |
| Azure subscription | In the same tenant as the Entra application. |
| Approved Azure region | For example, `italynorth`, subject to your requirements and service availability. |
| GitHub API origin | `https://api.github.com/` or `https://api.YOUR-SUBDOMAIN.ghe.com/`. |
| Enterprise slug | The slug used by the enterprise REST routes. |
| GitHub App ID | The numeric App ID, not its OAuth client ID. |
| GitHub App private key | An unencrypted RSA PEM, at least 2048 bits. |
| Maximum monthly budget | A whole USD amount for each created or updated budget. |

Separate responsibilities:

- **Azure operator:** resource-group/resource creation and role-assignment
  permissions, for example Owner, or Contributor plus Role Based Access
  Control Administrator.
- **Entra administrator:** application registration, service principal,
  credential, callbacks, user assignment and Conditional Access.
- **GitHub enterprise administrator:** App registration, permission approval,
  enterprise and organization installations, EMU provisioning and
  test-resource approval.

This template uses Key Vault access policies. If your subscription mandates
RBAC-only vaults or other controls, review and adapt the infrastructure before
deployment rather than bypassing policy. See the [deployment contract](deployment.md#infrastructure-contract).

## 1. Install the tools

Install Git, the .NET 10 SDK, Azure CLI (`az`), Azure Developer CLI (`azd`),
Python 3.10+, and PowerShell 7 on Windows.

Commands below use PowerShell and Windows paths. On Linux/macOS, use the
corresponding shell/path syntax and `python3` instead of `python`.

```powershell
git --version
dotnet --version
az version
azd version
python --version
pwsh --version
```

Open a terminal in the project directory containing `azure.yaml`.
If you cloned a repository containing this project as a subfolder, change
into that subfolder first.

Verify that your Azure cloud offers the required App Service runtime:

```powershell
az webapp list-runtimes --os linux `
  --query "[?contains(@, 'DOTNETCORE|10.0')]"
```

If .NET 10 is unavailable, stop and review an alternative hosting setup.
The template does not silently downgrade the runtime.

## 2. Register the Entra application manually

In the Microsoft Entra admin center:

1. Open **Identity > Applications > App registrations > New registration**.
2. Enter a name such as `Copilot Budget Portal`.
3. Select **Accounts in this organizational directory only**.
4. Create the registration without a redirect URI for now.
5. Record **Application (client) ID** and **Directory (tenant) ID**.
6. Verify its service principal appears in **Enterprise applications**.
7. Open **Certificates & secrets > Client secrets**.
8. Create a secret with an expiry allowed by your organization.
9. Immediately copy its **Value** for the protected local configuration in
   step 7.

Configure user assignment and Conditional Access according to your policies.
Keep implicit access-token and ID-token grants disabled.
No Microsoft Graph application permission is required by this portal.
The deployment does not create or modify Entra resources.

Callbacks are registered in step 12, once the Azure hostname is known.

## 3. Verify EMU provisioning

Your Entra-to-GitHub EMU provisioning must expose the immutable Entra object ID
as the GitHub SCIM `externalId`.

Verify this for an approved test user:

```text
Entra oid claim == GitHub EMU SCIM externalId
```

This is an identity-provider provisioning requirement, **not a manual mapping
file in the portal**.

The portal requires exactly one active matching EMU. It resolves the canonical
GitHub login from a validated SCIM `profileUrl`; `userName` is a fallback only
when `profileUrl` is absent and the account lookup confirms the login.
Email and display name are not used as authorization identities.

Reference: [GitHub EMU OIDC provisioning with Entra](https://learn.microsoft.com/entra/identity/saas-apps/github-enterprise-managed-user-oidc-provisioning-tutorial).

## 4. Register the enterprise-owned GitHub App

Use the correct GitHub host and register the App **as owned by the enterprise**.

| General setting | Value |
| --- | --- |
| Name | For example, `copilot-budget-portal`. |
| Homepage URL | An informational URL; update it after deployment if needed. |
| Webhooks | Disabled. |
| Repository permissions | None requested by the portal. |

Select **all** of these permissions:

| Permission scope | Permission | Access |
| --- | --- | --- |
| Enterprise | Enterprise billing / Billing | Read and write |
| Enterprise | Enterprise organization installations | Read-only |
| Enterprise | Enterprise SCIM | Read-only |
| Organization | Members | Read-only |
| Organization | **GitHub Copilot Business** / Copilot seat management | Read-only |

**GitHub Copilot Business** is the label used in the GitHub App permission UI
for reading Copilot seats; it is also referred to as Copilot seat management.
The portal does not require license-assignment write permission.

The enterprise token alone cannot perform the organization membership checks.
Organization permissions become usable through the organization installations
created by the administrator.

Record the **numeric App ID**. Do not substitute the OAuth client ID.

References:

- [Register a GitHub App](https://docs.github.com/en/enterprise-cloud@latest/apps/creating-github-apps/registering-a-github-app/registering-a-github-app).
- [GitHub App permissions](https://docs.github.com/en/enterprise-cloud@latest/rest/authentication/permissions-required-for-github-apps).

## 5. Generate and protect the App private key

In the App settings, open **Private keys > Generate a private key**.
Download the PEM and store it outside the repository, for example:

```text
C:\Secrets\copilot-budget-portal.private-key.pem
```

Use an unencrypted RSA PEM of at least 2048 bits.
Restrict access with Windows ACLs or equivalent operating-system permissions.
Never commit it, include it in screenshots, or send it through email/chat.

The key can mint App credentials. Possession of this key is much more
privileged than being a portal user.

## 6. Install the App on the enterprise and organizations

Manually install the App **at enterprise level and in the organizations
you want to include in the portal**, approving the required permissions.
At least one of those organizations must belong to the configured enterprise.
Do not request repository permissions. Other enterprise organizations can
remain uninstalled and are outside portal scope.

At the first budget-data request, the backend:

1. Finds the enterprise installation.
2. Lists the enterprise-owned organizations.
3. Selects existing organization installations belonging to the configured
   enterprise; organizations without the App are excluded. The complete
   enterprise login inventory remains available for center isolation.
4. Caches successful discovery for ten minutes per application instance.
5. Uses organization tokens to verify where the signed-in person is an
   active Owner.

No installation IDs need to be configured. All installations are managed by
the administrator; the portal only reads and validates them.

If installation errors persist, distinguish a missing enterprise installation,
no installed organization in that enterprise and a suspended in-scope
installation. These are explicit errors. An App installed only in another
enterprise does not satisfy the requirement. Successful discovery is cached
for ten minutes; adding/removing an organization installation takes effect
after expiry or an application restart. Failed discovery is not cached.

> [!IMPORTANT]
> If you change the App's permission definition, approve the new permissions
> on **existing enterprise and organization installations** as well.
> Updating the definition alone does not complete that approval.

Enterprise App installation APIs may be in preview and can change; verify
their availability for your enterprise.

Reference: [Enterprise installation](https://docs.github.com/en/enterprise-cloud@latest/apps/using-github-apps/installing-a-github-app-on-your-enterprise).

## 7. Prepare the local deployment configuration

From the project directory:

```powershell
Copy-Item .\scripts\deployment.example.json .\scripts\deployment.local.json
```

Replace the example values in `scripts/deployment.local.json`:

```json
{
  "Entra": {
    "TenantId": "<TENANT-GUID>",
    "ClientId": "<ENTRA-APPLICATION-CLIENT-GUID>",
    "ClientSecret": "<ENTRA-CLIENT-SECRET-VALUE>"
  },
  "Portal": {
    "GitHub": {
      "ApiBaseUrl": "https://api.YOUR-SUBDOMAIN.ghe.com/",
      "ApiVersion": "2026-03-10",
      "Enterprise": "<ENTERPRISE-SLUG>",
      "AppId": "<NUMERIC-GITHUB-APP-ID>",
      "PrivateKeyPemFile": "C:\\Secrets\\copilot-budget-portal.private-key.pem"
    },
    "MaxBudgetAmount": 1000
  }
}
```

For GitHub.com, use `https://api.github.com/`.
For Data Residency, use your dedicated API subdomain without additional paths.
`PrivateKeyPemFile` can also be relative to the configuration file.

`MaxBudgetAmount` is a **per-budget** whole monthly USD ceiling, not an
aggregate departmental cap. Zero permits only zero-amount creation/updates.
The configuration supports nonnegative signed 32-bit integer amounts.

Check that local state is ignored:

```powershell
git check-ignore scripts\deployment.local.json
git status --short
```

**The JSON contains a plaintext Entra secret.** Restrict local access.
Git exclusion does not protect the workstation.
Never add this JSON, PEMs, `.azure/`, secret exports or deployment transcripts
to source control. Avoid CLI debug logging with real credentials.

## 8. Validate the configuration

```powershell
python .\scripts\deploy.py validate --config .\scripts\deployment.local.json
```

Validation checks the tenant/client IDs, secret presence, API origin, App ID,
API date, amount ceiling, PEM delimiters and serialized configuration size.
The application additionally parses the key and checks its RSA strength.
Local validation does not prove API permissions or Azure policy compliance.
Resolve validation failures before proceeding.

## 9. Sign in to Azure

```powershell
az login --tenant <TENANT-GUID>
azd auth login
```

Azure CLI and Azure Developer CLI have separate sign-ins.
Use a subscription in the same tenant as the Entra application.

## 10. Create the deployment environment

```powershell
azd env new budget-portal-poc
azd env set PORTAL_DEPLOYMENT_CONFIG scripts/deployment.local.json
```

The local `.azure/` state contains sensitive plaintext values.
Use a trusted administrator workstation/runner and protect its files.
Do not share `azd env get-values` output.

GitHub Data Residency does not automatically cover Azure hosting, logs or
audit data. Select an approved Azure region and retention/access policies.

## 11. Deploy

```powershell
azd up
```

Hooks load and validate the JSON, read the PEM and prepare secret inputs.
Do not pre-escape secret values or manually populate the azd secret variables.
The hooks handle JSON escaping for Bicep parameter substitution.

When not already configured, select the appropriate subscription and region
in the native azd prompts.

The template creates:

- A dedicated resource group and Linux App Service.
- Key Vault with public access disabled.
- A VNet, integration/private-endpoint subnets, NSGs and private DNS.
- Managed-identity secret access, Application Insights and Log Analytics.

App Service uses VNet integration to resolve private Key Vault references.
Private endpoints and DNS add costs; network-resource permissions are required.
The web and SCM endpoints remain public: this is **not** a complete production
network/security baseline.

For new environments, a tenant mismatch can be detected after provisioning
and stop deployment without removing resources already created. Choose the
correct subscription from the start.

The postdeploy hook checks `/healthz`; GitHub discovery is not run by that
health check.

## 12. Register the Entra callbacks

Retrieve the deployed URL:

```powershell
azd env get-value AZURE_WEB_URL
```

In the Entra application from step 2, open **Authentication > Add a platform >
Web**, register both URLs, and save:

```text
https://<APP-HOST>/signin-oidc
https://<APP-HOST>/signout-callback-oidc
```

The provisioning hook also prints the callback URLs.
Keep implicit grants disabled.
Registration is manual and required before sign-in; `/healthz` does not test it.

## 13. Open the portal

Open the `AZURE_WEB_URL` value and choose **Sign in with Microsoft Entra ID**.
The first dashboard load can take longer because existing installations
and authorization inventories are being discovered.

The default language is **English**. Select **Italiano** in the top bar to
switch language. JavaScript applies the selection automatically; **Apply** is
the no-JavaScript fallback. The browser preference lasts one year.
Amounts remain USD regardless of language.

Open **API reference** after sign-in for the schematic inventory of the 15
runtime GitHub endpoints. Its invocation map explains when reads, token
issuance and confirmed budget changes happen. Expand an endpoint to inspect
permissions, authentication, payloads and official documentation.
Opening this page does not call GitHub or discover installations.

## 14. Perform approved acceptance checks

> [!WARNING]
> Saving or deleting through the real portal changes real GitHub budgets.
> Use approved test resources and coordinate with affected Owners, especially
> for shared users whose budget is global. The synthetic local preview is
> described in the [development guide](development.md#synthetic-ui-preview).

### Identity and authorization

- An active EMU with matching `externalId = oid` and an active Organization
  Owner role can load the dashboard.
- Unknown, inactive or suspended EMUs cannot access budget data.
- A provisioned user with no active Owner role is denied budget access.
- The App appears in the discovered enterprise organizations.
- Only authorized cost centers and licensed members of owned organizations
  are exposed as budget destinations.
- Licensed current members are eligible even if they also belong to other
  organizations; users lacking membership/seat in an owned organization are
  rejected.
- Forged requests for foreign targets, budget IDs or unsupported scopes are
  denied by the server, not merely hidden in the UI.

### Cost centers and budget changes

- Organization-containing centers do not appear as budget destinations.
- Nonempty user-only centers are eligible for defaults when every assigned
  user has membership and a Copilot seat in an owned organization.
- Shared membership alone does not exclude a user-only center.
  Direct foreign/ambiguous assignments remain subject to isolation checks.
- Empty centers and centers containing teams, repositories, unknown resources
  or unauthorized users/organizations are excluded.
- Creation/update within `MaxBudgetAmount` succeeds for an approved target;
  an over-limit amount is rejected.
- Only cost-center per-user defaults and individual overrides are managed.
  Both limit included and additional consumption per user and always stop
  usage at the budget. Forged submissions disabling stopping are rejected.
- Update/delete cannot be redirected to another target by changing the form.
- The resulting approved change is visible in GitHub billing.

GitHub permits per-user centers of users or teams, but this portal currently
supports only verified **direct users**. An organization-containing center
cannot be used for a per-user default.
For an existing budget, edit rather than recreate it.
See the [integration details](architecture.md#rest-integration-details) for
cost-center UUID writes and duplicate handling.

### Operations

- Sign-out and subsequent sign-in work.
- Language selection persists and budget pages/API reference render in both
  languages. The reference does not load operational data.
- Application Insights and Log Analytics receive the expected telemetry
  without credentials or configuration payloads.
- Restart/redeploy behavior is checked in the evaluation environment.

You can repeat the startup check:

```powershell
python .\scripts\deploy.py verify
```

`/healthz` proves application startup, **not** GitHub authorization or production
readiness. Record your organization's acceptance and approval separately;
there is no deployment-approval workflow built into the portal.

## Update, rotate or remove

| Task | Command / action |
| --- | --- |
| Code-only update | `azd deploy` |
| Configuration or secret update | Edit protected inputs, then `azd up`; see [rotation guidance](deployment.md#secrets-updates-and-rotation). |
| Remove Azure resources | `azd down`, only after approval and data/retention review. |

`azd down` does not remove Entra registrations, credentials or GitHub App
installations. A purge-protected deleted vault can block immediate recreation
with the same name; see [cleanup guidance](deployment.md#cleanup).

## Installation checklist

- [ ] PoC use and required App permissions approved; enterprise and selected
      organization installations completed by the administrator.
- [ ] EMU provisioning verified: SCIM `externalId = oid`.
- [ ] Single-tenant Entra registration, service principal and credential created.
- [ ] Enterprise-owned GitHub App registered with all five required permissions.
- [ ] Enterprise installation and any permission updates approved.
- [ ] Private key and local JSON protected and excluded from Git.
- [ ] Configuration validation completed.
- [ ] Correct Azure tenant, subscription and approved region selected.
- [ ] `azd up` completed and private Key Vault references resolved.
- [ ] Both Entra callbacks registered manually.
- [ ] Identity, Owner, membership, seats and cost-center rules tested.
- [ ] Approved budget changes and rejection cases tested.
- [ ] Telemetry, retention, costs, rotation and incident ownership reviewed.
- [ ] Adopting organization has recorded its acceptance decision.
