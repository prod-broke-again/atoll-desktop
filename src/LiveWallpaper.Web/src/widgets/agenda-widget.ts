import type { IWidget } from './contract';
import type { TodayEvent, TodayTask } from '../types/ipc';
import { DraggableWidgetManager } from './draggable';
import { SmoothScroller } from './scroller';
import { tr } from '../i18n';
import {
  esc,
  eventStatus,
  eventPhase,
  splitTimeLabel,
  indicatorWhen,
  groupTasks,
  priorityName,
  priorityClass,
  tasksCountLabel,
  fmtDateLong,
  eventTimeRange,
  parseLocal,
  type TodayPayload,
} from './agenda';

const MAX_PANEL_VH = 0.7;
const MIN_SCROLL_H = 96;
const MORPH_MS = 240;
const DETAIL_MS = 180;
const EDGE = 16;
const TASKS_FOLD = 8; // show this many, then "ещё N"
const FEW_EVENTS = 3; // both sections stacked (no segmented control) when content is small
const FEW_TASKS = 4;
const OPEN_COOLDOWN_MS = 1500; // "Открыть в WEEEK" double-click guard (same as launcher)

type Section = 'events' | 'tasks';
type Detail = { kind: 'task'; id: string } | { kind: 'event'; idx: number } | null;

const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

const CHEVRON = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M6 15l6-6 6 6"/></svg>';
const BACK = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M15 6l-6 6 6 6"/></svg>';
const EXT = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M14 5h5v5M19 5l-8 8M19 14v5H5V5h5"/></svg>';

/**
 * Agenda (Google Calendar + WEEEK) as its own widget, split out of the clock.
 * Collapsed: a fixed-width card with the next event and open-task counters.
 * Click: morphs into a panel with events / tasks and an in-panel detail view.
 * Expansion state is never persisted — the widget always starts collapsed.
 * Hidden entirely when nothing is connected and there is nothing to show.
 */
export class AgendaWidget implements IWidget {
  readonly id = 'agenda';
  private element: HTMLElement | null = null;
  private r: Record<string, HTMLElement> = {};
  private timer: number | null = null;
  private lastMinute = -1;

  private today: TodayPayload | null = null;
  private agendaEnabled = true; // context mode "showToday"
  private open = false;
  private morphToken = 0; // a morph is in flight while > 0 and matches the pending finaliser
  private detailToken = 0;
  private section: Section = 'events';
  private detail: Detail = null;
  private showAllTasks = false;
  private anchor: { x: number; y: number; w: number; h: number } | null = null;
  private movedWhileOpen = false;
  private lastOpenAt = 0;
  private lastToggleAt = 0;

  private listScroller: SmoothScroller | null = null;
  private detailScroller: SmoothScroller | null = null;
  private unsubscribe: (() => void)[] = [];

