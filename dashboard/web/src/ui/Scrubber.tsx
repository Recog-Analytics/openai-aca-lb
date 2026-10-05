import { useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { percent } from "../model/attention";
import type { PlaybackClock, PlaybackMode } from "../model/clock";
import { inScope, isClientError, isServed, type Scope } from "../model/funnel";
import type { Timeline } from "../model/timeline";

interface Props {
  timeline: Timeline;
  clock: PlaybackClock;
  now: number;
  t: number;
  mode: PlaybackMode;
  scope: Scope;
  /** The funnel window and its comparison window, as brackets over the strip. */
  windowMs: number;
  /** Null until a minute of history allows a comparison. */
  compareMs: number | null;
  onChange: () => void;
}

const BIN_MS = 2000;

/** Smallest "nice" axis maximum (1, 2 or 5 times a power of ten) at or above `value`. */
export function niceCeiling(value: number): number {
  if (value <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  return ([1, 2, 5, 10].find((step) => step * power >= value) ?? 10) * power;
}

/**
 * The last two minutes as a chart of exact requests per second, in two-second bars: served first time, served after
 * a retry, failed or refused. Two bands mark the window the figures cover ("now") and the window they compare with.
 * Pointing at a bar reads it out; a click, a drag or the arrow keys replay from there.
 */
export function Scrubber({ timeline, clock, now, t, mode, scope, windowMs, compareMs, onChange }: Props) {
  const track = useRef<HTMLDivElement>(null);
  const [pointed, setPointed] = useState<number | null>(null);
  const { from, to } = clock.bounds(now);
  const span = Math.max(1, to - from);
  const bins = Math.max(1, Math.round(span / BIN_MS));
  const direct = new Array<number>(bins).fill(0);
  const retried = new Array<number>(bins).fill(0);
  const failed = new Array<number>(bins).fill(0);
  for (const tick of timeline.ticksBetween(from, to)) {
    const index = Math.min(bins - 1, Math.max(0, Math.floor((tick.at - from) / BIN_MS)));
    for (const route of tick.routes) {
      if (!inScope(route, scope)) continue;
      const target = isServed(route.status) ? (route.hops.length > 1 ? retried : direct) : isClientError(route.status) ? direct : failed;
      target[index] = (target[index] ?? 0) + route.count;
    }
  }
  const totals = direct.map((value, index) => value + (retried[index] ?? 0) + (failed[index] ?? 0));
  const perSecond = BIN_MS / 1000;
  const axisMax = niceCeiling(Math.max(...totals, 1) / perSecond);
  const position = Math.min(1, Math.max(0, (t - from) / span));
  const at = (time: number) => `${Math.min(100, Math.max(0, ((time - from) / span) * 100))}%`;
  const band = (end: number) => ({ left: at(end - windowMs), width: `calc(${at(end)} - ${at(end - windowMs)})` });
  const behind = Math.round((to - t) / 1000);
  const ticks = [120, 90, 60, 30].filter((seconds) => seconds * 1000 <= span);
  const pointedTotal = pointed === null ? 0 : (totals[pointed] ?? 0);

  const seekTo = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect) return;
    clock.seek(from + ((clientX - rect.left) / rect.width) * span, Date.now());
    onChange();
  };
  const binAt = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0) return null;
    return Math.min(bins - 1, Math.max(0, Math.floor(((clientX - rect.left) / rect.width) * bins)));
  };
  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    seekTo(event.clientX);
  };
  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    setPointed(binAt(event.clientX));
    if (event.currentTarget.hasPointerCapture(event.pointerId)) seekTo(event.clientX);
  };
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const step = event.shiftKey ? 15_000 : 5000;
    const moves: Record<string, number> = { ArrowLeft: t - step, ArrowRight: t + step, Home: from, End: to };
    const target = moves[event.key];
    if (target === undefined) return;
    event.preventDefault();
    clock.seek(target, Date.now());
    onChange();
  };
  const toggle = () => {
    if (clock.mode === "paused") clock.resume(Date.now());
    else clock.pause(Date.now());
    onChange();
  };

  return (
    <div className="scrubber">
      <button type="button" className="play" onClick={toggle} aria-label={mode === "paused" ? "Resume playback" : "Pause playback"}>
        {mode === "paused"
          ? <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M5 3.5v9l7-4.5z" fill="currentColor" /></svg>
          : <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M4.5 3.5h2.5v9H4.5zM9 3.5h2.5v9H9z" fill="currentColor" /></svg>}
      </button>
      <div className="strip">
        <div className="strip-head">
          <span className="strip-title">Requests per second</span>
          <ul className="legend" aria-label="Legend">
            <li data-kind="direct">Served first time</li>
            <li data-kind="retried">After a retry</li>
            <li data-kind="failed">Failed or refused</li>
            <li data-kind="now">Now, as in the figures</li>
            {compareMs !== null && <li data-kind="compare">A minute earlier</li>}
          </ul>
          <span className="hint">Click or drag to replay. <kbd>←</kbd> <kbd>→</kbd> step, <kbd>Space</kbd> pauses.</span>
        </div>
        <div ref={track} className="track" role="slider" tabIndex={0} aria-label="Playback position"
          aria-valuemin={-Math.round(span / 1000)} aria-valuemax={0} aria-valuenow={-behind}
          aria-valuetext={mode === "live" ? "Live" : `${behind} seconds behind live`}
          onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerLeave={() => setPointed(null)} onKeyDown={onKeyDown}>
          <div className="gridline" data-at="top" aria-hidden="true"><span>{axisMax}/s</span></div>
          <div className="gridline" data-at="middle" aria-hidden="true"><span>{axisMax / 2}/s</span></div>
          {compareMs !== null && <div className="window" data-kind="compare" style={band(t - compareMs)} aria-hidden="true" />}
          <div className="window" style={band(t)} aria-hidden="true" />
          <div className="bins" aria-hidden="true">
            {totals.map((total, index) => (
              <span key={index} className="bin" data-pointed={pointed === index || undefined}
                style={{ height: total === 0 ? "1px" : `${Math.min(100, (100 * total) / perSecond / axisMax)}%` }}>
                <span className="bin-failed" style={{ flexGrow: failed[index] ?? 0 }} />
                <span className="bin-retried" style={{ flexGrow: retried[index] ?? 0 }} />
                <span className="bin-direct" style={{ flexGrow: direct[index] ?? 0 }} />
              </span>
            ))}
          </div>
          <div className="playhead" aria-hidden="true" style={{ left: `${position * 100}%` }} />
          {pointed !== null && (
            <div className="readout" aria-hidden="true" data-side={pointed > bins / 2 ? "left" : "right"}
              style={{ left: `${((pointed + 0.5) / bins) * 100}%` }}>
              <strong>{Math.max(0, Math.round((to - from - (pointed + 1) * BIN_MS) / 1000))} s ago</strong>
              <span>{(pointedTotal / perSecond).toFixed(1)} req/s</span>
              {(retried[pointed] ?? 0) > 0 && <span data-kind="retried">{percent((retried[pointed] ?? 0) / pointedTotal)} after a retry</span>}
              {(failed[pointed] ?? 0) > 0 && <span data-kind="failed">{percent((failed[pointed] ?? 0) / pointedTotal)} failed or refused</span>}
            </div>
          )}
        </div>
        <div className="axis" aria-hidden="true">
          {ticks.map((seconds) => <span key={seconds} style={{ left: at(to - seconds * 1000) }}>{seconds === 60 ? "1 min" : seconds === 120 ? "2 min" : `${seconds} s`} ago</span>)}
          <span style={{ left: "100%" }} data-end>now</span>
        </div>
      </div>
      <div className="when">
        {mode === "live" ? <span className="live-dot">Live</span> : <span>{mode === "paused" ? "Paused" : "Replay"}, {behind} s behind</span>}
        <button type="button" className="go-live" disabled={mode === "live"} onClick={() => { clock.goLive(); onChange(); }}>
          Go live
        </button>
      </div>
    </div>
  );
}
