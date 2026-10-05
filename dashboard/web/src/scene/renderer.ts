import { Application, Container, Graphics, Sprite, Texture } from "pixi.js";
import type { PlaybackClock } from "../model/clock";
import type { Flow, Funnel } from "../model/funnel";
import { linkPoints, makePath, pathPoint, stackPorts, type Path, type Point, type SceneLayout } from "../model/layout";
import { planRequest, segmentAt, selectForPlayback, type Anchor, type ParticleTone, type Plan } from "../model/playback";
import { snapSpring, spring, springs, stepSpring, type Spring } from "../model/spring";
import { nodeVisual, type NodeLook } from "../model/state";
import type { FrameState, Timeline } from "../model/timeline";
import type { DashboardDeployment, RequestRecord } from "../model/types";
import { hexToNumber, mix, palettes, type Palette } from "../theme";

export interface SceneInputs {
  layout: SceneLayout;
  funnel: Funnel | null;
  requestFilter: (request: RequestRecord) => boolean;
  nodeFilter: (deployment: DashboardDeployment) => boolean;
  selectedId: string | null;
  /** Scene keys to emphasise ("dep:<id>", "place:<key>", "caller:<id>", "refused"); the rest dims. */
  emphasis: Set<string> | null;
  palette: Palette;
  reducedMotion: boolean;
  onPick: (id: string | null) => void;
}

/** Ribbon width split by what happened to the requests: handled here, left after a 429, left after a failure. */
interface Band { handled: number; throttled: number; failed: number }
const bandWidth = (band: Band) => band.handled + band.throttled + band.failed;

interface Group { x: number; y: number; top: number; bottom: number; band: Band; ports: Map<string, number> }
/** A place box: ribbons enter at its left edge and leave from its right edge. */
interface Place extends Group { end: number }
interface DeploymentGeometry { x: number; disc: number; y: number; r: number; band: Band }

/** Animated positions and ports of everything, rebuilt each frame from springs. */
interface Geometry {
  barWidth: number;
  callers: Map<string, { x: number; y: number; band: Band }>;
  lb: Group & { inPorts: Map<string, number> };
  places: Map<string, Place>;
  deployments: Map<string, DeploymentGeometry>;
  refused: { x: number; y: number; band: Band };
  /** LB out-port → deployment disc, through its place. */
  paths: Map<string, Path>;
  refusedPath: Path;
  /** Caller → LB in-port. */
  callerPaths: Map<string, Path>;
}

interface Particle { id: string; x: number; y: number }

const PARTICLES_PER_SECOND = 40;
const MAX_PARTICLES = 320;
const LONGEST_PLAN_MS = 14_000;

/**
 * Draws the funnel (ribbons, bars, deployment discs) and request particles with PixiJS, and moves the DOM labels.
 * One continuous scene: inputs only move spring targets, so nothing restarts when data changes. Ribbon widths come
 * from exact counts; particles are a sample and only show the shape of individual requests.
 */
export class SceneRenderer {
  private inputs: SceneInputs;
  private readonly ribbons = new Graphics();
  private readonly trails = new Graphics();
  private readonly structure = new Graphics();
  private readonly discs = new Graphics();
  private readonly bursts = new Graphics();
  private readonly particleLayer = new Container();
  private readonly highlight = new Graphics();
  private readonly springs = new Map<string, Spring>();
  private readonly seen = new Set<string>();
  private readonly colorsByNode = new Map<string, number>();
  private readonly flashes = new Map<string, Spring>();
  private readonly sprites: Sprite[] = [];
  private readonly plans = new Map<string, Plan>();
  private readonly flashed = new Set<string>();
  private readonly buckets = new Map<number, Set<string>>();
  private bucketKey = "";
  private readonly labels = new Map<string, HTMLElement>();
  private visible: Particle[] = [];
  private initialized = false;
  private geometry: Geometry | null = null;
  /** Particle routes that enter the LB at a caller's port, built at most once per frame. */
  private readonly routes = new Map<string, Path>();
  private deltaMs = 16;
  private readonly texture: Texture;
  private filterVersion = 0;

  private constructor(private readonly app: Application, private readonly timeline: Timeline,
    private readonly clock: PlaybackClock, inputs: SceneInputs) {
    this.inputs = inputs;
    this.texture = dotTexture();
    app.stage.addChild(this.ribbons, this.trails, this.structure, this.discs, this.bursts, this.highlight, this.particleLayer);
    app.ticker.add((ticker) => this.frame(ticker.deltaMS));
    const canvas = app.canvas;
    canvas.addEventListener("pointerdown", (event) => this.inputs.onPick(this.pick(event)));
    canvas.addEventListener("pointermove", (event) => { canvas.style.cursor = this.pick(event) ? "pointer" : "default"; });
  }

