/** Animated aurora background: palette-tinted blobs, soft dust, vignette, atmosphere signals and film grain. */

import type { AtmosphereSignalsState } from './types/ipc';

export type RGB = [number, number, number];

interface Dust {
  x: number;
  y: number;
  vx: number;
  vy: number;
  r: number;
  phase: number;
  speed: number;
}

interface Ripple {
  x: number;
  y: number;
  t: number;
}

const DEFAULT_PALETTE: RGB[] = [
  [92, 110, 255],
  [176, 92, 255],
  [40, 170, 220],
];

const WARM_CALL_PALETTE: RGB[] = [
  [245, 158, 11],  // amber-500
  [234, 88, 12],   // orange-600
  [251, 191, 36],  // amber-400
];

const NIGHT_PALETTE: RGB[] = [
  [217, 119, 6],   // amber-600
  [180, 83, 9],    // amber-700
  [146, 64, 14],   // amber-800
];

const GAME_PALETTE: RGB[] = [
  [236, 72, 153],  // pink-500
  [139, 92, 246],  // violet-500
  [16, 185, 129],  // emerald-500
];

export function hexToRgb(hex: string): RGB | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function rgbToHsl([r, g, b]: RGB): [number, number, number] {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h = 0;
  if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
  else if (max === g) h = (b - r) / d + 2;
  else h = (r - g) / d + 4;
  return [h / 6, s, l];
}

