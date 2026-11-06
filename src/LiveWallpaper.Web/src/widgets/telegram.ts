import type { IWidget } from './contract';
import type { TelegramChat, TelegramMessage } from '../types/ipc';
import { DraggableWidgetManager } from './draggable';
import { SmoothScroller } from './scroller';
import { tr, isEn } from '../i18n';

const esc = (s: string) =>
  (s || '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

export class TelegramWidget implements IWidget {
  readonly id = 'telegram';
  private el: HTMLElement | null = null;
  private list: HTMLElement | null = null;
  private meta: HTMLElement | null = null;
  private scroller: SmoothScroller | null = null;
  private unsubscribe: Array<() => void> = [];
  private key = '';

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget tg';
    el.style.display = 'none';
    el.innerHTML = `
      <div class="w-head tg-head">
        <span class="w-label">Telegram</span>
        <span class="w-meta" data-r="meta"></span>
      </div>
      <div class="tg-frame agenda-frame" data-r="frame">
        <div class="tg-scroll agenda-scroll" data-r="list"></div>
      </div>
    `;
    this.el = el;
    this.list = el.querySelector('[data-r="list"]');
    this.meta = el.querySelector('[data-r="meta"]');
    const frame = el.querySelector('[data-r="frame"]') as HTMLElement;
    this.scroller = new SmoothScroller(this.list!, frame);

    el.addEventListener('click', (e) => {
      const row = (e.target as Element).closest<HTMLElement>('[data-action="tg-open"]');
      if (!row?.dataset.id) return;
      const id = Number(row.dataset.id);
      if (!Number.isSafeInteger(id)) return;
      window.chrome?.webview?.postMessage({ type: 'open_telegram_chat', id });
    });

    this.unsubscribe.push(
      DraggableWidgetManager.addGlobalWheelListener((_x, _y, delta, target) => {
        if (!this.el || this.el.style.display === 'none' || !target || !this.el.contains(target)) return false;
        this.scroller?.wheel(delta);
        return true;
      }),
    );

    container.appendChild(el);
    // Left column is the system card, then folders. Start in the open band to the right of "Система".
    DraggableWidgetManager.makeDraggable(this.id, el, () => this.defaultPosition());
  }

  /** Used before the card has a size; settle() runs again once it is actually visible. */
  private defaultPosition(): { x: number; y: number } {
    const sys = document.querySelector('.system-widget');
    if (sys instanceof HTMLElement && sys.offsetWidth > 0 && sys.style.display !== 'none') {
      const left = parseFloat(sys.style.left) || 48;
      const top = parseFloat(sys.style.top) || 40;
      return { x: left + sys.offsetWidth + 16, y: top };
    }
    return { x: 360, y: 40 };
  }

  update(data: unknown): void {
    const payload = data as TelegramMessage['data'] | null;
    if (!this.el || !this.list || !payload || typeof payload !== 'object') return;

    const chats = Array.isArray(payload.chats) ? payload.chats : [];
    const show = payload.enabled !== false && payload.available === true;
    const wasHidden = this.el.style.display === 'none';
    this.el.style.display = show ? '' : 'none';
    if (!show) {
      this.key = '';
      return;
    }
    // The card starts hidden, so the first layout pass never resolves overlaps.
    if (wasHidden) requestAnimationFrame(() => DraggableWidgetManager.refresh(this.id));

    if (this.meta) this.meta.textContent = '';

    const key = chats.map((chat) => `${chat.id}:${chat.unread}:${chat.name}:${chat.title ?? ''}:${chat.chatType}`).join('\n');
    if (key === this.key) {
      this.scroller?.sync();
      return;
    }
    this.key = key;
    this.list.innerHTML = chats.length
      ? chats.map((chat) => this.row(chat)).join('')
      : `<div class="tg-empty">${tr('нет важных чатов', 'no tracked chats')}</div>`;
    this.scroller?.sync();
  }

  pause(): void { /* host-driven */ }
  resume(): void { /* host-driven */ }

  destroy(): void {
    for (const unsubscribe of this.unsubscribe) unsubscribe();
    this.unsubscribe = [];
    this.scroller?.destroy();
    this.scroller = null;
    DraggableWidgetManager.unregister(this.id);
    this.el?.remove();
    this.el = null;
  }

  private row(chat: TelegramChat): string {
    const badge = chat.unread > 0
      ? `<b class="tg-badge">${chat.unread > 99 ? '99+' : chat.unread}</b>`
      : '';
    const sub = subtitle(chat);
    const subHtml = sub ? `<span class="tg-sub">${esc(sub)}</span>` : '';
    return `<button type="button" class="tg-row" data-action="tg-open" data-id="${chat.id}"><span class="tg-copy"><span class="tg-name">${esc(chat.name)}</span>${subHtml}</span>${badge}</button>`;
  }
}

function subtitle(chat: TelegramChat): string {
  const name = (chat.name || '').trim();
  const title = (chat.title || '').trim();
  const showTitle = title.length > 0 && title.localeCompare(name, isEn() ? 'en' : 'ru', { sensitivity: 'accent' }) !== 0;
  const kind = kindLabel(chat);
  const parts: string[] = [];
  if (showTitle) parts.push(title);
  if (kind) parts.push(kind);
  if (chat.unread > 0) parts.push(tr('непрочитано', 'unread'));
  return parts.join(' · ');
}

function kindLabel(chat: TelegramChat): string {
  const type = (chat.chatType || '').toLowerCase();
  if (type === 'group') return tr('группа', 'group');
  if (type === 'channel') return tr('канал', 'channel');
  if (type === 'forum') return tr('форум', 'forum');
  if (type === 'supergroup') {
    const blob = `${chat.name} ${chat.title ?? ''}`.toLocaleLowerCase(isEn() ? 'en' : 'ru');
    return blob.includes('форум') || blob.includes('forum') ? tr('форум', 'forum') : tr('супергруппа', 'supergroup');
  }
  return '';
}