  static async create(host: HTMLElement, timeline: Timeline, clock: PlaybackClock, inputs: SceneInputs): Promise<SceneRenderer> {
    const app = new Application();
    await app.init({ resizeTo: host, backgroundAlpha: 0, antialias: true, autoDensity: true,
      resolution: Math.min(window.devicePixelRatio || 1, 3), preference: "webgl" });
    app.canvas.setAttribute("aria-hidden", "true");
    host.appendChild(app.canvas);
    return new SceneRenderer(app, timeline, clock, inputs);
  }

  update(inputs: Partial<SceneInputs>): void {
    if (inputs.requestFilter && inputs.requestFilter !== this.inputs.requestFilter) this.filterVersion++;
    this.inputs = { ...this.inputs, ...inputs };
  }

  /** Binds a DOM label to a scene key; the renderer moves it every frame. */
  bindLabel(key: string, element: HTMLElement | null): void {
    if (element) this.labels.set(key, element);
    else this.labels.delete(key);
  }

  /** Current animated position of a deployment disc, for anchoring popovers. */
  nodePosition(id: string): (Point & { r: number }) | null {
    const node = this.geometry?.deployments.get(id);
    return node ? { x: node.disc, y: node.y, r: node.r } : null;
  }

  destroy(): void {
    this.app.destroy(true, { children: true, texture: true });
  }

  private frame(deltaMs: number): void {
    const { layout, palette } = this.inputs;
    const t = this.clock.time(Date.now());
    const state = this.timeline.stateAt(t);
    const colors = paletteNumbers(palette);
    this.deltaMs = deltaMs;
    this.seen.clear();
    const geometry = this.build();
    this.geometry = geometry;
    this.routes.clear();
    for (const key of [...this.springs.keys()]) if (!this.seen.has(key)) this.springs.delete(key);

    const streams = new Map<string, number>();
    this.bursts.clear();
    this.placeParticles(geometry, t, colors, streams);
    this.drawRibbons(geometry, state, t, colors, streams);
    this.drawStructure(geometry, state, t, colors);
    this.drawHighlight(geometry, colors);
    this.moveLabels(geometry);
    if (!this.initialized) this.initialized = layout.nodes.length > 0;
  }

  /** A spring per key: created at its first target, stepped once per frame, dropped when no longer used. */
  private value(key: string, target: number, config: { stiffness: number; damping: number } = springs.layout): number {
    let s = this.springs.get(key);
    if (!s) {
      s = spring(target);
      this.springs.set(key, s);
    }
    this.seen.add(key);
    s.target = target;
    if (this.inputs.reducedMotion || !this.initialized) snapSpring(s, target);
    else stepSpring(s, this.deltaMs, config);
    return s.value;
  }

  private band(key: string, flow: Flow | undefined, total: number, scale: number): Band {
    const share = (count: number) => (total > 0 ? (count / total) * scale : 0);
    return {
      handled: Math.max(0, this.value(`${key}:h`, share((flow?.served ?? 0) + (flow?.answered ?? 0)), springs.data)),
      throttled: Math.max(0, this.value(`${key}:t`, share(flow?.throttled ?? 0), springs.data)),
      failed: Math.max(0, this.value(`${key}:f`, share(flow?.failed ?? 0), springs.data)),
    };
  }

