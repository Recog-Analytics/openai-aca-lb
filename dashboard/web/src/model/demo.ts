import { DemoHistory, type Incident } from "./demoHistory";
import {
  baseTtfb, decorate, productionFlows, productionRate, productionReplicas, random, requestContext, type Flow, type RequestContext,
} from "./demoTraffic";
import { productionInventory } from "./inventory";
import type {
  CompactState, DashboardDeployment, DashboardFrame, DashboardRoute, DeploymentRate, HealthStateName, HistoryResponse, MergedDeployment,
  RequestAttempt, RequestRecord, RouteTick, SummaryBucket,
} from "./types";

export { random } from "./demoTraffic";

export const scenarios = [
  "calm", "sweden-slow", "france-throttled", "eastus2-outage", "eu-down",
  "production", "production-sweden-slow", "production-france-throttled", "production-sweden-outage", "production-eu-down",
] as const;
export type Scenario = (typeof scenarios)[number];

export const scenarioLabels: Record<Scenario, string> = {
  calm: "Devkit, calm",
  "sweden-slow": "Devkit, Sweden slow",
  "france-throttled": "Devkit, France throttled",
  "eastus2-outage": "Devkit, East US 2 outage",
  "eu-down": "Devkit, EU down",
  production: "Production",
  "production-sweden-slow": "Production, Sweden slow",
  "production-france-throttled": "Production, France throttled",
  "production-sweden-outage": "Production, Sweden outage",
  "production-eu-down": "Production, EU down",
};

export function isScenario(value: string | null): value is Scenario {
  return value !== null && (scenarios as readonly string[]).includes(value);
}

interface AccountFault { ttfbMs?: number; slowFactor?: number; throttleRate?: number; retryAfterMs?: number; errorRate?: number; outage?: boolean }

const faults: Record<Scenario, Record<string, AccountFault>> = {
  calm: {},
  "sweden-slow": { "oai-swedencentral": { ttfbMs: 3800 } },
  "france-throttled": { "oai-francecentral": { throttleRate: 0.75, retryAfterMs: 7000 } },
  "eastus2-outage": { "oai-eastus2": { outage: true } },
  "eu-down": { "oai-swedencentral": { outage: true }, "oai-francecentral": { outage: true }, "oai-germanywestcentral": { outage: true } },
  production: {},
  "production-sweden-slow": { "oai-swedencentral": { slowFactor: 6 } },
  "production-france-throttled": { "oai-francecentral": { throttleRate: 0.75, retryAfterMs: 7000 } },
  "production-sweden-outage": { "oai-swedencentral": { outage: true } },
  "production-eu-down": { "oai-swedencentral": { outage: true }, "oai-francecentral": { outage: true }, "oai-germanywestcentral": { outage: true } },
};

const minute = 60_000;
/** Past incidents of the production scenarios, so longer ranges show something; relative to the stream's start. */
const productionPast: [kind: Incident["kind"], account: string, deployment: string | undefined, startsAgo: number, minutes: number][] = [
  ["throttle", "oai-francecentral", undefined, 42 * minute, 4],
  ["slow", "oai-swedencentral", undefined, 185 * minute, 15],
  ["errors", "oai-swedencentral", "llm-gpt-5-mini", 540 * minute, 3],
];

function incidents(scenario: Scenario, start: number): Incident[] {
  const past = scenario.startsWith("production")
    ? productionPast.map(([kind, account, deployment, ago, length]): Incident => ({ kind, account, deployment, from: start - ago, to: start - ago + length * minute }))
    : [];
  // The scenario's own fault began a few minutes before the stream starts.
  const current = Object.entries(faults[scenario]).map(([account, fault]): Incident => ({
    kind: fault.outage ? "outage" : fault.throttleRate ? "throttle" : fault.errorRate ? "errors" : "slow", account, from: start - 5 * minute, to: Infinity,
  }));
  return [...past, ...current];
}

