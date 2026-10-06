import type { DashboardDeployment } from "./types";

/**
 * Human names for the identifiers the LB reports. Primary text always uses these; raw identifiers appear only as
 * secondary text in hover cards and request detail.
 */

/** Every Azure public-cloud region identifier and its display name (`az account list-locations`). */
const regionNames: Record<string, string> = {
  australiacentral: "Australia Central", australiacentral2: "Australia Central 2", australiaeast: "Australia East",
  australiasoutheast: "Australia Southeast", austriaeast: "Austria East", belgiumcentral: "Belgium Central",
  brazilsouth: "Brazil South", brazilsoutheast: "Brazil Southeast", canadacentral: "Canada Central", canadaeast: "Canada East",
  centralindia: "Central India", centralus: "Central US", chilecentral: "Chile Central", denmarkeast: "Denmark East",
  eastasia: "East Asia", eastus: "East US", eastus2: "East US 2", francecentral: "France Central", francesouth: "France South",
  germanynorth: "Germany North", germanywestcentral: "Germany West Central", indonesiacentral: "Indonesia Central",
  israelcentral: "Israel Central", italynorth: "Italy North", japaneast: "Japan East", japanwest: "Japan West",
  jioindiacentral: "Jio India Central", jioindiawest: "Jio India West", koreacentral: "Korea Central", koreasouth: "Korea South",
  malaysiawest: "Malaysia West", mexicocentral: "Mexico Central", newzealandnorth: "New Zealand North",
  northcentralus: "North Central US", northeurope: "North Europe", norwayeast: "Norway East", norwaywest: "Norway West",
  polandcentral: "Poland Central", qatarcentral: "Qatar Central", southafricanorth: "South Africa North",
  southafricawest: "South Africa West", southcentralus: "South Central US", southindia: "South India",
  southeastasia: "Southeast Asia", spaincentral: "Spain Central", swedencentral: "Sweden Central", swedensouth: "Sweden South",
  switzerlandnorth: "Switzerland North", switzerlandwest: "Switzerland West", taiwannorth: "Taiwan North",
  uaecentral: "UAE Central", uaenorth: "UAE North", uksouth: "UK South", ukwest: "UK West", westcentralus: "West Central US",
  westeurope: "West Europe", westindia: "West India", westus: "West US", westus2: "West US 2", westus3: "West US 3",
};

/**
 * Azure region display name. An identifier missing from the table (a new region) is still made readable from its
 * parts, so "polandcentral" can never reach the screen raw: "newregionnorth" reads "Newregion North".
 */
export function regionName(region: string): string {
  const key = region.toLowerCase();
  const known = regionNames[key];
  if (known) return known;
  const match = /^([a-z]+?)(north|south|east|west|central|northeast|southeast|northwest|southwest|westcentral)?(\d*)$/.exec(key);
  if (!match?.[1]) return region;
  const title = (word: string) => word.charAt(0).toUpperCase() + word.slice(1);
  const direction = match[2] === "westcentral" ? "West Central" : match[2] ? title(match[2]) : "";
  return [title(match[1]), direction, match[3]].filter(Boolean).join(" ");
}

/** "Replica 2": the service's stable number when it sends one, else the position in first-seen order. */
export function replicaLabel(replica: string, replicas: string[], numbers?: Record<string, number>): string {
  const number = numbers?.[replica] ?? replicas.indexOf(replica) + 1;
  return number > 0 ? `Replica ${number}` : "Replica";
}

export function modelName(modelKey: string | null): string {
  if (!modelKey) return "Unknown model";
  return modelKey.split("@")[0]?.replace(/^text-embedding-/, "embed-") || modelKey;
}

/**
 * Readable labels for every deployment's pool: the model name, plus a suffix only where the model needs telling
 * apart. Two deployment names for one model key read "gpt-4o-mini" and "gpt-4o-mini · public" (the suffix is what the
 * longer name adds); two versions of one model read "gpt-4o · 2024-11-20".
 */
export function poolLabels(deployments: Pick<DashboardDeployment, "id" | "modelKey" | "deployment">[]): Map<string, string> {
  const namesByKey = new Map<string, Set<string>>();
  const versionsByName = new Map<string, Set<string>>();
  for (const item of deployments) {
    namesByKey.set(item.modelKey, (namesByKey.get(item.modelKey) ?? new Set()).add(item.deployment));
    const [name = item.modelKey, version = ""] = item.modelKey.split("@");
    versionsByName.set(name, (versionsByName.get(name) ?? new Set()).add(version));
  }
  const labels = new Map<string, string>();
  for (const item of deployments) {
    const [name = item.modelKey, version = ""] = item.modelKey.split("@");
    const parts = [modelName(item.modelKey)];
    if ((versionsByName.get(name)?.size ?? 0) > 1 && version) parts.push(version);
    const names = [...(namesByKey.get(item.modelKey) ?? [])];
    if (names.length > 1) {
      const suffix = poolSuffix(item.deployment, names);
      if (suffix) parts.push(suffix);
    }
    labels.set(item.id, parts.join(" · "));
  }
  return labels;
}

/** What a deployment name adds to the shortest name of its model ("-public"), or its own distinct tail. */
function poolSuffix(name: string, names: string[]): string {
  const base = [...names].sort((a, b) => a.length - b.length || a.localeCompare(b))[0] ?? "";
  if (name === base) return "";
  const tail = name.startsWith(base) ? name.slice(base.length) : name.slice(commonPrefix(names).length);
  return tail.replace(/^[-_.\s]+|[-_.\s]+$/g, "").replace(/[-_]+/g, " ") || name;
}

function commonPrefix(values: string[]): string {
  let prefix = values[0] ?? "";
  for (const value of values) while (!value.startsWith(prefix)) prefix = prefix.slice(0, -1);
  return prefix;
}

const operationNames: Record<string, string> = {
  "chat.completions": "Chat completion", completions: "Completion", responses: "Response", embeddings: "Embeddings",
  "audio.transcriptions": "Audio transcription", "audio.translations": "Audio translation", "audio.speech": "Speech",
  "images.generations": "Image generation", "images.edits": "Image edit", "images.variations": "Image variation",
  realtime: "Realtime session", files: "Files", batches: "Batch", models: "Model list", other: "Other request",
};

/** The operation in words: "Chat completion". Unknown slugs fall back to the slug itself. */
export function operationName(operation: string | null | undefined): string | null {
  if (!operation) return null;
  return operationNames[operation] ?? operation;
}

/** "1.2 KB", "3.4 MB": request body sizes. */
export function bytesLabel(bytes: number | null | undefined): string | null {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return null;
  if (bytes < 1000) return `${bytes} B`;
  if (bytes < 1_000_000) return `${(bytes / 1000).toFixed(bytes < 10_000 ? 1 : 0)} KB`;
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}
