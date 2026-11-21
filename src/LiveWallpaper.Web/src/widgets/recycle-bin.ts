import type { IWidget } from './contract';
import type { RecycleMessage } from '../types/ipc';
import { DraggableWidgetManager, type WidgetPosition } from './draggable';
import { askConfirm } from './confirm-dialog';
import { FolderWidget } from './folder';
import { namedIcon } from '../icons';

// Fluent delete silhouette (microsoft/fluentui-system-icons, MIT), recolored for the glass tile.
// Outline when the bin is empty, solid when it holds something.
const GLYPH = `
<svg class="recycle-glyph" viewBox="0 0 24 24" width="28" height="28" aria-hidden="true">
  <path class="recycle-empty" fill="rgba(255,255,255,0.92)" d="M10 5H14C14 3.89543 13.1046 3 12 3C10.8954 3 10 3.89543 10 5ZM8.5 5C8.5 3.067 10.067 1.5 12 1.5C13.933 1.5 15.5 3.067 15.5 5H21.25C21.6642 5 22 5.33579 22 5.75C22 6.16421 21.6642 6.5 21.25 6.5H19.9309L18.7589 18.6112C18.5729 20.5334 16.9575 22 15.0263 22H8.97369C7.04254 22 5.42715 20.5334 5.24113 18.6112L4.06908 6.5H2.75C2.33579 6.5 2 6.16421 2 5.75C2 5.33579 2.33579 5 2.75 5H8.5ZM10.5 9.75C10.5 9.33579 10.1642 9 9.75 9C9.33579 9 9 9.33579 9 9.75V17.25C9 17.6642 9.33579 18 9.75 18C10.1642 18 10.5 17.6642 10.5 17.25V9.75ZM14.25 9C14.6642 9 15 9.33579 15 9.75V17.25C15 17.6642 14.6642 18 14.25 18C13.8358 18 13.5 17.6642 13.5 17.25V9.75C13.5 9.33579 13.8358 9 14.25 9ZM6.73416 18.4667C6.84577 19.62 7.815 20.5 8.97369 20.5H15.0263C16.185 20.5 17.1542 19.62 17.2658 18.4667L18.4239 6.5H5.57608L6.73416 18.4667Z"/>
  <path class="recycle-full" fill="rgba(255,255,255,0.96)" d="M10 5H14C14 3.89543 13.1046 3 12 3C10.8954 3 10 3.89543 10 5ZM8.5 5C8.5 3.067 10.067 1.5 12 1.5C13.933 1.5 15.5 3.067 15.5 5H21.25C21.6642 5 22 5.33579 22 5.75C22 6.16421 21.6642 6.5 21.25 6.5H19.9309L18.7589 18.6112C18.5729 20.5334 16.9575 22 15.0263 22H8.97369C7.04254 22 5.42715 20.5334 5.24113 18.6112L4.06908 6.5H2.75C2.33579 6.5 2 6.16421 2 5.75C2 5.33579 2.33579 5 2.75 5H8.5ZM10.5 9.75C10.5 9.33579 10.1642 9 9.75 9C9.33579 9 9 9.33579 9 9.75V17.25C9 17.6642 9.33579 18 9.75 18C10.1642 18 10.5 17.6642 10.5 17.25V9.75ZM14.25 9C13.8358 9 13.5 9.33579 13.5 9.75V17.25C13.5 17.6642 13.8358 18 14.25 18C14.6642 18 15 17.6642 15 17.25V9.75C15 9.33579 14.6642 9 14.25 9Z"/>
</svg>`;

function itemsWord(count: number): string {
  const n10 = count % 10;
  const n100 = count % 100;
  if (n10 === 1 && n100 !== 11) return 'объект';
  if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return 'объекта';
  return 'объектов';
}

function itemsLabel(count: number): string {
  return `${count} ${itemsWord(count)}`;
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 Б';
  const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  const text = i === 0 ? String(Math.round(value)) : value >= 10 ? value.toFixed(0) : value.toFixed(1);
  return `${text.replace('.', ',')} ${units[i]}`;
}

