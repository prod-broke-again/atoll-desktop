/**
 * Android-like shortcut drag between dock and folders.
 * Driven by the host mouse hook (DOM DnD never reaches the WorkerW WebView).
 */
export class IconDragManager {
  private static pending: { id: string; x: number; y: number; el: HTMLElement } | null = null;
  private static active: {
    id: string;
    sourceGroupId: string | null;
    ghost: HTMLElement;
  } | null = null;
  private static dropTarget: HTMLElement | null = null;

  public static isDragging(): boolean {
    return !!this.active;
  }

  /** Returns true when the press belongs to a launchable icon (defer click / start drag). */
  public static handleMouseDown(x: number, y: number, hit: Element | null): boolean {
    if (this.active) this.cancel();
    this.pending = null;

    const item =
      hit instanceof Element ? hit.closest<HTMLElement>('[data-action="launch"][data-id]') : null;
    if (!item) return false;

    const id = item.getAttribute('data-id');
    if (!id) return false;

    this.pending = { id, x, y, el: item };
    item.classList.add('is-pressed');
    return true;
  }

  public static handleMouseMove(x: number, y: number): void {
    if (this.active) {
      this.moveGhost(x, y);
      this.updateDropTarget(x, y);
      return;
    }

    if (!this.pending) return;
    if (Math.hypot(x - this.pending.x, y - this.pending.y) < 8) return;

    this.beginDrag(this.pending, x, y);
    this.pending = null;
  }

  /**
   * @returns true if the up was consumed (drag finished or deferred launch fired).
   */
  public static handleMouseUp(x: number, y: number): boolean {
    if (this.active) {
      const drop = this.resolveDrop(x, y);
      const { id, sourceGroupId } = this.active;
      this.cancel();

      if (drop && drop.groupId !== sourceGroupId) {
        window.chrome?.webview?.postMessage({
          type: 'move_item',
          id,
          targetGroupId: drop.groupId,
        });
      }
      return true;
    }

    if (this.pending) {
      const { el, x: px, y: py } = this.pending;
      el.classList.remove('is-pressed');
      this.pending = null;
      el.dispatchEvent(
        new MouseEvent('click', { bubbles: true, cancelable: true, clientX: px, clientY: py }),
      );
      return true;
    }

    return false;
  }

  public static cancel(): void {
    this.pending?.el.classList.remove('is-pressed');
    this.pending = null;
    this.clearDropHighlight();
    document.querySelectorAll('.is-icon-dragging').forEach((el) => el.classList.remove('is-icon-dragging'));
    this.active?.ghost.remove();
    this.active = null;
  }

  private static beginDrag(
    pending: { id: string; el: HTMLElement },
    x: number,
    y: number,
  ): void {
    pending.el.classList.remove('is-pressed');
    pending.el.classList.add('is-icon-dragging');

    const sourceGroup =
      pending.el.closest<HTMLElement>('[data-group-id]')?.getAttribute('data-group-id') ?? null;

    const img = pending.el.querySelector('img');
    const ghost = document.createElement('div');
    ghost.className = 'icon-drag-ghost';
    if (img) {
      const clone = img.cloneNode(true) as HTMLImageElement;
      clone.removeAttribute('loading');
      ghost.appendChild(clone);
    } else {
      ghost.textContent = pending.el.getAttribute('data-name')?.slice(0, 1) ?? '•';
    }
    (document.getElementById('widgetsOverlay') ?? document.body).appendChild(ghost);

    this.active = { id: pending.id, sourceGroupId: sourceGroup, ghost };
    this.moveGhost(x, y);
    this.updateDropTarget(x, y);
  }

  private static moveGhost(x: number, y: number): void {
    if (!this.active) return;
    this.active.ghost.style.left = `${Math.round(x)}px`;
    this.active.ghost.style.top = `${Math.round(y)}px`;
  }

  private static updateDropTarget(x: number, y: number): void {
    const next = this.hitDropZone(x, y);
    if (next === this.dropTarget) return;
    this.clearDropHighlight();
    this.dropTarget = next;
    next?.classList.add('is-drop-target');
  }

  private static clearDropHighlight(): void {
    this.dropTarget?.classList.remove('is-drop-target');
    this.dropTarget = null;
    document.querySelectorAll('.is-drop-target').forEach((el) => el.classList.remove('is-drop-target'));
  }

  private static hitDropZone(x: number, y: number): HTMLElement | null {
    // Temporarily hide ghost so elementFromPoint sees what's under it.
    const ghost = this.active?.ghost;
    if (ghost) ghost.style.visibility = 'hidden';
    const hit = document.elementFromPoint(x, y);
    if (ghost) ghost.style.visibility = '';

    if (!(hit instanceof Element)) return null;

    const panel = hit.closest<HTMLElement>('.folder-panel[data-group-id]');
    if (panel) return panel;

    const folder = hit.closest<HTMLElement>('.folder-tile-widget[data-group-id]');
    if (folder) return folder;

    const dock = hit.closest<HTMLElement>('.dock-widget');
    if (dock) return dock;

    return null;
  }

  private static resolveDrop(
    x: number,
    y: number,
  ): { groupId: string | null } | null {
    const zone = this.hitDropZone(x, y);
    if (!zone) return null;

    if (zone.classList.contains('dock-widget')) {
      return { groupId: null }; // desktop root → dock
    }

    const groupId = zone.getAttribute('data-group-id');
    if (!groupId) return null;
    return { groupId };
  }
}
