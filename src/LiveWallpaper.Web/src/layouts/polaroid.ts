import type { ICollageLayout, Pic, Slot, Viewport } from './contract';

export class PolaroidLayout implements ICollageLayout {
  readonly name = 'polaroid';
  readonly hoverFocus = true;

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const slots: Slot[] = [];
    const actual = Math.min(count, 8);

    const cardW = Math.min(width / 4.8, 320);
    const cardH = cardW * 1.25;

    // Arrange in a pleasant scattered ribbon across center screen
    const startX = width * 0.18;
    const endX = width * 0.82;
    const stepX = (endX - startX) / Math.max(1, actual - 1);

    const angles = [-6, 4, -4, 5, -3, 6, -5, 3];

    for (let i = 0; i < actual; i++) {
      const x = startX + i * stepX;
      const wave = Math.sin((i / (actual - 1)) * Math.PI) * 40;
      const y = height / 2 - 20 + wave;

      slots.push({
        x,
        y,
        width: cardW,
        height: cardH,
        rotationDeg: angles[i % angles.length],
        zIndex: i,
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
    const pad = 12;
    const bottomPad = 48; // Polaroid bottom margin

    // Drop shadow
    ctx.shadowColor = 'rgba(0, 0, 0, 0.45)';
    ctx.shadowBlur = 28;
    ctx.shadowOffsetY = 14;

    // White polaroid photo paper
    ctx.fillStyle = '#f8f9fa';
    this.roundRect(ctx, -w / 2, -h / 2, w, h, 8);
    ctx.fill();

    // Reset shadow for image
    ctx.shadowColor = 'transparent';

    // Photo area
    const imgW = w - pad * 2;
    const imgH = h - pad - bottomPad;
    const imgX = -w / 2 + pad;
    const imgY = -h / 2 + pad;

    ctx.save();
    ctx.beginPath();
    ctx.rect(imgX, imgY, imgW, imgH);
    ctx.clip();

    if (image && image.width > 0) {
      this.drawCover(ctx, image, imgX, imgY, imgW, imgH);
    } else {
      ctx.fillStyle = '#1c2128';
      ctx.fillRect(imgX, imgY, imgW, imgH);
    }
    ctx.restore();

    // Subtle inner stroke around photo
    ctx.strokeStyle = 'rgba(0, 0, 0, 0.12)';
    ctx.lineWidth = 1;
    ctx.strokeRect(imgX, imgY, imgW, imgH);

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
