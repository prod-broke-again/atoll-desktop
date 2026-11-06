import type { ICollageLayout, Pic, Slot, Viewport } from './contract';
import { drawCover, sheen } from './shared';

const SKEW_DEG = 14;

/** Full-height slanted ribbons (parallelograms) across the screen. */
export class DiagonalLayout implements ICollageLayout {
  readonly name = 'diagonal' as const;

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const n = Math.max(3, Math.min(count, width >= 1600 ? 7 : 5));
    const gap = 14;
    const skew = Math.tan((SKEW_DEG * Math.PI) / 180) * height; // horizontal shift top→bottom
    const total = width + skew; // ribbons extend past both edges
    const w = (total - gap * (n - 1)) / n;
    const slots: Slot[] = [];
    for (let i = 0; i < n; i++) {
      slots.push({
        x: -skew / 2 + w / 2 + i * (w + gap),
        y: height / 2,
        width: w,
        height: height + 4,
        rotationDeg: 0,
        zIndex: 1, // uniform parallax depth keeps the grid aligned
        opacity: 1,
        scale: skew, // carries the skew offset to renderSlot
      });
    }
    return slots;
  }

  renderSlot(ctx: CanvasRenderingContext2D, slot: Slot, image: Pic | null, alpha: number): void {
    const { width: w, height: h } = slot;
    const s = slot.scale / 2;
    ctx.save();
    ctx.globalAlpha = alpha;
    ctx.translate(slot.x, slot.y);
    ctx.beginPath();
    ctx.moveTo(-w / 2 + s, -h / 2);
    ctx.lineTo(w / 2 + s, -h / 2);
    ctx.lineTo(w / 2 - s, h / 2);
    ctx.lineTo(-w / 2 - s, h / 2);
    ctx.closePath();
    ctx.fillStyle = '#141824';
    ctx.fill();
    if (image && image.width > 0) {
      ctx.save();
      ctx.clip();
      const bw = w + 2 * s;
      drawCover(ctx, image, -bw / 2, -h / 2, bw, h);
      sheen(ctx, -bw / 2, -h / 2, bw, h);
      ctx.restore();
    }
    ctx.strokeStyle = 'rgba(255,255,255,0.12)';
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.restore();
  }
}
