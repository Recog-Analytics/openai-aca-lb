import { describe, expect, test } from "bun:test";
import { DemoStream } from "./demo";
import { computeFunnel } from "./funnel";
import { IDLE_MODELS, labelHeight, layoutScene, type LayoutInput, type SceneLayout, type View } from "./layout";
import { poolLabels, regionName } from "./names";
import { productionInventory } from "./inventory";
import type { DashboardDeployment } from "./types";

const production: DashboardDeployment[] = productionInventory.map((item) => ({
  id: item.id, account: item.account, deployment: item.deployment, region: item.region, modelKey: item.model, tier: item.tier,
  zone: item.zone, weight: item.weight, state: "Healthy", p95TtfbMs: null, accountOpen: false,
}));
const callers = ["portal", "agents", "smoke-test", "translator"];
const labels = poolLabels(production);

/** Root font size the stylesheet gives each viewport width: clamp(14px, 0.5vw + 7px, 30px). */
const remFor = (width: number) => Math.min(30, Math.max(14, width * 0.005 + 7));

interface Box { name: string; left: number; right: number; top: number; bottom: number }

/** Rough Geist advance: 0.56 em per character, plus a share like "12.3 %". */
const textWidth = (text: string, fontRem: number, rem: number) => text.length * 0.56 * fontRem * rem;

/**
 * Every label the scene draws, as a box, from the layout and the label metrics the stylesheet uses. A deployment label
 * is centred on its node and as tall as its density; a group label fills its box; callers sit left of their bars.
 */
function labelBoxes(layout: SceneLayout, deployments: DashboardDeployment[]): Box[] {
  const { rem, columns } = layout;
  const byId = new Map(deployments.map((item) => [item.id, item]));
  const boxes: Box[] = [];
  for (const node of layout.nodes) {
    const height = labelHeight[node.density] * rem;
    const deployment = byId.get(node.id);
    const name = node.kind === "idle" ? "14 quiet 0.4 %" : node.kind === "fold" ? "21 deployments down"
      : `${layout.view === "region" ? labels.get(node.id) ?? "" : regionName(deployment?.region ?? "")}${node.density === "one" ? "  100 %" : ""}`;
    // Taller densities put the share and the status on a line of their own.
    const lines = node.density === "one" ? [name] : [name, "100 %  Open, probe in 81 s"];
    const left = node.x + node.r + 0.65 * rem;
    const right = left + Math.max(...lines.map((line) => textWidth(line, node.density === "one" ? 0.8 : 0.92, rem)));
    boxes.push({ name: `node ${node.id}`, left, right, top: node.y - height / 2, bottom: node.y + height / 2 });
  }
  for (const place of layout.places)
    boxes.push({ name: `group ${place.key}`, left: columns.places, right: columns.placesEnd, top: place.top, bottom: place.bottom });
  for (const caller of layout.callers)
    boxes.push({ name: `caller ${caller.id}`, left: columns.callers - 0.7 * rem - textWidth(caller.id, 0.92, rem), right: columns.callers - 0.7 * rem,
      top: caller.y - 1.6 * rem, bottom: caller.y + 1.6 * rem });
  boxes.push({ name: "refused", left: layout.refused.x, right: layout.refused.x + 11 * rem, top: layout.refused.y - 1.45 * rem, bottom: layout.refused.y + 1.45 * rem });
  return boxes;
}

function overlaps(boxes: Box[]): string[] {
  const hits: string[] = [];
  for (let i = 0; i < boxes.length; i++)
    for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i]!;
      const b = boxes[j]!;
      if (a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5) hits.push(`${a.name} × ${b.name}`);
    }
  return hits;
}

const ids = (filter: (item: DashboardDeployment) => boolean) => new Set(production.filter(filter).map((item) => item.id));
// Realistic: what the dashboard folds for the production demo's traffic once it settles. The App folds a healthy
// deployment under 0.5 % of the window's requests and unfolds it only above 1 %, so in steady state the line is 1 %.
const snapshot = new DemoStream("production", Date.parse("2026-10-05T12:00:00Z")).snapshot();
const funnel = computeFunnel((snapshot.routeHistory ?? []).slice(-30).map((tick) => ({ at: Date.parse(tick.at), routes: tick.routes })), production,
  { model: "", zone: "", caller: "" });
const realisticIdle = ids((item) => (funnel.deployments.get(item.id)?.reached ?? 0) / funnel.total < 0.01);
const swedenDown = new Map([...ids((item) => item.account === "oai-swedencentral")].map((id) => [id, "open"]));
const scattered = new Map(production.filter((_, index) => index % 9 === 0).map((item) => [item.id, "throttled"]));
const sizes: [number, number][] = [[1280, 720], [1600, 900], [1920, 1080], [3840, 2160]];
const cases: [string, Partial<LayoutInput>][] = [
  ["nothing idle (71 lanes)", {}],
  ["everything idle", { idle: new Set(production.map((item) => item.id)) }],
  ["realistic traffic", { idle: realisticIdle }],
  ["Sweden down", { idle: realisticIdle, problems: swedenDown }],
  ["scattered throttling", { idle: realisticIdle, problems: scattered }],
  ["every group expanded", { idle: realisticIdle, problems: swedenDown,
    expanded: new Set([...production.map((item) => item.region), "global", ...labels.values(), IDLE_MODELS]) }],
];

