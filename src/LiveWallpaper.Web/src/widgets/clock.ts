import type { IWidget } from './contract';
import { pad2, fmtDateLong } from './agenda';
import { DraggableWidgetManager } from './draggable';

/** Edge offset from the screen border, matches the "nice" margin used by draggable widgets. */
const MARGIN = 48;

export type ClockPosition =
  | 'top-left'
  | 'top-center'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-center'
  | 'bottom-right';

const POSITIONS: readonly string[] = [
  'top-left',
  'top-center',
  'top-right',
  'bottom-left',
  'bottom-center',
  'bottom-right',
];

/** Optional display fonts for the clock. Loaded from Google Fonts in index.html. */
const CLOCK_FONTS: Record<string, string> = {
  default: '',
  inter: "'Inter', 'Segoe UI', system-ui, sans-serif",
  manrope: "'Manrope', 'Segoe UI', system-ui, sans-serif",
  mono: "'JetBrains Mono', 'Cascadia Mono', Consolas, monospace",
  serif: "'Playfair Display', Georgia, 'Times New Roman', serif",
};

const normalizePosition = (v: unknown): ClockPosition | null =>
  typeof v === 'string' && (POSITIONS as readonly string[]).includes(v) ? (v as ClockPosition) : null;

/**
 * Plain-text clock: time + date, no card, no agenda. Placed by a preset from settings
 * (no dragging — it is a background element), with an optional display font.
 */
export class ClockWidget implements IWidget {
  readonly id = 'clock';
  private element: HTMLElement | null = null;
  private r: Record<string, HTMLElement> = {};
  private timer: number | null = null;
  private position: ClockPosition = 'top-center';

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget clock-widget align-center';
    el.innerHTML = `
      <div class="clock-time">
        <span class="clock-hm">00:00</span><span class="clock-sec">00</span>
      </div>
      <div class="clock-date"></div>
    `;
    this.element = el;
    this.r.hm = el.querySelector('.clock-hm') as HTMLElement;
    this.r.sec = el.querySelector('.clock-sec') as HTMLElement;
    this.r.date = el.querySelector('.clock-date') as HTMLElement;
    container.appendChild(el);

    // Preset placement instead of dragging. Registered statically so draggable neighbours
    // settle around the clock, but it never starts a drag itself.
    DraggableWidgetManager.registerStatic(this.id, el, () => this.computePosition());
    // A webfont arriving late changes the width — recentre once fonts are in.
    document.fonts?.ready.then(() => this.applyPosition()).catch(() => { /* older engines */ });

    this.tick();
    this.startTimer();
    requestAnimationFrame(() => this.applyPosition());
  }

  /** Config from the host (`widgets.clockPosition` / `widgets.clockFont`). */
  applyConfig(cfg: { position?: unknown; font?: unknown }): void {
    const el = this.element;
    if (!el) return;
    const pos = normalizePosition(cfg.position);
    if (pos) {
      this.position = pos;
      el.classList.toggle('align-center', pos.endsWith('-center'));
      el.classList.toggle('align-right', pos.endsWith('-right'));
      this.applyPosition();
    }
    if (typeof cfg.font === 'string' && cfg.font in CLOCK_FONTS) {
      const stack = CLOCK_FONTS[cfg.font];
      if (stack) el.style.setProperty('--clock-font', stack);
      else el.style.removeProperty('--clock-font');
      el.classList.toggle('has-custom-font', !!stack);
      // A different font changes the width — recentre once it is in.
      requestAnimationFrame(() => this.applyPosition());
    }
  }

  update(data: unknown): void {
    if (!this.element) return;
    if (typeof data === 'object' && data !== null && 'visible' in data) {
      const visible = (data as { visible: boolean }).visible;
      this.element.style.display = visible ? '' : 'none';
      if (visible) requestAnimationFrame(() => this.applyPosition());
    }
  }

  pause(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  resume(): void {
    this.tick();
    this.startTimer();
  }

  destroy(): void {
    this.pause();
    DraggableWidgetManager.unregister(this.id);
    this.element?.remove();
    this.element = null;
  }

  // ---------------------------------------------------------------- clock

  private startTimer(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = window.setInterval(() => this.tick(), 1000);
  }

  private tick(): void {
    if (!this.element) return;
    const now = new Date();
    this.r.hm.textContent = `${pad2(now.getHours())}:${pad2(now.getMinutes())}`;
    this.r.sec.textContent = pad2(now.getSeconds());
    const date = fmtDateLong(now);
    if (this.r.date.textContent !== date) this.r.date.textContent = date;
  }

  // ---------------------------------------------------------------- placement

  /** Where the preset puts the clock. Also the static-registration placement callback. */
  private computePosition(): { x: number; y: number } {
    const el = this.element;
    const w = el?.offsetWidth || 0;
    const h = el?.offsetHeight || 0;
    const W = window.innerWidth;
    const H = window.innerHeight;
    const [v, hz] = this.position.split('-') as ['top' | 'bottom', 'left' | 'center' | 'right'];
    const x = hz === 'left' ? MARGIN : hz === 'right' ? W - w - MARGIN : (W - w) / 2;
    const y = v === 'top' ? MARGIN : H - h - MARGIN;
    return { x: Math.round(x), y: Math.round(y) };
  }

  private applyPosition(): void {
    const el = this.element;
    if (!el || !el.isConnected || el.style.display === 'none') return;
    const { x, y } = this.computePosition();
    el.style.left = `${x}px`;
    el.style.top = `${y}px`;
  }
}
