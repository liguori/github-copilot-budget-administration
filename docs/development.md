# Development and testing

[Back to README](../README.md) | [Architecture](architecture.md) |
[Azure installation](deployment-step-by-step.md)

This is an independent PoC. The
[disclaimer](../README.md#disclaimer-and-adoption-responsibility) applies to
local runs as well: real GitHub configuration means real budget operations.

## Prerequisites

- .NET 10 SDK. [global.json](../global.json) permits installed .NET 10 feature
  bands through `latestFeature` roll-forward.
- A trusted development HTTPS certificate.
- Approved Entra/GitHub test resources for a real integration run.
  No enterprise credentials are needed for the synthetic preview.

Run commands from the project directory.
The examples below use PowerShell.

```powershell
dotnet restore .\CopilotBudgetPortal.slnx
dotnet dev-certs https --trust
```

## Synthetic UI preview

```powershell
dotnet run --project .\tests\BudgetPortal.Web.Tests -- --preview
```

Open `https://localhost:7443`.
The preview is loopback-only and uses synthetic identity/budgets.
Writes are simulated and do not persist.

The preview host and fake authentication exist **only in the test project**.
They are not an authentication bypass or mode of the deployed application,
and `azd` does not publish them.

## Run against a real test enterprise

First complete the Entra registration, EMU provisioning and GitHub App
prerequisites in the [installation guide](deployment-step-by-step.md).
Use approved test resources: saving can change real budgets.
The administrator must install the App before opening the dashboard.

Configure secrets with `dotnet user-secrets --project .\src\BudgetPortal` or
the ignored `src/BudgetPortal/appsettings.Local.json`.
Do not commit either secrets or a PEM.
The local application schema is:

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
      "PrivateKeyPem": "<RSA-PRIVATE-KEY-PEM-WITH-NEWLINES>"
    },
    "MaxBudgetAmount": 1000
  }
}
```

For GitHub.com, use `https://api.github.com/`.
Use JSON escapes such as `\n` when putting PEM newlines in a JSON string.

**Application configuration uses `PrivateKeyPem`.** The Azure deployment
input instead uses `PrivateKeyPemFile`; its hook reads that file and
constructs the application configuration.

Register these as Entra **Web** callback URIs:

```text
https://localhost:7443/signin-oidc
https://localhost:7443/signout-callback-oidc
```

Then run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\BudgetPortal --urls https://localhost:7443
```

`Development` enables the standard user-secrets provider.
The optional local JSON is read in other environments too, but is excluded
from publication by the project.
In Azure, use the documented Key Vault references.
Do not enable verbose credential/request logging or expose a local debug
session publicly.

## Build and test

```powershell
dotnet build .\CopilotBudgetPortal.slnx --configuration Release
dotnet test .\CopilotBudgetPortal.slnx --configuration Release
python -m unittest discover -s .\scripts\tests -v
```

On Linux/macOS, use corresponding paths and `python3`.

| Tests | What they exercise |
| --- | --- |
| [Backend tests](../tests/BudgetPortal.Tests) | Real budget-service logic with simulated GitHub transport: read-only installation discovery, payloads, authorization, isolation, revocation, conflicts, bounded parallel loading and rate-limit cooldowns. |
| [Web tests](../tests/BudgetPortal.Web.Tests) | Razor HTTP pipeline with test authentication and a fake budget service: access requirements, antiforgery, forms, errors, localization and the read-only API reference. |
| [Deployment tests](../scripts/tests) | Local configuration hooks and infrastructure contracts, without deploying Azure resources. |

These layers have different boundaries. Web tests with a fake budget service
do not independently prove the production service's target authorization.
Backend tests do not independently prove the entire browser-to-service
pipeline. A complete adversarial HTTP integration matrix across create,
update and delete is not claimed by this PoC.

The backend scale regression covers twelve organizations with 250 members
and seats each, and ten organizations with 200 each. Each organization has
a paginated user-only cost center and eleven budgets. It verifies distinct
eligible users, complete inventories, unchanged budget records and a maximum
of four transport requests in flight across two simultaneous warm-cache
dashboard reads. With 20 ms injected per read, elapsed time must be below
65% of the cumulative sequential injected delay. This is
a controlled I/O regression, not a benchmark of live GitHub or an App Service
capacity guarantee. Transport tests also cover queued cancellation,
installation-specific cooldown expiry and the absence of write retries.

The ten-organization case requires 75 calls per full dashboard, 54 for an
individual-budget edit/delete confirmation, 57 for its update (including two
budget-list pages), and 55 for its delete. Tests assert exact counts and the
absence of unnecessary center calls on individual-budget paths. New-budget
destination reads omit budgets and foreign member contents. Targeted paths
also test identity/owner/member/seat revocation, foreign center composition,
unsupported scopes, mid-load retargeting, missing budgets, incomplete
inventories and absence of writes on authorization failure.

The supported domain scopes retain numeric values `2` (cost-center per-user
default) and `3` (individual override). Unknown scope names/values, including
numeric `1`, are rejected rather than remapped. Unsupported upstream budgets
are neither displayed nor writable. Tests cover direct reads, update/delete
denial and forged submissions disabling the mandatory usage stop.

API reference tests extract method/path pairs from the runtime GitHub
transport call sites and compare them with the catalog. Unrecognized route
construction fails the check rather than silently omitting an endpoint.
They also verify endpoint-specific authentication/permissions, complete
invocation-map coverage, English/Italian content, active navigation and
zero GitHub/budget-service calls while opening the reference.

Discovery regressions cover a partially installed enterprise: uninstalled
organizations do not block in-scope CRUD, grant Owner access, expose users
or authorize centers. Their logins remain in the complete enterprise
inventory, so known-but-unauthorized center resources are distinguished from
unknown attribution. Foreign direct user assignments still restrict center
defaults without revoking eligible enterprise-global individual overrides.
Other-enterprise installations cannot satisfy discovery, and suspended
in-scope installations still fail closed.

Passing local tests does not prove tenant consent, App permission approval,
subscription-policy compatibility, runtime availability, API behavior,
residency compliance or production readiness. Complete your organization's
approved live acceptance checks separately.

## Implementation conventions

- Keep authorization and payload construction in the budget service.
  Do not rely on a destination being present in a dropdown.
- Reuse the GitHub client and its safe error translation.
  Never expose tokens, keys, raw GitHub error bodies or configuration.
- Maintain English and Italian strings in
  [SharedResource.resx](../src/BudgetPortal/Resources/SharedResource.resx) and
  [SharedResource.it.resx](../src/BudgetPortal/Resources/SharedResource.it.resx).
  Use matching keys and format placeholders.
- New UI languages require updating supported cultures and the selection
  allowlist, not just adding a resource file.
- Update the architecture and installation guides when runtime permissions
  or authorization rules change.
- Keep [the API catalog](../src/BudgetPortal/Pages/ApiReference.cshtml.cs),
  invocation map and localized details aligned with runtime endpoints and
  operation-specific loading. Run the API reference and budget-service tests.
- Add tests for both permitted and denied paths, including absence of
  outbound budget writes when authorization fails.

Razor views are compiled during build/publish.
The project disables the Razor source generator to avoid the attribute-span
mapping regression observed with SDK 10.0.401; it does not enable runtime view
compilation.

Before publishing a fork or sharing diagnostics, review tracked files and Git
history for private deployment material. Git ignore rules do not remove data
already committed. Choose and include an appropriate software license before
distributing the project; making a repository public is not itself a license.