  mount(container: HTMLElement): void {
    const el = document.createElement('div');
    el.className = 'widget agenda-widget';
    el.innerHTML = `
      <div class="agenda-head">
        <span class="agenda-label" data-r="label">${tr('Сегодня', 'Today')}</span>
        <button class="agenda-collapse" data-action="agenda-collapse" title="${tr('Свернуть', 'Collapse')}">${CHEVRON}</button>
      </div>
      <div class="agenda-ind" data-r="ind" hidden>
        <span class="agenda-ind-ev" data-r="indEv" hidden>
          <i class="agenda-ind-dot"></i><span class="agenda-ind-when" data-r="indWhen"></span><span class="agenda-ind-title" data-r="indTitle"></span>
        </span>
        <span class="agenda-ind-tasks" data-r="indTasks" hidden></span>
      </div>
      <div class="agenda" data-r="agenda" hidden>
        <div class="agenda-list" data-r="list">
          <div class="agenda-seg" data-r="seg" hidden>
            <button data-action="agenda-section" data-section="events">${tr('События', 'Events')}</button>
            <button data-action="agenda-section" data-section="tasks">${tr('Задачи', 'Tasks')}</button>
          </div>
          <div class="agenda-frame" data-r="listFrame">
            <div class="agenda-scroll" data-r="listScroll"></div>
          </div>
        </div>
        <div class="agenda-detail" data-r="detail" hidden>
          <button class="agenda-back" data-action="agenda-back">${BACK}<span>${tr('Назад', 'Back')}</span></button>
          <div class="agenda-frame" data-r="detailFrame">
            <div class="agenda-scroll" data-r="detailScroll"></div>
          </div>
        </div>
      </div>
    `;
    this.element = el;
    el.querySelectorAll<HTMLElement>('[data-r]').forEach((n) => (this.r[n.dataset.r!] = n));
    container.appendChild(el);

    this.listScroller = new SmoothScroller(this.r.listScroll, this.r.listFrame);
    this.detailScroller = new SmoothScroller(this.r.detailScroll, this.r.detailFrame);

    el.addEventListener('click', (e) => this.onClick(e));

    this.unsubscribe.push(
      // Click anywhere else on the desktop collapses the panel (mouse_down only arrives for desktop hits).
      DraggableWidgetManager.addGlobalClickListener((_x, _y, target) => {
        if (this.open && this.element && !(target && this.element.contains(target))) this.collapse();
      }),
      DraggableWidgetManager.addGlobalWheelListener((_x, _y, delta, target) => {
        if (!this.open || !this.element || !target || !this.element.contains(target)) return false;
        const scroll = target.closest<HTMLElement>('.agenda-scroll');
        const s = scroll === this.r.detailScroll ? this.detailScroller : this.listScroller;
        s?.wheel(delta);
        return true; // the wheel over the panel never scrolls anything beneath it
      }),
      DraggableWidgetManager.addDragEndListener((id) => {
        if (id === this.id && this.open) this.movedWhileOpen = true;
      }),
    );
    const onResize = () => {
      if (this.open) this.collapse(true);
    };
    window.addEventListener('resize', onResize);
    this.unsubscribe.push(() => window.removeEventListener('resize', onResize));

    DraggableWidgetManager.makeDraggable(this.id, el, () => ({
      x: window.innerWidth - el.offsetWidth - 48,
      y: 40,
    }));

    this.startTimer();
    this.refreshIndicator();
  }

  update(_data: unknown): void {
    // State arrives via setToday / setAgendaEnabled; nothing streams here.
  }

  /** Snapshot from the host (`today` message). */
  setToday(data: unknown): void {
    const t = data as TodayPayload | null;
    this.today = t && typeof t === 'object' ? t : null;
    this.refreshIndicator();
    if (this.open) {
      // Keep the detail only if its item still exists in the new snapshot.
      const d = this.detail;
      if (d?.kind === 'task' && !this.tasks().some((x) => x.id === d.id)) this.detail = null;
      if (d?.kind === 'event' && !this.events()[d.idx]) this.detail = null;
      this.morph({ apply: () => this.renderPanel() });
    }
  }

  /** Context mode: whether the agenda (indicator + panel) is active at all. */
  setAgendaEnabled(on: boolean): void {
    if (this.agendaEnabled === on) return;
    this.agendaEnabled = on;
    if (!on && this.open) this.collapse(true);
    this.refreshIndicator();
  }

