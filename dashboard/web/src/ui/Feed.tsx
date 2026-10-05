import { AnimatePresence, motion } from "motion/react";
import { useState, type ReactNode } from "react";
import { bytesLabel, operationName } from "../model/names";
import {
  attemptTone, durationLabel, modelShort, regionName, requestTone, tierName,
} from "../model/state";
import type { TimedRequest } from "../model/timeline";
import type { DashboardDeployment, RequestRecord } from "../model/types";
import { placeShort } from "./Insights";
import { RunGrouper, type Run } from "../model/runs";

export type FeedMode = "notable" | "all";

interface Props {
  items: TimedRequest[];
  t: number;
  mode: FeedMode;
  onMode: (mode: FeedMode) => void;
  /** Set when the feed shows one fallback path only. */
  pathLabel: ReactNode;
  onClearPath: () => void;
  byId: Map<string, DashboardDeployment>;
  /** Readable pool label per deployment id. */
  labels: Map<string, string>;
  selected: RequestRecord | null;
  onSelect: (id: string | null) => void;
}

const enter = { type: "spring", duration: 0.35, bounce: 0 } as const;
const MAX_ROWS = 40;

export function Feed({ items, t, mode, onMode, pathLabel, onClearPath, byId, labels, selected, onSelect }: Props) {
  const [held, setHeld] = useState<TimedRequest[] | null>(null);
  const [grouper] = useState(() => new RunGrouper());
  const shown = grouper.group(held ?? items).slice(0, MAX_ROWS);
  const newest = held?.[0]?.playAt ?? Infinity;
  const waiting = held ? items.filter((item) => item.playAt > newest).length : 0;
  return (
    <section className="panel-section feed" aria-labelledby="feed-title">
      <div className="section-head">
        <h2 id="feed-title">Requests</h2>
        <div className="segmented small" role="radiogroup" aria-label="Requests to show">
          <button type="button" role="radio" aria-checked={mode === "notable"} onClick={() => onMode("notable")}>Retries and failures</button>
          <button type="button" role="radio" aria-checked={mode === "all"} onClick={() => onMode("all")}>All</button>
        </div>
      </div>
      {pathLabel && (
        <p className="feed-filter">
          Only {pathLabel}
          <button type="button" onClick={onClearPath}>Show all</button>
        </p>
      )}
      <AnimatePresence initial={false}>
        {selected && <RequestDetail key={selected.id} request={selected} byId={byId} labels={labels} onClose={() => onSelect(null)} />}
      </AnimatePresence>
      <div className="feed-hold" aria-live="polite">
        {held ? <span>{waiting === 0 ? "Paused while you read" : `Paused while you read, ${waiting} new`}</span>
          : <span>Point at the list to pause it. Click a request for its attempts.</span>}
      </div>
      <ol className="feed-list" onPointerEnter={() => setHeld(items)} onPointerLeave={() => setHeld(null)}>
        {shown.length === 0 && <li className="feed-empty">{mode === "notable" ? "No retries or failures in the last two minutes." : "No requests yet."}</li>}
        {/* New rows open from zero height, which pushes older rows down without overlap; old rows leave at once. */}
        <AnimatePresence initial={false}>
          {shown.map((run) => (
            <motion.li key={run.key} className="feed-item" initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }}
              transition={enter}>
              <FeedRow run={run} t={t} byId={byId} labels={labels} active={run.items.some((item) => item.request.id === selected?.id)} onSelect={onSelect} />
            </motion.li>
          ))}
        </AnimatePresence>
      </ol>
    </section>
  );
}

/** What the request asked for, in words: its model (pool label when known) and operation. */
function requestName(request: RequestRecord, byId: Map<string, DashboardDeployment>, labels: Map<string, string>): string {
  const id = request.attempts[0]?.deploymentId;
  const model = (id && byId.has(id) ? labels.get(id) : null) ?? modelShort(request.modelKey ?? request.requestedModel);
  return model;
}

/** The backend's error on the last attempt that has one: "429 · Requests to the ChatCompletions_Create Operation…". */
function lastError(request: RequestRecord): { code: string; message: string | null } | null {
  const attempt = [...request.attempts].reverse().find((item) => item.errorCode || item.errorMessage);
  return attempt ? { code: attempt.errorCode ?? String(attempt.status ?? "error"), message: attempt.errorMessage ?? null } : null;
}