// Mirrors tools/devkit/config/scenario.yaml so demo screenshots match the local stack.
const inventory: [account: string, region: string, name: string, model: string, tier: number, zone: string, weight: number][] = [
  ["oai-swedencentral", "swedencentral", "gpt4o", "gpt-4o@2024-11-20", 1, "eu", 120],
  ["oai-swedencentral", "swedencentral", "embeddings", "text-embedding-3-large@1", 1, "eu", 60],
  ["oai-francecentral", "francecentral", "gpt4o", "gpt-4o@2024-11-20", 1, "eu", 80],
  ["oai-germanywestcentral", "germanywestcentral", "gpt4o", "gpt-4o@2024-11-20", 1, "eu", 50],
  ["oai-germanywestcentral", "germanywestcentral", "embeddings", "text-embedding-3-large@1", 1, "eu", 40],
  ["oai-eastus2", "eastus2", "gpt4o-ptu", "gpt-4o@2024-11-20", 0, "us", 10],
  ["oai-eastus2", "eastus2", "gpt4o", "gpt-4o@2024-11-20", 1, "us", 150],
  ["oai-eastus2", "eastus2", "embeddings", "text-embedding-3-large@1", 1, "us", 80],
  ["oai-global", "swedencentral", "gpt4o-global", "gpt-4o@2024-11-20", 2, "global", 200],
];

// Each caller: its zones in order (the first is the default) and its share of traffic.
const orchestrator = { name: "orchestrator", home: "eu", alternate: "global" };
const devkit = { name: "devkit", home: "eu", alternate: null };
const reporting = { name: "reporting", home: "us", alternate: "global" };
const devkitFlows: Flow[] = [[orchestrator, 0.62], [devkit, 0.2], [reporting, 0.18]].flatMap(([caller, share]) => {
  const { name, home, alternate } = caller as typeof orchestrator | typeof devkit;
  const zones: [string, number][] = alternate ? [[home, 0.92], [alternate, 0.08]] : [[home, 1]];
  return zones.flatMap(([zone, zoneShare]) => [["text-embedding-3-large@1", 0.22], ["gpt-4o@2024-11-20", 0.78]].map(([modelKey, modelShare]) =>
    ({ caller: name, modelKey: String(modelKey), zone, share: (share as number) * zoneShare * Number(modelShare) })));
});

interface Circuit { openUntil: number | null; backoffMs: number; failures: number[]; consecutive: number }
interface Sim {
  deployment: DashboardDeployment;
  /** Shared by every deployment of the same account. */
  account: Circuit;
  throttledUntil: number;
  circuit: Circuit;
  ttfbs: number[];
  degraded: boolean;
}

const circuit = (): Circuit => ({ openUntil: null, backoffMs: 30_000, failures: [], consecutive: 0 });

/**
 * An in-browser stand-in for /api/stream. It simulates the LB's selection, retries and health rules closely enough
 * to make each devkit preset look right, and emits frames in the service's exact wire shape.
 */
export class DemoStream {
  private readonly sims: Sim[];
  private readonly rand: () => number;
  private readonly production: boolean;
  private readonly replicas: readonly [string, string];
  private readonly past: DemoHistory;
  private now: number;
  private sequence = 0;

  constructor(private readonly scenario: Scenario, start: number, seed = 7) {
    this.rand = random(seed);
    this.now = start - 120_000;
    this.production = scenario.startsWith("production");
    this.replicas = this.production ? productionReplicas : ["lb--demo-7f9d", "lb--demo-c41a"];
    const accounts = new Map<string, Circuit>();
    const shared = (name: string) => {
      const existing = accounts.get(name);
      if (existing) return existing;
      const created = circuit();
      accounts.set(name, created);
      return created;
    };
    const deployments: DashboardDeployment[] = this.production
      ? productionInventory.map((d) => ({
        id: d.id, account: d.account, deployment: d.deployment, region: d.region, modelKey: d.model, tier: d.tier, zone: d.zone, weight: d.weight,
        state: "Healthy", p95TtfbMs: null, accountOpen: false,
      }))
      : inventory.map(([account, region, name, model, tier, zone, weight]) => ({
        id: `/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/devkit/providers/Microsoft.CognitiveServices/accounts/${account}/deployments/${name}`,
        account, deployment: name, region, modelKey: model, tier, zone, weight, state: "Healthy", p95TtfbMs: null, accountOpen: false,
      }));
    this.sims = deployments.map((deployment) => ({
      account: shared(deployment.account), deployment, throttledUntil: 0, circuit: circuit(), ttfbs: [], degraded: false,
    }));
    this.past = new DemoHistory({
      seed, deployments, flows: this.production ? productionFlows : devkitFlows, rate: this.production ? productionRate : () => 27,
      incidents: incidents(scenario, start), simStart: this.now, clientErrorRate: this.production ? 0.002 : 0,
    });
  }

