targetScope = 'resourceGroup'

// Production allocation in rg-recog-llm. Capacity units are 1,000 tokens per minute.
// These deployments share one 6,000-unit EU Data Zone Standard quota pool.
var allocations = [
  { account: 'recog-llm-swc', capacity: 3330 }
  { account: 'recog-llm-frc', capacity: 670 }
  { account: 'recog-llm-gwc', capacity: 670 }
  { account: 'recog-llm-we', capacity: 665 }
  { account: 'recog-llm-pc', capacity: 665 }
]

resource accounts 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = [for allocation in allocations: {
  name: allocation.account
}]

// Serial writes avoid competing updates against the shared quota pool.
@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = [for (allocation, i) in allocations: {
  parent: accounts[i]
  name: 'recog-llm-gpt-6-sol'
  sku: {
    name: 'DataZoneStandard'
    capacity: allocation.capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-6-sol'
      version: '2026-09-22'
    }
    // Reuse each account's existing policy, as the live deployments do.
    raiPolicyName: 'recog-llm-gpt-5_4-rai-policy'
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}]