function FeedRow({ run, t, byId, labels, active, onSelect }: {
  run: Run; t: number; byId: Map<string, DashboardDeployment>; labels: Map<string, string>; active: boolean; onSelect: (id: string | null) => void;
}) {
  const [item] = run.items;
  if (!item) return null;
  const request = item.request;
  const tone = requestTone(request);
  const count = run.items.length;
  const ago = (entry: TimedRequest | undefined) => Math.max(0, Math.round((t - (entry?.completedAt ?? t)) / 1000));
  const newest = ago(item);
  const oldest = ago(run.items.at(-1));
  const error = tone === "ok" ? null : lastError(request);
  return (
    <button type="button" className="feed-row" data-tone={tone} aria-pressed={active} onClick={() => onSelect(active ? null : request.id)}
      aria-label={count > 1 ? `${count} identical requests, newest ${newest} seconds ago` : undefined}>
      <span className="row-status">{request.status}{count > 1 && <span className="row-count">×{count}</span>}</span>
      <span className="row-main">
        <span className="row-who">{request.caller ?? "unknown"} <span className="row-model">{[...new Set(run.items.map((entry) => requestName(entry.request, byId, labels)))].join(", ")}{operationName(request.operation) ? `, ${operationName(request.operation)?.toLowerCase()}` : ""}</span></span>
        <span className="chain">
          {request.attempts.length === 0 ? <span className="hop" data-tone={tone}>refused by LB</span>
            : request.attempts.map((attempt, index) => (
              <span key={index} className="hop" data-tone={attemptTone(attempt)}>
                {index > 0 && <span className="arrow" aria-hidden="true">→</span>}
                {placeShort(byId.get(attempt.deploymentId ?? "")) === "Unknown" ? regionName(attempt.region) : placeShort(byId.get(attempt.deploymentId ?? ""))} {attempt.status ?? "no response"}
              </span>
            ))}
        </span>
        {error && <span className="row-error"><strong>{error.code}</strong>{error.message ? ` ${error.message}` : ""}</span>}
      </span>
      <span className="row-meta">
        <span>{durationLabel(request.durationMs)}</span>
        <span className="row-ago">{newest < 2 ? "now" : `${newest} s ago`}{count > 1 && `, over ${Math.max(1, oldest - newest)} s`}</span>
      </span>
    </button>
  );
}

const reasons: Record<string, string> = {
  throttled: "Throttled, retried elsewhere",
  backend_error: "Server error, retried",
  misconfigured: "Deployment misconfigured, retried",
  ttfb_timeout: "No first byte in time, retried",
  account_failure: "Account unreachable, retried",
  transport_error: "Connection failed, retried",
};

function RequestDetail({ request, byId, labels, onClose }: {
  request: RequestRecord; byId: Map<string, DashboardDeployment>; labels: Map<string, string>; onClose: () => void;
}) {
  const started = new Date(request.startedAt);
  const operation = operationName(request.operation);
  const facts: [string, string][] = [
    ["Caller", request.caller ?? "Unknown"],
    ["Operation", operation ?? "Not recorded"],
    ["API version", request.apiVersion ?? (request.operation ? "None" : "Not recorded")],
    ["Request body", bytesLabel(request.requestBytes) ?? "Not recorded"],
    ["Output limit", request.maxOutputTokens != null ? `${request.maxOutputTokens.toLocaleString()} tokens` : request.operation ? "Not set" : "Not recorded"],
    ["Streaming", request.streaming ? "Yes" : "No"],
    ["Zone", request.zone ?? "Default"],
  ];
  return (
    <motion.section className="detail" aria-label="Selected request"
      initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }}
      transition={{ type: "spring", duration: 0.35, bounce: 0 }}>
      <div className="detail-inner" data-tone={requestTone(request)}>
        <div className="detail-head">
          <div>
            <h3>{requestName(request, byId, labels)}{operation ? `, ${operation.toLowerCase()}` : ""}</h3>
            <p>
              <span className="detail-status">{request.status}</span> after {durationLabel(request.durationMs)}, started {started.toLocaleTimeString()}.
            </p>
          </div>
          <button type="button" className="close" onClick={onClose} aria-label="Close request detail">
            <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" /></svg>
          </button>
        </div>
        <dl className="facts request-facts">
          {facts.map(([label, value]) => <Fact key={label} label={label} value={value} />)}
        </dl>
        <ol className="attempts">
          {request.attempts.length === 0 && <li className="attempt-none">The LB answered without trying a deployment: no candidate was available.</li>}
          {request.attempts.map((attempt, index) => {
            const deployment = attempt.deploymentId ? byId.get(attempt.deploymentId) : undefined;
            const place = deployment?.zone === "global" ? `Global (${regionName(attempt.region)})` : regionName(attempt.region);
            return (
              <li key={index} className="attempt" data-tone={attemptTone(attempt)}>
                <span className="attempt-index">{index + 1}</span>
                <span className="attempt-body">
                  <span><strong>{place}</strong>{deployment ? ` ${labels.get(deployment.id) ?? modelShort(deployment.modelKey)}` : ""}</span>
                  <span className="attempt-sub">{attempt.deployment} in {attempt.account}, {tierName(attempt.tier)}</span>
                  {attempt.retryReason && <span className="attempt-reason">{reasons[attempt.retryReason] ?? attempt.retryReason}</span>}
                  {(attempt.errorCode || attempt.errorMessage) && (
                    <span className="attempt-error"><strong>{attempt.errorCode ?? "Error"}</strong>{attempt.errorMessage && <span>{attempt.errorMessage}</span>}</span>
                  )}
                  {attempt.backendRequestId && <span className="attempt-sub">Azure request ID <span className="id">{attempt.backendRequestId}</span></span>}
                </span>
                <span className="attempt-result">
                  <strong>{attempt.status ?? "—"}</strong>
                  <span>{attempt.ttfbMs === null ? "no headers" : `TTFB ${durationLabel(attempt.ttfbMs)}`}</span>
                </span>
              </li>
            );
          })}
        </ol>
        <p className="detail-id">LB request <span className="id">{request.id}</span>. The LB never records prompts, completions or keys.</p>
      </div>
    </motion.section>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return <><dt>{label}</dt><dd>{value}</dd></>;
}
