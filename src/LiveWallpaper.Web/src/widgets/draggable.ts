import { LauncherContextMenu } from './context-menu';
import { IconDragManager } from './icon-drag';
import { LauncherTooltip } from './tooltip';

export interface WidgetPosition {
  x: number;
  y: number;
}

interface Registered {
  element: HTMLElement;
  defaultPos: () => WidgetPosition;
  /** Static widgets are placed programmatically (e.g. preset clock): they never start a drag. */
  static?: boolean;
}

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface Guide {
  axis: 'x' | 'y';
  pos: number; // line coordinate
  from: number;
  to: number;
}

const EDGE = 16; // hard clamp to the screen edge
const MARGIN = 48; // "nice" screen margin used by default layouts
const GAP = 16; // preferred spacing between widgets
const SNAP = 6; // snap threshold, px — small on purpose so it never feels sticky
const PAD = 8; // minimum free space kept between widgets when resolving overlaps

const HOVERABLE =
  '.launcher-item, .dock-item, .folder-tile, .w-btn-action, .m-btn, .srv-site, button.srv-meter, .srv-icon-btn, .media-progress-bar, .m-pill-btn, .agenda-widget [data-action], .tg-row, .lcm-row, .lcm-quick-btn';

type WheelListener = (x: number, y: number, delta: number, target: Element | null) => boolean;

/**
 * Wallpaper lives behind desktop icons, so DOM pointer events never arrive.
 * All interaction comes from the host's global mouse hook (CSS-pixel coordinates).
 *
 * Features: idempotent registration, persistent positions, Figma-like smart guides while
 * dragging (edges / centres / equal gaps / screen margins) and automatic overlap resolution.
 */
export class DraggableWidgetManager {
  private static registered = new Map<string, Registered>();
  private static activeDrag: { id: string; element: HTMLElement; offsetX: number; offsetY: number } | null = null;
  private static pressed: { x: number; y: number; id: string } | null = null;
  private static resizeBound = false;
  private static clickListeners: ((x: number, y: number, target: Element | null) => void)[] = [];
  private static wheelListeners: WheelListener[] = [];
  private static dragEndListeners: ((id: string) => void)[] = [];
  /** Expanded widgets (e.g. the clock panel) float above neighbours: no overlap resolution either way. */
  private static overlays = new Set<string>();
  private static currentHovered: HTMLElement | null = null;
  private static guideLayer: HTMLElement | null = null;

  /** Idempotent: calling twice for the same id only refreshes the element reference. */
  public static makeDraggable(id: string, element: HTMLElement, defaultPos: () => WidgetPosition): void {
    const existing = this.registered.get(id);
    this.registered.set(id, { element, defaultPos });
    if (existing) return;

    requestAnimationFrame(() => {
      const saved = this.loadPosition(id);
      const pos = saved ?? defaultPos();
      this.setPosition(element, pos.x, pos.y);
      // Defaults (and stale saved positions) must not land on top of other widgets.
      this.settle(id, false, !saved);
    });

    if (!this.resizeBound) {
      this.resizeBound = true;
      window.addEventListener('resize', () => this.clampAll());
    }

    // A widget that grows (alerts, new rows) must not spill over its neighbours.
    if (typeof ResizeObserver !== 'undefined') {
      let lastH = -1;
      let lastW = -1;
      new ResizeObserver(() => {
        if (this.activeDrag?.id === id || !this.registered.has(id)) return;
        if (element.offsetHeight === lastH && element.offsetWidth === lastW) return;
        const first = lastH < 0;
        lastH = element.offsetHeight;
        lastW = element.offsetWidth;
        if (first || !this.isVisible(element) || this.overlays.has(id)) return;
        const r = this.rectOf(element);
        this.setPosition(element, r.x, r.y);
        this.settle(id, true, false);
      }).observe(element);
    }
  }

