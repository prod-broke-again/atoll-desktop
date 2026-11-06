import type { ICollageLayout, Pic, Slot, Viewport } from './contract';

export class FilmLayout implements ICollageLayout {
  readonly name = 'film';

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const slots: Slot[] = [];

    // Two parallel slanted cinema film strips
    const frameW = 280;
    const frameH = 200;
    const gap = 36;
    const tiltDeg = -10;

    const stripY1 = height * 0.32;
    const stripY2 = height * 0.68;

    const actual = Math.min(count, 12);
    const half = Math.ceil(actual / 2);

    const totalW = half * (frameW + gap);
    const startX = (width - totalW) / 2 + frameW / 2;

    for (let i = 0; i < actual; i++) {
      const isTop = i < half;
      const indexInStrip = isTop ? i : i - half;
      const x = startX + indexInStrip * (frameW + gap);
      const y = isTop ? stripY1 : stripY2;

      slots.push({
        x,
        y,
        width: frameW,
        height: frameH,
        rotationDeg: tiltDeg,
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
    ctx.rotate((slot.rotationDeg * Math.PI) / 180);

    const w = slot.width;
    const h = slot.height;
    const stripPaddingY = 24;

    // Outer dark film strip backing
    ctx.fillStyle = '#0a0c10';
    ctx.fillRect(-w / 2 - 10, -h / 2 - stripPaddingY, w + 20, h + stripPaddingY * 2);

    // Film sprocket perforation holes (top and bottom)
    ctx.fillStyle = '#21262d';
    const holeW = 12;
    const holeH = 8;
    const numHoles = 8;
    const holeStep = (w + 20) / numHoles;

    for (let j = 0; j < numHoles; j++) {
      const hx = -w / 2 - 10 + j * holeStep + (holeStep - holeW) / 2;
      // Top sprocket
      this.roundRect(ctx, hx, -h / 2 - stripPaddingY + 8, holeW, holeH, 2);
      ctx.fill();
      // Bottom sprocket
      this.roundRect(ctx, hx, h / 2 + stripPaddingY - 16, holeW, holeH, 2);
      ctx.fill();
    }

    // Film Frame photo
    ctx.save();
    ctx.beginPath();
    ctx.rect(-w / 2, -h / 2, w, h);
    ctx.clip();

    if (image && image.width > 0) {
      this.drawCover(ctx, image, -w / 2, -h / 2, w, h);
    } else {
      ctx.fillStyle = '#161b22';
      ctx.fillRect(-w / 2, -h / 2, w, h);
    }
    ctx.restore();

    // Frame border
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.2)';
    ctx.lineWidth = 1.5;
    ctx.strokeRect(-w / 2, -h / 2, w, h);

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
