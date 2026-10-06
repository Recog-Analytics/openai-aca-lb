import { quotaShare, type Funnel } from "./funnel";
import { durationLabel, modelShort, nodeVisual, regionName } from "./state";
import type { FrameState } from "./timeline";
import type { DashboardDeployment } from "./types";

export type IssueKind = "down" | "open" | "probing" | "throttled" | "slow" | "disabled";
export type Tone = "ok" | "warning" | "critical";

/** One thing an operator should look at: deployments of one account that share a problem. */
export interface Issue {
  key: string;
  kind: IssueKind;
  tone: Tone;
  title: string;
  detail: string;
  deploymentIds: string[];
  /** Data zones of the deployments involved. */
  zones: string[];
  /** Share of requests in scope that reached these deployments and left them without being served. */
  movedOn: number;
}

const kindRank: Record<IssueKind, number> = { down: 0, open: 1, probing: 2, throttled: 3, slow: 4, disabled: 5 };

/** Account label in words: the region its deployments live in, with "Global" for a global-only account. */
function place(items: DashboardDeployment[]): string {
  const first = items[0];
  if (!first) return "Unknown";
  const regions = [...new Set(items.map((item) => regionName(item.region)))];
  const name = regions.join(" and ");
  return items.every((item) => item.tier === 2) ? `Global ${name}` : items.every((item) => item.tier === 0) ? `${name} PTU` : name;
}

function zonesOf(items: DashboardDeployment[]): string[] {
  return [...new Set(items.map((item) => item.zone))];
}

/** "gpt-4o-mini and gpt-4o-mini · public"; past three, "a, b and 4 more models". */
function models(items: DashboardDeployment[], labels: Map<string, string>): string {
  const names = [...new Set(items.map((item) => labels.get(item.id) ?? modelShort(item.modelKey)))];
  return names.length <= 3 ? names.join(" and ") : `${names.slice(0, 2).join(", ")} and ${names.length - 2} more models`;
}

const seconds = (ms: number | null) => (ms === null ? null : Math.max(1, Math.ceil(ms / 1000)));

/** Problems in the frame at time `t`, worst first. */
export function findIssues(frame: FrameState | null, funnel: Funnel, t: number, labels: Map<string, string> = new Map()): Issue[] {
  if (!frame) return [];
  const all = [...frame.deployments.values()];
  const byAccount = new Map<string, typeof all>();
  for (const item of all) byAccount.set(item.deployment.account, [...(byAccount.get(item.deployment.account) ?? []), item]);
  const issues: Issue[] = [];
  const deploymentList = all.map((item) => item.deployment);
  const servedVersusWeight = (group: DashboardDeployment[]) => {
    if (funnel.total === 0) return "";
    const served = group.reduce((sum, item) => sum + (funnel.deployments.get(item.id)?.served ?? 0), 0) / funnel.total;
    const quotas = group.map((item) => quotaShare(item, deploymentList, funnel));
    if (quotas.some((value) => value === null)) return "";
    const quota = quotas.reduce<number>((sum, value) => sum + (value ?? 0), 0);
    return quota - served >= 0.02 ? ` It serves ${percent(served)} of requests; its weight asks for ${percent(quota)}.` : "";
  };
  const movedOn = (ids: string[]) => funnel.total === 0 ? 0 : ids.reduce((sum, id) => {
    const flow = funnel.deployments.get(id);
    return sum + (flow ? flow.throttled + flow.failed : 0);
  }, 0) / funnel.total;

  for (const [account, items] of byAccount) {
    const deployments = items.map((item) => item.deployment);
    const visuals = items.map((item) => ({ item, visual: nodeVisual(item, frame.throttledSince.get(item.deployment.id), t) }));
    const accountDown = deployments.some((item) => item.accountOpen) ||
      (deployments.length > 1 && deployments.every((item) => item.state === "Open"));
    if (accountDown) {
      const probe = Math.min(...visuals.map(({ visual }) => visual.probeInMs ?? Infinity));
      const probing = visuals.some(({ visual }) => visual.look === "halfOpen");
      const ids = deployments.map((item) => item.id);
      issues.push({
        key: `${account}:down`, kind: "down", tone: "critical", title: `${place(deployments)} is down`,
        detail: `Its account is unreachable, so the LB skips ${deployments.length === 1 ? "its deployment" : `all ${deployments.length} of its deployments`}. ` +
          (probing ? "Probing now." : Number.isFinite(probe) ? `Next probe in ${seconds(probe)} s.` : "Waiting to probe."),
        deploymentIds: ids, zones: zonesOf(deployments), movedOn: movedOn(ids),
      });
      continue;
    }
    const groups = new Map<IssueKind, typeof visuals>();
    for (const entry of visuals) {
      const kind: IssueKind | null = entry.visual.look === "open" ? "open" : entry.visual.look === "halfOpen" ? "probing"
        : entry.visual.look === "throttled" ? "throttled" : entry.visual.look === "degraded" ? "slow"
        : entry.visual.look === "disabled" ? "disabled" : null;
      if (kind) groups.set(kind, [...(groups.get(kind) ?? []), entry]);
    }
    for (const [kind, entries] of groups) {
      const group = entries.map((entry) => entry.item.deployment);
      const ids = group.map((item) => item.id);
      // One model reads "Sweden Central gpt-4o"; several share the account's name and are listed in the detail.
      const name = group.length === 1 ? `${place(group)} ${models(group, labels)}` : place(group);
      // Distinct LB replicas, not replica reports: four deployments seen by two replicas is still two replicas.
      const replicas = (() => {
        const all = new Set(entries.flatMap((entry) => entry.item.replicas.map((replica) => replica.replica)));
        const affected = new Set(entries.flatMap((entry) => entry.item.replicas.filter((replica) => replica.state !== "Healthy").map((replica) => replica.replica)));
        return all.size > 1 && affected.size < all.size ? ` Seen by ${affected.size} of ${all.size} LB replicas.` : "";
      })();
      if (kind === "throttled") {
        const cooldown = Math.max(...entries.map((entry) => entry.visual.cooldownMs ?? 0));
        issues.push({ key: `${account}:${kind}`, kind, tone: "warning", title: `${name} is throttled`,
          detail: `Answering 429, so the LB sends its share elsewhere${cooldown > 0 ? ` for ${seconds(cooldown)} s more` : ""}.${servedVersusWeight(group)}${replicas}`,
          deploymentIds: ids, zones: zonesOf(group), movedOn: movedOn(ids) });
      } else if (kind === "slow") {
        const p95 = group.length > 3 ? `up to ${durationLabel(Math.max(...group.map((item) => item.p95TtfbMs ?? 0)))}`
          : group.map((item) => `${group.length > 1 ? `${labels.get(item.id) ?? modelShort(item.modelKey)} ` : ""}${durationLabel(item.p95TtfbMs)}`).join(", ");
        issues.push({ key: `${account}:${kind}`, kind, tone: "warning", title: `${name} is slow`,
          detail: `p95 time to first byte ${p95}, over twice its peers. The LB ranks it last and sends it only probes.${servedVersusWeight(group)}`,
          deploymentIds: ids, zones: zonesOf(group), movedOn: movedOn(ids) });
      } else if (kind === "open" || kind === "probing") {
        const probe = Math.min(...entries.map((entry) => entry.visual.probeInMs ?? Infinity));
        issues.push({ key: `${account}:${kind}`, kind, tone: "critical", title: `${name} ${group.length > 1 ? "circuits are" : "circuit is"} open`,
          detail: kind === "probing" ? "Failing requests opened the circuit. One probe request is testing it now."
            : `Failing requests opened the circuit. ${Number.isFinite(probe) ? `Next probe in ${seconds(probe)} s.` : ""}`,
          deploymentIds: ids, zones: zonesOf(group), movedOn: movedOn(ids) });
      } else {
        issues.push({ key: `${account}:${kind}`, kind, tone: "warning", title: `${name} is disabled`,
          detail: "Drained by the override file. The LB sends it nothing.", deploymentIds: ids, zones: zonesOf(group), movedOn: 0 });
      }
    }
  }
  return issues.sort((a, b) => kindRank[a.kind] - kindRank[b.kind] || b.movedOn - a.movedOn || a.title.localeCompare(b.title));
}

