import type { ICollageLayout, Pic, Slot, Viewport } from './contract';
import { drawCover, sheen } from './shared';

/**
 * Honeycomb over the whole screen. Pictures take the cells closest to the centre,
 * remaining cells are faint glass hexagons that fade out towards the edges.
 * Slot.opacity === 0 marks a decorative (empty) cell.
 */
export class HexLayout implements ICollageLayout {
  readonly name = 'hex' as const;
  readonly hoverFocus = true;

  computeSlots(viewport: Viewport, count: number): Slot[] {
    const { width, height } = viewport;
    const R = Math.max(110, Math.min(160, Math.round(height / 7))); // circumradius, pointy-top
    const cw = Math.sqrt(3) * R; // cell width
    const rh = 1.5 * R; // row step
    const cols = Math.ceil(width / cw) + 2;
    const rows = Math.ceil(height / rh) + 2;
    const ox = (width - (cols - 1) * cw) / 2;
    const oy = (height - (rows - 1) * rh) / 2;
    const cx = width / 2;
    const cy = height / 2;

    const cells: { x: number; y: number; d: number }[] = [];
    for (let r = 0; r < rows; r++) {
      for (let c = 0; c < cols; c++) {
        const x = ox + c * cw + (r % 2 ? cw / 2 : 0);
        const y = oy + r * rh;
        // Elliptical distance so pictures spread wider than tall (screen aspect)
        const d = Math.hypot((x - cx) / (width / height), y - cy);
        cells.push({ x, y, d });
      }
    }
    cells.sort((a, b) => a.d - b.d);
    const maxD = cells[cells.length - 1].d || 1;

    // Pictures first (in slot order 0..count-1), then decorative cells
    const slots: Slot[] = [];
    const pics = Math.min(count, cells.length);
    cells.forEach((cell, i) => {
      const isPic = i < pics;
      slots.push({
        x: cell.x,
        y: cell.y,
        width: cw,
        height: 2 * R,
        rotationDeg: 0,
        zIndex: 1, // uniform depth: the honeycomb must move as one sheet under parallax
        opacity: isPic ? 1 : 0,
        scale: isPic ? 1 : Math.max(0, 1 - cell.d / maxD), // fade for empty cells
      });
    });
    return slots;
  }

  renderSlot(ctx: CanvasRenderingContext2D, slot: Slot, image: Pic | null, alpha: number): void {
    const R = slot.height / 2 - 4; // 4px gutter
    const isDecor = slot.opacity === 0;
    ctx.save();
    ctx.translate(slot.x, slot.y);
    ctx.beginPath();
    for (let k = 0; k < 6; k++) {
      const a = (Math.PI / 3) * k - Math.PI / 2;
      const px = Math.cos(a) * R;
      const py = Math.sin(a) * R;
      if (k === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    }
    ctx.closePath();

    if (isDecor) {
      const f = slot.scale; // 1 near centre → 0 at edges
      ctx.globalAlpha = alpha * (0.25 + 0.75 * f);
      ctx.fillStyle = `rgba(255,255,255,${0.025 + 0.03 * f})`;
      ctx.fill();
      ctx.strokeStyle = `rgba(255,255,255,${0.04 + 0.06 * f})`;
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.restore();
      return;
    }

    ctx.globalAlpha = alpha;
    ctx.shadowColor = 'rgba(0,0,0,0.45)';
    ctx.shadowBlur = 24;
    ctx.shadowOffsetY = 8;
    ctx.fillStyle = '#141824';
    ctx.fill();
    ctx.shadowColor = 'transparent';
    if (image && image.width > 0) {
      ctx.save();
      ctx.clip();
      const w = Math.sqrt(3) * R;
      drawCover(ctx, image, -w / 2, -R, w, 2 * R);
      sheen(ctx, -w / 2, -R, w, 2 * R);
      ctx.restore();
    }
    ctx.strokeStyle = 'rgba(255,255,255,0.16)';
    ctx.lineWidth = 1.2;
    ctx.stroke();
    ctx.restore();
  }
}
