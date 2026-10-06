// This file is for doing static analysis and contains sensible defaults
// for the bicep analyser to minimise false-positives and provide the best results.

// This file is not intended to be used as a runtime configuration file.

targetScope = 'subscription'

param environmentName string = 'testing'
param location string = 'westus2'

module main 'main.bicep' = {
  name: 'main'
  params: {
    name: environmentName
    location: location
    keyVaultResourceId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/config-rg/providers/Microsoft.KeyVault/vaults/config-vault'
    // Equivalent scopes must receive one assignment, regardless of case or trailing separators.
    discoveryScopes: [
      '  AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA  '
      '/SUBSCRIPTIONS/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/'
      '/subscriptions/AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA/resourceGroups/Config-RG///'
      '/Subscriptions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/RESOURCEGROUPS/config-rg/'
    ]
  }
}
