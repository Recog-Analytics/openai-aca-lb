import { useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { percent } from "../model/attention";
import type { PlaybackClock, PlaybackMode } from "../model/clock";
import { rangeKeys, ranges, type RangeKey } from "../model/history";
import type { Bin } from "../model/strip";

interface Props {
  clock: PlaybackClock;
  bins: Bin[];
  from: number;
  to: number;
  t: number;
  mode: PlaybackMode;
  range: RangeKey;
  onRange: (range: RangeKey) => void;
  /** The funnel window and its comparison window, as bands over the strip; the window ends at `windowEnd`. */
  windowMs: number;
  windowEnd: number;
  /** Null until the history allows a comparison. */
  compareMs: number | null;
  loading: boolean;
  error: string | null;
  onChange: () => void;
}

/** Smallest "nice" axis maximum (1, 2 or 5 times a power of ten) at or above `value`. */
export function niceCeiling(value: number): number {
  if (value <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  return ([1, 2, 5, 10].find((step) => step * power >= value) ?? 10) * power;
}

/** Axis marks per range, in seconds before now. */
const marks: Record<RangeKey, number[]> = {
  "2m": [120, 90, 60, 30], "15m": [900, 600, 300], "1h": [3600, 2700, 1800, 900], "24h": [86_400, 64_800, 43_200, 21_600],
};

function agoLabel(seconds: number): string {
  if (seconds < 60) return `${seconds} s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  return `${Math.round(seconds / 3600)} h ago`;
}

const clockTime = (value: number) => new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const stateWords: Record<string, string> = { Degraded: "slow", Throttled: "throttled", Open: "down or circuit open", Disabled: "disabled" };

/**
 * The selected range as a chart of exact requests per second: served first time, served after a retry, failed or
 * refused. Above the bars, a thin track marks when any deployment had a problem. Two bands mark the window the figures
 * cover and the window they compare with. Pointing at a bar reads it out; a click, a drag or the arrow keys move the
 * playhead there. The range control zooms from the live two minutes out to the retained day.
 */
export function Scrubber({ clock, bins, from, to, t, mode, range, onRange, windowMs, windowEnd, compareMs, loading, error, onChange }: Props) {
  const track = useRef<HTMLDivElement>(null);
  const [pointed, setPointed] = useState<number | null>(null);
  const span = Math.max(1, to - from);
  const rate = (bin: Bin) => (bin.seconds > 0 ? (bin.direct + bin.retried + bin.failed) / bin.seconds : 0);
  const axisMax = niceCeiling(Math.max(...bins.map(rate), 0.1));
  const first = bins[0]?.start ?? from;
  const width = Math.max(1, to - first);
  const at = (time: number) => `${Math.min(100, Math.max(0, ((time - first) / width) * 100))}%`;
  const band = (end: number) => ({ left: at(end - windowMs), width: `calc(${at(end)} - ${at(end - windowMs)})` });
  const position = Math.min(1, Math.max(0, (t - first) / width));
  const behind = Math.max(0, to - t);
  const pointedBin = pointed === null ? undefined : bins[pointed];
  const pointedTotal = pointedBin ? pointedBin.direct + pointedBin.retried + pointedBin.failed : 0;
  const step = Math.max(5000, span / 24);

  const seekTo = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect) return;
    clock.seek(first + ((clientX - rect.left) / rect.width) * width, Date.now());
    onChange();
  };
  const binAt = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0) return null;
    return Math.min(bins.length - 1, Math.max(0, Math.floor(((clientX - rect.left) / rect.width) * bins.length)));
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
    const moves: Record<string, number> = { ArrowLeft: t - (event.shiftKey ? step * 3 : step), ArrowRight: t + (event.shiftKey ? step * 3 : step), Home: from, End: to };
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
    <div className="scrubber" data-live={mode === "live" || undefined}>
      <button type="button" className="play" onClick={toggle} aria-label={mode === "paused" ? "Resume playback" : "Pause playback"}>
        {mode === "paused"
          ? <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M5.5 3.5v9l7-4.5z" fill="currentColor" /></svg>
          : <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M4.5 3.5h2.5v9H4.5zM9 3.5h2.5v9H9z" fill="currentColor" /></svg>}
      </button>
      <div className="strip">
        <div className="strip-head">
          <div className="segmented small" role="radiogroup" aria-label="Time range">
            {rangeKeys.map((key) => (
              <button key={key} type="button" role="radio" aria-checked={key === range} onClick={() => onRange(key)}>{ranges[key].label}</button>
            ))}
          </div>
          <ul className="legend" aria-label="Requests per second, legend">
            <li data-kind="direct">Served</li>
            <li data-kind="retried">After a retry</li>
            <li data-kind="failed">Failed or refused</li>
            <li data-kind="problem" title="Any deployment was slow, throttled, open or disabled">Problem</li>
            <li data-kind="now" title="The window the figures and the funnel cover">In the figures</li>
            {compareMs !== null && <li data-kind="compare" title="The window the change markers compare with">Compared</li>}
          </ul>
          <span className="hint">{loading ? "Loading history…" : error ? <span data-tone="failed">{error}</span>
            : <>Drag to look back <kbd>←</kbd> <kbd>→</kbd> <kbd>Space</kbd></>}</span>
        </div>
        <div ref={track} className="track" role="slider" tabIndex={0} aria-label="Playback position"
          aria-valuemin={-Math.round(span / 1000)} aria-valuemax={0} aria-valuenow={-Math.round(behind / 1000)}
          aria-valuetext={mode === "live" ? "Live" : `${agoLabel(Math.round(behind / 1000))}, at ${clockTime(t)}`}
          onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerLeave={() => setPointed(null)} onKeyDown={onKeyDown}>
          <div className="incidents" aria-hidden="true">
            {bins.map((bin, index) => <span key={index} data-state={bin.worst ?? undefined} />)}
          </div>
          <div className="plot">
            <div className="gridline" data-at="top" aria-hidden="true"><span>{axisMax}/s</span></div>
            <div className="gridline" data-at="middle" aria-hidden="true"><span>{axisMax / 2}/s</span></div>
            {compareMs !== null && <div className="window" data-kind="compare" style={band(windowEnd - compareMs)} aria-hidden="true" />}
            <div className="window" style={band(windowEnd)} aria-hidden="true" />
            <div className="bins" aria-hidden="true">
              {bins.map((bin, index) => (
                <span key={index} className="bin" data-pointed={pointed === index || undefined} data-empty={bin.seconds === 0 || undefined}
                  style={{ height: bin.seconds === 0 ? "100%" : rate(bin) === 0 ? "1px" : `${Math.min(100, (100 * rate(bin)) / axisMax)}%` }}>
                  <span className="bin-failed" style={{ flexGrow: bin.failed }} />
                  <span className="bin-retried" style={{ flexGrow: bin.retried }} />
                  <span className="bin-direct" style={{ flexGrow: bin.direct }} />
                </span>
              ))}
            </div>
            <div className="playhead" aria-hidden="true" style={{ left: `${position * 100}%` }} />
          </div>
          {pointedBin && pointed !== null && (
            <div className="readout" aria-hidden="true" data-side={pointed > bins.length / 2 ? "left" : "right"}
              style={{ left: `${((pointed + 0.5) / bins.length) * 100}%` }}>
              <strong>{range === "2m" ? agoLabel(Math.max(0, Math.round((to - pointedBin.end) / 1000))) : `${clockTime(pointedBin.start)}–${clockTime(pointedBin.end)}`}</strong>
              {pointedBin.seconds === 0 ? <span>No data retained</span> : <span>{rate(pointedBin).toFixed(1)} req/s</span>}
              {pointedBin.retried > 0 && <span data-kind="retried">{percent(pointedBin.retried / pointedTotal)} after a retry</span>}
              {pointedBin.failed > 0 && <span data-kind="failed">{percent(pointedBin.failed / pointedTotal)} failed or refused</span>}
              {pointedBin.worst && <span data-kind="problem">{pointedBin.unhealthy === 1 ? "1 deployment" : `${pointedBin.unhealthy} deployments`} {stateWords[pointedBin.worst] ?? "unhealthy"}</span>}
            </div>
          )}
        </div>
        <div className="axis" aria-hidden="true">
          {marks[range].filter((seconds) => seconds * 1000 <= span + 1000).map((seconds) => (
            <span key={seconds} style={{ left: at(to - seconds * 1000) }}>{range === "2m" ? agoLabel(seconds) : clockTime(to - seconds * 1000)}</span>
          ))}
          <span style={{ left: "100%" }} data-end>now</span>
        </div>
      </div>
      <div className="when">
        {mode === "live" ? <span className="live-dot">Live</span> : <span className="behind">{mode === "paused" ? "Paused" : "Replay"}, {agoLabel(Math.round(behind / 1000))}</span>}
        <button type="button" className="go-live" disabled={mode === "live"} onClick={() => { clock.goLive(); onChange(); }}>
          Go live
        </button>
      </div>
    </div>
  );
}
