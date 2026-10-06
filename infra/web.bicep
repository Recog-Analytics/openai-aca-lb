param name string
param location string = resourceGroup().location
param tags object = {}

param applicationInsightsName string
param containerAppsEnvironmentName string
param containerRegistryName string
param identityName string
param identityClientId string
param discoveryScopes array
param keyVaultUri string
param callersFileSecretName string
param overridesFileSecretName string
param slackWebhookSecretName string = ''
param imageName string = ''
param dashboardIngestUrl string
param dashboardAudience string

var configurationPath = '/mnt/lb-config'
var discoveryEnvironment = [for (discoveryScope, index) in discoveryScopes: {
  name: 'Discovery__Scopes__${index}'
  value: discoveryScope
}]
var configurationSecrets = [
  {
    name: 'lb-callers'
    keyVaultUrl: '${keyVaultUri}secrets/${callersFileSecretName}'
    identity: userIdentity.id
  }
  {
    name: 'lb-overrides'
    keyVaultUrl: '${keyVaultUri}secrets/${overridesFileSecretName}'
    identity: userIdentity.id
  }
]
var slackSecrets = empty(slackWebhookSecretName) ? [] : [
  {
    name: 'lb-slack-webhook'
    keyVaultUrl: '${keyVaultUri}secrets/${slackWebhookSecretName}'
    identity: userIdentity.id
  }
]
var slackEnvironment = empty(slackWebhookSecretName) ? [] : [
  {
    name: 'Operations__SlackWebhookUrl'
    secretRef: 'lb-slack-webhook'
  }
]

module app 'core/host/container-app.bicep' = {
  name: '${deployment().name}-update'
  params: {
    name: name
    location: location
    tags: tags
    identityName: identityName
    ingressEnabled: true
    containerName: 'main'
    containerAppsEnvironmentName: containerAppsEnvironmentName
    containerRegistryName: containerRegistryName
    containerCpuCoreCount: '1'
    containerMemory: '2Gi'
    containerMinReplicas: 1
    containerMaxReplicas: 10
    external: true
    imageName: imageName
    targetPort: empty(imageName) ? 80 : 8080
    secrets: concat(configurationSecrets, slackSecrets)
    volumes: [
      {
        name: 'lb-config'
        storageType: 'Secret'
        secrets: [
          {
            secretRef: 'lb-callers'
            path: 'callers.yaml'
          }
          {
            secretRef: 'lb-overrides'
            path: 'overrides.yaml'
          }
        ]
      }
    ]
    volumeMounts: [
      {
        volumeName: 'lb-config'
        mountPath: configurationPath
      }
    ]
    probes: empty(imageName) ? [] : [
      {
        type: 'Liveness'
        httpGet: {
          path: '/healthz'
          port: 8080
          scheme: 'HTTP'
        }
        periodSeconds: 30
      }
      {
        type: 'Readiness'
        httpGet: {
          path: '/readyz'
          port: 8080
          scheme: 'HTTP'
        }
        periodSeconds: 10
      }
    ]
    env: concat([
      {
        name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
        value: applicationInsights.properties.ConnectionString
      }
      {
        name: 'AZURE_CLIENT_ID'
        value: identityClientId
      }
      {
        name: 'Discovery__CallersFilePath'
        value: '${configurationPath}/callers.yaml'
      }
      {
        name: 'Discovery__OverridesFilePath'
        value: '${configurationPath}/overrides.yaml'
      }
      {
        name: 'Dashboard__IngestUrl'
        value: dashboardIngestUrl
      }
      {
        name: 'Dashboard__Audience'
        value: dashboardAudience
      }
    ], discoveryEnvironment, slackEnvironment)
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

resource userIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
}

output SERVICE_WEB_NAME string = app.outputs.name
output uri string = app.outputs.uri
