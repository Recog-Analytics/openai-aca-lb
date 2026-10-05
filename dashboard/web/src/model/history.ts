import { parseTime, type FrameState, type Tick, type TimedRequest } from "./timeline";
import type { CompactState, DeploymentRate, HistoryResponse, MergedDeployment } from "./types";

/** The time ranges the strip can show, and for each the figure windows the operator can choose. */
export const ranges = {
  "2m": { label: "2 min", spanMs: 120_000, resolution: 1, windows: [10, 30, 60], window: 30 },
  "15m": { label: "15 min", spanMs: 900_000, resolution: 10, windows: [60, 300, 900], window: 900 },
  "1h": { label: "1 h", spanMs: 3_600_000, resolution: 30, windows: [300, 900, 3600], window: 3600 },
  "24h": { label: "24 h", spanMs: 86_400_000, resolution: 600, windows: [3600, 21_600, 86_400], window: 86_400 },
} as const;
export type RangeKey = keyof typeof ranges;
export const rangeKeys = Object.keys(ranges) as RangeKey[];

export function isRange(value: string | null): value is RangeKey {
  return value !== null && (rangeKeys as string[]).includes(value);
}

/** "30 s", "5 min", "1 h", "24 h". */
export function spanLabel(seconds: number): string {
  if (seconds < 60) return `${seconds}\u00a0s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}\u00a0min`;
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.round((seconds % 3600) / 60);
  return minutes === 0 || hours >= 6 ? `${Math.round(seconds / 3600)}\u00a0h` : `${hours}\u00a0h ${minutes}\u00a0min`;
}

export type HistoryFetcher = (from: number, to: number, resolution: number) => Promise<HistoryResponse>;

/** Bucket length of the tail fetched after coarse buckets, so live ticks (two minutes) always meet the history. */
export const TAIL_RESOLUTION = 10;

/**
 * One fetched stretch of history: exact route counts per bucket (usable as long ticks), the worst state of each troubled
 * deployment per bucket, and the sampled requests. Immutable once built.
 *
 * Buckets align to multiples of their length, so the last one is partial and the last whole one may end up to a bucket
 * before the fetch: ten minutes for the day range, longer than the live ticks reach back. A `tail` fetched at
 * TAIL_RESOLUTION from there to the same end fills that stretch.
 */
export class HistoryCache {
  readonly from: number;
  readonly to: number;
  readonly resolution: number;
  readonly ticks: Tick[];
  readonly requests: TimedRequest[];
  readonly deployments: MergedDeployment[];
  private readonly states: { at: number; states: CompactState[] }[];

  constructor(response: HistoryResponse, tail?: HistoryResponse) {
    this.from = parseTime(response.from) ?? 0;
    const mainTo = parseTime(response.to) ?? 0;
    this.to = tail ? Math.max(mainTo, parseTime(tail.to) ?? 0) : mainTo;
    this.resolution = response.resolution;
    const parse = (items: HistoryResponse["buckets"]) => items.map((bucket) => ({ ...bucket, time: parseTime(bucket.at) }))
      .filter((bucket): bucket is typeof bucket & { time: number } => bucket.time !== null);
    const main = parse(response.buckets);
    // Whole main buckets, then the tail's buckets after the last of them.
    const seam = main.reduce((latest, bucket) => (bucket.time <= mainTo ? Math.max(latest, bucket.time) : latest), -Infinity);
    const buckets = (tail ? [...main.filter((bucket) => bucket.time <= seam), ...parse(tail.buckets).filter((bucket) => bucket.time > seam)] : main)
      .sort((a, b) => a.time - b.time);
    this.ticks = buckets.map((bucket) => ({ at: bucket.time, routes: bucket.routes, seconds: bucket.seconds }));
    this.states = buckets.map((bucket) => ({ at: bucket.time, states: bucket.states }));
    const ids = new Set(response.requests.map((request) => request.id));
    this.requests = [...response.requests, ...(tail?.requests ?? []).filter((request) => !ids.has(request.id))].flatMap((request) => {
      const started = parseTime(request.startedAt);
      if (started === null) return [];
      const completedAt = started + Math.max(0, request.durationMs);
      return [{ request, playAt: completedAt, completedAt }];
    }).sort((a, b) => a.playAt - b.playAt);
    this.deployments = response.deployments;
  }

  /** Buckets that end in (from, to] and no later than `until`, where live ticks take over. */
  ticksBetween(from: number, to: number, until = Infinity): Tick[] {
    return this.ticks.filter((tick) => tick.at > from && tick.at <= to && tick.at <= until);
  }

  /**
   * Where live ticks take over from the buckets: the end of the last bucket that is whole (ends by the time of the fetch).
   * Buckets count up to it and live ticks after it, so no request is counted twice or dropped at the seam. Live ticks
   * reach back two minutes and the range is fetched again at least every minute, so they meet; if they ever do not, the
   * gap stays empty rather than double counted.
   */
  handoff(): number {
    let seam = -Infinity;
    for (const tick of this.ticks) if (tick.at <= this.to) seam = Math.max(seam, tick.at);
    return seam;
  }

  /** Worst state of each troubled deployment in the bucket that holds `t`, or null outside the fetched range. */
  statesAt(t: number): CompactState[] | null {
    if (t < this.from || t > this.to) return null;
    const bucket = this.states.find((item) => item.at > t) ?? this.states.at(-1);
    return bucket && t >= bucket.at - this.resolution * 1000 ? bucket.states : [];
  }

  /**
   * The scene's frame at a past time `t`: the deployments seen in the range, each in its worst state of that bucket
   * (healthy when the bucket lists nothing for it). Replica detail is not kept for the past, so every replica shows the
   * merged state.
   */
  frameAt(t: number, replicas: string[]): FrameState | null {
    const states = this.statesAt(t);
    if (states === null || this.deployments.length === 0) return null;
    const byId = new Map(states.map((state) => [state.deploymentId, state]));
    const deployments = new Map<string, MergedDeployment>();
    for (const item of this.deployments) {
      const state = byId.get(item.deployment.id);
      const deployment = {
        ...item.deployment, state: state?.state ?? "Healthy", accountOpen: state?.accountOpen ?? false, halfOpen: state?.halfOpen ?? false,
        p95TtfbMs: state?.p95TtfbMs ?? item.deployment.p95TtfbMs, throttledUntil: null, openUntil: null,
      } as const;
      deployments.set(item.deployment.id, {
        deployment, replicas: item.replicas.map((replica) => ({ ...replica, state: deployment.state, accountOpen: deployment.accountOpen })),
      });
    }
    return { at: t, replicas, deployments, rates: new Map<string, DeploymentRate>(), throttledSince: new Map() };
  }

  /** Sampled requests completed in (from, to], newest first. */
  requestsBetween(from: number, to: number): TimedRequest[] {
    const result: TimedRequest[] = [];
    for (let index = this.requests.length - 1; index >= 0; index--) {
      const item = this.requests[index];
      if (!item || item.completedAt <= from) break;
      if (item.completedAt <= to) result.push(item);
    }
    return result;
  }
}
