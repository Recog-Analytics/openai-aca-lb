import { describe, expect, test } from "bun:test";
import { DemoStream, isScenario, scenarioLabels, scenarios, type Scenario } from "./demo";
import { productionInventory } from "./inventory";
import type { DashboardFrame, HistoryResponse, RequestRecord } from "./types";

const t0 = Date.parse("2026-10-05T12:00:30.250Z");
const arm = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-llm/providers/Microsoft.CognitiveServices/accounts";

/** Two minutes of snapshot plus `seconds` deltas, with every request (not sampled) of the deltas. */
function run(scenario: Scenario, seconds = 0) {
  const stream = new DemoStream(scenario, t0);
  const snapshot = stream.snapshot();
  const deltas: DashboardFrame[] = [];
  for (let second = 0; second < seconds; second++) deltas.push(stream.tick());
  return { stream, snapshot, deltas, latest: deltas.at(-1) ?? snapshot };
}

const total = (history: HistoryResponse) => history.buckets.reduce((sum, bucket) => sum + bucket.routes.reduce((a, route) => a + route.count, 0), 0);

describe("production inventory", () => {
  test("matches the LB's 71 deployments", () => {
    expect(productionInventory.length).toBe(71);
    expect(new Set(productionInventory.map((d) => d.model)).size).toBe(21);
    expect(new Set(productionInventory.map((d) => d.id)).size).toBe(71);
    expect(productionInventory.filter((d) => d.zone === "global").map((d) => [d.deployment, d.tier, d.sku])).toEqual([
      ["llm-gpt-audio", 2, "GlobalStandard"], ["llm-gpt-audio-mini", 2, "GlobalStandard"],
    ]);
    const mini = productionInventory.find((d) => d.account === "oai-swedencentral" && d.deployment === "llm-gpt-4_1mini")!;
    expect(mini).toMatchObject({ id: `${arm}/oai-swedencentral/deployments/llm-gpt-4_1mini`, model: "gpt-4.1-mini@2025-04-14", region: "swedencentral", capacity: 16000, weight: 16000 });
    expect(productionInventory.find((d) => d.deployment === "stt-whisper")).toMatchObject({ sku: "Standard", tier: 1, model: "whisper@001", capacity: 30 });
    expect(productionInventory.find((d) => d.deployment === "llm-gpt4_1nano")).toMatchObject({ account: "oai-francecentral", region: "francecentral", capacity: 202 });
    expect(productionInventory.find((d) => d.account === "oai-polandcentral" && d.deployment === "llm-gpt-5")?.capacity).toBe(50);
    expect(new Set(productionInventory.map((d) => d.region))).toEqual(new Set(["francecentral", "germanywestcentral", "polandcentral", "swedencentral", "westeurope"]));
  });

  test("scenarios keep the devkit presets and label every one", () => {
    expect(scenarios).toContain("eastus2-outage");
    expect(isScenario("production-sweden-outage")).toBe(true);
    expect(isScenario("production-nowhere")).toBe(false);
    for (const scenario of scenarios) expect(scenarioLabels[scenario]).toBeString();
  });
});

describe("production traffic", () => {
  // Deltas carry at most 100 sampled requests per second; production stays below that, so they are all of them.
  const { snapshot, deltas, latest } = run("production", 120);
  const requests: RequestRecord[] = deltas.flatMap((frame) => frame.requests);
  const routes = deltas.flatMap((frame) => frame.routes ?? []);
  const routed = routes.reduce((sum, route) => sum + route.count, 0);

  test("is calm, EU-only, on two named replicas", () => {
    expect(latest.deployments.every((item) => item.deployment.state === "Healthy")).toBe(true);
    expect(latest.deployments.length).toBe(71);
    expect(requests.length).toBe(routed);
    expect(requests.every((request) => request.zone === "eu")).toBe(true);
    expect(Object.values(latest.replicaNumbers ?? {})).toEqual([1, 2]);
    expect(latest.replicas[0]).toBe("ca-lb--rev1-6b8f9c7d5-d2cmn");
    expect(routed / 120).toBeGreaterThan(18);
    expect(routed / 120).toBeLessThan(37);
    expect(snapshot.summary?.length).toBe(1440);
  });

  test("is skewed: a few models carry most requests and many deployments stay idle", () => {
    const byModel = new Map<string, number>();
    for (const request of requests) byModel.set(request.modelKey ?? "", (byModel.get(request.modelKey ?? "") ?? 0) + 1);
    const top = [...byModel.values()].sort((a, b) => b - a).slice(0, 3).reduce((a, b) => a + b, 0);
    expect(top / requests.length).toBeGreaterThan(0.6);
    const byDeployment = new Map<string, number>();
    for (const request of requests)
      for (const attempt of request.attempts) byDeployment.set(attempt.deploymentId ?? "", (byDeployment.get(attempt.deploymentId ?? "") ?? 0) + 1);
    const idle = productionInventory.filter((d) => (byDeployment.get(d.id) ?? 0) < requests.length * 0.002);
    expect(idle.length).toBeGreaterThanOrEqual(40);
    expect(requests.some((request) => request.poolKind === "deployment" && request.pool === "llm-gpt-4omini-public")).toBe(true);
  });

  test("records the request context and Azure's error details", () => {
    expect(requests.every((request) => request.operation && request.apiVersion && (request.requestBytes ?? 0) > 0)).toBe(true);
    expect(new Set(requests.map((request) => request.operation))).toEqual(new Set(["chat.completions", "responses", "audio.transcriptions"]));
    const attempts = requests.flatMap((request) => request.attempts);
    const failed = attempts.filter((attempt) => attempt.status === null || attempt.status >= 400);
    expect(failed.length).toBeGreaterThan(0);
    for (const attempt of failed.filter((item) => item.status !== null)) {
      expect(attempt.errorCode).toBeString();
      expect(attempt.errorMessage!.length).toBeLessThanOrEqual(300);
    }
    expect(attempts.filter((attempt) => attempt.status !== null).every((attempt) => /^[0-9a-f-]{36}$/.test(attempt.backendRequestId ?? ""))).toBe(true);
  });
});

