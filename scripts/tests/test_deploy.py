import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import re
import subprocess
import unittest
from unittest.mock import MagicMock, patch

SPEC = importlib.util.spec_from_file_location("deploy", Path(__file__).resolve().parents[1] / "deploy.py")
deploy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(deploy)
EXAMPLE = json.loads((Path(__file__).resolve().parents[1] / "deployment.example.json").read_text())
KEY = "-----BEGIN RSA PRIVATE KEY-----\nsynthetic-test-value\n-----END RSA PRIVATE KEY-----"


class ConfigurationTests(unittest.TestCase):
    def validate(self, document):
        with patch.object(deploy, "read_private_file", return_value=KEY):
            return deploy.validate_configuration(document, Path("."))

    def test_contract_is_portal_value_without_wrapper(self):
        original = copy.deepcopy(EXAMPLE)
        entra, serialized = self.validate(original)
        config = json.loads(serialized)
        self.assertEqual(original, EXAMPLE)
        self.assertNotIn("Portal", config)
        self.assertEqual(config["GitHub"]["PrivateKeyPem"], KEY)
        self.assertNotIn("PrivateKeyPemFile", config["GitHub"])
        self.assertEqual(entra["TenantId"], EXAMPLE["Entra"]["TenantId"])

    def test_minimal_portal_configuration_has_no_static_identity_or_organization_inventory(self):
        _, serialized = self.validate(copy.deepcopy(EXAMPLE))
        config = json.loads(serialized)
        self.assertNotIn("Identities", config)
        self.assertNotIn("Organizations", config)
        self.assertNotIn("EnterpriseInstallationId", config["GitHub"])

    def test_requires_manual_entra_application_and_secret(self):
        for field in ("ClientId", "ClientSecret"):
            document = copy.deepcopy(EXAMPLE)
            document["Entra"].pop(field)
            with self.subTest(field=field), self.assertRaises(deploy.DeploymentError):
                self.validate(document)

    def test_client_secret_validation_preserves_value_without_exposing_it(self):
        document = copy.deepcopy(EXAMPLE)
        document["Entra"]["ClientSecret"] = "synthetic-secret-value"
        entra, serialized = self.validate(document)
        self.assertEqual(entra["ClientSecret"], "synthetic-secret-value")
        self.assertNotIn("synthetic-secret-value", serialized)
        for value in (None, "", " \n", 123, True):
            document["Entra"]["ClientSecret"] = value
            with self.subTest(value=value), self.assertRaises(deploy.DeploymentError) as error:
                self.validate(document)
            self.assertIn("Entra.ClientSecret", str(error.exception))

    def test_rejects_non_integer_budgets(self):
        for amount in (-1, 1.2, True, "100", deploy.MAX_INT32 + 1):
            with self.subTest(amount=amount):
                document = copy.deepcopy(EXAMPLE)
                document["Portal"]["MaxBudgetAmount"] = amount
                with self.assertRaises(deploy.DeploymentError):
                    self.validate(document)

    def test_guid_must_be_canonical_and_nonempty(self):
        for value in ("00000000-0000-0000-0000-000000000000", "00000000000000000000000000000001",
                      "{00000000-0000-0000-0000-000000000001}", "not-a-guid"):
            with self.subTest(value=value), self.assertRaises(deploy.DeploymentError):
                deploy.guid(value, "TestGuid")

    def test_api_version_is_valid_exact_date(self):
        for version in ("2026-3-10", "2026-02-29", "0000-01-01", "2026-13-01",
                        "latest", "2026-03-10T00:00:00", None):
            document = copy.deepcopy(EXAMPLE)
            document["Portal"]["GitHub"]["ApiVersion"] = version
            with self.subTest(version=version), self.assertRaises(deploy.DeploymentError):
                self.validate(document)
        document["Portal"]["GitHub"]["ApiVersion"] = "2024-02-29"
        self.validate(document)

    def test_api_version_defaults_to_backend_default(self):
        document = copy.deepcopy(EXAMPLE)
        document["Portal"]["GitHub"].pop("ApiVersion")
        _, serialized = self.validate(document)
        self.assertEqual(json.loads(serialized)["GitHub"]["ApiVersion"], "2026-03-10")

    def test_api_must_use_https_without_embedded_credentials(self):
        for url in ("http://api.github.com/", "https://secret@example.com/", "https:///broken",
                    "https://api.github.com/?token=secret", "https://example.com/",
                    "https://api.example.com/", "https://api.a.b.ghe.com/", "https://api..ghe.com/",
                    "https://api.-tenant.ghe.com/", "https://api.github.com.evil.com/",
                    "https://api.github.com:8443/", "https://api.github.com/api/v3/",
                    "https://api.github.com/#fragment", "https://api.github.com:invalid/",
                    "https://api.github.com\n/", "https://api.github.com/?"):
            with self.subTest(url=url):
                document = copy.deepcopy(EXAMPLE)
                document["Portal"]["GitHub"]["ApiBaseUrl"] = url
                with self.assertRaises(deploy.DeploymentError):
                    self.validate(document)

    def test_supported_api_origins_are_canonicalized(self):
        for url, expected in [
            ("https://api.github.com", "https://api.github.com/"),
            ("https://api.github.com:443/", "https://api.github.com/"),
            ("https://API.EXAMPLE.GHE.COM/", "https://api.example.ghe.com/"),
            ("https://api.example-tenant.ghe.com:443/", "https://api.example-tenant.ghe.com/"),
        ]:
            with self.subTest(url=url):
                document = copy.deepcopy(EXAMPLE)
                document["Portal"]["GitHub"]["ApiBaseUrl"] = url
                _, serialized = self.validate(document)
                self.assertEqual(json.loads(serialized)["GitHub"]["ApiBaseUrl"], expected)

    def test_numeric_ids_fit_backend_signed_long(self):
        for value in (0, -1, True, 1.5, "1e3", "1/2", deploy.MAX_INT64 + 1):
            with self.subTest(value=value):
                with self.assertRaises(deploy.DeploymentError):
                    deploy.positive_id(value, "TestId")
        self.assertEqual(deploy.positive_id(str(deploy.MAX_INT64), "TestId"), deploy.MAX_INT64)

    def test_zero_and_maximum_budget_are_valid(self):
        for amount in (0, deploy.MAX_INT32):
            document = copy.deepcopy(EXAMPLE)
            document["Portal"]["MaxBudgetAmount"] = amount
            self.validate(document)

    def test_enterprise_slug_validation_prevents_path_injection(self):
        for value in ("../other", "my org", "-leading", "trailing-", "org/name", "org?admin=true", "org%2Fother"):
            with self.subTest(value=value):
                document = copy.deepcopy(EXAMPLE)
                document["Portal"]["GitHub"]["Enterprise"] = value
                with self.assertRaises(deploy.DeploymentError):
                    self.validate(document)

    def test_missing_key_reports_field_not_content(self):
        with patch.object(deploy, "read_private_file", return_value="DO-NOT-LOG"):
            with self.assertRaises(deploy.DeploymentError) as error:
                deploy.validate_configuration(EXAMPLE, Path("."))
        self.assertNotIn("DO-NOT-LOG", str(error.exception))

    def test_key_vault_size_limit(self):
        key = KEY + "x" * deploy.MAX_SECRET_BYTES
        with patch.object(deploy, "read_private_file", return_value=key):
            with self.assertRaises(deploy.DeploymentError):
                deploy.validate_configuration(EXAMPLE, Path("."))


