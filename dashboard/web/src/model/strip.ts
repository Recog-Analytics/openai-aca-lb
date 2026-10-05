import { inScope, isClientError, isServed, type Scope } from "./funnel";
import type { HistoryCache, RangeKey } from "./history";
import { parseTime, type FrameState, type Tick } from "./timeline";
import type { ProblemState, SummaryBucket } from "./types";

/** One bar of the time strip: exact requests that completed in [start, end), and the worst deployment state then. */
export interface Bin {
  start: number;
  end: number;
  /** Seconds of data in the bin; 0 means nothing was retained, which reads differently from no traffic. */
  seconds: number;
  direct: number;
  retried: number;
  failed: number;
  worst: ProblemState | null;
  /** Deployments that were not healthy at some point in the bin. */
  unhealthy: number;
}

/** Bars per range: 2 s bars for the live two minutes, then about a hundred bars whatever the range. */
export const binSeconds: Record<RangeKey, number> = { "2m": 2, "15m": 10, "1h": 30, "24h": 600 };

const severity: Record<ProblemState, number> = { Degraded: 1, Throttled: 2, Open: 3, Disabled: 0 };
const worse = (a: ProblemState | null, b: ProblemState | null) => (a === null ? b : b === null ? a : severity[b] > severity[a] ? b : a);

interface Input {
  range: RangeKey;
  from: number;
  to: number;
  /** Live ticks and history buckets inside [from, to]. */
  ticks: Tick[];
  history: HistoryCache | null;
  /** The day summary: it fills a 24-hour strip before the detail arrives, and outside the filters' reach. */
  summary: SummaryBucket[];
  /** Live frames, one a second: they mark problems in the bars of the last two minutes as they happen. */
  frames?: Pick<FrameState, "at" | "deployments">[];
  scope: Scope;
}

/**
 * The strip's bars for a range, from exact counts. Filters apply to route counts; the day summary has no route detail,
 * so it only fills day-strip bars the detail does not cover, and only when no filter is set. Its problem marks apply to
 * any bar its minute ends in.
 */
export function stripBins({ range, from, to, ticks, history, summary, frames = [], scope }: Input): Bin[] {
  const size = binSeconds[range] * 1000;
  const count = Math.max(1, Math.round((to - from) / size));
  const start = to - count * size;
  const bins: Bin[] = Array.from({ length: count }, (_, index) => ({
    start: start + index * size, end: start + (index + 1) * size, seconds: 0, direct: 0, retried: 0, failed: 0, worst: null, unhealthy: 0,
  }));
  const at = (time: number) => Math.min(count - 1, Math.max(0, Math.ceil((time - start) / size) - 1));
  for (const tick of ticks) {
    if (tick.at <= start || tick.at > to) continue;
    const bin = bins[at(tick.at)];
    if (!bin) continue;
    bin.seconds += tick.seconds ?? 1;
    for (const route of tick.routes) {
      if (!inScope(route, scope)) continue;
      if (isServed(route.status)) {
        if (route.hops.length > 1) bin.retried += route.count;
        else bin.direct += route.count;
      } else if (isClientError(route.status)) bin.direct += route.count;
      else bin.failed += route.count;
    }
  }
  if (history) for (const tick of history.ticks) {
    if (tick.at <= start || tick.at > to) continue;
    const bin = bins[at(tick.at)];
    const states = history.statesAt(tick.at - 1) ?? [];
    if (!bin) continue;
    for (const state of states) bin.worst = worse(bin.worst, state.state);
    bin.unhealthy = Math.max(bin.unhealthy, states.length);
  }
  const precise = new Set<Bin>();
  for (const frame of frames) {
    if (frame.at <= start || frame.at > to) continue;
    const bin = bins[at(frame.at)];
    if (!bin) continue;
    precise.add(bin);
    let unhealthy = 0;
    for (const item of frame.deployments.values()) {
      if (item.deployment.state === "Healthy") continue;
      unhealthy++;
      bin.worst = worse(bin.worst, item.deployment.state);
    }
    bin.unhealthy = Math.max(bin.unhealthy, unhealthy);
  }
  // Minute totals fit only bars of a minute or more, the day strip's, and only bars nothing more precise covers: a
  // minute's worst state would otherwise mark a whole minute for a ten-second blip the frames already placed.
  const coarse = binSeconds[range] >= 60;
  const filtered = Boolean(scope.model || scope.zone || scope.caller);
  const detailed = new Set(bins.filter((bin) => bin.seconds > 0));
  for (const bucket of summary) {
    const time = parseTime(bucket.at);
    if (time === null || time <= start || time > to) continue;
    const bin = bins[at(time)];
    if (!bin || !coarse || detailed.has(bin) || precise.has(bin)) continue;
    bin.worst = worse(bin.worst, bucket.worst);
    bin.unhealthy = Math.max(bin.unhealthy, bucket.unhealthy);
    if (filtered) continue;
    bin.seconds += 60;
    bin.direct += bucket.served - bucket.retried + Math.max(0, bucket.total - bucket.served - bucket.failed - bucket.refused);
    bin.retried += bucket.retried;
    bin.failed += bucket.failed + bucket.refused;
  }
  return bins;
}
