// The production LB's inventory (its /admin/state, 2026-10-05): 71 enabled deployments in five Azure OpenAI accounts.
// Weight equals capacity (thousands of tokens per minute) for every deployment.

export interface InventoryDeployment {
  id: string;
  accountId: string;
  account: string;
  deployment: string;
  /** Model key, "name@version". */
  model: string;
  region: string;
  sku: string;
  tier: number;
  zone: string;
  capacity: number;
  weight: number;
}

const subscription = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-llm/providers/Microsoft.CognitiveServices/accounts";

const regions: Record<string, string> = {
  "oai-swedencentral": "swedencentral",
  "oai-polandcentral": "polandcentral",
  "oai-westeurope": "westeurope",
  "oai-francecentral": "francecentral",
  "oai-germanywestcentral": "germanywestcentral",
};

// [account, deployment, model, sku, tier, zone, capacity]
const rows: [string, string, string, string, number, string, number][] = [
  ["oai-swedencentral", "llm-gpt-4o", "gpt-4o@2024-08-06", "DataZoneStandard", 1, "eu", 1000],
  ["oai-swedencentral", "llm-o3", "o3@2025-04-16", "DataZoneStandard", 1, "eu", 3000],
  ["oai-swedencentral", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14", "DataZoneStandard", 1, "eu", 16000],
  ["oai-swedencentral", "llm-gpt-o4mini", "o4-mini@2025-04-16", "DataZoneStandard", 1, "eu", 300],
  ["oai-swedencentral", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 1000],
  ["oai-swedencentral", "llm-gpt-5", "gpt-5@2025-08-07", "DataZoneStandard", 1, "eu", 3000],
  ["oai-swedencentral", "llm-gpt-4_1", "gpt-4.1@2025-04-14", "DataZoneStandard", 1, "eu", 3000],
  ["oai-swedencentral", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 500],
  ["oai-swedencentral", "llm-gpt-audio", "gpt-audio@2025-08-28", "GlobalStandard", 2, "global", 1000],
  ["oai-swedencentral", "llm-gpt-audio-mini", "gpt-audio-mini@2025-10-06", "GlobalStandard", 2, "global", 20],
  ["oai-swedencentral", "stt-whisper", "whisper@001", "Standard", 1, "eu", 30],
  ["oai-swedencentral", "llm-gpt-5-nano", "gpt-5-nano@2025-08-07", "DataZoneStandard", 1, "eu", 2957],
  ["oai-swedencentral", "llm-gpt-5-mini", "gpt-5-mini@2025-08-07", "DataZoneStandard", 1, "eu", 1000],
  ["oai-swedencentral", "llm-gpt-5_1", "gpt-5.1@2025-11-13", "DataZoneStandard", 1, "eu", 1000],
  ["oai-swedencentral", "llm-gpt-5_4", "gpt-5.4@2026-03-05", "DataZoneStandard", 1, "eu", 3000],
  ["oai-swedencentral", "llm-gpt-5_6-luna", "gpt-5.6-luna@2026-07-09", "DataZoneStandard", 1, "eu", 3279],
  ["oai-swedencentral", "llm-gpt-5_6-terra", "gpt-5.6-terra@2026-07-09", "DataZoneStandard", 1, "eu", 3293],
  ["oai-swedencentral", "llm-gpt-5_6-sol", "gpt-5.6-sol@2026-07-09", "DataZoneStandard", 1, "eu", 3333],
  ["oai-swedencentral", "llm-gpt-6-luna", "gpt-6-luna@2026-09-22", "DataZoneStandard", 1, "eu", 3279],
  ["oai-swedencentral", "llm-gpt-6-sol", "gpt-6-sol@2026-09-22", "DataZoneStandard", 1, "eu", 3330],
  ["oai-swedencentral", "llm-gpt-6-1-sol", "gpt-6.1-sol@2026-09-29", "DataZoneStandard", 1, "eu", 3275],
  ["oai-polandcentral", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 1000],
  ["oai-polandcentral", "llm-gpt-4o", "gpt-4o@2024-08-06", "DataZoneStandard", 1, "eu", 1000],
  ["oai-polandcentral", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14", "DataZoneStandard", 1, "eu", 1000],
  ["oai-polandcentral", "llm-gpt-4_1", "gpt-4.1@2025-04-14", "DataZoneStandard", 1, "eu", 300],
  ["oai-polandcentral", "llm-gpt-o4mini", "o4-mini@2025-04-16", "DataZoneStandard", 1, "eu", 300],
  ["oai-polandcentral", "llm-o3", "o3@2025-04-16", "DataZoneStandard", 1, "eu", 3000],
  ["oai-polandcentral", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 500],
  ["oai-polandcentral", "llm-gpt-5-nano", "gpt-5-nano@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-polandcentral", "llm-gpt-5-mini", "gpt-5-mini@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-polandcentral", "llm-gpt-5", "gpt-5@2025-08-07", "DataZoneStandard", 1, "eu", 50],
  ["oai-polandcentral", "llm-gpt-5_4", "gpt-5.4@2026-03-05", "DataZoneStandard", 1, "eu", 50],
  ["oai-westeurope", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 1000],
  ["oai-westeurope", "llm-gpt-4o", "gpt-4o@2024-08-06", "DataZoneStandard", 1, "eu", 1000],
  ["oai-westeurope", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14", "DataZoneStandard", 1, "eu", 1000],
  ["oai-westeurope", "llm-gpt-4_1", "gpt-4.1@2025-04-14", "DataZoneStandard", 1, "eu", 300],
  ["oai-westeurope", "llm-gpt-o4mini", "o4-mini@2025-04-16", "DataZoneStandard", 1, "eu", 300],
  ["oai-westeurope", "llm-o3", "o3@2025-04-16", "DataZoneStandard", 1, "eu", 3000],
  ["oai-westeurope", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 500],
  ["oai-westeurope", "llm-gpt-5-nano", "gpt-5-nano@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-westeurope", "llm-gpt-5-mini", "gpt-5-mini@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-westeurope", "llm-gpt-5", "gpt-5@2025-08-07", "DataZoneStandard", 1, "eu", 50],
  ["oai-westeurope", "llm-gpt-5_4", "gpt-5.4@2026-03-05", "DataZoneStandard", 1, "eu", 50],
  ["oai-francecentral", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 1000],
  ["oai-francecentral", "llm-gpt-4o", "gpt-4o@2024-08-06", "DataZoneStandard", 1, "eu", 1000],
  ["oai-francecentral", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14", "DataZoneStandard", 1, "eu", 1000],
  ["oai-francecentral", "llm-gpt-4_1", "gpt-4.1@2025-04-14", "DataZoneStandard", 1, "eu", 300],
  ["oai-francecentral", "llm-gpt4_1nano", "gpt-4.1-nano@2025-04-14", "DataZoneStandard", 1, "eu", 202],
  ["oai-francecentral", "llm-gpt-o4mini", "o4-mini@2025-04-16", "DataZoneStandard", 1, "eu", 300],
  ["oai-francecentral", "llm-o3", "o3@2025-04-16", "DataZoneStandard", 1, "eu", 3000],
  ["oai-francecentral", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 500],
  ["oai-francecentral", "llm-gpt-5-nano", "gpt-5-nano@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-francecentral", "llm-gpt-5-mini", "gpt-5-mini@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-francecentral", "llm-gpt-5", "gpt-5@2025-08-07", "DataZoneStandard", 1, "eu", 50],
  ["oai-francecentral", "llm-gpt-5_1", "gpt-5.1@2025-11-13", "DataZoneStandard", 1, "eu", 3000],
  ["oai-francecentral", "llm-gpt-5_6-luna", "gpt-5.6-luna@2026-07-09", "DataZoneStandard", 1, "eu", 54],
  ["oai-francecentral", "llm-gpt-5_6-terra", "gpt-5.6-terra@2026-07-09", "DataZoneStandard", 1, "eu", 40],
  ["oai-francecentral", "llm-gpt-5_4", "gpt-5.4@2026-03-05", "DataZoneStandard", 1, "eu", 50],
  ["oai-francecentral", "llm-gpt-6-luna", "gpt-6-luna@2026-09-22", "DataZoneStandard", 1, "eu", 54],
  ["oai-francecentral", "llm-gpt-6-1-sol", "gpt-6.1-sol@2026-09-29", "DataZoneStandard", 1, "eu", 55],
  ["oai-germanywestcentral", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 1000],
  ["oai-germanywestcentral", "llm-gpt-4o", "gpt-4o@2024-08-06", "DataZoneStandard", 1, "eu", 1000],
  ["oai-germanywestcentral", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14", "DataZoneStandard", 1, "eu", 1000],
  ["oai-germanywestcentral", "llm-gpt-4_1", "gpt-4.1@2025-04-14", "DataZoneStandard", 1, "eu", 300],
  ["oai-germanywestcentral", "llm-gpt-o4mini", "o4-mini@2025-04-16", "DataZoneStandard", 1, "eu", 300],
  ["oai-germanywestcentral", "llm-o3", "o3@2025-04-16", "DataZoneStandard", 1, "eu", 3000],
  ["oai-germanywestcentral", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18", "DataZoneStandard", 1, "eu", 500],
  ["oai-germanywestcentral", "llm-gpt-5-nano", "gpt-5-nano@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-germanywestcentral", "llm-gpt-5-mini", "gpt-5-mini@2025-08-07", "DataZoneStandard", 1, "eu", 200],
  ["oai-germanywestcentral", "llm-gpt-5", "gpt-5@2025-08-07", "DataZoneStandard", 1, "eu", 50],
  ["oai-germanywestcentral", "llm-gpt-5_4", "gpt-5.4@2026-03-05", "DataZoneStandard", 1, "eu", 50],
];

export const productionInventory: InventoryDeployment[] = rows.map(([account, deployment, model, sku, tier, zone, capacity]) => ({
  id: `${subscription}/${account}/deployments/${deployment}`, accountId: `${subscription}/${account}`, account, deployment, model,
  region: regions[account] ?? "", sku, tier, zone, capacity, weight: capacity,
}));
