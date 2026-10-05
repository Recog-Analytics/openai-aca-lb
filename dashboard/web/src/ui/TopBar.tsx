import { AnimatePresence, motion } from "motion/react";
import type { Connection } from "../data/source";
import { percent, type Verdict } from "../model/attention";
import type { Funnel } from "../model/funnel";
import type { ThemeName } from "../theme";

export interface Filters { model: string; zone: string; caller: string }

/** Funnel windows the operator can choose; each compares with the same window one minute earlier. */
export const windows = [10, 30, 60] as const;
export type WindowSeconds = (typeof windows)[number];

interface Props {
  connection: Connection;
  /** Seconds since the last frame while live; above a few seconds the page says so. */
  staleSeconds: number;
  replicas: number;
  verdict: Verdict;
  funnel: Funnel;
  previous: Funnel | null;
  filters: Filters;
  options: { model: string[]; zone: string[]; caller: string[] };
  onFilters: (filters: Filters) => void;
  windowSeconds: WindowSeconds;
  onWindow: (seconds: WindowSeconds) => void;
  theme: ThemeName;
  onTheme: () => void;
}

const scenarioNames: Record<string, string> = {
  calm: "calm", "sweden-slow": "Sweden slow", "france-throttled": "France throttled", "eastus2-outage": "East US 2 outage", "eu-down": "EU down",
};

const STALE_SECONDS = 5;
const swap = { type: "spring", duration: 0.35, bounce: 0 } as const;

