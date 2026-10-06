// Traffic shapes shared by the live demo stream and its statistical history.
import type { RequestAttempt } from "./types";

/** Deterministic PRNG so a scenario replays the same way in tests and screenshots. */
export function random(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** A uniform number in [0, 1) fixed by its inputs: the history's per-minute randomness. */
export function hash(seed: number, a: number, b: number): number {
  let t = Math.imul(seed ^ 0x9e3779b9, 0x85ebca6b);
  t = Math.imul(t ^ (t >>> 15) ^ a, 0xc2b2ae35);
  t = Math.imul(t ^ (t >>> 13) ^ b, 0x27d4eb2f);
  t = Math.imul(t ^ (t >>> 16), 0x85ebca6b);
  return ((t ^ (t >>> 13)) >>> 0) / 4294967296;
}

/** A share of requests that one caller sends to one pool. */
export interface Flow {
  caller: string;
  modelKey: string;
  zone: string;
  poolKind?: "model" | "deployment";
  pool?: string;
  share: number;
}

export interface RequestContext {
  operation: string;
  apiVersion: string;
  streaming: boolean;
  requestBytes: number;
  maxOutputTokens: number | null;
}

// Production: EU-only callers. The caller names are illustrative; the shares follow production's model mix.
const mix: [caller: string, model: string, share: number, deploymentPool?: string][] = [
  ["portal", "gpt-4.1-mini@2025-04-14", 19],
  ["agents", "gpt-4.1-mini@2025-04-14", 14],
  ["translator", "gpt-4.1-mini@2025-04-14", 9],
  ["agents", "gpt-5-mini@2025-08-07", 9],
  ["portal", "gpt-5-mini@2025-08-07", 6],
  ["translator", "gpt-4o-mini@2024-07-18", 6],
  ["portal", "gpt-4o-mini@2024-07-18", 3],
  ["portal", "gpt-4o-mini@2024-07-18", 2, "llm-gpt-4omini-public"],
  ["agents", "o3@2025-04-16", 5],
  ["portal", "o3@2025-04-16", 3],
  ["agents", "gpt-5@2025-08-07", 5],
  ["portal", "gpt-5@2025-08-07", 2],
  ["agents", "gpt-6.1-sol@2026-09-29", 5],
  ["portal", "whisper@001", 4],
  ["translator", "gpt-4.1@2025-04-14", 2],
  ["translator", "gpt-4o@2024-08-06", 0.5],
  ["smoke-test", "gpt-4.1-mini@2025-04-14", 0.25],
  ["smoke-test", "gpt-4o-mini@2024-07-18", 0.15],
];

export const productionFlows: Flow[] = mix.map(([caller, modelKey, share, pool]) =>
  pool ? { caller, modelKey, zone: "eu", poolKind: "deployment", pool, share } : { caller, modelKey, zone: "eu", poolKind: "model", pool: modelKey, share });

export const productionReplicas = ["ca-lb--rev1-6b8f9c7d5-d2cmn", "ca-lb--rev1-6b8f9c7d5-x5kqz"] as const;

const day = 86_400_000;
/** Requests per second: busier by day, with a slow wave on top (about 20 to 35). */
export function productionRate(at: number): number {
  return 27 - 4 * Math.cos((2 * Math.PI * at) / day) + 2.5 * Math.sin(at / 162_000) + 1.2 * Math.sin(at / 24_000);
}

/** Median time to first byte by model, in ms. */
export function baseTtfb(modelKey: string): number {
  const name = modelKey.split("@")[0] ?? modelKey;
  if (name === "whisper") return 1400;
  if (name === "o3") return 2100;
  if (name.startsWith("gpt-6") || name === "gpt-5") return 950;
  if (name.startsWith("gpt-5")) return 700;
  return name.includes("mini") || name.includes("nano") ? 380 : 520;
}

const pick = <T>(items: readonly T[], rand: () => number): T => items[Math.floor(rand() * items.length)] ?? (items[0] as T);

export function requestContext(caller: string, modelKey: string, rand: () => number): RequestContext {
  const name = modelKey.split("@")[0] ?? modelKey;
  if (name === "whisper")
    return { operation: "audio.transcriptions", apiVersion: "2024-06-01", streaming: false, requestBytes: Math.round(150_000 + rand() ** 2 * 6_000_000), maxOutputTokens: null };
  if (name.startsWith("text-embedding"))
    return { operation: "embeddings", apiVersion: "2024-10-21", streaming: false, requestBytes: Math.round(300 + rand() ** 2 * 12_000), maxOutputTokens: null };
  const reasoning = name === "o3" || name.startsWith("gpt-5") || name.startsWith("gpt-6");
  const responses = reasoning && caller === "agents" && rand() < 0.7;
  const apiVersion = responses || caller === "agents" || caller === "portal" ? "2025-04-01-preview" : caller === "translator" ? "2024-10-21" : pick(["2024-10-21", "2025-01-01-preview"], rand);
  const streaming = caller !== "translator" && caller !== "smoke-test" && rand() < (responses ? 0.3 : 0.45);
  const requestBytes = caller === "smoke-test" ? 180 + Math.round(rand() * 40) : Math.round((caller === "translator" ? 2400 : 900) + rand() ** 2 * 28_000);
  const maxOutputTokens = rand() < 0.4 ? pick([256, 512, 1024, 2048, 4096, 16_384], rand) : null;
  return { operation: responses ? "responses" : "chat.completions", apiVersion, streaming, requestBytes, maxOutputTokens };
}

const operationNames: Record<string, string> = {
  "chat.completions": "ChatCompletions_Create", responses: "Responses_Create", "audio.transcriptions": "AudioTranscriptions_Create",
};

export const contentFilterMessage = "The response was filtered due to the prompt triggering Azure OpenAI's content management policy. Please modify your prompt and retry. " +
  "To learn more about our content filtering policies please read our documentation: https://go.microsoft.com/fwlink/?linkid=2198766";

function requestId(rand: () => number): string {
  const hex = Array.from({ length: 4 }, () => Math.floor(rand() * 4294967296).toString(16).padStart(8, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20, 32)}`;
}

/** Adds what the LB records from Azure's response: the request id of every answered attempt, the error of a failed one. */
export function decorate(attempt: RequestAttempt, context: RequestContext, rand: () => number, retryAfterMs = 6000): RequestAttempt {
  if (attempt.status === null) return attempt;
  const backendRequestId = requestId(rand);
  if (attempt.status === 429) {
    const operation = operationNames[context.operation] ?? "ChatCompletions_Create";
    return {
      ...attempt, backendRequestId, errorCode: "429", errorMessage: `Requests to the ${operation} Operation under Azure OpenAI API version ${context.apiVersion} ` +
        `have exceeded token rate limit of your current OpenAI S0 pricing tier. Please retry after ${Math.max(1, Math.round(retryAfterMs / 1000))} seconds.`,
    };
  }
  if (attempt.status >= 500)
    return { ...attempt, backendRequestId, errorCode: "InternalServerError", errorMessage: "The server had an error while processing your request. Sorry about that!" };
  if (attempt.status >= 400) return { ...attempt, backendRequestId, errorCode: "content_filter", errorMessage: contentFilterMessage };
  return { ...attempt, backendRequestId };
}
