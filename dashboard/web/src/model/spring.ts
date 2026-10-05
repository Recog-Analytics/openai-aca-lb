/** A damped spring that keeps its value and velocity when its target moves, so retargeting never restarts motion. */
export interface Spring { value: number; velocity: number; target: number }

export const springs = {
  /** Layout moves: settles in about half a second with no visible overshoot. */
  layout: { stiffness: 120, damping: 22 },
  /** Link thickness and other data-driven quantities: slower and smooth. */
  data: { stiffness: 40, damping: 13 },
} as const;

export function spring(value: number): Spring {
  return { value, velocity: 0, target: value };
}

export function stepSpring(s: Spring, dtMs: number, config: { stiffness: number; damping: number }): void {
  // Fixed sub-steps keep the integration stable when a tab wakes up with a long frame.
  let remaining = Math.min(dtMs, 250) / 1000;
  while (remaining > 0) {
    const dt = Math.min(remaining, 1 / 120);
    const force = config.stiffness * (s.target - s.value) - config.damping * s.velocity;
    s.velocity += force * dt;
    s.value += s.velocity * dt;
    remaining -= dt;
  }
  if (Math.abs(s.target - s.value) < 1e-3 && Math.abs(s.velocity) < 1e-3) {
    s.value = s.target;
    s.velocity = 0;
  }
}

export function snapSpring(s: Spring, target: number): void {
  s.value = target;
  s.target = target;
  s.velocity = 0;
}
