/** Pre-decoded, pre-scaled picture (see main.ts → prepare). */
export type Pic = ImageBitmap;

export interface Viewport {
  width: number;
  height: number;
  devicePixelRatio: number;
}

export interface Slot {
  x: number;
  y: number;
  width: number;
  height: number;
  rotationDeg: number;
  zIndex: number;
  opacity: number;
  scale: number;
  clipPath?: Path2D;
}

export interface ICollageLayout {
  readonly name: string;
  /** Card under the cursor lifts, straightens and comes to the front. */
  readonly hoverFocus?: boolean;
  computeSlots(viewport: Viewport, count: number): Slot[];
  renderSlot(
    ctx: CanvasRenderingContext2D,
    slot: Slot,
    image: Pic | null,
    alpha: number
  ): void;
}