describe("production failures", () => {
  test("a Sweden outage opens the account while most requests are served elsewhere", () => {
    const { deltas, latest } = run("production-sweden-outage", 30);
    const sweden = latest.deployments.filter((item) => item.deployment.account === "oai-swedencentral");
    expect(sweden.every((item) => item.deployment.state === "Open" && item.deployment.accountOpen)).toBe(true);
    const requests = deltas.flatMap((frame) => frame.requests);
    expect(requests.filter((request) => request.status < 400).length / requests.length).toBeGreaterThan(0.85);
    expect(requests.filter((request) => request.modelKey === "whisper@001").every((request) => request.status >= 500)).toBe(true);
    expect(requests.every((request) => request.attempts.every((attempt) => attempt.status !== null || attempt.backendRequestId === undefined))).toBe(true);
  });

  test("France throttling and Sweden slowness show up", () => {
    const france = run("production-france-throttled", 5).latest.deployments.filter((item) => item.deployment.account === "oai-francecentral");
    expect(france.some((item) => item.deployment.state === "Throttled")).toBe(true);
    const sweden = run("production-sweden-slow", 5).latest.deployments.filter((item) => item.deployment.account === "oai-swedencentral");
    expect(sweden.some((item) => item.deployment.state === "Degraded")).toBe(true);
    const down = run("production-eu-down", 5).latest.deployments.filter((item) => item.deployment.accountOpen);
    expect(new Set(down.map((item) => item.deployment.account))).toEqual(new Set(["oai-swedencentral", "oai-francecentral", "oai-germanywestcentral"]));
  });
});

describe("demo history", () => {
  const { stream, snapshot, deltas } = run("production", 90);
  const now = Date.parse(deltas.at(-1)!.at);

  test("a minute bucket equals its 60 one-second buckets", () => {
    for (const ago of [50, 3, 1]) {
      const end = Math.floor(now / 60_000 - ago) * 60_000;
      const minute = stream.history(end - 60_000, end, 60);
      const seconds = stream.history(end - 60_000, end, 1);
      expect(minute.buckets.length).toBe(1);
      expect(seconds.buckets.length).toBe(60);
      expect(total(seconds)).toBe(total(minute));
      expect(total(minute)).toBeGreaterThan(900);
    }
  });

  test("the summary agrees with the history for the same minutes, live part included", () => {
    const closed = deltas.flatMap((frame) => frame.summary ?? []);
    expect(closed.length).toBeGreaterThanOrEqual(1);
    const summary = [...(snapshot.summary ?? []).slice(closed.length), ...closed];
    expect(summary.length).toBe(1440);
    const last = Date.parse(summary.at(-1)!.at);
    const day = stream.history(last - 86_400_000, last, 600);
    expect(total(day)).toBe(summary.reduce((sum, bucket) => sum + bucket.total, 0));
    for (const bucket of summary.slice(-5)) {
      const end = Date.parse(bucket.at);
      expect(total(stream.history(end - 60_000, end, 10))).toBe(bucket.total);
    }
  });

  test("past incidents leave states and notable requests", () => {
    const day = stream.history(now - 86_400_000, now, 600);
    const states = new Set(day.buckets.flatMap((bucket) => bucket.states.map((state) => state.state)));
    expect(states).toEqual(new Set(["Throttled", "Degraded", "Open"]));
    expect(day.requests.length).toBe(1000);
    expect(day.requests.some((request) => request.attempts.some((attempt) => attempt.errorCode === "429"))).toBe(true);
    expect(day.requests.every((request, index) => index === 0 || day.requests[index - 1]!.startedAt <= request.startedAt)).toBe(true);
    expect(day.requests.every((request) => request.operation && request.apiVersion)).toBe(true);
    expect(day.deployments.length).toBe(71);
    const outage = new DemoStream("production-sweden-outage", t0).history(t0 - 3_600_000, t0, 60);
    const open = outage.buckets.filter((bucket) => bucket.states.some((state) => state.accountOpen));
    expect(open.length).toBeGreaterThanOrEqual(4);
    expect(open.length).toBeLessThanOrEqual(7);
  });

  test("enforces the bucket cap and resolutions, deterministically and fast", () => {
    expect(() => stream.history(now - 3_600_000, now, 1)).toThrow();
    expect(() => stream.history(now - 3_600_000, now, 7)).toThrow();
    const fresh = () => new DemoStream("production", t0).history(t0 - 3_600_000, t0, 10);
    expect(fresh()).toEqual(fresh());
    const cold = new DemoStream("production-france-throttled", t0);
    const started = performance.now();
    cold.history(t0 - 86_400_000, t0, 600);
    cold.history(t0 - 3_600_000, t0, 10);
    expect(performance.now() - started).toBeLessThan(200);
  });
});
