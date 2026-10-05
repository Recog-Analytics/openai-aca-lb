import { placeKey } from "./funnel";
import type { DashboardDeployment } from "./types";

export interface Size { width: number; height: number }
export interface Point { x: number; y: number }

export interface NodeLayout extends Point {
  id: string;
  r: number;
  tier: number;
  place: string;
  /** An idle, healthy fallback: a short lane and a one-line label, so unused capacity stays visible but quiet. */
  compact: boolean;
}
export interface CallerLayout { id: string; y: number }
export interface PlaceLayout { key: string; y: number; top: number; bottom: number; compact: boolean }

export interface Columns {
  callers: number;
  lb: number;
  /** Left edge of the place boxes; ribbons enter here. */
  places: number;
  /** Right edge of the place boxes; ribbons leave here. */
  placesEnd: number;
  /** Deployment bars, where ribbons end. */
  bars: number;
  /** Deployment state discs, right of their bars. */
  discs: number;
}

export interface SceneLayout {
  size: Size;
  rem: number;
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
  /** Where requests the LB refused (no attempt) end, under the last place. */
  refused: Point;
}

/** Lane height of a compact deployment, in full lanes. */
const COMPACT_LANE = 0.5;
const PLACE_GAP = 0.45;
/** Zones sort by name, global last. */
const zoneRank = (zone: string) => (zone === "global" ? "\uffff" : zone);

/**
 * The funnel as a tree that reads left to right: callers → LB → place → deployment. A place is a region, or Global;
 * each appears once, because operators think in regions first. Each deployment has its own lane. Positions depend only
 * on structure and on which deployments are compact, never on traffic shares, so nodes keep their place while ribbon
 * widths move. Pure: same input, same output.
 */
export function layoutScene(deployments: DashboardDeployment[], callers: string[], size: Size, rem = 16,
  compact: ReadonlySet<string> = new Set()): SceneLayout {
  const { width, height } = size;
  const top = 3.4 * rem;
  const bottom = height - 1.2 * rem;

  const homeZone = new Map<string, string>();
  for (const item of deployments) {
    const key = placeKey(item);
    const current = homeZone.get(key);
    if (current === undefined || (current === "global" && item.zone !== "global")) homeZone.set(key, item.zone);
  }
  const sorted = [...deployments].sort((a, b) =>
    zoneRank(homeZone.get(placeKey(a)) ?? "").localeCompare(zoneRank(homeZone.get(placeKey(b)) ?? "")) ||
    placeKey(a).localeCompare(placeKey(b)) || a.tier - b.tier || a.modelKey.localeCompare(b.modelKey) || a.id.localeCompare(b.id));

  const lane = (item: DashboardDeployment) => (compact.has(item.id) ? COMPACT_LANE : 1);
  // The refused box may take all the traffic: room for a full ribbon (scale is 1.4 lanes) and a gap.
  const refusedLane = 1.7;
  let units = refusedLane;
  sorted.forEach((item, index) => {
    const previous = sorted[index - 1];
    units += lane(item) + (previous && placeKey(previous) !== placeKey(item) ? PLACE_GAP : 0);
  });
  const laneHeight = sorted.length === 0 ? 0 : Math.min((bottom - top) / units, 5.5 * rem);
  // Centre the lanes vertically when they do not need the full height.
  let cursor = top + Math.max(0, (bottom - top - laneHeight * units) / 2);

  const nodes: NodeLayout[] = [];
  const places: PlaceLayout[] = [];
  const r = Math.max(4, Math.min(laneHeight * 0.3, 0.8 * rem));
  sorted.forEach((item, index) => {
    const previous = sorted[index - 1];
    const key = placeKey(item);
    if (previous && placeKey(previous) !== key) cursor += laneHeight * PLACE_GAP;
    const laneTop = cursor;
    const small = compact.has(item.id);
    cursor += laneHeight * lane(item);
    nodes.push({ id: item.id, tier: item.tier, place: key, compact: small, x: 0, y: (laneTop + cursor) / 2, r: small ? r * 0.7 : r });
    const place = places.at(-1);
    if (place?.key === key) {
      place.bottom = cursor;
      place.compact &&= small;
    } else places.push({ key, y: 0, top: laneTop, bottom: cursor, compact: small });
  });
  for (const place of places) place.y = (place.top + place.bottom) / 2;
  const lastBottom = places.at(-1)?.bottom ?? (top + bottom) / 2;
  const firstTop = places[0]?.top ?? (top + bottom) / 2;
  const lbY = (firstTop + lastBottom) / 2;

  // Columns: caller names left of their bars, the LB close by, place boxes in the middle, deployment labels on the right.
  const callersX = 9.5 * rem;
  const lbX = callersX + 6 * rem;
  const discs = width - 13 * rem;
  const bars = discs - 1.5 * rem;
  const boxWidth = 10.5 * rem;
  const places0 = lbX + Math.max(2 * rem, (bars - lbX - boxWidth) * 0.45);
  const columns: Columns = { callers: callersX, lb: lbX, places: places0, placesEnd: places0 + boxWidth, bars, discs };
  for (const node of nodes) node.x = discs;

  // A single-lane place may take all the traffic; its ribbon must still fit its band.
  const scale = Math.max(6, Math.min(laneHeight * 1.4, height * 0.28));
  // Callers sit as close together as their labels allow, centred on the LB, so their ribbons stay short and flat.
  const callerSpacing = callers.length <= 1 ? 0 : Math.max(4 * rem, Math.min(scale / callers.length + 1.5 * rem, 4.8 * rem));
  const callerLayouts = callers.map((id, index) => ({ id, y: lbY + (index - (callers.length - 1) / 2) * callerSpacing }));

  return {
    size, rem, laneHeight, top, columns, nodes, places, callers: callerLayouts, scale,
    lb: { x: lbX, y: lbY },
    refused: { x: places0, y: lastBottom + Math.max(laneHeight * 0.95, 2.4 * rem) },
  };
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
