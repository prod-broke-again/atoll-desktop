import type { IWidget } from './contract';
import type { ServerStateMessage, ServerMetricsMessage, PrivacyModeMessage, MetricPoint, ServerSiteInfo } from '../types/ipc';
import { DraggableWidgetManager } from './draggable';
import { tr } from '../i18n';

const esc = (s: string) =>
  (s || '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

const CH_W = 280;
const CH_H = 64;
const WINDOW_SEC = 600; // 10 min of history on the chart

type Ssh = ServerStateMessage['ssh'];

function sshLabel(ssh: Ssh): string {
  switch (ssh) {
    case 'live': return tr('в сети', 'online');
    case 'connecting': return tr('подключение', 'connecting');
    case 'stale': return tr('нет данных', 'no data');
    case 'offline': return tr('нет связи', 'offline');
    default: return String(ssh);
  }
}

function fmtNet(kbps: number): string {
  if (!Number.isFinite(kbps)) return '—';
  if (kbps >= 1024) return `${(kbps / 1024).toFixed(kbps >= 10240 ? 0 : 1)} ${tr('МБ/с', 'MB/s')}`;
  return `${Math.round(kbps)} ${tr('КБ/с', 'KB/s')}`;
}

function fmtUptime(sec: number): string {
  if (!sec) return '';
  const d = Math.floor(sec / 86400);
  if (d >= 1) return `${d} ${tr('д', 'd')}`;
  const h = Math.floor(sec / 3600);
  return h >= 1 ? `${h} ${tr('ч', 'h')}` : `${Math.max(1, Math.floor(sec / 60))} ${tr('мин', 'min')}`;
}

function fmtAge(hours: number | null | undefined): string {
  if (hours == null) return '—';
  if (hours < 1) return `< 1 ${tr('ч', 'h')}`;
  if (hours < 48) return `${Math.round(hours)} ${tr('ч', 'h')}`;
  return `${Math.round(hours / 24)} ${tr('д', 'd')}`;
}

export class ServerWidget implements IWidget {
  readonly id = 'server';
  private el: HTMLElement | null = null;
  private history: MetricPoint[] = [];
  private state: ServerStateMessage | null = null;
  private privacy = false;
  private enabled = true;
  private r: Record<string, HTMLElement> = {};
  private paths: { cpuLine: SVGPathElement; cpuArea: SVGPathElement; memLine: SVGPathElement; miniLine: SVGPathElement; miniArea: SVGPathElement } | null = null;

  public isHidden(): boolean {
    return this.privacy || !this.enabled;
  }

  /** Plugin settings gate — hides the card when Server.Enabled is false. */
  setEnabled(on: boolean): void {
    this.enabled = on;
    this.applyVisibility();
    if (on && !this.privacy) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));
  }

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget srv is-plugin-hidden';
    if (this.loadCompact()) el.classList.add('is-compact');
    el.dataset.ssh = 'connecting';
    el.innerHTML = `
      <div class="srv-head">
        <div class="srv-host">
          <i class="srv-dot"></i>
          <span class="srv-host-name" data-r="host">${tr('сервер', 'server')}</span>
        </div>
        <span class="srv-conn" data-r="conn">${tr('подключение', 'connecting')}</span>
        <button class="srv-icon-btn srv-only-full" data-action="srv-ssh" title="${tr('Открыть SSH в терминале', 'Open SSH in terminal')}">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M4 6l6 6-6 6M12 18h8"/></svg>
        </button>
        <span class="srv-chevron" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M6 9l6 6 6-6"/></svg>
        </span>
      </div>

      <div class="srv-mini">
        <div class="srv-mini-cpu"><b data-r="miniCpu">—</b><span>CPU</span></div>
        <svg class="srv-mini-svg" viewBox="0 0 100 28" preserveAspectRatio="none">
          <path class="srv-mini-area" data-p="miniArea"/>
          <path class="srv-mini-line" data-p="miniLine"/>
        </svg>
        <div class="srv-mini-meta">
          <span>RAM <b data-r="miniMem">—</b></span>
          <span data-r="miniBal">—</span>
        </div>
      </div>

      <div class="srv-alert" data-r="alert" hidden></div>

      <div class="srv-chart">
        <div class="srv-hero">
          <span class="srv-hero-val" data-r="cpu">—</span>
          <span class="srv-hero-label">CPU</span>
        </div>
        <div class="srv-legend">
          <span class="srv-leg srv-leg-cpu">CPU</span>
          <span class="srv-leg srv-leg-mem">RAM <b data-r="mem">—</b></span>
        </div>
        <svg viewBox="0 0 ${CH_W} ${CH_H}" preserveAspectRatio="none" class="srv-svg">
          <defs>
            <linearGradient id="srvCpuFill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0" stop-color="rgb(var(--accent-rgb))" stop-opacity=".38"/>
              <stop offset="1" stop-color="rgb(var(--accent-rgb))" stop-opacity="0"/>
            </linearGradient>
          </defs>
          <line x1="0" x2="${CH_W}" y1="${CH_H * 0.5}" y2="${CH_H * 0.5}" class="srv-grid"/>
          <path class="srv-area" data-p="cpuArea"/>
          <path class="srv-line-mem" data-p="memLine"/>
          <path class="srv-line-cpu" data-p="cpuLine"/>
        </svg>
        <div class="srv-nodata" data-r="nodata">${tr('ожидание данных…', 'waiting for data…')}</div>
      </div>

      <div class="srv-stats">
        <div class="srv-stat"><span>${tr('Сеть', 'Network')}</span><b><i class="srv-arr">↓</i><em data-r="rx">—</em></b><b><i class="srv-arr">↑</i><em data-r="tx">—</em></b></div>
        <div class="srv-stat"><span>Load</span><b data-r="load">—</b></div>
        <div class="srv-stat"><span>${tr('Аптайм', 'Uptime')}</span><b data-r="up">—</b></div>
      </div>

      <div class="srv-sites" data-r="sites"></div>

      <div class="srv-meters">
        <div class="srv-meter" data-r="mDisk"><span>${tr('Диск', 'Disk')}</span><b>—</b><i><s></s></i></div>
        <div class="srv-meter" data-r="mS3"><span>S3</span><b>—</b><i><s></s></i></div>
        <div class="srv-meter" data-r="mBackup"><span>${tr('Бэкап', 'Backup')}</span><b>—</b><i><s></s></i></div>
        <button class="srv-meter" data-r="mBalance" data-action="srv-timeweb" title="${tr('Открыть панель сервера', 'Open Cloud Dashboard')}"><span>${tr('Баланс', 'Balance')}</span><b>—</b><i><s></s></i></button>
      </div>
    `;
    this.el = el;
    el.querySelectorAll<HTMLElement>('[data-r]').forEach((n) => (this.r[n.dataset.r!] = n));
    this.paths = {
      cpuLine: el.querySelector('[data-p="cpuLine"]') as SVGPathElement,
      cpuArea: el.querySelector('[data-p="cpuArea"]') as SVGPathElement,
      memLine: el.querySelector('[data-p="memLine"]') as SVGPathElement,
      miniLine: el.querySelector('[data-p="miniLine"]') as SVGPathElement,
      miniArea: el.querySelector('[data-p="miniArea"]') as SVGPathElement,
    };

    el.addEventListener('click', (e) => {
      const t = (e.target as Element).closest<HTMLElement>('[data-action]');
      if (!t) {
        // Plain click on the card: collapse / expand
        el.classList.toggle('is-compact');
        this.saveCompact(el.classList.contains('is-compact'));
        return;
      }
      const a = t.dataset.action;
      if (a === 'srv-ssh') this.send({ type: 'server_open', target: 'ssh' });
      else if (a === 'srv-timeweb') this.send({ type: 'server_open', target: 'timeweb' });
      else if (a === 'srv-site' && t.dataset.site) this.send({ type: 'server_open', target: 'site', name: t.dataset.site });
    });

    container.appendChild(el);
    DraggableWidgetManager.makeDraggable(this.id, el, () => ({
      x: window.innerWidth - el.offsetWidth - 48,
      y: Math.max(380, window.innerHeight - el.offsetHeight - 260),
    }));
  }

  update(data: unknown): void {
    const msg = data as ServerStateMessage | ServerMetricsMessage | PrivacyModeMessage;
    if (!msg || !this.el) return;

    if (msg.type === 'privacy_mode') {
      this.setPrivacy(msg.enabled);
      return;
    }
    if (msg.type === 'serverMetrics') {
      this.pushPoint(msg.point);
      return;
    }
    if (msg.type === 'serverState') {
      this.state = msg;
      if (Array.isArray(msg.history) && msg.history.length) {
        this.history = msg.history.slice(-WINDOW_SEC);
      }
      if (typeof msg.privacyMode === 'boolean') this.setPrivacy(msg.privacyMode);
      this.renderState();
      this.renderMetrics();
    }
  }

  pause(): void { /* host-driven */ }
  resume(): void { /* host-driven */ }

  destroy(): void {
    DraggableWidgetManager.unregister(this.id);
    this.el?.remove();
    this.el = null;
  }

  private loadCompact(): boolean {
    try {
      return localStorage.getItem('lw2_srv_compact') !== '0';
    } catch {
      return true;
    }
  }

  private saveCompact(v: boolean): void {
    try {
      localStorage.setItem('lw2_srv_compact', v ? '1' : '0');
    } catch { /* ignore */ }
  }

  private send(m: unknown): void {
    window.chrome?.webview?.postMessage(m);
  }

  private setPrivacy(on: boolean): void {
    this.privacy = on;
    this.applyVisibility();
    if (!on && this.enabled) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));
  }

  private applyVisibility(): void {
    this.el?.classList.toggle('is-plugin-hidden', !this.enabled || this.privacy);
  }

  private pushPoint(p: MetricPoint): void {
    this.history.push(p);
    const cutoff = p.t - WINDOW_SEC;
    while (this.history.length && this.history[0].t < cutoff) this.history.shift();
    this.renderMetrics();
  }

  private renderMetrics(): void {
    if (!this.paths) return;
    const last = this.history[this.history.length - 1];
    const live = this.state?.ssh === 'live' || (!this.state && !!last);

    this.r.cpu.textContent = last ? `${Math.round(last.cpu)}%` : '—';
    this.r.miniCpu.textContent = last ? `${Math.round(last.cpu)}%` : '—';
    this.r.miniMem.textContent = last ? `${Math.round(last.mem)}%` : '—';
    this.r.mem.textContent = last ? `${Math.round(last.mem)}%` : '—';
    this.r.rx.textContent = last ? fmtNet(last.rxKbps) : '—';
    this.r.tx.textContent = last ? fmtNet(last.txKbps) : '—';
    this.r.load.textContent = last ? last.load1.toFixed(2) : '—';
    this.el!.classList.toggle('cpu-hot', !!last && last.cpu >= 85);
    this.el!.classList.toggle('mem-hot', !!last && last.mem >= 90);

    const pts = this.history;
    this.r.nodata.hidden = pts.length >= 2;
    this.r.nodata.textContent = this.state && !live ? tr('нет связи с сервером', 'no connection to server') : tr('ожидание данных…', 'waiting for data…');
    if (pts.length >= 2) {
      // Mini sparkline: last 3 minutes
      const tEndM = pts[pts.length - 1].t;
      const recent = pts.filter((p) => p.t >= tEndM - 180);
      const mx = (t: number) => 100 - ((tEndM - t) / 180) * 100;
      const my = (v: number) => 26 - (Math.min(100, Math.max(0, v)) / 100) * 24;
      const ml = recent.map((p, i) => `${i ? 'L' : 'M'}${mx(p.t).toFixed(1)},${my(p.cpu).toFixed(1)}`).join('');
      this.paths.miniLine.setAttribute('d', ml);
      this.paths.miniArea.setAttribute('d', recent.length > 1 ? `${ml}L100,28L${mx(recent[0].t).toFixed(1)},28Z` : '');
    } else {
      this.paths.miniLine.setAttribute('d', '');
      this.paths.miniArea.setAttribute('d', '');
    }
    if (pts.length < 2) {
      this.paths.cpuLine.setAttribute('d', '');
      this.paths.cpuArea.setAttribute('d', '');
      this.paths.memLine.setAttribute('d', '');
      return;
    }
    const tEnd = pts[pts.length - 1].t;
    const x = (t: number) => CH_W - ((tEnd - t) / WINDOW_SEC) * CH_W;
    const y = (v: number) => CH_H - 3 - (Math.min(100, Math.max(0, v)) / 100) * (CH_H - 8);
    const line = (key: 'cpu' | 'mem') => pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p[key]).toFixed(1)}`).join('');
    const cpu = line('cpu');
    this.paths.cpuLine.setAttribute('d', cpu);
    this.paths.memLine.setAttribute('d', line('mem'));
    this.paths.cpuArea.setAttribute('d', `${cpu}L${x(tEnd).toFixed(1)},${CH_H}L${x(pts[0].t).toFixed(1)},${CH_H}Z`);
  }

  private renderState(): void {
    const s = this.state;
    if (!s || !this.el) return;

    this.el.dataset.ssh = s.ssh;
    this.r.host.textContent = s.host || tr('сервер', 'server');
    const conn = sshLabel(s.ssh);
    this.r.conn.textContent = s.server && s.server.status !== 'on' ? `${tr('сервер', 'server')}: ${s.server.status}` : conn;
    this.r.up.textContent = s.server ? fmtUptime(s.server.uptimeSec) || '—' : '—';

    // Alerts
    const crit = s.alerts.filter((a) => a.level === 'critical');
    const level = crit.length ? 'critical' : s.alerts.length ? 'warning' : '';
    this.el.dataset.level = level;
    const first = crit[0] ?? s.alerts[0];
    if (first) {
      const more = s.alerts.length > 1 ? ` <span class="srv-alert-more">+${s.alerts.length - 1}</span>` : '';
      this.r.alert.innerHTML = `<i></i><span>${esc(first.text)}</span>${more}`;
      this.r.alert.hidden = false;
    } else {
      this.r.alert.hidden = true;
    }

    this.renderSites(s.sites);

    // Meters
    const disk = s.server?.diskPct ?? null;
    this.meter('mDisk', disk != null ? `${Math.round(disk)}%` : '—', disk, disk != null && disk >= 90 ? 'crit' : disk != null && disk >= 80 ? 'warn' : '');
    const s3 = s.bucket?.usedPct ?? null;
    this.meter('mS3', s3 != null ? `${Math.round(s3)}%` : '—', s3, s3 != null && s3 >= 80 ? 'warn' : '');
    const age = s.backup?.ageHours ?? null;
    // Backup "freshness" bar: full when fresh, empties over 48 h
    const fresh = age != null ? Math.max(4, 100 - (age / 48) * 100) : null;
    this.meter('mBackup', fmtAge(age), fresh, age != null && age > 48 ? 'crit' : age != null && age > 36 ? 'warn' : '');
    const days = s.account?.daysLeft ?? null;
    const balPct = days != null ? Math.min(100, (days / 30) * 100) : null;
    const balTxt = days != null ? `${Math.floor(days)} ${tr('д', 'd')}` : s.account ? `${Math.round(s.account.balance)} ₽` : '—';
    const balLevel = days != null && days < 3 && !s.account?.autopay ? 'crit' : days != null && days < 7 ? 'warn' : '';
    this.meter('mBalance', balTxt, balPct, balLevel);
    this.r.miniBal.textContent = days != null ? `${tr('баланс', 'bal')} ${Math.floor(days)} ${tr('д', 'd')}` : '';
    this.r.miniBal.dataset.level = balLevel;
    if (s.account) {
      this.r.mBalance.title = `${tr('Баланс', 'Balance')} ${Math.round(s.account.balance)} ${s.account.currency === 'RUB' ? '₽' : s.account.currency}` +
        `${s.account.autopay ? tr(' · автоплатёж', ' · autopay') : ''}${s.account.staleMin > 30 ? ` · ${tr('данные', 'data')} ${s.account.staleMin} ${tr('мин назад', 'min ago')}` : ''}`;
    }
  }

  private meter(key: string, text: string, pct: number | null, level: string): void {
    const m = this.r[key];
    if (!m) return;
    (m.querySelector('b') as HTMLElement).textContent = text;
    (m.querySelector('s') as HTMLElement).style.width = `${pct == null ? 0 : Math.min(100, Math.max(0, pct))}%`;
    m.dataset.level = level;
  }

  private renderSites(sites: ServerSiteInfo[]): void {
    const box = this.r.sites;
    if (!sites.length) {
      box.hidden = true;
      return;
    }
    box.hidden = false;
    box.innerHTML = sites
      .map((st) => {
        const ms = st.ms != null ? `${st.ms} ${tr('мс', 'ms')}` : '—';
        const tls = st.tlsDays != null && st.tlsDays < 30 ? `<em class="srv-tls">SSL ${st.tlsDays} ${tr('д', 'd')}</em>` : '';
        return `<button class="srv-site" data-action="srv-site" data-site="${esc(st.name)}" data-state="${st.state}" title="${esc(st.url)}">
          <i></i><span class="srv-site-name">${esc(st.name)}</span>${tls}<span class="srv-site-ms">${ms}</span>
        </button>`;
      })
      .join('');
  }
}
