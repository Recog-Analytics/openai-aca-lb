/**
 * One palette feeds both the DOM (as CSS custom properties) and the WebGL scene. It is the Recog ops portal's brand
 * (shadcn tokens in oklch, neutral base, blue primary), converted to hex for the canvas. Where a brand colour is too
 * light or dark for WCAG AA as text on these surfaces, its lightness moves and its hue and chroma stay (noted inline).
 * State colours are reserved for state: throttled is the brand primary, the others are success, warning, destructive.
 */
export const palettes = {
  dark: {
    bg: "#0a0a0a", // background oklch(0.145 0 0)
    surface: "#171717", // card oklch(0.205 0 0)
    raised: "#262626", // secondary oklch(0.269 0 0)
    line: "#2e2e2e", // border, white 10 % over card
    lineStrong: "#404040", // accent oklch(0.371 0 0)
    text: "#fafafa", // foreground oklch(0.985 0 0)
    muted: "#a1a1a1", // muted-foreground oklch(0.708 0 0)
    healthy: "#00a63e", // success oklch(0.627 0.194 149.214)
    throttled: "#5e94ff", // primary hue 262.88, lightness 0.68 for AA on dark
    degraded: "#eea82f", // warning oklch(0.78 0.15 76)
    failed: "#ff6467", // destructive oklch(0.704 0.191 22.216)
    disabled: "#737373", // oklch(0.556 0 0)
    particle: "#f5f5f5",
    flow: "#e5e5e5",
    focus: "#5e94ff",
    brand: "#2563eb", // primary oklch(0.5461 0.2152 262.8809)
  },
  light: {
    bg: "#f4f4f5", // canvas hsl(240 5% 96%)
    surface: "#fafafa", // sidebar oklch(0.985 0 0)
    raised: "#ffffff", // card
    line: "#e5e5e5", // border oklch(0.922 0 0)
    lineStrong: "#d4d4d4", // oklch(0.87 0 0)
    text: "#0a0a0a", // foreground oklch(0.145 0 0)
    muted: "#696969", // muted-foreground at lightness 0.52 for AA on the canvas
    healthy: "#00802e", // success hue, lightness 0.52 for AA on white
    throttled: "#2563eb", // primary
    degraded: "#945a00", // warning hue, lightness 0.52 for AA on white
    failed: "#d01d21", // destructive hue, lightness 0.55 for AA on the canvas
    disabled: "#8a8a8a",
    particle: "#262626",
    flow: "#404040",
    focus: "#2563eb",
    brand: "#2563eb",
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
