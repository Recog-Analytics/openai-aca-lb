import { placeKey } from "./funnel";
import { modelName, poolLabels, regionName } from "./names";
import type { DashboardDeployment } from "./types";

export interface Size { width: number; height: number }
export interface Point { x: number; y: number }

/** How the funnel groups deployments after the LB: by where they run, or by what they serve. */
export type View = "region" | "model";
/** Label density, chosen from the space each lane gets: three lines, two lines, or one. */
export type Density = "full" | "two" | "one";

export interface NodeLayout extends Point {
  /**
   * A deployment id; `idle:<group>` for the lane of a group's idle deployments; `fold:<group>:<problem>` for the lane of
   * many deployments in a group with the same problem (an account outage downs every deployment at once).
   */
  id: string;
  kind: "deployment" | "idle" | "fold";
  /** Deployments this node stands for: itself, or the deployments its lane folds. */
  members: string[];
  r: number;
  tier: number;
  /** Key of the group (region or model) the node sits in. */
  place: string;
  density: Density;
  /** Top and bottom of the node's lane: its label never leaves it. */
  top: number;
  bottom: number;
}
export interface CallerLayout { id: string; y: number }
export interface PlaceLayout {
  key: string;
  /** Primary text: "Sweden Central", "gpt-4o-mini · public", "12 idle models". */
  label: string;
  y: number;
  top: number;
  bottom: number;
  /** Every deployment in the group is idle: the box is one line and holds no lanes but its idle lane. */
  compact: boolean;
  /** Name and share fit one line of the box ("gpt-5 6.5 %"), so a dense layout gives the box one line. */
  inline: boolean;
  /** Idle deployments the group folds into one lane, and whether the operator expanded them. */
  idle: number;
  expanded: boolean;
}

export interface Columns {
  callers: number;
  lb: number;
  /** Left edge of the group boxes; ribbons enter here. */
  places: number;
  /** Right edge of the group boxes; ribbons leave here. */
  placesEnd: number;
  /** Deployment bars, where ribbons end. */
  bars: number;
  /** Deployment state discs, right of their bars. */
  discs: number;
}

export interface SceneLayout {
  view: View;
  size: Size;
  /** Height the lanes need; larger than the visible height when even one-line labels do not fit, and the scene scrolls. */
  contentHeight: number;
  rem: number;
  density: Density;
  /** Ribbon width in pixels for 100 % of the requests in scope. */
  scale: number;
  laneHeight: number;
  /** Top of the lane area, under the column titles. */
  top: number;
  columns: Columns;
  callers: CallerLayout[];
  lb: Point;
  places: PlaceLayout[];
  nodes: NodeLayout[];
  /** The node that draws each deployment: itself, or its group's idle lane. */
  nodeOf: Map<string, string>;
  /** Where requests the LB refused (no attempt) end, under the last group. */
  refused: Point;
}

export interface LayoutInput {
  deployments: DashboardDeployment[];
  callers: string[];
  size: Size;
  rem?: number;
  view?: View;
  /** Healthy deployments with (almost) no traffic: they fold into their group's idle lane. */
  idle?: ReadonlySet<string>;
  /** Deployments with a problem, and which problem: their label gets one more line than the rest. */
  problems?: ReadonlyMap<string, string>;
  /** Groups whose idle deployments the operator expanded. */
  expanded?: ReadonlySet<string>;
}

/**
 * Label metrics in rem, shared with styles.css (`.scene[data-density]`). A lane is never shorter than the label it
 * holds, so labels in one column cannot overlap at any inventory size; when lanes would get shorter, the scene grows and
 * scrolls instead.
 */