  private build(): Geometry {
    const { layout, funnel } = this.inputs;
    const total = funnel?.total ?? 0;
    const scale = layout.scale;
    const c = layout.columns;
    const x = {
      callers: this.value("x:callers", c.callers), lb: this.value("x:lb", c.lb), places: this.value("x:places", c.places),
      placesEnd: this.value("x:placesEnd", c.placesEnd), bars: this.value("x:bars", c.bars), discs: this.value("x:discs", c.discs),
    };
    const barWidth = Math.max(4, layout.rem * 0.38);
    const minBar = Math.max(6, layout.rem * 0.5);

    const callers = new Map<string, { x: number; y: number; band: Band }>();
    for (const caller of layout.callers) {
      const flow = funnel?.callers.get(caller.id);
      const band = this.band(`caller:${caller.id}`, flow, total, scale);
      callers.set(caller.id, { x: x.callers, y: this.value(`caller:${caller.id}:y`, caller.y), band });
    }

    const deployments = new Map<string, DeploymentGeometry>();
    for (const node of layout.nodes)
      deployments.set(node.id, {
        x: x.bars, disc: x.discs, y: this.value(`dep:${node.id}:y`, node.y), r: this.value(`dep:${node.id}:r`, node.r),
        band: this.band(`dep:${node.id}`, funnel?.deployments.get(node.id), total, scale),
      });

    const places = new Map<string, Place>();
    for (const place of layout.places) {
      const children = layout.nodes.filter((node) => node.place === place.key)
        .map((node) => ({ id: node.id, item: deployments.get(node.id) })).sort((a, b) => (a.item?.y ?? 0) - (b.item?.y ?? 0));
      const y = this.value(`place:${place.key}:y`, place.y);
      const widths = children.map((child) => (child.item ? bandWidth(child.item.band) : 0));
      const ports = stackPorts(y, widths);
      const band = this.band(`place:${place.key}`, funnel?.places.get(place.key), total, scale);
      const height = Math.max(minBar, bandWidth(band), widths.reduce((a, b) => a + b, 0));
      places.set(place.key, { x: x.places, end: x.placesEnd, y, top: y - height / 2, bottom: y + height / 2, band,
        ports: new Map(children.map((child, index) => [child.id, ports[index] ?? y])) });
    }

    const refusedShare = total > 0 ? ((funnel?.refused ?? 0) / total) * scale : 0;
    const refused = {
      x: x.places, y: this.value("refused:y", layout.refused.y),
      band: { handled: 0, throttled: 0, failed: Math.max(0, this.value("refused:f", refusedShare, springs.data)) },
    };

    // The LB stacks callers on its left and places (then refused) on its right, in vertical order.
    const lbY = this.value("lb:y", layout.lb.y);
    const callerOrder = [...callers].sort((a, b) => a[1].y - b[1].y);
    const inWidths = callerOrder.map(([, item]) => bandWidth(item.band));
    const inPorts = stackPorts(lbY, inWidths);
    const outOrder: [string, number, number][] = [...places].map(([key, group]) => [`place:${key}`, group.y, bandWidth(group.band)]);
    outOrder.push(["refused", refused.y, bandWidth(refused.band)]);
    outOrder.sort((a, b) => a[1] - b[1]);
    const outPorts = stackPorts(lbY, outOrder.map(([, , width]) => width));
    const lbHeight = Math.max(minBar * 2, inWidths.reduce((a, b) => a + b, 0), outOrder.reduce((a, [, , w]) => a + w, 0));
    const lb = {
      x: x.lb, y: lbY, top: lbY - lbHeight / 2, bottom: lbY + lbHeight / 2, band: { handled: 0, throttled: 0, failed: 0 },
      ports: new Map(outOrder.map(([key], index) => [key, outPorts[index] ?? lbY])),
      inPorts: new Map(callerOrder.map(([id], index) => [id, inPorts[index] ?? lbY])),
    };

    const half = barWidth / 2;
    const paths = new Map<string, Path>();
    for (const node of layout.nodes) {
      const item = deployments.get(node.id);
      const place = places.get(node.place);
      if (!item || !place) continue;
      const a = { x: lb.x + half, y: lb.ports.get(`place:${node.place}`) ?? lbY };
      const b = { x: place.x, y: place.y };
      // Inside the box the request crosses from the place's centre to its port: hidden under the label, so straight.
      const c = { x: place.end, y: place.ports.get(node.id) ?? place.y };
      const d = { x: item.x - half, y: item.y };
      paths.set(node.id, makePath([a, ...linkPoints(a, b), c, ...linkPoints(c, d), { x: item.disc - item.r, y: item.y }]));
    }
    const refusedStart = { x: lb.x + half, y: lb.ports.get("refused") ?? lbY };
    const refusedPath = makePath([refusedStart, ...linkPoints(refusedStart, { x: refused.x, y: refused.y })]);
    const callerPaths = new Map<string, Path>();
    for (const [id, caller] of callers) {
      const start = { x: caller.x + half, y: caller.y };
      callerPaths.set(id, makePath([start, ...linkPoints(start, { x: lb.x - half, y: lb.inPorts.get(id) ?? lbY })]));
    }
    return { barWidth, callers, lb, places, deployments, refused, paths, refusedPath, callerPaths };
  }