  /**
   * Registers a widget that is placed purely in code (preset positions): it takes part in
   * overlap resolution — draggable neighbours settle around it — but never starts a drag itself.
   */
  public static registerStatic(id: string, element: HTMLElement, place: () => WidgetPosition): void {
    this.registered.set(id, { element, defaultPos: place, static: true });

    requestAnimationFrame(() => {
      const p = place();
      this.setPosition(element, p.x, p.y);
    });

    if (!this.resizeBound) {
      this.resizeBound = true;
      window.addEventListener('resize', () => this.clampAll());
    }
  }

  public static unregister(id: string): void {
    this.registered.delete(id);
  }

  public static addGlobalClickListener(listener: (x: number, y: number, target: Element | null) => void): () => void {
    this.clickListeners.push(listener);
    return () => {
      const idx = this.clickListeners.indexOf(listener);
      if (idx >= 0) this.clickListeners.splice(idx, 1);
    };
  }

  /** Runs before the default scroll handling; return true to consume the wheel event. */
  public static addGlobalWheelListener(listener: WheelListener): () => void {
    this.wheelListeners.push(listener);
    return () => {
      const idx = this.wheelListeners.indexOf(listener);
      if (idx >= 0) this.wheelListeners.splice(idx, 1);
    };
  }

  /** Fires after a user drop (not after programmatic moves). */
  public static addDragEndListener(listener: (id: string) => void): () => void {
    this.dragEndListeners.push(listener);
    return () => {
      const idx = this.dragEndListeners.indexOf(listener);
      if (idx >= 0) this.dragEndListeners.splice(idx, 1);
    };
  }

  /**
   * Marks a widget as a floating overlay (expanded panel): it is allowed to cover its neighbours,
   * neighbours do not move away from it, and its own size changes do not trigger auto-placement.
   */
  public static setOverlay(id: string, on: boolean): void {
    if (on) this.overlays.add(id);
    else this.overlays.delete(id);
  }

  public static isDragging(id: string): boolean {
    return this.activeDrag?.id === id && this.activeDrag.element.classList.contains('is-dragging');
  }

  /** Re-clamp after a widget changes size or becomes visible. */
  public static refresh(id: string): void {
    const reg = this.registered.get(id);
    if (!reg || this.overlays.has(id)) return;
    if (reg.static) {
      // Static widgets have no saved position — the placement callback is the source of truth.
      const p = reg.defaultPos();
      this.setPosition(reg.element, p.x, p.y);
      return;
    }
    const saved = this.loadPosition(id);
    const pos = saved ?? reg.defaultPos();
    this.setPosition(reg.element, pos.x, pos.y);
    this.settle(id, false, !saved);
  }

  // ---------------------------------------------------------------- input