export const labelHeight: Record<Density, number> = { full: 4.1, two: 2.6, one: 1 };
/** The tallest lane worth drawing; beyond it the lanes centre in the free space. */
const MAX_LANE = 5.5;
/** Group box heights in rem: name, share and note; name over share; one line for an idle group. */
const boxHeight: Record<Density, number> = { full: 3.1, two: 2.5, one: 2.5 };
const COMPACT_BOX = 1.75;
/** Gap between groups, in lanes, with a floor in rem. */
const GROUP_GAP = 0.45;
const MIN_GAP = 0.5;
/** Space under the last group for the refused box, which may take every request: a full ribbon and a gap. */
const REFUSED_SPACE = 2.8;
export const IDLE_MODELS = "idle-models";
const richer: Record<Density, Density> = { one: "two", two: "full", full: "full" };
/** This many deployments of one group with the same problem share one lane; fewer keep a lane each. */
export const FOLD_PROBLEMS = 4;

/** The key a deployment's group has in a view: its place for regions, its pool label for models. */
export function groupKey(view: View, deployment: DashboardDeployment, labels: Map<string, string>): string {
  return view === "region" ? placeKey(deployment) : (labels.get(deployment.id) ?? modelName(deployment.modelKey));
}

/** Natural order, so "gpt-4.1" < "gpt-4.1-mini" < "gpt-5" < "gpt-5.1" < "o3". */
const natural = (a: string, b: string) => a.localeCompare(b, "en", { numeric: true, sensitivity: "base" });

interface Lane { node: Omit<NodeLayout, "x" | "y" | "top" | "bottom" | "r" | "density">; height: (lane: number, density: Density) => number; density: (base: Density) => Density }
interface Group { place: Omit<PlaceLayout, "y" | "top" | "bottom">; lanes: Lane[] }

/**
 * The funnel as a tree that reads left to right: callers → LB → group → deployment. Groups are regions (and Global) or
 * models; each appears once. Every busy or troubled deployment has its own lane; idle healthy ones share one lane per
 * group until the operator expands it. Positions depend on structure and on which deployments are idle, never on traffic
 * shares, so nodes keep their place while ribbon widths move. Pure: same input, same output.
 */