  /** Per-second particle selection, recomputed only when data or filters change. */
  private selected(second: number): Set<string> {
    const key = `${this.timeline.version}:${this.filterVersion}`;
    if (key !== this.bucketKey) {
      this.buckets.clear();
      this.bucketKey = key;
    }
    let bucket = this.buckets.get(second);
    if (!bucket) {
      const items = this.timeline.playingBetween(second * 1000, second * 1000 + 1000)
        .filter((item) => this.inputs.requestFilter(item.request));
      bucket = new Set(selectForPlayback(items, PARTICLES_PER_SECOND).map((item) => item.request.id));
      this.buckets.set(second, bucket);
    }
    return bucket;
  }

  /** The path a particle follows between two anchors, entering the LB at its caller's port. */
  private route(g: Geometry, from: Anchor, to: Anchor, caller: string): { path: Path; reverse: boolean } | null {
    const viaLb = (path: Path, key: string) => {
      const cacheKey = `${caller}>${key}`;
      let result = this.routes.get(cacheKey);
      if (!result) {
        const lbIn = { x: g.lb.x - g.barWidth / 2, y: g.lb.inPorts.get(caller) ?? g.lb.y };
        this.routes.set(cacheKey, (result = makePath([lbIn, ...path.points])));
      }
      return result;
    };
    if (from.kind === "caller") {
      const path = g.callerPaths.get(from.id);
      return path ? { path, reverse: false } : null;
    }
    if (from.kind === "lb" && to.kind === "node") {
      const path = g.paths.get(to.id);
      return path ? { path: viaLb(path, to.id), reverse: false } : null;
    }
    if (from.kind === "node" && to.kind === "lb") {
      const path = g.paths.get(from.id);
      return path ? { path: viaLb(path, from.id), reverse: true } : null;
    }
    if (from.kind === "lb" && to.kind === "refused") return { path: viaLb(g.refusedPath, "refused"), reverse: false };
    return null;
  }

  private point(g: Geometry, anchor: Anchor, inside = 0): Point {
    if (anchor.kind === "node") {
      const node = g.deployments.get(anchor.id);
      return node ? { x: node.disc - node.r * (1 - inside), y: node.y } : { x: g.lb.x, y: g.lb.y };
    }
    if (anchor.kind === "refused") return { x: g.refused.x, y: g.refused.y };
    if (anchor.kind === "caller") return g.callers.get(anchor.id) ?? { x: g.lb.x, y: g.lb.y };
    return { x: g.lb.x, y: g.lb.y };
  }

  private placeParticles(g: Geometry, t: number, colors: Colors, streams: Map<string, number>): void {
    const { reducedMotion, selectedId } = this.inputs;
    const candidates = this.timeline.playingBetween(t - LONGEST_PLAN_MS, t + 1);
    const visible: Particle[] = [];
    const livePlans = new Set<string>();
    let used = 0;
    for (const item of candidates) {
      const id = item.request.id;
      if (!this.selected(Math.floor(item.playAt / 1000)).has(id) && id !== selectedId) continue;
      if (used >= MAX_PARTICLES) break;
      let plan = this.plans.get(id);
      if (!plan) {
        plan = planRequest(item.request);
        this.plans.set(id, plan);
      }
      livePlans.add(id);
      const elapsed = t - item.playAt;
      const selected = id === selectedId;
      const caller = item.request.caller ?? "unknown";
      if (reducedMotion) {
        // Reduced motion: no travel. The request appears where it ended and fades.
        const last = plan.segments.at(-1);
        if (!last || elapsed < 0 || elapsed > 1500) continue;
        const point = this.point(g, last.to, 1);
        this.paint(this.sprite(used), point, toneColor(last.tone, colors), 1 - elapsed / 1500, selected ? 1.8 : 1.15);
        visible.push({ id, ...point });
        used++;
        continue;
      }
      const active = segmentAt(plan, elapsed);
      if (!active) continue;
      const { segment, progress } = active;
      let point: Point;
      let color = colors.particle;
      let alpha = 1;
      let scale = selected ? 1.8 : 1;
      if (segment.kind === "travel" || segment.kind === "bounce") {
        const route = this.route(g, segment.from, segment.to, caller);
        if (!route) continue;
        const eased = segment.kind === "bounce" ? easeOutCubic(progress) : easeInOutSine(progress);
        point = pathPoint(route.path, route.reverse ? 1 - eased : eased);
        if (segment.kind === "bounce" || segment.to.kind === "refused") color = toneColor(segment.tone, colors);
      } else if (segment.kind === "dwell") {
        point = this.point(g, segment.from, 0.3 * easeOutCubic(progress));
        color = progress < 0.55 ? colors.particle : mix(colors.particle, toneColor(segment.tone, colors), (progress - 0.55) / 0.45);
      } else if (segment.kind === "absorb") {
        if (!this.flashed.has(id) && segment.from.kind === "node") {
          this.flashed.add(id);
          this.flash(segment.from.id).velocity += 6;
        }
        point = this.point(g, segment.from, 0.3 + 0.7 * progress);
        color = colors.healthy;
        alpha = 1 - progress;
        scale *= 1 - 0.6 * progress;
      } else if (segment.kind === "stream") {
        if (segment.from.kind === "node") streams.set(segment.from.id, (streams.get(segment.from.id) ?? 0) + 1);
        point = this.point(g, segment.from, 0.3);
        color = colors.healthy;
        alpha = progress > 0.85 ? (1 - progress) / 0.15 : 0.9;
        scale *= 0.8;
      } else {
        point = this.point(g, segment.from, 1);
        const tone = toneColor(segment.tone, colors);
        const size = segment.from.kind === "node" ? (g.deployments.get(segment.from.id)?.r ?? 8) : g.barWidth * 1.5;
        this.bursts.circle(point.x, point.y, size * (1 + 0.9 * easeOutCubic(progress)))
          .stroke({ color: tone, alpha: 0.85 * (1 - progress), width: 2 });
        color = tone;
        alpha = 1 - progress;
      }
      this.paint(this.sprite(used), point, color, alpha, scale);
      visible.push({ id, ...point });
      used++;
    }
    for (let index = used; index < this.sprites.length; index++) {
      const sprite = this.sprites[index];
      if (sprite) sprite.visible = false;
    }
    this.visible = visible;
    if (this.plans.size > livePlans.size + 500)
      for (const id of [...this.plans.keys()]) if (!livePlans.has(id)) { this.plans.delete(id); this.flashed.delete(id); }
  }

