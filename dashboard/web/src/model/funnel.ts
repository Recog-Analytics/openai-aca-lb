import type { Tick } from "./timeline";
import type { DashboardDeployment, DashboardHop, DashboardRoute } from "./types";

/** The request filters the funnel respects; an empty string means all. */
export interface Scope { model: string; zone: string; caller: string }

export function inScope(request: { caller: string | null; modelKey: string | null; zone: string | null }, scope: Scope): boolean {
  return (!scope.model || request.modelKey === scope.model) && (!scope.zone || request.zone === scope.zone) &&
    (!scope.caller || (request.caller ?? "unknown") === scope.caller);
}

/**
 * What happened to the requests that reached one node of the funnel, in request counts (not attempts).
 * `reached` = `served` + `answered` + `throttled` + `failed`: every request that reached the node either ended here
 * or left, and a request that left is counted once, by why it left.
 */
export interface Flow {
  /** Requests with at least one attempt here. */
  reached: number;
  /** Requests finally served (status below 400) by an attempt here. */
  served: number;
  /** Requests answered here with a client error: handled, not served, not the LB's problem. */
  answered: number;
  /** Requests that left after a 429 here: retried elsewhere or failed. */
  throttled: number;
  /** Requests that left after a failure here: retried elsewhere or failed. */
  failed: number;
  /** Requests whose first attempt was here. */
  first: number;
  /** Served here although the request's preferred tier was higher: spill from a tier that could not take it. */
  spilled: number;
}

/** A retry chain or failure pattern, summed over callers. */
export interface FallbackPath {
  key: string;
  hops: DashboardHop[];
  status: number;
  count: number;
}

export interface Funnel {
  /** Seconds of ticks in the window. */
  seconds: number;
  total: number;
  served: number;
  /** Served after more than one attempt. */
  fellBack: number;
  /** Ended with a server error or 429 after at least one attempt. */
  failed: number;
  /** Answered by the LB without an attempt: no deployment was available. */
  refused: number;
  /** Ended with a client error from a deployment. */
  rejected: number;
  callers: Map<string, Flow>;
  tiers: Map<number, Flow>;
  /** Keyed by `placeKey`. */
  places: Map<string, Flow>;
  deployments: Map<string, Flow>;
  /** Fallback and failure patterns, most frequent first. */
  paths: FallbackPath[];
}

export const emptyFlow = (): Flow => ({ reached: 0, served: 0, answered: 0, throttled: 0, failed: 0, first: 0, spilled: 0 });

/**
 * Where a deployment runs, as operators name it: its Azure region, or "global" for a Global deployment, which may
 * process requests anywhere. Each place appears once in the funnel; the tier is an attribute of the deployment.
 */
export function placeKey(deployment: Pick<DashboardDeployment, "zone" | "region">): string {
  return deployment.zone === "global" ? "global" : deployment.region;
}

/** The highest tier (lowest number) a request for this model and zone can use: where the LB tries first. */
export function preferredTier(deployments: DashboardDeployment[], modelKey: string | null, zone: string | null): number | null {
  let best: number | null = null;
  for (const item of deployments)
    if (item.modelKey === modelKey && item.state !== "Disabled" && (zone === "global" || item.zone === zone))
      best = best === null ? item.tier : Math.min(best, item.tier);
  return best;
}

export function isServed(status: number): boolean {
  return status < 400;
}

export function isClientError(status: number): boolean {
  return status >= 400 && status < 500 && status !== 429;
}

/**
 * Exact traffic shares down the funnel (caller → LB → tier → region → deployment) for the requests in scope that
 * completed in the given ticks. Pure; counts come from unsampled routes, never from sampled particles.
 */