export function layoutScene(input: LayoutInput): SceneLayout {
  const { deployments, callers, size } = input;
  const rem = input.rem ?? 16;
  const view = input.view ?? "region";
  const idle = input.idle ?? new Set<string>();
  const problems = input.problems ?? new Map<string, string>();
  const expanded = input.expanded ?? new Set<string>();
  const { width, height } = size;
  const top = 2.6 * rem;
  const groups = buildGroups(view, deployments, idle, problems, expanded, rem);

  // Columns: caller names left of their bars, the LB close by, group boxes in the middle, deployment labels on the right.
  const callersX = 9.5 * rem;
  const lbX = callersX + 6 * rem;
  const discs = width - 15 * rem;
  const bars = discs - 1.5 * rem;
  const boxWidth = 11 * rem;
  const places0 = lbX + Math.max(2 * rem, (bars - lbX - boxWidth) * 0.45);
  const columns: Columns = { callers: callersX, lb: lbX, places: places0, placesEnd: places0 + boxWidth, bars, discs };
  // Refused requests end in a box in the free corner right of the LB, at the bottom of the visible scene, so a refusal
  // is in view without a scroll. Only when that corner is too narrow does the box go under the last group.
  const refusedX = Math.min(lbX + 4 * rem, places0 - boxWidth - rem);
  const refusedInCorner = refusedX >= lbX + 1.5 * rem;
  const refusedSpace = refusedInCorner ? 0 : REFUSED_SPACE * rem;

  const laneCount = groups.reduce((sum, group) => sum + group.lanes.length, 0);
  const total = (lane: number, density: Density) => {
    const gap = Math.max(MIN_GAP * rem, lane * GROUP_GAP);
    return groups.reduce((sum, group) => sum + groupHeight(group, lane, density, rem), 0) + gap * Math.max(0, groups.length - 1) +
      refusedSpace;
  };
  const available = Math.max(0, height - top - 0.6 * rem);
  // The richest density whose shortest lanes still fit; then the tallest lane that fits, up to MAX_LANE.
  let density: Density = "one";
  let laneHeight = labelHeight.one * rem;
  for (const candidate of ["full", "two", "one"] as const) {
    const min = labelHeight[candidate] * rem;
    if (candidate !== "one" && total(min, candidate) > available) continue;
    density = candidate;
    let low = min;
    let high = Math.max(min, MAX_LANE * rem);
    if (total(high, candidate) <= available) low = high;
    else for (let step = 0; step < 24; step++) {
      const middle = (low + high) / 2;
      if (total(middle, candidate) <= available) low = middle;
      else high = middle;
    }
    laneHeight = laneCount === 0 ? 0 : low;
    break;
  }
  const used = total(laneHeight, density);
  const contentHeight = Math.max(height, Math.ceil(top + used + 0.6 * rem));
  // Centre the lanes vertically when they do not need the full height.
  let cursor = top + Math.max(0, (available - used) / 2);
  const gap = Math.max(MIN_GAP * rem, laneHeight * GROUP_GAP);
  const r = Math.max(4, Math.min(laneHeight * 0.3, 0.8 * rem, density === "one" ? 0.42 * rem : Infinity));

  const nodes: NodeLayout[] = [];
  const places: PlaceLayout[] = [];
  const nodeOf = new Map<string, string>();
  groups.forEach((group, index) => {
    if (index > 0) cursor += gap;
    const lanes = group.lanes.reduce((sum, lane) => sum + lane.height(laneHeight, density), 0);
    const box = groupHeight(group, laneHeight, density, rem);
    const groupTop = cursor;
    // Lanes centre on the box when the box is taller than they are.
    let laneCursor = groupTop + (box - lanes) / 2;
    for (const lane of group.lanes) {
      const laneTop = laneCursor;
      laneCursor += lane.height(laneHeight, density);
      const own = lane.density(density);
      nodes.push({ ...lane.node, density: own, x: 0, y: (laneTop + laneCursor) / 2, top: laneTop, bottom: laneCursor,
        r: lane.node.kind === "deployment" ? r : Math.max(3.5, r * (lane.node.kind === "idle" ? 0.7 : 1)) });
      for (const member of lane.node.members) nodeOf.set(member, lane.node.id);
    }
    cursor += box;
    places.push({ ...group.place, top: groupTop, bottom: cursor, y: (groupTop + cursor) / 2 });
  });
  const lastBottom = places.at(-1)?.bottom ?? top + available / 2;
  const firstTop = places[0]?.top ?? top + available / 2;
  const lbY = (firstTop + lastBottom) / 2;

  for (const node of nodes) node.x = discs;

  // A single-lane group may take all the traffic; its ribbon must still fit its band.
  const scale = Math.max(6, Math.min(Math.max(laneHeight * 1.4, 2.6 * rem), height * 0.28));
  // Callers sit as close together as their labels allow, centred on the LB, so their ribbons stay short and flat.
  const callerSpacing = callers.length <= 1 ? 0 : Math.max(4 * rem, Math.min(scale / callers.length + 1.5 * rem, 4.8 * rem));
  const callerLayouts = callers.map((id, index) => ({ id, y: lbY + (index - (callers.length - 1) / 2) * callerSpacing }));

  return {
    view, size, contentHeight, rem, density, laneHeight, top, columns, nodes, places, nodeOf, callers: callerLayouts, scale,
    lb: { x: lbX, y: lbY },
    refused: refusedInCorner ? { x: refusedX, y: height - 2.9 * rem }
      : { x: places0, y: lastBottom + Math.max(2.6 * rem, Math.min(laneHeight, 3.2 * rem)) },
  };
}

function groupHeight(group: Group, lane: number, density: Density, rem: number): number {
  const lanes = group.lanes.reduce((sum, item) => sum + item.height(lane, density), 0);
  return Math.max(lanes, (group.place.compact || (group.place.inline && density !== "full") ? COMPACT_BOX : boxHeight[density]) * rem);
}

