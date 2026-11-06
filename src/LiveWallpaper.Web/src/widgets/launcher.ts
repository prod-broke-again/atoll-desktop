import type { IWidget } from './contract';
import type { LauncherMessage, LauncherGroup } from '../types/ipc';
import { FolderWidget } from './folder';
import { DockWidget } from './dock';
import { DraggableWidgetManager } from './draggable';

export class LauncherWidgetManager implements IWidget {
  readonly id = 'launcher-manager';
  private container: HTMLElement | null = null;
  private foldersEl: HTMLElement | null = null;
  private foldersItemsEl: HTMLElement | null = null;
  private dockWidget: DockWidget | null = null;
  private folderWidgets = new Map<string, FolderWidget>();

  mount(container: HTMLElement): void {
    this.container = container;

    // 1. Folders container widget (draggable column placed next to dock under system widget)
    const foldersEl = document.createElement('div');
    foldersEl.className = 'widget folders-widget';
    foldersEl.innerHTML = '<div class="folders-items"></div>';
    foldersEl.style.display = 'none';
    this.foldersEl = foldersEl;
    this.foldersItemsEl = foldersEl.querySelector('.folders-items');
    container.appendChild(foldersEl);
    DraggableWidgetManager.makeDraggable('folders', foldersEl, () => ({ x: 48, y: 280 }));

    // 2. Dock widget (placed next to folder column under system widget)
    this.dockWidget = new DockWidget(() => ({
      x: 148,
      y: 280,
    }));
    this.dockWidget.mount(container);
  }

  update(data: unknown): void {
    if (!data || typeof data !== 'object') return;
    const msg = data as LauncherMessage;
    if (!this.container) return;

    // 1. Update Pinned Dock
    if (this.dockWidget && Array.isArray(msg.pinned)) {
      this.dockWidget.update(msg.pinned);
    }

    // 2. Update Folder Groups
    if (Array.isArray(msg.groups)) {
      const incomingIds = new Set(msg.groups.map((g: LauncherGroup) => g.id));

      // Remove folders that no longer exist
      for (const [id, widget] of this.folderWidgets) {
        if (!incomingIds.has(id)) {
          widget.destroy();
          this.folderWidgets.delete(id);
        }
      }

      if (this.foldersEl) {
        this.foldersEl.style.display = msg.groups.length > 0 ? '' : 'none';
      }

      if (this.foldersItemsEl) {
        if (msg.groups.length > 8) {
          this.foldersItemsEl.classList.add('is-compact');
        } else {
          this.foldersItemsEl.classList.remove('is-compact');
        }
      }

      // Update existing or mount new folders
      msg.groups.forEach((group: LauncherGroup) => {
        const existing = this.folderWidgets.get(group.id);
        if (existing) {
          existing.update(group);
        } else if (this.foldersItemsEl && this.container) {
          const folder = new FolderWidget(group, undefined, false);
          folder.mount(this.foldersItemsEl, this.container);
          this.folderWidgets.set(group.id, folder);
        }
      });

      DraggableWidgetManager.refresh('folders');
    }
  }

  pause(): void {
    FolderWidget.closeActive();
    this.dockWidget?.pause();
    for (const w of this.folderWidgets.values()) {
      w.pause();
    }
  }

  resume(): void {
    this.dockWidget?.resume();
    for (const w of this.folderWidgets.values()) {
      w.resume();
    }
  }

  destroy(): void {
    FolderWidget.closeActive();
    this.dockWidget?.destroy();
    this.dockWidget = null;
    for (const w of this.folderWidgets.values()) {
      w.destroy();
    }
    this.folderWidgets.clear();
    DraggableWidgetManager.unregister('folders');
    this.foldersEl?.remove();
    this.foldersEl = null;
    this.foldersItemsEl = null;
    this.container = null;
  }
}