  private flash(id: string): Spring {
    let s = this.flashes.get(id);
    if (!s) this.flashes.set(id, (s = spring(0)));
    return s;
  }

  private sprite(index: number): Sprite {
    let sprite = this.sprites[index];
    if (!sprite) {
      sprite = new Sprite(this.texture);
      sprite.anchor.set(0.5);
      this.sprites.push(sprite);
      this.particleLayer.addChild(sprite);
    }
    return sprite;
  }

  private paint(sprite: Sprite, point: Point, color: number, alpha: number, scale: number): void {
    const dark = this.inputs.palette === palettes.dark;
    const size = Math.max(12, this.inputs.layout.rem * 0.95) * scale;
    sprite.visible = alpha > 0.01;
    sprite.position.set(point.x, point.y);
    sprite.width = size;
    sprite.height = size;
    sprite.tint = color;
    sprite.alpha = Math.max(0, Math.min(1, alpha));
    sprite.blendMode = dark ? "add" : "normal";
  }

  /** Dim factor for a scene key under the current emphasis: 1 when emphasised or when nothing is. */
  private emphasis(key: string): number {
    const set = this.inputs.emphasis;
    return !set || set.has(key) ? 1 : 0.28;
  }

  private drawRibbons(g: Geometry, state: FrameState | null, t: number, colors: Colors, streams: Map<string, number>): void {
    const r = this.ribbons;
    const trails = this.trails;
    r.clear();
    trails.clear();
    const dark = this.inputs.palette === palettes.dark;
    const half = g.barWidth / 2;
    const style = {
      handled: { color: colors.flow, alpha: dark ? 0.2 : 0.16 },
      throttled: { color: colors.throttled, alpha: dark ? 0.55 : 0.45 },
      failed: { color: colors.failed, alpha: dark ? 0.55 : 0.45 },
    };
    const hairline = { color: colors.lineStrong, alpha: dark ? 0.55 : 0.75, width: 1 };
    const draw = (a: Point, b: Point, band: Band, dim: number) => {
      const width = bandWidth(band);
      if (width < 0.4) {
        r.moveTo(a.x, a.y);
        bezier(r, a, b);
        r.stroke({ ...hairline, alpha: hairline.alpha * dim });
        return;
      }
      let offset = -width / 2;
      for (const part of ["handled", "throttled", "failed"] as const) {
        const size = band[part];
        if (size < 0.05) continue;
        ribbon(r, a, b, offset, offset + size);
        r.fill({ color: style[part].color, alpha: style[part].alpha * dim });
        offset += size;
      }
    };

    for (const [id, caller] of g.callers)
      draw({ x: caller.x + half, y: caller.y }, { x: g.lb.x - half, y: g.lb.inPorts.get(id) ?? g.lb.y }, caller.band, this.emphasis(`caller:${id}`));
    for (const [key, place] of g.places)
      draw({ x: g.lb.x + half, y: g.lb.ports.get(`place:${key}`) ?? g.lb.y }, { x: place.x, y: place.y }, place.band, this.emphasis(`place:${key}`));
    if (bandWidth(g.refused.band) >= 0.4)
      draw({ x: g.lb.x + half, y: g.lb.ports.get("refused") ?? g.lb.y }, { x: g.refused.x, y: g.refused.y }, g.refused.band, this.emphasis("refused"));
    for (const node of this.inputs.layout.nodes) {
      const item = g.deployments.get(node.id);
      const place = g.places.get(node.place);
      if (!item || !place) continue;
      const a = { x: place.end, y: place.ports.get(node.id) ?? place.y };
      const b = { x: item.x - half, y: item.y };
      const visual = nodeVisual(state?.deployments.get(node.id), state?.throttledSince.get(node.id), t);
      const deployment = state?.deployments.get(node.id)?.deployment;
      const dim = this.emphasis(`dep:${node.id}`) * (deployment && !this.inputs.nodeFilter(deployment) ? 0.35 : 1);
      if (visual.look === "open" || visual.look === "halfOpen") {
        // An open circuit breaks the last link; a half-open one shows its single probe in the gap.
        strokePartial(r, a, b, 0, 0.4, { color: colors.failed, alpha: 0.6 * dim, width: 1.5 });
        strokePartial(r, a, b, 0.6, 1, { color: colors.failed, alpha: 0.6 * dim, width: 1.5 });
        if (visual.look === "halfOpen") {
          const probe = linkPoints(a, b, 2)[0] ?? a;
          const pulse = this.inputs.reducedMotion ? 0.8 : 0.55 + 0.45 * Math.sin(t / 260);
          r.circle(probe.x, probe.y, 3.5).fill({ color: colors.particle, alpha: pulse * dim });
          r.circle(probe.x, probe.y, 7).stroke({ color: colors.particle, alpha: 0.35 * pulse * dim, width: 1 });
        }
      } else draw(a, b, item.band, dim);
      // The bar-to-disc stub.
      r.moveTo(item.x + half, item.y).lineTo(item.disc - item.r, item.y).stroke({ ...hairline, alpha: hairline.alpha * dim });

      const count = streams.get(node.id) ?? 0;
      const path = g.paths.get(node.id);
      if (count > 0 && path && visual.look !== "open") {
        // Streaming responses flow back along the route until they finish.
        trails.moveTo(path.points[0]?.x ?? 0, path.points[0]?.y ?? 0);
        for (const point of path.points.slice(1)) trails.lineTo(point.x, point.y);
        trails.stroke({ color: colors.healthy, alpha: Math.min(0.5, 0.1 + 0.04 * count) * dim, width: 1.25 });
        if (!this.inputs.reducedMotion)
          for (let index = 0; index < Math.min(count, 4); index++) {
            const phase = (t / 1800 + index / Math.min(count, 4)) % 1;
            const point = pathPoint(path, 1 - phase);
            trails.circle(point.x, point.y, 1.7).fill({ color: colors.healthy, alpha: 0.85 * dim * Math.sin(Math.PI * phase) });
          }
      }
    }
  }