/** Groups in reading order, each with its lanes: busy or troubled deployments, then one idle lane (or every idle one). */
function buildGroups(view: View, deployments: DashboardDeployment[], idle: ReadonlySet<string>, problems: ReadonlyMap<string, string>,
  expanded: ReadonlySet<string>, rem: number): Group[] {
  const labels = poolLabels(deployments);
  const byGroup = new Map<string, DashboardDeployment[]>();
  for (const item of deployments) {
    const key = groupKey(view, item, labels);
    byGroup.set(key, [...(byGroup.get(key) ?? []), item]);
  }
  const homeZone = (items: DashboardDeployment[]) => (items.some((item) => item.zone !== "global") ? items.find((item) => item.zone !== "global")?.zone ?? "" : "￿");
  const leafName = (item: DashboardDeployment) => (view === "region" ? labels.get(item.id) ?? modelName(item.modelKey) : regionName(item.region));
  const leafOrder = (a: DashboardDeployment, b: DashboardDeployment) => a.tier - b.tier || natural(leafName(a), leafName(b)) || a.id.localeCompare(b.id);
  const isIdle = (item: DashboardDeployment) => idle.has(item.id) && !problems.has(item.id);

  const deploymentLane = (item: DashboardDeployment, key: string): Lane => ({
    node: { id: item.id, kind: "deployment", members: [item.id], tier: item.tier, place: key },
    height: (lane, density) => (problems.has(item.id) ? Math.max(lane, labelHeight[richer[density]] * rem) : lane),
    density: (base) => (problems.has(item.id) ? richer[base] : base),
  });
  const idleLane = (key: string, members: DashboardDeployment[]): Lane => ({
    node: { id: `idle:${key}`, kind: "idle", members: members.map((item) => item.id), tier: Math.min(...members.map((item) => item.tier)), place: key },
    height: (lane) => Math.max(labelHeight.one * rem, lane * 0.5),
    density: () => "one",
  });

  const groups: Group[] = [];
  const idleModels: DashboardDeployment[] = [];
  let unusedModels = 0;
  const keys = [...byGroup.keys()].sort((a, b) => view === "region"
    ? homeZone(byGroup.get(a) ?? []).localeCompare(homeZone(byGroup.get(b) ?? [])) || (a === "global" ? 1 : b === "global" ? -1 : natural(regionName(a), regionName(b)))
    : natural(a, b));
  for (const key of keys) {
    const items = [...(byGroup.get(key) ?? [])].sort(leafOrder);
    const quiet = items.filter(isIdle);
    const busy = items.filter((item) => !isIdle(item));
    // A model nobody uses joins one "idle models" group, so twelve unused models cost one line, not twelve boxes.
    if (view === "model" && busy.length === 0) {
      unusedModels++;
      if (!expanded.has(IDLE_MODELS)) {
        idleModels.push(...items);
        continue;
      }
    }
    const open = expanded.has(key);
    // Collapsed, many deployments with the same problem share a red lane; the problem stays in view, the list folds.
    const byProblem = new Map<string, DashboardDeployment[]>();
    for (const item of busy) {
      const problem = problems.get(item.id);
      if (problem) byProblem.set(problem, [...(byProblem.get(problem) ?? []), item]);
    }
    const folded = new Set<string>();
    const folds: Lane[] = [];
    for (const [problem, members] of byProblem) {
      if (members.length < FOLD_PROBLEMS) continue;
      for (const item of members) folded.add(item.id);
      folds.push({
        node: { id: `fold:${key}:${problem}`, kind: "fold", members: members.map((item) => item.id), tier: Math.min(...members.map((item) => item.tier)), place: key },
        height: (lane, density) => Math.max(lane, labelHeight[richer[density]] * rem),
        density: (base) => richer[base],
      });
    }
    const lanes = open ? items.map((item) => deploymentLane(item, key))
      : [...folds, ...busy.filter((item) => !folded.has(item.id)).map((item) => deploymentLane(item, key))];
    // Expanded, the idle lane stays as the way back; it then stands for no deployment.
    if (quiet.length > 0 && !open) lanes.push(idleLane(key, quiet));
    else if (open && quiet.length + folded.size > 0) {
      const lane = idleLane(key, quiet.length > 0 ? quiet : items);
      lanes.push({ ...lane, node: { ...lane.node, members: [] } });
    }
    const label = view === "region" ? (key === "global" ? "Global" : regionName(key)) : key;
    groups.push({ place: { key, label, compact: busy.length === 0 && !open, inline: fitsOneLine(label), idle: quiet.length + folded.size, expanded: open }, lanes });
  }
  // Expanded, the unused models stand in their own groups; a last "Hide" lane folds them back.
  if (view === "model" && expanded.has(IDLE_MODELS) && unusedModels > 0) {
    const lane = idleLane(IDLE_MODELS, deployments);
    groups.push({
      place: { key: IDLE_MODELS, label: unusedModels === 1 ? "1 idle model" : `${unusedModels} idle models`, compact: true, inline: true, idle: unusedModels, expanded: true },
      lanes: [{ ...lane, node: { ...lane.node, members: [] } }],
    });
  }
  if (idleModels.length > 0) {
    const models = new Set(idleModels.map((item) => groupKey("model", item, labels))).size;
    groups.push({
      place: { key: IDLE_MODELS, label: models === 1 ? "1 idle model" : `${models} idle models`, compact: true, inline: true, idle: idleModels.length, expanded: false },
      lanes: [idleLane(IDLE_MODELS, idleModels)],
    });
  }
  return groups;
}

