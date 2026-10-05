import { parseTime } from "./timeline";
import type { DashboardDeployment, DeploymentRate, HealthStateName, MergedDeployment, RequestAttempt, RequestRecord } from "./types";

export type NodeLook = "healthy" | "throttled" | "degraded" | "open" | "halfOpen" | "disabled" | "absent";

export interface NodeVisual {
  look: NodeLook;
  /** Remaining share of the throttle cooldown, 1 → 0. Null when unknown or not throttled. */
  cooldown: number | null;
  cooldownMs: number | null;
  probeInMs: number | null;
  /** Replicas that report each state, worst first. */
  replicaStates: { state: HealthStateName; count: number }[];
}

const severity: Record<HealthStateName, number> = { Healthy: 0, Degraded: 1, Throttled: 2, Open: 3, Disabled: 4 };

export function nodeVisual(item: MergedDeployment | undefined, throttledSince: number | undefined, t: number): NodeVisual {
  if (!item) return { look: "absent", cooldown: null, cooldownMs: null, probeInMs: null, replicaStates: [] };
  const deployment = item.deployment;
  const counts = new Map<HealthStateName, number>();
  for (const replica of item.replicas) counts.set(replica.state, (counts.get(replica.state) ?? 0) + 1);
  const replicaStates = [...counts].map(([state, count]) => ({ state, count }))
    .sort((a, b) => severity[b.state] - severity[a.state]);
  let cooldown: number | null = null;
  let cooldownMs: number | null = null;
  let probeInMs: number | null = null;
  const until = parseTime(deployment.throttledUntil);
  if (deployment.state === "Throttled" && until !== null) {
    cooldownMs = Math.max(0, until - t);
    const total = until - (throttledSince ?? t);
    cooldown = total > 0 ? Math.min(1, Math.max(0, cooldownMs / total)) : 1;
  }
  const openUntil = parseTime(deployment.openUntil);
  if (deployment.state === "Open" && openUntil !== null) probeInMs = Math.max(0, openUntil - t);
  const look: NodeLook = deployment.state === "Open" && deployment.halfOpen ? "halfOpen"
    : deployment.state === "Open" ? "open"
    : deployment.state === "Disabled" ? "disabled"
    : deployment.state === "Throttled" ? "throttled"
    : deployment.state === "Degraded" ? "degraded" : "healthy";
  return { look, cooldown, cooldownMs, probeInMs, replicaStates };
}

export type AttemptTone = "ok" | "throttled" | "failed" | "rejected";

/** How one backend attempt reads in the scene: a bounce colour for retries, an end colour for the last attempt. */
export function attemptTone(attempt: RequestAttempt): AttemptTone {
  if (attempt.healthOutcome === "Throttled" || attempt.status === 429) return "throttled";
  if (attempt.healthOutcome === "Success" || (attempt.status !== null && attempt.status < 400)) return "ok";
  if (attempt.healthOutcome === "Ignored" && attempt.status !== null && attempt.status < 500) return "rejected";
  return "failed";
}

export type RequestTone = "ok" | "retried" | "throttled" | "failed" | "rejected";

export function requestTone(request: RequestRecord): RequestTone {
  if (request.status < 400) return request.attempts.length > 1 ? "retried" : "ok";
  if (request.status === 429) return "throttled";
  if (request.status < 500 && request.outcome !== "failure") return "rejected";
  return "failed";
}

export const outcomeOrder = ["Success", "Throttled", "Failure", "AccountFailure", "Misconfigured", "Ignored"] as const;

export interface OutcomeShare { outcome: string; share: number; count: number }

export function outcomeMix(rate: DeploymentRate | undefined): OutcomeShare[] {
  if (!rate) return [];
  const total = rate.outcomes.reduce((sum, item) => sum + item.count, 0);
  if (total <= 0) return [];
  const rank = (outcome: string) => {
    const index = outcomeOrder.indexOf(outcome as (typeof outcomeOrder)[number]);
    return index < 0 ? outcomeOrder.length : index;
  };
  return rate.outcomes.filter((item) => item.count > 0)
    .map((item) => ({ outcome: item.outcome, count: item.count, share: item.count / total }))
    .sort((a, b) => rank(a.outcome) - rank(b.outcome));
}

const regionNames: Record<string, string> = {
  swedencentral: "Sweden Central",
  francecentral: "France Central",
  germanywestcentral: "Germany West Central",
  westeurope: "West Europe",
  northeurope: "North Europe",
  switzerlandnorth: "Switzerland North",
  uksouth: "UK South",
  eastus: "East US",
  eastus2: "East US 2",
  westus: "West US",
  westus3: "West US 3",
  southcentralus: "South Central US",
  japaneast: "Japan East",
  australiaeast: "Australia East",
};

/** Azure region display name; unknown regions fall back to the identifier. */
export function regionName(region: string): string {
  return regionNames[region.toLowerCase()] ?? region;
}

/** Compact region label for attempt chains: "sweden", "eastus2". */
export function regionShort(region: string): string {
  const name = region.toLowerCase();
  const match = /^(.*?)(central|westcentral|north|south|east|west)$/.exec(name);
  return match?.[1] && !/\d/.test(name) ? match[1] : name;
}

export function modelShort(modelKey: string | null): string {
  if (!modelKey) return "unknown";
  return modelKey.split("@")[0]?.replace(/^text-embedding-/, "embed-") ?? modelKey;
}

export function tierName(tier: number): string {
  return tier === 0 ? "Provisioned" : tier === 1 ? "Standard" : tier === 2 ? "Global" : `Tier ${tier}`;
}

export function weightLabel(weight: number, tier: number): string {
  return tier === 0 ? `${weight} PTU` : `${weight}K TPM`;
}

export function durationLabel(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms)) return "—";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 10_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms / 1000)} s`;
}

/** "sweden 429 → france 200", the compact chain used in the feed. */
export function chainLabel(request: RequestRecord): string {
  if (request.attempts.length === 0) return `LB ${request.status}`;
  return request.attempts.map((attempt) => `${regionShort(attempt.region)} ${attempt.status ?? "×"}`).join(" → ");
}

/** Text twin of each node state, so colour is never the only signal. */
export function statusText(visual: NodeVisual, item: MergedDeployment | undefined): string | null {
  switch (visual.look) {
    case "throttled": return !visual.cooldownMs ? "Throttled" : `Throttled, ${Math.ceil(visual.cooldownMs / 1000)} s`;
    case "degraded": return `Slow, p95 ${durationLabel(item?.deployment.p95TtfbMs ?? null)}`;
    case "open": return !visual.probeInMs ? "Circuit open" : `Open, probe in ${Math.ceil(visual.probeInMs / 1000)} s`;
    case "halfOpen": return "Probing";
    case "disabled": return "Disabled";
    case "absent": return "Not reported";
    default: return null;
  }
}

/** A place's name in the funnel: the region, or "Global" for a Global deployment. */
export function placeName(deployment: Pick<DashboardDeployment, "zone" | "region">): string {
  return deployment.zone === "global" ? "Global" : regionName(deployment.region);
}
