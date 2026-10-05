import { MotionConfig } from "motion/react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { startSource, type Connection, type Source } from "./data/source";
import { findIssues, verdict } from "./model/attention";
import { PlaybackClock } from "./model/clock";
import { isScenario } from "./model/demo";
import { computeFunnel, inScope, placeKey, type Funnel, type Scope } from "./model/funnel";
import { HistoryCache, isRange, ranges, TAIL_RESOLUTION, type RangeKey } from "./model/history";
import { groupKey, layoutScene, type View } from "./model/layout";
import { poolLabels } from "./model/names";
import { nodeVisual, requestTone } from "./model/state";
import { stripBins } from "./model/strip";
import { Timeline, WINDOW_MS, type FrameState, type Tick, type TimedRequest } from "./model/timeline";
import type { DashboardDeployment, MergedDeployment, RequestRecord } from "./model/types";
import { Scene } from "./scene/Scene";
import { applyPalette, palettes, type ThemeName } from "./theme";
import { Feed, type FeedMode } from "./ui/Feed";
import { Chain, Insights } from "./ui/Insights";
import { LoadingState } from "./ui/LoadingState";
import { Scrubber } from "./ui/Scrubber";
import { TopBar, type Filters } from "./ui/TopBar";

/** Sampled requests the feed groups into at most 40 rows of identical runs. */
const FEED_REQUESTS = 400;
/** In the live two minutes, shares compare with the same window this much earlier; longer windows compare with the one before. */
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

const initialRange: RangeKey = isRange(params.get("range")) ? (params.get("range") as RangeKey) : "2m";

function initialWindow(range: RangeKey): number {
  const value = Number(params.get("window"));
  return (ranges[range].windows as readonly number[]).includes(value) ? value : ranges[range].window;
}

/** Below this share of requests a healthy deployment counts as quiet; it needs this much to count as busy again. */
const IDLE_SHARE = 0.005;
const BUSY_SHARE = 0.01;

/**
 * Healthy deployments that took (almost) no traffic in the window: they fold into their group's idle lane. The two
 * thresholds keep a deployment that sees the odd request from switching back and forth every few seconds. A deployment
 * with a problem never folds, because it is the news.
 */
function idleDeployments(previous: ReadonlySet<string>, frame: FrameState | null, funnel: Funnel, t: number): ReadonlySet<string> {
  if (!frame) return previous;
  const next = new Set<string>();
  for (const [id, item] of frame.deployments) {
    if (nodeVisual(item, frame.throttledSince.get(id), t).look !== "healthy") continue;
    const reached = funnel.total === 0 ? 0 : (funnel.deployments.get(id)?.reached ?? 0) / funnel.total;
    if (reached < IDLE_SHARE || (previous.has(id) && reached < BUSY_SHARE)) next.add(id);
  }
  return sameSet(next, previous) ? previous : next;
}

/** A recovered deployment keeps its problem lane this long, so a deployment that flaps does not move every lane each time. */
const PROBLEM_HOLD_MS = 15_000;

/**
 * Deployments that have a problem, or had one in the last PROBLEM_HOLD_MS, with the problem and when it was last seen.
 * Throttling comes and goes every few seconds under load; the layout follows this steadier set, the colours follow the
 * live state.
 */
function heldProblems(previous: ReadonlyMap<string, { look: string; seen: number }>, frame: FrameState | null, t: number) {
  const next = new Map<string, { look: string; seen: number }>();
  for (const [id, item] of frame?.deployments ?? []) {
    const look = nodeVisual(item, frame?.throttledSince.get(id), t).look;
    // Open and probing are one problem, so a probe does not split an outage's lane.
    if (look !== "healthy") next.set(id, { look: look === "halfOpen" ? "open" : look, seen: t });
  }
  for (const [id, held] of previous) if (!next.has(id) && t - held.seen < PROBLEM_HOLD_MS && t >= held.seen) next.set(id, held);
  // Same set and problems, and every live problem seen within 5 s: keep the old map, so React re-renders at most every 5 s.
  const same = next.size === previous.size && [...next].every(([id, held]) => {
    const old = previous.get(id);
    return old?.look === held.look && held.seen - old.seen < 5000;
  });
  return same ? previous : next;
}

