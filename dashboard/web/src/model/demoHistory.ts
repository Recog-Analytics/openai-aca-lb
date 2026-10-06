// The demo's past: exact per-minute route counts drawn from the traffic mix, without simulating each request.
import { baseTtfb, decorate, hash, random, requestContext, type Flow } from "./demoTraffic";
import type {
  CompactState, DashboardDeployment, DashboardHop, DashboardRoute, HistoryBucket, HistoryResponse, MergedDeployment, ProblemState,
  RequestAttempt, RequestRecord, SummaryBucket,
} from "./types";

export type IncidentKind = "slow" | "throttle" | "errors" | "outage";

/** A past or ongoing fault of an account, or of one deployment name in it. `from`/`to` in ms. */
export interface Incident {
  kind: IncidentKind;
  account: string;
  deployment?: string;
  from: number;
  to: number;
}

export interface HistoryOptions {
  seed: number;
  deployments: DashboardDeployment[];
  flows: Flow[];
  /** Requests per second at an instant. */
  rate: (at: number) => number;
  incidents: Incident[];
  /** Start of the live simulation (ms): earlier seconds are statistical, later ones recorded. */
  simStart: number;
  /** Share of chat requests that Azure's content filter rejects. */
  clientErrorRate: number;
}

/** A route without its count; one shared object per distinct route, so counts merge by identity. */
interface Template {
  route: Omit<DashboardRoute, "count">;
  notable: boolean;
  served: boolean;
  retried: boolean;
  failed: boolean;
  refused: boolean;
}

type Counts = Map<Template, number>;
interface Slice { routes: Counts; states: CompactState[] }
interface Condition { kind: IncidentKind; fraction: number }
interface FlowPlan { flow: Flow; candidates: DashboardDeployment[]; calm: [Template, number][] }

export const resolutions = [1, 10, 30, 60, 300, 600, 1800, 3600] as const;
export const maxBuckets = 1500;
const maxRequests = 1000;
const severity: Record<ProblemState, number> = { Degraded: 1, Throttled: 2, Open: 3, Disabled: 4 };
const kindSeverity: Record<IncidentKind, number> = { slow: 1, throttle: 2, errors: 3, outage: 4 };
const stateOf: Record<IncidentKind, ProblemState> = { slow: "Degraded", throttle: "Throttled", errors: "Open", outage: "Open" };
const baselineErrorRate = 0.004;

export class DemoHistory {
  private readonly templates = new Map<string, Template>();
  private readonly byId: Map<string, DashboardDeployment>;
  private readonly plans: FlowPlan[];
  private readonly simStartSecond: number;
  private readonly recorded = new Map<number, Slice>();
  private readonly minutes = new Map<number, Slice>();
  private readonly secondsOf = new Map<number, Slice[]>();
  private readonly summaries = new Map<number, SummaryBucket>();
  /** Exclusive end of the data, in epoch seconds. */
  private end: number;

  constructor(private readonly options: HistoryOptions) {
    this.byId = new Map(options.deployments.map((item) => [item.id, item]));
    this.simStartSecond = Math.floor(options.simStart / 1000);
    this.end = this.simStartSecond;
    this.plans = options.flows.map((flow) => {
      const candidates = options.deployments.filter((d) => (flow.poolKind === "deployment" ? d.deployment === flow.pool : d.modelKey === flow.modelKey) &&
        (flow.zone === "global" || d.zone === flow.zone));
      const plan: FlowPlan = { flow, candidates, calm: [] };
      plan.calm = this.cells(plan, null);
      return plan;
    });
  }

  get endSecond(): number {
    return this.end;
  }

  /** Stores the next live second; returns that second's minute summary when it closes a minute. */
  record(routes: DashboardRoute[], states: CompactState[]): SummaryBucket | null {
    const counts: Counts = new Map();
    for (const route of routes) {
      const { count, ...rest } = route;
      const template = this.intern(rest);
      counts.set(template, (counts.get(template) ?? 0) + count);
    }
    this.recorded.set(this.end++, { routes: counts, states });
    this.recorded.delete(this.end - 3700);
    this.minutes.delete(Math.floor(this.end / 60) - 1500);
    this.summaries.delete(Math.floor(this.end / 60) - 1500);
    return this.end % 60 === 0 ? this.summary(this.end / 60 - 1) : null;
  }

  /** Every closed minute of the last 24 hours, oldest first. */
  summaries24h(): SummaryBucket[] {
    const last = Math.floor(this.end / 60);
    return Array.from({ length: 1440 }, (_, index) => this.summary(last - 1440 + index));
  }

