import type { IWidget } from './contract';
import { DraggableWidgetManager } from './draggable';
import { tr } from '../i18n';

export interface WeatherPayload {
  enabled: boolean;
  city: string;
  temperature: number;
  apparentTemperature: number;
  weatherCode: number;
  weatherDescription: string;
  icon: string;
  windSpeed: number;
  relativeHumidity: number;
  isDay: boolean;
  units: string;
}

const signed = (n: number) => {
  const r = Math.round(n);
  return `${r > 0 ? '+' : ''}${r}`;
};

const esc = (s: string) =>
  s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

export class WeatherWidget implements IWidget {
  readonly id = 'weather';
  private element: HTMLElement | null = null;

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget weather-widget';
    el.style.display = 'none';
    this.element = el;
    container.appendChild(el);

    DraggableWidgetManager.makeDraggable(this.id, el, () => ({
      x: window.innerWidth - el.offsetWidth - 48,
      y: 210,
    }));
  }

  update(data: unknown): void {
    if (!this.element) return;
    const w = data as WeatherPayload | null;
    if (!w || !w.enabled || !w.city) {
      this.element.style.display = 'none';
      return;
    }

    const wasHidden = this.element.style.display === 'none';
    this.element.style.display = '';
    this.element.innerHTML = `
      <div class="w-head">
        <span class="w-label">${tr('Погода', 'Weather')}</span>
        <span class="w-meta">${esc(w.city)}</span>
      </div>
      <div class="weather-main">
        <div class="weather-icon">${this.icon(w.icon)}</div>
        <div class="weather-temp">${signed(w.temperature)}<sup>°</sup></div>
        <div class="weather-desc">
          <div>${esc(w.weatherDescription || '')}</div>
          <small>${tr('ощущается', 'feels like')} ${signed(w.apparentTemperature)}°</small>
        </div>
      </div>
      <div class="w-foot chips">
        <span class="chip"><svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 3s6 6.4 6 11a6 6 0 0 1-12 0c0-4.6 6-11 6-11z"/></svg>${Math.round(w.relativeHumidity)}%</span>
        <span class="chip"><svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M3 8h11a3 3 0 1 0-3-3M3 16h15a3 3 0 1 1-3 3M3 12h8"/></svg>${w.windSpeed} ${tr('м/с', 'm/s')}</span>
      </div>
    `;
    if (wasHidden) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));
  }

  pause(): void { /* host-driven */ }
  resume(): void { /* host-driven */ }

  destroy(): void {
    this.element?.remove();
    this.element = null;
  }

  private icon(icon: string): string {
    const sun = `<circle cx="12" cy="12" r="4.2" class="i-sun"/><g class="i-rays"><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"/></g>`;
    const cloud = `<path class="i-cloud" d="M17 18.5H8a4.5 4.5 0 1 1 1-8.9A5.5 5.5 0 0 1 19.6 11 3.8 3.8 0 0 1 17 18.5z"/>`;
    const wrap = (inner: string) => `<svg viewBox="0 0 24 24" width="44" height="44">${inner}</svg>`;
    switch (icon) {
      case 'sun':
        return wrap(sun);
      case 'moon':
        return wrap(`<path class="i-moon" d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>`);
      case 'sun-cloud':
      case 'cloud-sun':
        return wrap(`<g transform="translate(-3 -3) scale(.8)">${sun}</g>${cloud}`);
      case 'rain':
      case 'heavy-rain':
        return wrap(`<g transform="translate(0 -2)">${cloud}</g><path class="i-rain" d="M9 19l-1 2.5M13 19l-1 2.5M17 19l-1 2.5"/>`);
      case 'snow':
      case 'heavy-snow':
        return wrap(`<g transform="translate(0 -2)">${cloud}</g><path class="i-snow" d="M9 20.5h.01M13 21.5h.01M17 20.5h.01"/>`);
      case 'thunder':
      case 'thunder-hail':
        return wrap(`<g transform="translate(0 -2)">${cloud}</g><path class="i-bolt" d="M13 15l-2.5 4h3L11 23"/>`);
      default:
        return wrap(cloud);
    }
  }
}