function sameSet(a: ReadonlySet<string>, b: ReadonlySet<string>): boolean {
  return a.size === b.size && [...a].every((id) => b.has(id));
}

/** A request's attempt chain in the same terms as a fallback path, so a path can filter the feed. */
function pathKey(request: RequestRecord): string {
  const outcome = request.status < 400 ? "served" : String(request.status);
  return `${request.attempts.map((attempt) => `${attempt.deploymentId ?? attempt.deployment}~${attempt.healthOutcome}`).join(">")}|${outcome}`;
}

export function App() {
  const timeline = useMemo(() => new Timeline(), []);
  const clock = useMemo(() => new PlaybackClock(timeline), [timeline]);
  const source = useRef<Source | null>(null);
  const [connection, setConnection] = useState<Connection>({ kind: "connecting" });
  // Client time of the latest React tick; the scene reads the clock itself every frame.
  const [now, setNow] = useState(() => Date.now());
  const refresh = useCallback(() => setNow(Date.now()), []);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [filters, setFilters] = useState<Filters>({ model: params.get("model") ?? "", zone: params.get("zone") ?? "", caller: params.get("caller") ?? "" });
  const [view, setView] = useState<View>(params.get("view") === "model" ? "model" : "region");
  const [range, setRange] = useState<RangeKey>(initialRange);
  const [windowSeconds, setWindowSeconds] = useState<number>(() => initialWindow(initialRange));
  const [feedMode, setFeedMode] = useState<FeedMode>(params.get("feed") === "all" ? "all" : "notable");
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [focus, setFocus] = useState<string[] | null>(null);
  const [idle, setIdle] = useState<ReadonlySet<string>>(() => new Set());
  const [held, setHeld] = useState<ReadonlyMap<string, { look: string; seen: number }>>(() => new Map());
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  // The range a cache was fetched for: the service clamps a range to its retention, so a short answer is still the answer.
  const [fetched, setHistory] = useState<{ range: RangeKey; cache: HistoryCache } | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const prefersDark = useMedia("(prefers-color-scheme: dark)");
  const reducedMotion = useMedia("(prefers-reduced-motion: reduce)");
  const [themeChoice, setThemeChoice] = useState<ThemeName | null>(() => {
    const forced = params.get("theme");
    return forced === "dark" || forced === "light" ? forced : storedTheme();
  });
  const theme: ThemeName = themeChoice ?? (prefersDark ? "dark" : "light");

  useEffect(() => applyPalette(theme), [theme]);
  useEffect(() => {
    const started = startSource(timeline, isScenario(scenario) ? scenario : null, setConnection);
    source.current = started;
    return () => {
      started.stop();
      source.current = null;
    };
  }, [timeline]);
  // React reads the timeline four times a second; the scene itself animates every frame.
  useEffect(() => {
    const timer = window.setInterval(refresh, 250);
    return () => window.clearInterval(timer);
  }, [refresh]);

  // The strip's range decides how far back the playhead may go and which history to fetch.
  const { spanMs, resolution } = ranges[range];
  clock.setRange(spanMs, timeline.retention ? (spanMs > 3_600_000 ? timeline.retention.minutes : timeline.retention.seconds) : null);
  const ready = timeline.latestAt !== null;
  useEffect(() => {
    if (range === "2m" || !ready) return;
    let cancelled = false;
    let loaded = false;
    let loadedTo = -Infinity;
    const load = () => {
      // While the operator looks at the past, the fetched range holds still under the playhead, until the live ticks
      // move on past its end and would leave a gap between the two.
      if (loaded && clock.mode !== "live" && loadedTo >= (timeline.ticks[0]?.at ?? Infinity) - 1000) return;
      const to = Date.now() + timeline.clockOffset;
      // A paused playhead and its window stay inside the fetch, however far wall time has moved on (within 1500 buckets).
      const paused = clock.mode === "live" ? to : clock.time(Date.now()) - windowSeconds * 1000;
      const from = Math.max(to - 1500 * resolution * 1000, Math.min(to - spanMs, paused));
      const fetch = source.current?.history;
      if (!fetch) return;
      // Coarse buckets leave up to one bucket before the fetch uncovered; a fine tail covers it (see HistoryCache).
      const tailFrom = Math.floor(to / (resolution * 1000)) * resolution * 1000 - resolution * 1000;
      Promise.all([fetch(from, to, resolution), resolution > TAIL_RESOLUTION * 6 ? fetch(tailFrom, to, TAIL_RESOLUTION) : Promise.resolve(undefined)]).then(([response, tail]) => {
        if (cancelled) return;
        loaded = true;
        const cache = new HistoryCache(response, tail);
        loadedTo = cache.to;
        setHistory({ range, cache });
        setHistoryError(null);
      }, (error: unknown) => {
        if (!cancelled) setHistoryError(error instanceof Error ? error.message : "History unavailable");
      });
    };
    load();
    const timer = window.setInterval(load, Math.min(60_000, Math.max(10_000, resolution * 1000)));
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [range, ready, spanMs, resolution, windowSeconds, clock, timeline]);

  useEffect(() => {
    const url = new URL(window.location.href);
    const state: Record<string, string> = {
      ...filters, view: view === "model" ? "model" : "", range: range === "2m" ? "" : range,
      window: windowSeconds === ranges[range].window ? "" : String(windowSeconds), feed: feedMode === "all" ? "all" : "",
    };
    for (const [key, value] of Object.entries(state)) {
      if (value) url.searchParams.set(key, value);
      else url.searchParams.delete(key);
    }
    window.history.replaceState(null, "", url);
  }, [filters, view, range, windowSeconds, feedMode]);

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

  // The live two minutes need no history; a fetched range only counts while its range is selected.
  const history = range !== "2m" && fetched?.range === range ? fetched.cache : null;
  const t = clock.time(now);
  const live = clock.mode === "live";
  // Live ticks cover the last two minutes exactly; history buckets cover what lies before them.
  const liveFrom = timeline.ticks[0]?.at ?? Infinity;
  const seam = history?.handoff() ?? -Infinity;
  const ticksIn = (from: number, to: number): Tick[] =>
    [...(history?.ticksBetween(from, to, seam) ?? []), ...timeline.ticksBetween(Math.max(from, seam), to)];
  const liveFrame = timeline.stateAt(t);
  const pastFrame = history && t < (timeline.frames[0]?.at ?? Infinity) ? history.frameAt(t, liveFrame?.replicas ?? []) : null;
  const frame = pastFrame ?? liveFrame;

  const known = new Map<string, MergedDeployment>();
  for (const item of history?.deployments ?? []) known.set(item.deployment.id, item);
  for (const item of timeline.knownDeployments()) known.set(item.deployment.id, item);
  const deployments = [...known.values()];
  const deploymentList = deployments.map((item) => item.deployment);
  const byId = new Map(deploymentList.map((item) => [item.id, item]));
  const labelKey = deploymentList.map((item) => `${item.id}:${item.modelKey}:${item.deployment}`).join("|");
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const labels = useMemo(() => poolLabels(deploymentList), [labelKey]);
  const groupOf = useCallback((deployment: DashboardDeployment) => (view === "region" ? placeKey(deployment) : groupKey("model", deployment, labels)), [view, labels]);

  const scope: Scope = filters;
  const bounds = clock.bounds(now);
  // Over history, the window never claims more than the data behind it: a 1 h window 30 min into an hour of data covers 30 min.
  // Until the detail arrives (or if it fails), only the live ticks are data, and the window says so.
  // The loaded history's own start holds still while paused, so the figures for a paused moment do not drift.
  const dataFrom = history ? history.from : Math.max(bounds.from, liveFrom);
  // Over history the window ends on a bucket boundary, so it covers whole buckets and says exactly which interval they
  // are (windows are whole multiples of their range's bucket). Live, it ends at the playhead.
  const bucketMs = resolution * 1000;
  const windowEnd = history && t <= seam ? Math.floor(t / bucketMs) * bucketMs : t;
  const windowMs = range === "2m" ? windowSeconds * 1000 : Math.max(1000, Math.min(windowSeconds * 1000, windowEnd - dataFrom));
  // Deployment flows decide what folds; group flows are recomputed below once the layout says how deployments are grouped.
  const flows = computeFunnel(ticksIn(windowEnd - windowMs, windowEnd), deploymentList, scope, groupOf);
  const compareMs = range === "2m" ? COMPARE_MS : windowMs;
  const earliestData = range === "2m" ? (timeline.ticks[0]?.at ?? Infinity) : Math.min(history?.from ?? Infinity, liveFrom);
  // The comparison needs the whole earlier window inside the retained data.
  const comparable = range === "2m"
    ? earliestData <= t - COMPARE_MS - windowMs + 1000 && t - COMPARE_MS - windowMs >= t - WINDOW_MS - 5000
    : earliestData <= t - compareMs - windowMs + resolution * 1000;

  // Callers come from exact counts as well as samples, so a quiet caller still has its place.
  const callers = [...new Set([...timeline.ticks.flatMap((tick) => tick.routes.map((route) => route.caller ?? "unknown")),
    ...(history?.ticks ?? []).flatMap((tick) => tick.routes.map((route) => route.caller ?? "unknown")),
    ...timeline.requests.map((item) => item.request.caller ?? "unknown")])].sort();
  const rem = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
  // Derived state that depends on its own previous value (hysteresis): React allows setting it during render.
  const nextIdle = idleDeployments(idle, frame, flows, t);
  if (nextIdle !== idle) setIdle(nextIdle);
  const nextHeld = heldProblems(held, frame, t);
  if (nextHeld !== held) setHeld(nextHeld);
  const problems = new Map([...held].map(([id, entry]) => [id, entry.look]));
  const sorted = (set: ReadonlySet<string>) => [...set].sort().join("|");
  // The layout only moves when its structure changes; the key captures exactly that.
  const layoutKey = `${view}#${labelKey}#${deploymentList.map((item) => `${item.id}:${item.tier}:${item.region}:${item.zone}`).join("|")}#${callers.join("|")}#${sorted(idle)}#${[...problems].map((entry) => entry.join(":")).sort().join("|")}#${sorted(expanded)}#${size.width}x${size.height}@${rem}`;
  const layout = useMemo(() => layoutScene({ deployments: deploymentList, callers, size, rem, view, idle, problems, expanded }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [layoutKey]);
  // Group flows follow the drawn groups: in the model view, unused models share the "idle models" group.
  const drawnGroup = new Map(layout.nodes.flatMap((node) => node.members.map((id) => [id, node.place] as const)));
  const groupFn = view === "model" ? (deployment: DashboardDeployment) => drawnGroup.get(deployment.id) ?? groupOf(deployment) : groupOf;
  const funnel = view === "model" ? computeFunnel(ticksIn(windowEnd - windowMs, windowEnd), deploymentList, scope, groupFn) : flows;
  const previous = comparable ? computeFunnel(ticksIn(windowEnd - compareMs - windowMs, windowEnd - compareMs), deploymentList, scope, groupFn) : null;
  const issues = findIssues(frame, funnel, t, labels);
  const headline = verdict(issues, funnel, frame?.deployments.size ?? 0);

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

  // The feed lists live samples, then the history's samples from before them.
  const feed: TimedRequest[] = [];
  const keep = (item: TimedRequest) => {
    if (!requestFilter(item.request)) return;
    if (selectedPath ? pathKey(item.request) !== selectedPath : feedMode === "notable" && requestTone(item.request) === "ok") return;
    feed.push(item);
  };
  const liveRequestsFrom = timeline.requests[0]?.playAt ?? Infinity;
  for (let index = timeline.requests.length - 1; index >= 0 && feed.length < FEED_REQUESTS; index--) {
    const item = timeline.requests[index];
    if (item && item.playAt <= t) keep(item);
  }
  if (history && feed.length < FEED_REQUESTS)
    for (const item of history.requestsBetween(bounds.from, Math.min(t, liveRequestsFrom - 1))) {
      if (feed.length >= FEED_REQUESTS) break;
      keep(item);
    }
  const selected = selectedId
    ? (timeline.requests.find((item) => item.request.id === selectedId) ?? history?.requests.find((item) => item.request.id === selectedId))?.request ?? null
    : null;
  const path = selectedPath ? funnel.paths.find((item) => item.key === selectedPath) ?? null : null;
  const bins = stripBins({ range, from: bounds.from, to: bounds.to, ticks: ticksIn(bounds.from, bounds.to), history, summary: timeline.summary, frames: timeline.frames, scope });

  const changeRange = (next: RangeKey) => {
    setRange(next);
    setWindowSeconds(ranges[next].window);
    clock.setRange(ranges[next].spanMs, null);
    clock.goLive();
    refresh();
  };
  const toggleGroup = useCallback((key: string) => setExpanded((current) => {
    const next = new Set(current);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    return next;
  }), []);

  if (!ready && connection.kind !== "reconnecting") return <LoadingState />;

  return (
    <MotionConfig reducedMotion="user">
      <div className="app" data-live={live || undefined}>
        <TopBar connection={connection} staleSeconds={staleSeconds} replicas={frame?.replicas.length ?? 0} verdict={headline} funnel={funnel} previous={previous}
          filters={filters} options={options} onFilters={setFilters} range={range} windowSeconds={windowSeconds} coveredSeconds={Math.round(windowMs / 1000)} onWindow={setWindowSeconds}
          compareLabel={range === "2m" ? "a minute earlier" : "the window before"} view={view} onView={setView}
          viewing={live ? null : { t: windowEnd, windowMs, now: bounds.to }} onGoLive={() => { clock.goLive(); refresh(); }}
          theme={theme} onTheme={() => {
            const next = theme === "dark" ? "light" : "dark";
            setThemeChoice(next);
            try { window.localStorage.setItem("lb-dashboard-theme", next); } catch { /* storage may be blocked */ }
          }} />
        <main className="stage">
          <div className="scene-scroll">
            <Scene timeline={timeline} clock={clock} layout={layout} deployments={deployments} frame={frame} t={t}
              funnel={funnel} previous={previous} palette={palettes[theme]} reducedMotion={reducedMotion}
              requestFilter={requestFilter} nodeFilter={nodeFilter} focus={focus} labels={labels} onToggleGroup={toggleGroup} pastFrame={pastFrame}
              replicaNumbers={timeline.replicaNumbers}
              selectedId={selectedId} onSelect={setSelectedId} onResize={setSize} />
          </div>
          <Scrubber clock={clock} bins={bins} from={bounds.from} to={bounds.to} t={t} mode={clock.mode} range={range} onRange={changeRange}
            windowMs={windowMs} windowEnd={windowEnd} compareMs={previous ? compareMs : null} loading={range !== "2m" && !history && !historyError} error={historyError}
            onChange={refresh} />
        </main>
        <aside className="panel" aria-label="Attention, fallbacks and requests">
          <Insights issues={issues} funnel={funnel} byId={byId} labels={labels} windowSeconds={Math.round(windowMs / 1000)} selectedPath={selectedPath}
            onFocus={setFocus} onPath={setSelectedPath} />
          <Feed items={feed} t={t} mode={feedMode} onMode={setFeedMode} byId={byId} labels={labels}
            pathLabel={path ? <Chain hops={path.hops} status={path.status} byId={byId} /> : selectedPath ? "the selected path" : null}
            onClearPath={() => setSelectedPath(null)} selected={selected} onSelect={setSelectedId} />
        </aside>
      </div>
    </MotionConfig>
  );
}