describe("layout at production scale", () => {
  for (const view of ["region", "model"] as View[])
    for (const [width, height] of sizes)
      for (const [name, input] of cases)
        test(`${view} view, ${width}×${height}, ${name}: no two labels overlap and every label fits its lane`, () => {
          const rem = remFor(width);
          const layout = layoutScene({ deployments: production, callers, size: { width, height }, rem, view, ...input });
          // Every deployment is drawn exactly once, by its own lane or by the lane that folds it.
          expect(new Set(production.map((item) => layout.nodeOf.get(item.id))).has(undefined)).toBe(false);
          for (const node of layout.nodes) {
            expect(labelHeight[node.density] * rem).toBeLessThanOrEqual(node.bottom - node.top + 0.01);
            expect(node.top).toBeGreaterThanOrEqual(layout.top - 0.01);
            expect(node.bottom).toBeLessThanOrEqual(layout.contentHeight + 0.01);
          }
          expect(overlaps(labelBoxes(layout, production))).toEqual([]);
          // Labels stay inside the scene horizontally.
          for (const box of labelBoxes(layout, production)) {
            expect(box.left).toBeGreaterThanOrEqual(0);
            expect(box.right).toBeLessThanOrEqual(width + 0.5);
          }
        });

  // 1184×636 is the scene's box in a 1600×900 window, measured in Chrome.
  test("realistic production traffic fits 1600×900 in the region view without scrolling", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1184, height: 636 }, rem: 15, view: "region", idle: realisticIdle });
    expect(layout.contentHeight).toBe(636);
    expect(layout.density).toBe("one");
  });

  test("71 busy deployments that cannot fit grow the scene, which scrolls, instead of squeezing labels", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1600, height: 560 }, rem: 15 });
    expect(layout.contentHeight).toBeGreaterThan(560);
    expect(layout.laneHeight).toBe(labelHeight.one * 15);
  });

  test("idle deployments fold into one lane per group; an expanded group lists them and keeps the way back", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, idle: realisticIdle });
    const sweden = layout.places.find((place) => place.key === "swedencentral")!;
    const idleLane = layout.nodes.find((node) => node.id === "idle:swedencentral")!;
    expect(sweden.idle).toBe(idleLane.members.length);
    expect(idleLane.members.every((id) => realisticIdle.has(id))).toBe(true);
    const open = layoutScene({ deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, idle: realisticIdle, expanded: new Set(["swedencentral"]) });
    expect(open.nodes.filter((node) => node.place === "swedencentral" && node.kind === "deployment")).toHaveLength(19);
    expect(open.nodes.find((node) => node.id === "idle:swedencentral")?.members).toEqual([]);
  });

  test("an account outage is one red lane, never folded into idle, and its group is not compact", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, idle: realisticIdle, problems: swedenDown });
    const fold = layout.nodes.find((node) => node.id === "fold:swedencentral:open")!;
    // Sweden's account holds 19 regional deployments; its two Global audio deployments sit in the Global group.
    expect(fold.members).toHaveLength(19);
    expect(fold.density).toBe("two");
    expect(layout.nodes.find((node) => node.id === "idle:swedencentral")).toBeUndefined();
    expect(layout.places.find((place) => place.key === "swedencentral")?.compact).toBe(false);
  });

  test("the model view gathers unused models into one group", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, view: "model", idle: realisticIdle });
    const unused = layout.places.find((place) => place.key === IDLE_MODELS)!;
    expect(unused.label).toMatch(/^\d+ idle models$/);
    expect(layout.places.some((place) => place.key === "gpt-4o-mini · public")).toBe(true);
  });

  test("expanded idle models list their models and keep a lane that folds them back", () => {
    const input = { deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, view: "model" as const, idle: realisticIdle };
    const folded = layoutScene(input);
    const open = layoutScene({ ...input, expanded: new Set([IDLE_MODELS]) });
    const back = open.places.find((place) => place.key === IDLE_MODELS)!;
    expect(back.expanded).toBe(true);
    expect(back.idle).toBe(Number(folded.places.find((place) => place.key === IDLE_MODELS)!.label.split(" ")[0]));
    expect(open.nodes.find((node) => node.id === `idle:${IDLE_MODELS}`)?.members).toEqual([]);
    expect(open.places.length).toBeGreaterThan(folded.places.length);
  });

  test("positions depend on structure only, so traffic never moves a node", () => {
    const input = { deployments: production, callers, size: { width: 1600, height: 900 }, rem: 15, idle: realisticIdle };
    expect(layoutScene(input)).toEqual(layoutScene(input));
  });

  test("reads left to right with the refused box in view", () => {
    const layout = layoutScene({ deployments: production, callers, size: { width: 1600, height: 560 }, rem: 15, idle: realisticIdle });
    const c = layout.columns;
    expect(c.callers).toBeLessThan(c.lb);
    expect(c.lb).toBeLessThan(c.places);
    expect(c.placesEnd).toBeLessThan(c.bars);
    expect(c.bars).toBeLessThan(c.discs);
    expect(layout.refused.y + 1.45 * 15).toBeLessThanOrEqual(560);
  });
});
