import { motion } from "motion/react";
import { percent, rate as formatRate } from "../model/attention";
import type { Flow } from "../model/funnel";
import { replicaLabel } from "../model/names";
import { durationLabel, outcomeMix, placeName, statusText, tierName, weightLabel, type NodeVisual } from "../model/state";
import type { DeploymentRate, MergedDeployment } from "../model/types";

interface Props {
  item: MergedDeployment;
  /** The deployment's pool label: "gpt-4o-mini · public". */
  label: string;
  replicaOrder: string[];
  replicaNumbers: Record<string, number>;
  rate: DeploymentRate | undefined;
  /** Exact request counts at this deployment over the window, out of `total`. */
  flow: Flow | undefined;
  total: number;
  seconds: number;
  visual: NodeVisual;
  x: number;
  y: number;
  r: number;
  sceneWidth: number;
  sceneHeight: number;
}

const outcomeNames: Record<string, string> = {
  Success: "Succeeded", Throttled: "Throttled (429)", Failure: "Failed", AccountFailure: "Account unreachable",
  Misconfigured: "Misconfigured", Ignored: "Client error",
};

/** Hover card anchored beside its node; it grows out of the node's side. */
export function NodeCard({ item, label, replicaOrder, replicaNumbers, rate, flow, total, seconds, visual, x, y, r, sceneWidth, sceneHeight }: Props) {
  const d = item.deployment;
  const left = x > sceneWidth * 0.6;
  // The card's anchor slides along its height with the node's position, so it never leaves the scene.
  const along = Math.min(0.92, Math.max(0.08, y / Math.max(1, sceneHeight)));
  const mix = outcomeMix(rate);
  // Shares with their exact counts, so a small percentage can be checked against real requests.
  const share = (count: number) => `${percent(total > 0 ? count / total : 0)} (${count.toLocaleString()} req)`;
  return (
    <motion.div
      className="node-card" role="tooltip"
      style={{ left: left ? x - r - 14 : x + r + 14, top: y, transformOrigin: `${left ? "right" : "left"} ${along * 100}%`,
        translateX: left ? "-100%" : "0%", translateY: `${-along * 100}%` }}
      initial={{ opacity: 0, scale: 0.96 }} animate={{ opacity: 1, scale: 1 }} exit={{ opacity: 0, scale: 0.98, transition: { duration: 0.1 } }}
      transition={{ type: "spring", duration: 0.22, bounce: 0 }}>
      {/* Human names first; the raw deployment and account names stay as secondary text for support. */}
      <div className="card-head">
        <strong>{label} in {placeName(d)}</strong>
        <span className="raw">{d.deployment} in {d.account}</span>
      </div>
      <p className="card-state" data-look={visual.look}>{statusText(visual, item) ?? "Healthy"}</p>
      <dl className="facts">
        <dt>Model</dt><dd>{d.modelKey.replace("@", ", version ")}</dd>
        <dt>Tier</dt><dd>{tierName(d.tier)} ({d.tier})</dd>
        <dt>Zone</dt><dd>{d.zone}</dd>
        <dt>Weight</dt><dd>{weightLabel(d.weight, d.tier)}</dd>
        <dt>p95 TTFB</dt><dd>{durationLabel(d.p95TtfbMs)}</dd>
        <dt>Attempts</dt><dd>{rate ? formatRate(rate.requestsPerSecond) : "none"}</dd>
      </dl>
      {flow && total > 0 && (
        <dl className="facts traffic">
          <dt>Reached</dt><dd>{share(flow.reached)}</dd>
          <dt>Served</dt><dd>{share(flow.served)}</dd>
          {flow.spilled > 0 && <><dt>Spilled in</dt><dd>{share(flow.spilled)}</dd></>}
          {flow.throttled > 0 && <><dt>Left on 429</dt><dd data-tone="throttled">{share(flow.throttled)}</dd></>}
          {flow.failed > 0 && <><dt>Left on failure</dt><dd data-tone="failed">{share(flow.failed)}</dd></>}
          {flow.answered > 0 && <><dt>Client errors</dt><dd>{share(flow.answered)}</dd></>}
        </dl>
      )}
      {mix.length > 0 && (
        <div className="mix">
          <div className="mix-bar" aria-hidden="true">
            {mix.map((part) => <span key={part.outcome} data-outcome={part.outcome} style={{ flexGrow: part.share }} />)}
          </div>
          <ul>
            {mix.map((part) => (
              <li key={part.outcome} data-outcome={part.outcome}>{outcomeNames[part.outcome] ?? part.outcome} {percent(part.share)}</li>
            ))}
          </ul>
        </div>
      )}
      <div className="replicas">
        <span>{item.replicas.length === 1 ? "Seen by 1 LB replica" : `Seen by ${item.replicas.length} LB replicas`}</span>
        {visual.replicaStates.length > 1 && (
          <span>{visual.replicaStates.map((entry) => `${entry.count} ${entry.state.toLowerCase()}`).join(", ")}</span>
        )}
        <ul>
          {item.replicas.map((replica) => (
            <li key={replica.replica} data-state={replica.state} title={replica.replica}>
              <span className="replica-name">{replicaLabel(replica.replica, replicaOrder, replicaNumbers)}</span>
              <span>{replica.state}{replica.accountOpen ? ", account open" : ""}</span>
            </li>
          ))}
        </ul>
      </div>
      {flow && total > 0 && <p className="card-window">Shares of all {total.toLocaleString()} requests in the last {seconds} s</p>}
    </motion.div>
  );
}
