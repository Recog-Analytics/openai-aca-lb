import { AnimatePresence, motion } from "motion/react";
import { useState } from "react";
import { percent, type Issue } from "../model/attention";
import { splitPaths, type FallbackPath, type Funnel } from "../model/funnel";
import { spanLabel } from "../model/history";
import { modelShort, regionName } from "../model/state";
import type { DashboardDeployment, DashboardHop } from "../model/types";

const enter = { type: "spring", duration: 0.35, bounce: 0 } as const;

/** "Sweden", "Germany", "Poland", "East US 2 PTU": short enough for a chain of three. */
export function placeShort(deployment: DashboardDeployment | undefined): string {
  if (!deployment) return "Unknown";
  const name = regionName(deployment.region).replace(/\s+(West |North |South |East )?Central$/, "");
  return deployment.tier === 0 ? `${name} PTU` : deployment.tier === 2 ? `${name} Global` : name;
}

const outcomeWords: Record<string, string> = {
  Success: "served", Throttled: "429", Failure: "error", AccountFailure: "unreachable", Misconfigured: "misconfigured", Ignored: "client error",
};

export function hopTone(outcome: string): string {
  return outcome === "Success" ? "ok" : outcome === "Throttled" ? "throttled" : outcome === "Ignored" ? "rejected" : "failed";
}

export function Chain({ hops, status, byId }: { hops: DashboardHop[]; status: number; byId: Map<string, DashboardDeployment> }) {
  if (hops.length === 0) return <span className="chain"><span className="hop" data-tone="failed">Refused, {status}</span></span>;
  return (
    <span className="chain">
      {hops.map((hop, index) => (
        <span key={index} className="hop" data-tone={hopTone(hop.outcome)}>
          {index > 0 && <span className="arrow" aria-label="then">→</span>}
          {placeShort(byId.get(hop.deploymentId))} <span className="hop-outcome">{outcomeWords[hop.outcome] ?? hop.outcome}</span>
        </span>
      ))}
      {status >= 400 && hops.at(-1)?.outcome !== "Ignored" && <span className="hop" data-tone="failed"><span className="arrow" aria-hidden="true">→</span>failed, {status}</span>}
    </span>
  );
}

interface Props {
  issues: Issue[];
  funnel: Funnel;
  byId: Map<string, DashboardDeployment>;
  labels: Map<string, string>;
  windowSeconds: number;
  selectedPath: string | null;
  onFocus: (deploymentIds: string[] | null) => void;
  onPath: (key: string | null) => void;
}

/** What needs attention and why requests fell back: the two answers that sit above the request feed. */
export function Insights({ issues, funnel, byId, labels, windowSeconds, selectedPath, onFocus, onPath }: Props) {
  const windowLabel = spanLabel(windowSeconds);
  const [showRare, setShowRare] = useState(false);
  const { notable, rare } = splitPaths(funnel);
  const rareCount = rare.reduce((sum, path) => sum + path.count, 0);
  const paths = showRare ? [...notable, ...rare] : notable;
  const share = (count: number) => percent(funnel.total > 0 ? count / funnel.total : 0);
  return (
    <>
      <section className="panel-section" aria-labelledby="attention-title">
        <div className="section-head">
          <h2 id="attention-title">Needs attention</h2>
          <span>{issues.length === 0 ? "Nothing" : issues.length === 1 ? "1 problem" : `${issues.length} problems`}</span>
        </div>
        {issues.length === 0 ? <p className="all-clear">Every deployment is healthy. A problem appears here with what it affects.</p> : (
          <ol className="issues">
            <AnimatePresence initial={false}>
              {issues.map((issue) => (
                <motion.li key={issue.key} initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }}
                  exit={{ opacity: 0, height: 0 }} transition={enter}>
                  <div className="issue" data-tone={issue.tone} data-kind={issue.kind} tabIndex={0}
                    onPointerEnter={() => onFocus(issue.deploymentIds)} onPointerLeave={() => onFocus(null)}
                    onFocus={() => onFocus(issue.deploymentIds)} onBlur={() => onFocus(null)}>
                    <strong>{issue.title}</strong>
                    <p>{issue.detail}</p>
                    {issue.movedOn > 0 && <p className="issue-impact">{share(issue.movedOn * funnel.total)} of requests moved on from it in the last {windowLabel}.</p>}
                  </div>
                </motion.li>
              ))}
            </AnimatePresence>
          </ol>
        )}
      </section>
      <section className="panel-section" aria-labelledby="fallbacks-title">
        <div className="section-head">
          <h2 id="fallbacks-title">Fallbacks and failures</h2>
          <span>last {windowLabel}, click one to list its requests</span>
        </div>
        {notable.length === 0 && rare.length === 0 ? <p className="all-clear">No request needed a retry or failed.</p> : (
          <ol className="paths">
            {paths.map((path) => (
              <li key={path.key}>
                <button type="button" className="path" aria-pressed={selectedPath === path.key}
                  onClick={() => onPath(selectedPath === path.key ? null : path.key)}
                  onPointerEnter={() => onFocus(path.hops.map((hop) => hop.deploymentId))} onPointerLeave={() => onFocus(null)}>
                  <span className="path-model">{pathModel(path, byId, labels)}</span>
                  <Chain hops={path.hops} status={path.status} byId={byId} />
                  <span className="path-count">
                    <strong>{share(path.count)}</strong>
                    <span>{path.count.toLocaleString()} req</span>
                  </span>
                </button>
              </li>
            ))}
            {rare.length > 0 && (
              <li>
                <button type="button" className="path-rest" aria-expanded={showRare} onClick={() => setShowRare(!showRare)}>
                  {showRare ? "Hide" : notable.length === 0 ? "Only stray retries and errors:" : "Also"} {rare.length === 1 ? "1 rare pattern" : `${rare.length} rare patterns`},
                  {" "}{rareCount === 1 ? "1 request" : `${rareCount} requests`} ({share(rareCount)})
                </button>
              </li>
            )}
          </ol>
        )}
      </section>
    </>
  );
}

function pathModel(path: FallbackPath, byId: Map<string, DashboardDeployment>, labels: Map<string, string>): string {
  const first = path.hops[0];
  return first ? labels.get(first.deploymentId) ?? modelShort(byId.get(first.deploymentId)?.modelKey ?? null) : "Any model";
}
