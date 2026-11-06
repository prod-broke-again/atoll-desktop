/**
 * Pure helpers for the agenda that lives inside the clock widget:
 * nearest-event maths, task grouping, labels. No DOM here.
 */
import type { TodayEvent, TodayTask, TodayMessage } from '../types/ipc';
import { isEn, t } from '../i18n';

export type TodayPayload = TodayMessage['data'];

export const SOON_MINUTES = 15;

/** Column headers in the order the host sorts them; anything else follows in host order. */
const COLUMN_ORDER = ['в работе', 'на проверку', 'к работе', 'in progress', 'in review', 'to do'];

export const esc = (s: string): string =>
  (s || '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);

export function pad2(n: number): string {
  return n.toString().padStart(2, '0');
}

export function fmtHm(d: Date): string {
  return `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}

export function parseLocal(s: string | null | undefined): Date | null {
  if (!s) return null;
  const d = new Date(s);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Russian plural: (1, 'задача', 'задачи', 'задач') */
export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

export function tasksCountLabel(n: number): string {
  if (isEn()) return `${n} ${n === 1 ? 'task' : 'tasks'}`;
  return `${n} ${plural(n, 'задача', 'задачи', 'задач')}`;
}

export function minutesLabel(min: number): string {
  if (isEn()) {
    if (min < 60) return `in ${min} min`;
    const h = Math.floor(min / 60);
    const m = min % 60;
    return m ? `in ${h}h ${m}m` : `in ${h}h`;
  }
  if (min < 60) return `через ${min} мин`;
  const h = Math.floor(min / 60);
  const m = min % 60;
  return m ? `через ${h} ч ${m} мин` : `через ${h} ч`;
}

export interface EventStatus {
  /** Event running right now (timed, today). */
  current: TodayEvent | null;
  /** Next timed event that has not started yet (today or later). */
  next: TodayEvent | null;
  /** Whole minutes until `next` starts. */
  minutesToNext: number | null;
  /** Something is happening now or within SOON_MINUTES. */
  soon: boolean;
}

export function eventStatus(events: TodayEvent[], now: Date): EventStatus {
  let current: TodayEvent | null = null;
  let next: TodayEvent | null = null;
  let nextStart = Infinity;
  const t = now.getTime();

  for (const e of events) {
    if (e.allDay) continue;
    const start = parseLocal(e.startLocal);
    if (!start) continue;
    const end = parseLocal(e.endLocal) ?? new Date(start.getTime() + 30 * 60_000);
    if (start.getTime() <= t && end.getTime() > t) {
      if (!current) current = e;
      continue;
    }
    if (start.getTime() > t && start.getTime() < nextStart) {
      nextStart = start.getTime();
      next = e;
    }
  }

  const minutesToNext = next ? Math.max(0, Math.round((nextStart - t) / 60_000)) : null;
  const soon = !!current || (minutesToNext !== null && minutesToNext < SOON_MINUTES);
  return { current, next, minutesToNext, soon };
}

export type EventPhase = 'past' | 'now' | 'future';

export function eventPhase(e: TodayEvent, now: Date): EventPhase {
  if (e.allDay) return 'future';
  const start = parseLocal(e.startLocal);
  if (!start) return 'future';
  const end = parseLocal(e.endLocal) ?? new Date(start.getTime() + 30 * 60_000);
  if (end.getTime() <= now.getTime()) return 'past';
  if (start.getTime() <= now.getTime()) return 'now';
  return 'future';
}

/**
 * The host puts the day into timeLabel for non-today events ("завтра · 11:00", "чт 30 окт · 10:00").
 * Split it so the day can be rendered as a tag and the time stays tabular.
 */
export function splitTimeLabel(e: TodayEvent): { time: string; day: string } {
  const label = e.timeLabel || '';
  if (e.isToday === false) {
    const i = label.indexOf(' · ');
    if (i > 0) return { time: label.slice(i + 3), day: label.slice(0, i) };
    return { time: label, day: '' };
  }
  return { time: e.allDay ? t('весь день', 'all day') : label, day: t('сегодня', 'today') };
}

/** Compact "when" for the collapsed indicator: "через 25 мин" / "14:00" / "завтра 11:00". */
export function indicatorWhen(e: TodayEvent, minutesTo: number | null): string {
  if (e.isToday !== false && minutesTo !== null && minutesTo < 60) return minutesLabel(minutesTo);
  const { time, day } = splitTimeLabel(e);
  const start = time.split('–')[0];
  return e.isToday === false && day ? `${day} ${start}` : start;
}

export interface TaskGroup {
  column: string;
  tasks: TodayTask[];
}

/** Groups tasks by column, keeping the host's order (В работе → На проверку → К работе → rest). */
export function groupTasks(tasks: TodayTask[]): TaskGroup[] {
  const groups: TaskGroup[] = [];
  const byName = new Map<string, TaskGroup>();
  for (const t of tasks) {
    if (t.isCompleted) continue;
    const name = (t.column || '').trim() || (isEn() ? 'No Column' : 'Без колонки');
    let g = byName.get(name.toLowerCase());
    if (!g) {
      g = { column: name, tasks: [] };
      byName.set(name.toLowerCase(), g);
      groups.push(g);
    }
    g.tasks.push(t);
  }
  const rank = (name: string) => {
    const l = name.toLowerCase();
    const i = COLUMN_ORDER.findIndex((c) => l.includes(c));
    return i < 0 ? COLUMN_ORDER.length : i;
  };
  // Stable sort: known columns first in canonical order, unknown ones keep host order.
  return groups
    .map((g, i) => ({ g, i }))
    .sort((a, b) => rank(a.g.column) - rank(b.g.column) || a.i - b.i)
    .map((x) => x.g);
}

/** WEEEK API: 0 Low, 1 Medium, 2 High, 3 Hold. */
export function priorityName(p: number | null | undefined): string {
  if (isEn()) {
    switch (p) {
      case 3: return 'On Hold';
      case 2: return 'High';
      case 1: return 'Medium';
      case 0: return 'Low';
      default: return 'No Priority';
    }
  }
  switch (p) {
    case 3: return 'Заморожен';
    case 2: return 'Высокий';
    case 1: return 'Средний';
    case 0: return 'Низкий';
    default: return 'Без приоритета';
  }
}

export function priorityClass(p: number | null | undefined): string {
  switch (p) {
    case 3: return 'p-hold';
    case 2: return 'p-high';
    case 1: return 'p-mid';
    case 0: return 'p-low';
    default: return 'p-none';
  }
}

export function fmtDateLong(d: Date): string {
  return d.toLocaleDateString(isEn() ? 'en-US' : 'ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });
}

export function eventTimeRange(e: TodayEvent): string {
  const start = parseLocal(e.startLocal);
  const end = parseLocal(e.endLocal);
  if (e.allDay || !start) return t('Весь день', 'All day');
  if (!end || end <= start) return fmtHm(start);
  if (end.toDateString() === start.toDateString()) return `${fmtHm(start)} – ${fmtHm(end)}`;
  return `${fmtHm(start)} – ${fmtDateLong(end)}, ${fmtHm(end)}`;
}