export interface Verdict { tone: Tone; title: string; kind: IssueKind | null }

const compact = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 });

/** Formats a request count: "8", "940", "1.2K", "3.4M". */
export function count(value: number): string {
  return compact.format(value);
}

/** Formats a request rate, in the unit that keeps it above 0.1: "12/s", "0.4/s", "0.5/min", "2/h", "< 0.1/h". */
export function rate(perSecond: number): string {
  if (perSecond <= 0) return "0/s";
  if (perSecond * 3600 < 0.1) return "<\u202f0.1/h";
  const [value, unit] = perSecond >= 0.1 ? [perSecond, "s"] : perSecond * 60 >= 0.1 ? [perSecond * 60, "min"] : [perSecond * 3600, "h"];
  return `${value.toFixed(value < 10 ? 1 : 0).replace(/\.0$/, "")}/${unit}`;
}

/**
 * Formats a share of requests: "12 %", "0.4 %", "< 0.1 %", "99.7 %". A share is never rounded to 0 % or 100 %
 * unless it is exactly that, so a single failure stays visible.
 */
export function percent(share: number): string {
  const value = share * 100;
  const unit = "\u202f%";
  if (value <= 0) return `0${unit}`;
  if (value >= 100) return `100${unit}`;
  if (value < 0.1) return `<\u202f0.1${unit}`;
  if (value > 99.9) return `>\u202f99.9${unit}`;
  if (value < 9.95 || value > 99) return `${(Math.floor(value * 10) / 10).toFixed(1).replace(/\.0$/, "")}${unit}`;
  return `${Math.round(value)}${unit}`;
}

/**
 * The answer to "is anything wrong right now?" in one line. The header's figures carry the impact, so the line names
 * only the problem, and its tone also reflects lost requests that no deployment state explains.
 */
export function verdict(issues: Issue[], funnel: Funnel, deployments: number): Verdict {
  const lost = funnel.total === 0 ? 0 : (funnel.refused + funnel.failed) / funnel.total;
  const top = issues[0];
  if (!top) {
    return {
      // A stray failure is not news; a percent of lost requests is.
      tone: lost >= 0.01 ? "critical" : lost >= 0.005 ? "warning" : "ok",
      title: deployments === 0 ? "Waiting for the load balancer" : `All ${deployments} deployments healthy`, kind: null,
    };
  }
  const tone: Tone = issues.some((issue) => issue.tone === "critical") || lost >= 0.01 ? "critical" : "warning";
  const down = issues.filter((issue) => issue.kind === "down");
  const zones = new Set(down.flatMap((issue) => issue.zones));
  const zone = zones.size === 1 ? [...zones][0] : undefined;
  const title = down.length > 1 ? `${down.length} ${zone && zone !== "global" ? `${zone.toUpperCase()} ` : ""}accounts are down`
    : issues.length === 1 ? top.title : `${top.title}, and ${issues.length - 1} more`;
  return { tone, title, kind: top.kind };
}
