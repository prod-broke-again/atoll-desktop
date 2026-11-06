const ID_RE = /^[A-Za-z0-9_-]{11}$/;

export function parseYouTubeVideoId(input: string | undefined | null): string | null {
  const raw = (input ?? '').trim();
  if (!raw) return null;
  if (ID_RE.test(raw)) return raw;

  try {
    const url = new URL(raw.includes('://') ? raw : `https://${raw}`);
    const host = url.hostname.replace(/^www\./i, '').toLowerCase();
    const allowed = new Set([
      'youtu.be',
      'youtube.com',
      'm.youtube.com',
      'music.youtube.com',
      'youtube-nocookie.com',
    ]);
    if (!allowed.has(host)) return null;

    if (host === 'youtu.be') {
      const id = url.pathname.split('/').filter(Boolean)[0];
      return id && ID_RE.test(id) ? id : null;
    }

    const v = url.searchParams.get('v');
    if (v && ID_RE.test(v)) return v;

    const parts = url.pathname.split('/').filter(Boolean);
    for (let i = 0; i < parts.length - 1; i++) {
      if (parts[i] === 'embed' || parts[i] === 'shorts' || parts[i] === 'live' || parts[i] === 'v') {
        return ID_RE.test(parts[i + 1]) ? parts[i + 1] : null;
      }
    }
  } catch {
    return null;
  }
  return null;
}

export class YoutubeWallpaper {
  private iframe: HTMLIFrameElement | null = null;
  private videoId: string | null = null;
  private sound = false;
  private paused = false;
  private readyTimer = 0;
  private readonly host: HTMLElement;

  constructor(host: HTMLElement) {
    this.host = host;
  }

  get active(): boolean {
    return this.videoId !== null;
  }

  apply(url: string | undefined, soundEnabled: boolean): boolean {
    const id = parseYouTubeVideoId(url);
    this.sound = !!soundEnabled;
    if (!id) {
      this.destroy();
      return false;
    }
    if (id !== this.videoId) {
      this.videoId = id;
      this.mount();
    } else {
      this.syncPlayback();
    }
    return true;
  }

  setPaused(paused: boolean): void {
    this.paused = paused;
    this.syncPlayback();
  }

  destroy(): void {
    this.clearTimer();
    this.videoId = null;
    this.iframe?.remove();
    this.iframe = null;
    this.host.hidden = true;
    this.host.replaceChildren();
  }

  private mount(): void {
    this.clearTimer();
    this.host.replaceChildren();
    const iframe = document.createElement('iframe');
    iframe.setAttribute('allow', 'autoplay; encrypted-media; fullscreen');
    iframe.setAttribute('allowfullscreen', '');
    iframe.setAttribute('title', 'YouTube wallpaper');
    iframe.referrerPolicy = 'strict-origin-when-cross-origin';
    iframe.src = this.embedUrl();
    iframe.addEventListener('load', () => {
      this.readyTimer = window.setTimeout(() => this.syncPlayback(), 600);
    });
    this.iframe = iframe;
    this.host.appendChild(iframe);
    this.host.hidden = false;
  }

  private embedUrl(): string {
    const id = this.videoId!;
    const mute = this.sound ? '0' : '1';
    const origin = encodeURIComponent(location.origin);
    const params = [
      'autoplay=1',
      `mute=${mute}`,
      'controls=0',
      'modestbranding=1',
      'rel=0',
      'loop=1',
      `playlist=${id}`,
      'playsinline=1',
      'enablejsapi=1',
      'iv_load_policy=3',
      'disablekb=1',
      'fs=0',
      `origin=${origin}`,
    ].join('&');
    return `https://www.youtube.com/embed/${id}?${params}`;
  }

  private syncPlayback(): void {
    if (!this.iframe?.contentWindow) return;
    if (this.paused) {
      this.command('pauseVideo');
      this.command('mute');
      return;
    }
    this.command('playVideo');
    if (this.sound) {
      this.command('unMute');
      this.command('setVolume', [80]);
    } else {
      this.command('mute');
    }
  }

  private command(func: string, args: unknown[] = []): void {
    try {
      this.iframe?.contentWindow?.postMessage(JSON.stringify({ event: 'command', func, args }), '*');
    } catch {
      /* iframe not ready */
    }
  }

  private clearTimer(): void {
    if (this.readyTimer) {
      clearTimeout(this.readyTimer);
      this.readyTimer = 0;
    }
  }
}
