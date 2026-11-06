import './settings.css';
import { icon, Icons, namedIcon } from '../icons';

interface Site { name: string; url: string }
interface WallpaperSource {
  type: 'youtube' | 'video' | 'image';
  value: string;
  durationSeconds: number;
  previewUrl?: string | null;
}

interface PluginField {
  id: string;
  label: string;
  hint?: string;
  kind: string;
  value?: unknown;
  secretExists?: boolean;
}

interface PluginManifest {
  pluginId: string;
  displayName: string;
  description: string;
  fields: PluginField[];
}

interface Settings {
  collage: {
    enabled: boolean;
    pinsDirectory: string;
    style: string;
    shuffleStyle: boolean;
    intervalMinutes: number;
    enableColorHarmony: boolean;
  };
  pinsCount: number;
  weather: { enabled: boolean; city: string; latitude: number; longitude: number };
  performance: { autoPauseFullscreen: boolean; autoPauseBattery: boolean; fpsIdle: number };
  launcher: { enabled: boolean; hideDesktopIcons: boolean };
  server: { enabled: boolean; sshAlias: string; timewebEnabled: boolean; sites: Site[] };
  widgets: { showClock: boolean; clockPosition: string; clockFont: string };
  context: { mode: string };
  atmosphere: { enabled: boolean; enableProdAlert: boolean; enableUpcomingCall: boolean; enableLowBalanceBeacon: boolean };
  setupPalette: {
    enabled: boolean;
    syncWindowsAccent: boolean;
    syncWindowsTerminal: boolean;
    syncRedragonHid: boolean;
    syncOpenRgb: boolean;
    syncRazerChroma: boolean;
    currentAccentHex: string;
    desktopMinIntervalMinutes?: number;
    keyboardIntensity?: number;
    nightKeyboardIntensity?: number;
  };
  taskbar: { enabled: boolean };
  wallpaper: {
    enabled: boolean;
    youtubeUrl: string;
    soundEnabled: boolean;
    rotationSeconds: number;
    shuffle: boolean;
    sources: WallpaperSource[];
  };
  plugins: PluginManifest[];
  autostart: boolean;
  tokenExists: boolean;
  weeekTokenExists: boolean;
  calendarIcsExists: boolean;
}

type HostMsg =
  | { type: 'settings'; data: Settings }
  | { type: 'settings_saved' }
  | { type: 'folder_picked'; path: string; count: number }
  | { type: 'wallpaper_file_picked'; mediaType: 'video' | 'image'; path: string; previewUrl?: string | null }
  | { type: 'geocode_result'; ok: boolean; name?: string; lat?: number; lon?: number }
  | { type: 'token_status'; exists: boolean; saved: boolean }
  | { type: 'weeek_token_status'; exists: boolean; saved: boolean }
  | { type: 'calendar_ics_status'; exists: boolean; saved: boolean }
  | { type: 'diagnostics_result'; text: string }
  | { type: 'error'; message: string };

declare global {
  interface Window {
    chrome?: { webview?: { addEventListener(e: string, h: (ev: { data: unknown }) => void): void; postMessage(m: unknown): void } };
  }
}

const send = (m: unknown) => window.chrome?.webview?.postMessage(m);
const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

const YT_ID = /^[A-Za-z0-9_-]{11}$/;
function youtubeId(input: string): string | null {
  const raw = input.trim();
  if (!raw) return null;
  if (YT_ID.test(raw)) return raw;
  try {
    const url = new URL(raw.includes('://') ? raw : `https://${raw}`);
    const host = url.hostname.replace(/^www\./i, '').toLowerCase();
    if (host === 'youtu.be') {
      const id = url.pathname.split('/').filter(Boolean)[0];
      return id && YT_ID.test(id) ? id : null;
    }
    if (host.includes('youtube')) {
      const v = url.searchParams.get('v');
      if (v && YT_ID.test(v)) return v;
      const parts = url.pathname.split('/').filter(Boolean);
      for (let i = 0; i < parts.length - 1; i++) {
        if (['embed', 'shorts', 'live', 'v'].includes(parts[i]!)) {
          return YT_ID.test(parts[i + 1]!) ? parts[i + 1]! : null;
        }
      }
    }
  } catch { /* ignore */ }
  return null;
}

