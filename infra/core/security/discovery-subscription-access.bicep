metadata description = 'Grants deployment discovery and managed identity inference access in a subscription.'

targetScope = 'subscription'

param principalId string
param grantInferenceAccess bool

// Geography lookup calls /subscriptions/{id}/locations even for resource-group discovery.
var roleIds = concat(['acdd72a7-3385-48ef-bd42-f606fba81ae7'], grantInferenceAccess ? ['5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'] : [])

resource roles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in roleIds: {
  name: guid(subscription().id, principalId, roleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]
