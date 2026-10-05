import { describe, expect, test } from "bun:test";
import { PlaybackClock } from "./clock";
import { DemoStream, scenarios } from "./demo";
import { findIssues, percent, verdict } from "./attention";
import { computeFunnel, preferredTier, quotaShare, splitPaths } from "./funnel";
import { RunGrouper } from "./runs";
import { niceCeiling } from "../ui/Scrubber";
import { layoutScene, linkPoint, makePath, pathPoint, stackPorts } from "./layout";
import { planRequest, segmentAt, selectForPlayback, timing } from "./playback";
import { snapSpring, spring, springs, stepSpring } from "./spring";
import { attemptTone, chainLabel, nodeVisual, outcomeMix, regionShort, requestTone } from "./state";
import { PLAYBACK_LAG_MS, Timeline, type TimedRequest } from "./timeline";
import type { DashboardDeployment, DashboardFrame, DashboardRoute, MergedDeployment, RequestAttempt, RequestRecord } from "./types";

const t0 = Date.parse("2026-10-04T10:00:00Z");

function deployment(id: string, patch: Partial<DashboardDeployment> = {}): MergedDeployment {
  return {
    deployment: {
      id, account: "acct", deployment: id, region: "swedencentral", modelKey: "gpt-4o@1", tier: 1, zone: "eu",
      weight: 100, state: "Healthy", p95TtfbMs: 300, accountOpen: false, ...patch,
    },
    replicas: [{ replica: "r1", state: patch.state ?? "Healthy", p95TtfbMs: 300, accountOpen: false }],
  };
}

function attempt(patch: Partial<RequestAttempt> = {}): RequestAttempt {
  return { deployment: "d", account: "acct", region: "swedencentral", tier: 1, status: 200, ttfbMs: 300,
    healthOutcome: "Success", retryReason: null, deploymentId: "d", ...patch };
}

function request(id: string, completedAt: number, patch: Partial<RequestRecord> = {}): RequestRecord {
  return { id, startedAt: new Date(completedAt - 1000).toISOString(), caller: "orchestrator", requestedModel: "gpt-4o",
    modelKey: "gpt-4o@1", zone: "eu", streaming: false, status: 200, durationMs: 1000, attempts: [attempt()], outcome: "success", ...patch };
}

function frame(at: number, patch: Partial<DashboardFrame> = {}): DashboardFrame {
  return { at: new Date(at).toISOString(), replicas: ["r1"], deployments: [deployment("d")], requests: [], counts: [], ...patch };
}

describe("timeline", () => {
  test("snapshot history plays at completion; a late delta request joins the playhead within a second", () => {
    const timeline = new Timeline();
    timeline.apply("snapshot", frame(t0, { requests: [request("old", t0 - 60_000)] }), t0 - 100);
    timeline.apply("delta", frame(t0 + 1000, { requests: [request("late", t0 - 10_000), request("fresh", t0 + 500)] }), t0 + 900);
    const byId = new Map(timeline.requests.map((item) => [item.request.id, item]));
    expect(byId.get("old")?.playAt).toBe(t0 - 60_000);
    expect(byId.get("fresh")?.playAt).toBe(t0 + 500);
    const late = byId.get("late")!.playAt;
    expect(late).toBeGreaterThanOrEqual(t0 + 1000 - PLAYBACK_LAG_MS);
    expect(late).toBeLessThan(t0 + 2000 - PLAYBACK_LAG_MS);
    expect(timeline.clockOffset).toBe(100);
    expect(timeline.requests.map((item) => item.playAt)).toEqual([...timeline.requests.map((item) => item.playAt)].sort((a, b) => a - b));
  });

  test("a reconnect snapshot does not duplicate requests", () => {
    const timeline = new Timeline();
    timeline.apply("snapshot", frame(t0, { requests: [request("a", t0)] }), t0);
    timeline.apply("snapshot", frame(t0 + 1000, { requests: [request("a", t0), request("b", t0 + 500)] }), t0 + 1000);
    expect(timeline.requests.map((item) => item.request.id)).toEqual(["a", "b"]);
  });

  test("stateAt returns the frame in effect and tracks the start of a throttle run", () => {
    const timeline = new Timeline();
    const throttled = [deployment("d", { state: "Throttled", throttledUntil: new Date(t0 + 8000).toISOString() })];
    timeline.apply("snapshot", frame(t0), t0);
    timeline.apply("delta", frame(t0 + 1000, { deployments: throttled }), t0 + 1000);
    timeline.apply("delta", frame(t0 + 2000, { deployments: throttled }), t0 + 2000);
    expect(timeline.stateAt(t0 + 500)?.at).toBe(t0);
    expect(timeline.stateAt(t0 + 2500)?.throttledSince.get("d")).toBe(t0 + 1000);
    expect(timeline.stateAt(t0 - 5000)?.at).toBe(t0);
  });

  test("prunes beyond the two-minute window and resets on a clock jump back", () => {
    const timeline = new Timeline();
    for (let second = 0; second <= 200; second++)
      timeline.apply("delta", frame(t0 + second * 1000, { requests: [request(`r${second}`, t0 + second * 1000)] }), 0);
    expect(timeline.earliestAt).toBeGreaterThan(t0 + 60_000);
    expect(timeline.earliestAt).toBe(Math.min(timeline.frames[0]!.at, timeline.requests[0]!.playAt));
    expect(timeline.requests.length).toBeLessThan(140);
    timeline.apply("delta", frame(t0), 0);
    expect(timeline.frames.length).toBe(1);
    expect(timeline.requests.length).toBe(0);
  });
});

