import type { IWidget } from './contract';
import { DraggableWidgetManager } from './draggable';

export interface MediaPayload {
  hasMedia: boolean;
  title: string;
  artist: string;
  album: string;
  isPlaying: boolean;
  thumbnailBase64?: string | null;
  positionSeconds?: number;
  durationSeconds?: number;
  canSeek?: boolean;
  isYandexMusic?: boolean;
  isLiked?: boolean;
  trackId?: string | null;
}

const ICON_PLAY = `<svg viewBox="0 0 24 24" width="18" height="18"><path d="M8 5.5v13a1 1 0 0 0 1.5.86l10.5-6.5a1 1 0 0 0 0-1.72L9.5 4.64A1 1 0 0 0 8 5.5z" fill="currentColor"/></svg>`;
const ICON_PAUSE = `<svg viewBox="0 0 24 24" width="18" height="18"><rect x="6.5" y="5" width="4" height="14" rx="1.2" fill="currentColor"/><rect x="13.5" y="5" width="4" height="14" rx="1.2" fill="currentColor"/></svg>`;

function formatTime(seconds: number): string {
  if (isNaN(seconds) || seconds < 0) return '0:00';
  const mins = Math.floor(seconds / 60);
  const secs = Math.floor(seconds % 60);
  return `${mins}:${secs < 10 ? '0' : ''}${secs}`;
}

export class MediaWidget implements IWidget {
  readonly id = 'media';
  private element: HTMLElement | null = null;
  private refs: {
    art: HTMLImageElement;
    backdrop: HTMLElement;
    title: HTMLElement;
    artist: HTMLElement;
    play: HTMLElement;
    progressFill: HTMLElement;
    timeCurrent: HTMLElement;
    timeDuration: HTMLElement;
    ymActions: HTMLElement;
    progressBar: HTMLElement;
  } | null = null;
  private lastThumb: string | null | undefined;
  private currentDuration = 0;
  private currentPos = 0;
  private basePos = 0; // position reported by the host…
  private baseAt = 0; // …and when we received it (performance.now)
  private playing = false;
  private ticker: number | null = null;

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget media-widget';
    el.style.display = 'none';
    el.innerHTML = `
      <div class="media-backdrop"></div>
      <div class="media-row">
        <div class="media-art"><img alt=""/></div>
        <div class="media-meta">
          <div class="media-eyebrow">
            <span class="eq"><i></i><i></i><i></i><i></i></span>
            <span class="media-source-label">Сейчас играет</span>
          </div>
          <div class="media-title"></div>
          <div class="media-artist"></div>
        </div>
      </div>

      <div class="media-timeline-container">
        <div class="media-progress-bar" data-seek title="Перемотать">
          <div class="media-progress-fill"></div>
        </div>
        <div class="media-time-labels">
          <span class="media-time-current">0:00</span>
          <span class="media-time-duration">0:00</span>
        </div>
      </div>

      <div class="media-footer-row">
        <div class="media-ym-actions" style="display: none;">
          <button class="m-pill-btn" data-action="media_open_wave" title="Открыть главную с Моей волной">
            <span class="pill-dot"></span> Волна
          </button>
          <button class="m-pill-btn" data-action="media_open_collection" title="Открыть Коллекцию">
            Коллекция
          </button>
        </div>

        <div class="media-controls">
          <button class="m-btn" data-action="media_prev" title="Назад">
            <svg viewBox="0 0 24 24" width="16" height="16"><path d="M6 5v14M18 6.2v11.6a.8.8 0 0 1-1.2.7L9 12.7a.8.8 0 0 1 0-1.4l7.8-5.8a.8.8 0 0 1 1.2.7z" fill="currentColor" stroke="currentColor"/></svg>
          </button>
          <button class="m-btn m-btn-main" data-action="media_toggle_play" title="Пауза / Воспроизведение"></button>
          <button class="m-btn" data-action="media_next" title="Вперёд">
            <svg viewBox="0 0 24 24" width="16" height="16"><path d="M18 5v14M6 6.2v11.6a.8.8 0 0 0 1.2.7l7.8-5.8a.8.8 0 0 0 0-1.4L7.2 5.5A.8.8 0 0 0 6 6.2z" fill="currentColor" stroke="currentColor"/></svg>
          </button>
        </div>
      </div>
    `;
    this.element = el;
    this.refs = {
      art: el.querySelector('.media-art img') as HTMLImageElement,
      backdrop: el.querySelector('.media-backdrop') as HTMLElement,
      title: el.querySelector('.media-title') as HTMLElement,
      artist: el.querySelector('.media-artist') as HTMLElement,
      play: el.querySelector('[data-action="media_toggle_play"]') as HTMLElement,
      progressFill: el.querySelector('.media-progress-fill') as HTMLElement,
      timeCurrent: el.querySelector('.media-time-current') as HTMLElement,
      timeDuration: el.querySelector('.media-time-duration') as HTMLElement,
      ymActions: el.querySelector('.media-ym-actions') as HTMLElement,
      progressBar: el.querySelector('.media-progress-bar') as HTMLElement,
    };

