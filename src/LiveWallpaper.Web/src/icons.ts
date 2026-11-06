import type { IconNode } from 'lucide';
import {
  Image as ImageIcon,
  LayoutGrid,
  PanelsTopLeft,
  Sparkles,
  Settings2,
  Puzzle,
  MonitorPlay,
  FolderOpen,
  CloudSun,
  Keyboard,
  Gauge,
  Plus,
  Trash2,
  ChevronUp,
  ChevronDown,
  Scissors,
  Copy,
  Pencil,
  ExternalLink,
  Shield,
  Folder,
  Link2,
  Info,
  Pin,
  Ellipsis,
  FolderPlus,
} from 'lucide';

function attrsToString(attrs: Record<string, string | number | undefined>): string {
  return Object.entries(attrs)
    .filter(([, v]) => v != null)
    .map(([k, v]) => `${k}="${String(v).replace(/"/g, '&quot;')}"`)
    .join(' ');
}

/** Renders a Lucide IconNode as an inline SVG string for template HTML. */
export function icon(node: IconNode, size = 18, className?: string): string {
  const body = node
    .map(([tag, attrs]) => `<${tag} ${attrsToString(attrs)} />`)
    .join('');
  const cls = className ? ` class="${className}"` : '';
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"${cls} aria-hidden="true">${body}</svg>`;
}

export const Icons = {
  wallpaper: ImageIcon,
  collage: LayoutGrid,
  widgets: PanelsTopLeft,
  atmosphere: Sparkles,
  system: Settings2,
  plugin: Puzzle,
  monitor: MonitorPlay,
  folderOpen: FolderOpen,
  weather: CloudSun,
  keyboard: Keyboard,
  gauge: Gauge,
  plus: Plus,
  trash: Trash2,
  chevronUp: ChevronUp,
  chevronDown: ChevronDown,
  cut: Scissors,
  copy: Copy,
  rename: Pencil,
  open: ExternalLink,
  runas: Shield,
  folder: Folder,
  path: Link2,
  props: Info,
  pin: Pin,
  more: Ellipsis,
  folderPlus: FolderPlus,
} as const;

export type IconName = keyof typeof Icons;

export function namedIcon(name: IconName, size = 18, className?: string): string {
  return icon(Icons[name], size, className);
}