describe("state mapping", () => {
  test("cooldown drains from the start of the throttle run to its deadline", () => {
    const item = deployment("d", { state: "Throttled", throttledUntil: new Date(t0 + 10_000).toISOString() });
    const visual = nodeVisual(item, t0, t0 + 2500);
    expect(visual.look).toBe("throttled");
    expect(visual.cooldown).toBeCloseTo(0.75);
    expect(visual.cooldownMs).toBe(7500);
  });

  test("open, half-open, disabled and absent looks", () => {
    expect(nodeVisual(deployment("d", { state: "Open", openUntil: new Date(t0 + 5000).toISOString() }), undefined, t0))
      .toMatchObject({ look: "open", probeInMs: 5000 });
    expect(nodeVisual(deployment("d", { state: "Open", halfOpen: true }), undefined, t0).look).toBe("halfOpen");
    expect(nodeVisual(deployment("d", { state: "Disabled" }), undefined, t0).look).toBe("disabled");
    expect(nodeVisual(undefined, undefined, t0).look).toBe("absent");
  });

  test("replica breakdown lists the worst state first", () => {
    const item = deployment("d", { state: "Open" });
    item.replicas = [
      { replica: "a", state: "Healthy", p95TtfbMs: null, accountOpen: false },
      { replica: "b", state: "Open", p95TtfbMs: null, accountOpen: false },
      { replica: "c", state: "Open", p95TtfbMs: null, accountOpen: false },
    ];
    expect(nodeVisual(item, undefined, t0).replicaStates).toEqual([{ state: "Open", count: 2 }, { state: "Healthy", count: 1 }]);
  });

  test("attempt and request tones", () => {
    expect(attemptTone(attempt({ status: 429, healthOutcome: "Throttled" }))).toBe("throttled");
    expect(attemptTone(attempt({ status: null, healthOutcome: "AccountFailure" }))).toBe("failed");
    expect(attemptTone(attempt({ status: 400, healthOutcome: "Ignored" }))).toBe("rejected");
    expect(requestTone(request("a", t0, { attempts: [attempt({ status: 429 }), attempt()] }))).toBe("retried");
    expect(requestTone(request("a", t0, { status: 503, outcome: "error", attempts: [] }))).toBe("failed");
    expect(requestTone(request("a", t0, { status: 400, outcome: "error" }))).toBe("rejected");
  });

  test("outcome mix orders success first and computes shares", () => {
    const mix = outcomeMix({ deploymentId: "d", requestsPerSecond: 4, outcomes: [
      { deploymentId: "d", outcome: "Throttled", count: 1 }, { deploymentId: "d", outcome: "Success", count: 3 }] });
    expect(mix.map((item) => [item.outcome, item.share])).toEqual([["Success", 0.75], ["Throttled", 0.25]]);
  });

  test("chain labels use short region names", () => {
    expect(regionShort("swedencentral")).toBe("sweden");
    expect(regionShort("germanywestcentral")).toBe("germany");
    expect(regionShort("eastus2")).toBe("eastus2");
    const chained = request("a", t0, { attempts: [attempt({ region: "francecentral", status: 429 }), attempt({ region: "swedencentral" })] });
    expect(chainLabel(chained)).toBe("france 429 → sweden 200");
  });
});