  /** Two minutes of history, as the service's first SSE event would carry. */
  snapshot(): DashboardFrame {
    const history: RequestRecord[] = [];
    const routeHistory: RouteTick[] = [];
    for (let second = 0; second < 120; second++) {
      const { requests, routes } = this.advance();
      history.push(...sample(requests, 100, this.rand));
      routeHistory.push({ at: new Date(this.now).toISOString(), routes });
    }
    return { ...this.frame([]), requests: history, routes: [], routeHistory, summary: this.past.summaries24h() };
  }

  /** One second later: the next delta. Requests are sampled like the service does; counts and routes are not. */
  tick(): DashboardFrame {
    const { requests, counts, routes, closed } = this.advance();
    return { ...this.frame(counts), requests: sample(requests, 100, this.rand), routes, routeHistory: [], summary: closed ? [closed] : [] };
  }

  /** What GET /api/history would answer: `from`/`to` in ms, `resolution` in seconds. Throws where the service answers 400. */
  history(from: number, to: number, resolution: number): HistoryResponse {
    const deployments = this.sims.map((sim): MergedDeployment => ({
      deployment: { ...sim.deployment, state: "Healthy", p95TtfbMs: null, accountOpen: false, throttledUntil: null, openUntil: null, halfOpen: false },
      replicas: this.replicas.map((replica) => ({ replica, state: "Healthy", p95TtfbMs: null, accountOpen: false })),
    }));
    return this.past.history(from, to, resolution, deployments);
  }

  private advance(): { requests: RequestRecord[]; counts: DeploymentRate[]; routes: DashboardRoute[]; closed: SummaryBucket | null } {
    const from = this.now;
    this.now += 1000;
    let count: number;
    if (this.production) count = Math.max(0, Math.round(productionRate(this.now) + (this.rand() - 0.5) * 5));
    else {
      const wave = 1 + 0.25 * Math.sin(this.now / 9000) + 0.1 * Math.sin(this.now / 2300);
      count = Math.round(24 * wave + this.rand() * 6);
    }
    const completions = Array.from({ length: count }, () => from + this.rand() * 1000).sort((a, b) => a - b);
    const requests = completions.map((at) => (this.production ? this.productionRequest(at) : this.request(at)));
    const tally = new Map<string, Map<string, number>>();
    for (const request of requests)
      for (const attempt of request.attempts) {
        const id = attempt.deploymentId ?? attempt.deployment;
        const outcomes = tally.get(id) ?? new Map<string, number>();
        outcomes.set(attempt.healthOutcome, (outcomes.get(attempt.healthOutcome) ?? 0) + 1);
        tally.set(id, outcomes);
      }
    const counts = [...tally].map(([deploymentId, outcomes]) => ({
      deploymentId,
      requestsPerSecond: [...outcomes.values()].reduce((a, b) => a + b, 0),
      outcomes: [...outcomes].map(([outcome, value]) => ({ deploymentId, outcome, count: value })),
    }));
    const routes = new Map<string, DashboardRoute>();
    for (const request of requests) {
      const hops = request.attempts.map((attempt) => ({ deploymentId: attempt.deploymentId ?? attempt.deployment, outcome: attempt.healthOutcome }));
      const key = [request.caller, request.modelKey, request.zone, request.status, ...hops.map((hop) => `${hop.deploymentId}~${hop.outcome}`)].join("|");
      const existing = routes.get(key);
      if (existing) existing.count++;
      else routes.set(key, {
        caller: request.caller, modelKey: request.modelKey, zone: request.zone, status: request.status, hops, count: 1,
        ...(request.poolKind ? { poolKind: request.poolKind, pool: request.pool ?? null } : {}),
      });
    }
    const states = this.sims.flatMap((sim): CompactState[] => {
      const health = this.health(sim);
      return health.state === "Healthy" ? [] : [{
        deploymentId: sim.deployment.id, state: health.state, seconds: 1, accountOpen: sim.account.openUntil !== null, halfOpen: health.halfOpen, p95TtfbMs: p95(sim.ttfbs),
      }];
    });
    const list = [...routes.values()];
    return { requests, counts, routes: list, closed: this.past.record(list, states) };
  }

