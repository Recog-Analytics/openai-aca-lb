import { DemoStream, type Scenario } from "../model/demo";
import type { HistoryFetcher } from "../model/history";
import type { Timeline } from "../model/timeline";
import type { DashboardFrame, FrameKind, HistoryResponse } from "../model/types";

export type Connection =
  | { kind: "connecting" }
  | { kind: "live" }
  | { kind: "reconnecting"; failures: number }
  | { kind: "demo"; scenario: Scenario };

function isFrame(value: unknown): value is DashboardFrame {
  if (typeof value !== "object" || value === null) return false;
  const frame = value as Record<string, unknown>;
  return typeof frame.at === "string" && Array.isArray(frame.deployments) && Array.isArray(frame.requests) &&
    Array.isArray(frame.counts) && Array.isArray(frame.replicas);
}

function isHistory(value: unknown): value is HistoryResponse {
  if (typeof value !== "object" || value === null) return false;
  const history = value as Record<string, unknown>;
  return typeof history.from === "string" && typeof history.resolution === "number" && Array.isArray(history.buckets) &&
    Array.isArray(history.requests) && Array.isArray(history.deployments);
}

/** /api/history behind the same sign-in as the stream. */
async function fetchHistory(from: number, to: number, resolution: number): Promise<HistoryResponse> {
  const query = new URLSearchParams({ from: new Date(from).toISOString(), to: new Date(to).toISOString(), resolution: String(resolution) });
  const response = await fetch(`/api/history?${query}`, { credentials: "same-origin" });
  if (!response.ok) throw new Error(`History unavailable (${response.status})`);
  const data: unknown = await response.json();
  if (!isHistory(data)) throw new Error("History response has an unexpected shape");
  return data;
}

export interface Source {
  stop: () => void;
  history: HistoryFetcher;
}

/** Feeds the timeline from /api/stream, or from the in-browser demo stream, and fetches history from the same place. */
export function startSource(timeline: Timeline, scenario: Scenario | null, onConnection: (connection: Connection) => void): Source {
  if (scenario) {
    const demo = new DemoStream(scenario, Date.now());
    timeline.apply("snapshot", demo.snapshot(), Date.now());
    onConnection({ kind: "demo", scenario });
    const timer = window.setInterval(() => timeline.apply("delta", demo.tick(), Date.now()), 1000);
    return {
      // A stopped source leaves nothing behind, so a restarted one (React remounts in development) starts clean.
      stop: () => {
        window.clearInterval(timer);
        timeline.reset();
      },
      history: (from, to, resolution) => Promise.resolve(demo.history(from, to, resolution)),
    };
  }
  onConnection({ kind: "connecting" });
  let stream: EventSource | null = null;
  let retry = 0;
  let failures = 0;
  const receive = (kind: FrameKind) => (event: MessageEvent<string>) => {
    let data: unknown;
    try {
      data = JSON.parse(event.data);
    } catch {
      return;
    }
    if (!isFrame(data)) return;
    timeline.apply(kind, data, Date.now());
    if (failures !== 0 || kind === "snapshot") onConnection({ kind: "live" });
    failures = 0;
  };
  const connect = () => {
    stream = new EventSource("/api/stream");
    stream.addEventListener("snapshot", receive("snapshot"));
    stream.addEventListener("delta", receive("delta"));
    stream.onerror = () => {
      onConnection({ kind: "reconnecting", failures: ++failures });
      // EventSource retries dropped connections itself, but gives up on an HTTP error such as an expired login.
      if (stream?.readyState === EventSource.CLOSED) retry = window.setTimeout(connect, Math.min(30_000, 1000 * 2 ** failures));
    };
  };
  connect();
  return {
    stop: () => {
      window.clearTimeout(retry);
      stream?.close();
      timeline.reset();
    },
    history: fetchHistory,
  };
}
