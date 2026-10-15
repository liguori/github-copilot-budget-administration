import json
from pathlib import Path
import shutil
import subprocess
import unittest


@unittest.skipUnless(shutil.which("az"), "Azure CLI is required for Bicep compilation.")
class PrivateNetworkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = Path(__file__).resolve().parents[2]
        result = subprocess.run(
            [shutil.which("az"), "bicep", "build", "--file", str(root / "infra" / "main.bicep"), "--stdout"],
            cwd=root, text=True, capture_output=True, check=False,
        )
        if result.returncode:
            raise RuntimeError("Bicep compilation failed: " + result.stderr)
        template = json.loads(result.stdout)
        module = next(r for r in template["resources"] if r["type"] == "Microsoft.Resources/deployments")
        cls.resources = module["properties"]["template"]["resources"]
        cls.variables = module["properties"]["template"]["variables"]

    def resource(self, resource_type):
        matches = [r for r in self.resources if r["type"] == resource_type]
        self.assertEqual(len(matches), 1)
        return matches[0]

    def test_vault_is_private_and_managed_identity_remains_get_only(self):
        vault = self.resource("Microsoft.KeyVault/vaults")
        self.assertEqual(vault["properties"]["publicNetworkAccess"], "Disabled")
        self.assertEqual(vault["properties"]["accessPolicies"][0]["permissions"]["secrets"], ["get"])
        self.assertIn("principalId", vault["properties"]["accessPolicies"][0]["objectId"])

    def test_app_integrates_with_delegated_subnet_and_routes_outbound_traffic(self):
        app = self.resource("Microsoft.Web/sites")
        self.assertTrue(app["properties"]["siteConfig"]["vnetRouteAllEnabled"])
        self.assertIn("/subnets/app-integration", app["properties"]["virtualNetworkSubnetId"])
        subnets = self.resource("Microsoft.Network/virtualNetworks")["properties"]["subnets"]
        self.assertEqual(len(subnets), 2)
        integration = next(s for s in subnets if s["name"] == "app-integration")
        self.assertEqual(integration["properties"]["delegations"][0]["properties"]["serviceName"],
                         "Microsoft.Web/serverFarms")
        endpoint = next(s for s in subnets if s["name"] == "private-endpoints")
        self.assertNotIn("delegations", endpoint["properties"])
        self.assertEqual(endpoint["properties"]["privateEndpointNetworkPolicies"], "Enabled")
        for subnet in subnets:
            self.assertIn("networkSecurityGroup", subnet["properties"])

    def test_private_endpoint_has_vault_connection_and_linked_dns(self):
        endpoint = self.resource("Microsoft.Network/privateEndpoints")
        self.assertIn("/subnets/private-endpoints", endpoint["properties"]["subnet"]["id"])
        connection = endpoint["properties"]["privateLinkServiceConnections"][0]["properties"]
        self.assertEqual(connection["groupIds"], ["vault"])
        self.assertIn("Microsoft.KeyVault/vaults", connection["privateLinkServiceId"])
        zone = self.resource("Microsoft.Network/privateDnsZones")
        self.assertEqual(zone["name"], "privatelink.vaultcore.azure.net")
        link = self.resource("Microsoft.Network/privateDnsZones/virtualNetworkLinks")
        self.assertFalse(link["properties"]["registrationEnabled"])
        self.assertIn("Microsoft.Network/virtualNetworks", link["properties"]["virtualNetwork"]["id"])
        group = self.resource("Microsoft.Network/privateEndpoints/privateDnsZoneGroups")
        self.assertIn("privatelink.vaultcore.azure.net",
                      group["properties"]["privateDnsZoneConfigs"][0]["properties"]["privateDnsZoneId"])

    def test_app_settings_wait_for_private_dns_resources(self):
        dependencies = self.resource("Microsoft.Web/sites/config")["dependsOn"]
        self.assertTrue(any("Microsoft.Network/privateEndpoints/privateDnsZoneGroups" in d for d in dependencies))
        self.assertTrue(any("Microsoft.Network/privateDnsZones/virtualNetworkLinks" in d for d in dependencies))

    def test_endpoint_nsg_allows_only_app_subnet_https(self):
        groups = [r for r in self.resources if r["type"] == "Microsoft.Network/networkSecurityGroups"]
        self.assertEqual(len(groups), 2)
        rules = [rule for group in groups for rule in group["properties"]["securityRules"]]
        allow = next(r["properties"] for r in rules if r["name"] == "allow-app-https")
        self.assertEqual(allow["sourceAddressPrefix"], "[variables('integrationSubnetPrefix')]")
        self.assertEqual(allow["destinationAddressPrefix"], "[variables('endpointSubnetPrefix')]")
        self.assertEqual(self.variables["integrationSubnetPrefix"], "10.72.0.0/24")
        self.assertEqual(self.variables["endpointSubnetPrefix"], "10.72.1.0/24")
        self.assertEqual(allow["destinationPortRange"], "443")
        self.assertEqual(allow["direction"], "Inbound")
        self.assertEqual(allow["access"], "Allow")
        self.assertTrue(any(r["name"] == "deny-other-inbound" for r in rules))
        self.assertTrue(any(r["name"] == "deny-other-outbound" for r in rules))

    def test_platform_service_tags_are_not_used_in_allow_rules(self):
        deny_only_tags = {"AzurePlatformDNS", "AzurePlatformIMDS", "AzurePlatformLKM"}
        groups = [r for r in self.resources if r["type"] == "Microsoft.Network/networkSecurityGroups"]
        for group in groups:
            for rule in group["properties"]["securityRules"]:
                properties = rule["properties"]
                prefixes = [properties.get("sourceAddressPrefix"), properties.get("destinationAddressPrefix")]
                prefixes.extend(properties.get("sourceAddressPrefixes", []))
                prefixes.extend(properties.get("destinationAddressPrefixes", []))
                if deny_only_tags.intersection(prefixes):
                    self.assertEqual(properties["access"], "Deny", rule["name"])