class HookTests(unittest.TestCase):
    def test_cli_resolves_windows_cmd_launcher(self):
        executable = r"C:\Azure CLI\az.cmd"
        response = subprocess.CompletedProcess([executable], 0, stdout="{}", stderr="")
        with patch.object(deploy.shutil, "which", return_value=executable), \
             patch.object(deploy.subprocess, "run", return_value=response) as run:
            self.assertEqual(deploy.run(["az", "account", "show"], "safe"), "{}")
        self.assertEqual(run.call_args.args[0], [executable, "account", "show"])

    def test_prepared_values_produce_valid_bicep_parameters_without_changing_secrets(self):
        entra = copy.deepcopy(EXAMPLE["Entra"])
        entra["ClientSecret"] = 'synthetic-"quoted"\\secret\n\twith unicode \u00e8 and ${NOT_AN_ENV_VAR}'
        portal = json.dumps({"GitHub": {"PrivateKeyPem": KEY}, "MaxBudgetAmount": 10000},
                            separators=(",", ":"))
        values = {"AZURE_ENV_NAME": "regression-test", "AZURE_LOCATION": "italynorth"}
        output = io.StringIO()
        with patch.dict(deploy.os.environ, {}, clear=True), \
             patch.object(deploy, "environment", return_value=values), \
             patch.object(deploy, "load_configuration", return_value=(Path("config"), (entra, portal))), \
             patch.object(deploy, "save_env", side_effect=lambda name, value: values.update({name: str(value)})), \
             contextlib.redirect_stdout(output):
            deploy.prepare()
        template = (deploy.ROOT / "infra" / "main.parameters.json").read_text(encoding="utf-8")
        resolved = re.sub(r"\$\{([A-Z_]+)\}", lambda match: values[match.group(1)], template)
        parameters = json.loads(resolved)["parameters"]
        self.assertEqual(parameters["entraClientSecret"]["value"], entra["ClientSecret"])
        self.assertEqual(parameters["portalConfigurationJson"]["value"], portal)
        self.assertEqual(json.loads(parameters["portalConfigurationJson"]["value"])["GitHub"]["PrivateKeyPem"], KEY)
        self.assertNotIn(entra["ClientSecret"], output.getvalue())
        self.assertNotIn(KEY, output.getvalue())

    def test_cli_error_never_exposes_credentials(self):
        response = subprocess.CompletedProcess(["az"], 1, stdout="SECRET-STDOUT", stderr="SECRET-STDERR")
        out, err = io.StringIO(), io.StringIO()
        with patch.object(deploy.shutil, "which", return_value="az"), \
             patch.object(deploy.subprocess, "run", return_value=response):
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                with self.assertRaises(deploy.DeploymentError) as error:
                    deploy.run(["az", "rest", "--body", "SECRET-ARGUMENT"], "Safe actionable guidance")
        self.assertEqual(str(error.exception), "Safe actionable guidance")
        self.assertEqual(out.getvalue() + err.getvalue(), "")

    def test_cli_success_captures_secret_without_printing(self):
        response = subprocess.CompletedProcess(["az"], 0, stdout='{"secretText":"SECRET"}', stderr="")
        out = io.StringIO()
        with patch.object(deploy.shutil, "which", return_value="az"), \
             patch.object(deploy.subprocess, "run", return_value=response):
            with contextlib.redirect_stdout(out):
                self.assertEqual(deploy.az_json(["rest"], "safe"), {"secretText": "SECRET"})
        self.assertEqual(out.getvalue(), "")

    def test_prepare_loads_only_administrator_supplied_entra_credentials(self):
        values = {"AZURE_SUBSCRIPTION_ID": "subscription"}
        entra = {"TenantId": EXAMPLE["Entra"]["TenantId"],
                 "ClientId": "00000000-0000-0000-0000-000000000004",
                 "ClientSecret": "existing-secret"}
        with patch.object(deploy, "environment", return_value=values), \
             patch.object(deploy, "load_configuration", return_value=(Path("config"), (entra, "{}"))), \
             patch.object(deploy, "run"), patch.object(deploy, "save_env") as save, \
             patch.object(deploy, "read_private_file") as read_file, \
             patch.object(deploy, "az_json", return_value={"tenantId": entra["TenantId"]}), \
             contextlib.redirect_stdout(io.StringIO()):
            deploy.prepare()
        save.assert_any_call("ENTRA_CLIENT_ID", entra["ClientId"])
        save.assert_any_call("ENTRA_CLIENT_SECRET", "existing-secret")
        read_file.assert_not_called()

    def test_prepare_without_subscription_leaves_selection_to_azd(self):
        entra = copy.deepcopy(EXAMPLE["Entra"])
        with patch.dict(deploy.os.environ, {}, clear=True), \
             patch.object(deploy, "environment", return_value={}), \
             patch.object(deploy, "load_configuration", return_value=(Path("config"), (entra, "{}"))), \
             patch.object(deploy, "save_env") as save, \
             patch.object(deploy, "az_json") as azure, \
             contextlib.redirect_stdout(io.StringIO()):
            deploy.prepare()
        azure.assert_not_called()
        save.assert_any_call("ENTRA_CLIENT_ID", entra["ClientId"])
        self.assertFalse(any(call.args[0] in ("AZURE_SUBSCRIPTION_ID", "AZURE_LOCATION")
                             for call in save.call_args_list))

    def test_prepare_reads_json_from_azd_environment_setting(self):
        path = Path("scripts") / "operator-config.json"
        with patch.dict(deploy.os.environ, {}, clear=True), \
             patch.object(deploy, "environment", return_value={"PORTAL_DEPLOYMENT_CONFIG": str(path)}), \
             patch.object(Path, "read_text", return_value=json.dumps(EXAMPLE)), \
             patch.object(deploy, "read_private_file", return_value=KEY), \
             patch.object(deploy, "save_env") as save, \
             patch.object(deploy, "az_json") as azure, \
             patch.object(deploy, "input", create=True) as prompt, \
             contextlib.redirect_stdout(io.StringIO()):
            deploy.prepare()
        prompt.assert_not_called()
        azure.assert_not_called()
        save.assert_any_call("PORTAL_DEPLOYMENT_CONFIG", path.resolve())
        save.assert_any_call("ENTRA_CLIENT_SECRET", EXAMPLE["Entra"]["ClientSecret"])

    def test_selected_subscription_must_belong_to_entra_tenant(self):
        with patch.object(deploy, "az_json", return_value={"tenantId": "different-tenant"}):
            with self.assertRaisesRegex(deploy.DeploymentError, "must match"):
                deploy.validate_subscription_tenant("subscription", EXAMPLE["Entra"]["TenantId"])

    def test_postprovision_prints_manual_callback_urls(self):
        values = {
            "AZURE_WEB_URL": "https://portal.azurewebsites.net",
            "AZURE_WEB_NAME": "portal",
            "AZURE_RESOURCE_GROUP": "group",
            "AZURE_SUBSCRIPTION_ID": "subscription",
            "ENTRA_TENANT_ID": EXAMPLE["Entra"]["TenantId"],
        }
        output = io.StringIO()
        with patch.object(deploy, "environment", return_value=values), \
             patch.object(deploy, "az_json", return_value={"tenantId": EXAMPLE["Entra"]["TenantId"]}), \
             contextlib.redirect_stdout(output):
            deploy.redirects()
        self.assertIn("https://portal.azurewebsites.net/signin-oidc", output.getvalue())
        self.assertIn("https://portal.azurewebsites.net/signout-callback-oidc", output.getvalue())

    def test_postprovision_tenant_mismatch_stops_before_reference_refresh(self):
        values = {
            "AZURE_WEB_URL": "https://portal.azurewebsites.net",
            "AZURE_WEB_NAME": "portal",
            "AZURE_RESOURCE_GROUP": "group",
            "AZURE_SUBSCRIPTION_ID": "selected-subscription",
            "ENTRA_TENANT_ID": EXAMPLE["Entra"]["TenantId"],
        }
        with patch.object(deploy, "environment", return_value=values), \
             patch.object(deploy, "az_json", return_value={"tenantId": "different-tenant"}) as azure:
            with self.assertRaisesRegex(deploy.DeploymentError, "must match"):
                deploy.redirects()
        azure.assert_called_once()
        self.assertEqual(azure.call_args.args[0],
                         ["account", "show", "--subscription", "selected-subscription"])


