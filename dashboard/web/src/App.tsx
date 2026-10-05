import { MotionConfig } from "motion/react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { startSource, type Connection } from "./data/source";
import { findIssues, verdict } from "./model/attention";
import { PlaybackClock } from "./model/clock";
import { isScenario } from "./model/demo";
import { computeFunnel, inScope, type Funnel, type Scope } from "./model/funnel";
import { layoutScene } from "./model/layout";
import { nodeVisual, requestTone } from "./model/state";
import { Timeline, WINDOW_MS, type FrameState, type TimedRequest } from "./model/timeline";
import type { DashboardDeployment, RequestRecord } from "./model/types";
import { Scene } from "./scene/Scene";
import { applyPalette, palettes, type ThemeName } from "./theme";
import { Feed, type FeedMode } from "./ui/Feed";
import { Chain, Insights } from "./ui/Insights";
import { Scrubber } from "./ui/Scrubber";
import { TopBar, windows, type Filters, type WindowSeconds } from "./ui/TopBar";

/** Sampled requests the feed groups into at most 40 rows of identical runs. */
const FEED_REQUESTS = 400;
/** Shares compare with the same window this much earlier. */
const COMPARE_MS = 60_000;
const params = new URLSearchParams(window.location.search);
const scenario = params.get("demo");

function storedTheme(): ThemeName | null {
  try {
    const value = window.localStorage.getItem("lb-dashboard-theme");
    return value === "dark" || value === "light" ? value : null;
  } catch {
    return null;
  }
}

function useMedia(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const media = window.matchMedia(query);
    const listener = () => setMatches(media.matches);
    media.addEventListener("change", listener);
    return () => media.removeEventListener("change", listener);
  }, [query]);
  return matches;
}

function initialWindow(): WindowSeconds {
  const value = Number(params.get("window"));
  return windows.find((item) => item === value) ?? 30;
}

/** Below this share of requests a healthy deployment counts as idle; it needs this much to count as busy again. */
const IDLE_SHARE = 0.002;
const BUSY_SHARE = 0.01;

/**
 * Healthy deployments that took (almost) no traffic in the window: unused fallbacks, drawn compact. The two thresholds
 * keep a fallback that sees the odd request from switching size every few seconds. A deployment with a problem never
 * goes compact, because it is the news.
 */
function idleFallbacks(previous: ReadonlySet<string>, frame: FrameState | null, funnel: Funnel, t: number): ReadonlySet<string> {
  if (!frame || funnel.total === 0) return previous;
  const next = new Set<string>();
  for (const [id, item] of frame.deployments) {
    if (nodeVisual(item, frame.throttledSince.get(id), t).look !== "healthy") continue;
    const reached = (funnel.deployments.get(id)?.reached ?? 0) / funnel.total;
    if (reached < IDLE_SHARE || (previous.has(id) && reached < BUSY_SHARE)) next.add(id);
  }
  return next.size === previous.size && [...next].every((id) => previous.has(id)) ? previous : next;
}

/** A request's attempt chain in the same terms as a fallback path, so a path can filter the feed. */
function pathKey(request: RequestRecord): string {
  const outcome = request.status < 400 ? "served" : String(request.status);
  return `${request.attempts.map((attempt) => `${attempt.deploymentId ?? attempt.deployment}~${attempt.healthOutcome}`).join(">")}|${outcome}`;
}

