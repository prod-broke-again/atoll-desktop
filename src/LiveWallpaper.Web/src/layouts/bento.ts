import type { ICollageLayout, Pic, Slot, Viewport } from './contract';

export class BentoLayout implements ICollageLayout {
  readonly name = 'bento' as const;

  private readonly layoutGrid = [
    // [col, row, colSpan, rowSpan] — 9 tiles that cover the 8×4 grid completely.
    [0, 0, 3, 2],
    [3, 0, 2, 2],
    [5, 0, 3, 1],
    [5, 1, 3, 1],
    [0, 2, 2, 2],
    [2, 2, 2, 1],
    [2, 3, 2, 1],
    [4, 2, 2, 2],
    [6, 2, 2, 2],
  ];

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const m = 40;
    const g = 16;
    const cols = 8;
    const rows = 4;

    const uw = (width - 2 * m - (cols - 1) * g) / cols;
    const uh = (height - 2 * m - (rows - 1) * g) / rows;

    const actualCount = Math.min(count, this.layoutGrid.length);
    const slots: Slot[] = [];

    for (let i = 0; i < actualCount; i++) {
      const [col, row, sw, sh] = this.layoutGrid[i];
      const x = m + col * (uw + g);
      const y = m + row * (uh + g);
      const w = sw * uw + (sw - 1) * g;
      const h = sh * uh + (sh - 1) * g;

      slots.push({
        x: x + w / 2,
        y: y + h / 2,
        width: w,
        height: h,
        rotationDeg: 0,
        zIndex: 1, // uniform parallax depth keeps the grid aligned
        opacity: 1,
        scale: 1,
      });
    }

    return slots;
  }

  renderSlot(
    ctx: CanvasRenderingContext2D,
    slot: Slot,
    image: Pic | null,
    alpha: number
  ): void {
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.translate(slot.x, slot.y);

    const w = slot.width;
    const h = slot.height;
    const r = 20;

    ctx.shadowColor = 'rgba(0, 0, 0, 0.4)';
    ctx.shadowBlur = 24;
    ctx.shadowOffsetY = 8;

    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.fillStyle = '#141824';
    ctx.fill();

    ctx.shadowColor = 'transparent';

    if (image && image.width > 0) {
      ctx.save();
      this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
      ctx.clip();
      this.drawCover(ctx, image, -w / 2, -h / 2, w, h);
      ctx.restore();
    }

    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.lineWidth = 1.5;
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.14)';
    ctx.stroke();

    ctx.restore();
  }

  private roundRect(
    ctx: CanvasRenderingContext2D,
    x: number,
    y: number,
    w: number,
    h: number,
    r: number
  ): void {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  private drawCover(
    ctx: CanvasRenderingContext2D,
    img: Pic,
    x: number,
    y: number,
    w: number,
    h: number
  ): void {
    const imgRatio = img.width / img.height;
    const targetRatio = w / h;
    let sx = 0, sy = 0, sw = img.width, sh = img.height;

    if (imgRatio > targetRatio) {
      sw = img.height * targetRatio;
      sx = (img.width - sw) / 2;
    } else {
      sh = img.width / targetRatio;
      sy = (img.height - sh) / 2;
    }

    ctx.drawImage(img, sx, sy, sw, sh, x, y, w, h);
  }
}