  history(from: number, to: number, resolution: number, deployments: MergedDeployment[]): HistoryResponse {
    if (!(resolutions as readonly number[]).includes(resolution)) throw new Error(`Unsupported resolution ${resolution}`);
    if (!(from < to)) throw new Error("from must be before to");
    const retained = this.end - (resolution < 60 ? 3600 : 86_400);
    const first = Math.max(Math.ceil(from / 1000), retained);
    const last = Math.min(Math.ceil(to / 1000), this.end);
    const start = Math.floor(first / resolution) * resolution;
    const count = last > first ? Math.ceil((last - start) / resolution) : 0;
    if (count > maxBuckets) throw new Error(`${count} buckets exceed the limit of ${maxBuckets}`);
    const buckets: HistoryBucket[] = [];
    const slices: { from: number; to: number; routes: Counts }[] = [];
    for (let index = 0; index < count; index++) {
      const bucketStart = start + index * resolution;
      const s0 = Math.max(bucketStart, first);
      const s1 = Math.min(bucketStart + resolution, last);
      if (s1 <= s0) continue;
      const routes: Counts = new Map();
      const states = new Map<string, CompactState>();
      for (let second = s0; second < s1;) {
        const minute = Math.floor(second / 60);
        const whole = second === minute * 60 && second + 60 <= s1;
        const slice = whole ? this.minute(minute) : this.second(second);
        if (slice) {
          for (const [template, value] of slice.routes) routes.set(template, (routes.get(template) ?? 0) + value);
          for (const state of slice.states) mergeState(states, state);
        }
        second += whole ? 60 : 1;
      }
      buckets.push({ at: iso((bucketStart + resolution) * 1000), seconds: s1 - s0, routes: emit(routes), states: [...states.values()] });
      slices.push({ from: s0, to: s1, routes });
    }
    return {
      from: iso(first * 1000), to: iso(Math.max(first, last) * 1000), resolution, buckets,
      requests: this.sampleRequests(slices, hash(this.options.seed, first, last ^ resolution)), deployments,
    };
  }

  // ---- minutes and seconds

  private summary(minute: number): SummaryBucket {
    const cached = this.summaries.get(minute);
    if (cached) return cached;
    const slice = this.minute(minute);
    const bucket: SummaryBucket = { at: iso((minute + 1) * 60_000), total: 0, served: 0, retried: 0, failed: 0, refused: 0, worst: null, unhealthy: slice.states.length };
    for (const [template, count] of slice.routes) {
      bucket.total += count;
      if (template.served) bucket.served += count;
      if (template.retried) bucket.retried += count;
      if (template.failed) bucket.failed += count;
      if (template.refused) bucket.refused += count;
    }
    for (const state of slice.states) if (bucket.worst === null || severity[state.state] > severity[bucket.worst]) bucket.worst = state.state;
    if (minute * 60 + 60 <= this.end) this.summaries.set(minute, bucket);
    return bucket;
  }

  /** A minute's routes and worst states: drawn from the mix before the live part, summed from its seconds after. */
  private minute(minute: number): Slice {
    const cached = this.minutes.get(minute);
    if (cached) return cached;
    let slice: Slice;
    if (minute * 60 + 60 <= this.simStartSecond) slice = this.drawMinute(minute);
    else {
      const routes: Counts = new Map();
      const states = new Map<string, CompactState>();
      for (let second = minute * 60; second < minute * 60 + 60; second++) {
        const part = this.second(second);
        if (!part) continue;
        for (const [template, value] of part.routes) routes.set(template, (routes.get(template) ?? 0) + value);
        for (const state of part.states) mergeState(states, state);
      }
      slice = { routes, states: [...states.values()] };
    }
    if (minute * 60 + 60 <= this.end) this.minutes.set(minute, slice);
    return slice;
  }

  private second(second: number): Slice | undefined {
    if (second >= this.simStartSecond) return this.recorded.get(second);
    const minute = Math.floor(second / 60);
    let seconds = this.secondsOf.get(minute);
    if (!seconds) {
      if (this.secondsOf.size > 120) this.secondsOf.clear();
      seconds = this.spread(minute);
      this.secondsOf.set(minute, seconds);
    }
    return seconds[second - minute * 60];
  }

