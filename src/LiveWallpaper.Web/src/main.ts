import './style.css';
import type {
  HostToWebMessage,
  CollageMessage,
  CollageImageItem,
  ContextModeMessage,
  AtmosphereSignalsMessage,
} from './types/ipc';
import { LayoutRegistry } from './layouts/registry';
import type { ICollageLayout, Pic, Slot, Viewport } from './layouts/contract';
import type { IWidget } from './widgets/contract';
import { ClockWidget } from './widgets/clock';
import { AgendaWidget } from './widgets/agenda-widget';
import { WeatherWidget } from './widgets/weather';
import { SystemWidget } from './widgets/system';
import { MediaWidget } from './widgets/media';
import { LauncherWidgetManager } from './widgets/launcher';
import { ServerWidget } from './widgets/server';
import { TelegramWidget } from './widgets/telegram';
import { DraggableWidgetManager } from './widgets/draggable';
import { IconDragManager } from './widgets/icon-drag';
import { LauncherContextMenu } from './widgets/context-menu';
import { handleConfirmResult } from './widgets/confirm-dialog';
import { handlePromptNameResult } from './widgets/name-dialog';
import { BackgroundRenderer, buildPalette, vivid } from './background';
import { YoutubeWallpaper } from './youtube-wallpaper';
import { WallpaperPlaylist, type WallpaperPlaylistConfig } from './wallpaper-playlist';

interface LoadedCollage {
  layout: ICollageLayout;
  slots: Slot[];
  images: (Pic | null)[];
  hover: number[]; // eased 0..1 hover amount per slot
  start: number; // performance.now() when the enter animation began
  delays: number[]; // per-slot enter delay, ms (staggered deal-in)
}

class LiveWallpaperApp {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private bg: BackgroundRenderer;
  private dpr = window.devicePixelRatio || 1;
  private width = 0;
  private height = 0;
  private mouseX = 0;
  private mouseY = 0;
  private smoothX = 0;
  private smoothY = 0;
  private isPaused = false;
  private targetFps = 30;
  private lastFrameTime = 0;

  private widgets: IWidget[] = [];
  private clockWidget!: ClockWidget;
  private agendaWidget!: AgendaWidget;
  private weatherWidget!: WeatherWidget;
  private systemWidget!: SystemWidget;
  private mediaWidget!: MediaWidget;
  private launcherManager!: LauncherWidgetManager;
  private serverWidget!: ServerWidget;
  private telegramWidget!: TelegramWidget;

  private currentCollage: LoadedCollage | null = null;
  private previousCollage: LoadedCollage | null = null;
  private lastInputAt = 0;
  private loadToken = 0;
  private lastCollageMsg: (CollageMessage & { ambientColor?: string }) | null = null;
  private pointerOverWidget = false;
  private inputCapturePosted = false;

  private atmosphereBeacon: HTMLElement | null = null;
  private atmosphereProdAlert: HTMLElement | null = null;
  private atmosphereCallChip: HTMLElement | null = null;
  private hasAtmosphereActive = false;
  private youtube: YoutubeWallpaper;
  private wallpaperPlaylist: WallpaperPlaylist;
  constructor() {
    this.canvas = document.getElementById('wallpaperCanvas') as HTMLCanvasElement;
    const context = this.canvas.getContext('2d');
    if (!context) throw new Error('Failed to get 2D context');
    this.ctx = context;
    this.bg = new BackgroundRenderer(this.ctx);
    const videoHost = document.getElementById('videoBackground') ?? document.createElement('div');
    const imageHost = document.getElementById('imageBackground') ?? document.createElement('div');
    this.youtube = new YoutubeWallpaper(videoHost);
    this.wallpaperPlaylist = new WallpaperPlaylist(this.youtube, videoHost, imageHost);

    this.initCanvasSize();
    this.setupListeners();
    this.setupWidgets();
    this.requestNextFrame();
    window.chrome?.webview?.postMessage({ type: 'ready' });
  }

  private initCanvasSize(): void {
    this.dpr = window.devicePixelRatio || 1;
    this.width = window.innerWidth;
    this.height = window.innerHeight;
    this.canvas.width = Math.round(this.width * this.dpr);
    this.canvas.height = Math.round(this.height * this.dpr);
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);

