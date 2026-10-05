import { AnimatePresence, motion } from "motion/react";
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { percent } from "../model/attention";
import type { PlaybackClock } from "../model/clock";
import { placeKey, quotaShare, type Flow, type Funnel } from "../model/funnel";
import type { SceneLayout } from "../model/layout";
import { modelShort, nodeVisual, placeName, statusText, tierName, weightLabel, type NodeLook } from "../model/state";
import type { FrameState, Timeline } from "../model/timeline";
import type { DashboardDeployment, MergedDeployment, RequestRecord } from "../model/types";
import type { Palette } from "../theme";
import { NodeCard } from "../ui/NodeCard";
import { SceneRenderer } from "./renderer";

interface Props {
  timeline: Timeline;
  clock: PlaybackClock;
  layout: SceneLayout;
  deployments: MergedDeployment[];
  frame: FrameState | null;
  t: number;
  funnel: Funnel;
  /** The same window one minute earlier, for change markers. Null when that history is not retained. */
  previous: Funnel | null;
  palette: Palette;
  reducedMotion: boolean;
  requestFilter: (request: RequestRecord) => boolean;
  nodeFilter: (deployment: DashboardDeployment) => boolean;
  /** Deployments to emphasise from outside the scene, such as a hovered attention item. */
  focus: string[] | null;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onResize: (size: { width: number; height: number }) => void;
}

/** Change against a minute ago, in percentage points, shown only when it is big enough to matter. */
const DELTA_POINTS = 5;

