import { parseYouTubeVideoId, YoutubeWallpaper } from './youtube-wallpaper';

export interface WallpaperSource {
  type: 'youtube' | 'video' | 'image';
  value: string;
  durationSeconds?: number;
}

export interface WallpaperPlaylistConfig {
  /** When false, skip custom sources and show Windows desktop wallpaper instead. */
  enabled?: boolean;
  sources?: WallpaperSource[];
  rotationSeconds?: number;
  shuffle?: boolean;
  soundEnabled?: boolean;
  youtubeUrl?: string;
  /** Host-mapped URL of the current Windows desktop wallpaper (SPI_GETDESKWALLPAPER). */
  systemWallpaperUrl?: string;
}

export class WallpaperPlaylist {
  private readonly youtube: YoutubeWallpaper;
  private readonly videoHost: HTMLElement;
  private readonly imageHost: HTMLElement;
  private localVideo: HTMLVideoElement | null = null;
  private localImage: HTMLImageElement | null = null;
  private order: WallpaperSource[] = [];
  private index = 0;
  private timer = 0;
  private sound = false;
  private paused = false;
  private shuffle = false;
  private rotationSeconds = 300;
  private active = false;

  constructor(youtube: YoutubeWallpaper, videoHost: HTMLElement, imageHost: HTMLElement) {
    this.youtube = youtube;
    this.videoHost = videoHost;
    this.imageHost = imageHost;
  }

  get isActive(): boolean {
    return this.active;
  }

  apply(cfg: WallpaperPlaylistConfig | undefined): boolean {
    this.clearTimer();
    const customOn = cfg?.enabled !== false;
    const sources = customOn ? this.normalizeSources(cfg) : [];
    if (sources.length === 0) {
      // No custom playlist — show the real Windows desktop wallpaper when available.
      const systemUrl = (cfg?.systemWallpaperUrl ?? '').trim();
      if (systemUrl) {
        this.sound = false;
        this.order = [];
        this.index = 0;
        this.active = true;
        this.showSystemImage(systemUrl);
        return true;
      }
      this.stopAll();
      this.active = false;
      return false;
    }

    this.sound = cfg?.soundEnabled === true;
    this.shuffle = cfg?.shuffle === true;
    this.rotationSeconds = Math.max(15, cfg?.rotationSeconds ?? 300);
    this.order = this.shuffle ? shuffleCopy(sources) : [...sources];
    this.index = 0;
    this.active = true;
    this.showCurrent();
    return true;
  }

  setPaused(paused: boolean): void {
    this.paused = paused;
    this.youtube.setPaused(paused);
    if (this.localVideo) {
      if (paused) this.localVideo.pause();
      else void this.localVideo.play().catch(() => undefined);
    }
  }

  destroy(): void {
    this.clearTimer();
    this.stopAll();
    this.active = false;
  }

  private normalizeSources(cfg: WallpaperPlaylistConfig | undefined): WallpaperSource[] {
    const list = (cfg?.sources ?? []).filter((s) => (s.value ?? '').trim().length > 0);
    if (list.length > 0) return list;
    const legacy = (cfg?.youtubeUrl ?? '').trim();
    if (legacy) return [{ type: 'youtube', value: legacy }];
    return [];
  }

  private showSystemImage(url: string): void {
    this.clearTimer();
    this.hideLocalMedia();
    this.youtube.destroy();
    this.videoHost.hidden = true;
    this.imageHost.hidden = false;
    this.imageHost.removeAttribute('hidden');
    const img = this.localImage ?? this.createImage();
    this.localImage = img;
    if (!urlsMatch(img.currentSrc || img.src, url)) {
      img.src = url;
    }
  }

  private showCurrent(): void {
    this.clearTimer();
    if (this.order.length === 0) {
      this.stopAll();
      this.active = false;
      return;
    }

    if (this.index >= this.order.length) {
      this.index = 0;
      if (this.shuffle) this.order = shuffleCopy(this.order);
    }

    const item = this.order[this.index]!;
    this.hideLocalMedia();
    this.youtube.destroy();

    const dwellMs = (item.durationSeconds && item.durationSeconds > 0 ? item.durationSeconds : this.rotationSeconds) * 1000;

    if (item.type === 'youtube') {
      this.imageHost.hidden = true;
      this.youtube.apply(item.value, this.sound);
      this.youtube.setPaused(this.paused);
      this.timer = window.setTimeout(() => this.advance(), dwellMs);
      return;
    }

    const kind = (item.type || '').toLowerCase();

    if (kind === 'video') {
      this.imageHost.hidden = true;
      this.videoHost.hidden = false;
      const video = this.localVideo ?? this.createVideo();
      this.localVideo = video;
      if (!urlsMatch(video.currentSrc || video.src, item.value)) {
        video.src = item.value;
        video.load();
      }
      video.muted = !this.sound;
      if (!this.paused) void video.play().catch(() => undefined);
      else video.pause();
      this.timer = window.setTimeout(() => this.advance(), dwellMs);
      return;
    }

    // image (default for non-youtube local sources)
    this.videoHost.hidden = true;
    this.imageHost.hidden = false;
    this.imageHost.removeAttribute('hidden');
    const img = this.localImage ?? this.createImage();
    this.localImage = img;
    if (!urlsMatch(img.currentSrc || img.src, item.value)) {
      img.src = item.value;
    }
    this.timer = window.setTimeout(() => this.advance(), dwellMs);
  }

  private advance(): void {
    this.index += 1;
    this.showCurrent();
  }

  private createVideo(): HTMLVideoElement {
    const video = document.createElement('video');
    video.playsInline = true;
    video.loop = true;
    video.autoplay = true;
    video.muted = !this.sound;
    video.setAttribute('aria-hidden', 'true');
    video.style.cssText =
      'position:absolute;top:50%;left:50%;min-width:100%;min-height:100%;width:auto;height:auto;transform:translate(-50%,-50%);object-fit:cover;pointer-events:none;';
    this.videoHost.replaceChildren(video);
    return video;
  }

  private createImage(): HTMLImageElement {
    const img = document.createElement('img');
    img.alt = '';
    img.decoding = 'async';
    img.style.cssText = 'width:100%;height:100%;object-fit:cover;pointer-events:none;';
    this.imageHost.replaceChildren(img);
    return img;
  }

  private hideLocalMedia(): void {
    this.localVideo?.pause();
    if (this.localVideo) this.localVideo.removeAttribute('src');
    this.localImage?.removeAttribute('src');
  }

  private stopAll(): void {
    this.hideLocalMedia();
    this.youtube.destroy();
    this.videoHost.hidden = true;
    this.imageHost.hidden = true;
  }

  private clearTimer(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = 0;
    }
  }
}

function shuffleCopy<T>(items: T[]): T[] {
  const a = [...items];
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j]!, a[i]!];
  }
  return a;
}

function urlsMatch(a: string, b: string): boolean {
  if (!a || !b) return false;
  if (a === b) return true;
  try {
    return decodeURIComponent(a) === decodeURIComponent(b);
  } catch {
    return false;
  }
}

export { parseYouTubeVideoId };