export function TopBar(props: Props) {
  const { connection, staleSeconds, replicas, verdict, funnel, previous, filters, options, onFilters, windowSeconds, onWindow, theme, onTheme } = props;
  const status = connection.kind === "demo" ? `Demo: ${scenarioNames[connection.scenario] ?? connection.scenario}`
    : connection.kind === "live" && staleSeconds >= STALE_SECONDS ? `No data for ${staleSeconds} s. Figures are as of then.`
    : connection.kind === "live" ? (replicas === 0 ? "Connected, no LB replica reporting" : replicas === 1 ? "Live from 1 replica" : `Live from ${replicas} replicas`)
    : connection.kind === "connecting" ? "Connecting to the stream"
    : connection.failures > 3 ? "Stream unavailable. Reload the page to sign in again." : "Reconnecting";
  const share = (f: Funnel | null, pick: (f: Funnel) => number) => (f && f.total > 0 ? pick(f) / f.total : null);
  const rate = (f: Funnel | null) => (f && f.seconds > 0 ? f.total / f.seconds : null);
  const lost = (f: Funnel) => f.failed + f.refused;
  const spilled = (f: Funnel) => [...f.tiers.values()].reduce((sum, tier) => sum + tier.spilled, 0);
  return (
    <header className="top">
      <div className="toolbar">
        <div className="brand">
          <h1>Load balancer</h1>
          <p className="connection" data-kind={connection.kind} data-stale={staleSeconds >= STALE_SECONDS || undefined} role="status">{status}</p>
        </div>
        <div className="controls">
          {(["model", "zone", "caller"] as const).map((key) => (
            <label key={key} className="filter" data-active={filters[key] !== "" || undefined}>
              <span className="visually-hidden">{key === "model" ? "Model" : key === "zone" ? "Zone" : "Caller"}</span>
              <select value={filters[key]} onChange={(event) => onFilters({ ...filters, [key]: event.target.value })}>
                <option value="">{key === "model" ? "All models" : key === "zone" ? "All zones" : "All callers"}</option>
                {options[key].map((value) => <option key={value} value={value}>{value}</option>)}
              </select>
            </label>
          ))}
          <div className="segmented" role="radiogroup" aria-label="Window for traffic shares">
            {windows.map((seconds) => (
              <button key={seconds} type="button" role="radio" aria-checked={seconds === windowSeconds} onClick={() => onWindow(seconds)}>
                {seconds === 60 ? "1 min" : `${seconds} s`}
              </button>
            ))}
          </div>
          <button type="button" className="icon-button" onClick={onTheme} aria-label={theme === "dark" ? "Use light theme" : "Use dark theme"}>
            {theme === "dark"
              ? <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true"><circle cx="10" cy="10" r="3.6" fill="none" stroke="currentColor" strokeWidth="1.5" /><path d="M10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M4.7 15.3l1.4-1.4M13.9 6.1l1.4-1.4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>
              : <svg viewBox="0 0 20 20" width="18" height="18" aria-hidden="true"><path d="M15.5 12.6A6.5 6.5 0 0 1 7.4 4.5a6.5 6.5 0 1 0 8.1 8.1z" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" /></svg>}
          </button>
        </div>
      </div>
      {/* The verdict and the figures that size it read as one line: what is wrong, then how much it costs. */}
      <div className="headline">
        <div className="verdict" data-tone={verdict.tone} data-kind={verdict.kind ?? undefined} aria-live="polite">
          {/* The verdict changes rarely; when it does, the old line leaves downward and the new one arrives. */}
          <AnimatePresence mode="popLayout" initial={false}>
            <motion.h2 key={verdict.title} initial={{ opacity: 0, y: -10 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: 8 }} transition={swap}>
              <span className="verdict-mark" aria-hidden="true" />
              {verdict.title}
            </motion.h2>
          </AnimatePresence>
        </div>
        <div className="kpi-block">
          <dl className="kpis" aria-describedby="kpi-caption">
            <Kpi label="Served" value={share(funnel, (f) => f.served)} before={share(previous, (f) => f.served)} format={percent} unit="share" good="up" />
            <Kpi label="Failed or refused" value={share(funnel, lost)} before={share(previous, lost)} format={percent} unit="share" good="down"
              alert={funnel.total > 0 && lost(funnel) / funnel.total >= 0.005} />
            <Kpi label="After a retry" value={share(funnel, (f) => f.fellBack)} before={share(previous, (f) => f.fellBack)} format={percent} unit="share" good="down" />
            <Kpi label="Spilled to a lower tier" value={share(funnel, spilled)} before={share(previous, spilled)} format={percent} unit="share" good="down" />
            <Kpi label="Requests" value={rate(funnel)} before={rate(previous)} format={(v) => `${v.toFixed(v < 10 ? 1 : 0)}/s`} unit="rate" />
          </dl>
          <p id="kpi-caption" className="kpi-caption">
            Last {windowSeconds === 60 ? "minute" : `${windowSeconds} s`}{previous ? ", change against a minute earlier" : ". Changes appear once a minute of history is in."}
          </p>
        </div>
      </div>
    </header>
  );
}

function Kpi({ label, value, before, format, unit, good, alert }: {
  label: string; value: number | null; before: number | null; format: (value: number) => string;
  unit: "rate" | "share"; good?: "up" | "down"; alert?: boolean;
}) {
  const change = value !== null && before !== null ? value - before : null;
  const significant = change !== null && (unit === "share" ? Math.abs(change) >= 0.01 : before !== null && before > 0 && Math.abs(change) / before >= 0.1);
  const direction = change !== null && change > 0 ? "up" : "down";
  const tone = !significant || !good ? "neutral" : direction === good ? "good" : "bad";
  return (
    <div data-alert={alert || undefined}>
      <dt>{label}</dt>
      <dd>
        <span className="kpi-value">{value === null ? "—" : format(value)}</span>
        <span className="kpi-change" data-tone={tone}>
          {!significant || change === null ? "" : unit === "share"
            ? `${change > 0 ? "+" : "−"}${Math.round(Math.abs(change) * 100) || "<1"} pts`
            : `${change > 0 ? "+" : "−"}${Math.abs(change).toFixed(1)}/s`}
        </span>
      </dd>
    </div>
  );
}
