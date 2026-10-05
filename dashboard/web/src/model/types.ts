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
}

export type FrameKind = "snapshot" | "delta";