describe("layout", () => {
  const demo = new DemoStream("calm", t0).snapshot();
  const deployments = demo.deployments.map((item) => item.deployment);
  const layout = layoutScene(deployments, ["devkit", "orchestrator", "reporting"], { width: 1600, height: 900 }, 16);

  test("places every deployment once, in its own lane, grouped by place, each place once", () => {
    expect(layout.nodes.length).toBe(deployments.length);
    expect(new Set(layout.nodes.map((node) => node.y)).size).toBe(layout.nodes.length);
    // Regions first: East US 2 holds its PTU and standard deployments; Global is a place of its own, last.
    expect(layout.places.map((place) => place.key)).toEqual(["francecentral", "germanywestcentral", "swedencentral", "eastus2", "global"]);
    expect(layout.nodes.filter((node) => node.place === "eastus2").map((node) => node.tier)).toEqual([0, 1, 1]);
    for (const node of layout.nodes) {
      const place = layout.places.find((item) => item.key === node.place)!;
      expect(node.y).toBeGreaterThan(place.top);
      expect(node.y).toBeLessThan(place.bottom);
    }
  });

  test("reads left to right: callers, LB, place boxes, deployment, with room for labels", () => {
    const c = layout.columns;
    expect(c.callers).toBeLessThan(c.lb);
    expect(c.lb).toBeLessThan(c.places);
    expect(c.placesEnd - c.places).toBe(10.5 * 16);
    expect(c.placesEnd).toBeLessThan(c.bars);
    expect(c.bars).toBeLessThan(c.discs);
    expect(1600 - c.discs).toBeGreaterThanOrEqual(12 * 16);
    expect(layout.refused.y).toBeGreaterThan(layout.places.at(-1)!.bottom);
    expect(layout.refused.y).toBeLessThan(900);
  });

  test("callers sit close to the LB with room for a three-line label each", () => {
    const [first, second] = layout.callers;
    expect(second!.y - first!.y).toBeGreaterThanOrEqual(4 * 16);
    expect(layout.columns.lb - layout.columns.callers).toBeLessThanOrEqual(6 * 16);
  });

  test("idle fallbacks get half a lane; the rest of the lanes grow", () => {
    const global = layout.nodes.find((node) => node.place === "global")!;
    const compact = layoutScene(deployments, ["devkit", "orchestrator", "reporting"], { width: 1600, height: 900 }, 16, new Set([global.id]));
    expect(compact.nodes.find((node) => node.id === global.id)?.compact).toBe(true);
    expect(compact.places.find((place) => place.key === "global")?.compact).toBe(true);
    const height = (l: typeof layout, key: string) => { const place = l.places.find((item) => item.key === key)!; return place.bottom - place.top; };
    expect(height(compact, "global")).toBeCloseTo(compact.laneHeight / 2);
    expect(compact.laneHeight).toBeGreaterThan(layout.laneHeight);
  });

  test("positions depend on structure only, so traffic never moves a node", () => {
    expect(layoutScene(deployments, ["devkit", "orchestrator", "reporting"], { width: 1600, height: 900 }, 16)).toEqual(layout);
  });

  test("ports stack children in order around the parent centre", () => {
    expect(stackPorts(100, [10, 20, 30])).toEqual([75, 90, 115]);
    expect(stackPorts(50, [])).toEqual([]);
  });

  test("paths move at constant speed along their points", () => {
    const path = makePath([{ x: 0, y: 0 }, { x: 10, y: 0 }, { x: 10, y: 30 }]);
    expect(path.total).toBe(40);
    expect(pathPoint(path, 0.25)).toEqual({ x: 10, y: 0 });
    expect(pathPoint(path, 0.5)).toEqual({ x: 10, y: 10 });
    expect(pathPoint(path, 1)).toEqual({ x: 10, y: 30 });
  });

  test("links leave and enter horizontally", () => {
    expect(linkPoint({ x: 0, y: 0 }, { x: 100, y: 50 }, 0)).toEqual({ x: 0, y: 0 });
    expect(linkPoint({ x: 0, y: 0 }, { x: 100, y: 50 }, 1)).toEqual({ x: 100, y: 50 });
    expect(linkPoint({ x: 0, y: 0 }, { x: 100, y: 50 }, 0.5).y).toBeCloseTo(25);
  });
});

