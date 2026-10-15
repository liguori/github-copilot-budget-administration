targetScope = 'subscription'

@minLength(1)
param environmentName string
param location string
@secure()
param entraTenantId string
@secure()
param entraClientId string
@secure()
param entraClientSecret string
@secure()
param portalConfigurationJson string

var token = uniqueString(subscription().subscriptionId, environmentName, location, 'budget-portal')
var tags = {
  'azd-env-name': environmentName
  application: 'gh-copilot-budget-administration'
}

// Never reuse a generic resource group selected by another azd project.
resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-budget-portal-${token}'
  location: location
  tags: tags
}

module resources './resources.bicep' = {
  name: 'budget-portal-${token}'
  scope: resourceGroup
  params: {
    location: location
    token: token
    tags: tags
    entraTenantId: entraTenantId
    entraClientId: entraClientId
    entraClientSecret: entraClientSecret
    portalConfigurationJson: portalConfigurationJson
  }
}

output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_WEB_NAME string = resources.outputs.webName
output AZURE_WEB_URL string = resources.outputs.webUrl
output AZURE_KEY_VAULT_NAME string = resources.outputs.keyVaultName
