export interface MouseMessage {
  type: 'mouse';
  x: number;
  y: number;
  /** False when the pointer is over a regular window (not the desktop): no widget hover. */
  overDesktop?: boolean;
}

export interface MouseDownMessage {
  type: 'mouse_down';
  x: number;
  y: number;
  button: string;
}

export interface WheelMessage {
  type: 'wheel';
  x: number;
  y: number;
  delta: number;
}

export interface MouseUpMessage {
  type: 'mouse_up';
  x: number;
  y: number;
  button: string;
}

export interface ConfigMessage {
  type: 'config';
  config: {
    collage: {
      intervalMinutes: number;
      style: string;
      shuffleStyle?: boolean;
      enableColorHarmony: boolean;
    };
    weather: {
      enabled: boolean;
      city: string;
      units: string;
    };
    performance: {
      fpsIdle: number;
      fpsTransition: number;
      autoPauseFullscreen: boolean;
      autoPauseBattery: boolean;
    };
    widgets: {
      showClock: boolean;
      showDate: boolean;
      showWeather: boolean;
      showSystemMetrics: boolean;
      clockPosition: string;
      clockFont: string;
      systemPosition: string;
      weatherPosition: string;
    };
    server?: {
      enabled?: boolean;
    };
    wallpaper?: {
      enabled?: boolean;
      youtubeUrl?: string;
      soundEnabled?: boolean;
      sources?: { type: string; value: string; durationSeconds?: number }[];
      rotationSeconds?: number;
      shuffle?: boolean;
      systemWallpaperUrl?: string;
    };
  };
}

export interface TelemetryMessage {
  type: 'telemetry';
  cpuPercent: number;
  ramPercent: number;
  ramUsedGb: number;
  ramTotalGb: number;
  gpuPercent: number;
  gpuTempCelsius?: number;
  netDownloadKbps: number;
  netUploadKbps: number;
}

export interface WeatherMessage {
  type: 'weather';
  data: {
    enabled: boolean;
    city: string;
    temperature: number;
    apparentTemperature: number;
    weatherCode: number;
    weatherDescription: string;
    icon: string;
    windSpeed: number;
    relativeHumidity: number;
    isDay: boolean;
    units: string;
  };
}

export interface CollageImageItem {
  id: string;
  url: string; // e.g., https://pins.livewallpaper/filename.jpg
  width: number;
  height: number;
  dominantColor: string;
}

export interface CollageMessage {
  type: 'collage';
  style: string;
  items: CollageImageItem[];
  palette: string[];
}

export interface PauseMessage {
  type: 'pause_state';
  isPaused: boolean;
  reason?: 'fullscreen' | 'user' | 'battery' | 'lock';
}

export interface MediaMessage {
  type: 'media';
  data: {
    hasMedia: boolean;
    title: string;
    artist: string;
    album: string;
    isPlaying: boolean;
    thumbnailBase64?: string | null;
    positionSeconds?: number;
    durationSeconds?: number;
    canSeek?: boolean;
    isYandexMusic?: boolean;
    isLiked?: boolean;
    trackId?: string | null;
  };
}

export interface LauncherItem {
  id: string;
  name: string;
  iconUrl: string;
}

export interface LauncherGroup {
  id: string;
  name: string;
  items: LauncherItem[];
}

export interface LauncherMessage {
  type: 'launcher';
  pinned: LauncherItem[];
  groups: LauncherGroup[];
}

export interface MetricPoint {
  t: number;
  cpu: number;
  mem: number;
  swap: number;
  load1: number;
  diskPct: number;
  rxKbps: number;
  txKbps: number;
}

export interface ServerSiteInfo {
  name: string;
  url: string;
  state: 'up' | 'slow' | 'down' | 'unknown';
  ms: number | null;
  tlsDays: number | null;
}

export interface ServerStateMessage {
  type: 'serverState';
  host: string;
  ssh: 'connecting' | 'live' | 'stale' | 'offline';
  server: { status: string; uptimeSec: number; diskPct: number } | null;
  sites: ServerSiteInfo[];
  account: { balance: number; currency: string; daysLeft: number | null; autopay: boolean; staleMin: number } | null;
  backup: { ageHours: number | null } | null;
  bucket: { usedPct: number; objects: number } | null;
  alerts: { level: 'warning' | 'critical'; text: string }[];
  history?: MetricPoint[];
  privacyMode?: boolean;
}

export interface ServerMetricsMessage {
  type: 'serverMetrics';
  point: MetricPoint;
}

export interface PrivacyModeMessage {
  type: 'privacy_mode';
  enabled: boolean;
}

export interface ContextWidgetsConfig {
  showClock: boolean;
  showWeather: boolean;
  showSystem: boolean;
  showServer: boolean;
  showLauncher: boolean;
  showMedia: boolean;
  showToday: boolean;
  showTelegram: boolean;
  palettePreset: string;
}

export interface TodayEvent {
  id: string;
  title: string;
  startLocal: string;
  endLocal?: string | null;
  allDay: boolean;
  timeLabel: string;
  /** false for "upcoming" events shown when today has few events */
  isToday?: boolean;
}