/**
 * Whether a group's name and a share ("100 %") fit one line of its box: 11rem less 1.5rem of padding, at the name's
 * 0.92rem and an average Geist advance of 0.56em.
 */
function fitsOneLine(label: string): boolean {
  return (label.length + 7) * 0.56 * 0.92 <= 9.5;
}

/** Where each child's ribbon leaves its parent: stacked in child order and centred on the parent, as in a Sankey. */
export function stackPorts(center: number, widths: number[], gap = 0): number[] {
  const total = widths.reduce((sum, width) => sum + width, 0) + gap * Math.max(0, widths.length - 1);
  let cursor = center - total / 2;
  return widths.map((width) => {
    const port = cursor + width / 2;
    cursor += width + gap;
    return port;
  });
}

/** Point on a horizontal-tangent cubic link from `a` to `b` at progress `t`. */
export function linkPoint(a: Point, b: Point, t: number): Point {
  const dx = (b.x - a.x) * 0.5;
  const u = 1 - t;
  const c1x = a.x + dx;
  const c2x = b.x - dx;
  return {
    x: u * u * u * a.x + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * b.x,
    y: u * u * u * a.y + 3 * u * u * t * a.y + 3 * u * t * t * b.y + t * t * t * b.y,
  };
}

/** A polyline with cumulative lengths, for moving a particle at constant speed along a route. */
export interface Path { points: Point[]; lengths: number[]; total: number }

export function makePath(points: Point[]): Path {
  const lengths = [0];
  for (let index = 1; index < points.length; index++) {
    const a = points[index - 1];
    const b = points[index];
    lengths.push((lengths[index - 1] ?? 0) + (a && b ? Math.hypot(b.x - a.x, b.y - a.y) : 0));
  }
  return { points, lengths, total: lengths.at(-1) ?? 0 };
}

/** Point at distance fraction `t` (0..1) along a path. */
export function pathPoint(path: Path, t: number): Point {
  const first = path.points[0] ?? { x: 0, y: 0 };
  if (path.total <= 0) return first;
  const target = Math.min(1, Math.max(0, t)) * path.total;
  let low = 0;
  let high = path.lengths.length - 1;
  while (low < high) {
    const middle = (low + high) >> 1;
    if ((path.lengths[middle] ?? 0) < target) low = middle + 1;
    else high = middle;
  }
  const index = Math.max(1, low);
  const a = path.points[index - 1] ?? first;
  const b = path.points[index] ?? a;
  const start = path.lengths[index - 1] ?? 0;
  const length = (path.lengths[index] ?? start) - start;
  const local = length > 0 ? (target - start) / length : 0;
  return { x: a.x + (b.x - a.x) * local, y: a.y + (b.y - a.y) * local };
}

/** Samples a horizontal-tangent link into points, excluding its start. */
export function linkPoints(a: Point, b: Point, samples = 14): Point[] {
  const points: Point[] = [];
  for (let index = 1; index <= samples; index++) points.push(linkPoint(a, b, index / samples));
  return points;
}
