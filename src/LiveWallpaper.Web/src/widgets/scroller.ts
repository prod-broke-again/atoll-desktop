/**
 * Wheel-driven smooth scrolling for a container with `overflow: hidden`.
 *
 * The wallpaper gets no native wheel events (input comes from the host hook), so the native
 * scrollbar is useless anyway. This lerps scrollTop to a target, drives a thin indicator thumb
 * and toggles "can scroll up/down" classes used for the gradient fades.
 */
const STEP = 64; // px per wheel notch (delta 120)
const LERP = 0.22; // per 60 Hz frame
const HIDE_AFTER = 800; // ms

const reduced = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

export class SmoothScroller {
  private readonly el: HTMLElement;
  private readonly bar: HTMLElement;
  private readonly thumb: HTMLElement;
  private target = 0;
  private raf = 0;
  private last = 0;
  private hideTimer = 0;

  /**
   * @param el    the scrolling element (overflow hidden, fixed height)
   * @param frame the element that wraps `el` and hosts the indicator + fades (position: relative)
   */
  constructor(el: HTMLElement, frame: HTMLElement) {
    this.el = el;
    const bar = document.createElement('div');
    bar.className = 'scroll-bar';
    const thumb = document.createElement('i');
    bar.appendChild(thumb);
    frame.appendChild(bar);
    this.bar = bar;
    this.thumb = thumb;
    this.sync();
  }

  /**
   * Restore the last target after a relayout. Must not clamp the target to 0 when the element is
   * hidden or momentarily un-capped (`max-height: none` during measure) — that would lose the
   * position across the list ↔ detail swap.
   */
  sync(): void {
    if (this.el.clientHeight === 0) return;
    const max = this.maxScroll();
    if (max > 0) this.target = Math.min(Math.max(0, this.target), max);
    if (!this.raf) this.el.scrollTop = this.target;
    this.updateChrome();
  }

  reset(): void {
    this.stop();
    this.target = 0;
    this.el.scrollTop = 0;
    this.updateChrome();
  }

  /** @param delta host wheel delta (+120 = one notch up) */
  wheel(delta: number): void {
    const max = this.maxScroll();
    if (max <= 0) return;
    this.target = Math.min(max, Math.max(0, this.target + (-delta / 120) * STEP));
    this.show();
    if (reduced()) {
      this.el.scrollTop = this.target;
      this.updateChrome();
      return;
    }
    if (!this.raf) {
      this.last = performance.now();
      this.raf = requestAnimationFrame((t) => this.step(t));
    }
  }

  destroy(): void {
    this.stop();
    this.bar.remove();
  }

  private step(now: number): void {
    if (this.el.clientHeight === 0) {
      // Hidden mid-animation (view swapped): stop, keep the target; sync() restores it on show.
      this.raf = 0;
      return;
    }
    const dt = Math.min(4, Math.max(0.25, (now - this.last) / (1000 / 60)));
    this.last = now;
    const cur = this.el.scrollTop;
    const diff = this.target - cur;
    if (Math.abs(diff) < 0.5) {
      this.el.scrollTop = this.target;
      this.raf = 0;
      this.updateChrome();
      return;
    }
    this.el.scrollTop = cur + diff * (1 - Math.pow(1 - LERP, dt));
    this.updateChrome();
    this.raf = requestAnimationFrame((t) => this.step(t));
  }

  private stop(): void {
    if (this.raf) cancelAnimationFrame(this.raf);
    this.raf = 0;
  }

  private maxScroll(): number {
    return Math.max(0, this.el.scrollHeight - this.el.clientHeight);
  }

  private show(): void {
    this.bar.classList.add('is-active');
    window.clearTimeout(this.hideTimer);
    this.hideTimer = window.setTimeout(() => this.bar.classList.remove('is-active'), HIDE_AFTER);
  }

  private updateChrome(): void {
    const max = this.maxScroll();
    const top = this.el.scrollTop;
    const frame = this.bar.parentElement;
    if (frame) {
      frame.classList.toggle('can-up', top > 2);
      frame.classList.toggle('can-down', top < max - 2);
    }
    if (max <= 0) {
      this.bar.style.display = 'none';
      return;
    }
    this.bar.style.display = '';
    const track = this.bar.clientHeight || this.el.clientHeight;
    const ratio = this.el.clientHeight / this.el.scrollHeight;
    const h = Math.max(24, Math.round(track * ratio));
    const y = Math.round((top / max) * (track - h));
    this.thumb.style.height = `${h}px`;
    this.thumb.style.transform = `translateY(${y}px)`;
  }
}
