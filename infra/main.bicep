targetScope = 'subscription'

@description('Specifies the location for all resources.')
param location string

@description('Subscription IDs or subscription/resource-group ARM IDs to discover. Empty uses the generated resource group.')
param discoveryScopes array = []

@description('ARM ID of an existing RBAC-enabled Key Vault containing the configuration files.')
param keyVaultResourceId string

@description('Key Vault secret containing the callers YAML file.')
param callersFileSecretName string = 'lb-callers'

@description('Key Vault secret containing the overrides YAML file.')
param overridesFileSecretName string = 'lb-overrides'

@description('Optional Key Vault secret containing the Slack incoming webhook URL. Empty disables Slack alerts.')
param slackWebhookSecretName string = ''

@description('Prebuilt proxy image. Empty uses a placeholder until the azd postprovision hook deploys the proxy.')
param imageName string = ''

@description('SKU name for OpenAI.')
param openAiSkuName string = 'S0'

@description('Version of the Chat GPT model.')
param chatGptModelVersion string = '0613'

@description('Name of the Chat GPT deployment.')
param chatGptDeploymentName string = 'chat'

@description('Name of the Chat GPT model.')
param embeddingGptModelName string = 'text-embedding-ada-002'

@description('Version of the Chat GPT model.')
param embeddingGptModelVersion string = '2'

@description('Name of the Chat GPT deployment.')
param embeddingGptDeploymentName string = 'embedding'

@description('Name of the Chat GPT model.')
param chatGptModelName string = 'gpt-35-turbo'

@description('The OpenAI endpoints capacity (in thousands of tokens per minute)')
param deploymentCapacity int = 30

// You can add more OpenAI instances by adding more objects to the openAiInstances object
@description('Object containing OpenAI instances. You can add more instances by adding more objects to this parameter.')
param openAiInstances object = {
  openAi1: {
    name: 'openai1'
    location: 'eastus'
  }
  openAi2: {
    name: 'openai2'
    location: 'northcentralus'
  }
  openAi3: {
    name: 'openai3'
    location: 'eastus2'
  }
}

@minLength(1)
@maxLength(64)
@description('Name which is used to generate a short unique hash for each resource')
param name string

// Load abbreviations from JSON file
var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, name, location))
var prefix = '${name}-${resourceToken}'
var tags = { 'azd-env-name': name }
var discoveryScopeParts = [for discoveryScope in discoveryScopes: filter(split(toLower(trim(discoveryScope)), '/'), part => !empty(part))]
var canonicalDiscoveryScopes = [for parts in discoveryScopeParts: length(parts) == 1
  ? '/subscriptions/${parts[0]}'
  : length(parts) == 2
    ? '/subscriptions/${parts[1]}'
    : '/subscriptions/${parts[1]}/resourceGroups/${parts[3]}']
var normalizedDiscoveryScopes = union(canonicalDiscoveryScopes, [])
var effectiveDiscoveryScopes = empty(discoveryScopes) ? [resourceGroup.id] : normalizedDiscoveryScopes
var subscriptionScopes = filter(effectiveDiscoveryScopes, discoveryScope => length(split(discoveryScope, '/')) == 3)
var resourceGroupScopes = filter(effectiveDiscoveryScopes, discoveryScope => length(split(discoveryScope, '/')) == 5)
var parentSubscriptions = union(map(effectiveDiscoveryScopes, discoveryScope => split(discoveryScope, '/')[2]), [])
var keyVaultIdParts = split(keyVaultResourceId, '/')



resource resourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: '${name}-rg'
  location: location
  tags: tags
}

// Monitor application with Azure Monitor
module monitoring 'core/monitor/monitoring.bicep' = {
  name: 'monitoring'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    applicationInsightsDashboardName: '${prefix}-appinsights-dashboard'
    applicationInsightsName: '${prefix}-appinsights'
    logAnalyticsName: '${take(prefix, 50)}-loganalytics' // Max 63 chars
  }
}