export interface TodayTask {
  id: string;
  title: string;
  /** WEEEK: 0 Low, 1 Medium, 2 High, 3 Hold */
  priority?: number | null;
  isCompleted: boolean;
  dueLabel?: string | null;
  board?: string | null;
  column?: string | null;
}

export interface TodayMessage {
  type: 'today';
  data: {
    enabled: boolean;
    dateLabel: string;
    events: TodayEvent[];
    tasks: TodayTask[];
    calendarError?: string | null;
    tasksError?: string | null;
    hasWeeekToken: boolean;
    hasCalendar: boolean;
  };
}

export interface TelegramChat {
  id: number;
  name: string;
  /** Telegram's own title, when it differs from the label we show. */
  title?: string | null;
  unread: number;
  username?: string | null;
  chatType: string;
}

export interface TelegramMessage {
  type: 'telegram';
  data: {
    enabled: boolean;
    available: boolean;
    chats: TelegramChat[];
  };
}

export interface ContextModeMessage {
  type: 'context_mode';
  selectedMode: 'auto' | 'work' | 'game' | 'night';
  effectiveMode: 'work' | 'game' | 'night';
  reason: string;
  widgets: ContextWidgetsConfig;
}

export interface AtmosphereSignalsState {
  prodAlert: boolean;
  prodAlertText?: string | null;
  upcomingCall: boolean;
  upcomingCallMinutes?: number | null;
  upcomingCallTitle?: string | null;
  lowBalance: boolean;
  lowBalanceText?: string | null;
}

export interface AtmosphereSignalsMessage {
  type: 'atmosphere_signals';
  signals: AtmosphereSignalsState;
}

export interface PromptNameResultMessage {
  type: 'prompt_name_result';
  requestId: string;
  value: string | null;
}

export interface ConfirmResultMessage {
  type: 'confirm_result';
  requestId: string;
  confirmed: boolean;
}

export type HostToWebMessage =
  | MouseMessage
  | MouseDownMessage
  | MouseUpMessage
  | WheelMessage
  | ConfigMessage
  | TelemetryMessage
  | WeatherMessage
  | MediaMessage
  | CollageMessage
  | PauseMessage
  | LauncherMessage
  | ServerStateMessage
  | ServerMetricsMessage
  | PrivacyModeMessage
  | ContextModeMessage
  | AtmosphereSignalsMessage
  | TodayMessage
  | TelegramMessage
  | PromptNameResultMessage
  | ConfirmResultMessage;

export interface LaunchMessage {
  type: 'launch';
  id: string;
}

export interface OpenGroupMessage {
  type: 'open_group';
  id: string;
}

export interface ServerOpenMessage {
  type: 'server_open';
  target: 'ssh' | 'timeweb' | 'site';
  name?: string;
}

/** Opaque WEEEK task id from the latest snapshot; the host builds the URL. */
export interface OpenTaskMessage {
  type: 'open_task';
  id: string;
}

/** Opaque chat id from the latest snapshot; the host builds t.me or tg://. */
export interface OpenTelegramChatMessage {
  type: 'open_telegram_chat';
  id: number;
}

export interface SetModeMessage {
  type: 'set_mode';
  mode: 'auto' | 'work' | 'game' | 'night';
}

export interface TestSignalMessage {
  type: 'test_signal';
  signal: 'prod' | 'call' | 'balance';
  active: boolean;
}

export type LauncherActionKind =
  | 'open'
  | 'runas'
  | 'properties'
  | 'open_location'
  | 'copy_path'
  | 'cut'
  | 'copy'
  | 'delete'
  | 'rename'
  | 'pin_start'
  | 'more';

export interface LauncherActionMessage {
  type: 'launcher_action';
  action: LauncherActionKind;
  id: string;
  name?: string;
  kind?: 'item' | 'group';
  /** Physical pixels relative to wallpaper monitor origin (CSS × devicePixelRatio). */
  x?: number;
  y?: number;
}

export interface CreateFolderMessage {
  type: 'create_folder';
  name: string;
}

export interface RenameFolderMessage {
  type: 'rename_folder';
  id: string;
  name: string;
}

export interface MoveItemMessage {
  type: 'move_item';
  id: string;
  targetGroupId?: string | null;
}

export interface RenameItemMessage {
  type: 'rename_item';
  id: string;
  name: string;
}

export interface DeleteItemMessage {
  type: 'delete_item';
  id: string;
}

export type WebToHostMessage =
  | { type: 'ready' }
  | { type: 'request_next_collage' }
  | { type: 'log'; payload?: unknown }
  | LaunchMessage
  | OpenGroupMessage
  | CreateFolderMessage
  | RenameFolderMessage
  | MoveItemMessage
  | RenameItemMessage
  | DeleteItemMessage
  | LauncherActionMessage
  | ServerOpenMessage
  | OpenTaskMessage
  | OpenTelegramChatMessage
  | SetModeMessage
  | TestSignalMessage;
