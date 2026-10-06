import { attemptTone, requestTone, type AttemptTone } from "./state";
import { hash, type TimedRequest } from "./timeline";
import type { RequestRecord } from "./types";

export type Anchor = { kind: "caller"; id: string } | { kind: "lb" } | { kind: "node"; id: string } | { kind: "refused" };
export type SegmentKind = "travel" | "dwell" | "bounce" | "absorb" | "stream" | "burst";
export type ParticleTone = AttemptTone | "pending";

export interface Segment {
  kind: SegmentKind;
  from: Anchor;
  to: Anchor;
  start: number;
  duration: number;
  tone: ParticleTone;
}

export interface Plan { segments: Segment[]; duration: number }

/** Compressed timings: the scene shows the shape of a request, not its wall-clock duration. */
export const timing = { toLb: 380, toNode: 900, toRefused: 420, bounce: 650, absorb: 280, burst: 420, dwellMin: 90, dwellMax: 620, streamMin: 700, streamMax: 5200 };

/** Time at the deployment before headers: logarithmic in TTFB so 200 ms and 5 s both read, neither dominates. */
export function dwellMs(ttfbMs: number | null): number {
  if (ttfbMs === null || !Number.isFinite(ttfbMs) || ttfbMs <= 0) return timing.dwellMin;
  return Math.min(timing.dwellMax, timing.dwellMin + 170 * Math.log10(1 + ttfbMs / 100));
}

/** How long a stream's trail stays: grows with the body time after the first byte, compressed and capped. */
export function streamMs(request: RequestRecord, ttfbMs: number | null): number {
  const body = Math.max(0, request.durationMs - (ttfbMs ?? 0));
  return Math.min(timing.streamMax, Math.max(timing.streamMin, timing.streamMin + Math.sqrt(body) * 22));
}

export function planRequest(request: RequestRecord): Plan {
  const segments: Segment[] = [];
  let t = 0;
  const add = (kind: SegmentKind, from: Anchor, to: Anchor, duration: number, tone: ParticleTone) => {
    segments.push({ kind, from, to, start: t, duration, tone });
    t += duration;
  };
  const caller: Anchor = { kind: "caller", id: request.caller ?? "unknown" };
  const lb: Anchor = { kind: "lb" };
  add("travel", caller, lb, timing.toLb, "pending");
  if (request.attempts.length === 0) {
    // Refused at the LB: it follows the refused ribbon and ends there.
    const tone = requestTone(request) === "throttled" ? "throttled" : "failed";
    add("travel", lb, { kind: "refused" }, timing.toRefused, tone);
    add("burst", { kind: "refused" }, { kind: "refused" }, timing.burst, tone);
    return { segments, duration: t };
  }
  request.attempts.forEach((attempt, index) => {
    const node: Anchor = { kind: "node", id: attempt.deploymentId ?? attempt.deployment };
    const tone = attemptTone(attempt);
    const last = index === request.attempts.length - 1;
    add("travel", lb, node, timing.toNode, "pending");
    // The dwell carries the attempt's outcome so the particle can change colour as headers arrive.
    add("dwell", node, node, dwellMs(attempt.ttfbMs), tone);
    if (!last) add("bounce", node, lb, timing.bounce, tone === "ok" ? "failed" : tone);
    else if (tone === "ok" && request.streaming && request.status < 400) add("stream", node, node, streamMs(request, attempt.ttfbMs), "ok");
    else if (tone === "ok" && request.status < 400) add("absorb", node, node, timing.absorb, "ok");
    else add("burst", node, node, timing.burst, tone === "ok" ? "failed" : tone);
  });
  return { segments, duration: t };
}

/** Segment active at `elapsed` ms into a plan, with its local progress. */
export function segmentAt(plan: Plan, elapsed: number): { segment: Segment; progress: number } | null {
  if (elapsed < 0 || elapsed >= plan.duration) return null;
  for (const segment of plan.segments)
    if (elapsed < segment.start + segment.duration)
      return { segment, progress: Math.min(1, Math.max(0, (elapsed - segment.start) / segment.duration)) };
  return null;
}

/**
 * Caps particles per second of scene time. Failures and retries are kept first because they carry the news;
 * the rest are chosen by id hash, so live play and replay pick the same requests.
 */
export function selectForPlayback(items: TimedRequest[], perSecond: number): TimedRequest[] {
  const buckets = new Map<number, TimedRequest[]>();
  for (const item of items) {
    const key = Math.floor(item.playAt / 1000);
    buckets.set(key, [...(buckets.get(key) ?? []), item]);
  }
  const result: TimedRequest[] = [];
  for (const bucket of buckets.values()) {
    if (bucket.length <= perSecond) {
      result.push(...bucket);
      continue;
    }
    const ranked = [...bucket].sort((a, b) => interest(b.request) - interest(a.request) || hash(a.request.id) - hash(b.request.id));
    result.push(...ranked.slice(0, perSecond));
  }
  return result.sort((a, b) => a.playAt - b.playAt);
}

function interest(request: RequestRecord): number {
  const tone = requestTone(request);
  return tone === "ok" ? 0 : 1;
}