describe("funnel", () => {
  const d = (id: string, tier: number, region: string, weight: number, zone = "eu") =>
    deployment(id, { tier, region, weight, zone }).deployment;
  const inventory = [d("ptu", 0, "francecentral", 10), d("se", 1, "swedencentral", 120), d("fr", 1, "francecentral", 80),
    d("glob", 2, "swedencentral", 200, "global")];
  const route = (hops: [string, string][], count: number, patch: Partial<DashboardRoute> = {}): DashboardRoute => ({
    caller: "orchestrator", modelKey: "gpt-4o@1", zone: "eu", status: 200, count,
    hops: hops.map(([deploymentId, outcome]) => ({ deploymentId, outcome })), ...patch,
  });
  const ticks = [
    { at: t0, routes: [route([["ptu", "Success"]], 70), route([["ptu", "Throttled"], ["fr", "Success"]], 10)] },
    { at: t0 + 1000, routes: [route([["se", "Success"]], 10, { caller: "batch" }), route([["fr", "Throttled"], ["se", "Success"]], 5),
      route([], 3, { status: 503 }), route([["se", "Failure"]], 2, { status: 502 })] },
  ];
  const funnel = computeFunnel(ticks, inventory, { model: "", zone: "", caller: "" });

  test("counts requests, not attempts, and splits them into served, failed and refused", () => {
    expect(funnel.total).toBe(100);
    expect(funnel.served).toBe(95);
    expect(funnel.fellBack).toBe(15);
    expect(funnel.failed).toBe(2);
    expect(funnel.refused).toBe(3);
    expect(funnel.seconds).toBe(2);
    expect(funnel.callers.get("orchestrator")?.reached).toBe(90);
    expect(funnel.callers.get("batch")?.served).toBe(10);
  });

  test("tiers, places and deployments show what they served, what left them and what spilled into them", () => {
    const tier0 = funnel.tiers.get(0)!;
    expect([tier0.reached, tier0.served, tier0.throttled, tier0.first]).toEqual([80, 70, 10, 80]);
    const tier1 = funnel.tiers.get(1)!;
    // Ten requests reached tier 1 after tier 0 throttled them; tier 1 served them, so they spilled in.
    expect([tier1.reached, tier1.served, tier1.failed, tier1.spilled]).toEqual([27, 25, 2, 25]);
    // A retry inside the tier (France 429 → Sweden) counts once for the tier and as served there.
    expect(tier1.throttled).toBe(0);
    // Spill lands where the request was served: France standard took the ten requests the France PTU throttled.
    expect(funnel.deployments.get("fr")?.spilled).toBe(10);
    expect(funnel.deployments.get("se")?.spilled).toBe(15);
    expect(funnel.places.get("francecentral")?.spilled).toBe(10);
  });

  test("regions and deployments show reached and moved on, with the reason", () => {
    const france = funnel.deployments.get("fr")!;
    expect([france.reached, france.served, france.throttled]).toEqual([15, 10, 5]);
    // France is one place for both of its tiers; a request that tried the PTU and then France standard counts once.
    const place = funnel.places.get("francecentral")!;
    expect([place.reached, place.served, place.throttled]).toEqual([85, 80, 5]);
    // The Global deployment's account is in Sweden, but it is a place of its own.
    expect(funnel.places.get("swedencentral")?.reached).toBe(17);
    const sweden = funnel.deployments.get("se")!;
    expect([sweden.reached, sweden.served, sweden.failed, sweden.first]).toEqual([17, 15, 2, 12]);
  });

  test("fallback paths sum over callers, most frequent first", () => {
    expect(funnel.paths.map((path) => path.count)).toEqual([10, 5, 3, 2]);
    expect(funnel.paths[2]?.hops).toEqual([]);
  });

  test("rare fallback paths fold into one line; frequent or large ones keep a row", () => {
    const busy = computeFunnel([...ticks, { at: t0 + 2000, routes: [route([["se", "Success"]], 900)] }], inventory, { model: "", zone: "", caller: "" });
    const { notable, rare } = splitPaths(busy);
    expect(notable.map((path) => path.count)).toEqual([10, 5, 3]);
    expect(rare.map((path) => path.count)).toEqual([2]);
    // Two requests in a hundred are rare by count, but too large a share to hide.
    expect(splitPaths(funnel).notable.map((path) => path.count)).toEqual([10, 5, 3, 2]);
  });

  test("filters apply to every count", () => {
    const batch = computeFunnel(ticks, inventory, { model: "", zone: "", caller: "batch" });
    expect([batch.total, batch.served]).toEqual([10, 10]);
    expect(computeFunnel(ticks, inventory, { model: "other", zone: "", caller: "" }).total).toBe(0);
  });

  test("quota share follows weight inside a model, tier and zone group", () => {
    // Tier 1 EU served 25 of 100; Sweden has 120 of 200 weight, so its weight asks for 15 %.
    expect(quotaShare(inventory[1]!, inventory, funnel)).toBeCloseTo(0.15);
    expect(preferredTier(inventory, "gpt-4o@1", "eu")).toBe(0);
    expect(preferredTier(inventory, "gpt-4o@1", "us")).toBeNull();
  });

  test("the timeline keeps unsampled ticks from the snapshot and each delta, without duplicates", () => {
    const stream = new DemoStream("france-throttled", t0);
    const timeline = new Timeline();
    const snapshot = stream.snapshot();
    timeline.apply("snapshot", snapshot, t0);
    timeline.apply("snapshot", snapshot, t0);
    expect(timeline.ticks.length).toBe(120);
    const delta = stream.tick();
    timeline.apply("delta", delta, t0 + 1000);
    expect(timeline.ticks.length).toBe(121);
    const counted = (delta.routes ?? []).reduce((sum, item) => sum + item.count, 0);
    expect(counted).toBeGreaterThan(delta.requests.length - 1);
    expect(timeline.ticksBetween(t0 - 30_000, t0 + 1000).length).toBe(31);
  });
});

