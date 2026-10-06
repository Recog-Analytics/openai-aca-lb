import { describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { palettes } from "../theme";
import { PlaybackClock } from "./clock";
import { HistoryCache, isRange } from "./history";
import { worstMember } from "./state";
import { bytesLabel, operationName, poolLabels, regionName, replicaLabel } from "./names";
import { stripBins } from "./strip";
import { Timeline } from "./timeline";
import type { DashboardDeployment, DashboardRoute, HistoryResponse } from "./types";

const all = { model: "", zone: "", caller: "" };

describe("human names", () => {
  test("every region the production inventory uses has its display name, and unknown ones are made readable", () => {
    expect(["francecentral", "germanywestcentral", "polandcentral", "swedencentral", "westeurope"].map(regionName))
      .toEqual(["France Central", "Germany West Central", "Poland Central", "Sweden Central", "West Europe"]);
    expect(regionName("eastus2")).toBe("East US 2");
    expect(regionName("newlandnorth")).toBe("Newland North");
    expect(regionName("atlantis")).toBe("Atlantis");
  });

  test("two pools of one model read as the model plus a readable suffix; one pool is just the model", () => {
    const d = (id: string, deployment: string, modelKey: string) => ({ id, deployment, modelKey });
    const labels = poolLabels([
      d("a", "llm-gpt-4omini", "gpt-4o-mini@2024-07-18"), d("b", "llm-gpt-4omini-public", "gpt-4o-mini@2024-07-18"),
      d("c", "llm-gpt-4_1mini", "gpt-4.1-mini@2025-04-14"), d("e", "x-gpt-4o", "gpt-4o@2024-08-06"), d("f", "y-gpt-4o", "gpt-4o@2024-11-20"),
    ]);
    expect(labels.get("a")).toBe("gpt-4o-mini");
    expect(labels.get("b")).toBe("gpt-4o-mini · public");
    expect(labels.get("c")).toBe("gpt-4.1-mini");
    expect(labels.get("f")).toBe("gpt-4o · 2024-11-20");
  });

  test("replicas are numbered by the service, else in first-seen order", () => {
    const replicas = ["ca-lb--rev1-6b8f9c7d5-d2cmn", "ca-lb--rev1-6b8f9c7d5-x9k2p"];
    expect(replicaLabel(replicas[1]!, replicas)).toBe("Replica 2");
    expect(replicaLabel(replicas[0]!, replicas, { [replicas[0]!]: 3 })).toBe("Replica 3");
  });

  test("operations and sizes read in words", () => {
    expect(operationName("chat.completions")).toBe("Chat completion");
    expect(operationName("audio.transcriptions")).toBe("Audio transcription");
    expect(operationName(null)).toBeNull();
    expect(bytesLabel(812)).toBe("812 B");
    expect(bytesLabel(12_400)).toBe("12 KB");
    expect(bytesLabel(3_400_000)).toBe("3.4 MB");
  });
});

describe("brand palette", () => {
  const css = readFileSync(new URL("../styles.css", import.meta.url), "utf8");
  const block = (start: string) => css.slice(css.indexOf(start), css.indexOf("}", css.indexOf(start)));
  const vars = (text: string) => Object.fromEntries([...text.matchAll(/--([a-z-]+):\s*(#[0-9a-f]{6})/g)].map((match) => [match[1], match[2]]));
  const kebab = (name: string) => name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);

  test("the stylesheet's colour defaults equal theme.ts in both themes, so DOM and canvas never drift", () => {
    const dark = vars(block(":root {"));
    const light = vars(block(':root:not([data-theme="dark"])'));
    for (const [name, value] of Object.entries(palettes.dark)) expect([name, dark[kebab(name)]]).toEqual([name, value]);
    for (const [name, value] of Object.entries(palettes.light)) expect([name, light[kebab(name)]]).toEqual([name, value]);
  });

  const luminance = (hex: string) => {
    const [r, g, b] = [1, 3, 5].map((index) => Number.parseInt(hex.slice(index, index + 2), 16) / 255)
      .map((value) => (value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
  };
  const contrast = (a: string, b: string) => {
    const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (high! + 0.05) / (low! + 0.05);
  };

  test("every text colour meets WCAG AA (4.5:1) on every surface it is set on, in both themes", () => {
    for (const palette of [palettes.dark, palettes.light])
      for (const text of [palette.text, palette.muted, palette.healthy, palette.throttled, palette.degraded, palette.failed])
        for (const surface of [palette.bg, palette.surface, palette.raised])
          expect(contrast(text, surface)).toBeGreaterThanOrEqual(4.5);
  });

  test("the brand primary is the throttled colour in light mode and stays the brand mark in both", () => {
    expect(palettes.light.throttled).toBe("#2563eb");
    expect(palettes.dark.brand).toBe("#2563eb");
  });
});

const route = (status: number, hops: number, count: number): DashboardRoute => ({
  caller: "portal", modelKey: "gpt-4.1-mini@2025-04-14", zone: "eu", status, count,
  hops: Array.from({ length: hops }, (_, index) => ({ deploymentId: `d${index}`, outcome: index < hops - 1 ? "Throttled" : "Success" })),
});
const deployment: DashboardDeployment = { id: "d0", account: "oai-swedencentral", deployment: "llm-gpt-4_1mini", region: "swedencentral",
  modelKey: "gpt-4.1-mini@2025-04-14", tier: 1, zone: "eu", weight: 16000, state: "Healthy", p95TtfbMs: 300, accountOpen: false };
const t0 = Date.parse("2026-10-05T12:00:00Z");

function response(): HistoryResponse {
  return {
    from: new Date(t0 - 3_600_000).toISOString(), to: new Date(t0).toISOString(), resolution: 600,
    buckets: [
      { at: new Date(t0 - 3_000_000).toISOString(), seconds: 600, routes: [route(200, 1, 500), route(200, 2, 20)], states: [] },
      { at: new Date(t0 - 2_400_000).toISOString(), seconds: 600, routes: [route(200, 1, 400), route(429, 2, 30)],
        states: [{ deploymentId: "d0", state: "Throttled", seconds: 240, accountOpen: false, halfOpen: false, p95TtfbMs: 900 }] },
    ],
    requests: [{ id: "r1", startedAt: new Date(t0 - 2_500_000).toISOString(), caller: "portal", requestedModel: "gpt-4.1-mini",
      modelKey: "gpt-4.1-mini@2025-04-14", zone: "eu", streaming: false, status: 429, durationMs: 300, attempts: [], outcome: "error" }],
    deployments: [{ deployment, replicas: [{ replica: "r", state: "Healthy", p95TtfbMs: 300, accountOpen: false }] }],
  };
}

describe("history", () => {
  test("buckets act as long ticks, so the funnel counts their seconds and requests exactly", () => {
    const history = new HistoryCache(response());
    const ticks = history.ticksBetween(t0 - 3_600_000, t0);
    expect(ticks.map((tick) => tick.seconds)).toEqual([600, 600]);
    expect(ticks.flatMap((tick) => tick.routes).reduce((sum, item) => sum + item.count, 0)).toBe(950);
    expect(history.ticksBetween(t0 - 3_600_000, t0, t0 - 2_700_000)).toHaveLength(1);
  });

  test("buckets hand over to live ticks at the last whole bucket, so the seam neither drops nor double counts", () => {
    const history = new HistoryCache(response());
    const seam = history.handoff();
    expect(seam).toBe(t0 - 2_400_000);
    const live = [{ at: t0 - 2_400_000, routes: [route(200, 1, 7)] }, { at: t0 - 2_399_000, routes: [route(200, 1, 3)] }];
    const counted = [...history.ticksBetween(t0 - 3_600_000, t0, seam), ...live.filter((tick) => tick.at > seam)]
      .flatMap((tick) => tick.routes).reduce((sum, item) => sum + item.count, 0);
    // Both buckets whole (950) plus only the live second after the seam (3); the live second at the seam is in the bucket.
    expect(counted).toBe(953);
  });

  test("a fine tail fills the stretch after the last whole coarse bucket, without double counting", () => {
    const main = response();
    // The main fetch ends 5 min after its last whole bucket; its partial bucket (at t0) must not count.
    main.to = new Date(t0 - 2_100_000).toISOString();
    main.buckets.push({ at: new Date(t0 - 1_800_000).toISOString(), seconds: 300, routes: [route(200, 1, 999)], states: [] });
    const tail: HistoryResponse = { ...response(), from: new Date(t0 - 2_400_000).toISOString(), to: new Date(t0 - 2_100_000).toISOString(), resolution: 10,
      buckets: [{ at: new Date(t0 - 2_390_000).toISOString(), seconds: 10, routes: [route(200, 1, 4)], states: [] },
        { at: new Date(t0 - 2_100_000).toISOString(), seconds: 10, routes: [route(200, 1, 6)], states: [] }] };
    const history = new HistoryCache(main, tail);
    const counted = history.ticks.flatMap((tick) => tick.routes).reduce((sum, item) => sum + item.count, 0);
    expect(counted).toBe(950 + 4 + 6);
    expect(history.handoff()).toBe(t0 - 2_100_000);
    expect(history.requests).toHaveLength(1);
  });

  test("the frame at a past time shows each deployment's worst state of that bucket, healthy otherwise", () => {
    const history = new HistoryCache(response());
    expect(history.frameAt(t0 - 2_600_000, ["r"])?.deployments.get("d0")?.deployment.state).toBe("Throttled");
    expect(history.frameAt(t0 - 3_100_000, ["r"])?.deployments.get("d0")?.deployment.state).toBe("Healthy");
    expect(history.frameAt(t0 - 7_200_000, ["r"])).toBeNull();
    expect(history.requestsBetween(t0 - 3_600_000, t0).map((item) => item.request.id)).toEqual(["r1"]);
  });

  test("the strip shows exact rates per bar, marks problem bars and leaves unretained time empty", () => {
    const history = new HistoryCache(response());
    const bins = stripBins({ range: "1h", from: t0 - 3_600_000, to: t0, ticks: history.ticks, history, summary: [], scope: all });
    expect(bins).toHaveLength(120);
    const filled = bins.filter((bin) => bin.seconds > 0);
    expect(filled).toHaveLength(2);
    expect(filled[0]).toMatchObject({ direct: 500, retried: 20, failed: 0, worst: null });
    expect(filled[1]).toMatchObject({ direct: 400, failed: 30, worst: "Throttled", unhealthy: 1 });
  });

  test("the day summary fills a 24-hour strip before the detail arrives, but never under a filter", () => {
    const summary = Array.from({ length: 20 }, (_, index) => ({
      at: new Date(t0 - (20 - index) * 60_000).toISOString(), total: 100, served: 98, retried: 3, failed: 2, refused: 0,
      worst: index === 5 ? "Open" as const : null, unhealthy: index === 5 ? 3 : 0,
    }));
    const bins = stripBins({ range: "24h", from: t0 - 86_400_000, to: t0, ticks: [], history: null, summary, scope: all });
    const filled = bins.filter((bin) => bin.seconds > 0);
    expect(filled.reduce((sum, bin) => sum + bin.direct + bin.retried + bin.failed, 0)).toBe(2000);
    expect(filled.some((bin) => bin.worst === "Open" && bin.unhealthy === 3)).toBe(true);
    const scoped = stripBins({ range: "24h", from: t0 - 86_400_000, to: t0, ticks: [], history: null, summary, scope: { ...all, caller: "x" } });
    expect(scoped.every((bin) => bin.seconds === 0)).toBe(true);
  });

  test("a minute summary never marks a short bar or a bar the frames already cover", () => {
    const open = { at: new Date(t0).toISOString(), total: 10, served: 10, retried: 0, failed: 0, refused: 0, worst: "Open" as const, unhealthy: 2 };
    const short = stripBins({ range: "2m", from: t0 - 120_000, to: t0 + 1000, ticks: [], history: null, summary: [open], scope: all });
    expect(short.every((bin) => bin.worst === null)).toBe(true);
    const healthy = { deployment, replicas: [] };
    const day = stripBins({ range: "24h", from: t0 - 86_400_000, to: t0 + 600_000, ticks: [], history: null, summary: [open],
      frames: [{ at: t0 - 1000, deployments: new Map([["d0", healthy]]) }], scope: all });
    expect(day.every((bin) => bin.worst === null)).toBe(true);
  });

  test("live frames mark an ongoing problem in every bar it spans, before any minute closes", () => {
    const throttled = { deployment: { ...deployment, state: "Throttled" as const }, replicas: [] };
    const frames = Array.from({ length: 10 }, (_, index) => ({ at: t0 - 20_000 + index * 1000, deployments: new Map([["d0", throttled]]) }));
    const bins = stripBins({ range: "2m", from: t0 - 120_000, to: t0, ticks: [], history: null, summary: [], frames, scope: all });
    const marked = bins.filter((bin) => bin.worst === "Throttled");
    // Bars cover (start, end], so ten frames starting on a bar edge touch six bars.
    expect(marked.length).toBe(6);
    expect(marked.every((bin) => bin.unhealthy === 1)).toBe(true);
  });

  test("only real ranges pass from the URL; inherited names fall back", () => {
    expect(["2m", "15m", "1h", "24h"].every(isRange)).toBe(true);
    expect(isRange("constructor")).toBe(false);
    expect(isRange("__proto__")).toBe(false);
  });

  test("a folded lane speaks for its worst current member, not its first", () => {
    const merged = (id: string, state: "Healthy" | "Open") => ({ deployment: { ...deployment, id, state }, replicas: [] });
    const frame = { at: t0, replicas: [], rates: new Map(), throttledSince: new Map(),
      deployments: new Map([["a", merged("a", "Healthy")], ["b", merged("b", "Open")]]) };
    expect(worstMember(["a", "b"], frame, t0)?.id).toBe("b");
  });

  test("a long range lets the playhead reach the retained past and holds it there", () => {
    const timeline = new Timeline();
    timeline.apply("snapshot", { at: new Date(t0).toISOString(), replicas: [], deployments: [], requests: [], counts: [] }, t0);
    const clock = new PlaybackClock(timeline);
    clock.setRange(3_600_000, t0 - 3_600_000);
    const { from } = clock.bounds(t0);
    expect(from).toBeLessThanOrEqual(t0 - 3_500_000);
    clock.seek(t0 - 1_800_000, t0);
    expect(clock.mode).toBe("paused");
    expect(clock.time(t0 + 10_000)).toBe(t0 - 1_800_000);
    clock.goLive();
    expect(clock.mode).toBe("live");
  });

  test("the timeline keeps the day summary, appends closed minutes and the replica numbers", () => {
    const timeline = new Timeline();
    const bucket = (minutes: number) => ({ at: new Date(t0 - minutes * 60_000).toISOString(), total: 1, served: 1, retried: 0, failed: 0, refused: 0, worst: null, unhealthy: 0 });
    timeline.apply("snapshot", { at: new Date(t0).toISOString(), replicas: ["a"], deployments: [], requests: [], counts: [], summary: [bucket(2), bucket(1)],
      retention: { secondsFrom: new Date(t0 - 3_600_000).toISOString(), minutesFrom: new Date(t0 - 86_400_000).toISOString() }, replicaNumbers: { a: 1 } }, t0);
    timeline.apply("delta", { at: new Date(t0 + 60_000).toISOString(), replicas: ["a"], deployments: [], requests: [], counts: [], summary: [bucket(0)] }, t0 + 60_000);
    expect(timeline.summary).toHaveLength(3);
    expect(timeline.retention?.seconds).toBe(t0 - 3_600_000);
    expect(timeline.replicaNumbers).toEqual({ a: 1 });
  });
});
