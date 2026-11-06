import type { ICollageLayout, Pic, Slot, Viewport } from './contract';

export class FanLayout implements ICollageLayout {
  readonly name = 'fan' as const;
  readonly hoverFocus = true;

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    // Classic hand-of-cards fan: outer cards are drawn first, the centre card ends up on top.
    const n = Math.min(count, 9);
    const half = Math.floor(n / 2);
    const ks: number[] = [];
    for (let d = half; d >= 1; d--) ks.push(-d, d);
    ks.push(0);
    if (n % 2 === 0) ks.splice(ks.indexOf(-half), 1);

    const baseH = Math.min(height * 0.62, 680);
    const baseW = baseH * (440 / 700);
    // Keep the whole fan inside the middle ~62% of the screen so it never runs under the widgets.
    // Fan spans ~3/4 of the screen width; widgets get pushed aside by the overlap resolver.
    const spacingX = Math.min(baseW * 0.55, (width * 0.38 - baseW * 0.45) / Math.max(1, half));
    const cy = height * 0.53;

    const slots: Slot[] = [];
    for (let i = 0; i < ks.length; i++) {
      const k = ks[i];
      const scale = 1 - 0.06 * Math.abs(k);
      slots.push({
        x: width / 2 + k * spacingX,
        y: cy + k * k * 7,
        width: baseW * scale,
        height: baseH * scale,
        rotationDeg: k * 5,
        zIndex: half - Math.abs(k),
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
    const r = 24;

    // Drop shadow
    ctx.shadowColor = 'rgba(0, 0, 0, 0.55)';
    ctx.shadowBlur = 40;
    ctx.shadowOffsetY = 14;

    // Rounded card background (visible while the image loads / if it is missing)
    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    const bgGrad = ctx.createLinearGradient(0, -h / 2, 0, h / 2);
    bgGrad.addColorStop(0, 'rgba(40, 44, 62, 0.92)');
    bgGrad.addColorStop(1, 'rgba(16, 18, 28, 0.92)');
    ctx.fillStyle = bgGrad;
    ctx.fill();

    // Reset shadow for image clipping
    ctx.shadowColor = 'transparent';

    if (image && image.width > 0) {
      ctx.save();
      this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
      ctx.clip();
      this.drawCover(ctx, image, -w / 2, -h / 2, w, h);
      // Glassy top sheen + bottom shade for depth
      const sheen = ctx.createLinearGradient(0, -h / 2, 0, h / 2);
      sheen.addColorStop(0, 'rgba(255, 255, 255, 0.10)');
      sheen.addColorStop(0.35, 'rgba(255, 255, 255, 0)');
      sheen.addColorStop(0.75, 'rgba(0, 0, 0, 0)');
      sheen.addColorStop(1, 'rgba(0, 0, 0, 0.28)');
      ctx.fillStyle = sheen;
      ctx.fillRect(-w / 2, -h / 2, w, h);
      ctx.restore();
    }

    // Border highlight stroke
    this.roundRect(ctx, -w / 2, -h / 2, w, h, r);
    ctx.lineWidth = 1;
    ctx.strokeStyle = slot.rotationDeg === 0 ? 'rgba(255, 255, 255, 0.28)' : 'rgba(255, 255, 255, 0.12)';
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
