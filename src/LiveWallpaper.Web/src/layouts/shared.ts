import type { Pic } from './contract';

/** Draw a picture "object-fit: cover" into the given box (current transform applies). */
export function drawCover(ctx: CanvasRenderingContext2D, img: Pic, x: number, y: number, w: number, h: number): void {
  const ir = img.width / img.height;
  const tr = w / h;
  let sx = 0, sy = 0, sw = img.width, sh = img.height;
  if (ir > tr) {
    sw = img.height * tr;
    sx = (img.width - sw) / 2;
  } else {
    sh = img.width / tr;
    sy = (img.height - sh) / 2;
  }
  ctx.drawImage(img, sx, sy, sw, sh, x, y, w, h);
}

/** Subtle top sheen + bottom shade used by all card-like layouts. */
export function sheen(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number): void {
  const g = ctx.createLinearGradient(0, y, 0, y + h);
  g.addColorStop(0, 'rgba(255,255,255,0.08)');
  g.addColorStop(0.3, 'rgba(255,255,255,0)');
  g.addColorStop(0.75, 'rgba(0,0,0,0)');
  g.addColorStop(1, 'rgba(0,0,0,0.25)');
  ctx.fillStyle = g;
  ctx.fillRect(x, y, w, h);
}
