import type { TimedRequest } from "./timeline";
import type { RequestRecord } from "./types";

/**
 * Consecutive requests that tell an operator the same story: same caller, final status and chain of places and
 * statuses. The model is left out, so "refused by the LB" for two models of one caller is one row.
 */
export interface Run { key: string; items: TimedRequest[] }

function signature(request: RequestRecord): string {
  return `${request.caller}|${request.status}|${request.attempts.map((attempt) => `${attempt.region}:${attempt.tier}:${attempt.status}`).join(">")}`;
}

/**
 * Groups consecutive identical requests (newest first) into runs. A run keeps its key while it grows at the top and
 * loses old requests at the bottom, so a burst of the same failure is one row whose count climbs, not a scrolling list.
 */
export class RunGrouper {
  private keys = new Map<string, string>();

  group(items: TimedRequest[]): Run[] {
    const runs: Run[] = [];
    let previous = "";
    for (const item of items) {
      const value = signature(item.request);
      const last = runs.at(-1);
      if (last && value === previous) last.items.push(item);
      else runs.push({ key: "", items: [item] });
      previous = value;
    }
    const next = new Map<string, string>();
    const used = new Set<string>();
    for (const run of runs) {
      const known = run.items.map((item) => this.keys.get(item.request.id)).find((key) => key !== undefined && !used.has(key));
      run.key = known ?? run.items[0]?.request.id ?? "";
      used.add(run.key);
      for (const item of run.items) next.set(item.request.id, run.key);
    }
    this.keys = next;
    return runs;
  }
}

/**
 * Sampled requests, newest first. By default only the ones with news (retries, failures, refusals), so the list moves
 * at the speed of problems. It holds still while the pointer is over it.
 */
