#!/usr/bin/env python3
"""azd hooks. Configuration and CLI credential output are never printed."""

import argparse
import copy
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid
from urllib.parse import urlsplit
from urllib.error import URLError
from urllib.request import build_opener, HTTPRedirectHandler, Request

ROOT = Path(__file__).resolve().parents[1]
MAX_SECRET_BYTES = 25000
MAX_INT64 = 9223372036854775807
MAX_INT32 = 2147483647


class DeploymentError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise DeploymentError(message)


def run(command, guidance):
    """Suppress CLI stdout/stderr, including secrets and request bodies on error."""
    executable = shutil.which(command[0])
    require(executable, guidance)
    try:
        result = subprocess.run([executable, *command[1:]], cwd=ROOT, text=True, capture_output=True, check=False)
    except OSError:
        raise DeploymentError(guidance) from None
    if result.returncode:
        raise DeploymentError(guidance)
    return result.stdout.strip()


def az_json(arguments, guidance):
    output = run(["az", *arguments, "--only-show-errors", "--output", "json"], guidance)
    try:
        return json.loads(output) if output else {}
    except ValueError:
        raise DeploymentError("Azure CLI returned an invalid response; update Azure CLI and retry.") from None


def environment():
    raw = run(["azd", "env", "get-values", "--output", "json"], "Select an azd environment with azd env new/select.")
    try:
        result = json.loads(raw)
        require(isinstance(result, dict), "azd environment must be a JSON object.")
        return result
    except ValueError:
        raise DeploymentError("Update azd: azd env get-values --output json must be supported.") from None


def save_env(name, value):
    # azd manages quoting/escaping multiline and JSON values. Never echo this command.
    run(["azd", "env", "set", name, str(value)], f"Could not save deployment setting {name}.")
    secure_local_state()


def secure_local_state():
    azure = ROOT / ".azure"
    if not azure.exists() or os.name == "nt":
        return
    for path in [azure, *azure.rglob("*")]:
        if not path.is_symlink():
            path.chmod(0o700 if path.is_dir() else 0o600)