module managedIdentity 'core/security/managed-identity.bicep' = {
  name: 'managed-identity'
  scope: resourceGroup
  params: {
    name: '${abbrs.managedIdentityUserAssignedIdentities}${resourceToken}'
    location: location
    tags: tags
  }
}

module subscriptionDiscoveryAccess 'core/security/discovery-subscription-access.bicep' = [for subscriptionId in parentSubscriptions: {
  name: 'discovery-sub-${uniqueString(subscriptionId)}'
  scope: subscription(subscriptionId)
  params: {
    principalId: managedIdentity.outputs.managedIdentityPrincipalId
    grantInferenceAccess: contains(subscriptionScopes, '/subscriptions/${subscriptionId}')
  }
}]

module resourceGroupDiscoveryAccess 'core/security/discovery-resource-group-access.bicep' = [for discoveryScope in resourceGroupScopes: {
  name: 'discovery-rg-${uniqueString(discoveryScope)}'
  scope: az.resourceGroup(split(discoveryScope, '/')[2], split(discoveryScope, '/')[4])
  params: {
    principalId: managedIdentity.outputs.managedIdentityPrincipalId
  }
}]

module configurationAccess 'core/security/configuration-access.bicep' = {
  name: 'configuration-access'
  scope: az.resourceGroup(keyVaultIdParts[2], keyVaultIdParts[4])
  params: {
    keyVaultName: keyVaultIdParts[8]
    principalId: managedIdentity.outputs.managedIdentityPrincipalId
  }
}

// Web frontend
module web 'web.bicep' = {
  name: 'web'
  scope: resourceGroup
  params: {
    name: replace('${take(prefix, 19)}-ca', '--', '-')
    location: location
    tags: tags
    applicationInsightsName: monitoring.outputs.applicationInsightsName
    logAnalyticsWorkspaceName: monitoring.outputs.logAnalyticsWorkspaceName
    identityName: managedIdentity.outputs.managedIdentityName
    identityClientId: managedIdentity.outputs.managedIdentityClientId
    containerAppsEnvironmentName: '${prefix}-containerapps-env'
    containerRegistryName: '${replace(prefix, '-', '')}registry'
    discoveryScopes: effectiveDiscoveryScopes
    keyVaultUri: configurationAccess.outputs.keyVaultUri
    callersFileSecretName: callersFileSecretName
    overridesFileSecretName: overridesFileSecretName
    slackWebhookSecretName: slackWebhookSecretName
    imageName: imageName
  }
  dependsOn: [
    subscriptionDiscoveryAccess
    resourceGroupDiscoveryAccess
    openAis
  ]
}

module openAis 'core/ai/cognitiveservices.bicep' = [for (config, i) in items(openAiInstances): {
  name: '${config.value.name}-${resourceToken}'
  scope: resourceGroup
  params: {
    name: '${config.value.name}-${resourceToken}'
    location: config.value.location
    tags: tags
    managedIdentityName: managedIdentity.outputs.managedIdentityName
    sku: {
      name: openAiSkuName
    }
    deploymentCapacity: deploymentCapacity
    deployments: [
      {
        name: chatGptDeploymentName
        model: {
          format: 'OpenAI'
          name: chatGptModelName
          version: chatGptModelVersion
        }
        scaleSettings: {
          scaleType: 'Standard'
        }
      }
      {
        name: embeddingGptDeploymentName
        model: {
          format: 'OpenAI'
          name: embeddingGptModelName
          version: embeddingGptModelVersion
        }
        sku: {
          name: 'Standard'
          capacity: deploymentCapacity
        }
      }
    ]
  }
}]

output CONTAINER_APP_URL string =web.outputs.uri
output SERVICE_WEB_NAME string = web.outputs.SERVICE_WEB_NAME
output AZURE_REGISTRY_NAME string =web.outputs.AZURE_REGISTRY_NAME
output RESOURCE_GROUP_NAME string =resourceGroup.name