class HealthTests(unittest.TestCase):
    def response(self, body=b"Healthy", status=200):
        response = MagicMock()
        response.__enter__.return_value = response
        response.status = status
        response.read.return_value = body
        return response

    def test_healthy_response_succeeds(self):
        opener = MagicMock()
        opener.open.return_value = self.response()
        output = io.StringIO()
        with patch.object(deploy, "build_opener", return_value=opener), contextlib.redirect_stdout(output):
            deploy.verify_health("https://portal.azurewebsites.net")
        self.assertIn("reports Healthy", output.getvalue())
        request = opener.open.call_args.args[0]
        self.assertEqual(request.full_url, "https://portal.azurewebsites.net/healthz")

    def test_startup_failure_then_recovery_is_retried(self):
        opener = MagicMock()
        opener.open.side_effect = [deploy.URLError("PRIVATE-ERROR"), self.response(b"Unhealthy", 503), self.response()]
        with patch.object(deploy, "build_opener", return_value=opener), \
             patch.object(deploy.time, "sleep") as sleep, contextlib.redirect_stdout(io.StringIO()):
            deploy.verify_health("https://portal.azurewebsites.net", max_attempts=3)
        self.assertEqual(opener.open.call_count, 3)
        self.assertEqual(sleep.call_count, 2)

    def test_placeholder_and_login_pages_do_not_count_as_healthy(self):
        opener = MagicMock()
        opener.open.return_value = self.response(b"<html>App Service placeholder or sign-in</html>")
        with patch.object(deploy, "build_opener", return_value=opener), patch.object(deploy.time, "sleep"):
            with self.assertRaisesRegex(deploy.DeploymentError, "startup/JSON console logs"):
                deploy.verify_health("https://portal.azurewebsites.net", max_attempts=2)
        self.assertEqual(opener.open.call_count, 2)

    def test_failure_does_not_log_sensitive_http_details(self):
        opener = MagicMock()
        opener.open.side_effect = deploy.URLError("SECRET-RESPONSE")
        out, err = io.StringIO(), io.StringIO()
        with patch.object(deploy, "build_opener", return_value=opener), \
             contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            with self.assertRaises(deploy.DeploymentError) as error:
                deploy.verify_health("https://portal.azurewebsites.net", max_attempts=1)
        self.assertNotIn("SECRET", str(error.exception) + out.getvalue() + err.getvalue())

    def test_deadline_ends_retry_window(self):
        opener = MagicMock()
        with patch.object(deploy, "build_opener", return_value=opener), \
             patch.object(deploy.time, "monotonic", side_effect=[0, 301]):
            with self.assertRaises(deploy.DeploymentError):
                deploy.verify_health("https://portal.azurewebsites.net")
        opener.open.assert_not_called()

    def test_redirects_are_not_followed(self):
        self.assertIsNone(deploy.NoRedirects().redirect_request(None, None, 302, "Found", {}, "https://login.example"))

    def test_health_origin_rejects_http_and_embedded_credentials(self):
        for url in ("http://portal.azurewebsites.net", "https://secret@portal.azurewebsites.net",
                    "https://portal.azurewebsites.net/login", "https://portal.azurewebsites.net:8080"):
            with self.subTest(url=url), self.assertRaises(deploy.DeploymentError):
                deploy.verify_health(url)


if __name__ == "__main__":
    unittest.main()