    this.mouseX = this.smoothX = this.width / 2;
    this.mouseY = this.smoothY = this.height / 2;
    this.bg.resize(this.width, this.height);

    const vp = this.viewport();
    for (const c of [this.currentCollage, this.previousCollage]) {
      if (c) c.slots = c.layout.computeSlots(vp, c.images.length);
    }
  }

  private viewport(): Viewport {
    return { width: this.width, height: this.height, devicePixelRatio: this.dpr };
  }

  private setupListeners(): void {
    window.addEventListener('resize', () => this.initCanvasSize());
    window.chrome?.webview?.addEventListener('message', (e: { data: unknown }) => {
      try {
        this.handleHostMessage(e.data as HostToWebMessage);
      } catch (err) {
        console.error('host message failed', err);
      }
    });
  }

  /** Tell the mouse hook to swallow Explorer events while the cursor is over our chrome. */
  private setInputCapture(active: boolean): void {
    this.pointerOverWidget = active;
    if (active === this.inputCapturePosted) return;
    this.inputCapturePosted = active;
    window.chrome?.webview?.postMessage({ type: 'input_capture', active });
  }

  private handleHostMessage(message: HostToWebMessage): void {
    if (!message || typeof message !== 'object') return;

    switch (message.type) {
      case 'mouse': {
        // Host sends physical pixels relative to the primary monitor.
        const x = message.x / this.dpr;
        const y = message.y / this.dpr;
        this.lastInputAt = performance.now();
        this.mouseX = x;
        this.mouseY = y;
        if (
          message.overDesktop === false &&
          !DraggableWidgetManager.hasActiveDrag() &&
          !IconDragManager.isDragging()
        ) {
          if (LauncherContextMenu.isOpen()) LauncherContextMenu.close();
          this.setInputCapture(false);
          DraggableWidgetManager.clearHover();
          break;
        }
        const under = document.elementFromPoint(x, y);
        const overInteractive = !!(
          (under instanceof Element &&
            under.closest('.widget, .launcher-context-menu, .lw-name-dialog-root')) ||
          LauncherContextMenu.isOpen() ||
          IconDragManager.isDragging() ||
          DraggableWidgetManager.hasActiveDrag()
        );
        this.setInputCapture(overInteractive);
        DraggableWidgetManager.handleGlobalMouseMove(x, y);
        break;
      }
      case 'prompt_name_result':
        handlePromptNameResult(message);
        break;
      case 'confirm_result':
        handleConfirmResult(message);
        break;

      case 'mouse_down': {
        const x = message.x / this.dpr;
        const y = message.y / this.dpr;
        this.lastInputAt = performance.now();
        if (message.button === 'right') {
          DraggableWidgetManager.handleGlobalContextMenu(x, y);
          // Keep Explorer from eating the next click while the menu is open
          // (cursor may not move between RMB and LMB).
          this.setInputCapture(LauncherContextMenu.isOpen());
        } else {
          this.bg.addRipple(x, y);
          DraggableWidgetManager.handleGlobalMouseDown(x, y, message.button);
          const under = document.elementFromPoint(x, y);
          this.setInputCapture(
            LauncherContextMenu.isOpen() ||
              !!(under instanceof Element && under.closest('.widget, .launcher-context-menu, .lw-name-dialog-root')),
          );
        }
        break;
      }

      case 'wheel':
        DraggableWidgetManager.handleGlobalWheel(message.x / this.dpr, message.y / this.dpr, message.delta);
        break;

      case 'mouse_up':
        DraggableWidgetManager.handleGlobalMouseUp(
          message.x / this.dpr,
          message.y / this.dpr,
          message.button,
        );
        break;

      case 'pause_state':
        this.isPaused = message.isPaused;
        this.wallpaperPlaylist.setPaused(this.isPaused);
        if (this.isPaused) {
          this.widgets.forEach((w) => w.pause());
        } else {
          this.widgets.forEach((w) => w.resume());
          this.requestNextFrame();
        }
        break;

      case 'weather':
        this.weatherWidget.update(message.data);
        break;

      case 'today':
        this.agendaWidget.setToday(message.data);
        break;

      case 'telegram':
        this.telegramWidget.update(message.data);
        break;

      case 'media':
        this.mediaWidget.update(message.data);
        break;

      case 'launcher':
        this.launcherManager.update(message);
        break;

      case 'serverState':
        this.serverWidget.update(message);
        break;

      case 'serverMetrics':
        this.serverWidget.update(message);
        break;

      case 'privacy_mode':
        this.serverWidget.update(message);
        break;

      case 'telemetry':
        this.systemWidget.update(message);
        break;

      case 'config':
        this.targetFps = Math.max(20, message.config?.performance?.fpsIdle || 30);
        this.applyVideoWallpaper(message.config?.wallpaper);
        if (message.config?.widgets) {
          this.clockWidget.update({ visible: message.config.widgets.showClock });
          this.clockWidget.applyConfig({
            position: message.config.widgets.clockPosition,
            font: message.config.widgets.clockFont,
          });
          this.systemWidget.setEnabled(message.config.widgets.showSystemMetrics !== false);
        }
        {
          const serverCfg = (message.config as { server?: { enabled?: boolean } } | undefined)?.server;
          this.serverWidget.setEnabled(serverCfg?.enabled !== false);
        }
        {
          // Layout picked in the tray: re-lay out the current pictures right away.
          const c = message.config?.collage;
          const last = this.lastCollageMsg;
          if (c && last && !c.shuffleStyle && c.style && c.style.toLowerCase() !== last.style.toLowerCase()) {
            this.loadNewCollage({ ...last, style: c.style });
          }
        }
        break;

      case 'context_mode':
        this.applyContextMode(message);
        break;

      case 'atmosphere_signals':
        this.applyAtmosphereSignals(message);
        break;

      case 'collage':
        this.loadNewCollage(message);
        break;
    }
  }

  private applyVideoWallpaper(
    wallpaper?: {
      enabled?: boolean;
      youtubeUrl?: string;
      soundEnabled?: boolean;
      sources?: { type: string; value: string; durationSeconds?: number }[];
      rotationSeconds?: number;
      shuffle?: boolean;
      systemWallpaperUrl?: string;
    },
  ): void {
    const on = this.wallpaperPlaylist.apply(wallpaper as unknown as WallpaperPlaylistConfig | undefined);
    this.wallpaperPlaylist.setPaused(this.isPaused);
    this.bg.setVideoBackdrop(on);
    document.body.classList.toggle('has-video-bg', on);
    this.lastInputAt = performance.now();
  }

  private loadNewCollage(msg: CollageMessage & { ambientColor?: string }): void {
    this.lastCollageMsg = msg;
    const token = ++this.loadToken;
    const layout = LayoutRegistry.get(msg.style);
    const slots = layout.computeSlots(this.viewport(), msg.items.length);

    // Palette & accent from the pins' dominant colors.
    const colors = msg.items.map((i) => i.dominantColor);
    if (msg.ambientColor) colors.push(msg.ambientColor);
    const palette = buildPalette(colors);
    this.bg.setPalette(palette);
    this.applyAccent(palette);

    // Only decode as many pictures as the layout can show, each scaled to its slot size.
    const used = msg.items.slice(0, slots.length);
    Promise.all(used.map((item, i) => this.prepare(item, slots[i]))).then((images) => {
      if (token !== this.loadToken) {
        images.forEach((b) => b?.close());
        return; // a newer collage arrived meanwhile
      }
      this.retire(this.previousCollage);
      this.previousCollage = this.currentCollage;
      if (this.previousCollage) this.previousCollage.start = performance.now();
      const start = performance.now() + (this.previousCollage ? 160 : 0);
      this.currentCollage = { layout, slots, images, hover: slots.map(() => 0), start, delays: this.dealOrder(slots) };
      this.lastInputAt = performance.now();
    });
  }

  /**
   * Loads a pin and turns it into a GPU-friendly ImageBitmap scaled to ~its on-screen size.
   * Drawing a 4000px JPEG every frame is what made transitions stutter.
   * NOTE: no crossOrigin — the pins host does not send CORS headers.
   */
  private prepare(item: CollageImageItem, slot: Slot | undefined): Promise<Pic | null> {
    return new Promise((resolve) => {
      const img = new Image();
      img.decoding = 'async';
      img.onload = async () => {
        try {
          const need = slot ? Math.max(slot.width, slot.height) * this.dpr * 1.25 : 1200;
          const k = Math.min(1, need / Math.max(img.naturalWidth, img.naturalHeight));
          const bmp = await createImageBitmap(img, {
            resizeWidth: Math.max(1, Math.round(img.naturalWidth * k)),
            resizeHeight: Math.max(1, Math.round(img.naturalHeight * k)),
            resizeQuality: 'high',
          });
          resolve(bmp);
        } catch {
          resolve(null);
        }
      };
      img.onerror = () => {
        console.warn('Pin failed to load:', item.url);
        resolve(null);
      };
      img.src = item.url;
    });
  }

  /** Enter delays: cards deal in from the screen centre outwards. */
  private dealOrder(slots: Slot[]): number[] {
    const cx = this.width / 2;
    const cy = this.height / 2;
    const order = slots
      .map((s, i) => ({ i, d: Math.hypot(s.x - cx, (s.y - cy) * 0.6) }))
      .sort((a, b) => a.d - b.d);
    const step = Math.min(70, 900 / Math.max(1, slots.length));
    const delays = new Array<number>(slots.length).fill(0);
    order.forEach((o, rank) => (delays[o.i] = rank * step));
    return delays;
  }

  private retire(c: LoadedCollage | null): void {
    c?.images.forEach((b) => b?.close());
  }

  private applyAccent(palette: [number, number, number][]): void {
    const root = document.documentElement.style;
    const a = vivid(palette[0], 0.72, 0.6);
    const b = vivid(palette[1] ?? palette[0], 0.74, 0.55);
    root.setProperty('--accent-rgb', a.map((v) => Math.round(v)).join(', '));
    root.setProperty('--accent-2-rgb', b.map((v) => Math.round(v)).join(', '));
  }

  private applyContextMode(msg: ContextModeMessage): void {
    const { widgets, effectiveMode } = msg;

    this.bg.setContextMode(effectiveMode, widgets.palettePreset);

    const toggle = (selector: string, show: boolean) => {
      document.querySelectorAll(selector).forEach((el) => {
        el.classList.toggle('is-mode-hidden', !show);
      });
    };

    toggle('.clock-widget', widgets.showClock);
    toggle('.weather-widget', widgets.showWeather);
    toggle('.system-widget', widgets.showSystem);
    toggle('.srv', widgets.showServer);
    toggle('.media-widget', widgets.showMedia);
    toggle('.dock-widget, .folder-tile', widgets.showLauncher);
    toggle('.tg', widgets.showTelegram !== false);
    toggle('.agenda-widget', widgets.showToday !== false);
    this.agendaWidget.setAgendaEnabled(widgets.showToday !== false);

    this.lastInputAt = performance.now();
  }

  private applyAtmosphereSignals(msg: AtmosphereSignalsMessage): void {
    const s = msg.signals;
    this.bg.setAtmosphere(s);
    this.hasAtmosphereActive = s.prodAlert || s.upcomingCall || s.lowBalance;

    if (this.atmosphereBeacon) {
      this.atmosphereBeacon.classList.toggle('is-visible', !!s.lowBalance);
      const textEl = this.atmosphereBeacon.querySelector('.atmosphere-beacon-text');
      if (textEl) {
        textEl.textContent = s.lowBalanceText || 'Низкий баланс Timeweb';
      }
    }

    if (this.atmosphereProdAlert) {
      this.atmosphereProdAlert.classList.toggle('is-visible', !!s.prodAlert);
      const textEl = this.atmosphereProdAlert.querySelector('.atmosphere-prod-text');
      if (textEl) {
        textEl.textContent = s.prodAlertText || 'Сбой прода!';
      }
    }

    if (this.atmosphereCallChip) {
      this.atmosphereCallChip.classList.toggle('is-visible', !!s.upcomingCall);
      this.atmosphereCallChip.classList.toggle('has-prod-offset', !!s.prodAlert);
      const textEl = this.atmosphereCallChip.querySelector('.atmosphere-call-text');
      if (textEl) {
        textEl.textContent = s.upcomingCallTitle || 'Скоро созвон';
      }
    }

    this.lastInputAt = performance.now();
  }

  private setupWidgets(): void {
    const overlay = document.getElementById('widgetsOverlay') || document.body;
    this.systemWidget = new SystemWidget();
    this.clockWidget = new ClockWidget();
    this.agendaWidget = new AgendaWidget();
    this.weatherWidget = new WeatherWidget();
    this.mediaWidget = new MediaWidget();
    this.launcherManager = new LauncherWidgetManager();
    this.serverWidget = new ServerWidget();
    this.telegramWidget = new TelegramWidget();
    for (const w of [this.systemWidget, this.clockWidget, this.agendaWidget, this.weatherWidget, this.mediaWidget, this.launcherManager, this.serverWidget, this.telegramWidget]) {
      w.mount(overlay);
      this.widgets.push(w);
    }

    // Atmosphere elements:
    // 1. Amber beacon at low balance
    const beacon = document.createElement('div');
    beacon.className = 'atmosphere-beacon';
    beacon.innerHTML = `
      <i class="atmosphere-beacon-dot"></i>
      <span class="atmosphere-beacon-text">Низкий баланс Timeweb</span>
    `;
    beacon.addEventListener('click', () => {
      window.chrome?.webview?.postMessage({ type: 'server_open', target: 'timeweb' });
    });
    overlay.appendChild(beacon);
    this.atmosphereBeacon = beacon;

    // 2. Production down alert banner
    const prodAlert = document.createElement('div');
    prodAlert.className = 'atmosphere-prod-alert';
    prodAlert.innerHTML = `
      <i class="atmosphere-prod-dot"></i>
      <span class="atmosphere-prod-text">Сбой прода!</span>
    `;
    prodAlert.addEventListener('click', () => {
      window.chrome?.webview?.postMessage({ type: 'server_open', target: 'ssh' });
    });
    overlay.appendChild(prodAlert);
    this.atmosphereProdAlert = prodAlert;

    // 3. Upcoming call chip
    const callChip = document.createElement('div');
    callChip.className = 'atmosphere-call-chip';
    callChip.innerHTML = `<span class="atmosphere-call-text">Скоро созвон</span>`;
    overlay.appendChild(callChip);
    this.atmosphereCallChip = callChip;
  }

  /** Index of the top-most slot under the cursor (rotated-rect hit test), or -1. */
  private hitSlot(c: LoadedCollage, px: number, py: number): number {
    if (this.pointerOverWidget) return -1;
    let best = -1;
    let bestZ = -Infinity;
    for (let i = 0; i < c.slots.length; i++) {
      const s = c.slots[i];
      if (s.opacity === 0 || !c.images[i]) continue; // decorative / empty cells don't react
      const depth = s.zIndex * 0.15 + 0.5;
      const dx = this.mouseX - (s.x + px * depth);
      const dy = this.mouseY - (s.y + py * depth);
      const a = (-s.rotationDeg * Math.PI) / 180;
      const lx = dx * Math.cos(a) - dy * Math.sin(a);
      const ly = dx * Math.sin(a) + dy * Math.cos(a);
      const z = i + c.hover[i] * 100;
      if (Math.abs(lx) <= s.width / 2 && Math.abs(ly) <= s.height / 2 && z > bestZ) {
        bestZ = z;
        best = i;
      }
    }
    return best;
  }

  /**
   * @param phase 'enter' — staggered deal-in; 'exit' — whole collage fades/settles away.
   * @param f frame-rate independent smoothing helper
   */
  private drawCollage(c: LoadedCollage, now: number, phase: 'enter' | 'exit', px: number, py: number, f: (k: number) => number): boolean {
    const n = c.slots.length;
    let animating = false;

    if (phase === 'enter' && c.layout.hoverFocus) {
      const hovered = this.hitSlot(c, px, py);
      for (let i = 0; i < n; i++) {
        const target = i === hovered ? 1 : 0;
        c.hover[i] += (target - c.hover[i]) * f(0.16);
        if (Math.abs(target - c.hover[i]) < 0.003) c.hover[i] = target;
        else animating = true;
      }
    }

    const focus = c.hover.reduce((bi, v, i, arr) => (v > (arr[bi] ?? 0) ? i : bi), -1);
    const focusAmt = focus >= 0 ? c.hover[focus] : 0;
    const order = c.slots.map((_, i) => i).sort((a, b) => c.slots[a].x - c.slots[b].x);
    const rank = new Array<number>(n);
    order.forEach((idx, r) => (rank[idx] = r));

    const exitT = phase === 'exit' ? Math.min(1, (now - c.start) / 420) : 0;
    const exitE = 1 - Math.pow(1 - exitT, 2);

    const drawOrder = c.slots.map((_, i) => i).sort((a, b) => c.hover[a] - c.hover[b] || a - b);
    for (const i of drawOrder) {
      const raw = c.slots[i];
      const h = c.hover[i];
      const depth = raw.zIndex * 0.15 + 0.5;

      let alpha = 1;
      let dy = 0;
      let k = 1 + 0.07 * h;
      if (phase === 'enter') {
        const t = Math.min(1, Math.max(0, (now - c.start - c.delays[i]) / 620));
        if (t < 1) animating = true;
        const e = 1 - Math.pow(1 - t, 3); // easeOutCubic
        alpha = e;
        dy = (1 - e) * 34;
        k *= 0.94 + 0.06 * e;
      } else {
        alpha = 1 - exitE;
        dy = -exitE * 14;
        k *= 1 - 0.035 * exitE;
      }
      if (alpha <= 0.001) continue;

      let dx = 0;
      if (focus >= 0 && i !== focus && focusAmt > 0) {
        const dr = rank[i] - rank[focus];
        dx = Math.sign(dr) * (36 / Math.abs(dr)) * focusAmt;
      }
      const slot = {
        ...raw,
        x: raw.x + px * depth + dx,
        y: raw.y + py * depth + dy - 26 * h,
        width: raw.width * k,
        height: raw.height * k,
        rotationDeg: raw.rotationDeg * (1 - 0.65 * h),
      };
      c.layout.renderSlot(this.ctx, slot, c.images[i] ?? null, alpha);
    }
    return animating || (phase === 'exit' && exitT < 1);
  }

  private requestNextFrame(): void {
    if (this.isPaused) return;
    requestAnimationFrame((now) => this.render(now));
  }

  /**
   * Renders at the display refresh rate (100/75 Hz…) while anything moves — transitions, hover,
   * mouse, drags — and drops to the configured idle FPS when the scene is calm.
   */
  private render(timestamp: number): void {
    if (this.isPaused) return;

    const active = timestamp - this.lastInputAt < 1500;
    const delta = timestamp - this.lastFrameTime;
    if (!active && delta < 1000 / this.targetFps - 1) {
      this.requestNextFrame();
      return;
    }
    // Frame-rate independent smoothing: k is "per 60 Hz frame".
    const dtFrames = Math.min(4, Math.max(0.25, delta / (1000 / 60)));
    const f = (k: number) => 1 - Math.pow(1 - k, dtFrames);
    this.lastFrameTime = timestamp;

    const { ctx } = this;
    ctx.clearRect(0, 0, this.width, this.height);
    this.bg.drawBack(timestamp, this.mouseX, this.mouseY, dtFrames);

    this.smoothX += (this.mouseX - this.smoothX) * f(0.08);
    this.smoothY += (this.mouseY - this.smoothY) * f(0.08);
    const px = ((this.smoothX - this.width / 2) / this.width) * 18;
    const py = ((this.smoothY - this.height / 2) / this.height) * 18;
    let moving = Math.abs(this.mouseX - this.smoothX) + Math.abs(this.mouseY - this.smoothY) > 0.5;

    // Collage draws on the canvas above DOM wallpaper (image/video/YouTube).
    // Wallpaper no longer suppresses the pin layer — both can be on together.
    if (this.previousCollage) {
      const still = this.drawCollage(this.previousCollage, timestamp, 'exit', px, py, f);
      if (!still) {
        this.retire(this.previousCollage);
        this.previousCollage = null;
      }
      moving = true;
    }
    if (this.currentCollage) {
      moving = this.drawCollage(this.currentCollage, timestamp, 'enter', px, py, f) || moving;
    }

    this.bg.drawFront(dtFrames, timestamp);
    if (moving || this.hasAtmosphereActive) this.lastInputAt = timestamp;

    this.requestNextFrame();
  }
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        addEventListener(event: string, handler: (e: { data: unknown }) => void): void;
        postMessage(message: unknown): void;
      };
    };
  }
}


if (import.meta.env.DEV) {
  await import('./dev-mock');
}

new LiveWallpaperApp();
