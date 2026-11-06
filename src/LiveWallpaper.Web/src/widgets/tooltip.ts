/**
 * Animated name tip for launcher icons. Driven by synthetic hover from the mouse hook
 * (DOM pointerenter never fires on the wallpaper WebView).
 */
export class LauncherTooltip {
  private static el: HTMLElement | null = null;
  private static hideTimer: number | null = null;
  private static currentKey: string | null = null;

  private static ensure(): HTMLElement {
    if (this.el) return this.el;
    const tip = document.createElement('div');
    tip.className = 'launcher-tooltip';
    tip.setAttribute('aria-hidden', 'true');
    document.body.appendChild(tip);
    this.el = tip;
    return tip;
  }

  public static show(anchor: HTMLElement, label: string): void {
    const text = label.trim();
    if (!text) {
      this.hide(true);
      return;
    }

    const key = `${anchor}-${text}`;
    if (this.currentKey === key && this.el?.classList.contains('is-visible')) return;
    this.currentKey = key;

    if (this.hideTimer != null) {
      window.clearTimeout(this.hideTimer);
      this.hideTimer = null;
    }

    const tip = this.ensure();
    tip.textContent = text;

    const rect = anchor.getBoundingClientRect();
    tip.classList.remove('is-visible', 'is-below');
    // Force layout so the first paint has size.
    void tip.offsetWidth;

    const tipW = tip.offsetWidth;
    const tipH = tip.offsetHeight;
    let left = rect.left + rect.width / 2 - tipW / 2;
    left = Math.max(10, Math.min(left, window.innerWidth - tipW - 10));

    const spaceAbove = rect.top;
    const preferBelow = spaceAbove < tipH + 14;
    let top: number;
    if (preferBelow) {
      top = rect.bottom + 10;
      tip.classList.add('is-below');
    } else {
      top = rect.top - tipH - 10;
    }

    tip.style.left = `${Math.round(left)}px`;
    tip.style.top = `${Math.round(top)}px`;
    requestAnimationFrame(() => tip.classList.add('is-visible'));
  }

  public static hide(immediate = false): void {
    this.currentKey = null;
    if (!this.el) return;
    if (this.hideTimer != null) {
      window.clearTimeout(this.hideTimer);
      this.hideTimer = null;
    }
    if (immediate) {
      this.el.classList.remove('is-visible');
      return;
    }
    this.hideTimer = window.setTimeout(() => {
      this.el?.classList.remove('is-visible');
      this.hideTimer = null;
    }, 80);
  }

  public static fromHoverTarget(target: HTMLElement | null): void {
    if (!target) {
      this.hide();
      return;
    }

    // Dock / panel items: icon-only → show name on hover.
    if (target.classList.contains('dock-item') || target.classList.contains('launcher-item')) {
      const name =
        target.getAttribute('data-name') ||
        target.getAttribute('aria-label') ||
        target.querySelector('img')?.getAttribute('alt') ||
        '';
      this.show(target, name);
      return;
    }

    // Folder tiles already show a persistent title; still tip the full name if truncated.
    if (target.classList.contains('folder-tile')) {
      const widget = target.closest<HTMLElement>('.folder-tile-widget');
      const title = widget?.querySelector('.folder-tile-title')?.textContent ?? '';
      if (title) this.show(target, title);
      else this.hide();
      return;
    }

    this.hide();
  }
}