    el.querySelectorAll<HTMLElement>('[data-action]').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        this.send(btn.dataset.action!);
      });
    });

    // Handle progress bar seek click
    this.refs.progressBar.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!this.refs || this.currentDuration <= 0 || this.refs.progressBar.classList.contains('is-disabled')) return;
      const rect = this.refs.progressBar.getBoundingClientRect();
      const clickX = Math.max(0, Math.min(e.clientX - rect.left, rect.width));
      const ratio = clickX / rect.width;
      const targetSec = ratio * this.currentDuration;
      
 
      this.basePos = this.currentPos = targetSec;
      this.baseAt = performance.now();
      this.updateTimelineUI();

      window.chrome?.webview?.postMessage({
        type: 'media_seek',
        position: Math.round(targetSec)
      });
    });

    container.appendChild(el);

    DraggableWidgetManager.makeDraggable(this.id, el, () => ({
      x: window.innerWidth - el.offsetWidth - 48,
      y: window.innerHeight - el.offsetHeight - 72,
    }));
  }

  update(data: unknown): void {
    if (!this.element || !this.refs) return;
    const m = data as MediaPayload | null;
    if (!m || !m.hasMedia || !m.title) {
      this.element.style.display = 'none';
      this.playing = false;
      this.syncTicker();
      return;
    }

    const wasHidden = this.element.style.display === 'none';
    this.element.style.display = '';
    this.element.classList.toggle('is-playing', m.isPlaying);

    this.refs.title.textContent = m.title;
    this.refs.artist.textContent = m.artist || 'Неизвестный исполнитель';
    this.refs.play.innerHTML = m.isPlaying ? ICON_PAUSE : ICON_PLAY;

    // Yandex Music detection & pills
    if (m.isYandexMusic) {
      this.refs.ymActions.style.display = 'flex';
      const label = this.element.querySelector('.media-source-label');
      if (label) label.textContent = 'Яндекс Музыка';
    } else {
      this.refs.ymActions.style.display = 'none';
      const label = this.element.querySelector('.media-source-label');
      if (label) label.textContent = 'Сейчас играет';
    }

    // Timeline: the host only reports changes; between them we extrapolate locally.
    this.currentDuration = m.durationSeconds ?? 0;
    this.basePos = this.currentPos = m.positionSeconds ?? 0;
    this.baseAt = performance.now();
    this.playing = m.isPlaying;
    this.refs.progressBar.classList.toggle('is-disabled', !m.canSeek);
    this.updateTimelineUI();
    this.syncTicker();

    if (m.thumbnailBase64 !== this.lastThumb) {
      this.lastThumb = m.thumbnailBase64;
      const src = m.thumbnailBase64 ? `data:image/jpeg;base64,${m.thumbnailBase64}` : '';
      this.refs.art.src = src;
      this.refs.art.style.visibility = src ? 'visible' : 'hidden';
      this.refs.backdrop.style.backgroundImage = src ? `url("${src}")` : 'none';
    }

    if (wasHidden) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));
  }

  private syncTicker(): void {
    const need = this.playing && this.currentDuration > 0;
    if (need && this.ticker === null) {
      this.ticker = window.setInterval(() => {
        this.currentPos = Math.min(this.currentDuration, this.basePos + (performance.now() - this.baseAt) / 1000);
        this.updateTimelineUI();
      }, 250);
    } else if (!need && this.ticker !== null) {
      clearInterval(this.ticker);
      this.ticker = null;
    }
  }

  private updateTimelineUI(): void {
    if (!this.refs) return;
    const pos = Math.max(0, this.currentPos);
    const dur = Math.max(0, this.currentDuration);
    
    const pct = dur > 0 ? Math.min(100, Math.max(0, (pos / dur) * 100)) : 0;
    this.refs.progressFill.style.width = `${pct}%`;
    this.refs.timeCurrent.textContent = formatTime(pos);
    this.refs.timeDuration.textContent = dur > 0 ? `-${formatTime(dur - pos)}` : '0:00';
  }

  pause(): void { /* host-driven */ }
  resume(): void { /* host-driven */ }

  destroy(): void {
    this.element?.remove();
    this.element = null;
  }

  private send(type: string): void {
    window.chrome?.webview?.postMessage({ type });
  }
}