function sourcePreview(src: WallpaperSource): string | null {
  if (src.type === 'youtube') {
    const id = youtubeId(src.value);
    return id ? `https://i.ytimg.com/vi/${id}/hqdefault.jpg` : null;
  }
  if (src.previewUrl) return src.previewUrl;
  if (src.type === 'image' && /^https?:\/\//i.test(src.value)) return src.value;
  return null;
}

function sourceTitle(src: WallpaperSource): string {
  if (src.type === 'youtube') {
    const id = youtubeId(src.value);
    return id ? `YouTube · ${id}` : 'YouTube';
  }
  const name = src.value.replace(/\\/g, '/').split('/').pop() || src.value;
  return name.length > 42 ? name.slice(0, 39) + '…' : name;
}

function formatDwell(seconds: number): string {
  if (seconds < 60) return `${seconds} с`;
  const m = Math.round(seconds / 60);
  return `${m} мин`;
}

const CORE_NAV: { id: string; label: string; iconHtml: string }[] = [
  { id: 'wallpaper', label: 'Обои', iconHtml: icon(Icons.wallpaper, 18) },
  { id: 'collage', label: 'Коллаж', iconHtml: icon(Icons.collage, 18) },
  { id: 'widgets', label: 'Виджеты', iconHtml: icon(Icons.widgets, 18) },
  { id: 'atmosphere', label: 'Атмосфера', iconHtml: icon(Icons.atmosphere, 18) },
  { id: 'system', label: 'Система', iconHtml: icon(Icons.system, 18) },
];

const LAYOUTS: { key: string; title: string; svg: string }[] = [
  { key: 'fan', title: 'Веер', svg: '<rect x="12" y="8" width="14" height="24" rx="2" transform="rotate(-14 19 20)"/><rect x="38" y="8" width="14" height="24" rx="2" transform="rotate(14 45 20)"/><rect x="25" y="6" width="14" height="26" rx="2"/>' },
  { key: 'bento', title: 'Бенто', svg: '<rect x="4" y="4" width="24" height="18" rx="2"/><rect x="31" y="4" width="13" height="18" rx="2"/><rect x="47" y="4" width="13" height="8" rx="2"/><rect x="47" y="14" width="13" height="8" rx="2"/><rect x="4" y="25" width="15" height="11" rx="2"/><rect x="22" y="25" width="22" height="11" rx="2"/><rect x="47" y="25" width="13" height="11" rx="2"/>' },
  { key: 'masonry', title: 'Плитка', svg: '<rect x="4" y="2" width="12" height="16" rx="2"/><rect x="4" y="21" width="12" height="17" rx="2"/><rect x="19" y="6" width="12" height="12" rx="2"/><rect x="19" y="21" width="12" height="17" rx="2"/><rect x="34" y="2" width="12" height="20" rx="2"/><rect x="34" y="25" width="12" height="13" rx="2"/><rect x="49" y="8" width="11" height="15" rx="2"/><rect x="49" y="26" width="11" height="12" rx="2"/>' },
  { key: 'polaroid', title: 'Полароиды', svg: '<rect x="6" y="8" width="16" height="20" rx="1.5" transform="rotate(-8 14 18)"/><rect x="24" y="10" width="16" height="20" rx="1.5" transform="rotate(5 32 20)"/><rect x="42" y="8" width="16" height="20" rx="1.5" transform="rotate(-4 50 18)"/>' },
  { key: 'hex', title: 'Соты', svg: '<path d="M14 6l7 4v8l-7 4-7-4v-8z"/><path d="M29 6l7 4v8l-7 4-7-4v-8z"/><path d="M44 6l7 4v8l-7 4-7-4v-8z"/><path d="M21.5 19l7 4v8l-7 4-7-4v-8z"/><path d="M36.5 19l7 4v8l-7 4-7-4v-8z"/><path d="M51.5 19l7 4v8l-7 4-7-4v-8z"/>' },
  { key: 'diagonal', title: 'Диагональ', svg: '<path d="M8 4h10l-6 32H2z"/><path d="M22 4h10l-6 32H16z"/><path d="M36 4h10l-6 32H30z"/><path d="M50 4h10l-6 32H44z"/>' },
  { key: 'film', title: 'Плёнка', svg: '<rect x="2" y="7" width="60" height="11" rx="1.5" transform="rotate(-8 32 12)"/><rect x="2" y="22" width="60" height="11" rx="1.5" transform="rotate(-8 32 28)"/>' },
];

class SettingsApp {
  private s!: Settings;
  private saved = '';
  private active = 'wallpaper';
  private pluginEdits: Record<string, Record<string, unknown>> = {};
  private wpFocus = 0;
  private root = document.getElementById('settings')!;

  constructor() {
    window.chrome?.webview?.addEventListener('message', (e) => this.onHost(e.data as HostMsg));
    send({ type: 'settings_get' });
    if (!window.chrome?.webview) this.onHost({ type: 'settings', data: DEMO });
  }

  private onHost(m: HostMsg): void {
    switch (m.type) {
      case 'settings':
        this.s = m.data;
        this.normalize();
        this.initPluginEdits();
        this.saved = this.serialize();
        this.render();
        break;
      case 'settings_saved':
        this.saved = this.serialize();
        this.updateDirty(true);
        break;
      case 'folder_picked':
        this.s.collage.pinsDirectory = m.path;
        this.s.pinsCount = m.count;
        this.q('#pinsPath').textContent = m.path;
        this.q('#pinsCount').textContent = this.countText(m.count);
        this.updateDirty();
        break;
      case 'wallpaper_file_picked':
        this.s.wallpaper.sources.push({
          type: m.mediaType,
          value: m.path,
          durationSeconds: 0,
          previewUrl: m.previewUrl ?? null,
        });
        this.wpFocus = this.s.wallpaper.sources.length - 1;
        this.renderWallpaperStage();
        this.updateDirty();
        break;
      case 'geocode_result': {
        const st = this.q('#geoStatus');
        if (m.ok && m.lat != null && m.lon != null) {
          this.s.weather.latitude = m.lat;
          this.s.weather.longitude = m.lon;
          st.textContent = `Найдено: ${m.name} · ${m.lat.toFixed(2)}, ${m.lon.toFixed(2)}`;
          st.className = 'status ok';
          this.updateDirty();
        } else {
          st.textContent = 'Город не найден';
          st.className = 'status err';
        }
        (this.q('#geoBtn') as HTMLButtonElement).disabled = false;
        break;
      }
      case 'token_status':
        this.s.tokenExists = m.exists;
        this.refreshPluginSecret('timeweb', 'token', m.exists);
        break;
      case 'weeek_token_status':
        this.s.weeekTokenExists = m.exists;
        this.refreshPluginSecret('today', 'weeekToken', m.exists);
        break;
      case 'calendar_ics_status':
        this.s.calendarIcsExists = m.exists;
        break;
      case 'diagnostics_result': {
        const pre = this.q('#diag');
        pre.hidden = false;
        pre.textContent = m.text;
        (this.q('#diagBtn') as HTMLButtonElement).disabled = false;
        this.q('#diagBtn').textContent = 'Проверить подключение';
        break;
      }
      case 'error':
        console.warn(m.message);
        break;
    }
  }

  private normalize(): void {
    if (!this.s.wallpaper) this.s.wallpaper = { enabled: true, youtubeUrl: '', soundEnabled: false, rotationSeconds: 300, shuffle: false, sources: [] };
    if (this.s.wallpaper.enabled == null) this.s.wallpaper.enabled = true;
    if (!this.s.wallpaper.sources) this.s.wallpaper.sources = [];
    if (this.s.collage.enabled == null) this.s.collage.enabled = false;
    if (!this.s.plugins) this.s.plugins = [];
    if (!this.s.server?.sites) this.s.server = { ...this.s.server, sites: [] };
  }

  private initPluginEdits(): void {
    this.pluginEdits = {};
    for (const p of this.s.plugins) {
      const bag: Record<string, unknown> = {};
      for (const f of p.fields) {
        if (f.kind === 'password') bag[f.id] = '';
        else bag[f.id] = f.value ?? (f.kind === 'toggle' ? false : '');
      }
      this.pluginEdits[p.pluginId] = bag;
    }
  }

  private serialize(): string {
    return JSON.stringify({ ...this.s, plugins: this.pluginEdits, server: { sites: this.s.server.sites } });
  }

  private q(sel: string): HTMLElement {
    return this.root.querySelector(sel) as HTMLElement;
  }

  private countText(n: number): string {
    const m10 = n % 10, m100 = n % 100;
    const w = m10 === 1 && m100 !== 11 ? 'картинка' : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? 'картинки' : 'картинок';
    return `${n} ${w}`;
  }

  private toggle(id: string, on: boolean): string {
    return `<label class="toggle"><input type="checkbox" id="${id}" ${on ? 'checked' : ''}/><span></span></label>`;
  }

  private row(title: string, sub: string, control: string): string {
    return `<div class="row"><div class="row-text"><div class="row-title">${title}</div>${sub ? `<div class="row-sub">${sub}</div>` : ''}</div>${control}</div>`;
  }

  private pct(value: number | undefined, fallback: number): number {
    const v = value == null || Number.isNaN(value) ? fallback : value;
    return Math.round(Math.min(1, Math.max(0.05, v)) * 100);
  }

  private navBtn(id: string, label: string, iconHtml: string, active: boolean): string {
    return `<button type="button" class="nav-btn ${active ? 'active' : ''}" data-nav="${id}"><span class="nav-ico">${iconHtml}</span><span class="nav-label">${label}</span></button>`;
  }

  private render(): void {
    const s = this.s;
    const pluginNav = s.plugins
      .map((p) =>
        this.navBtn(
          `plugin-${p.pluginId}`,
          p.displayName,
          icon(Icons.plugin, 18),
          this.active === `plugin-${p.pluginId}`,
        ),
      )
      .join('');

    this.root.innerHTML = `
    <div class="shell">
      <header class="titlebar">
        <div class="titlebar-drag" id="titleDrag">
          <img class="titlebar-mark" src="https://app.livewallpaper/brand-mark.svg" alt="" width="18" height="18"/>
          <span>LiveWallpaper</span>
          <span class="titlebar-chip">настройки</span>
        </div>
        <div class="win-controls">
          <button type="button" class="win-btn" id="winMin" title="Свернуть">&#8211;</button>
          <button type="button" class="win-btn" id="winMax" title="Развернуть">&#9633;</button>
          <button type="button" class="win-btn close" id="winClose" title="Закрыть">&#10005;</button>
        </div>
      </header>
      <div class="app">
        <aside class="side">
          <div class="brand">
            <img src="https://app.livewallpaper/brand-mark.svg" alt="" width="32" height="32"/>
            <div><b>LiveWallpaper</b><small>Пульт рабочего стола</small></div>
          </div>
          <nav class="nav" aria-label="Разделы">
            <div class="nav-group-label">Разделы</div>
            ${CORE_NAV.map((n) => this.navBtn(n.id, n.label, n.iconHtml, this.active === n.id)).join('')}
            ${s.plugins.length ? `<div class="nav-group-label">Плагины</div>${pluginNav}` : ''}
          </nav>
          <div class="side-foot">
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>W</kbd><span>коллаж</span></div>
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>P</kbd><span>пауза</span></div>
            <div class="hotkey"><kbd>Win</kbd><kbd>Alt</kbd><kbd>H</kbd><span>приватность</span></div>
          </div>
        </aside>
        <main class="main">
          <div class="panels">
            ${this.panelWallpaper()}
            ${this.panelCollage()}
            ${this.panelWidgets()}
            ${this.panelAtmosphere()}
            ${this.panelSystem()}
            ${s.plugins.map((p) => this.panelPlugin(p)).join('')}
          </div>
          <footer class="foot">
            <span class="dirty" id="dirty"><i></i><span>Изменений нет</span></span>
            <button class="btn ghost" id="close">Закрыть</button>
            <button class="btn primary" id="save">Сохранить</button>
          </footer>
        </main>
      </div>
    </div>`;

    this.showPanel(this.active);
    this.renderWallpaperStage();
    this.renderSites();
    this.bind();
    this.updateDirty();
  }

  private panel(id: string, title: string, lead: string, body: string, headingIcon?: string): string {
    const ico = headingIcon ? `<span class="panel-ico">${headingIcon}</span>` : '';
    return `<section class="panel ${id === 'wallpaper' ? 'panel-wide' : ''} ${this.active === id ? 'active' : ''}" id="panel-${id}" data-panel="${id}"><header class="panel-head">${ico}<div><h2>${title}</h2><p class="lead">${lead}</p></div></header>${body}</section>`;
  }

  private panelWallpaper(): string {
    const w = this.s.wallpaper;
    const gated = w.enabled !== false ? '' : ' is-disabled';
    return this.panel(
      'wallpaper',
      'Обои',
      'Свой плейлист фото/видео/YouTube. Выключено — фон как у Windows.',
      `
      <div class="card">${this.row('Свои обои', 'Выключено — текущие обои Windows 11', this.toggle('wpEnabled', w.enabled !== false))}</div>
      <div class="wp-body${gated}">
      <div class="wp-stage" id="wpStage"></div>
      <div class="wp-toolbar">
        <div class="wp-add">
          <input class="input grow" id="wpYtInput" placeholder="Ссылка YouTube" spellcheck="false"/>
          <button class="btn" id="wpAddYt" type="button">${namedIcon('plus', 16)} Добавить</button>
        </div>
        <div class="wp-add-files">
          <button class="btn" id="wpAddImage" type="button">${namedIcon('wallpaper', 16)} Фото</button>
          <button class="btn" id="wpAddVideo" type="button">${namedIcon('monitor', 16)} Видео</button>
        </div>
      </div>
      <div class="card wp-cycle">
        <div class="row col">
          <div class="row-text">
            <div class="row-title">Интервал смены</div>
            <div class="row-sub">Если у кадра своё время = 0, берётся это значение</div>
          </div>
          <div class="range">
            <input type="range" id="wpRotation" min="15" max="3600" step="15" value="${w.rotationSeconds}"/>
            <output id="wpRotationOut">${formatDwell(w.rotationSeconds)}</output>
          </div>
        </div>
        ${this.row('Перемешивать', 'Новый порядок после полного цикла', this.toggle('wpShuffle', w.shuffle))}
        ${this.row('Звук', 'YouTube и локальное видео', this.toggle('wpSound', w.soundEnabled))}
      </div>
      </div>`,
      icon(Icons.wallpaper, 22),
    );
  }

  private panelCollage(): string {
    const c = this.s.collage;
    const gated = c.enabled ? '' : ' is-disabled';
    return this.panel('collage', 'Коллаж', 'Пины поверх обоев (своих или Windows).', `
      <div class="card">${this.row('Коллаж на рабочем столе', 'Рисуется поверх обоев, не вместо них', this.toggle('collageEnabled', c.enabled))}</div>
      <div class="card collage-body${gated}">
        <div class="row">
          <div class="row-text"><div class="row-title">Папка с картинками</div><div class="row-sub path" id="pinsPath">${esc(c.pinsDirectory || 'не выбрана')}</div></div>
          <span class="status" id="pinsCount">${this.countText(this.s.pinsCount)}</span>
          <button class="btn" id="pickFolder" type="button">${namedIcon('folderOpen', 16)} Выбрать…</button>
        </div>
      </div>
      <div class="card collage-body${gated}">
        <div class="row col">
          <div class="row-text"><div class="row-title">Раскладка</div></div>
          <div class="layouts ${c.shuffleStyle ? 'disabled' : ''}" id="layouts">
            ${LAYOUTS.map((l) => `<button type="button" class="layout ${l.key === c.style ? 'active' : ''}" data-layout="${l.key}"><svg class="layout-diagram" viewBox="0 0 64 40" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">${l.svg}</svg>${l.title}</button>`).join('')}
          </div>
        </div>
        ${this.row('Случайная раскладка', 'Менять раскладку вместе с картинками', this.toggle('shuffle', c.shuffleStyle))}
      </div>
      <div class="card collage-body${gated}">
        <div class="row col">
          <div class="row-text"><div class="row-title">Смена картинок</div></div>
          <div class="range"><input type="range" id="interval" min="1" max="60" value="${c.intervalMinutes}"/><output id="intervalOut">${c.intervalMinutes} мин</output></div>
        </div>
        ${this.row('Гармония цветов', 'Подбирать картинки близких оттенков', this.toggle('harmony', c.enableColorHarmony))}
      </div>`, icon(Icons.collage, 22));
  }

  private panelWidgets(): string {
    const s = this.s;
    return this.panel('widgets', 'Виджеты', 'Часы, погода и лаунчер на рабочем столе.', `
      <div class="card">
        ${this.row('Часы', 'Только время и дата', this.toggle('clock', s.widgets.showClock))}
        <div class="row col">
          <div class="row-text"><div class="row-title">Расположение часов</div></div>
          <div class="inline" style="gap:8px; margin-top:8px">
            <div class="seg" id="clockPosV">${([['top', 'Сверху'], ['bottom', 'Снизу']] as const).map(([v, t]) => `<button type="button" data-cposv="${v}" class="${s.widgets.clockPosition?.startsWith(v) ? 'active' : ''}">${t}</button>`).join('')}</div>
            <div class="seg" id="clockPosH">${([['left', 'Слева'], ['center', 'По центру'], ['right', 'Справа']] as const).map(([v, t]) => `<button type="button" data-cposh="${v}" class="${s.widgets.clockPosition?.endsWith(v) ? 'active' : ''}">${t}</button>`).join('')}</div>
          </div>
        </div>
        <div class="row">
          <div class="row-text"><div class="row-title">Шрифт часов</div></div>
          <select class="input" id="clockFont" style="width:200px">
            ${([['default', 'По умолчанию (Segoe)'], ['inter', 'Inter'], ['manrope', 'Manrope'], ['mono', 'JetBrains Mono'], ['serif', 'Playfair Display']] as const).map(([v, t]) => `<option value="${v}" ${s.widgets.clockFont === v ? 'selected' : ''}>${t}</option>`).join('')}
          </select>
        </div>
        ${this.row('Погода', '', this.toggle('weather', s.weather.enabled))}
        <div class="row">
          <div class="row-text"><div class="row-title">Город</div><div class="status" id="geoStatus">${s.weather.latitude || s.weather.longitude ? `${s.weather.latitude.toFixed(2)}, ${s.weather.longitude.toFixed(2)}` : ''}</div></div>
          <input class="input" id="city" value="${esc(s.weather.city)}" style="width:200px" placeholder="Например, Тбилиси"/>
          <button class="btn" id="geoBtn" type="button">Найти</button>
        </div>
      </div>
      <div class="card">
        ${this.row('Папки-лаунчер', 'Папки и файлы с рабочего стола Windows', this.toggle('launcher', s.launcher.enabled))}
        ${this.row('Скрывать значки Windows', 'Прячет иконки Explorer; лаунчер показывает свои плитки', this.toggle('hideIcons', s.launcher.hideDesktopIcons))}
      </div>`, icon(Icons.widgets, 22));
  }

  private panelAtmosphere(): string {
    const s = this.s;
    return this.panel('atmosphere', 'Атмосфера', 'Режимы, сигналы и единая палитра сетапа.', `
      <div class="card">
        <div class="row col">
          <div class="row-text"><div class="row-title">Режим работы</div><div class="row-sub">Авто по приложениям и времени или фиксированный.</div></div>
          <div class="seg" id="modeSeg" style="margin-top:6px">
            <button type="button" data-mode="auto" class="${s.context.mode === 'auto' ? 'active' : ''}">Авто</button>
            <button type="button" data-mode="work" class="${s.context.mode === 'work' ? 'active' : ''}">Рабочий</button>
            <button type="button" data-mode="game" class="${s.context.mode === 'game' ? 'active' : ''}">Игровой</button>
            <button type="button" data-mode="night" class="${s.context.mode === 'night' ? 'active' : ''}">Ночной</button>
          </div>
        </div>
      </div>
      <div class="card">
        ${this.row('Красные края при сбое', 'Пульс по периметру при падении сайта или сервера', this.toggle('atmProd', s.atmosphere.enableProdAlert))}
        ${this.row('Тёплый фон перед созвоном', 'За 15 мин до встречи', this.toggle('atmCall', s.atmosphere.enableUpcomingCall))}
        ${this.row('Янтарная точка при низком балансе', 'Timeweb Cloud', this.toggle('atmBal', s.atmosphere.enableLowBalanceBeacon))}
      </div>
      <div class="card">
        <div class="row col">
          <div class="inline" style="gap:8px; flex-wrap:wrap">
            <button class="btn" id="testProdBtn" type="button">Красные края</button>
            <button class="btn" id="testCallBtn" type="button">Тёплый фон</button>
            <button class="btn" id="testBalBtn" type="button">Янтарная точка</button>
          </div>
        </div>
      </div>
      <div class="card">
        ${this.row('Акцент Windows', 'Окна и панель задач', this.toggle('palWin', s.setupPalette?.syncWindowsAccent ?? true))}
        ${this.row('Тема Windows Terminal', '', this.toggle('palTerm', s.setupPalette?.syncWindowsTerminal ?? true))}
        ${this.row('Redragon (HID)', '', this.toggle('palRedragon', s.setupPalette?.syncRedragonHid ?? true))}
        <div class="row col">
          <div class="row-text"><div class="row-title">Яркость клавиатуры</div></div>
          <div class="range"><input type="range" id="kbBright" min="5" max="100" value="${this.pct(s.setupPalette?.keyboardIntensity, 0.85)}"/><output id="kbBrightOut">${this.pct(s.setupPalette?.keyboardIntensity, 0.85)}%</output></div>
        </div>
        <div class="row col">
          <div class="row-text"><div class="row-title">Яркость ночью</div></div>
          <div class="range"><input type="range" id="kbNight" min="5" max="100" value="${this.pct(s.setupPalette?.nightKeyboardIntensity, 0.4)}"/><output id="kbNightOut">${this.pct(s.setupPalette?.nightKeyboardIntensity, 0.4)}%</output></div>
        </div>
        ${this.row('OpenRGB', '', this.toggle('palRgb', s.setupPalette?.syncOpenRgb ?? false))}
        ${this.row('Razer Chroma', '', this.toggle('palRazer', s.setupPalette?.syncRazerChroma ?? false))}
        ${this.row('Windows — не чаще', '', `<div class="seg" id="palInterval">${[10, 20, 60].map((v) => `<button type="button" data-palint="${v}" class="${(s.setupPalette?.desktopMinIntervalMinutes ?? 20) === v ? 'active' : ''}">${v} мин</button>`).join('')}</div>`)}
        <div class="row">
          <div class="row-text"><div class="row-title">Текущий акцент</div><div class="row-sub" id="accentHex">${esc(s.setupPalette?.currentAccentHex || '#6366F1')}</div></div>
          <div class="palette-swatch" id="accentSwatch" style="background:${esc(s.setupPalette?.currentAccentHex || '#6366F1')}"></div>
          <button class="btn" id="applyPaletteBtn" type="button">Применить сейчас</button>
        </div>
      </div>`, icon(Icons.atmosphere, 22));
  }

  private panelSystem(): string {
    const s = this.s;
    return this.panel('system', 'Система', 'Производительность, панель и запуск.', `
      <div class="card">
        ${this.row('Пауза в играх', 'При полноэкранных приложениях', this.toggle('pauseFs', s.performance.autoPauseFullscreen))}
        ${this.row('Пауза от батареи', '', this.toggle('pauseBat', s.performance.autoPauseBattery))}
        ${this.row('FPS в простое', '', `<div class="seg" id="fps">${[30, 45, 60].map((v) => `<button type="button" data-fps="${v}" class="${s.performance.fpsIdle === v ? 'active' : ''}">${v}</button>`).join('')}</div>`)}
        ${this.row('Своя панель задач (Бета)', 'Экспериментальная замена панели Windows', this.toggle('taskbar', s.taskbar?.enabled ?? false))}
        ${this.row('Автозапуск', 'Вместе с Windows', this.toggle('autostart', s.autostart))}
        <div class="row">
          <div class="row-text"><div class="row-title">Обновление</div><div class="row-sub">Пересобрать обои и перезапустить</div></div>
          <button class="btn" id="updateBtn" type="button">Обновить</button>
        </div>
      </div>`, icon(Icons.system, 22));
  }

  private panelPlugin(p: PluginManifest): string {
    const pid = `plugin-${p.pluginId}`;
    const fields = p.fields.map((f) => this.renderPluginField(p.pluginId, f)).join('');
    const extra =
      p.pluginId === 'server'
        ? `<div class="card"><div class="row col"><div class="row-text"><div class="row-title">Сайты</div><div class="row-sub">Проверка доступности и SSL</div></div><div class="sites" id="sites"></div><button class="btn" id="addSite" type="button">${namedIcon('plus', 16)} Добавить сайт</button></div>
           <div class="row col"><button class="btn" id="diagBtn" type="button">Проверить подключение</button><pre class="diag" id="diag" hidden></pre></div></div>`
        : '';
    const panelIcon =
      p.pluginId === 'system' ? Icons.system :
      p.pluginId === 'server' ? Icons.gauge :
      p.pluginId === 'telegram' ? Icons.plugin :
      Icons.plugin;
    return this.panel(pid, p.displayName, p.description || '', `<div class="card">${fields}</div>${extra}`, icon(panelIcon, 22));
  }

  private renderPluginField(pluginId: string, f: PluginField): string {
    const val = this.pluginEdits[pluginId]?.[f.id];
    if (f.kind === 'toggle') {
      return this.row(f.label, f.hint ?? '', `<label class="toggle"><input type="checkbox" data-pf="${pluginId}" data-fid="${f.id}" ${val ? 'checked' : ''}/><span></span></label>`);
    }
    if (f.kind === 'password') {
      const st = f.secretExists ? '<span class="status ok">Сохранён</span>' : '<span class="status">Не задан</span>';
      return `<div class="row col"><div class="row-text"><div class="row-title">${esc(f.label)}</div>${f.hint ? `<div class="row-sub">${esc(f.hint)}</div>` : ''}${st}</div>
        <div class="inline"><input class="input" type="password" data-pf="${pluginId}" data-fid="${f.id}" placeholder="новое значение" autocomplete="off" style="width:220px"/>
        <button type="button" class="btn ghost danger" data-pf-del="${pluginId}" data-fid="${f.id}">Удалить</button></div></div>`;
    }
    const inputType = f.kind === 'number' ? 'number' : 'text';
    return `<div class="row"><div class="row-text"><div class="row-title">${esc(f.label)}</div>${f.hint ? `<div class="row-sub">${esc(f.hint)}</div>` : ''}</div>
      <input class="input" type="${inputType}" data-pf="${pluginId}" data-fid="${f.id}" value="${esc(String(val ?? ''))}" style="width:260px"/></div>`;
  }

  private renderWallpaperStage(): void {
    const stage = this.q('#wpStage');
    if (!stage) return;
    const list = this.s.wallpaper.sources;
    if (list.length === 0) {
      this.wpFocus = 0;
      stage.innerHTML = `
        <div class="wp-preview is-empty">
          <div class="wp-preview-frame">
            <div class="wp-preview-empty">
              ${namedIcon('monitor', 36, 'wp-preview-empty-ico')}
              <span class="wp-preview-empty-title">Нет кадра</span>
              <span class="wp-preview-empty-sub">Добавьте фото, видео или ссылку YouTube — оно станет фоном стола</span>
            </div>
            <div class="wp-timer" aria-hidden="true"><i style="width:0%"></i></div>
          </div>
        </div>
        <div class="wp-rail empty-rail">Плейлист пуст</div>`;
      return;
    }

    if (this.wpFocus >= list.length) this.wpFocus = list.length - 1;
    if (this.wpFocus < 0) this.wpFocus = 0;
    const focus = list[this.wpFocus]!;
    const dwell =
      focus.durationSeconds > 0 ? focus.durationSeconds : this.s.wallpaper.rotationSeconds;
    const preview = sourcePreview(focus);
    const typeLabel = ({ youtube: 'YouTube', video: 'Видео', image: 'Фото' } as Record<string, string>)[
      focus.type
    ] ?? focus.type;

    stage.innerHTML = `
      <div class="wp-preview">
        <div class="wp-preview-frame">
          ${
            preview
              ? `<img class="wp-preview-img" src="${esc(preview)}" alt="" decoding="async"/>`
              : `<div class="wp-preview-fallback" data-kind="${esc(focus.type)}"><span>${esc(typeLabel)}</span></div>`
          }
          <div class="wp-preview-meta">
            <span class="wp-preview-kind">${esc(typeLabel)}</span>
            <span class="wp-preview-name" title="${esc(focus.value)}">${esc(sourceTitle(focus))}</span>
            <span class="wp-preview-dwell">${formatDwell(dwell)}</span>
          </div>
          <div class="wp-timer" title="Интервал этого кадра"><i style="width:${Math.min(100, (dwell / Math.max(this.s.wallpaper.rotationSeconds, 1)) * 100)}%"></i></div>
        </div>
        <div class="wp-preview-actions">
          <label class="wp-dwell-field">
            <span>Своё время, с</span>
            <input class="input wp-dur" type="number" min="0" max="86400" data-wp-dur="${this.wpFocus}" value="${focus.durationSeconds || 0}"/>
          </label>
          <button type="button" class="btn ghost" data-wp-up="${this.wpFocus}" ${this.wpFocus <= 0 ? 'disabled' : ''}>${namedIcon('chevronUp', 16)} Выше</button>
          <button type="button" class="btn ghost" data-wp-down="${this.wpFocus}" ${this.wpFocus >= list.length - 1 ? 'disabled' : ''}>${namedIcon('chevronDown', 16)} Ниже</button>
          <button type="button" class="btn ghost danger" data-wp-del="${this.wpFocus}">${namedIcon('trash', 16)} Убрать</button>
        </div>
      </div>
      <div class="wp-rail" id="wpRail" role="list">
        ${list
          .map((item, i) => {
            const thumb = sourcePreview(item);
            const kind = ({ youtube: 'YouTube', video: 'Видео', image: 'Фото' } as Record<string, string>)[
              item.type
            ] ?? item.type;
            return `<button type="button" class="wp-thumb ${i === this.wpFocus ? 'is-active' : ''}" data-wp-focus="${i}" role="listitem" title="${esc(sourceTitle(item))}">
              ${
                thumb
                  ? `<img src="${esc(thumb)}" alt="" decoding="async" loading="lazy"/>`
                  : `<span class="wp-thumb-fallback">${esc(kind.slice(0, 1))}</span>`
              }
              <span class="wp-thumb-cap">${esc(kind)}</span>
            </button>`;
          })
          .join('')}
      </div>`;
  }

  private renderSites(): void {
    const box = this.q('#sites');
    if (!box) return;
    box.innerHTML = this.s.server.sites.length
      ? this.s.server.sites
          .map(
            (x, i) => `<div class="site">
          <input class="input" data-site-name="${i}" value="${esc(x.name)}" placeholder="Название"/>
          <input class="input" data-site-url="${i}" value="${esc(x.url)}" placeholder="https://"/>
          <button class="btn ghost danger" data-site-del="${i}" type="button">✕</button></div>`,
          )
          .join('')
      : '<div class="empty">Сайтов нет — добавь хотя бы прод.</div>';
  }

  private showPanel(id: string): void {
    this.active = id;
    this.root.querySelectorAll<HTMLElement>('.panel').forEach((p) => p.classList.toggle('active', p.dataset.panel === id));
    this.root.querySelectorAll<HTMLElement>('[data-nav]').forEach((b) => b.classList.toggle('active', b.dataset.nav === id));
  }

  private refreshPluginSecret(pluginId: string, fieldId: string, exists: boolean): void {
    const p = this.s.plugins.find((x) => x.pluginId === pluginId);
    const f = p?.fields.find((x) => x.id === fieldId);
    if (f) f.secretExists = exists;
    if (this.active === `plugin-${pluginId}`) this.render();
  }

  private bind(): void {
    const s = this.s;
    const on = (id: string, fn: (v: boolean) => void) => this.q(`#${id}`)?.addEventListener('change', (e) => { fn((e.target as HTMLInputElement).checked); this.updateDirty(); });

    this.q('#titleDrag')?.addEventListener('mousedown', (e) => {
      if ((e.target as HTMLElement).closest('.win-controls')) return;
      send({ type: 'settings_drag' });
    });
    this.q('#winMin')?.addEventListener('click', () => send({ type: 'settings_minimize' }));
    this.q('#winMax')?.addEventListener('click', () => send({ type: 'settings_maximize' }));
    this.q('#winClose')?.addEventListener('click', () => send({ type: 'settings_close' }));
    this.q('#close')?.addEventListener('click', () => send({ type: 'settings_close' }));

    this.root.querySelectorAll<HTMLElement>('[data-nav]').forEach((b) =>
      b.addEventListener('click', () => {
        this.showPanel(b.dataset.nav!);
        if (b.dataset.nav === 'plugin-server') {
          this.renderSites();
          this.bindSites();
        }
      }),
    );

    on('collageEnabled', (v) => {
      s.collage.enabled = v;
      this.root.querySelectorAll('.collage-body').forEach((el) => el.classList.toggle('is-disabled', !v));
      this.updateDirty();
    });

    this.q('#pickFolder')?.addEventListener('click', () => send({ type: 'pick_folder' }));
    this.root.querySelectorAll<HTMLElement>('[data-layout]').forEach((b) =>
      b.addEventListener('click', () => {
        s.collage.style = b.dataset.layout!;
        s.collage.shuffleStyle = false;
        (this.q('#shuffle') as HTMLInputElement).checked = false;
        this.q('#layouts')?.classList.remove('disabled');
        this.root.querySelectorAll('[data-layout]').forEach((x) => x.classList.toggle('active', x === b));
        this.updateDirty();
      }));
    on('shuffle', (v) => { s.collage.shuffleStyle = v; this.q('#layouts')?.classList.toggle('disabled', v); });
    const interval = this.q('#interval') as HTMLInputElement;
    interval?.addEventListener('input', () => {
      s.collage.intervalMinutes = Number(interval.value);
      this.q('#intervalOut').textContent = `${interval.value} мин`;
      this.updateDirty();
    });
    on('harmony', (v) => (s.collage.enableColorHarmony = v));

    this.q('#wpAddYt')?.addEventListener('click', () => {
      const inp = this.q('#wpYtInput') as HTMLInputElement;
      const url = inp.value.trim();
      if (!url) return;
      s.wallpaper.sources.push({ type: 'youtube', value: url, durationSeconds: 0 });
      inp.value = '';
      this.wpFocus = s.wallpaper.sources.length - 1;
      this.renderWallpaperStage();
      this.bindWallpaperList();
      this.updateDirty();
    });
    this.q('#wpYtInput')?.addEventListener('keydown', (e) => {
      if ((e as KeyboardEvent).key === 'Enter') this.q('#wpAddYt')?.click();
    });
    this.q('#wpAddVideo')?.addEventListener('click', () => send({ type: 'pick_wallpaper_video' }));
    this.q('#wpAddImage')?.addEventListener('click', () => send({ type: 'pick_wallpaper_image' }));
    on('wpEnabled', (v) => {
      s.wallpaper.enabled = v;
      this.render();
      this.bind();
      this.updateDirty();
    });
    on('wpShuffle', (v) => (s.wallpaper.shuffle = v));
    on('wpSound', (v) => (s.wallpaper.soundEnabled = v));
    const rot = this.q('#wpRotation') as HTMLInputElement;
    rot?.addEventListener('input', () => {
      s.wallpaper.rotationSeconds = Number(rot.value);
      this.q('#wpRotationOut').textContent = formatDwell(Number(rot.value));
      this.renderWallpaperStage();
      this.bindWallpaperList();
      this.updateDirty();
    });
    this.bindWallpaperList();

    on('clock', (v) => (s.widgets.showClock = v));
    s.widgets.clockPosition ||= 'top-center';
    const setClockPos = (v: string, h: string) => { s.widgets.clockPosition = `${v}-${h}`; this.updateDirty(); };
    this.root.querySelectorAll<HTMLElement>('[data-cposv]').forEach((b) =>
      b.addEventListener('click', () => {
        const h = s.widgets.clockPosition.split('-')[1] || 'center';
        this.root.querySelectorAll('[data-cposv]').forEach((x) => x.classList.toggle('active', x === b));
        setClockPos(b.dataset.cposv!, h);
      }));
    this.root.querySelectorAll<HTMLElement>('[data-cposh]').forEach((b) =>
      b.addEventListener('click', () => {
        const v = s.widgets.clockPosition.split('-')[0] || 'top';
        this.root.querySelectorAll('[data-cposh]').forEach((x) => x.classList.toggle('active', x === b));
        setClockPos(v, b.dataset.cposh!);
      }));
    this.q('#clockFont')?.addEventListener('change', (e) => { s.widgets.clockFont = (e.target as HTMLSelectElement).value; this.updateDirty(); });

    on('weather', (v) => (s.weather.enabled = v));
    this.q('#city')?.addEventListener('input', (e) => { s.weather.city = (e.target as HTMLInputElement).value; this.updateDirty(); });
    this.q('#geoBtn')?.addEventListener('click', () => {
      (this.q('#geoBtn') as HTMLButtonElement).disabled = true;
      this.q('#geoStatus').textContent = 'Поиск…';
      send({ type: 'geocode', city: s.weather.city });
    });
    on('launcher', (v) => (s.launcher.enabled = v));
    on('hideIcons', (v) => (s.launcher.hideDesktopIcons = v));

    this.root.querySelectorAll<HTMLElement>('[data-mode]').forEach((b) =>
      b.addEventListener('click', () => {
        s.context.mode = b.dataset.mode!;
        this.root.querySelectorAll('[data-mode]').forEach((x) => x.classList.toggle('active', x === b));
        this.updateDirty();
        send({ type: 'set_mode', mode: s.context.mode });
      }));

    on('atmProd', (v) => (s.atmosphere.enableProdAlert = v));
    on('atmCall', (v) => (s.atmosphere.enableUpcomingCall = v));
    on('atmBal', (v) => (s.atmosphere.enableLowBalanceBeacon = v));

    let testProd = false;
    this.q('#testProdBtn')?.addEventListener('click', () => {
      testProd = !testProd;
      this.q('#testProdBtn').classList.toggle('test-active-prod', testProd);
      send({ type: 'test_signal', signal: 'prod', active: testProd });
    });
    let testCall = false;
    this.q('#testCallBtn')?.addEventListener('click', () => {
      testCall = !testCall;
      this.q('#testCallBtn').classList.toggle('test-active-call', testCall);
      send({ type: 'test_signal', signal: 'call', active: testCall });
    });
    let testBal = false;
    this.q('#testBalBtn')?.addEventListener('click', () => {
      testBal = !testBal;
      this.q('#testBalBtn').classList.toggle('test-active-bal', testBal);
      send({ type: 'test_signal', signal: 'balance', active: testBal });
    });

    if (!s.setupPalette) s.setupPalette = { enabled: true, syncWindowsAccent: true, syncWindowsTerminal: true, syncRedragonHid: true, syncOpenRgb: false, syncRazerChroma: false, currentAccentHex: '#6366F1' };
    on('palWin', (v) => (s.setupPalette.syncWindowsAccent = v));
    on('palTerm', (v) => (s.setupPalette.syncWindowsTerminal = v));
    on('palRedragon', (v) => (s.setupPalette.syncRedragonHid = v));
    on('palRgb', (v) => (s.setupPalette.syncOpenRgb = v));
    on('palRazer', (v) => (s.setupPalette.syncRazerChroma = v));
    this.root.querySelectorAll<HTMLElement>('[data-palint]').forEach((b) =>
      b.addEventListener('click', () => {
        s.setupPalette.desktopMinIntervalMinutes = Number(b.dataset.palint);
        this.root.querySelectorAll('[data-palint]').forEach((x) => x.classList.toggle('active', x === b));
        this.updateDirty();
      }));
    const bindBright = (id: string, key: 'keyboardIntensity' | 'nightKeyboardIntensity') => {
      const input = this.q('#' + id) as HTMLInputElement;
      input?.addEventListener('input', () => {
        s.setupPalette[key] = Number(input.value) / 100;
        this.q('#' + id + 'Out').textContent = `${input.value}%`;
        this.updateDirty();
      });
    };
    bindBright('kbBright', 'keyboardIntensity');
    bindBright('kbNight', 'nightKeyboardIntensity');
    this.q('#applyPaletteBtn')?.addEventListener('click', () => send({ type: 'apply_palette' }));

    on('pauseFs', (v) => (s.performance.autoPauseFullscreen = v));
    on('pauseBat', (v) => (s.performance.autoPauseBattery = v));
    this.root.querySelectorAll<HTMLElement>('[data-fps]').forEach((b) =>
      b.addEventListener('click', () => {
        s.performance.fpsIdle = Number(b.dataset.fps);
        this.root.querySelectorAll('[data-fps]').forEach((x) => x.classList.toggle('active', x === b));
        this.updateDirty();
      }));
    on('taskbar', (v) => (s.taskbar.enabled = v));
    this.q('#updateBtn')?.addEventListener('click', () => { send({ type: 'update_app' }); });
    on('autostart', (v) => (s.autostart = v));

    this.root.querySelectorAll<HTMLElement>('[data-pf]').forEach((el) => {
      const pluginId = el.dataset.pf!;
      const fid = el.dataset.fid!;
      el.addEventListener('input', () => {
        const t = el as HTMLInputElement;
        this.pluginEdits[pluginId]![fid] = t.type === 'checkbox' ? t.checked : t.type === 'number' ? Number(t.value) : t.value;
        this.updateDirty();
      });
      el.addEventListener('change', () => {
        const t = el as HTMLInputElement;
        if (t.type === 'checkbox') this.pluginEdits[pluginId]![fid] = t.checked;
        this.updateDirty();
      });
    });
    this.root.querySelectorAll<HTMLElement>('[data-pf-del]').forEach((b) =>
      b.addEventListener('click', () => {
        const pid = b.dataset.pfDel!;
        const fid = b.dataset.fid!;
        if (pid === 'timeweb' && fid === 'token') send({ type: 'token_delete' });
        else if (pid === 'today' && fid === 'weeekToken') send({ type: 'weeek_token_delete' });
        else if (pid === 'today' && fid === 'googleCalendarIcsUrl') send({ type: 'calendar_ics_delete' });
      }),
    );

    this.bindSites();
    this.q('#diagBtn')?.addEventListener('click', () => {
      (this.q('#diagBtn') as HTMLButtonElement).disabled = true;
      this.q('#diagBtn').textContent = 'Проверяю…';
      send({ type: 'diagnostics' });
    });

    this.q('#save')?.addEventListener('click', () => {
      const data = {
        collage: s.collage,
        weather: s.weather,
        performance: s.performance,
        launcher: s.launcher,
        server: { sites: s.server.sites.filter((x) => x.name.trim() && /^https?:\/\/.+/.test(x.url.trim())) },
        widgets: s.widgets,
        context: s.context,
        atmosphere: s.atmosphere,
        setupPalette: s.setupPalette,
        taskbar: s.taskbar,
        wallpaper: s.wallpaper,
        autostart: s.autostart,
        plugins: this.pluginEdits,
      };
      send({ type: 'settings_save', data });
    });
  }

  private bindWallpaperList(): void {
    const stage = this.q('#wpStage');
    if (!stage) return;

    stage.querySelectorAll<HTMLElement>('[data-wp-focus]').forEach((b) =>
      b.addEventListener('click', () => {
        this.wpFocus = +b.dataset.wpFocus!;
        this.renderWallpaperStage();
        this.bindWallpaperList();
      }),
    );

    stage.querySelectorAll<HTMLInputElement>('[data-wp-dur]').forEach((inp) =>
      inp.addEventListener('change', () => {
        const i = +inp.dataset.wpDur!;
        this.s.wallpaper.sources[i]!.durationSeconds = Math.max(0, Number(inp.value) || 0);
        this.renderWallpaperStage();
        this.bindWallpaperList();
        this.updateDirty();
      }),
    );

    stage.querySelectorAll<HTMLElement>('[data-wp-del]').forEach((b) =>
      b.addEventListener('click', () => {
        const i = +b.dataset.wpDel!;
        this.s.wallpaper.sources.splice(i, 1);
        if (this.wpFocus >= this.s.wallpaper.sources.length) {
          this.wpFocus = Math.max(0, this.s.wallpaper.sources.length - 1);
        }
        this.renderWallpaperStage();
        this.bindWallpaperList();
        this.updateDirty();
      }),
    );

    stage.querySelectorAll<HTMLElement>('[data-wp-up]').forEach((b) =>
      b.addEventListener('click', () => {
        const i = +b.dataset.wpUp!;
        if (i <= 0) return;
        const arr = this.s.wallpaper.sources;
        [arr[i - 1], arr[i]] = [arr[i]!, arr[i - 1]!];
        this.wpFocus = i - 1;
        this.renderWallpaperStage();
        this.bindWallpaperList();
        this.updateDirty();
      }),
    );

    stage.querySelectorAll<HTMLElement>('[data-wp-down]').forEach((b) =>
      b.addEventListener('click', () => {
        const i = +b.dataset.wpDown!;
        const arr = this.s.wallpaper.sources;
        if (i >= arr.length - 1) return;
        [arr[i], arr[i + 1]] = [arr[i + 1]!, arr[i]!];
        this.wpFocus = i + 1;
        this.renderWallpaperStage();
        this.bindWallpaperList();
        this.updateDirty();
      }),
    );
  }

  private bindSites(): void {
    const sites = this.q('#sites');
    if (!sites) return;
    sites.addEventListener('input', (e) => {
      const t = e.target as HTMLInputElement;
      if (t.dataset.siteName) this.s.server.sites[+t.dataset.siteName].name = t.value;
      if (t.dataset.siteUrl) this.s.server.sites[+t.dataset.siteUrl].url = t.value;
      this.updateDirty();
    });
    sites.addEventListener('click', (e) => {
      const b = (e.target as Element).closest<HTMLElement>('[data-site-del]');
      if (!b) return;
      this.s.server.sites.splice(+b.dataset.siteDel!, 1);
      this.renderSites();
      this.updateDirty();
    });
    this.q('#addSite')?.addEventListener('click', () => {
      this.s.server.sites.push({ name: '', url: 'https://' });
      this.renderSites();
      this.updateDirty();
    });
  }

  private updateDirty(justSaved = false): void {
    const d = this.q('#dirty');
    if (!d) return;
    const dirty = this.serialize() !== this.saved;
    d.className = `dirty ${dirty ? 'on' : justSaved ? 'saved' : ''}`;
    (d.querySelector('span') as HTMLElement).textContent = dirty ? 'Есть несохранённые изменения' : justSaved ? 'Сохранено' : 'Изменений нет';
    (this.q('#save') as HTMLButtonElement).disabled = !dirty;
  }
}

const DEMO: Settings = {
  collage: { enabled: true, pinsDirectory: 'C:\\Users\\Username\\Pictures\\Pins', style: 'fan', shuffleStyle: false, intervalMinutes: 5, enableColorHarmony: true },
  pinsCount: 0,
  weather: { enabled: true, city: 'Москва', latitude: 55.7558, longitude: 37.6173 },
  performance: { autoPauseFullscreen: true, autoPauseBattery: true, fpsIdle: 30 },
  launcher: { enabled: true, hideDesktopIcons: true },
  server: { enabled: true, sshAlias: 'myserver', timewebEnabled: true, sites: [{ name: 'api.example.com', url: 'https://api.example.com' }] },
  widgets: { showClock: true, clockPosition: 'top-center', clockFont: 'default' },
  context: { mode: 'auto' },
  atmosphere: { enabled: true, enableProdAlert: true, enableUpcomingCall: true, enableLowBalanceBeacon: true },
  setupPalette: { enabled: true, syncWindowsAccent: true, syncWindowsTerminal: true, syncRedragonHid: true, syncOpenRgb: false, syncRazerChroma: false, currentAccentHex: '#6366F1', keyboardIntensity: 0.85, nightKeyboardIntensity: 0.4 },
  taskbar: { enabled: false },
  wallpaper: { enabled: true, youtubeUrl: '', soundEnabled: false, rotationSeconds: 300, shuffle: false, sources: [] },
  plugins: [
    { pluginId: 'telegram', displayName: 'Telegram', description: '', fields: [{ id: 'enabled', label: 'Включить', kind: 'toggle', value: true }] },
  ],
  autostart: true,
  tokenExists: true,
  weeekTokenExists: false,
  calendarIcsExists: false,
};

new SettingsApp();