  private drawStructure(g: Geometry, state: FrameState | null, t: number, colors: Colors): void {
    const s = this.structure;
    const d = this.discs;
    s.clear();
    d.clear();
    const { reducedMotion, palette } = this.inputs;
    const dark = palette === palettes.dark;
    const w = g.barWidth;
    const bar = (x: number, top: number, bottom: number, dim: number, color = colors.text) => {
      const height = Math.max(w, bottom - top);
      const y = (top + bottom) / 2 - height / 2;
      s.roundRect(x - w / 2, y, w, height, w / 2).fill({ color, alpha: (dark ? 0.82 : 0.78) * dim });
    };
    for (const [id, caller] of g.callers) {
      const width = bandWidth(caller.band);
      bar(caller.x, caller.y - width / 2, caller.y + width / 2, this.emphasis(`caller:${id}`));
    }
    // Places and the refused node are DOM boxes over the canvas; only the LB, callers and deployments have bars.
    bar(g.lb.x, g.lb.top, g.lb.bottom, 1);

    for (const node of this.inputs.layout.nodes) {
      const item = g.deployments.get(node.id);
      if (!item) continue;
      const merged = state?.deployments.get(node.id);
      const visual = nodeVisual(merged, state?.throttledSince.get(node.id), t);
      const width = bandWidth(item.band);
      const filterDim = merged && !this.inputs.nodeFilter(merged.deployment) ? 0.35 : 1;
      const dim = this.emphasis(`dep:${node.id}`) * filterDim;
      bar(item.x, item.y - width / 2, item.y + width / 2, dim);

      const x = item.disc;
      const y = item.y;
      const r = Math.max(0.1, item.r);
      const target = ringColor(visual.look, colors);
      const current = this.colorsByNode.get(node.id) ?? target;
      const color = mix(current, target, reducedMotion ? 1 : 0.12);
      this.colorsByNode.set(node.id, color);
      const faded = visual.look === "open" || visual.look === "halfOpen" ? 0.55 : visual.look === "disabled" || visual.look === "absent" ? 0.5 : 1;
      const alpha = dim * faded;
      const ring = Math.max(1.5, r * 0.2);
      if (visual.look === "degraded") {
        const breath = reducedMotion ? 0.5 : 0.5 + 0.5 * Math.sin(t / 380);
        d.circle(x, y, r * (1.45 + 0.2 * breath)).fill({ color: colors.degraded, alpha: (0.1 + 0.16 * breath) * dim });
      }
      d.circle(x, y, r).fill({ color: colors.bg, alpha: 1 });
      d.circle(x, y, r).fill({ color, alpha: 0.22 * alpha });
      if (visual.look === "throttled") {
        d.circle(x, y, r - ring / 2).stroke({ color: colors.throttled, alpha: 0.25 * alpha, width: ring });
        // Reduced motion drains in tenths instead of continuously.
        const remaining = visual.cooldown === null ? 1 : reducedMotion ? Math.ceil(visual.cooldown * 10) / 10 : visual.cooldown;
        if (remaining > 0.002)
          d.moveTo(x, y - (r - ring / 2)).arc(x, y, r - ring / 2, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * remaining)
            .stroke({ color: colors.throttled, alpha, width: ring * 1.2, cap: "round" });
      } else if (visual.look === "disabled") {
        d.circle(x, y, r - ring / 2).stroke({ color: colors.disabled, alpha: 0.8 * dim, width: ring * 0.7 });
        d.moveTo(x - r * 0.55, y + r * 0.55).lineTo(x + r * 0.55, y - r * 0.55).stroke({ color: colors.disabled, alpha: 0.8 * dim, width: ring * 0.7 });
      } else {
        d.circle(x, y, r - ring / 2).stroke({ color, alpha: alpha * (visual.look === "absent" ? 0.4 : 1), width: ring });
      }
      const flash = this.flashes.get(node.id);
      if (flash) {
        if (reducedMotion) snapSpring(flash, 0);
        else stepSpring(flash, this.deltaMs, { stiffness: 90, damping: 14 });
        if (flash.value > 0.02)
          d.circle(x, y, r * (1 + 0.35 * Math.min(1, flash.value))).stroke({ color: colors.healthy, alpha: Math.min(0.5, flash.value * 0.5) * dim, width: 1.25 });
      }
    }
  }

