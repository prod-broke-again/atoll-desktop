/**
 * Dev mock for browser testing without the C# host.
 * Only included in Vite development mode (`vite dev`).
 */

import type { MetricPoint, ServerStateMessage, TodayMessage, TodayEvent, TodayTask, TelegramMessage } from './types/ipc';

type MessageHandler = (e: { data: unknown }) => void;

const params = new URLSearchParams(location.search);
const isEn = params.get('lang') === 'en';
const mockScenario = params.get('mock') || 'full';
const mockLayout = params.get('layout') || 'fan';

function atToday(h: number, m: number, dayOffset = 0): Date {
  const d = new Date();
  d.setDate(d.getDate() + dayOffset);
  d.setHours(h, m, 0, 0);
  return d;
}

function localIso(d: Date): string {
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}:00`;
}

function hm(d: Date): string {
  return `${d.getHours().toString().padStart(2, '0')}:${d.getMinutes().toString().padStart(2, '0')}`;
}

function mockEvent(id: string, title: string, start: Date, end: Date | null, allDay = false, en = false): TodayEvent {
  const today = start.toDateString() === new Date().toDateString();
  const tomorrow = start.toDateString() === atToday(0, 0, 1).toDateString();
  const dayPrefix = today
    ? ''
    : tomorrow
      ? en ? 'tomorrow · ' : 'завтра · '
      : en
        ? `${start.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })} · `
        : `${start.toLocaleDateString('ru-RU', { weekday: 'short', day: 'numeric', month: 'short' }).replace(/\./g, '')} · `;
  const time = allDay ? (en ? 'all day' : 'весь день') : end ? `${hm(start)}–${hm(end)}` : hm(start);
  return {
    id,
    title,
    startLocal: localIso(start),
    endLocal: end ? localIso(end) : null,
    allDay,
    timeLabel: dayPrefix + time,
    isToday: today,
  };
}

function mockTask(id: string, title: string, priority: number | null, board: string, column: string, dueLabel: string | null = null): TodayTask {
  return { id, title, priority, isCompleted: false, dueLabel, board, column };
}

function buildMockToday(scenario: string, en = false): TodayMessage['data'] {
  const dateLabel = new Date().toLocaleDateString(en ? 'en-US' : 'ru-RU', {
    day: 'numeric',
    month: 'long',
    weekday: 'long',
  });
  if (scenario === 'empty') {
    return { enabled: true, dateLabel, events: [], tasks: [], hasWeeekToken: false, hasCalendar: false };
  }

  const now = new Date();
  const events: TodayEvent[] = en
    ? [
        mockEvent('e1', 'Architecture Focus Session', atToday(10, 0), atToday(12, 30), false, true),
        mockEvent('e2', 'Core Team Daily Sync', atToday(13, 0), atToday(13, 30), false, true),
        mockEvent('e3', 'Code Review & Refactoring', atToday(18, 30), atToday(19, 0), false, true),
        mockEvent('e4', 'TechConf 2026 Keynote', atToday(0, 0), atToday(0, 0, 1), true, true),
        mockEvent('e5', 'Sprint Planning & Grooming', atToday(11, 0, 1), atToday(11, 45, 1), false, true),
        mockEvent('e6', 'Production Release v2.0', atToday(15, 0, 2), atToday(16, 0, 2), false, true),
        mockEvent('e7', 'Feature Demo & Walkthrough', atToday(12, 0, 4), atToday(13, 0, 4), false, true),
      ]
    : [
        mockEvent('e1', 'Фокус-сессия: Архитектура', atToday(10, 0), atToday(12, 30)),
        mockEvent('e2', 'Командный дейли-митап', atToday(13, 0), atToday(13, 30)),
        mockEvent('e3', 'Код-ревью и рефакторинг', atToday(18, 30), atToday(19, 0)),
        mockEvent('e4', 'Техническая конференция TechConf', atToday(0, 0), atToday(0, 0, 1), true),
        mockEvent('e5', 'Планёрка по спринту', atToday(11, 0, 1), atToday(11, 45, 1)),
        mockEvent('e6', 'Релиз v2.0 на продакшен', atToday(15, 0, 2), atToday(16, 0, 2)),
        mockEvent('e7', 'Демонстрация функционала', atToday(12, 0, 4), atToday(13, 0, 4)),
      ];

  if (scenario === 'soon') {
    const s = new Date(now.getTime() + 10 * 60_000);
    events.unshift(mockEvent('e0', en ? 'Design Sync' : 'Созвон по макетам', s, new Date(s.getTime() + 30 * 60_000), false, en));
  } else if (scenario === 'now') {
    const s = new Date(now.getTime() - 12 * 60_000);
    events.unshift(mockEvent('e0', en ? 'Standup Meeting' : 'Планёрка', s, new Date(s.getTime() + 40 * 60_000), false, en));
  }

  const tasks: TodayTask[] = en
    ? [
        mockTask('101', 'Optimize asset pipeline: implement WebP and HTTP caching', 2, 'Frontend', 'In Progress'),
        mockTask('102', 'Extract catalog filter bar into separate component', 1, 'UI Kit', 'In Progress'),
        mockTask('103', 'Analytics XLSX export with custom column picker', 1, 'Backend', 'In Progress', 'Oct 30'),
        mockTask('104', 'Set up OAuth 2.0 authorization flow via GitHub', 3, 'Security', 'In Progress'),
        mockTask('105', 'Cross-browser responsive QA pass in Safari', 0, 'QA', 'In Review'),
        mockTask('106', 'Update API reference docs and SDK usage examples', null, 'Docs', 'In Review'),
        mockTask('107', 'Review and approve Terms of Service updates', 2, 'Legal', 'In Review'),
        mockTask('108', 'Migrate media library to S3 storage bucket', 1, 'Infra', 'To Do'),
        mockTask('109', 'Add end-to-end integration tests for auth flows', 0, 'Tests', 'To Do'),
        mockTask('110', 'Refine dark mode palette for high-contrast OLED displays', 0, 'Design', 'To Do'),
        mockTask('111', 'Upgrade framework dependencies to latest LTS release', 1, 'Release', 'To Do'),
        mockTask('112', 'Compress vector assets and optimize SVG sprites', null, 'Perf', 'To Do'),
        mockTask('113', 'Design 404 error page with intelligent search suggestion', 0, 'Frontend', 'To Do'),
        mockTask('114', 'Configure high-priority alerts in Slack and Telegram', 2, 'Monitoring', 'To Do'),
      ]
    : [
        mockTask('101', 'Оптимизировать загрузку медиа-файлов: настроить кэширование и WebP', 2, 'Фронтенд', 'В работе'),
        mockTask('102', 'Вынести фильтры каталога в отдельный компонент', 1, 'UI Kit', 'В работе'),
        mockTask('103', 'Доработать выгрузку аналитики в XLSX с выбором столбцов', 1, 'Бэкенд', 'В работе', '30 окт'),
        mockTask('104', 'Настроить OAuth 2.0 авторизацию через GitHub', 3, 'Безопасность', 'В работе'),
        mockTask('105', 'Проверить адаптивную вёрстку интерфейса в Safari', 0, 'QA', 'На проверку'),
        mockTask('106', 'Обновить документацию API и примеры вызовов', null, 'Docs', 'На проверку'),
        mockTask('107', 'Согласовать формулировки пользовательского соглашения', 2, 'Юристы', 'На проверку'),
        mockTask('108', 'Перенести медиатеку в S3 хранилище', 1, 'Инфраструктура', 'К работе'),
        mockTask('109', 'Добавить автотесты для сценариев авторизации', 0, 'Тесты', 'К работе'),
        mockTask('110', 'Адаптировать тёмную тему оформления для мобильных устройств', 0, 'Дизайн', 'К работе'),
        mockTask('111', 'Обновить зависимости фреймворка и провести регрессионное тестирование', 1, 'Релиз', 'К работе'),
        mockTask('112', 'Сжать векторные изображения и оптимизировать SVG-спрайты', null, 'Оптимизация', 'К работе'),
        mockTask('113', 'Страница 404 с поиском по разделам', 0, 'Фронтенд', 'К работе'),
        mockTask('114', 'Настроить отправку критических алертов в Slack/Telegram', 2, 'Мониторинг', 'К работе'),
      ];

  if (scenario === 'few') {
    return { enabled: true, dateLabel, events: events.slice(1, 3), tasks: tasks.slice(0, 3), hasWeeekToken: true, hasCalendar: true };
  }
  return { enabled: true, dateLabel, events, tasks, hasWeeekToken: true, hasCalendar: true };
}

function buildMockTelegram(scenario: string, en = false): TelegramMessage['data'] {
  if (scenario === 'empty' || scenario === 'tg-off') {
    return { enabled: true, available: false, chats: [] };
  }

  const tracked: TelegramMessage['data']['chats'] = en
    ? [
        { id: 101, name: 'DevOps & Alerts', title: 'Infra Ops', unread: 14, username: null, chatType: 'supergroup' },
        { id: 102, name: 'Alex Smith', unread: 2, username: 'alex_dev', chatType: 'user' },
        { id: 103, name: 'Core Dev Team', title: 'Engineering', unread: 5, username: null, chatType: 'group' },
        { id: 104, name: 'Design System', title: 'UI / UX', unread: 0, username: 'design_team', chatType: 'supergroup' },
      ]
    : [
        { id: 101, name: 'DevOps & Мониторинг', title: 'Инфраструктура', unread: 14, username: null, chatType: 'supergroup' },
        { id: 102, name: 'Алексей Смирнов', unread: 2, username: 'alex_dev', chatType: 'user' },
        { id: 103, name: 'Команда Проекта', title: 'Основной чат', unread: 5, username: null, chatType: 'group' },
        { id: 104, name: 'Дизайн-система', title: 'UI / UX', unread: 0, username: 'design_team', chatType: 'supergroup' },
      ];

  if (scenario === 'tg-many') {
    for (let i = 0; i < 8; i++) {
      tracked.push({
        id: 200 + i,
        name: en ? `Project Channel ${i + 1}` : `Рабочий чат ${i + 1}`,
        unread: i % 3 === 0 ? i + 1 : 0,
        username: i % 2 === 0 ? `devchat${i}` : null,
        chatType: 'group',
      });
    }
  }

  return { enabled: true, available: true, chats: tracked };
}

const handlers: MessageHandler[] = [];

function emit(data: unknown): void {
  for (const h of handlers) {
    h({ data });
  }
}

function generateMockHistory(count: number, baseCpu = 22, baseRam = 44): MetricPoint[] {
  const points: MetricPoint[] = [];
  const now = Math.floor(Date.now() / 1000);
  for (let i = count - 1; i >= 0; i--) {
    const t = now - i * 2;
    const cpu = Math.min(100, Math.max(4, baseCpu + Math.sin(i * 0.25) * 14 + (Math.random() * 8 - 4)));
    const ram = Math.min(100, Math.max(10, baseRam + Math.cos(i * 0.1) * 3));
    points.push({
      t,
      cpu: Math.round(cpu * 10) / 10,
      mem: Math.round(ram * 10) / 10,
      swap: 2.1,
      load1: 0.42,
      diskPct: 35.5,
      rxKbps: Math.round(40 + Math.random() * 80),
      txKbps: Math.round(20 + Math.random() * 50),
    });
  }
  return points;
}

let metricsTimer: ReturnType<typeof setInterval> | null = null;
let currentPrivacyMode = false;

function buildMockState(scenario: 'good' | 'warning' | 'critical', en = false): ServerStateMessage {
  if (scenario === 'warning') {
    return {
      type: 'serverState',
      host: 'api.example.com',
      ssh: 'live',
      server: {
        status: 'on',
        uptimeSec: 2400000,
        diskPct: 66.4,
      },
      account: {
        balance: 140.0,
        currency: 'RUB',
        daysLeft: 4,
        autopay: false,
        staleMin: 0,
      },
      bucket: {
        usedPct: 84,
        objects: 1420,
      },
      backup: {
        ageHours: 42,
      },
      sites: [
        {
          name: 'api.example.com',
          url: 'https://api.example.com',
          state: 'slow',
          ms: 1850,
          tlsDays: 9,
        },
      ],
      alerts: en
        ? [
            { level: 'warning', text: 'Backup is stale (>36 h)' },
            { level: 'warning', text: 'api.example.com: slow (1850 ms)' },
            { level: 'warning', text: 'TLS api.example.com: 9 days' },
          ]
        : [
            { level: 'warning', text: 'Бэкап устарел (>36 ч)' },
            { level: 'warning', text: 'api.example.com: slow (1850 мс)' },
            { level: 'warning', text: 'TLS api.example.com: 9 дн.' },
          ],
      history: generateMockHistory(40, 65, 78),
    };
  }

  if (scenario === 'critical') {
    return {
      type: 'serverState',
      host: 'api.example.com',
      ssh: 'offline',
      server: {
        status: 'off',
        uptimeSec: 0,
        diskPct: 78.5,
      },
      account: {
        balance: 12.0,
        currency: 'RUB',
        daysLeft: 0,
        autopay: false,
        staleMin: 45,
      },
      bucket: {
        usedPct: 96,
        objects: 2100,
      },
      backup: {
        ageHours: 96,
      },
      sites: [
        {
          name: 'api.example.com',
          url: 'https://api.example.com',
          state: 'down',
          ms: null,
          tlsDays: 0,
        },
      ],
      alerts: en
        ? [
            { level: 'critical', text: 'SSH offline > 2 min' },
            { level: 'critical', text: 'api.example.com: down (2 consecutive)' },
            { level: 'critical', text: 'Server stopped (status: off)' },
          ]
        : [
            { level: 'critical', text: 'SSH offline > 2 мин' },
            { level: 'critical', text: 'api.example.com: down (2 подряд)' },
            { level: 'critical', text: 'Сервер выключен (status: off)' },
          ],
      history: generateMockHistory(20, 10, 30),
    };
  }

  // Good / healthy scenario
  return {
    type: 'serverState',
    host: 'api.example.com',
    ssh: 'live',
    server: {
      status: 'on',
      uptimeSec: 1209600,
      diskPct: 35.5,
    },
    account: {
      balance: 1450.5,
      currency: 'RUB',
      daysLeft: 48,
      autopay: true,
      staleMin: 0,
    },
    bucket: {
      usedPct: 33,
      objects: 840,
    },
    backup: {
      ageHours: 14,
    },
    sites: [
      {
        name: 'api.example.com',
        url: 'https://api.example.com',
        state: 'up',
        ms: 142,
        tlsDays: 68,
      },
    ],
    alerts: [],
    history: generateMockHistory(40, 22, 44),
  };
}

// Crisp inline vector icons that never fail to load
function svgUri(svg: string): string {
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
}

const ICON_CHROME = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><circle cx="32" cy="32" r="30" fill="#ffffff"/><circle cx="32" cy="32" r="28" fill="#1a73e8"/><circle cx="32" cy="32" r="14" fill="#ffffff"/><circle cx="32" cy="32" r="11" fill="#1a73e8"/><path d="M32 18h24.2A28 28 0 0 0 12.3 12L24.4 33z" fill="#ea4335"/><path d="M32 46l12.1-21h-24.2a28 28 0 0 0 24.2 35z" fill="#fbbc04"/><path d="M32 46l12.1-21A28 28 0 0 0 4 32a28 28 0 0 0 8.3 20z" fill="#34a853"/></svg>');
const ICON_TERMINAL = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#1e1e2e"/><path d="M16 22l12 10-12 10M32 42h16" stroke="#58b6f6" stroke-width="4.5" stroke-linecap="round" stroke-linejoin="round" fill="none"/></svg>');
const ICON_VSCODE = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0066b8"/><path d="M46 12l-14 13-14-11-6 3v34l6 3 14-11 14 13 6-3V15z" fill="#007acc"/><path d="M46 12L32 25l14 14 6-3V15z" fill="#1f9cf0"/><path d="M18 14L12 17v30l6 3 14-11L18 26z" fill="#0065a9"/></svg>');
const ICON_ANDROID = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#2d3748"/><path d="M22 30a10 10 0 0 1 20 0v4H22z" fill="#3ddc84"/><circle cx="27" cy="28" r="1.5" fill="#2d3748"/><circle cx="37" cy="28" r="1.5" fill="#2d3748"/><path d="M25 21l-3-4M39 21l3-4" stroke="#3ddc84" stroke-width="2.5" stroke-linecap="round"/><rect x="22" y="36" width="20" height="12" rx="2" fill="#3ddc84"/><rect x="17" y="36" width="3.5" height="9" rx="1.5" fill="#3ddc84"/><rect x="43.5" y="36" width="3.5" height="9" rx="1.5" fill="#3ddc84"/></svg>');
const ICON_WEBSTORM = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0d1117"/><path d="M8 8h48v48H8z" fill="#087cfa" rx="10"/><rect x="15" y="15" width="34" height="34" rx="7" fill="#111827"/><text x="20" y="38" font-family="system-ui, sans-serif" font-weight="900" font-size="16" fill="#087cfa">WS</text></svg>');
const ICON_PHPSTORM = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0d1117"/><path d="M8 8h48v48H8z" fill="#b125ea" rx="10"/><rect x="15" y="15" width="34" height="34" rx="7" fill="#111827"/><text x="21" y="38" font-family="system-ui, sans-serif" font-weight="900" font-size="16" fill="#b125ea">PS</text></svg>');
const ICON_SERVER = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0369a1"/><rect x="14" y="16" width="36" height="8" rx="2" fill="#e0f2fe"/><rect x="14" y="28" width="36" height="8" rx="2" fill="#e0f2fe"/><rect x="14" y="40" width="36" height="8" rx="2" fill="#e0f2fe"/><circle cx="20" cy="20" r="1.5" fill="#0284c7"/><circle cx="20" cy="32" r="1.5" fill="#0284c7"/><circle cx="20" cy="44" r="1.5" fill="#0284c7"/></svg>');
const ICON_GEMINI = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#1e1b4b"/><path d="M32 14c0 9.9 8.1 18 18 18-9.9 0-18 8.1-18 18 0-9.9-8.1-18-18-18 9.9 0 18-8.1 18-18z" fill="#a855f7"/><circle cx="44" cy="20" r="3" fill="#38bdf8"/></svg>');
const ICON_CALENDAR = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#f59e0b"/><rect x="14" y="16" width="36" height="34" rx="6" fill="#ffffff"/><path d="M14 24h36v-2a6 6 0 0 0-6-6H20a6 6 0 0 0-6 6z" fill="#d97706"/><circle cx="24" cy="32" r="2.5" fill="#f59e0b"/><circle cx="32" cy="32" r="2.5" fill="#f59e0b"/><circle cx="40" cy="32" r="2.5" fill="#f59e0b"/><circle cx="24" cy="40" r="2.5" fill="#f59e0b"/><circle cx="32" cy="40" r="2.5" fill="#f59e0b"/></svg>');
const ICON_STEAM = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#171a21"/><circle cx="32" cy="32" r="20" fill="none" stroke="#66c0f4" stroke-width="3"/><path d="M22 36l12-5 8 5" stroke="#ffffff" stroke-width="3" fill="none" stroke-linecap="round"/><circle cx="42" cy="36" r="4.5" fill="#66c0f4"/><circle cx="34" cy="31" r="3" fill="#ffffff"/><circle cx="22" cy="36" r="4" fill="#66c0f4"/></svg>');
const ICON_EPIC = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#18181b"/><path d="M18 16h28l-4 30-10 4-10-4z" fill="#333333"/><text x="24" y="38" font-family="system-ui, sans-serif" font-weight="900" font-size="20" fill="#ffffff">E</text></svg>');
const ICON_GAMEPAD = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#7c3aed"/><path d="M18 24h28a10 10 0 0 1 10 10v6a6 6 0 0 1-6 6l-6-4-6 4H26l-6-4-6 4a6 6 0 0 1-6-6v-6a10 10 0 0 1 10-10z" fill="#ffffff"/><path d="M23 32v6M20 35h6" stroke="#7c3aed" stroke-width="2.5" stroke-linecap="round"/><circle cx="41" cy="33" r="2" fill="#7c3aed"/><circle cx="45" cy="37" r="2" fill="#7c3aed"/></svg>');
const ICON_SPOTIFY = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><circle cx="32" cy="32" r="30" fill="#1ed760"/><path d="M20 25c7-2 17-1.5 24 2.5M22 32c6-1.5 14-1 20 2M24 39c5-1.2 11-.8 16 1.8" stroke="#121212" stroke-width="3.5" stroke-linecap="round" fill="none"/></svg>');
const ICON_VLC = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#ea580c"/><path d="M32 14l-14 32h28z" fill="#f97316"/><path d="M24 32h16l2 5H22zM21 39h22l2 5H19z" fill="#ffffff"/><ellipse cx="32" cy="46" rx="16" ry="4" fill="#c2410c"/></svg>');
const ICON_OBS = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0f172a"/><circle cx="32" cy="32" r="18" fill="none" stroke="#ffffff" stroke-width="4"/><path d="M32 20a12 12 0 0 1 12 12M44 32a12 12 0 0 1-12 12M32 44a12 12 0 0 1-12-12" stroke="#38bdf8" stroke-width="4" stroke-linecap="round" fill="none"/></svg>');
const ICON_PHOTOSHOP = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#001e36"/><rect x="8" y="8" width="48" height="48" rx="8" fill="#001e36" stroke="#31a8ff" stroke-width="2.5"/><text x="17" y="38" font-family="system-ui, sans-serif" font-weight="bold" font-size="20" fill="#31a8ff">Ps</text></svg>');
const ICON_TOR = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#4c1d95"/><circle cx="32" cy="32" r="18" fill="none" stroke="#c084fc" stroke-width="3"/><path d="M32 14c-10 0-18 8-18 18s8 18 18 18" stroke="#e9d5ff" stroke-width="3" fill="none"/><path d="M32 20c-6.6 0-12 5.4-12 12s5.4 12 12 12" stroke="#ffffff" stroke-width="3" fill="none"/></svg>');
const ICON_QBIT = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#1e40af"/><path d="M32 16v20M24 28l8 8 8-8" stroke="#60a5fa" stroke-width="4" stroke-linecap="round" stroke-linejoin="round" fill="none"/><path d="M18 42h28" stroke="#ffffff" stroke-width="4" stroke-linecap="round"/></svg>');
const ICON_WIFI = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#059669"/><path d="M16 26a24 24 0 0 1 32 0M21 32a17 17 0 0 1 22 0M26 38a10 10 0 0 1 12 0" stroke="#ffffff" stroke-width="3.5" stroke-linecap="round" fill="none"/><circle cx="32" cy="44" r="3" fill="#ffffff"/></svg>');
const ICON_CALC = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#334155"/><rect x="18" y="16" width="28" height="10" rx="2" fill="#94a3b8"/><circle cx="23" cy="34" r="2.5" fill="#f8fafc"/><circle cx="32" cy="34" r="2.5" fill="#f8fafc"/><circle cx="41" cy="34" r="2.5" fill="#f8fafc"/><circle cx="23" cy="42" r="2.5" fill="#f8fafc"/><circle cx="32" cy="42" r="2.5" fill="#f8fafc"/><circle cx="41" cy="42" r="2.5" fill="#38bdf8"/></svg>');
const ICON_SETTINGS = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#475569"/><circle cx="32" cy="32" r="8" fill="none" stroke="#f1f5f9" stroke-width="4"/><path d="M32 14v4M32 46v4M14 32h4M46 32h4M19 19l3 3M42 42l3 3M19 45l3-3M42 22l3-3" stroke="#f1f5f9" stroke-width="4" stroke-linecap="round"/></svg>');
const ICON_BLENDER = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#ea580c"/><circle cx="32" cy="36" r="10" fill="#ffffff"/><circle cx="32" cy="36" r="6" fill="#0284c7"/><path d="M32 26V14M24 28L14 20M40 28l10-8" stroke="#ffffff" stroke-width="4" stroke-linecap="round"/></svg>');
const ICON_UNITY = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#18181b"/><path d="M32 16l14 8v16l-14 8-14-8V24z" fill="none" stroke="#ffffff" stroke-width="3.5"/><path d="M32 16v16m0 0l14 8m-14-8L18 40" stroke="#ffffff" stroke-width="3.5"/></svg>');
const ICON_UNREAL = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#0f172a"/><circle cx="32" cy="32" r="18" fill="none" stroke="#cbd5e1" stroke-width="3"/><text x="24" y="39" font-family="serif" font-weight="bold" font-size="22" fill="#ffffff">U</text></svg>');
const ICON_GODOT = svgUri('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64"><rect width="64" height="64" rx="15" fill="#478cbf"/><path d="M20 24h24a6 6 0 0 1 6 6v10a6 6 0 0 1-6 6H20a6 6 0 0 1-6-6V30a6 6 0 0 1 6-6z" fill="#ffffff"/><circle cx="26" cy="34" r="3.5" fill="#478cbf"/><circle cx="38" cy="34" r="3.5" fill="#478cbf"/><path d="M26 18l2 6M38 18l-2 6" stroke="#ffffff" stroke-width="3" stroke-linecap="round"/></svg>');

const mockPinned = [
  { id: 'pin_chrome', name: 'Google Chrome', iconUrl: ICON_CHROME },
  { id: 'pin_code', name: 'VS Code', iconUrl: ICON_VSCODE },
];

function buildMockGroups(en = false) {
  return [
    {
      id: 'grp_dev',
      name: en ? 'Development' : 'Разработка',
      items: [
        { id: 'dev_as', name: 'Android Studio', iconUrl: ICON_ANDROID },
        { id: 'dev_cursor', name: 'VS Code', iconUrl: ICON_VSCODE },
        { id: 'dev_antigravity', name: 'Terminal', iconUrl: ICON_TERMINAL },
        { id: 'dev_ws', name: 'WebStorm', iconUrl: ICON_WEBSTORM },
        { id: 'dev_ps', name: 'PhpStorm', iconUrl: ICON_PHPSTORM },
        { id: 'dev_laragon', name: 'Server', iconUrl: ICON_SERVER },
        { id: 'dev_gemini', name: 'Gemini AI', iconUrl: ICON_GEMINI },
        { id: 'dev_weeek', name: 'Calendar', iconUrl: ICON_CALENDAR },
      ],
    },
    {
      id: 'grp_games',
      name: en ? 'Gaming' : 'Игры',
      items: [
        { id: 'g_steam', name: 'Steam', iconUrl: ICON_STEAM },
        { id: 'g_epic', name: 'Epic Games', iconUrl: ICON_EPIC },
        { id: 'g_gamepad', name: 'Controller', iconUrl: ICON_GAMEPAD },
        { id: 'g_apex', name: 'Action Arena', iconUrl: ICON_GAMEPAD },
        { id: 'g_cs2', name: 'Cyber Tactics', iconUrl: ICON_STEAM },
      ],
    },
    {
      id: 'grp_media',
      name: en ? 'Media' : 'Медиа',
      items: [
        { id: 'm_spotify', name: 'Spotify', iconUrl: ICON_SPOTIFY },
        { id: 'm_vlc', name: 'VLC Player', iconUrl: ICON_VLC },
        { id: 'm_obs', name: 'OBS Studio', iconUrl: ICON_OBS },
        { id: 'm_ps', name: 'Photoshop', iconUrl: ICON_PHOTOSHOP },
      ],
    },
    {
      id: 'grp_net',
      name: en ? 'Network' : 'Сеть',
      items: [
        { id: 'net_tor', name: 'Tor Browser', iconUrl: ICON_TOR },
        { id: 'net_qbit', name: 'qBittorrent', iconUrl: ICON_QBIT },
        { id: 'net_wifi', name: 'Wi-Fi / VPN', iconUrl: ICON_WIFI },
      ],
    },
    {
      id: 'grp_system',
      name: en ? 'System' : 'Система',
      items: [
        { id: 'sys_term', name: en ? 'Terminal' : 'Терминал Windows', iconUrl: ICON_TERMINAL },
        { id: 'sys_calc', name: en ? 'Calculator' : 'Калькулятор', iconUrl: ICON_CALC },
        { id: 'sys_settings', name: en ? 'Settings' : 'Параметры', iconUrl: ICON_SETTINGS },
      ],
    },
    {
      id: 'grp_gamedev',
      name: en ? 'GameDev' : 'Геймдев',
      items: [
        { id: 'gd_blender', name: 'Blender 4.2', iconUrl: ICON_BLENDER },
        { id: 'gd_unity', name: 'Unity Hub', iconUrl: ICON_UNITY },
        { id: 'gd_unreal', name: 'Unreal Engine', iconUrl: ICON_UNREAL },
        { id: 'gd_godot', name: 'Godot Engine', iconUrl: ICON_GODOT },
      ],
    },
  ];
}

const mockCollageImages = [
  { id: 'img1', url: 'https://images.unsplash.com/photo-1506744038136-46273834b3fb?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#2a4365' },
  { id: 'img2', url: 'https://images.unsplash.com/photo-1518709268805-4e9042af9f23?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#805ad5' },
  { id: 'img3', url: 'https://images.unsplash.com/photo-1507525428034-b723cf961d3e?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#0284c7' },
  { id: 'img4', url: 'https://images.unsplash.com/photo-1513694203232-719a280e022f?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#d97706' },
  { id: 'img5', url: 'https://images.unsplash.com/photo-1519681393784-d120267933ba?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#4f46e5' },
  { id: 'img6', url: 'https://images.unsplash.com/photo-1441974231531-c6227db76b6e?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#15803d' },
  { id: 'img7', url: 'https://images.unsplash.com/photo-1509316975850-ff9c5deb0cd9?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#ea580c' },
  { id: 'img8', url: 'https://images.unsplash.com/photo-1486406146926-c627a92ad1ab?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#0ea5e9' },
  { id: 'img9', url: 'https://images.unsplash.com/photo-1495616811223-4d98c6e9c869?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#e11d48' },
  { id: 'img10', url: 'https://images.unsplash.com/photo-1515694346937-94d85e41e6f0?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#4338ca' },
  { id: 'img11', url: 'https://images.unsplash.com/photo-1470071459604-3b5ec3a7fe05?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#047857' },
  { id: 'img12', url: 'https://images.unsplash.com/photo-1492144534655-ae79c964c9d7?auto=format&fit=crop&w=1200&q=80', width: 1200, height: 800, dominantColor: '#18181b' },
];

if (!window.chrome?.webview) {
  const chromeObj = (window.chrome = window.chrome || ({} as unknown as Window['chrome']));
  chromeObj!.webview = {
    addEventListener(_event: string, handler: MessageHandler): void {
      handlers.push(handler);
    },
    postMessage(msg: unknown): void {
      console.log('[DevMock IPC Web->Host]', msg);
      const obj = msg as { type?: string; id?: string };

      if (obj?.type === 'ready') {
        setTimeout(() => {
          // 0. Mock collage (layout switchable via ?layout=fan|bento|hex|diagonal|masonry|polaroid)
          emit({
            type: 'collage',
            style: mockLayout,
            items: mockCollageImages,
            palette: ['#4f46e5', '#0284c7', '#805ad5', '#ea580c', '#15803d'],
          });

          // 1. Telemetry
          emit({
            type: 'telemetry',
            cpuPercent: 18,
            ramPercent: 44,
            ramUsedGb: 14.1,
            ramTotalGb: 32,
            gpuPercent: 12,
            netDownloadKbps: 1840,
            netUploadKbps: 320,
          });

          // 2. Weather
          emit({
            type: 'weather',
            data: {
              enabled: true,
              city: isEn ? 'San Francisco' : 'Москва',
              temperature: isEn ? 19 : 9,
              apparentTemperature: isEn ? 18 : 7,
              weatherCode: 3,
              weatherDescription: isEn ? 'Partly Cloudy' : 'Пасмурно',
              windSpeed: 4.2,
              relativeHumidity: 72,
              isDay: true,
              units: 'metric',
            },
          });

          // 3. Media
          emit({
            type: 'media',
            data: {
              hasMedia: true,
              title: 'Midnight City',
              artist: 'M83',
              album: "Hurry Up, We're Dreaming",
              isPlaying: true,
              thumbnailBase64: null,
            },
          });

          // 4. Launcher (Pinned + 6 Groups)
          emit({
            type: 'launcher',
            pinned: mockPinned,
            groups: buildMockGroups(isEn),
          });

          // 5. Server State (default healthy)
          emit(buildMockState('good', isEn));

          // 6. Today (events + WEEEK tasks) — the agenda widget
          emit({ type: 'today', data: buildMockToday(mockScenario, isEn) });

          // 7. Telegram unread (tracked chats only)
          emit({ type: 'telegram', data: buildMockTelegram(mockScenario, isEn) });

          // 8. Config
          emit({
            type: 'config',
            config: {
              performance: { fpsIdle: 30 },
              collage: {},
              widgets: {
                showClock: params.get('clockHide') !== '1',
                clockPosition: params.get('clockPos') || 'top-center',
                clockFont: params.get('clockFont') || 'default',
              },
              wallpaper: {
                youtubeUrl: params.get('yt') || '',
                soundEnabled: params.get('ytSound') === '1',
              },
            },
          });

          // Periodic server metrics (every 2s)
          if (metricsTimer) clearInterval(metricsTimer);
          let lastCpu = 22;
          let lastRam = 44;
          metricsTimer = setInterval(() => {
            lastCpu = Math.min(95, Math.max(5, lastCpu + (Math.random() * 12 - 6)));
            lastRam = Math.min(95, Math.max(20, lastRam + (Math.random() * 2 - 1)));
            emit({
              type: 'serverMetrics',
              point: {
                t: Math.floor(Date.now() / 1000),
                cpu: Math.round(lastCpu * 10) / 10,
                mem: Math.round(lastRam * 10) / 10,
                swap: 2.1,
                load1: Math.round((lastCpu / 50) * 100) / 100,
                diskPct: 35.5,
                rxKbps: Math.round(30 + Math.random() * 85),
                txKbps: Math.round(15 + Math.random() * 45),
              },
            });
          }, 2000);
        }, 80);
      } else if (obj?.type === 'launch') {
        console.info(`%c[DevMock Launched] ID: ${obj.id}`, 'color: #38bdf8; font-weight: bold;');
      } else if (obj?.type === 'open_group') {
        console.info(`%c[DevMock Open Explorer Group] ID: ${obj.id}`, 'color: #38bdf8; font-weight: bold;');
      } else if (obj?.type === 'server_open') {
        const serverMsg = msg as { target: string; name?: string };
        console.info(`%c[DevMock Server Open] Target: ${serverMsg.target} Name: ${serverMsg.name ?? ''}`, 'color: #38bdf8; font-weight: bold;');
      } else if (obj?.type === 'open_task') {
        console.info(`%c[DevMock Open WEEEK task] ID: ${obj.id}`, 'color: #38bdf8; font-weight: bold;');
      } else if (obj?.type === 'open_telegram_chat') {
        console.info(`%c[DevMock Open Telegram chat] ID: ${obj.id}`, 'color: #38bdf8; font-weight: bold;');
      }
    },
  };

  // Bridge native browser pointer events into host mouse IPC messages
  const dpr = window.devicePixelRatio || 1;

  window.addEventListener(
    'click',
    (e) => {
      if (e.isTrusted) e.stopPropagation();
    },
    true,
  );
  window.addEventListener('mousemove', (e) => {
    emit({
      type: 'mouse',
      x: e.clientX * dpr,
      y: e.clientY * dpr,
    });
  });

  window.addEventListener('mousedown', (e) => {
    emit({
      type: 'mouse_down',
      x: e.clientX * dpr,
      y: e.clientY * dpr,
      button: e.button === 2 ? 'Right' : 'Left',
    });
  });

  window.addEventListener('mouseup', (e) => {
    emit({
      type: 'mouse_up',
      x: e.clientX * dpr,
      y: e.clientY * dpr,
      button: e.button === 2 ? 'Right' : 'Left',
    });
  });

  window.addEventListener(
    'wheel',
    (e) => {
      e.preventDefault();
      emit({ type: 'wheel', x: e.clientX * dpr, y: e.clientY * dpr, delta: e.deltaY > 0 ? -120 : 120 });
    },
    { passive: false },
  );

  (window as unknown as { __devMock: unknown }).__devMock = {
    emit,
    mockGroups: buildMockGroups(isEn),
    mockPinned,
    setServerScenario: (scenario: 'good' | 'warning' | 'critical') => {
      emit(buildMockState(scenario, isEn));
    },
    setTodayScenario: (scenario: 'full' | 'soon' | 'now' | 'few' | 'empty') => {
      emit({ type: 'today', data: buildMockToday(scenario, isEn) });
    },
    setLayout: (style: string) => {
      emit({
        type: 'collage',
        style,
        items: mockCollageImages,
        palette: ['#4f46e5', '#0284c7', '#805ad5', '#ea580c', '#15803d'],
      });
    },
    togglePrivacyMode: () => {
      currentPrivacyMode = !currentPrivacyMode;
      emit({ type: 'privacy_mode', enabled: currentPrivacyMode });
    },
    openFirstFolder: () => {
      const tile = document.querySelector('.folder-tile-widget') as HTMLElement;
      tile?.click();
    },
    hoverItem: () => {
      const item = document.querySelector('.launcher-item') as HTMLElement;
      if (item) {
        item.classList.add('is-hovered');
      }
    },
    hoverDock: () => {
      const item = document.querySelector('.dock-item') as HTMLElement;
      if (item) {
        item.classList.add('is-hovered');
      }
    },
  };
}

export {};
