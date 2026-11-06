import type { IWidget } from './contract';
import type { LauncherGroup } from '../types/ipc';
import { namedIcon } from '../icons';
import { DraggableWidgetManager, type WidgetPosition } from './draggable';
import { askName } from './name-dialog';

function esc(str: string): string {
  return str.replace(/[&<>"']/g, (m) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[m] || m);
}

export class FolderWidget implements IWidget {
  private static activeOpenFolder: FolderWidget | null = null;

  public static closeActive(): void {
    if (this.activeOpenFolder) {
      this.activeOpenFolder.closePanel();
    }
  }

  readonly id: string;
  private group: LauncherGroup;
  private element: HTMLElement | null = null;
  private overlayContainer: HTMLElement | null = null;
  private defaultPosFn?: () => WidgetPosition;
  private standalone: boolean;

  private panelEl: HTMLElement | null = null;
  private previewGridEl: HTMLElement | null = null;
  private titleEl: HTMLElement | null = null;
  private removeClickListener: (() => void) | null = null;

  constructor(group: LauncherGroup, defaultPosFn?: () => WidgetPosition, standalone = true) {
    this.group = group;
    this.id = `folder_${group.id}`;
    this.defaultPosFn = defaultPosFn;
    this.standalone = standalone;
  }

  mount(container: HTMLElement, overlayContainer?: HTMLElement): void {
    this.overlayContainer = overlayContainer ?? container;

    const el = document.createElement('div');
    el.className = this.standalone ? 'widget folder-tile-widget' : 'folder-tile-widget';
    el.setAttribute('data-group-id', this.group.id);
    el.innerHTML = `
      <div class="folder-tile" data-action="folder" data-name="${esc(this.group.name)}">
        <div class="folder-preview-grid"></div>
      </div>
      <div class="folder-tile-title"></div>
    `;

    this.element = el;
    this.previewGridEl = el.querySelector('.folder-preview-grid');
    this.titleEl = el.querySelector('.folder-tile-title');
    container.appendChild(el);

    // Clicking the tile toggles its curtain panel
    el.addEventListener('click', (e) => {
      e.stopPropagation();
      this.togglePanel();
    });

    // Close panel when clicking outside
    this.removeClickListener = DraggableWidgetManager.addGlobalClickListener((_, __, target) => {
      if (!this.panelEl) return;
      if (target instanceof Element) {
        if (!this.panelEl.contains(target) && !this.element?.contains(target)) {
          this.closePanel();
        }
      } else {
        this.closePanel();
      }
    });

    if (this.standalone && this.defaultPosFn) {
      DraggableWidgetManager.makeDraggable(this.id, el, this.defaultPosFn);
    }
    this.renderTile();
  }

  update(data: unknown): void {
    if (!data || typeof data !== 'object') return;
    this.group = data as LauncherGroup;
    this.renderTile();

    // If panel is currently open, refresh items on the fly
    if (this.panelEl) {
      this.renderPanelContent();
    }
  }

  pause(): void {
    this.closePanel();
  }

  resume(): void {}

  destroy(): void {
    this.closePanel();
    this.removeClickListener?.();
    this.removeClickListener = null;
    if (this.standalone) {
      DraggableWidgetManager.unregister(this.id);
    }
    this.element?.remove();
    this.element = null;
    this.previewGridEl = null;
    this.titleEl = null;
  }

  private renderTile(): void {
    if (!this.titleEl || !this.previewGridEl || !this.element) return;
    this.element.setAttribute('data-group-id', this.group.id);
    this.titleEl.textContent = this.group.name;
    this.element.querySelector('.folder-tile')?.setAttribute('data-name', this.group.name);

    const firstFour = this.group.items.slice(0, 4);
    const cellsHtml = [0, 1, 2, 3]
      .map((i) => {
        const item = firstFour[i];
        if (item) {
          return `<div class="folder-preview-cell"><img class="folder-mini-icon" src="${item.iconUrl}" decoding="async" loading="lazy" alt="" /></div>`;
        }
        return `<div class="folder-preview-cell is-empty"></div>`;
      })
      .join('');

    this.previewGridEl.innerHTML = cellsHtml;
  }

  public togglePanel(): void {
    if (this.panelEl) {
      this.closePanel();
    } else {
      this.openPanel();
    }
  }

  public openPanel(): void {
    if (!this.element || !this.overlayContainer) return;

    // Only one panel can be open at a time
    if (FolderWidget.activeOpenFolder && FolderWidget.activeOpenFolder !== this) {
      FolderWidget.activeOpenFolder.closePanel();
    }
    FolderWidget.activeOpenFolder = this;

    const panel = document.createElement('div');
    panel.className = 'widget folder-panel';
    panel.setAttribute('data-group-id', this.group.id);
    panel.innerHTML = `
      <div class="folder-panel-head">
        <div class="folder-panel-title-wrap">
          <span class="w-label">Группа</span>
          <h3 class="folder-panel-title">${esc(this.group.name)}</h3>
        </div>
        <div class="folder-panel-actions">
          <button class="w-btn-action" data-action="rename-folder" title="Переименовать">
            ${namedIcon('rename', 16)}
          </button>
          <button class="w-btn-action" data-action="open-folder" title="Открыть в проводнике">
            ${namedIcon('folderOpen', 16)}
          </button>
        </div>
      </div>
      <div class="folder-grid"></div>
    `;

    this.panelEl = panel;
    this.overlayContainer.appendChild(panel);
    this.renderPanelContent();

    // Position panel near tile and calculate scale origin
    const tileRect = this.element.getBoundingClientRect();
    const panelWidth = 380;
    const panelHeight = Math.min(520, panel.offsetHeight || 320);

    // Place panel adjacent to tile
    let targetX = tileRect.right + 18;
    if (targetX + panelWidth > window.innerWidth - 18) {
      targetX = tileRect.left - panelWidth - 18;
    }
    targetX = Math.max(18, Math.min(window.innerWidth - panelWidth - 18, targetX));

    let targetY = tileRect.top - 20;
    targetY = Math.max(18, Math.min(window.innerHeight - panelHeight - 18, targetY));

    panel.style.left = `${Math.round(targetX)}px`;
    panel.style.top = `${Math.round(targetY)}px`;

    // Compute origin relative to panel for seamless Android curtain scale+fade animation
    const originX = Math.round(tileRect.left + tileRect.width / 2 - targetX);
    const originY = Math.round(tileRect.top + tileRect.height / 2 - targetY);
    panel.style.transformOrigin = `${originX}px ${originY}px`;
    panel.classList.add('is-open');

    // Action listeners inside panel
    panel.addEventListener('click', (e) => {
      const launchTarget = (e.target as HTMLElement).closest<HTMLElement>('[data-action="launch"]');
      if (launchTarget) {
        const id = launchTarget.getAttribute('data-id');
        if (id) {
          window.chrome?.webview?.postMessage({ type: 'launch', id });
          this.closePanel();
        }
        return;
      }

      const openFolderTarget = (e.target as HTMLElement).closest<HTMLElement>('[data-action="open-folder"]');
      if (openFolderTarget) {
        window.chrome?.webview?.postMessage({ type: 'open_group', id: this.group.id });
        this.closePanel();
        return;
      }

      const renameTarget = (e.target as HTMLElement).closest<HTMLElement>('[data-action="rename-folder"]');
      if (renameTarget) {
        void askName({
          title: 'Переименовать папку',
          label: 'Имя папки',
          initial: this.group.name,
          confirmLabel: 'Сохранить',
        }).then((next) => {
          if (next && next !== this.group.name) {
            window.chrome?.webview?.postMessage({
              type: 'rename_folder',
              id: this.group.id,
              name: next,
            });
          }
        });
        return;
      }
    });
  }

  public closePanel(): void {
    if (!this.panelEl) return;
    if (FolderWidget.activeOpenFolder === this) {
      FolderWidget.activeOpenFolder = null;
    }

    const p = this.panelEl;
    this.panelEl = null;

    p.classList.remove('is-open');
    p.classList.add('is-closing');
    setTimeout(() => {
      p.remove();
    }, 180);
  }

  private renderPanelContent(): void {
    if (!this.panelEl) return;
    const grid = this.panelEl.querySelector('.folder-grid');
    if (!grid) return;

    grid.innerHTML = this.group.items
      .map(
        (item) => `
        <div class="launcher-item" data-action="launch" data-id="${esc(item.id)}" data-name="${esc(item.name)}" data-group-id="${esc(this.group.id)}" aria-label="${esc(item.name)}">
          <div class="launcher-icon-wrap">
            <img class="launcher-icon" src="${item.iconUrl}" decoding="async" loading="lazy" alt="${esc(item.name)}" />
          </div>
          <span class="launcher-name">${esc(item.name)}</span>
        </div>`
      )
      .join('');
  }
}