describe("attention", () => {
  const empty = computeFunnel([], [], { model: "", zone: "", caller: "" });
  const frameOf = (items: MergedDeployment[]) => {
    const timeline = new Timeline();
    timeline.apply("delta", { at: new Date(t0).toISOString(), replicas: ["r1"], deployments: items, requests: [], counts: [] }, t0);
    return timeline.stateAt(t0);
  };

  test("an unreachable account is one issue, and several make a zone headline", () => {
    const frame = frameOf([
      deployment("a", { account: "oai-sweden", accountOpen: true, state: "Open", openUntil: new Date(t0 + 30_000).toISOString() }),
      deployment("b", { account: "oai-sweden", accountOpen: true, state: "Open" }),
      deployment("c", { account: "oai-france", region: "francecentral", accountOpen: true, state: "Open" }),
      deployment("d", { account: "oai-us", region: "eastus2", zone: "us" }),
    ]);
    const issues = findIssues(frame, empty, t0);
    expect(issues.map((issue) => issue.title)).toEqual(["France Central is down", "Sweden Central is down"]);
    expect(issues[1]?.detail).toContain("Next probe in 30 s");
    expect(verdict(issues, empty, 4)).toMatchObject({ tone: "critical", title: "2 EU accounts are down" });
  });

  test("healthy with a stray failure stays calm; a percent of failures does not", () => {
    const funnel = (failed: number) => computeFunnel([{ at: t0, routes: [
      { caller: "a", modelKey: "m", zone: "eu", status: 200, hops: [{ deploymentId: "x", outcome: "Success" }], count: 1000 - failed },
      { caller: "a", modelKey: "m", zone: "eu", status: 500, hops: [{ deploymentId: "x", outcome: "Failure" }], count: failed },
    ] }], [], { model: "", zone: "", caller: "" });
    expect(verdict([], funnel(1), 9)).toMatchObject({ tone: "ok", title: "All 9 deployments healthy" });
    expect(verdict([], funnel(20), 9).tone).toBe("critical");
  });

  test("a throttled deployment names itself and its cooldown", () => {
    const frame = frameOf([deployment("f", { account: "oai-france", region: "francecentral", state: "Throttled",
      throttledUntil: new Date(t0 + 6000).toISOString() })]);
    const [issue] = findIssues(frame, empty, t0);
    expect(issue?.title).toBe("France Central gpt-4o is throttled");
    expect(issue?.detail).toContain("for 6 s more");
  });

  test("percent keeps small shares honest", () => {
    expect([percent(0), percent(0.0004), percent(0.012), percent(0.5), percent(0.9975), percent(0.9996), percent(1)])
      .toEqual(["0\u202f%", "<\u202f0.1\u202f%", "1.2\u202f%", "50\u202f%", "99.7\u202f%", ">\u202f99.9\u202f%", "100\u202f%"]);
  });
});

