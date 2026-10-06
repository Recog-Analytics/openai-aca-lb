import type { DashboardFrame, DashboardRoute, DeploymentRate, FrameKind, MergedDeployment, RequestRecord, SummaryBucket } from "./types";

/** Scene time lags server time so late batches still play in completion order. */
export const PLAYBACK_LAG_MS = 2500;
/** The dashboard service retains two minutes of requests; the scrub window matches it. */
export const WINDOW_MS = 120_000;

export interface TimedRequest {
  request: RequestRecord;
  /** Server-clock time at which the scene starts playing this request. */
  playAt: number;
  completedAt: number;
}

/**
 * Unsampled route counts of requests that completed in the `seconds` before `at`: one second for a live tick, a whole
 * bucket for history.
 */
export interface Tick {
  at: number;
  routes: DashboardRoute[];
  seconds?: number;
}

export interface FrameState {
  at: number;
  replicas: string[];
  deployments: Map<string, MergedDeployment>;
  rates: Map<string, DeploymentRate>;
  /** Start of the current uninterrupted Throttled run, per deployment, so a cooldown ring knows its full length. */
  throttledSince: Map<string, number>;
}

export function parseTime(value: string | null | undefined): number | null {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : null;
}

/** Stable 32-bit FNV-1a hash; spreads late requests and makes sampling deterministic. */
export function hash(value: string): number {
  let result = 0x811c9dc5;
  for (let index = 0; index < value.length; index++) {
    result ^= value.charCodeAt(index);
    result = Math.imul(result, 0x01000193);
  }
  return result >>> 0;
}

/**
 * Accumulates SSE frames into a two-minute history that the scene, feed and scrubber read at any time.
 * Mutable for speed; `version` increments on every change so React can subscribe cheaply.
 */
export class Timeline {
  frames: FrameState[] = [];
  requests: TimedRequest[] = [];
  ticks: Tick[] = [];
  /** Per-minute totals of the retained day, oldest first. */
  summary: SummaryBucket[] = [];
  /** Earliest server times with per-second and per-minute history; null until the service says. */
  retention: { seconds: number; minutes: number } | null = null;
  replicaNumbers: Record<string, number> = {};
  /** Server time minus client time, from the latest frame. */
  clockOffset = 0;
  version = 0;
  private readonly ids = new Set<string>();

  apply(kind: FrameKind, frame: DashboardFrame, clientNow: number): void {
    const at = parseTime(frame.at);
    if (at === null) return;
    const last = this.frames.at(-1);
    // A server restart or clock jump makes the old history meaningless.
    if (last && at < last.at - 5000) this.reset();
    this.clockOffset = at - clientNow;
    const previous = this.frames.at(-1);
    const deployments = new Map(frame.deployments.map((item) => [item.deployment.id, item]));
    const throttledSince = new Map<string, number>();
    for (const [id, item] of deployments) {
      if (item.deployment.state !== "Throttled") continue;
      const wasThrottled = previous?.deployments.get(id)?.deployment.state === "Throttled";
      throttledSince.set(id, wasThrottled ? (previous?.throttledSince.get(id) ?? at) : at);
    }
    const state: FrameState = {
      at,
      replicas: frame.replicas,
      deployments,
      rates: new Map(frame.counts.map((item) => [item.deploymentId, item])),
      throttledSince,
    };
    if (previous && at <= previous.at) this.frames[this.frames.length - 1] = state;
    else this.frames.push(state);
    for (const tick of frame.routeHistory ?? []) this.addTick(parseTime(tick.at), tick.routes);
    if (kind === "delta") this.addTick(at, frame.routes ?? []);
    if (frame.replicaNumbers) this.replicaNumbers = frame.replicaNumbers;
    const secondsFrom = parseTime(frame.retention?.secondsFrom);
    const minutesFrom = parseTime(frame.retention?.minutesFrom);
    if (secondsFrom !== null && minutesFrom !== null) this.retention = { seconds: secondsFrom, minutes: minutesFrom };
    if (kind === "snapshot" && frame.summary) this.summary = [...frame.summary];
    else for (const bucket of frame.summary ?? []) {
      const time = parseTime(bucket.at);
      const last = parseTime(this.summary.at(-1)?.at);
      if (time !== null && (last === null || time > last)) this.summary.push(bucket);
    }
    const dayAgo = at - 24 * 3600_000;
    const stale = this.summary.findIndex((bucket) => (parseTime(bucket.at) ?? 0) > dayAgo);
    if (stale > 0) this.summary.splice(0, stale);

    const playhead = at - PLAYBACK_LAG_MS;
    const added: TimedRequest[] = [];
    for (const request of frame.requests) {
      if (this.ids.has(request.id)) continue;
      const started = parseTime(request.startedAt);
      if (started === null) continue;
      const completedAt = started + Math.max(0, request.durationMs);
      // Snapshot history plays at its completion time; a late delta request joins the playhead within one second.
      const playAt = kind === "snapshot" || completedAt >= playhead ? completedAt : playhead + (hash(request.id) % 1000);
      this.ids.add(request.id);
      added.push({ request, playAt, completedAt });
    }
    if (added.length > 0) {
      this.requests.push(...added);
      this.requests.sort((a, b) => a.playAt - b.playAt);
    }
    this.prune(at);
    this.version++;
  }

