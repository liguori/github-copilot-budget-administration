param location string
param token string
param tags object
@secure()
param entraTenantId string
@secure()
param entraClientId string
@secure()
param entraClientSecret string
@secure()
param portalConfigurationJson string

var integrationSubnetPrefix = '10.72.0.0/24'
var endpointSubnetPrefix = '10.72.1.0/24'

resource integrationSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-budget-app-${token}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'allow-key-vault-https'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: integrationSubnetPrefix
          destinationAddressPrefix: endpointSubnetPrefix
        }
      }
      {
        name: 'allow-public-https'
        properties: {
          priority: 110
          direction: 'Outbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: integrationSubnetPrefix
          destinationAddressPrefix: 'Internet'
        }
      }
      {
        name: 'deny-other-outbound'
        properties: {
          priority: 4000
          direction: 'Outbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'deny-inbound'
        properties: {
          priority: 4000
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource endpointSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-budget-kv-${token}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'allow-app-https'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: integrationSubnetPrefix
          destinationAddressPrefix: endpointSubnetPrefix
        }
      }
      {
        name: 'deny-other-inbound'
        properties: {
          priority: 4000
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-budget-${token}'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.72.0.0/16'] }
    subnets: [
      {
        name: 'app-integration'
        properties: {
          addressPrefix: integrationSubnetPrefix
          networkSecurityGroup: { id: integrationSecurityGroup.id }
          delegations: [{
            name: 'app-service'
            properties: { serviceName: 'Microsoft.Web/serverFarms' }
          }]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: endpointSubnetPrefix
          networkSecurityGroup: { id: endpointSecurityGroup.id }
          privateEndpointNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-budget-${token}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-budget-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-budget-${token}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: { reserved: true }
}

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: 'app-budget-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: '${network.id}/subnets/app-integration'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      vnetRouteAllEnabled: true
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      numberOfWorkers: 1
    }
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-budget-${token}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    enableRbacAuthorization: false
    publicNetworkAccess: 'Disabled'
    accessPolicies: [{
      tenantId: subscription().tenantId
      objectId: web.identity.principalId
      permissions: { secrets: ['get'] }
    }]
  }
}

resource vaultDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource vaultDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: vaultDnsZone
  name: 'budget-network'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: network.id }
  }
}

resource vaultEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-budget-kv-${token}'
  location: location
  tags: tags
  properties: {
    subnet: { id: '${network.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [{
      name: 'key-vault'
      properties: {
        privateLinkServiceId: vault.id
        groupIds: ['vault']
      }
    }]
  }
}

resource vaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: vaultEndpoint
  name: 'key-vault'
  properties: {
    privateDnsZoneConfigs: [{
      name: 'key-vault'
      properties: { privateDnsZoneId: vaultDnsZone.id }
    }]
  }
}

resource tenantSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'entra-tenant-id'
  properties: { value: entraTenantId }
}
resource clientSecretId 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'entra-client-id'
  properties: { value: entraClientId }
}
resource oidcSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'entra-client-secret'
  properties: { value: entraClientSecret }
}
resource portalSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'portal-configuration'
  properties: { value: portalConfigurationJson }
}

resource monitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, web.id, 'monitoring-metrics-publisher')
  scope: insights
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

resource settings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: web
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    AllowedHosts: web.properties.defaultHostName
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
    Entra__TenantId: '@Microsoft.KeyVault(SecretUri=${tenantSecret.properties.secretUri})'
    Entra__ClientId: '@Microsoft.KeyVault(SecretUri=${clientSecretId.properties.secretUri})'
    Entra__ClientSecret: '@Microsoft.KeyVault(SecretUri=${oidcSecret.properties.secretUri})'
    PORTAL_CONFIGURATION_JSON: '@Microsoft.KeyVault(SecretUri=${portalSecret.properties.secretUri})'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
    APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    XDT_MicrosoftApplicationInsights_Mode: 'recommended'
  }
  dependsOn: [
    vaultDnsLink
    vaultDnsGroup
  ]
}

resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'app-service-logs'
  scope: web
  properties: {
    workspaceId: workspace.id
    logs: [
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServiceAppLogs', enabled: true }
    ]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output webName string = web.name
output webUrl string = 'https://${web.properties.defaultHostName}'
output keyVaultName string = vault.name
