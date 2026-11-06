export interface IWidget {
  readonly id: string;
  mount(container: HTMLElement): void;
  update(data: unknown): void;
  pause(): void;
  resume(): void;
  destroy(): void;
}