  reset(): void {
    this.frames = [];
    this.requests = [];
    this.ticks = [];
    this.summary = [];
    this.retention = null;
    this.ids.clear();
    this.version++;
  }

  get latestAt(): number | null {
    return this.frames.at(-1)?.at ?? null;
  }

  /** Start of the retained history: snapshot requests reach further back than the first frame. */
  get earliestAt(): number | null {
    const frame = this.frames[0]?.at;
    const request = this.requests[0]?.playAt;
    if (frame === undefined) return null;
    return request === undefined ? frame : Math.min(frame, request);
  }

  /** The frame in effect at server time `t`: the latest one at or before it, else the first. */
  stateAt(t: number): FrameState | null {
    const frames = this.frames;
    if (frames.length === 0) return null;
    let low = 0;
    let high = frames.length - 1;
    while (low < high) {
      const middle = (low + high + 1) >> 1;
      if ((frames[middle]?.at ?? Infinity) <= t) low = middle;
      else high = middle - 1;
    }
    return frames[low] ?? null;
  }

  /** Requests whose playback starts in [from, to). */
  playingBetween(from: number, to: number): TimedRequest[] {
    const start = lowerBound(this.requests, from);
    const result: TimedRequest[] = [];
    for (let index = start; index < this.requests.length; index++) {
      const item = this.requests[index];
      if (!item || item.playAt >= to) break;
      result.push(item);
    }
    return result;
  }

  /** Ticks that end in (from, to]: exact counts for requests that completed in that window. */
  ticksBetween(from: number, to: number): Tick[] {
    return this.ticks.filter((tick) => tick.at > from && tick.at <= to);
  }

  /** Every deployment seen in the retained window, so nodes keep identity while scrubbing. */
  knownDeployments(): MergedDeployment[] {
    const result = new Map<string, MergedDeployment>();
    for (const frame of this.frames) for (const [id, item] of frame.deployments) result.set(id, item);
    return [...result.values()];
  }

  /** Ticks stay sorted and unique by time; a tick seen twice (snapshot after a reconnect) is ignored. */
  private addTick(at: number | null, routes: DashboardRoute[]): void {
    if (at === null) return;
    const last = this.ticks.at(-1);
    if (last && at <= last.at) {
      if (this.ticks.some((tick) => tick.at === at)) return;
      this.ticks.push({ at, routes });
      this.ticks.sort((a, b) => a.at - b.at);
    } else this.ticks.push({ at, routes });
  }

  private prune(latest: number): void {
    const cutoff = latest - WINDOW_MS - PLAYBACK_LAG_MS - 5000;
    let frames = 0;
    // Keep one frame older than the cutoff so the state at the window's start stays defined.
    while (frames + 1 < this.frames.length && (this.frames[frames + 1]?.at ?? Infinity) <= cutoff) frames++;
    if (frames > 0) this.frames.splice(0, frames);
    const ticks = this.ticks.findIndex((tick) => tick.at > cutoff);
    if (ticks !== 0) this.ticks.splice(0, ticks < 0 ? this.ticks.length : ticks);
    const requests = lowerBound(this.requests, cutoff);
    if (requests > 0) for (const item of this.requests.splice(0, requests)) this.ids.delete(item.request.id);
  }
}

function lowerBound(items: TimedRequest[], value: number): number {
  let low = 0;
  let high = items.length;
  while (low < high) {
    const middle = (low + high) >> 1;
    if ((items[middle]?.playAt ?? Infinity) < value) low = middle + 1;
    else high = middle;
  }
  return low;
}