describe("playback", () => {
  test("a retried streaming request bounces once, then streams", () => {
    const plan = planRequest(request("a", t0, { streaming: true, durationMs: 9000, attempts: [
      attempt({ deploymentId: "x", status: 429, healthOutcome: "Throttled", retryReason: "throttled" }),
      attempt({ deploymentId: "y" })] }));
    expect(plan.segments.map((segment) => segment.kind)).toEqual(["travel", "travel", "dwell", "bounce", "travel", "dwell", "stream"]);
    expect(plan.segments[3]?.tone).toBe("throttled");
    expect(plan.segments.at(-1)?.duration).toBeGreaterThan(timing.streamMin);
    expect(segmentAt(plan, plan.duration)).toBeNull();
    expect(segmentAt(plan, 10)?.segment.kind).toBe("travel");
  });

  test("a request refused at the LB follows the refused ribbon and bursts there", () => {
    const plan = planRequest(request("a", t0, { status: 429, outcome: "error", attempts: [] }));
    expect(plan.segments.map((segment) => [segment.kind, segment.tone])).toEqual([["travel", "pending"], ["travel", "throttled"], ["burst", "throttled"]]);
    expect(plan.segments[1]?.to).toEqual({ kind: "refused" });
  });

  test("sampling caps each second, keeps failures first, and is deterministic", () => {
    const items: TimedRequest[] = Array.from({ length: 50 }, (_, index) => ({
      request: request(`r${index}`, t0, index === 7 ? { status: 503, outcome: "failure" } : {}),
      playAt: t0 + index * 10, completedAt: t0 + index * 10,
    }));
    const chosen = selectForPlayback(items, 12);
    expect(chosen.length).toBe(12);
    expect(chosen.some((item) => item.request.id === "r7")).toBe(true);
    expect(selectForPlayback([...items].reverse(), 12).map((item) => item.request.id)).toEqual(chosen.map((item) => item.request.id));
  });
});

describe("playback clock", () => {
  test("pause freezes, resume replays from the same point, seek clamps to the window, replay catches up to live", () => {
    const timeline = new Timeline();
    timeline.apply("snapshot", frame(t0, { requests: [request("old", t0 - 90_000)] }), t0);
    const clock = new PlaybackClock(timeline);
    const live = t0 - PLAYBACK_LAG_MS;
    expect(clock.time(t0)).toBe(live);
    clock.pause(t0);
    expect(clock.time(t0 + 5000)).toBe(live);
    clock.resume(t0 + 5000);
    expect(clock.mode).toBe("replay");
    expect(clock.time(t0 + 6000)).toBe(live + 1000);
    clock.seek(t0 - 500_000, t0 + 6000);
    expect(clock.time(t0 + 6000)).toBe(t0 - 90_000);
    clock.seek(Infinity, t0 + 6000);
    expect(clock.mode).toBe("live");
    clock.pause(t0);
    clock.resume(t0);
    expect(clock.time(t0 + 1)).toBe(live + 1);
    expect(clock.mode).toBe("live");
  });
});