  private drawMinute(minute: number): Slice {
    const { seed, rate } = this.options;
    const total = Math.round(60 * rate(minute * 60_000 + 30_000) * (0.94 + 0.12 * hash(seed, minute, 1)));
    const flowCounts = apportion(this.plans.map((plan, index) => plan.flow.share * (0.8 + 0.4 * hash(seed, minute, 100 + index))), total, hash(seed, minute, 2));
    const conditions = this.conditions(minute * 60, minute * 60 + 60);
    const routes: Counts = new Map();
    this.plans.forEach((plan, index) => {
      const count = flowCounts[index] ?? 0;
      if (count === 0) return;
      const affected = conditions.size > 0 && plan.candidates.some((d) => conditions.has(d.id));
      const cells = affected ? this.cells(plan, conditions) : plan.calm;
      const split = apportion(cells.map(([, mass]) => mass), count, hash(seed, minute, 200 + index));
      cells.forEach(([template], cell) => {
        const value = split[cell] ?? 0;
        if (value > 0) routes.set(template, (routes.get(template) ?? 0) + value);
      });
    });
    return { routes, states: this.incidentStates(minute * 60, minute * 60 + 60) };
  }

  /** Spreads a drawn minute over its 60 seconds so the seconds sum to the minute exactly. */
  private spread(minute: number): Slice[] {
    const seconds: Slice[] = Array.from({ length: 60 }, (_, index) => ({
      routes: new Map(), states: this.incidentStates(minute * 60 + index, minute * 60 + index + 1),
    }));
    let index = 0;
    for (const [template, count] of this.drawMinute(minute).routes) {
      const offset = hash(this.options.seed, minute, 1000 + index++);
      // Unit j lands in second floor((j + offset) * 60 / count); second s holds the units with j in [s * count / 60 - offset, …).
      let previous = 0;
      seconds.forEach(({ routes }, second) => {
        const next = Math.min(count, Math.max(0, Math.ceil(((second + 1) * count) / 60 - offset)));
        if (next > previous) routes.set(template, next - previous);
        previous = next;
      });
    }
    return seconds;
  }

  // ---- incidents

  private matches(incident: Incident, d: DashboardDeployment): boolean {
    return d.account === incident.account && (incident.deployment === undefined || incident.deployment === d.deployment);
  }

  private conditions(s0: number, s1: number): Map<string, Condition> {
    const result = new Map<string, Condition>();
    for (const incident of this.options.incidents) {
      const overlap = Math.min(s1 * 1000, incident.to) - Math.max(s0 * 1000, incident.from);
      if (overlap <= 0) continue;
      for (const d of this.options.deployments) {
        if (!this.matches(incident, d)) continue;
        const existing = result.get(d.id);
        if (!existing || kindSeverity[incident.kind] > kindSeverity[existing.kind])
          result.set(d.id, { kind: incident.kind, fraction: Math.min(1, overlap / ((s1 - s0) * 1000)) });
      }
    }
    return result;
  }

  private incidentStates(s0: number, s1: number): CompactState[] {
    const states = new Map<string, CompactState>();
    for (const incident of this.options.incidents) {
      const overlap = Math.min(s1 * 1000, incident.to) - Math.max(s0 * 1000, incident.from);
      if (overlap <= 0) continue;
      for (const d of this.options.deployments) {
        if (!this.matches(incident, d)) continue;
        mergeState(states, {
          deploymentId: d.id, state: stateOf[incident.kind], seconds: Math.ceil(overlap / 1000), accountOpen: incident.kind === "outage", halfOpen: false,
          p95TtfbMs: incident.kind === "slow" ? Math.round(baseTtfb(d.modelKey) * 6.5) : null,
        });
      }
    }
    return [...states.values()];
  }

  // ---- route cells

  private intern(route: Omit<DashboardRoute, "count">): Template {
    const key = [route.caller, route.modelKey, route.zone, route.status, route.poolKind ?? "", route.pool ?? "",
      ...route.hops.map((hop) => `${hop.deploymentId}~${hop.outcome}`)].join("|");
    const existing = this.templates.get(key);
    if (existing) return existing;
    const served = route.status < 400;
    const hops = route.hops.length;
    const created: Template = {
      route, served, retried: served && hops > 1, refused: hops === 0, failed: hops > 0 && (route.status >= 500 || route.status === 429),
      notable: route.status >= 400 || hops > 1,
    };
    this.templates.set(key, created);
    return created;
  }

