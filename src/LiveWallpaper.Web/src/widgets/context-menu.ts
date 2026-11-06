import type { LauncherActionKind, LauncherActionMessage } from '../types/ipc';
import { namedIcon, type IconName } from '../icons';
import { DraggableWidgetManager } from './draggable';
import { askConfirm } from './confirm-dialog';
import { askName } from './name-dialog';

export interface ContextMenuTarget {
  id: string;
  name: string;
  kind: 'item' | 'group';
  x: number;
  y: number;
}

export type DesktopMenuAction = 'create_folder';

function svg(name: IconName): string {
  return namedIcon(name, 16);
}

function postAction(target: ContextMenuTarget, action: LauncherActionKind): void {
  const dpr = window.devicePixelRatio || 1;
  const payload: LauncherActionMessage = {
    type: 'launcher_action',
    action,
    id: target.id,
    name: target.name,
    kind: target.kind,
    x: Math.round(target.x * dpr),
    y: Math.round(target.y * dpr),
  };
  window.chrome?.webview?.postMessage(payload);
}

async function promptRename(target: ContextMenuTarget): Promise<void> {
  const next = await askName({
    title: target.kind === 'group' ? 'Переименовать папку' : 'Переименовать',
    label: 'Имя',
    initial: target.name,
    confirmLabel: 'Сохранить',
  });
  if (!next || next === target.name) return;
  if (target.kind === 'group') {
    window.chrome?.webview?.postMessage({ type: 'rename_folder', id: target.id, name: next });
  } else {
    window.chrome?.webview?.postMessage({ type: 'rename_item', id: target.id, name: next });
  }
}

export async function createDesktopFolder(): Promise<void> {
  const name = await askName({
    title: 'Новая папка',
    label: 'Имя на рабочем столе',
    initial: 'Новая папка',
    confirmLabel: 'Создать',
  });
  if (name) {
    window.chrome?.webview?.postMessage({ type: 'create_folder', name });
  }
}

export class LauncherContextMenu {
  private static root: HTMLElement | null = null;
  private static target: ContextMenuTarget | null = null;
  private static dismissListener: (() => void) | null = null;

  public static isOpen(): boolean {
    return !!this.root;
  }

  public static open(target: ContextMenuTarget): void {
    this.close();
    this.target = target;

    const el = document.createElement('div');
    el.className = 'launcher-context-menu';
    el.setAttribute('role', 'menu');

    const quick = document.createElement('div');
    quick.className = 'lcm-quick';
    const quickActions: { action: LauncherActionKind; label: string; icon: IconName }[] =
      target.kind === 'group'
        ? [
            { action: 'rename', label: 'Переименовать', icon: 'rename' },
            { action: 'delete', label: 'Удалить', icon: 'trash' },
          ]
        : [
            { action: 'cut', label: 'Вырезать', icon: 'cut' },
            { action: 'copy', label: 'Копировать', icon: 'copy' },
            { action: 'rename', label: 'Переименовать', icon: 'rename' },
            { action: 'delete', label: 'Удалить', icon: 'trash' },
          ];
    quick.innerHTML = quickActions
      .map(
        (q) =>
          `<button type="button" class="lcm-quick-btn" data-action="${q.action}" title="${q.label}">${svg(q.icon)}</button>`
      )
      .join('');
    el.appendChild(quick);

    const list = document.createElement('div');
    list.className = 'lcm-list';

    const rows: { action: LauncherActionKind; label: string; icon: IconName; show: boolean }[] = [
      { action: 'open', label: 'Открыть', icon: 'open', show: true },
      { action: 'runas', label: 'Запуск от имени администратора', icon: 'runas', show: target.kind === 'item' },
      { action: 'open_location', label: 'Открыть расположение файла', icon: 'folder', show: target.kind === 'item' },
      { action: 'copy_path', label: 'Копировать как путь', icon: 'path', show: true },
      { action: 'properties', label: 'Свойства', icon: 'props', show: true },
      { action: 'pin_start', label: 'Закрепить в меню «Пуск»', icon: 'pin', show: target.kind === 'item' },
      { action: 'more', label: 'Показать дополнительные параметры', icon: 'more', show: true },
    ];

    list.innerHTML = rows
      .filter((r) => r.show)
      .map(
        (r) =>
          `<button type="button" class="lcm-row" data-action="${r.action}"><span class="lcm-row-icon">${svg(r.icon)}</span><span class="lcm-row-label">${r.label}</span></button>`
      )
      .join('');
    el.appendChild(list);

    el.addEventListener('click', (e) => {
      const btn = (e.target as HTMLElement).closest<HTMLElement>('[data-action]');
      if (!btn || !this.target) return;
      e.stopPropagation();
      const action = btn.getAttribute('data-action') as LauncherActionKind;
      const t = this.target;
      this.close();
      if (action === 'rename') {
        void promptRename(t);
      } else if (action === 'delete') {
        void askConfirm(`Удалить «${t.name}»?`, 'Удаление').then((ok) => {
          if (ok) postAction(t, 'delete');
        });
      } else {
        postAction(t, action);
      }
    });

    this.mount(el, target.x, target.y);
  }

  /** Empty-desktop menu (create folder, …). */
  public static openDesktop(x: number, y: number): void {
    this.close();
    this.target = null;

    const el = document.createElement('div');
    el.className = 'launcher-context-menu launcher-context-menu--desktop';
    el.setAttribute('role', 'menu');

    const list = document.createElement('div');
    list.className = 'lcm-list';
    list.innerHTML = `
      <button type="button" class="lcm-row" data-desktop-action="create_folder">
        <span class="lcm-row-icon">${svg('folderPlus')}</span>
        <span class="lcm-row-label">Создать папку</span>
      </button>`;
    el.appendChild(list);

    el.addEventListener('click', (e) => {
      const btn = (e.target as HTMLElement).closest<HTMLElement>('[data-desktop-action]');
      if (!btn) return;
      e.stopPropagation();
      const action = btn.getAttribute('data-desktop-action') as DesktopMenuAction;
      this.close();
      if (action === 'create_folder') void createDesktopFolder();
    });

    this.mount(el, x, y);
  }

  private static mount(el: HTMLElement, x: number, y: number): void {
    const overlay = document.getElementById('widgetsOverlay') ?? document.body;
    overlay.appendChild(el);
    this.root = el;

    const menuW = 280;
    const menuH = el.offsetHeight || 320;
    let left = x;
    let top = y;
    left = Math.max(12, Math.min(window.innerWidth - menuW - 12, left));
    top = Math.max(12, Math.min(window.innerHeight - menuH - 12, top));
    el.style.left = `${Math.round(left)}px`;
    el.style.top = `${Math.round(top)}px`;

    requestAnimationFrame(() => el.classList.add('is-visible'));

    // Dismiss only on clicks outside the menu — inside clicks must reach [data-action] first.
    this.dismissListener = DraggableWidgetManager.addGlobalClickListener((_x, _y, hit) => {
      if (hit instanceof Element && hit.closest('.launcher-context-menu')) return;
      this.close();
    });
  }

  public static close(): void {
    this.dismissListener?.();
    this.dismissListener = null;
    this.target = null;
    this.root?.remove();
    this.root = null;
  }
}