  pause(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  resume(): void {
    this.refreshIndicator();
    this.startTimer();
  }

  destroy(): void {
    this.pause();
    this.unsubscribe.forEach((u) => u());
    this.unsubscribe = [];
    this.listScroller?.destroy();
    this.detailScroller?.destroy();
    DraggableWidgetManager.setOverlay(this.id, false);
    DraggableWidgetManager.unregister(this.id);
    this.element?.remove();
    this.element = null;
  }

  // ---------------------------------------------------------------- data helpers

  private hasAgenda(): boolean {
    const t = this.today;
    return !!t && t.enabled && this.agendaEnabled && (t.hasCalendar || t.hasWeeekToken || this.events().length > 0 || this.tasks().length > 0);
  }

  private events(): TodayEvent[] {
    return this.today?.events ?? [];
  }

  private tasks(): TodayTask[] {
    return (this.today?.tasks ?? []).filter((t) => !t.isCompleted);
  }

  // ---------------------------------------------------------------- minute timer

  private startTimer(): void {
    if (this.timer !== null) clearInterval(this.timer);
    this.timer = window.setInterval(() => this.tick(), 1000);
  }

  /** "Через N мин", the soon glow and row phases all follow the wall clock, not snapshots. */
  private tick(): void {
    const now = new Date();
    if (now.getMinutes() === this.lastMinute) return;
    this.lastMinute = now.getMinutes();
    if (!this.open || !this.detail) this.refreshIndicator(now);
    if (this.open) this.refreshPhases(now);
  }

  // ---------------------------------------------------------------- collapsed indicator

  private refreshIndicator(now = new Date()): void {
    if (!this.element) return;
    this.element.style.display = this.hasAgenda() || this.open ? '' : 'none';

    const ind = this.r.ind;
    this.r.label.textContent = this.today?.dateLabel || tr('Сегодня', 'Today');
    if (!this.hasAgenda()) {
      ind.hidden = true;
      ind.classList.remove('is-soon');
      this.element.classList.remove('has-agenda');
      return;
    }
    this.element.classList.add('has-agenda');

    const events = this.events();
    const tasks = this.tasks();
    const st = eventStatus(events, now);
    const ev = st.current ?? st.next;

    if (ev) {
      const when = st.current ? tr('сейчас', 'now') : indicatorWhen(ev, st.minutesToNext);
      this.r.indWhen.textContent = when;
      this.r.indTitle.textContent = ev.title;
      this.r.indEv.hidden = false;
    } else {
      this.r.indEv.hidden = true;
    }
    if (tasks.length) {
      this.r.indTasks.textContent = tasksCountLabel(tasks.length);
      this.r.indTasks.hidden = false;
    } else {
      this.r.indTasks.hidden = true;
    }
    ind.hidden = !ev && !tasks.length;
    ind.classList.toggle('is-soon', !!ev && st.soon);
  }

  // ---------------------------------------------------------------- expand / collapse

  private onClick(e: MouseEvent): void {
    const target = e.target as Element | null;
    const actionEl = target?.closest<HTMLElement>('[data-action]');
    if (!actionEl) {
      // Plain click on the collapsed card toggles; blank panel space does nothing.
      if (!this.open && target?.closest('.agenda-head, .agenda-ind')) this.toggle();
      return;
    }
    const a = actionEl.dataset.action;
    switch (a) {
      case 'agenda-collapse':
        this.toggle();
        break;
      case 'agenda-section': {
        const s = actionEl.dataset.section as Section | undefined;
        if (s && s !== this.section) {
          this.section = s;
          this.showAllTasks = false;
          this.morph({ apply: () => this.renderList() });
        }
        break;
      }
      case 'agenda-more':
        this.showAllTasks = true;
        this.morph({ apply: () => this.renderList(true) });
        break;
      case 'agenda-task':
        if (actionEl.dataset.id) this.openDetail({ kind: 'task', id: actionEl.dataset.id });
        break;
      case 'agenda-event': {
        const idx = Number(actionEl.dataset.idx);
        if (Number.isInteger(idx)) this.openDetail({ kind: 'event', idx });
        break;
      }
      case 'agenda-back':
        this.openDetail(null);
        break;
      case 'agenda-open':
        this.openInWeeek(actionEl.dataset.id || '');
        break;
    }
  }

  /** Expand / collapse with a short debounce so a doubled synthetic click doesn't immediately undo itself. */
  private toggle(): void {
    const now = performance.now();
    if (now - this.lastToggleAt < MORPH_MS) return;
    this.lastToggleAt = now;
    if (this.open) this.collapse();
    else if (this.hasAgenda()) this.expand();
  }

  private expand(): void {
    const el = this.element;
    if (!el || this.open || this.morphToken || DraggableWidgetManager.isDragging(this.id)) return;
    this.open = true;
    this.movedWhileOpen = false;
    this.detail = null;
    this.showAllTasks = false;
    const st = eventStatus(this.events(), new Date());
    this.section = st.current || st.next ? 'events' : this.tasks().length ? 'tasks' : 'events';

    const x = parseFloat(el.style.left) || el.offsetLeft;
    const y = parseFloat(el.style.top) || el.offsetTop;
    const anchor = { x, y, w: el.offsetWidth, h: el.offsetHeight };
    this.anchor = anchor;
    DraggableWidgetManager.setOverlay(this.id, true);

    this.morph({
      apply: () => {
        el.classList.add('is-open');
        this.r.agenda.hidden = false;
        this.r.list.classList.remove('is-out', 'is-back');
        this.r.detail.classList.remove('is-in', 'is-out');
        this.renderPanel();
      },
      place: (w, h) => this.fitOnScreen(anchor, w, h),
    });
  }

  private collapse(instant = false): void {
    const el = this.element;
    if (!el || !this.open) return;
    this.open = false;
    this.detail = null;
    this.showAllTasks = false;

    const restore = this.anchor && !this.movedWhileOpen ? { x: this.anchor.x, y: this.anchor.y } : null;
    const finish = () => {
      this.r.agenda.hidden = true;
      this.listScroller?.reset();
      this.detailScroller?.reset();
      this.refreshIndicator();
      requestAnimationFrame(() => {
        DraggableWidgetManager.setOverlay(this.id, false);
        // Dragged while open: the drop position is the new home — let the usual placement run.
        if (this.movedWhileOpen) DraggableWidgetManager.refresh(this.id);
      });
    };
    this.morph({
      // Measure the collapsed size with the panel out of flow, but keep it in the DOM during
      // the shrink so the content fades instead of vanishing.
      apply: () => {
        el.classList.remove('is-open');
        this.r.agenda.hidden = true;
      },
      afterMeasure: () => {
        this.r.agenda.hidden = false;
      },
      place: restore ? () => restore : undefined,
      done: finish,
      instant,
    });
  }

  /**
   * Keeps the expanded panel on screen. Grows from the widget's top-left; if that overflows,
   * aligns to the widget's right / bottom edge instead, then clamps to the screen.
   */
  private fitOnScreen(a: { x: number; y: number; w: number; h: number }, w1: number, h1: number): { x: number; y: number } {
    const W = window.innerWidth;
    const H = window.innerHeight;
    let nx = a.x;
    let ny = a.y;
    if (nx + w1 > W - EDGE) nx = a.x + a.w - w1;
    if (ny + h1 > H - EDGE) ny = a.y + a.h - h1;
    nx = Math.max(EDGE, Math.min(nx, W - EDGE - w1));
    ny = Math.max(EDGE, Math.min(ny, H - EDGE - h1));
    return { x: Math.round(nx), y: Math.round(ny) };
  }

  /**
   * FLIP for size: measures before and after `apply()`, then animates width / height (and
   * optionally left / top) between the two. Content fades on its own via CSS.
   * A newer morph supersedes a pending one (its finaliser is skipped), so rapid clicks stay smooth.
   */
  private morph(o: {
    apply: () => void;
    afterMeasure?: () => void;
    place?: (w: number, h: number) => { x: number; y: number };
    done?: () => void;
    instant?: boolean;
  }): void {
    const el = this.element;
    if (!el) return;
    const token = ++this.morphToken;

    const w0 = el.offsetWidth;
    const h0 = el.offsetHeight;
    el.classList.remove('is-morph');
    el.style.width = '';
    el.style.height = '';
    o.apply();
    this.layout();
    const w1 = el.offsetWidth;
    const h1 = el.offsetHeight;
    const target = o.place ? o.place(w1, h1) : null;
    o.afterMeasure?.();

    const finish = () => {
      if (token !== this.morphToken) return; // superseded
      this.morphToken = 0;
      el.classList.remove('is-morph');
      el.style.width = '';
      el.style.height = '';
      this.layout();
      o.done?.();
    };

    const skipAnim = o.instant || reducedMotion() || !el.isConnected || el.style.display === 'none';
    if (skipAnim || (w0 === w1 && h0 === h1 && !target)) {
      if (target) {
        el.style.left = `${target.x}px`;
        el.style.top = `${target.y}px`;
      }
      finish();
      return;
    }

    el.style.width = `${w0}px`;
    el.style.height = `${h0}px`;
    void el.offsetWidth; // flush so the transition starts from the old size
    el.classList.add('is-morph');
    el.style.width = `${w1}px`;
    el.style.height = `${h1}px`;
    if (target) {
      el.style.left = `${target.x}px`;
      el.style.top = `${target.y}px`;
    }
    window.setTimeout(finish, MORPH_MS + 20);
  }

  /**
   * Caps the visible scroll area so the whole panel stays within MAX_PANEL_VH of the screen.
   * Everything but the scroll body ("chrome": paddings, header, segmented control, back
   * button) is measured, not assumed. Must run with no inline height on the widget.
   */
  private layout(): void {
    const el = this.element;
    if (!el || !this.open) return;
    const scroll = this.detail ? this.r.detailScroll : this.r.listScroll;
    const scroller = this.detail ? this.detailScroller : this.listScroller;
    scroll.style.maxHeight = 'none';
    const chrome = el.offsetHeight - scroll.offsetHeight;
    const cap = Math.max(MIN_SCROLL_H, Math.floor(window.innerHeight * MAX_PANEL_VH) - chrome);
    scroll.style.maxHeight = `${cap}px`;
    scroller?.sync();
  }

  // ---------------------------------------------------------------- panel rendering

  private renderPanel(keepScroll = false): void {
    if (this.detail) {
      this.r.list.hidden = true;
      this.r.detail.hidden = false;
      this.renderDetail();
    } else {
      this.r.detail.hidden = true;
      this.r.list.hidden = false;
      this.renderList(keepScroll);
    }
  }

  private renderList(keepScroll = false): void {
    const t = this.today;
    const events = this.events();
    const tasks = this.tasks();
    const now = new Date();

    const stacked = events.length <= FEW_EVENTS && tasks.length <= FEW_TASKS;
    this.r.seg.hidden = stacked;
    if (!stacked) {
      this.r.seg.querySelectorAll<HTMLElement>('button').forEach((b) => b.classList.toggle('is-active', b.dataset.section === this.section));
    }

    const eventsHtml = this.eventsHtml(events, now, t);
    const tasksHtml = this.tasksHtml(tasks, t, stacked);

    let html = '';
    if (stacked) {
      html = `<div class="agenda-sec"><div class="agenda-sec-label">${tr('События', 'Events')}</div>${eventsHtml}</div>` +
        `<div class="agenda-sec"><div class="agenda-sec-label">${tr('Задачи', 'Tasks')}</div>${tasksHtml}</div>`;
    } else {
      html = `<div class="agenda-sec">${this.section === 'events' ? eventsHtml : tasksHtml}</div>`;
    }
    this.r.listScroll.innerHTML = html;
    if (keepScroll) this.listScroller?.sync();
    else this.listScroller?.reset();
  }

  private eventsHtml(events: TodayEvent[], now: Date, t: TodayPayload | null): string {
    if (!events.length) {
      const msg = t?.calendarError || (t?.hasCalendar ? tr('Событий нет', 'No events') : tr('Календарь не подключён', 'Calendar not connected'));
      return `<div class="agenda-empty">${esc(msg)}</div>`;
    }
    const allDay = events.map((e, i) => ({ e, i })).filter((x) => x.e.allDay);
    const timed = events.map((e, i) => ({ e, i })).filter((x) => !x.e.allDay);
    let html = '';
    if (allDay.length) {
      html += `<div class="agenda-chips">${allDay
        .map(({ e, i }) => {
          const { day } = splitTimeLabel(e);
          const dayTag = e.isToday === false && day ? `<em>${esc(day)}</em>` : '';
          return `<button class="agenda-chip" data-action="agenda-event" data-idx="${i}"><i></i>${tr('весь день', 'all day')}${dayTag}<span>${esc(e.title)}</span></button>`;
        })
        .join('')}</div>`;
    }
    html += `<ul class="agenda-rows">${timed
      .map(({ e, i }) => {
        const phase = eventPhase(e, now);
        const { time, day } = splitTimeLabel(e);
        return `<li class="agenda-row agenda-ev is-${phase}" data-action="agenda-event" data-idx="${i}" data-phase="${phase}">
          <span class="agenda-time">${esc(time)}</span>
          <span class="agenda-title">${esc(e.title)}</span>
          <span class="agenda-tag">${esc(day)}</span>
        </li>`;
      })
      .join('')}</ul>`;
    return html;
  }

  private tasksHtml(tasks: TodayTask[], t: TodayPayload | null, stacked: boolean): string {
    if (!tasks.length) {
      const msg = t?.tasksError || (t?.hasWeeekToken ? tr('Задач нет', 'No tasks') : tr('WEEEK не подключён', 'WEEEK not connected'));
      return `<div class="agenda-empty">${esc(msg)}</div>`;
    }
    const limit = this.showAllTasks ? Infinity : TASKS_FOLD;
    const groups = groupTasks(tasks);
    // Under the stacked "Задачи" heading a lone column header would just repeat itself.
    const showGroups = !(stacked && groups.length === 1);
    let shown = 0;
    let html = '';
    for (const g of groups) {
      if (shown >= limit) break;
      html += `${showGroups ? `<div class="agenda-group">${esc(g.column)}</div>` : ''}<ul class="agenda-rows">`;
      for (const task of g.tasks) {
        if (shown >= limit) break;
        shown++;
        html += `<li class="agenda-row agenda-task ${priorityClass(task.priority)}" data-action="agenda-task" data-id="${esc(task.id)}" title="${esc(task.title)}">
          <i class="agenda-prio"></i>
          <span class="agenda-task-body">
            <span class="agenda-title agenda-clamp">${esc(task.title)}</span>
            ${task.board ? `<span class="agenda-board">${esc(task.board)}</span>` : ''}
          </span>
          ${task.dueLabel ? `<span class="agenda-due">${esc(task.dueLabel)}</span>` : ''}
        </li>`;
      }
      html += '</ul>';
    }
    const rest = tasks.length - shown;
    if (rest > 0) html += `<button class="agenda-more" data-action="agenda-more">${tr('ещё', 'more')} ${rest}</button>`;
    return html;
  }

  /** Re-tint past / current rows without rebuilding the list. */
  private refreshPhases(now: Date): void {
    const events = this.events();
    this.r.listScroll.querySelectorAll<HTMLElement>('.agenda-ev').forEach((row) => {
      const e = events[Number(row.dataset.idx)];
      if (!e) return;
      const phase = eventPhase(e, now);
      if (row.dataset.phase === phase) return;
      row.classList.remove(`is-${row.dataset.phase}`);
      row.classList.add(`is-${phase}`);
      row.dataset.phase = phase;
    });
  }

  // ---------------------------------------------------------------- detail

  /** Swaps list ↔ detail: the outgoing view fades briefly, the incoming one slides in (CSS keyframes). */
  private openDetail(d: Detail): void {
    if (!this.open) return;
    this.detail = d;
    const token = ++this.detailToken;
    const list = this.r.list;
    const detail = this.r.detail;
    const outgoing = d ? list : detail;
    const incomingClass = d ? 'is-in' : 'is-back';
    const incoming = d ? detail : list;

    const swap = () => {
      if (token !== this.detailToken || !this.open) return;
      outgoing.classList.remove('is-out');
      this.morph({
        apply: () => {
          this.renderPanel(!d); // coming back: keep the list where the user left it
          incoming.classList.add(incomingClass);
        },
      });
      window.setTimeout(() => incoming.classList.remove(incomingClass), DETAIL_MS + 40);
    };

    if (reducedMotion()) {
      swap();
      return;
    }
    outgoing.classList.add('is-out');
    window.setTimeout(swap, 90);
  }

  private renderDetail(): void {
    const d = this.detail;
    let html = '';
    if (d?.kind === 'task') {
      const task = this.tasks().find((x) => x.id === d.id);
      if (task) {
        const chips = [task.board, task.column].filter(Boolean).map((c) => `<span class="agenda-dchip">${esc(c!)}</span>`).join('');
        html = `
          <div class="agenda-dtitle">${esc(task.title)}</div>
          <div class="agenda-dchips">${chips}</div>
          <dl class="agenda-dl">
            <dt>${tr('Приоритет', 'Priority')}</dt><dd><i class="agenda-prio ${priorityClass(task.priority)}"></i>${esc(priorityName(task.priority))}</dd>
            ${task.dueLabel ? `<dt>${tr('Срок', 'Due')}</dt><dd>${esc(task.dueLabel)}</dd>` : ''}
          </dl>
          <button class="agenda-btn" data-action="agenda-open" data-id="${esc(task.id)}">${EXT}<span>${tr('Открыть в WEEEK', 'Open in WEEEK')}</span></button>`;
      }
    } else if (d?.kind === 'event') {
      const e = this.events()[d.idx];
      if (e) {
        const start = parseLocal(e.startLocal);
        const { day } = splitTimeLabel(e);
        html = `
          <div class="agenda-dtitle">${esc(e.title)}</div>
          <div class="agenda-dchips">${e.allDay ? `<span class="agenda-dchip">${tr('весь день', 'all day')}</span>` : ''}${day ? `<span class="agenda-dchip">${esc(day)}</span>` : ''}</div>
          <dl class="agenda-dl">
            <dt>${tr('Время', 'Time')}</dt><dd>${esc(eventTimeRange(e))}</dd>
            ${start ? `<dt>${tr('Дата', 'Date')}</dt><dd class="agenda-cap">${esc(fmtDateLong(start))}</dd>` : ''}
          </dl>`;
      }
    }
    this.r.detailScroll.innerHTML = html || `<div class="agenda-empty">${tr('Элемент больше не доступен', 'Item is no longer available')}</div>`;
    this.detailScroller?.reset();
  }

  private openInWeeek(id: string): void {
    if (!id) return;
    const now = performance.now();
    if (now - this.lastOpenAt < OPEN_COOLDOWN_MS) return;
    // Only ids from the latest snapshot leave the page; the host validates again and builds the URL.
    if (!this.tasks().some((t) => t.id === id)) return;
    this.lastOpenAt = now;
    window.chrome?.webview?.postMessage({ type: 'open_task', id });
  }
}