  private productionRequest(completedAt: number): RequestRecord {
    const flow = this.pickFlow();
    const context = requestContext(flow.caller, flow.modelKey, this.rand);
    const inPool = (sim: Sim) => (flow.poolKind === "deployment" ? sim.deployment.deployment === flow.pool : sim.deployment.modelKey === flow.modelKey);
    const attempts: RequestAttempt[] = [];
    const tried = new Set<string>();
    let elapsed = 0;
    let status = 503;
    for (let index = 0; index < 3; index++) {
      const sim = this.select(inPool, flow.zone, tried, completedAt);
      if (!sim) {
        if (attempts.length === 0) status = this.sims.some((item) => inPool(item) && item.throttledUntil > completedAt) ? 429 : 503;
        break;
      }
      tried.add(sim.deployment.id);
      const attempt = this.attempt(sim, completedAt, context);
      attempts.push(attempt);
      elapsed += attempt.ttfbMs ?? 900;
      status = attempt.status ?? 502;
      if (attempt.retryReason === null) break;
    }
    const success = status < 400;
    const audio = context.operation === "audio.transcriptions";
    const body = !success ? 0 : context.streaming ? 2500 + this.rand() * 9000 : audio ? 800 + this.rand() * 3000 : 600 + this.rand() * 2400;
    const durationMs = Math.round(elapsed + body);
    const id = `demo-${(this.sequence++).toString(36).padStart(6, "0")}`;
    return {
      id, startedAt: new Date(completedAt - durationMs).toISOString(), caller: flow.caller,
      requestedModel: flow.poolKind === "deployment" ? flow.pool ?? null : flow.modelKey.split("@")[0] ?? flow.modelKey, modelKey: flow.modelKey,
      zone: flow.zone, status, durationMs, attempts,
      outcome: success ? "success" : attempts.length === 0 || (status < 500 && status !== 429) ? "error" : "failure",
      poolKind: flow.poolKind ?? "model", pool: flow.pool ?? flow.modelKey, ...context,
    };
  }

  private pickFlow(): Flow {
    const total = productionFlows.reduce((sum, flow) => sum + flow.share, 0);
    let pick = this.rand() * total;
    for (const flow of productionFlows) {
      pick -= flow.share;
      if (pick <= 0) return flow;
    }
    return productionFlows[0] as Flow;
  }

  private request(completedAt: number): RequestRecord {
    const pick = this.rand();
    const caller = pick < 0.62 ? orchestrator : pick < 0.82 ? devkit : reporting;
    const zone = caller.alternate !== null && this.rand() < 0.08 ? caller.alternate : caller.home;
    const embeddings = this.rand() < 0.22;
    const modelKey = embeddings ? "text-embedding-3-large@1" : "gpt-4o@2024-11-20";
    const streaming = !embeddings && this.rand() < 0.6;
    const attempts: RequestAttempt[] = [];
    const tried = new Set<string>();
    let elapsed = 0;
    let status = 503;
    for (let index = 0; index < 3; index++) {
      const sim = this.select((item) => item.deployment.modelKey === modelKey, zone, tried, completedAt);
      if (!sim) {
        if (attempts.length === 0)
          status = this.sims.some((item) => item.deployment.modelKey === modelKey && item.throttledUntil > completedAt) ? 429 : 503;
        break;
      }
      tried.add(sim.deployment.id);
      const attempt = this.attempt(sim, completedAt, null);
      attempts.push(attempt);
      elapsed += attempt.ttfbMs ?? 900;
      status = attempt.status ?? 502;
      if (attempt.retryReason === null) break;
    }
    const success = status < 400;
    const body = !success ? 0 : embeddings ? 40 + this.rand() * 80 : streaming ? 2500 + this.rand() * 9000 : 600 + this.rand() * 2400;
    const durationMs = Math.round(elapsed + body);
    const id = `demo-${(this.sequence++).toString(36).padStart(6, "0")}`;
    return {
      id, startedAt: new Date(completedAt - durationMs).toISOString(), caller: caller.name, requestedModel: modelKey.split("@")[0] ?? modelKey,
      modelKey, zone, streaming, status, durationMs, attempts, outcome: success ? "success" : attempts.length === 0 ? "error" : "failure",
    };
  }