export function App() {
  const timeline = useMemo(() => new Timeline(), []);
  const [compact, setCompact] = useState<ReadonlySet<string>>(() => new Set());
  const clock = useMemo(() => new PlaybackClock(timeline), [timeline]);
  const [connection, setConnection] = useState<Connection>({ kind: "connecting" });
  // Client time of the latest React tick; the scene reads the clock itself every frame.
  const [now, setNow] = useState(() => Date.now());
  const refresh = useCallback(() => setNow(Date.now()), []);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [filters, setFilters] = useState<Filters>({ model: params.get("model") ?? "", zone: params.get("zone") ?? "", caller: params.get("caller") ?? "" });
  const [windowSeconds, setWindowSeconds] = useState<WindowSeconds>(initialWindow);
  const [feedMode, setFeedMode] = useState<FeedMode>(params.get("feed") === "all" ? "all" : "notable");
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [focus, setFocus] = useState<string[] | null>(null);
  const prefersDark = useMedia("(prefers-color-scheme: dark)");
  const reducedMotion = useMedia("(prefers-reduced-motion: reduce)");
  const [themeChoice, setThemeChoice] = useState<ThemeName | null>(() => {
    const forced = params.get("theme");
    return forced === "dark" || forced === "light" ? forced : storedTheme();
  });
  const theme: ThemeName = themeChoice ?? (prefersDark ? "dark" : "light");

  useEffect(() => applyPalette(theme), [theme]);
  useEffect(() => startSource(timeline, isScenario(scenario) ? scenario : null, setConnection), [timeline]);
  // React reads the timeline four times a second; the scene itself animates every frame.
  useEffect(() => {
    const timer = window.setInterval(refresh, 250);
    return () => window.clearInterval(timer);
  }, [refresh]);

  useEffect(() => {
    const url = new URL(window.location.href);
    const state: Record<string, string> = { ...filters, window: windowSeconds === 30 ? "" : String(windowSeconds), feed: feedMode === "all" ? "all" : "" };
    for (const [key, value] of Object.entries(state)) {
      if (value) url.searchParams.set(key, value);
      else url.searchParams.delete(key);
    }
    window.history.replaceState(null, "", url);
  }, [filters, windowSeconds, feedMode]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      if (target?.closest("input, select, textarea, [role=slider]")) return;
      if (event.key === " " && !target?.closest("button")) {
        event.preventDefault();
        if (clock.mode === "paused") clock.resume(Date.now());
        else clock.pause(Date.now());
        refresh();
      } else if (event.key === "Escape") {
        setSelectedId(null);
        setSelectedPath(null);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [clock, refresh]);

  const t = clock.time(now);
  const frame = timeline.stateAt(t);
  const deployments = timeline.knownDeployments();
  const deploymentList = deployments.map((item) => item.deployment);
  const byId = new Map(deploymentList.map((item) => [item.id, item]));
  const scope: Scope = filters;
  const windowMs = windowSeconds * 1000;
  const funnel = computeFunnel(timeline.ticksBetween(t - windowMs, t), deploymentList, scope);
  const earliest = timeline.ticks[0]?.at ?? Infinity;
  // The comparison needs the whole earlier window inside the retained history.
  const previous = earliest <= t - COMPARE_MS - windowMs + 1000 && t - COMPARE_MS - windowMs >= t - WINDOW_MS - 5000
    ? computeFunnel(timeline.ticksBetween(t - COMPARE_MS - windowMs, t - COMPARE_MS), deploymentList, scope) : null;
  const windowLabel = windowSeconds === 60 ? "minute" : `${windowSeconds}\u00a0s`;
  const issues = findIssues(frame, funnel, t);
  const headline = verdict(issues, funnel, frame?.deployments.size ?? 0);

  // Callers come from exact counts as well as samples, so a quiet caller still has its place.
  const callers = [...new Set([...timeline.ticks.flatMap((tick) => tick.routes.map((route) => route.caller ?? "unknown")),
    ...timeline.requests.map((item) => item.request.caller ?? "unknown")])].sort();
  const rem = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
  // Derived state that depends on its own previous value (hysteresis): React allows setting it during render.
  const nextCompact = idleFallbacks(compact, frame, funnel, t);
  if (nextCompact !== compact) setCompact(nextCompact);
  // The layout only moves when deployments, callers, idle fallbacks or the scene size change; the key captures exactly those.
  const layoutKey = `${deploymentList.map((item) => `${item.id}:${item.tier}:${item.region}:${item.zone}`).join("|")}#${callers.join("|")}#${[...compact].sort().join("|")}#${size.width}x${size.height}@${rem}`;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const layout = useMemo(() => layoutScene(deploymentList, callers, size, rem, compact), [layoutKey]);

  // The service sends a frame every second; a gap means the numbers on screen are getting old.
  const latest = timeline.latestAt;
  const staleSeconds = connection.kind === "live" && latest !== null ? Math.floor((now + timeline.clockOffset - latest) / 1000) : 0;

  // A background tab still says what is wrong: the verdict in the title, its tone in the icon.
  useEffect(() => {
    document.title = `${headline.title} | Load balancer`;
    const color = headline.tone === "critical" ? palettes.dark.failed : headline.tone === "warning"
      ? (headline.kind === "throttled" ? palettes.dark.throttled : palettes.dark.degraded) : palettes.dark.healthy;
    const icon = document.querySelector<HTMLLinkElement>("link[rel=icon]");
    if (icon) icon.href = `data:image/svg+xml,${encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><circle cx="16" cy="16" r="11" fill="${color}"/></svg>`)}`;
  }, [headline.title, headline.tone, headline.kind]);

  const options = {
    model: [...new Set(deploymentList.map((item) => item.modelKey))].sort(),
    zone: [...new Set(deploymentList.map((item) => item.zone))].sort(),
    caller: callers,
  };

  const requestFilter = useCallback((request: RequestRecord) => inScope(request, filters), [filters]);
  const nodeFilter = useCallback((deployment: DashboardDeployment) =>
    (!filters.model || deployment.modelKey === filters.model) && (!filters.zone || filters.zone === "global" || deployment.zone === filters.zone),
  [filters]);

  const feed: TimedRequest[] = [];
  for (let index = timeline.requests.length - 1; index >= 0 && feed.length < FEED_REQUESTS; index--) {
    const item = timeline.requests[index];
    if (!item || item.playAt > t || !requestFilter(item.request)) continue;
    if (selectedPath ? pathKey(item.request) !== selectedPath : feedMode === "notable" && requestTone(item.request) === "ok") continue;
    feed.push(item);
  }
  const selected = selectedId ? (timeline.requests.find((item) => item.request.id === selectedId)?.request ?? null) : null;
  const path = selectedPath ? funnel.paths.find((item) => item.key === selectedPath) ?? null : null;

  return (
    <MotionConfig reducedMotion="user">
      <div className="app">
        <TopBar connection={connection} staleSeconds={staleSeconds} replicas={frame?.replicas.length ?? 0} verdict={headline} funnel={funnel} previous={previous}
          filters={filters} options={options} onFilters={setFilters} windowSeconds={windowSeconds} onWindow={setWindowSeconds}
          theme={theme} onTheme={() => {
            const next = theme === "dark" ? "light" : "dark";
            setThemeChoice(next);
            try { window.localStorage.setItem("lb-dashboard-theme", next); } catch { /* storage may be blocked */ }
          }} />
        <main className="stage">
          <div className="scene-scroll">
          <Scene timeline={timeline} clock={clock} layout={layout} deployments={deployments} frame={frame} t={t}
            funnel={funnel} previous={previous} palette={palettes[theme]} reducedMotion={reducedMotion}
            requestFilter={requestFilter} nodeFilter={nodeFilter} focus={focus}
            selectedId={selectedId} onSelect={setSelectedId} onResize={setSize} />
          </div>
          <Scrubber timeline={timeline} clock={clock} now={now} t={t} mode={clock.mode} scope={scope}
            windowMs={windowMs} compareMs={previous ? COMPARE_MS : null} onChange={refresh} />
        </main>
        <aside className="panel" aria-label="Attention, fallbacks and requests">
          <Insights issues={issues} funnel={funnel} byId={byId} windowLabel={windowLabel} selectedPath={selectedPath}
            onFocus={setFocus} onPath={setSelectedPath} />
          <Feed items={feed} t={t} mode={feedMode} onMode={setFeedMode} byId={byId}
            pathLabel={path ? <Chain hops={path.hops} status={path.status} byId={byId} /> : selectedPath ? "the selected path" : null}
            onClearPath={() => setSelectedPath(null)} selected={selected} onSelect={setSelectedId} />
        </aside>
      </div>
    </MotionConfig>
  );
}