def guid(value, field):
    try:
        require(isinstance(value, str) and re.fullmatch(
            r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", value) is not None,
            f"{field} must be a nonempty canonical UUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).")
        parsed = uuid.UUID(value)
        require(parsed.int != 0, f"{field} cannot be the empty UUID.")
        return str(parsed)
    except (ValueError, TypeError, AttributeError):
        raise DeploymentError(f"{field} must be an immutable UUID, not an email or display name.") from None


def positive_id(value, field):
    require(not isinstance(value, bool) and re.fullmatch(r"[1-9][0-9]*", str(value)) is not None,
            f"{field} must be a positive integer.")
    number = int(value)
    require(number <= MAX_INT64, f"{field} must fit a positive signed 64-bit integer.")
    return number


def required_text(value, field):
    require(isinstance(value, str) and bool(value.strip()), f"{field} must be a nonempty string.")
    return value.strip()


def github_name(value, field, managed_user=False, max_length=100):
    name = required_text(value, field)
    pattern = r"[A-Za-z0-9](?:[A-Za-z0-9_-]*[A-Za-z0-9])?" if managed_user else r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?"
    require(len(name) <= max_length and re.fullmatch(pattern, name) is not None,
            f"{field} must be a GitHub slug/login of at most {max_length} characters using letters, digits and internal hyphens"
            + (" (managed-user underscores are permitted)." if managed_user else "."))
    return name


def github_origin(value):
    origin = required_text(value, "GitHub.ApiBaseUrl")
    message = ("GitHub.ApiBaseUrl must be https://api.github.com/ or "
               "https://api.<single-subdomain>.ghe.com/, with no credentials, path, query, "
               "fragment or nondefault port.")
    try:
        api = urlsplit(origin)
        hostname = api.hostname or ""
        allowed = hostname == "api.github.com" or re.fullmatch(
            r"api\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.ghe\.com", hostname) is not None
        require(api.scheme == "https" and allowed and api.port in (None, 443)
                and api.username is None and api.password is None
                and api.path in ("", "/") and not api.query and not api.fragment
                and not any(c.isspace() for c in origin) and "?" not in origin and "#" not in origin,
                message)
    except ValueError:
        raise DeploymentError(message) from None
    return f"https://{hostname}/"


def read_private_file(path, base, field):
    file = Path(required_text(path, field)).expanduser()
    if not file.is_absolute():
        file = base / file
    try:
        return file.read_text(encoding="utf-8").strip()
    except (OSError, UnicodeError):
        raise DeploymentError(f"Cannot read {field}; check the configured file path and permissions.") from None


def validate_configuration(document, base):
    require(isinstance(document, dict), "Deployment configuration must be a JSON object.")
    entra = copy.deepcopy(document.get("Entra"))
    portal = copy.deepcopy(document.get("Portal"))
    require(isinstance(entra, dict) and isinstance(portal, dict), "Configuration requires Entra and Portal objects.")
    entra["TenantId"] = guid(entra.get("TenantId"), "Entra.TenantId")
    entra["ClientId"] = guid(entra.get("ClientId"), "Entra.ClientId")
    secret = entra.get("ClientSecret")
    require(isinstance(secret, str) and bool(secret.strip()),
            "Entra.ClientSecret must contain the client secret value.")
    github = portal.get("GitHub")
    require(isinstance(github, dict), "Portal.GitHub is required.")
    github["ApiBaseUrl"] = github_origin(github.get("ApiBaseUrl"))
    version = github.get("ApiVersion", "2026-03-10")
    require(isinstance(version, str) and re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", version) is not None,
            "GitHub.ApiVersion must be a valid YYYY-MM-DD date.")
    try:
        datetime.date.fromisoformat(version)
    except ValueError:
        raise DeploymentError("GitHub.ApiVersion must be a valid YYYY-MM-DD date.") from None
    github["ApiVersion"] = version
    github["Enterprise"] = github_name(github.get("Enterprise"), "GitHub.Enterprise")
    github["AppId"] = str(positive_id(github.get("AppId"), "GitHub.AppId"))
    key = read_private_file(github.pop("PrivateKeyPemFile", None), base, "GitHub.PrivateKeyPemFile")
    require(re.search(r"-----BEGIN (RSA )?PRIVATE KEY-----", key) is not None
            and re.search(r"-----END (RSA )?PRIVATE KEY-----", key) is not None,
            "GitHub.PrivateKeyPemFile must contain an unencrypted PEM private key.")
    github["PrivateKeyPem"] = key
    require(isinstance(portal.get("MaxBudgetAmount"), int) and not isinstance(portal["MaxBudgetAmount"], bool)
            and 0 <= portal["MaxBudgetAmount"] <= MAX_INT32,
            "Portal.MaxBudgetAmount must be a nonnegative signed 32-bit whole currency-unit amount.")
    serialized = json.dumps(portal, separators=(",", ":"))
    require(len(serialized.encode()) <= MAX_SECRET_BYTES, "Portal configuration exceeds the Key Vault secret size limit.")
    return entra, serialized


def load_configuration(values):
    config_path = os.environ.get("PORTAL_DEPLOYMENT_CONFIG") or values.get("PORTAL_DEPLOYMENT_CONFIG")
    if not config_path:
        require(sys.stdin.isatty(), "Set PORTAL_DEPLOYMENT_CONFIG to your administrator-managed JSON file before provisioning.")
        config_path = input("Path to administrator deployment JSON (see scripts/deployment.example.json): ").strip()
    path = Path(config_path).expanduser().resolve()
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError):
        raise DeploymentError("Cannot read deployment JSON; check its path and JSON syntax. Contents were not logged.") from None
    return path, validate_configuration(document, path.parent)


def escape_parameter_value(value):
    """azd substitutes variables into JSON text before parsing the parameter file."""
    return json.dumps(value)[1:-1]


def prepare():
    values = environment()
    path, (entra, portal) = load_configuration(values)
    save_env("PORTAL_DEPLOYMENT_CONFIG", path)
    subscription = values.get("AZURE_SUBSCRIPTION_ID") or os.environ.get("AZURE_SUBSCRIPTION_ID")
    if subscription:
        validate_subscription_tenant(subscription, entra["TenantId"])
    save_env("ENTRA_TENANT_ID", entra["TenantId"])
    save_env("ENTRA_CLIENT_ID", entra["ClientId"])
    save_env("ENTRA_CLIENT_SECRET", escape_parameter_value(entra["ClientSecret"]))
    save_env("PORTAL_CONFIGURATION_JSON", escape_parameter_value(portal))
    print("Manual Entra application configuration validated and loaded. "
          "Secrets are saved in local .azure state; do not share it.")