  private available(sim: Sim, now: number): boolean {
    const open = (c: Circuit) => c.openUntil !== null && now < c.openUntil;
    return sim.throttledUntil <= now && !open(sim.circuit) && !open(sim.account);
  }

  private select(inPool: (sim: Sim) => boolean, zone: string, tried: Set<string>, now: number): Sim | null {
    const candidates = this.sims.filter((sim) => inPool(sim) && !tried.has(sim.deployment.id) &&
      (zone === "global" || sim.deployment.zone === zone) && this.available(sim, now));
    if (candidates.length === 0) return null;
    const degraded = candidates.filter((sim) => sim.degraded);
    const healthy = candidates.filter((sim) => !sim.degraded);
    if (degraded.length > 0 && (healthy.length === 0 || this.rand() < 0.05)) return this.weighted(degraded);
    const tier = Math.min(...healthy.map((sim) => sim.deployment.tier));
    return this.weighted(healthy.filter((sim) => sim.deployment.tier === tier));
  }

  private weighted(items: Sim[]): Sim | null {
    const total = items.reduce((sum, sim) => sum + sim.deployment.weight, 0);
    let pick = this.rand() * total;
    for (const sim of items) {
      pick -= sim.deployment.weight;
      if (pick <= 0) return sim;
    }
    return items.at(-1) ?? null;
  }

  /** One backend attempt. `context` is null for the devkit scenarios, whose attempts carry no Azure response details. */
  private attempt(sim: Sim, now: number, context: RequestContext | null): RequestAttempt {
    const d = sim.deployment;
    const fault = faults[this.scenario][d.account] ?? {};
    const base = { deployment: d.deployment, account: d.account, region: d.region, tier: d.tier, deploymentId: d.id };
    const attempt = this.respond(sim, now, context, fault, base);
    return context ? decorate(attempt, context, this.rand, fault.retryAfterMs) : attempt;
  }

  private respond(sim: Sim, now: number, context: RequestContext | null, fault: AccountFault,
    base: Pick<RequestAttempt, "deployment" | "account" | "region" | "tier" | "deploymentId">): RequestAttempt {
    const d = sim.deployment;
    const account = sim.account;
    if (fault.outage) {
      // Connection refused opens the whole account at once.
      this.fail(account, now, true);
      this.fail(sim.circuit, now, false);
      return { ...base, status: null, ttfbMs: null, healthOutcome: "AccountFailure", retryReason: "account_failure" };
    }
    if (account.openUntil !== null) this.close(account);
    if (this.rand() < (fault.throttleRate ?? 0)) {
      sim.throttledUntil = Math.max(sim.throttledUntil, now + (fault.retryAfterMs ?? 10_000));
      return { ...base, status: 429, ttfbMs: 40 + this.rand() * 60, healthOutcome: "Throttled", retryReason: "throttled" };
    }
    if (this.rand() < (fault.errorRate ?? 0.004)) {
      this.fail(sim.circuit, now, false);
      return { ...base, status: 500, ttfbMs: 200 + this.rand() * 300, healthOutcome: "Failure", retryReason: "backend_error" };
    }
    if (context && context.operation !== "audio.transcriptions" && this.rand() < 0.002)
      return { ...base, status: 400, ttfbMs: 150 + this.rand() * 100, healthOutcome: "Ignored", retryReason: null };
    const embeddings = d.modelKey.startsWith("text-embedding");
    const mean = fault.ttfbMs ?? (context ? baseTtfb(d.modelKey) : embeddings ? 120 : d.tier === 0 ? 260 : 420) * (fault.slowFactor ?? 1);
    const ttfb = Math.round(mean * (0.7 + this.rand() * 0.6) + (this.rand() < 0.05 ? mean : 0));
    sim.ttfbs.push(ttfb);
    if (sim.ttfbs.length > 40) sim.ttfbs.shift();
    this.close(sim.circuit);
    this.evaluateLatency(d.modelKey);
    return { ...base, status: 200, ttfbMs: ttfb, healthOutcome: "Success", retryReason: null };
  }