describe("spring", () => {
  test("retargeting keeps velocity and settles exactly", () => {
    const s = spring(0);
    s.target = 100;
    stepSpring(s, 100, springs.layout);
    const velocity = s.velocity;
    expect(velocity).toBeGreaterThan(0);
    s.target = 50;
    stepSpring(s, 16, springs.layout);
    expect(Math.abs(s.velocity)).toBeGreaterThan(0);
    for (let index = 0; index < 200; index++) stepSpring(s, 16, springs.layout);
    expect(s.value).toBe(50);
    snapSpring(s, 3);
    expect(s).toEqual({ value: 3, target: 3, velocity: 0 });
  });
});

describe("demo stream", () => {
  const run = (scenario: (typeof scenarios)[number]) => {
    const stream = new DemoStream(scenario, t0);
    const timeline = new Timeline();
    timeline.apply("snapshot", stream.snapshot(), t0);
    for (let second = 1; second <= 5; second++) timeline.apply("delta", stream.tick(), t0 + second * 1000);
    return { timeline, latest: timeline.stateAt(Infinity)! };
  };
  const byAccount = (state: ReturnType<typeof run>["latest"], account: string) =>
    [...state.deployments.values()].filter((item) => item.deployment.account === account).map((item) => item.deployment.state);

  test("calm keeps every deployment healthy and every request successful", () => {
    const { timeline, latest } = run("calm");
    expect([...latest.deployments.values()].every((item) => item.deployment.state === "Healthy")).toBe(true);
    const failed = timeline.requests.filter((item) => item.request.status >= 400).length;
    expect(failed / timeline.requests.length).toBeLessThan(0.01);
  });

  test("sweden-slow degrades Sweden", () => {
    expect(byAccount(run("sweden-slow").latest, "oai-swedencentral")).toContain("Degraded");
  });

  test("france-throttled throttles France with a cooldown deadline", () => {
    const { latest } = run("france-throttled");
    const france = [...latest.deployments.values()].find((item) => item.deployment.account === "oai-francecentral")!;
    expect(france.deployment.state).toBe("Throttled");
    expect(france.deployment.throttledUntil).toBeString();
  });

  test("eastus2-outage opens the whole account and traffic still succeeds elsewhere", () => {
    const { timeline, latest } = run("eastus2-outage");
    expect(byAccount(latest, "oai-eastus2").every((state) => state === "Open")).toBe(true);
    const recent = timeline.requests.slice(-60);
    expect(recent.filter((item) => item.request.caller === "orchestrator").every((item) => item.request.status === 200)).toBe(true);
  });

  test("eu-down fails EU-zone requests while global-zone requests succeed", () => {
    const { timeline } = run("eu-down");
    const recent = timeline.requests.slice(-120);
    expect(recent.filter((item) => item.request.zone === "eu").every((item) => item.request.status >= 500)).toBe(true);
    const global = recent.filter((item) => item.request.zone === "global" && item.request.modelKey?.startsWith("gpt-4o"));
    expect(global.every((item) => item.request.status === 200)).toBe(true);
  });
});

describe("feed runs", () => {
  const refused = (id: string, at: number, model: string) => ({ request: request(id, at, { caller: "reporting", modelKey: model, status: 503, attempts: [] }),
    playAt: at, completedAt: at });
  const served = (id: string, at: number) => ({ request: request(id, at), playAt: at, completedAt: at });

  test("consecutive requests with the same story are one run, whatever their model", () => {
    const runs = new RunGrouper().group([refused("c", 3, "gpt-4o@1"), refused("b", 2, "embed@1"), served("x", 1), refused("a", 0, "gpt-4o@1")]);
    expect(runs.map((run) => run.items.map((item) => item.request.id))).toEqual([["c", "b"], ["x"], ["a"]]);
  });

  test("a run keeps its key while it grows at the top and loses requests at the bottom", () => {
    const grouper = new RunGrouper();
    const [first] = grouper.group([refused("b", 2, "m"), refused("a", 1, "m")]);
    const [grown] = grouper.group([refused("d", 4, "m"), refused("c", 3, "m"), refused("b", 2, "m")]);
    expect(grown?.key).toBe(first?.key);
    expect(grown?.items.length).toBe(3);
  });
});

describe("time strip", () => {
  test("the axis maximum is a round number at or above the peak", () => {
    expect([niceCeiling(0), niceCeiling(7), niceCeiling(23), niceCeiling(50), niceCeiling(51), niceCeiling(0.3)]).toEqual([1, 10, 50, 50, 100, 0.5]);
  });
});