  /** How one flow's requests split into routes for a minute in which some deployments are faulty. */
  private cells(plan: FlowPlan, conditions: Map<string, Condition> | null): [Template, number][] {
    const { flow, candidates } = plan;
    const masses = new Map<Template, number>();
    const add = (status: number, hops: [DashboardDeployment, string][], mass: number) => {
      if (mass <= 0) return;
      const route: Omit<DashboardRoute, "count"> = {
        caller: flow.caller, modelKey: flow.modelKey, zone: flow.zone, status,
        hops: hops.map(([d, outcome]): DashboardHop => ({ deploymentId: d.id, outcome })),
        ...(flow.poolKind ? { poolKind: flow.poolKind, pool: flow.pool ?? null } : {}),
      };
      const template = this.intern(route);
      masses.set(template, (masses.get(template) ?? 0) + mass);
    };
    if (candidates.length === 0) {
      add(503, [], 1);
      return [...masses];
    }
    const lowest = Math.min(...candidates.map((d) => d.tier));
    const primary = candidates.filter((d) => d.tier === lowest);
    const weight = primary.reduce((sum, d) => sum + d.weight, 0);
    const name = flow.modelKey.split("@")[0] ?? "";
    const clientErrors = name === "whisper" || name.startsWith("text-embedding") ? 0 : this.options.clientErrorRate;
    for (const d of primary) {
      const share = d.weight / weight;
      const condition = conditions?.get(d.id);
      const fraction = condition?.fraction ?? 0;
      const alternates = this.alternates(candidates, d, conditions);
      const spread = (mass: number, prefix: [DashboardDeployment, string][], fallback: number) => {
        if (alternates.length === 0) add(fallback, prefix, mass);
        for (const [alternate, part] of alternates) add(200, [...prefix, [alternate, "Success"]], mass * part);
      };
      const calm = share * (1 - fraction);
      add(200, [[d, "Success"]], calm * (1 - baselineErrorRate - clientErrors));
      const backup = alternates[0]?.[0];
      add(backup ? 200 : 500, backup ? [[d, "Failure"], [backup, "Success"]] : [[d, "Failure"]], calm * baselineErrorRate);
      add(400, [[d, "Ignored"]], calm * clientErrors);
      const faulty = share * fraction;
      if (!condition || faulty <= 0) continue;
      if (condition.kind === "outage") {
        spread(faulty * 0.03, [[d, "AccountFailure"]], 502);
        spread(faulty * 0.97, [], 503);
      } else if (condition.kind === "throttle") {
        add(200, [[d, "Success"]], faulty * 0.3);
        spread(faulty * 0.15, [[d, "Throttled"]], 429);
        spread(faulty * 0.55, [], 429);
      } else if (condition.kind === "errors") {
        spread(faulty * 0.3, [[d, "Failure"]], 500);
        spread(faulty * 0.7, [], 503);
      } else if (alternates.length === 0) add(200, [[d, "Success"]], faulty);
      else {
        add(200, [[d, "Success"]], faulty * 0.06);
        spread(faulty * 0.94, [], 200);
      }
    }
    return [...masses];
  }

  /** Where the LB sends a request that skips or fails on `d`: healthy peers first, by weight, heaviest first. */
  private alternates(candidates: DashboardDeployment[], d: DashboardDeployment, conditions: Map<string, Condition> | null): [DashboardDeployment, number][] {
    const others = candidates.filter((item) => item !== d);
    const clean = others.filter((item) => !conditions?.has(item.id));
    const soft = others.filter((item) => {
      const kind = conditions?.get(item.id)?.kind;
      return kind === "slow" || kind === "throttle";
    });
    const pool = clean.length > 0 ? clean : soft;
    if (pool.length === 0) return [];
    const lowest = Math.min(...pool.map((item) => item.tier));
    const chosen = pool.filter((item) => item.tier === lowest).sort((a, b) => b.weight - a.weight);
    const total = chosen.reduce((sum, item) => sum + item.weight, 0);
    return chosen.map((item) => [item, item.weight / total]);
  }

  // ---- sampled requests

  /** Up to 1000 synthetic requests: notable ones first, spread evenly over the range, then ordinary ones; oldest first. */
  private sampleRequests(slices: { from: number; to: number; routes: Counts }[], offset: number): RequestRecord[] {
    const rand = random(Math.floor(offset * 4294967296));
    const notable: [number, Template, number][] = [];
    const ordinary: [number, Template, number][] = [];
    slices.forEach((slice, index) => {
      for (const [template, count] of slice.routes) (template.notable ? notable : ordinary).push([index, template, count]);
    });
    const picked: [number, Template][] = [];
    const take = (entries: [number, Template, number][], limit: number) => {
      const total = entries.reduce((sum, [, , count]) => sum + count, 0);
      const wanted = Math.min(limit, total);
      if (wanted === 0) return;
      const step = total / wanted;
      let next = offset * step;
      let seen = 0;
      for (const [index, template, count] of entries) {
        seen += count;
        while (next < seen && picked.length < maxRequests) {
          picked.push([index, template]);
          next += step;
        }
      }
    };
    take(notable, maxRequests);
    take(ordinary, maxRequests - picked.length);
    const records = picked.map(([index, template], n) => {
      const slice = slices[index] ?? { from: 0, to: 1 };
      return this.synthesize(template.route, (slice.from + rand() * (slice.to - slice.from)) * 1000, `hist-${slice.from.toString(36)}-${n}`, rand);
    });
    return records.sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  }