  private fail(c: Circuit, now: number, immediate: boolean): void {
    if (c.openUntil !== null) {
      // A failed half-open probe doubles the wait, up to five minutes.
      if (now >= c.openUntil) {
        c.backoffMs = Math.min(c.backoffMs * 2, 300_000);
        c.openUntil = now + c.backoffMs;
      }
      return;
    }
    c.failures = [...c.failures.filter((at) => at > now - 30_000), now];
    c.consecutive++;
    if (immediate || c.consecutive >= 3 || c.failures.length >= 5) c.openUntil = now + c.backoffMs;
  }

  private close(c: Circuit): void {
    c.openUntil = null;
    c.backoffMs = 30_000;
    c.failures = [];
    c.consecutive = 0;
  }

  /** Re-evaluates one model's deployments: only their samples changed. */
  private evaluateLatency(modelKey: string): void {
    const group = this.sims.filter((sim) => sim.deployment.modelKey === modelKey);
    const p95s = new Map(group.map((sim) => [sim, p95(sim.ttfbs)]));
    for (const sim of group) {
      const own = p95s.get(sim) ?? null;
      const peers = group.filter((item) => item !== sim).map((item) => p95s.get(item) ?? null)
        .filter((value): value is number => value !== null).sort((a, b) => a - b);
      const median = peers[Math.floor(peers.length / 2)];
      if (own === null || median === undefined || sim.ttfbs.length < 20) continue;
      if (!sim.degraded && own > 2 * median && own > 2000) sim.degraded = true;
      else if (sim.degraded && own < 1.5 * median) sim.degraded = false;
    }
  }

  private health(sim: Sim): { state: HealthStateName; openUntil: number | null; halfOpen: boolean } {
    const openUntils = [sim.circuit.openUntil, sim.account.openUntil].filter((value): value is number => value !== null);
    const openUntil = openUntils.length > 0 ? Math.max(...openUntils) : null;
    const state: HealthStateName = openUntil !== null ? "Open" : sim.throttledUntil > this.now ? "Throttled" : sim.degraded ? "Degraded" : "Healthy";
    return { state, openUntil, halfOpen: openUntil !== null && openUntil <= this.now };
  }

  private frame(counts: DeploymentRate[]): DashboardFrame {
    const now = this.now;
    const [first, second] = this.replicas;
    const deployments: MergedDeployment[] = this.sims.map((sim) => {
      const account = sim.account;
      const { state, openUntil, halfOpen } = this.health(sim);
      const p = p95(sim.ttfbs);
      const deployment: DashboardDeployment = {
        ...sim.deployment, state, p95TtfbMs: p, accountOpen: account.openUntil !== null,
        throttledUntil: state === "Throttled" ? new Date(sim.throttledUntil).toISOString() : null,
        openUntil: openUntil === null ? null : new Date(openUntil).toISOString(),
        halfOpen,
      };
      // Throttling is per replica, so the second replica often has not seen the 429 yet.
      const lagging: HealthStateName = state === "Throttled" && sim.throttledUntil % 2000 < 1000 ? "Healthy" : state;
      return {
        deployment,
        replicas: [
          { replica: first, state, p95TtfbMs: p, accountOpen: deployment.accountOpen },
          { replica: second, state: lagging, p95TtfbMs: p, accountOpen: deployment.accountOpen },
        ],
      };
    });
    return {
      at: new Date(now).toISOString(), replicas: [first, second], deployments, requests: [], counts,
      retention: { secondsFrom: new Date(now - 3_600_000).toISOString(), minutesFrom: new Date(now - 86_400_000).toISOString() },
      replicaNumbers: { [first]: 1, [second]: 2 },
    };
  }
}

/** Uniform sample without replacement, in completion order. */
function sample<T>(items: T[], limit: number, rand: () => number): T[] {
  if (items.length <= limit) return items;
  const indexes = items.map((_, index) => index);
  for (let index = indexes.length - 1; index > 0; index--) {
    const other = Math.floor(rand() * (index + 1));
    [indexes[index], indexes[other]] = [indexes[other] ?? 0, indexes[index] ?? 0];
  }
  return indexes.slice(0, limit).sort((a, b) => a - b).map((index) => items[index]).filter((item): item is T => item !== undefined);
}

function p95(values: number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)] ?? null;
}