export function computeFunnel(ticks: Tick[], deployments: DashboardDeployment[], scope: Scope): Funnel {
  const byId = new Map(deployments.map((item) => [item.id, item]));
  const preferred = new Map<string, number | null>();
  const funnel: Funnel = {
    seconds: ticks.length, total: 0, served: 0, fellBack: 0, failed: 0, refused: 0, rejected: 0,
    callers: new Map(), tiers: new Map(), places: new Map(), deployments: new Map(), paths: [],
  };
  const paths = new Map<string, FallbackPath>();
  const flow = <K>(map: Map<K, Flow>, key: K): Flow => {
    let item = map.get(key);
    if (!item) map.set(key, (item = emptyFlow()));
    return item;
  };

  for (const tick of ticks)
    for (const route of tick.routes) {
      if (route.count <= 0 || !inScope(route, scope)) continue;
      const count = route.count;
      const served = isServed(route.status);
      const clientError = isClientError(route.status);
      funnel.total += count;
      const caller = flow(funnel.callers, route.caller ?? "unknown");
      caller.reached += count;
      if (route.hops.length === 0) {
        funnel.refused += count;
        caller.failed += count;
        addPath(paths, route);
        continue;
      }
      if (served) {
        funnel.served += count;
        caller.served += count;
        if (route.hops.length > 1) funnel.fellBack += count;
      } else if (clientError) {
        funnel.rejected += count;
        caller.answered += count;
      } else {
        funnel.failed += count;
        caller.failed += count;
      }
      if (route.hops.length > 1 || (!served && !clientError)) addPath(paths, route);

      // Each node a request touched counts it once: served or answered where it ended, else by why it last left.
      const last = route.hops.length - 1;
      const levels: { key: string; map: Level; hop: number }[] = [];
      route.hops.forEach((hop, index) => {
        const deployment = byId.get(hop.deploymentId);
        levels.push({ key: hop.deploymentId, map: "deployment", hop: index });
        if (!deployment) return;
        levels.push({ key: placeKey(deployment), map: "place", hop: index });
        levels.push({ key: String(deployment.tier), map: "tier", hop: index });
      });
      // Latest hop per node decides the node's outcome; the earliest decides whether it was the first choice.
      const touched = new Map<string, { map: Level; key: string; first: number; latest: number }>();
      for (const level of levels) {
        const id = `${level.map}|${level.key}`;
        const seen = touched.get(id);
        if (seen) seen.latest = level.hop;
        else touched.set(id, { map: level.map, key: level.key, first: level.hop, latest: level.hop });
      }
      const finalHop = route.hops[last];
      const finalDeployment = finalHop ? byId.get(finalHop.deploymentId) : undefined;
      let spilled = false;
      if (served && finalDeployment) {
        const key = `${route.modelKey ?? ""}|${route.zone ?? ""}`;
        if (!preferred.has(key)) preferred.set(key, preferredTier(deployments, route.modelKey, route.zone));
        const best = preferred.get(key);
        spilled = best !== null && best !== undefined && finalDeployment.tier > best;
      }
      for (const node of touched.values()) {
        const target = node.map === "tier" ? flow(funnel.tiers, Number(node.key))
          : flow(node.map === "place" ? funnel.places : funnel.deployments, node.key);
        target.reached += count;
        if (node.first === 0) target.first += count;
        if (node.latest === last && served) {
          target.served += count;
          if (spilled) target.spilled += count;
        } else if (node.latest === last && clientError) target.answered += count;
        else if (route.hops[node.latest]?.outcome === "Throttled") target.throttled += count;
        else target.failed += count;
      }
    }

  funnel.paths = [...paths.values()].sort((a, b) => b.count - a.count || a.key.localeCompare(b.key));
  return funnel;
}

type Level = "tier" | "place" | "deployment";

function addPath(paths: Map<string, FallbackPath>, route: DashboardRoute): void {
  const outcome = isServed(route.status) ? "served" : String(route.status);
  const key = `${route.hops.map((hop) => `${hop.deploymentId}~${hop.outcome}`).join(">")}|${outcome}`;
  const existing = paths.get(key);
  if (existing) existing.count += route.count;
  else paths.set(key, { key, hops: route.hops, status: route.status, count: route.count });
}

/**
 * Share of a deployment's group that its weight asks for: the group is the deployments of the same model, tier and
 * zone, which the LB splits by weight. Returns the share of all requests in scope the deployment would serve if the
 * group's served traffic followed the weights exactly. Null when the group served nothing.
 */
export function quotaShare(deployment: DashboardDeployment, deployments: DashboardDeployment[], funnel: Funnel): number | null {
  if (funnel.total === 0) return null;
  const group = deployments.filter((item) => item.modelKey === deployment.modelKey && item.tier === deployment.tier &&
    item.zone === deployment.zone && item.state !== "Disabled");
  const weight = group.reduce((sum, item) => sum + item.weight, 0);
  const served = group.reduce((sum, item) => sum + (funnel.deployments.get(item.id)?.served ?? 0), 0);
  if (weight <= 0 || served <= 0 || deployment.state === "Disabled") return null;
  return (served * deployment.weight) / weight / funnel.total;
}

/** Patterns this rare are background noise (a stray 500, one retry) and fold into one line. */
const RARE_COUNT = 3;
const RARE_SHARE = 0.005;
const MAX_PATHS = 5;

/** Fallback paths worth a row of their own, and the rest. */
export function splitPaths(funnel: Funnel): { notable: FallbackPath[]; rare: FallbackPath[] } {
  const isNotable = (path: FallbackPath) => path.count >= RARE_COUNT || (funnel.total > 0 && path.count / funnel.total >= RARE_SHARE);
  const notable = funnel.paths.filter(isNotable);
  return { notable: notable.slice(0, MAX_PATHS), rare: [...notable.slice(MAX_PATHS), ...funnel.paths.filter((path) => !isNotable(path))] };
}