export function Scene(props: Props) {
  const { timeline, clock, layout, deployments, frame, t, funnel, previous, palette, reducedMotion, requestFilter, nodeFilter,
    focus, selectedId, onSelect, onResize } = props;
  const host = useRef<HTMLDivElement>(null);
  const [renderer, setRenderer] = useState<SceneRenderer | null>(null);
  const [hovered, setHovered] = useState<string | null>(null);
  const byId = new Map(deployments.map((item) => [item.deployment.id, item.deployment]));
  const emphasis = emphasise(hovered, focus, layout, funnel);
  const inputs = { layout, funnel, palette, reducedMotion, requestFilter, nodeFilter, selectedId, emphasis, onPick: onSelect };
  const latest = useRef(inputs);
  useEffect(() => {
    latest.current = inputs;
    renderer?.update(inputs);
  });

  useEffect(() => {
    const element = host.current;
    if (!element) return;
    let disposed = false;
    let created: SceneRenderer | null = null;
    void SceneRenderer.create(element, timeline, clock, latest.current).then((instance) => {
      if (disposed) instance.destroy();
      else {
        created = instance;
        setRenderer(instance);
      }
    });
    return () => {
      disposed = true;
      created?.destroy();
    };
  }, [timeline, clock]);

  useLayoutEffect(() => {
    const element = host.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      if (entry) onResize({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [onResize]);

  const bind = (key: string) => (element: HTMLElement | null) => renderer?.bindLabel(key, element);
  const total = funnel.total;
  const share = (count: number) => (total > 0 ? count / total : 0);
  const delta = (pick: (funnel: Funnel) => number) => {
    if (!previous || previous.total === 0 || total === 0) return null;
    const points = 100 * (pick(funnel) / total - pick(previous) / previous.total);
    return Math.abs(points) >= DELTA_POINTS ? points : null;
  };
  const hover = (key: string) => ({ onPointerEnter: () => setHovered(key), onPointerLeave: () => setHovered(null),
    "data-muted": emphasis && !emphasis.has(key) ? true : undefined });
  const hoveredId = hovered?.startsWith("dep:") ? hovered.slice(4) : null;
  const hoveredItem = hoveredId ? frame?.deployments.get(hoveredId) : undefined;
  const hoveredPosition = hoveredId ? (renderer?.nodePosition(hoveredId) ?? null) : null;
  const compact = layout.laneHeight < layout.rem * 3;
  const rate = funnel.seconds > 0 ? total / funnel.seconds : 0;
  const tiers = [...new Set(deployments.map((item) => item.deployment.tier))].sort((a, b) => a - b);
  const placeNames = new Map(deployments.map((item) => [placeKey(item.deployment), placeName(item.deployment)]));

  return (
    <div className="scene" ref={host} data-compact={compact || undefined} onPointerLeave={() => setHovered(null)}>
      <div className="scene-over">
        {(["callers", "places", "deployments"] as const).map((column) => (
          <div key={column} className="column-head" data-column={column} ref={bind(`column:${column}`)}>
            {column === "callers" ? "Callers" : column === "places" ? "Region" : "Deployment"}
          </div>
        ))}

        {/* The LB's column: its rate, then the split by tier, which answers "is the quota split what we want?". */}
        <div className="lb-head" data-column="lb" ref={bind("column:lb")}>
          <span>Load balancer <strong>{rate.toFixed(rate < 10 ? 1 : 0)}</strong> req/s</span>
          <span className="lb-caption" id="lb-tiers-caption">Served by tier</span>
          <div className="lb-tiers" role="list" aria-labelledby="lb-tiers-caption">
            {tiers.map((tier) => {
              const flow = funnel.tiers.get(tier);
              const served = share(flow?.served ?? 0);
              const spilled = share(flow?.spilled ?? 0);
              return (
                <div key={tier} className="lb-tier" role="listitem" data-zero={served === 0 || undefined} {...hover(`tier:${tier}`)}>
                  <span>{tierName(tier)}</span>
                  <strong>{percent(served)}</strong>
                  <span data-tone="spill">{spilled >= 0.005 ? `${percent(spilled)} spilled in` : ""}</span>
                </div>
              );
            })}
          </div>
        </div>

        {layout.callers.map((caller) => {
          const flow = funnel.callers.get(caller.id);
          return (
            <div key={caller.id} className="flow-label" data-align="end" ref={bind(`caller:${caller.id}`)}>
              <span className="flow-name">{caller.id}</span>
              <Figures share={share(flow?.reached ?? 0)} delta={delta((f) => f.callers.get(caller.id)?.reached ?? 0)}
                note={flow && flow.failed > 0 && (flow.failed / flow.reached >= 0.01) ? <span data-tone="failed">{percent(share(flow.failed))} failed</span> : null} />
            </div>
          );
        })}

        {layout.places.map((place) => {
          const flow = funnel.places.get(place.key);
          return (
            <div key={place.key} className="place" data-compact={place.compact || undefined} ref={bind(`place:${place.key}`)}
              {...hover(`place:${place.key}`)} title={place.key === "global" ? "Global deployments may process requests in any Azure region" : undefined}>
              <span className="flow-name">{placeNames.get(place.key) ?? place.key}</span>
              <Figures share={share(flow?.served ?? 0)} delta={delta((f) => f.places.get(place.key)?.served ?? 0)}
                note={place.compact ? null : <FlowNote flow={flow} share={share} />} />
            </div>
          );
        })}

        <div className="place" data-kind="refused" data-hidden={funnel.refused === 0 || undefined} ref={bind("refused")} aria-hidden={funnel.refused === 0}
          {...hover("refused")}>
          <span className="flow-name">Refused by the LB</span>
          <Figures share={share(funnel.refused)} delta={delta((f) => f.refused)} note={<span>no deployment available</span>} />
        </div>

        {layout.nodes.map((node) => {
          const deployment = byId.get(node.id);
          if (!deployment) return null;
          const item = frame?.deployments.get(node.id);
          const visual = nodeVisual(item, frame?.throttledSince.get(node.id), t);
          const status = statusText(visual, item);
          const flow = funnel.deployments.get(node.id);
          const quota = item ? quotaShare(item.deployment, [...(frame?.deployments.values() ?? [])].map((entry) => entry.deployment), funnel) : null;
          return (
            <div key={node.id} className="deployment" data-look={visual.look} data-dim={!nodeFilter(deployment) || undefined}
              data-compact={node.compact || undefined} ref={bind(`dep:${node.id}`)} {...hover(`dep:${node.id}`)}>
              <button type="button" className="node-hit" style={{ ["--r" as string]: `${node.r}px` }}
                aria-label={`${modelShort(deployment.modelKey)} in ${placeNames.get(node.place) ?? node.place}, ${tierName(deployment.tier)}, ${status ?? "healthy"}, serving ${percent(share(flow?.served ?? 0))} of requests`}
                onFocus={() => setHovered(`dep:${node.id}`)} onBlur={() => setHovered(null)} />
              <div className="flow-label" data-kind="deployment">
                <span className="flow-name">{modelShort(deployment.modelKey)} <span className="flow-weight">{weightLabel(deployment.weight, deployment.tier)}</span></span>
                <Figures share={share(flow?.served ?? 0)} delta={delta((f) => f.deployments.get(node.id)?.served ?? 0)}
                  inline={status ? <StatusNote look={visual.look} text={status} /> : <WeightNote quota={quota} served={share(flow?.served ?? 0)} />}
                  note={node.compact ? null : <FlowNote flow={flow} share={share} spill={false} />} />
              </div>
            </div>
          );
        })}
      </div>
      {layout.nodes.length > 0 && (
        <p className="scene-hint">Point at a tier, region or deployment to trace its traffic. Click a moving request to follow it.</p>
      )}
      <AnimatePresence>
        {hoveredId && hoveredItem && hoveredPosition && (
          <NodeCard key={hoveredId} item={hoveredItem} rate={frame?.rates.get(hoveredId)} flow={funnel.deployments.get(hoveredId)} total={total}
            seconds={funnel.seconds} visual={nodeVisual(hoveredItem, frame?.throttledSince.get(hoveredId), t)}
            x={hoveredPosition.x} y={hoveredPosition.y} r={hoveredPosition.r} sceneWidth={layout.size.width} sceneHeight={layout.size.height} />
        )}
      </AnimatePresence>
      {layout.nodes.length === 0 && (
        <motion.p className="scene-empty" initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: 0.6 }}>
          Waiting for the first load balancer batch. Deployments appear here once a replica reports its routing table.
        </motion.p>
      )}
    </div>
  );
}

/** The node's share in large type, with its change against a minute ago and one line of context. */
function Figures({ share, delta, inline, note }: { share: number; delta: number | null; inline?: ReactNode; note: ReactNode }) {
  // Big shares read first: the figure grows with the square root of its share, and a zero share recedes.
  return (
    <>
      <span className="flow-figures">
        <span className="flow-share" data-zero={share === 0 || undefined} style={{ ["--s" as string]: Math.sqrt(share).toFixed(3) }}>{percent(share)}</span>
        {delta !== null && (
          <span className="flow-delta" data-direction={delta > 0 ? "up" : "down"} title="Change in share against the same window a minute earlier">
            {delta > 0 ? "+" : "−"}{Math.round(Math.abs(delta))} pts
          </span>
        )}
        {inline && <span className="flow-inline">{inline}</span>}
      </span>
      {note && <span className="flow-note">{note}</span>}
    </>
  );
}

/** Why requests left this node, or what it took that it should not have: the fallback story in one line. */
function FlowNote({ flow, share, spill = true }: { flow: Flow | undefined; share: (count: number) => number; spill?: boolean }) {
  if (!flow) return null;
  const left = flow.throttled + flow.failed;
  // Background noise (a stray 500) stays out of the labels; a node that loses a real part of its traffic shows it.
  if (left > 0 && (left / flow.reached >= 0.2 || share(left) >= 0.02)) {
    const tone = flow.throttled >= flow.failed ? "throttled" : "failed";
    return <span data-tone={tone}>{percent(share(left))} moved on</span>;
  }
  if (spill && share(flow.spilled) >= 0.01) return <span data-tone="spill">{percent(share(flow.spilled))} spilled in</span>;
  return null;
}

/** What the deployment's weight asks for, shown only when the actual share is clearly off it. */
function WeightNote({ quota, served }: { quota: number | null; served: number }) {
  if (quota === null || Math.abs(served - quota) < 0.03) return null;
  return <span data-tone="quota" title="Share this deployment would serve if its group split traffic by weight">weight {percent(quota)}</span>;
}

function StatusNote({ look, text }: { look: NodeLook; text: string }) {
  return <span data-look={look}>{text}</span>;
}

/**
 * Scene keys to keep bright while something is hovered or focused: the node, its place and descendants, and for a
 * deployment, the deployments its requests fell back to.
 */
function emphasise(hovered: string | null, focus: string[] | null, layout: SceneLayout, funnel: Funnel): Set<string> | null {
  const keys = new Set<string>();
  const addDeployment = (id: string) => {
    const node = layout.nodes.find((item) => item.id === id);
    if (!node) return;
    keys.add(`dep:${id}`);
    keys.add(`place:${node.place}`);
  };
  if (focus && focus.length > 0) for (const id of focus) addDeployment(id);
  else if (hovered?.startsWith("dep:")) {
    const id = hovered.slice(4);
    addDeployment(id);
    for (const path of funnel.paths) {
      const index = path.hops.findIndex((hop) => hop.deploymentId === id);
      if (index >= 0) for (const hop of path.hops.slice(index + 1)) addDeployment(hop.deploymentId);
    }
  } else if (hovered?.startsWith("place:")) {
    keys.add(hovered);
    for (const node of layout.nodes) if (`place:${node.place}` === hovered) addDeployment(node.id);
  } else if (hovered?.startsWith("tier:")) {
    keys.add(hovered);
    for (const node of layout.nodes) if (`tier:${node.tier}` === hovered) addDeployment(node.id);
  } else if (hovered === "refused") keys.add("refused");
  else return null;
  for (const caller of layout.callers) keys.add(`caller:${caller.id}`);
  return keys;
}
