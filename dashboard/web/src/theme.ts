/**
 * One palette feeds both the DOM (as CSS custom properties) and the WebGL scene.
 * Requests are warm light moving through cool infrastructure; ribbons are the same light, accumulated.
 * State colours are reserved for state.
 */
export const palettes = {
  dark: {
    bg: "#121925",
    surface: "#18212e",
    raised: "#202b3a",
    line: "#2a3647",
    lineStrong: "#3c4a5f",
    text: "#e8ecf3",
    muted: "#93a0b4",
    healthy: "#45c9a0",
    throttled: "#5ba8ff",
    degraded: "#f2b33d",
    failed: "#ff6767",
    disabled: "#6b7587",
    particle: "#f6e7c8",
    flow: "#e9dcc0",
    focus: "#9cc7ff",
  },
  light: {
    bg: "#edf0f4",
    surface: "#f8fafc",
    raised: "#ffffff",
    line: "#d0d7e1",
    lineStrong: "#a5b1c1",
    text: "#152030",
    muted: "#4f5d70",
    healthy: "#097a5b",
    throttled: "#1e69d0",
    degraded: "#946100",
    failed: "#c63333",
    disabled: "#7f8795",
    particle: "#2c3550",
    flow: "#3d4868",
    focus: "#1e69d0",
  },
} as const;

export type ThemeName = keyof typeof palettes;
export type Palette = { [K in keyof (typeof palettes)["dark"]]: string };

export function applyPalette(theme: ThemeName): void {
  const root = document.documentElement;
  // Swap colours in one frame: without this, every colour transition fires at once and the page smears.
  const freeze = document.createElement("style");
  freeze.textContent = "*,*::before,*::after{transition:none !important}";
  document.head.appendChild(freeze);
  root.dataset.theme = theme;
  root.style.colorScheme = theme;
  for (const [name, value] of Object.entries(palettes[theme]))
    root.style.setProperty(`--${name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`)}`, value);
  void root.offsetHeight;
  requestAnimationFrame(() => freeze.remove());
}

export function hexToNumber(hex: string): number {
  return Number.parseInt(hex.slice(1), 16);
}

export function mix(a: number, b: number, t: number): number {
  const channel = (shift: number) => {
    const from = (a >> shift) & 0xff;
    const to = (b >> shift) & 0xff;
    return Math.round(from + (to - from) * t) << shift;
  };
  return channel(16) | channel(8) | channel(0);
}
