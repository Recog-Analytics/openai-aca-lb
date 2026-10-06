// Wire shapes of the dashboard service's /api/stream frames (ASP.NET web JSON, camelCase).

export type HealthStateName = "Healthy" | "Throttled" | "Degraded" | "Open" | "Disabled";

export interface DashboardDeployment {
  id: string;
  account: string;
  deployment: string;
  region: string;
  modelKey: string;
  tier: number;
  zone: string;
  weight: number;
  state: HealthStateName;
  p95TtfbMs: number | null;
  accountOpen: boolean;
  throttledUntil?: string | null;
  openUntil?: string | null;
  halfOpen?: boolean;
}

export interface ReplicaDeployment {
  replica: string;
  state: HealthStateName;
  p95TtfbMs: number | null;
  accountOpen: boolean;
}

export interface MergedDeployment {
  deployment: DashboardDeployment;
  replicas: ReplicaDeployment[];
}

export interface OutcomeCount {
  deploymentId: string;
  outcome: string;
  count: number;
}

export interface DeploymentRate {
  deploymentId: string;
  requestsPerSecond: number;
  outcomes: OutcomeCount[];
}

export interface RequestAttempt {
  deployment: string;
  account: string;
  region: string;
  tier: number;
  status: number | null;
  ttfbMs: number | null;
  healthOutcome: string;
  retryReason: string | null;
  deploymentId: string | null;
  /** Azure error JSON `error.code` of a failed attempt. Absent from older replicas. */
  errorCode?: string | null;
  /** Azure error JSON `error.message`, at most 300 characters. */
  errorMessage?: string | null;
  /** The backend's `apim-request-id` or `x-request-id`, for Azure support. */
  backendRequestId?: string | null;
}

export interface RequestRecord {
  id: string;
  startedAt: string;
  caller: string | null;
  requestedModel: string | null;
  modelKey: string | null;
  zone: string | null;
  streaming: boolean;
  status: number;
  durationMs: number;
  attempts: RequestAttempt[];
  outcome: string;
  /** "model" for a model-key pool, "deployment" for a deployment-name pool. Absent from older replicas. */
  poolKind?: "model" | "deployment" | null;
  pool?: string | null;
  /** Operation slug from the path: "chat.completions", "responses", "embeddings", "audio.transcriptions", … */
  operation?: string | null;
  apiVersion?: string | null;
  /** Request body size in bytes. */
  requestBytes?: number | null;
  /** max_completion_tokens, max_tokens or max_output_tokens from a JSON body. */
  maxOutputTokens?: number | null;
}

/** One backend attempt of a route. */
export interface DashboardHop {
  deploymentId: string;
  outcome: string;
}

/** Unsampled count of requests that share caller, model, zone, final status and attempt chain. */
export interface DashboardRoute {
  caller: string | null;
  modelKey: string | null;
  zone: string | null;
  status: number;
  hops: DashboardHop[];
  count: number;
  poolKind?: "model" | "deployment" | null;
  pool?: string | null;
}

export interface RouteTick {
  at: string;
  routes: DashboardRoute[];
}

export interface DashboardFrame {
  at: string;
  replicas: string[];
  deployments: MergedDeployment[];
  requests: RequestRecord[];
  counts: DeploymentRate[];
  /** Route counts of this tick. Absent from an older dashboard service. */
  routes?: DashboardRoute[];
  /** Retained route ticks; only the snapshot fills it. */
  routeHistory?: RouteTick[];
  /** Per-minute totals for the retained day: the snapshot carries all, a delta that closes a minute carries that one. */
  summary?: SummaryBucket[];
  /** Earliest instants with per-second and per-minute history. */
  retention?: { secondsFrom: string; minutesFrom: string };
  /** A stable small number per live replica, for "Replica 1…N". */
  replicaNumbers?: Record<string, number>;
}

export type ProblemState = Exclude<HealthStateName, "Healthy">;

/** One minute of the day summary: request outcomes and the worst deployment state. */
export interface SummaryBucket {
  /** Minute end. */
  at: string;
  total: number;
  served: number;
  retried: number;
  failed: number;
  refused: number;
  worst: ProblemState | null;
  unhealthy: number;
}

/** A deployment's worst state within a history bucket; healthy deployments are omitted. */
export interface CompactState {
  deploymentId: string;
  state: ProblemState;
  /** Seconds it was not healthy in the bucket. */
  seconds: number;
  accountOpen: boolean;
  halfOpen: boolean;
  p95TtfbMs: number | null;
}

export interface HistoryBucket {
  /** Bucket end; a bucket covers [at - resolution, at). */
  at: string;
  /** Seconds of data present in the bucket. */
  seconds: number;
  routes: DashboardRoute[];
  states: CompactState[];
}

/** GET /api/history: exact route counts and worst states per bucket, plus sampled requests, for a past range. */
export interface HistoryResponse {
  from: string;
  to: string;
  /** Bucket length in seconds. */
  resolution: number;
  buckets: HistoryBucket[];
  requests: RequestRecord[];
  deployments: MergedDeployment[];
}

export type FrameKind = "snapshot" | "delta";
