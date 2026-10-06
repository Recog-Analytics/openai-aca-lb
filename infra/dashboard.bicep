extension microsoftGraphV1

param name string
param location string = resourceGroup().location
param tags object = {}

param containerAppsEnvironmentName string
param containerRegistryName string
@description('User-assigned identity of the dashboard. It needs only AcrPull.')
param identityName string
@description('LB managed identity. Its object ID is the only principal allowed on /ingest.')
param lbIdentityPrincipalId string
param lbIdentityClientId string
@description('Tenant-unique key of the app registration. Keeps redeployments idempotent.')
param appRegistrationName string
param imageName string = ''

module app 'core/host/container-app.bicep' = {
  name: '${deployment().name}-app'
  params: {
    name: name
    location: location
    tags: tags
    identityName: identityName
    ingressEnabled: true
    external: true
    containerName: 'main'
    containerAppsEnvironmentName: containerAppsEnvironmentName
    containerRegistryName: containerRegistryName
    containerCpuCoreCount: '0.5'
    containerMemory: '1.0Gi'
    // The service merges replica batches in memory. A second replica would split the stream.
    containerMinReplicas: 1
    containerMaxReplicas: 1
    imageName: imageName
    targetPort: empty(imageName) ? 80 : 8080
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
          path: '/healthz'
          port: 8080
          scheme: 'HTTP'
        }
        periodSeconds: 10
      }
    ]
    env: [
      {
        name: 'Dashboard__LbIdentityObjectId'
        value: lbIdentityPrincipalId
      }
    ]
  }
}

// Single-tenant sign-in. Without a client secret, Easy Auth signs users in with ID tokens only.
// Version 2 access tokens use the client ID as audience, so the LB requests `<client ID>/.default`.
resource registration 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: appRegistrationName
  displayName: 'LB dashboard (${name})'
  signInAudience: 'AzureADMyOrg'
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    redirectUris: [
      '${app.outputs.uri}/.auth/login/aad/callback'
    ]
    implicitGrantSettings: {
      enableIdTokenIssuance: true
    }
  }
}

resource principal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: registration.appId
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: name
}

resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = {
  parent: containerApp
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: [
        '/healthz'
      ]
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: registration.appId
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            registration.appId
          ]
          // Browser sessions come from this app. Ingest tokens come from the LB identity; the service then checks its object ID.
          defaultAuthorizationPolicy: {
            allowedApplications: [
              registration.appId
              lbIdentityClientId
            ]
          }
        }
      }
    }
  }
  dependsOn: [
    principal
  ]
}

output name string = app.outputs.name
output uri string = app.outputs.uri
output clientId string = registration.appId
