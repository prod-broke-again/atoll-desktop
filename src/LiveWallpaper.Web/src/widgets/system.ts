import type { IWidget } from './contract';
import type { TelemetryMessage } from '../types/ipc';
import { DraggableWidgetManager } from './draggable';
import { tr } from '../i18n';

const R = 26;
const CIRC = 2 * Math.PI * R;

interface Gauge {
  arc: SVGCircleElement;
  value: HTMLElement;
  sub?: HTMLElement;
}

export class SystemWidget implements IWidget {
  readonly id = 'system';
  private element: HTMLElement | null = null;
  private gauges: Record<'cpu' | 'ram' | 'gpu', Gauge> | null = null;
  private netDown: HTMLElement | null = null;
  private netUp: HTMLElement | null = null;
  private statusEl: HTMLElement | null = null;
  private enabled = true;

  /** Plugin / settings gate — independent of context-mode showSystem. */
  setEnabled(on: boolean): void {
    this.enabled = on;
    this.element?.classList.toggle('is-plugin-hidden', !on);
    if (on) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));
  }

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget system-widget';
    if (!this.enabled) el.classList.add('is-plugin-hidden');
    const gauge = (key: string, label: string) => `
      <div class="gauge gauge-${key}">
        <svg viewBox="0 0 64 64" class="gauge-svg">
          <circle cx="32" cy="32" r="${R}" class="gauge-track" fill="none"/>
          <circle cx="32" cy="32" r="${R}" class="gauge-arc" data-arc="${key}" fill="none"
            stroke-dasharray="${CIRC.toFixed(2)}" stroke-dashoffset="${CIRC.toFixed(2)}"/>
        </svg>
        <div class="gauge-center"><span class="gauge-value" data-val="${key}">0</span><span class="gauge-unit">%</span></div>
        <div class="gauge-label">${label}</div>
      </div>`;

    el.innerHTML = `
      <div class="w-head">
        <span class="w-label">${tr('Система', 'System')}</span>
        <span class="w-status" data-status>
          <i></i><span>${tr('онлайн', 'online')}</span>
        </span>
      </div>
      <div class="gauges">
        ${gauge('cpu', 'CPU')}
        ${gauge('ram', 'RAM')}
        ${gauge('gpu', 'GPU')}
      </div>
      <div class="w-foot net">
        <span><svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 4v15m0 0-6-6m6 6 6-6"/></svg><b data-net="down">0</b></span>
        <span class="ram-sub" data-ramsub></span>
        <span><svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 20V5m0 0-6 6m6-6 6 6"/></svg><b data-net="up">0</b></span>
      </div>
    `;
    this.element = el;
    const pick = (key: 'cpu' | 'ram' | 'gpu'): Gauge => ({
      arc: el.querySelector(`[data-arc="${key}"]`) as SVGCircleElement,
      value: el.querySelector(`[data-val="${key}"]`) as HTMLElement,
    });
    this.gauges = { cpu: pick('cpu'), ram: pick('ram'), gpu: pick('gpu') };
    this.gauges.ram.sub = el.querySelector('[data-ramsub]') as HTMLElement;
    this.netDown = el.querySelector('[data-net="down"]');
    this.netUp = el.querySelector('[data-net="up"]');
    this.statusEl = el.querySelector('[data-status]');
    container.appendChild(el);

    DraggableWidgetManager.makeDraggable(this.id, el, () => ({ x: 48, y: 40 }));
  }

  update(data: unknown): void {
    const t = data as TelemetryMessage;
    if (!this.gauges || !t || t.type !== 'telemetry') return;

    this.setGauge(this.gauges.cpu, t.cpuPercent);
    this.setGauge(this.gauges.ram, t.ramPercent);
    this.setGauge(this.gauges.gpu, t.gpuPercent);
    if (this.gauges.ram.sub) {
      this.gauges.ram.sub.textContent = `${t.ramUsedGb.toFixed(1)} / ${Math.round(t.ramTotalGb)} ${tr('ГБ', 'GB')}`;
    }
    if (this.netDown) this.netDown.textContent = this.formatNet(t.netDownloadKbps);
    if (this.netUp) this.netUp.textContent = this.formatNet(t.netUploadKbps);
  }

  pause(): void {
    this.statusEl?.classList.add('is-paused');
    const label = this.statusEl?.querySelector('span');
    if (label) label.textContent = tr('пауза', 'paused');
  }

  resume(): void {
    this.statusEl?.classList.remove('is-paused');
    const label = this.statusEl?.querySelector('span');
    if (label) label.textContent = tr('онлайн', 'online');
  }

  destroy(): void {
    this.element?.remove();
    this.element = null;
  }

  private setGauge(g: Gauge, percent: number): void {
    const p = Math.min(100, Math.max(0, percent || 0));
    g.arc.style.strokeDashoffset = (CIRC * (1 - p / 100)).toFixed(2);
    g.arc.classList.toggle('is-hot', p >= 85);
    g.value.textContent = Math.round(p).toString();
  }

  private formatNet(kbps: number): string {
    if (kbps >= 1024) return `${(kbps / 1024).toFixed(1)} ${tr('МБ/с', 'MB/s')}`;
    return `${Math.round(kbps)} ${tr('КБ/с', 'KB/s')}`;
  }
}