  public static handleGlobalMouseDown(x: number, y: number, button = 'left'): void {
    if (button === 'right') return;
    if (this.activeDrag) return;

    const hit = document.elementFromPoint(x, y);

    for (const l of this.clickListeners) {
      try {
        l(x, y, hit);
      } catch { /* listener errors must not break input */ }
    }

    // Launcher icons: defer launch so a drag can move them between folders/dock.
    if (IconDragManager.handleMouseDown(x, y, hit instanceof Element ? hit : null)) {
      return;
    }

    const actionEl =
      hit instanceof Element
        ? hit.closest<HTMLElement>('[data-action], [data-desktop-action], [data-seek]')
        : null;
    if (actionEl) {
      actionEl.classList.add('is-pressed');
      setTimeout(() => actionEl.classList.remove('is-pressed'), 160);
      // Real cursor coordinates: needed by position-aware controls (seek bar).
      // `element.click()` would report clientX = 0.
      actionEl.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: x, clientY: y }));
      return;
    }

    // Prefer the element the browser actually hit (respects z-order: an expanded panel covering
    // a neighbour must win), then fall back to rect containment.
    const hitWidget = hit instanceof Element ? hit.closest<HTMLElement>('.widget') : null;
    for (const [id, reg] of this.registered) {
      if (reg.static) continue;
      const { element } = reg;
      if (!this.isVisible(element)) continue;
      const r = this.rectOf(element);
      const inside = hitWidget ? hitWidget === element : x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h;
      if (inside) {
        this.pressed = { x, y, id };
        this.activeDrag = { id, element, offsetX: x - r.x, offsetY: y - r.y };
        return;
      }
    }
  }

  public static hasActiveDrag(): boolean {
    return !!this.activeDrag;
  }

  public static clearHover(): void {
    this.currentHovered?.classList.remove('is-hovered');
    this.currentHovered = null;
    LauncherTooltip.hide(true);
  }

  public static handleGlobalMouseMove(x: number, y: number): void {
    IconDragManager.handleMouseMove(x, y);
    if (IconDragManager.isDragging()) {
      LauncherTooltip.hide(true);
      return;
    }

    const hit = document.elementFromPoint(x, y);
    const hoverable = hit instanceof Element ? hit.closest<HTMLElement>(HOVERABLE) : null;
    if (hoverable !== this.currentHovered) {
      this.currentHovered?.classList.remove('is-hovered');
      hoverable?.classList.add('is-hovered');
      this.currentHovered = hoverable;
      if (this.activeDrag) LauncherTooltip.hide(true);
      else LauncherTooltip.fromHoverTarget(hoverable);
    }

    if (!this.activeDrag) return;
    LauncherTooltip.hide(true);
    const { id, element, offsetX, offsetY } = this.activeDrag;
    if (this.pressed && Math.hypot(x - this.pressed.x, y - this.pressed.y) < 4) return;
    this.pressed = null;
    element.classList.add('is-dragging');
    element.classList.remove('is-settling');

    const raw: Rect = { x: x - offsetX, y: y - offsetY, w: element.offsetWidth, h: element.offsetHeight };
    const { rect, guides } = this.snap(id, raw);
    this.setPosition(element, rect.x, rect.y);
    this.drawGuides(guides);
  }

  public static handleGlobalContextMenu(x: number, y: number): void {
    LauncherContextMenu.close();
    const hit = document.elementFromPoint(x, y);

    // RMB on an open menu (or its chrome) — ignore; left-click path handles items.
    if (hit instanceof Element && hit.closest('.launcher-context-menu')) return;

    const target = hit instanceof Element ? hit.closest<HTMLElement>(HOVERABLE) : null;
    // Menu rows are hoverable but must not open a nested item menu.
    if (target?.closest('.lcm-row, .lcm-quick-btn')) return;
    if (target?.closest('.w-btn-action')) return;

    if (!target) {
      // Over another widget (clock/media/…) — swallow only; desktop menu is for empty space.
      if (hit instanceof Element && hit.closest('.widget')) return;
      LauncherContextMenu.openDesktop(x, y);
      return;
    }

    let kind: 'item' | 'group' = 'item';
    let id = target.getAttribute('data-id');
    let name = target.getAttribute('data-name') ?? '';

    const folderWidget = target.closest<HTMLElement>('.folder-tile-widget');
    if (folderWidget && !target.classList.contains('launcher-item')) {
      kind = 'group';
      id = folderWidget.getAttribute('data-group-id');
      name = folderWidget.querySelector('.folder-tile-title')?.textContent?.trim() ?? name;
    }

    if (!id) {
      if (target.closest('.widget')) return;
      LauncherContextMenu.openDesktop(x, y);
      return;
    }
    LauncherContextMenu.open({ id, name, kind, x, y });
  }

  public static handleGlobalMouseUp(x = 0, y = 0, button = 'left'): void {
    if (button === 'right') {
      this.activeDrag = null;
      this.pressed = null;
      IconDragManager.cancel();
      return;
    }

    if (IconDragManager.handleMouseUp(x, y)) {
      this.activeDrag = null;
      this.pressed = null;
      return;
    }

    if (!this.activeDrag) return;
    const { element, id } = this.activeDrag;
    const wasDragging = element.classList.contains('is-dragging');
    if (wasDragging) {
      element.classList.remove('is-dragging');
      this.drawGuides([]);
      this.settle(id, true, true);
      for (const l of this.dragEndListeners) {
        try {
          l(id);
        } catch { /* listener errors must not break input */ }
      }
    } else if (this.pressed) {
      const { x, y } = this.pressed;
      const hit = document.elementFromPoint(x, y);
      hit?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: x, clientY: y }));
    }
    this.activeDrag = null;
    this.pressed = null;
  }

  /** Wheel events from the global hook: scroll the nearest scrollable element under the cursor. */
  public static handleGlobalWheel(x: number, y: number, delta: number): boolean {
    const hit = document.elementFromPoint(x, y);
    for (const l of this.wheelListeners) {
      try {
        if (l(x, y, delta, hit)) return true;
      } catch { /* listener errors must not break input */ }
    }
    let node: Element | null = hit;
    while (node && node !== document.body) {
      if (node instanceof HTMLElement && node.scrollHeight > node.clientHeight + 1) {
        const oy = getComputedStyle(node).overflowY;
        if (oy === 'auto' || oy === 'scroll') {
          node.scrollBy({ top: (-delta / 120) * 56, behavior: 'smooth' });
          return true;
        }
      }
      node = node.parentElement;
    }
    return false;
  }

  // ---------------------------------------------------------------- smart guides

  private static snap(id: string, r: Rect): { rect: Rect; guides: Guide[] } {
    const W = window.innerWidth;
    const H = window.innerHeight;
    const others = this.otherRects(id);

    // Candidate lines: [value, kind, related rect]
    type Cand = { v: number; ref: Rect | null };
    const xs: Cand[] = [
      { v: MARGIN, ref: null },
      { v: W - MARGIN, ref: null },
      { v: W / 2, ref: null },
    ];
    const ys: Cand[] = [
      { v: MARGIN, ref: null },
      { v: H - MARGIN, ref: null },
      { v: H / 2, ref: null },
    ];
    for (const o of others) {
      xs.push({ v: o.x, ref: o }, { v: o.x + o.w, ref: o }, { v: o.x + o.w / 2, ref: o });
      ys.push({ v: o.y, ref: o }, { v: o.y + o.h, ref: o }, { v: o.y + o.h / 2, ref: o });
    }

    // Spacing candidates: preferred GAP plus every gap already used between aligned neighbours.
    const gaps = new Set<number>([GAP]);
    for (const a of others) {
      for (const b of others) {
        if (a === b) continue;
        const hg = b.x - (a.x + a.w);
        if (hg > 0 && hg < 160 && overlap(a.y, a.h, b.y, b.h)) gaps.add(Math.round(hg));
        const vg = b.y - (a.y + a.h);
        if (vg > 0 && vg < 160 && overlap(a.x, a.w, b.x, b.w)) gaps.add(Math.round(vg));
      }
    }

    let best: { d: number; x: number; g: Guide | null } = { d: SNAP + 1, x: r.x, g: null };
    let bestY: { d: number; y: number; g: Guide | null } = { d: SNAP + 1, y: r.y, g: null };

    // Alignment snapping (edges & centres)
    const anchorsX = [0, r.w / 2, r.w];
    for (const c of xs) {
      for (const a of anchorsX) {
        const d = Math.abs(r.x + a - c.v);
        if (d < best.d) best = { d, x: c.v - a, g: { axis: 'x', pos: c.v, from: 0, to: 0 } };
      }
    }
    const anchorsY = [0, r.h / 2, r.h];
    for (const c of ys) {
      for (const a of anchorsY) {
        const d = Math.abs(r.y + a - c.v);
        if (d < bestY.d) bestY = { d, y: c.v - a, g: { axis: 'y', pos: c.v, from: 0, to: 0 } };
      }
    }

    // Gap snapping: sit exactly one known gap away from a neighbour on the same row/column.
    for (const o of others) {
      for (const g of gaps) {
        if (overlap(r.y, r.h, o.y, o.h)) {
          for (const x of [o.x + o.w + g, o.x - g - r.w]) {
            const d = Math.abs(r.x - x);
            if (d < best.d) {
              const lineX = x > o.x ? o.x + o.w + g / 2 : o.x - g / 2;
              best = { d, x, g: { axis: 'x', pos: lineX, from: 0, to: 0 } };
            }
          }
        }
        if (overlap(r.x, r.w, o.x, o.w)) {
          for (const y of [o.y + o.h + g, o.y - g - r.h]) {
            const d = Math.abs(r.y - y);
            if (d < bestY.d) {
              const lineY = y > o.y ? o.y + o.h + g / 2 : o.y - g / 2;
              bestY = { d, y, g: { axis: 'y', pos: lineY, from: 0, to: 0 } };
            }
          }
        }
      }
    }

    const out: Rect = { ...r, x: best.g ? best.x : r.x, y: bestY.g ? bestY.y : r.y };
    const guides: Guide[] = [];
    if (best.g) {
      // Vertical line spanning the moving widget and every rect touching this line.
      const touching = [out, ...others.filter((o) => near(o.x, best.g!.pos) || near(o.x + o.w, best.g!.pos) || near(o.x + o.w / 2, best.g!.pos))];
      best.g.from = Math.min(...touching.map((t) => t.y)) - 8;
      best.g.to = Math.max(...touching.map((t) => t.y + t.h)) + 8;
      if (touching.length === 1) { best.g.from = 0; best.g.to = H; }
      guides.push(best.g);
    }
    if (bestY.g) {
      const touching = [out, ...others.filter((o) => near(o.y, bestY.g!.pos) || near(o.y + o.h, bestY.g!.pos) || near(o.y + o.h / 2, bestY.g!.pos))];
      bestY.g.from = Math.min(...touching.map((t) => t.x)) - 8;
      bestY.g.to = Math.max(...touching.map((t) => t.x + t.w)) + 8;
      if (touching.length === 1) { bestY.g.from = 0; bestY.g.to = W; }
      guides.push(bestY.g);
    }
    return { rect: out, guides };
  }

  private static drawGuides(guides: Guide[]): void {
    if (!this.guideLayer) {
      const layer = document.createElement('div');
      layer.className = 'drag-guides';
      (document.getElementById('widgetsOverlay') ?? document.body).appendChild(layer);
      this.guideLayer = layer;
    }
    const layer = this.guideLayer;
    while (layer.children.length < guides.length) layer.appendChild(document.createElement('i'));
    Array.from(layer.children).forEach((node, i) => {
      const el = node as HTMLElement;
      const g = guides[i];
      if (!g) {
        el.style.opacity = '0';
        return;
      }
      el.style.opacity = '1';
      el.className = g.axis === 'x' ? 'guide-v' : 'guide-h';
      if (g.axis === 'x') {
        el.style.left = `${Math.round(g.pos)}px`;
        el.style.top = `${Math.round(g.from)}px`;
        el.style.width = '';
        el.style.height = `${Math.round(g.to - g.from)}px`;
      } else {
        el.style.top = `${Math.round(g.pos)}px`;
        el.style.left = `${Math.round(g.from)}px`;
        el.style.height = '';
        el.style.width = `${Math.round(g.to - g.from)}px`;
      }
    });
  }

  // ---------------------------------------------------------------- overlap resolution

  /**
   * Moves the widget to the closest free spot if it overlaps another one.
   * @param animate smooth glide (after a user drop)
   * @param persist save the final position
   */
  private static settle(id: string, animate: boolean, persist: boolean): void {
    const reg = this.registered.get(id);
    if (!reg || !this.isVisible(reg.element)) return;
    const el = reg.element;
    const r = this.rectOf(el);
    const others = this.otherRects(id);
    let target = { x: r.x, y: r.y };

    // An expanded overlay may cover neighbours; it only gets clamped to the screen.
    if (!this.overlays.has(id) && others.some((o) => intersects(r, o, PAD))) {
      target = this.findFreeSpot(r, others);
    }

    if (target.x !== r.x || target.y !== r.y) {
      if (animate) {
        el.classList.add('is-settling');
        setTimeout(() => el.classList.remove('is-settling'), 320);
      }
      this.setPosition(el, target.x, target.y);
    }
    if (persist) {
      this.savePosition(id, { x: parseFloat(el.style.left), y: parseFloat(el.style.top) });
    }
  }

  private static findFreeSpot(r: Rect, others: Rect[]): WidgetPosition {
    const W = window.innerWidth;
    const H = window.innerHeight;
    const fits = (x: number, y: number) =>
      x >= EDGE && y >= EDGE && x + r.w <= W - EDGE && y + r.h <= H - EDGE &&
      !others.some((o) => intersects({ x, y, w: r.w, h: r.h }, o, PAD));

    // 1) Natural candidates: flush against each neighbour, aligned to its edges.
    const cands: WidgetPosition[] = [];
    for (const o of others) {
      for (const y of [r.y, o.y, o.y + o.h - r.h]) {
        cands.push({ x: o.x + o.w + GAP, y }, { x: o.x - GAP - r.w, y });
      }
      for (const x of [r.x, o.x, o.x + o.w - r.w]) {
        cands.push({ x, y: o.y + o.h + GAP }, { x, y: o.y - GAP - r.h });
      }
    }
    let best: WidgetPosition | null = null;
    let bestD = Infinity;
    for (const c of cands) {
      if (!fits(c.x, c.y)) continue;
      const d = Math.hypot(c.x - r.x, c.y - r.y);
      if (d < bestD) { bestD = d; best = c; }
    }
    if (best) return best;

    // 2) Fallback: expanding ring search on an 8 px grid.
    for (let rad = 8; rad < Math.max(W, H); rad += 8) {
      for (let a = 0; a < 360; a += 10) {
        const x = Math.round(r.x + Math.cos((a * Math.PI) / 180) * rad);
        const y = Math.round(r.y + Math.sin((a * Math.PI) / 180) * rad);
        if (fits(x, y)) return { x, y };
      }
    }
    return { x: r.x, y: r.y }; // screen is full — leave it where it is
  }

  // ---------------------------------------------------------------- helpers

  private static clampAll(): void {
    for (const id of this.registered.keys()) this.refresh(id);
  }

  private static isVisible(el: HTMLElement): boolean {
    return el.isConnected && el.style.display !== 'none' && el.offsetWidth > 0;
  }

  /** Layout rect, ignoring CSS transforms (drag scale, entrance animation). */
  private static rectOf(el: HTMLElement): Rect {
    return {
      x: parseFloat(el.style.left) || el.offsetLeft,
      y: parseFloat(el.style.top) || el.offsetTop,
      w: el.offsetWidth,
      h: el.offsetHeight,
    };
  }

  private static otherRects(id: string): Rect[] {
    const out: Rect[] = [];
    for (const [oid, { element }] of this.registered) {
      if (oid === id || this.overlays.has(oid) || !this.isVisible(element)) continue;
      const r = this.rectOf(element);
      if (r.x < -1000) continue; // not placed yet
      out.push(r);
    }
    return out;
  }

  private static setPosition(element: HTMLElement, x: number, y: number): void {
    const w = element.offsetWidth || 84;
    const h = element.offsetHeight || 104;
    const winW = window.innerWidth || 1920;
    const winH = window.innerHeight || 1080;
    const maxX = Math.max(EDGE, winW - w - EDGE);
    const maxY = Math.max(EDGE, winH - h - EDGE);
    element.style.left = `${Math.round(Math.min(Math.max(EDGE, x), maxX))}px`;
    element.style.top = `${Math.round(Math.min(Math.max(EDGE, y), maxY))}px`;
  }

  private static savePosition(id: string, pos: WidgetPosition): void {
    try {
      localStorage.setItem(`lw2_pos_${id}`, JSON.stringify(pos));
    } catch { /* storage unavailable */ }
  }

  private static loadPosition(id: string): WidgetPosition | null {
    try {
      const raw = localStorage.getItem(`lw2_pos_${id}`);
      if (!raw) return null;
      const p = JSON.parse(raw) as WidgetPosition;
      return typeof p.x === 'number' && typeof p.y === 'number' ? p : null;
    } catch {
      return null;
    }
  }
}

function overlap(a: number, al: number, b: number, bl: number): boolean {
  return a < b + bl && b < a + al;
}

function intersects(a: Rect, b: Rect, pad: number): boolean {
  return a.x < b.x + b.w + pad && b.x < a.x + a.w + pad && a.y < b.y + b.h + pad && b.y < a.y + a.h + pad;
}

function near(a: number, b: number): boolean {
  return Math.abs(a - b) < 1;
}