  private drawHighlight(g: Geometry, colors: Colors): void {
    const h = this.highlight;
    h.clear();
    const id = this.inputs.selectedId;
    if (!id) return;
    const item = this.timeline.requests.find((entry) => entry.request.id === id);
    if (!item) return;
    // The selected request's chain: caller to LB, then every attempted deployment, failed hops in their colour.
    const caller = item.request.caller ?? "unknown";
    const stroke = (path: Path | undefined, color: number) => {
      const first = path?.points[0];
      if (!path || !first) return;
      h.moveTo(first.x, first.y);
      for (const point of path.points.slice(1)) h.lineTo(point.x, point.y);
      h.stroke({ color, alpha: 0.9, width: 2, join: "round" });
    };
    stroke(g.callerPaths.get(caller), colors.text);
    if (item.request.attempts.length === 0) stroke(g.refusedPath, colors.failed);
    item.request.attempts.forEach((attempt, index) => {
      const last = index === item.request.attempts.length - 1;
      const color = last && item.request.status < 400 ? colors.healthy : attempt.status === 429 ? colors.throttled : colors.failed;
      stroke(g.paths.get(attempt.deploymentId ?? attempt.deployment), color);
    });
  }

  private moveLabels(g: Geometry): void {
    for (const [key, element] of this.labels) {
      let point: Point | null = null;
      let extent = 0;
      if (key.startsWith("dep:")) {
        const node = g.deployments.get(key.slice(4));
        if (node) point = { x: node.disc + node.r, y: node.y };
      } else if (key.startsWith("caller:")) {
        const caller = g.callers.get(key.slice(7));
        if (caller) point = { x: caller.x - g.barWidth / 2, y: caller.y };
      } else if (key.startsWith("place:")) {
        const place = g.places.get(key.slice(6));
        if (place) {
          point = { x: place.x, y: place.y };
          extent = place.bottom - place.top;
        }
      } else if (key === "refused") {
        point = { x: g.refused.x, y: g.refused.y };
        extent = bandWidth(g.refused.band);
      } else if (key.startsWith("column:")) {
        const column = key.slice(7);
        const x = column === "callers" ? g.callers.values().next().value?.x : column === "lb" ? g.lb.x
          : column === "places" ? g.places.values().next().value?.x
          : column === "deployments" ? g.deployments.values().next().value?.disc : undefined;
        if (x !== undefined) point = { x, y: 0 };
      }
      if (!point) continue;
      element.style.transform = `translate(${point.x}px, ${point.y}px)`;
      if (extent) element.style.setProperty("--extent", `${extent}px`);
    }
  }

