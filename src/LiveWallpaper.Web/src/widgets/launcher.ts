import type { IWidget } from './contract';
import type { LauncherMessage, LauncherGroup } from '../types/ipc';
import { FolderWidget } from './folder';
import { DockWidget } from './dock';

export class LauncherWidgetManager implements IWidget {
  readonly id = 'launcher-manager';
  private container: HTMLElement | null = null;
  private dockWidget: DockWidget | null = null;
  private folderWidgets = new Map<string, FolderWidget>();

  mount(container: HTMLElement): void {
    this.container = container;

    // Create and mount pinned dock (placed next to folder column under system widget)
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

      // Update existing or mount new folders
      msg.groups.forEach((group: LauncherGroup, index: number) => {
        const existing = this.folderWidgets.get(group.id);
        if (existing) {
          existing.update(group);
        } else {
          const folder = new FolderWidget(group, () => ({
            x: 48,
            y: 280 + index * 110,
          }));
          folder.mount(this.container!);
          this.folderWidgets.set(group.id, folder);
        }
      });
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
    this.container = null;
  }
}
