import { PLAYBACK_LAG_MS, WINDOW_MS, type Timeline } from "./timeline";

export type PlaybackMode = "live" | "paused" | "replay";

/** Scene time in server milliseconds: live, frozen, or replaying the retained window at normal speed. */
export class PlaybackClock {
  mode: PlaybackMode = "live";
  private pausedAt = 0;
  private replayOffset = 0;

  constructor(private readonly timeline: Timeline) {}

  live(clientNow: number): number {
    return clientNow + this.timeline.clockOffset - PLAYBACK_LAG_MS;
  }

  bounds(clientNow: number): { from: number; to: number } {
    const to = this.live(clientNow);
    const earliest = this.timeline.earliestAt ?? to;
    return { from: Math.min(to, Math.max(earliest, to - WINDOW_MS)), to };
  }

  time(clientNow: number): number {
    if (this.mode === "paused") return this.pausedAt;
    const live = this.live(clientNow);
    if (this.mode === "replay") {
      const replay = clientNow + this.replayOffset;
      if (replay < live) return replay;
      this.mode = "live";
    }
    return live;
  }

  pause(clientNow: number): void {
    this.pausedAt = this.time(clientNow);
    this.mode = "paused";
  }

  resume(clientNow: number): void {
    if (this.mode !== "paused") return;
    this.replayOffset = this.pausedAt - clientNow;
    this.mode = "replay";
    this.time(clientNow);
  }

  /** Moves the playhead; a paused clock stays paused, a running clock replays from there. */
  seek(t: number, clientNow: number): void {
    const { from, to } = this.bounds(clientNow);
    const target = Math.min(to, Math.max(from, t));
    if (this.mode === "paused") this.pausedAt = target;
    else {
      this.replayOffset = target - clientNow;
      this.mode = target >= to ? "live" : "replay";
    }
  }

  goLive(): void {
    this.mode = "live";
  }
}