  private pick(event: PointerEvent): string | null {
    const rect = this.app.canvas.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    let best: string | null = null;
    let distance = 16 * 16;
    for (const particle of this.visible) {
      const d = (particle.x - x) ** 2 + (particle.y - y) ** 2;
      if (d < distance) {
        distance = d;
        best = particle.id;
      }
    }
    return best;
  }
}

type Colors = Record<keyof Palette, number>;

function paletteNumbers(palette: Palette): Colors {
  return Object.fromEntries(Object.entries(palette).map(([key, value]) => [key, hexToNumber(value)])) as Colors;
}

function toneColor(tone: ParticleTone, colors: Colors): number {
  return tone === "ok" ? colors.healthy : tone === "throttled" ? colors.throttled : tone === "failed" ? colors.failed
    : tone === "rejected" ? colors.muted : colors.particle;
}

function ringColor(look: NodeLook, colors: Colors): number {
  switch (look) {
    case "healthy": return colors.healthy;
    case "throttled": return colors.throttled;
    case "degraded": return colors.degraded;
    case "open":
    case "halfOpen": return colors.failed;
    default: return colors.disabled;
  }
}

function bezier(g: Graphics, a: Point, b: Point): void {
  const dx = (b.x - a.x) * 0.5;
  g.bezierCurveTo(a.x + dx, a.y, b.x - dx, b.y, b.x, b.y);
}

/** A band between two horizontal-tangent curves, offset `from`..`to` pixels across the link. */
function ribbon(g: Graphics, a: Point, b: Point, from: number, to: number): void {
  const dx = (b.x - a.x) * 0.5;
  g.moveTo(a.x, a.y + from);
  g.bezierCurveTo(a.x + dx, a.y + from, b.x - dx, b.y + from, b.x, b.y + from);
  g.lineTo(b.x, b.y + to);
  g.bezierCurveTo(b.x - dx, b.y + to, a.x + dx, a.y + to, a.x, a.y + to);
  g.closePath();
}

function strokePartial(g: Graphics, a: Point, b: Point, from: number, to: number, style: { color: number; alpha: number; width: number }): void {
  const points = [a, ...linkPoints(a, b, 24)];
  const start = Math.round(from * 24);
  const end = Math.round(to * 24);
  const first = points[start];
  if (!first) return;
  g.moveTo(first.x, first.y);
  for (let index = start + 1; index <= end; index++) {
    const point = points[index];
    if (point) g.lineTo(point.x, point.y);
  }
  g.stroke({ ...style, cap: "round" });
}

function easeInOutSine(t: number): number {
  return -(Math.cos(Math.PI * t) - 1) / 2;
}

function easeOutCubic(t: number): number {
  return 1 - (1 - t) ** 3;
}

/** A soft round light: bright core, falloff to transparent, tinted per particle. */
function dotTexture(): Texture {
  const size = 64;
  const canvas = document.createElement("canvas");
  canvas.width = size;
  canvas.height = size;
  const context = canvas.getContext("2d");
  if (context) {
    const gradient = context.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
    gradient.addColorStop(0, "rgba(255,255,255,1)");
    gradient.addColorStop(0.22, "rgba(255,255,255,0.95)");
    gradient.addColorStop(0.32, "rgba(255,255,255,0.35)");
    gradient.addColorStop(1, "rgba(255,255,255,0)");
    context.fillStyle = gradient;
    context.fillRect(0, 0, size, size);
  }
  return Texture.from(canvas);
}