export class RecycleBinWidget implements IWidget {
  readonly id = 'recycle-bin';
  private element: HTMLElement | null = null;
  private overlay: HTMLElement | null = null;
  private card: HTMLElement | null = null;
  private count = 0;
  private bytes = 0;
  private clearing = false;
  private removeClick: (() => void) | null = null;
  private removeContext: (() => void) | null = null;
  private onPointerDown: ((e: PointerEvent) => void) | null = null;
  private onForeignPanel: (() => void) | null = null;
  private onKey: ((e: KeyboardEvent) => void) | null = null;
  private modeObserver: MutationObserver | null = null;

  mount(container: HTMLElement): void {
    this.overlay = container;

    const el = document.createElement('div');
    el.className = 'widget recycle-bin-widget';
    el.setAttribute('aria-label', 'Корзина');
    el.innerHTML = `
      <div class="recycle-tile" data-name="Корзина">
        ${GLYPH}
        <span class="recycle-badge" hidden></span>
      </div>
      <div class="folder-tile-title">Корзина</div>
    `;
    this.element = el;
    container.appendChild(el);

    el.addEventListener('click', (e) => {
      e.stopPropagation();
      if (this.card) this.closeCard();
      else this.openCard();
    });

    this.removeClick = DraggableWidgetManager.addGlobalClickListener((_x, _y, hit) => {
      if (!this.card) return;
      if (hit instanceof Element && hit.closest('.recycle-bin-widget, .recycle-card')) return;
      this.closeCard();
    });

    this.removeContext = DraggableWidgetManager.addContextMenuHandler((_x, _y, hit) => {
      if (hit instanceof Element && hit.closest('.recycle-bin-widget, .recycle-card')) {
        if (!this.card) this.openCard();
        return true;
      }
      this.closeCard();
      return false;
    });

    this.onPointerDown = (e: PointerEvent) => {
      if (!this.card) return;
      const t = e.target;
      if (t instanceof Element && t.closest('.recycle-bin-widget, .recycle-card')) return;
      this.closeCard();
    };
    document.addEventListener('pointerdown', this.onPointerDown, true);

    this.onForeignPanel = () => this.closeCard();
    window.addEventListener('lw-close-recycle-card', this.onForeignPanel);

    this.onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && this.card) this.closeCard();
    };
    document.addEventListener('keydown', this.onKey);

    this.modeObserver = new MutationObserver(() => {
      if (this.element?.classList.contains('is-mode-hidden')) this.closeCard();
    });
    this.modeObserver.observe(el, { attributes: true, attributeFilter: ['class'] });

    DraggableWidgetManager.makeDraggable(this.id, el, () => this.defaultPosition());
    this.renderState();
  }

  update(data: unknown): void {
    if (!data || typeof data !== 'object') return;
    const msg = data as RecycleMessage;
    if (msg.type !== 'recycle') return;
    this.count = Math.max(0, Math.round(msg.count || 0));
    this.bytes = Math.max(0, msg.bytes || 0);
    this.clearing = false;
    this.renderState();
  }

  pause(): void {
    this.closeCard();
  }

  resume(): void {}

  destroy(): void {
    this.closeCard();
    this.removeClick?.();
    this.removeClick = null;
    this.removeContext?.();
    this.removeContext = null;
    if (this.onPointerDown) document.removeEventListener('pointerdown', this.onPointerDown, true);
    this.onPointerDown = null;
    if (this.onForeignPanel) window.removeEventListener('lw-close-recycle-card', this.onForeignPanel);
    this.onForeignPanel = null;
    if (this.onKey) document.removeEventListener('keydown', this.onKey);
    this.onKey = null;
    this.modeObserver?.disconnect();
    this.modeObserver = null;
    DraggableWidgetManager.unregister(this.id);
    this.element?.remove();
    this.element = null;
    this.overlay = null;
  }

  private defaultPosition(): WidgetPosition {
    return {
      x: 48,
      y: Math.max(48, window.innerHeight - 140),
    };
  }

  private renderState(): void {
    if (!this.element) return;
    const full = this.count > 0;
    this.element.classList.toggle('is-full', full);
    const badge = this.element.querySelector('.recycle-badge');
    if (badge) {
      if (full) {
        badge.removeAttribute('hidden');
        badge.textContent = this.count > 99 ? '99+' : String(this.count);
      } else {
        badge.setAttribute('hidden', '');
        badge.textContent = '';
      }
    }
    if (this.card) this.renderCard();
  }

  private openCard(): void {
    if (this.card || !this.element || !this.overlay) return;
    FolderWidget.closeActive();

    const card = document.createElement('div');
    card.className = 'widget recycle-card';
    card.setAttribute('role', 'dialog');
    card.setAttribute('aria-label', 'Корзина');
    card.addEventListener('click', (e) => {
      const btn = (e.target as Element).closest<HTMLButtonElement>('[data-action]');
      if (!btn || btn.disabled) return;
      e.stopPropagation();
      const action = btn.dataset.action;
      if (action === 'open-recycle') {
        window.chrome?.webview?.postMessage({ type: 'open_recycle' });
        this.closeCard();
      } else if (action === 'empty-recycle') {
        void this.confirmEmpty();
      }
    });

    this.card = card;
    this.overlay.appendChild(card);
    this.renderCard();
    this.placeCard();
    window.chrome?.webview?.postMessage({ type: 'recycle_refresh' });
  }

  private renderCard(): void {
    if (!this.card) return;
    const full = this.count > 0;
    const canEmpty = full && !this.clearing;
    const emptyLabel = this.clearing ? 'Очистка…' : 'Очистить';
    const fact = full
      ? `<span class="recycle-card-count">${this.count}</span><span class="recycle-card-word">${itemsWord(this.count)}</span>`
      : `<span class="recycle-card-count">Пусто</span>`;
    const size = full && this.bytes > 0 ? `<span class="recycle-card-size">${formatBytes(this.bytes)}</span>` : '';

    this.card.innerHTML = `
      <div class="recycle-card-head">
        <div class="recycle-card-fact">${fact}</div>
        ${size}
      </div>
      <div class="recycle-card-actions">
        <button type="button" class="recycle-row primary" data-action="open-recycle">${namedIcon('folderOpen', 16)}<span>Открыть</span></button>
        <button type="button" class="recycle-row${canEmpty ? ' danger' : ''}" data-action="empty-recycle" ${canEmpty ? '' : 'disabled'}>${namedIcon('trash', 16)}<span>${emptyLabel}</span></button>
      </div>
    `;
  }

  private placeCard(): void {
    if (!this.card || !this.element) return;
    const tile = this.element.getBoundingClientRect();
    const cardW = this.card.offsetWidth || 220;
    const cardH = this.card.offsetHeight || 120;
    const gap = 8;
    const margin = 12;
    let left = tile.right + gap;
    let originX = 'left';
    if (left + cardW > window.innerWidth - margin) {
      left = tile.left - cardW - gap;
      originX = 'right';
    }
    left = Math.max(margin, Math.min(window.innerWidth - cardW - margin, left));
    let top = tile.bottom - cardH;
    if (top < margin) top = Math.min(tile.top, window.innerHeight - cardH - margin);
    top = Math.max(margin, Math.min(window.innerHeight - cardH - margin, top));
    this.card.style.left = `${Math.round(left)}px`;
    this.card.style.top = `${Math.round(top)}px`;
    this.card.style.transformOrigin = `${originX} bottom`;
  }

  private closeCard(): void {
    this.card?.remove();
    this.card = null;
  }

  private async confirmEmpty(): Promise<void> {
    if (this.clearing || this.count <= 0) return;
    const size = this.bytes > 0 ? ` (${formatBytes(this.bytes)})` : '';
    const ok = await askConfirm(
      `Удалить ${itemsLabel(this.count)}${size} без возможности восстановления?`,
      'Очистить корзину',
    );
    if (!ok) return;
    this.clearing = true;
    this.renderCard();
    window.chrome?.webview?.postMessage({ type: 'empty_recycle' });
  }
}
