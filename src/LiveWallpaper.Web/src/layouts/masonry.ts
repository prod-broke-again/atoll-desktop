import type { ICollageLayout, Pic, Slot, Viewport } from './contract';

export class MasonryLayout implements ICollageLayout {
  readonly name = 'masonry';

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const slots: Slot[] = [];

    const cols = width >= 1600 ? 6 : 5;
    const gap = 18;
    const padX = 80;
    const padY = 50;

    const colW = (width - padX * 2 - (cols - 1) * gap) / cols;
    // Staggered column starts (some begin above the screen) for a natural Pinterest look
    const colHeights = Array.from({ length: cols }, (_, c) => padY - [0, 70, 25, 95, 40, 60][c % 6]);

    const actual = count;
    const aspectRatios = [1.3, 0.9, 1.5, 1.1, 1.4, 1.0, 1.25, 0.85, 1.35, 1.15, 1.45, 0.95];

    for (let i = 0; i < actual; i++) {
      // Find shortest column
      let shortestCol = 0;
      for (let c = 1; c < cols; c++) {
        if (colHeights[c] < colHeights[shortestCol]) {
          shortestCol = c;
        }
      }

      const ratio = aspectRatios[i % aspectRatios.length];
      const h = colW * ratio;
      const x = padX + shortestCol * (colW + gap) + colW / 2;
      const y = colHeights[shortestCol] + h / 2;

      // Only add if fits within screen bounds plus margin
      if (y - h / 2 < height) {
        slots.push({
          x,
          y,
          width: colW,
          height: h,
          rotationDeg: 0,
          zIndex: 1, // uniform parallax depth keeps the grid aligned
          opacity: 1,
          scale: 1,
        });

        colHeights[shortestCol] += h + gap;
      }
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
    const r = 18;

    // Soft ambient shadow
    ctx.shadowColor = 'rgba(0, 0, 0, 0.4)';
    ctx.shadowBlur = 24;
    ctx.shadowOffsetY = 10;

    // Card path
    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.fillStyle = '#161b22';
    ctx.fill();

    ctx.shadowColor = 'transparent';

    ctx.save();
    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.clip();

    if (image && image.width > 0) {
      this.drawCover(ctx, image, -w / 2, -h / 2, w, h);
    }
    ctx.restore();

    // Specular border
    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.18)';
    ctx.lineWidth = 1.5;
    ctx.stroke();

    ctx.restore();
  }

  private roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  private drawCover(ctx: CanvasRenderingContext2D, img: Pic, x: number, y: number, w: number, h: number) {
    const imgRatio = img.width / img.height;
    const slotRatio = w / h;
    let sx = 0, sy = 0, sw = img.width, sh = img.height;

    if (imgRatio > slotRatio) {
      sw = img.height * slotRatio;
      sx = (img.width - sw) / 2;
    } else {
      sh = img.width / slotRatio;
      sy = (img.height - sh) / 2;
    }

    ctx.drawImage(img, sx, sy, sw, sh, x, y, w, h);
  }
}
