import type { ICollageLayout } from './contract';
import { FanLayout } from './fan';
import { BentoLayout } from './bento';
import { HexLayout } from './hex';
import { DiagonalLayout } from './diagonal';
import { PolaroidLayout } from './polaroid';
import { MasonryLayout } from './masonry';
import { FilmLayout } from './film';

export class LayoutRegistry {
  private static layouts: Map<string, ICollageLayout> = new Map<string, ICollageLayout>([
    ['fan', new FanLayout()],
    ['bento', new BentoLayout()],
    ['hex', new HexLayout()],
    ['diagonal', new DiagonalLayout()],
    ['polaroid', new PolaroidLayout()],
    ['masonry', new MasonryLayout()],
    ['film', new FilmLayout()],
  ]);

  public static get(name: string): ICollageLayout {
    return this.layouts.get(name.toLowerCase()) ?? this.layouts.get('fan')!;
  }
}
