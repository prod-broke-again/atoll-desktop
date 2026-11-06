import type { IWidget } from './contract';
import type { LauncherItem } from '../types/ipc';
import { DraggableWidgetManager, type WidgetPosition } from './draggable';

function esc(str: string): string {
  return str.replace(/[&<>"']/g, (m) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[m] || m);
}

export class DockWidget implements IWidget {
  readonly id = 'dock';
  private element: HTMLElement | null = null;
  private containerEl: HTMLElement | null = null;
  private items: LauncherItem[] = [];
  private defaultPosFn: () => WidgetPosition;

  constructor(defaultPosFn?: () => WidgetPosition) {
    this.defaultPosFn = defaultPosFn ?? (() => ({ x: 48, y: 720 }));
  }

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget dock-widget';
    el.innerHTML = '<div class="dock-items"></div>';

    this.element = el;
    this.containerEl = el.querySelector('.dock-items');
    container.appendChild(el);

    // Click handler for dock items
    el.addEventListener('click', (e) => {
      const target = (e.target as HTMLElement).closest<HTMLElement>('[data-action="launch"]');
      if (!target) return;
      const itemId = target.getAttribute('data-id');
      if (itemId) {
        window.chrome?.webview?.postMessage({ type: 'launch', id: itemId });
      }
    });

    DraggableWidgetManager.makeDraggable(this.id, el, this.defaultPosFn);
    this.renderItems();
  }

  update(data: unknown): void {
    if (!Array.isArray(data)) return;
    this.items = data as LauncherItem[];
    if (this.element) {
      this.element.style.display = this.items.length > 0 ? '' : 'none';
    }
    this.renderItems();
    DraggableWidgetManager.refresh(this.id);
  }

  pause(): void {}
  resume(): void {}

  destroy(): void {
    DraggableWidgetManager.unregister(this.id);
    this.element?.remove();
    this.element = null;
    this.containerEl = null;
  }

  private renderItems(): void {
    if (!this.containerEl) return;
    if (this.items.length === 0) {
      this.containerEl.innerHTML = '';
      return;
    }

    this.containerEl.innerHTML = this.items
      .map(
        (item) => `
        <div class="dock-item" data-action="launch" data-id="${esc(item.id)}" data-name="${esc(item.name)}" aria-label="${esc(item.name)}">
          <img class="dock-icon" src="${item.iconUrl}" decoding="async" loading="lazy" alt="${esc(item.name)}" />
        </div>`
      )
      .join('');
  }
}