def validate_subscription_tenant(subscription, tenant_id):
    account = az_json(["account", "show", "--subscription", subscription],
                      "Azure CLI cannot access the selected subscription. Run az login for the target tenant.")
    require(account.get("tenantId", "").lower() == guid(tenant_id, "ENTRA_TENANT_ID"),
            "Azure subscription tenant and Entra.TenantId must match. Select a subscription in the configured tenant.")


def redirects():
    values = environment()
    url = values.get("AZURE_WEB_URL")
    require(isinstance(url, str) and url.startswith("https://"), "Provisioning did not produce AZURE_WEB_URL.")
    web_name = values.get("AZURE_WEB_NAME")
    group = values.get("AZURE_RESOURCE_GROUP")
    require(web_name and group, "Provisioning must produce AZURE_WEB_NAME and AZURE_RESOURCE_GROUP.")
    subscription = values.get("AZURE_SUBSCRIPTION_ID")
    require(subscription, "AZURE_SUBSCRIPTION_ID is missing from the azd environment.")
    validate_subscription_tenant(subscription, values.get("ENTRA_TENANT_ID"))
    print("Register these URLs manually on the Entra application's Web platform before sign-in:")
    print(url + "/signin-oidc")
    print(url + "/signout-callback-oidc")
    # Key Vault references can initially race managed identity propagation. Ask App Service
    # to refresh after provisioning; an unresolved reference still fails application startup.
    az_json(["rest", "--subscription", subscription, "--method", "POST", "--url",
             f"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{group}"
             f"/providers/Microsoft.Web/sites/{web_name}/config/configreferences/appsettings/refresh?api-version=2022-03-01"],
            "Key Vault reference refresh failed. Check managed identity access and rerun python scripts/deploy.py redirects.")


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def verify_health(base_url, timeout_seconds=300, max_attempts=60, interval_seconds=5):
    parsed = urlsplit(base_url)
    require(parsed.scheme == "https" and bool(parsed.hostname) and parsed.username is None
            and parsed.password is None and parsed.port in (None, 443)
            and parsed.path in ("", "/") and not parsed.query and not parsed.fragment,
            "AZURE_WEB_URL must be the provisioned HTTPS origin.")
    request = Request(base_url.rstrip("/") + "/healthz", headers={"Accept": "text/plain"})
    opener = build_opener(NoRedirects())
    deadline = time.monotonic() + timeout_seconds
    for attempt in range(max_attempts):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        try:
            with opener.open(request, timeout=min(10, remaining)) as response:
                if response.status == 200 and response.read(64).strip() == b"Healthy":
                    print("Deployment verified: /healthz reports Healthy over HTTPS.")
                    return
        except (URLError, OSError, TimeoutError):
            pass
        if attempt + 1 < max_attempts:
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(interval_seconds, remaining))
    raise DeploymentError(
        "Postdeployment health verification failed within the retry window. The portal did not return "
        "Healthy from /healthz. Check App Service startup/JSON console logs, .NET 10 runtime, "
        "Entra/Portal configuration, and Key Vault reference resolution. If identity propagation "
        "is pending, run python scripts/deploy.py redirects to refresh references, then "
        "python scripts/deploy.py verify. Deployment is not verified; no authentication bypass was enabled.")


def verify():
    values = environment()
    url = values.get("AZURE_WEB_URL")
    require(isinstance(url, str), "Provisioning did not produce AZURE_WEB_URL.")
    print("Waiting up to five minutes for /healthz (startup and Key Vault propagation).")
    verify_health(url)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "redirects", "verify", "validate"])
    parser.add_argument("--config", help="Local administrator JSON (validate only)")
    args = parser.parse_args()
    try:
        if args.action == "validate":
            require(args.config, "validate requires --config.")
            load_configuration({"PORTAL_DEPLOYMENT_CONFIG": args.config})
            print("Deployment configuration is valid; no credentials or cloud resources were created.")
        else:
            require(shutil.which("azd") and shutil.which("az"), "Install Azure Developer CLI (azd) and Azure CLI (az).")
            secure_local_state()
            {"prepare": prepare, "redirects": redirects, "verify": verify}[args.action]()
    except (DeploymentError, KeyError, TypeError, ValueError):
        error = sys.exc_info()[1]
        message = str(error) if isinstance(error, DeploymentError) else "Invalid configuration or unexpected Azure response. No sensitive details were logged."
        print(f"Deployment stopped: {message}", file=sys.stderr)
        return 1
    finally:
        secure_local_state()
    return 0


if __name__ == "__main__":
    sys.exit(main())