function hslToRgb(h: number, s: number, l: number): RGB {
  if (s === 0) return [l * 255, l * 255, l * 255];
  const hue2rgb = (p: number, q: number, t: number) => {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  return [hue2rgb(p, q, h + 1 / 3) * 255, hue2rgb(p, q, h) * 255, hue2rgb(p, q, h - 1 / 3) * 255];
}

export function vivid(c: RGB, light = 0.55, minSat = 0.55): RGB {
  const [h, s, l] = rgbToHsl(c);
  if (s < 0.08) return hslToRgb(0.64, 0.35, Math.max(light, l * 0.8)); // greys → cool blue-grey
  return hslToRgb(h, Math.max(minSat, Math.min(0.9, s * 1.2)), light);
}

function blendRgb(a: RGB, b: RGB, t: number): RGB {
  const k = Math.min(1, Math.max(0, t));
  return [
    Math.round(a[0] + (b[0] - a[0]) * k),
    Math.round(a[1] + (b[1] - a[1]) * k),
    Math.round(a[2] + (b[2] - a[2]) * k),
  ];
}

export function buildPalette(colors: string[]): RGB[] {
  const parsed = colors.map(hexToRgb).filter((c): c is RGB => c !== null);
  if (parsed.length === 0) return DEFAULT_PALETTE.map((c) => [...c] as RGB);
  // Prefer the most saturated colors, keep hue variety.
  const sorted = [...parsed].sort((a, b) => rgbToHsl(b)[1] - rgbToHsl(a)[1]);
  const picks: RGB[] = [];
  for (const c of sorted) {
    const h = rgbToHsl(c)[0];
    if (picks.every((p) => Math.abs(rgbToHsl(p)[0] - h) > 0.06)) picks.push(c);
    if (picks.length === 3) break;
  }
  while (picks.length < 3) picks.push(sorted[picks.length % sorted.length]);
  return picks.map((c) => vivid(c));
}

export class BackgroundRenderer {
  private basePalette: RGB[] = DEFAULT_PALETTE.map((c) => [...c] as RGB);
  private palette: RGB[] = DEFAULT_PALETTE.map((c) => [...c] as RGB);
  private target: RGB[] = DEFAULT_PALETTE.map((c) => [...c] as RGB);
  private dust: Dust[] = [];
  private ripples: Ripple[] = [];
  private grain: CanvasPattern | null = null;
  private w = 0;
  private h = 0;
  private glowX = 0;
  private glowY = 0;

  // Context & atmosphere states
  private contextMode: 'work' | 'game' | 'night' = 'work';
  private palettePreset = 'pin';
  private prodAlertAmount = 0;
  private callWarmthAmount = 0;
  private signals: AtmosphereSignalsState = {
    prodAlert: false,
    upcomingCall: false,
    lowBalance: false,
  };

  private videoBackdrop = false;

  private ctx: CanvasRenderingContext2D;

  constructor(ctx: CanvasRenderingContext2D) {
    this.ctx = ctx;
    this.grain = this.makeGrain();
  }

  resize(w: number, h: number): void {
    this.w = w;
    this.h = h;
    this.glowX = w / 2;
    this.glowY = h / 2;
    const count = Math.round(Math.min(110, (w * h) / 22000));
    this.dust = Array.from({ length: count }, () => ({
      x: Math.random() * w,
      y: Math.random() * h,
      vx: (Math.random() - 0.5) * 0.12,
      vy: -(0.04 + Math.random() * 0.14),
      r: 0.4 + Math.random() * Math.random() * 1.6,
      phase: Math.random() * Math.PI * 2,
      speed: 0.4 + Math.random() * 1.2,
    }));
  }

  setPalette(p: RGB[]): void {
    this.basePalette = p.map((c) => [...c] as RGB);
    this.recomputeTargetPalette();
  }

  setContextMode(mode: 'work' | 'game' | 'night', preset?: string): void {
    this.contextMode = mode;
    if (preset) this.palettePreset = preset;
    this.recomputeTargetPalette();
  }

  setAtmosphere(signals: AtmosphereSignalsState): void {
    this.signals = signals;
    this.recomputeTargetPalette();
  }

  setVideoBackdrop(on: boolean): void {
    this.videoBackdrop = on;
  }

  private recomputeTargetPalette(): void {
    let p = this.basePalette.map((c) => [...c] as RGB);

    // Context mode presets
    if (this.contextMode === 'night' || this.palettePreset === 'night') {
      p = p.map((c, i) => blendRgb(c, NIGHT_PALETTE[i], 0.75));
    } else if (this.contextMode === 'game' || this.palettePreset === 'game') {
      p = p.map((c, i) => blendRgb(c, GAME_PALETTE[i], 0.70));
    }

    // Atmosphere Signal: Upcoming call warm shift
    if (this.signals.upcomingCall) {
      p = p.map((c, i) => blendRgb(c, WARM_CALL_PALETTE[i], 0.85));
    }

    this.target = p;
  }

  get accent(): RGB {
    return this.target[0];
  }

  addRipple(x: number, y: number): void {
    if (this.ripples.length > 4) this.ripples.shift();
    this.ripples.push({ x, y, t: 0 });
  }

  /** Base layer: drawn before the collage. */
  drawBack(time: number, mouseX: number, mouseY: number, dt = 1): void {
    const { ctx, w, h } = this;

    // Smoothly ease atmosphere signal amounts
    const targetProd = this.signals.prodAlert ? 1 : 0;
    this.prodAlertAmount += (targetProd - this.prodAlertAmount) * (1 - Math.pow(0.92, dt));

    const targetWarmth = this.signals.upcomingCall ? 1 : 0;
    this.callWarmthAmount += (targetWarmth - this.callWarmthAmount) * (1 - Math.pow(0.94, dt));

    // Smoothly approach target palette (≈2s)
    for (let i = 0; i < 3; i++) {
      for (let k = 0; k < 3; k++) {
        this.palette[i][k] += (this.target[i][k] - this.palette[i][k]) * (1 - Math.pow(0.97, dt));
      }
    }

    const [a, b, c] = this.palette;

    // Video wallpaper is a DOM layer behind the canvas; keep pixels transparent so it shows through.
    if (this.videoBackdrop) return;

    // Base background gradient: adapts warmly if upcoming call is active, or deeply dimmed if night
    const base = ctx.createLinearGradient(0, 0, w, h);
    if (this.callWarmthAmount > 0.05) {
      const kw = this.callWarmthAmount;
      const r0 = Math.round((a[0] * 0.07 + 6) * (1 - kw) + 24 * kw);
      const g0 = Math.round((a[1] * 0.07 + 7) * (1 - kw) + 14 * kw);
      const b0 = Math.round((a[2] * 0.07 + 12) * (1 - kw) + 10 * kw);
      const r1 = Math.round((c[0] * 0.05 + 5) * (1 - kw) + 18 * kw);
      const g1 = Math.round((c[1] * 0.05 + 5) * (1 - kw) + 10 * kw);
      const b1 = Math.round((c[2] * 0.05 + 9) * (1 - kw) + 8 * kw);
      base.addColorStop(0, `rgb(${r0}, ${g0}, ${b0})`);
      base.addColorStop(1, `rgb(${r1}, ${g1}, ${b1})`);
    } else if (this.contextMode === 'night') {
      base.addColorStop(0, `rgb(${Math.round(a[0] * 0.04 + 4)}, ${Math.round(a[1] * 0.04 + 4)}, ${Math.round(a[2] * 0.04 + 6)})`);
      base.addColorStop(1, `rgb(${Math.round(c[0] * 0.03 + 3)}, ${Math.round(c[1] * 0.03 + 3)}, ${Math.round(c[2] * 0.03 + 5)})`);
    } else {
      base.addColorStop(0, `rgb(${Math.round(a[0] * 0.07 + 6)}, ${Math.round(a[1] * 0.07 + 7)}, ${Math.round(a[2] * 0.07 + 12)})`);
      base.addColorStop(1, `rgb(${Math.round(c[0] * 0.05 + 5)}, ${Math.round(c[1] * 0.05 + 5)}, ${Math.round(c[2] * 0.05 + 9)})`);
    }

    ctx.fillStyle = base;
    ctx.fillRect(0, 0, w, h);

    // Aurora blobs drifting slowly
    const t = time / 1000;
    const m = Math.max(w, h);
    const speed = this.contextMode === 'game' ? 1.3 : 1.0;
    const blobs: [RGB, number, number, number, number][] = [
      [a, 0.22 + Math.sin(t * 0.05 * speed) * 0.1, 0.25 + Math.cos(t * 0.04 * speed) * 0.12, 0.62, 0.30],
      [b, 0.8 + Math.cos(t * 0.045 * speed) * 0.1, 0.3 + Math.sin(t * 0.06 * speed) * 0.14, 0.55, 0.24],
      [c, 0.55 + Math.sin(t * 0.035 * speed + 2) * 0.16, 0.95 + Math.cos(t * 0.05 * speed) * 0.06, 0.7, 0.22],
    ];

    ctx.save();
    ctx.globalCompositeOperation = 'screen';
    for (const [col, fx, fy, fr, alpha] of blobs) {
      const x = fx * w, y = fy * h, r = fr * m;
      const g = ctx.createRadialGradient(x, y, 0, x, y, r);
      g.addColorStop(0, `rgba(${col[0] | 0}, ${col[1] | 0}, ${col[2] | 0}, ${alpha})`);
      g.addColorStop(0.45, `rgba(${col[0] | 0}, ${col[1] | 0}, ${col[2] | 0}, ${alpha * 0.35})`);
      g.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, w, h);
    }

    // Atmosphere Signal 2: Warm sunbeam / amber morning glow before upcoming call
    if (this.callWarmthAmount > 0.02) {
      const kw = this.callWarmthAmount;
      const glow = ctx.createRadialGradient(w * 0.5, 0, 0, w * 0.5, 0, Math.max(w, h) * 0.75);
      glow.addColorStop(0, `rgba(245, 158, 11, ${0.28 * kw})`);
      glow.addColorStop(0.45, `rgba(251, 146, 60, ${0.12 * kw})`);
      glow.addColorStop(1, 'rgba(0, 0, 0, 0)');
      ctx.fillStyle = glow;
      ctx.fillRect(0, 0, w, h);
    }

    // Cursor light, eased
    const g = 1 - Math.pow(0.88, dt);
    this.glowX += (mouseX - this.glowX) * g;
    this.glowY += (mouseY - this.glowY) * g;
    const gl = ctx.createRadialGradient(this.glowX, this.glowY, 0, this.glowX, this.glowY, 380);
    gl.addColorStop(0, `rgba(${a[0] | 0}, ${a[1] | 0}, ${a[2] | 0}, 0.10)`);
    gl.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = gl;
    ctx.fillRect(0, 0, w, h);
    ctx.restore();

    // Dust particles
    for (const p of this.dust) {
      const dx = p.x - mouseX, dy = p.y - mouseY;
      const d2 = dx * dx + dy * dy;
      if (d2 < 140 * 140 && d2 > 1) {
        const d = Math.sqrt(d2);
        const f = ((140 - d) / 140) * 0.9 * dt;
        p.x += (dx / d) * f;
        p.y += (dy / d) * f;
      }
      p.x += p.vx * dt * speed;
      p.y += p.vy * dt * speed;
      if (p.y < -4) { p.y = h + 4; p.x = Math.random() * w; }
      if (p.x < -4) p.x = w + 4;
      if (p.x > w + 4) p.x = -4;
      const tw = 0.35 + 0.45 * (0.5 + 0.5 * Math.sin(t * p.speed + p.phase));
      ctx.fillStyle = `rgba(230, 236, 255, ${tw * Math.min(1, p.r)})`;
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  /** Top layer: ripples, vignette, atmosphere signals, grain. Drawn after the collage. */
  drawFront(dt = 1, timestamp = performance.now()): void {
    const { ctx, w, h } = this;
    const a = this.palette[0];

    // 1. Cursor click ripples
    for (let i = this.ripples.length - 1; i >= 0; i--) {
      const r = this.ripples[i];
      r.t += 0.022 * dt;
      if (r.t >= 1) { this.ripples.splice(i, 1); continue; }
      const e = 1 - Math.pow(1 - r.t, 3);
      ctx.beginPath();
      ctx.arc(r.x, r.y, 12 + e * 220, 0, Math.PI * 2);
      ctx.strokeStyle = `rgba(${a[0] | 0}, ${a[1] | 0}, ${a[2] | 0}, ${(1 - r.t) * 0.45})`;
      ctx.lineWidth = 1.5 + (1 - r.t) * 2;
      ctx.stroke();
    }

    // 2. Base vignette — skipped for media wallpapers so the photo/video stays clear.
    if (!this.videoBackdrop) {
      const v = ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.35, w / 2, h / 2, Math.max(w, h) * 0.75);
      v.addColorStop(0, 'rgba(0,0,0,0)');
      v.addColorStop(1, 'rgba(0,0,0,0.55)');
      ctx.fillStyle = v;
      ctx.fillRect(0, 0, w, h);
    }

    // 3. Atmosphere Signal 1: Красноватые края экрана при падении прода (Production Down Alert)
    if (this.prodAlertAmount > 0.005) {
      const pulse = 0.5 + 0.5 * Math.sin(timestamp * 0.003); // smooth ~2.1s breathing pulse
      const alpha = this.prodAlertAmount * (0.36 + 0.30 * pulse);
      const edgeDepth = Math.max(90, Math.min(220, Math.min(w, h) * 0.16));

      ctx.save();
      // Use source-over for vibrant, unmistakable alarming crimson edges
      ctx.globalCompositeOperation = 'source-over';

      // Top edge
      const topGrad = ctx.createLinearGradient(0, 0, 0, edgeDepth);
      topGrad.addColorStop(0, `rgba(220, 38, 38, ${alpha})`);
      topGrad.addColorStop(1, 'rgba(220, 38, 38, 0)');
      ctx.fillStyle = topGrad;
      ctx.fillRect(0, 0, w, edgeDepth);

      // Bottom edge
      const bottomGrad = ctx.createLinearGradient(0, h, 0, h - edgeDepth);
      bottomGrad.addColorStop(0, `rgba(220, 38, 38, ${alpha})`);
      bottomGrad.addColorStop(1, 'rgba(220, 38, 38, 0)');
      ctx.fillStyle = bottomGrad;
      ctx.fillRect(0, h - edgeDepth, w, edgeDepth);

      // Left edge
      const leftGrad = ctx.createLinearGradient(0, 0, edgeDepth, 0);
      leftGrad.addColorStop(0, `rgba(220, 38, 38, ${alpha * 0.95})`);
      leftGrad.addColorStop(1, 'rgba(220, 38, 38, 0)');
      ctx.fillStyle = leftGrad;
      ctx.fillRect(0, 0, edgeDepth, h);

      // Right edge
      const rightGrad = ctx.createLinearGradient(w, 0, w - edgeDepth, 0);
      rightGrad.addColorStop(0, `rgba(220, 38, 38, ${alpha * 0.95})`);
      rightGrad.addColorStop(1, 'rgba(220, 38, 38, 0)');
      ctx.fillStyle = rightGrad;
      ctx.fillRect(w - edgeDepth, 0, edgeDepth, h);

      // Corner radial boosts for seamless perimeter aura
      const cornerR = edgeDepth * 1.35;
      const corners: [number, number][] = [[0, 0], [w, 0], [0, h], [w, h]];
      for (const [cx, cy] of corners) {
        const cg = ctx.createRadialGradient(cx, cy, 0, cx, cy, cornerR);
        cg.addColorStop(0, `rgba(239, 68, 68, ${alpha * 0.65})`);
        cg.addColorStop(1, 'rgba(239, 68, 68, 0)');
        ctx.fillStyle = cg;
        ctx.fillRect(cx === 0 ? 0 : w - cornerR, cy === 0 ? 0 : h - cornerR, cornerR, cornerR);
      }

      ctx.restore();
    }

    // 4. Film grain (aurora only)
    if (!this.videoBackdrop && this.grain) {
      ctx.save();
      ctx.globalAlpha = 0.045;
      ctx.fillStyle = this.grain;
      ctx.fillRect(0, 0, w, h);
      ctx.restore();
    }
  }

  private makeGrain(): CanvasPattern | null {
    const c = document.createElement('canvas');
    c.width = c.height = 160;
    const g = c.getContext('2d');
    if (!g) return null;
    const img = g.createImageData(160, 160);
    for (let i = 0; i < img.data.length; i += 4) {
      const n = Math.random() * 255;
      img.data[i] = img.data[i + 1] = img.data[i + 2] = n;
      img.data[i + 3] = 255;
    }
    g.putImageData(img, 0, 0);
    return this.ctx.createPattern(c, 'repeat');
  }
}