  private synthesize(route: Omit<DashboardRoute, "count">, completedAt: number, id: string, rand: () => number): RequestRecord {
    const caller = route.caller ?? "unknown";
    const modelKey = route.modelKey ?? "";
    const context = requestContext(caller, modelKey, rand);
    let elapsed = 0;
    const attempts = route.hops.map((hop, index): RequestAttempt => {
      const d = this.byId.get(hop.deploymentId);
      const last = index === route.hops.length - 1;
      const status = hop.outcome === "Success" ? (route.status < 400 ? route.status : 200) : hop.outcome === "Throttled" ? 429 :
        hop.outcome === "Failure" ? (last && route.status >= 500 ? route.status : 500) : hop.outcome === "AccountFailure" ? null : route.status;
      const ttfbMs = status === null ? null : Math.round(hop.outcome === "Success" ? baseTtfb(modelKey) * (0.7 + rand() * 0.6) : hop.outcome === "Throttled" ? 40 + rand() * 60 : 150 + rand() * 300);
      elapsed += ttfbMs ?? 900;
      const retryReason = hop.outcome === "Throttled" ? "throttled" : hop.outcome === "Failure" ? "backend_error" : hop.outcome === "AccountFailure" ? "account_failure" : null;
      return decorate({
        deployment: d?.deployment ?? hop.deploymentId, account: d?.account ?? "", region: d?.region ?? "", tier: d?.tier ?? 1, status, ttfbMs,
        healthOutcome: hop.outcome, retryReason, deploymentId: hop.deploymentId,
      }, context, rand);
    });
    const success = route.status < 400;
    const durationMs = Math.round(elapsed + (!success ? 0 : context.streaming ? 2500 + rand() * 9000 : 400 + rand() * 2400));
    return {
      id, startedAt: iso(completedAt - durationMs), caller: route.caller, requestedModel: route.poolKind === "deployment" ? route.pool ?? null : modelKey.split("@")[0] ?? modelKey,
      modelKey: route.modelKey, zone: route.zone, streaming: context.streaming, status: route.status, durationMs, attempts,
      outcome: success ? "success" : attempts.length === 0 || (route.status < 500 && route.status !== 429) ? "error" : "failure",
      poolKind: route.poolKind ?? null, pool: route.pool ?? null, operation: context.operation, apiVersion: context.apiVersion,
      requestBytes: context.requestBytes, maxOutputTokens: context.maxOutputTokens,
    };
  }
}

/** Splits `total` into integers proportional to `weights` (systematic sampling): exact sum, deterministic for an offset in [0, 1). */
function apportion(weights: number[], total: number, offset: number): number[] {
  const sum = weights.reduce((a, b) => a + b, 0);
  if (sum <= 0) return weights.map(() => 0);
  let cumulative = 0;
  let previous = 0;
  return weights.map((weight, index) => {
    cumulative += weight;
    const current = index === weights.length - 1 ? total : Math.min(total, Math.floor((cumulative / sum) * total + offset));
    const count = current - previous;
    previous = current;
    return count;
  });
}

function mergeState(states: Map<string, CompactState>, state: CompactState): void {
  const existing = states.get(state.deploymentId);
  if (!existing) {
    states.set(state.deploymentId, { ...state });
    return;
  }
  if (severity[state.state] > severity[existing.state]) existing.state = state.state;
  existing.seconds += state.seconds;
  existing.accountOpen ||= state.accountOpen;
  existing.halfOpen ||= state.halfOpen;
  if (state.p95TtfbMs !== null && (existing.p95TtfbMs === null || state.p95TtfbMs > existing.p95TtfbMs)) existing.p95TtfbMs = state.p95TtfbMs;
}

function emit(routes: Counts): DashboardRoute[] {
  return [...routes].map(([template, count]) => ({ ...template.route, hops: template.route.hops.map((hop) => ({ ...hop })), count }));
}

const iso = (ms: number) => new Date(ms).toISOString();
